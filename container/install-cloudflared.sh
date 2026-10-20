#!/usr/bin/env bash
set -euo pipefail
architecture=${1:-$(dpkg --print-architecture)}
case "$architecture" in
  amd64) checksum=3bfc539f45d2620c71debb7a86be0efa737ed5ccbad9eb85c787116900e5c586 ;;
  arm64) checksum=2c65dfabf616c40efc7545d107d7a57ad5e21377e2964b7e88af19a238074a46 ;;
  *) echo 'Only linux/amd64 and linux/arm64 are supported.' >&2; exit 1 ;;
esac
mkdir -p /opt/repoagent/tools/bin
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
curl --fail --location --retry 3 -o "$work/cloudflared" "https://github.com/cloudflare/cloudflared/releases/download/2026.10.1/cloudflared-linux-$architecture"
printf '%s  %s\n' "$checksum" "$work/cloudflared" | sha256sum --check --status
install -m 755 "$work/cloudflared" /opt/repoagent/tools/bin/cloudflared
/opt/repoagent/tools/bin/cloudflared --version
