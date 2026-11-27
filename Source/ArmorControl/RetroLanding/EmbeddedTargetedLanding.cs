using System;
using System.Threading;
using System.Threading.Tasks;
using ArmorOverhaul.ArmorControl.Protocol;
using LandingPoc;
using UnityEngine;

namespace KspRetroLanding
{
    internal enum LandingTargetSource { Waypoint, LandedVessel }

    public sealed partial class KspLanding
    {
        private enum TargetedLeg { Planning, Boostback, Cutoff, Recovery }
        private enum TargetedSearch { None, Boostback, Cutoff, Glide }

        private sealed class TargetedRecovery
        {
            internal CelestialBody Body;
            internal LandingTargetSource Source;
            internal double EastOffset, NorthOffset;
            internal string Name;
            internal double Latitude, Longitude, Height;
            internal TargetedLeg Leg;
            internal TargetedSearch Search;
            internal Task<BoostbackPreviewResult> Work;
            internal CancellationTokenSource Cancellation;
            internal readonly FootprintAero[] Samples = new FootprintAero[6];
            internal readonly float[] Refresh = new float[6];
            internal LandingTuning Tuning;
            internal double CapturedAt, Delivered, DeliveredMagnitude, BoostDeadline;
            internal double NextFallbackAttempt;
            internal double TrimMinimum, TrimMeasurement = double.NaN, ReferencePositionError, ReferenceVelocityError;
            internal double CorrectionAt, LastSearchMilliseconds;
            internal double FirstActivationAt = double.NaN, FirstActivationAngle = double.NaN;
            internal double FirstThrustAt = double.NaN, FirstBodyAngle = double.NaN, FirstThrustAngle = double.NaN;
            internal int LastSearchTraces;
            internal long BoostCommandMask;
            internal Vector3d LastBoostDirection;
            internal AttitudeVector TraceThrust, TraceDirection;
            internal string CutoffReason;
            internal BoostbackCorrectionResult Correction;
            internal BoostbackCutoffTrend CutoffTrend;
            internal BoostbackStage BoostStage;
            internal float NextSearch, GlideSamplesDeadline, AeroDeadline;
            internal TargetedSearch AeroRequest;
            internal FarSampleStatus AeroSampleStatus;
            internal int AeroBudgetDenied, AeroVoxelPending, AeroInvalid;
            internal float NextBoostbackRetry = float.PositiveInfinity;
            internal double LastBoostbackPlanAt = double.NaN;
            internal string LastBoostbackRejection;
            internal BoostbackPreviewResult LastBoostbackPlan;
            internal bool StandardFallback, WaitingForEntry, ObservedAboveEntry;
            internal bool BoostUllageRequested, BoostIgnitionCommitted, UseJointPlan;
            internal FootprintAero BoostAero;
            internal float RefreshBoostAero;
            internal string Warning;
            internal BoostbackApproach Boost, Glide, Cutoff;
            internal BoostbackFallback Fallback;
            internal BoostbackPrediction Prediction;
        }

        private TargetedRecovery targeted;
        private double TargetedEntryTilt => targeted?.Leg == TargetedLeg.Recovery &&
            targeted.UseJointPlan && !targeted.StandardFallback ? targeted.Boost.BurnDirections.EntryTilt : 0;

        internal static bool HasTargetedWaypoint(Vessel active) => WaypointFor(active) != null;

        private static FinePrint.Waypoint WaypointFor(Vessel active)
        {
            if (active == null || active.mainBody == null) return null;
            try { return FindFootprintWaypoint(active.mainBody); }
            catch { return null; }
        }

        internal static Vessel LandedTargetFor(Vessel active)
        {
            if (active == null || active.mainBody == null) return null;
            ITargetable selection = FlightGlobals.fetch == null ? null : FlightGlobals.fetch.VesselTarget;
            Vessel target = selection as Vessel;
            if (target == null && selection is ModuleDockingNode port && port.part != null)
                target = port.part.vessel;
            return target != null && target.id != active.id && target.mainBody == active.mainBody &&
                target.Landed && !target.Splashed && FootprintAtmosphere.Finite(target.latitude) &&
                Math.Abs(target.latitude) <= 90 && FootprintAtmosphere.Finite(target.longitude) ? target : null;
        }

        internal bool EngageTargetedEmbedded(LandingTargetSource source = LandingTargetSource.Waypoint,
            bool fromWindow = false, string expectedTargetVesselId = null)
        {
            Vessel active = FlightGlobals.ActiveVessel;
            if (!IsIdle || active == null || active.mainBody == null) return false;
            CelestialBody body = active.mainBody;
            double latitude, longitude, eastOffset = 0, northOffset = 0;
            string name;
            if (source == LandingTargetSource.LandedVessel)
            {
                Vessel anchor = LandedTargetFor(active);
                if (anchor == null || (!string.IsNullOrEmpty(expectedTargetVesselId) &&
                    !string.Equals(expectedTargetVesselId, anchor.id.ToString("N"), StringComparison.OrdinalIgnoreCase)))
                    return false;
                name = string.IsNullOrEmpty(anchor.vesselName) ? "Landed vessel" : anchor.vesselName;
                eastOffset = cfg.VesselTargetEastOffset;
                northOffset = cfg.VesselTargetNorthOffset;
                FootprintGeometry.Destination(anchor.latitude, anchor.longitude, northOffset, eastOffset, body.Radius,
                    out latitude, out longitude);
            }
            else
            {
                FinePrint.Waypoint waypoint = WaypointFor(active);
                if (waypoint == null) return false;
                name = string.IsNullOrEmpty(waypoint.name) ? "Navigation waypoint" : waypoint.name;
                latitude = waypoint.latitude;
                longitude = waypoint.longitude;
            }
            if (!FootprintAtmosphere.Finite(latitude) || !FootprintAtmosphere.Finite(longitude)) return false;
            double ground = body.pqsController == null ? 0 :
                body.pqsController.GetSurfaceHeight(body.GetRelSurfaceNVector(latitude, longitude)) - body.Radius;
            if (!FootprintAtmosphere.Finite(ground)) return false;
            StopTargeted();
            targeted = new TargetedRecovery
            {
                Body = body, Name = name, Source = source, EastOffset = eastOffset, NorthOffset = northOffset,
                Latitude = latitude, Longitude = longitude, Height = body.ocean ? Math.Max(0, ground) : ground,
                WaitingForEntry = active.altitude >= cfg.EntryBurnAltitude,
                Tuning = Tuning, Leg = TargetedLeg.Planning
            };
            embeddedFlight = null;
            StartMode(Mode.Control, fromWindow);
            if (IsIdle)
            {
                StopTargeted();
                return false;
            }
            return true;
        }

        private static void CancelTargetedSearch(TargetedRecovery active)
        {
            if (active.Work == null) return;
            var pending = active.Work;
            var cancellation = active.Cancellation;
            active.Work = null;
            active.Cancellation = null;
            active.Search = TargetedSearch.None;
            cancellation.Cancel();
            pending.ContinueWith(finished =>
            {
                _ = finished.Exception;
                cancellation.Dispose();
            }, TaskScheduler.Default);
        }

        private void StopTargeted()
        {
            if (targeted == null) return;
            CancelTargetedSearch(targeted);
            targeted = null;
        }

        private void PauseTargeted()
        {
            if (targeted == null) return;
            CancelTargetedSearch(targeted);
            if (targeted.Leg == TargetedLeg.Boostback)
                AbortBoostback(targeted, "warp_interrupted_boostback", "Warp interrupted Boostback.");
            targeted.NextSearch = 0;
        }

        private static void FallBackTargeted(TargetedRecovery active, string reason)
        {
            CancelTargetedSearch(active);
            if (active.Leg == TargetedLeg.Boostback)
                active.Leg = TargetedLeg.Cutoff;
            else if (active.Leg != TargetedLeg.Cutoff)
                active.Leg = TargetedLeg.Recovery;
            if (active.Leg == TargetedLeg.Cutoff && active.CutoffReason == null)
                active.CutoffReason = "safety_fallback";
            active.StandardFallback = true;
            active.NextBoostbackRetry = float.PositiveInfinity;
            active.Warning = reason;
            active.Glide = null;
        }

        private static void RetryTargetedGlide(TargetedRecovery active, string warning)
        {
            if (active.Leg == TargetedLeg.Planning) active.Leg = TargetedLeg.Recovery;
            active.Warning = warning;
            active.NextSearch = Time.realtimeSinceStartup + 1;
        }

        private static void DeferBoostbackPlan(TargetedRecovery active, string reason, string warning)
        {
            active.LastBoostbackRejection = reason;
            active.Leg = TargetedLeg.Recovery;
            active.Warning = warning;
            // Leave a background slot for the glide forecast between failed plans.
            active.NextBoostbackRetry = Time.realtimeSinceStartup + 2;
        }

        private static void TargetedSearchUnavailable(TargetedRecovery active, TargetedSearch search,
            double now, string reason, string warning)
        {
            if (search == TargetedSearch.Cutoff) return;
            if (search == TargetedSearch.Boostback)
            {
                active.LastBoostbackPlan = null;
                active.LastBoostbackPlanAt = now;
                if (active.Leg == TargetedLeg.Boostback)
                {
                    active.LastBoostbackRejection = reason;
                    active.NextSearch = Time.realtimeSinceStartup + 0.5f;
                }
                else DeferBoostbackPlan(active, reason, warning);
            }
            else RetryTargetedGlide(active, warning);
        }

        private static void AbortBoostback(TargetedRecovery active, string reason, string warning)
        {
            BeginBoostbackCutoff(active, reason);
            active.Warning = warning;
        }

        private void RequestBoostbackUllage(TargetedRecovery active)
        {
            if (active.BoostUllageRequested) return;
            active.BoostUllageRequested = true;
            if (!RcsEnabled && engines.Exists(engine => engine.NeedsUllage))
                vessel.ActionGroups.SetGroup(KSPActionGroup.RCS, true);
        }

        internal RetroTargetedSnapshot TargetedReadback => targeted == null ? null : new RetroTargetedSnapshot
        {
            Waypoint = targeted.Name,
            VesselTarget = targeted.Source == LandingTargetSource.LandedVessel,
            EastOffset = targeted.EastOffset, NorthOffset = targeted.NorthOffset,
            State = targeted.Leg.ToString(),
            Fallback = targeted.StandardFallback,
            Warning = targeted.Warning,
            Pitch = targeted.Glide?.PitchAoa ?? double.NaN,
            Bank = targeted.Glide?.GlideBank ?? double.NaN,
            BoostRemaining = targeted.Fallback?.Remaining ?? (targeted.Boost == null ? double.NaN :
                Math.Max(0, targeted.Boost.DeltaV - targeted.Delivered))
        };

        private bool TryTargetedTouchdown(Command command, out RetroTouchdown snapshot)
        {
            snapshot = default;
            var active = targeted;
            if (active == null || command.Phase == Phase.Landed) return false;
            BoostbackApproach estimate = null;
            if (active.Leg == TargetedLeg.Planning || active.Leg == TargetedLeg.Boostback || active.Leg == TargetedLeg.Cutoff)
            {
                if (active.Fallback != null && active.Cutoff == null)
                {
                    snapshot = new RetroTouchdown { Status = "boostback_coarse", Waypoint = active.Name };
                    return true;
                }
                estimate = active.Cutoff ?? active.Boost;
            }
            else if (command.Phase == Phase.Coast && !active.StandardFallback && !active.WaitingForEntry)
                estimate = active.Glide;
            if (estimate == null)
            {
                if (active.Leg != TargetedLeg.Planning) return false;
                snapshot = new RetroTouchdown { Status = "calculating", Waypoint = active.Name };
                return true;
            }
            snapshot = new RetroTouchdown { Status = estimate.Touchdown.Status, Waypoint = active.Name,
                HasPoint = estimate.Touchdown.HasPoint, UsesFar = estimate.Touchdown.UsesFar,
                Atmospheric = estimate.Touchdown.Atmospheric, Latitude = estimate.Lat, Longitude = estimate.Lon,
                MissDistance = estimate.MissDistance, MissEast = estimate.MissEast, MissNorth = estimate.MissNorth,
                MissBearing = double.NaN, Eta = estimate.TotalSeconds,
                EntrySeconds = estimate.Touchdown.EntrySeconds, LandingSeconds = estimate.Touchdown.LandingSeconds };
            return true;
        }

        private bool TryTargetedGlideAttitude(double altitude, bool ignitionBlocked, AttitudeVector retrograde, AttitudeVector up,
            AttitudeVector north, ref double aoa, out Vector3d direction, out AttitudeVector forward)
        {
            direction = default;
            forward = default;
            if (targeted == null || targeted.Body != vessel.mainBody || targeted.Leg != TargetedLeg.Recovery ||
                !UsesCoastAttitude(lastPhase, ignitionBlocked) || altitude >= cfg.EntryBurnAltitude ||
                targeted.WaitingForEntry || targeted.StandardFallback)
                return false;

            double bank = targeted.Glide?.GlideBank ?? 0;
            aoa = targeted.Glide?.PitchAoa ?? cfg.CoastPitchAoaDegrees;
            direction = WorldDirection(AttitudeGuidance.BankedCoastPointing(retrograde, up, north, bank, aoa, out forward));
            return true;
        }

        private Vector3d TargetedVelocity(Vector3d up, Vector3d lateral, GroundReading ground, double vz)
        {
            if (targeted == null || targeted.Body != vessel.mainBody || targeted.StandardFallback || targeted.Leg != TargetedLeg.Recovery ||
                cfg.IgnitionAltitudeBlocked(ground.Clearance) || (lastPhase != Phase.Main && lastPhase != Phase.Final) || vz >= 0)
                return Vector3d.zero;
            if (hasLandingSite)
            {
                FallBackTargeted(targeted, "Using a safer touchdown surface. Switching to standard landing.");
                return Vector3d.zero;
            }
            var target = vessel.mainBody.GetWorldSurfacePosition(targeted.Latitude, targeted.Longitude, targeted.Height);
            Vector3d displacement = target - vessel.CoMD;
            displacement -= up * Vector3d.Dot(displacement, up);
            double time = 2 * ground.Clearance / Math.Max(cfg.TouchSpeed, -vz + cfg.TouchSpeed);
            double demand = AttitudeGuidance.FinalLateralDemand(lateral.magnitude);
            double tilt = AttitudeGuidance.FinalTiltDegrees(demand, time) * AttitudeGuidance.Degree;
            double vertical = cfg.Gravity + Math.Max(0, vz * vz - cfg.TouchSpeed * cfg.TouchSpeed) / (2 * ground.Clearance);
            double thrust = 0;
            for (int i = 0; i < engines.Count; i++)
                if (lastMask > 0 && (lastMask & (1L << i)) != 0) thrust += engines[i].Max;
            double limit = Math.Min(cfg.MaxG * Settings.G0, thrust / vessel.GetTotalMass());
            double lateralAcceleration = Math.Min(cfg.Gravity * Math.Tan(tilt),
                Math.Sqrt(Math.Max(0, limit * limit - vertical * vertical)));
            AttitudeVector velocity = TargetedLandingGuidance.TargetVelocity(AttitudeCoordinates(displacement),
                AttitudeCoordinates(lateral), time, lateralAcceleration, Math.Max(0.35, cfg.TouchSpeed * cfg.SampleSeconds),
                out bool reachable);
            if (!reachable)
                FallBackTargeted(targeted, "Waypoint is outside the powered capture envelope. Switching to standard landing.");
            return WorldDirection(velocity);
        }

        private bool ReadTargetedCommand(Flight flight, double now, double elapsed, Vector3d up, Vector3d velocity,
            Vector3d thrust, out Command command, out Vector3d direction)
        {
            try
            {
                bool handled = TryTargetedBoostback(flight, now, elapsed, up, velocity, thrust, out command, out direction);
                if (handled && targeted.Boost != null)
                {
                    ObserveBoostbackCommand(targeted, command.Engines);
                    if (direction.sqrMagnitude > 0)
                    {
                        targeted.LastBoostDirection = direction;
                        TargetedBasis(up, out Vector3d east, out Vector3d north);
                        targeted.TraceDirection = new AttitudeVector(Vector3d.Dot(direction, east),
                            Vector3d.Dot(direction, north), Vector3d.Dot(direction, up));
                        targeted.TraceThrust = new AttitudeVector(Vector3d.Dot(thrust, east),
                            Vector3d.Dot(thrust, north), Vector3d.Dot(thrust, up));
                    }
                }
                return handled;
            }
            catch (Exception failure)
            {
                command = default;
                direction = default;
                if (targeted == null) throw;
                Debug.LogWarning("[RetroLanding] Targeted guidance failed: " + failure.Message);
                if (targeted.Leg == TargetedLeg.Recovery || targeted.Leg == TargetedLeg.Planning)
                {
                    TargetedSearch search = targeted.Leg == TargetedLeg.Planning ? TargetedSearch.Boostback : targeted.Search;
                    CancelTargetedSearch(targeted);
                    TargetedSearchUnavailable(targeted, search, now, "guidance_exception", "Targeted prediction failed. Retrying.");
                    return false;
                }
                AbortBoostback(targeted, "boostback_guidance_failed", "Boostback guidance failed. Checking glide after cutoff.");
                direction = up;
                command = new Command { Phase = Phase.Boostback, Reason = "Boostback cutoff, waiting for engines to shut down" };
                return true;
            }
        }

        private bool TryTargetedBoostback(Flight flight, double now, double elapsed, Vector3d up, Vector3d velocity,
            Vector3d thrust, out Command command, out Vector3d direction)
        {
            command = default;
            direction = default;
            var active = targeted;
            if (active == null || flight.Contact) return false;
            if (active.Leg == TargetedLeg.Boostback || active.Leg == TargetedLeg.Cutoff)
                ObserveBoostbackImpulse(active, flight, up, thrust, elapsed);
            if (active.Body != vessel.mainBody && !active.StandardFallback)
            {
                FallBackTargeted(active, "Target waypoint is on another body. Switching to standard landing.");
            }
            if (flight.SolidBurnActive)
            {
                CancelTargetedSearch(active);
                if (active.Leg == TargetedLeg.Boostback)
                    AbortBoostback(active, "solid_motor_burning", "An active solid motor interrupted Boostback.");
                return false;
            }

            LandingTuning tuning = Tuning;
            if (!active.Tuning.SameFlightSettings(tuning))
            {
                if (active.Leg == TargetedLeg.Boostback)
                    AbortBoostback(active, "settings_changed", "Landing settings changed during Boostback.");
                CancelTargetedSearch(active);
                Array.Clear(active.Samples, 0, active.Samples.Length);
                Array.Clear(active.Refresh, 0, active.Refresh.Length);
                active.Tuning = tuning;
                active.Glide = null;
                active.UseJointPlan = false;
                active.BoostAero = default;
                active.NextSearch = active.GlideSamplesDeadline = active.AeroDeadline = active.RefreshBoostAero = 0;
                active.AeroRequest = TargetedSearch.None;
            }

            ReadTargetedSearch(active, flight, now);
            if (active.Leg == TargetedLeg.Recovery)
            {
                if (!active.StandardFallback && UsesCoastAttitude(lastPhase, cfg.IgnitionAltitudeBlocked(flight.Height)) &&
                    (flight.Running | flight.Starting | flight.IssuedStarts) == 0)
                {
                    if (!TargetedLandingGuidance.MayStartBoostback(flight.Height, flight.Vz, cfg))
                        active.NextBoostbackRetry = float.PositiveInfinity;
                    bool retryBoostback = Time.realtimeSinceStartup >= active.NextBoostbackRetry &&
                        TargetedLandingGuidance.CanRetryBoostback(flight, lastPhase, cfg);
                    if (retryBoostback) RequestBoostbackUllage(active);
                    ScheduleTargetedSearch(active, retryBoostback ? TargetedSearch.Boostback : TargetedSearch.Glide,
                        flight, up, velocity, now);
                    if (retryBoostback) TryBeginFallbackBoostback(active, flight, up, velocity, now);
                }
                if (active.Leg == TargetedLeg.Recovery) return false;
            }

            controller.ObserveBoostbackAltitude(flight.SeaLevelAltitude);
            active.ObservedAboveEntry |= flight.SeaLevelAltitude > cfg.EntryBurnAltitude;
            bool mayStart = TargetedLandingGuidance.MayStartBoostback(flight.Height, flight.Vz, cfg);
            if (active.Leg == TargetedLeg.Planning && !mayStart)
            {
                CancelTargetedSearch(active);
                active.Leg = TargetedLeg.Recovery;
                active.NextSearch = 0;
                return false;
            }
            if (active.Leg == TargetedLeg.Planning)
            {
                if ((flight.Running | flight.Starting | flight.IssuedStarts) != 0)
                {
                    TargetedSearchUnavailable(active, TargetedSearch.Boostback, now, "thrust_already_active",
                        "Waiting for separation thrust to clear before retrying Boostback.");
                    return false;
                }
                RequestBoostbackUllage(active);
                ScheduleTargetedSearch(active, TargetedSearch.Boostback, flight, up, velocity, now);
                if (!TryBeginFallbackBoostback(active, flight, up, velocity, now))
                {
                    if (active.Leg == TargetedLeg.Recovery) return false;
                    direction = RoughBoostbackDirection(active, flight, up, velocity);
                    command = new Command { Phase = Phase.Boostback, PrepareBoostbackUllage = true,
                        Reason = "Planning Boostback, aligning and preparing ullage" };
                    return true;
                }
            }

            if (active.Leg == TargetedLeg.Boostback && !active.BoostIgnitionCommitted)
            {
                active.BoostIgnitionCommitted =
                    ((flight.Running | flight.IssuedStarts) & active.Boost.PrecisionEngines) != 0;
                if (!active.BoostIgnitionCommitted && !mayStart)
                    BeginBoostbackCutoff(active, "landing_window_before_ignition");
            }

            if (active.Leg == TargetedLeg.Boostback)
            {
                var correction = FreshBoostbackCorrection(active, now);
                var fallback = active.Fallback;
                bool returnOnly = active.Boost.ReturnOnly;
                bool coarse = fallback != null && !HasBoostbackPrediction(correction);
                if (fallback != null)
                {
                    UpdateFallbackEstimate(active, flight, up, velocity, now);
                    SetBoostbackPrediction(active, PredictionSource(correction, fallback.Source));
                }
                else if (returnOnly)
                    SetBoostbackPrediction(active, PredictionSource(correction, BoostbackPrediction.FarReturn));
                if (fallback != null || returnOnly)
                {
                    if (active.BoostCommandMask != 0 && active.Search == TargetedSearch.Boostback)
                    {
                        CancelTargetedSearch(active);
                        active.NextSearch = 0;
                    }
                }
                direction = BoostbackDirection(active, up);
                if (direction.sqrMagnitude == 0) direction = active.LastBoostDirection.sqrMagnitude > 0 ? active.LastBoostDirection : up;
                UpdateBoostbackReference(active, correction, flight, up, velocity, now);
                var estimate = correction?.CutNow;
                if (active.UseJointPlan && estimate != null && !estimate.Touchdown.EntryCompleted) estimate = null;
                bool finishVerifiedHold = estimate?.ReturnOnly == true && active.BoostStage == BoostbackStage.Fine &&
                    correction.Mask == active.BoostCommandMask && now < active.CorrectionAt + correction.HoldSeconds &&
                    VerifiedReleaseMatches(active, correction);

                string cutoff = coarse ? CoarseBoostbackCutoff(active) : null;
                if (active.BoostIgnitionCommitted)
                {
                    if (BoostbackImpulseRemaining(active) <= 0) cutoff = "impulse_limit";
                    else if (!coarse && !finishVerifiedHold) cutoff = TargetedLandingGuidance.BoostbackCutoffReason(active.DeliveredMagnitude,
                        fallback == null ? active.Boost.BoostThrustDeltaV : 0, estimate,
                        active.CorrectionAt, ref active.CutoffTrend);
                }
                if (cutoff != null) BeginBoostbackCutoff(active, cutoff);
                else if (active.BoostStage == BoostbackStage.Fine && correction?.AfterCorrection != null &&
                    now >= active.CorrectionAt + correction.HoldSeconds && VerifiedReleaseMatches(active, correction))
                    BeginBoostbackCutoff(active, "verified_hold_complete");
                else if (now > active.BoostDeadline)
                {
                    AbortBoostback(active, "boostback_timeout", "Boostback timed out. Checking glide after cutoff.");
                }
                else if (active.BoostStage == BoostbackStage.Fine && !active.CutoffTrend.EnteredWindow && correction != null &&
                    correction.Mask == active.Boost.PrecisionEngines && correction.Candidates >= 5 &&
                    correction.AfterCorrection == null && estimate?.Safe == true)
                {
                    AbortBoostback(active, "no_improving_correction", "No useful Boostback correction. Checking glide after cutoff.");
                }
                else if (TryBoostbackBank(active, flight, now, up, estimate, correction, ref direction, out command))
                {
                    if (command.Engines == 0 && (fallback != null || returnOnly && cfg.OptimizeRecoveryTrajectory))
                        ScheduleTargetedSearch(active, TargetedSearch.Boostback, flight, up, velocity, now);
                    else if (command.Engines != 0 && (fallback != null || returnOnly ||
                        (active.BoostStage == BoostbackStage.Full || active.BoostStage == BoostbackStage.Fine) &&
                        (flight.EstablishedThrust & command.Engines) == command.Engines) && active.Work == null &&
                        Time.realtimeSinceStartup >= active.NextSearch)
                    {
                        var live = CaptureBoostbackCorrection(active, flight, up, direction, command);
                        if (live != null) ScheduleTargetedSearch(active, TargetedSearch.Cutoff, flight, up, velocity, now, live);
                    }
                    return true;
                }
                else
                {
                    AbortBoostback(active, "thrust_limits_changed", "Boostback engines or max G changed. Checking glide after cutoff.");
                }
            }

            if ((flight.Running | flight.Starting | flight.IssuedStarts) == 0 && thrust.sqrMagnitude < 1)
            {
                active.Leg = TargetedLeg.Recovery;
                active.NextSearch = 0;
                return false;
            }
            direction = active.LastBoostDirection.sqrMagnitude > 0 ? active.LastBoostDirection :
                active.Boost == null ? up : BoostbackDirection(active, up);
            command = new Command { Phase = Phase.Boostback, Reason = "Boostback cutoff: " + active.CutoffReason };
            return true;
        }

        private bool TryBoostbackBank(TargetedRecovery active, Flight flight, double now, Vector3d up,
            BoostbackApproach estimate, BoostbackCorrectionResult correction, ref Vector3d direction, out Command command)
        {
            command = default;
            long precision = active.Boost.PrecisionEngines;
            var fallback = active.Fallback;
            bool coarse = fallback != null && !HasBoostbackPrediction(correction);
            double remaining = fallback?.Remaining ?? Math.Max(0, active.Boost.DeltaV - active.Delivered);
            if (!controller.TryBoostbackLimits(precision, flight, out double latestMinimum, out double trimMaximum))
                return false;
            if (active.TrimMeasurement != nextThrust)
            {
                active.TrimMinimum = latestMinimum;
                active.TrimMeasurement = nextThrust;
            }
            double angle = Vector3.Angle(frame.up, (Vector3)direction);
            var previous = active.BoostStage;
            double miss = TargetedLandingGuidance.CanGuideBoostback(estimate) ? estimate.MissDistance : double.PositiveInfinity;
            if (coarse) miss = fallback.Miss;
            var stage = TargetedLandingGuidance.BoostbackStageFor(previous, angle,
                precision != 0 && (flight.EstablishedThrust & precision) == precision,
                fallback != null && !coarse && estimate?.Safe == true ? double.PositiveInfinity : remaining, miss,
                trimMaximum / flight.Mass, cfg);
            if (coarse) stage = TargetedLandingGuidance.CoarseBoostbackStage(previous, stage, angle,
                fallback.SamplesFromSource >= 2, miss, fallback.ImpactSeconds, trimMaximum / flight.Mass, cfg);
            long mask = TargetedLandingGuidance.BoostbackMask(stage, precision, active.Boost.Engines);
            double minimum = active.TrimMinimum, maximum = trimMaximum;
            if (mask != 0 && mask != precision &&
                !controller.TryBoostbackLimits(mask, flight, out minimum, out maximum)) return false;
            if (previous != stage && stage == BoostbackStage.Fine)
            {
                CancelTargetedSearch(active);
                active.NextSearch = 0;
                active.Correction = correction = null;
                double budget = BoostbackImpulseRemaining(active);
                double nominal = Math.Max(remaining / (trimMaximum / flight.Mass), cfg.AttitudeResponseSeconds);
                double repairWindow = Math.Min(budget * flight.Mass / Math.Max(1e-3, minimum),
                    Math.Max(nominal, active.Boost.BurnSeconds) * TargetedLandingGuidance.BoostbackImpulseLimit);
                active.BoostDeadline = Math.Max(active.BoostDeadline, now + repairWindow + cfg.AttitudeResponseSeconds);
            }
            double thrust = stage == BoostbackStage.Fine ? coarse ?
                remaining * flight.Mass / TargetedLandingGuidance.CorrectionInterval(stage) : minimum : maximum;
            if (coarse && stage == BoostbackStage.Full && angle > TargetedLandingGuidance.FullBoostbackAngle)
                thrust = minimum;
            if (fallback != null && mask != 0)
            {
                if (now >= fallback.NextFuelCheck || mask != fallback.LastFuelBank)
                {
                    fallback.FuelLimit = active.DeliveredMagnitude + FallbackFuelLimit(active, flight, mask);
                    fallback.LastFuelBank = mask;
                    fallback.NextFuelCheck = now + 0.5;
                }
                double available = Math.Min(fallback.FuelLimit - active.DeliveredMagnitude, BoostbackImpulseRemaining(active));
                if (available < minimum / flight.Mass * cfg.SampleSeconds) return false;
                maximum = Math.Min(maximum, available * flight.Mass / cfg.SampleSeconds);
            }
            if (mask != 0 && correction?.AfterCorrection != null && correction.Mask == mask && ReferenceMatches(active) &&
                TargetedLandingGuidance.FollowBoostbackCorrection(stage, now - active.CorrectionAt, correction.HoldSeconds))
            {
                direction = BodyFixedBoostbackDirection(correction.Direction, up);
                if (stage == BoostbackStage.Fine) thrust = correction.Thrust;
            }
            double acceleration = mask == 0 ? 0 :
                TargetedLandingGuidance.BoundedBoostbackThrust(thrust, minimum, maximum) / flight.Mass;
            active.BoostStage = stage;
            command = new Command
            {
                Phase = Phase.Boostback, Engines = mask, X = acceleration, PrepareBoostbackUllage = true,
                BoostbackIgnitionAngle = stage == BoostbackStage.Initial ? cfg.BoostbackStartAoa :
                    Math.Min(cfg.BoostbackStartAoa, TargetedLandingGuidance.FullBoostbackAngle),
                Reason = stage == BoostbackStage.Aligning ? "Aligning for Boostback" :
                    stage == BoostbackStage.Initial ? "Boostback inner bank" :
                    stage == BoostbackStage.Fine ? "Boostback fine turn" :
                    active.UseJointPlan ? "Optimized Boostback" : "Targeted Boostback"
            };
            if (coarse) command.Reason += " (" + fallback.Source + ")";
            else if (active.Prediction == BoostbackPrediction.FarReturn) command.Reason += " (return trajectory)";
            return true;
        }

        private Vector3d RoughBoostbackDirection(TargetedRecovery active, Flight flight, Vector3d up, Vector3d velocity)
        {
            Vector3d displacement = active.Body.GetWorldSurfacePosition(active.Latitude, active.Longitude, active.Height) - vessel.CoMD;
            double fallTime = (flight.Vz + Math.Sqrt(flight.Vz * flight.Vz +
                2 * cfg.Gravity * Math.Max(0, flight.Height))) / cfg.Gravity;
            Vector3d change = displacement / Math.Max(1, fallTime) - velocity;
            change -= up * Vector3d.Dot(change, up);
            return change.sqrMagnitude > 0.01 ? change.normalized : up;
        }

        private void ObserveTargetedRecovery(Command command, Flight flight)
        {
            if (targeted == null || targeted.Leg != TargetedLeg.Recovery) return;
            if (command.Phase == Phase.EntryBurn)
            {
                targeted.WaitingForEntry = true;
                CancelTargetedSearch(targeted);
                return;
            }
            if (command.Phase == Phase.Coast && flight.SeaLevelAltitude < cfg.EntryBurnAltitude)
                targeted.WaitingForEntry = false;
            if ((command.Phase == Phase.Yolo || command.Phase == Phase.Recoast) && !targeted.StandardFallback &&
                !cfg.IgnitionAltitudeBlocked(flight.Height))
                FallBackTargeted(targeted, "Landing recovery took priority over the waypoint. Continuing with standard landing.");
            if (command.Phase == Phase.Main || command.Phase == Phase.Final || command.Phase == Phase.Landed)
            {
                targeted.NextBoostbackRetry = float.PositiveInfinity;
                CancelTargetedSearch(targeted);
            }
        }

        private Vector3d BoostbackDirection(TargetedRecovery active, Vector3d up)
        {
            TargetedBasis(up, out Vector3d east, out Vector3d north);
            return (east * active.Boost.EastDeltaV + north * active.Boost.NorthDeltaV + up * active.Boost.UpDeltaV).normalized;
        }

        private void TargetedBasis(Vector3d up, out Vector3d east, out Vector3d north)
        {
            north = WorldDirection(LocalNorth(up));
            CelestialBody body = vessel.mainBody;
            double lat = Math.Max(-89.99, Math.Min(89.99, vessel.latitude));
            Vector3d e = body.GetWorldSurfacePosition(lat, vessel.longitude + 0.01, 0) -
                         body.GetWorldSurfacePosition(lat, vessel.longitude - 0.01, 0);
            east = (e - up * Vector3d.Dot(e, up)).normalized;
        }

        private void ReadTargetedSearch(TargetedRecovery active, Flight flight, double now)
        {
            if (active.Work == null || !active.Work.IsCompleted) return;
            var finished = active.Work;
            TargetedSearch search = active.Search;
            active.Work = null;
            active.Search = TargetedSearch.None;
            active.Cancellation.Dispose();
            active.Cancellation = null;
            if (finished.Status != TaskStatus.RanToCompletion)
            {
                _ = finished.Exception;
                TargetedSearchUnavailable(active, search, now, "task_failed", "Targeted prediction failed. Retrying.");
                return;
            }

            BoostbackPreviewResult plan = finished.Result;
            active.LastSearchMilliseconds = plan.RunMilliseconds;
            active.LastSearchTraces = plan.Footprints;
            if (search == TargetedSearch.Cutoff)
            {
                if (now - active.CapturedAt <= BoostbackCorrectionLifetime(active.BoostStage))
                {
                    if ((active.Fallback != null || active.Boost.ReturnOnly) && !HasBoostbackPrediction(plan.Correction)) return;
                    active.Correction = plan.Correction;
                    active.CorrectionAt = active.CapturedAt;
                    active.Cutoff = plan.Correction?.CutNow;
                }
                return;
            }
            if (search == TargetedSearch.Boostback)
            {
                active.LastBoostbackPlan = plan;
                active.LastBoostbackPlanAt = active.CapturedAt;
                active.LastBoostbackRejection = null;
                if (active.BoostCommandMask != 0 || active.BoostIgnitionCommitted) return;
                if (!TargetedLandingGuidance.MayStartBoostback(flight.Height, flight.Vz, cfg))
                {
                    active.LastBoostbackRejection = "window_closed";
                    active.Leg = TargetedLeg.Recovery;
                    active.NextBoostbackRetry = float.PositiveInfinity;
                    active.NextSearch = 0;
                    return;
                }
                if (now - active.CapturedAt > 1)
                {
                    active.LastBoostbackRejection = "stale_plan";
                    if (active.Leg == TargetedLeg.Recovery)
                        DeferBoostbackPlan(active, "stale_plan", "Boostback prediction expired. Retrying from a fresh flight state.");
                    active.NextSearch = 0;
                    return;
                }
                if (plan.Status == "no_burn_required")
                {
                    active.Boost = null;
                    active.Fallback = null;
                    active.Leg = TargetedLeg.Recovery;
                    active.NextBoostbackRetry = float.PositiveInfinity;
                    active.NextSearch = 0;
                    active.Warning = null;
                    return;
                }
                bool usable = plan.Status == "return_planned" || plan.Status == "planned" || plan.Status == "planned_with_trim";
                if (!usable || !TargetedLandingGuidance.CanGuideBoostback(plan.DefaultAoa) || plan.DefaultAoa.Engines == 0)
                {
                    active.LastBoostbackRejection = "no_accepted_plan";
                    if (active.Leg != TargetedLeg.Boostback)
                        DeferBoostbackPlan(active, "no_accepted_plan", "No accepted Boostback trajectory yet. Retrying from the next flight state.");
                    return;
                }
                if ((flight.Running | flight.Starting | flight.IssuedStarts) != 0 ||
                    !controller.CanUseBoostbackBank(plan.DefaultAoa.Engines, flight, out _))
                {
                    active.LastBoostbackRejection = "main_bank_unavailable";
                    if (active.Leg != TargetedLeg.Boostback)
                        DeferBoostbackPlan(active, "main_bank_unavailable", "Boostback main engine group did not pass the live check. Retrying.");
                    return;
                }
                if (!controller.TryBoostbackLimits(plan.DefaultAoa.PrecisionEngines, flight, out double minimum, out _))
                {
                    active.LastBoostbackRejection = "precision_bank_unavailable";
                    if (active.Leg != TargetedLeg.Boostback)
                        DeferBoostbackPlan(active, "precision_bank_unavailable", "Boostback precision engine group did not pass the live check. Retrying.");
                    return;
                }

                active.Boost = plan.DefaultAoa;
                active.Fallback = null;
                SetBoostbackPrediction(active, active.Boost.ReturnOnly ? BoostbackPrediction.FarReturn : BoostbackPrediction.Far);
                active.TrimMinimum = minimum;
                active.TrimMeasurement = nextThrust;
                active.UseJointPlan = active.Boost.JointPlan;
                active.Glide = plan.GlideApproach;
                double actualAlignment = Vector3.Angle(frame.up, (Vector3)BoostbackDirection(active,
                    (vessel.CoMD - active.Body.position).normalized)) / TurnDegreesPerSecond;
                active.BoostDeadline = now + Math.Max(active.Boost.CutoffSeconds, actualAlignment +
                    controller.BoostbackSpoolSeconds(active.Boost.Engines, flight) +
                    active.Boost.BurnSeconds * TargetedLandingGuidance.BoostbackImpulseLimit) +
                    Math.Max(1, cfg.AttitudeResponseSeconds);
                active.Leg = TargetedLeg.Boostback;
                active.NextBoostbackRetry = float.PositiveInfinity;
                active.NextSearch = 0;
                active.Warning = null;
                return;
            }

            if (!UsesCoastAttitude(lastPhase, cfg.IgnitionAltitudeBlocked(flight.Height))) return;
            double tolerance = TargetedLandingGuidance.GlideTolerance(flight.SeaLevelAltitude, active.Height, cfg);
            if (plan.GlideApproach?.Safe != true)
            {
                active.Warning = "No safe targeted glide found yet. Rechecking during descent.";
                return;
            }
            active.Glide = plan.GlideApproach;
            active.Warning = active.Glide.MissDistance <= tolerance ? null :
                "Predicted miss exceeds the current glide tolerance. Tracking the closest safe glide and rechecking.";
        }

        private void ScheduleTargetedSearch(TargetedRecovery active, TargetedSearch search, Flight flight,
            Vector3d up, Vector3d velocity, double now, BoostbackCorrectionInput correction = null)
        {
            float realtime = Time.realtimeSinceStartup;
            if (active.Work != null || realtime < active.NextSearch) return;
            if (cfg.MinimumCoastPitch > cfg.CoastPitchAoaDegrees || cfg.CoastPitchAoaDegrees > cfg.MaximumCoastPitch)
            {
                TargetedSearchUnavailable(active, search, now, "invalid_aoa_range", "Default AoA is outside the configured bounds. Retrying.");
                return;
            }
            if (active.Body.atmosphere && !farCoast.CanSampleFootprint)
            {
                active.AeroSampleStatus = FarSampleStatus.Unavailable;
                TargetedSearchUnavailable(active, search, now, "far_unavailable", "FAR aero samples are unavailable. Retrying.");
                return;
            }
            if (active.AeroRequest != search)
            {
                active.AeroRequest = search;
                active.AeroDeadline = active.GlideSamplesDeadline = 0;
            }
            if (NeedsGlideBounds(active, search) && active.GlideSamplesDeadline == 0)
                active.GlideSamplesDeadline = realtime + 1;
            if (active.AeroDeadline == 0) active.AeroDeadline = realtime + 2;
            if (!CaptureTargetedAtmosphere(active, search, flight, up, velocity, realtime,
                out FootprintAtmosphere nominal, out FootprintAtmosphere minimum, out FootprintAtmosphere maximum,
                out FootprintAtmosphere boostbackAtmosphere))
            {
                if (realtime >= active.AeroDeadline)
                {
                    active.AeroDeadline = active.GlideSamplesDeadline = 0;
                    active.NextSearch = realtime + 0.5f;
                    TargetedSearchUnavailable(active, search, now, "far_sample_timeout", "FAR aero samples were not ready in time. Retrying.");
                }
                return;
            }
            active.AeroDeadline = active.GlideSamplesDeadline = 0;
            active.AeroSampleStatus = FarSampleStatus.Ready;

            TargetedBasis(up, out Vector3d east, out Vector3d north);
            Engine[] specs = controller.CaptureEngineMeasurements();
            for (int i = 0; i < specs.Length; i++)
                if (specs[i].Ignitions != engines[i].Ignitions)
                    specs[i] = specs[i].WithIgnitions(engines[i].Ignitions);
            // A rolling cutoff forecast begins after shutting the boostback bank down.
            flight.Running = flight.Starting = flight.IssuedStarts = 0;
            flight.EngineThrustAccelerations = null;
            flight.IgnitionElapsed = null;
            flight.AeroForecast = null;
            flight.AlignmentSeconds = 0;
            double usable = flight.Propellant?.ReachableTons(flight.Restartable) ?? 0;
            if (usable <= 0 || flight.Mass <= 0)
            {
                TargetedSearchUnavailable(active, search, now, "no_reachable_propellant", "No reachable propellant estimate for targeted recovery. Retrying.");
                return;
            }

            double dry = Math.Max(0.001, flight.Mass - usable);
            double isp = cfg.SpecificImpulse;
            foreach (Engine engine in specs) isp = Math.Max(isp, engine.SpecificImpulse);
            double currentGlideBank = 0;
            AttitudeVector currentGlideDirection = default;
            if (search == TargetedSearch.Glide)
                MeasureGlideAttitude(velocity, up, out currentGlideBank, out currentGlideDirection);
            var input = new BoostbackPreviewInput
            {
                Flight = flight, Engines = specs, Settings = cfg.Copy(), BackupTorque = BackupTorque(),
                Radius = active.Body.Radius, RotationRate = RotationRate(active.Body),
                Latitude = vessel.latitude, Longitude = vessel.longitude,
                EastSpeed = Vector3d.Dot(east, velocity), NorthSpeed = Vector3d.Dot(north, velocity),
                BoostbackBodyAxis = new AttitudeVector(Vector3d.Dot(east, frame.up),
                    Vector3d.Dot(north, frame.up), Vector3d.Dot(up, frame.up)),
                BoostbackTurnRate = TurnDegreesPerSecond * AttitudeGuidance.Degree,
                WaypointLatitude = active.Latitude, WaypointLongitude = active.Longitude, WaypointHeight = active.Height,
                PositionTolerance = search == TargetedSearch.Glide
                    ? TargetedLandingGuidance.GlideTolerance(flight.SeaLevelAltitude, active.Height, cfg)
                    : TargetedLandingGuidance.ApproachTolerance,
                DryMass = dry,
                MaxDeltaV = search == TargetedSearch.Boostback ? isp * Settings.G0 * Math.Log(flight.Mass / dry) : 0,
                DefaultAtmosphere = nominal, MinimumAtmosphere = minimum, MaximumAtmosphere = maximum,
                BoostbackAtmosphere = boostbackAtmosphere, ObservedAboveEntry = active.ObservedAboveEntry,
                EntryTiltDegrees = search == TargetedSearch.Cutoff && active.UseJointPlan
                    ? active.Boost.BurnDirections.EntryTilt : 0,
                EntryNoseDownAtmosphere = nominal.WithPitchSample(active.Samples[4]),
                EntryNoseUpAtmosphere = nominal.WithPitchSample(active.Samples[5]),
                CurrentGlideBank = currentGlideBank,
                CurrentGlideDirection = flight.SeaLevelAltitude < cfg.EntryBurnAltitude && flight.Vz <= 0 ? currentGlideDirection : default,
                PreferredGlideAoa = active.Glide?.PitchAoa ?? double.NaN,
                PreferredGlideBank = active.Glide?.GlideBank ?? 0
            };
            active.CapturedAt = now;
            active.Search = search;
            active.NextSearch = realtime + (float)(search == TargetedSearch.Cutoff
                ? TargetedLandingGuidance.CorrectionInterval(active.BoostStage) : 1.0);
            active.Cancellation = new CancellationTokenSource();
            CancellationToken token = active.Cancellation.Token;
            bool planReturn = search == TargetedSearch.Boostback && active.Boost?.ReturnOnly != true;
            active.Work = Task.Run(() =>
            {
                if (correction != null) return BoostbackPreview.Correct(input, correction, token);
                if (planReturn) return BoostbackPreview.PlanReturn(input, token);
                return BoostbackPreview.Plan(input, token, search == TargetedSearch.Glide);
            }, token);
        }

        private static double RotationRate(CelestialBody body) =>
            body.rotates && Math.Abs(body.rotationPeriod) > 1e-6 ? 2 * Math.PI / body.rotationPeriod : 0;

        private void MeasureGlideAttitude(Vector3d velocity, Vector3d up, out double bank, out AttitudeVector pointing)
        {
            bank = 0;
            pointing = default;
            if (velocity.sqrMagnitude < 1) return;
            AttitudeVector retrograde = AttitudeCoordinates(-velocity.normalized);
            AttitudeVector localUp = AttitudeCoordinates(up), north = LocalNorth(up);
            AttitudeVector axis = AttitudeCoordinates(frame.up);
            AttitudeGuidance.BankedCoastPointing(retrograde, localUp, north, 0, 0,
                out AttitudeVector forward);
            bank = -AttitudeGuidance.RollError(axis, AttitudeCoordinates(-frame.forward),
                retrograde, forward) / AttitudeGuidance.Degree;
            pointing = new AttitudeVector(axis.Dot(retrograde), axis.Dot(forward), axis.Dot(retrograde.Cross(forward)));
        }

        private bool CaptureTargetedAtmosphere(TargetedRecovery active, TargetedSearch search, Flight flight, Vector3d up,
            Vector3d velocity, float now, out FootprintAtmosphere nominal, out FootprintAtmosphere minimum,
            out FootprintAtmosphere maximum, out FootprintAtmosphere boostback)
        {
            var body = active.Body;
            nominal = minimum = maximum = boostback = null;
            if (!body.atmosphere)
            {
                nominal = minimum = maximum = new FootprintAtmosphere(0, null, default, default, true);
                return true;
            }
            double depth = body.atmosphereDepth;
            bool needEntryTrim = NeedsEntryTrim(active, search);
            int required = needEntryTrim ? 6 : NeedsGlideBounds(active, search) ? 4 : 2;
            // First fill missing samples. A failing refresh of an existing one
            // must not consume the only slot while a required sample is missing.
            bool missing = !active.Samples[0].Valid || !active.Samples[1].Valid;
            for (int i = 0; i < required; i++)
            {
                double pitch = TargetPitch(i);
                if (i != 0 && pitch == 0 && active.Samples[0].Valid)
                {
                    active.Samples[i] = active.Samples[0];
                    continue;
                }
                if (i > 1 && i < 4 && pitch == cfg.CoastPitchAoaDegrees && active.Samples[1].Valid)
                {
                    active.Samples[i] = active.Samples[1];
                    continue;
                }
                if (now < active.Refresh[i] || missing && (i >= 2 || active.Samples[i].Valid)) continue;
                double altitude = Math.Min(depth * 0.85, Math.Max(0, flight.SeaLevelAltitude));
                if (i >= 4) altitude = Math.Min(altitude, cfg.EntryBurnAltitude);
                else if (i > 0) altitude = Math.Min(altitude, cfg.EntryBurnAltitude * 0.85);
                if (velocity.sqrMagnitude < 4) { active.AeroSampleStatus = FarSampleStatus.Slow; return false; }
                var direction = AttitudeGuidance.BankedCoastPointing(AttitudeCoordinates(-velocity.normalized),
                    AttitudeCoordinates(up), LocalNorth(up), 0, pitch, out AttitudeVector forward);
                if (i >= 4)
                    forward = new AttitudeGuidance().RollForward(direction, AttitudeCoordinates(up),
                        LocalNorth(up), rollDegrees, cfg.SampleSeconds);
                if (farCoast.TryFootprintAero(vessel, frame, velocity, up, WorldDirection(direction), altitude,
                    body.GetPressure(altitude), out FootprintAero sample, out FarSampleStatus status, WorldDirection(forward)))
                {
                    active.Samples[i] = sample;
                    active.Refresh[i] = now + 2;
                }
                else if (i < 2 && !active.Samples[i].Valid) RecordTargetedAeroDenial(active, status);
            }
            if (!active.Samples[0].Valid || !active.Samples[1].Valid) return false;
            if ((search == TargetedSearch.Boostback || search == TargetedSearch.Cutoff) &&
                flight.SeaLevelAltitude < depth && now >= active.RefreshBoostAero)
            {
                double altitude = Math.Max(0, flight.SeaLevelAltitude);
                Vector3d direction = search == TargetedSearch.Cutoff && active.LastBoostDirection.sqrMagnitude > 0
                    ? active.LastBoostDirection : RoughBoostbackDirection(active, flight, up, velocity);
                if (farCoast.TryFootprintAero(vessel, frame, velocity, up, direction, altitude,
                    body.GetPressure(altitude), out FootprintAero sample, out FarSampleStatus status))
                {
                    active.BoostAero = sample;
                    active.RefreshBoostAero = now + 2;
                }
                else if (missing && search == TargetedSearch.Boostback && status == FarSampleStatus.FrameBudget)
                    return false;
            }
            if (now < active.GlideSamplesDeadline &&
                (required > 2 && (!active.Samples[2].Valid || !active.Samples[3].Valid) ||
                 needEntryTrim && (!active.Samples[4].Valid || !active.Samples[5].Valid))) return false;
            double[] pressure = ReadFootprintPressure(body);
            nominal = SampledFootprintAtmosphere(depth, pressure, active.Samples[0], active.Samples[1], true,
                cfg.CoastPitchAoaDegrees);
            if (search != TargetedSearch.Glide && active.BoostAero.Valid && now < active.RefreshBoostAero)
                boostback = new FootprintAtmosphere(depth, pressure, active.Samples[0], active.BoostAero, true);
            if (required > 2)
            {
                if (active.Samples[2].Valid)
                    minimum = SampledFootprintAtmosphere(depth, pressure, active.Samples[0], active.Samples[2], true,
                        cfg.MinimumCoastPitch);
                if (active.Samples[3].Valid)
                    maximum = SampledFootprintAtmosphere(depth, pressure, active.Samples[0], active.Samples[3], true,
                        cfg.MaximumCoastPitch);
            }
            return true;
        }

        private static void RecordTargetedAeroDenial(TargetedRecovery active, FarSampleStatus status)
        {
            active.AeroSampleStatus = status;
            switch (status)
            {
                case FarSampleStatus.FrameBudget: active.AeroBudgetDenied++; break;
                case FarSampleStatus.VoxelPending: active.AeroVoxelPending++; break;
                case FarSampleStatus.InvalidForce:
                case FarSampleStatus.Error: active.AeroInvalid++; break;
            }
        }

        private bool NeedsGlideBounds(TargetedRecovery active, TargetedSearch search) =>
            search == TargetedSearch.Glide || NeedsEntryTrim(active, search);

        private bool NeedsEntryTrim(TargetedRecovery active, TargetedSearch search) =>
            search == TargetedSearch.Boostback && active.Boost?.ReturnOnly == true && cfg.OptimizeRecoveryTrajectory ||
            search == TargetedSearch.Cutoff && active.UseJointPlan;

        private double TargetPitch(int index)
        {
            switch (index)
            {
                case 0: return 0;
                case 1: return cfg.CoastPitchAoaDegrees;
                case 2: return cfg.MinimumCoastPitch;
                case 3: return cfg.MaximumCoastPitch;
                case 4: return -BoostbackPreview.EntryTrimAngle;
                default: return BoostbackPreview.EntryTrimAngle;
            }
        }
    }
}
