Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
. (Join-Path $repo 'Tools/Common.ps1')
function Check([bool]$Condition, [string]$Label) {
    if (-not $Condition) { throw "Tooling regression: $Label" }
}
$paths = Get-ArmorControlBuildPaths
Check ($paths.ProductRoot -eq $repo) 'repository path independent of working directory'
[xml]$project = Get-Content -LiteralPath $paths.SourceProject -Raw
Check ($paths.Version -eq [string]$project.Project.PropertyGroup.Version) 'default version matches project'
Check ((Get-ArmorControlBuildPaths -Version '9.8.7').Version -eq '9.8.7') 'explicit version override'
Check ($paths.ArtifactsRoot -notmatch '(?i)(^|[\\/])GameData([\\/]|$)') 'safe default output'
$second = Get-ArmorControlBuildPaths
Check ($second.ArtifactsRoot -eq $paths.ArtifactsRoot) 'stable checkout-specific output'
foreach ($unsafe in @((Join-Path $repo 'Artifacts'), (Join-Path ([IO.Path]::GetTempPath()) 'gAmEdAtA/test'))) {
    # The first case is unsafe only for a checkout inside GameData.
    if ($unsafe -notmatch '(?i)(^|[\\/])GameData([\\/]|$)') { continue }
    $rejected = $false
    try { $null = Get-ArmorControlBuildPaths -ArtifactsDirectory $unsafe } catch { $rejected = $true }
    Check $rejected 'reject GameData artifact paths'
}
foreach ($version in @('../0.1.1', 'v0.1.1', '0.1.1;bad')) {
    $rejected = $false
    try { $null = Get-ArmorControlBuildPaths -Version $version } catch { $rejected = $true }
    Check $rejected 'reject unsafe version filenames'
}
foreach ($script in Get-ChildItem -LiteralPath (Join-Path $repo 'Tools') -Filter '*.ps1' -File) {
    $tokens = $null
    $parseErrors = $null
    $null = [Management.Automation.Language.Parser]::ParseFile($script.FullName, [ref]$tokens, [ref]$parseErrors)
    Check ($parseErrors.Count -eq 0) "PowerShell syntax: $($script.Name)"
}
foreach ($webFile in (Get-ArmorControlWebFiles)) {
    Check (Test-Path -LiteralPath (Join-Path $repo $webFile) -PathType Leaf) "packaged web allowlist: $webFile"
}
Write-Host 'PASS tooling: portable paths, default version, GameData/version guards, script syntax and runtime allowlist'
