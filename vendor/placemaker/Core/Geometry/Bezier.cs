// Placemaker — cubic Bézier helpers (CS2 net courses are cubic Béziers in 3D).
//
// FitCubics follows P. J. Schneider, "An Algorithm for Automatically Fitting Digitized Curves"
// (Graphics Gems, 1990): least-squares control points with fixed end tangents, Newton
// reparameterisation, split at the worst point. Own implementation.

using System;
using System.Collections.Generic;
using Unity.Mathematics;

namespace Placemaker.Core.Geometry
{
    /// <summary>Cubic Bézier in 3D (same layout as the game's Bezier4x3: A start, B/C handles, D end).</summary>
    public struct CubicBezier
    {
        public float3 A, B, C, D;

        public CubicBezier(float3 a, float3 b, float3 c, float3 d) { A = a; B = b; C = c; D = d; }

        /// <summary>Straight segment as a cubic (handles at thirds, the game's convention for straight roads).</summary>
        public static CubicBezier Line(float3 a, float3 d) => new CubicBezier(a, math.lerp(a, d, 1f / 3f), math.lerp(a, d, 2f / 3f), d);

        /// <summary>
        /// Curve from a to d leaving a along startDir and arriving at d along endDir (XZ directions).
        /// Handle length = circular-arc approximation for the turn (4/3·tan(θ/4)·r), dist/3 when straight.
        /// </summary>
        public static CubicBezier FromTangents(float3 a, float3 startDir, float3 d, float3 endDir)
        {
            float2 t0 = math.normalizesafe(startDir.xz), t1 = math.normalizesafe(endDir.xz);
            float chord = math.distance(a.xz, d.xz);
            float theta = math.acos(math.clamp(math.dot(t0, t1), -1f, 1f));
            float h;
            if (theta < 1e-3f || chord < 1e-4f) h = chord / 3f;
            else
            {
                float r = chord / (2f * math.max(math.sin(theta / 2f), 1e-4f));
                h = math.min(4f / 3f * math.tan(theta / 4f) * r, chord);
            }
            float3 b = a + new float3(t0.x, 0, t0.y) * h;
            float3 c = d - new float3(t1.x, 0, t1.y) * h;
            b.y = math.lerp(a.y, d.y, 1f / 3f);
            c.y = math.lerp(a.y, d.y, 2f / 3f);
            return new CubicBezier(a, b, c, d);
        }

        public float3 Position(float t)
        {
            float u = 1 - t;
            return u * u * u * A + 3 * u * u * t * B + 3 * u * t * t * C + t * t * t * D;
        }

        /// <summary>First derivative (not normalised).</summary>
        public float3 Derivative(float t)
        {
            float u = 1 - t;
            return 3 * u * u * (B - A) + 6 * u * t * (C - B) + 3 * t * t * (D - C);
        }

        public float3 Tangent(float t)
        {
            var d = Derivative(t);
            if (math.lengthsq(d) < 1e-12f) d = D - A; // coincident handle: fall back to the chord
            return math.normalizesafe(d);
        }

        /// <summary>de Casteljau split at t.</summary>
        public void Split(float t, out CubicBezier left, out CubicBezier right)
        {
            float3 ab = math.lerp(A, B, t), bc = math.lerp(B, C, t), cd = math.lerp(C, D, t);
            float3 abc = math.lerp(ab, bc, t), bcd = math.lerp(bc, cd, t);
            float3 m = math.lerp(abc, bcd, t);
            left = new CubicBezier(A, ab, abc, m);
            right = new CubicBezier(m, bcd, cd, D);
        }

        /// <summary>Sub-curve between t0 and t1.</summary>
        public CubicBezier Cut(float t0, float t1)
        {
            if (t1 < t0) { var t = t0; t0 = t1; t1 = t; }
            t0 = math.saturate(t0); t1 = math.saturate(t1);
            if (t1 <= 0) return new CubicBezier(A, A, A, A);
            Split(t1, out var l, out _);
            l.Split(t0 / t1, out _, out var mid);
            return mid;
        }

        /// <summary>Arc length (adaptive subdivision until chord and control polygon agree to tolerance).</summary>
        public float Length(float tolerance = 1e-3f) => LengthRec(this, tolerance, 0);

        /// <summary>Arc length in the XZ plane only.</summary>
        public float LengthXZ(float tolerance = 1e-3f)
        {
            var flat = new CubicBezier(new float3(A.x, 0, A.z), new float3(B.x, 0, B.z), new float3(C.x, 0, C.z), new float3(D.x, 0, D.z));
            return flat.Length(tolerance);
        }

        private static float LengthRec(CubicBezier c, float tol, int depth)
        {
            float chord = math.distance(c.A, c.D);
            float poly = math.distance(c.A, c.B) + math.distance(c.B, c.C) + math.distance(c.C, c.D);
            if (poly - chord <= tol || depth >= 16) return (2 * chord + poly) / 3f;
            c.Split(0.5f, out var l, out var r);
            return LengthRec(l, tol * 0.5f, depth + 1) + LengthRec(r, tol * 0.5f, depth + 1);
        }

        /// <summary>count + 1 points at uniform parameter steps (ends included).</summary>
        public float3[] Sample(int count)
        {
            count = math.max(1, count);
            var r = new float3[count + 1];
            for (int i = 0; i <= count; i++) r[i] = Position((float)i / count);
            return r;
        }

        /// <summary>Parameter values at uniform arc-length spacing (ends included; last step may be shorter by &lt; spacing/2).</summary>
        public float[] ParametersBySpacing(float spacing)
        {
            const int Table = 64;
            var cum = new float[Table + 1];
            float3 prev = A;
            for (int i = 1; i <= Table; i++)
            {
                float3 p = Position((float)i / Table);
                cum[i] = cum[i - 1] + math.distance(prev, p);
                prev = p;
            }
            float total = cum[Table];
            int count = math.max(1, (int)math.round(total / math.max(spacing, 1e-3f)));
            var ts = new float[count + 1];
            int seg = 0;
            for (int k = 0; k <= count; k++)
            {
                float s = total * k / count;
                while (seg < Table - 1 && cum[seg + 1] < s) seg++;
                float span = cum[seg + 1] - cum[seg];
                float f = span > 0 ? math.saturate((s - cum[seg]) / span) : 0;
                ts[k] = (seg + f) / Table;
            }
            ts[0] = 0; ts[count] = 1;
            return ts;
        }

        public float3[] SampleBySpacing(float spacing)
        {
            var ts = ParametersBySpacing(spacing);
            var r = new float3[ts.Length];
            for (int i = 0; i < ts.Length; i++) r[i] = Position(ts[i]);
            return r;
        }

        /// <summary>Parameter at arc length s from A (clamped).</summary>
        public float ParameterAtDistance(float s)
        {
            float total = Length();
            if (s <= 0) return 0;
            if (s >= total) return 1;
            float lo = 0, hi = 1;
            for (int it = 0; it < 30; it++)
            {
                float m = 0.5f * (lo + hi);
                Split(m, out var l, out _);
                if (l.Length() < s) lo = m; else hi = m;
            }
            return 0.5f * (lo + hi);
        }

        /// <summary>Parameter of the point closest to p in the XZ plane (coarse scan + refinement).</summary>
        public float ClosestParameterXZ(float2 p)
        {
            const int N = 32;
            float best = float.MaxValue, bt = 0;
            for (int i = 0; i <= N; i++)
            {
                float t = (float)i / N;
                float d = math.distancesq(Position(t).xz, p);
                if (d < best) { best = d; bt = t; }
            }
            float step = 1f / N;
            for (int it = 0; it < 20; it++)
            {
                step *= 0.5f;
                foreach (float t in new[] { bt - step, bt + step })
                {
                    if (t < 0 || t > 1) continue;
                    float d = math.distancesq(Position(t).xz, p);
                    if (d < best) { best = d; bt = t; }
                }
            }
            return bt;
        }

        public CubicBezier Reversed() => new CubicBezier(D, C, B, A);

        public override string ToString() => $"Bezier({A} → {D})";
    }

    public static class BezierFit
    {
        /// <summary>
        /// Fits a chain of cubics to an open polyline so no input point is farther than maxError (m)
        /// from the curve. Consecutive cubics are G1-continuous at split points.
        /// </summary>
        public static List<CubicBezier> FitCubics(IReadOnlyList<float3> points, float maxError)
        {
            var result = new List<CubicBezier>();
            var pts = new List<float3>();
            if (points != null)
                foreach (var p in points)
                    if (pts.Count == 0 || math.distancesq(pts[pts.Count - 1], p) > 1e-10f) pts.Add(p);
            if (pts.Count < 2) return result;
            if (pts.Count == 2) { result.Add(CubicBezier.Line(pts[0], pts[1])); return result; }
            float3 t1 = EndTangent(pts, true);
            float3 t2 = EndTangent(pts, false);
            FitRange(pts, 0, pts.Count - 1, t1, t2, math.max(maxError, 1e-4f), result, 0);
            return result;
        }

        /// <summary>Single cubic best fitting the polyline with free end tangents taken from the ends.</summary>
        public static CubicBezier FitCubic(IReadOnlyList<float3> points)
        {
            var pts = new List<float3>(points);
            if (pts.Count < 2) return default;
            if (pts.Count == 2) return CubicBezier.Line(pts[0], pts[1]);
            float3 t1 = EndTangent(pts, true), t2 = EndTangent(pts, false);
            var u = ChordParams(pts, 0, pts.Count - 1);
            var bez = Generate(pts, 0, pts.Count - 1, u, t1, t2);
            for (int it = 0; it < 4; it++)
            {
                u = Reparameterize(pts, 0, pts.Count - 1, u, bez);
                bez = Generate(pts, 0, pts.Count - 1, u, t1, t2);
            }
            return bez;
        }

        /// <summary>
        /// Unit tangent at an end of the polyline (pointing into it), from the one-sided 3-point
        /// derivative of the parabola through the first/last three points (chord-length spaced). Far
        /// better than the first chord on sampled arcs; falls back to the chord.
        /// </summary>
        private static float3 EndTangent(List<float3> p, bool atStart)
        {
            int n = p.Count;
            float3 a = atStart ? p[0] : p[n - 1], b = atStart ? p[1] : p[n - 2];
            float3 chord = math.normalizesafe(b - a);
            if (n < 3) return chord;
            float3 c = atStart ? p[2] : p[n - 3];
            float h1 = math.distance(a, b), h2 = math.distance(b, c);
            if (h1 < 1e-6f || h2 < 1e-6f) return chord;
            float3 d = -(2 * h1 + h2) / (h1 * (h1 + h2)) * a + (h1 + h2) / (h1 * h2) * b - h1 / (h2 * (h1 + h2)) * c;
            float3 t = math.normalizesafe(d);
            return math.dot(t, chord) > 0.5f ? t : chord;
        }

        private static void FitRange(List<float3> d, int first, int last, float3 tHat1, float3 tHat2, float error, List<CubicBezier> output, int depth)
        {
            int nPts = last - first + 1;
            if (nPts == 2)
            {
                float dist = math.distance(d[first], d[last]) / 3f;
                output.Add(new CubicBezier(d[first], d[first] + tHat1 * dist, d[last] + tHat2 * dist, d[last]));
                return;
            }
            var u = ChordParams(d, first, last);
            var bez = Generate(d, first, last, u, tHat1, tHat2);
            float maxErr = MaxError(d, first, last, bez, u, out int split);
            if (maxErr < error) { output.Add(bez); return; }
            if (maxErr < error * 4f)
            {
                for (int it = 0; it < 6; it++)
                {
                    u = Reparameterize(d, first, last, u, bez);
                    bez = Generate(d, first, last, u, tHat1, tHat2);
                    maxErr = MaxError(d, first, last, bez, u, out split);
                    if (maxErr < error) { output.Add(bez); return; }
                }
            }
            if (depth > 24) { output.Add(bez); return; }
            split = math.clamp(split, first + 1, last - 1);
            float3 center = math.normalizesafe(d[split - 1] - d[split + 1]);
            if (math.lengthsq(center) < 1e-12f) center = math.normalizesafe(d[split - 1] - d[split]);
            FitRange(d, first, split, tHat1, center, error, output, depth + 1);
            FitRange(d, split, last, -center, tHat2, error, output, depth + 1);
        }

        private static float[] ChordParams(List<float3> d, int first, int last)
        {
            var u = new float[last - first + 1];
            for (int i = first + 1; i <= last; i++) u[i - first] = u[i - first - 1] + math.distance(d[i], d[i - 1]);
            float total = u[u.Length - 1];
            for (int i = 1; i < u.Length; i++) u[i] = total > 0 ? u[i] / total : (float)i / (u.Length - 1);
            return u;
        }

        private static CubicBezier Generate(List<float3> d, int first, int last, float[] u, float3 tHat1, float3 tHat2)
        {
            int n = last - first + 1;
            double c00 = 0, c01 = 0, c11 = 0, x0 = 0, x1 = 0;
            float3 p0 = d[first], p3 = d[last];
            for (int i = 0; i < n; i++)
            {
                float t = u[i], s = 1 - t;
                float b0 = s * s * s, b1 = 3 * t * s * s, b2 = 3 * t * t * s, b3 = t * t * t;
                float3 a0 = tHat1 * b1, a1 = tHat2 * b2;
                c00 += math.dot(a0, a0); c01 += math.dot(a0, a1); c11 += math.dot(a1, a1);
                float3 tmp = d[first + i] - (p0 * (b0 + b1) + p3 * (b2 + b3));
                x0 += math.dot(a0, tmp); x1 += math.dot(a1, tmp);
            }
            double det = c00 * c11 - c01 * c01;
            double alphaL = 0, alphaR = 0;
            if (math.abs(det) > 1e-12)
            {
                alphaL = (x0 * c11 - c01 * x1) / det;
                alphaR = (c00 * x1 - x0 * c01) / det;
            }
            float segLength = math.distance(p0, p3);
            float eps = 1e-6f * segLength;
            if (alphaL < eps || alphaR < eps || double.IsNaN(alphaL) || double.IsNaN(alphaR))
            {
                float dist = segLength / 3f;
                return new CubicBezier(p0, p0 + tHat1 * dist, p3 + tHat2 * dist, p3);
            }
            return new CubicBezier(p0, p0 + tHat1 * (float)alphaL, p3 + tHat2 * (float)alphaR, p3);
        }

        private static float MaxError(List<float3> d, int first, int last, CubicBezier bez, float[] u, out int split)
        {
            split = (last - first + 1) / 2 + first;
            float max = 0;
            for (int i = first + 1; i < last; i++)
            {
                float dist = math.distancesq(bez.Position(u[i - first]), d[i]);
                if (dist >= max) { max = dist; split = i; }
            }
            return math.sqrt(max);
        }

        private static float[] Reparameterize(List<float3> d, int first, int last, float[] u, CubicBezier bez)
        {
            var r = new float[u.Length];
            for (int i = first; i <= last; i++)
            {
                float t = u[i - first];
                float3 q = bez.Position(t) - d[i];
                float3 q1 = bez.Derivative(t);
                float u2 = 1 - t;
                float3 q2 = 6 * u2 * (bez.C - 2 * bez.B + bez.A) + 6 * t * (bez.D - 2 * bez.C + bez.B);
                float num = math.dot(q, q1), den = math.dot(q1, q1) + math.dot(q, q2);
                r[i - first] = math.abs(den) > 1e-12f ? math.saturate(t - num / den) : t;
            }
            r[0] = 0; r[r.Length - 1] = 1;
            return r;
        }
    }
}
