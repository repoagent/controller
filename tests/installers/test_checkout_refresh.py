"""Real managed-checkout startup freshness; empty polls create no native turns."""
from contextlib import closing, contextmanager
import hashlib
import hmac
import http.client
import json
import os
from pathlib import Path
import queue
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
                line = events.get(timeout=max(0.001, deadline-time.monotonic()))
                if line is None: self.fail('Controller closed before ' + fragment + '\n' + ''.join(lines))
                if fragment in line: return line
        f.process, f.wait = process, wait
        try:
            wait(ready)
            yield
        finally:
            if process.poll() is None: process.send_signal(signal.SIGINT)
            try: process.wait(timeout=10)
            except subprocess.TimeoutExpired: process.kill(); process.wait(timeout=5)
            reader.join(timeout=5); process.stdout.close()
            print('MATRIX_CONTROLLER exit=' + str(process.returncode) + ' ' + ''.join(line for line in lines if any(s in line for s in ('checkout-refreshed', 'refresh-pending', 'refresh-blocked', 'manual-recovery'))), flush=True)

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

    def test_periodic_refresh_preserves_local_work_and_refuses_unsafe_remote_changes(self):
        for case in ('ignored', 'dirty', 'staged', 'untracked', 'commit', 'operation', 'ignored-file', 'ignored-directory', 'nonff', 'identity', 'moving-target'):
            with self.subTest(case=case), self.fixture() as f, self.controller(f):
                initial = f.git('-C', f.checkout, 'rev-parse', 'HEAD')
                if case.startswith('ignored'):
                    (f.checkout / '.git/info/exclude').write_text('local-cache\n')
                    if case == 'ignored-directory':
                        (f.checkout / 'local-cache').mkdir(); (f.checkout / 'local-cache/bytes').write_bytes(b'local bytes')
                    else: (f.checkout / 'local-cache').write_bytes(b'local bytes')
                if case in ('dirty', 'staged', 'commit'):
                    (f.checkout / 'README').write_text('user bytes\n')
                    if case != 'dirty': f.git('-C', f.checkout, 'add', '.')
                    if case == 'commit': f.git('-C', f.checkout, '-c', 'user.name=fixture', '-c', 'user.email=fixture@example.test', 'commit', '-m', 'user commit')
                if case == 'untracked': (f.checkout / 'local-only').write_bytes(b'local bytes')
                if case == 'operation': (f.checkout / '.git/MERGE_HEAD').write_text(initial + '\n')
                before = self.snapshot(f.checkout)
                requests = len(f.provider.git_tokens)
                if case == 'nonff':
                    f.git('-C', f.directory / 'seed', 'checkout', '--orphan', 'replacement')
                    f.git('-C', f.directory / 'seed', 'rm', '-rf', '.')
                    (f.directory / 'seed/README').write_text('unrelated history')
                    f.git('-C', f.directory / 'seed', 'add', '.')
                    f.git('-C', f.directory / 'seed', 'commit', '-m', 'nonff')
                    f.git('-C', f.directory / 'seed', 'push', f.directory / 'repositories/owner/project.git', '+HEAD:main')
                else: target = self.publish(f, 'local-cache' if case in ('ignored-file', 'ignored-directory') else 'README')
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
                if case == 'ignored':
                    f.wait('checkout-refreshed')
                    self.assertEqual(f.git('-C', f.checkout, 'rev-parse', 'HEAD'), target)
                    self.assertEqual((f.checkout / 'local-cache').read_bytes(), b'local bytes')
                    self.assertEqual(json.loads(f.owner.read_text())['commit'], target)
                else:
                    f.wait('refresh-blocked' if case in ('ignored-file', 'ignored-directory', 'identity') else 'refresh-pending')
                    self.assertEqual(self.snapshot(f.checkout), before)
                    self.assertEqual(json.loads(f.owner.read_text())['commit'], initial)
                    if case in ('dirty', 'staged', 'untracked', 'commit', 'operation'): self.assertEqual(len(f.provider.git_tokens), requests)
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

    def rpc(self, f, method):
        self.assertTrue(select.select([f.responses], [], [], 20)[0], 'Missing native request ' + method)
        message = json.loads(f.responses.readline())
        self.assertEqual(message['method'], method)
        return message

    def reply(self, f, request, result):
        f.commands.write(json.dumps(dict(id=request['id'], result=result)) + '\n'); f.commands.flush()

    def terminal(self, f, thread, turn):
        f.commands.write(json.dumps(dict(method='turn/completed', params=dict(threadId=thread, turn=dict(id=turn, status='completed')))) + '\n'); f.commands.flush()

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

    def test_shared_turn_early_completion_waits_for_the_new_admission_acknowledgement(self):
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
            request = self.rpc(f, 'turn/start')
            self.terminal(f, 'S1', 'T1')
            f.wait('native-event')
            journal = json.loads(f.journal.read_text())
            self.assertEqual(len(journal['active']), 1, 'Early terminal cleared admission before acknowledgement')
            self.assertFalse(next(iter(journal['active'].values()))['confirmed'])
            self.assertEqual(f.git('-C', f.checkout, 'rev-parse', 'HEAD'), initial)
            self.assertEqual(len(f.provider.git_tokens), requests)
            self.reply(f, request, dict(turn=dict(id='T1')))
            f.wait('checkout-refreshed'); self.settled(f)
            self.assertEqual(f.git('-C', f.checkout, 'rev-parse', 'HEAD'), target)
            self.assertEqual(json.loads(f.journal.read_text())['active'], {})
            print('MATRIX_EARLY shared-turn-ack-and-save-required=true', flush=True)

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
                    f.wait('observation-error')
                    self.assertFalse(select.select([f.responses], [], [], 0)[0])
                    self.assertEqual(f.git('-C', f.checkout, 'rev-parse', 'HEAD'), initial)
                    self.assertEqual(len(f.provider.git_tokens), requests)
                result = subprocess.run(f.command + ['--once'], env=f.environment, capture_output=True, text=True, timeout=20)
                self.assertEqual(result.returncode, 1)
                self.assertIn('refresh' if case == 'journal-write' else 'uncertain', result.stdout.lower())
                print('MATRIX_FAULT case=' + case + ' durable-busy=true admission-blocked=true', flush=True)



    def test_safe_dirty_work_defers_refresh_but_allows_existing_discussion_assistance(self):
        with self.fixture(protocol=True) as f, self.controller(f):
            f.provider.discussions = [self.discussion(1)]
            request = self.rpc(f, 'thread/start')
            self.reply(f, request, dict(cwd=str(f.checkout), thread=dict(id='S1', cwd=str(f.checkout))))
            request = self.rpc(f, 'turn/start'); self.reply(f, request, dict(turn=dict(id='T1')))
            f.wait('poll-completed success=True')
            initial = f.git('-C', f.checkout, 'rev-parse', 'HEAD')
            (f.checkout / 'README').write_text('preserve user bytes\n')
            before = self.snapshot(f.checkout)
            requests = len(f.provider.git_tokens)
            self.publish(f); self.pending(f)
            self.terminal(f, 'S1', 'T1'); f.wait('refresh-pending')
            f.provider.discussions[0]['body'] += ' assistance with dirty checkout'
            request = self.rpc(f, 'turn/start')
            self.reply(f, request, dict(turn=dict(id='T2')))
            f.wait('poll-completed success=True')
            self.assertEqual(self.snapshot(f.checkout), before)
            self.assertEqual(f.git('-C', f.checkout, 'rev-parse', 'HEAD'), initial)
            self.assertEqual(json.loads(f.owner.read_text())['commit'], initial)
            self.assertEqual(len(f.provider.git_tokens), requests)
            self.assertFalse(json.loads(f.journal.read_text())['blocked'])
            self.terminal(f, 'S1', 'T2'); f.wait('native-event')
            print('MATRIX_DIRTY assistance=accepted user-content-index-refs=preserved nofetch=true', flush=True)



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
                        f.wait('manual-recovery')
                        self.assertTrue(json.loads(f.journal.read_text())['blocked'])
                        self.assertTrue(json.loads(f.state.read_text())['discussions']['D1']['pending'])
                        self.assertFalse(select.select([f.responses], [], [], 0)[0])
                        self.assertEqual(len(f.provider.git_tokens), requests)
                    else:
                        request = self.rpc(f, 'turn/start')
                        turn = 'T2' if case == 'resume' else 'T1'
                        self.reply(f, request, dict(turn=dict(id=turn)))
                        self.terminal(f, 'S1', turn)
                        f.wait('checkout-refreshed'); self.settled(f)
                        self.assertEqual(f.git('-C', f.checkout, 'rev-parse', 'HEAD'), target)
                if case == 'save-failure':
                    Path(str(f.state) + '.tmp').rmdir()  # Release only the fixture write fault.
                    result = subprocess.run(f.command + ['--once'], env=f.environment, capture_output=True, text=True, timeout=20)
                    self.assertEqual(result.returncode, 1); self.assertIn('uncertain', result.stdout)
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
            config.write_text(config.read_text() + 'model = "gpt-6.1-sol"\nmodel_provider = "fixture"\n[model_providers.fixture]\nname = "Credential-free local fixture"\nbase_url = "https://broker.fixture/v1"\nwire_api = "responses"\nrequires_openai_auth = false\nsupports_websockets = false\nrequest_max_retries = 0\nstream_max_retries = 0\n')
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
                first = json.loads(f.wait('native-event').split(' native-event ', 1)[1])
                self.assertEqual(first['method'], 'turn/completed'); print('NATIVE_COMPLETED ' + json.dumps(first), flush=True)
                self.settled(f)
                self.assertEqual(first['params']['turn']['status'], 'completed')
                self.assertEqual(len(f.provider.model_requests), 1)
                session = json.loads(f.state.read_text())['discussions']['D1']['sessionId']
                self.assertEqual(first['params']['threadId'], session)
                rollout, = home.rglob('rollout-*.jsonl')
                prefix = rollout.read_bytes()
                self.assertIn(b'NATIVE_USER_FIRST', prefix)
                self.assertIn(b'Harmless fixture assistant 1.', prefix)
                request_a = json.dumps(f.provider.model_requests[0]['body'])
                self.assertIn('NATIVE_AGENTS_A', request_a)
                self.assertIn('NATIVE_SKILL_BODY_A', request_a)
                context('B', 'high'); target = self.publish(f)
                f.wait('checkout-refreshed'); self.settled(f)
                probe = native_config(f.checkout, home, discovery=True, environment=f.environment)
                self.assertEqual(probe['config']['model_reasoning_effort'], 'high')
                project = [layer for layer in probe['layers'] if layer['name']['type'] == 'project']
                self.assertTrue(project); self.assertFalse(project[0].get('disabledReason'))
                listed = json.dumps(probe['fixtureSkills'])
                self.assertIn(str(f.checkout / '.agents/skills/repo-refresh/SKILL.md'), listed)
                self.assertIn('NATIVE_DESCRIPTION_B', listed)
                self.assertNotIn('NATIVE_DESCRIPTION_A', listed)
                f.provider.discussions[0]['body'] += ' $repo-refresh NATIVE_USER_SECOND'
                second = json.loads(f.wait('native-event').split(' native-event ', 1)[1])
                self.assertEqual(second['method'], 'turn/completed'); print('NATIVE_COMPLETED ' + json.dumps(second), flush=True)
                self.settled(f)
                self.assertEqual(second['params']['turn']['status'], 'completed')
                self.assertEqual(second['params']['threadId'], session)
                self.assertEqual(json.loads(f.state.read_text())['discussions']['D1']['sessionId'], session)
                self.assertEqual(len(f.provider.model_requests), 2)
                request_b = json.dumps(f.provider.model_requests[1]['body'])
                for text in ('NATIVE_USER_FIRST', 'Harmless fixture assistant 1.', 'NATIVE_USER_SECOND', 'NATIVE_AGENTS_B', 'NATIVE_SKILL_BODY_B'):
                    self.assertIn(text, request_b)
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
                    self.assertEqual(deliver(notice, event='discussion'), 202); f.wait('observation-error')
                    self.assertEqual(f.git('-C', f.checkout, 'rev-parse', 'HEAD'), target)


if __name__ == '__main__':
    unittest.main()
