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

function Wait-App([Diagnostics.Process]$Process, [string]$Phase, [int]$TimeoutMilliseconds = 45000) {
    if (!$Process.WaitForExit($TimeoutMilliseconds)) {
        $Process.Kill()
        throw "The monitor-only process timed out in $Phase."
    }
    if ($Process.ExitCode -ne 0) { throw "AirDesk exited with code $($Process.ExitCode) in $Phase." }
}

$report = Join-Path $folder 'ui-smoke.json'
$started = (Get-Date).ToUniversalTime()
$app = Start-Process -FilePath $file.FullName -ArgumentList '--ui-smoke','--monitor-only','--record-history' -WindowStyle Hidden -PassThru
Wait-App $app 'WPF smoke'
if (!(Test-Path $report) -or (Get-Item $report).LastWriteTimeUtc -lt $started) { throw 'No current WPF report.' }
$renderings = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
$scales = ($renderings.dpi | Sort-Object -Unique) -join ','
if ($scales -ne '96,120,144,192') { throw "Unexpected WPF render scales $scales." }
foreach ($image in 'dark-100','light-125','dark-150','light-200','compact-100','wide-150','live-graphs','cooling','curve-preview','devices','history','settings','tray','osd','fps-settings') {
    if (!(Test-Path -LiteralPath (Join-Path $folder "$image.png"))) { throw "Missing WPF snapshot $image." }
}
$monitoring = Get-Content -LiteralPath (Join-Path $folder 'smoke.json') -Raw | ConvertFrom-Json
if (!$monitoring.WindowVisible -or $monitoring.Cooling.CanEnable -or $monitoring.Cooling.CanCalibrate -or $monitoring.Readings.Count -eq 0) {
    throw 'The window did not start in monitoring mode with readings.'
}
if ($ExpectNoPawnIO -and ($monitoring.Driver.Registration -ne 0 -or $monitoring.Driver.Version)) {
    throw 'Diagnostics did not detect the missing driver.'
}
$historyPath = Join-Path $folder 'benchmark-history/history-v2.json.gz'
if (!(Test-Path -LiteralPath $historyPath)) { throw 'The test copy did not persist the recorded history.' }
$historyFile = [IO.File]::OpenRead($historyPath)
$historyReader = [IO.StreamReader]::new([IO.Compression.GZipStream]::new($historyFile, [IO.Compression.CompressionMode]::Decompress))
try { $journal = $historyReader.ReadToEnd() | ConvertFrom-Json }
finally { $historyReader.Dispose() }
if ($journal.Version -ne 2 -or !($journal.Series | Where-Object { $_.Samples.Count -gt 0 })) {
    throw 'The test copy did not persist the recorded history.'
}

$tray = Start-Process -FilePath $file.FullName -ArgumentList '--benchmark','--tray','--monitor-only' -WindowStyle Hidden -PassThru
try {
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(15)
    $ready = $false
    while (!$tray.HasExited -and [DateTimeOffset]::UtcNow -lt $deadline) {
        $activation = $null
        if ([Threading.EventWaitHandle]::TryOpenExisting('Local\AirDesk.Activate', [ref]$activation)) {
            $activation.Dispose()
            $ready = $true
            break
        }
        Start-Sleep -Milliseconds 150
    }
    if (!$ready) { throw 'The tray instance did not register its activation event.' }
    'PASS: tray registered activation'
    $tray.Refresh()
    if ($tray.HasExited -or $tray.MainWindowHandle -ne [IntPtr]::Zero) { throw 'The tray instance started with a visible window.' }
    $soak = Start-Process -FilePath $file.FullName -ArgumentList '--soak' -WindowStyle Hidden -PassThru
    Wait-App $soak 'rejection of the duplicate soak command' 12000
    $tray.Refresh()
    if ($tray.HasExited -or $tray.MainWindowHandle -ne [IntPtr]::Zero) { throw 'The soak command disturbed the active tray instance.' }
    $second = Start-Process -FilePath $file.FullName -ArgumentList '--monitor-only' -WindowStyle Hidden -PassThru
    Wait-App $second 'activation from a second launch' 12000
    Wait-App $tray 'tray benchmark shutdown'
    $visible = Get-Content -LiteralPath (Join-Path $folder 'benchmark.json') -Raw | ConvertFrom-Json
    if (!$visible.WindowVisible -or $visible.Readings.Count -eq 0) { throw 'Second launch failed to reopen the tray instance.' }
}
finally {
    if (!$tray.HasExited) { $tray.Kill() }
}

'PASS: WPF, tray, history persistence, simulated suspend/resume, both themes, 100–200% render, passive OSD.'
