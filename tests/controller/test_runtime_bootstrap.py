"""Preparation contract against the actual separately published controller image."""
import json
import os
from pathlib import Path
import shutil
import subprocess
import unittest

ROOT = Path(__file__).resolve().parents[2]
IMAGE = os.environ.get('REPOAGENT_CONTROLLER_TEST_IMAGE',
    json.loads((ROOT / 'infra/controller/image.json').read_text())['image'])
SOURCE = ROOT / 'infra/controller/controller-runtime.py'

# Only synthetic opaque transport bytes; no broker, account or network is used.
JOURNEY = r'''
import json, pathlib, runpy
m = runpy.run_path('/acceptance/controller-runtime.py')
p = pathlib.Path
settings = dict(repository='owner/repo', repositoryId=345, appId='123', installationId=456,
    brokerUrl='https://broker.example.org', clone=True, mode='fixed-url',
    publicUrl='https://controller.example.org', mentionUser='repoagent',
    model='gpt-6.1-sol', webSearch='live', toolUpdates='pinned',
    listenPort=8080, reconcileInterval=300)
secret = p('/tmp/enrollment')
secret.write_bytes(b'SYNTHETIC_ENROLLMENT_TRANSPORT')
secret.chmod(0o600)
m['prepare_runtime'](settings, secret)
config = p('/data/config/settings.json')
saved = json.loads(config.read_text())
assert all(saved.get(k) == v for k, v in settings.items())
assert p('/data/enrollment/enrollment.json').read_bytes() == secret.read_bytes()
assert p('/data/enrollment/enrollment.json').stat().st_mode & 0o777 == 0o600
# A later launch must retain native sign-in/trust and per-discussion selections.
saved['enroll'] = ['https://github.com/owner/repo/discussions/1']
config.write_text(json.dumps(saved))
native = p('/data/credentials/codex/config.toml')
native.write_bytes(b'model="gpt-6.1-sol"\n[projects."/workspace/repository"]\ntrust_level="trusted"\n')
auth = p('/data/credentials/codex/auth.json')
auth.write_bytes(b'SYNTHETIC_PRIVATE_AUTH')
auth.chmod(0o600)
before = (config.read_bytes(), native.read_bytes(), auth.read_bytes())
m['prepare_runtime'](settings, secret)
assert (config.read_bytes(), native.read_bytes(), auth.read_bytes()) == before, 'Restart replaced retained settings or native trust/auth'
# Conflicting settings must fail before replacing the private credential.
secret.write_bytes(b'DIFFERENT_SYNTHETIC_ENROLLMENT')
try:
    m['prepare_runtime'](dict(settings, model='other-model'), secret)
except ValueError as error:
    assert 'settings differ' in str(error)
else:
    raise AssertionError('Conflicting settings accepted')
assert p('/data/enrollment/enrollment.json').read_bytes() == b'SYNTHETIC_ENROLLMENT_TRANSPORT'
assert (config.read_bytes(), native.read_bytes(), auth.read_bytes()) == before
print('BOOTSTRAP_RETENTION_AND_CONFLICT_OK')
'''


class RuntimeBootstrapTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        if not shutil.which('docker'):
            raise RuntimeError('Docker required for the published runtime contract')
        if subprocess.run(['docker', 'image', 'inspect', IMAGE], capture_output=True).returncode:
            subprocess.run(['docker', 'pull', '--quiet', IMAGE], check=True, timeout=180)

    def test_prepare_with_real_image_retains_state_and_rejects_conflicting_configuration(self):
        result = self.run_journey(SOURCE)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn('BOOTSTRAP_RETENTION_AND_CONFLICT_OK', result.stdout)

    @staticmethod
    def run_journey(source):
        return subprocess.run(['docker', 'run', '--rm', '--network', 'none',
            '--mount', f'type=bind,source={source},target=/acceptance/controller-runtime.py,readonly',
            '--entrypoint', '/usr/bin/python3', IMAGE, '-E', '-c', JOURNEY],
            capture_output=True, text=True, timeout=45)


if __name__ == '__main__':
    unittest.main()
