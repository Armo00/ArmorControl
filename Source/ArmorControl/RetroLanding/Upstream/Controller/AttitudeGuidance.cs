using System;

namespace LandingPoc
{
    internal struct AttitudeVector
    {
        internal readonly double X, Y, Z;

        internal AttitudeVector(double x, double y, double z) { X = x; Y = y; Z = z; }
        internal double Length { get { return Math.Sqrt(Dot(this)); } }
        internal AttitudeVector Unit { get { return this * (1 / Math.Max(Length, 1e-12)); } }
        internal double Dot(AttitudeVector other) { return X * other.X + Y * other.Y + Z * other.Z; }
        internal AttitudeVector Cross(AttitudeVector b) { return new AttitudeVector(Y * b.Z - Z * b.Y, Z * b.X - X * b.Z, X * b.Y - Y * b.X); }
        internal AttitudeVector InPlane(AttitudeVector normal) { return this - normal * Dot(normal); }

        public static AttitudeVector operator +(AttitudeVector a, AttitudeVector b) { return new AttitudeVector(a.X + b.X, a.Y + b.Y, a.Z + b.Z); }
        public static AttitudeVector operator -(AttitudeVector a, AttitudeVector b) { return a + b * -1; }
        public static AttitudeVector operator *(AttitudeVector v, double scale) { return new AttitudeVector(v.X * scale, v.Y * scale, v.Z * scale); }
    }

    internal sealed class AttitudeGuidance
    {
        internal const double Degree = Math.PI / 180;
        internal const double RetrogradeMinimumSpeed = 1.0;
        internal const double ReferenceRollRate = 20 * Degree;
        internal const double CoastMaxRate = 0.5;
        internal const double AngleResponseSeconds = 1.6;
        private AttitudeVector retrograde, previousAxis, levelForward, finalTangent;
        private bool trackingRetrograde, hasRollReference, useNorth, hasFinalPointing;

        internal AttitudeVector SurfaceRetrograde(AttitudeVector velocity, AttitudeVector initialDirection)
        {
            double speed = velocity.Length;
            trackingRetrograde = speed >= (trackingRetrograde ? 0.5 : RetrogradeMinimumSpeed);
            if (trackingRetrograde) retrograde = velocity * (-1 / speed);
            else if (retrograde.Length < 0.5) retrograde = initialDirection;
            return retrograde;
        }

        internal static AttitudeVector CoastPointing(AttitudeVector retrograde, AttitudeVector up,
                                                     AttitudeVector north, double pitchAoaDegrees)
        {
            if (pitchAoaDegrees == 0) return retrograde;
            double angle = pitchAoaDegrees * Degree;
            return retrograde * Math.Cos(angle) + PitchUp(retrograde, up, north) * Math.Sin(angle);
        }

        internal static AttitudeVector BankedCoastPointing(AttitudeVector retrograde, AttitudeVector up,
            AttitudeVector north, double bankDegrees, double pitchDegrees, out AttitudeVector forward)
        {
            AttitudeVector pitchUp = Rotate(PitchUp(retrograde, up, north), retrograde, bankDegrees * Degree);
            double angle = pitchDegrees * Degree;
            double sine = Math.Sin(angle), cosine = Math.Cos(angle);
            forward = pitchUp * cosine - retrograde * sine;
            return retrograde * cosine + pitchUp * sine;
        }

        internal static double PitchAoa(AttitudeVector retrograde, AttitudeVector pointing, AttitudeVector up,
                                        AttitudeVector north)
        {
            return Math.Atan2(pointing.Dot(PitchUp(retrograde, up, north)), pointing.Dot(retrograde)) / Degree;
        }

        private static AttitudeVector PitchUp(AttitudeVector retrograde, AttitudeVector up, AttitudeVector north)
        {
            AttitudeVector upInPlane = up.InPlane(retrograde);
            return (upInPlane.Length < 1e-4 ? north.InPlane(retrograde) : upInPlane).Unit;
        }

        internal static double FinalLateralDemand(double speed)
        {
            return Math.Max(0, Math.Min(1, (speed - 0.5) / 7.5));
        }

        internal static double FinalTiltDegrees(double demand, double timeToContact)
        {
            // Reach the touchdown cone early enough for the hull to follow.
            double timeToLevel = Math.Max(0, timeToContact - 0.5);
            // Small velocity reversals should not send the hull into another
            // large correction while it is still unwinding the last turn.
            double brakingDemand = Math.Max(0, Math.Min(1, (demand - 0.2) / 0.55));
            double speedLimit = 1.5 + 18.5 * brakingDemand * brakingDemand * (3 - 2 * brakingDemand);
            return Math.Min(speedLimit, Math.Min(8 + 12 * demand, 2.5 + 0.6 * FinalAimRateDegrees(demand) * timeToLevel));
        }
        internal static double FinalAimRateDegrees(double demand) => 6 + 12 * demand;
        internal static double FinalBodyRateDegrees(double demand) => 14 + 8 * demand;
        internal const double FinalRollRate = 0.20;

        internal AttitudeVector FinalPointing(AttitudeVector target, AttitudeVector up, AttitudeVector body,
                                              double lateralSpeed, double demand, double step, double timeToContact,
                                              out AttitudeVector aimRate, out AttitudeVector settledAim)
        {
            double limit = Math.Tan(FinalTiltDegrees(demand, timeToContact) * Degree);
            double weight = lateralSpeed * lateralSpeed / (lateralSpeed * lateralSpeed + 0.36);
            AttitudeVector desired = Limited(target.InPlane(up) * (weight / Math.Max(0.05, target.Dot(up))), limit);
            settledAim = (up + desired).Unit;
            if (!hasFinalPointing)
            {
                finalTangent = Limited(body.InPlane(up) * (1 / Math.Max(0.05, body.Dot(up))), limit);
                hasFinalPointing = true;
            }
            AttitudeVector previous = (up + finalTangent.InPlane(up)).Unit;
            double slew = FinalAimRateDegrees(demand) * Degree;
            finalTangent = Limited(finalTangent.InPlane(up), limit);
            finalTangent += Limited(desired - finalTangent, slew * step);
            AttitudeVector aim = (up + finalTangent).Unit;
            aimRate = step > 0 ? Limited(PointingError(previous, aim, up) * (1 / step), slew) : default;
            return aim;
        }

        internal void ResetFinalPointing() { hasFinalPointing = false; }

        private static AttitudeVector Limited(AttitudeVector value, double magnitude)
        {
            return value.Length > magnitude ? value.Unit * magnitude : value;
        }

        internal AttitudeVector RollForward(AttitudeVector direction, AttitudeVector up, AttitudeVector north,
                                           double rollDegrees, double step)
        {
            AttitudeVector zenith = up.InPlane(direction);
            // The horizon loses its roll reference near vertical. Hysteresis
            // and a rate limit keep the heading continuous through that region.
            useNorth = zenith.Length < (useNorth ? 0.32 : 0.22);
            AttitudeVector reference = useNorth ? north.InPlane(direction).Unit : zenith.Unit;
            if (!hasRollReference)
            {
                levelForward = reference;
                hasRollReference = true;
            }
            else
            {
                AttitudeVector transported = Swing(previousAxis, direction, levelForward, levelForward).InPlane(direction).Unit;
                double correction = SignedAngle(transported, reference, direction);
                levelForward = Rotate(transported, direction, Clamp(correction, ReferenceRollRate * step));
            }
            previousAxis = direction;
            return Rotate(levelForward, direction, WrapDegrees(rollDegrees) * Degree);
        }

        internal static double WrapDegrees(double angle)
        {
            double turns = angle % 360;
            return turns >= 180 ? turns - 360 : turns < -180 ? turns + 360 : turns;
        }

        internal static AttitudeVector PointingError(AttitudeVector from, AttitudeVector to, AttitudeVector fallback)
        {
            AttitudeVector axis = from.Cross(to);
            double sine = axis.Length, cosine = from.Dot(to);
            if (sine > 1e-8) return axis * (Math.Atan2(sine, cosine) / sine);
            return cosine < 0 ? fallback.InPlane(from).Unit * Math.PI : new AttitudeVector();
        }

        internal static double RollError(AttitudeVector bodyAxis, AttitudeVector bodyForward,
                                         AttitudeVector targetAxis, AttitudeVector targetForward)
        {
            AttitudeVector aligned = Swing(bodyAxis, targetAxis, bodyForward, bodyForward);
            return SignedAngle(aligned, targetForward, targetAxis);
        }

        private static AttitudeVector Swing(AttitudeVector from, AttitudeVector to, AttitudeVector fallback, AttitudeVector value)
        {
            AttitudeVector rotation = PointingError(from, to, fallback);
            return rotation.Length > 1e-8 ? Rotate(value, rotation.Unit, rotation.Length) : value;
        }

        internal static double SignedAngle(AttitudeVector from, AttitudeVector to, AttitudeVector around)
        {
            return Math.Atan2(around.Dot(from.Cross(to)), from.Dot(to));
        }

        private static AttitudeVector Rotate(AttitudeVector vector, AttitudeVector axis, double angle)
        {
            double cosine = Math.Cos(angle);
            return vector * cosine + axis.Cross(vector) * Math.Sin(angle) + axis * (axis.Dot(vector) * (1 - cosine));
        }

        internal static double DampAxis(double error, double rate, double availableAcceleration, double maxRate,
                                        double referenceRate = 0, double? settledError = null)
        {
            const double angleDeadband = 0.12 * Degree;
            const double rateDeadband = 0.08 * Degree;
            const double rateResponseSeconds = 0.4;
            double distance = Math.Max(0, Math.Abs(error) - angleDeadband);
            if (distance == 0 && Math.Abs(rate - referenceRate) <= rateDeadband) return 0;
            // Preserve braking room on both sides of the target even if RCS
            // provides far less angular acceleration than the powered TVC.
            double stoppingRate = Math.Sqrt(2 * Math.Max(0, availableAcceleration) * 0.55 * distance);
            double desired = Clamp(referenceRate + Math.Sign(error) * Math.Min(distance / AngleResponseSeconds, stoppingRate), maxRate);
            if (settledError.HasValue)
            {
                // The moving reference can still be turning when the hull
                // is already close to the final aim. Leave room to stop there.
                double left = Math.Max(0, Math.Abs(settledError.Value) - angleDeadband);
                double braking = Math.Min(Settings.TerminalTurnBraking, Math.Max(0, availableAcceleration));
                double safeRate = Math.Sqrt(0.9 * braking * left);
                double direction = Math.Sign(settledError.Value);
                desired = direction * Math.Max(0, Math.Min(safeRate, direction * desired));
            }
            return (desired - rate) / rateResponseSeconds;
        }

        internal static double TrimCoastAxis(double error, double rate, double authority, double step, ref double trim)
        {
            double command = DampAxis(error, rate, authority, CoastMaxRate);
            if (Math.Abs(error) > 10 * Degree || authority <= 0)
                trim = 0;
            else if (Math.Abs(rate) < CoastMaxRate / 3 &&
                (Math.Abs(command + trim) < authority || Math.Sign(command) != Math.Sign(command + trim)))
                trim = Clamp(trim + command * step / 2.5, 0.3 * authority);
            return command + trim;
        }

        private static double Clamp(double value, double limit) { return Math.Max(-limit, Math.Min(limit, value)); }
    }
}
