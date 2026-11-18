[CmdletBinding()]
param(
    [string]$OutputDirectory = 'dist',
    [string]$Dotnet = 'dotnet',
    [string]$Iscc,
    [string]$SignTool,
    [ValidatePattern('^[0-9a-fA-F]{40}$')][string]$CertificateThumbprint,
    [string]$TimestampUrl = 'http://timestamp.digicert.com',
    [switch]$Release
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$appProject = Join-Path $root 'src/AirDesk/AirDesk.csproj'
$output = (New-Item -ItemType Directory -Path $OutputDirectory -Force).FullName
$stage = (New-Item -ItemType Directory -Path (Join-Path $output ('.stage-' + [guid]::NewGuid().ToString('N')))).FullName

function Invoke-Checked([string]$Program, [string[]]$Arguments) {
    & $Program @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Program exited with code $LASTEXITCODE." }
}

function Require-Signature([string]$File) {
    $signature = Get-AuthenticodeSignature -LiteralPath $File
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Thumbprint -ne $CertificateThumbprint -or !$signature.TimeStamperCertificate) {
        throw "Missing or mismatched Authenticode signature: $File"
    }
}

try {
    $version = (Invoke-Checked $Dotnet @('msbuild', $appProject, '-nologo', '-getProperty:Version')).Trim()
    if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "Invalid package version: $version" }
    $sha = (Invoke-Checked 'git' @('-C', $root, 'rev-parse', 'HEAD')).Trim()
    $dirty = @(Invoke-Checked 'git' @('-C', $root, 'status', '--porcelain'))
    if ($Release -and ($dirty.Count -or !$SignTool -or !$CertificateThumbprint -or !$Iscc)) {
        throw 'Release packaging requires a clean checkout, Inno Setup, SignTool and the release signing certificate.'
    }
    if ($CertificateThumbprint -and (!$SignTool -or !$Iscc)) {
        throw 'Signing requires SignTool and Inno Setup.'
    }

    $app = Join-Path $stage "AirDesk-$version"
    Invoke-Checked $Dotnet @('publish', $appProject, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
        '-o', $app, '--nologo', '-warnaserror', '-p:DebugType=None', "-p:SourceRevisionId=$sha")
    $executable = Get-Item -LiteralPath (Join-Path $app 'AirDesk.exe')
    if ($executable.VersionInfo.ProductVersion -ne "$version+$sha") { throw 'Packaged executable has incorrect Git version metadata.' }

    $docs = Invoke-Checked 'git' @('-C', $root, 'ls-files', '--', 'docs', 'licenses')
    $files = @('README.md','CHANGELOG.md','CONTRIBUTING.md','LICENSE','THIRD-PARTY-NOTICES.md') + @($docs)
    foreach ($item in $files) {
        $target = Join-Path $app $item
        New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $root $item) -Destination $target
    }

    $nuget = Get-Content -LiteralPath (Join-Path $root 'src/AirDesk/obj/project.assets.json') -Raw | ConvertFrom-Json
    $deps = Get-Content -LiteralPath (Join-Path $app 'AirDesk.deps.json') -Raw | ConvertFrom-Json
    $runtimeLibraries = ($deps.targets.PSObject.Properties[$deps.runtimeTarget.name].Value).PSObject.Properties.Name
    $approved = @('BlackSharp.Core/1.0.7', 'DiskInfoToolkit/1.1.2', 'HidSharp/2.6.4', 'LibreHardwareMonitorLib/0.9.6',
        'Mono.Posix.NETStandard/1.0.0', 'RAMSPDToolkit-NDD/1.4.2', 'System.IO.Ports/10.0.3', 'System.Management/10.0.2')
    $actual = @($runtimeLibraries | Where-Object { $_ -notmatch '^runtimepack\.|^AirDesk(?:\.Core)?/' })
    if (Compare-Object $approved $actual) { throw 'Published dependencies changed. Review their sources and licenses before packaging.' }

    $licenses = Join-Path $app 'licenses'
    function Copy-RuntimeLicense([string]$Pack, [string]$Name, [string]$Target) {
        $match = @($runtimeLibraries | Where-Object { $_.StartsWith("runtimepack.$Pack/", 'OrdinalIgnoreCase') })
        if ($match.Count -ne 1) { throw "Missing .NET runtime pack: $Pack" }
        $relative = ($match[0] -replace '^runtimepack\.', '').ToLowerInvariant()
        foreach ($folder in $nuget.packageFolders.PSObject.Properties.Name) {
            $file = Join-Path (Join-Path $folder $relative) $Name
            if (Test-Path -LiteralPath $file -PathType Leaf) {
                Copy-Item -LiteralPath $file -Destination (Join-Path $licenses $Target)
                return
            }
        }
        throw "Missing license $Name for $Pack"
    }
    Copy-RuntimeLicense 'Microsoft.NETCore.App.Runtime.win-x64' 'LICENSE.TXT' 'NET-Runtime-LICENSE.txt'
    Copy-RuntimeLicense 'Microsoft.NETCore.App.Runtime.win-x64' 'THIRD-PARTY-NOTICES.TXT' 'NET-Runtime-THIRD-PARTY-NOTICES.txt'
    Copy-RuntimeLicense 'Microsoft.WindowsDesktop.App.Runtime.win-x64' 'LICENSE' 'NET-WindowsDesktop-LICENSE.txt'
    $runtimeLibraries | Set-Content -LiteralPath (Join-Path $licenses 'PUBLISHED-RUNTIME.txt') -Encoding utf8

    if ((Get-ChildItem -LiteralPath $app -Recurse -File | Where-Object Name -match '(?i)^pawnio.*\.(sys|exe|inf|cat)$')) {
        throw 'The AirDesk package must not redistribute the PawnIO driver.'
    }
    @("Version: $version", "Commit: $sha", "Configuration: Release/win-x64", "Signed: $([bool]$CertificateThumbprint)",
        "Working tree changes: $([bool]$dirty.Count)") | Set-Content -LiteralPath (Join-Path $app 'BUILD.txt') -Encoding utf8

    if ($CertificateThumbprint) {
        foreach ($file in 'AirDesk.exe','AirDesk.dll','AirDesk.Core.dll') {
            $path = Join-Path $app $file
            Invoke-Checked $SignTool @('sign','/sha1',$CertificateThumbprint,'/fd','SHA256','/tr',$TimestampUrl,'/td','SHA256',$path)
            Require-Signature $path
        }
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = Join-Path $stage "AirDesk-$version-Portable-win-x64.zip"
    [IO.Compression.ZipFile]::CreateFromDirectory($app, $zip, [IO.Compression.CompressionLevel]::Optimal, $true)
    $packages = @($zip)
    if ($Iscc) {
        $arguments = @('/Qp', "/DPublishDir=$app", "/DOutputPath=$stage", "/DAppVersion=$version")
        if ($CertificateThumbprint) {
            $resolvedSignTool = (Get-Command $SignTool -CommandType Application).Source
            $arguments += '/DSignedRelease'
            $arguments += ('/Sairdesk=$q{0}$q sign /sha1 {1} /fd SHA256 /tr $q{2}$q /td SHA256 $f' -f
                $resolvedSignTool, $CertificateThumbprint, $TimestampUrl)
        }
        Invoke-Checked $Iscc ($arguments + (Join-Path $root 'installer/AirDesk.iss'))
        $setup = Join-Path $stage "AirDesk-$version-Setup-win-x64.exe"
        if (!(Test-Path -LiteralPath $setup -PathType Leaf)) { throw 'Installer compiler did not produce the expected file.' }
        if ($CertificateThumbprint) { Require-Signature $setup }
        $packages += $setup
    }
    $packages | ForEach-Object {
        $name = Split-Path -Leaf $_
        '{0}  {1}' -f (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant(), $name
        Move-Item -LiteralPath $_ -Destination (Join-Path $output $name) -Force
    } | Set-Content -LiteralPath (Join-Path $output "AirDesk-$version-SHA256.txt") -Encoding ascii
}
finally {
    $resolvedStage = [IO.Path]::GetFullPath($stage)
    $checkedOutput = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($output)) + [IO.Path]::DirectorySeparatorChar
    if ($resolvedStage.StartsWith($checkedOutput, 'OrdinalIgnoreCase') -and (Split-Path -Leaf $resolvedStage) -match '^\.stage-[0-9a-f]{32}$') {
        Remove-Item -LiteralPath $resolvedStage -Recurse -Force
    }
}
