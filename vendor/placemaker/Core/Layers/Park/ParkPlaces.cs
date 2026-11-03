// Places inside a park (DESIGN.md §9): the park carves a described place (plaza) out of its own
// area before planning paths, then makes its paths END at the place's edge instead of crossing
// it, and adds one link path when no path reaches the place. Planting and furniture keep out of
// it through Occupancy (the place is a reserved polygon for the park's own planting).

using System;
using System.Collections.Generic;
using Placemaker.Core.Geometry;
using Placemaker.Core.Layers.Common;
using Unity.Mathematics;

namespace Placemaker.Core.Layers.Park
{
    public static class ParkPlaces
    {
        /// <summary>A path end counts as joined to a place when it lies this close to its outline.</summary>
        public const float JoinTolerance = 0.75f;
        private const int ClipSamples = 40;
        private const float MinPiece = 2f;

        /// <summary>
        /// Cuts every curve where it enters the place: the parts outside are kept and end exactly on
        /// the place outline, the parts inside are dropped. Then, if no path touches the place, links
        /// the nearest path end to it with a short straight path.
        /// </summary>
        public static List<PathCurve> ClipAndConnect(List<PathCurve> curves, float2[] place, LayerRegion region, out int joins)
        {
            var result = new List<PathCurve>(curves.Count + 2);
            joins = 0;
            if (place == null || place.Length < 3) { result.AddRange(curves); return result; }
            Poly.Bounds(place, out var mn, out var mx);
            foreach (var c in curves)
            {
                Poly.Bounds(c.Samples, out var cmn, out var cmx);
                if (cmx.x < mn.x - 1f || cmx.y < mn.y - 1f || cmn.x > mx.x + 1f || cmn.y > mx.y + 1f) { result.Add(c); continue; }
                var ts = new float[ClipSamples + 1];
                var inside = new bool[ClipSamples + 1];
                bool any = false, all = true;
                for (int i = 0; i <= ClipSamples; i++)
                {
                    ts[i] = i / (float)ClipSamples;
                    inside[i] = Poly.Contains(place, c.Point(ts[i]));
                    any |= inside[i]; all &= inside[i];
                }
                if (!any) { result.Add(c); continue; }
                if (all) continue;
                // Outside intervals with their crossing parameters refined by bisection.
                float start = inside[0] ? -1f : 0f;
                for (int i = 1; i <= ClipSamples; i++)
                {
                    if (inside[i] == inside[i - 1]) continue;
                    float t = Crossing(c, place, ts[i - 1], ts[i], inside[i - 1]);
                    if (!inside[i - 1])
                    {
                        Add(result, c, start, t, ref joins);   // leaving the outside: interval ends at the place
                        start = -1f;
                    }
                    else start = t;                            // re-emerging from the place
                }
                if (start >= 0f) Add(result, c, start, 1f, ref joins);
            }
            if (joins == 0 && result.Count > 0)
            {
                var link = Link(result, place, region);
                if (link != null) { result.Add(link); joins++; }
            }
            return result;
        }

        private static void Add(List<PathCurve> into, PathCurve c, float t0, float t1, ref int joins)
        {
            if (t0 < 0f || t1 - t0 < 1e-4f) return;
            var piece = Sub(c, t0, t1);
            if (piece.Length < MinPiece) return;
            into.Add(piece);
            if (t0 > 0f || t1 < 1f) joins++;
        }

        private static float Crossing(PathCurve c, float2[] place, float a, float b, bool insideAtA)
        {
            for (int k = 0; k < 18; k++)
            {
                float m = (a + b) * 0.5f;
                if (Poly.Contains(place, c.Point(m)) == insideAtA) a = m; else b = m;
            }
            return (a + b) * 0.5f;
        }

        /// <summary>The part of a cubic Bézier between t0 and t1 (de Casteljau), gate ends kept only when untouched.</summary>
        public static PathCurve Sub(PathCurve c, float t0, float t1)
        {
            // Split at t1 → left part [0, t1]; then split that at t0 / t1 → right part.
            Split(c.A, c.B, c.C, c.D, t1, out var l0, out var l1, out var l2, out var l3);
            float u = t1 > 1e-6f ? t0 / t1 : 0f;
            Split(l0, l1, l2, l3, u, out _, out _, out _, out _, out var r0, out var r1, out var r2, out var r3);
            var s = new PathCurve
            {
                A = r0, B = r1, C = r2, D = r3, Role = c.Role, HalfWidth = c.HalfWidth, Kind = c.Kind, Tier = c.Tier,
                GateA = t0 <= 0f ? c.GateA : -1, GateD = t1 >= 1f ? c.GateD : -1,
            };
            ParkPaths.Sample(s);
            return s;
        }

        private static void Split(float2 a, float2 b, float2 c, float2 d, float t, out float2 l0, out float2 l1, out float2 l2, out float2 l3) =>
            Split(a, b, c, d, t, out l0, out l1, out l2, out l3, out _, out _, out _, out _);

        private static void Split(float2 a, float2 b, float2 c, float2 d, float t,
            out float2 l0, out float2 l1, out float2 l2, out float2 l3, out float2 r0, out float2 r1, out float2 r2, out float2 r3)
        {
            var ab = math.lerp(a, b, t); var bc = math.lerp(b, c, t); var cd = math.lerp(c, d, t);
            var abc = math.lerp(ab, bc, t); var bcd = math.lerp(bc, cd, t);
            var p = math.lerp(abc, bcd, t);
            l0 = a; l1 = ab; l2 = abc; l3 = p;
            r0 = p; r1 = bcd; r2 = cd; r3 = d;
        }

        /// <summary>Straight link from the path end nearest the place to the closest point of its outline.</summary>
        private static PathCurve Link(List<PathCurve> curves, float2[] place, LayerRegion region)
        {
            float best = float.MaxValue; float2 from = default, to = default; PathCurve owner = null;
            foreach (var c in curves)
                foreach (var end in new[] { c.A, c.D })
                {
                    var q = Closest(place, end);
                    float d = math.distance(end, q);
                    if (d >= best) continue;
                    if (region.CrossesBoundary(end, q, true)) continue;
                    best = d; from = end; to = q; owner = c;
                }
            if (owner == null || best < 0.05f) return null;
            var role = owner.Role;
            var link = new PathCurve
            {
                A = from, B = math.lerp(from, to, 1f / 3f), C = math.lerp(from, to, 2f / 3f), D = to,
                Role = role, HalfWidth = Composer.DefaultHalfWidth(role), Kind = PathEdgeKind.Secondary,
            };
            ParkPaths.Sample(link);
            return link;
        }

        public static float2 Closest(float2[] ring, float2 p)
        {
            float best = float.MaxValue; float2 r = p;
            for (int i = 0; i < ring.Length; i++)
            {
                var a = ring[i]; var b = ring[(i + 1) % ring.Length];
                var ab = b - a;
                float len = math.lengthsq(ab);
                float t = len > 1e-9f ? math.saturate(math.dot(p - a, ab) / len) : 0f;
                var q = a + ab * t;
                float d = math.distancesq(p, q);
                if (d < best) { best = d; r = q; }
            }
            return r;
        }

        /// <summary>Distance from a point to a ring's outline.</summary>
        public static float OutlineDistance(float2[] ring, float2 p) => math.distance(p, Closest(ring, p));

        /// <summary>Number of curve ends lying on the place outline (paths that join it).</summary>
        public static int CountJoins(IEnumerable<PathCurve> curves, float2[] place)
        {
            int n = 0;
            foreach (var c in curves)
            {
                if (OutlineDistance(place, c.A) <= JoinTolerance) n++;
                if (OutlineDistance(place, c.D) <= JoinTolerance) n++;
            }
            return n;
        }
    }
}
