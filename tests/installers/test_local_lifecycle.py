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

    def test_update_preview_is_immutable_and_read_only(self):
        self.configure()
        host = self.private / 'host'
        host.mkdir(mode=0o700)
        approved = json.dumps(dict(deviceId='device-1', appId=123, installationId=234))
        approval = dict(version=2, setupVersion=2, approvalKind='repository-agent-variable', repository='owner/repository',
                        repositoryId=345, brokerOrigin=runtime.BROKER, approvalVariable='APPROVED_CONTROLLER', profile='polling',
                        pendingId=None, deadline=None, marker=approved, expectedValueSha256=hashlib.sha256(approved.encode()).hexdigest(),
                        approvalWriteAttempted=True, approvalObserved=True)
        for name, value in [('approval.json', approval), ('launch.json', dict(instance=runtime.INSTANCE, runtimeImageId=runtime.IMAGE, repository='owner/repository'))]:
            path = host / name
            path.write_text(json.dumps(value)); path.chmod(0o600)
            if os.name == 'nt':
                self.set_windows_private_acl(host, directory=True); self.set_windows_private_acl(path)
        before = self.state()
        for selectors, valid in [(('-Image', 'sha256:' + 'b' * 64), True), (('-Image', 'repoagent:latest'), False),
                                 (('-Image', runtime.IMAGE, '-Rollback'), False)]:
            result = subprocess.run(['pwsh', '-NoProfile', '-File', str(ROOT / 'install.ps1'), '-Action', 'Update',
                                     '-Instance', runtime.INSTANCE, '-StateDirectory', str(host), '-NonInteractive', *selectors],
                                    env=self.environment, capture_output=True, text=True, timeout=30)
            self.assertEqual(result.returncode == 0, valid, result.stdout + result.stderr)
            if valid:
                self.assertIn('deferred until Apply', result.stdout)
                self.assertIn('device-1', result.stdout)
            self.assert_no_effects(before)

    def test_uninstall_preview_defers_volume_binding_and_has_no_effects(self):
        self.configure()
        before = self.state()
        for extra in ((), ('-GitHubEnvironment', 'production-west', '-ExpectedEnvironmentId', '456')):
            with self.subTest(legacy=bool(extra)):
                result = self.uninstall(*extra)
                self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
                outcome = [json.loads(line) for line in result.stdout.splitlines() if line.startswith('{')][0]
                self.assertEqual((outcome['action'], outcome['instance'], outcome['result']), ('Uninstall', runtime.INSTANCE, 'preview'))
                self.assertIn('unverified until Apply', result.stdout)
                self.assertIn(runtime.IMAGE, result.stdout)
                self.assertFalse((self.private / 'host').exists())
                self.assert_no_effects(before)
                revoke = outcome['revoke']
                if extra:
                    self.assertEqual((revoke['approvalKind'], revoke.get('githubEnvironment'), revoke.get('environmentId')),
                                     ('environment-variable', 'production-west', 456), result.stdout)
                    readable = '\n'.join(line for line in result.stdout.splitlines() if not line.startswith('{'))
                    self.assertIn('production-west', readable)
                    self.assertIn('456', readable)
                else:
                    self.assertEqual(revoke['approvalKind'], 'repository-agent-variable')
                    self.assertNotIn('githubEnvironment', revoke)
                    self.assertNotIn('environmentId', revoke)

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


def assert_interrupted_image_recovery(test, cut):
    """Actual public processes and Docker state, with transport interruption only."""
    host = test.prepare_update()
    transport = test.directory / 'docker'
    shutil.copyfile(Path(__file__).with_name('docker_fixture.py'), transport)
    transport.chmod(0o755)
    test.environment['REPOAGENT_LIFECYCLE_REAL_DOCKER'] = shutil.which('docker')
    test.environment['REPOAGENT_LIFECYCLE_CUT'] = cut
    failed = test.update('-Image', test.target, '-Apply')
    test.assertNotEqual(failed.returncode, 0, failed.stdout + failed.stderr)
    state = json.loads(test.files("from pathlib import Path; print(Path('/data/enrollment/launch-state.json').read_text())"))
    test.assertEqual((state['pending'], state['priorImage']), ('update', test.image))
    observed = test.docker('container', 'inspect', test.name, success=False)
    test.assertEqual(observed.returncode == 0, cut != 'create')
    if cut == 'complete':
        test.assertEqual(json.loads((host / 'launch.json').read_text())['launchState']['pending'], 'none')
    test.environment['REPOAGENT_LIFECYCLE_CUT'] = ''
    refused = test.public('start')
    test.assertNotEqual(refused.returncode, 0, refused.stdout + refused.stderr)
    test.environment['REPOAGENT_STATE_DIRECTORY'] = str(test.directory / 'alternate-state')
    test.assertNotEqual(test.public('start').returncode, 0)
    test.environment['REPOAGENT_STATE_DIRECTORY'] = str(host)
    stopped = test.public('stop')
    test.assertEqual(stopped.returncode, 0, stopped.stdout + stopped.stderr)
    if cut == 'complete':
        (host / 'launch.json').write_text('{corrupt')
    test.environment['REPOAGENT_LIFECYCLE_CUT'] = 'create'
    test.assertNotEqual(test.update('-Rollback', '-Apply').returncode, 0)
    pending = json.loads(test.files("from pathlib import Path; print(Path('/data/enrollment/launch-state.json').read_text())"))
    test.assertEqual((pending['pending'], pending['priorImage'], pending['targetImage']), ('rollback', test.image, test.image))
    test.environment['REPOAGENT_LIFECYCLE_CUT'] = ''
    recovered = test.update('-Rollback', '-Apply')
    test.assertEqual(recovered.returncode, 0, recovered.stdout + recovered.stderr)
    test.assertEqual(json.loads(test.docker('container', 'inspect', test.name).stdout)[0]['Image'], test.image)
    test.assertEqual(test.public('start').returncode, 0)


def assert_review_recovery_regressions(test):
    host = test.prepare_update()
    transport = test.directory / 'docker'
    shutil.copyfile(Path(__file__).with_name('docker_fixture.py'), transport); transport.chmod(0o755)
    test.environment.update(REPOAGENT_LIFECYCLE_REAL_DOCKER=shutil.which('docker'), REPOAGENT_LIFECYCLE_CUT='stop')
    test.assertNotEqual(test.update('-Image', test.target, '-Apply').returncode, 0)
    record = json.loads(test.files("from pathlib import Path; print(Path('/data/enrollment/launch-state.json').read_text())"))
    test.environment['REPOAGENT_LIFECYCLE_CUT'] = ''
    with test.subTest(finding='P001-missing-authority'):
        try:
            test.files("from pathlib import Path; Path('/data/enrollment/launch-state.json').unlink()")
            test.assertNotEqual(test.public('start').returncode, 0)
            test.environment['REPOAGENT_STATE_DIRECTORY'] = str(test.directory / 'alternate-host')
            test.assertNotEqual(test.public('start').returncode, 0)
        finally:
            test.environment['REPOAGENT_STATE_DIRECTORY'] = str(host)
            test.write_launch_state(record)
    restored = test.update('-Rollback', '-Apply')
    test.assertEqual(restored.returncode, 0, restored.stdout + restored.stderr)
    updated = test.update('-Image', test.target, '-Apply')
    test.assertEqual(updated.returncode, 0, updated.stdout + updated.stderr)
    with test.subTest(finding='P002-transitioned-preview'):
        preview = test.update('-Rollback')
        test.assertEqual(preview.returncode, 0, preview.stdout + preview.stderr)
        test.assertEqual(test.uninstall(apply=False)[0].returncode, 0)
    test.assertEqual(test.update('-Rollback', '-Apply').returncode, 0)
    test.environment['REPOAGENT_LIFECYCLE_CUT'] = 'started'
    test.assertNotEqual(test.update('-Image', test.target, '-Apply').returncode, 0)
    test.environment.update(REPOAGENT_LIFECYCLE_CUT='', REPOAGENT_LIFECYCLE_MISSING_IMAGE=test.image)
    with test.subTest(finding='P004-safe-stop-without-write-helper'):
        try:
            stopped = test.public('stop')
            test.assertEqual(stopped.returncode, 0, stopped.stdout + stopped.stderr)
            test.assertFalse(json.loads(test.docker('container', 'inspect', test.name).stdout)[0]['State']['Running'])
        finally:
            test.environment.pop('REPOAGENT_LIFECYCLE_MISSING_IMAGE')
    test.assertEqual(test.update('-Rollback', '-Apply').returncode, 0)
    for selection in [('-Image', test.target), ('-Rollback',)]:
        with test.subTest(finding='GH-4343535481-missing-enrollment', selection=selection):
            before = (host / 'launch.json').read_bytes()
            authority = test.files("from pathlib import Path; print(Path('/data/enrollment/launch-state.json').read_text())")
            selected = json.loads(test.docker('container', 'inspect', test.name).stdout)[0]['Id']
            credential = test.files("from pathlib import Path; print(Path('/data/enrollment/enrollment.json').read_text())")
            try:
                test.files("from pathlib import Path; Path('/data/enrollment/enrollment.json').unlink()")
                refused = test.update(*selection, '-Apply')
                test.assertNotEqual(refused.returncode, 0, refused.stdout + refused.stderr)
                observed = json.loads(test.docker('container', 'inspect', test.name).stdout)[0]
                test.assertEqual((observed['Id'], observed['State']['Running']), (selected, True))
                test.assertEqual((host / 'launch.json').read_bytes(), before)
                test.assertEqual(test.files("from pathlib import Path; print(Path('/data/enrollment/launch-state.json').read_text())"), authority)
            finally:
                test.files("from pathlib import Path; Path('/data/enrollment/enrollment.json').write_text(" + repr(credential) + ")")
    with test.subTest(finding='P003-corrupt-mirror-uninstall'):
        approved = json.loads((host / 'approval.json').read_text())['marker']
        scenario, log = test.directory / 'scenario.json', test.directory / 'gh.jsonl'
        scenario.write_text(json.dumps([{'body': {'id': 345, 'full_name': 'owner/repository'}},
                                       {'body': {'name': 'APPROVED_CONTROLLER', 'value': approved}}, {'status': 403}]))
        test.environment.update(REVOKE_GH_SCENARIO=str(scenario), REVOKE_GH_LOG=str(log), GH_TOKEN='SYNTHETIC-ONLY')
        (host / 'launch.json').write_text('{corrupt')
        _, result = test.uninstall()
        test.assertEqual((result['localLaunch'], result['localRuntime'], result['localCredentials']), ('disabled', 'removed', 'cleared'))


def assert_recorded_configuration_preserved(test):
    host = test.prepare_update()
    arguments = ('--repository', 'owner/repository', '--app-id', '123', '--broker-url', runtime.BROKER,
                 '--enrollment-file', str(test.directory / 'enrollment.json'))
    test.assertEqual(test.public('stop').returncode, 0)
    configured = test.public('configure', *arguments, '--model', 'retained-model')
    test.assertEqual(configured.returncode, 0, configured.stdout + configured.stderr)
    updated = test.update('-Image', test.target, '-Apply')
    test.assertEqual(updated.returncode, 0, updated.stdout + updated.stderr)
    test.assertEqual(test.public('stop').returncode, 0)
    snapshot = "import json,hashlib; from pathlib import Path; print(json.dumps({p:hashlib.sha256(Path(p).read_bytes()).hexdigest() for p in ['/data/config/settings.json','/data/credentials/codex/config.toml']}))"
    before = test.files(snapshot)
    mirror = (host / 'launch.json').read_bytes()
    authority = test.files("from pathlib import Path; print(Path('/data/enrollment/launch-state.json').read_text())")
    refused = test.public('configure', *arguments, '--model', 'different-model')
    test.assertNotEqual(refused.returncode, 0, refused.stdout + refused.stderr)
    test.assertEqual(test.files(snapshot), before)
    test.assertEqual((host / 'launch.json').read_bytes(), mirror)
    test.assertEqual(test.files("from pathlib import Path; print(Path('/data/enrollment/launch-state.json').read_text())"), authority)
    matched = test.public('configure', *arguments)
    test.assertEqual(matched.returncode, 0, matched.stdout + matched.stderr)
    test.assertEqual(test.files(snapshot), before)
    test.assertEqual(test.public('start').returncode, 0)
