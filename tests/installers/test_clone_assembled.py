"""Current clone host → real HTTPS broker/Table/DP → actual controller consumer.

Caller supplies AUTH_TEST_TABLE_ENDPOINT and owns only that emulator. External
GitHub/signing transport uses the existing fixture; no deployed provider access.
"""
import json
import os
from pathlib import Path
import socket
import subprocess
import time
import unittest

import test_host_enrollment as host

ROOT = Path(__file__).resolve().parents[2]


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
        journey = host.EnrollmentJourney(self, profile='polling-clone')
        directory = journey.directory
        subprocess.run(['openssl', 'pkcs12', '-export', '-out', str(directory / 'cert.pfx'),
                        '-inkey', str(directory / 'key.pem'), '-in', str(directory / 'cert.pem'),
                        '-passout', 'pass:test-only'], check=True, capture_output=True)
        with socket.socket() as selection:
            selection.bind(('127.0.0.1', 0))
            port = selection.getsockname()[1]
        journey.origin = f'https://localhost:{port}'
        table = 'cloneassembled' + os.urandom(10).hex()
        command = ['dotnet', str(ROOT / 'tests/installers/BrokerFixture/bin/Release/net10.0/BrokerFixture.dll'),
                   str(directory), os.environ['AUTH_TEST_TABLE_ENDPOINT'], table, str(port), 'create', 'no-ack-fault']
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
            self.assertEqual(receipt['policyVersion'], 'repository-agent-clone-approval-v2')
            journey.private()
            self.assertTrue(any((directory / 'keys').glob('*.xml')))
            self.assertTrue(journey.read('recovery.json')['acknowledged'])
            # Completion used the remaining repository sign in its minute.
            # Observe actual quota metadata and cross only that legitimate window.
            snapshots = [event for event in events() if event.get('quotaObservation') and event['partition'] == 'repository:123']
            if snapshots[-1]['counters']['Signs'] >= 2:
                cross_observed_window()
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
            self.assertEqual(scopes, [{'agent_variables': 'read'}, {'discussions': 'write', 'contents': 'read'}])
        finally:
            if broker.poll() is None:
                broker.communicate('clean\n', timeout=20)
                self.assertEqual(broker.returncode, 0, output.read_text())
            if broker.stdin:
                broker.stdin.close()


if __name__ == '__main__':
    unittest.main()
