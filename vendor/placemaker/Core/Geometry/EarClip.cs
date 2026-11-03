// Placemaker — triangulation.
//
// Two triangulators on purpose:
//  * Triangulate: robust ear clipping in double for our own meshes (filled previews, rescue splits).
//  * GameTriangulate: a model of what CS2 does with an area ring, used only to PREDICT acceptance.
//    It is written from the behaviour documented in ParkingLotTool's notes (KruemelGames, GPL-3.0,
//    Geometry/Cs2Triangulierung.cs comments — the code there is a port of proprietary game code and
//    is deliberately NOT used): plain ear clipping of the ring in float at world coordinates, ear test
//    = float cross product of the two edge vectors must reach float.Epsilon, an attempt budget of
//    2 × remaining vertices that resets after every clipped ear, and the area is dropped unless
//    exactly n − 2 triangles result. The traversal is the classic public-domain ear clipper
//    (J. W. Ratcliff, flipcode 2000) which matches that description.

using System;
using System.Collections.Generic;
using Unity.Mathematics;

namespace Placemaker.Core.Geometry
{
    /// <summary>Ear clipping. Returns false on failure.</summary>
    public static class EarClip
    {
        /// <summary>
        /// Robust ear clipping (double precision, either orientation). On success returns n − 2
        /// triangles of indices into ring, each counter-clockwise. Collinear vertices are handled;
        /// fails (false) only for self-intersecting or degenerate rings.
        /// </summary>
        public static bool Triangulate(IReadOnlyList<float2> ring, out List<int3> triangles)
        {
            triangles = new List<int3>();
            int n = ring?.Count ?? 0;
            if (n < 3) return false;
            double2 o = ring[0];
            var p = new double2[n];
            for (int i = 0; i < n; i++)
            {
                if (!Poly.IsFinite(ring[i])) return false;
                p[i] = (double2)ring[i] - o;
            }
            return TriangulateD(p, triangles);
        }

        internal static bool TriangulateD(double2[] p, List<int3> triangles)
        {
            int n = p.Length;
            double area = 0;
            for (int i = 0, j = n - 1; i < n; j = i++) area += Poly.CrossD(p[j], p[i]);
            if (area == 0 || double.IsNaN(area)) return false;
            bool ccw = area > 0;
            // Work in CCW order through an index map.
            var map = new int[n];
            for (int i = 0; i < n; i++) map[i] = ccw ? i : n - 1 - i;
            var prev = new int[n];
            var next = new int[n];
            for (int i = 0; i < n; i++) { prev[i] = (i - 1 + n) % n; next[i] = (i + 1) % n; }

            double2 P(int k) => p[map[k]];
            double Turn(int k) => Poly.CrossD(P(k) - P(prev[k]), P(next[k]) - P(k));

            int remaining = n, cur = 0, stall = 0;
            bool allowDegenerate = false;
            while (remaining > 3)
            {
                if (IsEar(cur, allowDegenerate))
                {
                    int a = prev[cur], c = next[cur];
                    triangles.Add(new int3(map[a], map[cur], map[c]));
                    next[a] = c; prev[c] = a;
                    remaining--;
                    cur = a;
                    stall = 0;
                    allowDegenerate = false;
                    continue;
                }
                cur = next[cur];
                if (++stall > remaining)
                {
                    if (allowDegenerate) return false;
                    allowDegenerate = true;
                    stall = 0;
                }
            }
            int x = cur, y = next[x], z = next[y];
            triangles.Add(new int3(map[x], map[y], map[z]));
            return triangles.Count == n - 2;

            bool IsEar(int k, bool degenerate)
            {
                double t = Turn(k);
                if (degenerate ? t < 0 : t <= 0) return false;
                double2 a = P(prev[k]), b = P(k), c = P(next[k]);
                if (degenerate && t == 0)
                {
                    // Collinear vertex: removing it adds a zero-area triangle and never crosses anything,
                    // provided it lies between its neighbours (not a fold-back).
                    return math.dot(b - a, c - b) >= 0;
                }
                int q = next[next[k]];
                while (q != prev[k])
                {
                    double2 v = P(q);
                    if (!(math.all(v == a) || math.all(v == b) || math.all(v == c)) && InTriangle(v, a, b, c)) return false;
                    q = next[q];
                }
                return true;
            }
        }

        private static bool InTriangle(double2 v, double2 a, double2 b, double2 c) =>
            Poly.CrossD(b - a, v - a) >= 0 && Poly.CrossD(c - b, v - b) >= 0 && Poly.CrossD(a - c, v - c) >= 0;

        /// <summary>
        /// Triangulates a region with holes (holes opened with seams first). vertices are the pieces'
        /// vertices concatenated; triangles index into them. Used for filled previews.
        /// </summary>
        public static bool Triangulate(Region region, out float2[] vertices, out List<int3> triangles)
        {
            triangles = new List<int3>();
            var verts = new List<float2>();
            vertices = Array.Empty<float2>();
            if (region == null) return false;
            var pieces = region.HasHoles ? Poly.CutHoles(region) : new List<float2[]> { region.Outer };
            bool ok = pieces.Count > 0;
            foreach (var piece in pieces)
            {
                if (!Triangulate(piece, out var t)) { ok = false; continue; }
                int b = verts.Count;
                verts.AddRange(piece);
                foreach (var tri in t) triangles.Add(tri + b);
            }
            vertices = verts.ToArray();
            return ok;
        }

        /// <summary>
        /// Model of CS2's area triangulation on the ring it actually triangulates (already inset — see
        /// <see cref="SurfaceAcceptance.GameInsetRing"/>). Returns the number of triangles (n − 2) or
        /// 0 when the game would drop the area. Ring must be CCW; float arithmetic on purpose: the ear
        /// test's cancellation depends on the distance from the world origin.
        /// </summary>
        public static int GameTriangulate(IReadOnlyList<float2> ring, List<int3> triangles = null)
        {
            int n = ring?.Count ?? 0;
            triangles?.Clear();
            if (n < 3) return 0;
            var v = new int[n];
            for (int i = 0; i < n; i++) v[i] = i;
            int nv = n, made = 0;
            int count = 2 * nv;
            for (int vi = nv - 1; nv > 2;)
            {
                if (0 >= count--) return 0;
                int u = vi; if (nv <= u) u = 0;
                vi = u + 1; if (nv <= vi) vi = 0;
                int w = vi + 1; if (nv <= w) w = 0;
                if (Snip(ring, u, vi, w, nv, v))
                {
                    if (made == n - 2) return 0;
                    triangles?.Add(new int3(v[u], v[vi], v[w]));
                    made++;
                    for (int s = vi, t = vi + 1; t < nv; s++, t++) v[s] = v[t];
                    nv--;
                    count = 2 * nv;
                }
            }
            return made == n - 2 ? made : 0;
        }

        private static bool Snip(IReadOnlyList<float2> r, int u, int vi, int w, int nv, int[] v)
        {
            float2 a = r[v[u]], b = r[v[vi]], c = r[v[w]];
            float2 f = (b - a) * (c - a).yx;
            if (f.x - f.y < float.Epsilon) return false;
            for (int k = 0; k < nv; k++)
            {
                if (k == u || k == vi || k == w) continue;
                if (InTriangleF(r[v[k]], a, b, c)) return false;
            }
            return true;
        }

        private static bool InTriangleF(float2 p, float2 a, float2 b, float2 c)
        {
            float2 ab = b - a, bc = c - b, ca = a - c;
            return ab.x * (p.y - a.y) - ab.y * (p.x - a.x) >= 0f &&
                   bc.x * (p.y - b.y) - bc.y * (p.x - b.x) >= 0f &&
                   ca.x * (p.y - c.y) - ca.y * (p.x - c.x) >= 0f;
        }
    }
}
