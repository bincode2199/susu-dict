param([string]$InputDirectory='docs/evidence/F00/memory-suspend',[int]$LogicalProcessors=8,[string]$Scope='F00 eight-window UI fixture only; excludes plugin host, services and full production state')
$ErrorActionPreference='Stop'
if($LogicalProcessors -le 0){throw 'LogicalProcessors must be positive'}
$samples=@(Get-Content (Join-Path $InputDirectory 'windows-memory-samples.json') -Raw | ConvertFrom-Json | ForEach-Object { $_ })
function At([int]$second){$samples | Sort-Object { [Math]::Abs($_.secondsAfterHidden-$second) } | Select-Object -First 1}
function CpuBetween($first,$last){
    $before=@($first.processes | Sort-Object pid)
    $after=@($last.processes | Sort-Object pid)
    if(($before.pid -join ',') -ne ($after.pid -join ',')){throw 'CPU interval changes process membership; choose a stable interval'}
    if(@($before+$after | Where-Object {$null -eq $_.cpuTicks}).Count){throw 'CPU counters missing'}
    $elapsed=([DateTimeOffset]$last.timestamp-[DateTimeOffset]$first.timestamp).TotalSeconds
    if($elapsed -le 0){throw 'Invalid sample timestamps'}
    $ticks=($after | Measure-Object cpuTicks -Sum).Sum-($before | Measure-Object cpuTicks -Sum).Sum
    if($ticks -lt 0){throw 'CPU counter decreased'}
    [pscustomobject]@{from=$first.secondsAfterHidden;to=$last.secondsAfterHidden;elapsedSeconds=$elapsed;cpuSeconds=$ticks/1e7;percentOneCore=100*$ticks/1e7/$elapsed;percentMachine=100*$ticks/1e7/$elapsed/$LogicalProcessors}
}
$warm=At 300
$idle=At 899
[pscustomobject]@{
    scope=$Scope
    logicalProcessors=$LogicalProcessors
    warm=[pscustomobject]@{secondsAfterHidden=$warm.secondsAfterHidden;privateWorkingSetMiB=$warm.totalPrivateWorkingSet/1MB;privateBytesMiB=$warm.totalPrivateBytes/1MB;processCount=@($warm.processes).Count;withinFixtureMemoryTarget=$warm.totalPrivateWorkingSet -le 60MB}
    idle=[pscustomobject]@{secondsAfterHidden=$idle.secondsAfterHidden;privateWorkingSetMiB=$idle.totalPrivateWorkingSet/1MB;privateBytesMiB=$idle.totalPrivateBytes/1MB;processCount=@($idle.processes).Count;withinFixtureMemoryTarget=$idle.totalPrivateWorkingSet -le 25MB}
    warmCpu=CpuBetween (At 240) $warm
    idleCpu=CpuBetween (At 840) $idle
} | ConvertTo-Json -Depth 6
