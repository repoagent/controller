"""Cleanup source contracts through real compilation and the public workflow route.

Raw external responses are inputs, never Azure scheduling or permission evidence.
"""
import copy
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
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

    def operation(self, mode, changes=None):
        active = IDENTITY_ID if mode.startswith('cleanup-identity-') else JOB_ID
        self.scenario['plan'] = dict(status='Succeeded', changes=(changes if changes is not None else
            [dict(resourceId=active, changeType='Create')]))
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
        self.assertEqual(write['parameters']['parameters']['storageName']['value'], deployment.STORAGE)
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
        self.assertEqual(output['tableResourceId'], deployment.TABLE_ID)
        self.assertEqual(output['tableEndpoint'], f'https://{deployment.STORAGE}.table.core.windows.net/')
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
        self.assertEqual(output['tableResourceId'], deployment.TABLE_ID)
        self.assertEqual(output['tableEndpoint'], f'https://{deployment.STORAGE}.table.core.windows.net/')
        self.assertTrue(output['applied'])
        self.assertFalse(output['releaseAcceptance'])
        self.assertEqual((output['cleanupJobId'], output['cleanupIdentityId']), (JOB_ID, IDENTITY_ID))
        self.assertEqual(output['cleanupIdentityClientId'], CLIENT)
        self.assertGreaterEqual(len(Path(self.env['IMAGE_JOURNAL']).read_text().splitlines()), 2)
        self.assertNotIn('runtimeConfiguration', output)

    def test_invalid_selected_name_refuses_before_source_image_or_azure(self):
        self.env['AUTH_KEY_VAULT_NAME'] = 'invalid--vault'
        self.main = [self.env['GITHUB_SHA'], 'f' * 40]
        result = self.operation('cleanup-apply')
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('AUTH_KEY_VAULT_NAME', result.stderr)
        self.assertEqual(result.stdout, '')
        self.assertEqual(self.calls, [])
        self.assertFalse(Path(self.env['IMAGE_JOURNAL']).exists())
        self.assertEqual(json.loads(Path(self.env['GIT_OBSERVATIONS']).read_text()), self.main)

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


    def test_stable_prior_image_updates_and_manual_schedule_restores(self):
        # C90-03: observing prior A is distinct from accepting desired current B.
        for trigger in ('Schedule', 'Manual'):
            with self.subTest(trigger=trigger):
                prior = copy.deepcopy(self.job)
                prior['properties']['template']['containers'][0]['image'] = activation.IMAGE[:-64] + 'b' * 64
                if trigger == 'Manual':
                    prior['properties']['configuration'].pop('scheduleTriggerConfig')
                    prior['properties']['configuration'].update(triggerType='Manual',
                        manualTriggerConfig=dict(parallelism=1, replicaCompletionCount=1))
                self.scenario['resources'][JOB_ID] = prior
                result = self.operation('cleanup-apply')
                self.assertEqual(result.returncode, 0, result.stderr)
                self.assertEqual(self.assert_single_write(False)['image']['value'], activation.IMAGE)

    def test_source_and_identity_changes_after_plan_never_write(self):
        # C90-07: source and platform identity are independently rechecked.
        baseline = copy.deepcopy(self.scenario)
        for changed in ('source', 'identity', 'job-image', 'job-disappears'):
            with self.subTest(changed=changed):
                self.scenario = copy.deepcopy(baseline)
                self.main = [self.env['GITHUB_SHA']]
                if changed == 'source':
                    self.main += ['c' * 40]
                elif changed == 'identity':
                    drift = cleanup_identity()
                    drift['properties']['clientId'] = deployment.CLIENT_ID
                    self.scenario['postPlanResources'] = {IDENTITY_ID: drift}
                else:
                    self.scenario['resources'][JOB_ID] = copy.deepcopy(self.job)
                    drift = copy.deepcopy(self.job)
                    drift['properties']['template']['containers'][0]['image'] = activation.IMAGE[:-64] + 'c' * 64
                    self.scenario['postPlanResources'] = {JOB_ID: None if changed == 'job-disappears' else drift}
                result = self.operation('cleanup-apply')
                self.assertNotEqual(result.returncode, 0)
                self.assertEqual(self.creates(), [])

    def test_what_if_cannot_authorize_inactive_foreign_or_destructive_writes(self):
        # C90-08: declarations are not the active one-resource write manifest.
        for mode, resource_id, change in (
                ('cleanup-apply', IDENTITY_ID, 'NoChange'),
                ('cleanup-identity-apply', JOB_ID, 'Create'),
                ('cleanup-apply', deployment.APP_ID, 'Modify'),
                ('cleanup-apply', JOB_ID, 'Delete')):
            with self.subTest(mode=mode, resource_id=resource_id, change=change):
                result = self.operation(mode, [dict(resourceId=resource_id, changeType=change)])
                self.assertNotEqual(result.returncode, 0)
                self.assertEqual(self.creates(), [])

    def test_dangerous_job_shapes_never_confirm_and_prior_drift_never_writes(self):
        # C90-09/10: one table owns distinct dangerous shapes, not a mode matrix.
        container = ('properties', 'template', 'containers', 0)
        cases = [
            ('epoch-before', container + ('env', 2, 'value'), 'foreign', False),
            ('profile-before', ('properties', 'workloadProfileName'), 'Dedicated', False),
            ('mutable-before', container + ('image',), 'ghcr.io/repoagent/controller-auth:main', False),
            ('foreign-repository-before', container + ('image',), 'ghcr.io/foreign/auth@sha256:' + 'a' * 64, False),
            ('ownership-before', ('tags', 'repositoryId'), 'foreign', False),
            ('secret-ref-after', container + ('env', 0), dict(name='Broker__TableEndpoint', value='ignored', secretRef='secret'), True),
            ('duplicate-env-after', container + ('env', 1), self.job['properties']['template']['containers'][0]['env'][0], True),
            ('extra-container-after', ('properties', 'template', 'containers'), [{}, {}], True),
            ('init-container-after', ('properties', 'template', 'initContainers'), [{}], True),
            ('probe-after', container + ('probes',), [{}], True),
            ('mount-after', container + ('volumeMounts',), [{}], True),
            ('registry-after', ('properties', 'configuration', 'registries'), [dict(server='private')], True),
            ('old-image-after', container + ('image',), activation.IMAGE[:-64] + 'b' * 64, True),
            ('missing-profile-after', ('properties', 'workloadProfileName'), None, True),
            ('cpu-after', container + ('resources', 'cpu'), 0.5, True),
        ]
        baseline = copy.deepcopy(self.scenario)
        for name, path, value, after in cases:
            with self.subTest(shape=name):
                self.scenario = copy.deepcopy(baseline)
                bad = copy.deepcopy(self.job)
                target = bad
                for key in path[:-1]:
                    target = target[key]
                target[path[-1]] = copy.deepcopy(value)
                self.scenario['postCreateResources' if after else 'resources'][JOB_ID] = bad
                result = self.operation('cleanup-apply')
                self.assertNotEqual(result.returncode, 0)
                self.assertEqual(len(self.creates()), int(after))
                if after:
                    self.assertIn('unconfirmed', result.stderr)
                    self.assertNotIn('"applied": true', result.stdout)

    def test_every_cleanup_dependency_must_be_fresh_after_create(self):
        # C90-10: retain first/existing variants only for cache-bearing active object.
        storage_id = deployment.SCOPE + '/providers/Microsoft.Storage/storageAccounts/' + deployment.STORAGE
        baseline = copy.deepcopy(self.scenario)
        cases = [(rid, False) for rid in (LOG_ID, deployment.ENVIRONMENT_ID, storage_id, deployment.TABLE_ID, IDENTITY_ID)]
        cases += [(JOB_ID, first) for first in (True, False)]
        for missing, first in cases:
            with self.subTest(missing=missing, first=first):
                self.scenario = copy.deepcopy(baseline)
                if not first:
                    self.scenario['resources'][JOB_ID] = copy.deepcopy(self.job)
                self.scenario['postCreateResources'] = {JOB_ID: self.job, missing: None}
                result = self.operation('cleanup-apply')
                self.assert_missing_after_write(result, missing, False)

    def test_changed_image_attestation_immediately_before_create_never_writes(self):
        # C90-07/11: raw anonymous config changes after its first accepted observation.
        source = activation.SKOPEO.replace(
            "os.environ.get('IMAGE_SOURCE', os.environ['AUTH_EXPECTED_SOURCE_SHA'])",
            "(os.environ['AUTH_EXPECTED_SOURCE_SHA'] if len(open(os.environ['IMAGE_JOURNAL']).readlines()) == 1 else 'f' * 40)")
        (self.directory / 'skopeo').write_text('#!' + sys.executable + '\n' + source)
        result = self.operation('cleanup-apply')
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(self.creates(), [])
        self.assertEqual(len(Path(self.env['IMAGE_JOURNAL']).read_text().splitlines()), 2)

    def test_raw_read_failures_are_not_absence_or_grant_authority(self):
        # C90-09: raw provider errors/malformed input stay private and never write.
        baseline = copy.deepcopy(self.scenario)
        for response in ({'error': '(Forbidden) ' + activation.CANARY}, {'raw': '{malformed'},
                         {'error': 'connection timeout ' + activation.CANARY}):
            with self.subTest(response=response):
                self.scenario = copy.deepcopy(baseline)
                self.scenario['responses']['resource show'] = response
                result = self.operation('cleanup-apply')
                self.assertNotEqual(result.returncode, 0)
                self.assertEqual(self.creates(), [])

    def test_reused_log_binding_and_identity_ids_are_checked_before_write(self):
        baseline = copy.deepcopy(self.scenario)
        for resource_id, path, value in (
                (LOG_ID, ('properties', 'retentionInDays'), 90),
                (deployment.ENVIRONMENT_ID, ('properties', 'appLogsConfiguration', 'logAnalyticsConfiguration', 'customerId'), 'foreign'),
                (IDENTITY_ID, ('properties', 'principalId'), 'malformed')):
            with self.subTest(resource_id=resource_id, path=path):
                self.scenario = copy.deepcopy(baseline)
                target = self.scenario['resources'][resource_id]
                for key in path[:-1]:
                    target = target[key]
                target[path[-1]] = value
                result = self.operation('cleanup-apply')
                self.assertNotEqual(result.returncode, 0)
                self.assertEqual(self.creates(), [])

    def test_bootstrap_dependencies_must_remain_present_after_identity_create(self):
        baseline = copy.deepcopy(self.scenario)
        storage_id = deployment.SCOPE + '/providers/Microsoft.Storage/storageAccounts/' + deployment.STORAGE
        for missing in (LOG_ID, deployment.ENVIRONMENT_ID, storage_id, deployment.TABLE_ID):
            with self.subTest(missing=missing):
                self.scenario = copy.deepcopy(baseline)
                self.scenario['resources'].pop(IDENTITY_ID)
                self.scenario['postCreateResources'] = {IDENTITY_ID: cleanup_identity(), missing: None}
                result = self.operation('cleanup-identity-apply')
                self.assert_missing_after_write(result, missing, True)

    def test_public_cost_plan_preserves_scoped_arithmetic_and_open_operator_inputs(self):
        # C90-12: output arithmetic belongs to this project, SDK guarantees do not.
        Path(self.env['GIT_OBSERVATIONS']).write_text(json.dumps(self.main))
        result = deployment.DeploymentProcessTests.run_deploy(self, 'cost-plan')
        self.assertEqual(result.returncode, 0, result.stderr)
        output = json.loads(result.stdout)
        plan = output['cleanupCapacityPlan']
        expected = dict(nominalStartsPerDay=1440, processSeconds=60, sdkTableDispatches=128,
            sdkBatchDispatches=32, logicalQueryPages=16, turnAdmissionDispatches=40, turnAdmissionBatches=8,
            nominalDailySdkTableDispatches=184320, nominalDailySdkBatchDispatches=46080,
            nominalReplicaSecondsPerDay=129600, nominalVcpuSecondsPerDay=32400, nominalGiBSecondsPerDay=64800,
            logRetentionDays=30, logDailyQuotaGb=1, hardSpendingCap=False, globalConcurrencyCap=False)
        self.assertEqual({key: plan[key] for key in expected}, expected)
        envelope = plan['reviewedHttp11Envelope']
        self.assertEqual([envelope[key] for key in ('maxSendsPerSdkDispatch', 'calculatedTableSendsPerStart',
            'calculatedBatchSendsPerStart', 'nominalDailyCalculatedTableSends', 'nominalDailyCalculatedBatchSends')],
            [4, 512, 128, 737280, 184320])
        self.assertEqual(envelope['scope'], 'Azure.Core1.62.0/.NET10.0.13/direct HTTP1.x/no handler authentication')
        self.assertTrue(envelope['batchesIncludedInTotal'])
        self.assertFalse(envelope['measuredWireTraffic'])
        self.assertFalse(envelope['executingRuntimeAttested'])
        self.assertTrue(output['budgetPlan']['missingInputs'])
        self.assertFalse(output['budgetPlan']['readyToApply'])
        self.assertEqual(self.calls, [])


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
        self.assertIn(container['resources'], (dict(cpu=0.25, memory='0.5Gi'),
                                               dict(cpu="[json('0.25')]", memory='0.5Gi')))
        env = {entry['name']: entry for entry in container['env']}
        self.assertEqual(len(container['env']), 4)
        self.assertEqual(set(env), {'Broker__TableEndpoint', 'Broker__TableName',
            'Broker__DeploymentEpoch', 'Broker__ManagedIdentityClientId'})
        self.assertTrue(all(set(entry) == {'name', 'value'} for entry in container['env']))
        self.assertIn("parameters('storageName')", env['Broker__TableEndpoint']['value'])
        self.assertIn('.table.core.windows.net/', env['Broker__TableEndpoint']['value'])
        self.assertEqual(env['Broker__TableName']['value'], "[parameters('tableName')]")
        self.assertEqual(env['Broker__DeploymentEpoch']['value'], "[parameters('deploymentEpoch')]")
        client = env['Broker__ManagedIdentityClientId']['value']
        self.assertRegex(client, r"^\[if\(parameters\('identityOnly'\), '', reference\(")
        self.assertTrue(client.endswith('.clientId)]'), client)
        for name, field in (('cleanupIdentityClientId', 'clientId'), ('cleanupIdentityPrincipalId', 'principalId')):
            value = template['outputs'][name]['value']
            self.assertTrue(value.startswith("[if(parameters('identityOnly'), reference("), value)
            self.assertEqual(value.count('.' + field), 2)
            self.assertEqual(value.count("resourceId('Microsoft.ManagedIdentity/userAssignedIdentities', format('{0}-cleanup', parameters('appName')))"), 2)
            self.assertNotIn('Microsoft.App/jobs', value)
        rbac, = templates['cleanup-rbac']['resources']
        self.assertEqual(rbac['type'], 'Microsoft.Authorization/roleAssignments')
        self.assertIn('Microsoft.Storage/storageAccounts/tableServices/tables', rbac['scope'])
        self.assertIn("parameters('tableName')", rbac['scope'])
        self.assertIn('0a9a7e1f-b9d0-4cc4-a60d-0319b160aaa3', json.dumps(templates['cleanup-rbac']))
        self.assertEqual(rbac['properties']['principalType'], 'ServicePrincipal')
        self.assertIn('-cleanup', rbac['properties']['principalId'])


class CleanupWorkflowTests(unittest.TestCase):
    def test_cleanup_routes_preserve_main_oidc_and_ordinary_release_selection(self):
        # C90-11/13: reuse existing ordinary guard assertions, add only cleanup routing.
        activation.ActivationWorkflowConfigurationTests.test_activation_uses_existing_branch_oidc_scope_and_excludes_normal_jobs(self)
        workflow = activation.WORKFLOW.read_text()
        options = workflow.split('options: [', 1)[1].split(']', 1)[0].split(', ')
        whitelist = workflow.split("contains(fromJSON('[", 1)[1].split("]')", 1)[0]
        for mode in ('cleanup-identity-plan', 'cleanup-identity-apply', 'cleanup-plan', 'cleanup-apply'):
            self.assertIn(mode, options)
            self.assertIn('"' + mode + '"', whitelist)
            self.assertIn(mode, activation.ROUTE.split(')', 1)[0])
        self.assertIn("!startsWith(inputs.operation, 'cleanup-identity-')", workflow)
        self.assertNotIn('AUTH_CLEANUP_READY=', activation.ROUTE)


if __name__ == '__main__':
    unittest.main()
