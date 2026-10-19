#!/usr/bin/env bash
set -euo pipefail
case "${1:-$(dpkg --print-architecture)}" in
  amd64) triplet=x86_64-unknown-linux-musl
    codex_sum=beb339e8ecd9ba057a6bb2dc81ad8ca474812edfbf0fc5056ca818797648dde4
    host_sum=314263128ba6592e7e807c265deeaf928a692b72dcbfeb5e0b20c9423b703529
    gh_sum=bb766f710eef8ede859c18578c72c327597cd4c8a85b06001b1f3843c6019386 ;;
  arm64) triplet=aarch64-unknown-linux-musl
    codex_sum=a7261e65f4a96d8f4ca65a069c6cf2dc2c33defc341e48078acefda9461fffe4
    host_sum=284356aefb197f00b76b912efdfdbae8ccf3ea1f89b976dde064b7ccb43214ef
    gh_sum=7862c86c72f43df3a2d93ddde6f473285b4e2af61b494849846827e513ef6484 ;;
  *) echo 'Only linux/amd64 and linux/arm64 are supported.' >&2; exit 1 ;;
esac
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
cd "$work"
for tool in codex codex-code-mode-host; do
  curl --fail --location --retry 3 -o "$tool.tar.gz" "https://github.com/openai/codex/releases/download/rust-v0.163.0/$tool-$triplet.tar.gz"
  if [[ "$tool" == codex ]]; then checksum=$codex_sum; else checksum=$host_sum; fi
  printf '%s  %s\n' "$checksum" "$tool.tar.gz" | sha256sum --check --status
  tar -xzf "$tool.tar.gz"
  install -m 755 "$tool-$triplet" "/usr/local/bin/$tool"
done
curl --fail --location --retry 3 -o gh.tar.gz "https://github.com/cli/cli/releases/download/v2.102.0/gh_2.102.0_linux_${1}.tar.gz"
printf '%s  gh.tar.gz\n' "$gh_sum" | sha256sum --check --status
tar -xzf gh.tar.gz
install -m 755 "gh_2.102.0_linux_${1}/bin/gh" /usr/local/bin/gh
codex --version
gh --version
