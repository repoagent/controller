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
    if any("scope" in resource or "subscriptionId" in resource or "resourceGroup" in resource for resource in resources):
        raise ValueError("Template may only write the selected resource group")
    if not template["$schema"].endswith("/deploymentTemplate.json#"):
        raise ValueError("Expected a resource-group template")
    if any("listKeys(" in str(value) for value in template.get("outputs", {}).values()):
        raise ValueError("Credential output forbidden")


if __name__ == "__main__":
    validate(sys.argv[1])
