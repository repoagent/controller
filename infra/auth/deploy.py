#!/usr/bin/env python3
"""Guard resource ownership before an explicit resource-group Incremental apply.

Azure is the external process boundary. Never infer absence from a failed list or
an arbitrary CLI failure. No raw Azure response/error body is logged.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile

HERE = Path(__file__).resolve().parent
REPOSITORY_ID = "1426154477"
OWNERSHIP = {"repositoryId": REPOSITORY_ID, "ownerId": "345029828", "managedBy": "repoagent-auth"}
RESOURCE_TYPES = (
    "Microsoft.ManagedIdentity/userAssignedIdentities",
    "Microsoft.OperationalInsights/workspaces",
    "Microsoft.App/managedEnvironments",
    "Microsoft.Storage/storageAccounts",
    "Microsoft.Storage/storageAccounts/blobServices/containers",
    "Microsoft.KeyVault/vaults",
    "Microsoft.App/containerApps",
)


class DeploymentError(Exception):
    pass


def required(name):
    value = os.environ.get(name, "")
    if not value or value.strip() != value:
        raise DeploymentError(f"Missing or invalid {name}")
    return value


class Deployment:
    def __init__(self, mode):
        self.mode = mode
        self.foundation = mode.startswith("foundation-")
        self.subscription = required("AZURE_SUBSCRIPTION_ID")
        self.group = required("AZURE_RESOURCE_GROUP")
        self.location = required("AZURE_LOCATION")
        # This entrypoint is deliberately bound to the approved destination.
        if (self.subscription, self.group, self.location) != (
            "46f7e5d6-05f3-435b-8089-6c63a203dbfa", "rg-repoagent-controller-auth", "swedencentral"
        ):
            raise DeploymentError("Target differs from the approved subscription/group/location")
        self.app = required("AUTH_APP_NAME")
        if not re.fullmatch(r"[a-z][a-z0-9-]{1,20}[a-z0-9]", self.app) or "--" in self.app:
            raise DeploymentError("AUTH_APP_NAME must be 3-22 lowercase letters/digits/hyphens")
        self.scope = f"/subscriptions/{self.subscription}/resourceGroups/{self.group}"
        suffix = hashlib.sha256(f"{self.scope}/{self.app}".encode()).hexdigest()[:16]
        self.storage = f"ra{suffix}dp"
        self.vault = f"ra-{suffix}-kv"
        self.params = {"location": self.location, "appName": self.app, "storageName": self.storage,
                       "vaultName": self.vault, "deployApp": not self.foundation}
        self.resources = [
            (RESOURCE_TYPES[0], self.app + "-runtime", "2023-01-31", "auth-runtime"),
            (RESOURCE_TYPES[1], self.app + "-logs", "2023-09-01", "auth-logs"),
            (RESOURCE_TYPES[2], self.app + "-environment", "2025-01-01", "auth-environment"),
            (RESOURCE_TYPES[3], self.storage, "2023-05-01", "auth-data-protection"),
            (RESOURCE_TYPES[4], self.storage + "/default/data-protection", "2023-05-01", "auth-data-protection"),
            (RESOURCE_TYPES[5], self.vault, "2023-07-01", "auth-security"),
        ]
        if not self.foundation:
            self.runtime_configuration()
            self.resources.append((RESOURCE_TYPES[6], self.app, "2025-01-01", "auth-service"))
        if mode not in ("check", "foundation-check", "foundation-plan", "foundation-apply"):
            image = required("AUTH_IMAGE")
            if not re.fullmatch(r"[a-z0-9][a-z0-9.:-]*/[a-z0-9][a-z0-9/_.-]*@sha256:[0-9a-f]{64}", image):
                raise DeploymentError("AUTH_IMAGE must be a registry-qualified sha256 digest")
            for name, expected in (("AUTH_IMAGE_ROLE", "auth-service"), ("AUTH_IMAGE_NON_ROOT", "true"), ("AUTH_IMAGE_PUBLIC", "true")):
                if required(name) != expected:
                    raise DeploymentError(f"Image preflight attestation missing: {name}")
            self.params["image"] = image

    def runtime_configuration(self):
        for name in ("AUTH_PROVIDER_READY", "AUTH_STORAGE_READY", "AUTH_CLEANUP_READY", "AUTH_RUNTIME_ACCESS_READY"):
            if required(name) != "true":
                raise DeploymentError(f"Runtime gate is not ready: {name}")
        for name, param, kind in (
            ("AUTH_SIGNING_KEY_URI", "signingKeyUri", "keys"),
            ("AUTH_DATA_PROTECTION_KEY_URI", "dataProtectionKeyUri", "keys"),
            ("AUTH_OAUTH_CLIENT_SECRET_URI", "oauthClientSecretUri", "secrets"),
        ):
            value = required(name)
            if not re.fullmatch(rf"https://{re.escape(self.vault)}\.vault\.azure\.net/{kind}/[A-Za-z0-9-]+/[0-9a-f]{{32}}", value):
                raise DeploymentError(f"{name} must be a versioned reference in the foundation vault")
            self.params[param] = value
        # Key Vault object names are case-insensitive; versions do not define
        # separate signing and wrapping keys.
        signing_key = self.params["signingKeyUri"].rsplit("/", 1)[0].casefold()
        protection_key = self.params["dataProtectionKeyUri"].rsplit("/", 1)[0].casefold()
        if signing_key == protection_key:
            raise DeploymentError("App signing and Data Protection must use separate keys")
        store = required("AUTH_STORE_RESOURCE_ID")
        if not re.fullmatch(rf"/subscriptions/{re.escape(self.subscription)}/resourceGroups/[A-Za-z0-9_.()-]+/providers/[A-Za-z0-9.]+(?:/[A-Za-z0-9_.()-]+){{2,}}", store):
            raise DeploymentError("AUTH_STORE_RESOURCE_ID must be a nonsecret resource ID in the selected subscription")
        self.params["storeResourceId"] = store

    def resource_id(self, kind, name):
        provider, *types = kind.split("/")
        return self.scope + "/providers/" + provider + "/" + "/".join(part for pair in zip(types, name.split("/"), strict=True) for part in pair)

    def az(self, args, allow_missing=False):
        result = subprocess.run(["az", *args, "--subscription", self.subscription, "--output", "json", "--only-show-errors"],
                                text=True, capture_output=True, check=False)
        if result.returncode:
            # Azure CLI's documented ARM errors include '(Code)'. Only explicit
            # resource absence may create; authorization, transport and other
            # failures remain fatal. Never print server error bodies.
            codes = re.findall(r"\(([A-Za-z][A-Za-z0-9]+)\)", result.stderr)
            code = codes[0] if codes else ""
            if allow_missing and code in ("ResourceNotFound", "ParentResourceNotFound"):
                return None
            category = "forbidden" if code in ("AuthorizationFailed", "Forbidden", "AuthorizationPermissionMismatch") else (
                "not-found" if code in ("ResourceNotFound", "ResourceGroupNotFound", "ParentResourceNotFound") else (
                    "network" if any(word in result.stderr.lower() for word in ("connection", "timeout", "timed out", "name resolution")) else "azure-error"))
            raise DeploymentError(f"Azure {category} during {' '.join(args[:3])}; no further writes attempted")
        try:
            value = json.loads(result.stdout)
        except ValueError as error:
            raise DeploymentError("Azure malformed JSON response") from error
        if not isinstance(value, dict):
            raise DeploymentError("Azure malformed object response")
        return value

    def inspect(self):
        group = self.az(["group", "show", "--name", self.group])
        if str(group.get("id", "")).lower() != self.scope.lower() or group.get("location") != self.location:
            raise DeploymentError("Azure resource group identity/location mismatch")
        for kind, name, version, role in self.resources:
            resource_id = self.resource_id(kind, name)
            resource = self.az(["resource", "show", "--ids", resource_id, "--api-version", version], allow_missing=True)
            if resource is None:
                if not self.foundation and role != "auth-service":
                    raise DeploymentError("Runtime requires existing foundation and operator-granted access")
                continue
            if str(resource.get("id", "")).lower() != resource_id.lower() or str(resource.get("type", "")).lower() != kind.lower():
                raise DeploymentError("Azure malformed resource identity")
            container = kind.endswith("/containers")
            if container and not isinstance(resource.get("properties"), dict):
                raise DeploymentError("Azure malformed container properties")
            tags = resource.get("properties", {}).get("metadata") if container else resource.get("tags")
            expected = {**OWNERSHIP, "role": role}
            if not isinstance(tags, dict) or any(tags.get(key) != value for key, value in expected.items()):
                raise DeploymentError(f"Ownership conflict for {name}; refusing adoption")
            if not container and resource.get("location") != self.location:
                raise DeploymentError(f"Resource location conflict for {name}")

    def run(self):
        if self.mode in ("check", "foundation-check"):
            return self.outputs(False)
        # Validate the real template before any Azure calls.
        subprocess.run([str(HERE / "validate.sh")], check=True, stdout=sys.stderr)
        with tempfile.TemporaryDirectory(prefix="auth-deployment-") as directory:
            compiler = shutil.which(os.environ.get("BICEP_BIN", "bicep")) or str(Path.home() / ".azure/bin/bicep")
            template = Path(directory) / "main.json"
            subprocess.run([compiler, "build", str(HERE / "main.bicep"), "--outfile", str(template)], check=True, stdout=sys.stderr)
            from validate_arm import validate
            validate(template)
            self.inspect()
            parameters = Path(directory) / "parameters.json"
            parameters.write_text(json.dumps({"parameters": {key: {"value": value} for key, value in self.params.items()}}), encoding="utf-8")
            common = ["--resource-group", self.group, "--name", f"auth-{self.app}", "--mode", "Incremental",
                      "--template-file", str(template), "--parameters", "@" + str(parameters)]
            self.az(["deployment", "group", "validate", *common])
            plan = self.az(["deployment", "group", "what-if", *common, "--no-pretty-print", "--result-format", "FullResourcePayloads"])
            changes = plan.get("changes")
            if plan.get("status") != "Succeeded" or not isinstance(changes, list):
                raise DeploymentError("Azure malformed or unsuccessful what-if")
            allowed = {self.resource_id(kind, name).lower() for kind, name, _, _ in self.resources}
            for change in changes:
                if not isinstance(change, dict) or change.get("changeType") not in ("Create", "Modify", "NoChange", "Ignore"):
                    raise DeploymentError("What-if contains deletion or unsupported changes")
                if change["changeType"] != "Ignore" and str(change.get("resourceId", "")).lower() not in allowed:
                    raise DeploymentError("What-if contains a resource outside the owned manifest")
                if change["changeType"] != "Ignore":
                    print(f"Plan: {change['changeType']} {change['resourceId']}", file=sys.stderr)
            applied = self.mode in ("apply", "foundation-apply")
            if applied:
                self.inspect()  # Recheck after planning, immediately before writes.
                result = self.az(["deployment", "group", "create", *common])
                if not isinstance(result.get("properties"), dict) or result["properties"].get("provisioningState") != "Succeeded":
                    raise DeploymentError("Deployment did not report Succeeded; inspect Azure before retrying")
        return self.outputs(applied)

    def outputs(self, applied):
        return {"appName": self.app, "runtimeIdentityId": self.resource_id(RESOURCE_TYPES[0], self.app + "-runtime"),
                "keyVaultName": self.vault, "dataProtectionBlobUri": f"https://{self.storage}.blob.core.windows.net/data-protection/keyring.xml",
                "applied": applied}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("mode", choices=("check", "plan", "apply", "foundation-check", "foundation-plan", "foundation-apply"))
    args = parser.parse_args()
    try:
        print(json.dumps(Deployment(args.mode).run()))
    except (DeploymentError, OSError, ValueError, subprocess.CalledProcessError) as error:
        print(f"Auth deployment refused: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
