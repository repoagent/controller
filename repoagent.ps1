#Requires -Version 5.1
param(
    [Parameter(Position = 0)][string]$Command = 'help',
    [Parameter(ValueFromRemainingArguments = $true)][string[]]$CommandArguments
)

# Windows host orchestration; application tools and credentials stay in Docker.
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$instance = if ($env:REPOAGENT_INSTANCE) { $env:REPOAGENT_INSTANCE } else { 'default' }
if ($instance -cnotmatch '^[a-z0-9][a-z0-9-]{0,40}$') { throw 'Invalid REPOAGENT_INSTANCE' }
$image = if ($env:REPOAGENT_IMAGE) { $env:REPOAGENT_IMAGE } else { 'repoagent:local' }
$containerName = "repoagent-$instance"
$volumeArguments = @()
$runtimeVolumes = @()
foreach ($storage in @('config', 'credentials', 'workspace', 'history', 'state', 'tools')) {
    $destination = if ($storage -eq 'workspace') { '/workspace' } else { "/data/$storage" }
    $mount = "type=volume,src=$containerName-$storage,dst=$destination"
    $volumeArguments += @('--mount', $mount)
    if ($storage -eq 'config') { $mount += ',readonly' }
    $runtimeVolumes += @('--mount', $mount)
}

function Invoke-Docker {
    # Windows PowerShell 5.1 can turn native stderr into terminating errors even
    # for successful commands. Use Docker's exit status as the failure signal.
    $ErrorActionPreference = 'Continue'
    & docker @args
    if ($LASTEXITCODE -ne 0) { throw "Docker failed (exit $LASTEXITCODE). See the output above." }
}

function Invoke-Config {
    Invoke-Docker run --rm --init @volumeArguments $image @args
}

function Test-AgentRunning {
    $ErrorActionPreference = 'Continue'
    $result = & docker inspect --format '{{.State.Running}}' $containerName 2>$null
    return ($LASTEXITCODE -eq 0 -and $result -eq 'true')
}

function Assert-Stopped {
    if (Test-AgentRunning) { throw 'Stop the agent before changing configuration, credentials or skills.' }
}

function Get-FileMount([string]$Path, [string]$Destination) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "File is missing: $Path" }
    $resolved = (Resolve-Path -LiteralPath $Path).ProviderPath
    if ($resolved -match '[,\r\n]') { throw 'Bind-mount paths cannot contain commas or newlines.' }
    return @('--mount', "type=bind,src=$resolved,dst=$Destination,readonly")
}

function Get-KeyMount {
    $key = Invoke-Config get keyPath
    return Get-FileMount $key '/run/secrets/github-app.pem'
}

function Start-Agent {
    if (Test-AgentRunning) { Write-Host "$containerName is already running."; return }
    $keyMount = Get-KeyMount
    # The container may not exist on first installation.
    $ErrorActionPreference = 'Continue'
    & docker rm $containerName 2>$null | Out-Null
    $ErrorActionPreference = 'Stop'
    $networkArguments = @()
    if ((Invoke-Config get mode) -eq 'fixed-url') {
        $port = Invoke-Config get listenPort
        $networkArguments = @('--publish', "127.0.0.1:${port}:$port")
    }
    Invoke-Docker run -d --init --name $containerName @networkArguments @runtimeVolumes @keyMount $image start
    $deadline = (Get-Date).AddMinutes(10)
    while ((Get-Date) -lt $deadline) {
        if (-not (Test-AgentRunning)) {
            & docker logs $containerName
            throw 'Agent startup failed; inspect the output above.'
        }
        $output = Invoke-Docker logs $containerName 2>&1
        if (($output -join "`n").Contains(' ready repository=')) { $output; return }
        Start-Sleep -Seconds 1
    }
    & docker logs $containerName
    Invoke-Docker stop --timeout 20 $containerName
    throw 'Startup did not become ready within ten minutes; container stopped.'
}

switch ($Command) {
    'build' {
        $options = @{}
        for ($index = 0; $index -lt $CommandArguments.Count; $index += 2) {
            if ($index + 1 -ge $CommandArguments.Count) { throw 'Image build option requires a value' }
            switch ($CommandArguments[$index]) {
                '--manifest' { $options.Manifest = $CommandArguments[$index + 1] }
                '--derived-dockerfile' { $options.DerivedDockerfile = $CommandArguments[$index + 1] }
                '--build-arg' { $options.BuildArg = @($options.BuildArg | Where-Object { $null -ne $_ }) + $CommandArguments[$index + 1] }
                default { throw 'Unknown image build option' }
            }
        }
        & "$PSScriptRoot/scripts/build-image.ps1" @options
    }
    'setup' {
        Assert-Stopped
        if ($CommandArguments.Count) { throw 'Pass installation options directly to .\install.ps1 -SkipBuild -NoStart.' }
        & "$PSScriptRoot/install.ps1" -SkipBuild -NoStart
    }
    'configure' { Assert-Stopped; Invoke-Config configure @CommandArguments }
    'get' { Invoke-Config get @CommandArguments }
    'login' { Assert-Stopped; Invoke-Docker run --rm --init -i @volumeArguments $image login @CommandArguments }
    'import-codex-auth' {
        Assert-Stopped
        if ($CommandArguments.Count -ne 1) { throw 'Supply one auth.json file path.' }
        $authMount = Get-FileMount $CommandArguments[0] '/run/secrets/codex-auth.json'
        $copyAuth = "import json,pathlib; p=pathlib.Path('/run/secrets/codex-auth.json'); json.loads(p.read_text()); d=pathlib.Path('/data/credentials/codex/auth.json'); d.write_bytes(p.read_bytes()); d.chmod(0o600)"
        Invoke-Docker run --rm --init @volumeArguments @authMount --entrypoint python3 $image -c $copyAuth
    }
    'check' { Assert-Stopped; $keyMount = Get-KeyMount; Invoke-Docker run --rm --init @volumeArguments @keyMount $image check }
    'start' { Start-Agent }
    'stop' { Invoke-Docker stop --timeout 20 $containerName }
    'restart' { Invoke-Docker stop --timeout 20 $containerName; Start-Agent }
    'logs' { Invoke-Docker logs $containerName @CommandArguments }
    'versions' { Invoke-Config versions }
    'state' { Invoke-Config state }
    'enroll' { Assert-Stopped; Invoke-Config enroll @CommandArguments }
    'skills' {
        if ($CommandArguments.Count -gt 0 -and $CommandArguments[0] -eq 'install') {
            Assert-Stopped
            if ($CommandArguments.Count -lt 2) { throw 'Supply a skills.json file path.' }
            $selectionMount = Get-FileMount $CommandArguments[1] '/run/skills.json'
            $tokenMount = @(); $tokenArgument = @()
            if ($CommandArguments.Count -eq 3) {
                $tokenMount = Get-FileMount $CommandArguments[2] '/run/secrets/skill-token'
                $tokenArgument = @('--token-file', '/run/secrets/skill-token')
            }
            Invoke-Docker run --rm --init @volumeArguments @selectionMount @tokenMount $image install-skills /run/skills.json @tokenArgument
        } else { Invoke-Config skills }
    }
    'help' {
        Write-Host @'
Usage: .\repoagent.ps1 COMMAND [arguments]
  build | setup | configure | get | login | import-codex-auth
  check | start | stop | restart | logs [--follow] | versions | state
  enroll DISCUSSION_URL ... | skills [install SKILLS_JSON [TOKEN_FILE]]
Configure uses the same --repository, --app-id and --key-path options as ./repoagent.
Use --mention-user= to disable the optional mention account.
Set $env:REPOAGENT_INSTANCE to select the same instance used during installation.
Use .\install.ps1 -? for guided installation options.
'@
    }
    default { throw "Unknown command '$Command'. Run .\repoagent.ps1 help." }
}
