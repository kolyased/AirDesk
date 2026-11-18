[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PreviousSetup,
    [Parameter(Mandatory)][string]$CurrentSetup
)

$ErrorActionPreference = 'Stop'
$previous = Get-Item -LiteralPath $PreviousSetup
$current = Get-Item -LiteralPath $CurrentSetup
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('AirDesk-upgrade-' + [guid]::NewGuid().ToString('N'))
$destination = Join-Path $fixture 'app'
$uninstall = Join-Path $destination 'unins000.exe'
$uninstallKey = 'HKCU:/Software/Microsoft/Windows/CurrentVersion/Uninstall/{A05B3464-9731-44CA-94A7-902E82617A47}_is1'
$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'AirDesk/AirDesk.lnk'
$runKey = 'HKCU:/Software/Microsoft/Windows/CurrentVersion/Run'
$data = Join-Path $env:LOCALAPPDATA 'AirDesk'
$preferences = Join-Path $data 'monitor-preferences-v1.json'
$history = Join-Path $data 'history-v1.json'

function Invoke-Setup([string]$Program, [string]$Options, [string]$Phase) {
    $log = Join-Path $fixture "$Phase.log"
    $flags = '/SP- /VERYSILENT /SUPPRESSMSGBOXES /NORESTART {0} /LOG="{1}"' -f $Options, $log
    $process = Start-Process -FilePath $Program -ArgumentList $flags -WindowStyle Hidden -PassThru
    if (!$process.WaitForExit(50000)) { throw "Setup timed out. See $log" }
    if ($process.ExitCode -ne 0) { throw "Setup failed at $Phase. See $log" }
}

function Test-InstalledVersion($Package) {
    $setup = $Package.VersionInfo
    $installed = (Get-Item -LiteralPath (Join-Path $destination 'AirDesk.exe')).VersionInfo
    if ($installed.FileMajorPart -ne $setup.FileMajorPart -or $installed.FileMinorPart -ne $setup.FileMinorPart -or
        $installed.FileBuildPart -ne $setup.FileBuildPart -or !(Test-Path $shortcut) -or !(Test-Path $uninstallKey)) {
        throw 'Installed files, version or shortcut do not match the package.'
    }
}

if (Test-Path $uninstallKey) { throw 'Use a disposable user without an installed copy of AirDesk.' }
if (Test-Path $shortcut) { throw 'The Start-menu shortcut already exists.' }
if ((Get-ItemProperty -LiteralPath $runKey -ErrorAction SilentlyContinue).PSObject.Properties['AirDesk']) {
    throw 'The current user already has AirDesk in auto-start.'
}
foreach ($existing in $preferences, "$preferences.bak", $history, "$history.bak") {
    if (Test-Path -LiteralPath $existing) { throw 'Use a disposable user without AirDesk settings or history.' }
}

New-Item -ItemType Directory -Path $fixture, $data -Force | Out-Null
try {
    $options = '/LANG=english /DIR="{0}" /TASKS=""' -f $destination
    Invoke-Setup $previous.FullName $options 'install-previous'
    Test-InstalledVersion $previous
    Set-Content -LiteralPath $preferences -Value '{"SchemaVersion":2,"Theme":1,"Aliases":{"/cpu":"QA processor"}}' -Encoding utf8
    Set-Content -LiteralPath $history -Value '{"Version":1,"Points":[]}' -Encoding utf8
    $saved = @(Get-FileHash -LiteralPath $preferences,$history)
    Invoke-Setup $current.FullName $options 'install-update'
    Test-InstalledVersion $current
    Invoke-Setup $uninstall '' 'uninstall-update'
    if ((Test-Path $destination/AirDesk.exe) -or (Test-Path $shortcut) -or (Test-Path $uninstallKey)) {
        throw 'Uninstall did not remove the application and shortcut.'
    }
    foreach ($item in $saved) {
        if ((Get-FileHash -LiteralPath $item.Path).Hash -ne $item.Hash) { throw "User data changed: $($item.Path)" }
    }
    Invoke-Setup $previous.FullName $options 'rollback'
    Test-InstalledVersion $previous
    Invoke-Setup $uninstall '' 'uninstall-rollback'
    foreach ($item in $saved) {
        if ((Get-FileHash -LiteralPath $item.Path).Hash -ne $item.Hash) { throw "Rollback changed data: $($item.Path)" }
    }
    'PASS: in-place upgrade, preserved settings/history, uninstall and rollback.'
}
finally {
    if ($saved) {
        foreach ($item in $saved) {
            if ((Test-Path -LiteralPath $item.Path) -and (Get-FileHash -LiteralPath $item.Path).Hash -eq $item.Hash) {
                Remove-Item -LiteralPath $item.Path
            }
        }
    }
    "Logs: $fixture"
}
