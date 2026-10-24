"""Real managed-checkout startup freshness; empty polls create no native turns."""
from contextlib import closing, contextmanager
import hashlib
import hmac
import http.client
import json
import os
from pathlib import Path
import queue
import re
import runpy
import select
import signal
import shutil
import socket
import subprocess
import tarfile
import tempfile
import textwrap
import threading
import time
from types import SimpleNamespace
import urllib.request
import unittest

from test_managed_git import CONTROLLER, ROOT, CredentialManager, GitProvider

# Real native model turns against GitProvider's credential-free local Responses endpoint.
FIXTURE_MODEL = 'model = "gpt-6.1-sol"\nmodel_provider = "fixture"\n[model_providers.fixture]\nname = "Credential-free local fixture"\nbase_url = "https://broker.fixture/v1"\nwire_api = "responses"\nrequires_openai_auth = false\nsupports_websockets = false\nrequest_max_retries = 0\nstream_max_retries = 0\n'


def native_event(line):
    """Fields of the controller's redacted native-event log line; the raw native message is never logged."""
    return re.search(r' native-event method=(?P<method>\S*) thread=(?P<thread>\S*) turn=(?P<turn>\S*) status=(?P<status>\S*)'
                     r' will-retry=(?P<willRetry>\S*) error=(?P<error>.*)$', line.rstrip('\n')).groupdict()


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
        CredentialManager.build()
        controller = os.environ.get('CHECKOUT_CONTROLLER')
        if controller and not Path(controller).is_absolute():
            raise RuntimeError('CHECKOUT_CONTROLLER must be an absolute executable path')
        cls.controller_command = [str(Path(controller).resolve(strict=True))] if controller else ['dotnet', str(CONTROLLER)]

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
            command = self.controller_command + ['--repo', 'owner/project', '--app-id', '123',
                       '--broker-url', 'https://broker.fixture', '--enrollment-file', str(enrollment),
                       '--state', str(state), '--workspace', str(workspace), '--clone', '--once']
            before = git('--git-dir', directory / 'repositories/owner/project.git', 'rev-parse', 'HEAD')
            self.assertIn('poll-completed success=True', run(command))
            self.assertEqual(git('-C', checkout, 'rev-parse', 'HEAD'), before)
            self.assertEqual((checkout / 'README').read_text(), 'approved repository\n')
            self.assertEqual(json.loads(owner.read_text())['commit'], before)
            self.assertEqual(json.loads(state.read_text())['discussions'], {})
            # An owner-executable workspace keeps checkout records private to
            # the same UID; refreshing must also update that retained record.
            for path in (owner, workspace / '.repoagent-checkout.lock', checkout / '.git/repoagent-owner'):
                path.chmod(0o700)
            seed = directory / 'seed'
            (seed / 'README').write_text('current default branch B\n')
            git('-C', seed, 'add', 'README')
            git('-C', seed, 'commit', '-m', 'advance default branch to B')
            after = git('-C', seed, 'rev-parse', 'HEAD')
            git('-C', seed, 'push', directory / 'repositories/owner/project.git', 'main')
            self.assertNotEqual(before, after)
            self.assertEqual(git('--git-dir', directory / 'repositories/owner/project.git', 'rev-parse', 'HEAD'), after)
            # Prove existing authenticated helper/HTTPS Git transport can fetch B,
            # without fetching into or updating the subject's owned checkout. The
            # stopped controller kept no lease, so a live manager holds one meanwhile.
            helpers = Path(str(state) + '.github-app') / 'bin'
            control = directory / 'fetch-control'
            manager = CredentialManager(enrollment, helpers.parent, environment)
            try:
                run([str(helpers / 'gh'), 'repo', 'clone', 'https://github.com/owner/project.git', str(control)])
                run([str(helpers / 'repoagent-git'), 'fetch', '--directory', str(control)])
            finally:
                manager.stop()
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
            # Local work left on another branch while origin was force-pushed is replaced at the startup idle boundary.
            git('-C', checkout, 'checkout', '--quiet', '-b', 'agent-branch')
            (checkout / 'README').write_text('stopped agent bytes\n')
            git('-C', checkout, '-c', 'user.name=fixture', '-c', 'user.email=fixture@example.test', 'commit', '--quiet', '-am', 'agent commit')
            (checkout / 'agent-notes').write_text('stopped agent notes\n')
            rewritten = self.force_push(SimpleNamespace(git=git, directory=directory))
            output = run(command)
            self.assertIn('poll-completed success=True', output)
            self.assertEqual(git('-C', checkout, 'rev-parse', 'HEAD'), rewritten)
            self.assertEqual(git('-C', checkout, 'symbolic-ref', 'HEAD'), 'refs/heads/main')
            self.assertEqual(git('-C', checkout, 'status', '--porcelain', '--untracked-files=all'), '')
            self.assertEqual((checkout / 'README').read_text(), 'rewritten history\n')
            self.assertEqual(json.loads(owner.read_text())['commit'], rewritten)


    @contextmanager
    def fixture(self, protocol=False, responses=False, webhooks=False, clone=True):
        with tempfile.TemporaryDirectory(prefix='refresh-matrix-') as temporary:
            directory = Path(temporary)
            provider = GitProvider(directory, responses=responses)
            provider.webhooks, provider.clone, provider.hooks = webhooks, clone, []
            try:
                home, workspace = directory / 'home', directory / 'workspace'
                home.mkdir(mode=0o700); workspace.mkdir()
                config = home / 'config.toml'
                config.write_text('web_search = "disabled"\napproval_policy = "never"\nsandbox_mode = "danger-full-access"\ncli_auth_credentials_store = "file"\n')
                config.chmod(0o600)
                enrollment = directory / 'enrollment.json'
                enrollment.write_text(json.dumps(provider.enrollment())); enrollment.chmod(0o600)
                environment = provider.environment()
                environment.update(HOME=str(home), CODEX_HOME=str(home), CODEX_CA_CERTIFICATE=str(provider.cert),
                                   PATH=str(self.native.parent) + os.pathsep + os.environ['PATH'],
                                   HTTP_PROXY=environment['HTTPS_PROXY'], http_proxy=environment['HTTPS_PROXY'],
                                   https_proxy=environment['HTTPS_PROXY'], ALL_PROXY='', all_proxy='', no_proxy='')
                def run(command):
                    result = subprocess.run(list(map(str, command)), env=environment, capture_output=True, text=True, timeout=60)
                    self.assertEqual(result.returncode, 0, (result.stdout + result.stderr).replace(provider.token, '[lease]'))
                    return result.stdout.strip()
                def git(*args): return run(['git', *args])
                state = directory / 'state.json'
                checkout = workspace / 'repository'
                command = self.controller_command + ['--repo', 'owner/project', '--app-id', '123', '--broker-url',
                           'https://broker.fixture', '--enrollment-file', str(enrollment), '--state', str(state),
                           '--workspace', str(workspace), '--clone', '--interval', '1']
                if not clone: command.remove('--clone')
                f = SimpleNamespace(directory=directory, provider=provider, environment=environment, state=state,
                                    checkout=checkout, owner=workspace / '.repoagent-checkout.json', command=command,
                                    run=run, git=git, journal=Path(str(state) + '.refresh.json'))
                if protocol:
                    incoming, outgoing = directory / 'native-in', directory / 'native-out'
                    os.mkfifo(incoming); os.mkfifo(outgoing)
                    f.commands = os.fdopen(os.open(incoming, os.O_RDWR), 'w', buffering=1)
                    f.responses = os.fdopen(os.open(outgoing, os.O_RDWR), 'r')
                    relay = directory / 'relay'; relay.mkdir()
                    (relay / 'codex').write_text(textwrap.dedent('''\
                        #!/usr/bin/python3
                        import json,os,subprocess,sys,threading
                        child=subprocess.Popen([os.environ['MATRIX_NATIVE'],*sys.argv[1:]],stdin=subprocess.PIPE,stdout=subprocess.PIPE,text=True)
                        if os.environ.get('MATRIX_PIDS'):
                         with open(os.environ['MATRIX_PIDS'],'w') as proof: json.dump(dict(relay=os.getpid(),native=child.pid),proof)
                        lock=threading.Lock()
                        def emit(line):
                         with lock: print(line,end='',flush=True)
                        threading.Thread(target=lambda:[emit(line) for line in child.stdout],daemon=True).start()
                        controls=False
                        def commands():
                         with open(os.environ['MATRIX_IN']) as stream:
                          for line in stream:
                           if json.loads(line).get('fixtureCloseOutput'): os.close(1);return
                           forward=json.loads(line).get('fixtureForward')
                           if forward: child.stdin.write(json.dumps(forward)+'\\n');child.stdin.flush()
                           else: emit(line)
                        for line in sys.stdin:
                         message=json.loads(line);method=message.get('method','')
                         if method in ('thread/start','thread/resume','turn/start') or (method=='initialize' and os.path.exists(os.environ['MATRIX_HOLD'])):
                          if not controls:
                           controls=True;threading.Thread(target=commands,daemon=True).start()
                          with open(os.environ['MATRIX_OUT'],'w') as stream: stream.write(line)
                         else: child.stdin.write(line);child.stdin.flush()
                        child.stdin.close();child.wait();sys.exit(child.returncode)
                        '''))
                    (relay / 'codex').chmod(0o700)
                    environment.update(PATH=str(relay) + os.pathsep + environment['PATH'], MATRIX_NATIVE=str(self.native),
                                       MATRIX_IN=str(incoming), MATRIX_OUT=str(outgoing), MATRIX_HOLD=str(directory / 'hold-reload'))
                try: yield f
                finally:
                    if protocol: f.commands.close(); f.responses.close()
            finally: provider.close()

    @contextmanager
    def controller(self, f, ready='poll-completed success=True'):
        process = subprocess.Popen(f.command, env=f.environment, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
        events = queue.Queue()
        lines = []
        def output():
            for line in process.stdout:
                lines.append(line); events.put(line)
            events.put(None)
        reader = threading.Thread(target=output); reader.start()
        def wait(fragment, timeout=20):
            deadline = time.monotonic() + timeout
            while True:
                try: line = events.get(timeout=max(0.001, deadline-time.monotonic()))
                except queue.Empty: self.fail('Timed out before ' + fragment + '; refresh outcomes:\n' + ''.join(item for item in lines if ' refresh-' in item))
                if line is None: self.fail('Controller closed before ' + fragment + '\n' + ''.join(lines))
                if fragment in line: return line
        def wait_or_native(fragment, timeout=20):
            # Event barrier for the protocol relay: None means native input arrived before the controller line.
            deadline = time.monotonic() + timeout
            while time.monotonic() < deadline:
                if select.select([f.responses], [], [], 0.05)[0]: return None
                try: line = events.get_nowait()
                except queue.Empty: continue
                if line is None: self.fail('Controller closed before ' + fragment + '\n' + ''.join(lines))
                if fragment in line: return line
            self.fail('Neither native input nor ' + fragment + ' was observed\n' + ''.join(lines))
        f.process, f.wait, f.wait_or_native, f.lines = process, wait, wait_or_native, lines
        try:
            wait(ready)
            yield
        finally:
            if process.poll() is None: process.send_signal(signal.SIGINT)
            try: process.wait(timeout=10)
            except subprocess.TimeoutExpired: process.kill(); process.wait(timeout=5)
            reader.join(timeout=5); process.stdout.close()
            print('MATRIX_CONTROLLER exit=' + str(process.returncode) + ' ' + ''.join(line for line in lines if any(s in line for s in ('checkout-refreshed', 'refresh-pending', 'refresh-blocked', 'manual-recovery', 'delivery-retry'))), flush=True)

    def publish(self, f, name='README', content='remote B\n'):
        seed = f.directory / 'seed'
        (seed / name).write_text(content)
        f.git('-C', seed, 'add', '.')
        f.git('-C', seed, 'commit', '-m', 'remote ' + name)
        f.git('-C', seed, 'push', f.directory / 'repositories/owner/project.git', 'HEAD:main')
        return f.git('-C', seed, 'rev-parse', 'HEAD')

    def snapshot(self, root):
        files = [p for p in root.rglob('*') if p.is_file() and '.git' not in p.relative_to(root).parts]
        files += [root / '.git/index', root / '.git/HEAD']
        files += list((root / '.git/refs/heads').rglob('*'))
        files += [root / '.git/MERGE_HEAD'] if (root / '.git/MERGE_HEAD').exists() else []
        return {str(p.relative_to(root)): p.read_bytes() for p in files if p.is_file()}

    def force_push(self, f, content='rewritten history\n'):
        seed = f.directory / 'seed'
        f.git('-C', seed, 'checkout', '--quiet', '--orphan', 'replacement')
        f.git('-C', seed, 'rm', '-rf', '--quiet', '.')
        (seed / 'README').write_text(content)
        f.git('-C', seed, 'add', '.')
        f.git('-C', seed, 'commit', '-m', 'rewrite mainline')
        f.git('-C', seed, 'push', f.directory / 'repositories/owner/project.git', '+HEAD:main')
        return f.git('-C', seed, 'rev-parse', 'HEAD')

    def diverge(self, f, case):
        """Leave the owned checkout in one local or diverged state an agent could create."""
        commit = ['-c', 'user.name=fixture', '-c', 'user.email=fixture@example.test', 'commit', '--quiet']
        if case.startswith('ignored'):
            (f.checkout / '.git/info/exclude').write_text('local-cache\n')
            if case == 'ignored-directory':
                (f.checkout / 'local-cache').mkdir(); (f.checkout / 'local-cache/bytes').write_bytes(b'local bytes')
            else: (f.checkout / 'local-cache').write_bytes(b'local bytes')
        if case in ('dirty', 'staged', 'commit', 'branch', 'force-push'):
            if case == 'branch': f.git('-C', f.checkout, 'checkout', '--quiet', '-b', 'agent-branch')
            (f.checkout / 'README').write_text('user bytes\n')
            if case != 'dirty': f.git('-C', f.checkout, 'add', '.')
            if case in ('commit', 'branch', 'force-push'): f.git('-C', f.checkout, *commit, '-m', 'local commit')
        if case == 'untracked':
            (f.checkout / 'local-only').write_bytes(b'local bytes')
            (f.checkout / 'scratch').mkdir(); (f.checkout / 'scratch/notes').write_bytes(b'local bytes')
        if case in ('skip-worktree', 'assume-unchanged'):
            f.git('-C', f.checkout, 'update-index', '--' + case, 'README')
            (f.checkout / 'README').write_text('hidden user bytes\n')
        if case == 'merge':
            f.git('-C', f.checkout, 'checkout', '--quiet', '-b', 'agent-side')
            (f.checkout / 'README').write_text('side bytes\n'); f.git('-C', f.checkout, *commit, '-am', 'side')
            f.git('-C', f.checkout, 'checkout', '--quiet', 'main')
            (f.checkout / 'README').write_text('main bytes\n'); f.git('-C', f.checkout, *commit, '-am', 'main')
            conflict = subprocess.run(['git', '-C', str(f.checkout), '-c', 'user.name=fixture', '-c', 'user.email=fixture@example.test',
                                       'merge', 'agent-side'], env=f.environment, capture_output=True, text=True, timeout=60)
            self.assertNotEqual(conflict.returncode, 0)
            self.assertTrue((f.checkout / '.git/MERGE_HEAD').is_file(), 'Fixture did not leave an interrupted merge')

    def local_state(self, f):
        return [f.git('-C', f.checkout, *args) for args in (('symbolic-ref', 'HEAD'), ('rev-parse', 'HEAD'),
                ('status', '--porcelain', '--untracked-files=all'), ('ls-files', '-v'))]

    def test_idle_refresh_forces_local_and_diverged_checkout_to_origin_and_preserves_outside_work(self):
        for case in ('ignored', 'dirty', 'staged', 'untracked', 'commit', 'branch', 'skip-worktree', 'assume-unchanged', 'merge',
                     'ignored-file', 'ignored-directory', 'force-push'):
            with self.subTest(case=case), self.fixture() as f, self.controller(f):
                initial = f.git('-C', f.checkout, 'rev-parse', 'HEAD')
                # Historical/mutating worktrees and runtime artifacts live outside the owned checkout and are never reset.
                history, artifacts = f.directory / 'history-worktree', f.directory / 'agent-artifacts'
                f.git('-C', f.checkout, 'worktree', 'add', '--quiet', '-b', 'history', history, initial)
                (history / 'README').write_text('historical mutation\n')
                artifacts.mkdir(); (artifacts / 'result').write_bytes(b'retained artifact')
                clean = self.local_state(f)
                self.diverge(f, case)
                if not case.startswith('ignored'): self.assertNotEqual(self.local_state(f), clean, 'Fixture left no local state')
                agent_branch = f.git('-C', f.checkout, 'rev-parse', 'agent-branch') if case == 'branch' else None
                if case == 'force-push': target, expected = self.force_push(f), 'rewritten history\n'
                else:
                    name = 'local-cache' if case in ('ignored-file', 'ignored-directory') else 'README'
                    target, expected = self.publish(f, name), 'remote B\n'
                f.wait('checkout-refreshed')
                actual = f.git('-C', f.checkout, 'rev-parse', 'HEAD')
                print('MATRIX_FORCE case=' + case + ' baseline=' + initial + ' origin=' + target + ' actual=' + actual, flush=True)
                self.assertEqual(actual, target)
                self.assertEqual(f.git('-C', f.checkout, 'symbolic-ref', 'HEAD'), 'refs/heads/main')
                self.assertEqual(f.git('-C', f.checkout, 'status', '--porcelain', '--untracked-files=all'), '')
                self.assertFalse(any(line[0] == 'S' or line[0].islower() for line in f.git('-C', f.checkout, 'ls-files', '-v').splitlines()))
                self.assertFalse((f.checkout / '.git/MERGE_HEAD').exists())
                self.assertEqual((f.checkout / ('local-cache' if case in ('ignored-file', 'ignored-directory') else 'README')).read_text(), expected)
                self.assertEqual(json.loads(f.owner.read_text())['commit'], target)
                if case == 'ignored': self.assertEqual((f.checkout / 'local-cache').read_bytes(), b'local bytes')
                if case == 'branch': self.assertEqual(f.git('-C', f.checkout, 'rev-parse', 'agent-branch'), agent_branch)
                self.assertEqual((history / 'README').read_text(), 'historical mutation\n')
                self.assertEqual(f.git('-C', history, 'rev-parse', 'HEAD'), initial)
                self.assertEqual(f.git('-C', history, 'symbolic-ref', 'HEAD'), 'refs/heads/history')
                self.assertEqual((artifacts / 'result').read_bytes(), b'retained artifact')
                self.settled(f)

    def test_idle_refresh_restores_local_and_diverged_checkout_while_origin_has_not_moved(self):
        for case in ('dirty', 'untracked', 'commit', 'branch', 'skip-worktree', 'merge', 'ignored', 'rebase'):
            with self.subTest(case=case), self.fixture() as f, self.controller(f):
                initial = f.git('-C', f.checkout, 'rev-parse', 'HEAD')
                history = f.directory / 'history-worktree'
                f.git('-C', f.checkout, 'worktree', 'add', '--quiet', '-b', 'history', history, initial)
                (history / 'README').write_text('historical mutation\n')
                if case == 'rebase':
                    # An interrupted operation defers the restore and leaves every local byte in place.
                    self.diverge(f, 'dirty'); (f.checkout / '.git/rebase-merge').mkdir()
                    before = self.snapshot(f.checkout)
                    f.wait('refresh-pending')
                    self.assertEqual(self.snapshot(f.checkout), before)
                    continue
                self.diverge(f, case)
                agent_branch = f.git('-C', f.checkout, 'rev-parse', 'agent-branch') if case == 'branch' else None
                if case == 'ignored':
                    # Ignored caches alone leave the checkout settled: nothing is restored or removed.
                    for _ in range(3): f.wait('poll-completed success=True')
                    self.assertFalse(any('checkout-refreshed' in line for line in f.lines))
                    self.assertEqual((f.checkout / 'local-cache').read_bytes(), b'local bytes')
                    continue
                f.wait('checkout-refreshed')
                print('MATRIX_RESTORE case=' + case + ' origin=' + initial + ' actual=' + f.git('-C', f.checkout, 'rev-parse', 'HEAD'), flush=True)
                self.assertEqual(f.git('-C', f.checkout, 'rev-parse', 'HEAD'), initial)
                self.assertEqual(f.git('-C', f.checkout, 'symbolic-ref', 'HEAD'), 'refs/heads/main')
                self.assertEqual(f.git('-C', f.checkout, 'status', '--porcelain', '--untracked-files=all'), '')
                self.assertFalse(any(line[0] == 'S' or line[0].islower() for line in f.git('-C', f.checkout, 'ls-files', '-v').splitlines()))
                self.assertFalse((f.checkout / '.git/MERGE_HEAD').exists())
                self.assertEqual(json.loads(f.owner.read_text())['commit'], initial)
                if case == 'branch': self.assertEqual(f.git('-C', f.checkout, 'rev-parse', 'agent-branch'), agent_branch)
                self.assertEqual((history / 'README').read_text(), 'historical mutation\n')
                self.assertEqual(f.git('-C', history, 'rev-parse', 'HEAD'), initial)
                self.settled(f)

    def test_periodic_refresh_defers_interrupted_operations_and_refuses_unsafe_remote_changes(self):
        for case in ('rebase', 'identity', 'moving-target'):
            with self.subTest(case=case), self.fixture() as f, self.controller(f):
                initial = f.git('-C', f.checkout, 'rev-parse', 'HEAD')
                if case == 'rebase': (f.checkout / '.git/rebase-merge').mkdir()
                before = self.snapshot(f.checkout)
                requests = len(f.provider.git_tokens)
                self.publish(f)
                if case == 'identity': f.provider.repository_id += 1
                if case == 'moving-target':
                    captured, release = threading.Event(), threading.Event()
                    def changed(sha):
                        f.provider.ref_observer = None; captured.set()
                        if not release.wait(15): raise TimeoutError('moving target release')
                    f.provider.ref_observer = changed
                    try:
                        self.assertTrue(captured.wait(10)); self.publish(f, content='remote C\n')
                    finally: release.set()
                f.wait('refresh-blocked' if case == 'identity' else 'refresh-pending')
                self.assertEqual(self.snapshot(f.checkout), before)
                self.assertEqual(json.loads(f.owner.read_text())['commit'], initial)
                if case == 'rebase': self.assertEqual(len(f.provider.git_tokens), requests)
                print('MATRIX_GIT case=' + case + ' baseline=' + initial + ' preserved=true', flush=True)

    def test_changed_identity_blocks_admission_when_branch_ref_is_unavailable(self):
        for status in (404, 200):
            with self.subTest(ref_status=status), self.fixture(protocol=True) as f, self.controller(f):
                initial = f.git('-C', f.checkout, 'rev-parse', 'HEAD')
                before = self.snapshot(f.checkout); requests = len(f.provider.git_tokens)
                f.provider.repository_id += 1; f.provider.ref_status = status
                self.publish(f)
                f.wait(' refresh-')  # Observe either durable pending or blocked, never infer idle from time.
                journal = json.loads(f.journal.read_text())
                self.assertEqual(self.snapshot(f.checkout), before)
                self.assertEqual(json.loads(f.owner.read_text())['commit'], initial)
                self.assertEqual(len(f.provider.git_tokens), requests)
                print('IDENTITY_REF status=' + str(status) + ' observed-id=' + str(f.provider.repository_id)
                      + ' blocked=' + str(journal['blocked']) + ' reason=' + journal.get('reason', ''), flush=True)
                self.assertTrue(journal['blocked'], 'Observed immutable identity mismatch must block despite unavailable/malformed ref')
                f.provider.discussions = [self.discussion(1)]
                f.wait('observation-error')
                self.assertFalse(select.select([f.responses], [], [], 0)[0], 'Identity mismatch admitted native work')

    def test_transient_final_metadata_failure_retains_pending_assistance_and_retries(self):
        with self.fixture(protocol=True) as f, self.controller(f):
            (f.checkout / '.git/info/exclude').write_text('local-cache\n')
            (f.checkout / 'local-cache').write_bytes(b'user cache\n')
            initial = f.git('-C', f.checkout, 'rev-parse', 'HEAD')
            before = self.snapshot(f.checkout)
            f.provider.postfetch_failure = dict(git=len(f.provider.git_tokens), count=0)
            target = self.publish(f)
            f.wait(' refresh-')
            journal = json.loads(f.journal.read_text())
            self.assertGreater(f.provider.postfetch_failure['count'], 0, 'Final metadata outage was not exercised')
            self.assertEqual(f.git('-C', f.checkout, 'rev-parse', 'FETCH_HEAD^{commit}'), target)
            self.assertEqual(self.snapshot(f.checkout), before)
            self.assertEqual(json.loads(f.owner.read_text())['commit'], initial)
            self.assertNotIn('applying', journal)
            self.assertGreater(journal['requested'], journal['completed'])
            print('POSTFETCH_METADATA fetched=' + target + ' baseline=' + initial + ' blocked=' + str(journal['blocked'])
                  + ' faults=' + str(f.provider.postfetch_failure['count']) + ' reason=' + journal.get('reason', ''), flush=True)
            self.assertFalse(journal['blocked'], 'Ordinary final metadata outage must defer without blocking safe assistance')
            f.provider.discussions = [self.discussion(1)]
            request = self.rpc(f, 'thread/start')
            self.reply(f, request, dict(cwd=str(f.checkout), thread=dict(id='S1', cwd=str(f.checkout))))
            request = self.rpc(f, 'turn/start'); self.reply(f, request, dict(turn=dict(id='T1')))
            f.wait('poll-completed success=True')
            self.assertEqual(f.git('-C', f.checkout, 'rev-parse', 'HEAD'), initial)
            f.provider.postfetch_failure = None
            self.terminal(f, 'S1', 'T1')
            f.wait('checkout-refreshed'); self.settled(f)
            self.assertEqual(f.git('-C', f.checkout, 'rev-parse', 'HEAD'), target)
            self.assertEqual(json.loads(f.owner.read_text())['commit'], target)
            self.assertEqual((f.checkout / 'local-cache').read_bytes(), b'user cache\n')

    def discussion(self, number):
        return dict(id='D' + str(number), number=number, url='https://github.com/owner/project/discussions/' + str(number),
                    body='@test-app[bot] fixture ' + str(number), bodyHTML='fixture', title='fixture',
                    author=dict(__typename='User', login='participant'), reactionGroups=[dict(content='EYES', viewerHasReacted=True)])

    def comment(self, number, author, body):
        return dict(id='C' + str(number), url='https://github.com/owner/project/discussions/1#discussioncomment-' + str(number),
                    body=body, bodyHTML=body, createdAt='2026-10-23T00:00:00Z', updatedAt='2026-10-23T00:00:00Z',
                    author=dict(__typename='User', login=author), reactionGroups=[], replyTo=None)

    def dispatched(self, request):
        text = request['params']['input'][0]['text']
        self.assertTrue(text.startswith('$github-discussion\n'), text[:80])
        return json.loads(text.split('\n', 1)[1])

    def subjects(self, envelope):
        return [(change['subject']['id'], change['subject']['author']['login'], change['subject']['body']) for change in envelope['changes']]

    def eyes(self, f, expected, timeout=20):
        # The outbox adds EYES in the background after acceptance, in queue order.
        deadline = time.monotonic() + timeout
        while f.provider.reactions != expected and time.monotonic() < deadline: time.sleep(0.05)
        self.assertEqual(f.provider.reactions, expected)

    def test_input_during_an_active_turn_waits_for_idle_before_dispatch_and_eyes(self):
        with self.fixture(protocol=True) as f, self.controller(f):
            f.provider.discussions = [dict(self.discussion(1), reactionGroups=[])]
            request = self.rpc(f, 'thread/start')
            self.reply(f, request, dict(cwd=str(f.checkout), thread=dict(id='S1', cwd=str(f.checkout))))
            request = self.rpc(f, 'turn/start'); self.reply(f, request, dict(turn=dict(id='T1')))
            f.wait('poll-completed success=True')
            self.eyes(f, ['D1'])
            # T1 remains active: the native has reported no idle transition.
            f.provider.comments = {'D1': [self.comment(1, 'alice', 'first while active'), self.comment(2, 'bob', 'second while active')]}
            for _ in range(2):  # Two complete observations, not elapsed time, establish that the input waits.
                self.assertIsNotNone(f.wait_or_native('poll-completed success=True'), 'Input reached native during the active turn')
            self.assertEqual(f.provider.reactions, ['D1'], 'Waiting input received EYES before dispatch')
            self.terminal(f, 'S1', 'T1')
            request = self.rpc(f, 'turn/start')
            self.assertEqual(f.provider.reactions, ['D1'], 'EYES preceded native acceptance of the dispatched batch')
            self.assertEqual(self.subjects(self.dispatched(request)),
                             [('C1', 'alice', 'first while active'), ('C2', 'bob', 'second while active')])
            self.reply(f, request, dict(turn=dict(id='T2')))
            f.wait('receipt https://github.com/owner/project/discussions/1#discussioncomment-2')
            self.assertEqual(f.provider.reactions, ['D1', 'C1', 'C2'])
            self.assertNotIn('waiting', json.loads(f.state.read_text())['discussions']['D1'])
            print('IDLE_DISPATCH waiting-polls=2 premature-eyes=none batch=C1,C2 eyes-after-acceptance=true', flush=True)

    def test_failed_turn_session_takes_the_next_input_without_a_restart(self):
        with self.fixture(protocol=True) as f, self.controller(f):
            f.provider.discussions = [dict(self.discussion(1), reactionGroups=[])]
            request = self.rpc(f, 'thread/start')
            self.reply(f, request, dict(cwd=str(f.checkout), thread=dict(id='S1', cwd=str(f.checkout))))
            request = self.rpc(f, 'turn/start'); self.reply(f, request, dict(turn=dict(id='T1')))
            f.wait('poll-completed success=True')
            # Pinned native leaves a failed turn's thread in systemError until its next turn, and accepts that turn.
            self.terminal(f, 'S1', 'T1', status='systemError', outcome='failed'); f.wait('native-event')
            f.provider.comments = {'D1': [self.comment(1, 'alice', 'first after error'), self.comment(2, 'bob', 'second after error')]}
            request = self.rpc(f, 'turn/start')
            self.assertEqual(request['params']['threadId'], 'S1')
            envelope = self.dispatched(request)
            self.assertEqual(envelope['_bridge']['mode'], 'continue')
            self.assertEqual(self.subjects(envelope), [('C1', 'alice', 'first after error'), ('C2', 'bob', 'second after error')])
            self.reply(f, request, dict(turn=dict(id='T2')))
            f.wait('receipt https://github.com/owner/project/discussions/1#discussioncomment-2')
            self.assertEqual(f.provider.reactions, ['D1', 'C1', 'C2'])
            self.terminal(f, 'S1', 'T2'); f.wait('native-event')
            print('IDLE_STATUS failed-turn=next-input-dispatched restart=none order=C1,C2', flush=True)

    def test_unknown_and_active_status_hold_input_in_order_across_restart(self):
        for resumed in ('active', 'unrecognized'):
            with self.subTest(resumed=resumed), self.fixture(protocol=True) as f:
                with self.controller(f):
                    f.provider.discussions = [dict(self.discussion(1), reactionGroups=[])]
                    request = self.rpc(f, 'thread/start')
                    self.reply(f, request, dict(cwd=str(f.checkout), thread=dict(id='S1', cwd=str(f.checkout))))
                    request = self.rpc(f, 'turn/start'); self.reply(f, request, dict(turn=dict(id='T1')))
                    f.wait('poll-completed success=True')
                    self.terminal(f, 'S1', 'T1', status='fixtureFutureStatus'); f.wait('native-event')
                    f.provider.comments = {'D1': [self.comment(1, 'alice', 'first after error'), self.comment(2, 'bob', 'second after error')]}
                    line = f.wait_or_native('input-waiting')
                    self.assertIsNotNone(line, 'Input reached a session whose native status is unrecognized')
                    self.assertIn('status=Unknown', line)
                    self.assertIsNotNone(f.wait_or_native('poll-completed success=True'), 'Input reached a session not reported idle')
                    self.eyes(f, ['D1'])
                saved = json.loads(f.state.read_text())['discussions']['D1']
                self.assertNotIn('pending', saved)
                self.assertEqual([item['change']['subject']['id'] for item in saved['waiting']['inputs']], ['C1', 'C2'])
                status = json.loads(f.run(self.controller_command + ['status', '--state', f.state]))
                self.assertEqual((status['work']['waiting'], status['sessions'][0]['waiting']), (2, 2))
                # A restarted native has not loaded S1: the controller loads it and uses only the reported status.
                with self.controller(f, 'mode=polling'):
                    request = self.rpc(f, 'thread/resume')
                    self.reply(f, request, dict(cwd=str(f.checkout), thread=dict(id='S1', cwd=str(f.checkout))),
                               status='active' if resumed == 'active' else 'fixtureFutureStatus')
                    for _ in range(2):
                        self.assertIsNotNone(f.wait_or_native('poll-completed success=True'), 'Input reached a session not reported idle')
                    self.assertEqual(f.provider.reactions, ['D1'])
                    self.assertEqual(json.loads(f.journal.read_text())['active'], {}, 'Unused load admission was retained')
                    self.notify(f, 'thread/status/changed', threadId='S1', status=dict(type='idle'))
                    request = self.rpc(f, 'turn/start')
                    envelope = self.dispatched(request)
                    self.assertEqual(envelope['_bridge']['mode'], 'continue')
                    self.assertEqual(self.subjects(envelope), [('C1', 'alice', 'first after error'), ('C2', 'bob', 'second after error')])
                    self.assertEqual(f.provider.reactions, ['D1'], 'EYES preceded acceptance after restart')
                    self.reply(f, request, dict(turn=dict(id='T2')))
                    f.wait('receipt https://github.com/owner/project/discussions/1#discussioncomment-2')
                    self.assertEqual(f.provider.reactions, ['D1', 'C1', 'C2'])
                    self.terminal(f, 'S1', 'T2'); f.wait('native-event')
                print('IDLE_STATUS unknown=held restart-resume=' + resumed + ' held-until-idle=true order=C1,C2', flush=True)

    def test_oversized_waiting_input_splits_into_ordered_batches_without_loss(self):
        with self.fixture(protocol=True) as f, self.controller(f):
            f.provider.discussions = [dict(self.discussion(1), reactionGroups=[])]
            request = self.rpc(f, 'thread/start')
            self.reply(f, request, dict(cwd=str(f.checkout), thread=dict(id='S1', cwd=str(f.checkout))))
            request = self.rpc(f, 'turn/start'); self.reply(f, request, dict(turn=dict(id='T1')))
            f.wait('poll-completed success=True')
            # Five maximum-size GitHub comment bodies exceed one controller batch.
            bodies = [str(number) + ':' + 'x' * 65534 for number in range(1, 6)]
            f.provider.comments = {'D1': [self.comment(number, 'author' + str(number), body) for number, body in enumerate(bodies, 1)]}
            for _ in range(2):
                self.assertIsNotNone(f.wait_or_native('poll-completed success=True'), 'Input reached native during the active turn')
            delivered, turn = [], 1
            while len(delivered) < len(bodies):
                self.terminal(f, 'S1', 'T' + str(turn)); turn += 1
                request = self.rpc(f, 'turn/start')
                text = request['params']['input'][0]['text']
                self.assertLessEqual(len(text.split('\n', 1)[1]), 256 * 1024)
                batch = self.subjects(self.dispatched(request))
                self.assertTrue(batch, 'Empty batch dispatched')
                self.reply(f, request, dict(turn=dict(id='T' + str(turn))))
                f.wait('receipt https://github.com/owner/project/discussions/1#discussioncomment-' + batch[-1][0][1:])
                delivered += batch
                self.assertEqual(f.provider.reactions, ['D1'] + [subject for subject, _, _ in delivered])
                if len(delivered) < len(bodies):  # The remainder waits for the next native idle report.
                    self.assertIsNotNone(f.wait_or_native('poll-completed success=True'), 'Remainder reached the active turn')
            self.assertEqual(delivered, [('C' + str(number), 'author' + str(number), body) for number, body in enumerate(bodies, 1)])
            self.assertGreaterEqual(turn - 1, 2, 'Fixture did not exceed one batch')
            self.assertNotIn('waiting', json.loads(f.state.read_text())['discussions']['D1'])
            print('IDLE_SPLIT batches=' + str(turn - 1) + ' changes=' + str(len(delivered)) + ' order=preserved', flush=True)

    def test_rejected_native_input_is_retried_after_backoff_as_a_marked_redelivery(self):
        with self.fixture(protocol=True) as f, self.controller(f):
            f.provider.discussions = [dict(self.discussion(1), reactionGroups=[])]
            request = self.rpc(f, 'thread/start')
            self.reply(f, request, dict(cwd=str(f.checkout), thread=dict(id='S1', cwd=str(f.checkout))))
            request = self.rpc(f, 'turn/start'); self.reply(f, request, dict(turn=dict(id='T1')))
            self.terminal(f, 'S1', 'T1'); f.wait('native-event')
            self.eyes(f, ['D1'])
            f.provider.comments = {'D1': [self.comment(1, 'alice', 'please answer')]}
            request = self.rpc(f, 'turn/start')
            first = self.dispatched(request)
            self.assertNotIn('redelivery', first['_bridge'])
            self.reject(f, request)
            for _ in range(2):  # Neither a blocked Discussion nor a retry on every poll.
                self.assertIsNotNone(f.wait_or_native('poll-completed'), 'Rejected input was retried without backoff')
            request = self.rpc(f, 'turn/start')
            retried = self.dispatched(request)
            self.assertIs(retried['_bridge'].get('redelivery'), True)
            self.assertEqual(self.subjects(retried), [('C1', 'alice', 'please answer')])
            self.assertEqual(f.provider.reactions, ['D1'], 'EYES preceded acceptance of the redelivery')
            self.reply(f, request, dict(turn=dict(id='T2')))
            f.wait('receipt https://github.com/owner/project/discussions/1#discussioncomment-1')
            self.assertEqual(f.provider.reactions, ['D1', 'C1'])
            self.terminal(f, 'S1', 'T2'); f.wait('native-event')
            # Input observed after the accepted redelivery is ordinary again.
            f.provider.comments['D1'].append(self.comment(2, 'bob', 'follow-up'))
            request = self.rpc(f, 'turn/start')
            envelope = self.dispatched(request)
            self.assertNotIn('redelivery', envelope['_bridge'])
            self.assertEqual(self.subjects(envelope), [('C2', 'bob', 'follow-up')])
            self.reply(f, request, dict(turn=dict(id='T3')))
            f.wait('receipt https://github.com/owner/project/discussions/1#discussioncomment-2')
            saved = json.loads(f.state.read_text())['discussions']['D1']
            self.assertNotIn('pending', saved); self.assertNotIn('waiting', saved)
            self.assertFalse(json.loads(f.journal.read_text())['blocked'])
            self.terminal(f, 'S1', 'T3'); f.wait('native-event')
            print('REDELIVERY rejected=retried marker=_bridge.redelivery eyes-after-acceptance=true', flush=True)

    def test_unacknowledged_native_input_is_redelivered_once_idle_while_the_checkout_stays_excluded(self):
        with self.fixture(protocol=True) as f, self.controller(f):
            f.provider.discussions = [dict(self.discussion(1), reactionGroups=[])]
            request = self.rpc(f, 'thread/start')
            self.reply(f, request, dict(cwd=str(f.checkout), thread=dict(id='S1', cwd=str(f.checkout))))
            request = self.rpc(f, 'turn/start'); self.reply(f, request, dict(turn=dict(id='T1')))
            self.terminal(f, 'S1', 'T1'); f.wait('native-event')
            initial = f.git('-C', f.checkout, 'rev-parse', 'HEAD')
            f.provider.comments = {'D1': [self.comment(1, 'alice', 'please answer')]}
            request = self.rpc(f, 'turn/start')
            self.assertEqual(self.subjects(self.dispatched(request)), [('C1', 'alice', 'please answer')])
            target = self.publish(f)
            f.wait('delivery-retry', timeout=90)  # The acknowledgement is lost: the controller's native request deadline passes.
            self.pending(f)
            self.assertEqual(f.git('-C', f.checkout, 'rev-parse', 'HEAD'), initial, 'Checkout refreshed while delivery was uncertain')
            # The native accepted the input after all; its late turn runs past the retry backoff.
            self.notify(f, 'thread/status/changed', threadId='S1', status=dict(type='active'))
            self.notify(f, 'turn/started', threadId='S1', turn=dict(id='T2', status='inProgress'))
            deadline = time.monotonic() + 7
            while time.monotonic() < deadline:
                self.assertIsNotNone(f.wait_or_native('poll-completed'), 'Redelivery reached a session that was not idle')
            journal = json.loads(f.journal.read_text())
            self.assertFalse(journal['blocked'], journal.get('reason'))
            self.assertEqual(f.git('-C', f.checkout, 'rev-parse', 'HEAD'), initial, 'Checkout refreshed under the late native turn')
            self.terminal(f, 'S1', 'T2')
            request = self.rpc(f, 'turn/start')
            redelivered = self.dispatched(request)
            self.assertIs(redelivered['_bridge'].get('redelivery'), True)
            self.assertEqual(self.subjects(redelivered), [('C1', 'alice', 'please answer')])
            self.assertEqual(f.provider.reactions, ['D1'], 'EYES preceded acknowledged acceptance')
            self.reply(f, request, dict(turn=dict(id='T3')))
            f.wait('receipt https://github.com/owner/project/discussions/1#discussioncomment-1')
            self.assertEqual(f.provider.reactions, ['D1', 'C1'])
            self.terminal(f, 'S1', 'T3')
            # The acknowledged redelivery completed, so no native turn can still hold the input: refresh resumes.
            f.wait('checkout-refreshed'); self.settled(f)
            self.assertEqual(f.git('-C', f.checkout, 'rev-parse', 'HEAD'), target)
            self.assertEqual(json.loads(f.journal.read_text())['active'], {})
            self.assertNotIn('waiting', json.loads(f.state.read_text())['discussions']['D1'])
            print('REDELIVERY lost-ack=redelivered late-native-turn=held-until-idle checkout=excluded-until-resolved', flush=True)

    def test_delivery_interrupted_by_restart_is_redelivered_and_a_failed_receipt_never_resends_it(self):
        with self.fixture(protocol=True, clone=False) as f:
            with self.controller(f):
                f.provider.discussions = [dict(self.discussion(1), reactionGroups=[])]
                request = self.rpc(f, 'thread/start'); self.reply(f, request, dict(thread=dict(id='S1')))
                request = self.rpc(f, 'turn/start'); self.reply(f, request, dict(turn=dict(id='T1')))
                self.terminal(f, 'S1', 'T1'); f.wait('native-event')
                self.eyes(f, ['D1'])
                f.provider.comments = {'D1': [self.comment(1, 'alice', 'please answer')]}
                request = self.rpc(f, 'turn/start')  # The controller stops before any acknowledgement arrives.
                self.assertNotIn('redelivery', self.dispatched(request)['_bridge'])
            f.provider.reaction_status = 403  # GitHub will refuse EYES for the accepted redelivery.
            with self.controller(f, 'mode=polling'):
                request = self.rpc(f, 'thread/resume'); self.reply(f, request, dict(thread=dict(id='S1')))
                request = self.rpc(f, 'turn/start')
                redelivered = self.dispatched(request)
                self.assertIs(redelivered['_bridge'].get('redelivery'), True)
                self.assertEqual(self.subjects(redelivered), [('C1', 'alice', 'please answer')])
                self.reply(f, request, dict(turn=dict(id='T2')))
                f.wait('receipt-error https://github.com/owner/project/discussions/1#discussioncomment-1')
                self.terminal(f, 'S1', 'T2'); f.wait('native-event')
                for _ in range(2):
                    self.assertIsNotNone(f.wait_or_native('poll-completed success=True'), 'Accepted input was resent after its receipt failed')
            with self.controller(f, 'mode=polling'):
                for _ in range(2):
                    self.assertIsNotNone(f.wait_or_native('poll-completed success=True'), 'Accepted input was resent after a restart')
                self.assertNotIn(f.provider.token, ''.join(f.lines))
            self.assertEqual(f.provider.reactions, ['D1'])
            saved = json.loads(f.state.read_text())['discussions']['D1']
            self.assertNotIn('pending', saved); self.assertNotIn('waiting', saved)
            print('REDELIVERY restart=redelivered receipt-failure=no-resend', flush=True)

    def rpc(self, f, method):
        self.assertTrue(select.select([f.responses], [], [], 20)[0], 'Missing native request ' + method)
        message = json.loads(f.responses.readline())
        self.assertEqual(message['method'], method)
        return message

    def reply(self, f, request, result, status='idle'):
        # Pinned native thread/start and thread/resume results report the loaded thread's runtime status.
        if request['method'] in ('thread/start', 'thread/resume') and 'thread' in result:
            result['thread'].setdefault('status', dict(type=status))
        f.commands.write(json.dumps(dict(id=request['id'], result=result)) + '\n'); f.commands.flush()

    def reject(self, f, request, message='fixture rejection'):
        f.commands.write(json.dumps(dict(id=request['id'], error=dict(code=-32600, message=message))) + '\n'); f.commands.flush()

    def notify(self, f, method, **params):
        f.commands.write(json.dumps(dict(method=method, params=params)) + '\n'); f.commands.flush()

    def terminal(self, f, thread, turn, status='idle', outcome='completed'):
        # Pinned native order: the thread status changes before turn/completed.
        self.notify(f, 'thread/status/changed', threadId=thread, status=dict(type=status))
        self.notify(f, 'turn/completed', threadId=thread, turn=dict(id=turn, status=outcome))

    def pending(self, f):
        for _ in range(5):
            f.wait('poll-completed success=True')
            journal = json.loads(f.journal.read_text())
            if journal['requested'] > journal['completed']: return journal
        self.fail('Periodic trigger was not persisted while native work was active')

    def test_all_sessions_block_refresh_until_correlated_completion_and_preserve_reload_checkpoint(self):
        for hold_reload in (False, True):
            with self.subTest(hold_reload=hold_reload), self.fixture(protocol=True) as f:
                # Initial empty observation establishes production bootstrap before protocol-only sessions.
                with self.controller(f):
                    f.provider.discussions = [self.discussion(1), self.discussion(2)]
                    for i in (1, 2):
                        request = self.rpc(f, 'thread/start')
                        self.reply(f, request, dict(cwd=str(f.checkout), thread=dict(id='S' + str(i), cwd=str(f.checkout))))
                        request = self.rpc(f, 'turn/start')
                        self.reply(f, request, dict(turn=dict(id='T' + str(i))))
                    f.wait('poll-completed success=True')
                    initial = f.git('-C', f.checkout, 'rev-parse', 'HEAD')
                    requests = len(f.provider.git_tokens)
                    target = self.publish(f)
                    self.pending(f)
                    self.assertEqual(len(f.provider.git_tokens), requests)
                    self.terminal(f, 'S1', 'T1')
                    f.wait('native-event')
                    self.pending(f)
                    self.assertEqual(f.git('-C', f.checkout, 'rev-parse', 'HEAD'), initial)
                    self.assertEqual(len(f.provider.git_tokens), requests)
                    self.terminal(f, 'S1', 'T1')  # Known duplicate cannot release session2.
                    f.wait('native-event')
                    if hold_reload: (f.directory / 'hold-reload').touch()
                    self.terminal(f, 'S2', 'T2')
                    f.wait('checkout-refreshed')
                    self.assertEqual(f.git('-C', f.checkout, 'rev-parse', 'HEAD'), target)
                    if hold_reload:
                        self.rpc(f, 'initialize')  # Git/owner B; real replacement initialize deliberately withheld.
                        journal = json.loads(f.journal.read_text())
                        self.assertIn('applying', journal, 'Applying checkpoint cleared while replacement initialize is pending')
                        self.assertEqual(journal['applying']['target'], target)
                        self.assertLess(journal['completed'], journal['requested'])
                    else: self.settled(f)
                if hold_reload:
                    (f.directory / 'hold-reload').unlink()
                    result = subprocess.run(f.command + ['--once'], env=f.environment, capture_output=True, text=True, timeout=20)
                    self.assertEqual(result.returncode, 1)
                    self.assertIn('uncertain', result.stdout)
                    self.assertEqual(json.loads(f.journal.read_text())['applying']['target'], target)
                print('MATRIX_LIFECYCLE all-session-completion=true hold-reload=' + str(hold_reload), flush=True)


    def settled(self, f):
        for _ in range(5):
            f.wait('poll-completed success=True')
            journal = json.loads(f.journal.read_text())
            if not journal.get('applying') and journal['completed'] == journal['requested']:
                self.assertFalse(journal['blocked'])
                self.assertEqual(journal['completed'], journal['requested'])
                return
        self.fail('Native reload did not complete its durable checkpoint')

    def test_refresh_precedes_waiting_input_and_early_completion_waits_for_acknowledgement(self):
        with self.fixture(protocol=True) as f, self.controller(f):
            f.provider.discussions = [self.discussion(1)]
            request = self.rpc(f, 'thread/start')
            self.reply(f, request, dict(cwd=str(f.checkout), thread=dict(id='S1', cwd=str(f.checkout))))
            request = self.rpc(f, 'turn/start'); self.reply(f, request, dict(turn=dict(id='T1')))
            f.wait('poll-completed success=True')
            initial = f.git('-C', f.checkout, 'rev-parse', 'HEAD')
            requests = len(f.provider.git_tokens)
            target = self.publish(f); self.pending(f)
            f.provider.discussions[0]['body'] += ' changed'
            self.assertIsNotNone(f.wait_or_native('input-waiting'), 'Input reached native during the active turn')
            self.assertEqual(f.git('-C', f.checkout, 'rev-parse', 'HEAD'), initial)
            self.assertEqual(len(f.provider.git_tokens), requests)
            self.terminal(f, 'S1', 'T1')
            # The eligible refresh takes the idle boundary before the waiting input reaches the reloaded native.
            f.wait('checkout-refreshed')
            self.assertEqual(f.git('-C', f.checkout, 'rev-parse', 'HEAD'), target)
            request = self.rpc(f, 'thread/resume')
            self.reply(f, request, dict(cwd=str(f.checkout), thread=dict(id='S1', cwd=str(f.checkout))))
            request = self.rpc(f, 'turn/start')
            self.assertIn(' changed', self.dispatched(request)['changes'][0]['subject']['body'])
            self.terminal(f, 'S1', 'T2')  # The native can complete a turn before its acknowledgement arrives.
            f.wait('native-event')
            journal = json.loads(f.journal.read_text())
            self.assertEqual(len(journal['active']), 1, 'Early terminal cleared admission before acknowledgement')
            self.assertFalse(next(iter(journal['active'].values()))['confirmed'])
            self.reply(f, request, dict(turn=dict(id='T2')))
            f.wait('forwarded'); self.settled(f)
            self.assertEqual(json.loads(f.journal.read_text())['active'], {})
            print('MATRIX_EARLY refresh-before-waiting-input=true ack-and-save-required=true', flush=True)

    def test_unknown_terminal_and_failed_journal_write_never_publish_idle(self):
        for case in ('unknown-terminal', 'journal-write'):
            with self.subTest(case=case), self.fixture(protocol=True) as f:
                with self.controller(f):
                    f.provider.discussions = [self.discussion(1)]
                    request = self.rpc(f, 'thread/start')
                    self.reply(f, request, dict(cwd=str(f.checkout), thread=dict(id='S1', cwd=str(f.checkout))))
                    request = self.rpc(f, 'turn/start'); self.reply(f, request, dict(turn=dict(id='T1')))
                    f.wait('poll-completed success=True')
                    initial = f.git('-C', f.checkout, 'rev-parse', 'HEAD')
                    requests = len(f.provider.git_tokens)
                    self.publish(f); self.pending(f)
                    if case == 'journal-write': Path(str(f.journal) + '.tmp').mkdir()
                    self.terminal(f, 'unknown' if case == 'unknown-terminal' else 'S1', 'T1')
                    f.wait('refresh-blocked')
                    journal = json.loads(f.journal.read_text())
                    self.assertTrue(journal['active'])
                    if case == 'unknown-terminal': self.assertTrue(journal['blocked'])
                    f.provider.discussions[0]['body'] += ' changed after fault'
                    # An unmatched terminal leaves S1's accepted turn outstanding, so input waits before admission.
                    f.wait('input-waiting' if case == 'unknown-terminal' else 'observation-error')
                    self.assertFalse(select.select([f.responses], [], [], 0)[0])
                    self.assertEqual(f.git('-C', f.checkout, 'rev-parse', 'HEAD'), initial)
                    self.assertEqual(len(f.provider.git_tokens), requests)
                result = subprocess.run(f.command + ['--once'], env=f.environment, capture_output=True, text=True, timeout=20)
                self.assertEqual(result.returncode, 1)
                self.assertIn('refresh' if case == 'journal-write' else 'uncertain', result.stdout.lower())
                print('MATRIX_FAULT case=' + case + ' durable-busy=true admission-blocked=true', flush=True)



    def test_active_turn_delays_forced_refresh_and_next_turn_starts_on_origin(self):
        with self.fixture(protocol=True) as f, self.controller(f):
            f.provider.discussions = [self.discussion(1)]
            request = self.rpc(f, 'thread/start')
            self.reply(f, request, dict(cwd=str(f.checkout), thread=dict(id='S1', cwd=str(f.checkout))))
            request = self.rpc(f, 'turn/start'); self.reply(f, request, dict(turn=dict(id='T1')))
            f.wait('poll-completed success=True')
            initial = f.git('-C', f.checkout, 'rev-parse', 'HEAD')
            # The active turn edits and commits inside the owned checkout while origin is force-pushed.
            (f.checkout / 'README').write_text('active agent bytes\n')
            f.git('-C', f.checkout, '-c', 'user.name=fixture', '-c', 'user.email=fixture@example.test', 'commit', '--quiet', '-am', 'agent commit')
            (f.checkout / 'agent-notes').write_text('active agent notes\n')
            during = self.snapshot(f.checkout)
            requests = len(f.provider.git_tokens)
            target = self.force_push(f); self.pending(f)
            self.assertEqual(self.snapshot(f.checkout), during, 'Refresh changed the checkout under an active turn')
            self.assertEqual(len(f.provider.git_tokens), requests, 'Refresh fetched while native work was active')
            self.terminal(f, 'S1', 'T1')
            f.wait('checkout-refreshed'); self.settled(f)
            f.provider.discussions[0]['body'] += ' next input'
            request = self.rpc(f, 'thread/resume')
            # Native has not started the next turn yet; it must already see origin's content.
            self.assertEqual(f.git('-C', f.checkout, 'rev-parse', 'HEAD'), target)
            self.assertEqual((f.checkout / 'README').read_text(), 'rewritten history\n')
            self.assertFalse((f.checkout / 'agent-notes').exists())
            self.assertEqual(f.git('-C', f.checkout, 'status', '--porcelain', '--untracked-files=all'), '')
            self.reply(f, request, dict(thread=dict(id='S1', cwd=str(f.checkout)), cwd=str(f.checkout)))
            request = self.rpc(f, 'turn/start'); self.reply(f, request, dict(turn=dict(id='T2')))
            f.wait('poll-completed success=True')
            self.assertNotEqual(initial, target)
            self.assertEqual(json.loads(f.owner.read_text())['commit'], target)
            self.terminal(f, 'S1', 'T2'); f.wait('native-event')



    def test_unexpected_replacement_native_eof_preserves_active_uncertainty(self):
        with self.fixture(protocol=True) as f, self.controller(f):
            baseline = self.publish(f)
            f.wait('checkout-refreshed'); self.settled(f)
            f.provider.discussions = [self.discussion(1)]
            request = self.rpc(f, 'thread/start')
            self.reply(f, request, dict(cwd=str(f.checkout), thread=dict(id='S1', cwd=str(f.checkout))))
            request = self.rpc(f, 'turn/start'); self.reply(f, request, dict(turn=dict(id='T1')))
            f.wait('poll-completed success=True')
            requests = len(f.provider.git_tokens)
            self.publish(f, content='later C\n'); self.pending(f)
            f.commands.write(json.dumps(dict(fixtureCloseOutput=True)) + '\n'); f.commands.flush()
            f.wait('refresh-blocked')
            journal = json.loads(f.journal.read_text())
            self.assertTrue(journal['blocked']); self.assertTrue(journal['active'])
            f.provider.discussions[0]['body'] += ' after replacement EOF'
            f.wait('observation-error')
            self.assertFalse(select.select([f.responses], [], [], 0)[0])
            self.assertEqual(f.git('-C', f.checkout, 'rev-parse', 'HEAD'), baseline)
            self.assertEqual(len(f.provider.git_tokens), requests)
            print('MATRIX_EOF replacement-epoch=blocked active-retained=true nofetch=true', flush=True)



    def test_create_resume_and_clean_active_restart_preserve_reservations(self):
        for case in ('create', 'resume', 'save-failure', 'active-restart'):
            with self.subTest(case=case), self.fixture(protocol=True) as f:
                if case in ('resume', 'active-restart'):
                    with self.controller(f):
                        f.provider.discussions = [self.discussion(1)]
                        request = self.rpc(f, 'thread/start')
                        self.reply(f, request, dict(cwd=str(f.checkout), thread=dict(id='S1', cwd=str(f.checkout))))
                        request = self.rpc(f, 'turn/start'); self.reply(f, request, dict(turn=dict(id='T1')))
                        f.wait('poll-completed success=True')
                        if case == 'resume':
                            self.terminal(f, 'S1', 'T1'); f.wait('native-event')
                            self.assertEqual(json.loads(f.journal.read_text())['active'], {})
                    if case == 'active-restart':
                        saved = json.loads(f.journal.read_text())
                        self.assertTrue(saved['active']); self.assertFalse(saved['blocked'])
                        result = subprocess.run(f.command + ['--once'], env=f.environment, capture_output=True, text=True, timeout=20)
                        self.assertEqual(result.returncode, 1); self.assertIn('uncertain', result.stdout)
                        self.assertTrue(json.loads(f.journal.read_text())['active'])
                        print('GAP clean-active restart=blocked persisted-active=true', flush=True)
                        continue
                f.provider.ref_status = 404
                with self.controller(f, 'refresh-pending'):
                    if case == 'resume': f.provider.discussions[0]['body'] += ' cold resumed input'
                    else: f.provider.discussions = [self.discussion(1)]
                    request = self.rpc(f, 'thread/resume' if case == 'resume' else 'thread/start')
                    f.provider.ref_status = None
                    initial = f.git('-C', f.checkout, 'rev-parse', 'HEAD')
                    requests = len(f.provider.git_tokens)
                    target = self.publish(f)
                    # The explicit held RPC makes this a busy barrier, never an idle-by-time inference.
                    self.assertFalse(select.select([f.responses], [], [], 2)[0], 'First send crossed held create/resume')
                    journal = json.loads(f.journal.read_text())
                    self.assertEqual(len(journal['active']), 1)
                    self.assertGreater(journal['requested'], journal['completed'])
                    self.assertFalse(next(iter(journal['active'].values()))['confirmed'])
                    self.assertEqual(f.git('-C', f.checkout, 'rev-parse', 'HEAD'), initial)
                    self.assertEqual(len(f.provider.git_tokens), requests)
                    if case == 'save-failure': Path(str(f.state) + '.tmp').mkdir()
                    self.reply(f, request, dict(cwd=str(f.checkout), thread=dict(id='S1', cwd=str(f.checkout))))
                    if case == 'save-failure':
                        # Nothing was sent, so the idle refresh may proceed while the batch waits to retry.
                        f.wait('checkout-refreshed')
                        self.assertTrue(any(' delivery-retry ' in line for line in f.lines))
                        self.assertFalse(json.loads(f.journal.read_text())['blocked'])
                        self.assertEqual(f.git('-C', f.checkout, 'rev-parse', 'HEAD'), target)
                        self.assertFalse(select.select([f.responses], [], [], 0)[0], 'Input was sent without a durable checkpoint')
                        Path(str(f.state) + '.tmp').rmdir()  # Release only the fixture write fault.
                        request = self.rpc(f, 'thread/resume')  # The refresh reloaded the native.
                        self.reply(f, request, dict(cwd=str(f.checkout), thread=dict(id='S1', cwd=str(f.checkout))))
                        request = self.rpc(f, 'turn/start')
                        envelope = self.dispatched(request)
                        self.assertEqual(envelope['_bridge']['mode'], 'bootstrap')
                        self.assertNotIn('redelivery', envelope['_bridge'], 'No earlier turn/start carried this input')
                        self.reply(f, request, dict(turn=dict(id='T1')))
                        self.terminal(f, 'S1', 'T1'); f.wait('native-event')
                    else:
                        request = self.rpc(f, 'turn/start')
                        turn = 'T2' if case == 'resume' else 'T1'
                        self.reply(f, request, dict(turn=dict(id=turn)))
                        self.terminal(f, 'S1', turn)
                        f.wait('checkout-refreshed'); self.settled(f)
                        self.assertEqual(f.git('-C', f.checkout, 'rev-parse', 'HEAD'), target)
                print('GAP case=' + case + ' reservation-and-first-send=protected', flush=True)



    def test_late_fetch_head_change_cannot_replace_the_captured_validated_target(self):
        with self.fixture() as f, self.controller(f):
            initial = f.git('-C', f.checkout, 'rev-parse', 'HEAD')
            observed = []
            def after_lookup(sha):
                if sha == initial: return
                observed.append(sha)
                if len(observed) == 2:
                    f.provider.ref_observer = None
                    (f.checkout / '.git/FETCH_HEAD').write_text(initial + '\t\tlate fixture ref change\n')
            f.provider.ref_observer = after_lookup
            target = self.publish(f)
            f.wait('checkout-refreshed'); self.settled(f)
            self.assertEqual(observed, [target, target])
            self.assertEqual(f.git('-C', f.checkout, 'rev-parse', 'FETCH_HEAD'), initial)
            self.assertEqual(f.git('-C', f.checkout, 'rev-parse', 'HEAD'), target)
            self.assertEqual(json.loads(f.owner.read_text())['commit'], target)
            print('GAP late-FETCH_HEAD=' + initial + ' captured-applied=' + target, flush=True)



    def test_genuine_controller_resume_receives_refreshed_instructions_skills_and_preserved_history(self):
        from test_checkout_native import native_config
        with self.fixture(responses=True) as f:
            home, seed = f.directory / 'home', f.directory / 'seed'
            config = home / 'config.toml'
            config.write_text(config.read_text() + FIXTURE_MODEL)
            skill = seed / '.agents/skills/repo-refresh'
            skill.mkdir(parents=True); (seed / '.codex').mkdir()
            def context(version, effort):
                (seed / 'AGENTS.md').write_text('Repository context NATIVE_AGENTS_' + version + '.\n')
                (seed / '.codex/config.toml').write_text('model_reasoning_effort = "' + effort + '"\n')
                (skill / 'SKILL.md').write_text('---\nname: repo-refresh\ndescription: Fixture scoped skill NATIVE_DESCRIPTION_' + version + '.\n---\nScoped instruction NATIVE_SKILL_BODY_' + version + '.\n')
            context('A', 'low'); self.publish(f, content='native initial A\n')
            with self.controller(f):
                f.provider.discussions = [self.discussion(1)]
                f.provider.discussions[0]['body'] += ' $repo-refresh NATIVE_USER_FIRST'
                first = native_event(f.wait('native-event'))
                self.assertEqual(first['method'], 'turn/completed'); print('NATIVE_COMPLETED ' + json.dumps(first), flush=True)
                self.settled(f)
                self.assertEqual(first['status'], 'completed')
                self.assertEqual(len(f.provider.model_requests), 1)
                session = json.loads(f.state.read_text())['discussions']['D1']['sessionId']
                self.assertEqual(first['thread'], session)
                rollout, = home.rglob('rollout-*.jsonl')
                prefix = rollout.read_bytes()
                self.assertIn(b'NATIVE_USER_FIRST', prefix)
                self.assertIn(b'Harmless fixture assistant 1.', prefix)
                request_a = json.dumps(f.provider.model_requests[0]['body'])
                self.assertIn('NATIVE_AGENTS_A', request_a)
                self.assertIn('NATIVE_SKILL_BODY_A', request_a)
                # Local instruction/skill edits and a rewritten origin mainline: origin's B must win at idle.
                (f.checkout / 'AGENTS.md').write_text('Local agent edit NATIVE_AGENTS_LOCAL.\n')
                (f.checkout / '.agents/skills/repo-refresh/SKILL.md').write_text('---\nname: repo-refresh\ndescription: Local NATIVE_DESCRIPTION_LOCAL.\n---\nLocal NATIVE_SKILL_BODY_LOCAL.\n')
                context('B', 'high'); f.git('-C', seed, 'add', '.')
                f.git('-C', seed, 'commit', '--quiet', '--amend', '-m', 'rewrite native context B')
                f.git('-C', seed, 'push', f.directory / 'repositories/owner/project.git', '+HEAD:main')
                target = f.git('-C', seed, 'rev-parse', 'HEAD')
                f.wait('checkout-refreshed'); self.settled(f)
                probe = native_config(f.checkout, home, discovery=True, environment=f.environment)
                self.assertEqual(probe['config']['model_reasoning_effort'], 'high')
                project = [layer for layer in probe['layers'] if layer['name']['type'] == 'project']
                self.assertTrue(project); self.assertFalse(project[0].get('disabledReason'))
                listed = json.dumps(probe['fixtureSkills'])
                self.assertIn(str(f.checkout / '.agents/skills/repo-refresh/SKILL.md'), listed)
                self.assertIn('NATIVE_DESCRIPTION_B', listed)
                self.assertNotIn('NATIVE_DESCRIPTION_A', listed); self.assertNotIn('NATIVE_DESCRIPTION_LOCAL', listed)
                f.provider.discussions[0]['body'] += ' $repo-refresh NATIVE_USER_SECOND'
                second = native_event(f.wait('native-event'))
                self.assertEqual(second['method'], 'turn/completed'); print('NATIVE_COMPLETED ' + json.dumps(second), flush=True)
                self.settled(f)
                self.assertEqual(second['status'], 'completed')
                self.assertEqual(second['thread'], session)
                self.assertEqual(json.loads(f.state.read_text())['discussions']['D1']['sessionId'], session)
                self.assertEqual(len(f.provider.model_requests), 2)
                # Each turn states the reasoning its checkout's project configuration selects at that turn.
                self.assertEqual([request['body']['reasoning']['effort'] for request in f.provider.model_requests], ['low', 'high'])
                request_b = json.dumps(f.provider.model_requests[1]['body'])
                for text in ('NATIVE_USER_FIRST', 'Harmless fixture assistant 1.', 'NATIVE_USER_SECOND', 'NATIVE_AGENTS_B', 'NATIVE_SKILL_BODY_B'):
                    self.assertIn(text, request_b)
                for text in ('NATIVE_AGENTS_LOCAL', 'NATIVE_SKILL_BODY_LOCAL', 'NATIVE_DESCRIPTION_LOCAL'):
                    self.assertNotIn(text, request_b)
                self.assertLess(request_b.index('NATIVE_AGENTS_A'), request_b.index('NATIVE_AGENTS_B'))
                self.assertIn('These AGENTS.md instructions replace all previously provided AGENTS.md instructions.', request_b)
                self.assertEqual(json.loads(f.owner.read_text())['commit'], target)
                self.assertEqual(json.loads(f.journal.read_text())['active'], {})
            final = rollout.read_bytes()
            self.assertTrue(final.startswith(prefix))
            self.assertIn(b'NATIVE_USER_SECOND', final)
            self.assertIn(b'Harmless fixture assistant 2.', final)
            self.assertFalse(any(item[0] == 'POST' for item in f.provider.denied))
            print('NATIVE_INTEGRATION ' + json.dumps(dict(session=session, commit=target, requests=f.provider.model_requests,
                  completions=[first, second], config=probe['config'], skills=probe['fixtureSkills'], denied=f.provider.denied,
                  network=f.provider.requests, prefixLength=len(prefix), prefixSha256=hashlib.sha256(prefix).hexdigest(),
                  finalPrefixSha256=hashlib.sha256(final[:len(prefix)]).hexdigest(), rolloutSha256=hashlib.sha256(final).hexdigest())), flush=True)

    def test_pinned_native_active_turn_holds_input_until_its_idle_report(self):
        with self.fixture(responses=True) as f:
            config = f.directory / 'home/config.toml'
            config.write_text(config.read_text() + FIXTURE_MODEL)
            hold = threading.Event(); f.provider.response_holds[1] = hold
            try:
                with self.controller(f):
                    f.provider.discussions = [dict(self.discussion(1), reactionGroups=[])]
                    f.wait('native-accepted')
                    f.wait('poll-completed success=True')
                    self.eyes(f, ['D1'])
                    session = json.loads(f.state.read_text())['discussions']['D1']['sessionId']
                    # The first model response is held, so the real native turn stays active.
                    f.provider.comments = {'D1': [self.comment(1, 'alice', 'NATIVE_WAITING_INPUT')]}
                    f.wait('poll-completed success=True'); f.wait('poll-completed success=True')
                    self.assertEqual(f.provider.reactions, ['D1'], 'Input was accepted into the active native turn')
                    self.assertTrue(any('session-status session=' + session + ' status=Active' in line for line in f.lines),
                                    'Adapter did not observe the native active report')
                    self.assertTrue(any('input-waiting' in line and 'sources=C1' in line for line in f.lines))
                    self.assertFalse(any('session-status session=' + session + ' status=Idle' in line for line in f.lines))
                    hold.set()
                    first = native_event(f.wait('native-event'))
                    f.wait('session-status session=' + session + ' status=Active')  # Native report for the dispatched turn.
                    second = native_event(f.wait('native-event'))
                    self.eyes(f, ['D1', 'C1'])
            finally: hold.set()
            for completed in (first, second):
                self.assertEqual((completed['method'], completed['thread'], completed['status']),
                                 ('turn/completed', session, 'completed'))
            self.assertNotEqual(first['turn'], second['turn'], 'Waiting input steered the first turn')
            self.assertEqual(len(f.provider.model_requests), 2)
            self.assertNotIn('NATIVE_WAITING_INPUT', json.dumps(f.provider.model_requests[0]['body']))
            self.assertIn('NATIVE_WAITING_INPUT', json.dumps(f.provider.model_requests[1]['body']))
            self.assertEqual(f.provider.reactions, ['D1', 'C1'])
            print('NATIVE_IDLE_DISPATCH session=' + session + ' turns=' + first['turn'] + ',' + second['turn']
                  + ' statuses=' + json.dumps([line.split(' ', 2)[2].strip() for line in f.lines if 'session-status' in line]), flush=True)

    def test_pinned_native_turns_use_the_labelled_model_and_reasoning_and_label_removal_restores_the_defaults(self):
        # Pinned native keeps a turn/start model and effort for later turns, so only an explicit default restores it.
        with self.fixture(responses=True) as f:
            config = f.directory / 'home/config.toml'
            config.write_text(config.read_text() + FIXTURE_MODEL)
            f.provider.labels = {'D1': ['repoagent:luna6:xhigh']}
            with self.controller(f):
                f.provider.discussions = [self.discussion(1)]
                first = native_event(f.wait('native-event'))
                f.provider.labels = {}
                f.wait('poll-completed success=True'); f.wait('poll-completed success=True')
                self.assertEqual(len(f.provider.model_requests), 1, 'A label-only change invoked the agent')
                f.provider.discussions[0]['body'] += ' after the label was removed'
                second = native_event(f.wait('native-event'))
        self.assertEqual([(event['method'], event['status']) for event in (first, second)], [('turn/completed', 'completed')] * 2)
        self.assertEqual([(request['body']['model'], request['body']['reasoning']['effort']) for request in f.provider.model_requests],
                         [('gpt-6-luna', 'xhigh'), ('gpt-6.1-sol', 'low')])
        print('NATIVE_MODEL_SELECTION ' + json.dumps([dict(model=request['body']['model'], reasoning=request['body']['reasoning'])
                                                       for request in f.provider.model_requests]), flush=True)

    def test_genuine_sessions_discover_packaged_skills_and_receive_orientation_when_started_or_resumed(self):
        packaged = Path(self.controller_command[-1]).parent  # The controller's own installed skill directories.
        with self.fixture(responses=True) as f:
            f.provider.model_request_limit = 3
            config = f.directory / 'home/config.toml'
            config.write_text(config.read_text() + FIXTURE_MODEL)
            def completed():
                event = native_event(f.wait('native-event'))
                self.assertEqual((event['method'], event['status']), ('turn/completed', 'completed'), event)
            with self.controller(f):
                f.provider.discussions = [self.discussion(1)]
                completed()
                f.provider.discussions[0]['body'] += ' same-process continuation'
                completed()
            with self.controller(f):
                f.provider.discussions[0]['body'] += ' after restart'
                completed()
        self.assertEqual(len(f.provider.model_requests), 3, f.provider.denied)
        def texts(request):
            return [part['text'] for item in request['body']['input'] for part in item.get('content') or [] if 'text' in part]
        def supplied(request):
            # Native wraps each supplied SKILL.md exactly as read; history repeats earlier turns' blocks.
            blocks = [re.fullmatch(r'<skill>\n<name>(.+)</name>\n<path>(.+)</path>\n(.*)\n</skill>', text, re.DOTALL) for text in texts(request)]
            blocks = [block for block in blocks if block]
            for name, path, content in (block.groups() for block in blocks):
                if name in ('github-discussion', 'repoagent-orientation'):
                    self.assertEqual(Path(path), packaged / name / 'SKILL.md')
                    self.assertEqual(content, (ROOT / '.agents/skills' / name / 'SKILL.md').read_text())
            return [block[1] for block in blocks]
        started, continued, resumed = map(supplied, f.provider.model_requests)
        self.assertEqual((started.count('repoagent-orientation'), continued.count('repoagent-orientation'), resumed.count('repoagent-orientation')), (1, 1, 2))
        self.assertEqual((started.count('github-discussion'), continued.count('github-discussion'), resumed.count('github-discussion')), (1, 2, 3))
        catalog = '\n'.join(text for text in texts(f.provider.model_requests[0]) if '### Available skills' in text)
        for name in ('github-discussion', 'repoagent-orientation'):
            self.assertRegex(catalog, r'(?m)^- ' + name + ': ')
        self.assertTrue(any('$repoagent-orientation' in text and '$github-discussion' in text for text in texts(f.provider.model_requests[0])))
        print('ORIENTATION_INTEGRATION ' + json.dumps(dict(packaged=str(packaged), started=started, continued=continued, resumed=resumed)), flush=True)

    def test_signed_push_is_durable_idle_safe_and_scoped_to_managed_clone(self):
        for clone in (True, False):
            with self.subTest(clone=clone), self.fixture(protocol=clone, webhooks=True, clone=clone) as f:
                with socket.socket() as listener:
                    listener.bind(('127.0.0.1', 0)); port = listener.getsockname()[1]
                f.command += ['--webhook-url', 'https://fixture.invalid', '--listen-port', str(port), '--reconcile-interval', '3600']
                if clone: f.provider.discussions = [self.discussion(1)]
                with self.controller(f, 'mode=webhook-fixed'):
                    settings = json.loads(Path(str(f.state) + '.webhook/hook.json').read_text())
                    def deliver(payload, signature=True, event='push', delivery=1):
                        body = json.dumps(payload).encode()
                        digest = hmac.new(settings['secret'].encode(), body, hashlib.sha256).hexdigest()
                        with closing(http.client.HTTPConnection('127.0.0.1', port, timeout=10)) as connection:
                            connection.request('POST', '/github/webhook', body, {'X-GitHub-Event': event,
                                'X-GitHub-Delivery': '00000000-0000-4000-8000-00000000000' + str(delivery),
                                'X-Hub-Signature-256': 'sha256=' + (digest if signature else '0' * 64)})
                            response = connection.getresponse(); response.read(); return response.status
                    payload = dict(repository=dict(full_name='owner/project', id=345), ref='refs/heads/main', after='0' * 40)
                    if not clone:
                        f.wait('reconciliation-completed discussion=all success=True')
                        self.assertNotIn('push', f.provider.hooks[0]['events'])
                        self.assertEqual(deliver(payload), 204); self.assertFalse(f.journal.exists())
                        continue
                    request = self.rpc(f, 'thread/start')
                    self.reply(f, request, dict(cwd=str(f.checkout), thread=dict(id='S1', cwd=str(f.checkout))))
                    request = self.rpc(f, 'turn/start'); self.reply(f, request, dict(turn=dict(id='T1')))
                    f.wait('reconciliation-completed discussion=all success=True')
                    initial = f.git('-C', f.checkout, 'rev-parse', 'HEAD'); requests = len(f.provider.git_tokens)
                    target = self.publish(f); before = json.loads(f.journal.read_text())['requested']
                    print('SIGNED_PUSH_READY held=S1/T1 originB=' + target + ' headA=' + initial, flush=True)
                    self.assertEqual(deliver(payload), 202, 'Valid default push must persist a refresh request before acceptance')
                    self.assertIn('push', f.provider.hooks[0]['events'])
                    requested = json.loads(f.journal.read_text())['requested']; self.assertGreater(requested, before)
                    for body, valid, status in [(payload, False, 401), (dict(payload, repository=dict(full_name='other/project', id=345)), True, 403),
                            (dict(payload, repository=dict(full_name='owner/project', id=999)), True, 403),
                            (dict(payload, ref=123), True, 400), (dict(payload, ref='refs/heads/other'), True, 204)]:
                        self.assertEqual(deliver(body, valid), status)
                        self.assertEqual(json.loads(f.journal.read_text())['requested'], requested)
                    for delivery, sha in enumerate((target, initial), 1):
                        self.assertEqual(deliver(dict(payload, after=sha), delivery=delivery), 202)
                        self.assertGreater(json.loads(f.journal.read_text())['requested'], requested)
                        requested = json.loads(f.journal.read_text())['requested']
                    self.assertEqual(f.git('-C', f.checkout, 'rev-parse', 'HEAD'), initial)
                    self.assertEqual(len(f.provider.git_tokens), requests)
                    (f.directory / 'hold-reload').touch(); self.terminal(f, 'S1', 'T1')
                    initialize = self.rpc(f, 'initialize'); applying = json.loads(f.journal.read_text())['applying']
                    self.assertEqual(f.git('-C', f.checkout, 'rev-parse', 'HEAD'), target)
                    self.assertEqual(json.loads(f.owner.read_text())['commit'], target)
                    self.assertEqual(deliver(payload), 202)
                    journal = json.loads(f.journal.read_text())
                    self.assertEqual(journal['applying'], applying); self.assertGreater(journal['requested'], applying['generation'])
                    requested = journal['requested']; (f.directory / 'hold-reload').unlink()
                    f.commands.write(json.dumps(dict(fixtureForward=initialize)) + '\n'); f.commands.flush()
                    f.provider.discussions[0]['body'] += ' after reload'
                    notice = dict(payload, action='edited', discussion=dict(number=1))
                    self.assertEqual(deliver(notice, event='discussion'), 202)
                    request = self.rpc(f, 'thread/resume'); self.reply(f, request, dict(thread=dict(id='S1', cwd=str(f.checkout)), cwd=str(f.checkout)))
                    request = self.rpc(f, 'turn/start'); self.reply(f, request, dict(turn=dict(id='T2')))
                    f.wait('webhook-observed discussion=1 success=True')
                    journal = json.loads(f.journal.read_text()); self.assertNotIn('applying', journal)
                    self.assertEqual(journal['completed'], requested); self.assertFalse(journal['blocked'])
                    durable = f.journal.read_bytes(); Path(str(f.journal) + '.tmp').mkdir()
                    self.assertEqual(deliver(payload), 500); self.assertEqual(f.journal.read_bytes(), durable)
                    f.provider.discussions[0]['body'] += ' after fault'
                    self.assertEqual(deliver(notice, event='discussion'), 202)
                    # T2 is still active, so the input waits; at idle the faulted journal blocks its admission.
                    self.assertIsNotNone(f.wait_or_native('input-waiting'), 'Input reached the active turn')
                    self.terminal(f, 'S1', 'T2'); f.wait('dispatch-error')
                    self.assertFalse(select.select([f.responses], [], [], 0)[0], 'Faulted journal admitted native input')
                    self.assertEqual(f.git('-C', f.checkout, 'rev-parse', 'HEAD'), target)

    @contextmanager
    def durable_queue(self, f):
        host = ROOT / 'tests/installers/QueueHost'
        subprocess.run(['dotnet', 'build', str(host), '-c', 'Release', '--nologo', '-v', 'quiet'],
                       check=True, capture_output=True, text=True, timeout=120)
        image = 'mcr.microsoft.com/azure-storage/azurite@sha256:830430c1da1a2d537e08f3e6764dd1f5ae00cf0346bcaf625b968ec3f0971fd5'
        container = subprocess.check_output(['docker', 'run', '--detach', '--publish', '127.0.0.1::10001', image,
            'azurite-queue', '--queueHost', '0.0.0.0', '--skipApiVersionCheck', '--location', '/data'], text=True).strip()
        try:
            address = subprocess.check_output(['docker', 'port', container, '10001/tcp'], text=True).strip()
            hostname, port = address.rsplit(':', 1)
            deadline = time.monotonic() + 30
            while True:
                try:
                    # Docker's published TCP port can accept before the emulator has bound its socket.
                    logs = subprocess.check_output(['docker', 'logs', container], text=True, stderr=subprocess.STDOUT)
                    if 'Azurite Queue service successfully listens' in logs: break
                    if time.monotonic() >= deadline: self.fail('Owned Azurite queue did not become ready: ' + logs)
                    time.sleep(.1)
                except OSError:
                    if time.monotonic() >= deadline: self.fail('Owned Azurite queue did not listen')
                    time.sleep(.1)
            f.environment['CONTROLLER_TEST_QUEUE_ENDPOINT'] = 'http://' + address + '/devstoreaccount1/controller-work'
            command = ['dotnet', str(host / 'bin/Release/net11.0/QueueHost.dll')]
            def queue_operation(operation, *arguments):
                result = subprocess.run(command + ['queue', operation, *arguments], env=f.environment,
                    capture_output=True, text=True, timeout=10)
                self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
                return int(result.stdout.strip()) if operation in ('count', 'peek') else None
            queue_operation('create')
            f.command = command + f.command[len(self.controller_command):] + ['--installation-id', '234', '--repository-id', '345',
                '--azure-work-queue', 'https://fixturequeue.queue.core.windows.net/controller-work']
            with socket.socket() as listener:
                listener.bind(('127.0.0.1', 0)); f.webhook_port = listener.getsockname()[1]
            f.command += ['--webhook-url', 'https://fixture.invalid', '--listen-port', str(f.webhook_port)]
            yield queue_operation
        finally:
            subprocess.run(['docker', 'rm', '--force', container], check=True, capture_output=True, text=True, timeout=15)

    def durable_delivery(self, f, signature=True, repository_id=345, own=False, payload=None, event='discussion_comment'):
        settings = json.loads(Path(str(f.state) + '.webhook/hook.json').read_text())
        body = json.dumps(payload if payload is not None else dict(repository=dict(full_name='owner/project', id=repository_id),
            action='edited', discussion=dict(number=1), sender=dict(login='test-app[bot]' if own else 'participant', type='Bot' if own else 'User'))).encode()
        digest = hmac.new(settings['secret'].encode(), body, hashlib.sha256).hexdigest()
        with closing(http.client.HTTPConnection('127.0.0.1', f.webhook_port, timeout=10)) as connection:
            connection.request('POST', '/github/webhook', body, {'X-GitHub-Event': event,
                'X-GitHub-Delivery': '00000000-0000-4000-8000-000000000001',
                'X-Hub-Signature-256': 'sha256=' + (digest if signature else '0' * 64)})
            response = connection.getresponse(); response.read(); return response.status

    def test_invalid_durable_installation_leaves_no_startup_work(self):
        with self.fixture(webhooks=True) as f, self.durable_queue(f) as queue_operation:
            f.command[f.command.index('--installation-id') + 1] = '999'
            result = subprocess.run(f.command, env=f.environment, capture_output=True, text=True, timeout=15)
            self.assertEqual(result.returncode, 1)
            self.assertIn('CLI installation differs from enrollment', result.stdout + result.stderr)
            self.assertEqual(queue_operation('count'), 0, 'Invalid installation persisted foreign nonexpiring startup work')
            self.assertEqual(f.provider.requests, [], 'Local binding refusal must precede broker or GitHub contact')

    def test_durable_pending_refresh_recovers_without_another_delivery(self):
        with self.fixture(webhooks=True) as f, self.durable_queue(f) as queue_operation:
            with self.controller(f, 'mode=webhook-fixed'):
                f.wait('durable-webhook-observed hint=reconcile')
                deadline = time.monotonic() + 15
                while queue_operation('count'):
                    if time.monotonic() >= deadline: self.fail('Initial reconciliation did not release its queue hold')
                    time.sleep(.1)
                f.provider.ref_status = 503
                target = self.publish(f)
                payload = dict(repository=dict(full_name='owner/project', id=345), ref='refs/heads/main', after=target)
                self.assertEqual(self.durable_delivery(f, payload=payload, event='push'), 202)
                f.wait('refresh-pending')
                pending = json.loads(f.journal.read_text())
                self.assertGreater(pending['requested'], pending['completed'])
                self.assertFalse(pending['blocked'])
                self.assertNotIn('applying', pending)
                self.assertGreater(queue_operation('count'), 0)
                f.provider.ref_status = None
                deadline = time.monotonic() + 20
                while json.loads(f.owner.read_text())['commit'] != target:
                    if time.monotonic() >= deadline:
                        self.assertEqual(json.loads(f.owner.read_text())['commit'], target,
                                         'Recovered provider did not refresh retained work without another delivery')
                    time.sleep(.1)
                deadline = time.monotonic() + 15
                while queue_operation('count'):
                    if time.monotonic() >= deadline: self.fail('Recovered refresh did not release its retained work')
                    time.sleep(.1)
                completed = json.loads(f.journal.read_text())
                self.assertEqual(completed['requested'], pending['requested'], 'Retry created an autonomous refresh request')
                self.assertEqual(completed['completed'], completed['requested'])
                self.assertEqual(f.git('-C', f.checkout, 'rev-parse', 'HEAD'), target)

    def test_durable_webhooks_retain_unfinished_native_work_and_hold_additional_input_until_idle(self):
        with self.fixture(protocol=True, webhooks=True) as f, self.durable_queue(f) as queue_operation:
            with self.controller(f, 'mode=webhook-fixed'):
                f.wait('durable-webhook-observed hint=reconcile')
                def eventually_count(expected):
                    deadline = time.monotonic() + 15
                    while True:
                        actual = queue_operation('count')
                        if actual == expected: return
                        if time.monotonic() >= deadline: self.fail(f'Queue count {actual}, expected {expected}')
                        time.sleep(.1)
                eventually_count(0)
                def deliver(**settings): return self.durable_delivery(f, **settings)
                self.assertEqual(deliver(signature=False), 401)
                self.assertEqual(deliver(repository_id=999), 403)
                self.assertEqual(deliver(own=True), 204)
                self.assertEqual(queue_operation('count'), 0)
                f.provider.discussions = [self.discussion(1)]
                self.assertEqual(deliver(), 202)
                self.assertGreater(queue_operation('count'), 0, 'HTTP202 must have durable work before native acknowledgment')
                request = self.rpc(f, 'thread/start')
                self.reply(f, request, dict(cwd=str(f.checkout), thread=dict(id='S1', cwd=str(f.checkout))))
                request = self.rpc(f, 'turn/start'); self.reply(f, request, dict(turn=dict(id='T1')))
                f.wait('durable-webhook-observed hint=discussion:1')
                self.assertGreater(queue_operation('count'), 0, 'Native acknowledgment cannot release unfinished work')
                self.assertEqual(queue_operation('peek'), 0, 'Invisible received work must remain retained')
                f.provider.discussions[0]['body'] += ' additional input'
                self.assertEqual(deliver(), 202)
                self.assertIsNotNone(f.wait_or_native('durable-webhook-observed hint=discussion:1'), 'Additional input reached the active turn')
                self.assertGreater(queue_operation('count'), 0, 'Waiting input must retain its durable work')
                self.assertEqual(deliver(), 202)  # Unchanged content must not repeat waiting input.
                f.wait('durable-webhook-observed hint=discussion:1')
                self.assertFalse(select.select([f.responses], [], [], 0)[0], 'Duplicate delivery repeated native input')
                self.terminal(f, 'S1', 'T1')
                request = self.rpc(f, 'turn/start')
                self.assertIn('additional input', self.dispatched(request)['changes'][0]['subject']['body'])
                self.reply(f, request, dict(turn=dict(id='T2')))
                f.wait('forwarded')
                self.assertGreater(queue_operation('count'), 0, 'Dispatched input retains durable work until its turn completes')
                self.terminal(f, 'S1', 'T2')
                eventually_count(0)
                self.assertEqual(json.loads(f.journal.read_text())['active'], {})
                self.assertFalse(json.loads(f.journal.read_text())['blocked'])
            self.assertFalse(any(hook.get('active') is False for hook in f.provider.hooks),
                             'Ordinary replica shutdown must preserve the signed wake hook')
            option = f.command.index('--azure-work-queue')
            del f.command[option:option + 2]
            f.run(f.command + ['--disable-webhook'])
            self.assertIs(f.provider.hooks[-1]['active'], False, 'Explicit stopped-instance disable must confirm hook removal')

    def test_lost_durable_work_lease_stops_native_children_and_preserves_unknown_turn(self):
        with self.fixture(protocol=True, webhooks=True) as f, self.durable_queue(f) as queue_operation:
            f.provider.discussions = [self.discussion(1)]
            pids = f.directory / 'native-pids.json'
            f.environment['MATRIX_PIDS'] = str(pids)
            with self.controller(f, 'mode=webhook-fixed'):
                request = self.rpc(f, 'thread/start')
                self.reply(f, request, dict(cwd=str(f.checkout), thread=dict(id='S1', cwd=str(f.checkout))))
                request = self.rpc(f, 'turn/start'); self.reply(f, request, dict(turn=dict(id='T1')))
                f.wait('durable-webhook-observed hint=reconcile')
                proof = json.loads(pids.read_text())
                def running(pid):
                    try: return Path(f'/proc/{pid}/stat').read_text().split(') ', 1)[1][0] != 'Z'
                    except FileNotFoundError: return False
                self.assertTrue(all(running(pid) for pid in proof.values()))
                self.assertGreater(queue_operation('count'), 0)
                saved = f.state.read_bytes()
                queue_operation('clear')  # Real service removes the receipt; the next renewal must fail.
                self.assertEqual(f.process.wait(timeout=25), 1)
                self.assertTrue(json.loads(f.journal.read_text())['active'])
                self.assertTrue(all(not running(pid) for pid in proof.values()), 'A failed queue lease left native children running')
                self.assertEqual(f.state.read_bytes(), saved)
            resumed = subprocess.run(f.command, env=f.environment, capture_output=True, text=True, timeout=20)
            self.assertEqual(resumed.returncode, 1, resumed.stdout + resumed.stderr)
            self.assertIn('uncertain', resumed.stdout)
            self.assertEqual(f.state.read_bytes(), saved)
            self.assertFalse(select.select([f.responses], [], [], 0)[0], 'Restart replayed an outcome-unknown native input')

    def test_foreign_durable_work_is_preserved_without_native_adoption(self):
        with self.fixture(protocol=True, webhooks=True) as f, self.durable_queue(f) as queue_operation:
            queue_operation('foreign')
            with self.controller(f, 'mode=webhook-fixed'):
                self.assertEqual(f.process.wait(timeout=20), 1)
                self.assertGreater(queue_operation('count'), 0, 'A foreign binding must remain available for manual inspection')
                self.assertFalse(select.select([f.responses], [], [], 0)[0], 'Foreign queued work admitted native input')

    def test_push_at_durable_idle_commit_retains_its_hold_before_refresh(self):
        with self.fixture(webhooks=True) as f, self.durable_queue(f) as queue_operation:
            queue_operation('idle-race')

    def test_uncertain_durable_send_stops_an_active_turn_after_the_service_committed(self):
        with self.fixture(protocol=True, webhooks=True) as f, self.durable_queue(f) as queue_operation:
            f.provider.discussions = [self.discussion(1)]
            marker = f.directory / 'drop-next-queue-send'
            f.environment['CONTROLLER_TEST_DROP_QUEUE_SEND'] = str(marker)
            with self.controller(f, 'mode=webhook-fixed'):
                request = self.rpc(f, 'thread/start')
                self.reply(f, request, dict(cwd=str(f.checkout), thread=dict(id='S1', cwd=str(f.checkout))))
                request = self.rpc(f, 'turn/start'); self.reply(f, request, dict(turn=dict(id='T1')))
                f.wait('durable-webhook-observed hint=reconcile')
                saved = f.state.read_bytes()
                marker.touch()
                try: self.assertNotEqual(self.durable_delivery(f), 202)
                except (http.client.RemoteDisconnected, ConnectionResetError): pass  # Runtime closed during fail-stop.
                self.assertFalse(marker.exists(), 'The fixture never forwarded and lost the committed send response')
                self.assertGreaterEqual(queue_operation('count'), 2, 'Azurite must contain the accepted hint and unfinished startup work')
                self.assertEqual(f.process.wait(timeout=10), 1, 'An ambiguous enqueue left native work running')
                self.assertTrue(json.loads(f.journal.read_text())['active'])
                self.assertEqual(f.state.read_bytes(), saved)

    def discussion_event(self, number, action='edited'):
        return dict(repository=dict(full_name='owner/project', id=345), action=action,
                    discussion=dict(number=number), sender=dict(login='participant', type='User'))

    def drained(self, queue_operation):
        deadline = time.monotonic() + 20
        while queue_operation('count'):
            if time.monotonic() >= deadline: self.fail('Durable work was not completed')
            time.sleep(.1)

    def start_sessions(self, f, *numbers):
        for number in numbers:
            request = self.rpc(f, 'thread/start')
            self.reply(f, request, dict(cwd=str(f.checkout), thread=dict(id='S' + str(number), cwd=str(f.checkout))))
            request = self.rpc(f, 'turn/start'); self.reply(f, request, dict(turn=dict(id='T' + str(number))))
        f.wait('durable-webhook-observed hint=reconcile')
        for number in numbers: self.terminal(f, 'S' + str(number), 'T' + str(number))

    def test_deleted_discussion_is_forgotten_without_stopping_live_durable_work(self):
        with self.fixture(protocol=True, webhooks=True) as f, self.durable_queue(f) as queue_operation:
            f.provider.discussions = [self.discussion(1), self.discussion(2)]
            with self.controller(f, 'mode=webhook-fixed'):
                self.start_sessions(f, 1, 2)
                self.drained(queue_operation)
            saved = json.loads(f.state.read_text())['discussions']
            self.assertEqual(saved['D1']['sessionId'], 'S1')
            # Deployed incident: #1 is deleted while its state and retained hint survive, with live work queued behind it.
            del f.provider.discussions[0]
            queue_operation('hint', '1'); queue_operation('hint', '2')
            with self.controller(f, 'mode=webhook-fixed'):
                self.assertIn('session=S1', f.wait('discussion-forgotten'))
                f.wait('durable-webhook-observed hint=discussion:1')
                f.wait('durable-webhook-observed hint=discussion:2')
                discussions = json.loads(f.state.read_text())['discussions']
                self.assertNotIn('D1', discussions, 'Confirmed deletion must forget the Discussion state')
                self.assertEqual(discussions['D2'], saved['D2'], 'Unrelated session and checkpoints must survive')
                # A duplicate deletion notification ahead of new live input is harmless.
                f.provider.discussions[0]['body'] += ' behind a stale hint'
                self.assertEqual(self.durable_delivery(f, payload=self.discussion_event(1, 'deleted'), event='discussion'), 202)
                self.assertEqual(self.durable_delivery(f, payload=self.discussion_event(2), event='discussion'), 202)
                request = self.rpc(f, 'thread/resume'); self.assertEqual(request['params']['threadId'], 'S2')
                self.reply(f, request, dict(thread=dict(id='S2', cwd=str(f.checkout)), cwd=str(f.checkout)))
                request = self.rpc(f, 'turn/start'); self.assertEqual(request['params']['threadId'], 'S2')
                self.reply(f, request, dict(turn=dict(id='T3')))
                f.wait('durable-webhook-observed hint=discussion:1')
                f.wait('durable-webhook-observed hint=discussion:2')
                continued = json.loads(f.state.read_text())['discussions']
                self.assertEqual(sorted(continued), ['D2'])
                self.assertEqual(continued['D2']['sessionId'], 'S2')
                self.assertEqual(continued['D2']['sources']['D2']['content']['body'], f.provider.discussions[0]['body'])
                self.terminal(f, 'S2', 'T3')
                self.drained(queue_operation)
                self.assertIsNone(f.process.poll(), 'Stale work stopped the shared worker')
            lookups = len(f.provider.lookups)
            with self.controller(f, 'mode=webhook-fixed'):
                f.wait('durable-webhook-observed hint=reconcile')
                self.drained(queue_operation)
                self.assertIsNone(f.process.poll())
            self.assertEqual(len(f.provider.lookups), lookups, 'Obsolete work was retried after restart')
            self.assertEqual(sorted(json.loads(f.state.read_text())['discussions']), ['D2'])
            self.assertFalse(select.select([f.responses], [], [], 0)[0], 'A deleted Discussion received native input')

    def test_unconfirmed_discussion_lookup_failures_retain_work_and_back_off_behind_live_work(self):
        missing = 'Could not resolve to a Discussion with the number of 1.'
        failures = [  # One lookup each; none is GitHub's typed deletion of this Discussion.
            (200, {'data': {'repository': None}, 'errors': [{'type': 'NOT_FOUND', 'path': ['repository'],
                   'message': "Could not resolve to a Repository with the name 'owner/project'."}]}),
            (200, {'data': {'repository': {'discussion': None}}, 'errors': [{'type': 'FORBIDDEN',
                   'path': ['repository', 'discussion'], 'message': 'Resource not accessible by integration'}]}),
            (200, {'errors': [{'type': 'RATE_LIMITED', 'message': 'API rate limit exceeded for installation.'}]}),
            (200, {'data': {'repository': {'discussion': None}}, 'errors': [{'path': ['repository', 'discussion'], 'message': missing}]}),
            (200, {'data': {'repository': {'discussion': None}}}),
            (502, {'message': 'Server Error'}),
        ]
        with self.fixture(protocol=True, webhooks=True) as f, self.durable_queue(f) as queue_operation:
            f.provider.discussions = [self.discussion(1), self.discussion(2)]
            with self.controller(f, 'mode=webhook-fixed'):
                self.start_sessions(f, 1, 2)
                self.drained(queue_operation)
                retained = json.loads(f.state.read_text())['discussions']['D1']
                f.provider.lookup_faults = {1: list(failures)}
                for _ in failures:
                    self.assertEqual(self.durable_delivery(f, payload=self.discussion_event(1), event='discussion'), 202)
                f.provider.discussions[1]['body'] += ' behind failing work'
                self.assertEqual(self.durable_delivery(f, payload=self.discussion_event(2), event='discussion'), 202)
                request = self.rpc(f, 'turn/start'); self.assertEqual(request['params']['threadId'], 'S2')
                self.reply(f, request, dict(turn=dict(id='T3')))
                f.wait('durable-webhook-observed hint=discussion:2')
                self.assertIsNone(f.process.poll(), 'A failed observation stopped the shared worker')
                self.assertEqual(f.provider.lookup_faults[1], [], 'Every failure class must be observed before live work')
                self.assertEqual(json.loads(f.state.read_text())['discussions']['D1'], retained,
                                 'An unconfirmed failure was mistaken for deletion')
                self.assertGreater(queue_operation('count'), 0, 'Failed work must remain durable')
                self.terminal(f, 'S2', 'T3')
                for _ in failures: f.wait('durable-webhook-observed hint=discussion:1', timeout=30)
                self.drained(queue_operation)
                self.assertIsNone(f.process.poll())
            lookups = [(at, failed) for number, at, failed in f.provider.lookups if number == 1]
            self.assertEqual([failed for _, failed in lookups], [True] * len(failures) + [False] * len(failures))
            first_failure, first_retry = lookups[0][0], lookups[len(failures)][0]
            print('LOOKUP_BACKOFF first-retry-after=%.1fs' % (first_retry - first_failure), flush=True)
            self.assertGreaterEqual(first_retry - first_failure, 4, 'Failed work was retried without backoff')
            self.assertEqual(json.loads(f.state.read_text())['discussions']['D1'], retained)
            self.assertFalse(select.select([f.responses], [], [], 0)[0], 'Retried unchanged work repeated native input')

    def test_deleted_discussion_with_waiting_input_and_a_running_turn_never_dispatches_and_its_work_completes(self):
        with self.fixture(protocol=True, webhooks=True) as f, self.durable_queue(f) as queue_operation:
            f.provider.discussions = [self.discussion(1), self.discussion(2)]
            with self.controller(f, 'mode=webhook-fixed'):
                for number in (1, 2):
                    request = self.rpc(f, 'thread/start')
                    self.reply(f, request, dict(cwd=str(f.checkout), thread=dict(id='S' + str(number), cwd=str(f.checkout))))
                    request = self.rpc(f, 'turn/start'); self.reply(f, request, dict(turn=dict(id='T' + str(number))))
                f.wait('durable-webhook-observed hint=reconcile')
                self.terminal(f, 'S2', 'T2')
                # T1 keeps D1's active slot; a D1 edit waits in the controller behind it.
                f.provider.discussions[0]['body'] += ' while T1 runs'
                self.assertEqual(self.durable_delivery(f, payload=self.discussion_event(1), event='discussion'), 202)
                f.wait('input-waiting'); f.wait('durable-webhook-observed hint=discussion:1')
                saved = json.loads(f.state.read_text())['discussions']
                self.assertEqual(saved['D1']['running']['turn'], 'T1')
                self.assertEqual(len(saved['D1']['waiting']['inputs']), 1)
                del f.provider.discussions[0]
                self.assertEqual(self.durable_delivery(f, payload=self.discussion_event(1, 'deleted'), event='discussion'), 202)
                self.assertIn('session=S1', f.wait('discussion-forgotten'))
                f.wait('durable-webhook-observed hint=discussion:1')
                discussions = json.loads(f.state.read_text())['discussions']
                self.assertEqual(sorted(discussions), ['D2'], 'Waiting input, running and checkpoint state of D1 must be forgotten')
                self.assertEqual(discussions['D2'], saved['D2'], 'Unrelated session and checkpoints must survive')
                self.assertGreater(queue_operation('count'), 0, 'Work was completed while the native turn still ran')
                # The native ends the turn: its slot frees, the forgotten input is never sent and retained work completes.
                self.terminal(f, 'S1', 'T1')
                self.drained(queue_operation)
                self.assertFalse(select.select([f.responses], [], [], 0)[0], 'Forgotten input reached the native')
                f.provider.discussions[0]['body'] += ' after the deletion'
                self.assertEqual(self.durable_delivery(f, payload=self.discussion_event(2), event='discussion'), 202)
                request = self.rpc(f, 'turn/start'); self.assertEqual(request['params']['threadId'], 'S2')
                self.reply(f, request, dict(turn=dict(id='T3')))
                f.wait('durable-webhook-observed hint=discussion:2')
                self.terminal(f, 'S2', 'T3')
                self.drained(queue_operation)
                self.assertIsNone(f.process.poll(), 'Forgetting stopped the shared worker')


if __name__ == '__main__':
    unittest.main()
