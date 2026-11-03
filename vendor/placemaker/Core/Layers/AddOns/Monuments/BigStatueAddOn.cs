// Big statue / monument: a statue scaled up (PlannedObject.Scale, honoured by the build's
// ObjectDefinition.m_Scale) on a stepped plinth (terraformed, blended), at the head of an axis.
//
//   axis      the rectangle's long axis; the approach comes from the end people arrive at (the host's
//             "Path to it") and runs as a wide walk to the foot of the plinth
//   plinth    2–4 square steps raised in the terrain; the paved court round it continues as a forecourt
//   avenue    paired lamps / trees / flags / small statues either side of the approach
//   kinds     a single statue, a group (the main figure with two lesser ones on their own pedestals),
//             or a colossus (twice the scale, a taller base)
//   backdrop  a half-ring of trees or hedge behind the plinth so the figure reads against green

using Placemaker.Core.Layers.Common;
using Unity.Mathematics;

namespace Placemaker.Core.Layers.AddOns.Monuments
{
    [AreaGenerator(171)]
    public sealed class BigStatueAddOn : MonumentAddOn
    {
        public const string LayerId = "bigStatue";
        protected override string Area => "AddOn.BigStatue";
        public override string Id => LayerId;

        protected override float2 Footprint(LayerContext ctx) => new float2(Spec.Length, Spec.Width) * (ctx.S("kind") == "colossus" ? 1.25f : 1f);

        protected override void DefineControls(ControlSet c)
        {
            c.Choice("kind", "statue", ControlTier.Essential, "statue", "statue", "group", "colossus").Tip();
            c.Slider("size", 250, 100, 500, 25, "%", ControlTier.Essential, "statue").Tip();
            c.Asset("statueAsset", AssetCategories.CenterpieceStatue, ControlTier.Essential, "statue");
            c.Toggle("plinth", true, ControlTier.Essential, "statue").Tip();
            c.Slider("steps", 3, 1, 4, 1, "count", ControlTier.More, "statue").When("plinth=true");
            c.Slider("stepRise", 0.5, 0.2, 1, 0.1, "m", ControlTier.More, "statue").When("plinth=true");
            c.Choice("focal", "end", ControlTier.More, "layout", "end", "centre").Tip();
            c.Choice("avenue", "lamps", ControlTier.More, "layout", "lamps", "trees", "flags", "statues", "none").Tip();
            c.Choice("backdrop", "trees", ControlTier.More, "layout", "trees", "hedge", "none");
            c.Toggle("paving", true, ControlTier.More, "ground");
            c.Asset("pavingAsset", AssetCategories.SurfacePaving, ControlTier.More, "ground").When("paving=true");
            c.Asset("pathAsset", AssetCategories.Pathway, ControlTier.More, "ground");
            c.Toggle("uplights", true, ControlTier.More, "extras");
            c.Slider("benches", 2, 0, 8, 1, "count", ControlTier.More, "extras");
            c.RandomRange("size", 150, 400);
            c.Chance("plinth", 0.85f);
        }

        protected override void DefinePresets()
        {
            Preset("hero", ("kind", "statue"), ("size", 250.0), ("steps", 3.0), ("avenue", "lamps"), ("backdrop", "trees"));
            Preset("colossus", ("kind", "colossus"), ("size", 450.0), ("steps", 4.0), ("stepRise", 0.6), ("avenue", "flags"), ("backdrop", "trees"), ("benches", 4.0));
            Preset("group", ("kind", "group"), ("size", 200.0), ("steps", 2.0), ("avenue", "statues"), ("backdrop", "hedge"));
            Preset("square", ("kind", "statue"), ("size", 200.0), ("focal", "centre"), ("steps", 2.0), ("avenue", "trees"), ("backdrop", "none"), ("benches", 6.0));
        }

        protected override void Build(LayerContext ctx, Occupancy occ, OrientedRect site, float2 toward)
        {
            string chosen = ctx.S("statueAsset");
            string cat = LeisureKit.Category(ctx, chosen, MonumentKit.StatueChain);
            if (cat == null) return;
            var region = ctx.LayerRegion;
            var asset = AssetMix.One(chosen);
            string kind = ctx.S("kind");
            float scale = ctx.F("size") / 100f * (kind == "colossus" ? 1.6f : 1f);
            // A stand-in (sculpture or fountain when no figure is installed) stays near life size: a
            // 4.5× fountain or bicycle sculpture is a landmark nobody asked for.
            if (string.IsNullOrEmpty(chosen) && !MonumentKit.IsFigure(cat)) scale = math.min(scale, 1.25f);
            float hl = site.HalfLength, hw = site.HalfWidth;

            // Size the figure to the room: base radius ≈ 1.3 m per unit of scale, the plinth round it.
            float baseR = AssetMix.FootprintRadius(ctx.Site, asset, 1.3f);
            bool plinth = ctx.B("plinth");
            int steps = plinth ? ctx.I("steps") : 0;
            float top = baseR * scale + 0.8f;
            float outer = top + steps * 1.1f;
            float room = math.min(hw - 2.5f, hl * 0.45f);
            if (outer > room)
            {
                float k = room / outer;
                scale *= k; top *= k; outer = room;
                ctx.Issue(IssueSeverity.Info, K + "Issue.ScaledDown", NameKey);
            }
            if (outer < 1.5f) { NoSpace(ctx, NameKey); return; }

            var entry = MonumentKit.Entry(occ, site, toward, true);
            float e = EndSign(site, entry);
            var d = site.Axis * e;                 // from the statue towards the visitors
            var side = new float2(-d.y, d.x);
            float u = ctx.S("focal") == "centre" ? 0f : -e * (hl - outer - 3f);
            var P = site.Local(u, 0f);

            // Ground: the plinth court and forecourt as one paved rectangle on the axis.
            float courtHalf = math.min(outer + 1.8f, hw - 0.4f);
            float frontRoom = math.dot(site.Local(e * hl, 0f) - P, d) - 0.4f;
            float backRoom = math.dot(P - site.Local(-e * hl, 0f), d) - 0.4f;
            float back = math.min(outer + 1.8f, backRoom);
            float front = math.min(outer + 1.8f + math.max(4f, hl * 0.25f), frontRoom);
            if (ctx.B("paving"))
            {
                var mid = P + d * (front - back) * 0.5f;
                var court = new OrientedRect { Center = mid, Axis = d, HalfLength = (front + back) * 0.5f, HalfWidth = courtHalf };
                ctx.Surface(court.Ring, SurfaceRole.Paving, AssetMix.One(ctx.S("pavingAsset")), AssetCategories.SurfacePaving);
            }

            float rise = 0f;
            if (steps > 0) rise = MonumentKit.Plinth(ctx, region, P, d, outer, top, steps, ctx.F("stepRise"), 4);
            ctx.Plan.Facts["plinthHeight"] = rise;

            // The figure faces the visitors.
            var statue = ctx.Object(P, MonumentKit.Yaw(d), ObjectRole.Centerpiece, asset, Hashing.Mix(ctx.Seed, 0x57A7u), -1, cat);
            statue.Scale = scale;
            occ.Take(P, outer, 0);
            ctx.Plan.Facts["statueScale"] = scale;
            int figures = 1;
            if (kind == "group")
                for (int s = -1; s <= 1; s += 2)
                {
                    var q = P + side * s * (outer + 1.6f) + d * 0.6f;
                    if (MonumentKit.Put(ctx, occ, q, MonumentKit.Yaw(d), ObjectRole.Centerpiece, asset, cat, 1.3f, Hashing.Mix(ctx.Seed, 0x6A0u + (uint)(s + 1)), scale * 0.55f) != null) figures++;
                }
            ctx.Plan.Facts["figures"] = figures;

            // Approach: entry → axis → foot of the plinth (a wide walk, nodes shared).
            var pathAsset = AssetMix.One(ctx.S("pathAsset"));
            var foot = P + d * (outer + 0.4f);
            float along = math.dot(entry - P, d), lat = math.dot(entry - P, side);
            var knee = P + d * math.max(along, outer + 3f);
            if (math.abs(lat) < 1.5f && along > outer + 1f) MonumentKit.Path(ctx, occ, entry, foot, NetRole.PathwayWide, pathAsset);
            else
            {
                MonumentKit.Path(ctx, occ, entry, knee, NetRole.PathwayWide, pathAsset);
                MonumentKit.Path(ctx, occ, knee, foot, NetRole.PathwayWide, pathAsset);
            }

            Avenue(ctx, occ, P, d, side, outer, frontRoom - 1f, asset, cat, scale);
            Backdrop(ctx, occ, P, d, outer);
            if (ctx.B("uplights"))
            {
                string lc = MonumentKit.Quiet("", AssetCategories.LampLow, AssetCategories.Lamp);
                if (lc != null)
                    for (int i = 0; i < 4; i++)
                    {
                        var q = P + (d * ((i & 1) == 0 ? 1 : -1) + side * ((i & 2) == 0 ? 1 : -1)) * (outer + 0.6f);
                        MonumentKit.Put(ctx, occ, q, 0f, ObjectRole.Lamp, new AssetRef(""), lc, 0.4f, Hashing.Mix(ctx.Seed, 0x0B0u + (uint)i));
                    }
            }
            int benches = ctx.I("benches"), placed = 0;
            var bench = AssetMix.One(ctx.S("benchAsset"));
            for (int i = 0; i < benches; i++)
            {
                int s = (i & 1) == 0 ? 1 : -1;
                float t = outer + 3f + (i / 2) * 3.5f;
                var q = P + d * t + side * s * math.min(courtHalf - 0.8f, 4.2f);
                if (MonumentKit.Put(ctx, occ, q, MonumentKit.Yaw(P - q), ObjectRole.Bench, bench, null, 1.1f, Hashing.Mix(ctx.Seed, 0xBE0u + (uint)i)) != null) placed++;
            }
            ctx.Stat(Keys.StatBenches, placed, Keys.UnitCount);
            ctx.Stat("Placemaker.Stat.Monuments", figures, Keys.UnitCount);
        }

        private void Avenue(LayerContext ctx, Occupancy occ, float2 P, float2 d, float2 side, float outer, float front, AssetRef statueAsset, string statueCat, float scale)
        {
            string kind = ctx.S("avenue");
            if (kind == "none") return;
            ObjectRole role; string cat; AssetRef asset = new AssetRef(""); float r; float sc = 1f;
            switch (kind)
            {
                case "trees": role = ObjectRole.Tree; cat = MonumentKit.Quiet("", AssetCategories.Tree); r = 2f; break;
                case "flags": role = ObjectRole.Furniture; cat = MonumentKit.Quiet("", AssetCategories.MemorialFlag, AssetCategories.Lamp); r = 0.6f; if (cat == AssetCategories.Lamp) role = ObjectRole.Lamp; break;
                case "statues": role = ObjectRole.Centerpiece; cat = statueCat; asset = statueAsset; r = 1.2f; sc = math.max(1f, scale * 0.4f); break;
                default: role = ObjectRole.Lamp; cat = MonumentKit.Quiet("", AssetCategories.Lamp); r = 0.5f; break;
            }
            if (cat == null) return;
            float off = Composer.DefaultHalfWidth(NetRole.PathwayWide) + (role == ObjectRole.Tree ? 3f : 1.4f);
            float spacing = role == ObjectRole.Tree ? 8f : 6f;
            int n = 0;
            for (float t = outer + 2.5f; t <= front; t += spacing)
            {
                // Pairs only: both sides or neither (symmetry is the point of an avenue).
                var a = P + d * t + side * off; var b = P + d * t - side * off;
                if (!occ.Free(a, r, 0.3f) || !occ.Free(b, r, 0.3f)) continue;
                if (MonumentKit.Put(ctx, occ, a, MonumentKit.Yaw(-side), role, asset, cat, r, Hashing.Mix(ctx.Seed, 0xA7E0u + (uint)n), sc, 0.3f) != null) n++;
                if (MonumentKit.Put(ctx, occ, b, MonumentKit.Yaw(side), role, asset, cat, r, Hashing.Mix(ctx.Seed, 0xA7E0u + (uint)n), sc, 0.3f) != null) n++;
            }
            ctx.Plan.Facts["avenue"] = n;
        }

        private void Backdrop(LayerContext ctx, Occupancy occ, float2 P, float2 d, float outer)
        {
            string kind = ctx.S("backdrop");
            if (kind == "none") return;
            bool trees = kind == "trees";
            string cat = trees ? MonumentKit.Quiet("", AssetCategories.Tree) : MonumentKit.Quiet("", AssetCategories.Hedge, AssetCategories.Bush);
            if (cat == null) return;
            float r = outer + (trees ? 4.5f : 2.8f);
            int count = trees ? 7 : 13;
            float baseAngle = math.atan2(-d.y, -d.x);
            for (int i = 0; i < count; i++)
            {
                float a = baseAngle + (i / (float)(count - 1) - 0.5f) * math.radians(150f);
                var q = P + new float2(math.cos(a), math.sin(a)) * r;
                var role = trees ? ObjectRole.Tree : cat == AssetCategories.Bush ? ObjectRole.Bush : ObjectRole.Furniture;
                MonumentKit.Put(ctx, occ, q, a + math.PI / 2f, role, new AssetRef(""), cat, trees ? 2f : 0.9f, Hashing.Mix(ctx.Seed, 0xBAC0u + (uint)i), 1f, 0.4f);
            }
        }
    }
}
