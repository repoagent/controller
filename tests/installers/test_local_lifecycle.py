"""Public dispatch/preview tests; filesystem lifecycle evidence uses real Docker."""
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import unittest

import test_private_runtime as runtime

ROOT = Path(__file__).resolve().parents[2]


class LocalLifecycleProcessTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        runtime.PrivateRuntimeTests.setUpClass.__func__(cls)

    def setUp(self):
        runtime.PrivateRuntimeTests.setUp(self)
        self.environment.pop('INSTALLER_DOCKER_LEGACY_ABSENT_MARKER', None)
        suffix = '.exe' if os.name == 'nt' else ''
        shutil.copy2(self.directory / ('docker' + suffix), self.directory / ('gh' + suffix))

    set_windows_private_acl = runtime.PrivateRuntimeTests.set_windows_private_acl
    assert_private_source_readable = runtime.PrivateRuntimeTests.assert_private_source_readable
    invoke = runtime.PrivateRuntimeTests.invoke
    state = runtime.PrivateRuntimeTests.state
    configure = runtime.PrivateRuntimeTests.configure

    def uninstall(self, *extra):
        return subprocess.run(['pwsh', '-NoLogo', '-NoProfile', '-File', str(ROOT / 'install.ps1'),
            '-Action', 'Uninstall', '-Instance', runtime.INSTANCE, '-StateDirectory', str(self.private / 'host'),
            '-Repository', 'owner/repository', '-ExpectedRepositoryId', '345', '-DeviceId', 'device-1',
            '-AppId', '123', '-InstallationId', '234', '-BrokerOrigin', runtime.BROKER, '-EnrollmentProfile', 'polling',
            '-ApprovalVariable', 'APPROVED_CONTROLLER', '-ExpectedValueSha256', 'b' * 64, '-NonInteractive', *extra],
            env=self.environment, text=True, capture_output=True, timeout=30)

    def assert_no_effects(self, before):
        after = self.state()
        self.assertEqual({key: value for key, value in after.items() if key != 'events'},
                         {key: value for key, value in before.items() if key != 'events'})
        added = after['events'][len(before['events']):]
        metadata = (['container', 'ls'], ['volume', 'ls'], ['container', 'inspect'], ['volume', 'inspect'], ['image', 'inspect'])
        self.assertTrue(all(event[:2] in metadata for event in added), added)

    def test_uninstall_preview_defers_volume_binding_and_has_no_effects(self):
        self.configure()
        before = self.state()
        result = self.uninstall()
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        outcome = [json.loads(line) for line in result.stdout.splitlines() if line.startswith('{')][0]
        self.assertEqual((outcome['action'], outcome['instance'], outcome['result']), ('Uninstall', runtime.INSTANCE, 'preview'))
        self.assertIn('unverified until Apply', result.stdout)
        self.assertIn(runtime.IMAGE, result.stdout)
        self.assertFalse((self.private / 'host').exists())
        self.assert_no_effects(before)

    def test_conflicting_saved_binding_refuses_before_effects(self):
        self.configure()
        host = self.private / 'host'
        host.mkdir(mode=0o700)
        marker = {'schemaVersion': 1, 'state': 'disabled', 'instance': runtime.INSTANCE, 'image': runtime.IMAGE,
                  'binding': {'repository': 'different/repository', 'repositoryId': 345, 'deviceId': 'device-1',
                              'appId': 123, 'installationId': 234, 'brokerOrigin': runtime.BROKER, 'profile': 'polling',
                              'approvalKind': 'repository-agent-variable', 'approvalVariable': 'APPROVED_CONTROLLER',
                              'expectedValueSha256': 'b' * 64, 'githubEnvironment': None, 'environmentId': None}}
        receipt = dict(schemaVersion=1, marker=marker, localLaunch='disabled', localRuntime='removed',
                       localCredentials='cleared', hostRecovery='absent', revoke=None, result='partial')
        path = host / 'lifecycle.json'
        path.write_text(json.dumps(receipt))
        path.chmod(0o600)
        if os.name == 'nt':
            self.set_windows_private_acl(host, directory=True)
            self.set_windows_private_acl(path)
        before, saved = self.state(), path.read_bytes()
        result = self.uninstall('-Apply')
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(path.read_bytes(), saved)
        self.assert_no_effects(before)

    def test_failed_or_malformed_engine_discovery_cannot_authorize_apply(self):
        for output in ('unexpected-name', ''):
            with self.subTest(output=output):
                self.environment['INSTALLER_DOCKER_DISCOVERY_OUTPUT'] = output
                self.environment['INSTALLER_DOCKER_DISCOVERY_EXIT'] = '0' if output else '73'
                result = self.uninstall('-Apply')
                self.assertNotEqual(result.returncode, 0)
                self.assertFalse((self.private / 'host').exists())
                self.assertFalse(any(event[0] == 'run' for event in self.state()['events']))

    @unittest.skipUnless(os.name == 'posix', 'Actual root Bash entrypoint requires POSIX')
    def test_bash_managed_import_and_reset_refuse_before_effects(self):
        self.configure()
        selected = self.state()
        selected['volumes'] = {key: value for key, value in selected['volumes'].items() if key.endswith('-enrollment')}
        self.state_path.write_text(json.dumps(selected))
        before = self.state()
        self.environment['INSTALLER_DOCKER_OWNER_OUTPUT'] = 'managed'
        for arguments in [('import-codex-auth', str(self.enrollment)), ('reset',)]:
            result = subprocess.run(['bash', str(ROOT / 'repoagent'), *arguments], env=self.environment,
                                    text=True, capture_output=True, timeout=30)
            self.assertNotEqual(result.returncode, 0)
            self.assertIn('repoagent.ps1', result.stdout + result.stderr)
            self.assert_no_effects(before)
        self.environment['INSTALLER_DOCKER_OWNER_OUTPUT'] = 'malformed'
        result = subprocess.run(['bash', str(ROOT / 'repoagent'), 'reset'], env=self.environment, capture_output=True, timeout=30)
        self.assertNotEqual(result.returncode, 0)
        self.assert_no_effects(before)

    @unittest.skipUnless(os.name == 'posix', 'Actual root Bash entrypoint requires POSIX')
    def test_unlabeled_direct_bash_configuration_remains_available(self):
        self.environment.pop('INSTALLER_DOCKER_REAL_CONFIGURE')
        self.environment['INSTALLER_DOCKER_OWNER_OUTPUT'] = 'direct'
        for arguments in [('configure', '--repository', 'owner/direct', '--app-id', '123', '--key-path', '/synthetic/key.pem'), ('get', 'repository')]:
            result = subprocess.run(['bash', str(ROOT / 'repoagent'), *arguments], env=self.environment,
                                    text=True, capture_output=True, timeout=30)
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(result.stdout.strip(), 'owner/direct')

    @unittest.skipUnless(os.name == 'posix', 'Credential-free executable isolation uses POSIX symlinks')
    def test_explicit_revoke_works_without_any_docker_executable(self):
        commands = self.directory / 'revoke-bin'
        commands.mkdir()
        (commands / 'python3').symlink_to(sys.executable)
        gh = commands / 'gh'
        shutil.copyfile(Path(__file__).with_name('gh_fixture.py'), gh)
        gh.chmod(0o755)
        scenario, log = self.directory / 'scenario.json', self.directory / 'gh.jsonl'
        value = 'synthetic-approval'
        scenario.write_text(json.dumps([{'body': {'id': 345, 'full_name': 'owner/repository'}},
                                       {'body': {'name': 'APPROVED_CONTROLLER', 'value': value}}, {'status': 204}]))
        environment = dict(self.environment, PATH=str(commands), REVOKE_GH_SCENARIO=str(scenario), REVOKE_GH_LOG=str(log))
        self.assertIsNone(shutil.which('docker', path=environment['PATH']))
        result = subprocess.run([shutil.which('pwsh'), '-NoProfile', '-File', str(ROOT / 'install.ps1'),
            '-Action', 'Revoke', '-Repository', 'owner/repository', '-ApprovalVariable', 'APPROVED_CONTROLLER',
            '-ExpectedRepositoryId', '345', '-ExpectedValueSha256', hashlib.sha256(value.encode()).hexdigest(),
            '-Apply', '-NonInteractive'], env=environment, capture_output=True, text=True, timeout=30)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        outcome = [json.loads(line) for line in result.stdout.splitlines() if line.startswith('{')][0]
        self.assertEqual((outcome['githubMarker'], outcome['brokerClosure'], outcome['outstandingTokens']), ('removed', 'unconfirmed', 'unsettled'))
        self.assertEqual([json.loads(line)['argv'][4] for line in log.read_text().splitlines()], ['GET', 'GET', 'DELETE'])
        self.assertEqual(json.loads(log.read_text().splitlines()[-1])['argv'][-1], 'repos/owner/repository/agents/variables/APPROVED_CONTROLLER')
