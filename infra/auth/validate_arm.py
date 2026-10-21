"""Check the compiled template's write boundary against the preflight manifest."""
import json
import sys
from deploy import RESOURCE_TYPES


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


if __name__ == "__main__":
    validate(sys.argv[1])
