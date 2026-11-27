using System;
using LandingPoc;

namespace KspRetroLanding
{
    public sealed partial class KspLanding
    {
        private LandingYawSample yawCommand, yawWritten;
    }

    internal readonly struct LandingYawSample
    {
        internal const string Columns = ",yaw_command_ut,yaw_written_ut,yaw_target_error_deg,yaw_axis_rate_deg_s" +
            ",yaw_commanded_input,yaw_written_input";

        internal readonly bool Valid;
        private readonly double commandUt, writtenUt, errorDegrees, rateDegrees;
        private readonly float command, written;

        internal LandingYawSample(double ut, double localError, double localRate, float command)
            : this(ut, double.NaN, -localError / AttitudeGuidance.Degree, -localRate / AttitudeGuidance.Degree, command, 0)
        {
            // KSP positive yaw input acts along negative local Z.
        }

        private LandingYawSample(double commandUt, double writtenUt, double errorDegrees, double rateDegrees,
            float command, float written)
        {
            Valid = true;
            this.commandUt = commandUt;
            this.writtenUt = writtenUt;
            this.errorDegrees = errorDegrees;
            this.rateDegrees = rateDegrees;
            this.command = command;
            this.written = written;
        }

        internal LandingYawSample Written(double ut, float value) =>
            new LandingYawSample(commandUt, ut, errorDegrees, rateDegrees, command, value);

        internal string ToCsv(IFormatProvider format) => Valid && !double.IsNaN(writtenUt)
            ? string.Format(format, ",{0:F6},{1:F6},{2:F3},{3},{4:F4},{5:F4}", commandUt, writtenUt, errorDegrees,
                double.IsNaN(rateDegrees) ? "" : rateDegrees.ToString("F3", format), command, written)
            : ",,,,,,";
    }
}
