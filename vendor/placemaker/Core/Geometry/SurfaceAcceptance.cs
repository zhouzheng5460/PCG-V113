// Placemaker — will CS2 keep this area ring, and how to make it keep it.
//
// Reimplemented from documented behaviour (RESEARCH-NOTES rule 6; ParkingLotTool's measured notes by
// KruemelGames, GPL-3.0): CS2 moves every node of the ring 0.1 m inwards (miter at the corner, no
// extension for nearly straight corners), ear-clips the result in float at world coordinates with an
// attempt budget of 2n, and silently drops the WHOLE area unless exactly n − 2 triangles result.
// Measured in game by PLT: a 0.0008 m neck can survive while a quad with a 0.14 m edge is dropped —
// no length/area/angle rule explains it, only "survive the 0.1 m inset, then ear-clip".
//
// IsAccepted = that model + our own safety policy (edges ≥ 0.3 m, simple ring, simple inset ring,
// no hairline). MakeAcceptable never returns a ring IsAccepted rejects.

using System;
using System.Collections.Generic;
using Unity.Mathematics;

namespace Placemaker.Core.Geometry
{
    /// <summary>Why a ring would be (or was) dropped. Useful for PlanIssues and diagnostics.</summary>
    public enum SurfaceVerdict
    {
        Accepted = 0,
        TooFewPoints = 1,
        NotFinite = 2,
        NotSimple = 3,
        ShortEdge = 4,
        Hairline = 5,
        InsetCollapses = 6,
        EarClipFails = 7,
    }

    /// <summary>
    /// CS2 area acceptance. CS2 insets every area ring by 0.1 m, ear-clips it in float precision and
    /// silently drops the whole area if triangulation fails (RESEARCH-NOTES rule 6). Areas have no holes.
    /// </summary>
    public static class SurfaceAcceptance
    {
        public const float GameInset = 0.1f;
        public const float MinEdge = 0.4f;
        /// <summary>Rings whose mean width 2A/P is below this (m) are hairlines: dropped, never rescued.</summary>
        public const float HairlineWidth = 0.05f;

        /// <summary>True when CS2 will keep this ring (simple, CCW-able, triangulates after the 0.1 m inset).</summary>
        public static bool IsAccepted(IReadOnlyList<float2> ring) => Diagnose(ring) == SurfaceVerdict.Accepted;

        public static bool IsAccepted(IReadOnlyList<float3> ring) => ring != null && IsAccepted(Poly.To2D(ring));

        /// <summary>Full verdict: our safety policy first, then the game model.</summary>
        public static SurfaceVerdict Diagnose(IReadOnlyList<float2> ring)
        {
            var v = Prepare(ring, out var r);
            if (v != SurfaceVerdict.Accepted) return v;
            if (Poly.MinEdgeLength(r) < MinEdge - 1e-4f) return SurfaceVerdict.ShortEdge;
            if (!Poly.IsSimple(r)) return SurfaceVerdict.NotSimple;
            if (IsHairline(r)) return SurfaceVerdict.Hairline;
            return GameVerdict(r);
        }

        /// <summary>
        /// Pure model of the game, without our margins (edges, hairline). Mostly for diagnostics and
        /// tests: emit only rings that pass <see cref="IsAccepted(IReadOnlyList{float2})"/>.
        /// </summary>
        public static bool GameWouldKeep(IReadOnlyList<float2> ring)
        {
            if (Prepare(ring, out var r) != SurfaceVerdict.Accepted) return false;
            var inset = GameInsetRing(r);
            return EarClip.GameTriangulate(inset) == r.Length - 2;
        }

        private static SurfaceVerdict GameVerdict(float2[] r)
        {
            var inset = GameInsetRing(r);
            if (!Poly.IsSimple(inset) || Poly.SignedAreaD(inset) <= 0) return SurfaceVerdict.InsetCollapses;
            // An edge running backwards after the inset means that part of the ring collapsed through
            // itself. A triangle even comes out point-mirrored but still CCW (the game would keep it as
            // a garbage triangle); policy: refuse.
            int n = r.Length;
            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;
                if (math.dot(inset[j] - inset[i], r[j] - r[i]) <= 0) return SurfaceVerdict.InsetCollapses;
            }
            return EarClip.GameTriangulate(inset) == r.Length - 2 ? SurfaceVerdict.Accepted : SurfaceVerdict.EarClipFails;
        }

        private static SurfaceVerdict Prepare(IReadOnlyList<float2> ring, out float2[] r)
        {
            r = null;
            if (ring == null || ring.Count < 3) return SurfaceVerdict.TooFewPoints;
            var list = new List<float2>(ring.Count);
            foreach (var p in ring)
            {
                if (!Poly.IsFinite(p)) return SurfaceVerdict.NotFinite;
                list.Add(p);
            }
            // CS2 stores the ring without a repeated closing point.
            while (list.Count > 1 && math.all(list[0] == list[list.Count - 1])) list.RemoveAt(list.Count - 1);
            if (list.Count < 3) return SurfaceVerdict.TooFewPoints;
            r = Poly.EnsureCcw(list.ToArray());
            return SurfaceVerdict.Accepted;
        }

        /// <summary>Mean width 2A/P below <see cref="HairlineWidth"/>.</summary>
        public static bool IsHairline(IReadOnlyList<float2> ring) => Poly.MeanWidth(ring) < HairlineWidth;

        /// <summary>
        /// The ring CS2 actually triangulates: every node moved inset metres inwards along the miter
        /// of its corner (float, like the game). Nearly straight corners get the plain perpendicular.
        /// Look at this ring, not the sent one, to see why an area vanished.
        /// </summary>
        public static float2[] GameInsetRing(IReadOnlyList<float2> ccwRing, float inset = GameInset)
        {
            int n = ccwRing.Count;
            var r = new float2[n];
            for (int i = 0; i < n; i++)
            {
                float2 prev = ccwRing[(i - 1 + n) % n], cur = ccwRing[i], next = ccwRing[(i + 1) % n];
                float2 d1 = math.normalizesafe(cur - prev), d2 = math.normalizesafe(next - cur);
                float2 n1 = new float2(-d1.y, d1.x);
                float sin = d1.x * d2.y - d1.y * d2.x, cos = math.dot(d1, d2);
                // Point on both shifted edges: n1·inset + d1·k·inset with k = −tan(turn/2).
                float tanHalfTurn = math.abs(1f + cos) > 1e-12f ? sin / (1f + cos) : float.PositiveInfinity * math.sign(sin);
                float k = math.abs(tanHalfTurn) < 0.001f ? 0f : -tanHalfTurn;
                if (float.IsInfinity(k) || float.IsNaN(k)) k = 0f; // full fold-back: no defined miter
                r[i] = cur + (n1 + d1 * k) * inset;
            }
            return r;
        }

        // ───────────────────────────── rescue ─────────────────────────────

        /// <summary>
        /// Returns rings CS2 will accept covering (nearly) the same area: cleaned, and split into
        /// pieces when needed. Empty list = nothing usable. Heights come from heightAt.
        /// </summary>
        public static List<float3[]> MakeAcceptable(IReadOnlyList<float2> ring, Func<float2, float> heightAt)
        {
            var result = new List<float3[]>();
            foreach (var r in MakeAcceptable2D(ring, out _)) result.Add(Poly.To3D(r, heightAt));
            return result;
        }

        public static List<float3[]> MakeAcceptable(Region region, Func<float2, float> heightAt)
        {
            var result = new List<float3[]>();
            foreach (var r in MakeAcceptable2D(region, out _)) result.Add(Poly.To3D(r, heightAt));
            return result;
        }

        public static List<float2[]> MakeAcceptable2D(IReadOnlyList<float2> ring) => MakeAcceptable2D(ring, out _);

        /// <summary>
        /// 2D rescue. Pipeline: clean (0.3 m edges, needles, collinear), resolve self-intersections
        /// into pieces (holes they enclose are opened with seams), drop hairlines, keep accepted
        /// rings as they are; split rejected ones recursively along triangulation chords (vertices
        /// shared exactly, area preserved), then merge neighbouring pieces back whenever the union is
        /// still accepted. Pieces that stay rejected are dropped — a rejected ring never comes back.
        /// droppedArea reports the area lost (m²).
        /// </summary>
        public static List<float2[]> MakeAcceptable2D(IReadOnlyList<float2> ring, out float droppedArea)
        {
            var result = new List<float2[]>();
            droppedArea = 0;
            if (ring == null || ring.Count < 3) return result;
            double inArea = math.abs(Poly.SignedAreaD(ring));
            foreach (var region in Poly.CleanAll(ring, MinEdge))
                foreach (var piece in region.HasHoles ? Poly.CutHoles(region) : new List<float2[]> { region.Outer })
                    Rescue(piece, result);
            droppedArea = (float)math.max(0, inArea - SumArea(result));
            return result;
        }

        /// <summary>Region variant: holes are cut first (CS2 areas cannot have holes).</summary>
        public static List<float2[]> MakeAcceptable2D(Region region, out float droppedArea)
        {
            var result = new List<float2[]>();
            droppedArea = 0;
            if (region == null) return result;
            double inArea = region.Area;
            foreach (var r in Poly.Boolean(new[] { region }, null, BoolOp.Union))
                foreach (var piece in r.HasHoles ? Poly.CutHoles(r) : new List<float2[]> { r.Outer })
                    foreach (var cleaned in Poly.CleanAll(piece, MinEdge))
                        foreach (var p2 in cleaned.HasHoles ? Poly.CutHoles(cleaned) : new List<float2[]> { cleaned.Outer })
                            Rescue(p2, result);
            droppedArea = (float)math.max(0, inArea - SumArea(result));
            return result;
        }

        /// <summary>
        /// CS2 areas cannot have holes: opens every hole with seams (corridorWidth = 0, nothing lost)
        /// or a removed corridor strip (corridorWidth &gt; 0), and returns accepted hole-free rings.
        /// </summary>
        public static List<float2[]> CutHoles(IReadOnlyList<float2> outer, IReadOnlyList<IReadOnlyList<float2>> holes, float corridorWidth = 0f)
        {
            var region = new Region(Poly.EnsureCcw(ToArray(outer)));
            if (holes != null) foreach (var h in holes) if (h != null && h.Count >= 3) region.Holes.Add(Poly.EnsureCw(ToArray(h)));
            var result = new List<float2[]>();
            foreach (var r in Poly.Boolean(new[] { region }, null, BoolOp.Union))
                foreach (var piece in r.HasHoles ? Poly.CutHoles(r, corridorWidth) : new List<float2[]> { r.Outer })
                    foreach (var cleaned in Poly.CleanAll(piece, MinEdge))
                        foreach (var p2 in cleaned.HasHoles ? Poly.CutHoles(cleaned, corridorWidth) : new List<float2[]> { cleaned.Outer })
                            Rescue(p2, result);
            return result;
        }

        private static float2[] ToArray(IReadOnlyList<float2> r)
        {
            var a = new float2[r?.Count ?? 0];
            for (int i = 0; i < a.Length; i++) a[i] = r[i];
            return a;
        }

        private static double SumArea(List<float2[]> rings)
        {
            double s = 0;
            foreach (var r in rings) s += math.abs(Poly.SignedAreaD(r));
            return s;
        }

        private static void Rescue(float2[] ring, List<float2[]> output)
        {
            var c = Poly.Clean(ring, MinEdge);
            if (c.Length < 3 || IsHairline(c)) return;
            if (IsAccepted(c)) { output.Add(c); return; }

            // Pieces are index lists into c: neighbours share vertices bit-exactly (no T-gaps).
            var leaves = new List<List<int>>();
            var all = new List<int>(c.Length);
            for (int i = 0; i < c.Length; i++) all.Add(i);
            SplitRecursive(c, all, leaves, 0);

            var accepted = new List<bool>(leaves.Count);
            foreach (var l in leaves) accepted.Add(IsAccepted(Emit(c, l)));
            MergeLeaves(c, leaves, accepted);
            for (int i = 0; i < leaves.Count; i++)
            {
                if (!accepted[i]) continue;
                var ringOut = Emit(c, leaves[i]);
                if (IsAccepted(ringOut)) output.Add(ringOut);
            }
        }

        private static float2[] Emit(float2[] c, List<int> idx)
        {
            var r = new float2[idx.Count];
            for (int i = 0; i < r.Length; i++) r[i] = c[idx[i]];
            // Only exactly collinear chord ends go: removing them leaves a T-junction, never a gap.
            return Poly.RemoveCollinear(r, 1e-5f);
        }

        private static void SplitRecursive(float2[] c, List<int> poly, List<List<int>> leaves, int depth)
        {
            if (poly.Count <= 3 || depth > 64 || IsAccepted(Emit(c, poly)))
            {
                leaves.Add(poly);
                return;
            }
            var local = new float2[poly.Count];
            for (int i = 0; i < local.Length; i++) local[i] = c[poly[i]];
            if (!EarClip.Triangulate(local, out var tris)) { leaves.Add(poly); return; }

            // Chord choice: most balanced split (by vertex count) among chords ≥ MinEdge; short chords
            // only when nothing else exists. Balanced halves keep the recursion depth at O(log n).
            int m = poly.Count, bestI = -1, bestJ = -1;
            double bestScore = double.MinValue;
            foreach (var t in tris)
            {
                for (int e = 0; e < 3; e++)
                {
                    int a = t[e], b = t[(e + 1) % 3];
                    int i = math.min(a, b), j = math.max(a, b);
                    if (j - i == 1 || (i == 0 && j == m - 1)) continue; // boundary edge
                    double len = math.distance(local[i], local[j]);
                    double balance = math.min(j - i, m - (j - i));
                    double score = balance + (len >= MinEdge ? 1e6 : 0) + math.min(len, 50) * 1e-3;
                    if (score > bestScore) { bestScore = score; bestI = i; bestJ = j; }
                }
            }
            if (bestI < 0) { leaves.Add(poly); return; }
            var p1 = poly.GetRange(bestI, bestJ - bestI + 1);
            var p2 = new List<int>(m - (bestJ - bestI) + 1);
            for (int k = bestJ; k < m; k++) p2.Add(poly[k]);
            for (int k = 0; k <= bestI; k++) p2.Add(poly[k]);
            SplitRecursive(c, p1, leaves, depth + 1);
            SplitRecursive(c, p2, leaves, depth + 1);
        }

        /// <summary>Greedy merge of pieces sharing a chord while the union stays accepted (largest unions first).</summary>
        private static void MergeLeaves(float2[] c, List<List<int>> leaves, List<bool> accepted)
        {
            for (int round = 0; round < 4 * leaves.Count + 4; round++)
            {
                var edgeOwner = new Dictionary<long, int>();
                int bestA = -1, bestB = -1;
                var candidates = new List<KeyValuePair<double, int2>>();
                var keys = new List<long>();
                for (int li = 0; li < leaves.Count; li++)
                {
                    var l = leaves[li];
                    for (int k = 0; k < l.Count; k++)
                    {
                        int u = l[k], v = l[(k + 1) % l.Count];
                        long key = ((long)math.min(u, v) << 32) | (uint)math.max(u, v);
                        if (edgeOwner.TryGetValue(key, out int other) && other != li)
                        {
                            double area = math.abs(Poly.SignedAreaD(Emit(c, leaves[other]))) + math.abs(Poly.SignedAreaD(Emit(c, l)));
                            candidates.Add(new KeyValuePair<double, int2>(area, new int2(other, li)));
                            keys.Add(key);
                        }
                        else edgeOwner[key] = li;
                    }
                }
                if (candidates.Count == 0) return;
                var orderIdx = new int[candidates.Count];
                for (int i = 0; i < orderIdx.Length; i++) orderIdx[i] = i;
                Array.Sort(orderIdx, (x, y) => candidates[y].Key.CompareTo(candidates[x].Key));
                List<int> merged = null;
                foreach (int ci in orderIdx)
                {
                    int2 pair = candidates[ci].Value;
                    var m = MergeAlong(leaves[pair.x], leaves[pair.y], keys[ci]);
                    if (m == null || !IsAccepted(Emit(c, m))) continue;
                    bestA = pair.x; bestB = pair.y; merged = m;
                    break;
                }
                if (merged == null) return;
                int hi = math.max(bestA, bestB), lo = math.min(bestA, bestB);
                leaves[lo] = merged; accepted[lo] = true;
                leaves.RemoveAt(hi); accepted.RemoveAt(hi);
            }
        }

        private static List<int> MergeAlong(List<int> a, List<int> b, long key)
        {
            int u = (int)(key >> 32), v = (int)(key & 0xffffffffL);
            int ia = IndexOfEdge(a, u, v, out bool aForward);
            int ib = IndexOfEdge(b, u, v, out bool bForward);
            if (ia < 0 || ib < 0 || aForward == bForward) return null;
            // a has x→y at ia (x = a[ia]); b has y→x. Merged walk: a from y around to x, then b from x around to y.
            int x = a[ia], y = a[(ia + 1) % a.Count];
            var result = new List<int>(a.Count + b.Count - 2);
            for (int k = 1; k <= a.Count; k++) result.Add(a[(ia + k) % a.Count]); // y ... x
            int jb = b.IndexOf(x);
            for (int k = 1; k < b.Count - 1; k++) result.Add(b[(jb + k) % b.Count]); // after x ... before y
            var seen = new HashSet<int>();
            foreach (int q in result) if (!seen.Add(q)) return null; // shares more than one edge: not simple
            return result;
        }

        private static int IndexOfEdge(List<int> l, int u, int v, out bool forward)
        {
            for (int k = 0; k < l.Count; k++)
            {
                int p = l[k], q = l[(k + 1) % l.Count];
                if (p == u && q == v) { forward = true; return k; }
                if (p == v && q == u) { forward = false; return k; }
            }
            forward = false;
            return -1;
        }
    }
}
