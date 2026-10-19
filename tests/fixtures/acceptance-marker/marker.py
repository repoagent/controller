#!/usr/bin/env python3
"""A skill-owned action whose output is absent when this skill is not installed."""
import hashlib
import sys
from pathlib import Path
import time

value = sys.argv[1]
if '--hold' in sys.argv[2:]:
    Path('/workspace/hold-ready-' + value).write_text('ready')
    deadline = time.monotonic() + 90
    while not Path('/workspace/hold-release-' + value).exists():
        if time.monotonic() >= deadline:
            raise TimeoutError('Test runner did not release the native action')
        time.sleep(0.2)
print('SKILL_ACTION_' + hashlib.sha256(('repoagent-skill-v1:' + value).encode()).hexdigest()[:16])
