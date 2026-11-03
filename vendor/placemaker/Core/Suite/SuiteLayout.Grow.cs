// Game runner: land made to order that arrives after launch (a queue session gets a scenario with "land" in its
// inbox). The plot is added east of the last one; this writes its ground into the live heightmap the way the launch
// would have, had the plot been there from the start, but only outside what is already laid out, so the grid, the
// earlier plots and every standing build keep their ground exactly.

using Unity.Mathematics;

namespace Placemaker.Core.Suite
{
    public sealed partial class SuiteLayout
    {
        /// <summary>
        /// Patches <paramref name="current"/> (the live heightmap) for a fixture that grew: texels inside the old
        /// fixture rectangle (<paramref name="keepMin"/>..<paramref name="keepMax"/>) are never touched; elsewhere every
        /// texel the grown fixture now reaches gets what <see cref="Heightmap"/> gives it (the layout inside, the ease into
        /// <paramref name="original"/>, the map's heights before the launch, within the blend). Texels whose blend did not
        /// change are left alone, so without <paramref name="original"/> (null: the current heights stand in for it) the
        /// error stays near the new land. Returns the number of texels changed.
        /// </summary>
        public int PatchOutside(ushort[] current, ushort[] original, float2 keepMin, float2 keepMax)
        {
            int n = HeightmapSize;
            if (current == null || current.Length != n * n) throw new System.ArgumentException("heightmap must be " + n + "x" + n);
            if (original != null && original.Length != n * n) throw new System.ArgumentException("original heightmap must be " + n + "x" + n);
            float2 cell = MapExtent / n;
            var lo = FixtureMin - Blend;
            var hi = FixtureMax + Blend;
            int px0 = math.max(0, (int)math.floor((lo.x - MapOffset.x) / cell.x)), px1 = math.min(n - 1, (int)math.ceil((hi.x - MapOffset.x) / cell.x));
            int py0 = math.max(0, (int)math.floor((lo.y - MapOffset.y) / cell.y)), py1 = math.min(n - 1, (int)math.ceil((hi.y - MapOffset.y) / cell.y));
            int changed = 0;
            for (int py = py0; py <= py1; py++)
            {
                float z = MapOffset.y + (py + 0.5f) * cell.y;
                for (int px = px0; px <= px1; px++)
                {
                    float x = MapOffset.x + (px + 0.5f) * cell.x;
                    if (x >= keepMin.x && x <= keepMax.x && z >= keepMin.y && z <= keepMax.y) continue;   // laid out already
                    float d = OutsideFixture(x, z);
                    if (d >= Blend) continue;   // the map's own ground, as before
                    float dx = math.max(math.max(keepMin.x - x, x - keepMax.x), 0f), dz = math.max(math.max(keepMin.y - z, z - keepMax.y), 0f);
                    float dOld = math.sqrt(dx * dx + dz * dz);
                    if (d >= dOld) continue;     // the grown fixture is no nearer: its ease is what it was
                    int i = py * n + px;
                    float map = Decode(original != null ? original[i] : current[i]);
                    ushort v = Encode(math.lerp(Height(x, z), map, math.smoothstep(0f, Blend, d)));
                    if (current[i] != v) { current[i] = v; changed++; }
                }
            }
            return changed;
        }
    }
}
