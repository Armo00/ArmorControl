using System;
using System.Reflection;
using Stopwatch = System.Diagnostics.Stopwatch;
using LandingPoc;
using UnityEngine;

namespace KspRetroLanding
{
    internal sealed class FarCoastPrediction
    {
        private delegate void CalculateForces(Vessel vessel, out Vector3 force, out Vector3 torque,
                                              Vector3 airVelocity, double altitude);
        private const double RefreshSeconds = 0.3;
        private const double MaximumPoseChange = 5;
        private const double PlanningHorizonSeconds = 20;
        private const int SamplesPerFrame = 2;
        private readonly CalculateForces calculate;
        private readonly Func<Vessel, bool> voxelizationValid;
        private readonly Vector3d[] velocities = new Vector3d[4];
        private readonly double[] times = new double[4];
        private int samples, nextSample;
        private double retryAt;
        private sealed class Capture
        {
            internal double Time, PitchAoa, Gravity, LiveDrag;
            internal Quaternion Rotation;
            internal AeroCoastForecast Path;
            internal SampleAero Sample;
        }
        private Capture pending, forecast;
        private int sampledFrame = -1;
        private int footprintFrame = -1, spareSamples;
        internal bool CanSampleFootprint => !failed && calculate != null && voxelizationValid != null;
        private bool failed;
        private long queryTicks, frameTicks, peakFrameTicks;
        private int queryFrame = -1;
        internal int QueryCount { get; private set; }
        internal double QueryMilliseconds => 1000.0 * queryTicks / Stopwatch.Frequency;

        internal double TakePeakFrameMilliseconds()
        {
            double value = 1000.0 * peakFrameTicks / Stopwatch.Frequency;
            peakFrameTicks = 0;
            return value;
        }

        private void MeasureForces(Vessel vessel, out Vector3 force, out Vector3 torque, Vector3 velocity, double altitude)
        {
            long start = Stopwatch.GetTimestamp();
            try { calculate(vessel, out force, out torque, velocity, altitude); }
            finally
            {
                long cost = Stopwatch.GetTimestamp() - start;
                if (queryFrame != Time.frameCount)
                {
                    queryFrame = Time.frameCount;
                    frameTicks = 0;
                }
                queryTicks += cost;
                frameTicks += cost;
                peakFrameTicks = Math.Max(peakFrameTicks, frameTicks);
                QueryCount++;
            }
        }

        internal double MeasuredDrag { get; private set; }
        internal double ModeledDrag { get; private set; }

        internal FarCoastPrediction()
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.GetName().Name != "FerramAerospaceResearch") continue;
                Type api = assembly.GetType("FerramAerospaceResearch.FARAPI");
                if (api == null) continue;
                Type vectorOut = typeof(Vector3).MakeByRefType();
                MethodInfo forces = api.GetMethod("CalculateVesselAeroForces",
                    new[] { typeof(Vessel), vectorOut, vectorOut, typeof(Vector3), typeof(double) });
                MethodInfo ready = api.GetMethod("VesselVoxelizationCompletedAndValid", new[] { typeof(Vessel) });
                if (forces == null || ready == null || !forces.IsStatic || !ready.IsStatic ||
                    forces.ReturnType != typeof(void) || ready.ReturnType != typeof(bool)) continue;
                calculate = (CalculateForces)Delegate.CreateDelegate(typeof(CalculateForces), forces, false);
                voxelizationValid = (Func<Vessel, bool>)Delegate.CreateDelegate(typeof(Func<Vessel, bool>), ready, false);
                break;
            }
        }

        internal void Update(Vessel vessel, Transform frame, Vector3d coastPointing, double now, double altitude,
                             Vector3d velocity, Vector3d horizontal, Vector3d up, Settings cfg, ref Flight flight)
        {
            MeasuredDrag = ModeledDrag = double.NaN;
            if (calculate == null || voxelizationValid == null || failed || !vessel.mainBody.atmosphere ||
                altitude >= vessel.mainBody.atmosphereDepth || flight.Contact)
                return;
            double speed = velocity.magnitude;
            if (speed < 2) return;
            MeasureDrag(now, velocity, up, cfg);
            if (double.IsNaN(MeasuredDrag)) return;
            double coastPitchAoa = cfg.CoastPitchAoaAt(altitude);
            try
            {
                // FAR reads live vessel/Unity state. Spread its queries across
                // rendered frames, including when KSP catches up physics ticks.
                if (sampledFrame != Time.frameCount)
                {
                    sampledFrame = Time.frameCount;
                    if (pending != null && !SamePose(pending, frame.rotation, coastPitchAoa)) pending = null;
                    int budget = SamplesPerFrame - (footprintFrame == Time.frameCount ? 1 : 0);
                    if (pending == null && (now >= retryAt ||
                        (forecast != null && coastPitchAoa != forecast.PitchAoa)))
                    {
                        retryAt = now + RefreshSeconds;
                        if (voxelizationValid(vessel))
                        {
                            Vector3 force, torque;
                            MeasureForces(vessel, out force, out torque, (Vector3)velocity, altitude);
                            budget--;
                            Quaternion rotation = frame.rotation;
                            Quaternion toCoastPose = Quaternion.FromToRotation(frame.up, (Vector3)coastPointing);
                            pending = new Capture { Time = now, Rotation = rotation, PitchAoa = coastPitchAoa,
                                Gravity = cfg.Gravity, Path = new AeroCoastForecast(flight),
                                LiveDrag = Math.Max(0, -Vector3d.Dot((Vector3d)force, velocity) / (flight.Mass * speed)),
                                Sample = Sampler(vessel, altitude, horizontal, up, frame, rotation, toCoastPose, flight) };
                        }
                        else
                            forecast = null;
                    }
                    if (pending != null && !voxelizationValid(vessel))
                        pending = forecast = null;
                    if (pending != null)
                    {
                        pending.Path.Advance(pending.Gravity, pending.Sample, PlanningHorizonSeconds, budget);
                        budget = 0;
                        if (pending.Path.Complete)
                        {
                            if (pending.Path.Duration > 0) forecast = pending;
                            pending = null;
                        }
                    }
                    spareSamples = budget;
                }
                Capture usable = forecast;
                if (usable == null || !SamePose(usable, frame.rotation, coastPitchAoa) ||
                    !usable.Path.Matches(flight, now - usable.Time)) usable = pending;
                if (usable == null || !SamePose(usable, frame.rotation, coastPitchAoa) ||
                    !usable.Path.Matches(flight, now - usable.Time)) return;

                // A substantially overoptimistic FAR force must not buy more coast time.
                // The four-sample check is independent of the powered observer's 25 m/s² clamp.
                double age = now - usable.Time;
                flight.AeroForecastAge = age;
                Flight next = usable.Path.Project(flight, cfg.SampleSeconds, cfg.Gravity);
                ModeledDrag = Math.Max(0, -(flight.Vx * (next.Vx - flight.Vx) +
                    flight.Vz * (next.Vz - flight.Vz + cfg.Gravity * cfg.SampleSeconds)) / (cfg.SampleSeconds * speed));
                if (usable.LiveDrag <= 2 * MeasuredDrag + 12)
                    flight.AeroForecast = usable.Path;
            }
            catch (Exception ex)
            {
                forecast = pending = null;
                failed = true;
                Debug.LogWarning("[RetroLanding] FAR prediction disabled for this engagement: " + ex.Message);
            }
        }

        internal bool TryFootprintAero(Vessel vessel, Transform frame, Vector3d velocity, Vector3d up,
                                       Vector3d pointing, double altitude, double pressure, out FootprintAero coefficients, Vector3d? dorsal = null)
        {
            coefficients = default;
            int frameId = Time.frameCount;
            if (!CanSampleFootprint || footprintFrame == frameId || (sampledFrame == frameId && spareSamples == 0) ||
                pressure <= 0 || velocity.sqrMagnitude < 4 || !voxelizationValid(vessel)) return false;
            footprintFrame = frameId;
            try
            {
                Quaternion pose = dorsal.HasValue ?
                    Quaternion.LookRotation((Vector3)dorsal.Value, (Vector3)pointing) *
                        Quaternion.Inverse(Quaternion.LookRotation(-frame.forward, frame.up)) :
                    Quaternion.FromToRotation(frame.up, (Vector3)pointing);
                MeasureForces(vessel, out Vector3 force, out _, Quaternion.Inverse(pose) * (Vector3)velocity, altitude);
                Vector3d aero = (Vector3d)(pose * force);
                Vector3d direction = velocity.normalized;
                Vector3d lift = (up - Vector3d.Dot(up, direction) * direction).normalized;
                Vector3d cross = Vector3d.Cross(up, direction).normalized;
                double q = pressure * velocity.sqrMagnitude;
                double drag = -Vector3d.Dot(aero, direction) / q;
                double upForce = Vector3d.Dot(aero, lift) / q;
                double sideForce = Vector3d.Dot(aero, cross) / q;
                if (!FootprintAtmosphere.Finite(drag) || drag < 0 ||
                    !FootprintAtmosphere.Finite(upForce) || !FootprintAtmosphere.Finite(sideForce)) return false;
                coefficients = new FootprintAero(drag, upForce, sideForce, true);
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[RetroLanding] Touchdown aero sample failed: " + ex.Message);
                return false;
            }
        }

        private static bool SamePose(Capture capture, Quaternion rotation, double pitchAoa) =>
            capture.PitchAoa == pitchAoa && Quaternion.Angle(capture.Rotation, rotation) <= MaximumPoseChange;

        private void MeasureDrag(double now, Vector3d velocity, Vector3d up, Settings cfg)
        {
            if (samples > 0)
            {
                double elapsed = now - times[(nextSample + times.Length - 1) % times.Length];
                if (elapsed <= 0 || elapsed > 2.05 * cfg.SampleSeconds) samples = 0;
            }
            times[nextSample] = now;
            velocities[nextSample] = velocity;
            nextSample = (nextSample + 1) % times.Length;
            samples = Math.Min(times.Length, samples + 1);
            if (samples < times.Length) return;
            double sumTime = 0, sumTimeSquared = 0;
            Vector3d sumVelocity = Vector3d.zero, sumTimeVelocity = Vector3d.zero;
            for (int i = 0; i < times.Length; i++)
            {
                double t = times[i] - now;
                sumTime += t;
                sumTimeSquared += t * t;
                sumVelocity += velocities[i];
                sumTimeVelocity += t * velocities[i];
            }
            double meanTime = sumTime / times.Length;
            Vector3d accel = (sumTimeVelocity - meanTime * sumVelocity) / (sumTimeSquared - meanTime * sumTime) +
                cfg.Gravity * up;
            MeasuredDrag = Math.Max(0, -Vector3d.Dot(accel, sumVelocity.normalized));
        }

        private SampleAero Sampler(Vessel vessel, double altitude, Vector3d horizontal, Vector3d up,
                                  Transform frame, Quaternion rotation, Quaternion toCoastPose, Flight initial)
        {
            double mass = initial.Mass;
            double groundAltitude = altitude - initial.Height;
            double atmosphereDepth = vessel.mainBody.atmosphereDepth;
            double referenceAngle = Math.Atan2(initial.Vz, initial.Vx);
            Quaternion toCurrentPose = Quaternion.Inverse(toCoastPose);
            Quaternion toCapturedFrame = Quaternion.Inverse(rotation);
            return delegate(double height, double vx, double vz, out double ax, out double az)
            {
                ax = az = 0;
                double asl = groundAltitude + height;
                if (asl >= atmosphereDepth || vx * vx + vz * vz < 4) return true;
                // FAR evaluates the live hull geometry. Query the flow in
                // that pose and express the force in the retained trajectory.
                double angle = Math.Atan2(vz, vx) - referenceAngle;
                double cosine = Math.Cos(angle), sine = Math.Sin(angle);
                Vector3d airVelocity = (cosine * vx + sine * vz) * horizontal +
                    (cosine * vz - sine * vx) * up;
                Vector3 force, torque;
                Quaternion rotationDelta = frame.rotation * toCapturedFrame;
                MeasureForces(vessel, out force, out torque, rotationDelta * toCurrentPose * (Vector3)airVelocity, asl);
                Vector3d acceleration = (Vector3d)(toCoastPose * Quaternion.Inverse(rotationDelta) * force) / mass;
                double forward = Vector3d.Dot(acceleration, horizontal);
                double vertical = Vector3d.Dot(acceleration, up);
                ax = cosine * forward - sine * vertical;
                az = sine * forward + cosine * vertical;
                // Reject invalid forces and substantial thrust in the direction of travel.
                double work = Vector3d.Dot(acceleration, airVelocity);
                return !double.IsNaN(work) && !double.IsInfinity(work) && work <= airVelocity.magnitude;
            };
        }
    }
}
