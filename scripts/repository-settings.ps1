#Requires -Version 7.4
<#
.SYNOPSIS
Inspect or explicitly change controller settings held in direct repository Actions variables.
.DESCRIPTION
Uses the operator's authenticated host gh; no host credential reaches the controller. Inspection only
reads. Changes require -Apply. Only names in the controller's shared catalogue
(src/RepoAgentController/repository-settings.json) are accepted, with the controller's validation rules.
The controller runtime can read, never write, these variables.
#>
[CmdletBinding()]
param(
    [string]$Repository,
    [string[]]$Setting = @(),
    [string[]]$ClearSetting = @(),
    [switch]$Apply,
    [switch]$NonInteractive
)
$ErrorActionPreference = 'Stop'
Import-Module "$PSScriptRoot/host-enrollment.psm1" -Force -DisableNameChecking
$script:Diagnostic = 'Repository settings could not be inspected. Check host gh authentication and the selected repository.'
$script:Calls = 0
$script:CallLimit = 0
function Stop-Settings([string]$Message) { $script:Diagnostic = $Message; throw 'Settings stopped.' }
function GitHub([string]$Method, [string]$Endpoint, $Body = $null) {
    $script:Calls++
    if ($script:Calls -gt $script:CallLimit) { Stop-Settings 'GitHub work limit reached; inspect before retrying.' }
    $result = Invoke-HostGitHub $Method $Endpoint $Body
    if ($result.Status -in @(401, 403)) { Stop-Settings "GitHub denied this operation (HTTP $($result.Status)). The host gh account needs repository Variables read access to inspect, and write access to change, settings." }
    return $result
}
function Read-Catalogue {
    $catalogue = Read-HostJson ([IO.File]::ReadAllText((Join-Path $PSScriptRoot '../src/RepoAgentController/repository-settings.json')))
    Assert-Fields $catalogue @('schemaVersion', 'reserved', 'settings')
    if ($catalogue.schemaVersion -ne 1 -or $catalogue.settings -isnot [array] -or $catalogue.reserved -isnot [array]) { throw 'Unsupported repository settings catalogue.' }
    foreach ($name in $catalogue.reserved) { Assert-Text $name '\AREPOAGENT_[A-Z0-9_]{1,90}\z' }
    $script:Reserved = $catalogue.reserved
    foreach ($entry in $catalogue.settings) {
        if ($entry.type -ceq 'token') { Assert-Fields $entry @('name', 'description', 'type') }
        else { Assert-Fields $entry @('name', 'description', 'type', 'minimum', 'maximum') }
        Assert-Text $entry.name '\AREPOAGENT_[A-Z0-9_]{1,90}\z'
        if ($entry.name -in $script:Reserved) { throw 'Invalid repository settings catalogue entry.' }
        if ($entry.type -cne 'token' -and ($entry.type -cne 'integer' -or $entry.minimum -isnot [long] -or $entry.maximum -isnot [long] -or $entry.minimum -lt 0 -or $entry.maximum -lt $entry.minimum)) { throw 'Invalid repository settings catalogue entry.' }
    }
    return $catalogue.settings
}
# Mirrors RepositorySetting.Normalize in the controller: the written value is what the controller consumes.
function Get-Normalized($Entry, [string]$Value) {
    $text = $Value.Trim()
    # A model or reasoning name; whether the Codex account offers it is checked when a turn uses it.
    if ($Entry.type -ceq 'token') { $text = $text.ToLowerInvariant(); if ($text -cmatch '\A[a-z0-9][a-z0-9._-]{0,63}\z') { return $text } else { return $null } }
    if ($text -cnotmatch '\A[0-9]{1,18}\z') { return $null }
    $number = [long]::Parse($text, [Globalization.CultureInfo]::InvariantCulture)
    if ($number -lt $Entry.minimum -or $number -gt $Entry.maximum) { return $null }
    return $number.ToString([Globalization.CultureInfo]::InvariantCulture)
}
function Get-Reason($Entry) {
    if ($Entry.type -ceq 'token') { return "must be a name of at most 64 letters, digits, '.', '_' or '-'" }
    return "must be an integer from $($Entry.minimum) to $($Entry.maximum)"
}
function Find-Entry([string]$Name) {
    $reserved = $script:Reserved | Where-Object { $_ -ieq $Name } | Select-Object -First 1
    if ($reserved) { Stop-Settings "$reserved is reserved for the controller approval marker; enrollment and revocation manage it. Nothing was written." }
    $entry = $catalogue | Where-Object { $_.name -ieq $Name } | Select-Object -First 1
    if (-not $entry) {
        $shown = if ($Name -cmatch '\A[A-Za-z0-9_]{1,100}\z') { "'$Name'" } else { 'name' }
        Stop-Settings "Unsupported setting $shown. Supported: $(($catalogue | ForEach-Object name) -join ', ')."
    }
    return $entry
}
function Read-Setting($Entry) {
    $r = GitHub GET ($variablesPath + '/' + [Uri]::EscapeDataString($Entry.name))
    # GitHub confirms absence with 404; any other failure leaves the current value unknown.
    if ($r.Status -eq 404) { return [ordered]@{ name = $Entry.name; state = 'absent' } }
    if ($r.Status -ne 200) { return [ordered]@{ name = $Entry.name; state = 'read-failed'; reason = "HTTP $($r.Status)" } }
    $variable = Read-HostJson $r.Body
    if ($variable.name -isnot [string] -or $variable.name -ine $Entry.name -or $variable.value -isnot [string]) { Stop-Settings 'GitHub returned a malformed repository variable.' }
    $value = Get-Normalized $Entry $variable.value
    if ($null -eq $value) { return [ordered]@{ name = $Entry.name; state = 'invalid'; reason = (Get-Reason $Entry); raw = $variable.value } }
    return [ordered]@{ name = $Entry.name; state = 'configured'; value = $value; raw = $variable.value }
}
function Get-Public($Observed) {
    $public = [ordered]@{}
    foreach ($key in $Observed.Keys | Where-Object { $_ -ne 'raw' }) { $public[$key] = $Observed[$key] }
    return $public
}

try {
    $catalogue = @(Read-Catalogue)
    $script:CallLimit = 1 + 3 * $catalogue.Count
    if (-not $Repository -and -not $NonInteractive) { $Repository = Read-Host 'Repository (OWNER/REPO)' }
    $script:Diagnostic = 'Supply -Repository OWNER/REPO with supported -Setting NAME=VALUE and -ClearSetting NAME entries.'
    Assert-Text $Repository '\A[A-Za-z0-9_.-]{1,100}/[A-Za-z0-9_.-]{1,100}\z'
    if (@($Repository.Split('/') | Where-Object { $_ -in @('.', '..') }).Count) { throw 'Invalid repository.' }
    $desired = [ordered]@{}
    foreach ($item in @($Setting) + @($ClearSetting | ForEach-Object { [pscustomobject]@{ Clear = $_ } })) {
        if ($item -is [string]) {
            $parts = $item -split '=', 2
            $entry = Find-Entry $parts[0]
            if ($parts.Count -ne 2) { Stop-Settings 'Use -Setting NAME=VALUE.' }
            $value = Get-Normalized $entry $parts[1]
            if ($null -eq $value) { Stop-Settings "$($entry.name) $(Get-Reason $entry); nothing was written." }
        } else { $entry = Find-Entry $item.Clear; $value = $null }
        if ($desired.Contains($entry.name)) { Stop-Settings 'Select each setting at most once.' }
        $desired[$entry.name] = $value
    }
    $repoPath = 'repos/' + (($Repository.Split('/') | ForEach-Object { [Uri]::EscapeDataString($_) }) -join '/')
    $variablesPath = $repoPath + '/actions/variables'
    $script:Diagnostic = 'GitHub transport or response validation failed. Nothing is retried automatically; inspect and rerun.'
    $r = GitHub GET $repoPath
    if ($r.Status -ne 200) { Stop-Settings 'Repository access could not be verified.' }
    $repo = Read-HostJson $r.Body
    if ($repo.full_name -isnot [string] -or $repo.full_name -ine $Repository) { Stop-Settings 'Repository identity differs from the selected repository.' }
    $current = [ordered]@{}
    foreach ($entry in $catalogue) { $current[$entry.name] = Read-Setting $entry }
    $changes = @()
    foreach ($name in $desired.Keys) {
        $observed = $current[$name]; $value = $desired[$name]
        if ($observed.state -eq 'read-failed') { Stop-Settings "$name could not be read ($($observed.reason)); its current value is unknown, so nothing was written." }
        $action = if ($null -eq $value) { if ($observed.state -eq 'absent') { 'unchanged' } else { 'delete' } }
            elseif ($observed.state -eq 'absent') { 'create' }
            elseif ($observed.raw -ceq $value) { 'unchanged' } else { 'update' }
        $changes += [ordered]@{ name = $name; action = $action; value = $value }
    }
    foreach ($observed in $current.Values) {
        Write-Host "$($observed.name): $($observed.state)$(if ($observed.Contains('value')) { " $($observed.value)" })$(if ($observed.Contains('reason')) { " ($($observed.reason))" })"
    }
    foreach ($change in $changes) { Write-Host "Planned: $($change.name) $($change.action)$(if ($null -ne $change.value) { " to $($change.value)" })" }
    if (-not $Apply) {
        if ($changes.Count) { Write-Host 'Read-only preview. Rerun with -Apply to write these repository variables.' }
    } else {
        foreach ($change in $changes | Where-Object { $_.action -ne 'unchanged' }) {
            $path = $variablesPath + '/' + [Uri]::EscapeDataString($change.name)
            $script:Diagnostic = "Writing $($change.name) is unconfirmed. Inspect the repository variable and rerun; no automatic retry was attempted."
            $r = switch ($change.action) {
                'create' { GitHub POST $variablesPath ([ordered]@{ name = $change.name; value = $change.value }) }
                'update' { GitHub PATCH $path ([ordered]@{ name = $change.name; value = $change.value }) }
                'delete' { GitHub DELETE $path }
            }
            if ($r.Status -notin @{ create = @(201); update = @(204); delete = @(204, 404) }[$change.action]) { Stop-Settings "$($script:Diagnostic) (HTTP $($r.Status))" }
            $current[$change.name] = Read-Setting (Find-Entry $change.name)
            $confirmed = $current[$change.name]
            if (($null -eq $change.value -and $confirmed.state -ne 'absent') -or ($null -ne $change.value -and $confirmed.raw -cne $change.value)) { Stop-Settings 'The written setting could not be confirmed; inspect it before retrying.' }
            Write-Host "Applied: $($change.name) $($change.action)"
        }
    }
    [ordered]@{ repository = $Repository; settings = @($current.Values | ForEach-Object { Get-Public $_ }); changes = $changes; applied = [bool]$Apply } | ConvertTo-Json -Depth 5 -Compress
    exit 0
} catch {
    Write-Warning $script:Diagnostic
    exit 1
}
