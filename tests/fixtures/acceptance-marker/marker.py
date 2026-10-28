#!/usr/bin/env python3
"""A skill-owned action whose output is absent when this skill is not installed."""
import hashlib
import json
import sys
from pathlib import Path
import time

value = sys.argv[1]
if '--hold' in sys.argv[2:]:
    ready = Path('/workspace/hold-ready-' + value)
    deadline = time.monotonic() + 300
    ready.write_text(json.dumps({'expiresAt': time.time() + 300}))
    try:
        while not Path('/workspace/hold-release-' + value).exists():
            if time.monotonic() >= deadline:
                raise TimeoutError('Test runner did not release the agent action')
            time.sleep(0.2)
    finally:
        ready.unlink(missing_ok=True)
print('SKILL_ACTION_' + hashlib.sha256(('repoagent-skill-v1:' + value).encode()).hexdigest()[:16])
