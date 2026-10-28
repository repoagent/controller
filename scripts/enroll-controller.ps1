#Requires -Version 7.4
[CmdletBinding()]
param(
    [string]$Repository,
    [string]$BrokerOrigin,
    [string]$ApprovalVariable,
    [ValidateSet('polling', 'webhook', 'polling-clone', 'webhook-clone', 'polling-settings', 'webhook-settings', 'polling-clone-settings', 'webhook-clone-settings')][string]$Profile,
    [string]$StateDirectory,
    # Fresh setup v4 uses a direct repository Agent variable. ActionsVariable is accepted
    # only when resuming matching saved v3 state. Saved state keeps its setup version.
    [ValidateSet('ActionsVariable', 'AgentVariable')][string]$ApprovalLocation,
    [switch]$Apply,
    [switch]$NonInteractive
)
$ErrorActionPreference = 'Stop'
Import-Module "$PSScriptRoot/host-enrollment.psm1" -Force -DisableNameChecking
$script:Diagnostic = 'Enrollment could not be verified. Inspect host authentication, selected coordinates and private state before retrying.'
$script:Calls = 0
$script:Clock = [Diagnostics.Stopwatch]::StartNew()
$script:LocationName = 'Agent'
function Stop-Enrollment([string]$Message) { $script:Diagnostic = $Message; throw 'Enrollment stopped.' }
function GitHub([string]$Method, [string]$Endpoint, $Body = $null) {
    $script:Calls++
    if ($script:Calls -gt 12 -or $script:Clock.Elapsed.TotalMinutes -gt 15) { Stop-Enrollment 'GitHub work limit reached; inspect before retrying.' }
    $result = Invoke-HostGitHub $Method $Endpoint $Body
    if ($result.Status -in @(401, 403, 422)) { Stop-Enrollment "GitHub denied this operation (HTTP $($result.Status)). Check host gh access, repository $($script:LocationName) variables read/write permission and API availability." }
    return $result
}
function Read-Repository {
    $r = GitHub GET $repoPath
    if ($r.Status -ne 200) { Stop-Enrollment 'Repository access could not be verified.' }
    $repo = Read-HostJson $r.Body; Assert-Id $repo.id
    if ($repo.full_name -isnot [string] -or $repo.full_name -ine $Repository -or ($config.repositoryId -and $config.repositoryId -ne $repo.id)) { Stop-Enrollment 'Repository identity changed.' }
    return $repo.id
}
function Read-VariableAccess {
    # One listing page proves that host gh can read the selected location. The number of variables
    # never limits enrollment; unrelated variables may share the selected store.
    $r = GitHub GET ($variablesPath + '?per_page=1&page=1')
    if ($r.Status -ne 200) { Stop-Enrollment "Direct repository $($script:LocationName) variables are unavailable; absence and write access are unconfirmed." }
    $v = Read-HostJson $r.Body
    Assert-Fields $v @('total_count','variables')
    if (($v.total_count -isnot [long] -and $v.total_count -isnot [int]) -or $v.total_count -lt 0 -or $v.variables -isnot [array] -or $v.variables.Count -ne [Math]::Min(1, $v.total_count)) { Stop-Enrollment "$($script:LocationName)-variable listing is malformed or incomplete." }
    foreach ($item in $v.variables) {
        if ($item -isnot [Collections.IDictionary] -or $item.name -isnot [string] -or [string]::IsNullOrEmpty($item.name) -or $item.value -isnot [string]) { Stop-Enrollment "$($script:LocationName)-variable listing is malformed." }
    }
    return $v
}
function Confirm-Absent {
    # GitHub also answers 404 without read access. A listing page read after the named 404 proves
    # access, so the 404 is absence; a page that shows the selected name contradicts it.
    $v = Read-VariableAccess
    foreach ($item in $v.variables) {
        if ($item.name -ieq $ApprovalVariable) { Stop-Enrollment "The selected $($script:LocationName) variable already exists. Rerun to inspect it; no write was attempted." }
    }
}
function Check-Recovery {
    Assert-Fields $recovery @('version','setupVersion','approvalKind','request','secret','completionId','lastWorkAt','acknowledged','ackAttempted')
    Assert-Id $recovery.version; Assert-Id $recovery.setupVersion
    if ($recovery.version -ne $setupVersion -or $recovery.setupVersion -ne $setupVersion -or $recovery.approvalKind -cne $approvalKind) { Stop-Enrollment "Recovery state is not $($script:LocationName)-variable enrollment v$setupVersion; it cannot be migrated or replayed." }
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
    for ($attempt = 0; $attempt -lt 3; $attempt++) {
        if ($attempt -gt 0) { Wait-Work }
        try {
            $r = Invoke-EnrollmentBroker $BrokerOrigin $Method $Path $Secret $Body
        } finally {
            # Pace subsequent work from the completed attempt, including unknown
            # transport outcomes. Every retry also gets a pre-send checkpoint.
            $recovery.lastWorkAt = [DateTimeOffset]::UtcNow.ToString('O')
            Write-PrivateJson $recoveryPath $recovery
        }
        if ($r.Status -ne 429 -or $attempt -eq 2) { break }
        $delay = 60L # Durable repository quota denials have no Retry-After header.
        if ($null -ne $r.RetryAfter) {
            $date = [DateTimeOffset]::MinValue
            if (-not [long]::TryParse($r.RetryAfter, [Globalization.NumberStyles]::None, [Globalization.CultureInfo]::InvariantCulture, [ref]$delay)) {
                if (-not [DateTimeOffset]::TryParseExact($r.RetryAfter, 'r', [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::AssumeUniversal, [ref]$date)) {
                    Stop-Enrollment 'Broker Retry-After is invalid; private recovery state retained for inspection.'
                }
                $delay = [long][Math]::Ceiling(($date - [DateTimeOffset]::UtcNow).TotalSeconds)
            }
        }
        if ($delay -gt 120) { Stop-Enrollment 'Broker Retry-After exceeds the bounded wait; private recovery state retained.' }
        $delay = [Math]::Max(5L, $delay)
        if ($config.deadline -and [DateTimeOffset]::Parse($config.deadline) -le [DateTimeOffset]::UtcNow.AddSeconds($delay + 5)) {
            Stop-Enrollment 'Rate-limit wait would exhaust the original enrollment deadline; private recovery state retained.'
        }
        if ($script:Clock.Elapsed.TotalSeconds + $delay + 5 -ge 900) { Stop-Enrollment 'Host work limit reached; private recovery state retained.' }
        Write-Host "Broker rate limit reached; retrying the same operation in $delay seconds ($($attempt + 1)/2)."
        Start-Sleep -Milliseconds ([int]($delay * 1000))
    }
    # Only enrollment start has the settlement-preflight diagnostic contract.
    # Empty 409 binding/possession conflicts and unrecognized bodies remain stop/inspect.
    if ($Method -ceq 'POST' -and $Path -ceq "/v$setupVersion/enrollments" -and $r.Status -in @(409,503)) {
        $startDiagnostic = $null
        try {
            $denial = Read-HostJson $r.Body
            if ($r.Status -eq 409) {
                Assert-Fields $denial @('code','retryAfterSeconds')
                if ($denial.code -is [string] -and $denial.code -ceq 'enrollment_not_ready' -and
                    ($denial.retryAfterSeconds -is [long] -or $denial.retryAfterSeconds -is [int]) -and $denial.retryAfterSeconds -eq 60) {
                    $startDiagnostic = 'Old repository authority is not ready for replacement. Recheck using this same private state after at least 60 seconds; that interval does not promise settlement or readiness. No approval was written; private state is retained.'
                }
            } else {
                Assert-Fields $denial @('code')
                if ($denial.code -is [string] -and $denial.code -ceq 'enrollment_settlement_unconfirmed') {
                    $startDiagnostic = 'Old repository authority settlement is unconfirmed. Retain this private state and inspect broker availability and settlement before retrying the same request. No safe-ready time is known and no approval was written.'
                }
            }
        } catch { } # Never reflect provider bodies or treat malformed guidance as readiness.
        if ($startDiagnostic) { Stop-Enrollment $startDiagnostic }
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
    if ($m.version -ne $setupVersion -or $m.approvalKind -cne $approvalKind -or $m.githubHost -cne 'github.com' -or $m.appId -ne 5387625 -or $m.appSlug -cne 'repo-agent-connector' -or $m.repositoryId -ne $config.repositoryId -or $m.repository -ine $Repository -or $m.variable -cne $ApprovalVariable -or $m.profile -cne $Profile -or $m.policyVersion -cne $policy) { Stop-Enrollment 'Broker approval marker differs from the trusted host intent.' }
    Assert-Text $m.deviceId '\A[0-9a-f]{32}\z'
    Assert-Text $m.challenge '\A[A-Za-z0-9_-]{43}\z'
    Assert-Text $m.epoch '\A[A-Za-z0-9_-]{1,128}\z'
    if ($config.marker -and $config.marker -cne $Text) { Stop-Enrollment 'Broker changed the saved marker.' }
    return $m
}
function Check-Enrollment($Enrollment, $Marker) {
    $bindingFields = @('protocolVersion','brokerOrigin','githubHost','deviceId','deviceSecret','appId','appSlug','installationId','repositoryId','repository','permissions')
    # Clone (runtime v2) and repository-settings (runtime v3) files name their profile and policy.
    if ($runtimeVersion -gt 1) { $bindingFields += @('profile','policyVersion') }
    Assert-Fields $Enrollment $bindingFields
    if ($runtimeVersion -gt 1 -and ($Enrollment.profile -cne $Profile -or $Enrollment.policyVersion -cne $policy)) { Stop-Enrollment 'Enrollment profile policy differs.' }
    foreach ($key in @('protocolVersion','appId','installationId','repositoryId')) { Assert-Id $Enrollment[$key] }
    if ($Enrollment.protocolVersion -ne $runtimeVersion -or (Get-BrokerOrigin $Enrollment.brokerOrigin) -cne $BrokerOrigin -or $Enrollment.githubHost -cne 'github.com' -or $Enrollment.deviceId -cne $Marker.deviceId -or $Enrollment.appId -ne $Marker.appId -or $Enrollment.appSlug -cne $Marker.appSlug -or $Enrollment.installationId -ne $Marker.installationId -or $Enrollment.repositoryId -ne $Marker.repositoryId -or $Enrollment.repository -ine $Repository) { Stop-Enrollment 'Completion binding differs from the approved enrollment.' }
    Assert-Text $Enrollment.deviceSecret '\A[A-Za-z0-9_-]{43,128}\z'
    $fields = @('discussions'); if ($Profile.StartsWith('webhook', [StringComparison]::Ordinal)) { $fields += 'repository_hooks' }
    if ($clone) { $fields += 'contents' }
    $variablesPermission = if ($setupVersion -eq 3) { 'actions_variables' } else { 'agent_variables' }
    if ($settings) { $fields += $variablesPermission }
    if ($runtimeVersion -gt 1 -and $Enrollment.permissions.Contains('metadata')) { $fields += 'metadata' }
    Assert-Fields $Enrollment.permissions $fields
    foreach ($key in $fields) { if ($Enrollment.permissions[$key] -cne $(if ($key -in @('contents','metadata','actions_variables','agent_variables')) { 'read' } else { 'write' })) { Stop-Enrollment 'Completion permission profile differs.' } }
}
function Wait-Work([switch]$AcknowledgmentRecovery) {
    if (-not $AcknowledgmentRecovery -and $config.deadline -and [DateTimeOffset]::Parse($config.deadline) -le [DateTimeOffset]::UtcNow.AddSeconds(5)) { Stop-Enrollment 'Original enrollment deadline reached; private recovery state retained.' }
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
    if ($ApprovalLocation) { $ApprovalLocation = (Get-ApprovalLocation -Location $ApprovalLocation).Location }
    $config = $null
    $savedConfig = Test-Path -LiteralPath $configPath
    if ($savedConfig) {
        $config = Read-PrivateJson $configPath
        $savedKind = Get-ApprovalStateKind $config
        if ($savedKind -ceq 'environment-variable') { Stop-Enrollment 'Legacy Environment enrollment cannot resume through repository-variable setup. Retain its files, revoke/settle old authority separately and use a new state directory for fresh enrollment.' }
        # Saved approvals keep their recorded location; migration is a separate, deliberate fresh enrollment.
        $savedLocation = (Get-ApprovalLocation -Kind $savedKind -SetupVersion $config.setupVersion).Location
        if ($ApprovalLocation -and $ApprovalLocation -cne $savedLocation) { Stop-Enrollment "Saved $savedKind enrollment cannot be reinterpreted as $ApprovalLocation approval. Revoke and settle it, then enroll afresh in a new state directory." }
        $ApprovalLocation = $savedLocation
        foreach ($pair in @(@('Repository','repository'),@('BrokerOrigin','brokerOrigin'),@('ApprovalVariable','approvalVariable'),@('Profile','profile'))) {
            $supplied = Get-Variable -Name $pair[0] -ValueOnly
            if ($supplied -and $supplied -cne $config[$pair[1]]) { Stop-Enrollment 'Supplied inputs conflict with saved enrollment; use the original inputs.' }
            Set-Variable -Name $pair[0] -Value $config[$pair[1]]
        }
    }
    $location = if ($config) { Get-ApprovalLocation -Kind $savedKind -SetupVersion $config.setupVersion } else { Get-ApprovalLocation -Location $ApprovalLocation }
    if (-not $config -and $location.SetupVersion -ne 4) { Stop-Enrollment 'Fresh enrollment requires setup v4 in repository Agent variables. Saved legacy state may only resume its original request; revoke and settle old authority before enrolling afresh.' }
    $actions = $location.Location -ceq 'ActionsVariable'
    $setupVersion = $location.SetupVersion
    $approvalKind = $location.Kind
    $script:LocationName = $location.Name
    $defaultVariable = $location.DefaultVariable
    if (-not $Repository -and -not $NonInteractive) { $Repository = Read-Host 'Repository (OWNER/REPO)' }
    if (-not $BrokerOrigin -and -not $NonInteractive) { $BrokerOrigin = Read-Host 'Trusted broker HTTPS origin' }
    if (-not $ApprovalVariable) { $ApprovalVariable = if ($NonInteractive) { $defaultVariable } else { $answer = Read-Host "Approval variable [$defaultVariable]"; if ($answer) { $answer } else { $defaultVariable } } }
    if (-not $Profile) { $Profile = 'polling' }
    Assert-Text $Profile '\A(?:polling|webhook)(?:-clone)?(?:-settings)?\z'
    $clone = $Profile.Contains('-clone', [StringComparison]::Ordinal)
    # Runtime versions do not select the approval store. The saved setup contract fixes it.
    $settings = $Profile.EndsWith('-settings', [StringComparison]::Ordinal)
    if ($settings -and $setupVersion -eq 2) { Stop-Enrollment 'Legacy setup v2 serves only frozen profiles; a settings profile requires fresh setup v4 enrollment.' }
    $runtimeVersion = if ($settings) { 3 } elseif ($clone) { 2 } else { 1 }
    $policy = Get-ApprovalPolicy -SetupVersion $setupVersion -Profile $Profile
    $permissionPreview = 'discussions:write'
    if ($clone) { $permissionPreview += ', contents:read' }
    if ($Profile.StartsWith('webhook', [StringComparison]::Ordinal)) { $permissionPreview += ', repository_hooks:write' }
    if ($settings) { $permissionPreview += $(if ($actions) { ', actions_variables:read' } else { ', agent_variables:read' }) }
    $script:Diagnostic = "Supply OWNER/REPO, a trusted HTTPS broker origin, and a valid $($script:LocationName) approval variable of at most 100 characters."
    Assert-Text $Repository '\A[A-Za-z0-9_.-]{1,100}/[A-Za-z0-9_.-]{1,100}\z'
    if (@($Repository.Split('/') | Where-Object { $_ -in @('.', '..') }).Count) { Stop-Enrollment 'Invalid repository.' }
    Assert-Text $ApprovalVariable '\A[A-Za-z_][A-Za-z0-9_]{0,99}\z'
    $ApprovalVariable = $ApprovalVariable.ToUpperInvariant()
    if ($ApprovalVariable.StartsWith('GITHUB_', [StringComparison]::Ordinal)) { Stop-Enrollment 'Approval variable cannot start with GITHUB_.' }
    if ($setupVersion -in @(3,4) -and -not (Test-RepositoryApprovalName $ApprovalVariable)) { Stop-Enrollment "Approval variable $ApprovalVariable is in the REPOAGENT_ repository-settings namespace, which repository-settings.ps1 writes. Use REPOAGENT_APPROVAL or a name without the REPOAGENT_ prefix." }
    $BrokerOrigin = Get-BrokerOrigin $BrokerOrigin
    if ($null -eq $config) { $config = [ordered]@{ version=$setupVersion; setupVersion=$setupVersion; approvalKind=$approvalKind; repository=$Repository; repositoryId=$null; brokerOrigin=$BrokerOrigin; approvalVariable=$ApprovalVariable; profile=$Profile; pendingId=$null; deadline=$null; marker=$null; expectedValueSha256=$null; approvalWriteAttempted=$false; approvalObserved=$false } }
    $recovery = $null
    if (Test-Path -LiteralPath $recoveryPath) {
        $recovery = Read-PrivateJson $recoveryPath
        Check-Recovery
    } elseif ($savedConfig -or $config.pendingId -or (Test-Path -LiteralPath $enrollmentPath)) { Stop-Enrollment 'Recovery journal is missing; do not create a replacement enrollment.' }
    Write-Host "Repository: $Repository; broker: $BrokerOrigin; App: 5387625 / repo-agent-connector; profile: $Profile"
    Write-Host "Setup/marker: $setupVersion; controller/runtime: $runtimeVersion; policy: $policy; permissions: $permissionPreview"
    Write-Host "Repository $($script:LocationName)-variable approval: $ApprovalVariable; state: $StateDirectory"
    if ($location.Legacy) { Write-Host 'Saved legacy setup retains its original approval and authority. Moving to setup v4 requires revocation, settlement and a fresh enrollment in a new state directory.' }
    Write-Host "Apply may start one pending $($script:LocationName)-variable enrollment, write its validated public marker, save a private device credential and acknowledge it. Existing matching approval is reused; conflicts stop."
    if (-not $Apply) { Write-Host 'Read-only plan. Use -Apply to execute. The fresh marker is available only after start.'; exit 0 }
    $script:Diagnostic = 'Private enrollment state could not be verified. Use an owned local directory with mode 0700/0600 on Linux or a current-user-only protected DACL on Windows; do not discard recovery state.'
    $null = Initialize-PrivateDirectory $StateDirectory
    $repoPath = 'repos/' + (($Repository.Split('/') | ForEach-Object { [Uri]::EscapeDataString($_) }) -join '/')
    $variablesPath = $repoPath + '/' + $location.Path
    $varPath = $variablesPath + '/' + [Uri]::EscapeDataString($ApprovalVariable)
    $brokerSetup = "/v$setupVersion"
    $script:Diagnostic = 'Repository identity could not be read or validated through host gh; no broker enrollment was started.'
    $config.repositoryId = Read-Repository
    $script:Diagnostic = 'Repository approval-variable access could not be read or validated through host gh; no broker enrollment was started.'
    $null = Read-VariableAccess
    $script:Diagnostic = 'Private enrollment binding could not be saved; preserve the selected state directory.'
    Write-PrivateJson $configPath $config
    if ($null -eq $recovery) {
        $recovery = [ordered]@{ version=$setupVersion; setupVersion=$setupVersion; approvalKind=$approvalKind; request=[ordered]@{ idempotencyId=[Guid]::NewGuid().ToString('N'); repositoryId=$config.repositoryId; repository=$Repository; variable=$ApprovalVariable; profile=$Profile }; secret=[Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32)).TrimEnd('=').Replace('+','-').Replace('/','_'); completionId=$null; lastWorkAt=$null; acknowledged=$false; ackAttempted=$false }
        Write-PrivateJson $recoveryPath $recovery
    }
    $script:Diagnostic = 'Enrollment transport or document validation failed. Check broker availability and the saved binding; no automatic repair was attempted.'
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
        $status = Broker POST ($brokerSetup + '/enrollments') $recovery.secret $recovery.request
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
            $completion = Broker POST ($brokerSetup + '/enrollments/' + $config.pendingId + '/completion') $recovery.secret
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
    # Explicit recovery may replay an already-active ACK after expiry. Retain the
    # original deadline so automatic429 retries still cannot wait beyond it.
    Wait-Work -AcknowledgmentRecovery:$recovery.ackAttempted
    $recovery.ackAttempted = $true
    Write-PrivateJson $recoveryPath $recovery
    $receipt = Broker POST ($brokerSetup + '/devices/' + $enrollment.deviceId + '/acknowledgment') $enrollment.deviceSecret @{ completionId=$recovery.completionId }
    Assert-Fields $receipt @('state')
    if ($receipt.state -cne 'active') { Stop-Enrollment 'Activation is unconfirmed; retry acknowledgment using the persisted device credential.' }
    $recovery.acknowledged = $true; $recovery.secret = $null
    Write-PrivateJson $recoveryPath $recovery
    [ordered]@{ state='active'; enrollmentFile=$enrollmentPath; approvalFile=$configPath; brokerClosure='not-requested'; outstandingTokens='unsettled' } | ConvertTo-Json -Compress
    exit 0
} catch {
    # Static source coordinates and exception type help diagnose a failed boundary without reflecting provider bodies,
    # credentials, full host paths or private document contents.
    $location = 'unknown'
    if ($_.InvocationInfo -and $_.InvocationInfo.ScriptName) {
        $location = [IO.Path]::GetFileName($_.InvocationInfo.ScriptName) + ':' + $_.InvocationInfo.ScriptLineNumber
    }
    $errorType = $_.Exception.GetBaseException().GetType().Name
    Write-Warning "$script:Diagnostic Failure location: $location; error type: $errorType."
    exit 1
}
