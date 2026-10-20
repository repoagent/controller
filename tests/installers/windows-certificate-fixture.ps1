# Test-only public localhost trust. Requires explicit opt-in and existing admin rights.
param(
    [Parameter(Mandatory)][ValidateSet('add', 'remove')][string]$Operation,
    [Parameter(Mandatory)][string]$DerPath,
    [Parameter(Mandatory)][string]$Thumbprint
)
$ErrorActionPreference = 'Stop'
$script:phase = 'input'
function Write-Phase([string]$Name, [string]$Status = 'enter', [int]$HResult = 0) {
    $script:phase = $Name
    [Console]::Out.WriteLine((@{ operation = $Operation; phase = $Name; status = $Status; hresult = $HResult } | ConvertTo-Json -Compress))
    [Console]::Out.Flush()
}
function Open-Root {
    Write-Phase 'open'
    $store = [System.Security.Cryptography.X509Certificates.X509Store]::new('Root', 'LocalMachine')
    try {
        $store.Open([System.Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite -bor
                    [System.Security.Cryptography.X509Certificates.OpenFlags]::OpenExistingOnly)
        return $store
    } catch { $store.Dispose(); throw }
}
function Close-Root($Store) {
    $previous = $script:phase
    Write-Phase 'close'
    $Store.Dispose()
    Write-Phase 'close' 'ok'
    $script:phase = $previous
}
function Inspect-Owned($Store, [bool]$Remove) {
    Write-Phase 'find'
    $certificates = $Store.Certificates
    $count = 0
    try {
        # Validate every selected object before deleting any object.
        foreach ($candidate in $certificates) {
            if ($candidate.Thumbprint -eq $Thumbprint) {
                if ([Convert]::ToBase64String($candidate.RawData) -cne $script:expectedDer) {
                    throw [System.InvalidOperationException]::new('Certificate ownership mismatch')
                }
                $count++
            }
        }
        if ($Remove) {
            foreach ($candidate in $certificates) {
                if ($candidate.Thumbprint -eq $Thumbprint) {
                    Write-Phase 'remove'
                    $Store.Remove($candidate)
                    Write-Phase 'remove' 'ok'
                }
            }
        }
        return $count
    } finally { foreach ($candidate in $certificates) { $candidate.Dispose() } }
}
$certificate = $null
try {
    Write-Phase 'input'
    if (-not $IsWindows -or $env:REPOAGENT_TEST_WINDOWS_ROOT -cne 'LocalMachine') {
        throw [System.InvalidOperationException]::new('Explicit Windows test Root opt-in required')
    }
    Write-Phase 'admin'
    $identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
    try {
        $principal = [System.Security.Principal.WindowsPrincipal]::new($identity)
        if (-not $principal.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)) {
            throw [System.UnauthorizedAccessException]::new('Existing administrator rights required')
        }
    } finally { $identity.Dispose() }
    Write-Phase 'admin' 'ok'
    $der = [IO.File]::ReadAllBytes($DerPath)
    if ($der.Length -eq 0 -or $der.Length -gt 65536 -or $Thumbprint -notmatch '\A[0-9a-fA-F]{40}\z') {
        throw [System.ArgumentException]::new('Invalid public certificate input')
    }
    $certificate = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($der)
    if ($certificate.HasPrivateKey -or $certificate.Thumbprint -ne $Thumbprint) {
        throw [System.ArgumentException]::new('Public certificate identity mismatch')
    }
    $script:expectedDer = [Convert]::ToBase64String($der)
    $store = Open-Root
    try {
        $count = Inspect-Owned $store ($Operation -eq 'remove')
        if ($Operation -eq 'add' -and $count -eq 0) {
            Write-Phase 'add'
            $store.Add($certificate)
            Write-Phase 'add' 'ok'
        }
    } finally { Close-Root $store }
    Write-Phase 'reopen'
    $store = Open-Root
    try {
        $count = Inspect-Owned $store $false
        if (($Operation -eq 'add' -and $count -eq 0) -or ($Operation -eq 'remove' -and $count -ne 0)) {
            throw [System.InvalidOperationException]::new('Certificate persistence mismatch')
        }
        Write-Phase $(if ($Operation -eq 'add') { 'present' } else { 'absent' }) 'ok'
    } finally { Close-Root $store }
    Write-Phase 'complete' 'ok'
} catch {
    $failure = $_.Exception
    while ($failure.InnerException) { $failure = $failure.InnerException }
    Write-Phase $script:phase 'error' $failure.HResult
    exit 1
} finally { if ($null -ne $certificate) { $certificate.Dispose() } }
