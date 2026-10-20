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
import json, os, subprocess, sys, time
args = sys.argv[1:]
with open(os.environ['AZ_PLAN_LOG'], 'a') as out: out.write(json.dumps(args) + '\\n')
responses = json.load(open(os.environ['AZ_PLAN_RESPONSES']))
key = ' '.join(args[:3]) if args[:2] == ['containerapp', 'env'] else ' '.join(args[:2])
r = responses[key]
if r.get('sleep'): time.sleep(r['sleep'])
if r.get('hold_pipes'):
    subprocess.Popen([sys.executable, '-c', 'import time; time.sleep(5)'])
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
    def run_plan(self, responses=None, extra=(), fixture_prefix='azure plan '):
        directory = Path(self.enterContext(tempfile.TemporaryDirectory(prefix=fixture_prefix)))
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
        options = {'-SubscriptionId': SUB, '-ResourceGroup': 'rg', '-Location': 'westeurope',
                   '-EnvironmentName': 'env', '-RegistryName': 'regis', '-RepositoryId': '123',
                   '-ControllerId': CONTROLLER,
                   '-Image': 'regis.azurecr.io/controller@sha256:' + 'a' * 64}
        options.update(zip(extra[::2], extra[1::2]))
        args = ['pwsh', '-NoLogo', '-NoProfile', '-File', str(ROOT / 'scripts/plan-azure.ps1')]
        for name, value in options.items():
            args.extend((name, value))
        result = subprocess.run(args, env=env, capture_output=True, text=True, timeout=20)
        calls = [json.loads(line) for line in log.read_text().splitlines()] if log.exists() else []
        for call in calls:
            self.assertEqual(call[call.index('--subscription') + 1], SUB)
            operation = tuple(call[:3] if call[:2] == ['containerapp', 'env'] else call[:2])
            self.assertIn(operation, {('account', 'show'), ('provider', 'show'),
                ('group', 'show'), ('containerapp', 'env', 'show'), ('containerapp', 'show'),
                ('acr', 'show')})
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

    def test_denial_cannot_be_overridden_by_a_not_found_error_line(self):
        data = scenario()
        data['containerapp env show'] = {'code': 1, 'error':
            'ERROR: (ResourceNotFound) PRIVATE\nERROR: (AuthorizationFailed) PRIVATE'}
        result, _ = self.run_plan(data)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('denied', result.stderr)
        self.assertEqual(result.stdout, '')
        self.assertNotIn('PRIVATE', result.stderr)

    def test_open_output_pipes_cannot_outlive_the_discovery_timeout(self):
        data = scenario()
        data['containerapp env show']['hold_pipes'] = True
        result, _ = self.run_plan(data, ['-RequestTimeoutSeconds', '1'])
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('timed out', result.stderr)
        self.assertEqual(result.stdout, '')

    def test_resource_group_argument_is_preserved_as_one_token(self):
        group = 'rg.with-(parentheses)'
        data = scenario()
        for response in data.values():
            if response.get('body', {}).get('id'):
                response['body']['id'] = response['body']['id'].replace('/rg', '/' + group)
        result, calls = self.run_plan(data, ['-ResourceGroup', group])
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(json.loads(result.stdout)['resourceGroup'], group)
        for call in calls:
            if call[:2] == ['group', 'show']:
                self.assertEqual(call[call.index('--name') + 1], group)
            elif '--resource-group' in call:
                self.assertEqual(call[call.index('--resource-group') + 1], group)

    @unittest.skipUnless(os.name == 'nt', 'native Windows batch launcher')
    def test_batch_launcher_rejects_shell_metacharacters_before_discovery(self):
        for character in ('%', '!', '&', '^'):
            with self.subTest(character=character):
                result, calls = self.run_plan(fixture_prefix='azure plan PRIVATE' + character)
                self.assertNotEqual(result.returncode, 0)
                self.assertIn('unsupported command characters', result.stderr)
                self.assertEqual(calls, [])
                self.assertEqual(result.stdout, '')
                self.assertNotIn('PRIVATE', result.stderr)

    def test_malformed_discovery_scalars_never_establish_ownership(self):
        cases = [
            ('account show', ('id',), []),
            ('account show', ('state',), ['Enabled']),
            ('provider show', ('registrationState',), []),
            ('group show', ('id',), [BASE]),
            ('containerapp env show', ('id',), None),
            ('containerapp env show', ('properties', 'provisioningState'), True),
            ('containerapp show', ('id',), [APP]),
            ('containerapp show', ('tags', 'repoagent-controller-id'), [CONTROLLER]),
            ('containerapp show', ('tags', 'repoagent-repository-id'), ['123', '456']),
            ('containerapp show', ('tags', 'repoagent-repository-id'), 123),
            ('containerapp show', ('tags',), []),
        ]
        for operation, field, value in cases:
            with self.subTest(operation=operation, field=field, value=value):
                data = scenario()
                data['containerapp show'] = {'body': {'id': APP, 'location': 'westeurope',
                    'tags': {'repoagent-controller-id': CONTROLLER,
                             'repoagent-repository-id': '123'},
                    'properties': {'managedEnvironmentId': ENV}}}
                target = data[operation]['body']
                for key in field[:-1]:
                    target = target[key]
                target[field[-1]] = value
                result, _ = self.run_plan(data)
                action = json.loads(result.stdout)['resources'][-1]['action'] if result.stdout else None
                self.assertNotEqual(result.returncode, 0, f'Malformed response produced {action}')
                self.assertEqual(result.stdout, '')

    def test_manifest_scalar_shapes_are_rejected_before_discovery(self):
        cases = [
            (('platform',), []),
            (('platform',), ['linux/amd64']),
            (('controller', 'source'), []),
            (('agent', 'profile'), ['codex']),
            (('controller', 'sha256'), []),
            (('agent', 'profile'), None),
            (('controller', 'source'), True),
            (('platform',), 7),
        ]
        directory = Path(self.enterContext(tempfile.TemporaryDirectory()))
        manifest = directory / 'image.json'
        for field, value in cases:
            with self.subTest(field=field, value=value):
                data = json.loads((ROOT / 'image/default.json').read_text())
                target = data
                for key in field[:-1]:
                    target = target[key]
                target[field[-1]] = value
                manifest.write_text(json.dumps(data))
                result, calls = self.run_plan(extra=['-ImageManifest', str(manifest)])
                self.assertNotEqual(result.returncode, 0, f'Malformed manifest accepted: {field}={value}')
                self.assertEqual(calls, [])
                self.assertEqual(result.stdout, '')

    def test_invalid_name_and_mutable_image_fail_before_discovery(self):
        for extra in (['-AppName', 'INVALID'], ['-Image', 'regis.azurecr.io/controller:latest']):
            result, calls = self.run_plan(extra=extra)
            self.assertNotEqual(result.returncode, 0)
            self.assertEqual(calls, [])

    def test_manifest_digest_terminal_line_feed_is_rejected_before_discovery(self):
        directory = Path(self.enterContext(tempfile.TemporaryDirectory()))
        manifest = directory / 'image.json'
        for section, field, value in (
            ('controller', 'sha256', 'a' * 64 + '\n'),
            ('baseImage', 'reference', 'regis.azurecr.io/base@sha256:' + 'b' * 64 + '\n'),
        ):
            with self.subTest(field=section + '.' + field):
                data = json.loads((ROOT / 'image/default.json').read_text())
                data.setdefault(section, {})[field] = value
                manifest.write_text(json.dumps(data))
                result, calls = self.run_plan(extra=['-ImageManifest', str(manifest)])
                self.assertNotEqual(result.returncode, 0,
                                    f'Terminal LF accepted with {len(calls)} Azure calls')
                self.assertEqual(calls, [])
                self.assertEqual(result.stdout, '')

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
