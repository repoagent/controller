#Requires -Version 5.1
param([string]$Manifest, [string]$DerivedDockerfile, [string[]]$BuildArg = @())
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (-not $Manifest) { $Manifest = Join-Path $root 'image/default.json' }
function Invoke-Docker {
    $ErrorActionPreference = 'Continue'
    & docker @args
    if ($LASTEXITCODE -ne 0) { throw "Docker failed (exit $LASTEXITCODE)." }
}
function Get-InputFile([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf) -or $Path -match '[,\r\n]') { throw 'Input must be a file; Docker mount paths cannot contain commas or newlines.' }
    return (Resolve-Path -LiteralPath $Path).ProviderPath
}
if ($BuildArg.Count -and -not $DerivedDockerfile) { throw '-BuildArg requires -DerivedDockerfile' }
$Manifest = Get-InputFile $Manifest
$context = Join-Path ([System.IO.Path]::GetTempPath()) ('repoagent-image-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $context | Out-Null
try {
    $mounts = @('--mount', "type=bind,src=$root,dst=/source,readonly", '--mount', "type=bind,src=$Manifest,dst=/input/manifest.json,readonly", '--mount', "type=bind,src=$context,dst=/output")
    $arguments = @('--source', '/source', '--manifest', '/input/manifest.json', '--output', '/output')
    if ($DerivedDockerfile) {
        $DerivedDockerfile = Get-InputFile $DerivedDockerfile
        $mounts += @('--mount', "type=bind,src=$DerivedDockerfile,dst=/input/Derived.Dockerfile,readonly")
        $arguments += @('--derived-dockerfile', '/input/Derived.Dockerfile')
    }
    $helper = 'python@sha256:781449467ffb6f04218f09b1ecdcdc7d22b289ee5da9ec498b024e24ad7a6db7'
    $userArguments = @()
    if ($env:OS -ne 'Windows_NT') { $userArguments = @('--user', ((& id -u) + ':' + (& id -g))) }
    Invoke-Docker run --rm --network none --read-only @userArguments @mounts --entrypoint python3 $helper /source/scripts/compose-image.py @arguments
    $reference = (Get-Content -LiteralPath "$context/base-reference.txt" -Raw).Trim()
    if ($reference.StartsWith('sha256:')) {
        if ((Invoke-Docker image inspect --format '{{.Id}}' $reference) -ne $reference) { throw 'Requested immutable local base is not present' }
        $localBase = 'repoagent-local-base:' + $reference.Substring(7)
        Invoke-Docker tag $reference $localBase
        if ((Invoke-Docker image inspect --format '{{.Id}}' $localBase) -ne $reference) { throw 'Local base identity changed' }
    }
    $image = if ($env:REPOAGENT_IMAGE) { $env:REPOAGENT_IMAGE } else { 'repoagent:local' }
    $target = if ($env:REPOAGENT_TARGET) { $env:REPOAGENT_TARGET } else { 'runtime' }
    if ($target -notin @('runtime', 'dotnet-development', 'live-tests')) { throw 'Unsupported build target' }
    $base = $image
    if ($DerivedDockerfile) { $base = 'repoagent-derived-base:' + (Split-Path $context -Leaf) }
    Invoke-Docker build --platform linux/amd64 --target $target -f "$context/container/Containerfile" -t $base $context
    if ($DerivedDockerfile) {
        $buildArguments = @()
        foreach ($value in $BuildArg) { $buildArguments += @('--build-arg', $value) }
        Invoke-Docker build --platform linux/amd64 --build-arg "REPOAGENT_BASE=$base" @buildArguments -f "$context/Derived.Dockerfile" -t $image $context
    }
    Invoke-Docker image inspect --format '{{.Id}}' $image
    if ($target -ne 'live-tests') { Invoke-Docker run --rm $image versions }
} finally { Remove-Item -LiteralPath $context -Recurse -Force }
