using System;
using System.Collections.Generic;
using LandingPoc;
using UnityEngine;

namespace KspRetroLanding
{
    public sealed partial class KspLanding
    {
        internal int DiscoveredCount { get; private set; }
        internal int AdmittedCount => engines.Count;
        internal int BankChanges { get; private set; }
        internal string State => state;
        internal string DecisionReason { get; private set; }
        internal string CurrentPhase => lastPhase.ToString();
        internal float CommandedThrottle => throttle;
        internal double FarDrag => farCoast == null ? double.NaN : farCoast.ModeledDrag;
        internal int SelectedCount
        {
            get
            {
                int count = 0;
                for (long mask = Math.Max(0L, lastMask); mask != 0; mask &= mask - 1) count++;
                return count;
            }
        }

        private void AdmitModules()
        {
            DiscoveredCount = engines.Count;
            if (engines.Count == 0) throw new InvalidOperationException("No enabled engine modules.");
            if (engines.Count > EngineModuleCatalog.Capacity)
            {
                double pressure = vessel.staticPressurekPa / 101.325;
                foreach (var entry in engines) entry.Measure(pressure, TimeWarp.fixedDeltaTime);
                var discovered = engines.ToArray();
                EngineModuleCatalog.KeepStrongest(engines, e => e.Max, e => e.Engine.part.flightID,
                    e => e.Engine.part.Modules.IndexOf(e.Engine));
                foreach (EngineBinding entry in discovered)
                    if (!engines.Contains(entry)) unmanagedEngines.Add(entry);
            }
            ignitionElapsed = new double[engines.Count];
            BankChanges = 0;
            DecisionReason = null;
        }

        private List<int[]> ReadEditorFamilies(Engine[] specs)
        {
            var families = new List<List<int>>();
            var byIdentity = new Dictionary<KeyValuePair<uint, int>, List<int>>();
            for (int index = 0; index < engines.Count; index++)
            {
                ModuleEngines module = engines[index].Engine;
                Part part = module.part;
                uint family = part.flightID;
                if (part.symmetryCounterparts != null)
                    foreach (Part other in part.symmetryCounterparts)
                        if (other != null && other.vessel == vessel) family = Math.Min(family, other.flightID);
                var key = new KeyValuePair<uint, int>(family, part.Modules.IndexOf(module));
                List<int> slots;
                if (!byIdentity.TryGetValue(key, out slots))
                {
                    slots = new List<int>();
                    byIdentity.Add(key, slots);
                    families.Add(slots);
                }
                slots.Add(index);
            }
            var ordered = new List<int[]>();
            foreach (var slots in families)
            {
                double x = 0, y = 0;
                foreach (int i in slots) { x += specs[i].X; y += specs[i].Y; }
                x /= slots.Count;
                y /= slots.Count;
                slots.Sort((a, b) => {
                    int order = Math.Atan2(specs[a].Y-y, specs[a].X-x)
                        .CompareTo(Math.Atan2(specs[b].Y-y, specs[b].X-x));
                    return order != 0 ? order : a.CompareTo(b);
                });
                ordered.Add(slots.ToArray());
            }
            return ordered;
        }
    }
}
