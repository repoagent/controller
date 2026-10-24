#!/usr/bin/env python3
"""Scripted external gh responses; record literal argv before any delay/output."""
import json
import os
from pathlib import Path
import sys
import time

if os.environ.get('HOST_ENROLLMENT_NATIVE_CONSUMER') == '1':
    # Actual consumer gh process boundary; never accept the operator credential.
    if os.environ.get('GH_TOKEN') != 'assembled-runtime-token' or sys.argv[1] != 'api':
        sys.exit(91)
    if sys.argv[2].startswith('repos/example/project/actions/variables/') and sys.argv[sys.argv.index('--method') + 1] == 'GET':
        # Repository-settings read with the brokered runtime credential; the fixture repository configures one setting.
        with Path(os.environ['REVOKE_GH_LOG']).open('a', encoding='utf-8') as stream:
            stream.write(json.dumps({'argv': sys.argv[1:], 'runtimeCredentialObserved': True}) + '\n')
        name = sys.argv[2].rsplit('/', 1)[1]
        if name != 'REPOAGENT_MAX_ACTIVE_SESSIONS':
            print('HTTP/2.0 404 Not Found\r\n\r\n{"message":"Not Found"}', end='')
            sys.exit(1)
        print('HTTP/2.0 200 OK\r\n\r\n' + json.dumps({'name': name, 'value': '3'}), end='')
        sys.exit(0)
    payload = json.load(sys.stdin)
    if 'viewer' not in payload.get('query', ''):
        sys.exit(92)
    with Path(os.environ['REVOKE_GH_LOG']).open('a', encoding='utf-8') as stream:
        stream.write(json.dumps({'argv': sys.argv[1:], 'runtimeCredentialObserved': True}) + '\n')
    print(json.dumps({'data': {'viewer': {'login': 'repo-agent-connector[bot]'}}}))
    sys.exit(0)

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
    # Direct repository Actions variables ('variables') and legacy Agent variables
    # ('agent_variables') are separate GitHub stores; neither falls back to the other.
    location = next((prefix for prefix in ('repos/example/project/actions/variables', 'repos/example/project/agents/variables')
                     if endpoint == prefix or endpoint.startswith((prefix + '?', prefix + '/'))), None)
    store = model.setdefault('agent_variables' if location and '/agents/' in location else 'variables', {})
    if endpoint == 'repos/example/project' and method == 'GET':
        status, value = 200, {'id': 123, 'full_name': 'example/project'}
    elif location and endpoint.startswith(location + '?') and method == 'GET':
        # GitHub pages these listings: default 10 and at most 30 per page.
        query = dict(pair.split('=', 1) for pair in endpoint.split('?', 1)[1].split('&') if pair)
        size, page = min(30, int(query.get('per_page', 10))), int(query.get('page', 1))
        entries = [{'name': name, 'value': value} for name, value in sorted(store.items())]
        status = model.get('listing_status', 200)
        value = model.get('listing', {'total_count': len(entries), 'variables': entries[(page - 1) * size:page * size]})
    elif location and endpoint == location and method == 'POST':
        status = model.get('write_status', 201)
        if not 400 <= status < 500:  # A rejection is never applied; a 5xx write may have been.
            store[body['name']] = body['value']
            model_path.write_text(json.dumps(model), encoding='utf-8')
    elif location and endpoint.startswith(location + '/'):
        name = endpoint.rsplit('/', 1)[1]
        if method == 'GET':
            status = model.get('read_status', {}).get(name, 200 if name in store else 404)
            value = {'name': name, 'value': store.get(name, '')} if status == 200 else {'message': 'Fixture'}
        elif method == 'PATCH':
            status = model.get('write_status', 204)
            if not 400 <= status < 500:
                store[name] = body['value']
                model_path.write_text(json.dumps(model), encoding='utf-8')
        elif method == 'DELETE':
            store.pop(name, None)
            model_path.write_text(json.dumps(model), encoding='utf-8')
            status = 204
    wire = f'HTTP/2.0 {status} Fixture\r\nContent-Type: application/json\r\n\r\n' + json.dumps(value) + '\n'
    sys.stdout.buffer.write(wire.encode('utf-8'))
    sys.stdout.buffer.flush()
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
