"""Private persistent layout, setup endpoint and first Azure controller activation."""
import fcntl
import http.server
import json
import os
from pathlib import Path
import stat
import subprocess
import sys
import tempfile


def private_directory(path):
    path.mkdir(mode=0o700, exist_ok=True)
    info = path.lstat()
    if not stat.S_ISDIR(info.st_mode) or info.st_uid != os.getuid() or stat.S_IMODE(info.st_mode) != 0o700:
        raise ValueError('Expected an owned private directory: ' + str(path))


def private_file(info):
    if (not stat.S_ISREG(info.st_mode) or info.st_uid != os.getuid()
            or stat.S_IMODE(info.st_mode) != 0o600 or info.st_nlink != 1):
        raise ValueError('Expected an owned private regular file.')


def read_private_json(path):
    descriptor = os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK)
    with os.fdopen(descriptor) as saved:
        private_file(os.fstat(saved.fileno()))
        return json.load(saved)


def initialize(share, binding, *, setup_only=True):
    if share.is_symlink() or not share.is_dir():
        raise ValueError('Expected the mounted share directory.')
    # The dedicated share is trusted to this UID. Serialize competing initializers;
    # the native controller separately owns its process-lifetime state lease.
    descriptor = os.open(share / 'runtime.lock', os.O_CREAT | os.O_RDWR | os.O_NOFOLLOW, 0o600)
    with os.fdopen(descriptor, 'r+') as lock:
        private_file(os.fstat(lock.fileno()))
        fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        root = share / 'runtime'
        root.mkdir(mode=0o700, exist_ok=True)
        info = root.lstat()
        if (not stat.S_ISDIR(info.st_mode) or info.st_uid != os.getuid()
                or stat.S_IMODE(info.st_mode) not in (0o700, 0o711)):
            raise ValueError('Expected the owned runtime parent directory.')
        marker = root / 'binding.json'
        if marker.exists() or marker.is_symlink():
            if read_private_json(marker) != binding:
                raise ValueError('Persistent controller binding differs; refusing adoption.')
        elif any(root.iterdir()):
            raise ValueError('Refusing nonempty unbound runtime storage.')
        else:
            descriptor, temporary = tempfile.mkstemp(prefix='.binding-', dir=root)
            try:
                with os.fdopen(descriptor, 'w') as output:
                    json.dump(binding, output, sort_keys=True)
                    output.flush()
                    os.fsync(output.fileno())
                os.replace(temporary, marker)
                descriptor = os.open(root, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
                try:
                    os.fsync(descriptor)
                finally:
                    os.close(descriptor)
            finally:
                Path(temporary).unlink(missing_ok=True)
        for relative in ('data', 'workspace', 'data/config', 'data/config/skills', 'data/enrollment',
                         'data/credentials', 'data/credentials/codex', 'data/history', 'data/state',
                         'data/tools', 'data/control', 'data/participant',
                         'data/state/bridge.json.github-app', 'data/state/bridge.json.github-app/bin',
                         'data/state/bridge.json.github-app/clone-bin'):
            private_directory(root / relative)
        enrollment = root / 'data/enrollment/enrollment.json'
        if setup_only and (enrollment.exists() or enrollment.is_symlink()):
            raise ValueError('Already enrolled; setup cannot replace an active controller.')
        sessions = root / 'data/credentials/codex/sessions'
        if sessions.is_symlink():
            if os.readlink(sessions) != '/data/history':
                raise ValueError('Unexpected native history link.')
        elif sessions.exists():
            raise ValueError('Refusing to replace native session storage.')
        else:
            sessions.symlink_to('/data/history', target_is_directory=True)
        # Root squash maps the platform's mount helper to an unprivileged UID.
        # Search-only access lets it mount subpaths without exposing private data.
        root.chmod(0o711)


def prepare_runtime(settings, secret_path):
    """Prepare the existing image runtime without launching the native controller."""
    config = Path('/data/config/settings.json')
    configured = config.exists() or config.is_symlink()
    if configured:
        value = read_private_json(config)
        if not isinstance(value, dict) or any(value.get(key) != expected for key, expected in settings.items()):
            raise ValueError('Saved runtime settings differ; use the controlled configuration lifecycle.')
    with secret_path.open('rb') as source:
        enrollment = source.read(65537)
    if not 1 <= len(enrollment) <= 65536:
        raise ValueError('Expected 1..65536 bytes of private enrollment.')
    runtime = [sys.executable, '-E', '/opt/repoagent/runtime.py']
    if not configured:
        arguments = ['configure', '--clone', '--enrollment-file', '/data/enrollment/enrollment.json']
        for key, option in (
            ('repository', '--repository'), ('repositoryId', '--repository-id'),
            ('appId', '--app-id'), ('installationId', '--installation-id'),
            ('brokerUrl', '--broker-url'), ('mode', '--mode'), ('publicUrl', '--public-url'),
            ('mentionUser', '--mention-user'), ('model', '--model'), ('webSearch', '--web-search'),
            ('toolUpdates', '--tool-updates'), ('listenPort', '--listen-port'),
            ('reconcileInterval', '--reconcile-interval'),
        ):
            arguments.extend((option, str(settings[key])))
        if settings.get('azureWorkQueue'):
            arguments.extend(('--azure-work-queue', settings['azureWorkQueue']))
        try:
            subprocess.run(runtime + arguments, check=True)
        except subprocess.CalledProcessError:
            # Rejected URLs may contain credentials. Never include the command
            # from CalledProcessError in a bootstrap traceback.
            raise ValueError('Runtime configuration was refused.') from None
    # The existing transport secures the target. Only the native consumer owns
    # enrollment parsing, scope, expiry and broker authorization policy.
    subprocess.run(runtime + ['import-enrollment'], input=enrollment, check=True)


class SetupHealth(http.server.BaseHTTPRequestHandler):
    def __getattr__(self, name):
        # BaseHTTPRequestHandler dispatches through do_METHOD. Every method has
        # the same metadata-only route policy; none can configure the runtime.
        if name.startswith('do_'):
            return self.do_GET
        raise AttributeError(name)

    def setup(self):
        self.request.settimeout(5)
        super().setup()

    def do_GET(self):
        status = 200 if self.path == '/health/setup' else 503 if self.path == '/health/ready' else 404
        body = b'{"mode":"setup-required","controllerStarted":false}\n' if status != 404 else b''
        self.send_response(status)
        self.send_header('Content-Type', 'application/json')
        self.send_header('Cache-Control', 'no-store')
        self.send_header('Content-Length', str(len(body)))
        self.end_headers()
        if self.command != 'HEAD':
            self.wfile.write(body)

    def log_message(self, *_):
        pass  # No request paths, queries or client input in console logs.


if __name__ == '__main__':
    os.umask(0o077)
    if os.getuid() != 1000:
        raise ValueError('The controller runtime requires UID1000.')
    if sys.argv[1] in ('initialize', 'initialize-active'):
        initialize(Path('/mnt/controller'), json.loads(sys.argv[2]), setup_only=sys.argv[1] == 'initialize')
        print('Private persistent controller setup initialized; UID1000.', flush=True)
    elif sys.argv[1] == 'serve':
        print('Setup required; controller has not started.', flush=True)
        with http.server.HTTPServer(('0.0.0.0', 8080), SetupHealth) as server:
            server.serve_forever()
    elif sys.argv[1] == 'activate':
        prepare_runtime(json.loads(sys.argv[2]), Path('/run/enrollment/enrollment.json'))
        os.execv(sys.executable, [sys.executable, '-E', '/opt/repoagent/runtime.py', 'start'])
    else:
        raise ValueError('Expected initialize, initialize-active, serve or activate.')
