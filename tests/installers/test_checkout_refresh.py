"""Real managed-checkout startup freshness; empty polls create no native turns."""
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

from test_managed_git import CONTROLLER, ROOT, GitProvider


@unittest.skipIf(os.name == 'nt', 'Managed checkout uses the Linux shipping tools')
class CheckoutRefreshTests(unittest.TestCase):
    def test_restart_reconciles_the_owned_checkout_to_the_current_default_branch(self):
        with tempfile.TemporaryDirectory(prefix='checkout-refresh-') as temporary:
            directory = Path(temporary)
            provider = GitProvider(directory)
            self.addCleanup(provider.close)
            home, workspace = directory / 'home', directory / 'workspace'
            home.mkdir(mode=0o700)
            workspace.mkdir()
            config = home / 'config.toml'
            config.write_text('model = "gpt-6.1-sol"\nweb_search = "disabled"\napproval_policy = "never"\nsandbox_mode = "danger-full-access"\ncli_auth_credentials_store = "file"\n')
            config.chmod(0o600)
            enrollment = directory / 'enrollment.json'
            enrollment.write_text(json.dumps(provider.enrollment()))
            enrollment.chmod(0o600)
            environment = provider.environment()
            environment.update(HOME=str(home), CODEX_HOME=str(home), CODEX_CA_CERTIFICATE=str(provider.cert))
            # All outbound HTTP goes to the existing local denying CONNECT fixture.
            for name in ('HTTP_PROXY', 'http_proxy', 'https_proxy'):
                environment[name] = environment['HTTPS_PROXY']
            environment.update(ALL_PROXY='', all_proxy='', no_proxy='')

            def run(command):
                result = subprocess.run(command, env=environment, capture_output=True, text=True, timeout=60)
                diagnostic = (result.stdout + result.stderr).replace(provider.token, '[fixture lease]')
                self.assertEqual(result.returncode, 0, diagnostic)
                return result.stdout.strip()

            def git(*args):
                return run(['git', *map(str, args)])

            state = directory / 'state.json'
            checkout = workspace / 'repository'
            owner = workspace / '.repoagent-checkout.json'
            command = ['dotnet', str(CONTROLLER), '--repo', 'owner/project', '--app-id', '123',
                       '--broker-url', 'https://broker.fixture', '--enrollment-file', str(enrollment),
                       '--state', str(state), '--workspace', str(workspace), '--clone', '--once']
            before = git('--git-dir', directory / 'repositories/owner/project.git', 'rev-parse', 'HEAD')
            self.assertIn('poll-completed success=True', run(command))
            self.assertEqual(git('-C', checkout, 'rev-parse', 'HEAD'), before)
            self.assertEqual((checkout / 'README').read_text(), 'approved repository\n')
            self.assertEqual(json.loads(owner.read_text())['commit'], before)
            self.assertEqual(json.loads(state.read_text())['discussions'], {})
            seed = directory / 'seed'
            (seed / 'README').write_text('current default branch B\n')
            git('-C', seed, 'add', 'README')
            git('-C', seed, 'commit', '-m', 'advance default branch to B')
            after = git('-C', seed, 'rev-parse', 'HEAD')
            git('-C', seed, 'push', directory / 'repositories/owner/project.git', 'main')
            self.assertNotEqual(before, after)
            self.assertEqual(git('--git-dir', directory / 'repositories/owner/project.git', 'rev-parse', 'HEAD'), after)
            # Prove existing authenticated helper/HTTPS Git transport can fetch B,
            # without fetching into or updating the subject's owned checkout.
            helpers = Path(str(state) + '.github-app') / 'bin'
            control = directory / 'fetch-control'
            run([str(helpers / 'gh'), 'repo', 'clone', 'https://github.com/owner/project.git', str(control)])
            run([str(helpers / 'repoagent-git'), 'fetch', '--directory', str(control)])
            self.assertEqual(git('-C', control, 'rev-parse', 'FETCH_HEAD'), after)
            self.assertEqual(git('-C', checkout, 'rev-parse', 'HEAD'), before)
            output = run(command)
            self.assertIn('poll-completed success=True', output)
            actual = git('-C', checkout, 'rev-parse', 'HEAD')
            print('REFRESH baseline=' + before + ' target=' + after + ' actual=' + actual
                  + ' native=' + str(Path(shutil.which('codex')).resolve())
                  + ' transport=GitProvider-normal-TLS empty-poll=true helper-fetch-control=passed', flush=True)
            self.assertEqual(actual, after, 'Restart must reconcile the clean owned checkout before its empty poll')
            self.assertEqual((checkout / 'README').read_text(), 'current default branch B\n')
            self.assertEqual(json.loads(owner.read_text())['commit'], after)
            self.assertEqual(json.loads(state.read_text())['discussions'], {})


if __name__ == '__main__':
    unittest.main()
