// rf-shops3 — the mall district planned from the player's entrances: a ring drive follows the site (rounded corners, round
// round a circle, an L round an L), shops on both sides of it (big rows backing onto the streets, small rows facing in), and
// inside it the grid of lanes: rails front to back, rungs across, rows of shops on both sides of every lane with a planted
// courtyard between the backs, a square at the central crossing (two café corners, two garden corners), parking courts mixed
// in on Downtown. Nothing joins a street but the player's entrances (drives and driveways into the ring, walks).

using System.Collections.Generic;
using Placemaker.Core.Geometry;
using Placemaker.Core.Layers.Common;
using Unity.Mathematics;

namespace Placemaker.Core.Layers.Commercial
{
    public sealed partial class MallDistrictLayer
    {
        public const string RingFact = "districtRing";

        protected override bool UsesEntrances => true;

        // (Rows of the asked depth, and two-cell rows: the loop moves out and grows. On a small block the deep rows leave a ring
        // too short for rows either side of the entrances' ways; the plan with more shop ground wins, the asked depth on a tie.)
        protected override bool ComposeFromEntrances(LayerContext ctx, Mall mall, List<StreetShops.Run> runs)
        {
            if (Cells(ctx) <= 2) return RingDistrict(ctx, mall, runs, 99);
            var planMark = PlanMark.Of(ctx.Plan); var mallMark = mall.Mark();
            float deep = RingDistrict(ctx, mall, runs, 99) ? mall.ShopArea() : 0f;
            planMark.Restore(ctx.Plan); mall.Rewind(mallMark); mall.ResetDesign();
            float shallow = RingDistrict(ctx, mall, runs, 2) ? mall.ShopArea() : 0f;
            if (shallow > 0f && shallow >= 1.1f * deep) return true;
            planMark.Restore(ctx.Plan); mall.Rewind(mallMark); mall.ResetDesign();
            return deep > 0f && RingDistrict(ctx, mall, runs, 99);
        }

        private static bool RingDistrict(LayerContext ctx, Mall mall, List<StreetShops.Run> runs, int cellsCap)
        {
            var planMark = PlanMark.Of(ctx.Plan); var mallMark = mall.Mark();
            bool Fail(int step) { planMark.Restore(ctx.Plan); mall.Rewind(mallMark); ctx.Plan.Facts["districtRingFail"] = step; return false; }
            var r = StripMallLayer.MainFrame(ctx, mall, runs);
            mall.MainDir = r.Dir;
            string kinds = ctx.S("lanes");
            var (railAsset, hr) = kinds == "pedestrian" ? Ped(ctx, out var railRole) : Lane(ctx, out railRole);
            var (rungAsset, hp) = kinds == "alley" ? Lane(ctx, out var rungRole) : Ped(ctx, out rungRole);
            var (loopAsset, hl) = Mall.ShopLane(ctx);
            int inner = math.max(2, math.min(Cells(ctx), cellsCap));
            float d = inner * Mall.Cell;
            float court = ctx.S("blocks") == "compact" ? 0f : ctx.S("blocks") == "spacious" ? 32f : 16f;
            court = math.max(court, mall.SlopeGap());
            mall.AnchorWeight = 1.4f;

            float InsetFor(float2 p, float2 nOut) => mall.RowInset(p, nOut, hl, d);
            var line = mall.RingLineVar(InsetFor, 7f, 26f);
            if (line == null) return Fail(1);
            var loop = mall.RingLoop(line, NetRole.Alley, loopAsset, hl, r.At(0f, 0f) + r.In * 1e5f);
            if (loop == null) return Fail(2);
            float lsMin = float.MaxValue, lsMax = float.MinValue, tF = float.MaxValue, tB = float.MinValue;
            foreach (var p in loop.Points)
            {
                float s = math.dot(p - r.A, r.Dir), t = math.dot(p - r.A, r.In);
                lsMin = math.min(lsMin, s); lsMax = math.max(lsMax, s); tF = math.min(tF, t); tB = math.max(tB, t);
            }

            // Rails (front to back) and rung levels (across), a block of two rows back to back and a courtyard apart.
            float railStep = 2f * d + 2f * hr + court, rungStep = 2f * d + 2f * hp + court;
            float x0 = lsMin + hl + 2f * d + court + hr, x1 = lsMax - hl - 2f * d - court - hr;
            float y0 = tF + hl + 2f * d + court + hp, y1 = tB - hl - 2f * d - court - hp;
            ctx.Plan.Facts["districtX"] = x1 - x0; ctx.Plan.Facts["districtY"] = y1 - y0;
            var xs = new List<float>();
            if (x1 - x0 >= -0.5f)
            {
                int n = math.max(0, (int)math.round((x1 - x0) / railStep));
                for (int k = 0; k <= n; k++) xs.Add(n == 0 ? 0.5f * (x0 + x1) : math.lerp(x0, x1, (float)k / n));
            }
            var ys = new List<float>();
            if (y1 - y0 >= -0.5f)
            {
                int m = math.max(0, (int)math.round((y1 - y0) / rungStep));
                for (int j = 0; j <= m; j++) ys.Add(m == 0 ? 0.5f * (y0 + y1) : math.lerp(y0, y1, (float)j / m));
            }
            // Rails: chords of the ring along the frame's In, joined to it at both ends.
            var rails = new List<(Mall.Lane lane, float x)>();
            float tMidAll = 0.5f * (tF + tB);
            foreach (var x0r in xs)
            {
                // (a chord meeting the ring right at one of its bends is moved a few metres: the bend's corridor is in the way)
                foreach (float shift in new[] { 0f, 4f, -4f, 8f, -8f })
                {
                    float x = x0r + shift;
                    if (!Mall.Chord(loop.Points, r.At(x, tMidAll), r.In, out var pa, out var pb)) continue;
                    if (math.distance(pa, pb) < 2f * d + 20f) continue;
                    var rail = mall.Connect(Mall.End.OnLane(loop, pa), null, Mall.End.OnLane(loop, pb), railRole, railAsset, hr);
                    if (rail != null) { rails.Add((rail, x)); break; }
                }
            }
            // Rungs: at each level, pieces between neighbouring lanes (the ring, the rails, the ring again).
            var rungs = new List<(Mall.Lane lane, float y, int k)>();
            foreach (var y in ys)
            {
                if (!Mall.Chord(loop.Points, r.At(0.5f * (lsMin + lsMax), y), r.Dir, out var pl, out var pr)) continue;
                var stops = new List<(float2 at, Mall.Lane on)> { (pl, loop) };
                foreach (var (rail, x) in rails)
                {
                    var q = Mall.Project(rail, r.At(x, y), out float dq);
                    if (dq < 0.5f) stops.Add((q, rail));
                }
                stops.Add((pr, loop));
                for (int k = 0; k + 1 < stops.Count; k++)
                {
                    if (math.distance(stops[k].at, stops[k + 1].at) < 2f * Mall.Cell + 2f * hr + 6f) continue;
                    var g = mall.Connect(Mall.End.OnLane(stops[k].on, stops[k].at), null, Mall.End.OnLane(stops[k + 1].on, stops[k + 1].at), rungRole, rungAsset, hp);
                    if (g != null) rungs.Add((g, y, k));
                }
            }

            // Paseos: from each rung a walk through the row into the courtyard garden behind it (a door in the middle of the block).
            var paseos = new List<(float2[] rect, float2 a, float2 b)>();
            if (court >= 12f)
                foreach (var (g, y, k) in rungs)
                {
                    float len = math.distance(g.Points[0], g.Points[g.Points.Count - 1]);
                    if (len < 2f * Mall.PaseoWidth + 4f * Mall.Cell) continue;
                    var f = Mall.Frame(g.Points[0], g.Points[g.Points.Count - 1]);
                    float mid = 0.5f * f.To;
                    // the courtyard lies on the deeper side (frame In of a rung running along r.Dir is r.In)
                    float sgn = math.dot(f.In, r.In) >= 0f ? 1f : -1f;
                    var rect = Mall.Rect(f, mid - Mall.PaseoWidth * 0.5f, mid + Mall.PaseoWidth * 0.5f, sgn * hp, sgn * (hp + d));
                    if (!mall.Fits(rect)) continue;
                    var end = f.At(mid, sgn * (hp + d + 0.6f));
                    if (!mall.Walk(f.At(mid, 0f), g, end, null)) continue;
                    paseos.Add((mall.Hold(rect), f.At(mid, sgn * hp), f.At(mid, sgn * (hp + d))));
                    mall.Doors.Add(end);
                }

            // The square: the four corners of the crossing nearest the middle.
            var squares = new List<float2[]>();
            if (ctx.B("squares") && rails.Count > 0 && ys.Count > 0)
            {
                float cs = 0.5f * (lsMin + lsMax), ct = 0.5f * (tF + tB);
                float bx = rails[0].x, by = ys[0];
                foreach (var (_, x) in rails) if (math.abs(x - cs) < math.abs(bx - cs)) bx = x;
                foreach (var y in ys) if (math.abs(y - ct) < math.abs(by - ct)) by = y;
                float q = math.min(14f, d + 2f);
                for (int sx = -1; sx <= 1; sx += 2)
                    for (int sy = -1; sy <= 1; sy += 2)
                    {
                        float a0 = bx + sx * hr, a1 = bx + sx * (hr + q), b0 = by + sy * hp, b1 = by + sy * (hp + q);
                        var sq = Mall.Rect(r, math.min(a0, a1), math.max(a0, a1), math.min(b0, b1), math.max(b0, b1));
                        if (mall.Fits(sq)) squares.Add(mall.Hold(sq));
                    }
            }

            // Parking courts (Downtown: "more parking, intermingled"): some blocks between two rails and two rungs are lots.
            int lotsMade = 0;
            if (ctx.S("parkingBlocks") != "none" && kinds != "pedestrian" && rails.Count >= 2 && ys.Count >= 2)
            {
                bool many = ctx.S("parkingBlocks") == "many";
                int c = 0;
                for (int j = 0; j + 1 < ys.Count; j++)
                    for (int k = 0; k + 1 < rails.Count; k++)
                    {
                        if ((k + 2 * j) % (many ? 2 : 3) != 1) continue;
                        float a0 = rails[k].x + hr, a1 = rails[k + 1].x - hr, b0 = ys[j] + hp, b1 = ys[j + 1] - hp;
                        if (b1 - b0 < Mall.LotDepth(1) + 2f || a1 - a0 < 30f) continue;
                        var rect = Mall.Rect(r, a0, a1, b0, b1);
                        if (!mall.Fits(rect)) continue;
                        float midT = 0.5f * (b0 + b1);
                        var e = new List<(float2, string)>
                        {
                            (r.At(a0, midT), "alley"), (r.At(a1, midT), "alley"),
                        };
                        if (mall.Lot(rect, e, Mall.Parking(ctx), 0xD90u + (uint)c++)) lotsMade++;
                    }
            }

            // The rows on the ring's outside are held (an entrance cuts its way through them); then the entrances.
            for (int i = 0; i + 1 < loop.Points.Count; i++)
            {
                var f = Mall.Frame(loop.Points[i], loop.Points[i + 1]);
                for (float trim = hl + 0.5f; trim <= hl + 16f; trim += 2f)
                {
                    if (f.To - 2f * trim < 8f) break;
                    if (mall.HoldRow(Mall.Rect(f, trim, f.To - trim, -hl, -(Mall.ZoneOffset(hl) + d + 1f))) != null) break;
                }
            }
            mall.RouteGates();
            mall.ReleaseRows();
            ctx.Check();

            // Rows: the ring both sides, the rails, the rungs.
            int rows = mall.LaneRows(loop, inner, null, 0, 0f, 0f, 3, 2);
            foreach (var (rail, _) in rails) rows += mall.LaneRows(rail, inner, null, 0, 0f, 0f, 3, 2);
            foreach (var (g, _, _) in rungs) rows += mall.LaneRows(g, inner, null, 0, 0f, 0f, 3, 2);
            if (kinds != "pedestrian") mall.CornerCourts(loop, Mall.Parking(ctx), 0xDC0u);
            if (mall.Shops.Count == 0) return Fail(9);

            // The square's corners: a diagonal pair of café squares, the other pair gardens.
            uint salt = 0xD10u;
            for (int i = 0; i < squares.Count; i++)
            {
                var sq = squares[i];
                mall.Release(sq);
                string look = i % 4 == 0 || i % 4 == 3 ? "cafe" : "garden";
                if (!mall.FillOpen(sq, "plaza", look, mall.LaneDoors(sq, 6f), null, salt++, null, false)) mall.Green(sq, salt++);
                else if (mall.X.Trucks > 0 && look == "cafe") mall.FoodCarts(Poly.Centroid(sq), r.Dir, 1, 4f, r.In, salt++);
            }
            foreach (var p in paseos) { mall.Release(p.rect); mall.DressPaseo(p.rect, p.a, p.b, salt += 4); mall.Taken.Add(p.rect); }
            // A ring too small for lanes inside (a normal block): the middle, inside the inner rows, is the quarter's square (café
            // tables, a fountain or a tree ring, food trucks), not a lawn; walks come in between the rows from the ring.
            // Downtown ("more parking, intermingled") makes it a parking court entered from the ring between the rows.
            if (rails.Count == 0 && rungs.Count == 0 && (ctx.B("squares") || ctx.S("parkingBlocks") != "none"))
            {
                var mid = Poly.Centroid(loop.Points.ToArray());
                foreach (var piece in mall.RestPieces(250f))
                {
                    if (!Poly.Contains(piece, mid)) continue;
                    var doors = mall.LaneDoors(piece, 4f);
                    if (ctx.S("parkingBlocks") != "none" && kinds != "pedestrian" && doors.Count > 0)
                    {
                        var e = new List<(float2, string)>();
                        foreach (var (_, at) in doors) { e.Add((at, "alley")); if (e.Count >= 2) break; }
                        if (mall.CleanLot(piece, e, Mall.Parking(ctx), salt++)) { ctx.Plan.Facts["districtCourt"] = 1; break; }
                    }
                    if (!ctx.B("squares")) break;
                    if (mall.FillOpen(piece, "plaza", "cafe", doors, null, salt++, null, false))
                    {
                        ctx.Plan.Facts["districtSquare"] = 1;
                        if (mall.X.Trucks > 0) mall.FoodCarts(Poly.Centroid(piece), r.Dir, math.min(2, mall.X.Trucks), 4f, r.In, salt++);
                    }
                    break;
                }
            }
            ctx.Plan.Facts["paseos"] = paseos.Count;
            ctx.Plan.Facts[RingFact] = 1;
            ctx.Plan.Facts["districtRails"] = rails.Count;
            ctx.Plan.Facts["districtRungs"] = rungs.Count;
            ctx.Plan.Facts["parkingBlocks"] = lotsMade;
            return true;
        }
    }
}
