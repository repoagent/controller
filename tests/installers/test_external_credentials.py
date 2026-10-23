"""Actual baseline container startup with externally injected controller credentials.

Opt-in: REPOAGENT_TEST_DOCKER_IMAGE selects an image built from this checkout and
REPOAGENT_TEST_NATIVE_CONTROLLER the controller executable it contains. GitProvider
replaces GitHub and the broker at the HTTPS proxy boundary (controlled transport,
not provider acceptance). Every credential is a synthetic canary.
"""
import hashlib
import json
import os
from pathlib import Path
import subprocess
import tempfile
import time
import unittest
import uuid

from test_clone_assembled import selected_native_sha256
from test_managed_git import GitProvider

ROOT = Path(__file__).resolve().parents[2]
IMAGE = os.environ.get('REPOAGENT_TEST_DOCKER_IMAGE', '')
DEVICE_SECRET = 'externalControllerCanary_' + 'Q' * 24
LEASE = 'external-lease-canary'
AMBIENT = 'ambient-human-token-canary'
CANARIES = (DEVICE_SECRET, LEASE, AMBIENT)
# Durable runtime storage only: no config volume and no enrollment volume.
VOLUMES = ('credentials', 'workspace', 'history', 'state', 'tools')
CONFIGURATION = dict(REPOAGENT_REPOSITORY='owner/project', REPOAGENT_APP_ID='123',
                     REPOAGENT_BROKER_URL='https://broker.fixture', REPOAGENT_CLONE='true',
                     REPOAGENT_REPOSITORY_ID='345', REPOAGENT_INSTALLATION_ID='234',
                     REPOAGENT_TOOL_UPDATES='pinned')
SEED_CODEX = ("import pathlib,sys; d=pathlib.Path('/data/credentials/codex'); d.mkdir(mode=0o700,exist_ok=True); "
              "p=d/'auth.json'; p.write_bytes(sys.stdin.buffer.read()); p.chmod(0o600)")
# Runs inside the started container. It learns the canary from its own inherited
# environment, so the canary never appears in an argument.
INSPECT = r'''
import json,os
needle=json.loads(os.environ['REPOAGENT_ENROLLMENT'])['deviceSecret'].encode()
files=[]
for top in ('/data','/workspace','/home','/tmp','/opt/repoagent'):
 for directory,_,names in os.walk(top):
  for name in names:
   path=os.path.join(directory,name)
   if os.path.islink(path) or not os.path.isfile(path):continue
   try:
    with open(path,'rb') as stream:
     if needle in stream.read():files.append(path)
   except OSError:pass
agents=[]
for pid in filter(str.isdigit,os.listdir('/proc')):
 if int(pid)==os.getpid():continue
 try:
  command=open('/proc/'+pid+'/cmdline','rb').read().split(b'\0')
  environment=open('/proc/'+pid+'/environ','rb').read()
 except OSError:continue
 if any(part.endswith(b'codex') for part in command):
  agents.append(dict(command=b' '.join(command).decode(errors='replace'),secret=needle in environment))
print(json.dumps(dict(files=files,agents=agents,enrollmentFile=os.path.lexists('/data/enrollment/enrollment.json'))))
'''


@unittest.skipUnless(IMAGE, 'Opt-in: REPOAGENT_TEST_DOCKER_IMAGE built from this checkout is required')
class ExternalControllerCredentialTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.native = selected_native_sha256()
        cls.image = subprocess.run(['docker', 'image', 'inspect', '--format', '{{.Id}}', IMAGE],
                                   capture_output=True, text=True, check=True, timeout=30).stdout.strip()
        for source, target in (('container/runtime.py', '/opt/repoagent/runtime.py'),
                               ('container/quick_tunnel.py', '/opt/repoagent/quick_tunnel.py'),
                               ('container/update_tools.py', '/opt/repoagent/update_tools.py'),
                               (None, '/opt/repoagent/controller/RepoAgentController')):
            actual = subprocess.run(['docker', 'run', '--rm', '--network', 'none', '--entrypoint', '/usr/bin/sha256sum',
                                     cls.image, target], capture_output=True, text=True, check=True, timeout=60).stdout.split()[0]
            expected = cls.native if source is None else hashlib.sha256((ROOT / source).read_bytes()).hexdigest()
            if actual != expected:
                raise RuntimeError(f'{target} in the selected image differs from this checkout')

    def setUp(self):
        self.directory = Path(self.enterContext(tempfile.TemporaryDirectory(prefix='external-credentials-')))
        self.instance = 'ext182-' + uuid.uuid4().hex[:10]
        self.provider = GitProvider(self.directory, repository='owner/project', token=LEASE, listen_host='0.0.0.0')
        self.addCleanup(self.provider.close)
        self.provider.broker_secret = DEVICE_SECRET
        self.provider.api_authorizations = []
        self.public = self.directory / 'public'
        self.public.mkdir()
        (self.public / 'ca.pem').write_bytes(self.provider.cert.read_bytes())
        self.containers = []
        self.addCleanup(self.cleanup)
        self.docker('run', '--rm', '-i', '--network', 'none', *self.mounts(('credentials',)),
                    '--entrypoint', '/usr/bin/python3', self.image, '-c', SEED_CODEX,
                    data=json.dumps({'OPENAI_API_KEY': 'sk-synthetic-codex-canary'}))

    def cleanup(self):
        for name in self.containers:
            subprocess.run(['docker', 'rm', '--force', name], capture_output=True, timeout=60)
        for kind in VOLUMES:
            subprocess.run(['docker', 'volume', 'rm', self.volume(kind)], capture_output=True, timeout=60)

    def volume(self, kind):
        return 'repoagent-' + self.instance + '-' + kind

    def mounts(self, kinds=VOLUMES):
        arguments = []
        for kind in kinds:
            target = '/workspace' if kind == 'workspace' else '/data/' + kind
            arguments += ['--mount', f'type=volume,src={self.volume(kind)},dst={target}']
        return arguments

    def docker(self, *arguments, data=None, environment=None, success=True, timeout=120):
        # The ambient human token is planted through --env on purpose.
        self.assertFalse(any(canary in argument for argument in arguments for canary in (DEVICE_SECRET, LEASE)),
                         'A credential reached a command argument')
        result = subprocess.run(['docker', *arguments], input=data, env=environment, capture_output=True,
                                text=True, timeout=timeout)
        output = result.stdout + result.stderr
        self.assertFalse([canary for canary in CANARIES if canary in output], 'Credential found in diagnostics')
        if success:
            self.assertEqual(result.returncode, 0, output)
        return result

    def enrollment(self, **changes):
        value = self.provider.enrollment()
        value.update(deviceSecret=DEVICE_SECRET, brokerOrigin='https://broker.fixture', **changes)
        return json.dumps(value)

    def runtime(self, command, *, secret=None, name=None, configuration=CONFIGURATION, success=True):
        """Run the image's own entrypoint, injecting the secret by variable name only."""
        arguments = ['run', '--init', '--add-host', 'host.docker.internal:host-gateway']
        if name:
            arguments += ['-d', '--name', name]
            self.containers.append(name)
        else:
            arguments += ['--rm']
        arguments += self.mounts()
        arguments += ['--mount', f'type=bind,src={self.public},dst=/run/test-public,readonly']
        proxy = 'http://host.docker.internal:' + str(self.provider.server.server_address[1])
        values = dict(HTTPS_PROXY=proxy, https_proxy=proxy, HTTP_PROXY=proxy, http_proxy=proxy, NO_PROXY='', no_proxy='',
                      SSL_CERT_FILE='/run/test-public/ca.pem', GIT_SSL_CAINFO='/run/test-public/ca.pem',
                      GH_TOKEN=AMBIENT, **configuration)
        for key, value in values.items():
            arguments += ['--env', key + '=' + value]
        client = {key: value for key, value in os.environ.items() if key != 'REPOAGENT_ENROLLMENT'}
        if secret is not None:
            client['REPOAGENT_ENROLLMENT'] = secret
            arguments += ['--env', 'REPOAGENT_ENROLLMENT']
        return self.docker(*arguments, self.image, command, environment=client, success=success, timeout=180)

    def wait_ready(self, name):
        deadline = time.monotonic() + 180
        while True:
            logs = self.docker('logs', name)
            output = logs.stdout + logs.stderr
            if ' ready repository=owner/project' in output:
                return output
            running = self.docker('inspect', '--format', '{{.State.Running}}', name).stdout.strip()
            if running != 'true' or time.monotonic() > deadline:
                self.fail('Controller did not become ready:\n' + output)
            time.sleep(1)

    def broker_requests(self):
        return [request for request in self.provider.requests if request[1] == 'broker.fixture']

    def assert_no_github_fallback(self):
        self.assertFalse([value for value in self.provider.api_authorizations if AMBIENT in value or LEASE not in value])

    def test_fresh_container_authorizes_injected_enrollment_and_restarts_with_reinjection(self):
        first = self.instance + '-first'
        self.runtime('start', secret=self.enrollment(), name=first)
        self.wait_ready(first)
        self.assertEqual(self.broker_requests(), [('POST', 'broker.fixture', '/v2/devices/device-1/installation-token')])
        self.assertTrue(self.provider.api_authorizations)
        self.assert_no_github_fallback()
        self.assertEqual(set(self.provider.git_tokens), {LEASE})
        mounts = json.loads(self.docker('inspect', '--format', '{{json .Mounts}}', first).stdout)
        self.assertEqual(sorted(mount['Destination'] for mount in mounts),
                         sorted(['/run/test-public', '/workspace'] + ['/data/' + kind for kind in VOLUMES if kind != 'workspace']))
        proof = json.loads(self.docker('exec', first, '/usr/bin/python3', '-c', INSPECT).stdout)
        self.assertEqual(proof['files'], [], 'Injected enrollment was copied into a file')
        self.assertFalse(proof['enrollmentFile'])
        self.assertTrue(proof['agents'], 'Native agent process not observed')
        self.assertFalse([agent for agent in proof['agents'] if agent['secret']], 'Native agent inherited the controller secret')
        state = json.loads(self.docker('exec', first, 'cat', '/data/state/bridge.json').stdout)
        self.assertEqual((state['repository'], state['agentLogin']), ('owner/project', 'test-app[bot]'))
        self.docker('stop', '--time', '20', first)
        self.docker('rm', first)

        # Restart replaces the container and reinjects the same external input;
        # runtime state and native history stay in their volumes.
        second = self.instance + '-second'
        self.runtime('start', secret=self.enrollment(), name=second)
        self.wait_ready(second)
        self.assertEqual(len(self.broker_requests()), 2, 'Each lifetime acquires its own in-memory lease')
        restored = json.loads(self.docker('exec', second, 'cat', '/data/state/bridge.json').stdout)
        self.assertEqual((restored['repository'], restored['agentLogin']), ('owner/project', 'test-app[bot]'))
        self.assertFalse(json.loads(self.docker('exec', second, '/usr/bin/python3', '-c', INSPECT).stdout)['files'])
        self.assert_no_github_fallback()
        self.docker('stop', '--time', '20', second)

    def test_missing_injected_enrollment_denies_startup_without_contact_or_fallback(self):
        result = self.runtime('start', success=False)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('REPOAGENT_ENROLLMENT', result.stdout + result.stderr)
        self.assertEqual(self.provider.requests, [])

    def test_mismatched_injected_enrollment_denies_startup_before_broker_contact(self):
        result = self.runtime('start', secret=self.enrollment(repository='owner/other'), success=False)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('differs from enrollment', result.stdout + result.stderr)
        self.assertEqual(self.provider.requests, [])

    def test_rejected_injected_secret_denies_startup_without_github_fallback(self):
        self.provider.broker_secret = 'externalBrokerExpects_' + 'R' * 24
        result = self.runtime('start', secret=self.enrollment(), success=False)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('HTTP 401', result.stdout + result.stderr)
        self.assertEqual(self.broker_requests(), [('POST', 'broker.fixture', '/v2/devices/device-1/installation-token')])
        self.assertEqual(self.provider.api_authorizations, [])


if __name__ == '__main__':
    unittest.main()
