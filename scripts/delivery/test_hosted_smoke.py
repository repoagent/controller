"""The shipped hosted orchestration's cleanup decision at its command boundary.

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
elif name == 'gh':
 if 'graphql' in args:
  request=json.load(sys.stdin)
  print(json.dumps(dict(data=dict(repository=dict(discussions=dict(nodes=[],pageInfo=dict(hasNextPage=False,endCursor=None)))))))
 else: print(json.dumps(dict(variables=[],total_count=0)))
'''


@unittest.skipIf(os.name == 'nt', 'External command doubles use Linux executable scripts')
class HostedCleanupTests(unittest.TestCase):
    def cleanup(self, closure):
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
        result = subprocess.run([sys.executable, str(SCRIPT), 'cleanup', str(directory)],
                                env=environment, capture_output=True, text=True, timeout=20)
        report = json.loads((directory / 'evidence/cleanup.json').read_text())
        self.assertNotIn('ghs_DO_NOT_PUBLISH', result.stdout + result.stderr + json.dumps(report))
        return result, report, state

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
