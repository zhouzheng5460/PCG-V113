// Terrain variation as PlannedTerraform cells (permanent; the plan always carries a warning).
//
// A height field is sampled on a grid of square cells; each cell whose target differs from the
// current ground becomes one Level terraform polygon (clipped to the region at the outline). The
// game layer levels each cell with brush dabs, so cell size ≈ the build's brush radius.
//
// Rules (playtest 2, item 6): variation only ever happens in the interior. Every shape is blended
// back to the ORIGINAL ground height towards the outline over `Falloff` metres (smoothstep of the
// distance to the outline), so the border never gets a cliff. Neighbouring cells are relaxed so no
// step exceeds the max slope.
//
// Shapes: mounds (isolated noise humps), hills (rolling noise), terraces (levels following the
// outline, up or down), sunken / raised (one interior level), bowl (amphitheatre rows rising away
// from a focal point), berms (ridges along road frontage), flatten (one plane).

using System;
using System.Collections.Generic;
using Placemaker.Core.Geometry;
using Unity.Mathematics;

namespace Placemaker.Core.Layers.Common
{
    public enum TerrainMode { None = 0, Mounds = 1, Hills = 2, Terraces = 3, Sunken = 4, Raised = 5, Bowl = 6, Berms = 7, Flatten = 8 }

    public struct TerrainSpec
    {
        public TerrainMode Mode;
        /// <summary>Peak height (mounds, hills, raised, berms) or total depth/height (sunken, terraces, bowl), metres.</summary>
        public float Height;
        /// <summary>Typical feature size (mounds/hills), metres.</summary>
        public float Scale;
        /// <summary>Largest slope between neighbouring cells, percent (rise/run·100).</summary>
        public float MaxSlopePct;
        /// <summary>Distance over which the shape blends back to the original ground at the outline.</summary>
        public float Falloff;
        /// <summary>Terrace levels (2..4).</summary>
        public int Levels;
        /// <summary>Terraces: true = levels go down towards the middle (stepped sunken plaza).</summary>
        public bool Down;
        /// <summary>Bowl: the focal point (stage) and the row spacing.</summary>
        public float2 Focal;
        public float RowSpacing;
        public uint Seed;
        /// <summary>Berms: road-facing outline pieces (empty = whole outline).</summary>
        public List<(float2 a, float2 b)> Frontage;
        /// <summary>Cells whose centre this rejects are left untouched (e.g. a pond basin).</summary>
        public Func<float2, bool> Skip;
        /// <summary>
        /// (s31) Shape on a fine node grid emitted as SLOPED quads (Common/Sculpt): a continuous surface instead of 12 m
        /// level cells (inside the union of level cells the build's raster has no bank, so neighbours at different levels
        /// were vertical steps — the "curtains" of the playtest).
        /// </summary>
        public bool Sloped;
        /// <summary>(s31, optional) 0..1 per point, multiplies the change (decorative shapes fade out near paths and furniture).</summary>
        public Func<float2, float> Weight;

        public static TerrainMode Parse(string s)
        {
            switch (s)
            {
                case "mounds": return TerrainMode.Mounds;
                case "hills": return TerrainMode.Hills;
                case "terraces": case "terracedUp": return TerrainMode.Terraces;
                case "terracedDown": return TerrainMode.Terraces;
                case "sunken": return TerrainMode.Sunken;
                case "raised": return TerrainMode.Raised;
                case "bowl": case "amphitheatre": return TerrainMode.Bowl;
                case "berms": return TerrainMode.Berms;
                case "flatten": return TerrainMode.Flatten;
                default: return TerrainMode.None;
            }
        }
    }

    public sealed class TerrainResult
    {
        public int Cells;
        public float MinOffset, MaxOffset;
        /// <summary>Planned ground height (after shaping) at a point; the original ground outside the shaped cells.</summary>
        public Func<float2, float> HeightAfter;
    }

    public static class TerrainShaper
    {
        public const float DefaultCell = 12f;
        public const int MaxCells = 1500;
        public const string PermanentKey = "Placemaker.Terrain.Issue.Permanent";
        private const float MinChange = 0.2f;

        /// <summary>Mean ground height over the region (grid sample).</summary>
        public static float MeanHeight(LayerContext ctx, LayerRegion region)
        {
            double sum = 0; int n = 0;
            var size = region.Max - region.Min;
            float step = math.max(2f, math.sqrt(size.x * size.y / 400f));
            for (float y = region.Min.y + step * 0.5f; y < region.Max.y; y += step)
                for (float x = region.Min.x + step * 0.5f; x < region.Max.x; x += step)
                {
                    var p = new float2(x, y);
                    if (!region.Contains(p)) continue;
                    sum += ctx.H(p); n++;
                }
            return n == 0 ? ctx.H(region.Centroid) : (float)(sum / n);
        }

        /// <summary>Shapes the region and emits the terraform cells into ctx.Plan. Adds the permanent warning when anything changes.</summary>
        public static TerrainResult Shape(LayerContext ctx, LayerRegion region, TerrainSpec spec)
        {
            var result = new TerrainResult { HeightAfter = ctx.H };
            if (spec.Mode == TerrainMode.None || region == null) return result;
            if (spec.Mode == TerrainMode.Sunken || spec.Mode == TerrainMode.Bowl || (spec.Mode == TerrainMode.Terraces && spec.Down)) ctx.Plan.Facts["intentionalPit"] = 1;   // s58: the pit guard keeps off
            if (spec.Sloped) return ShapeSloped(ctx, region, spec);
            float cell = math.max(DefaultCell, math.sqrt(region.Area / MaxCells));
            int nx = math.max(1, (int)math.ceil((region.Max.x - region.Min.x) / cell));
            int ny = math.max(1, (int)math.ceil((region.Max.y - region.Min.y) / cell));
            var offset = new float[nx * ny];
            var baseH = new float[nx * ny];
            var inside = new bool[nx * ny];
            var dist = new float[nx * ny];
            float mean = MeanHeight(ctx, region);
            float falloff = math.max(cell, spec.Falloff);
            region.DeepestPoint(out float inscribed, null, 12);
            inscribed = math.max(1f, inscribed);
            float height = math.max(0f, spec.Height);

            for (int iy = 0; iy < ny; iy++)
            {
                if ((iy & 15) == 0) ctx.Check();
                for (int ix = 0; ix < nx; ix++)
                {
                    int i = iy * nx + ix;
                    var c = region.Min + (new float2(ix, iy) + 0.5f) * cell;
                    if (!region.Contains(c)) continue;
                    if (spec.Skip != null && spec.Skip(c)) continue;
                    inside[i] = true;
                    float h0 = ctx.H(c);
                    float d = region.BoundaryDistance(c, inscribed + 1f);
                    dist[i] = d;
                    float blend = Smooth(0f, falloff, d);
                    float target;   // absolute target before blending
                    switch (spec.Mode)
                    {
                        case TerrainMode.Mounds:
                        {
                            float n = ValueNoise.Clumps(c, math.max(8f, spec.Scale), spec.Seed);
                            float hump = math.pow(math.saturate((n - 0.45f) / 0.55f), 1.5f);
                            target = h0 + height * math.saturate(hump * 1.6f);
                            break;
                        }
                        case TerrainMode.Hills:
                            target = h0 + height * ValueNoise.Clumps(c, math.max(12f, spec.Scale), spec.Seed);
                            break;
                        case TerrainMode.Terraces:
                        {
                            int levels = math.clamp(spec.Levels, 2, 4);
                            float band = math.max(cell, (inscribed - falloff * 0.5f) / levels);
                            int k = math.min(levels - 1, (int)math.floor(math.max(0f, d - falloff * 0.5f) / band));
                            float step = height / math.max(1, levels - 1);
                            target = mean + (spec.Down ? -1f : 1f) * step * k;
                            break;
                        }
                        case TerrainMode.Sunken:
                            target = mean - height * Smooth(falloff * 0.5f, falloff * 0.5f + cell * 1.5f, d);
                            break;
                        case TerrainMode.Raised:
                            target = mean + height * Smooth(falloff * 0.5f, falloff * 0.5f + cell * 1.5f, d);
                            break;
                        case TerrainMode.Bowl:
                        {
                            float rows = math.max(2f, spec.RowSpacing);
                            float r = math.distance(c, spec.Focal);
                            float k = math.floor(r / rows);
                            target = mean + math.min(height, k * rows * 0.35f);
                            break;
                        }
                        case TerrainMode.Berms:
                        {
                            float w = math.max(6f, spec.Scale);
                            float df = spec.Frontage != null && spec.Frontage.Count > 0 ? StreetFrontage.DistanceToFrontage(c, spec.Frontage, w * 3f) : d;
                            float x = df / w;   // ridge peaks at half the berm width from the outline
                            target = h0 + height * math.saturate(1f - math.abs(x - 0.5f) * 2f);
                            // Berms hug the edge on purpose: blend over a short distance only.
                            blend = Smooth(0f, cell * 0.5f, d);
                            break;
                        }
                        case TerrainMode.Flatten:
                            target = mean;
                            break;
                        default: target = h0; break;
                    }
                    baseH[i] = h0;
                    offset[i] = (target - h0) * blend;
                }
            }

            // Slope limit (offsets only — the ground's own slope is not ours to fix). First the outline:
            // no cell may differ from the original ground by more than the max slope times its distance
            // to the outline (no cliff at the border, ever). Then neighbours: where two cells differ by
            // more than a slope step, the one changed more gives way (so the edge stays anchored).
            float slope = math.max(0.05f, spec.MaxSlopePct / 100f);
            float maxStep = slope * cell;
            for (int i = 0; i < offset.Length; i++)
                if (inside[i]) offset[i] = math.clamp(offset[i], -slope * dist[i], slope * dist[i]);
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
                            if (math.abs(t - tj) <= maxStep || math.abs(t - baseH[i]) <= math.abs(offset[j])) continue;
                            t = tj + math.sign(t - tj) * maxStep;
                            changed = true;
                        }
                        offset[i] = t - baseH[i];
                    }
                if (!changed) break;
            }

            float minO = 0f, maxO = 0f; int cells = 0;
            for (int iy = 0; iy < ny; iy++)
                for (int ix = 0; ix < nx; ix++)
                {
                    int i = iy * nx + ix;
                    if (!inside[i] || math.abs(offset[i]) < MinChange) continue;
                    var c = region.Min + (new float2(ix, iy) + 0.5f) * cell;
                    var square = LayerRegion.Rect(c, new float2(1, 0), cell * 0.5f, cell * 0.5f);
                    float2[] ring = square;
                    bool whole = true;
                    foreach (var q in square) if (!region.Contains(q)) { whole = false; break; }
                    if (!whole)
                    {
                        var pieces = Poly.Intersection(square, region.Ring);
                        if (pieces.Count == 0 || pieces[0].Outer == null || pieces[0].Outer.Length < 3) continue;
                        ring = pieces[0].Outer;
                    }
                    float target = baseH[i] + offset[i];
                    ctx.Plan.Terraform.Add(new PlannedTerraform
                    {
                        Ring = Poly.To3D(ring, ctx.H),
                        Kind = offset[i] > 0f ? TerraformKind.Raise : TerraformKind.Lower,
                        TargetHeight = target,
                        Permanent = true,
                    });
                    minO = math.min(minO, offset[i]); maxO = math.max(maxO, offset[i]);
                    cells++;
                }
            result.Cells = cells; result.MinOffset = minO; result.MaxOffset = maxO;
            if (cells > 0)
            {
                ctx.Issue(IssueSeverity.Warning, PermanentKey);
                float cellSize = cell; int w = nx, hgt = ny; var off = offset; var ins = inside; var min = region.Min;
                result.HeightAfter = p =>
                {
                    int ix = (int)math.floor((p.x - min.x) / cellSize), iy = (int)math.floor((p.y - min.y) / cellSize);
                    if (ix < 0 || iy < 0 || ix >= w || iy >= hgt) return ctx.H(p);
                    int i = iy * w + ix;
                    // Cells below MinChange are not emitted, so the ground there stays as it is.
                    return ctx.H(p) + (ins[i] && math.abs(off[i]) >= MinChange ? off[i] : 0f);
                };
                // What this layer places afterwards stands on the new ground (LayerContext.P3/G).
                // No polygon test: outside the shaped cells HeightAfter already returns the original ground.
                ctx.Ground.Override(region.Min, region.Max, result.HeightAfter);
                ctx.Plan.Facts["terrainCells"] = cells;
                ctx.Plan.Facts["terrainMaxRaise"] = maxO;
                ctx.Plan.Facts["terrainMaxLower"] = -minO;
            }
            return result;
        }

        /// <summary>
        /// (s31) The same shapes on a node grid (≥ 4 m) emitted as sloped quads: mounds and hills are smooth landforms,
        /// terraces and bowls get ramps no steeper than the max slope instead of cliffs, and the change dies out towards
        /// the outline over the falloff (never steeper than the max slope there either).
        /// </summary>
        private static TerrainResult ShapeSloped(LayerContext ctx, LayerRegion region, TerrainSpec spec)
        {
            float cell = math.max(4f, math.sqrt(region.Area / 12000f));
            float mean = MeanHeight(ctx, region);
            float falloff = math.max(cell, spec.Falloff);
            region.DeepestPoint(out float inscribed, null, 12);
            inscribed = math.max(1f, inscribed);
            float height = math.max(0f, spec.Height);
            var sc = Sculpt.Over(ctx, region, null, cell, spec.Skip);
            var s = spec;
            sc.Shape((p, h0, cur) =>
            {
                float d = region.BoundaryDistance(p, inscribed + 1f);
                float blend = Smooth(0f, falloff, d);
                float target = Target(s, p, h0, d, mean, inscribed, falloff, DefaultCell, height, ref blend);
                float w = s.Weight != null ? math.saturate(s.Weight(p)) : 1f;
                return h0 + (target - h0) * blend * w;
            });
            float slope = math.max(0.05f, spec.MaxSlopePct / 100f);
            sc.LimitGrade(slope);
            sc.CapToBorder(slope);
            var r = sc.Emit(ctx, new[] { region.Ring });
            return r;
        }

        private static float Target(TerrainSpec spec, float2 c, float h0, float d, float mean, float inscribed, float falloff, float cell, float height, ref float blend)
        {
            switch (spec.Mode)
            {
                case TerrainMode.Mounds:
                {
                    float n = ValueNoise.Clumps(c, math.max(8f, spec.Scale), spec.Seed);
                    float hump = math.pow(math.saturate((n - 0.45f) / 0.55f), 1.5f);
                    return h0 + height * math.saturate(hump * 1.6f);
                }
                case TerrainMode.Hills:
                    return h0 + height * ValueNoise.Clumps(c, math.max(12f, spec.Scale), spec.Seed);
                case TerrainMode.Terraces:
                {
                    int levels = math.clamp(spec.Levels, 2, 4);
                    float band = math.max(cell, (inscribed - falloff * 0.5f) / levels);
                    int k = math.min(levels - 1, (int)math.floor(math.max(0f, d - falloff * 0.5f) / band));
                    float step = height / math.max(1, levels - 1);
                    return mean + (spec.Down ? -1f : 1f) * step * k;
                }
                case TerrainMode.Sunken:
                    return mean - height * Smooth(falloff * 0.5f, falloff * 0.5f + cell * 1.5f, d);
                case TerrainMode.Raised:
                    return mean + height * Smooth(falloff * 0.5f, falloff * 0.5f + cell * 1.5f, d);
                case TerrainMode.Bowl:
                {
                    float rows = math.max(2f, spec.RowSpacing);
                    float r = math.distance(c, spec.Focal);
                    float k = math.floor(r / rows);
                    return mean + math.min(height, k * rows * 0.35f);
                }
                case TerrainMode.Berms:
                {
                    float w = math.max(6f, spec.Scale);
                    float df = spec.Frontage != null && spec.Frontage.Count > 0 ? StreetFrontage.DistanceToFrontage(c, spec.Frontage, w * 3f) : d;
                    float x = df / w;
                    blend = Smooth(0f, cell * 0.5f, d);
                    return h0 + height * math.saturate(1f - math.abs(x - 0.5f) * 2f);
                }
                case TerrainMode.Flatten:
                    return mean;
                default: return h0;
            }
        }

        private static float Smooth(float e0, float e1, float x)
        {
            if (e1 <= e0) return x >= e0 ? 1f : 0f;
            float t = math.saturate((x - e0) / (e1 - e0));
            return t * t * (3f - 2f * t);
        }
    }
}
