#Requires -Version 7.4
# The journal contains resource identities and hashes, never enrollment or tokens.
Import-Module (Join-Path $PSScriptRoot '../host-enrollment.psm1') -DisableNameChecking

function Open-AzureUpdateState([string]$Directory, [string]$ResourceId) {
    Assert-Text $ResourceId '\A/subscriptions/[0-9a-f-]{36}/resourceGroups/[A-Za-z0-9_.()-]+/providers/Microsoft.App/containerApps/[a-z][a-z0-9-]+\z'
    $directory = Initialize-PrivateDirectory $Directory
    $path = Join-Path $directory 'update.lock'
    if (Test-Path -LiteralPath $path) { Assert-PrivatePath $path }
    $options = [IO.FileStreamOptions]::new()
    $options.Mode = [IO.FileMode]::OpenOrCreate
    $options.Access = [IO.FileAccess]::ReadWrite
    $options.Share = [IO.FileShare]::None
    if ($IsLinux) { $options.UnixCreateMode = [IO.UnixFileMode]384 }
    try { $lock = [IO.FileStream]::new($path, $options) }
    catch { throw 'Another operation holds this Azure update state directory.' }
    try {
        Set-PrivateFileAcl $path
        Assert-PrivatePath $path
        $context = @{ ResourceId = $ResourceId; Path = (Join-Path $directory 'update.json'); Lock = $lock }
        $null = Read-AzureUpdateJournal $context
        return $context
    } catch { $lock.Dispose(); throw }
}

function Read-AzureUpdateJournal($Context) {
    if (-not (Test-Path -LiteralPath $Context.Path)) { return $null }
    $record = Read-PrivateJson $Context.Path
    if ($record.version -eq 1) { throw 'Pre-release journal lacks launch evidence; preserve it and inspect Azure before recovery.' }
    Assert-AzureUpdateJournal $record $Context.ResourceId
    return $record
}

function Assert-AzureUpdateJournal($Record, [string]$ResourceId) {
    Assert-Fields $record @('version','resourceId','configurationSha256','previousRevision','previousImage','candidateRevision','candidateImage','phase','launchAttempted')
    if (($record.version -isnot [long] -and $record.version -isnot [int]) -or
            $record.version -ne 2 -or $record.resourceId -cne $ResourceId -or $record.launchAttempted -isnot [bool]) {
        throw 'The update journal belongs to a different Azure app or version.'
    }
    Assert-Text $record.configurationSha256 '\A[0-9a-f]{64}\z'
    foreach ($key in @('previousRevision','candidateRevision')) {
        Assert-Text $record[$key] '\A[a-z][a-z0-9-]+--[a-z0-9-]+\z'
        if (-not $record[$key].StartsWith(($ResourceId.Split('/')[-1] + '--'), [StringComparison]::Ordinal)) {
            throw 'The recorded revision belongs to a different Azure app.'
        }
    }
    foreach ($key in @('previousImage','candidateImage')) {
        Assert-Text $record[$key] '\Aghcr\.io/repoagent/controller@sha256:[0-9a-f]{64}\z'
    }
    Assert-Text $record.phase '\A(prepared|draining|drained|launching|ready|committed|rollback-draining|rollback-ready|rolled-back)\z'
    if (($record.phase -cin @('prepared','draining','drained') -and $record.launchAttempted) -or
            ($record.phase -cin @('launching','ready','committed') -and -not $record.launchAttempted)) {
        throw 'The journal phase conflicts with retained launch evidence.'
    }
}

function Begin-AzureImageUpdate($Context, [string]$ConfigurationSha256, [string]$PreviousRevision,
        [string]$PreviousImage, [string]$CandidateRevision, [string]$CandidateImage) {
    $previous = Read-AzureUpdateJournal $Context
    if ($previous -and $previous.phase -cnotin @('committed', 'rolled-back')) {
        throw 'An Azure update is pending; use explicit Rollback before another Update.'
    }
    if ($previous -and $previous.configurationSha256 -cne $ConfigurationSha256) {
        throw 'Retained Azure configuration differs from the recorded update binding.'
    }
    $record = [ordered]@{
        version = 2; resourceId = $Context.ResourceId; configurationSha256 = $ConfigurationSha256
        previousRevision = $PreviousRevision; previousImage = $PreviousImage
        candidateRevision = $CandidateRevision; candidateImage = $CandidateImage; phase = 'prepared'; launchAttempted = $false
    }
    Assert-AzureUpdateJournal $record $Context.ResourceId
    Write-PrivateJson $Context.Path $record
    return Read-AzureUpdateJournal $Context
}

function Set-AzureUpdatePhase($Context, [string]$Phase) {
    $record = Read-AzureUpdateJournal $Context
    if (-not $record) { throw 'No Azure update journal exists; inspect before recovery.' }
    $next = @{
        prepared = 'draining'; draining = 'drained'; drained = 'launching'
        launching = 'ready'; ready = 'committed'
        'rollback-draining' = 'rollback-ready'; 'rollback-ready' = 'rolled-back'
    }
    $rollback = $Phase -ceq 'rollback-draining' -and $record.phase -cne 'rolled-back'
    if ($Phase -cne $record.phase -and -not $rollback -and $next[$record.phase] -cne $Phase) {
        throw 'Invalid Azure update transition; use explicit Rollback for recovery.'
    }
    $record.phase = $Phase
    if ($Phase -ceq 'launching') { $record.launchAttempted = $true }
    Write-PrivateJson $Context.Path $record
    return Read-AzureUpdateJournal $Context
}

Export-ModuleMember -Function Open-AzureUpdateState, Read-AzureUpdateJournal, Begin-AzureImageUpdate, Set-AzureUpdatePhase
