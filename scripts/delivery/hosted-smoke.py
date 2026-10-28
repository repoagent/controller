#!/usr/bin/env python3
"""Hosted-only orchestration of the real installer smoke, with no credentials in artifacts.

The reserved repository, approval coordinate and Discussion marker belong only to this globally
serialized smoke. A revoke acknowledgement closes enrollment; it never proves token settlement.
"""
import hashlib
import json
import os
from pathlib import Path
import re
import runpy
import shutil
import subprocess
import sys
import tarfile
import threading
import time
import urllib.request

ROOT = Path(__file__).resolve().parents[2]
REPOSITORY = 'repoagent/ControllerTestRepo'
BROKER = 'https://repoagent-auth.livelydune-309701b4.swedencentral.azurecontainerapps.io'
APPROVAL = 'SMOKE_CONTROLLER_APPROVAL'
MARKER = '<!-- repoagent.hosted-smoke/v1 -->'
TITLE = re.compile(r'\A\[RepoAgent hosted smoke [0-9a-f]{12}\] mention\Z')


class Refused(Exception):
    pass


def command(arguments, *, data=None, timeout=120, check=True, environment=None):
    result = subprocess.run([str(arg) for arg in arguments], input=data, capture_output=True, text=True,
                            timeout=timeout, cwd=ROOT, env=environment)
    if check and result.returncode:
        raise Refused(f'{Path(str(arguments[0])).name} failed; inspect the retained private run state before retrying')
    return result


def gh(endpoint, *, method='GET', body=None):
    args = ['gh', 'api', '--hostname', 'github.com', '--method', method, endpoint]
    if body is not None:
        args += ['--input', '-']
    output = command(args, data=json.dumps(body) if body is not None else None).stdout
    if len(output) > 8 * 1024 * 1024:
        raise Refused('GitHub response exceeded the smoke observation bound')
    return json.loads(output) if output.strip() else None


def graphql(query, variables=None):
    value = gh('graphql', method='POST', body={'query': query, 'variables': variables or {}})
    if value.get('errors'):
        raise Refused('GitHub refused a smoke observation; no provider diagnostics are published')
    return value['data']


def private_write(path, value):
    with path.open('w', encoding='utf-8') as output:
        os.chmod(path, 0o600)
        json.dump(value, output, indent=2)
        output.write('\n')


def directory(path):
    path = Path(path)
    if not path.is_absolute() or path.is_symlink() or ROOT == path.resolve() or ROOT in path.resolve().parents:
        raise Refused('Use an absolute private smoke directory outside the source checkout')
    path.mkdir(mode=0o700, parents=True, exist_ok=True)
    if path.stat().st_mode & 0o077:
        raise Refused('Smoke state must be private (0700); do not reuse a public directory')
    return path


def identity():
    if not os.environ.get('GH_TOKEN'):
        raise Refused('REPOAGENT_E2E_GH_TOKEN is required for the dedicated repository')
    if gh('user').get('login') != 'repoagent':
        raise Refused('The smoke requires the configured repoagent participant authority')
    repository = gh('repos/' + REPOSITORY)
    if repository.get('full_name') != REPOSITORY or repository.get('has_discussions') is not True:
        raise Refused('The dedicated smoke repository or Discussions capability could not be verified')
    return repository


def metadata(path):
    value = json.loads((path / 'run.json').read_text())
    if value.get('schema') != 1 or value.get('repository') != REPOSITORY or value.get('approval') != APPROVAL \
            or not re.fullmatch(r'smoke-[0-9]+-[0-9]+', value.get('instance', '')):
        raise Refused('Smoke coordinates are unrecognized; do not guess cleanup targets')
    return value


def outcomes(path, name, value):
    evidence = path / 'evidence'
    evidence.mkdir(mode=0o700, exist_ok=True)
    private_write(evidence / (name + '.json'), value)


def install_failure(path, result):
    # Retain only a short diagnostic, never the private installer log or credential supplies.
    text = re.sub(r'\x1b\[[0-9;]*m', '', result.stdout + result.stderr)
    for name in ('GH_TOKEN', 'GITHUB_TOKEN', 'OPENAI_API_KEY'):
        if value := os.environ.get(name):
            text = text.replace(value, '<redacted>')
    # Enrollment/setup secrets, bearer tokens and hashes are opaque long strings.
    text = re.sub(r'[A-Za-z0-9_+/=\-]{32,}', '<redacted>', text)
    text = re.sub(r'[\x00-\x08\x0b-\x1f\x7f]', '', text)
    diagnostic = '\n'.join(line[:300] for line in text.splitlines() if line.strip())
    outcomes(path, 'install-failure', {'stage': 'fresh-install', 'exitCode': result.returncode,
                                      'diagnostic': diagnostic[-4000:]})


def tools(path):
    path = Path(path)
    path.mkdir(mode=0o700, parents=True, exist_ok=True)
    version, url, digest = runpy.run_path(str(ROOT / 'scripts/compose-image.py'))['ARTIFACTS']['gh']
    archive = path / 'gh.tar.gz'
    with urllib.request.urlopen(url, timeout=60) as response, archive.open('wb') as output:
        shutil.copyfileobj(response, output)
    if hashlib.sha256(archive.read_bytes()).hexdigest() != digest:
        raise Refused('Candidate GitHub CLI checksum differs from the reviewed catalog')
    with tarfile.open(archive, 'r:gz') as package:
        member = package.getmember(f'gh_{version}_linux_amd64/bin/gh')
        if not member.isfile():
            raise Refused('Candidate GitHub CLI is not a regular executable')
        with package.extractfile(member) as source, (path / 'gh').open('wb') as output:
            shutil.copyfileobj(source, output)
    (path / 'gh').chmod(0o700)
    archive.unlink()
    if not command([path / 'gh', '--version']).stdout.startswith('gh version ' + version + ' '):
        raise Refused('Candidate GitHub CLI did not report its selected version')
    print('Installed candidate GitHub CLI ' + version)


def variables():
    rows, page = [], 1
    while True:
        value = gh(f'repos/{REPOSITORY}/agents/variables?per_page=100&page={page}')
        if not isinstance(value.get('variables'), list) or not isinstance(value.get('total_count'), int):
            raise Refused('Agent variable inventory is unconfirmed')
        rows += value['variables']
        if len(rows) >= value['total_count']:
            return rows
        if not value['variables'] or page >= 10:
            raise Refused('Agent variable inventory exceeded its bounded sweep')
        page += 1


def sweep_discussions():
    pending, cursor, pages = [], None, 0
    while True:
        value = graphql('''query($cursor:String){repository(owner:"repoagent",name:"ControllerTestRepo"){
          discussions(first:100,after:$cursor){nodes{id title body author{login}}pageInfo{hasNextPage endCursor}}}}''', {'cursor': cursor})
        pages += 1
        page = value['repository']['discussions']
        for item in page['nodes']:
            if TITLE.fullmatch(item['title']) and item['body'].startswith(MARKER + '\n') \
                    and (item.get('author') or {}).get('login') == 'repoagent':
                pending.append(item['id'])
        if not page['pageInfo']['hasNextPage']:
            break
        cursor = page['pageInfo']['endCursor']
        if not cursor or pages >= 10:
            raise Refused('Discussion sweep exceeded its bounded inventory')
    for node in pending:
        graphql('mutation($id:ID!){deleteDiscussion(input:{id:$id}){clientMutationId}}', {'id': node})
    return len(pending)


def parsed_outcome(result):
    candidates = []
    for line in result.stdout.splitlines():
        try:
            value = json.loads(line)
        except json.JSONDecodeError:
            continue
        if isinstance(value, dict) and 'githubMarker' in value:
            candidates.append(value)
    if len(candidates) != 1:
        raise Refused('Revocation did not return one structured closure outcome')
    return candidates[0]


def revoke(arguments):
    result = command(['pwsh', '-NoLogo', '-NoProfile', '-File', ROOT / 'install.ps1', '-Action', 'Revoke',
                      '-Repository', REPOSITORY, '-NonInteractive', '-Apply', *arguments], check=False)
    value = parsed_outcome(result)
    if result.returncode or value.get('repository') != REPOSITORY or value.get('approvalVariable') != APPROVAL \
            or value.get('githubMarker') not in ('removed', 'absent'):
        raise Refused('Smoke approval withdrawal is unconfirmed; pinning remains blocked')
    return value


def smoke_marker(marker, repository_id):
    return isinstance(marker, dict) and all(marker.get(key) == value for key, value in {
        'version': 4, 'approvalKind': 'repository-agent-variable', 'appId': 5387625,
        'appSlug': 'repo-agent-connector', 'repositoryId': repository_id, 'repository': REPOSITORY,
        'variable': APPROVAL, 'profile': 'webhook-clone', 'githubHost': 'github.com',
        'policyVersion': 'repository-agent-clone-approval-v4'}.items())

def sweep(path):
    repository = identity()
    path = directory(path)
    run, attempt = os.environ.get('GITHUB_RUN_ID', ''), os.environ.get('GITHUB_RUN_ATTEMPT', '')
    if not re.fullmatch(r'[0-9]{1,20}', run) or not re.fullmatch(r'[0-9]{1,4}', attempt):
        raise Refused('Hosted run coordinates are required')
    value = {'schema': 1, 'repository': REPOSITORY, 'repositoryId': repository['id'], 'approval': APPROVAL,
             'instance': f'smoke-{run}-{attempt}', 'image': os.environ.get('IMAGE', '')}
    if not re.fullmatch(r'repoagent-controller:[a-z0-9-]+', value['image']):
        raise Refused('Use the local candidate image built by this workflow')
    if (path / 'run.json').exists():
        previous = metadata(path)
        if any(previous.get(key) != item for key, item in value.items()):
            raise Refused('Existing smoke state belongs to different coordinates')
        value = previous
    private_write(path / 'run.json', value)
    deleted = sweep_discussions()
    approval = next((row for row in variables() if row['name'] == APPROVAL), None)
    withdrawn = False
    if approval:
        marker = json.loads(approval['value'])
        if not smoke_marker(marker, repository['id']):
            raise Refused('Reserved smoke approval contains an unexpected binding; manual inspection is required')
        digest = hashlib.sha256(approval['value'].encode()).hexdigest()
        revoke(['-ApprovalLocation', 'AgentVariable', '-ApprovalVariable', APPROVAL,
                '-ExpectedValueSha256', digest, '-ExpectedRepositoryId', str(repository['id'])])
        withdrawn = True
    outcomes(path, 'sweep', {'discussionsRemoved': deleted, 'oldApprovalWithdrawn': withdrawn,
                            'oldBrokerClosure': 'unconfirmed' if withdrawn else 'not-observed',
                            'oldTokens': 'unsettled', 'replacementRequiresBrokerPreflight': True})


def install(path):
    path = directory(path)
    value = metadata(path)
    identity()
    key = os.environ.get('OPENAI_API_KEY')
    if not key:
        raise Refused('OPENAI_API_KEY is required; no credential is invented')
    inventory = json.loads(command(['docker', 'run', '--rm', '--network', 'none', '--entrypoint', 'cat',
                                    value['image'], '/opt/repoagent/image-inventory.json']).stdout)
    expected = json.loads((ROOT / 'image/default.json').read_text())
    if inventory['githubCli']['version'] != expected['githubCli']['version'] \
            or inventory['agent']['version'] != expected['agent']['version'] \
            or inventory['tools']['cloudflared'] != expected['tools']['cloudflared']:
        raise Refused('The candidate image differs from the selected tool inventory')
    if not command(['gh', '--version']).stdout.startswith('gh version ' + expected['githubCli']['version'] + ' '):
        raise Refused('Runner GitHub CLI differs from the candidate image')
    versions = command(['docker', 'run', '--rm', '--network', 'none', value['image'], 'versions']).stdout
    checks = ((r'^gh version (\S+) ', expected['githubCli']['version']),
              (r'^codex-cli (\S+)$', expected['agent']['version']),
              (r'^cloudflared version (\S+) ', expected['tools']['cloudflared']))
    for pattern, selected in checks:
        if re.findall(pattern, versions, re.M) != [selected]:
            raise Refused('A candidate tool did not report exactly the selected version')
    image = json.loads(command(['docker', 'image', 'inspect', value['image']]).stdout)[0]
    if image['Os'] != 'linux' or image['Architecture'] != 'amd64' or image['Config']['User'] != '1000' or (image['Config'].get('Labels') or {}).get('io.repoagent.role') != 'controller':
        raise Refused('Candidate platform, role or runtime identity differs from the operator image contract')
    value['imageId'] = image['Id']
    private_write(path / 'run.json', value)
    auth = path / 'codex-auth.json'
    private_write(auth, {'OPENAI_API_KEY': key})
    state = path / 'state'
    arguments = ['pwsh', '-NoLogo', '-NoProfile', '-File', ROOT / 'install.ps1', '-Authentication', 'Broker',
                 '-Repository', REPOSITORY, '-BrokerOrigin', BROKER, '-ApprovalVariable', APPROVAL,
                 '-ApprovalLocation', 'AgentVariable', '-Clone', '-Mode', 'quick-tunnel', '-MentionUser', '',
                 '-Instance', value['instance'], '-StateDirectory', state, '-SkipBuild',
                 '-CodexAuthPath', auth, '-ReconcileInterval', '3600', '-WebSearch', 'disabled', '-NonInteractive', '-Apply']
    deadline = time.monotonic() + 80 * 60
    environment = dict(os.environ, REPOAGENT_IMAGE=value['image'])
    while True:
        result = command(arguments, check=False, timeout=900, environment=environment)
        # Raw installer output stays private and is never an uploaded artifact.
        (path / 'installer.log').write_text(result.stdout + result.stderr)
        (path / 'installer.log').chmod(0o600)
        if result.returncode == 0:
            break
        text = ' '.join(re.sub(r'\x1b\[[0-9;]*m', '', result.stdout + result.stderr).split())
        if 'Old repository authority is not ready for replacement. Recheck using this same private state after at least 60 seconds;' in text:
            delay = 60
        elif 'Broker discovery is still pending. Rerun the same request after at least five seconds.' in text:
            delay = 5
        else:
            install_failure(path, result)
            raise Refused('Fresh installation failed; preserve private state and inspect the bounded installer diagnostic')
        if time.monotonic() + delay >= deadline:
            install_failure(path, result)
            raise Refused('Replacement is still not ready after the bounded wait; no new request identity is selected')
        print('Rechecking the same private enrollment request after the required interval; readiness is not promised.')
        time.sleep(delay)
    enrollment = json.loads((state / 'enrollment.json').read_text())
    approval = json.loads((state / 'approval.json').read_text())
    if approval.get('setupVersion') != 4 or approval.get('profile') != 'webhook-clone':
        raise Refused('The installed controller did not use the matching v4 tools')
    runtime = json.loads(command(['docker', 'inspect', 'repoagent-' + value['instance']]).stdout)[0]
    names = {item.split('=', 1)[0] for item in runtime['Config'].get('Env') or []}
    if runtime['HostConfig']['Privileged'] or runtime['Config']['User'] != '1000' or runtime['Image'] != value['imageId'] or names & {'GH_TOKEN', 'GITHUB_TOKEN', 'OPENAI_API_KEY'}:
        raise Refused('Installed runtime privilege, image or credential isolation differs from the operator contract')
    value['actor'] = enrollment['appSlug'] + '[bot]'
    private_write(path / 'run.json', value)
    outcomes(path, 'installed', {'state': 'ready', 'setupVersion': 4, 'mode': 'quick-tunnel',
                               'tools': {name: inventory[name] for name in ('githubCli', 'agent', 'tools')}})


def journey(path):
    path = directory(path)
    value = metadata(path)
    environment = dict(os.environ, REPOAGENT_REPOSITORY=REPOSITORY, REPOAGENT_ACTOR=value['actor'],
                       REPOAGENT_EVIDENCE=str(path / 'evidence'), REPOAGENT_SMOKE_LOG=str(path / 'bridge.log'))
    stopped = threading.Event()

    def capture():
        while not stopped.is_set():
            result = command(['docker', 'logs', '--tail', '2000', 'repoagent-' + value['instance']], check=False)
            (path / 'bridge.log').write_text(result.stdout + result.stderr)
            (path / 'bridge.log').chmod(0o600)
            stopped.wait(2)

    thread = threading.Thread(target=capture, daemon=True)
    thread.start()
    try:
        result = command(['dotnet', 'run', '--project', 'tests/RepoAgent.LiveTests', '--configuration', 'Release', '--',
                          '-method', '*Packaged_bridge_preserves*'], environment=environment, timeout=600, check=False)
        if result.returncode:
            raise Refused('Real hosted Discussion journey failed; inspect the nonsecret outcome artifact')
        print('Real hosted Quick Tunnel, EYES receipt and App reply observed.')
    finally:
        stopped.set()
        thread.join(timeout=125)


def cleanup(path):
    path = directory(path)
    if not (path / 'run.json').exists():
        outcomes(path, 'cleanup', {'state': 'not-started', 'brokerClosure': 'not-observed', 'tokens': 'unsettled'})
        return
    value = metadata(path)
    failures, closure = [], None
    name = 'repoagent-' + value['instance']
    try:
        running = command(['docker', 'inspect', '--format', '{{.State.Running}}', name], check=False)
        if running.returncode == 0 and running.stdout.strip() == 'true':
            environment = dict(os.environ, REPOAGENT_INSTANCE=value['instance'], REPOAGENT_IMAGE=value['image'],
                               REPOAGENT_STATE_DIRECTORY=str(path / 'state'))
            command(['pwsh', '-NoLogo', '-NoProfile', '-File', ROOT / 'repoagent.ps1', 'stop'], timeout=180,
                    environment=environment)
        state = path / 'state'
        if (state / 'enrollment.json').exists() and (state / 'approval.json').exists():
            closure = revoke(['-ApprovalStatePath', str(state / 'approval.json'),
                              '-EnrollmentFile', str(state / 'enrollment.json')])
            if closure.get('brokerClosure') != 'confirmed':
                raise Refused('Enrollment closure is unconfirmed even though the approval may be absent')
        else:
            recovery = state / 'recovery.json'
            if recovery.exists() and json.loads(recovery.read_text()).get('ackAttempted') is not False:
                raise Refused('Missing enrollment credential after an acknowledgement attempt; closure is unconfirmed')
            approval = next((row for row in variables() if row['name'] == APPROVAL), None)
            if approval:
                marker = json.loads(approval['value'])
                if not smoke_marker(marker, value['repositoryId']):
                    raise Refused('Incomplete setup approval binding differs; no deletion attempted')
                digest = hashlib.sha256(approval['value'].encode()).hexdigest()
                revoke(['-ApprovalLocation', 'AgentVariable', '-ApprovalVariable', APPROVAL,
                        '-ExpectedValueSha256', digest, '-ExpectedRepositoryId', str(value['repositoryId'])])
        if any(row['name'] == APPROVAL for row in variables()):
            raise Refused('Reserved smoke approval still exists after revocation')
        command(['docker', 'rm', name], check=False)
        for storage in ('config', 'credentials', 'workspace', 'history', 'state', 'tools', 'enrollment'):
            volume = name + '-' + storage
            inspected = command(['docker', 'volume', 'inspect', volume], check=False)
            if inspected.returncode:
                continue
            labels = json.loads(inspected.stdout)[0].get('Labels') or {}
            if labels.get('io.repoagent.owner') != 'private-runtime-v1' or labels.get('io.repoagent.instance') != value['instance'] \
                    or labels.get('io.repoagent.storage') != storage or (value.get('imageId') and labels.get('io.repoagent.image') != value['imageId']):
                raise Refused('Smoke storage ownership is unconfirmed; no deletion attempted')
            command(['docker', 'volume', 'rm', volume])
        for secret in (path / 'codex-auth.json', state / 'enrollment.json'):
            secret.unlink(missing_ok=True)
    except (Refused, OSError, ValueError, subprocess.TimeoutExpired) as error:
        failures.append(str(error))
    try:
        removed = sweep_discussions()
    except (Refused, OSError, ValueError, subprocess.TimeoutExpired) as error:
        removed = None
        failures.append(str(error))
    outcomes(path, 'cleanup', {'state': 'unconfirmed' if failures else 'complete', 'discussionsRemoved': removed,
                              'approval': 'unconfirmed' if failures else 'absent',
                              'brokerClosure': (closure or {}).get('brokerClosure', 'not-activated'),
                              'outstandingTokens': 'unsettled', 'failures': failures})
    if failures:
        raise Refused('Cleanup is unconfirmed; the candidate must not be pinned')


if __name__ == '__main__':
    try:
        if len(sys.argv) != 3 or sys.argv[1] not in ('tools', 'sweep', 'install', 'journey', 'cleanup'):
            raise Refused('Expected tools|sweep|install|journey|cleanup DIRECTORY')
        if sys.argv[1] != 'tools' and os.environ.get('REPOAGENT_LIVE') != '1':
            raise Refused('REPOAGENT_LIVE=1 is required to authorize disposable hosted smoke activity')
        globals()[sys.argv[1]](Path(sys.argv[2]))
    except Refused as error:
        sys.exit('Hosted smoke refused: ' + str(error))
    except (OSError, ValueError, KeyError, TypeError, subprocess.TimeoutExpired):
        # Provider output and private file contents never enter workflow logs or artifacts.
        sys.exit('Hosted smoke refused. Inspect retained private run state and nonsecret evidence; no pin is authorized.')
