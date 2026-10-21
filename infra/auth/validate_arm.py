"""Check the compiled template's write boundary against the preflight manifest."""
import json
import sys
from deploy import RESOURCE_TYPES, OWNERSHIP, INSIGHTS, PUBLISHER


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
            0, 1, {"cpu": "[json('0.25')]", "memory": "0.5Gi"}, 30, "[json('0.1')]"):
        raise ValueError("Template exceeds the approved pilot capacity/log envelope")
    environment = next(r for r in resources if r["type"] == "Microsoft.App/managedEnvironments")
    if environment["properties"].get("appLogsConfiguration") != {"destination": "none"}:
        raise ValueError("Console persistence must remain disabled")
    telemetry = {item["name"]: item["value"] for item in container["env"] if item["name"].startswith("APPLICATIONINSIGHTS_")}
    if telemetry != telemetry_env():
        raise ValueError("Runtime monitoring binding differs")
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
        **telemetry_env(),
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
            or len(container["env"]) != 6 or any(set(item) != {"name", "value"} for item in container["env"])
            or {item["name"]: item["value"] for item in container["env"]} != expected_env):
        raise ValueError("Cleanup template exceeds its fixed identity/runtime/schedule boundary")
    for key, field in (("cleanupIdentityClientId", "clientId"), ("cleanupIdentityPrincipalId", "principalId")):
        value = template["outputs"][key]["value"]
        # Both branches refer to the same exact identity; preserve lazy branch selection.
        created = reference + "." + field
        if value != "[if(parameters('identityOnly'), " + created + ", " + reference + "." + field + ")]":
            raise ValueError("Cleanup identity outputs must guard inactive references")


def telemetry_env():
    return {"APPLICATIONINSIGHTS_CONNECTION_STRING": "[parameters('monitoringConnectionString')]",
            "APPLICATIONINSIGHTS_STATSBEAT_DISABLED": "true"}


def validate_monitoring(path):
    with open(path, encoding="utf-8") as stream:
        template = json.load(stream)
    resources, variables = template["resources"], template["variables"]
    manifest = [(INSIGHTS, "2020-02-02", "insights", "[parameters('location')]"),
                ("Microsoft.Insights/actionGroups", "2023-01-01", "alerts", "global"),
                ("Microsoft.Consumption/budgets", "2024-08-01", "monthly-budget", None),
                ("Microsoft.Insights/metricAlerts", "2018-03-01", "metricRules", "global"),
                ("Microsoft.Insights/scheduledQueryRules", "2023-12-01", "queryRules", "[parameters('location')]")]
    if (len(resources) != len(manifest) or template.get("outputs") or variables.get("ownership") != OWNERSHIP
            or template["parameters"].get("recipient") != {"type": "securestring"}
            or not template["$schema"].endswith("/deploymentTemplate.json#")):
        raise ValueError("Monitoring parameter/ownership boundary differs")
    for resource, (kind, version, suffix, location) in zip(resources, manifest, strict=True):
        name = ("[format('{0}-{1}', parameters('appName'), variables('" + suffix + "')[copyIndex()].suffix)]"
                if suffix.endswith("Rules") else "[format('{0}-" + suffix + "', parameters('appName'))]")
        if (resource["type"] != kind or resource["apiVersion"] != version or resource["name"] != name
                or resource.get("location") != location or any(k in resource for k in ("scope", "subscriptionId", "resourceGroup"))
                or (location and resource.get("tags") != "[union(variables('ownership'), createObject('role', 'auth-monitoring'))]")):
            raise ValueError("Monitoring writes differ from exact typed manifest")
        if suffix.endswith("Rules") and (resource.get("condition") != "[parameters('enableAlerts')]"
                or resource.get("copy", {}).get("count") != "[length(variables('" + suffix + "'))]"):
            raise ValueError("Monitoring rule expansion differs")
    if ([r["suffix"] for r in variables["metricRules"]] != ["cleanup-execution-failed", "http-errors", "memory-pressure"]
            or [r["suffix"] for r in variables["queryRules"]] != ["cleanup-observation", "telemetry-volume"]
            or resources[0]["properties"] != {"Application_Type": "web", "DisableLocalAuth": True, "DisableIpMasking": False,
                "IngestionMode": "LogAnalytics", "WorkspaceResourceId": "[resourceId('Microsoft.OperationalInsights/workspaces', format('{0}-logs', parameters('appName')))]"}
            or resources[2].get("condition") != "[greater(parameters('budgetAmount'), 0)]"
            or resources[2]["properties"]["timePeriod"] != {"startDate": "[parameters('budgetStartDate')]"}):
        raise ValueError("Monitoring scope/AAD/budget contract differs")

    if resources[1]["properties"] != {"groupShortName": "repoauth", "enabled": True, "emailReceivers": [
            {"name": "operator", "emailAddress": "[parameters('recipient')]", "useCommonAlertSchema": True}]}:
        raise ValueError("Monitoring recipient/action boundary differs")
    if [(r["metric"], r["aggregation"], r["operator"], r["threshold"], r["window"]) for r in variables["metricRules"]] != [
            ("Executions", "Total", "GreaterThan", 0, "PT5M"), ("Requests", "Total", "GreaterThanOrEqual", 5, "PT5M"),
            ("WorkingSetBytes", "Average", "GreaterThan", 429496729, "PT15M")]:
        raise ValueError("Native alert thresholds differ")
    if any(r["properties"].get("enabled") is not True or r["properties"].get("autoMitigate") is not True
           or r["properties"].get("severity") != 2 for r in resources[3:]):
        raise ValueError("Alert state/severity differs")


def validate_monitoring_rbac(path):
    with open(path, encoding="utf-8") as stream:
        template = json.load(stream)
    scope = "resourceId('Microsoft.Insights/components', format('{0}-insights', parameters('appName')))"
    if (len(template["resources"]) != 2 or template.get("outputs") or template["variables"] != {
            "publisher": "[subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '" + PUBLISHER + "')]"}):
        raise ValueError("Monitoring RBAC manifest differs")
    for resource, suffix in zip(template["resources"], ("runtime", "cleanup"), strict=True):
        identity = "resourceId('Microsoft.ManagedIdentity/userAssignedIdentities', format('{0}-" + suffix + "', parameters('appName')))"
        if resource != {"type": "Microsoft.Authorization/roleAssignments", "apiVersion": "2022-04-01", "scope": "[" + scope + "]",
                "name": "[guid(" + scope + ", " + identity + ", variables('publisher'))]", "properties": {
                    "principalId": "[reference(" + identity + ", '2023-01-31').principalId]",
                    "principalType": "ServicePrincipal", "roleDefinitionId": "[variables('publisher')]"}}:
            raise ValueError("Monitoring RBAC may only grant each exact identity publisher at Insights scope")


if __name__ == "__main__":
    {"main": validate, "cleanup": validate_cleanup, "monitoring": validate_monitoring,
     "monitoring-rbac": validate_monitoring_rbac}[sys.argv[2] if len(sys.argv) > 2 else "main"](sys.argv[1])
