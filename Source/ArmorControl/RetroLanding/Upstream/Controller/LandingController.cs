using System;
using System.Collections.Generic;
using Group = LandingPoc.EngineGroup;

namespace LandingPoc
{
    // Planar KSP test model. Use t, kN, m and seconds throughout.
    public enum Phase { Coast, Main, Final, Recoast, Yolo, Landed, EntryBurn, Boostback }

    public sealed class Engine
    {
        public readonly double X, Y, Min, Max, SpoolSeconds;
        public readonly int Ignitions;
        public readonly bool StopAllowed, Gimbal;
        internal readonly Torque Moment, Authority, Axis;
        internal readonly bool QuantifiedControl, HasMount;
        internal readonly double SpecificImpulse;

        public Engine(double x, double y, double min, double max, int ignitions, double spoolSeconds,
                      bool stopAllowed = true, bool gimbal = true)
            : this(x, y, min, max, ignitions, spoolSeconds, stopAllowed, gimbal, new Torque(-y, 0, x), new Torque(), null)
        {
        }

        internal Engine WithIgnitions(int remaining) =>
            new Engine(X, Y, Min, Max, remaining, SpoolSeconds, StopAllowed, Gimbal, Moment, Authority,
                QuantifiedControl ? (Torque?)Axis : null, SpecificImpulse, HasMount);

        internal Engine(double x, double y, double min, double max, int ignitions, double spoolSeconds,
                        bool stopAllowed, bool gimbal, Torque moment, Torque authority, Torque? axis = null,
                        double specificImpulse = 0, bool hasMount = true)
        {
            X = x; Y = y; Min = min; Max = max;
            Ignitions = ignitions; SpoolSeconds = spoolSeconds; StopAllowed = stopAllowed; Gimbal = gimbal;
            Moment = moment; Authority = authority;
            Axis = axis ?? new Torque(0, 1, 0);
            QuantifiedControl = axis.HasValue;
            HasMount = hasMount;
            SpecificImpulse = specificImpulse > 0 && !double.IsInfinity(specificImpulse) ? specificImpulse : 0;
        }
    }

    public sealed class Settings
    {
        public const double G0 = 9.80665;
        public const double InitialPlanningThrottle = 0.9;
        internal const double MinimumPositiveThrottle = 0.001;
        public const double IgnitionToleranceDegrees = 10;
        public const double TerminalTurnBraking = 0.11;
        public double MaxG = 4;
        public double BoostbackMaxG = double.NaN, EntryMaxG = double.NaN;
        public double BoostbackStartAoa = 110, BoostbackFineDistance = 15000;

        internal double ThrustLimitFor(Phase phase)
        {
            if (phase == Phase.Boostback && !double.IsNaN(BoostbackMaxG)) return BoostbackMaxG;
            if (phase == Phase.EntryBurn && !double.IsNaN(EntryMaxG)) return EntryMaxG;
            return MaxG;
        }
        public double CoastPitchAoaDegrees;
        public double VesselTargetEastOffset, VesselTargetNorthOffset;
        public double CoastPitchAoaMinimum = double.NaN, CoastPitchAoaMaximum = double.NaN;
        internal double MinimumCoastPitch => double.IsNaN(CoastPitchAoaMinimum) ? CoastPitchAoaDegrees : CoastPitchAoaMinimum;
        internal double MaximumCoastPitch => double.IsNaN(CoastPitchAoaMaximum) ? CoastPitchAoaDegrees : CoastPitchAoaMaximum;
        public double MaxIgnitionAltitude = 8000;
        public double EntryBurnAltitude = 65000;
        public double EntryBurnSpeed = 1300;
        public double Gravity = 9.81;
        public double SpecificImpulse = 300;
        public double TouchSpeed = 1;
        public double PrecisionSeconds = 4;
        public double AttitudeResponseSeconds;
        public double SampleSeconds = 0.02;
        public double GearDeploymentSeconds = 1.0;
        public double GearSpeedLimit = 70;
        public double GearCommandAdvanceSeconds;
        public bool OptimizeFuel;
        public bool OptimizeRecoveryTrajectory;

        internal Settings Copy() => (Settings)MemberwiseClone();

        internal bool IgnitionAltitudeBlocked(double clearance)
        {
            return MaxIgnitionAltitude > 0 && clearance > MaxIgnitionAltitude;
        }

        internal double CoastPitchAoaAt(double seaLevelAltitude)
        {
            return seaLevelAltitude < EntryBurnAltitude ? CoastPitchAoaDegrees : 0;
        }
    }

    public struct Flight
    {
        public double Height, Vx, Vz, Mass, SeaLevelAltitude, SurfaceSpeed;
        internal double? UniversalTime;
        public double LateralThrustAcceleration, VerticalThrustAcceleration, LateralThrustDirection;
        internal double? SurfaceHorizontalSpeed;
        internal double CrossThrustAcceleration, CrossExternalAcceleration;
        internal AttitudeVector[] EngineThrustAccelerations;
        public double LateralVelocityLookahead;
        public double UprightAngle, TerminalAlignmentSeconds;
        public double PitchRate, MeasuredTurnRate, MeasuredTurnAcceleration;
        public double LateralAxisRate, UpAxisRate;
        public bool AttitudeRateMeasured;
        public double CoastUpAcceleration, CoastLateralAcceleration;
        public bool CoastAccelerationMeasured;
        public double CoastDragRate;
        internal AeroCoastForecast AeroForecast;
        internal double AeroForecastAge;
        public double ExternalLateralAcceleration, ExternalUpAcceleration;
        public long Running, Starting, Restartable, Unsettled;
        public double AlignmentSeconds, UllageSeconds;
        public bool Contact, GearCommanded, SolidBurnActive;
        internal double[] IgnitionElapsed;
        internal long IssuedStarts;
        internal long EstablishedThrust;
        internal bool EngineThrustMeasured;
        internal PropellantBudget Propellant;
    }

    public struct Command
    {
        public Phase Phase;
        public long Engines;
        public double X, Up, IgnitionLead;
        public double IgnitionAlignmentSeconds;
        internal double BoostbackIgnitionAngle;
        internal bool PrepareBoostbackUllage;
        public bool DeployGear, Emergency, VerticalOnly;
        public string Reason;
        public string YoloTrigger;
        internal string YoloInhibit;
        internal string YoloState;
        internal bool YoloTerminalGuidance;
        internal bool UsesTerminalGuidance { get { return Phase == Phase.Final || YoloTerminalGuidance; } }
        public double CappedPlanMargin, PlannedTerminalSeconds;
        public double PlannedJoinHeight, PlannedJoinSpeed;
        internal double PlannedJoinMass, PlannedFinalThrottle;
        internal double PlannedIgnitionWait;
        internal double PlannedPropellantKg;
        internal double PropellantShortfallKg;
        internal bool FuelPlanSelected;
    }

    public sealed partial class Controller
    {
        public const int MaxMonitoredEngines = EngineModuleCatalog.Capacity;

        private struct Plan
        {
            public Group Main, Final;
            public double JoinHeight, JoinSpeed, Wait, TerminalSeconds;
            public double RequestedTerminalSeconds;
            public double Propellant, TerminalThrottleFraction;
            public double JoinMass, JoinThrottle;
            public double RelightX, RelightUp;
            public bool FadeMainLift;
            public bool ImpendingRelightGate;
            public bool FuelOptimized;
        }

        private struct CoastSearch
        {
            internal Flight Flight;
            internal Plan Plan;
            internal double MaxG, Altitude, Precision, Margin, Shortfall;
            internal bool Optimize;
        }

        private struct MainSearchCandidate
        {
            internal Group Group;
            internal double Acceleration, Lead, Latest;
        }

        private struct IgnitionProfile : IEquatable<IgnitionProfile>
        {
            internal double Acceleration, Lead;
            internal bool Reducing;

            public bool Equals(IgnitionProfile other) => Acceleration == other.Acceleration &&
                Lead == other.Lead && Reducing == other.Reducing;

            public override bool Equals(object obj) => obj is IgnitionProfile other && Equals(other);

            public override int GetHashCode() => unchecked((Acceleration.GetHashCode() * 397 ^
                Lead.GetHashCode()) * 397 ^ Reducing.GetHashCode());
        }

        private struct IgnitionWindow
        {
            internal bool Feasible;
            internal double First, Last;
        }

        private readonly Settings cfg;
        private readonly List<MainSearchCandidate> mainSearchCandidates = new List<MainSearchCandidate>();
        private readonly Dictionary<IgnitionProfile, IgnitionWindow> ignitionWindows =
            new Dictionary<IgnitionProfile, IgnitionWindow>();
        private Engine[] specs;
        private Torque backupTorque;
        private long catalogAvailable, catalogRunning;
        private readonly long[] editorMasks;
        private readonly long fixedEngines;
        private Phase phase = Phase.Coast;
        private double previousEntryAltitude = double.NaN;
        private string entryExitReason;
        private long relightShutdownMask;
        private int relightBankLimit = int.MaxValue;
        private long selected;
        private Plan plan;
        private CoastSearch? coastSearch;
        private bool emergency, verticalOnly, usedAlignmentRelight;
        internal bool CappedFinalCommitted { get; set; }
        private string yoloTrigger;
        private string yoloInhibit;
        private string yoloState;
        private bool yoloTerminalGuidance;
        private const double EmergencyMinimumDescentSpeed = 10;
        private double cappedPlanMargin;
        private double propellantShortfall;
        private long emergencyGroup;
        private bool awaitingEmergencyStarts;
        private double mainStartX, mainStartUp;
        private const double HorizontalTolerance = 0.6;
        internal const double MainHandoffHorizontalRatio = 0.15;
        private const double TerminalTiltTangent = 0.2;
        private const double MainSideTrimRadians = 20 * Math.PI / 180;
        private static readonly double[] TerminalDurationFactors = { 1, 0.75, 1.25, 0.5, 1.5, 1.75, 2 };
        private static readonly double[] AlternativeTerminalThrottle = { 0.25, 0.75, 0.125, 0.875 };
        private static readonly double[] FuelDurationFactors = { 0.65, 0.8, 1, 1.2 };
        private static readonly double[] FuelTerminalThrottles = { 0.5, 0.75, 0.875 };
        private const double FuelComparisonLookahead = 4;

        public Controller(Engine[] engines, Settings settings)
            : this(engines, settings, new Torque(), null)
        {
        }

        public Controller(Engine[] engines, Settings settings, IList<int[]> families)
            : this(engines, settings, new Torque(), families)
        {
        }

        internal Controller(Engine[] engines, Settings settings, Torque backup, IList<int[]> families = null,
                            bool backgroundCatalog = false)
        {
            if (engines.Length > MaxMonitoredEngines) throw new ArgumentOutOfRangeException("engines");
            cfg = settings;
            this.backgroundCatalog = backgroundCatalog;
            for (int i = 0; i < engines.Length; i++)
                if (!engines[i].StopAllowed) fixedEngines |= 1L << i;
            catalogAvailable = EngineModuleCatalog.All(engines.Length);
            editorMasks = EngineModuleCatalog.EditorFamilyMasks(engines.Length, families);
            catalogBuilder = new EngineGroups(engines, backup);
            priorityMasks = catalogBuilder.PriorityMasks(editorMasks);
            cachedMasks.UnionWith(priorityMasks);
            RefreshThrust(engines, backup);
        }

        internal Engine[] CaptureEngineMeasurements() => (Engine[])specs.Clone();

        internal bool CanUseBoostbackBank(long mask, Flight flight, out double acceleration)
        {
            bool usable = TryBoostbackLimits(mask, flight, out _, out double maximum);
            acceleration = usable ? maximum / flight.Mass : 0;
            return usable;
        }

        internal bool TryBoostbackLimits(long mask, Flight flight, out double minimum, out double maximum)
        {
            minimum = maximum = 0;
            Group bank = LiveGroup(mask);
            if (bank == null || !Eligible(bank, flight) || (mask & fixedEngines) != 0 ||
                double.IsPositiveInfinity(EngineStartDelay(mask, flight))) return false;
            minimum = MinimumRunningThrust(bank);
            maximum = Limit(bank, flight, true, Phase.Boostback);
            return maximum > 0 && maximum >= minimum;
        }

        internal double BoostbackSpoolSeconds(long mask, Flight flight) => EngineStartDelay(mask, flight);

        internal void ObserveBoostbackAltitude(double altitude)
        {
            // Retain an above-entry sample until the first normal guidance tick.
            if (altitude > cfg.EntryBurnAltitude) previousEntryAltitude = altitude;
        }

        // The caller preserves engine indexes for the life of a flight.
        public void RefreshThrust(Engine[] engines)
        {
            RefreshThrust(engines, backupTorque);
        }

        internal void RefreshThrust(Engine[] engines, Torque backup)
        {
            specs = engines;
            backupTorque = backup;
            catalogBuilder = catalogBuilder.WithMeasurements(engines, backup);
            liveByMask.Clear();
            groupsDirty = true;
        }

        private static bool Subset(long subset, long all) { return (subset & all) == subset; }
        private static int CompareGroups(Group candidate, Group current)
        {
            int count = candidate.Count.CompareTo(current.Count);
            if (count != 0) return count;
            // Eligibility has already checked live moment reserve. Choose
            // the inner, geometrically balanced bank independently of CoM.
            int placement = Group.InsideFirst(candidate, current);
            return placement != 0 ? placement : Group.CompareBalance(candidate, current);
        }

        private static int ComparePlanGroups(Plan candidate, Plan current)
        {
            int terminal = CompareGroups(candidate.Final, current.Final);
            if (terminal != 0) return terminal;
            // A centered bank that can keep the same terminal engine
            // avoids spending control authority on permanent TVC trim.
            int centered = current.Main.CenteredInstallation.CompareTo(candidate.Main.CenteredInstallation);
            if (centered != 0 || candidate.Main.Count != current.Main.Count) return centered;
            return CompareGroups(candidate.Main, current.Main);
        }

        public static double Norm(double x, double z) { return Math.Sqrt(x * x + z * z); }

        private static double BallisticImpactSeconds(double height, double verticalSpeed, double gravity)
        {
            return (verticalSpeed + Math.Sqrt(verticalSpeed * verticalSpeed +
                2 * Math.Max(0, height) * gravity)) / gravity;
        }

        public static bool CanReachSurface(double height, double verticalSpeed, double horizontalSpeed,
            double horizontalDistanceToEdge, double gravity, double maxAcceleration)
        {
            // Even combining maximum lateral braking with ballistic descent
            // cannot hit a surface we must pass before reaching its height.
            double time = Math.Min(BallisticImpactSeconds(height, verticalSpeed, gravity),
                horizontalSpeed / maxAcceleration);
            return time * (horizontalSpeed - 0.5 * maxAcceleration * time) <= horizontalDistanceToEdge;
        }

        private bool Eligible(Group group, Flight s)
        {
            long available = s.Running | s.Starting | s.Restartable;
            return Subset(group.Mask, available) && Subset(fixedEngines & s.Running, group.Mask);
        }

        private double Limit(Group group, Flight s, bool capped, Phase burn = Phase.Main)
        {
            return capped ? Math.Min(group.Max, cfg.ThrustLimitFor(burn) * Settings.G0 * s.Mass) : group.Max;
        }

        private double PlanningLimit(Group group, Flight s, double throttle)
        {
            // Reserve authority against both the physical throttle range and
            // the thrust-G ceiling, whichever binds first.
            double thrust = group.Min + throttle * (group.Max - group.Min);
            return Math.Min(thrust, throttle * cfg.MaxG * Settings.G0 * s.Mass);
        }

        private double IgnitionLead(long mask, Flight flight)
        {
            return EngineStartDelay(mask, flight) + (Subset(mask, flight.Running) ? 0 : flight.AlignmentSeconds);
        }

        private double EngineStartDelay(long mask, Flight flight)
        {
            long toStart = mask & ~flight.Running;
            if (toStart == 0) return 0;
            double spool = 0;
            for (int i = 0; i < specs.Length; i++)
            {
                if ((toStart & (1L << i)) == 0) continue;
                double partDelay = specs[i].SpoolSeconds;
                if (!EngineResponse.Known(partDelay)) return double.PositiveInfinity;
                spool = Math.Max(spool, partDelay);
            }
            return spool + ((toStart & flight.Unsettled) != 0 ? flight.UllageSeconds : 0);
        }

        private double TerminalReference(Group group, Flight s, double limit)
        {
            return (Math.Max(cfg.Gravity, group.Min / s.Mass) + limit / s.Mass) / 2;
        }

        private double TerminalHeight(double entrySpeed, double seconds)
        {
            return (entrySpeed + cfg.TouchSpeed) / 2 * seconds;
        }

        private double ExhaustSpeed(Group group)
        {
            return (group != null && group.SpecificImpulse > 0 ? group.SpecificImpulse : cfg.SpecificImpulse) * Settings.G0;
        }

        private double MassAfter(double mass, double accel, double seconds, Group group = null)
        {
            return mass * Math.Exp(-accel * seconds / ExhaustSpeed(group));
        }

        private double TerminalTargetForce(Group group, double mass, double throttle)
        {
            double lower = Math.Max(group.Min, cfg.Gravity * mass);
            double upper = Math.Min(group.Max, cfg.MaxG * Settings.G0 * mass);
            if (upper <= lower) return upper;
            double nominal = group.Min + throttle * (group.Max - group.Min);
            // Preserve room for corrections when the G cap or hover force
            // clips the physical throttle range.
            double reserve = 0.5 * Math.Min(throttle, 1 - throttle) * (upper - lower);
            return Math.Max(lower + reserve, Math.Min(upper - reserve, nominal));
        }

        private static double ThrottleForForce(Group group, double force)
        {
            return group.Max > group.Min ? (force - group.Min) / (group.Max - group.Min) : 1;
        }

        private double PlannedThrottleError(Plan plan)
        {
            double target = TerminalTargetForce(plan.Final, plan.JoinMass, plan.TerminalThrottleFraction);
            return Math.Abs(plan.JoinThrottle - ThrottleForForce(plan.Final, target));
        }

        private bool FitsPropellant(Flight s, Group first, double firstTons, Group final = null, double finalTons = 0)
        {
            if (s.Propellant == null) return true;
            double startup = EngineStartDelay(first.Mask, s) * first.Max / ExhaustSpeed(first);
            double shortfall = s.Propellant.Shortfall(first.Mask, firstTons + startup,
                final == null ? 0 : final.Mask, finalTons);
            if (shortfall <= 1e-6) return true;
            propellantShortfall = Math.Min(propellantShortfall, shortfall);
            return false;
        }

        // Minimum duration to reach the terminal vertical speed and cancel the
        // horizontal velocity, assuming constant thrust acceleration.
        private double MainSeconds(double horizontal, double downChange, double maxAccel)
        {
            double b = maxAccel * maxAccel - cfg.Gravity * cfg.Gravity;
            if (b <= 0) return double.PositiveInfinity;
            double c = cfg.Gravity * downChange;
            return (c + Math.Sqrt(c * c + b * (horizontal * horizontal + downChange * downChange))) / b;
        }

        private double CoastMargin(Flight s, double wait, double maxAccel, double joinHeight, double joinSpeed)
        {
            double down = -s.Vz + cfg.Gravity * wait;
            double height = s.Height + s.Vz * wait - 0.5 * cfg.Gravity * wait * wait;
            if (down < joinSpeed)
            {
                double coast = (joinSpeed - down) / cfg.Gravity;
                height -= (down + joinSpeed) * coast / 2;
                down = joinSpeed;
            }
            double burn = MainSeconds(s.Vx, down - joinSpeed, maxAccel);
            return height - joinHeight - (down + joinSpeed) * burn / 2;
        }

        private bool TerminalSegmentFits(Group group, double startMass, double acceleration, double seconds)
        {
            return acceleration * startMass <= Math.Min(group.Max, cfg.MaxG * Settings.G0 * startMass) &&
                   acceleration * MassAfter(startMass, acceleration, seconds, group) >= group.Min;
        }

        private Plan FindPlan(Flight s, long activeMainMask = 0,
                              double planningThrottle = Settings.InitialPlanningThrottle, bool igniteNow = false, List<Group> choices = null)
        {
            // Use the configured terminal estimate first. The solver may
            // move the handoff in either direction to find a continuous burn.
            Plan best = new Plan();
            bool alreadyPowered = activeMainMask != 0;
            if (alreadyPowered) planningThrottle = 1;
            int smallestCount = int.MaxValue;
            double strongest = 0;
            foreach (Group group in choices ?? groups)
                if (Eligible(group, s) && (!alreadyPowered || Subset(group.Mask, s.Running)) &&
                    PlanningLimit(group, s, planningThrottle) > s.Mass * cfg.Gravity && group.Min <= Limit(group, s, true))
                {
                    smallestCount = Math.Min(smallestCount, group.Count);
                    strongest = Math.Max(strongest, PlanningLimit(group, s, planningThrottle));
                }
            if (smallestCount == int.MaxValue) return best;
            foreach (double factor in TerminalDurationFactors)
            {
                int maxCount = best.Final == null ? int.MaxValue : best.Final.Count;
                double seconds = cfg.PrecisionSeconds * factor;
                Plan candidate = FindPlan(s, activeMainMask, seconds, maxCount, 0.5, planningThrottle, igniteNow, choices: choices);
                if (candidate.Main == null && (alreadyPowered || factor == 1))
                {
                    foreach (double throttle in AlternativeTerminalThrottle)
                    {
                        candidate = FindPlan(s, activeMainMask, seconds, maxCount, throttle, planningThrottle, igniteNow, choices: choices);
                        if (candidate.Main != null) break;
                    }
                }
                if (candidate.Main == null) continue;
                int preference = best.Main == null ? -1 : ComparePlanGroups(candidate, best);
                if (preference < 0 ||
                    (preference == 0 && candidate.Wait > best.Wait + 0.004 &&
                     PlanningLimit(candidate.Main, s, planningThrottle) > PlanningLimit(best.Main, s, planningThrottle)))
                    best = candidate;
                if (best.Final.Count == smallestCount &&
                    (alreadyPowered || PlanningLimit(best.Main, s, planningThrottle) >= strongest))
                    break;
            }
            if (alreadyPowered && best.Main == null)
            {
                // A faster-than-planned main burn may still leave a long
                // trajectory near the terminal engine's throttle floor.
                double seconds = Math.Max(cfg.PrecisionSeconds, TravelTime(s, 0, cfg.TouchSpeed));
                foreach (double throttle in AlternativeTerminalThrottle)
                {
                    best = FindPlan(s, activeMainMask, seconds, int.MaxValue, throttle, planningThrottle, igniteNow, choices: choices);
                    if (best.Main != null) break;
                }
            }
            if (cfg.OptimizeFuel && !alreadyPowered && !igniteNow)
            {
                best = RecheckFuelPlan(s, best, planningThrottle);
                if (best.Main != null && best.Wait <= FuelComparisonLookahead && !best.FuelOptimized)
                    best = FindLowerFuelPlan(s, best, planningThrottle);
            }
            best.RequestedTerminalSeconds = cfg.PrecisionSeconds;
            return best;
        }

        private Plan RecheckFuelPlan(Flight s, Plan baseline, double planningThrottle)
        {
            // The original reference can expire before the selected shorter
            // plan reaches ignition. Re-solve the selected recipe with each
            // new observation so that its own start remains available.
            if (!plan.FuelOptimized || plan.RequestedTerminalSeconds != cfg.PrecisionSeconds ||
                (baseline.Main != null && (baseline.Main.Mask != plan.Main.Mask ||
                    baseline.Final.Mask != plan.Final.Mask))) return baseline;
            Plan next = FindPlan(s, 0, plan.TerminalSeconds, plan.Final.Count, plan.TerminalThrottleFraction,
                planningThrottle, false, plan.Main.Mask, plan.Final.Mask);
            if (next.Main == null || !FuelStartFits(s, next) ||
                (baseline.Main != null && next.Propellant >= baseline.Propellant)) return baseline;
            next.FuelOptimized = true;
            return next;
        }

        private Plan FindLowerFuelPlan(Flight s, Plan baseline, double planningThrottle)
        {
            Plan best = baseline;
            foreach (double factor in FuelDurationFactors)
            {
                double seconds = baseline.TerminalSeconds * factor;
                foreach (double throttle in FuelTerminalThrottles)
                {
                    Plan candidate = FindPlan(s, 0, seconds, baseline.Final.Count, throttle, planningThrottle, false,
                        baseline.Main.Mask, baseline.Final.Mask);
                    if (candidate.Main == null || candidate.Propellant >= best.Propellant ||
                        !FuelStartFits(s, candidate)) continue;
                    best = candidate;
                }
            }
            // Ignore changes smaller than half a percent of predicted burn
            // fuel, where small sensor changes could switch the plan back.
            if (best.Propellant >= 0.995 * baseline.Propellant) return baseline;
            best.FuelOptimized = true;
            return best;
        }

        private bool FuelStartFits(Flight s, Plan candidate)
        {
            double seconds = candidate.Wait + IgnitionLead(candidate.Main.Mask, s);
            if (s.AeroForecast != null && seconds > s.AeroForecast.Duration - s.AeroForecastAge) return false;
            Flight atBurn = Coast(s, seconds);
            double x, up;
            return MainVector(atBurn, candidate.JoinHeight, candidate.JoinSpeed, out x, out up,
                candidate.Main.Mask != candidate.Final.Mask) &&
                AimError(s, x, up) <= (Settings.IgnitionToleranceDegrees * Math.PI / 180) +
                    (candidate.Wait + s.AlignmentSeconds) * Math.PI / 9;
        }

        private Plan FindPlan(Flight s, long activeMainMask, double terminalSeconds, int maxCount,
                              double terminalThrottle, double planningThrottle, bool igniteNow,
                              long mainMask = 0, long finalMask = 0, List<Group> choices = null)
        {
            Plan best = SearchPlan(s, activeMainMask, terminalSeconds, maxCount, terminalThrottle, planningThrottle,
                igniteNow, mainMask, finalMask, choices: choices);
            if (best.Main == null) return best;

            // Re-solve the same pair after integrating its main-burn fuel.
            // The mass and handoff state are coupled: changing either changes
            // the remaining main impulse and therefore the other.
            double sizingMass = best.JoinMass;
            double previousMass = s.Mass;
            for (int i = 0; i < 5 && (i == 0 || Math.Abs(sizingMass - previousMass) > 0.0005 * s.Mass); i++)
            {
                Plan corrected = SearchPlan(s, activeMainMask, terminalSeconds, maxCount, terminalThrottle,
                    planningThrottle, igniteNow, best.Main.Mask, best.Final.Mask, sizingMass);
                if (corrected.Main == null)
                {
                    sizingMass = 0.5 * (sizingMass + previousMass);
                    continue;
                }
                if (PlannedThrottleError(corrected) < PlannedThrottleError(best))
                    best = corrected;
                previousMass = sizingMass;
                sizingMass = corrected.JoinMass;
            }
            return best;
        }

        private Plan SearchPlan(Flight s, long activeMainMask, double terminalSeconds, int maxCount,
                                double terminalThrottle, double planningThrottle, bool igniteNow,
                                long mainMask, long finalMask, double joinMass = 0, List<Group> choices = null)
        {
            Plan best = new Plan();
            bool alreadyPowered = activeMainMask != 0;
            bool scheduleCoast = !alreadyPowered && !igniteNow;
            double coastToGround = CoastImpactSeconds(s);
            double altitudeWait = 0;
            if (!alreadyPowered && cfg.IgnitionAltitudeBlocked(s.Height))
            {
                if (igniteNow || cfg.IgnitionAltitudeBlocked(Coast(s, coastToGround).Height)) return best;
                double above = 0, below = coastToGround;
                for (int i = 0; i < 18; i++)
                {
                    double mid = (above + below) / 2;
                    if (cfg.IgnitionAltitudeBlocked(Coast(s, mid).Height)) above = mid;
                    else below = mid;
                }
                altitudeWait = below;
            }
            bool forecastLimited = scheduleCoast && s.Vz < 0 && s.AeroForecast != null &&
                s.AeroForecast.Duration > AeroCoastForecast.LocalHorizonSeconds;
            double forecastEnd = forecastLimited ? s.AeroForecast.Duration - s.AeroForecastAge :
                double.PositiveInfinity;
            // No bank can ignite inside this forecast if the earliest allowed
            // ignition is beyond it, even before counting engine start time.
            if (altitudeWait > forecastEnd) return best;

            mainSearchCandidates.Clear();
            foreach (Group first in PlanGroups(mainMask, choices))
            {
                if ((mainMask != 0 && first.Mask != mainMask) ||
                    (alreadyPowered && first.Mask != activeMainMask) || !Eligible(first, s)) continue;
                double limit = PlanningLimit(first, s, planningThrottle);
                if (first.Min > limit || limit <= s.Mass * cfg.Gravity) continue;
                double lead = alreadyPowered ? 0 : IgnitionLead(first.Mask, s);
                if (double.IsInfinity(lead)) continue;
                double latest = scheduleCoast ? Math.Min(coastToGround, forecastEnd - lead) : 0;
                if (latest < altitudeWait) continue;
                mainSearchCandidates.Add(new MainSearchCandidate { Group = first, Acceleration = limit / s.Mass,
                    Lead = lead, Latest = latest });
            }
            if (mainSearchCandidates.Count == 0) return best;

            double cachedJoinSpeed = double.NaN;
            foreach (Group last in PlanGroups(finalMask, choices, terminal: true))
            {
                if (last.Count > maxCount || (best.Final != null && CompareGroups(last, best.Final) > 0)) break;
                if ((finalMask != 0 && last.Mask != finalMask) || !Eligible(last, s)) continue;
                double referenceMass = joinMass > 0 ? joinMass : s.Mass;
                double endLimit = joinMass > 0 ? Math.Min(last.Max, cfg.MaxG * Settings.G0 * referenceMass) :
                    PlanningLimit(last, s, planningThrottle);
                if (endLimit <= referenceMass * cfg.Gravity || last.Min > endLimit) continue;
                double lowerTerminal = Math.Max(cfg.Gravity, last.Min / referenceMass);
                double terminalAccel = joinMass > 0 ? TerminalTargetForce(last, referenceMass, terminalThrottle) / referenceMass :
                    lowerTerminal + terminalThrottle * (endLimit / referenceMass - lowerTerminal);
                double joinSpeed = cfg.TouchSpeed + (terminalAccel - cfg.Gravity) * terminalSeconds;
                double joinHeight = TerminalHeight(joinSpeed, terminalSeconds);
                if (joinSpeed != cachedJoinSpeed)
                {
                    ignitionWindows.Clear();
                    cachedJoinSpeed = joinSpeed;
                }
                foreach (MainSearchCandidate main in mainSearchCandidates)
                {
                    Group first = main.Group;
                    if (!Subset(last.Mask, first.Mask) || (fixedEngines & (first.Mask ^ last.Mask)) != 0)
                        continue;
                    var profile = new IgnitionProfile { Acceleration = main.Acceleration, Lead = main.Lead,
                        Reducing = first.Mask != last.Mask };
                    if (!ignitionWindows.TryGetValue(profile, out IgnitionWindow window))
                    {
                        window = FindIgnitionWindow(s, main, profile.Reducing, scheduleCoast, altitudeWait,
                            joinHeight, joinSpeed);
                        ignitionWindows.Add(profile, window);
                    }
                    if (!window.Feasible) continue;
                    double earliest = window.Last;
                    double firstWait = window.First;
                    double lead = main.Lead;

                    var candidate = new Plan { Main = first, Final = last, JoinHeight = joinHeight,
                        JoinSpeed = joinSpeed, TerminalSeconds = terminalSeconds, Wait = earliest,
                        TerminalThrottleFraction = terminalThrottle };
                    if (!FitsContinuousPlan(Coast(s, candidate.Wait + lead), ref candidate, terminalAccel))
                    {
                        // Probe an earlier ignition if the latest one fails
                        // the continuous-burn thrust constraints.
                        double later = earliest;
                        for (int i = 1; i <= 12; i++)
                        {
                            candidate.Wait = earliest + (firstWait - earliest) * i / 12;
                            if (FitsContinuousPlan(Coast(s, candidate.Wait + lead), ref candidate, terminalAccel))
                                break;
                            later = candidate.Wait;
                        }
                        if (candidate.Wait == later) continue;
                        for (int i = 0; i < 14 && later - candidate.Wait > 0.001; i++)
                        {
                            Plan probe = candidate;
                            probe.Wait = (candidate.Wait + later) / 2;
                            if (FitsContinuousPlan(Coast(s, probe.Wait + lead), ref probe, terminalAccel))
                                candidate = probe;
                            else later = probe.Wait;
                        }
                    }

                    if (!alreadyPowered && cfg.MaxIgnitionAltitude > 0 &&
                        cfg.IgnitionAltitudeBlocked(Coast(s, candidate.Wait).Height)) continue;

                    int preference = best.Final == null ? -1 : ComparePlanGroups(candidate, best);
                    if (preference < 0 ||
                        (preference == 0 && (candidate.Wait > best.Wait + 0.004 ||
                         (Math.Abs(candidate.Wait - best.Wait) <= 0.004 && first.Count < best.Main.Count))))
                        best = candidate;
                }
            }
            return best;
        }

        private IgnitionWindow FindIgnitionWindow(Flight s, MainSearchCandidate main, bool reducing,
                                                  bool scheduleCoast, double altitudeWait,
                                                  double joinHeight, double joinSpeed)
        {
            double earliest = scheduleCoast ? Math.Max(altitudeWait, EarliestMainWait(s, main.Lead, joinSpeed)) : 0;
            double latest = main.Latest;
            if (earliest > latest) return default;
            double forceTime = earliest + main.Lead;
            double margin = CoastMargin(s, forceTime, main.Acceleration, joinHeight, joinSpeed);
            if (margin > cappedPlanMargin) cappedPlanMargin = margin;
            if (!FitsMainLimit(Coast(s, forceTime), joinHeight, joinSpeed, main.Acceleration, reducing))
                return default;
            double first = earliest;
            for (int i = 0; i < 18 && latest - earliest > 0.001; i++)
            {
                double mid = (earliest + latest) / 2;
                if (FitsMainLimit(Coast(s, mid + main.Lead), joinHeight, joinSpeed, main.Acceleration, reducing))
                    earliest = mid;
                else latest = mid;
            }
            return new IgnitionWindow { Feasible = true, First = first, Last = earliest };
        }

        private bool FitsContinuousPlan(Flight atBurn, ref Plan candidate, double terminalAccel)
        {
            double x, up;
            if (!MainVector(atBurn, candidate.JoinHeight, candidate.JoinSpeed, out x, out up,
                candidate.Main.Mask != candidate.Final.Mask)) return false;
            double mainTime = TravelTime(atBurn, candidate.JoinHeight, candidate.JoinSpeed);
            double mainAccel = Norm(x, up);
            double joinMass = MassAfter(atBurn.Mass, mainAccel, mainTime, candidate.Main);
            // With constant acceleration the force falls with mass. Check
            // the physical floor at the end of each powered segment.
            if (mainAccel * joinMass < candidate.Main.Min ||
                !TerminalSegmentFits(candidate.Final, joinMass, terminalAccel, candidate.TerminalSeconds)) return false;
            double finalMass = MassAfter(joinMass, terminalAccel, candidate.TerminalSeconds, candidate.Final);
            candidate.Propellant = atBurn.Mass - finalMass;
            candidate.JoinMass = joinMass;
            candidate.JoinThrottle = ThrottleForForce(candidate.Final, terminalAccel * joinMass);
            return FitsPropellant(atBurn, candidate.Main, atBurn.Mass - joinMass, candidate.Final, joinMass - finalMass);
        }

        private bool CanDeployGear(Flight s)
        {
            double speed = Norm(s.Vx, s.Vz);
            if (s.Vz >= 0 || speed <= 0) return false;
            double ax = s.LateralThrustAcceleration + s.ExternalLateralAcceleration;
            double az = s.VerticalThrustAcceleration + s.ExternalUpAcceleration - cfg.Gravity;
            double speedRate = (s.Vx * ax + s.Vz * az) / speed;
            double timeToExposure = Math.Min(cfg.GearDeploymentSeconds,
                cfg.GearDeploymentSeconds / 3 + cfg.GearCommandAdvanceSeconds);
            double openingSpeed = speed + Math.Min(0, speedRate) * timeToExposure;
            return openingSpeed <= cfg.GearSpeedLimit && BallisticImpactSeconds(s.Height, s.Vz, cfg.Gravity)
                <= cfg.GearDeploymentSeconds + cfg.AttitudeResponseSeconds + 0.8 + cfg.GearCommandAdvanceSeconds;
        }

        private Flight Coast(Flight s, double seconds)
        {
            if (s.AeroForecast != null) return s.AeroForecast.Project(s, seconds, cfg.Gravity);
            if (s.CoastDragRate > 1e-6)
            {
                // Freeze the measured drag rate over this lookahead. It
                // opposes both components of the surface velocity.
                double decay = Math.Exp(-s.CoastDragRate * seconds);
                double integrated = (1 - decay) / s.CoastDragRate;
                s.Height += s.Vz * integrated - cfg.Gravity / s.CoastDragRate * (seconds - integrated);
                s.Vx *= decay;
                s.Vz = s.Vz * decay - cfg.Gravity * integrated;
                return s;
            }
            return BallisticCoast(s, seconds);
        }

        private Flight BallisticCoast(Flight s, double seconds)
        {
            s.Height += seconds * (s.Vz - 0.5 * cfg.Gravity * seconds);
            s.Vz -= cfg.Gravity * seconds;
            return s;
        }

        private double CoastImpactSeconds(Flight s)
        {
            if (s.AeroForecast == null) return BallisticImpactSeconds(s.Height, s.Vz, cfg.Gravity);
            double horizon = s.AeroForecast.Duration - s.AeroForecastAge;
            Flight endpoint = Coast(s, horizon);
            if (endpoint.Height > 0) return horizon + BallisticImpactSeconds(endpoint.Height, endpoint.Vz, cfg.Gravity);
            double lo = 0, hi = horizon;
            for (int i = 0; i < 12; i++)
            {
                double mid = (lo + hi) / 2;
                if (Coast(s, mid).Height > 0) lo = mid;
                else hi = mid;
            }
            return hi;
        }

        private double EarliestMainWait(Flight s, double lead, double joinSpeed)
        {
            if (s.AeroForecast == null)
                return Math.Max(Math.Max(0, s.Vz / cfg.Gravity), (s.Vz + joinSpeed) / cfg.Gravity - lead);
            double horizon = s.AeroForecast.Duration - s.AeroForecastAge;
            double seconds = lead;
            Flight previous = Coast(s, seconds);
            if (previous.Vz <= -joinSpeed) return 0;
            while (seconds < horizon)
            {
                double nextTime = Math.Min(horizon, seconds + AeroCoastForecast.StepSeconds);
                Flight next = Coast(s, nextTime);
                if (next.Vz <= -joinSpeed)
                    return seconds - lead + (nextTime - seconds) * (previous.Vz + joinSpeed) / (previous.Vz - next.Vz);
                seconds = nextTime;
                previous = next;
            }
            return seconds - lead + (previous.Vz + joinSpeed) / cfg.Gravity;
        }

        private bool FitsMainLimit(Flight s, double joinHeight, double joinSpeed, double limit, bool reducingEngines)
        {
            double x, up;
            return s.Vz <= 0 && MainVector(s, joinHeight, joinSpeed, out x, out up, reducingEngines) &&
                Norm(x, up) <= limit;
        }

        public static double ColdMainAcceleration(Flight s, double side, double up, double ceiling)
        {
            double along = Math.Sign(side) * s.LateralThrustDirection;
            double upright = Math.Cos(s.UprightAngle);
            if ((side != 0 && along <= 0) || upright <= 0) return double.PositiveInfinity;
            double required = Math.Max(side == 0 ? 0 : Math.Abs(side) / along, up / upright);
            return required <= ceiling ? required : double.PositiveInfinity;
        }

        public bool ColdMainBrakeThenTurn(Flight s, long mask, double x, double up, double ceiling)
        {
            double vertical = Math.Cos(s.UprightAngle);
            if (s.Vz >= 0 || vertical < 0.5 || s.LateralThrustDirection > 0 || vertical * ceiling <= cfg.Gravity)
                return false;
            double lead = EngineStartDelay(mask, s);
            if (double.IsInfinity(lead)) return false;
            s = BallisticCoast(s, lead);
            if (s.Height <= 0) return false;

            // Credit only the measured startup axis until TVC can move it.
            // Bound the cross-axis motion as well as the vertical net burn.
            double turn = Math.Max(cfg.AttitudeResponseSeconds, AimError(s, x, up) / (Math.PI / 9));
            double az = vertical * ceiling - cfg.Gravity;
            double cross = Math.Sqrt(Math.Max(0, 1 - vertical * vertical -
                s.LateralThrustDirection * s.LateralThrustDirection));
            s.Height += turn * (s.Vz + 0.5 * az * turn);
            s.Vz += az * turn;
            s.Vx = Norm(s.Vx + turn * ceiling * s.LateralThrustDirection, turn * ceiling * cross);
            return s.Vz < 0 && CoastMargin(s, 0, ceiling, 0, cfg.TouchSpeed) >= 0;
        }

        private bool AirBrakingCanDefer(Flight s)
        {
            if (s.Vz >= 0) return false;
            // Take one more observed drag interval, then check that a burn
            // can still brake ballistically if the drag disappears.
            double delay = Math.Max(cfg.AttitudeResponseSeconds, cfg.SampleSeconds);
            Flight next;
            if (s.AeroForecast != null)
            {
                next = Coast(s, delay);
                Flight ballistic = BallisticCoast(s, delay);
                if (next.Height <= 0 || Norm(next.Vx, next.Vz) >= Norm(ballistic.Vx, ballistic.Vz)) return false;
            }
            else
            {
                double up = (s.CoastAccelerationMeasured ? s.CoastUpAcceleration : -s.Vz * s.CoastDragRate) - cfg.Gravity;
                double side = s.CoastAccelerationMeasured ? s.CoastLateralAcceleration : -s.Vx * s.CoastDragRate;
                // Look at the atmospheric work separately from gravity, which
                // may accelerate a descending vessel through thin air.
                if (s.Vx * side + s.Vz * (up + cfg.Gravity) >= 0) return false;
                next = s;
                next.Height += delay * (s.Vz + 0.5 * up * delay);
                next.Vz += up * delay;
                next.Vx = Math.Max(0, s.Vx + side * delay);
            }
            return CappedBrakeFitsAfter(next, 0);
        }

        private bool FindLaterAeroLanding(Flight s, ref Plan immediate)
        {
            if (s.AeroForecast == null || s.Vz >= 0 || s.Running != 0 || s.Starting != 0) return false;
            double remaining = s.AeroForecast.Duration - s.AeroForecastAge;
            if (remaining <= AeroCoastForecast.LocalHorizonSeconds) return false;

            Group strongest = null;
            double limit = 0;
            foreach (Group group in groups)
            {
                if (!Eligible(group, s)) continue;
                double cap = PlanningLimit(group, s, Settings.InitialPlanningThrottle);
                if (group.Min > cap || cap <= limit || double.IsInfinity(IgnitionLead(group.Mask, s))) continue;
                strongest = group;
                limit = cap;
            }
            if (strongest == null || limit <= s.Mass * cfg.Gravity) return false;
            double lead = IgnitionLead(strongest.Mask, s);
            double capAccel = limit / s.Mass;
            double baseline = BrakeImpulse(Coast(s, lead), capAccel);
            var probes = new List<KeyValuePair<double, double>>();
            double interval = Math.Max(AeroCoastForecast.StepSeconds, remaining / 32);
            for (double wait = interval; wait + lead <= remaining; wait += interval)
            {
                if (cfg.MaxIgnitionAltitude > 0 && cfg.IgnitionAltitudeBlocked(Coast(s, wait).Height)) continue;
                Flight atThrust = Coast(s, wait + lead);
                if (atThrust.Height <= 0) break;
                if (atThrust.Vz >= 0 || CoastMargin(atThrust, 0, capAccel, 0, cfg.TouchSpeed) < 0) continue;
                double impulse = BrakeImpulse(atThrust, capAccel);
                if (impulse <= baseline - Math.Max(5, baseline * 0.02))
                    probes.Add(new KeyValuePair<double, double>(wait, impulse));
            }
            probes.Sort((a, b) => a.Value.CompareTo(b.Value));
            // This search can look past a gap in ballistic reachability. A
            // prospective coast must still lead to a full Main + Final plan.
            for (int i = 0; i < Math.Min(4, probes.Count); i++)
            {
                double wait = probes[i].Key;
                Flight future = Coast(s, wait);
                future.AeroForecastAge += wait;
                double observedMargin = cappedPlanMargin;
                Plan candidate = immediate.Main == null
                    ? FindPlan(future, igniteNow: true)
                    : ReplanCoastPair(future, immediate, igniteNow: true);
                cappedPlanMargin = observedMargin;
                if (candidate.Main == null || (immediate.Main != null && candidate.Propellant >= immediate.Propellant)) continue;
                candidate.Wait = wait;
                immediate = candidate;
                return true;
            }
            return false;
        }

        private double BrakeImpulse(Flight s, double acceleration)
        {
            return acceleration * MainSeconds(s.Vx, Math.Max(0, -s.Vz - cfg.TouchSpeed), acceleration);
        }

        private bool SameCoastSettings(CoastSearch search) => search.MaxG == cfg.MaxG &&
            search.Altitude == cfg.MaxIgnitionAltitude && search.Precision == cfg.PrecisionSeconds &&
            search.Optimize == cfg.OptimizeFuel;

        private Plan ReplanCoastPair(Flight s, Plan pair, bool igniteNow = false)
        {
            double seconds = pair.RequestedTerminalSeconds == cfg.PrecisionSeconds
                ? pair.TerminalSeconds : cfg.PrecisionSeconds;
            Plan result = FindPlan(s, 0, seconds, pair.Final.Count, pair.TerminalThrottleFraction,
                Settings.InitialPlanningThrottle, igniteNow, pair.Main.Mask, pair.Final.Mask);
            result.RequestedTerminalSeconds = cfg.PrecisionSeconds;
            result.FuelOptimized = pair.FuelOptimized;
            return result;
        }

        private bool CappedBrakeFitsAfter(Flight s, double wait)
        {
            foreach (Group group in groups)
            {
                if (!Eligible(group, s)) continue;
                double ceiling = PlanningLimit(group, s, Settings.InitialPlanningThrottle);
                double lead = IgnitionLead(group.Mask, s);
                if (ceiling >= group.Min && !double.IsInfinity(lead) &&
                    CoastMargin(s, wait + lead, ceiling / s.Mass, 0, cfg.TouchSpeed) >= 0) return true;
            }
            return false;
        }

        private Plan FindRelight(Flight s, int fewerThan = int.MaxValue, double shutdownLead = 0)
        {
            CompleteCatalog();
            Plan best = new Plan();
            double extra = Math.Max(0, s.Vz / cfg.Gravity);
            double coastToGround = (s.Vz + Math.Sqrt(s.Vz * s.Vz + 2 * cfg.Gravity * s.Height)) / cfg.Gravity;
            foreach (Group group in groups)
            {
                if (group.Count >= fewerThan || !Eligible(group, s)) continue;
                double limit = Limit(group, s, true);
                if (limit <= s.Mass * cfg.Gravity || group.Min > limit) continue;
                double delay = EngineStartDelay(group.Mask, s) + shutdownLead;
                if (double.IsInfinity(delay)) continue;
                Plan candidate = RelightPlan(s, group, TerminalReference(group, s, limit), extra, coastToGround, delay);
                if (candidate.Main == null)
                    candidate = RelightPlan(s, group, limit / s.Mass, extra, coastToGround, delay);
                if (candidate.Main == null) continue;
                int preference = best.Final == null ? -1 : CompareGroups(group, best.Final);
                if (preference < 0 || (preference == 0 && candidate.Wait > best.Wait + 0.004))
                    best = candidate;
            }
            if (best.Main != null || !CappedFinalCommitted) return best;

            // Once Final has committed, a lack of steering time may rule
            // out cancelling every metre per second of lateral speed. Keep
            // the max-G cap and use any remaining angle to reduce that speed.
            double bestSide = -1;
            foreach (Group group in groups)
            {
                if (group.Count >= fewerThan || !Eligible(group, s)) continue;
                double limit = Limit(group, s, true);
                double delay = EngineStartDelay(group.Mask, s) + shutdownLead;
                if (group.Min > limit || double.IsInfinity(delay)) continue;
                double acceleration = limit / s.Mass;
                for (int degrees = 18; degrees >= 0; degrees -= 6)
                {
                    double angle = degrees * Math.PI / 180;
                    double up = acceleration * Math.Cos(angle);
                    if (up <= cfg.Gravity) continue;
                    double x = -acceleration * Math.Sin(angle);
                    double preparation = RelightAlignment(s, x, up);
                    double earliest = Math.Max(extra, preparation) + delay;
                    Flight vertical = s;
                    vertical.Vx = 0;
                    if (earliest >= coastToGround || CoastMargin(vertical, earliest, up, 0, cfg.TouchSpeed) < 0)
                        continue;
                    double lo = earliest, hi = coastToGround;
                    for (int i = 0; i < 16; i++)
                    {
                        double t = (lo + hi) / 2;
                        if (CoastMargin(vertical, t, up, 0, cfg.TouchSpeed) >= 0) lo = t;
                        else hi = t;
                    }
                    double duration = Math.Max(0, -s.Vz + cfg.Gravity * lo - cfg.TouchSpeed) / (up - cfg.Gravity);
                    double mass = MassAfter(s.Mass, acceleration, duration, group);
                    if (!FitsPropellant(s, group, s.Mass - mass)) continue;
                    double removedSide = Math.Min(s.Vx, -x * duration);
                    if (removedSide < bestSide || (removedSide == bestSide && CompareGroups(group, best.Final) >= 0)) continue;
                    Plan candidate = ScheduleRelight(s, group, x, up, extra, lo, delay);
                    if (candidate.Main == null) continue;
                    best = candidate;
                    bestSide = removedSide;
                }
            }
            return best;
        }

        private Plan RelightPlan(Flight s, Group group, double acceleration, double apex,
                                 double contact, double delay)
        {
            double margin = CoastMargin(s, apex + delay, acceleration, 0, cfg.TouchSpeed);
            if (margin > cappedPlanMargin) cappedPlanMargin = margin;
            if (margin < 0) return new Plan();
            // Find the latest time for established thrust. The aircraft may
            // turn during that coast, but ignition still requires the real gate.
            double lo = apex + delay, hi = Math.Max(lo, contact);
            for (int i = 0; i < 20; i++)
            {
                double mid = (lo + hi) / 2;
                if (CoastMargin(s, mid, acceleration, 0, cfg.TouchSpeed) >= 0) lo = mid;
                else hi = mid;
            }
            double down = Math.Max(cfg.TouchSpeed, -s.Vz + cfg.Gravity * lo);
            double duration = MainSeconds(s.Vx, down - cfg.TouchSpeed, acceleration);
            double x = -s.Vx / Math.Max(cfg.SampleSeconds, duration);
            double up = cfg.Gravity + (down - cfg.TouchSpeed) / Math.Max(cfg.SampleSeconds, duration);
            double remainingMass = MassAfter(s.Mass, acceleration, duration, group);
            if (acceleration * remainingMass < group.Min || !FitsPropellant(s, group, s.Mass - remainingMass)) return new Plan();
            Plan direct = ScheduleRelight(s, group, x, up, apex, lo, delay);
            if (direct.Main != null || cfg.AttitudeResponseSeconds <= 0) return direct;

            // A smaller initial tilt can enter the ignition cone sooner.
            // Pay for the remaining lateral turn under thrust before using it.
            double target = Math.Atan2(x, up);
            for (int step = 1; step <= 4; step++)
            {
                double angle = target * (1 - step / 4.0);
                double sideways = acceleration * Math.Sin(angle);
                double upward = acceleration * Math.Cos(angle);
                double rate = Math.Min(Math.PI / 9, s.MeasuredTurnRate);
                if (rate <= 0) break;
                double turnAfterStart = Math.Abs(target - angle) / rate;
                double earliest = Math.Max(apex, RelightAlignment(s, sideways, upward)) + delay;
                if (earliest > lo || !RelightTurnFits(s, earliest, sideways, upward, turnAfterStart, acceleration)) continue;
                double latest = lo;
                for (int i = 0; i < 14; i++)
                {
                    double t = (earliest + latest) / 2;
                    if (RelightTurnFits(s, t, sideways, upward, turnAfterStart, acceleration)) earliest = t;
                    else latest = t;
                }
                Plan candidate = ScheduleRelight(s, group, sideways, upward, apex, earliest, delay);
                if (candidate.Main != null) return candidate;
            }
            return new Plan();
        }

        private bool RelightTurnFits(Flight s, double wait, double x, double up, double turn, double acceleration)
        {
            s = BallisticCoast(s, wait);
            double az = up - cfg.Gravity;
            s.Height += turn * (s.Vz + 0.5 * az * turn);
            s.Vz += az * turn;
            s.Vx = Math.Abs(s.Vx + x * turn);
            return s.Vz < 0 && CoastMargin(s, 0, acceleration, 0, cfg.TouchSpeed) >= 0;
        }

        private Plan ScheduleRelight(Flight s, Group group, double x, double up, double apex, double latest, double delay)
        {
            double turn = RelightAlignment(s, x, up);
            // A stationary hull can begin steering while there is time to
            // observe its response. Tick still requires the measured gate.
            double preparation = double.IsPositiveInfinity(turn) ? cfg.AttitudeResponseSeconds : turn;
            if (Math.Max(apex, preparation) + delay > latest + 1e-5) return new Plan();
            double wait = Math.Max(0, latest - delay - preparation);
            bool gateClosing = turn <= 0 && s.Vz <= 0 && CannotWaitForRelightGate(s, group, x, up, wait);
            return new Plan { Main = group, Final = group, Wait = gateClosing ? 0 : wait,
                RelightX = x, RelightUp = up, ImpendingRelightGate = gateClosing };
        }

        private bool CannotWaitForRelightGate(Flight s, Group group, double x, double up, double wait)
        {
            if (usedAlignmentRelight || cfg.AttitudeResponseSeconds <= 0 || s.MeasuredTurnRate <= 0) return false;
            double away = AwayTurnRate(s, x, up);
            if (away <= 0) return false;
            double cone = Settings.IgnitionToleranceDegrees * Math.PI / 180;
            double leave = Math.Max(0, cone - AimError(s, x, up)) / away;
            double settle = (s.Unsettled & group.Mask) != 0 ? s.UllageSeconds : 0;
            double reverse = cfg.AttitudeResponseSeconds + TurnArrestTime(s, away);
            double downAtPower = -s.Vz + cfg.Gravity * EngineStartDelay(group.Mask, s);
            double minimumBraking = Math.Max(0, group.Min / s.Mass - cfg.Gravity);
            if (downAtPower < minimumBraking * cfg.AttitudeResponseSeconds) return false;
            return leave <= Math.Max(2 * cfg.SampleSeconds, settle) && wait >= leave && wait <= leave + reverse;
        }

        private double TurnArrestTime(Flight s, double away)
        {
            double modeled = cfg.AttitudeResponseSeconds * away / s.MeasuredTurnRate;
            if (!s.AttitudeRateMeasured || s.MeasuredTurnAcceleration >= 0) return modeled;
            return Math.Max(modeled, away / -s.MeasuredTurnAcceleration);
        }

        private double RelightAlignment(Flight s, double x, double up)
        {
            if (cfg.AttitudeResponseSeconds <= 0) return 0;
            double travel = AimError(s, x, up) - Settings.IgnitionToleranceDegrees * Math.PI / 180;
            if (travel <= 0) return 0;
            if (s.MeasuredTurnRate <= 0) return double.PositiveInfinity;
            double errorRate = AimErrorRate(s, x, up);
            double away = Math.Max(0, errorRate);
            // If the hull is still rotating away, include reversal time and
            // the angular drift accumulated before arresting that motion.
            double timeToArrest = TurnArrestTime(s, away);
            double seconds = timeToArrest + (travel + 0.5 * away * timeToArrest) / s.MeasuredTurnRate;
            if (errorRate < 0 && s.MeasuredTurnAcceleration > 0)
            {
                // A measured acceleration is useful only when it is already
                // driving the actual error toward the ignition cone.
                double closing = -errorRate;
                double acceleration = s.MeasuredTurnAcceleration * Math.Min(1, closing / s.MeasuredTurnRate);
                double acceleratingTurn = 2 * travel / (closing + Math.Sqrt(closing * closing + 2 * acceleration * travel));
                seconds = Math.Min(seconds, acceleratingTurn);
            }
            return seconds;
        }

        private bool HasTimeToObserveRelightTurn(Flight s)
        {
            if (s.MeasuredTurnAcceleration <= 0 || AimErrorRate(s, 0, 1) >= 0) return false;
            foreach (Group group in groups)
            {
                if (!Eligible(group, s)) continue;
                double limit = Limit(group, s, true);
                double lead = EngineStartDelay(group.Mask, s) + cfg.AttitudeResponseSeconds;
                if (group.Min <= limit && !double.IsInfinity(lead) &&
                    CoastMargin(s, lead, limit / s.Mass, 0, cfg.TouchSpeed) >= 0) return true;
            }
            return false;
        }

        private double TravelTime(Flight s, double endHeight, double endSpeed)
        {
            return 2 * (s.Height - endHeight) / Math.Max(cfg.TouchSpeed, -s.Vz + endSpeed);
        }

        private bool MainVector(Flight s, double joinHeight, double joinSpeed, out double x, out double up,
                                bool reducingEngines = false, bool fadeLift = false, bool useRetrograde = true)
        {
            double down = -s.Vz;
            double duration = TravelTime(s, joinHeight, joinSpeed);
            if (duration <= cfg.SampleSeconds) { x = 0; up = 0; return false; }
            double turn = Math.Min(duration * 0.3, cfg.AttitudeResponseSeconds);
            double lateralTime;
            if (reducingEngines && cfg.AttitudeResponseSeconds > 0)
            {
                // Model the lateral acceleration tapering to zero through
                // the turn, while the main gimbals are still available.
                double taper = Math.Min(duration, cfg.AttitudeResponseSeconds);
                double carry = s.LateralThrustAcceleration != 0 ? Math.Min(turn, taper / 2) : 0;
                double remainingSide = s.Vx + s.LateralThrustAcceleration * carry;
                double direction = Math.Sign(s.Vx);
                // Finish most of the lateral braking while the main bank
                // still has enough time and torque to turn back toward up.
                lateralTime = duration - (taper + carry) / 2;
                lateralTime -= Math.Min(0.35 * lateralTime, cfg.AttitudeResponseSeconds);
                x = -direction * Math.Max(0, direction * remainingSide) / lateralTime;
            }
            else
            {
                lateralTime = Math.Max(duration - turn, cfg.AttitudeResponseSeconds);
                x = -(s.Vx + s.LateralThrustAcceleration * turn) /
                    lateralTime;
            }
            up = cfg.Gravity + (down - joinSpeed) / duration;
            x -= s.ExternalLateralAcceleration;
            double lift = s.ExternalUpAcceleration;
            if (fadeLift && lift > 0 && cfg.AttitudeResponseSeconds > 0)
            {
                // Positive lift measured broadside may disappear as the
                // vehicle turns toward the terminal configuration.
                double horizon = duration / cfg.AttitudeResponseSeconds;
                lift *= (1 - Math.Exp(-horizon)) / horizon;
            }
            up -= lift;
            if (useRetrograde && up > 0 && cfg.AttitudeResponseSeconds > 0)
            {
                // Favor surface retrograde while preserving vertical authority
                // and the lateral impulse needed for the terminal handoff.
                double retrograde = Math.Atan2(-s.Vx, Math.Max(0, down));
                double trajectory = Math.Atan2(x, up);
                double towardRetrograde = Math.Max(retrograde - MainSideTrimRadians,
                    Math.Min(retrograde + MainSideTrimRadians, trajectory));
                double velocityAllowance = HorizontalTolerance / lateralTime;
                double correction = up * Math.Tan(towardRetrograde) - x;
                x += Math.Max(-velocityAllowance, Math.Min(velocityAllowance, correction));
                if (reducingEngines && s.AttitudeRateMeasured && s.Running != 0)
                {
                    // The actual axis takes time to unload lateral thrust.
                    // Reserve that impulse before the velocity crosses zero.
                    double response = cfg.AttitudeResponseSeconds;
                    double horizon = 1.5 * response;
                    double carry = response * (1 - Math.Exp(-horizon / response));
                    double direction = Math.Sign(s.Vx);
                    double currentNet = direction * (s.LateralThrustAcceleration + s.ExternalLateralAcceleration);
                    double residual = Math.Abs(s.Vx) + Math.Min(0, currentNet) * carry;
                    double lateralLimit = Math.Max(0, residual) / (horizon - carry);
                    double net = direction * (x + s.ExternalLateralAcceleration);
                    x = direction * Math.Max(net, -lateralLimit) - s.ExternalLateralAcceleration;
                }
            }
            return up > 0;
        }

        private Command AlignForMain(Flight s, long mask, string reason)
        {
            if (mask == 0) return Output(s, 0, -s.Vx, -s.Vz, reason);
            double x, up;
            if (plan.Main != null && cfg.AttitudeResponseSeconds > 0 &&
                MainVector(Coast(s, plan.Wait + IgnitionLead(plan.Main.Mask, s)), plan.JoinHeight, plan.JoinSpeed, out x, out up,
                    plan.Main.Mask != plan.Final.Mask))
                return Output(s, mask, x, up, reason);
            return Output(s, mask, -s.Vx, Math.Max(1, -s.Vz), reason);
        }

        private bool FinalVector(Flight s, out double x, out double up)
        {
            double down = -s.Vz;
            double closingSpeeds = Math.Max(cfg.TouchSpeed, down + cfg.TouchSpeed);
            // The last correction should still use the measured clearance.
            // Bound the division by one physics sample of travel.
            double seconds = FinalTimeToContact(s, cfg);
            double height = 0.5 * seconds * closingSpeeds;
            up = cfg.Gravity + (down * down - cfg.TouchSpeed * cfg.TouchSpeed) / (2 * height);
            double turn = Math.Min(seconds * 0.5, s.TerminalAlignmentSeconds);
            double controlTime = seconds - turn;
            double residual = s.Vx + 0.5 * s.LateralThrustDirection * Math.Max(0, up) * turn;
            x = -residual / Math.Max(controlTime, Math.Max(0.8, cfg.AttitudeResponseSeconds));
            // The terminal group can finish cancelling a small residual
            // during the remaining descent, within its tilt and thrust limits.
            double tolerance = Math.Max(HorizontalTolerance, down * 0.03);
            double tiltTangent = TerminalTiltTangent;
            bool steeringTime = cfg.AttitudeResponseSeconds > 0 && controlTime > cfg.AttitudeResponseSeconds;
            if (steeringTime)
                tiltTangent += Math.Min(0.42, (controlTime - cfg.AttitudeResponseSeconds) * 0.1);
            double lateralLimit = Math.Max(0, up * tiltTangent);
            if (cfg.AttitudeResponseSeconds > 0)
            {
                // Leave time to damp the last lateral correction under the
                // terminal attitude and thrust constraints.
                tolerance = Math.Max(tolerance, 0.75 * lateralLimit * controlTime);
                // Start unloading while the nose is still turning, but a
                // forecast of reversal only releases braking. A command in
                // the other direction requires a measured velocity reversal.
                double projectedSpeed = s.Vx + Math.Min(s.LateralVelocityLookahead, seconds) *
                    (s.LateralThrustAcceleration + s.ExternalLateralAcceleration);
                double direction = Math.Sign(s.Vx);
                double lateralSpeed = direction * Math.Max(0, direction * projectedSpeed - 0.1);
                double lateralTime = Math.Max(0.5, Math.Min(1.5, 0.6 * controlTime));
                x = Math.Max(-lateralLimit, Math.Min(lateralLimit, -lateralSpeed / lateralTime));
            }
            return down > 0 && up > 0 && Math.Abs(residual) <= tolerance &&
                   Math.Abs(s.Vx) <= tolerance + lateralLimit * turn &&
                   Math.Cos(s.UprightAngle) > (steeringTime ? 0.5 : 0.7) && Math.Abs(x) <= lateralLimit;
        }

        internal static double FinalTimeToContact(Flight s, Settings settings) =>
            Math.Max(settings.SampleSeconds, 2 * s.Height / Math.Max(settings.TouchSpeed, -s.Vz + settings.TouchSpeed));

        private double LeadFinalLateral(Flight s, Group group, double x, double up)
        {
            if (emergency || !s.AttitudeRateMeasured || cfg.AttitudeResponseSeconds <= 0 || s.Vz >= 0 ||
                s.UprightAngle >= Math.PI / 2 || !Subset(group.Mask, s.Running))
                return x;
            double axial = up - s.ExternalUpAcceleration;
            double ceiling = Limit(group, s, true) / s.Mass;
            if (axial <= 0) return x;
            if (axial >= ceiling) return s.ExternalLateralAcceleration;

            double remaining = FinalTimeToContact(s, cfg);
            double speed = Math.Abs(s.Vx);
            double direction = Math.Sign(s.Vx);
            double demand = AttitudeGuidance.FinalLateralDemand(speed);
            double tilt = AttitudeGuidance.FinalTiltDegrees(demand, remaining) * AttitudeGuidance.Degree;
            double availableSide = Math.Min(axial * Math.Tan(tilt), Math.Sqrt(ceiling * ceiling - axial * axial));

            // Reserve time to level the hull. Predict the lateral impulse
            // during that turn so the body can start unloading before reversal.
            double settle = Math.Min(cfg.AttitudeResponseSeconds, 0.4 * remaining);
            double brakingTime = Math.Max(0.55, Math.Min(1.4, 0.55 * (remaining - settle)));
            double attitude = Math.Atan2(direction * s.LateralThrustDirection, Math.Cos(s.UprightAngle));
            double alignedSide = axial * Math.Tan(attitude);
            double correctionTime = AttitudeGuidance.AngleResponseSeconds;
            double carry = correctionTime * (1 - Math.Exp(-remaining / correctionTime));
            double turnRate = direction * s.PitchRate;
            if (attitude * turnRate < 0)
            {
                // A turn already moving toward vertical has less impulse
                // left than a stationary hull at the same tilt.
                double timeToVertical = Math.Abs(attitude / turnRate);
                double turning = Math.Min(remaining, timeToVertical);
                carry = Math.Min(carry, turning * (1 - 0.5 * turning / timeToVertical));
            }
            double departure = direction * (s.LateralThrustAcceleration + s.ExternalLateralAcceleration) - alignedSide;
            double transient = Math.Min(remaining, Math.Min(0.25, s.LateralVelocityLookahead));
            double residual = speed + alignedSide * carry + departure * transient;
            double requested = -Math.Max(0, residual - 0.1) / brakingTime;
            double nozzle = Math.Max(-availableSide, Math.Min(0, requested - direction * s.ExternalLateralAcceleration));
            return direction * nozzle + s.ExternalLateralAcceleration;
        }

        private Group FinalGroup(Flight s, bool capped, double x, double up, int maxCount = int.MaxValue)
        {
            return SelectFinalGroup(s, capped, x, up, maxCount, 0);
        }

        private Group SelectFinalGroup(Flight s, bool capped, double x, double up, int maxCount, long preferredMask)
        {
            Group best = null;
            bool bestHover = false;
            double needed = Norm(x, up) * s.Mass;
            double remaining = TravelTime(s, 0, cfg.TouchSpeed);
            double requiredTilt = Math.Atan2(Math.Abs(x), up);
            // Evaluate the projected ceiling through the turn when there is
            // room to steer. Otherwise require authority at the current tilt.
            double projectedTilt = remaining > 2 * s.TerminalAlignmentSeconds
                ? Math.Max(requiredTilt, (s.UprightAngle + requiredTilt) / 2) : s.UprightAngle;
            foreach (Group group in TryFirst(preferredMask))
            {
                if (group.Count > maxCount || !Subset(group.Mask, s.Running) || !Eligible(group, s)) continue;
                double limit = Limit(group, s, capped);
                double projected = up * s.Mass / Math.Max(0.5, Math.Cos(projectedTilt));
                if (group.Min > needed || Math.Max(needed, projected) > limit || up <= cfg.Gravity) continue;
                if (capped && group.Min > up * MassAfter(s.Mass, Norm(x, up), remaining))
                    continue;
                if (group.Mask == preferredMask) return group;
                bool hover = group.Min <= cfg.Gravity * s.Mass && cfg.Gravity * s.Mass <= limit;
                int placement = best != null && best.Count == group.Count ? Group.InsideFirst(group, best) : 0;
                if (best == null || placement < 0 || (placement == 0 && ((hover && !bestHover) ||
                    (hover == bestHover && CompareGroups(group, best) <= 0 &&
                     (group.Count != best.Count || group.Min < best.Min)))))
                {
                    best = group;
                    bestHover = hover;
                }
            }
            return best;
        }

        private bool CanTrackMain(Flight s, Group running, Plan target, out double x, out double up,
                                  bool cappedMain = true)
        {
            if (!MainVector(s, target.JoinHeight, target.JoinSpeed, out x, out up,
                running.Mask != target.Final.Mask, target.FadeMainLift))
                return false;
            double duration = TravelTime(s, target.JoinHeight, target.JoinSpeed);
            double accel = Norm(x, up);
            double joinMass = MassAfter(s.Mass, accel, duration, running);
            Group terminal = LiveGroup(target.Final.Mask);
            double terminalAccel = cfg.Gravity + (target.JoinSpeed - cfg.TouchSpeed) / target.TerminalSeconds;
            return accel * s.Mass <= Limit(running, s, cappedMain) * 1.005 && accel * joinMass >= running.Min &&
                   terminal != null && Eligible(terminal, s) &&
                   TerminalSegmentFits(terminal, joinMass, terminalAccel, target.TerminalSeconds) &&
                   FitsPropellant(s, running, s.Mass - joinMass, terminal,
                       joinMass - MassAfter(joinMass, terminalAccel, target.TerminalSeconds, terminal));
        }

        private double AimError(Flight s, double x, double up)
        {
            double cosine = (s.LateralThrustDirection * x + Math.Cos(s.UprightAngle) * up) / Norm(x, up);
            return Math.Acos(Math.Max(-1, Math.Min(1, cosine)));
        }

        private double AwayTurnRate(Flight s, double x, double up)
        {
            return Math.Max(0, AimErrorRate(s, x, up));
        }

        private double AimErrorRate(Flight s, double x, double up)
        {
            if (s.AttitudeRateMeasured)
            {
                double projectionRate = (s.LateralAxisRate * x + s.UpAxisRate * up) / Norm(x, up);
                double error = AimError(s, x, up);
                return -projectionRate / Math.Max(0.01, Math.Sin(error));
            }
            double currentAim = Math.Atan2(s.LateralThrustDirection, Math.Cos(s.UprightAngle));
            return -Math.Sign(Math.Atan2(x, up) - currentAim) * s.PitchRate;
        }

        private bool MainTurnFits(Flight s, Plan target, double x, double up)
        {
            if (cfg.AttitudeResponseSeconds <= 0) return true;
            // Budget both the initial turn and a return into the single-engine
            // capture cone. The powered turn limit is below the attitude loop's max.
            double poweredRate = Math.PI / 9;
            double travel = AimError(s, x, up) + Math.Max(0, Math.Abs(Math.Atan2(x, up)) - Math.PI / 3);
            double recovery = AwayTurnRate(s, x, up) * cfg.AttitudeResponseSeconds / poweredRate;
            return travel / poweredRate + recovery <= TravelTime(s, target.JoinHeight, target.JoinSpeed);
        }

        private Plan ReplanMain(Flight s, Group running)
        {
            Plan best = FindMainTrajectory(s, running);
            if (best.Main != null) return best;
            if (s.ExternalUpAcceleration > 0 && cfg.AttitudeResponseSeconds > 0)
            {
                best = FindFineMainTrajectory(s, running, true);
                if (best.Main != null) return best;
            }
            PrepareNeighbors(running.Mask);
            foreach (Group alternative in TryFirst(fewerEngines))
            {
                if (alternative.Mask == running.Mask || !Subset(alternative.Mask, running.Mask & s.Running) ||
                    !Eligible(alternative, s) || (fixedEngines & (running.Mask ^ alternative.Mask)) != 0 ||
                    (best.Main != null && alternative.Count < best.Main.Count)) continue;
                Plan candidate = FindMainTrajectory(ThrustAfterDownselect(s, running, alternative), alternative);
                double x, up;
                // An alternative consisting entirely of the landing group is
                // a terminal handoff. Keep it behind the terminal capture checks.
                if (candidate.Main == null || candidate.Main.Mask == candidate.Final.Mask || !TerminalTurnReady(s) ||
                    !MainVector(ThrustAfterDownselect(s, running, alternative), candidate.JoinHeight, candidate.JoinSpeed,
                        out x, out up, alternative.Mask != candidate.Final.Mask, candidate.FadeMainLift) ||
                    !TerminalVerticalAuthority(s, alternative, up + s.ExternalUpAcceleration)) continue;
                if (fewerEngines.Contains(alternative.Mask)) return candidate;
                if (best.Main == null || candidate.Main.Count > best.Main.Count ||
                    (candidate.Main.Count == best.Main.Count && ComparePlanGroups(candidate, best) < 0))
                    best = candidate;
            }
            return best;
        }

        private Flight ThrustAfterDownselect(Flight s, Group prior, Group current)
        {
            if (prior.Mask == current.Mask) return s;
            if (s.EngineThrustAccelerations != null && Subset(current.Mask, s.Running) &&
                Subset(current.Mask, prior.Mask))
            {
                AttitudeVector sum = default;
                for (int i = 0; i < s.EngineThrustAccelerations.Length; i++)
                    if ((current.Mask & (1L << i)) != 0) sum += s.EngineThrustAccelerations[i];
                s.LateralThrustAcceleration = sum.X;
                s.VerticalThrustAcceleration = sum.Y;
                s.CrossThrustAcceleration = sum.Z;
                return s;
            }
            double fraction = current.Max / prior.Max;
            s.LateralThrustAcceleration *= fraction;
            s.VerticalThrustAcceleration *= fraction;
            s.CrossThrustAcceleration *= fraction;
            return s;
        }

        private Plan FindMainTrajectory(Flight s, Group running)
        {
            double x, up;
            Plan coarse = FindPlan(s, running.Mask);
            if (coarse.Main != null && CanTrackMain(s, running, coarse, out x, out up) &&
                Norm(x, up) * s.Mass <= Limit(running, s, true) && MainTurnFits(s, coarse, x, up))
            {
                if (Math.Abs(coarse.TerminalSeconds - cfg.PrecisionSeconds) <= cfg.SampleSeconds)
                    return coarse;
                Plan requested = FineMainPlan(s, running, cfg.PrecisionSeconds, false);
                return requested.Main != null ? requested : coarse;
            }

            return FindFineMainTrajectory(s, running, false);
        }

        private Plan FindFineMainTrajectory(Flight s, Group running, bool fadeLift)
        {
            // Refine the sampled terminal force interval only after losing
            // the current burn. Keep the running engines while solving.
            foreach (double factor in TerminalDurationFactors)
            {
                Plan candidate = FineMainPlan(s, running, cfg.PrecisionSeconds * factor, fadeLift);
                if (candidate.Main != null) return candidate;
            }
            // A feasible burn can fall between the normal duration probes.
            // Test nearby durations before widening the engine selection.
            const int durationStepsPerReserve = 80;
            for (int offset = 1; offset <= durationStepsPerReserve / 2; offset++)
            {
                double delta = cfg.PrecisionSeconds * offset / durationStepsPerReserve;
                Plan candidate = FineMainPlan(s, running, cfg.PrecisionSeconds + delta, fadeLift);
                if (candidate.Main != null) return candidate;
                candidate = FineMainPlan(s, running, cfg.PrecisionSeconds - delta, fadeLift);
                if (candidate.Main != null) return candidate;
            }
            return new Plan();
        }

        private Plan FineMainPlan(Flight s, Group running, double seconds, bool fadeLift,
                                  bool cappedMain = true)
        {
            Plan best = new Plan();
            foreach (Group terminal in groups)
            {
                if (!Subset(terminal.Mask, s.Running & running.Mask) || !Eligible(terminal, s) ||
                    (fixedEngines & (terminal.Mask ^ running.Mask)) != 0 ||
                    (best.Final != null && CompareGroups(terminal, best.Final) >= 0)) continue;
                double lower, upper;
                double lift = fadeLift ? Math.Min(0, s.ExternalUpAcceleration) : s.ExternalUpAcceleration;
                if (!MainTerminalForceRange(s, terminal, seconds, lift, out lower, out upper)) continue;
                int subdivisions = cappedMain ? 256 : 32;
                double preferred = (lower + upper) / 2;
                for (int i = 0; i < 3; i++)
                {
                    double speed = cfg.TouchSpeed + (preferred - cfg.Gravity) * seconds;
                    double height = TerminalHeight(speed, seconds);
                    double side, vertical;
                    if (!MainVector(s, height, speed, out side, out vertical, running.Mask != terminal.Mask, fadeLift))
                        break;
                    double mass = MassAfter(s.Mass, Norm(side, vertical), TravelTime(s, height, speed), running);
                    preferred = Math.Max(lower, Math.Min(upper, TerminalTargetForce(terminal, mass, 0.5) / mass));
                }
                int center = (int)Math.Round((preferred - lower) / (upper - lower) * subdivisions);
                for (int i = 0; i <= 2 * Math.Max(center, subdivisions - center); i++)
                {
                    int index = center + (i % 2 == 0 ? -i / 2 : (i + 1) / 2);
                    if (index < 0 || index > subdivisions) continue;
                    double accel = lower + (upper - lower) * index / subdivisions;
                    double down = cfg.TouchSpeed + (accel - cfg.Gravity) * seconds;
                    var target = new Plan { Main = running, Final = terminal, JoinHeight = TerminalHeight(down, seconds),
                        JoinSpeed = down, TerminalSeconds = seconds, RequestedTerminalSeconds = cfg.PrecisionSeconds,
                        FadeMainLift = fadeLift, TerminalThrottleFraction = 0.5 };
                    double x, up;
                    if (!CanTrackMain(s, running, target, out x, out up, cappedMain) ||
                        Norm(x, up) * s.Mass > Limit(running, s, cappedMain) || !MainTurnFits(s, target, x, up)) continue;
                    UpdateJoinEstimate(s, running, ref target, Norm(x, up));
                    best = target;
                    break;
                }
            }
            return best;
        }

        private void UpdateJoinEstimate(Flight s, Group running, ref Plan target, double mainAcceleration)
        {
            target.JoinMass = MassAfter(s.Mass, mainAcceleration, TravelTime(s, target.JoinHeight, target.JoinSpeed), running);
            double terminalAcceleration = cfg.Gravity + (target.JoinSpeed - cfg.TouchSpeed) / target.TerminalSeconds;
            target.JoinThrottle = ThrottleForForce(target.Final, target.JoinMass * terminalAcceleration);
        }

        private bool MainTerminalForceRange(Flight s, Group terminal, double seconds, double lift, out double lower, out double upper)
        {
            lower = cfg.Gravity;
            upper = cfg.Gravity;
            double down = -s.Vz;
            double effectiveGravity = cfg.Gravity - lift;
            double b = effectiveGravity * seconds;
            double discriminant = b * b + 4 * (down * down + effectiveGravity * (2 * s.Height - cfg.TouchSpeed * seconds));
            if (discriminant <= 0) return false;

            // The roots bound the terminal entry speeds that require upward
            // thrust in Main. Also leave at least one sample before handoff.
            double root = Math.Sqrt(discriminant);
            double minDown = (-b - root) / 2;
            double maxDown = Math.Min((-b + root) / 2,
                (2 * s.Height - cfg.TouchSpeed * seconds - cfg.SampleSeconds * down) / (seconds + cfg.SampleSeconds));
            const double edge = 1e-7;
            lower = Math.Max(Math.Max(cfg.Gravity, terminal.Min / s.Mass),
                cfg.Gravity + (minDown - cfg.TouchSpeed) / seconds + edge);
            upper = Math.Min(Limit(terminal, s, true) / s.Mass,
                cfg.Gravity + (maxDown - cfg.TouchSpeed) / seconds - edge);
            return upper > lower;
        }

        private Command BeginFinal(Flight s, Group terminal, double x, double up, string reason)
        {
            Flight terminalFlight = s;
            if (phase == Phase.Main && !MainHandoffLateralReady(s))
                reason += " (horizontal target not reached)";
            if ((phase == Phase.Main || phase == Phase.Final) && s.Running != terminal.Mask)
            {
                Group active = LiveGroup(s.Running);
                if (active != null)
                {
                    terminalFlight = ThrustAfterDownselect(s, active, terminal);
                    FinalVector(terminalFlight, out x, out up);
                }
            }
            selected = terminal.Mask;
            PrepareNeighbors(selected);
            phase = Phase.Final;
            if (!emergency) CappedFinalCommitted = true;
            return Output(s, selected, LeadFinalLateral(terminalFlight, terminal, x, up), up, reason);
        }

        private Group RecoverTerminal(Flight s, Group running, int maxCount)
        {
            if (cfg.AttitudeResponseSeconds <= 0) return null;
            Group best = null;
            foreach (Group group in groups)
            {
                if (group.Count > maxCount || !Subset(group.Mask, s.Running & running.Mask) || !Eligible(group, s) ||
                    (fixedEngines & (running.Mask ^ group.Mask)) != 0 ||
                    (best != null && CompareGroups(group, best) >= 0)) continue;
                if (TerminalHandoffReady(s, running, group) &&
                    TerminalTurnFits(ThrustAfterDownselect(s, running, group), group)) best = group;
            }
            return best;
        }

        private static bool LateralReversalImminent(Flight s, double seconds)
        {
            double braking = -s.LateralThrustAcceleration - s.ExternalLateralAcceleration;
            return braking > 0 && s.Vx <= seconds * braking;
        }

        private bool TerminalTurnReady(Flight s)
        {
            if (!s.AttitudeRateMeasured) return true;
            double x, up;
            FinalVector(s, out x, out up);
            up = Math.Max(0.01, up - s.ExternalUpAcceleration);
            x = Math.Min(0, x - s.ExternalLateralAcceleration);
            double angularSpeed = Math.Max(Math.Abs(s.PitchRate), s.MeasuredTurnRate);
            double stoppingAngle = angularSpeed * angularSpeed / (2 * Settings.TerminalTurnBraking);
            // Keep the main gimbals until the smaller group can arrest the
            // current angular momentum without overshooting its thrust axis.
            double errorRate = AimErrorRate(s, x, up);
            double closingFraction = angularSpeed > 0
                ? Math.Max(0, Math.Min(1, -errorRate / angularSpeed)) : 0;
            double usableTurn = AimError(s, x, up) * closingFraction;
            return stoppingAngle <= usableTurn + Math.PI / 60;
        }

        private bool TerminalHandoffReady(Flight s, Group running, Group terminal)
        {
            if (terminal.Mask == running.Mask) return true;
            Flight reduced = ThrustAfterDownselect(s, running, terminal);
            double x, up;
            FinalVector(reduced, out x, out up);
            return TerminalTurnReady(reduced) && TerminalVerticalAuthority(reduced, terminal, up);
        }

        private bool TerminalVerticalAuthority(Flight s, Group group, double neededUp, bool capped = true)
        {
            if (!s.AttitudeRateMeasured) return true;
            double projection = Math.Cos(s.UprightAngle);
            double measured = Norm(Norm(s.LateralThrustAcceleration, s.VerticalThrustAcceleration),
                s.CrossThrustAcceleration);
            if (measured > 0.1) projection = Math.Min(projection, s.VerticalThrustAcceleration / measured);
            double available = Limit(group, s, capped) / s.Mass * projection + s.ExternalUpAcceleration;
            return neededUp <= available;
        }

        private bool TryTerminalThrust(Flight s, Group running, ref double x, double up, bool capped = true)
        {
            double ceiling = Limit(running, s, capped) / s.Mass;
            double axial = Math.Max(0, up - s.ExternalUpAcceleration);
            double lateral = Math.Min(0, x - s.ExternalLateralAcceleration);
            if (Norm(lateral, axial) <= 1.005 * ceiling) return true;
            if (axial >= ceiling) return false;
            double availableSide = Math.Sqrt(ceiling * ceiling - axial * axial);
            double netBraking = availableSide - s.ExternalLateralAcceleration;
            double remaining = TravelTime(s, 0, cfg.TouchSpeed);
            if (s.Vx > Math.Max(0, netBraking) * remaining + HorizontalTolerance) return false;
            x = Math.Min(0, s.ExternalLateralAcceleration - availableSide);
            return true;
        }

        private bool TryReduceFinalFloor(Flight s, Group running, double side, double up, out Command command)
        {
            command = default;
            double force = Norm(side - s.ExternalLateralAcceleration, up - s.ExternalUpAcceleration) * s.Mass;
            // Throttle remains the first choice. Reduce the bank only when
            // its real minimum would stop the descent above the ground.
            double minimumUp = running.Min / s.Mass * Math.Cos(s.UprightAngle) + s.ExternalUpAcceleration;
            if (running.Min <= force || minimumUp <= cfg.Gravity ||
                s.Vz * s.Vz >= 2 * (minimumUp - cfg.Gravity) *
                    (s.Height - Math.Max(0.25, -s.Vz * cfg.SampleSeconds))) return false;

            PrepareNeighbors(running.Mask);
            Group best = null;
            foreach (Group group in TryFirst(fewerEngines))
            {
                if (group.Count >= running.Count || !Subset(group.Mask, s.Running & running.Mask) ||
                    !Eligible(group, s) || group.Min > force || (best != null && CompareGroups(group, best) >= 0)) continue;
                Flight reduced = ThrustAfterDownselect(s, running, group);
                double x = side;
                if (TryTerminalThrust(reduced, group, ref x, up) &&
                    TerminalHandoffReady(s, running, group))
                {
                    best = group;
                    if (fewerEngines.Contains(group.Mask)) break;
                }
            }
            if (best != null)
            {
                command = BeginFinal(s, best, side, up, "reduce final bank at minimum thrust");
                return true;
            }
            if (running.Count <= 1) return false;

            // A controllable smaller bank may be disjoint from a symmetric
            // running pair. A cold handoff must fit after both are shut down.
            Flight coast = s;
            coast.Running = coast.Starting = coast.IssuedStarts = 0;
            coast.ExternalUpAcceleration = coast.ExternalLateralAcceleration = 0;
            Plan relight = FindRelight(coast, running.Count, Math.Max(0.12, 2 * cfg.SampleSeconds));
            if (relight.Main == null) return false;
            ResetUnpowered(Phase.Recoast);
            relightShutdownMask = s.Running;
            relightBankLimit = running.Count;
            plan = relight;
            command = Output(s, 0, plan.RelightX, plan.RelightUp, "final minimum thrust requires a smaller relight bank");
            return true;
        }

        private bool CanKeepFinalGroup(Flight s, Group group, bool recoveringRise)
        {
            if (group == null || group.ContainsUnusableEngine || !Eligible(group, s) ||
                !Subset(group.Mask, s.Running)) return false;
            double minimum = group.Min + Settings.MinimumPositiveThrottle * (group.Max - group.Min);
            return minimum <= Limit(group, s, true) &&
                (!recoveringRise || minimum / s.Mass + s.ExternalUpAcceleration < cfg.Gravity);
        }

        private Group RunningFinalGroup(Flight s, bool recoveringRise)
        {
            // A selected, live set can remain controllable at its operating
            // throttle even if a refresh rejects an endpoint of its range.
            Group live = selected != 0 && Subset(selected, s.Running)
                ? catalogBuilder.MakeGroup(selected) : null;
            if (CanKeepFinalGroup(s, live, recoveringRise)) return live;

            Group best = null;
            foreach (Group group in groups)
            {
                if (!CanKeepFinalGroup(s, group, recoveringRise)) continue;
                if (best == null || Limit(group, s, true) > Limit(best, s, true) ||
                    (Limit(group, s, true) == Limit(best, s, true) && CompareGroups(group, best) < 0))
                    best = group;
            }
            return best;
        }

        private Command RecoverCappedFinal(Flight s, double x, double up, bool recoveringRise = false)
        {
            bool filteredGroup = yoloTrigger == "final_group_unavailable";
            yoloTrigger = null;
            yoloInhibit = "capped_final";
            Group live = RunningFinalGroup(s, recoveringRise);
            if (live == null)
            {
                if (phase == Phase.Recoast || phase == Phase.Coast)
                    return Output(s, 0, 0, 1, "coast, no capped terminal relight available");
                return BeginRecoast(s, recoveringRise
                    ? "airborne reversal, active engines cannot throttle below hover"
                    : "coast, no usable running group within max G");
            }

            selected = live.Mask;
            phase = Phase.Final;
            if (recoveringRise)
                return Output(s, selected, s.ExternalLateralAcceleration, s.ExternalUpAcceleration + 0.01,
                    "final, reduce live throttle until descent resumes");
            if (s.UprightAngle >= Math.PI / 2 || (s.EngineThrustMeasured && s.VerticalThrustAcceleration < 0))
                return Output(s, selected, s.ExternalLateralAcceleration, s.ExternalUpAcceleration + 0.01,
                    "final, hold minimum thrust while pointing up");

            double ceiling = Limit(live, s, true) / s.Mass;
            double axial = Math.Max(0, up - s.ExternalUpAcceleration);
            if (axial > 1.005 * ceiling && !cfg.IgnitionAltitudeBlocked(s.Height))
            {
                Group reinforced = DirectCappedGroup(s, EmergencyImpactSeconds(s), out _, out _, live.Mask);
                if (reinforced != null)
                {
                    plan.RelightX = x;
                    plan.RelightUp = Math.Max(0.01, up);
                    return BeginFinal(s, reinforced, x, plan.RelightUp, "final, max throttle insufficient, add capped thrust");
                }
            }
            double side = Math.Sqrt(Math.Max(0, ceiling * ceiling - axial * axial));
            x = s.ExternalLateralAcceleration + Math.Max(-side, Math.Min(0, x - s.ExternalLateralAcceleration));
            return Output(s, selected, LeadFinalLateral(s, live, x, up), Math.Max(0.01, up), filteredGroup
                ? "final, retain burning engines after group refresh"
                : "final, adjust existing throttle within max G");
        }

        private bool TerminalTurnFits(Flight s, Group group, bool capped = true, bool allowRelight = true)
        {
            const double step = 0.05;
            double horizontal = s.Vx;
            double angle = Math.Atan2(s.LateralThrustDirection, Math.Cos(s.UprightAngle));
            double rate = s.PitchRate;
            double thrust = Norm(Norm(s.LateralThrustAcceleration, s.VerticalThrustAcceleration),
                s.CrossThrustAcceleration);
            double nozzleSideError = s.LateralThrustAcceleration - thrust * Math.Sin(angle);
            double nozzleUpError = s.VerticalThrustAcceleration - thrust * Math.Cos(angle);
            double cross = 0;
            double measuredSide = s.ExternalLateralAcceleration;
            double measuredLift = s.ExternalUpAcceleration;
            double startMass = s.Mass;
            double horizon = 2 * TravelTime(s, 0, cfg.TouchSpeed);
            for (double time = 0; time < horizon; time += step)
            {
                double direction = horizontal < 0 ? -1 : 1;
                double decay = cfg.AttitudeResponseSeconds > 0 ? Math.Exp(-time / (2 * cfg.AttitudeResponseSeconds)) : 1;
                s.ExternalLateralAcceleration = direction * measuredSide * decay;
                s.ExternalUpAcceleration = measuredLift * decay;
                s.Vx = Math.Abs(horizontal);
                s.UprightAngle = Math.Abs(angle);
                s.LateralThrustDirection = direction * Math.Sin(angle);
                s.LateralThrustAcceleration = s.LateralThrustDirection * thrust;
                s.TerminalAlignmentSeconds = s.UprightAngle / (Math.PI / 6);
                double x, up;
                FinalVector(s, out x, out up);
                double remaining = TravelTime(s, 0, cfg.TouchSpeed);
                double nozzleUp = Math.Max(0.01, up - s.ExternalUpAcceleration);
                double sideLimit = nozzleUp * (0.2 + Math.Min(0.8, 0.25 * remaining));
                double nozzleSide = Math.Max(-sideLimit, Math.Min(0, x - s.ExternalLateralAcceleration));
                double target = Math.Atan2(direction * nozzleSide, nozzleUp);
                double error = target - angle;
                // Use the single-engine rate limiter and the acceleration
                // it has demonstrated while turning under thrust.
                const double terminalTurnAcceleration = Settings.TerminalTurnBraking;
                double rateLimit = Math.Min(0.55, Math.Sqrt(2 * terminalTurnAcceleration * Math.Abs(error)));
                double desiredRate = Math.Max(-rateLimit, Math.Min(rateLimit, error / 0.8));
                double change = terminalTurnAcceleration * step;
                rate += Math.Max(-change, Math.Min(change, desiredRate - rate));
                angle = cfg.AttitudeResponseSeconds > 0 ? angle + rate * step : target;

                double ceiling = Limit(group, s, capped) / s.Mass;
                double floor = group.Min / s.Mass;
                if (ceiling < floor) return false;
                double projection = Math.Max(0.5, Math.Min(Math.Cos(angle), Math.Cos(target)));
                double request = Math.Max(floor, Math.Min(ceiling, nozzleUp / projection));
                thrust += (request - thrust) * Math.Min(1, step / 0.16);
                // Carry the measured nozzle / hull mismatch through the
                // response interval, including thrust out of the flight plane.
                double nozzleDecay = cfg.AttitudeResponseSeconds > 0
                    ? Math.Exp(-time / cfg.AttitudeResponseSeconds) : 0;
                double vertical = thrust * Math.Cos(angle) + nozzleDecay * nozzleUpError +
                    s.ExternalUpAcceleration - cfg.Gravity;
                horizontal += step * (thrust * Math.Sin(angle) + measuredSide * decay + nozzleDecay * nozzleSideError);
                cross += step * (nozzleDecay * s.CrossThrustAcceleration + s.CrossExternalAcceleration * decay);
                s.Height += step * (s.Vz + 0.5 * vertical * step);
                s.Vz += step * vertical;
                s.Mass = MassAfter(s.Mass, thrust, step, group);
                if (s.Height <= 0)
                    return Norm(horizontal, cross) <= 2 && s.Vz >= -2 * cfg.TouchSpeed && Math.Abs(angle) < Math.PI / 9 &&
                        FitsPropellant(s, group, startMass - s.Mass);
                if (s.Vz > 0)
                {
                    if (!allowRelight) return false;
                    // A high minimum may arrest descent early. Admit the
                    // handoff only if the remaining coast permits a relight.
                    s.Running = s.Starting = 0;
                    s.Vx = Norm(horizontal, cross);
                    s.PitchRate = Math.Sign(horizontal) * rate;
                    s.MeasuredTurnRate = Math.Max(s.MeasuredTurnRate, Math.Abs(rate));
                    double observedMargin = cappedPlanMargin;
                    bool relightFits = FindRelight(s).Main != null;
                    cappedPlanMargin = observedMargin;
                    return relightFits;
                }
            }
            return false;
        }

        private Command ContinueMain(Flight s, Group terminal, double fx, double fz,
                                     double x, double up, string reason)
        {
            x = PreserveMainSideThrust(s, x, up);
            if (terminal != null && cfg.AttitudeResponseSeconds > 0 &&
                !TerminalWindowSurvives(s, terminal.Count, x, up))
                return BeginFinal(s, terminal, fx, fz, "handoff before losing the terminal window");
            return Output(s, selected, x, up, reason);
        }

        private static bool MainHandoffLateralReady(Flight s) =>
            s.Vz < 0 && (s.SurfaceHorizontalSpeed ?? Math.Abs(s.Vx)) <= -s.Vz * MainHandoffHorizontalRatio;

        private bool TryBrakeLaterallyBeforeHandoff(Flight s, Group main, Group terminal, out double x, out double up)
        {
            x = up = 0;
            return !MainHandoffLateralReady(s) && cfg.AttitudeResponseSeconds > 0 &&
                plan.Main != null && plan.Final != null && plan.Main.Mask == main.Mask &&
                CanTrackMain(s, main, plan, out x, out up) && TerminalWindowSurvives(s, terminal.Count, x, up);
        }

        private double PreserveMainSideThrust(Flight s, double x, double up)
        {
            Group main = plan.Main, final = plan.Final;
            if (!s.AttitudeRateMeasured || cfg.AttitudeResponseSeconds <= 0 || main == null || final == null ||
                main.Mask == final.Mask || main.Mask != selected || !Subset(main.Mask, s.Running) || up <= 0)
                return x;
            double beforeJoin = TravelTime(s, plan.JoinHeight, plan.JoinSpeed);
            if (beforeJoin < 0 || beforeJoin > 1.5 * cfg.AttitudeResponseSeconds) return x;
            double direction = Math.Sign(s.Vx);
            double braking = -direction * s.LateralThrustAcceleration;
            if (braking <= 0) return x;

            // Pitch is already producing useful side thrust. Do not assume
            // the main bank keeps that force after the planned down-select.
            // Stop holding the old sideways attitude as the measured speed
            // reaches the handoff cone. Estimate the remaining impulse using
            // the nozzles of the engines that will actually survive the cut.
            if (MainHandoffLateralReady(s)) return x;
            Flight reduced = ThrustAfterDownselect(s, main, final);
            double reducedBraking = Math.Max(0, -direction * reduced.LateralThrustAcceleration);
            double carry = Math.Max(cfg.SampleSeconds, beforeJoin + cfg.AttitudeResponseSeconds * reducedBraking / braking);
            double allowed = (Math.Abs(s.Vx) - HorizontalTolerance) / carry + direction * s.ExternalLateralAcceleration;
            double ceiling = Limit(main, s, true) / s.Mass;
            double verticalRoom = Math.Sqrt(Math.Max(0, ceiling * ceiling - up * up));
            double preserve = Math.Min(braking, Math.Min(verticalRoom, Math.Max(0, allowed)));
            return direction * Math.Min(direction * x, -preserve);
        }

        private bool TerminalWindowSurvives(Flight s, int terminalCount, double x, double up)
        {
            // Predict one response interval under the currently requested
            // main burn, starting from the achieved thrust and body attitude.
            double delay = Math.Min(cfg.AttitudeResponseSeconds, TravelTime(s, 0, cfg.TouchSpeed) / 4);
            double measured = Norm(s.LateralThrustAcceleration, s.VerticalThrustAcceleration);
            if (delay <= cfg.SampleSeconds || measured <= 0) return true;
            s = PredictMainResponse(s, x, up, delay, measured);
            return s.Height > 0 && FinalVector(s, out x, out up) && FinalGroup(s, true, x, up, terminalCount) != null;
        }

        private bool ContinuousHandoffWindowClosing(Flight s, Group main, Group terminal, double up)
        {
            if (!s.AttitudeRateMeasured || cfg.AttitudeResponseSeconds <= 0 || terminal.Mask == main.Mask)
                return false;
            Flight reduced = ThrustAfterDownselect(s, main, terminal);
            // The instantaneous rate gate is deliberately conservative. If
            // keeping the current minimum thrust would spend the window, test
            // the complete powered descent with the actual turn in progress.
            return TerminalVerticalAuthority(reduced, terminal, up) &&
                !TerminalWindowSurvives(s, terminal.Count, s.LateralThrustAcceleration, s.VerticalThrustAcceleration) &&
                TerminalTurnFits(reduced, terminal, allowRelight: false);
        }

        private Flight PredictMainResponse(Flight s, double x, double up, double delay, double measured)
        {
            double response = 1 - Math.Exp(-delay / cfg.AttitudeResponseSeconds);
            double angle = Math.Sign(s.LateralThrustDirection) * s.UprightAngle;
            double travel = response * (Math.Atan2(x, up) - angle);
            double maxTravel = delay * Math.PI / 9;
            double nextAngle = angle + Math.Max(-maxTravel, Math.Min(maxTravel, travel));
            double nextThrust = measured + response * (Norm(x, up) - measured);
            double ax = 0.5 * (s.LateralThrustAcceleration + nextThrust * Math.Sin(nextAngle)) + s.ExternalLateralAcceleration;
            double az = 0.5 * (s.VerticalThrustAcceleration + nextThrust * Math.Cos(nextAngle)) +
                s.ExternalUpAcceleration - cfg.Gravity;
            s.Height += (s.Vz + 0.5 * az * delay) * delay;
            s.Vz += az * delay;
            s.Vx += ax * delay;
            s.Mass = MassAfter(s.Mass, 0.5 * (measured + nextThrust), delay);
            s.LateralThrustAcceleration = nextThrust * Math.Sin(nextAngle);
            s.VerticalThrustAcceleration = nextThrust * Math.Cos(nextAngle);
            s.EngineThrustAccelerations = null;
            s.LateralThrustDirection = Math.Sin(nextAngle);
            double turnRate = s.UprightAngle > 0 && s.TerminalAlignmentSeconds > 0
                ? s.UprightAngle / s.TerminalAlignmentSeconds : double.PositiveInfinity;
            s.UprightAngle = Math.Abs(nextAngle);
            s.TerminalAlignmentSeconds = s.UprightAngle / turnRate;
            if (s.Vx < 0)
            {
                s.Vx = -s.Vx;
                s.LateralThrustAcceleration = -s.LateralThrustAcceleration;
                s.LateralThrustDirection = -s.LateralThrustDirection;
                s.ExternalLateralAcceleration = -s.ExternalLateralAcceleration;
            }
            return s;
        }

        private bool TryContinueCappedMain(Flight s, Group running, out double x, out double up)
        {
            x = up = 0;
            if (cfg.AttitudeResponseSeconds <= 0 || s.Vz >= 0) return false;
            double actualTilt = Math.Cos(s.UprightAngle);
            if (s.Vx * s.LateralThrustDirection + s.Vz * actualTilt >= 0) return false;

            // The handoff trajectory can be temporarily infeasible while
            // the hull is moving toward its aim. Replan again after one short
            // response interval if the current group still has braking room.
            if (!MainVector(s, 0, cfg.TouchSpeed, out x, out up, reducingEngines: true, fadeLift: true))
                return false;
            double ceiling = Limit(running, s, true) / s.Mass;
            double floor = running.Min / s.Mass;
            double magnitude = Norm(x, up);
            double targetTilt = up / magnitude;
            double projection = Math.Max(0.2, (actualTilt + targetTilt) / 2);
            double acceleration = Math.Max(floor, Math.Min(ceiling, up / Math.Min(targetTilt, projection)));
            x *= acceleration / magnitude;
            up *= acceleration / magnitude;
            double delay = Math.Min(cfg.AttitudeResponseSeconds, BallisticImpactSeconds(s.Height, s.Vz, cfg.Gravity) / 4);
            if (delay <= cfg.SampleSeconds) return false;
            Flight next = PredictMainResponse(s, x, up, delay, Norm(s.LateralThrustAcceleration, s.VerticalThrustAcceleration));
            if (next.Height <= 0 || next.Vz >= 0) return false;
            double alignment = AimError(next, -next.Vx, Math.Max(1, -next.Vz)) / (Math.PI / 9);
            if (CoastMargin(next, alignment, ceiling, 0, cfg.TouchSpeed) >= 0) return true;

            // Keep credit for upward thrust already available during the
            // remaining turn. A ballistic delay here would model an engine
            // cutoff even though this command keeps the group firing.
            double upwardThrust = Math.Min(s.VerticalThrustAcceleration, next.VerticalThrustAcceleration);
            double netUp = upwardThrust + Math.Min(0, s.ExternalUpAcceleration) - cfg.Gravity;
            if (netUp <= 0) return false;
            double turning = Math.Min(alignment, -next.Vz / (2 * netUp));
            next.Height += turning * (next.Vz + 0.5 * netUp * turning);
            next.Vz += turning * netUp;
            next.Vx += turning * Math.Max(0, next.LateralThrustAcceleration + next.ExternalLateralAcceleration);
            return next.Height > 0 && CoastMargin(next, 0, ceiling, 0, cfg.TouchSpeed) >= 0;
        }

        private Command Output(Flight s, long engines, double x, double up, string reason)
        {
            double accel = Norm(x, up);
            double cap = cfg.ThrustLimitFor(phase) * Settings.G0;
            // Powered Final passes a net braking vector to the flight adapter,
            // which subtracts aerodynamics and caps the actual nozzle thrust.
            bool netTerminalVector = phase == Phase.Final && s.Running != 0;
            if (engines != 0 && !emergency && !netTerminalVector && accel > cap)
            {
                x *= cap / accel;
                up *= cap / accel;
            }
            long planned = engines;
            if (planned == 0 && (phase == Phase.Coast || phase == Phase.Recoast) && plan.Main != null)
                planned = plan.Main.Mask;
            bool relighting = plan.RelightUp > 0 && (phase == Phase.Recoast || phase == Phase.Final);
            double alignment = Subset(planned, s.Running) ? 0 :
                relighting ? RelightAlignment(s, plan.RelightX, plan.RelightUp) : s.AlignmentSeconds;
            return new Command { Phase = phase, Engines = engines, X = x, Up = up, Reason = reason,
                IgnitionLead = EngineStartDelay(planned, s) + alignment, IgnitionAlignmentSeconds = alignment,
                Emergency = emergency, VerticalOnly = verticalOnly,
                YoloTrigger = yoloTrigger, YoloInhibit = yoloInhibit, YoloState = yoloState,
                YoloTerminalGuidance = yoloTerminalGuidance,
                CappedPlanMargin = cappedPlanMargin, PlannedTerminalSeconds = plan.TerminalSeconds,
                PlannedJoinHeight = plan.JoinHeight, PlannedJoinSpeed = plan.JoinSpeed,
                PlannedJoinMass = plan.JoinMass, PlannedFinalThrottle = plan.JoinMass > 0 ? plan.JoinThrottle : double.NaN,
                PlannedIgnitionWait = planned != 0 && phase == Phase.Coast ? plan.Wait : double.NaN,
                PlannedPropellantKg = plan.Propellant * 1000,
                PropellantShortfallKg = double.IsPositiveInfinity(propellantShortfall) ? 0 : propellantShortfall * 1000,
                FuelPlanSelected = plan.FuelOptimized,
                DeployGear = !s.Contact && !s.SolidBurnActive && !s.GearCommanded && CanDeployGear(s) };
        }

        private void ResetUnpowered(Phase next)
        {
            phase = next;
            selected = 0;
            emergency = false;
            emergencyGroup = 0;
            awaitingEmergencyStarts = false;
            verticalOnly = false;
            entryExitReason = null;
            relightShutdownMask = 0;
            relightBankLimit = int.MaxValue;
            CancelCatalogSearch();
            coastSearch = null;
            plan = new Plan();
        }

        internal static double MinimumRunningThrust(Group group) =>
            group.Min + Settings.MinimumPositiveThrottle * (group.Max - group.Min);

        private double EntryThrust(Group group, Flight s)
        {
            if (group == null || (group.Mask & fixedEngines) != 0 || !Eligible(group, s) ||
                double.IsPositiveInfinity(EngineStartDelay(group.Mask, s))) return 0;
            double limit = Limit(group, s, true, Phase.EntryBurn);
            return MinimumRunningThrust(group) <= limit ? limit : 0;
        }

        internal double EntryTiltDegrees { get; set; }

        private Command ContinueEntryBurn(Flight s)
        {
            if (entryExitReason != null) return EndEntryBurn(s, entryExitReason);
            if (s.SurfaceSpeed <= cfg.EntryBurnSpeed)
                return EndEntryBurn(s, "entry burn target reached");

            Group group = LiveGroup(selected);
            double thrust = EntryThrust(group, s);
            if (thrust <= 0)
            {
                CompleteCatalog();
                double bestDelay = double.PositiveInfinity;
                foreach (Group candidate in groups)
                {
                    double available = EntryThrust(candidate, s);
                    if (available <= 0 || available < thrust) continue;
                    double delay = EngineStartDelay(candidate.Mask, s);
                    if (available == thrust)
                    {
                        // Once the allowed thrust is matched, starting more
                        // engines cannot increase deceleration.
                        int preference = candidate.Count.CompareTo(group.Count);
                        if (preference == 0) preference = delay.CompareTo(bestDelay);
                        if (preference == 0) preference = Group.InsideFirst(candidate, group);
                        if (preference == 0) preference = Group.CompareBalance(candidate, group);
                        if (preference >= 0) continue;
                    }
                    group = candidate;
                    thrust = available;
                    bestDelay = delay;
                }
            }
            if (thrust <= 0) return EndEntryBurn(s, "entry burn ended, no available engine group within max G");

            selected = group.Mask;
            double acceleration = thrust / s.Mass;
            double up = Math.Max(-1, Math.Min(1, -s.Vz / s.SurfaceSpeed));
            double side = -Math.Sqrt(1 - up * up);
            if (EntryTiltDegrees == 0 || s.Vx < 1)
                return Output(s, selected, acceleration * side, acceleration * up,
                    "entry burn, brake along surface retrograde");
            double angle = EntryTiltDegrees * AttitudeGuidance.Degree;
            double cosine = Math.Cos(angle), sine = Math.Sin(angle);
            return Output(s, selected, acceleration * (side * cosine + up * sine),
                acceleration * (up * cosine - side * sine), "entry burn, follow optimized trajectory");
        }

        private Command EndEntryBurn(Flight s, string reason)
        {
            // Do not let the landing controller adopt residual entry thrust
            // as a new landing burn as the engines shut down.
            if ((s.Running & ~fixedEngines) != 0)
            {
                entryExitReason = reason;
                return Output(s, 0, -s.Vx, -s.Vz, reason + ", wait for thrust to decay");
            }
            ResetUnpowered(Phase.Coast);
            return AlignForMain(s, 0, reason);
        }

        private Command BeginRecoast(Flight s, string reason)
        {
            ResetUnpowered(Phase.Recoast);
            return Output(s, 0, 0, 1, reason);
        }

        public Command Tick(Flight s)
        {
            bool crossedEntryAltitude = previousEntryAltitude > cfg.EntryBurnAltitude &&
                s.SeaLevelAltitude <= cfg.EntryBurnAltitude && s.Vz < 0;
            previousEntryAltitude = s.SeaLevelAltitude;
            yoloTrigger = null;
            yoloInhibit = null;
            yoloState = null;
            yoloTerminalGuidance = false;
            cappedPlanMargin = double.NegativeInfinity;
            propellantShortfall = double.PositiveInfinity;
            if (s.Contact)
            {
                phase = Phase.Landed;
                CancelCatalogSearch();
                return Output(s, 0, 0, 0, "contact confirmed");
            }
            if (s.SolidBurnActive)
            {
                ResetUnpowered(Phase.Coast);
                yoloInhibit = "solid_motor_burning";
                return AlignForMain(s, 0, "attitude only, waiting for solid motor burnout");
            }
            UpdateGroupAvailability(s);
            if (phase == Phase.Final && !emergency) CappedFinalCommitted = true;
            if (phase == Phase.Coast && crossedEntryAltitude && s.SurfaceSpeed > cfg.EntryBurnSpeed)
                ResetUnpowered(Phase.EntryBurn);
            if (phase == Phase.EntryBurn)
                return ContinueEntryBurn(s);
            bool ignitionBlocked = cfg.IgnitionAltitudeBlocked(s.Height);
            bool runningLandingThrust = s.Vz <= 0.02 && (s.Running & ~fixedEngines) != 0;
            if (ignitionBlocked && phase == Phase.Coast && runningLandingThrust)
            {
                // Adopt a powered descent, without mistaking rising-stage
                // separation thrust for an interrupted landing burn.
                phase = Phase.Main;
                selected = s.Running;
            }
            if (phase == Phase.Final && CappedFinalCommitted && s.Vz > 0.02)
                return RecoverCappedFinal(s, 0, 0, recoveringRise: true);
            if ((phase == Phase.Main || phase == Phase.Final || phase == Phase.Yolo) && s.Vz > 0.02)
                return BeginRecoast(s, "airborne reversal, shut down and recompute relight");
            if (ignitionBlocked && (phase == Phase.Main || phase == Phase.Final || phase == Phase.Yolo))
            {
                // A live altitude change may invalidate a queued start. Do
                // not cut an active burn or interrupt a start already issued.
                if ((s.Running | s.IssuedStarts) == 0)
                    ResetUnpowered(phase == Phase.Final ? Phase.Recoast : Phase.Coast);
                else
                {
                    s.Starting &= s.IssuedStarts;
                    s.Restartable = 0;
                }
            }
            if (phase == Phase.Coast || phase == Phase.Recoast)
            {
                bool direct = phase == Phase.Recoast;
                if (direct && ((s.Running | s.IssuedStarts) & relightShutdownMask) != 0)
                    return Output(s, 0, plan.RelightX, plan.RelightUp, "coast, waiting for the previous bank to shut down");
                if (direct && s.Vx * s.Vx + s.Vz * s.Vz + 2 * cfg.Gravity * s.Height <=
                    4 * cfg.TouchSpeed * cfg.TouchSpeed)
                {
                    plan = new Plan();
                    return Output(s, 0, 0, 1, "coast through the final contact window");
                }
                if (!direct && Math.Abs(s.Vx) <= HorizontalTolerance && runningLandingThrust)
                {
                    selected = s.Running;
                    phase = Phase.Final;
                }
                else
                {
                    plan = direct ? FindRelight(s, relightBankLimit) : FindCoastPlan(s, ignitionBlocked);
                    if (direct && plan.Main == null && relightBankLimit != int.MaxValue)
                        plan = FindRelight(s);
                    if (ignitionBlocked)
                    {
                        yoloInhibit = "ignition_altitude_limit";
                        if (direct)
                            return Output(s, 0, plan.RelightX, plan.RelightUp, "coast, wait below ignition ceiling to relight");
                        return AlignForMain(s, 0, plan.Main == null
                            ? "coast, above ignition ceiling without a capped plan below it yet"
                            : "coast, track the planned window below the ignition ceiling");
                    }
                    if (!direct && plan.Main == null)
                    {
                        // A continuous burn from a predicted high-energy state may
                        // have no min-thrust handoff. Preserve time to replan while
                        // braking still fits even in the ballistic case.
                        double reserve = Math.Max(cfg.PrecisionSeconds, cfg.AttitudeResponseSeconds);
                        if (s.Running == 0 && s.Starting == 0 &&
                            ((catalogSearch != null && MayWaitForCatalog(s)) || CappedBrakeFitsAfter(s, reserve)))
                            return AlignForMain(s, 0, !double.IsPositiveInfinity(propellantShortfall)
                                ? "coast, current plan exceeds reachable propellant"
                                : "coast, retry main plan while capped braking has margin");
                        // A late engagement may already have spent the reserve.
                        CompleteCatalog();
                        plan = FindPlan(s, planningThrottle: 1, igniteNow: true);
                    }
                    if (!direct && (plan.Main == null || plan.Wait <= cfg.SampleSeconds * 1.5))
                    {
                        if (FindLaterAeroLanding(s, ref plan))
                        {
                            RecordCoastCheck(s, plan);
                            return AlignForMain(s, 0, "coast, modeled aerodynamic braking opens a later powered landing window");
                        }
                        if (AirBrakingCanDefer(s))
                            return AlignForMain(s, 0, "coast, retain useful aerodynamic braking");
                    }
                    if (plan.Main != null)
                    {
                        if (plan.Wait > cfg.SampleSeconds * 1.5 || s.Vz > 0 ||
                            (direct && RelightAlignment(s, plan.RelightX, plan.RelightUp) > 0))
                            return direct
                                ? Output(s, 0, plan.RelightX, plan.RelightUp, "coast, align for terminal relight")
                                : AlignForMain(s, 0, "coast, align for planned main burn");
                        selected = plan.Main.Mask;
                        phase = direct ? Phase.Final : Phase.Main;
                        if (direct)
                        {
                            emergency = false;
                            usedAlignmentRelight |= plan.ImpendingRelightGate;
                        }
                        if (!direct)
                        {
                            Command start = AlignForMain(s, selected, "establish main thrust");
                            mainStartX = start.X;
                            mainStartUp = start.Up;
                        }
                    }
                    else if (phase == Phase.Recoast && s.Vz > 0)
                        return Output(s, 0, 0, 1, "coast to apex before relight");
                    else if (direct && HasTimeToObserveRelightTurn(s))
                        return Output(s, 0, 0, 1, "coast, continue turning into the relight window");
                    else
                    {
                        yoloTrigger = !double.IsPositiveInfinity(propellantShortfall) ? "landing_propellant_shortfall" :
                            phase == Phase.Recoast ? "recoast_plan_unavailable" : "coast_plan_unavailable";
                        double side, up;
                        FinalVector(s, out side, out up);
                        return RecoverOrEscalate(s, side, up);
                    }
                }
            }

            Group running = LiveGroup(selected);
            bool pending = (s.Starting & selected) != 0;
            double fx, fz;
            bool nearVertical = FinalVector(s, out fx, out fz);
            if (phase == Phase.Main && running != null && Eligible(running, s) && running.Min <= Limit(running, s, true))
            {
                if (pending || (s.Running & selected) == 0)
                    return Output(s, selected, mainStartX, mainStartUp, "establish main thrust");
                Group terminalGroup = nearVertical && plan.Final != null
                    ? SelectFinalGroup(s, true, fx, fz, plan.Final.Count, plan.Final.Mask) : null;
                if (terminalGroup != null && !TerminalHandoffReady(s, running, terminalGroup))
                {
                    if (ContinuousHandoffWindowClosing(s, running, terminalGroup, fz))
                        return BeginFinal(s, terminalGroup, fx, fz, "use continuous terminal handoff before minimum thrust closes window");
                    terminalGroup = null;
                }
                if (terminalGroup != null && terminalGroup.Mask != running.Mask && cfg.AttitudeResponseSeconds > 0 &&
                    s.UprightAngle > Math.PI / 6 && LateralReversalImminent(s, cfg.AttitudeResponseSeconds / 2) &&
                    !TerminalTurnFits(ThrustAfterDownselect(s, running, terminalGroup), terminalGroup))
                    terminalGroup = null;
                double remaining = TravelTime(s, 0, cfg.TouchSpeed);
                bool reserveChanged = plan.RequestedTerminalSeconds != cfg.PrecisionSeconds;
                if (terminalGroup != null && !reserveChanged && remaining <= plan.TerminalSeconds + 2 * cfg.SampleSeconds)
                {
                    if (TryBrakeLaterallyBeforeHandoff(s, running, terminalGroup, out double side, out double vertical))
                        return ContinueMain(s, terminalGroup, fx, fz, side, vertical, "main, reduce horizontal speed before handoff");
                    return BeginFinal(s, terminalGroup, fx, fz, "planned terminal window reached");
                }

                double x, up;
                if (!reserveChanged && plan.Main != null && CanTrackMain(s, running, plan, out x, out up))
                {
                    const double throttleTolerance = 0.05;
                    double previousError = plan.JoinMass > 0 ? PlannedThrottleError(plan) : 0;
                    UpdateJoinEstimate(s, running, ref plan, Norm(x, up));
                    double error = PlannedThrottleError(plan);
                    if (previousError <= throttleTolerance && error > throttleTolerance &&
                        remaining > plan.TerminalSeconds + cfg.AttitudeResponseSeconds)
                    {
                        Plan revised = FindMainTrajectory(s, running);
                        double revisedX, revisedUp;
                        if (revised.Main != null && PlannedThrottleError(revised) < error &&
                            Math.Abs(revised.TerminalSeconds - cfg.PrecisionSeconds) <= Math.Abs(plan.TerminalSeconds - cfg.PrecisionSeconds) &&
                            CanTrackMain(s, running, revised, out revisedX, out revisedUp))
                        {
                            plan = revised;
                            return Output(s, selected, revisedX, revisedUp, "replan main brake for predicted handoff throttle");
                        }
                    }
                    return ContinueMain(s, terminalGroup, fx, fz, x, up, "track main brake into terminal window");
                }

                Plan replan = ReplanMain(s, running);
                if (replan.Main == null || replan.Main.Mask != running.Mask)
                {
                    Group recovery = RecoverTerminal(s, running, plan.Final == null ? running.Count : plan.Final.Count);
                    if (recovery != null)
                        return BeginFinal(s, recovery, fx, fz, "terminal group can finish the remaining descent");
                }
                if (replan.Main != null && MainVector(ThrustAfterDownselect(s, running, replan.Main),
                    replan.JoinHeight, replan.JoinSpeed, out x, out up, replan.Main.Mask != replan.Final.Mask, replan.FadeMainLift))
                {
                    plan = replan;
                    selected = replan.Main.Mask;
                    if (terminalGroup != null && !Subset(terminalGroup.Mask, selected)) terminalGroup = null;
                    if (nearVertical && remaining <= plan.TerminalSeconds + 2 * cfg.SampleSeconds)
                    {
                        Group replannedTerminal = SelectFinalGroup(s, true, fx, fz, plan.Final.Count, plan.Final.Mask);
                        if (replannedTerminal != null && TerminalHandoffReady(s, running, replannedTerminal))
                        {
                            if (TryBrakeLaterallyBeforeHandoff(s, running, replannedTerminal, out double side, out double vertical))
                                return ContinueMain(s, replannedTerminal, fx, fz, side, vertical, "main, reduce horizontal speed before handoff");
                            return BeginFinal(s, replannedTerminal, fx, fz, "replanned terminal window reached");
                        }
                    }
                    return ContinueMain(s, terminalGroup, fx, fz, x, up, "replan continuous main and terminal burn");
                }
                if (reserveChanged && plan.Main != null && CanTrackMain(s, running, plan, out x, out up))
                {
                    plan.RequestedTerminalSeconds = cfg.PrecisionSeconds;
                    return ContinueMain(s, terminalGroup, fx, fz, x, up, "keep feasible main plan after changing final reserve");
                }
                if (terminalGroup != null)
                    return BeginFinal(s, terminalGroup, fx, fz, "terminal set can finish descent before main plan is lost");

                double untilJoin = TravelTime(s, plan.JoinHeight, plan.JoinSpeed);
                double ceiling = Limit(running, s, true) / s.Mass;
                if (plan.Main != null && untilJoin <= cfg.AttitudeResponseSeconds &&
                    MainVector(s, plan.JoinHeight, plan.JoinSpeed, out x, out up, selected != plan.Final.Mask, plan.FadeMainLift) &&
                    Norm(x, up) <= ceiling && CoastMargin(s, 0, ceiling, 0, cfg.TouchSpeed) >= 0)
                {
                    double floor = running.Min / s.Mass;
                    if (Norm(x, up) < floor) up = Math.Sqrt(floor * floor - x * x);
                    return Output(s, selected, x, up, "continue capped braking near the handoff");
                }
                if (TryContinueCappedMain(s, running, out x, out up))
                    return Output(s, selected, x, up, "continue capped braking while replanning the handoff");
            }
            if (phase == Phase.Final && running != null && Eligible(running, s) &&
                running.Min <= Limit(running, s, true))
            {
                if (pending || (s.Running & selected) == 0)
                    return plan.RelightUp > 0
                        ? Output(s, selected, plan.RelightX, plan.RelightUp, plan.ImpendingRelightGate
                            ? "establish terminal thrust before the attitude window closes" : "establish terminal thrust")
                        : Output(s, selected, 0, 1, "establish terminal thrust");
                // The minimum thrust limits nozzle magnitude. At an angle
                // its vertical projection can be below the engine floor.
                if (TryReduceFinalFloor(s, running, fx, fz, out Command reduction)) return reduction;
                double terminalUp = Math.Max(0.01, fz);
                if (TryTerminalThrust(s, running, ref fx, fz))
                    return Output(s, selected, LeadFinalLateral(s, running, fx, fz), terminalUp, "track touchdown speed");
                if (TerminalTurnFits(s, running))
                    return Output(s, selected, LeadFinalLateral(s, running, fx, fz), terminalUp, "keep capped terminal braking through the turn");
            }
            if (phase == Phase.Main || phase == Phase.Final)
            {
                string prefix = phase == Phase.Main ? "main_" : "final_";
                yoloTrigger = running == null || !Eligible(running, s) ? prefix + "group_unavailable"
                    : running.Min > Limit(running, s, true) ? prefix + "minimum_above_limit"
                    : prefix + "braking_vector_unavailable";
            }
            return RecoverOrEscalate(s, fx, fz);
        }

        private double EmergencyImpactSeconds(Flight s)
        {
            double height = s.Height;
            double acceleration = s.VerticalThrustAcceleration + s.ExternalUpAcceleration - cfg.Gravity;
            double discriminant = s.Vz * s.Vz - 2 * acceleration * height;
            if (discriminant < 0) return double.PositiveInfinity;
            double closing = Math.Sqrt(discriminant) - s.Vz;
            return closing > 0 ? 2 * height / closing : double.PositiveInfinity;
        }

        private double EmergencyThrustLead(Group group, Flight s)
        {
            double lead = 0;
            for (int i = 0; i < specs.Length; i++)
            {
                long bit = 1L << i;
                if ((bit & group.Mask & ~s.Running) == 0) continue;
                double spool = specs[i].SpoolSeconds;
                if (!EngineResponse.Known(spool)) return double.PositiveInfinity;
                double elapsed = s.IgnitionElapsed == null ? -1 : s.IgnitionElapsed[i];
                double delay = elapsed >= 0 ? Math.Max(0, spool - elapsed) : spool + s.AlignmentSeconds +
                    ((bit & s.Unsettled) != 0 ? s.UllageSeconds : 0);
                lead = Math.Max(lead, delay);
            }
            return lead;
        }

        private Group DirectCappedGroup(Flight s, double impact, out double x, out double up, long retainedMask = 0)
        {
            Group best = null;
            x = up = 0;
            long active = s.Running | s.Starting;
            int bestRank = int.MaxValue;
            if (retainedMask != 0) PrepareNeighbors(retainedMask);
            foreach (Group group in TryFirst(retainedMask == 0 ? null : moreEngines))
            {
                if (retainedMask != 0 && (group.Mask == retainedMask || !Subset(retainedMask, group.Mask))) continue;
                if (!Eligible(group, s)) continue;
                double lead = EmergencyThrustLead(group, s);
                if (lead >= impact) continue;
                Flight atThrust = BallisticCoast(s, lead);
                double side, vertical;
                if (!MainVector(atThrust, 0, cfg.TouchSpeed, out side, out vertical, fadeLift: true,
                    useRetrograde: false)) continue;
                double accel = Norm(side, vertical);
                double seconds = TravelTime(atThrust, 0, cfg.TouchSpeed);
                double remainingMass = MassAfter(s.Mass, accel, seconds, group);
                if (accel * s.Mass > Limit(group, s, true) ||
                    accel * remainingMass < group.Min || !FitsPropellant(s, group, s.Mass - remainingMass)) continue;
                if (lead == 0 && !TerminalVerticalAuthority(s, group, vertical + s.ExternalUpAcceleration))
                    continue;
                int rank = EmergencyGroupRank(group.Mask, s.Running, active);
                if (best != null && (rank > bestRank || (rank == bestRank && CompareGroups(group, best) >= 0))) continue;
                best = group;
                bestRank = rank;
                x = side;
                up = vertical;
                if (retainedMask != 0 && moreEngines.Contains(group.Mask)) return group;
            }
            return best;
        }

        private Command RecoverOrEscalate(Flight s, double fx, double fz)
        {
            if (CappedFinalCommitted)
                return RecoverCappedFinal(s, fx, fz);
            CompleteCatalog();
            double impact = EmergencyImpactSeconds(s);
            bool lowSpeed = !emergency && s.Vz >= -EmergencyMinimumDescentSpeed;
            Group best = null;
            Group keep = null;
            bool anyAvailable = false;
            foreach (Group group in groups)
            {
                if (!Eligible(group, s)) continue;
                anyAvailable = true;
                if (EmergencyThrustLead(group, s) >= impact) continue;
                if (best == null || group.Max > best.Max) best = group;
                if (Subset(group.Mask, s.Running) && group.Min <= Limit(group, s, true) &&
                    (keep == null || group.Count < keep.Count ||
                    (group.Count == keep.Count && group.Max > keep.Max))) keep = group;
            }
            if (lowSpeed || best == null)
            {
                yoloInhibit = lowSpeed ? "low_descent_speed" : anyAvailable ? "thrust_after_impact" : "no_usable_thrust";
                yoloTrigger = null;
                emergency = verticalOnly = false;
                emergencyGroup = 0;
                awaitingEmergencyStarts = false;
                if (keep != null)
                    return BeginFinal(s, keep, fx, Math.Max(0.01, fz), "keep existing capped thrust while monitoring contact");
                if (phase != Phase.Coast && phase != Phase.Recoast)
                    return BeginRecoast(s, "coast, no useful emergency ignition before contact");
                return Output(s, 0, 0, 1, "coast, no useful emergency ignition before contact");
            }
            // Recoast has already searched for a capped direct relight with
            // its measured attitude deadline. Preserve that gate on fallback.
            if (!emergency && phase != Phase.Recoast)
            {
                double x, up;
                Group capped = DirectCappedGroup(s, impact, out x, out up);
                if (capped != null)
                {
                    yoloInhibit = "capped_brake_available";
                    yoloTrigger = null;
                    plan = new Plan { RelightX = x, RelightUp = up };
                    return BeginFinal(s, capped, x, up, "use direct capped braking after losing the planned handoff");
                }
            }
            phase = Phase.Yolo;
            emergency = true;
            return Emergency(s, best);
        }

        private Command Emergency(Flight s, Group strongest)
        {
            verticalOnly = false;
            long established = s.EngineThrustMeasured ? s.EstablishedThrust : s.Running;
            if (awaitingEmergencyStarts && !Subset(emergencyGroup, established))
            {
                emergencyGroup = strongest.Mask;
                if (!Subset(emergencyGroup, established))
                    return EstablishEmergencyThrust(s, strongest);
            }
            awaitingEmergencyStarts = false;
            if (established == 0) return EstablishEmergencyThrust(s, strongest);

            Group active = LiveGroup(s.Running);
            Command recovery;
            if (TryResumeCappedLanding(s, established, active, out recovery)) return recovery;

            double x, up;
            Group bridge = FindEmergencyTransition(s, established, active, out x, out up);
            if (bridge != null)
            {
                yoloState = "uncapped_landing";
                return Output(s, bridge.Mask, x, up, "YOLO: brake toward a capped terminal handoff");
            }
            Group tail = FindEstablishedTerminal(s, established, active, false, out x, out up);
            if (tail != null)
            {
                yoloState = "uncapped_landing";
                yoloTerminalGuidance = true;
                return Output(s, tail.Mask, x, up, "YOLO: track touchdown speed without the cap");
            }
            Group controlled = FindEmergencyLandingGroup(s, established, active, out x, out up);
            if (controlled != null)
            {
                yoloState = "uncapped_landing";
                return Output(s, controlled.Mask, x, up, "YOLO: continue the feasible landing without the cap");
            }

            if (s.Vz < -EmergencyMinimumDescentSpeed && !Subset(strongest.Mask, established) &&
                CoastMargin(s, EmergencyThrustLead(strongest, s),
                strongest.Max / s.Mass, 0, cfg.TouchSpeed) >= 0)
                return EstablishEmergencyThrust(s, strongest);

            return EmergencyVerticalBrake(s, "vertical_brake", "YOLO: maximum available thrust toward vertical");
        }

        private Command EstablishEmergencyThrust(Flight s, Group group)
        {
            emergencyGroup = group.Mask;
            awaitingEmergencyStarts = true;
            yoloState = "establishing";
            double top = group.Max / s.Mass;
            if (CoastMargin(s, EmergencyThrustLead(group, s), top, 0, cfg.TouchSpeed) < 0)
                return EmergencyVerticalBrake(s, "establishing", "YOLO: establish maximum vertical thrust");
            double length = Norm(s.Vx, Math.Max(1, -s.Vz));
            return Output(s, group.Mask, -top * s.Vx / length, top * Math.Max(1, -s.Vz) / length,
                "YOLO: establish all available thrust");
        }

        private Command EmergencyVerticalBrake(Flight s, string state, string reason)
        {
            double impact = EmergencyImpactSeconds(s);
            Flight uprightStart = s;
            double tolerance = Settings.IgnitionToleranceDegrees * Math.PI / 180;
            uprightStart.AlignmentSeconds = s.UprightAngle > tolerance
                ? s.TerminalAlignmentSeconds * (1 - tolerance / s.UprightAngle) : 0;
            Group best = null;
            foreach (Group group in groups)
                if (Eligible(group, s) && (best == null || group.Max > best.Max) &&
                    (s.Vz < -EmergencyMinimumDescentSpeed || Subset(group.Mask, s.Running | s.Starting)) &&
                    EmergencyThrustLead(group, uprightStart) < impact) best = group;
            if (awaitingEmergencyStarts) emergencyGroup = best == null ? 0 : best.Mask;
            verticalOnly = true;
            yoloState = state;
            return Output(s, best == null ? 0 : best.Mask, 0, best == null ? 1 : best.Max / s.Mass, reason);
        }

        private bool TryResumeCappedLanding(Flight s, long established, Group active, out Command command)
        {
            command = new Command();
            double terminalX, terminalUp;
            Group terminal = FindEstablishedTerminal(s, established, active, true, out terminalX, out terminalUp);
            if (terminal != null)
            {
                EndEmergency();
                plan = new Plan();
                command = BeginFinal(s, terminal, terminalX, terminalUp, "YOLO recovery: resume capped Final");
                return true;
            }

            Plan best = new Plan();
            double bestX = 0, bestUp = 0;
            foreach (Group candidate in groups)
            {
                if (!Subset(candidate.Mask, established) || !Eligible(candidate, s) ||
                    candidate.Min > Limit(candidate, s, true) ||
                    CoastMargin(s, 0, Limit(candidate, s, true) / s.Mass, 0, cfg.TouchSpeed) < 0) continue;
                Flight reduced = active != null ? ThrustAfterDownselect(s, active, candidate) : s;
                Plan path = FindPlan(reduced, candidate.Mask);
                double x, up;
                if (path.Main == null || !CanTrackMain(reduced, candidate, path, out x, out up) ||
                    !MainTurnFits(reduced, path, x, up) ||
                    (active != null && candidate.Mask != active.Mask && !TerminalTurnReady(reduced)) ||
                    (best.Main != null && ComparePlanGroups(path, best) >= 0)) continue;
                best = path;
                bestX = x;
                bestUp = up;
            }
            string reason = "YOLO recovery: resume capped Main";
            long mask = best.Main == null ? 0 : best.Main.Mask;
            if (mask == 0)
            {
                // The current capped group can hold through a measured turn
                // while the normal main-burn solver keeps looking for a handoff.
                if (active == null || !Subset(active.Mask, established) || !Eligible(active, s) ||
                    TravelTime(s, 0, cfg.TouchSpeed) <= cfg.AttitudeResponseSeconds ||
                    active.Min > Limit(active, s, true) || !TryContinueCappedMain(s, active, out bestX, out bestUp))
                    return false;
                mask = active.Mask;
                reason = "YOLO recovery: resume capped braking through the turn";
            }
            EndEmergency();
            plan = best;
            selected = mask;
            phase = Phase.Main;
            command = Output(s, selected, bestX, bestUp, reason);
            return true;
        }

        private void EndEmergency()
        {
            emergency = verticalOnly = awaitingEmergencyStarts = false;
            emergencyGroup = 0;
            yoloTrigger = null;
            yoloState = "capped_recovery";
        }

        private Group FindEstablishedTerminal(Flight s, long established, Group active, bool capped,
                                              out double side, out double vertical)
        {
            Group best = null;
            side = vertical = 0;
            foreach (Group group in groups)
            {
                if (!Subset(group.Mask, established) || !Eligible(group, s) ||
                    group.Min > Limit(group, s, capped) || (best != null && CompareGroups(group, best) >= 0)) continue;
                Flight reduced = active != null ? ThrustAfterDownselect(s, active, group) : s;
                double x, up;
                if (!FinalVector(reduced, out x, out up) || !TryTerminalThrust(reduced, group, ref x, up, capped) ||
                    (group.Mask != s.Running && (!TerminalVerticalAuthority(reduced, group, up, capped) ||
                        !TerminalTurnReady(reduced))) ||
                    !TerminalTurnFits(reduced, group, capped, allowRelight: false)) continue;
                best = group;
                side = x;
                vertical = up;
            }
            return best;
        }

        private Group FindEmergencyTransition(Flight s, long established, Group active, out double x, out double up)
        {
            x = up = 0;
            if (cfg.MaxG * Settings.G0 <= cfg.Gravity) return null;
            Group bestMain = null;
            Group bestFinal = null;
            double terminalBudget = Math.Min(cfg.PrecisionSeconds, TravelTime(s, 0, cfg.TouchSpeed));
            foreach (Group candidate in groups)
            {
                if (candidate.Count < 2 || !Subset(candidate.Mask, established) || !Eligible(candidate, s) ||
                    CoastMargin(s, 0, candidate.Max / s.Mass, 0, cfg.TouchSpeed) < 0) continue;

                Flight reduced = active != null ? ThrustAfterDownselect(s, active, candidate) : s;
                foreach (double factor in TerminalDurationFactors)
                {
                    Plan path = FineMainPlan(reduced, candidate, terminalBudget * factor, true, cappedMain: false);
                    if (path.Main == null || path.Final.Mask == candidate.Mask || (bestFinal != null &&
                        (CompareGroups(path.Final, bestFinal) > 0 ||
                         (CompareGroups(path.Final, bestFinal) == 0 && CompareGroups(candidate, bestMain) >= 0)))) continue;
                    double side, vertical;
                    if (!CanTrackMain(reduced, candidate, path, out side, out vertical, cappedMain: false) ||
                        (candidate.Mask != s.Running && !TerminalVerticalAuthority(reduced, candidate,
                            vertical + reduced.ExternalUpAcceleration, capped: false))) continue;
                    bestMain = candidate;
                    bestFinal = path.Final;
                    x = side;
                    up = vertical;
                    if (bestFinal.Count == 1) break;
                }
            }
            return bestMain;
        }

        private Group FindEmergencyLandingGroup(Flight s, long established, Group active, out double x, out double up)
        {
            Group best = null;
            x = up = 0;
            foreach (Group group in groups)
            {
                if (!Subset(group.Mask, established) || !Eligible(group, s) ||
                    (best != null && CompareGroups(group, best) >= 0)) continue;
                Flight reduced = active != null ? ThrustAfterDownselect(s, active, group) : s;
                double side, vertical;
                if (!MainVector(reduced, 0, cfg.TouchSpeed, out side, out vertical, fadeLift: true,
                    useRetrograde: false)) continue;
                double acceleration = Norm(side, vertical);
                double seconds = TravelTime(reduced, 0, cfg.TouchSpeed);
                var direct = new Plan { JoinSpeed = cfg.TouchSpeed };
                double remainingMass = MassAfter(reduced.Mass, acceleration, seconds, group);
                if (acceleration * reduced.Mass > group.Max ||
                    acceleration * remainingMass < group.Min ||
                    !FitsPropellant(reduced, group, reduced.Mass - remainingMass) ||
                    (group.Mask != s.Running && !TerminalVerticalAuthority(reduced, group,
                        vertical + reduced.ExternalUpAcceleration, capped: false)) ||
                    !MainTurnFits(reduced, direct, side, vertical)) continue;
                if (reduced.AttitudeRateMeasured && cfg.AttitudeResponseSeconds > 0)
                {
                    double delay = Math.Min(cfg.AttitudeResponseSeconds, seconds / 4);
                    Flight next = PredictMainResponse(reduced, side, vertical, delay,
                        Norm(reduced.LateralThrustAcceleration, reduced.VerticalThrustAcceleration));
                    if (next.Height <= 0 || next.Vz >= 0 ||
                        CoastMargin(next, 0, group.Max / next.Mass, 0, cfg.TouchSpeed) < 0) continue;
                }
                best = group;
                x = side;
                up = vertical;
            }
            return best;
        }

        private static int EmergencyGroupRank(long candidate, long running, long active)
        {
            if (Subset(candidate, running)) return 0;
            if (candidate == active) return 1;
            if (Subset(running, candidate) && Subset(candidate, active)) return 2;
            if (Subset(active, candidate)) return 3;
            return 4;
        }
    }
}
