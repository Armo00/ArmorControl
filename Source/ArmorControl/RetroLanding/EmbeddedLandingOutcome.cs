using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ArmorOverhaul.ArmorControl.Protocol;
using LandingPoc;
using UnityEngine;

namespace KspRetroLanding
{
    public sealed partial class KspLanding
    {
        private sealed class LandingApproach
        {
            internal double Ut, TargetLatitude, TargetLongitude;
            internal FootprintOrigin Origin;
            internal Task<(TouchdownEstimate Unpowered, TouchdownEstimate Powered)> Work;
            internal CancellationTokenSource Stop;
            internal string Status;
        }

        private RetroLandingResult lastLanding;
        private Guid lastLandingVessel;
        private LandingApproach landingApproach;
        private string landingOutcomePath;
        private StreamWriter landingOutcomeLog;
        private double nextSettlingLog;

        internal RetroLandingResult LastLandingFor(Vessel active) =>
            active != null && lastLandingVessel == active.id ? lastLanding : null;

        private void DrawLastLanding(Vessel active)
        {
            var result = LastLandingFor(active);
            if (result == null) return;
            if (!FootprintAtmosphere.Finite(result.MissDistance))
            {
                GUILayout.Label(string.Format(Invariant, "Actual touchdown {0:F5}°, {1:F5}°",
                    result.Latitude, result.Longitude), panelLabel);
                return;
            }
            GUILayout.Label(string.Format(Invariant, "Actual touchdown · {0}\n{1:F2} km | {2} {3:F2} km | {4} {5:F2} km",
                result.Waypoint, result.MissDistance / 1000, result.MissEast < 0 ? "West" : "East", Math.Abs(result.MissEast) / 1000,
                result.MissNorth < 0 ? "South" : "North", Math.Abs(result.MissNorth) / 1000), panelLabel);
        }

        private void StartLandingOutcome(string directory, string stamp)
        {
            landingApproach = null;
            landingOutcomePath = Path.Combine(directory, "flight-touchdown-" + stamp + ".csv");
            nextSettlingLog = 0;
            if (vessel != null && lastLandingVessel == vessel.id) lastLanding = null;
        }

        private void RecordLandingIgnition(double now, Flight flight)
        {
            if (landingApproach != null || targeted == null || targeted.Body != vessel.mainBody) return;
            var body = vessel.mainBody;
            Vector3d up = (vessel.CoMD - body.position).normalized;
            Vector3d velocity = vessel.srf_velocity;
            var approach = landingApproach = new LandingApproach
            {
                Ut = now, TargetLatitude = targeted.Latitude, TargetLongitude = targeted.Longitude,
                Status = "waiting_aero"
            };
            try
            {
                approach.Origin = FootprintFrame(body, up, velocity);
                FootprintAero retro = targeted.Samples[0];
                if (!retro.Valid && footprintBody == body) retro = footprintRetroAero;
                if (!body.atmosphere || retro.Valid)
                {
                    var atmosphere = new FootprintAtmosphere(body.atmosphere ? body.atmosphereDepth : 0,
                        body.atmosphere ? ReadFootprintPressure(body) : null, retro, retro, true);
                    Vector3d lateral = velocity - Vector3d.Dot(velocity, up) * up;
                    if (lateral.sqrMagnitude > 0.0025)
                        flight.LateralThrustAcceleration *= Vector3d.Dot(horizontal, lateral.normalized);
                    var coast = controller.CaptureFootprint(flight, atmosphere, body.Radius, unpowered: true);
                    var powered = controller.CaptureFootprint(flight, atmosphere, body.Radius);
                    approach.Stop = new CancellationTokenSource();
                    CancellationToken token = approach.Stop.Token;
                    approach.Work = Task.Run(() => (coast(token), powered(token)), token);
                    approach.Status = retro.Far ? "far" : body.atmosphere ? "measured" : "vacuum";
                }
            }
            catch (Exception error)
            {
                approach.Status = "forecast_error";
                Debug.LogWarning("[RetroLanding] Ignition forecasts unavailable: " + error.Message);
            }
            WriteLandingOutcome("ignition", now);
        }

        private void PollLandingApproach(double now)
        {
            var approach = landingApproach;
            if (approach?.Work == null || !approach.Work.IsCompleted) return;
            var work = approach.Work;
            approach.Work = null;
            approach.Stop.Dispose();
            approach.Stop = null;
            if (work.Status != TaskStatus.RanToCompletion)
            {
                _ = work.Exception;
                approach.Status = "forecast_error";
                WriteLandingOutcome("forecasts", now);
                return;
            }
            WriteLandingOutcome("forecasts", now, FootprintReadback(work.Result.Unpowered, approach.Origin),
                FootprintReadback(work.Result.Powered, approach.Origin));
        }

        private void RecordTouchdown(double now)
        {
            lastLandingVessel = vessel.id;
            var result = lastLanding = new RetroLandingResult
            {
                Ut = now, Latitude = vessel.latitude, Longitude = vessel.longitude,
                MissDistance = double.NaN, MissEast = double.NaN, MissNorth = double.NaN,
                Settling = vessel.Splashed ? "splash" : supportWheels.Count > 0 ? "settling" : "contact"
            };
            if (targeted != null && targeted.Body == vessel.mainBody)
            {
                result.Waypoint = targeted.Name;
                FootprintGeometry.Deviation(targeted.Latitude, targeted.Longitude, result.Latitude, result.Longitude,
                    vessel.mainBody.Radius, out result.MissDistance, out _, out result.MissEast, out result.MissNorth);
            }
            WriteLandingOutcome("touchdown", now);
        }

        private void SetLandingOutcome(string outcome, double now)
        {
            if (LastLandingFor(vessel) == null) return;
            lastLanding.Settling = outcome;
            WriteLandingOutcome("settling_result", now);
        }

        private void WriteLandingOutcome(string eventName, double now, RetroTouchdown unpowered = default, RetroTouchdown powered = default)
        {
            if (landingOutcomePath == null || vessel == null) return;
            try
            {
                if (landingOutcomeLog == null)
                {
                    landingOutcomeLog = File.CreateText(landingOutcomePath);
                    landingOutcomeLog.WriteLine("ut,event,situation,landed,splashed,settling,surface_speed_mps,vertical_speed_mps,angular_speed_deg_s,roll_rate_deg_s,tilt_deg,supports,grounded_part_ids," +
                        "ignition_ut,ignition_lat_deg,ignition_lon_deg,target_lat_deg,target_lon_deg,approach_east,approach_north,aero_source," +
                        "unpowered_status,unpowered_lat_deg,unpowered_lon_deg,unpowered_along_m,powered_status,powered_lat_deg,powered_lon_deg,powered_along_m," +
                        "contact_ut,contact_lat_deg,contact_lon_deg,actual_miss_m,actual_east_m,actual_north_m,actual_along_m");
                }
                var grounded = supportWheels.FindAll(wheel => wheel != null && wheel.isGrounded);
                var result = LastLandingFor(vessel);
                var approach = landingApproach;
                var rb = vessel.rootPart?.rb;
                Vector3d up = (vessel.CoMD - vessel.mainBody.position).normalized;
                landingOutcomeLog.WriteLine(string.Join(",",
                    Number(now), eventName, vessel.situation.ToString(), vessel.Landed.ToString(), vessel.Splashed.ToString(), result?.Settling ?? "",
                    Number(vessel.srf_velocity.magnitude), Number(Vector3d.Dot(vessel.srf_velocity, up)),
                    Number(rb == null ? double.NaN : rb.angularVelocity.magnitude / AttitudeGuidance.Degree),
                    Number(rb == null ? double.NaN : Vector3.Dot(rb.angularVelocity, frame.up) / AttitudeGuidance.Degree),
                    Number(Vector3.Angle(frame.up, (Vector3)up)), grounded.Count.ToString(Invariant),
                    string.Join("|", grounded.ConvertAll(wheel => wheel.part.flightID.ToString(Invariant))),
                    Number(approach?.Ut), Number(approach?.Origin.Latitude), Number(approach?.Origin.Longitude),
                    Number(approach?.TargetLatitude), Number(approach?.TargetLongitude), Number(approach?.Origin.AlongEast),
                    Number(approach?.Origin.AlongNorth), approach?.Status ?? "", unpowered.Status ?? "",
                    Number(unpowered.HasPoint ? unpowered.Latitude : (double?)null), Number(unpowered.HasPoint ? unpowered.Longitude : (double?)null),
                    Number(unpowered.HasPoint ? AlongOffset(approach, unpowered.Latitude, unpowered.Longitude) : double.NaN), powered.Status ?? "",
                    Number(powered.HasPoint ? powered.Latitude : (double?)null), Number(powered.HasPoint ? powered.Longitude : (double?)null),
                    Number(powered.HasPoint ? AlongOffset(approach, powered.Latitude, powered.Longitude) : double.NaN),
                    Number(result?.Ut), Number(result?.Latitude), Number(result?.Longitude), Number(result?.MissDistance),
                    Number(result?.MissEast), Number(result?.MissNorth), Number(result == null ? double.NaN : AlongOffset(approach, result.Latitude, result.Longitude))));
                landingOutcomeLog.Flush();
            }
            catch (Exception error)
            {
                landingOutcomePath = null;
                Debug.LogWarning("[RetroLanding] Could not write touchdown diagnostics: " + error.Message);
            }

            string Number(double? value) => value.HasValue && FootprintAtmosphere.Finite(value.Value)
                ? value.Value.ToString("F8", Invariant) : "";
        }

        private static double AlongOffset(LandingApproach approach, double latitude, double longitude)
        {
            if (approach == null || approach.Origin.Radius <= 0) return double.NaN;
            FootprintGeometry.Deviation(approach.TargetLatitude, approach.TargetLongitude, latitude, longitude,
                approach.Origin.Radius, out _, out _, out double east, out double north);
            return east * approach.Origin.AlongEast + north * approach.Origin.AlongNorth;
        }

        private void CloseLandingOutcome(bool onContact)
        {
            if (landingOutcomePath != null && vessel != null)
            {
                double now = Planetarium.GetUniversalTime();
                PollLandingApproach(now);
                if (!onContact && LastLandingFor(vessel)?.Settling == "settling") SetLandingOutcome("interrupted", now);
                if (landingApproach?.Work != null) WriteLandingOutcome("forecasts_pending", now);
            }
            var approach = landingApproach;
            if (approach?.Stop != null)
            {
                approach.Stop.Cancel();
                if (approach.Work == null) approach.Stop.Dispose();
                else approach.Work.ContinueWith(work =>
                {
                    _ = work.Exception;
                    approach.Stop.Dispose();
                }, TaskScheduler.Default);
            }
            landingApproach = null;
            landingOutcomePath = null;
            try { landingOutcomeLog?.Dispose(); }
            catch (IOException error)
            {
                Debug.LogWarning("[RetroLanding] Could not close touchdown diagnostics: " + error.Message);
            }
            landingOutcomeLog = null;
        }
    }
}
