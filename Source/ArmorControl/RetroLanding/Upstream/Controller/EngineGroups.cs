using System;
using System.Collections.Generic;
using System.Linq;

namespace LandingPoc
{
    internal struct Torque
    {
        internal readonly double X, Y, Z;
        internal double Magnitude => Controller.Norm(Controller.Norm(X, Y), Z);

        internal Torque(double x, double y, double z) { X = x; Y = y; Z = z; }

        public static Torque operator +(Torque a, Torque b) { return new Torque(a.X + b.X, a.Y + b.Y, a.Z + b.Z); }
        public static Torque operator *(Torque a, double n) { return new Torque(a.X * n, a.Y * n, a.Z * n); }

        internal double FractionOf(Torque available, double tolerance)
        {
            double x = Excess(X, available.X, tolerance);
            double y = Excess(Y, available.Y, tolerance);
            double z = Excess(Z, available.Z, tolerance);
            return Math.Sqrt(x * x + y * y + z * z);
        }

        private static double Excess(double demand, double available, double tolerance)
        {
            double excess = Math.Max(0, Math.Abs(demand) - tolerance);
            return excess == 0 ? 0 : excess / Math.Max(1e-8, available);
        }
    }

    internal readonly struct EngineMount
    {
        internal readonly double X, Y, RingSquared;

        internal EngineMount(double x, double y, double ringSquared) { X = x; Y = y; RingSquared = ringSquared; }
    }

    internal sealed class EngineGroup
    {
        public long Mask;
        public int Count;
        public double Min, Max;
        internal double RadiusSquared, RadialSum, Lead, Balance, TrimOffset, SpecificImpulse;
        private double mountX, mountY;
        internal int StartsRemaining;
        internal bool HasGimbal, ContainsUnusableEngine, QuantifiedControl;
        internal Torque MinimumMoment, MaximumMoment, MinimumAuthority, MaximumAuthority;
        internal Torque MinimumForce, MaximumForce;

        internal EngineGroup Add(Engine engine, EngineMount mount, long bit)
        {
            return new EngineGroup
            {
                Mask = Mask | bit, Count = Count + 1, Min = Min + engine.Min, Max = Max + engine.Max,
                RadiusSquared = Math.Max(RadiusSquared, mount.RingSquared), RadialSum = RadialSum + mount.RingSquared,
                mountX = mountX + mount.X, mountY = mountY + mount.Y,
                MinimumMoment = MinimumMoment + engine.Moment * engine.Min,
                MaximumMoment = MaximumMoment + engine.Moment * engine.Max,
                MinimumAuthority = MinimumAuthority + engine.Authority * engine.Min,
                MaximumAuthority = MaximumAuthority + engine.Authority * engine.Max,
                MinimumForce = MinimumForce + engine.Axis * engine.Min,
                MaximumForce = MaximumForce + engine.Axis * engine.Max,
                HasGimbal = HasGimbal || engine.Gimbal,
                ContainsUnusableEngine = ContainsUnusableEngine || engine.Max <= 0,
                QuantifiedControl = QuantifiedControl || engine.QuantifiedControl,
                Lead = Math.Max(Lead, EngineResponse.Known(engine.SpoolSeconds) ? engine.SpoolSeconds : double.PositiveInfinity),
                SpecificImpulse = SpecificImpulse > 0 && engine.SpecificImpulse > 0
                    ? Math.Min(SpecificImpulse, engine.SpecificImpulse) : Math.Max(SpecificImpulse, engine.SpecificImpulse),
                StartsRemaining = Math.Min(Count == 0 ? int.MaxValue : StartsRemaining,
                    engine.Ignitions < 0 ? int.MaxValue : engine.Ignitions)
            };
        }

        internal bool Evaluate(Torque backup)
        {
            // Reserve control away from the static trim point, including on the
            // high-thrust end. A shared throttle interpolates the two endpoints.
            Torque minControl = MinimumAuthority + backup;
            Torque maxControl = MaximumAuthority + backup;
            TrimOffset = Math.Max(Min == 0 ? 0 : MinimumMoment.Magnitude / Min,
                Max == 0 ? 0 : MaximumMoment.Magnitude / Max);
            Balance = Math.Max(MinimumMoment.FractionOf(minControl, 0.02 * Min),
                MaximumMoment.FractionOf(maxControl, 0.02 * Max));
            bool steerable = QuantifiedControl ? maxControl.X > 0 && maxControl.Z > 0 :
                HasGimbal || (backup.X > 0 && backup.Z > 0);
            return !ContainsUnusableEngine && Max > 0 && steerable && Balance <= 0.8 &&
                FollowsControlAxis(MaximumForce, Max) && FollowsControlAxis(MinimumForce, Min);
        }

        private static bool FollowsControlAxis(Torque force, double thrust)
        {
            double aimTolerance = Settings.IgnitionToleranceDegrees * Math.PI / 180;
            // Guidance points the reference axis and uses the sum of engine
            // thrust magnitudes. Reject a group whose net force diverges materially.
            return force.Y >= thrust * Math.Cos(aimTolerance) &&
                Controller.Norm(force.X, force.Z) <= force.Y * Math.Tan(aimTolerance);
        }

        internal static int InsideFirst(EngineGroup a, EngineGroup b)
        {
            int outer = Math.Round(a.RadiusSquared, 4).CompareTo(Math.Round(b.RadiusSquared, 4));
            if (outer != 0) return outer;
            int inner = Math.Round(a.RadialSum, 4).CompareTo(Math.Round(b.RadialSum, 4));
            return inner != 0 ? inner : Math.Round(a.InstallationOffsetSquared, 4)
                .CompareTo(Math.Round(b.InstallationOffsetSquared, 4));
        }

        private double InstallationOffsetSquared => (mountX * mountX + mountY * mountY) / Math.Max(1, Count * Count);

        internal bool CenteredInstallation
        {
            get
            {
                double tolerance = EngineGroups.MountingTolerance + 0.015 * Math.Sqrt(RadiusSquared);
                return InstallationOffsetSquared <= tolerance * tolerance;
            }
        }

        internal static int CompareBalance(EngineGroup a, EngineGroup b)
        {
            // Small motion of the center of mass should not reorder
            // otherwise equivalent engine groups.
            return Math.Round(a.TrimOffset, 2).CompareTo(Math.Round(b.TrimOffset, 2));
        }

        internal static int BalancedFirst(EngineGroup a, EngineGroup b)
        {
            int balance = CompareBalance(a, b);
            return balance != 0 ? balance : InsideFirst(a, b);
        }
    }

    internal sealed class EngineGroups
    {
        private const int BeamWidth = 12;
        private const int RetainedPerCount = 5;
        internal const double MountingTolerance = 0.04;
        private readonly Engine[] engines;
        private readonly EngineMount[] mounts;
        private readonly Torque backup;

        internal EngineGroups(Engine[] engines, Torque backup)
        {
            this.engines = engines;
            this.backup = backup;
            mounts = MeasureInstallation(engines);
        }

        private static EngineMount[] MeasureInstallation(Engine[] engines)
        {
            // The same physical engines remain on their rings when fuel
            // moves the CoM or an engine becomes unavailable for restart.
            double centerX = 0, centerY = 0;
            int count = 0;
            foreach (Engine engine in engines)
            {
                if (!engine.HasMount) continue;
                centerX += engine.X;
                centerY += engine.Y;
                count++;
            }
            var result = new EngineMount[engines.Length];
            if (count == 0) return result;
            centerX /= count;
            centerY /= count;
            var radius = new double[engines.Length];
            for (int i = 0; i < engines.Length; i++)
                radius[i] = Controller.Norm(engines[i].X - centerX, engines[i].Y - centerY);
            double ring = 0;
            foreach (int i in Enumerable.Range(0, engines.Length).Where(i => engines[i].HasMount).OrderBy(i => radius[i]))
            {
                if (radius[i] - ring > MountingTolerance + 0.015 * ring) ring = radius[i];
                result[i] = new EngineMount(engines[i].X - centerX, engines[i].Y - centerY, ring * ring);
            }
            return result;
        }

        internal void Populate(List<EngineGroup> result, long available, long running, long[] editorMasks, params long[] preserve)
        {
            result.Clear();
            var included = new HashSet<long>();
            AddSymmetricGroups(result, included, available);
            if (running != 0 && running != available) AddSymmetricGroups(result, included, running);
            if (engines.Length <= EngineModuleCatalog.ExhaustiveLimit)
            {
                for (long mask = 1; mask <= available; mask++)
                    if ((mask & available) == mask && included.Add(mask)) AddIfUsable(result, mask);
                result.Sort((a, b) => a.Mask.CompareTo(b.Mask));
                return;
            }

            AddKnownGroups(result, included, available, running, editorMasks, preserve);
            Search(result, included, available, running);
            // A group that can take over an already burning set cannot rely on
            // a complementary engine from the general available pool.
            if (running != 0 && running != available) Search(result, included, running, 0);
            result.Sort((a, b) => a.Mask.CompareTo(b.Mask));
        }

        internal void PopulateFromMasks(List<EngineGroup> result, long available, long running, long[] editorMasks,
                                        List<EngineGroup> previous, params long[] preserve)
        {
            result.Clear();
            var included = new HashSet<long>();
            AddKnownGroups(result, included, available, running, editorMasks, preserve);
            for (int i = 0; i < engines.Length; i++)
            {
                long mask = 1L << i;
                if ((available & mask) != 0 && included.Add(mask)) AddIfUsable(result, mask);
            }
            if (previous == null)
            {
                AddSymmetricGroups(result, included, available);
                if (running != 0 && running != available) AddSymmetricGroups(result, included, running);
            }
            else
            {
                foreach (EngineGroup group in previous)
                    if ((group.Mask & available) == group.Mask && included.Add(group.Mask)) AddIfUsable(result, group.Mask);
            }
            result.Sort((a, b) => a.Mask.CompareTo(b.Mask));
        }

        private void AddKnownGroups(List<EngineGroup> result, HashSet<long> included, long available, long running,
                                    long[] editorMasks, long[] preserve)
        {
            foreach (long mask in preserve.Concat(new[] { running, available }).Concat(editorMasks.Select(m => m & available)))
                if (mask != 0 && (mask & available) == mask && included.Add(mask)) AddIfUsable(result, mask);
        }

        private void AddIfUsable(List<EngineGroup> result, long mask)
        {
            EngineGroup group = MakeGroup(mask);
            if (group.Evaluate(backup)) result.Add(group);
        }

        internal EngineGroup MakeGroup(long mask)
        {
            var group = new EngineGroup();
            for (int i = 0; i < engines.Length; i++)
                if ((mask & (1L << i)) != 0) group = group.Add(engines[i], mounts[i], 1L << i);
            return group;
        }

        private void AddSymmetricGroups(List<EngineGroup> result, HashSet<long> included, long available)
        {
            int[] slots = Enumerable.Range(0, engines.Length)
                .Where(i => (available & (1L << i)) != 0 && engines[i].Max > 0).ToArray();
            for (int order = 2; order <= slots.Length; order++)
            {
                double angle = 2 * Math.PI / order;
                double cos = Math.Cos(angle), sin = Math.Sin(angle);
                var orbits = new List<EngineGroup>();
                long used = 0;
                foreach (int start in slots)
                {
                    if ((used & (1L << start)) != 0) continue;
                    long orbit = RotationOrbit(slots, start, order, cos, sin);
                    if (orbit == 0 || (orbit & used) != 0) continue;
                    used |= orbit;
                    orbits.Add(MakeGroup(orbit));
                    if (included.Add(orbit)) AddIfUsable(result, orbit);
                }
                if (orbits.Count < 2) continue;

                // Retain a high-thrust and a low-minimum candidate at each
                // size of symmetric bank without enumerating all unions.
                AddOrbitUnions(result, included, orbits.OrderByDescending(g => g.Max));
                AddOrbitUnions(result, included, orbits.OrderBy(g => g.Min));
            }
        }

        private void AddOrbitUnions(List<EngineGroup> result, HashSet<long> included, IEnumerable<EngineGroup> orbits)
        {
            var prefixes = new long[engines.Length + 1];
            foreach (EngineGroup orbit in orbits)
            {
                for (int i = engines.Length - orbit.Count; i >= 0; i--)
                {
                    if (i > 0 && prefixes[i] == 0) continue;
                    int next = i + orbit.Count;
                    if (prefixes[next] == 0) prefixes[next] = prefixes[i] | orbit.Mask;
                }
            }
            foreach (long mask in prefixes)
                if (mask != 0 && included.Add(mask)) AddIfUsable(result, mask);
        }

        private long RotationOrbit(int[] slots, int start, int order, double cos, double sin)
        {
            EngineMount first = mounts[start];
            double radius = Controller.Norm(first.X, first.Y);
            if (radius <= MountingTolerance) return 1L << start;
            double tolerance = MountingTolerance + 0.015 * radius;
            long orbit = 1L << start;
            double x = first.X, y = first.Y;
            for (int n = 1; n < order; n++)
            {
                double nextX = x * cos - y * sin;
                y = x * sin + y * cos;
                x = nextX;
                int best = -1;
                double error = tolerance * tolerance;
                foreach (int i in slots)
                {
                    if ((orbit & (1L << i)) != 0) continue;
                    double dx = mounts[i].X - x, dy = mounts[i].Y - y;
                    double distance = dx * dx + dy * dy;
                    if (distance >= error) continue;
                    best = i;
                    error = distance;
                }
                if (best < 0) return 0;
                orbit |= 1L << best;
            }
            return orbit;
        }

        private void Search(List<EngineGroup> result, HashSet<long> included, long available, long running)
        {
            var beam = new List<EngineGroup> { new EngineGroup() };
            EngineGroup prefix = new EngineGroup();
            var sorted = Enumerable.Range(0, engines.Length).Where(i => (available & (1L << i)) != 0)
                .OrderBy(i => mounts[i].RingSquared).ToArray();
            var insideOrder = Comparer<EngineGroup>.Create(EngineGroup.InsideFirst);
            var balancedOrder = Comparer<EngineGroup>.Create(EngineGroup.BalancedFirst);
            for (int depth = 0; depth < sorted.Length; depth++)
            {
                var next = new Dictionary<long, EngineGroup>();
                var usable = new List<EngineGroup>();
                int inside = sorted[depth];
                prefix = prefix.Add(engines[inside], mounts[inside], 1L << inside);
                next.Add(prefix.Mask, prefix);
                foreach (EngineGroup group in beam)
                {
                    foreach (int i in sorted)
                    {
                        long bit = 1L << i;
                        long mask = group.Mask | bit;
                        if (mask == group.Mask || next.ContainsKey(mask)) continue;
                        next.Add(mask, group.Add(engines[i], mounts[i], bit));
                    }
                }
                foreach (EngineGroup group in next.Values)
                    if (group.Evaluate(backup)) usable.Add(group);
                var nearest = usable.OrderBy(g => g, insideOrder).ThenByDescending(g => g.StartsRemaining)
                    .ThenBy(g => g.Balance).ThenBy(g => g.Mask);
                var mostThrust = usable.OrderByDescending(g => g.Max).ThenBy(g => g, insideOrder).ThenBy(g => g.Mask);
                var lowFloor = usable.OrderBy(g => g.Min).ThenBy(g => g, insideOrder).ThenBy(g => g.Mask);
                var quickStart = usable.OrderBy(g => g.Lead).ThenBy(g => g, insideOrder).ThenBy(g => g.Mask);
                var mostBalanced = usable.OrderBy(g => g, balancedOrder);
                var withCurrent = nearest.Where(g => (g.Mask & running) == running);
                foreach (EngineGroup group in nearest.Take(RetainedPerCount).Concat(mostThrust.Take(1))
                    .Concat(lowFloor.Take(1)).Concat(quickStart.Take(1)).Concat(withCurrent.Take(1)).Concat(mostBalanced.Take(1)))
                    if (included.Add(group.Mask)) result.Add(group);

                // Keep unbalanced intermediate sets so an additional engine can
                // cancel their moment. Search stays bounded for large clusters.
                beam = next.Values.OrderBy(g => g.Balance).ThenBy(g => g.TrimOffset)
                    .ThenBy(g => g, insideOrder).ThenBy(g => g.Mask).Take(BeamWidth)
                    .Concat(next.Values.OrderBy(g => g, insideOrder).ThenBy(g => g.Balance).ThenBy(g => g.Mask).Take(BeamWidth))
                    .Concat(next.Values.OrderByDescending(g => g.Max).ThenBy(g => g.Balance).ThenBy(g => g.Mask).Take(BeamWidth / 2))
                    .Concat(withCurrent.Take(1)).Distinct().ToList();
            }
        }
    }
}
