"""Local process integration tests; real Bicep, substituted Azure CLI only.

Run: python3 -m unittest discover -s infra/auth/tests -v
These prove deployment orchestration, not Azure provisioning or RBAC behavior.
"""
import copy
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

AUTH = Path(__file__).resolve().parents[1]
SUBSCRIPTION = '46f7e5d6-05f3-435b-8089-6c63a203dbfa'
GROUP = 'rg-repoagent-controller-auth'
SCOPE = f'/subscriptions/{SUBSCRIPTION}/resourceGroups/{GROUP}'
# Fixed fixture names calculated independently for the approved target/auth-test.
VAULT = 'ra-0d59e18d5f921c41-kv'
STORAGE = 'ra0d59e18d5f921c41dp'
MANIFEST = [
    ('Microsoft.ManagedIdentity/userAssignedIdentities', 'auth-test-runtime', 'auth-runtime'),
    ('Microsoft.OperationalInsights/workspaces', 'auth-test-logs', 'auth-logs'),
    ('Microsoft.App/managedEnvironments', 'auth-test-environment', 'auth-environment'),
    ('Microsoft.Storage/storageAccounts', STORAGE, 'auth-data-protection'),
    ('Microsoft.Storage/storageAccounts/blobServices/containers', STORAGE + '/default/data-protection', 'auth-data-protection'),
    ('Microsoft.KeyVault/vaults', VAULT, 'auth-security'),
    ('Microsoft.App/containerApps', 'auth-test', 'auth-service'),
]


def owned_resources():
    result = {}
    for kind, name, role in MANIFEST:
        if kind.endswith('/containers'):
            resource_id = f'{SCOPE}/providers/Microsoft.Storage/storageAccounts/{STORAGE}/blobServices/default/containers/data-protection'
        else:
            resource_id = f'{SCOPE}/providers/{kind}/{name}'
        ownership = {'repositoryId': '1426154477', 'ownerId': '345029828', 'managedBy': 'repoagent-auth', 'role': role}
        result[resource_id] = {'id': resource_id, 'type': kind, 'location': 'swedencentral',
                               'tags': ownership, 'properties': {'metadata': ownership}}
    return result


class DeploymentProcessTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.directory = Path(self.temp.name)
        shutil.copyfile(Path(__file__).with_name('az_double.py'), self.directory / 'az')
        (self.directory / 'az').chmod(0o755)
        self.env = {key: value for key, value in os.environ.items() if not key.startswith(('AUTH_', 'AZURE_'))}
        self.env.update({
            'PATH': str(self.directory) + os.pathsep + os.environ['PATH'],
            'AZ_DOUBLE_SCENARIO': str(self.directory / 'scenario.json'),
            'AZ_DOUBLE_JOURNAL': str(self.directory / 'journal.jsonl'),
            'AZURE_SUBSCRIPTION_ID': SUBSCRIPTION, 'AZURE_RESOURCE_GROUP': GROUP,
            'AZURE_LOCATION': 'swedencentral', 'AUTH_APP_NAME': 'auth-test',
            'AUTH_PROVIDER_READY': 'true', 'AUTH_STORAGE_READY': 'true',
            'AUTH_CLEANUP_READY': 'true', 'AUTH_RUNTIME_ACCESS_READY': 'true',
            'AUTH_SIGNING_KEY_URI': f'https://{VAULT}.vault.azure.net/keys/signing/' + 'a' * 32,
            'AUTH_DATA_PROTECTION_KEY_URI': f'https://{VAULT}.vault.azure.net/keys/protection/' + 'b' * 32,
            'AUTH_OAUTH_CLIENT_SECRET_URI': f'https://{VAULT}.vault.azure.net/secrets/oauth/' + 'c' * 32,
            'AUTH_STORE_RESOURCE_ID': f'{SCOPE}/providers/Microsoft.Storage/storageAccounts/authstore',
            'AUTH_IMAGE': 'ghcr.io/repoagent/auth-service@sha256:' + 'd' * 64,
            'AUTH_IMAGE_ROLE': 'auth-service', 'AUTH_IMAGE_NON_ROOT': 'true', 'AUTH_IMAGE_PUBLIC': 'true',
        })
        self.scenario = {'group': {'id': SCOPE, 'location': 'swedencentral'},
                         'resources': owned_resources(), 'plan': {'status': 'Succeeded', 'changes': []}}

    def run_deploy(self, mode='apply'):
        Path(self.env['AZ_DOUBLE_SCENARIO']).write_text(json.dumps(self.scenario))
        journal = Path(self.env['AZ_DOUBLE_JOURNAL'])
        journal.unlink(missing_ok=True)
        result = subprocess.run([str(AUTH / 'deploy.sh'), mode], env=self.env, capture_output=True, text=True, timeout=60)
        self.calls = [json.loads(line) for line in journal.read_text().splitlines()] if journal.exists() else []
        # All Azure reads and writes must stay in the explicit approved subscription.
        for call in self.calls:
            args = call['args']
            self.assertIn('--subscription', args)
            self.assertEqual(args[args.index('--subscription') + 1], SUBSCRIPTION)
        return result

    def creates(self):
        return [call for call in self.calls if call['args'][:3] == ['deployment', 'group', 'create']]

    def assert_refused(self, result, message):
        self.assertNotEqual(result.returncode, 0, result.stdout)
        self.assertIn(message, result.stderr)
        self.assertEqual(self.creates(), [], 'Refusal must happen before a deployment write')
        read_commands = {('group', 'show'), ('resource', 'show'),
                         ('deployment', 'group', 'validate'), ('deployment', 'group', 'what-if')}
        for call in self.calls:
            args = call['args']
            command = tuple(args[:3] if args[0] == 'deployment' else args[:2])
            self.assertIn(command, read_commands, 'Refusal must not issue any mutation command')

    def test_invalid_configuration_and_each_unready_gate_stop_before_azure(self):
        cases = {'AUTH_APP_NAME': 'Bad Name', 'AZURE_SUBSCRIPTION_ID': 'wrong-subscription',
                 'AUTH_IMAGE': 'ghcr.io/repoagent/auth:latest', 'AUTH_IMAGE_ROLE': 'controller',
                 'AUTH_IMAGE_NON_ROOT': 'false', 'AUTH_IMAGE_PUBLIC': 'false',
                 'AUTH_SIGNING_KEY_URI': 'https://other.vault.azure.net/keys/key/version',
                 **{key: 'false' for key in ('AUTH_PROVIDER_READY', 'AUTH_STORAGE_READY', 'AUTH_CLEANUP_READY', 'AUTH_RUNTIME_ACCESS_READY')}}
        original = self.env.copy()
        for name, value in cases.items():
            with self.subTest(name=name):
                self.env = {**original, name: value}
                self.assert_refused(self.run_deploy(), 'Auth deployment refused')
                self.assertEqual(self.calls, [])

    def test_check_validates_configuration_without_image_attestation(self):
        self.env = {key: value for key, value in self.env.items() if not key.startswith('AUTH_IMAGE')}
        result = self.run_deploy('check')
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertFalse(json.loads(result.stdout)['applied'])
        self.assertEqual(self.calls, [])
        self.env['AUTH_DATA_PROTECTION_KEY_URI'] = self.env['AUTH_SIGNING_KEY_URI'].rsplit('/', 1)[0] + '/' + 'e' * 32
        self.assert_refused(self.run_deploy('check'), 'separate keys')
        self.assertEqual(self.calls, [])

    def test_foundation_check_discovers_names_without_runtime_configuration_or_azure(self):
        self.env = {key: value for key, value in self.env.items()
                    if not key.startswith('AUTH_') or key == 'AUTH_APP_NAME'}
        result = self.run_deploy('foundation-check')
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(json.loads(result.stdout)['keyVaultName'], VAULT)
        self.assertEqual(self.calls, [])

    def test_foundation_can_create_missing_resources_incrementally(self):
        self.scenario['resources'] = {}
        self.scenario['plan']['changes'] = [{'changeType': 'Create', 'resourceId': resource_id} for resource_id in list(owned_resources())[:-1]]
        result = self.run_deploy('foundation-apply')
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertTrue(json.loads(result.stdout)['applied'])
        self.assertEqual(len(self.creates()), 1)
        self.assert_incremental_commands(deploy_app=False)

    def assert_incremental_commands(self, deploy_app):
        commands = [call for call in self.calls if call['args'][:2] == ['deployment', 'group']]
        self.assertIn('what-if', [call['args'][2] for call in commands])
        for call in commands:
            args = call['args']
            self.assertEqual(args[args.index('--mode') + 1], 'Incremental')
            self.assertEqual(args[args.index('--resource-group') + 1], GROUP)
            self.assertEqual(call['parameters']['parameters']['deployApp']['value'], deploy_app)

    def test_same_owned_runtime_apply_preserves_unrelated_resources(self):
        unrelated = f'{SCOPE}/providers/Microsoft.ManagedIdentity/userAssignedIdentities/id-controller-auth-github'
        self.scenario['resources'][unrelated] = {'id': unrelated, 'tags': {'owner': 'someone-else'}}
        self.scenario['plan']['changes'] = [
            {'changeType': 'Modify', 'resourceId': list(owned_resources())[-1]},
            {'changeType': 'Ignore', 'resourceId': unrelated},
        ]
        result = self.run_deploy()
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(len(self.creates()), 1)
        self.assert_incremental_commands(deploy_app=True)
        self.assertFalse(any('delete' in call['args'] for call in self.calls))

    def test_plan_does_not_apply(self):
        result = self.run_deploy('plan')
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertFalse(json.loads(result.stdout)['applied'])
        self.assertEqual(self.creates(), [])
        self.assert_incremental_commands(deploy_app=True)

    def test_runtime_refuses_missing_foundation(self):
        self.scenario['resources'] = {}
        self.assert_refused(self.run_deploy(), 'Runtime requires existing foundation')

    def test_conflicting_ownership_including_container_metadata_refuses_adoption(self):
        baseline = copy.deepcopy(self.scenario)
        for resource_id, resource in baseline['resources'].items():
            with self.subTest(resource=resource_id):
                self.scenario = copy.deepcopy(baseline)
                target = self.scenario['resources'][resource_id]
                if resource['type'].endswith('/containers'):
                    target['properties']['metadata']['repositoryId'] = 'other-repository'
                else:
                    target['tags']['ownerId'] = 'other-owner'
                self.assert_refused(self.run_deploy(), 'Ownership conflict')

    def test_azure_failures_never_become_resource_absence(self):
        responses = [({'error': '(AuthorizationFailed) sensitive-server-body'}, 'forbidden'),
                     ({'error': 'Connection timeout sensitive-server-body'}, 'network'),
                     ({'raw': '{malformed'}, 'malformed'),
                     ({'body': []}, 'malformed'),
                     ({'body': {}}, 'malformed resource identity')]
        for response, message in responses:
            with self.subTest(response=response):
                self.scenario['responses'] = {'resource show': response}
                result = self.run_deploy('foundation-apply')
                self.assert_refused(result, message)
                self.assertNotIn('sensitive-server-body', result.stderr)

    def test_bad_what_if_cannot_apply(self):
        app_id = list(owned_resources())[-1]
        plans = [None, {}, {'status': 'Failed', 'changes': []},
                 {'status': 'Succeeded', 'changes': {}},
                 {'status': 'Succeeded', 'changes': [{'changeType': 'Delete', 'resourceId': app_id}]},
                 {'status': 'Succeeded', 'changes': [{'changeType': 'Create', 'resourceId': SCOPE + '/providers/Microsoft.App/containerApps/unrelated'}]},
                 {'status': 'Succeeded', 'changes': [None]}]
        for plan in plans:
            with self.subTest(plan=plan):
                self.scenario['plan'] = plan
                self.assert_refused(self.run_deploy(), 'Auth deployment refused')

    def test_failed_apply_is_not_reported_as_success_or_retried(self):
        for response in ({'error': '(DeploymentFailed) sensitive-server-body'},
                         {'body': {'properties': {'provisioningState': 'Failed'}}},
                         {'body': {'properties': []}}):
            with self.subTest(response=response):
                self.scenario['responses'] = {'deployment group create': response}
                result = self.run_deploy()
                self.assertNotEqual(result.returncode, 0)
                self.assertEqual(len(self.creates()), 1)
                self.assertEqual(result.stdout, '')
                self.assertNotIn('sensitive-server-body', result.stderr)


if __name__ == '__main__':
    unittest.main()
