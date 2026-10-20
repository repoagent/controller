"""Own a temporary, accountless tunnel and the bridge for one container lifetime."""
import datetime
import os
import queue
import re
import signal
import subprocess
import threading
import time


def log(event, message):
    print(datetime.datetime.now(datetime.timezone.utc).isoformat() + ' ' + event + ' ' + message, flush=True)


def run(bridge, port):
    """Discover a connected tunnel, then launch the existing receiver with its origin."""
    stopping = threading.Event()
    previous = {}
    processes = []
    messages = queue.SimpleQueue()

    def stop(signum, frame):
        stopping.set()

    def output(process):
        for line in process.stdout:
            url = re.search(r'https://[a-z0-9][a-z0-9-]*\.trycloudflare\.com(?=[\s/]|$)', line)
            if url:
                messages.put(('url', url.group()))
            if 'Registered tunnel connection' in line:
                messages.put(('connected', None))
            if ' ERR ' in line or ' WRN ' in line:
                log('tunnel-diagnostic', line.strip()[:800])

    def terminate(process, grace):
        # Signal only the bridge first: it must disable its hook before ingress stops.
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

    try:
        for signum in (signal.SIGINT, signal.SIGTERM):
            previous[signum] = signal.signal(signum, stop)
        tunnel = subprocess.Popen([
            'cloudflared', 'tunnel', '--no-autoupdate', '--protocol', 'http2',
            '--url', 'http://127.0.0.1:' + str(port)
        ], stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, start_new_session=True)
        processes.append(tunnel)
        reader = threading.Thread(target=output, args=(tunnel,), daemon=True)
        reader.start()
        origin, connected = None, False
        deadline = time.monotonic() + 90
        log('tunnel-starting', 'mode=quick-tunnel temporary-hostname=true')
        while not (origin and connected):
            if stopping.is_set():
                return 0
            if tunnel.poll() is not None:
                raise RuntimeError('cloudflared exited before a connected public URL was available; check network access and Cloudflare availability, then restart.')
            if time.monotonic() >= deadline:
                raise RuntimeError('No connected Quick Tunnel URL within 90 seconds; check network access and Cloudflare availability, then restart.')
            try:
                kind, value = messages.get(timeout=0.2)
                if kind == 'url':
                    origin = value
                elif kind == 'connected':
                    connected = True
            except queue.Empty:
                pass
        if stopping.is_set():
            return 0
        log('tunnel-connected', 'mode=quick-tunnel public-url=' + origin)
        agent = subprocess.Popen(bridge + ['--webhook-url', origin], start_new_session=True)
        processes.append(agent)
        while not stopping.wait(0.2):
            if tunnel.poll() is not None:
                raise RuntimeError('cloudflared exited; stopping the bridge. Restart to obtain a new URL and update the saved hook.')
            if agent.poll() is not None:
                raise RuntimeError('Bridge exited with status ' + str(agent.returncode) + '; stopping its Quick Tunnel. Inspect the preceding bridge error before restarting.')
        return 0
    except (OSError, RuntimeError, subprocess.SubprocessError) as error:
        log('tunnel-error', str(error))
        return 1
    finally:
        for process in reversed(processes):
            terminate(process, 12 if process is not processes[0] else 3)
            label = "bridge" if process is not processes[0] else "cloudflared"
            log("tunnel-process-stopped", "process=" + label + " exit-code=" + str(process.returncode))
        for signum, handler in previous.items():
            signal.signal(signum, handler)
        if processes:
            log('tunnel-stopped', 'bridge-and-tunnel-cleaned-up=true')
