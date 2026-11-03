// Farm add-ons shared by the farmstead and the orchard park (and usable by any green type):
//  * apiary     — a block of beehives in a quiet sunny spot: rows, a cluster or a ring, an optional
//                 low fence with a gate gap, and bee-friendly flowers around it;
//  * farmStand  — a fruit / cider stand by the entrance or along a path: the stall, crates of produce
//                 stacked beside it, hay bales as a counter decoration, a couple of tables.
// Both place into free space only (Occupancy) and degrade to an Info issue when the category is empty.
// The urban farm's "honeyHives" and "farmStall" were merged into these two (g5); the old ids are aliases.

using System.Collections.Generic;
using Placemaker.Core.Layers.AddOns;
using Placemaker.Core.Layers.Common;
using Unity.Mathematics;

namespace Placemaker.Core.Layers.Types.Farmstead
{
    [AreaGenerator(171)]
    public sealed class ApiaryAddOn : AddOnLayer
    {
        public const string LayerId = "apiary";

        protected override string Area => "AddOn.Apiary";
        public override string Id => LayerId;
        public override string Icon => "Media/Game/Resources/Food.svg";
        public override string[] Accepts => new[] { RegionTags.Green, RegionTags.Core };

        protected override void DefineControls(ControlSet c)
        {
            c.Slider("hives", 8, 2, 30, 1, "count", ControlTier.Essential, "hives");
            c.Choice("layout", "rows", ControlTier.Essential, "hives", "rows", "cluster", "ring").Tip();
            c.Choice("where", "edge", ControlTier.Essential, "hives", "edge", "open", "centre").Tip();
            c.Toggle("fence", true, ControlTier.More, "hives");
            c.Toggle("flowers", true, ControlTier.More, "hives").Tip();
            c.Asset("hiveAsset", AssetCategories.Beehive, ControlTier.More, "hives");
            c.Asset("fenceAsset", AssetCategories.Fence, ControlTier.More, "hives").When("fence=true");
            c.RandomRange("hives", 4, 14);
        }

        protected override void DefinePresets()
        {
            Preset("smallholder", ("hives", 4.0), ("layout", "cluster"), ("fence", false));
            Preset("beeYard", ("hives", 16.0), ("layout", "rows"), ("fence", true));
        }

        protected override void PlanCore(LayerContext ctx)
        {
            var hiveAsset = AssetMix.One(ctx.S("hiveAsset"));
            // The urban farm's hive category is the fallback (honeyHives was merged into this feature).
            string cat = LeisureKit.Category(ctx, ctx.S("hiveAsset"), AssetCategories.Beehive, AssetCategories.CityFarmBeehive);
            if (cat == null) return;
            var occ = Occupancy(ctx);
            int wanted = ctx.I("hives");
            string layout = ctx.S("layout");
            // Footprint of the whole apiary: rows of hives 1.8 m apart, 3 m between rows.
            int perRow = math.max(2, (int)math.ceil(math.sqrt(wanted * 1.5f)));
            int rows = (int)math.ceil(wanted / (float)perRow);
            float radius = layout == "ring" ? math.max(3f, wanted * 1.9f / (2f * math.PI)) + 1.5f
                : layout == "cluster" ? math.sqrt(wanted) * 1.3f + 1.5f
                : math.max(perRow * 1.8f, rows * 3f) * 0.5f + 1.5f;
            var pref = ctx.S("where") == "open" ? Spots.Preference.Open : ctx.S("where") == "centre" ? Spots.Preference.Centre : Spots.Preference.NearBoundary;
            if (!Spots.Find(occ, radius + 1f, pref, Hashing.Mix(ctx.Seed, 0xA9u), out var c, null, 0f, 1f, 1500))
            {
                NoSpace(ctx, "Placemaker.AddOn.Apiary.Name");
                return;
            }
            // Hive entrances face the sunny side (south, -Z) and away from the nearest path.
            float yaw = math.PI;
            var spots = new List<float2>();
            if (layout == "ring")
            {
                float r = radius - 1.5f;
                for (int i = 0; i < wanted; i++)
                {
                    float a = i * 2f * math.PI / wanted;
                    spots.Add(c + new float2(math.cos(a), math.sin(a)) * r);
                }
            }
            else if (layout == "cluster")
            {
                var rng = Hashing.Rng(ctx.Seed, 0xC1u);
                for (int i = 0; i < wanted * 4 && spots.Count < wanted; i++)
                {
                    var p = c + rng.NextFloat2Direction() * math.sqrt(rng.NextFloat()) * (radius - 1.2f);
                    bool near = false;
                    foreach (var q in spots) if (math.distancesq(p, q) < 2.2f * 2.2f) { near = true; break; }
                    if (!near) spots.Add(p);
                }
            }
            else
            {
                for (int r = 0; r < rows; r++)
                    for (int k = 0; k < perRow && spots.Count < wanted; k++)
                        spots.Add(c + new float2((k - (perRow - 1) * 0.5f) * 1.8f, (r - (rows - 1) * 0.5f) * 3f));
            }
            int placed = 0;
            foreach (var p in spots)
            {
                uint v = Hashing.Mix(ctx.Seed, 0xB0u + (uint)placed);
                float y = layout == "ring" ? Facing(c, p) : yaw + (Hashing.Unit(v) - 0.5f) * 0.25f;
                if (LeisureKit.Put(ctx, occ, p, y, ObjectRole.Furniture, hiveAsset, cat, 0.55f, v, 0.5f, 0.2f)) placed++;
            }
            if (placed == 0) { NoSpace(ctx, "Placemaker.AddOn.Apiary.Name"); return; }
            occ.Take(c, radius - 1f, 0);

            if (ctx.B("fence"))
            {
                var fence = AssetMix.One(ctx.S("fenceAsset"));
                var toward = math.normalizesafe(ctx.LayerRegion.Centroid - c, new float2(0, -1));
                foreach (var run in Fences.Ring(c, radius + 0.6f, 10, toward, 2.5f))
                    if (ctx.LayerRegion.Contains(run.c0) && ctx.LayerRegion.Contains(run.c1))
                        ctx.StraightNet(run.c0, run.c1, NetRole.Fence, fence);
            }
            int flowers = 0;
            if (ctx.B("flowers"))
            {
                string fcat = LeisureKit.Category(ctx, "", AssetCategories.Flowerbed, AssetCategories.Bush);
                if (fcat != null)
                    for (int i = 0; i < 10; i++)
                    {
                        float a = i * 2f * math.PI / 10f + 0.3f;
                        var p = c + new float2(math.cos(a), math.sin(a)) * (radius + 2.4f);
                        var role = fcat == AssetCategories.Bush ? ObjectRole.Bush : ObjectRole.Planter;
                        if (LeisureKit.Put(ctx, occ, p, a, role, AssetMix.One(""), fcat, 1f, Hashing.Mix(ctx.Seed, 0xF1u + (uint)i), 0.5f, 0.2f)) flowers++;
                    }
            }
            if (placed < wanted) ctx.Issue(IssueSeverity.Info, "Placemaker.AddOn.Apiary.Issue.Fewer", placed, wanted);
            ctx.Stat("Placemaker.Stat.Beehives", placed, Keys.UnitCount);
            ctx.Stat(Keys.StatFlowerbeds, flowers, Keys.UnitCount);
        }
    }

    [AreaGenerator(172)]
    public sealed class FarmStandAddOn : AddOnLayer
    {
        public const string LayerId = "farmStand";

        protected override string Area => "AddOn.FarmStand";
        public override string Id => LayerId;
        public override string Icon => "Media/Game/Resources/Vegetables.svg";
        public override string[] Accepts => new[] { RegionTags.Green, RegionTags.Core };

        protected override void DefineControls(ControlSet c)
        {
            c.Slider("count", 1, 1, 4, 1, "count", ControlTier.Essential, "farmStand");
            c.Choice("where", "entrances", ControlTier.Essential, "farmStand", "entrances", "paths", "edges").Tip();
            c.Choice("sells", "fruit", ControlTier.Essential, "farmStand", "fruit", "cider", "veg", "honey");
            c.Toggle("crates", true, ControlTier.More, "farmStand").Tip();
            c.Toggle("hay", true, ControlTier.More, "farmStand");
            c.Toggle("seating", true, ControlTier.More, "farmStand");
            c.Asset("standAsset", AssetCategories.MarketStall, ControlTier.More, "farmStand");
            c.AssetMulti("crateAssets", AssetCategories.FruitCrate, ControlTier.More, "farmStand").When("crates=true");
            c.RandomRange("count", 1, 2);
        }

        protected override void DefinePresets()
        {
            Preset("honesty", ("count", 1.0), ("seating", false), ("hay", true), ("sells", "veg"));
            Preset("ciderBar", ("count", 1.0), ("seating", true), ("sells", "cider"));
            Preset("market", ("count", 3.0), ("where", "paths"), ("seating", true));
        }

        protected override void PlanCore(LayerContext ctx)
        {
            var standAsset = AssetMix.One(ctx.S("standAsset"));
            string standCat = LeisureKit.Category(ctx, ctx.S("standAsset"), AssetCategories.MarketStall, AssetCategories.Kiosk);
            if (standCat == null) return;
            var occ = Occupancy(ctx);
            int wanted = ctx.I("count");
            string where = ctx.S("where");
            float radius = AssetMix.FootprintRadius(ctx.Site, standAsset, 2.5f);
            var anchors = where == "edges" ? PathAnchors.Beside(new Occupancy(ctx.LayerRegion, null, ctx.Site), radius + 2f, 6f) : PathAnchors.Beside(occ, radius + 2f, 5f);
            anchors.RemoveAll(a => !occ.Free(a.Position, radius, 0.5f, 0.3f));
            var picks = PathAnchors.Pick(anchors, wanted, math.max(16f, math.sqrt(ctx.LayerRegion.Area) / (wanted + 1)), where == "entrances");
            var crateIds = ctx.L("crateAssets");
            string crateCat = ctx.B("crates") ? LeisureKit.Category(ctx, crateIds.Length > 0 ? "x" : "", AssetCategories.FruitCrate, AssetCategories.CityFarmProp, AssetCategories.FarmHay) : null;
            string hayCat = ctx.B("hay") ? LeisureKit.Category(ctx, "", AssetCategories.FarmHay) : null;
            string seatCat = ctx.B("seating") ? LeisureKit.Category(ctx, "", AssetCategories.CafeSeating, AssetCategories.Picnic, AssetCategories.Bench) : null;
            // Honey stands put a hive or two on show when the category exists.
            string hiveCat = ctx.S("sells") == "honey" && AssetAvailability.Has(AssetCategories.Beehive) ? AssetCategories.Beehive : null;
            int placed = 0, crates = 0, seats = 0;
            foreach (var a in picks)
            {
                uint v = Hashing.Mix(ctx.Seed, 0x57u + (uint)placed);
                float yaw = LeisureKit.Yaw(a.Position, a.Toward);
                if (!LeisureKit.Put(ctx, occ, a.Position, yaw, ObjectRole.Furniture, standAsset, standCat, radius, v, 0.5f, 0.3f, 0)) continue;
                placed++;
                var back = math.normalizesafe(a.Position - a.Toward, new float2(0, 1));
                // Crates stacked on both flanks of the stall, a little forward so they read as the display.
                if (crateCat != null)
                    for (int k = 0; k < 6; k++)
                    {
                        float side = (k & 1) == 0 ? 1f : -1f;
                        var p = a.Position + a.Tangent * side * (radius + 0.9f + (k >> 1) * 0.9f) - back * 0.6f;
                        if (LeisureKit.Put(ctx, occ, p, yaw + (k & 1) * 0.3f, ObjectRole.Furniture, AssetMix.Pick(crateIds, Hashing.Mix(v, (uint)k)), crateCat, 0.45f, Hashing.Mix(v, (uint)k), 0.4f, 0.1f)) crates++;
                    }
                if (hayCat != null)
                {
                    var p = a.Position + back * (radius + 1.4f) + a.Tangent * (radius * 0.5f);
                    LeisureKit.Put(ctx, occ, p, yaw + 0.4f, ObjectRole.Furniture, AssetMix.One(""), hayCat, 1f, Hashing.Mix(v, 0x4Au), 0.4f, 0.2f);
                }
                if (hiveCat != null)
                {
                    var p = a.Position + back * (radius + 1.2f) - a.Tangent * (radius * 0.6f);
                    LeisureKit.Put(ctx, occ, p, yaw, ObjectRole.Furniture, AssetMix.One(""), hiveCat, 0.6f, Hashing.Mix(v, 0x4Bu), 0.4f, 0.2f);
                }
                if (seatCat != null)
                    for (int k = -1; k <= 1; k += 2)
                    {
                        var p = a.Position + a.Tangent * k * (radius + 4.5f) + back * 1.5f;
                        var role = seatCat == AssetCategories.Bench ? ObjectRole.Bench : ObjectRole.Furniture;
                        if (LeisureKit.Put(ctx, occ, p, LeisureKit.Yaw(p, a.Position), role, AssetMix.One(""), seatCat, 1.4f, Hashing.Mix(v, (uint)(k + 9)))) seats++;
                    }
            }
            if (placed == 0) NoSpace(ctx, "Placemaker.AddOn.FarmStand.Name");
            else if (placed < wanted) ctx.Issue(IssueSeverity.Info, "Placemaker.AddOn.FarmStand.Issue.Fewer", placed, wanted);
            ctx.Stat("Placemaker.Stat.Kiosks", placed, Keys.UnitCount);
            ctx.Stat("Placemaker.Stat.Seats", seats, Keys.UnitCount);
            ctx.Plan.Facts["farmStandCrates"] = crates;
        }
    }
}
