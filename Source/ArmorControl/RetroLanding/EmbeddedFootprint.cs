using System;
using System.Threading;
using System.Threading.Tasks;
using ArmorOverhaul.ArmorControl.Protocol;
using LandingPoc;
using UnityEngine;

namespace KspRetroLanding
{
    public sealed partial class KspLanding
    {
        private struct FootprintOrigin
        {
            internal double Latitude, Longitude, Radius;
            internal double AlongNorth, AlongEast, CrossNorth, CrossEast;
        }

        private Task<TouchdownEstimate> footprintWork;
        private CancellationTokenSource footprintStop;
        private FootprintOrigin footprintOrigin;
        private RetroTouchdown? footprintResult;
        private Phase footprintPhase;
        private long footprintBank;
        private LandingTuning footprintTuning;
        private float nextFootprint, nextRetroAero, nextPitchAero, nextFootprintWaypoint;
        private double footprintPitch = double.NaN;
        private double[] footprintPressure;
        private FootprintAero footprintRetroAero, footprintPitchAero;
        private FinePrint.Waypoint footprintWaypoint;
        private CelestialBody footprintBody;
        private bool footprintFailed;

        private void CancelFootprintWork()
        {
            if (footprintWork == null) return;
            Task<TouchdownEstimate> work = footprintWork;
            CancellationTokenSource stop = footprintStop;
            footprintWork = null;
            footprintStop = null;
            stop.Cancel();
            work.ContinueWith(completed =>
            {
                _ = completed.Exception;
                stop.Dispose();
            }, TaskScheduler.Default);
        }

        private void ResetFootprint()
        {
            CancelFootprintWork();
            footprintResult = null;
            footprintPressure = null;
            footprintPitch = double.NaN;
            footprintRetroAero = footprintPitchAero = default;
            footprintWaypoint = null;
            footprintBody = null;
            footprintFailed = false;
            nextFootprint = nextRetroAero = nextPitchAero = nextFootprintWaypoint = 0;
        }

        private RetroTouchdown? ReadFootprint(Command command, Flight flight)
        {
            if (TryTargetedTouchdown(command, out RetroTouchdown targetedEstimate)) return targetedEstimate;
            if (footprintFailed) return new RetroTouchdown { Status = "no_plan" };
            try { return CaptureFootprint(command, flight); }
            catch (Exception failure)
            {
                ResetFootprint();
                footprintFailed = true;
                Debug.LogWarning("[RetroLanding] Touchdown estimate unavailable: " + failure.Message);
                return new RetroTouchdown { Status = "no_plan" };
            }
        }

        private string FootprintTrace()
        {
            RetroTouchdown? estimate = embeddedFlight?.Touchdown;
            bool predicted = estimate.HasValue && estimate.Value.HasPoint;
            var point = estimate.GetValueOrDefault();
            return string.Format(Invariant, ",{0:F8},{1:F8},{2},{3},{4},{5},{6},{7},{8},{9},{10},{11}",
                vessel.latitude, vessel.longitude, point.Status ?? "",
                predicted ? point.Latitude.ToString("F8", Invariant) : "",
                predicted ? point.Longitude.ToString("F8", Invariant) : "",
                predicted && FootprintAtmosphere.Finite(point.MissDistance) ? point.MissDistance.ToString("F2", Invariant) : "",
                predicted ? point.Eta.ToString("F2", Invariant) : "",
                predicted ? point.EntrySeconds.ToString("F2", Invariant) : "",
                predicted ? point.LandingSeconds.ToString("F2", Invariant) : "",
                predicted && FootprintAtmosphere.Finite(point.MissBearing) ? point.MissBearing.ToString("F2", Invariant) : "",
                predicted && FootprintAtmosphere.Finite(point.MissEast) ? point.MissEast.ToString("F2", Invariant) : "",
                predicted && FootprintAtmosphere.Finite(point.MissNorth) ? point.MissNorth.ToString("F2", Invariant) : "");
        }

        private RetroTouchdown? CaptureFootprint(Command command, Flight flight)
        {
            if (flight.Contact || flight.SolidBurnActive || command.Phase == Phase.Yolo)
            {
                CancelFootprintWork();
                footprintResult = null;
                nextFootprint = 0;
                return null;
            }

            CelestialBody body = vessel.mainBody;
            if (footprintBody != body)
            {
                ResetFootprint();
                footprintBody = body;
            }
            float now = Time.realtimeSinceStartup;
            LandingTuning tuning = Tuning;
            if (command.Phase != footprintPhase || command.Engines != footprintBank ||
                !tuning.SameFlightSettings(footprintTuning))
            {
                CancelFootprintWork();
                footprintResult = null;
                footprintPhase = command.Phase;
                footprintBank = command.Engines;
                footprintTuning = tuning;
                nextFootprint = 0;
            }

            Vector3d up = (vessel.CoMD - body.position).normalized;
            Vector3d velocity = vessel.srf_velocity;
            if (body.atmosphere)
                SampleFootprintAero(body, flight, velocity, up, now);

            bool updateDeviation = false;
            if (footprintWork != null && footprintWork.IsCompleted)
            {
                updateDeviation = true;
                Task<TouchdownEstimate> finished = footprintWork;
                footprintStop.Dispose();
                footprintStop = null;
                footprintWork = null;
                if (finished.Status == TaskStatus.RanToCompletion)
                    footprintResult = FootprintReadback(finished.Result, footprintOrigin);
                else
                {
                    _ = finished.Exception;
                    footprintResult = new RetroTouchdown { Status = "no_plan" };
                }
            }
            if (footprintWork == null && now >= nextFootprint)
            {
                nextFootprint = now + 1;
                bool pitchAvailable = cfg.CoastPitchAoaDegrees == 0 || footprintPitchAero.Valid;
                var atmosphere = new FootprintAtmosphere(body.atmosphere ? body.atmosphereDepth : 0, footprintPressure,
                    footprintRetroAero, cfg.CoastPitchAoaDegrees == 0 ? footprintRetroAero : footprintPitchAero, pitchAvailable);
                footprintOrigin = FootprintFrame(body, up, velocity);
                // Match the footprint frame to actual surface motion even
                // when terminal guidance is tracking a terrain-avoidance velocity.
                Vector3d lateral = velocity - Vector3d.Dot(velocity, up) * up;
                if (lateral.sqrMagnitude > 0.0025) flight.LateralThrustAcceleration *= Vector3d.Dot(horizontal, lateral.normalized);
                var simulate = controller.CaptureFootprint(flight, atmosphere, body.Radius);
                footprintStop = new CancellationTokenSource();
                CancellationToken token = footprintStop.Token;
                footprintWork = Task.Run(() => simulate(token), token);
            }

            if (now >= nextFootprintWaypoint)
            {
                nextFootprintWaypoint = now + 1;
                footprintWaypoint = FindFootprintWaypoint(body);
                updateDeviation = true;
            }
            if (!footprintResult.HasValue) return new RetroTouchdown { Status = "calculating" };
            if (updateDeviation)
            {
                RetroTouchdown value = footprintResult.Value;
                value.Waypoint = targeted?.Name ?? footprintWaypoint?.name;
                value.MissDistance = value.MissBearing = value.MissEast = value.MissNorth = double.NaN;
                if (value.HasPoint && ((targeted != null && targeted.Body == body) || (targeted == null && footprintWaypoint != null)))
                    FootprintGeometry.Deviation(targeted?.Latitude ?? footprintWaypoint.latitude,
                        targeted?.Longitude ?? footprintWaypoint.longitude, value.Latitude, value.Longitude, body.Radius,
                        out value.MissDistance, out value.MissBearing, out value.MissEast, out value.MissNorth);
                footprintResult = value;
            }
            return footprintResult;
        }

        private void SampleFootprintAero(CelestialBody body, Flight flight, Vector3d velocity, Vector3d up, float now)
        {
            double depth = body.atmosphereDepth;
            if (depth <= 0) return;
            ReadFootprintPressure(body);

            double pitch = cfg.CoastPitchAoaDegrees;
            if (pitch != footprintPitch)
            {
                footprintPitch = pitch;
                footprintPitchAero = default;
                nextPitchAero = 0;
            }
            double speed = velocity.magnitude;
            if (speed < 2) return;
            bool sampleRetro = now >= nextRetroAero;
            bool samplePitch = pitch != 0 && now >= nextPitchAero && cfg.EntryBurnAltitude > 0;
            if (farCoast.CanSampleFootprint && (sampleRetro || samplePitch))
            {
                bool usePitch = samplePitch && (footprintRetroAero.Valid || !sampleRetro) &&
                    (!footprintPitchAero.Valid || nextPitchAero <= nextRetroAero);
                double altitude = Math.Min(0.85 * depth, Math.Max(0, flight.SeaLevelAltitude));
                if (usePitch) altitude = Math.Min(altitude, 0.85 * cfg.EntryBurnAltitude);
                AttitudeVector retro = AttitudeCoordinates(-velocity.normalized);
                AttitudeVector localUp = AttitudeCoordinates(up);
                Vector3d pointing = WorldDirection(AttitudeGuidance.CoastPointing(retro, localUp, LocalNorth(up),
                    usePitch ? pitch : 0));
                if (farCoast.TryFootprintAero(vessel, frame, velocity, up, pointing, altitude, body.GetPressure(altitude),
                    out FootprintAero coefficients))
                {
                    if (usePitch)
                    {
                        footprintPitchAero = coefficients;
                        nextPitchAero = now + 2;
                    }
                    else
                    {
                        footprintRetroAero = coefficients;
                        nextRetroAero = now + 2;
                    }
                }
            }
            // Stock or unavailable FAR still has a measured coast-drag estimate.
            // This cannot predict the effect of an unsampled pitch setting.
            if (!footprintRetroAero.Far && flight.CoastDragRate > 0 && flight.SeaLevelAltitude < depth)
            {
                double pressure = body.GetPressure(flight.SeaLevelAltitude);
                if (pressure > 1e-8)
                    footprintRetroAero = new FootprintAero(flight.CoastDragRate * flight.Mass / (pressure * speed), 0, 0, false);
            }
        }

        private double[] ReadFootprintPressure(CelestialBody body)
        {
            if (footprintPressure != null) return footprintPressure;
            footprintPressure = new double[129];
            for (int i = 0; i < footprintPressure.Length; i++)
                footprintPressure[i] = Math.Max(0, body.GetPressure(body.atmosphereDepth * i / (footprintPressure.Length - 1)));
            return footprintPressure;
        }

        private FootprintOrigin FootprintFrame(CelestialBody body, Vector3d up, Vector3d velocity)
        {
            double latitude = body.GetLatitude(vessel.CoMD), longitude = body.GetLongitude(vessel.CoMD);
            var north = WorldDirection(LocalNorth(up));
            Vector3d eastward = body.GetWorldSurfacePosition(Math.Max(-89.99, Math.Min(89.99, latitude)), longitude + 0.01, 0) -
                                body.GetWorldSurfacePosition(Math.Max(-89.99, Math.Min(89.99, latitude)), longitude - 0.01, 0);
            var east = (eastward - up * Vector3d.Dot(eastward, up)).normalized;
            Vector3d lateral = velocity - Vector3d.Dot(velocity, up) * up;
            Vector3d along = lateral.sqrMagnitude > 0.0025 ? lateral.normalized : north;
            Vector3d cross = Vector3d.Cross(up, along).normalized;
            return new FootprintOrigin { Latitude = latitude, Longitude = longitude, Radius = body.Radius,
                AlongNorth = Vector3d.Dot(along, north), AlongEast = Vector3d.Dot(along, east),
                CrossNorth = Vector3d.Dot(cross, north), CrossEast = Vector3d.Dot(cross, east) };
        }

        private static RetroTouchdown FootprintReadback(TouchdownEstimate estimate, FootprintOrigin origin)
        {
            var value = new RetroTouchdown
            {
                Status = estimate.Status, HasPoint = estimate.HasPoint, UsesFar = estimate.UsesFar,
                Atmospheric = estimate.Atmospheric,
                Eta = estimate.Seconds, EntrySeconds = estimate.EntrySeconds, LandingSeconds = estimate.LandingSeconds
            };
            if (estimate.HasPoint)
            {
                FootprintGeometry.Destination(origin.Latitude, origin.Longitude,
                    origin.AlongNorth * estimate.Along + origin.CrossNorth * estimate.Cross,
                    origin.AlongEast * estimate.Along + origin.CrossEast * estimate.Cross, origin.Radius,
                    out value.Latitude, out value.Longitude);
            }
            return value;
        }

        private static FinePrint.Waypoint FindFootprintWaypoint(CelestialBody body)
        {
            NavWaypoint navigation = NavWaypoint.fetch;
            var manager = FinePrint.WaypointManager.Instance();
            if (navigation == null || !navigation.IsActive || !ReferenceEquals(navigation.Body, body) || manager == null)
                return null;
            foreach (FinePrint.Waypoint point in manager.Waypoints)
                if (point != null && ReferenceEquals(point.celestialBody, body) && navigation.IsUsing(point) &&
                    FootprintAtmosphere.Finite(point.latitude) && Math.Abs(point.latitude) <= 90 &&
                    FootprintAtmosphere.Finite(point.longitude))
                    return point;
            return null;
        }
    }
}
