"""Opt-in real broker/Table/DP/native consumer journey; no live provider access.

Run with HOST_ENROLLMENT_ASSEMBLED_TABLE_ENDPOINT and REPOAGENT_TEST_NATIVE_CONTROLLER.
The caller owns Azurite lifecycle. Every run uses a unique disposable table.
"""
import hashlib
import json
import os
from pathlib import Path
import shutil
import socket
import ssl
import subprocess
import tempfile
import time
import unittest
import urllib.error
import urllib.request

import test_host_enrollment as process_tests

ROOT = Path(__file__).resolve().parents[2]
EXPECTED_NATIVE = '8e9024750a512414cb9aaa9d24e52bdf3cc0cf39035f5c248ebb40735077c7ea'


@unittest.skipUnless(os.environ.get('HOST_ENROLLMENT_ASSEMBLED_TABLE_ENDPOINT') and
                     os.environ.get('REPOAGENT_TEST_NATIVE_CONTROLLER'),
                     'Opt-in isolated Azurite endpoint and verified native consumer required')
class AssembledHostEnrollmentTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        process_tests.HostEnrollmentTests.setUpClass.__func__(cls)
        result = subprocess.run(['dotnet', 'build', str(ROOT / 'tests/installers/BrokerFixture/BrokerFixture.csproj'),
                                 '--nologo', '--verbosity', 'quiet', '--ignore-failed-sources'], cwd=ROOT,
                                capture_output=True, text=True, timeout=120)
        if result.returncode:
            raise RuntimeError(result.stdout + result.stderr)

    def test_real_broker_restart_recovers_receipt_and_unchanged_native_consumer(self):
        native = Path(os.environ['REPOAGENT_TEST_NATIVE_CONTROLLER'])
        self.assertEqual(hashlib.sha256(native.read_bytes()).hexdigest(), EXPECTED_NATIVE)
        # This artifact's exact source-input baseline is recorded by issue73.
        proof = subprocess.run(['git', 'diff', '--exit-code', 'dfd5617', '--', 'src/RepoAgentController',
                                'global.json', '.agents/skills/github-discussion'], cwd=ROOT, capture_output=True, text=True)
        self.assertEqual(proof.returncode, 0, 'Native source-input bytes differ: ' + proof.stdout)
        # Reuse only certificate/native-gh/private-path arrangement from process
        # fixture. Its separate mock broker is never the selected HTTPS origin.
        journey = process_tests.EnrollmentJourney(self)
        directory = journey.directory
        subprocess.run(['openssl', 'pkcs12', '-export', '-out', str(directory / 'cert.pfx'),
                        '-inkey', str(directory / 'key.pem'), '-in', str(directory / 'cert.pem'),
                        '-passout', 'pass:test-only'], check=True, capture_output=True)
        with socket.socket() as selection:
            selection.bind(('127.0.0.1', 0))
            port = selection.getsockname()[1]
        journey.origin = f'https://localhost:{port}'
        table = 'assembled' + os.urandom(12).hex()
        command = ['dotnet', str(ROOT / 'tests/installers/BrokerFixture/bin/Debug/net10.0/BrokerFixture.dll'),
                   str(directory), os.environ['HOST_ENROLLMENT_ASSEMBLED_TABLE_ENDPOINT'], table, str(port)]
        current = None
        output = directory / 'service-output.log'

        def start():
            nonlocal current
            stream = output.open('a', encoding='utf-8')
            current = subprocess.Popen(command, cwd=ROOT, stdin=subprocess.PIPE, stdout=stream, stderr=stream,
                                       text=True, env=journey.environment)
            stream.close()
            for _ in range(150):
                if 'READY' in output.read_text(encoding='utf-8'):
                    return
                if current.poll() is not None:
                    self.fail('Real broker failed startup: ' + output.read_text(encoding='utf-8'))
                time.sleep(0.1)
            self.fail('Real broker startup timed out: ' + output.read_text(encoding='utf-8'))

        def stop(clean=False):
            nonlocal current
            if current and current.poll() is None:
                current.communicate('clean\n' if clean else 'stop\n', timeout=20)
                self.assertEqual(current.returncode, 0, output.read_text(encoding='utf-8'))
            if current and current.stdin:
                current.stdin.close()
            current = None

        def events():
            return [json.loads(line) for line in (directory / 'broker.jsonl').read_text().splitlines()]

        def complete(secret, pending):
            request = urllib.request.Request(journey.origin + '/v2/enrollments/' + pending + '/completion',
                                             method='POST', headers={'Authorization': 'Bearer ' + secret})
            try:
                with urllib.request.urlopen(request, context=ssl.create_default_context(cafile=str(directory / 'cert.pem')),
                                            timeout=15) as response:
                    return response.status, json.load(response)
            except urllib.error.HTTPError as error:
                return error.code, None

        try:
            start()
            result = journey.run()
            self.assertNotEqual(result.returncode, 0, 'First ack transport is deliberately interrupted')
            self.assertTrue((journey.state / 'enrollment.json').exists(),
                result.stdout + result.stderr + output.read_text() + json.dumps(events()))
            receipt = journey.read('enrollment.json')
            recovery = journey.read('recovery.json')
            approval = journey.read('approval.json')
            self.assertTrue(recovery['ackAttempted'])
            self.assertEqual(approval['marker'], json.loads(journey.model.read_text())['variables'][journey.variable])
            marker = json.loads(approval['marker'])
            self.assertEqual((marker['version'], marker['approvalKind'], receipt['protocolVersion']),
                             (2, 'repository-agent-variable', 1))
            journey.private()
            stop()
            self.assertTrue(any((directory / 'keys').glob('*.xml')), 'Real persisted Data Protection keyring required')
            output.write_text('', encoding='utf-8')
            start()
            status, delivered = complete(recovery['secret'], approval['pendingId'])
            self.assertEqual(status, 200)
            self.assertEqual(delivered, {'completionId': recovery['completionId'], 'enrollment': receipt})
            provider_before = sum('provider' in event for event in events())
            status, _ = complete(recovery['secret'], approval['pendingId'])
            self.assertEqual(status, 429, 'Immediate physical completion replay must hit durable work floor')
            self.assertEqual(sum('provider' in event for event in events()), provider_before)
            time.sleep(5.1)
            result = journey.run(saved=True)
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr + output.read_text())
            self.assertEqual(journey.read('enrollment.json'), receipt)
            self.assertEqual(journey.read('recovery.json')['completionId'], recovery['completionId'])
            self.assertIsNone(journey.read('recovery.json')['secret'])
            self.assertEqual(journey.read('approval.json')['deadline'], approval['deadline'])
            self.assertEqual(sum('POST' in event['argv'] for event in journey.github()), 1)
            time.sleep(5.1)
            consumer_log = directory / 'consumer-gh.jsonl'
            result = subprocess.run([str(native), '--repo', 'example/project', '--app-id', '5387625',
                                     '--broker-url', journey.origin, '--enrollment-file', str(journey.state / 'enrollment.json'),
                                     '--state', str(directory / 'consumer-state.json'), '--check-auth'],
                                    env=dict(journey.environment, HOST_ENROLLMENT_NATIVE_CONSUMER='1',
                                             REVOKE_GH_LOG=str(consumer_log)), cwd=directory,
                                    capture_output=True, text=True, timeout=45)
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr + output.read_text())
            self.assertIn('repo-agent-connector[bot]', result.stdout)
            self.assertTrue(json.loads(consumer_log.read_text().splitlines()[0])['runtimeCredentialObserved'])
            recorded = events()
            self.assertTrue(any(event.get('path', '').endswith('/installation-token') and event['status'] == 200 for event in recorded))
            scopes = [event['permissions'] for event in recorded if 'permissions' in event]
            self.assertEqual(scopes, [{'agent_variables': 'read'}, {'discussions': 'write'}])
            self.assertTrue(any(event.get('receiptBeforeAck') for event in recorded))
            print('ASSEMBLED native SHA256=' + EXPECTED_NATIVE + ' table=' + table +
                  ' brokerRestart=true durableFloor=429 providerReplayWork=0 scopes=' + json.dumps(scopes))
            print('ASSEMBLED broker events=' + json.dumps(recorded))
        finally:
            stop(clean=True)


if __name__ == '__main__':
    unittest.main()
