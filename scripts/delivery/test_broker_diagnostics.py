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
elif Path(sys.argv[0]).name == 'gh' and args[0]=='api':
 fixtures=json.loads(Path(os.environ['TEST_FIXTURES']).read_text())
 value=fixtures[args[-1]]
 if value=='FAIL': sys.exit('DO_NOT_EXPORT_SECRET provider error')
 print(json.dumps(value))
elif args[:2] == ['account','show']:
 print(json.dumps(dict(id='46f7e5d6-05f3-435b-8089-6c63a203dbfa',tenantId='3c356090-bfe9-459b-a2ac-064231707881')))
elif args[:2] == ['containerapp','exec']:
 import tty
 tty.setcbreak(sys.stdin.fileno())
 assert os.isatty(sys.stdin.fileno())
 fixtures=json.loads(Path(os.environ['TEST_FIXTURES']).read_text())
 mode=fixtures.get('consoleMode','success')
 if mode=='denied': sys.exit('DO_NOT_EXPORT_SECRET provider error')
 if mode=='overflow': print('DO_NOT_EXPORT_SECRET'*10000);sys.exit(0)
 if mode=='hang':
  import time
  time.sleep(60)
 if mode=='drift':
  for key,value in fixtures.items():
   if key.endswith('/containerApps/repoagent-auth'):
    value['properties']['template']['containers'][0]['image']='ghcr.io/repoagent/controller-auth@sha256:'+'c'*64
  Path(os.environ['TEST_FIXTURES']).write_text(json.dumps(fixtures))
 print('INFO: DO_NOT_EXPORT_SECRET provider banner')
 if args[args.index('--command')+1]=='dotnet /app/RepoAgent.Auth.dll --inspect-retained-authority':
  print(fixtures.get('authorityRaw',json.dumps(fixtures['authorityPayload'])))
 else: print(json.dumps(dict(component='broker',version='1.0.0',source='b'*40)))
 if mode=='ambiguous': print(json.dumps(dict(component='broker',version='1.0.1',source='c'*40)))
 if mode=='private-json': print(json.dumps(dict(component='broker',version='1.0.0',source='b'*40,private='DO_NOT_EXPORT_SECRET')))
 if mode=='trailing-hang':
  import time
  sys.stdout.flush()
  time.sleep(60)
 sys.exit(fixtures.get('consoleExitCode',0))
elif args[:3] == ['rest','--method','get']:
 from urllib.parse import urlsplit
 url=args[args.index('--url')+1]; key=urlsplit(url).path
 fixtures=json.loads(Path(os.environ['TEST_FIXTURES']).read_text())
 if key not in fixtures: sys.exit(92)
 value=fixtures.get('historicalRequests', fixtures[key]) if 'AppRequests' in url else fixtures[key]
 if value == 'FAIL': sys.exit('DO_NOT_EXPORT_SECRET provider error')
 if value == 'OVERSIZE': sys.stdout.write('x'*2100000)
 else:
  if '--query' in args:
   import jmespath
   value=jmespath.search(args[args.index('--query')+1], value)
  print(json.dumps(value))
else: sys.exit(93)
'''


@unittest.skipIf(os.name == 'nt', 'Command transports use Linux executable scripts')
class BrokerDiagnosticsTests(unittest.TestCase):
    def invoke(self, change=None, script=SCRIPT, report_name='diagnostics.json', **environment):
        folder = Path(self.enterContext(tempfile.TemporaryDirectory(prefix='broker-diagnostics-')))
        binaries = folder / 'bin'
        binaries.mkdir()
        for name in ('az', 'git', 'gh'):
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
        fixtures['historicalRequests'] = dict(tables=[dict(columns=[dict(name=name) for name in
            ('Interval','Endpoint','Status','StoredRows','RepresentedRequests','MissingWeights')],
            rows=[['2026-10-27T21:35:00Z','renewal','429',2,200,0]])])
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
        result = subprocess.run([sys.executable, str(script), str(output)], env=env, cwd=ROOT,
                                capture_output=True, text=True, timeout=45)
        calls = [json.loads(row) for row in ledger.read_text().splitlines()] if ledger.exists() else []
        report = json.loads((output/report_name).read_text()) if (output/report_name).exists() else None
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

    def test_historical_request_counts_are_sampled_fixed_window_aggregates_not_caller_evidence(self):
        from urllib.parse import parse_qs, urlsplit
        result, report, calls, _ = self.invoke()
        self.assertEqual(result.returncode, 0, result.stderr)
        history = report['historicalRequests']['observations']
        self.assertEqual(history['intervals'], [dict(interval='2026-10-27T21:35:00Z', endpoint='renewal',
            status='429', storedRows=2, representedRequests=200, missingWeights=0)])
        self.assertFalse(history['completeTrafficCoverage'])
        request = next(call for call in calls if 'AppRequests' in ' '.join(call))
        query = parse_qs(urlsplit(request[request.index('--url')+1]).query)['query'][0]
        for bound in ('2026-10-27T21:30:00Z','2026-10-27T22:15:00Z',
                      '2026-10-28T05:30:00Z','2026-10-28T06:30:00Z'):
            self.assertIn(bound, query)
        self.assertIn('sum(ItemCount)', query)
        self.assertNotIn('Properties', query)

    def test_historical_unknown_categories_never_echo_provider_values_and_empty_is_not_zero(self):
        def private(fixtures, job):
            fixtures['historicalRequests']['tables'][0]['rows'][0][1:3] = [SECRET, SECRET]
        result, report, _, _ = self.invoke(private)
        self.assertEqual(result.returncode, 0, result.stderr)
        row = report['historicalRequests']['observations']['intervals'][0]
        self.assertEqual((row['endpoint'], row['status']), ('other', 'other'))
        self.assertNotIn(SECRET, json.dumps(report) + result.stdout + result.stderr)
        def empty(fixtures, job):
            fixtures['historicalRequests']['tables'][0]['rows'] = []
        result, report, _, _ = self.invoke(empty)
        self.assertEqual(result.returncode, 0, result.stderr)
        evidence = report['historicalRequests']['observations']
        self.assertEqual(evidence['observedMinutes'], 0)
        self.assertFalse(evidence['completeTrafficCoverage'])
        self.assertIn('Empty results do not prove zero traffic', evidence['coverage'])

    def test_untrusted_historical_results_remain_partial_without_private_output(self):
        for mode in ('denied', 'oversize', 'partial', 'cap', 'extra-column', 'time', 'fraction', 'null', 'weights'):
            with self.subTest(mode=mode):
                def invalid(fixtures, job):
                    response = fixtures['historicalRequests']
                    table = response['tables'][0]
                    if mode == 'denied': fixtures['historicalRequests'] = 'FAIL'
                    elif mode == 'oversize': fixtures['historicalRequests'] = 'OVERSIZE'
                    elif mode == 'partial': response['error'] = dict(message=SECRET)
                    elif mode == 'cap': table['rows'] *= 8192
                    elif mode == 'extra-column': table['columns'].append(dict(name=SECRET))
                    elif mode == 'time': table['rows'][0][0] = '2026-10-28T03:00:00Z'
                    elif mode == 'fraction': table['rows'][0][3] = 1.5
                    elif mode == 'null': table['rows'][0][4] = None
                    elif mode == 'weights': table['rows'][0][4] = 1
                result, report, _, _ = self.invoke(invalid)
                self.assertEqual(result.returncode, 1)
                self.assertEqual(report['historicalRequests']['state'], 'unavailable')
                self.assertNotIn(SECRET, json.dumps(report) + result.stdout + result.stderr)

    def test_replica_snapshot_uses_the_documented_cli_preview_protocol(self):
        result, report, calls, _ = self.invoke()
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(report['executions']['observations']['replicas'][0]['observations']['containers'],
                         [dict(state='NotRunning', ready=False, restarts=1)])
        request = next(call for call in calls if '/replicas?' in ' '.join(call))
        self.assertIn('api-version=2023-11-02-preview', ' '.join(request))

    def test_alert_rule_arm_identity_is_accepted_without_a_name_only_server_filter(self):
        from urllib.parse import parse_qs, urlsplit
        rule = GROUP + '/providers/Microsoft.Insights/metricAlerts/repoagent-auth-cleanup-execution-failed'
        for identity in (rule, rule.upper()):
            with self.subTest(identity=identity):
                def platform(fixtures, job):
                    fixtures[GROUP + '/providers/Microsoft.AlertsManagement/alerts']['value'][0]['properties']['essentials']['alertRule'] = identity
                result, report, calls, _ = self.invoke(platform)
                self.assertEqual(result.returncode, 0, report['alerts'])
                self.assertEqual(report['alerts']['observations'][0]['condition'], 'Resolved')
                request = next(call for call in calls if '/Microsoft.AlertsManagement/alerts?' in ' '.join(call))
                parameters = parse_qs(urlsplit(request[request.index('--url')+1]).query)
                self.assertNotIn('alertRule', parameters)
                self.assertEqual(parameters['targetResource'], [GROUP + '/providers/Microsoft.App/jobs/repoagent-auth-cleanup'])
                self.assertEqual(parameters['timeRange'], ['1d'])
                self.assertEqual(parameters['pageCount'], ['10'])

    def test_other_alert_rules_are_unavailable_without_exporting_the_rule(self):
        rule_id = GROUP + '/providers/Microsoft.Insights/metricAlerts/repoagent-auth-cleanup-execution-failed'
        for identity in (SECRET, rule_id.replace('rg-repoagent-controller-auth', 'other-group'), rule_id + '-other'):
            with self.subTest(identity=identity):
                def unrelated(fixtures, job):
                    fixtures[GROUP + '/providers/Microsoft.AlertsManagement/alerts']['value'][0]['properties']['essentials']['alertRule'] = identity
                result, report, _, _ = self.invoke(unrelated)
                self.assertEqual(result.returncode, 1)
                self.assertEqual(report['state'], 'partial')
                self.assertEqual(report['alerts'], dict(state='unavailable', reason='target-ownership-mismatch'))
                self.assertNotIn(identity, json.dumps(report) + result.stdout + result.stderr)


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
        self.assertFalse(report['executions']['observations']['pageTruncated'])
        self.assertEqual(sum('/replicas?' in ' '.join(call) for call in calls), 3)

    def test_verbose_execution_snapshots_are_projected_before_the_bounded_output_pipe(self):
        def verbose(fixtures, job):
            first = fixtures[job+'/executions']['value'][0]
            first['properties']['template'] = dict(containers=[dict(
                image='ghcr.io/repoagent/controller-auth@sha256:'+'b'*64,
                env=[dict(name='PRIVATE', value=SECRET*200000)], args=[SECRET])])
        result, report, calls, _ = self.invoke(verbose)
        self.assertEqual(result.returncode, 0, report['executions'])
        execution = report['executions']['observations']['items'][0]
        self.assertEqual(execution['name'], 'repoagent-auth-cleanup-123')
        self.assertEqual(execution['status'], 'Failed')
        self.assertEqual(execution['images'], ['ghcr.io/repoagent/controller-auth@sha256:'+'b'*64])
        self.assertNotIn(SECRET, json.dumps(report) + result.stdout + result.stderr)

    def test_large_provider_page_yields_only_a_validated_bounded_prefix_and_honest_truncation(self):
        def page(fixtures, job):
            first = fixtures[job+'/executions']['value'][0]
            fixtures[job+'/executions'] = dict(value=[dict(first, name='repoagent-auth-cleanup-'+str(i)) for i in range(150)])
            # This outside-prefix row is intentionally neither validated nor exported.
            fixtures[job+'/executions']['value'][-1]['name'] = SECRET
            for i in range(3):
                fixtures[job+'/executions/repoagent-auth-cleanup-'+str(i)+'/replicas'] = dict(value=[])
        result, report, calls, _ = self.invoke(page)
        self.assertEqual(result.returncode, 0, report['executions'])
        evidence = report['executions']['observations']
        self.assertEqual(evidence['pageRows'], 100)
        self.assertTrue(evidence['pageTruncated'])
        self.assertEqual(len(evidence['items']), 20)
        self.assertTrue(evidence['truncated'])
        self.assertIn('First 100 rows', evidence['coverage'])
        self.assertEqual(sum('/replicas?' in ' '.join(call) for call in calls), 3)
        self.assertNotIn(SECRET, json.dumps(report)+result.stdout+result.stderr)

    def test_malformed_selected_prefix_rows_still_make_history_unavailable(self):
        def malformed(fixtures, job):
            fixtures[job+'/executions']['value'][0]['name'] = SECRET
        result, report, calls, _ = self.invoke(malformed)
        self.assertEqual(result.returncode, 1)
        self.assertEqual(report['executions']['state'], 'unavailable')
        self.assertFalse(any('/replicas?' in ' '.join(call) for call in calls))
        self.assertNotIn(SECRET, json.dumps(report)+result.stdout+result.stderr)

    def test_recent_failed_execution_is_retained_when_newer_successes_fill_the_page(self):
        def history(fixtures, job):
            image = 'ghcr.io/repoagent/controller-auth@sha256:'+'c'*64
            rows = [dict(name='repoagent-auth-cleanup-'+str(i), properties=dict(
                status='Succeeded' if i < 25 else 'Failed',
                startTime='2026-10-28T20:'+str(59-i).zfill(2)+':00Z',
                template=dict(containers=[dict(image=image)]))) for i in range(30)]
            fixtures[job+'/executions'] = dict(value=rows)
            for row in rows:
                fixtures[job+'/executions/'+row['name']+'/replicas'] = dict(value=[])
        result, report, calls, _ = self.invoke(history)
        self.assertEqual(result.returncode, 0, result.stderr)
        evidence = report['executions']['observations']
        self.assertEqual(len(evidence['items']), 20)
        self.assertEqual([row['name'] for row in evidence['items'] if row['status'] == 'Failed'],
                         ['repoagent-auth-cleanup-25', 'repoagent-auth-cleanup-26'])
        self.assertEqual(evidence['items'][0]['name'], 'repoagent-auth-cleanup-0')
        self.assertEqual(evidence['items'][-1]['images'], ['ghcr.io/repoagent/controller-auth@sha256:'+'c'*64])
        self.assertEqual({row['execution'] for row in evidence['replicas']},
                         {'repoagent-auth-cleanup-0', 'repoagent-auth-cleanup-1', 'repoagent-auth-cleanup-25'})
        self.assertEqual(sum('/replicas?' in ' '.join(call) for call in calls), 3)
        self.assertTrue(evidence['truncated'])

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

