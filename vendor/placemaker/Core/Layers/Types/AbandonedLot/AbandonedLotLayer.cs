// Abandoned lot: a plot the city forgot. Composition (not soup):
//   * a collapse/attention point ("Rubble focus": centre, street side or far corner) — rubble piles
//     near it thinning to chunks and dust, optional rubble mounds (terraform, blended to the edge);
//   * old foundations: concrete slabs broken into shards along slanted cracks, weeds coming up in the
//     cracks, wall remnants standing in runs along the slab edges, graffiti on them;
//   * overgrowth reclaiming from the outline inwards (weeds, young self-seeded trees in the corners);
//   * a fence rotting in panels, desire paths worn across from gate to gate;
//   * junk dumped at the street side; a squatter camp in the most sheltered corner.
// Subtypes are real compositions: Overgrown (green, slab mostly reclaimed), Rubble field (mounds,
// piles everywhere, dirt ground), Demolished block (clean foundations in a row along the street,
// sorted piles, intact site fence), Squatter camp (shacks round a fire), Fenced wasteland (graded
// gravel behind an intact chain-link fence, waiting for a developer).

using Placemaker.Core.Layers.Types.Scrapyard;
using System.Collections.Generic;
using Placemaker.Core.Layers.Common;
using Placemaker.Core.Layers.Leisure;
using Placemaker.Core.Layers.Types.UrbanRuins;
using Unity.Mathematics;

namespace Placemaker.Core.Layers.Types.Ruins
{
    [AreaGenerator(310, Group = "industrial")]
    public sealed class AbandonedLotLayer : LeisureLayer
    {
        /// <summary>Vehicle / non-park site: plain entrances, no pedestrian/bike/main kinds, never blocks Build.</summary>
        protected override bool TypedEntrances => false;

        public static readonly string[] Subtypes = { "overgrown", "rubbleField", "demolished", "squatterCamp", "fencedWasteland" };

        /// <summary>Vanilla industrial fences (prefab names = their UI icon names); automatic otherwise.</summary>
        public static readonly string[] SiteFences = { "TallIndustrialChainLinkFence", "IndustrialMetalFence", "LowIndustrialWoodFence" };

        protected override string Area => "Abandoned";
        public override string Id => "abandonedLot";
        public override string Icon => "Media/Game/Icons/Bulldozer.svg";
        public override float MinArea => 300f;
        protected override int WantedEntrances(LayerRegion region) => math.min(2, Entrances.WantedByArea(region.Area));
        public override string[] Emits => new[] { RegionTags.Green, RegionTags.Core, StreetFrontage.ShopsTag, DirtJumps.Place };

        protected override List<FeatureInfo> BuildFeatures() => new List<FeatureInfo>
        {
            Feature(DebrisAddOn.LayerId, ("mix", "junk"), ("where", "edges")),
            DirtJumps.InPlace(Feature(DirtJumpsAddOn.LayerId, ("lines", 1.0), ("height", 1.25))),
            DecayKit.Relabel(Feature("bigtrees", ("density", 1.5)), "Placemaker.Abandoned.Feature.bigtrees"),
            Feature("rocks", ("size", "small"), ("clusters", 3.0)),
            Feature("decals", ("theme", "dirt"), ("ground", "lawn"), ("density", 1.5)),
            Feature("terrain", ("shape", "mounds"), ("height", 1.0)),
            StreetShopsFeature(Area),
        };

        protected override void DefineControls(ControlSet c)
        {
            c.Choice("subtype", "overgrown", ControlTier.Essential, "lot", Subtypes).Tip();
            c.Slider("overgrowth", 60, 0, 100, 5, "%", ControlTier.Essential, "lot").Tip();
            c.Slider("rubble", 30, 0, 100, 5, "%", ControlTier.Essential, "lot").Tip();
            c.Slider("junk", 30, 0, 100, 5, "%", ControlTier.Essential, "lot").Tip();
            c.Slider("graffiti", 30, 0, 100, 5, "%", ControlTier.Essential, "lot").Tip();

            c.Slider("foundations", 1, 0, 4, 1, "count", ControlTier.More, "ground").Tip();
            c.Slider("cracking", 60, 0, 100, 5, "%", ControlTier.More, "ground").Tip();
            c.Toggle("groundPaint", true, ControlTier.More, "ground").Tip();
            c.Slider("paintLayer", 1, 1, 4, 1, "count", ControlTier.More, "ground").When("groundPaint=true").Tip();
            c.Toggle("desirePaths", true, ControlTier.More, "ground").Tip();
            c.Toggle("puddles", true, ControlTier.More, "ground");
            c.Asset("slabSurface", AssetCategories.SurfacePaving, ControlTier.More, "ground");

            c.Choice("focus", "centre", ControlTier.More, "rubble", "centre", "street", "corner").Tip();
            c.Choice("pileSize", "medium", ControlTier.More, "rubble", "small", "medium", "large");
            c.Slider("mounds", 0, 0, 4, 0.5, "m", ControlTier.More, "rubble").Tip();
            c.Toggle("walls", true, ControlTier.More, "rubble").Tip();
            c.AssetMulti("rubbleAssets", AssetCategories.RuinsRubble, ControlTier.More, "rubble");

            c.Slider("youngTrees", 20, 0, 80, 5, "perHa", ControlTier.More, "planting").Tip();
            c.Toggle("crackWeeds", true, ControlTier.More, "planting").Tip();
            c.AssetMulti("weedAssets", AssetCategories.RuinsWeeds, ControlTier.More, "planting");
            c.AssetMulti("treeSpecies", AssetCategories.Tree, ControlTier.More, "planting");

            c.Toggle("fence", true, ControlTier.More, "boundary");
            c.Slider("fenceGaps", 40, 0, 100, 5, "%", ControlTier.More, "boundary").When("fence=true").Tip();
            c.Asset("fenceAsset", AssetCategories.Fence, ControlTier.More, "boundary").When("fence=true");

            c.Slider("shelters", 0, 0, 16, 1, "count", ControlTier.More, "camp").Tip();
            c.Toggle("campfire", true, ControlTier.More, "camp");
            c.AssetMulti("shelterAssets", AssetCategories.RuinsShelter, ControlTier.More, "camp");

            c.AssetMulti("junkAssets", AssetCategories.RuinsJunk, ControlTier.More, "assets");
            EntranceMarkers(c);
            DefineStreetShopControls(c);

            c.RandomRange("foundations", 0, 2);
            c.RandomRange("mounds", 0, 2);
            c.RandomRange("shelters", 0, 6);
            c.Fixed("paintLayer");
        }

        protected override void DefinePresets()
        {
            Preset("overgrown", ("subtype", "overgrown"), ("overgrowth", 90.0), ("rubble", 15.0), ("junk", 20.0), ("graffiti", 20.0),
                ("foundations", 1.0), ("cracking", 85.0), ("youngTrees", 45.0), ("fenceGaps", 70.0), ("focus", "centre"), ("mounds", 0.0));
            Preset("rubbleField", ("subtype", "rubbleField"), ("overgrowth", 20.0), ("rubble", 90.0), ("junk", 25.0), ("graffiti", 10.0),
                ("foundations", 0.0), ("mounds", 2.5), ("pileSize", "large"), ("youngTrees", 0.0), ("fenceGaps", 55.0), ("walls", false));
            Preset("demolished", ("subtype", "demolished"), ("overgrowth", 10.0), ("rubble", 45.0), ("junk", 10.0), ("graffiti", 15.0),
                ("foundations", 3.0), ("cracking", 25.0), ("focus", "street"), ("youngTrees", 0.0), ("fenceGaps", 0.0), ("desirePaths", false));
            Preset("squatterCamp", ("subtype", "squatterCamp"), ("overgrowth", 50.0), ("rubble", 20.0), ("junk", 70.0), ("graffiti", 55.0),
                ("foundations", 1.0), ("cracking", 60.0), ("shelters", 7.0), ("campfire", true), ("fenceGaps", 60.0), ("focus", "corner"));
            Preset("fencedWasteland", ("subtype", "fencedWasteland"), ("overgrowth", 35.0), ("rubble", 10.0), ("junk", 15.0), ("graffiti", 25.0),
                ("foundations", 0.0), ("fenceGaps", 0.0), ("youngTrees", 5.0), ("desirePaths", false), ("walls", false));
        }

        /// <summary>Type-specific curated defaults (over GritAssets' shared sets); null = shared only.</summary>
        private static readonly Dictionary<string, GritAssets.Set> ArtObjects = new Dictionary<string, GritAssets.Set>
        {
            // Squatters keep warm round a burn barrel (the pool's "campfire" was a tent).
            [AssetCategories.CampingFire] = new GritAssets.Set(true, "BarrelFire01|BarrelFire02", "BarrelFire02|BarrelFire01"),
        }, ArtSurfaces = null;

        // Art pass: whatever the player left automatic gets the curated gritty defaults (GritAssets).
        protected override void PlanCore(LayerContext ctx) { PlanArt(ctx); GritAssets.Apply(ctx.Plan, ctx.Seed, ArtObjects, ArtSurfaces); GritAssets.CapDecals(ctx.Plan, ctx.LayerRegion.Area, 110f); }

        private void PlanArt(LayerContext ctx)
        {
            var region = ctx.LayerRegion;
            var occ = new Occupancy(region, ctx.Input.Reserved, ctx.Site);
            var guard = new Occupancy(region, ctx.Input.Reserved, ctx.Site);
            var zones = new Zones();
            var gates = Gates(ctx);
            string sub = ctx.S("subtype");
            // Foundations follow the plot edges, like the building that stood here.
            YardKit.GridFrame(region, out var centre, out var axis, out var side, out float major, out float minor);
            float ha = region.Area / 10000f;
            float over = ctx.F("overgrowth") / 100f, rubble = ctx.F("rubble") / 100f, junk = ctx.F("junk") / 100f, graffiti = ctx.F("graffiti") / 100f;
            int paintLayer = ctx.I("paintLayer");
            bool paint = ctx.B("groundPaint");
            var focus = DecayKit.Focus(region, gates, ctx.S("focus"));
            float pileScale = ctx.S("pileSize") == "small" ? 0.7f : ctx.S("pileSize") == "large" ? 1.5f : 1f;
            string weedCat = DecayKit.Cat(ctx, ctx.L("weedAssets"), AssetCategories.RuinsWeeds, AssetCategories.Bush);
            string rubbleCat = rubble > 0f ? DecayKit.Cat(ctx, ctx.L("rubbleAssets"), AssetCategories.RuinsRubble, AssetCategories.Rock) : null;
            string junkCat = junk > 0f ? DecayKit.Cat(ctx, ctx.L("junkAssets"), AssetCategories.RuinsJunk) : null;
            ctx.Check();

            // ── Boundary first: the fence and desire paths shape where everything else may go.
            if (ctx.B("fence"))
            {
                float gaps = ctx.F("fenceGaps") / 100f;
                DecayKit.BrokenFence(ctx, occ, region, gates, gaps, AssetMix.One(ctx.S("fenceAsset"), SiteFences), 0xFE7Cu, sub == "fencedWasteland" ? 2.5f : 3.5f);
            }

            // ── Ground.
            float2[] whole = DecayKit.Inset(region, 1.5f);
            if (paint && (sub == "rubbleField" || sub == "demolished" || sub == "fencedWasteland") && whole != null)
                TerrainPaint.Patch(ctx, whole, paintLayer, sub == "fencedWasteland" ? 0.6f : 0.85f, 4f);
            if (sub == "fencedWasteland")
            {
                // Graded gravel pad behind the fence, waiting for a developer.
                var pad = DecayKit.Inset(region, math.clamp(math.sqrt(region.Area) * 0.08f, 4f, 12f));
                if (pad != null && Geometry.Poly.SignedArea(pad) > 200f) { ctx.Surface(pad, SurfaceRole.Ground, AssetMix.One(""), AssetCategories.SurfaceNatural); }
            }

            // ── Foundations: broken slabs (in a row along the street for a demolished block).
            int wantedSlabs = ctx.I("foundations");
            // Footprint of a former building: full length 9–32 m, about 0.62 as deep.
            float slabL = math.clamp(math.sqrt(region.Area) * 0.13f, 9f, 32f), slabW = slabL * 0.62f;
            var slabs = new List<(OrientedRect r, DecayKit.Slab s)>();
            var slabAsset = AssetMix.One(ctx.S("slabSurface"));
            for (int i = 0; i < wantedSlabs; i++)
            {
                float2 near = sub == "demolished" ? focus + axis * ((i - (wantedSlabs - 1) * 0.5f) * (slabL + 5f)) : focus + side * (i * slabW * 1.2f) * ((i & 1) == 0 ? 1f : -1f);
                if (!region.Contains(near)) near = focus;
                uint s = Hashing.Mix(ctx.Seed, 0x51ABu + (uint)i);
                float scale = 0.8f + 0.4f * Hashing.Unit(s);
                if (!DecayKit.FindRect(region, occ, zones, near, axis, slabL * 0.5f * scale, slabW * 0.5f * scale, 2f, s, out var r, 90)) continue;
                zones.Add(Grow(r, 0.6f).Ring);
                var slab = DecayKit.BrokenSlab(ctx, r, ctx.F("cracking") / 100f, SurfaceRole.Paving, slabAsset, AssetCategories.SurfacePaving, s);
                slabs.Add((r, slab));
            }
            ctx.Check();

            // ── Room for the dirt-jumps feature (kids dig their jumps into the open ground).
            DirtJumps.CarvePlace(ctx, region, occ, zones, region.DeepestPoint(out _, null, 12), axis, 1);

            // ── Desire paths: worn across the lot between the gates (and to the camp later).
            if (ctx.B("desirePaths") && gates.Count >= 2)
                DecayKit.DesirePath(ctx, occ, region, gates[0].Position, gates[1].Position, paintLayer, 0xD351u);

            // ── Wall remnants on the slab edges (runs survive, gaps where they fell), graffiti on them.
            int walls = 0, graffitiCount = 0;
            string wallCat = ctx.B("walls") && slabs.Count > 0 ? DecayKit.Cat(ctx, null, AssetCategories.RuinsWall, AssetCategories.RuinsSlab) : null;
            foreach (var (r, slab) in slabs)
            {
                var ring = r.Ring;
                for (int e = 0; e < 4; e++)
                {
                    var a = ring[e]; var b = ring[(e + 1) % 4];
                    var t = math.normalizesafe(b - a);
                    var inward = new float2(-t.y, t.x);
                    float yaw = math.atan2(t.x, t.y) + math.PI * 0.5f;
                    float survive = sub == "demolished" ? 0.25f : 0.45f;
                    foreach (var p0 in LeisureKit.Along(a, b, 2.6f))
                    {
                        var p = p0 + inward * 0.6f;
                        uint v = Hashing.Mix(ctx.Seed, (int)(p.x * 3f) + 0x3A11, (int)(p.y * 3f));
                        bool stands = wallCat != null && ValueNoise.Clumps(p, 9f, Hashing.Mix(ctx.Seed, 0xA11u)) > 1f - survive;
                        if (stands && occ.Free(p, 0.7f, 0.6f, 0.05f))
                        {
                            ctx.Object(p, yaw, ObjectRole.Furniture, AssetMix.One(""), v, -1, wallCat);
                            occ.Take(p, 0.9f, 2);
                            walls++;
                            if (Hashing.Unit(Hashing.Mix(v, 3u)) < graffiti && AssetAvailability.Has(AssetCategories.DecalGraffiti)
                                && DecayKit.Decal(ctx, guard, p - inward * 1.3f, yaw, AssetCategories.DecalGraffiti, Hashing.Mix(v, 4u))) graffitiCount++;
                        }
                    }
                }
                // Cracks and puddles on the slab.
                if (AssetAvailability.Has(AssetCategories.DecalCracks))
                    foreach (var sh in slab.Shards)
                    {
                        var c = Geometry.Poly.Centroid(sh);
                        uint v = Hashing.Mix(ctx.Seed, (int)(c.x * 5f), (int)(c.y * 5f));
                        if (Hashing.Unit(v) < 0.4f + ctx.F("cracking") / 160f) DecayKit.Decal(ctx, guard, c, Hashing.Unit(v) * 6.283f, AssetCategories.DecalCracks, v);
                    }
                if (ctx.B("puddles") && AssetAvailability.Has(AssetCategories.DecalPuddles))
                    for (int k = 0; k < 2; k++)
                    {
                        uint v = Hashing.Mix(ctx.Seed, (uint)k + 0x9D1u + (uint)(r.Center.x * 7f));
                        var p = r.Local((Hashing.Unit(v) - 0.5f) * r.HalfLength * 1.4f, (Hashing.Unit(Hashing.Mix(v)) - 0.5f) * r.HalfWidth * 1.4f);
                        DecayKit.Decal(ctx, guard, p, Hashing.Unit(v) * 6.283f, AssetCategories.DecalPuddles, v);
                    }
            }
            ctx.Check();

            // ── Squatter camp in the most sheltered spot (far from the gates).
            int shelters = ctx.I("shelters");
            if (sub == "squatterCamp") shelters = math.max(shelters, 4);
            int sheltersPlaced = 0;
            float2 camp = focus;
            bool hasCamp = false;
            if (shelters > 0)
            {
                float campR = math.clamp(3.5f + shelters * 0.75f, 5f, 15f);
                var avoid = LeisureKit.Points(gates);
                if (Spots.Find(occ, campR + 2.5f, Spots.Preference.NearBoundary, Hashing.Mix(ctx.Seed, 0xCA4Bu), out camp, avoid, math.min(30f, minor * 0.4f), 2f)
                    || Spots.Find(occ, campR * 0.7f, Spots.Preference.Open, Hashing.Mix(ctx.Seed, 0xCA4Cu), out camp))
                {
                    hasCamp = true;
                    if (paint) TerrainPaint.Patch(ctx, LeisureKit.Blob(camp, campR + 2f, Hashing.Mix(ctx.Seed, 0xCA4Du), 14, 0.25f), paintLayer, 0.8f, 2f);
                    if (ctx.B("campfire"))
                    {
                        string fire = DecayKit.Cat(ctx, null, AssetCategories.CampingFire, AssetCategories.Camping);
                        if (fire != null) LeisureKit.Put(ctx, occ, camp, 0f, ObjectRole.Furniture, AssetMix.One(""), fire, 1.2f, Hashing.Mix(ctx.Seed, 0xF14Eu), 1f, 0.3f);
                        else occ.Take(camp, 1.2f, 2);
                        // Crates and drums as seats round the fire.
                        if (junkCat != null)
                            for (int k = 0; k < 4; k++)
                            {
                                float a = k * 1.5708f + 0.4f;
                                var p = camp + new float2(math.cos(a), math.sin(a)) * 2.6f;
                                LeisureKit.Put(ctx, occ, p, LeisureKit.Yaw(p, camp), ObjectRole.Furniture, AssetMix.Pick(ctx.L("junkAssets"), (uint)k), junkCat, 0.6f, Hashing.Mix(ctx.Seed, 0x5EA7u + (uint)k), 0.5f, 0.1f);
                            }
                    }
                    else occ.Take(camp, 1.5f, 2);
                    string shelterCat = DecayKit.Cat(ctx, ctx.L("shelterAssets"), AssetCategories.RuinsShelter, AssetCategories.CampingPitch, AssetCategories.Camping, AssetCategories.GardenShed);
                    if (shelterCat != null)
                    {
                        float phase = Hashing.Unit(Hashing.Mix(ctx.Seed, 0x5E1u)) * 6.283f;
                        for (int k = 0; k < shelters * 3 && sheltersPlaced < shelters; k++)
                        {
                            uint v = Hashing.Mix(ctx.Seed, 0x5E10u + (uint)k);
                            float rr = campR * (0.75f + 0.35f * Hashing.Unit(v)) + (k / shelters) * 3f;
                            float a = phase + (k + 0.3f * Hashing.Unit(Hashing.Mix(v))) * 6.283f / shelters;
                            var p = camp + new float2(math.cos(a), math.sin(a)) * rr;
                            if (LeisureKit.Put(ctx, occ, p, LeisureKit.Yaw(p, camp), ObjectRole.Furniture, AssetMix.Pick(ctx.L("shelterAssets"), v), shelterCat, 2f, v, 0.8f, 0.4f)) sheltersPlaced++;
                        }
                    }
                    if (junkCat != null)
                        DecayKit.JunkCluster(ctx, occ, camp + new float2(campR + 3f, 0f), 4 + (int)(junk * 6f), ctx.L("junkAssets"), junkCat, 0xCA5Eu);
                    if (ctx.B("desirePaths") && gates.Count > 0)
                        DecayKit.DesirePath(ctx, occ, region, gates[0].Position, camp, paintLayer, 0xD352u);
                }
                else ctx.Issue(IssueSeverity.Info, "Placemaker.Abandoned.Issue.NoCampSpot");
            }
            ctx.Check();

            // ── Rubble mounds (terraform) around the collapse point, blended to the ground at their rim.
            float rubbleR = math.clamp(math.sqrt(region.Area) * (0.22f + 0.35f * rubble), 10f, 110f) * (sub == "rubbleField" ? 1.4f : 1f);
            int moundCells = 0;
            if (ctx.F("mounds") > 0f)
            {
                var sub0 = LeisureKit.Intersect(region, LayerRegion.Circle(focus, rubbleR, 20));
                if (sub0 != null && sub0.Area > 200f)
                {
                    var res = TerrainShaper.Shape(ctx, sub0, new TerrainSpec
                    {
                        Mode = TerrainMode.Mounds, Height = ctx.F("mounds"), Scale = 16f, MaxSlopePct = 35f, Falloff = math.max(8f, rubbleR * 0.35f),
                        Seed = Hashing.Mix(ctx.Seed, 0x40DDu), Skip = p => zones.Inside(p, 2f) || (hasCamp && math.distance(p, camp) < 14f),
                    });
                    moundCells = res.Cells;
                }
            }

            // ── Rubble: piles near the collapse point thinning outwards (a second and third focus on a rubble field).
            var rubbleTotal = new DecayKit.RubbleResult();
            if (rubbleCat != null)
            {
                var foci = new List<float2> { focus };
                if (sub == "rubbleField" && Spots.Find(occ, 6f, Spots.Preference.Open, Hashing.Mix(ctx.Seed, 0xF0C2u), out var f2, foci, rubbleR * 0.8f, 2f, 1500))
                    foci.Add(f2);
                bool onSlabs = sub == "rubbleField";
                for (int i = 0; i < foci.Count; i++)
                {
                    var res = DecayKit.RubbleField(ctx, occ, guard, region, foci[i], rubbleR * (i == 0 ? 1f : 0.7f), rubble, ctx.L("rubbleAssets"), rubbleCat, pileScale,
                        0x2B1u + (uint)i * 77u, p => !onSlabs && zones.Inside(p, 0.5f), onSlabs ? 900 : 450);
                    rubbleTotal.Piles += res.Piles; rubbleTotal.Chunks += res.Chunks; rubbleTotal.Decals += res.Decals;
                }
                if (sub == "demolished")
                    rubbleTotal.Piles += SortedPiles(ctx, occ, zones, region, focus, axis, side, rubbleCat, pileScale);
            }
            ctx.Check();

            // ── Junk dumped at the street side / along the fence.
            int junkItems = 0;
            if (junkCat != null)
            {
                int heaps = (int)math.round(junk * (1.5f + ha * 3f));
                float2 street = gates.Count > 0 ? gates[0].Position : region.Ring[0];
                var spots = new List<float2>();
                for (int i = 0; i < heaps; i++)
                {
                    if (!Spots.Find(occ, 2.5f, i % 3 == 2 ? Spots.Preference.Open : Spots.Preference.NearBoundary, Hashing.Mix(ctx.Seed, 0x7C0u + (uint)i), out var s, spots, 10f, 1.5f, 1200)) break;
                    spots.Add(s);
                    junkItems += DecayKit.JunkCluster(ctx, occ, s, 3 + (int)(junk * 6f), ctx.L("junkAssets"), junkCat, 0x7C10u + (uint)i * 19u);
                }
            }

            // ── Overgrowth reclaiming from the edges; weeds in the cracks first.
            int crackWeeds = 0;
            if (ctx.B("crackWeeds"))
                foreach (var (r, slab) in slabs)
                    crackWeeds += DecayKit.CrackWeeds(ctx, occ, slab.Cracks, math.saturate(over * 0.9f + 0.05f), ctx.L("weedAssets"), weedCat, 0xC3Au + (uint)(r.Center.x * 3f));
            float edgeReach = sub == "overgrown" ? 22f : 14f;
            var (weeds, trees) = DecayKit.Overgrowth(ctx, occ, region, over, ctx.F("youngTrees"), ctx.L("weedAssets"), weedCat, ctx.L("treeSpecies"), 0x0F6u,
                p => zones.Inside(p, 0.8f), edgeReach);
            ctx.Check();

            // ── Ground decals: dirt and puddles across the open ground, graffiti on the fence line.
            if (AssetAvailability.Has(AssetCategories.DecalDirt))
            {
                int dirt = math.min(400, (int)(region.Area / 250f * (0.3f + rubble)));
                int n = 0;
                foreach (var p in Scatter.Candidates(region, 4f, Hashing.Mix(ctx.Seed, 0xD1Bu), dirt * 3 + 20, ctx.Cancel))
                {
                    if (n >= dirt) break;
                    uint v = Hashing.Mix(ctx.Seed, (int)(p.x * 9f), (int)(p.y * 9f));
                    if (Scatter.Clustering(p, 12f, Hashing.Mix(ctx.Seed, 0xD1Cu), 0.2f) < Hashing.Unit(v)) continue;
                    if (DecayKit.Decal(ctx, guard, p, Hashing.Unit(v) * 6.283f, AssetCategories.DecalDirt, v)) n++;
                }
            }
            if (ctx.B("puddles") && AssetAvailability.Has(AssetCategories.DecalPuddles))
            {
                int puddles = math.clamp((int)(ha * 12f), 2, 60), n = 0;
                foreach (var p in Scatter.Candidates(region, 8f, Hashing.Mix(ctx.Seed, 0x9D2u), puddles * 4, ctx.Cancel))
                {
                    if (n >= puddles) break;
                    if (zones.Inside(p)) continue;
                    if (DecayKit.Decal(ctx, guard, p, Hashing.Unit(Hashing.Mix(ctx.Seed, (int)p.x, (int)p.y)) * 6.283f, AssetCategories.DecalPuddles, Hashing.Mix(ctx.Seed, (int)p.y, (int)p.x))) n++;
                }
            }
            if (ctx.B("fence") && graffiti > 0f)
            {
                var ring = region.Ring;
                for (int e = 0; e < ring.Length; e++)
                {
                    var a = ring[e]; var b = ring[(e + 1) % ring.Length];
                    graffitiCount += DecayKit.GraffitiAlong(ctx, guard, a, b, region.InwardNormal(e), graffiti * 0.35f, 0x6AFu + (uint)e * 97u, 6f);
                }
            }

            EmitGreenRegions(ctx, occ, 80f);
            CommonStats(ctx);
            ctx.Stat("Placemaker.Stat.RubblePiles", rubbleTotal.Piles + rubbleTotal.Chunks, Keys.UnitCount);
            ctx.Stat("Placemaker.Stat.JunkItems", junkItems, Keys.UnitCount);
            ctx.Stat("Placemaker.Stat.Foundations", slabs.Count, Keys.UnitCount);
            ctx.Stat(Keys.StatBushes, weeds + crackWeeds, Keys.UnitCount);
            if (hasCamp) ctx.Stat("Placemaker.Stat.Shelters", sheltersPlaced, Keys.UnitCount);
            ctx.Plan.Facts["foundations"] = slabs.Count;
            ctx.Plan.Facts["walls"] = walls;
            ctx.Plan.Facts["rubblePiles"] = rubbleTotal.Piles;
            ctx.Plan.Facts["rubbleChunks"] = rubbleTotal.Chunks;
            ctx.Plan.Facts["junk"] = junkItems;
            ctx.Plan.Facts["weeds"] = weeds;
            ctx.Plan.Facts["crackWeeds"] = crackWeeds;
            ctx.Plan.Facts["youngTrees"] = trees;
            ctx.Plan.Facts["graffiti"] = graffitiCount;
            ctx.Plan.Facts["shelters"] = sheltersPlaced;
            ctx.Plan.Facts["moundCells"] = moundCells;
            if (wantedSlabs > slabs.Count) ctx.Issue(IssueSeverity.Info, "Placemaker.Abandoned.Issue.FewerFoundations", slabs.Count, wantedSlabs);
        }

        private static OrientedRect Grow(OrientedRect r, float d) =>
            new OrientedRect { Center = r.Center, Axis = r.Axis, HalfLength = r.HalfLength + d, HalfWidth = r.HalfWidth + d };

        /// <summary>Demolished block: the cleared rubble sorted into a neat row of big piles along the street side.</summary>
        private static int SortedPiles(LayerContext ctx, Occupancy occ, Zones zones, LayerRegion region, float2 focus, float2 axis, float2 side, string cat, float scale)
        {
            int n = 0;
            float2 dir = math.dot(side, region.DeepestPoint(out _, null, 12) - focus) > 0f ? side : -side;
            var row = focus + dir * 6f;
            for (int i = -4; i <= 4; i++)
            {
                var p = row + axis * (i * 7f * scale);
                uint v = Hashing.Mix(ctx.Seed, 0x5097u + (uint)(i + 10));
                if (zones.Inside(p, 2f)) continue;
                if (LeisureKit.Put(ctx, occ, p, Hashing.Unit(v) * 6.283f, ObjectRole.Furniture, AssetMix.One(""), cat, 2.4f * scale, v, 1f, 0.6f)) n++;
            }
            return n;
        }
    }
}
