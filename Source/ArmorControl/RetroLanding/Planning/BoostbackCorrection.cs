using System;
using System.Diagnostics;
using System.Threading;

namespace LandingPoc
{
    internal sealed class BoostbackCorrectionInput
    {
        // Vectors in the captured east, north, up frame. Forces are in kN.
        internal AttitudeVector Direction, RetainedForce, ReleasedForce;
        internal long Mask, ReleasedMask;
        internal double Minimum, Maximum, Requested, ImpulseRemaining;
        internal double RetainedTailSeconds, ReleasedTailSeconds;
        internal double Interval;
        internal bool Fine, RequireEntry;
    }

    internal sealed class BoostbackCorrectionResult
    {
        internal BoostbackApproach CutNow, AfterCorrection;
        internal AttitudeVector Direction; // Body-fixed Cartesian, as in propagation.
        internal AttitudeVector StartPosition, StartVelocity, EndPosition, EndVelocity;
        internal double Thrust, HoldSeconds, ElapsedMilliseconds, TailImpulse;
        internal long Mask;
        internal int Traces, Candidates;
    }

    internal sealed partial class BoostbackPreview
    {
        private const double ProbeAngle = 5 * Degree;
        private const int CorrectionBudgetMilliseconds = 220;
        private static readonly AttitudeVector GlobeNorth = new AttitudeVector(0, 0, 1);

        internal static BoostbackPreviewResult Correct(BoostbackPreviewInput input, BoostbackCorrectionInput live,
            CancellationToken stop)
        {
            if (!Valid(input)) return new BoostbackPreviewResult { Status = "invalid_snapshot" };
            var preview = new BoostbackPreview(input, stop);
            var correction = preview.FindCorrection(live);
            return new BoostbackPreviewResult
            {
                Status = correction.CutNow?.Touchdown.Status ?? "no_plan",
                DefaultAoa = correction.CutNow, Correction = correction, Footprints = preview.footprints,
                RunMilliseconds = correction.ElapsedMilliseconds
            };
        }

        internal static void SurfaceState(double latDegrees, double lonDegrees, double radius, double height,
            double eastSpeed, double northSpeed, double verticalSpeed, out AttitudeVector position, out AttitudeVector velocity)
        {
            double lat = latDegrees * Degree, lon = lonDegrees * Degree;
            var up = new AttitudeVector(Math.Cos(lat) * Math.Cos(lon), Math.Cos(lat) * Math.Sin(lon), Math.Sin(lat));
            var east = new AttitudeVector(-Math.Sin(lon), Math.Cos(lon), 0);
            position = up * (radius + height);
            velocity = east * eastSpeed + up.Cross(east) * northSpeed + up * verticalSpeed;
        }

        private BoostbackCorrectionResult FindCorrection(BoostbackCorrectionInput live)
        {
            var watch = Stopwatch.StartNew();
            double aoa = input.PreferredGlideAoa;
            FootprintAtmosphere air = aoa >= input.MinimumAoa && aoa <= input.MaximumAoa ? PitchAtmosphere(aoa) : null;
            double roll = input.PreferredGlideBank;
            if (air == null) { air = input.DefaultAtmosphere; aoa = input.DefaultAoa; roll = 0; }
            var result = new BoostbackCorrectionResult
            {
                StartPosition = initial.Position, StartVelocity = initial.Velocity, Mask = live.Mask
            };
            var axis = ToBodyFixed(live.Direction).Unit;
            result.CutNow = CorrectionTrace(live, axis, 0, 0, air, aoa, roll, out _);
            result.TailImpulse = result.CutNow?.BoostThrustDeltaV ?? double.NaN;
            bool cutoffFeasible = result.CutNow?.Safe == true && (!live.RequireEntry || result.CutNow.Touchdown.EntryCompleted);

            if (cutoffFeasible && TargetedLandingGuidance.IsBoostbackCutoffTarget(result.CutNow))
                return Complete();

            double quantum = Math.Max(0.01, input.Settings.SampleSeconds);
            double available = live.ImpulseRemaining - (FootprintAtmosphere.Finite(result.TailImpulse) ? result.TailImpulse : 0);
            double probeThrust = live.Fine ? live.Minimum : live.Requested;
            double hold = HoldTime(probeThrust, live.Interval, available, quantum);
            if (live.Mask != 0 && probeThrust > 0)
            {
                if (!live.Fine && hold >= quantum && result.CutNow != null &&
                    watch.ElapsedMilliseconds < CorrectionBudgetMilliseconds)
                {
                    // Fly the correction implied by the latest predicted cutoff, as in BORG.
                    // Keep the planned radial component for a jointly optimized reentry.
                    var targetward = FeedbackDirection(result.CutNow, axis, live.RequireEntry);
                    if (targetward.Length > 0.5)
                        Consider(targetward, probeThrust, hold);
                    if (result.AfterCorrection != null)
                        return Complete();
                }
                var lateral = initial.Position.Unit.Cross(axis).Unit;
                var pitchUp = axis.Cross(lateral).Unit;
                foreach (AttitudeVector direction in new[] { axis,
                    (axis + lateral * Math.Tan(ProbeAngle)).Unit, (axis - lateral * Math.Tan(ProbeAngle)).Unit,
                    (axis + pitchUp * Math.Tan(ProbeAngle)).Unit, (axis - pitchUp * Math.Tan(ProbeAngle)).Unit })
                {
                    if (hold < quantum || watch.ElapsedMilliseconds >= CorrectionBudgetMilliseconds) break;
                    Consider(direction, probeThrust, hold);
                }

                if (live.Fine && result.AfterCorrection != null && result.CutNow != null &&
                    watch.ElapsedMilliseconds < CorrectionBudgetMilliseconds)
                {
                    // Estimate the most useful hold length from the measured
                    // endpoint response, then verify that length with the full trace.
                    double dx = result.AfterCorrection.MissEast - result.CutNow.MissEast;
                    double dy = result.AfterCorrection.MissNorth - result.CutNow.MissNorth;
                    double change = dx * dx + dy * dy;
                    if (change > 1e-6)
                    {
                        double duration = -hold * (result.CutNow.MissEast * dx + result.CutNow.MissNorth * dy) / change;
                        double adjusted = HoldTime(probeThrust, Math.Max(quantum, Math.Min(3 * live.Interval, duration)),
                            available, quantum);
                        if (adjusted >= quantum && Math.Abs(adjusted - hold) >= quantum)
                            Consider(result.Direction, probeThrust, adjusted);
                    }
                }
                if (result.AfterCorrection == null && result.CutNow != null && hold >= quantum &&
                    watch.ElapsedMilliseconds < CorrectionBudgetMilliseconds)
                {
                    var targetward = (initialEast * -result.CutNow.MissEast + initialNorth * -result.CutNow.MissNorth).Unit;
                    if (targetward.Length > 0.5)
                    {
                        // A larger turn is useful occasionally, but only a
                        // trajectory that passes the same cutoff test wins.
                        var retry = TargetedLandingGuidance.Slew(axis, targetward, GlobeNorth, 4 * ProbeAngle);
                        double duration = HoldTime(probeThrust, 2 * live.Interval, available, quantum);
                        if (duration >= quantum) Consider(retry, probeThrust, duration);
                    }
                }

                if (live.Fine && !TargetedLandingGuidance.IsBoostbackCutoffTarget(result.AfterCorrection) &&
                    live.Maximum > probeThrust && watch.ElapsedMilliseconds < CorrectionBudgetMilliseconds)
                {
                    double higher = 0.5 * (live.Minimum + live.Maximum);
                    double shorter = HoldTime(higher, live.Interval * 0.5, available, quantum);
                    if (shorter >= quantum)
                        Consider(result.AfterCorrection == null ? axis : result.Direction, higher, shorter);
                }
            }

            return Complete();

            BoostbackCorrectionResult Complete()
            {
                result.Traces = footprints;
                result.ElapsedMilliseconds = watch.Elapsed.TotalMilliseconds;
                return result;
            }

            void Consider(AttitudeVector direction, double thrust, double seconds)
            {
                stop.ThrowIfCancellationRequested();
                result.Candidates++;
                var candidate = CorrectionTrace(live, direction, thrust, seconds, air, aoa, roll,
                    out TrajectoryState atRelease);
                if (candidate == null || !candidate.Safe || live.RequireEntry && !candidate.Touchdown.EntryCompleted ||
                    candidate.BoostThrustDeltaV > live.ImpulseRemaining || !Better(candidate, result.AfterCorrection) ||
                    (cutoffFeasible && candidate.MissDistance >= result.CutNow.MissDistance))
                    return;
                result.AfterCorrection = candidate;
                result.Direction = direction;
                result.Thrust = thrust;
                result.HoldSeconds = seconds;
                result.EndPosition = atRelease.Position;
                result.EndVelocity = atRelease.Velocity;
            }
        }

        private AttitudeVector FeedbackDirection(BoostbackApproach cutoff, AttitudeVector reference, bool keepPitch)
        {
            SurfaceState(input.WaypointLatitude, input.WaypointLongitude, input.Radius, 0, 0, 0, 0,
                out AttitudeVector target, out _);
            SurfaceState(cutoff.Lat, cutoff.Lon, input.Radius, 0, 0, 0, 0,
                out AttitudeVector impact, out _);
            var up = initial.Position.Unit;
            var lateral = target - impact;
            lateral = (lateral - up * lateral.Dot(up)).Unit;
            if (!keepPitch || lateral.Length < 0.5) return lateral;
            double vertical = reference.Dot(up);
            return (lateral * Math.Sqrt(Math.Max(0, 1 - vertical * vertical)) + up * vertical).Unit;
        }

        private double HoldTime(double thrust, double desired, double remaining, double quantum)
        {
            if (thrust <= 0 || remaining <= 0) return 0;
            double duration = Math.Min(desired, remaining * input.Flight.Mass / thrust);
            return duration >= quantum ? duration : 0;
        }

        private AttitudeVector ToBodyFixed(AttitudeVector local) =>
            initialEast * local.X + initialNorth * local.Y + initial.Position.Unit * local.Z;

        private BoostbackApproach CorrectionTrace(BoostbackCorrectionInput live, AttitudeVector direction,
            double thrust, double hold, FootprintAtmosphere atmosphere, double aoa, double roll, out TrajectoryState atRelease)
        {
            var motion = initial;
            atRelease = motion;
            var flight = input.Flight;
            flight.Propellant = flight.Propellant.ForForecast();
            AttitudeVector retained = ToBodyFixed(live.RetainedForce);
            AttitudeVector released = ToBodyFixed(live.ReleasedForce);
            var axis = retained.Length > 0.001 ? retained.Unit : ToBodyFixed(input.BoostbackBodyAxis).Unit;
            if (axis.Length < 0.5) axis = direction;
            var trial = new BoostbackApproach { PitchAoa = aoa, GlideBank = roll };
            double retainedTail = 2 * live.RetainedTailSeconds;
            double releasedTail = 2 * live.ReleasedTailSeconds;
            double time = 0;
            double total = Math.Max(hold + retainedTail, releasedTail);
            double commandThrust = retained.Length;
            FootprintAtmosphere burnAir = input.BoostbackAtmosphere ?? atmosphere;
            double exhaust = input.Settings.SpecificImpulse * Settings.G0;
            while (time + 1e-8 < total)
            {
                stop.ThrowIfCancellationRequested();
                double dt = Math.Min(0.1, total - time);
                if (time < hold) dt = Math.Min(dt, hold - time);
                double mid = time + dt * 0.5;
                AttitudeVector held;
                if (time < hold)
                {
                    axis = TargetedLandingGuidance.Slew(axis, direction, GlobeNorth, input.BoostbackTurnRate * dt);
                    double liveCap = flight.Mass * input.Settings.ThrustLimitFor(Phase.Boostback) * Settings.G0;
                    if (live.Minimum > liveCap) return null;
                    double requested = Math.Min(thrust, liveCap);
                    commandThrust += (requested - commandThrust) * Math.Min(1, dt / Math.Max(0.1, input.Settings.SampleSeconds));
                    held = axis * commandThrust;
                }
                else held = axis * (commandThrust * TailFraction(mid - hold, retainedTail));
                var tail = released * TailFraction(mid, releasedTail);
                double heldFuel = held.Length * dt / exhaust, releasedFuel = tail.Length * dt / exhaust;
                if ((heldFuel > 0 && !flight.Propellant.ConsumeForecast(live.Mask, heldFuel)) ||
                    (releasedFuel > 0 && !flight.Propellant.ConsumeForecast(live.ReleasedMask, releasedFuel)))
                    return null;
                var acceleration = (held + tail) * (1 / flight.Mass);
                trial.BoostThrustDeltaV += acceleration.Length * dt;
                if (!Advance(ref motion, dt, acceleration, burnAir, flight.Mass, input.BoostbackAtmosphere != null) ||
                    motion.Altitude(input.Radius) <= input.WaypointHeight) return null;
                flight.Mass -= heldFuel + releasedFuel;
                if (flight.Mass < input.DryMass + input.FuelReserve) return null;
                time += dt;
                if (time <= hold + 1e-8) atRelease = motion;
            }
            trial.CutoffSeconds = time;
            trial.BoostFuel = input.Flight.Mass - flight.Mass;
            return FinishTrace(motion, flight, input.Engines, trial, atmosphere);
        }

        internal static double TailFraction(double elapsed, double duration) =>
            duration > 0 ? Math.Max(0, 1 - elapsed / duration) : 0;
    }
}
