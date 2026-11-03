// Rest area: a stop beside the road. Composition, from the road inwards:
//   1. a car park on the frontage at the main entrance (the Parking layer's planner on a cut of the
//      outline — truck bays, EV chargers and stall angles are its own options),
//   2. the amenity hub where people step out of the car: a paved court with the restroom block,
//      kiosks / vending, a map board, benches and bins (a fuel island at a truck stop, a charging
//      lounge at an EV stop),
//   3. a footpath spine from the hub to the far end, with picnic spots and shelters along it,
//   4. a fenced dog walk tucked against the outline, off a branch path,
//   5. at the far end a viewpoint (lookout, bench arc, optional raised mound blended into the ground),
//   6. a tree belt screening the outline and loose shade clumps in the grove.
// Subtypes are presets: highway rest stop, picnic stop, truck stop, scenic overlook, EV charging stop.

using System.Collections.Generic;
using Placemaker.Core.Geometry;
using Placemaker.Core.Layers.Common;
using Placemaker.Core.Layers.Leisure;
using Unity.Mathematics;

namespace Placemaker.Core.Layers.Types.Transport
{
    [AreaGenerator(90, Group = "transport")]
    public sealed class RestAreaLayer : LeisureLayer
    {
        /// <summary>Vehicle / non-park site: plain entrances, no pedestrian/bike/main kinds, never blocks Build.</summary>
        protected override bool TypedEntrances => false;

        protected override string Area => "RestArea";
        public override string Id => "restArea";
        public override string Icon => "Media/Game/Icons/Highways.svg";
        public override float MinArea => 1500f;
        public override string[] Emits => new[] { RegionTags.Green, RegionTags.Core };
        protected override bool HostsStreetShops => false;

        public static readonly string[] Styles = { "highway", "picnic", "truck", "overlook", "ev" };

        protected override List<FeatureInfo> BuildFeatures() => new List<FeatureInfo>
        {
            CoreFeature("playground", ("size", "small")), Feature("shops", ("count", 1.0), ("where", "paths")), Labelled(Feature("bigtrees"), "Placemaker.Park.Feature.bigtrees"),
            Feature("rocks", ("size", "small")), Labelled(Feature("flowerbeds", ("density", 4.0)), "Placemaker.Park.Feature.flowerbeds"), Feature("decals", ("theme", "leaves")),
            Feature("terrain", ("shape", "flatten")),
        };

        private static FeatureInfo Labelled(FeatureInfo f, string key) { f.LabelKey = key; return f; }

        protected override void DefineControls(ControlSet c)
        {
            // Essentials: what kind of stop and how much of it is car park.
            c.Choice("style", "highway", ControlTier.Essential, "layout", Styles).Tip();
            c.Slider("lotShare", 30, 0, 70, 5, "%", ControlTier.Essential, "layout").Tip();
            c.Choice("stallAngle", "90", ControlTier.Essential, "layout", "90", "60", "45");
            c.Toggle("restrooms", true, ControlTier.Essential, "facilities");
            c.Slider("groups", 6, 0, 30, 1, "count", ControlTier.Essential, "picnic").Tip();

            c.Choice("lotGreen", "edges", ControlTier.More, "spaces", "none", "edges", "medians").Tip();
            c.Toggle("truckBays", false, ControlTier.More, "spaces").Tip();
            c.Slider("evShare", 4, 0, 30, 1, "%", ControlTier.More, "spaces").Tip();
            c.Slider("accessible", 2, 0, 10, 1, "%", ControlTier.More, "spaces");

            c.Slider("hubSize", 24, 14, 44, 2, "m", ControlTier.More, "facilities").Layout(12).Tip();
            c.Slider("kiosks", 1, 0, 4, 1, "count", ControlTier.More, "facilities");
            c.Slider("vending", 2, 0, 8, 1, "count", ControlTier.More, "facilities").Tip();
            c.Slider("fuelPumps", 0, 0, 8, 1, "count", ControlTier.More, "facilities").Tip();
            c.Toggle("signs", true, ControlTier.More, "facilities").Tip();

            c.Slider("tables", 2, 1, 4, 1, "count", ControlTier.More, "picnic").Tip();
            c.Slider("shelters", 2, 0, 8, 1, "count", ControlTier.More, "picnic");
            c.Toggle("grills", false, ControlTier.More, "picnic").Tip();

            c.Toggle("dogRun", true, ControlTier.More, "dogs").Tip();
            c.Slider("dogRunSize", 22, 12, 40, 2, "m", ControlTier.More, "dogs").Layout(12).When("dogRun=true");

            c.Toggle("viewpoint", false, ControlTier.More, "views").Tip();
            c.Slider("moundHeight", 0, 0, 8, 0.5, "m", ControlTier.More, "views").When("viewpoint=true").Tip();
            c.Slider("viewBenches", 5, 0, 12, 1, "count", ControlTier.More, "views").When("viewpoint=true");

            c.Slider("treeDensity", 25, 0, 80, 5, "perHa", ControlTier.More, "planting");
            c.Toggle("screen", true, ControlTier.More, "planting").Tip();
            c.AssetMulti("treeSpecies", AssetCategories.Tree, ControlTier.More, "planting");

            c.Toggle("lamps", true, ControlTier.More, "lighting");
            c.Slider("lampSpacing", 24, 12, 40, 2, "m", ControlTier.More, "lighting").Layout(8).When("lamps=true");

            c.Asset("pathAsset", AssetCategories.Pathway, ControlTier.More, "assets");
            c.Asset("tableAsset", AssetCategories.Picnic, ControlTier.More, "assets");
            c.Asset("shelterAsset", AssetCategories.Shelter, ControlTier.More, "assets");
            c.Asset("kioskAsset", AssetCategories.Kiosk, ControlTier.More, "assets");
            c.Asset("benchAsset", AssetCategories.Bench, ControlTier.More, "assets");
            c.Asset("lampAsset", AssetCategories.Lamp, ControlTier.More, "assets");
            c.Asset("fenceAsset", AssetCategories.Fence, ControlTier.More, "assets");
            EntranceMarkers(c);

            c.RandomRange("lotShare", 15, 50);
            c.RandomRange("groups", 3, 14);
            c.RandomRange("moundHeight", 0, 4);
            c.Fixed("style");
            c.Fixed("restrooms");
        }

        protected override void DefinePresets()
        {
            Preset("highway", ("style", "highway"));
            Preset("picnic", ("style", "picnic"), ("lotShare", 15.0), ("groups", 14.0), ("tables", 3.0), ("shelters", 4.0), ("grills", true),
                ("kiosks", 0.0), ("vending", 0.0), ("treeDensity", 40.0), ("lotGreen", "medians"));
            Preset("truck", ("style", "truck"), ("lotShare", 55.0), ("truckBays", true), ("fuelPumps", 4.0), ("kiosks", 2.0), ("vending", 4.0),
                ("groups", 3.0), ("dogRun", false), ("lotGreen", "none"), ("hubSize", 30.0), ("treeDensity", 15.0));
            Preset("overlook", ("style", "overlook"), ("lotShare", 20.0), ("viewpoint", true), ("moundHeight", 4.0), ("viewBenches", 8.0),
                ("screen", false), ("groups", 4.0), ("dogRun", false), ("vending", 0.0), ("treeDensity", 15.0));
            Preset("ev", ("style", "ev"), ("lotShare", 35.0), ("evShare", 30.0), ("stallAngle", "60"), ("kiosks", 1.0), ("vending", 3.0),
                ("lotGreen", "medians"), ("hubSize", 28.0));
        }

        private const uint SaltLot = 0xA1u;

        protected override void PlanCore(LayerContext ctx)
        {
            var full = ctx.LayerRegion;
            string style = ctx.S("style");
            var gates = Gates(ctx);
            var g0 = gates.Count > 0 ? gates[0] : new Gate { Position = full.Ring[0], Segment = 0 };
            var tangent = math.normalizesafe(full.Ring[(g0.Segment + 1) % full.Ring.Length] - full.Ring[g0.Segment], new float2(1, 0));
            var inward = full.InwardNormal(g0.Segment);
            var pathAsset = AssetMix.One(ctx.S("pathAsset"));

            // ── 1. Car park on the frontage at the main entrance ──
            SubLot lot = null;
            float2 walkIn = g0.Position;
            float share = ctx.F("lotShare") / 100f;
            if (share > 0f)
            {
                float want = full.Area * share;
                float depth = math.clamp(math.sqrt(want / 1.5f), 22f, 110f);
                float width = math.clamp(want / depth, 26f, 400f);
                var cut = LayerRegion.Rect(g0.Position + inward * (depth * 0.5f - 2f), tangent, width * 0.5f, depth * 0.5f + 2f);
                lot = TransportKit.PlanLot(ctx, cut, g0.Position, ParkingSettings(ctx), SaltLot);
                if (lot != null)
                {
                    var rest = TransportKit.Subtract(full.Ring, cut, MinArea * 0.3f);
                    var restRegion = rest == null ? null : LayerRegion.From(rest);
                    if (restRegion != null)
                    {
                        ctx.LayerRegion = restRegion;
                        walkIn = TransportKit.FreeEdgePoint(restRegion, g0.Position + inward * depth, ctx.Plan, 3.5f);
                    }
                }
            }
            var region = ctx.LayerRegion;
            var occ = new Occupancy(region, ctx.Input.Reserved, ctx.Site);
            if (lot != null) TransportKit.Occupy(occ, ctx.Plan);
            ctx.Check();

            // ── 2. Far point: viewpoint / end of the spine ──
            bool view = ctx.B("viewpoint") || style == "overlook";
            float moundR = view && ctx.F("moundHeight") > 0f ? math.clamp(math.sqrt(region.Area) * 0.18f, 14f, 30f) : 7f;
            if (!TransportKit.FarSpot(occ, walkIn, moundR, out var far) && !TransportKit.FarSpot(occ, walkIn, 4f, out far)) far = region.Centroid;
            moundR = math.min(moundR, math.max(0f, occ.Clearance(far, moundR)));

            // ── 3. Footpath spine and side entrances ──
            // The spine ends at the edge of the viewpoint platform, not in its middle.
            var spineEnd = far + math.normalizesafe(walkIn - far) * 7f;
            if (!region.ContainsDisc(spineEnd, 1f) || math.distance(walkIn, far) < 16f) spineEnd = far;
            TransportKit.Route(ctx, occ, walkIn, spineEnd, pathAsset);
            for (int i = 1; i < gates.Count; i++)
            {
                var g = gates[i].Position;
                if (lot != null && Poly.Contains(lot.Ring, g)) continue;
                if (region.BoundaryDistance(g, 2f) > 1.5f) continue;
                if (NearestOnPaths(occ, g, out var foot)) TransportKit.Route(ctx, occ, g, foot, pathAsset);
            }
            ctx.Check();

            // ── 4. Amenity hub at the walk-in point ──
            int restrooms = 0, kiosks = 0, vend = 0, signs = 0, fuel = 0;
            Hub(ctx, occ, walkIn, inward, tangent, style, ref restrooms, ref kiosks, ref vend, ref signs, ref fuel);
            ctx.Check();

            // ── 5. Dog walk ──
            int dogRuns = 0;
            var avoid = new List<float2> { walkIn, far };
            if (ctx.B("dogRun")) dogRuns = DogRun(ctx, occ, avoid, pathAsset);

            // ── 6. Viewpoint ──
            TerrainResult mound = null;
            if (view) mound = Viewpoint(ctx, occ, far, walkIn, moundR);
            ctx.Check();

            // ── 7. Picnic spots along the spine, shelters beside it ──
            int tables = Picnic(ctx, occ, avoid);
            ctx.Check();

            // ── 8. Path furniture ──
            var bench = RestAreaArt.Bench(ctx);
            PathBenches(ctx, occ, 45f, bench, 0xBE00u);
            if (ctx.B("lamps")) PathLamps(ctx, occ, ctx.Raw("lampSpacing"), RestAreaArt.Lamp(ctx), 0x1A00u);

            // ── 9. Trees: a belt along the outline (screen) and shade clumps ──
            var treeIds = ctx.L("treeSpecies");
            if (treeIds.Length == 0) treeIds = RestAreaArt.Trees;
            bool canTrees = true;
            if (canTrees)
            {
                if (ctx.B("screen") && style != "overlook")
                {
                    int belt = (int)(region.Perimeter / 7f);
                    LeisureKit.Plant(ctx, occ, region, belt, ObjectRole.Tree, treeIds, 2.5f, 12f, 0.75f, 0x5C00u,
                        p => region.BoundaryDistance(p, 12f) > 8f, 2, 3);
                }
                int trees = (int)(region.Area / 10000f * ctx.F("treeDensity"));
                LeisureKit.Plant(ctx, occ, region, trees, ObjectRole.Tree, treeIds, 2.5f, 24f, 0.3f, 0x7E00u,
                    p => view && math.distance(p, far) < moundR + 4f, 1, 3);
            }
            TransportKit.ClearPlantsFromPaths(ctx.Plan, lot, occ);
            if (mound != null) Settle(ctx.Plan, far, moundR + 1f, mound.HeightAfter);

            EmitGreenRegions(ctx, occ);
            CommonStats(ctx);
            ctx.Stat(Keys.StatRestrooms, restrooms, Keys.UnitCount);
            ctx.Stat(Keys.StatPicnicTables, tables, Keys.UnitCount);
            ctx.Plan.Facts["kiosks"] = kiosks;
            ctx.Plan.Facts["vending"] = vend;
            ctx.Plan.Facts["fuelPumps"] = fuel;
            ctx.Plan.Facts["signs"] = signs;
            ctx.Plan.Facts["dogRuns"] = dogRuns;
            ctx.Plan.Facts["lot"] = lot != null ? 1 : 0;
        }

        private static SettingsBag ParkingSettings(LayerContext ctx)
        {
            var b = TransportKit.Bag(("stallAngle", ctx.S("stallAngle")), ("greenery", ctx.S("lotGreen")), ("evShare", (double)ctx.F("evShare")),
                ("accessibleShare", (double)ctx.F("accessible")), ("lanterns", ctx.B("lamps")), ("perimeterRoad", true));
            if (ctx.B("truckBays"))
            {
                b.Values["stallWidth"] = 4.0;
                b.Values["stallLength"] = 7.0;
                b.Values["aisleWidth"] = 8.0;
            }
            var trees = ctx.L("treeSpecies");
            if (trees.Length > 0) b.Values["trees"] = trees;
            return b;
        }

        private static bool NearestOnPaths(Occupancy occ, float2 p, out float2 foot)
        {
            float best = float.MaxValue; foot = p;
            foreach (var s in occ.Paths.Segments)
            {
                var q = SegmentIndex.ClosestPoint(p, s.A, s.B);
                float d = math.distancesq(q, p);
                if (d < best) { best = d; foot = q; }
            }
            return best < float.MaxValue;
        }

        // ───────────── amenity hub ─────────────

        private static void Hub(LayerContext ctx, Occupancy occ, float2 walkIn, float2 inward, float2 tangent, string style,
            ref int restrooms, ref int kiosks, ref int vend, ref int signs, ref int fuel)
        {
            var region = ctx.LayerRegion;
            float size = ctx.F("hubSize");
            OrientedRect hub = default;
            bool ok = false;
            foreach (float k in new[] { 1f, 0.8f, 0.6f })
            {
                float w = size * k, d = size * 0.6f * k;
                hub = new OrientedRect { Center = walkIn + inward * (d * 0.5f + 0.6f), Axis = tangent, HalfLength = w * 0.5f, HalfWidth = d * 0.5f };
                if (TransportKit.RectInside(region, hub)) { ok = true; break; }
            }
            uint seed = Hashing.Mix(ctx.Seed, 0x4B00u);
            float yawIn = LeisureKit.Yaw(hub.Center, hub.Center - inward);   // facing the car park
            if (ok)
            {
                ctx.Surface(hub.Ring, SurfaceRole.Paving, RestAreaArt.Court, AssetCategories.SurfacePaving);
                float hl = hub.HalfLength, hw = hub.HalfWidth;

                // Restroom block in a back corner.
                if (ctx.B("restrooms") && !ctx.Input.HasChild("restrooms"))
                {
                    string cat = AssetCategories.Restroom;
                    foreach (float side in new[] { -1f, 1f })
                            if (LeisureKit.Put(ctx, occ, hub.Local(side * (hl - 4f), hw - 3.5f), yawIn, ObjectRole.Furniture, RestAreaArt.Restroom, cat, 3.2f, Hashing.Mix(seed, 1u), 0.3f, 0.2f, 0))
                            { restrooms++; break; }
                }

                // Kiosks and vending machines in a row along the back edge.
                var row = new List<float2>();
                for (float u = -hl + 2f; u <= hl - 2f; u += 3f) row.Add(hub.Local(u, hw - 1.6f));
                row.Sort((a, b) => math.distancesq(a, hub.Center).CompareTo(math.distancesq(b, hub.Center)));
                int wantK = ctx.I("kiosks");
                string kcat = wantK > 0 ? AssetCategories.Kiosk : null;
                if (kcat != null)
                    foreach (var p in row)
                    {
                        if (kiosks >= wantK) break;
                        if (LeisureKit.Put(ctx, occ, p, yawIn, ObjectRole.Furniture, RestAreaArt.Kiosk(ctx), kcat, 2.2f, Hashing.Mix(seed, 10u + (uint)kiosks), 0.3f, 0.3f, 0)) kiosks++;
                    }
                int wantV = ctx.I("vending");
                string vcat = wantV > 0 ? AssetCategories.Vending : null;
                if (vcat != null)
                    foreach (var p in row)
                    {
                        if (vend >= wantV) break;
                        uint vv = Hashing.Mix(seed, 20u + (uint)vend);
                        if (LeisureKit.Put(ctx, occ, p, yawIn, ObjectRole.Furniture, AssetMix.Pick(RestAreaArt.Vending, vv), vcat, 0.9f, vv, 0.3f, 0.2f, 0)) vend++;
                    }

                // Truck stop fuel island on the car park side; EV lounge tables.
                int wantF = ctx.I("fuelPumps");
                string fcat = wantF > 0 ? LeisureKit.Category(ctx, "", AssetCategories.FuelPump) : null;
                if (fcat != null)
                    for (int i = 0; i < wantF; i++)
                    {
                        float u = (i - (wantF - 1) * 0.5f) * 3.5f;
                        if (math.abs(u) > hl - 1.5f) continue;
                        if (LeisureKit.Put(ctx, occ, hub.Local(u, -hw + 2.2f), yawIn + 1.5708f, ObjectRole.Furniture, AssetMix.One(""), fcat, 0.9f, Hashing.Mix(seed, 30u + (uint)i), 0.3f, 0.2f, 0)) fuel++;
                    }
                if (style == "ev")
                {
                    string ccat = LeisureKit.Category(ctx, "", AssetCategories.CafeSeating, AssetCategories.Picnic);
                    if (ccat != null)
                        for (int i = 0; i < 4; i++)
                            LeisureKit.Put(ctx, occ, hub.Local((i - 1.5f) * 4f, 0.5f), 0f, ObjectRole.Furniture, AssetMix.One(""), ccat, 1.2f, Hashing.Mix(seed, 40u + (uint)i), 0.3f, 0.3f);
                }

                // Map board by the walk-in point, benches along the front, bins at the corners, lamps.
                if (ctx.B("signs"))
                {
                    // Map board by the walk-in point (a park info board, not a road sign).
                    foreach (float side in new[] { 1f, -1f })
                        if (LeisureKit.Put(ctx, occ, hub.Local(side * 3.2f, -hw + 1.2f), yawIn, ObjectRole.Furniture, RestAreaArt.MapBoard, AssetCategories.Wayfinding, 1.0f, Hashing.Mix(seed, 50u), 0.2f, 0.2f)) { signs++; break; }
                }
                var bench = RestAreaArt.Bench(ctx);
                for (float u = -hl + 2.5f; u <= hl - 2.5f; u += 4.5f)
                    LeisureKit.Put(ctx, occ, hub.Local(u, -hw + 2.6f), yawIn + math.PI, ObjectRole.Bench, bench, null, 1.0f, Hashing.Mix(seed, 60u + (uint)(u * 10f)));
                foreach (var (u, v) in new[] { (-hl + 1f, -hw + 1f), (hl - 1f, -hw + 1f) })
                    LeisureKit.Put(ctx, occ, hub.Local(u, v), 0f, ObjectRole.Bin, AssetMix.One(""), null, 0.4f, Hashing.Mix(seed, 70u), 0.2f, 0.1f);
                if (ctx.B("lamps"))
                    foreach (var (u, v) in new[] { (-hl + 0.8f, hw - 0.8f), (hl - 0.8f, hw - 0.8f), (-hl + 0.8f, 0f), (hl - 0.8f, 0f) })
                        LeisureKit.Put(ctx, occ, hub.Local(u, v), 0f, ObjectRole.Lamp, RestAreaArt.Lamp(ctx), null, 0.4f, Hashing.Mix(seed, 80u), 0.2f, 0.1f);
                // The hub court stays clear of trees and picnic spots.
                foreach (var p in new[] { hub.Local(-hl * 0.5f, 0f), hub.Local(hl * 0.5f, 0f), hub.Center })
                    occ.Take(p, math.max(hw, hl * 0.5f), 2);
                return;
            }

            // No room for a court: facilities stand beside the paths, nearest the walk-in first.
            if (ctx.B("restrooms") && !ctx.Input.HasChild("restrooms"))
            {
                string cat = AssetCategories.Restroom;
                foreach (var a in PathAnchors.Pick(PathAnchors.Beside(occ, 3.2f, 6f, 1f), 6, 10f, true))
                        if (LeisureKit.Put(ctx, occ, a.Position, LeisureKit.Yaw(a.Position, a.Toward), ObjectRole.Furniture, RestAreaArt.Restroom, cat, 3.2f, Hashing.Mix(seed, 1u), 3.2f, 0.2f, 0)) { restrooms++; break; }
            }
            int want = ctx.I("kiosks") + ctx.I("vending");
            string any = want > 0 ? AssetCategories.Kiosk : null;
            if (any != null)
                foreach (var a in PathAnchors.Pick(PathAnchors.Beside(occ, 2f, 5f, 0.8f), want, 6f, true))
                    if (LeisureKit.Put(ctx, occ, a.Position, LeisureKit.Yaw(a.Position, a.Toward), ObjectRole.Furniture, RestAreaArt.Kiosk(ctx), any, 2f, Hashing.Mix(seed, 90u + (uint)kiosks), 2f, 0.2f, 0)) kiosks++;
        }

        // ───────────── dog walk ─────────────

        private static int DogRun(LayerContext ctx, Occupancy occ, List<float2> avoid, AssetRef pathAsset)
        {
            float r = ctx.F("dogRunSize") * 0.5f;
            uint seed = Hashing.Mix(ctx.Seed, 0xD060u);
            if (!Spots.Find(occ, r + 1.5f, Spots.Preference.NearBoundary, seed, out var c, avoid, r + 14f, 1f, budget: 1500)) return 0;
            avoid.Add(c);
            // Gate faces the nearest path.
            var dir = NearestOnPaths(occ, c, out var foot) && math.distance(foot, c) > 0.5f ? math.normalizesafe(foot - c) : math.normalizesafe(ctx.LayerRegion.Centroid - c, new float2(0, 1));
            var fence = RestAreaArt.DogFence(ctx);
            foreach (var run in Fences.Ring(c, r, 14, dir, 2.5f)) ctx.StraightNet(run.c0, run.c1, NetRole.Fence, fence);
            string amen = LeisureKit.Category(ctx, "", AssetCategories.DogAmenity, AssetCategories.Bin);
            if (amen != null) LeisureKit.Put(ctx, occ, c + dir * (r - 2.5f) + new float2(-dir.y, dir.x) * 2f, 0f, ObjectRole.Furniture, AssetMix.One(""), amen, 0.5f, Hashing.Mix(seed, 1u), 0.2f, 0.2f);
            string agility = AssetAvailability.Has(AssetCategories.DogAgility) ? AssetCategories.DogAgility : null;
            if (agility != null)
                for (int i = 0; i < 3; i++)
                {
                    float a = math.atan2(dir.y, dir.x) + math.PI + (i - 1) * 0.9f;
                    var p = c + new float2(math.cos(a), math.sin(a)) * r * 0.45f;
                    LeisureKit.Put(ctx, occ, p, a, ObjectRole.Furniture, AssetMix.One(""), agility, 1.2f, Hashing.Mix(seed, 2u + (uint)i), 0.2f, 0.3f);
                }
            LeisureKit.Put(ctx, occ, c - dir * (r - 3f), LeisureKit.Yaw(c - dir * (r - 3f), c), ObjectRole.Bench, RestAreaArt.Bench(ctx), null, 1.0f, Hashing.Mix(seed, 9u));
            occ.Take(c, r + 0.5f, 2);
            // Branch path from the gate to the spine.
            var gate = c + dir * (r + 1.6f);
            if (NearestOnPaths(occ, gate, out var spine)) TransportKit.Route(ctx, occ, gate, spine, pathAsset);
            return 1;
        }

        // ───────────── viewpoint ─────────────

        private static TerrainResult Viewpoint(LayerContext ctx, Occupancy occ, float2 at, float2 from, float moundR)
        {
            uint seed = Hashing.Mix(ctx.Seed, 0x7100u);
            var outward = math.normalizesafe(at - from, new float2(0, 1));
            ctx.Surface(LayerRegion.Circle(at, 6f, 16), SurfaceRole.Paving, RestAreaArt.Court, AssetCategories.SurfacePaving);
            // A viewing pavilion: the catalog's "lookouts" are 30-55 m towers that would swallow the platform.
            LeisureKit.Put(ctx, occ, at + outward * 2.5f, LeisureKit.Yaw(at, at + outward), ObjectRole.Furniture, RestAreaArt.ViewShelter, AssetCategories.Lookout, 2.2f, Hashing.Mix(seed, 1u), 0.5f, 0.3f, 0);
            int n = ctx.I("viewBenches");
            var bench = RestAreaArt.Bench(ctx);
            float baseA = math.atan2(outward.y, outward.x);
            for (int i = 0; i < n; i++)
            {
                float a = baseA + (n == 1 ? 0f : (i / (float)(n - 1) - 0.5f) * 2.4f);
                var p = at + new float2(math.cos(a), math.sin(a)) * 5f;
                LeisureKit.Put(ctx, occ, p, LeisureKit.Yaw(at, p), ObjectRole.Bench, bench, null, 1.0f, Hashing.Mix(seed, 10u + (uint)i), 0.3f, 0.2f);
            }
            occ.Take(at, 6.5f, 2);
            float h = ctx.F("moundHeight");
            if (h <= 0f || moundR < 12f) return null;
            var circle = LayerRegion.From(LayerRegion.Circle(at, moundR, 24));
            if (circle == null) return null;
            var spec = new TerrainSpec { Mode = TerrainMode.Raised, Height = h, Falloff = moundR * 0.75f, MaxSlopePct = 30f, Seed = seed };
            return TerrainShaper.Shape(ctx, circle, spec);
        }

        /// <summary>Objects, path ends and surfaces on the raised mound stand on the new ground.</summary>
        private static void Settle(AreaPlan plan, float2 c, float r, System.Func<float2, float> h)
        {
            if (h == null) return;
            bool On(float3 p) => math.distance(p.xz, c) < r;
            foreach (var o in plan.Objects) if (On(o.Position)) o.Position.y = h(o.Position.xz);
            foreach (var n in plan.Nets)
            {
                if (On(n.A)) n.A.y = h(n.A.xz);
                if (On(n.B)) n.B.y = h(n.B.xz);
                if (On(n.C)) n.C.y = h(n.C.xz);
                if (On(n.D)) n.D.y = h(n.D.xz);
            }
            foreach (var s in plan.Surfaces)
                for (int i = 0; i < s.Ring.Length; i++)
                    if (On(s.Ring[i])) s.Ring[i].y = h(s.Ring[i].xz);
        }

        // ───────────── picnic grove ─────────────

        private static int Picnic(LayerContext ctx, Occupancy occ, List<float2> avoid)
        {
            int tables = 0;
            var table = RestAreaArt.Table(ctx);
            int wantShelters = ctx.I("shelters");
            if (wantShelters > 0)
            {
                string cat = AssetCategories.Shelter;
                {
                    var asset = RestAreaArt.Shelter(ctx);
                    int n = 0;
                    foreach (var a in PathAnchors.Pick(PathAnchors.Beside(occ, 4.5f, 8f, 1.2f), wantShelters, 25f, false))
                    {
                        if (!occ.Free(a.Position, 4.5f, 3f, 0.3f)) continue;
                        if (!ctx.LayerRegion.ContainsDisc(a.Position + a.Tangent * 1.5f, 1f) || !ctx.LayerRegion.ContainsDisc(a.Position - a.Tangent * 1.5f, 1f)) continue;
                        uint v = Hashing.Mix(ctx.Seed, 0x5E10u + (uint)n);
                        float yaw = LeisureKit.Yaw(a.Position, a.Toward);
                        ctx.Object(a.Position, yaw, ObjectRole.Furniture, asset, v, -1, cat);
                        ctx.Object(a.Position + a.Tangent * 1.5f, yaw, ObjectRole.Furniture, table, Hashing.Mix(v, 1u), -1, AssetCategories.Picnic);
                        ctx.Object(a.Position - a.Tangent * 1.5f, yaw, ObjectRole.Furniture, table, Hashing.Mix(v, 2u), -1, AssetCategories.Picnic);
                        occ.Take(a.Position, 4.5f, 0);
                        tables += 2; n++;
                    }
                }
            }
            int wanted = ctx.I("groups"), perGroup = ctx.I("tables");
            // Grills, not camp fires (the automatic camp-fire pool resolved to makeshift tents).
            string grill = ctx.B("grills") ? AssetCategories.Picnic : null;
            var treeIds = ctx.L("treeSpecies");
            if (treeIds.Length == 0) treeIds = RestAreaArt.Trees;
            bool canTrees = true;
            var spots = new List<float2>(avoid);
            for (int g = 0; g < wanted; g++)
            {
                if (!Spots.Find(occ, 4.5f, Spots.Preference.NearPaths, Hashing.Mix(ctx.Seed, 0x6400u + (uint)g), out var c, spots, 13f, 1.5f, budget: 1200)) break;
                spots.Add(c);
                uint v = Hashing.Mix(ctx.Seed, 0x6500u + (uint)g);
                float phase = Hashing.Unit(v) * 6.283f;
                for (int k = 0; k < perGroup; k++)
                {
                    float a = phase + k * 6.283f / perGroup;
                    var p = c + new float2(math.cos(a), math.sin(a)) * (perGroup == 1 ? 0f : 2.6f);
                    if (LeisureKit.Put(ctx, occ, p, a + 1.57f, ObjectRole.Furniture, table, AssetCategories.Picnic, 1.3f, Hashing.Mix(v, (uint)k), 0.5f, 0.2f)) tables++;
                }
                if (grill != null) LeisureKit.Put(ctx, occ, c + new float2(math.cos(phase + 3.4f), math.sin(phase + 3.4f)) * 4.6f, phase, ObjectRole.Furniture, RestAreaArt.Grill, grill, 0.8f, Hashing.Mix(v, 50u));
                LeisureKit.Put(ctx, occ, c + new float2(math.cos(phase + 2.2f), math.sin(phase + 2.2f)) * 4.4f, 0f, ObjectRole.Bin, AssetMix.One(""), null, 0.5f, Hashing.Mix(v, 51u));
                if (canTrees) LeisureKit.Put(ctx, occ, c + new float2(math.cos(phase + 1.2f), math.sin(phase + 1.2f)) * 6.2f, phase, ObjectRole.Tree, AssetMix.Pick(treeIds, v), null, 2.5f, Hashing.Mix(v, 52u), 1f, 0.3f, 3, 3);
            }
            ctx.Plan.Facts["picnicGroups"] = spots.Count - avoid.Count;
            return tables;
        }
    }
}
