#!/usr/bin/env bash
set -euo pipefail
version=${1:?Usage: package-controller.sh VERSION OUTPUT_DIRECTORY}
output=${2:?Usage: package-controller.sh VERSION OUTPUT_DIRECTORY}
[[ $version =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$ ]] || { echo 'Invalid version' >&2; exit 1; }
root=$(git rev-parse --show-toplevel)
source_commit=$(git rev-parse HEAD)
mkdir -p "$output"
output=$(realpath "$output")
stage=$(mktemp -d)
trap 'rm -rf "$stage"' EXIT
name="repoagent-controller-$version-linux-amd64"
dotnet publish "$root/src/RepoAgentController/RepoAgentController.csproj" -c Release -r linux-x64 -p:PublishAot=true -p:Version="$version" -p:SourceRevisionId="$source_commit" -o "$stage/$name"
# Symbols are not required to execute the distribution.
rm -f "$stage/$name/RepoAgentController.dbg"
test -s "$stage/$name/github-discussion/SKILL.md"
"$stage/$name/RepoAgentController" --help
printf 'version=%s\nsource_commit=%s\nruntime=linux-x64\n' "$version" "$source_commit" > "$stage/$name/VERSION"
dotnet publish "$root/tests/NativeSmoke/NativeSmoke.csproj" -c Release -r linux-x64 -p:PublishAot=true -o "$stage/smoke"
"$stage/smoke/NativeSmoke"
(cd "$stage/$name" && sha256sum RepoAgentController github-discussion/SKILL.md VERSION > SHA256SUMS)
tar -C "$stage" -czf "$output/$name.tar.gz" "$name"
(cd "$output" && sha256sum "$name.tar.gz" > "$name.tar.gz.sha256")
