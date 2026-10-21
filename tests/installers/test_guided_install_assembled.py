"""Opt-in public Install -> production broker/Table/DP -> actual Docker/native.

Only external provider/signing/Codex transports are controlled. The fixture owns
its Azurite, private files, image and runtime resources; no live credentials.
"""
import hashlib
import datetime
import json
import os
from pathlib import Path
import shutil
import socket
import ssl
import subprocess
import time
import unittest
import uuid
import urllib.error
import urllib.request

import test_host_enrollment as host
import test_host_enrollment_assembled as enrollment

ROOT = Path(__file__).resolve().parents[2]
BASE = 'sha256:09132ca02e59183e84eeb164a2a33ca8afbaa7aa56787f7101820a8da0916e9d'
AZURITE = 'mcr.microsoft.com/azure-storage/azurite:3.37.0'


@unittest.skipUnless(os.environ.get('REPOAGENT_GUIDED_ASSEMBLED') == '1' and
                     os.environ.get('REPOAGENT_TEST_NATIVE_CONTROLLER'),
                     'Opt-in actual Docker and verified native controller required')
class AssembledGuidedInstallTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        host.HostEnrollmentTests.setUpClass.__func__(cls)
        result = subprocess.run(['dotnet', 'build', str(ROOT / 'tests/installers/BrokerFixture/BrokerFixture.csproj'),
            '--nologo', '--verbosity', 'quiet', '--ignore-failed-sources'], cwd=ROOT,
            capture_output=True, text=True, timeout=120)
        if result.returncode:
            raise RuntimeError(result.stdout + result.stderr)

    def test_public_install_reaches_native_readiness_then_restart_reuses_identity(self):
        native = Path(os.environ['REPOAGENT_TEST_NATIVE_CONTROLLER'])
        self.assertEqual(hashlib.sha256(native.read_bytes()).hexdigest(), enrollment.EXPECTED_NATIVE)
        source = subprocess.run(['git', 'diff', '--exit-code', 'dfd5617', '--', 'src/RepoAgentController',
            'global.json', '.agents/skills/github-discussion'], cwd=ROOT, capture_output=True, text=True)
        self.assertEqual(source.returncode, 0, source.stdout)
        journey = host.EnrollmentJourney(self, variable='APPROVED_CONTROLLER')
        directory = journey.directory
        instance = 'guided-actual-' + uuid.uuid4().hex[:12]
        image = 'repoagent:issue74-assembled-' + instance
        base_alias = 'repoagent:issue74-base-' + instance
        azurite = 'issue74-azurite-' + instance
        resources = []

        def docker(*arguments, check=True):
            result = subprocess.run(['docker', *arguments], capture_output=True, text=True, timeout=180)
            if check:
                self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
            return result

        def cleanup():
            # Each name is generated above and belongs to this one test.
            docker('rm', '-f', 'repoagent-' + instance, check=False)
            for storage in ('config', 'credentials', 'workspace', 'history', 'state', 'tools', 'enrollment'):
                docker('volume', 'rm', 'repoagent-' + instance + '-' + storage, check=False)
            docker('rm', '-f', azurite, check=False)
            for reference in resources:
                docker('image', 'rm', reference, check=False)
        self.addCleanup(cleanup)
        self.assertEqual(docker('info', '--format', '{{.OSType}}').stdout.strip(), 'linux')
        self.assertEqual(docker('image', 'inspect', BASE, '--format', '{{.Id}}').stdout.strip(), BASE)
        # Docker Desktop exposes the actual host through this DNS name; an
        # alternate normal DNS origin can be selected on Linux Docker hosts.
        broker_host = os.environ.get('REPOAGENT_GUIDED_BROKER_HOST', 'host.docker.internal')
        socket.gethostbyname(broker_host)
        # Generate a unique public test CA/SAN, trusted by both normal TLS clients.
        subprocess.run(['openssl', 'req', '-x509', '-newkey', 'rsa:2048', '-nodes',
            '-keyout', str(directory / 'key.pem'), '-out', str(directory / 'cert.pem'), '-days', '1',
            '-subj', '/CN=' + broker_host, '-addext', 'subjectAltName=DNS:' + broker_host],
            check=True, capture_output=True, timeout=30)
        subprocess.run(['openssl', 'pkcs12', '-export', '-out', str(directory / 'cert.pfx'),
            '-inkey', str(directory / 'key.pem'), '-in', str(directory / 'cert.pem'),
            '-passout', 'pass:test-only'], check=True, capture_output=True, timeout=30)
        with socket.socket() as selection:
            selection.bind(('0.0.0.0', 0))
            port = selection.getsockname()[1]
        journey.origin = f'https://{broker_host}:{port}'
        # Initial guided happy path must complete; the separate #53 assembled
        # test owns deliberately interrupted ack transport coverage.
        (directory / 'ack-response-lost').write_text('disabled for guided happy path')
        journey.environment.update(HOST_ENROLLMENT_FIXTURE_ORIGIN=journey.origin,
            HOST_ENROLLMENT_FIXTURE_BIND='0.0.0.0', NO_PROXY=broker_host + ',127.0.0.1,localhost',
            no_proxy=broker_host + ',127.0.0.1,localhost')
        context = directory / 'image-context'
        context.mkdir()
        for path in (ROOT / 'container').glob('*.py'):
            shutil.copyfile(path, context / path.name)
        shutil.copyfile(native, context / 'RepoAgentController')
        shutil.copyfile(ROOT / '.agents/skills/github-discussion/SKILL.md', context / 'SKILL.md')
        shutil.copyfile(directory / 'cert.pem', context / 'test-ca.crt')
        # External commands only; these do not emit the controller's ready log.
        (context / 'gh').write_text('''#!/usr/bin/python3
import json, os, sys
if os.environ.get('GH_TOKEN') != 'assembled-runtime-token': sys.exit(91)
if any(os.environ.get(k) for k in ('GITHUB_TOKEN','HOST_ENROLLMENT_GH_MODEL')): sys.exit(92)
request=json.load(sys.stdin)
query=request['query']
if 'viewer' in query: data={'viewer':{'login':'repo-agent-connector[bot]'}}
elif 'discussions(' in query: data={'repository':{'discussions':{'nodes':[],'pageInfo':{'hasNextPage':False,'endCursor':None}}}}
else: sys.exit(93)
print(json.dumps({'data':data}))
''')
        (context / 'codex').write_text('''#!/usr/bin/python3
import json, pathlib, sys
if sys.argv[1:] == ['login','status']:
    auth=pathlib.Path('/data/credentials/codex/auth.json')
    sys.exit(0 if auth.exists() and json.loads(auth.read_text()).get('fixtureAuth') else 1)
if sys.argv[1:] != ['app-server']: sys.exit(94)
for line in sys.stdin:
    request=json.loads(line)
    if request.get('method') == 'initialize':
        print(json.dumps({'id':request['id'],'result':{}}),flush=True)
    elif request.get('method') != 'initialized': sys.exit(95)
''')
        docker('tag', BASE, base_alias)
        resources.append(base_alias)
        (context / 'Dockerfile').write_text(f'''FROM {base_alias}
USER root
COPY *.py /opt/repoagent/
COPY RepoAgentController /opt/repoagent/controller/RepoAgentController
COPY SKILL.md /opt/repoagent/controller/github-discussion/SKILL.md
COPY gh codex /opt/repoagent/tools/bin/
COPY test-ca.crt /usr/local/share/ca-certificates/issue74-test-ca.crt
RUN chmod 0755 /opt/repoagent/controller/RepoAgentController /opt/repoagent/tools/bin/gh /opt/repoagent/tools/bin/codex && update-ca-certificates
ENV NO_PROXY={broker_host} no_proxy={broker_host}
USER 1000
''')
        docker('build', '--network', 'none', '-t', image, str(context))
        resources.append(image)
        image_id = docker('image', 'inspect', image, '--format', '{{.Id}}').stdout.strip()
        docker('run', '-d', '--rm', '--name', azurite, '-p', '127.0.0.1::10002', AZURITE)
        table_port = docker('port', azurite, '10002/tcp').stdout.strip().rsplit(':', 1)[1]
        table_endpoint = 'http://127.0.0.1:' + table_port + '/devstoreaccount1'
        # A published Docker port can accept TCP before Azurite's HTTP server
        # is ready. Observe an actual HTTP response before starting TableClient.
        opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
        deadline = time.monotonic() + 20
        while True:
            try:
                with opener.open(table_endpoint + '/Tables', timeout=2):
                    break
            except urllib.error.HTTPError as error:
                error.close()
                break  # An authenticated API error still proves HTTP readiness.
            except (OSError, urllib.error.URLError):
                if time.monotonic() >= deadline:
                    self.fail('Owned Azurite HTTP timeout: ' + docker('logs', azurite).stdout)
                time.sleep(.1)
        table = 'guided' + uuid.uuid4().hex
        command = ['dotnet', str(ROOT / 'tests/installers/BrokerFixture/bin/Debug/net10.0/BrokerFixture.dll'),
            str(directory), table_endpoint, table, str(port)]
        current = None
        initialized = False
        service_output = directory / 'service-output.log'

        def start_broker():
            nonlocal current, initialized
            service_output.write_text('')  # Fresh readiness observation after restart.
            with service_output.open('a') as stream:
                current = subprocess.Popen(command + ['resume' if initialized else 'create'], cwd=ROOT,
                    env=journey.environment, stdin=subprocess.PIPE, stdout=stream, stderr=stream, text=True)
            deadline = time.monotonic() + 25
            while time.monotonic() < deadline:
                if 'READY' in service_output.read_text():
                    initialized = True
                    return
                if current.poll() is not None:
                    self.fail('Broker setup failed: ' + service_output.read_text())
                time.sleep(.1)
            self.fail('Broker readiness timeout: ' + service_output.read_text())

        def stop_broker(clean=False):
            nonlocal current
            if current and current.poll() is None:
                current.communicate('clean\n' if clean else 'stop\n', timeout=20)
                self.assertEqual(current.returncode, 0, service_output.read_text())
            if current and current.stdin:
                current.stdin.close()
            current = None
        self.addCleanup(stop_broker, True)
        auth = directory / 'fake-auth.json'
        auth.write_text('{"fixtureAuth":true}')
        auth.chmod(0o600)
        environment = dict(journey.environment, REPOAGENT_IMAGE=image_id, REPOAGENT_INSTANCE=instance,
            CODEX_HOME=str(directory / 'unused-auth-home'))

        def install(saved=False):
            arguments = ['pwsh', '-NoProfile', '-File', str(ROOT / 'install.ps1'), '-Instance', instance,
                '-StateDirectory', str(journey.state), '-Apply', '-NonInteractive']
            if not saved:
                arguments += ['-Repository', 'example/project', '-BrokerOrigin', journey.origin,
                    '-CodexAuthPath', str(auth), '-SkipBuild', '-ToolUpdates', 'pinned']
            result = subprocess.run(arguments, cwd=directory, env=environment,
                input='', capture_output=True, text=True, timeout=150)
            # Only a bounded sanitized transcript is retained outside owned temp.
            output = result.stdout + result.stderr
            secrets = [journey.environment['GH_TOKEN']]
            if (journey.state / 'enrollment.json').exists():
                secrets.append(journey.read('enrollment.json')['deviceSecret'])
            if (journey.state / 'recovery.json').exists():
                secrets.append(journey.read('recovery.json').get('secret'))
            for secret in filter(None, secrets):
                self.assertFalse(secret in output, 'Private credential appeared in public installer output')
            print('GUIDED_ASSEMBLED_PROCESS ' + json.dumps({'command':arguments,
                'returncode':result.returncode,'output':output,'imageId':image_id,
                'nativeSha256':enrollment.EXPECTED_NATIVE}), flush=True)
            return result

        def events():
            return [json.loads(line) for line in (directory / 'broker.jsonl').read_text().splitlines()]

        def private_fingerprint(document):
            # Assertion diagnostics must never render the generated credential.
            return hashlib.sha256(json.dumps(document, sort_keys=True).encode('utf-8')).hexdigest()

        def assert_same_identity(receipt, marker):
            self.assertEqual(private_fingerprint(journey.read('enrollment.json')), private_fingerprint(receipt))
            self.assertEqual(journey.read('approval.json')['marker'], marker)
            self.assertEqual(json.loads(journey.model.read_text())['variables'][journey.variable], marker)
            self.assertEqual(journey.read('launch.json')['runtimeImageId'], image_id)
            self.assertEqual(sum('POST' in event['argv'] for event in journey.github()), 1)
            self.assertEqual(sum(event.get('path') == '/v2/enrollments' for event in events()), 1)

        scenario_deadline = time.monotonic() + 300

        def wait_observed_window(phase, event_offset):
            observed = [event for event in events()[event_offset:] if event.get('quotaObservation') and
                        event['status'] == 429 and event['partition'] == 'repository:123']
            self.assertTrue(observed, 'No actual repository quota observation for bounded resume')
            observed = observed[-1]
            # The real fixed-minute repository ledger (not a guessed delay) is
            # authoritative. Stop on an unexplained denial instead of retrying.
            self.assertEqual(observed['counters']['Signs'], 2, json.dumps(observed))
            target = (observed['counters']['Window'] + 1) * 60 + .2
            floor = None
            if observed['path'].endswith(('/completion', '/acknowledgment')):
                floors = [event for event in events()[event_offset:] if event.get('floorObservation') and
                          event['path'] == observed['path']]
                self.assertTrue(floors, 'Enrollment resume requires the actual persisted work deadline')
                floor = floors[-1]
                target = max(target, datetime.datetime.fromisoformat(floor['nextWorkAt']).timestamp() + .1)
            delay = max(0, target - time.time())
            self.assertLessEqual(delay, 61)
            self.assertLess(time.monotonic() + delay, scenario_deadline)
            approval = journey.read('approval.json')
            if approval.get('deadline'):
                self.assertLess(target, datetime.datetime.fromisoformat(approval['deadline']).timestamp())
            print('GUIDED_ASSEMBLED_RATE_WAIT ' + json.dumps({'phase':phase,
                'targetUtc':datetime.datetime.fromtimestamp(target,datetime.timezone.utc).isoformat(),
                'waitSeconds':delay,'observation':observed,'persistedWorkFloor':floor}), flush=True)
            while time.time() < target:
                time.sleep(min(20, max(0, target - time.time())))

        def assert_rate_limited_partial(result, receipt, marker):
            self.assertNotEqual(result.returncode, 0)
            self.assertIn('HTTP 429', result.stdout + result.stderr)
            assert_same_identity(receipt, marker)
            selected = docker('container', 'inspect', 'repoagent-' + instance, check=False)
            if selected.returncode == 0:
                self.assertFalse(json.loads(selected.stdout)[0]['State']['Running'])

        start_broker()
        # A CA/SAN/reachability failure is fixture setup, never behavioral red.
        with socket.create_connection((broker_host, port), timeout=5) as connection:
            with ssl.create_default_context(cafile=str(directory / 'cert.pem')).wrap_socket(
                    connection, server_hostname=broker_host):
                pass
        docker('run', '--rm', '--entrypoint', '/usr/bin/python3', image_id, '-c',
            "import socket,ssl; s=socket.create_connection(('" + broker_host + "'," + str(port) + ",),timeout=5); ssl.create_default_context().wrap_socket(s,server_hostname='" + broker_host + "').close()")
        event_offset = len(events())
        result = install()
        receipt = journey.read('enrollment.json')
        approval = journey.read('approval.json')
        marker = approval['marker']
        if result.returncode:
            assert_rate_limited_partial(result, receipt, marker)
            # Enrollment and native startup may straddle a fixed window; the
            # observation names the actual denied operation in either case.
            wait_observed_window('initial-public-install-authorization', event_offset)
            event_offset = len(events())
            result = install(saved=True)
            if result.returncode:
                assert_rate_limited_partial(result, receipt, marker)
                denied = [event for event in events()[event_offset:] if event.get('status') == 429]
                self.assertTrue(denied)
                self.assertTrue(denied[-1]['path'].endswith('/installation-token'), json.dumps(denied))
                wait_observed_window('initial-runtime-authorization', event_offset)
                result = install(saved=True)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr + service_output.read_text() + json.dumps(events()))
        assert_same_identity(receipt, marker)
        first = json.loads(docker('container', 'inspect', 'repoagent-' + instance).stdout)[0]
        self.assertEqual(first['Config']['User'], '1000')
        self.assertEqual(first['Config']['Entrypoint'], ['/usr/bin/python3', '-E', '/opt/repoagent/runtime.py'])
        self.assertEqual(first['Config']['Labels']['io.repoagent.owner'], 'private-runtime-v1')
        self.assertEqual(first['Config']['Labels']['io.repoagent.image'], image_id)
        mounts = {mount['Destination']:mount for mount in first['Mounts']}
        self.assertEqual(len(mounts), 7)
        self.assertFalse(mounts['/data/enrollment']['RW'])
        self.assertFalse(mounts['/data/config']['RW'])
        enrollment_volume = 'repoagent-' + instance + '-enrollment'
        probe = """import hashlib,json,os,pathlib,stat
p=pathlib.Path('/data/enrollment/enrollment.json')
assert os.getuid()==1000 and p.stat().st_uid==1000
assert stat.S_IMODE(p.stat().st_mode)==0o600
assert stat.S_IMODE(p.parent.stat().st_mode)==0o700
proof={'uid':os.getuid(),'fileMode':'0600','directoryMode':'0700',
 'enrollmentSha256':hashlib.sha256(p.read_bytes()).hexdigest(),
 'nativeSha256':hashlib.sha256(pathlib.Path('/opt/repoagent/controller/RepoAgentController').read_bytes()).hexdigest(),
 'runtimeSha256':hashlib.sha256(pathlib.Path('/opt/repoagent/runtime.py').read_bytes()).hexdigest()}
try: p.write_bytes(p.read_bytes())
except OSError: proof['readOnlyWriteRejected']=True
else: raise AssertionError('Enrollment mount writable')
print(json.dumps(proof))
"""
        proof = json.loads(docker('run', '--rm', '--mount',
            'type=volume,src=' + enrollment_volume + ',dst=/data/enrollment,readonly',
            '--entrypoint', '/usr/bin/python3', image_id, '-c', probe).stdout)
        self.assertEqual(proof['nativeSha256'], enrollment.EXPECTED_NATIVE)
        self.assertEqual(proof['runtimeSha256'], hashlib.sha256((ROOT / 'container/runtime.py').read_bytes()).hexdigest())
        self.assertEqual(proof['enrollmentSha256'], hashlib.sha256((journey.state / 'enrollment.json').read_bytes()).hexdigest())
        self.assertTrue(proof['readOnlyWriteRejected'])
        print('GUIDED_ASSEMBLED_IMAGE ' + json.dumps(proof), flush=True)
        self.assertTrue(first['State']['Running'])
        self.assertIn(' ready ', docker('logs', 'repoagent-' + instance).stdout)
        stopped = subprocess.run(['pwsh', '-NoProfile', '-File', str(ROOT / 'repoagent.ps1'), 'stop'],
            cwd=directory, env=environment, capture_output=True, text=True, timeout=45)
        self.assertEqual(stopped.returncode, 0, stopped.stdout + stopped.stderr)
        stop_broker()
        self.assertTrue(list((directory / 'keys').glob('*.xml')))
        start_broker()
        # A fresh container ID and its fresh log prove readiness belongs to the
        # restarted broker/runtime, not the previous invocation's log stream.
        event_offset = len(events())
        result = install(saved=True)
        if result.returncode:
            assert_rate_limited_partial(result, receipt, marker)
            denied = [event for event in events()[event_offset:] if event.get('status') == 429]
            self.assertTrue(denied and denied[-1]['path'].endswith('/installation-token'))
            wait_observed_window('restart-runtime-authorization', event_offset)
            result = install(saved=True)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr + service_output.read_text() + json.dumps(events()))
        assert_same_identity(receipt, marker)
        second = json.loads(docker('container', 'inspect', 'repoagent-' + instance).stdout)[0]
        self.assertTrue(second['State']['Running'])
        self.assertNotEqual(second['Id'], first['Id'])
        self.assertIn(' ready ', docker('logs', 'repoagent-' + instance).stdout)
        self.assertEqual(second['Image'], image_id)
        self.assertEqual(second['Mounts'], first['Mounts'])
        self.assertEqual(private_fingerprint(journey.read('enrollment.json')), private_fingerprint(receipt))
        self.assertEqual(journey.read('approval.json'), approval)
        self.assertEqual(sum('POST' in event['argv'] for event in journey.github()), 1)
        recorded = events()
        self.assertEqual([event['activation'] for event in recorded if 'activation' in event],
            ['fresh-created-and-verified', 'resume-existing-fixture-table'])
        self.assertEqual(sum(event.get('path') == '/v2/enrollments' for event in recorded), 1)
        self.assertTrue(any(event.get('path', '').endswith('/installation-token') and
            event.get('status') == 200 for event in recorded))
        print('GUIDED_ASSEMBLED broker events=' + json.dumps(recorded), flush=True)


if __name__ == '__main__':
    unittest.main()
