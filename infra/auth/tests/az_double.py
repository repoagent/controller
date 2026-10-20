#!/usr/bin/env python3
"""Scenario-driven Azure CLI boundary, never connects to Azure.

Unknown commands fail closed. Fixtures provide Azure-shaped responses, while the
journal captures the actual command and parameter file before it disappears.
"""
import json
import os
from pathlib import Path
import sys

args = sys.argv[1:]
scenario = json.loads(Path(os.environ['AZ_DOUBLE_SCENARIO']).read_text())
record = {'args': args}
if '--parameters' in args:
    record['parameters'] = json.loads(Path(args[args.index('--parameters') + 1].removeprefix('@')).read_text())
with open(os.environ['AZ_DOUBLE_JOURNAL'], 'a') as journal:
    journal.write(json.dumps(record) + '\n')

command = ' '.join(args[:3]) if args[0] == 'deployment' else ' '.join(args[:2])
response = scenario.get('responses', {}).get(command)
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
    elif command == 'deployment group create':
        response = {'body': {'properties': {'provisioningState': 'Succeeded'}}}
    else:
        response = {'error': 'Unsupported Azure command: ' + command, 'code': 99}
if 'error' in response:
    print(response['error'], file=sys.stderr)
    sys.exit(response.get('code', 1))
print(response['raw'] if 'raw' in response else json.dumps(response['body']))
