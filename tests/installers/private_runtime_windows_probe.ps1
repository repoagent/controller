param(
    [ValidateSet('private', 'mount')][string]$Kind,
    [string]$ModulePath,
    [string]$Source,
    [string]$StatePath,
    [string]$ContainerName
)
$ErrorActionPreference = 'Stop'
function Emit($Name, $Value) {
    Write-Output ("[DEBUG-issue73-win] " + $Name + '=' + $Value)
}
try {
    if ($Kind -eq 'private') {
        $module = Import-Module -Name $ModulePath -PassThru -Force
        & $module {
            param($Source)
            function Emit($Name, $Value) {
                Write-Output ("[DEBUG-issue73-win] " + $Name + '=' + $Value)
            }
            function Stage($Name, [scriptblock]$Action) {
                try { & $Action | Out-Null; Emit $Name 'pass' }
                catch {
                    Emit $Name 'fail'
                    Emit ($Name + '.exceptionType') $_.Exception.GetType().FullName
                    Emit ($Name + '.line') $_.InvocationInfo.ScriptLineNumber
                }
            }
            $parent = [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($Source))
            Stage 'links' { Assert-NoLinks $Source }
            Stage 'parentAcl' { Assert-PrivateWindowsAcl (Get-Acl -LiteralPath $parent) }
            Stage 'ancestors' { Assert-WindowsAncestors $parent }
            Stage 'fileAcl' { Assert-PrivateWindowsAcl (Get-Acl -LiteralPath $Source) }
            Stage 'handleAcl' {
                $stream = [IO.FileStream]::new($Source, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
                try { Assert-PrivateWindowsAcl ([IO.FileSystemAclExtensions]::GetAccessControl($stream)) }
                finally { $stream.Dispose() }
            }
            Stage 'fullReader' { $bytes = Read-PrivateFileBytes $Source; $null = $bytes.Length }
            $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
            $rootOwner = (Get-Acl -LiteralPath ([IO.Path]::GetPathRoot($parent))).GetOwner([Security.Principal.SecurityIdentifier]).Value
            $dangerous = [Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles -bor
                [Security.AccessControl.FileSystemRights]::ChangePermissions -bor [Security.AccessControl.FileSystemRights]::TakeOwnership
            $ancestor = [IO.Path]::GetDirectoryName($parent)
            $ordinal = 0
            while ($ancestor) {
                $acl = Get-Acl -LiteralPath $ancestor
                foreach ($rule in $acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier])) {
                    $mask = $rule.FileSystemRights -band $dangerous
                    if ($rule.AccessControlType -eq 'Allow' -and $mask -ne 0) {
                        $value = $rule.IdentityReference.Value
                        $category = if ($value -eq $sid) { 'current' }
                            elseif ($value -eq $rootOwner) { 'root-owner' }
                            elseif ($value -eq 'S-1-5-18') { 'SYSTEM' }
                            elseif ($value -eq 'S-1-5-32-544') { 'Administrators' }
                            elseif ($value -eq 'S-1-3-0') { 'CreatorOwner' }
                            else { 'other' }
                        Emit 'ancestorAce' ("ordinal=$ordinal;category=$category;inheritance=$($rule.InheritanceFlags);propagation=$($rule.PropagationFlags);dangerousMask=$([int]$mask)")
                    }
                }
                $ordinal++
                $ancestor = [IO.Path]::GetDirectoryName($ancestor)
            }
        } $Source
    } else {
        $state = Get-Content -Raw -LiteralPath $StatePath | ConvertFrom-Json -AsHashtable
        $existing = $state.containers[$ContainerName]
        $key = $state.settings.keyPath
        $full = [IO.Path]::GetFullPath($key)
        $resolved = (Resolve-Path -LiteralPath $key).ProviderPath
        Emit 'fullEqualsResolved' ($full -ceq $resolved)
        Emit 'keyHasShortNameComponent' ($key -match '(?i)(?:^|[\\/])[^\\/]*~[0-9]+(?:[\\/]|$)')
        $events = @($state.events | Where-Object { $_[0] -eq 'run' -and $_[-1] -eq 'start' })
        $eventMounts = @{}
        if ($events.Count) {
            $last = $events[-1]
            for ($index = 0; $index -lt $last.Count - 1; $index++) {
                if ($last[$index] -eq '--mount') {
                    $parts = @{}
                    foreach ($part in $last[$index + 1].Split(',')) {
                        $pair = $part.Split('=', 2)
                        if ($pair.Count -eq 2) { $parts[$pair[0]] = $pair[1] }
                    }
                    $eventMounts[$parts.dst] = $parts
                }
            }
        }
        $expected = @{}
        foreach ($storage in @('config', 'credentials', 'workspace', 'history', 'state', 'tools')) {
            $destination = if ($storage -eq 'workspace') { '/workspace' } else { "/data/$storage" }
            $expected[$destination] = @{ Type='volume'; Name="$ContainerName-$storage"; RW=($storage -ne 'config') }
        }
        $expected['/run/secrets/github-app.pem'] = @{ Type='bind'; RW=$false }
        $ordinal = 0
        foreach ($mount in $existing.Mounts) {
            $wanted = $expected[$mount.Destination]
            Emit 'mount' ("ordinal=$ordinal;knownDestination=$([bool]$wanted);typeMatches=$($wanted -and $mount.Type -ceq $wanted.Type);rwMatches=$($wanted -and $mount.RW -eq $wanted.RW)")
            if ($wanted -and $wanted.Type -eq 'volume') {
                Emit 'volumeNameMatches' ($mount.Name -ceq $wanted.Name)
            } elseif ($wanted -and $wanted.Type -eq 'bind') {
                Emit 'sourceEqualsOriginalRun' ($mount.Source -ceq $eventMounts[$mount.Destination].src)
                Emit 'sourceEqualsFull' ($mount.Source -ceq $full)
                Emit 'sourceEqualsResolved' ($mount.Source -ceq $resolved)
                Emit 'sourceLength' $mount.Source.Length
                Emit 'fullLength' $full.Length
                Emit 'resolvedLength' $resolved.Length
            }
            $ordinal++
        }
    }
} catch {
    Emit 'probeFailureType' $_.Exception.GetType().FullName
    Emit 'probeFailureLine' $_.InvocationInfo.ScriptLineNumber
    exit 1
}
