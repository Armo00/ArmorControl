Set-StrictMode -Version Latest

function Invoke-Checked([string]$Label, [scriptblock]$Action) {
    Write-Host "[ArmorControl] $Label"
    & $Action
    if ($LASTEXITCODE -ne 0) { throw "$Label failed with exit code $LASTEXITCODE." }
}

function Get-ArmorControlBuildPaths {
    param([string]$KspRoot, [string]$ArtifactsDirectory, [string]$Version)
    $productRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
    $sourceProject = Join-Path $productRoot 'Source/ArmorControl/ArmorControl.csproj'
    if (-not $Version) {
        [xml]$project = Get-Content -LiteralPath $sourceProject -Raw
        $Version = [string]$project.Project.PropertyGroup.Version
    }
    if ($Version -notmatch '^[0-9]+\.[0-9]+(?:\.[0-9]+)?(?:-[A-Za-z0-9.-]+)?$') {
        throw "Invalid release version: $Version"
    }
    if (-not $KspRoot) {
        $candidate = Split-Path -Parent (Split-Path -Parent $productRoot)
        if (Test-Path -LiteralPath (Join-Path $candidate 'KSP_x64_Data/Managed/Assembly-CSharp.dll')) {
            $KspRoot = $candidate
        }
    }
    if ($KspRoot) { $KspRoot = [IO.Path]::GetFullPath($KspRoot) }
    if (-not $ArtifactsDirectory) {
        $sha = [Security.Cryptography.SHA256]::Create()
        try { $id = [BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($productRoot))).Replace('-', '').Substring(0, 12) }
        finally { $sha.Dispose() }
        $ArtifactsDirectory = Join-Path ([IO.Path]::GetTempPath()) "ArmorControl-build-$id"
    }
    $artifacts = [IO.Path]::GetFullPath($ArtifactsDirectory).TrimEnd([char[]]@('\', '/'))
    # KSP recursively loads DLLs in GameData, including test and intermediate DLLs.
    if ($artifacts -match '(?i)(^|[\\/])GameData([\\/]|$)') {
        throw 'ArtifactsDirectory must be outside GameData.'
    }
    $artifacts += [IO.Path]::DirectorySeparatorChar
    [pscustomobject]@{
        ProductRoot = $productRoot
        SourceProject = $sourceProject
        KspRoot = $KspRoot
        Version = $Version
        ArtifactsRoot = $artifacts
        DllPath = Join-Path $artifacts 'ArmorControl/bin/Release/ArmorControl.dll'
    }
}

function Assert-ArmorControlDependencies([string]$KspRoot) {
    if (-not $KspRoot) { throw 'Specify -KspRoot pointing to a KSP 1.12.5 installation with MechJeb 2.14.3 and VesselView.' }
    foreach ($relative in @(
        'KSP_x64_Data/Managed/Assembly-CSharp.dll',
        'KSP_x64_Data/Managed/UnityEngine.dll',
        'KSP_x64_Data/Managed/UnityEngine.CoreModule.dll',
        'KSP_x64_Data/Managed/UnityEngine.AnimationModule.dll',
        'KSP_x64_Data/Managed/UnityEngine.IMGUIModule.dll',
        'KSP_x64_Data/Managed/UnityEngine.ImageConversionModule.dll',
        'GameData/MechJeb2/Plugins/MechJeb2.dll',
        'GameData/VesselView/Plugins/VesselView.dll'
    )) {
        if (-not (Test-Path -LiteralPath (Join-Path $KspRoot $relative) -PathType Leaf)) {
            throw "Missing build dependency: $relative"
        }
    }
    $mjVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $KspRoot 'GameData/MechJeb2/Plugins/MechJeb2.dll')).FileVersion
    if ($mjVersion -ne '2.14.3.0') { throw "ArmorControl requires MechJeb 2.14.3.0; found $mjVersion." }
}

function Get-ArmorControlWebFiles {
    @(
        'prototype/index.html', 'prototype/app.js', 'prototype/i18n.js',
        'prototype/styles.css', 'prototype/localization.css', 'prototype/trajectory-layout.css',
        'prototype/Localization/en-US.json', 'prototype/Localization/zh-CN.json',
        'prototype/Localization/languages.json', 'prototype/Localization/README.md'
    )
}
