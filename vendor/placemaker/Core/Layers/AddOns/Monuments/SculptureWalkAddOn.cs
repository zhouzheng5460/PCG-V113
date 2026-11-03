// Sculpture walk: a walk the length of the rectangle (gentle S-curve, straight gallery or zigzag)
// with sculptures on paved pads set alternately to either side at a steady rhythm. Each station has a
// backdrop of clipped shrubs behind the piece and a bench across the walk facing it, so every sculpture
// is framed and seen. Sculptures vary per object (monument.sculpture is a variety category) and in
// scale. The walk is split into exact Bézier pieces (de Casteljau) so a spur from the host's path can
// join one of its nodes.

using System.Collections.Generic;
using Placemaker.Core.Layers.Common;
using Unity.Mathematics;

namespace Placemaker.Core.Layers.AddOns.Monuments
{
    [AreaGenerator(174)]
    public sealed class SculptureWalkAddOn : MonumentAddOn
    {
        public const string LayerId = "sculptureWalk";
        protected override string Area => "AddOn.SculptureWalk";
        public override string Id => LayerId;

        protected override void DefineControls(ControlSet c)
        {
            c.Choice("style", "curve", ControlTier.Essential, "walk", "curve", "straight", "zigzag").Tip();
            c.Slider("sculptures", 6, 2, 16, 1, "count", ControlTier.Essential, "walk");
            c.Slider("size", 150, 75, 300, 25, "%", ControlTier.Essential, "walk").Tip();
            c.AssetMulti("sculptureAssets", AssetCategories.MonumentSculpture, ControlTier.Essential, "walk");
            c.Toggle("variety", true, ControlTier.More, "walk").Tip();
            c.Toggle("pads", true, ControlTier.More, "stations");
            c.Toggle("backdrops", true, ControlTier.More, "stations").Tip();
            c.Toggle("benches", true, ControlTier.More, "stations");
            c.Toggle("lamps", true, ControlTier.More, "stations");
            c.Toggle("trees", true, ControlTier.More, "stations");
            c.Asset("pavingAsset", AssetCategories.SurfacePaving, ControlTier.More, "ground").When("pads=true");
            c.Asset("pathAsset", AssetCategories.Pathway, ControlTier.More, "ground");
            c.RandomRange("sculptures", 4, 10);
            c.RandomRange("size", 100, 250);
        }

        protected override void DefinePresets()
        {
            Preset("meander", ("style", "curve"), ("sculptures", 6.0), ("trees", true), ("backdrops", true));
            Preset("gallery", ("style", "straight"), ("sculptures", 8.0), ("size", 125.0), ("trees", false), ("backdrops", false), ("variety", false));
            Preset("garden", ("style", "zigzag"), ("sculptures", 5.0), ("size", 200.0), ("trees", true), ("backdrops", true));
        }

        protected override void Build(LayerContext ctx, Occupancy occ, OrientedRect site, float2 toward)
        {
            var ids = ctx.L("sculptureAssets");
            string chosen = ids.Length > 0 ? ids[0] : "";
            string cat = LeisureKit.Category(ctx, chosen, MonumentKit.SculptureChain);
            if (cat == null) return;
            var entry = MonumentKit.Entry(occ, site, toward, true);
            float e = EndSign(site, entry);
            var d = site.Axis * -e;
            var side = new float2(-d.y, d.x);
            float hl = site.HalfLength - 0.2f, hw = site.HalfWidth;
            var A = site.Center - d * hl; var B = site.Center + d * hl;
            float amp = math.min(hw * 0.28f, 3.5f);
            string style = ctx.S("style");
            var pathAsset = AssetMix.One(ctx.S("pathAsset"));

            // Master curve(s) of the walk, as Bézier pieces with shared nodes.
            var pieces = new List<float2[]>();
            if (style == "zigzag")
            {
                var p1 = A + d * (hl * 2f / 3f) + side * amp; var p2 = A + d * (hl * 4f / 3f) - side * amp;
                foreach (var (a, b) in new[] { (A, p1), (p1, p2), (p2, B) }) Split(pieces, Line(a, b), 2);
            }
            else if (style == "straight") Split(pieces, Line(A, B), 6);
            else Split(pieces, new[] { A, A + d * (hl * 2f / 3f) + side * amp * 1.6f, B - d * (hl * 2f / 3f) - side * amp * 1.6f, B }, 6);
            float length = 0f;
            foreach (var p in pieces)
            {
                ctx.CurvedNet(p[0], p[1], p[2], p[3], NetRole.Pathway, pathAsset);
                var prev = p[0];
                for (int j = 1; j <= 6; j++)
                {
                    var q = Bez(p, j / 6f);
                    occ.TakeCorridor(prev, q, 1.5f, 100); occ.Paths.Add(prev, q, 1.5f, 100);
                    length += math.distance(prev, q); prev = q;
                }
            }
            // Spur from where people arrive to the nearest node (unless they arrive at an end).
            float best = float.MaxValue; float2 node = A;
            foreach (var p in pieces) foreach (var q in new[] { p[0], p[3] }) { float dd = math.distance(q, entry); if (dd < best) { best = dd; node = q; } }
            if (best > 1f) MonumentKit.Path(ctx, occ, entry, node, NetRole.Pathway, pathAsset);

            // Stations at an even rhythm along the walk, alternating sides.
            int wanted = ctx.I("sculptures");
            var samples = new List<(float2 p, float2 n)>();
            foreach (var p in pieces)
                for (int j = 0; j < 12; j++)
                {
                    float t = (j + 0.5f) / 12f;
                    var q = Bez(p, t); var tan = math.normalizesafe(BezD(p, t), d);
                    samples.Add((q, new float2(-tan.y, tan.x)));
                }
            float baseScale = ctx.F("size") / 100f;
            bool variety = ctx.B("variety");
            float pad = 2.1f + 0.4f * baseScale;
            string bush = ctx.B("backdrops") ? MonumentKit.Quiet("", AssetCategories.Hedge, AssetCategories.Bush) : null;
            int placed = 0, benches = 0;
            int stations = math.max(1, wanted);
            for (int i = 0; i < stations; i++)
            {
                ctx.Check();
                int si = (int)math.round((i + 0.5f) / stations * (samples.Count - 1));
                var (p, n) = samples[si];
                float s = (i & 1) == 0 ? 1f : -1f;
                // Pieces sit on the side with more room when the curve swings.
                var c0 = p + n * s * (1.5f + pad + 0.6f);
                if (!MonumentKit.Inside(site, c0, pad + 0.3f)) { s = -s; c0 = p + n * s * (1.5f + pad + 0.6f); }
                if (!MonumentKit.Inside(site, c0, pad * 0.6f) || !occ.Free(c0, pad * 0.8f, 0.3f, 0.2f)) continue;
                uint v = Hashing.Mix(ctx.Seed, 0x5C00u + (uint)i);
                float sc = baseScale * (variety ? 0.75f + 0.55f * Hashing.Unit(Hashing.Mix(v, 3u)) : 1f);
                if (ctx.B("pads")) ctx.Surface(LayerRegion.Circle(c0, pad, 16), SurfaceRole.Paving, AssetMix.One(ctx.S("pavingAsset")), AssetCategories.SurfacePaving);
                var o = ctx.Object(c0, MonumentKit.Yaw(p - c0), ObjectRole.Centerpiece, AssetMix.Pick(ids, v), v, -1, cat);
                o.Scale = sc;
                occ.Take(c0, pad * 0.8f, 0);
                placed++;
                if (bush != null)
                    for (int k = -2; k <= 2; k++)
                    {
                        float a = math.atan2(n.y * s, n.x * s) + k * 0.42f;
                        var q = c0 + new float2(math.cos(a), math.sin(a)) * (pad + 1.1f);
                        MonumentKit.Put(ctx, occ, q, a, bush == AssetCategories.Bush ? ObjectRole.Bush : ObjectRole.Furniture, new AssetRef(""), bush, 0.6f, Hashing.Mix(v, 0xB5u + (uint)(k + 2)), 1f, 0.2f, 0.1f);
                    }
                if (ctx.B("benches"))
                {
                    var q = p - n * s * (1.5f + 1.3f);
                    if (MonumentKit.Put(ctx, occ, q, MonumentKit.Yaw(c0 - q), ObjectRole.Bench, new AssetRef(""), null, 1f, Hashing.Mix(v, 0xBEu), 1f, 0.3f) != null) benches++;
                }
                if (ctx.B("lamps") && (i & 1) == 1)
                {
                    int lj = math.min(samples.Count - 1, si + 3);
                    var (lp, ln) = samples[lj];
                    MonumentKit.Put(ctx, occ, lp - ln * s * 2.2f, 0f, ObjectRole.Lamp, new AssetRef(""), AssetCategories.Lamp, 0.4f, Hashing.Mix(v, 0x1Au), 1f, 0.3f);
                }
            }
            if (placed < wanted) ctx.Issue(IssueSeverity.Info, K + "Issue.FewerSculptures", placed, wanted);
            if (ctx.B("trees") && AssetAvailability.Has(AssetCategories.Tree))
                for (int i = 0; i < samples.Count; i += 4)
                {
                    var (p, n) = samples[i];
                    for (int s = -1; s <= 1; s += 2)
                    {
                        var q = p + n * s * (hw * 0.85f);
                        if (MonumentKit.Inside(site, q, 2.2f)) MonumentKit.Put(ctx, occ, q, 0f, ObjectRole.Tree, new AssetRef(""), null, 2.2f, Hashing.Mix(ctx.Seed, 0x7E00u + (uint)(i * 2 + s + 1)), 1f, 0.5f, 0.6f);
                    }
                }
            ctx.Plan.Facts["sculptures"] = placed;
            ctx.Stat("Placemaker.Stat.Sculptures", placed, Keys.UnitCount);
            ctx.Stat(Keys.StatBenches, benches, Keys.UnitCount);
            ctx.Stat(Keys.StatPathLength, math.round(length), Keys.UnitM);
        }

        private static float2[] Line(float2 a, float2 b) => new[] { a, math.lerp(a, b, 1f / 3f), math.lerp(a, b, 2f / 3f), b };

        /// <summary>Splits a cubic Bézier into n exact pieces (de Casteljau at equal parameter steps).</summary>
        private static void Split(List<float2[]> into, float2[] c, int n)
        {
            var rest = c;
            for (int i = 0; i < n; i++)
            {
                if (i == n - 1) { into.Add(rest); break; }
                float t = 1f / (n - i);
                var ab = math.lerp(rest[0], rest[1], t); var bc = math.lerp(rest[1], rest[2], t); var cd = math.lerp(rest[2], rest[3], t);
                var abc = math.lerp(ab, bc, t); var bcd = math.lerp(bc, cd, t); var m = math.lerp(abc, bcd, t);
                into.Add(new[] { rest[0], ab, abc, m });
                rest = new[] { m, bcd, cd, rest[3] };
            }
        }

        private static float2 Bez(float2[] p, float t)
        {
            float u = 1f - t;
            return u * u * u * p[0] + 3f * u * u * t * p[1] + 3f * u * t * t * p[2] + t * t * t * p[3];
        }

        private static float2 BezD(float2[] p, float t)
        {
            float u = 1f - t;
            return 3f * u * u * (p[1] - p[0]) + 6f * u * t * (p[2] - p[1]) + 3f * t * t * (p[3] - p[2]);
        }
    }
}
