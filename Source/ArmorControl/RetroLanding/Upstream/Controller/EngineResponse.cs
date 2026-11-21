using System;

namespace LandingPoc
{
    public static class EngineResponse
    {
        public static bool Known(double seconds)
        {
            return seconds >= 0 && !double.IsInfinity(seconds);
        }

        // The stock ramp applies Lerp(current, requested, rate * dt) per tick.
        // Sum the resulting normalized thrust deficit after each update.
        public static double StockLerp(bool enabled, double rate, double dt)
        {
            if (!enabled) return 0;
            double step = Math.Min(1, rate * dt);
            return step > 0 && dt > 0 ? dt * (1 - step) / step : double.NaN;
        }

        // Stock's alternate throttle response uses separate startup and
        // running multipliers. Accumulate lost impulse at full command, as
        // RealFuels does for its built-in effective spool time.
        public static double StockAlternate(bool instant, double target, double rate, double ignitionLevel,
                                            double startup, double started, double dt)
        {
            if (instant) return 0;
            if (target <= 0 || rate <= 0 || dt <= 0) return double.NaN;
            double current = 0, loss = 0;
            for (int i = 0; i * dt < 120; i++)
            {
                double remaining = target - current;
                double mult = current > ignitionLevel
                    ? (1 - (1 - remaining) * (1 - remaining)) * 5 * started
                    : 0.0005 + 4.05 * current * startup;
                double step = rate * dt * mult;
                if (remaining <= Math.Max(0.005, step)) return loss;
                if (step <= 0 || double.IsNaN(step)) return double.NaN;
                current += step;
                loss += dt * (1 - current / target);
            }
            return double.NaN;
        }
    }
}
