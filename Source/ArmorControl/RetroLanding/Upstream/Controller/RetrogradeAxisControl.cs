using System;

namespace LandingPoc
{
    internal struct RetrogradeAxisControl
    {
        private double lastRate, command, meanInput, meanAcceleration, inputVariance, inputAcceleration, measuredAuthority;
        internal double Authority { get; private set; }

        internal double Update(double error, double rate, double targetRate, double applied, double nominal,
                               double elapsed, double step)
        {
            if (elapsed > 0)
            {
                // Fit acceleration against the input applied over the physics step.
                // The intercept captures torque that persists with zero input.
                double acceleration = (rate - lastRate) / elapsed;
                double weight = 1 - Math.Exp(-elapsed / 0.65);
                double inputDelta = applied - meanInput;
                double accelerationDelta = acceleration - meanAcceleration;
                meanInput += weight * inputDelta;
                meanAcceleration += weight * accelerationDelta;
                inputVariance = (1 - weight) * (inputVariance + weight * inputDelta * inputDelta);
                inputAcceleration = (1 - weight) * (inputAcceleration + weight * inputDelta * accelerationDelta);
                if (inputVariance >= 0.015)
                {
                    double fitted = inputAcceleration / inputVariance;
                    if (fitted > 0 && !double.IsInfinity(fitted))
                        measuredAuthority = fitted;
                }
            }
            lastRate = rate;

            Authority = Math.Max(0.05 * AttitudeGuidance.Degree, measuredAuthority > 0 ? measuredAuthority : nominal);
            double distance = Math.Max(0, Math.Abs(error) - 0.12 * AttitudeGuidance.Degree);
            double stoppingRate = Math.Sqrt(1.1 * Authority * distance);
            double correction = Math.Sign(error) * Math.Min(AttitudeGuidance.CoastMaxRate,
                Math.Min(distance / 1.2, stoppingRate));
            double bias = measuredAuthority > 0 ? Clamp(meanAcceleration / Authority - meanInput, 0.35) : 0;
            double response = Math.Max(0.45, 4 * step);
            double requested = Clamp((targetRate + correction - rate) / (Authority * response) - bias, 1);
            command += Clamp(requested - command, Math.Min(0.65, 6 * step));
            return command;
        }

        private static double Clamp(double value, double limit) => Math.Max(-limit, Math.Min(limit, value));
    }
}
