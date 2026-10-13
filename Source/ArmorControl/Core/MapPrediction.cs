using System.Collections.Generic;

namespace ArmorOverhaul.ArmorControl.Core
{
    // Ephemeral main-thread data, never exposed to a transport worker.
    internal sealed class MapPredictionPatch
    {
        internal double Start, End;
        internal Orbit Orbit;
        internal List<MapPoint> Atmosphere;
    }

    internal sealed class MapPrediction
    {
        internal readonly List<MapPredictionPatch> Patches = new List<MapPredictionPatch>();
        internal double EntryUt = double.NaN, ImpactUt = double.NaN;
        internal double FreshUt = double.NaN;
        internal string Status = "unavailable";
    }
}
