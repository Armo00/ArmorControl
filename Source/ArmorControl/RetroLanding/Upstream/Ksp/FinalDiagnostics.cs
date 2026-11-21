using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using LandingPoc;
using UnityEngine;

namespace KspRetroLanding
{
    public sealed partial class KspLanding
    {
        private StreamWriter finalTrace;
        private bool finalTraceFailed;
        private double lastFinalTraceUt;
        private Vector3d lastTracedAim;

        [Serializable]
        private sealed class FinalSample
        {
            public string save, vesselId, phase, mode, reason;
            public long unixMs;
            public double ut, clearanceM, surfaceSpeedMps, horizontalSpeedMps, verticalSpeedMps;
            public double timeToContactS;
            public double horizontalToDownSpeedRatio, handoffHorizontalRatioTarget;
            public bool descending;
            public double bodyTiltDeg, rawTiltDeg, targetTiltDeg, aimErrorDeg, rollTargetDeg, rollErrorDeg;
            public double tiltLimitDeg, aimRateLimitDegS, bodyRateLimitDegS, rollRateLimitDegS, aimSlewDegS;
            public double requestedLateralMps2, actualLateralMps2, requestedUpMps2, actualUpMps2;
            public double thrustKn, tvcDeflectionDeg, tvcLimitDeg;
            public int groundedWheels, enginesFiring;
            public float mainThrottle, bankThrottle;
            public bool attitudeLimited, tvcLimited, tvcMeasured, contact;
            public Vector3 bodyAxis, rawAim, limitedAim, bodyDorsal, targetDorsal, velocity, thrustVector;
            public Vector3 bodyRateDegS, aimRateDegS, controlInput, commandedControlInput;
        }

        [Serializable]
        private sealed class EngineSample
        {
            public string part;
            public long flightId;
            public int moduleIndex, bit;
            public bool selected, starting, running, armed, limited;
            public float thrustKn, throttlePercent;
        }

        [Serializable]
        private sealed class GimbalSample
        {
            public int moduleIndex, pivot;
            public bool limited, locked, hasActuation, hasDeflection, usesResponseSpeed;
            public float xDeg, yDeg, deflectionDeg, limitDeg, responseSpeed, limiterPercent;
            public float xpLimitDeg, xnLimitDeg, ypLimitDeg, ynLimitDeg;
            public Vector3 moduleDemand;
        }

        private void WriteFinalDiagnostics(double now, Flight flight, Command command, GroundReading ground,
            int supports, Vector3d velocity, Vector3d thrustVector, Vector3d rawPointing, Vector3d pointing,
            Vector3d rollForward, double rollError, bool steeringLimited, double demand, Vector3 aimRate)
        {
            if (finalTraceFailed) return;
            try
            {
                if (finalTrace == null)
                {
                    string name = "flight-final-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff", Invariant) +
                        "-" + Guid.NewGuid().ToString("N") + ".jsonl";
                    finalTrace = File.CreateText(Path.Combine(RecoveryLogDirectory, name));
                    finalTrace.AutoFlush = true;
                }

                Vector3d up = (vessel.CoMD - vessel.mainBody.position).normalized;
                Vector3d north = WorldDirection(LocalNorth(up));
                Vector3d east = Vector3d.Cross(north, up);
                Rigidbody rb = vessel.rootPart.rb;
                FlightCtrlState applied = vessel.ctrlState;
                double dt = now - lastFinalTraceUt;
                double timeToContact = Controller.FinalTimeToContact(flight, cfg);
                var sample = new FinalSample
                {
                    save = HighLogic.SaveFolder, vesselId = vessel.id.ToString("D"),
                    phase = command.Phase.ToString(), mode = mode.ToString(), reason = command.Reason, ut = now,
                    unixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    clearanceM = ground.Clearance, surfaceSpeedMps = velocity.magnitude,
                    timeToContactS = timeToContact,
                    horizontalSpeedMps = (velocity - flight.Vz * up).magnitude, verticalSpeedMps = flight.Vz,
                    descending = flight.Vz < 0,
                    horizontalToDownSpeedRatio = flight.Vz < 0 ? (velocity - flight.Vz * up).magnitude / -flight.Vz : 0,
                    handoffHorizontalRatioTarget = Controller.MainHandoffHorizontalRatio,
                    bodyTiltDeg = Vector3.Angle(frame.up, (Vector3)up),
                    rawTiltDeg = Vector3.Angle((Vector3)rawPointing, (Vector3)up),
                    targetTiltDeg = Vector3.Angle((Vector3)pointing, (Vector3)up),
                    aimErrorDeg = Vector3.Angle(frame.up, (Vector3)pointing),
                    rollTargetDeg = rollDegrees, rollErrorDeg = rollError / AttitudeGuidance.Degree,
                    attitudeLimited = steeringLimited,
                    tiltLimitDeg = steeringLimited ? AttitudeGuidance.FinalTiltDegrees(demand, timeToContact) : 0,
                    aimRateLimitDegS = steeringLimited ? AttitudeGuidance.FinalAimRateDegrees(demand) : 0,
                    bodyRateLimitDegS = steeringLimited ? AttitudeGuidance.FinalBodyRateDegrees(demand) : 0,
                    rollRateLimitDegS = steeringLimited ? AttitudeGuidance.FinalRollRate / AttitudeGuidance.Degree : 0,
                    aimSlewDegS = dt > 0 && dt <= 2 * cfg.SampleSeconds && lastTracedAim.sqrMagnitude > 0
                        ? Vector3.Angle((Vector3)lastTracedAim, (Vector3)pointing) / dt : 0,
                    requestedLateralMps2 = command.X, actualLateralMps2 = flight.LateralThrustAcceleration,
                    requestedUpMps2 = command.Up, actualUpMps2 = flight.VerticalThrustAcceleration,
                    tvcLimited = finalGimbals.Count > 0, tvcLimitDeg = finalGimbalLimit,
                    groundedWheels = supports, contact = flight.Contact,
                    mainThrottle = applied.mainThrottle, bankThrottle = throttle,
                    controlInput = new Vector3(applied.pitch, applied.roll, applied.yaw),
                    aimRateDegS = Mathf.Rad2Deg * frame.InverseTransformDirection(aimRate),
                    commandedControlInput = mode == Mode.Control && command.Phase != Phase.Landed
                        ? rotationInput : Vector3.zero,
                    bodyAxis = Project(frame.up, east, north, up), bodyDorsal = Project(-frame.forward, east, north, up),
                    rawAim = Project(rawPointing, east, north, up), limitedAim = Project(pointing, east, north, up),
                    targetDorsal = Project(rollForward, east, north, up),
                    velocity = Project(velocity, east, north, up), thrustVector = Project(thrustVector, east, north, up),
                    bodyRateDegS = rb == null ? Vector3.zero :
                        Mathf.Rad2Deg * frame.InverseTransformDirection(rb.angularVelocity)
                };
                // KSP's JsonUtility can omit nested managed arrays. Serialize
                // each object independently and assemble the two lists here.
                var engineJson = new StringBuilder();
                for (int i = 0; i < engines.Count; i++)
                {
                    EngineBinding binding = engines[i];
                    ModuleEngines engine = binding.Engine;
                    long bit = 1L << i;
                    bool selected = (command.Engines & bit) != 0;
                    bool running = (flight.Running & bit) != 0;
                    bool starting = !running && (binding.Issued || (flight.Starting & bit) != 0);
                    if (!selected && !starting && !running && engine.finalThrust <= 0) continue;
                    var reading = new EngineSample
                    {
                        part = engine.part.name, flightId = engine.part.flightID,
                        moduleIndex = engine.part.Modules.IndexOf(engine), bit = i, selected = selected,
                        running = running, starting = starting, armed = engine.EngineIgnited,
                        thrustKn = engine.finalThrust, throttlePercent = engine.independentThrottle
                            ? engine.independentThrottlePercentage : 100 * applied.mainThrottle
                    };
                    sample.thrustKn += reading.thrustKn;
                    if (reading.thrustKn > 0) sample.enginesFiring++;
                    GimbalSample[] gimbals = ReadGimbalDiagnostics(binding, sample, reading);
                    if (engineJson.Length > 0) engineJson.Append(',');
                    string engineObject = JsonUtility.ToJson(reading);
                    engineJson.Append(engineObject, 0, engineObject.Length - 1).Append(",\"gimbals\":[");
                    for (int g = 0; g < gimbals.Length; g++)
                    {
                        if (g > 0) engineJson.Append(',');
                        engineJson.Append(JsonUtility.ToJson(gimbals[g]));
                    }
                    engineJson.Append("]}");
                }
                string frameObject = JsonUtility.ToJson(sample);
                finalTrace.WriteLine(frameObject.Substring(0, frameObject.Length - 1) + ",\"engines\":[" + engineJson + "]}");
                lastFinalTraceUt = now;
                lastTracedAim = pointing;
            }
            catch (Exception ex)
            {
                CloseFinalDiagnostics();
                finalTraceFailed = true;
                Debug.LogWarning("[RetroLanding] Final telemetry unavailable: " + ex.Message);
            }
        }

        private GimbalSample[] ReadGimbalDiagnostics(EngineBinding engine, FinalSample sample, EngineSample reading)
        {
            var result = new List<GimbalSample>();
            foreach (PartModule module in torqueModules)
            {
                var gimbal = module as ModuleGimbal;
                if (gimbal == null || gimbal.part != engine.Engine.part || gimbal.gimbalTransforms == null) continue;
                bool limited = finalGimbals.ContainsKey(gimbal);
                float limiter = gimbal.gimbalLimiter / 100;
                for (int i = 0; i < gimbal.gimbalTransforms.Count; i++)
                {
                    Transform pivot = gimbal.gimbalTransforms[i];
                    if (!engine.UsesGimbal(pivot)) continue;
                    reading.limited |= limited;
                    bool hasDeflection = gimbal.initRots != null && i < gimbal.initRots.Count;
                    bool hasActuation = gimbal.oldActuationLocal != null && i < gimbal.oldActuationLocal.Length;
                    Vector3 axes = hasActuation ? gimbal.oldActuationLocal[i] : Vector3.zero;
                    float angle = hasDeflection ? Quaternion.Angle(gimbal.initRots[i], pivot.localRotation) : 0;
                    if (limited && hasDeflection)
                    {
                        sample.tvcMeasured = true;
                        sample.tvcDeflectionDeg = Math.Max(sample.tvcDeflectionDeg, angle);
                    }
                    result.Add(new GimbalSample
                    {
                        moduleIndex = gimbal.part.Modules.IndexOf(gimbal), pivot = i,
                        limited = limited, locked = gimbal.gimbalLock, hasActuation = hasActuation,
                        hasDeflection = hasDeflection, xDeg = axes.x, yDeg = axes.y, deflectionDeg = angle,
                        limitDeg = FinalGimbalSettings.Envelope(gimbal.gimbalRangeXP, gimbal.gimbalRangeXN,
                            gimbal.gimbalRangeYP, gimbal.gimbalRangeYN, gimbal.gimbalRange) * limiter,
                        xpLimitDeg = EffectiveRange(gimbal.gimbalRangeXP, gimbal.gimbalRange) * limiter,
                        xnLimitDeg = EffectiveRange(gimbal.gimbalRangeXN, gimbal.gimbalRange) * limiter,
                        ypLimitDeg = EffectiveRange(gimbal.gimbalRangeYP, gimbal.gimbalRange) * limiter,
                        ynLimitDeg = EffectiveRange(gimbal.gimbalRangeYN, gimbal.gimbalRange) * limiter,
                        usesResponseSpeed = gimbal.useGimbalResponseSpeed,
                        responseSpeed = gimbal.gimbalResponseSpeed, limiterPercent = gimbal.gimbalLimiter,
                        moduleDemand = gimbal.actuation
                    });
                }
            }
            return result.ToArray();
        }

        private static float EffectiveRange(float value, float fallback) => Math.Max(0, value < 0 ? fallback : value);

        private static Vector3 Project(Vector3d value, Vector3d east, Vector3d north, Vector3d up)
        {
            return new Vector3((float)Vector3d.Dot(value, east), (float)Vector3d.Dot(value, north),
                (float)Vector3d.Dot(value, up));
        }

        private static string RecoveryLogDirectory => Path.Combine(KSPUtil.ApplicationRootPath, "Logs", "ArmorControl", "Recovery");

        private void CloseFinalDiagnostics()
        {
            try { if (finalTrace != null) finalTrace.Dispose(); }
            catch (IOException ex) { Debug.LogWarning("[RetroLanding] Closing Final telemetry: " + ex.Message); }
            finalTrace = null;
            finalTraceFailed = false;
            lastTracedAim = Vector3d.zero;
        }
    }
}
