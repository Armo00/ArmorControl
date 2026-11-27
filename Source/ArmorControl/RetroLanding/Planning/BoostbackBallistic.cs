using System;

namespace LandingPoc
{
    internal static class BoostbackBallistic
    {
        // Propagate the current coast without atmosphere. Use an inertial frame
        // and rotate the ground back underneath the result at impact.
        internal static bool TryImpact(double radius, double mu, double rotationRate, double latitude, double longitude,
            double altitude, double eastSpeed, double northSpeed, double verticalSpeed, double ground,
            out double lat, out double lon, out double seconds, out double speed)
        {
            lat = lon = seconds = speed = 0;
            if (radius <= 0 || mu <= 0 || altitude <= ground) return false;
            BoostbackPreview.SurfaceState(latitude, longitude, radius, altitude, eastSpeed, northSpeed, verticalSpeed,
                out AttitudeVector position, out AttitudeVector surfaceVelocity);
            var spin = new AttitudeVector(0, 0, rotationRate);
            var velocity = surfaceVelocity + spin.Cross(position);
            double surface = radius + ground;
            const double step = 2;
            var gravity = Gravity(position);
            for (double t = 0; t < 900; t += step)
            {
                var next = position + velocity * step + gravity * (step * step / 2);
                var endGravity = Gravity(next);
                var endVelocity = velocity + (gravity + endGravity) * (step / 2);
                if (next.Length <= surface)
                {
                    double fraction = (position.Length - surface) / (position.Length - next.Length);
                    var impact = position + (next - position) * fraction;
                    seconds = t + step * fraction;
                    lat = Math.Atan2(impact.Z, Math.Sqrt(impact.X * impact.X + impact.Y * impact.Y)) / AttitudeGuidance.Degree;
                    lon = AttitudeGuidance.WrapDegrees((Math.Atan2(impact.Y, impact.X) - rotationRate * seconds) / AttitudeGuidance.Degree);
                    speed = (velocity + (endVelocity - velocity) * fraction - spin.Cross(impact)).Length;
                    return FootprintAtmosphere.Finite(speed);
                }
                position = next;
                velocity = endVelocity;
                gravity = endGravity;
            }
            return false;

            AttitudeVector Gravity(AttitudeVector r) => r.Unit * (-mu / r.Dot(r));
        }
    }
}
