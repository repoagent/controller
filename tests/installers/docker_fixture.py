#!/usr/bin/env python3
"""Process-boundary double for installer decisions, not Docker/provider compatibility."""
import json
import os
from pathlib import Path
import sys

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
    finish(output='Fixture image built')
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
    if option in ('--mount', '--name', '--entrypoint', '--publish'):
        options[option] = arguments[index + 1]
        if option == '--mount':
            mounts.append(arguments[index + 1])
        index += 2
    else:
        index += 1
image, *operation = arguments[index:]
if options.get('--entrypoint') == 'sh':
    finish(1 if os.environ.get('INSTALLER_UNREADABLE_KEY') else 0)
if options.get('--entrypoint') == 'python3':
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
