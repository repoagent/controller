#!/usr/bin/env bash
set -euo pipefail
architecture=${1:-$(dpkg --print-architecture)}
[[ "$architecture" == amd64 ]] || { echo 'Only linux/amd64 is supported.' >&2; exit 1; }
mkdir -p /opt/repoagent/tools/bin
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
url=$(jq -er '.cloudflared.url' /tmp/tool-artifacts.json)
checksum=$(jq -er '.cloudflared.sha256' /tmp/tool-artifacts.json)
curl --fail --location --retry 3 -o "$work/cloudflared" "$url"
printf '%s  %s\n' "$checksum" "$work/cloudflared" | sha256sum --check --status
install -m 755 "$work/cloudflared" /opt/repoagent/tools/bin/cloudflared
HOME="$work/home" /opt/repoagent/tools/bin/cloudflared --version
