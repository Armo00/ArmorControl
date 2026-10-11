[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ArchivePath,
    [string]$Version,
    [string]$DllPath
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Common.ps1')
$paths = Get-ArmorControlBuildPaths -Version $Version
$archivePath = [IO.Path]::GetFullPath($ArchivePath)
$checksum = Get-Content -LiteralPath "$archivePath.sha256" -Raw
$expected = '^(?<hash>[A-Fa-f0-9]{64}) \*' + [regex]::Escape([IO.Path]::GetFileName($archivePath)) + '\s*$'
if ($checksum -notmatch $expected) { throw 'Invalid checksum file or archive name.' }
$expectedHash = $Matches.hash
if ((Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash -ne $expectedHash) { throw 'Archive checksum mismatch.' }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($archivePath)
try {
    $prefix = 'GameData/ArmorControl/'
    $expectedFiles = @('Plugins/ArmorControl.dll', 'settings.cfg', 'README.md', 'README.zh-CN.md', 'CHANGELOG.md') + @(Get-ArmorControlWebFiles)
    $allowedDirectories = @('GameData/', $prefix)
    foreach ($relative in $expectedFiles) {
        $directory = $prefix + $relative
        while ($directory.LastIndexOf('/') -gt 0) {
            $directory = $directory.Substring(0, $directory.LastIndexOf('/'))
            $allowedDirectories += "$directory/"
        }
    }
    $found = @{}
    foreach ($entry in $archive.Entries) {
        $name = $entry.FullName.Replace('\', '/')
        if ($name.EndsWith('/')) {
            if ($name -cnotin $allowedDirectories) { throw "Unexpected archive directory: $name" }
            continue
        }
        if (-not $name.StartsWith($prefix, [StringComparison]::Ordinal)) { throw "Unexpected archive path: $name" }
        $relative = $name.Substring($prefix.Length)
        if ($relative -cnotin $expectedFiles -or $found.ContainsKey($relative)) { throw "Unexpected or duplicate runtime file: $name" }
        $found[$relative] = $entry
    }
    foreach ($relative in $expectedFiles) {
        if (-not $found.ContainsKey($relative)) { throw "Missing runtime file: $relative" }
    }
    $runtimeWebFiles = @(Get-ArmorControlWebFiles | Where-Object { -not $_.EndsWith('/README.md') })
    foreach ($relative in @('settings.cfg') + $runtimeWebFiles + $(if ($DllPath) { @('Plugins/ArmorControl.dll') } else { @() })) {
        $stream = $found[$relative].Open()
        $sha = [Security.Cryptography.SHA256]::Create()
        try { $entryHash = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '') }
        finally { $sha.Dispose(); $stream.Dispose() }
        $source = if ($relative -eq 'settings.cfg') { Join-Path $paths.ProductRoot 'settings.example.cfg' }
            elseif ($relative -eq 'Plugins/ArmorControl.dll') { [IO.Path]::GetFullPath($DllPath) }
            else { Join-Path $paths.ProductRoot $relative }
        if ($entryHash -ne (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash) { throw "Runtime file differs from verified source: $relative" }
    }
}
finally { $archive.Dispose() }
Write-Host "[ArmorControl] PACKAGE PASS: checksum, exact runtime layout, default settings, web catalogs and selected DLL."
