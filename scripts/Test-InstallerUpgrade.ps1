[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PreviousSetup,
    [Parameter(Mandatory)][string]$CurrentSetup,
    [string]$EvidenceDirectory = [IO.Path]::GetTempPath(),
    [switch]$DesktopSmoke
)

$ErrorActionPreference = 'Stop'
$previous = Get-Item -LiteralPath $PreviousSetup
$current = Get-Item -LiteralPath $CurrentSetup
$evidence = (New-Item -ItemType Directory -Path $EvidenceDirectory -Force).FullName
$fixture = Join-Path $evidence ('AirDesk-upgrade-' + [guid]::NewGuid().ToString('N'))
$destination = Join-Path $fixture 'app'
$executable = Join-Path $destination 'AirDesk.exe'
$uninstallKey = 'HKCU:/Software/Microsoft/Windows/CurrentVersion/Uninstall/{A05B3464-9731-44CA-94A7-902E82617A47}_is1'
$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'AirDesk/AirDesk.lnk'
$runKey = 'Software\Microsoft\Windows\CurrentVersion\Run'
$data = Join-Path $env:LOCALAPPDATA 'AirDesk'
$preferences = Join-Path $data 'monitor-preferences-v1.json'
$history = Join-Path $data 'history-v1.json'
$saved = @()

function Invoke-Setup([string]$Program, [string]$Options, [string]$Phase, [switch]$ExpectBlocked) {
    $log = Join-Path $fixture "$Phase.log"
    $flags = '/SP- /VERYSILENT /SUPPRESSMSGBOXES /NORESTART {0} /LOG="{1}"' -f $Options, $log
    $process = Start-Process -FilePath $Program -ArgumentList $flags -WindowStyle Hidden -PassThru
    try {
        if (!$process.WaitForExit(90000)) {
            $process.Kill($true)
            throw "Setup timed out. See $log"
        }
        if ($ExpectBlocked) {
            if ($process.ExitCode -eq 0 -or !(Select-String -LiteralPath $log -SimpleMatch 'AirDesk is currently running' -Quiet)) {
                throw "Setup did not report a running AirDesk instance. See $log"
            }
        }
        elseif ($process.ExitCode -ne 0) {
            throw "Unexpected exit code $($process.ExitCode) in $Phase. See $log"
        }
    }
    finally { $process.Dispose() }
}

function Get-RegisteredUninstaller([string]$Phase) {
    $registration = Get-ItemProperty -LiteralPath $uninstallKey
    $prefix = [regex]::Escape($destination + [IO.Path]::DirectorySeparatorChar)
    $pattern = '^"' + $prefix + '(?<name>unins[0-9]+\.exe)"$'
    if ($registration.UninstallString -notmatch $pattern) {
        throw "${Phase}: expected a registered Inno uninstaller inside $destination."
    }
    $path = Join-Path $destination $Matches.name
    if (!(Test-Path -LiteralPath $path -PathType Leaf)) { throw "${Phase}: missing registered uninstaller $path." }
    return $path
}

function Test-InstalledVersion($Package, [string]$Phase) {
    $setup = $Package.VersionInfo
    $installed = (Get-Item -LiteralPath $executable).VersionInfo
    if ($installed.FileMajorPart -ne $setup.FileMajorPart -or $installed.FileMinorPart -ne $setup.FileMinorPart -or
        $installed.FileBuildPart -ne $setup.FileBuildPart) {
        throw "${Phase}: installed version $($installed.FileVersionRaw) does not match setup version $($setup.FileVersionRaw)."
    }
    if (!(Test-Path -LiteralPath $shortcut -PathType Leaf)) { throw "${Phase}: missing Start-menu shortcut $shortcut." }
    Get-RegisteredUninstaller $Phase
}

function Get-StartupCommand {
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($runKey)
    if ($null -eq $key) { return }
    try { return ,($key.GetValue('AirDesk', $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)) }
    finally { $key.Dispose() }
}

function Test-Startup($Expected = $null) {
    $actual = Get-StartupCommand
    if ($null -eq $actual -and $null -eq $Expected) { return }
    if ($actual -isnot [string] -or $actual -ne $Expected) { throw 'Unexpected current-user AirDesk startup command.' }
}

function Test-Uninstalled([string]$Uninstaller, [string]$Phase) {
    # Inno's temporary second phase can outlive the original uninstaller process.
    $dataFile = [IO.Path]::ChangeExtension($Uninstaller, '.dat')
    $timer = [Diagnostics.Stopwatch]::StartNew()
    while ((Test-Path -LiteralPath $Uninstaller) -or (Test-Path -LiteralPath $dataFile)) {
        if ($timer.Elapsed.TotalSeconds -ge 15) { throw "${Phase}: uninstaller files were not removed." }
        Start-Sleep -Milliseconds 100
    }
    $paths = $executable, (Join-Path $destination 'AirDesk.dll'), (Join-Path $destination 'AirDesk.Core.dll'),
        (Join-Path $destination 'AirDesk.Windows.dll'), $shortcut, $uninstallKey
    foreach ($path in $paths) {
        if (Test-Path -LiteralPath $path) { throw "${Phase}: uninstaller left an installed item: $path" }
    }
    Test-Startup
}

function Test-SavedData {
    foreach ($item in $saved) {
        if ((Get-FileHash -LiteralPath $item.Path).Hash -ne $item.Hash) { throw "User data changed: $($item.Path)" }
    }
}

if ($previous.VersionInfo.FileVersionRaw -ge $current.VersionInfo.FileVersionRaw) {
    throw 'The upgrade fixture requires an older installer and a newer installer.'
}
if (Test-Path $uninstallKey) { throw 'Use a disposable user without an installed copy of AirDesk.' }
if (Test-Path $shortcut) { throw 'The Start-menu shortcut already exists.' }
if ($null -ne (Get-StartupCommand)) {
    throw 'The current user already has AirDesk in auto-start.'
}
foreach ($existing in $preferences, "$preferences.bak", $history, "$history.bak") {
    if (Test-Path -LiteralPath $existing) { throw 'Use a disposable user without AirDesk settings or history.' }
}

New-Item -ItemType Directory -Path $fixture, $data -Force | Out-Null
try {
    $options = '/LANG=english /DIR="{0}"' -f $destination
    Invoke-Setup $current.FullName $options 'install-current-clean'
    $uninstall = Test-InstalledVersion $current 'install-current-clean'
    Test-Startup
    if ($DesktopSmoke) {
        try { & (Join-Path $PSScriptRoot 'Test-Desktop.ps1') -Executable $executable -ExpectNoPawnIO }
        finally {
            $desktop = (New-Item -ItemType Directory -Path (Join-Path $fixture 'desktop')).FullName
            Get-ChildItem -LiteralPath $destination -File | Where-Object {
                $_.Extension -eq '.png' -or $_.Name -in 'smoke.json', 'ui-smoke.json', 'benchmark.json'
            } | Copy-Item -Destination $desktop
        }
    }
    Invoke-Setup $uninstall '' 'uninstall-current-clean'
    Test-Uninstalled $uninstall 'uninstall-current-clean'

    Invoke-Setup $previous.FullName $options 'install-previous'
    $null = Test-InstalledVersion $previous 'install-previous'
    Test-Startup
    $preferencesFixture = '{"SchemaVersion":2,"Theme":1,"Aliases":{"/cpu":"QA processor"}}'
    $historyFixture = '{"Version":1,"Points":[]}'
    Set-Content -LiteralPath $preferences -Value $preferencesFixture -Encoding utf8
    $saved += Get-FileHash -LiteralPath $preferences
    Set-Content -LiteralPath $history -Value $historyFixture -Encoding utf8
    $saved += Get-FileHash -LiteralPath $history

    $created = $false
    $mutex = [Threading.Mutex]::new($true, 'Local\AirDesk', [ref]$created)
    try {
        if (!$created) { throw 'An AirDesk instance already holds the installer mutex.' }
        Invoke-Setup $current.FullName $options 'blocked-while-running' -ExpectBlocked
    }
    finally {
        if ($created) { $mutex.ReleaseMutex() }
        $mutex.Dispose()
    }
    $null = Test-InstalledVersion $previous 'blocked-while-running'
    Test-Startup
    Test-SavedData

    Invoke-Setup $current.FullName "$options /TASKS=autostart" 'install-update'
    $uninstall = Test-InstalledVersion $current 'install-update'
    Test-Startup ('"{0}" --autostart' -f $executable)
    Test-SavedData
    Invoke-Setup $uninstall '' 'uninstall-update'
    Test-Uninstalled $uninstall 'uninstall-update'
    Test-SavedData
    Invoke-Setup $previous.FullName $options 'rollback'
    $uninstall = Test-InstalledVersion $previous 'rollback'
    Test-Startup
    Test-SavedData
    Invoke-Setup $uninstall '' 'uninstall-rollback'
    Test-Uninstalled $uninstall 'uninstall-rollback'
    Test-SavedData
    'PASS: clean install, running-instance protection, upgrade, startup task, uninstall, rollback and preserved settings/history.'
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
