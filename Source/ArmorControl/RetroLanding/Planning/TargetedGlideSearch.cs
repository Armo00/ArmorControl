using System;
using System.Collections.Generic;
using System.Linq;

namespace LandingPoc
{
    internal sealed partial class BoostbackPreview
    {
        private FootprintGlideSteering GlideSteering(double pitch, double bank) =>
            new FootprintGlideSteering(bank, input.GlideRollRate, input.CurrentGlideBank, pitch,
                input.CurrentGlideDirection, input.Settings.AttitudeResponseSeconds,
                input.CurrentGlideBankRate, input.PreferredGlideBank, input.TrackLiveGlide);

        private void PlanLiveGlide(BoostbackPreviewResult output)
        {
            double heldPitch = input.PreferredGlideAoa >= input.MinimumAoa && input.PreferredGlideAoa <= input.MaximumAoa
                ? input.PreferredGlideAoa : input.DefaultAoa;
            double heldBank = FootprintAtmosphere.Finite(input.PreferredGlideBank) ? input.PreferredGlideBank : 0;
            var tried = new HashSet<KeyValuePair<double, double>>();
            BoostbackApproach held = Probe(heldPitch, heldBank, keep: true);
            output.HeldGlideMiss = held?.Safe == true ? held.MissDistance : double.NaN;
            if (Reached(held))
            {
                SelectGlide(output, held);
                output.GlideDecision = "hold_within_tolerance";
                return;
            }

            BoostbackApproach best = held?.Safe == true ? held : null;
            double[] pitches =
            {
                input.DefaultAoa, (heldPitch + input.DefaultAoa) / 2, heldPitch,
                input.MinimumAoa, input.MaximumAoa, (input.MinimumAoa + input.DefaultAoa) / 2,
                (input.MaximumAoa + input.DefaultAoa) / 2, 0
            };
            double measuredBank = input.CurrentGlideDirection.Length > 0.5 ? input.CurrentGlideBank : heldBank;
            double[] banks = { heldBank, measuredBank, heldBank - 11.25, heldBank + 11.25, heldBank / 2,
                0, 30, -30, 60, -60, 90, -90, 135, -135, 180 };
            var orderedBanks = banks.Select(AttitudeGuidance.WrapDegrees).Distinct()
                .OrderBy(bank => Math.Abs(AttitudeGuidance.WrapDegrees(bank - measuredBank))).ToArray();
            var orderedPitches = pitches.Distinct().Where(p => p >= input.MinimumAoa && p <= input.MaximumAoa)
                .OrderBy(AoaDeviation).ToArray();
            foreach (double bank in orderedBanks)
            {
                if (footprints >= MaximumFootprints) break;
                foreach (double pitch in orderedPitches)
                {
                    if (footprints >= MaximumFootprints || Reached(best) && AoaDeviation(pitch) > AoaDeviation(best.PitchAoa)) break;
                    Consider(Probe(pitch, pitch == 0 ? heldBank : bank));
                }
            }

            if (best != null)
            {
                output.GlideApproach = best;
                if (Reached(best)) SelectGlide(output, best);
                output.GlideDecision = ReferenceEquals(best, held) ? "hold_no_reachable_improvement" :
                    Reached(best) ? "correct_into_tolerance" : "correct_toward_target";
            }
            else output.GlideDecision = "no_safe_glide";

            void Consider(BoostbackApproach candidate)
            {
                if (candidate?.Safe != true) return;
                if (held?.Safe == true && candidate.MissDistance >= held.MissDistance) return;
                if (best == null || Prefer(candidate, best)) best = candidate;
            }

            bool Prefer(BoostbackApproach candidate, BoostbackApproach current)
            {
                bool candidateFits = Reached(candidate), currentFits = Reached(current);
                if (candidateFits != currentFits) return candidateFits;
                if (!candidateFits) return CorrectionCost(candidate) < CorrectionCost(current);
                int pitch = AoaDeviation(candidate.PitchAoa).CompareTo(AoaDeviation(current.PitchAoa));
                if (pitch != 0) return pitch < 0;
                int time = GlideSteering(candidate.PitchAoa, candidate.GlideBank).AcquisitionSeconds
                    .CompareTo(GlideSteering(current.PitchAoa, current.GlideBank).AcquisitionSeconds);
                if (time != 0) return time < 0;
                int side = SideDemand(candidate).CompareTo(SideDemand(current));
                return side != 0 ? side < 0 : candidate.MissDistance < current.MissDistance;
            }

            double CorrectionCost(BoostbackApproach approach)
            {
                // Outside the tolerance, trade waypoint error against changing
                // the control command. The allowed miss sets the distance scale.
                double range = input.MaximumAoa - input.MinimumAoa;
                double pitch = range > 0 ? AoaDeviation(approach.PitchAoa) / range : 0;
                double roll = Math.Abs(AttitudeGuidance.WrapDegrees(approach.GlideBank - heldBank)) / 180;
                return approach.MissDistance + input.PositionTolerance * (pitch + roll);
            }

            BoostbackApproach Probe(double pitch, double bank, bool keep = false)
            {
                if (!tried.Add(new KeyValuePair<double, double>(pitch, AttitudeGuidance.WrapDegrees(bank)))) return null;
                var steering = GlideSteering(pitch, bank);
                double reserve = Math.Max(input.Settings.AttitudeResponseSeconds, Math.Abs(pitch) / input.GlideRollRate);
                double acquisition = steering.AcquisitionSeconds + reserve;
                double available = FootprintAtmosphere.Finite(input.GlideTimeAvailable)
                    ? input.GlideTimeAvailable : double.PositiveInfinity;
                if (!keep && (double.IsPositiveInfinity(acquisition) || acquisition > available)) return null;
                var atmosphere = PitchAtmosphere(pitch);
                var approach = atmosphere == null ? null : Trace(null, 0, 0, pitch, atmosphere, bank);
                if (keep || approach?.Safe != true) return approach;
                double coast = approach.TotalSeconds - approach.Touchdown.LandingSeconds - approach.Touchdown.EntrySeconds;
                double needed = Math.Max(steering.AcquisitionSeconds, approach.Touchdown.GlideRollSeconds) + reserve;
                return needed <= Math.Min(available, coast) ? approach : null;
            }
        }

        private double AoaDeviation(double pitch) => Math.Abs(pitch - input.DefaultAoa);

        // Distance from the unbanked pitch plane. Both pitch and bank can
        // reduce the requested crossrange correction.
        private static double SideDemand(BoostbackApproach approach) =>
            Math.Abs(Math.Sin(approach.PitchAoa * AttitudeGuidance.Degree) *
                     Math.Sin(approach.GlideBank * AttitudeGuidance.Degree));
    }
}
