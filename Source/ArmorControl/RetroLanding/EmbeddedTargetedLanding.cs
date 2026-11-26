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
            internal double CapturedAt, CutoffObservedAt, Delivered, BoostDeadline;
            internal BoostbackStage BoostStage;
            internal float NextSearch, GlideSamplesDeadline, AeroDeadline;
            internal bool StandardFallback, WaitingForEntry, ObservedAboveEntry;
            internal bool BoostUllageRequested, BoostIgnitionCommitted, UseJointPlan;
            internal FootprintAero BoostAero;
            internal float RefreshBoostAero;
            internal string Warning;
            internal BoostbackApproach Boost, Glide, Cutoff;
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
                Latitude = latitude, Longitude = longitude, Height = Math.Max(0, ground),
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
                FallBackTargeted(targeted, "Warp interrupted Boostback. Switching to standard landing.");
            targeted.NextSearch = 0;
        }

        private static void FallBackTargeted(TargetedRecovery active, string reason, BoostbackApproach boundary = null)
        {
            CancelTargetedSearch(active);
            if (active.Leg == TargetedLeg.Boostback)
                active.Leg = TargetedLeg.Cutoff;
            else if (active.Leg != TargetedLeg.Cutoff)
                active.Leg = TargetedLeg.Recovery;
            active.StandardFallback = true;
            active.Warning = reason;
            active.Glide = boundary != null && boundary.Safe ? boundary : null;
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
            BoostRemaining = targeted.Boost == null ? double.NaN : Math.Max(0, targeted.Boost.DeltaV - targeted.Delivered)
        };

        private bool TryTargetedTouchdown(Command command, out RetroTouchdown snapshot)
        {
            snapshot = default;
            var active = targeted;
            if (active == null || command.Phase == Phase.Landed) return false;
            BoostbackApproach estimate = null;
            if (active.Leg == TargetedLeg.Planning || active.Leg == TargetedLeg.Boostback || active.Leg == TargetedLeg.Cutoff)
                estimate = active.Cutoff ?? active.Boost;
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

        private bool TryTargetedGlideAttitude(double altitude, AttitudeVector retrograde, AttitudeVector up,
            AttitudeVector north, ref double aoa, out Vector3d direction, out AttitudeVector forward)
        {
            direction = default;
            forward = default;
            if (targeted == null || targeted.Body != vessel.mainBody || targeted.Leg != TargetedLeg.Recovery || lastPhase != Phase.Coast ||
                altitude >= cfg.EntryBurnAltitude || targeted.WaitingForEntry || (targeted.StandardFallback && targeted.Glide == null))
                return false;

            double bank = targeted.Glide?.GlideBank ?? 0;
            aoa = targeted.Glide?.PitchAoa ?? cfg.CoastPitchAoaDegrees;
            AttitudeGuidance.BankedCoastPointing(retrograde, up, north, bank, 0, out AttitudeVector bankReference);
            double error = AttitudeGuidance.RollError(AttitudeCoordinates(frame.up), AttitudeCoordinates(-frame.forward),
                retrograde, bankReference);
            double rate = vessel.rootPart.rb == null ? 0 : Math.Abs(Vector3.Dot(vessel.rootPart.rb.angularVelocity, frame.up));
            if (Math.Abs(error) > Settings.IgnitionToleranceDegrees * AttitudeGuidance.Degree / 2 ||
                rate > AttitudeGuidance.ReferenceRollRate / 4)
                aoa = 0;
            direction = WorldDirection(AttitudeGuidance.BankedCoastPointing(retrograde, up, north, bank, aoa, out forward));
            return true;
        }

        private Vector3d TargetedVelocity(Vector3d up, Vector3d lateral, GroundReading ground, double vz)
        {
            if (targeted == null || targeted.Body != vessel.mainBody || targeted.StandardFallback || targeted.Leg != TargetedLeg.Recovery ||
                (lastPhase != Phase.Main && lastPhase != Phase.Final) || vz >= 0)
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
            try { return TryTargetedBoostback(flight, now, elapsed, up, velocity, thrust, out command, out direction); }
            catch (Exception failure)
            {
                command = default;
                direction = default;
                if (targeted == null) throw;
                FallBackTargeted(targeted, "Targeted guidance failed. Switching to standard landing.");
                Debug.LogWarning("[RetroLanding] Targeted guidance failed: " + failure.Message);
                if (targeted.Leg != TargetedLeg.Cutoff) return false;
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
            if (active.Body != vessel.mainBody && !active.StandardFallback)
            {
                FallBackTargeted(active, "Target waypoint is on another body. Switching to standard landing.");
            }
            if (flight.SolidBurnActive)
            {
                CancelTargetedSearch(active);
                if (active.Leg == TargetedLeg.Boostback)
                    FallBackTargeted(active, "Boostback interrupted by an active solid motor. Switching to standard landing.");
                return false;
            }

            LandingTuning tuning = Tuning;
            if (!active.Tuning.SameFlightSettings(tuning))
            {
                if (active.Leg == TargetedLeg.Boostback)
                    FallBackTargeted(active, "Landing settings changed during Boostback. Switching to standard landing.");
                CancelTargetedSearch(active);
                Array.Clear(active.Samples, 0, active.Samples.Length);
                Array.Clear(active.Refresh, 0, active.Refresh.Length);
                active.Tuning = tuning;
                active.Glide = null;
                active.UseJointPlan = false;
                active.BoostAero = default;
                active.NextSearch = active.GlideSamplesDeadline = active.AeroDeadline = active.RefreshBoostAero = 0;
            }

            ReadTargetedSearch(active, flight, now);
            if (active.Leg == TargetedLeg.Recovery)
            {
                if (!active.StandardFallback && !active.WaitingForEntry && lastPhase == Phase.Coast &&
                    flight.SeaLevelAltitude < cfg.EntryBurnAltitude && (flight.Running | flight.Starting | flight.IssuedStarts) == 0)
                    ScheduleTargetedSearch(active, TargetedSearch.Glide, flight, up, velocity, now);
                return false;
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
                    FallBackTargeted(active, "An engine is already running. Switching to standard landing.");
                    return false;
                }
                if (!active.BoostUllageRequested)
                {
                    active.BoostUllageRequested = true;
                    if (!RcsEnabled && engines.Exists(engine => engine.NeedsUllage))
                        vessel.ActionGroups.SetGroup(KSPActionGroup.RCS, true);
                }
                ScheduleTargetedSearch(active, TargetedSearch.Boostback, flight, up, velocity, now);
                if (active.Leg == TargetedLeg.Recovery) return false;
                direction = RoughBoostbackDirection(active, flight, up, velocity);
                command = new Command { Phase = Phase.Boostback, PrepareBoostbackUllage = true,
                    Reason = "Planning Boostback, aligning and preparing ullage" };
                return true;
            }

            if (active.Leg == TargetedLeg.Boostback && !active.BoostIgnitionCommitted)
            {
                active.BoostIgnitionCommitted =
                    ((flight.Running | flight.IssuedStarts) & active.Boost.PrecisionEngines) != 0;
                if (!active.BoostIgnitionCommitted && !mayStart)
                {
                    CancelTargetedSearch(active);
                    active.Leg = TargetedLeg.Cutoff;
                }
            }

            if (active.Leg == TargetedLeg.Boostback)
            {
                direction = BoostbackDirection(active, up);
                active.Delivered = TargetedLandingGuidance.AddImpulse(active.Delivered,
                    Vector3d.Dot(thrust, direction), flight.Mass,
                    elapsed <= Math.Max(0.1, 2 * cfg.SampleSeconds) ? elapsed : 0);
                BoostbackApproach estimate = now - active.CutoffObservedAt <= 0.75 ? active.Cutoff : null;
                if (active.UseJointPlan && estimate != null && !estimate.Touchdown.EntryCompleted) estimate = null;
                if (TargetedLandingGuidance.CanCutBoostback(active.Delivered, active.Boost.DeltaV, estimate))
                {
                    CancelTargetedSearch(active);
                    active.Leg = TargetedLeg.Cutoff;
                }
                else if (now > active.BoostDeadline)
                    FallBackTargeted(active, "Boostback did not deliver its planned impulse in time. Switching to standard landing.");
                else if (TryBoostbackBank(active, flight, now, up, estimate, ref direction, out command))
                {
                    if (thrust.sqrMagnitude > 1)
                        ScheduleTargetedSearch(active, TargetedSearch.Cutoff, flight, up, velocity, now);
                    return true;
                }
                else
                    FallBackTargeted(active, "Boostback engines or max G changed. Switching to standard landing.");
            }

            if ((flight.Running | flight.Starting | flight.IssuedStarts) == 0 && thrust.sqrMagnitude < 1)
            {
                active.Leg = TargetedLeg.Recovery;
                active.NextSearch = 0;
                return false;
            }
            direction = active.Boost == null ? up : BoostbackDirection(active, up);
            command = new Command { Phase = Phase.Boostback, Reason = "Boostback cutoff, waiting for engines to shut down" };
            return true;
        }

        private bool TryBoostbackBank(TargetedRecovery active, Flight flight, double now, Vector3d up,
            BoostbackApproach estimate, ref Vector3d direction, out Command command)
        {
            command = default;
            long precision = active.Boost.PrecisionEngines;
            double remaining = Math.Max(0, active.Boost.DeltaV - active.Delivered);
            if (!controller.CanUseBoostbackBank(precision, flight, out double trimAcceleration)) return false;
            double angle = Vector3.Angle(frame.up, (Vector3)direction);
            var previous = active.BoostStage;
            var stage = TargetedLandingGuidance.BoostbackStageFor(previous, angle,
                precision != 0 && (flight.EstablishedThrust & precision) == precision,
                remaining, estimate != null && estimate.Safe ? estimate.MissDistance : double.PositiveInfinity,
                trimAcceleration, cfg);
            long mask = TargetedLandingGuidance.BoostbackMask(stage, precision, active.Boost.Engines);
            double acceleration = mask == precision ? trimAcceleration : 0;
            if (stage == BoostbackStage.Full && mask != precision &&
                !controller.CanUseBoostbackBank(mask, flight, out acceleration)) return false;
            if (stage == BoostbackStage.Fine)
            {
                if (previous != stage)
                    active.BoostDeadline = Math.Max(active.BoostDeadline,
                        now + remaining / trimAcceleration + 2 * Math.Max(1, cfg.AttitudeResponseSeconds));
                if (estimate != null && estimate.Safe)
                {
                    TargetedBasis(up, out Vector3d east, out Vector3d north);
                    Vector3d correction = -estimate.MissEast * east - estimate.MissNorth * north;
                    if (correction.sqrMagnitude > 1 && Vector3d.Dot(correction, direction) > 0)
                    {
                        double elevation = Vector3d.Dot(direction, up);
                        var lateral = (direction - up * elevation).normalized;
                        var turned = WorldDirection(TargetedLandingGuidance.Slew(AttitudeCoordinates(lateral),
                            AttitudeCoordinates(correction.normalized), AttitudeCoordinates(up), 20 * AttitudeGuidance.Degree));
                        direction = turned * Math.Sqrt(Math.Max(0, 1 - elevation * elevation)) + up * elevation;
                    }
                }
                acceleration = Math.Min(acceleration, remaining / Math.Max(cfg.SampleSeconds,
                    Math.Min(1, cfg.AttitudeResponseSeconds)));
            }
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
                return;
            }
            if (command.Phase == Phase.Coast && flight.SeaLevelAltitude < cfg.EntryBurnAltitude)
                targeted.WaitingForEntry = false;
            if ((command.Phase == Phase.Yolo || command.Phase == Phase.Recoast) && !targeted.StandardFallback)
                FallBackTargeted(targeted, "Landing recovery took priority over the waypoint. Continuing with standard landing.");
            if (command.Phase == Phase.Main || command.Phase == Phase.Final || command.Phase == Phase.Landed)
                CancelTargetedSearch(targeted);
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
                if (search != TargetedSearch.Cutoff)
                    FallBackTargeted(active, "Targeted prediction failed. Switching to standard landing.");
                return;
            }

            BoostbackPreviewResult plan = finished.Result;
            if (search == TargetedSearch.Cutoff)
            {
                if (now - active.CapturedAt <= 0.75 && plan.DefaultAoa != null)
                {
                    active.Cutoff = plan.DefaultAoa;
                    active.CutoffObservedAt = active.CapturedAt;
                }
                return;
            }
            if (search == TargetedSearch.Boostback)
            {
                if (!TargetedLandingGuidance.MayStartBoostback(flight.Height, flight.Vz, cfg))
                {
                    active.Leg = TargetedLeg.Recovery;
                    active.NextSearch = 0;
                }
                else if (now - active.CapturedAt > 1)
                {
                    active.NextSearch = 0;
                }
                else if (plan.Status == "planned" && plan.DefaultAoa != null && plan.DefaultAoa.Engines != 0 &&
                    controller.CanUseBoostbackBank(plan.DefaultAoa.Engines, flight, out _) &&
                    controller.CanUseBoostbackBank(plan.DefaultAoa.PrecisionEngines, flight, out _))
                {
                    active.Boost = plan.DefaultAoa;
                    active.UseJointPlan = active.Boost.JointPlan;
                    active.Glide = plan.GlideApproach;
                    double actualAlignment = Vector3.Angle(frame.up, (Vector3)BoostbackDirection(active,
                        (vessel.CoMD - active.Body.position).normalized)) / TurnDegreesPerSecond;
                    active.BoostDeadline = now + Math.Max(active.Boost.CutoffSeconds, actualAlignment +
                        controller.BoostbackSpoolSeconds(active.Boost.Engines, flight) + active.Boost.BurnSeconds) +
                        Math.Max(1, cfg.AttitudeResponseSeconds);
                    active.Leg = TargetedLeg.Boostback;
                    active.NextSearch = 0;
                }
                else if (plan.Status == "no_burn_required")
                {
                    active.Leg = TargetedLeg.Recovery;
                    active.NextSearch = 0;
                }
                else
                    FallBackTargeted(active, "No safe Boostback plan within the available fuel and engine limits. Switching to standard landing.");
                return;
            }

            if (lastPhase != Phase.Coast) return;
            if (plan.UseStandardLanding || plan.GlideApproach == null)
            {
                string status = plan.GlideWarning == "waypoint_outside_tested_aoa_range" ?
                    "Waypoint is outside the configured AoA options. Continuing with standard landing." :
                    "No reliable targeted glide. Continuing with standard landing.";
                FallBackTargeted(active, status, plan.GlideApproach);
            }
            else active.Glide = plan.GlideApproach;
        }

        private void ScheduleTargetedSearch(TargetedRecovery active, TargetedSearch search, Flight flight,
            Vector3d up, Vector3d velocity, double now)
        {
            float realtime = Time.realtimeSinceStartup;
            if (active.Work != null || realtime < active.NextSearch) return;
            if (cfg.MinimumCoastPitch > cfg.CoastPitchAoaDegrees || cfg.CoastPitchAoaDegrees > cfg.MaximumCoastPitch)
            {
                if (search != TargetedSearch.Cutoff)
                    FallBackTargeted(active, "Default AoA is outside the configured bounds. Switching to standard landing.");
                return;
            }
            if (active.Body.atmosphere && !farCoast.CanSampleFootprint)
            {
                if (search != TargetedSearch.Cutoff)
                    FallBackTargeted(active, "FAR aero samples are unavailable. Switching to standard landing.");
                return;
            }
            if (NeedsGlideBounds(active, search) && active.GlideSamplesDeadline == 0)
                active.GlideSamplesDeadline = realtime + 1;
            if (active.AeroDeadline == 0) active.AeroDeadline = realtime + 2;
            if (!CaptureTargetedAtmosphere(active, search, flight, up, velocity, realtime,
                out FootprintAtmosphere nominal, out FootprintAtmosphere minimum, out FootprintAtmosphere maximum,
                out FootprintAtmosphere boostbackAtmosphere))
            {
                if (realtime >= active.AeroDeadline && search != TargetedSearch.Cutoff)
                    FallBackTargeted(active, "FAR aero samples were not ready in time. Switching to standard landing.");
                return;
            }
            active.AeroDeadline = 0;

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
                if (search != TargetedSearch.Cutoff)
                    FallBackTargeted(active, "No reachable propellant for targeted recovery. Switching to standard landing.");
                return;
            }

            double dry = Math.Max(0.001, flight.Mass - usable);
            double isp = cfg.SpecificImpulse;
            foreach (Engine engine in specs) isp = Math.Max(isp, engine.SpecificImpulse);
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
                PositionTolerance = TargetedLandingGuidance.ApproachTolerance, DryMass = dry,
                MaxDeltaV = search == TargetedSearch.Boostback ? isp * Settings.G0 * Math.Log(flight.Mass / dry) : 0,
                DefaultAtmosphere = nominal, MinimumAtmosphere = minimum, MaximumAtmosphere = maximum,
                BoostbackAtmosphere = boostbackAtmosphere, ObservedAboveEntry = active.ObservedAboveEntry,
                EntryTiltDegrees = search == TargetedSearch.Cutoff && active.UseJointPlan
                    ? active.Boost.BurnDirections.EntryTilt : 0,
                EntryNoseDownAtmosphere = nominal.WithPitchSample(active.Samples[4]),
                EntryNoseUpAtmosphere = nominal.WithPitchSample(active.Samples[5]),
                CurrentGlideBank = search == TargetedSearch.Glide ? MeasuredGlideBank(velocity, up) : 0,
                PreferredGlideAoa = active.Glide?.PitchAoa ?? double.NaN,
                PreferredGlideBank = active.Glide?.GlideBank ?? 0
            };
            active.CapturedAt = now;
            active.Search = search;
            active.NextSearch = realtime + (search == TargetedSearch.Cutoff ? 0.5f : 1f);
            active.Cancellation = new CancellationTokenSource();
            CancellationToken token = active.Cancellation.Token;
            active.Work = Task.Run(() => BoostbackPreview.Plan(input, token, search == TargetedSearch.Glide), token);
        }

        private static double RotationRate(CelestialBody body) =>
            body.rotates && Math.Abs(body.rotationPeriod) > 1e-6 ? 2 * Math.PI / body.rotationPeriod : 0;

        private double MeasuredGlideBank(Vector3d velocity, Vector3d up)
        {
            if (velocity.sqrMagnitude < 1) return 0;
            AttitudeVector retrograde = AttitudeCoordinates(-velocity.normalized);
            AttitudeGuidance.BankedCoastPointing(retrograde, AttitudeCoordinates(up), LocalNorth(up), 0, 0,
                out AttitudeVector forward);
            return -AttitudeGuidance.RollError(AttitudeCoordinates(frame.up), AttitudeCoordinates(-frame.forward),
                retrograde, forward) / AttitudeGuidance.Degree;
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
            if (search == TargetedSearch.Boostback && flight.SeaLevelAltitude < depth && now >= active.RefreshBoostAero)
            {
                double altitude = Math.Max(0, flight.SeaLevelAltitude);
                Vector3d direction = RoughBoostbackDirection(active, flight, up, velocity);
                if (farCoast.TryFootprintAero(vessel, frame, velocity, up, direction, altitude,
                    body.GetPressure(altitude), out FootprintAero sample))
                {
                    active.BoostAero = sample;
                    active.RefreshBoostAero = now + 2;
                }
            }
            bool needEntryTrim = search == TargetedSearch.Boostback && cfg.OptimizeRecoveryTrajectory ||
                search == TargetedSearch.Cutoff && active.UseJointPlan;
            int required = needEntryTrim ? 6 : NeedsGlideBounds(active, search) ? 4 : 2;
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
                if (now < active.Refresh[i]) continue;
                double altitude = Math.Min(depth * 0.85, Math.Max(0, flight.SeaLevelAltitude));
                if (i >= 4) altitude = Math.Min(altitude, cfg.EntryBurnAltitude);
                else if (i > 0) altitude = Math.Min(altitude, cfg.EntryBurnAltitude * 0.85);
                if (velocity.sqrMagnitude < 4) return false;
                var direction = AttitudeGuidance.BankedCoastPointing(AttitudeCoordinates(-velocity.normalized),
                    AttitudeCoordinates(up), LocalNorth(up), 0, pitch, out AttitudeVector forward);
                if (i >= 4)
                    forward = new AttitudeGuidance().RollForward(direction, AttitudeCoordinates(up),
                        LocalNorth(up), rollDegrees, cfg.SampleSeconds);
                if (farCoast.TryFootprintAero(vessel, frame, velocity, up, WorldDirection(direction), altitude,
                    body.GetPressure(altitude), out FootprintAero sample, WorldDirection(forward)))
                {
                    active.Samples[i] = sample;
                    active.Refresh[i] = now + 2;
                }
            }
            if (!active.Samples[0].Valid || !active.Samples[1].Valid) return false;
            if (now < active.GlideSamplesDeadline &&
                (required > 2 && (!active.Samples[2].Valid || !active.Samples[3].Valid) ||
                 needEntryTrim && (!active.Samples[4].Valid || !active.Samples[5].Valid))) return false;
            double[] pressure = ReadFootprintPressure(body);
            nominal = new FootprintAtmosphere(depth, pressure, active.Samples[0], active.Samples[1], true);
            if (search == TargetedSearch.Boostback && active.BoostAero.Valid && now < active.RefreshBoostAero)
                boostback = new FootprintAtmosphere(depth, pressure, active.Samples[0], active.BoostAero, true);
            if (required > 2)
            {
                if (active.Samples[2].Valid)
                    minimum = new FootprintAtmosphere(depth, pressure, active.Samples[0], active.Samples[2], true);
                if (active.Samples[3].Valid)
                    maximum = new FootprintAtmosphere(depth, pressure, active.Samples[0], active.Samples[3], true);
            }
            return true;
        }

        private bool NeedsGlideBounds(TargetedRecovery active, TargetedSearch search) =>
            search == TargetedSearch.Glide || (search == TargetedSearch.Boostback && cfg.OptimizeRecoveryTrajectory) ||
            (search == TargetedSearch.Cutoff && active.UseJointPlan);

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
