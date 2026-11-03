// Obelisk: a tall needle (scaled up) on a stepped square base, centred on a paved court (square,
// diamond or round) with four matching corner pieces, benches facing it on the open sides, an
// optional low hedge ring with openings on the four axes and a flower ring round the base.
// No obelisk installed → a monument, then a statue, on the same base.

using Placemaker.Core.Layers.Common;
using Unity.Mathematics;

namespace Placemaker.Core.Layers.AddOns.Monuments
{
    [AreaGenerator(172)]
    public sealed class ObeliskAddOn : MonumentAddOn
    {
        public const string LayerId = "obelisk";
        protected override string Area => "AddOn.Obelisk";
        public override string Id => LayerId;

        protected override void DefineControls(ControlSet c)
        {
            c.Slider("size", 250, 100, 500, 25, "%", ControlTier.Essential, "obelisk").Tip();
            c.Asset("obeliskAsset", AssetCategories.MonumentObelisk, ControlTier.Essential, "obelisk");
            c.Slider("steps", 2, 0, 4, 1, "count", ControlTier.Essential, "obelisk").Tip();
            c.Choice("court", "square", ControlTier.Essential, "layout", "square", "diamond", "round");
            c.Choice("corners", "lamps", ControlTier.More, "layout", "lamps", "statues", "planters", "none").Tip();
            c.Toggle("hedgeRing", true, ControlTier.More, "layout").Tip();
            c.Toggle("flowers", true, ControlTier.More, "layout");
            c.Slider("benches", 3, 0, 4, 1, "count", ControlTier.More, "extras");
            c.Toggle("uplights", true, ControlTier.More, "extras");
            c.Asset("pavingAsset", AssetCategories.SurfacePaving, ControlTier.More, "ground");
            c.Asset("pathAsset", AssetCategories.Pathway, ControlTier.More, "ground");
            c.RandomRange("size", 150, 400);
        }

        protected override void DefinePresets()
        {
            Preset("needle", ("size", 350.0), ("steps", 3.0), ("court", "square"), ("corners", "lamps"), ("hedgeRing", false), ("flowers", false));
            Preset("garden", ("size", 200.0), ("steps", 1.0), ("court", "round"), ("corners", "planters"), ("hedgeRing", true), ("flowers", true));
            Preset("civic", ("size", 300.0), ("steps", 2.0), ("court", "diamond"), ("corners", "statues"), ("hedgeRing", true), ("benches", 4.0));
        }

        protected override void Build(LayerContext ctx, Occupancy occ, OrientedRect site, float2 toward)
        {
            string chosen = ctx.S("obeliskAsset");
            string cat = LeisureKit.Category(ctx, chosen, MonumentKit.ObeliskChain);
            if (cat == null) return;
            if (cat != AssetCategories.MonumentObelisk) ctx.Issue(IssueSeverity.Info, K + "Issue.NoObelisk");
            var region = ctx.LayerRegion;
            var asset = AssetMix.One(chosen);
            var C = site.Center;
            float half = math.min(site.HalfLength, site.HalfWidth) - 1f;
            if (half < 3f) { NoSpace(ctx, NameKey); return; }
            float scale = ctx.F("size") / 100f;
            float baseR = AssetMix.FootprintRadius(ctx.Site, asset, 1f) * scale;
            int steps = ctx.I("steps");
            float top = baseR + 0.6f, outer = top + steps * 0.9f;
            float maxOuter = half * 0.5f;
            if (outer > maxOuter) { float k = maxOuter / outer; scale *= k; top *= k; outer = maxOuter; ctx.Issue(IssueSeverity.Info, K + "Issue.ScaledDown", NameKey); }

            var entry = MonumentKit.Entry(occ, site, toward);
            var d = math.normalizesafe(entry - C, site.Axis);
            // Square courts align with the rectangle; the approach snaps to the nearest court axis.
            string court = ctx.S("court");
            var axis = court == "diamond" ? math.normalize(site.Axis + site.Side) : site.Axis;
            var ax2 = new float2(-axis.y, axis.x);
            var dirs = new[] { axis, ax2, -axis, -ax2 };
            int open = 0; float bestDot = -2f;
            for (int i = 0; i < 4; i++) { float dt = math.dot(dirs[i], d); if (dt > bestDot) { bestDot = dt; open = i; } }
            var face = dirs[open];

            float courtHalf = court == "diamond" ? half * 0.7f : half;
            float2[] ring = court == "round" ? LayerRegion.Circle(C, half, 32) : LayerRegion.Rect(C, axis, courtHalf, courtHalf);
            ctx.Surface(ring, SurfaceRole.Paving, AssetMix.One(ctx.S("pavingAsset")), AssetCategories.SurfacePaving);
            float rise = steps > 0 ? MonumentKit.Plinth(ctx, region, C, axis, outer, top, steps, 0.45f, 4) : 0f;
            ctx.Plan.Facts["plinthHeight"] = rise;
            var ob = ctx.Object(C, MonumentKit.Yaw(face), ObjectRole.Centerpiece, asset, Hashing.Mix(ctx.Seed, 0x0BE1u), -1, cat);
            ob.Scale = scale;
            occ.Take(C, outer + 0.3f, 0);
            ctx.Plan.Facts["obeliskScale"] = scale;

            // Path: entry → the court edge on the open axis.
            float edgeDist = court == "round" ? half : courtHalf;
            var gate = C + face * edgeDist;
            if (math.distance(entry, gate) > 1f) MonumentKit.Path(ctx, occ, entry, gate, NetRole.Pathway, AssetMix.One(ctx.S("pathAsset")));

            // Corners: four matching pieces on the diagonals.
            string corners = ctx.S("corners");
            if (corners != "none")
            {
                ObjectRole role = corners == "lamps" ? ObjectRole.Lamp : corners == "planters" ? ObjectRole.Planter : ObjectRole.Centerpiece;
                string cc = corners == "lamps" ? MonumentKit.Quiet("", AssetCategories.Lamp) : corners == "planters" ? MonumentKit.Quiet("", AssetCategories.Planter, AssetCategories.Bush)
                    : MonumentKit.Quiet("", MonumentKit.OrnamentChain);
                if (cc == AssetCategories.Bush) role = ObjectRole.Bush;
                var cornerAsset = new AssetRef("");
                if (role == ObjectRole.Centerpiece) cornerAsset = MonumentKit.OrnamentAsset(cc, out role);
                float cr = court == "round" ? half * 0.72f : courtHalf * 0.82f * math.SQRT2;
                if (cc != null)
                    for (int i = 0; i < 4; i++)
                    {
                        var dir = math.normalize(dirs[i] + dirs[(i + 1) % 4]);
                        MonumentKit.Put(ctx, occ, C + dir * cr, MonumentKit.Yaw(-dir), role, cornerAsset, cc, 0.7f, Hashing.Mix(ctx.Seed, 0xC0u + (uint)i));
                    }
            }
            if (ctx.B("flowers"))
            {
                string fc = MonumentKit.Quiet("", AssetCategories.Flowerbed, AssetCategories.Bush);
                if (fc != null)
                    for (int i = 0; i < 8; i++)
                    {
                        float a = i * math.PI / 4f + math.PI / 8f;
                        var q = C + new float2(math.cos(a), math.sin(a)) * (outer + 1.4f);
                        MonumentKit.Put(ctx, occ, q, a, fc == AssetCategories.Bush ? ObjectRole.Bush : ObjectRole.Planter, new AssetRef(""), fc, 0.6f, Hashing.Mix(ctx.Seed, 0xF10u + (uint)i));
                    }
            }
            int benches = ctx.I("benches"), placed = 0;
            for (int k = 1; k <= 3 && placed < benches; k++)
            {
                var dir = dirs[(open + k) % 4];
                var q = C + dir * (outer + (edgeDist - outer) * 0.55f);
                if (MonumentKit.Put(ctx, occ, q, MonumentKit.Yaw(-dir), ObjectRole.Bench, new AssetRef(""), null, 1.1f, Hashing.Mix(ctx.Seed, 0xBE0u + (uint)k)) != null) placed++;
            }
            if (placed < benches)
            {
                // Fourth bench: beside the approach, facing the needle.
                var q = C + face * (outer + (edgeDist - outer) * 0.55f) + new float2(-face.y, face.x) * 2.6f;
                if (MonumentKit.Put(ctx, occ, q, MonumentKit.Yaw(C - q), ObjectRole.Bench, new AssetRef(""), null, 1.1f, Hashing.Mix(ctx.Seed, 0xBE9u)) != null) placed++;
            }
            if (ctx.B("uplights"))
            {
                string lc = MonumentKit.Quiet("", AssetCategories.LampLow);
                if (lc != null)
                    for (int i = 0; i < 4; i++)
                        MonumentKit.Put(ctx, occ, C + dirs[i] * (outer + 0.5f) + dirs[(i + 1) % 4] * (outer * 0.5f), 0f, ObjectRole.Lamp, new AssetRef(""), lc, 0.3f, Hashing.Mix(ctx.Seed, 0x0B0u + (uint)i));
            }
            if (ctx.B("hedgeRing")) HedgeRing(ctx, occ, C, axis, court, half, courtHalf, dirs);
            ctx.Stat(Keys.StatBenches, placed, Keys.UnitCount);
            ctx.Stat("Placemaker.Stat.Monuments", 1, Keys.UnitCount);
        }

        /// <summary>Low hedge round the court just inside its edge, with an opening on each axis.</summary>
        private static void HedgeRing(LayerContext ctx, Occupancy occ, float2 C, float2 axis, string court, float half, float courtHalf, float2[] dirs)
        {
            string hc = MonumentKit.Quiet("", AssetCategories.Hedge, AssetCategories.Bush);
            if (hc == null) return;
            var role = hc == AssetCategories.Bush ? ObjectRole.Bush : ObjectRole.Furniture;
            int n = 0;
            if (court == "round")
            {
                float r = half - 0.8f;
                int count = math.max(8, (int)(2f * math.PI * r / 1.8f));
                for (int i = 0; i < count; i++)
                {
                    float a = i * 2f * math.PI / count;
                    var q = C + new float2(math.cos(a), math.sin(a)) * r;
                    bool gap = false;
                    foreach (var dd in dirs) if (math.distance(q, C + dd * r) < 2.2f) gap = true;
                    if (gap) continue;
                    if (MonumentKit.Put(ctx, occ, q, a, role, new AssetRef(""), hc, 0.7f, Hashing.Mix(ctx.Seed, 0x4ED0u + (uint)i), 1f, 0.2f, 0.1f) != null) n++;
                }
            }
            else
            {
                float r = courtHalf - 0.8f;
                for (int s = 0; s < 4; s++)
                {
                    var nrm = dirs[s]; var along = dirs[(s + 1) % 4];
                    for (float t = -r + 0.9f; t <= r - 0.9f; t += 1.8f)
                    {
                        if (math.abs(t) < 2.2f) continue;
                        var q = C + nrm * r + along * t;
                        if (MonumentKit.Put(ctx, occ, q, MonumentKit.Yaw(along), role, new AssetRef(""), hc, 0.7f, Hashing.Mix(ctx.Seed, 0x4ED0u + (uint)n), 1f, 0.2f, 0.1f) != null) n++;
                    }
                }
            }
            ctx.Plan.Facts["hedges"] = n;
        }
    }
}
