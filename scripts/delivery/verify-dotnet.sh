#!/usr/bin/env bash
# Compile and format every project, including credential-dependent live tests (without running them).
set -euo pipefail
cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.."
python3 - <<'PYTHON'
import json
from pathlib import Path
import xml.etree.ElementTree as ET
root_policy = json.loads(Path('global.json').read_text())
if root_policy != json.loads(Path('service/auth/global.json').read_text()):
    raise SystemExit('Root and broker SDK policies must match exactly')
projects = {node.attrib['Path'] for node in ET.parse('RepoAgent.slnx').getroot().findall('Project')}
# This gate also runs from git archives during broker bootstrap, without a .git directory.
found = {path.as_posix() for base in ('src', 'tests', 'service/auth')
         for path in Path(base).rglob('*.csproj')
         if not {'bin', 'obj', '.claude', '.codex', '.local', 'artifacts'}.intersection(path.parts)}
if projects != found:
    raise SystemExit('RepoAgent.slnx must cover every C# project in src, tests and service/auth')
PYTHON
dotnet build RepoAgent.slnx --configuration Release --nologo
dotnet format RepoAgent.slnx --no-restore --verify-no-changes --severity warn
