// rf-shops3 — the strip mall as a designed centre, planned from the player's entrances (docs/refactor/SHOPS-ARCHITECT.md,
// "The architect's concepts"). One composition for every shape (a block, a block four times bigger, a long rectangle, an
// L, a circle, a slope, a site that touches no street):
//   • a RING DRIVE follows the site's outline (corners rounded), close to the main street (a planted frontage between),
//     a row depth in from the back and, on wide sites, the sides: the shops stand outside it, facing in, their backs to the
//     neighbours; on a round site they go most of the way round;
//   • inside, on deep sites, RUNGS across the ring carry rows of shops back to back (the user: "a giant strip mall with
//     multiple rows of buildings and a labyrinth of parking"); everything else inside is lot, entered from the ring;
//   • a PEDESTRIAN SPINE from the ring's front edge through the front lot to the first row of shops, ending in a pocket
//     plaza at the shopfront; pads (a restaurant, a bank) in the front corners; a retention pond (big box);
//   • nothing is joined to a street by the layout itself: every way in is an entrance the player placed (MallRoute).

using System.Collections.Generic;
using Placemaker.Core.Geometry;
using Placemaker.Core.Layers.Common;
using Unity.Mathematics;

namespace Placemaker.Core.Layers.Commercial
{
    public sealed partial class StripMallLayer
    {
        /// <summary>Plan fact: the centre composition was used (1), with its rows of shops and its rungs.</summary>
        public const string CentreFact = "stripCentre";

        /// <summary>
        /// The frame of the centre: the frontage run most of the player's car entrances are on, else the widest of the long
        /// runs; with no frontage at all, the first car entrance's own normal, else the site's long axis.
        /// </summary>
        internal static StreetShops.Run MainFrame(LayerContext ctx, Mall mall, List<StreetShops.Run> runs)
        {
            if (mall.Gates != null && runs.Count > 0)
            {
                int bestK = -1, bestN = 0;
                for (int k = 0; k < runs.Count; k++)
                {
                    int n = 0;
                    var r = runs[k];
                    foreach (var g in mall.Gates)
                    {
                        if (!g.Cars) continue;
                        float s = math.dot(g.At - r.A, r.Dir), t = math.dot(g.At - r.A, r.In);
                        if (s >= r.From - 4f && s <= r.To + 4f && math.abs(t) < 6f) n++;
                    }
                    if (n > bestN) { bestN = n; bestK = k; }
                }
                if (bestK >= 0) return runs[bestK];
            }
            if (runs.Count > 0) return MainStreet(ctx, runs);
            if (mall.Gates != null)
                foreach (var g in mall.Gates)
                    if (g.Cars) return new StreetShops.Run { A = g.At, In = g.In, Dir = new float2(g.In.y, -g.In.x), From = -50f, To = 50f };
            mall.Region.PrincipalAxes(out var c, out var major, out float majorSpan, out float minorSpan);
            var dir = math.normalizesafe(major);
            var inn = new float2(-dir.y, dir.x);
            return new StreetShops.Run { A = c - inn * minorSpan * 0.5f, Dir = dir, In = inn, From = -majorSpan * 0.5f, To = majorSpan * 0.5f };
        }

        private static bool Centre(LayerContext ctx, Mall mall, List<StreetShops.Run> runs)
        {
            var planMark = PlanMark.Of(ctx.Plan); var mallMark = mall.Mark();
            bool Fail(int step) { planMark.Restore(ctx.Plan); mall.Rewind(mallMark); ctx.Plan.Facts["centreFail"] = step; return false; }
            var r = MainFrame(ctx, mall, runs);
            mall.MainDir = r.Dir;
            var x = mall.X;
            var (asset, h) = Mall.ShopLane(ctx);
            int cells = Cells(ctx);
            float d = cells * Mall.Cell;
            bool bigBox = ctx.Raw("plotDepth") >= 44f;
            int maxPiece = bigBox ? 6 : 3;
            // Anchors at the ends of every row (a grocer, a pharmacy), inline shops between; big boxes are all big.
            mall.AnchorWeight = bigBox ? 1.25f : 1.8f;
            var ring = mall.Region.Ring;
            float sMin = float.MaxValue, sMax = float.MinValue, tMin = float.MaxValue, tMax = float.MinValue;
            foreach (var p in ring)
            {
                float s = math.dot(p - r.A, r.Dir), t = math.dot(p - r.A, r.In);
                sMin = math.min(sMin, s); sMax = math.max(sMax, s); tMin = math.min(tMin, t); tMax = math.max(tMax, t);
            }
            float width = sMax - sMin, depth = tMax - tMin;
            // The planted frontage between the main street and the ring's front drive.
            float band = x.Designed || x.Walkways ? 12f : 5f;
            float rowNeed = h + d + 9f;
            bool sideRows = width - 2f * rowNeed >= 64f;
            float frontInset = h + band;
            if (depth - rowNeed - frontInset < Mall.LotDepth(1) + 4f) return Fail(1);
            float RowsInset(float2 p, float2 nOut) => mall.RowInset(p, nOut, h, d);
            float InsetFor(float2 p, float2 nOut)
            {
                float f = math.dot(nOut, -r.In), b = math.dot(nOut, r.In);
                if (f > 0.55f) return frontInset;
                if (b > 0.55f) return RowsInset(p, nOut);
                return sideRows ? RowsInset(p, nOut) : h + 7f;
            }
            var line = mall.RingLineVar(InsetFor, 10f, 26f);
            if (line == null) return Fail(2);
            var loop = mall.RingLoop(line, NetRole.Alley, asset, h, r.At(0.5f * (sMin + sMax), tMax));
            if (loop == null) return Fail(3);
            float2 OutN(int i) { var dd = math.normalizesafe(loop.Points[i + 1] - loop.Points[i]); return new float2(dd.y, -dd.x); }
            bool Front(int i) => math.dot(OutN(i), -r.In) > 0.55f;
            bool RowSeg(int i)
            {
                if (math.distance(loop.Points[i], loop.Points[i + 1]) < 2f * Mall.Cell + 2f * h + 2f) return false;
                var n = OutN(i);
                if (math.dot(n, -r.In) > 0.55f) return false;
                if (math.dot(n, r.In) > 0.55f) return true;
                return sideRows;
            }

            // The ring's extent in the frame (its front and back drives).
            float tF = float.MaxValue, tB = float.MinValue, lsMin = float.MaxValue, lsMax = float.MinValue;
            foreach (var p in loop.Points)
            {
                float t = math.dot(p - r.A, r.In), s = math.dot(p - r.A, r.Dir);
                tF = math.min(tF, t); tB = math.max(tB, t); lsMin = math.min(lsMin, s); lsMax = math.max(lsMax, s);
            }
            float sMid = 0.5f * (lsMin + lsMax);
            ctx.Plan.Facts["ringDepth"] = tB - tF;

            // ── rungs across the ring on deep sites: rows of shops back to back, lots between ──
            float D = tB - tF - 2f * h;
            float lotMin = Mall.LotDepth(2);
            float pair = 4f * h + 2f * d;
            int nPairs = math.clamp((int)math.floor((D - lotMin) / (pair + lotMin)), 0, 6);
            var rungs = new List<(Mall.Lane lane, int side)>();
            if (nPairs > 0)
            {
                float L = (D - nPairs * pair) / (nPairs + 1);
                float t = tF + h + L;
                for (int k = 0; k < nPairs; k++)
                {
                    foreach (var (y, side) in new[] { (t + h, +1), (t + 3f * h + 2f * d, -1) })
                    {
                        foreach (float shift in new[] { 0f, 3f, -3f, 6f, -6f })
                        {
                            if (!Mall.Chord(loop.Points, r.At(sMid, y + shift), r.Dir, out var pa, out var pb)) continue;
                            if (math.distance(pa, pb) < 70f) continue;
                            var g = mall.Connect(Mall.End.OnLane(loop, pa), null, Mall.End.OnLane(loop, pb), NetRole.Alley, asset, h);
                            if (g != null) { rungs.Add((g, side)); break; }
                        }
                    }
                    t += pair + L;
                }
            }

            // ── the row bands, held while the lots are planned (their stubs then split the lanes before the rows go in) ──
            void HoldBand(Mall.Lane lane, int seg, int side)
            {
                var f = Mall.Frame(lane.Points[seg], lane.Points[seg + 1]);
                float a = side > 0 ? h : -(Mall.ZoneOffset(h) + d + 1f), b = side > 0 ? Mall.ZoneOffset(h) + d + 1f : -h;
                for (float trim = h + 0.5f; trim <= h + 16f; trim += 2f)
                {
                    if (f.To - 2f * trim < 8f) break;
                    if (mall.HoldRow(Mall.Rect(f, trim, f.To - trim, a, b)) != null) break;
                }
            }
            for (int i = 0; i + 1 < loop.Points.Count; i++) if (RowSeg(i)) HoldBand(loop, i, -1);
            foreach (var (lane, side) in rungs) HoldBand(lane, 0, side);

            // ── kept land: the frontage, the spine and its plaza, the pond ──
            uint salt = 0x3A00u;
            float tFront = tF - h - 0.4f;
            {
                // The frontage: the land between the main street and the ring's front drive, over the drive's front stretch.
                float fa = float.MaxValue, fb = float.MinValue;
                // (only the straight front: the corridors of the rounded corners reach past their ends)
                for (int i = 0; i + 1 < loop.Points.Count; i++)
                    if (math.dot(OutN(i), -r.In) > 0.92f)
                        foreach (var p in new[] { loop.Points[i], loop.Points[i + 1] }) { float s = math.dot(p - r.A, r.Dir); fa = math.min(fa, s); fb = math.max(fb, s); }
                fa += h + 1f; fb -= h + 1f;
                if (fb - fa >= 20f && tFront - tMin >= 4f)
                {
                    var rect = Mall.Rect(r, fa, fb, tMin - 2f, tFront);
                    float2[] piece = null;
                    try { piece = Mall.Largest(Poly.Intersection(mall.Region.Ring, rect), 60f); } catch (System.Exception) { piece = null; }
                    ctx.Plan.Facts["frontageTry"] = piece == null ? -1 : Poly.IsConvex(piece) ? 1 : 0;
                    if (piece != null && Poly.IsConvex(piece))
                    {
                        var k = mall.Keep(piece, Mall.KeepKind.Frontage, salt++);
                        ctx.Plan.Facts["frontageKept"] = k != null ? 1 : mall.Free(piece) ? 2 : 3;
                        if (k != null) { k.Frame = r; k.S0 = fa; k.S1 = fb; k.T0 = math.max(tMin, 0f); k.T1 = tFront; }
                    }
                }
            }
            // The spine: from the ring's front drive straight to the first row of shops (the first rung, else the ring's back),
            // aligned with a footpath entrance on the main street when the player placed one.
            float sSpine = sMid;
            if (mall.Gates != null)
                foreach (var g in mall.Gates)
                {
                    if (g.Kind != ShopEntrances.Footpath) continue;
                    float t = math.dot(g.At - r.A, r.In);
                    if (math.abs(t) < 8f) { sSpine = math.clamp(math.dot(g.At - r.A, r.Dir), lsMin + 30f, lsMax - 30f); break; }
                }
            if ((x.Walkways || x.Plaza) && Mall.Chord(loop.Points, r.At(sSpine, 0.5f * (tF + tB)), r.In, out var pF, out var pBk))
            {
                Mall.Lane target = loop; var end = pBk;
                foreach (var (lane, side) in rungs)
                {
                    if (side < 0) continue;
                    var q = Mall.Project(lane, r.At(sSpine, math.dot(lane.Points[0] - r.A, r.In)), out float dq);
                    if (dq < 0.5f && math.dot(q - r.A, r.In) < math.dot(end - r.A, r.In)) { target = lane; end = q; }
                }
                float t0 = math.dot(pF - r.A, r.In) + h + 0.3f, t1 = math.dot(end - r.A, r.In) - target.Half - 0.3f;
                ctx.Plan.Facts["spineLen"] = t1 - t0;
                if (t1 - t0 >= 22f)
                {
                    float plazaD = x.Plaza ? 11f : 0f;
                    Mall.Kept spine = null, plaza = null;
                    // (each end pulled back until it is clear of the ring's corridors: a crossing near a bend of the ring)
                    for (float pull = 0f; pull <= 6f && plaza == null && x.Plaza; pull += 1f) plaza = mall.KeepRect(r, sSpine - 10f, sSpine + 10f, t1 - plazaD - pull, t1 - pull, Mall.KeepKind.Plaza, salt);
                    salt++;
                    float tTop = plaza != null ? math.dot(Mall.Rect(r, 0f, 1f, 0f, 0f)[0] - r.A, r.In) : 0f;
                    float spineEnd = t1 - (plaza != null ? plazaD : 0f);
                    if (plaza != null) spineEnd = plaza.T0;
                    for (float pull = 0f; pull <= 6f && spine == null && x.Walkways; pull += 1f) spine = mall.KeepRect(r, sSpine - 4f, sSpine + 4f, t0 + pull, spineEnd, Mall.KeepKind.Spine, salt);
                    salt++;
                    ctx.Plan.Facts["spineKept"] = (spine != null ? 1 : 0) + (plaza != null ? 2 : 0);
                    if (spine != null || plaza != null)
                    {
                        var walk = mall.PathChain(new List<float2> { pF, end }, null, target, false, loop);
                        if (walk != null)
                        {
                            if (spine != null) spine.Walk.AddRange(walk);
                            mall.SpineFronts.Add((walk[0], loop));
                            ctx.Plan.Facts["spines"] = 1;
                        }
                    }
                    if (plaza != null) plaza.Facing = r.In;
                }
            }
            // Pads in the front corners of the ring (a restaurant, a bank by the road); a pond takes one corner on a big box.
            var pads = new List<(float2[] rect, StreetShops.Run f)>();
            {
                int front = -1; float flen = 0f;
                for (int i = 0; i + 1 < loop.Points.Count; i++)
                    if (Front(i)) { float l = math.distance(loop.Points[i], loop.Points[i + 1]); if (l > flen) { flen = l; front = i; } }
                if (front >= 0 && (x.Pads || x.Pond) && tB - tF > 3f * Mall.Cell + 30f)
                {
                    var f = Mall.Frame(loop.Points[front], loop.Points[front + 1]);
                    float pw = 3f * Mall.Cell, pd = 3f * Mall.Cell;
                    bool pond = x.Pond && bigBox;
                    // Corner A (start of the front drive): pad; corner B: pond on a big box, else a second pad on a long front.
                    if (x.Pads && flen >= 70f)
                    {
                        var rect = Mall.Rect(f, h + 8f, h + 8f + pw, h + 0.5f, h + 0.5f + pd);
                        var held = mall.HoldRow(rect);
                        ctx.Plan.Facts["padHold"] = (ctx.Plan.Facts.TryGetValue("padHold", out var ph) ? ph : 0) + (held != null ? 1 : 100);
                        if (held != null) pads.Add((held, f));
                    }
                    if (pond && flen >= 80f && tB - tF > 0f)
                    {
                        // As big as the front lot allows (between the front drive and the first rung, a third of the front at most).
                        float frontLot = tB - tF;
                        foreach (var (lane, side) in rungs) frontLot = math.min(frontLot, math.dot(lane.Points[0] - r.A, r.In) - tF);
                        float hullR = math.min(0.5f * (frontLot - 2f * h - 3f), 0.17f * flen);
                        float R = math.min(20f, (hullR - 3f) / 1.32f);
                        hullR = R * 1.32f + 3f;
                        if (R >= 8f)
                        {
                            var c = f.At(f.To - h - 6f - hullR, h + 1.5f + hullR);
                            var blob = LeisureKit.Blob(c, R, Hashing.Mix(ctx.Seed, 0xB0Bu), 14, 0.22f);
                            var hull = LayerRegion.Circle(c, hullR, 10);
                            var k = mall.Keep(hull, Mall.KeepKind.Pond, salt++);
                            if (k != null) { k.Inner = blob; ctx.Plan.Facts["ponds"] = 1; } else ctx.Plan.Facts["pondFail"] = StreetShops.Fits(mall.Region, hull) ? 2 : 1;
                        }
                    }
                    else if (x.Pads && flen >= 90f)
                    {
                        var rect = Mall.Rect(f, f.To - h - 8f - pw, f.To - h - 8f, h + 0.5f, h + 0.5f + pd);
                        var held = mall.HoldRow(rect);
                        ctx.Plan.Facts["padHold"] = (ctx.Plan.Facts.TryGetValue("padHold", out var ph) ? ph : 0) + (held != null ? 1 : 100);
                        if (held != null) pads.Add((held, f));
                    }
                }
            }

            // Planted islands from the ring's inner corners (an L): the lot inside splits into tidy rectangles, a tree row between.
            Islands(mall, line, r, h, salt + 0x40u);

            // ── the player's entrances: drives, alleys and walks into this network ──
            mall.RouteGates();
            ctx.Check();

            // ── the lots: everything free inside and round the ring, entered from it ──
            mall.LotBudget = float.MaxValue;
            RestLots(ctx, mall, 0xA90u, 400f, false, false);
            ctx.Check();

            // ── the shops: round the ring (outside, facing in), on the rungs, the pads ──
            var padRects = new List<float2[]>();
            foreach (var (rect, _) in pads) padRects.Add(rect);
            mall.ReleaseRows();
            int rows = 0;
            rows += mall.LaneRows(loop, cells, null, -1, 0f, 0f, maxPiece, 2, RowSeg) > 0 ? 1 : 0;
            foreach (var (lane, side) in rungs) rows += mall.LaneRows(lane, cells, null, side, 0f, 0f, maxPiece, 2) > 0 ? 1 : 0;
            int padsMade = 0;
            foreach (var (rect, f) in pads)
                if (mall.Row(f, math.dot(rect[0] - f.A, f.Dir) < math.dot(rect[1] - f.A, f.Dir) ? Min(rect, f) : Min(rect, f), Max(rect, f), Mall.ZoneOffset(loop), 3, 1f, null, 3, 2) > 0) padsMade++;
            if (mall.Shops.Count == 0) return Fail(9);
            // the back corners outside the ring: corner courts (the front ones keep the frontage, the pads and the pond)
            mall.CornerCourts(loop, Mall.Parking(ctx), 0xAC0u, i => Front(i));
            ctx.Plan.Facts[CentreFact] = 1;
            ctx.Plan.Facts[RowsFact] = rows;
            ctx.Plan.Facts["stripRungs"] = rungs.Count;
            ctx.Plan.Facts["pads"] = padsMade;
            ctx.Plan.Facts["sideRows"] = sideRows ? 1 : 0;
            return true;
        }

        /// <summary>
        /// At every inner (reflex) corner of the ring, a 6 m planted island across the inside to the opposite side of the ring
        /// (the shorter way, square to the frame), so the lot inside is two tidy rectangles instead of one L that no lot fits.
        /// </summary>
        internal static int Islands(Mall mall, float2[] line, StreetShops.Run r, float h, uint salt)
        {
            int made = 0, n = line.Length;
            for (int i = 0; i < n; i++)
            {
                var p = line[(i + n - 1) % n]; var v = line[i]; var q = line[(i + 1) % n];
                if (Poly.Cross(v - p, q - v) >= -1e-3f) continue;   // convex (CCW)
                float best = float.MaxValue; float2 bu = default, be = default;
                foreach (var u in new[] { r.Dir, -r.Dir, r.In, -r.In })
                {
                    var start = v + u * (h + 0.6f);
                    if (!Poly.Contains(line, start)) continue;
                    var far = start + u * 600f;
                    float tmin = float.MaxValue; float2 hitP = start;
                    for (int k = 0; k < n; k++)
                        if (Poly.SegmentIntersection(start, far, line[k], line[(k + 1) % n], out var hp, out float tt, out _) && tt * 600f > 0.5f && tt * 600f < tmin) { tmin = tt * 600f; hitP = hp; }
                    if (tmin < 12f || tmin > 400f) continue;
                    if (tmin < best) { best = tmin; bu = u; be = hitP; }
                }
                if (best == float.MaxValue) continue;
                var a = v + bu * (h + 0.3f); var b = be - bu * (h + 0.3f);
                var nrm = new float2(-bu.y, bu.x);
                var rect = Poly.EnsureCcw(new[] { a - nrm * 3f, b - nrm * 3f, b + nrm * 3f, a + nrm * 3f });
                if (mall.Keep(rect, Mall.KeepKind.Island, salt + (uint)made) != null) made++;
            }
            return made;
        }

        private static float Min(float2[] rect, StreetShops.Run f) { float m = float.MaxValue; foreach (var v in rect) m = math.min(m, math.dot(v - f.A, f.Dir)); return m; }
        private static float Max(float2[] rect, StreetShops.Run f) { float m = float.MinValue; foreach (var v in rect) m = math.max(m, math.dot(v - f.A, f.Dir)); return m; }
    }
}
