using System;

namespace LandingPoc
{
    internal delegate bool SampleAero(double height, double vx, double vz, out double ax, out double az);

    // Integrated once per aero refresh and queried by every engine group in the planner.
    internal sealed class AeroCoastForecast
    {
        internal const double StepSeconds = 0.4;
        internal const double LocalHorizonSeconds = 4.8;
        private const int MaximumSteps = 64;

        private struct Sample
        {
            internal double Time, Height, Vx, Vz;
        }

        private readonly Sample[] path = new Sample[MaximumSteps + 1];
        private int last;
        private double ax, az;
        private bool initialized;
        internal bool Complete { get; private set; }
        internal double Duration { get { return path[last].Time; } }
        internal AeroCoastForecast(Flight initial)
        {
            path[0] = new Sample { Height = initial.Height, Vx = initial.Vx, Vz = initial.Vz };
        }

        internal static AeroCoastForecast Integrate(Flight initial, double gravity, SampleAero sampleAero,
                                                    double horizon = LocalHorizonSeconds)
        {
            var forecast = new AeroCoastForecast(initial);
            forecast.Advance(gravity, sampleAero, horizon, MaximumSteps + 1);
            return forecast.last > 0 ? forecast : null;
        }

        internal void Advance(double gravity, SampleAero sampleAero, double horizon, int budget)
        {
            if (Complete || budget <= 0) return;
            if (!initialized)
            {
                Sample initial = path[0];
                initialized = sampleAero(initial.Height, initial.Vx, initial.Vz, out ax, out az);
                budget--;
                if (!initialized) { Complete = true; return; }
            }
            while (budget-- > 0 && last < MaximumSteps && Duration + 1e-8 < horizon)
            {
                Sample current = path[last];
                // Keep closely spaced samples for the local force check. Farther
                // out, take longer steps but bound the change in velocity.
                double speed = Controller.Norm(current.Vx, current.Vz);
                double netAcceleration = Controller.Norm(ax, az - gravity);
                double dt = current.Time < LocalHorizonSeconds - 1e-8 ? StepSeconds
                    : Math.Max(StepSeconds, Math.Min(2.5, 0.10 * speed / Math.Max(1, netAcceleration)));
                dt = Math.Min(dt, horizon - current.Time);
                double half = 0.5 * dt;
                double midpointHeight = current.Height + half * (current.Vz + 0.5 * half * (az - gravity));
                if (midpointHeight > 0 && !sampleAero(midpointHeight, current.Vx + half * ax,
                    current.Vz + half * (az - gravity), out ax, out az))
                {
                    Complete = true;
                    return;
                }
                double netUp = az - gravity;
                double vx = current.Vx + dt * ax;
                // The planar planner uses horizontal speed along the current track.
                if (current.Vx * vx < 0) vx = 0;
                path[++last] = new Sample {
                    Time = current.Time + dt,
                    Height = current.Height + dt * (current.Vz + 0.5 * dt * netUp),
                    Vx = vx, Vz = current.Vz + dt * netUp
                };
                if (path[last].Height <= 0) { Complete = true; return; }
            }
            Complete = last == MaximumSteps || Duration + 1e-8 >= horizon;
        }

        private Sample At(double seconds)
        {
            if (seconds <= 0) return path[0];
            if (seconds >= Duration) return path[last];
            int lo = 0, hi = last;
            while (hi - lo > 1)
            {
                int mid = (lo + hi) / 2;
                if (path[mid].Time <= seconds) lo = mid;
                else hi = mid;
            }
            Sample first = path[lo], next = path[hi];
            double dt = seconds - first.Time;
            double fraction = dt / (next.Time - first.Time);
            double verticalChange = (next.Vz - first.Vz) * fraction;
            return new Sample {
                Height = first.Height + dt * (first.Vz + 0.5 * verticalChange),
                Vx = first.Vx + (next.Vx - first.Vx) * fraction,
                Vz = first.Vz + verticalChange
            };
        }

        internal bool Matches(Flight measured, double age)
        {
            if (age < 0 || age >= Duration) return false;
            Sample expected = At(age);
            double velocityError = Controller.Norm(measured.Vx - expected.Vx, measured.Vz - expected.Vz);
            double speed = Controller.Norm(measured.Vx, measured.Vz);
            return velocityError <= Math.Max(5, 0.05 * speed) &&
                   Math.Abs(measured.Height - expected.Height) <= Math.Max(5, 0.015 * measured.Height);
        }

        internal Flight Project(Flight measured, double seconds, double gravity)
        {
            double age = measured.AeroForecastAge;
            double modeled = Math.Min(seconds, Duration - age);
            Sample from = At(age), to = At(age + modeled);
            // Anchor the cached trajectory to the latest measured state.
            measured.Height += to.Height - from.Height + (measured.Vz - from.Vz) * modeled;
            measured.Vx = Math.Max(0, measured.Vx + to.Vx - from.Vx);
            measured.Vz += to.Vz - from.Vz;

            // Only credit FAR forces over the computed part of the coast.
            double remainder = seconds - modeled;
            measured.Height += remainder * (measured.Vz - 0.5 * gravity * remainder);
            measured.Vz -= gravity * remainder;
            return measured;
        }
    }
}
