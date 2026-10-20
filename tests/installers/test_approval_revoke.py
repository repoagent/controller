"""Public PowerShell revoke orchestration; only the external gh executable is doubled.

Linux pwsh evidence does not establish native Windows or live GitHub compatibility.
"""
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
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
    return [{'body': {'id': 123}}, {'body': {'id': 456}},
            {'body': {'name': VARIABLE, 'value': value}}, {'status': 204}]


@unittest.skipUnless(os.name == 'posix' and shutil.which('pwsh'),
                     'POSIX executable fixture and PowerShell 7 required; native Windows unverified')
class ApprovalRevokeTests(unittest.TestCase):
    def run_revoke(self, *, arguments=(), scenario=None, apply=False, expected=None,
                   repository='example/project', script_root=ROOT, interactive_input=None):
        directory = Path(self.enterContext(tempfile.TemporaryDirectory(prefix='revoke boundary ')))
        gh = directory / 'gh'
        shutil.copyfile(Path(__file__).with_name('gh_fixture.py'), gh)
        gh.chmod(0o755)
        scenario_path = directory / 'scenario.json'
        scenario_path.write_text(json.dumps(scenario if scenario is not None else responses()))
        log = directory / 'requests.jsonl'
        remote = directory / 'remote-value'
        remote.write_text(VALUE)
        environment = dict(os.environ, PATH=str(directory) + os.pathsep + os.environ['PATH'],
                           REVOKE_GH_SCENARIO=str(scenario_path), REVOKE_GH_LOG=str(log),
                           REVOKE_REMOTE_VALUE=str(remote), CODEX_HOME=str(directory / 'absent-codex'))
        command = ['pwsh', '-NoLogo', '-NoProfile', '-File', str(script_root / 'install.ps1'),
                   '-Action', 'Revoke']
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
        result = subprocess.run(command, input=interactive_input or '', capture_output=True, text=True,
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
        events = [json.loads(line) for line in log.read_text().splitlines()] if log.exists() else []
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
