#!/usr/bin/env bash
# Keep installation and `repoagent setup` on the same guided configuration path.
set -euo pipefail
root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
exec bash "$root/scripts/setup-wizard.sh" "$@"
