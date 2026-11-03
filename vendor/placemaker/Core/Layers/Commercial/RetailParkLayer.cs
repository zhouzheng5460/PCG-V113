// s32 — Retail park: a few big boxes round one big shared car park. The lot holds the main street frontage behind a
// planted verge; a service road (Small Road where it exists, else the shop lane) wraps it as a horseshoe and the big
// plots (deep, wide, high-density commercial by default) stand outside the road facing the lot — the classic out-of-town
// retail park. Walkways cross from the lot to the store fronts; pad sites (small plots for a café or a drive-through)
// take the main street frontage beside the horseshoe; the rest is planted.
// Styles: horseshoe (boxes on three sides) · twin (two terraces facing each other across the lot; the road runs through
// to a street at the back when there is one).
// Presets: classic · megastore · outlet · trade.

using System.Collections.Generic;
using Placemaker.Core.Layers.Common;
using Unity.Mathematics;

namespace Placemaker.Core.Layers.Commercial
{
    [AreaGenerator(22, Group = Group)]
    public sealed class RetailParkLayer : CommercialLayer
    {
        protected override string Area => "RetailPark";
        public override string Id => "retailPark";
        public override string Icon => "Media/Game/Icons/ZoneCommercial.svg";
        public override float MinArea => 9000f;
        protected override float GradeTolerance => 0.04f;
        protected override bool RowsOnFreeFrontage => false;
        /// <summary>(shops) The big shared car park is the type.</summary>
        protected override float LotShare => 0.6f;

        public static readonly string[] Styles = { "horseshoe", "twin" };
        public static readonly string[] Units = { "small", "medium", "large" };
        /// <summary>The planted strip between the main street and the lot (m): one row of trees.</summary>
        public const float Verge = 8f;

        protected override void DefineControls(ControlSet c)
        {
            c.Choice("style", "horseshoe", ControlTier.Essential, "layout", Styles).Tip();
            c.Choice("unit", "large", ControlTier.Essential, "shops", Units).Tip();
            c.Choice("road", "street", ControlTier.More, "layout", "street", "lane").Tip();
            c.Toggle("pads", true, ControlTier.More, "shops").Tip();
            CommonControls(c, ZoneKinds.CommercialHigh, 40, "large", maxDepth: 48);
            c.Fixed("style");
        }

        protected override void DefinePresets()
        {
            Preset("classic", ("style", "horseshoe"), ("unit", "large"), ("plotDepth", 40.0));
            Preset("megastore", ("style", "horseshoe"), ("unit", "large"), ("plotDepth", 48.0), ("pads", false), ("evShare", 12.0));
            Preset("outlet", ("style", "horseshoe"), ("unit", "small"), ("plotDepth", 24.0), ("zoneKind", ZoneKinds.CommercialLow), ("lotSize", "medium"), ("road", "lane"));
            Preset("trade", ("style", "twin"), ("unit", "medium"), ("plotDepth", 48.0), ("zoneKind", ZoneKinds.Industrial), ("lotGreen", "edges"), ("pads", false));
        }

        protected override void Compose(LayerContext ctx, Mall mall, List<StreetShops.Run> runs)
        {
            var r = runs[0];
            var (asset, h) = ctx.S("road") == "lane" ? Mall.ShopLane(ctx) : Mall.ServiceRoad(ctx);
            // Plots start where the road's zone cells do (a 12 m road's first cell begins 8 m from its centre line).
            float zo = Build.ZoneBlockPredictor.CellWidth(2f * h) * 4f;
            int cells = Cells(ctx);
            // Lot size = the most aisles it may have; it takes the depth the back terrace leaves.
            int maxAisles = Mall.Aisles(ctx.S("lotSize")) + 2;
            int unit = ctx.S("unit") == "small" ? 2 : ctx.S("unit") == "medium" ? 3 : 5;
            bool twin = ctx.S("style") == "twin";
            float from = r.From + (r.StartCorner ? Mall.CornerClear : 2f), to = r.To - (r.EndCorner ? Mall.CornerClear : 2f);
            float c = 0.5f * (from + to);
            float reach = mall.Reach(r, from, to);
            // The lot: as wide as leaves room for the side terraces (≤ 150 m: wider sites give the rest to pads), as deep as
            // leaves room for the back terrace.
            float side = zo + cells * Mall.Cell;
            float w = math.min(to - from - 2f * (h + side), 150f);
            int wanted = cells;
            while (w < 56f && cells > 2) { cells--; side = zo + cells * Mall.Cell; w = math.min(to - from - 2f * (h + side), 150f); }
            // (shops) A block too narrow for boxes on the legs (the default 150 m city block): the road hugs the sides and the boxes
            // stand along its back run only, as deep as asked (a big-box terrace facing the lot), instead of a street row.
            if (w < 56f && !twin) { cells = wanted; side = 0f; w = math.min(to - from - 2f * h, 150f); }
            float backNeed = twin ? 12f : h + zo + cells * Mall.Cell;
            int aisles = math.clamp((int)math.floor((reach - 1f - Verge - h - backNeed) / Mall.LotDepth(1)), 1, maxAisles);
            while (!twin && cells > 2 && Verge + Mall.LotDepth(aisles) + 2f * h + zo + cells * Mall.Cell > reach - 1f) cells--;
            float lt = Mall.LotDepth(aisles);
            if (w < 40f || Verge + lt + h > reach - 1f)
            {
                if (mall.Extra) return;   // (shops) another frontage: nothing left behind
                mall.Row(r, from, to, 0f, math.min(cells, 4), 1f, null, unit);
                ctx.Issue(IssueSeverity.Info, FallbackKey);
                return;
            }
            float l = c - w * 0.5f - h, rr = c + w * 0.5f + h;   // road centre lines
            float back = Verge + lt + h;
            Mall.Lane road = null;
            var legs = new List<Mall.Lane>();
            if (twin)
            {
                float rl = mall.ReachAt(r, l), rrr = mall.ReachAt(r, rr);
                if (StreetShops.StreetEnd(ctx.Input, r.At(l, rl), r.In, out _, out _, out _) && StreetShops.StreetEnd(ctx.Input, r.At(rr, rrr), r.In, out _, out _, out _))
                {
                    var a = mall.Connect(Mall.End.Street(r.At(l, 0f), -r.In), null, Mall.End.Street(r.At(l, rl), r.In), NetRole.Alley, asset, h);
                    var b = a == null ? null : mall.Connect(Mall.End.Street(r.At(rr, 0f), -r.In), null, Mall.End.Street(r.At(rr, rrr), r.In), NetRole.Alley, asset, h);
                    if (b == null) mall.Remove(a);
                    else
                    {
                        legs.Add(a); legs.Add(b);
                        // Through: the lot takes the depth (up to four aisles), a planted strip at the back.
                        float avail = math.min(rl, rrr) - Verge - 12f;
                        lt = Mall.LotDepth(math.clamp((int)math.floor(avail / Mall.LotDepth(1)), 1, 4));
                    }
                }
            }
            if (legs.Count == 0)
            {
                road = mall.Connect(Mall.End.Street(r.At(l, 0f), -r.In), new[] { r.At(l, back), r.At(rr, back) }, Mall.End.Street(r.At(rr, 0f), -r.In), NetRole.Alley, asset, h);
                if (road == null)
                {
                    if (mall.Extra) return;   // (shops) another frontage: nothing left behind
                    mall.Row(r, from, to, 0f, math.min(cells, 4), 1f, null, unit);
                    ctx.Issue(IssueSeverity.Info, FallbackKey);
                    return;
                }
                legs.Add(road);
            }

            // The lot behind its verge, entered from the street and from the road on every side it touches.
            float s0 = c - w * 0.5f, s1 = c + w * 0.5f, t0 = Verge, t1 = Verge + lt;
            var e = new List<(float2, string)>();
            LotEntrances(e, r, s0 + 6f, s1 - 6f, t0, "entrance");
            // Joins split the road; a piece of a 12 m road shorter than two cells gets no zone block (BlockSystem), so
            // stubs and walkways keep ≥ 20 m apart along the road: the terraces stay zoned.
            float legLen = Verge + lt;
            e.Add((r.At(s0, math.lerp(t0, t1, 0.65f)), "alley"));
            e.Add((r.At(s1, math.lerp(t0, t1, 0.65f)), "alley"));
            if (legLen * 0.3f >= 20f)
            {
                e.Add((r.At(s0, math.lerp(t0, t1, 0.3f)), "footpath"));
                e.Add((r.At(s1, math.lerp(t0, t1, 0.3f)), "footpath"));
            }
            if (!twin)
            {
                float run = w + 2f * h;
                if (run >= 66f)
                {
                    e.Add((r.At(math.lerp(s0, s1, 0.25f), t1), "alley"));
                    e.Add((r.At(math.lerp(s0, s1, 0.75f), t1), "alley"));
                }
                // A walkway from the aisles to the road in front of the back terrace (people cross to the store fronts).
                if (run >= 88f) e.Add((r.At(c, t1), "footpath"));
            }
            if (!mall.Lot(Mall.Rect(r, s0, s1, t0, t1), e, Mall.Parking(ctx), 0xF1u)) ctx.Issue(IssueSeverity.Info, NoLotKey);

            // The big boxes: outside the road, facing the lot. Corner plots are left to the back terrace.
            if (twin && legs.Count == 2)
            {
                // Outside each road a full terrace; between the roads (beyond the lot) the land is shared evenly.
                float gap = (rr - zo) - (l + zo);
                int innerCells = math.clamp((int)math.floor(gap * 0.5f / Mall.Cell), 0, cells);
                mall.LaneRows(legs[0], cells, null, 1, 0f, 0f, unit, math.min(cells, 3));
                mall.LaneRows(legs[1], cells, null, -1, 0f, 0f, unit, math.min(cells, 3));
                if (innerCells > 0)
                {
                    mall.LaneRows(legs[0], innerCells, null, -1, mall.GateTrim(legs[0], true, t1 + Mall.Cell), 0f, unit, math.min(innerCells, 2));
                    mall.LaneRows(legs[1], innerCells, null, 1, mall.GateTrim(legs[1], true, t1 + Mall.Cell), 0f, unit, math.min(innerCells, 2));
                }
            }
            // The horseshoe runs up the left leg, across and down: its left side (+1) is the outside all the way round.
            else foreach (var lane in legs) mall.LaneRows(lane, cells, null, 1, 0f, 0f, unit, math.min(cells, 3));
            // Pads on the main street beside the horseshoe: small separate plots with planting between.
            if (ctx.B("pads"))
            {
                Pads(mall, r, from, l - zo - side - Mall.Gap, unit);
                Pads(mall, r, rr + zo + side + Mall.Gap, to, unit);
                // The other streets: small units back to back with the boxes (a whole cell off the road).
                var others = new List<StreetShops.Run>();
                for (int i = 1; i < runs.Count; i++) others.Add(runs[i]);
                mall.StreetRows(others, 3, ZoneKinds.CommercialLow, 2, 2);
            }
        }

        /// <summary>Pad sites: two-cell plots, three deep, one every ~30 m of frontage with planting between.</summary>
        private static void Pads(Mall mall, StreetShops.Run r, float s0, float s1, int unit)
        {
            float len = s1 - s0;
            if (len < 2 * Mall.Cell) return;
            int n = math.max(1, (int)math.floor((len + 14f) / 30f));
            float step = len / n;
            for (int i = 0; i < n; i++)
            {
                float mid = s0 + step * (i + 0.5f);
                float half = math.min(Mall.Cell, step * 0.5f - 3f);
                if (half < Mall.Cell * 0.5f) continue;
                mall.Row(r, mid - half, mid + half, 0f, 3, 1f, ZoneKinds.CommercialLow, 2, 2);
            }
        }
    }
}
