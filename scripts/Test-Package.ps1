[CmdletBinding()]
param(
    [string]$Directory = 'dist',
    [string]$Commit = (git rev-parse HEAD)
)

$ErrorActionPreference = 'Stop'
$metadata = @(Get-ChildItem -LiteralPath $Directory -File -Filter 'AirDesk-*-SHA256.txt')
if ($metadata.Count -ne 1) { throw 'Expected one AirDesk checksum manifest.' }
$listed = @(Get-Content -LiteralPath $metadata[0].FullName)
$checked = foreach ($entry in $listed) {
    if ($entry -notmatch '^(?<hash>[0-9a-f]{64})  (?<file>AirDesk-\d+\.\d+\.\d+-(?:Portable-win-x64\.zip|Setup-win-x64\.exe))$') {
        throw "Invalid checksum entry: $entry"
    }
    $expected = $Matches.hash
    $item = Get-Item -LiteralPath (Join-Path $Directory $Matches.file)
    if ((Get-FileHash -LiteralPath $item.FullName).Hash -ne $expected) { throw "SHA-256 mismatch: $($item.Name)" }
    $item
}
$portable = @($checked | Where-Object Extension -eq '.zip')
if ($portable.Count -ne 1) { throw 'Expected one portable package in the checksum manifest.' }
$version = [regex]::Match($portable[0].Name, '^AirDesk-(\d+\.\d+\.\d+)').Groups[1].Value
$base = "AirDesk-$version/"
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($portable[0].FullName)
try {
    foreach ($item in 'AirDesk.exe','AirDesk.Core.dll','AirDesk.Windows.dll','AirDesk.deps.json','BUILD.txt','README.md','LICENSE',
        'THIRD-PARTY-NOTICES.md','licenses/NET-Runtime-THIRD-PARTY-NOTICES.txt') {
        if ($null -eq $archive.GetEntry($base + $item)) { throw "Missing $item" }
    }
    foreach ($entry in $archive.Entries) {
        if (!$entry.FullName.StartsWith($base, 'Ordinal') -or $entry.FullName -match '(?:^|/)\.\.(?:/|$)' -or
            $entry.Name -match '\.(pdb|dmp|sys|pfx|pem|key|csv)$|^(soak\.jsonl|monitor-preferences-.*|history-v.*|fan-binding-.*|sensors\.json|smoke\.json|benchmark\.json|.*\.log)$') {
            throw "Unexpected package entry: $($entry.FullName)"
        }
    }
    $reader = [IO.StreamReader]::new($archive.GetEntry($base + 'BUILD.txt').Open())
    try {
        $build = $reader.ReadToEnd() -split '\r?\n'
        if ($build -notcontains "Commit: $Commit" -or $build -notcontains "Version: $version") {
            throw 'Invalid version or commit in BUILD.txt.'
        }
    }
    finally { $reader.Dispose() }
}
finally { $archive.Dispose() }
'PASS: package hashes, metadata, dependencies and exclusion of private files.'
