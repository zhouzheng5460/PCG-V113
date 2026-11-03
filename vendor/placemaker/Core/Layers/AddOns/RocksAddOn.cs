// Rocky outcrop feature (playtest 4b #3: "Rocky outcrops are supposed to be mainly terrain painting
// and not actual rocks evenly spaced out"). Each outcrop is mostly PAINTED ground: an organic main
// blob painted as soft nested rings (faint rim → full core, so the edge fades into the grass) plus a
// few small satellite blobs. On top sit a handful of rock props in tight, odd-numbered groups — one big
// anchor stone with smaller ones leaning against it — placed off-centre, never on a grid or spiral.
// Category rock (vanilla stones/boulders); the paint uses a map terrain layer (1–4, rock/gravel on most maps).

using System.Collections.Generic;
using Placemaker.Core.Geometry;
using Placemaker.Core.Layers.Common;
using Unity.Mathematics;

namespace Placemaker.Core.Layers.AddOns
{
    [AreaGenerator(145)]
    public sealed class RocksAddOn : AddOnLayer
    {
        public const string LayerId = "rocks";

        protected override string Area => "AddOn.Rocks";
        public override string Id => LayerId;
        public override string Icon => "Media/Game/Icons/PropsStone.svg";

        protected override void DefineControls(ControlSet c)
        {
            c.Slider("clusters", 3, 1, 12, 1, "count", ControlTier.Essential, "rocks");
            c.Choice("size", "medium", ControlTier.Essential, "rocks", "small", "medium", "large");
            c.AssetMulti("assets", AssetCategories.Rock, ControlTier.Essential, "rocks");
            c.Slider("rocks", 5, 1, 15, 1, "count", ControlTier.More, "rocks");
            c.Toggle("patch", true, ControlTier.More, "rocks").Tip();
            c.Slider("patchLayer", 1, 1, 4, 1, "count", ControlTier.More, "rocks").When("patch=true").Tip();
            c.Toggle("rim", true, ControlTier.More, "rocks");
            c.RandomRange("clusters", 2, 5);
        }

        protected override void DefinePresets()
        {
            Preset("scattered", ("clusters", 6.0), ("size", "small"), ("rocks", 3.0));
            Preset("outcrop", ("clusters", 2.0), ("size", "large"), ("rocks", 10.0));
        }

        /// <summary>Splits <paramref name="n"/> rocks into odd groups of at most 7, largest first (5 → 5, 6 → 5+1, 10 → 7+3).</summary>
        public static List<int> OddGroups(int n)
        {
            var groups = new List<int>();
            while (n > 0)
            {
                int g = math.min(n, 7);
                if ((g & 1) == 0) g--;
                groups.Add(g);
                n -= g;
            }
            return groups;
        }

        protected override void PlanCore(LayerContext ctx)
        {
            var ids = ctx.L("assets");
            if (!AssetAvailability.Require(ctx.Plan, AssetCategories.Rock, ids, Keys.Category(AssetCategories.Rock))) return;
            var occ = Occupancy(ctx);
            var region = ctx.LayerRegion;
            float R = ctx.S("size") == "small" ? 3.5f : ctx.S("size") == "large" ? 9f : 5.5f;
            int wanted = ctx.I("clusters");
            var spots = Spread(occ, R + 1f, wanted, Spots.Preference.Open, Hashing.Mix(ctx.Seed, 0x40C5u), R * 3f);
            int rocks = 0, clusters = 0;
            float rockR = AssetMix.MaxFootprint(ctx.Site, ids, 0.6f);
            bool paint = ctx.B("patch");
            int layer = math.clamp(ctx.I("patchLayer"), 1, 4);
            var groups = OddGroups(ctx.I("rocks"));
            foreach (var c in spots)
            {
                uint cs = Hashing.Mix(ctx.Seed, (uint)clusters * 977u + 3u);
                if (paint) PaintOutcrop(ctx, region, c, R, cs, layer);

                // Rock groups: the main (largest) group off-centre, the others towards the far side, each a
                // tight huddle round one big anchor stone.
                float baseAngle = Hashing.Unit(Hashing.Mix(cs, 11u)) * 6.283f;
                for (int g = 0; g < groups.Count; g++)
                {
                    uint gs = Hashing.Mix(cs, (uint)g * 131u + 7u);
                    float ga = baseAngle + (g == 0 ? 0f : math.PI * (0.7f + 0.6f * Hashing.Unit(gs)) + g * 0.9f);
                    float gd = R * (g == 0 ? 0.15f + 0.2f * Hashing.Unit(Hashing.Mix(gs, 1u)) : 0.45f + 0.25f * Hashing.Unit(Hashing.Mix(gs, 1u)));
                    var anchor = c + new float2(math.cos(ga), math.sin(ga)) * gd;
                    // Art pass a6: the automatic rock pool is all 1 m stones; the main huddle of a medium or
                    // large outcrop leans on a real vanilla boulder (4–5 m), so the outcrop has a mass.
                    bool boulder = g == 0 && ids.Length == 0 && ctx.S("size") != "small";
                    rocks += Group(ctx, occ, region, anchor, groups[g], g == 0 ? 1f : 0.75f, rockR, ids, gs, boulder);
                }

                // A few low plants at the rim (odd count, irregular).
                if (ctx.B("rim"))
                    for (int i = 0; i < 3; i++)
                    {
                        uint v = Hashing.Mix(cs, (uint)i + 50u);
                        float a = baseAngle + 1.2f + i * 1.9f + (Hashing.Unit(v) - 0.5f) * 1.1f;
                        var p = c + new float2(math.cos(a), math.sin(a)) * R * (0.8f + 0.35f * Hashing.Unit(Hashing.Mix(v, 1u)));
                        LeisureKit.Put(ctx, occ, p, a, ObjectRole.Bush, AssetMix.One(""), null, 1f, Hashing.Mix(cs, (uint)i + 60u));
                    }
                clusters++;
            }
            if (clusters == 0) NoSpace(ctx, "Placemaker.AddOn.Rocks.Name");
            ctx.Stat("Placemaker.Stat.Rocks", rocks, Keys.UnitCount);
        }

        /// <summary>
        /// One huddle: a big anchor stone and smaller stones leaning on it from one side (an arc, not a
        /// ring), sizes falling off. Returns how many rocks were placed.
        /// </summary>
        private static readonly string[] Boulders = { "Boulder01", "Boulder02", "Boulder03", "Boulder04", "Boulder05", "Boulder06", "Boulder07", "Boulder08" };

        private static int Group(LayerContext ctx, Occupancy occ, LayerRegion region, float2 anchor, int count, float sizeK, float rockR, string[] ids, uint seed,
            bool boulder = false)
        {
            int placed = 0;
            float anchorScale = (1.5f + 0.5f * Hashing.Unit(seed)) * sizeK;
            // A boulder anchor is ~2.4 m in radius at scale 1 (rocks ~0.6 m); the stones lean on its flank.
            float boulderScale = 0.6f + 0.3f * Hashing.Unit(Hashing.Mix(seed, 9u));
            float boulderR = 2.4f * boulderScale;
            float side = Hashing.Unit(Hashing.Mix(seed, 2u)) * 6.283f;
            for (int i = 0; i < count; i++)
            {
                uint v = Hashing.Mix(seed, (uint)i * 7919u + 13u);
                float scale;
                float2 p;
                if (i == 0 && boulder)
                {
                    if (region == null || !region.ContainsDisc(anchor, boulderR) || !occ.Free(anchor, boulderR, 0.3f, 0.05f)) { boulder = false; scale = anchorScale; p = anchor; }
                    else
                    {
                        var b = ctx.Object(anchor, Hashing.Unit(Hashing.Mix(v, 5u)) * 6.283f, ObjectRole.Furniture, AssetMix.Pick(Boulders, v), v, -1, AssetCategories.Rock);
                        b.Scale = boulderScale;
                        occ.Take(anchor, boulderR, 2);
                        placed++;
                        continue;
                    }
                }
                else if (i == 0) { scale = anchorScale; p = anchor; }
                else
                {
                    // Descending sizes with jitter; placed against the anchor within a ~220° arc.
                    scale = math.max(0.45f, anchorScale * (0.75f - 0.07f * i) * (0.8f + 0.4f * Hashing.Unit(v)));
                    float a = side + (Hashing.Unit(Hashing.Mix(v, 1u)) - 0.5f) * 3.8f;
                    float d = (boulder ? boulderR + rockR * scale * 0.6f + 0.4f : rockR * (anchorScale * 0.7f + scale * 0.6f)) * (0.85f + 0.45f * Hashing.Unit(Hashing.Mix(v, 2u)));
                    if (i > 4) d *= 1.5f;   // the last pebbles trail away from the huddle
                    p = anchor + new float2(math.cos(a), math.sin(a)) * d;
                }
                float r = rockR * scale * 0.6f;
                if (region == null || !region.ContainsDisc(p, r) || !occ.Free(p, r, 0.3f, 0.05f)) continue;
                var o = ctx.Object(p, Hashing.Unit(Hashing.Mix(v, 5u)) * 6.283f, ObjectRole.Furniture, AssetMix.Pick(ids, v), v, -1, AssetCategories.Rock);
                o.Scale = scale;
                occ.Take(p, r, 2);
                placed++;
            }
            return placed;
        }

        /// <summary>
        /// Painted ground of one outcrop: a main noisy blob as three nested rings (outline faint, core
        /// full, so the paint fades out) and 2–4 small satellite blobs. Everything stays inside the region.
        /// </summary>
        private static void PaintOutcrop(LayerContext ctx, LayerRegion region, float2 c, float R, uint seed, int layer)
        {
            var rings = new (float k, float strength)[] { (1.05f, 0.3f), (0.75f, 0.6f), (0.5f, 1f) };
            bool any = false;
            for (int i = 0; i < rings.Length; i++)
            {
                var ring = LeisureKit.Blob(c, R * rings[i].k, Hashing.Mix(seed, (uint)i + 300u), 18, 0.22f);
                if (!Inside(region, ring)) continue;
                ctx.Plan.Paint.Add(new PlannedPaint { Ring = Poly.To3D(ring, ctx.H), Layer = layer, Strength = rings[i].strength });
                any = true;
            }
            int satellites = 2 + (int)(Hashing.Mix(seed, 400u) % 3u);
            for (int s = 0; s < satellites; s++)
            {
                uint v = Hashing.Mix(seed, (uint)s + 410u);
                float a = Hashing.Unit(v) * 6.283f;
                float r = R * (0.22f + 0.2f * Hashing.Unit(Hashing.Mix(v, 1u)));
                var sc = c + new float2(math.cos(a), math.sin(a)) * R * (0.75f + 0.3f * Hashing.Unit(Hashing.Mix(v, 2u)));
                var outer = LeisureKit.Blob(sc, r, Hashing.Mix(v, 3u), 12, 0.3f);
                if (!Inside(region, outer)) continue;
                ctx.Plan.Paint.Add(new PlannedPaint { Ring = Poly.To3D(outer, ctx.H), Layer = layer, Strength = 0.35f });
                var core = LeisureKit.Blob(sc, r * 0.55f, Hashing.Mix(v, 4u), 10, 0.3f);
                ctx.Plan.Paint.Add(new PlannedPaint { Ring = Poly.To3D(core, ctx.H), Layer = layer, Strength = 0.7f });
                any = true;
            }
            if (!any) return;
            foreach (var i in ctx.Plan.Issues) if (i.Key == TerrainPaint.PermanentKey) return;
            ctx.Issue(IssueSeverity.Warning, TerrainPaint.PermanentKey);
        }

        private static bool Inside(LayerRegion region, float2[] ring)
        {
            if (region == null) return false;
            foreach (var p in ring) if (!region.Contains(p)) return false;
            return true;
        }
    }
}
