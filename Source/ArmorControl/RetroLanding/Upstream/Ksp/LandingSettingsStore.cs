using System;
using System.Globalization;
using System.IO;
using LandingPoc;
using UnityEngine;

namespace KspRetroLanding
{
    internal struct LandingTuning : IEquatable<LandingTuning>
    {
        internal double MaxG, FinalSeconds, Roll, CoastPitch, MaxIgnitionAltitude, EntryAltitude, EntrySpeed;
        internal double CoastPitchMin, CoastPitchMax;
        internal double BoostbackMaxG, EntryMaxG, BoostbackStartAoa, BoostbackFineDistance;
        internal double VesselTargetEastOffset, VesselTargetNorthOffset;
        internal bool OptimizeFuel;

        internal LandingTuning(Settings settings, double roll)
        {
            MaxG = settings.MaxG;
            BoostbackMaxG = settings.ThrustLimitFor(Phase.Boostback);
            EntryMaxG = settings.ThrustLimitFor(Phase.EntryBurn);
            BoostbackStartAoa = settings.BoostbackStartAoa;
            BoostbackFineDistance = settings.BoostbackFineDistance;
            FinalSeconds = settings.PrecisionSeconds;
            Roll = roll;
            CoastPitch = settings.CoastPitchAoaDegrees;
            CoastPitchMin = settings.CoastPitchAoaMinimum;
            CoastPitchMax = settings.CoastPitchAoaMaximum;
            VesselTargetEastOffset = settings.VesselTargetEastOffset;
            VesselTargetNorthOffset = settings.VesselTargetNorthOffset;
            MaxIgnitionAltitude = settings.MaxIgnitionAltitude;
            EntryAltitude = settings.EntryBurnAltitude;
            EntrySpeed = settings.EntryBurnSpeed;
            OptimizeFuel = settings.OptimizeFuel;
        }

        internal void Apply(Settings settings, out double roll)
        {
            settings.MaxG = MaxG;
            settings.BoostbackMaxG = BoostbackMaxG;
            settings.EntryMaxG = EntryMaxG;
            settings.BoostbackStartAoa = BoostbackStartAoa;
            settings.BoostbackFineDistance = BoostbackFineDistance;
            settings.PrecisionSeconds = FinalSeconds;
            roll = Roll;
            settings.CoastPitchAoaDegrees = CoastPitch;
            settings.CoastPitchAoaMinimum = CoastPitchMin;
            settings.CoastPitchAoaMaximum = CoastPitchMax;
            settings.VesselTargetEastOffset = VesselTargetEastOffset;
            settings.VesselTargetNorthOffset = VesselTargetNorthOffset;
            settings.MaxIgnitionAltitude = MaxIgnitionAltitude;
            settings.EntryBurnAltitude = EntryAltitude;
            settings.EntryBurnSpeed = EntrySpeed;
            settings.OptimizeFuel = OptimizeFuel;
        }

        public bool Equals(LandingTuning other) => SameFlightSettings(other) &&
            VesselTargetEastOffset == other.VesselTargetEastOffset && VesselTargetNorthOffset == other.VesselTargetNorthOffset;

        // Offsets are captured when a target is locked. Editing them only
        // changes future engagements.
        internal bool SameFlightSettings(LandingTuning other)
        {
            return MaxG == other.MaxG && BoostbackMaxG == other.BoostbackMaxG && EntryMaxG == other.EntryMaxG &&
                BoostbackStartAoa == other.BoostbackStartAoa && BoostbackFineDistance == other.BoostbackFineDistance &&
                FinalSeconds == other.FinalSeconds && Roll == other.Roll &&
                CoastPitch == other.CoastPitch && CoastPitchMin.Equals(other.CoastPitchMin) &&
                CoastPitchMax.Equals(other.CoastPitchMax) && MaxIgnitionAltitude == other.MaxIgnitionAltitude &&
                EntryAltitude == other.EntryAltitude && EntrySpeed == other.EntrySpeed && OptimizeFuel == other.OptimizeFuel;
        }
    }

    internal sealed class LandingSettingsStore
    {
        private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
        private readonly string path;
        private LandingTuning defaults, current;
        internal bool HasCurrent { get; private set; }
        internal LandingTuning Current { get { return current; } }
        internal bool PanelOpen { get; private set; }

        internal LandingSettingsStore(string path)
        {
            this.path = path;
            defaults = new LandingTuning(new Settings(), 0);
            current = defaults;
            try
            {
                if (!File.Exists(path)) return;
                ConfigNode root = ConfigNode.Load(path);
                if (root == null) return;
                defaults = Read(root.GetNode("DEFAULTS"), defaults);
                ConfigNode node = root.GetNode("CURRENT");
                current = Read(node, defaults);
                HasCurrent = node != null;
                PanelOpen = bool.TryParse(root.GetValue("panelOpen"), out bool open) && open;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[RetroLanding] Cannot load landing settings: " + ex.Message);
            }
        }

        internal LandingTuning Reset()
        {
            SaveCurrent(defaults);
            return defaults;
        }

        internal void SaveCurrent(LandingTuning values)
        {
            current = values;
            HasCurrent = true;
            Write();
        }

        internal void SaveDefaults(LandingTuning values)
        {
            defaults = values;
            SaveCurrent(values);
        }

        internal void SavePanelOpen(bool open)
        {
            if (PanelOpen == open) return;
            PanelOpen = open;
            Write();
        }

        private static LandingTuning Read(ConfigNode node, LandingTuning fallback)
        {
            if (node == null) return fallback;
            double landing = Number(node, "maxG", fallback.MaxG, double.Epsilon, 20);
            bool legacy = node.GetValue("maxG") != null;
            return new LandingTuning
            {
                MaxG = landing,
                BoostbackMaxG = Number(node, "boostbackMaxG", legacy ? landing : fallback.BoostbackMaxG, double.Epsilon, 20),
                EntryMaxG = Number(node, "entryMaxG", legacy ? landing : fallback.EntryMaxG, double.Epsilon, 20),
                BoostbackStartAoa = Number(node, "boostbackStartAoa", fallback.BoostbackStartAoa, double.Epsilon, 180),
                BoostbackFineDistance = Number(node, "boostbackFineDistance", fallback.BoostbackFineDistance, double.Epsilon),
                FinalSeconds = Number(node, "finalSeconds", fallback.FinalSeconds, double.Epsilon),
                Roll = AttitudeGuidance.WrapDegrees(Number(node, "roll", fallback.Roll, -double.MaxValue)),
                CoastPitch = Number(node, "coastPitch", fallback.CoastPitch, -90, 90),
                CoastPitchMin = ReadAoaBound(node, "coastPitchMin", fallback.CoastPitchMin),
                CoastPitchMax = ReadAoaBound(node, "coastPitchMax", fallback.CoastPitchMax),
                VesselTargetEastOffset = Number(node, "vesselTargetEastOffset", fallback.VesselTargetEastOffset, -double.MaxValue),
                VesselTargetNorthOffset = Number(node, "vesselTargetNorthOffset", fallback.VesselTargetNorthOffset, -double.MaxValue),
                MaxIgnitionAltitude = Number(node, "maxIgnitionAltitude", fallback.MaxIgnitionAltitude, 0),
                EntryAltitude = Number(node, "entryAltitude", fallback.EntryAltitude, double.Epsilon),
                EntrySpeed = Number(node, "entrySpeed", fallback.EntrySpeed, double.Epsilon),
                OptimizeFuel = bool.TryParse(node.GetValue("optimizeFuel"), out bool fuel) ? fuel : fallback.OptimizeFuel
            };
        }

        private static double ReadAoaBound(ConfigNode node, string key, double fallback) =>
            node.GetValue(key) == "default" ? double.NaN : Number(node, key, fallback, -90, 90);

        private static double Number(ConfigNode node, string key, double fallback, double min, double max = double.MaxValue)
        {
            return double.TryParse(node.GetValue(key), NumberStyles.Float, Invariant, out double value) &&
                value >= min && value <= max ? value : fallback;
        }

        private static ConfigNode WriteNode(string name, LandingTuning values)
        {
            var node = new ConfigNode(name);
            node.AddValue("maxG", values.MaxG.ToString("R", Invariant));
            node.AddValue("boostbackMaxG", values.BoostbackMaxG.ToString("R", Invariant));
            node.AddValue("entryMaxG", values.EntryMaxG.ToString("R", Invariant));
            node.AddValue("boostbackStartAoa", values.BoostbackStartAoa.ToString("R", Invariant));
            node.AddValue("boostbackFineDistance", values.BoostbackFineDistance.ToString("R", Invariant));
            node.AddValue("finalSeconds", values.FinalSeconds.ToString("R", Invariant));
            node.AddValue("roll", values.Roll.ToString("R", Invariant));
            node.AddValue("coastPitch", values.CoastPitch.ToString("R", Invariant));
            node.AddValue("coastPitchMin", double.IsNaN(values.CoastPitchMin) ? "default" : values.CoastPitchMin.ToString("R", Invariant));
            node.AddValue("coastPitchMax", double.IsNaN(values.CoastPitchMax) ? "default" : values.CoastPitchMax.ToString("R", Invariant));
            node.AddValue("vesselTargetEastOffset", values.VesselTargetEastOffset.ToString("R", Invariant));
            node.AddValue("vesselTargetNorthOffset", values.VesselTargetNorthOffset.ToString("R", Invariant));
            node.AddValue("maxIgnitionAltitude", values.MaxIgnitionAltitude.ToString("R", Invariant));
            node.AddValue("entryAltitude", values.EntryAltitude.ToString("R", Invariant));
            node.AddValue("entrySpeed", values.EntrySpeed.ToString("R", Invariant));
            node.AddValue("optimizeFuel", values.OptimizeFuel.ToString());
            return node;
        }

        private void Write()
        {
            string temporary = path + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var root = new ConfigNode();
                root.AddValue("panelOpen", PanelOpen);
                root.AddNode(WriteNode("DEFAULTS", defaults));
                if (HasCurrent) root.AddNode(WriteNode("CURRENT", current));
                if (!root.Save(temporary)) throw new IOException("ConfigNode failed to write the settings.");
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[RetroLanding] Cannot save landing settings: " + ex.Message);
            }
        }
    }
}
