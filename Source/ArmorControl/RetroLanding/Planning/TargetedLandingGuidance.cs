using System;

namespace LandingPoc
{
    internal enum BoostbackStage { Aligning, Initial, Full, Fine }

    internal struct BoostbackCutoffTrend
    {
        private double? previousUt;
        private double previousMiss;
        internal bool EnteredWindow { get; private set; }

        internal bool Observe(double ut, double miss)
        {
            if (previousUt.HasValue && ut <= previousUt.Value) return false;
            bool increasing = EnteredWindow && miss > previousMiss;
            previousUt = ut;
            previousMiss = miss;
            EnteredWindow |= miss <= TargetedLandingGuidance.BoostbackCutoffWindow;
            return increasing;
        }
    }

    internal static class TargetedLandingGuidance
    {
        // The initial plan and the end of targeted glide use the same radius.
        internal const double ApproachTolerance = 250;
        internal const double FullBoostbackAngle = 3;
        internal const double BoostbackCutoffWindow = 2500;
        internal const double BoostbackCutoffDistance = 300;
        internal const double InitialGlideTolerance = 3000;
        internal const double BoostbackImpulseLimit = 1.5;
        internal const double MainCorrectionInterval = 1.0;
        internal const double FineCorrectionInterval = 0.5;

        internal static double CorrectionInterval(BoostbackStage stage) =>
            stage == BoostbackStage.Fine ? FineCorrectionInterval : MainCorrectionInterval;

        // The caller already checks freshness and tracking. Keep a full main update's
        // aim through the brief handoff to the next result. A short burn still expires.
        internal static bool FollowBoostbackCorrection(BoostbackStage stage, double age, double hold) =>
            age < hold || (stage == BoostbackStage.Full && hold >= MainCorrectionInterval);

        internal static bool MayStartBoostback(double clearance, double verticalSpeed, Settings settings) =>
            verticalSpeed >= 0 || settings.IgnitionAltitudeBlocked(clearance);

        internal static bool GlideConflictsWithIgnition(Settings settings, double clearance, double waitSeconds,
            double glideAngle, double alignmentAngle, double turnDegreesPerSecond) =>
            !settings.IgnitionAltitudeBlocked(clearance) && glideAngle > Settings.IgnitionToleranceDegrees &&
            waitSeconds <= Math.Max(glideAngle, alignmentAngle) / Math.Max(1, turnDegreesPerSecond) + 2 * settings.SampleSeconds;

        internal static double GlideTolerance(double altitude, double targetHeight, Settings settings)
        {
            double lower = targetHeight + Math.Max(0, settings.MaxIgnitionAltitude);
            double span = settings.EntryBurnAltitude - lower;
            if (span <= 0) return ApproachTolerance;
            double fraction = Math.Max(0, Math.Min(1, (altitude - lower) / span));
            return ApproachTolerance + fraction * (InitialGlideTolerance - ApproachTolerance);
        }

        internal static BoostbackStage BoostbackStageFor(BoostbackStage stage, double angle,
            bool initialEstablished, double remaining, double miss,
            double precisionAcceleration, Settings settings)
        {
            if (stage == BoostbackStage.Fine) return stage;
            double fullAngle = Math.Min(FullBoostbackAngle, settings.BoostbackStartAoa);
            if (stage == BoostbackStage.Aligning)
                return angle <= settings.BoostbackStartAoa ? BoostbackStage.Initial : stage;
            if (stage == BoostbackStage.Initial && (angle > fullAngle || !initialEstablished)) return stage;
            double fineImpulse = precisionAcceleration * Math.Max(1, settings.AttitudeResponseSeconds);
            if (miss <= settings.BoostbackFineDistance || remaining <= fineImpulse)
                return BoostbackStage.Fine;
            return BoostbackStage.Full;
        }

        internal static long BoostbackMask(BoostbackStage stage, long precision, long full) =>
            stage == BoostbackStage.Aligning ? 0 : stage == BoostbackStage.Full ? full : precision;

        internal static AttitudeVector Slew(AttitudeVector from, AttitudeVector to,
            AttitudeVector fallback, double radians)
        {
            AttitudeVector error = AttitudeGuidance.PointingError(from, to, fallback);
            double angle = Math.Min(error.Length, Math.Max(0, radians));
            if (angle <= 1e-9) return from;
            AttitudeVector axis = error.Unit;
            return from * Math.Cos(angle) + axis.Cross(from) * Math.Sin(angle) +
                axis * (axis.Dot(from) * (1 - Math.Cos(angle)));
        }

        internal static string BoostbackCutoffReason(double delivered, double planned, BoostbackApproach cutoffForecast,
            double forecastUt, ref BoostbackCutoffTrend trend)
        {
            if (planned > 0 && delivered > planned * BoostbackImpulseLimit) return "impulse_limit";
            if (cutoffForecast?.Safe != true || !FootprintAtmosphere.Finite(cutoffForecast.MissDistance) ||
                !FootprintAtmosphere.Finite(forecastUt)) return null;
            if (IsBoostbackCutoffTarget(cutoffForecast)) return "predicted_target";
            return trend.Observe(forecastUt, cutoffForecast.MissDistance) ? "predicted_distance_increasing" : null;
        }

        internal static bool IsBoostbackCutoffTarget(BoostbackApproach forecast) =>
            forecast != null && forecast.Safe && forecast.MissDistance <= BoostbackCutoffDistance;

        internal static double BoundedBoostbackThrust(double requested, double minimum, double maximum) =>
            Math.Max(minimum, Math.Min(maximum, requested));

        internal static double AddImpulse(double delivered, double projectedThrust, double mass, double dt) =>
            delivered + (mass > 0 && dt > 0 ? projectedThrust * dt / mass : 0);

        internal static AttitudeVector TargetVelocity(AttitudeVector offset, AttitudeVector velocity,
            double time, double lateralAcceleration, double contactTolerance, out bool capture)
        {
            // A short correction has to finish with zero lateral speed.
            // Allow an initially moving craft to use its natural decel path.
            AttitudeVector error = offset - velocity * (time / 2);
            double correction = lateralAcceleration * time * time / 4;
            capture = error.Length <= correction + contactTolerance;
            if (!capture || time <= 0.5 || offset.Length <= contactTolerance) return default;
            double speed = Math.Min(2 * offset.Length / time, lateralAcceleration * time / 2);
            return offset.Unit * speed;
        }
    }
}
