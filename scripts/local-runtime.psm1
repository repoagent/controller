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
    if (@($names | Where-Object { $_ -cne $Name }).Count) { throw 'Unexpected selected-resource discovery.' }
    if ($names -cnotcontains $Name) { return $null }
    if ($Kind -eq 'volume') {
        return @{ Labels = (Invoke-Docker volume inspect --format '{{json .Labels}}' $Name | ConvertFrom-Json -AsHashtable) }
    }
    # Deliberately exclude environment variables and unrelated container settings.
    # Legacy native argument passing strips embedded double quotes.
    $format = '{{printf `{%s:%s,%s:%s,%s:%s,%s:%s,%s:%s,%s:%s,%s:%s,%s:%s}` (json `Id`) (json .Id) (json `Image`) (json .Image) (json `User`) (json .Config.User) (json `Entrypoint`) (json .Config.Entrypoint) (json `Cmd`) (json .Config.Cmd) (json `Labels`) (json .Config.Labels) (json `Mounts`) (json .Mounts) (json `Running`) (json .State.Running)}}'
    $description = Invoke-Docker container inspect --format $format $Name | ConvertFrom-Json -AsHashtable
    if ($description -isnot [Collections.IDictionary]) { throw 'Malformed selected container inspection.' }
    return $description
}

function Assert-Owned($Resource, [string]$Storage) {
    $labels = $Resource.Labels
    if (-not $labels -or $labels['io.repoagent.owner'] -ne 'private-runtime-v1' -or
        $labels['io.repoagent.instance'] -cne $instance -or $labels['io.repoagent.storage'] -cne $Storage -or
        $labels['io.repoagent.image'] -cnotmatch '^sha256:[a-f0-9]{64}$') {
        throw 'Conflicting or legacy resource: choose a fresh REPOAGENT_INSTANCE. No resources were adopted or removed.'
    }
}

function Get-OwnershipArguments([string]$Storage) {
    return @('--label', 'io.repoagent.owner=private-runtime-v1', '--label', "io.repoagent.instance=$instance",
        '--label', "io.repoagent.storage=$Storage", '--label', "io.repoagent.image=$script:image")
}

function Initialize-LocalRuntime([string]$Instance, [string]$Image, [bool]$ConfigureBroker = $false, [bool]$Create = $false, [string]$StateDirectory, [bool]$MetadataOnly = $false, [bool]$SafeStop = $false) {
    if ($Instance -cnotmatch '^[a-z0-9][a-z0-9-]{0,40}$') { throw 'Invalid selected instance.' }
    $script:instance = $Instance
    $script:image = $Image
    $script:containerName = "repoagent-$Instance"
    $script:storageNames = @('config', 'credentials', 'workspace', 'history', 'state', 'tools')
    $script:existingContainer = Get-Resource container $containerName
    $script:hostDirectory = if ($StateDirectory) { $StateDirectory } elseif ($env:REPOAGENT_STATE_DIRECTORY) { $env:REPOAGENT_STATE_DIRECTORY } else { Join-Path ([Environment]::GetFolderPath('UserProfile')) ".repoagent/instances/$Instance" }
    $script:launchPath = Join-Path ([IO.Path]::GetFullPath($hostDirectory)) 'launch.json'
    $script:mirror = $null
    try { if (Test-Path -LiteralPath $launchPath) { $script:mirror = Read-LocalJson $launchPath } } catch { Write-Verbose 'Host launch mirror is unreadable; volume recovery is required.' }
    $script:record = $null
    $resources = @{}
    foreach ($storage in $storageNames + @('enrollment')) {
        $resources[$storage] = Get-Resource volume "$containerName-$storage"
    }
    $script:brokerMode = $ConfigureBroker
    foreach ($resource in @($resources.Values) + @($script:existingContainer)) {
        if ($resource -and $resource.Labels -and $resource.Labels['io.repoagent.owner'] -eq 'private-runtime-v1') { $script:brokerMode = $true }
    }
    if (-not $script:brokerMode) { return Get-LocalRuntimeContext }
    $pinned = $null
    # Complete the conflict scan before creating or mounting anything.
    foreach ($storage in $storageNames + @('enrollment', 'container')) {
        $resource = if ($storage -eq 'container') { $script:existingContainer } else { $resources[$storage] }
        if ($resource) {
            Assert-Owned $resource $storage
            $resourceImage = $resource.Labels['io.repoagent.image']
            if ($storage -eq 'container') { continue }
            if ($pinned -and $pinned -ne $resourceImage) { throw 'Instance image metadata conflicts; resolve manually.' }
            $pinned = $resourceImage
        }
    }
    if (-not $pinned) {
        $pinned = Invoke-Docker image inspect --format '{{.Id}}' $image
        if ($pinned -cnotmatch '^sha256:[a-f0-9]{64}$') { throw 'Select an existing immutable controller image.' }
    }
    $script:storageOrigin = $pinned
    $script:image = if ($MetadataOnly -and $mirror.launchState) { Assert-Text $mirror.runtimeImageId '\Asha256:[a-f0-9]{64}\z'; $mirror.runtimeImageId } else { $pinned }
    $Image = $pinned
    foreach ($storage in $storageNames + @('enrollment')) {
        if (-not $resources[$storage]) {
            if (-not $ConfigureBroker) { throw 'Incomplete broker instance: rerun broker configure to create missing owned storage.' }
        }
    }
    $incomplete = @($resources.Values | Where-Object { $_ }).Count -lt 7
    if ($incomplete) {
        if ($existingContainer -and $existingContainer.Image -cne $storageOrigin) { throw 'Missing storage for a transitioned launch; restore it manually.' }
        $script:image = Confirm-LocalImage $image
    }
    if ($ConfigureBroker -and $Create) {
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
    if ($MetadataOnly) { return Get-LocalRuntimeContext }
    if ($incomplete) { return Get-LocalRuntimeContext }
    Read-LocalLaunchState $SafeStop
    if ($script:existingContainer) { Assert-CompatibleContainer '' $script:existingContainer.Image }
    return Get-LocalRuntimeContext
}

function Assert-CompatibleContainer([string]$DirectKey, [string]$ExpectedImage = $image) {
    $existing = $script:existingContainer
    if (-not $existing) { return }
    if ($script:brokerMode) {
        Assert-Owned $existing 'container'
        if ($existing.Labels['io.repoagent.image'] -cne $existing.Image) { throw 'Container launch identity differs.' }
    }
    $expectedImage = Invoke-Docker image inspect --format '{{.Id}}' $ExpectedImage
    $role = Invoke-Docker image inspect --format '{{index .Config.Labels `io.repoagent.role`}}:{{.Config.User}}' $expectedImage
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
        $key = $DirectKey
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

function Get-LocalRuntimeContext {
    return @{ Instance=$instance; Image=$script:image; Managed=$brokerMode; Container=$existingContainer; Name=$containerName; LaunchState=$record; StorageOrigin=$storageOrigin }
}
function Invoke-LocalHelper([string]$Operation, $Marker) {
    if (-not $brokerMode) { throw 'Selected instance is not managed.' }
    if ($Operation -eq 'clear' -and (Get-Resource container $containerName).Running) { throw 'Selected runtime must remain stopped before clearing.' }
    foreach ($storage in $storageNames + @('enrollment')) {
        $resource = Get-Resource volume "$containerName-$storage"
        Assert-Owned $resource $storage
        if ($resource.Labels['io.repoagent.image'] -cne $storageOrigin) { throw 'Selected storage identity changed.' }
    }
    $source = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../container/local-lifecycle.py'))
    if ($source -match '[,\p{Cc}]') { throw 'Unsafe helper source path.' }
    $item = Get-Item -LiteralPath $source
    while ($item) {
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Unsafe helper source link.' }
        $item = if ($item -is [IO.DirectoryInfo]) { $item.Parent } else { $item.Directory }
    }
    $mounts = @()
    foreach ($storage in @('enrollment', 'config') + $(if ($Operation -eq 'clear') { @('credentials','state') } else { @() })) {
        $access = if ($Operation -in @('inspect','verify') -or ($storage -eq 'config' -and $Operation -ne 'restore') -or ($storage -eq 'enrollment' -and $Operation -eq 'restore')) { ',readonly' } else { '' }
        $mounts += @('--mount', "type=volume,src=$containerName-$storage,dst=/data/$storage$access")
    }
    $inputText = if ($Marker) { $Marker | ConvertTo-Json -Depth 8 -Compress } else { '' }
    # The daemon may be remote: stream the trusted host program, never bind a
    # workstation path. Keep its existing arguments and JSON stdin unchanged.
    $program = [IO.File]::ReadAllText($source, [Text.UTF8Encoding]::new($false, $true))
    $request = @{ source=$program; input=$inputText } | ConvertTo-Json -Depth 3 -Compress
    $bootstrap = "import io,json,sys; request=json.load(sys.stdin); sys.stdin=io.TextIOWrapper(io.BytesIO(request['input'].encode('utf-8'))); sys.argv=sys.argv[1:]; exec(compile(request['source'],sys.argv[0],'exec'))"
    $output = $request | & docker run --pull never --rm -i --network none --user 1000 @mounts --entrypoint /usr/bin/python3 $script:helperImage -E -c $bootstrap /source/local-lifecycle.py $script:helperImage $Operation
    if ($LASTEXITCODE -ne 0) { throw 'Selected lifecycle state is unconfirmed; inspect private file safety and binding.' }
    return Read-HostJson ($output -join "`n")
}
function Assert-LocalRuntimeAction([string]$Action, [string[]]$Arguments, $ProposedLaunch = $null) {
    if (-not $brokerMode -or $Action -in @('help','build','stop','logs','get','state','versions','Revoke','Uninstall')) { return }
    if ($Action -eq 'skills' -and @($Arguments)[0] -ne 'install') { return }
    $volume = Get-Resource volume "$containerName-enrollment"
    if (-not $volume) { return }
    $state = Invoke-LocalHelper inspect
    if ($state.marker) { throw 'Selected instance is disabled. Use safe stop, inspection or Uninstall retry; never automatically reenroll.' }
    if ($state.launchState -and ($state.launchState.pending -ne 'none' -or -not $mirror -or $mirror.instance -cne $instance -or
        $mirror.runtimeImageId -cne $state.launchState.currentImage -or $mirror.repository -cne $state.launchState.currentLaunch.binding.repository -or
        -not (Test-LaunchEquality $state.launchState $mirror.launchState))) { throw 'Selected update is pending or its host mirror differs; use explicit Rollback.' }
    if ($state.launchState) {
        $choices = Get-LaunchChoices $state.launchState
        if ($ProposedLaunch) { foreach ($field in $choices.Keys) { if ([string]$ProposedLaunch[$field] -cne $choices[$field]) { throw 'Proposed launch choices differ from volume authority; keep recorded choices.' } } }
        foreach ($field in $choices.Keys) { if ([string]$mirror[$field] -cne $choices[$field]) { throw 'Host launch choices differ from volume authority; use explicit Rollback.' } }
    }
}
function Get-SelectedContainer([bool]$SafeStop = $false) {
    $null = Initialize-LocalRuntime $instance $image $false $false $hostDirectory $false $SafeStop
    if (-not $brokerMode) { throw 'Selected instance is not managed.' }
    if (-not $existingContainer) { return $null }
    $again = Get-Resource container $containerName
    if ($existingContainer.Id -cnotmatch '^[a-f0-9]{64}$' -or -not $again -or $again.Id -cne $existingContainer.Id) { throw 'Selected container identity changed.' }
    return $again
}
function Stop-LocalRuntime([string]$ExpectedId) {
    $selected = Get-SelectedContainer $true
    if (-not $selected) { return }
    if ($ExpectedId -and $selected.Id -cne $ExpectedId) { throw 'Selected stop identity changed.' }
    Invoke-Docker stop --timeout 20 $selected.Id | Out-Null
    $after = Get-Resource container $containerName
    if ($after -and ($after.Id -cne $selected.Id -or $after.Running)) { throw 'Selected stop is unconfirmed.' }
}
function Remove-LocalRuntime([string]$ExpectedId) {
    $selected = Get-SelectedContainer
    if (-not $selected) { return }
    if ($ExpectedId -and $selected.Id -cne $ExpectedId) { throw 'Selected removal identity changed.' }
    if ($selected.Running) { throw 'Selected runtime is still running.' }
    Invoke-Docker rm $selected.Id | Out-Null
}
Import-Module "$PSScriptRoot/host-enrollment.psm1" -DisableNameChecking
Import-Module "$PSScriptRoot/private-file.psm1"
function Read-LocalJson([string]$Path) {
    return Read-HostJson ([Text.Encoding]::UTF8.GetString((Read-PrivateFileBytes $Path)))
}
function Invoke-LocalUninstall([hashtable]$Options) {
    $stateDirectory = $Options.StateDirectory
    if (-not $stateDirectory) { $stateDirectory = Join-Path ([Environment]::GetFolderPath('UserProfile')) ".repoagent/instances/$($Options.Instance)" }
    $stateDirectory = [IO.Path]::GetFullPath($stateDirectory)
    $approvalPath = if ($Options.ApprovalStatePath) { $Options.ApprovalStatePath } else { Join-Path $stateDirectory 'approval.json' }
    $approval = if (Test-Path -LiteralPath $approvalPath) { Read-LocalJson $approvalPath } else { $null }
    $receiptPath = Join-Path $stateDirectory 'lifecycle.json'
    $previous = if (Test-Path -LiteralPath $receiptPath) { Read-LocalJson $receiptPath } else { $null }
    if ($previous) {
        Assert-Fields $previous @('schemaVersion','marker','localLaunch','localRuntime','localCredentials','hostRecovery','revoke','result')
        if ($previous.schemaVersion -ne 1) { throw 'Unknown lifecycle receipt.' }
    }
    $binding = if ($previous) { $previous.marker.binding } else { $null }
    if ($approval) {
        $kind = Get-ApprovalStateKind $approval
        $approved = Read-HostJson $approval.marker
        $binding = [ordered]@{ repository=$approval.repository; repositoryId=$approval.repositoryId; deviceId=$approved.deviceId;
            appId=$approved.appId; installationId=$approved.installationId; brokerOrigin=$approval.brokerOrigin; profile=$approval.profile;
            approvalKind=$kind; approvalVariable=$approval.approvalVariable; expectedValueSha256=$approval.expectedValueSha256;
            githubEnvironment=$(if ($kind -eq 'environment-variable') { $approval.repoEnvironment } else { $null });
            environmentId=$(if ($kind -eq 'environment-variable') { $approval.environmentId } else { $null }) }
    }
    $pairs = @(@('Repository','repository'),@('ExpectedRepositoryId','repositoryId'),@('DeviceId','deviceId'),@('AppId','appId'),
        @('InstallationId','installationId'),@('BrokerOrigin','brokerOrigin'),@('EnrollmentProfile','profile'),@('ApprovalVariable','approvalVariable'),
        @('ExpectedValueSha256','expectedValueSha256'),@('GitHubEnvironment','githubEnvironment'),@('ExpectedEnvironmentId','environmentId'))
    $locations = @{ ActionsVariable='repository-actions-variable'; AgentVariable='repository-agent-variable' }
    if (-not $binding) {
        $binding = [ordered]@{ approvalKind=$(if ($Options.GitHubEnvironment) { 'environment-variable' } elseif ($Options.ApprovalLocation) { $locations[$Options.ApprovalLocation] } else { 'repository-actions-variable' }) }
        foreach ($pair in $pairs) { $binding[$pair[1]] = $null }
    }
    if ($Options.ApprovalLocation -and $binding.approvalKind -cne $locations[$Options.ApprovalLocation]) { throw 'Selected approval coordinates conflict.' }
    foreach ($pair in $pairs) {
        if ($Options[$pair[0]]) {
            if ($binding[$pair[1]] -and [string]$binding[$pair[1]] -cne [string]$Options[$pair[0]]) { throw 'Selected approval coordinates conflict.' }
            $binding[$pair[1]] = $Options[$pair[0]]
        }
    }
    foreach ($field in @('repositoryId','appId','installationId')) {
        if ([string]$binding[$field] -cnotmatch '^[1-9][0-9]*$') { throw 'Complete nonsecret approval and device coordinates are required.' }
        $binding[$field] = [long]$binding[$field]
    }
    if ($binding.environmentId) { $binding.environmentId = [long]$binding.environmentId }
    foreach ($field in @('repository','deviceId','brokerOrigin','profile','approvalVariable','expectedValueSha256')) {
        if (-not $binding[$field]) { throw 'Complete nonsecret approval and device coordinates are required.' }
    }
    Assert-Text $binding.repository '\A[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+\z'
    Assert-Text $binding.deviceId '\A[A-Za-z0-9_-]{1,128}\z'
    Assert-Text $binding.expectedValueSha256 '\A[0-9a-f]{64}\z'
    Assert-Text $binding.approvalVariable '\A[A-Z_][A-Z0-9_]{0,99}\z'
    Assert-Text $binding.profile '\A(?:polling|webhook)(?:-clone)?\z'
    $null = Get-BrokerOrigin $binding.brokerOrigin
    $context = Initialize-LocalRuntime $Options.Instance $(if ($env:REPOAGENT_IMAGE) { $env:REPOAGENT_IMAGE } else { 'repoagent:local' }) $false $false $stateDirectory (-not $Options.Apply)
    if (-not $context.Managed) { throw 'Uninstall requires a proven managed instance.' }
    $marker = [ordered]@{ schemaVersion=1; state='disabled'; instance=$context.Instance; image=$context.Image; binding=$binding }
    if ($previous -and ($previous.marker | ConvertTo-Json -Depth 8 -Compress) -cne ($marker | ConvertTo-Json -Depth 8 -Compress)) { throw 'Saved lifecycle binding differs.' }
    $launchPath = Join-Path $stateDirectory 'launch.json'
    if (Test-Path -LiteralPath $launchPath) {
        $launch = try { Read-LocalJson $launchPath } catch { if (-not $context.LaunchState) { throw 'Unconfirmed host launch state.' }; $null }
        if ($launch -and ($launch.instance -cne $context.Instance -or (-not $context.LaunchState -and $launch.runtimeImageId -cne $context.Image) -or $launch.repository -cne $binding.repository)) { throw 'Saved launch identity differs.' }
    }
    $remote = [ordered]@{ approvalKind=$binding.approvalKind; repository=$binding.repository; repositoryId=$binding.repositoryId;
        approvalVariable=$binding.approvalVariable; observedValueSha256=''; expectedValueSha256=$binding.expectedValueSha256;
        githubMarker='unverifiable'; brokerClosure='unconfirmed'; outstandingTokens='unsettled'; recovery='Retry with retained nonsecret coordinates.' }
    $result = [ordered]@{ action='Uninstall'; instance=$context.Instance; localLaunch='unconfirmed'; localRuntime='unconfirmed';
        localCredentials='retained'; hostRecovery='unconfirmed'; revoke=$remote; selfRevoke='not-attempted'; result='partial' }
    Write-Host "Instance $($context.Instance); image $($context.Image); repository $($binding.repository); device $($binding.deviceId)."
    Write-Host "Approval $($binding.approvalKind): $($binding.approvalVariable); fingerprint $($binding.expectedValueSha256)."
    if ($binding.approvalKind -eq 'environment-variable') {
        $remote.githubEnvironment = $binding.githubEnvironment
        $remote.environmentId = $binding.environmentId
        Write-Host "GitHub Environment: $($binding.githubEnvironment); ID: $($binding.environmentId)."
    }
    Write-Host 'Clear selected enrollment/enrollment.json, credentials/codex/auth.json and state/bridge.json.github-app/installation-token.json plus .tmp; retain all volumes.'
    if (-not $Options.Apply) {
        Write-Host 'Read-only preview; private-volume disable and binding remain unverified until Apply.'
        $result.result = 'preview'
        return $result
    }
    $null = Invoke-LocalHelper verify $marker
    $receipt = [ordered]@{ schemaVersion=1; marker=$marker; localLaunch='intent'; localRuntime='unconfirmed';
        localCredentials='retained'; hostRecovery='unconfirmed'; revoke=$null; result='partial' }
    try {
        $null = Initialize-PrivateDirectory $stateDirectory
        Write-PrivateJson $receiptPath $receipt
        $null = Invoke-LocalHelper disable $marker
        $result.localLaunch = 'disabled'
    } catch {
        try { Stop-LocalRuntime; $result.localRuntime = 'stopped' } catch { $result.localRuntime = 'possibly-running' }
        Write-Host 'Durable disable unconfirmed; no credentials cleared. Manual inspection is required.'
    }
    if ($result.localLaunch -eq 'disabled') {
        try { Stop-LocalRuntime; $result.localRuntime = 'stopped' } catch { $result.localRuntime = 'possibly-running' }
        $revokeOptions = @{ Repository=$binding.repository; ApprovalVariable=$binding.approvalVariable; ExpectedRepositoryId=[string]$binding.repositoryId;
            ExpectedValueSha256=$binding.expectedValueSha256; Apply=$true; NonInteractive=$true }
        if ($approval) { $revokeOptions.ApprovalStatePath = $approvalPath }
        if ($binding.approvalKind -eq 'environment-variable') { $revokeOptions.GitHubEnvironment=$binding.githubEnvironment; $revokeOptions.ExpectedEnvironmentId=[string]$binding.environmentId }
        else { $revokeOptions.ApprovalLocation = if ($binding.approvalKind -eq 'repository-agent-variable') { 'AgentVariable' } else { 'ActionsVariable' } }
        if ($Options.EnrollmentFile -and $approval) { $revokeOptions.EnrollmentFile = $Options.EnrollmentFile }
        try {
            $output = @(& "$PSScriptRoot/revoke-approval.ps1" @revokeOptions)
            $result.revoke = Read-HostJson ($output -join "`n")
            if ($revokeOptions.EnrollmentFile -and $result.revoke.githubMarker -eq 'removed') { $result.selfRevoke = 'unconfirmed' }
            if ($result.revoke.brokerClosure -eq 'confirmed') { $result.selfRevoke = 'confirmed' }
        } catch { Write-Host 'Remote revocation is unconfirmed; retained coordinates permit a credential-free retry.' }
        if ($result.localRuntime -eq 'stopped') {
            try { Remove-LocalRuntime; $result.localRuntime = 'removed' } catch { $result.localRuntime = 'unconfirmed' }
            try {
                $cleared = Invoke-LocalHelper clear $marker
                $result.localCredentials = if (@($cleared.files.Values | Where-Object { $_ -ne 'cleared' }).Count) { 'partial' } else { 'cleared' }
            } catch { $result.localCredentials = 'partial' }
            $hostEnrollment = Join-Path $stateDirectory 'enrollment.json'
            $result.hostRecovery = 'absent'
            if (Test-Path -LiteralPath $hostEnrollment) {
                $result.hostRecovery = 'protected'
                try {
                    $recovery = Read-LocalJson (Join-Path $stateDirectory 'recovery.json')
                    Assert-Fields $recovery @('version','setupVersion','approvalKind','request','secret','completionId','lastWorkAt','acknowledged','ackAttempted')
                    Assert-Fields $recovery.request @('idempotencyId','repositoryId','repository','variable','profile')
                    $recoveryVersion = @{ 'repository-agent-variable'=2; 'repository-actions-variable'=3 }[$binding.approvalKind]
                    if (-not $recoveryVersion -or $recovery.version -ne $recoveryVersion -or $recovery.setupVersion -ne $recoveryVersion -or $recovery.approvalKind -cne $binding.approvalKind -or $recovery.acknowledged -isnot [bool] -or $recovery.ackAttempted -cne $true -or $recovery.completionId -cnotmatch '^[0-9a-f]{32}$') { throw 'Unresolved recovery.' }
                    foreach ($pair in @(@('repositoryId','repositoryId'),@('repository','repository'),@('variable','approvalVariable'),@('profile','profile'))) {
                        if ($recovery.request[$pair[0]] -cne $binding[$pair[1]]) { throw 'Recovery binding differs.' }
                    }
                    $credential = Read-LocalJson $hostEnrollment
                    foreach ($key in @('repository','repositoryId','deviceId','appId','installationId','brokerOrigin')) {
                        if ($credential[$key] -cne $binding[$key]) { throw 'Host enrollment binding differs.' }
                    }
                    if ($launch -and $recovery.acknowledged -ceq $true -and $null -eq $recovery.secret -and $credential.deviceId -ceq $binding.deviceId -and $credential.repositoryId -eq $binding.repositoryId) {
                        Remove-Item -LiteralPath $hostEnrollment
                        $result.hostRecovery = 'cleared'
                    }
                } catch { Write-Host 'Protected unresolved host recovery material retained; resolve through the existing enrollment recovery procedure.' }
            }
        }
    }
    if ($result.localLaunch -eq 'disabled' -and $result.localRuntime -eq 'removed' -and $result.localCredentials -eq 'cleared' -and $result.revoke.githubMarker -in @('removed','absent') -and $result.revoke.brokerClosure -eq 'confirmed') { $result.result = 'complete' }
    foreach ($field in @('localLaunch','localRuntime','localCredentials','hostRecovery','revoke','result')) { $receipt[$field] = $result[$field] }
    try { Write-PrivateJson $receiptPath $receipt } catch { $result.result='partial'; Write-Host 'Host retry receipt is unconfirmed; preserve the selected coordinates.' }
    return $result
}
function Test-LaunchEquality($Left, $Right) {
    $a = [Text.Json.Nodes.JsonNode]::Parse(($Left | ConvertTo-Json -Depth 8 -Compress))
    $b = [Text.Json.Nodes.JsonNode]::Parse(($Right | ConvertTo-Json -Depth 8 -Compress))
    return [Text.Json.Nodes.JsonNode]::DeepEquals($a, $b)
}
function Confirm-LocalImage([string]$Reference) {
    $id = Invoke-Docker image inspect --format '{{.Id}}' $Reference
    $role = Invoke-Docker image inspect --format '{{index .Config.Labels `io.repoagent.role`}}:{{.Config.User}}' $id
    $entry = Invoke-Docker image inspect --format '{{json .Config.Entrypoint}}' $id | ConvertFrom-Json
    if ($id -cnotmatch '^sha256:[a-f0-9]{64}$' -or $role -cne 'controller:1000' -or @($entry).Count -ne 3 -or
        ($entry -join "`n") -cne "/usr/bin/python3`n-E`n/opt/repoagent/runtime.py") { throw 'An available immutable UID1000 controller image with the supported entrypoint is required.' }
    return $id
}
function Read-LocalLaunchState([bool]$SafeStop = $false) {
    $candidates = @($mirror.launchState.currentImage, $mirror.launchState.priorImage, $existingContainer.Image) | Where-Object { $_ } | Select-Object -Unique
    if (-not $candidates) { $candidates = @($storageOrigin) }
    $queue = [Collections.Generic.Queue[string]]::new([string[]]@($candidates))
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $selected = $false
    while ($queue.Count) {
        $candidate = $queue.Dequeue()
        if (-not $seen.Add($candidate)) { continue }
        if ($candidate -cnotmatch '^sha256:[a-f0-9]{64}$') { throw 'Invalid helper bootstrap identity.' }
        & docker image inspect --format '{{.Id}}' $candidate 2>$null | Out-Null
        if ($LASTEXITCODE -ne 0) { continue }
        $script:helperImage = Confirm-LocalImage $candidate
        $state = Invoke-LocalHelper inspect
        $script:record = $state.launchState
        if ($record -and $candidate -cnotin @($record.currentImage, $record.priorImage) -and -not $SafeStop) {
            foreach ($known in @($record.currentImage, $record.priorImage) | Where-Object { $_ }) { $queue.Enqueue($known) }
            continue
        }
        if (-not $record -and -not $SafeStop -and ($state.launchRequired -or $mirror.launchState -or $candidate -cne $storageOrigin -or ($existingContainer -and $existingContainer.Image -cne $storageOrigin))) { throw 'Missing original launch authority; no transitioned resources were adopted.' }
        $selected = $true
        break
    }
    if (-not $selected) { throw 'Restore an exact recorded current or prior helper image manually.' }
    if ($record) {
        if ($record.instance -cne $instance -or $record.storageOriginImage -cne $storageOrigin) { throw 'Authoritative launch ownership differs.' }
        $script:image = $record.currentImage
        if ($existingContainer -and $existingContainer.Image -cnotin @($record.currentImage, $record.priorImage, $record.targetImage)) { throw 'Unknown selected container launch.' }
    }
}
function Get-LaunchChoices($Record) {
    $choices = [ordered]@{}
    foreach ($key in $Record.currentLaunch.settings.Keys | Where-Object { $_ -notin @('keyPath','enroll') }) {
        $field = if ($key -eq 'brokerUrl') { 'brokerOrigin' } else { $key }
        $choices[$field] = if ($key -eq 'clone') { ([string]$Record.currentLaunch.settings[$key]).ToLowerInvariant() } else { [string]$Record.currentLaunch.settings[$key] }
    }
    $choices['enrollmentProfile'] = $Record.currentLaunch.binding.profile
    return $choices
}
function Write-LaunchMirror($Record) {
    if (-not $mirror -or $mirror.instance -cne $instance) { throw 'Restore the original checked host launch mirror.' }
    $script:mirror['launchState'] = $Record
    $script:mirror['runtimeImageId'] = $Record.currentImage
    $choices = Get-LaunchChoices $Record
    foreach ($field in $choices.Keys) { $script:mirror[$field] = $choices[$field] }
    $null = Initialize-PrivateDirectory $hostDirectory
    Write-PrivateJson $launchPath $mirror
}
function Invoke-LocalUpdate([hashtable]$Options) {
    foreach ($option in $Options.Keys) {
        if ($option -notin @('Action','Image','Rollback','Instance','StateDirectory','Apply','NonInteractive')) { throw 'Update accepts only instance/state, Image or Rollback, Apply and NonInteractive.' }
    }
    if ([bool]$Options.Image -eq [bool]$Options.Rollback) { throw 'Select exactly one immutable Image or Rollback.' }
    $context = Initialize-LocalRuntime $Options.Instance 'repoagent:local' $false $false $Options.StateDirectory (-not $Options.Apply)
    if (-not $context.Managed -or ($mirror -and $mirror.instance -cne $instance) -or
        (-not $mirror -and -not ($Options.Rollback -and $Options.Apply -and $record))) { throw 'Update requires checked managed launch state.' }
    if (-not $mirror) { $script:mirror = [ordered]@{ instance=$instance; repository=$record.currentLaunch.binding.repository; runtimeImageId=$record.currentImage; launchState=$record } }
    if ($Options.Image -and $Options.Image -cnotmatch '^(sha256:[a-f0-9]{64}|[a-z0-9][a-z0-9./:_-]*@sha256:[a-f0-9]{64})$') { throw 'Select an immutable local image ID or repository digest; no pull is performed.' }
    $target = if ($Options.Rollback) { $mirror.launchState.priorImage } else { Confirm-LocalImage $Options.Image }
    if ($Options.Rollback -and $Options.Apply) { $binding = $record.currentLaunch.binding } else {
        $approval = Read-LocalJson (Join-Path $hostDirectory 'approval.json')
        $kind = Get-ApprovalStateKind $approval
        $approved = Read-HostJson $approval.marker
        $binding = [ordered]@{ repository=$approval.repository; repositoryId=$approval.repositoryId; deviceId=$approved.deviceId; appId=$approved.appId;
            installationId=$approved.installationId; brokerOrigin=$approval.brokerOrigin; profile=$approval.profile; approvalKind=$kind;
            approvalVariable=$approval.approvalVariable; expectedValueSha256=$approval.expectedValueSha256;
            githubEnvironment=$(if ($kind -eq 'environment-variable') { $approval.repoEnvironment } else { $null }); environmentId=$(if ($kind -eq 'environment-variable') { $approval.environmentId } else { $null }) }
    }
    $kind = $binding.approvalKind
    if ($mirror.repository -cne $binding.repository) { throw 'Host repository binding differs.' }
    if (-not $record -and $mirror.runtimeImageId -cne $image) { throw 'Original host launch image differs.' }
    Write-Host "Instance $instance; repository $($binding.repository); device $($binding.deviceId); image $image."
    Write-Host "Approval $kind/$($binding.approvalVariable); prior $($mirror.launchState.priorImage); target $target; retain all seven volumes and private data."
    if (-not $Options.Apply) { Write-Host 'Read-only preview; authoritative volume verification is deferred until Apply.'; return }
    if ($Options.Rollback) { $target = $record.priorImage; if (-not $target) { throw 'No authoritative prior image is recorded.' } }
    else { Assert-LocalRuntimeAction Update @() }
    $target = Confirm-LocalImage $target
    $marker = [ordered]@{ schemaVersion=1; state='disabled'; instance=$instance; image=$image; binding=$binding }
    if ((Invoke-LocalHelper inspect).marker) { throw 'Selected instance is disabled.' }
    $verified = Invoke-LocalHelper verify $marker
    if (-not $record) {
        if ($Options.Rollback) { throw 'No authoritative prior image is recorded.' }
        $script:record = [ordered]@{ schemaVersion=1; instance=$instance; volumes=[ordered]@{}; storageOriginImage=$storageOrigin;
            currentImage=$image; currentLaunch=$verified.launch; priorImage=$null; priorLaunch=$null; targetImage=$null; targetLaunch=$null;
            pending='none'; operationId=[Guid]::NewGuid().ToString('N'); revision=1 }
        foreach ($storage in $storageNames + @('enrollment')) { $record.volumes[$storage] = "$containerName-$storage" }
    }
    if ($Options.Rollback -and $context.Container) { $record.currentImage = $context.Container.Image }
    $record.currentLaunch = $verified.launch
    if (-not $Options.Rollback) { $record.priorImage=$record.currentImage; $record.priorLaunch=$record.currentLaunch }
    $record.targetImage=$target
    $record.targetLaunch=$(if ($Options.Rollback) { $record.priorLaunch } else { $record.currentLaunch })
    $record.pending=$(if ($Options.Rollback) { 'rollback' } else { 'update' })
    $record.operationId=[Guid]::NewGuid().ToString('N'); $record.revision++
    try {
        $null = Invoke-LocalHelper launch $record
        Write-LaunchMirror $record
        if ($context.Container) {
            Stop-LocalRuntime $context.Container.Id
            Remove-LocalRuntime $context.Container.Id
        }
        $null = Initialize-LocalRuntime $instance $image $false $false $hostDirectory
        if (Get-Resource container $containerName) { throw 'Selected name is no longer absent.' }
        if ($Options.Rollback) { $null = Invoke-LocalHelper restore $record }
        $mounts = @()
        foreach ($storage in $storageNames + @('enrollment')) {
            $destination = if ($storage -eq 'workspace') { '/workspace' } else { "/data/$storage" }
            $access = if ($storage -in @('config','enrollment')) { ',readonly' } else { '' }
            $mounts += @('--mount', "type=volume,src=$containerName-$storage,dst=$destination$access")
        }
        $network = if ($record.targetLaunch.settings.mode -eq 'fixed-url') { @('--publish', "127.0.0.1:$($record.targetLaunch.settings.listenPort):$($record.targetLaunch.settings.listenPort)") } else { @() }
        $script:image=$target
        $labels = Get-OwnershipArguments container
        $targetId = Invoke-Docker run --pull never -d --init @labels --name $containerName @network @mounts $target start
        if ($targetId -cnotmatch '^[a-f0-9]{64}$') { throw 'Target creation identity is unconfirmed.' }
        $deadline = (Get-Date).AddMinutes(10)
        do {
            $observed = Get-Resource container $containerName
            if (-not $observed -or $observed.Id -cne $targetId -or -not $observed.Running -or $observed.Image -cne $target) { throw 'Target runtime identity or readiness differs.' }
            $output = Invoke-Docker logs $observed.Id
            if (($output -join "`n").Contains(' ready repository=')) { break }
            if ((Get-Date) -ge $deadline) { throw 'Target runtime readiness timed out.' }
            Start-Sleep -Seconds 1
        } while ($true)
        $record.currentImage=$target; $record.currentLaunch=$record.targetLaunch; $record.pending='none'; $record.revision++
        Write-LaunchMirror $record
        $null = Invoke-LocalHelper launch $record
        $null = Initialize-LocalRuntime $instance $target $false $false $hostDirectory
        Assert-LocalRuntimeAction Update @()
        Write-Host 'Selected image transition completed. Prior image and all seven volumes retained.'
    } catch { throw 'Update is unconfirmed. Preserve all data and use explicit Update -Rollback -Apply with the original state directory.' }
}

Export-ModuleMember -Function Invoke-LocalUpdate, Initialize-LocalRuntime, Assert-CompatibleContainer, Get-OwnershipArguments, Assert-LocalRuntimeAction, Stop-LocalRuntime, Remove-LocalRuntime, Invoke-LocalUninstall
