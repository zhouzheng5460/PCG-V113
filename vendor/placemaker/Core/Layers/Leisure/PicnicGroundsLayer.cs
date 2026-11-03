// Picnic grounds: a gentle loop path round a big open lawn; picnic groups scattered over the grass
// (tables in a small ring, a grill, a shade tree and a bin), shelters with tables beside the path,
// restrooms built in (one per 1.5 ha by default), trees in loose clumps that keep the middle open.

using System.Collections.Generic;
using Placemaker.Core.Layers.Common;
using Placemaker.Core.Layers.Park;
using Unity.Mathematics;

namespace Placemaker.Core.Layers.Leisure
{
    [AreaGenerator(53, Group = "parks")]
    public sealed class PicnicGroundsLayer : LeisureLayer
    {
        protected override string Area => "Picnic";
        public override string Id => "picnicGrounds";
        public override string Icon => "Media/Game/Icons/CityPark.svg";
        public override float MinArea => 1200f;

        protected override List<FeatureInfo> BuildFeatures() => new List<FeatureInfo>
        {
            CoreFeature("playground", ("size", "medium")), Feature("shops", ("count", 2.0), ("where", "paths")), StreetShopsFeature(Area),
            Feature("rocks", ("size", "medium")), Feature("decals", ("theme", "leaves")), Feature("terrain", ("shape", "mounds"), ("height", 1.5)),
        };

        protected override void DefineControls(ControlSet c)
        {
            c.Slider("groups", 10, 2, 40, 1, "count", ControlTier.Essential, "picnic");
            c.Slider("tables", 3, 1, 6, 1, "count", ControlTier.Essential, "picnic").Tip();
            c.Slider("shelters", 2, 0, 8, 1, "count", ControlTier.Essential, "picnic");
            c.Toggle("grills", true, ControlTier.Essential, "picnic").Tip();
            c.Slider("openLawn", 30, 0, 60, 5, "%", ControlTier.More, "picnic").Tip();
            c.Asset("tableAsset", AssetCategories.Picnic, ControlTier.More, "picnic");
            c.Asset("shelterAsset", AssetCategories.Shelter, ControlTier.More, "picnic");
            c.Toggle("facilities", true, ControlTier.More, "facilities").Tip();
            c.Asset("binAsset", AssetCategories.Bin, ControlTier.More, "facilities");
            c.Slider("treeDensity", 28, 0, 80, 5, "perHa", ControlTier.More, "planting");
            c.AssetMulti("treeSpecies", AssetCategories.Tree, ControlTier.More, "planting");
            c.Asset("pathAsset", AssetCategories.Pathway, ControlTier.More, "layout");
            EntranceMarkers(c);
            DefineStreetShopControls(c);
            c.RandomRange("groups", 5, 16);
            c.Fixed("facilities");
        }

        protected override void DefinePresets()
        {
            // Art pass (a1): families spread blankets on the lawn; the barbecue and shady grounds use picnic table sets.
            Preset("family", ("groups", 10.0), ("tables", 3.0), ("shelters", 2.0), ("grills", true), ("tableAsset", "PicnicBlanket01"));
            Preset("bbq", ("groups", 16.0), ("tables", 2.0), ("shelters", 4.0), ("grills", true), ("openLawn", 15.0));
            Preset("shady", ("groups", 8.0), ("treeDensity", 55.0), ("openLawn", 20.0), ("shelters", 0.0));
        }

        private static bool CircuitPath(LayerContext ctx, Occupancy occ, List<Gate> gates, AssetRef asset)
        {
            var region = ctx.LayerRegion;
            var pts = LeisureKit.Points(gates);
            float keep = math.max(ctx.M(3f), ctx.EdgeMargin + 1f);
            System.Func<float2, bool> ok = p => region.ContainsDisc(p, keep) && !occ.HitsPolygon(p, 2f);
            var graph = ParkPathStyles.Circuit(region, pts, ok);
            if (graph == null || graph.IsEmpty) return false;
            var curves = ParkPaths.Build(graph, region, true, "narrow");
            if (curves.Count == 0) return false;
            ParkPaths.Index(curves, occ.Corridors, 100);
            ParkPaths.Index(curves, occ.Paths, 100);
            ParkPaths.Emit(ctx, curves, gates, asset);
            ctx.Plan.Facts["circuit"] = 1;
            return true;
        }

        protected override void PlanCore(LayerContext ctx)
        {
            var region = ctx.LayerRegion;
            var occ = new Occupancy(region, ctx.Input.Reserved, ctx.Site);
            var gates = Gates(ctx);
            Lawn.Cover(ctx, region.Ring, AssetMix.One("", ParkArt.Grass));
            // Art pass (a1): the promised "gentle loop round a big open lawn" — a circuit walk inset from the edge with
            // the entrances joined to it; the generic planner (a teardrop to one side) only when no circuit fits.
            if (!CircuitPath(ctx, occ, gates, AssetMix.One(ctx.S("pathAsset"))))
                NaturalPaths(ctx, occ, gates, 1, 5f, 30f, "narrow", AssetMix.One(ctx.S("pathAsset")), true);

            // The open lawn in the middle (kept free of trees, used by a playground feature as core).
            var lawnCentre = region.DeepestPoint(out float ins, (q, cap) => occ.Clearance(q, cap), 14);
            float open = math.min(ins - 2f, math.sqrt(region.Area * ctx.F("openLawn") / 100f / math.PI));

            bool canTables = AssetAvailability.Require(ctx.Plan, AssetCategories.Picnic, new[] { ctx.S("tableAsset") }, Keys.Category(AssetCategories.Picnic));
            var table = AssetMix.One(ctx.S("tableAsset"), CampArt.Tables);
            // Blankets go straight on the grass in looser groups; table sets keep their ring.
            bool blankets = ctx.S("tableAsset").StartsWith("PicnicBlanket");
            int tables = 0, shelters = 0, groups = 0;

            // Shelters with tables beside the path.
            int wantedShelters = ctx.I("shelters");
            if (wantedShelters > 0 && canTables)
            {
                string cat = LeisureKit.Category(ctx, ctx.S("shelterAsset"), AssetCategories.Shelter);
                if (cat != null)
                {
                    var asset = AssetMix.One(ctx.S("shelterAsset"), CampArt.Shelter);
                    float r = AssetMix.FootprintRadius(ctx.Site, asset, 5f);
                    foreach (var a in PathAnchors.Pick(PathAnchors.Beside(occ, r, 8f, 1.5f), wantedShelters, 30f, false))
                    {
                        if (!occ.Free(a.Position, r, 0.5f, 0.3f)) continue;
                        uint v = Hashing.Mix(ctx.Seed, 0x5E10u + (uint)shelters);
                        float yaw = LeisureKit.Yaw(a.Position, a.Toward);
                        ctx.Object(a.Position, yaw, ObjectRole.Furniture, asset, v, -1, cat);
                        // Two tables under the roof (inside the shelter's own footprint).
                        var along = a.Tangent;
                        var roofTable = AssetMix.One("", CampArt.Tables);   // never a blanket under a roof
                        ctx.Object(a.Position + along * 1.6f, yaw, ObjectRole.Furniture, roofTable, Hashing.Mix(v, 1u), -1, AssetCategories.Picnic);
                        ctx.Object(a.Position - along * 1.6f, yaw, ObjectRole.Furniture, roofTable, Hashing.Mix(v, 2u), -1, AssetCategories.Picnic);
                        occ.Take(a.Position, r, 0);
                        tables += 2; shelters++;
                    }
                }
            }

            // Picnic groups over the grass, around (not in) the open lawn.
            string grill = ctx.B("grills") ? LeisureKit.Category(ctx, "", AssetCategories.CampingFire, AssetCategories.Picnic) : null;
            var bin = AssetMix.One(ctx.S("binAsset"), ParkArt.Bins);
            var treeIds = ctx.L("treeSpecies");
            if (treeIds.Length == 0) treeIds = PicnicArt.ShadeTrees;
            bool canTrees = AssetAvailability.Has(AssetCategories.Tree) || treeIds.Length > 0;
            if (canTables)
            {
                var avoid = new List<float2>();
                if (open > 6f) avoid.Add(lawnCentre);
                int wanted = ctx.I("groups"), perGroup = ctx.I("tables");
                for (int g = 0; g < wanted; g++)
                {
                    if (!Spots.Find(occ, 5.5f, Spots.Preference.Open, Hashing.Mix(ctx.Seed, 0x6400u + (uint)g), out var c, avoid, math.max(14f, open + 6f), 1.5f, budget: 1500)) break;
                    avoid.Add(c);
                    uint v = Hashing.Mix(ctx.Seed, 0x6500u + (uint)g);
                    float phase = Hashing.Unit(v) * 6.283f;
                    for (int k = 0; k < perGroup; k++)
                    {
                        float a = phase + k * 6.283f / perGroup;
                        var p = c + new float2(math.cos(a), math.sin(a)) * (perGroup == 1 ? 0f : blankets ? 3.2f : 2.8f);
                        if (LeisureKit.Put(ctx, occ, p, a + 1.57f, ObjectRole.Furniture, table, AssetCategories.Picnic, 1.3f, Hashing.Mix(v, (uint)k), 0.5f, 0.2f)) tables++;
                    }
                    if (grill != null) LeisureKit.Put(ctx, occ, c + new float2(math.cos(phase + 3.4f), math.sin(phase + 3.4f)) * 5f, phase, ObjectRole.Furniture, AssetMix.Pick(PicnicArt.Grills, v), grill, 0.8f, Hashing.Mix(v, 50u));
                    LeisureKit.Put(ctx, occ, c + new float2(math.cos(phase + 2.2f), math.sin(phase + 2.2f)) * 4.6f, 0f, ObjectRole.Bin, bin, null, 0.5f, Hashing.Mix(v, 51u));
                    if (canTrees) LeisureKit.Put(ctx, occ, c + new float2(math.cos(phase + 1.2f), math.sin(phase + 1.2f)) * 6.5f, phase, ObjectRole.Tree, AssetMix.Pick(treeIds, v), null, 2.5f, Hashing.Mix(v, 52u), 1f, 0.3f, 3, 3);
                    groups++;
                }
            }
            ctx.Check();

            // Restrooms: one per 1.5 ha near the paths (the restrooms feature replaces this).
            if (ctx.B("facilities") && !ctx.Input.HasChild("restrooms"))
            {
                string cat = LeisureKit.Category(ctx, "", AssetCategories.Restroom);
                if (cat != null)
                {
                    int wanted = math.max(1, (int)math.round(region.Area / 15000f));
                    int n = 0;
                    foreach (var a in PathAnchors.Pick(PathAnchors.Beside(occ, 4f, 8f, 1.2f), wanted, 40f, false))
                        if (LeisureKit.Put(ctx, occ, a.Position, LeisureKit.Yaw(a.Position, a.Toward), ObjectRole.Furniture, AssetMix.One("", ParkArt.Toilets), cat, 4f, Hashing.Mix(ctx.Seed, 0x7700u + (uint)n), 0.5f, 0.3f, 0)) n++;
                    ctx.Stat(Keys.StatRestrooms, n, Keys.UnitCount);
                }
            }

            // Loose tree clumps outside the open lawn.
            int trees = (int)(region.Area / 10000f * ctx.F("treeDensity"));
            if (trees > 0 && canTrees)
                LeisureKit.Plant(ctx, occ, region, trees, ObjectRole.Tree, treeIds, 2.5f, 22f, 0.3f, 0x7E10u, p => open > 6f && math.distance(p, lawnCentre) < open + 3f, 1, 3);

            EmitGreenRegions(ctx, occ);
            CommonStats(ctx);
            ctx.Stat(Keys.StatPicnicTables, tables, Keys.UnitCount);
            ctx.Plan.Facts["picnicGroups"] = groups;
            ctx.Plan.Facts["shelters"] = shelters;
        }
    }

    /// <summary>Art pass (a1): picnic defaults — broad shade trees and real grills (the "picnic" pool is all blankets).</summary>
    public static class PicnicArt
    {
        public static readonly string[] ShadeTrees = { "OakTree01", "EU_ChestnutTree01", "NA_LondonPlaneTree01", "NA_LindenTree01" };
        public static readonly string[] Grills = { "Grill01", "Grill02", "Grill03" };
    }
}
