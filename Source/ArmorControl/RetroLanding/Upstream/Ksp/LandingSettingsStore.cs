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
        internal bool OptimizeFuel;

        internal LandingTuning(Settings settings, double roll)
        {
            MaxG = settings.MaxG;
            FinalSeconds = settings.PrecisionSeconds;
            Roll = roll;
            CoastPitch = settings.CoastPitchAoaDegrees;
            MaxIgnitionAltitude = settings.MaxIgnitionAltitude;
            EntryAltitude = settings.EntryBurnAltitude;
            EntrySpeed = settings.EntryBurnSpeed;
            OptimizeFuel = settings.OptimizeFuel;
        }

        internal void Apply(Settings settings, out double roll)
        {
            settings.MaxG = MaxG;
            settings.PrecisionSeconds = FinalSeconds;
            roll = Roll;
            settings.CoastPitchAoaDegrees = CoastPitch;
            settings.MaxIgnitionAltitude = MaxIgnitionAltitude;
            settings.EntryBurnAltitude = EntryAltitude;
            settings.EntryBurnSpeed = EntrySpeed;
            settings.OptimizeFuel = OptimizeFuel;
        }

        public bool Equals(LandingTuning other)
        {
            return MaxG == other.MaxG && FinalSeconds == other.FinalSeconds && Roll == other.Roll &&
                CoastPitch == other.CoastPitch && MaxIgnitionAltitude == other.MaxIgnitionAltitude &&
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
            return new LandingTuning
            {
                MaxG = Number(node, "maxG", fallback.MaxG, double.Epsilon, 20),
                FinalSeconds = Number(node, "finalSeconds", fallback.FinalSeconds, double.Epsilon),
                Roll = AttitudeGuidance.WrapDegrees(Number(node, "roll", fallback.Roll, -double.MaxValue)),
                CoastPitch = Number(node, "coastPitch", fallback.CoastPitch, -90, 90),
                MaxIgnitionAltitude = Number(node, "maxIgnitionAltitude", fallback.MaxIgnitionAltitude, 0),
                EntryAltitude = Number(node, "entryAltitude", fallback.EntryAltitude, double.Epsilon),
                EntrySpeed = Number(node, "entrySpeed", fallback.EntrySpeed, double.Epsilon),
                OptimizeFuel = bool.TryParse(node.GetValue("optimizeFuel"), out bool fuel) ? fuel : fallback.OptimizeFuel
            };
        }

        private static double Number(ConfigNode node, string key, double fallback, double min, double max = double.MaxValue)
        {
            return double.TryParse(node.GetValue(key), NumberStyles.Float, Invariant, out double value) &&
                value >= min && value <= max ? value : fallback;
        }

        private static ConfigNode WriteNode(string name, LandingTuning values)
        {
            var node = new ConfigNode(name);
            node.AddValue("maxG", values.MaxG.ToString("R", Invariant));
            node.AddValue("finalSeconds", values.FinalSeconds.ToString("R", Invariant));
            node.AddValue("roll", values.Roll.ToString("R", Invariant));
            node.AddValue("coastPitch", values.CoastPitch.ToString("R", Invariant));
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
