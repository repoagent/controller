"""Current clone host → real HTTPS broker/Table/DP → actual controller consumer.

Caller supplies AUTH_TEST_TABLE_ENDPOINT and owns only that emulator. External
GitHub/signing transport uses the existing fixture; no deployed provider access.
"""
import hashlib
import json
import os
from pathlib import Path
import socket
import subprocess
import time
import unittest

import test_host_enrollment as host
from test_managed_git import GitProvider

ROOT = Path(__file__).resolve().parents[2]


def selected_native_sha256():
    selection = os.environ.get('REPOAGENT_TEST_NATIVE_CONTROLLER')
    if not selection:
        raise RuntimeError('Docker proof requires explicit REPOAGENT_TEST_NATIVE_CONTROLLER')
    artifact = Path(selection)
    try:
        if not artifact.is_file() or not os.access(artifact, os.X_OK):
            raise ValueError('must select an executable file')
        content = artifact.read_bytes()
        if not content.startswith(b'\x7fELF'):
            raise ValueError('must select a native Linux executable')
        identity = subprocess.run([str(artifact.resolve()), '--help'], capture_output=True,
                                  text=True, timeout=15, check=True)
        if not all(value in identity.stdout for value in
                   ('RepoAgentController', '--enrollment-file', '--clone', '--check-auth')):
            raise ValueError('executable does not expose the clone controller CLI')
    except (OSError, ValueError, subprocess.SubprocessError) as error:
        raise RuntimeError('Invalid REPOAGENT_TEST_NATIVE_CONTROLLER: ' + str(error)) from error
    return hashlib.sha256(content).hexdigest()


@unittest.skipUnless(os.environ.get('AUTH_TEST_TABLE_ENDPOINT'), 'Owned Azurite endpoint required')
class CloneAssembledTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        host.HostEnrollmentTests.setUpClass.__func__(cls)
        for project in ('tests/installers/BrokerFixture/BrokerFixture.csproj',
                        'src/RepoAgentController/RepoAgentController.csproj'):
            build = subprocess.run(['dotnet', 'build', str(ROOT / project), '-c', 'Release', '--nologo', '-v', 'quiet'],
                                   cwd=ROOT, capture_output=True, text=True, timeout=120)
            if build.returncode:
                raise RuntimeError(build.stdout + build.stderr)

    def test_completed_private_clone_file_is_consumed_by_current_controller(self):
        self.run_clone_journey()

    @unittest.skipUnless(os.environ.get('REPOAGENT_TEST_DOCKER_IMAGE'), 'Actual current-component Docker image required')
    def test_private_completed_clone_file_enters_actual_docker_runtime(self):
        self.run_clone_journey(container=True, expected_native=selected_native_sha256())

    def run_clone_journey(self, container=False, expected_native=None):
        journey = host.EnrollmentJourney(self, profile='polling-clone')
        directory = journey.directory
        subprocess.run(['openssl', 'pkcs12', '-export', '-out', str(directory / 'cert.pfx'),
                        '-inkey', str(directory / 'key.pem'), '-in', str(directory / 'cert.pem'),
                        '-passout', 'pass:test-only'], check=True, capture_output=True)
        with socket.socket() as selection:
            selection.bind(('127.0.0.1', 0))
            port = selection.getsockname()[1]
        journey.origin = f'https://localhost:{port}'
        provider = None
        public = directory / 'public'
        if container:
            subprocess.run(['openssl', 'req', '-x509', '-newkey', 'rsa:2048', '-nodes',
                '-keyout', str(directory / 'key.pem'), '-out', str(directory / 'cert.pem'), '-days', '1',
                '-subj', '/CN=broker.fixture', '-addext', 'subjectAltName=DNS:localhost,DNS:broker.fixture'],
                check=True, capture_output=True)
            subprocess.run(['openssl', 'pkcs12', '-export', '-out', str(directory / 'cert.pfx'),
                '-inkey', str(directory / 'key.pem'), '-in', str(directory / 'cert.pem'), '-passout', 'pass:test-only'],
                check=True, capture_output=True)
            provider_directory = directory / 'provider'
            provider_directory.mkdir()
            provider = GitProvider(provider_directory, repository='example/project', app_slug='repo-agent-connector',
                token='assembled-runtime-token', listen_host='0.0.0.0', broker_target=('127.0.0.1', port), repository_id=123)
            self.addCleanup(provider.close)
            public.mkdir()
            (public / 'ca.pem').write_bytes((directory / 'cert.pem').read_bytes() + provider.cert.read_bytes())
            journey.origin = f'https://broker.fixture:{port}'
            journey.environment.update(HTTPS_PROXY=provider.environment()['HTTPS_PROXY'], NO_PROXY='',
                                       SSL_CERT_FILE=str(public / 'ca.pem'))
        table = 'cloneassembled' + os.urandom(10).hex()
        command = ['dotnet', str(ROOT / 'tests/installers/BrokerFixture/bin/Release/net10.0/BrokerFixture.dll'),
                   str(directory), os.environ['AUTH_TEST_TABLE_ENDPOINT'], table, str(port), 'create', 'no-ack-fault', 'origin=' + journey.origin]
        output = directory / 'broker.log'
        with output.open('w') as stream:
            broker = subprocess.Popen(command, cwd=ROOT, stdin=subprocess.PIPE, stdout=stream, stderr=stream,
                                      text=True, env=journey.environment)
        try:
            deadline = time.monotonic() + 20
            while 'READY' not in output.read_text():
                if broker.poll() is not None or time.monotonic() > deadline:
                    self.fail('Broker setup failed: ' + output.read_text())
                time.sleep(.1)
            def events():
                return [json.loads(line) for line in (directory / 'broker.jsonl').read_text().splitlines()]

            def cross_observed_window():
                snapshots = [event for event in events() if event.get('quotaObservation') and event['partition'] == 'repository:123']
                observed = snapshots[-1]
                delay = max(0, (observed['counters']['Window'] + 1) * 60 + .2 - time.time())
                self.assertLessEqual(delay, 61)
                print('Observed legitimate broker quota cooldown seconds=' + str(round(delay, 3)), flush=True)
                time.sleep(delay)

            result = journey.run()
            if result.returncode and 'HTTP 429' in result.stdout:
                self.assertTrue(any(event.get('status') == 429 for event in events()))
                cross_observed_window()
                result = journey.run(saved=True)
            self.assertEqual(result.returncode, 0, journey.diagnostic(result) + output.read_text())
            receipt = journey.read('enrollment.json')
            self.assertEqual(receipt['protocolVersion'], 2)
            self.assertEqual(receipt['policyVersion'], 'repository-actions-clone-approval-v3')
            journey.private()
            self.assertTrue(any((directory / 'keys').glob('*.xml')))
            self.assertTrue(journey.read('recovery.json')['acknowledged'])
            # Completion used the remaining repository sign in its minute.
            # Observe actual quota metadata and cross only that legitimate window.
            snapshots = [event for event in events() if event.get('quotaObservation') and event['partition'] == 'repository:123']
            if snapshots[-1]['counters']['Signs'] >= 2:
                cross_observed_window()
            if container:
                self.assert_docker_runtime(journey, provider, public, expected_native)
                recorded = events()
                self.assertTrue(any(event.get('path', '').startswith('/v2/devices/') and
                                    event['path'].endswith('/installation-token') and event['status'] == 200 for event in recorded))
                self.assertTrue(any(event.get('receiptBeforeAck') for event in recorded))
                return
            consumer = os.environ.get('REPOAGENT_TEST_NATIVE_CONTROLLER')
            executable = [consumer] if consumer else ['dotnet', str(ROOT / 'src/RepoAgentController/bin/Release/net11.0/RepoAgentController.dll')]
            log = directory / 'consumer-gh.jsonl'
            result = subprocess.run(executable + ['--repo', 'example/project', '--app-id', '5387625',
                '--broker-url', journey.origin, '--enrollment-file', str(journey.state / 'enrollment.json'),
                '--state', str(directory / 'consumer-state.json'), '--clone', '--check-auth'],
                cwd=directory, env=dict(journey.environment, HOST_ENROLLMENT_NATIVE_CONSUMER='1', REVOKE_GH_LOG=str(log)),
                capture_output=True, text=True, timeout=45)
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr + output.read_text())
            self.assertTrue(json.loads(log.read_text().splitlines()[0])['runtimeCredentialObserved'])
            for secret in (receipt['deviceSecret'], 'assembled-runtime-token', journey.environment['GH_TOKEN']):
                self.assertNotIn(secret, result.stdout + result.stderr)
            recorded = events()
            self.assertTrue(any(event.get('path', '').startswith('/v2/devices/') and
                                event['path'].endswith('/installation-token') and event['status'] == 200 for event in recorded))
            self.assertTrue(any(event.get('receiptBeforeAck') for event in recorded))
            scopes = [event['permissions'] for event in recorded if 'permissions' in event]
            self.assertEqual(scopes, [{'actions_variables': 'read'}, {'discussions': 'write', 'contents': 'read'}])
        finally:
            if broker.poll() is None:
                broker.communicate('clean\n', timeout=20)
                self.assertEqual(broker.returncode, 0, output.read_text())
            if broker.stdin:
                broker.stdin.close()

    def assert_docker_runtime(self, journey, provider, public, expected_native):
        image = os.environ['REPOAGENT_TEST_DOCKER_IMAGE']
        instance = 'issue63-' + os.urandom(5).hex()
        name = 'repoagent-' + instance
        secrets = [journey.read('enrollment.json')['deviceSecret'], 'assembled-runtime-token', journey.environment['GH_TOKEN']]

        def checked(command, *, environment=None, timeout=60):
            result = subprocess.run(command, env=environment, capture_output=True, text=True, timeout=timeout)
            output = result.stdout + result.stderr
            self.assertTrue(all(secret not in output for secret in secrets), 'Credential found in diagnostics')
            self.assertEqual(result.returncode, 0, output)
            return result

        def docker(*arguments):
            return checked(['docker', '--host=unix:///var/run/docker.sock', *arguments])

        selected = docker('image', 'inspect', '--format', '{{.Id}}', image).stdout.strip()
        environment = dict(os.environ, REPOAGENT_INSTANCE=instance, REPOAGENT_IMAGE=selected)
        storage = ('config', 'credentials', 'workspace', 'history', 'state', 'tools', 'enrollment')
        container_name = name + '-probe'
        try:
            checked(['pwsh', '-NoProfile', '-File', str(ROOT / 'repoagent.ps1'), 'configure',
                '--repository', 'example/project', '--app-id', '5387625', '--broker-url', journey.origin,
                '--enrollment-file', str(journey.state / 'enrollment.json'), '--clone', '--tool-updates', 'pinned'],
                environment=environment)
            checked(['pwsh', '-NoProfile', '-File', str(ROOT / 'repoagent.ps1'), 'import-enrollment',
                     str(journey.state / 'enrollment.json')], environment=environment)
            probe = public / 'probe.py'
            probe.write_text(CONTAINER_PROBE)
            arguments = ['run', '--rm', '--init', '--name', container_name,
                         '--add-host', 'host.docker.internal:host-gateway']
            for kind in storage:
                target = '/workspace' if kind == 'workspace' else '/data/' + kind
                readonly = ',readonly' if kind in ('config', 'enrollment') else ''
                arguments += ['--mount', 'type=volume,src=' + name + '-' + kind + ',dst=' + target + readonly]
            arguments += ['--mount', 'type=bind,src=' + str(public) + ',dst=/run/issue63-public,readonly']
            proxy = 'http://host.docker.internal:' + str(provider.server.server_address[1])
            values = dict(HTTPS_PROXY=proxy, HTTP_PROXY=proxy, ALL_PROXY='', NO_PROXY='',
                https_proxy=proxy, http_proxy=proxy, all_proxy='', no_proxy='',
                SSL_CERT_FILE='/run/issue63-public/ca.pem', GIT_SSL_CAINFO='/run/issue63-public/ca.pem',
                EXPECTED_ENROLLMENT_SHA256=hashlib.sha256((journey.state / 'enrollment.json').read_bytes()).hexdigest())
            for key, value in values.items():
                arguments += ['--env', key + '=' + value]
            arguments += ['--entrypoint', '/usr/bin/python3', selected, '/run/issue63-public/probe.py']
            result = docker(*arguments)
            proof = json.loads(next(line for line in result.stdout.splitlines() if line.startswith('{')))
            self.assertTrue(all(proof[key] for key in ('privateFile', 'readonly', 'bot', 'clone', 'fetch', 'nativeFetch')))
            self.assertEqual(proof['uid'], 1000)
            self.assertTrue(provider.git_tokens)
            self.assertEqual(set(provider.git_tokens), {'assembled-runtime-token'})
            for filename in ('runtime.py', 'quick_tunnel.py', 'control.py', 'update_tools.py'):
                actual = docker('run', '--rm', '--network', 'none', '--entrypoint', '/usr/bin/sha256sum',
                                selected, '/opt/repoagent/' + filename).stdout.split()[0]
                self.assertEqual(actual, hashlib.sha256((ROOT / 'container' / filename).read_bytes()).hexdigest())
            actual_native = docker('run', '--rm', '--network', 'none', '--entrypoint', '/usr/bin/sha256sum',
                                   selected, '/opt/repoagent/controller/RepoAgentController').stdout.split()[0]
            self.assertEqual(actual_native, expected_native)
            print('DOCKER current-component image=' + selected + ' proof=' + json.dumps(proof), flush=True)
        finally:
            subprocess.run(['docker', '--host=unix:///var/run/docker.sock', 'rm', '--force', container_name],
                           capture_output=True, timeout=20)
            for kind in storage:
                resource = name + '-' + kind
                inspection = subprocess.run(['docker', '--host=unix:///var/run/docker.sock', 'volume', 'inspect',
                    '--format', '{{json .Labels}}', resource], capture_output=True, text=True, timeout=15)
                if inspection.returncode == 0:
                    labels = json.loads(inspection.stdout)
                    self.assertEqual(labels.get('io.repoagent.instance'), instance)
                    self.assertEqual(labels.get('io.repoagent.owner'), 'private-runtime-v1')
                    docker('volume', 'rm', resource)


CONTAINER_PROBE = r'''
import hashlib,json,os,queue,selectors,stat,subprocess,sys,threading
from pathlib import Path
sys.path.insert(0,'/opt/repoagent')
import runtime
file=Path('/data/enrollment/enrollment.json')
assert hashlib.sha256(file.read_bytes()).hexdigest()==os.environ['EXPECTED_ENROLLMENT_SHA256']
assert os.getuid()==1000 and file.stat().st_uid==1000 and stat.S_IMODE(file.stat().st_mode)==0o600
assert file.parent.stat().st_uid==1000 and stat.S_IMODE(file.parent.stat().st_mode)==0o700
assert not list(file.parent.glob('.enrollment-*'))
try:
 file.open('ab').close()
 raise AssertionError('Enrollment runtime mount must be readonly')
except OSError:pass
config=Path('/data/credentials/codex/config.toml').read_text()
assert 'approval_policy = "never"' in config and 'sandbox_mode = "danger-full-access"' in config
assert not Path('/data/credentials/codex/auth.json').exists()
for tool in ['gh','git','codex']:
 version=subprocess.run([tool,'--version'],capture_output=True,text=True,check=True).stdout.splitlines()[0]
 print(version,flush=True)
result=subprocess.run(runtime.CONTROLLER+runtime.bridge_args(runtime.settings())+['--check-auth'],capture_output=True,text=True)
assert result.returncode==0,result.stderr
assert 'repo-agent-connector[bot]' in result.stdout
wrapper='/data/state/bridge.json.github-app/bin/gh'
fetch='/data/state/bridge.json.github-app/bin/repoagent-git'
# The checked lifetime has exited without persisting its lease, so its wrapper is denied.
assert subprocess.run([wrapper,'repo','clone','example/project','/tmp/denied'],capture_output=True).returncode!=0
# A running controller holds the lease in memory and clones its own checkout through the same handoff.
controller=subprocess.Popen(runtime.CONTROLLER+runtime.bridge_args(runtime.settings())+['--interval','3600'],
 stdin=subprocess.DEVNULL,stdout=subprocess.PIPE,stderr=subprocess.STDOUT,text=True)
lines=queue.Queue()
threading.Thread(target=lambda:[lines.put(line) for line in controller.stdout],daemon=True).start()
while ' ready ' not in lines.get(timeout=90):pass
assert Path('/workspace/repository/README').read_text()=='approved repository\n'
subprocess.run([wrapper,'repo','clone','example/project','/tmp/checkout'],check=True,capture_output=True)
assert Path('/tmp/checkout/README').read_text()=='approved repository\n'
subprocess.run([fetch,'fetch','--directory','/tmp/checkout'],check=True,capture_output=True)
process=subprocess.Popen(['codex','app-server'],stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.DEVNULL,text=True)
buffer=b''
def request(i,method,params):
 global buffer
 process.stdin.write(json.dumps(dict(id=i,method=method,params=params))+'\n');process.stdin.flush()
 while True:
  while b'\n' not in buffer:
   with selectors.DefaultSelector() as ready:
    ready.register(process.stdout,selectors.EVENT_READ)
    assert ready.select(20),'Native response deadline'
   part=os.read(process.stdout.fileno(),4096);assert part;buffer+=part
  line,buffer=buffer.split(b'\n',1);message=json.loads(line)
  if message.get('id')==i:
   assert 'error' not in message,'Native RPC rejected'
   return message['result']
try:
 request(1,'initialize',dict(clientInfo=dict(name='container_clone_conformance',version='1'),capabilities=dict(experimentalApi=False)))
 process.stdin.write('{"method":"initialized"}\n');process.stdin.flush()
 response=request(2,'command/exec',dict(command=[fetch,'fetch','--directory','/tmp/checkout'],cwd='/tmp',timeoutMs=20000))
 assert response['exitCode']==0,response.get('stderr','')
 assert process.poll() is None
 pid=process.pid
finally:
 process.stdin.close()
 try:process.wait(timeout=5)
 except subprocess.TimeoutExpired:process.kill();process.wait(timeout=5)
 process.stdout.close()
 controller.terminate()
 controller.wait(timeout=20)
print(json.dumps(dict(uid=os.getuid(),privateFile=True,readonly=True,bot=True,clone=True,fetch=True,nativeFetch=True,nativePid=pid)))
'''


if __name__ == '__main__':
    unittest.main()
