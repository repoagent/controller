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
import json, pathlib, runpy, sys
m = runpy.run_path('/acceptance/controller-runtime.py')
p = pathlib.Path
settings = dict(repository='owner/repo', repositoryId=345, appId='123', installationId=456,
    brokerUrl='https://broker.example.org', clone=True, mode='fixed-url',
    publicUrl='https://controller.example.org', mentionUser='repoagent',
    model='gpt-6.1-sol', webSearch='live', toolUpdates='pinned',
    listenPort=8080, reconcileInterval=300)
queue = sys.argv[1] if len(sys.argv) > 1 else ''
if queue:
    settings['azureWorkQueue'] = queue
secret = p('/tmp/enrollment')
secret.write_bytes(b'SYNTHETIC_ENROLLMENT_TRANSPORT')
secret.chmod(0o600)
m['prepare_runtime'](settings, secret)
config = p('/data/config/settings.json')
saved = json.loads(config.read_text())
assert all(saved.get(k) == v for k, v in settings.items())
if queue:
    sys.path.insert(0, '/opt/repoagent')
    runtime = runpy.run_path('/opt/repoagent/runtime.py')
    arguments = runtime['bridge_args'](saved)
    assert arguments.count('--azure-work-queue') == 1
    assert arguments[arguments.index('--azure-work-queue') + 1] == queue
    checked = runtime['bridge_args'](saved, check_auth=True)
    assert '--azure-work-queue' not in checked
    assert checked[checked.index('--repository-id') + 1] == '345'
    assert checked[checked.index('--installation-id') + 1] == '456'
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
if queue:
    try:
        m['prepare_runtime'](dict(settings, azureWorkQueue=queue + '-other'), secret)
    except ValueError as error:
        assert 'settings differ' in str(error)
    else:
        raise AssertionError('Conflicting queue accepted')
    assert p('/data/enrollment/enrollment.json').read_bytes() == b'SYNTHETIC_ENROLLMENT_TRANSPORT'
    assert (config.read_bytes(), native.read_bytes(), auth.read_bytes()) == before
    # Direct reconfiguration must also preserve the enrollment's installation.
    import subprocess
    changed = subprocess.run([sys.executable, '-E', '/opt/repoagent/runtime.py', 'configure',
        '--repository', settings['repository'], '--app-id', settings['appId'],
        '--broker-url', settings['brokerUrl'], '--enrollment-file', '/data/enrollment/enrollment.json',
        '--clone', '--repository-id', str(settings['repositoryId']), '--installation-id', '457',
        '--mode', settings['mode'], '--public-url', settings['publicUrl'], '--azure-work-queue', queue],
        capture_output=True, text=True, timeout=10)
    assert changed.returncode != 0, 'Changed retained installation was accepted'
    assert (config.read_bytes(), native.read_bytes(), auth.read_bytes()) == before
    assert p('/data/enrollment/enrollment.json').read_bytes() == b'SYNTHETIC_ENROLLMENT_TRANSPORT'
print('BOOTSTRAP_RETENTION_AND_CONFLICT_OK')
'''

QUEUE_REFUSALS = r'''
import pathlib, subprocess, sys
queue = sys.argv[1]
cases = [(queue + '?sig=SYNTHETIC_QUEUE_CANARY', 'fixed-url'),
         (queue.replace('https://', 'http://'), 'fixed-url'),
         (queue.replace('https://', 'https://user:password@'), 'fixed-url'),
         (queue, 'polling')]
for endpoint, mode in cases:
    result = subprocess.run([sys.executable, '-E', '/opt/repoagent/runtime.py', 'configure',
        '--repository', 'owner/repo', '--app-id', '123', '--broker-url', 'https://broker.example.org',
        '--enrollment-file', '/data/enrollment/enrollment.json', '--clone', '--repository-id', '345',
        '--installation-id', '456', '--mode', mode, '--public-url', 'https://controller.example.org',
        '--azure-work-queue', endpoint], capture_output=True, text=True, timeout=10)
    assert result.returncode != 0, 'Unsafe queue configuration was accepted'
    assert 'SYNTHETIC_QUEUE_CANARY' not in result.stdout + result.stderr
    assert not pathlib.Path('/data/config/settings.json').exists()
    assert not pathlib.Path('/data/enrollment/enrollment.json').exists()
    assert not pathlib.Path('/data/credentials/codex/config.toml').exists()
print('UNSAFE_QUEUE_CONFIGURATION_REFUSED_BEFORE_PRIVATE_EFFECTS')
'''

BOOTSTRAP_REFUSALS = r'''
import json, pathlib, subprocess, sys
queue = sys.argv[1]
for endpoint in (queue + '?sig=SYNTHETIC_QUEUE_CANARY',
                 queue.replace('https://', 'https://PRIVATE_USER_CANARY:PRIVATE_PASSWORD_CANARY@')):
    settings = dict(repository='owner/repo', repositoryId=345, appId='123', installationId=456,
        brokerUrl='https://broker.example.org', mode='fixed-url', publicUrl='https://controller.example.org',
        mentionUser='repoagent', model='gpt-6.1-sol', webSearch='live', toolUpdates='pinned',
        listenPort=8080, reconcileInterval=300, azureWorkQueue=endpoint)
    secret = pathlib.Path('/tmp/enrollment')
    secret.write_bytes(b'SYNTHETIC_ENROLLMENT_TRANSPORT')
    secret.chmod(0o600)
    result = subprocess.run([sys.executable, '-E', '-c',
        "import json,pathlib,runpy,sys; runpy.run_path('/acceptance/controller-runtime.py')['prepare_runtime'](json.loads(sys.argv[1]),pathlib.Path('/tmp/enrollment'))",
        json.dumps(settings)], capture_output=True, text=True, timeout=15)
    assert result.returncode != 0, 'Unsafe bootstrap configuration was accepted'
    assert 'Azure work queue' in result.stdout + result.stderr, 'Queue validation was not reached'
    for canary in ('SYNTHETIC_QUEUE_CANARY', 'PRIVATE_USER_CANARY', 'PRIVATE_PASSWORD_CANARY'):
        assert canary not in result.stdout + result.stderr, 'Bootstrap logged rejected credentials'
    for target in ('/data/config/settings.json', '/data/enrollment/enrollment.json',
                   '/data/credentials/codex/config.toml'):
        assert not pathlib.Path(target).exists(), 'Refusal changed private state'
print('BOOTSTRAP_REJECTED_CREDENTIALS_REMAIN_PRIVATE')
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

    def test_queue_configuration_reaches_native_arguments_and_survives_restart(self):
        result = self.run_journey(SOURCE, queue='https://storageexample123.queue.core.windows.net/controller-work')
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn('BOOTSTRAP_RETENTION_AND_CONFLICT_OK', result.stdout)

    def test_unsafe_queue_configuration_is_refused_before_private_effects(self):
        result = self.run_journey(SOURCE, queue='https://storageexample123.queue.core.windows.net/controller-work',
                                  journey=QUEUE_REFUSALS)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn('UNSAFE_QUEUE_CONFIGURATION_REFUSED_BEFORE_PRIVATE_EFFECTS', result.stdout)

    def test_bootstrap_does_not_log_rejected_queue_credentials(self):
        result = self.run_journey(SOURCE, queue='https://storageexample123.queue.core.windows.net/controller-work',
                                  journey=BOOTSTRAP_REFUSALS)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn('BOOTSTRAP_REJECTED_CREDENTIALS_REMAIN_PRIVATE', result.stdout)

    @staticmethod
    def run_journey(source, *, queue='', journey=JOURNEY):
        command = ['docker', 'run', '--rm', '--network', 'none',
            '--mount', f'type=bind,source={source},target=/acceptance/controller-runtime.py,readonly',
        ]
        if queue and 'REPOAGENT_CONTROLLER_TEST_IMAGE' not in os.environ:
            command += ['--mount', f'type=bind,source={ROOT / "container/runtime.py"},target=/opt/repoagent/runtime.py,readonly']
        return subprocess.run(command + ['--entrypoint', '/usr/bin/python3', IMAGE, '-E', '-c', journey, queue],
            capture_output=True, text=True, timeout=45)


if __name__ == '__main__':
    unittest.main()
