// The hill as a height-offset field over the area, emitted as PlannedTerraform cells.
//
// Shape: for every cell, t = d / (d + s) where d is the distance to the outline and s the distance to
// the summit plateau — 0 on the outline, 1 on the plateau, and its level sets follow the outline's
// shape, so an L-shaped area gets an L-shaped hill instead of a cone that pokes out of one arm.
// A profile f(t) gives the character (cone, dome, rounded mound, flat-topped mesa, terraces) and a
// little value noise roughens the slopes.
//
// Blending (playtest 2, item 6): the offset is clamped by an envelope of the outline distance —
// at most 25 % within the first 15 m (no lip at the border, ever), then the player's max slope — and
// relaxed between neighbouring cells (chamfer passes) so no step exceeds the max slope. Offsets are
// only ever added to the original ground, so a sloped site keeps its slope at the outline.

using System;
using System.Collections.Generic;
using Placemaker.Core.Build;
using Placemaker.Core.Geometry;
using Placemaker.Core.Layers.Common;
using Unity.Mathematics;

namespace Placemaker.Core.Layers.Types.LookoutHill
{
    public enum HillShape { Cone = 0, Dome = 1, Mound = 2, Mesa = 3, Terraced = 4 }

    public struct HillSpec
    {
        public float Height;
        public HillShape Shape;
        public float2 Summit;
        public float Plateau;
        /// <summary>Largest slope between neighbouring cells (rise/run).</summary>
        public float MaxSlope;
        /// <summary>0..1 noise on the slopes.</summary>
        public float Roughness;
        public int Levels;
        public uint Seed;

        public static HillShape Parse(string s)
        {
            switch (s)
            {
                case "cone": return HillShape.Cone;
                case "dome": return HillShape.Dome;
                case "mesa": return HillShape.Mesa;
                case "terraced": return HillShape.Terraced;
                default: return HillShape.Mound;
            }
        }
    }

    public sealed class HillField
    {
        public const float EdgeBand = 15f;
        public const float EdgeSlope = 0.25f;
        public const int MaxCells = 1600;

        public float2 Min;
        public float Cell;
        public int Nx, Ny;
        public float[] Offset;
        public bool[] Inside;
        public float Peak;
        public int Cells;
        public float2 Summit;
        private Func<float2, float> _ground;

        /// <summary>Planned ground height (original ground + hill offset).</summary>
        public float HeightAt(float2 p) => _ground(p) + OffsetAt(p);

        /// <summary>Bilinear offset between cell centres (0 outside the area).</summary>
        public float OffsetAt(float2 p)
        {
            float fx = (p.x - Min.x) / Cell - 0.5f, fy = (p.y - Min.y) / Cell - 0.5f;
            int x0 = (int)math.floor(fx), y0 = (int)math.floor(fy);
            float tx = fx - x0, ty = fy - y0;
            float a = Get(x0, y0), b = Get(x0 + 1, y0), c = Get(x0, y0 + 1), d = Get(x0 + 1, y0 + 1);
            return math.lerp(math.lerp(a, b, tx), math.lerp(c, d, tx), ty);
        }

        private float Get(int x, int y)
        {
            if (x < 0 || y < 0 || x >= Nx || y >= Ny) return 0f;
            int i = y * Nx + x;
            return Inside[i] ? Offset[i] : 0f;
        }

        /// <summary>Gradient of the planned ground (central differences, metres per metre).</summary>
        public float2 Gradient(float2 p, float h = 2f) =>
            new float2(HeightAt(p + new float2(h, 0)) - HeightAt(p - new float2(h, 0)), HeightAt(p + new float2(0, h)) - HeightAt(p - new float2(0, h))) / (2f * h);

        /// <summary>Offset gradient only (the hill itself, ignoring the site's own slope).</summary>
        public float2 OffsetGradient(float2 p, float h = 2f) =>
            new float2(OffsetAt(p + new float2(h, 0)) - OffsetAt(p - new float2(h, 0)), OffsetAt(p + new float2(0, h)) - OffsetAt(p - new float2(0, h))) / (2f * h);

        public static float Envelope(float d, float maxSlope) =>
            d <= EdgeBand ? EdgeSlope * math.max(0f, d) : EdgeSlope * EdgeBand + maxSlope * (d - EdgeBand);

        public static float Profile(HillShape shape, float t, int levels)
        {
            t = math.saturate(t);
            switch (shape)
            {
                case HillShape.Cone: return t;
                case HillShape.Dome: return 1f - (1f - t) * (1f - t);
                case HillShape.Mesa: { float u = math.saturate(t / 0.6f); return u * u * (3f - 2f * u); }
                case HillShape.Terraced:
                {
                    int n = math.clamp(levels, 2, 6);
                    float q = t * t * (3f - 2f * t) * n;
                    float k = math.floor(q), f = q - k;
                    float ramp = math.saturate((f - 0.7f) / 0.3f);
                    return math.min(1f, (k + ramp * ramp * (3f - 2f * ramp)) / n);
                }
                default: return t * t * (3f - 2f * t);
            }
        }

        public static HillField Build(LayerContext ctx, LayerRegion region, HillSpec spec, Func<float2, float> skip = null)
        {
            var f = new HillField { _ground = ctx.H, Summit = spec.Summit };
            float cell = math.clamp(math.sqrt(region.Area / MaxCells), 5f, 30f);
            int nx = math.max(1, (int)math.ceil((region.Max.x - region.Min.x) / cell));
            int ny = math.max(1, (int)math.ceil((region.Max.y - region.Min.y) / cell));
            f.Min = region.Min; f.Cell = cell; f.Nx = nx; f.Ny = ny;
            var off = new float[nx * ny]; var inside = new bool[nx * ny]; var dist = new float[nx * ny];
            float cap = math.cmax(region.Max - region.Min);
            for (int iy = 0; iy < ny; iy++)
            {
                if ((iy & 15) == 0) ctx.Check();
                for (int ix = 0; ix < nx; ix++)
                {
                    int i = iy * nx + ix;
                    var c = region.Min + (new float2(ix, iy) + 0.5f) * cell;
                    if (!region.Contains(c)) continue;
                    inside[i] = true;
                    float d = region.BoundaryDistance(c, cap);
                    dist[i] = d;
                    float s = math.max(0f, math.distance(c, spec.Summit) - spec.Plateau);
                    float t = d + s > 1e-3f ? d / (d + s) : 1f;
                    float h = spec.Height * Profile(spec.Shape, t, spec.Levels);
                    if (spec.Roughness > 0f)
                        h += spec.Height * spec.Roughness * 0.12f * (ValueNoise.Sample(c / 22f, spec.Seed) - 0.5f) * 4f * t * (1f - t);
                    if (skip != null) h = math.min(h, skip(c));
                    off[i] = math.max(0f, h);
                }
            }

            // Envelope from the outline, then neighbour relaxation (two chamfer sweeps, twice).
            float slope = math.max(0.05f, spec.MaxSlope);
            float riser = spec.Shape == HillShape.Terraced ? slope * 1.8f : slope;
            for (int i = 0; i < off.Length; i++) if (inside[i]) off[i] = math.min(off[i], Envelope(dist[i], slope));
            float step = riser * cell, diag = step * 1.4142f;
            for (int pass = 0; pass < 2; pass++)
            {
                for (int iy = 0; iy < ny; iy++)
                    for (int ix = 0; ix < nx; ix++) Relax(off, inside, nx, ny, ix, iy, step, diag, -1);
                for (int iy = ny - 1; iy >= 0; iy--)
                    for (int ix = nx - 1; ix >= 0; ix--) Relax(off, inside, nx, ny, ix, iy, step, diag, 1);
            }
            f.Offset = off; f.Inside = inside;
            for (int i = 0; i < off.Length; i++) if (inside[i]) f.Peak = math.max(f.Peak, off[i]);
            return f;
        }

        // Cells outside the area count as 0 (anchored at the original ground).
        private static void Relax(float[] off, bool[] inside, int nx, int ny, int ix, int iy, float step, float diag, int dir)
        {
            int i = iy * nx + ix;
            if (!inside[i]) return;
            float v = off[i];
            for (int k = 0; k < 4; k++)
            {
                int dx = k == 0 ? dir : k == 1 ? 0 : k == 2 ? dir : -dir;
                int dy = k == 0 ? 0 : k == 1 ? dir : k == 2 ? dir : dir;
                int jx = ix + dx, jy = iy + dy;
                float n = jx < 0 || jy < 0 || jx >= nx || jy >= ny || !inside[jy * nx + jx] ? 0f : off[jy * nx + jx];
                v = math.min(v, n + (dx != 0 && dy != 0 ? diag : step));
            }
            off[i] = v;
        }

        /// <summary>
        /// Emits one terraform patch per changed grid cell (clipped at the outline). Returns the cell count.
        /// (rf-nature) Sloped patches through the planned ground at their corners (PlannedTerraform.Sloped, the s20
        /// sculpt surface), not one level per cell: neighbouring patches share their corner heights, so the hill is one
        /// continuous slope instead of a staircase of 5 m flat squares (the stepped contours, and the walls the paths cut
        /// into the steps).
        /// </summary>
        public int Emit(LayerContext ctx, LayerRegion region)
        {
            int cells = 0;
            for (int iy = 0; iy < Ny; iy++)
            {
                if ((iy & 15) == 0) ctx.Check();
                for (int ix = 0; ix < Nx; ix++)
                {
                    int i = iy * Nx + ix;
                    if (!Inside[i]) continue;
                    var c = Min + (new float2(ix, iy) + 0.5f) * Cell;
                    float2[] ring = LayerRegion.Rect(c, new float2(1, 0), Cell * 0.5f, Cell * 0.5f);
                    float most = Offset[i];
                    foreach (var q in ring) most = math.max(most, OffsetAt(q));
                    if (most < 0.2f) continue;
                    bool whole = true;
                    foreach (var q in ring) if (!region.Contains(q)) { whole = false; break; }
                    if (!whole)
                    {
                        var pieces = Poly.Intersection(ring, region.Ring);
                        if (pieces.Count == 0 || pieces[0].Outer == null || pieces[0].Outer.Length < 3) continue;
                        ring = pieces[0].Outer;
                    }
                    // Within the edge band no corner rises faster than the band allows (cell centres outside the area count
                    // as 0, so the interpolation alone would leave a lip at an outline that cuts a cell).
                    var r3 = Poly.To3D(ring, p => { float d = region.BoundaryDistance(p, EdgeBand + 1f); return _ground(p) + (d < EdgeBand ? math.min(OffsetAt(p), EdgeSlope * d) : OffsetAt(p)); });
                    ctx.Plan.Terraform.Add(new PlannedTerraform
                    {
                        Ring = r3, Kind = TerraformKind.Raise, TargetHeight = TerraformSurface.At(r3, Poly.Centroid(ring)), Permanent = true, Sloped = true,
                    });
                    cells++;
                }
            }
            Cells = cells;
            if (cells > 0) ctx.Issue(IssueSeverity.Warning, TerrainShaper.PermanentKey);
            return cells;
        }

        /// <summary>Grid points (cell centres) whose offset lies within ±tol of a level, for contour placements.</summary>
        public List<float2> Contour(float level, float tol)
        {
            var r = new List<float2>();
            for (int iy = 0; iy < Ny; iy++)
                for (int ix = 0; ix < Nx; ix++)
                {
                    int i = iy * Nx + ix;
                    if (Inside[i] && math.abs(Offset[i] - level) <= tol) r.Add(Min + (new float2(ix, iy) + 0.5f) * Cell);
                }
            return r;
        }
    }
}
