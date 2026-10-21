"""Real managed-checkout startup freshness; empty polls create no native turns."""
import hashlib
import json
import os
from pathlib import Path
import runpy
import shutil
import subprocess
import tarfile
import tempfile
import urllib.request
import unittest

from test_managed_git import CONTROLLER, ROOT, GitProvider


@unittest.skipIf(os.name == 'nt', 'Managed checkout uses the Linux shipping tools')
class CheckoutRefreshTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        owned = tempfile.TemporaryDirectory(prefix='checkout-refresh-native-')
        directory = Path(owned.name)
        def cleanup():
            owned.cleanup()
            assert not directory.exists(), 'Owned native prerequisite was not removed'
            print('REFRESH_NATIVE cleanup=removed path=' + str(directory), flush=True)
        cls.addClassCleanup(cleanup)
        native_bin, home = directory / 'bin', directory / 'home'
        native_bin.mkdir()
        home.mkdir(mode=0o700)
        version, url, checksum = runpy.run_path(str(ROOT / 'scripts/compose-image.py'))['ARTIFACTS']['codex']
        if json.loads((ROOT / 'image/default.json').read_text())['agent']['version'] != version:
            raise RuntimeError('Default Codex version differs from the reviewed artifact catalog')
        archive = directory / 'codex.tar.gz'
        with urllib.request.urlopen(url, timeout=60) as response, archive.open('wb') as output:
            shutil.copyfileobj(response, output)
        if hashlib.sha256(archive.read_bytes()).hexdigest() != checksum:
            raise RuntimeError('Native test prerequisite checksum mismatch')
        cls.native = native_bin / 'codex'
        with tarfile.open(archive, 'r:gz') as package:
            member = package.getmember('codex-x86_64-unknown-linux-musl')
            if not member.isfile():
                raise RuntimeError('Native test prerequisite must be a regular executable')
            with package.extractfile(member) as source, cls.native.open('wb') as output:
                shutil.copyfileobj(source, output)
        cls.native.chmod(0o755)
        identity = subprocess.run([str(cls.native), '--version'], capture_output=True, text=True,
                                  check=True, timeout=15, env=dict(PATH=os.environ['PATH'], HOME=str(home), CODEX_HOME=str(home)))
        if identity.stdout.strip() != 'codex-cli ' + version:
            raise RuntimeError('Native test prerequisite version mismatch')
        print('REFRESH_NATIVE version=' + version + ' archive-sha256=' + checksum
              + ' executable-sha256=' + hashlib.sha256(cls.native.read_bytes()).hexdigest()
              + ' path=' + str(cls.native) + ' owned-mode=' + oct(directory.stat().st_mode & 0o777)
              + ' home-mode=' + oct(home.stat().st_mode & 0o777), flush=True)
        build = subprocess.run(['dotnet', 'build', str(ROOT / 'src/RepoAgentController/RepoAgentController.csproj'),
                                '-c', 'Release', '--nologo', '-v', 'quiet'], cwd=ROOT,
                               capture_output=True, text=True, timeout=120)
        if build.returncode:
            raise RuntimeError(build.stdout + build.stderr)
        print('REFRESH_BUILD ' + build.stdout.strip(), flush=True)

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
            environment.update(HOME=str(home), CODEX_HOME=str(home), CODEX_CA_CERTIFICATE=str(provider.cert),
                               PATH=str(self.native.parent) + os.pathsep + os.environ['PATH'])
            self.assertEqual(shutil.which('codex', path=environment['PATH']), str(self.native))
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
                  + ' native=' + str(Path(shutil.which('codex', path=environment['PATH'])).resolve())
                  + ' transport=GitProvider-normal-TLS empty-poll=true helper-fetch-control=passed', flush=True)
            self.assertEqual(actual, after, 'Restart must reconcile the clean owned checkout before its empty poll')
            self.assertEqual((checkout / 'README').read_text(), 'current default branch B\n')
            self.assertEqual(json.loads(owner.read_text())['commit'], after)
            self.assertEqual(json.loads(state.read_text())['discussions'], {})


if __name__ == '__main__':
    unittest.main()
