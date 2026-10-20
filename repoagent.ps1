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

function Get-Resource([string]$Kind, [string]$Name) {
    $names = if ($Kind -eq 'volume') {
        @(Invoke-Docker volume ls --filter "name=^$Name`$" --format '{{.Name}}')
    } else {
        @(Invoke-Docker container ls -a --filter "name=^/$Name`$" --format '{{.Names}}')
    }
    if ($names -notcontains $Name) { return $null }
    if ($Kind -eq 'volume') {
        return @{ Labels = (Invoke-Docker volume inspect --format '{{json .Labels}}' $Name | ConvertFrom-Json -AsHashtable) }
    }
    # Deliberately exclude environment variables and unrelated container settings.
    $format = '{"Image":{{json .Image}},"User":{{json .Config.User}},"Entrypoint":{{json .Config.Entrypoint}},"Cmd":{{json .Config.Cmd}},"Labels":{{json .Config.Labels}},"Mounts":{{json .Mounts}},"Running":{{json .State.Running}}}'
    return (Invoke-Docker container inspect --format $format $Name | ConvertFrom-Json -AsHashtable)
}

function Assert-Owned($Resource, [string]$Storage) {
    $labels = $Resource.Labels
    if (-not $labels -or $labels['io.repoagent.owner'] -ne 'private-runtime-v1' -or
        $labels['io.repoagent.instance'] -ne $instance -or $labels['io.repoagent.storage'] -ne $Storage -or
        $labels['io.repoagent.image'] -cnotmatch '^sha256:[a-f0-9]{64}$') {
        throw 'Conflicting or legacy resource: choose a fresh REPOAGENT_INSTANCE. No resources were adopted or removed.'
    }
}

function Get-OwnershipArguments([string]$Storage) {
    return @('--label', 'io.repoagent.owner=private-runtime-v1', '--label', "io.repoagent.instance=$instance",
        '--label', "io.repoagent.storage=$Storage", '--label', "io.repoagent.image=$script:image")
}

function Initialize-Identity([bool]$ConfigureBroker = $false) {
    $script:existingContainer = Get-Resource container $containerName
    $resources = @{}
    foreach ($storage in $storageNames + @('enrollment')) {
        $resources[$storage] = Get-Resource volume "$containerName-$storage"
    }
    $script:brokerMode = $ConfigureBroker
    foreach ($resource in @($resources.Values) + @($script:existingContainer)) {
        if ($resource -and $resource.Labels -and $resource.Labels['io.repoagent.owner'] -eq 'private-runtime-v1') { $script:brokerMode = $true }
    }
    if (-not $script:brokerMode) { return }
    $pinned = $null
    # Complete the conflict scan before creating or mounting anything.
    foreach ($storage in $storageNames + @('enrollment', 'container')) {
        $resource = if ($storage -eq 'container') { $script:existingContainer } else { $resources[$storage] }
        if ($resource) {
            Assert-Owned $resource $storage
            $resourceImage = $resource.Labels['io.repoagent.image']
            if ($pinned -and $pinned -ne $resourceImage) { throw 'Instance image metadata conflicts; resolve manually.' }
            $pinned = $resourceImage
        }
    }
    if (-not $pinned) {
        $pinned = Invoke-Docker image inspect --format '{{.Id}}' $image
        if ($pinned -cnotmatch '^sha256:[a-f0-9]{64}$') { throw 'Select an existing immutable controller image.' }
    }
    $script:image = $pinned
    $role = Invoke-Docker image inspect --format '{{index .Config.Labels "io.repoagent.role"}}:{{.Config.User}}' $image
    if ($role -ne 'controller:1000') { throw 'Selected image must be a UID1000 controller image.' }
    foreach ($storage in $storageNames + @('enrollment')) {
        if (-not $resources[$storage]) {
            if (-not $ConfigureBroker) { throw 'Incomplete broker instance: rerun broker configure to create missing owned storage.' }
        }
    }
    if ($script:existingContainer) { Assert-CompatibleContainer }
    if ($ConfigureBroker) {
        foreach ($storage in $storageNames + @('enrollment')) {
            if (-not $resources[$storage]) {
                $labels = Get-OwnershipArguments $storage
                Invoke-Docker volume create @labels "$containerName-$storage" | Out-Null
                $created = Get-Resource volume "$containerName-$storage"
                Assert-Owned $created $storage
                if ($created.Labels['io.repoagent.image'] -ne $image) { throw 'Volume identity changed while creating.' }
            }
        }
    }
}

function Assert-CompatibleContainer {
    $existing = $script:existingContainer
    if (-not $existing) { return }
    if ($script:brokerMode) { Assert-Owned $existing 'container' }
    $expectedImage = Invoke-Docker image inspect --format '{{.Id}}' $image
    $role = Invoke-Docker image inspect --format '{{index .Config.Labels "io.repoagent.role"}}:{{.Config.User}}' $expectedImage
    if ($role -ne 'controller:1000' -or $existing.Image -ne $expectedImage -or $existing.User -ne '1000' -or
        @($existing.Entrypoint).Count -ne 3 -or
        -not [StringComparer]::Ordinal.Equals($existing.Entrypoint[0], '/usr/bin/python3') -or
        -not [StringComparer]::Ordinal.Equals($existing.Entrypoint[1], '-E') -or
        -not [StringComparer]::Ordinal.Equals($existing.Entrypoint[2], '/opt/repoagent/runtime.py') -or
        @($existing.Cmd).Count -ne 1 -or -not [StringComparer]::Ordinal.Equals($existing.Cmd[0], 'start')) {
        throw 'Existing container differs from the selected controller image/start configuration. Resolve it manually.'
    }
    # Container destinations and Docker volume names use Linux identity on every host.
    $expected = [Collections.Hashtable]::new([StringComparer]::Ordinal)
    foreach ($storage in $storageNames) {
        $destination = if ($storage -eq 'workspace') { '/workspace' } else { "/data/$storage" }
        $expected[$destination] = @{ Type = 'volume'; Name = "$containerName-$storage"; RW = ($storage -ne 'config') }
    }
    if ($script:brokerMode) {
        $expected['/data/enrollment'] = @{ Type = 'volume'; Name = "$containerName-enrollment"; RW = $false }
    } else {
        $key = Invoke-Config get keyPath
        if (-not $key) { throw 'Saved direct configuration is missing its PEM path; resolve manually.' }
        # Match Get-FileMount, including Windows short-name expansion.
        $resolvedKey = (Resolve-Path -LiteralPath $key).ProviderPath
        $expected['/run/secrets/github-app.pem'] = @{ Type = 'bind'; Source = $resolvedKey; RW = $false }
    }
    if (@($existing.Mounts).Count -ne $expected.Count) { throw 'Existing container has unexpected mounts; resolve manually.' }
    $hostPathComparer = if ($IsWindows) { [StringComparer]::OrdinalIgnoreCase } else { [StringComparer]::Ordinal }
    foreach ($mount in $existing.Mounts) {
        $wanted = $expected[$mount.Destination]
        if (-not $wanted -or $mount.Type -ne $wanted.Type -or $mount.RW -ne $wanted.RW -or
            ($wanted.Type -eq 'volume' -and -not [StringComparer]::Ordinal.Equals($mount.Name, $wanted.Name)) -or
            ($wanted.Type -eq 'bind' -and -not $hostPathComparer.Equals($mount.Source, $wanted.Source))) {
            throw 'Existing container has incompatible mounts; resolve manually.'
        }
        $expected.Remove($mount.Destination)
    }
}

function Get-CredentialMount {
    if ($script:brokerMode) { return @('--mount', "type=volume,src=$containerName-enrollment,dst=/data/enrollment,readonly") }
    return Get-KeyMount
}

function Import-Enrollment([byte[]]$Bytes) {
    if (-not $script:brokerMode) { throw 'Configure a fresh broker instance before importing enrollment.' }
    Assert-Stopped
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = (Get-Command docker -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
    $start.UseShellExecute = $false
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in @('run', '--rm', '--init', '-i', '--network', 'none', '--user', '1000', '--mount',
        "type=volume,src=$containerName-enrollment,dst=/data/enrollment", $image, 'import-enrollment')) {
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
    } catch { throw 'Private enrollment import failed. No partial enrollment is published; inspect resource permissions and retry.' }
    finally { $process.Dispose() }
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
    Assert-CompatibleContainer
    if (Test-AgentRunning) { Write-Host "$containerName is already running."; return }
    $keyMount = Get-CredentialMount
    if ($script:existingContainer) { Invoke-Docker rm $containerName | Out-Null }
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
if ($Command -notin @('help', 'build', 'setup')) { Initialize-Identity $configureBroker }
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
    'configure' { Assert-Stopped; Invoke-Config configure @configArguments; if ($configureBroker) { Import-Enrollment $privateBytes } }
    'import-enrollment' { Import-Enrollment $privateBytes }
    'get' { Invoke-Config get @CommandArguments }
    'login' { Assert-Stopped; Invoke-Docker run --rm --init -i @volumeArguments $image login @CommandArguments }
    'import-codex-auth' {
        Assert-Stopped
        if ($CommandArguments.Count -ne 1) { throw 'Supply one auth.json file path.' }
        $authMount = Get-FileMount $CommandArguments[0] '/run/secrets/codex-auth.json'
        $copyAuth = "import json,pathlib; p=pathlib.Path('/run/secrets/codex-auth.json'); json.loads(p.read_text()); d=pathlib.Path('/data/credentials/codex/auth.json'); d.write_bytes(p.read_bytes()); d.chmod(0o600)"
        Invoke-Docker run --rm --init @volumeArguments @authMount --entrypoint /usr/bin/python3 $image -c $copyAuth
    }
    'check' { Assert-Stopped; Assert-CompatibleContainer; $keyMount = Get-CredentialMount; Invoke-Docker run --rm --init @runtimeVolumes @keyMount $image check }
    'start' { Start-Agent }
    'stop' { Assert-CompatibleContainer; Invoke-Docker stop --timeout 20 $containerName }
    'restart' { Assert-CompatibleContainer; Invoke-Docker stop --timeout 20 $containerName; Start-Agent }
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
  build | setup | configure | get | login | import-codex-auth | import-enrollment
  check | start | stop | restart | logs [--follow] | versions | state
  enroll DISCUSSION_URL ... | skills [install SKILLS_JSON [TOKEN_FILE]]
Configure uses --repository and --app-id with either --key-path or
--broker-url HTTPS_ORIGIN --enrollment-file PRIVATE_FILE. Broker configure imports the file.
Use --mention-user= to disable the optional mention account.
Set $env:REPOAGENT_INSTANCE to select the same instance used during installation.
Use .\install.ps1 -? for guided installation options.
'@
    }
    default { throw "Unknown command '$Command'. Run .\repoagent.ps1 help." }
}
