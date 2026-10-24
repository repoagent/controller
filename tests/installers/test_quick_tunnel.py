"""Quick Tunnel supervision through the real wrapper, bridge and process boundaries.

The supervisor (container/quick_tunnel.py) runs as its own process. Its `cloudflared`
is a controlled local executable that prints Cloudflare's connection lines but opens no
tunnel. The actual controller registers its saved hook against the local TLS GitHub
fixture, never a real repository. Native Codex is a minimal app-server stand-in that
records requested methods, so recovery cannot silently start or replay native work.
"""
from contextlib import ExitStack, closing, contextmanager
import datetime
import hashlib
import hmac
import http.client
import json
import os
from pathlib import Path
import queue
import shutil
import signal
import socket
import subprocess
import sys
import tempfile
import textwrap
import threading
import time
from types import SimpleNamespace
import unittest

from test_managed_git import CONTROLLER, ROOT, GitProvider

HOOKS = '/repos/owner/project/hooks'
# An existing Discussion's native session; recovery must keep it as-is.
RETAINED_STATE = dict(version=1, repository='owner/project', agentLogin='test-app[bot]', discussions={
    'D7': dict(url='https://github.com/owner/project/discussions/7', sessionId='native-session-retained', sources={})})

FAKE_CLOUDFLARED = textwrap.dedent('''\
    #!/usr/bin/env python3
    """Controlled cloudflared: records each launch, then follows the test's plan."""
    import json, os, sys, time
    from pathlib import Path
    control = Path(os.environ['FAKE_TUNNEL_CONTROL'])
    launches = control / 'launches.jsonl'
    previous = [json.loads(line) for line in launches.read_text().splitlines()] if launches.exists() else []
    def alive(pid):
        try: return Path('/proc/%d/stat' % pid).read_text().rsplit(')', 1)[1].split()[0] not in ('Z', 'X')
        except OSError: return False
    number = len(previous) + 1
    plan = json.loads((control / 'plan.json').read_text())
    behavior = plan[min(number, len(plan)) - 1]
    with launches.open('a') as record:
        record.write(json.dumps(dict(number=number, pid=os.getpid(), argv=sys.argv[1:], started=time.time(), behavior=behavior,
                                     overlapping=[p['pid'] for p in previous if alive(p['pid'])],
                                     injected='REPOAGENT_ENROLLMENT' in os.environ)) + '\\n')
    def say(text):
        print(time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime()) + ' ' + text, file=sys.stderr, flush=True)
    if behavior == 'fail':
        say('ERR failed to request quick Tunnel: fixture provider unavailable')
        sys.exit(1)
    if behavior == 'hang':
        say('INF Requesting new quick Tunnel on trycloudflare.com...')
        while True:
            time.sleep(3600)
    say('INF Requesting new quick Tunnel on trycloudflare.com...')
    say('INF |  https://fixture-tunnel-%d.trycloudflare.com                    |' % number)
    say('INF Registered tunnel connection connIndex=0 connection=fixture event=0 ip=127.0.0.1 location=fixture protocol=http2')
    while True:
        time.sleep(3600)
    ''')

FAKE_CODEX = textwrap.dedent('''\
    #!/usr/bin/env python3
    """Minimal app-server: initializes and records every requested method."""
    import json, os, sys
    if sys.argv[1:] != ['app-server']:
        sys.exit(0)  # `codex login status` from the runtime entry point.
    with open(os.environ['FAKE_CODEX_LOG'], 'a') as log:
        for line in sys.stdin:
            message = json.loads(line)
            log.write(json.dumps(dict(pid=os.getpid(), method=message.get('method'))) + '\\n'); log.flush()
            if message.get('method') == 'initialize':
                result = dict(codexHome=os.environ['CODEX_HOME'], userAgent='fixture')
                print(json.dumps(dict(id=message['id'], result=result)), flush=True)
            elif message.get('method') == 'skills/extraRoots/set':  # Packaged skill registration at every initialize.
                print(json.dumps(dict(id=message['id'], result={})), flush=True)
            elif 'id' in message:
                print(json.dumps(dict(id=message['id'], error=dict(code=-32601, message='fixture: no native work'))), flush=True)
    ''')

# The real supervisor entry point; tests shorten its retry and connection timing only.
LAUNCH_SUPERVISOR = textwrap.dedent('''\
    import json, os, sys
    sys.path.insert(0, sys.argv[1])
    import quick_tunnel
    for name, value in json.loads(sys.argv[4]).items():
        setattr(quick_tunnel, name, value)
    # Like runtime.py's injected start: only the receiver's environment carries the enrollment, read from a private file.
    receiver = os.environ | json.loads(open(sys.argv[5]).read()) if len(sys.argv) > 5 else None
    sys.exit(quick_tunnel.run(json.loads(sys.argv[2]), int(sys.argv[3]), receiver))
    ''')

# The real runtime entry point with only its container paths redirected.
LAUNCH_RUNTIME = textwrap.dedent('''\
    import pathlib, sys
    sys.path.insert(0, sys.argv[1])
    import runtime
    runtime.CONFIG, runtime.CONTROLLER = pathlib.Path(sys.argv[2]), [sys.argv[3]]
    sys.argv = ['runtime.py', 'start']
    runtime.main()
    ''')

FAKE_CONTROLLER = textwrap.dedent('''\
    #!/usr/bin/env python3
    import json, os, sys
    with open(os.environ['FAKE_CONTROLLER_LOG'], 'a') as log:
        log.write(json.dumps(sys.argv[1:]) + '\\n')
    ''')


def alive(pid):
    try:
        return Path('/proc/%d/stat' % pid).read_text().rsplit(')', 1)[1].split()[0] not in ('Z', 'X')
    except OSError:
        return False


def owned_processes(directory):
    """Live processes whose command line names this journey's private directory."""
    found = []
    for entry in Path('/proc').iterdir():
        if entry.name.isdigit():
            try:
                command = (entry / 'cmdline').read_bytes().replace(b'\0', b' ').decode(errors='replace')
            except OSError:
                continue
            if str(directory) in command and alive(int(entry.name)):
                found.append((int(entry.name), command[:200]))
    return found


def free_port():
    with socket.socket() as listener:
        listener.bind(('127.0.0.1', 0))
        return listener.getsockname()[1]


@unittest.skipIf(os.name == 'nt', 'Quick Tunnel supervision runs in the Linux controller container')
class QuickTunnelRecoveryTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        build = subprocess.run(['dotnet', 'build', str(ROOT / 'src/RepoAgentController/RepoAgentController.csproj'),
                                '-c', 'Release', '--nologo', '-v', 'quiet'], cwd=ROOT,
                               capture_output=True, text=True, timeout=300)
        if build.returncode:
            raise RuntimeError(build.stdout + build.stderr)

    @contextmanager
    def instance(self, plan=('connect',), timing=None, injected=False):
        with tempfile.TemporaryDirectory(prefix='quick-tunnel-recovery-') as temporary:
            directory = Path(temporary)
            provider = GitProvider(directory)
            provider.webhooks, provider.clone, provider.hooks = True, False, []
            supervisor = None
            try:
                tools, control, home, workspace = (directory / name for name in ('tools', 'tunnel', 'home', 'workspace'))
                tools.mkdir(); control.mkdir(); home.mkdir(mode=0o700); workspace.mkdir()
                for name, source in (('cloudflared', FAKE_CLOUDFLARED), ('codex', FAKE_CODEX)):
                    (tools / name).write_text(source)
                    (tools / name).chmod(0o700)
                (control / 'plan.json').write_text(json.dumps(list(plan)))
                enrollment = directory / 'enrollment.json'
                enrollment.write_text(json.dumps(provider.enrollment()))
                enrollment.chmod(0o600)
                environment = provider.environment()
                for name in ('XDG_CONFIG_HOME', 'GH_CONFIG_DIR'):
                    environment.pop(name, None)
                environment.update(HOME=str(home), CODEX_HOME=str(home), PATH=str(tools) + os.pathsep + os.environ['PATH'],
                                   HTTP_PROXY=environment['HTTPS_PROXY'], http_proxy=environment['HTTPS_PROXY'],
                                   https_proxy=environment['HTTPS_PROXY'], ALL_PROXY='', all_proxy='', no_proxy='',
                                   FAKE_TUNNEL_CONTROL=str(control), FAKE_CODEX_LOG=str(directory / 'codex.jsonl'))
                state, port = directory / 'state/bridge.json', free_port()
                state.parent.mkdir()
                state.write_text(json.dumps(RETAINED_STATE))
                # The bridge's runtime executable, so a test can make the bridge impossible to start.
                runtime = directory / 'runtime/dotnet'
                runtime.parent.mkdir()
                runtime.symlink_to(shutil.which('dotnet'))
                # Mirrors runtime.bridge_args for broker quick-tunnel mode; the supervisor adds --webhook-url.
                bridge = [str(runtime), str(CONTROLLER), '--repo', 'owner/project', '--app-id', '123',
                          '--workspace', str(workspace), '--state', str(state), '--broker-url', 'https://broker.fixture',
                          '--enrollment-file', str(enrollment), '--listen-port', str(port), '--reconcile-interval', '3600']
                receiver = []
                if injected:
                    bridge[bridge.index('--enrollment-file'):bridge.index('--enrollment-file') + 2] = ['--enrollment-environment', 'REPOAGENT_ENROLLMENT']
                    variables = directory / 'receiver.json'
                    variables.write_text(json.dumps(dict(REPOAGENT_ENROLLMENT=enrollment.read_text())))
                    variables.chmod(0o600); enrollment.unlink()
                    receiver = [str(variables)]
                supervisor = subprocess.Popen([sys.executable, '-c', LAUNCH_SUPERVISOR, str(ROOT / 'container'),
                                               json.dumps(bridge), str(port), json.dumps(timing or {}), *receiver],
                                              env=environment, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
                events, lines = queue.Queue(), []
                def read():
                    for line in supervisor.stdout:
                        lines.append(line); events.put(line)
                    events.put(None)
                reader = threading.Thread(target=read, daemon=True)
                reader.start()

                def wait(fragment, timeout=60):
                    deadline = time.monotonic() + timeout
                    while True:
                        try:
                            line = events.get(timeout=max(0.001, deadline - time.monotonic()))
                        except queue.Empty:
                            self.fail('No ' + fragment + ' within ' + str(timeout) + 's\n' + ''.join(lines))
                        if line is None:
                            self.fail('Supervisor exited before ' + fragment + '\n' + ''.join(lines))
                        if fragment in line:
                            return line

                def launches():
                    path = control / 'launches.jsonl'
                    return [json.loads(line) for line in path.read_text().splitlines()] if path.exists() else []

                def hook_writes():
                    writes = [(method, path) for method, host, path in provider.requests
                              if host == 'api.github.com' and path.startswith(HOOKS)]
                    return list(zip(writes, provider.hooks))

                def deliver_ping(secret):
                    body = json.dumps(dict(zen='fixture', hook_id=456, repository=dict(full_name='owner/project', id=345))).encode()
                    digest = hmac.new(secret.encode(), body, hashlib.sha256).hexdigest()
                    with closing(http.client.HTTPConnection('127.0.0.1', port, timeout=10)) as connection:
                        connection.request('POST', '/github/webhook', body, {'X-GitHub-Event': 'ping',
                            'X-GitHub-Delivery': '00000000-0000-4000-8000-000000000192',
                            'X-Hub-Signature-256': 'sha256=' + digest})
                        response = connection.getresponse(); response.read()
                        return response.status

                def output():
                    return ''.join(lines)

                def stop(timeout=30):
                    if supervisor.poll() is None:
                        supervisor.send_signal(signal.SIGTERM)
                    supervisor.wait(timeout=timeout)
                    reader.join(timeout=5)
                    return supervisor.returncode

                def native_methods():
                    path = directory / 'codex.jsonl'
                    return [json.loads(line)['method'] for line in path.read_text().splitlines()] if path.exists() else []

                def replan(*behaviors):
                    staged = control / 'plan.tmp'
                    staged.write_text(json.dumps(list(behaviors)))
                    staged.replace(control / 'plan.json')

                @contextmanager
                def credential_held():
                    """Hold bridges at their broker credential request, so a restarted bridge cannot report ready."""
                    held, release, lease = threading.Event(), threading.Event(), provider.lease
                    def held_lease():
                        held.set()
                        release.wait(30)
                        return lease()
                    provider.lease = held_lease
                    try:
                        yield held
                    finally:
                        provider.lease = lease
                        release.set()

                def receiver_listening():
                    try:
                        with socket.create_connection(('127.0.0.1', port), timeout=2):
                            return True
                    except ConnectionRefusedError:
                        return False

                yield SimpleNamespace(directory=directory, provider=provider, supervisor=supervisor, wait=wait, launches=launches, port=port,
                                      hook_writes=hook_writes, deliver_ping=deliver_ping, output=output, stop=stop,
                                      hook=Path(str(state) + '.webhook/hook.json'), state=state, replan=replan,
                                      receiver_listening=receiver_listening, native_methods=native_methods, bridge_runtime=runtime,
                                      credential_held=credential_held)
            finally:
                if supervisor is not None and supervisor.poll() is None:
                    supervisor.send_signal(signal.SIGTERM)
                    try:
                        supervisor.wait(timeout=30)
                    except subprocess.TimeoutExpired:
                        supervisor.kill()
                        supervisor.wait(timeout=10)
                for pid, _ in owned_processes(directory):  # Only after a failed assertion.
                    os.kill(pid, signal.SIGKILL)
                if supervisor is not None:
                    supervisor.stdout.close()
                    print('QUICK_TUNNEL_SUPERVISOR exit=' + str(supervisor.returncode), flush=True)
                provider.close()

    def test_unexpected_tunnel_exit_registers_the_replacement_url_on_the_same_hook_before_readiness(self):
        timing = dict(RETRY_FIRST_SECONDS=1.0, RETRY_MAX_SECONDS=4.0)
        with self.instance(timing=timing) as q:
            q.wait(' ready repository=')
            saved = json.loads(q.hook.read_text())
            self.assertEqual(saved['hookId'], 456)
            self.assertEqual(q.deliver_ping(saved['secret']), 204)
            first = q.launches()[0]

            os.kill(first['pid'], signal.SIGKILL)
            killed = time.time()
            recovered = q.wait('tunnel-recovered')

            replacement = 'https://fixture-tunnel-2.trycloudflare.com'
            self.assertIn('public-url=' + replacement, recovered)
            launches = q.launches()
            self.assertEqual(len(launches), 2, 'One unexpected exit needs exactly one replacement tunnel')
            self.assertEqual(launches[1]['overlapping'], [], 'Replacement must not overlap the lost tunnel')
            self.assertGreaterEqual(launches[1]['started'] - killed, timing['RETRY_FIRST_SECONDS'] * 0.9,
                                    'Replacement must wait for its backoff instead of respawning immediately')
            # Same saved hook: one creation, then deactivation of the stale URL and a PATCH to the new one.
            writes = q.hook_writes()
            self.assertEqual([request for request, _ in writes],
                             [('POST', HOOKS), ('PATCH', HOOKS + '/456'), ('PATCH', HOOKS + '/456')])
            self.assertEqual(writes[1][1], {'active': False}, 'Stale temporary endpoint must be deactivated during recovery')
            updated = writes[2][1]
            self.assertIs(updated['active'], True)
            self.assertEqual(updated['config']['url'], replacement + '/github/webhook')
            self.assertEqual(updated['config']['secret'], saved['secret'])
            self.assertEqual(json.loads(q.hook.read_text()),
                             dict(saved, url=replacement + '/github/webhook'), 'Hook identity and signing secret must be retained')
            # Readiness only after the new tunnel, its hook registration and the receiver are ready.
            log = q.output()
            lost = log.index('tunnel-lost')
            order = [log.index('tunnel-connected mode=quick-tunnel public-url=' + replacement, lost),
                     log.index('webhook-updated hook=456 url=' + replacement, lost),
                     log.index(' ready repository=', lost),
                     log.index('tunnel-recovered', lost)]
            self.assertEqual(order, sorted(order), log)
            self.assertLess(log.index('webhook-disabled hook=456', lost), order[0], log)
            self.assertEqual(q.deliver_ping(saved['secret']), 204, 'Recovered receiver must accept the retained secret')
            self.assertEqual(set(q.native_methods()), {'initialize', 'initialized', 'skills/extraRoots/set'},
                             'Recovery must not start or replay native work')
            self.assertEqual(json.loads(q.state.read_text()), RETAINED_STATE, 'Recovery must keep bridge state')
            self.assertEqual(q.stop(), 0)
            print('QUICK_TUNNEL_RECOVERY ' + json.dumps(dict(hook=saved['hookId'], replacement=replacement,
                  backoff=round(launches[1]['started'] - killed, 2), writes=[r for r, _ in writes])), flush=True)

    def test_recovered_bridge_keeps_the_injected_enrollment_that_tunnels_never_receive(self):
        # External credentials (#182) reach only the receiver; recovery restarts it with the same receiver environment.
        with self.instance(timing=dict(RETRY_FIRST_SECONDS=0.5, RETRY_MAX_SECONDS=4.0), injected=True) as q:
            q.wait(' ready repository=')
            os.kill(q.launches()[0]['pid'], signal.SIGKILL)
            q.wait('tunnel-recovered')
            writes = q.hook_writes()
            self.assertEqual([request for request, _ in writes], [('POST', HOOKS), ('PATCH', HOOKS + '/456'), ('PATCH', HOOKS + '/456')])
            self.assertIs(writes[-1][1]['active'], True, 'The recovered bridge must authenticate with the injected enrollment')
            self.assertEqual([launch['injected'] for launch in q.launches()], [False, False], 'cloudflared received the controller credential')
            self.assertEqual(q.stop(), 0)

    def test_first_tunnel_failure_still_stops_startup_without_retry(self):
        with self.instance(plan=('fail',), timing=dict(RETRY_FIRST_SECONDS=0.1)) as q:
            self.assertEqual(q.supervisor.wait(timeout=30), 1)
            q.stop()
            log = q.output()
            self.assertIn('tunnel-error cloudflared exited before a connected public URL was available', log)
            self.assertEqual(len(q.launches()), 1, 'Startup failure must be reported, not retried')
            self.assertNotIn(' ready repository=', log)
            self.assertEqual(q.hook_writes(), [])

    def test_cloudflared_that_cannot_start_reports_its_os_error_without_blaming_the_network(self):
        with tempfile.TemporaryDirectory(prefix='quick-tunnel-missing-') as temporary:
            tools = Path(temporary) / 'tools'
            tools.mkdir()  # No cloudflared executable on PATH.
            result = subprocess.run([sys.executable, '-c', LAUNCH_SUPERVISOR, str(ROOT / 'container'),
                                     json.dumps([str(Path(temporary) / 'never-started-bridge')]), str(free_port()), '{}'],
                                    env=dict(os.environ, PATH=str(tools)), capture_output=True, text=True, timeout=60)
            self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
            error = next((line for line in result.stdout.splitlines() if ' tunnel-error ' in line), None)
            self.assertIsNotNone(error, result.stdout + result.stderr)
            self.assertIn("cloudflared could not start: [Errno 2] No such file or directory: 'cloudflared'", error)
            self.assertNotIn('network', error)
            self.assertNotIn('Cloudflare availability', error)

    def test_prolonged_tunnel_failure_retries_with_capped_backoff_without_claiming_readiness(self):
        timing = dict(RETRY_FIRST_SECONDS=0.4, RETRY_MAX_SECONDS=1.6)
        with self.instance(plan=('connect', 'fail'), timing=timing) as q:
            q.wait(' ready repository=')
            saved = json.loads(q.hook.read_text())
            os.kill(q.launches()[0]['pid'], signal.SIGKILL)
            for attempt in range(1, 6):
                q.wait('tunnel-recovery-failed attempt=' + str(attempt) + ' ')

            # Bounded: each retry waits for its doubling delay, capped at the maximum; never a tight loop.
            expected = [0.4, 0.8, 1.6, 1.6, 1.6]
            log = q.output()
            scheduled = [float(line.split('delay-seconds=')[1].split()[0]) for line in log.splitlines() if 'tunnel-recovering' in line]
            self.assertEqual(scheduled[:5], expected, log)
            failed = q.launches()[1:]
            self.assertEqual([launch['behavior'] for launch in failed[:5]], ['fail'] * 5)
            for earlier, later, delay in zip(failed, failed[1:5], expected[1:]):
                self.assertGreaterEqual(later['started'] - earlier['started'], delay * 0.9, 'Retry ignored its backoff')
            self.assertTrue(all(launch['overlapping'] == [] for launch in q.launches()), 'Tunnel owners overlapped')
            # Truthful while failing: no readiness claim, receiver stopped and the stale hook left inactive.
            self.assertEqual(log.count(' ready repository='), 1, log)
            self.assertNotIn('tunnel-recovered', log)
            self.assertFalse(q.receiver_listening(), 'A failed recovery must not leave a receiver advertising readiness')
            self.assertEqual(q.hook_writes()[-1][1], {'active': False})
            self.assertIsNone(q.supervisor.poll(), 'Prolonged failure must keep a later retry possible')

            # A later retry recovers once the provider is available again.
            q.replan('connect')
            recovered = q.wait('tunnel-recovered')
            replacement = 'https://fixture-tunnel-%d.trycloudflare.com' % len(q.launches())
            self.assertIn('public-url=' + replacement, recovered)
            self.assertGreater(int(recovered.split('attempts=')[1].split()[0]), 5)
            (request, updated) = q.hook_writes()[-1]
            self.assertEqual(request, ('PATCH', HOOKS + '/456'))
            self.assertEqual((updated['active'], updated['config']['url'], updated['config']['secret']),
                             (True, replacement + '/github/webhook', saved['secret']))
            self.assertEqual(q.deliver_ping(saved['secret']), 204)
            self.assertEqual(q.stop(), 0)
            print('QUICK_TUNNEL_PROLONGED ' + json.dumps(dict(scheduled=scheduled, launches=len(q.launches()),
                  gaps=[round(b['started'] - a['started'], 2) for a, b in zip(failed, failed[1:])])), flush=True)

    def test_restarted_bridge_failure_is_a_failed_recovery_attempt_that_is_retried(self):
        # A brief broker error just after a tunnel loss, or a bridge executable that cannot be run.
        timing = dict(RETRY_FIRST_SECONDS=0.5, RETRY_MAX_SECONDS=4.0)
        for phase in ('exits-before-ready', 'cannot-start'):
            with self.subTest(phase=phase), self.instance(timing=timing) as q:
                q.wait(' ready repository=')
                saved = json.loads(q.hook.read_text())
                # Inject the fault before the loss, not inside the short backoff. The running bridge holds an
                # hour-long lease and its executable is already loaded, so neither fault reaches it.
                if phase == 'exits-before-ready':
                    q.provider.lease_status = 503
                else:
                    unavailable = q.bridge_runtime.rename(q.bridge_runtime.with_name('dotnet-unavailable'))
                os.kill(q.launches()[0]['pid'], signal.SIGKILL)
                failed = q.wait('tunnel-recovery-failed attempt=1 ')
                failed_at = datetime.datetime.fromisoformat(failed.split(' ', 1)[0]).timestamp()
                if phase == 'exits-before-ready':
                    q.provider.lease_status = 200
                    self.assertIn('reason=restarted bridge exited with status ', failed)
                    self.assertIn('before reporting ready', failed)
                else:
                    unavailable.rename(q.bridge_runtime)
                    self.assertIn('reason=restarted bridge could not start: ', failed)
                    self.assertIn('No such file or directory', failed)
                recovered = q.wait('tunnel-recovered')

                replacement = 'https://fixture-tunnel-3.trycloudflare.com'
                self.assertIn('attempts=2 public-url=' + replacement, recovered)
                log = q.output()
                scheduled = [(int(line.split('attempt=')[1].split()[0]), float(line.split('delay-seconds=')[1].split()[0]))
                             for line in log.splitlines() if 'tunnel-recovering' in line]
                self.assertEqual(scheduled, [(1, 0.5), (2, 1.0)], log)
                # The failed attempt stops its replacement tunnel, then backs off before the next one.
                failure = log.index('tunnel-recovery-failed attempt=1 ')
                self.assertLess(log.index('tunnel-process-stopped process=cloudflared', failure),
                                log.index('tunnel-recovering attempt=2 '), log)
                launches = q.launches()
                self.assertEqual(len(launches), 3, log)
                self.assertTrue(all(launch['overlapping'] == [] for launch in launches), 'Tunnel owners overlapped')
                self.assertGreaterEqual(launches[2]['started'] - failed_at, 1.0 * 0.9, 'Retry ignored its backoff')
                # The failed attempt never claimed readiness or advertised its endpoint; the retry updates the same hook.
                self.assertEqual(log.count(' ready repository='), 2, log)
                self.assertNotIn('tunnel-error', log)
                writes = q.hook_writes()
                self.assertEqual([request for request, _ in writes],
                                 [('POST', HOOKS), ('PATCH', HOOKS + '/456'), ('PATCH', HOOKS + '/456')])
                self.assertEqual(writes[1][1], {'active': False})
                self.assertEqual((writes[2][1]['active'], writes[2][1]['config']['url'], writes[2][1]['config']['secret']),
                                 (True, replacement + '/github/webhook', saved['secret']))
                self.assertEqual(q.deliver_ping(saved['secret']), 204)
                self.assertEqual(q.stop(), 0)
                time.sleep(0.5)
                self.assertEqual(owned_processes(q.directory), [], 'Owned bridge, tunnel or native processes survived stop')
                print('QUICK_TUNNEL_BRIDGE_FAILURE ' + json.dumps(dict(phase=phase, scheduled=scheduled,
                      backoff=round(launches[2]['started'] - failed_at, 2))), flush=True)

    def test_replacement_tunnel_lost_before_the_restarted_bridge_is_ready_is_a_failed_recovery_attempt(self):
        timing = dict(RETRY_FIRST_SECONDS=0.5, RETRY_MAX_SECONDS=4.0)
        with self.instance(timing=timing) as q:
            q.wait(' ready repository=')
            saved = json.loads(q.hook.read_text())
            os.kill(q.launches()[0]['pid'], signal.SIGKILL)
            q.wait('tunnel-recovering attempt=1 ')
            with q.credential_held() as held:
                self.assertTrue(held.wait(30), 'Restarted bridge never requested its credential\n' + q.output())
                self.assertIn('tunnel-connected mode=quick-tunnel public-url=https://fixture-tunnel-2.', q.output())
                os.kill(q.launches()[1]['pid'], signal.SIGKILL)
                q.wait('tunnel-recovering attempt=2 ')
            recovered = q.wait('tunnel-recovered')

            log = q.output()
            replacement = 'https://fixture-tunnel-3.trycloudflare.com'
            self.assertIn('attempts=2 public-url=' + replacement, recovered)
            # Reported as a failed recovery attempt, not as another loss of a serving tunnel.
            self.assertEqual(log.count('tunnel-lost'), 1, log)
            failed = next((line for line in log.splitlines() if 'tunnel-recovery-failed attempt=1 ' in line), None)
            self.assertIsNotNone(failed, log)
            self.assertIn('reason=replacement cloudflared exited with status -9 before the restarted bridge reported ready', failed)
            # Both owned processes stop before the backoff, and the next tunnel overlaps nothing.
            failure = log.index('tunnel-recovery-failed attempt=1 ')
            retry = log.index('tunnel-recovering attempt=2 delay-seconds=1 ')
            self.assertLess(failure, log.index('tunnel-process-stopped process=bridge', failure), log)
            self.assertLess(log.index('tunnel-process-stopped process=cloudflared', failure), retry, log)
            self.assertLess(log.index('tunnel-process-stopped process=bridge', failure), retry, log)
            launches = q.launches()
            self.assertEqual(len(launches), 3, log)
            self.assertTrue(all(launch['overlapping'] == [] for launch in launches), 'Tunnel owners overlapped')
            # The failed attempt never claimed readiness or advertised the lost hostname.
            self.assertEqual(log.count(' ready repository='), 2, log)
            self.assertNotIn('tunnel-error', log)
            writes = q.hook_writes()
            self.assertEqual([request for request, _ in writes],
                             [('POST', HOOKS), ('PATCH', HOOKS + '/456'), ('PATCH', HOOKS + '/456')])
            self.assertEqual(writes[1][1], {'active': False})
            self.assertEqual((writes[2][1]['active'], writes[2][1]['config']['url'], writes[2][1]['config']['secret']),
                             (True, replacement + '/github/webhook', saved['secret']))
            self.assertEqual(q.deliver_ping(saved['secret']), 204)
            self.assertEqual(q.stop(), 0)

    def test_flapping_tunnel_keeps_backing_off_until_a_replacement_has_served(self):
        timing = dict(RETRY_FIRST_SECONDS=0.5, RETRY_MAX_SECONDS=3.0, BACKOFF_RESET_SECONDS=3.0)
        with self.instance(timing=timing) as q:
            q.wait(' ready repository=')
            delays = []
            for served in (0, 0, 3.5):
                time.sleep(served)
                os.kill(q.launches()[-1]['pid'], signal.SIGKILL)
                recovering = q.wait('tunnel-recovering ')
                delays.append((int(recovering.split('attempt=')[1].split()[0]), float(recovering.split('delay-seconds=')[1].split()[0])))
                q.wait('tunnel-recovered')
            # A replacement that dies before serving for BACKOFF_RESET_SECONDS does not restart the backoff.
            self.assertEqual(delays, [(1, 0.5), (2, 1.0), (1, 0.5)], q.output())
            self.assertEqual(q.stop(), 0)

    def test_intentional_stop_never_respawns_and_leaves_the_saved_hook_inactive(self):
        phases = {'serving': (('connect',), dict()),
                  'recovery-backoff': (('connect',), dict(RETRY_FIRST_SECONDS=60.0, RETRY_MAX_SECONDS=60.0)),
                  'replacement-connecting': (('connect', 'hang'), dict(RETRY_FIRST_SECONDS=0.2, CONNECT_SECONDS=60)),
                  'restarted-bridge-starting': (('connect',), dict(RETRY_FIRST_SECONDS=0.5))}
        for phase, (plan, timing) in phases.items():
            with self.subTest(phase=phase), self.instance(plan=plan, timing=timing) as q, ExitStack() as hold:
                q.wait(' ready repository=')
                if phase != 'serving':
                    os.kill(q.launches()[0]['pid'], signal.SIGKILL)
                    q.wait('tunnel-recovering attempt=1 ')
                if phase == 'restarted-bridge-starting':
                    self.assertTrue(hold.enter_context(q.credential_held()).wait(30), 'Restarted bridge did not start\n' + q.output())
                if phase == 'replacement-connecting':
                    deadline = time.monotonic() + 10
                    while len(q.launches()) < 2:
                        self.assertLess(time.monotonic(), deadline, 'Replacement tunnel did not start')
                        time.sleep(0.05)
                if phase == 'serving':
                    self.assertIn(q.launches()[0]['pid'], [pid for pid, _ in owned_processes(q.directory)])
                launched, writes = len(q.launches()), len(q.hook_writes())
                started = time.monotonic()
                self.assertEqual(q.stop(timeout=20), 0, q.output())
                elapsed = time.monotonic() - started
                time.sleep(0.5)
                log = q.output()
                self.assertEqual(len(q.launches()), launched, 'Intentional stop respawned cloudflared')
                self.assertEqual(owned_processes(q.directory), [], 'Owned bridge, tunnel or native processes survived stop')
                self.assertEqual(q.hook_writes()[-1][1], {'active': False}, 'Stopped instance must not advertise a temporary URL')
                self.assertIn('tunnel-stopped bridge-and-tunnel-cleaned-up=true', log)
                self.assertNotIn('tunnel-error', log)
                if phase == 'serving':
                    self.assertEqual(len(q.hook_writes()), writes + 1, 'Controlled stop applies the saved-hook shutdown')
                    self.assertLess(log.index('webhook-disabled hook=456'), log.index('process=cloudflared'), log)
                else:
                    self.assertEqual(len(q.hook_writes()), writes, 'Recovery already deactivated the saved hook')
                    self.assertNotIn('tunnel-recovered', log)
                    self.assertLess(elapsed, 10, 'Stop during recovery must not wait for its backoff or connection deadline')
                print('QUICK_TUNNEL_STOP ' + json.dumps(dict(phase=phase, seconds=round(elapsed, 2), launches=launched)), flush=True)


@unittest.skipIf(os.name == 'nt', 'Quick Tunnel supervision runs in the Linux controller container')
class HostingModeTunnelOwnershipTests(unittest.TestCase):
    def test_only_quick_tunnel_mode_launches_cloudflared(self):
        for mode in ('polling', 'fixed-url', 'quick-tunnel'):
            with self.subTest(mode=mode), tempfile.TemporaryDirectory(prefix='quick-tunnel-mode-') as temporary:
                directory = Path(temporary)
                tools, control = directory / 'tools', directory / 'tunnel'
                tools.mkdir(); control.mkdir()
                for name, source in (('cloudflared', FAKE_CLOUDFLARED), ('codex', FAKE_CODEX), ('controller', FAKE_CONTROLLER)):
                    (tools / name).write_text(source)
                    (tools / name).chmod(0o700)
                (control / 'plan.json').write_text(json.dumps(['connect']))
                settings = directory / 'settings.json'
                settings.write_text(json.dumps(dict(repository='owner/project', appId='123', brokerUrl='https://broker.fixture',
                    clone=False, mode=mode, publicUrl='https://agent.example.com' if mode == 'fixed-url' else '',
                    listenPort=free_port(), reconcileInterval=300, toolUpdates='pinned')))
                environment = dict(os.environ, PATH=str(tools) + os.pathsep + os.environ['PATH'], FAKE_TUNNEL_CONTROL=str(control),
                                   FAKE_CONTROLLER_LOG=str(directory / 'controller.jsonl'), FAKE_CODEX_LOG=str(directory / 'codex.jsonl'))
                result = subprocess.run([sys.executable, '-c', LAUNCH_RUNTIME, str(ROOT / 'container'), str(settings), str(tools / 'controller')],
                                        env=environment, capture_output=True, text=True, timeout=60)
                launches = (control / 'launches.jsonl').read_text().splitlines() if (control / 'launches.jsonl').exists() else []
                controller = [json.loads(line) for line in (directory / 'controller.jsonl').read_text().splitlines()]
                self.assertEqual(len(controller), 1, result.stdout + result.stderr)
                if mode == 'quick-tunnel':
                    self.assertEqual(len(launches), 1, 'Control: quick-tunnel mode owns exactly one tunnel')
                    self.assertIn('--webhook-url', controller[0])
                    continue
                self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
                self.assertEqual(launches, [], mode + ' must never launch cloudflared')
                self.assertNotIn('tunnel-', result.stdout + result.stderr)
                webhook = controller[0][controller[0].index('--webhook-url') + 1] if '--webhook-url' in controller[0] else None
                self.assertEqual(webhook, 'https://agent.example.com' if mode == 'fixed-url' else None)


if __name__ == '__main__':
    unittest.main()
