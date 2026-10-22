#Requires -Version 7.4
function Invoke-LifecycleCommand([string]$Executable, [string[]]$Arguments, [int]$TimeoutSeconds = 60) {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = (Get-Command $Executable -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    if ($IsWindows -and [IO.Path]::GetExtension($start.FileName) -ieq '.cmd') {
        $values = @($start.FileName) + $Arguments
        foreach ($value in $values) {
            if ($value -match '[%!&|<>^"\r\n]') { throw 'Use a CLI installation and state path without shell metacharacters.' }
        }
        $start.FileName = Join-Path ([Environment]::GetFolderPath('System')) 'cmd.exe'
        $start.Arguments = '/d /s /c "' + (($values | ForEach-Object { '"' + $_ + '"' }) -join ' ') + '"'
    } else {
        foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    }
    $start.Environment['AZURE_EXTENSION_USE_DYNAMIC_INSTALL'] = 'no'
    $start.Environment['AZURE_CORE_COLLECT_TELEMETRY'] = 'no'
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    $capture = [IO.MemoryStream]::new()
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $started = $false
    try {
        $started = $process.Start()
        if (-not $started) { throw 'Lifecycle CLI could not start.' }
        $streams = @($process.StandardOutput.BaseStream, $process.StandardError.BaseStream)
        $buffers = @([byte[]]::new(8192), [byte[]]::new(8192))
        $reads = @($streams[0].ReadAsync($buffers[0], 0, 8192), $streams[1].ReadAsync($buffers[1], 0, 8192))
        $counted = 0
        while ($null -ne $reads[0] -or $null -ne $reads[1]) {
            $remaining = $TimeoutSeconds * 1000 - $clock.ElapsedMilliseconds
            if ($remaining -le 0) { throw 'Lifecycle CLI timed out; its remote outcome is unknown. Inspect before retrying.' }
            $slots = @(0, 1 | Where-Object { $null -ne $reads[$_] })
            [Threading.Tasks.Task[]]$active = @($slots | ForEach-Object { $reads[$_] })
            $done = [Threading.Tasks.Task]::WaitAny($active, [int]$remaining)
            if ($done -eq -1) { throw 'Lifecycle CLI timed out; inspect before retrying.' }
            $slot = $slots[$done]
            $count = $reads[$slot].GetAwaiter().GetResult()
            if ($count -eq 0) { $reads[$slot] = $null; continue }
            $counted += $count
            if ($counted -gt 4194304) { throw 'Lifecycle CLI output exceeded its limit; inspect before retrying.' }
            if ($slot -eq 0) { $capture.Write($buffers[$slot], 0, $count) }
            $reads[$slot] = $streams[$slot].ReadAsync($buffers[$slot], 0, 8192)
        }
        $remaining = $TimeoutSeconds * 1000 - $clock.ElapsedMilliseconds
        if ($remaining -le 0 -or -not $process.WaitForExit([int]$remaining)) { throw 'Lifecycle CLI timed out; inspect before retrying.' }
        if ($process.ExitCode -ne 0) { throw 'Lifecycle CLI request failed; inspect the selected resource before retrying. Provider output was withheld.' }
        return [Text.UTF8Encoding]::new($false, $true).GetString($capture.ToArray())
    } finally {
        if ($started -and -not $process.HasExited) { try { $process.Kill($true) } catch { } }
        $process.Dispose()
        $capture.Dispose()
    }
}
Export-ModuleMember -Function Invoke-LifecycleCommand
