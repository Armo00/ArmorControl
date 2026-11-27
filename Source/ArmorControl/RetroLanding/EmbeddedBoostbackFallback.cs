using System;
using ArmorOverhaul.ArmorControl.Core;
using LandingPoc;
using UnityEngine;

namespace KspRetroLanding
{
    public sealed partial class KspLanding
    {
        private enum BoostbackPrediction { None, Ballistic, Trajectories, FarReturn, Far }

        private sealed class BoostbackFallback
        {
            internal long LandingBank;
            internal double NextEstimate, UpdatedAt, ImpactSeconds, ImpactSpeed, Remaining, Miss, ImpulseLimit;
            internal double NextFuelCheck, FuelLimit;
            internal long LastFuelBank;
            internal int SamplesFromSource;
            internal BoostbackPrediction Source;
        }

        private bool TryBeginFallbackBoostback(TargetedRecovery active, Flight flight, Vector3d up, Vector3d velocity, double now)
        {
            if (now < active.NextFallbackAttempt) return false;
            active.NextFallbackAttempt = now + 0.25;
            if (!controller.TryFallbackBoostbackBanks(flight, out long main, out long precision, out long landing)) return false;
            active.Fallback = new BoostbackFallback { LandingBank = landing };
            active.Boost = new BoostbackApproach { Engines = main, PrecisionEngines = precision };
            if (!UpdateFallbackEstimate(active, flight, up, velocity, now) ||
                active.Fallback.Miss <= TargetedLandingGuidance.BoostbackCutoffDistance ||
                !controller.TryBoostbackLimits(main, flight, out _, out double maximum))
            {
                active.Fallback = null;
                active.Boost = null;
                return false;
            }
            double limit = FallbackFuelLimit(active, flight, main);
            if (limit <= 0)
            {
                active.Fallback = null;
                active.Boost = null;
                return false;
            }
            active.Fallback.ImpulseLimit = Math.Min(limit,
                active.Fallback.Remaining * TargetedLandingGuidance.BoostbackImpulseLimit);
            active.Boost.BoostThrustDeltaV = active.Fallback.Remaining;
            active.Boost.BurnSeconds = active.Fallback.ImpulseLimit * flight.Mass / maximum;
            double align = Vector3.Angle(frame.up, (Vector3)BoostbackDirection(active, up)) / TurnDegreesPerSecond;
            active.BoostDeadline = now + align + controller.BoostbackSpoolSeconds(main, flight) +
                active.Boost.BurnSeconds + Math.Max(1, cfg.AttitudeResponseSeconds);
            active.Leg = TargetedLeg.Boostback;
            active.NextBoostbackRetry = float.PositiveInfinity;
            return true;
        }

        private bool UpdateFallbackEstimate(TargetedRecovery active, Flight flight, Vector3d up, Vector3d velocity, double now)
        {
            var fallback = active.Fallback;
            if (now < fallback.NextEstimate) return fallback.Source != BoostbackPrediction.None;
            fallback.NextEstimate = now + 0.5;
            TargetedBasis(up, out Vector3d east, out Vector3d north);
            double lat, lon, seconds, speed;
            BoostbackPrediction source;
            if (TrajectoriesBridge.TryReadImpact(vessel, now, out lat, out lon, out seconds, out speed))
                source = BoostbackPrediction.Trajectories;
            else if (BoostbackBallistic.TryImpact(active.Body.Radius, active.Body.gravParameter, RotationRate(active.Body),
                vessel.latitude, vessel.longitude, flight.SeaLevelAltitude,
                Vector3d.Dot(velocity, east), Vector3d.Dot(velocity, north), flight.Vz, active.Height,
                out lat, out lon, out seconds, out speed)) source = BoostbackPrediction.Ballistic;
            else
            {
                fallback.Source = BoostbackPrediction.None;
                fallback.SamplesFromSource = 0;
                return false;
            }

            Vector3d impact = active.Body.GetWorldSurfacePosition(lat, lon, active.Height);
            Vector3d target = active.Body.GetWorldSurfacePosition(active.Latitude, active.Longitude, active.Height);
            Vector3d offset = target - impact;
            // Include the measured thrust that persists after an immediate cutoff.
            Vector3d tail = Vector3d.zero;
            foreach (EngineBinding engine in engines)
                if (engine.Engine.finalThrust > 0)
                    tail += engine.ThrustVector() * engine.BoostbackShutdownTime(cfg.SampleSeconds);
            double duration = Math.Max(1, seconds);
            offset -= tail * (duration / flight.Mass);
            double eastSpeed = Vector3d.Dot(offset, east) / duration;
            double northSpeed = Vector3d.Dot(offset, north) / duration;
            active.Boost.EastDeltaV = eastSpeed;
            active.Boost.NorthDeltaV = northSpeed;
            fallback.Remaining = Controller.Norm(eastSpeed, northSpeed);
            fallback.Miss = fallback.Remaining * duration;
            fallback.ImpactSeconds = seconds;
            fallback.ImpactSpeed = speed;
            fallback.UpdatedAt = now;
            fallback.SamplesFromSource = source == fallback.Source ? Math.Min(2, fallback.SamplesFromSource + 1) : 1;
            fallback.Source = source;
            return true;
        }

        private double FallbackFuelLimit(TargetedRecovery active, Flight flight, long bank)
        {
            if (flight.Propellant == null || flight.Mass <= 0) return 0;
            var fallback = active.Fallback;
            double landingSpeed = Math.Sqrt(2 * cfg.Gravity * Math.Max(0, cfg.MaxIgnitionAltitude));
            double apex = flight.SeaLevelAltitude + Math.Max(0, flight.Vz) * Math.Max(0, flight.Vz) / (2 * cfg.Gravity);
            double entry = active.ObservedAboveEntry || apex > cfg.EntryBurnAltitude
                ? Math.Max(0, fallback.ImpactSpeed - cfg.EntryBurnSpeed) : 0;
            double recoveryDv = landingSpeed + cfg.Gravity * (cfg.PrecisionSeconds + cfg.AttitudeResponseSeconds) + entry;
            double exhaust = cfg.SpecificImpulse * Settings.G0;
            double reserve = flight.Mass * (1 - Math.Exp(-recoveryDv / exhaust));
            if (flight.Propellant.Shortfall(0, 0, fallback.LandingBank, reserve) > 1e-6) return 0;
            double lo = 0, hi = Math.Min(flight.Propellant.ReachableTons(bank), flight.Mass - reserve);
            for (int i = 0; i < 10; i++)
            {
                double middle = (lo + hi) / 2;
                if (flight.Propellant.Shortfall(bank, middle, fallback.LandingBank, reserve) > 1e-6) hi = middle;
                else lo = middle;
            }
            return exhaust * Math.Log(flight.Mass / Math.Max(0.001, flight.Mass - lo));
        }

        private static void SetBoostbackPrediction(TargetedRecovery active, BoostbackPrediction source)
        {
            if (active.Prediction == source) return;
            active.Prediction = source;
            active.CutoffTrend = default;
        }

        private static string CoarseBoostbackCutoff(TargetedRecovery active)
        {
            var estimate = active.Fallback;
            if (estimate.Source == BoostbackPrediction.None) return "coarse_impact_unavailable";
            if (estimate.Miss <= TargetedLandingGuidance.BoostbackCutoffDistance) return "coarse_predicted_target";
            return active.BoostIgnitionCommitted && active.CutoffTrend.Observe(estimate.UpdatedAt, estimate.Miss)
                ? "coarse_predicted_distance_increasing" : null;
        }

        private static bool HasBoostbackPrediction(BoostbackCorrectionResult correction) =>
            TargetedLandingGuidance.CanGuideBoostback(correction?.CutNow) ||
            TargetedLandingGuidance.CanGuideBoostback(correction?.AfterCorrection);

        private static BoostbackPrediction PredictionSource(BoostbackCorrectionResult correction, BoostbackPrediction otherwise)
        {
            var route = correction?.CutNow ?? correction?.AfterCorrection;
            if (!TargetedLandingGuidance.CanGuideBoostback(route)) return otherwise;
            return route.ReturnOnly ? BoostbackPrediction.FarReturn : BoostbackPrediction.Far;
        }

        private static double BoostbackImpulseRemaining(TargetedRecovery active) => Math.Max(0,
            (active.Fallback?.ImpulseLimit ?? (active.Boost.BoostThrustDeltaV * TargetedLandingGuidance.BoostbackImpulseLimit)) -
            active.DeliveredMagnitude);
    }
}
