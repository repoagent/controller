"""Real gh/Git managed-clone contract against a normally verified HTTPS provider.

No live provider access; the CONNECT fixture terminates TLS with a per-test CA.
Tokens are test-only and never included in assertion diagnostics.
"""
import base64
import datetime
import http.server
import json
import os
from pathlib import Path
import socketserver
import ssl
import subprocess
import tempfile
import threading
import unittest
from urllib.parse import urlsplit

ROOT = Path(__file__).resolve().parents[2]
CONTROLLER = ROOT / 'src/RepoAgentController/bin/Release/net11.0/RepoAgentController.dll'


class ManagedGitTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        build = subprocess.run(['dotnet', 'build', str(ROOT / 'src/RepoAgentController/RepoAgentController.csproj'),
                                '--configuration', 'Release', '--nologo', '--verbosity', 'quiet'],
                               cwd=ROOT, capture_output=True, text=True, timeout=120)
        if build.returncode:
            raise RuntimeError(build.stdout + build.stderr)

    def test_actual_gh_clone_and_fixed_fetch_use_only_the_current_repository_lease(self):
        with tempfile.TemporaryDirectory(prefix='managed-git-') as temporary:
            directory = Path(temporary)
            fixture = GitProvider(directory)
            try:
                environment = fixture.environment()
                enrollment = directory / 'enrollment.json'
                enrollment.write_text(json.dumps(fixture.enrollment()))
                enrollment.chmod(0o600)
                state = directory / 'controller-state.json'
                self.run_ok(['dotnet', str(CONTROLLER), '--repo', 'owner/project', '--app-id', '123',
                             '--broker-url', 'https://broker.fixture', '--enrollment-file', str(enrollment),
                             '--state', str(state), '--clone', '--check-auth'], environment)
                credential = Path(str(state) + '.github-app')
                wrapper = credential / 'bin/gh'
                checkout = directory / 'checkout'
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
            finally:
                fixture.close()

    def test_helper_accepts_repeated_git_capabilities_but_rejects_duplicate_authority(self):
        # Git's credential protocol allows array-valued extension attributes.
        # The real clone transcript includes repeated capability[] before authority.
        with tempfile.TemporaryDirectory(prefix='managed-helper-') as temporary:
            cache = Path(temporary) / 'installation-token.json'
            cache.write_text(json.dumps(dict(token='lease-A', repository='owner/project', profile='polling-clone',
                expiresAt=(datetime.datetime.now(datetime.timezone.utc) + datetime.timedelta(hours=1)).isoformat())))
            cache.chmod(0o600)
            extensions = 'capability[]=authtype\ncapability[]=state\n'
            authority = 'protocol=https\nhost=github.com\npath=owner/project.git\n'

            def invoke(record):
                return subprocess.run(['dotnet', str(CONTROLLER), 'git-helper', str(cache), 'get'],
                                      input=record, capture_output=True, text=True, timeout=15)

            duplicate = invoke('protocol=https\n' + authority + '\n')
            self.assertNotEqual(duplicate.returncode, 0)
            self.assertEqual(duplicate.stdout, 'quit=true\n\n')
            valid = invoke(extensions + authority + 'wwwauth[]=Basic realm="private fixture"\n\n')
            self.assertEqual(valid.returncode, 0, 'Valid Git capability extensions must not deny approved authority')
            self.assertTrue(valid.stdout == 'username=x-access-token\npassword=lease-A\n\n',
                            'Helper must return only the current repository lease')

    def run_ok(self, command, environment):
        result = subprocess.run(command, env=environment, capture_output=True, text=True, timeout=40)
        # Avoid disclosing Authorization values even when a subprocess fails.
        diagnostic = (result.stdout + result.stderr).replace('lease-A', '[test lease]').replace('ambient-token', '[ambient]')
        self.assertEqual(result.returncode, 0, diagnostic)
        return result


class GitProvider:
    def __init__(self, directory):
        self.directory = directory
        self.git_tokens = []
        self.requests = []
        self.cert = directory / 'cert.pem'
        self.key = directory / 'key.pem'
        subprocess.run(['openssl', 'req', '-x509', '-newkey', 'rsa:2048', '-nodes',
                        '-keyout', str(self.key), '-out', str(self.cert), '-days', '1',
                        '-subj', '/CN=github.com', '-addext',
                        'subjectAltName=DNS:github.com,DNS:api.github.com,DNS:broker.fixture'],
                       check=True, capture_output=True)
        seed = directory / 'seed'
        seed.mkdir()
        for args in [['init', '-b', 'main', str(seed)], ['-C', str(seed), 'config', 'user.email', 'fixture@example.test'],
                     ['-C', str(seed), 'config', 'user.name', 'fixture']]:
            subprocess.run(['git', *args], check=True, capture_output=True)
        (seed / 'README').write_text('approved repository\n')
        subprocess.run(['git', '-C', str(seed), 'add', 'README'], check=True, capture_output=True)
        subprocess.run(['git', '-C', str(seed), 'commit', '-m', 'fixture'], check=True, capture_output=True)
        target = directory / 'repositories/owner/project.git'
        target.parent.mkdir(parents=True)
        subprocess.run(['git', 'clone', '--bare', str(seed), str(target)], check=True, capture_output=True)
        provider = self

        class Handler(http.server.BaseHTTPRequestHandler):
            def log_message(self, *args):
                pass

            def do_CONNECT(self):
                self.send_response(200)
                self.end_headers()
                context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
                context.load_cert_chain(provider.cert, provider.key)
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
                if host == 'broker.fixture':
                    return self.reply(provider.lease())
                if host == 'api.github.com':
                    query = json.loads(body).get('query', '')
                    if 'repository' not in query:
                        return self.reply({'data': {'viewer': {'login': 'test-app[bot]'}}})
                    return self.reply({'data': {'repository': {'name': 'project', 'owner': {'login': 'owner'},
                                      'url': 'https://github.com/owner/project', 'isFork': False,
                                      'defaultBranchRef': {'name': 'main'}}}})
                if host != 'github.com' or not self.path.startswith('/owner/project.git/'):
                    return self.reply({}, 403)
                authorization = self.headers.get('Authorization', '')
                if not authorization:
                    self.send_response(401)
                    self.send_header('WWW-Authenticate', 'Basic realm="private fixture"')
                    self.send_header('Content-Length', '0')
                    self.end_headers()
                    return
                expected = 'Basic ' + base64.b64encode(b'x-access-token:lease-A').decode()
                if authorization != expected:
                    return self.reply({}, 403)
                provider.git_tokens.append('lease-A')
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

        self.server = Server(('127.0.0.1', 0), Handler)
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
        return dict(protocolVersion=2, brokerOrigin='https://broker.fixture', githubHost='github.com',
                    deviceId='device-1', deviceSecret='a' * 43, appId=123, appSlug='test-app', installationId=234,
                    repositoryId=345, repository='owner/project', permissions={'discussions': 'write', 'contents': 'read'},
                    profile='polling-clone', policyVersion='repository-agent-clone-approval-v2')

    def lease(self):
        lease = self.enrollment()
        for key in ['brokerOrigin', 'githubHost', 'deviceId', 'deviceSecret']:
            lease.pop(key)
        lease.update(token='lease-A', expiresAt=(datetime.datetime.now(datetime.timezone.utc) + datetime.timedelta(hours=1)).isoformat())
        return lease

    def close(self):
        self.server.shutdown()
        self.server.server_close()
        self.thread.join(timeout=5)


if __name__ == '__main__':
    unittest.main()
