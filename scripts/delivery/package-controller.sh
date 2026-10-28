#!/usr/bin/env bash
set -euo pipefail
version=${1:?Usage: package-controller.sh VERSION OUTPUT_DIRECTORY}
output=${2:?Usage: package-controller.sh VERSION OUTPUT_DIRECTORY}
[[ $version =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$ ]] || { echo 'Invalid version' >&2; exit 1; }
root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)
source_commit=${SOURCE_COMMIT:-$(git rev-parse HEAD)}
[[ $source_commit =~ ^[0-9a-f]{40}$ ]] || { echo 'A full source commit is required' >&2; exit 1; }
mkdir -p "$output"
output=$(realpath "$output")
stage=$(mktemp -d)
trap 'rm -rf "$stage"' EXIT
name="repoagent-controller-$version-linux-amd64"
python3 "$root/scripts/delivery/controller-release.py" notes "$root" "$stage/RELEASE_NOTES.md" --version "$version"
dotnet publish "$root/src/RepoAgentController/RepoAgentController.csproj" -c Release -r linux-x64 -p:PublishAot=true -p:TreatWarningsAsErrors=true -p:Version="$version" -p:ControllerReleaseNotesPath="$stage/RELEASE_NOTES.md" -p:SourceRevisionId="$source_commit" -o "$stage/$name"
# Symbols are not required to execute the distribution.
rm -f "$stage/$name/RepoAgentController.dbg"
test -s "$stage/$name/github-discussion/SKILL.md"
test -s "$stage/$name/repoagent-orientation/SKILL.md"
"$stage/$name/RepoAgentController" --help
actual_version=$("$stage/$name/RepoAgentController" --version)
[[ "$actual_version" == "$version" || "$actual_version" == "$version+"* ]] || { echo 'Compiled version differs from package version' >&2; exit 1; }
cmp "$stage/RELEASE_NOTES.md" "$stage/$name/RELEASE_NOTES.md"
printf 'version=%s\nsource_commit=%s\nruntime=linux-x64\n' "$version" "$source_commit" > "$stage/$name/VERSION"
dotnet publish "$root/tests/NativeSmoke/NativeSmoke.csproj" -c Release -r linux-x64 -p:PublishAot=true -p:TreatWarningsAsErrors=true -o "$stage/smoke"
"$stage/smoke/NativeSmoke"
(cd "$stage/$name" && sha256sum RepoAgentController github-discussion/SKILL.md repoagent-orientation/SKILL.md VERSION RELEASE_NOTES.md > SHA256SUMS)
tar -C "$stage" -czf "$output/$name.tar.gz" "$name"
(cd "$output" && sha256sum "$name.tar.gz" > "$name.tar.gz.sha256")
