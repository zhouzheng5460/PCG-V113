// s32 — Mall district ("all mall"): the whole block becomes shops. Deep high-density plots line every street round the
// site (big buildings); inside, a grid of lanes — rails from the main street to the back (through to a back street, else
// closed by a back lane into a U), rungs across them — is lined on both sides with shallow low-density plots (little
// buildings). Where rungs cross a middle rail the four corners open into small squares; the blocks between rails and
// rungs keep a courtyard garden in the middle, reached by a paseo from the rung below. Lane mouths on the streets get
// planted gateway strips (a whole cell, so the street — not the lane — zones the corner plots).
// Presets: district · pedestrian · mixedUse · dense · village.

using System.Collections.Generic;
using Placemaker.Core.Geometry;
using Placemaker.Core.Layers.Common;
using Unity.Mathematics;

namespace Placemaker.Core.Layers.Commercial
{
    [AreaGenerator(23, Group = Group)]
    public sealed partial class MallDistrictLayer : CommercialLayer
    {
        protected override string Area => "MallDistrict";
        public override string Id => "mallDistrict";
        public override string Icon => "Media/Game/Icons/ZoneCommercial.svg";
        public override float MinArea => 9000f;
        protected override float GradeTolerance => 0.04f;

        public static readonly string[] LaneKinds = { "mixed", "alley", "pedestrian" };
        public static readonly string[] Blocks = { "compact", "medium", "spacious" };
        public static readonly string[] ParkingBlocks = { "none", "some", "many" };

        protected override void DefineControls(ControlSet c)
        {
            c.Choice("lanes", "mixed", ControlTier.Essential, "layout", LaneKinds).Tip();
            c.Choice("blocks", "medium", ControlTier.Essential, "layout", Blocks).Tip();
            c.Toggle("squares", true, ControlTier.More, "layout").Tip();
            // (shops2) Parking lots mixed in among the blocks (the user, Downtown: "it needs more parking, intermingled").
            c.Choice("parkingBlocks", "none", ControlTier.Essential, "parking", ParkingBlocks).Tip();
            OuterRowControls(c, ZoneKinds.CommercialHigh);
            Shared(c.Slider("outerDepth", 32, 16, 48, 8, "m", ControlTier.More, "shops")).Tip().When("outerRow=true");
            CommonControls(c, ZoneKinds.CommercialLow, 16, null, true);
            // (rf-shops3) The architect's extras.
            ExtrasControls(c, pads: false, trucks: 2);
            c.Fixed("lanes");
            c.Fixed("blocks");
        }

        protected override void DefinePresets()
        {
            Preset("district", ("lanes", "mixed"), ("blocks", "medium"));
            Preset("pedestrian", ("lanes", "pedestrian"), ("blocks", "medium"), ("outerDepth", 24.0));
            Preset("mixedUse", ("lanes", "mixed"), ("blocks", "spacious"), ("zoneKind", ZoneKinds.Mixed), ("plotDepth", 24.0));
            Preset("dense", ("lanes", "alley"), ("blocks", "compact"), ("zoneKind", ZoneKinds.CommercialHigh), ("plotDepth", 24.0), ("outerDepth", 40.0), ("squares", false), ("parkingBlocks", "many"));
            Preset("village", ("lanes", "pedestrian"), ("blocks", "compact"), ("plotDepth", 16.0), ("outerDepth", 16.0), ("outerZone", ZoneKinds.CommercialLow));
        }

        protected override void Compose(LayerContext ctx, Mall mall, List<StreetShops.Run> runs)
        {
            var r = runs[0];
            string kinds = ctx.S("lanes");
            var (railAsset, hr) = kinds == "pedestrian" ? Ped(ctx, out var railRole) : Lane(ctx, out railRole);
            var (rungAsset, hp) = kinds == "alley" ? Lane(ctx, out var rungRole) : Ped(ctx, out rungRole);
            // Lane rows stand back to back: two cells at least (a one-cell row hands the row behind it its second zone row).
            int inner = math.max(2, Cells(ctx));
            // (shops) The outer rows face the city streets: only with "Shops facing the streets"; else the lane grid takes the whole site.
            int outer = ctx.B("outerRow") && ctx.B("streetShops") ? Mall.Cells(ctx.Raw("outerDepth")) : 0;
            float court = ctx.S("blocks") == "compact" ? 0f : ctx.S("blocks") == "spacious" ? 32f : 16f;
            // Rows of neighbouring lanes stand back to back; on a slope they need a bank between them (Mall.SlopeGap).
            court = math.max(court, mall.SlopeGap());

            float s0 = r.From, s1 = r.To;
            float reach = mall.Reach(r, s0 + 2f, s1 - 2f);
            // (shops) On an angled or curved block the stretch of the run with the most land straight behind it, not the whole run.
            var span = mall.BestSpan(r, 60f, 60f, r.From + 2f, r.To - 2f);
            if (span.HasValue && (span.Value.s0 > r.From + 3f || span.Value.s1 < r.To - 3f)) { s0 = span.Value.s0 - 2f; s1 = span.Value.s1 + 2f; reach = span.Value.reach; }
            bool atStart = s0 <= r.From + 0.5f, atEnd = s1 >= r.To - 0.5f;
            bool right = (atEnd && r.EndCorner) || mall.StreetAlong(r.At(s1, 0f) + r.Dir * 0.5f, r.At(s1, reach) + r.Dir * 0.5f);
            bool back = mall.StreetAlong(r.At(s1, reach) + r.In * 0.5f, r.At(s0, reach) + r.In * 0.5f);
            bool left = (atStart && r.StartCorner) || mall.StreetAlong(r.At(s0, reach) - r.Dir * 0.5f, r.At(s0, 0f) - r.Dir * 0.5f);

            // (shops) No outer row: rows backing onto a street keep clear of its first zone row (the kerb strip there), by the
            // street's own width (Mall.KerbMargin).
            float kbL = mall.KerbMargin(r.At(s0, reach * 0.5f), r.In), kbR = mall.KerbMargin(r.At(s1, reach * 0.5f), r.In);
            float kbF = mall.KerbMargin(r.At(0.5f * (s0 + s1), 0f), r.Dir), kbB = mall.KerbMargin(r.At(0.5f * (s0 + s1), reach), r.Dir);
            // Fit: rails need a row on each side; shrink the rows until at least two rails and one block fit.
            float d = inner * Mall.Cell, o = outer * Mall.Cell;
            float sl = 0f, sr = 0f, tf = 0f, tb = 0f;
            for (int guard = 0; guard < 12; guard++)
            {
                d = inner * Mall.Cell; o = outer * Mall.Cell;
                sl = s0 + (left ? (outer == 0 ? kbL : o) : 0f) + d + hr;
                sr = s1 - (right ? (outer == 0 ? kbR : o) : 0f) - d - hr;
                tf = (outer == 0 ? kbF : o) + d + hp;
                tb = reach - (back ? (outer == 0 ? kbB : o) : 0f) - d - hr;
                if (sr - sl >= 2f * d + 2f * hr && tb - tf >= 2f * d + 2f * hp) break;
                // Rows back to back stay two cells deep (a one-cell row would hand the row behind it its second zone row).
                if (outer > 2) outer--; else if (inner > 2) inner--; else if (outer > 0) outer = 0; else break;
            }
            if (sr - sl < 2f * d + 2f * hr || tb - tf < d + hp)
            {
                if (mall.Extra) return;   // (shops) another frontage: nothing left behind
                mall.ShopRow(r, s0 + 2f, s1 - 2f, 0f, math.max(1, inner));
                ctx.Issue(IssueSeverity.Info, FallbackKey);
                return;
            }

            // Rails: evenly spaced, ~ two rows + a courtyard apart.
            float railStep = 2f * d + 2f * hr + court;
            int intervals = math.max(1, (int)math.round((sr - sl) / railStep));
            while (intervals > 1 && (sr - sl) / intervals < 2f * d + 2f * hr) intervals--;
            var xs = new List<float>();
            for (int k = 0; k <= intervals; k++) xs.Add(math.lerp(sl, sr, (float)k / intervals));

            var rails = new Mall.Lane[xs.Count];
            Mall.Lane u = null;
            if (back && Through(ctx, mall, r, xs))
            {
                for (int k = 0; k < xs.Count; k++)
                    rails[k] = mall.Connect(Mall.End.Street(r.At(xs[k], 0f), -r.In), null, Mall.End.Street(r.At(xs[k], mall.ReachAt(r, xs[k])), r.In), railRole, railAsset, hr);
                tb = reach - (outer == 0 ? kbB : o) - d - hp;
            }
            else
            {
                back = false;
                tb = reach - d - hr;
                u = mall.Connect(Mall.End.Street(r.At(sl, 0f), -r.In), new[] { r.At(sl, tb), r.At(sr, tb) }, Mall.End.Street(r.At(sr, 0f), -r.In), railRole, railAsset, hr);
                if (u == null)
                {
                    if (mall.Extra) return;   // (shops) another frontage: nothing left behind
                    mall.ShopRow(r, s0 + 2f, s1 - 2f, 0f, math.max(1, inner));
                    ctx.Issue(IssueSeverity.Info, FallbackKey);
                    return;
                }
                rails[0] = u; rails[xs.Count - 1] = u;
                for (int k = 1; k + 1 < xs.Count; k++)
                    rails[k] = mall.Connect(Mall.End.Street(r.At(xs[k], 0f), -r.In), null, Mall.End.OnLane(u, r.At(xs[k], tb)), railRole, railAsset, hr);
            }

            // Rungs: levels between the front row and the back, ~ two rows + a courtyard apart.
            float rungStep = 2f * d + 2f * hp + court, minStep = 2f * d + 2f * hp;
            // With a back street the last rung lies one row in front of the back plots; under a U the back lane is the last.
            float last = back ? tb : tb - rungStep;
            var ys = new List<float>();
            if (last >= tf - 0.5f)
            {
                int m = math.max(0, (int)math.round((last - tf) / rungStep));
                while (m > 0 && (last - tf) / m < minStep) m--;
                for (int j = 0; j <= m; j++) ys.Add(m == 0 ? tf : math.lerp(tf, last, (float)j / m));
            }
            else if (!back && tb - tf >= minStep) ys.Add(tf);
            var rungs = new List<(Mall.Lane lane, int k, float y)>();
            foreach (float y in ys)
                for (int k = 0; k + 1 < xs.Count; k++)
                {
                    if (rails[k] == null || rails[k + 1] == null) continue;
                    var g = mall.Connect(Mall.End.OnLane(rails[k], r.At(xs[k], y)), null, Mall.End.OnLane(rails[k + 1], r.At(xs[k + 1], y)), rungRole, rungAsset, hp);
                    if (g != null) rungs.Add((g, k, y));
                }
            // The middle rung reaches out to the side streets too (the district is crossed, not walled).
            if (ys.Count > 0)
            {
                float ym = ys[ys.Count / 2];
                if (left && rails[0] != null) mall.Connect(Mall.End.OnLane(rails[0], r.At(xs[0], ym)), null, Mall.End.Street(r.At(s0, ym), -r.Dir), rungRole, rungAsset, hp);
                if (right && rails[xs.Count - 1] != null) mall.Connect(Mall.End.OnLane(rails[xs.Count - 1], r.At(xs[xs.Count - 1], ym)), null, Mall.End.Street(r.At(s1, ym), r.Dir), rungRole, rungAsset, hp);
            }

            // Squares at the crossings of middle rails nearest the centre (four corners, each a small square): one or two
            // focal points, not a square at every corner of the grid.
            var squares = new List<float2[]>();
            var crossings = new List<(int k, float y, float dist)>();
            float cxm = 0.5f * (xs[0] + xs[xs.Count - 1]), cym = ys.Count > 0 ? 0.5f * (ys[0] + ys[ys.Count - 1]) : 0f;
            foreach (float y in ys)
                for (int k = 1; k + 1 < xs.Count; k++)
                    if (rails[k] != null) crossings.Add((k, y, math.abs(xs[k] - cxm) + math.abs(y - cym)));
            crossings.Sort((p, q) => p.dist.CompareTo(q.dist));
            // The central crossing, or the two equally central ones of an even grid.
            int keep = crossings.Count >= 2 && crossings[1].dist - crossings[0].dist < 1f ? 2 : 1;
            if (crossings.Count > keep) crossings.RemoveRange(keep, crossings.Count - keep);
            if (ctx.B("squares"))
                foreach (var (k, y, _) in crossings)
                    {
                        float q = math.min(14f, d + 2f);
                        for (int sx = -1; sx <= 1; sx += 2)
                            for (int sy = -1; sy <= 1; sy += 2)
                            {
                                float a0 = xs[k] + sx * hr, a1 = xs[k] + sx * (hr + q), b0 = y + sy * hp, b1 = y + sy * (hp + q);
                                var sq = Mall.Rect(r, math.min(a0, a1), math.max(a0, a1), math.min(b0, b1), math.max(b0, b1));
                                if (mall.Fits(sq)) squares.Add(mall.Hold(sq));
                            }
                    }

            // Courtyards: a paseo from each rung (and the front row) up into the block it opens.
            var paseos = new List<(float2[] rect, float2 a, float2 b)>();
            if (court >= 12f)
            {
                var levels = new List<(float y, float h, bool lane)>();
                foreach (float y in ys) levels.Add((y, hp, true));
                for (int j = 0; j < levels.Count; j++)
                {
                    float y = levels[j].y;
                    float top = j + 1 < levels.Count ? levels[j + 1].y - hp : tb - hr;
                    if (top - y - hp - 2f * d < 12f) continue;
                    for (int k = 0; k + 1 < xs.Count; k++)
                    {
                        var rung = rungs.Find(x => x.k == k && math.abs(x.y - y) < 0.1f).lane;
                        if (rung == null || xs[k + 1] - xs[k] - 2f * hr - 2f * d < 12f) continue;
                        float mid = 0.5f * (xs[k] + xs[k + 1]);
                        float t0 = y + hp, t1 = y + hp + d;
                        var rect = Mall.Rect(r, mid - Mall.PaseoWidth * 0.5f, mid + Mall.PaseoWidth * 0.5f, t0, t1);
                        if (!mall.Free(rect)) continue;
                        // The walk ends just inside the courtyard: its garden's walk is snapped onto that point (a door).
                        if (!mall.Walk(r.At(mid, y), rung, r.At(mid, t1 + 0.6f), null)) continue;
                        paseos.Add((mall.Hold(rect), r.At(mid, t0), r.At(mid, t1)));
                        mall.Doors.Add(r.At(mid, t1 + 0.6f));
                    }
                }
            }

            // (shops2) Parking blocks: some of the blocks between two rails and two rungs are surface lots instead of rows (held now,
            // planned after the rows), staggered so every lane passes a lot now and then.
            var lotBlocks = new List<(float2[] rect, int k, int j)>();
            var lotDoors = new List<(float2[] rect, List<(float2 at, Mall.Lane lane)> doors)>();
            int lotsMade = 0;
            if (ctx.S("parkingBlocks") != "none" && kinds != "pedestrian" && ys.Count >= 1)
            {
                var cells = new List<(int k, int j, float y0, float y1, float dist)>();
                // The bands between rungs; with a single rung, the band between it and the U's back lane.
                var bands = new List<(float y0, float y1)>();
                for (int j = 0; j + 1 < ys.Count; j++) bands.Add((ys[j] + hp, ys[j + 1] - hp));
                if (!back && ys.Count >= 1) bands.Add((ys[ys.Count - 1] + hp, tb - hr));
                for (int j = 0; j < bands.Count; j++)
                    for (int k = 0; k + 1 < xs.Count; k++)
                    {
                        if (rails[k] == null || rails[k + 1] == null) continue;
                        float cx = 0.5f * (xs[k] + xs[k + 1]), cy = 0.5f * (bands[j].y0 + bands[j].y1);
                        cells.Add((k, j, bands[j].y0, bands[j].y1, math.abs(cx - cxm) + math.abs(cy - cym)));
                    }
                bool many = ctx.S("parkingBlocks") == "many";
                var pick = new List<(int k, int j, float y0, float y1, float dist)>();
                if (cells.Count <= 3)
                {
                    cells.Sort((p, q) => p.dist.CompareTo(q.dist));
                    if (cells.Count > 0) pick.Add(cells[0]);
                }
                else foreach (var c in cells) if ((c.k + 2 * c.j) % (many ? 2 : 3) == 1) pick.Add(c);
                foreach (var c in pick)
                {
                    float a0 = xs[c.k] + hr, a1 = xs[c.k + 1] - hr;
                    if (c.y1 - c.y0 < Mall.LotDepth(1) + 2f || a1 - a0 < 30f) continue;
                    var rect = Mall.Rect(r, a0, a1, c.y0, c.y1);
                    if (!mall.Fits(rect)) continue;
                    var held = mall.Hold(rect);
                    lotBlocks.Add((held, c.k, c.j));
                    // Its ways in: the middle of each rail side, and of the rungs before and behind it when they carry cars. The lot's
                    // stubs join the lanes there, so the rows across those lanes are split at the same points now (a plot straddling a
                    // join is never zoned), and the lot is entered exactly there.
                    float midT = 0.5f * (c.y0 + c.y1), midS = 0.5f * (a0 + a1);
                    // In from the rungs (the middle of a long run); from the rails only when the rungs are footways: a stub near a
                    // rail's junction would leave a stretch of rail too short for a zone block.
                    var doors = new List<(float2, Mall.Lane)>();
                    if (kinds == "alley")
                        foreach (var g in rungs)
                            if (g.k == c.k && (math.abs(g.y + hp - c.y0) < 0.5f || math.abs(g.y - hp - c.y1) < 0.5f))
                                doors.Add((Mall.Project(g.lane, r.At(midS, g.y), out _), g.lane));
                    if (doors.Count == 0)
                    {
                        doors.Add((Mall.Project(rails[c.k], r.At(xs[c.k], midT), out _), rails[c.k]));
                        doors.Add((Mall.Project(rails[c.k + 1], r.At(xs[c.k + 1], midT), out _), rails[c.k + 1]));
                    }
                    lotDoors.Add((held, doors));
                }
            }
            // The lots go in before the rows: their stubs join the lanes where the parking planner puts them, and the rows across
            // those lanes are then split at the real joins (a plot straddling a join is never zoned).
            uint lotSalt = 0xD90u;
            foreach (var (rect, doors) in lotDoors)
            {
                mall.Release(rect);
                var e = new List<(float2, string)>();
                foreach (var (at, lane) in doors) { Poly.ClosestPointOnBoundary(rect, at, out int seg, out float tt); e.Add((math.lerp(rect[seg], rect[(seg + 1) % rect.Length], tt), "alley")); }
                if (!mall.Lot(rect, e, Mall.Parking(ctx), lotSalt++)) mall.Hold(rect);   // planted after the rows (below)
                else lotsMade++;
            }

            // Plots: rails first (long, continuous), then the rungs; the streets last, a whole cell off every lane.
            // Street mouths keep a gateway the depth of the street's own plots (planted, a cell wide beside the lane).
            foreach (var lane in mall.Lanes.ToArray())
                if (lane.StartAttach >= 0)
                    mall.LaneRows(lane, inner, null, 0, mall.GateTrim(lane, true, o), mall.GateTrim(lane, false, o), 2);
            foreach (var lane in mall.Lanes.ToArray())
                if (lane.StartAttach < 0)
                    mall.LaneRows(lane, inner, null, 0, mall.GateTrim(lane, true, o), mall.GateTrim(lane, false, o), 2);
            if (outer > 0) mall.StreetRows(runs, outer, ctx.S("outerZone"), 3, 2);

            uint salt = 0xD10u;
            ctx.Plan.Facts["parkingBlocks"] = lotsMade;
            // The four corners of a crossing: a diagonal pair of café squares (fountain, seating), the other pair garden squares.
            for (int i = 0; i < squares.Count; i++)
            {
                var sq = squares[i];
                mall.Release(sq);
                string look = i % 4 == 0 || i % 4 == 3 ? "cafe" : "garden";
                if (!mall.FillOpen(sq, "plaza", look, mall.LaneDoors(sq, 6f), null, salt++, null, false)) mall.Green(sq, salt++);
            }
            foreach (var p in paseos) mall.DressPaseo(p.rect, p.a, p.b, salt += 4);
        }

        /// <summary>Every rail can reach a street at the back.</summary>
        private static bool Through(LayerContext ctx, Mall mall, StreetShops.Run r, List<float> xs)
        {
            foreach (var x in xs)
                if (!StreetShops.StreetEnd(ctx.Input, r.At(x, mall.ReachAt(r, x)), r.In, out _, out _, out _)) return false;
            return true;
        }

        private static (AssetRef, float) Lane(LayerContext ctx, out NetRole role)
        {
            role = NetRole.Alley;
            return Mall.ShopLane(ctx);
        }

        private static (AssetRef, float) Ped(LayerContext ctx, out NetRole role)
        {
            var p = Mall.PedLane(ctx, false);
            role = p.pedestrian ? NetRole.Street : NetRole.Alley;
            return (p.asset, p.half);
        }
    }
}
