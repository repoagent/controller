"""The shipped hosted orchestration's installation and cleanup at its command boundary.

Only external commands are replaced. No installer or provider adapter is exercised by these tests;
the real hosted journey owns their compatibility evidence.
"""
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
SCRIPT = ROOT / 'scripts/delivery/hosted-smoke.py'
DOUBLE = '''import json,os,sys
name = os.path.basename(sys.argv[0])
args = sys.argv[1:]
if name == 'pwsh':
 print(json.dumps(dict(repository='repoagent/ControllerTestRepo',approvalVariable='SMOKE_CONTROLLER_APPROVAL',
  githubMarker='removed',brokerClosure=os.environ['BROKER_CLOSURE'],outstandingTokens='unsettled')))
elif name == 'docker':
 if args[:1] == ['inspect'] or args[:2] == ['volume','inspect']: sys.exit(1)
 if args[:1] == ['logs'] and os.environ.get('DASHBOARD_CREATION'):
  print('2026-10-28T00:00:00Z dashboard-created https://github.com/repoagent/ControllerTestRepo/discussions/42')
elif name == 'gh':
 if 'graphql' in args:
  request=json.load(sys.stdin)
  if request['query'].startswith('mutation'):
   with open(os.environ['DELETIONS'],'a') as output: output.write(request['variables']['id']+'\\n')
   print(json.dumps(dict(data={}))); sys.exit(0)
  if os.environ.get('DASHBOARD_FIXTURE') and request.get('variables',{}).get('category'):
   rows=[]
   for identifier,login in [('baseline','repo-agent-connector'),('new','repo-agent-connector'),('human','repoagent'),('other-app','unrelated-app')]:
    rows.append(dict(id=identifier,number=42,url='https://github.com/repoagent/ControllerTestRepo/discussions/42',body='<!-- repoagent:controller-dashboard -->',author=dict(login=login),category=dict(id='category-1')))
   print(json.dumps({'data':{'repository':{'discussions':{'nodes':rows,'pageInfo':dict(hasNextPage=False)}}}})); sys.exit(0)
  print(json.dumps(dict(data=dict(repository=dict(discussions=dict(nodes=[],pageInfo=dict(hasNextPage=False,endCursor=None)))))))
 else: print(json.dumps(dict(variables=[],total_count=0)))
'''


@unittest.skipIf(os.name == 'nt', 'External command doubles use Linux executable scripts')
class HostedCleanupTests(unittest.TestCase):
    def install(self, failure=False, unready=False, timed_out=False):
        directory = Path(self.enterContext(tempfile.TemporaryDirectory(prefix='hosted-install-')))
        directory.chmod(0o700)
        binaries = directory / 'bin'
        binaries.mkdir()
        double = '''import json,os,sys
from pathlib import Path
args=sys.argv[1:]
image='repoagent-controller:hosted-smoke'
inventory=json.loads(Path(os.environ['SMOKE_INVENTORY']).read_text())
name=Path(sys.argv[0]).name
if name == 'curl':
 if os.environ.get('SMOKE_READINESS_FAIL'): sys.exit(22)
 print('200')
elif name == 'pwsh':
 if '-Image' in args or os.environ.get('REPOAGENT_IMAGE') != image:
  sys.exit('Fresh installation must select the candidate through REPOAGENT_IMAGE')
 if '-DashboardCategory' not in args or args[args.index('-DashboardCategory')+1] != 'Smoke status':
  sys.exit('Fresh hosted installation must enable the discovered dashboard category')
 if os.environ.get('SMOKE_INSTALL_FAIL'):
  print('Unable to complete validate the selected plan. Safe fixture failure. '+os.environ['GH_TOKEN']+' '+os.environ['OPENAI_API_KEY'])
  print('private token: '+('x'*48))
  sys.exit(9)
 state=Path(args[args.index('-StateDirectory')+1]); state.mkdir()
 (state/'approval.json').write_text(json.dumps(dict(setupVersion=4,profile='webhook-clone')))
 (state/'enrollment.json').write_text(json.dumps(dict(appSlug='repo-agent-connector')))
elif name == 'gh':
 if args == ['--version']: print('gh version '+inventory['githubCli']['version']+' (test)')
 elif args[-1] == 'user': print(json.dumps(dict(login='repoagent')))
 elif args[-1] == '-':
  request=json.load(sys.stdin)
  if 'discussionCategories' in request['query']:
   print(json.dumps({'data':{'repository':{'discussionCategories':{'nodes':[dict(id='category-1',name='Smoke status',isAnswerable=False)],'pageInfo':dict(hasNextPage=False)}}}}))
  else:
   print(json.dumps({'data':{'repository':{'discussions':{'nodes':[],'pageInfo':dict(hasNextPage=False)}}}}))
 else: print(json.dumps(dict(full_name='repoagent/ControllerTestRepo',has_discussions=True)))
elif args[0] == 'inspect':
 print(json.dumps([dict(Config=dict(User='1000',Env=[]),HostConfig=dict(Privileged=False),Image='sha256:candidate')]))
elif args[:2] == ['image','inspect']:
 print(json.dumps([dict(Id='sha256:candidate',Os='linux',Architecture='amd64',Config=dict(User='1000',Labels={'io.repoagent.role':'controller'}))]))
elif args[-1] == 'versions':
 print('gh version '+inventory['githubCli']['version']+' (test)')
 print('codex-cli '+inventory['agent']['version'])
 print('cloudflared version '+inventory['tools']['cloudflared']+' (test)')
else: print(json.dumps(inventory))
'''
        for name in ('docker', 'pwsh', 'gh', 'curl'):
            (binaries / name).write_text(f'#!{sys.executable}\n' + double)
            (binaries / name).chmod(0o700)
        (directory / 'run.json').write_text(json.dumps(dict(schema=1, repository='repoagent/ControllerTestRepo',
            approval='SMOKE_CONTROLLER_APPROVAL', instance='smoke-123-1', image='repoagent-controller:hosted-smoke')))
        environment = dict(os.environ, PATH=str(binaries)+os.pathsep+os.environ['PATH'], REPOAGENT_LIVE='1',
                           GH_TOKEN='ghs_DO_NOT_PUBLISH', OPENAI_API_KEY='sk_DO_NOT_PUBLISH',
                           REPOAGENT_IMAGE='wrong-inherited-image', SMOKE_INVENTORY=str(ROOT / 'image/default.json'))
        if failure:
            environment['SMOKE_INSTALL_FAIL'] = '1'
        if unready:
            environment['SMOKE_READINESS_FAIL'] = '1'
        entry = [sys.executable, str(SCRIPT)]
        if timed_out:
            launcher = directory / 'timeout.py'
            launcher.write_text('''import runpy,subprocess,sys,os
original=subprocess.run
def command(args,**kwargs):
 if args[0]=='pwsh':
  raise subprocess.TimeoutExpired(args,kwargs['timeout'],output=('Safe timeout phase. '+os.environ['GH_TOKEN']).encode(),stderr=os.environ['OPENAI_API_KEY'].encode())
 return original(args,**kwargs)
subprocess.run=command
script=sys.argv.pop(1)
runpy.run_path(script,run_name='__main__')
''')
            entry = [sys.executable, str(launcher), str(SCRIPT)]
        result = subprocess.run([*entry, 'install', str(directory)],
                                env=environment, capture_output=True, text=True, timeout=20)
        return result, directory

    def test_fresh_install_selects_candidate_through_supported_environment(self):
        result, directory = self.install()
        self.assertEqual(result.returncode, 0, result.stderr)
        report = json.loads((directory / 'evidence/installed.json').read_text())
        self.assertEqual(report['state'], 'ready')
        self.assertNotIn('DO_NOT_PUBLISH', result.stdout + result.stderr + json.dumps(report))

    def test_failed_install_retains_bounded_redacted_diagnostic_evidence(self):
        result, directory = self.install(failure=True)
        self.assertNotEqual(result.returncode, 0)
        report = json.loads((directory / 'evidence/install-failure.json').read_text())
        self.assertEqual(report['exitCode'], 9)
        self.assertIn('Safe fixture failure.', report['diagnostic'])
        self.assertNotIn('DO_NOT_PUBLISH', result.stdout + result.stderr + json.dumps(report))
        self.assertNotIn('x'*48, json.dumps(report))
        self.assertLessEqual(len(report['diagnostic']), 4000)

    def test_unready_broker_stops_before_enrollment_or_auth_supply_is_written(self):
        result, directory = self.install(unready=True)
        self.assertNotEqual(result.returncode, 0)
        report = json.loads((directory / 'evidence/broker-readiness.json').read_text())
        self.assertEqual(report['state'], 'unavailable')
        self.assertFalse((directory / 'state/approval.json').exists())
        self.assertFalse((directory / 'codex-auth.json').exists())

    def test_install_timeout_retains_bounded_redacted_phase_evidence(self):
        result, directory = self.install(timed_out=True)
        self.assertNotEqual(result.returncode, 0)
        report = json.loads((directory / 'evidence/install-failure.json').read_text())
        self.assertEqual(report['state'], 'timed-out')
        self.assertIsNone(report['exitCode'])
        self.assertIn('Safe timeout phase.', report['diagnostic'])
        self.assertNotIn('DO_NOT_PUBLISH', result.stdout + result.stderr + json.dumps(report))

    def cleanup(self, closure, dashboard=False, creation=False):
        directory = Path(self.enterContext(tempfile.TemporaryDirectory(prefix='hosted-cleanup-')))
        directory.chmod(0o700)
        state, binaries = directory / 'state', directory / 'bin'
        state.mkdir(mode=0o700)
        binaries.mkdir()
        for name in ('docker', 'pwsh', 'gh'):
            (binaries / name).write_text(f'#!{sys.executable}\n' + DOUBLE)
            (binaries / name).chmod(0o700)
        (directory / 'run.json').write_text(json.dumps(dict(schema=1,repository='repoagent/ControllerTestRepo',
            approval='SMOKE_CONTROLLER_APPROVAL',instance='smoke-123-1',image='repoagent-controller:hosted-smoke')))
        (state / 'approval.json').write_text('{}')
        (state / 'enrollment.json').write_text('{}')
        environment = dict(os.environ, PATH=str(binaries)+os.pathsep+os.environ['PATH'],
                           REPOAGENT_LIVE='1', GH_TOKEN='ghs_DO_NOT_PUBLISH', BROKER_CLOSURE=closure)
        if dashboard:
            value = json.loads((directory / 'run.json').read_text())
            value['dashboard'] = dict(categoryId='category-1',category='Smoke status',before=['baseline'])
            (directory / 'run.json').write_text(json.dumps(value))
            environment.update(DASHBOARD_FIXTURE='1',DELETIONS=str(directory / 'deleted.txt'))
        if creation:
            environment['DASHBOARD_CREATION'] = '1'
        result = subprocess.run([sys.executable, str(SCRIPT), 'cleanup', str(directory)],
                                env=environment, capture_output=True, text=True, timeout=20)
        report = json.loads((directory / 'evidence/cleanup.json').read_text())
        self.assertNotIn('ghs_DO_NOT_PUBLISH', result.stdout + result.stderr + json.dumps(report))
        return result, report, state

    def test_dashboard_cleanup_never_adopts_marker_without_this_container_creation_evidence(self):
        result, report, state = self.cleanup('confirmed', dashboard=True)
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(report['state'], 'unconfirmed')
        self.assertFalse((state.parent / 'deleted.txt').exists())

    def test_dashboard_cleanup_removes_only_new_app_dashboard_created_by_this_container(self):
        result, report, state = self.cleanup('confirmed', dashboard=True, creation=True)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual((state.parent / 'deleted.txt').read_text().splitlines(), ['new'])

    def test_exit_zero_from_revoke_cannot_make_unconfirmed_broker_closure_successful(self):
        result, report, state = self.cleanup('unconfirmed')
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(report['state'], 'unconfirmed')
        self.assertEqual(report['brokerClosure'], 'unconfirmed')
        self.assertTrue((state / 'enrollment.json').exists(), 'Retain the credential needed to retry unconfirmed closure')

    def test_confirmed_closure_succeeds_without_claiming_token_settlement(self):
        result, report, state = self.cleanup('confirmed')
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(report['state'], 'complete')
        self.assertEqual(report['brokerClosure'], 'confirmed')
        self.assertEqual(report['outstandingTokens'], 'unsettled')
        self.assertFalse((state / 'enrollment.json').exists())


if __name__ == '__main__':
    unittest.main()
