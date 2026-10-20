#!/usr/bin/env python3
"""Process-boundary double for installer decisions, not Docker/provider compatibility."""
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile

path = Path(os.environ['INSTALLER_DOCKER_STATE'])
state = json.loads(path.read_text()) if path.exists() else {'settings': {}, 'events': [], 'authenticated': False, 'running': False}
arguments = sys.argv[1:]
state['events'].append(arguments)


def finish(code=0, output=''):
    path.write_text(json.dumps(state))
    if output:
        print(output)
    sys.exit(code)


command = arguments[0]
if command == 'info':
    finish(output=os.environ.get('INSTALLER_DOCKER_OS', 'linux'))
if command == 'inspect':
    finish(output='true' if state['running'] else 'false')
if command == 'build':
    context = Path(arguments[-1])
    state.setdefault('builds', []).append({
        'arguments': arguments,
        'files': sorted(str(file.relative_to(context)) for file in context.rglob('*') if file.is_file()),
        'inventory': json.loads((context / 'image-inventory.json').read_text()),
        'dockerfile': (context / 'container/Containerfile').read_text(),
        'tool_artifacts': json.loads((context / 'tool-artifacts.json').read_text()),
        'derived': (context / 'Derived.Dockerfile').read_text() if (context / 'Derived.Dockerfile').exists() else None,
    })
    finish(output='Fixture image built')
if command == 'image' and arguments[1:3] == ['inspect', '--format']:
    reference = arguments[4]
    if reference.startswith('sha256:'):
        finish(output=os.environ.get('INSTALLER_DOCKER_LOCAL_ID_RESPONSE', reference))
    finish(output=state.get('image_tags', {}).get(reference, 'sha256:fixture-image-id'))
if command == 'tag':
    source, alias = arguments[1:]
    state.setdefault('image_tags', {})[alias] = (
        os.environ.get('INSTALLER_DOCKER_TAG_ID_RESPONSE', source))
    finish()
if command == 'rm':
    finish()
if command == 'stop':
    state['running'] = False
    finish()
if command == 'logs':
    finish(output='2026-10-20T00:00:00Z ready repository=example/project actor=example[bot] mode=polling')
if command != 'run':
    finish(2, 'Unexpected Docker fixture command')

index = 1
options = {}
mounts = []
while arguments[index].startswith('-'):
    option = arguments[index]
    if option in ('--mount', '--name', '--entrypoint', '--publish', '--network', '--user'):
        options[option] = arguments[index + 1]
        if option == '--mount':
            mounts.append(arguments[index + 1])
        index += 2
    else:
        index += 1
image, *operation = arguments[index:]
if image.startswith('python@sha256:') and operation[:1] == ['/source/scripts/compose-image.py']:
    bindings = {}
    for mount in mounts:
        parts = dict(part.split('=', 1) for part in mount.split(',') if '=' in part)
        bindings[parts['dst']] = parts['src']
    state.setdefault('helper_mounts', []).append(bindings)
    with tempfile.TemporaryDirectory(prefix='docker-helper-files-') as directory:
        helper_root = Path(directory)
        for destination, source in bindings.items():
            if destination == '/output':
                continue
            target = helper_root / destination.lstrip('/')
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(source, target)
        state.setdefault('helper_visible_files', []).append(sorted(
            str(file.relative_to(helper_root)) for file in helper_root.rglob('*') if file.is_file()))
        def helper_path(value):
            if value == '/output' or value.startswith('/output/'):
                return bindings['/output'] + value[len('/output'):]
            if value.startswith(('/source/', '/input/')) or value in ('/source', '/input'):
                return str(helper_root / value.lstrip('/'))
            return value
        helper_args = [helper_path(value) for value in operation[1:]]
        source = helper_root / operation[0].lstrip('/')
        result = subprocess.run([sys.executable, str(source), *helper_args], text=True, capture_output=True)
    if result.stdout:
        print(result.stdout, end='')
    if result.stderr:
        print(result.stderr, end='', file=sys.stderr)
    finish(result.returncode)
if options.get('--entrypoint') == 'sh':
    finish(1 if os.environ.get('INSTALLER_UNREADABLE_KEY') else 0)
if options.get('--entrypoint') in ('python3', '/usr/bin/python3'):
    state['authenticated'] = True
    finish()
if operation == ['versions']:
    finish(output='Fixture Codex and gh versions')
if operation[0] == 'get':
    if not state['settings']:
        finish(1)
    finish(output=str(state['settings'].get(operation[1], '')))
if operation[0] == 'configure':
    fields = {'repository': 'repository', 'app-id': 'appId', 'key-path': 'keyPath',
              'mention-user': 'mentionUser', 'model': 'model', 'mode': 'mode',
              'installation-id': 'installationId', 'tool-updates': 'toolUpdates',
              'web-search': 'webSearch', 'listen-port': 'listenPort',
              'reconcile-interval': 'reconcileInterval', 'public-url': 'publicUrl'}
    settings = {}
    remaining = iter(operation[1:])
    for argument in remaining:
        key, separator, value = argument.removeprefix('--').partition('=')
        settings[fields[key]] = value if separator else next(remaining)
    if state['settings'] and any(state['settings'][key] != settings[key] for key in ('repository', 'appId')):
        finish(1, 'Changing repository/App requires a new instance')
    state['settings'] = settings
    finish(output='Configured fixture repository')
if operation[0] == 'login':
    if not state['authenticated'] and '--non-interactive' in operation:
        finish(1, 'No valid container Codex sign-in; supply an auth.json or rerun interactively')
    state['authenticated'] = True
    finish(output='Logged in')
if operation[0] == 'check':
    finish(1 if os.environ.get('INSTALLER_CHECK_FAILURE') else 0, 'Fixture App check')
if operation[0] == 'start':
    state['running'] = True
    state['runtime_mounts'] = mounts
    finish(output=options['--name'])
finish(2, 'Unexpected Docker fixture operation')
