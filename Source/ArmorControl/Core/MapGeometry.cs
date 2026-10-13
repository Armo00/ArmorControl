using System;
using System.Collections.Generic;

namespace ArmorOverhaul.ArmorControl.Core
{
    internal sealed class MapPoint
    {
        internal double Ut, Latitude, Longitude, Altitude;
        internal MapPoint(double ut, double latitude, double longitude, double altitude)
        { Ut = ut; Latitude = latitude; Longitude = longitude; Altitude = altitude; }
    }

    internal sealed class MapSegment
    {
        internal string Source;
        internal bool Dashed;
        internal readonly List<MapPoint> Points = new List<MapPoint>();
    }

    internal sealed class MapDefaultWindow
    {
        internal double Start;
        internal int Hours;
        internal string Reason;
    }

    internal static class MapGeometry
    {
        internal static bool Finite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }
        internal static double WrapLongitude(double value) { return ((value + 180) % 360 + 360) % 360 - 180; }

        internal static MapDefaultWindow Default(bool atmosphere, double entryUt, double periapsis, double impactUt, double now)
        {
            if (atmosphere && Finite(entryUt)) return new MapDefaultWindow { Start = entryUt - 600, Hours = 3, Reason = "atmosphere" };
            if (!atmosphere && periapsis < 0 && Finite(impactUt)) return new MapDefaultWindow { Start = impactUt - 10800, Hours = 3, Reason = "impact" };
            return new MapDefaultWindow { Start = now, Hours = 5, Reason = "current" };
        }

        internal static MapPoint Interpolate(MapPoint a, MapPoint b, double ut)
        {
            double fraction = b.Ut <= a.Ut ? 0 : Math.Max(0, Math.Min(1, (ut - a.Ut) / (b.Ut - a.Ut)));
            return new MapPoint(ut, a.Latitude + (b.Latitude - a.Latitude) * fraction,
                WrapLongitude(a.Longitude + WrapLongitude(b.Longitude - a.Longitude) * fraction),
                a.Altitude + (b.Altitude - a.Altitude) * fraction);
        }

        internal static List<MapPoint> Window(IList<MapPoint> points, double start, double end)
        {
            var result = new List<MapPoint>();
            for (int i = 1; i < points.Count; i++)
            {
                MapPoint a = points[i - 1], b = points[i];
                if (b.Ut <= a.Ut || b.Ut < start || a.Ut > end) continue;
                MapPoint left = a.Ut < start ? Interpolate(a, b, start) : a;
                MapPoint right = b.Ut > end ? Interpolate(a, b, end) : b;
                if (result.Count == 0 || left.Ut > result[result.Count - 1].Ut) result.Add(left);
                if (right.Ut > result[result.Count - 1].Ut) result.Add(right);
            }
            return result;
        }

        internal static List<MapPoint> Sample(Func<double, MapPoint> point, double start, double end, double maxStep)
        {
            var result = new List<MapPoint>();
            if (!Finite(start) || !Finite(end) || end <= start || !Finite(maxStep) || maxStep <= 0) return result;
            result.Add(point(start));
            int intervals = (int)Math.Min(512, Math.Max(1, Math.Ceiling((end - start) / maxStep)));
            // Distribute the budget across the whole window, not just its beginning.
            for (int i = 1; i <= intervals; i++)
            {
                int budget = Math.Max(1, (2048 - result.Count) / (intervals - i + 1));
                SampleInterval(result, point, result[result.Count - 1], point(start + (end - start) * i / intervals), 0, budget);
            }
            return result;
        }

        private static void SampleInterval(List<MapPoint> points, Func<double, MapPoint> point, MapPoint a, MapPoint b, int depth, int budget)
        {
            MapPoint mid = point((a.Ut + b.Ut) * .5);
            double angular = Math.Max(Math.Abs(b.Latitude - a.Latitude), Math.Abs(WrapLongitude(b.Longitude - a.Longitude)));
            double error = Math.Abs(mid.Altitude - (a.Altitude + b.Altitude) * .5);
            if (depth < 16 && budget > 1 && (b.Ut - a.Ut > 120 || angular > 1 || error > 1000))
            { SampleInterval(points, point, a, mid, depth + 1, budget / 2); SampleInterval(points, point, mid, b, depth + 1, budget - budget / 2); }
            else points.Add(b);
        }

        internal static List<MapSegment> Split(IList<MapPoint> points, double radius, string source)
        {
            var result = new List<MapSegment>();
            if (!Finite(radius) || radius <= 0) return result;
            double solid = radius * .1, hidden = radius * 12; // 6 diameters, measured above the surface.
            MapSegment active = null;
            for (int i = 1; i < points.Count; i++)
            {
                MapPoint a = points[i - 1], b = points[i];
                if (!Finite(a.Ut) || !Finite(b.Ut) || !Finite(a.Altitude) || !Finite(b.Altitude)
                    || !Finite(a.Latitude) || !Finite(b.Latitude) || !Finite(a.Longitude) || !Finite(b.Longitude) || b.Ut <= a.Ut)
                { active = null; continue; }
                var cuts = new List<MapPoint> { a };
                foreach (double height in new[] { solid, hidden })
                    if ((a.Altitude < height && b.Altitude > height) || (a.Altitude > height && b.Altitude < height))
                        cuts.Add(Interpolate(a, b, a.Ut + (b.Ut - a.Ut) * (height - a.Altitude) / (b.Altitude - a.Altitude)));
                cuts.Add(b); cuts.Sort((x, y) => x.Ut.CompareTo(y.Ut));
                for (int j = 1; j < cuts.Count; j++)
                {
                    MapPoint left = cuts[j - 1], right = cuts[j];
                    double mid = (left.Altitude + right.Altitude) * .5;
                    if (mid > hidden) { active = null; continue; }
                    bool dashed = mid > solid;
                    if (active == null || active.Dashed != dashed)
                    { active = new MapSegment { Source = source, Dashed = dashed }; active.Points.Add(left); result.Add(active); }
                    active.Points.Add(right);
                }
            }
            return result;
        }
    }
}
