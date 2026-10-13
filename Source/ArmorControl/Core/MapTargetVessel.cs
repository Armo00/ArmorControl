using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using ArmorOverhaul.ArmorControl.Protocol;

namespace ArmorOverhaul.ArmorControl.Core
{
    // Detached main-thread values. No live Vessel/Orbit objects enter the transport store.
    internal sealed class MapTargetVessel
    {
        internal string Id, Name, Body, BodyName, Status = "none", EndReason;
        internal bool Available, OnSurface, HistoryMissing;
        internal double Latitude = double.NaN, Longitude = double.NaN, Altitude = double.NaN;

        internal string Json(IList<MapSegment> segments)
        {
            bool visible = Available && !string.IsNullOrEmpty(Id) && !string.IsNullOrEmpty(Body) && MapGeometry.Finite(Latitude)
                && Math.Abs(Latitude) <= 90 && MapGeometry.Finite(Longitude) && MapGeometry.Finite(Altitude);
            var json = new StringBuilder("{\"available\":").Append(visible ? "true" : "false")
                .Append(",\"id\":").Append(WireProtocol.Quote(Id))
                .Append(",\"name\":").Append(WireProtocol.Quote(Name))
                .Append(",\"body\":").Append(WireProtocol.Quote(Body))
                .Append(",\"bodyName\":").Append(WireProtocol.Quote(BodyName))
                .Append(",\"status\":").Append(WireProtocol.Quote(Available && !visible ? "unavailable" : Status))
                .Append(",\"endReason\":").Append(WireProtocol.Quote(EndReason))
                .Append(",\"onSurface\":").Append(OnSurface ? "true" : "false")
                .Append(",\"historyMissing\":").Append(HistoryMissing ? "true" : "false")
                .Append(",\"latitude\":").Append(Number(visible ? Latitude : double.NaN))
                .Append(",\"longitude\":").Append(Number(visible ? MapGeometry.WrapLongitude(Longitude) : double.NaN))
                .Append(",\"altitude\":").Append(Number(visible ? Altitude : double.NaN))
                .Append(",\"segments\":");
            AppendSegments(json, visible && !OnSurface ? segments : null);
            return json.Append('}').ToString();
        }

        internal static void AppendSegments(StringBuilder json, IList<MapSegment> segments)
        {
            json.Append('[');
            if (segments != null) for (int i = 0; i < segments.Count; i++)
            {
                if (i > 0) json.Append(',');
                MapSegment segment = segments[i];
                json.Append("{\"source\":").Append(WireProtocol.Quote(segment.Source))
                    .Append(",\"dashed\":").Append(segment.Dashed ? "true" : "false").Append(",\"points\":[");
                for (int j = 0; j < segment.Points.Count; j++)
                {
                    if (j > 0) json.Append(',');
                    MapPoint point = segment.Points[j];
                    json.Append('[').Append(Number(point.Ut)).Append(',').Append(Number(point.Latitude)).Append(',')
                        .Append(Number(point.Longitude)).Append(',').Append(Number(point.Altitude)).Append(']');
                }
                json.Append("]}");
            }
            json.Append(']');
        }

        private static string Number(double value)
        { return MapGeometry.Finite(value) ? value.ToString("R", CultureInfo.InvariantCulture) : "null"; }
    }
}
