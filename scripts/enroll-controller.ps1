#Requires -Version 7.4
[CmdletBinding()]
param(
    [string]$Repository,
    [string]$BrokerOrigin,
    [string]$GitHubEnvironment,
    [string]$ApprovalVariable,
    [ValidateSet('polling', 'webhook')][string]$Profile,
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
    if ($script:Calls -gt 30 -or $script:Clock.Elapsed.TotalMinutes -gt 15) { Stop-Enrollment 'GitHub work limit reached; inspect before retrying.' }
    $result = Invoke-HostGitHub $Method $Endpoint $Body
    if ($result.Status -in @(401, 403, 422)) { Stop-Enrollment "GitHub denied this operation (HTTP $($result.Status)). Check host gh access, Environments permission, Administration permission for creation, and repository plan support." }
    return $result
}
function Identity([string]$Path, [string]$Name, $ExpectedId = $null) {
    $r = GitHub GET $Path
    if ($r.Status -ne 200) { Stop-Enrollment 'GitHub identity is unavailable; absence is not established.' }
    $value = Read-HostJson $r.Body
    Assert-Id $value.id
    if ($value.name -isnot [string] -or $value.name -cne $Name -or ($null -ne $ExpectedId -and $value.id -ne $ExpectedId)) { Stop-Enrollment 'GitHub Environment binding changed.' }
    return $value.id
}
function Confirm-Absent([string]$Path, [string]$Collection, [string]$Name) {
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $total = $null
    for ($page = 1; $page -le 5; $page++) {
        $r = GitHub GET ($Path + '?per_page=100&page=' + $page)
        if ($r.Status -ne 200) { Stop-Enrollment 'Cannot confirm absence through a complete GitHub listing.' }
        $v = Read-HostJson $r.Body
        if (($v.total_count -isnot [long] -and $v.total_count -isnot [int]) -or $v.total_count -lt 0 -or $v.total_count -gt 500 -or ($null -ne $total -and $total -ne $v.total_count) -or $v[$Collection] -isnot [array] -or $v[$Collection].Count -gt 100) { Stop-Enrollment 'GitHub listing is incomplete or inconsistent.' }
        $total = $v.total_count
        foreach ($item in $v[$Collection]) {
            if ($item.name -isnot [string] -or -not $seen.Add($item.name)) { Stop-Enrollment 'GitHub listing has an invalid name or duplicate.' }
            if ([string]::Equals($item.name, $Name, [StringComparison]::OrdinalIgnoreCase)) { Stop-Enrollment 'The selected coordinate already exists. Rerun to inspect it; no write was attempted.' }
        }
        if ($seen.Count -eq $total) { return }
        if ($v[$Collection].Count -lt 100 -or $seen.Count -gt $total) { Stop-Enrollment 'GitHub listing is incomplete.' }
    }
    Stop-Enrollment 'GitHub listing exceeded the bounded page limit.'
}
function Broker([string]$Method, [string]$Path, [string]$Secret, $Body = $null) {
    $r = Invoke-EnrollmentBroker $BrokerOrigin $Method $Path $Secret $Body
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
    $config.pendingId = $Status.pendingId; $config.deadline = $Status.deadline
}
function Check-Marker([string]$Text) {
    $m = Read-HostJson $Text
    Assert-Fields $m @('version','githubHost','appId','appSlug','repositoryId','repository','installationId','deviceId','challenge','profile','policyVersion','epoch','environment','environmentId','variable')
    foreach ($key in @('version','appId','repositoryId','installationId','environmentId')) { Assert-Id $m[$key] }
    if ($m.version -ne 1 -or $m.githubHost -cne 'github.com' -or $m.appId -ne 5387625 -or $m.appSlug -cne 'repo-agent-connector' -or $m.repositoryId -ne $config.repositoryId -or $m.repository -ine $Repository -or $m.environment -cne $GitHubEnvironment -or $m.environmentId -ne $config.environmentId -or $m.variable -cne $ApprovalVariable -or $m.profile -cne $Profile -or $m.policyVersion -cne 'repository-approval-v1') { Stop-Enrollment 'Broker approval marker differs from the trusted host intent.' }
    Assert-Text $m.deviceId '\A[0-9a-f]{32}\z'
    Assert-Text $m.challenge '\A[A-Za-z0-9_-]{43}\z'
    Assert-Text $m.epoch '\A[A-Za-z0-9_-]{1,128}\z'
    if ($config.marker -and $config.marker -cne $Text) { Stop-Enrollment 'Broker changed the saved marker.' }
    return $m
}
function Check-Enrollment($Enrollment, $Marker) {
    Assert-Fields $Enrollment @('protocolVersion','brokerOrigin','githubHost','deviceId','deviceSecret','appId','appSlug','installationId','repositoryId','repository','permissions')
    foreach ($key in @('protocolVersion','appId','installationId','repositoryId')) { Assert-Id $Enrollment[$key] }
    if ($Enrollment.protocolVersion -ne 1 -or (Get-BrokerOrigin $Enrollment.brokerOrigin) -cne $BrokerOrigin -or $Enrollment.githubHost -cne 'github.com' -or $Enrollment.deviceId -cne $Marker.deviceId -or $Enrollment.appId -ne $Marker.appId -or $Enrollment.appSlug -cne $Marker.appSlug -or $Enrollment.installationId -ne $Marker.installationId -or $Enrollment.repositoryId -ne $Marker.repositoryId -or $Enrollment.repository -ine $Repository) { Stop-Enrollment 'Completion binding differs from the approved enrollment.' }
    Assert-Text $Enrollment.deviceSecret '\A[A-Za-z0-9_-]{43,128}\z'
    $fields = @('discussions'); if ($Profile -eq 'webhook') { $fields += 'repository_hooks' }
    Assert-Fields $Enrollment.permissions $fields
    foreach ($key in $fields) { if ($Enrollment.permissions[$key] -cne 'write') { Stop-Enrollment 'Completion permission profile differs.' } }
}
function Wait-Work {
    if ($config.deadline -and [DateTimeOffset]::Parse($config.deadline) -le [DateTimeOffset]::UtcNow.AddSeconds(5)) { Stop-Enrollment 'Original enrollment deadline reached; private recovery state retained.' }
    # Persist BEFORE sending: process restart also honors the provider's shared floor.
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
    $config = $null
    if (Test-Path -LiteralPath $configPath) {
        $config = Read-PrivateJson $configPath
        Assert-Fields $config @('version','repository','repositoryId','brokerOrigin','repoEnvironment','environmentId','approvalVariable','profile','pendingId','deadline','marker','expectedValueSha256','approvalWriteAttempted','approvalObserved')
        foreach ($pair in @(@('Repository','repository'),@('BrokerOrigin','brokerOrigin'),@('GitHubEnvironment','repoEnvironment'),@('ApprovalVariable','approvalVariable'),@('Profile','profile'))) {
            $supplied = Get-Variable -Name $pair[0] -ValueOnly
            if ($supplied -and $supplied -cne $config[$pair[1]]) { Stop-Enrollment 'Supplied inputs conflict with saved enrollment; use the original inputs.' }
            Set-Variable -Name $pair[0] -Value $config[$pair[1]]
        }
    }
    if (-not $Repository -and -not $NonInteractive) { $Repository = Read-Host 'Repository (OWNER/REPO)' }
    if (-not $BrokerOrigin -and -not $NonInteractive) { $BrokerOrigin = Read-Host 'Trusted broker HTTPS origin' }
    if (-not $GitHubEnvironment) { $GitHubEnvironment = if ($NonInteractive) { 'repoagent' } else { $answer = Read-Host 'GitHub Environment [repoagent]'; if ($answer) { $answer } else { 'repoagent' } } }
    if (-not $ApprovalVariable) { $ApprovalVariable = if ($NonInteractive) { 'APPROVED_CONTROLLER' } else { $answer = Read-Host 'Approval variable [APPROVED_CONTROLLER]'; if ($answer) { $answer } else { 'APPROVED_CONTROLLER' } } }
    if (-not $Profile) { $Profile = 'polling' }
    Assert-Text $Profile '\A(?:polling|webhook)\z'
    $script:Diagnostic = 'Supply OWNER/REPO, a trusted HTTPS broker origin, a 1-255 character Environment name, and a valid approval variable of at most 100 characters.'
    Assert-Text $Repository '\A[A-Za-z0-9_.-]{1,100}/[A-Za-z0-9_.-]{1,100}\z'
    if (@($Repository.Split('/') | Where-Object { $_ -in @('.', '..') }).Count) { Stop-Enrollment 'Invalid repository.' }
    if ([string]::IsNullOrWhiteSpace($GitHubEnvironment) -or $GitHubEnvironment.Length -gt 255 -or $GitHubEnvironment.ToCharArray().Where({ [char]::IsControl($_) }).Count) { Stop-Enrollment 'GitHub Environment requires 1-255 non-control characters.' }
    Assert-Text $ApprovalVariable '\A[A-Za-z_][A-Za-z0-9_]{0,99}\z'
    $ApprovalVariable = $ApprovalVariable.ToUpperInvariant()
    if ($ApprovalVariable.StartsWith('GITHUB_', [StringComparison]::Ordinal)) { Stop-Enrollment 'Approval variable cannot start with GITHUB_.' }
    $BrokerOrigin = Get-BrokerOrigin $BrokerOrigin
    if ($null -eq $config) { $config = [ordered]@{ version=1; repository=$Repository; repositoryId=$null; brokerOrigin=$BrokerOrigin; repoEnvironment=$GitHubEnvironment; environmentId=$null; approvalVariable=$ApprovalVariable; profile=$Profile; pendingId=$null; deadline=$null; marker=$null; expectedValueSha256=$null; approvalWriteAttempted=$false; approvalObserved=$false } }
    Write-Host "Repository: $Repository; broker: $BrokerOrigin; App: 5387625 / repo-agent-connector; profile: $Profile"
    Write-Host "GitHub Environment: $GitHubEnvironment; approval variable: $ApprovalVariable; state: $StateDirectory"
    Write-Host 'Apply may create the Environment, start one pending enrollment, write its validated public marker, save a private device credential and acknowledge it. Existing matching approval is reused; conflicts stop.'
    if (-not $Apply) { Write-Host 'Read-only plan. Use -Apply to execute. The fresh marker is available only after start.'; exit 0 }
    $script:Diagnostic = 'Private enrollment state could not be verified. Use an owned local directory with mode 0700/0600 on Linux or a current-user-only protected DACL on Windows; do not discard recovery state.'
    $null = Initialize-PrivateDirectory $StateDirectory
    $repoPath = 'repos/' + (($Repository.Split('/') | ForEach-Object { [Uri]::EscapeDataString($_) }) -join '/')
    $envPath = $repoPath + '/environments/' + [Uri]::EscapeDataString($GitHubEnvironment)
    $varPath = $envPath + '/variables/' + [Uri]::EscapeDataString($ApprovalVariable)
    $script:Diagnostic = 'Enrollment transport or document validation failed. Check host gh authentication, broker availability and the saved binding; no automatic repair was attempted.'
    $r = GitHub GET $repoPath
    if ($r.Status -ne 200) { Stop-Enrollment 'Repository access could not be verified.' }
    $repo = Read-HostJson $r.Body; Assert-Id $repo.id
    if ($repo.full_name -isnot [string] -or $repo.full_name -ine $Repository -or ($config.repositoryId -and $config.repositoryId -ne $repo.id)) { Stop-Enrollment 'Repository identity changed.' }
    $config.repositoryId = $repo.id
    $r = GitHub GET $envPath
    if ($r.Status -eq 404) {
        if ($config.environmentId) { Stop-Enrollment 'Previously bound Environment disappeared; enrollment cannot resume.' }
        Confirm-Absent ($repoPath + '/environments') 'environments' $GitHubEnvironment
        $r = GitHub PUT $envPath @{}
        if ($r.Status -ne 200) { Stop-Enrollment 'Environment creation is unconfirmed; inspect before rerunning.' }
    } elseif ($r.Status -ne 200) { Stop-Enrollment 'Cannot verify the selected Environment.' }
    $environment = Read-HostJson $r.Body; Assert-Id $environment.id
    if ($environment.name -isnot [string] -or $environment.name -cne $GitHubEnvironment -or ($config.environmentId -and $config.environmentId -ne $environment.id)) { Stop-Enrollment 'Environment binding differs.' }
    $config.environmentId = Identity $envPath $GitHubEnvironment $environment.id
    Write-PrivateJson $configPath $config
    if (Test-Path -LiteralPath $recoveryPath) {
        $recovery = Read-PrivateJson $recoveryPath
        Assert-Fields $recovery @('request','secret','completionId','lastWorkAt','acknowledged','ackAttempted')
        Assert-Fields $recovery.request @('idempotencyId','repositoryId','repository','environment','variable','profile')
        Assert-Text $recovery.request.idempotencyId '\A[0-9a-f]{32}\z'
        foreach ($pair in @(@('repositoryId','repositoryId'),@('repository','repository'),@('environment','repoEnvironment'),@('variable','approvalVariable'),@('profile','profile'))) {
            if ($recovery.request[$pair[0]] -cne $config[$pair[1]]) { Stop-Enrollment 'Recovery request differs from saved host intent.' }
        }
    } else {
        if ($config.pendingId -or (Test-Path -LiteralPath $enrollmentPath)) { Stop-Enrollment 'Recovery journal is missing; do not create a replacement enrollment.' }
        $recovery = [ordered]@{ request=[ordered]@{ idempotencyId=[Guid]::NewGuid().ToString('N'); repositoryId=$config.repositoryId; repository=$Repository; environment=$GitHubEnvironment; variable=$ApprovalVariable; profile=$Profile }; secret=[Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32)).TrimEnd('=').Replace('+','-').Replace('/','_'); completionId=$null; lastWorkAt=$null; acknowledged=$false; ackAttempted=$false }
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
        $status = Broker POST '/v1/enrollments' $recovery.secret $recovery.request
        Check-Status $status
        if ($null -eq $status.marker) { Save-State; Stop-Enrollment 'Broker discovery is still pending. Rerun the same request after at least five seconds.' }
        if ($status.marker -isnot [string]) { Stop-Enrollment 'Invalid broker marker.' }
        $marker = Check-Marker $status.marker
        $config.marker = $status.marker; $config.expectedValueSha256 = Get-ValueHash $status.marker
        Save-State
        Write-Host "Validated approval: repository ID $($marker.repositoryId), Environment ID $($marker.environmentId), installation $($marker.installationId), device $($marker.deviceId), profile $Profile."
        $null = Identity $envPath $GitHubEnvironment $config.environmentId
        $r = GitHub GET $varPath
        if ($r.Status -eq 200) {
            $variable = Read-HostJson $r.Body
            if ($variable.name -isnot [string] -or $variable.name -ine $ApprovalVariable -or $variable.value -isnot [string] -or $variable.value -cne $config.marker) { Stop-Enrollment 'Approval conflicts with this enrollment. Nothing was replaced; resolve old authority manually.' }
        } elseif ($r.Status -eq 404) {
            if ($config.approvalWriteAttempted -or $config.approvalObserved) { Stop-Enrollment 'A previous approval write was attempted and its marker is now absent. Inspect manually; it will not be restored.' }
            Confirm-Absent ($envPath + '/variables') 'variables' $ApprovalVariable
            $null = Identity $envPath $GitHubEnvironment $config.environmentId
            $config.approvalWriteAttempted = $true
            Write-PrivateJson $configPath $config
            $r = GitHub POST ($envPath + '/variables') @{ name=$ApprovalVariable; value=$config.marker }
            if ($r.Status -ne 201) { Stop-Enrollment 'Approval creation is unconfirmed. Inspect and rerun the same enrollment; no automatic retry was attempted.' }
        } else { Stop-Enrollment 'Approval could not be verified.' }
        $config.approvalObserved = $true
        Write-PrivateJson $configPath $config
        for ($poll = 0; $poll -lt 180; $poll++) {
            Wait-Work
            $completion = Broker POST ('/v1/enrollments/' + $config.pendingId + '/completion') $recovery.secret
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
    $receipt = Broker POST ('/v1/devices/' + $enrollment.deviceId + '/acknowledgment') $enrollment.deviceSecret @{ completionId=$recovery.completionId }
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
