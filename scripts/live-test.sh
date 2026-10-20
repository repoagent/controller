#!/usr/bin/env bash
set -euo pipefail
[[ ${REPOAGENT_LIVE:-} == 1 ]] || { echo 'NOT EXERCISED: set REPOAGENT_LIVE=1 to authorize disposable real GitHub/Codex activity.' >&2; exit 1; }
root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
instance=${REPOAGENT_INSTANCE:-default}
[[ "$instance" != default ]] || { echo 'Use a dedicated REPOAGENT_INSTANCE for live acceptance.' >&2; exit 1; }
image=${REPOAGENT_IMAGE:-repoagent:local}
name=repoagent-$instance
runner=$name-live-tests
control=$name-live-control
evidence=$name-live-evidence
repository=$("$root/repoagent" get repository)
[[ $("$root/repoagent" get mentionUser) == '' ]] || {
  echo 'Live fixtures use the canonical App mention; configure --mention-user "" in the dedicated test instance to avoid enrolling unrelated @repoagent history.' >&2; exit 1;
}
[[ $(docker inspect --format '{{.State.Running}}' "$name" 2>/dev/null || true) != true ]] || {
  echo 'Stop the dedicated agent instance before live-test.' >&2; exit 1;
}
"$root/repoagent" participant-identity
"$root/repoagent" check
actor=$(docker run --rm --mount "type=volume,src=$name-state,dst=/data/state" --entrypoint python3 "$image" -c \
  'import json; print(json.load(open("/data/state/bridge.json"))["agentLogin"])')
REPOAGENT_IMAGE="$image-live-tests" REPOAGENT_TARGET=live-tests "$root/repoagent" build
# Clean only the prior runner container; evidence/fixtures remain operator-owned.
docker rm "$runner" >/dev/null 2>&1 || true
helper=(docker run --rm -i --mount "type=volume,src=$control,dst=/data/control" --entrypoint python3 "$image" /opt/repoagent/control.py)
"$root/repoagent" start
docker inspect "$name" | "${helper[@]}" inspection
"$root/repoagent" versions | "${helper[@]}" artifact versions.txt
"$root/repoagent" skills | "${helper[@]}" artifact skills.json
docker run -d --name "$runner" \
  --mount "type=volume,src=$name-participant,dst=/data/participant,readonly" \
  --mount "type=volume,src=$name-workspace,dst=/workspace" \
  --mount "type=volume,src=$control,dst=/data/control" \
  --mount "type=volume,src=$evidence,dst=/evidence" \
  -e REPOAGENT_LIVE=1 -e "REPOAGENT_REPOSITORY=$repository" -e "REPOAGENT_ACTOR=$actor" \
  -e "REPOAGENT_LIVE_CASE=${REPOAGENT_LIVE_CASE:-journey}" "$image-live-tests"
on_exit() {
  status=$?
  trap - EXIT
  docker exec "$name" python3 /opt/repoagent/control.py restore-credential >/dev/null 2>&1 || true
  docker logs "$name" 2>&1 | "${helper[@]}" capture || true
  "$root/repoagent" stop >/dev/null 2>&1 || true
  if [[ $(docker inspect --format '{{.State.Running}}' "$runner" 2>/dev/null || true) == true ]]; then
    docker stop --timeout 10 "$runner" >/dev/null 2>&1 || true
  fi
  docker logs "$runner" 2>&1 || true
  echo "Evidence retained in Docker volumes $evidence and $control; failed Discussions remain for inspection."
  echo "Copy with: docker cp $runner:/evidence ./live-evidence"
  exit "$status"
}
trap on_exit EXIT
while [[ $(docker inspect --format '{{.State.Running}}' "$runner") == true ]]; do
  request=()
  while IFS= read -r -d '' value; do request+=("$value"); done < <("${helper[@]}" request)
  [[ ${#request[@]} -eq 3 ]] || continue
  identifier=${request[0]}; operation=${request[1]}; argument=${request[2]}
  status=0
  case "$operation" in
    capture) docker logs "$name" 2>&1 | "${helper[@]}" capture || status=$? ;;
    read-only-credential|restore-credential)
      docker exec "$name" python3 /opt/repoagent/control.py "$operation" || status=$? ;;
    replace|invalidate-session|restore-session)
      docker logs "$name" 2>&1 | "${helper[@]}" archive
      "$root/repoagent" stop
      if [[ "$operation" == replace ]]; then
        [[ -z "$argument" ]] || "$root/repoagent" enroll "$argument" || status=$?
      else
        "${helper[@]:0:3}" --mount "type=volume,src=$control,dst=/data/control" \
          --mount "type=volume,src=$name-state,dst=/data/state" --entrypoint python3 "$image" \
          /opt/repoagent/control.py "$operation" "$argument" || status=$?
      fi
      "$root/repoagent" start || status=$? ;;
    *) echo "Unknown lifecycle operation: $operation" >&2; status=1 ;;
  esac
  "${helper[@]}" response "$identifier" "$status"
done
exit "$(docker inspect --format '{{.State.ExitCode}}' "$runner")"
