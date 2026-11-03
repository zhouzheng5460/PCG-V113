// Building blocks of the Japanese garden: odd-numbered rock groupings (a dominant upright stone, a
// lower companion and a flat third set as a scalene triangle — never symmetric, never even), stepping-
// stone trails (stones only, a gentle S between two points), raked-gravel strokes painted into the
// terrain, and the tea house setting (house, waiting bench, stone basin with a lantern, approach).

using System;
using System.Collections.Generic;
using Placemaker.Core.Geometry;
using Placemaker.Core.Layers.Common;
using Unity.Mathematics;

namespace Placemaker.Core.Layers.Types.ZenGarden
{
    public static class ZenKit
    {
        public const float RockRadius = 1.1f;

        /// <summary>Stone scales of a group, largest first (relative to the dominant stone).</summary>
        private static readonly float[] StoneScale = { 1.45f, 1f, 0.72f, 0.6f, 0.52f, 0.46f, 0.4f };

        /// <summary>Odd group sizes for "mixed": the classic 5-3-3-1-3… rhythm.</summary>
        public static int GroupSize(string mode, int index)
        {
            switch (mode)
            {
                case "three": return 3;
                case "five": return 5;
                default:
                {
                    int[] rhythm = { 5, 3, 3, 1, 3, 5, 1, 3, 7 };
                    return rhythm[index % rhythm.Length];
                }
            }
        }

        /// <summary>Radius a group of n stones needs at the given scale.</summary>
        public static float GroupRadius(int n, float scale) => RockRadius * scale * (n <= 1 ? 1.6f : n <= 3 ? 2.8f : n <= 5 ? 3.6f : 4.4f);

        /// <summary>
        /// Places one rock group of <paramref name="n"/> stones (n odd) around <paramref name="c"/>:
        /// the dominant stone, then companions at uneven angles and distances. Returns stones placed.
        /// </summary>
        public static int RockGroup(LayerContext ctx, Occupancy occ, float2 c, int n, float scale, string[] ids, string category, uint seed,
            Func<float2, float, bool> blocked)
        {
            int placed = 0;
            float a0 = Hashing.Unit(seed) * 6.283f;
            // Uneven spokes: 0°, ~115°, ~230°+jitter… a scalene arrangement that never mirrors.
            float[] spoke = { 0f, 2.0f, 4.05f, 1.05f, 3.1f, 5.1f, 0.5f };
            float prevR = 0f;
            for (int i = 0; i < n && i < StoneScale.Length; i++)
            {
                uint v = Hashing.Mix(seed, (uint)i + 1u);
                float s = StoneScale[i] * scale * (0.9f + 0.2f * Hashing.Unit(v));
                float r = RockRadius * s;
                float2 p;
                if (i == 0) p = c;
                else
                {
                    float dist = (RockRadius * StoneScale[0] * scale + r) * (0.85f + 0.5f * Hashing.Unit(Hashing.Mix(v, 3u))) + (i > 2 ? prevR * 0.6f : 0f);
                    float a = a0 + spoke[i] + (Hashing.Unit(Hashing.Mix(v, 5u)) - 0.5f) * 0.5f;
                    p = c + new float2(math.cos(a), math.sin(a)) * dist;
                }
                if (blocked != null && blocked(p, r)) continue;
                if (!occ.Free(p, r, 0.3f, 0.05f)) continue;
                var o = ctx.Object(p, Hashing.Unit(Hashing.Mix(v, 7u)) * 6.283f, ObjectRole.Furniture, AssetMix.Pick(ids, v), v, -1, category);
                o.Scale = s;
                occ.Take(p, r, 2);
                prevR = r;
                placed++;
            }
            if (placed > 0 && (placed & 1) == 0)
            {
                // Never an even group: drop the smallest (last) stone.
                ctx.Plan.Objects.RemoveAt(ctx.Plan.Objects.Count - 1);
                placed--;
            }
            return placed;
        }

        /// <summary>
        /// Stepping stones from a to b along a gentle S-curve, every <paramref name="spacing"/> m, alternating
        /// a little left and right. Stones that would land on something are skipped. Returns stones placed.
        /// </summary>
        public static int SteppingTrail(LayerContext ctx, Occupancy occ, float2 a, float2 b, float spacing, AssetRef asset, string category, float scale,
            uint seed, Func<float2, float, bool> blocked, List<float2> placedOut = null)
        {
            float len = math.distance(a, b);
            if (len < 2f) return 0;
            var d = (b - a) / len;
            var side = new float2(-d.y, d.x);
            float bulge = len * 0.12f * (Hashing.Unit(seed) < 0.5f ? -1f : 1f);
            int n = math.max(1, (int)math.floor(len / math.max(0.5f, spacing)));
            int placed = 0;
            const float r = 0.45f;
            for (int i = 0; i <= n; i++)
            {
                float t = i / (float)n;
                // S-curve: one bulge each way.
                var p = math.lerp(a, b, t) + side * (bulge * math.sin(t * 2f * math.PI)) + side * ((i & 1) == 0 ? 0.18f : -0.18f);
                if (blocked != null && blocked(p, r)) continue;
                if (!occ.Free(p, r, 0.3f, 0.05f)) continue;
                uint v = Hashing.Mix(seed, (uint)i + 11u);
                var o = ctx.Object(p, LeisureKit.Yaw(a, b) + (Hashing.Unit(v) - 0.5f) * 0.6f, ObjectRole.Furniture, asset, v, -1, category);
                o.Scale = scale;
                occ.Take(p, r, 7);
                placedOut?.Add(p);
                placed++;
            }
            return placed;
        }

        /// <summary>
        /// Raked gravel as thin painted strokes: straight lines along the field's axis, broken around the
        /// rock groups, and/or concentric ripples around each group. Strokes stay inside the field.
        /// </summary>
        public static int Rake(LayerContext ctx, float2[] field, float2 axis, IReadOnlyList<(float2 c, float r)> groups, string mode, float spacing, int layer)
        {
            if (mode == "none" || field == null || field.Length < 3) return 0;
            int strokes = 0;
            const float half = 0.22f;   // stroke half width
            const int cap = 900;
            bool lines = mode == "lines" || mode == "both";
            bool ripples = mode == "ripples" || mode == "both";
            var fr = LayerRegion.From(field);
            if (fr == null) return 0;
            void Stroke(float2 a, float2 b)
            {
                if (strokes >= cap) return;
                float len = math.distance(a, b);
                if (len < 0.8f) return;
                var ring = LayerRegion.Rect((a + b) * 0.5f, (b - a) / len, len * 0.5f, half);
                ctx.Plan.Paint.Add(new PlannedPaint { Ring = Poly.To3D(ring, ctx.H), Layer = math.clamp(layer, 1, 4), Strength = 0.9f });
                strokes++;
            }
            if (lines)
            {
                var side = new float2(-axis.y, axis.x);
                Poly.Bounds(field, out var mn, out var mx);
                var centre = (mn + mx) * 0.5f;
                float reach = math.length(mx - mn) * 0.6f + 2f;
                for (float off = -reach; off <= reach; off += spacing)
                {
                    var o = centre + side * off;
                    foreach (var (s0, s1) in Poly.ClipSegmentToPolygon(field, o - axis * reach, o + axis * reach))
                    {
                        // Inset from the field edge, and broken where a ripple zone surrounds a group.
                        var a = s0 + axis * 0.8f; var b = s1 - axis * 0.8f;
                        if (math.dot(b - a, axis) <= 0f) continue;
                        var cuts = new List<float2>();
                        foreach (var (gc, gr) in groups)
                        {
                            float rr = gr + (ripples ? 3 * spacing : 0.8f);
                            float dv = math.dot(gc - a, side);
                            if (math.abs(dv) >= rr) continue;
                            float du = math.sqrt(rr * rr - dv * dv);
                            float u = math.dot(gc - a, axis);
                            cuts.Add(new float2(u - du, u + du));
                        }
                        cuts.Sort((x, y) => x.x.CompareTo(y.x));
                        float len = math.dot(b - a, axis), at = 0f;
                        foreach (var cut in cuts)
                        {
                            if (cut.x > at) Stroke(a + axis * at, a + axis * math.min(len, cut.x));
                            at = math.max(at, cut.y);
                        }
                        if (at < len) Stroke(a + axis * at, b);
                    }
                }
            }
            if (ripples)
                foreach (var (gc, gr) in groups)
                    for (int k = 1; k <= 3; k++)
                    {
                        float rr = gr + k * spacing;
                        int segs = math.clamp((int)(rr * 2.2f), 10, 28);
                        for (int i = 0; i < segs; i++)
                        {
                            float a0 = i * 2f * math.PI / segs, a1 = (i + 1) * 2f * math.PI / segs;
                            var p0 = gc + new float2(math.cos(a0), math.sin(a0)) * rr;
                            var p1 = gc + new float2(math.cos(a1), math.sin(a1)) * rr;
                            if (!fr.ContainsDisc(p0, 0.6f) || !fr.ContainsDisc(p1, 0.6f)) continue;
                            Stroke(p0, p1);
                        }
                    }
            return strokes;
        }

        /// <summary>
        /// The tea house setting on the quietest open spot (farthest from the entrances): the house facing
        /// the nearest path, a waiting bench on the way, a stone basin with a lantern, and stepping stones
        /// from the path to the door. Returns false when no spot fits.
        /// </summary>
        public static bool TeaHouse(LayerContext ctx, Occupancy occ, IReadOnlyList<float2> gates, AssetRef house, string houseCategory,
            bool bench, bool basin, bool lantern, bool stones, Func<float2, float, bool> blocked, out float2 at)
        {
            at = default;
            var region = occ.LayerRegion;
            float r = math.min(5f, AssetMix.FootprintRadius(ctx.Site, house, 3.5f));
            float best = float.MinValue;
            foreach (var p in Scatter.Candidates(region, 3f, Hashing.Mix(ctx.Seed, 0x7EAu), 1500, ctx.Cancel))
            {
                if (!occ.Free(p, r + 3f, 1.5f, 0.3f)) continue;
                if (blocked != null && blocked(p, r + 3f)) continue;
                float far = 0f;
                if (gates != null && gates.Count > 0)
                {
                    far = float.MaxValue;
                    foreach (var g in gates) far = math.min(far, math.distance(g, p));
                }
                // Secluded but reachable: far from the entrances, within reach of a path.
                float toPath = occ.Paths.Count > 0 ? occ.Paths.Clearance(p, 60f) : 0f;
                float score = far - math.max(0f, toPath - 18f) * 2f + Hashing.Unit(Hashing.Mix(ctx.Seed, (int)p.x, (int)p.y));
                if (score > best) { best = score; at = p; }
            }
            if (best == float.MinValue) return false;
            var door = occ.Paths.Count > 0 && occ.Paths.Nearest(at, 200f, out var foot) >= 0 ? foot : region.Centroid;
            var fwd = math.normalizesafe(door - at, new float2(0, 1));
            var side = new float2(-fwd.y, fwd.x);
            ctx.Object(at, LeisureKit.Yaw(at, door), ObjectRole.Furniture, house, Hashing.Mix(ctx.Seed, 0x7EA1u), -1, houseCategory);
            occ.Take(at, r, 2);
            uint s = Hashing.Mix(ctx.Seed, 0x7EA2u);
            if (basin && LeisureKit.Category(ctx, "", AssetCategories.ZenBasin) is string basinCat)
            {
                var bp = at + fwd * (r + 1.6f) + side * (r * 0.8f + 1.2f);
                if (LeisureKit.Put(ctx, occ, bp, LeisureKit.Yaw(bp, at), ObjectRole.Furniture, AssetMix.One(""), basinCat, 0.7f, s, 0.5f, 0.1f) && lantern)
                {
                    string lc = LeisureKit.Category(ctx, "", AssetCategories.ZenLantern, AssetCategories.Lamp);
                    if (lc != null) LeisureKit.Put(ctx, occ, bp + side * 1.5f, 0f, ObjectRole.Lamp, AssetMix.One(""), lc, 0.5f, Hashing.Mix(s, 3u), 0.5f, 0.1f);
                }
            }
            if (bench)
            {
                var wp = math.lerp(at, door, 0.55f) + side * 2.2f;
                LeisureKit.Put(ctx, occ, wp, LeisureKit.Yaw(wp, wp - side), ObjectRole.Bench, AssetMix.One(""), null, 1.3f, Hashing.Mix(s, 5u), 0.5f, 0.2f);
            }
            if (stones && math.distance(at, door) > r + 3f)
            {
                var (asset, cat, scale) = SteppingAsset(ctx, "");
                if (cat != null) SteppingTrail(ctx, occ, at + fwd * (r + 0.8f), door - fwd * 2.3f, 0.9f, asset, cat, scale, Hashing.Mix(s, 9u), blocked);
            }
            return true;
        }

        /// <summary>Stepping stone asset: the chosen one, a stepping-stone prop, else a small flat rock.</summary>
        public static (AssetRef asset, string category, float scale) SteppingAsset(LayerContext ctx, string chosen)
        {
            if (!string.IsNullOrEmpty(chosen)) return (AssetMix.One(chosen), AssetCategories.ZenSteppingStone, 1f);
            if (AssetAvailability.Has(AssetCategories.ZenSteppingStone)) return (AssetMix.One(""), AssetCategories.ZenSteppingStone, 1f);
            if (AssetAvailability.Has(AssetCategories.Rock)) return (AssetMix.One(""), AssetCategories.Rock, 0.3f);
            AssetAvailability.Require(ctx.Plan, AssetCategories.ZenSteppingStone, null, Keys.Category(AssetCategories.ZenSteppingStone));
            return (default, null, 1f);
        }
    }
}
