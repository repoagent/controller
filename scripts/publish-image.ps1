#Requires -Version 7.0
<#
.SYNOPSIS
Plan or explicitly apply publication of one immutable local controller image.
.DESCRIPTION
Uses only the host Docker client's existing registry identity. Returns JSON;
publication is verified only when an immutable remote manifest's config digest
matches ImageId. Runtime pull authorization and Azure deployment are separate.
#>
[CmdletBinding()]
param(
    [string]$ImageId,
    [string]$Destination,
    [string]$Repository,
    [string]$Tag,
    [switch]$Apply
)

$ErrorActionPreference = 'Stop'
$stage = 'validate inputs'
$publicationMayHaveChanged = $false
$digestPattern = '\Asha256:[a-f0-9]{64}\z'
$manifestTypes = @('application/vnd.docker.distribution.manifest.v2+json', 'application/vnd.oci.image.manifest.v1+json')
# Request only identity fields: full image inspection can contain user secrets.
$identityFormat = '{"id":{{json .Id}},"os":{{json .Os}},"architecture":{{json .Architecture}}}'

function Invoke-Docker([string[]]$Arguments, [switch]$Capture) {
    $start = [System.Diagnostics.ProcessStartInfo]::new()
    $start.FileName = 'docker'
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.CreateNoWindow = $true
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $start
    try {
        if (-not $process.Start()) { throw 'Docker could not start.' }
        $clock = [System.Diagnostics.Stopwatch]::StartNew()
        $output = [System.Text.StringBuilder]::new()
        $stdout = [char[]]::new(4096)
        $stderr = [char[]]::new(4096)
        $outputTask = $process.StandardOutput.ReadAsync($stdout, 0, $stdout.Length)
        $errorTask = $process.StandardError.ReadAsync($stderr, 0, $stderr.Length)
        $total = 0
        # Drain both pipes concurrently, without unbounded ReadToEnd buffering or
        # PowerShell callbacks running on threads without an available runspace.
        while ($null -ne $outputTask -or $null -ne $errorTask) {
            if ($clock.Elapsed.TotalMinutes -ge 15) { throw 'Docker timed out.' }
            if ($null -ne $outputTask -and $outputTask.IsCompleted) {
                $count = $outputTask.GetAwaiter().GetResult()
                $total += $count
                if ($total -gt 1048576) { throw 'Docker output exceeded the limit.' }
                if ($count -eq 0) { $outputTask = $null } else {
                    if ($Capture) { [void]$output.Append($stdout, 0, $count) }
                    $outputTask = $process.StandardOutput.ReadAsync($stdout, 0, $stdout.Length)
                }
            }
            if ($null -ne $errorTask -and $errorTask.IsCompleted) {
                $count = $errorTask.GetAwaiter().GetResult()
                $total += $count
                if ($total -gt 1048576) { throw 'Docker output exceeded the limit.' }
                if ($count -eq 0) { $errorTask = $null } else {
                    # Provider stderr may contain sensitive material; never echo it.
                    $errorTask = $process.StandardError.ReadAsync($stderr, 0, $stderr.Length)
                }
            }
            if ($null -ne $outputTask -or $null -ne $errorTask) { [System.Threading.Thread]::Sleep(10) }
        }
        if (-not $process.WaitForExit(1000) -or $process.ExitCode -ne 0) { throw 'Docker did not complete successfully.' }
        if ($Capture) { return $output.ToString() }
    } finally {
        if ($process.Id -and -not $process.HasExited) { $process.Kill($true) }
        $process.Dispose()
    }
}

function Read-DockerJson([string]$Text) {
    # Reject ambiguous duplicate keys as well as non-object/trailing JSON.
    $document = [System.Text.Json.JsonDocument]::Parse($Text)
    try {
        if ($document.RootElement.ValueKind -ne [System.Text.Json.JsonValueKind]::Object) { throw 'Expected an object.' }
        Assert-UniqueJsonProperties $document.RootElement
        return ConvertFrom-Json -InputObject $Text -AsHashtable -Depth 64
    } finally { $document.Dispose() }
}

function Assert-UniqueJsonProperties([System.Text.Json.JsonElement]$Element) {
    if ($Element.ValueKind -eq [System.Text.Json.JsonValueKind]::Object) {
        $names = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
        foreach ($property in $Element.EnumerateObject()) {
            if (-not $names.Add($property.Name)) { throw 'Duplicate JSON property.' }
            Assert-UniqueJsonProperties $property.Value
        }
    } elseif ($Element.ValueKind -eq [System.Text.Json.JsonValueKind]::Array) {
        foreach ($item in $Element.EnumerateArray()) { Assert-UniqueJsonProperties $item }
    }
}

function Assert-LocalIdentity([string]$Reference) {
    $identity = Read-DockerJson (Invoke-Docker -Arguments @('image', 'inspect', '--format', $identityFormat, $Reference) -Capture)
    if ($identity.id -isnot [string] -or $identity.os -isnot [string] -or $identity.architecture -isnot [string] -or
        $identity.id -cne $ImageId -or $identity.os -cne 'linux' -or $identity.architecture -cne 'amd64') {
        throw 'Local image identity or platform did not match.'
    }
}

function Assert-ImageManifest($Manifest) {
    if ($Manifest.schemaVersion -isnot [long] -or $Manifest.schemaVersion -ne 2 -or $Manifest.mediaType -isnot [string] -or
        $Manifest.mediaType -cnotin $manifestTypes -or $Manifest.ContainsKey('manifests')) {
        throw 'Expected one supported image manifest, not an image index.'
    }
}

try {
    if ($ImageId -cnotmatch $digestPattern) { throw 'ImageId must be a full immutable sha256 image ID.' }
    if ($Destination -cnotin @('Local', 'DockerHub', 'GHCR', 'ACR')) { throw 'Choose an explicit destination.' }
    $target = $null
    if ($Destination -ceq 'Local') {
        if ($PSBoundParameters.ContainsKey('Repository') -or $PSBoundParameters.ContainsKey('Tag')) { throw 'Local does not accept registry inputs.' }
    } else {
        # Match Docker distribution's lowercase repository components, with an
        # explicit registry host. Hub/GHCR also require a namespace. Tags are separate.
        $component = '[a-z0-9]+(?:(?:[._]|__|-+)[a-z0-9]+)*'
        if ($Repository.Length -gt 255 -or $Repository -cnotmatch "\A(?<host>[a-z0-9.-]+)/$component(?:/$component)*\z") { throw 'Invalid qualified repository.' }
        $registry = $Matches.host
        $validRegistry = switch ($Destination) {
            'DockerHub' { $registry -ceq 'docker.io' -and $Repository.Split('/').Count -ge 3 }
            'GHCR' { $registry -ceq 'ghcr.io' -and $Repository.Split('/').Count -ge 3 }
            'ACR' { $registry -cmatch '\A[a-z0-9]{5,50}\.azurecr\.io\z' }
        }
        if (-not $validRegistry -or $Tag -cnotmatch '\A[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}\z') { throw 'Destination host or explicit tag is invalid.' }
        $target = "${Repository}:$Tag"
    }

    $stage = 'inspect immutable local image'
    Assert-LocalIdentity $ImageId
    $result = [ordered]@{
        schemaVersion = 1
        status = 'LocalVerified'
        imageId = $ImageId
        destination = $Destination
        targetReference = $target
        immutableReference = $null
        remoteVerified = $false
        runtimePullAuthorized = $false
        azureDeploymentReady = $false
        plan = @('Use this immutable image only with the local Docker daemon; local-only images cannot deploy to remote Azure.')
    }
    if ($Destination -cne 'Local') {
        $result.status = 'Planned'
        $result.plan = @(
            'Prerequisites: PowerShell 7, Docker Linux amd64 image, Docker Buildx, and existing host Docker push plus manifest-read authority for this repository.',
            'Apply tags the explicit immutable local ID and pushes the chosen repository/tag. Existing local and remote tags may be overwritten.',
            'Resolve the remote tag and verify its immutable manifest config digest equals the supplied local image ID. Image indexes are unsupported.',
            'Concurrent writers can cause a partial or incorrect tag publication before a mismatch is detected. Failure never authorizes deployment; no automatic repair or deletion runs.',
            'Publication uses only host Docker identity. Runtime pull identity and Azure deployment authorization require the separate hosting gate (#38).',
            'No registry changes occur without -Apply.'
        )
        if ($Apply) {
            $stage = 'reinspect immutable local image'
            Assert-LocalIdentity $ImageId
            $stage = 'tag selected local image'
            Invoke-Docker -Arguments @('image', 'tag', $ImageId, $target)
            $stage = 'verify local publication tag'
            Assert-LocalIdentity $target
            $stage = 'push selected publication tag'
            $publicationMayHaveChanged = $true
            Invoke-Docker -Arguments @('image', 'push', $target)
            $stage = 'resolve remote publication digest'
            $descriptor = Read-DockerJson (Invoke-Docker -Arguments @('buildx', 'imagetools', 'inspect', $target, '--format', '{{json .Manifest}}') -Capture)
            # Buildx returns an OCI descriptor for a single image. Unlike the
            # raw manifest fetched below, this descriptor has no schemaVersion.
            if ($descriptor.mediaType -isnot [string] -or $descriptor.mediaType -cnotin $manifestTypes -or $descriptor.ContainsKey('manifests')) {
                throw 'Expected one supported image descriptor, not an image index.'
            }
            if ($descriptor.digest -isnot [string] -or $descriptor.digest -cnotmatch $digestPattern) { throw 'Invalid remote digest.' }
            $immutable = "${Repository}@$($descriptor.digest)"
            $stage = 'verify immutable remote image config'
            $manifest = Read-DockerJson (Invoke-Docker -Arguments @('buildx', 'imagetools', 'inspect', $immutable, '--raw') -Capture)
            Assert-ImageManifest $manifest
            if ($manifest.config -isnot [System.Collections.IDictionary] -or $manifest.config.digest -isnot [string] -or $manifest.config.digest -cne $ImageId) { throw 'Remote image config did not match.' }
            $result.status = 'PublishedVerified'
            $result.immutableReference = $immutable
            $result.remoteVerified = $true
        }
    }
    $result | ConvertTo-Json -Depth 6
} catch {
    # Do not include exception/provider output: even a credential helper's error
    # can contain secrets. Failed publication has no deployment-eligible reference.
    [ordered]@{
        schemaVersion = 1
        status = 'Unverified'
        stage = $stage
        publicationMayHaveChanged = $publicationMayHaveChanged
        immutableReference = $null
        remoteVerified = $false
        runtimePullAuthorized = $false
        azureDeploymentReady = $false
    } | ConvertTo-Json
    [Console]::Error.WriteLine("Image publication failed at stage: $stage. Publication is unverified; no deployment is authorized.")
    exit 1
}
