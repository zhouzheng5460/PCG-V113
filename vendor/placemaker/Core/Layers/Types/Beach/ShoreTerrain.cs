// Terrain shaping driven by the distance from the shore (beach slope, dunes, quay steps).
//
// Same rules as Common/TerrainShaper (cells ≈ brush size, permanent warning, slope relaxation), but
// the target height is a function of (point, original height, depth from the water) supplied by the
// type, and the blend back to the original ground happens towards the outline AWAY from the water
// only: along the waterline the beach is meant to meet the water, so the shore edge is not a border
// to blend to (offsets there are ~0 by construction: the beach plane starts at the shore height).

using System;
using System.Collections.Generic;
using Placemaker.Core.Geometry;
using Placemaker.Core.Layers.Common;
using Unity.Mathematics;

namespace Placemaker.Core.Layers.Types.Beach
{
    public static class ShoreTerrain
    {
        private const float MinChange = 0.2f;

        /// <param name="target">(point, original height, depth from the shore) → absolute target height.</param>
        /// <param name="beyond">(s13, optional) foreshore rings outside the land (Shoreline.Foreshore): shaped too, with a
        /// negative depth, so the beach slope can run down into the water when the outline stops short of it.</param>
        public static TerrainResult Shape(LayerContext ctx, Shoreline shore, Func<float2, float, float, float> target,
            float falloff, float maxSlopePct, float minCell = 6f, IReadOnlyList<float2[]> beyond = null)
        {
            var region = shore.Land;
            var result = new TerrainResult { HeightAfter = ctx.H };
            float cell = math.max(minCell, math.sqrt(region.Area / TerrainShaper.MaxCells));
            float2 gMin = region.Min, gMax = region.Max;
            if (beyond != null)
                foreach (var r in beyond)
                    foreach (var q in r) { gMin = math.min(gMin, q); gMax = math.max(gMax, q); }
            int nx = math.max(1, (int)math.ceil((gMax.x - gMin.x) / cell));
            int ny = math.max(1, (int)math.ceil((gMax.y - gMin.y) / cell));
            int Beyond(float2 c)
            {
                if (beyond == null) return -1;
                for (int k = 0; k < beyond.Count; k++) if (Poly.Contains(beyond[k], c)) return k;
                return -1;
            }
            var offset = new float[nx * ny];
            var baseH = new float[nx * ny];
            var inside = new bool[nx * ny];
            var edge = new float[nx * ny];
            falloff = math.max(cell, falloff);
            float slope = math.max(0.05f, maxSlopePct / 100f);

            for (int iy = 0; iy < ny; iy++)
            {
                if ((iy & 7) == 0) ctx.Check();
                for (int ix = 0; ix < nx; ix++)
                {
                    int i = iy * nx + ix;
                    var c = gMin + (new float2(ix, iy) + 0.5f) * cell;
                    bool land = region.Contains(c);
                    if (!land && Beyond(c) < 0) continue;
                    inside[i] = true;
                    float h0 = ctx.H(c);
                    float e = shore.EdgeDistance(c, falloff * 4f + 10f);
                    edge[i] = e;
                    float d = land ? shore.Depth(c) : -shore.Depth(c);
                    float t = target(c, h0, d);
                    if (!math.isfinite(t)) t = h0;
                    float off = (t - h0) * Smooth(0f, falloff, e);
                    baseH[i] = h0;
                    offset[i] = math.clamp(off, -slope * e, slope * e);
                }
            }

            // Neighbour relaxation (offsets only): the cell changed more gives way.
            float maxStep = slope * cell;
            for (int pass = 0; pass < 8; pass++)
            {
                bool changed = false;
                for (int iy = 0; iy < ny; iy++)
                    for (int ix = 0; ix < nx; ix++)
                    {
                        int i = iy * nx + ix;
                        if (!inside[i]) continue;
                        float t = baseH[i] + offset[i];
                        for (int k = 0; k < 4; k++)
                        {
                            int jx = ix + (k == 0 ? 1 : k == 1 ? -1 : 0), jy = iy + (k == 2 ? 1 : k == 3 ? -1 : 0);
                            if (jx < 0 || jy < 0 || jx >= nx || jy >= ny) continue;
                            int j = jy * nx + jx;
                            if (!inside[j]) continue;
                            float tj = baseH[j] + offset[j];
                            float allowed = maxStep + math.abs(baseH[i] - baseH[j]);   // the ground's own slope is not ours to fix
                            if (math.abs(t - tj) <= allowed || math.abs(offset[i]) <= math.abs(offset[j])) continue;
                            t = tj + math.sign(t - tj) * allowed;
                            changed = true;
                        }
                        float o = t - baseH[i];
                        offset[i] = math.clamp(o, -slope * edge[i], slope * edge[i]);
                    }
                if (!changed) break;
            }

            float minO = 0f, maxO = 0f; int cells = 0;
            for (int iy = 0; iy < ny; iy++)
                for (int ix = 0; ix < nx; ix++)
                {
                    int i = iy * nx + ix;
                    if (!inside[i] || math.abs(offset[i]) < MinChange) continue;
                    var c = gMin + (new float2(ix, iy) + 0.5f) * cell;
                    var square = LayerRegion.Rect(c, new float2(1, 0), cell * 0.5f, cell * 0.5f);
                    float2[] ring = square;
                    int bk = region.Contains(c) ? -1 : Beyond(c);
                    var clip = bk >= 0 ? beyond[bk] : region.Ring;
                    bool whole = true;
                    foreach (var q in square) if (!Poly.Contains(clip, q)) { whole = false; break; }
                    if (!whole)
                    {
                        var pieces = Poly.Intersection(square, clip);
                        if (pieces.Count == 0 || pieces[0].Outer == null || pieces[0].Outer.Length < 3) continue;
                        ring = pieces[0].Outer;
                    }
                    ctx.Plan.Terraform.Add(new PlannedTerraform
                    {
                        Ring = Poly.To3D(ring, ctx.H),
                        Kind = offset[i] > 0f ? TerraformKind.Raise : TerraformKind.Lower,
                        TargetHeight = baseH[i] + offset[i],
                        Permanent = true,
                    });
                    minO = math.min(minO, offset[i]); maxO = math.max(maxO, offset[i]);
                    cells++;
                }
            result.Cells = cells; result.MinOffset = minO; result.MaxOffset = maxO;
            if (cells > 0)
            {
                Warn(ctx);
                float cs = cell; int w = nx, hgt = ny; var off = offset; var ins = inside; var min = gMin;
                result.HeightAfter = p =>
                {
                    int ix = (int)math.floor((p.x - min.x) / cs), iy = (int)math.floor((p.y - min.y) / cs);
                    if (ix < 0 || iy < 0 || ix >= w || iy >= hgt) return ctx.H(p);
                    int i = iy * w + ix;
                    return ctx.H(p) + (ins[i] ? off[i] : 0f);
                };
                ctx.Plan.Facts["terrainCells"] = cells;
                ctx.Plan.Facts["terrainMaxRaise"] = maxO;
                ctx.Plan.Facts["terrainMaxLower"] = -minO;
            }
            return result;
        }

        /// <summary>A shallow hollow (two nested levels) — tide pools, quay step cuts.</summary>
        public static void Hollow(LayerContext ctx, float2[] outer, float2[] inner, float outerTarget, float innerTarget)
        {
            ctx.Plan.Terraform.Add(new PlannedTerraform { Ring = Poly.To3D(outer, ctx.H), Kind = TerraformKind.Lower, TargetHeight = outerTarget, Permanent = true });
            if (inner != null)
                ctx.Plan.Terraform.Add(new PlannedTerraform { Ring = Poly.To3D(inner, ctx.H), Kind = TerraformKind.Lower, TargetHeight = innerTarget, Permanent = true });
            Warn(ctx);
        }

        public static void Warn(LayerContext ctx)
        {
            foreach (var i in ctx.Plan.Issues) if (i.Key == TerrainShaper.PermanentKey) return;
            ctx.Issue(IssueSeverity.Warning, TerrainShaper.PermanentKey);
        }

        public static float Smooth(float e0, float e1, float x)
        {
            if (e1 <= e0) return x >= e0 ? 1f : 0f;
            float t = math.saturate((x - e0) / (e1 - e0));
            return t * t * (3f - 2f * t);
        }
    }
}
