using System;
using System.Collections.Generic;

namespace LandingPoc
{
    internal struct PropellantDemand
    {
        internal readonly int Resource;
        internal readonly double Fraction, ReachableTons;

        internal PropellantDemand(int resource, double fraction, double reachableTons)
        {
            Resource = resource;
            Fraction = fraction;
            ReachableTons = reachableTons;
        }
    }

    // The same bank may read the same tank from each engine. Within a
    // resource, use the smallest reachable amount and largest mass fraction.
    // This also gives a conservative budget with partial crossfeed or mixed banks.
    internal sealed class PropellantBudget
    {
        private readonly PropellantDemand[][] engines;
        private readonly Dictionary<long, Dictionary<int, PropellantDemand>> groups =
            new Dictionary<long, Dictionary<int, PropellantDemand>>();

        internal PropellantBudget(PropellantDemand[][] engines)
        {
            this.engines = engines;
        }

        internal double Shortfall(long firstMask, double firstTons, long finalMask, double finalTons)
        {
            if (firstTons <= 0 && finalTons <= 0) return 0;
            var first = Demands(firstTons > 0 ? firstMask : 0);
            var final = Demands(finalTons > 0 ? finalMask : 0);
            double shortage = 0;
            foreach (PropellantDemand resource in first.Values)
            {
                PropellantDemand later;
                double available = resource.ReachableTons;
                double required = resource.Fraction * firstTons;
                if (final.TryGetValue(resource.Resource, out later))
                {
                    available = Math.Min(available, later.ReachableTons);
                    required += later.Fraction * finalTons;
                }
                shortage = Math.Max(shortage, required - available);
            }
            foreach (PropellantDemand resource in final.Values)
                if (!first.ContainsKey(resource.Resource))
                    shortage = Math.Max(shortage, resource.Fraction * finalTons - resource.ReachableTons);
            return shortage;
        }

        private Dictionary<int, PropellantDemand> Demands(long mask)
        {
            Dictionary<int, PropellantDemand> result;
            if (groups.TryGetValue(mask, out result)) return result;
            result = new Dictionary<int, PropellantDemand>();
            for (int i = 0; i < engines.Length; i++)
            {
                if ((mask & (1L << i)) == 0) continue;
                foreach (PropellantDemand fuel in engines[i])
                {
                    PropellantDemand earlier;
                    result[fuel.Resource] = result.TryGetValue(fuel.Resource, out earlier)
                        ? new PropellantDemand(fuel.Resource, Math.Max(fuel.Fraction, earlier.Fraction),
                            Math.Min(fuel.ReachableTons, earlier.ReachableTons))
                        : fuel;
                }
            }
            groups.Add(mask, result);
            return result;
        }
    }
}
