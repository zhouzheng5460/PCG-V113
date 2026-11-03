// Golf: one layer, five formats (subtype = preset family).
//  * course / links / parkland — real hole routing fitted to the outline (GolfRouting): tee boxes,
//    tapered fairways that bend at doglegs, raised greens with pin flags, greenside and fairway bunkers
//    (sand, sunk into the ground), lateral water hazards (permanent ponds), cart paths beside every hole
//    with walks from green to next tee, a clubhouse with carts near the entrance, trees in the rough.
//  * pitch & putt — the same routing at 30–110 m holes, narrow fairways, greenside bunkers, footpaths.
//  * disc golf — wooded fairway corridors (no surfaces), paved tee pads, chain baskets, a narrow trail.
//  * mini golf — packed felt lanes with borders, an obstacle per lane, themed dressing (GolfVariants).
//  * driving range — a covered tee line, a fan of target greens down the range, safety netting (GolfVariants).
// Everything goes through Occupancy, so Reserved space and site obstacles are respected; terraform is
// small pads only (greens, tees, bunkers) clamped to stay blended near the outline.

using System;
using System.Collections.Generic;
using Placemaker.Core.Geometry;
using Placemaker.Core.Layers.Common;
using Placemaker.Core.Layers.Leisure;
using Unity.Mathematics;

namespace Placemaker.Core.Layers.Types.Golf
{
    [AreaGenerator(207, Group = "sports")]
    public sealed partial class GolfLayer : LeisureLayer
    {
        public const string PracticeTag = "practice";
        internal const int ClubKind = 53;

        protected override string Area => "Golf";
        public override string Id => "golf";
        public override string Icon => "Media/Game/Icons/LargeSportsParks.svg";
        public override float MinArea => 600f;
        public override string[] Emits => new[] { RegionTags.Green, RegionTags.Core, StreetFrontage.ShopsTag, PracticeTag };

        public static readonly string[] Subtypes = { "course", "pitchPutt", "miniGolf", "drivingRange", "discGolf" };

        /// <summary>A feature labelled with its layer name (no Placemaker.Feature.* key of its own).</summary>
        private static FeatureInfo Plain(string layer, params (string key, object value)[] preset)
        {
            var f = Feature(layer, preset);
            f.LabelKey = "";
            return f;
        }

        /// <summary>Art pass: a manicured course gets no scattered leaf/dirt decals by default (restrooms only).</summary>
        protected override string[] DefaultFeatures => new[] { "restrooms" };

        protected override List<FeatureInfo> BuildFeatures()
        {
            var practice = Feature(PuttingGreenAddOn.LayerId);
            practice.Target = PracticeTag;
            practice.LabelKey = "Placemaker.Golf.Feature.practiceGreen";
            practice.TooltipKey = "Placemaker.Golf.Feature.practiceGreen.Tooltip";
            return new List<FeatureInfo>
            {
                practice, Feature("restrooms"), Feature("shops", ("count", 1.0), ("where", "entrances")), StreetShopsFeature(Area),
                Plain("flowerbeds", ("density", 4.0)), Plain("bigtrees", ("density", 1.5)), Feature("rocks", ("clusters", 3.0)),
                Feature("decals", ("theme", "leaves"), ("density", 1.0)),
            };
        }

        protected override void DefineControls(ControlSet c)
        {
            c.Choice("subtype", "course", ControlTier.Essential, "course", Subtypes).Tip();
            c.Slider("holes", 9, 1, 18, 1, "count", ControlTier.Essential, "course").Tip();
            c.Slider("holeLength", 170, 20, 450, 5, "m", ControlTier.Essential, "course").Tip();
            c.Slider("doglegs", 40, 0, 100, 5, "%", ControlTier.Essential, "course").Tip();
            c.Choice("routing", "loop", ControlTier.More, "course", "loop", "outAndBack", "free").Tip();

            c.Slider("bunkers", 3, 0, 8, 1, "count", ControlTier.Essential, "hazards").Tip();
            c.Slider("water", 1, 0, 6, 1, "count", ControlTier.Essential, "hazards").Tip();
            c.Slider("bunkerDepth", 0.8, 0.3, 1.5, 0.1, "m", ControlTier.More, "hazards");
            c.Slider("waterDepth", 1.5, 0.5, 3, 0.5, "m", ControlTier.More, "hazards");

            c.Slider("fairwayWidth", 32, 8, 60, 1, "m", ControlTier.More, "greens").Tip();
            c.Slider("greenSize", 26, 8, 40, 1, "m", ControlTier.More, "greens").Tip();
            c.Slider("teeBoxes", 2, 1, 3, 1, "count", ControlTier.More, "greens").Tip();
            c.Toggle("flags", true, ControlTier.More, "greens");
            c.Toggle("shapeGreens", true, ControlTier.More, "greens").Tip();

            c.Toggle("cartPaths", true, ControlTier.Essential, "paths").Tip();
            c.Toggle("clubhouse", true, ControlTier.More, "paths").Tip();
            c.Toggle("teeBenches", true, ControlTier.More, "paths");
            c.Toggle("lamps", false, ControlTier.More, "paths");
            c.Asset("pathAsset", AssetCategories.Pathway, ControlTier.More, "paths");

            c.Slider("treeDensity", 60, 0, 400, 10, "perHa", ControlTier.Essential, "planting").Tip();
            c.Slider("bushDensity", 25, 0, 300, 5, "perHa", ControlTier.More, "planting");
            c.Toggle("roughPaint", true, ControlTier.More, "planting").Tip();
            c.Slider("roughLayer", 1, 1, 4, 1, "count", ControlTier.More, "planting").When("roughPaint=true").Tip();
            c.AssetMulti("treeSpecies", AssetCategories.Tree, ControlTier.More, "planting");

            c.Choice("miniTheme", "classic", ControlTier.Essential, "course", "classic", "adventure", "garden").When("subtype=miniGolf").Tip();
            c.Slider("bays", 14, 4, 40, 1, "count", ControlTier.Essential, "course").When("subtype=drivingRange").Tip();
            c.Slider("targets", 5, 1, 8, 1, "count", ControlTier.Essential, "course").When("subtype=drivingRange");

            c.Asset("fairwaySurface", AssetCategories.SurfaceGrass, ControlTier.More, "assets");
            c.Asset("greenSurface", AssetCategories.SurfaceGrass, ControlTier.More, "assets");
            c.Asset("bunkerSurface", AssetCategories.SurfaceSand, ControlTier.More, "assets");
            c.Asset("flagAsset", AssetCategories.GolfFlag, ControlTier.More, "assets");
            c.Asset("netAsset", AssetCategories.Fence, ControlTier.More, "assets");
            EntranceMarkers(c);
            DefineStreetShopControls(c);

            c.Fixed("subtype");
            c.RandomRange("holes", 3, 12);
            c.RandomRange("holeLength", 60, 260);
            c.RandomRange("treeDensity", 20, 160);
            c.RandomRange("water", 0, 3);
            c.Chance("lamps", 0.2f);
        }

        protected override void DefinePresets()
        {
            Preset("parkland", ("subtype", "course"), ("holes", 9.0), ("holeLength", 180.0), ("doglegs", 40.0), ("bunkers", 2.0), ("water", 2.0),
                ("treeDensity", 140.0), ("fairwayWidth", 30.0), ("routing", "loop"));
            Preset("links", ("subtype", "course"), ("holes", 9.0), ("holeLength", 220.0), ("doglegs", 65.0), ("bunkers", 6.0), ("water", 0.0),
                ("treeDensity", 0.0), ("bushDensity", 90.0), ("fairwayWidth", 40.0), ("routing", "outAndBack"), ("roughLayer", 2.0), ("cartPaths", false));
            Preset("pitchPutt", ("subtype", "pitchPutt"), ("holes", 9.0), ("holeLength", 65.0), ("doglegs", 0.0), ("bunkers", 2.0), ("water", 1.0),
                ("fairwayWidth", 14.0), ("greenSize", 16.0), ("teeBoxes", 1.0), ("cartPaths", false), ("treeDensity", 50.0));
            Preset("miniGolf", ("subtype", "miniGolf"), ("holes", 18.0), ("miniTheme", "adventure"), ("treeDensity", 12.0), ("bushDensity", 80.0),
                ("lamps", true), ("roughPaint", false));
            Preset("drivingRange", ("subtype", "drivingRange"), ("bays", 16.0), ("targets", 5.0), ("lamps", true), ("treeDensity", 20.0), ("bunkers", 1.0));
            Preset("discGolf", ("subtype", "discGolf"), ("holes", 9.0), ("holeLength", 85.0), ("doglegs", 30.0), ("bunkers", 0.0), ("water", 0.0),
                ("fairwayWidth", 10.0), ("treeDensity", 260.0), ("bushDensity", 90.0), ("cartPaths", false), ("teeBoxes", 1.0), ("routing", "free"));
        }

        protected override void PlanCore(LayerContext ctx)
        {
            switch (ctx.S("subtype"))
            {
                case "miniGolf": PlanMini(ctx); break;
                case "drivingRange": PlanRange(ctx); break;
                default: PlanCourse(ctx); break;
            }
        }

        // ───────────── routed formats: course, pitch & putt, disc golf ─────────────

        private sealed class Format
        {
            public bool Disc, Pitch;
            public float MinL, MaxL, MinHw, MaxHw, MinRg, MaxRg, Edge, Gap, TeeOffset, BunkerR;
            public bool FairwaySurface;
        }

        private static Format FormatFor(string subtype)
        {
            switch (subtype)
            {
                case "pitchPutt":
                    return new Format { Pitch = true, MinL = 30, MaxL = 110, MinHw = 5, MaxHw = 10, MinRg = 5, MaxRg = 10, Edge = 3, Gap = 5, TeeOffset = 9, BunkerR = 3f, FairwaySurface = true };
                case "discGolf":
                    return new Format { Disc = true, MinL = 35, MaxL = 150, MinHw = 3, MaxHw = 7, MinRg = 2.5f, MaxRg = 2.5f, Edge = 3, Gap = 4, TeeOffset = 8, BunkerR = 0f };
                default:
                    return new Format { MinL = 90, MaxL = 450, MinHw = 8, MaxHw = 30, MinRg = 6, MaxRg = 20, Edge = 6, Gap = 9, TeeOffset = 14, BunkerR = 5f, FairwaySurface = true };
            }
        }

        private void PlanCourse(LayerContext ctx)
        {
            var region = ctx.LayerRegion;
            var fmt = FormatFor(ctx.S("subtype"));
            var occ = new Occupancy(region, ctx.Input.Reserved, ctx.Site);
            var haz = new Occupancy(region, ctx.Input.Reserved, ctx.Site);   // hazards may nibble fairways, never greens/tees/paths
            var gates = Gates(ctx);
            var pathAsset = GolfAssets.PathRef(ctx.S("pathAsset"));

            // Clubhouse beside the main entrance; the course starts and (for loops) ends there.
            float2 home = Clubhouse(ctx, occ, gates, fmt.Disc ? 3f : 7f, fmt.Disc ? ShelterChain : ClubChain, !fmt.Disc);
            haz.Take(home, fmt.Disc ? 3f : 7f, 0);
            ctx.Check();

            var spec = new RoutingSpec
            {
                Holes = ctx.I("holes"),
                Length = math.clamp(ctx.F("holeLength"), fmt.MinL, fmt.MaxL),
                HalfWidth = math.clamp(ctx.F("fairwayWidth") * 0.5f, fmt.MinHw, fmt.MaxHw),
                GreenRadius = math.clamp(ctx.F("greenSize") * 0.5f, fmt.MinRg, fmt.MaxRg),
                EdgeMargin = fmt.Edge, Gap = fmt.Gap, TeeOffset = fmt.TeeOffset,
                DoglegChance = ctx.F("doglegs") / 100f, Style = ctx.S("routing"),
                Seed = Hashing.Mix(ctx.Seed, 0x601Fu),
            };
            spec.MinLength = math.max(fmt.MinL * 0.45f, spec.GreenRadius * 2f + 12f);
            var firstTees = new List<float2>();
            float2 inward = math.normalizesafe(region.Centroid - home, new float2(0, 1));
            for (int k = 0; k < 8; k++)
            {
                float a = math.atan2(inward.y, inward.x) + (k % 2 == 0 ? 1 : -1) * (k / 2) * math.radians(40f);
                firstTees.Add(home + new float2(math.cos(a), math.sin(a)) * ((fmt.Disc ? 3f : 7f) + 12f));
            }
            var holes = RouteBest(ctx, region, firstTees, home, fmt.Disc ? 3f : 7f, fmt, ref spec);
            foreach (var h in holes) GolfRouting.Take(occ, h, spec);
            if (holes.Count < spec.Holes) ctx.Issue(IssueSeverity.Info, "Placemaker.Golf.Issue.FewerHoles", holes.Count, spec.Holes);
            if (holes.Count == 0) ctx.Issue(IssueSeverity.Warning, "Placemaker.Golf.Issue.NoHoles");
            foreach (var h in holes) { haz.Take(h.Green, spec.GreenRadius + 1f, GolfRouting.GreenKind); haz.Take(h.Tee, 3f, GolfRouting.TeeKind); }
            ctx.Check();

            // Holes: fairway, green, tee boxes, flags and markers.
            bool warned = false;
            // Vanilla has no golf props: explicit stand-ins (GolfAssets) unless the player picked something.
            var (flagAsset, flagCat) = GolfAssets.FlagProp(ctx.S("flagAsset"), fmt.Disc);
            if (!ctx.B("flags")) flagCat = null;
            var (teeAsset, teeCat) = GolfAssets.TeeMarkerProp();
            var fairway = GolfAssets.FairwayRef(ctx.S("fairwaySurface"));
            var teeTurf = GolfAssets.TeeRef(ctx.S("fairwaySurface"));
            var green = GolfAssets.GreenRef(ctx.S("greenSurface"));
            int teeCount = fmt.Disc ? 1 : ctx.I("teeBoxes");
            foreach (var h in holes)
            {
                uint hv = Hashing.Mix(ctx.Seed, 0x4010u + (uint)h.Number * 131u);
                var d = h.TeeDir; var side = new float2(-d.y, d.x);
                if (fmt.FairwaySurface)
                {
                    float start = fmt.Pitch ? 0.12f : math.min(0.3f, 40f / math.max(1f, h.Length));
                    var fw = FairwayRing(h, spec.HalfWidth * (fmt.Pitch ? 0.75f : 0.82f), start, 1f - spec.GreenRadius * 0.4f / h.Length, hv);
                    if (fw != null) ctx.Surface(fw, SurfaceRole.Grass, fairway, AssetCategories.SurfaceGrass);
                }
                if (!fmt.Disc)
                {
                    var g = LeisureKit.Blob(h.Green, spec.GreenRadius, hv, 18, 0.16f);
                    ctx.Surface(g, SurfaceRole.Grass, green, AssetCategories.SurfaceGrass);
                    if (ctx.B("shapeGreens")) Pad(ctx, region, g, h.Green, 0.5f, ref warned);
                }
                // Tee boxes: front tee at the corridor start, back tees stepped behind it.
                for (int k = 0; k < teeCount; k++)
                {
                    var c = h.Tee - d * (k * 7.5f);
                    if (k > 0 && !occ.Free(c, 3.2f, 1.5f, 0.3f)) break;
                    if (k > 0) { occ.Take(c, 3.2f, GolfRouting.TeeKind); haz.Take(c, 3.2f, GolfRouting.TeeKind); }
                    if (fmt.Disc) ctx.Surface(LayerRegion.Rect(c, d, 2.2f, 0.9f), SurfaceRole.Paving, GolfAssets.KerbRef(), AssetCategories.SurfacePaving);
                    else
                    {
                        var tee = LayerRegion.Rect(c, d, 3f, 2.4f);
                        ctx.Surface(tee, SurfaceRole.Grass, teeTurf, AssetCategories.SurfaceGrass);
                        if (ctx.B("shapeGreens")) Pad(ctx, region, tee, c, 0.35f, ref warned);
                    }
                }
                if (teeCat != null)
                    for (int e = -1; e <= 1; e += 2)
                        ctx.Object(h.Tee + d * 1.5f + side * (e * (fmt.Disc ? 1.4f : 2.2f)), LeisureKit.Yaw(h.Tee, h.Tee + d), ObjectRole.Furniture,
                            teeAsset, Hashing.Mix(hv, (uint)e + 3u), -1, teeCat);
                if (flagCat != null)
                {
                    float off = fmt.Disc ? 0f : spec.GreenRadius * 0.35f * Hashing.Unit(Hashing.Mix(hv, 5u));
                    float ang = Hashing.Unit(Hashing.Mix(hv, 6u)) * 6.283f;
                    ctx.Object(h.Green + new float2(math.cos(ang), math.sin(ang)) * off, 0f, ObjectRole.Furniture, flagAsset, hv, -1, flagCat);
                }
            }
            ctx.Check();

            // Paths: cart paths beside the holes (or a footpath walk), clubhouse ↔ first tee / last green.
            CoursePaths(ctx, occ, haz, gates, holes, home, spec, fmt, pathAsset);
            ctx.Check();

            // Hazards: bunkers and ponds, after the paths so they never cut a path.
            int bunkers = fmt.Disc ? 0 : Bunkers(ctx, region, haz, occ, holes, spec, fmt, ref warned);
            int ponds = Ponds(ctx, region, haz, occ, holes, spec, fmt);
            ctx.Check();

            // Furniture: tee benches, lamps, carts, the practice spot.
            int benches = 0;
            if (ctx.B("teeBenches"))
                foreach (var h in holes)
                {
                    var d = h.TeeDir; var side = new float2(-d.y, d.x);
                    for (int e = -1; e <= 1; e += 2)
                    {
                        var p = h.Tee - d * 2f + side * (e * (fmt.Disc ? 4f : 6.5f));
                        if (LeisureKit.Put(ctx, occ, p, LeisureKit.Yaw(p, h.Tee), ObjectRole.Bench, AssetMix.One(""), null, 1.3f,
                            Hashing.Mix(ctx.Seed, 0xBE40u + (uint)h.Number), 0.5f, 0.3f)) { benches++; break; }
                    }
                }
            if (ctx.B("lamps")) PathLamps(ctx, occ, 28f, AssetMix.One(""), 0x1A40u);
            PracticeRegion(ctx, occ, home, fmt.Disc ? 6f : 10f);
            ctx.Check();

            // Planting: trees and bushes in the rough (corridors, greens and paths are taken), rough paint.
            Planting(ctx, occ, region, null);
            if (ctx.B("roughPaint"))
            {
                var inset = region.InsetVertices(math.min(4f, math.sqrt(region.Area) * 0.05f));
                if (Poly.IsSimple(inset) && Poly.SignedArea(inset) > 100f) TerrainPaint.Patch(ctx, inset, ctx.I("roughLayer"), 0.55f, 5f);
            }

            EmitGreenRegions(ctx, occ);
            CommonStats(ctx);
            float total = 0f; int par = 0;
            foreach (var h in holes) { total += h.Length; par += h.Par; }
            ctx.Stat("Placemaker.Stat.Holes", holes.Count, Keys.UnitCount);
            ctx.Stat("Placemaker.Stat.CourseLength", Math.Round(total), Keys.UnitM);
            ctx.Stat("Placemaker.Stat.Par", par, Keys.UnitCount);
            ctx.Stat("Placemaker.Stat.Bunkers", bunkers, Keys.UnitCount);
            ctx.Plan.Facts["holes"] = holes.Count;
            ctx.Plan.Facts["par"] = par;
            ctx.Plan.Facts["bunkers"] = bunkers;
            ctx.Plan.Facts["ponds"] = ponds;
            int doglegs = 0;
            foreach (var h in holes) if (h.Dogleg) doglegs++;
            ctx.Plan.Facts["doglegs"] = doglegs;
        }

        /// <summary>Tapered fairway outline along the hole's centre line with rounded ends; null when it would self-intersect.</summary>
        internal static float2[] FairwayRing(GolfHole h, float hw, float f0, float f1, uint seed)
        {
            if (f1 <= f0 + 0.05f) return null;
            for (int attempt = 0; attempt < 2; attempt++, hw *= 0.7f)
            {
                int n = math.max(4, (int)((f1 - f0) * h.Length / 6f));
                var left = new List<float2>(); var right = new List<float2>();
                float2 t0 = default, t1 = default, p0 = default, p1 = default; float w0 = 0, w1 = 0;
                for (int i = 0; i <= n; i++)
                {
                    float u = i / (float)n;
                    var p = h.At(math.lerp(f0, f1, u), out var t);
                    var nrm = new float2(-t.y, t.x);
                    float taper = math.lerp(0.55f, 1f, Smooth(u / 0.35f)) * math.lerp(1f, 0.75f, Smooth((u - 0.7f) / 0.3f));
                    float wobble = 1f + 0.24f * (ValueNoise.Sample(new float2(u * 5f, 3.7f), seed) - 0.5f);
                    float w = math.max(1.5f, hw * taper * wobble);
                    left.Add(p + nrm * w); right.Add(p - nrm * w);
                    if (i == 0) { t0 = t; p0 = p; w0 = w; }
                    if (i == n) { t1 = t; p1 = p; w1 = w; }
                }
                var ring = new List<float2>(right);
                for (int k = 1; k < 6; k++) ring.Add(p1 + Rot(t1, math.radians(-90f + k * 30f)) * w1);
                for (int i = left.Count - 1; i >= 0; i--) ring.Add(left[i]);
                for (int k = 1; k < 6; k++) ring.Add(p0 + Rot(t0, math.radians(90f + k * 30f)) * w0);
                var clean = Poly.Clean(ring, 0.3f);
                if (clean.Length >= 3 && Poly.IsSimple(clean)) return Poly.EnsureCcw(clean);
            }
            return null;
        }

        internal static float2 Rot(float2 v, float a) => new float2(v.x * math.cos(a) - v.y * math.sin(a), v.x * math.sin(a) + v.y * math.cos(a));
        private static float Smooth(float x) { x = math.saturate(x); return x * x * (3f - 2f * x); }

        /// <summary>
        /// A levelled pad (green, tee, bunker) as one terraform polygon. The change is clamped by the
        /// distance to the outline so nothing near the edge stands proud of the original ground.
        /// </summary>
        internal static void Pad(LayerContext ctx, LayerRegion region, float2[] ring, float2 centre, float delta, ref bool warned)
        {
            float edge = region.BoundaryDistance(centre, 60f);
            float max = 0.25f * edge + 0.2f;
            delta = math.clamp(delta, -max, max);
            if (math.abs(delta) < 0.15f) return;
            ctx.Plan.Terraform.Add(new PlannedTerraform
            {
                Ring = Poly.To3D(ring, ctx.H), Kind = delta > 0f ? TerraformKind.Raise : TerraformKind.Lower,
                TargetHeight = ctx.H(centre) + delta, Permanent = true,
            });
            if (!warned) { ctx.Issue(IssueSeverity.Warning, TerrainShaper.PermanentKey); warned = true; }
        }

        /// <summary>Clubhouse (or starter hut) just inside the main entrance, with a paved apron and parked carts. Returns its centre.</summary>
        internal static readonly string[] ClubChain = { AssetCategories.GolfClubhouse, AssetCategories.Restroom, AssetCategories.Shelter, AssetCategories.Kiosk };
        internal static readonly string[] ShelterChain = { AssetCategories.Shelter };
        internal static readonly string[] KioskChain = { AssetCategories.Kiosk, AssetCategories.Shelter };

        private static float2 Clubhouse(LayerContext ctx, Occupancy occ, List<Gate> gates, float radius, string[] chain, bool apron)
        {
            var region = ctx.LayerRegion;
            float2 home = region.Centroid;
            bool found = false;
            if (gates.Count > 0)
            {
                var g = gates[0];
                var inward = region.InwardNormal(g.Segment);
                for (float s = radius + 4f; s <= radius + 40f && !found; s += 3f)
                    for (int k = 0; k < 5 && !found; k++)
                    {
                        float lateral = (k % 2 == 0 ? 1 : -1) * (k / 2) * 6f;
                        var p = g.Position + inward * s + new float2(-inward.y, inward.x) * lateral;
                        if (occ.Free(p, radius, radius + 1f, 0.5f)) { home = p; found = true; }
                    }
            }
            if (!found && !Spots.Find(occ, radius, Spots.Preference.NearBoundary, Hashing.Mix(ctx.Seed, 0xC1Bu), out home)) home = region.Centroid;
            occ.Take(home, radius, ClubKind);
            if (!ctx.B("clubhouse")) return home;
            var facing = gates.Count > 0 ? gates[0].Position : region.Centroid;
            // Vanilla has no clubhouse prop (the automatic chain ended at an outhouse): a pavilion on a
            // paved terrace for courses and ranges, a campground shelter as a disc golf starter hut.
            var (club, clubCat) = GolfAssets.Prop("", chain[0],
                apron ? AssetMix.One(GolfAssets.Clubhouse, "Gazebo01", "Gazebo02") : AssetMix.One("CampgroundShelter01", "CampgroundShelter02"), AssetCategories.Shelter);
            ctx.Object(home, LeisureKit.Yaw(home, facing), ObjectRole.Furniture, club, Hashing.Mix(ctx.Seed, 0xC1B1u), -1, clubCat);
            if (apron)
            {
                var axis = math.normalizesafe(facing - home, new float2(0, 1));
                ctx.Surface(LayerRegion.Rect(home, axis, radius + 1.5f, radius + 1.5f), SurfaceRole.Paving, GolfAssets.ApronRef(), AssetCategories.SurfacePaving);
                // Pro shop beside the pavilion (the carts park on the other side, the entrance walk arrives in front).
                var side0 = new float2(-axis.y, axis.x);
                ctx.Object(home - side0 * (radius - 1f), LeisureKit.Yaw(home, facing), ObjectRole.Furniture,
                    AssetMix.One(GolfAssets.ProShop, "Kiosk02"), Hashing.Mix(ctx.Seed, 0xC1B2u), -1, AssetCategories.Kiosk);
                string cart = AssetAvailability.Has(AssetCategories.GolfCart) ? AssetCategories.GolfCart : null;
                if (cart != null)
                {
                    var side = new float2(-axis.y, axis.x);
                    for (int i = 0; i < 3; i++)
                        LeisureKit.Put(ctx, occ, home + side * (radius + 3f) + axis * (i * 2.2f - 2.2f), LeisureKit.Yaw(home, home + side), ObjectRole.Furniture,
                            AssetMix.One(""), cart, 1.1f, Hashing.Mix(ctx.Seed, 0xCA27u + (uint)i), 0.5f, 0.1f);
                }
            }
            return home;
        }

        /// <summary>
        /// Routes on a scratch occupancy; when hardly anything fits (small areas) it retries with a compact
        /// course — narrower corridors, smaller greens, shorter holes — and keeps the attempt with most holes.
        /// </summary>
        private static List<GolfHole> RouteBest(LayerContext ctx, LayerRegion region, List<float2> firstTees, float2 home, float clubR, Format fmt, ref RoutingSpec spec)
        {
            List<GolfHole> best = null; var bestSpec = spec; var s = spec;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                var trial = new Occupancy(region, ctx.Input.Reserved, ctx.Site);
                trial.Take(home, clubR + 5f, 0);
                var holes = GolfRouting.Route(region, trial, firstTees, home, s, ctx.Cancel);
                if (best == null || holes.Count > best.Count) { best = holes; bestSpec = s; }
                if (best.Count >= math.min(spec.Holes, 3)) break;
                s.HalfWidth = math.max(fmt.MinHw * 0.6f, s.HalfWidth * 0.65f);
                s.GreenRadius = math.max(fmt.MinRg * 0.8f, s.GreenRadius * 0.75f);
                s.Length *= 0.6f; s.MinLength = math.max(20f, s.MinLength * 0.7f);
                s.EdgeMargin *= 0.6f; s.Gap *= 0.6f; s.TeeOffset *= 0.75f;
            }
            spec = bestSpec;
            return best;
        }

        private static void CoursePaths(LayerContext ctx, Occupancy occ, Occupancy haz, List<Gate> gates, List<GolfHole> holes, float2 home,
            RoutingSpec spec, Format fmt, AssetRef asset)
        {
            var region = ctx.LayerRegion;
            bool cart = ctx.B("cartPaths") && !fmt.Disc && !fmt.Pitch;
            // Cart paths are 3 m concrete lanes (Pavement Path), not the 8 m promenade the automatic wide walk resolves to.
            var role = NetRole.Pathway;
            float phw = Composer.DefaultHalfWidth(role);
            float clubR = fmt.Disc ? 3f : 7f;
            int links = 0;

            void Emit(float2 a, float2 b)
            {
                LeisureKit.Path(ctx, occ, a, b, role, asset);
                haz.TakeCorridor(a, b, phw, 100);
            }
            // Straight link, else a dog-leg round the given obstacle centre (e.g. a green between the two ends).
            bool Link(float2 a, float2 b, float2 around, float aroundR)
            {
                if (math.distance(a, b) < 2f) return true;
                if (math.distance(a, b) <= 90f && Clear(region, occ, a, b, phw, 10f)) { Emit(a, b); links++; return true; }
                var mid = math.lerp(a, b, 0.5f);
                var away = math.normalizesafe(mid - around, Rot(math.normalizesafe(b - a), math.PI * 0.5f));
                foreach (float extra in new[] { 1.5f, 5f })
                {
                    var w = around + away * (aroundR + phw + extra);
                    if (math.distance(a, w) + math.distance(w, b) <= 110f && Clear(region, occ, a, w, phw, 10f) && Clear(region, occ, w, b, phw, 10f))
                    { Emit(a, w); Emit(w, b); links++; return true; }
                }
                return false;
            }

            // Entrances to the clubhouse.
            foreach (var g in gates)
            {
                var dir = math.normalizesafe(home - g.Position);
                Link(g.Position, home - dir * (clubR + 0.6f + phw), home, clubR);
            }
            float2 prevEnd = home + math.normalizesafe(holes.Count > 0 ? holes[0].Tee - home : region.Centroid - home) * (clubR + 0.6f + phw);
            float2 prevAround = home; float prevR = clubR;
            for (int i = 0; i < holes.Count; i++)
            {
                var h = holes[i];
                var d = h.TeeDir; var side = new float2(-d.y, d.x);
                // Walk ends beside the green on the side facing what comes next (next tee, or home after the last hole).
                var ap = h.ApproachDir; var perp = new float2(-ap.y, ap.x);
                var next = i + 1 < holes.Count ? holes[i + 1].Tee : home;
                float endSide = Poly.Cross(ap, next - h.Green) >= 0f ? 1f : -1f;
                float2 end = h.Green + ap * 1.5f + perp * endSide * (spec.GreenRadius + 3f + phw);
                float teeSide = Poly.Cross(d, prevEnd - h.Tee) >= 0f ? 1f : -1f;
                float2 start = h.Tee - d * 1.5f + side * teeSide * (GolfRouting.HalfWidthAt(0f, h.Length, spec) + phw + 1.5f);
                if (cart)
                {
                    // Alongside the hole on the side that fits best; blocked stretches split it into runs.
                    List<List<float2>> best = null; int bestCount = -1;
                    foreach (float s in new[] { teeSide, -teeSide })
                    {
                        var runs = new List<List<float2>>(); var cur = new List<float2>(); int count = 0;
                        for (int k = 0; k < h.Line.Length; k += 2)
                        {
                            float f = k / (float)math.max(1, h.Line.Length - 1);
                            var p = h.At(f, out var t);
                            var n = new float2(-t.y, t.x) * s;
                            float off = math.max(GolfRouting.HalfWidthAt(math.min(h.Length, f * h.Length + 12f), h.Length, spec) + phw + 2.5f,
                                f > 0.75f ? spec.GreenRadius + phw + 4f : 0f);
                            var q = p + n * off;
                            if (region.ContainsDisc(q, phw + 0.5f) && occ.Free(q, phw, phw + 0.5f, 0.2f)) { cur.Add(q); count++; }
                            else { if (cur.Count >= 2) runs.Add(cur); cur = new List<float2>(); }
                        }
                        if (cur.Count >= 2) runs.Add(cur);
                        if (count > bestCount) { bestCount = count; best = runs; }
                    }
                    if (best != null && best.Count > 0)
                    {
                        Link(prevEnd, best[0][0], h.Tee, 3f);
                        for (int r = 0; r < best.Count; r++)
                        {
                            var simple = Poly.SimplifyPolyline(best[r], 1.5f);
                            for (int k = 0; k + 1 < simple.Length; k++) Emit(simple[k], simple[k + 1]);
                            if (r + 1 < best.Count) Link(simple[simple.Length - 1], best[r + 1][0], h.Green, spec.GreenRadius + 2f);
                            else prevEnd = simple[simple.Length - 1];
                        }
                        prevAround = h.Green; prevR = spec.GreenRadius + 2f;
                        continue;
                    }
                }
                // Walking formats: a footpath from the previous green (or the clubhouse) to this tee.
                Link(prevEnd, start, prevAround, prevR);
                prevEnd = end; prevAround = h.Green; prevR = spec.GreenRadius + 2f;
            }
            if (holes.Count > 0 && math.distance(prevEnd, home) < 160f)
                Link(prevEnd, home + math.normalizesafe(prevEnd - home) * (clubR + 0.6f + phw), prevAround, prevR);
            ctx.Plan.Facts["pathLinks"] = links;
        }

        /// <summary>True when a straight path a→b stays inside and clear of everything taken (sampled).</summary>
        /// <summary>
        /// Disc test for paths. Close to its ends a path may touch what it leads to — the clubhouse, a tee
        /// box, a green apron — but never a placed object or anything reserved.
        /// </summary>
        private static bool DiscBlocked(Occupancy occ, float2 p, float hw, bool nearEnd)
        {
            if (!nearEnd) return occ.Discs.Overlaps(p, hw, 0.2f);
            var near = new List<int>();
            occ.Discs.Query(p, hw + 0.2f, near);
            foreach (int i in near)
            {
                var it = occ.Discs.Items[i];
                if (it.Kind == ClubKind || it.Kind == GolfRouting.TeeKind || it.Kind == GolfRouting.GreenKind) continue;
                float d = hw + it.R + 0.2f;
                if (math.distancesq(p, it.P) < d * d) return true;
            }
            return false;
        }

        internal static bool Clear(LayerRegion region, Occupancy occ, float2 a, float2 b, float hw, float endSlack = 0f)
        {
            if (region.CrossesBoundary(a, b, true)) return false;
            float len = math.distance(a, b);
            int n = math.max(1, (int)(len / 2f));
            for (int i = 0; i <= n; i++)
            {
                var p = math.lerp(a, b, i / (float)n);
                if (i > 0 && i < n && !region.Contains(p)) return false;
                if (DiscBlocked(occ, p, hw, math.min(math.distance(p, a), math.distance(p, b)) < 5f) || occ.HitsPolygon(p, hw)) return false;
                bool nearEnd = i == 0 || i == n || math.distance(p, a) < endSlack || math.distance(p, b) < endSlack;
                if (!nearEnd && occ.Corridors.Overlaps(p, hw, 0.2f)) return false;
            }
            return true;
        }

        private int Bunkers(LayerContext ctx, LayerRegion region, Occupancy haz, Occupancy occ, List<GolfHole> holes, RoutingSpec spec, Format fmt, ref bool warned)
        {
            int perHole = ctx.I("bunkers");
            if (perHole <= 0 || holes.Count == 0) return 0;
            var sand = GolfAssets.BunkerRef(ctx.S("bunkerSurface"));
            string sandCat = AssetCategories.SurfaceSand;
            float depth = ctx.F("bunkerDepth");
            int placed = 0;
            foreach (var h in holes)
            {
                uint hv = Hashing.Mix(ctx.Seed, 0xB0B0u + (uint)h.Number * 17u);
                var back = -h.ApproachDir;
                int greenside = fmt.Pitch ? perHole : (perHole + 1) / 2;
                int fairway = perHole - greenside;
                var spots = new List<(float2 c, float r)>();
                for (int j = 0; j < greenside; j++)
                {
                    float r = fmt.BunkerR * (0.75f + 0.5f * Hashing.Unit(Hashing.Mix(hv, (uint)j)));
                    float deg = (j % 2 == 0 ? 1 : -1) * (50f + 45f * (j / 2));
                    spots.Add((h.Green + Rot(back, math.radians(deg)) * (spec.GreenRadius + r + 2.5f), r));
                }
                if (!fmt.Pitch && h.Length > 120f)
                    for (int j = 0; j < fairway; j++)
                    {
                        float r = fmt.BunkerR * (0.9f + 0.6f * Hashing.Unit(Hashing.Mix(hv, (uint)j + 50u)));
                        // Landing zone of the drive (≈ 60–75 % of the hole), on the outside of a dogleg first.
                        float f = math.clamp(0.62f + 0.1f * j, 0.4f, 0.8f);
                        var p = h.At(f, out var t);
                        float s = (j % 2 == 0 ? 1f : -1f) * (h.Dogleg ? math.sign(Poly.Cross(h.Bend - h.Tee, h.Green - h.Bend)) * -1f : 1f);
                        spots.Add((p + new float2(-t.y, t.x) * s * (spec.HalfWidth * 0.75f + r * 0.6f), r));
                    }
                for (int j = 0; j < spots.Count; j++)
                {
                    var (c, r) = spots[j];
                    if (!region.ContainsDisc(c, r + 3f) || !haz.Free(c, r + 0.5f, r + 3f, 0.4f)) continue;
                    if (OnOtherHole(c, r, h, holes, spec)) continue;
                    var ring = LeisureKit.Blob(c, r, Hashing.Mix(hv, (uint)j + 100u), 12, 0.28f);
                    if (!ctx.Surface(ring, SurfaceRole.Decorative, sand, sandCat)) continue;
                    Pad(ctx, region, ring, c, -depth, ref warned);
                    haz.Take(c, r + 0.5f, 60); occ.Take(c, r + 0.5f, 60);
                    placed++;
                }
            }
            return placed;
        }

        private static bool OnOtherHole(float2 c, float r, GolfHole own, List<GolfHole> holes, RoutingSpec spec)
        {
            foreach (var o in holes)
            {
                if (o == own) continue;
                for (int i = 0; i + 1 < o.Line.Length; i++)
                    if (SegmentIndex.DistSq(c, o.Line[i], o.Line[i + 1]) < math.square(spec.HalfWidth * 0.8f + r)) return true;
                if (math.distance(c, o.Green) < spec.GreenRadius + r + 2f) return true;
            }
            return false;
        }

        private static int Ponds(LayerContext ctx, LayerRegion region, Occupancy haz, Occupancy occ, List<GolfHole> holes, RoutingSpec spec, Format fmt)
        {
            int wanted = ctx.I("water");
            if (wanted <= 0 || holes.Count == 0) return 0;
            float depth = ctx.F("waterDepth");
            int placed = 0; float area = 0f;
            for (int j = 0; j < wanted * 3 && placed < wanted; j++)
            {
                var h = holes[(j * 3 + 1) % holes.Count];
                uint v = Hashing.Mix(ctx.Seed, 0x9090u + (uint)j);
                float rp = math.clamp(h.Length * (fmt.Pitch ? 0.12f : 0.1f), fmt.Pitch ? 5f : 8f, fmt.Pitch ? 12f : 22f);
                float f = (j % 3) == 2 ? 0.92f : 0.45f + 0.12f * (j % 3);
                var p = h.At(f, out var t);
                var n = new float2(-t.y, t.x) * ((j & 1) == 0 ? 1f : -1f);
                float off = (f > 0.9f ? math.max(spec.GreenRadius, spec.HalfWidth) : spec.HalfWidth) + rp + 3f;
                var c = p + n * off;
                // Deep enough inside that the lowered basin stays blended with the original ground at the outline.
                float edgeNeed = rp + (0.5f + depth) / 0.25f + 1f;
                if (!region.ContainsDisc(c, edgeNeed) || !haz.Free(c, rp + 2f, edgeNeed, 0.5f)) continue;
                bool clash = false;
                foreach (var o in holes)
                {
                    for (int i = 0; i + 1 < o.Line.Length && !clash; i++)
                        if (SegmentIndex.DistSq(c, o.Line[i], o.Line[i + 1]) < math.square(spec.HalfWidth * 0.85f + rp)) clash = true;
                    if (math.distance(c, o.Green) < spec.GreenRadius + rp + 3f) clash = true;
                }
                if (clash) continue;
                var ring = LeisureKit.Blob(c, rp, v, 16, 0.25f);
                float shore = float.MaxValue;
                foreach (var q in ring) shore = math.min(shore, ctx.H(q));
                float level = shore - 0.5f;
                ctx.Plan.Terraform.Add(new PlannedTerraform
                {
                    Ring = Poly.To3D(ring, ctx.H), Kind = TerraformKind.Lower, TargetHeight = level - depth, Permanent = true,
                    WaterLevel = level, WaterAnchor = new float3(c.x, level, c.y),
                });
                ctx.Surface(ring, SurfaceRole.Water, new AssetRef("", Array.Empty<string>()));
                haz.Take(c, rp + 2f, 70); occ.Take(c, rp + 2f, 70);
                area += Poly.Area(ring);
                placed++;
            }
            if (placed > 0)
            {
                ctx.Issue(IssueSeverity.Warning, "Placemaker.Park.Issue.LakePermanent");
                ctx.Stat(Keys.StatLakeArea, Math.Round(area), Keys.UnitM2);
            }
            if (placed < wanted) ctx.Issue(IssueSeverity.Info, "Placemaker.Golf.Issue.FewerPonds", placed, wanted);
            return placed;
        }

        /// <summary>A free circle near the clubhouse handed to the practice-green feature ("practice" region).</summary>
        private static void PracticeRegion(LayerContext ctx, Occupancy occ, float2 home, float r)
        {
            var region = ctx.LayerRegion;
            for (float dist = r + 10f; dist < r + 60f; dist += 5f)
                for (int k = 0; k < 12; k++)
                {
                    float a = k * math.PI / 6f;
                    var c = home + new float2(math.cos(a), math.sin(a)) * dist;
                    if (!occ.Free(c, r + 1f, r + 1.5f, 0.5f)) continue;
                    occ.Take(c, r + 1f, 80);
                    ctx.AddRegion(LayerRegion.Circle(c, r, 20), PracticeTag, PracticeTag, RegionTags.Green);
                    return;
                }
            if (Spots.Find(occ, r * 0.7f, Spots.Preference.Open, Hashing.Mix(ctx.Seed, 0x9A0u), out var s))
            {
                occ.Take(s, r * 0.7f + 1f, 80);
                ctx.AddRegion(LayerRegion.Circle(s, r * 0.7f, 20), PracticeTag, PracticeTag, RegionTags.Green);
            }
            else ctx.AddRegion(region.Ring, PracticeTag, PracticeTag);
        }

        private static void Planting(LayerContext ctx, Occupancy occ, LayerRegion region, Func<float2, bool> reject)
        {
            float ha = region.Area / 10000f;
            var treeIds = ctx.L("treeSpecies");
            if (treeIds.Length == 0) treeIds = GolfAssets.Trees(ctx.S("subtype"));
            int trees = math.min(8000, (int)(ha * ctx.F("treeDensity")));
            if (trees > 0 && AssetAvailability.Require(ctx.Plan, AssetCategories.Tree, treeIds, Keys.Category(AssetCategories.Tree)))
                LeisureKit.Plant(ctx, occ, region, trees, ObjectRole.Tree, treeIds, 2.5f, 22f, 0.4f, 0x7E00u, reject, 1, 3);
            int bushes = math.min(8000, (int)(ha * ctx.F("bushDensity")));
            var bushIds = GolfAssets.Bushes(ctx.S("subtype"));
            if (bushes > 0 && AssetAvailability.Require(ctx.Plan, AssetCategories.Bush, bushIds, Keys.Category(AssetCategories.Bush)))
                LeisureKit.Plant(ctx, occ, region, bushes, ObjectRole.Bush, bushIds, 1.1f, 12f, 0.3f, 0xB500u, reject);
        }
    }
}
