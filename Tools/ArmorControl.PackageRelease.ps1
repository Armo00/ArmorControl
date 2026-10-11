[CmdletBinding()]
param(
    [string]$KspRoot,
    [string]$ArtifactsDirectory,
    [string]$Version,
    [string]$ReleaseDirectory,
    [switch]$SkipBuild,
    [string]$DllPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Common.ps1')
$paths = Get-ArmorControlBuildPaths -KspRoot $KspRoot -ArtifactsDirectory $ArtifactsDirectory -Version $Version
$Version = $paths.Version
$productRoot = $paths.ProductRoot
if (-not $ReleaseDirectory) { $ReleaseDirectory = Join-Path $paths.ArtifactsRoot 'Releases' }
$releaseRoot = [IO.Path]::GetFullPath($ReleaseDirectory).TrimEnd([char[]]@('\', '/'))
if ($releaseRoot -match '(?i)(^|[\\/])GameData([\\/]|$)') { throw 'ReleaseDirectory must be outside GameData.' }
$archivePath = Join-Path $releaseRoot "ArmorControl-$Version.zip"
$checksumPath = "$archivePath.sha256"
$notesPath = Join-Path $releaseRoot "ArmorControl-$Version-CHANGELOG.md"
foreach ($destination in @($archivePath, $checksumPath, $notesPath)) {
    if (Test-Path -LiteralPath $destination) { throw "Release already exists; refusing to overwrite: $destination" }
}

# Verification never deploys into the game. -SkipBuild still runs the full test gate.
$verifyArgs = @{
    KspRoot = $paths.KspRoot
    ArtifactsDirectory = $paths.ArtifactsRoot
    Version = $Version
    SkipBuild = $SkipBuild
    DllPath = $DllPath
}
& (Join-Path $PSScriptRoot 'ArmorControl.VerifyRelease.ps1') @verifyArgs
$binary = if ($DllPath) { [IO.Path]::GetFullPath($DllPath) } else { $paths.DllPath }

Add-Type -AssemblyName System.IO.Compression.FileSystem
New-Item -ItemType Directory -Path $releaseRoot -Force | Out-Null
$staging = Join-Path $releaseRoot ('.staging-' + [Guid]::NewGuid().ToString('N'))
$payload = Join-Path $staging 'GameData/ArmorControl'
New-Item -ItemType Directory -Path (Join-Path $payload 'Plugins') -Force | Out-Null
try {
    # Explicit runtime allowlist: no source, test harnesses, third-party DLLs or personal settings.
    foreach ($webFile in (Get-ArmorControlWebFiles)) {
        $destination = Join-Path $payload $webFile
        New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $productRoot $webFile) -Destination $destination
    }
    Copy-Item -LiteralPath $binary -Destination (Join-Path $payload 'Plugins/ArmorControl.dll')
    Copy-Item -LiteralPath (Join-Path $productRoot 'settings.example.cfg') -Destination (Join-Path $payload 'settings.cfg')
    foreach ($document in @('README.md', 'README.zh-CN.md', 'CHANGELOG.md')) {
        Copy-Item -LiteralPath (Join-Path $productRoot $document) -Destination $payload
    }
    [IO.Compression.ZipFile]::CreateFromDirectory($staging, $archivePath)
    $hash = Get-FileHash -LiteralPath $archivePath -Algorithm SHA256
    [IO.File]::WriteAllText($checksumPath, "$($hash.Hash) *$([IO.Path]::GetFileName($archivePath))" + [Environment]::NewLine, [Text.Encoding]::ASCII)
    Copy-Item -LiteralPath (Join-Path $productRoot 'CHANGELOG.md') -Destination $notesPath
    & (Join-Path $PSScriptRoot 'ArmorControl.VerifyPackage.ps1') -ArchivePath $archivePath -Version $Version -DllPath $binary
}
finally {
    $checkedStage = [IO.Path]::GetFullPath($staging)
    if ([IO.Path]::GetDirectoryName($checkedStage) -ne $releaseRoot -or [IO.Path]::GetFileName($checkedStage) -notmatch '^\.staging-[a-f0-9]{32}$') {
        throw 'Unsafe staging cleanup path.'
    }
    Remove-Item -LiteralPath $checkedStage -Recurse -Force
}
Write-Host "[ArmorControl] Package ready: $archivePath"
Write-Host "  SHA-256: $($hash.Hash)"
Write-Host "  Notes: $notesPath"
