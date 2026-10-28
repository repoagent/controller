"""Own a temporary, accountless tunnel and the bridge for one container lifetime.

The first tunnel must connect before startup continues. Afterwards a replacement
is requested with capped backoff while the existing controller finishes its work.
Only explicit idle status permits retirement; its normal shutdown also drains a
turn admitted after that snapshot. Recovery waits for actual controller exit,
then restarts with the replacement URL and reports recovery only after readiness.
"""
from contextlib import closing
import datetime
import errno
import http.client
import json
import os
import queue
import re
import select
import signal
import socket
import subprocess
import sys
import threading
import time

CONNECT_SECONDS = 90
RETRY_FIRST_SECONDS = 5
RETRY_MAX_SECONDS = 300
# How long a recovered tunnel must keep serving before backoff starts over.
BACKOFF_RESET_SECONDS = 300
STATUS_POLL_SECONDS = 1
STATUS_TIMEOUT_SECONDS = 2
WAIT_PROGRESS_SECONDS = 30
MAX_STATUS_BYTES = 65536
OUTPUT = threading.Lock()


def log(event, message):
    with OUTPUT:
        print(datetime.datetime.now(datetime.timezone.utc).isoformat() + ' ' + event + ' ' + message, flush=True)


class TunnelUnavailable(Exception):
    """The owned tunnel did not provide a connected public URL."""


class TunnelNotStarted(TunnelUnavailable):
    """The cloudflared executable could not be started."""


def status_state(body):
    """Decode the existing status contract without treating unknown input as idle."""
    def fields(pairs):
        result = {}
        for key, value in pairs:
            if key in result:
                raise ValueError('duplicate status field')
            result[key] = value
        return result

    def invalid_constant(value):
        raise ValueError('invalid JSON constant')

    value = json.loads(body, object_pairs_hook=fields, parse_constant=invalid_constant)
    if not isinstance(value, dict) or value.get('state') not in ('busy', 'idle'):
        raise ValueError('unknown status state')
    return value['state']


class _StatusSocket(socket.socket):
    """One local status request's I/O budget, including progress between received bytes."""
    def __init__(self, deadline, stopping):
        super().__init__(socket.AF_INET, socket.SOCK_STREAM)
        self.deadline, self.stopping = deadline, stopping

    def remaining(self):
        remaining = self.deadline - time.monotonic()
        if remaining <= 0 or self.stopping is not None and self.stopping.is_set():
            raise TimeoutError('status request ended')
        return remaining

    def connect_status(self, port):
        self.remaining()
        self.setblocking(False)
        error = self.connect_ex(('127.0.0.1', port))
        if error not in (0, errno.EINPROGRESS, errno.EWOULDBLOCK, errno.EALREADY):
            raise OSError(error, 'status connection failed')
        while error:
            _, writable, exceptional = select.select([], [self], [self], min(0.2, self.remaining()))
            if writable or exceptional:
                error = self.getsockopt(socket.SOL_SOCKET, socket.SO_ERROR)
                if error:
                    raise OSError(error, 'status connection failed')
        self.settimeout(min(0.2, self.remaining()))

    def recv_into(self, buffer, nbytes=0, flags=0):
        while True:
            self.settimeout(min(0.2, self.remaining()))
            try:
                return super().recv_into(buffer, nbytes, flags)
            except TimeoutError:
                # Retry inside SocketIO: a timeout escaping it makes its file permanently unreadable.
                continue

    def sendall(self, data, flags=0):
        pending = memoryview(data)
        while pending:
            self.settimeout(min(0.2, self.remaining()))
            try:
                sent = super().send(pending, flags)
            except TimeoutError:
                continue
            if not sent:
                raise OSError('status connection closed while sending')
            pending = pending[sent:]


def controller_status(port, stopping=None):
    """Query only the private loopback status port; diagnostic reasons contain no response data."""
    try:
        # HTTPConnection uses neither environment proxies nor redirects. Its normal HTTP parser reads through the budgeted socket.
        with (
            _StatusSocket(time.monotonic() + STATUS_TIMEOUT_SECONDS, stopping) as local,
            closing(http.client.HTTPConnection('127.0.0.1', port)) as connection,
        ):
            local.connect_status(port)
            connection.sock = local
            connection.request('GET', '/status')
            with connection.getresponse() as response:
                if response.status != 200:
                    return None, 'status-http-' + str(response.status)
                lengths = response.headers.get_all('Content-Length', [])
                if len(lengths) > 1 or lengths and not lengths[0].strip().isdigit():
                    return None, 'status-invalid-response'
                declared = int(lengths[0]) if lengths else None
                if declared is not None and declared > MAX_STATUS_BYTES:
                    return None, 'status-response-too-large'
                body = response.read(MAX_STATUS_BYTES + 1)
                if len(body) > MAX_STATUS_BYTES:
                    return None, 'status-response-too-large'
                if declared is not None and declared != len(body):
                    return None, 'status-invalid-response'
                state = status_state(body)
                local.remaining()
                return state, 'controller-busy'
    except (ValueError, UnicodeError, RecursionError):
        return None, 'status-invalid-response'
    except (OSError, http.client.HTTPException):
        return None, 'status-unavailable'


def run(bridge, port, environment=None, *, status_port):
    """Discover a connected tunnel, launch the existing receiver with its origin and keep both available.

    Only the receiver gets ``environment``; the tunnel inherits the ordinary process environment.
    """
    stopping = threading.Event()
    previous = {}
    owned = {}
    launched = False

    def stop(signum, frame):
        stopping.set()

    def tunnel_output(process, messages):
        for line in process.stdout:
            url = re.search(r'https://[a-z0-9][a-z0-9-]*\.trycloudflare\.com(?=[\s/]|$)', line)
            if url:
                messages.put(('url', url.group()))
            if 'Registered tunnel connection' in line:
                messages.put(('connected', None))
            if ' ERR ' in line or ' WRN ' in line:
                log('tunnel-diagnostic', line.strip()[:800])

    def bridge_output(process, ready):
        for line in process.stdout:
            with OUTPUT:
                sys.stdout.write(line)
                sys.stdout.flush()
            if line.split(' ', 2)[1:2] == ['ready']:
                ready.set()

    def terminate(label, grace):
        # Callers stop the bridge first: it must disable its hook before ingress stops.
        if label not in owned:
            return
        process, reader = owned.pop(label)
        if process.poll() is None:
            process.terminate()
        try:
            process.wait(timeout=grace)
        except subprocess.TimeoutExpired:
            log('tunnel-diagnostic', 'process shutdown exceeded its grace period; forcing owned process-group cleanup')
        finally:
            try:
                os.killpg(process.pid, signal.SIGKILL)
            except ProcessLookupError:
                pass
            process.wait(timeout=1)
            reader.join(timeout=2)
        log('tunnel-process-stopped', 'process=' + label + ' exit-code=' + str(process.returncode))

    def connect(observe=lambda: None):
        """Start one tunnel; return its connected origin, or None when stopping."""
        nonlocal launched
        messages = queue.SimpleQueue()
        try:
            tunnel = subprocess.Popen([
                'cloudflared', 'tunnel', '--no-autoupdate', '--protocol', 'http2',
                '--url', 'http://127.0.0.1:' + str(port)
            ], stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, errors='replace', start_new_session=True)
        except OSError as error:
            raise TunnelNotStarted('cloudflared could not start: ' + str(error)) from None
        reader = threading.Thread(target=tunnel_output, args=(tunnel, messages), daemon=True)
        reader.start()
        owned['cloudflared'] = (tunnel, reader)
        launched = True
        origin, connected = None, False
        deadline = time.monotonic() + CONNECT_SECONDS
        while not (origin and connected):
            observe()
            if stopping.is_set():
                return None
            if tunnel.poll() is not None:
                raise TunnelUnavailable('cloudflared exited before a connected public URL was available')
            if time.monotonic() >= deadline:
                raise TunnelUnavailable('No connected Quick Tunnel URL within ' + str(CONNECT_SECONDS) + ' seconds')
            try:
                kind, value = messages.get(timeout=0.2)
                if kind == 'url':
                    origin = value
                elif kind == 'connected':
                    connected = True
            except queue.Empty:
                pass
        if stopping.is_set():
            return None
        log('tunnel-connected', 'mode=quick-tunnel public-url=' + origin)
        return origin

    def launch(origin):
        ready = threading.Event()
        agent = subprocess.Popen(bridge + ['--webhook-url', origin], stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                                 text=True, errors='replace', start_new_session=True, env=environment)
        reader = threading.Thread(target=bridge_output, args=(agent, ready), daemon=True)
        reader.start()
        owned['bridge'] = (agent, reader)
        return ready

    def recovery_failed(attempt, reason, preserve_bridge=False):
        receiver = 'running' if preserve_bridge else 'stopped'
        log('tunnel-recovery-failed', 'attempt=' + str(attempt) + ' receiver=' + receiver + ' reason=' + reason
            + '; retrying with capped backoff')
        if not preserve_bridge:
            terminate('bridge', 12)
        terminate('cloudflared', 3)

    def recover(attempt, keep_bridge):
        """Replace a lost tunnel and restart the bridge with capped backoff.

        Return (origin, ready, attempt); origin is None when stopping.
        """
        retained = owned.get('bridge') if keep_bridge else None
        retiring = False

        def observe():
            nonlocal retained
            if retained is None or retained[0].poll() is None:
                return
            if not retiring or retained[0].returncode != 0:
                raise RuntimeError('Bridge exited with status ' + str(retained[0].returncode)
                                   + '; stopping its Quick Tunnel. Inspect the preceding bridge error before restarting.')
            # The host has drained accepted turns and exited. Only now clean up its group and release ownership.
            terminate('bridge', 12)
            retained = None

        next_progress = 0
        while True:
            attempt += 1
            delay = min(RETRY_MAX_SECONDS, RETRY_FIRST_SECONDS * 2 ** (attempt - 1))
            log('tunnel-recovering', 'attempt=' + str(attempt) + ' delay-seconds=' + format(delay, 'g')
                + ' receiver=' + ('running' if retained else 'stopped'))
            due = time.monotonic() + delay
            while time.monotonic() < due:
                observe()
                if stopping.wait(min(0.2, due - time.monotonic())):
                    return None, None, attempt
            try:
                origin = connect(observe)
            except TunnelUnavailable as error:
                recovery_failed(attempt, str(error), retained is not None)
                continue
            # A stop that arrives as connect() returns, such as while it logs, starts no replacement bridge.
            if origin is None or stopping.is_set():
                return None, None, attempt
            while retained is not None:
                observe()
                if retained is None or stopping.is_set():
                    break
                if owned['cloudflared'][0].poll() is not None:
                    break
                if not retiring:
                    state, reason = controller_status(status_port, stopping)
                    observe()
                    if stopping.is_set() or retained is None or owned['cloudflared'][0].poll() is not None:
                        break
                    if state == 'idle':
                        log('tunnel-retiring', 'receiver=draining reason=controller-idle; waiting for controller exit')
                        retiring = True
                        retained[0].terminate()
                        next_progress = time.monotonic() + WAIT_PROGRESS_SECONDS
                    elif time.monotonic() >= next_progress:
                        log('tunnel-waiting', 'receiver=running reason=' + reason + '; keeping the existing controller')
                        next_progress = time.monotonic() + WAIT_PROGRESS_SECONDS
                elif time.monotonic() >= next_progress:
                    log('tunnel-waiting', 'receiver=draining reason=controller-exit-pending')
                    next_progress = time.monotonic() + WAIT_PROGRESS_SECONDS
                if stopping.wait(0.2 if retiring else STATUS_POLL_SECONDS):
                    return None, None, attempt
            if stopping.is_set():
                return None, None, attempt
            if owned['cloudflared'][0].poll() is not None:
                recovery_failed(attempt, 'replacement cloudflared exited before the controller could relaunch', retained is not None)
                continue
            try:
                return origin, launch(origin), attempt
            except (OSError, subprocess.SubprocessError) as error:
                recovery_failed(attempt, 'restarted bridge could not start: ' + str(error))

    try:
        for signum in (signal.SIGINT, signal.SIGTERM):
            previous[signum] = signal.signal(signum, stop)
        log('tunnel-starting', 'mode=quick-tunnel temporary-hostname=true')
        try:
            origin = connect()
        except TunnelNotStarted as error:
            raise RuntimeError(str(error) + '; check that the container can run its bundled cloudflared executable, then restart.') from None
        except TunnelUnavailable as error:
            raise RuntimeError(str(error) + '; check network access and Cloudflare availability, then restart.') from None
        if origin is None or stopping.is_set():  # A stop can arrive after connect() last checked.
            return 0
        ready = launch(origin)
        attempt, serving_since = 0, None
        while not stopping.wait(0.2):
            keep_bridge = False
            agent, tunnel = owned['bridge'][0], owned['cloudflared'][0]
            if agent.poll() is not None:
                owned['bridge'][1].join(timeout=2)
                # Startup stays fail-fast, and a bridge that exits after reporting ready is not a tunnel failure.
                if not attempt or ready.is_set():
                    raise RuntimeError('Bridge exited with status ' + str(agent.returncode) + '; stopping its Quick Tunnel. Inspect the preceding bridge error before restarting.')
                recovery_failed(attempt, 'restarted bridge exited with status ' + str(agent.returncode)
                                + ' before reporting ready; see its preceding error')
            else:
                # Readiness counts only while the tunnel still runs; a replacement lost first is a failed attempt.
                if tunnel.poll() is None:
                    if serving_since is None and ready.is_set():
                        serving_since = time.monotonic()
                        if attempt:
                            log('tunnel-recovered', 'attempts=' + str(attempt) + ' public-url=' + origin + ' receiver=ready')
                    continue
                if attempt and serving_since is None:
                    recovery_failed(attempt, 'replacement cloudflared exited with status ' + str(tunnel.returncode)
                                    + ' before the restarted bridge reported ready')
                else:
                    if serving_since is not None and time.monotonic() - serving_since >= BACKOFF_RESET_SECONDS:
                        attempt = 0
                    log('tunnel-lost', 'exit-code=' + str(tunnel.returncode)
                        + '; requesting a replacement URL while keeping the existing controller until idle')
                    keep_bridge = True
                    terminate('cloudflared', 3)
            origin, ready, attempt = recover(attempt, keep_bridge)
            if origin is None:
                return 0
            serving_since = None
        return 0
    except (OSError, RuntimeError, subprocess.SubprocessError) as error:
        log('tunnel-error', str(error))
        return 1
    finally:
        terminate('bridge', 12)
        terminate('cloudflared', 3)
        for signum, handler in previous.items():
            signal.signal(signum, handler)
        if launched:
            log('tunnel-stopped', 'bridge-and-tunnel-cleaned-up=true')
