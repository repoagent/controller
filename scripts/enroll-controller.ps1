#Requires -Version 7.4
[CmdletBinding()]
param(
    [string]$Repository,
    [string]$BrokerOrigin,
    [string]$ApprovalVariable,
    [ValidateSet('polling', 'webhook', 'polling-clone', 'webhook-clone')][string]$Profile,
    [string]$StateDirectory,
    [switch]$Apply,
    [switch]$NonInteractive
)
$ErrorActionPreference = 'Stop'
Import-Module "$PSScriptRoot/host-enrollment.psm1" -Force -DisableNameChecking
$script:Diagnostic = 'Enrollment could not be verified. Inspect host authentication, selected coordinates and private state before retrying.'
$script:Calls = 0
$script:Clock = [Diagnostics.Stopwatch]::StartNew()
function Stop-Enrollment([string]$Message) { $script:Diagnostic = $Message; throw 'Enrollment stopped.' }
function GitHub([string]$Method, [string]$Endpoint, $Body = $null) {
    $script:Calls++
    if ($script:Calls -gt 12 -or $script:Clock.Elapsed.TotalMinutes -gt 15) { Stop-Enrollment 'GitHub work limit reached; inspect before retrying.' }
    $result = Invoke-HostGitHub $Method $Endpoint $Body
    if ($result.Status -in @(401, 403, 422)) { Stop-Enrollment "GitHub denied this operation (HTTP $($result.Status)). Check host gh access, Agent variables read/write permission and API availability." }
    return $result
}
function Read-Repository {
    $r = GitHub GET $repoPath
    if ($r.Status -ne 200) { Stop-Enrollment 'Repository access could not be verified.' }
    $repo = Read-HostJson $r.Body; Assert-Id $repo.id
    if ($repo.full_name -isnot [string] -or $repo.full_name -ine $Repository -or ($config.repositoryId -and $config.repositoryId -ne $repo.id)) { Stop-Enrollment 'Repository identity changed.' }
    return $repo.id
}
function Read-AgentListing {
    $r = GitHub GET ($variablesPath + '?per_page=30&page=1')
    if ($r.Status -ne 200) { Stop-Enrollment 'Direct repository Agent variables are unavailable; absence and write access are unconfirmed.' }
    $v = Read-HostJson $r.Body
    Assert-Fields $v @('total_count','variables')
    if (($v.total_count -isnot [long] -and $v.total_count -isnot [int]) -or $v.total_count -lt 0 -or $v.total_count -gt 60 -or $v.variables -isnot [array] -or $v.variables.Count -ne [Math]::Min(30, $v.total_count)) { Stop-Enrollment 'Agent-variable listing exceeds the broker limit or is incomplete.' }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($item in $v.variables) {
        if ($item -isnot [Collections.IDictionary] -or $item.name -isnot [string] -or [string]::IsNullOrEmpty($item.name) -or $item.value -isnot [string] -or -not $seen.Add($item.name)) { Stop-Enrollment 'Agent-variable listing is malformed or contains duplicate names.' }
    }
    return $v
}
function Confirm-Absent {
    $v = Read-AgentListing
    # Offset pages are not a snapshot. Never infer absence across multiple pages.
    if ($v.total_count -gt 30) { Stop-Enrollment 'Absence above 30 Agent variables is unverifiable. Inspect manually; no approval will be created or restored.' }
    foreach ($item in $v.variables) {
        if ($item.name -ieq $ApprovalVariable) { Stop-Enrollment 'The selected Agent variable already exists. Rerun to inspect it; no write was attempted.' }
    }
}
function Check-Recovery {
    Assert-Fields $recovery @('version','setupVersion','approvalKind','request','secret','completionId','lastWorkAt','acknowledged','ackAttempted')
    Assert-Id $recovery.version; Assert-Id $recovery.setupVersion
    if ($recovery.version -ne 2 -or $recovery.setupVersion -ne 2 -or $recovery.approvalKind -cne 'repository-agent-variable') { Stop-Enrollment 'Recovery state is not Agent enrollment v2; it cannot be migrated or replayed.' }
    Assert-Fields $recovery.request @('idempotencyId','repositoryId','repository','variable','profile')
    Assert-Id $recovery.request.repositoryId
    Assert-Text $recovery.request.idempotencyId '\A[0-9a-f]{32}\z'
    foreach ($pair in @(@('repositoryId','repositoryId'),@('repository','repository'),@('variable','approvalVariable'),@('profile','profile'))) {
        if ($recovery.request[$pair[0]] -cne $config[$pair[1]]) { Stop-Enrollment 'Recovery request differs from saved host intent.' }
    }
    if ($recovery.acknowledged -isnot [bool] -or $recovery.ackAttempted -isnot [bool]) { Stop-Enrollment 'Invalid recovery progress flags.' }
    if ($recovery.completionId) { Assert-Text $recovery.completionId '\A[0-9a-f]{32}\z' }
    if ($null -ne $recovery.lastWorkAt) { Assert-Text $recovery.lastWorkAt '\A\d{4}-\d\d-\d\dT[^\r\n]+(?:Z|[+-]\d\d:\d\d)\z' }
    if ($recovery.acknowledged) {
        if ($null -ne $recovery.secret -or -not $recovery.ackAttempted -or -not $recovery.completionId) { Stop-Enrollment 'Invalid acknowledged recovery state.' }
    } else { Assert-Text $recovery.secret '\A[A-Za-z0-9_-]{43}\z' }
}
function Broker([string]$Method, [string]$Path, [string]$Secret, $Body = $null) {
    try {
        $r = Invoke-EnrollmentBroker $BrokerOrigin $Method $Path $Secret $Body
    } finally {
        # The broker starts its durable work interval after transport/admission.
        # Keep the pre-send crash checkpoint, but pace subsequent work from the
        # completed attempt, including unknown transport outcomes.
        $recovery.lastWorkAt = [DateTimeOffset]::UtcNow.ToString('O')
        Write-PrivateJson $recoveryPath $recovery
    }
    if ($r.Status -ne 200) { Stop-Enrollment "Broker operation failed (HTTP $($r.Status)); private state is retained. Retry the same enrollment after inspection; no new identity was created." }
    return Read-HostJson $r.Body
}
function Save-State { Write-PrivateJson $configPath $config; Write-PrivateJson $recoveryPath $recovery }
function Check-Status($Status) {
    Assert-Fields $Status @('pendingId', 'state', 'marker', 'deadline')
    Assert-Text $Status.pendingId '\A[A-Za-z0-9_-]{1,128}\z'
    if ($Status.pendingId -cne ([string]$config.repositoryId + '_' + $recovery.request.idempotencyId)) { Stop-Enrollment 'Unexpected pending operation identity.' }
    Assert-Text $Status.state '\A(?:discovering|pending|completed|active|expired|closed)\z'
    Assert-Text $Status.deadline '\A\d{4}-\d\d-\d\dT[^\r\n]+(?:Z|[+-]\d\d:\d\d)\z'
    $deadline = [DateTimeOffset]::Parse($Status.deadline, [Globalization.CultureInfo]::InvariantCulture)
    if ($deadline -le [DateTimeOffset]::UtcNow -or $deadline -gt [DateTimeOffset]::UtcNow.AddMinutes(16) -or $Status.state -in @('expired', 'closed')) { Stop-Enrollment 'Enrollment expired or closed; do not restore or reuse its approval.' }
    if ($config.pendingId -and ($config.pendingId -cne $Status.pendingId -or $config.deadline -cne $Status.deadline)) { Stop-Enrollment 'Broker changed the pending identity or original deadline.' }
    if ($null -ne $Status.marker -and $Status.marker -isnot [string]) { Stop-Enrollment 'Invalid status marker.' }
    if ($config.marker -and $Status.marker -cne $config.marker) { Stop-Enrollment 'Status changed the saved approval marker.' }
    $config.pendingId = $Status.pendingId; $config.deadline = $Status.deadline
}
function Check-Marker([string]$Text) {
    $m = Read-HostJson $Text
    Assert-Fields $m @('version','approvalKind','githubHost','appId','appSlug','repositoryId','repository','installationId','deviceId','challenge','profile','policyVersion','epoch','variable')
    foreach ($key in @('version','appId','repositoryId','installationId')) { Assert-Id $m[$key] }
    if ($m.version -ne 2 -or $m.approvalKind -cne 'repository-agent-variable' -or $m.githubHost -cne 'github.com' -or $m.appId -ne 5387625 -or $m.appSlug -cne 'repo-agent-connector' -or $m.repositoryId -ne $config.repositoryId -or $m.repository -ine $Repository -or $m.variable -cne $ApprovalVariable -or $m.profile -cne $Profile -or $m.policyVersion -cne $policy) { Stop-Enrollment 'Broker approval marker differs from the trusted host intent.' }
    Assert-Text $m.deviceId '\A[0-9a-f]{32}\z'
    Assert-Text $m.challenge '\A[A-Za-z0-9_-]{43}\z'
    Assert-Text $m.epoch '\A[A-Za-z0-9_-]{1,128}\z'
    if ($config.marker -and $config.marker -cne $Text) { Stop-Enrollment 'Broker changed the saved marker.' }
    return $m
}
function Check-Enrollment($Enrollment, $Marker) {
    $bindingFields = @('protocolVersion','brokerOrigin','githubHost','deviceId','deviceSecret','appId','appSlug','installationId','repositoryId','repository','permissions')
    if ($clone) { $bindingFields += @('profile','policyVersion') }
    Assert-Fields $Enrollment $bindingFields
    if ($clone -and ($Enrollment.profile -cne $Profile -or $Enrollment.policyVersion -cne $policy)) { Stop-Enrollment 'Clone policy differs.' }
    foreach ($key in @('protocolVersion','appId','installationId','repositoryId')) { Assert-Id $Enrollment[$key] }
    if ($Enrollment.protocolVersion -ne $runtimeVersion -or (Get-BrokerOrigin $Enrollment.brokerOrigin) -cne $BrokerOrigin -or $Enrollment.githubHost -cne 'github.com' -or $Enrollment.deviceId -cne $Marker.deviceId -or $Enrollment.appId -ne $Marker.appId -or $Enrollment.appSlug -cne $Marker.appSlug -or $Enrollment.installationId -ne $Marker.installationId -or $Enrollment.repositoryId -ne $Marker.repositoryId -or $Enrollment.repository -ine $Repository) { Stop-Enrollment 'Completion binding differs from the approved enrollment.' }
    Assert-Text $Enrollment.deviceSecret '\A[A-Za-z0-9_-]{43,128}\z'
    $fields = @('discussions'); if ($Profile -in @('webhook','webhook-clone')) { $fields += 'repository_hooks' }
    if ($clone) { $fields += 'contents'; if ($Enrollment.permissions.Contains('metadata')) { $fields += 'metadata' } }
    Assert-Fields $Enrollment.permissions $fields
    foreach ($key in $fields) { if ($Enrollment.permissions[$key] -cne $(if ($key -in @('contents','metadata')) { 'read' } else { 'write' })) { Stop-Enrollment 'Completion permission profile differs.' } }
}
function Wait-Work {
    if ($config.deadline -and [DateTimeOffset]::Parse($config.deadline) -le [DateTimeOffset]::UtcNow.AddSeconds(5)) { Stop-Enrollment 'Original enrollment deadline reached; private recovery state retained.' }
    # Keep a pre-send checkpoint if the process dies during the request.
    if ($recovery.lastWorkAt) {
        $remaining = ([DateTimeOffset]::Parse($recovery.lastWorkAt).AddSeconds(5) - [DateTimeOffset]::UtcNow).TotalMilliseconds
        if ($remaining -gt 6000) { Stop-Enrollment 'Local work timestamp is in the future; inspect the host clock.' }
        if ($remaining -gt 0) { Start-Sleep -Milliseconds ([int][Math]::Ceiling($remaining)) }
    }
    $recovery.lastWorkAt = [DateTimeOffset]::UtcNow.ToString('O')
    Write-PrivateJson $recoveryPath $recovery
}

try {
    if (-not $StateDirectory) { $StateDirectory = Join-Path ([Environment]::GetFolderPath('UserProfile')) '.repoagent-enrollment' }
    $StateDirectory = [IO.Path]::GetFullPath($StateDirectory)
    $configPath = Join-Path $StateDirectory 'approval.json'; $recoveryPath = Join-Path $StateDirectory 'recovery.json'; $enrollmentPath = Join-Path $StateDirectory 'enrollment.json'
    if ($ApprovalVariable) { $ApprovalVariable = $ApprovalVariable.ToUpperInvariant() }
    if ($BrokerOrigin) { $BrokerOrigin = Get-BrokerOrigin $BrokerOrigin }
    $config = $null
    if (Test-Path -LiteralPath $configPath) {
        $config = Read-PrivateJson $configPath
        if ((Get-ApprovalStateKind $config) -cne 'repository-agent-variable') { Stop-Enrollment 'Legacy Environment enrollment cannot resume through Agent setup. Retain its files, revoke/settle old authority separately and use a new state directory for fresh enrollment.' }
        foreach ($pair in @(@('Repository','repository'),@('BrokerOrigin','brokerOrigin'),@('ApprovalVariable','approvalVariable'),@('Profile','profile'))) {
            $supplied = Get-Variable -Name $pair[0] -ValueOnly
            if ($supplied -and $supplied -cne $config[$pair[1]]) { Stop-Enrollment 'Supplied inputs conflict with saved enrollment; use the original inputs.' }
            Set-Variable -Name $pair[0] -Value $config[$pair[1]]
        }
    }
    if (-not $Repository -and -not $NonInteractive) { $Repository = Read-Host 'Repository (OWNER/REPO)' }
    if (-not $BrokerOrigin -and -not $NonInteractive) { $BrokerOrigin = Read-Host 'Trusted broker HTTPS origin' }
    if (-not $ApprovalVariable) { $ApprovalVariable = if ($NonInteractive) { 'APPROVED_CONTROLLER' } else { $answer = Read-Host 'Approval variable [APPROVED_CONTROLLER]'; if ($answer) { $answer } else { 'APPROVED_CONTROLLER' } } }
    if (-not $Profile) { $Profile = 'polling' }
    Assert-Text $Profile '\A(?:polling|webhook|polling-clone|webhook-clone)\z'
    $clone = $Profile.EndsWith('-clone', [StringComparison]::Ordinal)
    $runtimeVersion = if ($clone) { 2 } else { 1 }
    $policy = if ($clone) { 'repository-agent-clone-approval-v2' } else { 'repository-agent-approval-v2' }
    $permissionPreview = 'discussions:write'
    if ($clone) { $permissionPreview += ', contents:read' }
    if ($Profile -in @('webhook','webhook-clone')) { $permissionPreview += ', repository_hooks:write' }
    $script:Diagnostic = 'Supply OWNER/REPO, a trusted HTTPS broker origin, and a valid Agent approval variable of at most 100 characters.'
    Assert-Text $Repository '\A[A-Za-z0-9_.-]{1,100}/[A-Za-z0-9_.-]{1,100}\z'
    if (@($Repository.Split('/') | Where-Object { $_ -in @('.', '..') }).Count) { Stop-Enrollment 'Invalid repository.' }
    Assert-Text $ApprovalVariable '\A[A-Za-z_][A-Za-z0-9_]{0,99}\z'
    $ApprovalVariable = $ApprovalVariable.ToUpperInvariant()
    if ($ApprovalVariable.StartsWith('GITHUB_', [StringComparison]::Ordinal)) { Stop-Enrollment 'Approval variable cannot start with GITHUB_.' }
    $BrokerOrigin = Get-BrokerOrigin $BrokerOrigin
    if ($null -eq $config) { $config = [ordered]@{ version=2; setupVersion=2; approvalKind='repository-agent-variable'; repository=$Repository; repositoryId=$null; brokerOrigin=$BrokerOrigin; approvalVariable=$ApprovalVariable; profile=$Profile; pendingId=$null; deadline=$null; marker=$null; expectedValueSha256=$null; approvalWriteAttempted=$false; approvalObserved=$false } }
    $recovery = $null
    if (Test-Path -LiteralPath $recoveryPath) {
        $recovery = Read-PrivateJson $recoveryPath
        Check-Recovery
    } elseif ($config.pendingId -or (Test-Path -LiteralPath $enrollmentPath)) { Stop-Enrollment 'Recovery journal is missing; do not create a replacement enrollment.' }
    Write-Host "Repository: $Repository; broker: $BrokerOrigin; App: 5387625 / repo-agent-connector; profile: $Profile"
    Write-Host "Setup/marker: 2; controller/runtime: $runtimeVersion; policy: $policy; permissions: $permissionPreview"
    Write-Host "Repository Agent-variable approval: $ApprovalVariable; state: $StateDirectory"
    Write-Host 'Apply may start one pending Agent enrollment, write its validated public marker, save a private device credential and acknowledge it. Existing matching approval is reused; conflicts stop.'
    if (-not $Apply) { Write-Host 'Read-only plan. Use -Apply to execute. The fresh marker is available only after start.'; exit 0 }
    $script:Diagnostic = 'Private enrollment state could not be verified. Use an owned local directory with mode 0700/0600 on Linux or a current-user-only protected DACL on Windows; do not discard recovery state.'
    $null = Initialize-PrivateDirectory $StateDirectory
    $repoPath = 'repos/' + (($Repository.Split('/') | ForEach-Object { [Uri]::EscapeDataString($_) }) -join '/')
    $variablesPath = $repoPath + '/agents/variables'
    $varPath = $variablesPath + '/' + [Uri]::EscapeDataString($ApprovalVariable)
    $script:Diagnostic = 'Enrollment transport or document validation failed. Check host gh authentication, broker availability and the saved binding; no automatic repair was attempted.'
    $config.repositoryId = Read-Repository
    $null = Read-AgentListing
    Write-PrivateJson $configPath $config
    if ($null -eq $recovery) {
        $recovery = [ordered]@{ version=2; setupVersion=2; approvalKind='repository-agent-variable'; request=[ordered]@{ idempotencyId=[Guid]::NewGuid().ToString('N'); repositoryId=$config.repositoryId; repository=$Repository; variable=$ApprovalVariable; profile=$Profile }; secret=[Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32)).TrimEnd('=').Replace('+','-').Replace('/','_'); completionId=$null; lastWorkAt=$null; acknowledged=$false; ackAttempted=$false }
        Write-PrivateJson $recoveryPath $recovery
    }
    if ((Test-Path -LiteralPath $enrollmentPath) -and $recovery.completionId) {
        $marker = Check-Marker $config.marker
        $enrollment = Read-PrivateJson $enrollmentPath
        Check-Enrollment $enrollment $marker
        $r = GitHub GET $varPath
        if ($r.Status -ne 200) { Stop-Enrollment 'Saved approval is unavailable; do not recreate it.' }
        $variable = Read-HostJson $r.Body
        if ($variable.name -isnot [string] -or $variable.name -ine $ApprovalVariable -or $variable.value -isnot [string] -or $variable.value -cne $config.marker) { Stop-Enrollment 'Saved approval changed; do not restore it.' }
    } else {
        Assert-Text $recovery.secret '\A[A-Za-z0-9_-]{43}\z'
        Wait-Work
        $status = Broker POST '/v2/enrollments' $recovery.secret $recovery.request
        Check-Status $status
        if ($null -eq $status.marker) { Save-State; Stop-Enrollment 'Broker discovery is still pending. Rerun the same request after at least five seconds.' }
        if ($status.marker -isnot [string]) { Stop-Enrollment 'Invalid broker marker.' }
        $marker = Check-Marker $status.marker
        $config.marker = $status.marker; $config.expectedValueSha256 = Get-ValueHash $status.marker
        Save-State
        Write-Host "Validated approval: repository ID $($marker.repositoryId), installation $($marker.installationId), device $($marker.deviceId), profile $Profile; policy $policy; controller/runtime $runtimeVersion; permissions $permissionPreview."
        $null = Read-Repository
        $r = GitHub GET $varPath
        if ($r.Status -eq 200) {
            $variable = Read-HostJson $r.Body
            if ($variable.name -isnot [string] -or $variable.name -ine $ApprovalVariable -or $variable.value -isnot [string] -or $variable.value -cne $config.marker) { Stop-Enrollment 'Approval conflicts with this enrollment. Nothing was replaced; resolve old authority manually.' }
        } elseif ($r.Status -eq 404) {
            if ($config.approvalWriteAttempted -or $config.approvalObserved) { Stop-Enrollment 'A previous approval write was attempted and its marker is now absent. Inspect manually; it will not be restored.' }
            Confirm-Absent
            $null = Read-Repository
            $config.approvalWriteAttempted = $true
            Write-PrivateJson $configPath $config
            $r = GitHub POST $variablesPath @{ name=$ApprovalVariable; value=$config.marker }
            if ($r.Status -ne 201) { Stop-Enrollment 'Approval creation is unconfirmed. Inspect and rerun the same enrollment; no automatic retry was attempted.' }
        } else { Stop-Enrollment 'Approval could not be verified.' }
        $config.approvalObserved = $true
        Write-PrivateJson $configPath $config
        for ($poll = 0; $poll -lt 180; $poll++) {
            Wait-Work
            $completion = Broker POST ('/v2/enrollments/' + $config.pendingId + '/completion') $recovery.secret
            if ($completion.Contains('enrollment')) {
                Assert-Fields $completion @('completionId','enrollment')
                Assert-Text $completion.completionId '\A[0-9a-f]{32}\z'
                Check-Enrollment $completion.enrollment $marker
                if ($recovery.completionId -and $recovery.completionId -cne $completion.completionId) { Stop-Enrollment 'Completion receipt changed.' }
                $recovery.completionId = $completion.completionId
                Write-PrivateJson $recoveryPath $recovery
                Write-PrivateJson $enrollmentPath $completion.enrollment
                $enrollment = Read-PrivateJson $enrollmentPath
                Check-Enrollment $enrollment $marker
                break
            }
            Check-Status $completion
        }
        if (-not (Test-Path -LiteralPath $enrollmentPath)) { Stop-Enrollment 'Completion polling limit reached; recover using the same state directory.' }
    }
    Assert-Text $recovery.completionId '\A[0-9a-f]{32}\z'
    # Active ack may be retried beyond the pending deadline, without extending activity.
    if ($recovery.ackAttempted) { $config.deadline = $null }
    Wait-Work
    $recovery.ackAttempted = $true
    Write-PrivateJson $recoveryPath $recovery
    $receipt = Broker POST ('/v2/devices/' + $enrollment.deviceId + '/acknowledgment') $enrollment.deviceSecret @{ completionId=$recovery.completionId }
    Assert-Fields $receipt @('state')
    if ($receipt.state -cne 'active') { Stop-Enrollment 'Activation is unconfirmed; retry acknowledgment using the persisted device credential.' }
    $recovery.acknowledged = $true; $recovery.secret = $null
    Write-PrivateJson $recoveryPath $recovery
    [ordered]@{ state='active'; enrollmentFile=$enrollmentPath; approvalFile=$configPath; brokerClosure='not-requested'; outstandingTokens='unsettled' } | ConvertTo-Json -Compress
    exit 0
} catch {
    Write-Warning $script:Diagnostic
    exit 1
}
