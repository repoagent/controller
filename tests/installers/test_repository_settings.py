"""Public PowerShell repository-settings administration; only the external gh executable is doubled.

The stateful gh model stands in for GitHub's direct repository Actions-variable API.
It proves operator orchestration, not live GitHub permissions or native Windows acceptance.
"""
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
SETTING = 'REPOAGENT_MAX_ACTIVE_SESSIONS'
VARIABLES = 'repos/example/project/actions/variables'


class RepositorySettingsTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        if not shutil.which('pwsh'):
            raise unittest.SkipTest('PowerShell 7 required')
        if os.name == 'nt':
            raise unittest.SkipTest('The executable gh model is POSIX-only; native Windows acceptance is separate')

    def run_settings(self, *arguments, variables=None, read_status=None, write_status=None, repository='example/project'):
        directory = Path(self.enterContext(tempfile.TemporaryDirectory(prefix='repository settings ')))
        gh = directory / 'gh'
        shutil.copyfile(Path(__file__).with_name('gh_fixture.py'), gh)
        gh.chmod(0o755)
        model = {'variables': {}, 'actions_variables': dict(variables or {})}
        if read_status:
            model['actions_status'] = read_status
        if write_status:
            model['actions_write_status'] = write_status
        self.model = directory / 'github.json'
        self.model.write_text(json.dumps(model), encoding='utf-8')
        self.log = directory / 'github.jsonl'
        environment = dict(os.environ, PATH=str(directory) + os.pathsep + os.environ['PATH'],
                           HOST_ENROLLMENT_GH_MODEL=str(self.model), REVOKE_GH_LOG=str(self.log), PYTHONUTF8='1')
        self.assertEqual(Path(shutil.which('gh', path=environment['PATH'])).resolve(), gh.resolve())
        command = ['pwsh', '-NoLogo', '-NoProfile', '-File', str(ROOT / 'install.ps1'), '-Action', 'Settings',
                   '-NonInteractive']
        if repository is not None:
            command += ['-Repository', repository]
        result = subprocess.run(command + list(arguments), capture_output=True, text=True, encoding='utf-8',
                                env=environment, cwd=directory, timeout=120, stdin=subprocess.DEVNULL)
        outcomes = [json.loads(line) for line in result.stdout.splitlines() if line.startswith('{')]
        return result, (outcomes[-1] if outcomes else None)

    def requests(self):
        if not self.log.exists():
            return []
        return [(entry['argv'][entry['argv'].index('--method') + 1], next(a for a in entry['argv'] if a.startswith('repos/')), entry['body'])
                for entry in map(json.loads, self.log.read_text(encoding='utf-8').splitlines())]

    def remote(self):
        return json.loads(self.model.read_text(encoding='utf-8'))['actions_variables']

    def test_inspection_reports_supported_settings_with_host_reads_only(self):
        result, outcome = self.run_settings(variables={SETTING: '4', 'UNRELATED': 'kept-private'})
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(outcome['settings'], [{'name': SETTING, 'state': 'configured', 'value': '4'}])
        self.assertEqual((outcome['changes'], outcome['applied']), ([], False))
        self.assertEqual({method for method, _, _ in self.requests()}, {'GET'})
        self.assertIn(('GET', VARIABLES + '/' + SETTING, None), self.requests())
        self.assertNotIn('kept-private', result.stdout + result.stderr)

    def test_an_invalid_remote_value_is_reported_without_echoing_it(self):
        result, outcome = self.run_settings(variables={SETTING: 'unlimited'})
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(outcome['settings'], [{'name': SETTING, 'state': 'invalid', 'reason': 'must be an integer from 1 to 64'}])
        self.assertNotIn('unlimited', result.stdout + result.stderr)

    def test_apply_creates_then_updates_with_the_controller_normalized_value(self):
        result, outcome = self.run_settings('-Setting', SETTING + '= 06 ', '-Apply')
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(outcome['changes'], [{'name': SETTING, 'action': 'create', 'value': '6'}])
        self.assertEqual(outcome['settings'], [{'name': SETTING, 'state': 'configured', 'value': '6'}])
        self.assertIn(('POST', VARIABLES, {'name': SETTING, 'value': '6'}), self.requests())
        self.assertEqual(self.remote(), {SETTING: '6'})

        result, outcome = self.run_settings('-Setting', SETTING.lower() + '=8', '-Apply', variables={SETTING: '6', 'OTHER': 'x'})
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(outcome['changes'], [{'name': SETTING, 'action': 'update', 'value': '8'}])
        self.assertIn(('PATCH', VARIABLES + '/' + SETTING, {'name': SETTING, 'value': '8'}), self.requests())
        self.assertEqual(self.remote(), {SETTING: '8', 'OTHER': 'x'})

    def test_preview_and_unchanged_values_never_write(self):
        for arguments, variables, action in [(('-Setting', SETTING + '=6'), {}, 'create'),
                                             (('-Setting', SETTING + '=6', '-Apply'), {SETTING: '6'}, 'unchanged'),
                                             (('-ClearSetting', SETTING, '-Apply'), {}, 'unchanged')]:
            with self.subTest(arguments=arguments):
                result, outcome = self.run_settings(*arguments, variables=variables)
                self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
                self.assertEqual([change['action'] for change in outcome['changes']], [action])
                self.assertEqual({method for method, _, _ in self.requests()}, {'GET'})
                self.assertEqual(self.remote(), variables)

    def test_clear_removes_only_the_selected_setting(self):
        result, outcome = self.run_settings('-ClearSetting', SETTING, '-Apply', variables={SETTING: '4', 'OTHER': 'x'})
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(outcome['settings'], [{'name': SETTING, 'state': 'absent'}])
        self.assertEqual([request for request in self.requests() if request[0] != 'GET'], [('DELETE', VARIABLES + '/' + SETTING, None)])
        self.assertEqual(self.remote(), {'OTHER': 'x'})

    def test_unsupported_names_and_invalid_values_are_rejected_before_github(self):
        for arguments in [('-Setting', SETTING + '=0'), ('-Setting', SETTING + '=65'), ('-Setting', SETTING + '=four'),
                          ('-Setting', 'REPOAGENT_UNKNOWN=1'), ('-Setting', 'GITHUB_TOKEN=x'), ('-Setting', SETTING),
                          ('-ClearSetting', 'APPROVED_CONTROLLER'), ('-Setting', SETTING + '=4', '-ClearSetting', SETTING)]:
            with self.subTest(arguments=arguments):
                result, outcome = self.run_settings(*arguments, '-Apply', variables={'APPROVED_CONTROLLER': 'marker'})
                self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
                self.assertIsNone(outcome)
                self.assertEqual(self.requests(), [])
                self.assertEqual(self.remote(), {'APPROVED_CONTROLLER': 'marker'})

    def test_a_failed_read_is_not_mistaken_for_absence(self):
        result, outcome = self.run_settings(read_status={SETTING: 502})
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(outcome['settings'], [{'name': SETTING, 'state': 'read-failed', 'reason': 'HTTP 502'}])

        result, outcome = self.run_settings('-Setting', SETTING + '=6', '-Apply', read_status={SETTING: 502})
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertIn('current value is unknown', result.stdout + result.stderr)
        self.assertEqual({method for method, _, _ in self.requests()}, {'GET'})
        self.assertEqual(self.remote(), {})

    def test_denied_operator_write_is_actionable_and_leaves_the_setting_unchanged(self):
        result, outcome = self.run_settings('-Setting', SETTING + '=6', '-Apply', variables={SETTING: '4'},
                                            write_status=403)
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertIsNone(outcome)
        self.assertIn('HTTP 403', result.stdout + result.stderr)
        self.assertIn('write access', result.stdout + result.stderr)
        self.assertEqual(self.remote(), {SETTING: '4'})


if __name__ == '__main__':
    unittest.main()
