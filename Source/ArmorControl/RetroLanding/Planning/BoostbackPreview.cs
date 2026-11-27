using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace LandingPoc
{
    internal sealed class BoostbackPreviewInput
    {
        internal Flight Flight;
        internal Engine[] Engines;
        internal Settings Settings;
        internal Torque BackupTorque;
        internal double Radius, RotationRate, Latitude, Longitude, EastSpeed, NorthSpeed;
        internal double WaypointLatitude, WaypointLongitude, WaypointHeight;
        internal double MaxDeltaV, PositionTolerance, DryMass, FuelReserve;
        internal double GlideRollRate = AttitudeGuidance.ReferenceRollRate / AttitudeGuidance.Degree, CurrentGlideBank;
        // Observed arrow direction in the retrograde, pitch-up and lateral basis.
        internal AttitudeVector CurrentGlideDirection;
        internal double PreferredGlideAoa = double.NaN, PreferredGlideBank;
        // East, north and up at the captured position.
        internal AttitudeVector BoostbackBodyAxis;
        internal double BoostbackTurnRate;
        internal double DefaultAoa => Settings.CoastPitchAoaDegrees;
        internal double MinimumAoa => Settings.MinimumCoastPitch;
        internal double MaximumAoa => Settings.MaximumCoastPitch;
        internal FootprintAtmosphere DefaultAtmosphere, MinimumAtmosphere, MaximumAtmosphere, BoostbackAtmosphere;
        internal bool ObservedAboveEntry;
        internal double EntryTiltDegrees;
        internal FootprintAtmosphere EntryNoseDownAtmosphere, EntryNoseUpAtmosphere;
    }

    internal readonly struct RecoveryBurnDirections
    {
        internal readonly double BoostPitch, EntryTilt;
        internal RecoveryBurnDirections(double boostPitch, double entryTilt)
        {
            BoostPitch = boostPitch;
            EntryTilt = entryTilt;
        }
    }

    internal sealed class BoostbackPreviewResult
    {
        internal string Status;
        internal BoostbackApproach GlideApproach;
        internal int Footprints, ScreenedFootprints, VerifiedCandidates;
        internal int BoostbackBanks, BoostbackTrials, BoostbackBurnFailures, BoostbackFuelFailures, BoostbackLandingFailures;
        internal double FirstFlyableMilliseconds = double.NaN, RunMilliseconds;
        internal BoostbackCorrectionResult Correction;
        internal BoostbackApproach Baseline, DefaultAoa, MinimumAoa, MaximumAoa, BestPowered;
    }

    internal sealed class BoostbackApproach
    {
        internal TouchdownEstimate Touchdown;
        internal bool Safe;
        internal long Engines, PrecisionEngines;
        internal double EastDeltaV, NorthDeltaV, Heading, LeadSeconds, BurnSeconds, CutoffSeconds;
        internal double Lat, Lon, MissEast, MissNorth, MissDistance, TotalSeconds;
        internal double ForecastLatitude, ForecastLongitude, ForecastEast, ForecastNorth;
        internal double BoostFuel, RecoveryFuel, FuelAtTouchdown, PitchAoa, GlideBank;
        internal double UpDeltaV, BoostThrustDeltaV, PrecisionMinimumThrust;
        internal RecoveryBurnDirections BurnDirections;
        internal bool JointPlan, ReturnOnly;
        internal double DeltaV => Controller.Norm(Controller.Norm(EastDeltaV, NorthDeltaV), UpDeltaV);
        internal double PoweredDeltaV => BoostThrustDeltaV + Touchdown.EntryDeltaV;
        internal BoostbackApproach Copy() => (BoostbackApproach)MemberwiseClone();
    }

    // Evaluates a detached snapshot. The caller owns any flight commands.
    internal sealed partial class BoostbackPreview
    {
        private const double Degree = Math.PI / 180;
        private const int MaximumFootprints = 40;
        private const int MaximumGroups = 4;
        private const double TrajectoryStep = 0.5;
        private const double TrajectoryHorizon = 900;

        private readonly BoostbackPreviewInput input;
        private readonly CancellationToken stop;
        private readonly double gravityParameter;
        private readonly AttitudeVector rotation, initialEast, initialNorth;
        private readonly TrajectoryState initial;
        private int footprints;
        private int poweredTrials, burnFailures, fuelFailures, landingFailures;
        private readonly Dictionary<long, EngineGroup> precisionBanks = new Dictionary<long, EngineGroup>();

        internal static BoostbackPreviewResult Plan(BoostbackPreviewInput input, CancellationToken stop, bool includeGlide = true)
        {
            if (input?.Settings != null && !(input.MinimumAoa <= input.DefaultAoa && input.DefaultAoa <= input.MaximumAoa))
                return new BoostbackPreviewResult { Status = "invalid_aoa_range" };
            if (!Valid(input)) return new BoostbackPreviewResult { Status = "invalid_snapshot" };
            return new BoostbackPreview(input, stop).Plan(includeGlide);
        }

        internal static BoostbackPreviewResult PlanReturn(BoostbackPreviewInput input, CancellationToken stop)
        {
            if (input?.Settings != null && !(input.MinimumAoa <= input.DefaultAoa && input.DefaultAoa <= input.MaximumAoa))
                return new BoostbackPreviewResult { Status = "invalid_aoa_range" };
            if (!Valid(input)) return new BoostbackPreviewResult { Status = "invalid_snapshot" };
            return new BoostbackPreview(input, stop).Plan(includeGlide: false, returnOnly: true);
        }

        private static bool Valid(BoostbackPreviewInput x)
        {
            return x != null && x.Engines != null && x.Engines.Length <= EngineModuleCatalog.Capacity &&
                x.Settings != null && x.DefaultAtmosphere != null && x.Engines.All(engine => engine != null) &&
                FootprintAtmosphere.Finite(x.Flight.Mass) && x.Flight.Mass > x.DryMass && x.DryMass > 0 && x.Flight.Propellant != null &&
                !x.Flight.SolidBurnActive && !x.Flight.Contact && (x.Flight.Running | x.Flight.Starting | x.Flight.IssuedStarts) == 0 &&
                x.Radius > 0 && FootprintAtmosphere.Finite(x.Radius) &&
                x.Settings.Gravity > 0 && FootprintAtmosphere.Finite(x.Settings.Gravity) &&
                x.Settings.MaxG > 0 && FootprintAtmosphere.Finite(x.Settings.MaxG) &&
                x.Settings.SpecificImpulse > 0 && FootprintAtmosphere.Finite(x.Settings.SpecificImpulse) && x.MaxDeltaV >= 0 && x.PositionTolerance > 0 && x.FuelReserve >= 0 &&
                FootprintAtmosphere.Finite(x.MinimumAoa) && FootprintAtmosphere.Finite(x.MaximumAoa) &&
                x.GlideRollRate > 0 && FootprintAtmosphere.Finite(x.GlideRollRate) &&
                x.MinimumAoa <= x.DefaultAoa && x.DefaultAoa <= x.MaximumAoa &&
                x.Flight.AlignmentSeconds >= 0 &&
                FootprintAtmosphere.Finite(x.Flight.AlignmentSeconds) &&
                Math.Abs(x.Latitude) <= 90 && Math.Abs(x.WaypointLatitude) <= 90 &&
                FootprintAtmosphere.Finite(x.Longitude) && FootprintAtmosphere.Finite(x.WaypointLongitude) &&
                FootprintAtmosphere.Finite(x.Flight.Height) &&
                FootprintAtmosphere.Finite(x.WaypointHeight) && x.Flight.SeaLevelAltitude > x.WaypointHeight &&
                FootprintAtmosphere.Finite(x.Flight.SeaLevelAltitude) && FootprintAtmosphere.Finite(x.RotationRate) &&
                FootprintAtmosphere.Finite(x.EastSpeed) && FootprintAtmosphere.Finite(x.NorthSpeed) &&
                FootprintAtmosphere.Finite(x.Flight.Vz) && FootprintAtmosphere.Finite(x.MaxDeltaV);
        }

        private BoostbackPreview(BoostbackPreviewInput input, CancellationToken stop)
        {
            this.input = input;
            this.stop = stop;
            rotation = new AttitudeVector(0, 0, input.RotationRate);
            double lat = input.Latitude * Degree, lon = input.Longitude * Degree;
            AttitudeVector up = new AttitudeVector(Math.Cos(lat) * Math.Cos(lon), Math.Cos(lat) * Math.Sin(lon), Math.Sin(lat));
            initialEast = new AttitudeVector(-Math.Sin(lon), Math.Cos(lon), 0);
            initialNorth = up.Cross(initialEast);
            double radial = input.Radius + input.Flight.SeaLevelAltitude;
            gravityParameter = input.Settings.Gravity * radial * radial;
            initial = new TrajectoryState
            {
                Position = up * radial,
                Velocity = initialEast * input.EastSpeed + initialNorth * input.NorthSpeed + up * input.Flight.Vz,
                ObservedAboveEntry = input.ObservedAboveEntry || input.Flight.SeaLevelAltitude > input.Settings.EntryBurnAltitude
            };
        }

        private BoostbackPreviewResult Plan(bool includeGlide, bool returnOnly = false)
        {
            var output = new BoostbackPreviewResult();
            var elapsed = Stopwatch.StartNew();
            EngineGroup[] banks = Array.Empty<EngineGroup>();
            bool usePreferred = !returnOnly && input.MaxDeltaV == 0 &&
                input.PreferredGlideAoa >= input.MinimumAoa && input.PreferredGlideAoa <= input.MaximumAoa &&
                (input.PreferredGlideAoa != input.DefaultAoa || input.PreferredGlideBank != 0);
            var preferredAir = usePreferred ? PitchAtmosphere(input.PreferredGlideAoa) : null;
            BoostbackApproach retained = preferredAir == null ? null :
                Trace(null, 0, 0, input.PreferredGlideAoa, preferredAir, input.PreferredGlideBank);
            BoostbackApproach baseline = !includeGlide && retained != null ? retained :
                Trace(null, 0, 0, input.DefaultAoa, input.DefaultAtmosphere, returnOnly: returnOnly);
            output.Baseline = baseline;
            BoostbackApproach best = baseline != null && baseline.Safe ? baseline : null;
            if (Reached(best))
                output.Status = "no_burn_required";
            else if (input.MaxDeltaV == 0 ||
                !TargetedLandingGuidance.MayStartBoostback(input.Flight.Height, input.Flight.Vz, input.Settings))
                output.Status = "no_solution_in_budget";
            else
            {
                banks = FindBanks();
                foreach (EngineGroup bank in banks)
                {
                    stop.ThrowIfCancellationRequested();
                    output.BoostbackBanks++;
                    BoostbackApproach attempt = Refine(bank, baseline, returnOnly: returnOnly);
                    if (attempt != null && attempt.Engines != 0 && Better(attempt, output.BestPowered)) output.BestPowered = attempt;
                    if (Better(attempt, best)) best = attempt;
                    if (Reached(best) || footprints >= MaximumFootprints ||
                        returnOnly && TargetedLandingGuidance.CanStartBoostbackWithTrim(output.BestPowered, baseline)) break;
                }
                output.Status = Reached(best) ? "planned" :
                    banks.Length == 0 ? "no_engine_group" : "no_solution_in_budget";
            }

            output.DefaultAoa = best;
            if (Reached(best)) output.FirstFlyableMilliseconds = elapsed.Elapsed.TotalMilliseconds;
            if (!returnOnly && !includeGlide && input.Settings.OptimizeRecoveryTrajectory && banks.Length != 0)
                SearchJoint(output, banks, baseline, elapsed);
            if (!includeGlide && output.Status == "no_solution_in_budget" &&
                TargetedLandingGuidance.CanStartBoostbackWithTrim(output.BestPowered, baseline))
            {
                output.DefaultAoa = output.BestPowered;
                output.Status = "planned_with_trim";
                output.FirstFlyableMilliseconds = elapsed.Elapsed.TotalMilliseconds;
            }
            if (includeGlide) PlanGlide(output, best, retained, preferredAir != null);
            if (returnOnly)
            {
                if (output.Status == "planned" || output.Status == "planned_with_trim") output.Status = "return_planned";
                output.DefaultAoa = AsReturnOnly(output.DefaultAoa);
                output.Baseline = AsReturnOnly(output.Baseline);
                output.BestPowered = AsReturnOnly(output.BestPowered);
            }
            output.Footprints = footprints;
            output.BoostbackTrials = poweredTrials;
            output.BoostbackBurnFailures = burnFailures;
            output.BoostbackFuelFailures = fuelFailures;
            output.BoostbackLandingFailures = landingFailures;
            output.RunMilliseconds = elapsed.Elapsed.TotalMilliseconds;
            return output;
        }

        private static BoostbackApproach AsReturnOnly(BoostbackApproach approach)
        {
            if (approach?.Touchdown.HasPoint != true) return null;
            var result = approach.Copy();
            result.Safe = false;
            result.ReturnOnly = true;
            result.Touchdown.Status = "return_only";
            return result;
        }

        private void PlanGlide(BoostbackPreviewResult output, BoostbackApproach boost,
            BoostbackApproach retained, bool checkedRetained)
        {
            // All glide trials use the same finite burn. The default setting
            // alone determines the boostback direction and cutoff.
            boost = boost ?? output.Baseline ?? new BoostbackApproach();
            var bank = boost.Engines == 0 ? null :
                new EngineGroups(input.Engines, input.BackupTorque).MakeGroup(boost.Engines);
            BoostbackApproach bestGlide = boost.Safe ? boost : null;
            if (input.PreferredGlideAoa >= input.MinimumAoa && input.PreferredGlideAoa <= input.MaximumAoa &&
                FootprintAtmosphere.Finite(input.PreferredGlideBank))
            {
                if (!checkedRetained) retained = ProbeGlide(boost, bank, input.PreferredGlideAoa, input.PreferredGlideBank);
                if (Better(retained, bestGlide)) bestGlide = retained;
            }

            // A usable retained attitude only needs local refinement. Search
            // the full configured range on the first pass or after a miss.
            if (!Reached(retained))
            {
                output.MinimumAoa = ProbeGlide(boost, bank, input.MinimumAoa, 0);
                output.MaximumAoa = input.MaximumAoa == input.MinimumAoa ? output.MinimumAoa :
                    ProbeGlide(boost, bank, input.MaximumAoa, 0);
                double[] pitches = { input.MinimumAoa, (input.MinimumAoa + input.DefaultAoa) * 0.5, input.DefaultAoa,
                    (input.DefaultAoa + input.MaximumAoa) * 0.5, input.MaximumAoa };
                double[] banks = { 0, 30, -30, 45, -45, 90, -90, 135, -135, 180 };
                foreach (double pitch in pitches.Distinct())
                {
                    foreach (double roll in banks)
                    {
                        BoostbackApproach trial = roll == 0 && pitch == input.MinimumAoa ? output.MinimumAoa :
                            roll == 0 && pitch == input.MaximumAoa ? output.MaximumAoa : ProbeGlide(boost, bank, pitch, roll);
                        if (Better(trial, bestGlide)) bestGlide = trial;
                    }
                }
            }
            if (bestGlide == null) return;

            // Refine the best neighborhood before treating the bounded search
            // as a miss. Moving the bank and the pitch remains independent.
            double pitchStep = (input.MaximumAoa - input.MinimumAoa) / 8;
            foreach (double rollStep in new[] { 22.5, 11.25 })
            {
                double centerPitch = bestGlide.PitchAoa, centerRoll = bestGlide.GlideBank;
                for (int i = -1; i <= 1; i++)
                {
                    for (int j = -1; j <= 1; j++)
                    {
                        double pitch = Math.Max(input.MinimumAoa, Math.Min(input.MaximumAoa, centerPitch + i * pitchStep));
                        double roll = AttitudeGuidance.WrapDegrees(centerRoll + j * rollStep);
                        if (pitch == centerPitch && roll == centerRoll) continue;
                        BoostbackApproach trial = ProbeGlide(boost, bank, pitch, roll);
                        if (Better(trial, bestGlide)) bestGlide = trial;
                    }
                }
                pitchStep *= 0.5;
            }

            output.GlideApproach = bestGlide;
            if (Reached(bestGlide)) SelectGlide(output, bestGlide);
        }

        private static void SelectGlide(BoostbackPreviewResult output, BoostbackApproach approach)
        {
            output.GlideApproach = approach;
            output.Status = approach.Engines == 0 ? "glide_only" : "planned_with_glide";
        }

        private BoostbackApproach ProbeGlide(BoostbackApproach boost, EngineGroup bank, double pitch, double roll)
        {
            if (pitch == input.DefaultAoa && roll == 0) return boost;
            if (pitch == 0 && roll != 0) return null;
            FootprintAtmosphere atmosphere = PitchAtmosphere(pitch);
            return atmosphere == null ? null :
                Trace(bank, boost.EastDeltaV, boost.NorthDeltaV, pitch, atmosphere, roll, boost.BurnDirections);
        }

        private FootprintAtmosphere PitchAtmosphere(double aoa)
        {
            if (aoa == input.DefaultAoa || aoa == 0) return input.DefaultAtmosphere;
            bool lower = aoa < input.DefaultAoa;
            double edge = lower ? input.MinimumAoa : input.MaximumAoa;
            FootprintAtmosphere sample = lower ? input.MinimumAtmosphere : input.MaximumAtmosphere;
            if (sample == null || !sample.HasPitchSample || sample.Depth != input.DefaultAtmosphere.Depth) return null;
            return aoa == edge ? sample : input.DefaultAtmosphere.BlendPitch(sample, (aoa - input.DefaultAoa) / (edge - input.DefaultAoa));
        }

        private bool Reached(BoostbackApproach a) => a != null && a.Safe && a.MissDistance <= input.PositionTolerance;
        private static bool Better(BoostbackApproach a, BoostbackApproach b) =>
            a != null && a.Safe && (b == null || a.MissDistance < b.MissDistance);

        private EngineGroup[] FindBanks()
        {
            var candidates = new List<EngineGroup>();
            long available = input.Flight.Restartable & EngineModuleCatalog.All(input.Engines.Length);
            long stopAllowed = 0;
            for (int i = 0; i < input.Engines.Length; i++)
                if (input.Engines[i].StopAllowed && input.Engines[i].Ignitions != 0) stopAllowed |= 1L << i;
            new EngineGroups(input.Engines, input.BackupTorque).Populate(candidates, available & stopAllowed, 0, Array.Empty<long>());
            double cap = input.Flight.Mass * input.Settings.ThrustLimitFor(Phase.Boostback) * Settings.G0;
            EngineGroup[] usable = candidates.Where(g => Controller.MinimumRunningThrust(g) <= cap &&
                g.Max > 0 && FootprintAtmosphere.Finite(g.Lead)).ToArray();
            var banks = new List<EngineGroup>();
            var inside = Comparer<EngineGroup>.Create(EngineGroup.InsideFirst);
            // Resolve the lowest stable controllable single before trying
            // any of the powered trajectories. A balanced inner bank is the
            // fallback on installations with no steerable single.
            var singles = usable.Where(g => g.Count == 1).OrderBy(Controller.MinimumRunningThrust)
                .ThenBy(g => g, inside).ThenBy(g => g.Balance).ThenBy(g => g.Mask).ToArray();
            var precisionOrder = usable.OrderBy(g => g, inside).ThenBy(g => g.Count)
                .ThenBy(g => g.Balance).ThenBy(g => g.Mask).ToArray();
            foreach (EngineGroup bank in usable.OrderBy(g => ExpiringStarts(g.Mask))
                .ThenByDescending(g => Math.Min(cap, g.Max)).ThenBy(g => g.Count).ThenBy(g => g.Balance).ThenBy(g => g.Mask))
            {
                EngineGroup inner = singles.FirstOrDefault(g => (g.Mask & bank.Mask) == g.Mask) ??
                    precisionOrder.FirstOrDefault(g => (g.Mask & bank.Mask) == g.Mask &&
                        Math.Round(g.RadiusSquared, 4) < Math.Round(bank.RadiusSquared, 4));
                if (inner == null) continue;
                precisionBanks.Add(bank.Mask, inner);
                banks.Add(bank);
                if (banks.Count == MaximumGroups) break;
            }
            return banks.ToArray();
        }

        private int ExpiringStarts(long mask)
        {
            int count = 0;
            for (int i = 0; i < input.Engines.Length; i++)
                if ((mask & (1L << i)) != 0 && input.Engines[i].Ignitions == 1) count++;
            return count;
        }

        private BoostbackApproach Refine(EngineGroup bank, BoostbackApproach baseline,
            RecoveryBurnDirections directions = default, bool screen = false, int limit = MaximumFootprints,
            Stopwatch budget = null, BoostbackApproach seed = null, bool returnOnly = false)
        {
            double acceptDistance = returnOnly ? TargetedLandingGuidance.BoostbackCutoffWindow : input.PositionTolerance;
            double fallback = (input.Flight.Vz + Math.Sqrt(input.Flight.Vz * input.Flight.Vz +
                2 * input.Settings.Gravity * (input.Flight.SeaLevelAltitude - input.WaypointHeight))) / input.Settings.Gravity;
            double time = Math.Max(5, baseline?.TotalSeconds ?? fallback);
            FootprintGeometry.Deviation(input.Latitude, input.Longitude, input.WaypointLatitude, input.WaypointLongitude,
                input.Radius, out _, out _, out double waypointEast, out double waypointNorth);
            if (!FootprintAtmosphere.Finite(waypointEast) || !FootprintAtmosphere.Finite(waypointNorth)) return null;
            double seedEast = seed?.EastDeltaV ??
                (baseline == null ? waypointEast / time - input.EastSpeed : -baseline.MissEast / time);
            double seedNorth = seed?.NorthDeltaV ??
                (baseline == null ? waypointNorth / time - input.NorthSpeed : -baseline.MissNorth / time);
            ClampDeltaV(ref seedEast, ref seedNorth, directions.BoostPitch);
            BoostbackApproach Try(double east, double north) =>
                Test(bank, east, north, directions, screen, limit, budget, returnOnly);
            BoostbackApproach candidate = Try(seedEast, seedNorth);
            if (candidate == null && budget == null) candidate = Try(seedEast * 0.5, seedNorth * 0.5);
            if (candidate == null && budget == null) candidate = baseline;
            BoostbackApproach best = candidate != null && candidate.Safe ? candidate : null;
            for (int pass = 0; pass < 5 && candidate != null && (best == null || best.MissDistance > acceptDistance) &&
                HasTraceBudget(limit, budget); pass++)
            {
                double epsilon = Math.Min(5, Math.Max(1, input.MaxDeltaV / 10));
                double east = candidate.EastDeltaV, north = candidate.NorthDeltaV;
                var eastProbe = Try(east + epsilon, north);
                var northProbe = Try(east, north + epsilon);
                if (!TryCorrection(candidate, eastProbe, northProbe, out double correctionEast, out double correctionNorth))
                {
                    correctionEast = -candidate.MissEast / time;
                    correctionNorth = -candidate.MissNorth / time;
                }

                BoostbackApproach improved = null;
                for (double fraction = 1; fraction >= 0.25; fraction *= 0.5)
                {
                    double nextEast = east + fraction * correctionEast, nextNorth = north + fraction * correctionNorth;
                    ClampDeltaV(ref nextEast, ref nextNorth, directions.BoostPitch);
                    if (Controller.Norm(nextEast - east, nextNorth - north) < 0.02) break;
                    var trial = Try(nextEast, nextNorth);
                    if (Better(trial, best)) best = trial;
                    if (trial == null || trial.MissDistance >= candidate.MissDistance) continue;
                    improved = trial;
                    break;
                }
                candidate = improved;
            }
            return best;
        }

        private bool HasTraceBudget(int limit, Stopwatch budget) =>
            footprints < limit && (budget == null || budget.ElapsedMilliseconds < JointBudgetMilliseconds);

        private BoostbackApproach Test(EngineGroup bank, double east, double north, RecoveryBurnDirections directions,
            bool screen, int limit, Stopwatch budget, bool returnOnly)
        {
            if (!HasTraceBudget(limit, budget) || Controller.Norm(east, north) > HorizontalBudget(directions.BoostPitch) + 1e-8)
                return null;
            return Trace(bank, east, north, input.DefaultAoa, input.DefaultAtmosphere,
                directions: directions, screen: screen, returnOnly: returnOnly);
        }

        private static bool TryCorrection(BoostbackApproach origin, BoostbackApproach east, BoostbackApproach north,
                                          out double de, out double dn)
        {
            de = dn = 0;
            if (east == null || north == null) return false;
            double dx = east.EastDeltaV - origin.EastDeltaV, dy = north.NorthDeltaV - origin.NorthDeltaV;
            if (Math.Abs(dx * dy) < 1e-6) return false;
            double ee = (east.MissEast - origin.MissEast) / dx;
            double ne = (east.MissNorth - origin.MissNorth) / dx;
            double en = (north.MissEast - origin.MissEast) / dy;
            double nn = (north.MissNorth - origin.MissNorth) / dy;
            double determinant = ee * nn - en * ne;
            if (Math.Abs(determinant) < 1e-5) return false;
            de = (en * origin.MissNorth - nn * origin.MissEast) / determinant;
            dn = (ne * origin.MissEast - ee * origin.MissNorth) / determinant;
            return FootprintAtmosphere.Finite(de) && FootprintAtmosphere.Finite(dn);
        }

        private double HorizontalBudget(double boostPitch) => input.MaxDeltaV * Math.Cos(boostPitch * Degree);

        private void ClampDeltaV(ref double east, ref double north, double boostPitch)
        {
            double requested = Controller.Norm(east, north);
            double limit = HorizontalBudget(boostPitch);
            if (requested <= limit) return;
            east *= limit / requested;
            north *= limit / requested;
        }

        private BoostbackApproach Trace(EngineGroup bank, double east, double north, double aoa,
            FootprintAtmosphere atmosphere, double roll = 0, RecoveryBurnDirections directions = default, bool screen = false,
            bool returnOnly = false)
        {
            stop.ThrowIfCancellationRequested();
            double vertical = Controller.Norm(east, north) * Math.Tan(directions.BoostPitch * Degree);
            double deltaV = Controller.Norm(Controller.Norm(east, north), vertical);
            var flight = input.Flight;
            flight.Propellant = flight.Propellant.ForForecast();
            var motion = initial;
            var trial = new BoostbackApproach
            {
                EastDeltaV = east, NorthDeltaV = north, UpDeltaV = vertical, BurnDirections = directions,
                PitchAoa = aoa, GlideBank = roll, Heading = double.NaN
            };
            var engines = input.Engines;
            if (deltaV > 0)
            {
                if (bank == null) return null;
                poweredTrials++;
                trial.Engines = bank.Mask;
                trial.Heading = (Math.Atan2(east, north) / Degree + 360) % 360;
                EngineGroup precision = precisionBanks[bank.Mask];
                trial.PrecisionEngines = precision.Mask;
                trial.PrecisionMinimumThrust = Controller.MinimumRunningThrust(precision);
                AttitudeVector direction = (initialEast * east + initialNorth * north + initial.Position.Unit * vertical) * (1 / deltaV);
                if (!PropagateBoostback(ref motion, ref flight, trial, bank, precision, direction, atmosphere,
                    out long used))
                {
                    burnFailures++;
                    return null;
                }
                engines = AfterIgnition(engines, used, ref flight.Restartable);
                if (returnOnly && flight.Restartable == 0) return null;
            }

            var forecast = FinishTrace(motion, flight, engines, trial, atmosphere, screen, returnOnly);
            if (deltaV > 0 && forecast?.Safe != true) landingFailures++;
            return forecast;
        }

        private BoostbackApproach FinishTrace(TrajectoryState motion, Flight flight, Engine[] engines,
            BoostbackApproach trial, FootprintAtmosphere atmosphere, bool screen = false, bool returnOnly = false)
        {
            double aoa = trial.PitchAoa, roll = trial.GlideBank;
            if (!CoastToHandoff(ref motion, atmosphere, flight.Mass, aoa, roll, out double coast)) return null;
            double altitude = motion.Altitude(input.Radius);
            AttitudeVector up = motion.Position.Unit;
            double lat = Math.Asin(Math.Max(-1, Math.Min(1, up.Z))) / Degree;
            double lon = Math.Atan2(up.Y, up.X) / Degree;
            var surfaceEast = new AttitudeVector(-Math.Sin(lon * Degree), Math.Cos(lon * Degree), 0);
            AttitudeVector surfaceNorth = up.Cross(surfaceEast);
            double ve = motion.Velocity.Dot(surfaceEast), vn = motion.Velocity.Dot(surfaceNorth);
            double horizontal = Controller.Norm(ve, vn);
            flight.Height = altitude - input.WaypointHeight;
            flight.SeaLevelAltitude = altitude;
            flight.Vz = motion.Velocity.Dot(up);
            flight.Vx = horizontal;
            flight.SurfaceHorizontalSpeed = horizontal;
            flight.SurfaceSpeed = motion.Velocity.Length;
            flight.UniversalTime = (flight.UniversalTime ?? 0) + trial.CutoffSeconds + coast;
            flight.AlignmentSeconds = flight.LateralThrustAcceleration = flight.VerticalThrustAcceleration = 0;

            Settings settings = input.Settings.Copy();
            settings.Gravity = gravityParameter / motion.Position.Dot(motion.Position);
            settings.CoastPitchAoaDegrees = aoa;
            var controller = new Controller(engines, settings, input.BackupTorque);
            if (motion.ObservedAboveEntry && flight.SeaLevelAltitude <= settings.EntryBurnAltitude)
                controller.ObserveBoostbackAltitude(settings.EntryBurnAltitude + 1);
            controller.EntryTiltDegrees = trial.BurnDirections.EntryTilt + input.EntryTiltDegrees;
            FootprintAtmosphere entryAir = EntryAtmosphere(controller.EntryTiltDegrees);
            footprints++;
            if (screen) screenedFootprints++;
            var glide = new FootprintGlideSteering(roll, input.GlideRollRate, input.CurrentGlideBank, aoa,
                input.CurrentGlideDirection, settings.AttitudeResponseSeconds);
            bool saveGlideStart = !screen && !returnOnly && input.Settings.OptimizeRecoveryTrajectory && trial.Engines != 0;
            var landing = controller.CaptureFootprint(flight, atmosphere, input.Radius, glide, entryAir,
                screen, saveGlideStart, returnOnly)(stop);
            if (!landing.HasPoint) return null;
            double alongEast = horizontal < 0.02 ? 0 : ve / horizontal;
            double alongNorth = horizontal < 0.02 ? 1 : vn / horizontal;
            trial.ForecastLatitude = lat;
            trial.ForecastLongitude = lon;
            trial.ForecastEast = alongEast;
            trial.ForecastNorth = alongNorth;
            FootprintGeometry.Destination(lat, lon,
                alongNorth * landing.Along + alongEast * landing.Cross,
                alongEast * landing.Along - alongNorth * landing.Cross, input.Radius, out trial.Lat, out trial.Lon);
            FootprintGeometry.Deviation(input.WaypointLatitude, input.WaypointLongitude, trial.Lat, trial.Lon, input.Radius,
                out trial.MissDistance, out _, out trial.MissEast, out trial.MissNorth);
            if (!FootprintAtmosphere.Finite(trial.MissEast) || !FootprintAtmosphere.Finite(trial.MissNorth)) return null;
            trial.Touchdown = landing;
            trial.TotalSeconds = trial.CutoffSeconds + coast + landing.Seconds;
            trial.RecoveryFuel = flight.Mass - landing.TouchdownMass;
            trial.FuelAtTouchdown = landing.TouchdownMass - input.DryMass;
            trial.Safe = (screen || returnOnly ? landing.Status == "screened" : landing.Status == "estimated") &&
                trial.FuelAtTouchdown >= input.FuelReserve;
            return trial;
        }

        private bool PropagateBoostback(ref TrajectoryState motion, ref Flight flight, BoostbackApproach trial,
            EngineGroup full, EngineGroup precision, AttitudeVector direction, FootprintAtmosphere atmosphere, out long used)
        {
            used = 0;
            var stage = BoostbackStage.Aligning;
            var axis = (initialEast * input.BoostbackBodyAxis.X + initialNorth * input.BoostbackBodyAxis.Y +
                initial.Position.Unit * input.BoostbackBodyAxis.Z).Unit;
            bool hasAttitude = axis.Length > 0.5 && input.BoostbackTurnRate > 0;
            if (!hasAttitude) axis = direction;
            double aimReadyAt = hasAttitude ? 0 : flight.AlignmentSeconds;
            double localGround = flight.SeaLevelAltitude - flight.Height;
            FootprintAtmosphere burnAir = input.BoostbackAtmosphere ?? atmosphere;
            double clock = 0;
            double remaining = trial.DeltaV;
            double cap = input.Settings.ThrustLimitFor(Phase.Boostback) * Settings.G0;
            double timeToGround = (flight.Vz + Math.Sqrt(flight.Vz * flight.Vz +
                2 * input.Settings.Gravity * (flight.SeaLevelAltitude - input.WaypointHeight))) / input.Settings.Gravity;
            EngineGroup lit = null;
            long requested = 0;
            double ignitionAt = double.PositiveInfinity, readyAt = 0;
            bool ignitionCommitted = false;
            while (remaining > 1e-6)
            {
                stop.ThrowIfCancellationRequested();
                if (clock > TrajectoryHorizon) return false;
                if (!ignitionCommitted && !TargetedLandingGuidance.MayStartBoostback(
                    motion.Altitude(input.Radius) - localGround, motion.VerticalSpeed, input.Settings)) return false;
                double dt = TrajectoryStep / 2;
                if (clock < aimReadyAt)
                    dt = Math.Min(dt, aimReadyAt - clock);
                else
                {
                    double angle = Math.Acos(Math.Max(-1, Math.Min(1, axis.Dot(direction)))) / Degree;
                    double fineAcceleration = Math.Min(cap, precision.Max / flight.Mass);
                    stage = TargetedLandingGuidance.BoostbackStageFor(stage, angle, lit != null,
                        remaining, remaining * Math.Max(1, timeToGround - clock), fineAcceleration, input.Settings);
                    long mask = TargetedLandingGuidance.BoostbackMask(stage, precision.Mask, full.Mask);
                    if (mask != requested)
                    {
                        long additions = mask & ~used;
                        // Ullage begins during the turn. Only the remaining settle time delays ignition.
                        ignitionAt = clock + ((additions & flight.Unsettled) != 0 ?
                            Math.Max(0, flight.UllageSeconds - clock) : 0);
                        readyAt = ignitionAt + SpoolSeconds(additions);
                        used |= mask;
                        requested = mask;
                    }
                    if (clock + 1e-8 >= ignitionAt)
                        ignitionCommitted = true;
                    else if (requested != 0)
                        dt = Math.Min(dt, ignitionAt - clock);
                    if (clock + 1e-8 >= readyAt)
                        lit = requested == 0 ? null : requested == precision.Mask ? precision : full;
                    else
                        dt = Math.Min(dt, readyAt - clock);
                }
                AttitudeVector thrust = default;
                if (lit != null)
                {
                    double minimum = lit == precision ? trial.PrecisionMinimumThrust : Controller.MinimumRunningThrust(lit);
                    if (minimum > cap * flight.Mass) return false;
                    if (trial.BurnSeconds == 0) trial.LeadSeconds = clock;
                    double acceleration = Math.Min(cap, lit.Max / flight.Mass);
                    double projected = acceleration * axis.Dot(direction);
                    if (projected > 0) dt = Math.Min(dt, remaining / projected);
                    double exhaust = (lit.SpecificImpulse > 0 ? lit.SpecificImpulse : input.Settings.SpecificImpulse) * Settings.G0;
                    double burned = flight.Mass * (1 - Math.Exp(-acceleration * dt / exhaust));
                    if (flight.Mass - burned < input.DryMass + input.FuelReserve ||
                        !flight.Propellant.ConsumeForecast(lit.Mask, burned))
                    {
                        fuelFailures++;
                        return false;
                    }
                    thrust = axis * acceleration;
                    flight.Mass -= burned;
                    trial.BoostFuel += burned;
                    trial.BurnSeconds += dt;
                    trial.BoostThrustDeltaV += acceleration * dt;
                    remaining -= projected * dt;
                }
                if (!Advance(ref motion, dt, thrust, burnAir, flight.Mass, input.BoostbackAtmosphere != null) ||
                    motion.Altitude(input.Radius) <= input.WaypointHeight) return false;
                clock += dt;
                if (hasAttitude) axis = TargetedLandingGuidance.Slew(axis, direction, initial.Position.Unit,
                    input.BoostbackTurnRate * dt);
            }
            trial.CutoffSeconds = clock;
            return true;
        }

        private double SpoolSeconds(long additions)
        {
            double lead = 0;
            for (int i = 0; i < input.Engines.Length; i++)
                if ((additions & (1L << i)) != 0)
                    lead = Math.Max(lead, input.Engines[i].SpoolSeconds);
            return lead;
        }

        private bool CoastToHandoff(ref TrajectoryState state, FootprintAtmosphere atmosphere, double mass,
            double pitch, double roll, out double time)
        {
            // Keep the powered footprint's horizon available for descent after a long ascent.
            double handoff = Math.Max(atmosphere.Depth, Math.Max(input.Settings.EntryBurnAltitude + 1,
                input.WaypointHeight + input.Settings.MaxIgnitionAltitude + 1));
            time = 0;
            while (state.Altitude(input.Radius) > handoff || state.VerticalSpeed > 0)
            {
                stop.ThrowIfCancellationRequested();
                if (time >= TrajectoryHorizon) return false;
                double before = state.Altitude(input.Radius);
                bool pitched = pitch != 0 && before < input.Settings.EntryBurnAltitude;
                TrajectoryState next = state;
                if (!Advance(ref next, TrajectoryStep, default, atmosphere, mass, pitched, roll)) return false;
                double after = next.Altitude(input.Radius);
                if (before > handoff && after <= handoff)
                {
                    double dt = TrajectoryStep * (before - handoff) / (before - after);
                    time += dt;
                    return Advance(ref state, dt, default, atmosphere, mass, pitched, roll);
                }
                if (after <= input.WaypointHeight) return false;
                state = next;
                time += TrajectoryStep;
            }
            return true;
        }

        private bool Advance(ref TrajectoryState state, double dt, AttitudeVector thrust,
            FootprintAtmosphere atmosphere, double mass, bool pitched, double roll = 0)
        {
            if (!TryAero(state.Position, state.Velocity, atmosphere, mass, pitched, roll, out AttitudeVector aero))
                return false;
            AttitudeVector acceleration = Acceleration(state.Position, state.Velocity) + thrust + aero;
            AttitudeVector midPosition = state.Position + state.Velocity * (dt / 2);
            AttitudeVector midVelocity = state.Velocity + acceleration * (dt / 2);
            if (!TryAero(midPosition, midVelocity, atmosphere, mass, pitched, roll, out aero))
                return false;
            state.Position += midVelocity * dt;
            state.Velocity += (Acceleration(midPosition, midVelocity) + thrust + aero) * dt;
            state.ObservedAboveEntry |= state.Altitude(input.Radius) > input.Settings.EntryBurnAltitude;
            return true;
        }

        private bool TryAero(AttitudeVector position, AttitudeVector velocity, FootprintAtmosphere atmosphere,
            double mass, bool pitched, double roll, out AttitudeVector aero)
        {
            aero = default;
            double altitude = position.Length - input.Radius;
            if (altitude >= atmosphere.Depth) return true;
            AttitudeVector up = position.Unit;
            double vertical = velocity.Dot(up);
            AttitudeVector horizontal = velocity - up * vertical;
            if (!atmosphere.Acceleration(altitude, horizontal.Length, vertical, mass, pitched,
                out double along, out double radial, out double cross, roll)) return false;
            AttitudeVector heading = horizontal.Unit;
            aero = heading * along + up * radial + up.Cross(heading) * cross;
            return true;
        }

        private AttitudeVector Acceleration(AttitudeVector r, AttitudeVector v)
        {
            double distance = r.Length;
            return r * (-gravityParameter / (distance * distance * distance)) -
                rotation.Cross(v) * 2 - rotation.Cross(rotation.Cross(r));
        }

        private static Engine[] AfterIgnition(Engine[] engines, long bank, ref long restartable)
        {
            var next = (Engine[])engines.Clone();
            for (int i = 0; i < engines.Length; i++)
            {
                long bit = 1L << i;
                Engine original = engines[i];
                if ((bank & bit) == 0 || original.Ignitions <= 0) continue;
                int remaining = original.Ignitions - 1;
                if (remaining == 0) restartable &= ~bit;
                next[i] = original.WithIgnitions(remaining);
            }
            return next;
        }

        private struct TrajectoryState
        {
            internal AttitudeVector Position, Velocity;
            internal bool ObservedAboveEntry;
            internal double VerticalSpeed => Velocity.Dot(Position.Unit);
            internal double Altitude(double radius) => Position.Length - radius;
        }
    }
}
