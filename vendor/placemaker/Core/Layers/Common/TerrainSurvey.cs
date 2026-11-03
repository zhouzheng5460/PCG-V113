// Terrain survey (s20): what the ground under an area is like BEFORE anything is planned.
//
// Playtest 33: a waterfront drawn over a headland laid its terraform grid, promenade and props straight across a
// rocky cliff face. Every water type (and any type that wants to) now looks first: a grade grid over the site,
// classified flat / gentle / steep / cliff, so a layout can put its walk on the flatter shore band or on a bench it
// sculpts, leave cliffs natural, and keep furniture off slopes. Pure and cheap (one height sample per node).

using System;
using Unity.Mathematics;

namespace Placemaker.Core.Layers.Common
{
    public enum SlopeClass { Flat = 0, Gentle = 1, Steep = 2, Cliff = 3 }

    public sealed class TerrainSurvey
    {
        /// <summary>Grade (rise/run) limits: flat &lt; 5 %, gentle &lt; 15 %, steep &lt; 45 %, cliff above (≈ 24°).</summary>
        public const float FlatGrade = 0.05f, GentleGrade = 0.15f, SteepGrade = 0.45f;
        /// <summary>Furniture, benches, lamps: never on ground steeper than this without a pad.</summary>
        public const float FurnitureGrade = 0.12f;
        /// <summary>Walks: comfortable up to 8 %, a ramp up to ~12 %.</summary>
        public const float WalkGrade = 0.08f, RampGrade = 0.12f;

        public readonly float2 Origin;
        public readonly float Cell;
        public readonly int Nx, Ny;
        public readonly float[] Height, GradeGrid;
        private readonly Func<float2, float> _h;

        public TerrainSurvey(Func<float2, float> ground, float2 min, float2 max, float cell = 4f)
        {
            _h = ground ?? (_ => 0f);
            cell = math.max(1f, cell);
            min -= cell; max += cell;
            while ((long)(math.ceil((max.x - min.x) / cell) + 1) * (long)(math.ceil((max.y - min.y) / cell) + 1) > 60000) cell *= 1.2f;
            Origin = min; Cell = cell;
            Nx = (int)math.ceil((max.x - min.x) / cell) + 1;
            Ny = (int)math.ceil((max.y - min.y) / cell) + 1;
            Height = new float[Nx * Ny];
            GradeGrid = new float[Nx * Ny];
            for (int j = 0; j < Ny; j++)
                for (int i = 0; i < Nx; i++)
                {
                    float h = _h(Origin + new float2(i, j) * cell);
                    Height[j * Nx + i] = math.isfinite(h) ? h : 0f;
                }
            for (int j = 0; j < Ny; j++)
                for (int i = 0; i < Nx; i++)
                {
                    int i0 = math.max(0, i - 1), i1 = math.min(Nx - 1, i + 1), j0 = math.max(0, j - 1), j1 = math.min(Ny - 1, j + 1);
                    float gx = (Height[j * Nx + i1] - Height[j * Nx + i0]) / math.max(cell, (i1 - i0) * cell);
                    float gy = (Height[j1 * Nx + i] - Height[j0 * Nx + i]) / math.max(cell, (j1 - j0) * cell);
                    GradeGrid[j * Nx + i] = math.sqrt(gx * gx + gy * gy);
                }
        }

        public static TerrainSurvey Of(LayerContext ctx, LayerRegion region, float margin = 8f, float cell = 4f) =>
            new TerrainSurvey(ctx.H, region.Min - margin, region.Max + margin, cell);

        /// <summary>Grade of the original ground at p (bilinear on the grid; outside the grid sampled directly).</summary>
        public float GradeAt(float2 p)
        {
            float fx = (p.x - Origin.x) / Cell, fy = (p.y - Origin.y) / Cell;
            int i = (int)math.floor(fx), j = (int)math.floor(fy);
            if (i < 0 || j < 0 || i >= Nx - 1 || j >= Ny - 1) return Grade(_h, p, Cell);
            float tx = fx - i, ty = fy - j;
            int k = j * Nx + i;
            return math.lerp(math.lerp(GradeGrid[k], GradeGrid[k + 1], tx), math.lerp(GradeGrid[k + Nx], GradeGrid[k + Nx + 1], tx), ty);
        }

        /// <summary>Largest grade within r of p (samples the grid nodes around it).</summary>
        public float MaxGradeNear(float2 p, float r)
        {
            float g = GradeAt(p);
            int n = math.max(1, (int)math.ceil(r / Cell));
            for (int a = -n; a <= n; a++)
                for (int b = -n; b <= n; b++)
                {
                    var q = p + new float2(a, b) * (r / n);
                    if (math.distancesq(q, p) <= r * r + 1e-3f) g = math.max(g, GradeAt(q));
                }
            return g;
        }

        public SlopeClass ClassAt(float2 p) => Classify(GradeAt(p));

        public static SlopeClass Classify(float grade) =>
            grade < FlatGrade ? SlopeClass.Flat : grade < GentleGrade ? SlopeClass.Gentle : grade < SteepGrade ? SlopeClass.Steep : SlopeClass.Cliff;

        /// <summary>Share (0..1) of the region's ground in each slope class.</summary>
        public float[] Shares(LayerRegion region)
        {
            var count = new int[4]; int total = 0;
            for (int j = 0; j < Ny; j++)
                for (int i = 0; i < Nx; i++)
                {
                    var p = Origin + new float2(i, j) * Cell;
                    if (!region.Contains(p)) continue;
                    count[(int)Classify(GradeGrid[j * Nx + i])]++;
                    total++;
                }
            var s = new float[4];
            for (int c = 0; c < 4; c++) s[c] = total == 0 ? 0f : count[c] / (float)total;
            return s;
        }

        /// <summary>Steepest grade of any height function around p over ±r (four directions).</summary>
        public static float Grade(Func<float2, float> h, float2 p, float r)
        {
            float best = 0f;
            for (int a = 0; a < 4; a++)
            {
                float ang = a * math.PI / 4f;
                var d = new float2(math.cos(ang), math.sin(ang)) * r;
                best = math.max(best, math.abs(h(p + d) - h(p - d)) / (2f * r));
            }
            return best;
        }

        /// <summary>Height range (max − min) of h over a disc of radius r (9 samples): how much a pad there must level.</summary>
        public static float Relief(Func<float2, float> h, float2 p, float r)
        {
            float lo = h(p), hi = lo;
            for (int a = 0; a < 8; a++)
            {
                float ang = a * math.PI / 4f;
                float v = h(p + new float2(math.cos(ang), math.sin(ang)) * r);
                lo = math.min(lo, v); hi = math.max(hi, v);
            }
            return hi - lo;
        }
    }
}
