// Placemaker roads layer — city blocks (buildable land between roads) and frontage strips.
//
// Blocks are the bounded faces of the planar arrangement of: generated roads, existing roads inside
// the area and the area outline, each face shrunk by the half-width of the road along each of its
// sides. The outline is only a boundary where no road runs along it (coverage test), so an outline
// that follows a road (block pick, perimeter road) never leaves a sliver between road and outline.
// Dead ends (cul-de-sacs) cut a rounded notch; components floating inside a face are bridged by a
// 1 m slit because CS2 areas cannot have holes.

using System;
using System.Collections.Generic;
using System.Threading;
using Placemaker.Core.Geometry;
using Unity.Mathematics;

namespace Placemaker.Core.Layers.Roads
{
    internal static class RoadBlocks
    {
        public const byte KindGenerated = 0, KindExisting = 1, KindRing = 2, KindConnector = 3;

        /// <summary>Smallest block worth handing to a child layer (m²).</summary>
        public const float MinBlockArea = 150f;
        private const float Weld = 0.05f;
        private const float Chord = 4f;
        private const float BridgeHalfWidth = 0.5f;

        public struct Line
        {
            public List<float2> Points;
            public float HalfWidth;
            public byte Kind;
            public NetRole Role;
        }

        public struct Block
        {
            public float2[] Ring;
            public float2 Centroid;
            /// <summary>Every road side of the block is a roundabout (the island of a roundabout).</summary>
            public bool RoundaboutIsland;
        }

        private struct S
        {
            public float2 A, B;
            public float W;
            public byte Kind;
            public NetRole Role;
        }

        private sealed class Graph
        {
            public readonly List<float2> Pos = new List<float2>();
            public readonly List<(int u, int v, float w, byte kind, NetRole role)> Edges = new List<(int, int, float, byte, NetRole)>();
            public List<int>[] Adj;
            public bool[] Dead;
        }

        /// <summary>
        /// Computes the blocks. roads: generated and existing road centre lines with half-widths.
        /// capTips: positions of cul-de-sac tips with turning circles and their radius.
        /// </summary>
        public static List<Block> Compute(float2[] ring, List<Line> roads, List<(float2 p, float r)> capTips,
            List<BoundaryAttachment> alongEdge, CancellationToken cancel)
        {
            var blocks = new List<Block>();
            if (ring == null || ring.Length < 3) return blocks;

            var roadIndex = new SegmentIndex(16f);
            var roadSegs = new List<S>();
            foreach (var l in roads)
            {
                if (l.Points == null || l.Points.Count < 2) continue;
                for (int i = 0; i + 1 < l.Points.Count; i++)
                {
                    if (math.distancesq(l.Points[i], l.Points[i + 1]) < 1e-6f) continue;
                    roadSegs.Add(new S { A = l.Points[i], B = l.Points[i + 1], W = l.HalfWidth, Kind = l.Kind, Role = l.Role });
                    roadIndex.Add(l.Points[i], l.Points[i + 1], roadSegs.Count - 1);
                }
            }

            var segs = new List<S>(roadSegs);
            AddRing(ring, roadSegs, roadIndex, alongEdge, segs);
            cancel.ThrowIfCancellationRequested();

            Graph g = null;
            for (int attempt = 0; attempt < 8; attempt++)
            {
                g = BuildGraph(segs);
                PruneDangles(g, ring);
                cancel.ThrowIfCancellationRequested();
                if (!Bridge(g, segs)) break;
            }
            cancel.ThrowIfCancellationRequested();

            foreach (var face in Faces(g))
            {
                cancel.ThrowIfCancellationRequested();
                var pts = new List<float2>(face.Count);
                var widths = new List<float>(face.Count);
                var caps = new List<float>(face.Count);
                bool allRoundabout = true, anyRoad = false;
                foreach (int he in face)
                {
                    var e = g.Edges[he >> 1];
                    int from = (he & 1) == 0 ? e.u : e.v;
                    pts.Add(g.Pos[from]);
                    widths.Add(e.w);
                    caps.Add(CapRadius(g.Pos[from], capTips));
                    if (e.kind == KindGenerated || e.kind == KindExisting)
                    {
                        anyRoad = true;
                        if (e.role != NetRole.Roundabout) allRoundabout = false;
                    }
                    else if (e.w > 0f || e.kind == KindRing) allRoundabout = false;
                }
                foreach (var loop in Inset(pts, widths, caps))
                {
                    if (!Validate(loop, pts, widths)) continue;
                    var clean = Simplify(Poly.Clean(loop, SurfaceAcceptance.MinEdge), 0.1f);
                    if (clean.Length < 3 || !Poly.IsSimple(clean) || Poly.Area(clean) < MinBlockArea) continue;
                    blocks.Add(new Block { Ring = clean, Centroid = Poly.Centroid(clean), RoundaboutIsland = anyRoad && allRoundabout });
                }
            }
            return blocks;
        }

        private static float CapRadius(float2 p, List<(float2 p, float r)> capTips)
        {
            if (capTips == null) return 0f;
            foreach (var c in capTips) if (math.distancesq(c.p, p) < 0.25f) return c.r;
            return 0f;
        }

        // ───────────────────────── outline coverage ─────────────────────────

        private static void AddRing(float2[] ring, List<S> roadSegs, SegmentIndex roadIndex, List<BoundaryAttachment> alongEdge, List<S> output)
        {
            int n = ring.Length;
            var pieces = new List<(float2 a, float2 b, int seg)>();
            for (int i = 0; i < n; i++)
            {
                float2 a = ring[i], b = ring[(i + 1) % n];
                float len = math.distance(a, b);
                if (len < 1e-4f) continue;
                int k = math.max(1, (int)math.ceil(len / Chord));
                for (int j = 0; j < k; j++) pieces.Add((math.lerp(a, b, (float)j / k), math.lerp(a, b, (float)(j + 1) / k), i));
            }
            int m = pieces.Count;
            var covered = new bool[m];
            var width = new float[m];
            var scratch = new List<int>();
            var seen = new HashSet<int>();
            for (int i = 0; i < m; i++)
            {
                var (a, b, seg) = pieces[i];
                float2 mid = 0.5f * (a + b);
                float2 dir = math.normalizesafe(b - a);
                bool all = true;
                float w = 0f;
                foreach (float2 p in new[] { a, mid, b })
                {
                    roadIndex.Query(p - 40f, p + 40f, scratch, seen);
                    // Covered = some road running alongside (parallel) passes this point; a road crossing
                    // the outline covers the crossing point, not the outline.
                    bool alongside = false;
                    foreach (int id in scratch)
                    {
                        float d = Seg.Dist(p, roadIndex.A[id], roadIndex.B[id]);
                        float hw = roadSegs[roadIndex.Owner[id]].W;
                        float2 rd = math.normalizesafe(roadIndex.B[id] - roadIndex.A[id]);
                        if (math.abs(math.dot(dir, rd)) <= 0.7f) continue;
                        // A road alongside the outline eats into the land up to its curb.
                        w = math.max(w, hw - d);
                        if (d <= math.max(2f, 0.6f * hw)) alongside = true;
                    }
                    if (!alongside) all = false;
                }
                covered[i] = all;
                width[i] = math.max(0f, w);
                if (alongEdge != null)
                    foreach (var att in alongEdge)
                    {
                        if (att.Kind != AttachmentKind.RoadAlongEdge || att.RingSegment != seg || !(att.RoadWidth > 0f)) continue;
                        float d = Seg.Dist(att.Position.xz, ring[seg], ring[(seg + 1) % n]);
                        width[i] = math.max(width[i], att.RoadWidth * 0.5f - d);
                    }
            }
            bool anyCovered = false;
            foreach (bool c in covered) anyCovered |= c;
            for (int i = 0; i < m; i++)
            {
                if (covered[i]) continue;
                output.Add(new S { A = pieces[i].a, B = pieces[i].b, W = width[i], Kind = KindRing });
                if (!anyCovered) continue;
                // Where uncovered outline meets covered outline, tie it to the road so the face closes.
                if (covered[(i - 1 + m) % m]) Connect(pieces[i].a, roadIndex, scratch, seen, output);
                if (covered[(i + 1) % m]) Connect(pieces[i].b, roadIndex, scratch, seen, output);
            }
        }

        private static void Connect(float2 p, SegmentIndex roadIndex, List<int> scratch, HashSet<int> seen, List<S> output)
        {
            int id = roadIndex.Nearest(p, 40f, out float d, out float t, scratch, seen);
            if (id < 0 || d < Weld) return;
            float2 q = math.lerp(roadIndex.A[id], roadIndex.B[id], t);
            output.Add(new S { A = p, B = q, W = 0f, Kind = KindConnector });
        }

        // ───────────────────────── planar graph ─────────────────────────

        private static Graph BuildGraph(List<S> segs)
        {
            int n = segs.Count;
            var index = new SegmentIndex(16f);
            for (int i = 0; i < n; i++) index.Add(segs[i].A, segs[i].B, i);
            var splits = new List<float>[n];
            for (int i = 0; i < n; i++) splits[i] = new List<float> { 0f, 1f };
            var scratch = new List<int>();
            var seen = new HashSet<int>();
            for (int i = 0; i < n; i++)
            {
                float2 a = segs[i].A, b = segs[i].B;
                index.Query(math.min(a, b) - Weld, math.max(a, b) + Weld, scratch, seen);
                foreach (int j in scratch)
                {
                    if (j <= i) continue;
                    float2 c = segs[j].A, d = segs[j].B;
                    if (Seg.Intersect(a, b, c, d, out float ta, out float tb))
                    {
                        splits[i].Add(math.saturate(ta));
                        splits[j].Add(math.saturate(tb));
                    }
                    // T-junctions and collinear overlaps: an end point lying on the other segment.
                    TrySplitAt(c, a, b, splits[i]);
                    TrySplitAt(d, a, b, splits[i]);
                    TrySplitAt(a, c, d, splits[j]);
                    TrySplitAt(b, c, d, splits[j]);
                }
            }

            var g = new Graph();
            var cells = new Dictionary<long, List<int>>();
            int Node(float2 p)
            {
                int cx = (int)math.floor(p.x), cy = (int)math.floor(p.y);
                for (int dx = -1; dx <= 1; dx++)
                    for (int dy = -1; dy <= 1; dy++)
                        if (cells.TryGetValue(((long)(cx + dx) << 32) ^ (uint)(cy + dy), out var list))
                            foreach (int id in list)
                                if (math.distancesq(g.Pos[id], p) <= Weld * Weld) return id;
                g.Pos.Add(p);
                long k = ((long)cx << 32) ^ (uint)cy;
                if (!cells.TryGetValue(k, out var l)) cells[k] = l = new List<int>();
                l.Add(g.Pos.Count - 1);
                return g.Pos.Count - 1;
            }
            var byPair = new Dictionary<(int, int), int>();
            for (int i = 0; i < n; i++)
            {
                var sp = splits[i];
                sp.Sort();
                for (int k = 0; k + 1 < sp.Count; k++)
                {
                    if (sp[k + 1] - sp[k] < 1e-6f) continue;
                    float2 p = math.lerp(segs[i].A, segs[i].B, sp[k]);
                    float2 q = math.lerp(segs[i].A, segs[i].B, sp[k + 1]);
                    int u = Node(p), v = Node(q);
                    if (u == v) continue;
                    var key = u < v ? (u, v) : (v, u);
                    var s = segs[i];
                    if (byPair.TryGetValue(key, out int ei))
                    {
                        // Duplicate (overlapping) pieces: roads win over the outline, wider wins.
                        var e = g.Edges[ei];
                        bool road = s.Kind <= KindExisting, eRoad = e.kind <= KindExisting;
                        if ((road && !eRoad) || (road == eRoad && s.W > e.w))
                            g.Edges[ei] = (e.u, e.v, math.max(s.W, e.w), s.Kind, s.Role);
                        else if (s.W > e.w)
                            g.Edges[ei] = (e.u, e.v, s.W, e.kind, e.role);
                        continue;
                    }
                    byPair[key] = g.Edges.Count;
                    g.Edges.Add((u, v, s.W, s.Kind, s.Role));
                }
            }
            g.Adj = new List<int>[g.Pos.Count];
            for (int i = 0; i < g.Adj.Length; i++) g.Adj[i] = new List<int>(4);
            for (int i = 0; i < g.Edges.Count; i++) { g.Adj[g.Edges[i].u].Add(i); g.Adj[g.Edges[i].v].Add(i); }
            g.Dead = new bool[g.Edges.Count];
            return g;
        }

        private static void TrySplitAt(float2 p, float2 a, float2 b, List<float> splits)
        {
            float d = Seg.DistSq(p, a, b, out float t);
            if (d <= Weld * Weld && t > 1e-5f && t < 1f - 1e-5f) splits.Add(t);
        }

        private static int Degree(Graph g, int n)
        {
            int d = 0;
            foreach (int e in g.Adj[n]) if (!g.Dead[e]) d++;
            return d;
        }

        /// <summary>
        /// Removes dangling chains that are not real dead-end roads: loose outline/connector pieces and
        /// existing roads that stop at or beyond the outline (they were clipped there).
        /// </summary>
        private static void PruneDangles(Graph g, float2[] ring)
        {
            var queue = new ListQueue<int>();
            for (int n = 0; n < g.Pos.Count; n++) if (Degree(g, n) == 1) queue.Enqueue(n);
            while (queue.Count > 0)
            {
                int n = queue.Dequeue();
                if (Degree(g, n) != 1) continue;
                int ei = -1;
                foreach (int e in g.Adj[n]) if (!g.Dead[e]) { ei = e; break; }
                var edge = g.Edges[ei];
                bool remove = edge.kind == KindRing || edge.kind == KindConnector
                    || (edge.kind == KindExisting && (!Poly.Contains(ring, g.Pos[n]) || Poly.DistanceToBoundary(ring, g.Pos[n]) < 1.5f));
                if (!remove) continue;
                g.Dead[ei] = true;
                int other = edge.u == n ? edge.v : edge.u;
                if (Degree(g, other) == 1) queue.Enqueue(other);
            }
        }

        /// <summary>
        /// Joins every component to its nearest neighbour component with a thin connector (areas
        /// cannot have holes). Returns true when connectors were added and the graph must be rebuilt.
        /// </summary>
        private static bool Bridge(Graph g, List<S> segs)
        {
            int nodes = g.Pos.Count;
            var comp = new int[nodes];
            for (int i = 0; i < nodes; i++) comp[i] = -1;
            int count = 0;
            var stack = new ListStack<int>();
            var sizes = new List<float>();
            for (int s = 0; s < nodes; s++)
            {
                if (comp[s] >= 0 || Degree(g, s) == 0) continue;
                float2 mn = g.Pos[s], mx = g.Pos[s];
                comp[s] = count; stack.Push(s);
                while (stack.Count > 0)
                {
                    int n = stack.Pop();
                    mn = math.min(mn, g.Pos[n]); mx = math.max(mx, g.Pos[n]);
                    foreach (int e in g.Adj[n])
                    {
                        if (g.Dead[e]) continue;
                        int o = g.Edges[e].u == n ? g.Edges[e].v : g.Edges[e].u;
                        if (comp[o] < 0) { comp[o] = count; stack.Push(o); }
                    }
                }
                sizes.Add(math.lengthsq(mx - mn));
                count++;
            }
            if (count <= 1) return false;
            int main = 0;
            for (int c = 1; c < count; c++) if (sizes[c] > sizes[main]) main = c;
            bool added = false;
            for (int c = 0; c < count; c++)
            {
                if (c == main) continue;
                // Nearest point of any other component to a node of this one.
                float best = float.MaxValue;
                float2 from = default, to = default;
                for (int n = 0; n < nodes; n++)
                {
                    if (comp[n] != c) continue;
                    for (int e = 0; e < g.Edges.Count; e++)
                    {
                        if (g.Dead[e] || comp[g.Edges[e].u] == c) continue;
                        float d = Seg.DistSq(g.Pos[n], g.Pos[g.Edges[e].u], g.Pos[g.Edges[e].v], out float t);
                        if (d < best)
                        {
                            best = d;
                            from = g.Pos[n];
                            to = math.lerp(g.Pos[g.Edges[e].u], g.Pos[g.Edges[e].v], t);
                        }
                    }
                }
                if (best == float.MaxValue || best < Weld * Weld) continue;
                segs.Add(new S { A = from, B = to, W = BridgeHalfWidth, Kind = KindConnector });
                added = true;
            }
            return added;
        }

        // ───────────────────────── faces ─────────────────────────

        /// <summary>Bounded faces as lists of half-edges (edge*2 = u→v, edge*2+1 = v→u), interior on the left.</summary>
        private static List<List<int>> Faces(Graph g)
        {
            int nodes = g.Pos.Count;
            // Outgoing half-edges per node sorted by angle (counter-clockwise).
            var outgoing = new List<(float angle, int he)>[nodes];
            for (int n = 0; n < nodes; n++)
            {
                var list = new List<(float, int)>();
                foreach (int e in g.Adj[n])
                {
                    if (g.Dead[e]) continue;
                    var ed = g.Edges[e];
                    int he = ed.u == n ? e * 2 : e * 2 + 1;
                    int to = ed.u == n ? ed.v : ed.u;
                    float2 d = g.Pos[to] - g.Pos[n];
                    list.Add((math.atan2(d.y, d.x), he));
                }
                list.Sort((x, y) => x.Item1 != y.Item1 ? x.Item1.CompareTo(y.Item1) : x.Item2.CompareTo(y.Item2));
                outgoing[n] = list;
            }
            int From(int he) => (he & 1) == 0 ? g.Edges[he >> 1].u : g.Edges[he >> 1].v;
            int To(int he) => (he & 1) == 0 ? g.Edges[he >> 1].v : g.Edges[he >> 1].u;

            int hcount = g.Edges.Count * 2;
            var next = new int[hcount];
            for (int i = 0; i < hcount; i++) next[i] = -1;
            for (int n = 0; n < nodes; n++)
            {
                var list = outgoing[n];
                int k = list.Count;
                for (int i = 0; i < k; i++)
                {
                    // Arriving at n along the reverse of list[i]: leave on the next edge clockwise from it.
                    int incoming = list[i].Item2 ^ 1;
                    int nextOut = list[(i - 1 + k) % k].Item2;
                    next[incoming] = nextOut;
                }
            }

            var faces = new List<List<int>>();
            var used = new bool[hcount];
            for (int start = 0; start < hcount; start++)
            {
                if (used[start] || g.Dead[start >> 1] || next[start] < 0) continue;
                var face = new List<int>();
                int he = start;
                int guard = hcount + 1;
                while (!used[he] && guard-- > 0)
                {
                    used[he] = true;
                    face.Add(he);
                    he = next[he];
                    if (he < 0) break;
                }
                if (he != start || face.Count < 3) continue;
                double area = 0;
                foreach (int h in face)
                {
                    float2 a = g.Pos[From(h)], b = g.Pos[To(h)];
                    area += (double)a.x * b.y - (double)b.x * a.y;
                }
                if (area * 0.5 > MinBlockArea * 0.5) faces.Add(face);
            }
            return faces;
        }

        // ───────────────────────── inset ─────────────────────────

        private enum JoinKind { Mitre, Round, Cap, Step }

        private struct OffsetLine
        {
            public float2 P, D;
            public float W;
            public int Src;
        }

        private struct Join
        {
            public float EndA, StartB;
            public JoinKind Kind;
            public float2 Center;
            public float Radius;
        }

        /// <summary>
        /// Shrinks a face (CCW, edge i = pts[i]→pts[i+1] with half-width widths[i]). Each edge becomes an
        /// offset line; convex corners are mitred (exact), reflex corners and dead-end tips are rounded
        /// (caps[i] = turning-circle radius at pts[i], 0 = plain road end). Offset edges that come out
        /// reversed (source edge shorter than the inset can bear) are collapsed and their neighbours
        /// re-joined, like an edge event of a straight skeleton; remaining self-intersections are split
        /// apart and only positively oriented loops are kept.
        /// </summary>
        public static List<List<float2>> Inset(List<float2> facePts, List<float> faceWidths, List<float> faceCaps)
        {
            ExpandCaps(facePts, faceWidths, faceCaps, out var pts, out var widths);
            int n = pts.Count;
            var result = new List<List<float2>>();
            if (n < 3) return result;

            var lines = new OffsetLine[n];
            for (int i = 0; i < n; i++)
            {
                float2 d = math.normalizesafe(pts[(i + 1) % n] - pts[i]);
                lines[i] = new OffsetLine { P = pts[i] + new float2(-d.y, d.x) * widths[i], D = d, W = widths[i], Src = i };
            }
            var prev = new int[n];
            var next = new int[n];
            for (int i = 0; i < n; i++) { prev[i] = (i - 1 + n) % n; next[i] = (i + 1) % n; }
            var alive = new bool[n];
            for (int i = 0; i < n; i++) alive[i] = true;
            int aliveCount = n;

            Join JoinOf(int a, int b)
            {
                var la = lines[a];
                var lb = lines[b];
                float dot = math.dot(la.D, lb.D), cross = Poly.Cross(la.D, lb.D);
                bool shared = (la.Src + 1) % n == lb.Src;
                float2 v = pts[lb.Src];
                var j = new Join { Kind = JoinKind.Mitre };
                if (shared && dot < -0.999f)
                {
                    float r = math.max(la.W, lb.W);
                    j.Kind = r > 1e-4f ? JoinKind.Cap : JoinKind.Step;
                    j.Center = v; j.Radius = r;
                    j.EndA = math.dot(v - la.P, la.D) - math.sqrt(math.max(0f, r * r - la.W * la.W));
                    j.StartB = math.dot(v - lb.P, lb.D) + math.sqrt(math.max(0f, r * r - lb.W * lb.W));
                    return j;
                }
                if (math.abs(cross) < 1e-4f || (!shared && math.abs(cross) < 0.05f))
                {
                    j.Kind = JoinKind.Step;
                    j.EndA = math.dot(v - la.P, la.D);
                    j.StartB = math.dot(v - lb.P, lb.D);
                    return j;
                }
                if (shared && cross < 0f && dot <= 0.866f && (la.W > 1e-4f || lb.W > 1e-4f))
                {
                    j.Kind = JoinKind.Round;
                    j.Center = v; j.Radius = math.max(la.W, lb.W);
                    j.EndA = math.dot(v - la.P, la.D);
                    j.StartB = math.dot(v - lb.P, lb.D);
                    return j;
                }
                // Mitre: intersection of the two offset lines (exact for convex corners, conservative for reflex).
                float t = Poly.Cross(lb.P - la.P, lb.D) / cross;
                float2 x = la.P + la.D * t;
                j.EndA = t;
                j.StartB = math.dot(x - lb.P, lb.D);
                return j;
            }

            // Edge collapse: an offset edge whose end comes before its start has vanished.
            int guard = 4 * n + 16;
            bool changed = true;
            while (changed && aliveCount >= 3 && guard-- > 0)
            {
                changed = false;
                for (int i = 0; i < n && aliveCount >= 3; i++)
                {
                    if (!alive[i]) continue;
                    float s = JoinOf(prev[i], i).StartB;
                    float e = JoinOf(i, next[i]).EndA;
                    if (e >= s - 1e-3f) continue;
                    alive[i] = false;
                    aliveCount--;
                    next[prev[i]] = next[i];
                    prev[next[i]] = prev[i];
                    changed = true;
                }
            }
            if (aliveCount < 3) return result;

            int first = 0;
            while (!alive[first]) first++;
            var raw = new List<float2>(aliveCount * 2);
            int cur = first;
            do
            {
                int nx = next[cur];
                var j = JoinOf(cur, nx);
                var la = lines[cur];
                var lb = lines[nx];
                float2 endA = la.P + la.D * j.EndA, startB = lb.P + lb.D * j.StartB;
                raw.Add(endA);
                if (j.Kind == JoinKind.Round || j.Kind == JoinKind.Cap) Arc(raw, j.Center, endA, startB, j.Radius);
                if (j.Kind != JoinKind.Mitre) raw.Add(startB);
                cur = nx;
            } while (cur != first);

            foreach (var loop in SplitSelfIntersections(Dedup(raw)))
                if (loop.Count >= 3 && Poly.SignedArea(loop) > MinBlockArea) result.Add(loop);
            return result;
        }

        /// <summary>
        /// Turning circles larger than the road: the dead-end tip is replaced by a small half-circle
        /// of radius (cap − width) so the regular inset by the road width yields the full circle.
        /// </summary>
        private static void ExpandCaps(List<float2> pts, List<float> widths, List<float> caps, out List<float2> outPts, out List<float> outWidths)
        {
            int n = pts.Count;
            outPts = new List<float2>(n + 16);
            outWidths = new List<float>(n + 16);
            for (int j = 0; j < n; j++)
            {
                float2 v = pts[j];
                float w0 = widths[(j - 1 + n) % n], w1 = widths[j];
                float2 d0 = math.normalizesafe(v - pts[(j - 1 + n) % n]), d1 = math.normalizesafe(pts[(j + 1) % n] - v);
                float rho = caps[j] - math.max(w0, w1);
                if (math.dot(d0, d1) < -0.999f && rho > 0.5f)
                {
                    // Clockwise half circle from the left of the incoming direction, around the tip.
                    float2 n0 = new float2(-d0.y, d0.x);
                    float a0 = math.atan2(n0.y, n0.x);
                    const int steps = 8;
                    for (int k = 0; k <= steps; k++)
                    {
                        float a = a0 - math.PI * k / steps;
                        outPts.Add(v + rho * new float2(math.cos(a), math.sin(a)));
                        outWidths.Add(k < steps ? math.max(w0, w1) : w1);
                    }
                    continue;
                }
                outPts.Add(v);
                outWidths.Add(w1);
            }
        }

        /// <summary>Clockwise arc around c from s to e (exclusive), radius r, ≤ 15° steps.</summary>
        private static void Arc(List<float2> output, float2 c, float2 s, float2 e, float r)
        {
            float a0 = math.atan2(s.y - c.y, s.x - c.x), a1 = math.atan2(e.y - c.y, e.x - c.x);
            float sweep = a0 - a1;
            while (sweep <= 0f) sweep += 2f * math.PI;
            while (sweep > 2f * math.PI) sweep -= 2f * math.PI;
            int steps = (int)math.ceil(sweep / math.radians(15f));
            for (int i = 1; i < steps; i++)
            {
                float a = a0 - sweep * i / steps;
                output.Add(c + r * new float2(math.cos(a), math.sin(a)));
            }
        }

        private static List<float2> Dedup(List<float2> pts)
        {
            var r = new List<float2>(pts.Count);
            foreach (var p in pts)
                if (!(math.all(math.isfinite(p)))) continue;
                else if (r.Count == 0 || math.distancesq(r[r.Count - 1], p) > 1e-6f) r.Add(p);
            while (r.Count > 1 && math.distancesq(r[0], r[r.Count - 1]) <= 1e-6f) r.RemoveAt(r.Count - 1);
            RemoveSpikes(r);
            return r;
        }

        /// <summary>
        /// Removes zero-width back-and-forth spikes (A→B→A along one line). They appear when an edge is
        /// shorter than the inset and are invisible to crossing tests because the pieces are collinear.
        /// </summary>
        private static void RemoveSpikes(List<float2> r)
        {
            bool changed = true;
            int guard = r.Count * 4 + 8;
            while (changed && r.Count >= 3 && guard-- > 0)
            {
                changed = false;
                for (int i = 0; i < r.Count && r.Count >= 3; i++)
                {
                    float2 a = r[(i - 1 + r.Count) % r.Count], b = r[i], c = r[(i + 1) % r.Count];
                    float2 u = b - a, v = c - b;
                    float lu = math.length(u), lv = math.length(v);
                    if (lu < 1e-4f || lv < 1e-4f) { r.RemoveAt(i); changed = true; i--; continue; }
                    if (math.dot(u, v) < 0f && math.abs(Poly.Cross(u, v)) <= 1e-3f * lu * lv)
                    {
                        r.RemoveAt(i);
                        changed = true;
                        i--;
                    }
                }
            }
        }

        /// <summary>Splits a closed polyline at its self-intersections into simple loops.</summary>
        public static List<List<float2>> SplitSelfIntersections(List<float2> pts)
        {
            var done = new List<List<float2>>();
            var stack = new ListStack<List<float2>>();
            stack.Push(pts);
            int budget = 400;
            while (stack.Count > 0)
            {
                var loop = stack.Pop();
                if (loop.Count < 3) continue;
                if (budget-- <= 0 || !FirstCrossing(loop, out int i, out int j, out float2 p))
                {
                    if (budget > 0) done.Add(loop);
                    continue;
                }
                int n = loop.Count;
                var a = new List<float2> { p };
                for (int k = i + 1; k <= j; k++) a.Add(loop[k]);
                var b = new List<float2> { p };
                for (int k = j + 1; k < n; k++) b.Add(loop[k]);
                for (int k = 0; k <= i; k++) b.Add(loop[k]);
                stack.Push(Dedup(a));
                stack.Push(Dedup(b));
            }
            return done;
        }

        /// <summary>First pair of non-adjacent crossing edges (edge i = loop[i]→loop[i+1], i &lt; j), sweep on x.</summary>
        private static bool FirstCrossing(List<float2> loop, out int bi, out int bj, out float2 point)
        {
            int n = loop.Count;
            bi = bj = -1; point = default;
            var order = new int[n];
            var minX = new float[n];
            var maxX = new float[n];
            for (int i = 0; i < n; i++)
            {
                order[i] = i;
                float2 a = loop[i], b = loop[(i + 1) % n];
                minX[i] = math.min(a.x, b.x); maxX[i] = math.max(a.x, b.x);
            }
            Array.Sort(order, (x, y) => minX[x] != minX[y] ? minX[x].CompareTo(minX[y]) : x.CompareTo(y));
            for (int oi = 0; oi < n; oi++)
            {
                int i = order[oi];
                float2 a = loop[i], b = loop[(i + 1) % n];
                float yMin = math.min(a.y, b.y), yMax = math.max(a.y, b.y);
                for (int oj = oi + 1; oj < n && minX[order[oj]] <= maxX[i]; oj++)
                {
                    int j = order[oj];
                    if (j == i || (j + 1) % n == i || (i + 1) % n == j) continue;
                    float2 c = loop[j], d = loop[(j + 1) % n];
                    if (math.max(c.y, d.y) < yMin || math.min(c.y, d.y) > yMax) continue;
                    if (!Seg.Intersect(a, b, c, d, out float ta, out _)) continue;
                    int lo = math.min(i, j), hi = math.max(i, j);
                    if (bi < 0 || lo < bi || (lo == bi && hi < bj))
                    {
                        bi = lo; bj = hi;
                        point = math.lerp(a, b, math.saturate(ta));
                    }
                }
            }
            return bi >= 0;
        }

        /// <summary>Rejects offset artefacts: every vertex must keep its distance from the face's roads.</summary>
        private static bool Validate(List<float2> loop, List<float2> face, List<float> widths)
        {
            int n = face.Count;
            float maxW = 0f;
            var index = new SegmentIndex(16f);
            for (int i = 0; i < n; i++)
            {
                if (widths[i] <= 0.05f) continue;
                maxW = math.max(maxW, widths[i]);
                index.Add(face[i], face[(i + 1) % n], i);
            }
            var scratch = new List<int>();
            var seen = new HashSet<int>();
            if (maxW > 0f)
                foreach (float2 p in loop)
                {
                    index.Query(p - maxW, p + maxW, scratch, seen);
                    foreach (int id in scratch)
                    {
                        int i = index.Owner[id];
                        // Only the corridor beside a segment counts: around a junction point the road ends
                        // in the crossing road, not in a disc (a T junction has no bulge on its far side).
                        float d = math.sqrt(Seg.DistSq(p, face[i], face[(i + 1) % n], out float t));
                        if (t <= 1e-4f || t >= 1f - 1e-4f) continue;
                        if (d < widths[i] - 0.25f) return false;
                    }
                }
            float2 c = Poly.Centroid(loop);
            return Poly.Contains(face, c) || Poly.Contains(face, loop[0]);
        }

        /// <summary>Douglas–Peucker on a closed ring (keeps the extreme points of the bounding box as anchors).</summary>
        public static float2[] Simplify(float2[] ring, float tolerance)
        {
            int n = ring.Length;
            if (n <= 4) return ring;
            int far = 0;
            for (int i = 1; i < n; i++) if (math.distancesq(ring[i], ring[0]) > math.distancesq(ring[far], ring[0])) far = i;
            var keep = new bool[n];
            keep[0] = keep[far] = true;
            DP(ring, 0, far, tolerance, keep);
            DP(ring, far, n, tolerance, keep);
            var r = new List<float2>(n);
            for (int i = 0; i < n; i++) if (keep[i]) r.Add(ring[i]);
            return r.Count >= 3 ? r.ToArray() : ring;
        }

        private static void DP(float2[] ring, int first, int last, float tol, bool[] keep)
        {
            int n = ring.Length;
            if (last - first < 2) return;
            float2 a = ring[first], b = ring[last % n];
            float best = -1f; int idx = -1;
            for (int i = first + 1; i < last; i++)
            {
                float d = Seg.Dist(ring[i], a, b);
                if (d > best) { best = d; idx = i; }
            }
            if (best > tol)
            {
                keep[idx] = true;
                DP(ring, first, idx, tol, keep);
                DP(ring, idx, last, tol, keep);
            }
        }

        // ───────────────────────── ordering & frontage ─────────────────────────

        /// <summary>Principal axis of a ring (vertex PCA), canonicalised so +x-ish wins.</summary>
        public static float2 PrincipalAxis(IReadOnlyList<float2> ring)
        {
            float2 c = Poly.Centroid(ring);
            double sxx = 0, syy = 0, sxy = 0;
            foreach (var p in ring) { double dx = p.x - c.x, dy = p.y - c.y; sxx += dx * dx; syy += dy * dy; sxy += dx * dy; }
            double angle = 0.5 * Math.Atan2(2 * sxy, sxx - syy);
            var axis = new float2((float)Math.Cos(angle), (float)Math.Sin(angle));
            if (axis.x < -1e-4f || (math.abs(axis.x) <= 1e-4f && axis.y < 0)) axis = -axis;
            return math.normalizesafe(axis, new float2(1, 0));
        }

        /// <summary>Sorts blocks along the principal axis (then across it) and rotates each ring to a stable start vertex.</summary>
        public static void Order(List<Block> blocks, float2 axis)
        {
            float2 across = new float2(-axis.y, axis.x);
            blocks.Sort((x, y) =>
            {
                double ux = MathEx.Round(math.dot(x.Centroid, axis), 1), uy = MathEx.Round(math.dot(y.Centroid, axis), 1);
                if (ux != uy) return ux.CompareTo(uy);
                return math.dot(x.Centroid, across).CompareTo(math.dot(y.Centroid, across));
            });
            for (int b = 0; b < blocks.Count; b++)
            {
                var ring = blocks[b].Ring;
                int start = 0;
                for (int i = 1; i < ring.Length; i++)
                {
                    float si = math.dot(ring[i], axis), ss = math.dot(ring[start], axis);
                    if (si < ss - 1e-3f || (math.abs(si - ss) <= 1e-3f && math.dot(ring[i], across) < math.dot(ring[start], across))) start = i;
                }
                if (start == 0) continue;
                var r = new float2[ring.Length];
                for (int i = 0; i < ring.Length; i++) r[i] = ring[(start + i) % ring.Length];
                var bl = blocks[b]; bl.Ring = r; blocks[b] = bl;
            }
        }

        /// <summary>
        /// Frontage strips of a block: the block edges that face a generated road, cut into pieces of at
        /// most 80 m / 45° of turning (and at corners sharper than 50°). Each piece becomes a strip
        /// reaching `depth` into the block, never more than half the block's local thickness, so strips
        /// from opposite sides do not overlap; neighbouring pieces meet on the corner bisector.
        /// </summary>
        public static List<float2[]> Frontage(float2[] block, SegmentIndex roads, List<float> roadHalfWidths, float depth)
        {
            var result = new List<float2[]>();
            int n = block.Length;
            if (n < 3 || depth <= 0f) return result;
            var front = new bool[n];
            var scratch = new List<int>();
            var seen = new HashSet<int>();
            int count = 0;
            for (int i = 0; i < n; i++)
            {
                float2 mid = 0.5f * (block[i] + block[(i + 1) % n]);
                int id = roads.Nearest(mid, 48f, out float d, out _, scratch, seen);
                if (id < 0) continue;
                if (d <= roadHalfWidths[roads.Owner[id]] + 1.5f) { front[i] = true; count++; }
            }
            if (count == 0) return result;

            float Turn(int v)
            {
                float2 d0 = math.normalizesafe(block[v] - block[(v - 1 + n) % n]), d1 = math.normalizesafe(block[(v + 1) % n] - block[v]);
                return math.degrees(math.acos(math.clamp(math.dot(d0, d1), -1f, 1f)));
            }
            // Start at a run start, or (closed run) at the sharpest corner.
            int first = -1;
            for (int i = 0; i < n && first < 0; i++) if (front[i] && !front[(i - 1 + n) % n]) first = i;
            if (first < 0)
            {
                first = 0;
                for (int i = 1; i < n; i++) if (Turn(i) > Turn(first)) first = i;
            }

            var ringIndex = new RingIndex(block);
            var pieces = new List<List<int>>();
            List<int> piece = null;
            float length = 0f, turn = 0f;
            for (int k = 0; k < n; k++)
            {
                int i = (first + k) % n;
                if (!front[i]) { if (piece != null) { pieces.Add(piece); piece = null; } continue; }
                float t = Turn(i);
                if (piece != null && (t > 50f || length >= 80f || turn + t > 45f)) { pieces.Add(piece); piece = null; }
                if (piece == null) { piece = new List<int>(); length = 0f; turn = 0f; }
                else turn += t;
                piece.Add(i);
                length += math.distance(block[i], block[(i + 1) % n]);
            }
            if (piece != null) pieces.Add(piece);

            foreach (var pc in pieces)
            {
                var p = new List<float2>(pc.Count + 1);
                var dirs = new List<float2>(pc.Count + 1);
                foreach (int i in pc) p.Add(block[i]);
                int last = (pc[pc.Count - 1] + 1) % n;
                p.Add(block[last]);
                float runLength = 0f;
                for (int i = 0; i + 1 < p.Count; i++) runLength += math.distance(p[i], p[i + 1]);
                if (runLength < 8f) continue;
                // Inward offset direction at each point: bisector inside the piece and towards a neighbouring
                // frontage piece (shared corner), the piece's own normal at a free end.
                for (int k = 0; k < p.Count; k++)
                {
                    int v = k < pc.Count ? pc[k] : last;
                    int ePrev = (v - 1 + n) % n, eNext = v;
                    bool usePrev = k > 0 || front[ePrev];
                    bool useNext = k < pc.Count || front[eNext];
                    float2 nPrev = Left(block[(ePrev + 1) % n] - block[ePrev]), nNext = Left(block[(eNext + 1) % n] - block[eNext]);
                    float2 bis = usePrev && useNext ? math.normalizesafe(nPrev + nNext, nNext) : usePrev ? nPrev : nNext;
                    float cos = math.max(0.5f, math.dot(bis, useNext ? nNext : nPrev));
                    dirs.Add(bis / cos);
                }
                // Thickness and containment are probed at a handful of points per piece: blocks can have
                // thousands of vertices and a full scan per vertex made frontage quadratic (seconds).
                var probes = new List<int>();
                int stride = math.max(1, (p.Count - 1) / 4);
                for (int k = 0; k < p.Count; k += stride) probes.Add(k);
                if (probes[probes.Count - 1] != p.Count - 1) probes.Add(p.Count - 1);
                float limit = depth;
                foreach (int k in probes)
                {
                    float2 nrm = math.normalizesafe(dirs[k]);
                    float2 start = k == 0 || k == p.Count - 1 ? math.lerp(p[k], p[k == 0 ? 1 : k - 1], 0.02f) : p[k];
                    float thick = ringIndex.Ray(start + nrm * 0.05f, nrm, 2f * depth + 1f);
                    limit = math.min(limit, 0.5f * thick / math.max(1f, math.length(dirs[k])));
                }
                for (float d = limit; d >= 6f; d *= 0.8f)
                {
                    var ring = new float2[2 * p.Count];
                    for (int k = 0; k < p.Count; k++)
                    {
                        ring[k] = p[k];
                        ring[2 * p.Count - 1 - k] = p[k] + dirs[k] * d;
                    }
                    var clean = Poly.Clean(ring, SurfaceAcceptance.MinEdge);
                    if (clean.Length < 3 || !Poly.IsSimple(clean) || Poly.SignedArea(clean) < 20f) continue;
                    bool inside = true;
                    foreach (int k in probes)
                    {
                        if (!inside) break;
                        float2 q = p[k] + dirs[k] * d;
                        if (!ringIndex.Contains(q) && ringIndex.Distance(q, 0.1f) > 0.05f) inside = false;
                    }
                    if (!inside) continue;
                    result.Add(clean);
                    break;
                }
            }
            return result;
        }

        /// <summary>Grid index over a ring's edges: containment, ray and distance queries in ~O(1).</summary>
        private sealed class RingIndex
        {
            private readonly SegmentIndex _index = new SegmentIndex(8f);
            private readonly List<int> _scratch = new List<int>();
            private readonly HashSet<int> _seen = new HashSet<int>();
            private readonly float _maxX;

            public RingIndex(float2[] ring)
            {
                _maxX = float.MinValue;
                for (int i = 0; i < ring.Length; i++)
                {
                    _index.Add(ring[i], ring[(i + 1) % ring.Length], i);
                    _maxX = math.max(_maxX, ring[i].x);
                }
            }

            /// <summary>Even-odd test with a +x ray, only over the edges in that row.</summary>
            public bool Contains(float2 p)
            {
                _index.Query(p, new float2(_maxX + 1f, p.y), _scratch, _seen);
                bool inside = false;
                foreach (int id in _scratch)
                {
                    float2 a = _index.A[id], b = _index.B[id];
                    if ((a.y > p.y) != (b.y > p.y) && p.x < (b.x - a.x) * (p.y - a.y) / (b.y - a.y) + a.x) inside = !inside;
                }
                return inside;
            }

            /// <summary>Distance along dir to the first edge crossing, at most maxLength (returned when none).</summary>
            public float Ray(float2 p, float2 dir, float maxLength)
            {
                float2 far = p + dir * maxLength;
                _index.Query(math.min(p, far), math.max(p, far), _scratch, _seen);
                float best = maxLength;
                foreach (int id in _scratch)
                    if (Seg.Intersect(p, far, _index.A[id], _index.B[id], out float t, out _) && t > 1e-6f)
                        best = math.min(best, t * maxLength);
                return best;
            }

            public float Distance(float2 p, float maxDist)
            {
                int id = _index.Nearest(p, maxDist, out float d, out _, _scratch, _seen);
                return id < 0 ? float.MaxValue : d;
            }
        }

        private static float2 Left(float2 d)
        {
            d = math.normalizesafe(d);
            return new float2(-d.y, d.x);
        }
    }
}
