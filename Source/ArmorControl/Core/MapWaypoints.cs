using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using ArmorOverhaul.ArmorControl.Protocol;

namespace ArmorOverhaul.ArmorControl.Core
{
    // Detached main-thread values only. Never expose FinePrint/Unity objects to the server.
    internal sealed class MapWaypoint
    {
        internal string Id, Body, Name;
        internal double Latitude, Longitude, Altitude;
        internal bool Navigating, OnSurface, Custom;
    }

    internal static class MapWaypoints
    {
        internal static string Json(IEnumerable<MapWaypoint> points, string body)
        {
            var json = new StringBuilder("[");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (MapWaypoint point in points)
            {
                if (point == null || point.Body != body || string.IsNullOrEmpty(point.Id)
                    || !MapGeometry.Finite(point.Latitude) || Math.Abs(point.Latitude) > 90
                    || !MapGeometry.Finite(point.Longitude) || !seen.Add(point.Id)) continue;
                if (json.Length > 1) json.Append(',');
                json.Append("{\"id\":").Append(WireProtocol.Quote(point.Id))
                    .Append(",\"name\":").Append(WireProtocol.Quote(point.Name ?? ""))
                    .Append(",\"latitude\":").Append(Number(point.Latitude))
                    .Append(",\"longitude\":").Append(Number(MapGeometry.WrapLongitude(point.Longitude)))
                    .Append(",\"altitude\":").Append(Number(point.Altitude))
                    .Append(",\"navigating\":").Append(point.Navigating ? "true" : "false")
                    .Append(",\"onSurface\":").Append(point.OnSurface ? "true" : "false")
                    .Append(",\"custom\":").Append(point.Custom ? "true" : "false").Append('}');
            }
            return json.Append(']').ToString();
        }

        private static string Number(double value)
        { return MapGeometry.Finite(value) ? value.ToString("R", CultureInfo.InvariantCulture) : "null"; }
    }
}
