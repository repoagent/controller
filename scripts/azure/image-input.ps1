# Read the delivered v1 image manifest without adding a host Python dependency.
function Read-ImageInput([string]$Path) {
    $document = [System.Text.Json.JsonDocument]::Parse([IO.File]::ReadAllText((Resolve-Path $Path)))
    function Assert-Unique($Element) {
        if ($Element.ValueKind -eq 'Object') {
            $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
            foreach ($p in $Element.EnumerateObject()) {
                if (-not $seen.Add($p.Name)) { throw 'Duplicate image manifest field.' }
                Assert-Unique $p.Value
            }
        } elseif ($Element.ValueKind -eq 'Array') { foreach ($v in $Element.EnumerateArray()) { Assert-Unique $v } }
    }
    try { Assert-Unique $document.RootElement } finally { $document.Dispose() }
    $value = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -AsHashtable -NoEnumerate
    function Assert-Keys($Object, $Required, $Optional = @()) {
        if ($Object -isnot [Collections.IDictionary]) { throw 'Invalid image manifest object.' }
        foreach ($key in $Required) { if (-not $Object.Contains($key)) { throw 'Missing image manifest field.' } }
        foreach ($entry in $Object.GetEnumerator()) { if ($entry.Key -cnotin @($Required + $Optional)) { throw 'Unknown image manifest field.' } }
    }
    Assert-Keys $value @('schemaVersion','platform','controller','agent','githubCli','tools') @('baseImage')
    if ($value.schemaVersion -isnot [long] -and $value.schemaVersion -isnot [int]) { throw 'Invalid image schemaVersion.' }
    if ($value.schemaVersion -ne 1 -or $value.platform -isnot [string] -or $value.platform -cne 'linux/amd64') { throw 'Expected image manifest v1 linux/amd64.' }
    Assert-Keys $value.controller @('source') @('sha256')
    if ($value.controller.source -isnot [string] -or $value.controller.source -cne 'checkout') { throw 'Expected checkout controller source.' }
    if ($value.controller.Contains('sha256') -and ($value.controller.sha256 -isnot [string] -or $value.controller.sha256 -cnotmatch '^[0-9a-f]{64}$')) { throw 'Invalid controller source digest.' }
    Assert-Keys $value.agent @('profile','version')
    Assert-Keys $value.githubCli @('version')
    Assert-Keys $value.tools @() @('cloudflared','developerUtilities')
    if ($value.agent.profile -isnot [string] -or $value.agent.profile -cne 'codex') { throw 'Only the delivered Codex profile is supported.' }
    if ($value.tools.Contains('developerUtilities') -and $value.tools.developerUtilities -isnot [bool]) { throw 'Invalid developerUtilities selection.' }
    # Consume the existing reviewed catalog; this planner has no software version catalog.
    $catalog = Get-Content -LiteralPath (Join-Path $PSScriptRoot '../compose-image.py') -Raw
    $selected = @{ codex = $value.agent.version; gh = $value.githubCli.version }
    if ($value.tools.Contains('cloudflared')) { $selected.cloudflared = $value.tools.cloudflared }
    foreach ($name in $selected.Keys) {
        $match = [regex]::Match($catalog, "(?m)^    '" + $name + "': \('([^']+)',")
        if (-not $match.Success -or $selected[$name] -isnot [string] -or $selected[$name] -cne $match.Groups[1].Value) { throw 'Software selection is outside the delivered reviewed catalog.' }
    }
    if ($value.Contains('baseImage')) {
        Assert-Keys $value.baseImage @('reference')
        if ($value.baseImage.reference -isnot [string] -or $value.baseImage.reference -cnotmatch '^(?:[a-z0-9][a-z0-9._:/-]*@)?sha256:[0-9a-f]{64}$') { throw 'Invalid immutable base image.' }
    }
    return $value
}
