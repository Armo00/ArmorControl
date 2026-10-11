[CmdletBinding()]
param(
    [string]$KspRoot,
    [string]$ArtifactsDirectory,
    [string]$Version,
    [switch]$SkipBuild,
    [string]$DllPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Common.ps1')
$paths = Get-ArmorControlBuildPaths -KspRoot $KspRoot -ArtifactsDirectory $ArtifactsDirectory -Version $Version
$productRoot = $paths.ProductRoot
$sourceProject = $paths.SourceProject
$Version = $paths.Version
$addonSourcePath = Join-Path $productRoot 'Source/ArmorControl/ArmorControlAddon.cs'
$trajectoriesBridgePath = Join-Path $productRoot 'Source/ArmorControl/Core/TrajectoriesBridge.cs'
$testRoot = Join-Path $productRoot 'Tests'
$testProject = Join-Path $testRoot 'ArmorControl.TransportTests/ArmorControl.TransportTests.csproj'
if (-not $SkipBuild -and $DllPath) { throw '-DllPath is only supported with -SkipBuild.' }
$dllPath = if ($DllPath) { [IO.Path]::GetFullPath($DllPath) } else { $paths.DllPath }
$htmlPath = Join-Path $productRoot 'prototype/index.html'
$scriptPath = Join-Path $productRoot 'prototype/app.js'
$stylesPath = Join-Path $productRoot 'prototype/styles.css'
$settingsPath = Join-Path $productRoot 'settings.example.cfg'
$buildProperties = @("-p:ArmorControlArtifactsRoot=$($paths.ArtifactsRoot)", "-p:Version=$Version")
& (Join-Path $testRoot 'ArmorControl.ToolingTests.ps1')

if (-not $SkipBuild) {
    Assert-ArmorControlDependencies $paths.KspRoot
    Invoke-Checked 'Release DLL build' { dotnet build $sourceProject -c Release @buildProperties "-p:KspRoot=$($paths.KspRoot)" }
}
foreach ($required in @($dllPath, $htmlPath, $scriptPath, $stylesPath, $settingsPath)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "Required release file is missing: $required" }
}
$expectedVersion = [version](($Version -split '-')[0])
$actualVersion = [version]([Diagnostics.FileVersionInfo]::GetVersionInfo($dllPath).FileVersion)
if ($actualVersion.Major -ne $expectedVersion.Major -or $actualVersion.Minor -ne $expectedVersion.Minor -or
    $actualVersion.Build -ne [Math]::Max(0, $expectedVersion.Build)) {
    throw "DLL version $actualVersion does not match release $Version."
}

$oldTestRoot = [Environment]::GetEnvironmentVariable('ARMORCONTROL_TEST_ROOT', 'Process')
$oldTestDll = [Environment]::GetEnvironmentVariable('ARMORCONTROL_TEST_DLL', 'Process')
try {
    $env:ARMORCONTROL_TEST_ROOT = $productRoot
    $env:ARMORCONTROL_TEST_DLL = $dllPath
    Invoke-Checked 'Transport test build' { dotnet build $testProject -c Release @buildProperties }
    Invoke-Checked 'Transport and protocol regression' {
        dotnet (Join-Path $paths.ArtifactsRoot 'ArmorControl.TransportTests/bin/Release/net8.0/ArmorControl.TransportTests.dll')
    }
    Invoke-Checked 'Ascent test build' {
        dotnet build (Join-Path $testRoot 'ArmorControl.AscentSettingTests/ArmorControl.AscentSettingTests.csproj') -c Release @buildProperties
    }
    Invoke-Checked 'Native ascent setting regression' {
        dotnet (Join-Path $paths.ArtifactsRoot 'ArmorControl.AscentSettingTests/bin/Release/net8.0/ArmorControl.AscentSettingTests.dll')
    }
}
finally {
    [Environment]::SetEnvironmentVariable('ARMORCONTROL_TEST_ROOT', $oldTestRoot, 'Process')
    [Environment]::SetEnvironmentVariable('ARMORCONTROL_TEST_DLL', $oldTestDll, 'Process')
}
Invoke-Checked 'Frontend syntax' { node --check $scriptPath }
Invoke-Checked 'Localization syntax' { node --check (Join-Path $productRoot 'prototype/i18n.js') }
Invoke-Checked 'Localization regression' { node (Join-Path $testRoot 'ArmorControl.LocalizationTests.cjs') }
Invoke-Checked 'Review frontend regression' { node (Join-Path $testRoot 'ArmorControl.ReviewRegression.cjs') }
Invoke-Checked 'Ascent field synchronization regression' { node (Join-Path $testRoot 'ArmorControl.AscentSettingTests.cjs') }

$html = Get-Content -LiteralPath $htmlPath -Raw
$javascript = Get-Content -LiteralPath $scriptPath -Raw
$styles = Get-Content -LiteralPath $stylesPath -Raw
$settings = Get-Content -LiteralPath $settingsPath -Raw
$addonSource = Get-Content -LiteralPath $addonSourcePath -Raw
$trajectoriesBridge = Get-Content -LiteralPath $trajectoriesBridgePath -Raw
foreach ($view in @('flight', 'pannel', 'envelope', 'recorder', 'vessel', 'crew', 'target', 'trajectory', 'planner', 'autopilot', 'control')) {
    if (-not $html.Contains("id=`"view-$view`"")) { throw "Required view is missing: $view" }
}
foreach ($command in @(
    'vessel.control.set', 'vessel.control.release', 'vessel.actionGroup.toggle', 'vessel.stage', 'game.quicksave.load',
    'vessel.part.engine.setActive', 'vessel.part.engine.setThrustLimit', 'vessel.part.gimbal.set',
    'vessel.part.rcs.set', 'vessel.part.light.set', 'vessel.part.gear.set', 'vessel.part.cargo.set',
    'vessel.partGroup.set', 'vessel.view.selectPart', 'vessel.crew.eva',
    'mechjeb.maneuver.create', 'mechjeb.porkchop.solve', 'mechjeb.porkchop.create', 'mechjeb.smartass.set',
    'mechjeb.ascent.start', 'mechjeb.recorder.clear', 'mechjeb.envelope.set',
    'trajectories.settings.set', 'trajectories.profile.set', 'trajectories.target.set', 'trajectories.target.clear', 'trajectories.update',
    'mechjeb.landing.configure', 'mechjeb.landing.target', 'mechjeb.landing.stop', 'mechjeb.docking.configure', 'mechjeb.docking.start', 'mechjeb.docking.stop'
)) {
    if (-not $javascript.Contains($command) -and -not $html.Contains($command) -and -not $addonSource.Contains($command)) {
        throw "Required client command is missing: $command"
    }
}
if ($javascript.Contains('renderDemoTelemetry') -or $javascript.Contains("setConnectionState('demo'")) {
    throw 'Unsafe simulated telemetry fallback is present.'
}
foreach ($placeholder in @('DEVHOST', 'FAST 30 Hz · SLOW 10 Hz · SIM 2 Hz', 'CORE LOX TANK', 'INTERPLANETARY SOLVER / KERBIN → DUNA', '>18 426<')) {
    if ($html.Contains($placeholder) -or $javascript.Contains($placeholder)) {
        throw "Misleading design-time placeholder is present: $placeholder"
    }
}
if (([regex]::Matches($html, '<div class="action-pad">.*?</div>', 'Singleline').Value | Select-String '<button' -AllMatches).Matches.Count -ne 10) {
    throw 'Action group bank must contain exactly 10 buttons.'
}
foreach ($controlId in @('controlJoystick', 'controlRoll', 'controlThrottle')) {
    if (-not $html.Contains("id=`"$controlId`"")) { throw "Required touch control is missing: $controlId" }
}
if (-not $styles.Contains('@media (max-width: 440px)') -or -not $styles.Contains('.axis-layout { grid-template-columns: 1fr; height: auto; }')) {
    throw 'Narrow-screen single-column control layout is missing.'
}
if (-not $styles.Contains('touch-action: none')) { throw 'Touch gesture capture rule is missing.' }
if ((-not $javascript.Contains('automation?.ascentEnabled || automation?.nodeExecutorEnabled || automation?.landingEnabled')) -or
    (-not $javascript.Contains('state.control.throttleLocked ? 0 : state.control.throttle')) -or
    (-not $styles.Contains('.throttle.is-locked'))) {
    throw 'MechJeb burn-autopilot throttle lock is missing from the touch UI.'
}
if ((-not $addonSource.Contains('IsMechJebBurnAutopilotActive(vessel)')) -or
    (-not $addonSource.Contains('state.pitch = Mathf.Clamp(state.pitch + remotePitch, -1f, 1f)')) -or
    (-not $addonSource.Contains('if (IsMechJebBurnAutopilotActive(remoteControlVessel))')) -or
    (-not $addonSource.Contains('remoteThrottle = 0;'))) {
    throw 'Server-side MechJeb throttle arbitration or additive keyboard-style attitude input is missing.'
}
if (-not $html.Contains('data-part-group="rcs"') -or -not $html.Contains('id="partGroupActions"')) {
    throw 'Independent RCS grouping or one-click group controls are missing.'
}
if (-not $javascript.Contains("['rcs', 'cargo', 'gear', 'light']") -or -not $addonSource.Contains('new PartToggleSnapshot("rcs"')) {
    throw 'RCS must remain separate from the engine model.'
}
if (-not $javascript.Contains('bindSlideConfirmation(button, control.querySelector(''.slide-confirm'')') -or -not $javascript.Contains('class="crew-eva confirm-trigger"')) {
    throw 'Per-crew slide-to-confirm EVA control is missing.'
}
if ($settings -notmatch '(?m)^\s*autoStartServer\s*=\s*True\s*$') { throw 'ArmorControl must auto-start by default.' }
if (-not $javascript.Contains('state.smartAssDraft') -or -not $javascript.Contains('state.smartAssPendingTarget')) {
    throw 'Smart A.S.S. draft state protection is missing.'
}
if (-not $javascript.Contains('const rolledRight =') -or -not $javascript.Contains('const rolledUp =') -or -not $javascript.Contains('if (wy >= 0)')) {
    throw 'Canvas navball must rotate the spherical basis and preserve blue-sky/brown-ground hemispheres.'
}
if (-not $javascript.Contains('lerpAngle')) { throw 'Shortest-path angular interpolation is missing.' }
if (-not $html.Contains('id="sasHeaderState"')) { throw 'Persistent SAS status indicator is missing.' }
foreach ($confirmationId in @('stageSlideConfirm', 'quickloadSlideConfirm', 'ascentStageSlideConfirm')) {
    if (-not $html.Contains("id=`"$confirmationId`"")) { throw "Slide confirmation is missing: $confirmationId" }
}
if (-not $javascript.Contains('bindSlideConfirmation') -or -not $javascript.Contains('stageSlideConfirm') -or -not $javascript.Contains('quickloadSlideConfirm') -or -not $javascript.Contains('ascentStageSlideConfirm')) {
    throw 'Stage and quickload controls must use slide-to-confirm.'
}
if (-not $styles.Contains('-webkit-touch-callout: none')) { throw 'Touch callout suppression for confirmation rails is missing.' }
if (-not $javascript.Contains("serverCapabilities.has('game.quicksave.load')") -or -not $html.Contains('id="holdQuickload" aria-expanded="false" disabled')) {
    throw 'Quicksave control must remain disabled until the server advertises support.'
}
if (-not $addonSource.Contains('QuickSaveLoad.fetch') -or -not $addonSource.Contains('quicksave.sfs') -or -not $addonSource.Contains('BindingFlags.Instance | BindingFlags.NonPublic')) {
    throw 'KSP 1.12.5 native quickload bridge or safety checks are missing.'
}
if ($javascript.Contains('data-engine-limit-apply') -or -not $javascript.Contains('scheduleEngineLimit(event.target, false)')) {
    throw 'Engine thrust limit must stream while dragging without an Apply button.'
}
if (-not $html.Contains('id="clearRecorder"') -or -not $javascript.Contains("sendCommand('mechjeb.recorder.clear')") -or -not $addonSource.Contains('case "mechjeb.recorder.clear":')) {
    throw 'Flight Recorder clear-and-reset-T+0 command is missing.'
}
if (-not $javascript.Contains('function syncAscentField(') -or
    -not $javascript.Contains('apoapsisKm: ascentPath === ''PVG'' ? ascentApoapsisKm : ascentAltitudeKm') -or
    -not $javascript.Contains('useCurrentSettings: true') -or
    -not $addonSource.Contains('case "mechjeb.ascent.setting":')) {
    throw 'Ascent settings must synchronize per field and plots must reflect the live target orbit.'
}
if ((-not $html.Contains('id="ascentLaunchMode"')) -or
    (-not $html.Contains('value="TARGET_PLANE"')) -or
    (-not $html.Contains('value="TARGET_LAN"')) -or
    (-not $html.Contains('value="RENDEZVOUS"')) -or
    (-not $javascript.Contains('launchMode: $(''#ascentLaunchMode'').value')) -or
    (-not $addonSource.Contains('LaunchTiming.TimeToPhaseAngle')) -or
    (-not $addonSource.Contains('guidance.launchingToPlane = true')) -or
    (-not $addonSource.Contains('guidance.launchingToMatchLAN = true')) -or
    (-not $addonSource.Contains('guidance.launchingToRendezvous = true')) -or
    (-not $addonSource.Contains('MinimumTimeToPlane')) -or
    (-not $addonSource.Contains('MechJeb 2.14.3 exposes launch-to-target-LAN only with PVG guidance'))) {
    throw 'Distinct MechJeb target-plane, target-LAN and rendezvous launch bindings are missing.'
}
if ((-not $html.Contains('id="aeroCenterMarker"')) -or
    (-not $javascript.Contains('function renderAerodynamicCenter(aero)')) -or
    (-not $javascript.Contains('outsideAtmosphere')) -or
    (-not $javascript.Contains('readout.hidden = true')) -or
    (-not $addonSource.Contains('AerodynamicCenterProvider.Capture')) -or
    (-not $addonSource.Contains('RequestActiveVisualRedraw'))) {
    throw 'Event-driven vessel rendering or FAR aerodynamic-center overlay is missing.'
}
if ((-not $html.Contains('id="dockingOverrideSafeDistance"')) -or
    (-not $html.Contains('id="dockingOverrideStartDistance"')) -or
    (-not $html.Contains('id="dockingForceRoll"')) -or
    (-not $html.Contains('id="dockingRoll"')) -or
    (-not $javascript.Contains("sendCommand('mechjeb.docking.configure'")) -or
    (-not $addonSource.Contains('docking.forceRol =')) -or
    (-not $addonSource.Contains('docking.overrideTargetSize ='))) {
    throw 'MechJeb docking override or force-roll bindings are missing.'
}
if ((-not $html.Contains('id="dockCraftMarker"')) -or
    (-not $html.Contains('id="dockErrorX"')) -or
    (-not $html.Contains('id="dockErrorY"')) -or
    (-not $javascript.Contains('message.dockingAxisAvailable')) -or
    (-not $addonSource.Contains('targetController.DockingAxis.normalized')) -or
    (-not $addonSource.Contains('DockingAxisErrorX')) -or
    (-not $addonSource.Contains('DockingAxisErrorY'))) {
    throw 'Live target docking-axis projection is missing.'
}
if ((-not $html.Contains('id="landingAutopilotStep"')) -or
    (-not $html.Contains('id="landingPredictedCoordinates"')) -or
    (-not $html.Contains('id="landingTargetDifference"')) -or
    (-not $html.Contains('id="landingGearStageLimit"')) -or
    (-not $html.Contains('id="landingPredictionsEnabled"')) -or
    (-not $javascript.Contains("sendCommand('mechjeb.landing.configure'")) -or
    (-not $addonSource.Contains('landing.CurrentStep.status')) -or
    (-not $addonSource.Contains('prediction.maxDragGees')) -or
    (-not $addonSource.Contains('predictions.makeAerobrakeNodes'))) {
    throw 'MechJeb landing guidance settings or prediction readback is incomplete.'
}
if ((-not $html.Contains('id="view-envelope"')) -or
    (-not $html.Contains('id="envelopePreventOverheat"')) -or
    (-not $javascript.Contains("sendCommand('mechjeb.envelope.set'")) -or
    (-not $addonSource.Contains('MechJebModuleThrustController'))) {
    throw 'MechJeb flight-envelope page or thrust-controller bridge is missing.'
}
if ((-not $html.Contains('id="view-trajectory"')) -or
    (-not $html.Contains('id="trajectoryIntegrationStep"')) -or
    (-not $javascript.Contains("sendCommand('trajectories.profile.set'")) -or
    (-not $addonSource.Contains('TrajectoriesBridge.Capture'))) {
    throw 'Trajectories prediction page, profile controls, or runtime bridge is missing.'
}
if ((-not $javascript.Contains('function sendTrajectorySettings()')) -or
    (-not $javascript.Contains("input.id === 'trajectoryDisplayInFlight' && input.checked")) -or
    (-not $javascript.Contains("input.id === 'trajectoryDisplay' && !input.checked"))) {
    throw 'Trajectories switches must apply immediately and preserve the master-display dependency.'
}
if ((-not $trajectoriesBridge.Contains('FindObjectOfType(scenarioType)')) -or
    (-not $trajectoriesBridge.Contains('current is TypeInitializationException')) -or
    (-not $trajectoriesBridge.Contains('Waiting for the Trajectories scenario module.'))) {
    throw 'Trajectories API must wait for its ScenarioModule and circuit-break type initialization failures.'
}
if ((-not $html.Contains('id="ascentTargetOrbitEllipse"')) -or
    (-not $html.Contains('id="ascentProfileChart"')) -or
    (-not $javascript.Contains('function captureAscentSpatialHistory')) -or
    (-not $javascript.Contains('data-series="coast"')) -or
    (-not $javascript.Contains('function renderAscentTargetOrbit(model)')) -or
    (-not $javascript.Contains('function renderAscentProfile(model)')) -or
    (-not $javascript.Contains('updateAscentFlightProgress(message.altitude)')) -or
    (-not $javascript.Contains('orbit.semiMajorAxis'))) {
    throw 'Ascent target-orbit and flight-profile diagrams must be driven by draft, MechJeb, and live flight data.'
}
if (-not $addonSource.Contains('departureBodyOrbit.SynodicPeriod(targetOrbit)') -or -not $addonSource.Contains('OrbitUtil.GetTransferTime(departureBodyOrbit, targetOrbit)')) {
    throw 'Porkchop windows must follow MechJeb 2.14.3 dynamic transfer timing.'
}
if ((-not $javascript.Contains('Math.asinh')) -or
    (-not $javascript.Contains('const contourCount = 18')) -or
    (-not $javascript.Contains('isPorkchopCost')) -or
    (-not $html.Contains('data-pork-departure-tick')) -or
    (-not $html.Contains('id="porkMinimum"')) -or
    (-not $styles.Contains('.pork-minimum'))) {
    throw 'High-contrast Porkchop scaling, contours, dynamic axes, or minimum marker is missing.'
}

$dll = Get-Item -LiteralPath $dllPath
$webBytes = (Get-ChildItem -LiteralPath (Join-Path $productRoot 'prototype') -File | Measure-Object Length -Sum).Sum
$hash = Get-FileHash -LiteralPath $dllPath -Algorithm SHA256
Write-Host "[ArmorControl] PASS"
Write-Host "  DLL bytes : $($dll.Length)"
Write-Host "  Web bytes : $webBytes"
Write-Host "  SHA-256   : $($hash.Hash)"
