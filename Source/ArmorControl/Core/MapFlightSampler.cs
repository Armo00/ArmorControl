using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using ArmorOverhaul.ArmorControl.Protocol;
using ArmorOverhaul.ArmorControl.Rendering;
using UnityEngine;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace ArmorOverhaul.ArmorControl.Core
{
    internal sealed class MapFlightSampler
    {
        private readonly MapTelemetryStore store;
        private readonly PlanetMapProvider maps;
        private readonly List<MapPoint> history = new List<MapPoint>();
        private string context;
        private float nextCapture;
        private double recordedEntry = double.NaN;
        private long sequence;

        internal MapFlightSampler(MapTelemetryStore store) { this.store = store; maps = new PlanetMapProvider(store); }

        internal void Tick()
        {
            if (Time.realtimeSinceStartup < nextCapture) return;
            nextCapture = Time.realtimeSinceStartup + 1;
            // FlightGlobals.ActiveVessel dereferences fetch even outside the flight scene.
            Vessel vessel = HighLogic.LoadedSceneIsFlight && FlightGlobals.fetch != null ? FlightGlobals.ActiveVessel : null;
            string key = vessel != null && vessel.mainBody != null ? vessel.id + ":" + vessel.mainBody.flightGlobalsIndex : null;
            double now = key == null ? 0 : Planetarium.GetUniversalTime();
            if (key != context || history.Count > 0 && now < history[history.Count - 1].Ut)
            { context = key; history.Clear(); recordedEntry = double.NaN; }
            MapWindowRequest[] requests = store.Requests();
            if (key == null)
            {
                foreach (MapWindowRequest request in requests) store.Publish(request.Key,
                    "{\"type\":\"telemetry.map\",\"protocol\":1,\"sequence\":" + (++sequence) + ",\"available\":false,\"status\":\"no_vessel\"}");
                return;
            }
            CelestialBody body = vessel.mainBody;
            var current = new MapPoint(now, vessel.latitude, MapGeometry.WrapLongitude(vessel.longitude), vessel.altitude);
            if (history.Count == 0 || now > history[history.Count - 1].Ut)
            {
                MapPoint previous = history.LastOrDefault();
                if (previous != null && now - previous.Ut > 120)
                { history.Clear(); recordedEntry = double.NaN; previous = null; }
                if (body.atmosphere && previous != null && previous.Altitude >= body.atmosphereDepth && current.Altitude < body.atmosphereDepth)
                    recordedEntry = previous.Ut + (now - previous.Ut) * (previous.Altitude - body.atmosphereDepth) / (previous.Altitude - current.Altitude);
                if (body.atmosphere && current.Altitude > body.atmosphereDepth && vessel.verticalSpeed > 0) recordedEntry = double.NaN;
                history.Add(current);
                while (history.Count > 12000 || history.Count > 1 && now - history[1].Ut > 10800) history.RemoveAt(0);
            }
            if (requests.Length == 0) return;
            long started = Stopwatch.GetTimestamp();
            PlanetMapAsset map = maps.Get(body);
            // A landed/EVA vessel cannot use a prediction. Keep its actual history,
            // basemap and waypoints without scanning Trajectories or probing collisions.
            bool predictFlight = !vessel.LandedOrSplashed && !vessel.isEVA;
            MapPrediction prediction = predictFlight ? TrajectoriesBridge.CaptureMapPrediction(vessel) : new MapPrediction();
            double entry = MapGeometry.Finite(recordedEntry) ? recordedEntry : prediction.EntryUt;
            double impact = prediction.ImpactUt;
            if (predictFlight && !MapGeometry.Finite(entry) && body.atmosphere && vessel.orbit != null && vessel.orbit.PeA < body.atmosphereDepth)
                entry = RadiusIntersection(vessel.orbit, now, body.Radius + body.atmosphereDepth);
            string collisionSource = MapGeometry.Finite(impact) ? "Trajectories" : null;
            if (predictFlight && !body.atmosphere && vessel.orbit != null && vessel.orbit.PeA < 0 && !MapGeometry.Finite(impact))
            {
                try { impact = SurfaceIntersection(vessel.orbit, body, now); collisionSource = body.pqsController != null ? "terrain" : "sphere"; }
                catch { impact = double.NaN; collisionSource = null; }
            }
            var defaults = MapGeometry.Default(body.atmosphere, entry, vessel.orbit == null ? 0 : vessel.orbit.PeA, impact, now);
            bool waypointsAvailable;
            string waypoints = CaptureWaypoints(body, out waypointsAvailable);
            TargetFlight target = CaptureTarget(vessel, body, now);
            foreach (MapWindowRequest request in requests)
            {
                try { store.Publish(request.Key, Capture(vessel, body, now, request, defaults, prediction, map, entry, impact, collisionSource, waypoints, waypointsAvailable, target)); }
                catch { store.Publish(request.Key, "{\"type\":\"telemetry.map\",\"protocol\":1,\"sequence\":" + (++sequence) + ",\"available\":false,\"status\":\"capture_failed\"}"); }
            }
            store.RecordCapture(Stopwatch.GetTimestamp() - started);
        }

        private string Capture(Vessel vessel, CelestialBody body, double now, MapWindowRequest request, MapDefaultWindow defaults,
            MapPrediction prediction, PlanetMapAsset map, double entry, double impact, string collisionSource, string waypoints, bool waypointsAvailable, TargetFlight target)
        {
            double start = request.Automatic ? defaults.Start : now + request.StartOffset;
            double end = start + (request.Automatic ? defaults.Hours : request.Hours) * 3600d;
            var segments = new List<MapSegment>();
            if (start < now) segments.AddRange(MapGeometry.Split(MapGeometry.Window(history, start, Math.Min(end, now)), body.Radius, "history"));
            double future = Math.Max(start, now), predictedEnd = double.NaN;
            bool usePrediction = prediction.Status == "ready" && (prediction.Patches.Any(patch => patch.Atmosphere != null) || MapGeometry.Finite(impact));
            string status = "ready", endReason = null;
            if (vessel.LandedOrSplashed || vessel.isEVA) status = "landed";
            else if (!body.atmosphere && vessel.orbit != null && vessel.orbit.PeA < 0 && !MapGeometry.Finite(impact)) status = "missing_impact";
            else if (usePrediction)
            {
                foreach (MapPredictionPatch patch in prediction.Patches)
                {
                    double begin = Math.Max(future, patch.Start), stop = Math.Min(end, patch.End);
                    if (stop <= begin) continue;
                    List<MapPoint> points = patch.Atmosphere != null ? MapGeometry.Window(patch.Atmosphere, begin, stop)
                        : Sample(patch.Orbit, body, begin, stop, now);
                    segments.AddRange(MapGeometry.Split(points, body.Radius, patch.Atmosphere != null ? "atmosphere" : "orbit"));
                    if (points.Count > 0) predictedEnd = points[points.Count - 1].Ut;
                }
                if (prediction.Patches.Count > 0 && end > prediction.Patches[prediction.Patches.Count - 1].End)
                    endReason = MapGeometry.Finite(impact) ? "impact" : "prediction_end";
            }
            else
            {
                Orbit orbit = vessel.orbit;
                double cursor = future;
                for (int patchIndex = 0; orbit != null && orbit.referenceBody == body && patchIndex < 16 && cursor < end; patchIndex++)
                {
                    double stop = end;
                    bool final = orbit.patchEndTransition == Orbit.PatchTransitionType.FINAL;
                    if (!final && MapGeometry.Finite(orbit.EndUT)) stop = Math.Min(stop, orbit.EndUT);
                    if (MapGeometry.Finite(impact)) stop = Math.Min(stop, impact);
                    if (body.atmosphere && MapGeometry.Finite(entry)) stop = Math.Min(stop, entry);
                    if (stop > cursor)
                    {
                        List<MapPoint> points = Sample(orbit, body, cursor, stop, now);
                        segments.AddRange(MapGeometry.Split(points, body.Radius, "orbit"));
                        if (points.Count > 0) predictedEnd = points[points.Count - 1].Ut;
                    }
                    if (body.atmosphere && MapGeometry.Finite(entry) && stop >= entry) { status = "missing_aero"; endReason = "atmosphere"; break; }
                    if (MapGeometry.Finite(impact) && stop >= impact) { endReason = "impact"; break; }
                    if (final || stop >= end) break;
                    Orbit next = orbit.nextPatch;
                    if (next == null || next.referenceBody != body) { endReason = "soi"; break; }
                    if (orbit.patchEndTransition == Orbit.PatchTransitionType.MANEUVER) { endReason = "maneuver"; break; }
                    cursor = Math.Max(cursor, stop); orbit = next;
                }
                if (body.atmosphere && vessel.altitude <= body.atmosphereDepth && !usePrediction)
                { status = "missing_aero"; segments.RemoveAll(segment => segment.Source != "history"); }
            }
            bool hidden = segments.Count == 0 && MapGeometry.Finite(predictedEnd) && status == "ready";
            var json = new StringBuilder(60000);
            json.Append("{\"type\":\"telemetry.map\",\"protocol\":1,\"sequence\":").Append(++sequence);
            Field(json, "available", true); Field(json, "vesselId", vessel.id.ToString("N")); Field(json, "ut", now);
            Field(json, "requestKey", request.Key);
            Field(json, "status", hidden ? "above_height_limit" : status); Field(json, "predictionStatus", prediction.Status); Field(json, "endReason", endReason);
            Field(json, "start", start); Field(json, "end", end); Field(json, "automatic", request.Automatic); Field(json, "defaultReason", defaults.Reason);
            Field(json, "historyMissing", start < now && (history.Count == 0 || start < history[0].Ut)); Field(json, "predictionEnd", predictedEnd);
            Field(json, "body", body.bodyName); Field(json, "bodyName", body.displayName); Field(json, "radius", body.Radius);
            Field(json, "atmosphere", body.atmosphere); Field(json, "atmosphereDepth", body.atmosphereDepth);
            Field(json, "entryUt", entry); Field(json, "impactUt", impact); Field(json, "impactSource", collisionSource);
            Field(json, "entryTimeUnknown", body.atmosphere && vessel.altitude < body.atmosphereDepth && !MapGeometry.Finite(entry) && !vessel.LandedOrSplashed && !vessel.isEVA);
            Field(json, "apoapsis", vessel.orbit == null || vessel.orbit.eccentricity >= 1 ? double.NaN : vessel.orbit.ApA);
            Field(json, "periapsis", vessel.orbit == null ? double.NaN : vessel.orbit.PeA); Field(json, "inclination", vessel.orbit == null ? double.NaN : vessel.orbit.inclination);
            Field(json, "eccentricity", vessel.orbit == null ? double.NaN : vessel.orbit.eccentricity);
            Field(json, "period", vessel.orbit == null || vessel.orbit.eccentricity >= 1 ? double.NaN : vessel.orbit.period);
            Field(json, "latitude", vessel.latitude); Field(json, "longitude", MapGeometry.WrapLongitude(vessel.longitude)); Field(json, "altitude", vessel.altitude);
            Field(json, "mapKey", map.Key); Field(json, "mapReady", map.Ready); Field(json, "mapSource", map.Source); Field(json, "mapStatus", map.Status);
            Field(json, "mapReadbackMs", map.ReadbackMs); Field(json, "mapEncodeMs", map.EncodeMs);
            Field(json, "waypointsAvailable", waypointsAvailable);
            json.Append(",\"waypoints\":").Append(waypoints);
            json.Append(",\"target\":").Append(TargetJson(target, body, now, start, end));
            json.Append(",\"segments\":");
            MapTargetVessel.AppendSegments(json, segments);
            return json.Append('}').ToString();
        }

        private sealed class TargetFlight
        {
            internal readonly MapTargetVessel Snapshot = new MapTargetVessel();
            internal Orbit Orbit;
            internal double Entry = double.NaN, Impact = double.NaN;
        }

        private static TargetFlight CaptureTarget(Vessel active, CelestialBody body, double now)
        {
            var result = new TargetFlight();
            try
            {
                ITargetable selection = FlightGlobals.fetch == null ? null : FlightGlobals.fetch.VesselTarget;
                Vessel target = selection as Vessel;
                ModuleDockingNode port = selection as ModuleDockingNode;
                if (target == null && port != null && port.part != null) target = port.part.vessel;
                if (target == null || target == active || target.id == active.id) return result;
                MapTargetVessel snapshot = result.Snapshot;
                snapshot.Id = target.id.ToString("N"); snapshot.Name = target.vesselName;
                if (target.mainBody == null) { snapshot.Status = "unavailable"; return result; }
                snapshot.Body = target.mainBody.bodyName; snapshot.BodyName = target.mainBody.displayName;
                if (!ReferenceEquals(target.mainBody, body)) { snapshot.Status = "other_body"; return result; }
                snapshot.OnSurface = target.LandedOrSplashed;
                snapshot.Available = true;
                snapshot.Status = snapshot.OnSurface ? "ground" : "orbit";
                MapPoint position;
                if (snapshot.OnSurface || target.loaded)
                    position = new MapPoint(now, target.latitude, target.longitude, target.altitude);
                else
                    // Packed/unloaded vessel latitude and longitude can be stale. Propagate its real orbit at now.
                    position = GroundPoint(body, target.orbit.getPositionAtUT(now) - body.position, now, now);
                snapshot.Latitude = position.Latitude; snapshot.Longitude = position.Longitude; snapshot.Altitude = position.Altitude;
                if (snapshot.OnSurface) return result;
                result.Orbit = target.orbit;
                if (result.Orbit == null || result.Orbit.referenceBody != body) { snapshot.Status = "unavailable"; return result; }
                if (body.atmosphere && snapshot.Altitude <= body.atmosphereDepth)
                    snapshot.Status = "atmosphere";
                else if (body.atmosphere && result.Orbit.PeA < body.atmosphereDepth)
                    result.Entry = RadiusIntersection(result.Orbit, now, body.Radius + body.atmosphereDepth);
                if (!body.atmosphere && result.Orbit.PeA < 0)
                {
                    result.Impact = SurfaceIntersection(result.Orbit, body, now);
                    if (!MapGeometry.Finite(result.Impact)) snapshot.Status = "missing_impact";
                }
            }
            catch { result.Snapshot.Status = "unavailable"; result.Snapshot.Available = false; result.Orbit = null; }
            return result;
        }

        private static string TargetJson(TargetFlight target, CelestialBody body, double now, double start, double end)
        {
            var snapshot = new MapTargetVessel { Id = target.Snapshot.Id, Name = target.Snapshot.Name,
                Body = target.Snapshot.Body, BodyName = target.Snapshot.BodyName, Status = target.Snapshot.Status,
                Available = target.Snapshot.Available, OnSurface = target.Snapshot.OnSurface,
                Latitude = target.Snapshot.Latitude, Longitude = target.Snapshot.Longitude, Altitude = target.Snapshot.Altitude,
                HistoryMissing = target.Snapshot.Available && !target.Snapshot.OnSurface && start < now };
            var segments = new List<MapSegment>();
            try
            {
                if (snapshot.Available && !snapshot.OnSurface && snapshot.Status == "orbit")
                {
                    Orbit orbit = target.Orbit; double cursor = Math.Max(start, now);
                    for (int i = 0; orbit != null && orbit.referenceBody == body && i < 16 && cursor < end; i++)
                    {
                        double stop = end;
                        bool final = orbit.patchEndTransition == Orbit.PatchTransitionType.FINAL;
                        if (!final && MapGeometry.Finite(orbit.EndUT)) stop = Math.Min(stop, orbit.EndUT);
                        if (MapGeometry.Finite(target.Entry)) stop = Math.Min(stop, target.Entry);
                        if (MapGeometry.Finite(target.Impact)) stop = Math.Min(stop, target.Impact);
                        if (stop > cursor) segments.AddRange(MapGeometry.Split(Sample(orbit, body, cursor, stop, now), body.Radius, "orbit"));
                        if (MapGeometry.Finite(target.Entry) && stop >= target.Entry) { snapshot.EndReason = "atmosphere"; break; }
                        if (MapGeometry.Finite(target.Impact) && stop >= target.Impact) { snapshot.EndReason = "impact"; break; }
                        if (final || stop >= end) break;
                        if (orbit.patchEndTransition == Orbit.PatchTransitionType.MANEUVER) { snapshot.EndReason = "maneuver"; break; }
                        Orbit next = orbit.nextPatch;
                        if (next == null || next.referenceBody != body) { snapshot.EndReason = "soi"; break; }
                        cursor = Math.Max(cursor, stop); orbit = next;
                    }
                }
            }
            catch { snapshot.Status = "unavailable"; segments.Clear(); }
            return snapshot.Json(segments);
        }

        private static string CaptureWaypoints(CelestialBody body, out bool available)
        {
            try
            {
                var points = new List<MapWaypoint>();
                FinePrint.WaypointManager manager = FinePrint.WaypointManager.Instance();
                NavWaypoint navigation = NavWaypoint.fetch;
                if (manager != null)
                foreach (FinePrint.Waypoint point in manager.Waypoints)
                {
                    if (point == null || !point.visible || !ReferenceEquals(point.celestialBody, body)) continue;
                    string id = point.navigationId != Guid.Empty ? point.navigationId.ToString("N")
                        : point.seed.ToString(CultureInfo.InvariantCulture) + ":" + point.index.ToString(CultureInfo.InvariantCulture) + ":" + point.name;
                    points.Add(new MapWaypoint { Id = id, Body = body.bodyName, Name = point.name,
                        Latitude = point.latitude, Longitude = point.longitude, Altitude = point.altitude,
                        Navigating = navigation != null && navigation.IsActive && ReferenceEquals(navigation.Body, body) && navigation.IsUsing(point),
                        OnSurface = point.isOnSurface, Custom = point.isCustom });
                }
                available = true;
                return MapWaypoints.Json(points, body.bodyName);
            }
            catch { available = false; return "[]"; }
        }

        internal static MapPoint GroundPoint(CelestialBody body, Vector3d relative, double ut, double now)
        {
            Vector3d omega = body.angularVelocity;
            if (omega.sqrMagnitude > 0)
            {
                double angle = (-(ut - now) * omega.magnitude) % (2 * Math.PI);
                Vector3d axis = omega.normalized;
                relative = relative * Math.Cos(angle) + Vector3d.Cross(axis, relative) * Math.Sin(angle)
                    + axis * Vector3d.Dot(axis, relative) * (1 - Math.Cos(angle));
            }
            Vector3d world = relative + body.position;
            return new MapPoint(ut, body.GetLatitude(world), MapGeometry.WrapLongitude(body.GetLongitude(world)), relative.magnitude - body.Radius);
        }

        private static List<MapPoint> Sample(Orbit orbit, CelestialBody body, double start, double end, double now)
        {
            if (orbit == null) return new List<MapPoint>();
            double step = orbit.eccentricity < 1 && MapGeometry.Finite(orbit.period) && orbit.period > 0 ? Math.Min(120, orbit.period / 32) : 120;
            return MapGeometry.Sample(ut => GroundPoint(body, orbit.getPositionAtUT(ut) - body.position, ut, now), start, end, step);
        }

        private static double RadiusIntersection(Orbit orbit, double now, double radius)
        {
            if (orbit == null || orbit.getRelativePositionAtUT(now).magnitude <= radius) return double.NaN;
            double toPe = orbit.timeToPe;
            if (toPe <= 0 || !MapGeometry.Finite(toPe)) return double.NaN;
            double end = now + toPe;
            if (orbit.patchEndTransition != Orbit.PatchTransitionType.FINAL && MapGeometry.Finite(orbit.EndUT)) end = Math.Min(end, orbit.EndUT);
            if (orbit.getRelativePositionAtUT(end).magnitude > radius) return double.NaN;
            double start = now;
            for (int i = 0; i < 45; i++) { double mid = (start + end) * .5; if (orbit.getRelativePositionAtUT(mid).magnitude > radius) start = mid; else end = mid; }
            return (start + end) * .5;
        }

        private static double SurfaceIntersection(Orbit orbit, CelestialBody body, double now)
        {
            if (body.pqsController == null) return RadiusIntersection(orbit, now, body.Radius);
            if (!MapGeometry.Finite(orbit.timeToPe) || orbit.timeToPe <= 0) return double.NaN;
            double end = now + orbit.timeToPe;
            if (orbit.patchEndTransition != Orbit.PatchTransitionType.FINAL) end = Math.Min(end, orbit.EndUT);
            Func<double, double> clearance = ut =>
            {
                MapPoint p = GroundPoint(body, orbit.getPositionAtUT(ut) - body.position, ut, now);
                double terrain = body.pqsController.GetSurfaceHeight(body.GetRelSurfaceNVector(p.Latitude, p.Longitude)) - body.Radius;
                return p.Altitude - terrain;
            };
            double previous = now;
            for (int i = 1; i <= 96; i++)
            {
                double time = now + (end - now) * i / 96;
                if (clearance(time) <= 0)
                {
                    double left = previous, right = time;
                    for (int k = 0; k < 32; k++) { double mid = (left + right) * .5; if (clearance(mid) > 0) left = mid; else right = mid; }
                    return (left + right) * .5;
                }
                previous = time;
            }
            return double.NaN;
        }

        private static string Number(double value) { return MapGeometry.Finite(value) ? value.ToString("R", CultureInfo.InvariantCulture) : "null"; }
        private static void Field(StringBuilder json, string key, double value) { json.Append(",\"").Append(key).Append("\":").Append(Number(value)); }
        private static void Field(StringBuilder json, string key, bool value) { json.Append(",\"").Append(key).Append("\":").Append(value ? "true" : "false"); }
        private static void Field(StringBuilder json, string key, string value) { json.Append(",\"").Append(key).Append("\":").Append(WireProtocol.Quote(value)); }
    }
}
