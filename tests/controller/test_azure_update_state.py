"""Project-owned update/recovery state; real PowerShell, files and process locks."""
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
MODULE = ROOT / 'scripts/azure/update-state.psm1'
RESOURCE = '/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg/providers/Microsoft.App/containerApps/controller'
OLD = 'ghcr.io/repoagent/controller@sha256:' + 'a' * 64
NEW = 'ghcr.io/repoagent/controller@sha256:' + 'b' * 64


@unittest.skipUnless(shutil.which('pwsh'), 'PowerShell 7 host lifecycle')
class AzureUpdateStateTests(unittest.TestCase):
    def setUp(self):
        self.directory = Path(self.enterContext(tempfile.TemporaryDirectory()))
        self.state = self.directory / 'state'

    def command(self, body, *, resource=RESOURCE):
        def literal(value):
            return "'" + str(value).replace("'", "''") + "'"
        return ("$ErrorActionPreference='Stop'; Import-Module " + literal(MODULE) +
                ' -Force -DisableNameChecking; $c=Open-AzureUpdateState ' + literal(self.state) +
                ' ' + literal(resource) + '; try { ' + body + ' } finally { $c.Lock.Dispose() }')

    def run_state(self, body, *, resource=RESOURCE):
        return subprocess.run(['pwsh', '-NoLogo', '-NoProfile', '-Command', self.command(body, resource=resource)],
                              capture_output=True, text=True, timeout=15)

    @staticmethod
    def begin(candidate='controller--new'):
        return ("Begin-AzureImageUpdate $c '" + 'c' * 64 + "' 'controller--old' '" + OLD +
                "' '" + candidate + "' '" + NEW + "' | Out-Null; ")

    def test_pending_update_cannot_overwrite_rollback_authority(self):
        first = self.run_state(self.begin() + 'Set-AzureUpdatePhase $c draining | Out-Null')
        self.assertEqual(first.returncode, 0, first.stderr)
        before = (self.state / 'update.json').read_bytes()
        retry = self.run_state(self.begin('controller--another'))
        self.assertNotEqual(retry.returncode, 0, 'A pending update lost its original rollback authority')
        self.assertEqual((self.state / 'update.json').read_bytes(), before)

    def test_interrupted_update_requires_rollback_then_can_start_another_update(self):
        result = self.run_state(self.begin() + 'Set-AzureUpdatePhase $c draining | Out-Null')
        self.assertEqual(result.returncode, 0, result.stderr)
        invalid = self.run_state('Set-AzureUpdatePhase $c committed | Out-Null')
        self.assertNotEqual(invalid.returncode, 0)
        recovered = self.run_state('Set-AzureUpdatePhase $c rollback-draining | Out-Null; '
                                   'Set-AzureUpdatePhase $c rollback-ready | Out-Null; '
                                   'Set-AzureUpdatePhase $c rolled-back | Out-Null; ' +
                                   self.begin('controller--retry') + 'Read-AzureUpdateJournal $c | ConvertTo-Json -Compress')
        self.assertEqual(recovered.returncode, 0, recovered.stderr)
        record = json.loads(recovered.stdout)
        self.assertEqual(record['candidateRevision'], 'controller--retry')
        self.assertEqual(record['phase'], 'prepared')
        self.assertEqual(record['previousImage'], OLD)
        if os.name != 'nt':
            self.assertEqual(self.state.stat().st_mode & 0o777, 0o700)
            self.assertEqual((self.state / 'update.json').stat().st_mode & 0o777, 0o600)

    def test_another_process_cannot_acquire_the_selected_update_lock(self):
        owner = subprocess.Popen(['pwsh', '-NoLogo', '-NoProfile', '-Command',
                                  self.command("[Console]::WriteLine('locked'); [Console]::ReadLine() | Out-Null")],
                                 stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
        try:
            self.assertEqual(owner.stdout.readline().strip(), 'locked')
            conflict = self.run_state('')
            self.assertNotEqual(conflict.returncode, 0)
            self.assertFalse((self.state / 'update.json').exists())
        finally:
            owner.communicate('\n', timeout=15)
        released = self.run_state('')
        self.assertEqual(released.returncode, 0, released.stderr)

    def test_possible_launch_remains_recorded_through_rollback_and_process_restart(self):
        launched = self.run_state(self.begin() +
                                  "foreach($phase in @('draining','drained','launching','rollback-draining')) "
                                  '{ Set-AzureUpdatePhase $c $phase | Out-Null }')
        self.assertEqual(launched.returncode, 0, launched.stderr)
        resumed = self.run_state('Read-AzureUpdateJournal $c | ConvertTo-Json -Compress')
        self.assertEqual(resumed.returncode, 0, resumed.stderr)
        self.assertIs(json.loads(resumed.stdout).get('launchAttempted'), True,
                      'Rollback forgot that its remote copy may still exist')
        completed = self.run_state('Set-AzureUpdatePhase $c rollback-ready | Out-Null; '
                                   'Set-AzureUpdatePhase $c rolled-back | Out-Null; '
                                   'Read-AzureUpdateJournal $c | ConvertTo-Json -Compress')
        self.assertEqual(completed.returncode, 0, completed.stderr)
        self.assertIs(json.loads(completed.stdout)['launchAttempted'], True)

    def test_different_app_cannot_adopt_the_recovery_journal(self):
        first = self.run_state(self.begin())
        self.assertEqual(first.returncode, 0, first.stderr)
        before = (self.state / 'update.json').read_bytes()
        conflict = self.run_state('', resource=RESOURCE.replace('/controller', '/other'))
        self.assertNotEqual(conflict.returncode, 0)
        self.assertEqual((self.state / 'update.json').read_bytes(), before)

    def test_invalid_new_selection_or_configuration_drift_preserves_committed_authority(self):
        first = self.run_state(self.begin() + "foreach($phase in @('draining','drained','launching','ready','committed')) "
                               '{ Set-AzureUpdatePhase $c $phase | Out-Null }')
        self.assertEqual(first.returncode, 0, first.stderr)
        before = (self.state / 'update.json').read_bytes()
        for body in (self.begin('other--new'), self.begin().replace(NEW, 'ghcr.io/repoagent/controller:latest'),
                     self.begin().replace('c' * 64, 'd' * 64)):
            with self.subTest(selection=body):
                result = self.run_state(body)
                self.assertNotEqual(result.returncode, 0)
                self.assertEqual((self.state / 'update.json').read_bytes(), before)


@unittest.skipUnless(shutil.which('pwsh'), 'PowerShell 7 lifecycle command')
class LifecycleCommandTests(unittest.TestCase):
    def test_failed_cli_cannot_appear_successful_or_disclose_its_response(self):
        with tempfile.TemporaryDirectory() as directory:
            script = Path(directory) / 'provider.py'
            script.write_text('import sys\nprint("PRIVATE_RESPONSE")\n'
                              'print("PRIVATE_DIAGNOSTIC", file=sys.stderr)\nsys.exit(int(sys.argv[1]))\n')
            def literal(value):
                return "'" + str(value).replace("'", "''") + "'"
            command = ("$ErrorActionPreference='Stop'; Import-Module " +
                       literal(ROOT / 'scripts/azure/lifecycle-command.psm1') +
                       ' -Force; Invoke-LifecycleCommand ' + literal(sys.executable) +
                       ' @(' + literal(script) + ", '{code}')")
            success = subprocess.run(['pwsh', '-NoLogo', '-NoProfile', '-Command', command.format(code=0)],
                                     capture_output=True, text=True, timeout=15)
            self.assertEqual(success.returncode, 0, success.stderr)
            self.assertIn('PRIVATE_RESPONSE', success.stdout)
            self.assertNotIn('PRIVATE_DIAGNOSTIC', success.stdout + success.stderr)
            failure = subprocess.run(['pwsh', '-NoLogo', '-NoProfile', '-Command', command.format(code=7)],
                                     capture_output=True, text=True, timeout=15)
            self.assertNotEqual(failure.returncode, 0, 'A failed provider request appeared successful')
            self.assertNotIn('PRIVATE_RESPONSE', failure.stdout + failure.stderr)
            self.assertNotIn('PRIVATE_DIAGNOSTIC', failure.stdout + failure.stderr)


if __name__ == '__main__':
    unittest.main()
