"""Public PowerShell process tests; real Docker and Desktop acceptance are separate."""
import hashlib
import datetime
import http.server
import ssl
import threading
import uuid
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
BROKER = 'https://credentials.example.org'
ENROLLMENT_CANARY = 'abcdefghijklmnopqrstuvwxyz_0123456789-ABCDEFG'
IMAGE = 'sha256:' + 'a' * 64
INSTANCE = 'private-runtime'
NAME = 'repoagent-' + INSTANCE
STORAGE = ('config', 'credentials', 'workspace', 'history', 'state', 'tools', 'enrollment')


def frozen_enrollment():
    # Frozen-v1 shape from the unchanged consumer tests, never a real credential.
    return {'protocolVersion': 1, 'brokerOrigin': BROKER, 'githubHost': 'github.com',
            'deviceId': 'device-1', 'deviceSecret': ENROLLMENT_CANARY,
            'appId': 123, 'appSlug': 'test-app', 'installationId': 234,
            'repositoryId': 345, 'repository': 'owner/repository',
            'permissions': {'discussions': 'write'}}


class PrivateRuntimeTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        if not shutil.which('pwsh'):
            raise unittest.SkipTest('PowerShell 7 required')
        cls.native = os.name == 'nt' or os.environ.get('INSTALLER_DOCKER_NATIVE_FIXTURE') == '1'
        cls.fixture_build = None
        if cls.native:
            temporary = tempfile.TemporaryDirectory(prefix='native docker fixture ')
            cls.addClassCleanup(temporary.cleanup)
            build = Path(temporary.name)
            shutil.copytree(Path(__file__).with_name('DockerFixture'), build / 'project')
            (build / 'global.json').write_text(json.dumps({'sdk': {
                'version': '10.0.100', 'rollForward': 'latestFeature', 'allowPrerelease': False}}))
            cls.fixture_build = build / 'output'
            result = subprocess.run(['dotnet', 'build', str(build / 'project/DockerFixture.csproj'),
                '--output', str(cls.fixture_build), '--nologo', '--verbosity', 'quiet',
                '--ignore-failed-sources'], cwd=build, capture_output=True, text=True, timeout=120)
            if result.returncode:
                raise RuntimeError('Native Docker fixture build failed:\n' + result.stdout + result.stderr)

    def setUp(self):
        self.directory = Path(self.enterContext(tempfile.TemporaryDirectory(prefix='private runtime α ')))
        if self.native:
            for artifact in self.fixture_build.iterdir():
                shutil.copy2(artifact, self.directory / artifact.name)
        else:
            docker = self.directory / 'docker'
            shutil.copyfile(Path(__file__).with_name('docker_fixture.py'), docker)
            docker.chmod(0o755)
        self.private = self.directory / 'private α'
        self.private.mkdir(mode=0o700)
        self.enrollment = self.private / 'controller enrollment.json'
        self.enrollment.write_bytes(json.dumps(frozen_enrollment()).encode('utf-8'))
        self.enrollment.chmod(0o600)
        if os.name == 'nt':
            self.set_windows_private_acl(self.private, directory=True)
            self.set_windows_private_acl(self.enrollment)
        self.state_path = self.directory / 'docker-state.json'
        self.environment = dict(os.environ,
            PATH=str(self.directory) + os.pathsep + os.environ['PATH'],
            REPOAGENT_INSTANCE=INSTANCE, REPOAGENT_IMAGE=IMAGE,
            INSTALLER_DOCKER_STATE=str(self.state_path),
            INSTALLER_DOCKER_REAL_CONFIGURE=str(ROOT / 'container'),
            INSTALLER_DOCKER_PYTHON=sys.executable,
            INSTALLER_DOCKER_FIXTURE=str(Path(__file__).with_name('docker_fixture.py').resolve()),
            PYTHONUTF8='1')
        executable = self.directory / ('docker.exe' if os.name == 'nt' else 'docker')
        self.assertEqual(Path(shutil.which('docker', path=self.environment['PATH'])).resolve(), executable.resolve())

    def set_windows_private_acl(self, path, *, directory=False, broad=False, inherited=False):
        # Real current-SID protected DACLs, not mocked Get-Acl output.
        script = """
param($Path, $Kind, $Broad, $Inherited)
$ErrorActionPreference = 'Stop'
$sid = [Security.Principal.WindowsIdentity]::GetCurrent().User
$acl = if ($Kind -eq 'directory') { [Security.AccessControl.DirectorySecurity]::new() } else { [Security.AccessControl.FileSecurity]::new() }
$acl.SetOwner($sid)
$acl.SetAccessRuleProtection($Inherited -ne 'true', $false)
$acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid, 'FullControl', 'Allow'))
if ($Broad -eq 'true') { $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new('S-1-1-0'), 'Read', 'Allow')) }
Set-Acl -LiteralPath $Path -AclObject $acl
"""
        script_path = self.directory / 'set-fixture-acl.ps1'
        script_path.write_text(script, encoding='utf-8')
        result = subprocess.run(['pwsh', '-NoProfile', '-File', str(script_path), str(path),
            'directory' if directory else 'file', str(broad).lower(), str(inherited).lower()],
            capture_output=True, text=True, timeout=30)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)

    def invoke(self, *arguments, success=True):
        result = subprocess.run(['pwsh', '-NoLogo', '-NoProfile', '-File', str(ROOT / 'repoagent.ps1'),
            *arguments], capture_output=True, text=True, encoding='utf-8', env=self.environment,
            cwd=self.directory, timeout=30)
        output = result.stdout + result.stderr
        state = self.state()
        self.assertNotIn(ENROLLMENT_CANARY, output)
        self.assertNotIn(ENROLLMENT_CANARY, json.dumps(state))
        if success:
            self.assertEqual(result.returncode, 0, output)
        elif success is False:
            self.assertNotEqual(result.returncode, 0, output)
        return result

    def state(self):
        return json.loads(self.state_path.read_text()) if self.state_path.exists() else {}

    def configure(self, *additional, success=True):
        return self.invoke('configure', '--repository', 'owner/repository', '--app-id', '123',
            '--broker-url', BROKER, '--enrollment-file', str(self.enrollment), *additional, success=success)

    def with_windows_failure_probe(self, kind, action):
        # Temporary issue73 Windows diagnosis. Preserve the public failure and
        # collect only the handoff's tagged observations from synthetic inputs.
        try:
            return action()
        except AssertionError as original:
            if os.name != 'nt':
                raise
            try:
                probe = self.directory / 'issue73-windows-probe.ps1'
                probe.write_text(Path(__file__).with_name('private_runtime_windows_probe.ps1').read_text(
                    encoding='utf-8'), encoding='utf-8')
                result = subprocess.run(['pwsh', '-NoProfile', '-File', str(probe),
                    '-Kind', kind, '-ModulePath', str(ROOT / 'scripts/private-file.psm1'),
                    '-Source', str(self.enrollment), '-StatePath', str(self.state_path),
                    '-ContainerName', NAME], capture_output=True, text=True, encoding='utf-8',
                    env=self.environment, cwd=self.directory, timeout=30)
                diagnostics = [line for line in result.stdout.splitlines()
                               if line.startswith('[DEBUG-issue73-win] ')]
                if result.returncode:
                    diagnostics.append('[DEBUG-issue73-win] probeExitCode=' + str(result.returncode))
            except Exception as failure:
                diagnostics = ['[DEBUG-issue73-win] probeHostFailureType=' + type(failure).__name__]
            raise self.failureException(str(original) + '\n' + '\n'.join(diagnostics)) from original

    def test_powershell_configures_supplied_broker_enrollment_without_pem(self):
        self.with_windows_failure_probe('private', self.configure)
        state = self.state()
        self.assertEqual(state['settings']['brokerUrl'], BROKER)
        self.assertFalse(state['settings'].get('keyPath'))
        self.assertNotIn(str(self.enrollment), json.dumps(state))
        expected = {'size': self.enrollment.stat().st_size,
                    'sha256': hashlib.sha256(self.enrollment.read_bytes()).hexdigest()}
        self.assertEqual(state['imports'], [expected])
        # The transport accepts opaque bytes; the consumer alone owns JSON policy.
        self.enrollment.write_bytes(bytes([0, 1, 255, 10, 13]))
        self.invoke('import-enrollment', str(self.enrollment))
        self.enrollment.write_bytes(json.dumps(frozen_enrollment()).encode('utf-8'))
        self.invoke('import-enrollment', str(self.enrollment))
        self.invoke('check')
        self.invoke('start')
        self.environment['REPOAGENT_IMAGE'] = 'sha256:' + 'b' * 64
        self.invoke('restart')
        state = self.state()
        self.assertTrue(state['running'])
        self.assertEqual(state['imports'][1], {'size': 5, 'sha256': hashlib.sha256(bytes([0, 1, 255, 10, 13])).hexdigest()})
        lifecycle = [event for event in state['events'] if event[0] == 'run' and event[-1] in ('check', 'start', 'import-enrollment')]
        self.assertTrue(lifecycle)
        for event in lifecycle:
            self.assertEqual(event[-2], IMAGE)
            self.assertFalse(any('github-app.pem' in argument or 'type=bind,' in argument for argument in event))
            mounts = [event[index + 1] for index, argument in enumerate(event[:-1]) if argument == '--mount']
            enrollment_mount = next(mount for mount in mounts if 'dst=/data/enrollment' in mount)
            if event[-1] == 'import-enrollment':
                self.assertEqual(mounts, [f'type=volume,src={NAME}-enrollment,dst=/data/enrollment'])
                self.assertEqual(event[event.index('--network') + 1], 'none')
                self.assertEqual(event[event.index('--user') + 1], '1000')
            else:
                self.assertTrue(enrollment_mount.endswith(',readonly'))
        self.assertFalse(any(event[0] == 'build' or '--env' in event or '-e' in event for event in state['events']))

    def test_conflicting_named_resources_are_preserved_before_mount_or_mutation(self):
        for storage in STORAGE + ('container',):
            with self.subTest(storage=storage):
                foreign = {'Labels': None, 'foreignCanary': 'preserve-me'}
                kind = 'containers' if storage == 'container' else 'volumes'
                name = NAME if storage == 'container' else NAME + '-' + storage
                original = {kind: {name: foreign}, 'settings': {}, 'events': [], 'running': False}
                self.state_path.write_text(json.dumps(original))
                self.configure(success=False)
                state = self.state()
                self.assertEqual(state[kind][name], foreign)
                self.assertEqual(state['settings'], {})
                self.assertFalse(any(event[0] in ('run', 'rm', 'stop') or event[:2] == ['volume', 'create'] for event in state['events']))

    def test_ambiguous_or_missing_broker_inputs_never_create_resources(self):
        for options in [('--broker-url', BROKER), ('--enrollment-file', str(self.enrollment)),
                        ('--broker-url', BROKER, '--enrollment-file', str(self.enrollment), '--key-path', '/unused.pem')]:
            with self.subTest(options=options):
                self.invoke('configure', '--repository', 'owner/repository', '--app-id', '123', *options, success=False)
                self.assertFalse(self.state())

    def test_abbreviated_broker_options_cannot_mount_or_configure_foreign_volumes(self):
        volumes = {NAME + '-' + storage: {'Labels': None, 'foreignCanary': 'preserve-me'}
                   for storage in STORAGE}
        self.state_path.write_text(json.dumps({
            'volumes': volumes, 'containers': {}, 'settings': {}, 'events': [], 'running': False}))
        result = self.invoke('configure', '--repository', 'owner/repository', '--app-id', '123',
            '--broker-u', BROKER, '--enrollment-f', str(self.enrollment), success=None)
        state = self.state()
        self.assertNotEqual(result.returncode, 0,
            f"Abbreviated broker configure succeeded; saved configuration={bool(state['settings'])}. "
            + result.stdout + result.stderr)
        self.assertEqual(state['volumes'], volumes)
        self.assertEqual(state['settings'], {})
        self.assertFalse(any(event[0] in ('run', 'rm', 'stop') or event[:2] == ['volume', 'create']
                             for event in state['events']))

    def test_private_source_rejections_happen_before_docker(self):
        if os.name == 'nt':
            variants = ('broad-file', 'broad-parent', 'inherited-file')
        else:
            variants = ('broad-file', 'broad-parent', 'symlink-file', 'hardlink-file', 'oversize')
        original = self.enrollment.read_bytes()
        for variant in variants:
            with self.subTest(variant=variant):
                if os.name == 'nt':
                    target = self.private if variant == 'broad-parent' else self.enrollment
                    self.set_windows_private_acl(target, directory=target == self.private,
                                                 broad=variant.startswith('broad'), inherited=variant.startswith('inherited'))
                elif variant == 'broad-file':
                    self.enrollment.chmod(0o644)
                elif variant == 'broad-parent':
                    self.private.chmod(0o755)
                elif variant == 'symlink-file':
                    self.enrollment.rename(self.private / 'original')
                    self.enrollment.symlink_to(self.private / 'original')
                elif variant == 'hardlink-file':
                    os.link(self.enrollment, self.private / 'alias')
                else:
                    self.enrollment.write_bytes(b'x' * 65537)
                self.configure(success=False)
                self.assertFalse(self.state())
                if os.name == 'nt':
                    self.set_windows_private_acl(self.private, directory=True)
                    self.set_windows_private_acl(self.enrollment)
                else:
                    if self.enrollment.is_symlink():
                        self.enrollment.unlink()
                        (self.private / 'original').rename(self.enrollment)
                    (self.private / 'alias').unlink(missing_ok=True)
                    self.private.chmod(0o700)
                    self.enrollment.chmod(0o600)
                    self.enrollment.write_bytes(original)

    def test_legacy_direct_lifecycle_preserves_volumes_and_rejects_extra_host_mount(self):
        key = self.directory / 'legacy key.pem'
        key.write_text('not-a-real-app-key')
        self.invoke('configure', '--repository', 'owner/repository', '--app-id', '123', '--key-path', str(key))
        self.invoke('check')
        self.invoke('start')
        before = self.state()['volumes']
        self.with_windows_failure_probe('mount', lambda: self.invoke('stop'))
        self.invoke('start')
        self.assertEqual(self.state()['volumes'], before)
        state = self.state()
        state['containers'][NAME]['Mounts'].append({'Type': 'bind', 'Source': '/foreign', 'Destination': '/foreign', 'RW': True})
        state['events'] = []
        self.state_path.write_text(json.dumps(state))
        self.invoke('stop', success=False)
        self.assertTrue(self.state()['running'])
        self.assertFalse(any(event[0] in ('rm', 'stop') for event in self.state()['events']))


@unittest.skipUnless(os.name == 'posix' and os.environ.get('REPOAGENT_TEST_NATIVE_CONTROLLER'),
                     'Opt-in: freshly compiled current native consumer required')
class NativeBrokerConsumerTests(unittest.TestCase):
    def test_real_tls_lease_and_private_file_rejection_use_unchanged_native_consumer(self):
        with tempfile.TemporaryDirectory(prefix='native broker proof ') as temporary:
            directory = Path(temporary)
            certificate, key = directory / 'ca.pem', directory / 'tls.key'
            result = subprocess.run(['openssl', 'req', '-x509', '-newkey', 'rsa:2048', '-nodes',
                '-keyout', str(key), '-out', str(certificate), '-days', '1', '-subj', '/CN=localhost',
                '-addext', 'subjectAltName=DNS:localhost', '-addext', 'basicConstraints=critical,CA:TRUE'],
                capture_output=True, text=True, timeout=30)
            self.assertEqual(result.returncode, 0, result.stderr)
            requests = []
            imported = os.environ.get('REPOAGENT_TEST_IMPORTED_ENROLLMENT')
            enrollment = json.loads(Path(imported).read_text()) if imported else frozen_enrollment()
            token = 'native-fixture-token-never-real'
            class Broker(http.server.BaseHTTPRequestHandler):
                def do_POST(handler):
                    # Do not retain the secret header in diagnostic/event records.
                    authenticated = handler.headers.get('Authorization') == 'Bearer ' + ENROLLMENT_CANARY
                    requests.append((handler.path, authenticated))
                    if not authenticated:
                        handler.send_error(401)
                        return
                    lease = {name: enrollment[name] for name in ('protocolVersion', 'appId', 'appSlug',
                             'installationId', 'repositoryId', 'repository', 'permissions')}
                    lease.update(token=token, expiresAt=(datetime.datetime.now(datetime.timezone.utc)
                        + datetime.timedelta(minutes=30)).isoformat())
                    body = json.dumps(lease).encode('utf-8')
                    handler.send_response(200)
                    handler.send_header('Content-Type', 'application/json')
                    handler.send_header('Content-Length', str(len(body)))
                    handler.end_headers()
                    handler.wfile.write(body)
                def log_message(handler, *args):
                    pass
            server = http.server.ThreadingHTTPServer(('localhost', 18443 if imported else 0), Broker)
            context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
            context.load_cert_chain(certificate, key)
            server.socket = context.wrap_socket(server.socket, server_side=True)
            thread = threading.Thread(target=server.serve_forever, daemon=True)
            thread.start()
            self.addCleanup(thread.join, 5)
            self.addCleanup(server.server_close)
            self.addCleanup(server.shutdown)
            origin = 'https://localhost:' + str(server.server_port)
            enrollment['brokerOrigin'] = origin
            source = Path(imported) if imported else directory / 'enrollment.json'
            if not imported:
                source.write_text(json.dumps(enrollment))
                source.chmod(0o600)
            else:
                self.assertEqual(enrollment['brokerOrigin'], origin)
            gh = directory / 'gh'
            gh.write_text('#!' + sys.executable + "\nimport json,os,sys\n"
                          + "assert os.environ['GH_TOKEN'] == '" + token + "'\n"
                          + "sys.stdin.read(); print(json.dumps({'data':{'viewer':{'login':'test-app[bot]'}}}))\n")
            gh.chmod(0o755)
            environment = dict(os.environ, PATH=str(directory) + os.pathsep + os.environ['PATH'],
                               SSL_CERT_FILE=str(certificate), NO_PROXY='localhost', no_proxy='localhost')
            command = [os.environ['REPOAGENT_TEST_NATIVE_CONTROLLER'], '--repo', 'owner/repository',
                '--app-id', '123', '--broker-url', origin, '--enrollment-file', str(source),
                '--workspace', str(directory), '--state', str(directory / 'state.json'), '--check-auth']
            cases = [(None, 'owner/repository', True)] if imported else [
                (0o600, 'owner/repository', True), (0o644, 'owner/repository', False),
                (0o600, 'owner/other', False)]
            for mode, repository, success in cases:
                with self.subTest(mode=str(mode), repository=repository):
                    if mode is not None:
                        source.chmod(mode)
                    command[2] = repository
                    before = len(requests)
                    result = subprocess.run(command, capture_output=True, text=True, env=environment, timeout=30)
                    output = result.stdout + result.stderr
                    self.assertNotIn(ENROLLMENT_CANARY, output)
                    self.assertNotIn(token, output)
                    if success:
                        self.assertEqual(result.returncode, 0, output)
                        self.assertEqual(requests[-1], ('/v1/devices/device-1/installation-token', True))
                    else:
                        self.assertNotEqual(result.returncode, 0, output)
                        self.assertEqual(len(requests), before, 'Invalid file/binding must fail before broker traffic')
            if not imported:
                source.chmod(0o600)


@unittest.skipUnless(os.name == 'posix' and os.environ.get('REPOAGENT_PRIVATE_RUNTIME_IMAGE'),
                     'Opt-in: actual Docker with current runtime image required')
class DockerPrivateImportTests(unittest.TestCase):
    def test_public_import_keeps_private_atomic_bytes_and_readonly_runtime_mount(self):
        image = os.environ['REPOAGENT_PRIVATE_RUNTIME_IMAGE']
        instance = 'test73-' + uuid.uuid4().hex[:12]
        name = 'repoagent-' + instance
        volume = name + '-enrollment'
        def docker(*args, data=None, success=True):
            result = subprocess.run(['docker', *args], input=data, capture_output=True, timeout=45)
            output = result.stdout + result.stderr
            self.assertNotIn(ENROLLMENT_CANARY.encode(), output)
            if success:
                self.assertEqual(result.returncode, 0, output.decode(errors='replace'))
            else:
                self.assertNotEqual(result.returncode, 0, output.decode(errors='replace'))
            return result
        selected = docker('image', 'inspect', '--format', '{{.Id}}', image).stdout.decode().strip()
        # Reject stale runtime evidence even when an opt-in image was cached.
        actual = docker('run', '--rm', '--network', 'none', '--entrypoint', '/usr/bin/sha256sum',
                        selected, '/opt/repoagent/runtime.py').stdout.decode().split()[0]
        self.assertEqual(actual, hashlib.sha256((ROOT / 'container/runtime.py').read_bytes()).hexdigest())
        def cleanup():
            for storage in STORAGE:
                resource = name + '-' + storage
                inspected = subprocess.run(['docker', 'volume', 'inspect', '--format', '{{json .Labels}}', resource],
                                           capture_output=True, text=True, timeout=15)
                if inspected.returncode == 0:
                    labels = json.loads(inspected.stdout)
                    if labels.get('io.repoagent.owner') == 'private-runtime-v1' and labels.get('io.repoagent.instance') == instance:
                        docker('volume', 'rm', resource)
        self.addCleanup(cleanup)
        with tempfile.TemporaryDirectory(prefix='docker private α ') as temporary:
            directory = Path(temporary)
            source = directory / 'enrollment bytes.json'
            original = bytes([0, 1, 255, 10, 13])
            source.write_bytes(original)
            source.chmod(0o600)
            environment = dict(os.environ, REPOAGENT_INSTANCE=instance, REPOAGENT_IMAGE=selected)
            configured = subprocess.run(['pwsh', '-NoProfile', '-File', str(ROOT / 'repoagent.ps1'),
                'configure', '--repository', 'owner/repository', '--app-id', '123', '--broker-url', BROKER,
                '--enrollment-file', str(source)], env=environment, capture_output=True, text=True, timeout=60)
            self.assertEqual(configured.returncode, 0, configured.stdout + configured.stderr)
            def python(code, *, readonly=False, success=True):
                mount = 'type=volume,src=' + volume + ',dst=/data/enrollment' + (',readonly' if readonly else '')
                return docker('run', '--rm', '--network', 'none', '--user', '1000', '--mount', mount,
                              '--entrypoint', '/usr/bin/python3', selected, '-c', code, success=success)
            verify = ("from pathlib import Path; import stat; p=Path('/data/enrollment/enrollment.json'); "
                      "s=p.stat(); d=p.parent.stat(); assert p.read_bytes()==bytes([0,1,255,10,13]); "
                      "assert s.st_uid==1000 and stat.S_IMODE(s.st_mode)==0o600; "
                      "assert d.st_uid==1000 and stat.S_IMODE(d.st_mode)==0o700; "
                      "assert not list(p.parent.glob('.enrollment-*'))")
            python(verify, readonly=True)
            write = "from pathlib import Path; Path('/data/enrollment/enrollment.json').write_bytes(b'probe')"
            python(write, readonly=True, success=False)
            # Bounded assertion-sensitivity experiment: omitting readonly MUST
            # invalidate the assertion that the mounted runtime rejects writes.
            with self.assertRaises(AssertionError):
                python(write, readonly=False, success=False)
            source.write_bytes(original)
            imported = subprocess.run(['pwsh', '-NoProfile', '-File', str(ROOT / 'repoagent.ps1'),
                'import-enrollment', str(source)], env=environment, capture_output=True, text=True, timeout=60)
            self.assertEqual(imported.returncode, 0, imported.stdout + imported.stderr)
            mount = 'type=volume,src=' + volume + ',dst=/data/enrollment'
            docker('run', '--rm', '-i', '--network', 'none', '--user', '1000', '--mount', mount,
                   selected, 'import-enrollment', data=b'x' * 65537, success=False)
            python(verify, readonly=True)
            python("from pathlib import Path; Path('/data/enrollment/enrollment.json').chmod(0o644)")
            docker('run', '--rm', '-i', '--network', 'none', '--user', '1000', '--mount', mount,
                   selected, 'import-enrollment', data=b'new bytes', success=False)
            python("from pathlib import Path; p=Path('/data/enrollment/enrollment.json'); "
                   "assert p.read_bytes()==bytes([0,1,255,10,13]); p.chmod(0o600)")
            python(verify, readonly=True)
            if native := os.environ.get('REPOAGENT_TEST_NATIVE_CONTROLLER'):
                native_mount = 'type=bind,src=' + str(Path(native).resolve()) + ',dst=/run/current-consumer/RepoAgentController,readonly'
                docker('run', '--rm', '--network', 'none', '--user', '1000',
                    '--mount', mount + ',readonly', '--mount', native_mount,
                    '--entrypoint', '/run/current-consumer/RepoAgentController', selected,
                    '--repo', 'owner/repository', '--app-id', '123', '--broker-url', BROKER,
                    '--enrollment-file', '/data/enrollment/enrollment.json',
                    '--workspace', '/workspace', '--state', '/tmp/native-state.json', '--check-auth', success=False)
                # Real current consumer reads the imported UID1000/0600 file
                # through a read-only volume, using localhost TLS in this
                # network-disabled container and a controlled gh response.
                frozen = frozen_enrollment()
                frozen['brokerOrigin'] = 'https://localhost:18443'
                source.write_text(json.dumps(frozen))
                imported = subprocess.run(['pwsh', '-NoProfile', '-File', str(ROOT / 'repoagent.ps1'),
                    'import-enrollment', str(source)], env=environment, capture_output=True, text=True, timeout=60)
                self.assertEqual(imported.returncode, 0, imported.stdout + imported.stderr)
                docker('run', '--rm', '--network', 'none', '--user', '1000',
                    '--mount', mount + ',readonly',
                    '--mount', native_mount,
                    '--mount', 'type=bind,src=' + str(Path(__file__).resolve()) + ',dst=/tmp/fixtures/tests/installers/test_private_runtime.py,readonly',
                    '--env', 'REPOAGENT_TEST_NATIVE_CONTROLLER=/run/current-consumer/RepoAgentController',
                    '--env', 'REPOAGENT_TEST_IMPORTED_ENROLLMENT=/data/enrollment/enrollment.json',
                    '--entrypoint', '/usr/bin/python3', selected,
                    '/tmp/fixtures/tests/installers/test_private_runtime.py', 'NativeBrokerConsumerTests', '-v')


if __name__ == '__main__':
    unittest.main()
