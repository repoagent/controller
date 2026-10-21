"""Cleanup source contracts through real compilation and the public workflow route.

Raw external responses are inputs, never Azure scheduling or permission evidence.
"""
import copy
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

import test_activation as activation
import test_deploy as deployment

AUTH = deployment.AUTH
IDENTITY_ID = deployment.SCOPE + '/providers/Microsoft.ManagedIdentity/userAssignedIdentities/auth-test-cleanup'
JOB_ID = deployment.SCOPE + '/providers/Microsoft.App/jobs/auth-test-cleanup'
SIGNER_ID = deployment.SCOPE + '/providers/Microsoft.ManagedIdentity/userAssignedIdentities/auth-test-runtime'
CLIENT = '22222222-3333-4444-5555-666666666666'
PRINCIPAL = 'bbbbbbbb-cccc-dddd-eeee-ffffffffffff'
LOG_ID = deployment.SCOPE + '/providers/Microsoft.OperationalInsights/workspaces/auth-test-logs'
WORKSPACE = '33333333-4444-5555-6666-777777777777'


def cleanup_identity():
    return dict(id=IDENTITY_ID, type='Microsoft.ManagedIdentity/userAssignedIdentities',
                location='swedencentral', tags={**ownership(), 'role': 'auth-cleanup-identity'},
                properties=dict(clientId=CLIENT, principalId=PRINCIPAL))


def ownership():
    return dict(repositoryId='1426154477', ownerId='345029828', managedBy='repoagent-auth')


def cleanup_job(epoch):
    values = {'Broker__TableEndpoint': f'https://{deployment.STORAGE}.table.core.windows.net/',
              'Broker__TableName': 'brokerstate', 'Broker__DeploymentEpoch': epoch,
              'Broker__ManagedIdentityClientId': CLIENT}
    return dict(id=JOB_ID, type='Microsoft.App/jobs', location='swedencentral',
                tags={**ownership(), 'role': 'auth-cleanup'},
                identity=dict(type='UserAssigned', userAssignedIdentities={IDENTITY_ID: {}}),
                properties=dict(environmentId=deployment.ENVIRONMENT_ID, workloadProfileName='Consumption',
                    configuration=dict(triggerType='Schedule', replicaRetryLimit=0, replicaTimeout=90,
                        scheduleTriggerConfig=dict(cronExpression='* * * * *', parallelism=1, replicaCompletionCount=1)),
                    template=dict(containers=[dict(name='cleanup', image=activation.IMAGE,
                        command=['dotnet', 'RepoAgent.Auth.dll'], args=['cleanup', '--once'],
                        resources=dict(cpu=0.25, memory='0.5Gi'),
                        env=[dict(name=name, value=value) for name, value in values.items()])])))


class CleanupProcessTests(unittest.TestCase):
    # Reuse infrastructure methods, not TestCase inheritance or imported classes:
    # unittest must not collect the existing activation suite a second time.
    setUpBase = deployment.DeploymentProcessTests.setUp
    staging_app = activation.ActivationProcessTests.staging_app
    invoke = activation.ActivationProcessTests.invoke
    creates = activation.ActivationProcessTests.creates

    def setUp(self):
        activation.ActivationProcessTests.setUp(self)
        self.env = {key: value for key, value in self.env.items()
                    if key not in ('AUTH_SIGNING_KEY_URI', 'AUTH_DATA_PROTECTION_KEY_URI')}
        self.scenario['resources'].pop(deployment.APP_ID)
        self.scenario['resources'][IDENTITY_ID] = cleanup_identity()
        self.scenario['resources'][LOG_ID]['properties'] = dict(customerId=WORKSPACE,
            retentionInDays=30, workspaceCapping=dict(dailyQuotaGb=1))
        self.scenario['resources'][deployment.ENVIRONMENT_ID]['properties']['appLogsConfiguration'] = dict(
            destination='log-analytics', logAnalyticsConfiguration=dict(customerId=WORKSPACE))
        self.job = cleanup_job(self.env['AUTH_DEPLOYMENT_EPOCH'])
        self.scenario['postCreateResources'] = {JOB_ID: self.job}

    def operation(self, mode):
        active = IDENTITY_ID if mode.startswith('cleanup-identity-') else JOB_ID
        self.scenario['plan'] = dict(status='Succeeded', changes=[dict(resourceId=active, changeType='Create')])
        result = self.invoke(mode)
        allowed = {('group', 'show'), ('resource', 'show'), ('deployment', 'group', 'validate'),
                   ('deployment', 'group', 'what-if'), ('deployment', 'group', 'create')}
        for call in self.calls:
            args = call['args']
            self.assertIn(tuple(args[:3] if args[0] == 'deployment' else args[:2]), allowed)
        return result

    def assert_single_write(self, identity_only):
        self.assertEqual(len(self.creates()), 1, self.last_result.stdout + self.last_result.stderr)
        write = self.creates()[0]
        self.assertEqual(write['args'][write['args'].index('--mode') + 1], 'Incremental')
        self.assertEqual(write['parameters']['parameters']['identityOnly']['value'], identity_only)
        self.assertEqual({item['type'] for item in write['template']['resources']},
                         {'Microsoft.App/jobs', 'Microsoft.ManagedIdentity/userAssignedIdentities'})
        return write['parameters']['parameters']

    def test_identity_plan_and_bootstrap_need_no_image_or_http_configuration(self):
        # C90-01: plans never write; platform identity comes from independent readback.
        self.env = {key: value for key, value in self.env.items() if not key.startswith('AUTH_IMAGE')}
        self.scenario['resources'].pop(IDENTITY_ID)
        self.scenario['postCreateResources'] = {IDENTITY_ID: cleanup_identity()}
        plan = self.operation('cleanup-identity-plan')
        self.assertEqual(plan.returncode, 0, plan.stderr)
        self.assertEqual(self.creates(), [])
        self.assertFalse(json.loads(plan.stdout)['applied'])
        result = self.operation('cleanup-identity-apply')
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assert_single_write(True)
        output = json.loads(result.stdout)
        self.assertEqual((output['cleanupIdentityClientId'], output['cleanupIdentityPrincipalId']), (CLIENT, PRINCIPAL))
        self.assertTrue(output['applied'])
        self.assertFalse(output['releaseAcceptance'])
        self.assertFalse(Path(self.env['IMAGE_JOURNAL']).exists())
        self.assertFalse(any(JOB_ID in call['args'] for call in self.calls))

    def test_job_plan_and_apply_use_only_cleanup_identity_and_accepted_image(self):
        # C90-02: actual shell route selects narrow writes and no HTTP lifecycle.
        plan = self.operation('cleanup-plan')
        self.assertEqual(plan.returncode, 0, plan.stderr)
        self.assertEqual(self.creates(), [])
        result = self.operation('cleanup-apply')
        self.assertEqual(result.returncode, 0, result.stderr)
        params = self.assert_single_write(False)
        self.assertEqual(params['image']['value'], activation.IMAGE)
        self.assertEqual(params['deploymentEpoch']['value'], self.env['AUTH_DEPLOYMENT_EPOCH'])
        output = json.loads(result.stdout)
        self.assertTrue(output['applied'])
        self.assertFalse(output['releaseAcceptance'])
        self.assertEqual((output['cleanupJobId'], output['cleanupIdentityId']), (JOB_ID, IDENTITY_ID))
        self.assertEqual(output['cleanupIdentityClientId'], CLIENT)
        self.assertGreaterEqual(len(Path(self.env['IMAGE_JOURNAL']).read_text().splitlines()), 2)
        self.assertNotIn('runtimeConfiguration', output)

    def test_signer_identity_is_refused_before_write(self):
        # C90-05: no signing env is necessary for a signer attachment to be unsafe.
        self.job['identity']['userAssignedIdentities'] = {SIGNER_ID: {}}
        self.scenario['resources'][JOB_ID] = self.job
        result = self.operation('cleanup-apply')
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(self.creates(), [])
        self.assertIn('cleanup', result.stderr.lower())

    def test_signer_identity_readback_never_confirms_success(self):
        # C90-05: a successful ARM response cannot bless the wrong actual identity.
        self.job['identity']['userAssignedIdentities'] = {SIGNER_ID: {}}
        result = self.operation('cleanup-apply')
        self.assert_single_write(False)
        self.assertNotEqual(result.returncode, 0)
        self.assertNotIn('"applied": true', result.stdout)
        self.assertIn('unconfirmed', result.stderr.lower())

    def test_missing_job_after_existing_valid_observation_is_unconfirmed(self):
        # C90-06: valid cached state must not survive an absent post-create read.
        self.scenario['resources'][JOB_ID] = copy.deepcopy(self.job)
        self.scenario['postCreateResources'] = {JOB_ID: None}
        result = self.operation('cleanup-apply')
        self.assert_missing_after_write(result, JOB_ID, False)

    def test_identity_bootstrap_requires_actual_created_identity(self):
        # C90-06: identity-only mode permits absence before, never after, its write.
        self.env = {key: value for key, value in self.env.items() if not key.startswith('AUTH_IMAGE')}
        self.scenario['resources'].pop(IDENTITY_ID)
        self.scenario['postCreateResources'] = {IDENTITY_ID: None}
        result = self.operation('cleanup-identity-apply')
        self.assert_missing_after_write(result, IDENTITY_ID, True)

    def assert_missing_after_write(self, result, resource_id, identity_only):
        self.assert_single_write(identity_only)
        create = next(index for index, call in enumerate(self.calls)
                      if call['args'][:3] == ['deployment', 'group', 'create'])
        reads = [call for call in self.calls[create + 1:]
                 if call['args'][:2] == ['resource', 'show'] and resource_id in call['args']]
        self.assertTrue(reads, 'Confirmation needs a fresh resource observation after create')
        self.assertEqual(reads[-1]['responseCode'], 3)
        self.assertNotEqual(result.returncode, 0)
        self.assertNotIn('"applied": true', result.stdout)
        self.assertIn('unconfirmed', result.stderr.lower())


class CleanupCompiledContractTests(unittest.TestCase):
    def test_conditional_job_shape_and_operator_table_grant(self):
        # C90-04: inspect actual compiler output, never simulate ARM evaluation.
        compiler = shutil.which(os.environ.get('BICEP_BIN', 'bicep')) or str(Path.home() / '.azure/bin/bicep')
        with tempfile.TemporaryDirectory() as directory:
            templates = {}
            for name in ('cleanup', 'cleanup-rbac'):
                output = Path(directory) / (name + '.json')
                result = subprocess.run([compiler, 'build', str(AUTH / (name + '.bicep')), '--outfile', str(output)],
                                        capture_output=True, text=True, timeout=60)
                self.assertEqual(result.returncode, 0, result.stderr)
                templates[name] = json.loads(output.read_text())
        template = templates['cleanup']
        self.assertTrue(template['$schema'].endswith('/deploymentTemplate.json#'))
        resources = template['resources']
        self.assertEqual(len(resources), 2)
        identity, = [item for item in resources if item['type'] == 'Microsoft.ManagedIdentity/userAssignedIdentities']
        job, = [item for item in resources if item['type'] == 'Microsoft.App/jobs']
        self.assertEqual(identity['condition'], "[parameters('identityOnly')]")
        self.assertEqual(job['condition'], "[not(parameters('identityOnly'))]")
        self.assertEqual(job['apiVersion'], '2025-01-01')
        self.assertIn('-cleanup', job['name'])
        self.assertIn('-cleanup', identity['name'])
        self.assertEqual(job['identity']['type'], 'UserAssigned')
        attached, = job['identity']['userAssignedIdentities']
        self.assertIn('-cleanup', attached)
        self.assertNotIn('-runtime', attached)
        props = job['properties']
        self.assertEqual(set(props), {'environmentId', 'workloadProfileName', 'configuration', 'template'})
        self.assertEqual(props['workloadProfileName'], 'Consumption')
        self.assertIn('-environment', props['environmentId'])
        self.assertEqual(props['configuration'], dict(triggerType='Schedule', replicaRetryLimit=0, replicaTimeout=90,
            scheduleTriggerConfig=dict(cronExpression='* * * * *', parallelism=1, replicaCompletionCount=1)))
        self.assertEqual(set(props['template']), {'containers'})
        container, = props['template']['containers']
        self.assertEqual(set(container), {'name', 'image', 'command', 'args', 'env', 'resources'})
        self.assertEqual(container['command'], ['dotnet', 'RepoAgent.Auth.dll'])
        self.assertEqual(container['args'], ['cleanup', '--once'])
        self.assertEqual(container['image'], "[parameters('image')]")
        self.assertEqual(container['resources'], dict(cpu=0.25, memory='0.5Gi'))
        env = {entry['name']: entry for entry in container['env']}
        self.assertEqual(len(container['env']), 4)
        self.assertEqual(set(env), {'Broker__TableEndpoint', 'Broker__TableName',
            'Broker__DeploymentEpoch', 'Broker__ManagedIdentityClientId'})
        self.assertTrue(all(set(entry) == {'name', 'value'} for entry in container['env']))
        self.assertEqual(env['Broker__TableName']['value'], "[parameters('tableName')]")
        self.assertEqual(env['Broker__DeploymentEpoch']['value'], "[parameters('deploymentEpoch')]")
        client = env['Broker__ManagedIdentityClientId']['value']
        self.assertRegex(client, r"^\[if\(parameters\('identityOnly'\), '', reference\(")
        self.assertTrue(client.endswith('.clientId)]'), client)
        for name, field in (('cleanupIdentityClientId', 'clientId'), ('cleanupIdentityPrincipalId', 'principalId')):
            value = template['outputs'][name]['value']
            self.assertTrue(value.startswith("[if(parameters('identityOnly'), reference("), value)
            self.assertEqual(value.count('.' + field), 2)
            self.assertNotIn('Microsoft.App/jobs', value)
        rbac, = templates['cleanup-rbac']['resources']
        self.assertEqual(rbac['type'], 'Microsoft.Authorization/roleAssignments')
        self.assertIn('Microsoft.Storage/storageAccounts/tableServices/tables', rbac['scope'])
        self.assertIn("parameters('tableName')", rbac['scope'])
        self.assertIn('0a9a7e1f-b9d0-4cc4-a60d-0319b160aaa3', json.dumps(templates['cleanup-rbac']))
        self.assertEqual(rbac['properties']['principalType'], 'ServicePrincipal')
        self.assertIn('-cleanup', rbac['properties']['principalId'])


if __name__ == '__main__':
    unittest.main()
