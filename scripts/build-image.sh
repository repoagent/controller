#!/usr/bin/env bash
# Thin host adapter: the pinned container owns validation and context construction.
set -euo pipefail
root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
manifest="$root/image/default.json"
derived=''
build_args=()
while (( $# )); do
  case "$1" in
    --manifest|--derived-dockerfile|--build-arg)
      (( $# >= 2 )) || { echo "$1 requires a value" >&2; exit 2; }
      case "$1" in
        --manifest) manifest=$2 ;; --derived-dockerfile) derived=$2 ;; --build-arg) build_args+=(--build-arg "$2") ;;
      esac
      shift 2 ;;
    *) echo 'Unknown image build option' >&2; exit 2 ;;
  esac
done
[[ -n "$derived" || ${#build_args[@]} == 0 ]] || { echo '--build-arg requires --derived-dockerfile' >&2; exit 2; }
absolute_file() {
  [[ -f "$1" && "$1" != *','* && "$1" != *$'\n'* && "$1" != *$'\r'* ]] || { echo 'Input must be a file; Docker mount paths cannot contain commas or newlines.' >&2; return 2; }
  (cd -- "$(dirname -- "$1")" && printf '%s/%s' "$PWD" "$(basename -- "$1")")
}
manifest=$(absolute_file "$manifest")
context=$(mktemp -d)
trap 'rm -rf -- "$context"' EXIT
mounts=(--mount "type=bind,src=$root,dst=/source,readonly" --mount "type=bind,src=$manifest,dst=/input/manifest.json,readonly" --mount "type=bind,src=$context,dst=/output")
arguments=(--source /source --manifest /input/manifest.json --output /output)
if [[ -n "$derived" ]]; then
  derived=$(absolute_file "$derived")
  mounts+=(--mount "type=bind,src=$derived,dst=/input/Derived.Dockerfile,readonly")
  arguments+=(--derived-dockerfile /input/Derived.Dockerfile)
fi
helper=python@sha256:781449467ffb6f04218f09b1ecdcdc7d22b289ee5da9ec498b024e24ad7a6db7
# No network, socket, credentials, or home mount; write only the temporary context.
docker run --rm --network none --read-only --user "$(id -u):$(id -g)" "${mounts[@]}" --entrypoint python3 "$helper" /source/scripts/compose-image.py "${arguments[@]}"
reference=$(cat "$context/base-reference.txt")
if [[ "$reference" == sha256:* ]]; then
  [[ $(docker image inspect --format '{{.Id}}' "$reference") == "$reference" ]] || { echo 'Requested immutable local base is not present' >&2; exit 2; }
  local_base="repoagent-local-base:${reference#sha256:}"
  docker tag "$reference" "$local_base"
  [[ $(docker image inspect --format '{{.Id}}' "$local_base") == "$reference" ]] || { echo 'Local base identity changed' >&2; exit 2; }
fi
image=${REPOAGENT_IMAGE:-repoagent:local}
target=${REPOAGENT_TARGET:-runtime}
case "$target" in runtime|dotnet-development|live-tests) ;; *) echo 'Unsupported build target' >&2; exit 2 ;; esac
base=$image
[[ -z "$derived" ]] || base="repoagent-derived-base:$(basename "$context" | tr '[:upper:]' '[:lower:]')"
docker build --platform linux/amd64 --target "$target" -f "$context/container/Containerfile" -t "$base" "$context"
if [[ -n "$derived" ]]; then
  docker build --platform linux/amd64 --build-arg "REPOAGENT_BASE=$base" "${build_args[@]}" -f "$context/Derived.Dockerfile" -t "$image" "$context"
fi
docker image inspect --format '{{.Id}}' "$image"
[[ "$target" == live-tests ]] || docker run --rm "$image" versions
