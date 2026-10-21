#!/usr/bin/env python3
"""Scenario-driven Azure CLI boundary, never connects to Azure.

Unknown commands fail closed. Fixtures provide Azure-shaped responses, while the
journal captures the actual command and parameter file before it disappears.
"""
import json
import os
from pathlib import Path
import sys
import termios
import tty

args = sys.argv[1:]
scenario = json.loads(Path(os.environ['AZ_DOUBLE_SCENARIO']).read_text())
record = {'args': args}
command = ' '.join(args[:3]) if args[0] == 'deployment' or args[:2] in (['containerapp', 'revision'], ['containerapp', 'replica']) else ' '.join(args[:2])
terminal_error = None
if command == 'containerapp exec':
    # Azure CLI's Unix exec transport requires terminal stdin even in CI.
    # Exercise that actual OS precondition instead of granting a health marker.
    record['stdinIsTty'] = sys.stdin.isatty()
    previous = termios.tcgetattr(sys.stdin.fileno()) if record['stdinIsTty'] else None
    try:
        tty.setcbreak(sys.stdin.fileno())
        record['terminalSetup'] = 'ok'
    except (OSError, termios.error) as error:
        terminal_error = str(error)
        record['terminalSetup'] = 'error'
        record['terminalError'] = terminal_error
    finally:
        if previous is not None:
            termios.tcsetattr(sys.stdin.fileno(), termios.TCSADRAIN, previous)
            record['terminalRestored'] = termios.tcgetattr(sys.stdin.fileno()) == previous
if '--parameters' in args:
    record['parameters'] = json.loads(Path(args[args.index('--parameters') + 1].removeprefix('@')).read_text())
def journal():
    with open(os.environ['AZ_DOUBLE_JOURNAL'], 'a') as stream:
        stream.write(json.dumps(record) + '\n')


if terminal_error is not None:
    journal()
    sys.exit('Unix exec terminal setup failed: ' + terminal_error)

response = scenario.get('responses', {}).get(command)
if isinstance(response, list):
    response = response.pop(0)
    Path(os.environ['AZ_DOUBLE_SCENARIO']).write_text(json.dumps(scenario))
if response is None:
    if command == 'group show':
        response = {'body': scenario['group']}
    elif command == 'resource show':
        resource_id = args[args.index('--ids') + 1]
        resource = scenario['resources'].get(resource_id)
        response = {'body': resource} if resource else {'error': '(ResourceNotFound) Resource absent', 'code': 3}
    elif command == 'deployment group validate':
        response = {'body': {'properties': {'provisioningState': 'Succeeded'}}}
    elif command == 'deployment group what-if':
        response = {'body': scenario['plan']}
        if 'postPlanResources' in scenario:
            scenario['resources'].update(scenario['postPlanResources'])
            Path(os.environ['AZ_DOUBLE_SCENARIO']).write_text(json.dumps(scenario))
    elif command == 'deployment group create':
        if 'postCreateResources' in scenario:
            scenario['resources'].update(scenario['postCreateResources'])
            Path(os.environ['AZ_DOUBLE_SCENARIO']).write_text(json.dumps(scenario))
        response = {'body': {'properties': {'provisioningState': 'Succeeded'}}}
    elif command == 'containerapp show':
        response = {'body': scenario['resources'][scenario['appId']]}
    else:
        response = {'error': 'Unsupported Azure command: ' + command, 'code': 99}
if 'error' in response:
    journal()
    print(response['error'], file=sys.stderr)
    sys.exit(response.get('code', 1))
body = response.get('body')
if (command == 'containerapp revision list' and 'raw' not in response and isinstance(body, list)
        and all(isinstance(item, dict) and isinstance(item.get('name'), str) and item['name']
                and isinstance(item.get('properties'), dict)
                and isinstance(item['properties'].get('active'), bool) for item in body)):
    # CLI defaults to active revisions only. Preserve malformed/error fixtures
    # verbatim so they still exercise the application's fail-closed handling.
    if '--all' not in args:
        body = [item for item in body if item['properties']['active']]
    record['revisionList'] = [{'name': item['name'], 'properties': {'active': item['properties']['active']}}
                              for item in body]
if '--query' in args:
    sys.path.insert(0, os.environ['TEST_JMESPATH_DIRECTORY'])
    import jmespath
    body = jmespath.search(args[args.index('--query') + 1], body)
journal()
print(response['raw'] if 'raw' in response else json.dumps(body))
