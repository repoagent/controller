#!/usr/bin/env bash
set -euo pipefail
bash scripts/delivery/verify-dotnet.sh
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
work=$(cd "$work" && pwd)
results="$work/results.xml"
export CHECKOUT_AGENT="$work/codex"
python3 - "$work" <<'PYTHON'
import hashlib, json, os, runpy, shutil, subprocess, sys, tarfile, urllib.request
from pathlib import Path
work = Path(sys.argv[1]).resolve()
version, url, checksum = runpy.run_path('scripts/compose-image.py')['ARTIFACTS']['codex']
if json.loads(Path('image/default.json').read_text())['agent']['version'] != version:
    sys.exit('Default Codex version differs from the reviewed artifact catalog')
archive = work / 'codex.tar.gz'
with urllib.request.urlopen(url, timeout=60) as response, archive.open('wb') as output:
    shutil.copyfileobj(response, output)
if hashlib.sha256(archive.read_bytes()).hexdigest() != checksum:
    sys.exit('Agent test prerequisite checksum mismatch')
with tarfile.open(archive, 'r:gz') as package:
    member = package.getmember('codex-x86_64-unknown-linux-musl')
    if not member.isfile():
        sys.exit('Agent test prerequisite must be a regular executable')
    with package.extractfile(member) as source, (work / 'codex').open('wb') as output:
        shutil.copyfileobj(source, output)
(work / 'codex').chmod(0o755)
home = work / 'home'
home.mkdir(mode=0o700)
result = subprocess.run([str(work / 'codex'), '--version'], check=True, capture_output=True,
                        text=True, env=dict(os.environ, CODEX_HOME=str(home)))
if result.stdout.strip() != 'codex-cli ' + version:
    sys.exit('Agent test prerequisite version mismatch')
print(result.stdout.strip(), hashlib.sha256((work / 'codex').read_bytes()).hexdigest())
PYTHON
dotnet run --project tests/RepoAgentController.Tests/RepoAgentController.Tests.csproj --configuration Release -- -result-xml "$results"
python3 - "$results" <<'PYTHON'
import sys
import xml.etree.ElementTree as ET
assemblies = ET.parse(sys.argv[1]).getroot().findall('assembly')
if not assemblies or sum(int(a.attrib['total']) for a in assemblies) == 0:
    sys.exit('Controller test runner discovered zero tests')
PYTHON
# Controller process suites, kept until #203 replaces them with unit tests.
python3 -m unittest discover -s tests/installers -p 'test_*.py'
bash scripts/delivery/test-python.sh discover -s scripts/delivery -p 'test_*.py'
bash scripts/delivery/package-controller.sh "${2:-0.0.0-ci}" "${1:?Output directory required}"
