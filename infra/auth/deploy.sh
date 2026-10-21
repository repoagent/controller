#!/usr/bin/env bash
set -euo pipefail
umask 077
root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)
operation=${1:?Specify a deployment operation}
for name in AZURE_SUBSCRIPTION_ID AZURE_RESOURCE_GROUP AZURE_LOCATION AUTH_APP_NAME AUTH_STORAGE_ACCOUNT_NAME AUTH_KEY_VAULT_NAME; do
  test -n "${!name:-}" || { echo "Missing $name" >&2; exit 1; }
done
test "$AZURE_SUBSCRIPTION_ID" = 46f7e5d6-05f3-435b-8089-6c63a203dbfa
test "$AZURE_RESOURCE_GROUP" = rg-repoagent-controller-auth
test "$AZURE_LOCATION" = swedencentral
case "$operation" in
  check|plan|apply|promote-staged)
    for name in AUTH_PROVIDER_READY AUTH_STORAGE_READY AUTH_CLEANUP_READY AUTH_RUNTIME_ACCESS_READY AUTH_DEPLOYMENT_READY; do
      test "${!name:-}" = true || { echo "$name requires actual hosted acceptance" >&2; exit 1; }
    done ;;
esac
case "$operation" in check|foundation-check) exit 0 ;; esac

if [[ -n "${AUTH_EXPECTED_SOURCE_SHA:-}" ]]; then
  python3 "$root/scripts/delivery/auth-pipeline.py" source-check
else
  case "$operation" in plan|apply) ;; *) echo 'An accepted current main source SHA is required' >&2; exit 1 ;; esac
fi
target=(--subscription "$AZURE_SUBSCRIPTION_ID" --resource-group "$AZURE_RESOURCE_GROUP")
case "$operation" in
  promote-staged)
    test "$(az containerapp show "${target[@]}" --name "$AUTH_APP_NAME" --query properties.configuration.ingress -o json)" = null
    test "$(az containerapp show "${target[@]}" --name "$AUTH_APP_NAME" --query 'properties.template.containers[0].image' -o tsv)" = "${AUTH_IMAGE:?Select the staged image}" ;;
  stage-stop|stage-resume)
    test "$(az containerapp show "${target[@]}" --name "$AUTH_APP_NAME" --query properties.configuration.ingress -o json)" = null
    [[ "${AUTH_STAGE_REVISION:?Select the staging revision}" == "$AUTH_APP_NAME"--* ]]
    action=deactivate
    [[ "$operation" != stage-resume ]] || action=activate
    az containerapp revision "$action" "${target[@]}" --name "$AUTH_APP_NAME" --revision "$AUTH_STAGE_REVISION" --output none
    az containerapp replica list "${target[@]}" --name "$AUTH_APP_NAME" --revision "$AUTH_STAGE_REVISION" --query '[].{name:name}' -o json
    exit 0 ;;
esac

parameters=(location="$AZURE_LOCATION" appName="$AUTH_APP_NAME")
case "$operation" in
  foundation-plan|foundation-apply|plan|apply|stage-plan|stage-apply|promote-staged)
    template="$root/infra/auth/main.bicep"
    parameters+=(storageName="$AUTH_STORAGE_ACCOUNT_NAME" vaultName="$AUTH_KEY_VAULT_NAME")
    case "$operation" in
      foundation-*) parameters+=(deployApp=false) ;;
      *)
        python3 "$root/scripts/delivery/auth-pipeline.py" attest
        parameters+=(deployApp=true image="${AUTH_IMAGE:?Select an immutable image}"
          signingKeyUri="${AUTH_SIGNING_KEY_URI:?Select the signing key version}"
          dataProtectionKeyUri="${AUTH_DATA_PROTECTION_KEY_URI:?Select the wrapping key version}"
          deploymentEpoch="${AUTH_DEPLOYMENT_EPOCH:?Set the external restore epoch}")
        [[ -z "${AUTH_REQUEST_LIMIT_PER_MINUTE:-}" ]] || parameters+=(requestLimitPerMinute="$AUTH_REQUEST_LIMIT_PER_MINUTE")
        [[ -z "${AUTH_CONCURRENT_REQUEST_LIMIT:-}" ]] || parameters+=(concurrentRequestLimit="$AUTH_CONCURRENT_REQUEST_LIMIT")
        [[ -z "${AUTH_REQUEST_TIMEOUT_SECONDS:-}" ]] || parameters+=(requestTimeoutSeconds="$AUTH_REQUEST_TIMEOUT_SECONDS")
        case "$operation" in stage-*) parameters+=(staged=true) ;; esac ;;
    esac ;;
  cleanup-identity-plan|cleanup-identity-apply|cleanup-plan|cleanup-apply)
    template="$root/infra/auth/cleanup.bicep"
    parameters+=(storageName="$AUTH_STORAGE_ACCOUNT_NAME")
    case "$operation" in
      cleanup-identity-*) parameters+=(identityOnly=true) ;;
      *)
        python3 "$root/scripts/delivery/auth-pipeline.py" attest
        parameters+=(identityOnly=false image="${AUTH_IMAGE:?Select an immutable image}"
          deploymentEpoch="${AUTH_DEPLOYMENT_EPOCH:?Set the external restore epoch}") ;;
    esac ;;
  monitoring-plan|monitoring-apply)
    template="$root/infra/auth/monitoring.bicep"
    parameters+=("@${AUTH_MONITORING_PARAMETERS_FILE:?Supply a private ARM parameters file}") ;;
  *) echo "Unknown deployment operation: $operation" >&2; exit 1 ;;
esac
case "$operation" in
  monitoring-*) ;;
  *) [[ -z "${AUTH_TABLE_NAME:-}" ]] || parameters+=(tableName="$AUTH_TABLE_NAME") ;;
esac

compiler=${BICEP_BIN:-$HOME/.azure/bin/bicep}
build=$(mktemp -d)
trap 'rm -rf -- "$build"' EXIT
"$compiler" build "$template" --outfile "$build/template.json"
deployment=("${target[@]}" --name "auth-$AUTH_APP_NAME" --mode Incremental
  --template-file "$build/template.json" --parameters "${parameters[@]}")
case "$operation" in
  *-plan|plan)
    az deployment group what-if "${deployment[@]}" --no-pretty-print \
      --query 'changes[].{change:changeType,id:resourceId}' --output json ;;
  *)
    if ! az deployment group create "${deployment[@]}" --output none >"$build/result.log" 2>&1; then
      echo 'Azure deployment failed or is unconfirmed; inspect its deployment record before retrying.' >&2
      exit 1
    fi
    az deployment group show "${target[@]}" --name "auth-$AUTH_APP_NAME" \
      --query '{state:properties.provisioningState,outputs:properties.outputs}' --output json
    az resource list "${target[@]}" \
      --query '[].{name:name,type:type,location:location}' --output json ;;
esac
