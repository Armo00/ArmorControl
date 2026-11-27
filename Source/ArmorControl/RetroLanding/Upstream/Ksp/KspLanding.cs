using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using LandingPoc;
using ModuleWheels;
using UnityEngine;

namespace KspRetroLanding
{
    public sealed partial class KspLanding : MonoBehaviour
    {
        internal static KspLanding Instance { get; private set; }
        private enum Mode { Off, Observe, Control }
        private struct GroundReading
        {
            public double SeaLevelHeight, Distance, HullOffset, WheelOffset, RawDistance, FootClearance;
            public int ActiveSupports, FootRayHits;
            public bool WaterSurface;
            public Collider Collider, RawCollider;
            public double BottomOffset => Math.Min(HullOffset, WheelOffset);
            public double HullClearance => Distance + HullOffset;
            public double Clearance => Math.Max(0.05, Math.Min(HullClearance, FootClearance));
        }
        private readonly Settings cfg = new Settings
        {
            AttitudeResponseSeconds = 1.2, GearDeploymentSeconds = 6, GearSpeedLimit = 100,
            GearCommandAdvanceSeconds = 0.5,
            TouchSpeed = 1.6
        };
        private readonly List<EngineBinding> engines = new List<EngineBinding>();
        private AttitudeVector[] engineThrustAccelerations = Array.Empty<AttitudeVector>();
        private readonly List<EngineBinding> unmanagedEngines = new List<EngineBinding>();
        private readonly LandingEngineBay engineBay = new LandingEngineBay();
        private readonly List<ModuleWheelDeployment> gearModules = new List<ModuleWheelDeployment>();
        private readonly List<ModuleWheelBase> supportWheels = new List<ModuleWheelBase>();
        private readonly Dictionary<ModuleWheelBase, float> suspensionTravel = new Dictionary<ModuleWheelBase, float>();
        private readonly List<PartModule> torqueModules = new List<PartModule>();
        private readonly Dictionary<ModuleGimbal, FinalGimbalSettings> finalGimbals =
            new Dictionary<ModuleGimbal, FinalGimbalSettings>();
        private readonly RaycastHit[] surfaceHits = new RaycastHit[32];
        private const double UllageAllowance = 0.8;
        private const double TurnDegreesPerSecond = 30; // preflight assumption, calibrate with the flight log
        private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
        private Vessel vessel;
        private Transform frame;
        private int partCount;
        private Mode mode;
        private Controller controller;
        private FarCoastPrediction farCoast;
        private AttitudeGuidance attitude;
        private LandingSettingsStore settingsStore;
        private PropellantBudget propellant;
        private StreamWriter trace, engineTrace;
        private double nextLog, nextThrust;
        private double previousSampleTime, coastDragRate, coastTurnRate;
        private double lastTurnRate, measuredTurnAcceleration;
        private double nextSiteSurvey, siteLatitude, siteLongitude, siteAltitude, siteDistance;
        private double settleDeadline, supportedSince;
        private bool hasLandingSite;
        private Vector3d previousSurfaceVelocity, previousThrustAcceleration, externalAcceleration;
        private long lastMask = -1;
        private long previousRunningMask;
        private double[] ignitionElapsed;
        private Phase lastPhase;
        private string lastYoloInhibit;
        private string lastYoloState;
        private Vector3d horizontal = Vector3d.zero;
        private Rect window = new Rect(260, 100, 300, 145);
        private string capText = "4", boostbackCapText, entryCapText, boostbackStartAoaText, boostbackFineKmText;
        private Vector2 settingsScroll;
        internal Func<LandingTargetSource?, string> NativeStart;
        internal Action NativeStop;
        private string finalReserveText;
        private string rollText = "0";
        private string coastPitchAoaText, coastPitchMinText, coastPitchMaxText;
        private string ignitionAltitudeText, entryAltitudeText, entrySpeedText;
        private string vesselEastOffsetText, vesselNorthOffsetText;
        private double rollDegrees;
        private double pitchTrim, yawTrim, lastTrimAoa = double.NaN;
        private RetrogradeAxisControl retrogradePitch, retrogradeYaw;
        private Vector3d previousRetrogradeAim;
        private double previousRetrogradeTime;
        private bool previousRetrogradeRcs;
        private float finalGimbalLimit;
        private string state = "Idle";
        private bool previousSas, ullaging, suspendedForWarp, uiHidden;
        private bool independentThrottle, masterThrottleOwned;
        private Vector3 rotationInput;
        private VesselAutopilot.AutopilotMode previousAutopilot;
        private PartModule windowMechJeb;
        private PropertyInfo windowMechJebInhibit;
        private float throttle;
        private MethodInfo removeRemoteTechPilot;
        private Action<FlightCtrlState> remoteTechPilot;
        private Func<Guid, bool> remoteTechHasConnection;
        private Guid remoteTechVesselId;
        private bool? remoteTechConnected;

        private void Awake()
        {
            Instance = this;
            settingsStore = OpenEmbeddedSettings();
            settingsStore.Current.Apply(cfg, out rollDegrees);
            SyncSettingsText();
            GameEvents.onVesselGoOnRails.Add(OnVesselGoOnRails);
            GameEvents.onProtoPartModuleSnapshotSave.Add(OnModuleSnapshotSave);
            GameEvents.onProtoVesselSave.Add(OnVesselSnapshotSave);
            GameEvents.onHideUI.Add(HideUI);
            GameEvents.onShowUI.Add(ShowUI);
            InitializeToolbar();
        }
        internal void Engage() { if (IsIdle) StartMode(Mode.Control); }
        internal void Disengage() { StopMode("Recovery stopped", false); }
        internal bool IsIdle { get { return mode == Mode.Off; } }
        internal double ThrustLimitG { get { return cfg.MaxG; } }
        internal void SetThrustLimit(double acceleration)
        {
            if (!IsIdle) throw new InvalidOperationException("Recovery is active.");
            if (settingsStore.HasCurrent) return;
            cfg.MaxG = cfg.BoostbackMaxG = cfg.EntryMaxG = acceleration / Settings.G0;
            SyncSettingsText();
        }

        private void SyncSettingsText()
        {
            capText = cfg.MaxG.ToString("0.##", Invariant);
            boostbackCapText = cfg.ThrustLimitFor(Phase.Boostback).ToString("0.##", Invariant);
            entryCapText = cfg.ThrustLimitFor(Phase.EntryBurn).ToString("0.##", Invariant);
            boostbackStartAoaText = cfg.BoostbackStartAoa.ToString("0.##", Invariant);
            boostbackFineKmText = (cfg.BoostbackFineDistance / 1000).ToString("0.##", Invariant);
            finalReserveText = cfg.PrecisionSeconds.ToString("0.##", Invariant);
            rollText = rollDegrees.ToString("0.##", Invariant);
            coastPitchAoaText = cfg.CoastPitchAoaDegrees.ToString("0.##", Invariant);
            coastPitchMinText = double.IsNaN(cfg.CoastPitchAoaMinimum) ? "" : cfg.CoastPitchAoaMinimum.ToString("0.##", Invariant);
            coastPitchMaxText = double.IsNaN(cfg.CoastPitchAoaMaximum) ? "" : cfg.CoastPitchAoaMaximum.ToString("0.##", Invariant);
            ignitionAltitudeText = (cfg.MaxIgnitionAltitude / 1000).ToString("0.##", Invariant);
            entryAltitudeText = (cfg.EntryBurnAltitude / 1000).ToString("0.##", Invariant);
            entrySpeedText = cfg.EntryBurnSpeed.ToString("0.##", Invariant);
            vesselEastOffsetText = cfg.VesselTargetEastOffset.ToString("R", Invariant);
            vesselNorthOffsetText = cfg.VesselTargetNorthOffset.ToString("R", Invariant);
        }

        private static void DrawAoaBound(string label, ref string text, ref double bound)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(135));
            string previous = text;
            text = GUILayout.TextField(text, 8, GUILayout.Width(55));
            if (text != previous)
            {
                if (string.IsNullOrWhiteSpace(text)) bound = double.NaN;
                else if (double.TryParse(text, NumberStyles.Float, Invariant, out double value) && value >= -90 && value <= 90)
                    bound = value;
            }
            GUILayout.Label(double.IsNaN(bound) ? "Default coast AoA" : bound.ToString("0.##", Invariant));
            GUILayout.EndHorizontal();
        }

        private static void DrawVesselOffset(string label, ref string text, ref double offset)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(135));
            string previous = text;
            text = GUILayout.TextField(text, 14, GUILayout.Width(70));
            if (text != previous && double.TryParse(text, NumberStyles.Float, Invariant, out double value) &&
                FootprintAtmosphere.Finite(value))
                offset = value;
            GUILayout.Label("Active " + offset.ToString("0.##", Invariant));
            GUILayout.EndHorizontal();
        }

        private void OnGUI()
        {
            if (toolbarOpen && !uiHidden && FlightGlobals.ready && FlightGlobals.ActiveVessel != null)
            {
                window.x = Mathf.Clamp(window.x, 0, Mathf.Max(0, Screen.width - window.width));
                window.y = Mathf.Clamp(window.y, 0, Mathf.Max(0, Screen.height - window.height));
                window = GUILayout.Window(GetInstanceID(), window, DrawWindow, "Retro Landing");
            }
        }

        private void DrawWindow(int id)
        {
            if (IsIdle)
            {
                if (GUILayout.Button("Land along current track")) StartFromMenu(null);
                bool enabled = GUI.enabled;
                GUI.enabled = enabled && HasTargetedWaypoint(FlightGlobals.ActiveVessel);
                if (GUILayout.Button("Land at waypoint")) StartFromMenu(LandingTargetSource.Waypoint);
                GUI.enabled = enabled && LandedTargetFor(FlightGlobals.ActiveVessel) != null;
                if (GUILayout.Button("Land near target vessel")) StartFromMenu(LandingTargetSource.LandedVessel);
                GUI.enabled = enabled;
            }
            else if (GUILayout.Button("Stop landing"))
            {
                if (NativeStop != null) NativeStop();
                else Disengage();
            }
            GUILayout.Label(state);
            settingsScroll = GUILayout.BeginScrollView(settingsScroll, GUILayout.MaxHeight(Mathf.Max(120, Screen.height - 180)));
            Vessel active = FlightGlobals.ActiveVessel;
            if (active != null && active.loaded && active.ReferenceTransform != null)
                engineBay.Draw(active, IsIdle ? null : engines, IsIdle ? 0 : Math.Max(0, lastMask));
            DrawSettings();
            GUILayout.EndScrollView();
            GUI.DragWindow();
        }

        private void StartFromMenu(LandingTargetSource? source)
        {
            if (NativeStart != null)
            {
                string error = NativeStart(source);
                if (error != null) state = error;
                return;
            }
            if (source.HasValue) EngageTargetedEmbedded(source.Value, fromWindow: true);
            else StartMode(Mode.Control, fromWindow: true);
        }

        private void HideUI() { uiHidden = true; }
        private void ShowUI() { uiHidden = false; }

        private static void DrawPositiveSetting(string label, ref string text, ref double value, double maximum, double scale = 1)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(150));
            string previous = text;
            text = GUILayout.TextField(text, 12, GUILayout.Width(65));
            if (text != previous && double.TryParse(text, NumberStyles.Float, Invariant, out double edited) &&
                edited > 0 && edited <= maximum) value = edited * scale;
            GUILayout.Label((value / scale).ToString("0.##", Invariant));
            GUILayout.EndHorizontal();
        }

        private void DrawSettings()
        {
            var before = new LandingTuning(cfg, rollDegrees);
            DrawPositiveSetting("Landing max G", ref capText, ref cfg.MaxG, 20);
            DrawPositiveSetting("Boostback max G", ref boostbackCapText, ref cfg.BoostbackMaxG, 20);
            DrawPositiveSetting("Entry max G", ref entryCapText, ref cfg.EntryMaxG, 20);
            DrawPositiveSetting("Boostback start angle (deg)", ref boostbackStartAoaText, ref cfg.BoostbackStartAoa, 180);
            DrawPositiveSetting("Boostback fine range (km)", ref boostbackFineKmText, ref cfg.BoostbackFineDistance,
                double.MaxValue / 1000, 1000);
            GUILayout.BeginHorizontal();
            GUILayout.Label("Final reserve (s)", GUILayout.Width(100));
            string previous = finalReserveText;
            finalReserveText = GUILayout.TextField(finalReserveText, 7, GUILayout.Width(45));
            double reserve;
            if (finalReserveText != previous && double.TryParse(finalReserveText, NumberStyles.Float, Invariant, out reserve) &&
                reserve > 0 && !double.IsInfinity(reserve))
                cfg.PrecisionSeconds = reserve;
            GUILayout.Label("Active " + cfg.PrecisionSeconds.ToString("0.##", Invariant));
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label("Roll (deg)", GUILayout.Width(100));
            previous = rollText;
            rollText = GUILayout.TextField(rollText, 8, GUILayout.Width(55));
            double roll;
            if (rollText != previous && double.TryParse(rollText, NumberStyles.Float, Invariant, out roll) &&
                !double.IsNaN(roll) && !double.IsInfinity(roll))
                rollDegrees = AttitudeGuidance.WrapDegrees(roll);
            GUILayout.Label("Active " + rollDegrees.ToString("0.##", Invariant));
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label("Default coast AoA (deg)", GUILayout.Width(135));
            previous = coastPitchAoaText;
            coastPitchAoaText = GUILayout.TextField(coastPitchAoaText, 8, GUILayout.Width(55));
            double pitchAoa;
            if (coastPitchAoaText != previous && double.TryParse(coastPitchAoaText, NumberStyles.Float, Invariant, out pitchAoa) &&
                pitchAoa >= -90 && pitchAoa <= 90)
                cfg.CoastPitchAoaDegrees = pitchAoa;
            GUILayout.Label("Active " + cfg.CoastPitchAoaDegrees.ToString("0.##", Invariant));
            GUILayout.EndHorizontal();
            DrawAoaBound("Min coast AoA (deg)", ref coastPitchMinText, ref cfg.CoastPitchAoaMinimum);
            DrawAoaBound("Max coast AoA (deg)", ref coastPitchMaxText, ref cfg.CoastPitchAoaMaximum);
            if (cfg.MinimumCoastPitch > cfg.CoastPitchAoaDegrees || cfg.CoastPitchAoaDegrees > cfg.MaximumCoastPitch)
                GUILayout.Label("Default coast AoA must be inside the configured bounds.");
            GUILayout.BeginHorizontal();
            GUILayout.Label("Max ignition AGL (km)", GUILayout.Width(135));
            previous = ignitionAltitudeText;
            ignitionAltitudeText = GUILayout.TextField(ignitionAltitudeText, 8, GUILayout.Width(55));
            double altitudeKm;
            if (ignitionAltitudeText != previous && double.TryParse(ignitionAltitudeText, NumberStyles.Float, Invariant, out altitudeKm) &&
                altitudeKm >= 0 && altitudeKm <= double.MaxValue / 1000)
                cfg.MaxIgnitionAltitude = altitudeKm * 1000;
            GUILayout.Label(cfg.MaxIgnitionAltitude > 0
                ? "Active " + (cfg.MaxIgnitionAltitude / 1000).ToString("0.##", Invariant) : "Off");
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label("Entry burn ASL (km)", GUILayout.Width(135));
            previous = entryAltitudeText;
            entryAltitudeText = GUILayout.TextField(entryAltitudeText, 8, GUILayout.Width(55));
            double entryKm;
            if (entryAltitudeText != previous && double.TryParse(entryAltitudeText, NumberStyles.Float, Invariant, out entryKm) &&
                entryKm > 0 && entryKm <= double.MaxValue / 1000)
                cfg.EntryBurnAltitude = entryKm * 1000;
            GUILayout.Label("Active " + (cfg.EntryBurnAltitude / 1000).ToString("0.##", Invariant));
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label("Entry speed (m/s)", GUILayout.Width(135));
            previous = entrySpeedText;
            entrySpeedText = GUILayout.TextField(entrySpeedText, 8, GUILayout.Width(55));
            double entrySpeed;
            if (entrySpeedText != previous && double.TryParse(entrySpeedText, NumberStyles.Float, Invariant, out entrySpeed) &&
                entrySpeed > 0 && !double.IsInfinity(entrySpeed))
                cfg.EntryBurnSpeed = entrySpeed;
            GUILayout.Label("Active " + cfg.EntryBurnSpeed.ToString("0.##", Invariant));
            GUILayout.EndHorizontal();
            DrawVesselOffset("Vessel east offset (m)", ref vesselEastOffsetText, ref cfg.VesselTargetEastOffset);
            DrawVesselOffset("Vessel north offset (m)", ref vesselNorthOffsetText, ref cfg.VesselTargetNorthOffset);
            cfg.OptimizeFuel = GUILayout.Toggle(cfg.OptimizeFuel, "Optimize fuel");
            cfg.OptimizeRecoveryTrajectory = GUILayout.Toggle(cfg.OptimizeRecoveryTrajectory, "Optimize recovery trajectory");
            var current = new LandingTuning(cfg, rollDegrees);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Reset to defaults"))
            {
                settingsStore.Reset().Apply(cfg, out rollDegrees);
                SyncSettingsText();
            }
            else if (GUILayout.Button("Save as defaults"))
                settingsStore.SaveDefaults(current);
            else if (!current.Equals(before))
                settingsStore.SaveCurrent(current);
            GUILayout.EndHorizontal();
        }

        private void StartMode(Mode requested, bool fromWindow = false)
        {
            Vessel active = FlightGlobals.ActiveVessel;
            if (RailsWarpActive)
            {
                state = "End on-rails warp before starting landing control.";
                return;
            }
            if (active == null || !active.loaded || active.packed || active.ReferenceTransform == null)
            {
                state = "Select a loaded vessel in flight.";
                return;
            }
            if (requested == Mode.Control && (active.LandedOrSplashed || active.situation == Vessel.Situations.PRELAUNCH))
            {
                state = "An airborne vessel is required.";
                return;
            }
            try
            {
                vessel = active;
                frame = vessel.ReferenceTransform;
                partCount = vessel.parts.Count;
                settleDeadline = 0;
                engines.Clear();
                unmanagedEngines.Clear();
                gearModules.Clear();
                supportWheels.Clear();
                suspensionTravel.Clear();
                torqueModules.Clear();
                independentThrottle = false;
                rotationInput = Vector3.zero;
                foreach (Part part in vessel.parts)
                {
                    foreach (ModuleEngines engine in part.FindModulesImplementing<ModuleEngines>())
                    {
                        if (EngineBinding.UsesSolidPropellant(engine)) continue;
                        var binding = new EngineBinding(engine);
                        if (engine.isEnabled && engine.moduleIsEnabled)
                            engines.Add(binding);
                        else unmanagedEngines.Add(binding);
                    }
                    gearModules.AddRange(part.FindModulesImplementing<ModuleWheelDeployment>());
                    var wheels = part.FindModulesImplementing<ModuleWheelBase>();
                    supportWheels.AddRange(wheels);
                    foreach (ModuleWheelSuspension suspension in part.FindModulesImplementing<ModuleWheelSuspension>())
                        if (suspension.baseModuleIndex >= 0 && suspension.baseModuleIndex < wheels.Count)
                            suspensionTravel[wheels[suspension.baseModuleIndex]] = Math.Max(0, suspension.suspensionDistance);
                    foreach (PartModule module in part.Modules)
                        if (module is ITorqueProvider &&
                            (!(module is ModuleEngines engine) || !EngineBinding.UsesSolidPropellant(engine)))
                            torqueModules.Add(module);
                }
                AdmitModules();
                if (requested == Mode.Control && !vessel.IsControllable)
                    throw new InvalidOperationException("KSP reports the vessel is uncontrollable.");
                OpenTrace();
                controller = null;
                ResetGuidance();
                mode = requested;
                if (mode == Mode.Control)
                {
                    previousSas = vessel.ActionGroups[KSPActionGroup.SAS];
                    previousAutopilot = vessel.Autopilot.Mode;
                    if (fromWindow) TakeWindowMechJebControl();
                    TakeLandingThrottle();
                    AttachControls();
                    vessel.ActionGroups.SetGroup(KSPActionGroup.SAS, false);
                }
                state = mode + " active";
                Debug.Log("[RetroLanding] " + state + " on " + vessel.vesselName + ", monitored engines: " + engines.Count +
                    ", unmanaged modules: " + unmanagedEngines.Count);
            }
            catch (Exception ex)
            {
                StopMode("Cannot start: " + ex.Message, false);
                Debug.LogException(ex);
            }
        }

        private void ResetGuidance()
        {
            bool cappedFinalCommitted = controller != null && controller.CappedFinalCommitted;
            ResetFootprint();
            controller?.CancelCatalogSearch();
            ResetFinalGimbals();
            pitchTrim = yawTrim = 0;
            ResetRetrogradeControl();
            horizontal = Vector3d.zero;
            previousSampleTime = double.NaN;
            farCoast = new FarCoastPrediction();
            attitude = new AttitudeGuidance();
            coastDragRate = coastTurnRate = lastTurnRate = measuredTurnAcceleration = 0;
            nextSiteSurvey = 0;
            previousRunningMask = 0;
            hasLandingSite = false;
            siteDistance = 0;
            previousThrustAcceleration = externalAcceleration = Vector3d.zero;
            lastPhase = Phase.Coast;
            lastMask = -1;
            nextLog = 0;
            nextThrust = Planetarium.GetUniversalTime() + 0.5;
            Engine[] specs = MeasureSpecs();
            controller = new Controller(specs, cfg, BackupTorque(), ReadEditorFamilies(specs), backgroundCatalog: true)
            {
                CappedFinalCommitted = cappedFinalCommitted
            };
        }

        private void TakeLandingThrottle()
        {
            independentThrottle = true;
            foreach (EngineBinding engine in engines)
                engine.TakeIndependentThrottle(engine.Engine.independentThrottle
                    ? engine.Engine.independentThrottlePercentage / 100 : vessel.ctrlState.mainThrottle);
        }

        private void TakeWindowMechJebControl()
        {
            foreach (Part part in vessel.parts)
                foreach (PartModule module in part.Modules)
                {
                    if (module == null) continue;
                    Type core = module.GetType().Assembly.GetType("MuMech.MechJebCore");
                    if (core == null || !core.IsInstanceOfType(module)) continue;
                    PropertyInfo inhibit = core.GetProperty("DeactivateControl");
                    if (inhibit == null || !inhibit.CanRead || !inhibit.CanWrite || inhibit.PropertyType != typeof(bool))
                        continue;
                    if (!(bool)inhibit.GetValue(module))
                    {
                        inhibit.SetValue(module, true);
                        windowMechJeb = module;
                        windowMechJebInhibit = inhibit;
                    }
                    return;
                }
        }

        private bool RcsEnabled { get { return vessel.ActionGroups[KSPActionGroup.RCS]; } }

        private static bool RailsWarpActive
        {
            get { return TimeWarp.fetch != null && TimeWarp.WarpMode == TimeWarp.Modes.HIGH && TimeWarp.CurrentRateIndex > 0; }
        }

        private Torque BackupTorque()
        {
            Vector3 positive = Vector3.zero, negative = Vector3.zero;
            bool rcs = RcsEnabled;
            foreach (PartModule module in torqueModules)
            {
                if (module == null || !module.isEnabled || !module.moduleIsEnabled || module is ModuleGimbal ||
                    module is ModuleEngines || (!rcs && module is ModuleRCS))
                    continue;
                Vector3 plus, minus;
                ((ITorqueProvider)module).GetPotentialTorque(out plus, out minus);
                positive += plus;
                negative += minus;
            }
            return new Torque(Math.Min(Math.Abs(positive.x), Math.Abs(negative.x)),
                Math.Min(Math.Abs(positive.y), Math.Abs(negative.y)), Math.Min(Math.Abs(positive.z), Math.Abs(negative.z)));
        }

        private Engine[] MeasureSpecs()
        {
            // Size engine groups using their normal authority. Step
            // reapplies the temporary Final limit before using the actuators.
            ResetFinalGimbals();
            var specs = new Engine[engines.Count];
            var fuel = new PropellantDemand[engines.Count][];
            double pressure = vessel.staticPressurekPa / 101.325;
            double totalIsp = 0;
            for (int i = 0; i < engines.Count; i++)
            {
                EngineBinding entry = engines[i];
                entry.Measure(pressure, TimeWarp.fixedDeltaTime);
                specs[i] = entry.Spec(frame, (Vector3)vessel.CoMD);
                fuel[i] = entry.MeasurePropellant();
                totalIsp += entry.Isp;
            }
            cfg.SpecificImpulse = Math.Max(1, totalIsp / engines.Count);
            propellant = new PropellantBudget(fuel);
            return specs;
        }

        private void FixedUpdate()
        {
            if (mode == Mode.Off) return;
            try
            {
                if (vessel == null || vessel != FlightGlobals.ActiveVessel)
                {
                    StopMode("Active vessel changed. Engage again.", false);
                    return;
                }
                if (vessel.packed || RailsWarpActive)
                {
                    SuspendForWarp();
                    return;
                }
                if (!vessel.loaded || partCount != vessel.parts.Count || frame != vessel.ReferenceTransform ||
                    (mode == Mode.Control && !vessel.IsControllable))
                {
                    StopMode("Vessel, control point, topology or physics changed. Engage again.", false);
                    return;
                }
                if (suspendedForWarp)
                {
                    if (settleDeadline <= 0)
                    {
                        foreach (EngineBinding engine in engines)
                            if (!double.IsNaN(engine.RequestedAt) && !engine.Burning)
                                engine.Cancel(engine.Issued);
                        ResetGuidance();
                    }
                    suspendedForWarp = false;
                    Debug.Log("[RetroLanding] Resumed from on-rails warp.");
                }
                if (settleDeadline > 0) SettleOnLegs();
                else Step();
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                StopMode("Control ended: " + ex.Message, false);
            }
        }

        private void OnVesselGoOnRails(Vessel packedVessel)
        {
            if (!IsIdle && packedVessel == vessel) SuspendForWarp();
        }

        private void SuspendForWarp()
        {
            if (suspendedForWarp) return;
            suspendedForWarp = true;
            PauseTargeted();
            ResetFootprint();
            controller?.CancelCatalogSearch();
            rotationInput = Vector3.zero;
            throttle = 0;
            ullaging = false;
            ReleaseMasterThrottle();
            ResetFinalGimbals();
            attitude.ResetFinalPointing();
            if (mode == Mode.Control)
                ApplyIndependentThrottle(0);
            state = "Suspended during on-rails warp.";
            DecisionReason = state;
            lastMask = 0;
            Debug.Log("[RetroLanding] " + state);
        }

        private static bool UsesCoastAttitude(Phase current, bool ignitionBlocked)
        {
            return current == Phase.Coast || (current == Phase.Recoast && ignitionBlocked);
        }

        private void Step()
        {
            double now = Planetarium.GetUniversalTime();
            if (remoteTechHasConnection != null) remoteTechConnected = remoteTechHasConnection(remoteTechVesselId);
            if (now >= nextThrust || controller.CoastIgnitionImminent(now))
            {
                controller.RefreshThrust(MeasureSpecs(), BackupTorque());
                nextThrust = now + 0.5;
            }
            Vector3d up = (vessel.CoMD - vessel.mainBody.position).normalized;
            Vector3d velocity = vessel.srf_velocity;
            double vz = Vector3d.Dot(velocity, up);
            Vector3d lateral = velocity - vz * up;
            GroundReading ground = ReadGround(up, lateral, vz);
            bool ignitionBlocked = cfg.IgnitionAltitudeBlocked(ground.Clearance);
            cfg.Gravity = vessel.mainBody.gravParameter / (vessel.CoMD - vessel.mainBody.position).sqrMagnitude;
            Vector3d terrainVelocity = TerrainAvoidanceVelocity(now, up, ground, lateral, vz);
            Vector3d lateralError = lateral - terrainVelocity - TargetedVelocity(up, lateral, ground, vz);
            if (lateralError.sqrMagnitude > 0.0001) horizontal = lateralError.normalized;
            Vector3d crossTrack = Vector3d.Cross(up, horizontal).normalized;
            cfg.SampleSeconds = Time.fixedDeltaTime;
            AttitudeVector localUp = AttitudeCoordinates(up), north = LocalNorth(up);
            AttitudeVector surfaceRetrograde = attitude.SurfaceRetrograde(AttitudeCoordinates(velocity), AttitudeCoordinates(frame.up));
            double coastPitchAoa = cfg.CoastPitchAoaAt(ground.SeaLevelHeight);
            bool bankedCoast = TryTargetedGlideAttitude(ground.SeaLevelHeight, ignitionBlocked, surfaceRetrograde, localUp, north,
                ref coastPitchAoa, out Vector3d bankedPointing, out AttitudeVector bankedForward);
            if (coastPitchAoa != lastTrimAoa)
            {
                pitchTrim = yawTrim = 0;
                lastTrimAoa = coastPitchAoa;
            }
            Vector3d coastPointing = bankedCoast ? bankedPointing :
                WorldDirection(AttitudeGuidance.CoastPointing(surfaceRetrograde, localUp, north, coastPitchAoa));
            Vector3d braking = WorldDirection(surfaceRetrograde);
            double tilt = Vector3.Angle(frame.up, (Vector3)braking);
            double upright = Vector3d.Dot(frame.up, up);
            double uprightAngle = Math.Acos(Math.Max(-1, Math.Min(1, upright)));
            Rigidbody rootBody = vessel.rootPart.rb;
            Vector3d lateralTurnAxis = Vector3d.Cross(up, horizontal).normalized;
            double pitchRate = rootBody == null ? 0 : Vector3d.Dot(rootBody.angularVelocity, lateralTurnAxis);
            Vector3d axisRate = rootBody == null ? Vector3d.zero : Vector3d.Cross(rootBody.angularVelocity, frame.up);
            double turnRate = axisRate.magnitude;
            int supports = 0;
            foreach (ModuleWheelBase wheel in supportWheels)
                if (wheel != null && wheel.isGrounded) supports++;
            var flight = new Flight { Height = ground.Clearance, Vx = lateralError.magnitude, Vz = vz,
                UniversalTime = now,
                SurfaceHorizontalSpeed = lateral.magnitude,
                SeaLevelAltitude = ground.SeaLevelHeight, SurfaceSpeed = velocity.magnitude,
                Propellant = propellant,
                IgnitionElapsed = ignitionElapsed,
                EngineThrustMeasured = true,
                Mass = vessel.GetTotalMass(), Contact = vessel.LandedOrSplashed || supports >= 2,
                AlignmentSeconds = tilt / TurnDegreesPerSecond, UllageSeconds = UllageAllowance,
                UprightAngle = uprightAngle, TerminalAlignmentSeconds = uprightAngle * Mathf.Rad2Deg / TurnDegreesPerSecond,
                PitchRate = pitchRate,
                AttitudeRateMeasured = rootBody != null,
                LateralAxisRate = Vector3d.Dot(axisRate, horizontal), UpAxisRate = Vector3d.Dot(axisRate, up),
                LateralVelocityLookahead = 0.6 * cfg.AttitudeResponseSeconds,
                GearCommanded = vessel.ActionGroups[KSPActionGroup.Gear] };
            bool trackingCoast = UsesCoastAttitude(lastPhase, ignitionBlocked);
            double actualThrust = 0;
            int enginesFiring = 0;
            Vector3d thrustVector = Vector3d.zero;
            if (engineThrustAccelerations.Length != engines.Count)
                engineThrustAccelerations = new AttitudeVector[engines.Count];
            flight.EngineThrustAccelerations = engineThrustAccelerations;
            for (int i = 0; i < engines.Count; i++)
            {
                EngineBinding e = engines[i];
                ignitionElapsed[i] = e.Issued && !double.IsNaN(e.ActivatedAt) ? Math.Max(0, now - e.ActivatedAt) : -1;
                actualThrust += e.Engine.finalThrust;
                Vector3d force = e.Engine.finalThrust > 0 ? e.ThrustVector() : Vector3d.zero;
                thrustVector += force;
                Vector3d acceleration = force / flight.Mass;
                engineThrustAccelerations[i] = new AttitudeVector(Vector3d.Dot(acceleration, horizontal),
                    Vector3d.Dot(acceleration, up), Vector3d.Dot(acceleration, crossTrack));
                long bit = 1L << i;
                if (e.Burning)
                {
                    enginesFiring++;
                    flight.Running |= bit;
                    if (e.ThrustEstablished) flight.EstablishedThrust |= bit;
                    e.RequestedAt = double.NaN;
                }
                if (!double.IsNaN(e.RequestedAt) && !e.Burning && !e.Faulted)
                {
                    if (!RcsEnabled && !e.Issued && e.NeedsUllage && e.Stability < 0.99)
                    {
                        if (mode == Mode.Control) e.Cancel(false);
                    }
                    else if (now > e.StartDeadline)
                    {
                        e.Faulted = true;
                        if (mode == Mode.Control) e.Cancel(true);
                        Debug.LogWarning("[RetroLanding] Engine did not establish thrust: " + e.Engine.part.flightID);
                    }
                    else
                    {
                        flight.Starting |= bit;
                        if (e.Issued) flight.IssuedStarts |= bit;
                    }
                }
                if (e.CanStart()) flight.Restartable |= bit;
                bool needsUllage = e.NeedsUllage;
                if (needsUllage && !e.Burning && (flight.Restartable & bit) != 0 && e.Stability < 0.99)
                    flight.Unsettled |= bit;
            }
            flight.LateralThrustDirection = Vector3d.Dot(frame.up, horizontal);
            Vector3d thrustAcceleration = thrustVector / flight.Mass;
            flight.LateralThrustAcceleration = Vector3d.Dot(thrustAcceleration, horizontal);
            flight.VerticalThrustAcceleration = Vector3d.Dot(thrustAcceleration, up);
            flight.CrossThrustAcceleration = Vector3d.Dot(thrustAcceleration, crossTrack);
            double elapsed = now - previousSampleTime;
            if (elapsed > 0 && elapsed <= 2 * cfg.SampleSeconds)
            {
                double turnAcceleration = (turnRate - lastTurnRate) / elapsed;
                measuredTurnAcceleration += Math.Min(1, elapsed / 0.25) * (turnAcceleration - measuredTurnAcceleration);
                if (lastPhase == Phase.Recoast && actualThrust < 1)
                    coastTurnRate += Math.Min(1, elapsed / 0.6) * (turnRate - coastTurnRate);
                else if (actualThrust > 1)
                    coastTurnRate = 0;
                Vector3d acceleration = (velocity - previousSurfaceVelocity) / elapsed + cfg.Gravity * up;
                if (trackingCoast && actualThrust < 1 && velocity.sqrMagnitude > 1)
                {
                    double drag = Math.Max(0, -Vector3d.Dot(acceleration, velocity) / velocity.sqrMagnitude);
                    coastDragRate += Math.Min(1, elapsed / 0.6) * (drag - coastDragRate);
                }
                // Use measured acceleration and the real nozzle axes, including
                // TVC, to estimate aerodynamic force during the powered turn.
                if (flight.Running == previousRunningMask)
                {
                    Vector3d nonThrust = acceleration - 0.5 * (thrustAcceleration + previousThrustAcceleration);
                    nonThrust /= Math.Max(1, nonThrust.magnitude / 25);
                    externalAcceleration += Math.Min(1, elapsed / 0.35) * (nonThrust - externalAcceleration);
                }
            }
            previousSampleTime = now;
            previousRunningMask = flight.Running;
            lastTurnRate = turnRate;
            previousSurfaceVelocity = velocity;
            previousThrustAcceleration = thrustAcceleration;
            flight.CoastDragRate = coastDragRate;
            if (trackingCoast && actualThrust < 1)
            {
                flight.CoastAccelerationMeasured = elapsed > 0 && elapsed <= 2 * cfg.SampleSeconds;
                flight.CoastUpAcceleration = Vector3d.Dot(externalAcceleration, up);
                flight.CoastLateralAcceleration = Vector3d.Dot(externalAcceleration, horizontal);
            }
            flight.MeasuredTurnRate = Math.Max(turnRate, coastTurnRate);
            flight.MeasuredTurnAcceleration = measuredTurnAcceleration;
            flight.CrossExternalAcceleration = Vector3d.Dot(externalAcceleration, crossTrack);
            if ((lastPhase == Phase.Main || lastPhase == Phase.Final || lastPhase == Phase.Yolo) && actualThrust > 1)
            {
                flight.ExternalLateralAcceleration = Vector3d.Dot(externalAcceleration, horizontal);
                flight.ExternalUpAcceleration = Vector3d.Dot(externalAcceleration, up);
            }
            if (lastPhase == Phase.Final && vessel.rootPart.rb != null)
            {
                Vector3d turnAxis = Vector3d.Cross(frame.up, up).normalized;
                double closingRate = Vector3d.Dot(vessel.rootPart.rb.angularVelocity, turnAxis);
                if (closingRate > 0.01)
                {
                    // Side thrust fades during the turn, so estimate its
                    // remaining impulse using half the observed time to upright.
                    double stoppingLead = Math.Min(cfg.AttitudeResponseSeconds, 0.5 * uprightAngle / closingRate);
                    flight.LateralVelocityLookahead = Math.Max(flight.LateralVelocityLookahead, stoppingLead);
                }
            }
            bool unpoweredCoast = trackingCoast && actualThrust < 1 &&
                flight.Running == 0 && flight.Starting == 0;
            // Give mandatory targeted samples first use of the existing FAR budget.
            bool boostback = ReadTargetedCommand(flight, now, elapsed, up, velocity, thrustVector,
                out Command command, out Vector3d boostbackPointing);
            if (unpoweredCoast && !boostback)
            {
                SampleGlideAeroGrid(flight, velocity, up);
                farCoast.Update(vessel, frame, coastPointing, now, ground.SeaLevelHeight, velocity, horizontal, up, cfg, ref flight);
            }
            if (!boostback)
            {
                controller.EntryTiltDegrees = TargetedEntryTilt;
                command = controller.Tick(flight);
                ObserveTargetedRecovery(command, flight);
            }
            DecisionReason = command.Reason;
            if (lastPhase == Phase.EntryBurn && command.Phase != Phase.EntryBurn)
            {
                coastDragRate = 0;
                externalAcceleration = Vector3d.zero;
                previousSampleTime = double.NaN;
                farCoast = new FarCoastPrediction();
            }
            Vector3d requested = command.X * horizontal + command.Up * up;
            double timeToContact = Controller.FinalTimeToContact(flight, cfg);
            if (command.UsesTerminalGuidance && flight.Running != 0)
            {
                // Convert the desired net braking vector to a nozzle target.
                double thrustUp = Math.Max(0.01, command.Up - flight.ExternalUpAcceleration);
                double tangent = 0.2 + Math.Min(0.8, 0.25 * timeToContact);
                double thrustSide = command.X - flight.ExternalLateralAcceleration;
                thrustSide = Math.Max(-thrustUp * tangent, Math.Min(0, thrustSide));
                requested = thrustSide * horizontal + thrustUp * up;
            }
            Vector3d landingPointing = requested.sqrMagnitude > 1e-8 ? requested.normalized : up;
            bool alignForLanding = false;
            if (command.PrepareLandingAttitude && !ignitionBlocked && targeted?.Leg == TargetedLeg.Recovery)
            {
                alignForLanding = targeted.StandardFallback || TargetedLandingGuidance.GlideConflictsWithIgnition(
                    cfg, flight.Height, command.PlannedIgnitionWait, Vector3d.Angle(coastPointing, landingPointing),
                    Vector3d.Angle(frame.up, landingPointing), TurnDegreesPerSecond);
                if (alignForLanding && !targeted.StandardFallback)
                    FallBackTargeted(targeted, "Glide attitude conflicts with safe ignition. Aligning for standard landing.");
            }
            command.PrepareLandingAttitude = alignForLanding;
            Vector3d pointing = command.Phase == Phase.Boostback ? boostbackPointing :
                command.Phase == Phase.EntryBurn ?
                    (controller.EntryTiltDegrees != 0 && command.Engines != 0 && requested.sqrMagnitude > 1e-8
                        ? requested.normalized : braking) :
                UsesCoastAttitude(command.Phase, ignitionBlocked) && !alignForLanding ? coastPointing : landingPointing;
            Vector3d rawPointing = pointing;
            bool inFinal = command.Phase == Phase.Final;
            bool traceFinal = inFinal || lastPhase == Phase.Final;
            // Preserve the planned aim for a cold relight until ignition has begun.
            bool finalSteering = inFinal && ((flight.Running | flight.IssuedStarts) != 0 || actualThrust > 0);
            double finalDemand = inFinal ? AttitudeGuidance.FinalLateralDemand(lateral.magnitude) : 0;
            Vector3 finalAimRate = Vector3.zero;
            Vector3 finalSettledAim = Vector3.zero;
            if (finalSteering)
            {
                pointing = WorldDirection(attitude.FinalPointing(AttitudeCoordinates(pointing), localUp,
                    AttitudeCoordinates(frame.up), lateral.magnitude, finalDemand, cfg.SampleSeconds, timeToContact,
                    out AttitudeVector aimRate, out AttitudeVector settledAim));
                finalAimRate = (Vector3)WorldDirection(aimRate);
                finalSettledAim = (Vector3)WorldDirection(settledAim);
            }
            else
                attitude.ResetFinalPointing();
            if (inFinal && mode == Mode.Control)
            {
                // Keep the normal Final envelope, but reserve part of it to
                // stop the hull when the main burn hands over angular momentum.
                double rateDegrees = flight.MeasuredTurnRate / AttitudeGuidance.Degree;
                double errorDegrees = finalSteering ? Vector3.Angle(frame.up, finalSettledAim) : 0;
                double arrestDemand = Math.Min(1, Math.Max(rateDegrees / 8, errorDegrees / 5));
                LimitFinalGimbals(command.Engines | flight.Starting, Math.Max(finalDemand, arrestDemand));
            }
            else
                ResetFinalGimbals();
            AttitudeVector aim = AttitudeCoordinates(pointing);
            bool glideAttitude = bankedCoast && UsesCoastAttitude(command.Phase, ignitionBlocked) && !alignForLanding;
            AttitudeVector rollForward = glideAttitude ? bankedForward : attitude.RollForward(aim, localUp, north, rollDegrees, cfg.SampleSeconds);
            // KSP's pilot-facing dorsal axis is minus the reference transform's forward axis.
            double rollError = AttitudeGuidance.RollError(AttitudeCoordinates(frame.up), AttitudeCoordinates(-frame.forward), aim, rollForward);
            double targetProjection = Vector3d.Dot(pointing, up);
            double error = Vector3.Angle(frame.up, (Vector3)pointing);
            // A cold main group can light along the braking direction and
            // use its TVC to turn toward the planned powered vector.
            bool coldMain = command.Phase == Phase.Main && flight.Running == 0;
            double startError = coldMain ? Math.Min(error, Vector3.Angle(frame.up, (Vector3)braking)) : error;
            double mainStartAcceleration = 0;
            if (coldMain && startError > Settings.IgnitionToleranceDegrees)
            {
                double available = 0;
                for (int i = 0; i < engines.Count; i++)
                    if ((command.Engines & (1L << i)) != 0) available += engines[i].Max;
                double ceiling = Math.Min(available / flight.Mass, cfg.MaxG * Settings.G0);
                // A cold main start can use an off-axis body heading if its
                // projections already supply the required two braking axes.
                double projected = Controller.ColdMainAcceleration(flight, command.X, command.Up, ceiling);
                if (!double.IsPositiveInfinity(projected))
                {
                    startError = 0;
                    mainStartAcceleration = projected;
                }
                else if (controller.ColdMainBrakeThenTurn(flight, command.Engines, command.X, command.Up, ceiling))
                {
                    startError = 0;
                    mainStartAcceleration = ceiling;
                }
            }
            double bodyPitchAoa = velocity.magnitude >= AttitudeGuidance.RetrogradeMinimumSpeed
                ? AttitudeGuidance.PitchAoa(surfaceRetrograde, AttitudeCoordinates(frame.up), localUp, north) : double.NaN;
            double targetCoastAoa = alignForLanding
                ? AttitudeGuidance.PitchAoa(surfaceRetrograde, aim, localUp, north) : coastPitchAoa;
            CaptureEmbeddedFlight(command, flight, bodyPitchAoa, targetCoastAoa, lateral.magnitude);
            WriteTrace(now, flight, command, error, actualThrust, supports, upright, ground, targetProjection, lateral.magnitude,
                unpoweredCoast, enginesFiring, velocity, pointing, rollError, bodyPitchAoa, targetCoastAoa,
                rootBody == null ? 0 : Vector3.Dot(rootBody.angularVelocity, frame.up));
            state = string.Format(Invariant, "{0} {1} | {2:0} m | Vz {3:0.0} m/s\n{4}",
                mode, command.Phase, flight.Height, vz, command.Reason);
            if (command.Phase == Phase.Coast)
            {
                state += command.PlannedIgnitionWait >= 0 && !double.IsInfinity(command.PlannedIgnitionWait)
                    ? string.Format(Invariant, "\nLanding ignition in {0:0.0} s", command.PlannedIgnitionWait)
                    : "\nLanding ignition estimate unavailable.";
                if (!double.IsNaN(bodyPitchAoa))
                    state += string.Format(Invariant, "\nPitch AoA {0:0.0}° / {1:0.0}°", bodyPitchAoa, coastPitchAoa);
            }
            else if (command.Phase == Phase.EntryBurn)
                state += string.Format(Invariant, "\nSurface speed {0:0} m/s | Target {1:0} m/s", flight.SurfaceSpeed, cfg.EntryBurnSpeed);
            if (mode == Mode.Control && command.Phase != Phase.Landed)
                Execute(now, command, flight, pointing, rollError, startError, upright, targetProjection, actualThrust,
                    mainStartAcceleration, finalSteering, finalDemand, finalAimRate, finalSettledAim, glideAttitude);
            if (traceFinal)
                WriteFinalDiagnostics(now, flight, command, ground, supports, velocity, thrustVector,
                    rawPointing, pointing, WorldDirection(rollForward), rollError, finalSteering, finalDemand, finalAimRate);
            if (mode == Mode.Control && command.Phase == Phase.Landed)
                BeginTouchdown(now);
        }

        private void LimitFinalGimbals(long selected, double demand)
        {
            finalGimbalLimit = 0;
            foreach (PartModule module in torqueModules)
            {
                ModuleGimbal gimbal = module as ModuleGimbal;
                if (gimbal == null) continue;
                bool matched = false;
                if (gimbal.isEnabled && gimbal.moduleIsEnabled && !gimbal.gimbalLock && gimbal.gimbalTransforms != null)
                    for (int i = 0; i < engines.Count && !matched; i++)
                    {
                        EngineBinding engine = engines[i];
                        matched = engine.Engine.part == gimbal.part &&
                            ((selected & (1L << i)) != 0 || engine.Engine.finalThrust > 0) &&
                            gimbal.gimbalTransforms.Exists(engine.UsesGimbal);
                    }
                if (!matched)
                {
                    if (finalGimbals.TryGetValue(gimbal, out FinalGimbalSettings saved))
                    {
                        saved.Restore(gimbal);
                        finalGimbals.Remove(gimbal);
                    }
                    continue;
                }
                if (!finalGimbals.TryGetValue(gimbal, out FinalGimbalSettings original))
                    finalGimbals.Add(gimbal, original = new FinalGimbalSettings(gimbal));
                finalGimbalLimit = Mathf.Max(finalGimbalLimit, original.Apply(gimbal, demand));
            }
        }

        private void ResetFinalGimbals()
        {
            foreach (KeyValuePair<ModuleGimbal, FinalGimbalSettings> entry in finalGimbals)
                if (entry.Key != null) entry.Value.Restore(entry.Key);
            finalGimbals.Clear();
            finalGimbalLimit = 0;
        }

        private AttitudeVector LocalNorth(Vector3d up)
        {
            AttitudeVector normal = AttitudeCoordinates(up);
            AttitudeVector north = AttitudeCoordinates(vessel.mainBody.transform.up).InPlane(normal);
            if (north.Length < 1e-4) north = AttitudeCoordinates(vessel.mainBody.transform.forward).InPlane(normal);
            return north.Unit;
        }

        private static AttitudeVector AttitudeCoordinates(Vector3d v) { return new AttitudeVector(v.x, v.y, v.z); }
        private static Vector3d WorldDirection(AttitudeVector v) { return new Vector3d(v.X, v.Y, v.Z); }


        private void BeginTouchdown(double now)
        {
            if (vessel.Splashed || supportWheels.Count < 3)
            {
                StopMode("Contact confirmed. Throttle released.", true);
                return;
            }
            throttle = 0;
            ApplyIndependentThrottle(0);
            ReleaseMasterThrottle();
            ullaging = false;
            foreach (EngineBinding engine in engines)
                engine.Cancel(true);
            ReleaseLandingThrottle();
            ResetFinalGimbals();
            vessel.ActionGroups.SetGroup(KSPActionGroup.SAS, false);
            settleDeadline = now + 12;
            supportedSince = double.NaN;
            state = "Contact confirmed. Stabilizing on the landing legs.";
        }

        private void SettleOnLegs()
        {
            double now = Planetarium.GetUniversalTime();
            Vector3 up = (Vector3)(vessel.CoMD - vessel.mainBody.position).normalized;
            rotationInput = TrackAttitude(up, 0, 0, true);
            int supported = supportWheels.FindAll(w => w != null && w.isGrounded).Count;
            Rigidbody root = vessel.rootPart.rb;
            bool settled = supported >= 3 && vessel.srf_velocity.sqrMagnitude < 0.25 &&
                root != null && root.angularVelocity.sqrMagnitude < 0.0025f;
            if (!settled) supportedSince = double.NaN;
            else if (double.IsNaN(supportedSince)) supportedSince = now;
            if (now - supportedSince >= 2)
                StopMode("Landing stable. Throttle released.", true);
            else if (now >= settleDeadline)
                StopMode("Touchdown detected, but the vessel did not settle.", true);
        }

        private GroundReading ReadGround(Vector3d up, Vector3d lateral, double verticalSpeed)
        {
            // Use a common ray origin and include the static facility above
            // the planetary terrain, as KSP does when placing pad vessels.
            Vector3d origin = vessel.vesselTransform.position;
            double seaLevelHeight = vessel.mainBody.GetAltitude(origin);
            var ground = new GroundReading
            {
                SeaLevelHeight = seaLevelHeight,
                Distance = double.PositiveInfinity,
                RawDistance = double.PositiveInfinity,
                WheelOffset = double.PositiveInfinity,
                FootClearance = double.PositiveInfinity
            };
            foreach (Part part in vessel.parts)
            {
                Collider collider = part.collider;
                if (collider == null || !collider.enabled || collider.isTrigger || collider is WheelCollider) continue;
                Bounds b = collider.bounds;
                double radius = Math.Abs(up.x) * b.extents.x + Math.Abs(up.y) * b.extents.y + Math.Abs(up.z) * b.extents.z;
                ground.HullOffset = Math.Min(ground.HullOffset, Vector3d.Dot((Vector3d)b.center - origin, up) - radius);
            }
            float depth = (float)Math.Max(1000, seaLevelHeight + 600);
            ReadSupportClearance(origin, up, depth, ref ground);
            double horizontalSpeed = lateral.magnitude;
            Vector3d direction = lateral.normalized;
            int hits = Physics.RaycastNonAlloc((Vector3)origin, (Vector3)(-up), surfaceHits,
                depth, (1 << 15) | LayerUtil.DefaultEquivalent, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < hits; i++)
            {
                RaycastHit hit = surfaceHits[i];
                Collider surface = hit.collider;
                if (surface == null || FlightGlobals.GetPartUpwardsCached(surface.gameObject) != null) continue;
                if (hit.distance < ground.RawDistance)
                {
                    ground.RawDistance = hit.distance;
                    ground.RawCollider = surface;
                }
                if (hit.distance >= ground.Distance) continue;
                if (horizontalSpeed > 0 && lastPhase != Phase.Yolo)
                {
                    Bounds bounds = surface.bounds;
                    Vector3 extents = bounds.extents;
                    double projectedExtent = Math.Abs(direction.x) * extents.x + Math.Abs(direction.y) * extents.y +
                        Math.Abs(direction.z) * extents.z;
                    double distanceToEdge = projectedExtent + Vector3d.Dot((Vector3d)bounds.center - origin, direction);
                    if (!Controller.CanReachSurface(hit.distance + ground.BottomOffset, verticalSpeed, horizontalSpeed, distanceToEdge,
                        cfg.Gravity, cfg.MaxG * Settings.G0)) continue;
                }
                ground.Distance = hit.distance;
                ground.Collider = surface;
            }
            if (ground.Collider == null)
            {
                double terrain = ground.RawCollider == null ? vessel.terrainAltitude : vessel.PQSAltitude();
                ground.Distance = seaLevelHeight - terrain;
            }
            double contactDistance = FootprintGeometry.ContactSurfaceDistance(ground.Distance, seaLevelHeight, vessel.mainBody.ocean);
            if (contactDistance < ground.Distance || (vessel.mainBody.ocean && ground.Collider == null && contactDistance == seaLevelHeight))
            {
                ground.Distance = contactDistance;
                ground.Collider = null;
                ground.WaterSurface = true;
            }
            if (ground.ActiveSupports > 0 && double.IsPositiveInfinity(ground.FootClearance))
                ground.FootClearance = ground.Distance + ground.WheelOffset;
            return ground;
        }

        private void ReadSupportClearance(Vector3d origin, Vector3d up, float depth, ref GroundReading ground)
        {
            foreach (ModuleWheelBase support in supportWheels)
            {
                KSPWheelController wheel = support == null ? null : support.Wheel;
                var driver = wheel == null ? null : wheel.wheelCollider;
                if (driver == null || !driver.isActiveAndEnabled) continue;
                // The native collider owns the scaled centre, radius and current suspension travel.
                WheelCollider collider = driver.GetComponent<WheelCollider>();
                if (collider == null || !collider.enabled || collider.radius <= 0) continue;
                Transform mount = collider.transform;
                if (Vector3d.Dot((Vector3d)mount.up, up) <= 0) continue;
                float reach = collider.radius + collider.suspensionDistance;
                Vector3 foot = mount.TransformPoint(collider.center + reach * Vector3.down);
                ground.ActiveSupports++;
                ground.WheelOffset = Math.Min(ground.WheelOffset, Vector3d.Dot((Vector3d)foot - origin, up));

                // Measure terrain under each support at its live footprint, including on slopes.
                float offset = Math.Max(0.5f, Vector3.Distance(foot, mount.TransformPoint(collider.center)));
                int hits = Physics.RaycastNonAlloc(foot + offset * (Vector3)up, (Vector3)(-up), surfaceHits,
                    depth, (1 << 15) | LayerUtil.DefaultEquivalent, QueryTriggerInteraction.Ignore);
                double nearest = double.PositiveInfinity;
                for (int i = 0; i < hits; i++)
                {
                    RaycastHit hit = surfaceHits[i];
                    if (hit.collider == null || FlightGlobals.GetPartUpwardsCached(hit.collider.gameObject) != null) continue;
                    nearest = Math.Min(nearest, hit.distance - offset);
                }
                double contact = vessel.mainBody.ocean
                    ? FootprintGeometry.ContactSurfaceDistance(nearest, vessel.mainBody.GetAltitude(foot), true) : nearest;
                if (contact == nearest && !double.IsPositiveInfinity(nearest)) ground.FootRayHits++;
                ground.FootClearance = Math.Min(ground.FootClearance, contact);
            }
        }

        private Vector3d TerrainAvoidanceVelocity(double now, Vector3d up, GroundReading ground, Vector3d lateral, double vz)
        {
            if (lastPhase != Phase.Final || suspensionTravel.Count < 3 || vz >= 0)
                return Vector3d.zero;
            double remaining = 2 * ground.Clearance / (-vz + cfg.TouchSpeed);
            double verticalBraking = Vector3d.Dot(previousThrustAcceleration + externalAcceleration, up) - cfg.Gravity;
            double maneuverTime = Math.Min(remaining, -vz / Math.Max(1, verticalBraking));
            if (!hasLandingSite && now >= nextSiteSurvey && lateral.magnitude < 15 && maneuverTime > 4)
            {
                nextSiteSurvey = now + 0.8;
                SurveyLandingSurface(up, ground.Distance, Math.Min(maneuverTime, 12));
            }
            if (!hasLandingSite) return Vector3d.zero;
            Vector3d target = vessel.mainBody.GetWorldSurfacePosition(siteLatitude, siteLongitude, siteAltitude);
            Vector3d offset = target - vessel.vesselTransform.position;
            offset -= up * Vector3d.Dot(offset, up);
            siteDistance = offset.magnitude;
            double travelTime = Math.Max(1.5, Math.Min(5, maneuverTime / 2));
            double speed = Math.Min(5, cfg.Gravity * 0.25 * Math.Max(0, maneuverTime - cfg.AttitudeResponseSeconds) / 2);
            return siteDistance <= 0.35 ? Vector3d.zero : offset.normalized * Math.Min(speed, siteDistance / travelTime);
        }

        private void SurveyLandingSurface(Vector3d up, double distance, double remaining)
        {
            Vector3d origin = vessel.vesselTransform.position;
            double spread = 0, travel = double.PositiveInfinity;
            foreach (KeyValuePair<ModuleWheelBase, float> support in suspensionTravel)
            {
                if (support.Key == null || support.Key.Wheel == null || support.Value <= 0) continue;
                Vector3d offset = (Vector3d)support.Key.Wheel.WheelCenter - origin;
                spread = Math.Max(spread, (offset - up * Vector3d.Dot(offset, up)).magnitude);
                travel = Math.Min(travel, support.Value);
            }
            if (spread <= 0 || double.IsInfinity(travel)) return;

            double levelGrade = travel / (2 * spread);
            double centerOfMassHeight = Math.Max(spread, Vector3d.Dot(vessel.CoMD - origin, up));
            double stableGrade = levelGrade + spread / (2 * centerOfMassHeight);
            float depth = (float)Math.Max(100, distance + 300);
            RaycastHit current;
            if (!SampleSurface(origin, up, depth, out current) || SurfaceGrade(up, current.normal) <= stableGrade) return;

            // Search no farther than a rest-to-rest displacement at a
            // fraction of the available terminal lateral acceleration.
            double reachable = cfg.Gravity * 0.32 * remaining * remaining / 4;
            Vector3d side = Vector3d.Cross(up, horizontal).normalized;
            if (side.sqrMagnitude < 0.1) return;
            Vector3d forward = Vector3d.Cross(side, up).normalized;
            double spacing = Math.Max(2 * spread, reachable / 6);
            for (double radius = spacing; radius <= reachable + 1e-5; radius += spacing)
            {
                RaycastHit best = new RaycastHit();
                double bestGrade = levelGrade;
                for (int bearing = 0; bearing < 12; bearing++)
                {
                    double angle = bearing * 2 * Math.PI / 12;
                    Vector3d point = origin + radius * (Math.Cos(angle) * forward + Math.Sin(angle) * side);
                    RaycastHit hit;
                    if (!SampleSurface(point, up, depth, out hit)) continue;
                    double grade = SurfaceGrade(up, hit.normal);
                    if (grade >= bestGrade) continue;
                    best = hit;
                    bestGrade = grade;
                }
                if (best.collider == null) continue;
                Vector3d target = best.point;
                siteLatitude = vessel.mainBody.GetLatitude(target);
                siteLongitude = vessel.mainBody.GetLongitude(target);
                siteAltitude = vessel.mainBody.GetAltitude(target);
                hasLandingSite = true;
                Debug.LogFormat("[RetroLanding] Selected ground {0:F1} m away: slope {1:F1} to {2:F1} deg.",
                    radius, Math.Atan(SurfaceGrade(up, current.normal)) * Mathf.Rad2Deg, Math.Atan(bestGrade) * Mathf.Rad2Deg);
                return;
            }
        }

        private bool SampleSurface(Vector3d origin, Vector3d up, float depth, out RaycastHit nearest)
        {
            nearest = new RaycastHit();
            if (vessel.mainBody.ocean)
                depth = Math.Min(depth, (float)Math.Max(0, vessel.mainBody.GetAltitude(origin)));
            int hits = Physics.RaycastNonAlloc((Vector3)origin, (Vector3)(-up), surfaceHits, depth,
                (1 << 15) | LayerUtil.DefaultEquivalent, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < hits; i++)
            {
                RaycastHit hit = surfaceHits[i];
                if (hit.collider == null || FlightGlobals.GetPartUpwardsCached(hit.collider.gameObject) != null ||
                    (nearest.collider != null && hit.distance >= nearest.distance)) continue;
                nearest = hit;
            }
            return nearest.collider != null;
        }

        private static double SurfaceGrade(Vector3d up, Vector3d normal)
        {
            double vertical = Math.Max(0.01, Vector3d.Dot(up, normal));
            return (normal - up * vertical).magnitude / vertical;
        }

        private void Execute(double now, Command command, Flight flight, Vector3d pointing, double rollError, double alignment,
                             double upright, double targetProjection, double actualThrust, double mainStartAcceleration,
                             bool finalSteering, double finalDemand, Vector3 finalAimRate, Vector3 finalSettledAim, bool glideAttitude)
        {
            if (vessel.ActionGroups[KSPActionGroup.SAS]) vessel.ActionGroups.SetGroup(KSPActionGroup.SAS, false);
            double angularBraking = 0;
            if (!command.Emergency && command.Phase == Phase.Main)
                angularBraking = 0.65 * Settings.TerminalTurnBraking;
            else if (command.UsesTerminalGuidance)
                angularBraking = Settings.TerminalTurnBraking;
            bool altitudeBlocked = cfg.IgnitionAltitudeBlocked(flight.Height);
            bool entryBurn = command.Phase == Phase.EntryBurn;
            bool ignitionAllowed = command.Phase == Phase.Boostback || entryBurn || !altitudeBlocked;
            bool coasting = entryBurn || UsesCoastAttitude(command.Phase, altitudeBlocked) && !command.PrepareLandingAttitude;
            bool rcs = RcsEnabled;
            bool trackEntryRetrograde = actualThrust < 1 &&
                (entryBurn || (command.Phase == Phase.Coast && flight.SeaLevelAltitude >= cfg.EntryBurnAltitude));
            Phase steeringPhase = command.Phase == Phase.Final && !finalSteering ? Phase.Landed : command.Phase;
            rotationInput = TrackAttitude((Vector3)pointing, rollError, angularBraking, coasting, steeringPhase,
                finalDemand, trackEntryRetrograde, now, finalAimRate, finalSettledAim, glideAttitude);
            if (command.DeployGear) vessel.ActionGroups.SetGroup(KSPActionGroup.Gear, true);
            double ignitionAngle = command.Phase == Phase.Boostback ? command.BoostbackIgnitionAngle :
                Settings.IgnitionToleranceDegrees;
            long preparing = command.Engines == 0 ? flight.Restartable : command.Engines;
            ullaging = command.PrepareBoostbackUllage && rcs && (flight.Unsettled & preparing) != 0;
            bool ready = ignitionAllowed && alignment <= ignitionAngle;
            for (int i = 0; i < engines.Count; i++)
            {
                EngineBinding e = engines[i];
                bool selected = (command.Engines & (1L << i)) != 0;
                if (!selected)
                {
                    if (e.Engine.EngineIgnited || e.Issued || !double.IsNaN(e.RequestedAt)) e.Cancel(true);
                    continue;
                }
                if (!e.Burning && !e.Faulted && (ignitionAllowed || e.Issued))
                {
                    bool unsettled = e.NeedsUllage && !e.Issued && e.Stability < 0.99;
                    if (double.IsNaN(e.RequestedAt) && (!unsettled || rcs))
                        e.QueueStart(now, (command.Phase == Phase.Boostback ?
                            Math.Max(0, alignment - ignitionAngle) : alignment) / TurnDegreesPerSecond +
                            (unsettled ? UllageAllowance : 0));
                    if (unsettled) ready = false;
                    // Continue settling while a starting engine still
                    // produces no thrust. RF can lose stability between ticks.
                    ullaging |= rcs && e.NeedsUllage && alignment <= ignitionAngle;
                }
            }
            throttle = 0;
            double low = 0, high = 0;
            for (int i = 0; i < engines.Count; i++)
            {
                if ((command.Engines & (1L << i)) == 0) continue;
                EngineBinding e = engines[i];
                if (ready && !e.Burning && !e.Issued && e.CanStart())
                {
                    e.Activate(now);
                    if (command.Phase == Phase.Boostback && e.Issued) RecordBoostbackActivation(now, alignment);
                }
                // The alignment and ullage gates apply to new starts. An
                // already armed RF engine consumes a relight if throttle drops to zero.
                if (!e.Burning && !(e.Issued && e.Engine.EngineIgnited)) continue;
                low += e.Min;
                high += e.Max;
            }
            if (high <= 0)
            {
                ApplyIndependentThrottle(0);
                return;
            }
            double mass = flight.Mass;
            double thrust = mass * Math.Max(Controller.Norm(command.X, command.Up), mainStartAcceleration);
            if (command.Emergency && command.VerticalOnly)
                thrust = high;
            else if (command.UsesTerminalGuidance)
            {
                double netUp = Math.Max(0, command.Up - flight.ExternalUpAcceleration);
                // TVC can change the nozzle projection without changing the
                // hull attitude, especially during a fast turn.
                double measuredProjection = actualThrust > 1 ? flight.VerticalThrustAcceleration * mass / actualThrust : upright;
                double projection = Math.Max(0.5, Math.Min(measuredProjection, targetProjection));
                double verticalMargin = high / mass * measuredProjection + flight.ExternalUpAcceleration - cfg.Gravity;
                if (flight.Vx > cfg.TouchSpeed && flight.LateralThrustAcceleration > 0 && verticalMargin > 0 &&
                    flight.Vz * flight.Vz <= 1.8 * flight.Height * verticalMargin)
                    projection = Math.Max(projection, targetProjection);
                thrust = mass * netUp / projection;
            }
            if (!command.Emergency)
                thrust = Math.Min(thrust, mass * cfg.ThrustLimitFor(command.Phase) * Settings.G0);
            // A nonzero RF throttle requests actual ignition. Its physical floor
            // is handled by the group planner.
            throttle = high > low ? Mathf.Clamp((float)((thrust - low) / (high - low)), (float)Settings.MinimumPositiveThrottle, 1) : 1;
            ApplyIndependentThrottle(command.Engines);
        }

        private Vector3 TrackAttitude(Vector3 direction, double rollError, double angularBraking, bool coasting,
                                      Phase phase = Phase.Landed, double finalDemand = 0,
                                      bool trackEntryRetrograde = false, double now = 0, Vector3 finalAimRate = default,
                                      Vector3 finalSettledAim = default, bool glideAttitude = false)
        {
            AttitudeVector swing = AttitudeGuidance.PointingError(AttitudeCoordinates(frame.up),
                AttitudeCoordinates(direction), AttitudeCoordinates(-frame.forward));
            Vector3 error = frame.InverseTransformDirection((Vector3)WorldDirection(swing));
            Rigidbody body = vessel.rootPart.rb;
            Vector3 rate = body == null ? Vector3.zero : frame.InverseTransformDirection(body.angularVelocity);
            double yawAssist = glideAttitude ? AttitudeGuidance.GlideYawAssist(error.z, rate.z) : 1;
            Vector3 positive = Vector3.zero, negative = Vector3.zero;
            bool rcs = RcsEnabled;
            foreach (PartModule module in torqueModules)
            {
                if (module == null || !module.isEnabled || !module.moduleIsEnabled || (!rcs && module is ModuleRCS))
                    continue;
                ModuleGimbal gimbal = module as ModuleGimbal;
                if (gimbal != null && (gimbal.gimbalLock || gimbal.gimbalTransforms == null ||
                    !engines.Exists(engine => engine.Engine.part == gimbal.part && engine.Engine.finalThrust > 0.1 &&
                        gimbal.gimbalTransforms.Exists(engine.UsesGimbal))))
                    continue;
                Vector3 availablePositive, availableNegative;
                ((ITorqueProvider)module).GetPotentialTorque(out availablePositive, out availableNegative);
                positive += availablePositive;
                negative += availableNegative;
            }
            Vector3 inertia = vessel.MOI;
            Vector3 desiredAcceleration;
            if (phase != Phase.Coast || trackEntryRetrograde) pitchTrim = yawTrim = 0;
            if (trackEntryRetrograde)
                desiredAcceleration = Vector3.zero;
            else if (coasting || phase == Phase.Final)
            {
                double pitchAuthority = AngularAuthority(inertia.x, positive.x, negative.x);
                double yawAuthority = AngularAuthority(inertia.z, positive.z, negative.z);
                double maxRate = phase == Phase.Final ? AttitudeGuidance.FinalBodyRateDegrees(finalDemand) * AttitudeGuidance.Degree :
                    AttitudeGuidance.CoastMaxRate;
                Vector3 aimRate = frame.InverseTransformDirection(finalAimRate);
                Vector3 settledError = Vector3.zero;
                if (phase == Phase.Final)
                    settledError = frame.InverseTransformDirection((Vector3)WorldDirection(AttitudeGuidance.PointingError(
                        AttitudeCoordinates(frame.up), AttitudeCoordinates(finalSettledAim), AttitudeCoordinates(-frame.forward))));
                double pitch = phase == Phase.Coast
                    ? AttitudeGuidance.TrimCoastAxis(error.x, rate.x, pitchAuthority, cfg.SampleSeconds, ref pitchTrim)
                    : AttitudeGuidance.DampAxis(error.x, rate.x, pitchAuthority, maxRate, aimRate.x,
                        phase == Phase.Final ? (double?)settledError.x : null);
                double yaw = 0;
                if (yawAssist == 0) yawTrim = 0;
                else if (phase == Phase.Coast)
                    yaw = AttitudeGuidance.TrimCoastAxis(error.z, rate.z, yawAuthority, cfg.SampleSeconds, ref yawTrim);
                else
                    yaw = AttitudeGuidance.DampAxis(error.z, rate.z, yawAuthority, maxRate, aimRate.z,
                        phase == Phase.Final ? (double?)settledError.z : null);
                desiredAcceleration = new Vector3((float)pitch, 0, (float)yaw);
            }
            else
            {
                float rateLimit = angularBraking > 0
                    ? Mathf.Min(0.55f, Mathf.Sqrt((float)(2 * angularBraking) * error.magnitude)) : 0.55f;
                Vector3 targetRate = Vector3.ClampMagnitude(error / 0.8f, rateLimit);
                desiredAcceleration = (targetRate - rate) / 0.22f;
            }
            // Roll around the thrust line, and give nose acquisition priority
            // if the vessel is still far from the requested direction.
            double rollWeight = glideAttitude ? AttitudeGuidance.GlideRollPointingWeight(error.magnitude) :
                1 - Mathf.SmoothStep(0, 1, Mathf.InverseLerp(20, 65, error.magnitude * Mathf.Rad2Deg));
            if (phase == Phase.Final)
            {
                // A lightly damped roll can consume vector-control authority
                // while the nozzle is trying to arrest pitch or yaw.
                double swingRate = Math.Sqrt(rate.x * rate.x + rate.z * rate.z) * Mathf.Rad2Deg;
                rollWeight *= 1 - Mathf.SmoothStep(0, 1, Mathf.InverseLerp(2, 8,
                    Mathf.Max(error.magnitude * Mathf.Rad2Deg, (float)swingRate)));
            }
            desiredAcceleration.y = (float)AttitudeGuidance.DampAxis(rollError * rollWeight, rate.y,
                AngularAuthority(inertia.y, positive.y, negative.y), phase == Phase.Final ? AttitudeGuidance.FinalRollRate : 0.35);
            Vector3 signedTorque = Vector3.Scale(desiredAcceleration, inertia);
            foreach (EngineBinding engine in engines)
                if (engine.Burning) signedTorque -= engine.NeutralMoment * engine.Engine.finalThrust;
            // KSP maps positive control inputs onto negative vessel axes.
            var input = new Vector3(RotationAxis(signedTorque.x, positive.x, negative.x),
                RotationAxis(signedTorque.y, positive.y, negative.y),
                RotationAxis(signedTorque.z, positive.z, negative.z));
            if (trackEntryRetrograde)
            {
                double elapsed = now - previousRetrogradeTime;
                Vector3 targetRate = Vector3.zero;
                if (previousRetrogradeAim.sqrMagnitude == 0 || rcs != previousRetrogradeRcs ||
                    elapsed <= 0 || elapsed > Math.Max(0.25, 2 * cfg.SampleSeconds) || body == null)
                {
                    ResetRetrogradeControl();
                    elapsed = 0;
                }
                else
                {
                    AttitudeVector swingTarget = AttitudeGuidance.PointingError(AttitudeCoordinates(previousRetrogradeAim),
                        AttitudeCoordinates(direction), AttitudeCoordinates(-frame.forward));
                    targetRate = frame.InverseTransformDirection((Vector3)WorldDirection(swingTarget)) / (float)elapsed;
                }
                FlightCtrlState applied = vessel.ctrlState;
                double step = elapsed > 0 ? elapsed : cfg.SampleSeconds;
                double pitchAuthority = Math.Max(Math.Abs(positive.x), Math.Abs(negative.x)) / Math.Max(1e-6, inertia.x);
                double yawAuthority = Math.Max(Math.Abs(positive.z), Math.Abs(negative.z)) / Math.Max(1e-6, inertia.z);
                input.x = (float)-retrogradePitch.Update(error.x, rate.x, targetRate.x, -applied.pitch,
                    pitchAuthority, elapsed, step);
                input.z = (float)-retrogradeYaw.Update(error.z, rate.z, targetRate.z, -applied.yaw,
                    yawAuthority, elapsed, step);
                previousRetrogradeTime = now;
                previousRetrogradeAim = direction;
                previousRetrogradeRcs = rcs;
            }
            else if (previousRetrogradeAim.sqrMagnitude != 0)
                ResetRetrogradeControl();
            input.z *= (float)yawAssist;
            if (glideAttitude) input.y = (float)AttitudeGuidance.LimitGlideRoll(input.x, input.y, input.z);
            return input;
        }

        private void ResetRetrogradeControl()
        {
            previousRetrogradeAim = Vector3d.zero;
            retrogradePitch = default;
            retrogradeYaw = default;
        }

        private static double AngularAuthority(float inertia, float positive, float negative)
        {
            return Math.Min(Math.Abs(positive), Math.Abs(negative)) / Math.Max(1e-6, inertia);
        }

        private static float RotationAxis(float torque, float positive, float negative)
        {
            float available = Mathf.Max(Mathf.Abs(positive), Mathf.Abs(negative));
            return Mathf.Clamp(-torque / Mathf.Max(available, 0.001f), -1, 1);
        }

        private void ApplyControls(FlightCtrlState input)
        {
            if (mode != Mode.Control || suspendedForWarp || RailsWarpActive || vessel == null || vessel.packed) return;
            input.pitch = rotationInput.x;
            input.roll = rotationInput.y;
            input.yaw = rotationInput.z;
            if (ullaging && RcsEnabled) input.Z = -1;
            if (!independentThrottle) return;
            if (unmanagedEngines.Exists(e => e.Engine != null && e.Engine.isEnabled && e.Engine.moduleIsEnabled &&
                e.Engine.EngineIgnited && !e.Engine.independentThrottle))
                ReleaseMasterThrottle(input);
            else
            {
                input.mainThrottle = throttle > 0 ? 1 : 0;
                if (vessel == FlightGlobals.ActiveVessel) FlightInputHandler.state.mainThrottle = input.mainThrottle;
                masterThrottleOwned = true;
            }
        }

        private void ReleaseMasterThrottle(FlightCtrlState input = null)
        {
            if (!masterThrottleOwned) return;
            masterThrottleOwned = false;
            if (input != null) input.mainThrottle = 0;
            if (vessel == null) return;
            vessel.ctrlState.mainThrottle = 0;
            if (vessel == FlightGlobals.ActiveVessel) FlightInputHandler.state.mainThrottle = 0;
        }

        private void ReleaseLandingThrottle()
        {
            ReleaseMasterThrottle();
            if (!independentThrottle) return;
            foreach (EngineBinding engine in engines)
                if (engine.Engine != null) engine.ReturnToSharedThrottle();
            independentThrottle = false;
        }

        private void OnModuleSnapshotSave(GameEvents.FromToAction<ProtoPartModuleSnapshot, ConfigNode> snapshot)
        {
            if (mode != Mode.Control || !independentThrottle || snapshot.from.moduleRef == null) return;
            EngineBinding binding = engines.Find(e => e.Engine == snapshot.from.moduleRef);
            if (binding != null) binding.SaveOriginalThrottle(snapshot.to);
        }

        private void OnVesselSnapshotSave(GameEvents.FromToAction<ProtoVessel, ConfigNode> snapshot)
        {
            if (!masterThrottleOwned || vessel == null || snapshot.from.vesselID != vessel.id) return;
            ConfigNode controls = snapshot.to.GetNode("CTRLSTATE");
            if (controls != null) controls.SetValue("mainThrottle", "0", false);
        }

        private void ApplyIndependentThrottle(long selected)
        {
            if (!independentThrottle) return;
            for (int i = 0; i < engines.Count; i++)
                engines[i].Engine.independentThrottlePercentage = (selected & (1L << i)) != 0 ? 100 * throttle : 0;
        }

        private void AttachControls()
        {
            vessel.OnFlyByWire += ApplyControls;
            // A running onboard autopilot does not require a ground command.
            // RemoteTech applies sanctioned controls after its link policy.
            foreach (AssemblyLoader.LoadedAssembly loaded in AssemblyLoader.loadedAssemblies)
            {
                Type api = loaded.assembly.GetType("RemoteTech.API.API");
                if (api == null) continue;
                MethodInfo hasComputer = api.GetMethod("HasFlightComputer", new[] { typeof(Guid) });
                var signature = new[] { typeof(Guid), typeof(Action<FlightCtrlState>) };
                MethodInfo add = api.GetMethod("AddSanctionedPilot", signature);
                MethodInfo remove = api.GetMethod("RemoveSanctionedPilot", signature);
                if (add == null || remove == null || hasComputer == null ||
                    !(bool)hasComputer.Invoke(null, new object[] { vessel.id })) break;
                remoteTechVesselId = vessel.id;
                Action<FlightCtrlState> pilot = ApplyControls;
                add.Invoke(null, new object[] { remoteTechVesselId, pilot });
                remoteTechPilot = pilot;
                removeRemoteTechPilot = remove;
                MethodInfo hasConnection = api.GetMethod("HasAnyConnection", new[] { typeof(Guid) });
                if (hasConnection != null)
                    remoteTechHasConnection = (Func<Guid, bool>)Delegate.CreateDelegate(typeof(Func<Guid, bool>), hasConnection);
                Debug.Log("[RetroLanding] Registered onboard pilot with RemoteTech.");
                return;
            }
        }

        private void DetachControls()
        {
            if (vessel != null)
                vessel.OnFlyByWire -= ApplyControls;
            if (remoteTechPilot != null)
            {
                try { removeRemoteTechPilot.Invoke(null, new object[] { remoteTechVesselId, remoteTechPilot }); }
                catch (Exception ex) { Debug.LogException(ex); }
            }
            remoteTechPilot = null;
            removeRemoteTechPilot = null;
            remoteTechHasConnection = null;
            remoteTechConnected = null;
        }

        private void OpenTrace()
        {
            string directory = RecoveryLogDirectory;
            Directory.CreateDirectory(directory);
            string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff", Invariant);
            trace = File.CreateText(Path.Combine(directory, "flight-" + stamp + ".csv"));
            trace.AutoFlush = true;
            engineTrace = File.CreateText(Path.Combine(directory, "flight-engines-" + stamp + ".csv"));
            engineTrace.AutoFlush = true;
            trace.WriteLine("ut,mode,phase,clearance_m,vx_mps,vz_mps,mass_t,requested_mask,running_mask,starting_mask,available_mask,requested_g,actual_g,align_deg,limits_g,estimated_lead_s,gear_commanded,gear_deployed,gear_total,emergency,gear_grounded,vessel_situation,body_tilt_deg,lateral_axis,reason,yolo_trigger,capped_plan_margin_m,asl_m,ground_distance_m,bottom_offset_m,ground_source,ground_collider,ground_layer,ground_collider_id,planned_terminal_s,raw_ground_distance_m,raw_ground_collider,requested_lateral_mps2,requested_up_mps2,planned_join_clearance_m,planned_join_down_mps,external_lateral_mps2,external_up_mps2,final_reserve_s,actual_lateral_mps2,actual_up_mps2,target_tilt_deg,pitch_rate_deg_s,measured_turn_deg_s,ignition_alignment_s,coast_drag_rate_s_inv,foot_clearance_m,landing_site_distance_m,measured_turn_accel_deg_s2" +
                ",far_forecast_s,far_drag_mps2,measured_coast_drag_mps2,planned_ignition_wait_s" +
                ",previous_throttle,vessel_throttle,total_thrust_kn,engines_firing,yolo_inhibit,main_retrograde_offset_deg" +
                ",surface_speed_mps,vertical_speed_mps,horizontal_speed_mps,fuel_optimization_enabled,fuel_plan_selected,planned_propellant_kg,yolo_state,established_thrust_mask,managed_independent_throttle" +
                ",target_retrograde_offset_deg,body_retrograde_error_deg,roll_target_deg,roll_error_deg,roll_rate_deg_s,previous_pitch_input,previous_roll_input,previous_yaw_input,propellant_shortfall_kg" +
                ",coast_pitch_aoa_deg,max_ignition_agl_m,ignition_altitude_blocked,rcs_enabled,warp_rate,solid_burn_active" +
                ",entry_burn_asl_m,entry_burn_speed_mps,effective_coast_pitch_aoa_deg,planned_join_mass_t,planned_final_throttle" +
                ",body_coast_pitch_aoa_deg,final_gimbal_limit_deg,master_throttle_owned" +
                ",hull_clearance_m,wheel_bottom_offset_m,support_colliders_active,foot_raycast_hits" +
                ",retrograde_pitch_authority_deg_s2,retrograde_yaw_authority_deg_s2,applied_pitch_input,applied_yaw_input" +
                ",latitude_deg,longitude_deg,touchdown_estimate_status,touchdown_estimate_lat_deg,touchdown_estimate_lon_deg" +
                ",touchdown_waypoint_miss_m,touchdown_estimate_eta_s,touchdown_estimate_entry_s,touchdown_estimate_landing_burn_s" +
                ",touchdown_waypoint_bearing_deg,touchdown_waypoint_east_m,touchdown_waypoint_north_m" +
                BoostbackTraceColumns + ",render_frame,render_dt_s,far_total_calls,far_total_ms,far_peak_frame_ms,targeted_glide_tolerance_m");
            engineTrace.WriteLine("ut,bit,flight_id,part,rf,ignitions,ullage,min_kn,max_kn,actual_kn,spool_s,spool_source,available,issued,faulted,armed,flameout,previous_throttle,rf_combusting,vessel_throttle,engine_propellant_available,remotetech_connected,engine_independent_throttle_fraction,is_solid,activate_ut");
            for (int i = 0; i < engines.Count; i++)
                Debug.LogFormat("[RetroLanding] bit {0}: {1} flightID={2}", i, engines[i].Engine.part.name, engines[i].Engine.part.flightID);
            lastMask = -1;
            lastPhase = Phase.Coast;
            lastYoloInhibit = null;
            lastYoloState = null;
            nextLog = 0;
        }

        private void WriteTrace(double now, Flight s, Command c, double error, double thrust, int supports,
                                double upright, GroundReading ground, double targetProjection, double horizontalSpeed,
                                bool unpoweredCoast, int enginesFiring, Vector3d velocity, Vector3d pointing, double rollError,
                                double bodyPitchAoa, double coastPitchAoa, double rollRate)
        {
            if (trace == null || (now < nextLog && c.Phase == lastPhase && c.Engines == lastMask &&
                c.YoloTrigger == null && c.YoloInhibit == lastYoloInhibit && c.YoloState == lastYoloState)) return;
            double requestedG = c.Engines == 0 ? 0 : Controller.Norm(c.Up, c.X) / Settings.G0;
            if (c.Engines != 0 && c.UsesTerminalGuidance && s.Running != 0)
                requestedG = Math.Max(0, c.Up - s.ExternalUpAcceleration) / (Settings.G0 * Math.Max(0.5, targetProjection));
            double phaseLimit = cfg.ThrustLimitFor(c.Phase);
            if (!c.Emergency) requestedG = Math.Min(requestedG, phaseLimit);
            int deployed = gearModules.FindAll(g => g != null && g.fsm != null && g.fsm.CurrentState == g.st_deployed).Count;
            Collider surface = ground.Collider;
            Collider rawSurface = ground.RawCollider;
            string row = string.Format(Invariant,
                "{0:F3},{1},{2},{3:F2},{4:F2},{5:F2},{6:F3},{7},{8},{9},{10},{11:F3},{12:F3},{13:F2},{14:F2},{15:F2},{16},{17},{18},{19},{20},{21},{22:F2},{23:F3},\"{24}\",{25},{26},{27:F2},{28:F2},{29:F2},{30},\"{31}\",{32},{33},{34:F2},{35},\"{36}\",{37:F2},{38:F2},{39:F2},{40:F2},{41:F2},{42:F2},{43:F2},{44:F2},{45:F2},{46:F2},{47:F2},{48:F2},{49:F2},{50:F5},{51},{52},{53:F2}",
                now, mode, c.Phase, s.Height, horizontalSpeed, s.Vz, s.Mass, c.Engines, s.Running, s.Starting, s.Restartable,
                requestedG, thrust / (s.Mass * Settings.G0), error, phaseLimit, c.IgnitionLead,
                s.GearCommanded, deployed, gearModules.Count, c.Emergency, supports, vessel.situation,
                Math.Acos(Math.Max(-1, Math.Min(1, upright))) * 180 / Math.PI, s.LateralThrustDirection,
                c.Reason.Replace("\"", "\"\""), c.YoloTrigger,
                double.IsInfinity(c.CappedPlanMargin) ? "" : c.CappedPlanMargin.ToString("F2", Invariant),
                ground.SeaLevelHeight, ground.Distance, ground.BottomOffset,
                ground.WaterSurface ? "ocean_surface" : surface != null ? "raycast" : rawSurface != null ? "pqs" : "vessel_terrain_altitude",
                surface == null ? "" : surface.name.Replace("\"", "\"\""),
                surface == null ? "" : surface.gameObject.layer.ToString(Invariant),
                surface == null ? "" : surface.GetInstanceID().ToString(Invariant), c.PlannedTerminalSeconds,
                rawSurface == null ? "" : ground.RawDistance.ToString("F2", Invariant),
                rawSurface == null ? "" : rawSurface.name.Replace("\"", "\"\""),
                c.X, c.Up, c.PlannedJoinHeight, c.PlannedJoinSpeed, s.ExternalLateralAcceleration, s.ExternalUpAcceleration,
                cfg.PrecisionSeconds, s.LateralThrustAcceleration, s.VerticalThrustAcceleration,
                Math.Acos(Math.Max(-1, Math.Min(1, targetProjection))) * 180 / Math.PI,
                s.PitchRate * 180 / Math.PI, s.MeasuredTurnRate * 180 / Math.PI, c.IgnitionAlignmentSeconds, s.CoastDragRate,
                double.IsInfinity(ground.FootClearance) ? "" : ground.FootClearance.ToString("F2", Invariant),
                hasLandingSite ? siteDistance.ToString("F2", Invariant) : "", s.MeasuredTurnAcceleration * 180 / Math.PI);
            string retrogradeOffset = c.Phase == Phase.Main && velocity.sqrMagnitude > 1e-8
                ? Vector3.Angle((Vector3)pointing, (Vector3)(-velocity)).ToString("F2", Invariant) : "";
            bool haveRetrograde = velocity.magnitude >= AttitudeGuidance.RetrogradeMinimumSpeed;
            string steering = string.Format(Invariant, ",{0},{1},{2:F2},{3:F2},{4:F2},{5:F4},{6:F4},{7:F4}",
                haveRetrograde ? Vector3.Angle((Vector3)pointing, (Vector3)(-velocity)).ToString("F2", Invariant) : "",
                haveRetrograde ? Vector3.Angle(frame.up, (Vector3)(-velocity)).ToString("F2", Invariant) : "",
                rollDegrees, rollError / AttitudeGuidance.Degree, rollRate / AttitudeGuidance.Degree,
                rotationInput.x, rotationInput.y, rotationInput.z);
            trace.WriteLine(row + string.Format(Invariant, ",{0:F3},{1},{2},{3},{4:F4},{5:F4},{6:F2},{7},{8},{9},{10:F2},{11:F2},{12:F2},{13},{14},{15},{16},{17},{18}",
                s.AeroForecast == null ? 0 : s.AeroForecast.Duration - s.AeroForecastAge,
                unpoweredCoast && !double.IsNaN(farCoast.ModeledDrag) ? farCoast.ModeledDrag.ToString("F2", Invariant) : "",
                unpoweredCoast && !double.IsNaN(farCoast.MeasuredDrag) ? farCoast.MeasuredDrag.ToString("F2", Invariant) : "",
                double.IsNaN(c.PlannedIgnitionWait) ? "" : c.PlannedIgnitionWait.ToString("F3", Invariant),
                throttle, vessel.ctrlState.mainThrottle, thrust, enginesFiring, c.YoloInhibit, retrogradeOffset,
                velocity.magnitude, s.Vz, horizontalSpeed, cfg.OptimizeFuel, c.FuelPlanSelected,
                c.PlannedPropellantKg > 0 ? c.PlannedPropellantKg.ToString("F1", Invariant) : "", c.YoloState, s.EstablishedThrust,
                independentThrottle) + steering + string.Format(Invariant, ",{0:F1},{1:F2},{2:F2},{3},{4},{5:F2},{6},{7:F2},{8:F2},{9:F2},{10},{11},{12},{13},{14}",
                c.PropellantShortfallKg, cfg.CoastPitchAoaDegrees, cfg.MaxIgnitionAltitude, cfg.IgnitionAltitudeBlocked(s.Height),
                RcsEnabled, TimeWarp.CurrentRate, s.SolidBurnActive, cfg.EntryBurnAltitude, cfg.EntryBurnSpeed,
                c.Phase == Phase.Coast ? coastPitchAoa : 0,
                c.PlannedJoinMass > 0 ? c.PlannedJoinMass.ToString("F3", Invariant) : "",
                double.IsNaN(c.PlannedFinalThrottle) ? "" : c.PlannedFinalThrottle.ToString("F4", Invariant),
                double.IsNaN(bodyPitchAoa) ? "" : bodyPitchAoa.ToString("F2", Invariant),
                finalGimbals.Count != 0 ? finalGimbalLimit.ToString("F2", Invariant) : "", masterThrottleOwned) +
                string.Format(Invariant, ",{0:F2},{1},{2},{3}", ground.HullClearance,
                    ground.ActiveSupports == 0 ? "" : ground.WheelOffset.ToString("F2", Invariant),
                    ground.ActiveSupports, ground.FootRayHits) +
                string.Format(Invariant, ",{0},{1},{2:F4},{3:F4}",
                    retrogradePitch.Authority > 0 ? (retrogradePitch.Authority / AttitudeGuidance.Degree).ToString("F3", Invariant) : "",
                    retrogradeYaw.Authority > 0 ? (retrogradeYaw.Authority / AttitudeGuidance.Degree).ToString("F3", Invariant) : "",
                    vessel.ctrlState.pitch, vessel.ctrlState.yaw) + FootprintTrace() + BoostbackTrace(now) +
                string.Format(Invariant, ",{0},{1:F4},{2},{3:F3},{4:F3},{5}", Time.frameCount, Time.unscaledDeltaTime,
                    farCoast.QueryCount, farCoast.QueryMilliseconds, farCoast.TakePeakFrameMilliseconds(),
                    c.Phase == Phase.Coast && targeted?.Leg == TargetedLeg.Recovery && !targeted.StandardFallback
                        ? TargetedLandingGuidance.GlideTolerance(s.SeaLevelAltitude, targeted.Height, cfg).ToString("F2", Invariant) : ""));
            for (int i = 0; i < engines.Count; i++)
            {
                EngineBinding e = engines[i];
                engineTrace.WriteLine(string.Format(Invariant,
                    "{0:F3},{1},{2},{3},{4},{5},{6:F3},{7:F2},{8:F2},{9:F2},{10},{11},{12},{13},{14},{15},{16},{17:F4},{18},{19:F4},{20},{21},{22},{23},{24}",
                    now, i, e.Engine.part.flightID, e.Engine.part.name, e.Rf, e.Ignitions, e.Stability,
                    e.Min, e.Max, e.Engine.finalThrust,
                    EngineResponse.Known(e.SpoolSeconds) ? e.SpoolSeconds.ToString("F3", Invariant) : "",
                    e.SpoolSource, (s.Restartable & (1L << i)) != 0, e.Issued, e.Faulted,
                    e.Engine.EngineIgnited, e.Engine.flameout, throttle, e.RfCombusting,
                    vessel.ctrlState.mainThrottle, e.RfPropellantAvailable, remoteTechConnected,
                    e.Engine.independentThrottle ? (e.Engine.independentThrottlePercentage / 100).ToString("F4", Invariant) : "", false,
                    FootprintAtmosphere.Finite(e.ActivatedAt) ? e.ActivatedAt.ToString("F3", Invariant) : ""));
            }
            lastPhase = c.Phase;
            if (lastMask >= 0 && c.Engines != lastMask) BankChanges++;
            lastMask = c.Engines;
            lastYoloInhibit = c.YoloInhibit;
            lastYoloState = c.YoloState;
            nextLog = now + 0.1;
        }

        private void StopMode(string message, bool onContact)
        {
            Mode prior = mode;
            mode = Mode.Off;
            StopTargeted();
            ResetFootprint();
            controller?.CancelCatalogSearch();
            throttle = 0;
            ullaging = false;
            ResetFinalGimbals();
            suspendedForWarp = false;
            if (prior == Mode.Control)
            {
                ReleaseMasterThrottle();
                DetachControls();
                try
                {
                    if (vessel != null)
                    {
                        foreach (EngineBinding e in engines)
                            if (e.Engine != null && e.Engine.vessel == vessel && (e.Issued || onContact))
                                e.Cancel(true);
                        vessel.Autopilot.SetMode(previousAutopilot);
                        vessel.ActionGroups.SetGroup(KSPActionGroup.SAS, previousSas);
                    }
                }
                finally
                {
                    ReleaseLandingThrottle();
                }
            }
            if (windowMechJeb != null)
            {
                try { windowMechJebInhibit.SetValue(windowMechJeb, false); }
                catch (Exception ex) { Debug.LogException(ex); }
            }
            windowMechJeb = null;
            windowMechJebInhibit = null;
            independentThrottle = false;
            if (trace != null) { trace.Dispose(); trace = null; }
            if (engineTrace != null) { engineTrace.Dispose(); engineTrace = null; }
            CloseFinalDiagnostics();
            state = message;
        }

        private void OnDestroy()
        {
            engineBay.Dispose();
            GameEvents.onVesselGoOnRails.Remove(OnVesselGoOnRails);
            GameEvents.onProtoPartModuleSnapshotSave.Remove(OnModuleSnapshotSave);
            GameEvents.onProtoVesselSave.Remove(OnVesselSnapshotSave);
            GameEvents.onHideUI.Remove(HideUI);
            GameEvents.onShowUI.Remove(ShowUI);
            try { StopMode("Flight scene ended", false); }
            finally
            {
                ReleaseToolbar();
                if (Instance == this) Instance = null;
            }
        }
    }
}
