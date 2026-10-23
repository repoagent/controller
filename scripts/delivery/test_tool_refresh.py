"""Upstream gh/Codex discovery feeding verified baseline image publication.

Step tests execute the refresh workflow's actual shell steps against a copy of
this checkout, with a gh double that serves controlled upstream release
metadata and records GitHub writes. Workflow tests evaluate the shipped
triggers, dependencies and permissions. Neither proves a hosted Actions run,
live GitHub availability or that a particular upstream release is compatible.
"""
import base64
import json
import os
from pathlib import Path
import re
import runpy
import shutil
import subprocess
import sys
import tempfile
import textwrap
import unittest

ROOT = Path(__file__).resolve().parents[2]
WORKFLOWS = ROOT / '.github/workflows'
WORKFLOW = WORKFLOWS / 'refresh-baseline-tools.yml'
DEFAULT = json.loads((ROOT / 'image/default.json').read_text())
GH, CODEX = DEFAULT['githubCli']['version'], DEFAULT['agent']['version']
CATALOG = runpy.run_path(str(ROOT / 'scripts/compose-image.py'))['ARTIFACTS']
SHA = 'b' * 40
CANARY = 'ghs_TOKEN-MUST-NOT-APPEAR'
# Newer than anything this repository will pin for a long time, so the tests do not age.
GH_NEW, CODEX_NEW = '99.1.0', '99.2.0'
DIGESTS = {'gh': '1' * 64, 'codex': '2' * 64, 'codex-code-mode-host': '3' * 64}


def gh_release(version, **changes):
    name = f'gh_{version}_linux_amd64.tar.gz'
    release = {'tag_name': f'v{version}', 'draft': False, 'prerelease': False, 'assets': [
        {'name': f'gh_{version}_linux_arm64.tar.gz', 'digest': 'sha256:' + '9' * 64,
         'browser_download_url': f'https://github.com/cli/cli/releases/download/v{version}/gh_{version}_linux_arm64.tar.gz'},
        {'name': name, 'digest': 'sha256:' + DIGESTS['gh'],
         'browser_download_url': f'https://github.com/cli/cli/releases/download/v{version}/{name}'}]}
    release.update(changes)
    return release


def codex_release(version, **changes):
    release = {'tag_name': f'rust-v{version}', 'draft': False, 'prerelease': False, 'assets': [
        {'name': f'{tool}-x86_64-unknown-linux-musl.tar.gz', 'digest': 'sha256:' + DIGESTS[tool],
         'browser_download_url': f'https://github.com/openai/codex/releases/download/rust-v{version}/{tool}-x86_64-unknown-linux-musl.tar.gz'}
        for tool in ('codex', 'codex-code-mode-host')]}
    release.update(changes)
    return release


# Transport double only: serves controlled upstream release metadata and records
# every invocation. It implements no selection, commit or publication decisions.
GH_DOUBLE = '''import json, os, sys
argv = sys.argv[1:]
body = sys.stdin.read() if argv[:2] == ['api', 'graphql'] else None
with open(os.environ['GH_LEDGER'], 'a') as ledger:
    ledger.write(json.dumps(dict(argv=argv, repo=os.environ.get('GH_REPO'), body=body,
                                 token=os.environ.get('GH_TOKEN') == os.environ['EXPECTED_TOKEN'])) + '\\n')
releases = json.load(open(os.environ['GH_RELEASES']))
if len(argv) == 2 and argv[0] == 'api' and argv[1].endswith('/releases/latest'):
    repository = argv[1].removeprefix('repos/').removesuffix('/releases/latest')
    if repository not in releases:
        sys.exit('gh: Not Found (HTTP 404)')
    print(json.dumps(releases[repository]))
elif argv[:2] == ['api', 'graphql']:
    if os.environ.get('GH_COMMIT_REFUSED'):
        sys.exit('gh: Expected branch to point to the expected head but it did not')
    print(json.dumps({'data': {'createCommitOnBranch': {'commit': {'oid': 'c' * 40}}}}))
elif argv[:2] == ['workflow', 'run'] and os.environ.get('GH_DISPATCH_FAILS'):
    sys.exit('HTTP 403: Resource not accessible by integration')
'''


def job(name):
    jobs = WORKFLOW.read_text().split('\njobs:\n', 1)[1]
    body = re.split(rf'(?m)^  {re.escape(name)}:\n', jobs, maxsplit=1)[1]
    return re.split(r'(?m)^  (?=[a-z-]+:\n)', body, maxsplit=1)[0]


def shell(job_name, step_name):
    """The actual run: script of one workflow step."""
    step = re.split(r'(?m)^      - ', job(job_name).split(f'      - name: {step_name}\n', 1)[1], maxsplit=1)[0]
    inline = re.search(r'^        run: (?!\|)(.+)$', step, re.M)
    return inline[1] if inline else textwrap.dedent(step.split('        run: |\n', 1)[1])


def on_block(workflow):
    return workflow.read_text().split('\non:\n', 1)[1].split('\npermissions:', 1)[0]


def events(workflow):
    return {line.strip()[:-1] for line in on_block(workflow).splitlines() if re.fullmatch(r'  [a-z_]+:', line)}


def outputs(path):
    return dict(line.split('=', 1) for line in path.read_text().splitlines()) if path.exists() else {}


class RefreshStepTests(unittest.TestCase):
    def checkout(self, temporary=None, name='checkout'):
        """A copy of the files a fresh checkout's refresh steps read and rewrite."""
        temporary = temporary or Path(self.enterContext(tempfile.TemporaryDirectory(prefix='tool refresh ')))
        copy = temporary / name
        files = (ROOT / 'image/source-files.txt').read_text().split() + ['image/default.json']
        files += [str(path.relative_to(ROOT)) for path in (ROOT / 'scripts/delivery').glob('*.py')]
        for relative in files:
            (copy / relative).parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(ROOT / relative, copy / relative)
        return temporary, copy

    def run_step(self, job_name, step_name, temporary, copy, releases, **environment):
        binaries = temporary / 'bin'
        binaries.mkdir(exist_ok=True)
        (binaries / 'gh').write_text(f'#!{sys.executable}\n' + GH_DOUBLE)
        (binaries / 'gh').chmod(0o700)
        (temporary / 'releases.json').write_text(json.dumps(releases))
        # Each step gets its own output file and call record, as in Actions.
        ledger, output = temporary / 'gh.jsonl', temporary / 'github-output'
        ledger.unlink(missing_ok=True)
        output.unlink(missing_ok=True)
        env = dict(PATH=str(binaries) + os.pathsep + os.environ['PATH'], HOME=str(temporary), LANG='C.UTF-8',
                   RUNNER_TEMP=str(temporary), GITHUB_OUTPUT=str(output), GH_LEDGER=str(ledger),
                   GH_RELEASES=str(temporary / 'releases.json'), GH_TOKEN=CANARY, EXPECTED_TOKEN=CANARY,
                   GH_REPO='repoagent/controller', GITHUB_SHA=SHA, GITHUB_REPOSITORY='repoagent/controller',
                   GITHUB_SERVER_URL='https://github.com', GITHUB_RUN_ID='4242')
        env.update(environment)
        result = subprocess.run(['bash', '-e', '-c', shell(job_name, step_name)], cwd=copy, env=env,
                                capture_output=True, text=True, timeout=60)
        calls = [json.loads(line) for line in ledger.read_text().splitlines()] if ledger.exists() else []
        self.assertNotIn(CANARY, result.stdout + result.stderr + json.dumps(calls))
        return result, calls, outputs(output)

    def select(self, temporary, copy, releases):
        return self.run_step('select', 'Select supported upstream gh and Codex releases', temporary, copy, releases)

    def apply(self, temporary, copy):
        return self.run_step('verify', 'Pin the selected versions in this checkout', temporary, copy, {})

    def commit(self, temporary, **environment):
        # The commit job checks out the same source again and downloads the selection artifact.
        _, copy = self.checkout(temporary, 'commit-checkout')
        return self.run_step('commit', 'Pin the verified versions on main and publish a baseline',
                             temporary, copy, {}, **environment)

    def composed(self, copy):
        """The inventory composition would build from this checkout."""
        context = copy.parent / 'context'
        shutil.rmtree(context, ignore_errors=True)
        result = subprocess.run([sys.executable, str(copy / 'scripts/compose-image.py'), '--source', str(copy),
                                 '--manifest', str(copy / 'image/default.json'), '--output', str(context)],
                                capture_output=True, text=True)
        self.assertEqual(result.returncode, 0, result.stderr)
        return json.loads(result.stdout)

    def test_newer_supported_upstream_releases_are_selected_and_pinned_for_verification(self):
        temporary, copy = self.checkout()
        upstream = {'cli/cli': gh_release(GH_NEW), 'openai/codex': codex_release(CODEX_NEW)}

        result, calls, selected = self.select(temporary, copy, upstream)

        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(selected.get('changed'), 'true')
        self.assertEqual(sorted(call['argv'][1] for call in calls),
                         ['repos/cli/cli/releases/latest', 'repos/openai/codex/releases/latest'])
        self.assertTrue(all(call['token'] for call in calls), 'Discovery uses the workflow token, not anonymous limits')
        self.assertEqual((copy / 'image/default.json').read_text(), (ROOT / 'image/default.json').read_text(),
                         'Selection alone changes nothing; verification pins it in its own checkout')

        result, calls, _ = self.apply(temporary, copy)

        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(calls, [])
        inventory = self.composed(copy)
        self.assertEqual((inventory['githubCli']['version'], inventory['agent']['version']), (GH_NEW, CODEX_NEW))
        expected = {
            'gh': (GH_NEW, f'https://github.com/cli/cli/releases/download/v{GH_NEW}/gh_{GH_NEW}_linux_amd64.tar.gz'),
            'codex': (CODEX_NEW, f'https://github.com/openai/codex/releases/download/rust-v{CODEX_NEW}/codex-x86_64-unknown-linux-musl.tar.gz'),
            'codex-code-mode-host': (CODEX_NEW, f'https://github.com/openai/codex/releases/download/rust-v{CODEX_NEW}/codex-code-mode-host-x86_64-unknown-linux-musl.tar.gz'),
        }
        for tool, (version, url) in expected.items():
            with self.subTest(tool=tool):
                self.assertEqual(inventory['artifacts'][tool], {'version': version, 'url': url, 'sha256': DIGESTS[tool]})
                # verify.sh downloads the native-test Codex from this same catalog.
                self.assertEqual(runpy.run_path(str(copy / 'scripts/compose-image.py'))['ARTIFACTS'][tool],
                                 (version, url, DIGESTS[tool]))
        self.assertEqual(inventory['artifacts']['cloudflared']['version'], CATALOG['cloudflared'][0])
        # The Azure planner reads catalog lines directly (scripts/azure/image-input.ps1).
        catalog = (copy / 'scripts/compose-image.py').read_text()
        for tool, version in {'gh': GH_NEW, 'codex': CODEX_NEW, 'cloudflared': CATALOG['cloudflared'][0]}.items():
            self.assertEqual(re.search(rf"(?m)^    '{tool}': \('([^']+)',", catalog)[1], version)

    def test_current_or_older_upstream_releases_select_nothing(self):
        for name, upstream in {
                'current': {'cli/cli': gh_release(GH), 'openai/codex': codex_release(CODEX)},
                'older is never a downgrade': {'cli/cli': gh_release('1.0.0'), 'openai/codex': codex_release('0.1.0')},
        }.items():
            with self.subTest(case=name):
                temporary, copy = self.checkout()
                result, _, selected = self.select(temporary, copy, upstream)
                self.assertEqual(result.returncode, 0, result.stderr)
                self.assertEqual(selected.get('changed'), 'false')
                self.assertFalse((temporary / 'tool-selection').exists())

    def test_one_newer_tool_keeps_the_other_reviewed_pin(self):
        temporary, copy = self.checkout()
        result, _, selected = self.select(temporary, copy, {'cli/cli': gh_release(GH_NEW),
                                                            'openai/codex': codex_release(CODEX)})
        self.assertEqual(selected.get('changed'), 'true', result.stderr)
        result, _, _ = self.apply(temporary, copy)
        self.assertEqual(result.returncode, 0, result.stderr)
        inventory = self.composed(copy)
        self.assertEqual((inventory['githubCli']['version'], inventory['agent']['version']), (GH_NEW, CODEX))
        for tool in ('codex', 'codex-code-mode-host'):
            artifact = inventory['artifacts'][tool]
            self.assertEqual((artifact['version'], artifact['url'], artifact['sha256']), CATALOG[tool])

    def test_unusable_newer_releases_fail_visibly_and_select_nothing(self):
        gh = gh_release(GH_NEW)
        linux = gh['assets'][1]
        cases = {
            'prerelease': {'openai/codex': codex_release(CODEX_NEW, prerelease=True)},
            'draft': {'cli/cli': gh_release(GH_NEW, draft=True)},
            'release candidate tag': {'cli/cli': gh_release(GH_NEW, tag_name=f'v{GH_NEW}-rc.1')},
            'no linux amd64 asset': {'cli/cli': gh_release(GH_NEW, assets=gh['assets'][:1])},
            'no published digest': {'cli/cli': gh_release(GH_NEW, assets=[{**linux, 'digest': None}])},
            'non-sha256 digest': {'cli/cli': gh_release(GH_NEW, assets=[{**linux, 'digest': 'md5:' + '1' * 32}])},
            'asset hosted elsewhere': {'cli/cli': gh_release(GH_NEW, assets=[
                {**linux, 'browser_download_url': linux['browser_download_url'].replace('cli/cli', 'someone/cli')}])},
            'codex without its code-mode host': {'openai/codex': codex_release(
                CODEX_NEW, assets=codex_release(CODEX_NEW)['assets'][:1])},
            'upstream unavailable': {'cli/cli': None},
        }
        for name, change in cases.items():
            with self.subTest(case=name):
                upstream = {'cli/cli': gh_release(GH), 'openai/codex': codex_release(CODEX)} | change
                temporary, copy = self.checkout()
                result, _, selected = self.select(temporary, copy, {k: v for k, v in upstream.items() if v})
                self.assertNotEqual(result.returncode, 0)
                self.assertNotEqual(selected.get('changed'), 'true')
                self.assertFalse((temporary / 'tool-selection').exists())

    def selected(self):
        temporary, copy = self.checkout()
        result, _, selected = self.select(temporary, copy, {'cli/cli': gh_release(GH_NEW),
                                                            'openai/codex': codex_release(CODEX_NEW)})
        self.assertEqual(selected.get('changed'), 'true', result.stderr)
        return temporary

    def test_verified_selection_is_pinned_on_unchanged_main_and_then_published(self):
        temporary = self.selected()

        result, calls, _ = self.commit(temporary)

        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual([call['argv'][:2] for call in calls], [['api', 'graphql'], ['workflow', 'run']])
        self.assertTrue(all(call['repo'] == 'repoagent/controller' and call['token'] for call in calls))
        request = json.loads(calls[0]['body'])
        self.assertIn('createCommitOnBranch', request['query'])
        change = request['variables']['input']
        self.assertEqual(change['branch'], {'repositoryNameWithOwner': 'repoagent/controller', 'branchName': 'main'})
        self.assertEqual(change['expectedHeadOid'], SHA, 'Only the verified main commit may receive the pin')
        self.assertEqual(set(change['fileChanges']), {'additions'})
        files = {item['path']: base64.b64decode(item['contents']) for item in change['fileChanges']['additions']}
        self.assertEqual(set(files), {'scripts/compose-image.py', 'image/default.json'})
        manifest = json.loads(files['image/default.json'])
        self.assertEqual((manifest['githubCli']['version'], manifest['agent']['version']), (GH_NEW, CODEX_NEW))
        (temporary / 'committed-catalog.py').write_bytes(files['scripts/compose-image.py'])
        catalog = runpy.run_path(str(temporary / 'committed-catalog.py'))['ARTIFACTS']
        self.assertEqual({tool: catalog[tool][::2] for tool in DIGESTS},
                         {'gh': (GH_NEW, DIGESTS['gh']), 'codex': (CODEX_NEW, DIGESTS['codex']),
                          'codex-code-mode-host': (CODEX_NEW, DIGESTS['codex-code-mode-host'])})
        self.assertEqual(catalog['cloudflared'], CATALOG['cloudflared'])
        self.assertIn(f'gh {GH_NEW}', change['message']['headline'])
        self.assertIn(f'Codex {CODEX_NEW}', change['message']['headline'])
        self.assertIn('https://github.com/repoagent/controller/actions/runs/4242', change['message']['body'])
        self.assertEqual(calls[1]['argv'], ['workflow', 'run', 'publish-controller-image.yml', '--ref', 'main'])

    def test_moved_main_or_another_commit_pins_and_publishes_nothing(self):
        for name, environment, attempted in [
                ('main moved after verification', dict(GH_COMMIT_REFUSED='1'), [['api', 'graphql']]),
                ('selection verified for another commit', dict(GITHUB_SHA='d' * 40), [])]:
            with self.subTest(case=name):
                result, calls, _ = self.commit(self.selected(), **environment)
                self.assertNotEqual(result.returncode, 0)
                self.assertEqual([call['argv'][:2] for call in calls], attempted)

    def test_failed_dispatch_after_pinning_names_the_manual_publication(self):
        result, calls, _ = self.commit(self.selected(), GH_DISPATCH_FAILS='1')
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual([call['argv'][:2] for call in calls], [['api', 'graphql'], ['workflow', 'run']])
        self.assertIn('gh workflow run publish-controller-image.yml --ref main', result.stderr)


def needs(name):
    match = re.search(r'^    needs: \[(.*)\]$', job(name), re.M) or re.search(r'^    needs: (\S+)$', job(name), re.M)
    return {item.strip() for item in match[1].split(',')} if match else set()


def runs(name, *, event='schedule', ref='refs/heads/main', repository='repoagent/controller', changed='true'):
    """Evaluate a job's if: expression for one context (the subset this workflow uses).

    Without a status function GitHub also requires every needed job to have succeeded.
    """
    condition = re.search(r'^    if: (.+)$', job(name), re.M)
    if condition is None:
        return True
    context = {'github.event_name': event, 'github.ref': ref, 'github.repository': repository,
               'needs.select.outputs.changed': changed}
    expression = re.sub(r'(?:github|needs)\.[a-z_.]+', lambda match: repr(context[match[0]]), condition[1])
    expression = expression.replace('&&', ' and ').replace('||', ' or ')
    if not re.fullmatch(r'(?:\s|\(|\)|==|!=|and|or)*', re.sub(r"'[^']*'", '', expression)):
        raise AssertionError('Unsupported workflow expression: ' + condition[1])
    return eval(expression, {'__builtins__': {}})


def steps(name):
    return re.findall(r'(?m)^      - name: (.+)$', job(name))


class RefreshWorkflowTests(unittest.TestCase):
    def test_a_scheduled_workflow_refreshes_the_baseline_from_upstream_tool_releases(self):
        refreshing = [workflow.name for workflow in sorted(WORKFLOWS.glob('*.yml'))
                      if 'schedule' in events(workflow) and 'publish-controller-image.yml' in workflow.read_text()]
        self.assertTrue(refreshing, 'No workflow checks upstream gh/Codex releases and feeds baseline publication')
        self.assertEqual(events(WORKFLOW), {'schedule', 'workflow_dispatch'},
                         'Upstream checks run on a cadence or on demand, never on code changes')
        self.assertRegex(on_block(WORKFLOW), r"cron: '\d+ \d+ \* \* \*'")

    def test_only_a_verified_selection_reaches_main_and_publication(self):
        self.assertTrue(runs('select', event='schedule'))
        self.assertTrue(runs('select', event='workflow_dispatch'))
        self.assertFalse(runs('select', event='workflow_dispatch', ref='refs/heads/feature'))
        self.assertFalse(runs('select', repository='someone/controller'))
        for name in ['verify', 'commit']:
            with self.subTest(job=name):
                self.assertTrue(runs(name))
                self.assertFalse(runs(name, changed='false'), 'No update selected means nothing to verify or pin')
        self.assertGreaterEqual(needs('commit'), {'select', 'verify'})
        self.assertNotRegex(job('commit').split('\n    steps:', 1)[0], r'always\(\)|failure\(\)|cancelled\(\)',
                            'A failed or cancelled verification must leave main and the published baseline unchanged')
        self.assertFalse(runs('commit', repository='someone/controller'))
        verification = steps('verify')
        self.assertEqual(verification[0], 'Pin the selected versions in this checkout',
                         'Verification must exercise the selected tools, not the current pins')
        self.assertIn('bash scripts/delivery/verify.sh', shell('verify', 'Verify the controller with the selected Codex'))
        smoke = shell('verify', 'Build and smoke the image with the selected tools')
        for command in ['scripts/compose-image.py', '"$IMAGE" versions', '/opt/repoagent/native-smoke/NativeSmoke',
                        'scripts/delivery/image-release.py" check']:
            with self.subTest(command=command):
                self.assertIn(command, smoke)

    def test_write_privileges_stay_in_the_main_only_commit_job(self):
        self.assertIn('\npermissions:\n  contents: read\n', WORKFLOW.read_text())
        for name in ['select', 'verify']:
            with self.subTest(job=name):
                self.assertNotIn('write', job(name))
                self.assertNotIn('environment:', job(name))
        commit = job('commit')
        self.assertIn('environment: controller-release', commit)
        self.assertEqual(set(re.findall(r'(?m)^      ([a-z-]+): write$', commit)), {'contents', 'actions'})
        self.assertNotIn('persist-credentials: true', WORKFLOW.read_text())
        self.assertNotIn('id-token', WORKFLOW.read_text())


if __name__ == '__main__':
    unittest.main()
