using System;

namespace LandingPoc
{
    internal static class FootprintGeometry
    {
        private const double Degree = Math.PI / 180;

        internal static double ContactSurfaceDistance(double terrainDistance, double seaLevelHeight, bool hasOcean) =>
            hasOcean ? Math.Min(terrainDistance, seaLevelHeight) : terrainDistance;

        internal static void Destination(double latitude, double longitude, double north, double east, double radius,
                                         out double resultLatitude, out double resultLongitude)
        {
            double angle = Controller.Norm(north, east) / radius;
            double bearing = Math.Atan2(east, north);
            double lat = latitude * Degree;
            double sine = Math.Sin(lat) * Math.Cos(angle) + Math.Cos(lat) * Math.Sin(angle) * Math.Cos(bearing);
            resultLatitude = Math.Asin(Math.Max(-1, Math.Min(1, sine))) / Degree;
            resultLongitude = longitude + Math.Atan2(Math.Sin(bearing) * Math.Sin(angle) * Math.Cos(lat),
                Math.Cos(angle) - Math.Sin(lat) * sine) / Degree;
            resultLongitude = (resultLongitude % 360 + 540) % 360 - 180;
        }

        internal static void Deviation(double waypointLat, double waypointLon, double touchdownLat,
                                       double touchdownLon, double radius, out double distance, out double bearing,
                                       out double eastOffset, out double northOffset)
        {
            double start = waypointLat * Degree;
            double end = touchdownLat * Degree;
            double deltaLongitude = (touchdownLon - waypointLon) * Degree;
            double halfLatitude = Math.Sin((end - start) / 2);
            double halfLongitude = Math.Sin(deltaLongitude / 2);
            double cosStart = Math.Cos(start), cosEnd = Math.Cos(end);
            double h = halfLatitude * halfLatitude + cosStart * cosEnd * halfLongitude * halfLongitude;
            distance = 2 * radius * Math.Asin(Math.Sqrt(Math.Max(0, Math.Min(1, h))));

            double east = Math.Sin(deltaLongitude) * cosEnd;
            double north = cosStart * Math.Sin(end) - Math.Sin(start) * cosEnd * Math.Cos(deltaLongitude);
            // Coincident or antipodal points have no unique bearing. At a
            // polar waypoint there is no local true-north reference.
            double length = Controller.Norm(north, east);
            if (length < 1e-12 || Math.Abs(cosStart) < 1e-12)
            {
                bearing = double.NaN;
                eastOffset = northOffset = distance < 1e-5 ? 0 : double.NaN;
                return;
            }
            double meters = distance / length;
            eastOffset = east * meters;
            northOffset = north * meters;
            bearing = (Math.Atan2(east, north) / Degree + 360) % 360;
        }
    }
}
