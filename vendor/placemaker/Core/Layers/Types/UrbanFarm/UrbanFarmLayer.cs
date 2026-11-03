// Urban farm: intensive city growing on a compact plot — market garden beds, vineyard rows,
// glasshouse ranges, a lavender field, rooftop-style raised beds, or a mix. Composition:
//   farm tracks (spine + cross tracks, a grid, or a loop) cut the plot into fields along the
//   plot's principal axes; grass headlands separate the fields; every field is filled with its own
//   crop pattern (oriented rows, patchwork beds, packed glasshouses, paved bed grids);
//   a farmyard by the entrance (gravel, barn, water tanks, crates, machinery, farm shop);
//   an edge of hedge / fence / windbreak trees with gaps at the gates; optional terraces (vineyard)
//   or a levelled pad (glasshouses), always blended back to the original ground at the outline.
// Fields are emitted as "place:field" regions so features (picnic, farm stall, hives, restrooms)
// keep out of the crops; a visitor corner by the entrance is the "core" when a feature needs one.

using System;
using System.Collections.Generic;
using Placemaker.Core.Geometry;
using Placemaker.Core.Layers.Common;
using Placemaker.Core.Layers.Leisure;
using Unity.Mathematics;

namespace Placemaker.Core.Layers.Types.UrbanFarm
{
    [AreaGenerator(70, Group = "farms")]
    public sealed class UrbanFarmLayer : LeisureLayer
    {
        public const string LayerId = "urbanFarm";
        public const string FieldTag = "place:field";

        protected override string Area => "UrbanFarm";
        public override string Id => LayerId;
        public override string Icon => "Media/Game/Resources/Vegetables.svg";
        public override float MinArea => 600f;
        public override string[] Emits => new[] { RegionTags.Green, RegionTags.Core, StreetFrontage.ShopsTag, FieldTag };
        protected override int WantedEntrances(LayerRegion region) => region.Area > 25000f ? 2 : 1;

        /// <summary>Most crop accents (row-head plants, trellis ends) over the whole farm: fields are surfaces (GroundCover).</summary>
        public const int MaxCropItems = 600;
        /// <summary>Crop accents per hectare at 100 % density.</summary>
        public const float AccentsPerHa = 200f;
        private static readonly string[] MarketGround = { GroundCover.Rows, GroundCover.Rows, GroundCover.Soil, GroundCover.Green, GroundCover.Plowed };
        private const int MaxFields = 120;
        private static readonly string[] MixCycle = { "market", "lavender", "vines", "glass" };

        protected override List<FeatureInfo> BuildFeatures() => new List<FeatureInfo>
        {
            CoreFeature(Farmstead.FarmStandAddOn.LayerId),
            Feature(Farmstead.ApiaryAddOn.LayerId),
            CoreFeature("picnic"),
            Feature("restrooms"),
            Feature("shops", ("count", 1.0), ("where", "entrances")),
            StreetShopsFeature(Area),
            Feature("decals", ("theme", "dirt"), ("density", 0.6)),
        };

        protected override void DefineControls(ControlSet c)
        {
            c.Choice("crop", "market", ControlTier.Essential, "growing", "market", "vines", "glass", "lavender", "beds", "mixed").Tip();
            c.Choice("rows", "long", ControlTier.Essential, "growing", "long", "short", "diagonal").Tip();
            c.Slider("density", 100, 50, 150, 10, "%", ControlTier.Essential, "growing").Tip();
            c.Toggle("polytunnels", false, ControlTier.More, "growing").Tip();
            c.Slider("fieldSize", 50, 20, 120, 5, "m", ControlTier.Essential, "layout").Layout(15).Tip();
            c.Choice("tracks", "spine", ControlTier.Essential, "layout", "spine", "grid", "loop", "none").Tip();
            c.Toggle("farmyard", true, ControlTier.Essential, "farmyard").Tip();
            c.Slider("yardSize", 24, 12, 40, 2, "m", ControlTier.More, "farmyard").Layout(12).When("farmyard=true");
            c.Toggle("farmShop", true, ControlTier.More, "farmyard").Tip().When("farmyard=true");
            c.Toggle("machinery", true, ControlTier.More, "farmyard").When("farmyard=true");
            c.Choice("boundary", "hedge", ControlTier.Essential, "boundary", "none", "fence", "hedge", "both");
            c.Toggle("windbreak", false, ControlTier.More, "boundary").Tip();
            c.Slider("windbreakSpacing", 9, 5, 20, 1, "m", ControlTier.More, "boundary").Layout(5).When("windbreak=true");
            c.Choice("terrain", "none", ControlTier.More, "terrain", "none", "flatten", "terraces", "gentle").Tip();
            c.Slider("terrainHeight", 3, 1, 8, 0.5, "m", ControlTier.More, "terrain");
            c.Slider("terraceLevels", 3, 2, 4, 1, "count", ControlTier.More, "terrain").When("terrain=terraces");
            c.Toggle("soilPaint", true, ControlTier.More, "terrain").Tip();
            c.Slider("soilLayer", 1, 1, 4, 1, "count", ControlTier.More, "terrain").Tip().When("soilPaint=true");
            c.Slider("benchSpacing", 0, 0, 80, 10, "m", ControlTier.More, "visitors").Layout(10).Tip();
            c.Toggle("lamps", false, ControlTier.More, "visitors");
            c.Slider("scarecrows", 1, 0, 6, 1, "perHa", ControlTier.More, "visitors").Tip();
            c.Toggle("waterTanks", true, ControlTier.More, "visitors").Tip();
            c.AssetMulti("cropAssets", AssetCategories.GardenCrop, ControlTier.More, "assets");
            c.Asset("vineAsset", AssetCategories.CityFarmVine, ControlTier.More, "assets");
            c.Asset("lavenderAsset", AssetCategories.CityFarmLavender, ControlTier.More, "assets");
            c.Asset("glasshouseAsset", AssetCategories.Glasshouse, ControlTier.More, "assets");
            c.Asset("bedAsset", AssetCategories.Planter, ControlTier.More, "assets");
            c.Asset("barnAsset", AssetCategories.CityFarmBarn, ControlTier.More, "assets");
            c.Asset("hedgeAsset", AssetCategories.Hedge, ControlTier.More, "assets");
            c.Asset("fenceAsset", AssetCategories.Fence, ControlTier.More, "assets");
            c.AssetMulti("treeSpecies", AssetCategories.Tree, ControlTier.More, "assets");
            c.Asset("trackAsset", AssetCategories.Pathway, ControlTier.More, "assets");
            c.Asset("fieldSurface", AssetCategories.SurfaceDirt, ControlTier.More, "assets");
            c.Asset("benchAsset", AssetCategories.Bench, ControlTier.More, "assets");
            EntranceMarkers(c);
            DefineStreetShopControls(c);
            c.RandomRange("fieldSize", 30, 80);
            c.RandomRange("density", 70, 130);
            c.RandomRange("benchSpacing", 0, 40);
            c.Fixed("terrain");
            c.Fixed("soilPaint");
            c.Fixed("soilLayer");
            c.Chance("windbreak", 0.4f);
            c.Chance("lamps", 0.25f);
            c.Chance("polytunnels", 0.4f);
        }

        protected override void DefinePresets()
        {
            Preset("marketGarden", ("crop", "market"), ("rows", "long"), ("fieldSize", 40.0), ("tracks", "spine"), ("boundary", "hedge"),
                ("polytunnels", true), ("scarecrows", 2.0));
            Preset("vineyard", ("crop", "vines"), ("rows", "short"), ("fieldSize", 70.0), ("tracks", "grid"), ("boundary", "none"),
                ("terrain", "terraces"), ("terrainHeight", 4.0), ("soilPaint", false), ("scarecrows", 0.0), ("machinery", true), ("farmShop", true));
            Preset("greenhouses", ("crop", "glass"), ("rows", "long"), ("fieldSize", 60.0), ("tracks", "grid"), ("boundary", "fence"),
                ("terrain", "flatten"), ("soilPaint", false), ("scarecrows", 0.0), ("waterTanks", true));
            Preset("lavenderField", ("crop", "lavender"), ("rows", "long"), ("density", 120.0), ("fieldSize", 90.0), ("tracks", "spine"),
                ("boundary", "hedge"), ("windbreak", true), ("benchSpacing", 40.0), ("scarecrows", 0.0), ("machinery", false));
            Preset("rooftopBeds", ("crop", "beds"), ("fieldSize", 25.0), ("tracks", "grid"), ("boundary", "fence"), ("yardSize", 14.0),
                ("machinery", false), ("soilPaint", false), ("benchSpacing", 20.0), ("lamps", true), ("scarecrows", 0.0), ("waterTanks", false));
            Preset("cityFarm", ("crop", "mixed"), ("fieldSize", 45.0), ("tracks", "loop"), ("boundary", "both"), ("windbreak", true),
                ("benchSpacing", 40.0), ("scarecrows", 2.0));
        }

        private struct FieldPlan
        {
            public float2[] Ring;
            public string Kind;
        }

        protected override void PlanCore(LayerContext ctx)
        {
            var region = ctx.LayerRegion;
            var occ = new Occupancy(region, ctx.Input.Reserved, ctx.Site);
            var gates = Gates(ctx);
            string crop = ctx.S("crop");
            LeisureKit.Frame(region, out var centre, out var axis, out var side, out _, out _);
            string rows = ctx.S("rows");
            var rowDir = rows == "short" ? side : rows == "diagonal" ? math.normalize(axis + side) : axis;

            // 1. Ground and terrain.
            ShapeTerrain(ctx, region);
            Lawn.Cover(ctx, region.Ring, AssetMix.One(""));
            ctx.Check();

            // 2. Farm tracks.
            var trackAsset = AssetMix.One(ctx.S("trackAsset"));
            float hw = Composer.DefaultHalfWidth(NetRole.Pathway);
            float fieldSize = ctx.F("fieldSize");
            float laneGap = math.max(16f, fieldSize * 0.6f);
            var longOffsets = FarmKit.Offsets(region, centre, side, laneGap, 0f);
            var crossOffsets = FarmKit.Offsets(region, centre, axis, fieldSize, 0.5f);
            string tracks = ctx.S("tracks"), boundary = ctx.S("boundary");
            bool windbreak = ctx.B("windbreak");
            float edgeInset = windbreak ? 8f : boundary != "none" ? 4f : 2.5f;
            var spine = new List<(float2 a, float2 b)>();
            void Track(float2 p, float2 dir)
            {
                foreach (var (a, b) in FarmKit.Chords(region, p, dir, 1f))
                {
                    LeisureKit.Path(ctx, occ, a, b, NetRole.Pathway, trackAsset);
                    spine.Add((a, b));
                }
            }
            if (tracks != "none")
            {
                Track(centre, axis);
                if (tracks == "spine" || tracks == "grid") foreach (var o in crossOffsets) Track(centre + axis * o, side);
                if (tracks == "grid") foreach (var o in longOffsets) if (math.abs(o) > 0.5f) Track(centre + side * o, axis);
                if (tracks == "loop")
                {
                    float loopInset = edgeInset + 1f;
                    float2[] loop = null;
                    try { loop = FarmKit.Largest(Poly.Offset(region.Ring, loopInset)); } catch (Exception) { }
                    if (loop != null && Poly.Area(loop) > 400f)
                    {
                        loop = Poly.Simplify(loop, 1f);
                        for (int i = 0; i < loop.Length; i++)
                        {
                            var a = loop[i]; var b = loop[(i + 1) % loop.Length];
                            if (math.distance(a, b) < 1f) continue;
                            LeisureKit.Path(ctx, occ, a, b, NetRole.Pathway, trackAsset);
                            spine.Add((a, b));
                        }
                        edgeInset = loopInset + hw + 1.2f;
                    }
                }
            }
            int connectorStart = occ.Paths.Count;
            ConnectGates(ctx, occ, gates, spine, NetRole.Pathway, trackAsset);
            var connectors = new List<SegmentIndex.Seg>();
            for (int i = connectorStart; i < occ.Paths.Count; i++) connectors.Add(occ.Paths.Segments[i]);
            ctx.Check();

            // 3. Farmyard by the main entrance, and a visitor corner when a feature needs the core.
            var blockers = new List<float2[]>();
            float yardSign = 1f;
            if (ctx.B("farmyard") && gates.Count > 0 && Farmyard(ctx, occ, gates[0], out var yardRing, out yardSign)) blockers.Add(yardRing);
            float2[] cornerRing = null;
            if (ctx.Input.HasChildTarget(RegionTags.Core) && gates.Count > 0)
                cornerRing = VisitorCorner(ctx, occ, gates[0], -yardSign);
            if (cornerRing != null) blockers.Add(cornerRing);
            foreach (var s in connectors) blockers.Add(FarmKit.SegmentRect(s.A, s.B, s.HalfWidth + 1.2f));
            ctx.Check();

            // 4. Boundary: fence, hedgerow, windbreak trees — gaps at the gates.
            if (boundary == "fence" || boundary == "both") Fence(ctx, gates, AssetMix.One(ctx.S("fenceAsset")), 2.5f);
            if (boundary == "hedge" || boundary == "both")
            {
                string hedgeCat = LeisureKit.Category(ctx, ctx.S("hedgeAsset"), AssetCategories.Hedge, AssetCategories.Bush);
                FarmKit.Edge(ctx, occ, region, boundary == "both" ? 2.2f : 1.4f, 2.0f, gates, 4.5f, hedgeCat, new[] { ctx.S("hedgeAsset") }, 0.9f, 0xED6Eu);
            }
            if (windbreak && AssetAvailability.Require(ctx.Plan, AssetCategories.Tree, ctx.L("treeSpecies"), Keys.Category(AssetCategories.Tree)))
                FarmKit.Edge(ctx, occ, region, 5f, ctx.F("windbreakSpacing"), gates, 7f, AssetCategories.Tree, ctx.L("treeSpecies"), 2.2f, 0x3B2Eu, 2, 3);
            ctx.Check();

            // 5. Fields: cells between the field lines, inset for headlands, clipped to the edge band, carved.
            var lines = new List<(float2 p, float2 dir)> { (centre, axis) };
            foreach (var o in crossOffsets) lines.Add((centre + axis * o, side));
            foreach (var o in longOffsets) if (math.abs(o) > 0.5f) lines.Add((centre + side * o, axis));
            var cells = FarmKit.SplitByLines(region.Ring, lines, MaxFields * 2, ctx.Cancel);
            float2[] inner = null;
            try { inner = FarmKit.Largest(Poly.Offset(region.Ring, edgeInset)); } catch (Exception) { }
            var rings = new List<float2[]>();
            foreach (var cell in cells)
            {
                List<float2[]> inset;
                try { inset = Poly.Offset(cell, hw + 1.2f); } catch (Exception) { continue; }
                foreach (var r in inset)
                    if (inner != null) rings.AddRange(FarmKit.Clip(r, inner, 40f));
            }
            foreach (var b in blockers) rings = FarmKit.Carve(rings, b, 40f);
            ctx.Check();
            var fields = new List<FieldPlan>();
            int mixShift = (int)(ctx.Seed % 4u);
            foreach (var r in rings)
            {
                if (fields.Count >= MaxFields) break;
                var clean = Poly.Clean(r, 0.3f);
                if (clean.Length < 3 || Poly.Area(clean) < 60f) continue;
                int i = fields.Count;
                string kind = crop == "mixed" ? MixCycle[(i + mixShift) % MixCycle.Length] : crop;
                if (crop == "market" && ctx.B("polytunnels") && i % 3 == 1 && Poly.Area(clean) > 500f) kind = "glass";
                fields.Add(new FieldPlan { Ring = clean, Kind = kind });
            }
            if (fields.Count == 0) ctx.Issue(IssueSeverity.Warning, "Placemaker.UrbanFarm.Issue.NoFields");
            var fieldSet = new FarmKit.RingSet();
            for (int i = 0; i < fields.Count; i++)
            {
                fieldSet.Add(fields[i].Ring);
                ctx.AddRegion(fields[i].Ring, "field:" + i, FieldTag);
            }

            // 6. Things beside the tracks first (they claim space the crops then avoid).
            var bench = AssetMix.One(ctx.S("benchAsset"));
            if (ctx.F("benchSpacing") > 0f) FarmKit.BesidePaths(ctx, occ, ctx.F("benchSpacing"), ObjectRole.Bench, bench, 1.3f, 0xB3C0u);
            if (ctx.B("lamps")) FarmKit.BesidePaths(ctx, occ, 28f, ObjectRole.Lamp, AssetMix.One(""), 0.5f, 0x1A30u);
            if (ctx.B("waterTanks")) WaterTanks(ctx, occ, fields);
            Scarecrows(ctx, occ, region, fields);
            ctx.Check();

            // 7. Crops, field by field — ground before props (GroundCover): every field is ONE crop
            // surface (rows / soil / green / lavender…), the plants are accents on the row heads along
            // the tracks, plus wheel ruts along the rows where a farm decal set is installed.
            float density = math.clamp(ctx.F("density") / 100f, 0.5f, 1.5f);
            int budget = GroundCover.AccentBudget(region.Area, AccentsPerHa * density, 40, MaxCropItems), glasshouses = 0, beds = 0, soil = 0;
            int startBudget = budget, tracksBudget = 40;
            int rowFields = 0;
            foreach (var f in fields) if (f.Kind != "glass" && f.Kind != "beds") rowFields++;
            int perField = math.max(6, budget / math.max(1, rowFields));
            bool paint = ctx.B("soilPaint");
            int soilLayer = math.clamp(ctx.I("soilLayer"), 1, 4);
            string chosenGround = ctx.S("fieldSurface");
            for (int i = 0; i < fields.Count; i++)
            {
                if ((i & 7) == 0) ctx.Check();
                var f = fields[i];
                uint salt = Hashing.Mix(ctx.Seed, 0xF1E1u + (uint)i);
                switch (f.Kind)
                {
                    case "market":
                    {
                        // Patchwork: neighbouring fields alternate vegetable rows, bare soil and leafy green.
                        GroundCover.Field(ctx, f.Ring, MarketGround[(int)(salt % (uint)MarketGround.Length)], chosenGround);
                        if (paint && soil < 80) { TerrainPaint.Patch(ctx, f.Ring, soilLayer, 0.85f, 1.5f); soil++; }
                        string cat = LeisureKit.Category(ctx, ctx.L("cropAssets").Length > 0 ? "x" : "", AssetCategories.GardenCrop, AssetCategories.Bush);
                        GroundCover.RowHeads(ctx, occ, f.Ring, rowDir, 1.4f / density, 1.0f, 2.2f, 2, FarmKit.RoleFor(cat ?? AssetCategories.Bush), cat,
                            ctx.L("cropAssets"), perField, ref budget, salt);
                        GroundCover.RowTracks(ctx, occ, f.Ring, rowDir, ref tracksBudget, salt, 3);
                        break;
                    }
                    case "lavender":
                    {
                        GroundCover.Field(ctx, f.Ring, GroundCover.Lavender, chosenGround);
                        if (paint && soil < 80) { TerrainPaint.Patch(ctx, f.Ring, soilLayer, 0.6f, 1.5f); soil++; }
                        string cat = LeisureKit.Category(ctx, ctx.S("lavenderAsset"), AssetCategories.CityFarmLavender, AssetCategories.Flowerbed, AssetCategories.Bush);
                        GroundCover.RowHeads(ctx, occ, f.Ring, rowDir, 1.8f / density, 1.0f, 3.2f, 1, FarmKit.RoleFor(cat ?? AssetCategories.Bush), cat,
                            new[] { ctx.S("lavenderAsset") }, perField, ref budget, salt);
                        GroundCover.RowTracks(ctx, occ, f.Ring, rowDir, ref tracksBudget, salt, 3);
                        break;
                    }
                    case "vines":
                    {
                        GroundCover.Field(ctx, f.Ring, GroundCover.Green, chosenGround);
                        string cat = LeisureKit.Category(ctx, ctx.S("vineAsset"), AssetCategories.CityFarmVine, AssetCategories.Hedge, AssetCategories.Bush);
                        string post = AssetAvailability.Has(AssetCategories.CityFarmTrellis) ? AssetCategories.CityFarmTrellis : null;
                        float gap = 2.6f / density;
                        int n = GroundCover.RowHeads(ctx, occ, f.Ring, rowDir, gap, 1.6f, 4.0f, 1, FarmKit.RoleFor(cat ?? AssetCategories.Bush), cat,
                            new[] { ctx.S("vineAsset") }, post != null ? perField * 2 / 3 : perField, ref budget, salt);
                        // Trellis end posts on the row heads that carry vines.
                        if (post != null && n > 0)
                        {
                            int posts = 0;
                            foreach (var (a, b) in GroundCover.RowLines(f.Ring, rowDir, gap, 0.2f))
                            {
                                if (posts >= perField / 3 || budget <= 0) break;
                                var t = math.normalizesafe(b - a);
                                foreach (var end in new[] { a, b })
                                    if (occ.Free(end, 0.3f, 0f, 0.05f)) { ctx.Object(end, FarmKit.Yaw(t), ObjectRole.Furniture, AssetMix.One(""), salt, -1, post); occ.Take(end, 0.25f, 1); posts++; budget--; }
                            }
                        }
                        break;
                    }
                    case "glass":
                    {
                        int houses = Glasshouses(ctx, occ, f.Ring, rowDir, density, salt);
                        glasshouses += houses;
                        if (houses > 0) { FieldSurface(ctx, f.Ring, AssetCategories.SurfaceNatural); break; }
                        // Too small for a glasshouse: a cold-frame bed of vegetable rows instead of bare gravel.
                        GroundCover.Field(ctx, f.Ring, GroundCover.Rows, chosenGround);
                        string cat = LeisureKit.Category(ctx, ctx.L("cropAssets").Length > 0 ? "x" : "", AssetCategories.GardenCrop, AssetCategories.Bush);
                        GroundCover.RowHeads(ctx, occ, f.Ring, rowDir, 1.4f / density, 1.0f, 2.2f, 2, FarmKit.RoleFor(cat ?? AssetCategories.Bush), cat,
                            ctx.L("cropAssets"), perField, ref budget, salt);
                        break;
                    }
                    case "beds":
                        beds += RaisedBeds(ctx, occ, f.Ring, rowDir, density, salt);
                        break;
                }
            }
            ctx.Check();

            // 8. Regions for the features: the visitor corner is the core; the rest of the grass is green.
            EmitGreen(ctx, occ, fieldSet, cornerRing);
            CommonStats(ctx);
            ctx.Stat("Placemaker.Stat.Fields", fields.Count, Keys.UnitCount);
            ctx.Stat("Placemaker.Stat.Glasshouses", glasshouses, Keys.UnitCount);
            ctx.Plan.Facts["cropItems"] = startBudget - budget;
            ctx.Plan.Facts["raisedBeds"] = beds;
        }

        // ───────────── terrain ─────────────

        private static void ShapeTerrain(LayerContext ctx, LayerRegion region)
        {
            string t = ctx.S("terrain");
            if (t == "none") return;
            var spec = new TerrainSpec
            {
                Mode = t == "flatten" ? TerrainMode.Flatten : t == "terraces" ? TerrainMode.Terraces : TerrainMode.Hills,
                Height = t == "gentle" ? math.min(2f, ctx.F("terrainHeight")) : ctx.F("terrainHeight"),
                Scale = 45f, MaxSlopePct = 20f, Falloff = 18f, Levels = math.clamp(ctx.I("terraceLevels"), 2, 4),
                Seed = Hashing.Mix(ctx.Seed, 0x7E44u),
            };
            TerrainShaper.Shape(ctx, region, spec);
        }

        // ───────────── farmyard & visitor corner ─────────────

        private static bool Farmyard(LayerContext ctx, Occupancy occ, Gate g, out float2[] ring, out float sign)
        {
            var region = ctx.LayerRegion;
            var inward = region.InwardNormal(g.Segment);
            var along = new float2(inward.y, -inward.x);
            float size = ctx.F("yardSize"), hwPath = Composer.DefaultHalfWidth(NetRole.Pathway);
            ring = null; sign = 1f;
            OrientedRect r = default;
            bool found = false;
            for (int attempt = 0; attempt < 6 && !found; attempt++)
            {
                float s = size * (attempt < 2 ? 1f : attempt < 4 ? 0.8f : 0.6f);
                float sg = (attempt & 1) == 0 ? 1f : -1f;
                float hl = s * 0.5f, hwid = math.max(5f, s * 0.35f);
                var c = g.Position + inward * (hwid + 1.5f) + along * sg * (hl + hwPath + 2f);
                var cand = new OrientedRect { Center = c, Axis = along, HalfLength = hl, HalfWidth = hwid };
                if (LeisureKit.RectFits(region, occ, cand, 0.8f)) { r = cand; sign = sg; found = true; }
            }
            if (!found) return false;
            float inSide = math.dot(inward, r.Side) > 0f ? 1f : -1f;   // back of the yard = away from the outline
            float gateEnd = -sign;                                        // yard end next to the entrance path
            string ground = AssetAvailability.Has(AssetCategories.SurfaceNatural) ? AssetCategories.SurfaceNatural : null;
            ctx.Surface(r.Ring, SurfaceRole.Ground, AssetMix.One(""), ground);
            uint v = Hashing.Mix(ctx.Seed, 0xFA4Du);
            // Barn at the far end, back side.
            string barnCat = LeisureKit.Category(ctx, ctx.S("barnAsset"), AssetCategories.CityFarmBarn, AssetCategories.GardenShed, AssetCategories.Shelter);
            float barnR = math.clamp(math.min(r.HalfLength * 0.45f, r.HalfWidth * 0.7f), 2.5f, 7f);
            if (barnCat != null)
                LeisureKit.Put(ctx, occ, r.Local(-gateEnd * (r.HalfLength - barnR - 0.5f), inSide * (r.HalfWidth - barnR - 0.3f)), r.Yaw, ObjectRole.Furniture,
                    AssetMix.One(ctx.S("barnAsset")), barnCat, barnR, v, 0.3f, 0.2f);
            // Water tanks and stacked produce crates along the back.
            if (AssetAvailability.Has(AssetCategories.GardenShed))
                for (int k = 0; k < 2; k++)
                    LeisureKit.Put(ctx, occ, r.Local(gateEnd * (r.HalfLength * 0.15f + k * 2.4f), inSide * (r.HalfWidth - 1.4f)), 0f, ObjectRole.Furniture,
                        AssetMix.One(""), AssetCategories.GardenShed, 1.1f, Hashing.Mix(v, 10u + (uint)k), 0.3f, 0.2f);
            if (AssetAvailability.Has(AssetCategories.CityFarmProp))
                for (int k = 0; k < 6; k++)
                    LeisureKit.Put(ctx, occ, r.Local(gateEnd * (r.HalfLength * 0.5f + (k % 3) * 1.3f), inSide * (r.HalfWidth - 1.2f - (k / 3) * 1.4f)), r.Yaw,
                        ObjectRole.Furniture, AssetMix.One(""), AssetCategories.CityFarmProp, 0.6f, Hashing.Mix(v, 20u + (uint)k), 0.3f, 0.1f);
            if (ctx.B("machinery") && AssetAvailability.Has(AssetCategories.CityFarmMachine))
                LeisureKit.Put(ctx, occ, r.Local(-gateEnd * r.HalfLength * 0.1f, -inSide * r.HalfWidth * 0.25f), r.Yaw + 0.3f, ObjectRole.Furniture,
                    AssetMix.One(""), AssetCategories.CityFarmMachine, 2.4f, Hashing.Mix(v, 30u), 0.3f, 0.3f);
            if (ctx.B("farmShop"))
            {
                string shopCat = LeisureKit.Category(ctx, "", AssetCategories.MarketStall, AssetCategories.Kiosk);
                var p = r.Local(gateEnd * (r.HalfLength - 2.8f), -inSide * (r.HalfWidth - 2.8f));
                if (shopCat != null) LeisureKit.Put(ctx, occ, p, LeisureKit.Yaw(p, g.Position), ObjectRole.Furniture, AssetMix.One(""), shopCat, 2.4f, Hashing.Mix(v, 40u), 0.3f, 0.2f);
            }
            LeisureKit.TakeRect(occ, r);
            ring = new OrientedRect { Center = r.Center, Axis = r.Axis, HalfLength = r.HalfLength + 2f, HalfWidth = r.HalfWidth + 2f }.Ring;
            ctx.Plan.Facts["farmyard"] = 1;
            return true;
        }

        private static float2[] VisitorCorner(LayerContext ctx, Occupancy occ, Gate g, float sign)
        {
            var region = ctx.LayerRegion;
            float r = math.clamp(math.sqrt(region.Area) * 0.1f, 9f, 16f);
            var inward = region.InwardNormal(g.Segment);
            var along = new float2(inward.y, -inward.x);
            var c = g.Position + inward * (r + 2f) + along * sign * (r + Composer.DefaultHalfWidth(NetRole.Pathway) + 2f);
            if (!occ.Free(c, r, 0.5f, 0.3f) && !Spots.Find(occ, r, Spots.Preference.NearPaths, Hashing.Mix(ctx.Seed, 0xC0Eu), out c, null, 0f, 0.5f, 900))
                return null;
            occ.Take(c, r, 9);
            var clipped = FarmKit.Clip(LayerRegion.Circle(c, r + 1.5f, 20), region.Ring, 30f);
            return clipped.Count > 0 ? clipped[0] : null;
        }

        // ───────────── fields ─────────────

        private static void FieldSurface(LayerContext ctx, float2[] ring, string category)
        {
            string chosen = ctx.S("fieldSurface");
            string cat = category == AssetCategories.SurfaceDirt
                ? (string.IsNullOrEmpty(chosen) ? (AssetAvailability.Has(AssetCategories.SurfaceDirt) ? AssetCategories.SurfaceDirt : AssetCategories.SurfaceNatural) : AssetCategories.SurfaceDirt)
                : category;
            if (!AssetAvailability.Has(cat) && string.IsNullOrEmpty(chosen)) return;
            ctx.Surface(ring, SurfaceRole.Decorative, AssetMix.One(category == AssetCategories.SurfaceDirt ? chosen : ""), cat);
        }

        private static int Glasshouses(LayerContext ctx, Occupancy occ, float2[] field, float2 dir, float density, uint salt)
        {
            var fr = LayerRegion.From(field);
            if (fr == null) return 0;
            string chosen = ctx.S("glasshouseAsset");
            string cat = LeisureKit.Category(ctx, chosen, AssetCategories.Glasshouse, AssetCategories.GardenShed);
            if (cat == null) return 0;
            float length = 16f, width = 7f;
            if (!string.IsNullOrEmpty(chosen) && ctx.Site.Assets.TryGetValue(chosen, out var info))
            {
                var size = info.BoundsMax - info.BoundsMin;
                if (size.z > 1f && size.x > 1f) { length = size.z; width = size.x; }
            }
            if (cat == AssetCategories.GardenShed) { length = 4f; width = 3f; }
            float gap = 2.4f / density;
            var houses = FarmKit.PackRows(fr, occ, dir, length, width, gap + 1.5f, gap, 80, 0.4f, true);
            var asset = AssetMix.One(chosen);
            bool tanks = ctx.B("waterTanks") && AssetAvailability.Has(AssetCategories.GardenShed);
            for (int i = 0; i < houses.Count; i++)
            {
                var h = houses[i];
                uint v = Hashing.Mix(salt, (uint)i);
                ctx.Object(h.Center, h.Yaw, ObjectRole.Furniture, asset, v, -1, cat);
                if (tanks && (i & 1) == 0)
                {
                    var p = h.Local(h.HalfLength + 1.1f, h.HalfWidth * 0.5f);
                    if (fr.ContainsDisc(p, 0.6f) && occ.Free(p, 0.6f, 0f, 0.05f)) { ctx.Object(p, h.Yaw, ObjectRole.Furniture, AssetMix.One(""), Hashing.Mix(v, 3u), -1, AssetCategories.GardenShed); occ.Take(p, 0.6f, 1); }
                }
            }
            return houses.Count;
        }

        private static int RaisedBeds(LayerContext ctx, Occupancy occ, float2[] field, float2 dir, float density, uint salt)
        {
            var fr = LayerRegion.From(field);
            if (fr == null) return 0;
            ctx.Surface(field, SurfaceRole.Paving, AssetMix.One(""), AssetCategories.SurfacePaving);
            // A pergola / shelter in the middle of larger bed gardens, with a bench either side.
            if (fr.Area > 500f)
            {
                var c = fr.DeepestPoint(out float clear, null, 10);
                string shelter = clear > 4f ? LeisureKit.Category(ctx, "", AssetCategories.Shelter, AssetCategories.CafeSeating) : null;
                if (shelter != null && LeisureKit.Put(ctx, occ, c, FarmKit.Yaw(dir), ObjectRole.Furniture, AssetMix.One(""), shelter, 3f, Hashing.Mix(salt, 77u), 0.3f, 0.2f))
                {
                    var n = FarmKit.Perp(dir);
                    LeisureKit.Put(ctx, occ, c + n * 4.6f, FarmKit.Yaw(-n), ObjectRole.Bench, AssetMix.One(ctx.S("benchAsset")), null, 1.2f, Hashing.Mix(salt, 78u), 0.3f, 0.2f);
                    LeisureKit.Put(ctx, occ, c - n * 4.6f, FarmKit.Yaw(n), ObjectRole.Bench, AssetMix.One(ctx.S("benchAsset")), null, 1.2f, Hashing.Mix(salt, 79u), 0.3f, 0.2f);
                }
            }
            string chosen = ctx.S("bedAsset");
            string cat = LeisureKit.Category(ctx, chosen, AssetCategories.Planter, AssetCategories.GardenCrop);
            if (cat == null) return 0;
            float gap = 1.6f / math.max(0.6f, density);
            var beds = FarmKit.PackRows(fr, occ, dir, 4f, 1.6f, gap, gap, 300, 0.4f, false);
            var asset = AssetMix.One(chosen);
            for (int i = 0; i < beds.Count; i++)
                ctx.Object(beds[i].Center, beds[i].Yaw, FarmKit.RoleFor(cat), asset, Hashing.Mix(salt, (uint)i), -1, cat);
            return beds.Count;
        }

        private static void WaterTanks(LayerContext ctx, Occupancy occ, List<FieldPlan> fields)
        {
            if (!AssetAvailability.Has(AssetCategories.GardenShed)) return;
            for (int i = 0; i < fields.Count; i++)
            {
                var f = fields[i];
                if (f.Kind == "beds" || f.Kind == "glass" || Poly.Area(f.Ring) < 900f) continue;
                // The field corner nearest a track, pulled 1.8 m into the field.
                float best = float.MaxValue; float2 at = default;
                var centroid = Poly.Centroid(f.Ring);
                foreach (var v in f.Ring)
                {
                    float d = occ.Paths.Count > 0 ? occ.Paths.Clearance(v, 60f) : 0f;
                    if (d < best) { best = d; at = v; }
                }
                var p = at + math.normalizesafe(centroid - at) * 1.8f;
                if (Poly.Contains(f.Ring, p) && occ.Free(p, 1f, 0f, 0.1f))
                {
                    ctx.Object(p, 0f, ObjectRole.Furniture, AssetMix.One(""), Hashing.Mix(ctx.Seed, 0x7A4Cu + (uint)i), -1, AssetCategories.GardenShed);
                    occ.Take(p, 1f, 1);
                }
            }
        }

        private static void Scarecrows(LayerContext ctx, Occupancy occ, LayerRegion region, List<FieldPlan> fields)
        {
            int wanted = (int)math.round(region.Area / 10000f * ctx.F("scarecrows"));
            if (wanted <= 0) return;
            string cat = LeisureKit.Category(ctx, "", AssetCategories.CityFarmScarecrow);
            if (cat == null) return;
            var order = new List<int>();
            for (int i = 0; i < fields.Count; i++) if (fields[i].Kind == "market" || fields[i].Kind == "lavender") order.Add(i);
            order.Sort((a, b) => Poly.Area(fields[b].Ring).CompareTo(Poly.Area(fields[a].Ring)));
            int placed = 0;
            foreach (var i in order)
            {
                if (placed >= wanted) break;
                var fr = LayerRegion.From(fields[i].Ring);
                if (fr == null) continue;
                var p = fr.DeepestPoint(out float clear, null, 8);
                if (clear < 2f) continue;
                if (LeisureKit.Put(ctx, occ, p, Hashing.Unit(Hashing.Mix(ctx.Seed, (uint)i)) * 6.283f, ObjectRole.Furniture, AssetMix.One(""), cat, 0.6f,
                    Hashing.Mix(ctx.Seed, 0x5CA2u + (uint)i), 0f, 0.1f)) placed++;
            }
        }

        // ───────────── regions ─────────────

        private static void EmitGreen(LayerContext ctx, Occupancy occ, FarmKit.RingSet fields, float2[] corner)
        {
            var region = ctx.LayerRegion;
            var pieces = FreeSpace.Pieces(region, p => occ.Corridors.Overlaps(p, 0.75f) || occ.HitsPolygon(p, 0.5f) || fields.Contains(p, 0.5f),
                2.5f, 150f, ctx.Cancel);
            bool core = corner != null;
            if (core) ctx.AddRegion(corner, "core:0", RegionTags.Core, RegionTags.Green);
            for (int i = 0; i < pieces.Count && i < 24; i++)
                if (!core && i == 0) ctx.AddRegion(pieces[i], "green:0", RegionTags.Green, RegionTags.Core);
                else ctx.AddRegion(pieces[i], "green:" + i, RegionTags.Green);
            if (!core && pieces.Count == 0) ctx.AddRegion(region.Ring, "green:0", RegionTags.Green, RegionTags.Core);
        }
    }
}
