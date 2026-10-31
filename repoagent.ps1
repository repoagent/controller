#Requires -Version 7.0
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
$storageNames = @('config', 'credentials', 'workspace', 'history', 'state', 'tools')
$brokerMode = $false
foreach ($storage in $storageNames) {
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

Import-Module "$PSScriptRoot/scripts/local-runtime.psm1" -DisableNameChecking
function Initialize-Identity([bool]$ConfigureBroker = $false, [bool]$Create = $false) {
    $context = Initialize-LocalRuntime $instance $image $ConfigureBroker $Create $env:REPOAGENT_STATE_DIRECTORY $false ($Command -eq 'stop')
    $script:brokerMode = $context.Managed
    $script:image = $context.Image
    $script:existingContainer = $context.Container
    $script:launchState = $context.LaunchState
}
function Test-CompatibleRuntime {
    $key = if (-not $brokerMode -and $existingContainer) { Invoke-Config get keyPath } else { '' }
    Assert-CompatibleContainer $key
}

function Get-CredentialMount {
    if ($script:brokerMode) { return @('--mount', "type=volume,src=$containerName-enrollment,dst=/data/enrollment,readonly") }
    return Get-KeyMount
}

function Invoke-PrivateInput([byte[]]$Bytes, [string[]]$DockerArguments) {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = (Get-Command docker -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
    $start.UseShellExecute = $false
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in $DockerArguments) {
        $start.ArgumentList.Add($argument)
    }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    try {
        $null = $process.Start()
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        $process.StandardInput.BaseStream.Write($Bytes, 0, $Bytes.Length)
        $process.StandardInput.Close()
        $process.WaitForExit()
        $null = $stdout.GetAwaiter().GetResult()
        $null = $stderr.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0) { throw 'Import failed.' }
        $global:LASTEXITCODE = 0
    } catch { throw 'Private credential import failed. Inspect resource permissions and retry.' }
    finally { $process.Dispose() }
}

function Import-Enrollment([byte[]]$Bytes) {
    if (-not $script:brokerMode) { throw 'Configure a fresh broker instance before importing enrollment.' }
    Assert-Stopped
    Invoke-PrivateInput $Bytes @('run', '--rm', '--init', '-i', '--network', 'none', '--user', '1000', '--mount',
        "type=volume,src=$containerName-enrollment,dst=/data/enrollment", $image, 'import-enrollment')
}

function Invoke-Config {
    $mounts = if ($brokerMode -and $Command -in @('get', 'state', 'versions', 'skills', 'status', 'logs') -and @($CommandArguments)[0] -ne 'install') { @($volumeArguments | ForEach-Object { if ($_ -like 'type=volume,*') { $_ + ',readonly' } else { $_ } }) } else { $volumeArguments }
    $network = if ($brokerMode -and $Command -in @('get', 'state', 'versions', 'skills', 'status', 'logs') -and @($CommandArguments)[0] -ne 'install') { @('--network', 'none') } else { @() }
    Invoke-Docker run --rm --init @network @mounts $image @args
}

function Get-RecordedConfigureArguments([string[]]$Arguments) {
    if (-not $launchState) { return $Arguments }
    $invalid = 'Configuration must retain recorded launch choices; use full option names and matching values.'
    $values = @{ '--enrollment-file' = '/data/enrollment/enrollment.json' }
    foreach ($field in $launchState.currentLaunch.settings.Keys | Where-Object { $_ -notin @('keyPath','enroll') }) {
        $option = '--' + [regex]::Replace($field, '[A-Z]', '-$0').ToLowerInvariant()
        $values[$option] = $launchState.currentLaunch.settings[$field]
    }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    for ($index = 0; $index -lt $Arguments.Count; $index++) {
        $parts = $Arguments[$index] -split '=', 2
        $option = $parts[0]
        if (-not $values.ContainsKey($option) -or -not $seen.Add($option)) { throw $invalid }
        $expected = $values[$option]
        if ($option -eq '--clone') {
            if ($parts.Count -ne 1) { throw $invalid }
            $value = $true
        } else {
            if ($parts.Count -eq 2) { $value = $parts[1] }
            else { $index++; if ($index -ge $Arguments.Count) { throw $invalid }; $value = $Arguments[$index] }
            if ($expected -is [long] -or $expected -is [int]) {
                [long]$number = 0
                if (-not [long]::TryParse($value, [Globalization.NumberStyles]::Integer, [Globalization.CultureInfo]::InvariantCulture, [ref]$number)) { throw $invalid }
                $value = $number
            }
        }
        if ($null -eq $expected -or [string]$value -cne [string]$expected) { throw $invalid }
    }
    foreach ($required in @('--repository','--app-id','--broker-url','--enrollment-file')) { if (-not $seen.Contains($required)) { throw $invalid } }
    $canonical = @()
    foreach ($option in $values.Keys) {
        if ($values[$option] -is [bool]) { if ($values[$option]) { $canonical += $option } }
        elseif ($null -ne $values[$option]) { $canonical += "$option=$($values[$option])" }
    }
    return $canonical
}

function Test-AgentRunning {
    $ErrorActionPreference = 'Continue'
    $result = & docker inspect --format '{{.State.Running}}' $containerName 2>$null
    return ($LASTEXITCODE -eq 0 -and $result -eq 'true')
}

function Assert-Stopped {
    if (Test-AgentRunning) { throw 'Stop the agent before changing configuration, credentials or skills.' }
}

function Invoke-RunningCommand {
    if (@($CommandArguments | Where-Object { $_ -match '^--state(?:=|$)' }).Count) {
        throw 'The host wrapper selects the installed instance state; use REPOAGENT_INSTANCE, not --state.'
    }
    if (-not $existingContainer -or -not $existingContainer.Running) { throw 'The selected controller must already be running.' }
    Test-CompatibleRuntime
    $selected = Get-Resource container $containerName
    if (-not $selected -or $selected.Id -cne $existingContainer.Id -or -not $selected.Running) { throw 'Selected running container identity changed.' }
    & docker exec $selected.Id /usr/bin/python3 -E /opt/repoagent/runtime.py $Command @CommandArguments
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
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
    Test-CompatibleRuntime
    if (Test-AgentRunning) { Write-Host "$containerName is already running."; return }
    $keyMount = Get-CredentialMount
    if ($script:existingContainer) { if ($brokerMode) { Remove-LocalRuntime } else { Invoke-Docker rm $containerName | Out-Null } }
    $networkArguments = @()
    if ((Invoke-Config get mode) -eq 'fixed-url') {
        $port = Invoke-Config get listenPort
        $networkArguments = @('--publish', "127.0.0.1:${port}:$port")
    }
    $labels = if ($brokerMode) { Get-OwnershipArguments 'container' } else { @() }
    Invoke-Docker run -d --init @labels --name $containerName @networkArguments @runtimeVolumes @keyMount $image start
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

if ($Command -notin @('help', 'build', 'setup')) {
    $prepare = $Command -eq 'configure' -and @($CommandArguments | Where-Object { $_ -match '^--broker-url(=|$)' }).Count -gt 0
    Initialize-Identity $prepare
    Assert-LocalRuntimeAction $Command $CommandArguments
}

# Validate private input before creating any resources. Only the native consumer
# interprets frozen JSON; host configuration retains no copy of its contents.
$privateBytes = $null
$configArguments = @($CommandArguments)
$configureBroker = $false
if ($Command -eq 'configure') {
    # Credential mode must be decided before any Docker mount or resource write.
    # Keep argparse's optional long-option abbreviations out of this boundary.
    foreach ($argument in $CommandArguments) {
        $option = ($argument -split '=', 2)[0]
        if ($option.StartsWith('--') -and $option.Length -gt 2) {
            foreach ($selector in @('--broker-url', '--enrollment-file', '--key-path')) {
                if ($option -cne $selector -and $selector.StartsWith($option, [StringComparison]::Ordinal)) {
                    throw 'Spell credential selectors in full: --broker-url, --enrollment-file, --key-path.'
                }
            }
        }
    }
    $hasBroker = @($CommandArguments | Where-Object { $_ -match '^--(broker-url|enrollment-file)(=|$)' }).Count -gt 0
    if ($hasBroker) {
        if (@($CommandArguments | Where-Object { $_ -match '^--key-path(=|$)' }).Count) { throw 'Broker and direct-App inputs are mutually exclusive.' }
        $values = @{}
        $configArguments = @()
        for ($i = 0; $i -lt $CommandArguments.Count; $i++) {
            $arg = $CommandArguments[$i]
            if ($arg -match '^--(broker-url|enrollment-file)(?:=(.*))?$') {
                $key = $Matches[1]
                if ($values.ContainsKey($key)) { throw 'Duplicate broker input.' }
                $value = if ($arg.Contains('=')) { $Matches[2] } else {
                    $i++; if ($i -ge $CommandArguments.Count) { throw 'Missing broker input.' }; $CommandArguments[$i]
                }
                if (-not $value -or $value.StartsWith('--')) { throw 'Missing broker input.' }
                $values[$key] = $value
            } else { $configArguments += $arg }
        }
        if (-not $values['broker-url'] -or -not $values['enrollment-file']) { throw 'Supply --broker-url and --enrollment-file together.' }
        Import-Module "$PSScriptRoot/scripts/private-file.psm1"
        $privateBytes = Read-PrivateFileBytes $values['enrollment-file']
        $configArguments += @('--broker-url', $values['broker-url'], '--enrollment-file', '/data/enrollment/enrollment.json')
        $configureBroker = $true
    }
} elseif ($Command -eq 'import-enrollment') {
    if ($CommandArguments.Count -ne 1) { throw 'Supply one private enrollment file path.' }
    Import-Module "$PSScriptRoot/scripts/private-file.psm1"
    $privateBytes = Read-PrivateFileBytes $CommandArguments[0]
}
if ($Command -notin @('help', 'build', 'setup')) { Initialize-Identity $configureBroker ($Command -eq 'configure') }
if ($Command -eq 'configure' -and $brokerMode -and -not $configureBroker) { throw 'Broker instances require broker configuration; use a fresh instance for direct App mode.' }

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
    'configure' {
        Assert-Stopped
        $recordedArguments = Get-RecordedConfigureArguments $configArguments
        Invoke-Config configure @recordedArguments
        if ($configureBroker) { Import-Enrollment $privateBytes }
    }
    'import-enrollment' { Import-Enrollment $privateBytes }
    'get' { Invoke-Config get @CommandArguments }
    'login' { Assert-Stopped; Invoke-Docker run --rm --init -i @volumeArguments $image login @CommandArguments }
    'import-codex-auth' {
        Assert-Stopped
        if ($CommandArguments.Count -ne 1) { throw 'Supply one auth.json file path.' }
        $authPath = (Get-Item -LiteralPath $CommandArguments[0] -ErrorAction Stop).FullName
        $copyAuth = "import json,os,pathlib,sys,tempfile; b=sys.stdin.buffer.read(); json.loads(b); d=pathlib.Path('/data/credentials/codex'); d.mkdir(parents=True,exist_ok=True); f,p=tempfile.mkstemp(dir=d); s=os.fdopen(f,'wb'); s.write(b); s.close(); os.replace(p,d/'auth.json')"
        Invoke-PrivateInput ([IO.File]::ReadAllBytes($authPath)) @('run', '--rm', '--init', '-i', '--network', 'none', '--user', '1000',
            '--mount', "type=volume,src=$containerName-credentials,dst=/data/credentials", '--entrypoint', '/usr/bin/python3', $image, '-c', $copyAuth)
    }
    'check' { Assert-Stopped; Test-CompatibleRuntime; $keyMount = Get-CredentialMount; Invoke-Docker run --rm --init @runtimeVolumes @keyMount $image check }
    'start' { Start-Agent }
    'stop' { if ($brokerMode) { Stop-LocalRuntime } else { Test-CompatibleRuntime; Invoke-Docker stop --timeout 20 $containerName } }
    'restart' { if ($brokerMode) { Stop-LocalRuntime } else { Test-CompatibleRuntime; Invoke-Docker stop --timeout 20 $containerName }; Start-Agent }
    'logs' {
        # Retained logs live in the state volume and survive container replacement.
        if (@($CommandArguments).Count -eq 1 -and $CommandArguments[0] -eq '--retained') { Invoke-Config logs }
        else { Invoke-Docker logs $containerName @CommandArguments }
    }
    'activity' { Invoke-RunningCommand }
    'remote' { Invoke-RunningCommand }
    'status' { Invoke-Config status }
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
  build | setup | configure | get | login | import-codex-auth | import-enrollment
  check | start | stop | restart | logs [--follow] | logs --retained | status | versions | state
  activity [--discussion NUMBER_OR_URL] [--follow] [--limit 1..100] [--cursor CURSOR] [--format text|json]
  remote enable|pair|status|clients|disable [--format text|json] | remote revoke --client ID
  enroll DISCUSSION_URL ... | skills [install SKILLS_JSON [TOKEN_FILE]]
Configure uses --repository and --app-id with either --key-path or
--broker-url HTTPS_ORIGIN --enrollment-file PRIVATE_FILE. Broker configure imports the file.
Use --mention-user= to disable the optional mention account.
Configure accepts --repository-settings to read REPOAGENT_ Agent variables: direct-App
installations need Agent variables: read; broker mode needs a repository-settings profile
(-RepositorySettings). Change them with .\install.ps1 -Action Settings.
Set $env:REPOAGENT_INSTANCE to select the same instance used during installation.
Use .\install.ps1 -? for guided installation options.
'@
    }
    default { throw "Unknown command '$Command'. Run .\repoagent.ps1 help." }
}
