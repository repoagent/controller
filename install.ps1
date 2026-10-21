#Requires -Version 7.0

<#
.SYNOPSIS
Preview or explicitly apply one local RepoAgent installation.
.DESCRIPTION
PowerShell 7.4+ composes host Agent-variable enrollment and private Docker startup.
Inputs override saved launch choices; missing required inputs prompt unless
-NonInteractive. Without -Apply there are no image builds, containers or writes.
Existing own-App options remain available with -Authentication OwnApp.
.EXAMPLE
.\install.ps1 -Repository owner/repo -BrokerOrigin https://broker.example -Instance demo -NonInteractive
.EXAMPLE
.\install.ps1 -Repository owner/repo -BrokerOrigin https://broker.example -Instance demo -NonInteractive -Apply
.EXAMPLE
.\install.ps1 -Authentication OwnApp -Repository owner/repo -AppId 123 -KeyPath C:\keys\app.pem -Apply
#>
[CmdletBinding()]
param(
    [ValidateSet('Install', 'Enroll', 'Revoke')][string]$Action = 'Install',
    [ValidateSet('Broker', 'OwnApp')][string]$Authentication,
    [string]$BrokerOrigin,
    [string]$StateDirectory,
    [ValidateSet('polling', 'webhook', 'polling-clone', 'webhook-clone')][string]$EnrollmentProfile,
    [switch]$Clone,
    [string]$ApprovalStatePath,
    [string]$EnrollmentFile,
    [string]$GitHubEnvironment,
    [string]$ApprovalVariable,
    [string]$ExpectedValueSha256,
    [string]$ExpectedRepositoryId,
    [string]$ExpectedEnvironmentId,
    [switch]$Apply,
    [string]$ImageManifest,
    [string]$DerivedDockerfile,
    [string[]]$BuildArg = @(),
    [string]$Repository,
    [string]$AppId,
    [string]$KeyPath,
    [string]$CodexAuthPath,
    [string]$Instance = $(if ($env:REPOAGENT_INSTANCE) { $env:REPOAGENT_INSTANCE } else { 'default' }),
    [string]$InstallationId,
    [string]$MentionUser,
    [string]$Model,
    [ValidateSet('polling', 'fixed-url', 'quick-tunnel')][string]$Mode,
    [string]$PublicUrl,
    [string]$ListenPort,
    [string]$ReconcileInterval,
    [ValidateSet('latest', 'pinned')][string]$ToolUpdates,
    [ValidateSet('live', 'cached', 'disabled')][string]$WebSearch,
    [switch]$DeviceLogin,
    [switch]$SkipBuild,
    [switch]$NoStart,
    [switch]$NonInteractive
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
if ($Action -eq 'Enroll') {
    if ($GitHubEnvironment -or $ExpectedEnvironmentId) { throw 'Enroll uses a repository Agent variable; Environment inputs are only for explicit legacy revoke.' }
    $enrollOptions = @{}
    if ($EnrollmentProfile) { $enrollOptions.Profile = $EnrollmentProfile }
    & "$PSScriptRoot/scripts/enroll-controller.ps1" @enrollOptions -Repository $Repository -BrokerOrigin $BrokerOrigin `
        -StateDirectory $StateDirectory -ApprovalVariable $ApprovalVariable `
        -Apply:$Apply -NonInteractive:$NonInteractive
    exit $LASTEXITCODE
}
if ($Action -eq 'Revoke') {
    & "$PSScriptRoot/scripts/revoke-approval.ps1" -Repository $Repository `
        -GitHubEnvironment $GitHubEnvironment -ApprovalVariable $ApprovalVariable `
        -ExpectedValueSha256 $ExpectedValueSha256 -ExpectedRepositoryId $ExpectedRepositoryId `
        -ExpectedEnvironmentId $ExpectedEnvironmentId -ApprovalStatePath $ApprovalStatePath `
        -EnrollmentFile $EnrollmentFile -Apply:$Apply -NonInteractive:$NonInteractive
    exit $LASTEXITCODE
}
$provided = $PSBoundParameters
$cli = Join-Path $PSScriptRoot 'repoagent.ps1'
$stage = 'validate the selected plan'
$diagnostic = 'Check the selected inputs and private saved state; nothing is repaired automatically.'
$oldInstance = $env:REPOAGENT_INSTANCE
$oldImage = $env:REPOAGENT_IMAGE
function Stop-Install([string]$Message) { $script:diagnostic = $Message; throw 'Installation stopped.' }
function Setting([string]$Name, [string]$Default = '', [switch]$Required) {
    $field = $Name.Substring(0, 1).ToLowerInvariant() + $Name.Substring(1)
    $value = if ($provided.ContainsKey($Name)) { [string]$provided[$Name] }
        elseif ($saved -and $saved.Contains($field)) { [string]$saved[$field] }
        else { $Default }
    if (-not $value -and $Required -and -not $NonInteractive) { $value = Read-Host "Supply -$Name" }
    if (-not $value -and $Required) { Stop-Install "Supply -$Name, or use the original instance/state directory to resume." }
    if ($value -match '[\p{Cc}]') { Stop-Install "-$Name cannot contain control characters." }
    return $value
}
function Input-File([string]$Path) {
    if ($Path -match '[,\r\n]' -or -not (Test-Path -LiteralPath $Path -PathType Leaf)) { Stop-Install 'Selected input file is missing or has a path containing commas/newlines.' }
    $full = (Resolve-Path -LiteralPath $Path).ProviderPath
    $item = Get-Item -LiteralPath $full -Force
    while ($item) {
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { Stop-Install 'Selected input paths cannot traverse links.' }
        $item = if ($item -is [IO.DirectoryInfo]) { $item.Parent } else { $item.Directory }
    }
    return $full
}
function Invoke-Runtime([string[]]$Arguments) {
    $global:LASTEXITCODE = 0
    & $cli @Arguments | Out-Host
    if ($LASTEXITCODE -ne 0) { Stop-Install 'Runtime operation failed. Private enrollment and instance data are retained; retry the same installation.' }
}
function Show-Binding($Binding) {
    # Only bounded public identity fields are suitable for a transcript. The native
    # consumer remains responsible for validating the frozen credential itself.
    Assert-Text $Binding.deviceId '\A[0-9a-f]{32}\z'
    Assert-Id $Binding.appId; Assert-Id $Binding.installationId
    Write-Host "Saved device: $($Binding.deviceId); App: $($Binding.appId); installation: $($Binding.installationId); authorization is checked on Apply."
}
function Read-Document([string]$Path) {
    return Read-HostJson ([Text.Encoding]::UTF8.GetString((Read-PrivateFileBytes $Path)))
}
try {
    if ($PSVersionTable.PSVersion -lt [version]'7.4') { Stop-Install 'Guided Install requires PowerShell 7.4 or later for checked durable host state.' }
    if ($Instance -cnotmatch '^[a-z0-9][a-z0-9-]{0,40}$') { Stop-Install 'Instance must have 1–41 lowercase letters, digits or hyphens, starting with a letter or digit.' }
    if ($GitHubEnvironment -or $ExpectedEnvironmentId -or $EnrollmentFile -or $ApprovalStatePath) { Stop-Install 'Install uses its selected state directory. Use Enroll/Revoke or the supplied-enrollment runtime commands for those inputs.' }
    Import-Module "$PSScriptRoot/scripts/host-enrollment.psm1" -DisableNameChecking
    Import-Module "$PSScriptRoot/scripts/private-file.psm1"
    . "$PSScriptRoot/scripts/azure/image-input.ps1"
    if (-not $StateDirectory) { $StateDirectory = Join-Path ([Environment]::GetFolderPath('UserProfile')) ".repoagent/instances/$Instance" }
    $StateDirectory = [IO.Path]::GetFullPath($StateDirectory)
    if ($StateDirectory -match '[,\p{Cc}]') { Stop-Install 'State directory cannot contain commas or control characters.' }
    $launchPath = Join-Path $StateDirectory 'launch.json'
    $enrollmentPath = Join-Path $StateDirectory 'enrollment.json'
    $saved = if (Test-Path -LiteralPath $launchPath) { Read-Document $launchPath } else { $null }
    $fields = @('schemaVersion','instance','stateDirectory','authentication','repository','brokerOrigin','approvalVariable','enrollmentProfile',
        'imageManifest','imageManifestSha256','derivedDockerfile','derivedDockerfileSha256','runtimeImageId','appId','keyPath','installationId',
        'mentionUser','model','mode','publicUrl','listenPort','reconcileInterval','toolUpdates','webSearch','codexAuthPath')
    if ($saved) {
        if ($saved.Contains('clone')) { $fields += 'clone' }
        Assert-Fields $saved $fields
        if ($saved.schemaVersion -ne 1 -or $saved.instance -cne $Instance -or -not $(if ($IsWindows) { [StringComparer]::OrdinalIgnoreCase.Equals($saved.stateDirectory, $StateDirectory) } else { [StringComparer]::Ordinal.Equals($saved.stateDirectory, $StateDirectory) })) { Stop-Install 'Saved launch coordinates differ from the selected instance/state directory.' }
        foreach ($field in $fields | Where-Object { $_ -ne 'schemaVersion' }) {
            if ($saved[$field] -isnot [string] -or $saved[$field] -match '[\p{Cc}]') { Stop-Install 'Invalid saved launch choice; inspect the original state without discarding recovery files.' }
        }
    }
    # Older launch records have no clone selection and remain non-clone. This is
    # launch intent only; credential versions/profiles remain owned by enrollment.
    $savedClone = $saved -and $saved.Contains('clone') -and $saved.clone -ceq 'true'
    if ($saved -and $saved.Contains('clone') -and $saved.clone -cnotin @('true','false')) { Stop-Install 'Invalid saved clone selection.' }
    $resolvedClone = if ($provided.ContainsKey('Clone')) { [bool]$Clone } else { [bool]$savedClone }
    if ($saved -and $resolvedClone -ne [bool]$savedClone) { Stop-Install 'Changing clone authority requires a fresh instance and enrollment.' }
    $Authentication = Setting Authentication $(if ($AppId -or $KeyPath) { 'OwnApp' } else { 'Broker' })
    if ($Authentication -notin @('Broker','OwnApp')) { Stop-Install 'Authentication must be Broker or OwnApp.' }
    $approvalPath = Join-Path $StateDirectory 'approval.json'
    $approval = if (Test-Path -LiteralPath $approvalPath) { Read-Document $approvalPath } else { $null }
    if ($approval) {
        if ($Authentication -ne 'Broker') { Stop-Install 'Existing host enrollment requires Broker authentication; choose a fresh instance for OwnApp.' }
        # The enrollment command remains responsible for validating/recovering its document.
        foreach ($pair in @(@('Repository','repository'),@('BrokerOrigin','brokerOrigin'),@('ApprovalVariable','approvalVariable'),@('EnrollmentProfile','profile'))) {
            if (-not $provided.ContainsKey($pair[0]) -and (-not $saved -or -not $saved[$pair[0]])) { Set-Variable $pair[0] $approval[$pair[1]] }
        }
    }
    $Repository = Setting Repository $Repository -Required
    $Mode = Setting Mode 'polling'
    $resolvedProfile = Setting EnrollmentProfile $(if ($EnrollmentProfile) { $EnrollmentProfile } else { 'polling' })
    $BrokerOrigin = Setting BrokerOrigin $BrokerOrigin
    $ApprovalVariable = Setting ApprovalVariable $(if ($ApprovalVariable) { $ApprovalVariable } else { 'APPROVED_CONTROLLER' })
    $AppId = Setting AppId
    $KeyPath = Setting KeyPath
    $InstallationId = Setting InstallationId
    $MentionUser = Setting MentionUser
    $Model = Setting Model 'gpt-6.1-sol'
    $ToolUpdates = Setting ToolUpdates 'latest'
    $WebSearch = Setting WebSearch 'live'
    $PublicUrl = Setting PublicUrl
    $ListenPort = Setting ListenPort '8080'
    $ReconcileInterval = Setting ReconcileInterval '300'
    if ($Repository -cnotmatch '^[A-Za-z0-9_.-]{1,100}/[A-Za-z0-9_.-]{1,100}$' -or @($Repository.Split('/') | Where-Object { $_ -in @('.','..') }).Count) { Stop-Install 'Repository must be OWNER/REPO.' }
    if ($Mode -notin @('polling','fixed-url','quick-tunnel') -or $ToolUpdates -notin @('latest','pinned') -or $WebSearch -notin @('live','cached','disabled')) { Stop-Install 'Invalid runtime mode, tool update or web search choice.' }
    if (-not $Model -or ($MentionUser -and $MentionUser -cnotmatch '^[A-Za-z0-9]+(?:-[A-Za-z0-9]+)*$')) { Stop-Install 'Supply a model and a valid optional mention login without @.' }
    [long]$parsedInstallation = 0
    if ($InstallationId -and ($InstallationId -notmatch '^[1-9][0-9]*$' -or -not [long]::TryParse($InstallationId, [ref]$parsedInstallation))) { Stop-Install 'InstallationId must be a positive integer.' }
    if ($ListenPort -notmatch '^[0-9]{1,5}$' -or [int]$ListenPort -notin 1..65535 -or $ReconcileInterval -notmatch '^[1-9][0-9]{0,8}$') { Stop-Install 'ListenPort must be 1–65535 and ReconcileInterval a positive integer.' }
    if ($Mode -eq 'fixed-url') { $PublicUrl = Setting PublicUrl -Required; $PublicUrl = Get-BrokerOrigin $PublicUrl }
    if ($Authentication -eq 'Broker') {
        if ($resolvedClone) { Stop-Install 'Guided Broker installation supports ordinary polling only. Use explicit host enrollment and a fresh instance for clone authority.' }
        if ($KeyPath -or $provided.ContainsKey('AppId')) { Stop-Install 'Broker and own-App key/App selectors cannot be combined.' }
        if ($Mode -ne 'polling' -or $resolvedProfile -ne 'polling') { Stop-Install 'Guided Broker installation supports polling only; mode and profile must both be polling.' }
        $BrokerOrigin = Setting BrokerOrigin $BrokerOrigin -Required
        $BrokerOrigin = Get-BrokerOrigin $BrokerOrigin
        $ApprovalVariable = $ApprovalVariable.ToUpperInvariant()
        $enrollOptions = @{ Repository=$Repository; BrokerOrigin=$BrokerOrigin; ApprovalVariable=$ApprovalVariable; Profile=$resolvedProfile; StateDirectory=$StateDirectory; NonInteractive=$true }
        $global:LASTEXITCODE = 0
        & "$PSScriptRoot/scripts/enroll-controller.ps1" @enrollOptions | Out-Host
        if ($LASTEXITCODE -ne 0) { Stop-Install 'Host enrollment plan failed. Check the selected coordinates/private state; no runtime work was attempted.' }
    } else {
        if ($BrokerOrigin -or $provided.ContainsKey('ApprovalVariable') -or $provided.ContainsKey('EnrollmentProfile')) { Stop-Install 'OwnApp cannot use broker enrollment selectors.' }
        $AppId = Setting AppId -Required; $KeyPath = Input-File (Setting KeyPath -Required)
        if ($AppId -cnotmatch '^(?:[0-9]+|Iv[A-Za-z0-9.]+)$') { Stop-Install 'Supply an App ID or client ID, not a token.' }
        $ApprovalVariable = ''; $resolvedProfile = ''
    }
    foreach ($field in @('authentication','repository','brokerOrigin','appId','keyPath')) {
        $selected = Get-Variable $field -ValueOnly
        if ($saved -and $saved[$field] -cne $selected) { Stop-Install 'Changing saved repository, authentication or App binding requires a fresh instance.' }
    }
    $ImageManifest = Input-File (Setting ImageManifest (Join-Path $PSScriptRoot 'image/default.json'))
    $software = Read-ImageInput $ImageManifest
    $manifestHash = (Get-FileHash -LiteralPath $ImageManifest -Algorithm SHA256).Hash.ToLowerInvariant()
    $DerivedDockerfile = Setting DerivedDockerfile
    if ($DerivedDockerfile) { $DerivedDockerfile = Input-File $DerivedDockerfile }
    if ($BuildArg.Count -and -not $DerivedDockerfile) { Stop-Install 'BuildArg requires DerivedDockerfile.' }
    $derivedHash = if ($DerivedDockerfile) { (Get-FileHash -LiteralPath $DerivedDockerfile -Algorithm SHA256).Hash.ToLowerInvariant() } else { '' }
    if ($saved -and ($saved.imageManifestSha256 -cne $manifestHash -or $saved.derivedDockerfileSha256 -cne $derivedHash)) { Stop-Install 'Image inputs changed since the saved plan. Choose a fresh instance for different image choices.' }
    $runtimeImageId = if ($saved) { $saved.runtimeImageId } else { '' }
    if ($runtimeImageId -and $runtimeImageId -cnotmatch '^sha256:[a-f0-9]{64}$') { Stop-Install 'Invalid saved immutable image ID.' }
    $image = if ($runtimeImageId) { $runtimeImageId } elseif ($oldImage) { $oldImage } else { 'repoagent:local' }
    $hasCredentials = $false
    $dockerReady = $false
    if (Get-Command docker -ErrorAction SilentlyContinue) {
        $os = & docker info --format '{{.OSType}}' 2>$null
        $dockerReady = $LASTEXITCODE -eq 0 -and $os -eq 'linux'
    }
    if ($dockerReady) {
        if ((& docker inspect --format '{{.State.Running}}' "repoagent-$Instance" 2>$null) -eq 'true' -and $Apply) { Stop-Install 'Stop this instance with repoagent.ps1 before applying; its data will be retained.' }
        # Read only: owned labels recover an interrupted launch-ID save without creating mounts.
        foreach ($storage in @('config','credentials','workspace','history','state','tools','enrollment')) {
            $name = "repoagent-$Instance-$storage"
            $names = @(& docker volume ls --filter "name=^$name`$" --format '{{.Name}}')
            if ($LASTEXITCODE -ne 0) { Stop-Install 'Cannot inspect selected Docker storage.' }
            if ($names -notcontains $name) { continue }
            if ($storage -eq 'credentials') { $hasCredentials = $true }
            $labels = (& docker volume inspect --format '{{json .Labels}}' $name) | ConvertFrom-Json -AsHashtable
            if ($LASTEXITCODE -ne 0) { Stop-Install 'Cannot inspect selected volume ownership.' }
            if ($Authentication -eq 'Broker') {
                if (-not $labels -or $labels['io.repoagent.owner'] -ne 'private-runtime-v1' -or $labels['io.repoagent.instance'] -cne $Instance -or $labels['io.repoagent.storage'] -cne $storage -or $labels['io.repoagent.image'] -cnotmatch '^sha256:[a-f0-9]{64}$') { Stop-Install 'Conflicting or legacy Docker storage; choose a fresh instance.' }
                if ($runtimeImageId -and $runtimeImageId -cne $labels['io.repoagent.image']) { Stop-Install 'Saved image conflicts with owned Docker storage.' }
                $runtimeImageId = $labels['io.repoagent.image']; $image = $runtimeImageId
            } elseif ($labels -and $labels['io.repoagent.owner'] -eq 'private-runtime-v1') { Stop-Install 'Broker storage cannot be used for OwnApp.' }
        }
    }
    if ($Apply -and -not $dockerReady) { Stop-Install 'Start Docker Desktop with Linux containers, then retry.' }
    $build = -not $SkipBuild -and -not $runtimeImageId
    if (-not $build -and $dockerReady) {
        $id = & docker image inspect --format '{{.Id}}' $image 2>$null
        if ($LASTEXITCODE -ne 0 -or $id -cnotmatch '^sha256:[a-f0-9]{64}$') { Stop-Install 'Selected existing controller image is unavailable; build it first or omit SkipBuild on a fresh instance.' }
        $runtimeImageId = $id; $image = $id
    }
    $authHome = if ($env:CODEX_HOME) { $env:CODEX_HOME } else { Join-Path ([Environment]::GetFolderPath('UserProfile')) '.codex' }
    $detectedAuth = Join-Path $authHome 'auth.json'
    $CodexAuthPath = Setting CodexAuthPath $(if (Test-Path -LiteralPath $detectedAuth -PathType Leaf) { $detectedAuth } else { '' })
    if (-not $CodexAuthPath -and -not $hasCredentials -and -not $DeviceLogin) { $CodexAuthPath = Setting CodexAuthPath '' -Required }
    if ($DeviceLogin) { $CodexAuthPath = '-' }
    if ($CodexAuthPath -and $CodexAuthPath -ne '-' -and (-not $hasCredentials -or $provided.ContainsKey('CodexAuthPath'))) { $CodexAuthPath = Input-File $CodexAuthPath }
    if (-not $hasCredentials -and (-not $CodexAuthPath -or $CodexAuthPath -eq '-') -and -not $DeviceLogin) { Stop-Install 'Supply -CodexAuthPath for an existing sign-in, or explicitly select interactive -DeviceLogin.' }
    if ($NonInteractive -and $DeviceLogin) { Stop-Install 'DeviceLogin requires interactive Apply; supply CodexAuthPath or reuse container credentials.' }
    $baseImage = if ($software.Contains('baseImage')) { $software.baseImage.reference } else {
        $match = [regex]::Match((Get-Content "$PSScriptRoot/scripts/compose-image.py" -Raw), "(?m)^RUNTIME = '([^']+)'\r?$")
        if (-not $match.Success) { Stop-Install 'Cannot read the maintained base image selection.' }; $match.Groups[1].Value
    }
    $plan = [ordered]@{ clone=$(if ($resolvedClone) { 'true' } else { 'false' }); schemaVersion=1; instance=$Instance; stateDirectory=$StateDirectory; authentication=$Authentication; repository=$Repository;
        brokerOrigin=$BrokerOrigin; approvalVariable=$ApprovalVariable; enrollmentProfile=$resolvedProfile; imageManifest=$ImageManifest;
        imageManifestSha256=$manifestHash; derivedDockerfile=$DerivedDockerfile; derivedDockerfileSha256=$derivedHash; runtimeImageId=$runtimeImageId;
        appId=$AppId; keyPath=$KeyPath; installationId=$InstallationId; mentionUser=$MentionUser; model=$Model; mode=$Mode; publicUrl=$PublicUrl;
        listenPort=$ListenPort; reconcileInterval=$ReconcileInterval; toolUpdates=$ToolUpdates; webSearch=$WebSearch; codexAuthPath=$CodexAuthPath }
    Write-Host "Instance: $Instance; repository: $Repository; authentication: $Authentication; mode: $Mode"
    if ($resolvedClone) { Write-Host 'OwnApp clone mode: Contents read in addition to the selected Discussion profile; fresh authority is required.' }
    Write-Host "Host launch/recovery state: $StateDirectory"
    Write-Host "Image: $(if ($runtimeImageId) { $runtimeImageId } else { 'pending build' }); base: $baseImage; platform: linux/amd64; maintained compatibility: Ubuntu 26.04."
    Write-Host "Manifest: $ImageManifest; SHA256: $manifestHash; controller source: $($software.controller.source)."
    if ($DerivedDockerfile) { Write-Host "Derived Dockerfile: $DerivedDockerfile; SHA256: $derivedHash." }
    Write-Host "Software: Codex $($software.agent.version), gh $($software.githubCli.version); tools: $($software.tools | ConvertTo-Json -Compress); model: $Model; updates: $ToolUpdates."
    Write-Host 'Download/build size and duration are unknown; depend on cache, selected base and tools. Full source/ABI build validation occurs on Apply.'
    Write-Host "Container: repoagent-$Instance. Retain config, credentials, workspace, history, state and tools volumes; broker enrollment volume is read-only at runtime."
    foreach ($storage in @('config','credentials','workspace','history','state','tools') + $(if ($Authentication -eq 'Broker') { @('enrollment') } else { @() })) {
        $destination = if ($storage -eq 'workspace') { '/workspace' } else { "/data/$storage" }
        Write-Host "  repoagent-$Instance-$storage -> $destination $(if ($storage -in @('config','enrollment')) { '(runtime read-only)' })"
    }
    if ($Authentication -eq 'OwnApp') { Write-Host "App: $AppId; expected installation: $(if ($InstallationId) { $InstallationId } else { 'discovered during check' }); read-only PEM mount: /run/secrets/github-app.pem" }
    Write-Host 'Runtime mounts no host home, host gh credentials, recovery journal or Docker socket. Only selected Codex auth.json is imported; host history/sign-in are preserved.'
    if ($Authentication -eq 'Broker') {
        if (Test-Path -LiteralPath $enrollmentPath) {
            $binding = Read-Document $enrollmentPath
            Show-Binding $binding
        } else { Write-Host 'Device and installation: pending enrollment; no identity has been allocated.' }
    }
    if (-not $Apply) { Write-Host 'Read-only preview. Rerun with -Apply to enroll, prepare the image, configure, verify and start.'; exit 0 }
    $stage = 'save launch coordinates'
    $missing = [Collections.Generic.Stack[string]]::new()
    $parent = $StateDirectory
    while (-not (Test-Path -LiteralPath $parent)) { $missing.Push($parent); $parent = [IO.Path]::GetDirectoryName($parent) }
    foreach ($directory in $missing) { $null = Initialize-PrivateDirectory $directory }
    $null = Initialize-PrivateDirectory $StateDirectory
    Write-PrivateJson $launchPath $plan
    if ($Authentication -eq 'Broker') {
        $stage = 'enroll or resume the same controller'
        & "$PSScriptRoot/scripts/enroll-controller.ps1" @enrollOptions -Apply | Out-Host
        if ($LASTEXITCODE -ne 0) { Stop-Install 'Host enrollment is incomplete. Retain this state and retry the same command; no runtime was started.' }
        $binding = Read-Document $enrollmentPath
        if ($InstallationId -and $InstallationId -cne [string]$binding.installationId) { Stop-Install 'Expected installation differs from the enrolled binding; no runtime started.' }
        $AppId = [string]$binding.appId; $InstallationId = [string]$binding.installationId
        Write-Host "Bound device: $($binding.deviceId); App: $AppId; installation: $InstallationId."
        $plan.appId = $AppId; $plan.installationId = $InstallationId
        Write-PrivateJson $launchPath $plan
    }
    $env:REPOAGENT_INSTANCE = $Instance
    $env:REPOAGENT_IMAGE = $image
    if ($build) {
        $stage = 'build the selected image'
        $options = @{ Manifest=$ImageManifest }
        if ($DerivedDockerfile) { $options.DerivedDockerfile = $DerivedDockerfile }
        if ($BuildArg.Count) { $options.BuildArg = $BuildArg }
        & "$PSScriptRoot/scripts/build-image.ps1" @options | Out-Host
        if ($LASTEXITCODE -ne 0) { Stop-Install 'Image build failed; enrollment is retained for retry.' }
        $runtimeImageId = & docker image inspect --format '{{.Id}}' $image
        if ($LASTEXITCODE -ne 0 -or $runtimeImageId -cnotmatch '^sha256:[a-f0-9]{64}$') { Stop-Install 'Cannot resolve the built immutable controller image.' }
    }
    $env:REPOAGENT_IMAGE = $runtimeImageId
    $plan.runtimeImageId = $runtimeImageId
    Write-PrivateJson $launchPath $plan
    $stage = 'configure and privately import enrollment'
    $configuration = @('configure', '--repository', $Repository, '--app-id', $AppId, "--mention-user=$MentionUser", "--model=$Model", '--mode', $Mode, '--tool-updates', $ToolUpdates, '--web-search', $WebSearch)
    if ($resolvedClone) { $configuration += '--clone' }
    if ($InstallationId) { $configuration += @('--installation-id', $InstallationId) }
    if ($Authentication -eq 'Broker') { $configuration += @('--broker-url', $BrokerOrigin, '--enrollment-file', $enrollmentPath) }
    else {
        & docker run --rm --mount "type=bind,src=$KeyPath,dst=/run/secrets/github-app.pem,readonly" --entrypoint sh $runtimeImageId -c 'test -r /run/secrets/github-app.pem'
        if ($LASTEXITCODE -ne 0) { Stop-Install 'Docker UID1000 cannot read the selected PEM; check permissions/file sharing.' }
        $configuration += @('--key-path', $KeyPath, '--listen-port', $ListenPort, '--reconcile-interval', $ReconcileInterval, "--public-url=$PublicUrl")
    }
    Invoke-Runtime $configuration
    $stage = 'check Codex sign-in'
    $reuseCredentials = $false
    if ($hasCredentials -and -not $DeviceLogin -and -not $provided.ContainsKey('CodexAuthPath')) {
        # A volume may exist after failed configure/import. Only successful login
        # establishes reusable credentials; never replace a working refresh cache.
        $global:LASTEXITCODE = 0
        try { & $cli login --non-interactive | Out-Host; $reuseCredentials = $LASTEXITCODE -eq 0 } catch { $reuseCredentials = $false }
    }
    if (-not $reuseCredentials) {
        if ($CodexAuthPath -and $CodexAuthPath -ne '-') { Invoke-Runtime @('import-codex-auth', (Input-File $CodexAuthPath)) }
        if ($DeviceLogin) { Invoke-Runtime @('login') } else { Invoke-Runtime @('login','--non-interactive') }
    }
    $stage = 'verify runtime authorization'
    Invoke-Runtime @('check')
    if ($NoStart) { Write-Host 'Verified; selected runtime remains stopped (-NoStart).' }
    else { $stage = 'start the selected runtime'; Invoke-Runtime @('start'); Write-Host 'Selected runtime is ready.' }
    Write-Host "Resume: ./install.ps1 -Instance '$Instance' -StateDirectory '$($StateDirectory.Replace("'", "''"))' -NonInteractive -Apply"
    Write-Host "Lifecycle: set REPOAGENT_INSTANCE=$Instance and REPOAGENT_IMAGE=$runtimeImageId; use ./repoagent.ps1 logs --follow, stop, start or restart."
    Write-Host 'Image publication is separate: scripts/publish-image.ps1. Azure planning is read-only: scripts/plan-azure.ps1. This result establishes local readiness only.'
} catch {
    Write-Warning "Could not $stage. $diagnostic"
    Write-Host "Selected instance: $Instance. Retain the original state directory and rerun the same inputs with -Apply."
    exit 1
} finally {
    $env:REPOAGENT_INSTANCE = $oldInstance
    $env:REPOAGENT_IMAGE = $oldImage
}
