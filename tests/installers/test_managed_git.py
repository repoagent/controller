"""Real gh/Git managed-clone contract against a normally verified HTTPS provider.

No live provider access; the CONNECT fixture terminates TLS with a per-test CA.
Tokens are test-only and never included in assertion diagnostics.
Leases live only in a running credential manager's memory; wrappers reach it through
the controller's credential handoff, so every flow keeps a real manager running.
"""
import base64
from contextlib import contextmanager
import datetime
import http.server
import json
import selectors
import os
from pathlib import Path
import socket
import socketserver
import ssl
import subprocess
import tempfile
import threading
import unittest
from urllib.parse import urlsplit

ROOT = Path(__file__).resolve().parents[2]
CONTROLLER = ROOT / 'src/RepoAgentController/bin/Release/net11.0/RepoAgentController.dll'
DRIVER = ROOT / 'tests/installers/CredentialDriver/bin/Release/net11.0/CredentialDriver.dll'


def read_line(process, buffers, timeout=20):
    data = buffers.get(process.pid, b'')
    while b'\n' not in data:
        with selectors.DefaultSelector() as ready:
            ready.register(process.stdout, selectors.EVENT_READ)
            if not ready.select(timeout):
                raise AssertionError('Native/manager response timed out')
        chunk = os.read(process.stdout.fileno(), 4096)
        if not chunk:
            raise AssertionError('Native/manager exited before response')
        data += chunk
    result, buffers[process.pid] = data.split(b'\n', 1)
    return result.decode()


class CredentialManager:
    """The real in-memory credential manager, driven through its public lifecycle in its own process."""

    @staticmethod
    def build():
        driver = subprocess.run(['dotnet', 'build', str(ROOT / 'tests/installers/CredentialDriver/CredentialDriver.csproj'),
                                 '-c', 'Release', '--nologo', '-v', 'quiet'], cwd=ROOT,
                                capture_output=True, text=True, timeout=120)
        if driver.returncode:
            raise RuntimeError(driver.stdout + driver.stderr)

    def __init__(self, enrollment, credential, environment):
        self.buffers = {}
        self.process = subprocess.Popen(['dotnet', str(DRIVER), str(enrollment), str(credential)], env=environment,
                                        stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, text=True)
        if self.status() != 'initialized':
            raise AssertionError('Credential manager failed to initialize')

    def status(self):
        while True:
            status = read_line(self.process, self.buffers).strip()
            if status in ('initialized', 'renewed', 'denied'):
                return status

    def send(self, command):
        self.process.stdin.write(command + '\n')
        self.process.stdin.flush()
        return self.status()

    def stop(self):
        if self.process.poll() is None:
            self.process.stdin.close()
            try:
                self.process.wait(timeout=10)
            except subprocess.TimeoutExpired:
                self.process.kill()
                self.process.wait(timeout=5)
        self.process.stdout.close()


def persisted(paths, *tokens):
    """Files under paths that contain any token: no lease may reach persistent state."""
    found = []
    for root in paths:
        for path in [root] if root.is_file() else root.rglob('*'):
            if path.is_file() and not path.is_symlink() and path.stat().st_size and any(
                    token.encode() in path.read_bytes() for token in tokens):
                found.append(str(path.relative_to(root.parent)))
    return found


@unittest.skipIf(os.name == 'nt', 'Managed Git requires the Linux or macOS controller runtime')
class ManagedGitTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        build = subprocess.run(['dotnet', 'build', str(ROOT / 'src/RepoAgentController/RepoAgentController.csproj'),
                                '--configuration', 'Release', '--nologo', '--verbosity', 'quiet'],
                               cwd=ROOT, capture_output=True, text=True, timeout=120)
        if build.returncode:
            raise RuntimeError(build.stdout + build.stderr)
        CredentialManager.build()

    def test_actual_gh_clone_and_fixed_fetch_use_only_the_current_repository_lease(self):
        self.assert_clone_and_fetch()

    def test_fork_clone_with_quoted_paths_excludes_ambient_credentials_and_upstream(self):
        self.assert_clone_and_fetch(prefix="managed git's ", fork=True, ambient=True)

    @unittest.skipUnless(os.environ.get('REPOAGENT_TEST_NATIVE_CODEX'), 'Installed native Codex command boundary required')
    def test_persistent_native_process_observes_real_manager_renewal_then_denial(self):
        self.assert_clone_and_fetch(native=True)

    def assert_clone_and_fetch(self, prefix='managed-git-', fork=False, ambient=False, native=False):
        with tempfile.TemporaryDirectory(prefix=prefix) as temporary:
            directory = Path(temporary)
            fixture = GitProvider(directory, fork=fork)
            try:
                environment = fixture.environment()
                spy = directory / 'ambient-spy'
                if ambient:
                    home = directory / 'human-home'
                    home.mkdir()
                    (home / '.netrc').write_text('machine github.com login human password ambient-token\n')
                    (home / '.netrc').chmod(0o600)
                    helper = directory / 'ambient-helper'
                    helper.write_text('#!/bin/sh\nprintf used >> "' + str(spy) + '"\nexit 1\n')
                    helper.chmod(0o700)
                    config = home / '.gitconfig'
                    config.write_text('[credential]\n helper = ' + str(helper) + '\n[http]\n extraHeader = Authorization: ambient-token\n')
                    environment.update(HOME=str(home), GIT_CONFIG_GLOBAL=str(config), GIT_ASKPASS=str(helper),
                                       SSH_ASKPASS=str(helper), GIT_SSH_COMMAND=str(helper),
                                       GIT_CONFIG_COUNT='1', GIT_CONFIG_KEY_0='credential.helper', GIT_CONFIG_VALUE_0=str(helper))
                enrollment = directory / 'enrollment.json'
                enrollment.write_text(json.dumps(fixture.enrollment()))
                enrollment.chmod(0o600)
                state = directory / 'controller-state.json'
                native_controller = os.environ.get('REPOAGENT_TEST_NATIVE_CONTROLLER')
                command = [native_controller] if native_controller else ['dotnet', str(CONTROLLER)]
                self.run_ok(command + ['--repo', 'owner/project', '--app-id', '123',
                             '--broker-url', 'https://broker.fixture', '--enrollment-file', str(enrollment),
                             '--state', str(state), '--clone', '--check-auth'], environment)
                credential = Path(str(state) + '.github-app')
                wrapper = credential / 'bin/gh'
                checkout = directory / 'checkout'
                # The checked lifetime has exited and kept no lease: its installed helper is denied.
                stale = subprocess.run([str(credential / 'clone-bin/credential'), 'get'],
                                       input='protocol=https\nhost=github.com\npath=owner/project.git\n\n',
                                       env=environment, capture_output=True, text=True, timeout=15)
                self.assertNotEqual(stale.returncode, 0)
                self.assertEqual(stale.stdout, 'quit=true\n\n')
                self.assertEqual(persisted([credential, state], 'lease-A'), [])
                manager = CredentialManager(enrollment, credential, environment)
                try:
                    helper = subprocess.run([str(credential / 'clone-bin/credential'), 'get'],
                                            input='protocol=https\nhost=github.com\npath=owner/project.git\n\n',
                                            env=environment, capture_output=True, text=True, timeout=15)
                    self.assertEqual(helper.returncode, 0)
                    self.assertEqual(helper.stdout, 'username=x-access-token\npassword=lease-A\n\n')
                    self.run_ok([str(wrapper), 'repo', 'clone', 'owner/project', str(checkout)], environment)
                    self.assertEqual((checkout / 'README').read_text(), 'approved repository\n')
                    self.assertTrue(fixture.git_tokens)
                    self.assertEqual(set(fixture.git_tokens), {'lease-A'})
                    self.assertEqual(self.run_ok(['git', '-C', str(checkout), 'remote'], environment).stdout.strip(), 'origin')
                    self.run_ok([str(credential / 'bin/repoagent-git'), 'fetch', '--directory', str(checkout)], environment)
                    self.assertTrue((checkout / '.git/FETCH_HEAD').is_file())
                    # Git used the lease for HTTPS yet neither its configuration nor the controller files kept it.
                    self.assertEqual(persisted([credential, state, checkout / '.git'], 'lease-A'), [])
                    if fork:
                        self.assertEqual(self.run_ok(['git', '-C', str(checkout), 'config', '--get',
                                                     'remote.origin.gh-resolved'], fixture.environment()).stdout.strip(), 'base')
                        self.assertFalse(any('/upstream/' in path for method, host, path in fixture.requests))
                    self.assertFalse(spy.exists(), 'Ambient authentication must never execute')
                    if native:
                        self.assert_native_renewal(fixture, manager, credential, state, checkout)
                    if ambient:
                        self.assert_managed_denials(fixture, environment, manager, credential, wrapper, checkout, spy)
                finally:
                    manager.stop()

            finally:
                fixture.close()

    def assert_native_renewal(self, fixture, manager, credential, state, checkout):
        # The shipped image's native permission profile: commands reach the network and the credential handoff.
        home = fixture.directory / 'native-codex'
        home.mkdir(mode=0o700)
        (home / 'config.toml').write_text('approval_policy = "never"\nsandbox_mode = "danger-full-access"\n')
        environment = dict(fixture.environment(), CODEX_HOME=str(home))
        native = None
        buffers = {}

        def line(process):
            return read_line(process, buffers)

        try:
            native = subprocess.Popen([os.environ['REPOAGENT_TEST_NATIVE_CODEX'], 'app-server'], cwd=ROOT,
                                      env=environment, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                      stderr=subprocess.DEVNULL, text=True)
            request_id = 0

            def request(method, parameters):
                nonlocal request_id
                request_id += 1
                native.stdin.write(json.dumps(dict(id=request_id, method=method, params=parameters)) + '\n')
                native.stdin.flush()
                while True:
                    message = json.loads(line(native))
                    if message.get('id') == request_id:
                        self.assertNotIn('error', message, 'Native RPC request rejected')
                        return message['result']

            request('initialize', {'clientInfo': {'name': 'clone_conformance', 'version': '1'},
                                   'capabilities': {'experimentalApi': False}})
            native.stdin.write('{"method":"initialized"}\n')
            native.stdin.flush()
            wrapper = credential / 'bin/repoagent-git'
            original_wrapper = wrapper.read_bytes()

            def fetch():
                return request('command/exec', {'command': [str(wrapper), 'fetch', '--directory', str(checkout)],
                    'cwd': str(ROOT), 'timeoutMs': 20000})

            def helper():
                command = ['sh', '-c', "printf '%s\\n' protocol=https host=github.com path=owner/project.git '' | \"$1\" get",
                           'bound-helper', str(credential / 'clone-bin/credential')]
                return request('command/exec', {'command': command, 'cwd': str(ROOT), 'timeoutMs': 20000})

            first_helper = helper()
            self.assertEqual(first_helper['exitCode'], 0, json.dumps(first_helper))
            self.assertTrue(first_helper['stdout'] == 'username=x-access-token\npassword=lease-A\n\n')
            first = fetch()
            fixture.token, fixture.lease_offset_minutes = 'lease-B', 56
            self.assertEqual(manager.send('renew'), 'renewed')
            second_helper = helper()
            self.assertEqual(second_helper['exitCode'], 0)
            self.assertTrue(second_helper['stdout'] == 'username=x-access-token\npassword=lease-B\n\n')
            second = fetch()
            self.assertEqual(wrapper.read_bytes(), original_wrapper)
            self.assertEqual(persisted([credential, state, checkout / '.git'], 'lease-A', 'lease-B'), [])
            fixture.lease_status = 401
            self.assertEqual(manager.send('deny'), 'denied')
            before = len(fixture.requests)
            denied = helper()
            self.assertNotEqual(denied['exitCode'], 0)
            self.assertEqual(denied['stdout'], 'quit=true\n\n')
            denied_fetch = fetch()
            self.assertNotEqual(denied_fetch['exitCode'], 0)
            self.assertEqual(len(fixture.requests), before, 'Denied renewal must stop before provider network')
            self.assertIsNone(native.poll(), 'One native process must survive rotation and denial')
            print('Native persistent process pid=' + str(native.pid) + '; real manager/helper A-to-B/denial verified; fetch exits=' +
                  str(first['exitCode']) + '/' + str(second['exitCode']), flush=True)
            for phase, result in [('A', first), ('B', second)]:
                self.assertEqual(result['exitCode'], 0, 'Native managed fetch ' + phase + ' must succeed with existing permissions: ' +
                    result.get('stderr', '').replace('lease-A', '[test lease]').replace('lease-B', '[test lease]'))
        finally:
            if native is not None:
                if native.poll() is None:
                    native.stdin.close()
                    try:
                        native.wait(timeout=5)
                    except subprocess.TimeoutExpired:
                        native.kill()
                        native.wait(timeout=5)
                native.stdout.close()

    def assert_managed_denials(self, fixture, environment, manager, credential, wrapper, checkout, spy):
        before = len(fixture.requests)
        for arguments in [('repo', 'clone', 'other/project'),
                          ('repo', 'clone', 'owner/project', '--depth', '1')]:
            result = subprocess.run([str(wrapper), *arguments], env=environment,
                                    capture_output=True, text=True, timeout=15)
            self.assertNotEqual(result.returncode, 0)
        unknown = subprocess.run([str(credential / 'clone-bin/git'), 'status'],
                                 env=dict(environment, REPOAGENT_CLONE_TARGET=str(checkout)),
                                 capture_output=True, text=True, timeout=15)
        self.assertNotEqual(unknown.returncode, 0)
        self.assertEqual(len(fixture.requests), before, 'Rejected shapes must perform no provider work')
        included = fixture.directory / 'included-config'
        included.write_text('[fixture]\n value = harmless\n')
        for key, value in [('credential.helper', str(spy)), ('http.extraHeader', 'Authorization: ambient-token'),
                           ('url.https://other.example/.insteadOf', 'https://github.com/'),
                           ('include.path', str(included)), ('core.sshCommand', str(spy))]:
            with self.subTest(config=key):
                self.run_ok(['git', '-C', str(checkout), 'config', '--local', key, value], fixture.environment())
                result = subprocess.run([str(credential / 'bin/repoagent-git'), 'fetch', '--directory', str(checkout)],
                                        env=environment, capture_output=True, text=True, timeout=15)
                self.assertNotEqual(result.returncode, 0)
                self.assertEqual(len(fixture.requests), before, 'Unsafe local config must deny before network')
                self.run_ok(['git', '-C', str(checkout), 'config', '--local', '--unset-all', key], fixture.environment())
        self.run_ok(['git', '-C', str(checkout), 'status', '--porcelain'], fixture.environment())
        # A denied renewal, then the ended lifetime, leave nothing a wrapper could use or fall back from.
        fixture.lease_status = 401
        self.assertEqual(manager.send('deny'), 'denied')
        before = len(fixture.requests)
        for phase in ('denied', 'stopped'):
            if phase == 'stopped':
                manager.stop()
            result = subprocess.run([str(wrapper), 'repo', 'clone', 'owner/project', str(checkout) + '-denied'],
                                    env=environment, capture_output=True, text=True, timeout=15)
            self.assertNotEqual(result.returncode, 0)
            self.assertEqual(len(fixture.requests), before)
            self.assertFalse(spy.exists(), 'Denied state must not use ambient credentials')

    @contextmanager
    def live_lease(self, prefix):
        """A running manager holding lease-A for owner/project, as the controller would."""
        with tempfile.TemporaryDirectory(prefix=prefix) as temporary:
            directory = Path(temporary)
            fixture = GitProvider(directory)
            manager = None
            try:
                enrollment = directory / 'enrollment.json'
                enrollment.write_text(json.dumps(fixture.enrollment()))
                enrollment.chmod(0o600)
                credential = directory / 'credentials'
                manager = CredentialManager(enrollment, credential, fixture.environment())
                yield fixture, manager, credential
            finally:
                if manager:
                    manager.stop()
                fixture.close()

    def test_helper_accepts_repeated_git_capabilities_but_rejects_duplicate_authority(self):
        # Git's credential protocol allows array-valued extension attributes.
        # The real clone transcript includes repeated capability[] before authority.
        with self.live_lease('managed-helper-') as (_, _, credential):
            extensions = 'capability[]=authtype\ncapability[]=state\n'
            authority = 'protocol=https\nhost=github.com\npath=owner/project.git\n'

            def invoke(record):
                return subprocess.run(['dotnet', str(CONTROLLER), 'git-helper', str(credential), 'get'],
                                      input=record, capture_output=True, text=True, timeout=15)

            duplicate = invoke('protocol=https\n' + authority + '\n')
            self.assertNotEqual(duplicate.returncode, 0)
            self.assertEqual(duplicate.stdout, 'quit=true\n\n')
            valid = invoke(extensions + authority + 'wwwauth[]=Basic realm="private fixture"\n\n')
            self.assertEqual(valid.returncode, 0, 'Valid Git capability extensions must not deny approved authority')
            self.assertTrue(valid.stdout == 'username=x-access-token\npassword=lease-A\n\n',
                            'Helper must return only the current repository lease')

    def test_helper_denies_malformed_or_different_repository_records(self):
        with self.live_lease('managed-helper-denial-') as (_, _, credential):
            authority = {'protocol': 'https', 'host': 'github.com', 'path': 'owner/project.git'}
            records = {}
            for field, values in {
                'protocol': ['http', 'ssh'],
                'host': ['other.example', 'github.com:443', 'sub.github.com'],
                'path': ['other/project.git', 'owner/project-extra.git', 'owner/project.wiki.git',
                         'owner/project.git/extra', 'owner%2fproject.git', 'owner/../owner/project.git',
                         'owner/pro\rject.git', 'owner/pro\x00ject.git']
            }.items():
                for index, value in enumerate(values):
                    fields = dict(authority, **{field: value})
                    records[f'{field}-{index}'] = ''.join(f'{key}={item}\n' for key, item in fields.items()) + '\n'
            normal = ''.join(f'{key}={value}\n' for key, value in authority.items())
            for field in authority:
                records[f'duplicate-{field}'] = normal + f'{field}={authority[field]}\n\n'
            records['missing-path'] = 'protocol=https\nhost=github.com\n\n'
            records['malformed-line'] = normal + 'malformed\n\n'
            records['oversized'] = normal + 'extension=' + 'x' * 17000 + '\n\n'
            for name, record in records.items():
                with self.subTest(record=name):
                    result = subprocess.run(['dotnet', str(CONTROLLER), 'git-helper', str(credential), 'get'],
                                            input=record, capture_output=True, text=True, timeout=15)
                    self.assertNotEqual(result.returncode, 0,
                        'Malformed or mismatched authority must deny; credential emitted=' + str('password=' in result.stdout))
                    self.assertEqual(result.stdout, 'quit=true\n\n')

    def test_helper_line_endings_and_store_do_not_change_authority_or_newer_lease(self):
        with self.live_lease('managed-helper-state-') as (fixture, manager, credential):
            normal = 'protocol=https\nhost=github.com\npath=owner/project.git'

            def invoke(record, operation='get'):
                return subprocess.run(['dotnet', str(CONTROLLER), 'git-helper', str(credential), operation],
                                      input=record, capture_output=True, text=True, timeout=15)

            for name, record, allowed in [
                ('CRLF', normal.replace('\n', '\r\n') + '\r\n\r\n', True),
                ('EOF', normal, True), ('EOF-CR', normal + '\r', False),
                ('repeated-CR', normal + '\r\r\n\n', False)]:
                with self.subTest(ending=name):
                    result = invoke(record)
                    self.assertEqual(result.returncode == 0, allowed)
                    self.assertTrue(result.stdout == ('username=x-access-token\npassword=lease-A\n\n'
                                                     if allowed else 'quit=true\n\n'))
            stored = invoke(normal + '\npassword=other-value\n\n', 'store')
            self.assertEqual(stored.returncode, 0)
            self.assertEqual(stored.stdout, '')
            self.assertTrue(invoke(normal + '\n\n').stdout == 'username=x-access-token\npassword=lease-A\n\n',
                            'Store must not replace the held lease')
            fixture.token, fixture.lease_offset_minutes = 'lease-B', 56
            self.assertEqual(manager.send('renew'), 'renewed')
            rejected_old = invoke(normal + '\npassword=lease-A\n\n', 'erase')
            self.assertEqual(rejected_old.returncode, 0)
            current = invoke(normal + '\n\n')
            self.assertTrue(current.stdout == 'username=x-access-token\npassword=lease-B\n\n',
                            'Rejection of A must preserve renewed B')
            rejected_current = invoke(normal + '\npassword=lease-B\n\n', 'erase')
            self.assertEqual(rejected_current.returncode, 0)
            self.assertNotEqual(invoke(normal + '\n\n').returncode, 0)

    def run_ok(self, command, environment):
        result = subprocess.run(command, env=environment, capture_output=True, text=True, timeout=40)
        # Avoid disclosing Authorization values even when a subprocess fails.
        diagnostic = (result.stdout + result.stderr).replace('lease-A', '[test lease]').replace('ambient-token', '[ambient]')
        self.assertEqual(result.returncode, 0, diagnostic)
        return result


class GitProvider:
    def __init__(self, directory, fork=False, repository="owner/project", app_slug="test-app", token="lease-A", listen_host="127.0.0.1", broker_target=None, repository_id=345, responses=False):
        self.directory = directory
        self.repository, self.app_slug = repository, app_slug
        self.broker_target = broker_target
        self.repository_id = repository_id
        self.git_tokens = []
        self.token = token
        self.lease_offset_minutes = 0
        self.lease_status = 200
        self.requests = []
        self.responses, self.model_requests, self.denied = responses, [], []
        self.model_request_limit = 2
        # Discussion comment/reply nodes keyed by parent node ID, and EYES subjects in mutation order.
        self.comments, self.reactions = {}, []
        # Model request number -> threading.Event released by the test before that response streams.
        self.response_holds = {}
        self.cert = directory / 'cert.pem'
        self.key = directory / 'key.pem'
        subprocess.run(['openssl', 'req', '-x509', '-newkey', 'rsa:2048', '-nodes',
                        '-keyout', str(self.key), '-out', str(self.cert), '-days', '1',
                        '-subj', '/CN=github.com', '-addext',
                        'subjectAltName=DNS:github.com,DNS:api.github.com,DNS:broker.fixture'],
                       check=True, capture_output=True)
        self.server_cert, self.server_key = self.cert, self.key
        if responses:
            self.server_cert, self.server_key = directory / 'leaf.pem', directory / 'leaf.key'
            extensions = directory / 'leaf.ext'
            extensions.write_text('basicConstraints=critical,CA:FALSE\nextendedKeyUsage=serverAuth\nsubjectAltName=DNS:github.com,DNS:api.github.com,DNS:broker.fixture\n')
            subprocess.run(['openssl', 'req', '-newkey', 'rsa:2048', '-nodes', '-keyout', str(self.server_key), '-out', str(directory / 'leaf.csr'), '-subj', '/CN=broker.fixture'], check=True, capture_output=True)
            subprocess.run(['openssl', 'x509', '-req', '-in', str(directory / 'leaf.csr'), '-CA', str(self.cert), '-CAkey', str(self.key), '-CAcreateserial', '-out', str(self.server_cert), '-days', '1', '-extfile', str(extensions)], check=True, capture_output=True)
        seed = directory / 'seed'
        seed.mkdir()
        for args in [['init', '-b', 'main', str(seed)], ['-C', str(seed), 'config', 'user.email', 'fixture@example.test'],
                     ['-C', str(seed), 'config', 'user.name', 'fixture']]:
            subprocess.run(['git', *args], check=True, capture_output=True)
        (seed / 'README').write_text('approved repository\n')
        subprocess.run(['git', '-C', str(seed), 'add', 'README'], check=True, capture_output=True)
        subprocess.run(['git', '-C', str(seed), 'commit', '-m', 'fixture'], check=True, capture_output=True)
        target = directory / ('repositories/' + repository + '.git')
        target.parent.mkdir(parents=True)
        subprocess.run(['git', 'clone', '--bare', str(seed), str(target)], check=True, capture_output=True)
        provider = self

        class Handler(http.server.BaseHTTPRequestHandler):
            def log_message(self, *args):
                pass

            def do_CONNECT(self):
                if provider.broker_target and self.path == 'broker.fixture:' + str(provider.broker_target[1]):
                    with socket.create_connection(provider.broker_target, timeout=10) as upstream:
                        self.send_response(200)
                        self.end_headers()
                        with selectors.DefaultSelector() as relay:
                            relay.register(self.connection, selectors.EVENT_READ, upstream)
                            relay.register(upstream, selectors.EVENT_READ, self.connection)
                            while True:
                                ready = relay.select(30)
                                if not ready:
                                    return
                                for key, mask in ready:
                                    data = key.fileobj.recv(65536)
                                    if not data:
                                        return
                                    key.data.sendall(data)
                    return
                if self.path not in ('github.com:443', 'api.github.com:443', 'broker.fixture:443'):
                    provider.denied.append(('CONNECT', self.path))
                    self.send_error(403)
                    return
                self.send_response(200)
                self.end_headers()
                context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
                context.load_cert_chain(provider.server_cert, provider.server_key)
                self.connection = context.wrap_socket(self.connection, server_side=True)
                self.rfile = self.connection.makefile('rb')
                self.wfile = self.connection.makefile('wb')
                self.close_connection = False
                try:
                    self.handle_one_request()
                finally:
                    self.connection.close()

            def reply(self, value, status=200, content_type='application/json'):
                body = json.dumps(value).encode()
                self.send_response(status)
                self.send_header('Content-Type', content_type)
                self.send_header('Content-Length', str(len(body)))
                self.end_headers()
                self.wfile.write(body)

            def do_GET(self):
                self.dispatch()

            def do_POST(self):
                self.dispatch()

            def do_PATCH(self):
                self.dispatch()

            def dispatch(self):
                if self.headers.get('Transfer-Encoding', '').lower() == 'chunked':
                    chunks = []
                    while True:
                        length = int(self.rfile.readline().split(b';', 1)[0], 16)
                        if not length:
                            self.rfile.readline()
                            break
                        chunks.append(self.rfile.read(length))
                        self.rfile.read(2)
                    body = b''.join(chunks)
                else:
                    body = self.rfile.read(int(self.headers.get('Content-Length', 0)))
                host = self.headers.get('Host', '')
                provider.requests.append((self.command, host, self.path))
                if provider.responses and host == 'broker.fixture' and self.path.startswith('/v1/'):
                    if self.command != 'POST' or self.path != '/v1/responses' or self.headers.get('Content-Encoding', 'identity') != 'identity' or self.headers.get('Authorization') or len(provider.model_requests) >= provider.model_request_limit:
                        provider.denied.append((self.command, host, self.path)); return self.reply({}, 403)
                    provider.model_requests.append(dict(headers=dict(self.headers), body=json.loads(body)))
                    print('RESPONSES_REQUEST ' + json.dumps(provider.model_requests[-1]), flush=True)
                    number = len(provider.model_requests)
                    hold = provider.response_holds.get(number)
                    if hold is not None and not hold.wait(60):
                        return self.reply({}, 503)
                    rid = 'local-response-' + str(number)
                    item = dict(type='message', role='assistant', id='local-message-' + str(number), content=[dict(type='output_text', text='Harmless fixture assistant ' + str(number) + '.')])
                    events = [dict(type='response.created', response=dict(id=rid)), dict(type='response.output_item.done', item=item), dict(type='response.completed', response=dict(id=rid, usage=dict(input_tokens=0, input_tokens_details=None, output_tokens=0, output_tokens_details=None, total_tokens=0)))]
                    payload = ''.join('event: ' + e['type'] + '\ndata: ' + json.dumps(e) + '\n\n' for e in events).encode()
                    self.send_response(200); self.send_header('Content-Type', 'text/event-stream')
                    self.send_header('Content-Length', str(len(payload))); self.end_headers(); self.wfile.write(payload)
                    return
                if host == 'broker.fixture':
                    return self.reply(provider.lease() if provider.lease_status == 200 else {}, provider.lease_status)
                if host == 'api.github.com':
                    if getattr(provider, 'webhooks', False) and self.command in ('POST', 'PATCH') and self.path in ('/repos/' + repository + '/hooks', '/repos/' + repository + '/hooks/456'):
                        hook = json.loads(body); provider.hooks.append(hook)
                        return self.reply(dict(id=456, **hook))
                    if self.command == 'GET' and self.path == '/repos/' + repository:
                        fault = getattr(provider, 'postfetch_failure', None)
                        if fault and len(provider.git_tokens) > fault['git']:
                            fault.update(git=len(provider.git_tokens), count=fault['count'] + 1)
                            return self.reply({}, 503)
                        return self.reply(dict(id=provider.repository_id, full_name=repository, clone_url='https://github.com/' + repository + '.git', default_branch='main'))
                    if self.command == 'GET' and self.path == '/repos/' + repository + '/git/ref/heads/main':
                        if getattr(provider, 'ref_status', None):
                            return self.reply({}, provider.ref_status)
                        sha = subprocess.run(['git', '--git-dir', str(target), 'rev-parse', 'refs/heads/main'],
                                             check=True, capture_output=True, text=True, timeout=15).stdout.strip()
                        observer = getattr(provider, 'ref_observer', None)
                        if observer: observer(sha)
                        return self.reply(dict(ref='refs/heads/main', object=dict(type='commit', sha=sha)))
                    # A test may answer GraphQL requests itself first; None falls through to the defaults below.
                    answer = getattr(provider, 'graphql', None)
                    answer = answer(json.loads(body)) if answer and self.command == 'POST' and self.path == '/graphql' and body else None
                    if answer is not None:
                        return self.reply(answer)
                    if self.command != 'POST' or self.path != '/graphql' or not body:
                        print('GIT_PROVIDER_DENIED ' + json.dumps({'method': self.command, 'path': self.path}), flush=True)
                        return self.reply({}, 403)
                    query = json.loads(body).get('query', '')
                    variables = json.loads(body).get('variables') or {}
                    if 'addReaction(' in query:
                        provider.reactions.append(variables['id'])
                        return self.reply({'data': {'addReaction': {'reaction': {'id': 'EYES-' + str(len(provider.reactions))}}}})
                    if 'discussion(number:' in query:
                        return self.reply({'data': {'repository': {'discussion': provider.discussions[0]}}})
                    if 'discussions(' in query:
                        return self.reply({'data': {'repository': {'discussions': {'nodes': getattr(provider, 'discussions', []), 'pageInfo': {'hasNextPage': False, 'endCursor': None}}}}})
                    if 'node(id:' in query:
                        field = 'comments' if 'comments(' in query else 'replies' if 'replies(' in query else 'reactions'
                        nodes = [] if field == 'reactions' else provider.comments.get(variables.get('id'), [])
                        return self.reply({'data': {'node': {field: {'nodes': nodes, 'pageInfo': {'hasNextPage': False, 'endCursor': None}}}}})
                    if 'user(login:' in query:
                        return self.reply({'data': {'user': {'login': 'repoagent'}}})
                    if 'repository' not in query:
                        return self.reply({'data': {'viewer': {'login': provider.app_slug + '[bot]'}}})
                    return self.reply({'data': {'repository': {'name': repository.split('/')[1], 'owner': {'login': repository.split('/')[0]},
                                      'url': 'https://github.com/' + repository, 'isFork': fork,
                                      'parent': {'name': 'parent', 'owner': {'login': 'upstream'},
                                                 'url': 'https://github.com/upstream/parent',
                                                 'defaultBranchRef': {'name': 'main'}},
                                      'defaultBranchRef': {'name': 'main'}}}})
                if host != 'github.com' or not self.path.startswith('/' + repository + '.git/'):
                    return self.reply({}, 403)
                authorization = self.headers.get('Authorization', '')
                if not authorization:
                    self.send_response(401)
                    self.send_header('WWW-Authenticate', 'Basic realm="private fixture"')
                    self.send_header('Content-Length', '0')
                    self.end_headers()
                    return
                expected = 'Basic ' + base64.b64encode(('x-access-token:' + provider.token).encode()).decode()
                if authorization != expected:
                    return self.reply({}, 403)
                provider.git_tokens.append(provider.token)
                parsed = urlsplit(self.path)
                environment = dict(os.environ, GIT_PROJECT_ROOT=str(directory / 'repositories'),
                                   GIT_HTTP_EXPORT_ALL='1', PATH_INFO=parsed.path, QUERY_STRING=parsed.query,
                                   REQUEST_METHOD=self.command, CONTENT_TYPE=self.headers.get('Content-Type', ''),
                                   CONTENT_LENGTH=str(len(body)), REMOTE_USER='fixture')
                response = subprocess.run(['git', 'http-backend'], input=body, env=environment,
                                          capture_output=True, timeout=15)
                headers, payload = response.stdout.split(b'\r\n\r\n', 1)
                self.send_response(200)
                for header in headers.decode().split('\r\n'):
                    key, value = header.split(':', 1)
                    self.send_header(key, value.strip())
                self.send_header('Content-Length', str(len(payload)))
                self.end_headers()
                self.wfile.write(payload)

        class Server(socketserver.ThreadingTCPServer):
            allow_reuse_address = True
            daemon_threads = True

        self.server = Server((listen_host, 0), Handler)
        self.thread = threading.Thread(target=self.server.serve_forever, daemon=True)
        self.thread.start()

    def environment(self):
        environment = dict(os.environ)
        for key in list(environment):
            if key.lower().endswith('_proxy'):
                environment.pop(key)
        environment.update(HTTPS_PROXY=f'http://127.0.0.1:{self.server.server_address[1]}', NO_PROXY='',
                           SSL_CERT_FILE=str(self.cert), GIT_SSL_CAINFO=str(self.cert), GH_TOKEN='ambient-token')
        return environment

    def enrollment(self):
        enrollment = dict(protocolVersion=2, brokerOrigin='https://broker.fixture', githubHost='github.com',
                    deviceId='device-1', deviceSecret='a' * 43, appId=123, appSlug='test-app', installationId=234,
                    repositoryId=345, repository='owner/project', permissions={'discussions': 'write', 'contents': 'read'},
                    profile='polling-clone', policyVersion='repository-agent-clone-approval-v2')
        if getattr(self, 'webhooks', False):
            enrollment['permissions']['repository_hooks'] = 'write'
            enrollment['profile'] = 'webhook-clone'
            if not self.clone:
                enrollment.update(protocolVersion=1); enrollment['permissions'].pop('contents')
                enrollment.pop('profile'); enrollment.pop('policyVersion')
        return enrollment

    def lease(self):
        lease = self.enrollment()
        for key in ['brokerOrigin', 'githubHost', 'deviceId', 'deviceSecret']:
            lease.pop(key)
        lease.update(token=self.token, expiresAt=(datetime.datetime.now(datetime.timezone.utc) + datetime.timedelta(hours=1, minutes=self.lease_offset_minutes)).isoformat())
        return lease

    def close(self):
        self.server.shutdown()
        self.server.server_close()
        self.thread.join(timeout=5)


if __name__ == '__main__':
    unittest.main()
