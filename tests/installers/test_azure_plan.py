"""Planning-only subprocess evidence; Azure is replaced, PowerShell is real."""
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
SUB = '11111111-1111-1111-1111-111111111111'
OTHER = '22222222-2222-2222-2222-222222222222'
BASE = f'/subscriptions/{SUB}/resourceGroups/rg'
ENV = BASE + '/providers/Microsoft.App/managedEnvironments/env'
APP = BASE + '/providers/Microsoft.App/containerApps/repoagent-123'
REG = BASE + '/providers/Microsoft.ContainerRegistry/registries/regis'
CONTROLLER = '33333333-3333-3333-3333-333333333333'
FIXTURE = '''#!/usr/bin/env python3
import json, os, sys, time
args = sys.argv[1:]
with open(os.environ['AZ_PLAN_LOG'], 'a') as out: out.write(json.dumps(args) + '\\n')
responses = json.load(open(os.environ['AZ_PLAN_RESPONSES']))
key = ' '.join(args[:3]) if args[:2] == ['containerapp', 'env'] else ' '.join(args[:2])
r = responses[key]
if r.get('sleep'): time.sleep(r['sleep'])
print(r.get('raw', json.dumps(r.get('body'))))
print(r.get('error', ''), file=sys.stderr)
sys.exit(r.get('code', 0))
'''


def missing():
    return {'code': 3, 'error': 'ERROR: (ResourceNotFound) Not found'}


def scenario():
    return {'account show': {'body': {'id': SUB, 'state': 'Enabled'}},
            'provider show': {'body': {'registrationState': 'Registered'}},
            'group show': {'body': {'id': BASE}},
            'containerapp env show': {'body': {'id': ENV, 'location': 'westeurope',
                'properties': {'provisioningState': 'Succeeded'}}},
            'containerapp show': missing(),
            'acr show': {'body': {'id': REG, 'loginServer': 'regis.azurecr.io'}}}


@unittest.skipUnless(shutil.which('pwsh'), 'PowerShell subprocess fixture')
class AzurePlanTests(unittest.TestCase):
    def run_plan(self, responses=None, extra=()):
        directory = Path(self.enterContext(tempfile.TemporaryDirectory(prefix='azure plan ')))
        fixture = directory / 'fixture.py'
        fixture.write_text(FIXTURE)
        if os.name == 'nt':
            az = directory / 'az.cmd'
            az.write_text('@"' + sys.executable + '" "' + str(fixture) + '" %*\n')
        else:
            az = directory / 'az'
            az.write_text(FIXTURE)
            az.chmod(0o755)
        path = directory / 'responses.json'
        path.write_text(json.dumps(responses or scenario()))
        log = directory / 'log'
        env = dict(os.environ, PATH=str(directory) + os.pathsep + os.environ['PATH'],
                   AZ_PLAN_RESPONSES=str(path), AZ_PLAN_LOG=str(log))
        args = ['pwsh', '-NoLogo', '-NoProfile', '-File', str(ROOT / 'scripts/plan-azure.ps1'),
                '-SubscriptionId', SUB, '-ResourceGroup', 'rg', '-Location', 'westeurope',
                '-EnvironmentName', 'env', '-RegistryName', 'regis', '-RepositoryId', '123',
                '-ControllerId', CONTROLLER, '-Image', 'regis.azurecr.io/controller@sha256:' + 'a' * 64]
        result = subprocess.run(args + list(extra), env=env, capture_output=True, text=True, timeout=20)
        calls = [json.loads(line) for line in log.read_text().splitlines()] if log.exists() else []
        for call in calls:
            self.assertEqual(call[call.index('--subscription') + 1], SUB)
            self.assertIn(tuple(call[:2]), {('account', 'show'), ('provider', 'show'),
                ('group', 'show'), ('containerapp', 'env'), ('containerapp', 'show'), ('acr', 'show')})
        return result, calls

    def test_missing_app_proposes_create_but_never_claims_hosted_readiness(self):
        result, calls = self.run_plan()
        self.assertEqual(result.returncode, 0, result.stderr)
        plan = json.loads(result.stdout)
        self.assertEqual(plan['resources'][-1]['action'], 'create')
        self.assertEqual(plan['resources'][1]['action'], 'compatible-reuse')
        self.assertFalse(plan['readyToApply'])
        self.assertIsNone(plan['origin']['verifiedExisting'])
        self.assertEqual(plan['desired']['storage']['status'], 'unverified-release-gate')
        self.assertEqual(len(calls), 6)

    def test_only_matching_ownership_can_propose_update(self):
        for owned in (False, True):
            with self.subTest(owned=owned):
                data = scenario()
                data['containerapp show'] = {'body': {'id': APP, 'location': 'westeurope',
                    'tags': {'repoagent-controller-id': CONTROLLER,
                             'repoagent-repository-id': '123' if owned else '456'},
                    'properties': {'managedEnvironmentId': ENV, 'configuration': {'ingress': {
                        'external': True, 'fqdn': 'repoagent-123.example.westeurope.azurecontainerapps.io'}}}}}
                result, _ = self.run_plan(data)
                self.assertEqual(result.returncode, 0, result.stderr)
                plan = json.loads(result.stdout)
                self.assertEqual(plan['resources'][-1]['action'],
                                 'verified-update' if owned else 'explicit-adoption-required')
                self.assertTrue(plan['origin']['verifiedExisting'].startswith('https://'))

    def test_discovery_errors_are_not_absence_and_provider_text_is_not_logged(self):
        for response, message in [
            ({'code': 1, 'error': 'ERROR: (AuthorizationFailed) PRIVATE'}, 'denied'),
            ({'code': 1, 'error': 'connection PRIVATE'}, 'failed'),
            ({'raw': '{PRIVATE'}, 'Malformed'),
            ({'body': {'id': ENV.replace(SUB, OTHER)}}, 'wrong-subscription'),
            ({'sleep': 3}, 'timed out')]:
            with self.subTest(message=message):
                data = scenario(); data['containerapp env show'] = response
                result, _ = self.run_plan(data, ['-RequestTimeoutSeconds', '1'])
                self.assertNotEqual(result.returncode, 0)
                self.assertIn(message, result.stderr)
                self.assertNotIn('PRIVATE', result.stdout + result.stderr)
                self.assertEqual(result.stdout, '')

    def test_incompatible_environment_and_conflicting_app_are_rejected(self):
        data = scenario(); data['containerapp env show']['body']['location'] = 'eastus'
        result, _ = self.run_plan(data)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('Incompatible', result.stderr)
        data = scenario(); data['containerapp show'] = {'body': {'id': APP,
            'location': 'westeurope', 'properties': {'managedEnvironmentId': ENV + '-other'}}}
        result, _ = self.run_plan(data)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('Conflicting', result.stderr)

    def test_invalid_name_and_mutable_image_fail_before_discovery(self):
        for extra in (['-AppName', 'INVALID'], ['-Image', 'regis.azurecr.io/controller:latest']):
            result, calls = self.run_plan(extra=extra)
            self.assertNotEqual(result.returncode, 0)
            self.assertEqual(calls, [])

    def test_unknown_or_duplicate_manifest_fields_fail_without_discovery(self):
        directory = Path(self.enterContext(tempfile.TemporaryDirectory()))
        manifest = directory / 'image.json'
        original = (ROOT / 'image/default.json').read_text()
        for content in (original.replace('"schemaVersion": 1', '"schemaVersion": 1, "secret": "PRIVATE"'),
                        original.replace('"schemaVersion": 1', '"schemaVersion": 1, "schemaVersion": 1')):
            manifest.write_text(content)
            result, calls = self.run_plan(extra=['-ImageManifest', str(manifest)])
            self.assertNotEqual(result.returncode, 0)
            self.assertEqual(calls, [])
            self.assertNotIn('PRIVATE', result.stderr + result.stdout)


if __name__ == '__main__':
    unittest.main()
