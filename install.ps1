#Requires -Version 7.0

<#
.SYNOPSIS
Build, configure, verify and start RepoAgent with Docker Desktop Linux containers.
.DESCRIPTION
Prompts for missing values and explains each step. Reuses saved instance settings
as defaults. Imports an existing CODEX_HOME/auth.json (or ~/.codex/auth.json) when
available, otherwise offers container device sign-in. Install needs no host gh.
Revoke uses the host's existing gh authentication to inspect or withdraw one
repository approval; it needs no Docker, PEM or controller credential.
Use -NonInteractive to disable all prompts and browser opening; valid Codex
credentials must then be supplied or already present in the container.
.EXAMPLE
.\install.ps1
.EXAMPLE
.\install.ps1 -Repository my-org/my-repo -AppId 123456 -KeyPath C:\keys\app.pem -NonInteractive
.EXAMPLE
.\install.ps1 -Repository my-org/my-repo -AppId 123456 -KeyPath C:\keys\app.pem -CodexAuthPath C:\Users\me\.codex\auth.json -Instance my-repo -NoStart
.EXAMPLE
.\install.ps1 -Action Revoke -Repository my-org/my-repo
#>
[CmdletBinding()]
param(
    [ValidateSet('Install', 'Revoke')][string]$Action = 'Install',
    [string]$GitHubEnvironment = 'repoagent',
    [string]$ApprovalVariable = 'APPROVED_CONTROLLER',
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
if ($Action -eq 'Revoke') {
    & "$PSScriptRoot/scripts/revoke-approval.ps1" -Repository $Repository `
        -GitHubEnvironment $GitHubEnvironment -ApprovalVariable $ApprovalVariable `
        -ExpectedValueSha256 $ExpectedValueSha256 -ExpectedRepositoryId $ExpectedRepositoryId `
        -ExpectedEnvironmentId $ExpectedEnvironmentId -Apply:$Apply -NonInteractive:$NonInteractive
    exit $LASTEXITCODE
}
$provided = $PSBoundParameters
$cli = Join-Path $PSScriptRoot 'repoagent.ps1'
$stageIndex = 0
if ($Instance -cnotmatch '^[a-z0-9][a-z0-9-]{0,40}$') { throw 'Invalid instance. Use 1-41 lowercase letters, digits or hyphens; start with a letter or digit.' }
$env:REPOAGENT_INSTANCE = $Instance

function Show-Stage([string]$Title) {
    $script:stageIndex++
    Write-Host "`nStage $script:stageIndex/5 - $Title"
}

function Get-Setting([string]$Name, [string]$Field, [string]$Default, [string]$Prompt) {
    if ($provided.ContainsKey($Name)) { return [string]$provided[$Name] }
    if ($Field) {
        try {
            $saved = & $cli get $Field 2>$null
            if ($saved) { $Default = [string]$saved }
        } catch { } # A new instance has no settings yet.
    }
    if ($NonInteractive) { return $Default }
    $suffix = if ($Default) { " [$Default]" } else { '' }
    $answer = Read-Host "$Prompt$suffix"
    if ([string]::IsNullOrEmpty($answer)) { return $Default }
    return $answer
}

Write-Host 'RepoAgent installation: Docker check/build, GitHub configuration, Codex sign-in, verification, start.'
Write-Host "Instance: $Instance. Existing Docker volumes are preserved; stop a running instance before changing setup."
Show-Stage 'Check Docker and prepare the image'
Write-Host 'The agent can use its container filesystem/network. Its runtime mounts no host home directory or Docker socket.'
if (-not (Get-Command docker -ErrorAction SilentlyContinue)) { throw 'Docker CLI is missing. Install/start Docker Desktop and reopen PowerShell.' }
try {
    $dockerOs = & docker info --format '{{.OSType}}'
    if ($LASTEXITCODE -ne 0) { throw 'Docker is unavailable.' }
} catch { throw 'Cannot reach Docker. Start Docker Desktop and retry.' }
if ($dockerOs -ne 'linux') { throw 'RepoAgent needs Linux containers. Switch Docker Desktop to Linux containers.' }
$running = $false
try { $running = ((& docker inspect --format '{{.State.Running}}' "repoagent-$Instance" 2>$null) -eq 'true') } catch { }
if ($running) { throw 'Stop the running instance before installing; its stored data will be preserved.' }
if ($SkipBuild) { Write-Host 'Using the existing image; -SkipBuild skips the Docker build.' }
else {
    Write-Host 'Building the Linux image from this checkout. Build tools, Codex and gh are installed inside Docker.'
    $buildOptions = @{}
    if ($ImageManifest) { $buildOptions.Manifest = $ImageManifest }
    if ($DerivedDockerfile) { $buildOptions.DerivedDockerfile = $DerivedDockerfile }
    if ($BuildArg.Count) { $buildOptions.BuildArg = $BuildArg }
    & "$PSScriptRoot/scripts/build-image.ps1" @buildOptions
}

Show-Stage 'Choose the repository and GitHub App'
Write-Host 'Enable Discussions in the target repository: Settings > General > Features > Discussions.'
Write-Host 'For a new organization App: organization Settings > Developer settings > GitHub Apps > New GitHub App.'
Write-Host 'Choose a unique App name and your organization URL as Homepage URL. User authorization/callbacks are unused.'
Write-Host 'For polling: turn off Webhook Active; grant repository Discussions: read and write.'
Write-Host 'Choose Only on this account when the App belongs to the target organization.'
Write-Host 'Create the App; record its App ID; generate/download a private PEM key outside this checkout.'
Write-Host 'Install App > select the organization > Only select repositories > choose the target repository.'
Write-Host 'App creation, installation and permission approval happen in your browser; no GitHub account token is collected.'
if (-not $NonInteractive -and (-not $provided.ContainsKey('AppId') -or -not $provided.ContainsKey('KeyPath'))) {
    Write-Host 'Opening https://github.com/settings/apps (for an organization-owned App, use the organization settings above).'
    try { Start-Process 'https://github.com/settings/apps' | Out-Null } catch { Write-Host 'Open that URL manually in your browser.' }
}
$Repository = Get-Setting Repository repository '' 'Repository (OWNER/REPO)'
$AppId = Get-Setting AppId appId '' 'App ID or client ID'
$KeyPath = Get-Setting KeyPath keyPath '' 'PEM private-key file path'
$InstallationId = Get-Setting InstallationId installationId '' 'Expected installation ID (optional)'
$MentionUser = Get-Setting MentionUser mentionUser '' 'Dedicated mention user without @ (optional; empty uses the App bot)'
$Model = Get-Setting Model model 'gpt-6.1-sol' 'Codex model'
$Mode = Get-Setting Mode mode 'polling' 'Mode: polling, fixed-url or quick-tunnel'
$ToolUpdates = Get-Setting ToolUpdates toolUpdates 'latest' 'Tool updates: latest or pinned'
$WebSearch = Get-Setting WebSearch webSearch 'live' 'Web search: live, cached or disabled'
if (-not $Repository -or -not $AppId -or -not $KeyPath) { throw 'Repository, App ID and key path are required. Supply -Repository, -AppId and -KeyPath.' }
if (-not (Test-Path -LiteralPath $KeyPath -PathType Leaf)) { throw 'PEM private-key file is missing.' }
$KeyPath = (Resolve-Path -LiteralPath $KeyPath).ProviderPath
if ($KeyPath -match '[,\r\n]') { throw 'Private-key paths cannot contain commas or newlines.' }
$configuration = @('configure', '--repository', $Repository, '--app-id', $AppId, '--key-path', $KeyPath,
    "--mention-user=$MentionUser", '--model', $Model, '--mode', $Mode, '--tool-updates', $ToolUpdates, '--web-search', $WebSearch)
if ($Mode -ne 'polling') {
    Write-Host 'Webhook modes also require repository Webhooks: read and write; approve the installation permissions.'
    $ListenPort = Get-Setting ListenPort listenPort '8080' 'Listener port'
    $ReconcileInterval = Get-Setting ReconcileInterval reconcileInterval '300' 'Reconciliation interval in seconds'
    $configuration += @('--listen-port', $ListenPort, '--reconcile-interval', $ReconcileInterval)
    if ($Mode -eq 'fixed-url') {
        Write-Host 'Your external HTTPS proxy must forward /github/webhook to the container listener.'
        $PublicUrl = Get-Setting PublicUrl publicUrl '' 'Public HTTPS origin'
        $configuration += @('--public-url', $PublicUrl)
    } else { Write-Host 'Quick Tunnel creates a temporary public hostname for development; no Cloudflare account is needed.' }
}
if ($InstallationId) { $configuration += @('--installation-id', $InstallationId) }
Write-Host 'Checking that container UID 1000 can read the read-only PEM mount. Host file permissions are not changed.'
$image = if ($env:REPOAGENT_IMAGE) { $env:REPOAGENT_IMAGE } else { 'repoagent:local' }
& docker run --rm --mount "type=bind,src=$KeyPath,dst=/run/secrets/github-app.pem,readonly" --entrypoint sh $image -c 'test -r /run/secrets/github-app.pem'
if ($LASTEXITCODE -ne 0) { throw 'Docker cannot read the PEM as UID 1000. Check the file permissions and Docker file sharing.' }
Write-Host "Saving configuration for $Repository with App $AppId in instance $Instance."
& $cli @configuration

Show-Stage 'Reuse your Codex sign-in or sign in inside Docker'
Write-Host 'Only the selected auth.json is copied into the Docker credentials volume; host config and history are not imported.'
$authDirectory = if ($env:CODEX_HOME) { $env:CODEX_HOME } else { Join-Path $HOME '.codex' }
$detectedAuth = Join-Path $authDirectory 'auth.json'
if (-not (Test-Path -LiteralPath $detectedAuth -PathType Leaf)) { $detectedAuth = '' }
if ($DeviceLogin) { $CodexAuthPath = '' }
else {
    Write-Host 'Press Enter to use a detected cache; enter - to skip importing and use container sign-in.'
    $CodexAuthPath = Get-Setting CodexAuthPath '' $detectedAuth 'Existing Codex auth.json path (optional)'
}
if ($CodexAuthPath -and $CodexAuthPath -ne '-') {
    if (-not (Test-Path -LiteralPath $CodexAuthPath -PathType Leaf)) { throw 'Selected Codex auth.json file is missing.' }
    $CodexAuthPath = (Resolve-Path -LiteralPath $CodexAuthPath).ProviderPath
    Write-Host "Importing $CodexAuthPath read-only; its contents are not printed."
    & $cli import-codex-auth $CodexAuthPath
}
if ($NonInteractive) { & $cli login --non-interactive }
else {
    Write-Host 'Checking container sign-in. If needed, follow the printed device URL/code in your browser.'
    Write-Host 'Device login must be enabled in ChatGPT security/workspace settings. OS keyring credentials cannot be copied as a file.'
    & $cli login
}

Show-Stage 'Verify Codex and the installed GitHub App'
Write-Host 'Checking Codex login and the actual App installation, repository permission and bot identity before starting work.'
& $cli check
Show-Stage 'Start the agent and show next steps'
if ($NoStart) { Write-Host 'Verification succeeded. -NoStart leaves the agent stopped.' }
else {
    Write-Host 'Starting the container; latest mode checks tool updates before starting work. Waiting for the ready log.'
    & $cli start
}
Write-Host "For future PowerShell sessions: `$env:REPOAGENT_INSTANCE = '$Instance'"
Write-Host 'Use .\repoagent.ps1 logs --follow to follow logs; Ctrl+C stops the log viewer.'
Write-Host 'Use .\repoagent.ps1 stop, start or restart to manage the container.'
Write-Host 'Mention the actor login printed above, including [bot], in a Discussion body/comment to start a conversation.'
Write-Host 'Or, while stopped: .\repoagent.ps1 enroll https://github.com/OWNER/REPO/discussions/NUMBER'
Write-Host 'This installation handles Discussions. Its token does not grant repository code pushes or PR creation.'
Write-Host 'Configuration, Codex credentials, workspace, history, state and updated tools persist in Docker volumes.'
