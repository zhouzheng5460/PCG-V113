// Park path HIERARCHY (grand park wave, g3). The path style (ParkPathStyles / ParkPathPlanner)
// still draws the main walk; this file adds the other tiers on top of it, each with its own controls
// and asset, so a big park reads like a designed one instead of one uniform network:
//
//   bike route     a wide through-route from one side of the park to the other, with its OWN entries
//                  beside two far-apart entrances. Planned as a least-cost route on a grid whose cost
//                  punishes running alongside footpaths (closer + more parallel = dearer) but not crossing
//                  them square, so it keeps its distance and crosses at sensible angles. Every crossing
//                  becomes a shared node (both nets split there). Bike nets come from the catalog
//                  ("net.bike"), falling back to the wide pathway (ParkPathCategories.cs).
//   main walk      the style's network (unchanged; "pathWidth" decides wide/narrow as before).
//   small paths    narrow secondary links across big lawns, joining two main paths at proper junctions.
//   dirt trails    informal trails through the boundary belt (under the trees), meandering, joined to
//                  the nearest path at each end when one is close ("net.trail", fallback pathway).
//   desire lines   shortcuts people would wear where the network makes them detour: worn ground
//                  (terrain paint, feathered) or paved as narrow paths.
//
// Everything is deterministic (hash-seeded) and respects Reserved, the plaza, the lake and the outline.

using System;
using System.Collections.Generic;
using System.Threading;
using Placemaker.Core.Geometry;
using Placemaker.Core.Layers.Common;
using Unity.Mathematics;

namespace Placemaker.Core.Layers.Park
{
    public sealed class PathHierarchyOptions
    {
        public bool Bike;
        public float BikeGap = 10f;
        public int SmallPaths;
        public int Trails;
        public string Desire = "none";
        public int DesireCount = 3;
        public int DesireLayer = 1;

        public bool AnyNets => Bike || SmallPaths > 0 || Trails > 0;

        public static PathHierarchyOptions From(SettingsBag s) => new PathHierarchyOptions
        {
            Bike = s.B("bikePath", false),
            BikeGap = (float)s.Num("bikeGap", 10),
            SmallPaths = s.I("smallPaths", 0),
            Trails = s.I("trails", 0),
            Desire = s.S("desireLines", "none"),
            DesireCount = s.I("desireCount", 3),
            DesireLayer = s.I("desireLayer", 3),
        };
    }

    public static class ParkPathHierarchy
    {
        public const int PathCorridorKind = 100, DesireCorridorKind = 106;
        public const float BikeEntryOffset = 9f;
        private const float DesireHalfWidth = 0.9f;

        // ───────────────────────── tiers on top of the main walk ─────────────────────────

        /// <summary>Adds the bike route, small paths and trails to <c>s.Curves</c> (splitting hosts at every junction).</summary>
        public static void AddTiers(LayerContext ctx, ParkSite s, PathHierarchyOptions o, Func<float2, bool> okPoint)
        {
            if (s.Curves.Count == 0 && !o.Bike) return;
            var reserved = ReservedIndex(ctx.Input.Reserved);
            if (o.Bike)
            {
                var bike = BikeRoute(ctx, s, okPoint, reserved, o.BikeGap, Hashing.Mix(ctx.Seed, 0xB1CEu), out string problem);
                if (bike.Count == 0) ctx.Issue(IssueSeverity.Info, problem ?? "Placemaker.Park.Issue.NoBikeRoute");
                else
                {
                    var foot = s.Curves;
                    SplitCrossings(bike, foot, out int crossings, out float minAngle);
                    float len = 0f; foreach (var c in bike) len += c.Length;
                    s.Curves = new List<PathCurve>(foot);
                    s.Curves.AddRange(bike);
                    ParkPaths.Index(bike, s.Occ.Corridors, PathCorridorKind);
                    ctx.Plan.Facts["bikeLength"] = len;
                    ctx.Plan.Facts["bikeCrossings"] = crossings;
                    if (crossings > 0) ctx.Plan.Facts["bikeMinCrossingAngle"] = minAngle;
                    ctx.Stat("Placemaker.Stat.BikePath", Math.Round(len), Keys.UnitM);
                }
            }
            ctx.Check();
            if (o.SmallPaths > 0 && s.Curves.Count > 0)
            {
                int made = SmallPaths(s, okPoint, reserved, o.SmallPaths, Hashing.Mix(ctx.Seed, 0x5A11u), ctx.Cancel);
                ctx.Plan.Facts["smallPaths"] = made;
                if (made < o.SmallPaths) ctx.Issue(IssueSeverity.Info, "Placemaker.Park.Issue.FewerSmallPaths");
            }
            ctx.Check();
            if (o.Trails > 0)
            {
                int made = Trails(s, okPoint, reserved, o.Trails, Hashing.Mix(ctx.Seed, 0x7AA1u));
                ctx.Plan.Facts["trails"] = made;
                if (made == 0) ctx.Issue(IssueSeverity.Info, "Placemaker.Park.Issue.NoRoomForTrails");
            }
        }

        private static SegmentIndex ReservedIndex(ReservedSpace reserved)
        {
            var idx = new SegmentIndex(10f);
            if (reserved != null) foreach (var c in reserved.Corridors) idx.Add(c.A, c.B, c.HalfWidth, -1);
            return idx;
        }

        private static bool Clear(ParkSite s, SegmentIndex reserved, Func<float2, bool> okPoint, float2 p, float r) =>
            okPoint(p) && !reserved.Overlaps(p, r) && !s.Occ.HitsPolygon(p, r);

        // ───────────────────────── bike route ─────────────────────────

        public static List<PathCurve> BikeRoute(LayerContext ctx, ParkSite s, Func<float2, bool> okPoint, SegmentIndex reserved,
            float gap, uint seed, out string problem)
        {
            problem = null;
            var result = new List<PathCurve>();
            var region = s.LayerRegion;
            // Playtest 4b: the route connects the BIKE entrances (player markers, or hints from bike-friendly
            // frontage); it never invents entries of its own.
            var bikeGates = new List<int>();
            for (int i = 0; i < s.Gates.Count; i++) if (s.Gates[i].Kind == EntranceKinds.Bike) bikeGates.Add(i);
            if (bikeGates.Count == 0) { problem = "Placemaker.Park.Issue.NoBikeEntrances"; return result; }
            if (bikeGates.Count < 2) { problem = "Placemaker.Park.Issue.BikeNeedsTwoEntrances"; return result; }
            // The two bike entrances farthest apart: a through-route, not a loop.
            int ga = -1, gb = -1; float far = 0f;
            foreach (int i in bikeGates)
                foreach (int j in bikeGates)
                {
                    if (j <= i) continue;
                    float d = math.distance(s.Gates[i].Position, s.Gates[j].Position);
                    if (d > far) { far = d; ga = i; gb = j; }
                }
            if (ga < 0 || far < 40f) { problem = "Placemaker.Park.Issue.BikeNeedsTwoEntrances"; return result; }
            var entryA = s.Gates[ga];
            var entryB = s.Gates[gb];

            // Footpath geometry the route keeps away from.
            var foot = new SegmentIndex(8f);
            foreach (var c in s.Curves)
                for (int i = 1; i < c.Samples.Length; i++) foot.Add(c.Samples[i - 1], c.Samples[i], c.HalfWidth, 0);

            float h = math.clamp(math.sqrt(region.Area) / 45f, 3f, 6f);
            var size = region.Max - region.Min;
            int nx = (int)math.ceil(size.x / h) + 1, ny = (int)math.ceil(size.y / h) + 1;
            while (nx * ny > 40000) { h *= 1.25f; nx = (int)math.ceil(size.x / h) + 1; ny = (int)math.ceil(size.y / h) + 1; }
            int n = nx * ny;
            var valid = new bool[n];
            var basePen = new float[n];
            var nearDist = new float[n];
            var nearDir = new float2[n];
            float halfW = Composer.DefaultHalfWidth(NetRole.PathwayWide);
            float2 Cell(int k) => region.Min + new float2((k % nx) + 0.5f, (k / nx) + 0.5f) * h;
            for (int k = 0; k < n; k++)
            {
                var p = Cell(k);
                if (!Clear(s, reserved, okPoint, p, halfW + 1f)) continue;
                valid[k] = true;
                float bd = region.BoundaryDistance(p, 14f);
                basePen[k] = bd < 14f ? (14f - bd) / 14f * 1.5f : 0f;
                // A little noise so equal-cost ties do not produce staircase routes.
                basePen[k] += 0.05f * Hashing.Unit(Hashing.Mix(seed, (uint)k));
                nearDist[k] = float.MaxValue;
            }
            // Distance (and direction) to the nearest footpath, rasterised segment by segment within the gap.
            foreach (var sg in foot.Segments)
            {
                var mn = math.min(sg.A, sg.B) - gap - region.Min; var mx = math.max(sg.A, sg.B) + gap - region.Min;
                int x0 = math.max(0, (int)math.floor(mn.x / h)), y0 = math.max(0, (int)math.floor(mn.y / h));
                int x1 = math.min(nx - 1, (int)math.ceil(mx.x / h)), y1 = math.min(ny - 1, (int)math.ceil(mx.y / h));
                var sdir = math.normalizesafe(sg.B - sg.A, new float2(1, 0));
                for (int y = y0; y <= y1; y++)
                    for (int x = x0; x <= x1; x++)
                    {
                        int k = y * nx + x;
                        if (!valid[k]) continue;
                        float d = math.sqrt(SegmentIndex.DistSq(Cell(k), sg.A, sg.B));
                        if (d < nearDist[k]) { nearDist[k] = d; nearDir[k] = sdir; }
                    }
            }
            int start = NearestValid(entryA.Position, valid, Cell, n, h * 4f);
            int goal = NearestValid(entryB.Position, valid, Cell, n, h * 4f);
            if (start < 0 || goal < 0) { problem = "Placemaker.Park.Issue.NoBikeRoute"; return result; }
            var startP = Cell(start); var goalP = Cell(goal);

            float StepCost(int from, int to, float2 dir, float len)
            {
                float c = 1f + basePen[to];
                float d = nearDist[to];
                if (d < gap)
                {
                    var p = Cell(to);
                    // Near the entries the route may come close (it has to leave through the same edge).
                    float relax = math.saturate(math.min(math.distance(p, startP), math.distance(p, goalP)) / 15f);
                    float w = 1f - d / gap;
                    float par = math.abs(math.dot(dir, nearDir[to]));
                    c += relax * (6f * w * w * (0.08f + par * par) + (d < h * 0.75f ? 2.5f + 12f * par * par : 0f));
                }
                return c * len;
            }

            var cells = AStar(nx, ny, valid, start, goal, h, StepCost, ctx.Cancel);
            if (cells == null) { problem = "Placemaker.Park.Issue.NoBikeRoute"; return result; }
            var pts = new List<float2> { entryA.Position };
            foreach (var k in cells) pts.Add(Cell(k));
            pts.Add(entryB.Position);
            Func<float2, bool> ok = p => Clear(s, reserved, okPoint, p, halfW + 0.5f) && FootClear(foot, p, gap * 0.5f, entryA.Position, entryB.Position);
            var simple = Simplify(pts, h * 0.9f, ok);

            var segs = new List<float2>();
            for (int i = 1; i < simple.Count; i++) { segs.Add(simple[i - 1]); segs.Add(simple[i]); }
            var graph = ParkPathGraph.FromSegments(segs, new List<float2> { entryA.Position, entryB.Position });
            foreach (var c in ParkPaths.Build(graph, region, true, "wide"))
            {
                c.Tier = PathTier.Bike; c.Kind = PathEdgeKind.Primary; c.Role = NetRole.PathwayWide;
                c.HalfWidth = Composer.DefaultHalfWidth(NetRole.PathwayWide);
                if (c.GateA >= 0) c.GateA = c.GateA == 0 ? ga : gb;
                if (c.GateD >= 0) c.GateD = c.GateD == 0 ? ga : gb;
                result.Add(c);
            }
            return result;
        }

        private static bool FootClear(SegmentIndex foot, float2 p, float r, float2 ea, float2 eb) =>
            math.distance(p, ea) < 12f || math.distance(p, eb) < 12f || !foot.Overlaps(p, r * 0.5f);

        /// <summary>
        /// Entrances no path reaches yet get a straight link to the nearest path: a service entrance a
        /// service track (gravel), a bike entrance without a route a bike path, anything else a thin
        /// path. Hosts are split at the junction. Links that would leave the park are skipped.
        /// </summary>
        public static void ConnectLooseGates(LayerContext ctx, ParkSite s)
        {
            if (s.Curves.Count == 0) return;
            var used = new HashSet<int>();
            foreach (var c in s.Curves) { if (c.GateA >= 0) used.Add(c.GateA); if (c.GateD >= 0) used.Add(c.GateD); }
            int tracks = 0;
            for (int gi = 0; gi < s.Gates.Count; gi++)
            {
                if (used.Contains(gi)) continue;
                var g = s.Gates[gi];
                var link = Link(s, g.Position);
                if (link == null) continue;
                string kind = EntranceKinds.Normalize(g.Kind);
                var role = kind == EntranceKinds.Bike || kind == EntranceKinds.Main ? NetRole.PathwayWide : NetRole.Pathway;
                link.Role = role; link.HalfWidth = Composer.DefaultHalfWidth(role);
                link.Tier = kind == EntranceKinds.Service ? PathTier.Service : kind == EntranceKinds.Bike ? PathTier.Bike : PathTier.Main;
                link.Kind = PathEdgeKind.Secondary;
                link.GateA = gi;
                s.Curves.Add(link);
                ParkPaths.Index(new List<PathCurve> { link }, s.Occ.Corridors, PathCorridorKind);
                if (kind == EntranceKinds.Service) tracks++;
            }
            if (tracks > 0) ctx.Plan.Facts["serviceTracks"] = tracks;
        }

        /// <summary>
        /// Curve from entrance <paramref name="from"/> into the network (s9): straight in along the edge normal
        /// (GateApproach), turning towards the nearest reachable path only after a short approach — never a
        /// leg sideways along the outline. The host path is split at the junction. Null when unreachable.
        /// </summary>
        private static PathCurve Link(ParkSite s, float2 from)
        {
            var spine = new List<(float2 a, float2 b)>();
            foreach (var c0 in s.Curves)
                for (int i = 1; i < c0.Samples.Length; i++) spine.Add((c0.Samples[i - 1], c0.Samples[i]));
            bool Dry(float2 a, float2 b)
            {
                for (int k = 1; k < 10; k++) if (s.InLake(math.lerp(a, b, k / 10f), 1.5f)) return false;
                return true;
            }
            float approach = math.max(Layout.M(8f, 5f), Layout.EdgeMargin + 4f);
            var route = GateApproach.Route(s.LayerRegion, from, spine, approach, Dry);
            if (route == null) return LinkNearest(s, from, GateApproach.Inward(s.LayerRegion, from));
            if (route.Count < 2) return null;
            var target = route[route.Count - 1];
            int bestCurve = -1; float bestT = 0f, bestD = float.MaxValue;
            for (int i = 0; i < s.Curves.Count; i++)
            {
                float t = ClosestT(s.Curves[i], target, out float d);
                if (d < bestD) { bestD = d; bestCurve = i; bestT = t; }
            }
            if (bestCurve < 0) return null;
            var q = SplitForLink(s, bestCurve, bestT);
            var c = new PathCurve { A = from, D = q, GateD = -1 };
            if (route.Count == 3) { c.B = math.lerp(from, route[1], 0.75f); c.C = math.lerp(q, route[1], 0.6f); }
            else { c.B = math.lerp(from, q, 1f / 3f); c.C = math.lerp(from, q, 2f / 3f); }
            ParkPaths.Sample(c);
            return math.distance(from, q) > 0.5f ? c : null;
        }

        private static float ClosestT(PathCurve c, float2 p, out float d)
        {
            float best = float.MaxValue, bt = 0f;
            for (int k = 0; k <= 64; k++)
            {
                float dd = math.distancesq(c.Point(k / 64f), p);
                if (dd < best) { best = dd; bt = k / 64f; }
            }
            d = math.sqrt(best);
            return bt;
        }

        /// <summary>Splits curve <paramref name="index"/> at t (unless near an end) and returns the junction point.</summary>
        private static float2 SplitForLink(ParkSite s, int index, float t)
        {
            var host = s.Curves[index];
            if (t > 0.05f && t < 0.95f)
            {
                var first = ParkPlaces.Sub(host, 0f, t);
                var second = ParkPlaces.Sub(host, t, 1f);
                first.GateD = -1; second.GateA = -1;
                s.Curves[index] = first;
                s.Curves.Add(second);
                return first.D;
            }
            return t <= 0.05f ? host.A : host.D;
        }

        /// <summary>Old straight link to the nearest path point, only when it heads inward (within 60° of the edge normal).</summary>
        private static PathCurve LinkNearest(ParkSite s, float2 from, float2 inward)
        {
            int bestCurve = -1; float bestT = 0f, bestD = float.MaxValue; float2 q = from;
            for (int i = 0; i < s.Curves.Count; i++)
                for (int k = 0; k <= 32; k++)
                {
                    var p = s.Curves[i].Point(k / 32f);
                    float d = math.distancesq(p, from);
                    if (d < bestD) { bestD = d; bestCurve = i; bestT = k / 32f; q = p; }
                }
            if (bestCurve < 0 || bestD < 1f) return null;
            if (math.dot(math.normalizesafe(q - from), inward) < 0.5f) return null;
            float len = math.sqrt(bestD);
            for (int k = 1; k < 10; k++)
            {
                var p = math.lerp(from, q, k / 10f);
                if (math.distance(p, from) > 2f && !s.LayerRegion.ContainsDisc(p, 1f)) return null;
                if (s.InLake(p, 1.5f)) return null;
            }
            var host = s.Curves[bestCurve];
            if (bestT > 0.05f && bestT < 0.95f)
            {
                var first = ParkPlaces.Sub(host, 0f, bestT);
                var second = ParkPlaces.Sub(host, bestT, 1f);
                first.GateD = -1; second.GateA = -1;
                s.Curves[bestCurve] = first;
                s.Curves.Add(second);
                q = first.D;
            }
            else q = bestT <= 0.05f ? host.A : host.D;
            var c = new PathCurve { A = from, B = math.lerp(from, q, 1f / 3f), C = math.lerp(from, q, 2f / 3f), D = q, GateD = -1 };
            ParkPaths.Sample(c);
            return len > 0.5f ? c : null;
        }


        private static int NearestValid(float2 p, bool[] valid, Func<int, float2> cell, int n, float reach)
        {
            int best = -1; float bd = reach * reach;
            for (int k = 0; k < n; k++)
            {
                if (!valid[k]) continue;
                float d = math.distancesq(cell(k), p);
                if (d < bd) { bd = d; best = k; }
            }
            return best;
        }

        private static readonly int2[] Moves =
            { new int2(1, 0), new int2(-1, 0), new int2(0, 1), new int2(0, -1), new int2(1, 1), new int2(1, -1), new int2(-1, 1), new int2(-1, -1) };

        private static List<int> AStar(int nx, int ny, bool[] valid, int start, int goal, float h,
            Func<int, int, float2, float, float> cost, CancellationToken cancel)
        {
            int n = nx * ny;
            var g = new float[n];
            var from = new int[n];
            var closed = new bool[n];
            for (int i = 0; i < n; i++) { g[i] = float.MaxValue; from[i] = -1; }
            var heap = new MinHeap();
            float2 Pos(int k) => new float2(k % nx, k / nx) * h;
            g[start] = 0f;
            heap.Push(start, math.distance(Pos(start), Pos(goal)));
            int pops = 0;
            while (heap.Count > 0)
            {
                int u = heap.Pop();
                if (closed[u]) continue;
                closed[u] = true;
                if (u == goal) break;
                if ((++pops & 1023) == 0) cancel.ThrowIfCancellationRequested();
                int ux = u % nx, uy = u / nx;
                foreach (var m in Moves)
                {
                    int vx = ux + m.x, vy = uy + m.y;
                    if (vx < 0 || vy < 0 || vx >= nx || vy >= ny) continue;
                    int v = vy * nx + vx;
                    if (!valid[v] || closed[v]) continue;
                    // No corner cutting past invalid cells.
                    if (m.x != 0 && m.y != 0 && (!valid[uy * nx + vx] || !valid[vy * nx + ux])) continue;
                    float len = (m.x != 0 && m.y != 0 ? 1.41421356f : 1f) * h;
                    float ng = g[u] + cost(u, v, math.normalize(new float2(m.x, m.y)), len);
                    if (ng >= g[v]) continue;
                    g[v] = ng; from[v] = u;
                    heap.Push(v, ng + math.distance(Pos(v), Pos(goal)));
                }
            }
            if (from[goal] < 0 && goal != start) return null;
            var path = new List<int>();
            for (int k = goal; k >= 0; k = from[k]) { path.Add(k); if (k == start) break; }
            path.Reverse();
            return path;
        }

        private sealed class MinHeap
        {
            private readonly List<(int id, float key)> _a = new List<(int, float)>();
            public int Count => _a.Count;

            public void Push(int id, float key)
            {
                _a.Add((id, key));
                int i = _a.Count - 1;
                while (i > 0)
                {
                    int p = (i - 1) / 2;
                    if (_a[p].key <= _a[i].key) break;
                    (_a[p], _a[i]) = (_a[i], _a[p]); i = p;
                }
            }

            public int Pop()
            {
                var top = _a[0].id;
                var last = _a[_a.Count - 1];
                _a.RemoveAt(_a.Count - 1);
                if (_a.Count > 0)
                {
                    _a[0] = last;
                    int i = 0;
                    while (true)
                    {
                        int l = 2 * i + 1, r = l + 1, m = i;
                        if (l < _a.Count && _a[l].key < _a[m].key) m = l;
                        if (r < _a.Count && _a[r].key < _a[m].key) m = r;
                        if (m == i) break;
                        (_a[m], _a[i]) = (_a[i], _a[m]); i = m;
                    }
                }
                return top;
            }
        }

        /// <summary>Douglas–Peucker that also splits wherever the shortcut would be invalid.</summary>
        private static List<float2> Simplify(List<float2> pts, float tol, Func<float2, bool> ok)
        {
            var keep = new bool[pts.Count];
            keep[0] = keep[pts.Count - 1] = true;
            var stack = new Stack<(int, int)>();
            stack.Push((0, pts.Count - 1));
            while (stack.Count > 0)
            {
                var (a, b) = stack.Pop();
                if (b - a < 2) continue;
                int worst = -1; float wd = -1f;
                for (int i = a + 1; i < b; i++)
                {
                    float d = math.sqrt(SegmentIndex.DistSq(pts[i], pts[a], pts[b]));
                    if (d > wd) { wd = d; worst = i; }
                }
                bool split = wd > tol;
                if (!split)
                {
                    float len = math.distance(pts[a], pts[b]);
                    int k = math.max(2, (int)(len / 2f));
                    for (int i = 1; i < k && !split; i++) if (!ok(math.lerp(pts[a], pts[b], i / (float)k))) split = true;
                }
                if (!split) continue;
                keep[worst] = true;
                stack.Push((a, worst)); stack.Push((worst, b));
            }
            var r = new List<float2>();
            for (int i = 0; i < pts.Count; i++) if (keep[i]) r.Add(pts[i]);
            return r;
        }

        // ───────────────────────── crossings ─────────────────────────

        /// <summary>
        /// Splits every curve of <paramref name="a"/> and <paramref name="b"/> where they cross, so both nets share a
        /// node there. Returns the crossing count and the smallest crossing angle (degrees).
        /// </summary>
        public static void SplitCrossings(List<PathCurve> a, List<PathCurve> b, out int count, out float minAngle)
        {
            count = 0; minAngle = 90f;
            for (int guard = 0; guard < 400; guard++)
            {
                bool found = false;
                for (int i = 0; i < a.Count && !found; i++)
                    for (int j = 0; j < b.Count && !found; j++)
                    {
                        if (!FirstCrossing(a[i], b[j], out float ta, out float tb)) continue;
                        var p = a[i].Point(ta);
                        float cos = math.abs(math.dot(a[i].Tangent(ta), b[j].Tangent(tb)));
                        minAngle = math.min(minAngle, math.degrees(math.acos(math.saturate(cos))));
                        var (a0, a1) = SplitAt(a[i], ta, p);
                        var (b0, b1) = SplitAt(b[j], tb, p);
                        a[i] = a0; a.Insert(i + 1, a1);
                        b[j] = b0; b.Insert(j + 1, b1);
                        count++;
                        found = true;
                    }
                if (!found) return;
            }
        }

        private static bool FirstCrossing(PathCurve x, PathCurve y, out float tx, out float ty)
        {
            tx = ty = 0f;
            // Quick reject on bounds.
            Bounds(x, out var xmin, out var xmax); Bounds(y, out var ymin, out var ymax);
            if (xmax.x < ymin.x || ymax.x < xmin.x || xmax.y < ymin.y || ymax.y < xmin.y) return false;
            int n = ParkPaths.SamplesPerCurve;
            for (int i = 1; i < x.Samples.Length; i++)
                for (int j = 1; j < y.Samples.Length; j++)
                {
                    if (!Poly.SegmentIntersection(x.Samples[i - 1], x.Samples[i], y.Samples[j - 1], y.Samples[j], out var p, out float u, out float v)) continue;
                    // Shared ends are junctions already.
                    if (NearEnd(x, p) || NearEnd(y, p)) continue;
                    tx = (i - 1 + u) / n; ty = (j - 1 + v) / n;
                    return true;
                }
            return false;
        }

        private static bool NearEnd(PathCurve c, float2 p) => math.distance(c.A, p) < 1.5f || math.distance(c.D, p) < 1.5f;

        private static void Bounds(PathCurve c, out float2 mn, out float2 mx)
        {
            mn = new float2(float.MaxValue); mx = new float2(float.MinValue);
            foreach (var p in c.Samples) { mn = math.min(mn, p); mx = math.max(mx, p); }
        }

        /// <summary>Two halves of a curve at t, both ending exactly at <paramref name="p"/>.</summary>
        public static (PathCurve, PathCurve) SplitAt(PathCurve c, float t, float2 p)
        {
            var l = ParkPlaces.Sub(c, 0f, t);
            var r = ParkPlaces.Sub(c, t, 1f);
            var dl = p - l.D; l.D = p; l.C += dl;
            var dr = p - r.A; r.A = p; r.B += dr;
            ParkPaths.Sample(l); ParkPaths.Sample(r);
            return (l, r);
        }

        /// <summary>Splits <paramref name="host"/> (an element of curves) at t, snapping the joint to p.</summary>
        private static void SplitHost(List<PathCurve> curves, PathCurve host, float t, float2 p)
        {
            int i = curves.IndexOf(host);
            if (i < 0) return;
            if (t <= 0.02f || t >= 0.98f) return;   // the end node is the junction
            var (l, r) = SplitAt(host, t, p);
            curves[i] = l; curves.Insert(i + 1, r);
        }

        // ───────────────────────── anchors on the network ─────────────────────────

        private struct Anchor { public PathCurve Curve; public float T; public float2 P; public float2 Tan; }

        private static List<Anchor> Anchors(List<PathCurve> curves, float spacing, Func<PathCurve, bool> use)
        {
            var r = new List<Anchor>();
            foreach (var c in curves)
            {
                if (!use(c) || c.Length < 8f) continue;
                int k = math.max(1, (int)(c.Length / spacing));
                for (int i = 0; i <= k; i++)
                {
                    float t = (i + 0.5f) / (k + 1);
                    r.Add(new Anchor { Curve = c, T = t, P = c.Point(t), Tan = c.Tangent(t) });
                }
            }
            return r;
        }

        private static float2 PointOnAnchor(Anchor a, List<PathCurve> curves, out PathCurve host, out float t)
        {
            host = a.Curve; t = a.T;
            if (curves.Contains(a.Curve)) return a.P;
            // The anchor's curve was split by an earlier pick: find the piece that holds the point now.
            float best = float.MaxValue;
            foreach (var c in curves)
                for (int k = 0; k <= 32; k++)
                {
                    float d = math.distancesq(c.Point(k / 32f), a.P);
                    if (d < best) { best = d; host = c; t = k / 32f; }
                }
            return host.Point(t);
        }

        // ───────────────────────── small paths ─────────────────────────

        private static int SmallPaths(ParkSite s, Func<float2, bool> okPoint, SegmentIndex reserved, int wanted, uint seed, CancellationToken cancel)
        {
            var region = s.LayerRegion;
            float spacing = math.max(10f, math.sqrt(region.Area) / 12f);
            var anchors = Anchors(s.Curves, spacing, c => c.Tier == PathTier.Main);
            float hw = Composer.DefaultHalfWidth(NetRole.Pathway);
            int made = 0;
            for (int round = 0; round < wanted; round++)
            {
                cancel.ThrowIfCancellationRequested();
                int bi = -1, bj = -1; float bestScore = 0f;
                for (int i = 0; i < anchors.Count; i++)
                    for (int j = i + 1; j < anchors.Count; j++)
                    {
                        var a = anchors[i]; var b = anchors[j];
                        if (a.Curve == b.Curve) continue;
                        float d = math.distance(a.P, b.P);
                        if (d < 25f || d > 160f) continue;
                        var dir = (b.P - a.P) / d;
                        if (math.abs(math.dot(dir, a.Tan)) > 0.87f || math.abs(math.dot(dir, b.Tan)) > 0.87f) continue;
                        var mid = (a.P + b.P) * 0.5f;
                        float clear = s.Occ.Corridors.Clearance(mid, 40f);
                        if (clear < 12f) continue;
                        float score = clear * math.min(d, 70f) / 70f * (0.85f + 0.3f * Hashing.Unit(Hashing.Mix(seed, (uint)(i * 7919 + j))));
                        if (score <= bestScore) continue;
                        if (!LineClear(s, reserved, okPoint, a.P, b.P, hw + 0.5f, 6f)) continue;
                        bestScore = score; bi = i; bj = j;
                    }
                if (bi < 0) break;
                var pa = PointOnAnchor(anchors[bi], s.Curves, out var ha, out float ta);
                var pb = PointOnAnchor(anchors[bj], s.Curves, out var hb, out float tb);
                var curve = Gentle(pa, pb, Hashing.Mix(seed, (uint)round), p => Clear(s, reserved, okPoint, p, hw));
                curve.Tier = PathTier.Small; curve.Role = NetRole.Pathway; curve.Kind = PathEdgeKind.Secondary; curve.HalfWidth = hw;
                SplitHost(s.Curves, ha, ta, pa);
                SplitHost(s.Curves, hb, tb, pb);
                s.Curves.Add(curve);
                for (int i = 1; i < curve.Samples.Length; i++) s.Occ.Corridors.Add(curve.Samples[i - 1], curve.Samples[i], hw, PathCorridorKind);
                // Anchors near the new path's ends are spent (no fans of links from one point).
                anchors.RemoveAll(x => math.distance(x.P, pa) < 20f || math.distance(x.P, pb) < 20f);
                made++;
            }
            return made;
        }

        private static bool LineClear(ParkSite s, SegmentIndex reserved, Func<float2, bool> okPoint, float2 a, float2 b, float r, float skipEnds)
        {
            float len = math.distance(a, b);
            int k = math.max(2, (int)(len / 2.5f));
            for (int i = 1; i < k; i++)
            {
                var p = math.lerp(a, b, i / (float)k);
                float along = len * i / k;
                if (along < skipEnds || along > len - skipEnds) continue;
                if (!Clear(s, reserved, okPoint, p, r) || s.Occ.Corridors.Overlaps(p, r)) return false;
            }
            return true;
        }

        /// <summary>A gently bowed curve between two points (straight when the bow would hit something).</summary>
        private static PathCurve Gentle(float2 a, float2 b, uint seed, Func<float2, bool> ok)
        {
            float d = math.distance(a, b);
            var dir = (b - a) / math.max(1e-3f, d);
            var nrm = new float2(-dir.y, dir.x);
            float side = Hashing.Unit(seed) < 0.5f ? -1f : 1f;
            bool s = Hashing.Unit(Hashing.Mix(seed, 3u)) < 0.4f;   // an S-bend now and then
            float off = d * 0.09f;
            var c = new PathCurve { A = a, D = b };
            c.B = math.lerp(a, b, 1f / 3f) + nrm * off * side;
            c.C = math.lerp(a, b, 2f / 3f) + nrm * off * (s ? -side : side);
            ParkPaths.Sample(c);
            foreach (var p in c.Samples)
                if (math.distance(p, a) > 3f && math.distance(p, b) > 3f && !ok(p))
                {
                    c.B = math.lerp(a, b, 1f / 3f); c.C = math.lerp(a, b, 2f / 3f);
                    ParkPaths.Sample(c);
                    break;
                }
            return c;
        }

        // ───────────────────────── dirt trails ─────────────────────────

        private static int Trails(ParkSite s, Func<float2, bool> okPoint, SegmentIndex reserved, int wanted, uint seed)
        {
            var region = s.LayerRegion;
            float inset = math.clamp(math.sqrt(region.Area) * 0.04f, 6f, 8f);   // s9: wobble (±1.6 m) never brings it within the 3 m edge band
            var ring = region.InsetVertices(inset);
            if (ring == null || ring.Length < 3 || Poly.SignedArea(ring) < 50f || !Poly.IsSimple(ring)) return 0;
            ring = Poly.Resample(ring, 6f);
            int n = ring.Length;
            float hw = Composer.DefaultHalfWidth(NetRole.Pathway);
            var ok = new bool[n];
            int okCount = 0;
            for (int i = 0; i < n; i++)
            {
                var p = ring[i];
                bool good = Clear(s, reserved, okPoint, p, hw) && !s.Occ.Corridors.Overlaps(p, hw + 3f) && s.GateDistanceSq(p) > 12f * 12f;
                ok[i] = good; if (good) okCount++;
            }
            if (okCount < 5) return 0;
            // Runs of consecutive good points around the ring.
            var runs = new List<List<float2>>();
            int startAt = 0;
            if (okCount < n) while (ok[startAt]) startAt++;   // start at a gap so runs do not wrap
            var cur = new List<float2>();
            for (int k = 0; k <= n; k++)
            {
                int i = (startAt + k) % n;
                if (k < n && ok[i]) { cur.Add(ring[i]); continue; }
                if (cur.Count >= 5) runs.Add(cur);
                cur = new List<float2>();
            }
            runs.Sort((x, y) => y.Count.CompareTo(x.Count));
            int made = 0;
            var host = s.Curves;
            var anchors = Anchors(host, 6f, c => c.Tier != PathTier.Bike);
            foreach (var run0 in runs)
            {
                if (made >= wanted) break;
                // Long runs are cut to a walkable trail (≤ ~160 m) centred on the run.
                var run = run0;
                if (run.Count > 28) run = run.GetRange((run.Count - 28) / 2, 28);
                // One bend every ~18 m: each vertex becomes a net node, and a trail is a few long curves.
                var thin = new List<float2>();
                for (int i = 0; i < run.Count; i += 3) thin.Add(run[i]);
                if ((run.Count - 1) % 3 != 0) thin.Add(run[run.Count - 1]);
                run = thin;
                if (run.Count < 3) continue;
                uint rs = Hashing.Mix(seed, (uint)made);
                var pts = new List<float2>();
                for (int i = 0; i < run.Count; i++)
                {
                    var p = run[i];
                    if (i > 0 && i < run.Count - 1)
                    {
                        var t = math.normalizesafe(run[i + 1] - run[i - 1]);
                        var nrm = new float2(-t.y, t.x);
                        var q = p + nrm * (Hashing.Unit(Hashing.Mix(rs, (uint)i)) * 2f - 1f) * 1.6f;
                        if (Clear(s, reserved, okPoint, q, hw) && !s.Occ.Corridors.Overlaps(q, hw + 2f)) p = q;
                    }
                    pts.Add(p);
                }
                var segs = new List<float2>();
                for (int i = 1; i < pts.Count; i++) { segs.Add(pts[i - 1]); segs.Add(pts[i]); }
                // Join each end to the nearest path when one is close (otherwise the trail just fades out).
                var joins = new List<(Anchor a, float2 end)>();
                foreach (var end in new[] { pts[0], pts[pts.Count - 1] })
                {
                    int best = -1; float bd = 22f;
                    for (int i = 0; i < anchors.Count; i++)
                    {
                        float d = math.distance(anchors[i].P, end);
                        if (d < bd && LineClear(s, reserved, okPoint, end, anchors[i].P, hw, 5f)) { bd = d; best = i; }
                    }
                    if (best >= 0) { segs.Add(end); segs.Add(anchors[best].P); joins.Add((anchors[best], end)); }
                }
                var graph = ParkPathGraph.FromSegments(segs, Array.Empty<float2>());
                var curves = ParkPaths.Build(graph, region, true, "narrow");
                if (curves.Count == 0) continue;
                foreach (var c in curves)
                {
                    c.Tier = PathTier.Trail; c.Kind = PathEdgeKind.Secondary; c.Role = NetRole.Pathway; c.HalfWidth = hw;
                    for (int i = 1; i < c.Samples.Length; i++) s.Occ.Corridors.Add(c.Samples[i - 1], c.Samples[i], hw, PathCorridorKind);
                }
                foreach (var (a, _) in joins)
                {
                    var p = PointOnAnchor(a, s.Curves, out var h, out float t);
                    SplitHost(s.Curves, h, t, p);
                }
                s.Curves.AddRange(curves);
                made++;
            }
            return made;
        }

        // ───────────────────────── desire lines ─────────────────────────

        /// <summary>
        /// Shortcuts across lawns between network nodes whose walk is much longer than the straight line.
        /// "worn": feathered terrain paint; "paved": narrow paths. Returns how many were made.
        /// </summary>
        public static int DesireLines(LayerContext ctx, ParkSite s, PathHierarchyOptions o, Func<float2, bool> okPoint)
        {
            if (o.Desire != "worn" && o.Desire != "paved" || s.Curves.Count == 0) return 0;
            var reserved = ReservedIndex(ctx.Input.Reserved);
            // Nodes = curve ends (merged), edges = pedestrian curves (not the bike route).
            var nodes = new List<float2>();
            int Node(float2 p)
            {
                for (int i = 0; i < nodes.Count; i++) if (math.distancesq(nodes[i], p) < 1f) return i;
                nodes.Add(p); return nodes.Count - 1;
            }
            var edges = new List<(int a, int b, float w)>();
            foreach (var c in s.Curves)
            {
                if (c.Tier == PathTier.Bike) continue;
                edges.Add((Node(c.A), Node(c.D), c.Length));
            }
            int n = nodes.Count;
            if (n < 3 || n > 600) return 0;
            var adj = new List<(int to, float w)>[n];
            for (int i = 0; i < n; i++) adj[i] = new List<(int, float)>();
            foreach (var (a, b, w) in edges) { adj[a].Add((b, w)); adj[b].Add((a, w)); }
            var cands = new List<(float score, int a, int b)>();
            var dist = new float[n];
            for (int src = 0; src < n; src++)
            {
                ctx.Check();
                Dijkstra(adj, src, dist);
                for (int dst = src + 1; dst < n; dst++)
                {
                    float straight = math.distance(nodes[src], nodes[dst]);
                    if (straight < 20f || straight > 140f) continue;
                    float walk = dist[dst];
                    float ratio = float.IsInfinity(walk) || walk == float.MaxValue ? 3f : walk / straight;
                    if (ratio < 1.6f) continue;
                    cands.Add(((math.min(ratio, 3f) - 1f) * straight * (0.9f + 0.2f * Hashing.Unit(Hashing.Mix(ctx.Seed, (uint)(src * 1009 + dst)))), src, dst));
                }
            }
            cands.Sort((x, y) => y.score.CompareTo(x.score));
            var made = new List<(float2 a, float2 b)>();
            float hw = o.Desire == "paved" ? Composer.DefaultHalfWidth(NetRole.Pathway) : DesireHalfWidth;
            foreach (var (_, ia, ib) in cands)
            {
                if (made.Count >= o.DesireCount) break;
                var a = nodes[ia]; var b = nodes[ib];
                bool near = false;
                foreach (var (ma, mb) in made)
                    if (math.distance((ma + mb) * 0.5f, (a + b) * 0.5f) < 20f || math.distance(ma, a) + math.distance(mb, b) < 16f || math.distance(ma, b) + math.distance(mb, a) < 16f)
                    { near = true; break; }
                if (near || !DesireClear(s, reserved, okPoint, a, b, hw)) continue;
                made.Add((a, b));
            }
            var pathAsset = AssetMix.One("");
            for (int i = 0; i < made.Count; i++)
            {
                var (a, b) = made[i];
                if (o.Desire == "paved")
                {
                    var c = new PathCurve
                    {
                        A = a, B = math.lerp(a, b, 1f / 3f), C = math.lerp(a, b, 2f / 3f), D = b,
                        Tier = PathTier.Desire, Role = NetRole.Pathway, Kind = PathEdgeKind.Secondary, HalfWidth = hw,
                    };
                    ParkPaths.Sample(c);
                    s.Curves.Add(c);
                    for (int k = 1; k < c.Samples.Length; k++) s.Occ.Corridors.Add(c.Samples[k - 1], c.Samples[k], hw, PathCorridorKind);
                }
                else
                {
                    // Worn strip, trimmed off the paths at both ends, slightly wobbly, feathered.
                    float len = math.distance(a, b);
                    var dir = (b - a) / len; var nrm = new float2(-dir.y, dir.x);
                    var a2 = a + dir * 1.8f; var b2 = b - dir * 1.8f;
                    int k = math.max(2, (int)(len / 8f));
                    uint ws = Hashing.Mix(ctx.Seed, 0xDE51u + (uint)i);

                    // s33: one painted stroke (frayed, faded ends, knockout) instead of a feathered polygon.
                    var kit = new Placemaker.Core.Paint.PaintKit(ctx.Plan.Paint, ctx.H, ws) { Outline = ctx.LayerRegion.Ring };
                    TrailPaint.DesireLine(kit, a2, b2, 2f * hw + 0.6f, 0.72f, math.clamp(o.DesireLayer, 1, 4), ws);
                    TerrainPaint.Warn(ctx);

                    for (int j = 1; j <= k; j++) s.Occ.Corridors.Add(math.lerp(a2, b2, (j - 1f) / k), math.lerp(a2, b2, (float)j / k), hw, DesireCorridorKind);
                }
            }
            ctx.Plan.Facts["desireLines"] = made.Count;
            if (made.Count == 0) ctx.Issue(IssueSeverity.Info, "Placemaker.Park.Issue.NoDesireLines");
            return made.Count;
        }

        private static bool DesireClear(ParkSite s, SegmentIndex reserved, Func<float2, bool> okPoint, float2 a, float2 b, float hw)
        {
            float len = math.distance(a, b);
            int k = math.max(2, (int)(len / 2f));
            int crossings = 0; bool onPath = true;
            for (int i = 1; i < k; i++)
            {
                var p = math.lerp(a, b, i / (float)k);
                float along = len * i / k;
                if (!Clear(s, reserved, okPoint, p, hw)) return false;
                // Amenity places, feature clearings and placed things are not walked across.
                if (s.Occ.Discs.Overlaps(p, hw)) return false;
                if (along < 3f || along > len - 3f) continue;
                bool hit = s.Occ.Corridors.Overlaps(p, hw * 0.5f);
                if (hit && !onPath) crossings++;
                onPath = hit;
            }
            // Crossing one path on the way is how real desire lines look; running along paths is not.
            return crossings <= 1;
        }

        private static void Dijkstra(List<(int to, float w)>[] adj, int src, float[] dist)
        {
            for (int i = 0; i < dist.Length; i++) dist[i] = float.MaxValue;
            dist[src] = 0f;
            var heap = new MinHeap();
            heap.Push(src, 0f);
            var done = new bool[dist.Length];
            while (heap.Count > 0)
            {
                int u = heap.Pop();
                if (done[u]) continue;
                done[u] = true;
                foreach (var (v, w) in adj[u])
                {
                    float nd = dist[u] + w;
                    if (nd < dist[v]) { dist[v] = nd; heap.Push(v, nd); }
                }
            }
        }
    }
}
