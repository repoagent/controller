"""Public PowerShell enrollment; GitHub is replaced only at its executable boundary.

Linux execution does not establish native Windows or real broker acceptance.
"""
import json
import datetime
import hashlib
import http.server
import os
from pathlib import Path
import shutil
import subprocess
import ssl
import sys
import tempfile
import threading
import time
import unittest


ROOT = Path(__file__).resolve().parents[2]


class LostResponse(Exception):
    pass


class EnrollmentJourney:
    """Real process/TLS/filesystem; only GitHub and the broker are external doubles."""
    def __init__(self, case, *, variable='TEAM_CONTROLLER_APPROVAL', fault=None):
        self.case, self.variable, self.fault = case, variable, fault
        self.directory = Path(case.enterContext(tempfile.TemporaryDirectory(prefix='host enrollment ')))
        self.state = self.directory / 'private-state'
        self.model = self.directory / 'github.json'
        self.model.write_text(json.dumps({'variables': {}}), encoding='utf-8')
        self.log = self.directory / 'github.jsonl'
        self.events, self.errors = [], []
        self.observation_times = []
        self.deadline = (datetime.datetime.now(datetime.timezone.utc) + datetime.timedelta(minutes=15)).isoformat()
        self.marker = json.dumps(dict(version=2, approvalKind='repository-agent-variable', githubHost='github.com',
            appId=5387625, appSlug='repo-agent-connector', repositoryId=123, repository='example/project',
            installationId=789, deviceId='d' * 32, challenge='c' * 43, profile='polling',
            policyVersion='repository-agent-approval-v2', epoch='test-epoch', variable=variable), separators=(',', ':'))
        for artifact in case.fixture_build.iterdir():
            shutil.copy2(artifact, self.directory / artifact.name)
        openssl = shutil.which('openssl')
        if not openssl and os.name == 'nt':
            candidate = Path('C:/Program Files/Git/usr/bin/openssl.exe')
            openssl = str(candidate) if candidate.exists() else None
        if not openssl:
            raise RuntimeError('TLS journey requires OpenSSL to generate its temporary local certificate')
        cert, key = self.directory / 'cert.pem', self.directory / 'key.pem'
        subprocess.run([openssl, 'req', '-x509', '-newkey', 'rsa:2048', '-nodes', '-keyout', str(key),
                        '-out', str(cert), '-days', '1', '-subj', '/CN=localhost',
                        '-addext', 'subjectAltName=DNS:localhost'], check=True, capture_output=True, timeout=30)
        if os.name == 'nt':
            if os.environ.get('REPOAGENT_TEST_WINDOWS_ROOT') != 'LocalMachine':
                raise RuntimeError('Windows TLS tests require explicit REPOAGENT_TEST_WINDOWS_ROOT=LocalMachine '
                                   'and an already administrative shell; production enrollment needs no elevation')
            der = ssl.PEM_cert_to_DER_cert(cert.read_text(encoding='ascii'))
            thumbprint = hashlib.sha1(der).hexdigest()
            der_path = self.directory / 'public-certificate.der'
            der_path.write_bytes(der)
            def certificate_store(operation):
                # The helper launches no descendants. A file, rather than a pipe,
                # retains the last fixed API phase even if a native call hangs.
                diagnostic = self.directory / ('certificate-' + operation + '.jsonl')
                failure = None
                with diagnostic.open('wb') as output:
                    try:
                        result = subprocess.run(['pwsh', '-NoLogo', '-NoProfile', '-NonInteractive', '-File',
                            str(Path(__file__).with_name('windows-certificate-fixture.ps1')),
                            operation, str(der_path), thumbprint], stdin=subprocess.DEVNULL,
                            stdout=output, stderr=output, timeout=15,
                            creationflags=subprocess.CREATE_NO_WINDOW)
                        if result.returncode:
                            failure = 'failed'
                    except subprocess.TimeoutExpired:
                        failure = 'timed out'
                    except OSError as error:
                        failure = 'could not launch'
                        output.write((json.dumps({'operation': operation, 'phase': 'launch', 'status': 'error',
                                                  'win32': getattr(error, 'winerror', None) or 0}) + '\n').encode('utf-8'))
                with diagnostic.open('rb') as output:
                    phases = output.read(8192).decode('utf-8', errors='replace')
                print('WINDOWS_TRUST ' + operation + '\n' + phases, flush=True)
                if failure:
                    raise RuntimeError('Temporary opted-in LocalMachine Root ' + operation + ' ' + failure +
                                       '; bounded API phases:\n' + phases) from None
            # An interrupted add may already have succeeded. Remove only this
            # unique fixture certificate even when installation fails or times out.
            case.addCleanup(certificate_store, 'remove')
            certificate_store('add')
        journey = self

        class Handler(http.server.BaseHTTPRequestHandler):
            def log_message(self, *_):
                pass

            def do_POST(self):
                try:
                    result = journey.receive(self)
                    if journey.fault == 'redirect':
                        journey.fault = None
                        self.send_response(302)
                        self.send_header('Location', journey.origin + '/redirect-target')
                        self.send_header('Content-Length', '0')
                        self.end_headers()
                        return
                    data = json.dumps(result).encode('utf-8')
                    self.send_response(200)
                    self.send_header('Content-Length', str(len(data)))
                    self.end_headers()
                    self.wfile.write(data)
                except LostResponse:
                    self.close_connection = True
                    self.connection.close()
                except Exception as error:
                    journey.errors.append(repr(error))
                    self.send_error(500)

            def do_GET(self):
                journey.events.append({'path': self.path, 'method': 'GET'})
                self.send_error(500)

            def do_DELETE(self):
                try:
                    case.assertEqual(self.path, '/v1/devices/' + 'd' * 32)
                    case.assertEqual(self.headers.get('Authorization'), 'Bearer ' + journey.read('enrollment.json')['deviceSecret'])
                    journey.events.append({'path': self.path, 'method': 'DELETE'})
                    self.send_response(503 if journey.fault == 'revoke-unavailable' else 204)
                    self.send_header('Content-Length', '0')
                    self.end_headers()
                except Exception as error:
                    journey.errors.append(repr(error))
                    self.send_error(500)

        server = http.server.ThreadingHTTPServer(('127.0.0.1', 0), Handler)
        context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
        context.load_cert_chain(str(cert), str(key))
        server.socket = context.wrap_socket(server.socket, server_side=True)
        self.origin = f'https://localhost:{server.server_port}'
        threading.Thread(target=server.serve_forever, daemon=True).start()
        case.addCleanup(server.server_close)
        case.addCleanup(server.shutdown)
        self.environment = dict(os.environ, PATH=str(self.directory) + os.pathsep + os.environ['PATH'],
            SSL_CERT_FILE=str(cert), HOST_ENROLLMENT_GH_MODEL=str(self.model), REVOKE_GH_LOG=str(self.log),
            REVOKE_GH_PYTHON=sys.executable,
            REVOKE_GH_FIXTURE=str(Path(__file__).with_name('gh_fixture.py').resolve()),
            GH_TOKEN='HOST-CREDENTIAL-MUST-STAY-ON-HOST', PYTHONUTF8='1')

    def read(self, name):
        return json.loads((self.state / name).read_text(encoding='utf-8-sig'))

    def private(self):
        paths = [self.state, *self.state.iterdir()]
        self.case.assertFalse(any(path.suffix == '.tmp' for path in paths), paths)
        if os.name != 'nt':
            for path in paths:
                self.case.assertEqual(path.stat().st_mode & 0o777, 0o700 if path.is_dir() else 0o600)
        else:
            # Independent native ACL observation, not the production validator.
            script = '''$sid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
            Get-Item -LiteralPath $env:HOST_ENROLLMENT_STATE | ForEach-Object {
              @($_) + @(Get-ChildItem -LiteralPath $_.FullName) | ForEach-Object {
                $a=Get-Acl -LiteralPath $_.FullName
                $r=@($a.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier]))
                if (!$a.AreAccessRulesProtected -or $a.GetOwner([Security.Principal.SecurityIdentifier]).Value -ne $sid -or
                    $r.Count -ne 1 -or $r[0].IdentityReference.Value -ne $sid -or $r[0].AccessControlType -ne 'Allow' -or
                    $r[0].FileSystemRights -ne 'FullControl') { throw 'Unsafe native ACL' }
              }
            }'''
            checked = subprocess.run(['pwsh', '-NoLogo', '-NoProfile', '-Command', script], capture_output=True,
                text=True, env=dict(self.environment, HOST_ENROLLMENT_STATE=str(self.state)), timeout=15)
            self.case.assertEqual(checked.returncode, 0, checked.stdout + checked.stderr)

    def receive(self, request):
        body = json.loads(request.rfile.read(int(request.headers.get('Content-Length', '0'))) or '{}')
        recovery = self.read('recovery.json')
        credential = request.headers.get('Authorization')
        self.case.assertNotEqual(credential, 'Bearer ' + self.environment['GH_TOKEN'])
        self.events.append({'path': request.path, 'body': body, 'credential': credential})
        self.observation_times.append((request.path, time.monotonic()))
        if request.path == '/v2/enrollments':
            self.private()
            self.case.assertEqual(body, recovery['request'])
            self.case.assertEqual(set(body), {'idempotencyId', 'repositoryId', 'repository', 'variable', 'profile'})
            self.case.assertEqual(credential, 'Bearer ' + recovery['secret'])
            self.case.assertFalse((self.state / 'enrollment.json').exists())
            result = dict(pendingId='123_' + body['idempotencyId'], state='pending',
                          marker=self.marker, deadline=self.deadline)
            if self.fault == 'lost-start':
                self.fault = None
                raise LostResponse()
            return result
        if request.path.endswith('/completion'):
            self.case.assertEqual(credential, 'Bearer ' + recovery['secret'])
            remote = json.loads(self.model.read_text(encoding='utf-8'))
            self.case.assertEqual(remote['variables'][self.variable], self.marker)
            result = dict(completionId='a' * 32, enrollment=dict(protocolVersion=1, brokerOrigin=self.origin,
                githubHost='github.com', deviceId='d' * 32, deviceSecret='s' * 43, appId=5387625,
                appSlug='repo-agent-connector', installationId=789, repositoryId=123,
                repository='example/project', permissions={'discussions': 'write'}))
            if self.fault == 'lost-completion':
                self.fault = None
                raise LostResponse()
            return result
        self.case.assertEqual(request.path, '/v2/devices/' + 'd' * 32 + '/acknowledgment')
        self.case.assertEqual(body, {'completionId': 'a' * 32})
        saved = self.read('enrollment.json')
        self.case.assertEqual(saved['deviceSecret'], 's' * 43)
        self.case.assertEqual(recovery['completionId'], body['completionId'])
        self.case.assertTrue(recovery['ackAttempted'])
        self.case.assertEqual(credential, 'Bearer ' + saved['deviceSecret'])
        self.private()
        if self.fault == 'lost-ack':
            self.fault = None
            raise LostResponse()
        return {'state': 'active'}

    def run(self, *, saved=False, action='Enroll', extra=()):
        args = ['pwsh', '-NoLogo', '-NoProfile', '-File', str(ROOT / 'install.ps1'),
                '-Action', action, '-NonInteractive', '-Apply', '-StateDirectory', str(self.state)]
        if not saved:
            args += ['-Repository', 'example/project', '-BrokerOrigin', self.origin]
            if self.variable != 'APPROVED_CONTROLLER':
                args += ['-ApprovalVariable', self.variable.lower()]
        result = subprocess.run(args + list(extra), cwd=self.directory, env=self.environment,
                                input='', capture_output=True, text=True, encoding='utf-8', timeout=50)
        output = result.stdout + result.stderr
        for secret in (self.environment['GH_TOKEN'], 's' * 43):
            self.case.assertNotIn(secret, output)
        recovery = self.read('recovery.json') if (self.state / 'recovery.json').exists() else None
        if recovery and recovery['secret']:
            self.case.assertNotIn(recovery['secret'], output)
        return result

    def diagnostic(self, result):
        output = result.stdout + result.stderr
        secrets = [self.environment['GH_TOKEN'], 's' * 43]
        if (self.state / 'recovery.json').exists():
            secrets.append(self.read('recovery.json')['secret'])
        for secret in filter(None, secrets):
            output = output.replace(secret, '[redacted]')
        return json.dumps(dict(returncode=result.returncode,
            output=output.encode('utf-8')[:8192].decode('utf-8', errors='replace'),
            stateExists=self.state.exists(), approvalExists=(self.state / 'approval.json').exists(),
            recoveryExists=(self.state / 'recovery.json').exists(),
            githubCalls=len(self.log.read_text(encoding='utf-8').splitlines()) if self.log.exists() else 0,
            brokerHandlerErrors=len(self.errors)))

    def github(self):
        events = [json.loads(line) for line in self.log.read_text(encoding='utf-8').splitlines()]
        for event in events:
            argv = event['argv']
            self.case.assertEqual(argv[:3], ['api', '--hostname', 'github.com'])
            self.case.assertNotIn('/environments', ' '.join(argv))
            self.case.assertNotIn('PATCH', argv)
            self.case.assertNotIn('s' * 43, json.dumps(event))
            recovery_path = self.state / 'recovery.json'
            if recovery_path.exists() and self.read('recovery.json')['secret']:
                self.case.assertNotIn(self.read('recovery.json')['secret'], json.dumps(event))
        return events


class HostEnrollmentTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        if not shutil.which('pwsh'):
            raise RuntimeError('Enrollment acceptance requires PowerShell 7 on PATH')
        if not shutil.which('dotnet'):
            raise RuntimeError('The native gh fixture requires the .NET 10 SDK')
        temporary = tempfile.TemporaryDirectory(prefix='enrollment gh fixture ')
        cls.addClassCleanup(temporary.cleanup)
        build = Path(temporary.name)
        shutil.copytree(Path(__file__).with_name('GhFixture'), build / 'project',
                        ignore=shutil.ignore_patterns('bin', 'obj'))
        # Build outside the repository's unrelated SDK pin.
        (build / 'global.json').write_text(json.dumps({'sdk': {
            'version': '10.0.100', 'rollForward': 'latestFeature', 'allowPrerelease': False}}),
            encoding='utf-8')
        cls.fixture_build = build / 'output'
        result = subprocess.run(['dotnet', 'build', str(build / 'project/GhFixture.csproj'),
                                 '--output', str(cls.fixture_build), '--nologo',
                                 '--verbosity', 'quiet', '--ignore-failed-sources'],
                                cwd=build, capture_output=True, text=True, encoding='utf-8', timeout=120)
        if result.returncode:
            raise RuntimeError('Native gh fixture build failed:\n' + result.stdout + result.stderr)

    def test_preview_identifies_selected_repository_agent_variable_without_mutations(self):
        with tempfile.TemporaryDirectory(prefix='enrollment preview ') as temporary:
            directory = Path(temporary)
            for artifact in self.fixture_build.iterdir():
                shutil.copy2(artifact, directory / artifact.name)
            scenario = directory / 'scenario.json'
            # These are external API responses under an authenticated host credential,
            # not broker responses or enrollment decisions. Preview may remain offline.
            scenario.write_text(json.dumps([
                {'body': {'id': 123, 'full_name': 'example/project'}},
                {'body': {'total_count': 0, 'variables': []}},
            ]), encoding='utf-8')
            log = directory / 'requests.jsonl'
            state = directory / 'private-state'
            environment = dict(os.environ, PATH=str(directory) + os.pathsep + os.environ['PATH'],
                               REVOKE_GH_SCENARIO=str(scenario), REVOKE_GH_LOG=str(log),
                               REVOKE_GH_PYTHON=sys.executable,
                               REVOKE_GH_FIXTURE=str(Path(__file__).with_name('gh_fixture.py').resolve()),
                               GH_TOKEN='HOST-CREDENTIAL-MUST-STAY-ON-HOST', PYTHONUTF8='1')
            expected_gh = directory / ('gh.exe' if os.name == 'nt' else 'gh')
            self.assertEqual(Path(shutil.which('gh', path=environment['PATH']) or '').resolve(),
                             expected_gh.resolve(), 'Fixture must take precedence over host gh')
            result = subprocess.run([
                'pwsh', '-NoLogo', '-NoProfile', '-File', str(ROOT / 'install.ps1'),
                '-Action', 'Enroll', '-NonInteractive', '-Repository', 'example/project',
                '-BrokerOrigin', 'https://broker.example.test',
                '-ApprovalVariable', 'TEAM_CONTROLLER_APPROVAL',
                '-EnrollmentProfile', 'polling', '-StateDirectory', str(state),
            ], cwd=directory, env=environment, input='', capture_output=True, text=True,
                encoding='utf-8', timeout=30)
            output = result.stdout + result.stderr
            self.assertEqual(result.returncode, 0, output)
            self.assertFalse(state.exists(), 'Preview must not allocate private enrollment state')
            self.assertNotIn(environment['GH_TOKEN'], output)
            events = [json.loads(line) for line in log.read_text(encoding='utf-8').splitlines()] \
                if log.exists() else []
            for event in events:
                argv = event['argv']
                self.assertEqual(argv[0], 'api', event)
                self.assertEqual(argv[argv.index('--method') + 1], 'GET', event)
                self.assertNotIn('/environments', argv[-1], event)
            self.assertIn('TEAM_CONTROLLER_APPROVAL', output)
            self.assertRegex(output, r'(?i)agent[- ]variable',
                             'Preview must identify the repository Agent-variable approval coordinate')
            self.assertNotIn('GitHub Environment', output)

    def test_create_and_saved_resume_preserve_exact_marker_and_private_receipt(self):
        journey = EnrollmentJourney(self)
        result = journey.run()
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(journey.errors, [])
        approval = journey.read('approval.json')
        self.assertEqual(approval['marker'], journey.marker)
        self.assertEqual(approval['expectedValueSha256'], hashlib.sha256(journey.marker.encode()).hexdigest())
        self.assertIsNone(journey.read('recovery.json')['secret'])
        first_events = list(journey.events)
        result = journey.run(saved=True)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(journey.errors, [])
        self.assertEqual(len(journey.events), len(first_events) + 1)
        self.assertEqual(journey.events[-1], first_events[-1])
        writes = [event for event in journey.github() if 'POST' in event['argv']]
        self.assertEqual([event['body'] for event in writes], [{'name': journey.variable, 'value': journey.marker}])
        result = journey.run(saved=True, action='Revoke', extra=[
            '-ApprovalStatePath', str(journey.state / 'approval.json'),
            '-EnrollmentFile', str(journey.state / 'enrollment.json')])
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        outcome = json.loads(next(line for line in result.stdout.splitlines() if line.startswith('{')))
        self.assertEqual((outcome['githubMarker'], outcome['brokerClosure'], outcome['outstandingTokens']),
                         ('removed', 'confirmed', 'unsettled'))
        self.assertNotIn('environmentId', outcome)
        self.assertEqual(sum('DELETE' in event['argv'] for event in journey.github()), 1)
        self.assertEqual(journey.errors, [])

    def test_legacy_saved_enrollment_is_refused_without_remote_work_or_file_changes(self):
        journey = EnrollmentJourney(self)
        journey.state.mkdir(mode=0o700)
        legacy = dict(version=1, repository='example/project', repositoryId=123, brokerOrigin=journey.origin,
            repoEnvironment='repoagent', environmentId=456, approvalVariable=journey.variable, profile='polling',
            pendingId=None, deadline=None, marker=None, expectedValueSha256=None,
            approvalWriteAttempted=False, approvalObserved=False)
        path = journey.state / 'approval.json'
        if os.name == 'nt':
            # Creation uses a separate public preview journey's native filesystem
            # setup helper solely to arrange the complete historical private file.
            setup = '''Import-Module $env:HOST_ENROLLMENT_MODULE -Force
            $null=Initialize-PrivateDirectory $env:HOST_ENROLLMENT_STATE
            Write-PrivateJson (Join-Path $env:HOST_ENROLLMENT_STATE 'approval.json') (Read-HostJson $env:HOST_LEGACY_JSON)'''
            # Remove inherited directory so the real helper can create its DACL.
            journey.state.rmdir()
            result = subprocess.run(['pwsh', '-NoProfile', '-Command', setup], capture_output=True, text=True,
                env=dict(journey.environment, HOST_ENROLLMENT_MODULE=str(ROOT / 'scripts/host-enrollment.psm1'),
                         HOST_ENROLLMENT_STATE=str(journey.state), HOST_LEGACY_JSON=json.dumps(legacy)), timeout=20)
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        else:
            path.write_text(json.dumps(legacy), encoding='utf-8')
            path.chmod(0o600)
        original = path.read_bytes()
        result = journey.run(saved=True)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('Legacy Environment enrollment cannot resume', result.stdout)
        self.assertEqual(path.read_bytes(), original)
        self.assertEqual(journey.events, [])
        self.assertFalse(journey.log.exists())

    def test_marker_removal_reports_unconfirmed_optional_self_revoke_separately(self):
        journey = EnrollmentJourney(self, fault='revoke-unavailable')
        result = journey.run()
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        result = journey.run(saved=True, action='Revoke', extra=[
            '-ApprovalStatePath', str(journey.state / 'approval.json'),
            '-EnrollmentFile', str(journey.state / 'enrollment.json')])
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        outcome = json.loads(next(line for line in result.stdout.splitlines() if line.startswith('{')))
        self.assertEqual((outcome['githubMarker'], outcome['brokerClosure'], outcome['outstandingTokens']),
                         ('removed', 'unconfirmed', 'unsettled'))
        self.assertEqual(sum('DELETE' in event['argv'] for event in journey.github()), 1)

    def test_default_name_matching_approval_and_lost_ack_resume_without_new_setup(self):
        journey = EnrollmentJourney(self, variable='APPROVED_CONTROLLER', fault='lost-ack')
        journey.model.write_text(json.dumps({'variables': {journey.variable: journey.marker}}), encoding='utf-8')
        result = journey.run()
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(len(journey.events), 3)
        before = journey.read('recovery.json')
        self.assertTrue(before['ackAttempted'])
        result = journey.run(saved=True)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(len(journey.events), 4)
        self.assertEqual(journey.events[-2], journey.events[-1])
        self.assertFalse(any('POST' in event['argv'] for event in journey.github()))
        self.assertEqual(journey.errors, [])

    def test_conflicting_approval_stops_without_replacement(self):
        journey = EnrollmentJourney(self)
        journey.model.write_text(json.dumps({'variables': {journey.variable: 'another grant'}}), encoding='utf-8')
        result = journey.run()
        self.assertNotEqual(result.returncode, 0)
        diagnostic = journey.diagnostic(result)
        self.assertEqual(len(journey.events), 1, diagnostic)
        self.assertIn('Approval conflicts with this enrollment. Nothing was replaced; resolve old authority manually.',
                      result.stdout, diagnostic)
        self.assertFalse(any('POST' in event['argv'] for event in journey.github()))
        self.assertFalse((journey.state / 'enrollment.json').exists())

    def test_denied_or_incomplete_discovery_does_not_start_broker(self):
        for model in ({'variables': {}, 'listing_status': 403},
                      {'variables': {}, 'listing': {'total_count': 1, 'variables': []}}):
            with self.subTest(model=model):
                journey = EnrollmentJourney(self)
                journey.model.write_text(json.dumps(model), encoding='utf-8')
                result = journey.run()
                self.assertNotEqual(result.returncode, 0)
                self.assertEqual(journey.events, [])
                self.assertFalse((journey.state / 'recovery.json').exists())
                self.assertFalse(any('POST' in event['argv'] for event in journey.github()))

    def test_multi_page_nonappearance_cannot_create_approval(self):
        journey = EnrollmentJourney(self)
        entries = [{'name': f'OTHER_{index}', 'value': 'public'} for index in range(30)]
        journey.model.write_text(json.dumps({'variables': {}, 'listing': {
            'total_count': 31, 'variables': entries}}), encoding='utf-8')
        result = journey.run()
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('unverifiable', result.stdout)
        self.assertEqual(len(journey.events), 1)
        self.assertFalse(any('POST' in event['argv'] for event in journey.github()))

    def test_lost_start_or_completion_replays_same_private_identity_and_deadline(self):
        for fault in ('lost-start', 'lost-completion'):
            with self.subTest(fault=fault):
                journey = EnrollmentJourney(self, fault=fault)
                result = journey.run()
                # HttpClient may safely repeat a bodyless completion POST after
                # an empty lost response. Protect identity rather than requiring
                # that an invocation fail; the real broker owns its durable floor.
                self.assertIn(result.returncode, (0, 1), result.stdout + result.stderr)
                original_request = journey.read('recovery.json')['request']
                first_receipt = journey.read('enrollment.json') \
                    if (journey.state / 'enrollment.json').exists() else None
                first_completion = journey.read('recovery.json')['completionId']
                result = journey.run(saved=True)
                self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
                starts = [event for event in journey.events if event['path'] == '/v2/enrollments']
                self.assertIn(len(starts), (1, 2))
                self.assertTrue(all(event == starts[0] for event in starts))
                self.assertEqual(original_request, journey.read('recovery.json')['request'])
                if first_receipt:
                    self.assertEqual(first_receipt, journey.read('enrollment.json'))
                    self.assertEqual(first_completion, journey.read('recovery.json')['completionId'])
                self.assertEqual(journey.read('enrollment.json')['deviceId'], 'd' * 32)
                self.assertEqual(journey.read('recovery.json')['completionId'], 'a' * 32)
                self.assertEqual(journey.read('approval.json')['deadline'], journey.deadline)
                self.assertEqual(journey.errors, [])
                self.assertEqual(sum('POST' in event['argv'] for event in journey.github()), 1)

    def test_uncertain_write_then_absence_is_never_restored(self):
        journey = EnrollmentJourney(self)
        journey.model.write_text(json.dumps({'variables': {}, 'write_status': 500}), encoding='utf-8')
        result = journey.run()
        self.assertNotEqual(result.returncode, 0)
        self.assertTrue(journey.read('approval.json')['approvalWriteAttempted'])
        # An independent writer removes a possibly successful write before retry.
        journey.model.write_text(json.dumps({'variables': {}}), encoding='utf-8')
        result = journey.run(saved=True)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('will not be restored', result.stdout)
        self.assertEqual(sum('POST' in event['argv'] for event in journey.github()), 1)
        self.assertFalse((journey.state / 'enrollment.json').exists())

    def test_wrong_marker_binding_and_redirect_stop_before_approval_write(self):
        for fault in ('wrong-binding', 'duplicate', 'unknown-environment', 'wrong-scalar', 'redirect'):
            with self.subTest(fault=fault):
                journey = EnrollmentJourney(self, fault=fault)
                if fault == 'wrong-binding':
                    marker = json.loads(journey.marker)
                    marker['repositoryId'] = 999
                    journey.marker = json.dumps(marker)
                elif fault == 'duplicate':
                    journey.marker = journey.marker.replace('"version":2', '"version":2,"version":2')
                elif fault == 'unknown-environment':
                    marker = json.loads(journey.marker)
                    marker['environment'] = 'repoagent'
                    journey.marker = json.dumps(marker)
                elif fault == 'wrong-scalar':
                    marker = json.loads(journey.marker)
                    marker['installationId'] = '789'
                    journey.marker = json.dumps(marker)
                result = journey.run()
                self.assertNotEqual(result.returncode, 0)
                self.assertEqual(len(journey.events), 1, 'Redirect must not forward recovery credentials')
                self.assertFalse(any('POST' in event['argv'] for event in journey.github()))
                self.assertFalse((journey.state / 'enrollment.json').exists())

    def test_nonprivate_existing_state_directory_stops_before_github_or_broker(self):
        journey = EnrollmentJourney(self)
        journey.state.mkdir(mode=0o755)
        if os.name != 'nt':
            journey.state.chmod(0o755)
        result = journey.run()
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(journey.events, [])
        self.assertFalse(journey.log.exists())
        self.assertFalse((journey.state / 'recovery.json').exists())


if __name__ == '__main__':
    unittest.main()
