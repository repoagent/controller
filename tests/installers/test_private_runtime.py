"""Public PowerShell runtime orchestration; Docker Desktop acceptance is separate."""
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[2]
BROKER = 'https://credentials.example.org'
ENROLLMENT_CANARY = 'abcdefghijklmnopqrstuvwxyz_0123456789-ABCDEFG'


@unittest.skipUnless(os.name == 'posix' and shutil.which('pwsh'),
                     'Requires PowerShell 7 and the Linux Docker process fixture')
class PrivateRuntimeTests(unittest.TestCase):
    def test_powershell_configures_supplied_broker_enrollment_without_pem(self):
        """A supplied private frozen-v1 file selects broker mode without an App key."""
        with tempfile.TemporaryDirectory(prefix='repoagent private runtime ') as temporary:
            directory = Path(temporary)
            docker = directory / 'docker'
            shutil.copyfile(Path(__file__).with_name('docker_fixture.py'), docker)
            docker.chmod(0o755)
            enrollment = directory / 'controller enrollment.json'
            # Same frozen-v1 shape used by the unchanged consumer's broker tests.
            enrollment.write_text(json.dumps({
                'protocolVersion': 1, 'brokerOrigin': BROKER, 'githubHost': 'github.com',
                'deviceId': 'device-1', 'deviceSecret': ENROLLMENT_CANARY,
                'appId': 123, 'appSlug': 'test-app', 'installationId': 234,
                'repositoryId': 345, 'repository': 'owner/repository',
                'permissions': {'discussions': 'write'},
            }))
            enrollment.chmod(0o600)
            state_path = directory / 'docker-state.json'
            environment = dict(os.environ,
                PATH=str(directory) + os.pathsep + os.environ['PATH'],
                REPOAGENT_INSTANCE='private-runtime',
                REPOAGENT_IMAGE='sha256:' + 'a' * 64,
                INSTALLER_DOCKER_STATE=str(state_path),
                INSTALLER_DOCKER_REAL_CONFIGURE=str(ROOT / 'container'))

            result = subprocess.run([
                'pwsh', '-NoLogo', '-NoProfile', '-File', str(ROOT / 'repoagent.ps1'),
                'configure', '--repository', 'owner/repository', '--app-id', '123',
                '--broker-url', BROKER, '--enrollment-file', str(enrollment),
            ], capture_output=True, text=True, env=environment, cwd=directory, timeout=30)

            output = result.stdout + result.stderr
            state = json.loads(state_path.read_text())
            self.assertNotIn(ENROLLMENT_CANARY, output)
            self.assertNotIn(ENROLLMENT_CANARY, json.dumps(state))
            self.assertEqual(result.returncode, 0, output)
            self.assertEqual(state['settings']['brokerUrl'], BROKER)
            self.assertFalse(state['settings'].get('keyPath'))


if __name__ == '__main__':
    unittest.main()
