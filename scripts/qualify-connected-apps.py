#!/usr/bin/env python3
"""Manual pinned-runtime qualification; no credentials, model turns or account writes (ADR 0010)."""
import argparse
import json
import os
from pathlib import Path
import select
import subprocess
import tempfile
import time


def observe(executable, root, home, overrides):
    arguments = [str(executable), 'app-server']
    for key, value in overrides.items():
        arguments += ['-c', key + '=' + json.dumps(value)]
    environment = dict(PATH=os.environ['PATH'], HOME=str(home), CODEX_HOME=str(home), LANG='C.UTF-8')
    process = subprocess.Popen(arguments, cwd=root, env=environment, stdin=subprocess.PIPE,
                               stdout=subprocess.PIPE, stderr=subprocess.DEVNULL)
    buffer = b''

    def request(identifier, method, params):
        nonlocal buffer
        process.stdin.write((json.dumps(dict(id=identifier, method=method, params=params)) + '\n').encode())
        process.stdin.flush()
        deadline = time.monotonic() + 20
        while time.monotonic() < deadline:
            if b'\n' not in buffer:
                if not select.select([process.stdout], [], [], max(0, deadline - time.monotonic()))[0]:
                    break
                chunk = os.read(process.stdout.fileno(), 65536)
                if not chunk:
                    raise RuntimeError('Runtime closed before ' + method)
                buffer += chunk
            while b'\n' in buffer:
                line, buffer = buffer.split(b'\n', 1)
                response = json.loads(line)
                if response.get('id') == identifier:
                    if 'error' in response:
                        raise RuntimeError('Runtime refused ' + method)
                    return response['result']
        raise TimeoutError(method)

    try:
        request(1, 'initialize', dict(clientInfo=dict(name='repoagent_app_qualification', version='1'),
                                    capabilities=dict(experimentalApi=True)))
        process.stdin.write(b'{"method":"initialized"}\n')
        process.stdin.flush()
        return request(2, 'config/read', dict(cwd=str(root)))['config']
    finally:
        process.stdin.close()
        process.kill()
        process.wait(timeout=5)
        process.stdout.close()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--codex', required=True, type=Path)
    args = parser.parse_args()
    executable = args.codex.resolve(strict=True)
    pinned = json.loads((Path(__file__).resolve().parents[1] / 'image/default.json').read_text())['agent']['version']
    with tempfile.TemporaryDirectory(prefix='repoagent-app-qualification-') as temporary:
        directory = Path(temporary)
        home, root = directory / 'home', directory / 'repository'
        home.mkdir(mode=0o700)
        root.mkdir()
        (root / '.git').mkdir()
        (root / '.codex').mkdir()
        original = '[projects.' + json.dumps(str(root)) + ']\ntrust_level="trusted"\n[apps.gmail]\nenabled=true\n[apps.github]\nenabled=false\n'
        (home / 'config.toml').write_text(original)
        (root / '.codex/config.toml').write_text('[features]\napps=true\n[apps.future_app]\nenabled=true\n')
        version = subprocess.run([str(executable), '--version'], env=dict(PATH=os.environ['PATH'], HOME=str(home), CODEX_HOME=str(home)),
                                 capture_output=True, text=True, check=True, timeout=10).stdout.strip()
        if version != 'codex-cli ' + pinned:
            raise RuntimeError('Use the image/default.json pinned runtime: ' + pinned)
        initial = observe(executable, root, home, {'features.apps': False, 'apps._default.enabled': False})
        if initial['features']['apps'] is not False:
            raise RuntimeError('Disabled bootstrap gate was not honored')
        for mode in ('disabled', 'selected', 'all'):
            overrides = {'features.apps': mode != 'disabled', 'apps._default.enabled': mode == 'all'}
            for app in initial['apps']:
                if app != '_default':
                    overrides['apps.' + app + '.enabled'] = mode == 'all' or (mode == 'selected' and app == 'github')
            config = observe(executable, root, home, overrides)
            expected = dict(gmail=mode == 'all', github=mode != 'disabled', future_app=mode == 'all')
            if config['features']['apps'] != (mode != 'disabled') or config['apps']['_default']['enabled'] != (mode == 'all'):
                raise RuntimeError('Effective feature/default mismatch: ' + mode)
            if any(config['apps'][app]['enabled'] != allowed for app, allowed in expected.items()):
                raise RuntimeError('Merged app overrides widened policy: ' + mode)
            print('PASS ' + version + ' mode=' + mode + ' stale user/project app settings overridden')
        if (home / 'config.toml').read_text() != original:
            raise RuntimeError('Qualification changed user config')
    print('No credentials, model turns or live Gmail acceptance used.')


if __name__ == '__main__':
    main()
