// Placemaker roads layer (s52) — "Hillside terraces": the layout as planned road pieces. Streets are split where links
// join and every ≤ 60 m; links and walks at every profile kink (landing ends, turn ends) so each piece's height is a
// straight ramp. Curves are fitted through the polylines with the game's own fitting (RoadCurve.Fit) and split again
// where they would leave the polyline by more than 0.8 m. Pure.

using System;
using System.Collections.Generic;
using Unity.Mathematics;

namespace Placemaker.Core.Layers.Roads.Terraces
{
    internal static class TerraceNetwork
    {
        private const float StreetPiece = 60f, LinkPiece = 45f, MaxDeviation = 0.8f;

        /// <summary>Terrace streets (tagged with their street index) and the links between them.</summary>
        public static void Add(RoadNetwork net, TerraceLayout lay, TerraceOptions o)
        {
            for (int i = 0; i < lay.Streets.Count; i++)
            {
                var st = lay.Streets[i];
                var cuts = new List<float>();
                foreach (float j in st.Junctions) cuts.Add(st.Wrap(j));
                cuts.Sort();
                for (int k = cuts.Count - 1; k > 0; k--) if (cuts[k] - cuts[k - 1] < 1f) cuts.RemoveAt(k);
                var spans = new List<(float a, float b)>();
                if (st.Closed)
                {
                    if (cuts.Count == 0) cuts.Add(0f);
                    for (int k = 0; k < cuts.Count; k++)
                    {
                        float a = cuts[k], b = k + 1 < cuts.Count ? cuts[k + 1] : cuts[0] + st.Length;
                        if (b - a > 0.5f) spans.Add((a, b));
                    }
                }
                else
                {
                    float prev = 0f;
                    foreach (float c in cuts) { if (c - prev > 0.5f) spans.Add((prev, c)); prev = c; }
                    if (st.Length - prev > 0.5f) spans.Add((prev, st.Length));
                }
                foreach (var (a, b) in spans)
                {
                    var pts = st.Between(a, b, out var stations);
                    var p3 = new List<float3>(pts.Count);
                    for (int k = 0; k < pts.Count; k++) p3.Add(new float3(pts[k].x, st.HeightAt(stations[k]), pts[k].y));
                    var breaks = new bool[p3.Count];
                    breaks[0] = breaks[p3.Count - 1] = true;
                    Emit(net, p3, breaks, StreetPiece, NetRole.Street, e => e.TerraceStreet = i);
                }
            }
            var linkRole = o.AvenueLinks ? NetRole.Avenue : NetRole.Street;
            foreach (var r in lay.Links)
            {
                var p3 = new List<float3>(r.P.Count);
                for (int k = 0; k < r.P.Count; k++) p3.Add(new float3(r.P[k].x, r.H[k], r.P[k].y));
                Emit(net, p3, r.Break.ToArray(), LinkPiece, linkRole, e => e.TerraceLink = true);
            }
        }

        /// <summary>
        /// The bank walks, after the network design: each end joins the terrace street it was planned on (the street edge is
        /// split there; an end within 6 m of a node uses the node). Walks whose street went are skipped. Returns the count.
        /// </summary>
        public static int AddWalks(RoadNetwork net, TerraceLayout lay, Func<NetRole, float> halfWidth = null, float bankSlope = 0.4f)
        {
            int added = 0;
            foreach (var w in lay.Walks)
            {
                if (w.P.Count < 2) continue;
                // A road the network design added since (a link between pieces) may now cross the walk or pass it at another height.
                if (halfWidth != null && Blocked(net, w, halfWidth, bankSlope)) { lay.Reject("walk dropped: a road since"); continue; }
                if (!Join(net, w.P[0], w.From, out float3 a) || !Join(net, w.P[w.P.Count - 1], w.To, out float3 d)) { lay.Reject("walk dropped: street gone"); continue; }
                var p3 = new List<float3>(w.P.Count);
                for (int k = 0; k < w.P.Count; k++) p3.Add(new float3(w.P[k].x, w.H[k], w.P[k].y));
                p3[0] = a; p3[p3.Count - 1] = d;
                bool trail = w.Trail;
                Emit(net, p3, w.Break.ToArray(), LinkPiece, NetRole.Pathway, e =>
                {
                    e.Walk = true; e.Trail = trail; e.Category = trail ? AssetCategories.TrailPath : "";
                });
                added++;
            }
            if (added > 0) net.Weld();
            return added;
        }

        /// <summary>
        /// A planned walk meets a car road that is not one of its two streets (beyond its first and last 12 m) closer than the
        /// two corridors and the bank their height difference needs.
        /// </summary>
        private static bool Blocked(RoadNetwork net, TerraceRoute w, Func<NetRole, float> halfWidth, float bankSlope)
        {
            var s = Lines.Stations(w.P);
            float total = s[s.Length - 1];
            var pts = new List<float3>();
            foreach (var e in net.Edges)
            {
                if (!e.Alive || !e.IsCar) continue;
                pts.Clear();
                e.Curve.Flatten(pts, null, 4f, 6f);
                float2 mn = pts[0].xz, mx = pts[0].xz;
                foreach (var q in pts) { mn = math.min(mn, q.xz); mx = math.max(mx, q.xz); }
                float half = halfWidth(e.Role) + 1.5f + 2.5f;
                // The streets and links the walk was planned against have not moved: only what the network design added since.
                if (e.TerraceStreet >= 0 || e.TerraceLink) continue;
                bool own = false;
                for (int k = 0; k < w.P.Count; k++)
                {
                    if (s[k] < 12f || s[k] > total - 12f) continue;
                    var p = w.P[k];
                    if (p.x < mn.x - half - 40f || p.y < mn.y - half - 40f || p.x > mx.x + half + 40f || p.y > mx.y + half + 40f) continue;
                    for (int i = 0; i + 1 < pts.Count; i++)
                    {
                        float d = math.sqrt(Seg.DistSq(p, pts[i].xz, pts[i + 1].xz, out float t));
                        float dh = math.abs(w.H[k] - math.lerp(pts[i].y, pts[i + 1].y, t));
                        // On its own terrace the walk is level with its street.
                        if (own && dh < 0.75f) continue;
                        if (d < half + dh / math.max(0.1f, bankSlope)) return true;
                    }
                }
            }
            return false;
        }

        private static bool Join(RoadNetwork net, float2 p, int street, out float3 at)
        {
            at = default;
            int best = -1; float bd = 2.5f, bt = 0f;
            for (int i = 0; i < net.Edges.Count; i++)
            {
                var e = net.Edges[i];
                if (!e.Alive || !e.IsCar || e.TerraceStreet != street) continue;
                float d = e.Curve.DistanceXZ(p, out float t);
                if (d < bd) { bd = d; best = i; bt = t; }
            }
            if (best < 0) return false;
            var edge = net.Edges[best];
            float len = edge.Curve.LengthXZ(12);
            float sA = RoadGraph.ParamAt(edge.Curve, 6f), sD = RoadGraph.ParamAt(edge.Curve, 6f, true);
            if (len < 14f || bt <= sA) { at = edge.Curve.A; return true; }
            if (bt >= sD) { at = edge.Curve.D; return true; }
            at = edge.Curve.Position(bt);
            RoadGraph.Split(net, best, bt);
            net.Weld();
            return true;
        }

        /// <summary>Fits Bézier pieces through a 3D polyline: breaks where flagged and every ≤ maxLen, tighter where the fit is off.</summary>
        private static void Emit(RoadNetwork net, List<float3> p, bool[] breaks, float maxLen, NetRole role, Action<RoadEdge> tag)
        {
            int n = p.Count;
            if (n < 2) return;
            var s = new float[n];
            for (int i = 1; i < n; i++) s[i] = s[i - 1] + math.distance(p[i - 1].xz, p[i].xz);
            var dir = new float2[n];
            for (int i = 0; i < n; i++)
            {
                int a = math.max(0, i - 1), b = math.min(n - 1, i + 1);
                // At a break (a kink in the profile, the end of a turn) keep the plan direction continuous: central difference.
                dir[i] = math.normalizesafe(p[b].xz - p[a].xz, new float2(1, 0));
            }
            // Kept breaks: flagged ones that leave ≥ MinPiece on both sides (no crumbs the network passes would collapse).
            var keep = new List<int> { 0 };
            for (int i = 1; i < n - 1; i++)
                if (breaks != null && i < breaks.Length && breaks[i] && s[i] - s[keep[keep.Count - 1]] >= MinPiece && s[n - 1] - s[i] >= MinPiece)
                    keep.Add(i);
            keep.Add(n - 1);
            for (int q = 0; q + 1 < keep.Count; q++)
            {
                int a = keep[q], b = keep[q + 1];
                float span = s[b] - s[a];
                int parts = math.max(1, (int)math.ceil(span / maxLen));
                int start = a;
                for (int part = 1; part <= parts; part++)
                {
                    int cut = b;
                    if (part < parts)
                    {
                        float target = s[a] + span * part / parts;
                        cut = start + 1;
                        while (cut < b && s[cut] < target) cut++;
                        if (s[b] - s[cut] < MinPiece || s[cut] - s[start] < MinPiece) continue;
                    }
                    Piece(net, p, dir, start, cut, role, tag);
                    start = cut;
                }
            }
        }

        private const float MinPiece = 10f;

        /// <summary>A sharp corner of the polyline at i (a walk's zig-zag turn): each piece keeps its own direction there.</summary>
        private static bool Corner(List<float3> p, int i)
        {
            if (i <= 0 || i >= p.Count - 1) return false;
            float2 u = math.normalizesafe(p[i].xz - p[i - 1].xz), w = math.normalizesafe(p[i + 1].xz - p[i].xz);
            return math.dot(u, w) < 0.9f;   // > ~25°
        }

        private static void Piece(RoadNetwork net, List<float3> p, float2[] dir, int a, int b, NetRole role, Action<RoadEdge> tag)
        {
            if (b <= a) return;
            if (math.distance(p[a].xz, p[b].xz) < 0.3f) return;
            float2 ta = Corner(p, a) && a + 1 < p.Count ? math.normalizesafe(p[a + 1].xz - p[a].xz) : dir[a];
            float2 tb = Corner(p, b) && b > 0 ? math.normalizesafe(p[b].xz - p[b - 1].xz) : dir[b];
            var c = RoadCurve.Fit(p[a], new float3(ta.x, 0f, ta.y), new float3(tb.x, 0f, tb.y), p[b]);
            float len = 0f;
            for (int k = a + 1; k <= b; k++) len += math.distance(p[k - 1].xz, p[k].xz);
            if (b - a >= 2 && len >= 2f * MinPiece)
            {
                float dev = 0f;
                for (int k = a + 1; k < b; k++) dev = math.max(dev, c.DistanceXZ(p[k].xz, out _));
                if (dev > MaxDeviation)
                {
                    int m = (a + b) / 2;
                    Piece(net, p, dir, a, m, role, tag);
                    Piece(net, p, dir, m, b, role, tag);
                    return;
                }
            }
            LinearByLength(ref c);
            var e = new RoadEdge(c, role);
            tag?.Invoke(e);
            net.Edges.Add(e);
        }

        /// <summary>
        /// Heights that climb evenly by distance, not by curve parameter: a fitted curve's control arms make it run slower in
        /// places, and a height linear in t would then be steeper there (a 7 % ramp measured 11 % in its middle). The inner
        /// control heights put the curve on the straight-by-length profile at t = 1/3 and 2/3.
        /// </summary>
        public static void LinearByLength(ref RoadCurve c)
        {
            const int N = 24;
            var s = new float[N + 1];
            float2 prev = c.Position(0f).xz;
            for (int i = 1; i <= N; i++) { float2 q = c.Position(i / (float)N).xz; s[i] = s[i - 1] + math.distance(prev, q); prev = q; }
            float total = s[N];
            if (total < 1e-3f) return;
            float a = c.A.y, d = c.D.y;
            float g1 = math.lerp(a, d, s[N / 3] / total), g2 = math.lerp(a, d, s[2 * N / 3] / total);
            c.B.y = 3f * g1 - 1.5f * g2 - (5f / 6f) * a + d / 3f;
            c.C.y = 3f * g2 - 1.5f * g1 - (5f / 6f) * d + a / 3f;
        }
    }
}
