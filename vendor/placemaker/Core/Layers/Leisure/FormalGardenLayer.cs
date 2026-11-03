// Formal garden (French / Italian): perfectly symmetric about the long and the cross axis. Wide
// straight axes cross at a round point with a fountain basin; tree allées line the main axis;
// parterres — rectangular beds edged with low hedges, filled with flowerbeds in a grid and topiary
// at the corners — are laid out in one quadrant and mirrored into the other three (a bed is only
// built when all four mirror images fit, so the symmetry never breaks on an irregular outline).

using System.Collections.Generic;
using Placemaker.Core.Build;
using Placemaker.Core.Layers.AddOns.Monuments;
using Placemaker.Core.Layers.Common;
using Placemaker.Core.Layers.Park;
using Unity.Mathematics;

namespace Placemaker.Core.Layers.Leisure
{
    [AreaGenerator(55, Group = "gardens")]
    public sealed class FormalGardenLayer : LeisureLayer
    {
        protected override string Area => "Formal";
        public override string Id => "formalGarden";
        public override string Icon => "Media/Game/Icons/Landscaping.svg";
        public override float MinArea => 1500f;
        protected override int WantedEntrances(LayerRegion region) => 2;

        protected override List<FeatureInfo> BuildFeatures() => MonumentFeatures.With(new List<FeatureInfo>
        {
            Feature("restrooms"), Feature("shops", ("count", 1.0)), StreetShopsFeature(Area),
            Feature("decals", ("theme", "paving"), ("density", 0.5)), Feature("terrain", ("shape", "terraces"), ("height", 2.0), ("levels", 2.0)),
        });

        protected override void DefineControls(ControlSet c)
        {
            c.Slider("bedSize", 10, 5, 20, 1, "m", ControlTier.Essential, "parterres").Layout(5).Tip();
            c.Toggle("hedges", true, ControlTier.Essential, "parterres").Tip();
            c.Toggle("fountain", true, ControlTier.Essential, "parterres");
            c.Toggle("allees", true, ControlTier.Essential, "parterres").Tip();
            c.Toggle("topiary", true, ControlTier.More, "parterres");
            c.Slider("walks", 4.5, 2, 8, 0.5, "m", ControlTier.More, "parterres").Tip();
            c.Asset("hedgeAsset", AssetCategories.Hedge, ControlTier.More, "assets");
            c.AssetMulti("flowerAssets", AssetCategories.Flowerbed, ControlTier.More, "assets");
            c.Asset("fountainAsset", AssetCategories.CenterpieceFountain, ControlTier.More, "assets").When("fountain=true");
            c.Asset("treeAsset", AssetCategories.Tree, ControlTier.More, "assets");
            c.Asset("bushAsset", AssetCategories.Bush, ControlTier.More, "assets");
            c.Asset("benchAsset", AssetCategories.Bench, ControlTier.More, "assets");
            c.Asset("lampAsset", AssetCategories.Lamp, ControlTier.More, "assets");
            c.Asset("pathAsset", AssetCategories.Pathway, ControlTier.More, "assets");
            c.Toggle("fence", true, ControlTier.More, "boundary");
            c.Asset("fenceAsset", AssetCategories.Fence, ControlTier.More, "boundary").When("fence=true");
            EntranceMarkers(c);
            DefineStreetShopControls(c);
            c.RandomRange("bedSize", 7, 14);
        }

        protected override void DefinePresets()
        {
            Preset("french", ("bedSize", 12.0), ("allees", true), ("fountain", true), ("topiary", false));
            Preset("italian", ("bedSize", 9.0), ("allees", false), ("fountain", true), ("topiary", true));
            Preset("knot", ("bedSize", 6.0), ("hedges", true), ("allees", false), ("walks", 4.5));
        }

        protected override void PlanCore(LayerContext ctx)
        {
            var region = ctx.LayerRegion;
            var occ = new Occupancy(region, ctx.Input.Reserved, ctx.Site);
            var gates = Gates(ctx);
            Lawn.Cover(ctx, region.Ring, AssetMix.One(""));
            var pts = LeisureKit.Points(gates);
            LeisureKit.FrameToward(region, pts, out var centre, out var axis, out var side, out _, out _);
            region.DeepestPoint(out float ins, null, 12);
            var pathAsset = AssetMix.One(ctx.S("pathAsset"));

            // Axes (+ entrances joined straight), as the park's formal style. Thin paths (FEEDBACK-4).
            float keep = math.max(ctx.M(2f), ctx.EdgeMargin + 0.5f);
            var graph = ParkPathStyles.Formal(region, pts, centre, axis, side);
            var curves = ParkPaths.Build(graph, region, false, "narrow");
            // The axes end at the round point's edge (an axis between two opposite entrances would
            // otherwise run straight across the basin).
            float rp = math.clamp(ins * 0.22f, ctx.M(5f, 4f), ctx.M(12f, 5f));
            curves = ParkPlaces.ClipAndConnect(curves, LayerRegion.Circle(centre, rp, 24), region, out _);
            ParkPaths.Index(curves, occ.Corridors, 100);
            ParkPaths.Index(curves, occ.Paths, 100);
            ParkPaths.Emit(ctx, curves, gates, pathAsset);
            float axisHw = Composer.DefaultHalfWidth(NetRole.Pathway);
            // The main axis (the parterres' spine, where the cross walks join) must exist: the formal style
            // sometimes keeps only the cross axis.
            if (LeisureKit.Chord(region, centre, axis, keep + axisHw, out var m0, out var m1))
                foreach (var end in new[] { m0, m1 })
                {
                    var from = centre + math.normalizesafe(end - centre) * rp;
                    var mid = (from + end) * 0.5f;
                    if (math.distance(from, end) < 4f || occ.Paths.Overlaps(mid, 0.5f, 0f)) continue;
                    if (!region.CrossesBoundary(from, end, false)) LeisureKit.Path(ctx, occ, from, end, NetRole.Pathway, pathAsset);
                }

            // Round point with the basin (its paving is the plaza of the garden).
            ctx.Surface(LayerRegion.Circle(centre, rp, 24), SurfaceRole.Plaza, AssetMix.One(""), AssetCategories.SurfacePaving);
            occ.Take(centre, rp, 0);
            if (ctx.B("fountain"))
            {
                string cat = LeisureKit.Category(ctx, ctx.S("fountainAsset"), AssetCategories.CenterpieceFountain, AssetCategories.Centerpiece);
                if (cat != null) ctx.Object(centre, math.atan2(axis.x, axis.y), ObjectRole.Centerpiece, AssetMix.One(ctx.S("fountainAsset")), Hashing.Mix(ctx.Seed, 0xF0u), -1, cat);
            }
            ctx.Check();

            // Parterres: lattice in one quadrant, mirrored to all four. The walks between them are path nets
            // (FEEDBACK-4: no painted walks), so the gap is at least a path plus the hedges either side.
            float bed = ctx.F("bedSize");
            float walk = math.max(ctx.F("walks"), 2f * axisHw + 1.4f);
            float bl = bed, bw = bed * 0.6f;
            string hedgeCat = ctx.B("hedges") ? LeisureKit.Category(ctx, ctx.S("hedgeAsset"), AssetCategories.Hedge, AssetCategories.Bush) : null;
            string flowerCat = LeisureKit.Category(ctx, ctx.L("flowerAssets").Length > 0 ? "x" : "", AssetCategories.Flowerbed, AssetCategories.Bush);
            var flowers = ctx.L("flowerAssets");
            var hedge = AssetMix.One(ctx.S("hedgeAsset"));
            var bush = AssetMix.One(ctx.S("bushAsset"));
            float startU = rp + walk + bl * 0.5f, startV = axisHw + walk + (ctx.B("allees") ? 5f : 0f) + bw * 0.5f;
            int beds = 0, hedges = 0;
            var columns = new SortedDictionary<int, (float u, float vMax)>();
            int col = 0;
            for (float u = startU; u < ins * 4f; u += bl + walk, col++)
                for (float v = startV; v < ins * 4f; v += bw + walk)
                {
                    var quad = new OrientedRect[4];
                    bool all = true;
                    for (int m = 0; m < 4 && all; m++)
                    {
                        float su = (m & 1) == 0 ? 1f : -1f, sv = (m & 2) == 0 ? 1f : -1f;
                        quad[m] = new OrientedRect { Center = centre + axis * (su * u) + side * (sv * v), Axis = axis, HalfLength = bl * 0.5f, HalfWidth = bw * 0.5f };
                        all = LeisureKit.RectFits(region, occ, quad[m], 1f);
                    }
                    if (!all) continue;
                    columns[col] = (u, columns.TryGetValue(col, out var known) ? math.max(known.vMax, v) : v);
                    for (int m = 0; m < 4; m++)
                    {
                        var r = quad[m];
                        LeisureKit.TakeRect(occ, r);
                        uint vs = Hashing.Mix(ctx.Seed, 0xBED0u + (uint)beds);
                        // Ground before props (GroundCover): the parterre is a gravel bed with a lawn panel
                        // inside its low hedge — surfaces, not a flower every two metres.
                        ctx.Surface(r.Ring, SurfaceRole.Paving, AssetMix.One(""), SurfaceCategories.Gravel);
                        if (r.HalfLength > 2.2f && r.HalfWidth > 1.6f)
                        {
                            var panel = new OrientedRect { Center = r.Center, Axis = r.Axis, HalfLength = r.HalfLength - 1.1f, HalfWidth = r.HalfWidth - 1.1f };
                            ctx.Surface(panel.Ring, SurfaceRole.Grass, AssetMix.One(""), AssetCategories.SurfaceGrass);
                        }
                        if (hedgeCat != null)
                        {
                            var ring = r.Ring;
                            for (int k = 0; k < 4; k++)
                                hedges += GroundCover.HedgeRow(ctx, ring[k], ring[(k + 1) % 4], hedge, hedgeCat, ObjectRole.Bush, vs);
                        }
                        // One flowerbed in the middle of the panel (two on long beds) — the representative planting.
                        if (flowerCat != null)
                        {
                            int nf = r.HalfLength > 4.5f ? 2 : 1;
                            for (int k = 0; k < nf; k++)
                            {
                                float fu = nf == 1 ? 0f : (k == 0 ? -1f : 1f) * r.HalfLength * 0.45f;
                                ctx.Object(r.Local(fu, 0f), r.Yaw, flowerCat == AssetCategories.Bush ? ObjectRole.Bush : ObjectRole.Planter,
                                    AssetMix.Pick(flowers, vs), vs, -1, flowerCat);
                            }
                        }
                        // Topiary on the corners of the first beds round the basin only (representative, not every bed).
                        if (ctx.B("topiary") && col == 0)
                            foreach (var (cu, cv) in new[] { (-1f, -1f), (1f, 1f) })
                                ctx.Object(r.Local(cu * (r.HalfLength - 0.9f), cv * (r.HalfWidth - 0.9f)), 0f, ObjectRole.Bush, bush, Hashing.Mix(vs, 3u));
                        beds++;
                    }
                }
            if (beds == 0) ctx.Issue(IssueSeverity.Info, "Placemaker.Formal.Issue.NoParterres");

            // Cross walks: from the main axis out between the parterre columns (T-junctions on the axis; the
            // composer splits the axis where they end on it, so CS2 joins them).
            int walks = 0;
            foreach (var c in columns.Values)
            {
                float wu = c.u - bl * 0.5f - walk * 0.5f;
                foreach (int su in new[] { 1, -1 })
                    foreach (int sv in new[] { 1, -1 })
                    {
                        var a = centre + axis * (su * wu);
                        var b = a + side * (sv * (c.vMax + bw * 0.5f));
                        if (!region.ContainsDisc(b, 1f) || region.CrossesBoundary(a, b, false)) continue;
                        LeisureKit.Path(ctx, occ, a, b, NetRole.Pathway, pathAsset);
                        walks++;
                    }
            }
            ctx.Plan.Facts["crossWalks"] = walks;
            ctx.Check();

            // Allees: tree rows both sides of the main axis, mirrored (spacing is a layout distance; trees keep clear of the walks).
            var tree = AssetMix.One(ctx.S("treeAsset"));
            if (ctx.B("allees") && LeisureKit.Chord(region, centre, axis, 6f, out var a0, out var a1))
            {
                float off = axisHw + 3f;
                float half = math.distance(a0, a1) * 0.5f;
                for (float u = rp + 6f; u < half; u += ctx.M(8f, 5.5f))
                    foreach (int su in new[] { -1, 1 })
                        foreach (int sv in new[] { -1, 1 })
                        {
                            var p = centre + axis * (su * u) + side * (sv * off);
                            LeisureKit.Put(ctx, occ, p, 0f, ObjectRole.Tree, tree, null, 2.2f, Hashing.Mix(ctx.Seed, 0xA11Eu), 1f, 0.2f, 3, 3);
                        }
            }

            // Seats round the basin, then benches, lamps and bins beside the paths (never on the lawn).
            var bench = AssetMix.One(ctx.S("benchAsset"));
            for (int i = 0; i < 4; i++)
            {
                float a = math.atan2(axis.y, axis.x) + math.PI * 0.25f + i * math.PI * 0.5f;
                var p = centre + new float2(math.cos(a), math.sin(a)) * (rp + 1.8f);
                LeisureKit.Put(ctx, occ, p, LeisureKit.Yaw(p, centre), ObjectRole.Bench, bench, null, 1.2f, Hashing.Mix(ctx.Seed, 0xB0u + (uint)i));
            }
            PathLamps(ctx, occ, 16f, AssetMix.One(ctx.S("lampAsset")), 0x1A00u);
            ctx.Plan.Facts["pathBenches"] = PathBenches(ctx, occ, 30f, bench, 0xBE40u);
            ctx.Plan.Facts["pathBins"] = PathBins(ctx, occ, 70f, 0xB140u);
            ctx.Check();

            if (ctx.B("fence")) Fence(ctx, gates, AssetMix.One(ctx.S("fenceAsset")));
            EmitGreenRegions(ctx, occ, 100f);
            CommonStats(ctx);
            ctx.Stat("Placemaker.Stat.Parterres", beds, Keys.UnitCount);
        }
    }
}
