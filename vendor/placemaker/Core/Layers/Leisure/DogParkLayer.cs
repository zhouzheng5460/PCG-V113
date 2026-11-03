// Dog park: a fenced run (or a big-dog and a small-dog run split by a fence), entered through
// double gates — a small fenced vestibule inside each entrance whose inner opening is offset from
// the outer one, so a dog can never run straight out. An agility course in the big run, a seating
// hub by the entrance, dog bins and a drinking post, shade trees along the fence, worn ground.

using System.Collections.Generic;
using Placemaker.Core.Layers.Common;
using Placemaker.Core.Layers.Park;
using Unity.Mathematics;

namespace Placemaker.Core.Layers.Leisure
{
    [AreaGenerator(52, Group = "parks")]
    public sealed class DogParkLayer : LeisureLayer
    {
        protected override string Area => "DogPark";
        public override string Id => "dogPark";
        public override string Icon => "Media/Game/Icons/Pet.svg";
        public override float MinArea => 500f;
        protected override int WantedEntrances(LayerRegion region) => region.Area > 6000f ? 2 : 1;

        protected override List<FeatureInfo> BuildFeatures() => new List<FeatureInfo>
        {
            Feature("restrooms"), Feature("shops", ("count", 1.0)), StreetShopsFeature(Area), Feature("rocks", ("size", "small"), ("clusters", 2.0)),
            Feature("decals", ("theme", "dirt"), ("density", 0.45)), Feature("terrain", ("shape", "mounds"), ("height", 1.0)),
        };

        protected override void DefineControls(ControlSet c)
        {
            c.Choice("runs", "one", ControlTier.Essential, "runs", "one", "two").Tip();
            c.Slider("agility", 6, 0, 14, 1, "count", ControlTier.Essential, "runs").Tip();
            c.Choice("ground", "mixed", ControlTier.Essential, "runs", "grass", "dirt", "mixed");
            c.Toggle("doubleGates", true, ControlTier.Essential, "runs").Tip();
            c.Slider("benches", 4, 0, 12, 1, "count", ControlTier.More, "runs");
            c.Toggle("amenities", true, ControlTier.More, "runs").Tip();
            c.Slider("shadeTrees", 30, 0, 80, 5, "perHa", ControlTier.More, "planting");
            c.AssetMulti("treeSpecies", AssetCategories.Tree, ControlTier.More, "planting");
            c.AssetMulti("agilityAssets", AssetCategories.DogAgility, ControlTier.More, "assets");
            c.Asset("fenceAsset", AssetCategories.Fence, ControlTier.More, "assets");
            c.Asset("benchAsset", AssetCategories.Bench, ControlTier.More, "assets");
            c.Asset("groundSurface", AssetCategories.SurfaceDirt, ControlTier.More, "assets");
            EntranceMarkers(c);
            DefineStreetShopControls(c);
            c.RandomRange("agility", 3, 8);
        }

        protected override void DefinePresets()
        {
            Preset("neighbourhood", ("runs", "one"), ("agility", 4.0), ("ground", "grass"));
            Preset("split", ("runs", "two"), ("agility", 6.0), ("ground", "mixed"));
            Preset("agility", ("runs", "one"), ("agility", 14.0), ("ground", "dirt"), ("benches", 6.0));
        }

        protected override void PlanCore(LayerContext ctx)
        {
            var region = ctx.LayerRegion;
            var occ = new Occupancy(region, ctx.Input.Reserved, ctx.Site);
            var gates = Gates(ctx);
            var fence = AssetMix.One(ctx.S("fenceAsset"), DogArt.Fence);

            // Ground.
            string ground = ctx.S("ground");
            if (ground == "grass") Lawn.Cover(ctx, region.Ring, AssetMix.One("", ParkArt.Grass));
            else if (ground == "dirt")
            {
                string cat = LeisureKit.Category(ctx, ctx.S("groundSurface"), AssetCategories.SurfaceDirt, AssetCategories.SurfaceNatural);
                ctx.Surface(region.Ring, SurfaceRole.Ground, AssetMix.One(ctx.S("groundSurface"), DogArt.Worn), cat ?? AssetCategories.SurfaceNatural);
            }
            else Lawn.Cover(ctx, region.Ring, AssetMix.One("", ParkArt.Grass));

            // Perimeter fence with openings at the entrances.
            Fence(ctx, gates, fence, 1.6f);

            // Double gates: a 5×5 m vestibule inside each entrance; the inner opening sits on a side wall.
            var hubs = new List<float2>();
            foreach (var g in gates)
            {
                var inward = region.InwardNormal(g.Segment);
                var along = new float2(inward.y, -inward.x);
                var c = g.Position + inward * 2.6f;
                if (ctx.B("doubleGates") && region.ContainsDisc(c, 2.6f))
                {
                    float h = 2.5f;
                    var p00 = g.Position - along * h; var p10 = g.Position + along * h;
                    var p01 = p00 + inward * 5f; var p11 = p10 + inward * 5f;
                    ctx.StraightNet(p00, p01, NetRole.Fence, fence);                 // left wall
                    ctx.StraightNet(p01, p11, NetRole.Fence, fence);                 // inner wall (closed)
                    ctx.StraightNet(p10, math.lerp(p10, p11, 0.3f), NetRole.Fence, fence);   // right wall, opening at its inner part
                    occ.Take(c, 3.2f, 5);
                    hubs.Add(c + inward * 7f + along * 4f);
                }
                else hubs.Add(g.Position + inward * 8f);
            }

            // Two runs: a divider across the long axis at 62 %, with an opening near the middle.
            LayerRegion bigRun = region;
            if (ctx.S("runs") == "two")
            {
                LeisureKit.Frame(region, out var centre, out var axis, out var side, out float major, out _);
                var at = centre + axis * (major * 0.12f);
                if (LeisureKit.Chord(region, at, side, 0f, out var a, out var b) && math.distance(a, b) > 12f)
                {
                    var mid = math.lerp(a, b, 0.5f); var d = math.normalizesafe(b - a);
                    ctx.StraightNet(a, mid - d * 1.5f, NetRole.Fence, fence);
                    ctx.StraightNet(mid + d * 1.5f, b, NetRole.Fence, fence);
                    occ.TakeCorridor(a, b, 0.6f, 5);
                    var pieces = Geometry.Poly.SplitByLine(region.Ring, at, side);
                    float best = 0f;
                    foreach (var piece in pieces)
                    {
                        var r = LayerRegion.From(piece);
                        if (r != null && r.Area > best) { best = r.Area; bigRun = r; }
                    }
                    ctx.Plan.Facts["runs"] = 2;
                }
            }

            // Seating hub(s) by the entrances.
            var bench = AssetMix.One(ctx.S("benchAsset"), ParkArt.Benches);
            int benches = 0, wantedBenches = ctx.I("benches");
            for (int i = 0; i < hubs.Count && benches < wantedBenches; i++)
                for (int k = 0; k < 3 && benches < wantedBenches; k++)
                {
                    var p = hubs[i] + new float2(math.cos(k * 2.1f), math.sin(k * 2.1f)) * 3f;
                    if (LeisureKit.Put(ctx, occ, p, LeisureKit.Yaw(p, region.Centroid), ObjectRole.Bench, bench, null, 1.3f, Hashing.Mix(ctx.Seed, 0xD0B0u + (uint)benches), 1f)) benches++;
                }
            for (int i = 0; benches < wantedBenches && i < 30; i++)
            {
                var b = region.PointAt(region.Perimeter * (i + 0.5f) / 30f, out _, out var inward, out _);
                var p = b + inward * 2.2f;
                if (LeisureKit.Put(ctx, occ, p, LeisureKit.Yaw(p, p + inward), ObjectRole.Bench, bench, null, 1.3f, Hashing.Mix(ctx.Seed, 0xD0C0u + (uint)i), 1f)) benches++;
            }

            // Amenities: a dog bin by each gate, a drinking post by the first hub.
            if (ctx.B("amenities"))
            {
                string cat = LeisureKit.Category(ctx, "", AssetCategories.DogAmenity, AssetCategories.Bin);
                if (cat != null)
                {
                    int n = 0;
                    foreach (var h in hubs)
                    {
                        var role = cat == AssetCategories.Bin ? ObjectRole.Bin : ObjectRole.Furniture;
                        var amenity = cat == AssetCategories.Bin ? AssetMix.One("", ParkArt.Bins) : AssetMix.One("", DogArt.Drinking);
                        if (LeisureKit.Put(ctx, occ, h + new float2(2.5f, -2.5f), 0f, role, amenity, cat == AssetCategories.Bin ? null : cat, 0.6f, Hashing.Mix(ctx.Seed, 0xB1Bu + (uint)n++), 1f)) { }
                        // and a bin for the bags beside it
                        LeisureKit.Put(ctx, occ, h + new float2(-2.5f, -2.5f), 0f, ObjectRole.Bin, AssetMix.One("", ParkArt.Bins), null, 0.5f, Hashing.Mix(ctx.Seed, 0xB1Cu + (uint)n), 1f);
                    }
                }
            }

            // Agility course: props on an oval in the deepest part of the big run.
            int agility = ctx.I("agility"), props = 0;
            var ids = ctx.L("agilityAssets");
            if (agility > 0 && AssetAvailability.Require(ctx.Plan, AssetCategories.DogAgility, ids, Keys.Category(AssetCategories.DogAgility)))
            {
                // Art pass (a1): the vanilla DogPark set (hoops, jumps, ramps, see-saw, platform, weave poles, tunnels)
                // in course order; the props are small (0.6–3 m), so the oval stays compact enough to read as a course.
                if (ids.Length == 0) ids = DogArt.Course;
                var c = bigRun.DeepestPoint(out float ins, (q, cap) => occ.Clearance(q, cap), 14);
                float rx = math.clamp(ins - 3f, 4f, math.min(16f, 4f + agility * 0.9f));
                float ry = rx * 0.6f;
                LeisureKit.Frame(bigRun, out _, out var ax, out var sd, out _, out _);
                for (int i = 0; i < agility * 2 && props < agility; i++)
                {
                    float a = i * 2f * math.PI / (agility * 2) * 2f;
                    var p = c + ax * math.cos(a) * rx + sd * math.sin(a) * ry;
                    var tangent = math.normalizesafe(-ax * math.sin(a) * rx + sd * math.cos(a) * ry);
                    uint v = Hashing.Mix(ctx.Seed, 0xA61u + (uint)i);
                    var prop = AssetMix.Pick(ids, (uint)props);   // in course order, not random
                    if (LeisureKit.Put(ctx, occ, p, math.atan2(tangent.x, tangent.y), ObjectRole.Furniture, prop, AssetCategories.DogAgility, 1.6f, v, 1f, 0.6f))
                        props++;
                }
                if (ground == "mixed")
                    ctx.Surface(LeisureKit.Blob(c, rx + 2f, Hashing.Mix(ctx.Seed, 0xD1Au), 18, 0.15f), SurfaceRole.Decorative, AssetMix.One(ctx.S("groundSurface"), DogArt.Worn),
                        AssetAvailability.Has(AssetCategories.SurfaceDirt) ? AssetCategories.SurfaceDirt : AssetCategories.SurfaceNatural);
            }
            if (ground == "mixed")
                foreach (var h in hubs)
                    ctx.Surface(LeisureKit.Blob(h, 5f, Hashing.Mix(ctx.Seed, (uint)(h.x * 7f)), 14, 0.2f), SurfaceRole.Decorative, AssetMix.One(ctx.S("groundSurface"), DogArt.Worn),
                        AssetAvailability.Has(AssetCategories.SurfaceDirt) ? AssetCategories.SurfaceDirt : AssetCategories.SurfaceNatural);

            // Shade trees along the fence.
            int trees = (int)(region.Area / 10000f * ctx.F("shadeTrees"));
            if (trees > 0 && AssetAvailability.Require(ctx.Plan, AssetCategories.Tree, ctx.L("treeSpecies"), Keys.Category(AssetCategories.Tree)))
                LeisureKit.Plant(ctx, occ, region, trees, ObjectRole.Tree, ctx.L("treeSpecies").Length > 0 ? ctx.L("treeSpecies") : DogArt.Trees, 2.5f, 15f, 0.5f, 0xD07u, p => region.BoundaryDistance(p, 12f) > 9f, 2, 3);
            // Shrubs in clumps along the fence soften the run (vanilla DogPark sets plant green bushes there).
            int shrubs = (int)(region.Area / 10000f * 60f);
            if (shrubs > 0 && AssetAvailability.Has(AssetCategories.Bush))
                LeisureKit.Plant(ctx, occ, region, shrubs, ObjectRole.Bush, ParkArt.GreenShrubs, 1f, 8f, 0.4f, 0xD08u, p => region.BoundaryDistance(p, 8f) > 5f, 1, 1);

            // Walkable inside the fence (people walk their dogs anywhere).
            ctx.NavigationArea(region.Ring, AreaRole.PedestrianArea, new AssetRef("", "Pedestrian Area"));
            EmitGreenRegions(ctx, occ);
            CommonStats(ctx);
            ctx.Stat("Placemaker.Stat.AgilityProps", props, Keys.UnitCount);
        }
    }

    /// <summary>Art pass (a1): vanilla dog park set (from the game's DogPark01/02 palettes).</summary>
    public static class DogArt
    {
        public static readonly string[] Course =
        {
            "DogParkHoop01", "DogParkJumpFence01", "DogParkRamp02", "DogParkPole01", "DogParkTube01", "DogParkSeesaw01",
            "DogParkJumpFence02", "DogParkPlatform01", "DogParkHoop02", "DogParkTube02", "DogParkRamp01",
        };
        public static readonly string[] Drinking = { "DogParkDrinkingFountain01" };
        /// <summary>A 1.2 m mesh fence (a dog-proof run), a low residential fence without it.</summary>
        public static readonly string[] Fence = { "FenceIndustrialLow01", "FenceResidentialLow02" };
        public static readonly string[] Worn = { "Terrain Surfaces Pack Ground gaga TTP - Soil M Surface", ParkArt.Forest };
        public static readonly string[] Trees = { "EU_ChestnutTree01", "NA_LindenTree01", "NA_LondonPlaneTree01", "EU_PoplarTree01" };
    }
}
