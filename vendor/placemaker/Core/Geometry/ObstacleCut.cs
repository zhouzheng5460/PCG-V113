// Placemaker — cut obstacle footprints (building lots) out of a selection ring (s15).
//
// Plain Paint (and the build safety pass for rings) must never cover an existing building. CS2 areas
// cannot have holes, so a lot enclosed by the ring is opened to the outside with a narrow corridor
// (like Block paint does); a lot on the edge simply notches the ring. Pure geometry (Poly booleans).

using System;
using System.Collections.Generic;
using Unity.Mathematics;

namespace Placemaker.Core.Geometry
{
    public static class ObstacleCut
    {
        /// <summary>Corridor (m) cut from an enclosed obstacle to the outside so the ring needs no hole.</summary>
        public const float Corridor = 1f;

        /// <summary>True when the two polygons overlap by area (touching edges count as overlap).</summary>
        public static bool Overlaps(IReadOnlyList<float2> a, IReadOnlyList<float2> b)
        {
            if (a == null || b == null || a.Count < 3 || b.Count < 3) return false;
            Poly.Bounds(a, out var amin, out var amax);
            Poly.Bounds(b, out var bmin, out var bmax);
            if (math.any(amax < bmin) || math.any(bmax < amin)) return false;
            if (Poly.Contains(a, b[0]) || Poly.Contains(b, a[0])) return true;
            for (int i = 0; i < a.Count; i++)
            {
                float2 p = a[i], q = a[(i + 1) % a.Count];
                for (int j = 0; j < b.Count; j++)
                    if (Poly.SegmentsIntersect(p, q, b[j], b[(j + 1) % b.Count])) return true;
            }
            return false;
        }

        /// <summary>Obstacles grown by <paramref name="margin"/> metres (miter joins; unusable ones skipped).</summary>
        public static List<float2[]> Grow(IEnumerable<IReadOnlyList<float2>> obstacles, float margin)
        {
            var list = new List<float2[]>();
            if (obstacles == null) return list;
            foreach (var o in obstacles)
            {
                if (o == null || o.Count < 3) continue;
                bool finite = true;
                foreach (var p in o) if (!Poly.IsFinite(p)) { finite = false; break; }
                if (!finite) continue;
                var ring = new float2[o.Count];
                for (int i = 0; i < ring.Length; i++) ring[i] = o[i];
                if (Poly.Area(ring) < 0.01f) continue;
                ring = Poly.EnsureCcw(ring);
                if (margin > 0.001f)
                {
                    List<float2[]> grown;
                    try { grown = Poly.Offset(ring, -margin); }
                    catch (Exception) { grown = null; }
                    if (grown != null && grown.Count > 0) { list.AddRange(grown); continue; }
                }
                list.Add(ring);
            }
            return list;
        }

        /// <summary>
        /// The ring minus the obstacles as ONE simple CCW ring: the largest remaining piece, enclosed
        /// obstacles opened with a <see cref="Corridor"/>. Returns the input ring unchanged (same
        /// instance) when no obstacle touches it, null when nothing usable (≥ minArea m²) remains.
        /// </summary>
        public static float2[] Subtract(float2[] ring, IReadOnlyList<float2[]> obstacles, out int touched, float minArea = 1f)
        {
            touched = 0;
            if (ring == null || ring.Length < 3 || obstacles == null || obstacles.Count == 0) return ring;
            var hits = Touching(ring, obstacles);
            touched = hits.Count;
            if (hits.Count == 0) return ring;
            List<Region> pieces;
            try { pieces = Poly.Boolean(new IReadOnlyList<float2>[] { ring }, hits, BoolOp.Difference); }
            catch (Exception) { return null; }
            pieces.RemoveAll(p => p == null || p.Outer == null || p.Outer.Length < 3);
            if (pieces.Count == 0) return null;
            pieces.Sort((a, b) => b.Area.CompareTo(a.Area));
            var kept = pieces[0];
            float2[] outer = kept.Outer;
            if (kept.HasHoles)
            {
                List<float2[]> cut;
                try { cut = Poly.CutHoles(kept, Corridor); }
                catch (Exception) { cut = new List<float2[]>(); }
                outer = null;
                float best = -1f;
                foreach (var c in cut) { float a = Poly.Area(c); if (a > best) { best = a; outer = c; } }
                if (outer == null) return null;
            }
            outer = Poly.Clean(outer, SurfaceAcceptance.MinEdge + 0.05f);
            if (outer.Length < 3) return null;
            outer = Poly.EnsureCcw(Poly.RemoveCollinear(outer, 1e-4f));
            if (outer.Length < 3 || !Poly.IsSimple(outer) || Poly.Area(outer) < minArea) return null;
            return outer;
        }

        /// <summary>
        /// The ring minus the obstacles as every usable piece (regions may keep holes; callers that need
        /// hole-free rings run them through SurfaceAcceptance.MakeAcceptable2D). Null = untouched.
        /// </summary>
        public static List<Region> Pieces(IReadOnlyList<float2> ring, IReadOnlyList<float2[]> obstacles, out int touched)
        {
            touched = 0;
            if (ring == null || ring.Count < 3 || obstacles == null || obstacles.Count == 0) return null;
            var hits = Touching(ring, obstacles);
            touched = hits.Count;
            if (hits.Count == 0) return null;
            try
            {
                var pieces = Poly.Boolean(new[] { ring }, hits, BoolOp.Difference);
                pieces.RemoveAll(p => p == null || p.Outer == null || p.Outer.Length < 3);
                return pieces;
            }
            catch (Exception) { return new List<Region>(); }
        }

        private static List<float2[]> Touching(IReadOnlyList<float2> ring, IReadOnlyList<float2[]> obstacles)
        {
            var hits = new List<float2[]>();
            foreach (var o in obstacles)
                if (o != null && o.Length >= 3 && Overlaps(ring, o)) hits.Add(o);
            return hits;
        }
    }
}
