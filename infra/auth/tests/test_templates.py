"""Inspect the project's deployment contract after real Bicep compilation."""
import copy
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import tempfile
import unittest

AUTH = Path(__file__).resolve().parents[1]
TABLE_RESOURCE_ID = "[resourceId('Microsoft.Storage/storageAccounts/tableServices/tables', parameters('storageName'), 'default', parameters('tableName'))]"
IDENTITY_CLIENT_ID = "[reference(resourceId('Microsoft.ManagedIdentity/userAssignedIdentities', format('{0}-runtime', parameters('appName'))), '2023-01-31').clientId]"
IDENTITY_PRINCIPAL_ID = IDENTITY_CLIENT_ID.replace('.clientId]', '.principalId]')


def resource(template, kind):
    matches = [item for item in template['resources'] if item['type'] == kind]
    if len(matches) != 1:
        raise AssertionError(f'Expected one {kind}, got {len(matches)}')
    return matches[0]


def owned_origin_expression(template, expression):
    """Normalize only the origin's variable, interpolation and concatenation spellings."""
    def expand(value):
        value = value.removeprefix('[').removesuffix(']')
        for _ in range(len(template.get('variables', {})) + 1):
            expanded = re.sub(r"variables\('([^']+)'\)",
                              lambda match: template['variables'][match[1]].removeprefix('[').removesuffix(']'), value)
            if expanded == value:
                if "variables(" in expanded:
                    raise AssertionError('Unresolved origin variable')
                return expanded
            value = expanded
        raise AssertionError('Cyclic origin variables')

    def environment_name(value):
        return expand(value).replace("format('{0}-environment', parameters('appName'))",
                                     "concat(parameters('appName'), '-environment')")

    environment = resource(template, 'Microsoft.App/managedEnvironments')
    expression = environment_name(expression)
    owned_domain = (r"reference\(resourceId\('Microsoft.App/managedEnvironments', "
                    + re.escape(environment_name(environment['name']))
                    + r"\), '[^']+'\)\.defaultDomain")
    expression = re.sub(owned_domain, 'OWNED_ENVIRONMENT_DOMAIN', expression)
    return expression.replace("format('https://{0}.{1}', parameters('appName'), OWNED_ENVIRONMENT_DOMAIN)",
                              "concat('https://', parameters('appName'), '.', OWNED_ENVIRONMENT_DOMAIN)")


class CompiledDeploymentContractTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.temp = tempfile.TemporaryDirectory()
        cls.addClassCleanup(cls.temp.cleanup)
        cls.directory = Path(cls.temp.name)
        compiler = shutil.which(os.environ.get('BICEP_BIN', 'bicep')) or str(Path.home() / '.azure/bin/bicep')
        cls.templates = {}
        for name in ('main', 'runtime-rbac'):
            output = cls.directory / (name + '.json')
            subprocess.run([compiler, 'build', str(AUTH / (name + '.bicep')), '--outfile', str(output)],
                           capture_output=True, text=True, check=True, timeout=60)
            cls.templates[name] = json.loads(output.read_text())

    def test_owned_account_contains_one_dedicated_table_and_separate_blob_keyring(self):
        template = self.templates['main']
        table = resource(template, 'Microsoft.Storage/storageAccounts/tableServices/tables')
        self.assertEqual(table['name'], "[format('{0}/{1}/{2}', parameters('storageName'), 'default', parameters('tableName'))]")
        self.assertIn("[resourceId('Microsoft.Storage/storageAccounts', parameters('storageName'))]", table['dependsOn'])
        self.assertEqual(table['properties'], {})
        self.assertNotIn('tags', table)
        blob = resource(template, 'Microsoft.Storage/storageAccounts/blobServices/containers')
        self.assertIn("'data-protection'", blob['name'])
        self.assertEqual(blob['properties']['publicAccess'], 'None')
        storage = resource(template, 'Microsoft.Storage/storageAccounts')
        self.assertEqual(storage['kind'], 'StorageV2')
        self.assertEqual(storage['sku']['name'], 'Standard_LRS')
        self.assertFalse(storage['properties']['allowSharedKeyAccess'])
        self.assertTrue(storage['properties']['supportsHttpsTrafficOnly'])
        self.assertEqual(storage['properties']['minimumTlsVersion'], 'TLS1_2')
        self.assertEqual(template['outputs']['tableResourceId']['value'], TABLE_RESOURCE_ID)
        self.assertEqual(template['outputs']['tableName']['value'], "[parameters('tableName')]")
        self.assertNotIn('listKeys(', json.dumps(template['outputs']))
        self.assertFalse(any(item['type'].startswith('Microsoft.Authorization/') for item in template['resources']))
        self.assertFalse(any(item['type'].startswith('Microsoft.Consumption/') for item in template['resources']))

    def test_runtime_origin_binds_owned_environment_domain_and_is_output(self):
        template = self.templates['main']
        app = resource(template, 'Microsoft.App/containerApps')
        env = {entry['name']: entry['value']
               for entry in app['properties']['template']['containers'][0]['env']}
        self.assertIn('Broker__BrokerOrigin', env)
        self.assertEqual(app['name'], "[parameters('appName')]")
        expected = "concat('https://', parameters('appName'), '.', OWNED_ENVIRONMENT_DOMAIN)"
        for expression in (env['Broker__BrokerOrigin'], template['outputs']['brokerOrigin']['value']):
            self.assertEqual(owned_origin_expression(template, expression), expected)

    def test_container_binds_exact_production_table_identity_epoch_and_http_options(self):
        template = self.templates['main']
        app = resource(template, 'Microsoft.App/containerApps')
        container = app['properties']['template']['containers'][0]
        env = {entry['name']: entry['value'] for entry in container['env']}
        self.assertEqual({name: value for name, value in env.items() if name.startswith('Broker__')}, {
            'Broker__BrokerOrigin': env['Broker__BrokerOrigin'],
            'Broker__TableEndpoint': "[reference(resourceId('Microsoft.Storage/storageAccounts', parameters('storageName')), '2023-05-01').primaryEndpoints.table]",
            'Broker__TableName': "[parameters('tableName')]",
            'Broker__DeploymentEpoch': "[parameters('deploymentEpoch')]",
            'Broker__ManagedIdentityClientId': IDENTITY_CLIENT_ID,
            'Broker__RequestLimitPerMinute': "[string(parameters('requestLimitPerMinute'))]",
            'Broker__ConcurrentRequestLimit': "[string(parameters('concurrentRequestLimit'))]",
            'Broker__RequestTimeoutSeconds': "[string(parameters('requestTimeoutSeconds'))]"})
        self.assertEqual(template['outputs']['tableEndpoint']['value'], env['Broker__TableEndpoint'])
        self.assertEqual(template['outputs']['runtimeIdentityClientId']['value'], IDENTITY_CLIENT_ID)
        self.assertEqual(template['outputs']['runtimeIdentityPrincipalId']['value'], IDENTITY_PRINCIPAL_ID)
        self.assertEqual(app['identity']['type'], 'UserAssigned')
        self.assertEqual(env['Auth__SigningKeyUri'], "[parameters('signingKeyUri')]")
        self.assertEqual(env['Auth__DataProtectionKeyUri'], "[parameters('dataProtectionKeyUri')]")
        self.assertIn('data-protection/keyring.xml', env['Auth__DataProtectionBlobUri'])
        self.assertNotIn('Auth__StoreResourceId', env)
        self.assertNotIn('Auth__OAuthClientSecret', env)
        self.assertNotIn('secrets', app['properties']['configuration'])
        self.assertNotIn('oauthClientSecretUri', template['parameters'])
        self.assertNotIn('storeResourceId', template['parameters'])
        for name, default, maximum in (('requestLimitPerMinute', 120, 10000),
                                        ('concurrentRequestLimit', 8, 128), ('requestTimeoutSeconds', 15, 60)):
            self.assertEqual(template['parameters'][name], {
                'type': 'int', 'defaultValue': default, 'minValue': 1, 'maxValue': maximum})

    def test_pilot_capacity_and_log_limits_remain_explicit(self):
        template = self.templates['main']
        app = resource(template, 'Microsoft.App/containerApps')
        compute = app['properties']['template']
        self.assertEqual(compute['scale']['minReplicas'], 0)
        self.assertEqual(compute['scale']['maxReplicas'], 1)
        self.assertEqual(compute['containers'][0]['resources'], {'cpu': "[json('0.25')]", 'memory': '0.5Gi'})
        logs = resource(template, 'Microsoft.OperationalInsights/workspaces')['properties']
        self.assertEqual(logs['retentionInDays'], 30)
        self.assertEqual(logs['workspaceCapping']['dailyQuotaGb'], 1)

    def test_guard_refuses_capacity_drift_and_shared_key_reenablement(self):
        for field in ('replicas', 'cpu', 'memory', 'retention', 'logQuota', 'sharedKey'):
            with self.subTest(field=field):
                template = copy.deepcopy(self.templates['main'])
                app = resource(template, 'Microsoft.App/containerApps')['properties']['template']
                logs = resource(template, 'Microsoft.OperationalInsights/workspaces')['properties']
                if field == 'replicas':
                    app['scale']['maxReplicas'] = 2
                elif field in ('cpu', 'memory'):
                    app['containers'][0]['resources'][field] = 1 if field == 'cpu' else '1Gi'
                elif field == 'retention':
                    logs['retentionInDays'] = 90
                elif field == 'logQuota':
                    logs['workspaceCapping']['dailyQuotaGb'] = -1
                else:
                    resource(template, 'Microsoft.Storage/storageAccounts')['properties']['allowSharedKeyAccess'] = True
                output = self.directory / 'drift.json'
                output.write_text(json.dumps(template))
                result = subprocess.run(['python3', str(AUTH / 'validate_arm.py'), str(output)],
                                        capture_output=True, text=True, timeout=10)
                self.assertNotEqual(result.returncode, 0)
                self.assertIn('shared-key' if field == 'sharedKey' else 'capacity/log envelope', result.stderr)

    def test_operator_template_assigns_table_contributor_only_at_exact_table_scope(self):
        template = self.templates['runtime-rbac']
        assignments = [item for item in template['resources'] if item['type'] == 'Microsoft.Authorization/roleAssignments']
        table_access = [item for item in assignments if item['scope'] == TABLE_RESOURCE_ID]
        self.assertEqual(len(table_access), 1)
        self.assertEqual(table_access[0]['properties'], {
            'principalId': IDENTITY_PRINCIPAL_ID, 'principalType': 'ServicePrincipal',
            'roleDefinitionId': "[variables('tableContributor')]"})
        self.assertEqual(template['variables']['tableContributor'],
                         "[subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '0a9a7e1f-b9d0-4cc4-a60d-0319b160aaa3')]")
        self.assertEqual(len(assignments), 4)  # Signing, wrapping, Blob keyring and Table.
        self.assertEqual({item['role'] for item in template['variables']['permissions']}, {'sign', 'wrap', 'keyring'})
        self.assertNotIn('oauthClientSecretName', template['parameters'])
        self.assertNotIn('getSecret', json.dumps(template))
        self.assertEqual(template['outputs']['tableResourceId']['value'], TABLE_RESOURCE_ID)


if __name__ == '__main__':
    unittest.main()
