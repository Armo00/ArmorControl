using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ArmorOverhaul.ArmorControl.Protocol
{
    internal static class WireProtocol
    {
        internal const int Version = 1;
        private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

        internal static string Hello(string clientId)
        {
            return "{\"type\":\"hello\",\"protocol\":1,\"clientId\":" + Quote(clientId)
                + ",\"capabilities\":[\"telemetry.fast\",\"telemetry.regular\",\"telemetry.flightPanel\",\"telemetry.automation\",\"mechjeb.ascent\",\"mechjeb.ascent.targetLaunch\",\"mechjeb.landing\",\"mechjeb.docking\",\"mechjeb.rendezvous\",\"mechjeb.autowarp\",\"mechjeb.flightEnvelope\",\"trajectories.control\",\"target.catalog\",\"vessel.structure.live\",\"vessel.part.control\",\"vessel.part.actions\",\"vessel.view.selection\",\"vessel.aeroCenter.far\",\"vessel.crew.eva\",\"vessel.image.websocket\",\"recorder.incremental\",\"event.context\",\"event.vessel\",\"command.fifo\",\"command.idempotent\",\"control.deadman\",\"game.quicksave.load\",\"auth.sharedToken\",\"multiClient\"]}";
        }

        internal static string Health(int clients, RuntimeMetrics metrics, double fastSerializationAverageMs, double fastSerializationMaximumMs)
        {
            return "{\"status\":\"ok\",\"product\":\"ArmorControl\",\"protocol\":1,\"clients\":"
                + clients.ToString(Invariant)
                + ",\"metrics\":{\"fastSamples\":" + metrics.FastSamples.ToString(Invariant)
                + ",\"fastCaptureAverageMs\":" + Number(metrics.FastAverageMs)
                + ",\"fastCaptureMaximumMs\":" + Number(metrics.FastMaximumMs)
                + ",\"regularSamples\":" + metrics.RegularSamples.ToString(Invariant)
                + ",\"regularCaptureAverageMs\":" + Number(metrics.RegularAverageMs)
                + ",\"regularCaptureMaximumMs\":" + Number(metrics.RegularMaximumMs)
                + ",\"fastSerializationAverageMs\":" + Number(fastSerializationAverageMs)
                + ",\"fastSerializationMaximumMs\":" + Number(fastSerializationMaximumMs) + "}}";
        }

        internal static string Telemetry(TelemetrySnapshot value)
        {
            var json = new StringBuilder(420);
            json.Append("{\"type\":\"telemetry.fast\",\"protocol\":1");
            Append(json, "sequence", value.Sequence);
            Append(json, "ut", value.UniversalTime);
            Append(json, "met", value.MissionTime);
            Append(json, "scene", value.Scene);
            AppendNullable(json, "vesselId", value.VesselId);
            AppendNullable(json, "vesselName", value.VesselName);
            Append(json, "altitude", value.Altitude);
            Append(json, "radarAltitude", value.RadarAltitude);
            Append(json, "surfaceSpeed", value.SurfaceSpeed);
            Append(json, "verticalSpeed", value.VerticalSpeed);
            Append(json, "heading", value.Heading);
            Append(json, "pitch", value.Pitch);
            Append(json, "roll", value.Roll);
            Append(json, "throttle", value.Throttle);
            Append(json, "geeForce", value.GeeForce);
            Append(json, "dynamicPressureKpa", value.DynamicPressureKpa);
            json.Append('}');
            return json.ToString();
        }

        internal static string Ack(CommandResult result)
        {
            return "{\"type\":\"command.ack\",\"protocol\":1,\"commandId\":" + Quote(result.CommandId)
                + ",\"serverSequence\":" + result.ServerSequence.ToString(Invariant)
                + ",\"success\":" + (result.Success ? "true" : "false")
                + ",\"code\":" + Quote(result.Code) + ",\"message\":" + Quote(result.Message) + "}";
        }

        internal static string RegularTelemetry(RegularTelemetrySnapshot value)
        {
            var json = new StringBuilder(620);
            json.Append("{\"type\":\"telemetry.regular\",\"protocol\":1");
            Append(json, "sequence", value.Sequence);
            Append(json, "ut", value.UniversalTime);
            AppendNullable(json, "vesselId", value.VesselId);
            AppendNullable(json, "body", value.Body);
            AppendNullable(json, "situation", value.Situation);
            Append(json, "apoapsis", value.Apoapsis);
            Append(json, "periapsis", value.Periapsis);
            Append(json, "eccentricity", value.Eccentricity);
            Append(json, "inclination", value.Inclination);
            Append(json, "period", value.Period);
            Append(json, "timeToApoapsis", value.TimeToApoapsis);
            Append(json, "timeToPeriapsis", value.TimeToPeriapsis);
            Append(json, "semiMajorAxis", value.SemiMajorAxis);
            Append(json, "latitude", value.Latitude);
            Append(json, "longitude", value.Longitude);
            Append(json, "horizontalSurfaceSpeed", value.HorizontalSurfaceSpeed);
            Append(json, "mach", value.Mach);
            Append(json, "staticPressureKpa", value.StaticPressureKpa);
            Append(json, "mass", value.Mass);
            Append(json, "crewCount", value.CrewCount);
            Append(json, "partCount", value.PartCount);
            Append(json, "currentStage", value.CurrentStage);
            AppendNullable(json, "targetName", value.TargetName);
            json.Append('}');
            return json.ToString();
        }

        internal static string Pong(long sequence)
        {
            return "{\"type\":\"pong\",\"protocol\":1,\"sequence\":" + sequence.ToString(Invariant) + "}";
        }

        internal static string Error(string code, string message)
        {
            return "{\"type\":\"error\",\"protocol\":1,\"code\":" + Quote(code)
                + ",\"message\":" + Quote(message) + "}";
        }

        internal static string ContextChanged(TelemetrySnapshot value)
        {
            return "{\"type\":\"event.context\",\"protocol\":1,\"sequence\":"
                + value.Sequence.ToString(Invariant) + ",\"scene\":" + Quote(value.Scene)
                + ",\"vesselId\":" + (value.VesselId == null ? "null" : Quote(value.VesselId))
                + ",\"vesselName\":" + (value.VesselName == null ? "null" : Quote(value.VesselName)) + "}";
        }

        internal static string VesselChanged(RegularTelemetrySnapshot previous, RegularTelemetrySnapshot current)
        {
            var changes = new StringBuilder(64);
            AppendChange(changes, previous.CurrentStage != current.CurrentStage, "stage");
            AppendChange(changes, !string.Equals(previous.TargetName, current.TargetName, StringComparison.Ordinal), "target");
            AppendChange(changes, previous.PartCount != current.PartCount, "parts");
            AppendChange(changes, previous.CrewCount != current.CrewCount, "crew");
            if (changes.Length == 0) return null;
            return "{\"type\":\"event.vessel\",\"protocol\":1,\"sequence\":"
                + current.Sequence.ToString(Invariant) + ",\"vesselId\":" + Quote(current.VesselId)
                + ",\"changes\":[" + changes + "]}";
        }

        internal static string Crew(VesselStructureSnapshot value)
        {
            var json = new StringBuilder(256 + value.Crew.Length * 128);
            json.Append("{\"type\":\"vessel.crew\",\"protocol\":1");
            Append(json, "revision", value.Revision);
            AppendNullable(json, "vesselId", value.VesselId);
            AppendNullable(json, "vesselName", value.VesselName);
            Append(json, "capacity", value.CrewCapacity);
            json.Append(",\"crew\":[");
            for (int index = 0; index < value.Crew.Length; index++)
            {
                if (index > 0) json.Append(',');
                CrewMemberSnapshot member = value.Crew[index];
                json.Append("{\"name\":").Append(Quote(member.Name))
                    .Append(",\"profession\":").Append(Quote(member.Profession))
                    .Append(",\"level\":").Append(member.Level.ToString(Invariant))
                    .Append(",\"location\":").Append(Quote(member.Location))
                    .Append(",\"partId\":").Append(member.PartId.ToString(Invariant))
                    .Append(",\"evaAvailable\":").Append(member.EvaAvailable ? "true" : "false")
                    .Append(",\"evaUnavailableReason\":").Append(Quote(member.EvaUnavailableReason)).Append('}');
            }
            return json.Append("]}").ToString();
        }

        internal static string VesselStructure(VesselStructureSnapshot value)
        {
            var json = new StringBuilder(512 + value.Parts.Length * 280);
            json.Append("{\"type\":\"vessel.structure\",\"protocol\":1");
            Append(json, "revision", value.Revision);
            AppendNullable(json, "vesselId", value.VesselId);
            AppendNullable(json, "vesselName", value.VesselName);
            Append(json, "crewCapacity", value.CrewCapacity);
            json.Append(",\"crew\":[");
            for (int crewIndex = 0; crewIndex < value.Crew.Length; crewIndex++)
            {
                if (crewIndex > 0) json.Append(',');
                CrewMemberSnapshot member = value.Crew[crewIndex];
                json.Append("{\"name\":").Append(Quote(member.Name))
                    .Append(",\"profession\":").Append(Quote(member.Profession))
                    .Append(",\"level\":").Append(member.Level.ToString(Invariant))
                    .Append(",\"location\":").Append(Quote(member.Location))
                    .Append(",\"partId\":").Append(member.PartId.ToString(Invariant))
                    .Append(",\"evaAvailable\":").Append(member.EvaAvailable ? "true" : "false")
                    .Append(",\"evaUnavailableReason\":").Append(Quote(member.EvaUnavailableReason)).Append('}');
            }
            json.Append(']');
            AerodynamicCenterSnapshot aero = value.AerodynamicCenter ?? AerodynamicCenterSnapshot.Unavailable("Aerodynamic center unavailable.");
            json.Append(",\"aerodynamicCenter\":{\"available\":").Append(aero.Available ? "true" : "false")
                .Append(",\"projected\":").Append(aero.Projected ? "true" : "false")
                .Append(",\"source\":").Append(Quote(aero.Source))
                .Append(",\"status\":").Append(Quote(aero.Status));
            Append(json, "normalizedX", aero.NormalizedX);
            Append(json, "normalizedY", aero.NormalizedY);
            Append(json, "distanceFromCom", aero.DistanceFromCom);
            Append(json, "forwardOffset", aero.ForwardOffset);
            Append(json, "rightOffset", aero.RightOffset);
            Append(json, "upOffset", aero.UpOffset);
            json.Append('}');
            json.Append(",\"parts\":[");
            for (int index = 0; index < value.Parts.Length; index++)
            {
                if (index > 0) json.Append(',');
                PartSnapshot part = value.Parts[index];
                json.Append("{\"id\":").Append(part.Id.ToString(Invariant))
                    .Append(",\"parentId\":").Append(part.ParentId.HasValue ? part.ParentId.Value.ToString(Invariant) : "null")
                    .Append(",\"name\":").Append(Quote(part.Name))
                    .Append(",\"title\":").Append(Quote(part.Title))
                    .Append(",\"stage\":").Append(part.Stage.ToString(Invariant));
                Append(json, "mass", part.Mass);
                Append(json, "temperature", part.Temperature);
                Append(json, "maximumTemperature", part.MaximumTemperature);
                Append(json, "skinTemperature", part.SkinTemperature);
                Append(json, "maximumSkinTemperature", part.MaximumSkinTemperature);
                json.Append(",\"resources\":[");
                for (int resourceIndex = 0; resourceIndex < part.Resources.Length; resourceIndex++)
                {
                    if (resourceIndex > 0) json.Append(',');
                    ResourceSnapshot resource = part.Resources[resourceIndex];
                    json.Append("{\"name\":").Append(Quote(resource.Name));
                    Append(json, "amount", resource.Amount);
                    Append(json, "maximum", resource.Maximum);
                    json.Append('}');
                }
                json.Append("],\"modules\":[");
                for (int moduleIndex = 0; moduleIndex < part.Modules.Length; moduleIndex++)
                {
                    if (moduleIndex > 0) json.Append(',');
                    json.Append(Quote(part.Modules[moduleIndex]));
                }
                json.Append("],\"engines\":[");
                for (int engineIndex = 0; engineIndex < part.Engines.Length; engineIndex++)
                {
                    if (engineIndex > 0) json.Append(',');
                    EngineSnapshot engine = part.Engines[engineIndex];
                    json.Append("{\"moduleIndex\":").Append(engine.ModuleIndex.ToString(Invariant))
                        .Append(",\"engineId\":").Append(Quote(engine.EngineId))
                        .Append(",\"name\":").Append(Quote(engine.Name))
                        .Append(",\"ignited\":").Append(engine.Ignited ? "true" : "false")
                        .Append(",\"canActivate\":").Append(engine.CanActivate ? "true" : "false")
                        .Append(",\"canShutdown\":").Append(engine.CanShutdown ? "true" : "false");
                    Append(json, "thrustLimit", engine.ThrustLimit);
                    json.Append(",\"gimbalModuleIndex\":").Append(engine.GimbalModuleIndex.ToString(Invariant))
                        .Append(",\"gimbalEnabled\":").Append(engine.GimbalEnabled ? "true" : "false").Append('}');
                }
                json.Append("],\"toggles\":[");
                for (int toggleIndex = 0; toggleIndex < part.Toggles.Length; toggleIndex++)
                {
                    if (toggleIndex > 0) json.Append(',');
                    PartToggleSnapshot toggle = part.Toggles[toggleIndex];
                    json.Append("{\"kind\":").Append(Quote(toggle.Kind))
                        .Append(",\"moduleIndex\":").Append(toggle.ModuleIndex.ToString(Invariant))
                        .Append(",\"name\":").Append(Quote(toggle.Name))
                        .Append(",\"enabled\":").Append(toggle.Enabled ? "true" : "false")
                        .Append(",\"transitioning\":").Append(toggle.Transitioning ? "true" : "false").Append('}');
                }
                json.Append("],\"actions\":[");
                for (int actionIndex = 0; actionIndex < part.Actions.Length; actionIndex++)
                {
                    if (actionIndex > 0) json.Append(',');
                    PartActionSnapshot action = part.Actions[actionIndex];
                    json.Append("{\"kind\":").Append(Quote(action.Kind))
                        .Append(",\"moduleIndex\":").Append(action.ModuleIndex.ToString(Invariant))
                        .Append(",\"name\":").Append(Quote(action.Name))
                        .Append(",\"status\":").Append(Quote(action.Status))
                        .Append(",\"active\":").Append(action.Active ? "true" : "false")
                        .Append(",\"command\":").Append(Quote(action.Command))
                        .Append(",\"commandLabel\":").Append(Quote(action.CommandLabel))
                        .Append(",\"available\":").Append(action.Available ? "true" : "false")
                        .Append(",\"selectedOption\":").Append(Quote(action.SelectedOption))
                        .Append(",\"options\":[");
                    int optionCount = Math.Min(action.OptionValues.Length, action.OptionLabels.Length);
                    for (int optionIndex = 0; optionIndex < optionCount; optionIndex++)
                    {
                        if (optionIndex > 0) json.Append(',');
                        json.Append("{\"value\":").Append(Quote(action.OptionValues[optionIndex]))
                            .Append(",\"label\":").Append(Quote(action.OptionLabels[optionIndex])).Append('}');
                    }
                    json.Append("]}");
                }
                json.Append("]}");
            }
            return json.Append("]}").ToString();
        }

        internal static string RecorderReset(long revision)
        {
            return "{\"type\":\"recorder.reset\",\"protocol\":1,\"revision\":" + revision.ToString(Invariant) + "}";
        }

        internal static string RecorderSample(FlightRecorderSample value)
        {
            var json = new StringBuilder(420);
            json.Append("{\"type\":\"recorder.sample\",\"protocol\":1");
            AppendRecorderFields(json, value);
            return json.Append('}').ToString();
        }

        internal static string RecorderHistory(FlightRecorderHistory value)
        {
            var json = new StringBuilder(128 + value.Samples.Length * 360);
            json.Append("{\"type\":\"recorder.history\",\"protocol\":1");
            Append(json, "revision", value.Revision);
            json.Append(",\"samples\":[");
            for (int index = 0; index < value.Samples.Length; index++)
            {
                if (index > 0) json.Append(',');
                json.Append('{');
                AppendRecorderFields(json, value.Samples[index], false);
                json.Append('}');
            }
            return json.Append("]}").ToString();
        }

        internal static string RecorderCsv(FlightRecorderHistory value)
        {
            var csv = new StringBuilder(256 + value.Samples.Length * 260);
            csv.AppendLine("TimeSinceMark,CurrentStage,AltitudeASL,DownRange,SpeedSurface,SpeedOrbital,Mass,Acceleration,Q,AoA,AoS,AoD,AltitudeTrue,Pitch,GravityLosses,DragLosses,SteeringLosses,DeltaVExpended");
            foreach (FlightRecorderSample sample in value.Samples)
            {
                csv.Append(Number(sample.TimeSinceMark)).Append(',')
                    .Append(sample.CurrentStage.ToString(Invariant)).Append(',')
                    .Append(Number(sample.AltitudeAsl)).Append(',').Append(Number(sample.DownRange)).Append(',')
                    .Append(Number(sample.SpeedSurface)).Append(',').Append(Number(sample.SpeedOrbital)).Append(',')
                    .Append(Number(sample.Mass)).Append(',').Append(Number(sample.Acceleration)).Append(',')
                    .Append(Number(sample.DynamicPressure)).Append(',').Append(Number(sample.AngleOfAttack)).Append(',')
                    .Append(Number(sample.AngleOfSideslip)).Append(',').Append(Number(sample.AngleOfDisplacement)).Append(',')
                    .Append(Number(sample.AltitudeTrue)).Append(',').Append(Number(sample.Pitch)).Append(',')
                    .Append(Number(sample.GravityLosses)).Append(',').Append(Number(sample.DragLosses)).Append(',')
                    .Append(Number(sample.SteeringLosses)).Append(',').Append(Number(sample.DeltaVExpended)).AppendLine();
            }
            return csv.ToString();
        }

        internal static string FlightPanel(FlightPanelSnapshot value)
        {
            var json = new StringBuilder(1400);
            json.Append("{\"type\":\"telemetry.flightPanel\",\"protocol\":1");
            Append(json, "sequence", value.Sequence);
            AppendNullable(json, "vesselId", value.VesselId);
            AppendNullable(json, "currentOrbit", value.CurrentOrbit);
            Append(json, "orbitInclination", value.OrbitInclination);
            Append(json, "orbitPeriod", value.OrbitPeriod);
            Append(json, "orbitalSpeed", value.OrbitalSpeed);
            Append(json, "timeToApoapsis", value.TimeToApoapsis);
            Append(json, "timeToPeriapsis", value.TimeToPeriapsis);
            AppendNullable(json, "timeToSoiTransition", value.TimeToSoiTransition);
            Append(json, "orbitEccentricity", value.OrbitEccentricity);
            Append(json, "altitudeBottom", value.AltitudeBottom);
            AppendNullable(json, "coordinates", value.Coordinates);
            Append(json, "heading", value.Heading);
            Append(json, "surfaceGravity", value.SurfaceGravity);
            Append(json, "surfaceSpeed", value.SurfaceSpeed);
            Append(json, "verticalSpeed", value.VerticalSpeed);
            Append(json, "horizontalSurfaceSpeed", value.HorizontalSurfaceSpeed);
            Append(json, "maximumAcceleration", value.MaximumAcceleration);
            Append(json, "currentAcceleration", value.CurrentAcceleration);
            Append(json, "maximumThrust", value.MaximumThrust);
            Append(json, "currentThrust", value.CurrentThrust);
            Append(json, "surfaceTwr", value.SurfaceTwr);
            Append(json, "localTwr", value.LocalTwr);
            Append(json, "throttleTwr", value.ThrottleTwr);
            Append(json, "geeForce", value.GeeForce);
            Append(json, "atmosphericDrag", value.AtmosphericDrag);
            Append(json, "crewCapacity", value.CrewCapacity);
            Append(json, "crewCount", value.CrewCount);
            Append(json, "dryMass", value.DryMass);
            Append(json, "vesselMass", value.VesselMass);
            Append(json, "partCount", value.PartCount);
            Append(json, "stageDeltaVAtmosphere", value.StageDeltaVAtmosphere);
            Append(json, "stageDeltaVVacuum", value.StageDeltaVVacuum);
            Append(json, "totalDeltaVAtmosphere", value.TotalDeltaVAtmosphere);
            Append(json, "totalDeltaVVacuum", value.TotalDeltaVVacuum);
            Append(json, "stageTimeCurrentThrottle", value.StageTimeCurrentThrottle);
            Append(json, "stageTimeFullThrottle", value.StageTimeFullThrottle);
            Append(json, "stageTimeHover", value.StageTimeHover);
            Append(json, "terminalVelocity", value.TerminalVelocity);
            Append(json, "vesselCost", value.VesselCost);
            Append(json, "angleOfAttack", value.AngleOfAttack);
            Append(json, "angleOfSideslip", value.AngleOfSideslip);
            Append(json, "staticPressureKpa", value.StaticPressureKpa);
            AppendNullable(json, "currentBiome", value.CurrentBiome);
            Append(json, "dynamicPressureKpa", value.DynamicPressureKpa);
            AppendNullable(json, "suicideBurnCountdown", value.SuicideBurnCountdown);
            AppendNullable(json, "timeToImpact", value.TimeToImpact);
            AppendNullable(json, "targetClosestApproachDistance", value.TargetClosestApproachDistance);
            AppendNullable(json, "targetDistance", value.TargetDistance);
            return json.Append('}').ToString();
        }

        internal static string Automation(AutomationSnapshot value)
        {
            var json = new StringBuilder(560);
            json.Append("{\"type\":\"telemetry.automation\",\"protocol\":1");
            Append(json, "sequence", value.Sequence);
            AppendNullable(json, "vesselId", value.VesselId);
            Append(json, "mechJebAvailable", value.MechJebAvailable);
            Append(json, "maneuverNodeCount", value.ManeuverNodeCount);
            Append(json, "nextNodeUt", value.NextNodeUt);
            Append(json, "nextNodeDeltaV", value.NextNodeDeltaV);
            json.Append(",\"maneuverNodes\":[");
            ManeuverNodeSnapshot[] maneuverNodes = value.ManeuverNodes ?? new ManeuverNodeSnapshot[0];
            for (int index = 0; index < maneuverNodes.Length; index++)
            {
                if (index > 0) json.Append(',');
                json.Append('{');
                json.Append("\"ut\":").Append(Number(maneuverNodes[index].UniversalTime));
                json.Append(",\"deltaV\":").Append(Number(maneuverNodes[index].DeltaV));
                json.Append('}');
            }
            json.Append(']');
            Append(json, "nodeExecutorEnabled", value.NodeExecutorEnabled);
            Append(json, "smartAssEnabled", value.SmartAssEnabled);
            json.Append(",\"orbitGeometry\":").Append(value.OrbitGeometryJson ?? "null");
            AppendNullable(json, "smartAssMode", value.SmartAssMode);
            AppendNullable(json, "smartAssTarget", value.SmartAssTarget);
            Append(json, "smartAssAutoDisable", value.SmartAssAutoDisable);
            Append(json, "smartAssSurfacePitch", value.SmartAssSurfacePitch);
            Append(json, "smartAssSurfaceYaw", value.SmartAssSurfaceYaw);
            Append(json, "smartAssSurfaceRoll", value.SmartAssSurfaceRoll);
            Append(json, "smartAssVelocityPitch", value.SmartAssVelocityPitch);
            Append(json, "smartAssVelocityYaw", value.SmartAssVelocityYaw);
            Append(json, "smartAssVelocityRoll", value.SmartAssVelocityRoll);
            Append(json, "smartAssRoll", value.SmartAssRoll);
            Append(json, "landingEnabled", value.LandingEnabled);
            Append(json, "landingAtTarget", value.LandingAtTarget);
            Append(json, "landingPredictionReady", value.LandingPredictionReady);
            AppendNullable(json, "landingStatus", value.LandingStatus);
            AppendNullable(json, "landingStep", value.LandingStep);
            AppendNullable(json, "landingDescentMode", value.LandingDescentMode);
            Append(json, "landingUsesAtmosphere", value.LandingUsesAtmosphere);
            Append(json, "landingTouchdownSpeed", value.LandingTouchdownSpeed);
            Append(json, "landingDeployGears", value.LandingDeployGears);
            Append(json, "landingGearStageLimit", value.LandingGearStageLimit);
            Append(json, "landingDeployChutes", value.LandingDeployChutes);
            Append(json, "landingChuteStageLimit", value.LandingChuteStageLimit);
            Append(json, "landingRcsAdjustment", value.LandingRcsAdjustment);
            Append(json, "landingPredictionsEnabled", value.LandingPredictionsEnabled);
            Append(json, "landingPredictionSimulationRunning", value.LandingPredictionSimulationRunning);
            AppendNullable(json, "landingPredictionOutcome", value.LandingPredictionOutcome);
            Append(json, "landingPredictedLatitude", value.LandingPredictedLatitude);
            Append(json, "landingPredictedLongitude", value.LandingPredictedLongitude);
            Append(json, "landingPredictedAltitude", value.LandingPredictedAltitude);
            Append(json, "landingPredictionTargetDistance", value.LandingPredictionTargetDistance);
            Append(json, "landingPredictionMaxDrag", value.LandingPredictionMaxDrag);
            Append(json, "landingPredictionDeltaV", value.LandingPredictionDeltaV);
            Append(json, "landingPredictionTimeToLand", value.LandingPredictionTimeToLand);
            Append(json, "landingPredictionAerobrake", value.LandingPredictionAerobrake);
            Append(json, "landingAerobrakePeriapsis", value.LandingAerobrakePeriapsis);
            Append(json, "landingAerobrakeApoapsis", value.LandingAerobrakeApoapsis);
            Append(json, "landingAerobrakeEccentricity", value.LandingAerobrakeEccentricity);
            Append(json, "landingMakeAerobrakeNodes", value.LandingMakeAerobrakeNodes);
            Append(json, "landingShowTrajectory", value.LandingShowTrajectory);
            Append(json, "landingWorldTrajectory", value.LandingWorldTrajectory);
            Append(json, "landingCameraTrajectory", value.LandingCameraTrajectory);
            Append(json, "dockingEnabled", value.DockingEnabled);
            AppendNullable(json, "dockingStatus", value.DockingStatus);
            AppendNullable(json, "dockingStep", value.DockingStep);
            Append(json, "dockingAxialSeparation", value.DockingAxialSeparation);
            Append(json, "dockingLateralSeparation", value.DockingLateralSeparation);
            Append(json, "dockingAxisAvailable", value.DockingAxisAvailable);
            Append(json, "dockingAxisErrorX", value.DockingAxisErrorX);
            Append(json, "dockingAxisErrorY", value.DockingAxisErrorY);
            Append(json, "dockingSpeedLimit", value.DockingSpeedLimit);
            Append(json, "dockingOverrideSafeDistance", value.DockingOverrideSafeDistance);
            Append(json, "dockingSafeDistance", value.DockingSafeDistance);
            Append(json, "dockingOverrideStartDistance", value.DockingOverrideStartDistance);
            Append(json, "dockingStartDistance", value.DockingStartDistance);
            Append(json, "dockingForceRoll", value.DockingForceRoll);
            Append(json, "dockingRoll", value.DockingRoll);
            Append(json, "dockingDrawBoundingBox", value.DockingDrawBoundingBox);
            Append(json, "ascentEnabled", value.AscentEnabled);
            AppendNullable(json, "ascentStatus", value.AscentStatus);
            AppendNullable(json, "ascentPath", value.AscentPath);
            Append(json, "ascentDesiredOrbitAltitude", value.AscentDesiredOrbitAltitude);
            Append(json, "ascentDesiredInclination", value.AscentDesiredInclination);
            Append(json, "ascentTimedLaunch", value.AscentTimedLaunch);
            Append(json, "ascentTimeToLaunch", value.AscentTimeToLaunch);
            AppendNullable(json, "ascentLaunchMode", value.AscentLaunchMode);
            Append(json, "ascentLaunchTargetAvailable", value.AscentLaunchTargetAvailable);
            AppendNullable(json, "ascentLaunchTargetName", value.AscentLaunchTargetName);
            Append(json, "ascentLaunchPhaseAngle", value.AscentLaunchPhaseAngle);
            Append(json, "ascentLaunchLanDifference", value.AscentLaunchLanDifference);
            Append(json, "ascentAutoThrottle", value.AscentAutoThrottle);
            Append(json, "ascentCorrectiveSteering", value.AscentCorrectiveSteering);
            Append(json, "ascentAutoStage", value.AscentAutoStage);
            Append(json, "ascentLimitAoA", value.AscentLimitAoA);
            Append(json, "ascentMaxAoA", value.AscentMaxAoA);
            Append(json, "ascentLimitQEnabled", value.AscentLimitQEnabled);
            Append(json, "ascentLimitQ", value.AscentLimitQ);
            Append(json, "ascentForceRoll", value.AscentForceRoll);
            Append(json, "ascentVerticalRoll", value.AscentVerticalRoll);
            Append(json, "ascentTurnRoll", value.AscentTurnRoll);
            Append(json, "ascentSkipCircularization", value.AscentSkipCircularization);
            Append(json, "ascentDesiredLan", value.AscentDesiredLan);
            Append(json, "ascentCorrectiveSteeringGain", value.AscentCorrectiveSteeringGain);
            Append(json, "ascentDeploySolarPanels", value.AscentDeploySolarPanels);
            Append(json, "ascentDeployAntennas", value.AscentDeployAntennas);
            Append(json, "ascentRollAltitude", value.AscentRollAltitude);
            Append(json, "ascentAoAFadeoutPressure", value.AscentAoAFadeoutPressure);
            Append(json, "ascentGtTurnStartAltitude", value.AscentGtTurnStartAltitude);
            Append(json, "ascentGtTurnStartVelocity", value.AscentGtTurnStartVelocity);
            Append(json, "ascentGtTurnStartPitch", value.AscentGtTurnStartPitch);
            Append(json, "ascentGtIntermediateAltitude", value.AscentGtIntermediateAltitude);
            Append(json, "ascentGtHoldApTime", value.AscentGtHoldApTime);
            Append(json, "ascentClassicTurnStartAltitude", value.AscentClassicTurnStartAltitude);
            Append(json, "ascentClassicTurnStartVelocity", value.AscentClassicTurnStartVelocity);
            Append(json, "ascentClassicTurnEndAltitude", value.AscentClassicTurnEndAltitude);
            Append(json, "ascentClassicTurnEndAngle", value.AscentClassicTurnEndAngle);
            Append(json, "ascentClassicTurnShapeExponent", value.AscentClassicTurnShapeExponent);
            Append(json, "ascentClassicAutoPath", value.AscentClassicAutoPath);
            Append(json, "ascentPvgPitchStartVelocity", value.AscentPvgPitchStartVelocity);
            Append(json, "ascentPvgPitchRate", value.AscentPvgPitchRate);
            Append(json, "ascentPvgDesiredApoapsis", value.AscentPvgDesiredApoapsis);
            Append(json, "ascentPvgDesiredAttachAltitude", value.AscentPvgDesiredAttachAltitude);
            Append(json, "ascentPvgDynamicPressureTrigger", value.AscentPvgDynamicPressureTrigger);
            Append(json, "ascentPvgStagingTrigger", value.AscentPvgStagingTrigger);
            Append(json, "ascentPvgFixedCoast", value.AscentPvgFixedCoast);
            Append(json, "ascentPvgFixedCoastLength", value.AscentPvgFixedCoastLength);
            Append(json, "ascentPvgAttachAltitudeEnabled", value.AscentPvgAttachAltitudeEnabled);
            Append(json, "ascentPvgStagingTriggerEnabled", value.AscentPvgStagingTriggerEnabled);
            Append(json, "autoWarp", value.AutoWarp);
            Append(json, "rendezvousEnabled", value.RendezvousEnabled);
            AppendNullable(json, "rendezvousStatus", value.RendezvousStatus);
            Append(json, "rendezvousDesiredDistance", value.RendezvousDesiredDistance);
            Append(json, "rendezvousMaxPhasingOrbits", value.RendezvousMaxPhasingOrbits);
            Append(json, "rendezvousMaxClosingSpeed", value.RendezvousMaxClosingSpeed);
            AppendNullable(json, "targetName", value.TargetName);
            AppendNullable(json, "targetKind", value.TargetKind);
            Append(json, "targetDistance", value.TargetDistance);
            Append(json, "targetRelativeSpeed", value.TargetRelativeSpeed);
            Append(json, "targetOrbitApoapsis", value.TargetOrbitApoapsis);
            Append(json, "targetOrbitPeriapsis", value.TargetOrbitPeriapsis);
            Append(json, "targetOrbitInclination", value.TargetOrbitInclination);
            Append(json, "targetOrbitPeriod", value.TargetOrbitPeriod);
            json.Append(",\"targetCatalog\":[");
            TargetCatalogEntry[] targetCatalog = value.TargetCatalog ?? new TargetCatalogEntry[0];
            for (int index = 0; index < targetCatalog.Length; index++)
            {
                if (index > 0) json.Append(',');
                TargetCatalogEntry entry = targetCatalog[index];
                json.Append("{\"id\":").Append(Quote(entry.Id));
                json.Append(",\"parentId\":").Append(entry.ParentId == null ? "null" : Quote(entry.ParentId));
                json.Append(",\"kind\":").Append(Quote(entry.Kind));
                json.Append(",\"name\":").Append(Quote(entry.Name));
                json.Append(",\"bodyName\":").Append(entry.BodyName == null ? "null" : Quote(entry.BodyName));
                json.Append(",\"vesselId\":").Append(entry.VesselId == null ? "null" : Quote(entry.VesselId));
                json.Append(",\"partId\":").Append(entry.PartId.ToString(Invariant));
                json.Append('}');
            }
            json.Append(']');
            Append(json, "positionTargetExists", value.PositionTargetExists);
            AppendNullable(json, "targetBody", value.TargetBody);
            Append(json, "targetLatitude", value.TargetLatitude);
            Append(json, "targetLongitude", value.TargetLongitude);
            Append(json, "porkchopRevision", value.PorkchopRevision);
            AppendNullable(json, "porkchopStatus", value.PorkchopStatus);
            Append(json, "porkchopProgress", value.PorkchopProgress);
            Append(json, "sas", value.Sas);
            Append(json, "rcs", value.Rcs);
            Append(json, "gear", value.Gear);
            Append(json, "brakes", value.Brakes);
            Append(json, "lights", value.Lights);
            Append(json, "actionGroupMask", value.ActionGroupMask);
            AppendNullable(json, "solarPanels", value.SolarPanels);
            AppendNullable(json, "antennas", value.Antennas);
            Append(json, "autoStage", value.AutoStage);
            Append(json, "envelopeAvailable", value.EnvelopeAvailable);
            Append(json, "envelopeTerminalVelocity", value.EnvelopeTerminalVelocity);
            Append(json, "envelopeDynamicPressure", value.EnvelopeDynamicPressure);
            Append(json, "envelopeMaximumDynamicPressure", value.EnvelopeMaximumDynamicPressure);
            Append(json, "envelopeAcceleration", value.EnvelopeAcceleration);
            Append(json, "envelopeMaximumAcceleration", value.EnvelopeMaximumAcceleration);
            Append(json, "envelopeMaximumThrottle", value.EnvelopeMaximumThrottle);
            Append(json, "envelopeMaximumThrottleValue", value.EnvelopeMaximumThrottleValue);
            Append(json, "envelopeMinimumThrottle", value.EnvelopeMinimumThrottle);
            Append(json, "envelopeMinimumThrottleValue", value.EnvelopeMinimumThrottleValue);
            Append(json, "envelopePreventOverheat", value.EnvelopePreventOverheat);
            Append(json, "envelopePreventFlameout", value.EnvelopePreventFlameout);
            Append(json, "envelopeFlameoutSafetyPercent", value.EnvelopeFlameoutSafetyPercent);
            Append(json, "envelopePreventUnstableIgnition", value.EnvelopePreventUnstableIgnition);
            Append(json, "envelopeAutoRcsUllage", value.EnvelopeAutoRcsUllage);
            Append(json, "envelopeSmoothThrottle", value.EnvelopeSmoothThrottle);
            Append(json, "envelopeThrottleSmoothingTime", value.EnvelopeThrottleSmoothingTime);
            Append(json, "envelopeManageIntakes", value.EnvelopeManageIntakes);
            Append(json, "envelopeDifferentialThrottle", value.EnvelopeDifferentialThrottle);
            AppendTrajectory(json, value.Trajectory);
            return json.Append('}').ToString();
        }

        private static void AppendTrajectory(StringBuilder json, TrajectoryPredictionSnapshot value)
        {
            value = value ?? new TrajectoryPredictionSnapshot();
            json.Append(",\"trajectory\":{");
            json.Append("\"available\":").Append(value.Available ? "true" : "false");
            AppendNullable(json, "version", value.Version);
            AppendNullable(json, "status", value.Status);
            Append(json, "display", value.DisplayTrajectories);
            Append(json, "displayInFlight", value.DisplayTrajectoriesInFlight);
            Append(json, "alwaysUpdate", value.AlwaysUpdate);
            Append(json, "complete", value.DisplayCompleteTrajectory);
            Append(json, "bodyFixed", value.BodyFixedMode);
            Append(json, "autoUpdateAero", value.AutoUpdateAerodynamicModel);
            Append(json, "useCache", value.UseCache);
            Append(json, "defaultRetrograde", value.DefaultDescentIsRetrograde);
            Append(json, "integrationStep", value.IntegrationStepSize);
            Append(json, "maxPatches", value.MaximumPatchCount);
            Append(json, "maxFramesPerPatch", value.MaximumFramesPerPatch);
            AppendNullable(json, "aerodynamicModel", value.AerodynamicModel);
            Append(json, "computationTimeMs", value.ComputationTimeMilliseconds);
            Append(json, "errorCount", value.ErrorCount);
            Append(json, "maximumDecelerationG", value.MaximumDecelerationG);
            Append(json, "impactAvailable", value.ImpactAvailable);
            json.Append(",\"paths\":").Append(value.PathsJson ?? "[]");
            json.Append(",\"groundPaths\":").Append(value.GroundPathsJson ?? "[]");
            Append(json, "surfaceMapVersion", value.SurfaceMapVersion);
            Append(json, "bodyRadius", value.BodyRadius);
            Append(json, "impactTargetDistance", value.ImpactTargetDistance);
            AppendNullable(json, "pathStatus", value.PathStatus);
            Append(json, "timeToImpact", value.TimeToImpact);
            Append(json, "impactLatitude", value.ImpactLatitude);
            Append(json, "impactLongitude", value.ImpactLongitude);
            Append(json, "impactAltitude", value.ImpactAltitude);
            Append(json, "impactSpeed", value.ImpactSpeed);
            Append(json, "impactVerticalSpeed", value.ImpactVerticalSpeed);
            Append(json, "impactHorizontalSpeed", value.ImpactHorizontalSpeed);
            Append(json, "targetAvailable", value.TargetAvailable);
            Append(json, "targetLatitude", value.TargetLatitude);
            Append(json, "targetLongitude", value.TargetLongitude);
            Append(json, "targetAltitude", value.TargetAltitude);
            AppendNullable(json, "entryMode", value.EntryMode);
            Append(json, "entryRetrograde", value.EntryRetrograde);
            Append(json, "entryAngle", value.EntryAngle);
            AppendNullable(json, "highMode", value.HighAltitudeMode);
            Append(json, "highRetrograde", value.HighAltitudeRetrograde);
            Append(json, "highAngle", value.HighAltitudeAngle);
            AppendNullable(json, "lowMode", value.LowAltitudeMode);
            Append(json, "lowRetrograde", value.LowAltitudeRetrograde);
            Append(json, "lowAngle", value.LowAltitudeAngle);
            AppendNullable(json, "finalMode", value.FinalApproachMode);
            Append(json, "finalRetrograde", value.FinalApproachRetrograde);
            Append(json, "finalAngle", value.FinalApproachAngle);
            json.Append('}');
        }

        internal static string Porkchop(PorkchopResultSnapshot value)
        {
            var json = new StringBuilder(256 + value.Costs.Length * 12);
            json.Append("{\"type\":\"maneuver.porkchop\",\"protocol\":1");
            Append(json, "revision", value.Revision);
            Append(json, "width", value.Width);
            Append(json, "height", value.Height);
            Append(json, "minimumDepartureTime", value.MinimumDepartureTime);
            Append(json, "maximumDepartureTime", value.MaximumDepartureTime);
            Append(json, "minimumTransferTime", value.MinimumTransferTime);
            Append(json, "maximumTransferTime", value.MaximumTransferTime);
            Append(json, "bestDepartureIndex", value.BestDepartureIndex);
            Append(json, "bestDurationIndex", value.BestDurationIndex);
            json.Append(",\"costs\":[");
            for (int index = 0; index < value.Costs.Length; index++)
            {
                if (index > 0) json.Append(',');
                json.Append(Number(value.Costs[index]));
            }
            return json.Append("]}").ToString();
        }

        private static void AppendRecorderFields(StringBuilder json, FlightRecorderSample value, bool leadingComma = true)
        {
            if (!leadingComma && json.Length > 0 && json[json.Length - 1] == '{')
            {
                json.Append("\"sequence\":").Append(value.Sequence.ToString(Invariant));
            }
            else Append(json, "sequence", value.Sequence);
            Append(json, "revision", value.Revision);
            Append(json, "timeSinceMark", value.TimeSinceMark);
            Append(json, "currentStage", value.CurrentStage);
            Append(json, "altitudeAsl", value.AltitudeAsl);
            Append(json, "downRange", value.DownRange);
            Append(json, "speedSurface", value.SpeedSurface);
            Append(json, "speedOrbital", value.SpeedOrbital);
            Append(json, "mass", value.Mass);
            Append(json, "acceleration", value.Acceleration);
            Append(json, "dynamicPressure", value.DynamicPressure);
            Append(json, "angleOfAttack", value.AngleOfAttack);
            Append(json, "angleOfSideslip", value.AngleOfSideslip);
            Append(json, "angleOfDisplacement", value.AngleOfDisplacement);
            Append(json, "altitudeTrue", value.AltitudeTrue);
            Append(json, "pitch", value.Pitch);
            Append(json, "gravityLosses", value.GravityLosses);
            Append(json, "dragLosses", value.DragLosses);
            Append(json, "steeringLosses", value.SteeringLosses);
            Append(json, "deltaVExpended", value.DeltaVExpended);
        }

        internal static bool TryParseIncoming(string json, out IncomingMessage message)
        {
            message = null;
            if (string.IsNullOrWhiteSpace(json) || json.Length > 65536)
            {
                return false;
            }

            string type = StringProperty(json, "type");
            if (type == null)
            {
                return false;
            }

            message = new IncomingMessage
            {
                Type = type,
                CommandId = StringProperty(json, "commandId"),
                Name = StringProperty(json, "name"),
                Sequence = LongProperty(json, "sequence"),
                RawJson = json
            };
            return true;
        }

        internal static string ReadString(string json, string name)
        {
            return StringProperty(json, name);
        }

        internal static double ReadDouble(string json, string name, double fallback)
        {
            Match match = Regex.Match(json, "\\\"" + Regex.Escape(name) + "\\\"\\s*:\\s*(-?(?:[0-9]+(?:\\.[0-9]*)?|\\.[0-9]+)(?:[eE][+-]?[0-9]+)?)");
            double value;
            return match.Success && double.TryParse(match.Groups[1].Value, NumberStyles.Float, Invariant, out value) ? value : fallback;
        }

        internal static bool ReadBool(string json, string name, bool fallback)
        {
            Match match = Regex.Match(json, "\\\"" + Regex.Escape(name) + "\\\"\\s*:\\s*(true|false)", RegexOptions.IgnoreCase);
            return match.Success ? string.Equals(match.Groups[1].Value, "true", StringComparison.OrdinalIgnoreCase) : fallback;
        }

        private static string StringProperty(string json, string name)
        {
            Match match = Regex.Match(json, "\\\"" + Regex.Escape(name) + "\\\"\\s*:\\s*\\\"((?:\\\\.|[^\\\"])*)\\\"");
            if (!match.Success)
            {
                return null;
            }

            return match.Groups[1].Value
                .Replace("\\\"", "\"")
                .Replace("\\\\", "\\")
                .Replace("\\n", "\n")
                .Replace("\\r", "\r")
                .Replace("\\t", "\t");
        }

        private static long LongProperty(string json, string name)
        {
            Match match = Regex.Match(json, "\\\"" + Regex.Escape(name) + "\\\"\\s*:\\s*(-?[0-9]+)");
            long value;
            return match.Success && long.TryParse(match.Groups[1].Value, NumberStyles.Integer, Invariant, out value) ? value : 0;
        }

        private static void Append(StringBuilder json, string name, string value)
        {
            json.Append(",\"").Append(name).Append("\":").Append(Quote(value));
        }

        private static void AppendNullable(StringBuilder json, string name, string value)
        {
            json.Append(",\"").Append(name).Append("\":").Append(value == null ? "null" : Quote(value));
        }

        private static void Append(StringBuilder json, string name, long value)
        {
            json.Append(",\"").Append(name).Append("\":").Append(value.ToString(Invariant));
        }

        private static void Append(StringBuilder json, string name, double value)
        {
            json.Append(",\"").Append(name).Append("\":");
            json.Append(double.IsNaN(value) || double.IsInfinity(value) ? "null" : value.ToString("R", Invariant));
        }

        private static void Append(StringBuilder json, string name, bool value)
        {
            json.Append(",\"").Append(name).Append("\":").Append(value ? "true" : "false");
        }

        private static string Number(double value)
        {
            return double.IsNaN(value) || double.IsInfinity(value) ? "null" : value.ToString("0.######", Invariant);
        }

        private static void AppendChange(StringBuilder changes, bool include, string value)
        {
            if (!include) return;
            if (changes.Length > 0) changes.Append(',');
            changes.Append(Quote(value));
        }

        private static string Quote(string value)
        {
            if (value == null)
            {
                return "null";
            }

            var escaped = new StringBuilder(value.Length + 8);
            escaped.Append('"');
            foreach (char character in value)
            {
                switch (character)
                {
                    case '"': escaped.Append("\\\""); break;
                    case '\\': escaped.Append("\\\\"); break;
                    case '\n': escaped.Append("\\n"); break;
                    case '\r': escaped.Append("\\r"); break;
                    case '\t': escaped.Append("\\t"); break;
                    default:
                        if (character < 32) escaped.Append("\\u").Append(((int)character).ToString("x4"));
                        else escaped.Append(character);
                        break;
                }
            }
            return escaped.Append('"').ToString();
        }
    }
}
