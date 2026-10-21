#!/usr/bin/env python3
"""Guard resource ownership before an explicit resource-group Incremental apply.

Azure is the external process boundary. Never infer absence from a failed list or
an arbitrary CLI failure. No raw Azure response/error body is logged.
"""
import argparse
import json
import os
import pty
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile
import time

HERE = Path(__file__).resolve().parent
REPOSITORY_ID = "1426154477"
OWNERSHIP = {"repositoryId": REPOSITORY_ID, "ownerId": "345029828", "managedBy": "repoagent-auth"}
ACTIVATION = ("stage-plan", "stage-apply", "promote-staged", "stage-stop", "stage-resume")
CLEANUP = ("cleanup-identity-plan", "cleanup-identity-apply", "cleanup-plan", "cleanup-apply")
CLEANUP_JOB = "Microsoft.App/jobs"
ROOT = Path(__file__).resolve().parents[2]


def accepted_source():
    sha = required("AUTH_EXPECTED_SOURCE_SHA")
    if not re.fullmatch(r"[0-9a-f]{40}", sha):
        raise DeploymentError("Expected an accepted full main source SHA")
    def git(*args):
        result = subprocess.run(["git", *args], cwd=ROOT, capture_output=True, text=True, timeout=30)
        if result.returncode:
            raise DeploymentError("Cannot verify accepted main source")
        return result.stdout.strip()
    remote = git("ls-remote", "https://github.com/repoagent/controller.git", "refs/heads/main").split()
    if (os.environ.get("GITHUB_REF") != "refs/heads/main" or os.environ.get("GITHUB_SHA") != sha
            or git("rev-parse", "HEAD") != sha or remote != [sha, "refs/heads/main"]):
        raise DeploymentError("Accepted source differs from checkout or current main; restage the new source")
    return sha


RESOURCE_TYPES = (
    "Microsoft.ManagedIdentity/userAssignedIdentities",
    "Microsoft.OperationalInsights/workspaces",
    "Microsoft.App/managedEnvironments",
    "Microsoft.Storage/storageAccounts",
    "Microsoft.Storage/storageAccounts/blobServices/containers",
    "Microsoft.KeyVault/vaults",
    "Microsoft.App/containerApps",
    "Microsoft.Storage/storageAccounts/tableServices/tables",
)


def canonical_origin(app, domain):
    # A DNS suffix, never a URI or a caller-selected origin.
    label = r"[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?"
    if (not isinstance(domain, str) or len(app + "." + domain) > 253
            or not re.fullmatch(label + r"(?:\." + label + r")+", domain)):
        raise ValueError("Owned environment defaultDomain must be a DNS suffix")
    return "https://" + app + "." + domain


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
        self.activation = mode in ACTIVATION
        self.cleanup = mode in CLEANUP
        self.identity_only = mode.startswith("cleanup-identity-")
        self.isolated = self.activation and mode != "promote-staged"
        self.current = None
        self.revision = None
        self.replica = None
        self.deadline = None
        self.foundation = mode.startswith("foundation-") or mode == "cost-plan"
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
        self.storage = required("AUTH_STORAGE_ACCOUNT_NAME")
        if not re.fullmatch(r"[a-z0-9]{3,24}", self.storage):
            raise DeploymentError("AUTH_STORAGE_ACCOUNT_NAME must be 3-24 lowercase letters/digits")
        self.vault = required("AUTH_KEY_VAULT_NAME")
        if not re.fullmatch(r"[A-Za-z][A-Za-z0-9-]{1,22}[A-Za-z0-9]", self.vault) or "--" in self.vault:
            raise DeploymentError("AUTH_KEY_VAULT_NAME must be 3-24 ASCII letters/digits/hyphens, start with a letter, end alphanumeric and contain no consecutive hyphens")
        self.source = accepted_source() if self.activation or self.cleanup or os.environ.get("AUTH_EXPECTED_SOURCE_SHA") is not None else None
        self.table = os.environ.get("AUTH_TABLE_NAME", "brokerstate")
        if not re.fullmatch(r"[A-Za-z][A-Za-z0-9]{2,62}", self.table):
            raise DeploymentError("AUTH_TABLE_NAME must be 3-63 ASCII alphanumeric characters starting with a letter")
        self.identity = {"clientId": None, "principalId": None}
        self.origin = None
        if self.cleanup:
            self.cleanup_configuration()
            return
        self.params = {"location": self.location, "appName": self.app, "storageName": self.storage,
                       "vaultName": self.vault, "deployApp": not self.foundation, "tableName": self.table}
        if self.activation:
            self.params["staged"] = self.isolated
            result = subprocess.run([sys.executable, str(ROOT / "scripts/delivery/auth-pipeline.py"), "attest"],
                                    capture_output=True, text=True, timeout=60)
            if result.returncode:
                raise DeploymentError("Accepted image/source anonymous attestation failed")
        self.request_budget()
        self.resources = [
            (RESOURCE_TYPES[0], self.app + "-runtime", "2023-01-31", "auth-runtime"),
            (RESOURCE_TYPES[1], self.app + "-logs", "2023-09-01", "auth-logs"),
            (RESOURCE_TYPES[2], self.app + "-environment", "2025-01-01", "auth-environment"),
            (RESOURCE_TYPES[3], self.storage, "2023-05-01", "auth-data-protection"),
            (RESOURCE_TYPES[4], self.storage + "/default/data-protection", "2023-05-01", "auth-data-protection"),
            (RESOURCE_TYPES[5], self.vault, "2023-07-01", "auth-security"),
        ]
        self.resources.append((RESOURCE_TYPES[7], self.storage + "/default/" + self.table, "2023-05-01", "auth-broker-state"))
        if not self.foundation:
            self.runtime_configuration()
            self.resources.append((RESOURCE_TYPES[6], self.app, "2025-01-01", "auth-service"))
        if mode not in ("check", "foundation-check", "foundation-plan", "foundation-apply", "cost-plan"):
            image = required("AUTH_IMAGE")
            if not re.fullmatch(r"[a-z0-9][a-z0-9.:-]*/[a-z0-9][a-z0-9/_.-]*@sha256:[0-9a-f]{64}", image):
                raise DeploymentError("AUTH_IMAGE must be a registry-qualified sha256 digest")
            for name, expected in (("AUTH_IMAGE_ROLE", "auth-service"), ("AUTH_IMAGE_NON_ROOT", "true"), ("AUTH_IMAGE_PUBLIC", "true")):
                if not self.activation and required(name) != expected:
                    raise DeploymentError(f"Image preflight attestation missing: {name}")
            self.params["image"] = image

    def attest_cleanup(self):
        result = subprocess.run([sys.executable, str(ROOT / "scripts/delivery/auth-pipeline.py"), "attest"],
                                capture_output=True, text=True, timeout=60)
        if result.returncode:
            raise DeploymentError("Cleanup accepted image/source anonymous attestation failed")

    def cleanup_configuration(self):
        self.prior_image = None
        self.job_seen = False
        self.params = dict(location=self.location, appName=self.app, storageName=self.storage,
                           tableName=self.table, identityOnly=self.identity_only)
        self.resources = [
            (RESOURCE_TYPES[1], self.app + "-logs", "2023-09-01", "auth-logs"),
            (RESOURCE_TYPES[2], self.app + "-environment", "2025-01-01", "auth-environment"),
            (RESOURCE_TYPES[3], self.storage, "2023-05-01", "auth-data-protection"),
            (RESOURCE_TYPES[7], self.storage + "/default/" + self.table, "2023-05-01", "auth-broker-state"),
            (RESOURCE_TYPES[0], self.app + "-cleanup", "2023-01-31", "auth-cleanup-identity"),
        ]
        if not self.identity_only:
            epoch = required("AUTH_DEPLOYMENT_EPOCH")
            if not re.fullmatch(r"[A-Za-z0-9_-]{1,128}", epoch):
                raise DeploymentError("Cleanup requires a valid external deployment epoch")
            image = required("AUTH_IMAGE")
            if not re.fullmatch(r"ghcr\.io/repoagent/controller-auth@sha256:[0-9a-f]{64}", image):
                raise DeploymentError("Cleanup requires an immutable approved auth image")
            self.params.update(image=image, deploymentEpoch=epoch)
            self.attest_cleanup()
            self.resources.append((CLEANUP_JOB, self.app + "-cleanup", "2025-01-01", "auth-cleanup"))
        self.cleanup_write_id = self.resource_id(RESOURCE_TYPES[0] if self.identity_only else CLEANUP_JOB,
                                                 self.app + "-cleanup")

    def cleanup_env(self):
        return {"Broker__TableEndpoint": f"https://{self.storage}.table.core.windows.net/",
                "Broker__TableName": self.table, "Broker__DeploymentEpoch": self.params["deploymentEpoch"],
                "Broker__ManagedIdentityClientId": self.identity["clientId"]}

    def cleanup_job(self, job, after_apply):
        properties, identity = job.get("properties", {}), job.get("identity", {})
        config, template = properties.get("configuration", {}), properties.get("template", {})
        trigger = config.get("triggerType")
        schedule = {"cronExpression": "* * * * *", "parallelism": 1, "replicaCompletionCount": 1}
        expected = {"triggerType": trigger, "replicaRetryLimit": 0, "replicaTimeout": 90}
        if trigger == "Schedule":
            expected["scheduleTriggerConfig"] = schedule
        elif trigger == "Manual" and not after_apply:
            expected["manualTriggerConfig"] = {"parallelism": 1, "replicaCompletionCount": 1}
        else:
            raise DeploymentError("Cleanup trigger differs from the fixed schedule/manual configuration")
        # Empty provider-normalized collections grant no additional configuration.
        config = {key: value for key, value in config.items()
                  if not (key in ("secrets", "registries", "identitySettings") and value in (None, []))}
        trigger_config = config.get("scheduleTriggerConfig" if trigger == "Schedule" else "manualTriggerConfig", {})
        if (config != expected or type(config.get("replicaRetryLimit")) is not int
                or type(config.get("replicaTimeout")) is not int
                or any(type(trigger_config.get(key)) is not int for key in ("parallelism", "replicaCompletionCount"))
                or properties.get("workloadProfileName") != "Consumption"
                or properties.get("environmentId", "").lower() != self.resource_id(RESOURCE_TYPES[2], self.app + "-environment").lower()
                or identity.get("type") != "UserAssigned"
                or [key.lower() for key in identity.get("userAssignedIdentities", {})] != [self.resource_id(RESOURCE_TYPES[0], self.app + "-cleanup").lower()]
                or any(key not in ("containers", "initContainers", "volumes") for key in template)
                or template.get("initContainers") or template.get("volumes")):
            raise DeploymentError("Cleanup configuration/environment/identity differs")
        containers = template.get("containers", [])
        if len(containers) != 1:
            raise DeploymentError("Cleanup requires exactly one container")
        container = containers[0]
        image = container.get("image")
        if not isinstance(image, str) or not re.fullmatch(r"ghcr\.io/repoagent/controller-auth@sha256:[0-9a-f]{64}", image):
            raise DeploymentError("Cleanup prior image must be an immutable approved-repository digest")
        if after_apply:
            if image != self.params["image"]:
                raise DeploymentError("Cleanup readback differs from desired image")
        elif self.job_seen and image != self.prior_image:
            raise DeploymentError("Cleanup prior image changed during planning")
        env = container.get("env", [])
        if (container.get("name") != "cleanup" or container.get("command") != ["dotnet", "RepoAgent.Auth.dll"]
                or container.get("args") != ["cleanup", "--once"]
                or container.get("resources") != {"cpu": 0.25, "memory": "0.5Gi"}
                or any(key not in ("name", "image", "command", "args", "resources", "env", "probes", "volumeMounts") for key in container)
                or container.get("probes") or container.get("volumeMounts")
                or len(env) != 4 or any(set(item) != {"name", "value"} for item in env)
                or {item["name"]: item["value"] for item in env} != self.cleanup_env()):
            raise DeploymentError("Cleanup command/container/environment differs")
        if not after_apply:
            self.prior_image, self.job_seen = image, True
        self.current = job

    def runtime_configuration(self):
        gates = ("AUTH_PROVIDER_READY", "AUTH_STORAGE_READY", "AUTH_CLEANUP_READY", "AUTH_RUNTIME_ACCESS_READY")
        if self.mode == "promote-staged":
            gates += ("AUTH_DEPLOYMENT_READY",)
        for name in (() if self.isolated else gates):
            if required(name) != "true":
                raise DeploymentError(f"Runtime gate is not ready: {name}")
        for name, param, kind in (
            ("AUTH_SIGNING_KEY_URI", "signingKeyUri", "keys"),
            ("AUTH_DATA_PROTECTION_KEY_URI", "dataProtectionKeyUri", "keys"),
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
        epoch = required("AUTH_DEPLOYMENT_EPOCH")
        if not re.fullmatch(r"[A-Za-z0-9_-]{1,128}", epoch):
            raise DeploymentError("AUTH_DEPLOYMENT_EPOCH must be 1-128 ASCII letters/digits/hyphen/underscore")
        self.params["deploymentEpoch"] = epoch

    def request_budget(self):
        for name, param, default, maximum in (
            ("AUTH_REQUEST_LIMIT_PER_MINUTE", "requestLimitPerMinute", 120, 10000),
            ("AUTH_CONCURRENT_REQUEST_LIMIT", "concurrentRequestLimit", 8, 128),
            ("AUTH_REQUEST_TIMEOUT_SECONDS", "requestTimeoutSeconds", 15, 60),
        ):
            value = os.environ.get(name, str(default))
            if not re.fullmatch(r"[0-9]+", value) or not 1 <= int(value) <= maximum:
                raise DeploymentError(f"{name} must be an integer between 1 and {maximum}")
            self.params[param] = int(value)

    def resource_id(self, kind, name):
        provider, *types = kind.split("/")
        return self.scope + "/providers/" + provider + "/" + "/".join(part for pair in zip(types, name.split("/"), strict=True) for part in pair)

    def az(self, args, allow_missing=False, lists=False, empty=False):
        if self.source and (args[:3] == ["deployment", "group", "create"]
                            or args[:3] in (["containerapp", "revision", "activate"], ["containerapp", "revision", "deactivate"])):
            accepted_source()
        timeout = 1200 if args[:3] == ["deployment", "group", "create"] else 120
        if self.deadline is not None:
            timeout = max(1, self.deadline - time.monotonic())
        result = subprocess.run(["az", *args, "--subscription", self.subscription, "--output", "json", "--only-show-errors"],
                                text=True, capture_output=True, check=False, timeout=timeout)
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
        if empty:
            return {}
        try:
            value = json.loads(result.stdout)
        except ValueError as error:
            raise DeploymentError("Azure malformed JSON response") from error
        if not isinstance(value, list if lists else dict):
            raise DeploymentError("Azure malformed object response")
        return value

    def inspect(self, verify_ingress=False, require_resources=False):
        self.current = None
        logs = environment_logs = None
        group = self.az(["group", "show", "--name", self.group])
        if str(group.get("id", "")).lower() != self.scope.lower() or group.get("location") != self.location:
            raise DeploymentError("Azure resource group identity/location mismatch")
        for kind, name, version, role in self.resources:
            resource_id = self.resource_id(kind, name)
            resource = self.az(["resource", "show", "--ids", resource_id, "--api-version", version], allow_missing=True)
            if resource is None:
                if self.cleanup and not require_resources:
                    if self.identity_only and kind == RESOURCE_TYPES[0] and not any(self.identity.values()):
                        continue
                    if not self.identity_only and kind == CLEANUP_JOB and not self.job_seen:
                        continue
                    raise DeploymentError("Cleanup requires existing owned dependencies; absence is not grant authority")
                if require_resources:
                    raise DeploymentError("Deployed resource unavailable after apply; result unconfirmed")
                if not self.foundation and (role != "auth-service" or verify_ingress or self.mode in ("promote-staged", "stage-stop", "stage-resume")):
                    raise DeploymentError("Runtime requires existing foundation and operator-granted access")
                continue
            if str(resource.get("id", "")).lower() != resource_id.lower() or str(resource.get("type", "")).lower() != kind.lower():
                raise DeploymentError("Azure malformed resource identity")
            # Table ARM resources cannot carry tags/metadata: ownership follows
            # the already-checked account and reserved exact Table ID. No Table
            # ACLs or neighboring tables are rewritten.
            table = kind.endswith("/tables")
            container = kind.endswith("/containers")
            if table:
                continue
            if container and not isinstance(resource.get("properties"), dict):
                raise DeploymentError("Azure malformed container properties")
            tags = resource.get("properties", {}).get("metadata") if container else resource.get("tags")
            expected = {**OWNERSHIP, "role": role}
            if not isinstance(tags, dict) or any(tags.get(key) != value for key, value in expected.items()):
                raise DeploymentError(f"Ownership conflict for {name}; refusing adoption")
            if not container and resource.get("location") != self.location:
                raise DeploymentError(f"Resource location conflict for {name}")
            if self.cleanup and kind == RESOURCE_TYPES[1]:
                logs = resource.get("properties", {})
            if self.cleanup and kind == CLEANUP_JOB:
                self.cleanup_job(resource, require_resources)
            if kind == RESOURCE_TYPES[2]:
                if self.cleanup:
                    environment_logs = resource.get("properties", {}).get("appLogsConfiguration", {})
                properties = resource.get("properties")
                domain = properties.get("defaultDomain") if isinstance(properties, dict) else None
                try:
                    origin = canonical_origin(self.app, domain)
                except ValueError as error:
                    raise DeploymentError(str(error)) from None
                if self.origin is not None and origin != self.origin:
                    raise DeploymentError("Owned environment domain changed during deployment")
                self.origin = origin
            if self.activation and kind == RESOURCE_TYPES[6] and not verify_ingress:
                self.staged_app(resource)
                self.current = resource
                if self.mode not in ("stage-stop", "stage-resume"):
                    self.revision = resource.get("properties", {}).get("latestRevisionName")
            if verify_ingress and kind == RESOURCE_TYPES[6]:
                properties = resource.get("properties")
                if self.activation and isinstance(properties, dict):
                    self.revision = properties.get("latestRevisionName")
                configuration = properties.get("configuration") if isinstance(properties, dict) else None
                ingress = configuration.get("ingress") if isinstance(configuration, dict) else None
                if not isinstance(ingress, dict):
                    raise DeploymentError("Deployed public ingress is unavailable")
                if (properties.get("environmentId", "").lower() != self.resource_id(
                        RESOURCE_TYPES[2], self.app + "-environment").lower()
                        or ingress.get("external") is not True
                        or ingress.get("fqdn") != self.origin.removeprefix("https://")):
                    raise DeploymentError("Deployed public ingress differs from the canonical broker origin")
            if kind == RESOURCE_TYPES[0]:
                properties = resource.get("properties")
                if not isinstance(properties, dict):
                    raise DeploymentError("Azure malformed runtime identity properties")
                for key in self.identity:
                    value = properties.get(key)
                    if not isinstance(value, str) or not re.fullmatch(r"[0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}", value):
                        raise DeploymentError("Azure malformed runtime identity " + key)
                    if self.identity[key] is not None and self.identity[key] != value:
                        raise DeploymentError("Owned runtime identity changed during deployment")
                    self.identity[key] = value

        if self.cleanup and (not isinstance(logs, dict) or not logs.get("customerId")
                or logs.get("retentionInDays") != 30 or logs.get("workspaceCapping", {}).get("dailyQuotaGb") != 1
                or not isinstance(environment_logs, dict) or environment_logs.get("destination") != "log-analytics"
                or environment_logs.get("logAnalyticsConfiguration", {}).get("customerId") != logs["customerId"]):
            raise DeploymentError("Cleanup requires the owned bounded log workspace/environment binding")

    def staged_template(self, template):
        containers = template.get("containers", [])
        rules = template.get("scale", {}).get("rules")
        if (len(containers) != 1 or containers[0].get("name") != "auth-service"
                or containers[0].get("image") != self.params["image"]
                or any(containers[0].get(key) for key in ("command", "args", "volumeMounts"))
                or template.get("volumes", [])
                or containers[0].get("resources", {}).get("cpu") != 0.25
                or containers[0].get("resources", {}).get("memory") != "0.5Gi"
                or template.get("initContainers", [])
                or template.get("scale", {}).get("minReplicas") != 1
                or template.get("scale", {}).get("maxReplicas") != 1
                or (rules is not None and rules != [])):
            raise DeploymentError("Existing staging template differs from the accepted artifact/capacity")
        env = containers[0].get("env", [])
        values = {item["name"]: item.get("value") for item in env}
        expected = {"Broker__BrokerOrigin": self.origin, "AZURE_CLIENT_ID": self.identity["clientId"],
                    "Broker__ManagedIdentityClientId": self.identity["clientId"],
                    "Auth__SigningKeyUri": self.params["signingKeyUri"],
                    "Auth__DataProtectionKeyUri": self.params["dataProtectionKeyUri"],
                    "Broker__DeploymentEpoch": self.params["deploymentEpoch"],
                    "ASPNETCORE_HTTP_PORTS": "8080", "Auth__DataProtectionApplicationName": "repoagent-auth-" + self.app,
                    "Auth__DataProtectionBlobUri": f"https://{self.storage}.blob.core.windows.net/data-protection/keyring.xml",
                    "Broker__TableEndpoint": f"https://{self.storage}.table.core.windows.net/", "Broker__TableName": self.table,
                    **{"Broker__" + key[0].upper() + key[1:]: str(self.params[key]) for key in
                       ("requestLimitPerMinute", "concurrentRequestLimit", "requestTimeoutSeconds")}}
        if len(values) != len(env) or values != expected or any("secretRef" in item for item in env):
            raise DeploymentError("Existing staging runtime identity/origin/key/epoch differs")

    def staged_app(self, app):
        properties, identity = app.get("properties", {}), app.get("identity", {})
        config = properties.get("configuration", {})
        dapr = config.get("dapr")
        if (config.get("ingress") is not None or config.get("activeRevisionsMode") != "Single"
                or config.get("maxInactiveRevisions") != 3 or config.get("secrets", []) or config.get("registries", [])
                or (dapr is not None and (not isinstance(dapr, dict) or dapr.get("enabled", False) is not False))
                or properties.get("workloadProfileName") != "Consumption"
                or properties.get("environmentId", "").lower() != self.resource_id(RESOURCE_TYPES[2], self.app + "-environment").lower()
                or identity.get("type") != "UserAssigned"
                or [key.lower() for key in identity.get("userAssignedIdentities", {})] != [self.resource_id(RESOURCE_TYPES[0], self.app + "-runtime").lower()]):
            raise DeploymentError("Refusing public or foreign staging app configuration/identity")
        self.staged_template(properties.get("template", {}))

    def stage_lifecycle(self):
        self.inspect()
        self.revision = required("AUTH_STAGE_REVISION")
        if not re.fullmatch(re.escape(self.app) + r"--[a-z0-9-]+", self.revision):
            raise DeploymentError("Expected the exact accepted staging revision")
        target = ["--name", self.app, "--resource-group", self.group]
        selected = self.az(["containerapp", "revision", "show", *target, "--revision", self.revision])
        if selected.get("name") != self.revision:
            raise DeploymentError("Staging revision identity mismatch")
        self.staged_template(selected.get("properties", {}).get("template", {}))
        self.inspect()  # Immediately before activate/deactivate, including no-ingress and digest.
        action = "deactivate" if self.mode == "stage-stop" else "activate"
        self.az(["containerapp", "revision", action, *target, "--revision", self.revision], empty=True)
        self.deadline = time.monotonic() + 60
        while time.monotonic() < self.deadline:
            self.inspect()
            revisions = self.az(["containerapp", "revision", "list", "--all", *target], lists=True)
            names, active, replicas = set(), [], []
            for revision in revisions:
                name, properties = revision.get("name"), revision.get("properties", {})
                if (not isinstance(name, str) or not name.startswith(self.app + "--") or name in names
                        or not isinstance(properties.get("active"), bool)):
                    raise DeploymentError("Staging observation ambiguous; shutdown/resume unconfirmed")
                names.add(name)
                if properties["active"]:
                    active.append(name)
                observed = self.az(["containerapp", "replica", "list", *target, "--revision", name], lists=True)
                if any(not isinstance(item, dict) or not item.get("name") for item in observed):
                    raise DeploymentError("Malformed replica observation; shutdown/resume unconfirmed")
                replicas.extend((name, item["name"]) for item in observed)
            if self.revision not in names:
                raise DeploymentError("Selected revision missing; shutdown/resume unconfirmed")
            if self.mode == "stage-stop" and not active and not replicas:
                return dict(self.outputs(False), shutdownConfirmed=True)
            if self.mode == "stage-resume" and active == [self.revision] and len(replicas) == 1 and replicas[0][0] == self.revision:
                self.replica = replicas[0][1]
                self.local_health(target)
                return dict(self.outputs(False), resumed=True)
            time.sleep(min(1, max(0, self.deadline - time.monotonic())))
        raise DeploymentError("Staging shutdown/resume unconfirmed after 60 seconds; inspect before retrying")

    def local_health(self, target):
        # Health has no credentials/body. Actual private data-plane checks remain operator-only.
        shell = "for p in live ready; do " + (
            "if command -v curl >/dev/null; then curl -fsS -o /dev/null http://127.0.0.1:8080/health/$p; "
            "elif command -v wget >/dev/null; then wget -q -O /dev/null http://127.0.0.1:8080/health/$p; "
            "else exit 1; fi || exit 1; done; printf AUTH_LOCAL_HEALTH_OK")
        # Azure CLI's Unix exec requires terminal stdin even in a noninteractive workflow.
        # Only stdin uses the PTY; provider output stays captured, with no private input.
        master, slave = pty.openpty()
        try:
            result = subprocess.run(["az", "containerapp", "exec", *target, "--subscription", self.subscription,
                                     "--revision", self.revision, "--replica", self.replica, "--container", "auth-service",
                                     "--command", "/bin/sh -c '" + shell + "'"], stdin=slave, capture_output=True, text=True,
                                    timeout=max(1, self.deadline - time.monotonic()))
        finally:
            # run() kills and waits for its child on timeout; always release both terminal FDs.
            os.close(slave)
            os.close(master)
        if result.returncode or "AUTH_LOCAL_HEALTH_OK" not in result.stdout.splitlines():
            raise DeploymentError("Local health/tools unavailable in selected runtime context; resume unconfirmed")

    def run(self):
        if self.mode in ("stage-stop", "stage-resume"):
            return self.stage_lifecycle()
        if self.mode == "cost-plan":
            from cost_plan import budget_plan
            try:
                plan = budget_plan(self.scope)
            except ValueError as error:
                # This local validator emits fixed messages; decoding errors may contain input bytes.
                raise DeploymentError(str(error) if type(error) is ValueError else "Budget plan must contain valid UTF-8 JSON") from None
            return {**self.outputs(False), "budgetPlan": plan}
        if self.mode in ("check", "foundation-check"):
            return self.outputs(False)
        # Validate the real template before any Azure calls.
        subprocess.run([str(HERE / "validate.sh")], check=True, stdout=sys.stderr)
        with tempfile.TemporaryDirectory(prefix="auth-deployment-") as directory:
            compiler = shutil.which(os.environ.get("BICEP_BIN", "bicep")) or str(Path.home() / ".azure/bin/bicep")
            template_name = "cleanup" if self.cleanup else "main"
            template = Path(directory) / (template_name + ".json")
            subprocess.run([compiler, "build", str(HERE / (template_name + ".bicep")), "--outfile", str(template)], check=True, stdout=sys.stderr)
            from validate_arm import validate, validate_cleanup
            (validate_cleanup if self.cleanup else validate)(template)
            self.inspect()
            if self.origin is not None:
                print(f"Broker origin: {self.origin}", file=sys.stderr)
            parameters = Path(directory) / "parameters.json"
            parameters.write_text(json.dumps({"parameters": {key: {"value": value} for key, value in self.params.items()}}), encoding="utf-8")
            common = ["--resource-group", self.group, "--name", f"auth-{self.app}", "--mode", "Incremental",
                      "--template-file", str(template), "--parameters", "@" + str(parameters)]
            self.az(["deployment", "group", "validate", *common])
            plan = self.az(["deployment", "group", "what-if", *common, "--no-pretty-print", "--result-format", "FullResourcePayloads"])
            changes = plan.get("changes")
            if plan.get("status") != "Succeeded" or not isinstance(changes, list):
                raise DeploymentError("Azure malformed or unsuccessful what-if")
            allowed = ({self.cleanup_write_id.lower()} if self.cleanup else
                       {self.resource_id(kind, name).lower() for kind, name, _, _ in self.resources})
            for change in changes:
                if not isinstance(change, dict) or change.get("changeType") not in ("Create", "Modify", "NoChange", "Ignore"):
                    raise DeploymentError("What-if contains deletion or unsupported changes")
                if change["changeType"] != "Ignore" and str(change.get("resourceId", "")).lower() not in allowed:
                    raise DeploymentError("What-if contains a resource outside the owned manifest")
                if change["changeType"] != "Ignore":
                    print(f"Plan: {change['changeType']} {change['resourceId']}", file=sys.stderr)
            applied = self.mode in ("apply", "foundation-apply", "stage-apply", "promote-staged", "cleanup-identity-apply", "cleanup-apply")
            if applied:
                self.inspect()  # Recheck after planning, immediately before writes.
                if self.cleanup and not self.identity_only:
                    self.attest_cleanup()
                result = self.az(["deployment", "group", "create", *common])
                if not isinstance(result.get("properties"), dict) or result["properties"].get("provisioningState") != "Succeeded":
                    raise DeploymentError("Deployment did not report Succeeded; inspect Azure before retrying")
                if self.cleanup:
                    try:
                        self.inspect(require_resources=True)
                    except (DeploymentError, OSError, ValueError, KeyError, TypeError, AttributeError, subprocess.SubprocessError) as error:
                        raise DeploymentError("Cleanup apply unconfirmed; inspect before retrying") from error
                elif not self.foundation:
                    self.inspect(verify_ingress=not self.isolated, require_resources=True)
                    if self.isolated:
                        self.revision = self.current.get("properties", {}).get("latestRevisionName")
                        if not isinstance(self.revision, str) or not re.fullmatch(re.escape(self.app) + r"--[a-z0-9-]+", self.revision):
                            raise DeploymentError("Staging revision unavailable after apply")
                        selected = self.az(["containerapp", "revision", "show", "--name", self.app,
                                            "--resource-group", self.group, "--revision", self.revision])
                        if selected.get("name") != self.revision:
                            raise DeploymentError("Staging revision identity mismatch after apply")
                        self.staged_template(selected.get("properties", {}).get("template", {}))
                    if self.mode == "promote-staged":
                        result = subprocess.run([sys.executable, str(ROOT / "scripts/delivery/deploy-auth.py"), "deploy"],
                                                capture_output=True, text=True)
                        if result.returncode:
                            raise DeploymentError("Same-digest public rollout/origin verification failed")
                # Foundation creates the identity; read its platform-issued IDs.
                if self.foundation and not self.cleanup:
                    self.inspect(require_resources=True)
                    if not all(self.identity.values()):
                        raise DeploymentError("Deployment identity unavailable after apply; inspect Azure before retrying")
        return self.outputs(applied)

    def outputs(self, applied):
        from cost_plan import cleanup_capacity
        if self.cleanup:
            return {"operation": self.mode, "source": self.source, "image": self.params.get("image"),
                    "deploymentEpoch": self.params.get("deploymentEpoch"), "appName": self.app,
                    "cleanupJobId": self.resource_id(CLEANUP_JOB, self.app + "-cleanup"),
                    "cleanupIdentityId": self.resource_id(RESOURCE_TYPES[0], self.app + "-cleanup"),
                    "cleanupIdentityClientId": self.identity["clientId"], "cleanupIdentityPrincipalId": self.identity["principalId"],
                    "environmentId": self.resource_id(RESOURCE_TYPES[2], self.app + "-environment"),
                    "tableResourceId": self.resource_id(RESOURCE_TYPES[7], self.storage + "/default/" + self.table),
                    "tableEndpoint": f"https://{self.storage}.table.core.windows.net/", "tableName": self.table,
                    "cleanupConfiguration": None if self.identity_only else self.cleanup_env(),
                    "cleanupCapacityPlan": cleanup_capacity(), "applied": applied, "releaseAcceptance": False}
        endpoint = f"https://{self.storage}.table.core.windows.net/"
        configuration = {
            "Broker__TableEndpoint": endpoint, "Broker__TableName": self.table,
            "Broker__BrokerOrigin": self.origin,
            "Broker__ManagedIdentityClientId": self.identity["clientId"],
            "Broker__RequestLimitPerMinute": self.params["requestLimitPerMinute"],
            "Broker__ConcurrentRequestLimit": self.params["concurrentRequestLimit"],
            "Broker__RequestTimeoutSeconds": self.params["requestTimeoutSeconds"],
        }
        if not self.foundation:
            configuration["Broker__DeploymentEpoch"] = self.params["deploymentEpoch"]
        return {"appName": self.app, "brokerOrigin": self.origin, "runtimeIdentityId": self.resource_id(RESOURCE_TYPES[0], self.app + "-runtime"),
                "runtimeIdentityClientId": self.identity["clientId"], "runtimeIdentityPrincipalId": self.identity["principalId"],
                "keyVaultName": self.vault, "dataProtectionBlobUri": f"https://{self.storage}.blob.core.windows.net/data-protection/keyring.xml",
                "tableEndpoint": endpoint, "tableName": self.table,
                "tableResourceId": self.resource_id(RESOURCE_TYPES[7], self.storage + "/default/" + self.table),
                "runtimeConfiguration": configuration,
                "capacityPlan": {"minReplicas": 1 if self.isolated else 0, "maxReplicas": 1, "cpu": 0.25, "memory": "0.5Gi",
                                 "logRetentionDays": 30, "logDailyQuotaGb": 1,
                                 "hardSpendingCap": False, "requestLimitsAreProcessLocal": True},
                "cleanupCapacityPlan": cleanup_capacity(), "applied": applied,
                **({"source": self.source, "image": self.params.get("image"), "isolated": self.isolated,
                    "releaseAcceptance": False, "revision": self.revision, "replica": self.replica} if self.activation else {})}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("mode", choices=("check", "plan", "apply", "foundation-check", "foundation-plan", "foundation-apply", "cost-plan", *ACTIVATION, *CLEANUP))
    args = parser.parse_args()
    try:
        print(json.dumps(Deployment(args.mode).run()))
    except (DeploymentError, OSError, ValueError, KeyError, TypeError, AttributeError, subprocess.SubprocessError) as error:
        print(f"Auth deployment refused: {error if isinstance(error, DeploymentError) else type(error).__name__}; shutdown/resume unconfirmed if requested", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
