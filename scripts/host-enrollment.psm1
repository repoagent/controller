#Requires -Version 7.4
# Host-only boundaries. No host GitHub token is ever extracted from gh.
Set-StrictMode -Version Latest
# File contents are flushed before rename; the directory/rename itself is also
# flushed before the host may acknowledge a credential to the broker.
if (-not ('RepoAgent.HostPersistence' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Runtime.InteropServices;
namespace RepoAgent {
    public static class HostPersistence {
        [DllImport("libc", SetLastError = true)] static extern int open(string path, int flags);
        [DllImport("libc", SetLastError = true)] static extern int fsync(int fd);
        [DllImport("libc", SetLastError = true)] static extern int close(int fd);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool MoveFileEx(string source, string target, int flags);
        public static void Commit(string temporary, string target) {
            if (OperatingSystem.IsWindows()) {
                if (!MoveFileEx(temporary, target, 1 | 8)) throw new IOException("Private rename failed.");
            } else if (OperatingSystem.IsLinux()) {
                File.Move(temporary, target, true);
                int fd = open(Path.GetDirectoryName(target)!, 0x10000);
                if (fd < 0) throw new IOException("Private directory flush failed.");
                try { if (fsync(fd) != 0) throw new IOException("Private directory flush failed."); }
                finally { close(fd); }
            } else { throw new PlatformNotSupportedException(); }
        }
    }
}
'@
}

function Read-HostJson([string]$Text) {
    if ([Text.Encoding]::UTF8.GetByteCount($Text) -gt 65536) { throw 'Document exceeds 64 KiB.' }
    $document = [Text.Json.JsonDocument]::Parse($Text, [Text.Json.JsonDocumentOptions]@{ MaxDepth = 8 })
    try {
        function Test-Object($Element) {
            if ($Element.ValueKind -eq [Text.Json.JsonValueKind]::Object) {
                $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
                foreach ($p in $Element.EnumerateObject()) {
                    if (-not $names.Add($p.Name)) { throw 'Duplicate document field.' }
                    Test-Object $p.Value
                }
            } elseif ($Element.ValueKind -eq [Text.Json.JsonValueKind]::Array) {
                foreach ($item in $Element.EnumerateArray()) { Test-Object $item }
            }
        }
        if ($document.RootElement.ValueKind -ne [Text.Json.JsonValueKind]::Object) { throw 'Expected JSON object.' }
        Test-Object $document.RootElement
        function Convert-Element($Element) {
            switch ($Element.ValueKind) {
                Object { $result = [ordered]@{}; foreach ($p in $Element.EnumerateObject()) { $result[$p.Name] = Convert-Element $p.Value }; return $result }
                Array { $result = @($Element.EnumerateArray() | ForEach-Object { Convert-Element $_ }); return ,$result }
                String { return $Element.GetString() }
                Number { return $Element.GetInt64() }
                True { return $true }
                False { return $false }
                Null { return $null }
                default { throw 'Invalid JSON value.' }
            }
        }
        return Convert-Element $document.RootElement
    } finally { $document.Dispose() }
}
function Assert-Fields($Value, [string[]]$Fields) {
    if ($Value -isnot [Collections.IDictionary] -or $Value.Count -ne $Fields.Count) { throw 'Unexpected document fields.' }
    foreach ($key in $Value.Keys) { if ($Fields -cnotcontains $key) { throw 'Unexpected document field.' } }
}
function Assert-Text($Value, [string]$Pattern) {
    if ($Value -isnot [string] -or $Value -cnotmatch $Pattern) { throw 'Invalid string binding.' }
}
function Assert-Id($Value) {
    if (($Value -isnot [long] -and $Value -isnot [int]) -or $Value -le 0) { throw 'Invalid numeric identity.' }
}
function Get-ApprovalStateKind($State) {
    Assert-Id $State.version
    if ($State.version -eq 1) {
        Assert-Fields $State @('version','repository','repositoryId','brokerOrigin','repoEnvironment','environmentId','approvalVariable','profile','pendingId','deadline','marker','expectedValueSha256','approvalWriteAttempted','approvalObserved')
        Assert-Id $State.environmentId
        Assert-Text $State.repoEnvironment '\A[^\p{Cc}]{1,255}\z'
        Assert-Text $State.profile '\A(?:polling|webhook)\z'
        $kind = 'environment-variable'
    } elseif ($State.version -eq 2) {
        Assert-Fields $State @('version','setupVersion','approvalKind','repository','repositoryId','brokerOrigin','approvalVariable','profile','pendingId','deadline','marker','expectedValueSha256','approvalWriteAttempted','approvalObserved')
        Assert-Id $State.setupVersion
        if ($State.setupVersion -ne 2 -or $State.approvalKind -cne 'repository-agent-variable') { throw 'Unsupported approval state binding.' }
        $kind = 'repository-agent-variable'
    } else { throw 'Unsupported approval state version.' }
    Assert-Id $State.repositoryId
    Assert-Text $State.repository '\A[A-Za-z0-9_.-]{1,100}/[A-Za-z0-9_.-]{1,100}\z'
    Assert-Text $State.profile '\A(?:polling|webhook|polling-clone|webhook-clone)\z'
    Assert-Text $State.approvalVariable '\A[A-Z_][A-Z0-9_]{0,99}\z'
    $null = Get-BrokerOrigin $State.brokerOrigin
    if ($State.approvalWriteAttempted -isnot [bool] -or $State.approvalObserved -isnot [bool]) { throw 'Invalid approval progress flags.' }
    foreach ($field in @('pendingId','deadline','marker','expectedValueSha256')) {
        if ($null -ne $State[$field] -and $State[$field] -isnot [string]) { throw 'Invalid approval state field.' }
    }
    if ($State.marker) {
        Assert-Text $State.expectedValueSha256 '\A[0-9a-f]{64}\z'
        if ((Get-ValueHash $State.marker) -cne $State.expectedValueSha256) { throw 'Saved marker fingerprint differs.' }
    } elseif ($State.expectedValueSha256 -or $State.approvalObserved -or $State.approvalWriteAttempted) { throw 'Missing saved marker.' }
    return $kind
}
function Get-BrokerOrigin([string]$Value) {
    if ($Value -cnotmatch '\Ahttps://[A-Za-z0-9.-]+(?::[0-9]+)?/?\z') { throw 'Supply a canonical HTTPS broker origin.' }
    $uri = [Uri]$Value
    if ($uri.HostNameType -ne [UriHostNameType]::Dns -or $uri.UserInfo -or $uri.Query -or $uri.Fragment -or $uri.AbsolutePath -ne '/') { throw 'Invalid broker origin.' }
    return $uri.GetLeftPart([UriPartial]::Authority)
}
function Get-ValueHash([string]$Value) {
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($Value))).ToLowerInvariant()
}
function Assert-NoLink([string]$Path) {
    $item = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Links are not permitted for enrollment state.' }
}
function Assert-PrivatePath([string]$Path, [switch]$Directory) {
    Assert-NoLink $Path
    if ($IsWindows) {
        $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User
        $acl = Get-Acl -LiteralPath $Path
        if ($acl.GetOwner([Security.Principal.SecurityIdentifier]).Value -ne $sid.Value -or -not $acl.AreAccessRulesProtected) { throw 'Enrollment state must be owned privately by the current user.' }
        $rules = $acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier])
        if ($rules.Count -ne 1 -or $rules[0].IdentityReference.Value -ne $sid.Value -or $rules[0].AccessControlType -ne 'Allow' -or $rules[0].FileSystemRights -ne 'FullControl') { throw 'Enrollment state has unsafe access rules.' }
    } elseif ($IsLinux) {
        $mode = [IO.File]::GetUnixFileMode($Path)
        $expected = if ($Directory) { 448 } else { 384 }
        if ([int]$mode -ne $expected) { throw 'Enrollment state must have directory mode 0700 and file mode 0600.' }
        # stat/id are fixed OS programs; no shell or user-controlled option is used.
        $owner = & /usr/bin/stat -c '%u' -- $Path
        if ($LASTEXITCODE -ne 0) { throw 'Cannot verify enrollment ownership.' }
        $current = & /usr/bin/id -u
        if ($LASTEXITCODE -ne 0 -or $owner -cne $current) { throw 'Enrollment state belongs to another user.' }
    } else { throw 'Host enrollment supports Windows and Linux.' }
}
function Initialize-PrivateDirectory([string]$Path) {
    $Path = [IO.Path]::GetFullPath($Path)
    # Existing ancestors may be shared, but must not redirect the selected path.
    $ancestor = [IO.DirectoryInfo]::new($Path).Parent
    while ($null -ne $ancestor) { if ($ancestor.Exists) { Assert-NoLink $ancestor.FullName }; $ancestor = $ancestor.Parent }
    if (-not [IO.Directory]::Exists($Path)) {
        if (-not [IO.Directory]::Exists([IO.Path]::GetDirectoryName($Path))) { throw 'Create the parent state directory first.' }
        if ($IsWindows) {
            $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User
            $acl = [Security.AccessControl.DirectorySecurity]::new()
            $acl.SetOwner($sid); $acl.SetAccessRuleProtection($true, $false)
            $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
            [IO.FileSystemAclExtensions]::Create([IO.DirectoryInfo]::new($Path), $acl)
        } elseif ($IsLinux) { [IO.Directory]::CreateDirectory($Path, [IO.UnixFileMode]448) | Out-Null }
        else { throw 'Host enrollment supports Windows and Linux.' }
    }
    Assert-PrivatePath $Path -Directory
    return $Path
}
function Set-PrivateFileAcl([string]$Path) {
    if ($IsWindows) {
        $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User
        $acl = [Security.AccessControl.FileSecurity]::new()
        $acl.SetOwner($sid); $acl.SetAccessRuleProtection($true, $false)
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid, 'FullControl', 'Allow'))
        Set-Acl -LiteralPath $Path -AclObject $acl
    }
}
function Write-PrivateJson([string]$Path, $Value) {
    $parent = [IO.Path]::GetDirectoryName($Path)
    Assert-PrivatePath $parent -Directory
    if (Test-Path -LiteralPath $Path) { Assert-PrivatePath $Path }
    $temporary = Join-Path $parent ([Guid]::NewGuid().ToString('N') + '.tmp')
    $options = [IO.FileStreamOptions]::new()
    $options.Mode = [IO.FileMode]::CreateNew; $options.Access = [IO.FileAccess]::Write; $options.Share = [IO.FileShare]::None
    if ($IsLinux) { $options.UnixCreateMode = [IO.UnixFileMode]384 }
    $stream = [IO.FileStream]::new($temporary, $options)
    try {
        Set-PrivateFileAcl $temporary
        Assert-PrivatePath $temporary
        $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($Value | ConvertTo-Json -Depth 8 -Compress))
        $stream.Write($bytes, 0, $bytes.Length); $stream.Flush($true)
    } finally { $stream.Dispose() }
    try { [RepoAgent.HostPersistence]::Commit($temporary, $Path); Assert-PrivatePath $Path }
    finally { if (Test-Path -LiteralPath $temporary) { [IO.File]::Delete($temporary) } }
    $null = Read-PrivateJson $Path
}
function Read-PrivateJson([string]$Path) {
    Assert-PrivatePath ([IO.Path]::GetDirectoryName($Path)) -Directory
    Assert-PrivatePath $Path
    $file = [IO.File]::OpenRead($Path)
    try {
        if ($file.Length -gt 65536) { throw 'Private document exceeds 64 KiB.' }
        $reader = [IO.StreamReader]::new($file, [Text.UTF8Encoding]::new($false, $true))
        try { return Read-HostJson $reader.ReadToEnd() } finally { $reader.Dispose() }
    } finally { $file.Dispose() }
}

function Invoke-HostGitHub([string]$Method, [string]$Endpoint, $Body = $null) {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = (Get-Command gh -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
    $start.UseShellExecute = $false; $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true; $start.RedirectStandardInput = $true
    foreach ($argument in @('api', '--hostname', 'github.com', '--method', $Method, '--include', '-H', 'Accept: application/vnd.github+json', '-H', 'X-GitHub-Api-Version: 2026-03-10', $Endpoint)) { $start.ArgumentList.Add($argument) }
    if ($null -ne $Body) { $start.ArgumentList.Add('--input'); $start.ArgumentList.Add('-') }
    $start.Environment['GH_PROMPT_DISABLED'] = '1'; $start.Environment['GH_PAGER'] = 'cat'
    $process = [Diagnostics.Process]::new(); $process.StartInfo = $start
    $output = [IO.MemoryStream]::new(); $clock = [Diagnostics.Stopwatch]::StartNew()
    try {
        if (-not $process.Start()) { throw 'GitHub CLI could not start.' }
        if ($null -ne $Body) { $process.StandardInput.Write(($Body | ConvertTo-Json -Depth 8 -Compress)) }
        $process.StandardInput.Close()
        $streams = @($process.StandardOutput.BaseStream, $process.StandardError.BaseStream)
        $buffers = @([byte[]]::new(8192), [byte[]]::new(8192))
        $reads = @($streams[0].ReadAsync($buffers[0], 0, 8192), $streams[1].ReadAsync($buffers[1], 0, 8192)); $counted = 0
        while ($null -ne $reads[0] -or $null -ne $reads[1]) {
            $left = 15000 - $clock.ElapsedMilliseconds
            if ($left -le 0) { throw 'GitHub request timed out; inspect before retrying.' }
            $slots = @(0, 1 | Where-Object { $null -ne $reads[$_] })
            [Threading.Tasks.Task[]]$active = @($slots | ForEach-Object { $reads[$_] })
            $completed = [Threading.Tasks.Task]::WaitAny($active, [int]$left)
            if ($completed -eq -1) { throw 'GitHub request timed out.' }
            $slot = $slots[$completed]; $count = $reads[$slot].GetAwaiter().GetResult()
            if ($count -eq 0) { $reads[$slot] = $null; continue }
            $counted += $count
            if ($counted -gt 262144) { throw 'GitHub response exceeds limit.' }
            if ($slot -eq 0) { $output.Write($buffers[$slot], 0, $count) }
            $reads[$slot] = $streams[$slot].ReadAsync($buffers[$slot], 0, 8192)
        }
        $left = 15000 - $clock.ElapsedMilliseconds
        if ($left -le 0 -or -not $process.WaitForExit([int]$left)) { throw 'GitHub request timed out.' }
        $text = [Text.UTF8Encoding]::new($false, $true).GetString($output.ToArray())
        $statusLine = [regex]::Match($text, '\AHTTP/[0-9.]+\s+(?<status>[0-9]{3})[^\r\n]*\r?\n')
        $separator = [regex]::Match($text, '\r?\n\r?\n')
        if (-not $statusLine.Success -or -not $separator.Success) { throw 'Incomplete GitHub response.' }
        $status = [int]$statusLine.Groups['status'].Value
        if ($process.ExitCode -ne 0 -and $status -ge 200 -and $status -lt 300) { throw 'GitHub process failed.' }
        return @{ Status = $status; Body = $text.Substring($separator.Index + $separator.Length) }
    } finally {
        if ($process.Id -and -not $process.HasExited) { try { $process.Kill($true) } catch { } }
        $process.Dispose(); $output.Dispose()
    }
}
function Invoke-EnrollmentBroker([string]$Origin, [string]$Method, [string]$Path, [string]$Secret, $Body = $null) {
    $origin = Get-BrokerOrigin $Origin
    Assert-Text $Secret '\A[A-Za-z0-9_-]{43,128}\z'
    if ($Path -cnotmatch '\A(?:/v2/enrollments(?:/[A-Za-z0-9_-]+(?:/completion)?)?|/v2/devices/[A-Za-z0-9_-]+/acknowledgment|/v[12]/devices/[A-Za-z0-9_-]+)\z') { throw 'Invalid broker route.' }
    $operation = $Method + ' ' + $Path
    if ($operation -cnotmatch '\A(?:POST /v2/enrollments|GET /v2/enrollments/[A-Za-z0-9_-]+|POST /v2/enrollments/[A-Za-z0-9_-]+/completion|POST /v2/devices/[A-Za-z0-9_-]+/acknowledgment|DELETE /v[12]/devices/[A-Za-z0-9_-]+)\z') { throw 'Invalid broker operation.' }
    $handler = [Net.Http.HttpClientHandler]::new(); $handler.AllowAutoRedirect = $false; $handler.UseCookies = $false
    $client = [Net.Http.HttpClient]::new($handler)
    $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::new($Method), $origin + $Path)
    $request.Headers.Authorization = [Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer', $Secret)
    if ($null -ne $Body) { $request.Content = [Net.Http.StringContent]::new(($Body | ConvertTo-Json -Depth 8 -Compress), [Text.Encoding]::UTF8, 'application/json') }
    $timeout = [Threading.CancellationTokenSource]::new(15000)
    try {
        $response = $client.SendAsync($request, [Net.Http.HttpCompletionOption]::ResponseHeadersRead, $timeout.Token).GetAwaiter().GetResult()
        try {
            $stream = $response.Content.ReadAsStreamAsync($timeout.Token).GetAwaiter().GetResult()
            $buffer = [byte[]]::new(65537); $length = 0
            do {
                $count = $stream.ReadAsync($buffer, $length, $buffer.Length - $length, $timeout.Token).GetAwaiter().GetResult(); $length += $count
                if ($length -gt 65536) { throw 'Broker response exceeds limit.' }
            } while ($count -gt 0)
            return @{ Status = [int]$response.StatusCode; Body = [Text.UTF8Encoding]::new($false, $true).GetString($buffer, 0, $length) }
        } finally { $response.Dispose() }
    } finally { $timeout.Dispose(); $request.Dispose(); $client.Dispose() }
}
Export-ModuleMember -Function *
