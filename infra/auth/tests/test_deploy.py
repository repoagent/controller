"""Local process integration tests; real Bicep, substituted Azure CLI only.

Run: python3 -m unittest discover -s infra/auth/tests -v
These prove deployment orchestration, not Azure provisioning or RBAC behavior.
"""
import copy
import importlib.util
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest
import uuid
from unittest.mock import patch

AUTH = Path(__file__).resolve().parents[1]
SUBSCRIPTION = '46f7e5d6-05f3-435b-8089-6c63a203dbfa'
GROUP = 'rg-repoagent-controller-auth'
SCOPE = f'/subscriptions/{SUBSCRIPTION}/resourceGroups/{GROUP}'
# Explicit operator-selected fixtures, independent of deployment name selection.
VAULT = 'auth-readable-kv'
STORAGE = 'authreadablestate'
TABLE_ID = f'{SCOPE}/providers/Microsoft.Storage/storageAccounts/{STORAGE}/tableServices/default/tables/brokerstate'
DOMAIN = 'gentle-field-123.swedencentral.azurecontainerapps.io'
ORIGIN = 'https://auth-test.' + DOMAIN
ENVIRONMENT_ID = f'{SCOPE}/providers/Microsoft.App/managedEnvironments/auth-test-environment'
APP_ID = f'{SCOPE}/providers/Microsoft.App/containerApps/auth-test'
CLIENT_ID = '11111111-2222-3333-4444-555555555555'
PRINCIPAL_ID = 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee'
MANIFEST = [
    ('Microsoft.ManagedIdentity/userAssignedIdentities', 'auth-test-runtime', 'auth-runtime'),
    ('Microsoft.OperationalInsights/workspaces', 'auth-test-logs', 'auth-logs'),
    ('Microsoft.App/managedEnvironments', 'auth-test-environment', 'auth-environment'),
    ('Microsoft.Storage/storageAccounts', STORAGE, 'auth-data-protection'),
    ('Microsoft.Storage/storageAccounts/blobServices/containers', STORAGE + '/default/data-protection', 'auth-data-protection'),
    ('Microsoft.KeyVault/vaults', VAULT, 'auth-security'),
    ('Microsoft.Storage/storageAccounts/tableServices/tables', STORAGE + '/default/brokerstate', 'auth-broker-state'),
    ('Microsoft.App/containerApps', 'auth-test', 'auth-service'),
]


def owned_resources():
    result = {}
    for kind, name, role in MANIFEST:
        if kind.endswith('/tables'):
            result[TABLE_ID] = {'id': TABLE_ID, 'type': kind, 'properties': {}}
            continue
        if kind.endswith('/containers'):
            resource_id = f'{SCOPE}/providers/Microsoft.Storage/storageAccounts/{STORAGE}/blobServices/default/containers/data-protection'
        else:
            resource_id = f'{SCOPE}/providers/{kind}/{name}'
        ownership = {'repositoryId': '1426154477', 'ownerId': '345029828', 'managedBy': 'repoagent-auth', 'role': role}
        result[resource_id] = {'id': resource_id, 'type': kind, 'location': 'swedencentral',
                               'tags': ownership, 'properties': {'metadata': ownership}}
    result[f'{SCOPE}/providers/Microsoft.ManagedIdentity/userAssignedIdentities/auth-test-runtime']['properties'] = {
        'clientId': CLIENT_ID, 'principalId': PRINCIPAL_ID}
    result[ENVIRONMENT_ID]['properties'] = {'defaultDomain': DOMAIN}
    result[APP_ID]['properties'] = {'environmentId': ENVIRONMENT_ID, 'configuration': {
        'ingress': {'external': True, 'fqdn': ORIGIN.removeprefix('https://')}}}
    return result


CONNECTION = 'InstrumentationKey=22222222-2222-2222-2222-222222222222;IngestionEndpoint=https://test.in.applicationinsights.azure.com/'
INSIGHTS_ID = SCOPE + '/providers/Microsoft.Insights/components/auth-test-insights'


def monitoring_resources():
    """Independent provider readbacks for the owned telemetry deployment boundary."""
    ownership = dict(repositoryId='1426154477', ownerId='345029828', managedBy='repoagent-auth')
    logs_id = SCOPE + '/providers/Microsoft.OperationalInsights/workspaces/auth-test-logs'
    identity_id = SCOPE + '/providers/Microsoft.ManagedIdentity/userAssignedIdentities/auth-test-cleanup'
    return {
        logs_id: dict(id=logs_id, type='Microsoft.OperationalInsights/workspaces', location='swedencentral',
            tags={**ownership, 'role': 'auth-logs'}, properties=dict(retentionInDays=30, workspaceCapping=dict(dailyQuotaGb=0.1))),
        INSIGHTS_ID: dict(id=INSIGHTS_ID, type='Microsoft.Insights/components', location='swedencentral',
            tags={**ownership, 'role': 'auth-monitoring'}, properties=dict(DisableLocalAuth=True,
                WorkspaceResourceId=logs_id, InstrumentationKey='22222222-2222-2222-2222-222222222222', ConnectionString=CONNECTION)),
        identity_id: dict(id=identity_id, type='Microsoft.ManagedIdentity/userAssignedIdentities', location='swedencentral',
            tags={**ownership, 'role': 'auth-cleanup-identity'}, properties=dict(
                clientId='22222222-3333-4444-5555-666666666666', principalId='bbbbbbbb-cccc-dddd-eeee-ffffffffffff')),
    }


def publisher_assignments():
    role = '/subscriptions/' + SUBSCRIPTION + '/providers/Microsoft.Authorization/roleDefinitions/3913510d-42f4-4e42-8a64-420c390055eb'
    return [dict(scope=INSIGHTS_ID, roleDefinitionId=role, principalType='ServicePrincipal', principalId=principal,
        name=str(uuid.uuid5(uuid.UUID('11fb06fb-712d-4ddd-98c7-e71bbd588830'), '-'.join((INSIGHTS_ID, identity, role)))))
        for identity, principal in ((SCOPE + '/providers/Microsoft.ManagedIdentity/userAssignedIdentities/auth-test-runtime', PRINCIPAL_ID),
            (SCOPE + '/providers/Microsoft.ManagedIdentity/userAssignedIdentities/auth-test-cleanup', 'bbbbbbbb-cccc-dddd-eeee-ffffffffffff'))]


class DeploymentTimeoutTests(unittest.TestCase):
    def setUp(self):
        spec = importlib.util.spec_from_file_location('auth_deploy', AUTH / 'deploy.py')
        self.deploy = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(self.deploy)
        configuration = {'AZURE_SUBSCRIPTION_ID': SUBSCRIPTION, 'AZURE_RESOURCE_GROUP': GROUP,
                         'AZURE_LOCATION': 'swedencentral', 'AUTH_APP_NAME': 'auth-test',
                         'AUTH_STORAGE_ACCOUNT_NAME': STORAGE,
                         'AUTH_KEY_VAULT_NAME': VAULT}
        with patch.dict(os.environ, configuration, clear=True):
            self.deployment = self.deploy.Deployment('foundation-apply')
        self.process = subprocess.CompletedProcess(['az'], 0, stdout='{}', stderr='')

    def test_deployment_create_allows_twenty_minutes(self):
        with patch.object(self.deploy.subprocess, 'run', return_value=self.process) as run:
            self.assertEqual(self.deployment.az(['deployment', 'group', 'create']), {})
        self.assertEqual(run.call_args.kwargs['timeout'], 1200)

    def test_queries_validation_and_what_if_keep_two_minute_timeout(self):
        for command in (['group', 'show'], ['deployment', 'group', 'validate'],
                        ['deployment', 'group', 'what-if']):
            with self.subTest(command=command):
                with patch.object(self.deploy.subprocess, 'run', return_value=self.process) as run:
                    self.assertEqual(self.deployment.az(command), {})
                self.assertEqual(run.call_args.kwargs['timeout'], 120)

    def test_operation_deadline_takes_precedence_for_create_and_query(self):
        self.deployment.deadline = 150
        with patch.object(self.deploy.time, 'monotonic', return_value=100):
            for command in (['deployment', 'group', 'create'], ['group', 'show']):
                with self.subTest(command=command):
                    with patch.object(self.deploy.subprocess, 'run', return_value=self.process) as run:
                        self.assertEqual(self.deployment.az(command), {})
                    self.assertEqual(run.call_args.kwargs['timeout'], 50)


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
            'AUTH_STORAGE_ACCOUNT_NAME': STORAGE, 'AUTH_KEY_VAULT_NAME': VAULT,
            'AUTH_PROVIDER_READY': 'true', 'AUTH_STORAGE_READY': 'true',
            'AUTH_CLEANUP_READY': 'true', 'AUTH_RUNTIME_ACCESS_READY': 'true',
            'AUTH_SIGNING_KEY_URI': f'https://{VAULT}.vault.azure.net/keys/signing/' + 'a' * 32,
            'AUTH_DATA_PROTECTION_KEY_URI': f'https://{VAULT}.vault.azure.net/keys/protection/' + 'b' * 32,
            'AUTH_DEPLOYMENT_EPOCH': 'restore_2026-10-20',
            'AUTH_IMAGE': 'ghcr.io/repoagent/auth-service@sha256:' + 'd' * 64,
            'AUTH_IMAGE_ROLE': 'auth-service', 'AUTH_IMAGE_NON_ROOT': 'true', 'AUTH_IMAGE_PUBLIC': 'true',
        })
        self.scenario = {'responses': {'role assignment list': {'body': publisher_assignments()}}, 'group': {'id': SCOPE, 'location': 'swedencentral'},
                         'resources': owned_resources(), 'plan': {'status': 'Succeeded', 'changes': []}}
        self.scenario['resources'].update(monitoring_resources())
        self.scenario['resources'][ENVIRONMENT_ID]['properties']['appLogsConfiguration'] = dict(destination='none')

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
            command = tuple(args[:3] if args[0] == 'deployment' else args[:2])
            self.assertIn(command, {('group', 'show'), ('resource', 'show'), ('role', 'assignment'),
                ('deployment', 'group', 'validate'), ('deployment', 'group', 'what-if'),
                ('deployment', 'group', 'create')}, 'Ordinary deployment must never assign roles, read keys or apply budgets')
        return result

    def creates(self):
        return [call for call in self.calls if call['args'][:3] == ['deployment', 'group', 'create']]

    def assert_refused(self, result, message):
        self.assertNotEqual(result.returncode, 0, result.stdout)
        self.assertIn(message, result.stderr)
        self.assertEqual(self.creates(), [], 'Refusal must happen before a deployment write')
        read_commands = {('group', 'show'), ('resource', 'show'), ('role', 'assignment'),
                         ('deployment', 'group', 'validate'), ('deployment', 'group', 'what-if')}
        for call in self.calls:
            args = call['args']
            command = tuple(args[:3] if args[0] == 'deployment' else args[:2])
            self.assertIn(command, read_commands, 'Refusal must not issue any mutation command')

    def test_origin_is_unresolved_offline_and_stable_on_first_and_repeated_apply(self):
        result = self.run_deploy('check')
        self.assertIsNone(json.loads(result.stdout)['brokerOrigin'])
        self.assertEqual(self.calls, [])
        app = self.scenario['resources'].pop(APP_ID)
        self.scenario['postCreateResources'] = {APP_ID: app}
        for _ in range(2):
            result = self.run_deploy()
            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertEqual(json.loads(result.stdout)['brokerOrigin'], ORIGIN)
            self.assertEqual(json.loads(result.stdout)['runtimeConfiguration']['Broker__BrokerOrigin'], ORIGIN)
            self.assertIn('Broker origin: ' + ORIGIN, result.stderr)
            self.assertEqual(len(self.creates()), 1)
            self.scenario['resources'][APP_ID] = app

    def test_malformed_environment_domains_refuse_before_any_write(self):
        for domain in (None, '', 123, [], 'https://example.test', 'user:pass@example.test',
                       'example.test/path', 'example.test?query', 'example.test#fragment',
                       'example.test:443', '.example.test', 'bad_.example.test', '-bad.example.test'):
            with self.subTest(domain=domain):
                self.scenario['resources'][ENVIRONMENT_ID]['properties']['defaultDomain'] = domain
                self.assert_refused(self.run_deploy(), 'defaultDomain')

    def test_foreign_environment_identity_and_location_refuse_before_write(self):
        original = copy.deepcopy(self.scenario['resources'][ENVIRONMENT_ID])
        for key, value in (('id', APP_ID), ('type', 'Microsoft.App/containerApps'), ('location', 'other-region'), ('tags', {})):
            with self.subTest(key=key):
                self.scenario['resources'][ENVIRONMENT_ID] = {**original, key: value}
                self.assert_refused(self.run_deploy(), 'Auth deployment refused')

    def test_approved_location_representations_reach_foundation_plan(self):
        for target in ('group', 'environment'):
            resource = self.scenario['group'] if target == 'group' else self.scenario['resources'][ENVIRONMENT_ID]
            for location in ('swedencentral', 'Sweden Central'):
                with self.subTest(target=target, location=location):
                    resource['location'] = location
                    result = self.run_deploy('foundation-plan')
                    self.assertEqual(self.creates(), [])
                    self.assertEqual(result.returncode, 0, result.stderr)
                    self.assertFalse(json.loads(result.stdout)['applied'])
                    self.assertTrue(any(c['args'][:3] == ['deployment', 'group', 'what-if'] for c in self.calls))
            resource['location'] = 'swedencentral'

    def test_invalid_locations_refuse_at_foundation_guard_before_write(self):
        for target, message in (('group', 'resource group identity/location'), ('environment', 'Resource location conflict')):
            resource = self.scenario['group'] if target == 'group' else self.scenario['resources'][ENVIRONMENT_ID]
            for location in ('northeurope', 'sweden', 'swedencentral-extra', 'SwedenCentral',
                             'Sweden  Central', 'sw eden central', '(Europe) Sweden Central', '', None, 42, [], {}):
                with self.subTest(target=target, location=location):
                    resource['location'] = location
                    self.assert_refused(self.run_deploy('foundation-apply'), message)
                    self.assertFalse(any(c['args'][:2] == ['deployment', 'group'] for c in self.calls))
            resource['location'] = 'swedencentral'

    def test_public_ingress_mismatch_cannot_report_runtime_success(self):
        self.scenario['resources'][APP_ID]['properties']['configuration']['ingress']['fqdn'] = 'foreign.example.test'
        result = self.run_deploy()
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('canonical broker origin', result.stderr)
        self.assertEqual(result.stdout, '')
        self.assertEqual(len(self.creates()), 1)

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

    def test_check_rejects_case_aliases_of_one_key_even_across_versions(self):
        self.env['AUTH_SIGNING_KEY_URI'] = f'https://{VAULT}.vault.azure.net/keys/signing/' + 'a' * 32
        for version in ('a' * 32, 'b' * 32):
            with self.subTest(protection_version=version):
                self.env['AUTH_DATA_PROTECTION_KEY_URI'] = f'https://{VAULT}.vault.azure.net/keys/SIGNING/{version}'
                self.assert_refused(self.run_deploy('check'), 'separate keys')
                self.assertEqual(self.calls, [])

        self.env['AUTH_DATA_PROTECTION_KEY_URI'] = f'https://{VAULT}.vault.azure.net/keys/SIGNING-Protection/' + 'a' * 32
        result = self.run_deploy('check')
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertFalse(json.loads(result.stdout)['applied'])
        self.assertEqual(self.calls, [])

    def test_foundation_check_preserves_selected_names_without_runtime_configuration_or_azure(self):
        self.env = {key: value for key, value in self.env.items()
                    if not key.startswith('AUTH_') or key in ('AUTH_APP_NAME', 'AUTH_STORAGE_ACCOUNT_NAME', 'AUTH_KEY_VAULT_NAME')}
        for storage, vault in (('abc', 'AbC'), ('readablestorageaccount12', 'Readable-Auth-Vault-1234')):
            with self.subTest(storage=storage, vault=vault):
                self.env.update(AUTH_STORAGE_ACCOUNT_NAME=storage, AUTH_KEY_VAULT_NAME=vault)
                result = self.run_deploy('foundation-check')
                self.assertEqual(result.returncode, 0, result.stderr)
                output = json.loads(result.stdout)
                self.assertEqual(self.calls, [])
                self.assertEqual(output['keyVaultName'], vault)
                self.assertEqual(output['tableResourceId'],
                                 f'{SCOPE}/providers/Microsoft.Storage/storageAccounts/{storage}/tableServices/default/tables/brokerstate')
                self.assertEqual(output['tableEndpoint'], f'https://{storage}.table.core.windows.net/')
                self.assertEqual(output['dataProtectionBlobUri'], f'https://{storage}.blob.core.windows.net/data-protection/keyring.xml')

    def test_missing_or_invalid_selected_names_refuse_before_azure(self):
        cases = {
            'AUTH_STORAGE_ACCOUNT_NAME': (None, '', 'ab', 'a' * 25, 'Uppercase', 'has-hyphen', 'punct!', 'éclair', ' abc', 'abc ', '   '),
            'AUTH_KEY_VAULT_NAME': (None, '', 'ab', 'a' * 25, '1vault', '-vault', 'vault-', 'va--ult', 'punct!', 'éclair', ' abc', 'abc ', '   '),
        }
        original = self.env.copy()
        for name, values in cases.items():
            for value in values:
                with self.subTest(name=name, value=value):
                    self.env = {**original, name: value} if value is not None else {key: item for key, item in original.items() if key != name}
                    result = self.run_deploy('foundation-check')
                    self.assert_refused(result, name)
                    self.assertEqual(result.stdout, '')
                    self.assertEqual(self.calls, [])

    def test_foundation_can_create_missing_resources_incrementally(self):
        self.scenario['resources'] = {}
        self.scenario['postCreateResources'] = {resource_id: value for resource_id, value in owned_resources().items()
                                                if value['type'] != 'Microsoft.App/containerApps'}
        self.scenario['plan']['changes'] = [{'changeType': 'Create', 'resourceId': resource_id} for resource_id in list(owned_resources())[:-1]]
        result = self.run_deploy('foundation-apply')
        self.assertEqual(result.returncode, 0, result.stderr)
        output = json.loads(result.stdout)
        self.assertTrue(output['applied'])
        self.assertEqual(output['runtimeIdentityClientId'], CLIENT_ID)
        self.assertEqual(output['runtimeIdentityPrincipalId'], PRINCIPAL_ID)
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
            self.assertEqual(call['parameters']['parameters']['storageName']['value'], STORAGE)
            self.assertEqual(call['parameters']['parameters']['vaultName']['value'], VAULT)

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
        result = self.run_deploy()
        self.assert_refused(result, 'not-found')
        self.assertEqual(result.stdout, '')
        self.assertTrue(any(call.get('responseCode') == 3 for call in self.calls))

    def test_conflicting_ownership_including_container_metadata_refuses_adoption(self):
        baseline = copy.deepcopy(self.scenario)
        for resource_id, resource in baseline['resources'].items():
            if resource['type'].endswith('/tables'):
                continue  # Tables have no Azure-supported ownership metadata.
            with self.subTest(resource=resource_id):
                self.scenario = copy.deepcopy(baseline)
                target = self.scenario['resources'][resource_id]
                if resource['type'].endswith('/containers'):
                    target['properties']['metadata']['repositoryId'] = 'other-repository'
                else:
                    target['tags']['ownerId'] = 'other-owner'
                result = self.run_deploy()
                self.assert_refused(result, 'Auth deployment refused')
                self.assertIn('ownership', result.stderr.lower())

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
                self.scenario['responses']['deployment group create'] = response
                result = self.run_deploy()
                self.assertNotEqual(result.returncode, 0)
                self.assertEqual(len(self.creates()), 1)
                self.assertEqual(result.stdout, '')
                self.assertNotIn('sensitive-server-body', result.stderr)

    def test_check_emits_frozen_table_configuration_without_legacy_oauth_or_store(self):
        result = self.run_deploy('check')
        self.assertEqual(result.returncode, 0, result.stderr)
        output = json.loads(result.stdout)
        self.assertEqual(output['tableResourceId'], TABLE_ID)
        self.assertEqual(output['runtimeConfiguration'], {
            'APPLICATIONINSIGHTS_CONNECTION_STRING': '', 'APPLICATIONINSIGHTS_STATSBEAT_DISABLED': 'true',
            'Broker__BrokerOrigin': None,
            'Broker__TableEndpoint': f'https://{STORAGE}.table.core.windows.net/',
            'Broker__TableName': 'brokerstate', 'Broker__ManagedIdentityClientId': None,
            'Broker__DeploymentEpoch': 'restore_2026-10-20',
            'Broker__RequestLimitPerMinute': 120, 'Broker__ConcurrentRequestLimit': 8,
            'Broker__RequestTimeoutSeconds': 15})
        self.assertIsNone(output['runtimeIdentityPrincipalId'])
        self.assertEqual(self.calls, [])

    def test_plan_carries_operator_values_and_platform_issued_identity(self):
        self.env.update({'AUTH_TABLE_NAME': 'PilotState42', 'AUTH_DEPLOYMENT_EPOCH': 'new_epoch-42',
                         'AUTH_REQUEST_LIMIT_PER_MINUTE': '234', 'AUTH_CONCURRENT_REQUEST_LIMIT': '17',
                         'AUTH_REQUEST_TIMEOUT_SECONDS': '29'})
        table = self.scenario['resources'].pop(TABLE_ID)
        table_id = TABLE_ID.rsplit('/', 1)[0] + '/PilotState42'
        table['id'] = table_id
        self.scenario['resources'][table_id] = table
        # Unrelated tables are outside the reserved child boundary.
        unrelated = TABLE_ID.rsplit('/', 1)[0] + '/Unrelated'
        self.scenario['resources'][unrelated] = {'id': unrelated, 'type': table['type']}
        self.scenario['plan']['changes'] = [{'changeType': 'Ignore', 'resourceId': unrelated}]
        result = self.run_deploy('plan')
        self.assertEqual(result.returncode, 0, result.stderr)
        output = json.loads(result.stdout)
        self.assertEqual(output['tableResourceId'], table_id)
        self.assertEqual(output['runtimeIdentityClientId'], CLIENT_ID)
        self.assertEqual(output['runtimeIdentityPrincipalId'], PRINCIPAL_ID)
        self.assertEqual(output['runtimeConfiguration'], {
            'APPLICATIONINSIGHTS_CONNECTION_STRING': CONNECTION, 'APPLICATIONINSIGHTS_STATSBEAT_DISABLED': 'true',
            'Broker__BrokerOrigin': ORIGIN,
            'Broker__TableEndpoint': f'https://{STORAGE}.table.core.windows.net/',
            'Broker__TableName': 'PilotState42', 'Broker__ManagedIdentityClientId': CLIENT_ID,
            'Broker__DeploymentEpoch': 'new_epoch-42', 'Broker__RequestLimitPerMinute': 234,
            'Broker__ConcurrentRequestLimit': 17, 'Broker__RequestTimeoutSeconds': 29})
        for call in self.calls:
            if 'parameters' in call:
                parameters = {key: value['value'] for key, value in call['parameters']['parameters'].items()}
                self.assertEqual(parameters['tableName'], 'PilotState42')
                self.assertEqual(parameters['deploymentEpoch'], 'new_epoch-42')
                self.assertEqual(parameters['requestLimitPerMinute'], 234)
                self.assertEqual(parameters['concurrentRequestLimit'], 17)
                self.assertEqual(parameters['requestTimeoutSeconds'], 29)
                self.assertNotIn('storeResourceId', parameters)
                self.assertNotIn('oauthClientSecretUri', parameters)
            self.assertNotIn(unrelated, call['args'])
        self.assertEqual(self.creates(), [])

    def test_invalid_table_epoch_and_http_bounds_refuse_before_azure(self):
        cases = {
            'AUTH_TABLE_NAME': ('', 'ab', '1broker', 'broker-state', 'éclair', 'a' * 64),
            'AUTH_DEPLOYMENT_EPOCH': ('', 'has space', 'a/b', 'é', 'x' * 129),
            'AUTH_REQUEST_LIMIT_PER_MINUTE': ('0', '10001', '-1', '1.5', 'true'),
            'AUTH_CONCURRENT_REQUEST_LIMIT': ('0', '129', '-1', ' 8'),
            'AUTH_REQUEST_TIMEOUT_SECONDS': ('0', '61', '', '1e1'),
        }
        original = self.env.copy()
        for name, values in cases.items():
            for value in values:
                with self.subTest(name=name, value=value):
                    self.env = {**original, name: value}
                    self.assert_refused(self.run_deploy('check'), name)
                    self.assertEqual(self.calls, [])
        self.env = original.copy()
        del self.env['AUTH_DEPLOYMENT_EPOCH']
        self.assert_refused(self.run_deploy('check'), 'AUTH_DEPLOYMENT_EPOCH')

    def test_table_epoch_and_http_boundary_values_are_accepted(self):
        for table, epoch, rate, concurrent, timeout in (
            ('abc', 'x', '1', '1', '1'), ('a' * 63, 'x' * 128, '10000', '128', '60')):
            with self.subTest(table=table):
                self.env.update({'AUTH_TABLE_NAME': table, 'AUTH_DEPLOYMENT_EPOCH': epoch,
                                 'AUTH_REQUEST_LIMIT_PER_MINUTE': rate,
                                 'AUTH_CONCURRENT_REQUEST_LIMIT': concurrent,
                                 'AUTH_REQUEST_TIMEOUT_SECONDS': timeout})
                result = self.run_deploy('check')
                self.assertEqual(result.returncode, 0, result.stderr)
                config = json.loads(result.stdout)['runtimeConfiguration']
                self.assertEqual(config['Broker__TableName'], table)
                self.assertEqual(config['Broker__DeploymentEpoch'], epoch)
                self.assertEqual(config['Broker__RequestLimitPerMinute'], int(rate))
                self.assertEqual(config['Broker__ConcurrentRequestLimit'], int(concurrent))
                self.assertEqual(config['Broker__RequestTimeoutSeconds'], int(timeout))
                self.assertEqual(self.calls, [])

    def test_runtime_requires_existing_exact_table_identity(self):
        baseline = copy.deepcopy(self.scenario)
        for replacement in (None, {'id': TABLE_ID + 'other', 'type': MANIFEST[-2][0]},
                            {'id': TABLE_ID, 'type': 'Microsoft.Storage/storageAccounts'}):
            with self.subTest(table=replacement):
                self.scenario = copy.deepcopy(baseline)
                if replacement is None:
                    del self.scenario['resources'][TABLE_ID]
                    message = 'Runtime requires existing foundation'
                else:
                    self.scenario['resources'][TABLE_ID] = replacement
                    message = 'malformed resource identity'
                self.assert_refused(self.run_deploy('plan'), message)

    def test_malformed_identity_cannot_be_used_as_client_or_principal(self):
        identity_id = f'{SCOPE}/providers/Microsoft.ManagedIdentity/userAssignedIdentities/auth-test-runtime'
        for properties in ([], {}, {'clientId': PRINCIPAL_ID},
                           {'clientId': identity_id, 'principalId': PRINCIPAL_ID},
                           {'clientId': CLIENT_ID, 'principalId': 'not-a-uuid'}):
            with self.subTest(properties=properties):
                self.scenario['resources'][identity_id]['properties'] = properties
                result = self.run_deploy('plan')
                self.assert_refused(result, 'Auth deployment refused')
                self.assertEqual(result.stdout, '')
                self.assertTrue(any(call['args'][:2] == ['resource', 'show'] and identity_id in call['args'] for call in self.calls))

    def budget_file(self, inputs):
        filename = self.directory / 'budget.json'
        filename.write_text(json.dumps(inputs))
        self.env['AUTH_BUDGET_PLAN_FILE'] = str(filename)

    def test_cost_plan_lists_missing_choices_without_runtime_gates_or_azure(self):
        self.env = {key: value for key, value in self.env.items()
                    if not key.startswith('AUTH_') or key in ('AUTH_APP_NAME', 'AUTH_STORAGE_ACCOUNT_NAME', 'AUTH_KEY_VAULT_NAME')}
        result = self.run_deploy('cost-plan')
        self.assertEqual(result.returncode, 0, result.stderr)
        output = json.loads(result.stdout)
        budget = output['budgetPlan']
        for field in ('amount', 'timeGrain', 'startDate', 'endDate', 'thresholds'):
            self.assertIn(field, budget['missingInputs'])
        self.assertTrue(any('contactEmails' in field and 'contactGroups' in field for field in budget['missingInputs']))
        self.assertEqual(budget['inputs'], {})
        self.assertEqual(budget['scope'], SCOPE)
        self.assertEqual(budget['billingEligibility'], 'unknown')
        self.assertFalse(budget['readyToApply'])
        self.assertFalse(budget['hardSpendingCap'])
        self.assertTrue(any('billing' in gate for gate in budget['preApplyGates']))
        self.assertEqual(output['runtimeConfiguration']['Broker__RequestLimitPerMinute'], 120)
        self.assertEqual(output['runtimeConfiguration']['Broker__ConcurrentRequestLimit'], 8)
        self.assertEqual(output['runtimeConfiguration']['Broker__RequestTimeoutSeconds'], 15)
        self.assertEqual(output['capacityPlan'], {
            'minReplicas': 0, 'maxReplicas': 1, 'cpu': 0.25, 'memory': '0.5Gi',
            'logRetentionDays': 30, 'logDailyQuotaGb': 0.1, 'hardSpendingCap': False,
            'consoleDestination': 'none', 'requestLimitsAreProcessLocal': True})
        self.assertEqual(self.calls, [])
        self.env['AUTH_REQUEST_LIMIT_PER_MINUTE'] = '10001'
        self.assert_refused(self.run_deploy('cost-plan'), 'AUTH_REQUEST_LIMIT_PER_MINUTE')
        self.assertEqual(self.calls, [])

    def test_cost_plan_preserves_explicit_alert_choices_but_keeps_eligibility_gate(self):
        for period, notifications in (
            ('Monthly', {'contactEmails': ['operator@example.org']}),
            ('Quarterly', {'contactGroups': [SCOPE + '/providers/Microsoft.Insights/actionGroups/operator-alert']}),
            ('Annually', {'contactEmails': ['operator@example.org'],
                          'contactGroups': [SCOPE + '/providers/Microsoft.Insights/actionGroups/operator-alert']})):
            with self.subTest(period=period):
                inputs = {'scope': SCOPE, 'amount': 42, 'timeGrain': period,
                          'startDate': '2026-10-01', 'endDate': '2027-10-01',
                          'thresholds': [1, 50, 100, 200, 1000], **notifications}
                self.budget_file(inputs)
                self.env.update({'AUTH_REQUEST_LIMIT_PER_MINUTE': '321',
                                 'AUTH_CONCURRENT_REQUEST_LIMIT': '13', 'AUTH_REQUEST_TIMEOUT_SECONDS': '31'})
                result = self.run_deploy('cost-plan')
                self.assertEqual(result.returncode, 0, result.stderr)
                output = json.loads(result.stdout)
                budget = output['budgetPlan']
                self.assertEqual(output['runtimeConfiguration']['Broker__RequestLimitPerMinute'], 321)
                self.assertEqual(output['runtimeConfiguration']['Broker__ConcurrentRequestLimit'], 13)
                self.assertEqual(output['runtimeConfiguration']['Broker__RequestTimeoutSeconds'], 31)
                self.assertEqual(budget['inputs'], {key: value for key, value in inputs.items() if key != 'contactEmails'})
                for recipient in notifications.get('contactEmails', []):
                    self.assertNotIn(recipient, result.stdout + result.stderr)
                self.assertEqual(budget['missingInputs'], [])
                self.assertEqual(budget['schema'], 'Microsoft.Consumption/budgets@2024-08-01')
                self.assertEqual(budget['notification'], {'thresholdType': 'Actual', 'operator': 'GreaterThanOrEqualTo'})
                self.assertEqual(budget['billingEligibility'], 'unknown')
                self.assertFalse(budget['readyToApply'])
                self.assertTrue(budget['preApplyGates'])
                self.assertEqual(self.calls, [])

    def test_cost_plan_rejects_invalid_operator_choices_before_azure(self):
        cases = {
            'amount': (0, -1, True, 1.5, '42'),
            'timeGrain': ('Daily', '', None),
            'startDate': ('2017-05-01', '2026-10-02', '2026-02-30', '20261001', 20261001),
            'endDate': ('2026-09-01', '2026-10-01', 'not-a-date'),
            'thresholds': ([], [0], [1001], [50, 50], [True], [1.5], [1, 2, 3, 4, 5, 6]),
            'contactEmails': ('operator@example.org', ['not-an-email']),
            'contactGroups': ([SCOPE + '/providers/Microsoft.Insights/actionGroups/other/group'],
                              ['/subscriptions/other/resourceGroups/other/providers/Microsoft.Insights/actionGroups/alert']),
            'scope': (SCOPE + '-other', 123),
            'billingEligibility': ('verified',),  # Operator input cannot bypass the eligibility gate.
        }
        baseline = {'amount': 42, 'timeGrain': 'Monthly', 'startDate': '2026-10-01',
                    'endDate': '2027-10-01', 'thresholds': [80], 'contactEmails': ['operator@example.org']}
        for name, values in cases.items():
            for value in values:
                with self.subTest(name=name, value=value):
                    self.budget_file({**baseline, name: value})
                    self.assert_refused(self.run_deploy('cost-plan'), 'Budget')
                    self.assertEqual(self.calls, [])
        for inputs in ([], None):
            with self.subTest(inputs=inputs):
                self.budget_file(inputs)
                self.assert_refused(self.run_deploy('cost-plan'), 'Budget')
                self.assertEqual(self.calls, [])

    def test_cost_plan_decoding_failures_keep_budget_context_without_echoing_private_input(self):
        canary = 'private-budget-input-must-not-escape'
        for payload in (b'{"' + canary.encode() + b'":', canary.encode() + b'\xff'):
            with self.subTest(payload=payload):
                filename = self.directory / 'private-budget.json'
                filename.write_bytes(payload)
                self.env['AUTH_BUDGET_PLAN_FILE'] = str(filename)
                result = self.run_deploy('cost-plan')

                self.assert_refused(result, 'Budget')
                self.assertEqual(self.calls, [])
                self.assertEqual(result.stdout, '')
                self.assertNotIn(canary, result.stderr + json.dumps(self.calls))


if __name__ == '__main__':
    unittest.main()
