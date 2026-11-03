// Placemaker roads layer — cubic Bézier helpers in the XZ plane.
// FitCurve reproduces the curve CS2 builds for a course from two end tangents
// (Game.Net.NetUtils.FitCurve), as measured by Grid Road Generator (Ti4goc), so the
// planned curve is the curve the game will actually lay.

using System;
using System.Collections.Generic;
using Unity.Mathematics;

namespace Placemaker.Core.Layers.Roads
{
    /// <summary>Cubic Bézier A→D with control points B, C. Y is carried along but all queries are planar (XZ).</summary>
    public struct RoadCurve
    {
        public float3 A, B, C, D;

        public RoadCurve(float3 a, float3 b, float3 c, float3 d) { A = a; B = b; C = c; D = d; }

        public static RoadCurve Straight(float3 a, float3 d) =>
            new RoadCurve(a, math.lerp(a, d, 1f / 3f), math.lerp(a, d, 2f / 3f), d);

        /// <summary>
        /// The curve CS2 lays between two points given unit start/end tangents (both pointing along
        /// the direction of travel). Port of the game's own fitting, so the plan matches the build.
        /// </summary>
        public static RoadCurve Fit(float3 start, float3 startTangent, float3 endTangent, float3 end)
        {
            float2 a = start.xz, d = end.xz;
            float2 st = math.normalizesafe(startTangent.xz), et = math.normalizesafe(endTangent.xz);
            if (math.lengthsq(st) < 1e-8f || math.lengthsq(et) < 1e-8f) return Straight(start, end);
            float num = math.distance(a, d);
            if (num < 1e-4f) return Straight(start, end);
            // Intersection of a + x*st and d - y*et.
            float2 r = st, q = -et;
            float den = r.x * q.y - r.y * q.x;
            float2 tt;
            if (math.abs(den) < 1e-6f) tt = new float2(num * 0.75f);
            else
            {
                float2 w = d - a;
                float x = (w.x * q.y - w.y * q.x) / den;
                float y = (r.x * w.y - r.y * w.x) / den;
                tt = math.clamp(new float2(x, y), num * 0.01f, num);
            }
            float num2 = math.dot(st, et);
            if (num2 > 0f) tt = math.lerp(tt, new float2(num / math.sqrt(2f * num2 + 2f)), math.min(1f, num2 * num2));
            else if (num2 < 0f) tt = math.lerp(tt, new float2(num * 1.2071068f), math.min(1f, num2 * num2));
            float num3 = math.acos(math.saturate(-math.dot(st, -et)));
            float num4 = math.tan(num3 / 2f);
            float num5 = (tt.x + tt.y) / 6f;
            num5 = num4 >= 0.0001f ? num5 * (4f * math.tan(num3 / 4f) / num4) : num5 * 2f;
            float2 b = a + st * math.min(tt.x, num5);
            float2 c = d - et * math.min(tt.y, num5);
            return new RoadCurve(start,
                new float3(b.x, math.lerp(start.y, end.y, 1f / 3f), b.y),
                new float3(c.x, math.lerp(start.y, end.y, 2f / 3f), c.y),
                end);
        }

        public float3 Position(float t)
        {
            float u = 1f - t;
            return u * u * u * A + 3f * u * u * t * B + 3f * u * t * t * C + t * t * t * D;
        }

        /// <summary>Derivative (not normalised).</summary>
        public float3 Derivative(float t)
        {
            float u = 1f - t;
            return 3f * u * u * (B - A) + 6f * u * t * (C - B) + 3f * t * t * (D - C);
        }

        public float2 TangentXZ(float t)
        {
            float2 d = Derivative(t).xz;
            if (math.lengthsq(d) < 1e-10f) d = (D - A).xz;
            return math.normalizesafe(d);
        }

        public RoadCurve Reversed() => new RoadCurve(D, C, B, A);

        /// <summary>Exact de Casteljau split at t.</summary>
        public void Split(float t, out RoadCurve left, out RoadCurve right)
        {
            float3 ab = math.lerp(A, B, t), bc = math.lerp(B, C, t), cd = math.lerp(C, D, t);
            float3 abc = math.lerp(ab, bc, t), bcd = math.lerp(bc, cd, t);
            float3 m = math.lerp(abc, bcd, t);
            left = new RoadCurve(A, ab, abc, m);
            right = new RoadCurve(m, bcd, cd, D);
        }

        /// <summary>Sub-curve between t0 and t1 (t0 &lt; t1).</summary>
        public RoadCurve Sub(float t0, float t1)
        {
            if (t0 <= 0f && t1 >= 1f) return this;
            Split(t1, out RoadCurve left, out _);
            if (t0 <= 0f) return left;
            left.Split(t0 / math.max(t1, 1e-6f), out _, out RoadCurve mid);
            return mid;
        }

        public float LengthXZ(int samples = 16)
        {
            float len = 0f;
            float2 prev = A.xz;
            for (int i = 1; i <= samples; i++)
            {
                float2 p = Position((float)i / samples).xz;
                len += math.distance(prev, p);
                prev = p;
            }
            return len;
        }

        public bool IsStraight(float tolerance = 0.05f)
        {
            float2 a = A.xz, d = D.xz;
            float2 dir = math.normalizesafe(d - a);
            if (math.lengthsq(dir) < 1e-8f) return true;
            float2 n = new float2(-dir.y, dir.x);
            return math.abs(math.dot(B.xz - a, n)) < tolerance && math.abs(math.dot(C.xz - a, n)) < tolerance;
        }

        /// <summary>Closest point (XZ) on the curve. Coarse sampling then golden-section refinement.</summary>
        public float DistanceXZ(float2 p, out float t)
        {
            const int samples = 24;
            float best = float.MaxValue;
            int bestI = 0;
            for (int i = 0; i <= samples; i++)
            {
                float d = math.distancesq(Position((float)i / samples).xz, p);
                if (d < best) { best = d; bestI = i; }
            }
            float lo = math.max(0f, (bestI - 1f) / samples), hi = math.min(1f, (bestI + 1f) / samples);
            for (int k = 0; k < 30; k++)
            {
                float m1 = lo + (hi - lo) * 0.382f, m2 = lo + (hi - lo) * 0.618f;
                if (math.distancesq(Position(m1).xz, p) < math.distancesq(Position(m2).xz, p)) hi = m2; else lo = m1;
            }
            t = 0.5f * (lo + hi);
            float dEnd = math.distancesq(p, Position(t).xz);
            if (math.distancesq(p, A.xz) <= dEnd) { t = 0f; dEnd = math.distancesq(p, A.xz); }
            if (math.distancesq(p, D.xz) < dEnd) { t = 1f; dEnd = math.distancesq(p, D.xz); }
            return math.sqrt(dEnd);
        }

        /// <summary>
        /// Polyline approximation: chords no longer than maxChord and turning at most maxTurnDeg.
        /// Returns points with their curve parameters (first t = 0, last t = 1).
        /// </summary>
        public void Flatten(List<float3> points, List<float> ts, float maxChord = 4f, float maxTurnDeg = 6f)
        {
            float len = LengthXZ(8);
            int byLength = (int)math.ceil(len / math.max(0.5f, maxChord));
            float turn = math.degrees(math.acos(math.clamp(math.dot(TangentXZ(0f), TangentXZ(1f)), -1f, 1f)));
            // Control-polygon turning bounds the curve's total turning (it can exceed the end-to-end angle).
            float2 ab = (B - A).xz, bc = (C - B).xz, cd = (D - C).xz;
            float polyTurn = AngleDeg(ab, bc) + AngleDeg(bc, cd);
            int byTurn = (int)math.ceil(math.max(turn, polyTurn) / math.max(1f, maxTurnDeg));
            int n = math.clamp(math.max(byLength, byTurn), 1, 512);
            for (int i = 0; i <= n; i++)
            {
                float t = (float)i / n;
                points.Add(i == 0 ? A : i == n ? D : Position(t));
                ts?.Add(t);
            }
        }

        private static float AngleDeg(float2 a, float2 b)
        {
            if (math.lengthsq(a) < 1e-8f || math.lengthsq(b) < 1e-8f) return 0f;
            return math.degrees(math.acos(math.clamp(math.dot(math.normalize(a), math.normalize(b)), -1f, 1f)));
        }

        /// <summary>Translates the end points (and their neighbouring control points) so the shape is preserved.</summary>
        public RoadCurve WithEnds(float3 a, float3 d)
        {
            if (IsStraight()) return Straight(a, d);
            return new RoadCurve(a, B + (a - A), C + (d - D), d);
        }
    }

    /// <summary>Planar segment helpers shared by the roads layer.</summary>
    internal static class Seg
    {
        /// <summary>Proper or touching intersection of p1→p2 and p3→p4; ta/tb along each.</summary>
        public static bool Intersect(float2 p1, float2 p2, float2 p3, float2 p4, out float ta, out float tb)
        {
            ta = tb = 0f;
            float2 r = p2 - p1, s = p4 - p3;
            float den = r.x * s.y - r.y * s.x;
            if (math.abs(den) < 1e-9f) return false;
            float2 w = p3 - p1;
            ta = (w.x * s.y - w.y * s.x) / den;
            tb = (w.x * r.y - w.y * r.x) / den;
            const float e = 1e-6f;
            return ta >= -e && ta <= 1f + e && tb >= -e && tb <= 1f + e;
        }

        public static float DistSq(float2 p, float2 a, float2 b, out float t)
        {
            float2 ab = b - a;
            float len = math.lengthsq(ab);
            t = len > 1e-12f ? math.saturate(math.dot(p - a, ab) / len) : 0f;
            return math.distancesq(p, a + ab * t);
        }

        public static float Dist(float2 p, float2 a, float2 b) => math.sqrt(DistSq(p, a, b, out _));
    }

    /// <summary>Uniform-grid index of planar segments for proximity and crossing queries.</summary>
    internal sealed class SegmentIndex
    {
        private readonly float _cell;
        private readonly Dictionary<long, List<int>> _cells = new Dictionary<long, List<int>>();
        public readonly List<float2> A = new List<float2>();
        public readonly List<float2> B = new List<float2>();
        public readonly List<int> Owner = new List<int>();

        public SegmentIndex(float cell) { _cell = math.max(1f, cell); }

        private static long Key(int x, int y) => ((long)x << 32) ^ (uint)y;

        public int Add(float2 a, float2 b, int owner)
        {
            int id = A.Count;
            A.Add(a); B.Add(b); Owner.Add(owner);
            float2 mn = math.min(a, b), mx = math.max(a, b);
            int x0 = (int)math.floor(mn.x / _cell), x1 = (int)math.floor(mx.x / _cell);
            int y0 = (int)math.floor(mn.y / _cell), y1 = (int)math.floor(mx.y / _cell);
            for (int x = x0; x <= x1; x++)
                for (int y = y0; y <= y1; y++)
                {
                    long k = Key(x, y);
                    if (!_cells.TryGetValue(k, out var list)) _cells[k] = list = new List<int>();
                    list.Add(id);
                }
            return id;
        }

        /// <summary>Distinct segment ids whose cells overlap the box [mn, mx] (ascending, deterministic).</summary>
        public void Query(float2 mn, float2 mx, List<int> result, HashSet<int> seen)
        {
            result.Clear(); seen.Clear();
            int x0 = (int)math.floor(mn.x / _cell), x1 = (int)math.floor(mx.x / _cell);
            int y0 = (int)math.floor(mn.y / _cell), y1 = (int)math.floor(mx.y / _cell);
            if ((long)(x1 - x0 + 1) * (y1 - y0 + 1) > 4096)
            {
                for (int i = 0; i < A.Count; i++) result.Add(i);
                return;
            }
            for (int x = x0; x <= x1; x++)
                for (int y = y0; y <= y1; y++)
                    if (_cells.TryGetValue(Key(x, y), out var list))
                        foreach (int id in list) if (seen.Add(id)) result.Add(id);
            result.Sort();
        }

        /// <summary>Nearest segment to p within maxDist; returns -1 when none.</summary>
        public int Nearest(float2 p, float maxDist, out float dist, out float t, List<int> scratch, HashSet<int> seen)
        {
            Query(p - maxDist, p + maxDist, scratch, seen);
            int best = -1; dist = float.MaxValue; t = 0f;
            foreach (int id in scratch)
            {
                float d = Seg.DistSq(p, A[id], B[id], out float tt);
                if (d < dist) { dist = d; best = id; t = tt; }
            }
            dist = math.sqrt(dist);
            if (dist > maxDist) best = -1;
            return best;
        }
    }

    internal static class MathEx
    {
        public static bool Finite(float3 p) => math.all(math.isfinite(p));
        public static float Clamp(float v, float lo, float hi) => v < lo ? lo : v > hi ? hi : v;
        public static int Clamp(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;
        public static double Round(double v, int digits) => Math.Round(v, digits, MidpointRounding.AwayFromZero);
    }
}
