"""Read-only operator CLI at its command/output boundary; not Azure acceptance."""
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
SCRIPT = ROOT / 'scripts/delivery/broker-diagnostics.py'
SUB = '46f7e5d6-05f3-435b-8089-6c63a203dbfa'
TENANT = '3c356090-bfe9-459b-a2ac-064231707881'
GROUP = '/subscriptions/' + SUB + '/resourceGroups/rg-repoagent-controller-auth'
SHA = 'a' * 40
SECRET = 'DO_NOT_EXPORT_SECRET'
DOUBLE = '''import json,os,sys
from pathlib import Path
args=sys.argv[1:]
with open(os.environ['TEST_LEDGER'],'a') as output: output.write(json.dumps([Path(sys.argv[0]).name,*args])+'\\n')
if Path(sys.argv[0]).name == 'git':
 if args == ['rev-parse','HEAD']: print('a'*40)
 elif args == ['remote','get-url','origin']: print('https://github.com/repoagent/controller.git')
 elif args == ['ls-remote','--exit-code','origin','refs/heads/main']: print('a'*40+'\\trefs/heads/main')
 else: sys.exit(91)
elif args[:2] == ['account','show']:
 print(json.dumps(dict(id='46f7e5d6-05f3-435b-8089-6c63a203dbfa',tenantId='3c356090-bfe9-459b-a2ac-064231707881')))
elif args[:3] == ['rest','--method','get']:
 from urllib.parse import urlsplit
 url=args[args.index('--url')+1]; key=urlsplit(url).path
 fixtures=json.loads(Path(os.environ['TEST_FIXTURES']).read_text())
 if key not in fixtures: sys.exit(92)
 value=fixtures[key]
 if value == 'FAIL': sys.exit('DO_NOT_EXPORT_SECRET provider error')
 if value == 'OVERSIZE': sys.stdout.write('x'*2100000)
 else: print(json.dumps(value))
else: sys.exit(93)
'''


@unittest.skipIf(os.name == 'nt', 'Command transports use Linux executable scripts')
class BrokerDiagnosticsTests(unittest.TestCase):
    def invoke(self, change=None, **environment):
        folder = Path(self.enterContext(tempfile.TemporaryDirectory(prefix='broker-diagnostics-')))
        binaries = folder / 'bin'
        binaries.mkdir()
        for name in ('az', 'git'):
            path = binaries / name
            path.write_text('#!' + sys.executable + '\n' + DOUBLE)
            path.chmod(0o700)
        def resource(kind, name, properties):
            return dict(id=GROUP + '/providers/' + kind + '/' + name,
                        tags=dict(repositoryId='1426154477', ownerId='345029828', managedBy='repoagent-auth'),
                        properties=properties)
        image = 'ghcr.io/repoagent/controller-auth@sha256:' + 'b'*64
        containers = [dict(image=image, env=[dict(name='PRIVATE', value=SECRET)], args=[SECRET])]
        api = GROUP + '/providers/Microsoft.App/containerApps/repoagent-auth'
        job = GROUP + '/providers/Microsoft.App/jobs/repoagent-auth-cleanup'
        fixtures = {
            GROUP: dict(id=GROUP, location='swedencentral', tags=dict(private=SECRET)),
            api: resource('Microsoft.App/containerApps', 'repoagent-auth', dict(provisioningState='Succeeded',
                latestReadyRevisionName='repoagent-auth--ready', configuration=dict(activeRevisionsMode='Multiple', secrets=[SECRET]),
                template=dict(containers=containers, scale=dict(minReplicas=0, maxReplicas=1)))),
            job: resource('Microsoft.App/jobs', 'repoagent-auth-cleanup', dict(provisioningState='Succeeded',
                configuration=dict(triggerType='Schedule', replicaTimeout=90, replicaRetryLimit=0,
                    scheduleTriggerConfig=dict(cronExpression='* * * * *', parallelism=1, replicaCompletionCount=1)),
                template=dict(containers=containers))),
            job + '/executions': dict(value=[dict(name='repoagent-auth-cleanup-123',
                properties=dict(status='Failed', startTime='2026-10-28T13:40:00Z', endTime='2026-10-28T13:41:00Z',
                                template=dict(containers=containers)))], nextLink='https://private.invalid/'+SECRET),
            job + '/executions/repoagent-auth-cleanup-123/replicas': dict(value=[dict(properties=dict(containers=[
                dict(name='cleanup', ready=False, restartCount=1, runningState='NotRunning', runningStateDetails=SECRET)]))]),
            GROUP + '/providers/Microsoft.AlertsManagement/alerts': dict(value=[dict(name='acdb87f2-5f5e-41f7-b568-848ebab9f000',
                properties=dict(essentials=dict(alertRule='repoagent-auth-cleanup-execution-failed', targetResource=job,
                    monitorCondition='Resolved', severity='Sev2', startDateTime='2026-10-28T13:40:00Z',
                    lastModifiedDateTime='2026-10-28T16:07:00Z'), context=dict(private=SECRET)))]),
            GROUP + '/providers/Microsoft.OperationalInsights/workspaces/repoagent-auth-logs':
                dict(id=GROUP + '/providers/Microsoft.OperationalInsights/workspaces/repoagent-auth-logs',
                     properties=dict(customerId='11111111-2222-3333-4444-555555555555')),
            '/v1/workspaces/11111111-2222-3333-4444-555555555555/query': dict(tables=[dict(name='PrimaryResult',
                columns=[dict(name=name) for name in ('Name','Reason','Samples','LastObserved','Total','Maximum')],
                rows=[['repoagent.auth.cleanup.failures','',1,'2026-10-28T13:40:00Z',1,1]])])}
        if change:
            change(fixtures, job)
        fixture_path, ledger = folder / 'fixtures.json', folder / 'ledger.jsonl'
        fixture_path.write_text(json.dumps(fixtures))
        env = dict(os.environ, PATH=str(binaries)+os.pathsep+os.environ['PATH'], TEST_LEDGER=str(ledger),
            TEST_FIXTURES=str(fixture_path), GITHUB_REPOSITORY='repoagent/controller', GITHUB_REPOSITORY_ID='1426154477',
            GITHUB_REPOSITORY_OWNER_ID='345029828', GITHUB_REF='refs/heads/main', GITHUB_EVENT_NAME='workflow_dispatch',
            GITHUB_SHA=SHA, AUTH_EXPECTED_SOURCE_SHA=SHA, AZURE_SUBSCRIPTION_ID=SUB,
            AZURE_TENANT_ID=TENANT, AZURE_RESOURCE_GROUP='rg-repoagent-controller-auth', AZURE_LOCATION='swedencentral')
        env.update(environment)
        output = folder / 'evidence'
        result = subprocess.run([sys.executable, str(SCRIPT), str(output)], env=env, cwd=ROOT,
                                capture_output=True, text=True, timeout=30)
        calls = [json.loads(row) for row in ledger.read_text().splitlines()] if ledger.exists() else []
        report = json.loads((output/'diagnostics.json').read_text()) if (output/'diagnostics.json').exists() else None
        return result, report, calls, output

    def test_collects_fixed_read_only_evidence_without_private_provider_fields(self):
        result, report, calls, output = self.invoke()
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(report['schema'], 'repoagent.broker-diagnostics/v1')
        self.assertEqual(report['cleanup']['observations']['images'], ['ghcr.io/repoagent/controller-auth@sha256:'+'b'*64])
        self.assertEqual(report['executions']['observations']['items'][0]['status'], 'Failed')
        self.assertTrue(report['executions']['observations']['truncated'])
        self.assertEqual(report['alerts']['observations'][0]['condition'], 'Resolved')
        self.assertEqual(report['metrics']['observations'][0]['maximum'], 1)
        self.assertFalse(report['settlementProven'])
        self.assertEqual(sorted(path.name for path in output.iterdir()), ['diagnostics.json'])
        self.assertNotIn(SECRET, json.dumps(report) + result.stdout + result.stderr)
        for call in calls:
            if call[0] == 'az':
                self.assertIn(call[1:3], [['account','show'], ['rest','--method']])
                if call[1] == 'rest':
                    self.assertEqual(call[3], 'get')

    def test_replica_snapshot_uses_the_documented_cli_preview_protocol(self):
        result, report, calls, _ = self.invoke()
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(report['executions']['observations']['replicas'][0]['observations']['containers'],
                         [dict(state='NotRunning', ready=False, restarts=1)])
        request = next(call for call in calls if '/replicas?' in ' '.join(call))
        self.assertIn('api-version=2023-11-02-preview', ' '.join(request))

    def test_an_arbitrary_registry_tag_is_not_published_as_diagnostic_text(self):
        def malicious(fixtures, job):
            fixtures[job]['properties']['template']['containers'][0]['image'] = 'ghcr.io/repoagent/controller-auth:' + SECRET
        result, report, _, _ = self.invoke(malicious)
        self.assertNotIn(SECRET, json.dumps(report) + result.stdout + result.stderr)
        self.assertEqual(report['cleanup']['observations']['images'], ['unrecognized'])

    def test_denied_replica_read_is_partial_evidence_not_a_complete_observation(self):
        def denied(fixtures, job):
            fixtures[job + '/executions/repoagent-auth-cleanup-123/replicas'] = 'FAIL'
        result, report, _, _ = self.invoke(denied)
        self.assertEqual(result.returncode, 1)
        self.assertEqual(report['state'], 'partial')
        self.assertEqual(report['executions']['observations']['replicas'][0]['state'], 'unavailable')
        self.assertNotIn(SECRET, json.dumps(report) + result.stdout + result.stderr)

    def test_wrong_context_or_target_is_refused_before_provider_calls(self):
        for field, value in (('GITHUB_SHA','b'*40), ('GITHUB_REF','refs/heads/other'),
                             ('AZURE_SUBSCRIPTION_ID','other'), ('AUTH_APP_NAME','other')):
            with self.subTest(field=field):
                result, report, calls, output = self.invoke(**{field:value})
                self.assertNotEqual(result.returncode, 0)
                self.assertIsNone(report)
                self.assertEqual(calls, [])
                self.assertFalse(output.exists())

    def test_output_and_replica_reads_remain_bounded_when_provider_returns_many_executions(self):
        def many(fixtures, job):
            first = fixtures[job+'/executions']['value'][0]
            fixtures[job+'/executions'] = dict(value=[dict(first, name='repoagent-auth-cleanup-'+str(i)) for i in range(100)])
            for i in range(3):
                fixtures[job+'/executions/repoagent-auth-cleanup-'+str(i)+'/replicas'] = dict(value=[])
        result, report, calls, _ = self.invoke(many)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(len(report['executions']['observations']['items']), 20)
        self.assertTrue(report['executions']['observations']['truncated'])
        self.assertEqual(sum('/replicas?' in ' '.join(call) for call in calls), 3)

    def test_oversized_or_denied_metrics_are_explicitly_unavailable_without_raw_errors(self):
        for failure in ('FAIL','OVERSIZE'):
            with self.subTest(failure=failure):
                def fail(fixtures, job):
                    fixtures['/v1/workspaces/11111111-2222-3333-4444-555555555555/query'] = failure
                result, report, _, _ = self.invoke(fail)
                self.assertEqual(result.returncode, 1)
                self.assertEqual(report['metrics']['state'], 'unavailable')
                self.assertNotIn('observations', report['metrics'])
                self.assertNotIn(SECRET, json.dumps(report) + result.stdout + result.stderr)

    def test_unrecognized_metric_dimensions_are_not_exported(self):
        def private(fixtures, job):
            fixtures['/v1/workspaces/11111111-2222-3333-4444-555555555555/query']['tables'][0]['rows'][0][1] = SECRET
        result, report, _, _ = self.invoke(private)
        self.assertEqual(result.returncode, 1)
        self.assertEqual(report['metrics']['state'], 'unavailable')
        self.assertNotIn(SECRET, json.dumps(report) + result.stdout + result.stderr)

    def test_workspace_response_cannot_redirect_the_query_to_another_target(self):
        def wrong(fixtures, job):
            fixtures[GROUP + '/providers/Microsoft.OperationalInsights/workspaces/repoagent-auth-logs']['id'] = '/other/workspace'
        result, report, calls, _ = self.invoke(wrong)
        self.assertEqual(result.returncode, 1)
        self.assertEqual(report['metrics']['state'], 'unavailable')
        self.assertFalse(any('api.loganalytics.azure.com' in ' '.join(call) for call in calls))

