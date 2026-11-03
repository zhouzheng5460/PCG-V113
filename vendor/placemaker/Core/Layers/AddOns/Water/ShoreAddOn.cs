// Shore add-on (grand park wave, g2): the "Shore" feature of park and plaza. It runs in the
// "place:shore" regions a host carves when its area is partly in water (ShoreKit.CarveShore): the
// land band along the waterline plus the water inside the outline. "If I draw an area half in
// water, half out, make a pier, build the terrain into a wharf."
//
//   auto       the middle of the shore becomes a wharf (filled quay, bollards, moored boats, cargo)
//              with a pier off its edge; the rest is a seawall promenade
//   wharf      quay along the whole shore (harbour square)
//   promenade  seawall promenade with railings, benches, lamps and piers
//   pier       long piers with T-heads from a light waterside walk
//   boardwalk  a deck just off the shore over the water
//   beach      sand edge with umbrellas, loungers and boats pulled up
//   reeds      natural reed edge with a fishing jetty
// It can also run on its own on any region touching water (it finds the water itself); away from
// water it reports one Info issue and places nothing.

using System;
using Placemaker.Core.Layers.Common;
using Placemaker.Core.Layers.Common.Shore;
using Placemaker.Core.Layers.Types.Beach;
using Unity.Mathematics;

namespace Placemaker.Core.Layers.AddOns.Water
{
    [AreaGenerator(104)]
    public sealed class ShoreAddOn : AddOnLayer
    {
        public static readonly string[] Styles = { "auto", "wharf", "promenade", "pier", "boardwalk", "beach", "reeds" };
        public const string NoWaterKey = "Placemaker.AddOn.Shore.Issue.NoWater";
        public const string NeedsWaterInsideKey = "Placemaker.AddOn.Shore.Issue.WharfNeedsWater";

        protected override string Area => "AddOn.Shore";
        public override string Id => ShoreKit.LayerId;
        public override string Icon => "Media/Game/Icons/LeisurePier01Medium.svg";
        public override string[] Accepts => new[] { ShoreKit.Place };
        public override float MinArea => 60f;

        protected override void DefineControls(ControlSet c)
        {
            c.Choice("style", "auto", ControlTier.Essential, "shore", Styles).Tip();
            c.Slider("bandWidth", 10, 4, 24, 1, "m", ControlTier.Essential, "shore").Tip();
            c.Toggle("connect", true, ControlTier.Essential, "shore").Tip();

            c.Slider("piers", 1, 0, 4, 1, "count", ControlTier.More, "piers").Tip();
            c.Slider("pierLength", 30, 8, 120, 2, "m", ControlTier.More, "piers");
            c.Toggle("pierHeads", true, ControlTier.More, "piers").Tip();

            c.Slider("wharfDepth", 12, 4, 30, 1, "m", ControlTier.More, "harbor").Tip();
            c.Slider("wharfShare", 45, 10, 100, 5, "%", ControlTier.More, "harbor").Tip();
            c.Slider("bollardSpacing", 8, 4, 20, 1, "m", ControlTier.More, "harbor").Layout(3);
            c.Slider("boats", 4, 0, 12, 1, "count", ControlTier.More, "harbor").Tip();
            c.Toggle("cargo", true, ControlTier.More, "harbor").Tip();

            c.Toggle("railings", true, ControlTier.More, "edge");
            c.Slider("benchSpacing", 14, 0, 40, 1, "m", ControlTier.More, "edge").Layout(4);
            c.Slider("lampSpacing", 18, 0, 40, 1, "m", ControlTier.More, "edge").Layout(4);
            c.Slider("reedDensity", 0, 0, 100, 10, "%", ControlTier.More, "edge").Tip();

            c.Asset("pathAsset", AssetCategories.Pathway, ControlTier.More, "assets");
            c.Asset("surfaceAsset", AssetCategories.SurfacePaving, ControlTier.More, "assets");
            c.Asset("railingAsset", AssetCategories.Fence, ControlTier.More, "assets").When("railings=true");
            c.Asset("benchAsset", AssetCategories.Bench, ControlTier.More, "assets");
            c.Asset("lampAsset", AssetCategories.Lamp, ControlTier.More, "assets");
            c.Asset("boatAsset", AssetCategories.ShoreBoat, ControlTier.More, "assets");

            c.RandomRange("piers", 0, 2);
            c.RandomRange("boats", 0, 6);
            c.Chance("railings", 0.8f);
            c.Chance("cargo", 0.7f);
            c.Fixed("connect");
        }

        protected override void DefinePresets()
        {
            Preset("harbour", ("style", "wharf"), ("wharfShare", 100.0), ("wharfDepth", 14.0), ("boats", 6.0), ("piers", 1.0), ("railings", false));
            Preset("waterside", ("style", "auto"));
            Preset("promenade", ("style", "promenade"), ("piers", 1.0), ("bandWidth", 12.0), ("benchSpacing", 12.0));
            Preset("pleasurePier", ("style", "pier"), ("piers", 2.0), ("pierLength", 60.0), ("pierHeads", true), ("boats", 2.0), ("bandWidth", 8.0));
            Preset("boardwalk", ("style", "boardwalk"), ("piers", 0.0), ("reedDensity", 40.0), ("lampSpacing", 24.0));
            Preset("beach", ("style", "beach"), ("piers", 0.0), ("bandWidth", 16.0), ("boats", 3.0));
            Preset("reeds", ("style", "reeds"), ("piers", 1.0), ("pierLength", 14.0), ("pierHeads", false), ("reedDensity", 80.0), ("lampSpacing", 0.0), ("boats", 1.0));
        }

        protected override void PlanCore(LayerContext ctx)
        {
            int issuesBefore = ctx.Plan.Issues.Count;
            var survey = ShoreKit.Survey(ctx.Site, ctx.LayerRegion, ctx.H);
            Shoreline shore = survey.Any ? Shoreline.Analyze(ctx, ctx.LayerRegion, "auto") : null;
            // The shoreline's own "no water, assumed a side" note is replaced by ours.
            ctx.Plan.Issues.RemoveAll(i => i.Key == Shoreline.NoWaterKey);
            if (shore == null || shore.Mode == ShoreMode.Assumed || shore.Length < 6f)
            {
                ctx.Issue(IssueSeverity.Info, NoWaterKey);
                return;
            }
            var w = new ShoreWork(ctx, shore)
            {
                PathAsset = AssetMix.One(ctx.S("pathAsset")), Bench = AssetMix.One(ctx.S("benchAsset")), Lamp = AssetMix.One(ctx.S("lampAsset")),
                Paving = AssetMix.One(ctx.S("surfaceAsset")), Railing = AssetMix.One(ctx.S("railingAsset")), Boat = AssetMix.One(ctx.S("boatAsset")),
            };
            string style = ctx.S("style");
            float band = math.min(ctx.F("bandWidth"), math.max(3f, shore.MaxDepth - 1f));
            float L = shore.Length;
            float end = math.min(4f, L * 0.1f);
            int piers = ctx.I("piers");
            float pierLength = ctx.F("pierLength");
            bool heads = ctx.B("pierHeads");
            int boats = ctx.I("boats");
            float benches = ctx.F("benchSpacing"), lamps = ctx.F("lampSpacing");
            w.WalkRole = band >= 10f && style != "pier" && style != "reeds" ? NetRole.PathwayWide : NetRole.Pathway;
            float walkDepth = math.clamp(band * 0.62f, w.Hw + 1.2f, math.max(w.Hw + 1.2f, band - 0.4f));

            switch (style)
            {
                case "wharf":
                case "auto":
                {
                    float share = style == "wharf" ? ctx.F("wharfShare") / 100f : math.min(ctx.F("wharfShare") / 100f, 0.6f);
                    float wl = math.clamp(L * share, math.min(24f, L - 2f * end), L - 2f * end);
                    if (style == "auto") wl = math.min(wl, 90f);
                    float a = (L - wl) * 0.5f, b = a + wl;
                    ShoreTreatments.Walk(w, walkDepth, end, L - end);
                    bool filled = ShoreTreatments.Wharf(w, a, b, ctx.F("wharfDepth"), ctx.F("bollardSpacing"), boats, ctx.B("cargo"), lamps, piers > 0 ? (a + b) * 0.5f : float.NaN);
                    if (!filled && style == "wharf") ctx.Issue(IssueSeverity.Info, NeedsWaterInsideKey);
                    // A pier off the middle of the quay, more along the promenade.
                    if (piers > 0) ShoreTreatments.Pier(w, (a + b) * 0.5f, pierLength, heads, filled ? 0 : 2, lamps > 0f);
                    for (int i = 1; i < piers; i++)
                        ShoreTreatments.Pier(w, i % 2 == 1 ? a * 0.5f : b + (L - b) * 0.5f, pierLength, heads, 1, lamps > 0f);
                    if (!filled)
                    {
                        // Water beside the area (not inside): a quay edge on land — bollards instead of a railing.
                        QuayEdge(w, end, L - end);
                        ShoreTreatments.Promenade(w, end, L - end, band, true, false, benches, lamps);
                    }
                    else
                    {
                        ShoreTreatments.Promenade(w, end, a, band, true, ctx.B("railings"), benches, lamps);
                        ShoreTreatments.Promenade(w, b, L - end, band, true, ctx.B("railings"), benches, lamps);
                        // The apron behind the quay is paved too (the walk runs along it).
                        ShoreTreatments.Promenade(w, a, b, band, true, false, 0f, 0f);
                    }
                    break;
                }
                case "promenade":
                    ShoreTreatments.Walk(w, walkDepth, end, L - end);
                    PlacePiers(w, piers, pierLength, heads, math.min(boats, 2), lamps > 0f, end);
                    ShoreTreatments.Promenade(w, end, L - end, band, true, ctx.B("railings"), benches, lamps);
                    break;
                case "pier":
                    ShoreTreatments.Walk(w, math.min(walkDepth, w.Hw + 2.5f), end, L - end);
                    if (PlacePiers(w, math.max(1, piers), pierLength, heads, math.max(1, boats / math.max(1, piers)), true, end) == 0)
                        ctx.Issue(IssueSeverity.Info, "Placemaker.AddOn.Shore.Issue.PiersNeedWater");
                    ShoreTreatments.Promenade(w, end, L - end, band, false, ctx.B("railings"), benches, lamps);
                    break;
                case "boardwalk":
                    ShoreTreatments.Boardwalk(w, end, L - end, 3f, benches, lamps);
                    PlacePiers(w, piers, pierLength, heads, 0, lamps > 0f, end);
                    break;
                case "beach":
                {
                    float sand = math.max(4f, band - (w.Hw * 2f + 1f));
                    ShoreTreatments.Walk(w, sand + w.Hw + 0.6f, end, L - end);
                    ShoreTreatments.Beach(w, end, L - end, sand, 9f, boats);
                    PlacePiers(w, piers, pierLength, heads, 0, false, end);
                    if (benches > 0f) ShoreTreatments.Promenade(w, end, L - end, band, false, false, benches * 1.5f, lamps);
                    break;
                }
                case "reeds":
                    ShoreTreatments.Walk(w, math.max(walkDepth, 5f), end, L - end);
                    PlacePiers(w, piers, pierLength, heads, boats, false, end);
                    ShoreTreatments.Promenade(w, end, L - end, band, false, false, benches * 1.5f, lamps);
                    break;
            }
            ctx.Check();
            float reeds = ctx.F("reedDensity") / 100f;
            if (style == "reeds" || style == "boardwalk") reeds = math.max(reeds, 0.3f);
            if (reeds > 0f) ShoreTreatments.Reeds(w, end, L - end, reeds);
            if (ctx.B("connect")) ShoreTreatments.Connect(w);

            ctx.Stat("Placemaker.Stat.Shoreline", Math.Round(L), Keys.UnitM);
            if (w.Piers > 0) ctx.Stat("Placemaker.Stat.Piers", w.Piers, Keys.UnitCount);
            if (w.FillArea > 0f) ctx.Stat("Placemaker.Stat.QuayArea", Math.Round(w.FillArea), Keys.UnitM2);
            if (w.Boats > 0) ctx.Stat("Placemaker.Stat.Boats", w.Boats, Keys.UnitCount);
            ctx.Stat(Keys.StatBenches, w.Benches, Keys.UnitCount);
            ctx.Stat(Keys.StatLamps, w.Lamps, Keys.UnitCount);
            ctx.Plan.Facts["shoreMode"] = (int)shore.Mode;
            ctx.Plan.Facts["piers"] = w.Piers;
            ctx.Plan.Facts["pierLength"] = w.PierLength;
            ctx.Plan.Facts["wharves"] = w.Wharves;
            ctx.Plan.Facts["quayArea"] = w.FillArea;
            ctx.Plan.Facts["bollards"] = w.Bollards;
            ctx.Plan.Facts["boats"] = w.Boats;
            ctx.Plan.Facts["reeds"] = w.Reeds;
            ctx.Plan.Facts["railings"] = w.Railings;
            ctx.Plan.Facts["walkLength"] = w.WalkLength;
            ctx.Plan.Facts["links"] = w.Links;
            ctx.Plan.Facts["props"] = w.Props;
            ctx.Plan.Facts["umbrellas"] = w.Umbrellas;
        }

        /// <summary>Evenly spread piers; returns how many were built.</summary>
        private static int PlacePiers(ShoreWork w, int count, float length, bool heads, int boats, bool lamps, float end)
        {
            if (count <= 0) return 0;
            int built = 0;
            foreach (float s in w.Shore.Stations(count, math.max(end, 8f)))
                if (ShoreTreatments.Pier(w, s, length, heads, boats, lamps)) built++;
            return built;
        }

        /// <summary>Bollards along a land edge (water outside the area: no fill possible).</summary>
        private static void QuayEdge(ShoreWork w, float from, float to)
        {
            var ctx = w.Ctx;
            string cat = LeisureKit.Category(ctx, "", AssetCategories.WaterfrontMooring, AssetCategories.Bollard);
            if (cat == null) return;
            float spacing = math.max(4f, ctx.F("bollardSpacing"));
            for (float s = from + 2f; s < to - 2f; s += spacing)
            {
                if (w.InGap(s, 1.5f)) continue;
                var c = w.Shore.At(s, out var inw, out _);
                var p = c + inw * 0.9f;
                if (!w.OnLand(p, 0.5f)) continue;
                if (LeisureKit.Put(ctx, w.Occ, p, ShoreKit.Yaw(-inw), ObjectRole.Furniture, AssetMix.One(""), cat, 0.5f, Hashing.Mix(ctx.Seed, 0xB012u + (uint)w.Bollards), 0.2f, 0.1f))
                    w.Bollards++;
            }
        }
    }
}
