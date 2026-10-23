"""Quick Tunnel supervision through the real wrapper, bridge and process boundaries.

The supervisor (container/quick_tunnel.py) runs as its own process. Its `cloudflared`
is a controlled local executable that prints Cloudflare's connection lines but opens no
tunnel. The actual controller registers its saved hook against the local TLS GitHub
fixture, never a real repository. Native Codex is a minimal app-server stand-in that
records requested methods, so recovery cannot silently start or replay native work.
"""
from contextlib import closing, contextmanager
import hashlib
import hmac
import http.client
import json
import os
from pathlib import Path
import queue
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
                                     overlapping=[p['pid'] for p in previous if alive(p['pid'])])) + '\\n')
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
            elif 'id' in message:
                print(json.dumps(dict(id=message['id'], error=dict(code=-32601, message='fixture: no native work'))), flush=True)
    ''')

# The real supervisor entry point; tests shorten its retry and connection timing only.
LAUNCH_SUPERVISOR = textwrap.dedent('''\
    import json, sys
    sys.path.insert(0, sys.argv[1])
    import quick_tunnel
    for name, value in json.loads(sys.argv[4]).items():
        setattr(quick_tunnel, name, value)
    sys.exit(quick_tunnel.run(json.loads(sys.argv[2]), int(sys.argv[3])))
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
    def instance(self, plan=('connect',), timing=None):
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
                # Mirrors runtime.bridge_args for broker quick-tunnel mode; the supervisor adds --webhook-url.
                bridge = ['dotnet', str(CONTROLLER), '--repo', 'owner/project', '--app-id', '123',
                          '--workspace', str(workspace), '--state', str(state), '--broker-url', 'https://broker.fixture',
                          '--enrollment-file', str(enrollment), '--listen-port', str(port), '--reconcile-interval', '3600']
                supervisor = subprocess.Popen([sys.executable, '-c', LAUNCH_SUPERVISOR, str(ROOT / 'container'),
                                               json.dumps(bridge), str(port), json.dumps(timing or {})],
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

                def receiver_listening():
                    try:
                        with socket.create_connection(('127.0.0.1', port), timeout=2):
                            return True
                    except ConnectionRefusedError:
                        return False

                yield SimpleNamespace(directory=directory, provider=provider, supervisor=supervisor, wait=wait, launches=launches, port=port,
                                      hook_writes=hook_writes, deliver_ping=deliver_ping, output=output, stop=stop,
                                      hook=Path(str(state) + '.webhook/hook.json'), state=state, replan=replan,
                                      receiver_listening=receiver_listening, native_methods=native_methods)
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
            self.assertEqual(set(q.native_methods()), {'initialize', 'initialized'}, 'Recovery must not start or replay native work')
            self.assertEqual(json.loads(q.state.read_text()), RETAINED_STATE, 'Recovery must keep bridge state')
            self.assertEqual(q.stop(), 0)
            print('QUICK_TUNNEL_RECOVERY ' + json.dumps(dict(hook=saved['hookId'], replacement=replacement,
                  backoff=round(launches[1]['started'] - killed, 2), writes=[r for r, _ in writes])), flush=True)

    def test_first_tunnel_failure_still_stops_startup_without_retry(self):
        with self.instance(plan=('fail',), timing=dict(RETRY_FIRST_SECONDS=0.1)) as q:
            self.assertEqual(q.supervisor.wait(timeout=30), 1)
            q.stop()
            log = q.output()
            self.assertIn('tunnel-error cloudflared exited before a connected public URL was available', log)
            self.assertEqual(len(q.launches()), 1, 'Startup failure must be reported, not retried')
            self.assertNotIn(' ready repository=', log)
            self.assertEqual(q.hook_writes(), [])

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

    def test_flapping_tunnel_keeps_backing_off_until_a_replacement_has_served(self):
        timing = dict(RETRY_FIRST_SECONDS=0.5, RETRY_MAX_SECONDS=3.0)
        with self.instance(timing=timing) as q:
            q.wait(' ready repository=')
            delays = []
            for served in (0, 0, 3.5):
                time.sleep(served)
                os.kill(q.launches()[-1]['pid'], signal.SIGKILL)
                recovering = q.wait('tunnel-recovering ')
                delays.append((int(recovering.split('attempt=')[1].split()[0]), float(recovering.split('delay-seconds=')[1].split()[0])))
                q.wait('tunnel-recovered')
            # A replacement that dies before serving for the maximum delay does not restart the backoff.
            self.assertEqual(delays, [(1, 0.5), (2, 1.0), (1, 0.5)], q.output())
            self.assertEqual(q.stop(), 0)

    def test_intentional_stop_never_respawns_and_leaves_the_saved_hook_inactive(self):
        phases = {'serving': (('connect',), dict()),
                  'recovery-backoff': (('connect',), dict(RETRY_FIRST_SECONDS=60.0, RETRY_MAX_SECONDS=60.0)),
                  'replacement-connecting': (('connect', 'hang'), dict(RETRY_FIRST_SECONDS=0.2, CONNECT_SECONDS=60))}
        for phase, (plan, timing) in phases.items():
            with self.subTest(phase=phase), self.instance(plan=plan, timing=timing) as q:
                q.wait(' ready repository=')
                if phase != 'serving':
                    os.kill(q.launches()[0]['pid'], signal.SIGKILL)
                    q.wait('tunnel-recovering attempt=1 ')
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
