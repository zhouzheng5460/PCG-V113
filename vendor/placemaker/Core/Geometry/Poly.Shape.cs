// Placemaker — ring shaping: simplify, smooth, resample, preview fill runs, pole of inaccessibility.
//
// SmoothCorners, FillRuns and the moving-average loop smoothing are adapted from Grid Road Generator
// (Ti4goc), Core/FreeAreaPerimeter.cs and Core/BrushMask.cs.
// PoleOfInaccessibility follows the published "polylabel" idea (Mapbox, 2016): best-first search over
// square cells bounded by their centre distance + half diagonal. Own implementation.

using System;
using System.Collections.Generic;
using Unity.Mathematics;

namespace Placemaker.Core.Geometry
{
    public static partial class Poly
    {
        // ───────────────────────────── simplify ─────────────────────────────

        /// <summary>
        /// Douglas–Peucker on a closed ring: removes points closer than tolerance to the kept chord.
        /// Anchored at the two mutually farthest-apart points so the result does not depend on where
        /// the ring starts. If the simplified ring would self-intersect the tolerance is halved (up to
        /// 6 times) — the result is always simple when the input was.
        /// </summary>
        public static float2[] Simplify(IReadOnlyList<float2> ring, float tolerance)
        {
            if (ring == null || ring.Count <= 3 || tolerance <= 0) return ring == null ? Array.Empty<float2>() : ToArray(ring);
            bool inputSimple = IsSimple(ring);
            float tol = tolerance;
            for (int attempt = 0; attempt < 7; attempt++, tol *= 0.5f)
            {
                var s = SimplifyOnce(ring, tol);
                if (s.Length >= 3 && (!inputSimple || IsSimple(s))) return s;
            }
            return ToArray(ring);
        }

        private static float2[] ToArray(IReadOnlyList<float2> r)
        {
            var a = new float2[r.Count];
            for (int i = 0; i < a.Length; i++) a[i] = r[i];
            return a;
        }

        private static float2[] SimplifyOnce(IReadOnlyList<float2> loop, float tolerance)
        {
            int n = loop.Count;
            var keep = new bool[n];
            int first = 0, far = 0;
            for (int i = 1; i < n; i++) if (math.distancesq(loop[i], loop[0]) > math.distancesq(loop[far], loop[0])) far = i;
            for (int i = 0; i < n; i++) if (math.distancesq(loop[i], loop[far]) > math.distancesq(loop[first], loop[far])) first = i;
            keep[first] = keep[far] = true;
            var stack = new ListStack<int2>();
            stack.Push(new int2(first, far));
            stack.Push(new int2(far, first));
            double tol2 = (double)tolerance * tolerance;
            while (stack.Count > 0)
            {
                int2 span = stack.Pop();
                int from = span.x, to = span.y, count = (to - from + n) % n;
                if (count < 2) continue;
                double2 a = loop[from], ab = (double2)loop[to] - a;
                int worst = -1;
                double worstD = tol2;
                for (int s = 1; s < count; s++)
                {
                    int i = (from + s) % n;
                    double d = DistSqToSegmentD(loop[i], a, a + ab);
                    if (d > worstD) { worstD = d; worst = i; }
                }
                if (worst < 0) continue;
                keep[worst] = true;
                stack.Push(new int2(from, worst));
                stack.Push(new int2(worst, to));
            }
            var result = new List<float2>();
            for (int i = 0; i < n; i++) if (keep[i]) result.Add(loop[i]);
            return result.ToArray();
        }

        /// <summary>Douglas–Peucker on an open polyline (ends always kept).</summary>
        public static float2[] SimplifyPolyline(IReadOnlyList<float2> line, float tolerance)
        {
            int n = line?.Count ?? 0;
            if (n <= 2) return n == 0 ? Array.Empty<float2>() : ToArray(line);
            var keep = new bool[n];
            keep[0] = keep[n - 1] = true;
            var stack = new ListStack<int2>();
            stack.Push(new int2(0, n - 1));
            double tol2 = (double)tolerance * tolerance;
            while (stack.Count > 0)
            {
                int2 s = stack.Pop();
                int worst = -1; double wd = tol2;
                for (int i = s.x + 1; i < s.y; i++)
                {
                    double d = DistSqToSegmentD(line[i], line[s.x], line[s.y]);
                    if (d > wd) { wd = d; worst = i; }
                }
                if (worst < 0) continue;
                keep[worst] = true;
                stack.Push(new int2(s.x, worst));
                stack.Push(new int2(worst, s.y));
            }
            var r = new List<float2>();
            for (int i = 0; i < n; i++) if (keep[i]) r.Add(line[i]);
            return r.ToArray();
        }

        // ───────────────────────────── smoothing ─────────────────────────────

        /// <summary>
        /// Rounds every corner with a circular arc tangent to both edges (radius, reduced when an edge
        /// is too short to host it — at most 45 % of each adjacent edge), sampled every arcStep metres.
        /// Straight edges stay one segment. Nearly straight corners (&gt; 175°) are left alone.
        /// Adapted from Grid Road Generator (Ti4goc), FreeAreaPerimeter.Smooth.
        /// </summary>
        public static float2[] SmoothCorners(IReadOnlyList<float2> corners, float radius, float arcStep = 2f)
        {
            int n = corners?.Count ?? 0;
            if (n < 3 || radius <= 0) return n == 0 ? Array.Empty<float2>() : ToArray(corners);
            arcStep = math.max(arcStep, 0.05f);
            var result = new List<float2>();
            for (int i = 0; i < n; i++)
            {
                float2 prev = corners[(i + n - 1) % n], cur = corners[i], next = corners[(i + 1) % n];
                float2 toPrev = prev - cur, toNext = next - cur;
                float lenPrev = math.length(toPrev), lenNext = math.length(toNext);
                if (lenPrev < 1e-4f || lenNext < 1e-4f) continue;
                float2 u = toPrev / lenPrev, v = toNext / lenNext;
                float angle = math.acos(math.clamp(math.dot(u, v), -1f, 1f));
                if (angle > math.radians(175f) || angle < 1e-3f)
                {
                    result.Add(cur);
                    continue;
                }
                float half = angle * 0.5f;
                float tangentDistance = radius / math.tan(half);
                float maxDistance = 0.45f * math.min(lenPrev, lenNext);
                float r = radius;
                if (tangentDistance > maxDistance)
                {
                    tangentDistance = maxDistance;
                    r = tangentDistance * math.tan(half);
                }
                float2 t1 = cur + u * tangentDistance, t2 = cur + v * tangentDistance;
                float2 centre = cur + math.normalize(u + v) * (r / math.sin(half));
                float a1 = math.atan2(t1.y - centre.y, t1.x - centre.x);
                float a2 = math.atan2(t2.y - centre.y, t2.x - centre.x);
                float sweep = a2 - a1;
                while (sweep > math.PI) sweep -= 2f * math.PI;
                while (sweep < -math.PI) sweep += 2f * math.PI;
                int steps = math.max(1, (int)math.ceil(math.abs(sweep) * r / arcStep));
                for (int k = 0; k <= steps; k++)
                {
                    float a = a1 + sweep * k / steps;
                    result.Add(new float2(centre.x + r * math.cos(a), centre.y + r * math.sin(a)));
                }
            }
            float merge = math.min(arcStep * 0.25f, 0.05f);
            for (int i = result.Count - 1; i > 0 && result.Count > 3; i--)
                if (math.distance(result[i], result[i - 1]) < merge) result.RemoveAt(i);
            if (result.Count > 3 && math.distance(result[0], result[result.Count - 1]) < merge) result.RemoveAt(result.Count - 1);
            return result.ToArray();
        }

        /// <summary>3D variant of <see cref="SmoothCorners(IReadOnlyList{float2},float,float)"/>: heights interpolate from the corner.</summary>
        public static float3[] SmoothCorners(IReadOnlyList<float3> corners, float radius, float arcStep = 2f)
        {
            var flat = SmoothCorners(To2D(corners), radius, arcStep);
            var r = new float3[flat.Length];
            for (int i = 0; i < r.Length; i++)
            {
                // Height of the nearest original corner: smoothing never moves far from it.
                float best = float.MaxValue, h = 0;
                foreach (var c in corners)
                {
                    float d = math.distancesq(c.xz, flat[i]);
                    if (d < best) { best = d; h = c.y; }
                }
                r[i] = new float3(flat[i].x, h, flat[i].y);
            }
            return r;
        }

        /// <summary>Chaikin corner cutting (each pass doubles the vertex count; ratio 0.25 = classic).</summary>
        public static float2[] Chaikin(IReadOnlyList<float2> ring, int iterations = 2, float ratio = 0.25f)
        {
            if (ring == null || ring.Count < 3) return ring == null ? Array.Empty<float2>() : ToArray(ring);
            ratio = math.clamp(ratio, 0.01f, 0.49f);
            var cur = new List<float2>(ring);
            for (int it = 0; it < iterations && cur.Count < 1 << 16; it++)
            {
                var next = new List<float2>(cur.Count * 2);
                for (int i = 0; i < cur.Count; i++)
                {
                    float2 a = cur[i], b = cur[(i + 1) % cur.Count];
                    next.Add(math.lerp(a, b, ratio));
                    next.Add(math.lerp(a, b, 1 - ratio));
                }
                cur = next;
            }
            return cur.ToArray();
        }

        /// <summary>Moving-average smoothing of a closed loop (window 2·half+1, repeated passes).</summary>
        public static float2[] SmoothLoop(IReadOnlyList<float2> loop, int passes, int half = 1)
        {
            var result = ToArray(loop);
            int n = result.Length;
            if (n < 3 || passes <= 0 || half <= 0) return result;
            var next = new float2[n];
            for (int pass = 0; pass < passes; pass++)
            {
                for (int k = 0; k < n; k++)
                {
                    float2 sum = float2.zero;
                    for (int d = -half; d <= half; d++) sum += result[((k + d) % n + n) % n];
                    next[k] = sum / (2 * half + 1);
                }
                var t = result; result = next; next = t;
            }
            return result;
        }

        // ───────────────────────────── resampling ─────────────────────────────

        /// <summary>
        /// Points every step metres along the closed ring. keepCorners = true keeps every original
        /// vertex and subdivides each edge evenly (no edge longer than step); false walks the
        /// perimeter at exact spacing (corners may be cut).
        /// </summary>
        public static float2[] Resample(IReadOnlyList<float2> ring, float step, bool keepCorners = false)
        {
            int n = ring?.Count ?? 0;
            if (n < 2 || step <= 0) return n == 0 ? Array.Empty<float2>() : ToArray(ring);
            var result = new List<float2>();
            if (keepCorners)
            {
                for (int i = 0; i < n; i++)
                {
                    float2 a = ring[i], b = ring[(i + 1) % n];
                    int pieces = math.max(1, (int)math.ceil(math.distance(a, b) / step - 1e-4f));
                    for (int k = 0; k < pieces; k++) result.Add(math.lerp(a, b, (float)k / pieces));
                }
                return result.ToArray();
            }
            float total = Perimeter(ring);
            int count = math.max(3, (int)math.round(total / step));
            float spacing = total / count;
            float carry = 0f;
            for (int i = 0; i < n && result.Count < count; i++)
            {
                float2 a = ring[i], b = ring[(i + 1) % n];
                float length = math.distance(a, b);
                float t = carry;
                while (t < length && result.Count < count)
                {
                    result.Add(math.lerp(a, b, t / length));
                    t += spacing;
                }
                carry = t - length;
            }
            return result.ToArray();
        }

        /// <summary>Points every step metres along an open polyline (both ends kept).</summary>
        public static float2[] ResamplePolyline(IReadOnlyList<float2> line, float step)
        {
            int n = line?.Count ?? 0;
            if (n < 2 || step <= 0) return n == 0 ? Array.Empty<float2>() : ToArray(line);
            float total = 0;
            for (int i = 1; i < n; i++) total += math.distance(line[i - 1], line[i]);
            int count = math.max(1, (int)math.round(total / step));
            float spacing = total / count;
            var result = new List<float2> { line[0] };
            int seg = 0; float segStart = 0, segLen = math.distance(line[0], line[1]);
            for (int k = 1; k < count; k++)
            {
                float s = k * spacing;
                while (seg < n - 2 && s > segStart + segLen) { segStart += segLen; seg++; segLen = math.distance(line[seg], line[seg + 1]); }
                result.Add(math.lerp(line[seg], line[seg + 1], segLen > 0 ? math.saturate((s - segStart) / segLen) : 0));
            }
            result.Add(line[n - 1]);
            return result.ToArray();
        }

        // ───────────────────────────── preview fill ─────────────────────────────

        /// <summary>
        /// Even-odd scanline fill: horizontal runs (XZ) every step metres, at most maxLength long.
        /// Drawn as projected lines of width step they cover the interior (overlay can't fill polygons).
        /// Adapted from Grid Road Generator (Ti4goc), FreeAreaPerimeter.FillRuns.
        /// </summary>
        public static List<(float2 a, float2 b)> FillRuns(IReadOnlyList<float2> polygon, float step = 4f, float maxLength = 64f)
        {
            var result = new List<(float2, float2)>();
            int n = polygon?.Count ?? 0;
            if (n < 3 || step <= 0) return result;
            float minZ = float.MaxValue, maxZ = float.MinValue;
            foreach (float2 p in polygon) { minZ = math.min(minZ, p.y); maxZ = math.max(maxZ, p.y); }
            var crossings = new List<float>();
            for (float z = (math.floor(minZ / step) + 0.5f) * step; z < maxZ; z += step)
            {
                crossings.Clear();
                for (int i = 0; i < n; i++)
                {
                    float2 a = polygon[i], b = polygon[(i + 1) % n];
                    if ((a.y > z) != (b.y > z)) crossings.Add(a.x + (z - a.y) / (b.y - a.y) * (b.x - a.x));
                }
                crossings.Sort();
                for (int k = 0; k + 1 < crossings.Count; k += 2)
                {
                    float from = crossings[k], to = crossings[k + 1];
                    int pieces = math.max(1, (int)math.ceil((to - from) / math.max(maxLength, step)));
                    for (int piece = 0; piece < pieces; piece++)
                        result.Add((new float2(math.lerp(from, to, (float)piece / pieces), z), new float2(math.lerp(from, to, (float)(piece + 1) / pieces), z)));
                }
            }
            return result;
        }

        public static List<(float2 a, float2 b)> FillRuns(IReadOnlyList<float3> polygon, float step = 4f, float maxLength = 64f) =>
            FillRuns(To2D(polygon), step, maxLength);

        // ───────────────────────────── pole of inaccessibility ─────────────────────────────

        /// <summary>Interior point farthest from the boundary (centre of the largest inscribed circle), within precision metres.</summary>
        public static float2 PoleOfInaccessibility(IReadOnlyList<float2> ring, out float distance, float precision = 0f)
        {
            return PoleOfInaccessibility(new Region(ToArray(ring)), out distance, precision);
        }

        /// <summary>Pole of inaccessibility of a region with holes. precision ≤ 0 = 0.1 % of the size (≥ 1 cm).</summary>
        public static float2 PoleOfInaccessibility(Region region, out float distance, float precision = 0f)
        {
            distance = 0;
            if (region == null || region.Outer == null || region.Outer.Length < 3) return default;
            var rings = new List<float2[]>(region.Rings());
            Bounds(region.Outer, out var mn, out var mx);
            double2 o = (double2)(mn + mx) * 0.5;
            double2 size = (double2)(mx - mn);
            double cell = math.min(size.x, size.y);
            if (cell <= 0) return Centroid(region.Outer);
            if (precision <= 0) precision = (float)math.max(0.01, math.max(size.x, size.y) * 1e-3);
            double h = cell / 2;

            double Sd(double2 p) => SignedDistance(rings, p + o);

            var heap = new CellHeap();
            for (double x = mn.x - o.x; x < mx.x - o.x; x += cell)
                for (double y = mn.y - o.y; y < mx.y - o.y; y += cell)
                    heap.Push(MakeCell(new double2(x + h, y + h), h, Sd));

            var c0 = Centroid(region.Outer);
            var best = MakeCell((double2)c0 - o, 0, Sd);
            var bbox = MakeCell(double2.zero, 0, Sd);
            if (bbox.D > best.D) best = bbox;

            int guard = 200000;
            while (heap.Count > 0 && guard-- > 0)
            {
                var c = heap.Pop();
                if (c.D > best.D) best = c;
                if (c.Max - best.D <= precision) continue;
                double hh = c.H / 2;
                heap.Push(MakeCell(c.C + new double2(-hh, -hh), hh, Sd));
                heap.Push(MakeCell(c.C + new double2(hh, -hh), hh, Sd));
                heap.Push(MakeCell(c.C + new double2(-hh, hh), hh, Sd));
                heap.Push(MakeCell(c.C + new double2(hh, hh), hh, Sd));
            }
            distance = (float)math.max(0, best.D);
            return (float2)(best.C + o);
        }

        private struct Cell
        {
            public double2 C;
            public double H, D, Max;
        }

        private static Cell MakeCell(double2 c, double h, Func<double2, double> sd)
        {
            double d = sd(c);
            return new Cell { C = c, H = h, D = d, Max = d + h * math.SQRT2 };
        }

        /// <summary>Positive inside (even-odd over all rings), negative outside.</summary>
        private static double SignedDistance(List<float2[]> rings, double2 p)
        {
            bool inside = false;
            double best = double.MaxValue;
            foreach (var r in rings)
            {
                int n = r.Length;
                for (int i = 0, j = n - 1; i < n; j = i++)
                {
                    double2 a = r[i], b = r[j];
                    if ((a.y > p.y) != (b.y > p.y) && p.x < (b.x - a.x) * (p.y - a.y) / (b.y - a.y) + a.x) inside = !inside;
                    best = math.min(best, DistSqToSegmentD(p, a, b));
                }
            }
            return (inside ? 1 : -1) * math.sqrt(best);
        }

        /// <summary>Max-heap on Cell.Max (net48 has no PriorityQueue).</summary>
        private sealed class CellHeap
        {
            private readonly List<Cell> _a = new List<Cell>();
            public int Count => _a.Count;

            public void Push(Cell c)
            {
                _a.Add(c);
                int i = _a.Count - 1;
                while (i > 0)
                {
                    int p = (i - 1) >> 1;
                    if (_a[p].Max >= _a[i].Max) break;
                    var t = _a[p]; _a[p] = _a[i]; _a[i] = t; i = p;
                }
            }

            public Cell Pop()
            {
                var top = _a[0];
                int last = _a.Count - 1;
                _a[0] = _a[last];
                _a.RemoveAt(last);
                int i = 0;
                while (true)
                {
                    int l = 2 * i + 1, r = l + 1, m = i;
                    if (l < _a.Count && _a[l].Max > _a[m].Max) m = l;
                    if (r < _a.Count && _a[r].Max > _a[m].Max) m = r;
                    if (m == i) break;
                    var t = _a[m]; _a[m] = _a[i]; _a[i] = t; i = m;
                }
                return top;
            }
        }
    }
}
