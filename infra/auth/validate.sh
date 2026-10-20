#!/usr/bin/env bash
set -euo pipefail
root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
compiler=${BICEP_BIN:-bicep}
if ! command -v "$compiler" >/dev/null 2>&1; then
  if [[ -x "${HOME}/.azure/bin/bicep" ]]; then
    compiler="${HOME}/.azure/bin/bicep"
  else
    echo 'Install the standalone Bicep CLI, or set BICEP_BIN; validation never logs in or installs tools.' >&2
    exit 1
  fi
fi
build=$(mktemp -d)
trap 'rm -rf -- "$build"' EXIT
"$compiler" build "$root/main.bicep" --outfile "$build/main.json"
"$compiler" build "$root/runtime-rbac.bicep" --outfile "$build/runtime-rbac.json"
python3 "$root/validate_arm.py" "$build/main.json"
bash -n "$root/deploy.sh" "$root/validate.sh"
