using System;
using System.Globalization;
using ArmorOverhaul.ArmorControl.Protocol;
using KspRetroLanding;
using LandingPoc;
using MuMech;
using UnityEngine;

namespace ArmorOverhaul.ArmorControl.Core
{
    internal sealed class RetroLandingControl
    {
        private KspLanding pilot;
        private Vessel ship;
        private Guid lastVessel;
        private MechJebCore mechJeb;
        private bool previousInhibit;
        private bool standaloneLoaded;
        private string lastStatus;
        private bool reportedEngineBayFailure;

        internal bool Active => !ReferenceEquals(ship, null);

        internal void Initialize(GameObject host, double defaultMaxG)
        {
            if (pilot != null) return;
            pilot = host.AddComponent<KspLanding>();
            pilot.SetThrustLimit(defaultMaxG * Settings.G0);
            foreach (AssemblyLoader.LoadedAssembly loaded in AssemblyLoader.loadedAssemblies)
                if (loaded.assembly?.GetName().Name == "KSP_Landing_Assist")
                    standaloneLoaded = true;
        }

        private string CheckStart(Vessel vessel)
        {
            if (standaloneLoaded) return "Disable the standalone Retro Landing plugin and restart KSP.";
            if (pilot == null) return "Retro Landing controller unavailable.";
            if (vessel == null || vessel != FlightGlobals.ActiveVessel || !vessel.loaded || vessel.packed ||
                vessel.LandedOrSplashed || vessel.situation == Vessel.Situations.PRELAUNCH)
                return "An airborne, loaded vessel is required";
            if (pilot.OnRails) return "End on-rails warp before starting landing control.";
            if (vessel.orbit == null || (vessel.situation != Vessel.Situations.FLYING &&
                vessel.orbit.PeA >= vessel.mainBody.atmosphereDepth)) return "A suborbital descent is required";
            if (!vessel.IsControllable || vessel.ReferenceTransform == null || vessel.Autopilot?.SAS == null)
                return "Vessel attitude control is unavailable";
            return null;
        }

        internal string Start(Vessel vessel)
        {
            if (Active) return vessel == ship ? null : "Another recovery is active";
            string reason = CheckStart(vessel);
            if (reason != null) return reason;
            if (!HasEnabledEngine(vessel)) return "No enabled landing engine modules.";

            ship = vessel;
            lastVessel = vessel.id;
            mechJeb = vessel.GetMasterMechJeb();
            previousInhibit = mechJeb != null && mechJeb.DeactivateControl;
            try
            {
                if (mechJeb != null)
                {
                    EndMechJebFlight(mechJeb);
                    mechJeb.DeactivateControl = true;
                }
                pilot.EngageEmbedded();
                if (!pilot.IsIdle)
                {
                    lastStatus = null;
                    return null;
                }
                reason = pilot.State;
            }
            catch (Exception error)
            {
                reason = error.Message;
                Debug.LogException(error);
            }
            Stop(reason);
            return reason;
        }

        internal void Tick()
        {
            if (!Active) return;
            if (pilot == null || pilot.IsIdle) Stop(pilot == null ? "Flight scene ended" : pilot.State);
            else if (!HighLogic.LoadedSceneIsFlight || ship == null || ship != FlightGlobals.ActiveVessel ||
                (mechJeb != null && !mechJeb.DeactivateControl)) Stop("Vessel control changed");
        }

        internal void Stop(string status)
        {
            if (!Active) return;
            try
            {
                if (pilot != null && !pilot.IsIdle) pilot.Disengage();
            }
            finally
            {
                if (mechJeb != null) mechJeb.DeactivateControl = previousInhibit;
                ship = null;
                mechJeb = null;
                lastStatus = status;
            }
        }

        internal string UpdateSetting(string field, string value)
        {
            if (pilot == null) return "Retro Landing controller unavailable.";
            var tuning = pilot.Tuning;
            if (field == "optimizeFuel")
            {
                if (!bool.TryParse(value, out tuning.OptimizeFuel)) return "Invalid Retro Landing setting.";
            }
            else
            {
                if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) ||
                    double.IsNaN(number) || double.IsInfinity(number)) return "Invalid Retro Landing setting.";
                switch (field)
                {
                    case "maxG" when number > 0 && number <= 20: tuning.MaxG = number; break;
                    case "finalSeconds" when number > 0: tuning.FinalSeconds = number; break;
                    case "roll": tuning.Roll = AttitudeGuidance.WrapDegrees(number); break;
                    case "coastPitch" when number >= -90 && number <= 90: tuning.CoastPitch = number; break;
                    case "maxIgnitionKm" when number >= 0 && number <= double.MaxValue / 1000:
                        tuning.MaxIgnitionAltitude = number * 1000; break;
                    case "entryAltitudeKm" when number > 0 && number <= double.MaxValue / 1000:
                        tuning.EntryAltitude = number * 1000; break;
                    case "entrySpeed" when number > 0: tuning.EntrySpeed = number; break;
                    default: return "Invalid Retro Landing setting.";
                }
            }
            pilot.SaveTuning(tuning);
            return null;
        }

        internal bool UpdateDefaults(bool save)
        {
            if (pilot == null) return false;
            if (save) pilot.SaveDefaults();
            else pilot.ResetDefaults();
            return true;
        }

        internal void Report(Vessel vessel, AutomationSnapshot snapshot)
        {
            if (pilot == null) return;
            bool active = Active && vessel == ship && !pilot.IsIdle;
            RetroEngineBaySnapshot bay = null;
            try
            {
                bay = pilot.CaptureEngineBay(vessel);
                reportedEngineBayFailure = false;
            }
            catch (Exception error)
            {
                if (!reportedEngineBayFailure)
                    Debug.LogWarning("[ArmorControl] Retro Landing engine bay unavailable: " + error.Message);
                reportedEngineBayFailure = true;
            }
            string reason = active ? pilot.DecisionReason : CheckStart(vessel);
            if (!active && reason == null && !HasEnabledEngine(vessel))
                reason = "No enabled landing engine modules.";
            snapshot.RetroLanding = new RetroLandingSnapshot
            {
                Tuning = pilot.Tuning, Active = active, CanStart = !active && reason == null,
                Status = active ? pilot.CurrentPhase : vessel.id == lastVessel ? lastStatus : null,
                Reason = reason, Flight = active ? pilot.FlightReadback : null, EngineBay = bay
            };
            snapshot.SuborbitalActive = active;
            snapshot.SuborbitalController = active ? "ArmorControl" : null;
            snapshot.SuborbitalPhase = snapshot.RetroLanding.Status;
            snapshot.SuborbitalWaitReason = active ? reason : null;
            snapshot.SuborbitalThrottle = active ? pilot.CommandedThrottle : double.NaN;
            snapshot.SuborbitalEngineCount = active ? pilot.SelectedCount : 0;
            snapshot.SuborbitalBankChanges = active ? pilot.BankChanges : 0;
            snapshot.SuborbitalDrag = active ? pilot.FarDrag : double.NaN;
            snapshot.SuborbitalAirSource = MapGeometry.Finite(snapshot.SuborbitalDrag) ? "FAR vessel model" : null;
        }

        private static bool HasEnabledEngine(Vessel vessel)
        {
            foreach (Part part in vessel.parts)
            {
                if (part == null || part.Modules == null) continue;
                for (int index = 0; index < part.Modules.Count; index++)
                    if (part.Modules[index] is ModuleEngines engine && engine.isEnabled && engine.moduleIsEnabled &&
                        !EngineBinding.UsesSolidPropellant(engine))
                        return true;
            }
            return false;
        }

        private static void EndMechJebFlight(MechJebCore core)
        {
            var ascent = core.GetComputerModule<MechJebModuleAscentAutopilot>();
            if (ascent != null)
            {
                ascent.timedLaunch = false;
                if (ascent.enabled) ascent.enabled = false;
            }
            if (core.landing?.enabled == true) core.landing.StopLanding();
            var node = core.GetComputerModule<MechJebModuleNodeExecutor>();
            if (node?.enabled == true) node.Abort();
            var docking = core.GetComputerModule<MechJebModuleDockingAutopilot>();
            if (docking?.enabled == true) docking.enabled = false;
            var rendezvous = core.GetComputerModule<MechJebModuleRendezvousAutopilot>();
            if (rendezvous?.enabled == true) rendezvous.enabled = false;
            var staging = core.GetComputerModule<MechJebModuleStagingController>();
            if (staging?.enabled == true) staging.enabled = false;
            var smart = core.GetComputerModule<MechJebModuleSmartASS>();
            if (smart != null && smart.target != MechJebModuleSmartASS.Target.OFF)
            {
                smart.target = MechJebModuleSmartASS.Target.OFF;
                smart.mode = MechJebModuleSmartASS.Target2Mode[(int)smart.target];
                smart.Engage(true);
            }
            if (core.attitude?.enabled == true) core.attitude.attitudeDeactivate();
            if (core.thrust?.enabled == true) core.thrust.ThrustOff();
        }
    }
}
