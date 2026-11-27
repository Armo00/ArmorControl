using System;
using System.Diagnostics;
using System.Threading;

namespace LandingPoc
{
    internal readonly struct FootprintAero
    {
        internal readonly double Drag, Lift, Cross;
        internal readonly bool Valid, Far;

        internal FootprintAero(double drag, double lift, double cross, bool far)
        {
            Drag = drag;
            Lift = lift;
            Cross = cross;
            Valid = true;
            Far = far;
        }
    }

    internal readonly struct FootprintGlideSteering
    {
        internal readonly double BankDegrees, RollSeconds;

        internal FootprintGlideSteering(double bankDegrees, double rollRate, double currentBank = 0)
        {
            BankDegrees = bankDegrees;
            RollSeconds = Math.Abs(AttitudeGuidance.WrapDegrees(bankDegrees - currentBank)) / rollRate;
        }
    }

    // Force coefficients are divided by pressure and speed squared. The
    // immutable lookup is sampled on the KSP thread and used off-thread.
    internal sealed class FootprintAtmosphere
    {
        private readonly double depth;
        private readonly double[] pressure;
        private readonly FootprintAero retrograde, pitched;
        private readonly bool pitchedAvailable;
        private readonly double[] relativeDensity;
        private readonly FootprintAeroGrid retrogradeGrid, pitchedGrid, blendedGrid;
        private readonly double gridBlend;

        internal FootprintAtmosphere(double depth, double[] pressure, FootprintAero retrograde,
                                     FootprintAero pitched, bool pitchedAvailable, double[] relativeDensity = null,
                                     FootprintAeroGrid retrogradeGrid = null, FootprintAeroGrid pitchedGrid = null,
                                     FootprintAeroGrid blendedGrid = null, double gridBlend = 0)
        {
            this.depth = depth;
            this.pressure = pressure;
            this.retrograde = retrograde;
            this.pitched = pitched;
            this.pitchedAvailable = pitchedAvailable;
            this.relativeDensity = relativeDensity;
            this.retrogradeGrid = retrogradeGrid;
            this.pitchedGrid = pitchedGrid;
            this.blendedGrid = blendedGrid;
            this.gridBlend = gridBlend;
        }

        internal double Depth => depth;
        internal bool IsAtmospheric => depth > 0;
        internal bool UsesFar => retrograde.Far && (!pitchedAvailable || pitched.Far);
        internal bool HasPitchSample => !IsAtmospheric || (pitchedAvailable && pitched.Valid);

        internal FootprintAtmosphere WithPitchSample(FootprintAero sample) =>
            sample.Valid ? new FootprintAtmosphere(depth, pressure, retrograde, sample, true, relativeDensity, retrogradeGrid) : null;

        internal FootprintAtmosphere BlendPitch(FootprintAtmosphere boundary, double fraction)
        {
            if (boundary == null || depth != boundary.depth || !HasPitchSample || !boundary.HasPitchSample)
                return null;
            if (!IsAtmospheric) return this;
            FootprintAero other = boundary.pitched;
            var blended = new FootprintAero(
                pitched.Drag + fraction * (other.Drag - pitched.Drag),
                pitched.Lift + fraction * (other.Lift - pitched.Lift),
                pitched.Cross + fraction * (other.Cross - pitched.Cross), pitched.Far && other.Far);
            return new FootprintAtmosphere(depth, pressure, retrograde, blended, true, relativeDensity, retrogradeGrid,
                boundary.pitchedGrid == null ? null : pitchedGrid, boundary.pitchedGrid, fraction);
        }

        internal bool Acceleration(double altitude, double horizontal, double vertical, double mass,
                                   bool pitch, out double along, out double up, out double cross, double bankDegrees = 0)
        {
            along = up = cross = 0;
            double speedSquared = horizontal * horizontal + vertical * vertical;
            if (depth <= 0 || altitude >= depth || speedSquared < 4) return true;
            if (pitch && !pitchedAvailable) return false;
            FootprintAero coefficients = pitch ? pitched : retrograde;
            if (!coefficients.Valid) return true;
            double at = Math.Max(0, altitude) * (pressure.Length - 1) / depth;
            int index = Math.Min(pressure.Length - 2, (int)at);
            double fraction = at - index;
            double q = (pressure[index] + fraction * (pressure[index + 1] - pressure[index])) * speedSquared / mass;
            double speed = Math.Sqrt(speedSquared);
            var grid = pitch ? pitchedGrid : retrogradeGrid;
            if (grid != null && relativeDensity != null)
            {
                double density = relativeDensity[index] + fraction * (relativeDensity[index + 1] - relativeDensity[index]);
                FootprintAero adjacent = default;
                if (grid.TrySample(speed, density, out FootprintAero lookup) &&
                    (!pitch || blendedGrid == null || blendedGrid.TrySample(speed, density, out adjacent)))
                {
                    coefficients = pitch && blendedGrid != null ? FootprintAeroGrid.Mix(lookup, adjacent, gridBlend) : lookup;
                    q = density * speedSquared / mass;
                }
            }
            double lift = coefficients.Lift, side = coefficients.Cross;
            if (pitch && bankDegrees != 0)
            {
                // Roll about the relative wind, then pitch in the rolled plane.
                // The transverse force rotates with the pitch plane.
                double angle = bankDegrees * AttitudeGuidance.Degree;
                lift = coefficients.Lift * Math.Cos(angle) - coefficients.Cross * Math.Sin(angle);
                side = coefficients.Lift * Math.Sin(angle) + coefficients.Cross * Math.Cos(angle);
            }
            along = q * (-coefficients.Drag * horizontal - lift * vertical) / speed;
            up = q * (-coefficients.Drag * vertical + lift * horizontal) / speed;
            cross = q * side;
            return Finite(along) && Finite(up) && Finite(cross);
        }

        internal static bool Finite(double x) => !double.IsNaN(x) && !double.IsInfinity(x);
    }

    internal sealed class GlideCheckpoint
    {
        internal Flight Flight;
        internal Engine[] Engines;
        internal double Along, Cross, Seconds, HeadingX, HeadingY, Gravity, EntrySeconds, EntryDeltaV;
    }

    internal struct TouchdownEstimate
    {
        internal string Status;
        internal GlideCheckpoint GlideStart;
        internal double Along, Cross, Seconds, EntrySeconds, LandingSeconds, TouchdownMass;
        internal double GlideRollSeconds, GlidePitchSeconds, EntryDeltaV;
        internal bool HasPoint, UsesFar, Atmospheric, EntryCompleted;
    }

    public sealed partial class Controller
    {
        // Prediction owns the controller state and reads no game objects.
        private bool footprintSimulation;

        internal Func<CancellationToken, TouchdownEstimate> CaptureFootprint(
            Flight flight, FootprintAtmosphere atmosphere, double radius, FootprintGlideSteering glide = default,
            FootprintAtmosphere entryAtmosphere = null, bool screenOnly = false, bool captureGlide = false)
        {
            Engine[] engines = (Engine[])specs.Clone();
            Settings settings = cfg.Copy();
            Torque control = backupTorque;
            Plan currentPlan = plan;
            Phase currentPhase = phase;
            long currentBank = selected, shutdownMask = relightShutdownMask;
            double entryAltitude = previousEntryAltitude;
            string entryExit = entryExitReason;
            int relightLimit = relightBankLimit;
            bool committed = CappedFinalCommitted, relightUsed = usedAlignmentRelight;
            double entryTilt = EntryTiltDegrees;
            var masks = new long[cachedMasks.Count];
            cachedMasks.CopyTo(masks);
            flight.AeroForecast = null; // The live FAR forecast may still be growing.
            flight.EngineThrustAccelerations = null;
            flight.IgnitionElapsed = flight.IgnitionElapsed == null ? null : (double[])flight.IgnitionElapsed.Clone();
            flight.Propellant = flight.Propellant?.ForForecast();

            return stop =>
            {
                stop.ThrowIfCancellationRequested();
                var clone = new Controller(engines, settings, control)
                {
                    phase = currentPhase,
                    selected = currentBank,
                    previousEntryAltitude = entryAltitude,
                    plan = currentPlan,
                    entryExitReason = entryExit,
                    CappedFinalCommitted = committed,
                    relightShutdownMask = shutdownMask,
                    relightBankLimit = relightLimit,
                    usedAlignmentRelight = relightUsed,
                    footprintSimulation = true,
                    EntryTiltDegrees = entryTilt
                };
                clone.cachedMasks.UnionWith(masks);
                if (currentBank != 0) clone.cachedMasks.Add(currentBank);
                if (currentPlan.Main != null) clone.cachedMasks.Add(currentPlan.Main.Mask);
                if (currentPlan.Final != null) clone.cachedMasks.Add(currentPlan.Final.Mask);
                return clone.TraceFootprint(flight, atmosphere, radius, glide, entryAtmosphere, screenOnly, captureGlide, stop);
            };
        }

        private TouchdownEstimate TraceFootprint(Flight s, FootprintAtmosphere air, double radius,
                                                FootprintGlideSteering glide, FootprintAtmosphere entryAir,
                                                bool screenOnly, bool captureGlide, CancellationToken stop)
        {
            var result = new TouchdownEstimate { Status = "no_plan", UsesFar = air.UsesFar, Atmospheric = air.IsAtmospheric };
            if (s.SolidBurnActive || s.Contact || phase == Phase.Yolo || s.Mass <= 0 || radius <= 0)
                return result;

            double ground = s.SeaLevelAltitude - s.Height;
            double gravityParameter = cfg.Gravity * Math.Pow(radius + s.SeaLevelAltitude, 2);
            double originalTime = s.UniversalTime ?? 0;
            double vx = s.SurfaceHorizontalSpeed ?? s.Vx, vy = 0;
            double headingX = 1, headingY = 0;
            double thrustX = s.LateralThrustAcceleration, thrustUp = s.VerticalThrustAcceleration;
            double pitch = Math.Atan2(thrustX, Math.Max(0.01, thrustUp));
            double nextAero = 0;
            bool previouslyRolling = false;
            var start = new FootprintIgnition(specs, s);
            var watch = Stopwatch.StartNew();

            while (result.Seconds < 210)
            {
                stop.ThrowIfCancellationRequested();
                if (watch.ElapsedMilliseconds > 200)
                {
                    result.Status = "horizon";
                    return result;
                }
                double dt = phase == Phase.Coast && s.Height > 500 ? (screenOnly ? 0.75 : 0.25) : 0.05;
                cfg.SampleSeconds = dt;
                double horizontal = Norm(vx, vy);
                if (horizontal > 0.02) { headingX = vx / horizontal; headingY = vy / horizontal; }
                s.Vx = horizontal;
                s.SurfaceHorizontalSpeed = horizontal;
                s.SurfaceSpeed = Norm(horizontal, s.Vz);
                s.UniversalTime = originalTime + result.Seconds;
                s.SeaLevelAltitude = ground + s.Height;
                double radial = Math.Max(radius, radius + s.SeaLevelAltitude);
                cfg.Gravity = Math.Max(0.01, gravityParameter / (radial * radial) - horizontal * horizontal / radial);
                bool coasting = phase == Phase.Coast;
                bool entryCrossing = coasting && previousEntryAltitude > cfg.EntryBurnAltitude &&
                    s.SeaLevelAltitude <= cfg.EntryBurnAltitude && s.Vz < 0 && s.SurfaceSpeed > cfg.EntryBurnSpeed;
                bool glideWindow = coasting && !entryCrossing && s.SeaLevelAltitude < cfg.EntryBurnAltitude;
                bool rolling = glideWindow && result.GlideRollSeconds + 1e-8 < glide.RollSeconds;
                if (rolling != previouslyRolling) nextAero = 0;
                previouslyRolling = rolling;
                bool pitched = glideWindow && !rolling && cfg.CoastPitchAoaDegrees != 0;
                bool tiltedEntry = entryAir != null && (entryCrossing || phase == Phase.EntryBurn) && horizontal >= 1;
                var currentAir = tiltedEntry ? entryAir : air;
                if (!currentAir.Acceleration(s.SeaLevelAltitude, horizontal, s.Vz, s.Mass, pitched || tiltedEntry,
                    out double aeroX, out double aeroUp, out double aeroCross, tiltedEntry ? 0 : glide.BankDegrees))
                {
                    result.Status = "waiting_aero";
                    return result;
                }
                s.ExternalLateralAcceleration = coasting ? 0 : aeroX;
                s.ExternalUpAcceleration = coasting ? 0 : aeroUp;
                s.CrossExternalAcceleration = aeroCross;
                s.CoastAccelerationMeasured = coasting;
                s.CoastLateralAcceleration = aeroX;
                s.CoastUpAcceleration = aeroUp;
                s.CoastDragRate = 0;
                s.LateralThrustAcceleration = thrustX;
                s.VerticalThrustAcceleration = thrustUp;
                s.CrossThrustAcceleration = 0;
                s.LateralThrustDirection = Math.Sin(pitch);
                s.AttitudeRateMeasured = false;
                s.EngineThrustMeasured = false;
                if (captureGlide && coasting && result.EntryCompleted && result.GlideStart == null &&
                    (s.Running | s.Starting | s.IssuedStarts) == 0)
                {
                    Flight checkpoint = s;
                    checkpoint.Propellant = s.Propellant?.ForForecast();
                    checkpoint.AeroForecast = null;
                    result.GlideStart = new GlideCheckpoint
                    {
                        Flight = checkpoint, Engines = start.CaptureEngines(), Gravity = gravityParameter / (radial * radial),
                        Along = result.Along, Cross = result.Cross, Seconds = result.Seconds,
                        HeadingX = headingX, HeadingY = headingY, EntrySeconds = result.EntrySeconds, EntryDeltaV = result.EntryDeltaV
                    };
                }

                // There is no landing ignition command allowed far above the
                // ceiling. The Entry altitude crossing still enters guidance.
                bool skipPlanning = coasting && s.Running == 0 && s.Starting == 0 &&
                    cfg.IgnitionAltitudeBlocked(s.Height) &&
                    s.Height > cfg.MaxIgnitionAltitude + Math.Max(1000, -s.Vz * 15) &&
                    !(previousEntryAltitude > cfg.EntryBurnAltitude && s.SeaLevelAltitude <= cfg.EntryBurnAltitude);
                if (coasting)
                {
                    s.AlignmentSeconds = rolling ? 0 : Math.Abs(cfg.CoastPitchAoaAt(s.SeaLevelAltitude)) / 20;
                    if (!skipPlanning && !screenOnly && air.IsAtmospheric && result.Seconds >= nextAero)
                    {
                        double mass = s.Mass;
                        s.AeroForecast = AeroCoastForecast.Integrate(s, cfg.Gravity,
                            (double h, double x, double z, out double sampleX, out double sampleUp) =>
                                air.Acceleration(ground + h, x, z, mass, !rolling && cfg.CoastPitchAoaAt(ground + h) != 0,
                                    out sampleX, out sampleUp, out _, glide.BankDegrees), 20);
                        nextAero = result.Seconds + 1;
                        s.AeroForecastAge = 0;
                    }
                }
                else
                {
                    s.AeroForecast = null;
                    nextAero = 0;
                    s.AlignmentSeconds = 0;
                }

                if (phase == Phase.EntryBurn && result.EntrySeconds > 0 && s.SurfaceSpeed <= cfg.EntryBurnSpeed)
                    result.EntryCompleted = true;
                if (entryCrossing && horizontal >= 1 && EntryTiltDegrees != 0)
                    s.AlignmentSeconds = Math.Max(s.AlignmentSeconds, Math.Abs(EntryTiltDegrees) / 30);
                Command command;
                if (skipPlanning || (screenOnly && coasting && !entryCrossing))
                {
                    previousEntryAltitude = s.SeaLevelAltitude;
                    command = new Command { Phase = Phase.Coast };
                }
                else command = Tick(s);
                if (command.Phase == Phase.Yolo) return result;

                // At the Entry crossing the guidance switches back to retrograde.
                if (pitched && command.Phase == Phase.EntryBurn &&
                    !air.Acceleration(s.SeaLevelAltitude, horizontal, s.Vz, s.Mass, false, out aeroX, out aeroUp, out aeroCross))
                    return result;

                start.Update(command, result.Seconds, ref s);
                var bank = s.Running == 0 ? null : LiveGroup(s.Running);
                double desiredX = 0, desiredUp = 0;
                if (bank != null)
                {
                    double x = command.X, up = command.Up;
                    if (command.UsesTerminalGuidance)
                    {
                        x -= aeroX;
                        up = Math.Max(0.01, up - aeroUp);
                        double tilt = AttitudeGuidance.FinalTiltDegrees(AttitudeGuidance.FinalLateralDemand(horizontal),
                            FinalTimeToContact(s, cfg)) * AttitudeGuidance.Degree;
                        x = Math.Max(-up * Math.Tan(tilt), Math.Min(up * Math.Tan(tilt), x));
                    }
                    double direction = Math.Atan2(x, up);
                    double ceiling = Limit(bank, s, true, command.Phase) / s.Mass;
                    double floor = bank.Min / s.Mass;
                    if (ceiling < floor) return result;
                    double force = Math.Max(floor, Math.Min(ceiling, Norm(x, up)));
                    desiredX = force * Math.Sin(direction);
                    desiredUp = force * Math.Cos(direction);
                }
                // Approximate actuation between guidance samples. The cold
                // start itself uses the captured per-engine spool times.
                double response = Math.Min(1, dt / (bank == null ? 0.10 : 0.16));
                thrustX += (desiredX - thrustX) * response;
                thrustUp += (desiredUp - thrustUp) * response;
                pitch = Math.Atan2(thrustX, Math.Max(0.01, thrustUp));
                s.UprightAngle = Math.Abs(pitch);
                s.TerminalAlignmentSeconds = s.UprightAngle / (Math.PI / 6);
                double alongAccel = thrustX + aeroX;
                double ax = alongAccel * headingX - aeroCross * headingY;
                double ay = alongAccel * headingY + aeroCross * headingX;
                double az = thrustUp + aeroUp - cfg.Gravity;
                double nextHeight = s.Height + dt * (s.Vz + 0.5 * dt * az);
                double elapsed = nextHeight <= 0 ? dt * s.Height / Math.Max(1e-9, s.Height - nextHeight) : dt;
                double groundScale = radius / radial;
                result.Along += groundScale * elapsed * (vx + 0.5 * elapsed * ax);
                result.Cross += groundScale * elapsed * (vy + 0.5 * elapsed * ay);
                vx += ax * elapsed;
                vy += ay * elapsed;
                s.Vz += az * elapsed;
                s.Height = nextHeight;
                result.Seconds += elapsed;
                s.AeroForecastAge += elapsed;
                if (command.Phase == Phase.Coast && rolling) result.GlideRollSeconds += elapsed;
                if (command.Phase == Phase.Coast && pitched) result.GlidePitchSeconds += elapsed;
                if (bank != null)
                {
                    double acceleration = Norm(thrustX, thrustUp);
                    if (command.Phase == Phase.EntryBurn)
                    {
                        result.EntrySeconds += elapsed;
                        result.EntryDeltaV += acceleration * elapsed;
                    }
                    if (command.Phase == Phase.Main || command.Phase == Phase.Final)
                        result.LandingSeconds += elapsed;
                    double consumed = s.Mass - MassAfter(s.Mass, acceleration, elapsed, bank);
                    if (s.Propellant != null && !s.Propellant.ConsumeForecast(bank.Mask, consumed))
                        return result;
                    s.Mass -= consumed;
                }
                if (nextHeight <= 0)
                {
                    if (screenOnly)
                    {
                        // The cheap pass ranks glide endpoints. It never certifies a landing.
                        result.HasPoint = result.EntryCompleted;
                        result.Status = "screened";
                        result.TouchdownMass = s.Mass;
                        return result;
                    }
                    result.HasPoint = result.LandingSeconds > 0;
                    result.TouchdownMass = s.Mass;
                    if (result.HasPoint)
                        result.Status = s.Vz < -Math.Max(3, 2 * cfg.TouchSpeed) || Norm(vx, vy) > Math.Max(3, 2 * cfg.TouchSpeed)
                            ? "hard_contact" : "estimated";
                    return result;
                }
            }
            result.Status = "horizon";
            return result;
        }

        private sealed class FootprintIgnition
        {
            private readonly Engine[] engines;
            private readonly double[] readyAt;
            private readonly int[] starts;
            private long commanded;
            internal FootprintIgnition(Engine[] engines, Flight s)
            {
                this.engines = engines;
                readyAt = new double[engines.Length];
                starts = new int[engines.Length];
                commanded = s.Running | s.IssuedStarts;
                for (int i = 0; i < engines.Length; i++)
                {
                    long bit = 1L << i;
                    starts[i] = engines[i].Ignitions;
                    if ((s.IssuedStarts & bit & ~s.Running) != 0)
                    {
                        double elapsed = s.IgnitionElapsed == null ? 0 : Math.Max(0, s.IgnitionElapsed[i]);
                        readyAt[i] = Math.Max(0, engines[i].SpoolSeconds - elapsed);
                    }
                }
            }

            internal Engine[] CaptureEngines()
            {
                var copy = new Engine[engines.Length];
                for (int i = 0; i < copy.Length; i++)
                    copy[i] = engines[i].WithIgnitions(starts[i]);
                return copy;
            }

            internal void Update(Command command, double now, ref Flight s)
            {
                long requested = command.Engines;
                long additions = requested & ~commanded;
                s.Running &= requested;
                s.Starting = 0;
                for (int i = 0; i < engines.Length; i++)
                {
                    long bit = 1L << i;
                    if ((requested & bit) == 0) continue;
                    if ((additions & bit) != 0)
                    {
                        if (starts[i] == 0 || !EngineResponse.Known(engines[i].SpoolSeconds))
                            readyAt[i] = double.PositiveInfinity;
                        else
                        {
                            readyAt[i] = now + engines[i].SpoolSeconds + command.IgnitionAlignmentSeconds +
                                ((s.Unsettled & bit) != 0 ? s.UllageSeconds : 0);
                            if (starts[i] > 0 && --starts[i] == 0) s.Restartable &= ~bit;
                        }
                    }
                    if (now >= readyAt[i]) s.Running |= bit;
                    else s.Starting |= bit;
                }
                s.EstablishedThrust = s.Running;
                s.IssuedStarts = s.Starting;
                commanded = requested;
            }
        }
    }
}
