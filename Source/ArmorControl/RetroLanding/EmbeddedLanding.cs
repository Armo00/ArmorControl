using System;
using System.Collections.Generic;
using System.IO;
using ArmorOverhaul.ArmorControl.Protocol;
using LandingPoc;
using UnityEngine;

namespace KspRetroLanding
{
    public sealed partial class KspLanding
    {
        private RetroLandingFlight? embeddedFlight;

        private static LandingSettingsStore OpenEmbeddedSettings()
        {
            string gameData = Path.Combine(KSPUtil.ApplicationRootPath, "GameData");
            string path = Path.Combine(gameData, "ArmorControl", "PluginData", "RetroLanding.cfg");
            string legacy = Path.Combine(gameData, "KSP_Landing_Assist", "PluginData", "Settings.cfg");
            if (!File.Exists(path) && File.Exists(legacy))
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    File.Copy(legacy, path);
                }
                catch (IOException error)
                {
                    Debug.LogWarning("[ArmorControl] Cannot migrate Retro Landing settings: " + error.Message);
                }
                catch (UnauthorizedAccessException error)
                {
                    Debug.LogWarning("[ArmorControl] Cannot migrate Retro Landing settings: " + error.Message);
                }
            }
            return new LandingSettingsStore(path);
        }

        internal LandingTuning Tuning => new LandingTuning(cfg, rollDegrees);
        internal bool OnRails => RailsWarpActive;
        internal bool HasFlightState => embeddedFlight != null && !IsIdle;

        internal void SaveTuning(LandingTuning tuning)
        {
            tuning.Apply(cfg, out rollDegrees);
            settingsStore.SaveCurrent(tuning);
        }

        internal void SaveDefaults() => settingsStore.SaveDefaults(Tuning);
        internal void ResetDefaults() => settingsStore.Reset().Apply(cfg, out rollDegrees);

        internal void EngageEmbedded()
        {
            embeddedFlight = null;
            Engage();
        }

        internal RetroLandingFlight? FlightReadback => HasFlightState && !suspendedForWarp ? embeddedFlight : null;

        private void CaptureEmbeddedFlight(Command command, Flight flight, double pitchAoa, double targetAoa, double horizontalSpeed)
        {
            embeddedFlight = new RetroLandingFlight
            {
                Height = flight.Height, VerticalSpeed = flight.Vz, HorizontalSpeed = horizontalSpeed,
                IgnitionWait = command.Phase == Phase.Coast && command.PlannedIgnitionWait >= 0
                    ? command.PlannedIgnitionWait : double.NaN,
                PitchAoa = pitchAoa, TargetAoa = command.Phase == Phase.Coast ? targetAoa : double.NaN,
                SurfaceSpeed = flight.SurfaceSpeed,
                EntrySpeed = command.Phase == Phase.EntryBurn ? cfg.EntryBurnSpeed : double.NaN
            };
        }

        internal RetroEngineBaySnapshot CaptureEngineBay(Vessel active)
        {
            if (active == null || !active.loaded || active.packed || active.ReferenceTransform == null) return null;
            bool controlling = !IsIdle && vessel == active;
            return engineBay.CaptureEmbedded(active, controlling ? engines : null, controlling ? Math.Max(0L, lastMask) : 0);
        }
    }

    internal sealed partial class LandingEngineBay
    {
        internal RetroEngineBaySnapshot CaptureEmbedded(Vessel active, IList<EngineBinding> controlled, long command)
        {
            Capture(active, controlled, command);
            var result = new RetroEngineBaySnapshot
            {
                Engines = new RetroEngineSnapshot[readings.Count],
                Center = center, ThrustCenter = forceCenter, Radius = radius, Thrust = totalThrust,
                Producing = producing, Commanded = commanded
            };
            var points = new List<Vector2>[readings.Count];
            foreach (NozzleReading nozzle in nozzles)
            {
                if (points[nozzle.Slot] == null) points[nozzle.Slot] = new List<Vector2>();
                points[nozzle.Slot].Add(nozzle.Position);
            }
            for (int i = 0; i < readings.Count; i++)
            {
                EngineReading data = readings[i];
                Part part = data.Engine.part;
                result.Engines[i] = new RetroEngineSnapshot
                {
                    PartId = part.flightID, ModuleIndex = part.Modules.IndexOf(data.Engine),
                    Name = part.partInfo?.title ?? part.name, Label = data.Label,
                    Producing = data.On, Commanded = data.Commanded, Thrust = data.Thrust,
                    Nozzles = points[i]?.ToArray() ?? Array.Empty<Vector2>()
                };
            }
            return result;
        }
    }
}
