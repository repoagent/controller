#!/usr/bin/env bash
set -euo pipefail
architecture=${1:-$(dpkg --print-architecture)}
[[ "$architecture" == amd64 ]] || { echo 'Only linux/amd64 is supported.' >&2; exit 1; }
mkdir -p /opt/repoagent/tools/bin
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
cd "$work"
# Metadata is generated from the same reviewed catalog as the final inventory.
artifacts=/tmp/tool-artifacts.json
for tool in codex codex-code-mode-host gh; do
  url=$(jq -er --arg tool "$tool" '.[$tool].url' "$artifacts")
  checksum=$(jq -er --arg tool "$tool" '.[$tool].sha256' "$artifacts")
  version=$(jq -er --arg tool "$tool" '.[$tool].version' "$artifacts")
  curl --fail --location --retry 3 -o "$tool.tar.gz" "$url"
  printf '%s  %s\n' "$checksum" "$tool.tar.gz" | sha256sum --check --status
  tar -xzf "$tool.tar.gz"
  if [[ "$tool" == gh ]]; then executable="gh_${version}_linux_amd64/bin/gh"
  else executable="$tool-x86_64-unknown-linux-musl"; fi
  install -m 755 "$executable" "/opt/repoagent/tools/bin/$tool"
done
HOME="$work/home" CODEX_HOME="$work/codex" /opt/repoagent/tools/bin/codex --version
GH_CONFIG_DIR="$work/gh" /opt/repoagent/tools/bin/gh --version
