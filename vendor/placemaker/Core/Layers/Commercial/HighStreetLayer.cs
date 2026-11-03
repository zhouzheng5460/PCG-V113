// s32 — High street / market street: a pair of pedestrian streets (or one shared lane each side) leaves the main street
// and runs through the site — on to a street at the back when there is one, else round the far end as a U — with a
// long market strip between them: stalls and kiosks under trees (Market square planner), a café terrace or a garden
// walk (Plaza planner), or a planted avenue. Small plots (narrow, shallow, low-density commercial by default) line the
// outer side of each lane, every shop facing the market across its lane. Side lots on the main street are joined to
// the shop fronts by paseos (a paved walk through the row, from the lot's footpath entrance to the lane).
// Presets: market · farmers · cafe · boulevard · shared.

using System.Collections.Generic;
using Placemaker.Core.Layers.Common;
using Unity.Mathematics;

namespace Placemaker.Core.Layers.Commercial
{
    [AreaGenerator(21, Group = Group)]
    public sealed class HighStreetLayer : CommercialLayer
    {
        protected override string Area => "HighStreet";
        public override string Id => "highStreet";
        public override string Icon => "Media/Game/Icons/Pedestrian.svg";
        public override float MinArea => 4000f;
        protected override float GradeTolerance => 0.04f;

        public static readonly string[] Centres = { "market", "farmers", "cafe", "garden", "trees" };
        public static readonly string[] LaneKinds = { "pedestrian", "shared" };
        public static readonly string[] Grains = { "fine", "mixed", "coarse" };
        /// <summary>Planted forecourt between a street and the first plots of a lane leaving it (m): beyond the facing tolerance.</summary>
        public const float Forecourt = 4f;

        protected override void DefineControls(ControlSet c)
        {
            c.Choice("centre", "market", ControlTier.Essential, "layout", Centres).Tip();
            c.Choice("lane", "pedestrian", ControlTier.Essential, "layout", LaneKinds).Tip();
            c.Slider("centreWidth", 20, 12, 36, 4, "m", ControlTier.Essential, "layout").Tip();
            c.Slider("streetLength", 160, 48, 240, 8, "m", ControlTier.More, "layout").Tip();
            c.Choice("grain", "fine", ControlTier.More, "shops", Grains).Tip();
            c.Toggle("sideLots", true, ControlTier.More, "parking").Tip();
            OuterRowControls(c, ZoneKinds.CommercialLow);
            CommonControls(c, ZoneKinds.CommercialLow, 16, "medium");
            c.Fixed("centre");
            c.Fixed("lane");
        }

        protected override void DefinePresets()
        {
            Preset("market", ("centre", "market"), ("lane", "pedestrian"), ("centreWidth", 20.0));
            Preset("farmers", ("centre", "farmers"), ("lane", "pedestrian"), ("centreWidth", 24.0), ("zoneKind", ZoneKinds.Mixed), ("grain", "mixed"));
            Preset("cafe", ("centre", "cafe"), ("lane", "pedestrian"), ("centreWidth", 16.0), ("grain", "mixed"), ("plotDepth", 24.0));
            Preset("boulevard", ("centre", "garden"), ("lane", "pedestrian"), ("centreWidth", 24.0), ("plotDepth", 24.0), ("grain", "coarse"), ("zoneKind", ZoneKinds.CommercialHigh));
            Preset("shared", ("centre", "trees"), ("lane", "shared"), ("centreWidth", 12.0), ("grain", "mixed"), ("lotSize", "medium"));
        }

        protected override void Compose(LayerContext ctx, Mall mall, List<StreetShops.Run> runs)
        {
            var r = runs[0];
            bool shared = ctx.S("lane") == "shared";
            AssetRef asset; float hs; bool ped;
            if (shared) { var sl = Mall.ShopLane(ctx); asset = sl.asset; hs = sl.half; ped = false; }
            else { var pl = Mall.PedLane(ctx, false); asset = pl.asset; hs = pl.half; ped = pl.pedestrian; }
            var role = ped ? NetRole.Street : NetRole.Alley;
            int cells = Cells(ctx), grain = math.max(1, System.Array.IndexOf(Grains, ctx.S("grain")) + 1);
            float m = ctx.Raw("centreWidth");
            float from = r.From + (r.StartCorner ? Mall.CornerClear : 2f), to = r.To - (r.EndCorner ? Mall.CornerClear : 2f);
            float c = 0.5f * (from + to);
            for (int guard = 0; guard < 16; guard++)
            {
                float half = m * 0.5f + 2f * hs + cells * Mall.Cell;
                if (c - half >= from && c + half <= to) break;
                if (m > 12f) m -= 4f; else if (cells > 1) cells--; else break;
            }
            float d = cells * Mall.Cell;
            float sL = c - m * 0.5f - hs, sR = c + m * 0.5f + hs;
            float reach = mall.Reach(r, sL - hs - d, sR + hs + d);
            float want = ctx.Raw("streetLength");

            // Through to a street at the back when the site is about as deep as the street is long, else a U.
            var lanes = new List<Mall.Lane>();
            float len;
            float rl = mall.ReachAt(r, sL), rr = mall.ReachAt(r, sR);
            bool through = want >= math.min(rl, rr) - 2f * Mall.Cell
                && StreetShops.StreetEnd(ctx.Input, r.At(sL, rl), r.In, out _, out _, out _)
                && StreetShops.StreetEnd(ctx.Input, r.At(sR, rr), r.In, out _, out _, out _);
            if (through)
            {
                var a = mall.Connect(Mall.End.Street(r.At(sL, 0f), -r.In), null, Mall.End.Street(r.At(sL, rl), r.In), role, asset, hs);
                var b = a == null ? null : mall.Connect(Mall.End.Street(r.At(sR, 0f), -r.In), null, Mall.End.Street(r.At(sR, rr), r.In), role, asset, hs);
                if (a != null) lanes.Add(a);
                if (b != null) lanes.Add(b);
                len = math.min(rl, rr);
                through = b != null;
            }
            else len = 0f;
            if (!through)
            {
                foreach (var l in lanes) mall.Remove(l);
                lanes.Clear();
                len = math.floor(math.min(want, reach - 2f * hs - d - 2f) / Mall.Cell) * Mall.Cell;
                if (len >= 3 * Mall.Cell)
                {
                    var u = mall.Connect(Mall.End.Street(r.At(sL, 0f), -r.In), new[] { r.At(sL, len + hs), r.At(sR, len + hs) },
                        Mall.End.Street(r.At(sR, 0f), -r.In), role, asset, hs);
                    if (u != null) lanes.Add(u);
                }
            }
            if (lanes.Count == 0)
            {
                if (mall.Extra) return;   // (shops) another frontage: nothing left behind

                mall.ShopRow(r, from, to, 0f, cells);
                ctx.Issue(IssueSeverity.Info, FallbackKey);
                return;
            }

            // The market strip between the lanes, open to the street and onto both lanes every ~36 m.
            var strip = Mall.Rect(r, c - m * 0.5f, c + m * 0.5f, 0f, len);
            var doors = new List<(Mall.Lane, float2)>();
            var laneL = lanes[0];
            var laneR = lanes.Count > 1 ? lanes[1] : lanes[0];
            for (float t = 18f; t < len - 8f; t += 36f)
            {
                doors.Add((laneL, r.At(sL, t)));
                doors.Add((laneR, r.At(sR, t)));
            }
            Centre(ctx, mall, strip, doors, ctx.S("centre"));

            // Side lots on the main street, each joined to the shop fronts by a paseo through the row.
            var paseos = new List<(float2[] rect, float2 a, float2 b, Mall.Lane lane)>();
            if (ctx.B("sideLots"))
            {
                float lt = math.min(Mall.LotDepth(Mall.Aisles(ctx.S("lotSize"))), reach - 2f);
                uint salt = 0xE10u;
                foreach (var (s0, s1, edge, laneS, lane) in new[] { (from, sL - hs - d, sL - hs - d, sL, laneL), (sR + hs + d, to, sR + hs + d, sR, laneR) })
                {
                    if (s1 - s0 < 28f || lt < 18f) continue;
                    var e = new List<(float2, string)>();
                    LotEntrances(e, r, s0, s1, 0f, "entrance");
                    float tp = math.min(lt * 0.5f, len - Mall.Cell);
                    // (s39) Two whole cell columns of plots between the forecourt and the paseo (the game blocks a lone column).
                    tp = math.max(tp, Forecourt + Mall.MinStretch + Mall.PaseoWidth * 0.5f);
                    if (tp > lt - 3f || len - tp - Mall.PaseoWidth * 0.5f < Mall.MinStretch) { mall.Lot(Mall.Rect(r, s0, s1, 0f, lt), e, Mall.Parking(ctx), salt++); continue; }
                    var paseo = mall.Hold(Mall.Rect(r, edge, laneS + (laneS < c ? -hs : hs), tp - Mall.PaseoWidth * 0.5f, tp + Mall.PaseoWidth * 0.5f));
                    e.Add((r.At(edge, tp), "footpath"));
                    if (mall.Lot(Mall.Rect(r, s0, s1, 0f, lt), e, Mall.Parking(ctx), salt++))
                        paseos.Add((paseo, r.At(edge, tp), r.At(laneS + (laneS < c ? -hs : hs), tp), lane));
                    else mall.Release(paseo);
                }
            }

            // Shops: every lane lined on its outer side (the inner side is the market).
            // A small forecourt where a lane leaves a street: the first plots face the lane only (a corner plot would be
            // levelled to the street while its side meets a lane climbing away from it).
            foreach (var l in lanes) mall.LaneRows(l, cells, null, 0, mall.GateTrim(l, true, Forecourt), mall.GateTrim(l, false, Forecourt), grain);
            // The streets round the site keep their own shop fronts (deeper, a whole cell off the lanes).
            if (ctx.B("outerRow")) mall.StreetRows(runs, math.min(6, cells + 1), ctx.S("outerZone"), 3);
            uint ps = 0xE40u;
            foreach (var p in paseos) mall.DressPaseo(p.rect, p.a, p.b, ps += 4);
        }

        private static void Centre(LayerContext ctx, Mall mall, float2[] strip, List<(Mall.Lane, float2)> doors, string centre)
        {
            bool ok;
            switch (centre)
            {
                case "farmers": ok = mall.FillOpen(strip, "marketSquare", "farmers", doors, null, 0xE01u); break;
                case "cafe": ok = mall.FillOpen(strip, "plaza", "cafe", doors, null, 0xE02u, b => b.Values["palette"] = GroundPalettes.Classic); break;   // (shops) grey paving, not a sand strip
                case "garden": ok = mall.FillOpen(strip, "plaza", "garden", doors, null, 0xE03u); break;
                case "trees": mall.Green(strip, 0xE04u, 8f); ok = true; break;
                default: ok = mall.FillOpen(strip, "marketSquare", "weekly", doors, null, 0xE05u); break;
            }
            if (!ok && !mall.FillOpen(strip, "plaza", null, doors, null, 0xE06u)) mall.Green(strip, 0xE07u, 8f);
        }
    }
}
