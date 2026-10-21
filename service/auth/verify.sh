#!/usr/bin/env bash
set -euo pipefail
cd -- "$(dirname -- "${BASH_SOURCE[0]}")"
dotnet build dev/RepoAgent.Auth.AppHost/RepoAgent.Auth.AppHost.csproj --configuration Release --nologo

image='mcr.microsoft.com/azure-storage/azurite@sha256:830430c1da1a2d537e08f3e6764dd1f5ae00cf0346bcaf625b968ec3f0971fd5'
results=$(mktemp -d)
container=''
cleanup() {
  if [[ -n "$container" ]]; then docker rm --force "$container" >/dev/null; fi
  rm -rf -- "$results"
}
trap cleanup EXIT
container=$(docker run --detach --publish 127.0.0.1::10002 "$image" \
  azurite-table --tableHost 0.0.0.0 --location /data)
port=$(docker port "$container" 10002/tcp)
export AUTH_TEST_TABLE_ENDPOINT="http://${port}/devstoreaccount1"
# Wait for the process to listen, with a hard startup deadline; credentials/data are test-only.
python3 - "$port" <<'PY'
import socket, sys, time
host, port = sys.argv[1].rsplit(':', 1)
deadline = time.monotonic() + 30
while True:
    try:
        with socket.create_connection((host, int(port)), timeout=1):
            break
    except OSError:
        if time.monotonic() >= deadline:
            raise SystemExit('Azurite did not start within 30 seconds')
        time.sleep(0.1)
PY
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
print(f"Verified {counts.attrib['executed']} executed tests against pinned Azurite.")
PY
