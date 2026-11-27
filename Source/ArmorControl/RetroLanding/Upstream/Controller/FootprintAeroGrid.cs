using System;

namespace LandingPoc
{
    // Small per-pose table. Samples are normalized by relative density and
    // speed squared. An incomplete cell uses the existing local FAR sample.
    internal sealed class FootprintAeroGrid
    {
        internal static readonly double[] Speeds = { 120, 300, 600, 1000, 1600, 2400 };
        internal static readonly double[] HeightFractions = { 0.02, 0.10, 0.23, 0.40, 0.64, 0.84 };
        private readonly double[] density;
        private readonly FootprintAero[] samples;

        internal FootprintAeroGrid(double[] densityAtHeights, FootprintAero[] values)
        {
            density = (double[])densityAtHeights.Clone();
            samples = (FootprintAero[])values.Clone();
        }

        internal bool TrySample(double speed, double relativeDensity, out FootprintAero value)
        {
            value = default;
            if (speed < Speeds[0] || speed > Speeds[Speeds.Length - 1] || density[0] <= 0 ||
                relativeDensity > density[0] || relativeDensity < density[density.Length - 1]) return false;
            int x = 0, y = 0;
            while (x + 2 < Speeds.Length && speed > Speeds[x + 1]) x++;
            while (y + 2 < density.Length && relativeDensity < density[y + 1]) y++;
            if (density[y + 1] <= 0 || density[y] <= density[y + 1]) return false;
            int count = Speeds.Length;
            FootprintAero a = samples[y * count + x], b = samples[y * count + x + 1],
                c = samples[(y + 1) * count + x], d = samples[(y + 1) * count + x + 1];
            if (!a.Valid || !b.Valid || !c.Valid || !d.Valid) return false;

            double ux = (speed - Speeds[x]) / (Speeds[x + 1] - Speeds[x]);
            double uy = Math.Log(density[y] / relativeDensity) / Math.Log(density[y] / density[y + 1]);
            value = Mix(Mix(a, b, ux), Mix(c, d, ux), uy);
            return true;
        }

        internal static FootprintAero Mix(FootprintAero a, FootprintAero b, double fraction) =>
            new FootprintAero(a.Drag + fraction * (b.Drag - a.Drag), a.Lift + fraction * (b.Lift - a.Lift),
                a.Cross + fraction * (b.Cross - a.Cross), a.Far && b.Far);
    }
}
