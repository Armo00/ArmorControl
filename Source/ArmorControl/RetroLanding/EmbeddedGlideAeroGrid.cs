using System;
using System.Collections.Generic;
using LandingPoc;
using UnityEngine;

namespace KspRetroLanding
{
    public sealed partial class KspLanding
    {
        private sealed class AeroGridCapture
        {
            internal readonly FootprintAero[] Values =
                new FootprintAero[FootprintAeroGrid.Speeds.Length * FootprintAeroGrid.HeightFractions.Length];
            internal int Count;
            internal FootprintAeroGrid Snapshot;
        }

        private readonly Dictionary<double, AeroGridCapture> glideAeroGrids = new Dictionary<double, AeroGridCapture>();
        private double[] glideDensity, glideNodeDensity;
        private CelestialBody glideGridBody;
        private double glideGridAoa, glideGridAoaMin, glideGridAoaMax;
        private bool glideGridGear;
        private float nextGlideGridSample;

        private void ResetGlideAeroGrid()
        {
            glideGridBody = null;
            glideDensity = glideNodeDensity = null;
            glideAeroGrids.Clear();
            nextGlideGridSample = 0;
        }

        private void SampleGlideAeroGrid(Flight flight, Vector3d velocity, Vector3d up)
        {
            if (lastPhase != Phase.Coast || flight.Running != 0 || flight.Starting != 0 ||
                flight.Height < cfg.MaxIgnitionAltitude || velocity.sqrMagnitude < 4 ||
                !vessel.mainBody.atmosphere || !farCoast.CanSampleFootprint ||
                flight.SeaLevelAltitude >= vessel.mainBody.atmosphereDepth ||
                Time.realtimeSinceStartup < nextGlideGridSample) return;
            // The single-pose fallback always gets its first samples before
            // the optional grid starts using the shared per-frame budget.
            if (targeted != null && !targeted.StandardFallback && (!targeted.Samples[0].Valid || !targeted.Samples[1].Valid ||
                targeted.Leg == TargetedLeg.Recovery &&
                flight.SeaLevelAltitude < cfg.EntryBurnAltitude &&
                (!targeted.Samples[2].Valid || !targeted.Samples[3].Valid))) return;
            float now = Time.realtimeSinceStartup;
            nextGlideGridSample = now + 0.10f;
            CelestialBody body = vessel.mainBody;
            if (glideGridBody != body || glideGridGear != flight.GearCommanded ||
                glideGridAoa != cfg.CoastPitchAoaDegrees || glideGridAoaMin != cfg.MinimumCoastPitch ||
                glideGridAoaMax != cfg.MaximumCoastPitch)
            {
                ResetGlideAeroGrid();
                nextGlideGridSample = now + 0.10f;
                glideGridBody = body;
                glideGridGear = flight.GearCommanded;
                glideGridAoa = cfg.CoastPitchAoaDegrees;
                glideGridAoaMin = cfg.MinimumCoastPitch;
                glideGridAoaMax = cfg.MaximumCoastPitch;
                var pressure = ReadFootprintPressure(body);
                glideDensity = new double[pressure.Length];
                for (int i = 0; i < pressure.Length; i++)
                {
                    double height = body.atmosphereDepth * i / (pressure.Length - 1);
                    // The body's constant gas factor cancels when the force
                    // is normalized at sampling and restored in propagation.
                    glideDensity[i] = pressure[i] / Math.Max(1, body.GetTemperature(height));
                }
                glideNodeDensity = new double[FootprintAeroGrid.HeightFractions.Length];
                for (int i = 0; i < glideNodeDensity.Length; i++)
                {
                    double height = body.atmosphereDepth * FootprintAeroGrid.HeightFractions[i];
                    glideNodeDensity[i] = body.GetPressure(height) / Math.Max(1, body.GetTemperature(height));
                }
            }

            // Fill neighboring cells around the actual flight state first.
            // TryFootprintAero shares the existing rendered-frame FAR budget.
            double speedNow = velocity.magnitude;
            double heightNow = flight.SeaLevelAltitude / body.atmosphereDepth;
            double best = double.PositiveInfinity;
            double pitch = 0;
            int chosen = -1;
            AeroGridCapture selected = null;
            foreach (double candidate in new[] { 0.0, glideGridAoa, glideGridAoaMin, glideGridAoaMax })
            {
                if (!glideAeroGrids.TryGetValue(candidate, out AeroGridCapture grid))
                    glideAeroGrids.Add(candidate, grid = new AeroGridCapture());
                if (grid.Count == grid.Values.Length) continue;
                for (int i = 0; i < grid.Values.Length; i++)
                {
                    if (grid.Values[i].Valid) continue;
                    int column = i % FootprintAeroGrid.Speeds.Length, row = i / FootprintAeroGrid.Speeds.Length;
                    if (glideNodeDensity[row] <= 0) continue;
                    double range = Math.Abs(FootprintAeroGrid.Speeds[column] - speedNow) / Math.Max(500, speedNow);
                    double altitude = Math.Abs(FootprintAeroGrid.HeightFractions[row] - heightNow);
                    double score = range + 2 * altitude - (candidate == glideGridAoa ? 0.1 : 0);
                    if (score >= best) continue;
                    best = score; pitch = candidate; chosen = i; selected = grid;
                }
            }
            if (selected == null) return;
            int hi = chosen / FootprintAeroGrid.Speeds.Length;
            double heightSample = body.atmosphereDepth * FootprintAeroGrid.HeightFractions[hi];
            double pressureSample = body.GetPressure(heightSample);
            double densitySample = glideNodeDensity[hi];
            if (densitySample <= 0) return;
            double speed = FootprintAeroGrid.Speeds[chosen % FootprintAeroGrid.Speeds.Length];
            Vector3d scaledVelocity = velocity * (speed / speedNow);
            var pointing = AttitudeGuidance.BankedCoastPointing(AttitudeCoordinates(-velocity.normalized),
                AttitudeCoordinates(up), LocalNorth(up), 0, pitch, out AttitudeVector forward);
            if (!farCoast.TryFootprintAero(vessel, frame, scaledVelocity, up, WorldDirection(pointing), heightSample,
                pressureSample, out FootprintAero sample, WorldDirection(forward))) return;
            double scale = pressureSample / densitySample;
            selected.Values[chosen] = new FootprintAero(sample.Drag * scale, sample.Lift * scale, sample.Cross * scale, sample.Far);
            selected.Count++;
            selected.Snapshot = selected.Count >= 4 ? new FootprintAeroGrid(glideNodeDensity, selected.Values) : null;
        }

        private FootprintAtmosphere SampledFootprintAtmosphere(double depth, double[] pressure,
            FootprintAero retro, FootprintAero tilted, bool hasTilt, double pitch)
        {
            glideAeroGrids.TryGetValue(0, out AeroGridCapture baseline);
            glideAeroGrids.TryGetValue(pitch, out AeroGridCapture alternative);
            return new FootprintAtmosphere(depth, pressure, retro, tilted, hasTilt, glideDensity,
                baseline?.Snapshot, alternative?.Snapshot);
        }
    }
}
