// Orchard park: an orchard opened to the public as a park.
//
//  * Fruit trees on a planting lattice (square, quincunx, intensive rows, or variety blocks) in the
//    chosen row direction, rows of one variety each (blocks: one variety per block), a few gaps for
//    realism, ages from young to veteran.
//  * Mown aisles painted between the tree rows; blossom meadow patches (painted, with wildflowers)
//    under the trees.
//  * Paths: a cross, a grid of blocks, a loop inside the edge, or a blossom avenue (a wide path lined
//    with trees exactly aligned on both sides) — always joined to the entrances.
//  * A central glade (lawn with a veteran "mother tree", benches round the edge) that is the core
//    region for features (fountain, playground).
//  * Picnic tables in the shade between the trees by the paths, benches and optional lamps along the
//    paths, harvest crates/ladders under some trees, a windbreak row and/or hedge or fence round it.
//  * Features: beehives and a cider/fruit stand (farm add-ons), picnic, restrooms, playground, decals
//    (leaves/blossom), terrain (terraces for olive groves), rocks, fountain in the glade.
//
// Distinct from the farmstead (production fields seen from the road) and the urban farm: this is a
// park — paths, seating and open lawns come first, the trees are the scenery.

using System;
using System.Collections.Generic;
using Placemaker.Core.Geometry;
using Placemaker.Core.Layers.Common;
using Placemaker.Core.Layers.Leisure;
using Placemaker.Core.Layers.Park;
using Placemaker.Core.Layers.Types.Farmstead;
using Unity.Mathematics;

namespace Placemaker.Core.Layers.Types.OrchardPark
{
    [AreaGenerator(132, Group = "parks")]
    public sealed class OrchardParkLayer : LeisureLayer
    {
        protected override string Area => "OrchardPark";
        public override string Id => "orchardPark";
        public override string Icon => "Media/Game/Resources/Food.svg";
        public override float MinArea => 1500f;
        public override string[] Emits => new[] { RegionTags.Green, RegionTags.Core };
        protected override bool HostsStreetShops => false;

        private const int MaxTrees = 3000;
        private const int MaxAisles = 700;
        private const int MaxFlowers = 1500;

        protected override List<FeatureInfo> BuildFeatures()
        {
            var stand = Feature(FarmStandAddOn.LayerId, ("where", "entrances"), ("sells", "cider"));
            stand.DefaultOn = true;
            var hives = Feature(ApiaryAddOn.LayerId, ("hives", 8.0), ("where", "edge"), ("layout", "rows"));
            hives.DefaultOn = true;
            return new List<FeatureInfo>
            {
                stand, hives,
                Feature("picnic", ("groups", 2.0), ("shadeTrees", false)),
                Feature("restrooms", ("byArea", true), ("haEach", 4.0)),
                Feature("playground", ("size", "small")),
                CoreFeature("fountain", ("size", "small")),
                Feature("decals", ("theme", "leaves"), ("density", 1.5)),
                Feature("terrain", ("shape", "terraces"), ("height", 3.0), ("levels", 3.0)),
                Feature("rocks", ("clusters", 2.0), ("size", "small")),
            };
        }

        protected override void DefineControls(ControlSet c)
        {
            c.Choice("style", "apple", ControlTier.Essential, "identity", "apple", "cherry", "olive", "citrus", "community").Tip();

            c.Choice("grid", "square", ControlTier.Essential, "layout", "square", "quincunx", "rows", "blocks").Tip();
            c.Slider("spacing", 7, 4, 14, 0.5, "m", ControlTier.Essential, "layout").Layout(5).Tip();
            c.Choice("rowAngle", "along", ControlTier.Essential, "layout", "along", "across", "diagonal").Tip();
            c.Choice("paths", "cross", ControlTier.Essential, "layout", "cross", "grid", "loop", "avenue").Tip();
            c.Slider("blockSize", 45, 20, 100, 5, "m", ControlTier.More, "layout").Layout(15).Tip();
            c.Asset("pathAsset", AssetCategories.Pathway, ControlTier.More, "layout");

            c.AssetMulti("treeSpecies", AssetCategories.FruitTree, ControlTier.More, "trees");
            c.Choice("treeAge", "mixed", ControlTier.More, "trees", "young", "mixed", "mature");
            c.Slider("gaps", 5, 0, 40, 1, "%", ControlTier.More, "trees").Tip();
            c.Toggle("windbreak", false, ControlTier.More, "trees").Tip();
            c.AssetMulti("windbreakSpecies", AssetCategories.Tree, ControlTier.More, "trees").When("windbreak=true");

            c.Slider("meadow", 40, 0, 100, 5, "%", ControlTier.More, "meadow").Tip();
            c.Slider("flowers", 30, 0, 150, 5, "perHa", ControlTier.More, "meadow").Tip();
            c.Toggle("mownAisles", true, ControlTier.More, "meadow").Tip();
            c.Toggle("lawn", false, ControlTier.More, "meadow").Tip();
            c.Slider("meadowLayer", 1, 1, 4, 1, "count", ControlTier.More, "meadow").Tip();
            c.Slider("aisleLayer", 2, 1, 4, 1, "count", ControlTier.More, "meadow").When("mownAisles=true").Tip();

            c.Slider("picnicSpots", 4, 0, 20, 1, "count", ControlTier.More, "picnic").Tip();
            c.Slider("benchEvery", 40, 0, 120, 5, "m", ControlTier.More, "picnic").Layout(10).Tip();
            c.Toggle("lamps", false, ControlTier.More, "picnic");
            c.Toggle("harvestProps", true, ControlTier.More, "picnic").Tip();
            c.Toggle("glade", true, ControlTier.More, "picnic").Tip();
            c.Slider("gladeSize", 15, 5, 40, 1, "%", ControlTier.More, "picnic").When("glade=true");

            c.Choice("edge", "hedge", ControlTier.More, "boundary", "hedge", "fence", "both", "none");
            c.Asset("fenceAsset", AssetCategories.Fence, ControlTier.More, "boundary");

            EntranceMarkers(c);

            c.RandomRange("spacing", 5, 10);
            c.RandomRange("gaps", 0, 15);
            c.Fixed("meadowLayer");
            c.Fixed("aisleLayer");
        }

        // Art pass (a1): the trees are the scenery — a cross of walks through the glade by default (a path grid every
        // ~22 m at the default size gave as much path and aisle as orchard); the community orchard keeps bigger blocks.
        protected override void DefinePresets()
        {
            Preset("apple", ("style", "apple"), ("grid", "square"), ("spacing", 7.0), ("paths", "cross"), ("meadow", 40.0), ("flowers", 30.0),
                ("picnicSpots", 4.0), ("glade", true), ("harvestProps", true), ("edge", "hedge"), ("treeAge", "mixed"), ("gaps", 5.0));
            Preset("cherryBlossom", ("style", "cherry"), ("grid", "quincunx"), ("spacing", 9.0), ("paths", "avenue"), ("meadow", 10.0),
                ("lawn", true), ("mownAisles", false), ("picnicSpots", 12.0), ("benchEvery", 25.0), ("lamps", true), ("glade", true),
                ("gladeSize", 25.0), ("harvestProps", false), ("edge", "none"), ("treeAge", "mature"), ("gaps", 0.0), ("flowers", 10.0));
            Preset("oliveGrove", ("style", "olive"), ("grid", "quincunx"), ("spacing", 11.0), ("rowAngle", "diagonal"), ("paths", "loop"),
                ("meadow", 70.0), ("meadowLayer", 3.0), ("flowers", 10.0), ("mownAisles", false), ("picnicSpots", 3.0), ("glade", false),
                ("edge", "fence"), ("treeAge", "mature"), ("gaps", 12.0), ("benchEvery", 60.0), ("harvestProps", true));
            Preset("citrus", ("style", "citrus"), ("grid", "rows"), ("spacing", 6.0), ("paths", "cross"), ("meadow", 0.0), ("flowers", 0.0),
                ("mownAisles", true), ("aisleLayer", 3.0), ("picnicSpots", 4.0), ("glade", true), ("gladeSize", 12.0), ("lamps", true),
                ("edge", "hedge"), ("harvestProps", true), ("treeAge", "young"), ("windbreak", true), ("gaps", 2.0));
            Preset("community", ("style", "community"), ("grid", "blocks"), ("spacing", 6.0), ("paths", "grid"), ("blockSize", 60.0),
                ("meadow", 50.0), ("flowers", 60.0), ("picnicSpots", 8.0), ("glade", true), ("harvestProps", true), ("gaps", 15.0),
                ("treeAge", "mixed"), ("edge", "fence"), ("benchEvery", 30.0));
        }

        /// <summary>Name hints for the automatic fruit tree (catalog-dump pending; the category decides when none matches).</summary>
        private static string[] StyleHints(string style)
        {
            switch (style)
            {
                // Art pass (a1): ids checked against the player's catalog. Vanilla has one fruit tree (AppleTree01); the
                // VFP pack adds flowering and evergreen fruit trees that read as cherry / citrus. No olive exists: the
                // grey-green laurel fig is the closest silhouette.
                case "cherry": return new[] { "VFP CrapeMyrtleTree", "VFP JacarandaTree", "AppleTree01" };
                case "olive": return new[] { "VFP LaurelFigTree", "AppleTree01" };
                case "citrus": return new[] { "VFP EvergreenPearTree", "VFP LaurelFigTree", "AppleTree01" };
                default: return new[] { "AppleTree01", "VFP EvergreenPearTree" };
            }
        }

        protected override void PlanCore(LayerContext ctx)
        {
            var region = ctx.LayerRegion;
            var occ = new Occupancy(region, ctx.Input.Reserved, ctx.Site);
            var gates = Gates(ctx);
            LeisureKit.Frame(region, out var centre, out var axis, out var side, out float major, out float minor);
            // Art pass (a1): an orchard is planted square to its street. On a near-square site the principal axes are
            // ambiguous (often the diagonals: every row and path ran corner to corner), so square up with the longest edge.
            if (minor > major * 0.85f)
            {
                float best = 0f;
                for (int i = 0; i < region.Ring.Length; i++)
                {
                    var e = region.Ring[(i + 1) % region.Ring.Length] - region.Ring[i];
                    float l = math.length(e);
                    if (l > best) { best = l; axis = e / l; }
                }
                side = new float2(-axis.y, axis.x);
            }
            var pathAsset = AssetMix.One(ctx.S("pathAsset"));
            string style = ctx.S("style");
            float spacing = ctx.F("spacing");
            string pathMode = ctx.S("paths");
            if (ctx.B("lawn")) Lawn.Cover(ctx, region.Ring, AssetMix.One("", ParkArt.Grass));

            // Row frame.
            var rowDir = axis;
            switch (ctx.S("rowAngle"))
            {
                case "across": rowDir = side; break;
                case "diagonal": rowDir = math.normalize(axis + side); break;
            }
            var rowSide = new float2(-rowDir.y, rowDir.x);

            // 1. Glade (the core): a lawn circle in the middle.
            float2[] glade = null; float2 gladeC = centre; float gladeR = 0f;
            if (ctx.B("glade"))
            {
                var rule = new PlaceRule { Shape = PlaceShape.Circle, Anchor = PlaceAnchor.Centre, SizePct = ctx.F("gladeSize"), MinM = 14f, MaxM = 70f, Margin = 6f };
                var placed = PlaceSolver.Solve(region, ctx.Input.Reserved?.Polygons, rule, null, Hashing.Mix(ctx.Seed, 0x61ADu));
                if (placed.Ok)
                {
                    glade = placed.Ring; gladeC = placed.Center; gladeR = placed.Size * 0.5f;
                    if (!ctx.B("lawn")) Lawn.Cover(ctx, glade, AssetMix.One("", ParkArt.Grass));
                }
                else ctx.Issue(IssueSeverity.Info, "Placemaker.OrchardPark.Issue.NoGlade");
            }
            ctx.Check();

            // 2. Paths.
            var spine = new List<(float2 a, float2 b)>();
            var avenue = new List<(float2 a, float2 b)>();
            var origin = glade != null ? gladeC : centre;
            void Chords(float2 through, float2 dir, List<(float2, float2)> into)
            {
                foreach (var (a, b) in FarmsteadLayer.Line(region, through, dir))
                {
                    var d = math.normalizesafe(b - a);
                    if (math.distance(a, b) < 12f) continue;
                    into.Add((a + d * 3f, b - d * 3f));
                }
            }
            switch (pathMode)
            {
                case "cross":
                    Chords(origin, rowDir, spine);
                    Chords(origin, rowSide, spine);
                    break;
                case "avenue":
                    Chords(origin, axis, avenue);
                    Chords(origin, side, spine);
                    break;
                case "loop":
                {
                    var loop = region.InsetVertices(math.max(6f, spacing));
                    if (Poly.IsSimple(loop) && Poly.SignedArea(loop) > 400f)
                        for (int i = 0; i < loop.Length; i++)
                        {
                            var a = loop[i]; var b = loop[(i + 1) % loop.Length];
                            if (math.distance(a, b) > 2f && !region.CrossesBoundary(a, b, false)) spine.Add((a, b));
                        }
                    Chords(origin, axis, spine);
                    break;
                }
                default:   // grid of blocks
                {
                    float pathBlock = ctx.F("blockSize");
                    Lattice(region, origin, rowDir, rowSide, pathBlock, out int a0, out int a1, out int b0, out int b1);
                    for (int k = a0; k <= a1; k++) Chords(origin + rowDir * (k * pathBlock), rowSide, spine);
                    for (int k = b0; k <= b1; k++) Chords(origin + rowSide * (k * pathBlock), rowDir, spine);
                    break;
                }
            }
            foreach (var (a, b) in spine) LeisureKit.Path(ctx, occ, a, b, NetRole.Pathway, pathAsset);
            foreach (var (a, b) in avenue) LeisureKit.Path(ctx, occ, a, b, NetRole.PathwayWide, pathAsset);
            var all = new List<(float2 a, float2 b)>(spine); all.AddRange(avenue);
            if (all.Count == 0 && LeisureKit.Chord(region, centre, axis, 3f, out var ca, out var cb)) { LeisureKit.Path(ctx, occ, ca, cb, NetRole.Pathway, pathAsset); all.Add((ca, cb)); }
            ConnectGates(ctx, occ, gates, all, NetRole.Pathway, pathAsset);
            ctx.Check();

            // 3. Edge: hedge / fence, and a windbreak row of tall trees inside it.
            string edge = ctx.S("edge");
            if (edge == "fence" || edge == "both") Fence(ctx, gates, AssetMix.One(ctx.S("fenceAsset"), OrchardArt.Fence), 3.5f);
            int hedge = 0, windbreak = 0;
            if (edge == "hedge" || edge == "both")
            {
                string cat = LeisureKit.Category(ctx, "", AssetCategories.Hedge, AssetCategories.Bush);
                // Hedge pieces are ~4.1 m long: one per 4 m keeps a closed, non-overlapping hedge line.
                if (cat != null) hedge = EdgeRow(ctx, occ, gates, 1.4f, cat == AssetCategories.Hedge ? 4f : 2.4f, ObjectRole.Bush,
                    cat == AssetCategories.Hedge ? ParkArt.LowHedge : ParkArt.GreenShrubs, cat == AssetCategories.Bush ? null : cat, 1f, 0xED6Eu);
            }
            if (ctx.B("windbreak"))
                windbreak = EdgeRow(ctx, occ, gates, edge == "hedge" || edge == "both" ? 5.5f : 3f, 6f, ObjectRole.Tree, ctx.L("windbreakSpecies").Length > 0 ? ctx.L("windbreakSpecies") : OrchardArt.Windbreak, null, 2.4f, 0x3B1Du);
            ctx.Check();

            // 4. Avenue trees: exactly aligned pairs along the avenue.
            int trees = 0;
            var treeIds = ctx.L("treeSpecies");
            var hints = StyleHints(style);
            string treeCat = treeIds.Length > 0 ? AssetCategories.FruitTree : AssetAvailability.Has(AssetCategories.FruitTree) ? AssetCategories.FruitTree : null;
            if (treeIds.Length == 0 && treeCat == null) ctx.Issue(IssueSeverity.Info, Keys.NoAssetsIssue, Keys.Category(AssetCategories.FruitTree));
            string age = ctx.S("treeAge");
            int Age(uint v) => age == "young" ? (int)(v % 2u) : age == "mature" ? 2 + (int)(v % 2u) : 1 + (int)(v % 3u);
            foreach (var (a, b) in avenue)
            {
                float len = math.distance(a, b);
                var d = (b - a) / math.max(1e-3f, len);
                var n = new float2(-d.y, d.x);
                float off = Composer.DefaultHalfWidth(NetRole.PathwayWide) + 3f;
                for (float s = spacing * 0.5f; s < len; s += spacing)
                    for (int k = -1; k <= 1; k += 2)
                    {
                        var p = a + d * s + n * (k * off);
                        if (glade != null && math.distance(p, gladeC) < gladeR + 1f) continue;
                        uint v = Hashing.Mix(ctx.Seed, 0xA7E0u + (uint)trees);
                        if (LeisureKit.Put(ctx, occ, p, Hashing.Unit(v) * 6.28f, ObjectRole.Tree, AssetMix.Pick(treeIds, 1u, hints), treeCat, 2.2f, 1u, 1f, 0.3f, 3, Age(v)))
                            trees++;
                    }
            }

            // 5. Orchard lattice.
            string grid = ctx.S("grid");
            float along = grid == "rows" ? spacing * 0.6f : spacing;
            float across = grid == "rows" ? spacing * 1.5f : grid == "quincunx" ? spacing * 0.87f : spacing;
            float gapPct = ctx.F("gaps") / 100f;
            float block = math.max(10f, ctx.F("blockSize"));
            var treePts = new List<float2>();
            var rowSamples = new List<(float2 p, int row)>();
            {
                float minA = float.MaxValue, maxA = float.MinValue, minB = float.MaxValue, maxB = float.MinValue;
                foreach (var q in region.Ring)
                {
                    var dd = q - origin;
                    minA = math.min(minA, math.dot(dd, rowDir)); maxA = math.max(maxA, math.dot(dd, rowDir));
                    minB = math.min(minB, math.dot(dd, rowSide)); maxB = math.max(maxB, math.dot(dd, rowSide));
                }
                int rb0 = (int)math.floor(minB / across), rb1 = (int)math.ceil(maxB / across);
                int ra0 = (int)math.floor(minA / along) - 1, ra1 = (int)math.ceil(maxA / along) + 1;
                for (int rb = rb0; rb <= rb1 && trees < MaxTrees; rb++)
                {
                    float offA = grid == "quincunx" && (rb & 1) != 0 ? along * 0.5f : 0f;
                    // One variety per row (blocks: per block) — orchards are planted that way.
                    for (int ra = ra0; ra <= ra1 && trees < MaxTrees; ra++)
                    {
                        float ua = (ra + 0.5f) * along + offA, vb = (rb + 0.5f) * across;
                        var p = origin + rowDir * ua + rowSide * vb;
                        if (!region.ContainsDisc(p, 2.4f)) continue;
                        if (glade != null && math.distance(p, gladeC) < gladeR + 2.5f) continue;
                        uint v = Hashing.Mix(ctx.Seed, ra * 31 + 7, rb * 17 + 3);
                        if (Hashing.Unit(v) < gapPct) continue;
                        uint variety = grid == "blocks"
                            ? Hashing.Mix(ctx.Seed, (int)math.floor(ua / block), (int)math.floor(vb / block))
                            : Hashing.Mix(ctx.Seed, 0x5EEDu + (uint)(rb & 0xFFFF));
                        if (!LeisureKit.Put(ctx, occ, p, Hashing.Unit(Hashing.Mix(v, 2u)) * 6.28f, ObjectRole.Tree, AssetMix.Pick(treeIds, variety, hints), treeCat,
                                2.2f, variety, 1f, 0.3f, 3, Age(v)))
                            continue;
                        trees++;
                        treePts.Add(p);
                        rowSamples.Add((p, rb));
                    }
                    if ((rb & 7) == 0) ctx.Check();
                }
            }
            if (trees == 0) ctx.Issue(IssueSeverity.Warning, "Placemaker.OrchardPark.Issue.NoTrees");

            // 6. Glade: veteran mother tree in the middle, benches round the edge facing in.
            int gladeBenches = 0;
            var bench = AssetMix.One("", ParkArt.RusticBenches);
            if (glade != null)
            {
                uint v = Hashing.Mix(ctx.Seed, 0x307Eu);
                LeisureKit.Put(ctx, occ, gladeC, Hashing.Unit(v) * 6.28f, ObjectRole.Tree, AssetMix.Pick(treeIds, v, hints), treeCat, 3f, v, 1f, 0.5f, 3, 3);
                int n = math.clamp((int)(gladeR / 3f), 4, 12);
                for (int i = 0; i < n; i++)
                {
                    float a = (i + 0.5f) * 2f * math.PI / n;
                    var p = gladeC + new float2(math.cos(a), math.sin(a)) * (gladeR - 1.8f);
                    if (LeisureKit.Put(ctx, occ, p, LeisureKit.Yaw(p, gladeC), ObjectRole.Bench, bench, null, 1.3f, Hashing.Mix(v, (uint)i), 0.5f, 0.3f)) gladeBenches++;
                }
            }
            ctx.Check();

            // 7. Ground: mown aisles between the rows, blossom meadow patches with wildflowers.
            int aisles = 0;
            if (ctx.B("mownAisles"))
            {
                int layer = ctx.I("aisleLayer");
                float minB = float.MaxValue, maxB = float.MinValue;
                foreach (var q in region.Ring) { float t = math.dot(q - origin, rowSide); minB = math.min(minB, t); maxB = math.max(maxB, t); }
                for (int rb = (int)math.floor(minB / across); rb <= (int)math.ceil(maxB / across) && aisles < MaxAisles; rb++)
                    foreach (var (a, b) in FarmsteadLayer.Line(region, origin + rowSide * (rb * across), rowDir))
                    {
                        float len = math.distance(a, b);
                        if (len < 6f) continue;
                        var d = (b - a) / len;
                        float hw = math.min(2.5f, across * 0.25f);
                        if (len < hw * 6f + 6f) continue;
                        // Pull the ends in by more than the half width: lines may meet the outline at a slant.
                        var rect = LayerRegion.Rect((a + b) * 0.5f, d, len * 0.5f - hw * 2.5f - 1f, hw);
                        bool inside = true;
                        foreach (var q in rect) if (!region.Contains(q)) { inside = false; break; }
                        if (!inside) continue;
                        ctx.Plan.Paint.Add(new PlannedPaint { Ring = Poly.To3D(rect, ctx.H), Layer = layer, Strength = 0.6f });
                        if (++aisles >= MaxAisles) break;
                    }
                if (aisles > 0) WarnPaint(ctx);
            }
            var meadows = new List<(float2 c, float r)>();
            float meadowPct = ctx.F("meadow") / 100f;
            if (meadowPct > 0f)
            {
                float r = math.clamp(spacing * 2.5f, 10f, 30f);
                int want = math.clamp((int)(meadowPct * region.Area / (math.PI * r * r) * 0.8f), 1, 40);
                var rng = Hashing.Rng(ctx.Seed, 0x3EADu);
                for (int i = 0; i < want * 6 && meadows.Count < want; i++)
                {
                    var p = region.Min + rng.NextFloat2() * (region.Max - region.Min);
                    if (!region.ContainsDisc(p, r * 0.9f)) continue;
                    if (glade != null && math.distance(p, gladeC) < gladeR + r * 0.6f) continue;
                    bool near = false;
                    foreach (var m in meadows) if (math.distance(m.c, p) < (m.r + r) * 0.8f) { near = true; break; }
                    if (near) continue;
                    meadows.Add((p, r));
                    TerrainPaint.Patch(ctx, LeisureKit.Blob(p, r * 0.85f, Hashing.Mix(ctx.Seed, (uint)i), 14, 0.25f), ctx.I("meadowLayer"), 0.75f, 3f);
                }
            }
            int flowers = 0;
            int wantFlowers = math.min(MaxFlowers, (int)(ctx.F("flowers") * region.Area / 10000f));
            if (wantFlowers > 0)
            {
                string cat = LeisureKit.Category(ctx, "", AssetCategories.Flowerbed, AssetCategories.Bush);
                if (cat != null)
                {
                    Func<float2, bool> reject = null;
                    if (meadows.Count > 0)
                        reject = p => { foreach (var m in meadows) if (math.distancesq(p, m.c) < m.r * m.r * 0.7f) return false; return true; };
                    flowers = LeisureKit.Plant(ctx, occ, region, wantFlowers, cat == AssetCategories.Bush ? ObjectRole.Bush : ObjectRole.Planter, Array.Empty<string>(),
                        0.8f, 20f, 0.3f, 0xF10Au, reject, 1, 3, cat == AssetCategories.Bush ? null : cat);
                }
            }
            ctx.Check();

            // 8. Picnic spots in the shade by the paths, benches and lamps along the paths.
            int tables = 0;
            int wantTables = ctx.I("picnicSpots");
            if (wantTables > 0)
            {
                string cat = LeisureKit.Category(ctx, "", AssetCategories.Picnic, AssetCategories.Bench);
                if (cat != null)
                {
                    var anchors = PathAnchors.Beside(occ, 1.8f, 6f, 1.2f);
                    // Prefer spots with trees around (shade): count trees within 1.2 spacings.
                    anchors.Sort((x, y) =>
                    {
                        int sx = Shade(treePts, x.Position, spacing * 1.2f), sy = Shade(treePts, y.Position, spacing * 1.2f);
                        return sx != sy ? sy.CompareTo(sx) : 0;
                    });
                    var picks = PathAnchors.Pick(anchors, wantTables, math.max(15f, spacing * 2f), false);
                    foreach (var a in picks)
                    {
                        var role = cat == AssetCategories.Bench ? ObjectRole.Bench : ObjectRole.Furniture;
                        if (LeisureKit.Put(ctx, occ, a.Position, LeisureKit.Yaw(a.Position, a.Toward) + math.PI * 0.5f, role, role == ObjectRole.Bench ? AssetMix.One("", ParkArt.RusticBenches) : AssetMix.One("", CampArt.Tables), cat, 1.8f,
                                Hashing.Mix(ctx.Seed, 0x91C0u + (uint)tables)))
                            tables++;
                    }
                }
            }
            int benches = PathBenches(ctx, occ, ctx.Raw("benchEvery"), bench, 0xBE70u) + gladeBenches;
            int lamps = ctx.B("lamps") ? PathLamps(ctx, occ, 22f, AssetMix.One("", ParkArt.ParkLamps), 0x1A70u) : 0;

            // 9. Harvest crates / ladders under some trees.
            int crates = 0;
            if (ctx.B("harvestProps") && treePts.Count > 0)
            {
                string cat = LeisureKit.Category(ctx, "", AssetCategories.FruitCrate, AssetCategories.FarmHay);
                if (cat != null)
                    for (int i = 0; i < treePts.Count && crates < 120; i++)
                    {
                        uint v = Hashing.Mix(ctx.Seed, 0xC7A7u + (uint)i);
                        if (Hashing.Unit(v) > 0.07f) continue;
                        var p = treePts[i] + rowDir * (along * 0.5f) + rowSide * 0.6f;
                        if (LeisureKit.Put(ctx, occ, p, Hashing.Unit(Hashing.Mix(v, 1u)) * 6.28f, ObjectRole.Furniture, AssetMix.One(""), cat, 0.5f, v, 0.5f, 0.1f)) crates++;
                    }
            }
            ctx.Check();

            // 10. Regions: glade = core, the orchard blocks between the paths = green.
            var pieces = FreeSpace.Pieces(region, p => occ.Corridors.Overlaps(p, 0.75f) || occ.HitsPolygon(p, 0.5f) || (glade != null && Poly.Contains(glade, p)),
                math.max(2.5f, math.sqrt(region.Area / 12000f)), 150f, ctx.Cancel);
            // Art pass (a1): the orchard blocks first, the glade last — an untargeted feature (bee hives, picnic) takes the
            // first green region, and the beehives had landed in the picnic glade.
            if (pieces.Count == 0 && glade == null) ctx.AddRegion(region.Ring, "green:0", RegionTags.Green, RegionTags.Core);
            for (int i = 0; i < pieces.Count && i < 24; i++)
                if (i == 0 && glade == null) ctx.AddRegion(pieces[i], "green:0", RegionTags.Green, RegionTags.Core);
                else ctx.AddRegion(pieces[i], "green:" + i, RegionTags.Green);
            if (glade != null) ctx.AddRegion(glade, "glade", RegionTags.Green, RegionTags.Core);

            CommonStats(ctx);
            ctx.Stat("Placemaker.Stat.FruitTrees", trees, Keys.UnitCount);
            ctx.Stat(Keys.StatPicnicTables, tables, Keys.UnitCount);
            ctx.Stat(Keys.StatLamps, lamps, Keys.UnitCount);
            ctx.Stat(Keys.StatFlowerbeds, flowers, Keys.UnitCount);
            ctx.Plan.Facts["fruitTrees"] = trees;
            ctx.Plan.Facts["aisles"] = aisles;
            ctx.Plan.Facts["meadows"] = meadows.Count;
            ctx.Plan.Facts["crates"] = crates;
            ctx.Plan.Facts["hedge"] = hedge;
            ctx.Plan.Facts["windbreak"] = windbreak;
            ctx.Plan.Facts["benches"] = benches;
            ctx.Plan.Facts["glade"] = glade != null ? 1 : 0;
        }

        private static int Shade(List<float2> trees, float2 p, float r)
        {
            int n = 0; float r2 = r * r;
            foreach (var t in trees) if (math.distancesq(t, p) < r2 && ++n >= 6) break;
            return n;
        }

        private static void WarnPaint(LayerContext ctx)
        {
            foreach (var i in ctx.Plan.Issues) if (i.Key == TerrainPaint.PermanentKey) return;
            ctx.Issue(IssueSeverity.Warning, TerrainPaint.PermanentKey);
        }

        private static void Lattice(LayerRegion region, float2 origin, float2 a, float2 b, float pitch, out int a0, out int a1, out int b0, out int b1)
        {
            float minA = float.MaxValue, maxA = float.MinValue, minB = float.MaxValue, maxB = float.MinValue;
            foreach (var v in region.Ring)
            {
                var d = v - origin;
                minA = math.min(minA, math.dot(d, a)); maxA = math.max(maxA, math.dot(d, a));
                minB = math.min(minB, math.dot(d, b)); maxB = math.max(maxB, math.dot(d, b));
            }
            // Lines strictly inside, not hugging the outline (at least 40 % of a pitch in).
            a0 = (int)math.ceil(minA / pitch + 0.4f); a1 = (int)math.floor(maxA / pitch - 0.4f);
            b0 = (int)math.ceil(minB / pitch + 0.4f); b1 = (int)math.floor(maxB / pitch - 0.4f);
        }

        /// <summary>Items every <paramref name="step"/> m along the outline, <paramref name="inset"/> m in, gaps at the entrances.</summary>
        private static int EdgeRow(LayerContext ctx, Occupancy occ, List<Gate> gates, float inset, float step, ObjectRole role, string[] ids, string cat,
            float radius, uint salt)
        {
            var region = ctx.LayerRegion;
            int n = 0;
            for (float s = step * 0.5f; s < region.Perimeter && n < 1500; s += step)
            {
                var p = region.PointAt(s, out var t, out var inward, out _) + inward * inset;
                bool nearGate = false;
                foreach (var g in gates) if (math.distancesq(g.Position, p) < 36f) { nearGate = true; break; }
                if (nearGate) continue;
                uint v = Hashing.Mix(ctx.Seed, salt + (uint)n * 7u + (uint)(s * 3f));
                if (LeisureKit.Put(ctx, occ, p, role == ObjectRole.Tree ? Hashing.Unit(v) * 6.28f : math.atan2(t.x, t.y), role, AssetMix.Pick(ids, v), cat, radius, v,
                        math.max(0.3f, inset - radius - 0.2f), 0.2f, role == ObjectRole.Tree ? 3 : 4, role == ObjectRole.Tree ? 2 + (int)(v % 2u) : -1))
                    n++;
            }
            return n;
        }
    }

    /// <summary>Art pass (a1): orchard boundary and windbreak defaults.</summary>
    public static class OrchardArt
    {
        /// <summary>A low picket-style fence: an orchard park is open, not a compound.</summary>
        public static readonly string[] Fence = { "FenceResidentialLow01", "EU_FenceResidentialLow01" };
        /// <summary>Lombardy-poplar style windbreak.</summary>
        public static readonly string[] Windbreak = { "EU_PoplarTree01", "BirchTree01" };
    }
}
