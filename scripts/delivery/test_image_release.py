"""Automatic baseline image publication and its discoverable GitHub Release.

Workflow tests evaluate the shipped trigger filters and job conditions; release
tests execute the workflow's actual shell steps with a recording gh double.
Neither proves a hosted Actions run, GHCR access or GitHub Release API behavior.
"""
import hashlib
import json
import os
import re
from pathlib import Path
import subprocess
import sys
import tempfile
import textwrap
import unittest

ROOT = Path(__file__).resolve().parents[2]
WORKFLOW = ROOT / '.github/workflows/controller.yml'
DEFAULT = json.loads((ROOT / 'image/default.json').read_text())
GH, CODEX = DEFAULT['githubCli']['version'], DEFAULT['agent']['version']
SHA = 'b' * 40
RUN, ATTEMPT = '39886772576', '2'
REFERENCE = f'ghcr.io/repoagent/controller:sha-{SHA}-{RUN}-{ATTEMPT}'
IMAGE = 'ghcr.io/repoagent/controller@sha256:' + 'a' * 64
CONFIG_DIGEST = 'sha256:' + 'c' * 64
TAG = f'baseline-{RUN}.{ATTEMPT}'
CANARY = 'ghs_TOKEN-MUST-NOT-APPEAR'
MARKER = '<!-- repoagent.baseline-release/v1 -->'
# Shape of the shipping image's `versions` command output.
VERSIONS = f'''RepoAgent container; uid=1000
RepoAgentController: Native AOT, built with .NET SDK 11.0.100-rc.3
Python 3.14.4
gh version {GH} (2026-09-30)
https://github.com/cli/cli/releases/tag/v{GH}
codex-cli {CODEX}
ripgrep 15.1.0

PCRE2 10.43 is available (JIT is available)
cloudflared version 2026.10.1 (built 2026-10-15-17:08 UTC)
'''
# Transport double only: records the gh invocation; implements no release decisions.
GH_DOUBLE = '''import json, os, sys
with open(os.environ['GH_LEDGER'], 'a') as ledger:
    ledger.write(json.dumps(dict(argv=sys.argv[1:], repo=os.environ.get('GH_REPO'),
                                 token=os.environ.get('GH_TOKEN') == os.environ['EXPECTED_TOKEN'])) + '\\n')
if sys.argv[1:3] == ['release', 'view']:
    sys.exit(0 if os.environ.get('GH_RELEASE_EXISTS') else 'release not found')
'''


def triggers():
    """Event -> {'branches': [...], 'paths': [...]} from the workflow's on: block."""
    block = WORKFLOW.read_text().split('\non:\n', 1)[1].split('\npermissions:', 1)[0]
    events, event, key = {}, None, None
    for line in block.splitlines():
        if re.fullmatch(r'  [a-z_]+:', line):
            event = line.strip()[:-1]
            events[event] = {}
        elif match := re.fullmatch(r'    (branches|paths): \[(.*)\]', line):
            events[event][match[1]] = [item.strip().strip("'") for item in match[2].split(',')]
        elif match := re.fullmatch(r'    (branches|paths):', line):
            key = match[1]
            events[event][key] = []
        elif match := re.fullmatch(r"      - '(.+)'", line):
            events[event][key].append(match[1])
    return events


def matches(pattern, path):
    # GitHub filter globs: ** crosses directories, * stays within one segment.
    expression = re.escape(pattern).replace(r'\*\*', '.*').replace(r'\*', '[^/]*')
    return re.fullmatch(expression, path) is not None


def job(name):
    jobs = WORKFLOW.read_text().split('\njobs:\n', 1)[1]
    body = re.split(rf'(?m)^  {re.escape(name)}:\n', jobs, maxsplit=1)[1]
    return re.split(r'(?m)^  (?=[a-z-]+:\n)', body, maxsplit=1)[0]


def runs(name, *, event, ref='refs/heads/main', repository='repoagent/controller', tag='', smoke=False, verify=False):
    """Evaluate a job's if: expression for one GitHub context (the subset this workflow uses)."""
    condition = re.search(r'^    if: (.+)$', job(name), re.M)
    if condition is None:
        return True
    context = {'event_name': event, 'ref': ref, 'repository': repository}
    expression = re.sub(r'github\.([a-z_]+)', lambda match: repr(context[match[1]]), condition[1])
    expression = expression.replace('inputs.tag', repr(tag)).replace('inputs.smoke', repr(smoke)).replace('inputs.verify', repr(verify)).replace(' true', ' True')
    expression = expression.replace('&&', ' and ').replace('||', ' or ')
    if not re.fullmatch(r'(?:\s|\(|\)|==|!=|and|or|True|False)*', re.sub(r"'[^']*'", '', expression)):
        raise AssertionError('Unsupported workflow expression: ' + condition[1])
    return eval(expression, {'__builtins__': {}})


def needs(name):
    match = re.search(r'^    needs: \[(.*)\]$', job(name), re.M)
    return {item.strip() for item in match[1].split(',')} if match else set()


def shell(job_name, step_name):
    """The actual run: script of one workflow step."""
    step = re.split(r'(?m)^      - ', job(job_name).split(f'      - name: {step_name}\n', 1)[1], maxsplit=1)[0]
    inline = re.search(r'^        run: (?!\|)(.+)$', step, re.M)
    return inline[1] if inline else textwrap.dedent(step.split('        run: |\n', 1)[1])


def metadata(notes):
    return json.loads(notes.split(MARKER, 1)[1].split('```json\n', 1)[1].split('\n```', 1)[0])


class AutomaticPublicationTests(unittest.TestCase):
    def test_relevant_main_changes_are_verified_and_docs_only_changes_do_not_build_images(self):
        on = triggers()
        self.assertEqual(on['push'].get('branches'), ['main'])
        for path in (ROOT / 'image/source-files.txt').read_text().split() + ['scripts/delivery/controller-release.py']:
            with self.subTest(path=path):
                self.assertTrue(any(matches(pattern, path) for pattern in on['push']['paths']))
        for path in ['docs/ci-cd.md', 'service/auth/Program.cs', 'README.md']:
            self.assertFalse(any(matches(pattern, path) for pattern in on['push']['paths']))
        self.assertEqual(on['push']['paths'], on['pull_request']['paths'])

    def test_ordinary_ci_and_explicit_release_pr_checks_never_publish(self):
        for name in ['build', 'verify']:
            self.assertTrue(runs(name, event='pull_request', ref='refs/pull/7/merge'))
            self.assertTrue(runs(name, event='push'))
            self.assertTrue(runs(name, event='workflow_dispatch', ref='refs/heads/release-please--branches--main--components--controller', verify=True))
            self.assertFalse(runs(name, event='workflow_dispatch', ref='refs/heads/feature'))
        for name in ['publish', 'release', 'native-release']:
            self.assertNotRegex(WORKFLOW.read_text(), r'(?m)^  ' + name + ':')

    def semantic(self, name):
        workflow = (ROOT / '.github/workflows/controller-release.yml').read_text()
        return re.split(r'(?m)^  (?=[a-z-]+:\n)', workflow.split('\n  ' + name + ':\n', 1)[1], maxsplit=1)[0]

    def test_one_stable_main_source_drives_both_native_and_image_artifacts(self):
        workflow = (ROOT / '.github/workflows/controller-release.yml').read_text()
        self.assertIn("github.repository == 'repoagent/controller' && github.ref == 'refs/heads/main'", self.semantic('select'))
        self.assertIn('controller-release.py select', self.semantic('select'))
        self.assertIn('ref: ${{ needs.select.outputs.source }}', self.semantic('build'))
        self.assertIn('verify.sh "$RUNNER_TEMP/controller-image" "$VERSION"', self.semantic('build'))
        self.assertIn('NativeSmoke', self.semantic('build'))
        self.assertIn('cmp "$RUNNER_TEMP/controller-plan/RELEASE_NOTES.md"', self.semantic('build'))
        self.assertIn('cancel-in-progress: false', workflow)
        self.assertNotIn('pull_request:', workflow)

    def test_publication_requires_verified_transfer_positive_absence_and_anonymous_digest(self):
        publication = self.semantic('publish')
        self.assertIn('needs: [select, build]', publication)
        self.assertIn('sha256sum --check SHA256SUMS', publication)
        self.assertIn('controller-release.py image-free', publication)
        self.assertIn('--src-no-creds --preserve-digests', publication)
        self.assertIn('"docker://$reference")" = "$digest"', publication)
        self.assertIn('needs: [select, build, publish]', self.semantic('finalize'))
        self.assertIn('controller-release.py finalize', self.semantic('finalize'))

    def test_registry_and_release_write_privileges_remain_separate(self):
        publish, finish = self.semantic('publish'), self.semantic('finalize')
        self.assertIn('packages: write', publish)
        self.assertNotIn('contents: write', publish)
        self.assertIn('contents: write', finish)
        self.assertNotIn('packages: write', finish)
        for name in ['select', 'build']:
            self.assertNotIn('contents: write', self.semantic(name))
            self.assertNotIn('packages: write', self.semantic(name))

    def test_superseded_read_jobs_cancel_and_completed_release_retries_do_not_republish(self):
        for name in ['build', 'verify-artifact', 'verify']:
            self.assertIn('cancel-in-progress: true', job(name))
            self.assertIn('${{ github.ref }}', job(name))
        for name in ['build', 'publish', 'finalize']:
            self.assertIn("if: needs.select.outputs.complete != 'true'", self.semantic(name))

class BaselineReleaseTests(unittest.TestCase):
    def artifact(self, versions=VERSIONS):
        """The build job's checksummed artifact, with the inventory composed from this checkout."""
        temporary = Path(self.enterContext(tempfile.TemporaryDirectory(prefix='baseline release ')))
        composed = subprocess.run([sys.executable, str(ROOT / 'scripts/compose-image.py'), '--source', str(ROOT),
                                   '--manifest', str(ROOT / 'image/default.json'),
                                   '--output', str(temporary / 'context')], capture_output=True, text=True)
        self.assertEqual(composed.returncode, 0, composed.stderr)
        artifact = temporary / 'controller-image'
        artifact.mkdir()
        (artifact / 'controller.tar').write_bytes(b'docker archive fixture')
        (artifact / 'build.json').write_text(json.dumps({'containerimage.config.digest': CONFIG_DIGEST}))
        # This fixture represents the retained historical v1 publisher contract.
        inventory = json.loads((temporary / 'context/image-inventory.json').read_text())
        inventory['controller'].pop('version', None)
        inventory['controller'].pop('releaseNotesSha256', None)
        (artifact / 'image-inventory.json').write_text(json.dumps(inventory))
        (artifact / 'versions.txt').write_text(versions)
        sums = subprocess.run(['sha256sum', 'controller.tar', 'build.json', 'versions.txt', 'image-inventory.json'],
                              cwd=artifact, capture_output=True, text=True, check=True)
        (artifact / 'SHA256SUMS').write_text(sums.stdout)
        return temporary

    def run_step(self, job_name, step_name, temporary, **environment):
        binaries = temporary / 'bin'
        binaries.mkdir(exist_ok=True)
        (binaries / 'gh').write_text(f'#!{sys.executable}\n' + GH_DOUBLE)
        (binaries / 'gh').chmod(0o700)
        ledger = temporary / 'gh.jsonl'
        env = dict(PATH=str(binaries) + os.pathsep + os.environ['PATH'], HOME=str(temporary), LANG='C.UTF-8',
                   RUNNER_TEMP=str(temporary), GH_LEDGER=str(ledger), GH_TOKEN=CANARY, EXPECTED_TOKEN=CANARY,
                   GH_REPO='repoagent/controller', IMAGE=IMAGE, REFERENCE=REFERENCE, GITHUB_SHA=SHA,
                   GITHUB_REPOSITORY='repoagent/controller', GITHUB_SERVER_URL='https://github.com',
                   GITHUB_RUN_ID=RUN)
        env.update(environment)
        command = [sys.executable, str(ROOT / 'scripts/delivery/image-release.py'), 'publish', str(temporary / 'controller-image')] if job_name == 'release' else ['bash', '-e', '-c', shell(job_name, step_name)]
        result = subprocess.run(command, cwd=ROOT, env=env,
                                capture_output=True, text=True, timeout=30)
        calls = [json.loads(line) for line in ledger.read_text().splitlines()] if ledger.exists() else []
        self.assertNotIn(CANARY, result.stdout + result.stderr + json.dumps(calls))
        return result, calls

    def test_release_identifies_the_immutable_image_and_actual_bundled_versions(self):
        temporary = self.artifact()
        result, calls = self.run_step('release', 'Publish the discoverable baseline release', temporary)

        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual([call['argv'][:3] for call in calls], [['release', 'view', TAG], ['release', 'create', TAG]])
        self.assertTrue(all(call['repo'] == 'repoagent/controller' and call['token'] for call in calls))
        create = calls[1]['argv']
        options = dict(zip(create[3::2], create[4::2]))
        self.assertEqual(set(options), {'--target', '--title', '--notes'})
        self.assertEqual(options['--target'], SHA)
        self.assertIn(f'gh {GH}', options['--title'])
        self.assertIn(f'Codex {CODEX}', options['--title'])
        self.assertIn('docker pull ' + IMAGE, options['--notes'])
        inventory = (temporary / 'controller-image/image-inventory.json').read_bytes()
        self.assertEqual(metadata(options['--notes']), {
            'schema': 'repoagent.baseline-release/v1', 'release': TAG, 'platform': 'linux/amd64',
            'image': IMAGE, 'imageTag': REFERENCE, 'imageConfigDigest': CONFIG_DIGEST,
            'versions': {'controller': SHA, 'gh': GH, 'codex': CODEX},
            'inventorySha256': hashlib.sha256(inventory).hexdigest(),
            'run': 'https://github.com/repoagent/controller/actions/runs/' + RUN})

    def test_unverifiable_inputs_or_an_existing_release_never_create_a_release(self):
        cases = {
            'existing release': dict(GH_RELEASE_EXISTS='1'),
            'mutable image tag': dict(IMAGE='ghcr.io/repoagent/controller:latest'),
            'other repository': dict(IMAGE='ghcr.io/repoagent/controller-auth@sha256:' + 'a' * 64),
            'missing publish output': dict(IMAGE=''),
            'reference from another source': dict(REFERENCE=REFERENCE.replace(SHA, 'd' * 40)),
            'bundled gh differs from inventory': dict(versions=VERSIONS.replace(f'gh version {GH} ', 'gh version 9.9.9 ')),
            'artifact changed after checksum': dict(tamper=True),
        }
        for name, case in cases.items():
            with self.subTest(case=name):
                temporary = self.artifact(case.pop('versions', VERSIONS))
                if case.pop('tamper', False):
                    with (temporary / 'controller-image/versions.txt').open('a') as stream:
                        stream.write('unreviewed change after checksum\n')
                result, calls = self.run_step('release', 'Publish the discoverable baseline release', temporary, **case)

                self.assertNotEqual(result.returncode, 0)
                self.assertNotIn(['release', 'create'], [call['argv'][:2] for call in calls])

    def test_pull_request_check_requires_actual_bundled_versions_to_match_the_inventory(self):
        result, calls = self.run_step('build', 'Check release metadata for the bundled versions', self.artifact())
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(json.loads(result.stdout), {'controller': SHA, 'gh': GH, 'codex': CODEX})
        self.assertEqual(calls, [])
        for name, versions in {
                'gh differs': VERSIONS.replace(f'gh version {GH} ', 'gh version 9.9.9 '),
                'codex differs': VERSIONS.replace(f'codex-cli {CODEX}', 'codex-cli 9.9.9'),
                'codex missing': VERSIONS.replace(f'codex-cli {CODEX}\n', ''),
                'gh ambiguous': VERSIONS + f'gh version {GH} (2026-09-30)\n'}.items():
            with self.subTest(case=name):
                result, calls = self.run_step('build', 'Check release metadata for the bundled versions',
                                              self.artifact(versions))
                self.assertNotEqual(result.returncode, 0)
                self.assertEqual(calls, [])


if __name__ == '__main__':
    unittest.main()
