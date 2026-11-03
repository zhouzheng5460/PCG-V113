// Placemaker roads layer (s34) — graph helpers shared by the network passes (connectivity, access management,
// walkways, lanes, junction geometry): components, edge splitting, arc-length parameters, crossing and clearance
// queries against the planned network. Pure and deterministic.

using System;
using System.Collections.Generic;
using Placemaker.Core.Geometry;
using Unity.Mathematics;

namespace Placemaker.Core.Layers.Roads
{
    internal static class RoadGraph
    {
        public static bool Car(RoadEdge e) => e.Alive && e.IsCar;
        public static bool Any(RoadEdge e) => e.Alive;

        public static int Other(RoadEdge e, int node) => e.N0 == node ? e.N1 : e.N0;

        /// <summary>Unit XZ direction leaving <paramref name="node"/> along the edge.</summary>
        public static float2 Leaving(RoadEdge e, int node) => e.N0 == node ? e.Curve.TangentXZ(0f) : -e.Curve.TangentXZ(1f);

        /// <summary>Degrees over the edges the filter accepts.</summary>
        public static int[] Degrees(RoadNetwork net, Func<RoadEdge, bool> include)
        {
            var d = new int[net.Nodes.Count];
            foreach (var e in net.Edges)
                if (e.N0 >= 0 && include(e)) { d[e.N0]++; d[e.N1]++; }
            return d;
        }

        public static List<int>[] Incidence(RoadNetwork net, Func<RoadEdge, bool> include)
        {
            var inc = new List<int>[net.Nodes.Count];
            for (int i = 0; i < inc.Length; i++) inc[i] = new List<int>(4);
            for (int i = 0; i < net.Edges.Count; i++)
            {
                var e = net.Edges[i];
                if (e.N0 < 0 || !include(e)) continue;
                inc[e.N0].Add(i);
                if (e.N1 != e.N0) inc[e.N1].Add(i);
            }
            return inc;
        }

        /// <summary>Connected components over the accepted edges: component id per node (-1 = no accepted edge).</summary>
        public static int[] Components(RoadNetwork net, Func<RoadEdge, bool> include, out int count)
        {
            int n = net.Nodes.Count;
            var parent = new int[n];
            for (int i = 0; i < n; i++) parent[i] = i;
            int Find(int x)
            {
                while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; }
                return x;
            }
            var used = new bool[n];
            foreach (var e in net.Edges)
            {
                if (e.N0 < 0 || !include(e)) continue;
                used[e.N0] = used[e.N1] = true;
                int a = Find(e.N0), b = Find(e.N1);
                if (a != b) parent[Math.Max(a, b)] = Math.Min(a, b);
            }
            var id = new int[n];
            var map = new Dictionary<int, int>();
            count = 0;
            for (int i = 0; i < n; i++)
            {
                if (!used[i]) { id[i] = -1; continue; }
                int r = Find(i);
                if (!map.TryGetValue(r, out int c)) { c = count++; map[r] = c; }
                id[i] = c;
            }
            return id;
        }

        /// <summary>Curve parameter at arc length <paramref name="s"/> from the start (from the end when <paramref name="fromEnd"/>).</summary>
        public static float ParamAt(RoadCurve c, float s, bool fromEnd = false)
        {
            const int n = 48;
            float total = 0f;
            var cum = new float[n + 1];
            float2 prev = c.Position(0f).xz;
            for (int i = 1; i <= n; i++)
            {
                float2 p = c.Position((float)i / n).xz;
                total += math.distance(prev, p);
                cum[i] = total;
                prev = p;
            }
            if (total < 1e-4f) return fromEnd ? 1f : 0f;
            float target = math.clamp(fromEnd ? total - s : s, 0f, total);
            for (int i = 1; i <= n; i++)
                if (cum[i] >= target)
                {
                    float seg = cum[i] - cum[i - 1];
                    float f = seg > 1e-6f ? (target - cum[i - 1]) / seg : 0f;
                    return ((i - 1) + f) / n;
                }
            return 1f;
        }

        /// <summary>
        /// Splits edge <paramref name="ei"/> at t: the edge keeps [0,t], a copy takes [t,1] (with the cul-de-sac tip).
        /// Returns the copy's index. Node indices are stale until the caller welds.
        /// </summary>
        public static int Split(RoadNetwork net, int ei, float t)
        {
            var e = net.Edges[ei];
            e.Curve.Split(math.clamp(t, 0.001f, 0.999f), out var left, out var right);
            bool cap = e.CapAtEnd;
            e.Curve = left;
            e.CapAtEnd = false;
            var copy = e.Clone(right);
            copy.CapAtEnd = cap;
            net.Edges.Add(copy);
            return net.Edges.Count - 1;
        }

        /// <summary>Splits at t, welds, and returns the node at the split point.</summary>
        public static int SplitAndNode(RoadNetwork net, int ei, float t)
        {
            var p = net.Edges[ei].Curve.Position(math.clamp(t, 0.001f, 0.999f));
            Split(net, ei, t);
            net.Weld();
            return NodeAt(net, p.xz, 0.2f);
        }

        /// <summary>Index of the node within <paramref name="tolerance"/> of p (closest), -1 = none.</summary>
        public static int NodeAt(RoadNetwork net, float2 p, float tolerance)
        {
            int best = -1;
            float bd = tolerance * tolerance;
            for (int i = 0; i < net.Nodes.Count; i++)
            {
                float d = math.distancesq(net.Nodes[i].Position.xz, p);
                if (d <= bd) { bd = d; best = i; }
            }
            return best;
        }

        /// <summary>Arc length (XZ) of an edge.</summary>
        public static float Length(RoadEdge e) => e.Curve.LengthXZ(12);

        /// <summary>Points of a straight or curved link sampled every <paramref name="step"/> metres (both ends included).</summary>
        public static List<float2> Sample(RoadCurve c, float step)
        {
            float len = c.LengthXZ(12);
            int n = math.clamp((int)math.ceil(len / math.max(0.5f, step)), 1, 256);
            var pts = new List<float2>(n + 1);
            for (int i = 0; i <= n; i++) pts.Add(c.Position((float)i / n).xz);
            return pts;
        }

        /// <summary>Steepest grade (rise/run) over windows of <paramref name="window"/> m along the curve on the ground.</summary>
        public static float MaxGrade(RoadCurve c, Func<float2, float> height, float window = 12f, float step = 4f)
        {
            if (height == null) return 0f;
            var pts = Sample(c, step);
            if (pts.Count < 2) return 0f;
            var h = new float[pts.Count];
            var s = new float[pts.Count];
            for (int i = 0; i < pts.Count; i++)
            {
                h[i] = height(pts[i]);
                if (i > 0) s[i] = s[i - 1] + math.distance(pts[i - 1], pts[i]);
            }
            float total = s[s.Length - 1];
            if (total < 1f) return 0f;
            float w = math.min(window, total);
            float best = 0f;
            int j = 0;
            for (int i = 0; i < pts.Count; i++)
            {
                while (j < pts.Count - 1 && s[j] - s[i] < w) j++;
                float run = s[j] - s[i];
                if (run < w * 0.75f) break;
                best = math.max(best, math.abs(h[j] - h[i]) / run);
            }
            return best;
        }

        /// <summary>Mean grade end to end on the ground (|Δh| / length).</summary>
        public static float MeanGrade(RoadCurve c, Func<float2, float> height)
        {
            if (height == null) return 0f;
            float len = c.LengthXZ(12);
            if (len < 1f) return 0f;
            return math.abs(height(c.D.xz) - height(c.A.xz)) / len;
        }

        /// <summary>Angle (degrees, 0..180) between two directions.</summary>
        public static float Angle(float2 a, float2 b) => math.degrees(math.acos(math.clamp(math.dot(math.normalizesafe(a), math.normalizesafe(b)), -1f, 1f)));

        /// <summary>Smallest angle between <paramref name="dir"/> and the edges leaving <paramref name="node"/> (180 when none).</summary>
        public static float MinAngleAt(RoadNetwork net, List<int>[] inc, int node, float2 dir, int ignoreEdge = -1)
        {
            float best = 180f;
            if (node < 0 || node >= inc.Length) return best;
            foreach (int ei in inc[node])
            {
                if (ei == ignoreEdge) continue;
                best = math.min(best, Angle(Leaving(net.Edges[ei], node), dir));
            }
            return best;
        }

        /// <summary>
        /// Flattened alive edges in a grid index: does a new link cross anything, how close does it come to other roads.
        /// Rebuild after the network changes.
        /// </summary>
        internal sealed class EdgeIndex
        {
            private readonly SegmentIndex _index = new SegmentIndex(24f);
            private readonly List<int> _edge = new List<int>();
            private readonly List<int> _scratch = new List<int>();
            private readonly HashSet<int> _seen = new HashSet<int>();
            private readonly RoadNetwork _net;

            public EdgeIndex(RoadNetwork net, Func<RoadEdge, bool> include)
            {
                _net = net;
                var pts = new List<float3>();
                for (int i = 0; i < net.Edges.Count; i++)
                {
                    var e = net.Edges[i];
                    if (e.N0 < 0 || !include(e)) continue;
                    pts.Clear();
                    e.Curve.Flatten(pts, null, 3f, 6f);
                    for (int k = 0; k + 1 < pts.Count; k++) { _index.Add(pts[k].xz, pts[k + 1].xz, i); }
                }
            }

            /// <summary>
            /// True when the polyline crosses an indexed edge anywhere except within <paramref name="endSkip"/> m of its two
            /// ends (where it joins). Edges in <paramref name="ignore"/> never count.
            /// </summary>
            public bool Crosses(IReadOnlyList<float2> line, float endSkip, ICollection<int> ignore = null)
            {
                float total = 0f;
                var cum = new float[line.Count];
                for (int i = 1; i < line.Count; i++) { total += math.distance(line[i - 1], line[i]); cum[i] = total; }
                for (int i = 0; i + 1 < line.Count; i++)
                {
                    float2 a = line[i], b = line[i + 1];
                    _index.Query(math.min(a, b) - 0.5f, math.max(a, b) + 0.5f, _scratch, _seen);
                    foreach (int sid in _scratch)
                    {
                        int ei = _index.Owner[sid];
                        if (ignore != null && ignore.Contains(ei)) continue;
                        if (!Seg.Intersect(a, b, _index.A[sid], _index.B[sid], out float ta, out _)) continue;
                        float s = math.lerp(cum[i], cum[i + 1], math.saturate(ta));
                        if (s < endSkip || s > total - endSkip) continue;
                        return true;
                    }
                }
                return false;
            }

            /// <summary>
            /// Smallest distance from the polyline (ignoring its first and last <paramref name="endSkip"/> m) to an indexed edge
            /// not in <paramref name="ignore"/>. Returns <paramref name="cap"/> when nothing is closer.
            /// </summary>
            public float Clearance(IReadOnlyList<float2> line, float endSkip, float cap, ICollection<int> ignore = null)
            {
                float best = cap;
                float total = 0f;
                var cum = new float[line.Count];
                for (int i = 1; i < line.Count; i++) { total += math.distance(line[i - 1], line[i]); cum[i] = total; }
                for (int i = 0; i < line.Count; i++)
                {
                    if (cum[i] < endSkip || cum[i] > total - endSkip) continue;
                    float2 p = line[i];
                    _index.Query(p - cap, p + cap, _scratch, _seen);
                    foreach (int sid in _scratch)
                    {
                        int ei = _index.Owner[sid];
                        if (ignore != null && ignore.Contains(ei)) continue;
                        best = math.min(best, Seg.Dist(p, _index.A[sid], _index.B[sid]));
                    }
                }
                return best;
            }

            /// <summary>Indexed edges with a piece within <paramref name="radius"/> of p.</summary>
            public void Near(float2 p, float radius, HashSet<int> into)
            {
                _index.Query(p - radius, p + radius, _scratch, _seen);
                foreach (int sid in _scratch)
                    if (Seg.Dist(p, _index.A[sid], _index.B[sid]) <= radius) into.Add(_index.Owner[sid]);
            }

            /// <summary>First indexed edge hit by the ray p + dir·[minT, maxT] (not in ignore): edge, distance, point.</summary>
            public bool Ray(float2 p, float2 dir, float minT, float maxT, ICollection<int> ignore, out int edge, out float dist, out float2 hit)
            {
                edge = -1; dist = float.MaxValue; hit = default;
                float2 q = p + dir * maxT;
                _index.Query(math.min(p, q) - 0.5f, math.max(p, q) + 0.5f, _scratch, _seen);
                foreach (int sid in _scratch)
                {
                    int ei = _index.Owner[sid];
                    if (ignore != null && ignore.Contains(ei)) continue;
                    if (!Seg.Intersect(p, q, _index.A[sid], _index.B[sid], out float ta, out _)) continue;
                    float d = ta * maxT;
                    if (d < minT || d >= dist) continue;
                    dist = d; edge = ei; hit = p + dir * d;
                }
                return edge >= 0;
            }
        }

        /// <summary>
        /// Road distances from <paramref name="source"/> over the accepted edges (Dijkstra, stops beyond <paramref name="limit"/>).
        /// Unreached nodes are +∞.
        /// </summary>
        public static float[] Distances(RoadNetwork net, List<int>[] inc, int source, float limit, float[] lengths)
        {
            var dist = new float[net.Nodes.Count];
            for (int i = 0; i < dist.Length; i++) dist[i] = float.PositiveInfinity;
            if (source < 0) return dist;
            dist[source] = 0f;
            var open = new SortedSet<(float, int)> { (0f, source) };
            while (open.Count > 0)
            {
                var (d, n) = open.Min;
                open.Remove(open.Min);
                if (d > dist[n] || d > limit) continue;
                foreach (int ei in inc[n])
                {
                    var e = net.Edges[ei];
                    int o = Other(e, n);
                    float nd = d + lengths[ei];
                    if (nd < dist[o])
                    {
                        if (!float.IsPositiveInfinity(dist[o])) open.Remove((dist[o], o));
                        dist[o] = nd;
                        open.Add((nd, o));
                    }
                }
            }
            return dist;
        }

        public static float[] Lengths(RoadNetwork net)
        {
            var l = new float[net.Edges.Count];
            for (int i = 0; i < l.Length; i++) l[i] = net.Edges[i].N0 >= 0 ? net.Edges[i].Curve.LengthXZ(10) : 0f;
            return l;
        }

        /// <summary>A link curve between two points leaving <paramref name="a"/> along <paramref name="startDir"/> (straight when none).</summary>
        public static RoadCurve Link(float3 a, float3 b, float2 startDir = default, float2 endDir = default)
        {
            bool s = math.lengthsq(startDir) > 0.5f, e = math.lengthsq(endDir) > 0.5f;
            if (!s && !e) return RoadCurve.Straight(a, b);
            float2 chord = math.normalizesafe(b.xz - a.xz);
            float2 sd = s ? startDir : chord, ed = e ? endDir : chord;
            return RoadCurve.Fit(a, new float3(sd.x, 0f, sd.y), new float3(ed.x, 0f, ed.y), b);
        }
    }
}
