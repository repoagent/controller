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
    def invoke(self, change=None, script=SCRIPT, report_name='version-probe.json', **environment):
        def setup(fixtures, job):
            app = fixtures[APP]['properties']
            app['template']['containers'][0]['name'] = 'auth-service'
            fixtures[APP+'/revisions/'+REVISION] = dict(id=APP+'/revisions/'+REVISION, name=REVISION,
                properties=dict(active=True, healthState='Healthy', template=app['template']))
            fixtures[APP+'/revisions/'+REVISION+'/replicas/'+REPLICA] = dict(
                id=APP+'/revisions/'+REVISION+'/replicas/'+REPLICA, name=REPLICA,
                properties=dict(runningState='Running', containers=[dict(name='auth-service',ready=True,runningState='Running')]))
            fixtures[APP+'/revisions/'+REVISION+'/replicas'] = dict(value=[
                fixtures[APP+'/revisions/'+REVISION+'/replicas/'+REPLICA]])
            fixtures['repos/repoagent/controller/releases/tags/broker-v1.0.0'] = dict(
                tag_name='broker-v1.0.0',draft=False,prerelease=False,assets=[dict(id=7,name='broker-release.json')])
            fixtures['repos/repoagent/controller/commits/broker-v1.0.0'] = dict(sha='b'*40)
            fixtures['repos/repoagent/controller/releases/assets/7'] = dict(schema='repoagent.broker-release/v1',
                component='broker',tag='broker-v1.0.0',version='1.0.0',source='b'*40,image=IMAGE,
                imageTag='ghcr.io/repoagent/controller-auth:broker-v1.0.0',
                run='https://github.com/repoagent/controller/actions/runs/42')
            if change:
                change(fixtures)
        values = dict(AUTH_EXPECTED_IMAGE=IMAGE, AUTH_EXPECTED_REVISION=REVISION, AUTH_EXPECTED_REPLICA=REPLICA,
                      AUTH_RELEASE_TAG='broker-v1.0.0')
        values.update(environment)
        return harness.BrokerDiagnosticsTests.invoke(self, setup, script=script, report_name=report_name, **values)

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
                dict(AUTH_EXPECTED_REVISION='other--revision'), dict(AUTH_EXPECTED_REPLICA=REPLICA+'/other'),
                dict(AUTH_RELEASE_TAG='broker-v0.9.0')):
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

    def test_release_binding_and_unique_replica_are_required_before_any_console(self):
        for mode in ('draft','prerelease','asset-absent','image','source','multiple','empty'):
            with self.subTest(mode=mode):
                def change(fixtures):
                    release = fixtures['repos/repoagent/controller/releases/tags/broker-v1.0.0']
                    asset = fixtures['repos/repoagent/controller/releases/assets/7']
                    if mode in ('draft','prerelease'): release[mode] = True
                    elif mode == 'asset-absent': release['assets'] = []
                    elif mode == 'image': asset['image'] = IMAGE.replace('b'*64,'c'*64)
                    elif mode == 'source': asset['source'] = 'c'*40
                    else:
                        rows = fixtures[APP+'/revisions/'+REVISION+'/replicas']['value']
                        rows[:] = rows*2 if mode == 'multiple' else []
                result, report, calls, _ = self.invoke(change)
                self.assertEqual(result.returncode, 1)
                self.assertNotIn('metadata', report)
                self.assertFalse(any(call[:3] == ['az','containerapp','exec'] for call in calls))

    def test_unique_replica_can_be_discovered_without_caller_entering_its_name(self):
        result, report, _, _ = self.invoke(AUTH_EXPECTED_REPLICA='')
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(report['target']['replica'], REPLICA)

    def test_console_metadata_must_match_the_published_version_and_source(self):
        def wrong_contract(fixtures):
            fixtures['repos/repoagent/controller/releases/assets/7']['source'] = 'c'*40
            fixtures['repos/repoagent/controller/commits/broker-v1.0.0']['sha'] = 'c'*40
        result, report, _, _ = self.invoke(wrong_contract)
        self.assertEqual(result.returncode, 1)
        self.assertNotIn('metadata', report)

    def test_nonzero_cli_exit_still_withholds_even_valid_version_metadata(self):
        result, report, _, _ = self.invoke(lambda fixtures: fixtures.update(consoleExitCode=1))
        self.assertEqual(result.returncode, 1)
        self.assertEqual(report['state'], 'unavailable')
        self.assertNotIn('metadata', report)
