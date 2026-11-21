using UnityEngine;

namespace KspRetroLanding
{
    // The range and response fields are transient KSP fields. Leave the
    // player's persistent gimbalLimiter alone, including during quicksave.
    internal struct FinalGimbalSettings
    {
        private readonly float range, xp, xn, yp, yn, response;
        private readonly bool useResponse;

        internal FinalGimbalSettings(ModuleGimbal gimbal)
        {
            range = gimbal.gimbalRange;
            xp = gimbal.gimbalRangeXP;
            xn = gimbal.gimbalRangeXN;
            yp = gimbal.gimbalRangeYP;
            yn = gimbal.gimbalRangeYN;
            response = gimbal.gimbalResponseSpeed;
            useResponse = gimbal.useGimbalResponseSpeed;
        }

        internal float Apply(ModuleGimbal gimbal, double demand)
        {
            float maxRange = Envelope(xp, xn, yp, yn, range);
            float fraction = Mathf.Min((float)(0.45 + 0.35 * demand), (float)(3 + 4 * demand) / Mathf.Max(0.001f, maxRange));
            SetRanges(gimbal, fraction);
            float responseLimit = (float)(4 + 4 * demand);
            gimbal.useGimbalResponseSpeed = true;
            gimbal.gimbalResponseSpeed = useResponse && response > 0 ? Mathf.Min(response, responseLimit) : responseLimit;
            return Mathf.Max(0, maxRange) * fraction * gimbal.gimbalLimiter / 100;
        }

        internal void Restore(ModuleGimbal gimbal)
        {
            SetRanges(gimbal, 1);
            gimbal.useGimbalResponseSpeed = useResponse;
            gimbal.gimbalResponseSpeed = response;
        }

        internal static float Envelope(float xp, float xn, float yp, float yn, float fallback)
        {
            float x = Mathf.Max(xp < 0 ? fallback : xp, xn < 0 ? fallback : xn, 0);
            float y = Mathf.Max(yp < 0 ? fallback : yp, yn < 0 ? fallback : yn, 0);
            return Mathf.Sqrt(x * x + y * y);
        }

        private void SetRanges(ModuleGimbal gimbal, float fraction)
        {
            gimbal.gimbalRange = Limit(range, fraction);
            gimbal.gimbalRangeXP = Limit(xp, fraction);
            gimbal.gimbalRangeXN = Limit(xn, fraction);
            gimbal.gimbalRangeYP = Limit(yp, fraction);
            gimbal.gimbalRangeYN = Limit(yn, fraction);
        }

        private static float Limit(float original, float fraction) { return original < 0 ? original : original * fraction; }
    }
}
