#!/usr/bin/env bash
set -euo pipefail
# Use a signed immutable Ubuntu snapshot without changing the base's repositories.
snapshot=${2:?A reviewed Ubuntu snapshot is required}
cat > /tmp/repoagent.sources <<SOURCES
Types: deb
URIs: https://snapshot.ubuntu.com/ubuntu/$snapshot/
Suites: resolute resolute-updates resolute-security
Components: main universe
Signed-By: /usr/share/keyrings/ubuntu-archive-keyring.gpg
SOURCES
apt_options=(-o Dir::Etc::sourcelist=/tmp/repoagent.sources -o Dir::Etc::sourceparts=-)
apt-get "${apt_options[@]}" update
packages=(bash ca-certificates curl git python3 python3-yaml jq procps libicu78 libssl3t64 libstdc++6 zlib1g)
if [[ ${1:-false} == true ]]; then packages+=(ripgrep file patch diffutils unzip zip less openssh-client); fi
apt-get "${apt_options[@]}" install -y --no-install-recommends --no-upgrade "${packages[@]}"
rm /tmp/repoagent.sources
rm -rf /var/lib/apt/lists/*
