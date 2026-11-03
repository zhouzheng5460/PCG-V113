// Fountain add-on: a featured centerpiece with a paved apron and a ring of benches/lamps,
// placed at the most central free spot of its region. "Bigger fountain" = the size control:
// a larger footprint reserve, apron and seating ring (and, for explicit assets, the asset's own
// bounds when larger).

using System;
using Placemaker.Core.Layers.Common;
using Unity.Mathematics;

namespace Placemaker.Core.Layers.AddOns
{
    [AreaGenerator(100)]
    public sealed class FountainAddOn : AddOnLayer
    {
        protected override string Area => "AddOn.Fountain";
        public override string Id => "fountain";
        public override string Icon => "Media/Game/Icons/Water.svg";

        private static readonly string[] Sizes = { "small", "medium", "large", "grand" };
        private static float BasinRadius(string size) => size == "small" ? 3f : size == "large" ? 8f : size == "grand" ? 12f : 5f;

        protected override void DefineControls(ControlSet c)
        {
            c.Choice("size", "medium", ControlTier.Essential, "fountain", Sizes);
            c.Asset("asset", AssetCategories.Centerpiece, ControlTier.Essential, "fountain");
            c.Toggle("apron", true, ControlTier.More, "ground").Tip();
            c.Asset("apronSurface", AssetCategories.SurfacePaving, ControlTier.More, "ground").When("apron=true");
            c.Slider("benches", 6, 0, 16, 1, "count", ControlTier.More, "seating");
            c.Asset("benchAsset", AssetCategories.Bench, ControlTier.More, "seating");
            c.Toggle("lamps", true, ControlTier.More, "seating");
            c.Asset("lampAsset", AssetCategories.Lamp, ControlTier.More, "seating").When("lamps=true");
            c.Toggle("planters", false, ControlTier.More, "seating");
            c.Asset("planterAsset", AssetCategories.Planter, ControlTier.More, "seating").When("planters=true");
            c.Chance("apron", 0.85f);
            c.Chance("lamps", 0.8f);
            c.Chance("planters", 0.35f);
            c.RandomRange("benches", 2, 10);
        }

        protected override void DefinePresets()
        {
            Preset("simple", ("size", "small"), ("benches", 4.0), ("lamps", false));
            Preset("featured", ("size", "large"), ("benches", 8.0), ("planters", true));
            Preset("grand", ("size", "grand"), ("benches", 12.0), ("planters", true));
        }

        protected override void PlanCore(LayerContext ctx)
        {
            var asset = AssetMix.One(ctx.S("asset"));
            if (!AssetAvailability.Require(ctx.Plan, AssetCategories.Centerpiece, new[] { ctx.S("asset") }, Keys.Category(AssetCategories.Centerpiece)))
                return;
            var occ = Occupancy(ctx);
            int sizeIndex = Math.Max(0, Array.IndexOf(Sizes, ctx.S("size")));
            int benches = ctx.I("benches");
            bool lamps = ctx.B("lamps"), planters = ctx.B("planters");
            // s18: the place's focal point first (LeisureKit.FocalPoint — where a plaza lays its rondel), at the
            // requested size, then smaller: a smaller fountain on the focal point beats a big one beside it
            // (playtest: a fountain tower half over the edge of the square's circle). Then anywhere central.
            var focal = LeisureKit.FocalPoint(ctx.LayerRegion, ctx.Input.Reserved, out _);
            // Pass 0: focal point with its seating; 1: focal point, fountain alone (a small square's own benches
            // and lamps stand round it); 2: the most central free spot with seating.
            for (int pass = 0; pass < 3; pass++)
                // Try the requested size, then smaller ones: a smaller fountain beats none.
                for (int si = sizeIndex; si >= 0; si--)
                {
                    // Automatic: a vanilla fountain of the requested size (the centrepiece pool also holds clock hands).
                    var kit = si == 0 ? AddOnAssets.SmallFountains : si == 1 ? MediumFountains : AddOnAssets.LargeFountains;
                    if (string.IsNullOrEmpty(ctx.S("asset"))) asset = AssetMix.One("", kit);
                    float basin = math.max(BasinRadius(Sizes[si]), string.IsNullOrEmpty(ctx.S("asset")) ? AddOnAssets.Radius(ctx.Site, "", kit, 0f) * 0.75f : AssetMix.FootprintRadius(ctx.Site, asset, 0f));
                    bool alone = pass == 1;
                    bool seating = !alone && (benches > 0 || lamps || planters);
                    float apron = basin + (seating ? 4.5f : 2f);
                    float2 spot = focal;
                    if (pass < 2)
                    {
                        if (!ctx.LayerRegion.ContainsDisc(focal, apron + 0.5f) || occ.Clearance(focal, apron * 2f + 5f) < apron) continue;
                    }
                    else if (!Spots.Find(occ, apron, Spots.Preference.Centre, ctx.Seed, out spot)) continue;
                    if (si != sizeIndex) ctx.Issue(IssueSeverity.Info, "Placemaker.AddOn.Fountain.Issue.Smaller");
                    Build(ctx, occ, spot, basin, apron, asset, alone ? 0 : benches, !alone && lamps, !alone && planters);
                    return;
                }
            NoSpace(ctx, "Placemaker.AddOn.Fountain.Name");
        }

        /// <summary>
        /// (s18) What <see cref="PlanCore"/> will build on a focal point with <paramref name="room"/> metres of free
        /// radius, for an automatic asset: the apron radius (0 = no fountain fits there), the basin radius, and whether
        /// it stands alone (no seating ring). Lets a small plaza lay its focal pad to the fountain's real size.
        /// </summary>
        public static float PredictApron(SiteContext site, float room, string size, bool seating, out float basin, out bool alone)
        {
            int sizeIndex = Math.Max(0, Array.IndexOf(Sizes, size));
            for (int pass = 0; pass < 2; pass++)
                for (int si = sizeIndex; si >= 0; si--)
                {
                    var kit = si == 0 ? AddOnAssets.SmallFountains : si == 1 ? MediumFountains : AddOnAssets.LargeFountains;
                    basin = math.max(BasinRadius(Sizes[si]), AddOnAssets.Radius(site, "", kit, 0f) * 0.75f);
                    alone = pass == 1;
                    float apron = basin + (seating && !alone ? 4.5f : 2f);
                    if (apron + 0.5f <= room) return apron;
                }
            basin = 0f; alone = true;
            return 0f;
        }

        private static void Build(LayerContext ctx, Occupancy occ, float2 c, float basin, float apron, AssetRef asset,
            int benches, bool lamps, bool planters)
        {
            ctx.Object(c, Hashing.Unit(ctx.Seed) * 2f * math.PI, ObjectRole.Centerpiece, asset, Hashing.Mix(ctx.Seed, 0xF0u));
            occ.Take(c, basin, 0);
            if (ctx.B("apron"))
            {
                var paving = AddOnAssets.Or(ctx.S("apronSurface"), AddOnAssets.Paving);
                ctx.Surface(LayerRegion.Circle(c, apron, 32), SurfaceRole.Paving, paving);
                AddOnKit.LinkToPath(ctx, occ, c, apron, 1.5f, paving, null, 14f);
            }

            float seatRing = apron - 1.6f;
            float phase = Hashing.Unit(Hashing.Mix(ctx.Seed, 0x5Eu)) * 2f * math.PI;
            var bench = AddOnAssets.Or(ctx.S("benchAsset"), AddOnAssets.Bench);
            int placedBenches = 0;
            if (benches > 0)
                foreach (var p in RingPoints(c, seatRing, benches, phase))
                {
                    if (!occ.Free(p, 1.3f, 0.3f, 0.2f)) continue;
                    // Benches face the water.
                    ctx.Object(p, Facing(p, c), ObjectRole.Bench, bench, Hashing.Mix(ctx.Seed, (uint)placedBenches));
                    occ.Take(p, 1.3f, 1);
                    placedBenches++;
                }
            int between = math.max(benches, 4);
            float midPhase = phase + math.PI / between;
            if (lamps)
            {
                var lamp = AssetMix.One(ctx.S("lampAsset"));
                int i = 0;
                foreach (var p in RingPoints(c, seatRing + 0.6f, math.min(between, 8), midPhase))
                {
                    if (!occ.Free(p, 0.5f, 0.3f, 0.2f)) continue;
                    ctx.Object(p, 0f, ObjectRole.Lamp, lamp, Hashing.Mix(ctx.Seed, 0x1A00u + (uint)i++));
                    occ.Take(p, 0.5f, 2);
                }
            }
            if (planters)
            {
                var planter = AssetMix.One(ctx.S("planterAsset"));
                int i = 0;
                foreach (var p in RingPoints(c, basin + 1.6f, math.max(4, between), midPhase))
                {
                    if (!occ.Free(p, 0.8f, 0.3f, 0.2f)) continue;
                    ctx.Object(p, 0f, ObjectRole.Planter, planter, Hashing.Mix(ctx.Seed, 0x9100u + (uint)i++));
                    occ.Take(p, 0.8f, 3);
                }
            }
            ctx.Stat(Keys.StatFountains, 1, Keys.UnitCount);
            ctx.Stat(Keys.StatBenches, placedBenches, Keys.UnitCount);
            ctx.Plan.Facts["fountainRadius"] = basin;
        }

        private static readonly string[] MediumFountains = { "LargeFountain01", "SmallFountain01" };
    }
}
