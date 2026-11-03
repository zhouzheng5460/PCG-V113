// Terrain-aware trail routing for the hiking trail: A* over a coarse grid (≈ 6000 cells) with
// 16 move directions (the knight moves give smooth 26°/63° headings instead of 45° staircases).
//
// Step cost = length × (1 + wander·noise + landscape preference + edge keep-off + reuse) plus a
// steep grade penalty above the maximum grade. Because climbing straight up a slope steeper than
// the max grade costs far more than walking across it, the cheapest route follows the contours
// and turns back and forth (switchbacks) where it must gain height in a limited width — the way
// real trails are laid out. Water is crossable only where bridges are allowed (and discouraged).

using System;
using System.Collections.Generic;
using System.Threading;
using Placemaker.Core.Layers.Common;
using Unity.Mathematics;

namespace Placemaker.Core.Layers.Types.Hiking
{
    public enum TrailPreference { None = 0, High = 1, Low = 2 }

    public sealed class TrailGrid
    {
        public float Cell;
        public int Nx, Ny;
        public float2 Origin;
        public bool[] Ok;
        public float[] H, Water, Edge, Noise, Extra;
        public float HMin, HMax;
        public float MaxGrade = 0.1f, Wander = 0.4f;
        public bool Bridges = true;
        public TrailPreference Preference;

        public const float WetDepth = 0.2f;
        public int Count => Nx * Ny;
        public float2 Centre(int i) => Origin + (new float2(i % Nx, i / Nx) + 0.5f) * Cell;
        public bool Wet(int i) => Water[i] > WetDepth;

        /// <summary>Height rank 0 (lowest) .. 1 (highest); 0.5 on flat ground.</summary>
        public float Rank(float h) => HMax - HMin < 0.5f ? 0.5f : math.saturate((h - HMin) / (HMax - HMin));

        public static TrailGrid Build(LayerContext ctx, LayerRegion region, Occupancy occ, float margin, uint seed, int targetCells = 6000)
        {
            var g = new TrailGrid();
            g.Cell = math.max(3f, math.sqrt(region.Area / targetCells));
            g.Origin = region.Min;
            g.Nx = math.max(1, (int)math.ceil((region.Max.x - region.Min.x) / g.Cell));
            g.Ny = math.max(1, (int)math.ceil((region.Max.y - region.Min.y) / g.Cell));
            int n = g.Count;
            g.Ok = new bool[n]; g.H = new float[n]; g.Water = new float[n]; g.Edge = new float[n]; g.Noise = new float[n]; g.Extra = new float[n];
            g.HMin = float.MaxValue; g.HMax = float.MinValue;
            var site = ctx.Site;
            for (int i = 0; i < n; i++)
            {
                if ((i & 1023) == 0) ctx.Check();
                var c = g.Centre(i);
                if (!region.Contains(c)) continue;
                float edge = region.BoundaryDistance(c, 40f);
                g.Edge[i] = edge;
                g.H[i] = ctx.H(c);
                g.Water[i] = site?.WaterDepthAt != null ? site.WaterDepthAt(c) : 0f;
                g.HMin = math.min(g.HMin, g.H[i]); g.HMax = math.max(g.HMax, g.H[i]);
                if (edge < margin) continue;
                if (occ.HitsPolygon(c, 1.5f) || occ.Discs.Overlaps(c, 1.5f) || occ.Corridors.Overlaps(c, 1.5f)) continue;
                g.Ok[i] = true;
                g.Noise[i] = ValueNoise.Clumps(c, 38f, seed);
            }
            if (g.HMin > g.HMax) { g.HMin = g.HMax = 0f; }
            return g;
        }

        public int CellOf(float2 p)
        {
            int x = math.clamp((int)math.floor((p.x - Origin.x) / Cell), 0, Nx - 1);
            int y = math.clamp((int)math.floor((p.y - Origin.y) / Cell), 0, Ny - 1);
            return y * Nx + x;
        }

        /// <summary>Closest usable cell to a point (-1 when the grid has none).</summary>
        public int NearestOk(float2 p, bool dryOnly = false)
        {
            int best = -1; float bd = float.MaxValue;
            for (int i = 0; i < Count; i++)
            {
                if (!Ok[i] || (dryOnly && Wet(i))) continue;
                float d = math.distancesq(Centre(i), p);
                if (d < bd) { bd = d; best = i; }
            }
            return best;
        }

        /// <summary>Adds a reuse penalty around a route so later legs keep apart (except near the given points).</summary>
        public void Stamp(List<int> cells, float radius, float penalty, List<float2> exempt, float exemptRadius)
        {
            int r = (int)math.ceil(radius / Cell);
            foreach (var c in cells)
            {
                int cx = c % Nx, cy = c / Nx;
                for (int dy = -r; dy <= r; dy++)
                    for (int dx = -r; dx <= r; dx++)
                    {
                        int x = cx + dx, y = cy + dy;
                        if (x < 0 || y < 0 || x >= Nx || y >= Ny) continue;
                        int j = y * Nx + x;
                        var p = Centre(j);
                        if (math.distance(p, Centre(c)) > radius) continue;
                        bool skip = false;
                        foreach (var e in exempt) if (math.distance(p, e) < exemptRadius) { skip = true; break; }
                        if (!skip) Extra[j] = math.max(Extra[j], penalty);
                    }
            }
        }

        private static readonly int2[] Moves =
        {
            new int2(1, 0), new int2(-1, 0), new int2(0, 1), new int2(0, -1),
            new int2(1, 1), new int2(1, -1), new int2(-1, 1), new int2(-1, -1),
            new int2(2, 1), new int2(2, -1), new int2(-2, 1), new int2(-2, -1),
            new int2(1, 2), new int2(1, -2), new int2(-1, 2), new int2(-1, -2),
        };

        private float StepCost(int from, int to, float len)
        {
            float grade = math.abs(H[to] - H[from]) / len;
            float g = MaxGrade > 1e-3f ? grade / MaxGrade : 0f;
            float gradeCost = g <= 1f ? g * 0.6f : 0.6f + (g - 1f) * 25f + 2f;
            float m = 1f + Wander * Noise[to] * 1.4f + gradeCost + Extra[to];
            switch (Preference)
            {
                case TrailPreference.High: m += (1f - Rank(H[to])) * 0.9f; break;
                case TrailPreference.Low: m += Rank(H[to]) * 0.9f; break;
            }
            if (Edge[to] < 12f) m += (12f - Edge[to]) / 12f;
            if (Wet(to)) m += 3f;
            return len * m;
        }

        private bool Passable(int i) => Ok[i] && (Bridges || !Wet(i));

        /// <summary>
        /// Cheapest cell route from <paramref name="start"/> to <paramref name="goal"/>, or to any cell of
        /// <paramref name="goals"/> when given (Dijkstra). Null when unreachable or cancelled.
        /// </summary>
        public List<int> Route(int start, int goal, bool[] goals, CancellationToken cancel)
        {
            if (start < 0 || (goal < 0 && goals == null)) return null;
            int n = Count;
            var g = new float[n];
            var prev = new int[n];
            var closed = new bool[n];
            for (int i = 0; i < n; i++) { g[i] = float.MaxValue; prev[i] = -1; }
            var heap = new MinHeap(256);
            g[start] = 0f;
            float2 goalP = goal >= 0 ? Centre(goal) : default;
            heap.Push(start, H0(start, goal, goalP));
            int found = -1, pops = 0;
            while (heap.Count > 0)
            {
                int cur = heap.Pop();
                if (closed[cur]) continue;
                closed[cur] = true;
                if ((++pops & 2047) == 0) cancel.ThrowIfCancellationRequested();
                if (cur == goal || (goals != null && goals[cur] && cur != start)) { found = cur; break; }
                int cx = cur % Nx, cy = cur / Nx;
                foreach (var mv in Moves)
                {
                    int x = cx + mv.x, y = cy + mv.y;
                    if (x < 0 || y < 0 || x >= Nx || y >= Ny) continue;
                    int j = y * Nx + x;
                    if (closed[j] || !(Passable(j) || j == goal)) continue;
                    // Knight moves cross two cells: both must be passable.
                    if (math.abs(mv.x) + math.abs(mv.y) == 3)
                    {
                        int sx = math.sign(mv.x), sy = math.sign(mv.y);
                        int a, b;
                        if (math.abs(mv.x) == 2) { a = cy * Nx + cx + sx; b = (cy + sy) * Nx + cx + sx; }
                        else { a = (cy + sy) * Nx + cx; b = (cy + sy) * Nx + cx + sx; }
                        if (!Passable(a) || !Passable(b)) continue;
                    }
                    else if (mv.x != 0 && mv.y != 0)
                    {
                        if (!Passable(cy * Nx + x) && !Passable(y * Nx + cx)) continue;
                    }
                    float len = Cell * math.length(new float2(mv));
                    float ng = g[cur] + StepCost(cur, j, len);
                    if (ng >= g[j]) continue;
                    g[j] = ng; prev[j] = cur;
                    heap.Push(j, ng + H0(j, goal, goalP));
                }
            }
            if (found < 0) return null;
            var path = new List<int>();
            for (int c = found; c >= 0; c = prev[c]) path.Add(c);
            path.Reverse();
            return path;
        }

        private float H0(int i, int goal, float2 goalP) => goal >= 0 ? math.distance(Centre(i), goalP) : 0f;

        private sealed class MinHeap
        {
            private int[] _items; private float[] _keys; public int Count;
            public MinHeap(int cap) { _items = new int[cap]; _keys = new float[cap]; }
            public void Push(int item, float key)
            {
                if (Count == _items.Length) { Array.Resize(ref _items, Count * 2); Array.Resize(ref _keys, Count * 2); }
                int i = Count++;
                while (i > 0)
                {
                    int p = (i - 1) >> 1;
                    if (_keys[p] <= key) break;
                    _items[i] = _items[p]; _keys[i] = _keys[p]; i = p;
                }
                _items[i] = item; _keys[i] = key;
            }
            public int Pop()
            {
                int top = _items[0];
                int item = _items[--Count]; float key = _keys[Count];
                int i = 0;
                while (true)
                {
                    int l = 2 * i + 1;
                    if (l >= Count) break;
                    int r = l + 1, m = r < Count && _keys[r] < _keys[l] ? r : l;
                    if (_keys[m] >= key) break;
                    _items[i] = _items[m]; _keys[i] = _keys[m]; i = m;
                }
                if (Count > 0) { _items[i] = item; _keys[i] = key; }
                return top;
            }
        }
    }

    /// <summary>Polyline helpers for the trail.</summary>
    public static class TrailLine
    {
        /// <summary>Douglas–Peucker that never removes points flagged in <paramref name="keep"/>.</summary>
        public static List<float2> Simplify(List<float2> pts, bool[] keep, float tolerance)
        {
            int n = pts.Count;
            if (n <= 2) return new List<float2>(pts);
            var mark = new bool[n];
            mark[0] = mark[n - 1] = true;
            if (keep != null) for (int i = 0; i < n; i++) if (keep[i]) mark[i] = true;
            // Simplify each stretch between kept points independently.
            int s = 0;
            for (int e = 1; e < n; e++)
            {
                if (!mark[e]) continue;
                var stack = new Stack<int2>();
                stack.Push(new int2(s, e));
                while (stack.Count > 0)
                {
                    var r = stack.Pop();
                    float best = -1f; int bi = -1;
                    for (int i = r.x + 1; i < r.y; i++)
                    {
                        float d = math.sqrt(SegmentIndex.DistSq(pts[i], pts[r.x], pts[r.y]));
                        if (d > best) { best = d; bi = i; }
                    }
                    if (bi >= 0 && best > tolerance) { mark[bi] = true; stack.Push(new int2(r.x, bi)); stack.Push(new int2(bi, r.y)); }
                }
                s = e;
            }
            var result = new List<float2>();
            for (int i = 0; i < n; i++) if (mark[i]) result.Add(pts[i]);
            return result;
        }
    }
}
