#!/usr/bin/env bash
set -euo pipefail
command -v pwsh >/dev/null # Installer suite otherwise silently omits PowerShell.
results=$(mktemp)
trap 'rm -f "$results"' EXIT
dotnet run --project tests/RepoAgentController.Tests/RepoAgentController.Tests.csproj --configuration Release -- -result-xml "$results"
python3 - "$results" <<'PYTHON'
import sys
import xml.etree.ElementTree as ET
assemblies = ET.parse(sys.argv[1]).getroot().findall('assembly')
if not assemblies or sum(int(a.attrib['total']) for a in assemblies) == 0:
    sys.exit('Controller test runner discovered zero tests')
PYTHON
python3 tests/installers/test_installers.py
python3 -m unittest discover -s scripts/delivery -p 'test_*.py'
bash scripts/delivery/package-controller.sh "${2:-0.0.0-ci}" "${1:?Output directory required}"
