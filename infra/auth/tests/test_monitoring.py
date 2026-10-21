"""Compiled deployment and real process contracts; no hosted Azure/KQL claims."""
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
import test_templates as templates

AUTH = deployment.AUTH
RECIPIENT = 'private-monitoring-sentinel@example.test'


class MonitoringTemplates(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.temp = tempfile.TemporaryDirectory()
        cls.addClassCleanup(cls.temp.cleanup)
        compiler = shutil.which(os.environ.get('BICEP_BIN', 'bicep')) or str(Path.home() / '.azure/bin/bicep')
        cls.compiled = {}
        for name in ('monitoring', 'monitoring-rbac', 'main', 'cleanup'):
            output = Path(cls.temp.name) / (name + '.json')
            subprocess.run([compiler, 'build', str(AUTH / (name + '.bicep')), '--outfile', str(output)], check=True, capture_output=True, timeout=60)
            cls.compiled[name] = json.loads(output.read_text())

    def test_private_recipient_aad_and_currency_neutral_ten_year_budget(self):
        template = self.compiled['monitoring']
        self.assertEqual(template['parameters']['recipient'], {'type': 'securestring'})
        self.assertEqual(template.get('outputs', {}), {})
        self.assertFalse(template['parameters']['enableAlerts']['defaultValue'])
        self.assertEqual({r['type'] for r in template['resources']}, {'Microsoft.Insights/components', 'Microsoft.Insights/actionGroups',
            'Microsoft.Consumption/budgets', 'Microsoft.Insights/metricAlerts', 'Microsoft.Insights/scheduledQueryRules'})
        insights = templates.resource(template, 'Microsoft.Insights/components')['properties']
        self.assertTrue(insights['DisableLocalAuth'])
        self.assertEqual(insights['IngestionMode'], 'LogAnalytics')
        self.assertIn("format('{0}-logs', parameters('appName'))", insights['WorkspaceResourceId'])
        group = templates.resource(template, 'Microsoft.Insights/actionGroups')['properties']
        self.assertEqual(group['emailReceivers'], [dict(name='operator', emailAddress="[parameters('recipient')]", useCommonAlertSchema=True)])
        budget = templates.resource(template, 'Microsoft.Consumption/budgets')['properties']
        self.assertEqual(budget['amount'], "[parameters('budgetAmount')]")
        self.assertEqual(budget['timeGrain'], 'Monthly')
        self.assertEqual(budget['timePeriod'], dict(startDate="[parameters('budgetStartDate')]"))
        self.assertNotIn('currency', budget)
        # Bicep's actual lambda input carries the three notification thresholds.
        self.assertIn('createArray(50, 80, 100)', budget['notifications'])
        self.assertEqual(template['parameters']['budgetAmount']['minValue'], 0)

    def test_exact_five_unsplit_alert_declarations(self):
        template = self.compiled['monitoring']
        metrics, queries = template['variables']['metricRules'], template['variables']['queryRules']
        self.assertEqual([(r['suffix'], r['metric'], r['aggregation'], r['operator'], r['threshold'], r['window']) for r in metrics], [
            ('cleanup-execution-failed', 'Executions', 'Total', 'GreaterThan', 0, 'PT5M'),
            ('http-errors', 'Requests', 'Total', 'GreaterThanOrEqual', 5, 'PT5M'),
            ('memory-pressure', 'WorkingSetBytes', 'Average', 'GreaterThan', 429496729, 'PT15M')])
        self.assertEqual([(r['suffix'], r['column'], r['threshold'], r['frequency'], r['window']) for r in queries], [
            ('cleanup-observation', 'Alert', 0, 'PT5M', 'PT15M'), ('telemetry-volume', 'Bytes', 50000000, 'PT15M', 'P1D')])
        self.assertEqual(metrics[0]['dimensions'], [dict(name='state', operator='Include', values=["[parameters('jobFailureState')]"])])
        self.assertEqual(metrics[1]['dimensions'], [dict(name='statusCodeCategory', operator='Include', values=['5xx'])])
        self.assertEqual(metrics[2]['dimensions'], [])
        action = "[resourceId('Microsoft.Insights/actionGroups', format('{0}-alerts', parameters('appName')))]"
        for kind in ('Microsoft.Insights/metricAlerts', 'Microsoft.Insights/scheduledQueryRules'):
            rule = templates.resource(template, kind)
            self.assertEqual(rule['condition'], "[parameters('enableAlerts')]")
            criterion, = rule['properties']['criteria']['allOf']
            if kind.endswith('scheduledQueryRules'):
                self.assertNotIn('dimensions', criterion)
            self.assertNotIn('overrideQueryTimeRange', rule['properties'])
            self.assertEqual(rule['properties']['actions'], dict(actionGroups=[action]) if kind.endswith('scheduledQueryRules') else [dict(actionGroupId=action)])
        # Query execution and actual AppMetrics.Max mapping are root-hosted gates.

    def test_exact_scoped_publishers_and_only_two_cleanup_environment_additions(self):
        rbac = self.compiled['monitoring-rbac']
        assignments = rbac['resources']
        self.assertEqual(len(assignments), 2)
        self.assertEqual({r['type'] for r in assignments}, {'Microsoft.Authorization/roleAssignments'})
        self.assertEqual({r['properties']['principalId'] for r in assignments}, {
            "[reference(resourceId('Microsoft.ManagedIdentity/userAssignedIdentities', format('{0}-" + suffix + "', parameters('appName'))), '2023-01-31').principalId]"
            for suffix in ('runtime', 'cleanup')})
        for r in assignments:
            self.assertEqual(r['scope'], "[resourceId('Microsoft.Insights/components', format('{0}-insights', parameters('appName')))]")
            self.assertEqual(r['properties']['principalType'], 'ServicePrincipal')
            self.assertEqual(r['properties']['roleDefinitionId'], "[variables('publisher')]")
        self.assertEqual(rbac['variables']['publisher'], "[subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '3913510d-42f4-4e42-8a64-420c390055eb')]")
        main = self.compiled['main']
        logs = templates.resource(main, 'Microsoft.OperationalInsights/workspaces')['properties']
        self.assertEqual((logs['retentionInDays'], logs['workspaceCapping']['dailyQuotaGb']), (30, "[json('0.1')]"))
        self.assertEqual(templates.resource(main, 'Microsoft.App/managedEnvironments')['properties']['appLogsConfiguration'], dict(destination='none'))
        job = templates.resource(self.compiled['cleanup'], 'Microsoft.App/jobs')['properties']
        env = {e['name']: e['value'] for e in job['template']['containers'][0]['env']}
        self.assertEqual(set(env), {'Broker__TableEndpoint', 'Broker__TableName', 'Broker__DeploymentEpoch', 'Broker__ManagedIdentityClientId',
            'APPLICATIONINSIGHTS_CONNECTION_STRING', 'APPLICATIONINSIGHTS_STATSBEAT_DISABLED'})
        self.assertEqual(env['APPLICATIONINSIGHTS_STATSBEAT_DISABLED'], 'true')
        self.assertEqual(env['APPLICATIONINSIGHTS_CONNECTION_STRING'], "[parameters('monitoringConnectionString')]")
        self.assertNotIn('probes', job['template']['containers'][0])


class MonitoringProcess(unittest.TestCase):
    setUpBase = deployment.DeploymentProcessTests.setUp
    staging_app = activation.ActivationProcessTests.staging_app
    invoke = activation.ActivationProcessTests.invoke
    creates = activation.ActivationProcessTests.creates

    def setUp(self):
        activation.ActivationProcessTests.setUp(self)
        self.input = self.directory / 'monitoring-private.json'
        self.input.write_text(json.dumps(dict(recipient=RECIPIENT, budgetAmount=7, budgetStartDate='2026-10-01')))
        self.input.chmod(0o600)
        self.env['AUTH_MONITORING_INPUT_FILE'] = str(self.input)
        self.resources = [deployment.INSIGHTS_ID, deployment.SCOPE + '/providers/Microsoft.Insights/actionGroups/auth-test-alerts',
            deployment.SCOPE + '/providers/Microsoft.Consumption/budgets/auth-test-monthly-budget']
        self.scenario['resources'].pop(deployment.INSIGHTS_ID)
        self.scenario['plan'] = dict(status='Succeeded', changes=[dict(resourceId=r, changeType='Create') for r in self.resources])

    def operation(self, mode='monitoring-plan'):
        result = self.invoke(mode, 'exec bash infra/auth/deploy.sh "$AUTH_OPERATION"')
        self.assertNotIn(RECIPIENT, result.stdout + result.stderr)
        for call in self.calls:
            self.assertNotIn(RECIPIENT, json.dumps(call['args']))
            if 'parameters' in call:
                self.assertEqual(call['parameterMode'], 0o600)
        return result

    def test_plan_has_exact_manifest_no_writes_or_readiness_and_redacts_private_input(self):
        result = self.operation()
        self.assertEqual(result.returncode, 0, result.stderr)
        value = json.loads(result.stdout)
        self.assertEqual(set(value['resources']), set(self.resources))
        self.assertFalse(value['applied']); self.assertFalse(value['releaseAcceptance']); self.assertFalse(value['hardSpendingCap'])
        self.assertEqual(value['budgetAmount'], 7)
        self.assertEqual(self.creates(), [])

    def test_apply_confirms_only_exact_resources_and_readback_without_activating(self):
        inputs = json.loads(self.input.read_text())
        insights = copy.deepcopy(deployment.monitoring_resources()[deployment.INSIGHTS_ID])
        insights['properties']['IngestionMode'] = 'LogAnalytics'
        group = dict(id=self.resources[1], type='Microsoft.Insights/actionGroups', location='global',
            tags=insights['tags'], properties=dict(enabled=True, groupShortName='repoauth',
                emailReceivers=[dict(name='operator', emailAddress=RECIPIENT, useCommonAlertSchema=True)]))
        budget = dict(id=self.resources[2], type='Microsoft.Consumption/budgets', properties=dict(
            amount=7, category='Cost', timeGrain='Monthly', timePeriod=dict(startDate=inputs['budgetStartDate']),
            notifications={f'actual{n}': dict(enabled=True, operator='GreaterThanOrEqualTo', threshold=n,
                thresholdType='Actual', contactEmails=[RECIPIENT], contactGroups=[], contactRoles=[]) for n in (50, 80, 100)}))
        self.scenario['postCreateResources'] = {r['id']: r for r in (insights, group, budget)}
        self.scenario['responses']['deployment group what-if'] = [dict(body=self.scenario['plan']), dict(body=dict(
            status='Succeeded', changes=[dict(resourceId=r, changeType='NoChange') for r in self.resources]))]
        result = self.operation('monitoring-apply')
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(len(self.creates()), 1)
        self.assertFalse(json.loads(result.stdout)['releaseAcceptance'])
        self.assertTrue(json.loads(result.stdout)['applied'])
        self.assertEqual({r['type'] for r in self.creates()[0]['template']['resources']}, {
            'Microsoft.Insights/components', 'Microsoft.Insights/actionGroups', 'Microsoft.Consumption/budgets',
            'Microsoft.Insights/metricAlerts', 'Microsoft.Insights/scheduledQueryRules'})
        self.assertFalse(any(call['args'][0] in ('containerapp', 'role') for call in self.calls))

    def test_private_input_and_foreign_ownership_refuse_before_write(self):
        original = self.input.read_text()
        for inputs in ({}, {'recipient': RECIPIENT, 'budgetAmount': -1}, {'recipient': 'invalid'}, {'recipient': RECIPIENT, 'budgetAmount': True}):
            with self.subTest(inputs=inputs):
                self.input.write_text(json.dumps(inputs))
                result = self.operation('monitoring-apply')
                self.assertNotEqual(result.returncode, 0); self.assertEqual(self.creates(), [])
        self.input.write_text(original); self.input.chmod(0o644)
        self.assertNotEqual(self.operation().returncode, 0); self.assertEqual(self.creates(), [])
        self.input.chmod(0o600)
        logs = next(v for v in self.scenario['resources'].values() if v['type'] == 'Microsoft.OperationalInsights/workspaces')
        logs['tags']['ownerId'] = 'foreign'
        self.assertNotEqual(self.operation('monitoring-apply').returncode, 0); self.assertEqual(self.creates(), [])
