// Placemaker — convex decomposition by extending reflex edges.
//
// Algorithm idea from ParkingLotTool (KruemelGames, GPL-3.0), Geometry/Zellen/Zerlegung.cs
// (Konvexzerlegung): at a reflex corner the incoming edge is extended as a ray to the nearest
// non-adjacent edge and the polygon is cut there; recurse until every part is convex. New cut points
// are shared vertex objects (both parts reference the same vertex index, so seams match bit-exactly),
// and a ray that hits a vertex (within 1e-7 of the edge length, measured in PLT on an L-shape) is
// treated as hitting that vertex instead of creating a zero-length edge. Own implementation.
//
// Properties: every cut removes one reflex corner and creates none, so parts ≤ reflex corners + 1.
// Cuts continue existing edges, which keeps parking rows and plaza grids aligned with the outline.

using System;
using System.Collections.Generic;
using Unity.Mathematics;

namespace Placemaker.Core.Geometry
{
    public static class ConvexDecomposition
    {
        /// <summary>
        /// Convex parts of a simple ring (any orientation; it is cleaned first). Parts are CCW and share
        /// their seam vertices exactly. A part may contain a straight-through vertex where a seam ends
        /// on its edge (kept so neighbours stay T-junction free); pass removeCollinear to drop those.
        /// Empty list for degenerate input.
        /// </summary>
        public static List<float2[]> Decompose(IReadOnlyList<float2> ring, bool removeCollinear = false)
        {
            var result = new List<float2[]>();
            var c = Poly.Clean(ring, 1e-3f);
            if (c.Length < 3) return result;
            var verts = new List<double2>(c.Length * 2);
            double2 o = c[0];
            foreach (var p in c) verts.Add((double2)p - o);
            var start = new List<int>(c.Length);
            for (int i = 0; i < c.Length; i++) start.Add(i);

            var stack = new ListStack<List<int>>();
            stack.Push(start);
            int guard = 4 * c.Length + 16;
            var parts = new List<List<int>>();
            var straight = new HashSet<int>(); // vertices whose "reflex" turn is rounding noise
            while (stack.Count > 0)
            {
                var poly = stack.Pop();
                if (guard-- <= 0) { parts.Add(poly); continue; }
                int r = FirstReflex(verts, poly, straight);
                if (r < 0) { parts.Add(poly); continue; }
                int res = SplitAt(verts, poly, r, out var a, out var b);
                if (res == Straight) { straight.Add(poly[r]); stack.Push(poly); continue; }
                if (res == Failed) { parts.Add(poly); continue; }
                stack.Push(b);
                stack.Push(a);
            }
            foreach (var p in parts)
            {
                var ring2 = new float2[p.Count];
                for (int i = 0; i < ring2.Length; i++) ring2[i] = (float2)(verts[p[i]] + o);
                if (removeCollinear) ring2 = Poly.RemoveCollinear(ring2, 1e-6f);
                if (ring2.Length >= 3 && Poly.SignedAreaD(ring2) > 0) result.Add(ring2);
            }
            return result;
        }

        private const int Failed = 0, Done = 1, Straight = 2;

        private static int FirstReflex(List<double2> v, List<int> poly, HashSet<int> straight)
        {
            int n = poly.Count;
            for (int i = 0; i < n; i++)
            {
                if (straight.Contains(poly[i])) continue;
                double2 a = v[poly[(i - 1 + n) % n]], b = v[poly[i]], c = v[poly[(i + 1) % n]];
                // Relative tolerance: seam points are rounded, a straight-through seam end must not count as reflex.
                if (Poly.CrossD(b - a, c - b) < -1e-10 * math.length(b - a) * math.length(c - b)) return i;
            }
            return -1;
        }

        private static int SplitAt(List<double2> v, List<int> poly, int r, out List<int> first, out List<int> second)
        {
            first = second = null;
            int n = poly.Count;
            double2 a = v[poly[(r - 1 + n) % n]], b = v[poly[r]];
            double2 ray = b - a;
            double bestS = double.PositiveInfinity, bestT = 0;
            int bestEdge = -1;
            for (int e = 0; e < n; e++)
            {
                int e1 = (e + 1) % n;
                if (e == r || e1 == r) continue; // edges touching the reflex vertex
                double2 q = v[poly[e]], ev = v[poly[e1]] - q;
                double den = Poly.CrossD(ray, ev);
                if (den == 0) continue;
                double2 delta = q - b;
                double s = Poly.CrossD(delta, ev) / den;     // along the ray
                double t = Poly.CrossD(delta, ray) / den;    // along the edge
                double len = math.length(ev);
                if (math.abs(t) * len <= 1e-7) t = 0;
                else if (math.abs(1 - t) * len <= 1e-7) t = 1;
                if (s <= 0 || t < 0 || t > 1 || s >= bestS) continue;
                bestS = s; bestT = t; bestEdge = e;
            }
            if (bestEdge < 0) return Failed;

            if (bestT == 0 || bestT == 1)
            {
                int target = bestT == 0 ? bestEdge : (bestEdge + 1) % n;
                // The extension runs straight into a neighbour: the corner is straight up to rounding.
                if (target == (r + 1) % n || target == (r - 1 + n) % n) return Straight;
                first = Walk(poly, r, target);
                second = Walk(poly, target, r);
            }
            else
            {
                double2 q = v[poly[bestEdge]], q2 = v[poly[(bestEdge + 1) % n]];
                int hit = v.Count;
                v.Add(q + (q2 - q) * bestT);
                // first: r → … → edge start → hit ; second: hit → edge end → … → r
                first = Walk(poly, r, bestEdge);
                first.Add(hit);
                second = new List<int> { hit };
                second.AddRange(Walk(poly, (bestEdge + 1) % n, r));
            }
            return first.Count >= 3 && second.Count >= 3 ? Done : Failed;
        }

        /// <summary>Vertices from index i to index j (inclusive) going forward around the ring.</summary>
        private static List<int> Walk(List<int> poly, int i, int j)
        {
            int n = poly.Count;
            var l = new List<int>();
            int k = i;
            for (int guard = 0; guard <= n; guard++)
            {
                l.Add(poly[k]);
                if (k == j) break;
                k = (k + 1) % n;
            }
            return l;
        }
    }
}
