#!/usr/bin/env python3
"""Scenario-driven Azure CLI boundary, never connects to Azure.

Unknown commands fail closed. Fixtures provide Azure-shaped responses, while the
journal captures the actual command and parameter file before it disappears.
"""
import json
import os
from pathlib import Path
import shlex
import subprocess
import sys
import tempfile
import termios
import time
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
    record['stdoutIsTty'] = sys.stdout.isatty()
    record['stderrIsTty'] = sys.stderr.isatty()
    record['pid'] = os.getpid()
    record['parentPid'] = os.getppid()
    record['terminalName'] = os.ttyname(sys.stdin.fileno()) if record['stdinIsTty'] else None
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
if '--template-file' in args:
    record['template'] = json.loads(Path(args[args.index('--template-file') + 1]).read_text())
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
        record['responseCode'] = response.get('code', 0)
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
if command == 'containerapp exec' and 'health' in response:
    with tempfile.TemporaryDirectory(prefix='auth-health-tools-') as directory:
        root = Path(directory)
        requests = root / 'requests.jsonl'
        # Independent tool outcomes only; the actual outbound shell owns probes,
        # failure propagation and its marker. No real network executable is on PATH.
        tool = '''import json, os, pathlib, sys
with open(os.environ['HEALTH_REQUESTS'], 'a') as stream:
    stream.write(json.dumps(dict(tool=pathlib.Path(sys.argv[0]).name, url=sys.argv[-1])) + '\\n')
sys.exit(json.loads(os.environ['HEALTH_OUTCOMES']).get(sys.argv[-1], 0))
'''
        for name in response['health'].get('tools', ['curl', 'wget']):
            executable = root / name
            executable.write_text('#!' + sys.executable + '\n' + tool)
            executable.chmod(0o700)
        result = subprocess.run(shlex.split(args[args.index('--command') + 1]), capture_output=True, text=True,
                                env={**os.environ, 'PATH': directory, 'HEALTH_REQUESTS': str(requests),
                                     'HEALTH_OUTCOMES': json.dumps(response['health'].get('outcomes', {}))})
        record['healthRequests'] = [json.loads(line) for line in requests.read_text().splitlines()] if requests.exists() else []
    journal()
    sys.stdout.write(result.stdout)
    sys.stderr.write(result.stderr)
    sys.exit(result.returncode)
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
if 'stderr' in response:
    print(response['stderr'], file=sys.stderr, flush=True)
print(response['raw'] if 'raw' in response else json.dumps(body), flush=True)
if 'delay' in response:
    time.sleep(response['delay'])
