param([string]$Executable = 'artifacts/app-preview/susu.exe', [int]$Cold = 30, [int]$Hot = 30, [string]$Output = 'docs/evidence/F03/per03-hotkey.json')
# PER03 on the real susu.exe (development preview build, input translation available through fixtures):
# real Alt+A key presses reach RegisterHotKey; the host logs HotkeyReceived -> native shell visible and
# HotkeyReceived -> first painted frame of the main window. Cold samples wait for the keep-warm release and
# BrowserProcessExited (--release-after-seconds 3) between presses; hot samples reopen within keep-warm.
# Needs an interactive, rendering desktop. Uses its own data root; nothing in the user profile is touched.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root
Add-Type -Namespace SusuKeys -Name K -MemberDefinition '[DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, System.UIntPtr extra); [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);'
function Press([byte[]]$keys) {
    foreach ($k in $keys) { [SusuKeys.K]::keybd_event($k, 0, 0, [UIntPtr]::Zero) }
    [array]::Reverse($keys)
    foreach ($k in $keys) { [SusuKeys.K]::keybd_event($k, 0, 2, [UIntPtr]::Zero) }
}
if (-not [SusuKeys.K]::SetCursorPos(10, 10)) { throw 'The desktop is not interactive; PER03 needs real input.' }
$data = Join-Path $env:TEMP ('susu-per03-' + [Guid]::NewGuid().ToString('N'))
$process = Start-Process -FilePath (Join-Path $root $Executable) -ArgumentList "--data-root `"$data`" --release-after-seconds 3" -PassThru
$logs = Join-Path $data 'Local/Su-Su/logs'
function Events { if (Test-Path $logs) { @(Get-ChildItem $logs -Filter 'susu-*.jsonl' | Sort-Object Name | ForEach-Object { Get-Content $_.FullName } | ForEach-Object { $_ | ConvertFrom-Json }) } else { @() } }
function WaitFor([scriptblock]$condition, [int]$timeoutMs = 20000) {
    $deadline = [DateTime]::UtcNow.AddMilliseconds($timeoutMs)
    while ([DateTime]::UtcNow -lt $deadline) { if (& $condition) { return $true }; Start-Sleep -Milliseconds 50 }
    return $false
}
function Count([string]$phase) { @(Events | Where-Object { $_.event -eq 'timing' -and $_.phase -eq $phase }).Count }
function Exits { @(Events | Where-Object { $_.event -eq 'shell' -and $_.code -eq 'webview.browser-exited' }).Count }
$samples = [System.Collections.Generic.List[object]]::new()
$errors = [System.Collections.Generic.List[string]]::new()
try {
    if (-not (WaitFor { (Events | Where-Object event -eq 'app.start').Count -gt 0 })) { throw 'app did not start' }
    Start-Sleep -Milliseconds 1500
    $alt = 0x12; $a = 0x41; $esc = 0x1B
    foreach ($kind in @('cold') * $Cold + @('hot') * $Hot) {
        $before = Count 'FirstFrameAfterHotkey'
        $exitsBefore = Exits
        Press @($alt, $a)
        if (-not (WaitFor { (Count 'FirstFrameAfterHotkey') -gt $before })) { $errors.Add("$kind sample $($samples.Count): no first frame"); continue }
        $samples.Add($kind)
        Start-Sleep -Milliseconds 300
        Press @($esc)
        if ($kind -eq 'cold') {
            # Keep-warm release after 3 s, then the browser must exit before the next cold press.
            if (-not (WaitFor { (Exits) -gt $exitsBefore } 30000)) { $errors.Add('browser did not exit after release') }
            Start-Sleep -Milliseconds 500
        } else { Start-Sleep -Milliseconds 700 }
    }
}
finally {
    if (-not $process.HasExited) { Stop-Process -Id $process.Id }
}
$timings = @(Events | Where-Object { $_.event -eq 'timing' -and $_.window -eq 'Main' })
function Stats([double[]]$values) {
    $sorted = @($values | Sort-Object)
    if ($sorted.Count -eq 0) { return $null }
    $q = { param($p) $r = $p * ($sorted.Count - 1); $lo = [Math]::Floor($r); $hi = [Math]::Ceiling($r); [Math]::Round($sorted[$lo] + ($sorted[$hi] - $sorted[$lo]) * ($r - $lo), 1) }
    [pscustomobject]@{ samples = $sorted.Count; p50 = (& $q 0.5); p95 = (& $q 0.95); max = [Math]::Round($sorted[-1], 1) }
}
$frames = @($timings | Where-Object phase -eq 'FirstFrameAfterHotkey' | ForEach-Object { [double]$_.durationMs })
$shells = @($timings | Where-Object phase -eq 'NativeShellAfterHotkey' | ForEach-Object { [double]$_.durationMs })
$result = [pscustomobject]@{
    timestamp = [DateTimeOffset]::UtcNow
    scope = 'PER03 on NativeAOT susu.exe (dev preview): real Alt+A via keybd_event -> RegisterHotKey -> main window. Times from WM_HOTKEY receipt (Stopwatch) on the UI thread.'
    coldNativeShell = Stats ($shells | Select-Object -First $Cold)
    coldFirstFrame = Stats ($frames | Select-Object -First $Cold)
    hotNativeShell = Stats ($shells | Select-Object -Skip $Cold)
    hotFirstFrame = Stats ($frames | Select-Object -Skip $Cold)
    targets = 'hot content visible <=50 ms, cold native feedback <=150 ms, cold first interactive frame <=300 ms (P95)'
    errors = $errors
}
$result | ConvertTo-Json -Depth 5 | Set-Content $Output -Encoding utf8
$result | ConvertTo-Json -Depth 5
Remove-Item $data -Recurse -Force -ErrorAction SilentlyContinue
