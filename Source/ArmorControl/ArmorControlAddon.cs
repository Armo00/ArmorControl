using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using Stopwatch = System.Diagnostics.Stopwatch;
using ArmorOverhaul.ArmorControl.Core;
using ArmorOverhaul.ArmorControl.Protocol;
using ArmorOverhaul.ArmorControl.Rendering;
using ArmorOverhaul.ArmorControl.Transport;
using KSP.UI.Screens;
using MuMech;
using UnityEngine;

namespace ArmorOverhaul.ArmorControl
{
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public sealed class ArmorControlAddon : MonoBehaviour
    {
        private const string LogPrefix = "[ArmorControl] ";
        private const float ServerRetrySeconds = 5f;
        private static ArmorControlAddon instance;

        private readonly MainThreadBridge bridge = new MainThreadBridge();
        private VesselImageRenderer vesselImageRenderer;
        private ArmorControlServer server;
        private ArmorControlSettings settings;
        private string webRoot;
        private string settingsPath;
        private bool serverStartRequested;
        private string serverStatus = "Server stopped";
        private string portText = "8765";
        private bool windowVisible;
        private Rect windowRect = new Rect(100, 100, 340, 0);
        private ApplicationLauncherButton launcherButton;
        private Texture2D offlineIcon;
        private Texture2D onlineIcon;
        private long telemetrySequence;
        private long regularTelemetrySequence;
        private string reportedServerError;
        private float nextServerStartAttempt;
        private float nextStatusLog;
        private float nextRegularTelemetry;
        private float nextFastTelemetry;
        private float nextMetricsPublish;
        private long fastCaptureTicks;
        private long fastCaptureMaximumTicks;
        private long fastCaptureSamples;
        private long regularCaptureTicks;
        private long regularCaptureMaximumTicks;
        private long regularCaptureSamples;
        private long structureRevision;
        private string structureVesselId;
        private int structurePartCount = -1;
        private int structureCrewCount = -1;
        private float nextStructureRefresh;
        private int vesselVisualStateHash;
        private bool vesselVisualStateKnown;
        private MechJebModuleFlightRecorder recorderSource;
        private int recorderSourceIndex;
        private long recorderSequence;
        private bool recorderWarningReported;
        private long flightPanelSequence;
        private bool flightPanelWarningReported;
        private long automationSequence;
        private bool automationWarningReported;
        private AllGraphTransferCalculator porkchopWorker;
        private long porkchopRevision;
        private string porkchopStatus = "idle";
        private int porkchopProgress;
        private bool porkchopPublished;
        private int porkchopWidth;
        private int porkchopHeight;
        private double porkchopMinDeparture;
        private double porkchopMaxDeparture;
        private double porkchopMinTransfer;
        private double porkchopMaxTransfer;
        private double porkchopTargetPeriapsisHeight;
        private bool porkchopIncludeCapture;
        private CelestialBody porkchopTargetBody;
        private Vessel remoteControlVessel;
        private float remoteControlExpiresAt;
        private float remotePitch;
        private float remoteYaw;
        private float remoteRoll;
        private float remoteThrottle;
        private TargetCatalogEntry[] targetCatalog = new TargetCatalogEntry[0];
        private float nextTargetCatalogRefresh;

        private void Awake()
        {
            if (instance != null && instance != this)
            {
                Destroy(this);
                return;
            }

            instance = this;
            vesselImageRenderer = new VesselImageRenderer(bridge);
            DontDestroyOnLoad(gameObject);
            bridge.CommandHandler = ExecuteCommand;

            webRoot = Path.Combine(
                KSPUtil.ApplicationRootPath,
                "GameData",
                "ArmorControl",
                "prototype");
            settingsPath = Path.Combine(
                KSPUtil.ApplicationRootPath,
                "GameData",
                "ArmorControl",
                "settings.cfg");
            settings = ArmorControlSettings.Load(settingsPath, message => Debug.LogWarning(LogPrefix + message));
            portText = settings.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            GameEvents.onGUIApplicationLauncherReady.Add(CreateLauncherButton);
            GameEvents.onGUIApplicationLauncherDestroyed.Add(DestroyLauncherButton);
            if (ApplicationLauncher.Ready)
            {
                CreateLauncherButton();
            }
            if (!settings.Enabled)
            {
                Debug.Log(LogPrefix + "disabled by settings.cfg.");
                return;
            }

            serverStartRequested = settings.AutoStartServer;
            if (serverStartRequested)
            {
                TryStartServer();
            }
        }

        private void FixedUpdate()
        {
            if (vesselImageRenderer != null)
            {
                try { vesselImageRenderer.Tick(server != null && server.VesselImageClientCount > 0); }
                catch (Exception exception) { Debug.LogWarning(LogPrefix + "vessel image render failed: " + exception.Message); }
            }
            if (settings == null || !settings.Enabled || Time.realtimeSinceStartup < nextFastTelemetry)
            {
                return;
            }
            nextFastTelemetry = Time.realtimeSinceStartup + 1f / settings.FastTelemetryHz;
            long started = Stopwatch.GetTimestamp();
            bridge.Publish(CaptureSnapshot());
            RecordCapture(Stopwatch.GetTimestamp() - started, true);
        }

        private void Update()
        {
            if (settings != null && settings.Enabled && serverStartRequested && server == null
                && Time.realtimeSinceStartup >= nextServerStartAttempt)
            {
                TryStartServer();
            }

            bridge.DrainCommands(64);
            UpdateRemoteControlExpiry();
            UpdatePorkchop();

            if (settings != null && settings.Enabled && Time.realtimeSinceStartup >= nextRegularTelemetry)
            {
                nextRegularTelemetry = Time.realtimeSinceStartup + 1f / settings.RegularTelemetryHz;
                long started = Stopwatch.GetTimestamp();
                bridge.PublishRegular(CaptureRegularSnapshot());
                RecordCapture(Stopwatch.GetTimestamp() - started, false);
                RefreshStructureSnapshot();
                RefreshFlightRecorder();
                bridge.PublishFlightPanel(CaptureFlightPanel());
                bridge.PublishAutomation(CaptureAutomation());
            }

            if (settings != null && settings.Enabled && Time.realtimeSinceStartup >= nextMetricsPublish)
            {
                nextMetricsPublish = Time.realtimeSinceStartup + 1f;
                bridge.PublishMetrics(new RuntimeMetrics(
                    fastCaptureSamples, AverageMilliseconds(fastCaptureTicks, fastCaptureSamples), Milliseconds(fastCaptureMaximumTicks),
                    regularCaptureSamples, AverageMilliseconds(regularCaptureTicks, regularCaptureSamples), Milliseconds(regularCaptureMaximumTicks)));
            }

            if (server == null || Time.realtimeSinceStartup < nextStatusLog)
            {
                return;
            }
            nextStatusLog = Time.realtimeSinceStartup + 5f;
            string error = server.LastError;
            if (!string.IsNullOrEmpty(error) && !string.Equals(error, reportedServerError, StringComparison.Ordinal))
            {
                reportedServerError = error;
                Debug.LogWarning(LogPrefix + error);
            }
        }

        private void TryStartServer()
        {
            ArmorControlServer candidate = null;
            try
            {
                candidate = new ArmorControlServer(settings.BindAddress, settings.Port, webRoot, bridge, settings.AccessToken);
                candidate.Start();
                server = candidate;
                serverStatus = "Server running";
                nextServerStartAttempt = 0f;
                RefreshLauncherIcon();
                Debug.Log(LogPrefix + "v0.1.0 listening on " + settings.BindAddress + ":" + settings.Port
                    + " (fast " + settings.FastTelemetryHz + " Hz, regular " + settings.RegularTelemetryHz + " Hz), web root " + webRoot);
            }
            catch (Exception exception)
            {
                if (candidate != null)
                {
                    candidate.Dispose();
                }
                server = null;
                serverStatus = "Start failed: " + exception.Message;
                nextServerStartAttempt = Time.realtimeSinceStartup + ServerRetrySeconds;
                RefreshLauncherIcon();
                Debug.LogWarning(LogPrefix + "server start failed; retrying in " + ServerRetrySeconds
                    + " seconds: " + exception.Message);
            }
        }

        private void CreateLauncherButton()
        {
            if (launcherButton != null || !ApplicationLauncher.Ready)
            {
                return;
            }

            if (offlineIcon == null) offlineIcon = CreateLauncherIcon(new Color32(128, 145, 151, 255));
            if (onlineIcon == null) onlineIcon = CreateLauncherIcon(new Color32(54, 220, 174, 255));
            launcherButton = ApplicationLauncher.Instance.AddModApplication(
                ShowWindow,
                HideWindow,
                null,
                null,
                null,
                null,
                ApplicationLauncher.AppScenes.ALWAYS,
                server == null ? offlineIcon : onlineIcon);
        }

        private void DestroyLauncherButton()
        {
            if (launcherButton != null && ApplicationLauncher.Instance != null)
            {
                ApplicationLauncher.Instance.RemoveModApplication(launcherButton);
            }
            launcherButton = null;
            windowVisible = false;
        }

        private void RefreshLauncherIcon()
        {
            if (launcherButton != null)
            {
                launcherButton.SetTexture(server == null ? offlineIcon : onlineIcon);
            }
        }

        private void ShowWindow()
        {
            windowVisible = true;
        }

        private void HideWindow()
        {
            windowVisible = false;
        }

        private void OnGUI()
        {
            if (!windowVisible)
            {
                return;
            }
            windowRect = GUILayout.Window(GetInstanceID(), windowRect, DrawServerWindow, "ArmorControl");
        }

        private readonly ArmorControlLocalization localization = new ArmorControlLocalization();
        private string Localize(string source) { return localization.Text(webRoot, settings.Language, source); }
        private void SelectLanguage(string language)
        {
            settings.Language = language;
            try { settings.Save(settingsPath); }
            catch (Exception exception) { serverStatus = "Could not save settings: " + exception.Message; }
        }

        private void DrawServerWindow(int windowId)
        {
            string displayStatus = Localize(serverStatus);
            foreach (string prefix in new[] { "Start failed: ", "Could not save settings: " })
                if (serverStatus.StartsWith(prefix, StringComparison.Ordinal)) displayStatus = Localize(prefix) + serverStatus.Substring(prefix.Length);
            GUILayout.Label(displayStatus);
            GUILayout.BeginHorizontal();
            GUILayout.Label(Localize("Language"));
            if (GUILayout.Toggle(settings.Language == "zh-CN", "简体中文", GUI.skin.button) && settings.Language != "zh-CN") SelectLanguage("zh-CN");
            if (GUILayout.Toggle(settings.Language == "en-US", "English", GUI.skin.button) && settings.Language != "en-US") SelectLanguage("en-US");
            GUILayout.EndHorizontal();
            GUILayout.Space(4f);
            GUILayout.BeginHorizontal();
            GUILayout.Label(Localize("Port"), GUILayout.Width(64f));
            GUI.enabled = server == null && !serverStartRequested;
            portText = GUILayout.TextField(portText, 5, GUILayout.Width(90f));
            GUI.enabled = true;
            GUILayout.EndHorizontal();

            if (server == null)
            {
                if (!serverStartRequested)
                {
                    if (GUILayout.Button(Localize("Start Server"), GUILayout.Height(30f)))
                    {
                        StartServerFromWindow();
                    }
                }
                else
                {
                    GUILayout.Label(Localize("Retrying every {0} seconds...").Replace("{0}", ServerRetrySeconds.ToString()));
                    if (GUILayout.Button(Localize("Cancel Start"), GUILayout.Height(30f)))
                    {
                        serverStartRequested = false;
                        serverStatus = "Server stopped";
                    }
                }
            }
            else
            {
                GUILayout.Label(Localize("Local: ") + "http://127.0.0.1:" + settings.Port + "/");
                GUILayout.Label(Localize("Listening: ") + settings.BindAddress + ":" + settings.Port);
                GUILayout.Label(Localize("Clients: ") + server.ClientCount);
                if (GUILayout.Button(Localize("Stop Server"), GUILayout.Height(30f)))
                {
                    serverStartRequested = false;
                    StopServer();
                    serverStatus = "Server stopped";
                }
            }

            GUI.DragWindow(new Rect(0f, 0f, 10000f, 24f));
        }

        private void StartServerFromWindow()
        {
            int port;
            if (!int.TryParse(portText, out port) || port < 1024 || port > 65535)
            {
                serverStatus = "Port must be between 1024 and 65535";
                return;
            }

            settings.Port = port;
            try
            {
                settings.Save(settingsPath);
            }
            catch (Exception exception)
            {
                serverStatus = "Could not save settings: " + exception.Message;
                return;
            }
            serverStartRequested = true;
            TryStartServer();
        }

        private static Texture2D CreateLauncherIcon(Color32 accent)
        {
            const int size = 38;
            var texture = new Texture2D(size, size, TextureFormat.ARGB32, false);
            texture.name = "ArmorControl Launcher Icon";
            texture.filterMode = FilterMode.Bilinear;
            var pixels = new Color32[size * size];
            var transparent = new Color32(0, 0, 0, 0);
            var dark = new Color32(20, 31, 36, 235);
            float center = (size - 1) * 0.5f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = x - center;
                    float dy = y - center;
                    float distance = Mathf.Sqrt(dx * dx + dy * dy);
                    Color32 color = distance <= 16f ? dark : transparent;
                    if (distance >= 13.5f && distance <= 16f) color = accent;
                    if ((Mathf.Abs(dx) <= 1.2f || Mathf.Abs(dy) <= 1.2f) && distance <= 10f) color = accent;
                    if (Mathf.Abs(dx) + Mathf.Abs(dy) <= 5f) color = accent;
                    pixels[y * size + x] = color;
                }
            }
            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            return texture;
        }

        private TelemetrySnapshot CaptureSnapshot()
        {
            long sequence = Interlocked.Increment(ref telemetrySequence);
            string scene = HighLogic.LoadedScene.ToString();
            double universalTime = Planetarium.fetch == null ? 0 : Planetarium.GetUniversalTime();
            Vessel vessel = HighLogic.LoadedSceneIsFlight ? FlightGlobals.ActiveVessel : null;
            if (vessel == null)
            {
                return new TelemetrySnapshot(sequence, universalTime, 0, scene, null, null, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
            }

            float throttle = vessel.ctrlState == null ? 0 : vessel.ctrlState.mainThrottle;
            double pitch;
            double roll;
            ReadSurfaceAttitude(vessel, out pitch, out roll);
            return new TelemetrySnapshot(
                sequence,
                universalTime,
                vessel.missionTime,
                scene,
                vessel.id.ToString("N"),
                vessel.vesselName,
                vessel.altitude,
                vessel.radarAltitude,
                vessel.srfSpeed,
                vessel.verticalSpeed,
                FlightGlobals.ship_heading,
                pitch,
                roll,
                throttle,
                vessel.geeForce,
                vessel.dynamicPressurekPa);
        }

        private static void ReadSurfaceAttitude(Vessel vessel, out double pitch, out double roll)
        {
            Vector3 up = (Vector3)(vessel.CoM - vessel.mainBody.position).normalized;
            Quaternion surfaceFrame = Quaternion.LookRotation(vessel.north, up);
            Quaternion vesselSurface = Quaternion.Inverse(
                Quaternion.Euler(90f, 0f, 0f)
                * Quaternion.Inverse(vessel.GetTransform().rotation)
                * surfaceFrame);
            Vector3 angles = vesselSurface.eulerAngles;
            pitch = angles.x > 180f ? 360.0 - angles.x : -angles.x;
            roll = angles.z > 180f ? angles.z - 360.0 : angles.z;
        }

        private void RecordCapture(long elapsedTicks, bool fast)
        {
            if (fast)
            {
                fastCaptureTicks += elapsedTicks;
                fastCaptureSamples++;
                if (elapsedTicks > fastCaptureMaximumTicks) fastCaptureMaximumTicks = elapsedTicks;
                return;
            }

            regularCaptureTicks += elapsedTicks;
            regularCaptureSamples++;
            if (elapsedTicks > regularCaptureMaximumTicks) regularCaptureMaximumTicks = elapsedTicks;
        }

        private static double AverageMilliseconds(long ticks, long samples)
        {
            return samples == 0 ? 0 : Milliseconds(ticks) / samples;
        }

        private static double Milliseconds(long ticks)
        {
            return ticks * 1000.0 / Stopwatch.Frequency;
        }

        private RegularTelemetrySnapshot CaptureRegularSnapshot()
        {
            long sequence = Interlocked.Increment(ref regularTelemetrySequence);
            double universalTime = Planetarium.fetch == null ? 0 : Planetarium.GetUniversalTime();
            Vessel vessel = HighLogic.LoadedSceneIsFlight ? FlightGlobals.ActiveVessel : null;
            if (vessel == null || vessel.orbit == null)
            {
                return new RegularTelemetrySnapshot(
                    sequence, universalTime, null, null, HighLogic.LoadedScene.ToString(),
                    0, 0, 0, 0, 0, 0, 0, 0,
                    0, 0, 0, 0, 0, 0,
                    0, 0, 0, null);
            }

            Orbit orbit = vessel.orbit;
            ITargetable target = FlightGlobals.fetch == null ? null : FlightGlobals.fetch.VesselTarget;
            return new RegularTelemetrySnapshot(
                sequence,
                universalTime,
                vessel.id.ToString("N"),
                vessel.mainBody == null ? null : vessel.mainBody.bodyName,
                vessel.situation.ToString(),
                orbit.ApA,
                orbit.PeA,
                orbit.eccentricity,
                orbit.inclination,
                orbit.period,
                orbit.timeToAp,
                orbit.timeToPe,
                orbit.semiMajorAxis,
                vessel.latitude,
                vessel.longitude,
                vessel.horizontalSrfSpeed,
                vessel.mach,
                vessel.staticPressurekPa,
                vessel.GetTotalMass(),
                vessel.GetCrewCount(),
                vessel.parts == null ? 0 : vessel.parts.Count,
                KSP.UI.Screens.StageManager.CurrentStage,
                target == null ? null : target.GetName());
        }

        private void RefreshStructureSnapshot()
        {
            Vessel vessel = HighLogic.LoadedSceneIsFlight ? FlightGlobals.ActiveVessel : null;
            string vesselId = vessel == null ? null : vessel.id.ToString("N");
            int partCount = vessel == null || vessel.parts == null ? 0 : vessel.parts.Count;
            int crewCount = vessel == null ? 0 : vessel.GetCrewCount();
            bool structureChanged = !string.Equals(structureVesselId, vesselId, StringComparison.Ordinal)
                || structurePartCount != partCount || structureCrewCount != crewCount;
            if (!structureChanged && Time.realtimeSinceStartup < nextStructureRefresh)
            {
                return;
            }

            nextStructureRefresh = Time.realtimeSinceStartup + .2f;
            if (structureChanged && vesselImageRenderer != null) vesselImageRenderer.RequestRedraw();

            structureVesselId = vesselId;
            structurePartCount = partCount;
            structureCrewCount = crewCount;
            long revision = Interlocked.Increment(ref structureRevision);
            if (vessel == null)
            {
                vesselVisualStateKnown = false;
                bridge.PublishStructure(new VesselStructureSnapshot(revision, null, null, 0, null, null,
                    AerodynamicCenterSnapshot.Unavailable("No active vessel.")));
                return;
            }

            var crew = new List<CrewMemberSnapshot>(crewCount);
            var parts = new List<PartSnapshot>(partCount);
            int crewCapacity = 0;
            int visualStateHash = 17;
            bool activeVisuals = false;
            foreach (Part part in vessel.parts)
            {
                if (part == null) continue;
                crewCapacity += part.CrewCapacity;
                string title = part.partInfo == null ? part.name : part.partInfo.title;
                if (part.protoModuleCrew != null)
                {
                    foreach (ProtoCrewMember member in part.protoModuleCrew)
                    {
                        if (member == null) continue;
                        bool evaAvailable = FlightEVA.fetch != null && part.CrewCapacity > 0;
                        crew.Add(new CrewMemberSnapshot(member.name, member.trait, member.experienceLevel, title,
                            part.flightID, evaAvailable, evaAvailable ? null : "EVA service is unavailable."));
                    }
                }

                var resources = new List<ResourceSnapshot>();
                if (part.Resources != null)
                {
                    foreach (PartResource resource in part.Resources)
                    {
                        if (resource != null) resources.Add(new ResourceSnapshot(resource.resourceName, resource.amount, resource.maxAmount));
                    }
                }

                var modules = new List<string>();
                var engines = new List<EngineSnapshot>();
                var toggles = new List<PartToggleSnapshot>();
                var actions = new List<PartActionSnapshot>();
                if (part.Modules != null)
                {
                    var gimbals = new List<int>();
                    for (int moduleIndex = 0; moduleIndex < part.Modules.Count; moduleIndex++)
                    {
                        PartModule module = part.Modules[moduleIndex];
                        if (module == null) continue;
                        modules.Add(module.moduleName);
                        if (module is ModuleGimbal) gimbals.Add(moduleIndex);
                    }
                    int engineOrdinal = 0;
                    for (int moduleIndex = 0; moduleIndex < part.Modules.Count; moduleIndex++)
                    {
                        PartModule module = part.Modules[moduleIndex];
                        if (module == null) continue;
                        ModuleEngines engine = module as ModuleEngines;
                        if (engine != null)
                        {
                            int gimbalIndex = engineOrdinal < gimbals.Count ? gimbals[engineOrdinal] : (gimbals.Count == 1 ? gimbals[0] : -1);
                            ModuleGimbal gimbal = gimbalIndex >= 0 ? part.Modules[gimbalIndex] as ModuleGimbal : null;
                            engines.Add(new EngineSnapshot(moduleIndex, engine.engineID,
                                string.IsNullOrEmpty(engine.engineName) ? engine.engineID : engine.engineName,
                                engine.EngineIgnited, !engine.EngineIgnited,
                                engine.allowShutdown && engine.EngineIgnited, engine.thrustPercentage,
                                gimbalIndex, gimbal != null && !gimbal.gimbalLock));
                            unchecked
                            {
                                visualStateHash = visualStateHash * 31 + (int)part.flightID;
                                visualStateHash = visualStateHash * 31 + (engine.EngineIgnited ? 1 : 0);
                                visualStateHash = visualStateHash * 31 + Mathf.RoundToInt(engine.thrustPercentage);
                                visualStateHash = visualStateHash * 31 + (gimbal != null && !gimbal.gimbalLock ? 1 : 0);
                            }
                            activeVisuals |= engine.EngineIgnited;
                            engineOrdinal++;
                        }
                        ModuleLight light = module as ModuleLight;
                        if (light != null)
                            toggles.Add(new PartToggleSnapshot("light", moduleIndex, "灯光", light.isOn, false));
                        ModuleRCS rcs = module as ModuleRCS;
                        if (rcs != null)
                            toggles.Add(new PartToggleSnapshot("rcs", moduleIndex,
                                string.IsNullOrEmpty(rcs.GetModuleDisplayName()) ? "RCS 推进器" : rcs.GetModuleDisplayName(),
                                rcs.rcsEnabled, false));
                        PartActionSnapshot action = CapturePartAction(part, module, moduleIndex);
                        actions.AddRange(PartFlightEvents.Capture(module, moduleIndex));
                        if (action != null)
                        {
                            actions.Add(action);
                        }
                    }
                    PartToggleSnapshot gearToggle = CaptureGearToggle(part);
                    if (gearToggle != null) toggles.Add(gearToggle);
                    PartToggleSnapshot cargoToggle = CaptureCargoToggle(part, title);
                    if (cargoToggle != null) toggles.Add(cargoToggle);
                    foreach (PartToggleSnapshot toggle in toggles)
                    {
                        unchecked
                        {
                            visualStateHash = visualStateHash * 31 + (int)part.flightID;
                            visualStateHash = visualStateHash * 31 + toggle.ModuleIndex;
                            visualStateHash = visualStateHash * 31 + (toggle.Enabled ? 1 : 0);
                            visualStateHash = visualStateHash * 31 + (toggle.Transitioning ? 1 : 0);
                        }
                        activeVisuals |= toggle.Transitioning;
                    }
                    }

                parts.Add(new PartSnapshot(
                    part.flightID,
                    part.parent == null ? (uint?)null : part.parent.flightID,
                    part.partInfo == null ? part.name : part.partInfo.name,
                    title,
                    part.inverseStage,
                    part.mass,
                    part.temperature,
                    part.maxTemp,
                    part.skinTemperature,
                    part.skinMaxTemp,
                    resources.ToArray(),
                    modules.ToArray(),
                    engines.ToArray(),
                    toggles.ToArray(),
                    actions.ToArray()));
            }

            if (!vesselVisualStateKnown || vesselVisualStateHash != visualStateHash)
            {
                vesselVisualStateKnown = true;
                vesselVisualStateHash = visualStateHash;
                if (vesselImageRenderer != null) vesselImageRenderer.RequestRedraw();
            }
            if (activeVisuals && vesselImageRenderer != null) vesselImageRenderer.RequestActiveVisualRedraw();
            AerodynamicCenterSnapshot aerodynamicCenter = AerodynamicCenterProvider.Capture(vessel, vesselImageRenderer);
            bridge.PublishStructure(new VesselStructureSnapshot(revision, vesselId, vessel.vesselName, crewCapacity,
                crew.ToArray(), parts.ToArray(), aerodynamicCenter));
        }

        private static PartToggleSnapshot CaptureCargoToggle(Part part, string title)
        {
            bool cargoNamed = ContainsIgnoreCase(part.name, "cargo") || ContainsIgnoreCase(part.name, "bay")
                || ContainsIgnoreCase(title, "cargo") || ContainsIgnoreCase(title, "bay")
                || ContainsIgnoreCase(title, "货舱") || ContainsIgnoreCase(title, "机库");
            int animationIndex = -1;
            bool hasCargoModule = false;
            for (int index = 0; index < part.Modules.Count; index++)
            {
                PartModule module = part.Modules[index];
                if (module == null) continue;
                if (string.Equals(module.moduleName, "ModuleCargoBay", StringComparison.Ordinal)) hasCargoModule = true;
                if (animationIndex < 0 && module is ModuleAnimateGeneric) animationIndex = index;
            }
            if (animationIndex < 0 || (!cargoNamed && !hasCargoModule)) return null;
            ModuleAnimateGeneric animation = part.Modules[animationIndex] as ModuleAnimateGeneric;
            return animation == null ? null : new PartToggleSnapshot("cargo", animationIndex, "货舱门",
                animation.animSwitch, animation.aniState == ModuleAnimateGeneric.animationStates.MOVING);
        }

        private static PartToggleSnapshot CaptureGearToggle(Part part)
        {
            for (int index = 0; index < part.Modules.Count; index++)
            {
                PartModule module = part.Modules[index];
                if (module == null) continue;
                string moduleName = module.moduleName ?? string.Empty;
                if (moduleName.IndexOf("WheelDeployment", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    double position = ReadMemberDouble(module, "position", 0);
                    double deployed = ReadMemberDouble(module, "deployedPosition", 1);
                    double retracted = ReadMemberDouble(module, "retractedPosition", 0);
                    bool isDeployed = Math.Abs(position - deployed) <= Math.Abs(position - retracted);
                    string state = ReadMemberString(module, "stateString");
                    bool moving = ContainsIgnoreCase(state, "ing");
                    return new PartToggleSnapshot("gear", index, "起落架", isDeployed, moving);
                }
                if (string.Equals(moduleName, "KSPWheelAdjustableGear", StringComparison.Ordinal))
                {
                    PartModule wheelBase = FindModuleByName(part, "KSPWheelBase");
                    string state = ReadMemberString(wheelBase, "wheelState") ?? ReadMemberString(wheelBase, "persistentState");
                    bool isDeployed = ContainsIgnoreCase(state, "deploy") && !ContainsIgnoreCase(state, "retract");
                    bool moving = ContainsIgnoreCase(state, "ing");
                    return new PartToggleSnapshot("gear", index, "起落架", isDeployed, moving);
                }
            }
            return null;
        }

        private static PartActionSnapshot CapturePartAction(Part part, PartModule module, int moduleIndex)
        {
            if (part == null || module == null) return null;
            string moduleName = module.moduleName ?? module.GetType().Name;

            if (string.Equals(moduleName, "ModuleB9PartSwitch", StringComparison.Ordinal))
            {
                if (!B9SwitchPolicy.CanSwitchFrom(module)) return null;
                var values = new List<string>();
                var labels = new List<string>();
                System.Collections.IEnumerable subtypes = ReadMember(module, "subtypes") as System.Collections.IEnumerable;
                if (subtypes != null)
                {
                    foreach (object subtype in subtypes)
                    {
                        if (!B9SwitchPolicy.CanSwitchTo(subtype)) continue;
                        string value = ReadMemberString(subtype, "subtypeName") ?? ReadMemberString(subtype, "Name");
                        if (string.IsNullOrEmpty(value)) continue;
                        string label = ReadMemberString(subtype, "title");
                        values.Add(value);
                        labels.Add(LocalizePartActionLabel(string.IsNullOrEmpty(label) ? value : label));
                    }
                }
                string current = ReadMemberString(module, "CurrentSubtypeName") ?? ReadMemberString(module, "currentSubtypeName");
                string currentTitle = ReadMemberString(module, "currentSubtypeTitle");
                string description = ReadMemberString(module, "switcherDescription");
                return new PartActionSnapshot("b9", moduleIndex,
                    LocalizePartActionLabel(string.IsNullOrEmpty(description) ? "B9 零件切换" : description),
                    LocalizePartActionLabel(string.IsNullOrEmpty(currentTitle) ? current : currentTitle), false,
                    "b9.switch", "切换配置", values.Exists(value => value != current), values.ToArray(), labels.ToArray(), current);
            }

            ModuleParachute parachute = module as ModuleParachute;
            if (parachute != null)
            {
                string state = parachute.deploymentState.ToString();
                string command = null;
                string commandLabel = null;
                if (string.Equals(state, "STOWED", StringComparison.OrdinalIgnoreCase))
                {
                    command = "parachute.deploy"; commandLabel = "开伞 / 预备";
                }
                else if (string.Equals(state, "ACTIVE", StringComparison.OrdinalIgnoreCase))
                {
                    command = "parachute.disarm"; commandLabel = "取消预备";
                }
                else if (ContainsIgnoreCase(state, "DEPLOYED"))
                {
                    command = "parachute.cut"; commandLabel = "切断伞绳";
                }
                return new PartActionSnapshot("parachute", moduleIndex, "降落伞", ParachuteStateLabel(state),
                    !string.Equals(state, "STOWED", StringComparison.OrdinalIgnoreCase), command, commandLabel,
                    !string.IsNullOrEmpty(command), null, null, null);
            }

            ModuleGenerator generator = module as ModuleGenerator;
            if (generator != null)
            {
                bool active = generator.generatorIsActive;
                return new PartActionSnapshot("generator", moduleIndex,
                    string.IsNullOrEmpty(generator.GetModuleDisplayName()) ? "发电机" : generator.GetModuleDisplayName(),
                    string.IsNullOrEmpty(generator.status) ? (active ? "运行中" : "已停止") : generator.status,
                    active, active ? "generator.stop" : "generator.start", active ? "停止设备" : "启动设备",
                    !generator.isAlwaysActive, null, null, null);
            }

            ModuleResourceConverter converter = module as ModuleResourceConverter;
            if (converter != null)
            {
                bool reactor = ContainsIgnoreCase(moduleName, "reactor") || ContainsIgnoreCase(converter.ConverterName, "reactor");
                bool active = converter.IsActivated;
                string name = string.IsNullOrEmpty(converter.ConverterName)
                    ? (reactor ? "核反应堆" : "资源转换器") : converter.ConverterName;
                return new PartActionSnapshot(reactor ? "reactor" : "converter", moduleIndex, name,
                    string.IsNullOrEmpty(converter.status) ? (active ? "运行中" : "已停止") : converter.status,
                    active, active ? "converter.stop" : "converter.start", active ? "停止设备" : "启动设备",
                    !converter.AlwaysActive, null, null, null);
            }

            if (ContainsIgnoreCase(moduleName, "reactor"))
            {
                bool active = ReadMemberBool(module, "Enabled", ReadMemberBool(module, "IsActivated", false));
                string status = ReadMemberString(module, "CoreStatus") ?? ReadMemberString(module, "status");
                return new PartActionSnapshot("reactor", moduleIndex,
                    string.IsNullOrEmpty(module.GetModuleDisplayName()) ? "核反应堆" : module.GetModuleDisplayName(),
                    string.IsNullOrEmpty(status) ? (active ? "运行中" : "已停止") : status,
                    active, active ? "reactor.disable" : "reactor.enable", active ? "关闭反应堆" : "启动反应堆",
                    true, null, null, null);
            }

            if (module is ModuleCommand || module is ModuleDockingNode)
            {
                bool active = part.vessel != null && part.vessel.referenceTransformId == part.flightID;
                ModuleDockingNode port = module as ModuleDockingNode;
                if (port != null) active = active && part.GetReferenceTransform() == port.controlTransform;
                return new PartActionSnapshot("control", moduleIndex, "控制参考点",
                    active ? "当前控制点" : (module is ModuleDockingNode ? "对接口方向" : "指挥模块方向"),
                    active, "control.fromHere", active ? "正在从这里控制" : "从这里控制", !active,
                    null, null, null);
            }

            return null;
        }

        private static string LocalizePartActionLabel(string value)
        {
            if (string.IsNullOrEmpty(value) || value[0] != '#') return value;
            try { return KSP.Localization.Localizer.Format(value); }
            catch { return value; }
        }

        private static string ParachuteStateLabel(string state)
        {
            if (string.Equals(state, "STOWED", StringComparison.OrdinalIgnoreCase)) return "已收纳";
            if (string.Equals(state, "ACTIVE", StringComparison.OrdinalIgnoreCase)) return "已预备";
            if (string.Equals(state, "SEMIDEPLOYED", StringComparison.OrdinalIgnoreCase)) return "半展开";
            if (string.Equals(state, "DEPLOYED", StringComparison.OrdinalIgnoreCase)) return "已展开";
            if (string.Equals(state, "CUT", StringComparison.OrdinalIgnoreCase)) return "伞绳已切断";
            return state;
        }

        private static bool ContainsIgnoreCase(string value, string fragment)
        {
            return !string.IsNullOrEmpty(value) && value.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static PartModule FindModuleByName(Part part, string name)
        {
            if (part == null || part.Modules == null) return null;
            foreach (PartModule module in part.Modules)
                if (module != null && string.Equals(module.moduleName, name, StringComparison.Ordinal)) return module;
            return null;
        }

        private static object ReadMember(object target, string name)
        {
            if (target == null) return null;
            try
            {
                Type type = target.GetType();
                FieldInfo field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (field != null) return field.GetValue(target);
                PropertyInfo property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                return property == null ? null : property.GetValue(target, null);
            }
            catch { return null; }
        }

        private static double ReadMemberDouble(object target, string name, double fallback)
        {
            object value = ReadMember(target, name);
            try { return value == null ? fallback : Convert.ToDouble(value); }
            catch { return fallback; }
        }

        private static string ReadMemberString(object target, string name)
        {
            object value = ReadMember(target, name);
            return value == null ? null : value.ToString();
        }

        private static bool ReadMemberBool(object target, string name, bool fallback)
        {
            object value = ReadMember(target, name);
            try { return value == null ? fallback : Convert.ToBoolean(value); }
            catch { return fallback; }
        }

        private void RefreshFlightRecorder()
        {
            Vessel vessel = HighLogic.LoadedSceneIsFlight ? FlightGlobals.ActiveVessel : null;
            try
            {
                MechJebCore core = vessel == null ? null : vessel.GetMasterMechJeb();
                MechJebModuleFlightRecorder recorder = core == null ? null : core.GetComputerModule<MechJebModuleFlightRecorder>();
                if (recorder == null || recorder.history == null)
                {
                    if (recorderSource != null) bridge.ResetRecorder();
                    recorderSource = null;
                    recorderSourceIndex = 0;
                    return;
                }

                if (!ReferenceEquals(recorderSource, recorder) || recorder.historyIdx < recorderSourceIndex)
                {
                    recorderSource = recorder;
                    recorderSourceIndex = 0;
                    bridge.ResetRecorder();
                }

                int available = Math.Min(recorder.historyIdx, recorder.history.Length);
                while (recorderSourceIndex < available)
                {
                    MechJebModuleFlightRecorder.record item = recorder.history[recorderSourceIndex++];
                    bridge.PublishRecorder(new FlightRecorderSample(
                        Interlocked.Increment(ref recorderSequence),
                        item.timeSinceMark,
                        item.currentStage,
                        item.altitudeASL,
                        item.downRange,
                        item.speedSurface,
                        item.speedOrbital,
                        item.mass,
                        item.acceleration,
                        item.Q,
                        item.AoA,
                        item.AoS,
                        item.AoD,
                        item.altitudeTrue,
                        item.pitch,
                        item.gravityLosses,
                        item.dragLosses,
                        item.steeringLosses,
                        item.deltaVExpended), false);
                }
                recorderWarningReported = false;
            }
            catch (Exception exception)
            {
                if (!recorderWarningReported)
                {
                    recorderWarningReported = true;
                    Debug.LogWarning(LogPrefix + "MechJeb Flight Recorder adapter unavailable: " + exception.Message);
                }
            }
        }

        private FlightPanelSnapshot CaptureFlightPanel()
        {
            var snapshot = new FlightPanelSnapshot { Sequence = Interlocked.Increment(ref flightPanelSequence) };
            Vessel vessel = HighLogic.LoadedSceneIsFlight ? FlightGlobals.ActiveVessel : null;
            if (vessel == null) return snapshot;
            snapshot.VesselId = vessel.id.ToString("N");
            try
            {
                MechJebCore core = vessel.GetMasterMechJeb();
                MechJebModuleInfoItems info = core == null ? null : core.GetComputerModule<MechJebModuleInfoItems>();
                VesselState state = core == null ? null : core.vesselState;
                if (info == null || state == null) return snapshot;

                snapshot.CurrentOrbit = info.CurrentOrbitSummary();
                snapshot.OrbitInclination = state.orbitInclination;
                snapshot.OrbitPeriod = state.orbitPeriod;
                snapshot.OrbitalSpeed = state.speedOrbital;
                snapshot.TimeToApoapsis = state.orbitTimeToAp;
                snapshot.TimeToPeriapsis = state.orbitTimeToPe;
                snapshot.TimeToSoiTransition = info.TimeToSOITransition();
                snapshot.OrbitEccentricity = state.orbitEccentricity;
                snapshot.AltitudeBottom = state.altitudeBottom;
                snapshot.Coordinates = info.GetCoordinateString();
                snapshot.Heading = state.vesselHeading;
                snapshot.SurfaceGravity = info.SurfaceGravity();
                snapshot.SurfaceSpeed = state.speedSurface;
                snapshot.VerticalSpeed = state.speedVertical;
                snapshot.HorizontalSurfaceSpeed = state.speedSurfaceHorizontal;
                snapshot.MaximumAcceleration = info.MaxAcceleration();
                snapshot.CurrentAcceleration = info.CurrentAcceleration();
                snapshot.MaximumThrust = info.MaxThrust();
                snapshot.CurrentThrust = info.CurrentThrust();
                snapshot.SurfaceTwr = info.SurfaceTWR();
                snapshot.LocalTwr = info.LocalTWR();
                snapshot.ThrottleTwr = info.ThrottleTWR();
                snapshot.GeeForce = info.Acceleration();
                snapshot.AtmosphericDrag = info.AtmosphericDrag();
                snapshot.CrewCapacity = info.CrewCapacity();
                snapshot.CrewCount = info.CrewCount();
                snapshot.DryMass = info.DryMass();
                snapshot.VesselMass = info.VesselMass();
                snapshot.PartCount = info.PartCount();
                snapshot.StageDeltaVAtmosphere = info.StageDeltaVAtmosphere();
                snapshot.StageDeltaVVacuum = info.StageDeltaVVacuum();
                snapshot.TotalDeltaVAtmosphere = info.TotalDeltaVAtmosphere();
                snapshot.TotalDeltaVVacuum = info.TotalDeltaVVaccum();
                snapshot.StageTimeCurrentThrottle = info.StageTimeLeftCurrentThrottle();
                snapshot.StageTimeFullThrottle = info.StageTimeLeftFullThrottle();
                snapshot.StageTimeHover = info.StageTimeLeftHover();
                snapshot.TerminalVelocity = state.TerminalVelocity();
                snapshot.VesselCost = info.VesselCost();
                snapshot.AngleOfAttack = state.AoA;
                snapshot.AngleOfSideslip = state.AoS;
                // MechJeb's historical method name says kPA, but it returns Pa
                // (FlightGlobals.getStaticPressure() multiplied by 1000).
                snapshot.StaticPressureKpa = info.AtmosphericPressurekPA() / 1000.0;
                snapshot.CurrentBiome = info.CurrentBiome();
                // VesselState.dynamicPressure is stored in Pa for both stock and FAR.
                snapshot.DynamicPressureKpa = state.dynamicPressure / 1000.0;
                snapshot.SuicideBurnCountdown = info.SuicideBurnCountdown();
                snapshot.TimeToImpact = info.TimeToImpact();
                snapshot.TargetClosestApproachDistance = info.TargetClosestApproachDistance();
                snapshot.TargetDistance = info.TargetDistance();
                flightPanelWarningReported = false;
            }
            catch (Exception exception)
            {
                if (!flightPanelWarningReported)
                {
                    flightPanelWarningReported = true;
                    Debug.LogWarning(LogPrefix + "MechJeb Flight Pannel adapter unavailable: " + exception.Message);
                }
            }
            return snapshot;
        }

        private AutomationSnapshot CaptureAutomation()
        {
            var snapshot = new AutomationSnapshot { Sequence = Interlocked.Increment(ref automationSequence) };
            Vessel vessel = HighLogic.LoadedSceneIsFlight ? FlightGlobals.ActiveVessel : null;
            snapshot.Trajectory = TrajectoriesBridge.Capture(vessel);
            try { snapshot.OrbitGeometryJson = OrbitGeometry.Capture(vessel); }
            catch (Exception exception) { Debug.LogWarning(LogPrefix + "orbit geometry unavailable: " + exception.Message); }
            if (vessel == null) return snapshot;
            snapshot.VesselId = vessel.id.ToString("N");
            ITargetable target = FlightGlobals.fetch == null ? null : FlightGlobals.fetch.VesselTarget;
            snapshot.TargetName = target == null ? null : target.GetName();
            snapshot.TargetKind = TargetKind(target);
            if (Time.realtimeSinceStartup >= nextTargetCatalogRefresh)
            {
                nextTargetCatalogRefresh = Time.realtimeSinceStartup + 1f;
                targetCatalog = BuildTargetCatalog(vessel);
            }
            snapshot.TargetCatalog = targetCatalog;
            snapshot.PorkchopRevision = porkchopRevision;
            snapshot.PorkchopStatus = porkchopStatus;
            snapshot.PorkchopProgress = porkchopProgress;
            snapshot.ManeuverNodeCount = vessel.patchedConicSolver == null || vessel.patchedConicSolver.maneuverNodes == null
                ? 0 : vessel.patchedConicSolver.maneuverNodes.Count;
            snapshot.ManeuverNodes = new ManeuverNodeSnapshot[snapshot.ManeuverNodeCount];
            for (int index = 0; index < snapshot.ManeuverNodeCount; index++)
            {
                ManeuverNode node = vessel.patchedConicSolver.maneuverNodes[index];
                snapshot.ManeuverNodes[index] = new ManeuverNodeSnapshot(node.UT, node.DeltaV.magnitude);
            }
            if (snapshot.ManeuverNodeCount > 0)
            {
                ManeuverNode nextNode = vessel.patchedConicSolver.maneuverNodes[0];
                snapshot.NextNodeUt = nextNode.UT;
                snapshot.NextNodeDeltaV = nextNode.DeltaV.magnitude;
            }
            snapshot.Sas = vessel.ActionGroups[KSPActionGroup.SAS];
            snapshot.Rcs = vessel.ActionGroups[KSPActionGroup.RCS];
            snapshot.Gear = vessel.ActionGroups[KSPActionGroup.Gear];
            snapshot.Brakes = vessel.ActionGroups[KSPActionGroup.Brakes];
            snapshot.Lights = vessel.ActionGroups[KSPActionGroup.Light];
            for (int index = 1; index <= 10; index++)
                if (vessel.ActionGroups[CustomActionGroup(index)]) snapshot.ActionGroupMask |= 1 << (index - 1);
            snapshot.SolarPanels = DeploymentStatus(vessel.FindPartModulesImplementing<ModuleDeployableSolarPanel>());
            snapshot.Antennas = DeploymentStatus(vessel.FindPartModulesImplementing<ModuleDeployableAntenna>());
            try
            {
                MechJebCore core = vessel.GetMasterMechJeb();
                snapshot.MechJebAvailable = core != null;
                if (core == null) return snapshot;

                MechJebModuleNodeExecutor executor = core.GetComputerModule<MechJebModuleNodeExecutor>();
                MechJebModuleSmartASS smartAss = core.GetComputerModule<MechJebModuleSmartASS>();
                MechJebModuleLandingAutopilot landing = core.GetComputerModule<MechJebModuleLandingAutopilot>();
                MechJebModuleLandingPredictions landingPredictions = core.GetComputerModule<MechJebModuleLandingPredictions>();
                MechJebModuleDockingAutopilot docking = core.GetComputerModule<MechJebModuleDockingAutopilot>();
                MechJebModuleAscentAutopilot ascent = core.GetComputerModule<MechJebModuleAscentAutopilot>();
                MechJebModuleAscentGuidance ascentGuidance = core.GetComputerModule<MechJebModuleAscentGuidance>();
                MechJebModuleAscentGT ascentGt = core.GetComputerModule<MechJebModuleAscentGT>();
                MechJebModuleAscentClassic ascentClassic = core.GetComputerModule<MechJebModuleAscentClassic>();
                MechJebModuleAscentPVG ascentPvg = core.GetComputerModule<MechJebModuleAscentPVG>();
                MechJebModuleRendezvousAutopilot rendezvous = core.GetComputerModule<MechJebModuleRendezvousAutopilot>();
                MechJebModuleStagingController staging = core.GetComputerModule<MechJebModuleStagingController>();
                MechJebModuleThrustController thrustController = core.GetComputerModule<MechJebModuleThrustController>();
                MechJebModuleTargetController targetController = core.GetComputerModule<MechJebModuleTargetController>();
                snapshot.AutoStage = staging != null && staging.enabled;
                if (thrustController != null)
                {
                    snapshot.EnvelopeAvailable = true;
                    snapshot.EnvelopeTerminalVelocity = thrustController.limitToTerminalVelocity;
                    snapshot.EnvelopeDynamicPressure = thrustController.limitDynamicPressure;
                    snapshot.EnvelopeMaximumDynamicPressure = thrustController.maxDynamicPressure.val;
                    snapshot.EnvelopeAcceleration = thrustController.limitAcceleration;
                    snapshot.EnvelopeMaximumAcceleration = thrustController.maxAcceleration.val;
                    snapshot.EnvelopeMaximumThrottle = thrustController.limitThrottle;
                    snapshot.EnvelopeMaximumThrottleValue = thrustController.maxThrottle.val;
                    snapshot.EnvelopeMinimumThrottle = thrustController.limiterMinThrottle;
                    snapshot.EnvelopeMinimumThrottleValue = thrustController.minThrottle.val;
                    snapshot.EnvelopePreventOverheat = thrustController.limitToPreventOverheats;
                    snapshot.EnvelopePreventFlameout = thrustController.limitToPreventFlameout;
                    snapshot.EnvelopeFlameoutSafetyPercent = thrustController.flameoutSafetyPct.val;
                    snapshot.EnvelopePreventUnstableIgnition = thrustController.limitToPreventUnstableIgnition;
                    snapshot.EnvelopeAutoRcsUllage = thrustController.autoRCSUllaging;
                    snapshot.EnvelopeSmoothThrottle = thrustController.smoothThrottle;
                    snapshot.EnvelopeThrottleSmoothingTime = thrustController.throttleSmoothingTime;
                    snapshot.EnvelopeManageIntakes = thrustController.manageIntakes;
                    snapshot.EnvelopeDifferentialThrottle = thrustController.differentialThrottle;
                }
                snapshot.NodeExecutorEnabled = executor != null && executor.enabled;
                snapshot.AutoWarp = executor != null && executor.autowarp;
                if (rendezvous != null)
                {
                    snapshot.RendezvousEnabled = rendezvous.enabled;
                    snapshot.RendezvousStatus = rendezvous.status;
                    snapshot.RendezvousDesiredDistance = rendezvous.desiredDistance.val;
                    snapshot.RendezvousMaxPhasingOrbits = rendezvous.maxPhasingOrbits.val;
                    snapshot.RendezvousMaxClosingSpeed = rendezvous.maxClosingSpeed.val;
                }
                if (targetController != null && targetController.NormalTargetExists)
                {
                    snapshot.TargetName = targetController.Name;
                    snapshot.TargetDistance = targetController.Distance;
                    snapshot.TargetRelativeSpeed = targetController.RelativeVelocity.magnitude;
                    Orbit targetOrbit = targetController.TargetOrbit;
                    if (targetOrbit != null)
                    {
                        snapshot.AscentLaunchTargetAvailable = targetOrbit.referenceBody == vessel.mainBody;
                        snapshot.AscentLaunchTargetName = targetController.Name;
                        snapshot.TargetOrbitApoapsis = targetOrbit.ApA;
                        snapshot.TargetOrbitPeriapsis = targetOrbit.PeA;
                        snapshot.TargetOrbitInclination = targetOrbit.inclination;
                        snapshot.TargetOrbitPeriod = targetOrbit.period;
                    }
                    ModuleDockingNode targetDockingNode = targetController.Target as ModuleDockingNode;
                    if (targetDockingNode != null && targetDockingNode.nodeTransform != null)
                    {
                        Vector3d targetAxis = (Vector3d)targetController.DockingAxis.normalized;
                        if (targetAxis.magnitude > 1e-6)
                        {
                            Vector3d relativePosition = targetController.RelativePosition;
                            Vector3d lateralSeparation = Vector3d.Exclude(targetAxis, relativePosition);
                            snapshot.DockingAxisAvailable = true;
                            snapshot.DockingAxialSeparation = -Vector3d.Dot(relativePosition, targetAxis);
                            snapshot.DockingLateralSeparation = lateralSeparation.magnitude;
                            // MechJeb RelativePosition is vessel position minus target position, so its
                            // target-frame projection is already this vessel's signed axis offset.
                            snapshot.DockingAxisErrorX = Vector3d.Dot(lateralSeparation, (Vector3d)targetDockingNode.nodeTransform.right);
                            snapshot.DockingAxisErrorY = Vector3d.Dot(lateralSeparation, (Vector3d)targetDockingNode.nodeTransform.up);
                        }
                    }
                }
                if (targetController != null && targetController.PositionTargetExists)
                {
                    snapshot.PositionTargetExists = true;
                    snapshot.TargetBody = targetController.targetBody == null ? null : targetController.targetBody.bodyName;
                    snapshot.TargetLatitude = targetController.targetLatitude;
                    snapshot.TargetLongitude = targetController.targetLongitude;
                }
                if (smartAss != null)
                {
                    snapshot.SmartAssMode = smartAss.mode.ToString();
                    snapshot.SmartAssTarget = smartAss.target.ToString();
                    snapshot.SmartAssEnabled = !string.Equals(snapshot.SmartAssTarget, "OFF", StringComparison.OrdinalIgnoreCase)
                        && core.attitude != null
                        && core.attitude.enabled
                        && core.attitude.users.Contains(smartAss);
                    snapshot.SmartAssAutoDisable = smartAss.autoDisableSmartASS;
                    snapshot.SmartAssSurfacePitch = smartAss.srfPit;
                    snapshot.SmartAssSurfaceYaw = smartAss.srfHdg;
                    snapshot.SmartAssSurfaceRoll = smartAss.srfRol;
                    snapshot.SmartAssVelocityPitch = smartAss.srfVelPit;
                    snapshot.SmartAssVelocityYaw = smartAss.srfVelYaw;
                    snapshot.SmartAssVelocityRoll = smartAss.srfVelRol;
                    snapshot.SmartAssRoll = smartAss.rol.val;
                }
                if (landing != null)
                {
                    snapshot.LandingEnabled = landing.enabled;
                    snapshot.LandingAtTarget = landing.landAtTarget;
                    snapshot.LandingPredictionReady = landing.PredictionReady;
                    snapshot.LandingTouchdownSpeed = landing.touchdownSpeed.val;
                    snapshot.LandingDeployGears = landing.deployGears;
                    snapshot.LandingGearStageLimit = landing.limitGearsStage.val;
                    snapshot.LandingDeployChutes = landing.deployChutes;
                    snapshot.LandingChuteStageLimit = landing.limitChutesStage.val;
                    snapshot.LandingRcsAdjustment = landing.rcsAdjustment;
                    snapshot.LandingStep = landing.CurrentStep == null ? null : landing.CurrentStep.GetType().Name;
                    snapshot.LandingStatus = landing.CurrentStep == null ? null : landing.CurrentStep.status;
                    snapshot.LandingDescentMode = landing.descentSpeedPolicy == null ? null : landing.descentSpeedPolicy.GetType().Name;
                    snapshot.LandingUsesAtmosphere = landing.descentSpeedPolicy != null && landing.UseAtmosphereToBrake();
                    ReentrySimulation.Result prediction = landing.Prediction;
                    if (prediction != null)
                    {
                        snapshot.LandingPredictionOutcome = prediction.outcome.ToString();
                        snapshot.LandingPredictedLatitude = prediction.endPosition.latitude;
                        snapshot.LandingPredictedLongitude = prediction.endPosition.longitude;
                        snapshot.LandingPredictedAltitude = prediction.endASL;
                        snapshot.LandingPredictionMaxDrag = prediction.maxDragGees;
                        snapshot.LandingPredictionDeltaV = prediction.deltaVExpended;
                        snapshot.LandingPredictionTimeToLand = Math.Max(0, prediction.endUT - Planetarium.GetUniversalTime());
                        snapshot.LandingPredictionAerobrake = prediction.aeroBrake;
                        if (targetController != null && targetController.PositionTargetExists && prediction.body != null)
                        {
                            Vector3d predictedPosition = prediction.body.GetWorldSurfacePosition(
                                prediction.endPosition.latitude, prediction.endPosition.longitude, 0) - prediction.body.position;
                            Vector3d targetPosition = prediction.body.GetWorldSurfacePosition(
                                targetController.targetLatitude, targetController.targetLongitude, 0) - prediction.body.position;
                            snapshot.LandingPredictionTargetDistance = Vector3d.Distance(predictedPosition, targetPosition);
                        }
                        if (prediction.aeroBrake)
                        {
                            try
                            {
                                Orbit aerobrakeOrbit = prediction.AeroBrakeOrbit();
                                if (aerobrakeOrbit != null)
                                {
                                    snapshot.LandingAerobrakePeriapsis = aerobrakeOrbit.PeA;
                                    snapshot.LandingAerobrakeApoapsis = aerobrakeOrbit.ApA;
                                    snapshot.LandingAerobrakeEccentricity = aerobrakeOrbit.eccentricity;
                                }
                            }
                            catch { /* A partially updated MJ prediction must not break automation telemetry. */ }
                        }
                    }
                }
                if (landingPredictions != null)
                {
                    snapshot.LandingPredictionsEnabled = landingPredictions.enabled;
                    snapshot.LandingPredictionSimulationRunning = landingPredictions.SimulationRunning;
                    snapshot.LandingMakeAerobrakeNodes = landingPredictions.makeAerobrakeNodes;
                    snapshot.LandingShowTrajectory = landingPredictions.showTrajectory;
                    snapshot.LandingWorldTrajectory = landingPredictions.worldTrajectory;
                    snapshot.LandingCameraTrajectory = landingPredictions.camTrajectory;
                }
                if (docking != null)
                {
                    snapshot.DockingEnabled = docking.enabled;
                    snapshot.DockingStatus = docking.status;
                    snapshot.DockingStep = docking.dockingStep.ToString();
                    if (!snapshot.DockingAxisAvailable)
                    {
                        snapshot.DockingAxialSeparation = docking.zSep;
                        snapshot.DockingLateralSeparation = docking.lateralSep.magnitude;
                    }
                    snapshot.DockingSpeedLimit = docking.speedLimit.val;
                    snapshot.DockingOverrideSafeDistance = docking.overrideSafeDistance;
                    snapshot.DockingSafeDistance = docking.overridenSafeDistance.val;
                    snapshot.DockingOverrideStartDistance = docking.overrideTargetSize;
                    snapshot.DockingStartDistance = docking.overridenTargetSize.val;
                    snapshot.DockingForceRoll = docking.forceRol;
                    snapshot.DockingRoll = docking.rol.val;
                    snapshot.DockingDrawBoundingBox = docking.drawBoundingBox;
                }
                if (ascent != null)
                {
                    snapshot.AscentEnabled = ascent.enabled;
                    snapshot.AscentStatus = ascent.status;
                    snapshot.AscentPath = ascent.ascentPathIdxPublic.ToString();
                    snapshot.AscentDesiredOrbitAltitude = ascent.desiredOrbitAltitude.val;
                    snapshot.AscentDesiredInclination = ascent.desiredInclination;
                    snapshot.AscentTimedLaunch = ascent.timedLaunch;
                    snapshot.AscentTimeToLaunch = ascent.tMinus;
                    snapshot.AscentLaunchPhaseAngle = ascent.launchPhaseAngle.val;
                    snapshot.AscentLaunchLanDifference = ascent.launchLANDifference.val;
                    snapshot.AscentLaunchMode = "IMMEDIATE";
                    if (ascentGuidance != null)
                    {
                        if (ascentGuidance.launchingToRendezvous) snapshot.AscentLaunchMode = "RENDEZVOUS";
                        else if (ascentGuidance.launchingToPlane) snapshot.AscentLaunchMode = "TARGET_PLANE";
                        else if (ascentGuidance.launchingToMatchLAN) snapshot.AscentLaunchMode = "TARGET_LAN";
                        else if (ascentGuidance.launchingToLAN) snapshot.AscentLaunchMode = "MANUAL_LAN";
                        else if (ascent.timedLaunch) snapshot.AscentLaunchMode = "COUNTDOWN";
                    }
                    snapshot.AscentAutoThrottle = ascent.autoThrottle;
                    snapshot.AscentCorrectiveSteering = ascent.correctiveSteering;
                    snapshot.AscentAutoStage = ascent.autostage;
                    snapshot.AscentLimitAoA = ascent.limitAoA;
                    snapshot.AscentMaxAoA = ascent.maxAoA.val;
                    snapshot.AscentLimitQEnabled = ascent.limitQaEnabled;
                    snapshot.AscentLimitQ = ascent.limitQa.val;
                    snapshot.AscentForceRoll = ascent.forceRoll;
                    snapshot.AscentVerticalRoll = ascent.verticalRoll.val;
                    snapshot.AscentTurnRoll = ascent.turnRoll.val;
                    snapshot.AscentSkipCircularization = ascent.skipCircularization;
                    snapshot.AscentDesiredLan = ascent.desiredLAN;
                    snapshot.AscentCorrectiveSteeringGain = ascent.correctiveSteeringGain.val;
                    snapshot.AscentDeploySolarPanels = ascent.autodeploySolarPanels;
                    snapshot.AscentDeployAntennas = ascent.autoDeployAntennas;
                    snapshot.AscentRollAltitude = ascent.rollAltitude.val;
                    snapshot.AscentAoAFadeoutPressure = ascent.aoALimitFadeoutPressure.val;
                }
                if (ascentGt != null)
                {
                    snapshot.AscentGtTurnStartAltitude = ascentGt.turnStartAltitude.val;
                    snapshot.AscentGtTurnStartVelocity = ascentGt.turnStartVelocity.val;
                    snapshot.AscentGtTurnStartPitch = ascentGt.turnStartPitch.val;
                    snapshot.AscentGtIntermediateAltitude = ascentGt.intermediateAltitude.val;
                    snapshot.AscentGtHoldApTime = ascentGt.holdAPTime.val;
                }
                if (ascentClassic != null)
                {
                    snapshot.AscentClassicTurnStartAltitude = ascentClassic.turnStartAltitude.val;
                    snapshot.AscentClassicTurnStartVelocity = ascentClassic.turnStartVelocity.val;
                    snapshot.AscentClassicTurnEndAltitude = ascentClassic.turnEndAltitude.val;
                    snapshot.AscentClassicTurnEndAngle = ascentClassic.turnEndAngle.val;
                    snapshot.AscentClassicTurnShapeExponent = ascentClassic.turnShapeExponent.val;
                    snapshot.AscentClassicAutoPath = ascentClassic.autoPath;
                }
                if (ascentPvg != null)
                {
                    snapshot.AscentPvgPitchStartVelocity = ascentPvg.PitchStartVelocity.val;
                    snapshot.AscentPvgPitchRate = ascentPvg.PitchRate.val;
                    snapshot.AscentPvgDesiredApoapsis = ascentPvg.DesiredApoapsis.val;
                    snapshot.AscentPvgDesiredAttachAltitude = ascentPvg.DesiredAttachAlt.val;
                    snapshot.AscentPvgDynamicPressureTrigger = ascentPvg.DynamicPressureTrigger.val;
                    snapshot.AscentPvgStagingTrigger = ascentPvg.StagingTrigger.val;
                    snapshot.AscentPvgFixedCoast = ascentPvg.FixedCoast;
                    snapshot.AscentPvgFixedCoastLength = ascentPvg.FixedCoastLength.val;
                    snapshot.AscentPvgAttachAltitudeEnabled = ascentPvg.AttachAltFlag;
                    snapshot.AscentPvgStagingTriggerEnabled = ascentPvg.StagingTriggerFlag;
                }
                automationWarningReported = false;
            }
            catch (Exception exception)
            {
                if (!automationWarningReported)
                {
                    automationWarningReported = true;
                    Debug.LogWarning(LogPrefix + "MechJeb automation state unavailable: " + exception.Message);
                }
            }
            return snapshot;
        }

        private static string TargetKind(ITargetable target)
        {
            if (target == null) return null;
            if (target is CelestialBody) return "body";
            if (target is Vessel) return "vessel";
            if (target is ModuleDockingNode) return "dockingPort";
            return "other";
        }

        private static TargetCatalogEntry[] BuildTargetCatalog(Vessel activeVessel)
        {
            var entries = new List<TargetCatalogEntry>();
            if (FlightGlobals.Bodies != null)
            {
                foreach (CelestialBody body in FlightGlobals.Bodies)
                {
                    if (body == null) continue;
                    CelestialBody parent = body.referenceBody;
                    string parentId = parent == null || ReferenceEquals(parent, body) ? null : "body:" + parent.bodyName;
                    entries.Add(new TargetCatalogEntry("body:" + body.bodyName, parentId, "body",
                        body.displayName == null ? body.bodyName : body.displayName.Replace("^N", string.Empty),
                        body.bodyName, null, 0));
                }
            }

            if (FlightGlobals.Vessels == null) return entries.ToArray();
            foreach (Vessel candidate in FlightGlobals.Vessels)
            {
                if (candidate == null || candidate == activeVessel || candidate.state == Vessel.State.DEAD) continue;
                string vesselId = candidate.id.ToString("N");
                string bodyName = candidate.mainBody == null ? null : candidate.mainBody.bodyName;
                entries.Add(new TargetCatalogEntry("vessel:" + vesselId,
                    bodyName == null ? null : "body:" + bodyName, "vessel", candidate.vesselName,
                    bodyName, vesselId, 0));

                if (!candidate.loaded || candidate.parts == null) continue;
                foreach (Part part in candidate.parts)
                {
                    if (part == null || part.Modules == null) continue;
                    foreach (PartModule module in part.Modules)
                    {
                        ModuleDockingNode dockingNode = module as ModuleDockingNode;
                        if (dockingNode == null) continue;
                        string title = part.partInfo == null ? part.name : part.partInfo.title;
                        entries.Add(new TargetCatalogEntry("dock:" + vesselId + ":" + part.flightID,
                            "vessel:" + vesselId, "dockingPort", title, bodyName, vesselId, part.flightID));
                    }
                }
            }
            return entries.ToArray();
        }

        private CommandExecution ExecuteCommand(IncomingMessage message)
        {
            if (string.Equals(message.Name, "game.quicksave.load", StringComparison.Ordinal))
                return LoadQuickSave();

            if (!HighLogic.LoadedSceneIsFlight || FlightGlobals.ActiveVessel == null)
                return CommandFailure("not_in_flight", "Command requires an active vessel in the Flight scene.");

            Vessel vessel = FlightGlobals.ActiveVessel;
            MechJebCore core = null;
            if (message.Name.StartsWith("mechjeb.", StringComparison.Ordinal))
            {
                core = vessel.GetMasterMechJeb();
                if (core == null) return CommandFailure("mechjeb_unavailable", "No MechJeb core is available on the active vessel.");
            }

            switch (message.Name)
            {
                case "vessel.partGroup.set":
                    return SetPartGroup(vessel, WireProtocol.ReadString(message.RawJson, "kind"),
                        WireProtocol.ReadBool(message.RawJson, "enabled", false));
                case "vessel.part.engine.setActive":
                    {
                        Part part = FindActivePart(vessel, message);
                        if (part == null) return CommandFailure("part_unavailable", "The selected part is no longer available.");
                        ModuleEngines engine = FindPartModule<ModuleEngines>(part, message);
                        if (engine == null) return CommandFailure("module_unavailable", "The selected engine is no longer available.");
                        bool enabled = WireProtocol.ReadBool(message.RawJson, "enabled", engine.EngineIgnited);
                        if (enabled)
                        {
                            engine.Activate();
                        }
                        else
                        {
                            if (engine.EngineIgnited && !engine.allowShutdown)
                                return CommandFailure("engine_shutdown_disabled", "This engine cannot be shut down.");
                            engine.Shutdown();
                        }
                        return CommandSuccess("Engine " + (enabled ? "activated." : "shut down."));
                    }
                case "vessel.part.engine.setThrustLimit":
                    {
                        Part part = FindActivePart(vessel, message);
                        ModuleEngines engine = part == null ? null : FindPartModule<ModuleEngines>(part, message);
                        if (engine == null) return CommandFailure("module_unavailable", "The selected engine is no longer available.");
                        double percent = WireProtocol.ReadDouble(message.RawJson, "percent", double.NaN);
                        if (!IsFinite(percent) || percent < 0 || percent > 100)
                            return CommandFailure("invalid_parameter", "Engine thrust limit must be between 0 and 100 percent.");
                        engine.thrustPercentage = (float)percent;
                        return CommandSuccess("Engine thrust limit set to " + percent.ToString("0.#") + " percent.");
                    }
                case "vessel.part.gimbal.set":
                    {
                        Part part = FindActivePart(vessel, message);
                        ModuleGimbal gimbal = part == null ? null : FindPartModule<ModuleGimbal>(part, message);
                        if (gimbal == null) return CommandFailure("module_unavailable", "The selected gimbal is no longer available.");
                        bool enabled = WireProtocol.ReadBool(message.RawJson, "enabled", !gimbal.gimbalLock);
                        gimbal.gimbalLock = !enabled;
                        return CommandSuccess("Engine gimbal " + (enabled ? "enabled." : "locked."));
                    }
                case "vessel.part.light.set":
                    {
                        Part part = FindActivePart(vessel, message);
                        ModuleLight light = part == null ? null : FindPartModule<ModuleLight>(part, message);
                        if (light == null) return CommandFailure("module_unavailable", "The selected light is no longer available.");
                        bool enabled = WireProtocol.ReadBool(message.RawJson, "enabled", light.isOn);
                        if (enabled) light.LightsOn(); else light.LightsOff();
                        return CommandSuccess("Light " + (enabled ? "switched on." : "switched off."));
                    }
                case "vessel.part.rcs.set":
                    {
                        Part part = FindActivePart(vessel, message);
                        ModuleRCS rcs = part == null ? null : FindPartModule<ModuleRCS>(part, message);
                        if (rcs == null) return CommandFailure("module_unavailable", "The selected RCS module is no longer available.");
                        bool enabled = WireProtocol.ReadBool(message.RawJson, "enabled", rcs.rcsEnabled);
                        rcs.rcsEnabled = enabled;
                        return CommandSuccess("RCS module " + (enabled ? "enabled." : "disabled."));
                    }
                case "vessel.part.cargo.set":
                    {
                        Part part = FindActivePart(vessel, message);
                        ModuleAnimateGeneric animation = part == null ? null : FindPartModule<ModuleAnimateGeneric>(part, message);
                        if (animation == null) return CommandFailure("module_unavailable", "The selected cargo bay is no longer available.");
                        bool open = WireProtocol.ReadBool(message.RawJson, "enabled", animation.animSwitch);
                        if (animation.animSwitch != open) animation.Toggle();
                        return CommandSuccess("Cargo bay " + (open ? "opening." : "closing."));
                    }
                case "vessel.part.gear.set":
                    {
                        Part part = FindActivePart(vessel, message);
                        int moduleIndex = ReadModuleIndex(message);
                        if (part == null || moduleIndex < 0 || moduleIndex >= part.Modules.Count)
                            return CommandFailure("module_unavailable", "The selected landing gear is no longer available.");
                        PartModule deployment = part.Modules[moduleIndex];
                        PartToggleSnapshot current = CaptureGearToggle(part);
                        bool deployed = WireProtocol.ReadBool(message.RawJson, "enabled", current != null && current.Enabled);
                        if (current == null) return CommandFailure("module_unavailable", "The selected landing gear is no longer available.");
                        if (!current.Transitioning && current.Enabled != deployed && !InvokeNoArgumentMethod(deployment, "EventToggle", "deploy"))
                            return CommandFailure("gear_control_unavailable", "This landing gear has no compatible deployment control.");
                        return CommandSuccess("Landing gear " + (deployed ? "deploying." : "retracting."));
                    }
                case "vessel.part.action":
                    return ExecutePartAction(vessel, message);
                case "vessel.crew.eva":
                    {
                        Part part = FindActivePart(vessel, message);
                        string crewName = WireProtocol.ReadString(message.RawJson, "crewName");
                        if (part == null || string.IsNullOrEmpty(crewName) || part.protoModuleCrew == null)
                            return CommandFailure("crew_unavailable", "The selected crew member is no longer aboard this vessel.");
                        ProtoCrewMember crewMember = null;
                        foreach (ProtoCrewMember candidate in part.protoModuleCrew)
                            if (candidate != null && string.Equals(candidate.name, crewName, StringComparison.Ordinal)) { crewMember = candidate; break; }
                        if (crewMember == null) return CommandFailure("crew_unavailable", "The selected crew member is no longer in that cabin.");
                        if (FlightEVA.fetch == null) return CommandFailure("eva_unavailable", "KSP EVA service is unavailable.");
                        KerbalEVA eva = FlightEVA.fetch.spawnEVA(crewMember, part, part.airlock, true);
                        if (eva == null) return CommandFailure("eva_failed", "EVA could not start; the hatch may be obstructed or conditions may be unsafe.");
                        return CommandSuccess(crewName + " started EVA.");
                    }
                case "target.clear":
                    if (FlightGlobals.fetch == null) return CommandFailure("target_unavailable", "KSP target controller is unavailable.");
                    FlightGlobals.fetch.SetVesselTarget(null, true);
                    return CommandSuccess("KSP target cleared.");
                case "target.set":
                    if (FlightGlobals.fetch == null) return CommandFailure("target_unavailable", "KSP target controller is unavailable.");
                    string targetKind = WireProtocol.ReadString(message.RawJson, "kind");
                    string targetBodyName = WireProtocol.ReadString(message.RawJson, "bodyName");
                    string targetVesselId = WireProtocol.ReadString(message.RawJson, "vesselId");
                    uint targetPartId = (uint)Math.Max(0, WireProtocol.ReadDouble(message.RawJson, "partId", 0));
                    ITargetable selectedTarget = null;
                    if (string.Equals(targetKind, "body", StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (CelestialBody body in FlightGlobals.Bodies)
                            if (body != null && string.Equals(body.bodyName, targetBodyName, StringComparison.OrdinalIgnoreCase)) { selectedTarget = body; break; }
                    }
                    else
                    {
                        Guid selectedVesselGuid;
                        Vessel selectedVessel = null;
                        if (Guid.TryParseExact(targetVesselId, "N", out selectedVesselGuid))
                            foreach (Vessel candidate in FlightGlobals.Vessels)
                                if (candidate != null && candidate.id == selectedVesselGuid) { selectedVessel = candidate; break; }
                        if (string.Equals(targetKind, "vessel", StringComparison.OrdinalIgnoreCase)) selectedTarget = selectedVessel;
                        else if (string.Equals(targetKind, "dockingPort", StringComparison.OrdinalIgnoreCase) && selectedVessel != null && selectedVessel.parts != null)
                            foreach (Part part in selectedVessel.parts)
                            {
                                if (part == null || part.flightID != targetPartId || part.Modules == null) continue;
                                foreach (PartModule module in part.Modules)
                                {
                                    ModuleDockingNode dockingNode = module as ModuleDockingNode;
                                    if (dockingNode != null) { selectedTarget = dockingNode; break; }
                                }
                                if (selectedTarget != null) break;
                            }
                    }
                    if (selectedTarget == null) return CommandFailure("invalid_target", "The selected KSP target is no longer available.");
                    FlightGlobals.fetch.SetVesselTarget(selectedTarget, true);
                    return CommandSuccess("KSP target set to " + selectedTarget.GetName() + ".");
                case "vessel.view.mode":
                    string mode = vesselImageRenderer == null ? null : vesselImageRenderer.SetMode(WireProtocol.ReadString(message.RawJson, "mode"));
                    if (mode == null) return CommandFailure("invalid_parameter", "Unknown vessel view mode.");
                    return CommandSuccess("Vessel view mode set to " + mode + ".");
                case "vessel.view.rotate":
                    if (vesselImageRenderer == null) return CommandFailure("renderer_unavailable", "Vessel image renderer is unavailable.");
                    string plane = vesselImageRenderer.CycleView();
                    vesselImageRenderer.RequestRedraw();
                    return CommandSuccess("Vessel view changed to " + plane + ".");
                case "vessel.view.fit":
                    if (vesselImageRenderer == null) return CommandFailure("renderer_unavailable", "Vessel image renderer is unavailable.");
                    vesselImageRenderer.Fit();
                    return CommandSuccess("Vessel view fitted to frame.");
                case "vessel.view.selectPart":
                    if (vesselImageRenderer == null) return CommandFailure("renderer_unavailable", "Vessel image renderer is unavailable.");
                    uint selectedPartId = (uint)Math.Max(0, WireProtocol.ReadDouble(message.RawJson, "partId", 0));
                    if (selectedPartId != 0 && FindActivePart(vessel, selectedPartId) == null)
                        return CommandFailure("part_unavailable", "The selected part is no longer available.");
                    vesselImageRenderer.SelectPart(selectedPartId == 0 ? (uint?)null : selectedPartId);
                    return CommandSuccess(selectedPartId == 0 ? "Vessel part highlight cleared." : "Vessel part highlighted.");
                case "mechjeb.envelope.set":
                    {
                        MechJebModuleThrustController thrustController = core.GetComputerModule<MechJebModuleThrustController>();
                        if (thrustController == null) return CommandFailure("thrust_controller_unavailable", "MechJeb thrust controller is unavailable.");
                        double maximumDynamicPressureKpa = WireProtocol.ReadDouble(message.RawJson, "maximumDynamicPressureKpa", thrustController.maxDynamicPressure.val / 1000d);
                        double maximumAcceleration = WireProtocol.ReadDouble(message.RawJson, "maximumAcceleration", thrustController.maxAcceleration.val);
                        double maximumThrottlePercent = WireProtocol.ReadDouble(message.RawJson, "maximumThrottlePercent", thrustController.maxThrottle.val * 100d);
                        double minimumThrottlePercent = WireProtocol.ReadDouble(message.RawJson, "minimumThrottlePercent", thrustController.minThrottle.val * 100d);
                        double flameoutSafetyPercent = WireProtocol.ReadDouble(message.RawJson, "flameoutSafetyPercent", thrustController.flameoutSafetyPct.val);
                        double smoothingTime = WireProtocol.ReadDouble(message.RawJson, "smoothingTime", thrustController.throttleSmoothingTime);
                        if (!IsFinite(maximumDynamicPressureKpa) || maximumDynamicPressureKpa < 0 || maximumDynamicPressureKpa > 100000)
                            return CommandFailure("invalid_parameter", "Maximum dynamic pressure must be between 0 and 100,000 kPa.");
                        if (!IsFinite(maximumAcceleration) || maximumAcceleration < 0 || maximumAcceleration > 10000)
                            return CommandFailure("invalid_parameter", "Maximum acceleration must be between 0 and 10,000 m/s².");
                        if (!IsFinite(maximumThrottlePercent) || maximumThrottlePercent < 0 || maximumThrottlePercent > 100
                            || !IsFinite(minimumThrottlePercent) || minimumThrottlePercent < 0 || minimumThrottlePercent > 100
                            || minimumThrottlePercent > maximumThrottlePercent)
                            return CommandFailure("invalid_parameter", "Throttle limits must satisfy 0 ≤ minimum ≤ maximum ≤ 100 percent.");
                        if (!IsFinite(flameoutSafetyPercent) || flameoutSafetyPercent < 0 || flameoutSafetyPercent > 100)
                            return CommandFailure("invalid_parameter", "Flameout safety margin must be between 0 and 100 percent.");
                        if (!IsFinite(smoothingTime) || smoothingTime < 0 || smoothingTime > 60)
                            return CommandFailure("invalid_parameter", "Throttle smoothing time must be between 0 and 60 seconds.");

                        thrustController.limitToTerminalVelocity = WireProtocol.ReadBool(message.RawJson, "terminalVelocity", thrustController.limitToTerminalVelocity);
                        thrustController.limitDynamicPressure = WireProtocol.ReadBool(message.RawJson, "dynamicPressure", thrustController.limitDynamicPressure);
                        thrustController.maxDynamicPressure.val = maximumDynamicPressureKpa * 1000d;
                        thrustController.limitAcceleration = WireProtocol.ReadBool(message.RawJson, "acceleration", thrustController.limitAcceleration);
                        thrustController.maxAcceleration.val = maximumAcceleration;
                        thrustController.limitThrottle = WireProtocol.ReadBool(message.RawJson, "maximumThrottle", thrustController.limitThrottle);
                        thrustController.maxThrottle.val = maximumThrottlePercent / 100d;
                        thrustController.limiterMinThrottle = WireProtocol.ReadBool(message.RawJson, "minimumThrottle", thrustController.limiterMinThrottle);
                        thrustController.minThrottle.val = minimumThrottlePercent / 100d;
                        thrustController.limitToPreventOverheats = WireProtocol.ReadBool(message.RawJson, "preventOverheat", thrustController.limitToPreventOverheats);
                        thrustController.limitToPreventFlameout = WireProtocol.ReadBool(message.RawJson, "preventFlameout", thrustController.limitToPreventFlameout);
                        thrustController.flameoutSafetyPct.val = flameoutSafetyPercent;
                        thrustController.limitToPreventUnstableIgnition = WireProtocol.ReadBool(message.RawJson, "preventUnstableIgnition", thrustController.limitToPreventUnstableIgnition);
                        thrustController.autoRCSUllaging = WireProtocol.ReadBool(message.RawJson, "autoRcsUllage", thrustController.autoRCSUllaging);
                        thrustController.smoothThrottle = WireProtocol.ReadBool(message.RawJson, "smoothThrottle", thrustController.smoothThrottle);
                        thrustController.throttleSmoothingTime = smoothingTime;
                        thrustController.manageIntakes = WireProtocol.ReadBool(message.RawJson, "manageIntakes", thrustController.manageIntakes);
                        thrustController.differentialThrottle = WireProtocol.ReadBool(message.RawJson, "differentialThrottle", thrustController.differentialThrottle);
                        MechJebModuleStagingController stagingController = core.GetComputerModule<MechJebModuleStagingController>();
                        if (stagingController != null)
                            stagingController.enabled = WireProtocol.ReadBool(message.RawJson, "autoStage", stagingController.enabled);
                        return CommandSuccess("MechJeb flight envelope applied.");
                    }
                case "trajectories.settings.set":
                    {
                        string trajectoriesMessage;
                        return TrajectoriesBridge.ApplySettings(message.RawJson, out trajectoriesMessage)
                            ? CommandSuccess(trajectoriesMessage)
                            : CommandFailure("trajectories_unavailable", trajectoriesMessage);
                    }
                case "trajectories.profile.set":
                    {
                        string trajectoriesMessage;
                        return TrajectoriesBridge.ApplyProfile(message.RawJson, out trajectoriesMessage)
                            ? CommandSuccess(trajectoriesMessage)
                            : CommandFailure("trajectories_unavailable", trajectoriesMessage);
                    }
                case "trajectories.target.set":
                    {
                        string trajectoriesMessage;
                        return TrajectoriesBridge.SetTarget(message.RawJson, out trajectoriesMessage)
                            ? CommandSuccess(trajectoriesMessage)
                            : CommandFailure("trajectories_unavailable", trajectoriesMessage);
                    }
                case "trajectories.target.clear":
                    {
                        string trajectoriesMessage;
                        return TrajectoriesBridge.ClearTarget(out trajectoriesMessage)
                            ? CommandSuccess(trajectoriesMessage)
                            : CommandFailure("trajectories_unavailable", trajectoriesMessage);
                    }
                case "trajectories.update":
                    {
                        string trajectoriesMessage;
                        return TrajectoriesBridge.UpdateNow(out trajectoriesMessage)
                            ? CommandSuccess(trajectoriesMessage)
                            : CommandFailure("trajectories_unavailable", trajectoriesMessage);
                    }
                case "mechjeb.landing.configure":
                    MechJebModuleLandingAutopilot landingToConfigure = core.GetComputerModule<MechJebModuleLandingAutopilot>();
                    if (landingToConfigure == null) return CommandFailure("landing_unavailable", "MechJeb landing autopilot is unavailable.");
                    ConfigureLanding(core, landingToConfigure, message);
                    return CommandSuccess("MechJeb landing settings updated.");
                case "mechjeb.landing.untargeted":
                    MechJebModuleLandingAutopilot untargetedLanding = core.GetComputerModule<MechJebModuleLandingAutopilot>();
                    if (untargetedLanding == null) return CommandFailure("landing_unavailable", "MechJeb landing autopilot is unavailable.");
                    ConfigureLanding(core, untargetedLanding, message);
                    untargetedLanding.LandUntargeted(this);
                    return CommandSuccess("MechJeb untargeted landing started.");
                case "mechjeb.landing.target":
                    MechJebModuleLandingAutopilot targetedLanding = core.GetComputerModule<MechJebModuleLandingAutopilot>();
                    if (targetedLanding == null) return CommandFailure("landing_unavailable", "MechJeb landing autopilot is unavailable.");
                    ConfigureLanding(core, targetedLanding, message);
                    targetedLanding.LandAtPositionTarget(this);
                    return CommandSuccess("MechJeb targeted landing started.");
                case "mechjeb.landing.setTarget":
                    double latitude = WireProtocol.ReadDouble(message.RawJson, "latitude", double.NaN);
                    double longitude = WireProtocol.ReadDouble(message.RawJson, "longitude", double.NaN);
                    if (double.IsNaN(latitude) || double.IsInfinity(latitude) || latitude < -90 || latitude > 90
                        || double.IsNaN(longitude) || double.IsInfinity(longitude) || longitude < -180 || longitude > 180)
                        return CommandFailure("invalid_parameter", "Latitude must be -90..90 and longitude -180..180 degrees.");
                    core.GetComputerModule<MechJebModuleTargetController>().SetPositionTarget(vessel.mainBody, latitude, longitude);
                    return CommandSuccess("MechJeb surface target updated.");
                case "mechjeb.landing.stop":
                    core.GetComputerModule<MechJebModuleLandingAutopilot>().StopLanding();
                    return CommandSuccess("MechJeb landing autopilot stopped.");
                case "mechjeb.docking.configure":
                case "mechjeb.docking.start":
                    MechJebModuleDockingAutopilot docking = core.GetComputerModule<MechJebModuleDockingAutopilot>();
                    if (docking == null) return CommandFailure("docking_unavailable", "MechJeb docking autopilot is unavailable.");
                    bool startDocking = message.Name == "mechjeb.docking.start";
                    docking.speedLimit.val = Math.Max(.01, WireProtocol.ReadDouble(message.RawJson, "speedLimit", docking.speedLimit.val));
                    docking.overrideSafeDistance = WireProtocol.ReadBool(message.RawJson, "overrideSafeDistance", docking.overrideSafeDistance);
                    docking.overridenSafeDistance.val = Math.Max(0, WireProtocol.ReadDouble(message.RawJson, "safeDistance", docking.overridenSafeDistance.val));
                    bool legacyOverrideStartDistance = WireProtocol.ReadBool(message.RawJson, "overrideTargetSize", docking.overrideTargetSize);
                    docking.overrideTargetSize = WireProtocol.ReadBool(message.RawJson, "overrideStartDistance", legacyOverrideStartDistance);
                    double legacyStartDistance = WireProtocol.ReadDouble(message.RawJson, "targetSize", docking.overridenTargetSize.val);
                    docking.overridenTargetSize.val = Math.Max(.1, WireProtocol.ReadDouble(message.RawJson, "startDistance", legacyStartDistance));
                    docking.forceRol = WireProtocol.ReadBool(message.RawJson, "forceRoll", docking.forceRol);
                    docking.rol.val = Math.Max(-180, Math.Min(180, WireProtocol.ReadDouble(message.RawJson, "roll", docking.rol.val)));
                    docking.drawBoundingBox = WireProtocol.ReadBool(message.RawJson, "drawBoundingBox", docking.drawBoundingBox);
                    // MJ normally copies the override edit fields during FixedUpdate. Update the effective
                    // fields immediately as well so configuration is observable even before the next tick.
                    if (docking.overrideSafeDistance) docking.safeDistance = (float)docking.overridenSafeDistance.val;
                    if (docking.overrideTargetSize) docking.targetSize = (float)docking.overridenTargetSize.val;
                    if (startDocking) docking.enabled = true;
                    return CommandSuccess(startDocking ? "MechJeb docking autopilot started." : "MechJeb docking settings updated.");
                case "mechjeb.docking.stop":
                    core.GetComputerModule<MechJebModuleDockingAutopilot>().enabled = false;
                    return CommandSuccess("MechJeb docking autopilot stopped.");
                case "mechjeb.rendezvous.start":
                    MechJebModuleTargetController rendezvousTarget = core.GetComputerModule<MechJebModuleTargetController>();
                    if (rendezvousTarget == null || !rendezvousTarget.NormalTargetExists || rendezvousTarget.TargetOrbit == null)
                        return CommandFailure("invalid_target", "Rendezvous autopilot requires an orbital target.");
                    MechJebModuleRendezvousAutopilot rendezvous = core.GetComputerModule<MechJebModuleRendezvousAutopilot>();
                    if (rendezvous == null) return CommandFailure("rendezvous_unavailable", "MechJeb rendezvous autopilot is unavailable.");
                    rendezvous.desiredDistance.val = Math.Max(1, WireProtocol.ReadDouble(message.RawJson, "desiredDistance", rendezvous.desiredDistance.val));
                    rendezvous.maxPhasingOrbits.val = Math.Max(1, WireProtocol.ReadDouble(message.RawJson, "maxPhasingOrbits", rendezvous.maxPhasingOrbits.val));
                    rendezvous.maxClosingSpeed.val = Math.Max(.01, WireProtocol.ReadDouble(message.RawJson, "maxClosingSpeed", rendezvous.maxClosingSpeed.val));
                    rendezvous.enabled = true;
                    return CommandSuccess("MechJeb rendezvous autopilot started.");
                case "mechjeb.rendezvous.stop":
                    MechJebModuleRendezvousAutopilot rendezvousToStop = core.GetComputerModule<MechJebModuleRendezvousAutopilot>();
                    if (rendezvousToStop == null) return CommandFailure("rendezvous_unavailable", "MechJeb rendezvous autopilot is unavailable.");
                    rendezvousToStop.enabled = false;
                    return CommandSuccess("MechJeb rendezvous autopilot stopped.");
                case "mechjeb.autowarp.set":
                    MechJebModuleNodeExecutor warpExecutor = core.GetComputerModule<MechJebModuleNodeExecutor>();
                    if (warpExecutor == null) return CommandFailure("node_executor_unavailable", "MechJeb node executor is unavailable.");
                    warpExecutor.autowarp = WireProtocol.ReadBool(message.RawJson, "enabled", warpExecutor.autowarp);
                    return CommandSuccess("MechJeb automatic time warp " + (warpExecutor.autowarp ? "enabled." : "disabled."));
                case "mechjeb.ascent.start":
                    MechJebModuleAscentAutopilot ascent = core.GetComputerModule<MechJebModuleAscentAutopilot>();
                    if (ascent == null) return CommandFailure("ascent_unavailable", "MechJeb ascent autopilot is unavailable.");
                    double orbitAltitudeKm = WireProtocol.ReadDouble(message.RawJson, "orbitAltitudeKm", 100);
                    double inclination = WireProtocol.ReadDouble(message.RawJson, "inclination", 0);
                    double maxAoA = WireProtocol.ReadDouble(message.RawJson, "maxAoA", 5);
                    double maxQKpa = WireProtocol.ReadDouble(message.RawJson, "maxQKpa", 40);
                    double countdown = WireProtocol.ReadDouble(message.RawJson, "countdown", 0);
                    string launchMode = (WireProtocol.ReadString(message.RawJson, "launchMode") ?? (countdown > 0 ? "COUNTDOWN" : "IMMEDIATE")).Trim().ToUpperInvariant();
                    // Compatibility for ArmorControl clients from before the MechJeb launch modes were separated.
                    if (launchMode == "TARGET_ORBIT") launchMode = "TARGET_PLANE";
                    double verticalRoll = WireProtocol.ReadDouble(message.RawJson, "verticalRoll", 0);
                    double turnRoll = WireProtocol.ReadDouble(message.RawJson, "turnRoll", 0);
                    string ascentPathName = WireProtocol.ReadString(message.RawJson, "ascentPath") ?? "GRAVITYTURN";
                    ascentType ascentPath;
                    if (!Enum.TryParse(ascentPathName, true, out ascentPath) || !Enum.IsDefined(typeof(ascentType), ascentPath))
                        return CommandFailure("invalid_parameter", "Unknown MechJeb ascent path.");
                    if (!IsFinite(orbitAltitudeKm) || orbitAltitudeKm < 1 || orbitAltitudeKm > 1000000)
                        return CommandFailure("invalid_parameter", "Orbit altitude must be between 1 and 1,000,000 km.");
                    if (!IsFinite(inclination) || inclination < -180 || inclination > 180)
                        return CommandFailure("invalid_parameter", "Inclination must be between -180 and 180 degrees.");
                    ascent.desiredOrbitAltitude.val = orbitAltitudeKm * 1000;
                    ascent.desiredInclination = inclination;
                    ascent.ascentPathIdxPublic = ascentPath;
                    ascent.autoThrottle = WireProtocol.ReadBool(message.RawJson, "autoThrottle", true);
                    ascent.correctiveSteering = WireProtocol.ReadBool(message.RawJson, "correctiveSteering", true);
                    ascent.correctiveSteeringGain.val = Math.Max(0, WireProtocol.ReadDouble(message.RawJson, "correctiveSteeringGain", ascent.correctiveSteeringGain.val));
                    ascent.autostage = WireProtocol.ReadBool(message.RawJson, "autoStage", true);
                    ascent.desiredLAN = Math.Max(-180, Math.Min(360, WireProtocol.ReadDouble(message.RawJson, "desiredLan", ascent.desiredLAN)));
                    ascent.autodeploySolarPanels = WireProtocol.ReadBool(message.RawJson, "deploySolarPanels", ascent.autodeploySolarPanels);
                    ascent.autoDeployAntennas = WireProtocol.ReadBool(message.RawJson, "deployAntennas", ascent.autoDeployAntennas);
                    ascent.limitAoA = WireProtocol.ReadBool(message.RawJson, "limitAoA", false);
                    ascent.maxAoA.val = Math.Max(0, Math.Min(90, maxAoA));
                    ascent.aoALimitFadeoutPressure.val = Math.Max(0, WireProtocol.ReadDouble(message.RawJson, "aoaFadeoutKpa", ascent.aoALimitFadeoutPressure.val / 1000)) * 1000;
                    ascent.limitQaEnabled = WireProtocol.ReadBool(message.RawJson, "limitQ", false);
                    ascent.limitQa.val = Math.Max(0, maxQKpa) * 1000;
                    ascent.forceRoll = WireProtocol.ReadBool(message.RawJson, "forceRoll", false);
                    ascent.verticalRoll.val = Math.Max(-180, Math.Min(180, verticalRoll));
                    ascent.turnRoll.val = Math.Max(-180, Math.Min(180, turnRoll));
                    ascent.rollAltitude.val = Math.Max(0, WireProtocol.ReadDouble(message.RawJson, "rollAltitudeKm", ascent.rollAltitude.val / 1000)) * 1000;
                    ascent.skipCircularization = WireProtocol.ReadBool(message.RawJson, "skipCircularization", false);
                    ascent.launchPhaseAngle.val = Math.Max(-360, Math.Min(360,
                        WireProtocol.ReadDouble(message.RawJson, "launchPhaseAngle", ascent.launchPhaseAngle.val)));
                    ascent.launchLANDifference.val = Math.Max(-360, Math.Min(360,
                        WireProtocol.ReadDouble(message.RawJson, "launchLanDifference", ascent.launchLANDifference.val)));

                    MechJebModuleAscentGT gt = core.GetComputerModule<MechJebModuleAscentGT>();
                    if (gt != null)
                    {
                        gt.turnStartAltitude.val = Math.Max(0, WireProtocol.ReadDouble(message.RawJson, "gtTurnStartAltitudeKm", gt.turnStartAltitude.val / 1000)) * 1000;
                        gt.turnStartVelocity.val = Math.Max(0, WireProtocol.ReadDouble(message.RawJson, "gtTurnStartVelocity", gt.turnStartVelocity.val));
                        gt.turnStartPitch.val = Math.Max(0, Math.Min(90, WireProtocol.ReadDouble(message.RawJson, "gtTurnStartPitch", gt.turnStartPitch.val)));
                        gt.intermediateAltitude.val = Math.Max(0, WireProtocol.ReadDouble(message.RawJson, "gtIntermediateAltitudeKm", gt.intermediateAltitude.val / 1000)) * 1000;
                        gt.holdAPTime.val = Math.Max(0, WireProtocol.ReadDouble(message.RawJson, "gtHoldApTime", gt.holdAPTime.val));
                    }
                    MechJebModuleAscentClassic classic = core.GetComputerModule<MechJebModuleAscentClassic>();
                    if (classic != null)
                    {
                        classic.autoPath = WireProtocol.ReadBool(message.RawJson, "classicAutoPath", classic.autoPath);
                        classic.turnStartAltitude.val = Math.Max(0, WireProtocol.ReadDouble(message.RawJson, "classicTurnStartAltitudeKm", classic.turnStartAltitude.val / 1000)) * 1000;
                        classic.turnStartVelocity.val = Math.Max(0, WireProtocol.ReadDouble(message.RawJson, "classicTurnStartVelocity", classic.turnStartVelocity.val));
                        classic.turnEndAltitude.val = Math.Max(0, WireProtocol.ReadDouble(message.RawJson, "classicTurnEndAltitudeKm", classic.turnEndAltitude.val / 1000)) * 1000;
                        classic.turnEndAngle.val = Math.Max(0, Math.Min(90, WireProtocol.ReadDouble(message.RawJson, "classicTurnEndAngle", classic.turnEndAngle.val)));
                        classic.turnShapeExponent.val = Math.Max(.01, WireProtocol.ReadDouble(message.RawJson, "classicTurnShapeExponent", classic.turnShapeExponent.val));
                    }
                    MechJebModuleAscentPVG pvg = core.GetComputerModule<MechJebModuleAscentPVG>();
                    if (pvg != null)
                    {
                        double pvgApoapsisKm = WireProtocol.ReadDouble(message.RawJson, "pvgDesiredApoapsisKm", pvg.DesiredApoapsis.val / 1000);
                        if (ascentPath == ascentType.PVG && (!IsFinite(pvgApoapsisKm) || pvgApoapsisKm < orbitAltitudeKm))
                            return CommandFailure("invalid_parameter", "PVG apoapsis must be at or above its periapsis.");
                        pvg.PitchStartVelocity.val = Math.Max(0, WireProtocol.ReadDouble(message.RawJson, "pvgPitchStartVelocity", pvg.PitchStartVelocity.val));
                        pvg.PitchRate.val = Math.Max(0, WireProtocol.ReadDouble(message.RawJson, "pvgPitchRate", pvg.PitchRate.val));
                        pvg.DesiredApoapsis.val = Math.Max(0, pvgApoapsisKm) * 1000;
                        pvg.AttachAltFlag = WireProtocol.ReadBool(message.RawJson, "pvgAttachAltitudeEnabled", pvg.AttachAltFlag);
                        pvg.DesiredAttachAlt.val = Math.Max(0, WireProtocol.ReadDouble(message.RawJson, "pvgDesiredAttachAltitudeKm", pvg.DesiredAttachAlt.val / 1000)) * 1000;
                        pvg.DynamicPressureTrigger.val = Math.Max(0, WireProtocol.ReadDouble(message.RawJson, "pvgDynamicPressureTriggerKpa", pvg.DynamicPressureTrigger.val / 1000)) * 1000;
                        pvg.StagingTriggerFlag = WireProtocol.ReadBool(message.RawJson, "pvgStagingTriggerEnabled", pvg.StagingTriggerFlag);
                        pvg.StagingTrigger.val = (int)Math.Max(0, Math.Min(99, WireProtocol.ReadDouble(message.RawJson, "pvgStagingTrigger", pvg.StagingTrigger.val)));
                        pvg.FixedCoast = WireProtocol.ReadBool(message.RawJson, "pvgFixedCoast", pvg.FixedCoast);
                        pvg.FixedCoastLength.val = Math.Max(0, WireProtocol.ReadDouble(message.RawJson, "pvgFixedCoastLength", pvg.FixedCoastLength.val));
                    }
                    MechJebModuleAscentGuidance guidance = core.GetComputerModule<MechJebModuleAscentGuidance>();
                    ClearAscentLaunchFlags(guidance);
                    ascent.timedLaunch = false;
                    double launchDelay = 0;
                    if (launchMode == "COUNTDOWN")
                    {
                        if (!IsFinite(countdown) || countdown < 0 || countdown > 86400)
                            return CommandFailure("invalid_parameter", "Launch countdown must be between 0 and 86,400 seconds.");
                        launchDelay = countdown;
                    }
                    else if (launchMode == "TARGET_PLANE" || launchMode == "TARGET_LAN" || launchMode == "RENDEZVOUS")
                    {
                        if (!vessel.LandedOrSplashed)
                            return CommandFailure("invalid_state", "MechJeb target-orbit launch timing requires a landed or splashed vessel.");
                        MechJebModuleTargetController launchTarget = core.GetComputerModule<MechJebModuleTargetController>();
                        Orbit targetOrbit = launchTarget == null ? null : launchTarget.TargetOrbit;
                        if (launchTarget == null || !launchTarget.NormalTargetExists || targetOrbit == null || targetOrbit.referenceBody != vessel.mainBody)
                            return CommandFailure("invalid_target", "Select a vessel or object orbiting the launch body before using target-orbit launch.");
                        if (guidance == null) return CommandFailure("ascent_guidance_unavailable", "MechJeb ascent guidance window controls are unavailable.");
                        if (launchMode == "RENDEZVOUS")
                        {
                            if (ascentPath == ascentType.PVG)
                                return CommandFailure("invalid_parameter", "MechJeb 2.14.3 does not support launch-to-rendezvous with PVG guidance.");
                            launchDelay = LaunchTiming.TimeToPhaseAngle(ascent.launchPhaseAngle.val, vessel.mainBody, vessel.longitude, targetOrbit);
                            guidance.launchingToRendezvous = true;
                        }
                        else if (launchMode == "TARGET_LAN")
                        {
                            if (ascentPath != ascentType.PVG)
                                return CommandFailure("invalid_parameter", "MechJeb 2.14.3 exposes launch-to-target-LAN only with PVG guidance.");
                            if (!TryTimeToPlane(vessel.mainBody.rotationPeriod, vessel.latitude, vessel.longitude,
                                targetOrbit.LAN - ascent.launchLANDifference.val, ascent.desiredInclination, false,
                                out launchDelay, out inclination))
                                return CommandFailure("no_solution", "MechJeb could not calculate the target LAN launch window.");
                            guidance.launchingToMatchLAN = true;
                        }
                        else // TARGET_PLANE
                        {
                            if (!TryTimeToPlane(vessel.mainBody.rotationPeriod, vessel.latitude, vessel.longitude,
                                targetOrbit.LAN - ascent.launchLANDifference.val, targetOrbit.inclination, true,
                                out launchDelay, out inclination))
                                return CommandFailure("no_solution", "MechJeb could not calculate the target orbital-plane launch window.");
                            ascent.desiredInclination = inclination;
                            guidance.launchingToPlane = true;
                        }
                    }
                    else if (launchMode != "IMMEDIATE")
                    {
                        return CommandFailure("invalid_parameter", "Unknown MechJeb launch timing mode.");
                    }
                    if (!IsFinite(launchDelay) || launchDelay < 0)
                    {
                        ClearAscentLaunchFlags(guidance);
                        return CommandFailure("no_solution", "MechJeb returned an invalid launch window.");
                    }
                    if (launchMode != "IMMEDIATE") ascent.StartCountdown(Planetarium.GetUniversalTime() + launchDelay);
                    ascent.enabled = true;
                    return CommandSuccess(launchMode == "TARGET_PLANE" ? "MechJeb target orbital-plane launch window armed."
                        : launchMode == "TARGET_LAN" ? "MechJeb target-LAN launch window armed."
                        : launchMode == "RENDEZVOUS" ? "MechJeb rendezvous-phase launch window armed."
                        : "MechJeb ascent autopilot started.");
                case "mechjeb.ascent.stop":
                    MechJebModuleAscentAutopilot ascentToStop = core.GetComputerModule<MechJebModuleAscentAutopilot>();
                    if (ascentToStop == null) return CommandFailure("ascent_unavailable", "MechJeb ascent autopilot is unavailable.");
                    ascentToStop.timedLaunch = false;
                    ClearAscentLaunchFlags(core.GetComputerModule<MechJebModuleAscentGuidance>());
                    ascentToStop.enabled = false;
                    return CommandSuccess("MechJeb ascent autopilot stopped.");
                case "mechjeb.node.executeOne":
                    core.GetComputerModule<MechJebModuleNodeExecutor>().ExecuteOneNode(this);
                    return CommandSuccess("MechJeb is executing the next maneuver node.");
                case "mechjeb.node.executeAll":
                    core.GetComputerModule<MechJebModuleNodeExecutor>().ExecuteAllNodes(this);
                    return CommandSuccess("MechJeb is executing all maneuver nodes.");
                case "mechjeb.node.abort":
                    core.GetComputerModule<MechJebModuleNodeExecutor>().Abort();
                    return CommandSuccess("MechJeb node execution aborted.");
                case "mechjeb.node.removeAll":
                    if (vessel.patchedConicSolver == null) return CommandFailure("solver_unavailable", "Patched conic solver is unavailable.");
                    while (vessel.patchedConicSolver.maneuverNodes.Count > 0)
                    {
                        vessel.patchedConicSolver.RemoveManeuverNode(vessel.patchedConicSolver.maneuverNodes[0]);
                    }
                    return CommandSuccess("All maneuver nodes removed.");
                case "mechjeb.maneuver.create":
                    return CreateManeuver(vessel, core, message);
                case "mechjeb.porkchop.solve":
                    return StartPorkchop(vessel, core, message);
                case "mechjeb.porkchop.create":
                    return CreatePorkchopManeuver(vessel, core, message);
                case "mechjeb.recorder.mark":
                case "mechjeb.recorder.clear":
                    MechJebModuleFlightRecorder recorder = core.GetComputerModule<MechJebModuleFlightRecorder>();
                    if (recorder == null) return CommandFailure("recorder_unavailable", "MechJeb Flight Recorder is unavailable.");
                    recorder.Mark();
                    recorderSource = recorder;
                    recorderSourceIndex = 0;
                    bridge.ResetRecorder();
                    return CommandSuccess("MechJeb Flight Recorder history cleared; current time is T+0.");
                case "mechjeb.smartass.off":
                    MechJebModuleSmartASS smartAssToStop = core.GetComputerModule<MechJebModuleSmartASS>();
                    smartAssToStop.target = MechJebModuleSmartASS.Target.OFF;
                    smartAssToStop.mode = MechJebModuleSmartASS.Target2Mode[(int)MechJebModuleSmartASS.Target.OFF];
                    smartAssToStop.Engage(true);
                    return CommandSuccess("Smart A.S.S. disengaged.");
                case "mechjeb.smartass.set":
                    string targetName = WireProtocol.ReadString(message.RawJson, "target");
                    MechJebModuleSmartASS.Target target;
                    if (!Enum.TryParse(targetName, true, out target) || !Enum.IsDefined(typeof(MechJebModuleSmartASS.Target), target))
                        return CommandFailure("invalid_parameter", "Unknown Smart A.S.S. target.");
                    MechJebModuleSmartASS smartAss = core.GetComputerModule<MechJebModuleSmartASS>();
                    smartAss.target = target;
                    smartAss.mode = MechJebModuleSmartASS.Target2Mode[(int)target];
                    double pitchOffset = WireProtocol.ReadDouble(message.RawJson, "pitchOffset", 0);
                    double yawOffset = WireProtocol.ReadDouble(message.RawJson, "yawOffset", 0);
                    double rollOffset = WireProtocol.ReadDouble(message.RawJson, "rollOffset", 0);
                    smartAss.autoDisableSmartASS = WireProtocol.ReadBool(message.RawJson, "autoDisable", smartAss.autoDisableSmartASS);
                    if (target == MechJebModuleSmartASS.Target.SURFACE)
                    {
                        smartAss.srfPit = pitchOffset;
                        smartAss.srfHdg = yawOffset;
                        smartAss.srfRol = rollOffset;
                        smartAss.forcePitch = smartAss.forceYaw = smartAss.forceRol = true;
                    }
                    else if (target == MechJebModuleSmartASS.Target.SURFACE_PROGRADE || target == MechJebModuleSmartASS.Target.SURFACE_RETROGRADE)
                    {
                        smartAss.srfVelPit = pitchOffset;
                        smartAss.srfVelYaw = yawOffset;
                        smartAss.srfVelRol = rollOffset;
                        smartAss.forcePitch = smartAss.forceYaw = smartAss.forceRol = true;
                    }
                    else
                    {
                        smartAss.rol.val = rollOffset;
                        smartAss.forceRol = Math.Abs(rollOffset) > .0001;
                    }
                    smartAss.Engage(true);
                    return CommandSuccess("Smart A.S.S. target set to " + target + ".");
                case "vessel.system.toggle":
                    string system = WireProtocol.ReadString(message.RawJson, "system");
                    KSPActionGroup group;
                    if (!TrySystemActionGroup(system, out group)) return CommandFailure("invalid_parameter", "Unknown vessel system.");
                    vessel.ActionGroups.ToggleGroup(group);
                    return CommandSuccess(system + " toggled.");
                case "vessel.actionGroup.toggle":
                    int actionGroup = (int)WireProtocol.ReadDouble(message.RawJson, "group", 0);
                    if (actionGroup < 1 || actionGroup > 10) return CommandFailure("invalid_parameter", "Action group must be 1 through 10.");
                    vessel.ActionGroups.ToggleGroup(CustomActionGroup(actionGroup));
                    return CommandSuccess("Action group " + actionGroup + " toggled.");
                case "vessel.stage":
                    KSP.UI.Screens.StageManager.ActivateNextStage();
                    return CommandSuccess("Next stage activated.");
                case "vessel.solar.toggle":
                    ToggleDeployables(vessel.FindPartModulesImplementing<ModuleDeployableSolarPanel>());
                    return CommandSuccess("Solar panels toggled.");
                case "vessel.antenna.toggle":
                    ToggleDeployables(vessel.FindPartModulesImplementing<ModuleDeployableAntenna>());
                    return CommandSuccess("Antennas toggled.");
                case "vessel.control.set":
                    remotePitch = Mathf.Clamp((float)WireProtocol.ReadDouble(message.RawJson, "pitch", 0), -1f, 1f);
                    remoteYaw = Mathf.Clamp((float)WireProtocol.ReadDouble(message.RawJson, "yaw", 0), -1f, 1f);
                    remoteRoll = Mathf.Clamp((float)WireProtocol.ReadDouble(message.RawJson, "roll", 0), -1f, 1f);
                    bool mechJebOwnsThrottle = IsMechJebBurnAutopilotActive(vessel);
                    remoteThrottle = mechJebOwnsThrottle
                        ? 0
                        : Mathf.Clamp01((float)WireProtocol.ReadDouble(message.RawJson, "throttle", 0));
                    float holdMilliseconds = Mathf.Clamp((float)WireProtocol.ReadDouble(message.RawJson, "holdMilliseconds", 350), 100f, 1000f);
                    AttachRemoteControl(vessel);
                    remoteControlExpiresAt = Time.realtimeSinceStartup + holdMilliseconds / 1000f;
                    return CommandSuccess(mechJebOwnsThrottle
                        ? "Continuous attitude control updated; throttle is locked by the active MechJeb burn autopilot."
                        : "Continuous vessel control updated.");
                case "vessel.control.release":
                    ReleaseRemoteControl(true);
                    return CommandSuccess("Continuous vessel control released.");
                case "mechjeb.autostage.toggle":
                    MechJebModuleStagingController staging = core.GetComputerModule<MechJebModuleStagingController>();
                    staging.enabled = !staging.enabled;
                    return CommandSuccess("MechJeb autostaging " + (staging.enabled ? "enabled." : "disabled."));
                default:
                    return CommandFailure("unknown_command", "Unknown ArmorControl command: " + message.Name);
            }
        }

        private static void ClearAscentLaunchFlags(MechJebModuleAscentGuidance guidance)
        {
            if (guidance == null) return;
            guidance.launchingToPlane = false;
            guidance.launchingToRendezvous = false;
            guidance.launchingToMatchLAN = false;
            guidance.launchingToLAN = false;
        }

        private static bool TryTimeToPlane(double rotationPeriod, double latitude, double longitude,
            double lan, double inclination, bool chooseMinimumPlane, out double delay, out double resolvedInclination)
        {
            delay = double.NaN;
            resolvedInclination = inclination;
            try
            {
                Type functions = typeof(MechJebCore).Assembly.GetType("MechJebLib.Maths.Functions", false);
                if (functions == null) return false;
                if (!chooseMinimumPlane)
                {
                    MethodInfo method = functions.GetMethod("TimeToPlane", BindingFlags.Public | BindingFlags.Static);
                    if (method == null) return false;
                    delay = Convert.ToDouble(method.Invoke(null, new object[] { rotationPeriod, latitude, longitude, lan, inclination }));
                    return IsFinite(delay) && delay >= 0;
                }

                MethodInfo minimum = functions.GetMethod("MinimumTimeToPlane", BindingFlags.Public | BindingFlags.Static);
                if (minimum == null) return false;
                object result = minimum.Invoke(null, new object[] { rotationPeriod, latitude, longitude, lan, inclination });
                if (result == null) return false;
                FieldInfo item1 = result.GetType().GetField("Item1");
                FieldInfo item2 = result.GetType().GetField("Item2");
                if (item1 == null || item2 == null) return false;
                delay = Convert.ToDouble(item1.GetValue(result));
                resolvedInclination = Convert.ToDouble(item2.GetValue(result));
                return IsFinite(delay) && delay >= 0 && IsFinite(resolvedInclination);
            }
            catch
            {
                return false;
            }
        }

        private static Part FindActivePart(Vessel vessel, IncomingMessage message)
        {
            uint partId = (uint)Math.Max(0, WireProtocol.ReadDouble(message.RawJson, "partId", 0));
            return FindActivePart(vessel, partId);
        }

        private static Part FindActivePart(Vessel vessel, uint partId)
        {
            if (partId == 0 || vessel == null || vessel.parts == null) return null;
            foreach (Part part in vessel.parts)
                if (part != null && part.flightID == partId) return part;
            return null;
        }

        private static int ReadModuleIndex(IncomingMessage message)
        {
            double value = WireProtocol.ReadDouble(message.RawJson, "moduleIndex", -1);
            return IsFinite(value) ? (int)value : -1;
        }

        private static T FindPartModule<T>(Part part, IncomingMessage message) where T : PartModule
        {
            int index = ReadModuleIndex(message);
            return part == null || part.Modules == null || index < 0 || index >= part.Modules.Count
                ? null : part.Modules[index] as T;
        }

        private static bool InvokeNoArgumentMethod(object target, params string[] names)
        {
            if (target == null) return false;
            foreach (string name in names)
            {
                MethodInfo method = target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null, Type.EmptyTypes, null);
                if (method == null) continue;
                method.Invoke(target, null);
                return true;
            }
            return false;
        }

        private static CommandExecution ExecutePartAction(Vessel vessel, IncomingMessage message)
        {
            Part part = FindActivePart(vessel, message);
            int moduleIndex = ReadModuleIndex(message);
            if (part == null) return CommandFailure("part_unavailable", "The selected part is no longer available.");
            if (part.Modules == null || moduleIndex < 0 || moduleIndex >= part.Modules.Count)
                return CommandFailure("module_unavailable", "The selected part module is no longer available.");
            PartModule module = part.Modules[moduleIndex];
            if (module == null) return CommandFailure("module_unavailable", "The selected part module is no longer available.");
            string action = WireProtocol.ReadString(message.RawJson, "action") ?? string.Empty;
            if (action.StartsWith("partEvent.", StringComparison.Ordinal))
                return PartFlightEvents.Execute(module, action)
                    ? CommandSuccess("Part flight event executed.")
                    : CommandFailure("action_unavailable", "This part action is not currently available in flight.");

            ModuleGenerator generator = module as ModuleGenerator;
            if (action == "generator.start" || action == "generator.stop")
            {
                if (generator == null || generator.isAlwaysActive)
                    return CommandFailure("action_unavailable", "This generator cannot be switched remotely.");
                if (action == "generator.start") generator.Activate(); else generator.Shutdown();
                return CommandSuccess(action == "generator.start" ? "Generator started." : "Generator stopped.");
            }

            ModuleResourceConverter converter = module as ModuleResourceConverter;
            if (action == "converter.start" || action == "converter.stop")
            {
                if (converter == null || converter.AlwaysActive)
                    return CommandFailure("action_unavailable", "This resource converter cannot be switched remotely.");
                if (action == "converter.start") converter.StartResourceConverter(); else converter.StopResourceConverter();
                return CommandSuccess(action == "converter.start" ? "Resource converter started." : "Resource converter stopped.");
            }

            ModuleParachute parachute = module as ModuleParachute;
            if (action == "parachute.deploy" || action == "parachute.disarm" || action == "parachute.cut")
            {
                if (parachute == null) return CommandFailure("action_unavailable", "The selected parachute is unavailable.");
                if (action == "parachute.deploy") parachute.Deploy();
                else if (action == "parachute.disarm") parachute.Disarm();
                else parachute.CutParachute();
                return CommandSuccess("Parachute command executed.");
            }

            if (action == "reactor.enable" || action == "reactor.disable")
            {
                if (!ContainsIgnoreCase(module.moduleName, "reactor"))
                    return CommandFailure("action_unavailable", "The selected module is not a supported reactor.");
                bool invoked = action == "reactor.enable"
                    ? InvokeNoArgumentMethod(module, "EnableReactor", "StartResourceConverter")
                    : InvokeNoArgumentMethod(module, "DisableReactor", "StopResourceConverter");
                if (!invoked) return CommandFailure("action_unavailable", "This reactor has no compatible remote control event.");
                return CommandSuccess(action == "reactor.enable" ? "Reactor started." : "Reactor stopped.");
            }

            if (action == "b9.switch")
            {
                if (!string.Equals(module.moduleName, "ModuleB9PartSwitch", StringComparison.Ordinal)
                    || !B9SwitchPolicy.CanSwitchFrom(module))
                    return CommandFailure("action_unavailable", "This B9 switcher cannot be changed in flight.");
                string option = WireProtocol.ReadString(message.RawJson, "value");
                bool valid = false;
                System.Collections.IEnumerable subtypes = ReadMember(module, "subtypes") as System.Collections.IEnumerable;
                if (subtypes != null)
                {
                    foreach (object subtype in subtypes)
                    {
                        string name = ReadMemberString(subtype, "subtypeName") ?? ReadMemberString(subtype, "Name");
                        if (string.Equals(name, option, StringComparison.Ordinal) && B9SwitchPolicy.CanSwitchTo(subtype)) { valid = true; break; }
                    }
                }
                if (!valid) return CommandFailure("invalid_parameter", "The requested B9 subtype is unavailable.");
                MethodInfo switchSubtype = module.GetType().GetMethod("SwitchSubtype",
                    BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(string) }, null);
                if (switchSubtype == null) return CommandFailure("action_unavailable", "B9 subtype switching is unavailable.");
                switchSubtype.Invoke(module, new object[] { option });
                return CommandSuccess("B9 part subtype changed.");
            }

            if (action == "control.fromHere")
            {
                if (!(module is ModuleCommand) && !(module is ModuleDockingNode))
                    return CommandFailure("action_unavailable", "This module cannot become the vessel control reference.");
                ModuleDockingNode dockingReference = module as ModuleDockingNode;
                if (dockingReference != null) dockingReference.MakeReferenceTransform();
                else ((ModuleCommand)module).MakeReference();
                return CommandSuccess("Vessel control reference changed.");
            }

            return CommandFailure("action_unavailable", "The requested part action is not supported.");
        }

        private static CommandExecution SetPartGroup(Vessel vessel, string kind, bool enabled)
        {
            if (vessel == null || vessel.parts == null) return CommandFailure("vessel_unavailable", "No active vessel is available.");
            int changed = 0;
            foreach (Part part in vessel.parts)
            {
                if (part == null || part.Modules == null) continue;
                if (string.Equals(kind, "cargo", StringComparison.Ordinal))
                {
                    string title = part.partInfo == null ? part.name : part.partInfo.title;
                    PartToggleSnapshot cargo = CaptureCargoToggle(part, title);
                    if (cargo != null && cargo.Enabled != enabled && !cargo.Transitioning)
                    {
                        ModuleAnimateGeneric animation = part.Modules[cargo.ModuleIndex] as ModuleAnimateGeneric;
                        if (animation != null) { animation.Toggle(); changed++; }
                    }
                    continue;
                }
                if (string.Equals(kind, "gear", StringComparison.Ordinal))
                {
                    PartToggleSnapshot gear = CaptureGearToggle(part);
                    if (gear != null && gear.Enabled != enabled && !gear.Transitioning
                        && InvokeNoArgumentMethod(part.Modules[gear.ModuleIndex], "EventToggle", "deploy")) changed++;
                    continue;
                }
                foreach (PartModule module in part.Modules)
                {
                    if (module == null) continue;
                    if (string.Equals(kind, "engine", StringComparison.Ordinal))
                    {
                        ModuleEngines engine = module as ModuleEngines;
                        if (engine == null || engine.EngineIgnited == enabled) continue;
                        if (enabled) { engine.Activate(); changed++; }
                        else if (engine.allowShutdown) { engine.Shutdown(); changed++; }
                    }
                    else if (string.Equals(kind, "rcs", StringComparison.Ordinal))
                    {
                        ModuleRCS rcs = module as ModuleRCS;
                        if (rcs != null && rcs.rcsEnabled != enabled) { rcs.rcsEnabled = enabled; changed++; }
                    }
                    else if (string.Equals(kind, "light", StringComparison.Ordinal))
                    {
                        ModuleLight light = module as ModuleLight;
                        if (light == null || light.isOn == enabled) continue;
                        if (enabled) light.LightsOn(); else light.LightsOff();
                        changed++;
                    }
                }
            }
            if (kind != "engine" && kind != "rcs" && kind != "light" && kind != "gear" && kind != "cargo")
                return CommandFailure("invalid_parameter", "Unknown controllable part group.");
            return CommandSuccess("Part group " + kind + " updated; " + changed + " module(s) changed.");
        }

        private static CommandExecution LoadQuickSave()
        {
            string saveFolder = HighLogic.SaveFolder;
            string savesRoot = Path.GetFullPath(Path.Combine(KSPUtil.ApplicationRootPath, "saves"));
            string activeQuickSave = string.IsNullOrWhiteSpace(saveFolder) ? null : Path.Combine(savesRoot, saveFolder, "quicksave.sfs");
            if (HighLogic.LoadedScene == GameScenes.MAINMENU || string.IsNullOrWhiteSpace(saveFolder) || !File.Exists(activeQuickSave))
            {
                DateTime latestWrite = DateTime.MinValue;
                string latestFolder = null;
                foreach (string folder in Directory.GetDirectories(savesRoot))
                {
                    string candidate = Path.Combine(folder, "quicksave.sfs");
                    if (!File.Exists(candidate)) continue;
                    DateTime write = File.GetLastWriteTimeUtc(candidate);
                    if (write > latestWrite)
                    {
                        latestWrite = write;
                        latestFolder = Path.GetFileName(folder);
                    }
                }
                saveFolder = latestFolder;
            }
            if (string.IsNullOrWhiteSpace(saveFolder))
                return CommandFailure("save_unavailable", "No KSP save containing quicksave.sfs is available.");

            string quickSavePath = Path.GetFullPath(Path.Combine(savesRoot, saveFolder, "quicksave.sfs"));
            string savesPrefix = savesRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            if (!quickSavePath.StartsWith(savesPrefix, StringComparison.OrdinalIgnoreCase))
                return CommandFailure("invalid_save_folder", "The active save folder is outside the KSP saves directory.");
            if (!File.Exists(quickSavePath))
                return CommandFailure("quicksave_missing", "The active save does not contain quicksave.sfs.");

            // QuickSaveLoad is a flight-scene singleton and is not available at the main menu.
            // Load the same persistent Game object directly there, then let FlightDriver enter
            // flight and focus the vessel recorded by the quicksave.
            if (HighLogic.LoadedScene == GameScenes.MAINMENU)
            {
                try
                {
                    ConfigNode saveNode = GamePersistence.LoadSFSFile("quicksave", saveFolder);
                    if (saveNode == null)
                        return CommandFailure("quickload_failed", "KSP could not read the selected quicksave.");

                    Game game = GamePersistence.LoadGameCfg(saveNode, "quicksave", true, false);
                    if (game == null || game.flightState == null || !game.compatible)
                        return CommandFailure("quickload_failed", "KSP could not read the selected quicksave.");

                    // Match QuickSaveLoad.onQuickloadPipelineFinished. Scenario modules and
                    // the post-load event must be prepared before FlightDriver consumes the
                    // cached Game; omitting these leaves Game.Load without initialized state.
                    HighLogic.SaveFolder = saveFolder;
                    GamePersistence.UpdateScenarioModules(game);
                    GameEvents.onGameStatePostLoad.Fire(saveNode);
                    // Game.Load reads CurrentGame.Mode before assigning its receiver to
                    // CurrentGame. The flight-scene quickloader already has one, while a
                    // main-menu direct load must establish it explicitly.
                    HighLogic.CurrentGame = game;
                    FlightDriver.StartAndFocusVessel(game, game.flightState.activeVesselIdx);
                    return CommandSuccess("KSP quicksave load requested from the main menu.");
                }
                catch (Exception exception)
                {
                    return CommandFailure("quickload_failed", exception.Message);
                }
            }

            if (QuickSaveLoad.fetch == null)
                return CommandFailure("quickload_unavailable", "KSP QuickSaveLoad is not ready.");

            MethodInfo quickLoad = typeof(QuickSaveLoad).GetMethod(
                "quickLoad",
                BindingFlags.Instance | BindingFlags.NonPublic,
                null,
                new[] { typeof(string), typeof(string) },
                null);
            if (quickLoad == null)
                return CommandFailure("quickload_unavailable", "KSP 1.12.5 quickload API was not found.");

            try
            {
                quickLoad.Invoke(QuickSaveLoad.fetch, new object[] { "quicksave", saveFolder });
                return CommandSuccess("KSP quicksave load requested.");
            }
            catch (TargetInvocationException exception)
            {
                Exception cause = exception.InnerException ?? exception;
                return CommandFailure("quickload_failed", cause.Message);
            }
            catch (Exception exception)
            {
                return CommandFailure("quickload_failed", exception.Message);
            }
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static bool TrySystemActionGroup(string system, out KSPActionGroup group)
        {
            switch (system)
            {
                case "sas": group = KSPActionGroup.SAS; return true;
                case "rcs": group = KSPActionGroup.RCS; return true;
                case "gear": group = KSPActionGroup.Gear; return true;
                case "brakes": group = KSPActionGroup.Brakes; return true;
                case "lights": group = KSPActionGroup.Light; return true;
                default: group = KSPActionGroup.None; return false;
            }
        }

        private static KSPActionGroup CustomActionGroup(int index)
        {
            return (KSPActionGroup)((int)KSPActionGroup.Custom01 << (index - 1));
        }

        private static string DeploymentStatus<T>(List<T> modules) where T : ModuleDeployablePart
        {
            if (modules == null || modules.Count == 0) return "unavailable";
            bool extended = false;
            bool retracted = false;
            bool moving = false;
            foreach (T module in modules)
            {
                extended |= module.deployState == ModuleDeployablePart.DeployState.EXTENDED;
                retracted |= module.deployState == ModuleDeployablePart.DeployState.RETRACTED;
                moving |= module.deployState != ModuleDeployablePart.DeployState.EXTENDED
                    && module.deployState != ModuleDeployablePart.DeployState.RETRACTED;
            }
            return moving ? "moving" : extended && retracted ? "mixed" : extended ? "extended" : retracted ? "retracted" : "moving";
        }

        private static void ToggleDeployables<T>(List<T> modules) where T : ModuleDeployablePart
        {
            if (modules == null || modules.Count == 0) return;
            bool deploy = false;
            foreach (T module in modules)
                if (module.deployState == ModuleDeployablePart.DeployState.RETRACTED) { deploy = true; break; }
            foreach (T module in modules)
            {
                if (deploy) module.Extend();
                else module.Retract();
            }
        }

        private void AttachRemoteControl(Vessel vessel)
        {
            if (remoteControlVessel == vessel) return;
            ReleaseRemoteControl(true);
            remoteControlVessel = vessel;
            remoteControlVessel.OnFlyByWire += ApplyRemoteControl;
        }

        private void ApplyRemoteControl(FlightCtrlState state)
        {
            if (state == null || Time.realtimeSinceStartup > remoteControlExpiresAt)
            {
                return;
            }
            // KSP keyboard axes are an input layered onto the current control state. Preserve
            // MechJeb's attitude output and add the web axes in the same fashion instead of
            // replacing (and therefore implicitly disengaging) the autopilot's commands.
            state.pitch = Mathf.Clamp(state.pitch + remotePitch, -1f, 1f);
            state.yaw = Mathf.Clamp(state.yaw + remoteYaw, -1f, 1f);
            state.roll = Mathf.Clamp(state.roll + remoteRoll, -1f, 1f);
            if (IsMechJebBurnAutopilotActive(remoteControlVessel))
                remoteThrottle = 0;
            else
                state.mainThrottle = remoteThrottle;
        }

        private static bool IsMechJebBurnAutopilotActive(Vessel vessel)
        {
            if (vessel == null) return false;
            try
            {
                MechJebCore core = vessel.GetMasterMechJeb();
                if (core == null) return false;
                MechJebModuleAscentAutopilot ascent = core.GetComputerModule<MechJebModuleAscentAutopilot>();
                MechJebModuleNodeExecutor executor = core.GetComputerModule<MechJebModuleNodeExecutor>();
                MechJebModuleLandingAutopilot landing = core.GetComputerModule<MechJebModuleLandingAutopilot>();
                return (ascent != null && ascent.enabled)
                    || (executor != null && executor.enabled)
                    || (landing != null && landing.enabled);
            }
            catch
            {
                // If MechJeb is absent or changing vessels, retain ordinary KSP control behavior.
                return false;
            }
        }

        private void UpdateRemoteControlExpiry()
        {
            if (remoteControlVessel == null) return;
            if (remoteControlVessel != FlightGlobals.ActiveVessel || Time.realtimeSinceStartup > remoteControlExpiresAt)
                ReleaseRemoteControl(true);
        }

        private void ReleaseRemoteControl(bool zeroThrottle)
        {
            Vessel vessel = remoteControlVessel;
            bool mayZeroThrottle = zeroThrottle && vessel != null && !IsMechJebBurnAutopilotActive(vessel);
            remoteControlVessel = null;
            remoteControlExpiresAt = 0;
            remotePitch = remoteYaw = remoteRoll = 0;
            remoteThrottle = 0;
            if (vessel == null) return;
            vessel.OnFlyByWire -= ApplyRemoteControl;
            if (mayZeroThrottle && vessel.ctrlState != null)
                vessel.ctrlState.mainThrottle = 0;
        }

        private void ConfigureLanding(MechJebCore core, MechJebModuleLandingAutopilot landing, IncomingMessage message)
        {
            landing.deployGears = WireProtocol.ReadBool(message.RawJson, "deployGears", landing.deployGears);
            landing.limitGearsStage.val = (int)Math.Max(0, Math.Min(99,
                WireProtocol.ReadDouble(message.RawJson, "gearStageLimit", landing.limitGearsStage.val)));
            landing.deployChutes = WireProtocol.ReadBool(message.RawJson, "deployChutes", landing.deployChutes);
            landing.limitChutesStage.val = (int)Math.Max(0, Math.Min(99,
                WireProtocol.ReadDouble(message.RawJson, "chuteStageLimit", landing.limitChutesStage.val)));
            landing.rcsAdjustment = WireProtocol.ReadBool(message.RawJson, "rcsAdjustment", landing.rcsAdjustment);
            landing.touchdownSpeed.val = Math.Max(.1, WireProtocol.ReadDouble(message.RawJson, "touchdownSpeed", landing.touchdownSpeed.val));
            MechJebModuleLandingPredictions predictions = core.GetComputerModule<MechJebModuleLandingPredictions>();
            if (predictions == null) return;
            predictions.deployChutes = landing.deployChutes;
            predictions.limitChutesStage = landing.limitChutesStage.val;
            bool predictionsEnabled = WireProtocol.ReadBool(message.RawJson, "predictionsEnabled", predictions.enabled);
            if (predictionsEnabled && !predictions.users.Contains(this)) predictions.users.Add(this);
            else if (!predictionsEnabled && predictions.users.Contains(this)) predictions.users.Remove(this);
            predictions.makeAerobrakeNodes = WireProtocol.ReadBool(message.RawJson, "makeAerobrakeNodes", predictions.makeAerobrakeNodes);
            predictions.showTrajectory = WireProtocol.ReadBool(message.RawJson, "showTrajectory", predictions.showTrajectory);
            predictions.worldTrajectory = WireProtocol.ReadBool(message.RawJson, "worldTrajectory", predictions.worldTrajectory);
            predictions.camTrajectory = WireProtocol.ReadBool(message.RawJson, "cameraTrajectory", predictions.camTrajectory);
        }

        private CommandExecution StartPorkchop(Vessel vessel, MechJebCore core, IncomingMessage message)
        {
            MechJebModuleTargetController target = core.GetComputerModule<MechJebModuleTargetController>();
            CelestialBody targetBody = target == null ? null : target.Target as CelestialBody;
            Orbit targetOrbit = target == null ? null : target.TargetOrbit;
            Orbit departureBodyOrbit = vessel == null || vessel.orbit == null || vessel.orbit.referenceBody == null
                ? null : vessel.orbit.referenceBody.orbit;
            if (target == null || targetOrbit == null || targetBody == null || departureBodyOrbit == null)
                return CommandFailure("invalid_target", "Advanced transfer requires a celestial-body target orbit.");

            double now = Planetarium.fetch == null ? 0 : Planetarium.GetUniversalTime();
            double synodicPeriod = departureBodyOrbit.SynodicPeriod(targetOrbit);
            if (!IsFinite(synodicPeriod)) synodicPeriod = departureBodyOrbit.period;
            double transferTime = OrbitUtil.GetTransferTime(departureBodyOrbit, targetOrbit);
            if (!IsFinite(synodicPeriod) || synodicPeriod <= 0 || !IsFinite(transferTime) || transferTime <= 0)
                return CommandFailure("invalid_range", "MechJeb could not derive a transfer window for the selected bodies.");

            // Match MechJeb 2.14.3 OperationAdvancedTransfer.ComputeTimes: one and a half
            // synodic periods for departure, and twice the nominal transfer time for flight.
            porkchopMinDeparture = Math.Max(now, WireProtocol.ReadDouble(message.RawJson, "minimumDepartureTime", now));
            porkchopMaxDeparture = WireProtocol.ReadDouble(message.RawJson, "maximumDepartureTime", porkchopMinDeparture + 1.5 * synodicPeriod);
            porkchopMinTransfer = WireProtocol.ReadDouble(message.RawJson, "minimumTransferTime", 3600);
            porkchopMaxTransfer = WireProtocol.ReadDouble(message.RawJson, "maximumTransferTime", 2 * transferTime);
            porkchopWidth = Math.Max(16, Math.Min(256, (int)WireProtocol.ReadDouble(message.RawJson, "width", 160)));
            porkchopHeight = Math.Max(16, Math.Min(256, (int)WireProtocol.ReadDouble(message.RawJson, "height", 200)));
            porkchopIncludeCapture = WireProtocol.ReadBool(message.RawJson, "includeCaptureBurn", true);
            porkchopTargetPeriapsisHeight = Math.Max(0, WireProtocol.ReadDouble(message.RawJson, "targetPeriapsis", 60000));
            if (porkchopMaxDeparture <= porkchopMinDeparture
                || porkchopMinTransfer <= 0 || porkchopMaxTransfer <= porkchopMinTransfer)
                return CommandFailure("invalid_range", "Porkchop time ranges are invalid.");

            if (porkchopWorker != null && !porkchopWorker.Finished) porkchopWorker.Stop = true;
            porkchopTargetBody = targetBody;
            porkchopRevision++;
            porkchopStatus = "computing";
            porkchopProgress = 0;
            porkchopPublished = false;
            bridge.PublishPorkchop(new PorkchopResultSnapshot(porkchopRevision, porkchopWidth, porkchopHeight,
                porkchopMinDeparture, porkchopMaxDeparture, porkchopMinTransfer, porkchopMaxTransfer, -1, -1, null));
            porkchopWorker = new AllGraphTransferCalculator(vessel.orbit, targetOrbit,
                porkchopMinDeparture, porkchopMaxDeparture, porkchopMinTransfer, porkchopMaxTransfer,
                porkchopWidth, porkchopHeight, porkchopIncludeCapture);
            return CommandSuccess("Porkchop calculation started (revision " + porkchopRevision + ").");
        }

        private void UpdatePorkchop()
        {
            AllGraphTransferCalculator worker = porkchopWorker;
            if (worker == null || porkchopPublished) return;
            porkchopProgress = worker.Progress;
            if (!worker.Finished) return;
            double[,] matrix = worker.Computed;
            if (matrix == null)
            {
                porkchopStatus = "failed";
                porkchopPublished = true;
                return;
            }
            int width = matrix.GetLength(0);
            int height = matrix.GetLength(1);
            var costs = new double[width * height];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++) costs[y * width + x] = matrix[x, y];
            bridge.PublishPorkchop(new PorkchopResultSnapshot(porkchopRevision, width, height,
                porkchopMinDeparture, porkchopMaxDeparture, porkchopMinTransfer, porkchopMaxTransfer,
                worker.BestDate, worker.BestDuration, costs));
            porkchopStatus = "ready";
            porkchopProgress = 100;
            porkchopPublished = true;
        }

        private CommandExecution CreatePorkchopManeuver(Vessel vessel, MechJebCore core, IncomingMessage message)
        {
            if (porkchopWorker == null || !porkchopWorker.Finished || !porkchopPublished || porkchopTargetBody == null)
                return CommandFailure("porkchop_not_ready", "Porkchop calculation is not ready.");
            int departureIndex = (int)WireProtocol.ReadDouble(message.RawJson, "departureIndex", porkchopWorker.BestDate);
            int durationIndex = (int)WireProtocol.ReadDouble(message.RawJson, "durationIndex", porkchopWorker.BestDuration);
            if (departureIndex < 0 || departureIndex >= porkchopWidth || durationIndex < 0 || durationIndex >= porkchopHeight)
                return CommandFailure("invalid_selection", "Porkchop selection is outside the computed matrix.");
            double departure = porkchopWorker.DateFromIndex(departureIndex);
            double duration = porkchopWorker.DurationFromIndex(durationIndex);
            double now = Planetarium.fetch == null ? 0 : Planetarium.GetUniversalTime();
            System.Collections.Generic.List<ManeuverParameters> nodes = porkchopWorker.OptimizeEjection(
                departure, vessel.orbit, porkchopTargetBody, departure + duration, now,
                porkchopTargetBody.Radius + porkchopTargetPeriapsisHeight, porkchopIncludeCapture);
            if (nodes == null || nodes.Count == 0) return CommandFailure("no_solution", "Selected Porkchop cell did not produce a maneuver.");
            if (WireProtocol.ReadBool(message.RawJson, "replaceLast", false)) RemoveLastManeuverNode(vessel);
            foreach (ManeuverParameters node in nodes) vessel.PlaceManeuverNode(vessel.orbit, node.dV, node.UT);
            bool execute = WireProtocol.ReadBool(message.RawJson, "execute", false);
            if (execute) core.GetComputerModule<MechJebModuleNodeExecutor>().ExecuteAllNodes(this);
            return CommandSuccess(nodes.Count + " advanced transfer node(s) created" + (execute ? " and execution started." : "."));
        }

        private CommandExecution CreateManeuver(Vessel vessel, MechJebCore core, IncomingMessage message)
        {
            string operationName = WireProtocol.ReadString(message.RawJson, "operation");
            Operation operation;
            switch (operationName)
            {
                case "circularize": operation = new OperationCircularize(); break;
                case "periapsis":
                    var periapsis = new OperationPeriapsis();
                    periapsis.newPeA.val = WireProtocol.ReadDouble(message.RawJson, "periapsis", vessel.orbit.PeA);
                    operation = periapsis; break;
                case "apoapsis":
                    var apoapsis = new OperationApoapsis();
                    apoapsis.newApA.val = WireProtocol.ReadDouble(message.RawJson, "apoapsis", vessel.orbit.ApA);
                    operation = apoapsis; break;
                case "apsides":
                    var apsides = new OperationEllipticize();
                    apsides.newPeA.val = WireProtocol.ReadDouble(message.RawJson, "periapsis", vessel.orbit.PeA);
                    apsides.newApA.val = WireProtocol.ReadDouble(message.RawJson, "apoapsis", vessel.orbit.ApA);
                    operation = apsides; break;
                case "inclination":
                    var inclination = new OperationInclination();
                    inclination.newInc.val = WireProtocol.ReadDouble(message.RawJson, "inclination", vessel.orbit.inclination);
                    operation = inclination; break;
                case "lan":
                    var lan = new OperationLan();
                    lan.newLAN.val = WireProtocol.ReadDouble(message.RawJson, "lan", vessel.orbit.LAN);
                    operation = lan; break;
                case "semimajor":
                    var semiMajor = new OperationSemiMajor();
                    semiMajor.newSMA.val = WireProtocol.ReadDouble(message.RawJson, "semiMajorAxis", vessel.orbit.semiMajorAxis);
                    operation = semiMajor; break;
                case "longitude":
                    var longitude = new OperationLongitude();
                    longitude.newLAN.val = WireProtocol.ReadDouble(message.RawJson, "longitude", vessel.longitude);
                    operation = longitude; break;
                case "hohmann":
                    var hohmann = new OperationGeneric { simpleTransfer = true, intercept_only = false };
                    operation = hohmann; break;
                case "correction":
                    var correction = new OperationCourseCorrection();
                    correction.courseCorrectFinalPeA.val = WireProtocol.ReadDouble(message.RawJson, "targetPeriapsis", 0);
                    correction.interceptDistance.val = WireProtocol.ReadDouble(message.RawJson, "interceptDistance", 0);
                    operation = correction; break;
                case "intercept":
                    var intercept = new OperationLambert();
                    intercept.interceptInterval.val = WireProtocol.ReadDouble(message.RawJson, "interceptInterval", 3600);
                    operation = intercept; break;
                case "planes": operation = new OperationPlane(); break;
                case "velocity": operation = new OperationKillRelVel(); break;
                case "resonant":
                    var resonant = new OperationResonantOrbit();
                    resonant.resonanceNumerator.val = (int)WireProtocol.ReadDouble(message.RawJson, "numerator", 2);
                    resonant.resonanceDenominator.val = (int)WireProtocol.ReadDouble(message.RawJson, "denominator", 3);
                    operation = resonant; break;
                case "moonreturn":
                    var moonReturn = new OperationMoonReturn();
                    moonReturn.moonReturnAltitude.val = WireProtocol.ReadDouble(message.RawJson, "returnPeriapsis", 30000);
                    operation = moonReturn; break;
                case "planettransfer": operation = new OperationInterplanetaryTransfer(); break;
                case "advanced":
                    return CommandFailure("porkchop_selection_required", "Advanced transfer requires a solved Porkchop selection.");
                default:
                    return CommandFailure("invalid_operation", "Unknown maneuver operation.");
            }

            bool replaceLast = WireProtocol.ReadBool(message.RawJson, "replaceLast", false);
            double universalTime = Planetarium.fetch == null ? 0 : Planetarium.GetUniversalTime();
            Orbit planningOrbit = vessel.orbit;
            List<ManeuverNode> existingNodes = vessel.patchedConicSolver == null
                ? null : vessel.patchedConicSolver.maneuverNodes;
            if (existingNodes != null && existingNodes.Count > 0)
            {
                int predecessor = replaceLast ? existingNodes.Count - 2 : existingNodes.Count - 1;
                if (predecessor >= 0)
                {
                    ManeuverNode priorNode = existingNodes[predecessor];
                    if (priorNode.nextPatch != null) planningOrbit = priorNode.nextPatch;
                    universalTime = Math.Max(universalTime, priorNode.UT);
                }
            }

            string timingError;
            if (!ConfigureOperationTime(operation, message, out timingError))
                return CommandFailure("invalid_time_reference", timingError);

            MechJebModuleTargetController target = core.GetComputerModule<MechJebModuleTargetController>();
            System.Collections.Generic.List<ManeuverParameters> nodes = operation.MakeNodes(planningOrbit, universalTime, target);
            if (nodes == null || nodes.Count == 0)
                return CommandFailure("no_solution", operation.getErrorMessage() ?? "MechJeb did not produce a maneuver node.");
            if (replaceLast) RemoveLastManeuverNode(vessel);
            foreach (ManeuverParameters node in nodes)
            {
                vessel.PlaceManeuverNode(planningOrbit, node.dV, node.UT);
            }
            bool execute = WireProtocol.ReadBool(message.RawJson, "execute", false);
            if (execute) core.GetComputerModule<MechJebModuleNodeExecutor>().ExecuteAllNodes(this);
            return CommandSuccess(nodes.Count + " maneuver node(s) created by " + operationName + (execute ? " and execution started." : "."));
        }

        private static bool ConfigureOperationTime(Operation operation, IncomingMessage message, out string error)
        {
            error = null;
            string requestedName = WireProtocol.ReadString(message.RawJson, "timeReference");
            if (string.IsNullOrEmpty(requestedName)) return true;

            TimeReference requested;
            if (!Enum.TryParse(requestedName, true, out requested))
            {
                error = "Unknown MechJeb time reference: " + requestedName;
                return false;
            }

            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
            TimeSelector selector = null;
            FieldInfo[] operationFields = operation.GetType().GetFields(flags);
            foreach (FieldInfo field in operationFields)
            {
                if (typeof(TimeSelector).IsAssignableFrom(field.FieldType))
                {
                    selector = field.GetValue(field.IsStatic ? null : operation) as TimeSelector;
                    if (selector != null) break;
                }
            }
            if (selector == null)
            {
                if (requested == TimeReference.COMPUTED) return true;
                error = operation.GetType().Name + " does not expose a configurable time selector.";
                return false;
            }

            TimeReference[] allowed = null;
            FieldInfo currentIndex = null;
            foreach (FieldInfo field in typeof(TimeSelector).GetFields(flags))
            {
                if (field.FieldType == typeof(TimeReference[])) allowed = field.GetValue(selector) as TimeReference[];
                else if (field.FieldType == typeof(int) && field.Name.IndexOf("current", StringComparison.OrdinalIgnoreCase) >= 0)
                    currentIndex = field;
            }
            if (allowed == null || currentIndex == null)
            {
                error = "The installed MechJeb TimeSelector layout is unsupported.";
                return false;
            }
            int selectedIndex = Array.IndexOf(allowed, requested);
            if (selectedIndex < 0)
            {
                error = operation.GetType().Name + " does not support " + requestedName + ".";
                return false;
            }
            currentIndex.SetValue(selector, selectedIndex);

            if (requested == TimeReference.X_FROM_NOW)
            {
                double leadTime = Math.Max(.1, WireProtocol.ReadDouble(message.RawJson, "leadTime", 60));
                if (!SetEditableValue(selector, "lead", leadTime))
                {
                    error = "The installed MechJeb lead-time field is unsupported.";
                    return false;
                }
            }
            else if (requested == TimeReference.ALTITUDE)
            {
                double altitude = WireProtocol.ReadDouble(message.RawJson, "circularizeAltitude", double.NaN);
                if (double.IsNaN(altitude) || double.IsInfinity(altitude) || !SetEditableValue(selector, "altitude", altitude))
                {
                    error = "A finite circularization altitude is required.";
                    return false;
                }
            }
            return true;
        }

        private static bool SetEditableValue(TimeSelector selector, string fieldNameFragment, double value)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            object editable = null;
            foreach (FieldInfo field in typeof(TimeSelector).GetFields(flags))
            {
                if (field.Name.IndexOf(fieldNameFragment, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    editable = field.GetValue(selector);
                    if (editable != null) break;
                }
            }
            if (editable == null) return false;
            Type type = editable.GetType();
            PropertyInfo property = type.GetProperty("Val", flags) ?? type.GetProperty("val", flags);
            if (property != null && property.CanWrite)
            {
                property.SetValue(editable, Convert.ChangeType(value, property.PropertyType), null);
                return true;
            }
            FieldInfo valueField = type.GetField("val", flags) ?? type.GetField("Val", flags);
            if (valueField == null) return false;
            valueField.SetValue(editable, Convert.ChangeType(value, valueField.FieldType));
            return true;
        }

        private static void RemoveLastManeuverNode(Vessel vessel)
        {
            if (vessel == null || vessel.patchedConicSolver == null || vessel.patchedConicSolver.maneuverNodes == null
                || vessel.patchedConicSolver.maneuverNodes.Count == 0) return;
            int last = vessel.patchedConicSolver.maneuverNodes.Count - 1;
            vessel.patchedConicSolver.RemoveManeuverNode(vessel.patchedConicSolver.maneuverNodes[last]);
        }

        private static CommandExecution CommandSuccess(string message)
        {
            return new CommandExecution { Success = true, Code = "ok", Message = message };
        }

        private static CommandExecution CommandFailure(string code, string message)
        {
            return new CommandExecution { Success = false, Code = code, Message = message };
        }

        private void OnApplicationQuit()
        {
            ReleaseRemoteControl(true);
            StopServer();
        }

        private void OnDestroy()
        {
            if (instance == this)
            {
                instance = null;
                GameEvents.onGUIApplicationLauncherReady.Remove(CreateLauncherButton);
                GameEvents.onGUIApplicationLauncherDestroyed.Remove(DestroyLauncherButton);
                DestroyLauncherButton();
                ReleaseRemoteControl(true);
                StopServer();
                if (offlineIcon != null) Destroy(offlineIcon);
                if (onlineIcon != null) Destroy(onlineIcon);
                if (vesselImageRenderer != null)
                {
                    vesselImageRenderer.Dispose();
                    vesselImageRenderer = null;
                }
            }
        }

        private void StopServer()
        {
            ArmorControlServer current = server;
            server = null;
            if (current == null)
            {
                return;
            }

            current.Dispose();
            RefreshLauncherIcon();
            Debug.Log(LogPrefix + "server stopped.");
        }
    }
}
