[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Executable,
    [switch]$ExpectNoPawnIO
)

$ErrorActionPreference = 'Stop'
$file = Get-Item -LiteralPath $Executable
$folder = $file.DirectoryName
if (Get-Process AirDesk -ErrorAction SilentlyContinue | Where-Object Path -eq $file.FullName) {
    throw 'The test copy of AirDesk is already running.'
}
foreach ($name in 'Local\AirDesk','Local\AirDesk-0.1') {
    $existing = $null
    if ([Threading.Mutex]::TryOpenExisting($name, [ref]$existing)) {
        $existing.Dispose()
        throw 'Exit the other AirDesk instance before running the desktop test.'
    }
}
if ($ExpectNoPawnIO -and (Test-Path 'HKLM:/SYSTEM/CurrentControlSet/Services/PawnIO')) {
    throw 'This test requires Windows without PawnIO.'
}

function Wait-App([Diagnostics.Process]$Process) {
    if (!$Process.WaitForExit(45000)) {
        $Process.Kill()
        throw 'The monitor-only test copy took too long to exit.'
    }
    if ($Process.ExitCode -ne 0) { throw "AirDesk exited with code $($Process.ExitCode)." }
}

$report = Join-Path $folder 'ui-smoke.json'
$started = (Get-Date).ToUniversalTime()
$app = Start-Process -FilePath $file.FullName -ArgumentList '--ui-smoke','--monitor-only' -WindowStyle Hidden -PassThru
Wait-App $app
if (!(Test-Path $report) -or (Get-Item $report).LastWriteTimeUtc -lt $started) { throw 'No current WPF report.' }
$renderings = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
$scales = ($renderings.dpi | Sort-Object) -join ','
if ($scales -ne '96,120,144,192') { throw "Unexpected WPF render scales $scales." }
foreach ($image in 'dark-100','light-125','dark-150','light-200','cooling','history','settings','tray','osd') {
    if (!(Test-Path -LiteralPath (Join-Path $folder "$image.png"))) { throw "Missing WPF snapshot $image." }
}
$monitoring = Get-Content -LiteralPath (Join-Path $folder 'smoke.json') -Raw | ConvertFrom-Json
if (!$monitoring.WindowVisible -or $monitoring.Cooling.CanEnable -or $monitoring.Cooling.CanCalibrate -or $monitoring.Readings.Count -eq 0) {
    throw 'The window did not start in monitoring mode with readings.'
}
if ($ExpectNoPawnIO -and ($monitoring.Driver.Registration -ne 0 -or $monitoring.Driver.Version)) {
    throw 'Diagnostics did not detect the missing driver.'
}

$tray = Start-Process -FilePath $file.FullName -ArgumentList '--benchmark','--tray','--monitor-only' -WindowStyle Hidden -PassThru
try {
    Start-Sleep -Seconds 4
    $tray.Refresh()
    if ($tray.HasExited -or $tray.MainWindowHandle -ne [IntPtr]::Zero) { throw 'The tray instance started with a visible window.' }
    $soak = Start-Process -FilePath $file.FullName -ArgumentList '--soak' -WindowStyle Hidden -PassThru
    Wait-App $soak
    $tray.Refresh()
    if ($tray.HasExited -or $tray.MainWindowHandle -ne [IntPtr]::Zero) { throw 'The soak command disturbed the active tray instance.' }
    $second = Start-Process -FilePath $file.FullName -ArgumentList '--monitor-only' -WindowStyle Hidden -PassThru
    Wait-App $second
    Wait-App $tray
    $visible = Get-Content -LiteralPath (Join-Path $folder 'benchmark.json') -Raw | ConvertFrom-Json
    if (!$visible.WindowVisible -or $visible.Readings.Count -eq 0) { throw 'Second launch failed to reopen the tray instance.' }
}
finally {
    if (!$tray.HasExited) { $tray.Kill() }
}

'PASS: WPF launch, hide/reopen, tray activation, both themes, 100–200% render and passive OSD.'
