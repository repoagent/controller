"""Check the compiled template's write boundary against the preflight manifest."""
import json
import sys
from deploy import RESOURCE_TYPES, OWNERSHIP


def validate(path):
    with open(path, encoding="utf-8") as stream:
        template = json.load(stream)
    resources = template["resources"]
    actual = [resource["type"].lower() for resource in resources]
    expected = [kind.lower() for kind in RESOURCE_TYPES]
    if sorted(actual) != sorted(expected):
        raise ValueError("Template write set differs from the guarded resource manifest")
    app = next(resource for resource in resources if resource["type"] == "Microsoft.App/containerApps")
    container = app["properties"]["template"]["containers"][0]
    variables = template.get("variables", {})
    if template["parameters"].get("staged") != {"type": "bool", "defaultValue": False}:
        raise ValueError("Only fixed staged/release configurations are permitted")
    for field, location, staged, release in (
            ("configuration", app["properties"], "stagedConfiguration", "releaseConfiguration"),
            ("scale", app["properties"]["template"], "stagedScale", "releaseScale")):
        if location[field] != f"[if(parameters('staged'), variables('{staged}'), variables('{release}'))]":
            raise ValueError("Template must select only the two approved configurations")
    if variables["stagedConfiguration"] != {"activeRevisionsMode": "Single", "maxInactiveRevisions": 3}:
        raise ValueError("Staging must have no ingress")
    if variables["releaseConfiguration"] != {"activeRevisionsMode": "Single", "maxInactiveRevisions": 3,
            "ingress": {"external": True, "allowInsecure": False, "targetPort": 8080, "transport": "http"}}:
        raise ValueError("Release must retain public HTTPS ingress")
    if variables["stagedScale"] != {"minReplicas": 1, "maxReplicas": 1, "rules": []}:
        raise ValueError("Staging must have exactly one configured replica")
    scale = variables["releaseScale"]
    if scale.get("rules") != [{"name": "http", "http": {"metadata": {"concurrentRequests": "10"}}}]:
        raise ValueError("Release HTTP scaling rule changed")
    logs = next(resource for resource in resources if resource["type"] == "Microsoft.OperationalInsights/workspaces")
    if (scale["minReplicas"], scale["maxReplicas"], container["resources"],
            logs["properties"]["retentionInDays"], logs["properties"]["workspaceCapping"]["dailyQuotaGb"]) != (
            0, 1, {"cpu": "[json('0.25')]", "memory": "0.5Gi"}, 30, 1):
        raise ValueError("Template exceeds the approved pilot capacity/log envelope")
    storage = next(resource for resource in resources if resource["type"] == "Microsoft.Storage/storageAccounts")
    if storage["properties"].get("allowSharedKeyAccess") is not False:
        raise ValueError("Storage shared-key access must remain disabled")
    if any("scope" in resource or "subscriptionId" in resource or "resourceGroup" in resource for resource in resources):
        raise ValueError("Template may only write the selected resource group")
    if not template["$schema"].endswith("/deploymentTemplate.json#"):
        raise ValueError("Expected a resource-group template")
    if any("listKeys(" in str(value) for value in template.get("outputs", {}).values()):
        raise ValueError("Credential output forbidden")


def validate_cleanup(path):
    """Validate fixed compiled writes and lazy references without evaluating ARM."""
    with open(path, encoding="utf-8") as stream:
        template = json.load(stream)
    resources = template["resources"]
    identity, = [r for r in resources if r["type"] == "Microsoft.ManagedIdentity/userAssignedIdentities"]
    job, = [r for r in resources if r["type"] == "Microsoft.App/jobs"]
    name = "[format('{0}-cleanup', parameters('appName'))]"
    identity_id = "resourceId('Microsoft.ManagedIdentity/userAssignedIdentities', format('{0}-cleanup', parameters('appName')))"
    reference = "reference(" + identity_id + ", '2023-01-31')"
    if (len(resources) != 2 or identity["condition"] != "[parameters('identityOnly')]"
            or job["condition"] != "[not(parameters('identityOnly'))]"
            or identity["name"] != name or job["name"] != name
            or identity["apiVersion"] != "2023-01-31" or job["apiVersion"] != "2025-01-01"
            or any(any(key in r for key in ("scope", "subscriptionId", "resourceGroup")) for r in resources)
            or not template["$schema"].endswith("/deploymentTemplate.json#")):
        raise ValueError("Cleanup template differs from its conditional resource-group write manifest")
    if (set(identity) != {"condition", "type", "apiVersion", "name", "location", "tags"}
            or set(job) != {"condition", "type", "apiVersion", "name", "location", "tags", "identity", "properties"}
            or template.get("variables", {}).get("ownership") != OWNERSHIP
            or identity["location"] != "[parameters('location')]" or job["location"] != "[parameters('location')]"
            or identity["tags"] != "[union(variables('ownership'), createObject('role', 'auth-cleanup-identity'))]"
            or job["tags"] != "[union(variables('ownership'), createObject('role', 'auth-cleanup'))]"
            or set(template["outputs"]) != {"cleanupIdentityId", "cleanupIdentityClientId", "cleanupIdentityPrincipalId", "cleanupJobId"}
            or template["outputs"]["cleanupIdentityId"]["value"] != "[" + identity_id + "]"
            or template["outputs"]["cleanupJobId"]["value"] != "[resourceId('Microsoft.App/jobs', format('{0}-cleanup', parameters('appName')))]"):
        raise ValueError("Cleanup template ownership/location/output boundary differs")
    properties = job["properties"]
    container, = properties["template"]["containers"]
    expected_env = {
        "Broker__TableEndpoint": "[format('https://{0}.table.core.windows.net/', parameters('storageName'))]",
        "Broker__TableName": "[parameters('tableName')]",
        "Broker__DeploymentEpoch": "[parameters('deploymentEpoch')]",
        "Broker__ManagedIdentityClientId": "[if(parameters('identityOnly'), '', " + reference + ".clientId)]"}
    if (set(properties) != {"environmentId", "workloadProfileName", "configuration", "template"}
            or properties["workloadProfileName"] != "Consumption"
            or properties["environmentId"] != "[resourceId('Microsoft.App/managedEnvironments', format('{0}-environment', parameters('appName')))]"
            or job["identity"] != {"type": "UserAssigned", "userAssignedIdentities": {"[format('{0}', " + identity_id + ")]": {}}}
            or properties["configuration"] != {"triggerType": "Schedule", "replicaRetryLimit": 0, "replicaTimeout": 90,
                "scheduleTriggerConfig": {"cronExpression": "* * * * *", "parallelism": 1, "replicaCompletionCount": 1}}
            or set(properties["template"]) != {"containers"}
            or set(container) != {"name", "image", "command", "args", "env", "resources"}
            or container["name"] != "cleanup" or container["image"] != "[parameters('image')]"
            or container["command"] != ["dotnet", "RepoAgent.Auth.dll"] or container["args"] != ["cleanup", "--once"]
            or container["resources"] != {"cpu": "[json('0.25')]", "memory": "0.5Gi"}
            or len(container["env"]) != 4 or any(set(item) != {"name", "value"} for item in container["env"])
            or {item["name"]: item["value"] for item in container["env"]} != expected_env):
        raise ValueError("Cleanup template exceeds its fixed identity/runtime/schedule boundary")
    for key, field in (("cleanupIdentityClientId", "clientId"), ("cleanupIdentityPrincipalId", "principalId")):
        value = template["outputs"][key]["value"]
        # Both branches refer to the same exact identity; preserve lazy branch selection.
        created = reference + "." + field
        if value != "[if(parameters('identityOnly'), " + created + ", " + reference + "." + field + ")]":
            raise ValueError("Cleanup identity outputs must guard inactive references")


if __name__ == "__main__":
    (validate_cleanup if len(sys.argv) > 2 and sys.argv[2] == "cleanup" else validate)(sys.argv[1])
