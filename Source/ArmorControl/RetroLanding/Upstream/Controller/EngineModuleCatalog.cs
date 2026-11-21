using System;
using System.Collections.Generic;
using System.Linq;

namespace LandingPoc
{
    internal static class EngineModuleCatalog
    {
        internal const int Capacity = 35;
        internal const int ExhaustiveLimit = 9;

        // Use the in-game full-throttle capability, even when the module is idle.
        // Stable IDs also resolve ties between several engine modules on one part.
        internal static void KeepStrongest<T>(List<T> modules, Func<T, double> thrust,
            Func<T, uint> partId, Func<T, int> moduleIndex)
        {
            if (modules.Count <= Capacity) return;
            var kept = modules.OrderByDescending(m => {
                double value = thrust(m);
                return double.IsNaN(value) || double.IsInfinity(value) ? double.NegativeInfinity : value;
            }).ThenBy(partId).ThenBy(moduleIndex).Take(Capacity).ToArray();
            modules.Clear();
            modules.AddRange(kept);
        }

        internal static long All(int count)
        {
            if (count < 0 || count > Capacity) throw new ArgumentOutOfRangeException("count");
            return (1L << count) - 1;
        }

        // Editor families are hints for the geometry search. A family still
        // has to pass the same force and moment checks as any other group.
        internal static long[] EditorFamilyMasks(int count, IList<int[]> families)
        {
            if (families == null || count <= ExhaustiveLimit) return new long[0];
            var masks = new HashSet<long>();
            foreach (int[] slots in families)
            {
                long whole = 0;
                foreach (int i in slots)
                    if (i >= 0 && i < count) whole |= 1L << i;
                if (whole != 0) masks.Add(whole);
            }
            return masks.OrderBy(m => m).ToArray();
        }
    }
}
