using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using ArmorOverhaul.ArmorControl.Protocol;
using UnityEngine;

namespace ArmorOverhaul.ArmorControl.Core
{
    internal static class TrajectoriesBridge
    {
        private const BindingFlags StaticFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private static Assembly assembly;
        private static Type apiType;
        private static Type settingsType;
        private static Type trajectoryType;
        private static Type scenarioType;
        private static float nextBindingAttempt;
        private static float nextApiAttempt;
        private static bool fatalFailure;
        private static string lastFailure;

        internal static TrajectoryPredictionSnapshot Capture(Vessel vessel)
        {
            var snapshot = new TrajectoryPredictionSnapshot { Status = "Trajectories is not loaded." };
            if (!EnsureReady(out snapshot.Status)) return snapshot;
            snapshot.Available = true;
            try
            {
                snapshot.Version = Convert.ToString(GetApi("GetVersion"));
                // Optional visualization must never disable the settings/impact telemetry.
                try { snapshot.GroundPathsJson = CapturePaths(vessel); }
                catch (Exception error) { snapshot.GroundPathsJson = "[]"; snapshot.PathStatus = "轨迹读取失败：" + error.GetBaseException().Message; }
                snapshot.DisplayTrajectories = GetSetting<bool>("DisplayTrajectories");
                snapshot.DisplayTrajectoriesInFlight = GetSetting<bool>("DisplayTrajectoriesInFlight");
                snapshot.AlwaysUpdate = GetSetting<bool>("AlwaysUpdate");
                snapshot.DisplayCompleteTrajectory = GetSetting<bool>("DisplayCompleteTrajectory");
                snapshot.BodyFixedMode = GetSetting<bool>("BodyFixedMode");
                snapshot.AutoUpdateAerodynamicModel = GetSetting<bool>("AutoUpdateAeroDynamicModel");
                snapshot.UseCache = GetSetting<bool>("UseCache");
                snapshot.DefaultDescentIsRetrograde = GetSetting<bool>("DefaultDescentIsRetro");
                snapshot.IntegrationStepSize = GetSetting<double>("IntegrationStepSize");
                snapshot.MaximumPatchCount = GetSetting<int>("MaxPatchCount");
                snapshot.MaximumFramesPerPatch = GetSetting<int>("MaxFramesPerPatch");
                snapshot.AerodynamicModel = Convert.ToString(GetStatic(trajectoryType, "AerodynamicModelName"));
                // Trajectories 2.4.5.4 exposes the same millisecond value its GUI prints as "ms".
                snapshot.ComputationTimeMilliseconds = Convert.ToDouble(GetStatic(trajectoryType, "ComputationTime") ?? 0d);
                snapshot.ErrorCount = Convert.ToInt32(GetStatic(trajectoryType, "ErrorCount") ?? 0);
                snapshot.MaximumDecelerationG = Convert.ToDouble(GetStatic(trajectoryType, "MaxAccel") ?? 0f) / 9.80665d;

                var patches = (GetStatic(trajectoryType, "Patches") as IEnumerable)?.Cast<object>().ToArray();
                object impactPatch = patches?.LastOrDefault();
                var body = PathMember(PathMember(impactPatch, "StartingState"), "ReferenceBody") as CelestialBody;
                object impact = PathMember(impactPatch, "ImpactPosition");
                object velocityValue = PathMember(impactPatch, "ImpactVelocity");
                snapshot.SurfaceMapVersion = 1;
                snapshot.BodyRadius = body != null ? body.Radius : vessel?.mainBody?.Radius ?? 0;
                if (body != null && impact is Vector3d && velocityValue is Vector3d) {
                    Vector3d relative = (Vector3d)impact;
                    Vector3d position = relative + body.position;
                    snapshot.ImpactAvailable = true;
                    snapshot.TimeToImpact = Convert.ToDouble(PathMember(impactPatch, "EndTime")) - Planetarium.GetUniversalTime();
                    snapshot.ImpactLatitude = body.GetLatitude(position);
                    snapshot.ImpactLongitude = body.GetLongitude(position);
                    snapshot.ImpactAltitude = body.GetAltitude(position);
                    // Match MainGUI.UpdateInfoPage, including downward-positive vertical speed.
                    Vector3d surfaceVelocity = (Vector3d)velocityValue - body.getRFrmVel(position);
                    double vertical = Vector3d.Dot(surfaceVelocity, relative.normalized);
                    snapshot.ImpactSpeed = surfaceVelocity.magnitude;
                    snapshot.ImpactVerticalSpeed = -vertical;
                    snapshot.ImpactHorizontalSpeed = (surfaceVelocity - relative.normalized * vertical).magnitude;
                }
                object target = InvokeApi("GetTarget");
                var targetType = assembly.GetType("Trajectories.TargetProfile");
                var targetBody = GetStatic(targetType, "Body") as CelestialBody;
                if (target is Vector3d && targetBody != null && targetBody == (body ?? vessel?.mainBody)) {
                    Vector3d coordinates = (Vector3d)target;
                    snapshot.TargetAvailable = true;
                    snapshot.TargetLatitude = coordinates.x;
                    snapshot.TargetLongitude = coordinates.y;
                    snapshot.TargetAltitude = coordinates.z;
                    if (snapshot.ImpactAvailable) {
                        // Use the exact locally installed Trajectories distance implementation.
                        var distance = assembly.GetType("Trajectories.Util").GetMethod("DistanceFromLatitudeAndLongitude", StaticFlags);
                        snapshot.ImpactTargetDistance = Convert.ToDouble(distance.Invoke(null, new object[] {
                            targetBody.Radius + snapshot.ImpactAltitude, snapshot.ImpactLatitude, snapshot.ImpactLongitude,
                            snapshot.TargetLatitude, snapshot.TargetLongitude }));
                    }
                }
                ReadProfile(snapshot);
                snapshot.Status = snapshot.ImpactAvailable ? "Prediction ready." : "Waiting for an impact prediction.";
            }
            catch (Exception exception)
            {
                RecordFailure(exception);
                snapshot.Available = false;
                snapshot.Status = "Trajectories state unavailable: " + lastFailure;
            }
            return snapshot;
        }

        internal static bool ApplySettings(string rawJson, out string message)
        {
            message = null;
            if (!EnsureReady(out message)) return false;
            try
            {
                SetSetting("DisplayTrajectories", WireProtocol.ReadBool(rawJson, "display", GetSetting<bool>("DisplayTrajectories")));
                SetSetting("DisplayTrajectoriesInFlight", WireProtocol.ReadBool(rawJson, "displayInFlight", GetSetting<bool>("DisplayTrajectoriesInFlight")));
                SetSetting("AlwaysUpdate", WireProtocol.ReadBool(rawJson, "alwaysUpdate", GetSetting<bool>("AlwaysUpdate")));
                SetSetting("DisplayCompleteTrajectory", WireProtocol.ReadBool(rawJson, "complete", GetSetting<bool>("DisplayCompleteTrajectory")));
                SetSetting("BodyFixedMode", WireProtocol.ReadBool(rawJson, "bodyFixed", GetSetting<bool>("BodyFixedMode")));
                SetSetting("AutoUpdateAeroDynamicModel", WireProtocol.ReadBool(rawJson, "autoUpdateAero", GetSetting<bool>("AutoUpdateAeroDynamicModel")));
                SetSetting("UseCache", WireProtocol.ReadBool(rawJson, "useCache", GetSetting<bool>("UseCache")));
                SetSetting("DefaultDescentIsRetro", WireProtocol.ReadBool(rawJson, "defaultRetrograde", GetSetting<bool>("DefaultDescentIsRetro")));
                SetSetting("IntegrationStepSize", Clamp(WireProtocol.ReadDouble(rawJson, "integrationStep", GetSetting<double>("IntegrationStepSize")), 0.05, 10));
                SetSetting("MaxPatchCount", (int)Clamp(WireProtocol.ReadDouble(rawJson, "maxPatches", GetSetting<int>("MaxPatchCount")), 1, 16));
                SetSetting("MaxFramesPerPatch", (int)Clamp(WireProtocol.ReadDouble(rawJson, "maxFramesPerPatch", GetSetting<int>("MaxFramesPerPatch")), 1, 120));
                InvokeStatic(settingsType, "Save");
                if (WireProtocol.ReadBool(rawJson, "updateNow", false)) InvokeApi("UpdateTrajectory");
                message = "Trajectories settings applied.";
                return true;
            }
            catch (Exception exception) { RecordFailure(exception); message = lastFailure; return false; }
        }

        internal static bool ApplyProfile(string rawJson, out string message)
        {
            message = null;
            if (!EnsureReady(out message)) return false;
            try
            {
                var angles = new double[4];
                angles[0] = Clamp(WireProtocol.ReadDouble(rawJson, "entryAngle", 0), -180, 180) * Math.PI / 180d;
                angles[1] = Clamp(WireProtocol.ReadDouble(rawJson, "highAngle", 0), -180, 180) * Math.PI / 180d;
                angles[2] = Clamp(WireProtocol.ReadDouble(rawJson, "lowAngle", 0), -180, 180) * Math.PI / 180d;
                angles[3] = Clamp(WireProtocol.ReadDouble(rawJson, "finalAngle", 0), -180, 180) * Math.PI / 180d;
                string[] modes = {
                    NormalizeMode(WireProtocol.ReadString(rawJson, "entryMode")),
                    NormalizeMode(WireProtocol.ReadString(rawJson, "highMode")),
                    NormalizeMode(WireProtocol.ReadString(rawJson, "lowMode")),
                    NormalizeMode(WireProtocol.ReadString(rawJson, "finalMode"))
                };
                IList angleList = (IList)GetApi("DescentProfileAngles");
                IList modeList = (IList)GetApi("DescentProfileModes");
                IList gradeList = (IList)GetApi("DescentProfileGrades");
                for (int index = 0; index < 4; index++)
                {
                    angleList[index] = angles[index];
                    modeList[index] = modes[index] != "HORIZON";
                }
                gradeList[0] = WireProtocol.ReadBool(rawJson, "entryRetrograde", Convert.ToBoolean(gradeList[0]));
                gradeList[1] = WireProtocol.ReadBool(rawJson, "highRetrograde", Convert.ToBoolean(gradeList[1]));
                gradeList[2] = WireProtocol.ReadBool(rawJson, "lowRetrograde", Convert.ToBoolean(gradeList[2]));
                gradeList[3] = WireProtocol.ReadBool(rawJson, "finalRetrograde", Convert.ToBoolean(gradeList[3]));
                SetApi("DescentProfileAngles", angleList);
                SetApi("DescentProfileModes", modeList);
                SetApi("DescentProfileGrades", gradeList);
                InvokeApi("UpdateTrajectory");
                message = "Trajectories descent profile applied.";
                return true;
            }
            catch (Exception exception) { RecordFailure(exception); message = lastFailure; return false; }
        }

        internal static bool SetTarget(string rawJson, out string message)
        {
            message = null;
            if (!EnsureReady(out message)) return false;
            try
            {
                double latitude = Clamp(WireProtocol.ReadDouble(rawJson, "latitude", 0), -90, 90);
                double longitude = Clamp(WireProtocol.ReadDouble(rawJson, "longitude", 0), -180, 180);
                double altitude = WireProtocol.ReadDouble(rawJson, "altitude", 0);
                apiType.GetMethod("SetTarget", StaticFlags).Invoke(null, new object[] { latitude, longitude, (double?)altitude });
                message = "Trajectories target set.";
                return true;
            }
            catch (Exception exception) { RecordFailure(exception); message = lastFailure; return false; }
        }

        internal static bool ClearTarget(out string message)
        {
            message = null;
            if (!EnsureReady(out message)) return false;
            try { InvokeApi("ClearTarget"); message = "Trajectories target cleared."; return true; }
            catch (Exception exception) { RecordFailure(exception); message = lastFailure; return false; }
        }

        internal static bool UpdateNow(out string message)
        {
            message = null;
            if (!EnsureReady(out message)) return false;
            try { InvokeApi("UpdateTrajectory"); message = "Trajectories update requested."; return true; }
            catch (Exception exception) { RecordFailure(exception); message = lastFailure; return false; }
        }

        private static void ReadProfile(TrajectoryPredictionSnapshot snapshot)
        {
            IList angles = GetApi("DescentProfileAngles") as IList;
            IList modes = GetApi("DescentProfileModes") as IList;
            IList grades = GetApi("DescentProfileGrades") as IList;
            if (angles == null || modes == null || grades == null || angles.Count < 4) return;
            snapshot.EntryAngle = Convert.ToDouble(angles[0]) * 180d / Math.PI; snapshot.EntryMode = ProfileMode(modes, 0); snapshot.EntryRetrograde = Convert.ToBoolean(grades[0]);
            snapshot.HighAltitudeAngle = Convert.ToDouble(angles[1]) * 180d / Math.PI; snapshot.HighAltitudeMode = ProfileMode(modes, 1); snapshot.HighAltitudeRetrograde = Convert.ToBoolean(grades[1]);
            snapshot.LowAltitudeAngle = Convert.ToDouble(angles[2]) * 180d / Math.PI; snapshot.LowAltitudeMode = ProfileMode(modes, 2); snapshot.LowAltitudeRetrograde = Convert.ToBoolean(grades[2]);
            snapshot.FinalApproachAngle = Convert.ToDouble(angles[3]) * 180d / Math.PI; snapshot.FinalApproachMode = ProfileMode(modes, 3); snapshot.FinalApproachRetrograde = Convert.ToBoolean(grades[3]);
        }

        private static string ProfileMode(IList modes, int index)
        {
            return Convert.ToBoolean(modes[index]) ? "VELOCITY" : "HORIZON";
        }

        private static string NormalizeMode(string value)
        {
            string mode = (value ?? "VELOCITY").Trim().ToUpperInvariant();
            return mode == "HORIZON" ? "HORIZON" : "VELOCITY";
        }

        private static bool EnsureBinding()
        {
            if (apiType != null && settingsType != null && trajectoryType != null && scenarioType != null) return true;
            if (Time.realtimeSinceStartup < nextBindingAttempt) return false;
            nextBindingAttempt = Time.realtimeSinceStartup + 5f;
            assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(candidate =>
                string.Equals(candidate.GetName().Name, "Trajectories", StringComparison.OrdinalIgnoreCase));
            if (assembly == null) return false;
            apiType = assembly.GetType("Trajectories.API", false);
            settingsType = assembly.GetType("Trajectories.Settings", false);
            trajectoryType = assembly.GetType("Trajectories.Trajectory", false);
            scenarioType = assembly.GetType("Trajectories.Trajectories", false);
            return apiType != null && settingsType != null && trajectoryType != null && scenarioType != null;
        }

        private static string pathCache;
        private static Guid pathVessel;
        private static float nextPathCapture;

        private static object PathMember(object value, string name)
        {
            return TrajectoryMemberAccess.Read(value, name);
        }

        private static string CapturePaths(Vessel vessel)
        {
            if (vessel == null) return "[]";
            if (pathVessel == vessel.id && Time.realtimeSinceStartup < nextPathCapture) return pathCache;
            pathVessel = vessel.id;
            nextPathCapture = Time.realtimeSinceStartup + 1f;
            var json = new System.Text.StringBuilder("[");
            IEnumerable patches = GetStatic(trajectoryType, "Patches") as IEnumerable;
            if (patches != null) foreach (object patch in patches)
            {
                object start = PathMember(patch, "StartingState");
                if (!ReferenceEquals(PathMember(start, "ReferenceBody"), vessel.mainBody)) break;
                var points = new System.Collections.Generic.List<Vector3d>();
                Array atmosphere = PathMember(patch, "AtmosphericTrajectory") as Array;
                if (atmosphere != null && atmosphere.Length > 0)
                {
                    int stride = Math.Max(1, (int)Math.Ceiling(atmosphere.Length / 256d));
                    for (int i = 0; i < atmosphere.Length; i += stride)
                        AddGroundPoint(points, atmosphere.GetValue(i), vessel.mainBody);
                    AddGroundPoint(points, atmosphere.GetValue(atmosphere.Length - 1), vessel.mainBody);
                }
                else {
                    var orbit = PathMember(patch, "SpaceOrbit") as Orbit;
                    double begin = Math.Max(Planetarium.GetUniversalTime(), Convert.ToDouble(PathMember(start, "Time")));
                    double end = Convert.ToDouble(PathMember(patch, "EndTime"));
                    if (orbit != null && end > begin && !double.IsInfinity(end)) for (int i=0;i<=192;i++) {
                        double time = begin + (end-begin)*i/192d;
                        points.Add(GroundPoint(vessel.mainBody, orbit.getPositionAtUT(time)-vessel.mainBody.position, time, false));
                    }
                }
                if (PathMember(patch, "ImpactPosition") is Vector3d)
                    points.Add(GroundPoint(vessel.mainBody, (Vector3d)PathMember(patch,"ImpactPosition"), 0, true));
                if (points.Count == 0) continue;
                if (json.Length > 1) json.Append(',');
                json.Append(OrbitGeometry.Points(points));
            }
            return pathCache = json.Append(']').ToString();
        }

        private static void AddGroundPoint(System.Collections.Generic.List<Vector3d> points, object point, CelestialBody body)
        {
            if (PathMember(point, "pos") is Vector3d)
                points.Add(GroundPoint(body, (Vector3d)PathMember(point,"pos"), Convert.ToDouble(PathMember(point,"time")), GetSetting<bool>("BodyFixedMode")));
        }

        private static Vector3d GroundPoint(CelestialBody body, Vector3d relative, double time, bool alreadyBodyFixed)
        {
            if (!alreadyBodyFixed) relative = (Vector3d)trajectoryType.GetMethod("CalculateRotatedPosition", StaticFlags)
                .Invoke(null, new object[] { body, relative, time });
            Vector3d world = relative + body.position;
            return new Vector3d(body.GetLatitude(world), body.GetLongitude(world), body.GetAltitude(world));
        }

        private static bool EnsureReady(out string status)
        {
            status = "Trajectories is not loaded.";
            if (fatalFailure)
            {
                status = "Trajectories disabled for this KSP session: " + lastFailure;
                return false;
            }
            if (!EnsureBinding()) return false;
            if (Time.realtimeSinceStartup < nextApiAttempt)
            {
                status = "Trajectories temporarily unavailable: " + lastFailure;
                return false;
            }

            // Merely resolving the API type is not enough. Calling any API member initializes
            // Trajectories.Trajectories; doing that before KSP has created its ScenarioModule
            // makes the plugin's static logger null and permanently poisons the type initializer.
            // An existing ScenarioModule proves that KSP initialized the plugin in the right order.
            if (UnityEngine.Object.FindObjectOfType(scenarioType) == null)
            {
                status = "Waiting for the Trajectories scenario module.";
                return false;
            }
            return true;
        }

        private static void RecordFailure(Exception exception)
        {
            lastFailure = RootMessage(exception);
            nextApiAttempt = Time.realtimeSinceStartup + 5f;
            for (Exception current = exception; current != null; current = current.InnerException)
            {
                if (current is TypeInitializationException)
                {
                    fatalFailure = true;
                    break;
                }
            }
        }

        private static T GetSetting<T>(string name) { return (T)Convert.ChangeType(GetStatic(settingsType, name), typeof(T)); }
        private static void SetSetting(string name, object value) { SetStatic(settingsType, name, value); }
        private static object GetApi(string name) { return GetStatic(apiType, name); }
        private static void SetApi(string name, object value) { SetStatic(apiType, name, value); }
        private static object InvokeApi(string name) { return InvokeStatic(apiType, name); }
        private static object GetStatic(Type type, string name)
        {
            PropertyInfo property = type.GetProperty(name, StaticFlags);
            if (property == null) throw new MissingMemberException(type.FullName, name);
            return property.GetValue(null, null);
        }
        private static void SetStatic(Type type, string name, object value)
        {
            PropertyInfo property = type.GetProperty(name, StaticFlags);
            if (property == null || !property.CanWrite) throw new MissingMemberException(type.FullName, name);
            property.SetValue(null, value, null);
        }
        private static object InvokeStatic(Type type, string name)
        {
            MethodInfo method = type.GetMethod(name, StaticFlags, null, Type.EmptyTypes, null);
            if (method == null) throw new MissingMethodException(type.FullName, name);
            return method.Invoke(null, null);
        }
        private static double Clamp(double value, double minimum, double maximum)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) return minimum;
            return Math.Max(minimum, Math.Min(maximum, value));
        }
        private static string RootMessage(Exception exception)
        {
            while (exception.InnerException != null) exception = exception.InnerException;
            return exception.Message;
        }
    }
}
