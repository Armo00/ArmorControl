using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace ArmorOverhaul.ArmorControl.Core
{
    internal static class OrbitGeometry
    {
        private static string vesselKey, cached;
        private static float nextCapture;
        private static double lastUt, range;
        private static Vector3d lastDirection;
        private static readonly List<Vector3d> flown = new List<Vector3d>();

        internal static string Capture(Vessel vessel)
        {
            if (vessel == null || vessel.mainBody == null) { vesselKey = null; return null; }
            double now = Planetarium.GetUniversalTime();
            string key = vessel.id + ":" + vessel.mainBody.flightGlobalsIndex;
            if (key != vesselKey || now < lastUt)
            { vesselKey = key; flown.Clear(); range = 0; cached = null; nextCapture = 0; lastDirection = Vector3d.zero; }
            if (Time.realtimeSinceStartup < nextCapture) return cached;
            nextCapture = Time.realtimeSinceStartup + 1f;
            lastUt = now;
            CelestialBody body = vessel.mainBody;
            Vector3d direction = (Vector3d)body.transform.InverseTransformDirection((Vector3)(vessel.CoM - body.position));
            direction.Normalize();
            if (lastDirection.sqrMagnitude > 0)
                range += Math.Atan2(Vector3d.Cross(lastDirection, direction).magnitude,
                    Vector3d.Dot(lastDirection, direction)) * body.Radius;
            lastDirection = direction;
            if (flown.Count == 0 || now > flown[flown.Count - 1].z)
                flown.Add(new Vector3d(range, vessel.altitude, now));
            if (flown.Count > 1200) flown.RemoveAt(0);
            var json = new StringBuilder(24000);
            json.Append("{\"radius\":").Append(Number(body.Radius));
            json.Append(",\"current\":").Append(Points(Sample(vessel.orbit, now)));
            var nodes = vessel.patchedConicSolver == null ? null : vessel.patchedConicSolver.maneuverNodes;
            Orbit after = nodes != null && nodes.Count > 0 ? nodes[0].nextPatch : null;
            bool sameBody = after != null && after.referenceBody == body;
            json.Append(",\"planned\":").Append(Points(sameBody ? Sample(after, nodes[0].UT) : new List<Vector3d>()));
            json.Append(",\"node\":").Append(nodes != null && nodes.Count > 0
                ? Point(vessel.orbit.getPositionAtUT(nodes[0].UT) - body.position) : "null");
            json.Append(",\"craft\":").Append(Point(vessel.CoM - body.position));
            json.Append(",\"flown\":").Append(Points(flown));
            json.Append(",\"ut\":").Append(Number(now));
            json.Append(",\"plannedAvailable\":").Append(sameBody ? "true" : "false");
            return cached = json.Append('}').ToString();
        }

        internal static List<Vector3d> Sample(Orbit orbit, double start, double end = double.NaN)
        {
            var points = new List<Vector3d>();
            if (orbit == null || orbit.referenceBody == null) return points;
            double duration = orbit.eccentricity < 1 && Finite(orbit.period) ? orbit.period : 21600;
            if (!Finite(end)) end = start + duration;
            if (Finite(orbit.EndUT) && orbit.EndUT > start) end = Math.Min(end, orbit.EndUT);
            if (!Finite(end) || end <= start) return points;
            for (int i = 0; i <= 192; i++)
            {
                Vector3d p = orbit.getPositionAtUT(start + (end - start) * i / 192d) - orbit.referenceBody.position;
                if (!Finite(p.x) || !Finite(p.y) || !Finite(p.z)) break;
                if (p.magnitude < orbit.referenceBody.Radius * .999) break;
                points.Add(p);
            }
            return points;
        }

        internal static string Points(IEnumerable<Vector3d> points)
        { var json = new StringBuilder("["); foreach (Vector3d point in points) { if (json.Length > 1) json.Append(','); json.Append(Point(point)); } return json.Append(']').ToString(); }
        private static string Point(Vector3d point) { return "[" + Number(point.x) + "," + Number(point.y) + "," + Number(point.z) + "]"; }
        private static string Number(double value) { return Finite(value) ? value.ToString("G12", CultureInfo.InvariantCulture) : "0"; }
        private static bool Finite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }
    }
}
