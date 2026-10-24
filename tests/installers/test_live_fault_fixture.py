"""Live acceptance credential fault fixture (container/control.py revoke-credential, case C12).

The controller holds its installation token only in memory, so the fixture's fault is GitHub
revoking that live token. It is driven here against the real credential manager and its
handoff, with the controller's installed App gh wrapper and actual gh. GitHub is the HTTPS
fixture shared with the managed Git tests, which records the revocation request. This proves
the fixture's command path, not real GitHub revocation or the controller's later rejection.
"""
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

import test_managed_git as managed
from test_managed_git import CONTROLLER, ROOT, CredentialManager, GitProvider, persisted

CONTROL = ROOT / 'container/control.py'


@unittest.skipIf(os.name == 'nt', 'The credential handoff requires the Linux or macOS controller runtime')
class LiveFaultFixtureTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        # Builds the controller and the credential manager driver.
        managed.ManagedGitTests.setUpClass.__func__(cls)

    def revoke(self, state, environment):
        return subprocess.run([sys.executable, str(CONTROL), 'revoke-credential', str(state)], env=environment,
                              capture_output=True, text=True, timeout=60)

    def test_revoke_credential_revokes_only_the_running_controllers_in_memory_token(self):
        with tempfile.TemporaryDirectory(prefix='live fault ') as temporary:
            directory = Path(temporary)
            fixture = GitProvider(directory)
            self.addCleanup(fixture.close)
            # Ambient GH_TOKEN stands for a human credential the fixture must never fall back to.
            environment = fixture.environment()
            enrollment = directory / 'enrollment.json'
            enrollment.write_text(json.dumps(fixture.enrollment()))
            enrollment.chmod(0o600)
            state = directory / 'bridge.json'
            check = subprocess.run(['dotnet', str(CONTROLLER), '--repo', 'owner/project', '--app-id', '123',
                                    '--broker-url', 'https://broker.fixture', '--enrollment-file', str(enrollment),
                                    '--state', str(state), '--clone', '--check-auth'],
                                   env=environment, capture_output=True, text=True, timeout=120)
            self.assertEqual(check.returncode, 0, check.stdout + check.stderr)
            credential = Path(str(state) + '.github-app')

            stopped = self.revoke(state, environment)

            self.assertNotEqual(stopped.returncode, 0, 'Without a running controller there is no credential to revoke')
            self.assertEqual(fixture.revocations, [], 'The fixture never falls back to another credential')

            manager = CredentialManager(enrollment, credential, environment)
            try:
                result = self.revoke(state, environment)
            finally:
                manager.stop()

            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
            self.assertEqual(fixture.revocations, ['token lease-A'],
                             "GitHub receives one revocation of the running controller's current token")
            self.assertNotIn('lease-A', result.stdout + result.stderr)
            self.assertEqual(persisted([credential, state], 'lease-A'), [])


if __name__ == '__main__':
    unittest.main()
