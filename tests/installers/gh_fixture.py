#!/usr/bin/env python3
"""Scripted external gh responses; record literal argv before any delay/output."""
import json
import os
from pathlib import Path
import sys
import time

scenario = json.loads(Path(os.environ['REVOKE_GH_SCENARIO']).read_text())
log = Path(os.environ['REVOKE_GH_LOG'])
events = log.read_text().splitlines() if log.exists() else []
index = len(events)
with log.open('a') as stream:
    stream.write(json.dumps({'argv': sys.argv[1:], 'pid': os.getpid()}) + '\n')
if index >= len(scenario):
    sys.exit(99)
response = scenario[index]
if 'replace_value' in response:
    Path(os.environ['REVOKE_REMOTE_VALUE']).write_text(response['replace_value'])
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
