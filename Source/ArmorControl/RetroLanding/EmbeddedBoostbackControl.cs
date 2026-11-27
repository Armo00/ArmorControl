using System;
using LandingPoc;

namespace KspRetroLanding
{
    public sealed partial class KspLanding
    {
        private const string BoostbackTraceColumns =
            ",boostback_stage,boostback_plan_dv_mps,boostback_actual_dv_mps,boostback_projected_dv_mps" +
            ",boostback_trim_min_kn,boostback_cutoff_reason,boostback_prediction_ut,boostback_prediction_age_s" +
            ",boostback_cut_now_east_m,boostback_cut_now_north_m,boostback_corrected_east_m,boostback_corrected_north_m" +
            ",boostback_hold_s,boostback_tail_dv_mps,boostback_reference_pos_error_m,boostback_reference_vel_error_mps" +
            ",boostback_aim_east,boostback_aim_north,boostback_aim_up,boostback_thrust_east_kn,boostback_thrust_north_kn,boostback_thrust_up_kn" +
            ",boostback_correction_ms,boostback_correction_traces,targeted_last_search_ms,targeted_last_search_traces" +
            ",boostback_first_activate_ut,boostback_activate_angle_deg,boostback_first_thrust_ut" +
            ",boostback_first_body_angle_deg,boostback_first_thrust_angle_deg" +
            ",boostback_cut_now_aoa_deg,boostback_cut_now_bank_deg" +
            ",boostback_search_ut,boostback_search_status,boostback_search_rejection,boostback_search_powered_miss_m" +
            ",boostback_search_main_mask,boostback_search_trim_mask,boostback_search_banks,boostback_search_trials" +
            ",boostback_search_burn_failures,boostback_search_fuel_failures,boostback_search_landing_failures" +
            ",boostback_prediction_source,boostback_coarse_miss_m,targeted_far_status,targeted_far_retro_ready,targeted_far_aoa_ready" +
            ",targeted_far_budget_denied,targeted_far_voxel_pending,targeted_far_invalid,boostback_landing_forecast" +
            ",targeted_glide_decision,targeted_glide_held_miss_m";

        private string BoostbackTrace(double now)
        {
            var active = targeted;
            if (active == null) return new string(',', 55);
            string planning = BoostbackPlanningTrace(active) + string.Format(Invariant, ",{0},{1:F2},{2},{3},{4},{5},{6},{7},{8},{9},{10}",
                active.Prediction, active.Fallback?.Miss, active.AeroSampleStatus, active.Samples[0].Valid ? 1 : 0,
                active.Samples[1].Valid ? 1 : 0, active.AeroBudgetDenied, active.AeroVoxelPending, active.AeroInvalid,
                active.Correction?.CutNow?.Touchdown.Status ?? active.Boost?.Touchdown.Status,
                active.GlideDecision, FootprintAtmosphere.Finite(active.HeldGlideMiss) ? active.HeldGlideMiss.ToString("F2", Invariant) : "");
            if (active.Boost == null)
                return new string(',', 24) + string.Format(Invariant, ",{0:F3},{1}", active.LastSearchMilliseconds,
                    active.LastSearchTraces) + new string(',', 7) + planning;
            var correction = active.Correction;
            var cut = correction?.CutNow;
            var repaired = correction?.AfterCorrection;
            return string.Format(Invariant,
                ",{0},{1:F2},{2:F2},{3:F2},{4:F2},{5},{6},{7},{8},{9},{10},{11},{12},{13},{14},{15},{16:F5},{17:F5},{18:F5},{19:F2},{20:F2},{21:F2},{22},{23},{24:F3},{25},{26},{27},{28},{29},{30},{31},{32}",
                active.BoostStage, active.Boost.BoostThrustDeltaV, active.DeliveredMagnitude, active.Delivered, active.TrimMinimum,
                active.CutoffReason, Number(correction == null ? double.NaN : active.CorrectionAt),
                Number(correction == null ? double.NaN : now - active.CorrectionAt),
                Number(cut?.MissEast ?? double.NaN), Number(cut?.MissNorth ?? double.NaN),
                Number(repaired?.MissEast ?? double.NaN), Number(repaired?.MissNorth ?? double.NaN),
                Number(repaired == null ? double.NaN : correction.HoldSeconds), Number(correction?.TailImpulse ?? double.NaN),
                Number(active.ReferencePositionError), Number(active.ReferenceVelocityError),
                active.TraceDirection.X, active.TraceDirection.Y, active.TraceDirection.Z,
                active.TraceThrust.X, active.TraceThrust.Y, active.TraceThrust.Z,
                Number(correction?.ElapsedMilliseconds ?? double.NaN), correction?.Traces,
                active.LastSearchMilliseconds, active.LastSearchTraces,
                Number(active.FirstActivationAt), Number(active.FirstActivationAngle), Number(active.FirstThrustAt),
                Number(active.FirstBodyAngle), Number(active.FirstThrustAngle),
                Number(cut?.PitchAoa ?? double.NaN), Number(cut?.GlideBank ?? double.NaN)) + planning;

            string Number(double value) => FootprintAtmosphere.Finite(value) ? value.ToString("F3", Invariant) : "";
        }

        private static string BoostbackPlanningTrace(TargetedRecovery active)
        {
            var plan = active.LastBoostbackPlan;
            var powered = plan?.DefaultAoa;
            if (powered == null || powered.Engines == 0) powered = plan?.BestPowered;
            return string.Format(Invariant, ",{0},{1},{2},{3:F2},{4},{5},{6},{7},{8},{9},{10}",
                FootprintAtmosphere.Finite(active.LastBoostbackPlanAt) ? active.LastBoostbackPlanAt.ToString("F3", Invariant) : "",
                plan?.Status, active.LastBoostbackRejection, powered?.MissDistance, powered?.Engines, powered?.PrecisionEngines,
                plan?.BoostbackBanks, plan?.BoostbackTrials, plan?.BoostbackBurnFailures, plan?.BoostbackFuelFailures,
                plan?.BoostbackLandingFailures);
        }

        private void RecordBoostbackActivation(double now, double alignment)
        {
            if (targeted?.Leg != TargetedLeg.Boostback || !double.IsNaN(targeted.FirstActivationAt)) return;
            targeted.FirstActivationAt = now;
            targeted.FirstActivationAngle = alignment;
        }

        private static void BeginBoostbackCutoff(TargetedRecovery active, string reason)
        {
            CancelTargetedSearch(active);
            active.CutoffReason = reason;
            active.Leg = TargetedLeg.Cutoff;
        }

        private static double BoostbackCorrectionLifetime(BoostbackStage stage) =>
            TargetedLandingGuidance.CorrectionInterval(stage) + 0.25;

        private static BoostbackCorrectionResult FreshBoostbackCorrection(TargetedRecovery active, double now) =>
            now >= active.CorrectionAt && now - active.CorrectionAt <= BoostbackCorrectionLifetime(active.BoostStage)
                ? active.Correction : null;

        private void ObserveBoostbackCommand(TargetedRecovery active, long requested)
        {
            long removed = active.BoostCommandMask & ~requested;
            for (int i = 0; removed != 0 && i < engines.Count; i++)
            {
                long bit = 1L << i;
                if ((removed & bit) == 0) continue;
                engines[i].BeginBoostbackShutdown();
                removed &= ~bit;
            }
            active.BoostCommandMask = requested;
        }

        private void ObserveBoostbackImpulse(TargetedRecovery active, Flight flight, Vector3d up,
            Vector3d thrust, double elapsed)
        {
            if (active.Boost == null || active.Body != vessel.mainBody ||
                elapsed <= 0 || elapsed > Math.Max(0.1, 2 * cfg.SampleSeconds)) return;
            Vector3d referenceAxis = BoostbackDirection(active, up);
            if (double.IsNaN(active.FirstThrustAt) && thrust.sqrMagnitude > 0.01)
            {
                active.FirstThrustAt = flight.UniversalTime ?? double.NaN;
                active.FirstBodyAngle = Vector3d.Angle(frame.up, referenceAxis);
                active.FirstThrustAngle = Vector3d.Angle(thrust, referenceAxis);
            }
            active.Delivered = TargetedLandingGuidance.AddImpulse(active.Delivered,
                Vector3d.Dot(thrust, referenceAxis), flight.Mass, elapsed);
            active.DeliveredMagnitude = TargetedLandingGuidance.AddImpulse(active.DeliveredMagnitude,
                thrust.magnitude, flight.Mass, elapsed);

            for (int i = 0; i < engines.Count; i++) engines[i].ObserveBoostbackShutdown(elapsed);
        }

        private void UpdateBoostbackReference(TargetedRecovery active, BoostbackCorrectionResult correction,
            Flight flight, Vector3d up, Vector3d velocity, double now)
        {
            active.ReferencePositionError = active.ReferenceVelocityError = double.NaN;
            if (correction?.AfterCorrection == null) return;
            TargetedBasis(up, out Vector3d east, out Vector3d north);
            BoostbackPreview.SurfaceState(vessel.latitude, vessel.longitude, active.Body.Radius, flight.SeaLevelAltitude,
                Vector3d.Dot(velocity, east), Vector3d.Dot(velocity, north), flight.Vz, out AttitudeVector position,
                out AttitudeVector surfaceVelocity);
            double age = Math.Max(0, now - active.CorrectionAt);
            double fraction = Math.Min(1, age / Math.Max(1e-6, correction.HoldSeconds));
            var expectedVelocity = correction.StartVelocity + (correction.EndVelocity - correction.StartVelocity) * fraction;
            var expectedPosition = correction.StartPosition + (correction.EndPosition - correction.StartPosition) * fraction;
            if (age > correction.HoldSeconds)
                expectedPosition += correction.EndVelocity * (age - correction.HoldSeconds);
            active.ReferenceVelocityError = (surfaceVelocity - expectedVelocity).Length;
            active.ReferencePositionError = (position - expectedPosition).Length;
        }

        private static bool ReferenceMatches(TargetedRecovery active) =>
            active.ReferencePositionError <= 40 && active.ReferenceVelocityError <= 10;

        private static bool VerifiedReleaseMatches(TargetedRecovery active, BoostbackCorrectionResult correction)
        {
            if (!ReferenceMatches(active) || !TargetedLandingGuidance.IsBoostbackCutoffTarget(correction.AfterCorrection)) return false;
            double timeLeft = Math.Max(0, correction.AfterCorrection.TotalSeconds - correction.HoldSeconds);
            double trackingMargin = active.ReferencePositionError + active.ReferenceVelocityError * timeLeft;
            return correction.AfterCorrection.MissDistance + trackingMargin <= TargetedLandingGuidance.BoostbackCutoffDistance;
        }

        private Vector3d BodyFixedBoostbackDirection(AttitudeVector direction, Vector3d up)
        {
            TargetedBasis(up, out Vector3d east, out Vector3d north);
            double lon = vessel.longitude * AttitudeGuidance.Degree, lat = vessel.latitude * AttitudeGuidance.Degree;
            var surfaceEast = new AttitudeVector(-Math.Sin(lon), Math.Cos(lon), 0);
            var surfaceUp = new AttitudeVector(Math.Cos(lat) * Math.Cos(lon), Math.Cos(lat) * Math.Sin(lon), Math.Sin(lat));
            return (east * direction.Dot(surfaceEast) + up * direction.Dot(surfaceUp) +
                north * direction.Dot(surfaceUp.Cross(surfaceEast))).normalized;
        }

        private BoostbackCorrectionInput CaptureBoostbackCorrection(TargetedRecovery active, Flight flight, Vector3d up,
            Vector3d direction, Command command)
        {
            if (!controller.TryBoostbackLimits(command.Engines, flight, out double minimum, out double maximum)) return null;
            if (command.Engines == active.Boost.PrecisionEngines) minimum = active.TrimMinimum;
            TargetedBasis(up, out Vector3d east, out Vector3d north);
            Vector3d kept = Vector3d.zero, dropped = Vector3d.zero;
            double keptThrust = 0, keptTail = 0, droppedThrust = 0, droppedTail = 0;
            long droppedMask = 0;
            for (int i = 0; i < engines.Count; i++)
            {
                EngineBinding engine = engines[i];
                double force = engine.Engine.finalThrust;
                long bit = 1L << i;
                if (force <= 0) continue;
                double tail = engine.BoostbackShutdownTime(cfg.SampleSeconds);
                if ((command.Engines & bit) != 0)
                {
                    kept += engine.ThrustVector();
                    keptThrust += force;
                    keptTail += tail * force;
                }
                else
                {
                    dropped += engine.ThrustVector();
                    droppedThrust += force;
                    droppedTail += tail * force;
                    droppedMask |= bit;
                }
            }
            return new BoostbackCorrectionInput
            {
                Mask = command.Engines, ReleasedMask = droppedMask,
                Direction = Components(direction), RetainedForce = Components(kept), ReleasedForce = Components(dropped),
                Minimum = minimum, Maximum = maximum, Requested = command.X * flight.Mass,
                RetainedTailSeconds = keptThrust > 0 ? keptTail / keptThrust : 0,
                ReleasedTailSeconds = droppedThrust > 0 ? droppedTail / droppedThrust : 0,
                ImpulseRemaining = BoostbackImpulseRemaining(active),
                Interval = TargetedLandingGuidance.CorrectionInterval(active.BoostStage),
                Fine = active.BoostStage == BoostbackStage.Fine, RequireEntry = active.UseJointPlan,
                AwaitingThrust = (flight.EstablishedThrust & command.Engines) != command.Engines,
                AllowReturnOnly = active.Boost.ReturnOnly || active.Fallback != null
            };

            AttitudeVector Components(Vector3d vector) =>
                new AttitudeVector(Vector3d.Dot(vector, east), Vector3d.Dot(vector, north), Vector3d.Dot(vector, up));
        }
    }
}
