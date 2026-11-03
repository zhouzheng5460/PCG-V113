// Placemaker roads layer (s52) — hillside terraces: small numeric helpers (a scalar grid with blur, gradient and
// marching-squares contours; polylines resampled, smoothed by arc length and measured; a bucket index of segments
// for nearest / ray queries). Pure, deterministic, no shared state.

using System;
using System.Collections.Generic;
using Placemaker.Core.Geometry;
using Unity.Mathematics;

namespace Placemaker.Core.Layers.Roads.Terraces
{
    /// <summary>A regular grid of values over a box (node (i, j) at Origin + (i, j)·Cell).</summary>
    internal sealed class ScalarGrid
    {
        public readonly float2 Origin;
        public readonly float Cell;
        public readonly int Nx, Ny;
        public readonly float[] V;

        public ScalarGrid(float2 origin, float cell, int nx, int ny)
        {
            Origin = origin; Cell = cell; Nx = math.max(2, nx); Ny = math.max(2, ny);
            V = new float[Nx * Ny];
        }

        public static ScalarGrid Over(float2 min, float2 max, float cell)
        {
            int nx = (int)math.ceil((max.x - min.x) / cell) + 1, ny = (int)math.ceil((max.y - min.y) / cell) + 1;
            return new ScalarGrid(min, cell, nx, ny);
        }

        public ScalarGrid CloneEmpty() => new ScalarGrid(Origin, Cell, Nx, Ny);
        public float2 Pos(int i, int j) => Origin + new float2(i, j) * Cell;
        public float2 Max => Origin + new float2(Nx - 1, Ny - 1) * Cell;

        /// <summary>Bilinear sample, clamped to the grid.</summary>
        public float Sample(float2 p)
        {
            float fx = math.clamp((p.x - Origin.x) / Cell, 0f, Nx - 1.001f), fy = math.clamp((p.y - Origin.y) / Cell, 0f, Ny - 1.001f);
            int i = (int)fx, j = (int)fy;
            float tx = fx - i, ty = fy - j;
            int k = j * Nx + i;
            return math.lerp(math.lerp(V[k], V[k + 1], tx), math.lerp(V[k + Nx], V[k + Nx + 1], tx), ty);
        }

        /// <summary>Gradient (per metre) by central differences of the bilinear surface.</summary>
        public float2 Gradient(float2 p)
        {
            float e = Cell;
            return new float2(Sample(p + new float2(e, 0)) - Sample(p - new float2(e, 0)), Sample(p + new float2(0, e)) - Sample(p - new float2(0, e))) / (2f * e);
        }

        /// <summary>Approximate Gaussian blur (three box passes per axis) with standard deviation sigma (m).</summary>
        public void Blur(float sigma)
        {
            int r = math.max(1, (int)math.round(sigma / Cell * 0.8660254f));   // three boxes of width 2r+1 ≈ σ
            var tmp = new float[V.Length];
            for (int pass = 0; pass < 3; pass++)
            {
                BoxX(V, tmp, r);
                BoxY(tmp, V, r);
            }
        }

        private void BoxX(float[] src, float[] dst, int r)
        {
            for (int j = 0; j < Ny; j++)
            {
                int row = j * Nx;
                for (int i = 0; i < Nx; i++)
                {
                    float sum = 0f; int n = 0;
                    for (int d = -r; d <= r; d++) { int ii = math.clamp(i + d, 0, Nx - 1); sum += src[row + ii]; n++; }
                    dst[row + i] = sum / n;
                }
            }
        }

        private void BoxY(float[] src, float[] dst, int r)
        {
            for (int i = 0; i < Nx; i++)
                for (int j = 0; j < Ny; j++)
                {
                    float sum = 0f; int n = 0;
                    for (int d = -r; d <= r; d++) { int jj = math.clamp(j + d, 0, Ny - 1); sum += src[jj * Nx + i]; n++; }
                    dst[j * Nx + i] = sum / n;
                }
        }

        /// <summary>
        /// Contour lines at <paramref name="level"/> (marching squares, saddles split by the cell mean), joined into polylines;
        /// closed[k] = the line is a loop.
        /// </summary>
        public List<List<float2>> Contours(float level, List<bool> closed)
        {
            // Segments keyed by grid edges so that pieces chain exactly: edge id = 2·node (+0 horizontal, +1 vertical).
            var segA = new List<long>(); var segB = new List<long>();
            var pos = new Dictionary<long, float2>();
            long H(int i, int j) => 2L * (j * (long)Nx + i);
            long Vt(int i, int j) => 2L * (j * (long)Nx + i) + 1;
            float2 Cross(long id)
            {
                if (pos.TryGetValue(id, out var p)) return p;
                long node = id >> 1; int i = (int)(node % Nx), j = (int)(node / Nx);
                bool horiz = (id & 1) == 0;
                int k0 = j * Nx + i, k1 = horiz ? k0 + 1 : k0 + Nx;
                float a = V[k0], b = V[k1];
                float t = math.abs(b - a) > 1e-9f ? math.saturate((level - a) / (b - a)) : 0.5f;
                p = math.lerp(Pos(i, j), horiz ? Pos(i + 1, j) : Pos(i, j + 1), t);
                pos[id] = p;
                return p;
            }
            for (int j = 0; j < Ny - 1; j++)
                for (int i = 0; i < Nx - 1; i++)
                {
                    int k = j * Nx + i;
                    float v0 = V[k], v1 = V[k + 1], v2 = V[k + Nx + 1], v3 = V[k + Nx];
                    int c = (v0 >= level ? 1 : 0) | (v1 >= level ? 2 : 0) | (v2 >= level ? 4 : 0) | (v3 >= level ? 8 : 0);
                    if (c == 0 || c == 15) continue;
                    long eB = H(i, j), eR = Vt(i + 1, j), eT = H(i, j + 1), eL = Vt(i, j);
                    void Seg(long a, long b) { segA.Add(a); segB.Add(b); }
                    switch (c)
                    {
                        case 1: case 14: Seg(eL, eB); break;
                        case 2: case 13: Seg(eB, eR); break;
                        case 3: case 12: Seg(eL, eR); break;
                        case 4: case 11: Seg(eR, eT); break;
                        case 6: case 9: Seg(eB, eT); break;
                        case 7: case 8: Seg(eL, eT); break;
                        case 5:
                        case 10:
                        {
                            bool centreHigh = (v0 + v1 + v2 + v3) * 0.25f >= level;
                            if ((c == 5) == centreHigh) { Seg(eL, eT); Seg(eB, eR); }
                            else { Seg(eL, eB); Seg(eR, eT); }
                            break;
                        }
                    }
                }
            // Chain segments through shared edge crossings.
            var adj = new Dictionary<long, List<int>>();
            for (int s = 0; s < segA.Count; s++)
            {
                if (!adj.TryGetValue(segA[s], out var la)) adj[segA[s]] = la = new List<int>(2);
                la.Add(s);
                if (!adj.TryGetValue(segB[s], out var lb)) adj[segB[s]] = lb = new List<int>(2);
                lb.Add(s);
            }
            var used = new bool[segA.Count];
            var lines = new List<List<float2>>();
            // Open lines first (start at a crossing with one segment), then loops.
            for (int pass = 0; pass < 2; pass++)
                for (int s0 = 0; s0 < segA.Count; s0++)
                {
                    if (used[s0]) continue;
                    long start;
                    if (pass == 0)
                    {
                        if (adj[segA[s0]].Count == 1) start = segA[s0];
                        else if (adj[segB[s0]].Count == 1) start = segB[s0];
                        else continue;
                    }
                    else start = segA[s0];
                    var ids = new List<long> { start };
                    long cur = start;
                    int seg = s0;
                    while (seg >= 0)
                    {
                        used[seg] = true;
                        long next = segA[seg] == cur ? segB[seg] : segA[seg];
                        ids.Add(next);
                        cur = next;
                        seg = -1;
                        foreach (int t in adj[cur]) if (!used[t]) { seg = t; break; }
                    }
                    var line = new List<float2>(ids.Count);
                    foreach (var id in ids) line.Add(Cross(id));
                    bool loop = pass == 1 && ids.Count > 3 && ids[0] == ids[ids.Count - 1];
                    if (loop) line.RemoveAt(line.Count - 1);
                    if (line.Count >= 2) { lines.Add(line); closed?.Add(loop); }
                }
            return lines;
        }
    }

    /// <summary>Polyline helpers (points in metres, xz).</summary>
    internal static class Lines
    {
        public static float Length(IReadOnlyList<float2> p, bool closed = false)
        {
            float l = 0f;
            for (int i = 1; i < p.Count; i++) l += math.distance(p[i - 1], p[i]);
            if (closed && p.Count > 2) l += math.distance(p[p.Count - 1], p[0]);
            return l;
        }

        /// <summary>Cumulative arc length per point.</summary>
        public static float[] Stations(IReadOnlyList<float2> p)
        {
            var s = new float[p.Count];
            for (int i = 1; i < p.Count; i++) s[i] = s[i - 1] + math.distance(p[i - 1], p[i]);
            return s;
        }

        /// <summary>Points every ~<paramref name="step"/> m along the line (ends kept; a loop is resampled round and stays open-ended in the list).</summary>
        public static List<float2> Resample(IReadOnlyList<float2> p, float step, bool closed = false)
        {
            var src = new List<float2>(p);
            if (closed && src.Count > 2) src.Add(src[0]);
            float total = Length(src);
            var r = new List<float2>();
            if (src.Count < 2 || total < 1e-3f) { r.AddRange(src); return r; }
            int n = math.max(closed ? 3 : 1, (int)math.round(total / step));
            float d = total / n;
            int seg = 0; float acc = 0f;
            for (int k = 0; k <= n; k++)
            {
                if (closed && k == n) break;
                float target = k * d;
                while (seg < src.Count - 2 && acc + math.distance(src[seg], src[seg + 1]) < target) { acc += math.distance(src[seg], src[seg + 1]); seg++; }
                float len = math.distance(src[seg], src[seg + 1]);
                float t = len > 1e-6f ? math.saturate((target - acc) / len) : 0f;
                r.Add(math.lerp(src[seg], src[seg + 1], t));
            }
            return r;
        }

        /// <summary>
        /// Gaussian smoothing by index (the points are evenly spaced, so by arc length), sigma in points. Open lines keep their
        /// end points and taper the smoothing towards them.
        /// </summary>
        public static List<float2> Smooth(IReadOnlyList<float2> p, float sigmaPts, bool closed)
        {
            int n = p.Count;
            var r = new List<float2>(n);
            if (n < 3 || sigmaPts < 0.3f) { r.AddRange(p); return r; }
            int rad = math.max(1, (int)math.ceil(sigmaPts * 2.5f));
            var w = new float[rad + 1];
            for (int k = 0; k <= rad; k++) w[k] = math.exp(-0.5f * k * k / (sigmaPts * sigmaPts));
            for (int i = 0; i < n; i++)
            {
                if (!closed && (i == 0 || i == n - 1)) { r.Add(p[i]); continue; }
                int lim = closed ? rad : math.min(rad, math.min(i, n - 1 - i));
                float2 sum = p[i] * w[0]; float ws = w[0];
                for (int k = 1; k <= lim; k++)
                {
                    int a = i - k, b = i + k;
                    if (closed) { a = ((a % n) + n) % n; b %= n; }
                    sum += (p[a] + p[b]) * w[k]; ws += 2f * w[k];
                }
                r.Add(sum / ws);
            }
            return r;
        }

        /// <summary>Smallest radius of curvature (m) over windows of ~<paramref name="window"/> m (three-point circle).</summary>
        public static float MinRadius(IReadOnlyList<float2> p, bool closed, float window = 12f)
        {
            int n = p.Count;
            if (n < 3) return float.PositiveInfinity;
            float step = Length(p, closed) / math.max(1, closed ? n : n - 1);
            int k = math.max(1, (int)math.round(window / math.max(0.5f, step)));
            float best = float.PositiveInfinity;
            for (int i = 0; i < n; i++)
            {
                int a = i - k, b = i + k;
                if (closed) { a = ((a % n) + n) % n; b %= n; }
                else if (a < 0 || b >= n) continue;
                best = math.min(best, Radius(p[a], p[i], p[b]));
            }
            return best;
        }

        public static float Radius(float2 a, float2 b, float2 c)
        {
            float ab = math.distance(a, b), bc = math.distance(b, c), ca = math.distance(c, a);
            float cross = math.abs((b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x));
            if (cross < 1e-6f) return float.PositiveInfinity;
            return ab * bc * ca / (2f * cross);
        }

        /// <summary>Unit tangents (central differences).</summary>
        public static float2[] Tangents(IReadOnlyList<float2> p, bool closed)
        {
            int n = p.Count;
            var t = new float2[n];
            for (int i = 0; i < n; i++)
            {
                int a = i - 1, b = i + 1;
                if (closed) { a = (a + n) % n; b %= n; }
                else { a = math.max(0, a); b = math.min(n - 1, b); }
                t[i] = math.normalizesafe(p[b] - p[a], new float2(1, 0));
            }
            return t;
        }

        /// <summary>Closest point on the polyline: distance, segment index and t.</summary>
        public static float Closest(IReadOnlyList<float2> p, bool closed, float2 q, out int seg, out float t)
        {
            seg = 0; t = 0f;
            float best = float.MaxValue;
            int m = closed ? p.Count : p.Count - 1;
            for (int i = 0; i < m; i++)
            {
                var a = p[i]; var b = p[(i + 1) % p.Count];
                float d2 = Seg.DistSq(q, a, b, out float tt);
                if (d2 < best) { best = d2; seg = i; t = tt; }
            }
            return math.sqrt(best);
        }

        /// <summary>Pieces of the polyline that lie inside <paramref name="ring"/> (split where it leaves).</summary>
        public static List<List<float2>> ClipInside(IReadOnlyList<float2> p, bool closed, float2[] ring, float inset)
        {
            var r = new List<List<float2>>();
            int n = p.Count;
            if (n < 2) return r;
            bool In(float2 q) => Poly.Contains(ring, q) && Poly.DistanceToBoundary(ring, q) >= inset;
            var inside = new bool[n];
            int firstOut = -1;
            for (int i = 0; i < n; i++) { inside[i] = In(p[i]); if (!inside[i] && firstOut < 0) firstOut = i; }
            if (firstOut < 0)
            {
                // Entirely inside: one piece (a loop stays a loop: the caller knows it is closed).
                r.Add(new List<float2>(p));
                return r;
            }
            // Walk once round (for a loop start at an outside point so pieces do not wrap).
            int start = closed ? firstOut : 0;
            List<float2> cur = null;
            int count = closed ? n + 1 : n;
            for (int k = 0; k < count; k++)
            {
                int i = (start + k) % n;
                if (inside[i])
                {
                    if (cur == null) cur = new List<float2>();
                    cur.Add(p[i]);
                }
                else if (cur != null) { if (cur.Count >= 2) r.Add(cur); cur = null; }
            }
            if (cur != null && cur.Count >= 2) r.Add(cur);
            return r;
        }
    }

    /// <summary>Bucket index of segments with owner ids: nearest, crossing and ray queries.</summary>
    internal sealed class SegGrid
    {
        public readonly List<float2> A = new List<float2>(), B = new List<float2>();
        public readonly List<int> Owner = new List<int>();
        public readonly List<int> Index = new List<int>();   // point index of A within its owner's line
        private readonly float _cell;
        private readonly Dictionary<long, List<int>> _cells = new Dictionary<long, List<int>>();
        private int[] _stamp = new int[64];
        private int _tick;

        public SegGrid(float cell) { _cell = math.max(2f, cell); }

        private static long Key(int x, int y) => ((long)x << 32) ^ (uint)y;

        public void Add(float2 a, float2 b, int owner, int index)
        {
            int id = A.Count;
            A.Add(a); B.Add(b); Owner.Add(owner); Index.Add(index);
            float2 mn = math.min(a, b), mx = math.max(a, b);
            int x0 = (int)math.floor(mn.x / _cell), x1 = (int)math.floor(mx.x / _cell);
            int y0 = (int)math.floor(mn.y / _cell), y1 = (int)math.floor(mx.y / _cell);
            for (int x = x0; x <= x1; x++)
                for (int y = y0; y <= y1; y++)
                {
                    long k = Key(x, y);
                    if (!_cells.TryGetValue(k, out var l)) _cells[k] = l = new List<int>(4);
                    l.Add(id);
                }
        }

        public void AddLine(IReadOnlyList<float2> p, bool closed, int owner)
        {
            for (int i = 0; i + 1 < p.Count; i++) Add(p[i], p[i + 1], owner, i);
            if (closed && p.Count > 2) Add(p[p.Count - 1], p[0], owner, p.Count - 1);
        }

        /// <summary>Visits every segment whose cells touch the box (each once per call).</summary>
        public void Query(float2 mn, float2 mx, List<int> into)
        {
            into.Clear();
            if (_stamp.Length < A.Count) Array.Resize(ref _stamp, math.max(A.Count, _stamp.Length * 2));
            _tick++;
            if (_tick == int.MaxValue) { Array.Clear(_stamp, 0, _stamp.Length); _tick = 1; }
            int x0 = (int)math.floor(mn.x / _cell), x1 = (int)math.floor(mx.x / _cell);
            int y0 = (int)math.floor(mn.y / _cell), y1 = (int)math.floor(mx.y / _cell);
            for (int x = x0; x <= x1; x++)
                for (int y = y0; y <= y1; y++)
                    if (_cells.TryGetValue(Key(x, y), out var l))
                        foreach (int s in l)
                        {
                            if (_stamp[s] == _tick) continue;
                            _stamp[s] = _tick;
                            into.Add(s);
                        }
        }

        /// <summary>
        /// First segment hit by the ray from <paramref name="o"/> along unit <paramref name="d"/> within <paramref name="maxDist"/>,
        /// ignoring owners for which <paramref name="skip"/> is true. Returns the segment id (-1 = none) and the distance.
        /// </summary>
        public int Ray(float2 o, float2 d, float maxDist, Func<int, bool> skip, out float dist, List<int> scratch)
        {
            dist = float.MaxValue;
            int best = -1;
            float step = _cell;
            for (float s = 0f; s < maxDist; s += step)
            {
                float2 p0 = o + d * s, p1 = o + d * math.min(maxDist, s + step);
                Query(math.min(p0, p1) - 0.5f, math.max(p0, p1) + 0.5f, scratch);
                foreach (int id in scratch)
                {
                    if (skip != null && skip(Owner[id])) continue;
                    if (Poly.SegmentIntersection(o, o + d * maxDist, A[id], B[id], out _, out float ta, out _))
                    {
                        float dd = ta * maxDist;
                        if (dd > 0.5f && dd < dist) { dist = dd; best = id; }
                    }
                }
                if (best >= 0 && dist <= s + step) break;
            }
            return best;
        }

        /// <summary>True when segment a–b crosses any indexed segment whose owner is not skipped.</summary>
        public bool Crosses(float2 a, float2 b, Func<int, bool> skip, List<int> scratch)
        {
            Query(math.min(a, b) - 0.5f, math.max(a, b) + 0.5f, scratch);
            foreach (int id in scratch)
            {
                if (skip != null && skip(Owner[id])) continue;
                if (Poly.SegmentsIntersect(a, b, A[id], B[id])) return true;
            }
            return false;
        }

        /// <summary>Smallest distance from p to an indexed segment within r whose owner is not skipped (r when none).</summary>
        public float Nearest(float2 p, float r, Func<int, bool> skip, List<int> scratch, out int seg)
        {
            seg = -1;
            Query(p - r, p + r, scratch);
            float best = r * r;
            foreach (int id in scratch)
            {
                if (skip != null && skip(Owner[id])) continue;
                float d2 = Seg.DistSq(p, A[id], B[id], out _);
                if (d2 < best) { best = d2; seg = id; }
            }
            return math.sqrt(best);
        }
    }
}
