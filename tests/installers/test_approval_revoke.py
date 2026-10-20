"""Public PowerShell revoke orchestration; only the external gh executable is doubled.

Runs on POSIX and native Windows; provider behavior is supplied by a gh double.
Native Windows acceptance requires an actual Windows run; live GitHub is separate.
"""
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import time
import unittest
from urllib.parse import quote

ROOT = Path(__file__).resolve().parents[2]
VALUE = 'APPROVAL-CONTENTS-MUST-NOT-BE-LOGGED: {"controller":"α"}\n'
VARIABLE = 'APPROVED_CONTROLLER'
REPO = 'repos/example/project'
ENVIRONMENT = REPO + '/environments/repoagent'
VARIABLE_PATH = ENVIRONMENT + '/variables/' + VARIABLE


def digest(value):
    return hashlib.sha256(value.encode('utf-8')).hexdigest()


def responses(value=VALUE):
    return [{'body': {'id': 123, 'full_name': 'example/project'}}, {'body': {'id': 456}},
            {'body': {'name': VARIABLE, 'value': value}}, {'status': 204}]


class ApprovalRevokeTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        if not shutil.which('pwsh'):
            if os.name == 'nt':
                raise RuntimeError('Native Windows acceptance requires PowerShell 7 on PATH')
            raise unittest.SkipTest('PowerShell 7 required')
        cls.native_fixture = os.name == 'nt' or os.environ.get('REVOKE_GH_NATIVE_FIXTURE') == '1'
        cls.fixture_build = None
        if cls.native_fixture:
            if not shutil.which('dotnet'):
                raise RuntimeError('The native gh test fixture requires the .NET 10 SDK')
            # Outside the repo: its global.json pins an unrelated .NET 11 SDK.
            temporary = tempfile.TemporaryDirectory(prefix='native gh fixture ')
            cls.addClassCleanup(temporary.cleanup)
            build = Path(temporary.name)
            shutil.copytree(Path(__file__).with_name('GhFixture'), build / 'project')
            (build / 'global.json').write_text(json.dumps({'sdk': {
                'version': '10.0.100', 'rollForward': 'latestFeature', 'allowPrerelease': False}}),
                encoding='utf-8')
            cls.fixture_build = build / 'output'
            result = subprocess.run(['dotnet', 'build', str(build / 'project/GhFixture.csproj'),
                                     '--output', str(cls.fixture_build), '--nologo',
                                     '--verbosity', 'quiet', '--ignore-failed-sources'],
                                    cwd=build, capture_output=True, text=True, encoding='utf-8', timeout=120)
            if result.returncode:
                raise RuntimeError('Native gh fixture build failed:\n' + result.stdout + result.stderr)

    def run_revoke(self, *, arguments=(), scenario=None, apply=False, expected=None,
                   repository='example/project', script_root=ROOT, interactive_input=None):
        directory = Path(self.enterContext(tempfile.TemporaryDirectory(prefix='revoke boundary ')))
        if self.native_fixture:
            for artifact in self.fixture_build.iterdir():
                shutil.copy2(artifact, directory / artifact.name)
        else:
            gh = directory / 'gh'
            shutil.copyfile(Path(__file__).with_name('gh_fixture.py'), gh)
            gh.chmod(0o755)
        scenario_path = directory / 'scenario.json'
        scenario_path.write_text(json.dumps(scenario if scenario is not None else responses()), encoding='utf-8')
        log = directory / 'requests.jsonl'
        remote = directory / 'remote-value'
        remote.write_text(VALUE, encoding='utf-8')
        environment = dict(os.environ, PATH=str(directory) + os.pathsep + os.environ['PATH'],
                           REVOKE_GH_SCENARIO=str(scenario_path), REVOKE_GH_LOG=str(log),
                           REVOKE_REMOTE_VALUE=str(remote), CODEX_HOME=str(directory / 'absent-codex'),
                           REVOKE_GH_PYTHON=sys.executable,
                           REVOKE_GH_FIXTURE=str(Path(__file__).with_name('gh_fixture.py').resolve()),
                           PYTHONUTF8='1')
        expected_gh = directory / ('gh.exe' if os.name == 'nt' else 'gh')
        self.assertEqual(Path(shutil.which('gh', path=environment['PATH']) or '').resolve(),
                         expected_gh.resolve(), 'Executable fixture must take precedence over host gh')
        command = ['pwsh', '-NoLogo', '-NoProfile', '-File', str(script_root / 'install.ps1'),
                   '-Action', 'Revoke']
        # These retained cases explicitly exercise legacy teardown. Agent variables
        # are the new default; do not infer an Environment from omitted inputs.
        if '-GitHubEnvironment' not in arguments:
            command += ['-GitHubEnvironment', 'repoagent']
        if interactive_input is None:
            command.append('-NonInteractive')
        if repository is not None:
            command += ['-Repository', repository]
        if apply:
            command.append('-Apply')
        if expected is not None:
            command += ['-ExpectedValueSha256', expected]
        command.extend(arguments)
        started = time.monotonic()
        result = subprocess.run(command, input=interactive_input or '', capture_output=True, text=True, encoding='utf-8',
                                env=environment, cwd=directory, timeout=65)
        elapsed = time.monotonic() - started
        output = result.stdout + result.stderr
        self.assertNotIn(VALUE.rstrip(), output)
        self.assertNotIn('PROVIDER-CREDENTIAL-MUST-NOT-APPEAR', output)
        outcomes = [json.loads(line) for line in result.stdout.splitlines() if line.startswith('{')]
        self.assertEqual(len(outcomes), 1, output)
        outcome = outcomes[0]
        self.assertEqual(outcome['brokerClosure'], 'unconfirmed')
        self.assertEqual(outcome['outstandingTokens'], 'unsettled')
        events = [json.loads(line) for line in log.read_text(encoding='utf-8').splitlines()] if log.exists() else []
        self.assertLessEqual(len(events), 4, events)
        self.assertTrue(all(event['argv'][0] == 'api' for event in events))
        methods = [event['argv'][event['argv'].index('--method') + 1] for event in events]
        self.assertTrue(all(method in ('GET', 'DELETE') for method in methods), events)
        self.assertLessEqual(methods.count('DELETE'), 1)
        return result, outcome, events, elapsed, remote

    def assert_requests(self, events, methods, variable_path=VARIABLE_PATH, environment_path=ENVIRONMENT):
        paths = [REPO, environment_path, variable_path, variable_path]
        expected = [['api', '--hostname', 'github.com', '--method', method, '--include',
                     '-H', 'Accept: application/vnd.github+json', paths[index]]
                    for index, method in enumerate(methods)]
        self.assertEqual([event['argv'] for event in events], expected)

    def test_preview_without_install_prerequisites_never_writes(self):
        result, outcome, events, _, _ = self.run_revoke()
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(outcome['githubMarker'], 'preview')
        self.assertEqual(outcome['observedValueSha256'], digest(VALUE))
        self.assertEqual((outcome['repositoryId'], outcome['environmentId']), (123, 456))
        self.assert_requests(events, ['GET'] * 3)

    def test_matching_apply_deletes_once_and_reports_only_acknowledged_removal(self):
        result, outcome, events, _, _ = self.run_revoke(apply=True, expected=digest(VALUE).upper(),
            arguments=['-ExpectedRepositoryId', '123', '-ExpectedEnvironmentId', '456'])
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(outcome['githubMarker'], 'removed')
        self.assertIn('acknowledged', outcome['recovery'])
        self.assertIn('persistent absence', outcome['recovery'])
        self.assert_requests(events, ['GET'] * 3 + ['DELETE'])

    def test_interactive_missing_repository_and_hash_are_answered_before_explicit_apply(self):
        result, outcome, events, _, _ = self.run_revoke(
            repository=None, apply=True, interactive_input='example/project\n' + digest(VALUE) + '\n')
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(outcome['githubMarker'], 'removed')
        self.assert_requests(events, ['GET'] * 3 + ['DELETE'])

    def test_supplied_fingerprint_does_not_enable_apply(self):
        result, outcome, events, _, _ = self.run_revoke(expected=digest(VALUE))
        self.assertEqual(result.returncode, 0)
        self.assertEqual(outcome['githubMarker'], 'preview')
        self.assert_requests(events, ['GET'] * 3)

    def test_exact_decoded_utf8_value_is_hashed_without_normalization(self):
        values = ['', ' leading and trailing ', 'line\r\n', 'e\u0301', 'é', '{ "a": 1 }', '{"a":1}',
                  '2026-10-20T10:11:12Z', '2026-10-20T10:11:12.123+02:00']
        for value in values:
            with self.subTest(value=repr(value)):
                result, outcome, events, _, _ = self.run_revoke(
                    scenario=responses(value), apply=True, expected=digest(value))
                self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
                self.assertEqual(outcome['observedValueSha256'], digest(value))
                self.assert_requests(events, ['GET'] * 3 + ['DELETE'])

    def test_changed_newline_whitespace_unicode_or_json_never_deletes(self):
        pairs = [('line', 'line\n'), (' x ', 'x'), ('é', 'e\u0301'), ('{"a":1}', '{ "a": 1 }')]
        for expected, observed in pairs:
            with self.subTest(observed=repr(observed)):
                result, outcome, events, _, _ = self.run_revoke(
                    scenario=responses(observed), apply=True, expected=digest(expected))
                self.assertNotEqual(result.returncode, 0)
                self.assertEqual(outcome['githubMarker'], 'conflict')
                self.assert_requests(events, ['GET'] * 3)

    def test_invalid_or_missing_inputs_fail_before_remote_calls(self):
        cases = [dict(repository=None), dict(repository='owner/../repo'),
                 dict(repository='https://evil.invalid/a'), dict(apply=True),
                 dict(expected='bad'), dict(arguments=['-ExpectedRepositoryId', '0']),
                 dict(arguments=['-ExpectedEnvironmentId', '-1']),
                 dict(arguments=['-ApprovalVariable', 'GITHUB_TOKEN']),
                 dict(arguments=['-ApprovalVariable', 'x/y']),
                 dict(arguments=['-GitHubEnvironment', 'a\nb'])]
        for case in cases:
            with self.subTest(case=case):
                result, outcome, events, _, _ = self.run_revoke(**case)
                self.assertNotEqual(result.returncode, 0)
                self.assertEqual(outcome['githubMarker'], 'unverifiable')
                self.assertEqual(events, [])

    def test_environment_special_characters_are_one_literal_encoded_path_segment(self):
        name = 'production /%?# $(touch PWNED); "é"'
        env_path = REPO + '/environments/' + quote(name, safe='')
        result, outcome, events, _, remote = self.run_revoke(
            arguments=['-GitHubEnvironment', name, '-ApprovalVariable', VARIABLE.lower()],
            apply=True, expected=digest(VALUE))
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(outcome['githubEnvironment'], name)
        self.assert_requests(events, ['GET'] * 3 + ['DELETE'], env_path + '/variables/' + VARIABLE, env_path)
        self.assertFalse((remote.parent / 'PWNED').exists())

    def test_immutable_identity_mismatch_stops_before_variable_read(self):
        for flag, count in [('-ExpectedRepositoryId', 1), ('-ExpectedEnvironmentId', 2)]:
            with self.subTest(flag=flag):
                result, outcome, events, _, _ = self.run_revoke(
                    arguments=[flag, '999'], apply=True, expected=digest(VALUE))
                self.assertNotEqual(result.returncode, 0)
                self.assertEqual(outcome['githubMarker'], 'unverifiable')
                self.assert_requests(events, ['GET'] * count)

    def test_denied_missing_or_malformed_reads_are_unverifiable(self):
        cases = [(0, {'status': 403}), (1, {'status': 404}), (2, {'status': 403}),
                 (2, {'status': 404}), (0, {'body': {'id': '123'}}),
                 (1, {'body': {'id': 0}}), (2, {'body': 'not json'}),
                 (0, {'body': [{'id': 123}]}), (1, {'body': [{'id': 456}]}),
                 (2, {'body': [{'name': VARIABLE, 'value': VALUE}]}),
                 (2, {'body': {'name': 'OTHER', 'value': VALUE}}),
                 (2, {'body': {'name': VARIABLE, 'value': 123}}),
                 (2, {'raw': 'not HTTP headers'}),
                 (2, {'body': {'name': VARIABLE, 'value': VALUE}, 'exit': 1})]
        for index, response in cases:
            with self.subTest(index=index, response=response):
                scenario = responses()
                scenario[index] = dict(response, stderr='PROVIDER-CREDENTIAL-MUST-NOT-APPEAR')
                result, outcome, events, _, _ = self.run_revoke(
                    scenario=scenario, apply=True, expected=digest(VALUE))
                self.assertNotEqual(result.returncode, 0)
                self.assertEqual(outcome['githubMarker'], 'unverifiable')
                self.assert_requests(events, ['GET'] * (index + 1))

    def test_delete_failure_or_lost_response_never_retries_or_reports_removal(self):
        for response in [{'status': 403}, {'status': 404}, {'status': 200},
                         {'raw': '', 'exit': 1}, {'status': 204, 'exit': 1}]:
            with self.subTest(response=response):
                scenario = responses()
                scenario[3] = response
                result, outcome, events, _, _ = self.run_revoke(
                    scenario=scenario, apply=True, expected=digest(VALUE))
                self.assertNotEqual(result.returncode, 0)
                self.assertEqual(outcome['githubMarker'], 'unverifiable')
                self.assert_requests(events, ['GET'] * 3 + ['DELETE'])

    def test_read_timeout_and_delete_timeout_terminate_children_with_unresolved_result(self):
        for index in (0, 3):
            with self.subTest(index=index):
                scenario = responses()
                scenario[index] = {'delay': 90}
                result, outcome, events, elapsed, _ = self.run_revoke(
                    scenario=scenario, apply=True, expected=digest(VALUE))
                self.assertNotEqual(result.returncode, 0)
                self.assertEqual(outcome['githubMarker'], 'unverifiable')
                self.assertIn('timed out', result.stdout + result.stderr)
                self.assertLess(elapsed, 22)
                self.assert_requests(events, ['GET'] * min(index + 1, 3) + (['DELETE'] if index == 3 else []))
                self.assert_process_terminated(events[-1]['pid'])

    def assert_process_terminated(self, pid):
        if os.name == 'nt':
            # Query/wait only: os.kill(pid, 0) can terminate processes on Windows.
            import ctypes
            from ctypes import wintypes
            kernel = ctypes.WinDLL('kernel32', use_last_error=True)
            kernel.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
            kernel.OpenProcess.restype = wintypes.HANDLE
            kernel.WaitForSingleObject.argtypes = [wintypes.HANDLE, wintypes.DWORD]
            kernel.WaitForSingleObject.restype = wintypes.DWORD
            kernel.CloseHandle.argtypes = [wintypes.HANDLE]
            kernel.CloseHandle.restype = wintypes.BOOL
            handle = kernel.OpenProcess(0x00100000, False, pid)  # SYNCHRONIZE, no terminate rights
            if not handle:
                self.assertEqual(ctypes.get_last_error(), 87, 'Could not verify fixture process exit')
                return  # ERROR_INVALID_PARAMETER: PID no longer exists
            try:
                self.assertEqual(kernel.WaitForSingleObject(handle, 5000), 0,
                                 'Fixture child did not exit after installer termination')
            finally:
                kernel.CloseHandle(handle)
        else:
            with self.assertRaises(ProcessLookupError):
                os.kill(pid, 0)

    def test_combined_stream_output_limit_aborts_before_child_finishes(self):
        for index in (0, 3):
            with self.subTest(index=index):
                scenario = responses()
                scenario[index] = {'stdout_bytes': 140000, 'stderr_bytes': 140000, 'after_output_delay': 90}
                result, outcome, events, elapsed, _ = self.run_revoke(
                    scenario=scenario, apply=True, expected=digest(VALUE))
                self.assertNotEqual(result.returncode, 0)
                self.assertEqual(outcome['githubMarker'], 'unverifiable')
                self.assertIn('256 KiB', result.stdout + result.stderr)
                self.assertLess(elapsed, 8)
                self.assert_process_terminated(events[-1]['pid'])
                self.assert_requests(events, ['GET'] * min(index + 1, 3) + (['DELETE'] if index == 3 else []))

    def test_independent_writer_race_accepts_manual_reapproval_without_repair_writes(self):
        scenario = responses()
        scenario[2]['replace_value'] = 'newer-approval'
        scenario[3]['delete_value'] = True
        result, outcome, events, _, remote = self.run_revoke(
            scenario=scenario, apply=True, expected=digest(VALUE))
        self.assertEqual(result.returncode, 0)
        self.assertFalse(remote.exists())
        self.assertEqual(outcome['githubMarker'], 'removed')
        self.assertIn('manually reapproving', outcome['recovery'])
        self.assertIn('concurrent', result.stdout.lower())
        self.assert_requests(events, ['GET'] * 3 + ['DELETE'])


if __name__ == '__main__':
    unittest.main()
