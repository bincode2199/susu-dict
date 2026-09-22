param([ValidateSet('baseline','suspend','low')][string]$MemoryMode='baseline')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root
$evidence = Join-Path $root "docs/evidence/F00/memory-$MemoryMode"
New-Item -ItemType Directory -Force $evidence | Out-Null
$run = Join-Path $root ('artifacts/window-measure-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $run | Out-Null
$out = Join-Path $run 'events.jsonl'
$err = Join-Path $run 'stderr.txt'
$process = Start-Process -FilePath "$root/artifacts/probes/Susu.Probes.exe" -ArgumentList '--measure-windows',"$root/ui/dist",$MemoryMode -WorkingDirectory $root -WindowStyle Hidden -RedirectStandardOutput $out -RedirectStandardError $err -PassThru
$processId = $process.Id
Write-Output "Measurement PID $processId, events $out"
$rows = [System.Collections.Generic.List[object]]::new()
function Read-TreeSample([double]$seconds) {
    $all = @(Get-CimInstance Win32_Process | Select-Object ProcessId,ParentProcessId,Name)
    $ids = [System.Collections.Generic.HashSet[uint32]]::new()
    [void]$ids.Add([uint32]$processId)
    do {
        $added = $false
        foreach ($item in $all) { if ($ids.Contains([uint32]$item.ParentProcessId) -and $ids.Add([uint32]$item.ProcessId)) { $added = $true } }
    } while ($added)
    $counters = @(Get-CimInstance Win32_PerfFormattedData_PerfProc_Process | Where-Object { $ids.Contains([uint32]$_.IDProcess) })
    $members = @($counters | ForEach-Object {
        $cpu = try { (Get-Process -Id $_.IDProcess).TotalProcessorTime.Ticks } catch { $null }
        [pscustomobject]@{ pid=$_.IDProcess; name=$_.Name; privateWorkingSet=[long]$_.WorkingSetPrivate; privateBytes=[long]$_.PrivateBytes; cpuTicks=$cpu }
    })
    if (-not ($members | Where-Object { $_.pid -eq $processId })) { throw 'Main process missing from performance counters' }
    $sample = [pscustomobject]@{ timestamp=[DateTimeOffset]::UtcNow; secondsAfterHidden=$seconds; processes=$members; totalPrivateWorkingSet=[long](($members | Measure-Object privateWorkingSet -Sum).Sum); totalPrivateBytes=[long](($members | Measure-Object privateBytes -Sum).Sum) }
    $rows.Add($sample)
    $rows | ConvertTo-Json -Depth 8 | Set-Content "$evidence/windows-memory-samples.json"
    Write-Output "Sample $seconds seconds: $($members.Count) processes, $([Math]::Round($sample.totalPrivateWorkingSet/1MB,2)) MiB private working set"
}
try {
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(90)
    do {
        if ($process.HasExited) { throw "Measurement exited before readiness: $($process.ExitCode). See $err" }
        $events = if (Test-Path $out) { @(Get-Content $out | ForEach-Object { $_ | ConvertFrom-Json }) } else { @() }
        $hidden = $events | Where-Object event -eq 'all-hidden' | Select-Object -First 1
        if (-not $hidden) { Start-Sleep -Milliseconds 200 }
    } while (-not $hidden -and [DateTimeOffset]::UtcNow -lt $deadline)
    if (-not $hidden) { throw 'All-window readiness deadline exceeded' }
    foreach ($seconds in @(0,60,240,300,540,600,660,840,899)) {
        $targetTick = [long]$hidden.tick + $seconds * 1000
        while ([Environment]::TickCount64 -lt $targetTick) {
            if ($process.HasExited) { throw "Measurement exited early: $($process.ExitCode)" }
            Start-Sleep -Milliseconds 200
        }
        Read-TreeSample (([Environment]::TickCount64 - [long]$hidden.tick)/1000.0)
    }
    if (-not $process.WaitForExit(15000)) { throw 'Measurement did not exit within its bounded lifecycle' }
    if ($process.ExitCode -ne 0) { throw "Measurement failed: $($process.ExitCode)" }
    Copy-Item $out "$evidence/windows-lifecycle.jsonl"
    Write-Output 'Measurement completed. Raw results do not imply target budgets passed.'
}
finally {
    if (-not $process.HasExited) { Stop-Process -Id $process.Id; $process.WaitForExit() }
    if (Test-Path $out) { Copy-Item $out "$evidence/windows-lifecycle.jsonl" }
    if (Test-Path $err) { Copy-Item $err "$evidence/windows-measure-errors.txt" }
    $process.Dispose()
}
