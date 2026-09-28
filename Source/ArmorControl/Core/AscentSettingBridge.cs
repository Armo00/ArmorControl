using System;
using System.Globalization;
using MuMech;

namespace ArmorOverhaul.ArmorControl.Core
{
    // Called only through the existing main-thread FIFO. Each request writes one setting.
    internal static class AscentSettingBridge
    {
        internal static string Set(MechJebCore core, string field, string value)
        {
            var ascent = core.GetComputerModule<MechJebModuleAscentAutopilot>();
            if (ascent == null) return "MechJeb ascent autopilot is unavailable.";
            var gt = core.GetComputerModule<MechJebModuleAscentGT>();
            var classic = core.GetComputerModule<MechJebModuleAscentClassic>();
            var pvg = core.GetComputerModule<MechJebModuleAscentPVG>();
            var guidance = core.GetComputerModule<MechJebModuleAscentGuidance>();
            double number;
            bool flag;
            bool validNumber = double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number) && !double.IsNaN(number) && !double.IsInfinity(number);
            bool validBool = bool.TryParse(value, out flag);
            switch (field)
            {
                case "ascentPath":
                    ascentType path;
                    if (ascent.enabled) return "Stop ascent guidance before changing the ascent profile.";
                    if (!Enum.TryParse(value, true, out path) || !Enum.IsDefined(typeof(ascentType), path)) return "Unknown MechJeb ascent path.";
                    ascent.ascentPathIdxPublic = path;
                    return null;
                case "orbitAltitudeKm":

                    if (ascent.ascentPathIdxPublic == ascentType.PVG && pvg != null && number * 1000 > pvg.DesiredApoapsis.val) return "PVG apoapsis must be at or above its periapsis.";
                    if (!validNumber || number < 1 || number > 1000000) return "Invalid ascent setting value.";
                    ascent.desiredOrbitAltitude.val = (number * 1000);
                    return null;
                case "inclination":

                    if (guidance != null && guidance.launchingToPlane) return "Target-plane launch controls the inclination.";
                    if (!validNumber || number < -180 || number > 180) return "Invalid ascent setting value.";
                    ascent.desiredInclination = (number * 1);
                    // MJ 2.14.3's window writes its independent EditableDouble back every draw.
                    if (guidance != null) guidance.desiredInclination.val = number;
                    return null;
                case "desiredLan":

                    if (!validNumber || number < -180 || number > 360) return "Invalid ascent setting value.";
                    ascent.desiredLAN = (number * 1);
                    if (guidance != null) guidance.desiredLAN.val = number;
                    return null;
                case "autoThrottle":

                    if (!validBool) return "Invalid ascent setting value.";
                    ascent.autoThrottle = flag;
                    return null;
                case "correctiveSteering":

                    if (!validBool) return "Invalid ascent setting value.";
                    ascent.correctiveSteering = flag;
                    return null;
                case "autoStage":

                    if (!validBool) return "Invalid ascent setting value.";
                    ascent.autostage = flag;
                    return null;
                case "skipCircularization":

                    if (!validBool) return "Invalid ascent setting value.";
                    ascent.skipCircularization = flag;
                    return null;
                case "limitAoA":

                    if (!validBool) return "Invalid ascent setting value.";
                    ascent.limitAoA = flag;
                    return null;
                case "maxAoA":

                    if (!validNumber || number < 0 || number > 90) return "Invalid ascent setting value.";
                    ascent.maxAoA.val = (number * 1);
                    return null;
                case "limitQ":

                    if (!validBool) return "Invalid ascent setting value.";
                    ascent.limitQaEnabled = flag;
                    return null;
                case "maxQKpa":

                    if (!validNumber || number < 0 || number > 1000000000) return "Invalid ascent setting value.";
                    ascent.limitQa.val = (number * 1000);
                    return null;
                case "forceRoll":

                    if (!validBool) return "Invalid ascent setting value.";
                    ascent.forceRoll = flag;
                    return null;
                case "verticalRoll":

                    if (!validNumber || number < -180 || number > 180) return "Invalid ascent setting value.";
                    ascent.verticalRoll.val = (number * 1);
                    return null;
                case "turnRoll":

                    if (!validNumber || number < -180 || number > 180) return "Invalid ascent setting value.";
                    ascent.turnRoll.val = (number * 1);
                    return null;
                case "correctiveSteeringGain":

                    if (!validNumber || number < 0 || number > 1000000000) return "Invalid ascent setting value.";
                    ascent.correctiveSteeringGain.val = (number * 1);
                    return null;
                case "deploySolarPanels":

                    if (!validBool) return "Invalid ascent setting value.";
                    ascent.autodeploySolarPanels = flag;
                    return null;
                case "deployAntennas":

                    if (!validBool) return "Invalid ascent setting value.";
                    ascent.autoDeployAntennas = flag;
                    return null;
                case "rollAltitudeKm":

                    if (!validNumber || number < 0 || number > 1000000) return "Invalid ascent setting value.";
                    ascent.rollAltitude.val = (number * 1000);
                    return null;
                case "aoaFadeoutKpa":

                    if (!validNumber || number < 0 || number > 1000000000) return "Invalid ascent setting value.";
                    ascent.aoALimitFadeoutPressure.val = (number * 1000);
                    return null;
                case "launchPhaseAngle":

                    if (!validNumber || number < -360 || number > 360) return "Invalid ascent setting value.";
                    ascent.launchPhaseAngle.val = (number * 1);
                    return null;
                case "launchLanDifference":

                    if (!validNumber || number < -360 || number > 360) return "Invalid ascent setting value.";
                    ascent.launchLANDifference.val = (number * 1);
                    return null;
                case "gtTurnStartAltitudeKm":
                    if (gt == null) return "MechJeb ascent module is unavailable.";
                    if (!validNumber || number < 0 || number > 1000000) return "Invalid ascent setting value.";
                    gt.turnStartAltitude.val = (number * 1000);
                    return null;
                case "gtTurnStartVelocity":
                    if (gt == null) return "MechJeb ascent module is unavailable.";
                    if (!validNumber || number < 0 || number > 1000000000) return "Invalid ascent setting value.";
                    gt.turnStartVelocity.val = (number * 1);
                    return null;
                case "gtTurnStartPitch":
                    if (gt == null) return "MechJeb ascent module is unavailable.";
                    if (!validNumber || number < 0 || number > 90) return "Invalid ascent setting value.";
                    gt.turnStartPitch.val = (number * 1);
                    return null;
                case "gtIntermediateAltitudeKm":
                    if (gt == null) return "MechJeb ascent module is unavailable.";
                    if (!validNumber || number < 0 || number > 1000000) return "Invalid ascent setting value.";
                    gt.intermediateAltitude.val = (number * 1000);
                    return null;
                case "gtHoldApTime":
                    if (gt == null) return "MechJeb ascent module is unavailable.";
                    if (!validNumber || number < 0 || number > 1000000000) return "Invalid ascent setting value.";
                    gt.holdAPTime.val = (number * 1);
                    return null;
                case "classicAutoPath":
                    if (classic == null) return "MechJeb ascent module is unavailable.";
                    if (!validBool) return "Invalid ascent setting value.";
                    classic.autoPath = flag;
                    return null;
                case "classicTurnStartAltitudeKm":
                    if (classic == null) return "MechJeb ascent module is unavailable.";
                    if (!validNumber || number < 0 || number > 1000000) return "Invalid ascent setting value.";
                    classic.turnStartAltitude.val = (number * 1000);
                    return null;
                case "classicTurnStartVelocity":
                    if (classic == null) return "MechJeb ascent module is unavailable.";
                    if (!validNumber || number < 0 || number > 1000000000) return "Invalid ascent setting value.";
                    classic.turnStartVelocity.val = (number * 1);
                    return null;
                case "classicTurnEndAltitudeKm":
                    if (classic == null) return "MechJeb ascent module is unavailable.";
                    if (!validNumber || number < 0 || number > 1000000) return "Invalid ascent setting value.";
                    classic.turnEndAltitude.val = (number * 1000);
                    return null;
                case "classicTurnEndAngle":
                    if (classic == null) return "MechJeb ascent module is unavailable.";
                    if (!validNumber || number < 0 || number > 90) return "Invalid ascent setting value.";
                    classic.turnEndAngle.val = (number * 1);
                    return null;
                case "classicTurnShapeExponent":
                    if (classic == null) return "MechJeb ascent module is unavailable.";
                    if (!validNumber || number < 0.01 || number > 1000000000) return "Invalid ascent setting value.";
                    classic.turnShapeExponent.val = (number * 1);
                    return null;
                case "pvgPitchStartVelocity":
                    if (pvg == null) return "MechJeb ascent module is unavailable.";
                    if (!validNumber || number < 0 || number > 1000000000) return "Invalid ascent setting value.";
                    pvg.PitchStartVelocity.val = (number * 1);
                    return null;
                case "pvgPitchRate":
                    if (pvg == null) return "MechJeb ascent module is unavailable.";
                    if (!validNumber || number < 0 || number > 1000000000) return "Invalid ascent setting value.";
                    pvg.PitchRate.val = (number * 1);
                    return null;
                case "pvgDesiredApoapsisKm":
                    if (pvg == null) return "MechJeb ascent module is unavailable.";
                    if (number * 1000 < ascent.desiredOrbitAltitude.val) return "PVG apoapsis must be at or above its periapsis.";
                    if (!validNumber || number < 0 || number > 1000000) return "Invalid ascent setting value.";
                    pvg.DesiredApoapsis.val = (number * 1000);
                    return null;
                case "pvgAttachAltitudeEnabled":
                    if (pvg == null) return "MechJeb ascent module is unavailable.";
                    if (!validBool) return "Invalid ascent setting value.";
                    pvg.AttachAltFlag = flag;
                    return null;
                case "pvgDesiredAttachAltitudeKm":
                    if (pvg == null) return "MechJeb ascent module is unavailable.";
                    if (!validNumber || number < 0 || number > 1000000) return "Invalid ascent setting value.";
                    pvg.DesiredAttachAlt.val = (number * 1000);
                    return null;
                case "pvgDynamicPressureTriggerKpa":
                    if (pvg == null) return "MechJeb ascent module is unavailable.";
                    if (!validNumber || number < 0 || number > 1000000000) return "Invalid ascent setting value.";
                    pvg.DynamicPressureTrigger.val = (number * 1000);
                    return null;
                case "pvgStagingTriggerEnabled":
                    if (pvg == null) return "MechJeb ascent module is unavailable.";
                    if (!validBool) return "Invalid ascent setting value.";
                    pvg.StagingTriggerFlag = flag;
                    return null;
                case "pvgStagingTrigger":
                    if (pvg == null) return "MechJeb ascent module is unavailable.";
                    if (!validNumber || number < 0 || number > 99 || number != Math.Truncate(number)) return "Invalid ascent setting value.";
                    pvg.StagingTrigger.val = (int)(number * 1);
                    return null;
                case "pvgFixedCoast":
                    if (pvg == null) return "MechJeb ascent module is unavailable.";
                    if (!validBool) return "Invalid ascent setting value.";
                    pvg.FixedCoast = flag;
                    return null;
                case "pvgFixedCoastLength":
                    if (pvg == null) return "MechJeb ascent module is unavailable.";
                    if (!validNumber || number < 0 || number > 1000000000) return "Invalid ascent setting value.";
                    pvg.FixedCoastLength.val = (number * 1);
                    return null;
                default: return "Unknown ascent setting.";
            }
        }
    }
}
