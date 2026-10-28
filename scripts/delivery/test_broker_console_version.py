"""Fixed version-only operator CLI at the real POSIX PTY/process boundary."""
import json
import os
from pathlib import Path
import unittest
from scripts.delivery import test_broker_diagnostics as harness

GROUP, SECRET = harness.GROUP, harness.SECRET

SCRIPT = Path(__file__).with_name('broker-console-version.py')
APP = GROUP + '/providers/Microsoft.App/containerApps/repoagent-auth'
REVISION = 'repoagent-auth--ready'
REPLICA = REVISION + '-fixture'
IMAGE = 'ghcr.io/repoagent/controller-auth@sha256:'+'b'*64


@unittest.skipIf(os.name == 'nt', 'The version probe requires Linux PTY transport')
class BrokerConsoleVersionTests(unittest.TestCase):
    def invoke(self, change=None, **environment):
        def setup(fixtures, job):
            app = fixtures[APP]['properties']
            app['template']['containers'][0]['name'] = 'auth-service'
            fixtures[APP+'/revisions/'+REVISION] = dict(id=APP+'/revisions/'+REVISION, name=REVISION,
                properties=dict(active=True, healthState='Healthy', template=app['template']))
            fixtures[APP+'/revisions/'+REVISION+'/replicas/'+REPLICA] = dict(
                id=APP+'/revisions/'+REVISION+'/replicas/'+REPLICA, name=REPLICA,
                properties=dict(runningState='Running', containers=[dict(name='auth-service',ready=True,runningState='Running')]))
            if change:
                change(fixtures)
        values = dict(AUTH_EXPECTED_IMAGE=IMAGE, AUTH_EXPECTED_REVISION=REVISION, AUTH_EXPECTED_REPLICA=REPLICA)
        values.update(environment)
        return harness.BrokerDiagnosticsTests.invoke(self, setup, script=SCRIPT, report_name='version-probe.json', **values)

    def test_fixed_version_command_observes_only_metadata_using_terminal_stdin(self):
        result, report, calls, output = self.invoke()
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(report['state'], 'observed')
        self.assertEqual(report['metadata'], dict(component='broker',version='1.0.0',source='b'*40))
        self.assertFalse(report['remoteExitStatusEstablished'])
        self.assertFalse(report['settlementProven'])
        self.assertNotIn(SECRET, json.dumps(report)+result.stdout+result.stderr)
        console = [call for call in calls if call[:3] == ['az','containerapp','exec']]
        self.assertEqual(console, [['az','containerapp','exec','--subscription','46f7e5d6-05f3-435b-8089-6c63a203dbfa',
            '--resource-group','rg-repoagent-controller-auth','--name','repoagent-auth','--revision',REVISION,
            '--replica',REPLICA,'--container','auth-service','--command',
            'dotnet /app/RepoAgent.Auth.dll --version --json','--only-show-errors']])
        self.assertEqual(sorted(path.name for path in output.iterdir()), ['version-probe.json'])

    def test_wrong_source_or_caller_selected_target_is_refused_before_console(self):
        for environment in (dict(GITHUB_SHA='c'*40), dict(GITHUB_REF='refs/heads/other'),
                dict(AZURE_SUBSCRIPTION_ID='other'), dict(AUTH_EXPECTED_IMAGE=IMAGE+';id'),
                dict(AUTH_EXPECTED_REVISION='other--revision'), dict(AUTH_EXPECTED_REPLICA=REPLICA+'/other')):
            with self.subTest(environment=environment):
                result, report, calls, _ = self.invoke(**environment)
                self.assertNotEqual(result.returncode, 0)
                self.assertIsNone(report)
                self.assertEqual(calls, [])

    def test_unowned_stale_unready_or_different_runtime_prevents_console(self):
        for location, field, value in (
                ('app','id','/another/app'), ('app','latestReadyRevisionName','repoagent-auth--other'),
                ('app','image',IMAGE.replace('b'*64,'c'*64)), ('revision','image',IMAGE.replace('b'*64,'c'*64)),
                ('revision','active',False), ('replica','id','/another/replica'), ('replica','ready',False),
                ('replica','name','other-container')):
            with self.subTest(location=location, field=field):
                def mismatch(fixtures):
                    key = APP if location == 'app' else APP+'/revisions/'+REVISION
                    if location == 'replica': key += '/replicas/'+REPLICA
                    target = fixtures[key]
                    if field == 'id': target['id'] = value
                    elif field in ('image','name','ready'):
                        containers = target['properties']['containers'] if location == 'replica' else target['properties']['template']['containers']
                        containers[0][field] = value
                    else: target['properties'][field] = value
                result, report, calls, _ = self.invoke(mismatch)
                self.assertEqual(result.returncode, 1)
                self.assertEqual(report['state'], 'unavailable')
                self.assertNotIn('metadata', report)
                self.assertFalse(any(call[:3] == ['az','containerapp','exec'] for call in calls))

    def test_failed_oversized_ambiguous_or_private_console_output_is_not_exported(self):
        for mode in ('denied','overflow','ambiguous','private-json','drift'):
            with self.subTest(mode=mode):
                result, report, _, _ = self.invoke(lambda fixtures: fixtures.update(consoleMode=mode))
                self.assertEqual(result.returncode, 1)
                self.assertEqual(report['state'], 'unavailable')
                self.assertNotIn('metadata', report)
                self.assertNotIn(SECRET, json.dumps(report)+result.stdout+result.stderr)

    def test_console_without_completion_times_out_instead_of_claiming_observed_version(self):
        result, report, _, _ = self.invoke(lambda fixtures: fixtures.update(consoleMode='hang'))
        self.assertEqual(result.returncode, 1)
        self.assertEqual(report['reason'], 'console-timeout')
        self.assertNotIn('metadata', report)
