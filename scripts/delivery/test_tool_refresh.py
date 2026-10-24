"""Upstream gh/Codex discovery feeding verified baseline image publication.

Step tests execute the per-tool refresh workflow's actual shell steps against a
copy of this checkout, committed as its own git repository, with a gh double
that serves controlled upstream release metadata and records GitHub writes.
Workflow tests evaluate the shipped triggers, dependencies and permissions.
Neither proves a hosted Actions run, live GitHub availability or that a
particular upstream release is compatible.
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
PIPELINE = WORKFLOWS / 'refresh-baseline-tool.yml'
DEFAULT = json.loads((ROOT / 'image/default.json').read_text())
GH, CODEX = DEFAULT['githubCli']['version'], DEFAULT['agent']['version']
CATALOG = runpy.run_path(str(ROOT / 'scripts/compose-image.py'))['ARTIFACTS']
CANARY = 'ghs_TOKEN-MUST-NOT-APPEAR'
# Newer than anything this repository will pin for a long time, so the tests do not age.
GH_NEW, CODEX_NEW = '99.1.0', '99.2.0'
DIGESTS = {'gh': '1' * 64, 'codex': '2' * 64, 'codex-code-mode-host': '3' * 64}
URLS = {'gh': f'https://github.com/cli/cli/releases/download/v{GH_NEW}/gh_{GH_NEW}_linux_amd64.tar.gz',
        'codex': f'https://github.com/openai/codex/releases/download/rust-v{CODEX_NEW}/codex-x86_64-unknown-linux-musl.tar.gz',
        'codex-code-mode-host': f'https://github.com/openai/codex/releases/download/rust-v{CODEX_NEW}/codex-code-mode-host-x86_64-unknown-linux-musl.tar.gz'}
# Tool -> (upstream repository, its catalog artifacts, the newer version the tests offer, the current pin).
TOOLS = {'gh': ('cli/cli', ('gh',), GH_NEW, GH),
         'codex': ('openai/codex', ('codex', 'codex-code-mode-host'), CODEX_NEW, CODEX)}
# Identical trees and metadata give every checkout copy the same commit, as with one main commit.
GIT = dict(GIT_AUTHOR_NAME='fixture', GIT_AUTHOR_EMAIL='fixture@example.test', GIT_AUTHOR_DATE='2026-10-01T00:00:00Z',
           GIT_COMMITTER_NAME='fixture', GIT_COMMITTER_EMAIL='fixture@example.test',
           GIT_COMMITTER_DATE='2026-10-01T00:00:00Z', GIT_CONFIG_GLOBAL=os.devnull, GIT_CONFIG_NOSYSTEM='1')


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


def newer_upstream():
    return {'cli/cli': gh_release(GH_NEW), 'openai/codex': codex_release(CODEX_NEW)}


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


def job(name, workflow=PIPELINE):
    jobs = workflow.read_text().split('\njobs:\n', 1)[1]
    body = re.split(rf'(?m)^  {re.escape(name)}:\n', jobs, maxsplit=1)[1]
    return re.split(r'(?m)^  (?=[a-z-]+:\n)', body, maxsplit=1)[0]


def shell(job_name, step_name):
    """The actual run: script of one refresh workflow step."""
    step = re.split(r'(?m)^      - ', job(job_name).split(f'      - name: {step_name}\n', 1)[1], maxsplit=1)[0]
    inline = re.search(r'^        run: (?!\|)(.+)$', step, re.M)
    return inline[1] if inline else textwrap.dedent(step.split('        run: |\n', 1)[1])


def on_block(workflow):
    return workflow.read_text().split('\non:\n', 1)[1].split('\npermissions:', 1)[0]


def events(workflow):
    return {line.strip()[:-1] for line in on_block(workflow).splitlines() if re.fullmatch(r'  [a-z_]+:', line)}


def outputs(path):
    return dict(line.split('=', 1) for line in path.read_text().splitlines()) if path.exists() else {}


def git(copy, *arguments):
    return subprocess.run(['git', '-C', str(copy), *arguments], check=True, capture_output=True, text=True,
                          env=dict(os.environ, **GIT)).stdout.strip()


class RefreshStepTests(unittest.TestCase):
    def checkout(self, temporary=None, name='checkout', moved=False):
        """A fresh checkout of the files the refresh steps read and rewrite, at one main commit.

        moved adds a later commit, as a checkout of another main commit would have.
        """
        temporary = temporary or Path(self.enterContext(tempfile.TemporaryDirectory(prefix='tool refresh ')))
        copy = temporary / name
        files = (ROOT / 'image/source-files.txt').read_text().split() + ['image/default.json']
        files += [str(path.relative_to(ROOT)) for path in (ROOT / 'scripts/delivery').glob('*.py')]
        for relative in files:
            (copy / relative).parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(ROOT / relative, copy / relative)
        git(copy, 'init', '-q', '-b', 'main')
        git(copy, 'add', '-A')
        git(copy, 'commit', '-q', '-m', 'main')
        if moved:
            git(copy, 'commit', '-q', '--allow-empty', '-m', 'later main')
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
                   GH_REPO='repoagent/controller', GITHUB_SHA='d' * 40, GITHUB_REPOSITORY='repoagent/controller',
                   GITHUB_SERVER_URL='https://github.com', GITHUB_RUN_ID='4242', GIT_CONFIG_GLOBAL=os.devnull,
                   GIT_CONFIG_NOSYSTEM='1')
        env.update(environment)
        result = subprocess.run(['bash', '-e', '-c', shell(job_name, step_name)], cwd=copy, env=env,
                                capture_output=True, text=True, timeout=60)
        calls = [json.loads(line) for line in ledger.read_text().splitlines()] if ledger.exists() else []
        self.assertNotIn(CANARY, result.stdout + result.stderr + json.dumps(calls))
        return result, calls, outputs(output)

    def select(self, temporary, copy, releases, tool):
        # TOOL carries the workflow input naming the tool this refresh is for.
        return self.run_step('select', 'Select a supported upstream release of the tool', temporary, copy, releases,
                             TOOL=tool)

    def apply(self, temporary, copy):
        return self.run_step('verify', 'Pin the selected version in this checkout', temporary, copy, {})

    def commit(self, temporary, moved=False, **environment):
        # The commit job checks out the selection's base again and downloads the selection artifact.
        _, copy = self.checkout(temporary, 'commit-checkout', moved=moved)
        return self.run_step('commit', 'Pin the verified version on main and publish a baseline',
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

    def assert_pinned_alone(self, copy, tool):
        """The checkout pins tool's newer release and keeps every other reviewed pin exactly."""
        inventory = self.composed(copy)
        _, artifacts, version, _ = TOOLS[tool]
        self.assertEqual((inventory['githubCli']['version'], inventory['agent']['version']),
                         (GH_NEW if tool == 'gh' else GH, CODEX_NEW if tool == 'codex' else CODEX))
        catalog = runpy.run_path(str(copy / 'scripts/compose-image.py'))['ARTIFACTS']
        for artifact, reviewed in CATALOG.items():
            with self.subTest(artifact=artifact):
                expected = (version, URLS[artifact], DIGESTS[artifact]) if artifact in artifacts else reviewed
                self.assertEqual(inventory['artifacts'][artifact], dict(zip(('version', 'url', 'sha256'), expected)))
                # verify.sh downloads the native-test Codex from this same catalog.
                self.assertEqual(catalog[artifact], expected)
        # The Azure planner reads catalog lines directly (scripts/azure/image-input.ps1).
        text = (copy / 'scripts/compose-image.py').read_text()
        for artifact in ('gh', 'codex', 'cloudflared'):
            self.assertEqual(re.search(rf"(?m)^    '{artifact}': \('([^']+)',", text)[1], catalog[artifact][0])

    def test_a_newer_supported_release_is_selected_and_pinned_for_verification_of_that_tool_alone(self):
        for tool, (repository, _, version, current) in TOOLS.items():
            with self.subTest(tool=tool):
                temporary, copy = self.checkout()

                result, calls, selected = self.select(temporary, copy, newer_upstream(), tool)

                self.assertEqual(result.returncode, 0, result.stderr)
                self.assertEqual(selected.get('changed'), 'true')
                self.assertEqual(selected.get('base'), git(copy, 'rev-parse', 'HEAD'),
                                 'Verification and the pin use the commit that was selected from')
                self.assertEqual([call['argv'][1] for call in calls], [f'repos/{repository}/releases/latest'],
                                 'Each refresh reads only its own tool upstream')
                self.assertTrue(all(call['token'] for call in calls), 'Discovery uses the workflow token, not anonymous limits')
                self.assertIn(f'{current} -> {version}', result.stdout)
                self.assertEqual((copy / 'image/default.json').read_text(), (ROOT / 'image/default.json').read_text(),
                                 'Selection alone changes nothing; verification pins it in its own checkout')

                result, calls, _ = self.apply(temporary, copy)

                self.assertEqual(result.returncode, 0, result.stderr)
                self.assertEqual(calls, [])
                self.assert_pinned_alone(copy, tool)

    def test_current_or_older_upstream_releases_select_nothing(self):
        for name, upstream in {
                'current': {'cli/cli': gh_release(GH), 'openai/codex': codex_release(CODEX)},
                'older is never a downgrade': {'cli/cli': gh_release('1.0.0'), 'openai/codex': codex_release('0.1.0')},
        }.items():
            for tool in TOOLS:
                with self.subTest(case=name, tool=tool):
                    temporary, copy = self.checkout()
                    result, _, selected = self.select(temporary, copy, upstream, tool)
                    self.assertEqual(result.returncode, 0, result.stderr)
                    self.assertEqual(selected.get('changed'), 'false')
                    self.assertFalse((temporary / 'tool-selection').exists())

    def test_unusable_newer_releases_fail_visibly_and_select_nothing(self):
        gh = gh_release(GH_NEW)
        linux = gh['assets'][1]
        cases = {
            'prerelease': ('codex', {'openai/codex': codex_release(CODEX_NEW, prerelease=True)}),
            'draft': ('gh', {'cli/cli': gh_release(GH_NEW, draft=True)}),
            'release candidate tag': ('gh', {'cli/cli': gh_release(GH_NEW, tag_name=f'v{GH_NEW}-rc.1')}),
            'no linux amd64 asset': ('gh', {'cli/cli': gh_release(GH_NEW, assets=gh['assets'][:1])}),
            'no published digest': ('gh', {'cli/cli': gh_release(GH_NEW, assets=[{**linux, 'digest': None}])}),
            'non-sha256 digest': ('gh', {'cli/cli': gh_release(GH_NEW, assets=[{**linux, 'digest': 'md5:' + '1' * 32}])}),
            'asset hosted elsewhere': ('gh', {'cli/cli': gh_release(GH_NEW, assets=[
                {**linux, 'browser_download_url': linux['browser_download_url'].replace('cli/cli', 'someone/cli')}])}),
            'codex without its code-mode host': ('codex', {'openai/codex': codex_release(
                CODEX_NEW, assets=codex_release(CODEX_NEW)['assets'][:1])}),
            'upstream unavailable': ('gh', {'cli/cli': None}),
        }
        for name, (tool, change) in cases.items():
            with self.subTest(case=name):
                upstream = {'cli/cli': gh_release(GH), 'openai/codex': codex_release(CODEX)} | change
                temporary, copy = self.checkout()
                result, _, selected = self.select(temporary, copy, {k: v for k, v in upstream.items() if v}, tool)
                self.assertNotEqual(result.returncode, 0)
                self.assertNotEqual(selected.get('changed'), 'true')
                self.assertFalse((temporary / 'tool-selection').exists())

    def test_an_unusable_release_of_one_tool_never_blocks_the_others_update(self):
        for tool, other in (('gh', 'openai/codex'), ('codex', 'cli/cli')):
            with self.subTest(tool=tool):
                upstream = newer_upstream()
                upstream[other]['draft'] = True
                temporary, copy = self.checkout()
                result, _, selected = self.select(temporary, copy, upstream, tool)
                self.assertEqual(result.returncode, 0, result.stderr)
                self.assertEqual(selected.get('changed'), 'true')
                result, _, _ = self.apply(temporary, copy)
                self.assertEqual(result.returncode, 0, result.stderr)
                self.assert_pinned_alone(copy, tool)
                result, calls, _ = self.commit(temporary)
                self.assertEqual(result.returncode, 0, result.stderr)
                self.assertEqual([call['argv'][:2] for call in calls], [['api', 'graphql'], ['workflow', 'run']])

    def selected(self, tool='codex'):
        temporary, copy = self.checkout()
        result, _, selected = self.select(temporary, copy, newer_upstream(), tool)
        self.assertEqual(selected.get('changed'), 'true', result.stderr)
        return temporary, selected['base']

    def test_verified_selection_is_pinned_on_unchanged_main_and_then_published(self):
        for tool, (_, artifacts, version, current) in TOOLS.items():
            with self.subTest(tool=tool):
                temporary, base = self.selected(tool)

                result, calls, _ = self.commit(temporary)

                self.assertEqual(result.returncode, 0, result.stderr)
                self.assertEqual([call['argv'][:2] for call in calls], [['api', 'graphql'], ['workflow', 'run']])
                self.assertTrue(all(call['repo'] == 'repoagent/controller' and call['token'] for call in calls))
                request = json.loads(calls[0]['body'])
                self.assertIn('createCommitOnBranch', request['query'])
                change = request['variables']['input']
                self.assertEqual(change['branch'], {'repositoryNameWithOwner': 'repoagent/controller', 'branchName': 'main'})
                self.assertEqual(change.get('expectedHeadOid'), base, 'Only the verified main commit may receive the pin')
                self.assertEqual(set(change['fileChanges']), {'additions'})
                files = {item['path']: base64.b64decode(item['contents']) for item in change['fileChanges']['additions']}
                self.assertEqual(set(files), {'scripts/compose-image.py', 'image/default.json'})
                manifest = json.loads(files['image/default.json'])
                self.assertEqual((manifest['githubCli']['version'], manifest['agent']['version']),
                                 (GH_NEW if tool == 'gh' else GH, CODEX_NEW if tool == 'codex' else CODEX))
                (temporary / 'committed-catalog.py').write_bytes(files['scripts/compose-image.py'])
                catalog = runpy.run_path(str(temporary / 'committed-catalog.py'))['ARTIFACTS']
                self.assertEqual(catalog, CATALOG | {artifact: (version, URLS[artifact], DIGESTS[artifact])
                                                     for artifact in artifacts},
                                 'Only the verified tool changes; every other pin stays as reviewed')
                label = 'gh' if tool == 'gh' else 'Codex'
                self.assertEqual(change['message']['headline'], f'chore: refresh baseline {label} to {version}')
                self.assertIn(f'{label} {current} -> {version}', change['message']['body'])
                self.assertIn('https://github.com/repoagent/controller/actions/runs/4242', change['message']['body'])
                self.assertEqual(calls[1]['argv'], ['workflow', 'run', 'publish-controller-image.yml', '--ref', 'main'])

    def test_moved_main_or_another_commit_pins_and_publishes_nothing(self):
        for name, moved, environment, attempted in [
                ('main moved after verification', False, dict(GH_COMMIT_REFUSED='1'), [['api', 'graphql']]),
                ('selection verified for another commit', True, {}, [])]:
            with self.subTest(case=name):
                temporary, _ = self.selected()
                result, calls, _ = self.commit(temporary, moved=moved, **environment)
                self.assertNotEqual(result.returncode, 0)
                self.assertEqual([call['argv'][:2] for call in calls], attempted)

    def test_verification_refuses_a_checkout_of_another_commit(self):
        temporary, _ = self.selected()
        _, copy = self.checkout(temporary, 'verify-checkout', moved=True)
        result, _, _ = self.apply(temporary, copy)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('not the commit the selection was made from', result.stderr)
        self.assertEqual((copy / 'image/default.json').read_text(), (ROOT / 'image/default.json').read_text())

    def test_failed_dispatch_after_pinning_names_the_manual_publication(self):
        temporary, _ = self.selected()
        result, calls, _ = self.commit(temporary, GH_DISPATCH_FAILS='1')
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual([call['argv'][:2] for call in calls], [['api', 'graphql'], ['workflow', 'run']])
        self.assertIn('gh workflow run publish-controller-image.yml --ref main', result.stderr)


def needs(name, workflow=PIPELINE):
    match = re.search(r'^    needs: \[(.*)\]$', job(name, workflow), re.M) or re.search(r'^    needs: (\S+)$', job(name, workflow), re.M)
    return {item.strip() for item in match[1].split(',')} if match else set()


def runs(name, *, workflow=PIPELINE, event='schedule', ref='refs/heads/main', repository='repoagent/controller', changed='true'):
    """Evaluate a job's if: expression for one context (the subset this workflow uses).

    Without a status function GitHub also requires every needed job to have succeeded.
    """
    condition = re.search(r'^    if: (.+)$', job(name, workflow), re.M)
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


def checkout_ref(name):
    step = job(name).split('      - uses: actions/checkout@', 1)[1].split('\n      - ', 1)[0]
    match = re.search(r'(?m)^          ref: (.+)$', step)
    return match[1] if match else None


class RefreshWorkflowTests(unittest.TestCase):
    def test_a_scheduled_workflow_refreshes_the_baseline_from_upstream_tool_releases(self):
        refreshing = [workflow.name for workflow in sorted(WORKFLOWS.glob('*.yml')) if 'schedule' in events(workflow)
                      and any('publish-controller-image.yml' in (WORKFLOWS / called).read_text()
                              for called in re.findall(r'(?m)^    uses: \./\.github/workflows/(\S+)$', workflow.read_text()))]
        self.assertTrue(refreshing, 'No workflow checks upstream gh/Codex releases and feeds baseline publication')
        self.assertEqual(events(WORKFLOW), {'schedule', 'workflow_dispatch'},
                         'Upstream checks run on a cadence or on demand, never on code changes')
        self.assertRegex(on_block(WORKFLOW), r"cron: '\d+ \d+ \* \* \*'")
        self.assertEqual(events(PIPELINE), {'workflow_call'}, 'A tool refresh runs only as part of the scheduled refresh')

    def test_each_tool_is_selected_verified_and_pinned_by_its_own_refresh(self):
        refresh = job('refresh', WORKFLOW)
        self.assertRegex(refresh, r'(?m)^    uses: \./\.github/workflows/refresh-baseline-tool\.yml$',
                         'gh and Codex share one selection, verification and pin, so one bad release blocks the other')
        self.assertIn('      tool: ${{ matrix.tool }}\n', refresh)
        tools = re.search(r'(?m)^        tool: \[(.*)\]$', refresh)[1]
        upstream = runpy.run_path(str(ROOT / 'scripts/delivery/tool-refresh.py'))['UPSTREAM']
        self.assertEqual({item.strip() for item in tools.split(',')}, set(upstream))
        self.assertIn('      fail-fast: false\n', refresh, "One tool's failed refresh must not cancel the other's")
        self.assertIn('      max-parallel: 1\n', refresh,
                      'Concurrent refreshes would both pin on the same main and the later pin would be refused')
        self.assertEqual(checkout_ref('select'), 'main', 'Each refresh selects from the then-current main')
        for name in ('verify', 'commit'):
            with self.subTest(job=name):
                self.assertEqual(checkout_ref(name), '${{ needs.select.outputs.base }}',
                                 'Verification and the pin use exactly the commit that was selected from')
        self.assertIn('${{ inputs.tool }}', job('select').split('      - name: Select a supported upstream release of the tool\n', 1)[1])
        self.assertIn('tool-selection-${{ inputs.tool }}-', job('select'), 'Each tool keeps its own selection artifact')

    def test_only_a_verified_selection_reaches_main_and_publication(self):
        for workflow, name in ((WORKFLOW, 'refresh'), (PIPELINE, 'select')):
            with self.subTest(job=name):
                self.assertTrue(runs(name, workflow=workflow, event='schedule'))
                self.assertTrue(runs(name, workflow=workflow, event='workflow_dispatch'))
                self.assertFalse(runs(name, workflow=workflow, event='workflow_dispatch', ref='refs/heads/feature'))
                self.assertFalse(runs(name, workflow=workflow, repository='someone/controller'))
        for name in ['verify', 'commit']:
            with self.subTest(job=name):
                self.assertTrue(runs(name))
                self.assertFalse(runs(name, changed='false'), 'No update selected means nothing to verify or pin')
        self.assertGreaterEqual(needs('commit'), {'select', 'verify'})
        self.assertNotRegex(job('commit').split('\n    steps:', 1)[0], r'always\(\)|failure\(\)|cancelled\(\)',
                            'A failed or cancelled verification must leave main and the published baseline unchanged')
        self.assertFalse(runs('commit', repository='someone/controller'))
        verification = steps('verify')
        self.assertEqual(verification[0], 'Pin the selected version in this checkout',
                         'Verification must exercise the selected tools, not the current pins')
        self.assertIn('bash scripts/delivery/verify.sh', shell('verify', 'Verify the controller with the selected tools'))
        smoke = shell('verify', 'Build and smoke the image with the selected tools')
        for command in ['scripts/compose-image.py', '"$IMAGE" versions', '/opt/repoagent/native-smoke/NativeSmoke',
                        'scripts/delivery/image-release.py" check']:
            with self.subTest(command=command):
                self.assertIn(command, smoke)

    def test_write_privileges_stay_in_the_main_only_commit_job(self):
        for workflow in (WORKFLOW, PIPELINE):
            with self.subTest(workflow=workflow.name):
                self.assertIn('\npermissions:\n  contents: read\n', workflow.read_text())
                self.assertNotIn('persist-credentials: true', workflow.read_text())
                self.assertNotIn('id-token', workflow.read_text())
        # The scheduled refresh grants only the ceiling the called commit job needs.
        self.assertEqual(set(re.findall(r'(?m)^      ([a-z-]+): write$', job('refresh', WORKFLOW))), {'contents', 'actions'})
        for name in ['select', 'verify']:
            with self.subTest(job=name):
                self.assertNotIn('write', job(name))
                self.assertNotIn('environment:', job(name))
        commit = job('commit')
        self.assertIn('environment: controller-release', commit)
        self.assertEqual(set(re.findall(r'(?m)^      ([a-z-]+): write$', commit)), {'contents', 'actions'})


if __name__ == '__main__':
    unittest.main()
