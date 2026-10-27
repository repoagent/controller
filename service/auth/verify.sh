#!/usr/bin/env bash
set -euo pipefail
cd -- "$(dirname -- "${BASH_SOURCE[0]}")"
bash ../../scripts/delivery/verify-dotnet.sh

results=$(mktemp -d)
trap 'rm -rf -- "$results"' EXIT
dotnet run --project tests/RepoAgent.Auth.Tests/RepoAgent.Auth.Tests.csproj \
  --configuration Release -- -result-trx "$results/tests.trx"
python3 - "$results/tests.trx" <<'PY'
import sys, xml.etree.ElementTree as ET
root = ET.parse(sys.argv[1]).getroot()
counts = root.find('.//{*}Counters')
if counts is None or int(counts.attrib.get('total', 0)) < 1 or int(counts.attrib.get('executed', 0)) < 1:
    raise SystemExit('Verification failed: no tests executed')
if int(counts.attrib.get('failed', 0)) != 0 or counts.attrib.get('passed') != counts.attrib.get('executed'):
    raise SystemExit('Verification failed: not every executed test passed')
print(f"Verified {counts.attrib['executed']} executed tests.")
PY
