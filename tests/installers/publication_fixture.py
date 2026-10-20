#!/usr/bin/env python3
"""Scripted Docker transport; real argv, pipes and exit codes, no registry claim."""
import json
import os
from pathlib import Path
import sys
import time

scenario = json.loads(Path(os.environ['PUBLISH_SCENARIO']).read_text(encoding='utf-8'))
log = Path(os.environ['PUBLISH_LOG'])
index = len(log.read_text(encoding='utf-8').splitlines()) if log.exists() else 0
with log.open('a', encoding='utf-8') as stream:
    stream.write(json.dumps({'argv': sys.argv[1:], 'pid': os.getpid()}) + '\n')
if index >= len(scenario):
    sys.exit(99)
response = scenario[index]
if 'stdout_bytes' in response or 'stderr_bytes' in response:
    for offset in range(0, max(response.get('stdout_bytes', 0), response.get('stderr_bytes', 0)), 4096):
        for name in ('stdout', 'stderr'):
            size = min(4096, max(0, response.get(name + '_bytes', 0) - offset))
            getattr(sys, name).buffer.write(b'x' * size)
            getattr(sys, name).buffer.flush()
else:
    body = response.get('body', '')
    sys.stdout.write(body if isinstance(body, str) else json.dumps(body))
    sys.stdout.flush()
    sys.stderr.write(response.get('stderr', ''))
    sys.stderr.flush()
time.sleep(response.get('delay', 0))
sys.exit(response.get('exit', 0))
