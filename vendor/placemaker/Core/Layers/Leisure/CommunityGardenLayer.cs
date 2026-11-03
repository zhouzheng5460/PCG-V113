// Community garden / allotments: access lanes parallel to the long axis (one per double row of
// plots) joined by a cross lane, plots on both sides of every lane — each a patch of worked soil
// (dirt surface) with crop rows, a shed at the back (per plot or shared), a communal hub by the
// entrance (shelter, picnic tables, water tank) and a fence round it all.

using System.Collections.Generic;
using Placemaker.Core.Layers.Common;
using Unity.Mathematics;

namespace Placemaker.Core.Layers.Leisure
{
    [AreaGenerator(56, Group = "gardens")]
    public sealed class CommunityGardenLayer : LeisureLayer
    {
        protected override string Area => "Allotments";
        public override string Id => "communityGarden";
        public override string Icon => "Media/Game/Icons/ZoneAgricultureArea.svg";
        public override float MinArea => 600f;
        protected override int WantedEntrances(LayerRegion region) => region.Area > 8000f ? 2 : 1;

        protected override List<FeatureInfo> BuildFeatures() => new List<FeatureInfo>
        {
            Feature("restrooms"), Feature("shops", ("count", 1.0), ("where", "entrances")), StreetShopsFeature(Area),
            Feature("decals", ("theme", "dirt"), ("density", 0.8)), Feature("terrain", ("shape", "flatten")),
        };

        protected override void DefineControls(ControlSet c)
        {
            c.Slider("plotWidth", 6, 3, 12, 0.5, "m", ControlTier.Essential, "plots").Layout(3);
            c.Slider("plotDepth", 10, 5, 20, 1, "m", ControlTier.Essential, "plots").Layout(5);
            c.Choice("crops", "normal", ControlTier.Essential, "plots", "sparse", "normal", "dense");
            c.Choice("sheds", "shared", ControlTier.Essential, "plots", "none", "shared", "each").Tip();
            c.AssetMulti("cropAssets", AssetCategories.GardenCrop, ControlTier.More, "plots");
            c.Asset("shedAsset", AssetCategories.GardenShed, ControlTier.More, "plots");
            c.Asset("plotSurface", AssetCategories.SurfaceCrop, ControlTier.More, "plots");
            c.Toggle("hub", true, ControlTier.More, "facilities").Tip();
            c.Toggle("fence", true, ControlTier.More, "boundary");
            c.Asset("fenceAsset", AssetCategories.Fence, ControlTier.More, "boundary").When("fence=true");
            c.Asset("pathAsset", AssetCategories.Pathway, ControlTier.More, "layout");
            EntranceMarkers(c);
            DefineStreetShopControls(c);
            c.RandomRange("plotWidth", 4, 8);
            c.RandomRange("plotDepth", 8, 14);
        }

        protected override void DefinePresets()
        {
            Preset("allotments", ("plotWidth", 6.0), ("plotDepth", 10.0), ("sheds", "each"), ("crops", "normal"));
            Preset("community", ("plotWidth", 4.0), ("plotDepth", 6.0), ("sheds", "shared"), ("hub", true), ("crops", "dense"));
            Preset("marketGarden", ("plotWidth", 12.0), ("plotDepth", 20.0), ("sheds", "none"), ("crops", "dense"));
        }

        /// <summary>Most sample plants over the whole garden: plots are surfaces (GroundCover).</summary>
        private const int MaxCropItems = 500;
        private const float CropsPerHa = 200f;
        private static readonly string[] PlotGround = { GroundCover.Rows, GroundCover.Soil, GroundCover.Rows, GroundCover.Green, GroundCover.Plowed };

        protected override void PlanCore(LayerContext ctx)
        {
            var region = ctx.LayerRegion;
            var occ = new Occupancy(region, ctx.Input.Reserved, ctx.Site);
            var gates = Gates(ctx);
            Lawn.Cover(ctx, region.Ring, AssetMix.One(""));
            var pathAsset = AssetMix.One(ctx.S("pathAsset"));
            LeisureKit.Frame(region, out var centre, out var axis, out var side, out _, out float minor);
            float w = ctx.F("plotWidth"), d = ctx.F("plotDepth");
            float hw = Composer.DefaultHalfWidth(NetRole.Pathway);
            float laneStep = 2f * d + 2f * hw + 1.5f;

            // Lanes along the axis and one cross lane through the centre.
            var lanes = new List<(float2 a, float2 b)>();
            int n = math.max(0, (int)math.floor(minor / laneStep / 2f));
            for (int k = -n; k <= n; k++)
                if (LeisureKit.Chord(region, centre + side * (k * laneStep), axis, 2f, out var a, out var b)) lanes.Add((a, b));
            if (lanes.Count == 0 && LeisureKit.Chord(region, centre, axis, 2f, out var a2, out var b2)) lanes.Add((a2, b2));
            var spine = new List<(float2 a, float2 b)>(lanes);
            if (lanes.Count > 1 && LeisureKit.Chord(region, centre, side, 2f, out var c0, out var c1)) spine.Add((c0, c1));
            foreach (var (a, b) in spine) LeisureKit.Path(ctx, occ, a, b, NetRole.Pathway, pathAsset);
            ConnectGates(ctx, occ, gates, spine, NetRole.Pathway, pathAsset);
            ctx.Check();

            // Communal hub by the first entrance (before the plots claim the space).
            if (ctx.B("hub") && gates.Count > 0)
            {
                var g = gates[0];
                var inward = region.InwardNormal(g.Segment);
                var along = new float2(inward.y, -inward.x);
                var hub = g.Position + inward * 8f + along * 7f;
                if (!occ.Free(hub, 5f, 1f)) hub = g.Position + inward * 8f - along * 7f;
                if (occ.Free(hub, 5f, 1f))
                {
                    string shelter = LeisureKit.Category(ctx, "", AssetCategories.Shelter);
                    if (shelter != null) ctx.Object(hub, LeisureKit.Yaw(hub, g.Position), ObjectRole.Furniture, AssetMix.One(""), Hashing.Mix(ctx.Seed, 0x40Bu), -1, shelter);
                    if (AssetAvailability.Has(AssetCategories.Picnic))
                    {
                        ctx.Object(hub + along * 1.6f, 0f, ObjectRole.Furniture, AssetMix.One(""), Hashing.Mix(ctx.Seed, 0x40Cu), -1, AssetCategories.Picnic);
                        ctx.Object(hub - along * 1.6f, 0f, ObjectRole.Furniture, AssetMix.One(""), Hashing.Mix(ctx.Seed, 0x40Du), -1, AssetCategories.Picnic);
                    }
                    occ.Take(hub, 5f, 0);
                    string tank = LeisureKit.Category(ctx, "", AssetCategories.GardenShed);
                    if (tank != null) LeisureKit.Put(ctx, occ, hub + inward * 6f, 0f, ObjectRole.Furniture, AssetMix.One(""), tank, 1.2f, Hashing.Mix(ctx.Seed, 0x40Eu));
                }
            }

            // Plots on both sides of each lane.
            var plots = new List<OrientedRect>();
            foreach (var (a, b) in lanes)
            {
                float len = math.distance(a, b);
                var dir = (b - a) / math.max(1e-3f, len);
                var nrm = new float2(-dir.y, dir.x);
                for (float s = w * 0.5f + 1f; s + w * 0.5f <= len - 1f; s += w + 0.8f)
                    foreach (float sideSign in new[] { 1f, -1f })
                    {
                        var c = a + dir * s + nrm * sideSign * (hw + 0.6f + d * 0.5f);
                        var r = new OrientedRect { Center = c, Axis = dir, HalfLength = w * 0.5f, HalfWidth = d * 0.5f };
                        if (!LeisureKit.RectFits(region, occ, r, 0.3f, 0.2f)) continue;
                        LeisureKit.TakeRect(occ, r);
                        plots.Add(r);
                    }
            }
            ctx.Check();

            // Ground before props (GroundCover): every plot is ONE surface — a patchwork of vegetable rows,
            // bare soil, leafy green and freshly dug beds — with a few sample plants along its lane-side
            // front and a shed at the back of every other plot. Never a plant per square metre.
            string chosenPlot = ctx.S("plotSurface");
            var cropIds = ctx.L("cropAssets");
            string cropCat = LeisureKit.Category(ctx, cropIds.Length > 0 ? "x" : "", AssetCategories.GardenCrop, AssetCategories.Planter, AssetCategories.Bush);
            string density = ctx.S("crops");
            int perPlot = density == "dense" ? 6 : density == "sparse" ? 2 : 4;
            float rowGap = density == "dense" ? 1.1f : density == "sparse" ? 2.2f : 1.5f;
            int budget = GroundCover.AccentBudget(region.Area, CropsPerHa, 30, MaxCropItems);
            string shedMode = ctx.S("sheds");
            string shedCat = shedMode == "none" ? null : LeisureKit.Category(ctx, ctx.S("shedAsset"), AssetCategories.GardenShed, AssetCategories.Shelter);
            var shed = AssetMix.One(ctx.S("shedAsset"));
            // Spread the budget evenly: fewer plants per plot (and plants on every k-th plot) on big gardens.
            if (plots.Count > 0) perPlot = math.clamp(budget / plots.Count, 1, perPlot);
            int plotStride = plots.Count > 0 ? math.max(1, (plots.Count * perPlot + budget - 1) / math.max(1, budget)) : 1;
            int crops = 0, sheds = 0;
            for (int i = 0; i < plots.Count; i++)
            {
                var r = plots[i];
                uint v = Hashing.Mix(ctx.Seed, 0x9100u + (uint)i);
                GroundCover.Field(ctx, r.Ring, PlotGround[(int)(v % (uint)PlotGround.Length)], chosenPlot);
                // Orientation: which side of the plot faces its lane (the back is the other side).
                float backSign = occ.Paths.Nearest(r.Center, 50f, out var q) >= 0 && math.dot(q - r.Center, r.Side) > 0f ? -1f : 1f;
                bool hasShed = shedCat != null && (shedMode == "each" ? i % 2 == 0 : i % 6 == 0);
                // A short front row of sample plants facing the lane (one variety per plot: patchwork).
                var crop = AssetMix.Pick(cropIds, v);
                if (cropCat != null && budget > 0 && i % plotStride == 0)
                {
                    float t = -backSign * (r.HalfWidth - 0.8f);
                    int k = 0;
                    for (float u = -r.HalfLength + 0.7f; u <= r.HalfLength - 0.7f && k < perPlot && budget > 0; u += rowGap, k++)
                    {
                        ctx.Object(r.Local(u, t), r.Yaw, cropCat == AssetCategories.Bush ? ObjectRole.Bush : cropCat == AssetCategories.Planter ? ObjectRole.Planter : ObjectRole.Furniture,
                            crop, v, -1, cropCat);
                        crops++; budget--;
                    }
                }
                if (hasShed)
                {
                    var p = r.Local(r.HalfLength - 1.4f, backSign * (r.HalfWidth - 1.4f));
                    ctx.Object(p, math.atan2(-backSign * r.Side.x, -backSign * r.Side.y), ObjectRole.Furniture, shed, Hashing.Mix(v, 9u), -1, shedCat);
                    sheds++;
                }
            }
            if (plots.Count == 0) ctx.Issue(IssueSeverity.Warning, "Placemaker.Allotments.Issue.NoPlots");

            if (ctx.B("fence")) Fence(ctx, gates, AssetMix.One(ctx.S("fenceAsset")), 2.5f);
            EmitGreenRegions(ctx, occ, 60f);
            CommonStats(ctx);
            ctx.Stat("Placemaker.Stat.Plots", plots.Count, Keys.UnitCount);
            ctx.Stat("Placemaker.Stat.Sheds", sheds, Keys.UnitCount);
            ctx.Plan.Facts["cropItems"] = crops;
        }
    }
}
