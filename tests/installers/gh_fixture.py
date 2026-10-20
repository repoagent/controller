#!/usr/bin/env python3
"""Scripted external gh responses; record literal argv before any delay/output."""
import json
import os
from pathlib import Path
import sys
import time

if 'HOST_ENROLLMENT_GH_MODEL' in os.environ:
    # Stateful external GitHub API only. No broker or enrollment policy lives here.
    model_path = Path(os.environ['HOST_ENROLLMENT_GH_MODEL'])
    model = json.loads(model_path.read_text(encoding='utf-8'))
    argv = sys.argv[1:]
    method = argv[argv.index('--method') + 1]
    endpoint = next(arg for arg in argv if arg.startswith('repos/'))
    body = json.load(sys.stdin) if '--input' in argv else None
    with Path(os.environ['REVOKE_GH_LOG']).open('a', encoding='utf-8') as stream:
        stream.write(json.dumps({'argv': argv, 'body': body}) + '\n')
    status, value = 500, {}
    if endpoint == 'repos/example/project' and method == 'GET':
        status, value = 200, {'id': 123, 'full_name': 'example/project'}
    elif endpoint.startswith('repos/example/project/agents/variables?') and method == 'GET':
        entries = [{'name': name, 'value': value} for name, value in model['variables'].items()]
        status = model.get('listing_status', 200)
        value = model.get('listing', {'total_count': len(entries), 'variables': entries})
    elif endpoint == 'repos/example/project/agents/variables' and method == 'POST':
        model['variables'][body['name']] = body['value']
        model_path.write_text(json.dumps(model), encoding='utf-8')
        status = model.get('write_status', 201)
    elif endpoint.startswith('repos/example/project/agents/variables/'):
        name = endpoint.rsplit('/', 1)[1]
        if method == 'GET':
            status = 200 if name in model['variables'] else 404
            value = {'name': name, 'value': model['variables'].get(name, '')}
        elif method == 'DELETE':
            model['variables'].pop(name, None)
            model_path.write_text(json.dumps(model), encoding='utf-8')
            status = 204
    print(f'HTTP/2.0 {status} Fixture\r\nContent-Type: application/json\r\n\r\n' + json.dumps(value))
    sys.exit(0 if status < 400 else 1)

scenario = json.loads(Path(os.environ['REVOKE_GH_SCENARIO']).read_text(encoding='utf-8'))
log = Path(os.environ['REVOKE_GH_LOG'])
events = log.read_text(encoding='utf-8').splitlines() if log.exists() else []
index = len(events)
with log.open('a', encoding='utf-8') as stream:
    stream.write(json.dumps({'argv': sys.argv[1:], 'pid': os.getpid()}) + '\n')
if index >= len(scenario):
    sys.exit(99)
response = scenario[index]
if 'replace_value' in response:
    Path(os.environ['REVOKE_REMOTE_VALUE']).write_text(response['replace_value'], encoding='utf-8')
if response.get('delete_value'):
    Path(os.environ['REVOKE_REMOTE_VALUE']).unlink(missing_ok=True)
# Delay after recording a request models a possibly transmitted DELETE.
time.sleep(response.get('delay', 0))
if 'stdout_bytes' in response or 'stderr_bytes' in response:
    # Alternate streams so a combined output bound is exercised without deadlock.
    for offset in range(0, max(response.get('stdout_bytes', 0), response.get('stderr_bytes', 0)), 4096):
        for name in ('stdout', 'stderr'):
            size = min(4096, max(0, response.get(name + '_bytes', 0) - offset))
            getattr(sys, name).buffer.write(b'x' * size)
            getattr(sys, name).buffer.flush()
    time.sleep(response.get('after_output_delay', 0))
else:
    body = response.get('body', '')
    if not isinstance(body, str):
        body = json.dumps(body, ensure_ascii=False)
    wire = response.get('raw', f"HTTP/2.0 {response.get('status', 200)} Fixture\r\nContent-Type: application/json\r\n\r\n{body}")
    sys.stdout.buffer.write(wire.encode('utf-8'))
    sys.stdout.buffer.flush()
    sys.stderr.write(response.get('stderr', ''))
sys.exit(response.get('exit', 0))
