// Outdoor mall / lifestyle centre: a pedestrian shopping street (vanilla "Pedestrian Street", which carries zone
// blocks) leaves the main street with shop plots on both sides and ends on a plaza with a fountain (Plaza planner);
// behind the plaza a green with a playground (Park planner + playground). Styles (presets):
//   promenade  one straight pedestrian street (8 m), plaza at its end, side lots on the main street joined to the
//              street by paseos (a paved walk through the row from the lot's footpath entrance)
//   grand      the wide 16 m pedestrian street, deeper high-density plots, bigger lots
//   loop       a pedestrian loop (both ends on the street) with shops on both sides of it — outside facing in,
//              inside facing out — around a central court
//   lifestyle  (s32) the whole centre inside a ring drive: shops face the pedestrian street, their backs keep a planted
//              cell from the ring; plaza and green at the far end; perimeter lots outside the ring with planted
//              islands, entered from the street and the ring; paseos carry shoppers from the lots across the ring
//              through the rows to the shop fronts
// The other streets round the site keep their own shop fronts (s32, "Street shops").

using System.Collections.Generic;
using Placemaker.Core.Layers.Common;
using Unity.Mathematics;

namespace Placemaker.Core.Layers.Commercial
{
    [AreaGenerator(17, Group = Group)]
    public sealed class OutdoorMallLayer : CommercialLayer
    {
        protected override string Area => "OutdoorMall";
        public override string Id => "outdoorMall";
        public override string Icon => "Media/Game/Icons/Pedestrian.svg";
        public override float MinArea => 4000f;
        protected override float GradeTolerance => 0.04f;

        public static readonly string[] Styles = { "promenade", "grand", "loop", "lifestyle" };

        protected override void DefineControls(ControlSet c)
        {
            c.Choice("style", "promenade", ControlTier.Essential, "layout", Styles).Tip();
            c.Slider("plazaSize", 36, 0, 64, 4, "m", ControlTier.Essential, "layout").Tip();
            c.Slider("spineLength", 120, 40, 240, 10, "m", ControlTier.More, "layout").Tip();
            c.Toggle("sideLots", true, ControlTier.More, "parking").Tip();
            c.Toggle("playground", true, ControlTier.More, "layout").Tip();
            OuterRowControls(c, ZoneKinds.CommercialLow, false);
            CommonControls(c, ZoneKinds.CommercialLow, 24, "medium");
            c.Fixed("style");
        }

        protected override void DefinePresets()
        {
            Preset("promenade", ("style", "promenade"));
            Preset("grand", ("style", "grand"), ("lotSize", "large"), ("plotDepth", 24.0), ("plazaSize", 48.0), ("zoneKind", ZoneKinds.CommercialHigh), ("outerRow", true), ("outerZone", ZoneKinds.CommercialHigh));
            Preset("loop", ("style", "loop"), ("plazaSize", 40.0), ("plotDepth", 16.0));
            Preset("village", ("style", "promenade"), ("plotDepth", 16.0), ("lotSize", "small"), ("plazaSize", 28.0), ("zoneKind", ZoneKinds.Mixed), ("outerZone", ZoneKinds.Mixed));
            Preset("lifestyle", ("style", "lifestyle"), ("plotDepth", 24.0), ("plazaSize", 40.0), ("spineLength", 100.0), ("lotSize", "medium"), ("outerRow", true), ("outerZone", ZoneKinds.CommercialHigh));
        }

        protected override void Compose(LayerContext ctx, Mall mall, List<StreetShops.Run> runs)
        {
            var r = runs[0];
            string style = ctx.S("style");
            bool grand = style == "grand";
            var (asset, hs, ped) = Mall.PedLane(ctx, grand);
            var role = ped ? NetRole.Street : NetRole.Alley;
            int cells = Cells(ctx);
            float from = r.From + (r.StartCorner ? Mall.CornerClear : 2f), to = r.To - (r.EndCorner ? Mall.CornerClear : 2f);
            float c = 0.5f * (from + to);
            if (style == "lifestyle")
            {
                if (Lifestyle(ctx, mall, runs, r, from, to, c, hs, asset, role, cells)) return;
                style = "promenade";
                ctx.Issue(IssueSeverity.Info, FallbackKey);
            }
            float blockL, blockR;
            Mall.Lane spine;
            bool ok = style == "loop"
                ? Loop(ctx, mall, r, c, hs, asset, role, ref cells, out blockL, out blockR, out spine)
                : Promenade(ctx, mall, r, c, hs, asset, role, ref cells, out blockL, out blockR, out spine);
            if (!ok)
            {
                if (mall.Extra) return;   // (shops) another frontage: nothing left behind

                mall.ShopRow(r, from, to, 0f, cells);
                ctx.Issue(IssueSeverity.Info, FallbackKey);
                return;
            }
            var paseos = new List<(float2[] rect, float2 a, float2 b)>();
            if (ctx.B("sideLots"))
            {
                float lt = math.min(Mall.LotDepth(Mall.Aisles(ctx.S("lotSize"))), mall.Reach(r, from, to) - 2f);
                uint salt = 0xC10u;
                foreach (var (s0, s1, edge) in new[] { (from, blockL, blockL), (blockR, to, blockR) })
                {
                    if (s1 - s0 < 28f || lt < 18f) continue;
                    var e = new List<(float2, string)>();
                    LotEntrances(e, r, s0, s1, 0f, "entrance");
                    // (s32) A paseo through the shop row: from the lot's footpath entrance straight to the pedestrian street.
                    float2[] held = null;
                    float tp = math.min(lt * 0.5f, 40f);
                    // (s39) Far enough up the street to leave two whole columns of plots below the paseo.
                    tp = math.min(math.max(tp, Mall.MinStretch + Mall.PaseoWidth * 0.5f), lt - 3f);
                    float lotEdge = edge < c ? s1 : s0;
                    float laneEdge = edge < c ? c - hs : c + hs;
                    // The lot's footpath finds the street within 40 m (the parking planner's search): deeper rows get no paseo.
                    // (s39) …and leaves two whole cell columns of plots either side of it (the game blocks a lone column).
                    if (spine != null && math.abs(laneEdge - lotEdge) + hs <= 38f
                        && tp - Mall.PaseoWidth * 0.5f >= Mall.MinStretch && math.dot(spine.Points[spine.Points.Count - 1] - r.At(c, 0f), r.In) - tp - Mall.PaseoWidth * 0.5f >= Mall.MinStretch)
                    {
                        held = mall.Hold(Mall.Rect(r, math.min(lotEdge, laneEdge), math.max(lotEdge, laneEdge), tp - Mall.PaseoWidth * 0.5f, tp + Mall.PaseoWidth * 0.5f));
                        e.Add((r.At(lotEdge, tp), "footpath"));
                    }
                    if (mall.Lot(Mall.Rect(r, s0, s1, 0f, lt), e, Mall.Parking(ctx), salt++))
                    {
                        if (held != null) paseos.Add((held, r.At(edge, tp), r.At(laneEdge, tp)));
                    }
                    else mall.Release(held);
                }
                if (mall.Lots.Count == 0)
                    foreach (var back in runs)
                        if (math.dot(back.In, r.In) < -0.7f && mall.FrontLot(back, 90f, Mall.Aisles(ctx.S("lotSize")), Mall.Parking(ctx), salt++)) break;
            }
            // Shops: both sides of the pedestrian street, around the paseos (the loop planned its own rows).
            if (spine != null) mall.LaneRows(spine, cells, null, 0, 0f, 0f, 3);
            if (ctx.B("outerRow")) mall.StreetRows(runs, math.min(6, cells + 1), ctx.S("outerZone"), 3, 2);
            uint ps = 0xC40u;
            foreach (var p in paseos) mall.DressPaseo(p.rect, p.a, p.b, ps += 4);
        }

        private static bool Promenade(LayerContext ctx, Mall mall, StreetShops.Run r, float c, float hs, AssetRef asset, NetRole role, ref int cells, out float blockL, out float blockR, out Mall.Lane spine)
        {
            blockL = blockR = c;
            spine = null;
            float plaza = ctx.Raw("plazaSize");
            float reach = mall.Reach(r, c - hs - cells * Mall.Cell, c + hs + cells * Mall.Cell);
            float len = math.min(ctx.Raw("spineLength"), reach - plaza - 2f);
            if (len < 3 * Mall.Cell && plaza > 0f) { plaza = 0f; len = math.min(ctx.Raw("spineLength"), reach - 10f); }
            if (len < 3 * Mall.Cell) return false;
            len = math.floor(len / Mall.Cell) * Mall.Cell;
            var lane = mall.AddLane(r.At(c, 0f), -r.In, new[] { r.At(c, len) }, null, default, role, asset, hs);
            if (lane == null) return false;
            spine = lane;
            float d = cells * Mall.Cell;
            blockL = c - hs - d; blockR = c + hs + d;
            if (plaza >= 20f)
            {
                // The plaza is as wide as the shop block (the rows end on it); its fountain on the street's axis.
                float pw = math.max(plaza, 2f * (hs + d));
                var square = Mall.Rect(r, c - pw * 0.5f, c + pw * 0.5f, len, len + plaza);
                if (!mall.FillOpen(square, "plaza", "fountain", null, new[] { r.At(c, len) }, 0xC1u, Paved, false)
                    && !mall.Fill(square, "plaza", null, new[] { r.At(c, len) }, 0xC5u))
                    ctx.Issue(IssueSeverity.Info, FallbackKey);
            }
            return true;
        }

        private static bool Loop(LayerContext ctx, Mall mall, StreetShops.Run r, float c, float hs, AssetRef asset, NetRole role, ref int cells, out float blockL, out float blockR, out Mall.Lane spine)
        {
            blockL = blockR = c;
            spine = null;
            float d = cells * Mall.Cell;
            // Inside the loop: a row of plots along each leg (facing out) and a court between them.
            float inner = math.max(ctx.Raw("plazaSize"), 24f) + 2 * d;
            float half = inner * 0.5f + 2 * hs + d;
            float reach = mall.Reach(r, c - half, c + half);
            float depth = math.min(ctx.Raw("spineLength") * 0.6f, reach - 2 * hs - d - 2f);
            depth = math.floor(depth / Mall.Cell) * Mall.Cell;
            if (depth < 4 * Mall.Cell || c - half < r.From || c + half > r.To) return false;
            float l = c - inner * 0.5f - hs, rr = c + inner * 0.5f + hs;
            var lane = mall.AddLane(r.At(l, 0f), -r.In, new[] { r.At(l, depth + hs), r.At(rr, depth + hs) }, r.At(rr, 0f), -r.In, role, asset, hs);
            if (lane == null) return false;
            // Outside the loop, facing in.
            Leg(mall, r, l - hs, -1f, 0f, depth + 2 * hs, cells);
            Leg(mall, r, rr + hs, 1f, 0f, depth + 2 * hs, cells);
            mall.ShopRow(r, l - hs, rr + hs, depth + 2 * hs, cells);
            // Inside the loop, facing out; the court between them opens to the street.
            Leg(mall, r, l + hs, 1f, 0f, depth, cells);
            Leg(mall, r, rr - hs, -1f, 0f, depth, cells);
            mall.ShopRow(r, l + hs + d, rr - hs - d, depth, cells, -1f);
            var court = Mall.Rect(r, l + hs + d, rr - hs - d, 0f, depth - d);
            if (!mall.FillOpen(court, "plaza", "fountain", null, null, 0xC2u, Paved) && !mall.Fill(court, "plaza", null, null, 0xC6u)) mall.Fill(court, "park", "pocket", null, 0xC3u);
            blockL = l - hs - d; blockR = rr + hs + d;
            return true;
        }

        /// <summary>
        /// (s32) Lifestyle centre: pedestrian street + plots + plaza + green inside a ring drive, lots outside, paseos across.
        /// False when the site is too small (the caller falls back to the promenade).
        /// </summary>
        private static bool Lifestyle(LayerContext ctx, Mall mall, List<StreetShops.Run> runs, StreetShops.Run r, float from, float to, float c, float hs, AssetRef asset, NetRole role, int cells)
        {
            var (ringAsset, hr) = Mall.ShopLane(ctx);
            float g = Mall.Gap;
            float reach = mall.Reach(r, from, to);
            int aisles = Mall.Aisles(ctx.S("lotSize"));
            // Width: core + planted gaps + ring + at least a one-aisle lot each side.
            for (; cells > 1; cells--)
            {
                float coreHalf = hs + cells * Mall.Cell + g + hr;
                if (c - coreHalf - hr - Mall.LotDepth(1) >= from && c + coreHalf + hr + Mall.LotDepth(1) <= to) break;
            }
            float d = cells * Mall.Cell, hc = hs + d;
            float sL = c - hc - g - hr, sR = c + hc + g + hr;
            if (sL - hr - from < Mall.LotDepth(1) - 2f) return false;
            float plaza = ctx.Raw("plazaSize");
            if (plaza < 20f) plaza = 0f;
            float tB = math.min(reach - hr - 2f, ctx.Raw("spineLength") + plaza + 36f + hr);
            float len = math.floor(math.min(ctx.Raw("spineLength"), tB - hr - plaza - 20f) / Mall.Cell) * Mall.Cell;
            if (len < 4 * Mall.Cell) return false;

            var ring = mall.Connect(Mall.End.Street(r.At(sL, 0f), -r.In), new[] { r.At(sL, tB), r.At(sR, tB) }, Mall.End.Street(r.At(sR, 0f), -r.In), NetRole.Alley, ringAsset, hr);
            if (ring == null) return false;
            var spine = mall.Connect(Mall.End.Street(r.At(c, 0f), -r.In), null, Mall.End.Free(r.At(c, len)), role, asset, hs);
            if (spine == null) { mall.Remove(ring); return false; }

            // Paseos: a walk from the ring across the planted gap and through the row to the street, both sides.
            var paseos = new List<(float2[] rect, float2 a, float2 b)>();
            var tps = new List<float>();
            if (len >= 72f) { tps.Add(math.round(len * 0.33f)); tps.Add(math.round(len * 0.7f)); } else tps.Add(math.round(len * 0.5f));
            // A paseo stays clear of the ring's back corner (a walk there would meet the bend, not the leg).
            tps.RemoveAll(tp => tp > math.min(len - Mall.Cell, tB - hr - 6f));
            // (s39) Two whole cell columns of plots between paseos and at the ends (the game blocks a lone column).
            tps.RemoveAll(tp => tp - Mall.PaseoWidth * 0.5f < Mall.MinStretch || len - tp - Mall.PaseoWidth * 0.5f < Mall.MinStretch);
            if (tps.Count == 2 && tps[1] - tps[0] < Mall.MinStretch + Mall.PaseoWidth) tps.RemoveAt(1);
            foreach (float tp in tps)
                foreach (int side in new[] { -1, 1 })
                {
                    float leg = side < 0 ? sL : sR, edge = c + side * hs;
                    if (!mall.Walk(r.At(leg, tp), ring, r.At(c, tp), spine)) continue;
                    var rect = Mall.Rect(r, math.min(leg - side * hr, edge), math.max(leg - side * hr, edge), tp - Mall.PaseoWidth * 0.5f, tp + Mall.PaseoWidth * 0.5f);
                    paseos.Add((mall.Hold(rect), r.At(edge + side * d, tp), r.At(edge, tp)));
                }

            // The plaza at the street's end (fountain), the green beyond it up to the ring (a park with a playground).
            float2[] square = null, green = null;
            if (plaza > 0f) square = mall.Hold(Mall.Rect(r, c - hc, c + hc, len, len + plaza));
            float g0 = len + plaza, g1 = tB - hr;
            if (g1 - g0 >= 14f) green = mall.Hold(Mall.Rect(r, sL + hr, sR - hr, g0, g1));

            // Lots outside the ring: street entrances, an alley stub onto the ring, footpaths at the paseo nodes.
            float lt = math.min(Mall.LotDepth(aisles + 1), tB + hr);
            uint salt = 0xC20u;
            // On a slope the lots (graded in level tiers of their own) keep a planted bank from the ring.
            float bank = mall.SlopeGap() * 0.5f;
            foreach (var (s0, s1, side) in new[] { (from, sL - hr - bank, -1), (sR + hr + bank, to, 1) })
            {
                if (s1 - s0 < 22f) continue;
                float edge = side < 0 ? s1 : s0;
                float lotLen = math.min(lt, mall.Reach(r, s0, s1) - 2f);
                if (lotLen < 18f) continue;
                var e = new List<(float2, string)>();
                LotEntrances(e, r, s0, s1, 0f, "entrance");
                // The stub onto the ring keeps clear of the paseo footpaths (two joins a few cm apart would split the ring into
                // a sliver and leave one of them short of its node).
                foreach (float at in new[] { math.min(lotLen - 8f, tB * 0.85f), lotLen * 0.55f, lotLen * 0.2f })
                    if (at > 6f && tps.TrueForAll(tp => math.abs(tp - at) >= 8f)) { e.Add((r.At(edge, at), "alley")); break; }
                foreach (float tp in tps) if (tp < lotLen - 3f) e.Add((r.At(edge, tp), "footpath"));
                mall.Lot(Mall.Rect(r, s0, s1, 0f, lotLen), e, Mall.Parking(ctx), salt++);
            }
            // A lot behind the ring when the site goes on (entered from the ring's back run).
            float backDepth = reach - (tB + hr) - 2f;
            if (backDepth >= Mall.LotDepth(1) + 2f)
            {
                int ba = math.clamp((int)math.floor((backDepth - 2f) / Mall.LotDepth(1)), 1, aisles + 1);
                float t0 = tB + hr, t1 = t0 + Mall.LotDepth(ba);
                var e = new List<(float2, string)> { (r.At(math.lerp(sL, sR, 0.25f), t0), "alley"), (r.At(math.lerp(sL, sR, 0.75f), t0), "alley"), (r.At(c, t0), "footpath") };
                mall.Lot(Mall.Rect(r, sL - hr, sR + hr, t0, t1), e, Mall.Parking(ctx), salt++);
            }

            // Shops: both sides of the pedestrian street (around the paseos), then the other streets' own fronts.
            mall.LaneRows(spine, cells, null, 0, 0f, 0f, 3);
            if (ctx.B("outerRow")) mall.StreetRows(runs, math.min(6, cells + 1), ctx.S("outerZone"), 3, 2);

            if (square != null)
            {
                mall.Release(square);
                var sd = green != null ? new[] { r.At(c, len), r.At(c, g0) } : new[] { r.At(c, len) };
                if (!mall.FillOpen(square, "plaza", "fountain", null, sd, 0xC21u, Paved, false)) mall.Fill(square, "plaza", null, sd, 0xC22u);
            }
            if (green != null)
            {
                mall.Release(green);
                var doors = new List<(Mall.Lane, float2)> { (ring, r.At(c, tB)) };
                if (mall.FillOpen(green, "park", "urban", doors, new[] { r.At(c, g0) }, 0xC23u, b => { b.Values["fence"] = false; b.Values["belt"] = false; }, false))
                {
                    if (ctx.B("playground")) mall.AddOn(green, "playground", 0xC24u, b => b.Values["size"] = "small");
                }
                else mall.Green(green, 0xC25u);
            }
            uint ps = 0xC60u;
            foreach (var p in paseos) mall.DressPaseo(p.rect, p.a, p.b, ps += 4);
            return true;
        }

        /// <summary>Plots along a lane side at s = edge, reaching away (dir), from t0 to t1.</summary>
        private static void Leg(Mall mall, StreetShops.Run r, float edge, float dir, float t0, float t1, int cells)
        {
            float len = t1 - t0;
            int n = math.max(1, (int)math.ceil(len / (3 * Mall.Cell)));
            for (int k = 0; k < n; k++)
            {
                float a = t0 + len * k / n, b = t0 + len * (k + 1) / n;
                for (int dd = cells; dd >= 1; dd--)
                    if (mall.Shop(Mall.Rect(r, edge, edge + dir * dd * Mall.Cell, a, b))) break;
            }
        }

        /// <summary>(shops) The plaza in grey paving: the fountain style's sandstone read as a sand lot in the game.</summary>
        private static void Paved(SettingsBag b) => b.Values["palette"] = GroundPalettes.Classic;

        protected override bool KeepsPark(LayerContext ctx) => ctx.B("extraGreen");

        protected override void FillRest(LayerContext ctx, Mall mall)
        {
            if (KeepsPark(ctx)) GreenRest(ctx, mall, "pocket", ctx.B("playground"), 0xC80u);
            else base.FillRest(ctx, mall);
        }
    }
}
