using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace LandingPoc
{
    internal sealed partial class BoostbackPreview
    {
        private const int JointBudgetMilliseconds = 300;
        internal const double EntryTrimAngle = 8;
        private const int JointFinalists = 3;
        private const int ScreenTracesPerDirection = 7;
        private const int VerifyTracesPerCandidate = 10;
        private int screenedFootprints;

        private void SearchJoint(BoostbackPreviewResult output, EngineGroup[] banks, BoostbackApproach baseline,
            Stopwatch elapsed)
        {
            // The nominal result stays available when the optional search has
            // no validated improvement or runs out of its snapshot budget.
            var candidates = new List<BoostbackApproach>();
            BoostbackApproach incumbent = Reached(output.DefaultAoa) ? output.DefaultAoa : null;
            EngineGroup bank = banks.FirstOrDefault(g => g.Mask == incumbent?.Engines) ?? banks[0];
            var coarse = Stopwatch.StartNew();
            foreach (RecoveryBurnDirections directions in CandidateDirections())
            {
                stop.ThrowIfCancellationRequested();
                if (elapsed.ElapsedMilliseconds >= JointBudgetMilliseconds || coarse.ElapsedMilliseconds >= JointBudgetMilliseconds / 2)
                    break;
                var trial = Refine(bank, baseline, directions, screen: true, limit: footprints + ScreenTracesPerDirection,
                    budget: elapsed, seed: incumbent);
                if (trial != null && trial.Engines != 0 && trial.Touchdown.EntryCompleted)
                    candidates.Add(trial);
            }
            output.ScreenedFootprints = screenedFootprints;

            foreach (var candidate in candidates.OrderBy(t => Math.Max(0, t.MissDistance - input.PositionTolerance) * 0.015 +
                t.PoweredDeltaV).Take(JointFinalists))
            {
                stop.ThrowIfCancellationRequested();
                int limit = footprints + VerifyTracesPerCandidate;
                if (!HasTraceBudget(limit, elapsed)) break;
                output.VerifiedCandidates++;
                var trial = Refine(bank, baseline, candidate.BurnDirections, limit: limit - 3,
                    budget: elapsed, seed: candidate);
                // A safe nominal glide can miss the target. Search a few
                // useful poses near that trajectory before discarding it.
                if (!JointReached(trial))
                    trial = VerifyJointGlide(bank, trial?.Touchdown.EntryCompleted == true ? trial : candidate, limit, elapsed);
                if (!JointReached(trial)) continue;
                if (incumbent != null && trial.PoweredDeltaV >= incumbent.PoweredDeltaV) continue;
                trial.JointPlan = true;
                incumbent = trial;
                output.DefaultAoa = trial;
                output.GlideApproach = trial;
                output.Status = "planned";
                if (double.IsNaN(output.FirstFlyableMilliseconds))
                    output.FirstFlyableMilliseconds = elapsed.Elapsed.TotalMilliseconds;
            }
        }

        private IEnumerable<RecoveryBurnDirections> CandidateDirections()
        {
            // Deliberately bounded. Pitch signs are relative to the local
            // horizontal for Boostback and surface retrograde for Entry.
            foreach (double pitch in new[] { -10.0, -5, -20, 5, -15, 12 })
                yield return new RecoveryBurnDirections(pitch, 0);
            foreach (double entry in new[] { -EntryTrimAngle, EntryTrimAngle })
            {
                if (EntryAtmosphere(entry) == null) continue;
                yield return new RecoveryBurnDirections(-10, entry);
                yield return new RecoveryBurnDirections(5, entry);
            }
        }

        private FootprintAtmosphere EntryAtmosphere(double tilt) => tilt < 0 ? input.EntryNoseDownAtmosphere :
            tilt > 0 ? input.EntryNoseUpAtmosphere : null;

        private bool JointReached(BoostbackApproach trial) =>
            Reached(trial) && trial.Engines != 0 && trial.Touchdown.Status == "estimated" && trial.Touchdown.EntryCompleted;

        private BoostbackApproach ResumeGlide(BoostbackApproach origin, FootprintAtmosphere air, double aoa, double roll)
        {
            var start = origin.Touchdown.GlideStart;
            Settings settings = input.Settings.Copy();
            settings.Gravity = start.Gravity;
            settings.CoastPitchAoaDegrees = aoa;
            var controller = new Controller(start.Engines, settings, input.BackupTorque);
            var steering = new FootprintGlideSteering(roll, input.GlideRollRate, input.CurrentGlideBank);
            footprints++;
            var landing = controller.CaptureFootprint(start.Flight, air, input.Radius, steering)(stop);
            if (!landing.HasPoint) return null;
            double along = landing.Along, cross = landing.Cross;
            landing.Along = start.Along + along * start.HeadingX - cross * start.HeadingY;
            landing.Cross = start.Cross + along * start.HeadingY + cross * start.HeadingX;
            landing.Seconds += start.Seconds;
            landing.EntryCompleted = true;
            landing.EntrySeconds = start.EntrySeconds;
            landing.EntryDeltaV = start.EntryDeltaV;
            var trial = origin.Copy();
            trial.PitchAoa = aoa;
            trial.GlideBank = roll;
            trial.Touchdown = landing;
            trial.TotalSeconds = origin.TotalSeconds - origin.Touchdown.Seconds + landing.Seconds;
            trial.RecoveryFuel = origin.RecoveryFuel + origin.Touchdown.TouchdownMass - landing.TouchdownMass;
            trial.FuelAtTouchdown = landing.TouchdownMass - input.DryMass;
            trial.Safe = landing.Status == "estimated" && trial.FuelAtTouchdown >= input.FuelReserve;
            FootprintDestination(origin.ForecastLatitude, origin.ForecastLongitude,
                origin.ForecastEast, origin.ForecastNorth, landing, out trial.Lat, out trial.Lon);
            FootprintGeometry.Deviation(input.WaypointLatitude, input.WaypointLongitude, trial.Lat, trial.Lon, input.Radius,
                out trial.MissDistance, out _, out trial.MissEast, out trial.MissNorth);
            return FootprintAtmosphere.Finite(trial.MissDistance) ? trial : null;
        }

        private BoostbackApproach VerifyJointGlide(EngineGroup bank, BoostbackApproach origin, int limit, Stopwatch elapsed)
        {
            // Probe a change of range and both sides; every accepted control
            // pair gets a full powered-landing trace with sampled aero.
            double pitch = input.DefaultAoa == 0 ?
                (Math.Abs(input.MinimumAoa) >= Math.Abs(input.MaximumAoa) ? input.MinimumAoa : input.MaximumAoa) :
                input.DefaultAoa;
            double edge = input.DefaultAoa == input.MaximumAoa ? input.MinimumAoa : input.MaximumAoa;
            foreach (var pose in new[] { (AoA: edge, Roll: 0.0), (AoA: pitch, Roll: 30.0), (AoA: pitch, Roll: -30.0) })
            {
                if (!HasTraceBudget(limit, elapsed)) break;
                if ((pose.AoA == 0 && pose.Roll != 0) || (pose.AoA == input.DefaultAoa && pose.Roll == 0)) continue;
                var air = PitchAtmosphere(pose.AoA);
                if (air == null) continue;
                var trial = origin.Touchdown.GlideStart == null
                    ? Trace(bank, origin.EastDeltaV, origin.NorthDeltaV, pose.AoA, air, pose.Roll, origin.BurnDirections)
                    : ResumeGlide(origin, air, pose.AoA, pose.Roll);
                if (JointReached(trial)) return trial;
            }
            return origin;
        }
    }
}
