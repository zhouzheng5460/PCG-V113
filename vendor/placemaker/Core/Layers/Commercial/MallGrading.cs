// s25 — a commercial site at grade.
//
// Playtest (strip mall on sloped ground, screenshot 41): white mounds where the new lanes meet the lot and at its
// corners; on the far side of the lane the shop plots became pits with retaining walls, dirt floors and white fences.
// Causes (decompiled 1.6.2f1 behaviour, docs/proposals/s25.md):
//   * A CS2 road is level across its width. A lane laid on a cross-slope cuts one side and fills the other and banks
//     the ground beside it — into the lot surface: the white cones (the lot's light paving drapes over them).
//   * A zoned building levels its lot to the height of the road in front. On ground rising away from the lane the
//     construction site digs a pit with retaining walls; the white fences are the construction-site fences. No
//     Placemaker fence or terraform made them — the plots were simply not at the road's grade.
// Grading here (one Sculpt over the site, after the sub-plans):
//   * lanes: a smooth longitudinal profile (≤ 8 %), pinned to the street at attached ends and to the graded lot edge
//     where they run along a lot; level across their width (+1.5 m shoulders);
//   * shop plots: level at the height of the road they face, measured along the frontage (a building finds its
//     road's grade everywhere on the plot, so nothing digs in);
//   * everything else (pocket parks, buffers) only banks to those at ≤ 1 : 3 — no other change, no mounds;
//   * lots keep the Parking planner's own grading (level tiers), the sculpt treats them as fixed ground.
// On ground that already matches (flat sites, roads following the terrain) nothing is emitted.

using System;
using System.Collections.Generic;
using Placemaker.Core.Geometry;
using Placemaker.Core.Layers.Common;
using Unity.Mathematics;

namespace Placemaker.Core.Layers.Commercial
{
    public static class MallGrading
    {
        public const float LaneMaxGrade = 0.08f;
        public const float MaxBank = 0.33f;
        public const float Cell = 2f;
        /// <summary>Shoulder beside a lane kept level with it (m).</summary>
        public const float Shoulder = Cell;
        /// <summary>(s41) How far past the street's edge a lane's approach is graded (m), whatever the outline does.</summary>
        public const float ApproachDepth = 14f;
        /// <summary>(rf-shops3) Half width of a drive from the kerb (its asphalt strip, MallRoute.Drive).</summary>
        public const float DriveHalf = 3.6f;

        public static void Grade(Mall mall, List<(float2[] ring, Func<float2, float> height)> lots)
        {
            var ctx = mall.Ctx;
            var region = mall.Region;
            if (region == null) return;
            lots = lots ?? new List<(float2[], Func<float2, float>)>();
            float LotAt(float2 p, out bool inLot)
            {
                foreach (var (r, h) in lots) if (Poly.Contains(r, p)) { inLot = true; return h(p); }
                inLot = false;
                return float.NaN;
            }
            Func<float2, float> g0 = p => { float v = LotAt(p, out bool inside); return inside ? v : ctx.H(p); };
            foreach (var (r, h) in lots)
            {
                float2 mn = float.MaxValue, mx = float.MinValue;
                foreach (var v in r) { mn = math.min(mn, v); mx = math.max(mx, v); }
                var ring = r;
                ctx.Ground.Override(mn, mx, h, p => Poly.Contains(ring, p));
            }
            var shrunk = new List<float2[]>();
            foreach (var (r, _) in lots) shrunk.Add(Mall.Shrink(r, 0.6f));
            bool InLot(float2 p) { foreach (var r in shrunk) if (Poly.Contains(r, p)) return true; return false; }

            var approaches = Approaches(mall);
            float2 smn = region.Min, smx = region.Max;
            foreach (var q in approaches) foreach (var v in q) { smn = math.min(smn, v); smx = math.max(smx, v); }
            bool InApproach(float2 p) { foreach (var q in approaches) if (Poly.Contains(q, p)) return true; return false; }
            var sc = new Sculpt(g0, smn, smx, Cell, p => (region.Contains(p) || InApproach(p)) && !InLot(p));
            var prot = new bool[sc.Count];
            ctx.Check();

            // ── fixed levels a lane must meet beside it: the graded lots and the plots that face a street ──
            var roads = StreetFrontage.Roads(ctx.Input);
            var fixedLevels = new List<(float2[] ring, Func<float2, float> height)>(lots);
            foreach (var shop in mall.Shops)
            {
                if (!Facing(shop, roads, mall.Lanes, out var fa, out var fb, out var fl) || fl != null) continue;
                var dir0 = math.normalizesafe(fb - fa); float len0 = math.distance(fa, fb); var a0 = fa;
                fixedLevels.Add((shop, q => ctx.H(a0 + dir0 * math.clamp(math.dot(q - a0, dir0), 0f, len0))));
            }

            // ── lane profiles (plots read them; lanes are written last and win over plot margins) ──
            var profiles = new Dictionary<Mall.Lane, Profile>();
            // (s32) A lane ending on another lane (T / X junction) meets it at its height: hosts first.
            var pending = new List<Mall.Lane>(mall.Lanes);
            for (int pass = 0; pass < 6 && pending.Count > 0; pass++)
                for (int i = 0; i < pending.Count; i++)
                {
                    var lane = pending[i];
                    bool hostsReady = (lane.StartOn == null || profiles.ContainsKey(lane.StartOn)) && (lane.EndOn == null || profiles.ContainsKey(lane.EndOn));
                    if (lane.Points.Count >= 2 && !hostsReady && pass < 5) continue;
                    pending.RemoveAt(i--);
                    if (lane.Points.Count < 2) continue;
                    var pr = Profile.Of(lane, ctx, g0, fixedLevels, profiles);
                    profiles[lane] = pr;
                    mall.LaneProfiles[lane] = pr.H;
                }

            // ── shop plots: level at the grade of the road they face ──
            // Grown by a whole cell so every sculpt cell touching the plot is level (the bank starts behind it).
            // Margins first (a cell round each plot; where two meet the lower wins), then the plots themselves.
            var plotCore = new bool[sc.Count];
            var plotLane = new Mall.Lane[sc.Count];
            for (int stage = 0; stage < 2; stage++)
                foreach (var shop in mall.Shops)
                {
                    if (!Facing(shop, roads, mall.Lanes, out var a, out var b, out var lane)) continue;
                    Func<float2, float> road = lane != null && profiles.TryGetValue(lane, out var pr)
                        ? (Func<float2, float>)(q => { pr.Distance(q, out float s); return pr.At(s); })
                        : (q => ctx.H(q));
                    var area = Grow(shop, stage == 0 ? Cell + 0.25f : 0.25f);
                    var dir = math.normalizesafe(b - a);
                    float len = math.distance(a, b);
                    // (s32) Only the nodes in the plot's bounds (same order: a district has hundreds of plots).
                    foreach (int k in Nodes(sc, area, 0f))
                    {
                        if (!sc.Free[k]) continue;
                        var p = sc.Pos(k);
                        if (!Poly.Contains(area, p)) continue;
                        float t = math.clamp(math.dot(p - a, dir), 0f, len);
                        float h = road(a + dir * t);
                        sc.Target[k] = stage == 0 && prot[k] ? math.min(sc.Target[k], h) : h;
                        prot[k] = true;
                        if (stage == 1) { plotCore[k] = true; plotLane[k] = lane; }
                    }
                }

            // ── lanes: level across their width and a cell beyond ──
            var laneSet = new bool[sc.Count];
            var laneDist = new float[sc.Count];
            foreach (var kv in profiles)
            {
                var lane = kv.Key; var pr = kv.Value;
                // (s32) A lane may ask for a wider level shoulder (lanes through planted gateways and gaps).
                float reach = lane.Half + (lane.Shoulder >= 0f ? lane.Shoulder : Shoulder);
                foreach (int k in Nodes(sc, lane.Points, reach + 1f))
                {
                    if (!sc.Free[k]) continue;
                    var p = sc.Pos(k);
                    float dist = pr.Distance(p, out float s);
                    // A plot keeps its own level up to the lane's edge (a plot between a street and a lane faces the street).
                    // (s32) A lane with its own shoulder also levels a cell of a plot that faces another of the lanes (a corner plot
                    // beside a junction): the step between the two grades falls in the plot's side margin, not into the lane.
                    float keep = plotCore[k] ? (lane.Shoulder >= 0f && plotLane[k] != null && plotLane[k] != lane ? lane.Half + Cell : lane.Half + 0.25f) : reach;
                    if (dist > keep) continue;
                    float h = pr.At(s);
                    // (s32) Lanes with their own shoulder: the nearest lane sets a node (at a T or X junction the shoulder of the
                    // crossing lane must not lay a level platform across a sloping one).
                    if (laneSet[k] && lane.Shoulder >= 0f) { if (dist >= laneDist[k]) continue; sc.Target[k] = h; }
                    else sc.Target[k] = laneSet[k] ? 0.5f * (sc.Target[k] + h) : h;
                    laneDist[k] = laneSet[k] ? math.min(laneDist[k], dist) : dist;
                    prot[k] = laneSet[k] = true;
                }
            }

            // ── (rf-shops3) drives from the kerb (the player's car entrances): one straight ramp from the street's height at the
            // kerb to the lane's at its node, level across the drive and a shoulder (the lane's own surface stays the lane's) ──
            foreach (var (kerb, at, street, st, lane) in mall.KerbDrives)
            {
                float len = math.distance(kerb, at);
                if (len < 1f) continue;
                var d = (at - kerb) / len; var nrm = new float2(-d.y, d.x);
                float hs = StreetHeight(ctx, street, st, kerb);
                float he = g0(at);
                if (lane != null && profiles.TryGetValue(lane, out var lp)) { lp.Distance(at, out float sa); he = lp.At(sa); }
                float laneHalf = lane?.Half ?? 0f, run = math.max(1f, len - laneHalf);
                float reach = DriveHalf + Shoulder;
                foreach (int k in Nodes(sc, new[] { kerb, at }, reach + 3f))
                {
                    if (!sc.Free[k]) continue;
                    var p = sc.Pos(k);
                    float s = math.dot(p - kerb, d), off = math.abs(math.dot(p - kerb, nrm));
                    if (s < -2.5f || s > run || off > reach) continue;
                    if (laneSet[k] && laneDist[k] <= laneHalf + 0.25f) continue;
                    sc.Target[k] = math.lerp(hs, he, math.saturate(s / run));
                    prot[k] = true;
                }
            }

            Bank(sc, prot, MaxBank);
            ctx.Check();
            var clips = new List<float2[]> { region.Ring };
            clips.AddRange(approaches);
            var res = sc.Emit(ctx, clips, mall.GradeTolerance);
            ctx.Plan.Facts["gradeCells"] = res.Cells;
            if (res.Cells > 0) Sculpt.Resnap(ctx);
        }

        /// <summary>
        /// (s41) Height of an existing street at a lane's attachment: the street curve (NearbyNet A–D) at the attach parameter,
        /// the ground there when the street is unknown.
        /// </summary>
        public static float StreetHeight(LayerContext ctx, long id, float t, float2 at)
        {
            var nets = ctx.Site?.NearbyNets;
            if (nets != null)
                foreach (var n in nets)
                    if (n.ExternalId == id)
                    {
                        float y = Composer.Bezier(n.A, n.B, n.C, n.D, math.saturate(t)).y;
                        if (math.isfinite(y)) return y;
                    }
            return ctx.H(at);
        }

        /// <summary>Half width of the existing street a lane attaches to (0 when unknown).</summary>
        public static float StreetHalf(LayerContext ctx, long id)
        {
            var nets = ctx.Site?.NearbyNets;
            if (nets != null) foreach (var n in nets) if (n.ExternalId == id) return math.max(0f, n.Width * 0.5f);
            return 0f;
        }

        /// <summary>
        /// (s41) The approach of a lane end attached to a street: from the street's edge into the site, the lane's width plus
        /// its shoulders. The site outline usually lies a few metres off the kerb; the sculpt must reach the street there, or
        /// the strip between outline and kerb stays at the raw ground between a lane and a street both at the street's level.
        /// </summary>
        public static List<float2[]> Approaches(Mall mall)
        {
            var list = new List<float2[]>();
            var ctx = mall.Ctx;
            foreach (var lane in mall.Lanes)
            {
                if (lane.Points.Count < 2) continue;
                for (int end = 0; end < 2; end++)
                {
                    long id = end == 0 ? lane.StartAttach : lane.EndAttach;
                    if (id < 0) continue;
                    var p0 = end == 0 ? lane.Points[0] : lane.Points[lane.Points.Count - 1];
                    var p1 = end == 0 ? lane.Points[1] : lane.Points[lane.Points.Count - 2];
                    var d = math.normalizesafe(p1 - p0);
                    var nrm = new float2(-d.y, d.x);
                    float edge = StreetHalf(ctx, id), len = math.min(math.distance(p0, p1), edge + ApproachDepth);
                    float w = lane.Half + (lane.Shoulder >= 0f ? lane.Shoulder : Shoulder);
                    if (len <= edge + 0.5f) continue;
                    // From 2.5 m inside the street: the node rows round the kerb line must be graded too (2 m grid, 1.75 m raster).
                    var a = p0 + d * math.max(0f, edge - 2.5f); var b = p0 + d * len;
                    list.Add(Poly.EnsureCcw(new[] { a - nrm * w, b - nrm * w, b + nrm * w, a + nrm * w }));
                }
            }
            // (rf-shops3) The drives from the kerb, from 2.5 m inside the street to the lane.
            foreach (var (kerb, at, _, _, _) in mall.KerbDrives)
            {
                float len = math.distance(kerb, at);
                if (len < 1f) continue;
                var d = (at - kerb) / len; var nrm = new float2(-d.y, d.x);
                float w = DriveHalf + Shoulder;
                var a = kerb - d * 2.5f;
                list.Add(Poly.EnsureCcw(new[] { a - nrm * w, at - nrm * w, at + nrm * w, a + nrm * w }));
            }
            return list;
        }

        /// <summary>Sculpt nodes within the bounds of <paramref name="pts"/> grown by <paramref name="margin"/>, in increasing index order.</summary>
        private static IEnumerable<int> Nodes(Sculpt sc, IReadOnlyList<float2> pts, float margin)
        {
            float2 mn = new float2(float.MaxValue), mx = new float2(float.MinValue);
            foreach (var p in pts) { mn = math.min(mn, p); mx = math.max(mx, p); }
            mn -= margin; mx += margin;
            int i0 = math.max(0, (int)math.floor((mn.x - sc.Origin.x) / sc.Cell) - 1), i1 = math.min(sc.Nx - 1, (int)math.ceil((mx.x - sc.Origin.x) / sc.Cell) + 1);
            int j0 = math.max(0, (int)math.floor((mn.y - sc.Origin.y) / sc.Cell) - 1), j1 = math.min(sc.Ny - 1, (int)math.ceil((mx.y - sc.Origin.y) / sc.Cell) + 1);
            for (int j = j0; j <= j1; j++)
                for (int i = i0; i <= i1; i++)
                    yield return j * sc.Nx + i;
        }

        /// <summary>The plot side that runs along a street or a lane (the edgeZoning test, as Mall.Faces).</summary>
        public static bool Facing(float2[] rect, List<StreetFrontage.RoadLine> roads, List<Mall.Lane> lanes, out float2 a, out float2 b, out Mall.Lane lane)
        {
            Mall.Lane hit = null;
            bool Near(float2 p, out Mall.Lane l)
            {
                l = null;
                foreach (var r in roads)
                    if (math.sqrt(SegmentIndex.DistSq(p, r.A, r.B)) <= r.HalfWidth + StreetFrontage.Tolerance) return true;
                foreach (var ln in lanes)
                    for (int i = 0; i + 1 < ln.Points.Count; i++)
                        if (math.sqrt(SegmentIndex.DistSq(p, ln.Points[i], ln.Points[i + 1])) <= ln.Half + StreetFrontage.Tolerance) { l = ln; return true; }
                return false;
            }
            // An existing street first: where a plot lies between a street and a lane, the street's (older) blocks are the
            // ones the game shows, and the street does not move; a plot between two lanes takes the first.
            for (int pass = 0; pass < 2; pass++)
                for (int i = 0; i < rect.Length; i++)
                {
                    a = rect[i]; b = rect[(i + 1) % rect.Length];
                    if (Near(math.lerp(a, b, 0.5f), out hit) && Near(math.lerp(a, b, 0.2f), out _) && Near(math.lerp(a, b, 0.8f), out _)
                        && (pass == 1 || hit == null)) { lane = hit; return true; }
                }
            a = b = default; lane = null;
            return false;
        }

        /// <summary>Free, unprotected nodes may differ from their neighbours by at most maxGrade × distance (banks).</summary>
        public static void Bank(Sculpt sc, bool[] prot, float maxGrade, int passes = 60)
        {
            int nx = sc.Nx, ny = sc.Ny;
            float c = sc.Cell, dg = c * 1.41421356f;
            for (int pass = 0; pass < passes; pass++)
            {
                bool changed = false;
                for (int dir = 0; dir < 2; dir++)
                    for (int jj = 0; jj < ny; jj++)
                    {
                        int j = dir == 0 ? jj : ny - 1 - jj;
                        for (int ii = 0; ii < nx; ii++)
                        {
                            int i = dir == 0 ? ii : nx - 1 - ii;
                            int k = j * nx + i;
                            if (!sc.Free[k] || prot[k]) continue;
                            float lo = float.MinValue, hi = float.MaxValue;
                            for (int dj = -1; dj <= 1; dj++)
                                for (int di = -1; di <= 1; di++)
                                {
                                    if (di == 0 && dj == 0) continue;
                                    int x = i + di, y = j + dj;
                                    if (x < 0 || y < 0 || x >= nx || y >= ny) continue;
                                    int n = y * nx + x;
                                    float d = (di != 0 && dj != 0 ? dg : c) * maxGrade;
                                    lo = math.max(lo, sc.Target[n] - d);
                                    hi = math.min(hi, sc.Target[n] + d);
                                }
                            float t = lo > hi ? 0.5f * (lo + hi) : math.clamp(sc.Target[k], lo, hi);
                            // Towards the original ground wherever the neighbours allow it (banks never overshoot into mounds).
                            if (lo <= hi) t = math.clamp(sc.Base[k], lo, hi);
                            if (math.abs(t - sc.Target[k]) > 1e-3f) { sc.Target[k] = t; changed = true; }
                        }
                    }
                if (!changed) break;
            }
        }

        /// <summary>A convex ring offset outward by <paramref name="by"/> (mitred corners).</summary>
        private static float2[] Grow(float2[] q, float by)
        {
            var ring = Poly.EnsureCcw(q);
            int n = ring.Length;
            var r = new float2[n];
            for (int i = 0; i < n; i++)
            {
                var p0 = ring[(i + n - 1) % n]; var p1 = ring[i]; var p2 = ring[(i + 1) % n];
                var d1 = math.normalizesafe(p1 - p0); var d2 = math.normalizesafe(p2 - p1);
                var n1 = new float2(d1.y, -d1.x); var n2 = new float2(d2.y, -d2.x);   // outward for CCW
                float k = 1f + math.dot(n1, n2);
                r[i] = p1 + (k > 0.2f ? (n1 + n2) / k : n1 + n2) * by;
            }
            return r;
        }

        /// <summary>A lane's longitudinal profile: samples every Step metres along the centre line.</summary>
        public sealed class Profile
        {
            public const float Step = 2f;
            public readonly List<float2> Pts = new List<float2>();
            public readonly List<float> Cum = new List<float>();
            public float[] H;

            public static Profile Of(Mall.Lane lane, LayerContext ctx, Func<float2, float> g0, List<(float2[] ring, Func<float2, float> height)> lots, Dictionary<Mall.Lane, Profile> hosts = null)
            {
                var pr = new Profile();
                float total = 0f;
                for (int i = 0; i < lane.Points.Count; i++)
                {
                    if (i > 0) total += math.distance(lane.Points[i - 1], lane.Points[i]);
                    pr.Pts.Add(lane.Points[i]);
                    pr.Cum.Add(total);
                }
                int n = math.max(2, (int)math.ceil(total / Step) + 1);
                var h = new float[n];
                var pinned = new bool[n];
                for (int i = 0; i < n; i++)
                {
                    float s = total * i / (n - 1);
                    var p = pr.PointAt(s, out var tangent);
                    var nrm = new float2(-tangent.y, tangent.x);
                    // Level across: the mean of both edges; beside a lot the graded lot edge rules (no bank into the lot).
                    float best = float.NaN, bestD = lane.Half + 3f;
                    foreach (var (ring, f) in lots)
                    {
                        var q = Closest(ring, p, out float d);
                        if (d < bestD) { bestD = d; best = f(q + math.normalizesafe(q - p) * 0.4f); }
                    }
                    h[i] = math.isfinite(best) ? best : 0.5f * (g0(p + nrm * lane.Half) + g0(p - nrm * lane.Half));
                }
                // Attached ends sit on the street — at the street's own height (s41: the captured road surface, not the raw
                // terrain under it: a street on a fill or in a cut left the lane's first metres a step below / above it, the
                // bowl and the torn curtain at the strip mall's entrance, screenshots 71/72).
                // The samples on the street itself and its kerb (up to a metre past its edge) are the street too: the lane leaves
                // the street flush and only then ramps (≤ 8 %) to the site.
                if (lane.StartAttach >= 0)
                {
                    float hs = StreetHeight(ctx, lane.StartAttach, lane.StartT, lane.Points[0]), flush = StreetHalf(ctx, lane.StartAttach) + 1f;
                    for (int i = 0; i < n && total * i / (n - 1) <= flush; i++) { h[i] = hs; pinned[i] = true; }
                    h[0] = hs; pinned[0] = true;
                }
                if (lane.EndAttach >= 0)
                {
                    float hs = StreetHeight(ctx, lane.EndAttach, lane.EndT, lane.Points[lane.Points.Count - 1]), flush = StreetHalf(ctx, lane.EndAttach) + 1f;
                    for (int i = n - 1; i >= 0 && total - total * i / (n - 1) <= flush; i--) { h[i] = hs; pinned[i] = true; }
                    h[n - 1] = hs; pinned[n - 1] = true;
                }
                // (s32) Ends on another lane sit on that lane's profile; a closed loop meets itself.
                if (hosts != null && lane.StartOn != null && hosts.TryGetValue(lane.StartOn, out var hs0)) { hs0.Distance(lane.Points[0], out float s0); h[0] = hs0.At(s0); pinned[0] = true; }
                if (hosts != null && lane.EndOn != null && hosts.TryGetValue(lane.EndOn, out var hs1)) { hs1.Distance(lane.Points[lane.Points.Count - 1], out float s1); h[n - 1] = hs1.At(s1); pinned[n - 1] = true; }
                if (lane.Closed) { float m = 0.5f * (h[0] + h[n - 1]); h[0] = h[n - 1] = m; pinned[0] = pinned[n - 1] = true; }
                for (int pass = 0; pass < 6; pass++)
                {
                    var o = (float[])h.Clone();
                    for (int i = 1; i + 1 < n; i++) if (!pinned[i]) h[i] = 0.25f * o[i - 1] + 0.5f * o[i] + 0.25f * o[i + 1];
                }
                float ds = total / (n - 1), g = (lane.MaxGrade > 0f ? lane.MaxGrade : LaneMaxGrade) * ds;
                // (s47) Between two pinned samples (street ends, a host lane, the lot edge) the profile must be able to get from one
                // height to the other: where the pins are further apart in height than the grade limit allows over that run (a lane
                // down a hillside steeper than its limit, pinned to the street at one end and to a host at the other) the clamps
                // below could only keep the limit by dumping the whole difference as one step beside the first pinned sample — a
                // 0.7 m step 7 m from the kerb, the pit at the entrance. The run takes the grade it needs instead (evenly), the limit
                // everywhere else.
                var link = new float[n];   // the allowed |Δh| between sample i and i + 1
                for (int i = 0; i + 1 < n; i++) link[i] = g;
                for (int a = 0; a < n; a++)
                {
                    if (!pinned[a]) continue;
                    int b = a + 1;
                    while (b < n && !pinned[b]) b++;
                    if (b >= n) break;
                    if (b > a + 1)
                    {
                        float need = math.abs(h[b] - h[a]) / (b - a) * 1.02f;
                        if (need > g) for (int i = a; i < b; i++) link[i] = need;
                    }
                    a = b - 1;
                }
                for (int pass = 0; pass < 4; pass++)
                {
                    for (int i = 1; i < n; i++) if (!pinned[i]) h[i] = math.clamp(h[i], h[i - 1] - link[i - 1], h[i - 1] + link[i - 1]);
                    for (int i = n - 2; i >= 0; i--) if (!pinned[i]) h[i] = math.clamp(h[i], h[i + 1] - link[i], h[i + 1] + link[i]);
                }
                pr.H = h;
                pr._total = total;
                return pr;
            }

            private float _total;

            public float At(float s)
            {
                int n = H.Length;
                if (n == 1 || _total <= 0f) return H[0];
                float f = math.clamp(s / _total, 0f, 1f) * (n - 1);
                int i = math.min(n - 2, (int)math.floor(f));
                return math.lerp(H[i], H[i + 1], f - i);
            }

            public float2 PointAt(float s, out float2 tangent)
            {
                for (int i = 0; i + 1 < Pts.Count; i++)
                {
                    if (s > Cum[i + 1] && i + 2 < Pts.Count) continue;
                    tangent = math.normalizesafe(Pts[i + 1] - Pts[i]);
                    return Pts[i] + tangent * math.clamp(s - Cum[i], 0f, Cum[i + 1] - Cum[i]);
                }
                tangent = new float2(1, 0);
                return Pts[0];
            }

            /// <summary>Distance from p to the centre line and the arc length of the closest point.</summary>
            public float Distance(float2 p, out float s)
            {
                float best = float.MaxValue; s = 0f;
                for (int i = 0; i + 1 < Pts.Count; i++)
                {
                    var a = Pts[i]; var b = Pts[i + 1];
                    var ab = b - a; float l2 = math.lengthsq(ab);
                    float t = l2 > 1e-6f ? math.saturate(math.dot(p - a, ab) / l2) : 0f;
                    float d = math.distance(p, a + ab * t);
                    if (d < best) { best = d; s = Cum[i] + t * math.sqrt(l2); }
                }
                return best;
            }

            private static float2 Closest(float2[] ring, float2 p, out float dist)
            {
                float best = float.MaxValue; float2 q = p;
                for (int i = 0; i < ring.Length; i++)
                {
                    var a = ring[i]; var b = ring[(i + 1) % ring.Length];
                    var ab = b - a; float l2 = math.lengthsq(ab);
                    float t = l2 > 1e-6f ? math.saturate(math.dot(p - a, ab) / l2) : 0f;
                    var c = a + ab * t;
                    float d = math.distance(p, c);
                    if (d < best) { best = d; q = c; }
                }
                dist = best;
                return q;
            }
        }
    }
}
