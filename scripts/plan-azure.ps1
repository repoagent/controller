#Requires -Version 7.0
<# .SYNOPSIS
Discover explicit Azure resources and emit a nonsecret, read-only controller plan.
No resource changes, CLI context changes, login, secret reads or extension installs.
#>
[CmdletBinding()]
param(
    [string]$SubscriptionId,
    [string]$ResourceGroup,
    [string]$Location,
    [string]$EnvironmentName,
    [string]$RegistryName,
    [string]$Image,
    [string]$RepositoryId,
    [string]$ControllerId,
    [string]$AppName,
    [string]$ImageManifest = (Join-Path $PSScriptRoot '../image/default.json'),
    [ValidateRange(1,60)][int]$RequestTimeoutSeconds = 15
)
$ErrorActionPreference = 'Stop'
$diagnostic = 'Planning failed: verify nonsecret inputs and host Azure CLI prerequisites.'
function Fail([string]$Message) { $script:diagnostic = $Message; throw 'Planning stopped.' }
function Invoke-Discovery([string[]]$Arguments, [string]$ResourceId = '', [switch]$AllowMissing) {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $script:AzureCli
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $argv = @($Arguments) + @('--subscription',$SubscriptionId,'--output','json','--only-show-errors')
    if ($IsWindows -and [IO.Path]::GetExtension($script:AzureCli) -ieq '.cmd') {
        # Azure CLI's standard Windows entrypoint is a batch file. cmd expands
        # metacharacters even inside quotes: reject them before constructing its
        # command string. All accepted resource arguments are already allowlisted.
        foreach ($value in @($script:AzureCli) + $argv) {
            if ($value -match '[%!&|<>^"\r\n]') { Fail 'Azure CLI batch launcher path or arguments contain unsupported command characters. Use a plain installation path.' }
        }
        $start.FileName = Join-Path ([Environment]::GetFolderPath('System')) 'cmd.exe'
        $quoted = @(@($script:AzureCli) + $argv | ForEach-Object { '"' + $_ + '"' })
        $start.Arguments = '/d /s /c "' + ($quoted -join ' ') + '"'
    } else {
        foreach ($arg in $argv) { $start.ArgumentList.Add($arg) }
    }
    # Disable CLI auto-install so a read cannot install/upgrade extensions or prompt.
    $start.Environment['AZURE_EXTENSION_USE_DYNAMIC_INSTALL'] = 'no'
    $start.Environment['AZURE_CORE_COLLECT_TELEMETRY'] = 'no'
    $p = [Diagnostics.Process]::new(); $p.StartInfo = $start
    try {
        if (-not $p.Start()) { Fail 'Azure CLI could not start.' }
        $stdout = $p.StandardOutput.ReadToEndAsync(); $stderr = $p.StandardError.ReadToEndAsync()
        if (-not $p.WaitForExit($RequestTimeoutSeconds * 1000)) { Fail 'Azure discovery timed out; resource absence is unverified.' }
        $text = $stdout.GetAwaiter().GetResult(); $errorText = $stderr.GetAwaiter().GetResult()
        if ($p.ExitCode -ne 0) {
            # Only provider error codes establish absence; permission/network errors never do.
            if ($AllowMissing -and $errorText -match '(?m)^ERROR: \((ResourceNotFound|ResourceGroupNotFound)\)') { return $null }
            if ($errorText -match '(AuthorizationFailed|Forbidden|AuthenticationFailed|InvalidAuthenticationToken)') { Fail 'Azure discovery denied or inaccessible; resource absence is unverified.' }
            Fail 'Azure discovery failed; check subscription, permissions, network and installed containerapp support. Resource absence is unverified.'
        }
        try { $result = ConvertFrom-Json -InputObject $text -AsHashtable -ErrorAction Stop } catch { Fail 'Malformed Azure JSON response; resource absence is unverified.' }
        if ($result -isnot [Collections.IDictionary]) { Fail 'Malformed Azure resource response.' }
        if ($ResourceId -and $result.id -ine $ResourceId) { Fail 'Azure returned a wrong-subscription or unexpected resource identity.' }
        return $result
    } finally {
        if ($p.Id -and -not $p.HasExited) { $p.Kill($true); $p.WaitForExit() }
        $p.Dispose()
    }
}
try {
    foreach ($item in @($SubscriptionId,$ResourceGroup,$Location,$EnvironmentName,$RegistryName,$Image,$RepositoryId,$ControllerId)) {
        if ([string]::IsNullOrWhiteSpace($item)) { Fail 'Required: SubscriptionId, ResourceGroup, Location, EnvironmentName, RegistryName, Image, RepositoryId and ControllerId.' }
    }
    if ($SubscriptionId -notmatch '^[0-9a-fA-F]{8}(-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}$' -or $ControllerId -notmatch '^[0-9a-fA-F]{8}(-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}$' -or $RepositoryId -notmatch '^[1-9][0-9]{0,19}$') { Fail 'Use a subscription UUID, controller UUID and immutable numeric GitHub repository ID.' }
    if ($ResourceGroup -notmatch '^[a-zA-Z0-9_.()-]{1,90}$' -or $ResourceGroup.EndsWith('.') -or $Location -notmatch '^[a-z0-9]+$' -or $EnvironmentName -notmatch '^[a-zA-Z0-9][a-zA-Z0-9-]{0,58}[a-zA-Z0-9]$' -or $RegistryName -notmatch '^[a-zA-Z0-9]{5,50}$') { Fail 'Invalid Azure resource group, region, environment or registry name.' }
    if (-not $AppName) { $AppName = "repoagent-$RepositoryId" }
    if ($AppName.Length -gt 32 -or $AppName -cnotmatch '^[a-z][a-z0-9-]*[a-z0-9]$' -or $AppName.Contains('--')) { Fail 'AppName must be 2-32 lowercase letters/digits/hyphens, start with a letter, end alphanumeric and omit consecutive hyphens.' }
    if ($Image -cnotmatch '^[a-z0-9][a-z0-9.-]+(?::[0-9]+)?/[a-z0-9][a-z0-9._/-]*@sha256:[0-9a-f]{64}$') { Fail 'Image must be the final published controller registry/repository@sha256 digest, not a tag or local/base digest.' }
    . (Join-Path $PSScriptRoot 'azure/image-input.ps1')
    $software = Read-ImageInput $ImageManifest
    $cli = Get-Command az -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $cli) { Fail 'Azure CLI (az) must be installed and authenticated on the host, with containerapp support already installed.' }
    $script:AzureCli = $cli.Source
    $scope = "/subscriptions/$SubscriptionId"
    $groupId = "$scope/resourceGroups/$ResourceGroup"
    $environmentId = "$groupId/providers/Microsoft.App/managedEnvironments/$EnvironmentName"
    $appId = "$groupId/providers/Microsoft.App/containerApps/$AppName"
    $registryId = "$groupId/providers/Microsoft.ContainerRegistry/registries/$RegistryName"
    $account = Invoke-Discovery @('account','show')
    if ($account.id -ine $SubscriptionId -or $account.state -ne 'Enabled') { Fail 'Selected Azure subscription is wrong or disabled.' }
    $provider = Invoke-Discovery @('provider','show','--namespace','Microsoft.App')
    if ($provider.registrationState -ne 'Registered') { Fail 'Microsoft.App provider is not registered. Registration requires a separate operator action.' }
    $group = Invoke-Discovery @('group','show','--name',$ResourceGroup) $groupId -AllowMissing
    $env = $null; $app = $null; $registry = $null
    if ($group) {
        $env = Invoke-Discovery @('containerapp','env','show','--name',$EnvironmentName,'--resource-group',$ResourceGroup) $environmentId -AllowMissing
        $app = Invoke-Discovery @('containerapp','show','--name',$AppName,'--resource-group',$ResourceGroup) $appId -AllowMissing
        $registry = Invoke-Discovery @('acr','show','--name',$RegistryName,'--resource-group',$ResourceGroup) $registryId -AllowMissing
    }
    if ($env -and ($env.location -isnot [string] -or $env.properties.provisioningState -isnot [string])) { Fail 'Malformed Azure environment response.' }
    if ($app -and ($app.location -isnot [string] -or $app.properties.managedEnvironmentId -isnot [string])) { Fail 'Malformed Azure app response.' }
    if ($registry -and $registry.loginServer -isnot [string]) { Fail 'Malformed Azure registry response.' }
    if ($env -and ($env.location -ine $Location -or $env.properties.provisioningState -ne 'Succeeded' -or $env.properties.vnetConfiguration.internal -eq $true)) { Fail 'Incompatible Container Apps environment: require the selected region, successful provisioning and external access.' }
    if ($registry -and ($registry.loginServer -isnot [string] -or $Image.Split('/')[0] -ine $registry.loginServer)) { Fail 'Image registry does not match the discovered registry login server.' }
    if (-not $registry -and $Image.Split('/')[0] -ine "$($RegistryName.ToLowerInvariant()).azurecr.io") { Fail 'For a missing registry, Image must target its explicit Azure public-cloud login server.' }
    $action = 'create'; $ownership = 'absent'; $origin = $null
    if ($app) {
        if ($app.properties.managedEnvironmentId -ine $environmentId -or $app.location -ine $Location) { Fail 'Conflicting app name: existing app belongs to a different environment or region. No replacement is proposed.' }
        $ownership = 'unproven'; $action = 'explicit-adoption-required'
        if ($app.tags.'repoagent-controller-id' -ceq $ControllerId -and $app.tags.'repoagent-repository-id' -ceq $RepositoryId) { $ownership = 'matching-controller-and-repository-tags'; $action = 'verified-update' }
        $fqdn = $app.properties.configuration.ingress.fqdn
        if ($fqdn -is [string] -and $fqdn -cmatch '^[a-z0-9][a-z0-9.-]+\.azurecontainerapps\.io$' -and $app.properties.configuration.ingress.external -eq $true) { $origin = "https://$fqdn" }
    }
    $environmentAction = 'create'; if ($env) { $environmentAction = 'compatible-reuse' }
    $groupAction = 'create'; if ($group) { $groupAction = 'reuse' }
    $registryAction = 'create'; if ($registry) { $registryAction = 'reuse' }
    [ordered]@{
        schemaVersion = 1; evidence = 'read-only-discovery-plan; not live Azure acceptance'; readyToApply = $false
        subscriptionId = $SubscriptionId; resourceGroup = $ResourceGroup; location = $Location
        repositoryId = $RepositoryId; controllerId = $ControllerId; appName = $AppName
        software = $software; softwareManifestSha256 = (Get-FileHash -LiteralPath $ImageManifest -Algorithm SHA256).Hash.ToLowerInvariant()
        resources = @(@{id=$groupId;action=$groupAction},@{id=$environmentId;action=$environmentAction},@{id=$registryId;action=$registryAction},@{id=$appId;action=$action;ownership=$ownership})
        desired = @{ image=$Image; ingress=@{external=$true;allowInsecure=$false;targetPort=8080}; runAsUser=1000; minReplicas=1; maxReplicas=1; activeRevisionsMode='Single'; health=@{readiness=@{transport='TCP';port=8080};liveness=@{transport='TCP';port=8080};acceptance='Verify listener and webhook functionality on the deployed revision.'}; storage=@{mountPath='/data';status='unverified-release-gate'}; registryPull=@{authentication='user-assigned-managed-identity';identityResourceId=$null;role='AcrPull';roleDefinitionId='7f951dda-4ed3-4680-a7ca-43fe172d538d';scope=$registryId}; secrets=@{source='versioned-Key-Vault-references-only';references=@();status='unconfigured'} }
        origin = @{verifiedExisting=$origin;future='Read the provisioned app ingress FQDN after apply; never predict a ready endpoint.';webhook='Configure GitHub webhook HTTPS origin and versioned webhook-secret reference only after origin and delivery verification.'}
        gates = @('Verify the final published image digest, inventory/provenance and software manifest match; registry image existence was not queried.','Durable /data storage: validate UID1000 access, atomic writes, locks, restart persistence and backup/restore on actual Azure storage.','Broker hosted enrollment/renewal/revocation and outstanding-token barriers require acceptance. One replica does not fence old tokens.','Verify controller/repository ownership and explicit adoption approval before any unowned app mutation. Tags are operator-owned metadata, not authenticated broker enrollment.','Resolve runtime managed identity, registry permissions/ABAC mode and versioned Key Vault secret references; host credentials never enter the controller.','Capture existing revision, immutable image, secret versions, settings and storage backup; verify a rollback without deleting/recreating resources.','Apply must preserve unrelated settings, tags and data, re-read ownership and compatibility, and require a separately reviewed write plan.')
        costs = @('Container Apps compute with one minimum replica','Container Apps environment/log ingestion','Azure Container Registry storage/build/network','Durable storage capacity/transactions/backups','Key Vault and network/egress charges')
        operatorRoles = @(@{role='Contributor';scope=$groupId;purpose='Future resource writes, separately reviewed'},@{role='Role Based Access Control Administrator';scope=$registryId;purpose='Future scoped AcrPull assignment, subject to operator policy'})
        mutationPolicy = 'No writes performed. Never automatically adopt, delete or recreate.'
    } | ConvertTo-Json -Depth 20
} catch {
    [Console]::Error.WriteLine($diagnostic)
    exit 1
}
