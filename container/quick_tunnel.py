"""Own a temporary, accountless tunnel and the bridge for one container lifetime.

The first tunnel must connect before startup continues. Afterwards an unexpected
cloudflared exit stops the bridge, which deactivates its saved hook, and a
replacement tunnel is requested with capped exponential backoff. The bridge then
restarts with the replacement URL, updating that same saved hook; recovery is
reported only after the bridge reports readiness. A replacement that cannot
connect, a restarted bridge that cannot start or exits before readiness, and a
replacement tunnel lost before that readiness are failed attempts: both owned
processes stop and the next attempt waits for the next backoff delay.
"""
import datetime
import os
import queue
import re
import signal
import subprocess
import sys
import threading
import time

CONNECT_SECONDS = 90
RETRY_FIRST_SECONDS = 5
RETRY_MAX_SECONDS = 300
# How long a recovered tunnel must keep serving before backoff starts over.
BACKOFF_RESET_SECONDS = 300
OUTPUT = threading.Lock()


def log(event, message):
    with OUTPUT:
        print(datetime.datetime.now(datetime.timezone.utc).isoformat() + ' ' + event + ' ' + message, flush=True)


class TunnelUnavailable(Exception):
    """The owned tunnel did not provide a connected public URL."""


class TunnelNotStarted(TunnelUnavailable):
    """The cloudflared executable could not be started."""


def run(bridge, port):
    """Discover a connected tunnel, launch the existing receiver with its origin and keep both available."""
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

    def connect():
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
                                 text=True, errors='replace', start_new_session=True)
        reader = threading.Thread(target=bridge_output, args=(agent, ready), daemon=True)
        reader.start()
        owned['bridge'] = (agent, reader)
        return ready

    def recovery_failed(attempt, reason):
        log('tunnel-recovery-failed', 'attempt=' + str(attempt) + ' receiver=stopped reason=' + reason
            + '; retrying with capped backoff')
        terminate('bridge', 12)
        terminate('cloudflared', 3)

    def recover(attempt):
        """Replace a lost tunnel and restart the bridge with capped backoff.

        Return (origin, ready, attempt); origin is None when stopping.
        """
        while True:
            attempt += 1
            delay = min(RETRY_MAX_SECONDS, RETRY_FIRST_SECONDS * 2 ** (attempt - 1))
            log('tunnel-recovering', 'attempt=' + str(attempt) + ' delay-seconds=' + format(delay, 'g') + ' receiver=stopped')
            if stopping.wait(delay):
                return None, None, attempt
            try:
                origin = connect()
            except TunnelUnavailable as error:
                recovery_failed(attempt, str(error))
                continue
            if origin is None:
                return None, None, attempt
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
        if origin is None:
            return 0
        ready = launch(origin)
        attempt, serving_since = 0, None
        while not stopping.wait(0.2):
            agent, tunnel = owned['bridge'][0], owned['cloudflared'][0]
            if agent.poll() is not None:
                owned['bridge'][1].join(timeout=2)
                # Startup stays fail-fast, and a bridge that exits after reporting ready is not a tunnel failure.
                if not attempt or ready.is_set():
                    raise RuntimeError('Bridge exited with status ' + str(agent.returncode) + '; stopping its Quick Tunnel. Inspect the preceding bridge error before restarting.')
                recovery_failed(attempt, 'restarted bridge exited with status ' + str(agent.returncode)
                                + ' before reporting ready; see its preceding error')
            else:
                if serving_since is None and ready.is_set():
                    serving_since = time.monotonic()
                    if attempt:
                        log('tunnel-recovered', 'attempts=' + str(attempt) + ' public-url=' + origin + ' receiver=ready')
                if tunnel.poll() is None:
                    continue
                if attempt and serving_since is None:
                    recovery_failed(attempt, 'replacement cloudflared exited with status ' + str(tunnel.returncode)
                                    + ' before the restarted bridge reported ready')
                else:
                    if serving_since is not None and time.monotonic() - serving_since >= BACKOFF_RESET_SECONDS:
                        attempt = 0
                    log('tunnel-lost', 'exit-code=' + str(tunnel.returncode)
                        + '; stopping the bridge so it deactivates the saved hook, then requesting a replacement URL')
                    terminate('bridge', 12)
                    terminate('cloudflared', 3)
            origin, ready, attempt = recover(attempt)
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
