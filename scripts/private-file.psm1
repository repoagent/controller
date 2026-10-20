#Requires -Version 7.0
# A private local file reader, not an enrollment schema validator or ACL repair tool.
Set-StrictMode -Version Latest

function Assert-NoLinks([string]$Path) {
    $current = [IO.Path]::GetFullPath($Path)
    while ($current) {
        $entry = Get-Item -LiteralPath $current -Force -ErrorAction Stop
        if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Private file paths cannot traverse links or reparse points.' }
        $parent = [IO.Path]::GetDirectoryName($current)
        if ($parent -eq $current) { break }
        $current = $parent
    }
}

function Assert-PrivateWindowsAcl($Acl) {
    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User
    if ($Acl.GetOwner([Security.Principal.SecurityIdentifier]) -ne $sid -or -not $Acl.AreAccessRulesProtected) {
        throw 'Private file and parent must be owned by the current SID with protected DACLs.'
    }
    foreach ($rule in $Acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier])) {
        if ($rule.AccessControlType -eq 'Allow' -and $rule.IdentityReference -ne $sid) {
            throw 'Private file and parent must grant access only to the current SID.'
        }
    }
}

function Assert-WindowsAncestors([string]$Parent) {
    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $root = [IO.Path]::GetPathRoot($Parent)
    $rootOwner = (Get-Acl -LiteralPath $root).GetOwner([Security.Principal.SecurityIdentifier]).Value
    $trusted = @($sid, $rootOwner, 'S-1-5-18', 'S-1-5-32-544')
    $dangerous = [Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles -bor
        [Security.AccessControl.FileSystemRights]::ChangePermissions -bor [Security.AccessControl.FileSystemRights]::TakeOwnership
    $ancestor = [IO.Path]::GetDirectoryName($Parent)
    while ($ancestor) {
        $acl = Get-Acl -LiteralPath $ancestor
        foreach ($rule in $acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier])) {
            if ($rule.AccessControlType -eq 'Allow' -and $rule.IdentityReference.Value -notin $trusted -and
                ($rule.FileSystemRights -band $dangerous) -ne 0) {
                throw 'Private file path traverses an ancestor controlled by another account.'
            }
        }
        $ancestor = [IO.Path]::GetDirectoryName($ancestor)
    }
}

function Get-UnixIdentity([string]$Path) {
    $result = & /usr/bin/stat -L --format='%u:%a:%d:%i:%h:%F' -- $Path 2>$null
    if ($LASTEXITCODE -ne 0 -or @($result).Count -ne 1) { throw 'Cannot verify private file identity.' }
    return $result
}

function Assert-PrivateUnix([string]$Identity, [string]$Owner, [bool]$Directory) {
    $parts = $Identity.Split(':')
    $mode = [Convert]::ToInt32($parts[1], 8)
    $kind = if ($Directory) { 'directory' } else { 'regular file' }
    if ($parts[0] -ne $Owner -or ($mode -band 0x3f) -ne 0 -or $parts[5] -ne $kind -or (-not $Directory -and $parts[4] -ne '1')) {
        throw 'Private file and parent must be owned by the current user with owner-only access; file must have one link.'
    }
}

function Read-PrivateFileBytes {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Path)
    $stream = $null
    try {
        $full = [IO.Path]::GetFullPath($Path)
        $parent = [IO.Path]::GetDirectoryName($full)
        Assert-NoLinks $full
        if ($IsWindows) {
            Assert-PrivateWindowsAcl (Get-Acl -LiteralPath $parent)
            Assert-WindowsAncestors $parent
            Assert-PrivateWindowsAcl (Get-Acl -LiteralPath $full)
        } elseif ($IsLinux) {
            $owner = & /usr/bin/id -u
            if ($LASTEXITCODE -ne 0) { throw 'Cannot determine effective user.' }
            $before = Get-UnixIdentity $full
            $parentBefore = Get-UnixIdentity $parent
            Assert-PrivateUnix $before $owner $false
            Assert-PrivateUnix $parentBefore $owner $true
            # Ancestors may be shared only when sticky and root-owned (e.g. /tmp).
            $rootOwner = (Get-UnixIdentity '/').Split(':')[0]
            $ancestor = [IO.Path]::GetDirectoryName($parent)
            while ($ancestor) {
                $identity = (Get-UnixIdentity $ancestor).Split(':')
                $mode = [Convert]::ToInt32($identity[1], 8)
                if (($mode -band 0x12) -ne 0 -and -not ($identity[0] -eq $rootOwner -and ($mode -band 0x200) -ne 0)) {
                    throw 'Private file path traverses a writable ancestor.'
                }
                $ancestor = [IO.Path]::GetDirectoryName($ancestor)
            }
        } else { throw 'Private file import supports Windows and Linux PowerShell 7.' }
        $stream = [IO.FileStream]::new($full, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        if ($IsWindows) {
            Assert-PrivateWindowsAcl ([IO.FileSystemAclExtensions]::GetAccessControl($stream))
        } else {
            $handlePath = "/proc/$PID/fd/$($stream.SafeFileHandle.DangerousGetHandle())"
            if ((Get-UnixIdentity $handlePath) -ne $before) { throw 'Private file identity changed while opening.' }
        }
        if ($stream.Length -lt 1 -or $stream.Length -gt 65536) { throw 'Private file must contain 1..65536 bytes.' }
        $buffer = [byte[]]::new(65537)
        $length = 0
        while ($length -lt $buffer.Length) {
            $count = $stream.Read($buffer, $length, $buffer.Length - $length)
            if ($count -eq 0) { break }
            $length += $count
        }
        if ($length -lt 1 -or $length -gt 65536) { throw 'Private file must contain 1..65536 bytes.' }
        Assert-NoLinks $full
        if ($IsLinux) {
            if ((Get-UnixIdentity $full) -ne $before -or (Get-UnixIdentity $parent) -ne $parentBefore) { throw 'Private file identity changed while reading.' }
        } else {
            Assert-PrivateWindowsAcl (Get-Acl -LiteralPath $parent)
            Assert-WindowsAncestors $parent
            Assert-PrivateWindowsAcl ([IO.FileSystemAclExtensions]::GetAccessControl($stream))
        }
        $bytes = [byte[]]::new($length)
        [Array]::Copy($buffer, $bytes, $length)
        return ,$bytes
    } catch {
        # Do not echo paths, file content or platform exception data into transcripts.
        throw 'Cannot read private enrollment: require a regular private file and private parent owned by the current user, with no links, and 1..65536 bytes.'
    } finally {
        if ($stream) { $stream.Dispose() }
    }
}
Export-ModuleMember -Function Read-PrivateFileBytes
