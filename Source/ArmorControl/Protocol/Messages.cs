using System;

namespace ArmorOverhaul.ArmorControl.Protocol
{
    internal sealed class TelemetrySnapshot
    {
        internal static readonly TelemetrySnapshot Empty = new TelemetrySnapshot(
            0, 0, 0, "UNKNOWN", null, null, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

        internal TelemetrySnapshot(
            long sequence,
            double universalTime,
            double missionTime,
            string scene,
            string vesselId,
            string vesselName,
            double altitude,
            double radarAltitude,
            double surfaceSpeed,
            double verticalSpeed,
            double heading,
            double pitch,
            double roll,
            double throttle,
            double geeForce,
            double dynamicPressureKpa)
        {
            Sequence = sequence;
            UniversalTime = universalTime;
            MissionTime = missionTime;
            Scene = scene;
            VesselId = vesselId;
            VesselName = vesselName;
            Altitude = altitude;
            RadarAltitude = radarAltitude;
            SurfaceSpeed = surfaceSpeed;
            VerticalSpeed = verticalSpeed;
            Heading = heading;
            Pitch = pitch;
            Roll = roll;
            Throttle = throttle;
            GeeForce = geeForce;
            DynamicPressureKpa = dynamicPressureKpa;
        }

        internal long Sequence { get; private set; }
        internal double UniversalTime { get; private set; }
        internal double MissionTime { get; private set; }
        internal string Scene { get; private set; }
        internal string VesselId { get; private set; }
        internal string VesselName { get; private set; }
        internal double Altitude { get; private set; }
        internal double RadarAltitude { get; private set; }
        internal double SurfaceSpeed { get; private set; }
        internal double VerticalSpeed { get; private set; }
        internal double Heading { get; private set; }
        internal double Pitch { get; private set; }
        internal double Roll { get; private set; }
        internal double Throttle { get; private set; }
        internal double GeeForce { get; private set; }
        internal double DynamicPressureKpa { get; private set; }
    }

    internal sealed class IncomingMessage
    {
        internal string Type;
        internal string CommandId;
        internal string Name;
        internal long Sequence;
        internal string RawJson;
    }

    internal sealed class VesselImageSnapshot
    {
        internal static readonly VesselImageSnapshot Empty = new VesselImageSnapshot(0, null, null);

        internal VesselImageSnapshot(long revision, string vesselId, byte[] jpeg)
        {
            Revision = revision;
            VesselId = vesselId;
            Jpeg = jpeg ?? new byte[0];
        }

        internal long Revision { get; private set; }
        internal string VesselId { get; private set; }
        internal byte[] Jpeg { get; private set; }
    }

    internal sealed class RegularTelemetrySnapshot
    {
        internal static readonly RegularTelemetrySnapshot Empty = new RegularTelemetrySnapshot(
            0, 0, null, null, null,
            0, 0, 0, 0, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 0,
            0, 0, 0, null);

        internal RegularTelemetrySnapshot(
            long sequence,
            double universalTime,
            string vesselId,
            string body,
            string situation,
            double apoapsis,
            double periapsis,
            double eccentricity,
            double inclination,
            double period,
            double timeToApoapsis,
            double timeToPeriapsis,
            double semiMajorAxis,
            double latitude,
            double longitude,
            double horizontalSurfaceSpeed,
            double mach,
            double staticPressureKpa,
            double mass,
            int crewCount,
            int partCount,
            int currentStage,
            string targetName)
        {
            Sequence = sequence;
            UniversalTime = universalTime;
            VesselId = vesselId;
            Body = body;
            Situation = situation;
            Apoapsis = apoapsis;
            Periapsis = periapsis;
            Eccentricity = eccentricity;
            Inclination = inclination;
            Period = period;
            TimeToApoapsis = timeToApoapsis;
            TimeToPeriapsis = timeToPeriapsis;
            SemiMajorAxis = semiMajorAxis;
            Latitude = latitude;
            Longitude = longitude;
            HorizontalSurfaceSpeed = horizontalSurfaceSpeed;
            Mach = mach;
            StaticPressureKpa = staticPressureKpa;
            Mass = mass;
            CrewCount = crewCount;
            PartCount = partCount;
            CurrentStage = currentStage;
            TargetName = targetName;
        }

        internal long Sequence { get; private set; }
        internal double UniversalTime { get; private set; }
        internal string VesselId { get; private set; }
        internal string Body { get; private set; }
        internal string Situation { get; private set; }
        internal double Apoapsis { get; private set; }
        internal double Periapsis { get; private set; }
        internal double Eccentricity { get; private set; }
        internal double Inclination { get; private set; }
        internal double Period { get; private set; }
        internal double TimeToApoapsis { get; private set; }
        internal double TimeToPeriapsis { get; private set; }
        internal double SemiMajorAxis { get; private set; }
        internal double Latitude { get; private set; }
        internal double Longitude { get; private set; }
        internal double HorizontalSurfaceSpeed { get; private set; }
        internal double Mach { get; private set; }
        internal double StaticPressureKpa { get; private set; }
        internal double Mass { get; private set; }
        internal int CrewCount { get; private set; }
        internal int PartCount { get; private set; }
        internal int CurrentStage { get; private set; }
        internal string TargetName { get; private set; }
    }

    internal sealed class CommandResult
    {
        internal string CommandId;
        internal long ServerSequence;
        internal bool Success;
        internal string Code;
        internal string Message;
    }

    internal sealed class CommandExecution
    {
        internal bool Success;
        internal string Code;
        internal string Message;
    }

    internal sealed class RuntimeMetrics
    {
        internal static readonly RuntimeMetrics Empty = new RuntimeMetrics(0, 0, 0, 0, 0, 0);

        internal RuntimeMetrics(long fastSamples, double fastAverageMs, double fastMaximumMs,
            long regularSamples, double regularAverageMs, double regularMaximumMs)
        {
            FastSamples = fastSamples;
            FastAverageMs = fastAverageMs;
            FastMaximumMs = fastMaximumMs;
            RegularSamples = regularSamples;
            RegularAverageMs = regularAverageMs;
            RegularMaximumMs = regularMaximumMs;
        }

        internal long FastSamples { get; private set; }
        internal double FastAverageMs { get; private set; }
        internal double FastMaximumMs { get; private set; }
        internal long RegularSamples { get; private set; }
        internal double RegularAverageMs { get; private set; }
        internal double RegularMaximumMs { get; private set; }
    }

    internal sealed class VesselStructureSnapshot
    {
        internal static readonly VesselStructureSnapshot Empty = new VesselStructureSnapshot(0, null, null, 0, new CrewMemberSnapshot[0], new PartSnapshot[0], AerodynamicCenterSnapshot.Unavailable("No active vessel."));

        internal VesselStructureSnapshot(long revision, string vesselId, string vesselName, int crewCapacity, CrewMemberSnapshot[] crew, PartSnapshot[] parts)
            : this(revision, vesselId, vesselName, crewCapacity, crew, parts,
                AerodynamicCenterSnapshot.Unavailable("Aerodynamic center was not sampled."))
        {
        }

        internal VesselStructureSnapshot(long revision, string vesselId, string vesselName, int crewCapacity, CrewMemberSnapshot[] crew, PartSnapshot[] parts, AerodynamicCenterSnapshot aerodynamicCenter)
        {
            Revision = revision;
            VesselId = vesselId;
            VesselName = vesselName;
            CrewCapacity = crewCapacity;
            Crew = crew ?? new CrewMemberSnapshot[0];
            Parts = parts ?? new PartSnapshot[0];
            AerodynamicCenter = aerodynamicCenter ?? AerodynamicCenterSnapshot.Unavailable("Aerodynamic center was not sampled.");
        }

        internal long Revision { get; private set; }
        internal string VesselId { get; private set; }
        internal string VesselName { get; private set; }
        internal int CrewCapacity { get; private set; }
        internal CrewMemberSnapshot[] Crew { get; private set; }
        internal PartSnapshot[] Parts { get; private set; }
        internal AerodynamicCenterSnapshot AerodynamicCenter { get; private set; }
    }

    internal sealed class AerodynamicCenterSnapshot
    {
        internal static AerodynamicCenterSnapshot Unavailable(string status)
        {
            return new AerodynamicCenterSnapshot(false, false, "FAR", status, 0, 0, 0, 0, 0, 0);
        }

        internal AerodynamicCenterSnapshot(bool available, bool projected, string source, string status,
            double normalizedX, double normalizedY, double distanceFromCom, double forwardOffset,
            double rightOffset, double upOffset)
        {
            Available = available; Projected = projected; Source = source; Status = status;
            NormalizedX = normalizedX; NormalizedY = normalizedY; DistanceFromCom = distanceFromCom;
            ForwardOffset = forwardOffset; RightOffset = rightOffset; UpOffset = upOffset;
        }

        internal bool Available { get; private set; }
        internal bool Projected { get; private set; }
        internal string Source { get; private set; }
        internal string Status { get; private set; }
        internal double NormalizedX { get; private set; }
        internal double NormalizedY { get; private set; }
        internal double DistanceFromCom { get; private set; }
        internal double ForwardOffset { get; private set; }
        internal double RightOffset { get; private set; }
        internal double UpOffset { get; private set; }
    }

    internal sealed class CrewMemberSnapshot
    {
        internal CrewMemberSnapshot(string name, string profession, int level, string location, uint partId)
            : this(name, profession, level, location, partId, false, "EVA availability was not sampled.")
        {
        }

        internal CrewMemberSnapshot(string name, string profession, int level, string location, uint partId,
            bool evaAvailable, string evaUnavailableReason)
        {
            Name = name; Profession = profession; Level = level; Location = location; PartId = partId;
            EvaAvailable = evaAvailable; EvaUnavailableReason = evaUnavailableReason;
        }
        internal string Name { get; private set; }
        internal string Profession { get; private set; }
        internal int Level { get; private set; }
        internal string Location { get; private set; }
        internal uint PartId { get; private set; }
        internal bool EvaAvailable { get; private set; }
        internal string EvaUnavailableReason { get; private set; }
    }

    internal sealed class EngineSnapshot
    {
        internal EngineSnapshot(int moduleIndex, string engineId, string name, bool ignited, bool canActivate,
            bool canShutdown, double thrustLimit, int gimbalModuleIndex, bool gimbalEnabled)
        {
            ModuleIndex = moduleIndex; EngineId = engineId; Name = name; Ignited = ignited;
            CanActivate = canActivate; CanShutdown = canShutdown; ThrustLimit = thrustLimit;
            GimbalModuleIndex = gimbalModuleIndex; GimbalEnabled = gimbalEnabled;
        }
        internal int ModuleIndex { get; private set; }
        internal string EngineId { get; private set; }
        internal string Name { get; private set; }
        internal bool Ignited { get; private set; }
        internal bool CanActivate { get; private set; }
        internal bool CanShutdown { get; private set; }
        internal double ThrustLimit { get; private set; }
        internal int GimbalModuleIndex { get; private set; }
        internal bool GimbalEnabled { get; private set; }
    }

    internal sealed class PartToggleSnapshot
    {
        internal PartToggleSnapshot(string kind, int moduleIndex, string name, bool enabled, bool transitioning)
        {
            Kind = kind; ModuleIndex = moduleIndex; Name = name; Enabled = enabled; Transitioning = transitioning;
        }
        internal string Kind { get; private set; }
        internal int ModuleIndex { get; private set; }
        internal string Name { get; private set; }
        internal bool Enabled { get; private set; }
        internal bool Transitioning { get; private set; }
    }

    internal sealed class PartActionSnapshot
    {
        internal PartActionSnapshot(string kind, int moduleIndex, string name, string status, bool active,
            string command, string commandLabel, bool available, string[] optionValues,
            string[] optionLabels, string selectedOption)
        {
            Kind = kind; ModuleIndex = moduleIndex; Name = name; Status = status; Active = active;
            Command = command; CommandLabel = commandLabel; Available = available;
            OptionValues = optionValues ?? new string[0]; OptionLabels = optionLabels ?? new string[0];
            SelectedOption = selectedOption;
        }

        internal string Kind { get; private set; }
        internal int ModuleIndex { get; private set; }
        internal string Name { get; private set; }
        internal string Status { get; private set; }
        internal bool Active { get; private set; }
        internal string Command { get; private set; }
        internal string CommandLabel { get; private set; }
        internal bool Available { get; private set; }
        internal string[] OptionValues { get; private set; }
        internal string[] OptionLabels { get; private set; }
        internal string SelectedOption { get; private set; }
    }

    internal sealed class PartSnapshot
    {
        internal PartSnapshot(uint id, uint? parentId, string name, string title, int stage, double mass,
            double temperature, double maximumTemperature, double skinTemperature, double maximumSkinTemperature,
            ResourceSnapshot[] resources, string[] modules)
            : this(id, parentId, name, title, stage, mass, temperature, maximumTemperature, skinTemperature,
                maximumSkinTemperature, resources, modules, null, null)
        {
        }

        internal PartSnapshot(uint id, uint? parentId, string name, string title, int stage, double mass,
            double temperature, double maximumTemperature, double skinTemperature, double maximumSkinTemperature,
            ResourceSnapshot[] resources, string[] modules, EngineSnapshot[] engines, PartToggleSnapshot[] toggles)
            : this(id, parentId, name, title, stage, mass, temperature, maximumTemperature, skinTemperature,
                maximumSkinTemperature, resources, modules, engines, toggles, null)
        {
        }

        internal PartSnapshot(uint id, uint? parentId, string name, string title, int stage, double mass,
            double temperature, double maximumTemperature, double skinTemperature, double maximumSkinTemperature,
            ResourceSnapshot[] resources, string[] modules, EngineSnapshot[] engines, PartToggleSnapshot[] toggles,
            PartActionSnapshot[] actions)
        {
            Id = id; ParentId = parentId; Name = name; Title = title; Stage = stage; Mass = mass;
            Temperature = temperature; MaximumTemperature = maximumTemperature;
            SkinTemperature = skinTemperature; MaximumSkinTemperature = maximumSkinTemperature;
            Resources = resources ?? new ResourceSnapshot[0]; Modules = modules ?? new string[0];
            Engines = engines ?? new EngineSnapshot[0]; Toggles = toggles ?? new PartToggleSnapshot[0];
            Actions = actions ?? new PartActionSnapshot[0];
        }
        internal uint Id { get; private set; }
        internal uint? ParentId { get; private set; }
        internal string Name { get; private set; }
        internal string Title { get; private set; }
        internal int Stage { get; private set; }
        internal double Mass { get; private set; }
        internal double Temperature { get; private set; }
        internal double MaximumTemperature { get; private set; }
        internal double SkinTemperature { get; private set; }
        internal double MaximumSkinTemperature { get; private set; }
        internal ResourceSnapshot[] Resources { get; private set; }
        internal string[] Modules { get; private set; }
        internal EngineSnapshot[] Engines { get; private set; }
        internal PartToggleSnapshot[] Toggles { get; private set; }
        internal PartActionSnapshot[] Actions { get; private set; }
    }

    internal sealed class ResourceSnapshot
    {
        internal ResourceSnapshot(string name, double amount, double maximum)
        {
            Name = name; Amount = amount; Maximum = maximum;
        }
        internal string Name { get; private set; }
        internal double Amount { get; private set; }
        internal double Maximum { get; private set; }
    }

    internal sealed class FlightRecorderSample
    {
        internal long Revision { get; set; }
        internal FlightRecorderSample(long sequence, double timeSinceMark, int currentStage, double altitudeAsl,
            double downRange, double speedSurface, double speedOrbital, double mass, double acceleration,
            double dynamicPressure, double angleOfAttack, double angleOfSideslip, double angleOfDisplacement,
            double altitudeTrue, double pitch, double gravityLosses, double dragLosses,
            double steeringLosses, double deltaVExpended)
        {
            Sequence = sequence; TimeSinceMark = timeSinceMark; CurrentStage = currentStage; AltitudeAsl = altitudeAsl;
            DownRange = downRange; SpeedSurface = speedSurface; SpeedOrbital = speedOrbital; Mass = mass;
            Acceleration = acceleration; DynamicPressure = dynamicPressure; AngleOfAttack = angleOfAttack;
            AngleOfSideslip = angleOfSideslip; AngleOfDisplacement = angleOfDisplacement; AltitudeTrue = altitudeTrue;
            Pitch = pitch; GravityLosses = gravityLosses; DragLosses = dragLosses;
            SteeringLosses = steeringLosses; DeltaVExpended = deltaVExpended;
        }
        internal long Sequence { get; private set; }
        internal double TimeSinceMark { get; private set; }
        internal int CurrentStage { get; private set; }
        internal double AltitudeAsl { get; private set; }
        internal double DownRange { get; private set; }
        internal double SpeedSurface { get; private set; }
        internal double SpeedOrbital { get; private set; }
        internal double Mass { get; private set; }
        internal double Acceleration { get; private set; }
        internal double DynamicPressure { get; private set; }
        internal double AngleOfAttack { get; private set; }
        internal double AngleOfSideslip { get; private set; }
        internal double AngleOfDisplacement { get; private set; }
        internal double AltitudeTrue { get; private set; }
        internal double Pitch { get; private set; }
        internal double GravityLosses { get; private set; }
        internal double DragLosses { get; private set; }
        internal double SteeringLosses { get; private set; }
        internal double DeltaVExpended { get; private set; }
    }

    internal sealed class FlightRecorderHistory
    {
        internal FlightRecorderHistory(long revision, FlightRecorderSample[] samples)
        {
            Revision = revision; Samples = samples ?? new FlightRecorderSample[0];
        }
        internal long Revision { get; private set; }
        internal FlightRecorderSample[] Samples { get; private set; }
    }

    internal sealed class FlightPanelSnapshot
    {
        internal long Sequence;
        internal string VesselId;
        internal string CurrentOrbit;
        internal double OrbitInclination;
        internal double OrbitPeriod;
        internal double OrbitalSpeed;
        internal double TimeToApoapsis;
        internal double TimeToPeriapsis;
        internal string TimeToSoiTransition;
        internal double OrbitEccentricity;
        internal double AltitudeBottom;
        internal string Coordinates;
        internal double Heading;
        internal double SurfaceGravity;
        internal double SurfaceSpeed;
        internal double VerticalSpeed;
        internal double HorizontalSurfaceSpeed;
        internal double MaximumAcceleration;
        internal double CurrentAcceleration;
        internal double MaximumThrust;
        internal double CurrentThrust;
        internal double SurfaceTwr;
        internal double LocalTwr;
        internal double ThrottleTwr;
        internal double GeeForce;
        internal double AtmosphericDrag;
        internal int CrewCapacity;
        internal int CrewCount;
        internal double DryMass;
        internal double VesselMass;
        internal int PartCount;
        internal double StageDeltaVAtmosphere;
        internal double StageDeltaVVacuum;
        internal double TotalDeltaVAtmosphere;
        internal double TotalDeltaVVacuum;
        internal double StageTimeCurrentThrottle;
        internal double StageTimeFullThrottle;
        internal double StageTimeHover;
        internal double TerminalVelocity;
        internal double VesselCost;
        internal double AngleOfAttack;
        internal double AngleOfSideslip;
        internal double StaticPressureKpa;
        internal string CurrentBiome;
        internal double DynamicPressureKpa;
        internal string SuicideBurnCountdown;
        internal string TimeToImpact;
        internal string TargetClosestApproachDistance;
        internal string TargetDistance;
    }

    internal sealed class AutomationSnapshot
    {
        internal string OrbitGeometryJson;
        internal long Sequence;
        internal string VesselId;
        internal bool MechJebAvailable;
        internal int ManeuverNodeCount;
        internal double NextNodeUt;
        internal double NextNodeDeltaV;
        internal ManeuverNodeSnapshot[] ManeuverNodes;
        internal bool NodeExecutorEnabled;
        internal bool SmartAssEnabled;
        internal string SmartAssMode;
        internal string SmartAssTarget;
        internal bool SmartAssAutoDisable;
        internal double SmartAssSurfacePitch, SmartAssSurfaceYaw, SmartAssSurfaceRoll;
        internal double SmartAssVelocityPitch, SmartAssVelocityYaw, SmartAssVelocityRoll;
        internal double SmartAssRoll;
        internal bool LandingEnabled;
        internal bool LandingAtTarget;
        internal bool LandingPredictionReady;
        internal string LandingStatus;
        internal string LandingStep;
        internal string LandingDescentMode;
        internal bool LandingUsesAtmosphere;
        internal double LandingTouchdownSpeed;
        internal bool LandingDeployGears;
        internal int LandingGearStageLimit;
        internal bool LandingDeployChutes;
        internal int LandingChuteStageLimit;
        internal bool LandingRcsAdjustment;
        internal bool LandingPredictionsEnabled;
        internal bool LandingPredictionSimulationRunning;
        internal string LandingPredictionOutcome;
        internal double LandingPredictedLatitude;
        internal double LandingPredictedLongitude;
        internal double LandingPredictedAltitude;
        internal double LandingPredictionTargetDistance;
        internal double LandingPredictionMaxDrag;
        internal double LandingPredictionDeltaV;
        internal double LandingPredictionTimeToLand;
        internal bool LandingPredictionAerobrake;
        internal double LandingAerobrakePeriapsis;
        internal double LandingAerobrakeApoapsis;
        internal double LandingAerobrakeEccentricity;
        internal bool LandingMakeAerobrakeNodes;
        internal bool LandingShowTrajectory;
        internal bool LandingWorldTrajectory;
        internal bool LandingCameraTrajectory;
        internal bool DockingEnabled;
        internal string DockingStatus;
        internal string DockingStep;
        internal double DockingAxialSeparation;
        internal double DockingLateralSeparation;
        internal bool DockingAxisAvailable;
        internal double DockingAxisErrorX;
        internal double DockingAxisErrorY;
        internal double DockingSpeedLimit;
        internal bool DockingOverrideSafeDistance;
        internal double DockingSafeDistance;
        internal bool DockingOverrideStartDistance;
        internal double DockingStartDistance;
        internal bool DockingForceRoll;
        internal double DockingRoll;
        internal bool DockingDrawBoundingBox;
        internal bool AscentEnabled;
        internal string AscentStatus;
        internal string AscentPath;
        internal double AscentDesiredOrbitAltitude;
        internal double AscentDesiredInclination;
        internal bool AscentTimedLaunch;
        internal double AscentTimeToLaunch;
        internal string AscentLaunchMode;
        internal bool AscentLaunchTargetAvailable;
        internal string AscentLaunchTargetName;
        internal double AscentLaunchPhaseAngle;
        internal double AscentLaunchLanDifference;
        internal bool AscentAutoThrottle;
        internal bool AscentCorrectiveSteering;
        internal bool AscentAutoStage;
        internal bool AscentLimitAoA;
        internal double AscentMaxAoA;
        internal bool AscentLimitQEnabled;
        internal double AscentLimitQ;
        internal bool AscentForceRoll;
        internal double AscentVerticalRoll;
        internal double AscentTurnRoll;
        internal bool AscentSkipCircularization;
        internal double AscentDesiredLan;
        internal double AscentCorrectiveSteeringGain;
        internal bool AscentDeploySolarPanels;
        internal bool AscentDeployAntennas;
        internal double AscentRollAltitude;
        internal double AscentAoAFadeoutPressure;
        internal double AscentGtTurnStartAltitude;
        internal double AscentGtTurnStartVelocity;
        internal double AscentGtTurnStartPitch;
        internal double AscentGtIntermediateAltitude;
        internal double AscentGtHoldApTime;
        internal double AscentClassicTurnStartAltitude;
        internal double AscentClassicTurnStartVelocity;
        internal double AscentClassicTurnEndAltitude;
        internal double AscentClassicTurnEndAngle;
        internal double AscentClassicTurnShapeExponent;
        internal bool AscentClassicAutoPath;
        internal double AscentPvgPitchStartVelocity;
        internal double AscentPvgPitchRate;
        internal double AscentPvgDesiredApoapsis;
        internal double AscentPvgDesiredAttachAltitude;
        internal double AscentPvgDynamicPressureTrigger;
        internal int AscentPvgStagingTrigger;
        internal bool AscentPvgFixedCoast;
        internal double AscentPvgFixedCoastLength;
        internal bool AscentPvgAttachAltitudeEnabled;
        internal bool AscentPvgStagingTriggerEnabled;
        internal bool AutoWarp;
        internal bool RendezvousEnabled;
        internal string RendezvousStatus;
        internal double RendezvousDesiredDistance;
        internal double RendezvousMaxPhasingOrbits;
        internal double RendezvousMaxClosingSpeed;
        internal string TargetName;
        internal string TargetKind;
        internal double TargetDistance;
        internal double TargetRelativeSpeed;
        internal double TargetOrbitApoapsis;
        internal double TargetOrbitPeriapsis;
        internal double TargetOrbitInclination;
        internal double TargetOrbitPeriod;
        internal TargetCatalogEntry[] TargetCatalog;
        internal bool PositionTargetExists;
        internal string TargetBody;
        internal double TargetLatitude;
        internal double TargetLongitude;
        internal long PorkchopRevision;
        internal string PorkchopStatus;
        internal int PorkchopProgress;
        internal bool Sas;
        internal bool Rcs;
        internal bool Gear;
        internal bool Brakes;
        internal bool Lights;
        internal int ActionGroupMask;
        internal string SolarPanels;
        internal string Antennas;
        internal bool AutoStage;

        // MechJeb 2.14.3 thrust-controller flight-envelope settings.
        internal bool EnvelopeAvailable;
        internal bool EnvelopeTerminalVelocity;
        internal bool EnvelopeDynamicPressure;
        internal double EnvelopeMaximumDynamicPressure;
        internal bool EnvelopeAcceleration;
        internal double EnvelopeMaximumAcceleration;
        internal bool EnvelopeMaximumThrottle;
        internal double EnvelopeMaximumThrottleValue;
        internal bool EnvelopeMinimumThrottle;
        internal double EnvelopeMinimumThrottleValue;
        internal bool EnvelopePreventOverheat;
        internal bool EnvelopePreventFlameout;
        internal double EnvelopeFlameoutSafetyPercent;
        internal bool EnvelopePreventUnstableIgnition;
        internal bool EnvelopeAutoRcsUllage;
        internal bool EnvelopeSmoothThrottle;
        internal double EnvelopeThrottleSmoothingTime;
        internal bool EnvelopeManageIntakes;
        internal bool EnvelopeDifferentialThrottle;

        // Local Trajectories 2.4.5.4 state and controls.
        internal TrajectoryPredictionSnapshot Trajectory;
    }

    internal sealed class TrajectoryPredictionSnapshot
    {
        internal string PathsJson = "[]"; // Legacy spatial-path field; surface maps use GroundPathsJson.
        internal string GroundPathsJson;
        internal string PathStatus;
        internal int SurfaceMapVersion;
        internal double BodyRadius;
        internal double ImpactTargetDistance;
        internal bool Available;
        internal string Version;
        internal string Status;
        internal bool DisplayTrajectories;
        internal bool DisplayTrajectoriesInFlight;
        internal bool AlwaysUpdate;
        internal bool DisplayCompleteTrajectory;
        internal bool BodyFixedMode;
        internal bool AutoUpdateAerodynamicModel;
        internal bool UseCache;
        internal bool DefaultDescentIsRetrograde;
        internal double IntegrationStepSize;
        internal int MaximumPatchCount;
        internal int MaximumFramesPerPatch;
        internal string AerodynamicModel;
        internal double ComputationTimeMilliseconds;
        internal int ErrorCount;
        internal double MaximumDecelerationG;
        internal bool ImpactAvailable;
        internal double TimeToImpact;
        internal double ImpactLatitude;
        internal double ImpactLongitude;
        internal double ImpactAltitude;
        internal double ImpactSpeed;
        internal double ImpactVerticalSpeed;
        internal double ImpactHorizontalSpeed;
        internal bool TargetAvailable;
        internal double TargetLatitude;
        internal double TargetLongitude;
        internal double TargetAltitude;
        internal string EntryMode;
        internal bool EntryRetrograde;
        internal double EntryAngle;
        internal string HighAltitudeMode;
        internal bool HighAltitudeRetrograde;
        internal double HighAltitudeAngle;
        internal string LowAltitudeMode;
        internal bool LowAltitudeRetrograde;
        internal double LowAltitudeAngle;
        internal string FinalApproachMode;
        internal bool FinalApproachRetrograde;
        internal double FinalApproachAngle;
    }

    internal sealed class TargetCatalogEntry
    {
        internal TargetCatalogEntry(string id, string parentId, string kind, string name,
            string bodyName, string vesselId, uint partId)
        {
            Id = id;
            ParentId = parentId;
            Kind = kind;
            Name = name;
            BodyName = bodyName;
            VesselId = vesselId;
            PartId = partId;
        }

        internal string Id { get; private set; }
        internal string ParentId { get; private set; }
        internal string Kind { get; private set; }
        internal string Name { get; private set; }
        internal string BodyName { get; private set; }
        internal string VesselId { get; private set; }
        internal uint PartId { get; private set; }
    }

    internal sealed class ManeuverNodeSnapshot
    {
        internal double UniversalTime;
        internal double DeltaV;

        internal ManeuverNodeSnapshot(double universalTime, double deltaV)
        {
            UniversalTime = universalTime;
            DeltaV = deltaV;
        }
    }

    internal sealed class PorkchopResultSnapshot
    {
        internal static readonly PorkchopResultSnapshot Empty = new PorkchopResultSnapshot(0, 0, 0, 0, 0, 0, 0, -1, -1, new double[0]);
        internal PorkchopResultSnapshot(long revision, int width, int height, double minimumDepartureTime,
            double maximumDepartureTime, double minimumTransferTime, double maximumTransferTime,
            int bestDepartureIndex, int bestDurationIndex, double[] costs)
        {
            Revision = revision; Width = width; Height = height; MinimumDepartureTime = minimumDepartureTime;
            MaximumDepartureTime = maximumDepartureTime; MinimumTransferTime = minimumTransferTime;
            MaximumTransferTime = maximumTransferTime; BestDepartureIndex = bestDepartureIndex;
            BestDurationIndex = bestDurationIndex; Costs = costs ?? new double[0];
        }
        internal long Revision { get; private set; }
        internal int Width { get; private set; }
        internal int Height { get; private set; }
        internal double MinimumDepartureTime { get; private set; }
        internal double MaximumDepartureTime { get; private set; }
        internal double MinimumTransferTime { get; private set; }
        internal double MaximumTransferTime { get; private set; }
        internal int BestDepartureIndex { get; private set; }
        internal int BestDurationIndex { get; private set; }
        internal double[] Costs { get; private set; }
    }
}
