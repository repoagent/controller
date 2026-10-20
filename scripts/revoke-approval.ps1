#Requires -Version 7.0
<#
.SYNOPSIS
Inspect or withdraw one repository approval using the host's gh authentication.
.DESCRIPTION
Apply checks the SHA-256 of the exact decoded variable value before one DELETE.
GitHub has no atomic compare-and-delete: concurrent writes may need manual repair.
#>
[CmdletBinding()]
param(
    [string]$Repository,
    [string]$GitHubEnvironment = 'repoagent',
    [string]$ApprovalVariable = 'APPROVED_CONTROLLER',
    [string]$ExpectedValueSha256,
    [string]$ExpectedRepositoryId,
    [string]$ExpectedEnvironmentId,
    [switch]$Apply,
    [switch]$NonInteractive
)

$ErrorActionPreference = 'Stop'
$script:SafeDiagnostic = 'The GitHub result could not be verified. Check host gh authentication and inspect the selected variable before retrying.'
$script:CallCount = 0
$script:RemoteClock = [Diagnostics.Stopwatch]::new()
$repositoryId = $null
$environmentId = $null

function Stop-Revoke([string]$Message) {
    $script:SafeDiagnostic = $Message
    throw 'Revocation verification failed.'
}

function Write-Outcome([string]$Marker, [string]$Fingerprint, [string]$Recovery) {
    [ordered]@{
        repository = $Repository
        repositoryId = $repositoryId
        githubEnvironment = $GitHubEnvironment
        environmentId = $environmentId
        approvalVariable = $ApprovalVariable
        observedValueSha256 = $Fingerprint
        expectedValueSha256 = $ExpectedValueSha256
        githubMarker = $Marker
        brokerClosure = 'unconfirmed'
        outstandingTokens = 'unsettled'
        recovery = $Recovery
    } | ConvertTo-Json -Compress
}

function Invoke-GitHub([string]$Method, [string]$Endpoint) {
    $script:CallCount++
    $remaining = 60000 - $script:RemoteClock.ElapsedMilliseconds
    if ($script:CallCount -gt 4 -or $remaining -le 0) { Stop-Revoke 'The remote-work limit was reached; inspect GitHub before retrying.' }
    $timeout = [int][Math]::Min(15000, $remaining)
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $script:GitHubCli
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in @('api', '--hostname', 'github.com', '--method', $Method,
            '--include', '-H', 'Accept: application/vnd.github+json', $Endpoint)) {
        $start.ArgumentList.Add($argument)
    }
    $start.Environment['GH_PROMPT_DISABLED'] = '1'
    $start.Environment['GH_PAGER'] = 'cat'
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    $started = $false
    $output = [IO.MemoryStream]::new()
    $clock = [Diagnostics.Stopwatch]::StartNew()
    try {
        $started = $process.Start()
        if (-not $started) { Stop-Revoke 'GitHub CLI could not start.' }
        # Read both byte streams concurrently, with bounded buffers and a combined
        # 256 KiB limit. Never retain or print provider stderr.
        $streams = @($process.StandardOutput.BaseStream, $process.StandardError.BaseStream)
        $buffers = @([byte[]]::new(8192), [byte[]]::new(8192))
        $reads = @($streams[0].ReadAsync($buffers[0], 0, 8192), $streams[1].ReadAsync($buffers[1], 0, 8192))
        $byteCount = 0
        while ($null -ne $reads[0] -or $null -ne $reads[1]) {
            $timeLeft = $timeout - $clock.ElapsedMilliseconds
            if ($timeLeft -le 0) { Stop-Revoke 'GitHub request timed out; its remote result may be unknown.' }
            $slots = @(0, 1 | Where-Object { $null -ne $reads[$_] })
            [Threading.Tasks.Task[]]$active = @($slots | ForEach-Object { $reads[$_] })
            $completed = [Threading.Tasks.Task]::WaitAny($active, [int]$timeLeft)
            if ($completed -eq -1) { Stop-Revoke 'GitHub request timed out; its remote result may be unknown.' }
            $slot = $slots[$completed]
            $count = $reads[$slot].GetAwaiter().GetResult()
            if ($count -eq 0) { $reads[$slot] = $null; continue }
            $byteCount += $count
            if ($byteCount -gt 262144) { Stop-Revoke 'GitHub response exceeded the 256 KiB processing limit; its remote result may be unknown.' }
            if ($slot -eq 0) { $output.Write($buffers[$slot], 0, $count) }
            $reads[$slot] = $streams[$slot].ReadAsync($buffers[$slot], 0, 8192)
        }
        $timeLeft = $timeout - $clock.ElapsedMilliseconds
        if ($timeLeft -le 0 -or -not $process.WaitForExit([int]$timeLeft)) {
            Stop-Revoke 'GitHub request timed out; its remote result may be unknown.'
        }
        $text = [Text.UTF8Encoding]::new($false, $true).GetString($output.ToArray())
        $statusLine = [regex]::Match($text, '\AHTTP/[0-9.]+\s+(?<status>[0-9]{3})[^\r\n]*\r?\n')
        $separator = [regex]::Match($text, '\r?\n\r?\n')
        if (-not $statusLine.Success -or -not $separator.Success) { Stop-Revoke 'GitHub returned incomplete response headers; the result is unverified.' }
        $status = [int]$statusLine.Groups['status'].Value
        if ($process.ExitCode -ne 0 -and $status -ge 200 -and $status -lt 300) {
            Stop-Revoke 'GitHub CLI failed despite a success status; the result is unverified.'
        }
        return @{ Status = $status; Body = $text.Substring($separator.Index + $separator.Length) }
    } finally {
        if ($started -and -not $process.HasExited) {
            try { $process.Kill($true) } catch { } # Do not hide the safe diagnostic.
        }
        $process.Dispose()
        $output.Dispose()
    }
}

function Read-Identity([string]$Endpoint, [string]$Expected, [string]$Kind) {
    $response = Invoke-GitHub GET $Endpoint
    if ($response.Status -ne 200) { Stop-Revoke "$Kind could not be verified (HTTP $($response.Status)); absence is not confirmed." }
    $data = ConvertFrom-Json -InputObject $response.Body -AsHashtable -ErrorAction Stop
    if (($data.id -isnot [long] -and $data.id -isnot [int]) -or $data.id -le 0) { Stop-Revoke "$Kind returned an invalid immutable identity." }
    if ($Expected -and [long]$Expected -ne $data.id) { Stop-Revoke "$Kind identity differs from the expected ID; nothing was deleted." }
    return $data.id
}

try {
    if (-not $Repository -and -not $NonInteractive) { $Repository = Read-Host 'Repository (OWNER/REPO)' }
    if ($Repository -cnotmatch '\A[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+\z' `
        -or @($Repository.Split('/') | Where-Object { $_ -in @('.', '..') }).Count) {
        Stop-Revoke 'Supply -Repository OWNER/REPO.'
    }
    if ([string]::IsNullOrWhiteSpace($GitHubEnvironment) -or $GitHubEnvironment.Length -gt 255 `
        -or $GitHubEnvironment.ToCharArray().Where({ [char]::IsControl($_) }).Count) {
        Stop-Revoke 'GitHub environment must contain 1-255 non-control characters.'
    }
    if ($ApprovalVariable -cnotmatch '\A[A-Za-z_][A-Za-z0-9_]{0,254}\z' `
        -or $ApprovalVariable.StartsWith('GITHUB_', [StringComparison]::OrdinalIgnoreCase)) {
        Stop-Revoke 'Approval variable must be a valid GitHub configuration-variable name.'
    }
    $ApprovalVariable = $ApprovalVariable.ToUpperInvariant()
    # Prompt before any GitHub calls, so the selected variable GET remains the
    # final remote observation before the single DELETE. Apply is explicit consent.
    if ($Apply -and -not $ExpectedValueSha256 -and -not $NonInteractive) {
        $ExpectedValueSha256 = Read-Host 'Expected exact-value SHA-256 (obtain it with a read-only preview first)'
    }
    if (($ExpectedValueSha256 -and $ExpectedValueSha256 -cnotmatch '\A[0-9a-fA-F]{64}\z') `
        -or ($Apply -and -not $ExpectedValueSha256)) {
        Stop-Revoke 'Apply requires -ExpectedValueSha256 with 64 hexadecimal SHA-256 characters.'
    }
    foreach ($expectedId in @($ExpectedRepositoryId, $ExpectedEnvironmentId)) {
        $parsed = 0L
        if ($expectedId -and ($expectedId -cnotmatch '\A[1-9][0-9]*\z' `
            -or -not [long]::TryParse($expectedId, [ref]$parsed))) {
            Stop-Revoke 'Expected repository/environment IDs must be positive 64-bit integers.'
        }
    }
    $script:GitHubCli = (Get-Command gh -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
    $parts = $Repository.Split('/')
    $repoEndpoint = 'repos/' + [Uri]::EscapeDataString($parts[0]) + '/' + [Uri]::EscapeDataString($parts[1])
    $environmentEndpoint = $repoEndpoint + '/environments/' + [Uri]::EscapeDataString($GitHubEnvironment)
    $variableEndpoint = $environmentEndpoint + '/variables/' + [Uri]::EscapeDataString($ApprovalVariable)
    Write-Host 'Inspecting the selected repository, environment and approval using host gh. No host credentials are exported.'
    Write-Host 'Approval means GitHub-authorized variable management; it does not attest a particular human administrator.'
    Write-Host 'A concurrent writer can lose a newer approval between GET and DELETE. Inspect and reapprove manually if needed.'
    $script:RemoteClock.Start()
    $repositoryId = Read-Identity $repoEndpoint $ExpectedRepositoryId 'Repository'
    $environmentId = Read-Identity $environmentEndpoint $ExpectedEnvironmentId 'GitHub environment'
    $response = Invoke-GitHub GET $variableEndpoint
    if ($response.Status -ne 200) { Stop-Revoke "Approval could not be verified (HTTP $($response.Status)); absence or removal is not confirmed." }
    $value = ConvertFrom-Json -InputObject $response.Body -AsHashtable -ErrorAction Stop
    if ($value.name -isnot [string] -or $value.value -isnot [string] `
        -or -not [string]::Equals($value.name, $ApprovalVariable, [StringComparison]::OrdinalIgnoreCase)) {
        Stop-Revoke 'GitHub returned an unexpected approval name or value.'
    }
    $fingerprint = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData(
        [Text.Encoding]::UTF8.GetBytes($value.value))).ToLowerInvariant()
    if ($ExpectedValueSha256 -and $ExpectedValueSha256 -ine $fingerprint) {
        Write-Outcome 'conflict' $fingerprint 'The observed value differs from the expected value; nothing was deleted. Inspect the selected approval before retrying.'
        exit 1
    }
    if (-not $Apply) {
        Write-Outcome 'preview' $fingerprint 'Read-only preview. Review the target and use -Apply with -ExpectedValueSha256 to withdraw it.'
        exit 0
    }
    # No retry, lock or atomic compare-delete claim: this check deliberately
    # accepts an independent-writer availability race and manual recovery.
    $removed = Invoke-GitHub DELETE $variableEndpoint
    if ($removed.Status -ne 204) { Stop-Revoke "Approval removal is unconfirmed (HTTP $($removed.Status)); inspect GitHub before retrying." }
    Write-Outcome 'removed' $fingerprint 'GitHub acknowledged deletion at this coordinate; concurrent value and persistent absence are not proven. Check broker closure and outstanding tokens separately. Resolve the old enrollment before manually reapproving.'
    exit 0
} catch {
    # Parsing/OS exceptions may contain provider data. Only controlled diagnostics
    # are exposed, never response bodies, stderr or credentials.
    Write-Warning $script:SafeDiagnostic
    Write-Outcome 'unverifiable' '' 'No automatic retry or repair was attempted. Inspect GitHub before retrying; broker closure and outstanding tokens remain unconfirmed.'
    exit 1
}
