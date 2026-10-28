#!/usr/bin/env bash
# Run the Python verification suite with its exact dependency, without global installs.
set -euo pipefail
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
python3 -m venv --without-pip "$work/python"
python3 - "$work/jmespath.whl" <<'PYTHON'
import hashlib
from pathlib import Path
import sys
import urllib.request

url = 'https://files.pythonhosted.org/packages/31/b4/b9b800c45527aadd64d5b442f9b932b00648617eb5d63d2c7a6587b7cafc/jmespath-1.0.1-py3-none-any.whl'
with urllib.request.urlopen(url, timeout=30) as response:
    wheel = response.read(1024 * 1024 + 1)
if len(wheel) > 1024 * 1024 or hashlib.sha256(wheel).hexdigest() != '02e2e4cc71b5bcab88332eebf907519190dd9e6e82107fa7f83b1003a6252980':
    sys.exit('Python verification dependency checksum mismatch')
Path(sys.argv[1]).write_bytes(wheel)
PYTHON
# JMESPath is pure Python; Python imports the pinned wheel as a ZIP path. No pip,
# ensurepip or global package mutation is needed, including on minimal Linux hosts.
PYTHONPATH="$work/jmespath.whl" "$work/python/bin/python" -m unittest "$@"
