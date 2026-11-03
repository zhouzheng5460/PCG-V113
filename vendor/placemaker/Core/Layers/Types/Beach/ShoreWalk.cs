// Shore walk (s30): ONE smooth curve near the water at ONE smooth height, and the ground as one continuous field around it.
//
// Playtest (screenshots 54–56): the waterfront promenade "played to the existing surface too much". Its line was the
// waterline offset (every wiggle of the real shore became a zig-zag of the paving edge), its level was re-fitted every
// few metres (a promenade dipping up and down), and the edge (quay segments, railings) was cut into pieces by every link,
// pier and cliff probe — short walls at different heights with gaps, overlapping ends and vertical terrain "curtains"
// where ramps, pads and level patches met the slope at different heights. CS2 renders any height jump between two
// heightmap texels (~3.5 m apart) as a stretched vertical sheet, and a net flattens the ground across its width, so a
// path on a cross slope or a deck at a different level from the ground beside it makes the same sheet.
//
// The rule now: path near the water in a smooth line and let it work itself out.
//   curve    the waterline offset, heavily smoothed by arc length (Gaussian, σ ≈ 18–30 m), pushed inland wherever it
//            would come closer to the water than its margin (the push itself smoothed), trimmed to the land; loop-free
//   height   the ground under the curve, smoothed over ±40 m, clamped to a band, grade ≤ 2 % along it
//   field    every target height is a function of (signed distance from the curve, arc length, original ground):
//            flat across the walk and its shoulders, one smooth bank to the water, one smooth bank (or a deliberate
//            retaining wall) to the ground behind — no pads, ramps or level patches cut into it
// Pure and deterministic.

using System;
using System.Collections.Generic;
using Placemaker.Core.Geometry;
using Unity.Mathematics;

namespace Placemaker.Core.Layers.Types.Beach
{
    public sealed class ShoreWalk
    {
        /// <summary>Resampled curve (~2 m), land on the LEFT (left normal points inland).</summary>
        public float2[] Line;
        public float[] S;
        /// <summary>Planned walk height per sample.</summary>
        public float[] H;
        public float Length => S == null || S.Length == 0 ? 0f : S[S.Length - 1];

        public const float Spacing = 2f;

        private ShoreWalk(float2[] line)
        {
            Line = line;
            S = Arc(line);
            H = new float[line.Length];
        }

        public static ShoreWalk Of(float2[] line) => line == null || line.Length < 2 ? null : new ShoreWalk(line);

        public static float[] Arc(float2[] line)
        {
            var s = new float[line.Length];
            for (int i = 1; i < line.Length; i++) s[i] = s[i - 1] + math.distance(line[i - 1], line[i]);
            return s;
        }

        // ───────────── queries ─────────────

        private int Seg(float s, out float t)
        {
            int n = Line.Length;
            if (s <= 0f) { t = 0f; return 0; }
            if (s >= S[n - 1]) { t = 1f; return n - 2; }
            int lo = 0, hi = n - 1;
            while (hi - lo > 1) { int m = (lo + hi) >> 1; if (S[m] <= s) lo = m; else hi = m; }
            t = (s - S[lo]) / math.max(1e-5f, S[lo + 1] - S[lo]);
            return lo;
        }

        public float HeightAt(float s)
        {
            int k = Seg(s, out float t);
            return math.lerp(H[k], H[k + 1], t);
        }

        /// <summary>Point at arc length s, unit tangent and inland normal (left of the tangent).</summary>
        public float2 At(float s, out float2 tangent, out float2 inland)
        {
            int k = Seg(s, out float t);
            // Tangent blended between neighbouring segments so offsets of the curve stay smooth at the samples.
            var t0 = Tan(k); var t1 = Tan(math.min(k + 1, Line.Length - 2));
            tangent = math.normalizesafe(math.lerp(t0, t1, t), t0);
            inland = new float2(-tangent.y, tangent.x);
            return math.lerp(Line[k], Line[k + 1], t);
        }

        private float2 Tan(int k) => math.normalizesafe(Line[k + 1] - Line[k], new float2(1, 0));

        /// <summary>
        /// Signed distance from the curve (positive inland), the arc length of the closest point, and how far p lies
        /// beyond the curve's ends along the end tangent (0 alongside it).
        /// </summary>
        private float4[] _box;
        private int _last;

        public float Signed(float2 p, out float s, out float beyond)
        {
            // The curve extended by straight rays along its end tangents: the closest point moves continuously past the ends
            // (a closest point that jumped from the end to an interior bend made the field step there). Bounding-box pruning
            // with a warm start from the previous query (the field asks for every node of a 2 m grid).
            const float Ray = 400f;
            int n = Line.Length;
            if (_box == null)
            {
                _box = new float4[n + 1];
                for (int k = -1; k < n; k++) { Seg(k, out var a, out var b); _box[k + 1] = new float4(math.min(a, b), math.max(a, b)); }
            }
            float Dist(int k, out float t)
            {
                Seg(k, out var a, out var b);
                var ab = b - a; float l2 = math.lengthsq(ab);
                t = l2 > 1e-9f ? math.saturate(math.dot(p - a, ab) / l2) : 0f;
                return math.distancesq(p, a + ab * t);
            }
            int bk = math.clamp(_last, -1, n - 1);
            float best = Dist(bk, out float bt);
            for (int k = -1; k < n; k++)
            {
                var bx = _box[k + 1];
                float dx = math.max(0f, math.max(bx.x - p.x, p.x - bx.z)), dy = math.max(0f, math.max(bx.y - p.y, p.y - bx.w));
                if (dx * dx + dy * dy > best) continue;
                float d = Dist(k, out float t);
                if (d < best || d == best && k < bk) { best = d; bk = k; bt = t; }
            }
            _last = bk;
            float2 q, tan;
            beyond = 0f;
            if (bk == -1) { tan = Tan(0); q = Line[0] - tan * Ray * (1f - bt); beyond = Ray * (1f - bt); s = 0f; }
            else if (bk == n - 1) { tan = Tan(n - 2); q = Line[n - 1] + tan * Ray * bt; beyond = Ray * bt; s = S[n - 1]; }
            else { tan = Tan(bk); q = math.lerp(Line[bk], Line[bk + 1], bt); s = math.lerp(S[bk], S[bk + 1], bt); }
            var rel = p - q;
            float cross = tan.x * rel.y - tan.y * rel.x;
            return cross >= 0f ? math.sqrt(best) : -math.sqrt(best);

            void Seg(int k, out float2 a, out float2 b)
            {
                if (k == -1) { b = Line[0]; a = b - Tan(0) * Ray; }
                else if (k == n - 1) { a = Line[n - 1]; b = a + Tan(n - 2) * Ray; }
                else { a = Line[k]; b = Line[k + 1]; }
            }
        }

        /// <summary>Polyline parallel to the curve at signed offset d (positive inland), loop-free.</summary>
        public float2[] Offset(float d, float spacing = Spacing)
        {
            int n = math.max(2, (int)math.ceil(Length / spacing) + 1);
            var pts = new List<float2>(n);
            for (int i = 0; i < n; i++)
            {
                var p = At(Length * i / (n - 1), out _, out var nrm);
                pts.Add(p + nrm * d);
            }
            return Shoreline.RemoveLoops(pts).ToArray();
        }

        /// <summary>The band between signed offsets d0 &lt; d1 along [s0, s1] as one ring (CCW-agnostic).</summary>
        public float2[] Strip(float d0, float d1, float s0 = 0f, float s1 = -1f, float spacing = Spacing)
        {
            if (s1 < 0f) s1 = Length;
            int n = math.max(2, (int)math.ceil((s1 - s0) / spacing) + 1);
            var a = new List<float2>(n); var b = new List<float2>(n);
            for (int i = 0; i < n; i++)
            {
                var p = At(math.lerp(s0, s1, i / (float)(n - 1)), out _, out var nrm);
                a.Add(p + nrm * d0); b.Add(p + nrm * d1);
            }
            var ra = Shoreline.RemoveLoops(a); var rb = Shoreline.RemoveLoops(b);
            var ring = new List<float2>(ra);
            for (int i = rb.Count - 1; i >= 0; i--) ring.Add(rb[i]);
            return ring.ToArray();
        }

        // ───────────── construction ─────────────

        /// <summary>
        /// Gaussian smoothing of a polyline by arc length (σ metres), a few passes; the ends are kept in place so a
        /// trimmed curve still reaches the border. Then resampled at <paramref name="spacing"/>.
        /// </summary>
        public static float2[] SmoothLine(IReadOnlyList<float2> pts, float sigma, int passes = 2, float spacing = Spacing)
        {
            if (pts == null || pts.Count < 3) return pts == null ? null : new List<float2>(pts).ToArray();
            var a = Poly.ResamplePolyline(new List<float2>(pts), spacing);
            if (a.Length < 3 || sigma <= 0f) return a;
            int reach = (int)math.ceil(sigma * 2.5f / spacing);
            var w = new float[reach + 1];
            for (int k = 0; k <= reach; k++) { float x = k * spacing / sigma; w[k] = math.exp(-0.5f * x * x); }
            for (int pass = 0; pass < passes; pass++)
            {
                var b = new float2[a.Length];
                int n = a.Length;
                for (int i = 0; i < n; i++)
                {
                    // Near an end the window shrinks symmetrically (the end stays put, no drift along the curve).
                    int r = math.min(reach, math.min(i, n - 1 - i));
                    float2 sum = 0f; float ws = 0f;
                    for (int k = -r; k <= r; k++) { float wk = w[math.abs(k)]; sum += a[i + k] * wk; ws += wk; }
                    b[i] = sum / ws;
                }
                a = Poly.ResamplePolyline(Shoreline.RemoveLoops(b), spacing);
            }
            return a;
        }

        /// <summary>
        /// The walk curve: the real waterline offset <paramref name="dw"/> m inland over [s0, s1], smoothed (σ), then pushed
        /// inland wherever it comes closer to the water than <paramref name="minDw"/>(arc length) — the push dilated and
        /// smoothed along the curve so it never makes a bump — and cut to the pieces that stay on the land
        /// (<paramref name="keep"/>) and are at least <paramref name="minLength"/> long.
        /// </summary>
        public static List<float2[]> FromWater(Shoreline shore, float dw, float s0, float s1, float sigma, Func<float2, float> minDw,
            Func<float2, bool> keep, float minLength)
        {
            var result = new List<float2[]>();
            if (s1 - s0 < 4f) return result;
            // The real waterline, smoothed FIRST (its wiggles and the coarse water snapshot's steps gone), then offset along its
            // own smooth normal: an offset of a jagged line folds into loops and cusps at every inside bend, and smoothing
            // afterwards cannot round a cusp the size of the offset.
            var water = new List<float2>();
            int n = math.max(2, (int)math.ceil((s1 - s0) / Spacing));
            float2 inwMid = new float2(0, 1);
            for (int i = 0; i <= n; i++)
            {
                var wp = shore.WaterAt(math.lerp(s0, s1, i / (float)n), out var inw);
                water.Add(wp);
                if (i == n / 2) inwMid = inw;
            }
            var ws = SmoothLine(Shoreline.RemoveLoops(water), sigma, 3);
            if (ws == null || ws.Length < 2) return result;
            float sideSign = 1f;
            {
                int m = ws.Length / 2;
                var t = math.normalizesafe(ws[math.min(m + 1, ws.Length - 1)] - ws[math.max(0, m - 1)], new float2(1, 0));
                if (math.dot(new float2(-t.y, t.x), inwMid) < 0f) sideSign = -1f;
            }
            var raw = new List<float2>(ws.Length);
            for (int i = 0; i < ws.Length; i++)
            {
                var t = math.normalizesafe(ws[math.min(i + 1, ws.Length - 1)] - ws[math.max(0, i - 1)], new float2(1, 0));
                raw.Add(ws[i] + new float2(-t.y, t.x) * (dw * sideSign));
            }
            var line = SmoothLine(Shoreline.RemoveLoops(raw), sigma * 0.5f, 2);
            if (minDw != null)
                for (int iter = 0; iter < 3; iter++)
                {
                    var need = new float[line.Length];
                    bool any = false;
                    for (int i = 0; i < line.Length; i++) { need[i] = math.max(0f, minDw(line[i]) - shore.FromWater(line[i])); any |= need[i] > 0.05f; }
                    if (!any) break;
                    // Dilate (a smooth bump under every need, wide enough not to read as a kink), then move along the normal.
                    var push = Dilate(need, Spacing, math.max(12f, sigma * 1.2f));
                    var moved = new float2[line.Length];
                    for (int i = 0; i < line.Length; i++)
                    {
                        var t = math.normalizesafe(line[math.min(i + 1, line.Length - 1)] - line[math.max(0, i - 1)], new float2(1, 0));
                        moved[i] = line[i] + new float2(-t.y, t.x) * (push[i] * 1.05f + (push[i] > 0f ? 0.2f : 0f));
                    }
                    line = SmoothLine(Shoreline.RemoveLoops(moved), sigma * 0.35f, 1);
                }
            // Pieces on the land.
            var run = new List<float2>();
            void Flush()
            {
                if (run.Count >= 2)
                {
                    var piece = run.ToArray();
                    if (Shoreline.LengthOf(piece) >= minLength) result.Add(Poly.ResamplePolyline(new List<float2>(piece), Spacing));
                }
                run.Clear();
            }
            foreach (var p in line) { if (keep == null || keep(p)) run.Add(p); else Flush(); }
            Flush();
            return result;
        }


        /// <summary>Smallest radius a walk or an edge curve may have (m).</summary>
        public const float MinCurveRadius = 40f;

        /// <summary>
        /// <see cref="FromWater"/>, smoothed harder (σ × 1.4, at most three times) until every piece's radius is ≥
        /// <see cref="MinCurveRadius"/>: a promenade or a deck never follows the waterline's small wiggles.
        /// </summary>
        public static List<float2[]> SmoothFromWater(Shoreline shore, float dw, float2 run, float sigma, Func<float2, float> minDw, Func<float2, bool> keep, float minLength)
        {
            List<float2[]> lines = null;
            for (int k = 0; k < 4; k++)
            {
                lines = FromWater(shore, dw, run.x, run.y, sigma, minDw, keep, minLength);
                for (int i = 0; i < lines.Count; i++) lines[i] = TrimHooks(lines[i], MinCurveRadius);
                bool ok = true;
                foreach (var l in lines) if (MinRadius(l) < MinCurveRadius) ok = false;
                if (ok) break;
                sigma *= 1.4f;
            }
            return lines;
        }

        /// <summary>
        /// Drops the ends of a curve where it hooks (radius &lt; <paramref name="minRadius"/> within the last metres): the
        /// waterline turns round the outline's corners, and an end that follows it reads as a crook.
        /// </summary>
        public static float2[] TrimHooks(float2[] line, float minRadius, float half = 10f)
        {
            if (line == null || line.Length < 8) return line;
            var s = Arc(line);
            float total = s[s.Length - 1];
            int a = 0, b = line.Length - 1;
            bool Sharp(int i)
            {
                int lo = i, hi = i;
                while (lo > 0 && s[i] - s[lo] < half) lo--;
                while (hi < line.Length - 1 && s[hi] - s[i] < half) hi++;
                if (lo == i || hi == i) return false;
                return Circumradius(line[lo], line[i], line[hi]) < minRadius;
            }
            for (int i = 0; i < line.Length && s[i] < total * 0.3f; i++) if (Sharp(i)) a = i + 1;
            for (int i = line.Length - 1; i >= 0 && s[i] > total * 0.7f; i--) if (Sharp(i)) b = i - 1;
            if (b - a < 3) return line;
            var r = new float2[b - a + 1];
            Array.Copy(line, a, r, 0, r.Length);
            return r;
        }
        /// <summary>A smooth envelope over a profile of needs: max over j of need_j · bump(|s_i − s_j| / width).</summary>
        public static float[] Dilate(float[] need, float spacing, float width)
        {
            int n = need.Length;
            var r = new float[n];
            int reach = (int)math.ceil(width / spacing);
            for (int j = 0; j < n; j++)
            {
                if (need[j] <= 0f) continue;
                for (int k = math.max(0, j - reach); k <= math.min(n - 1, j + reach); k++)
                {
                    float x = math.abs(k - j) * spacing / width;
                    float bump = 1f - x * x * (3f - 2f * x);
                    r[k] = math.max(r[k], need[j] * bump);
                }
            }
            return r;
        }

        /// <summary>
        /// The walk's height: the ground under the curve (median across ±<paramref name="halfWidth"/>), smoothed over
        /// ±<paramref name="smoothing"/> m, clamped to [lo, hi](s), grade-limited to <paramref name="maxGrade"/> both ways.
        /// </summary>
        public void Profile(Func<float2, float> ground, float halfWidth, float smoothing, float maxGrade, Func<float, float> lo, Func<float, float> hi)
        {
            int n = Line.Length;
            var raw = new float[n];
            for (int i = 0; i < n; i++)
            {
                var p = At(S[i], out _, out var nrm);
                float c0 = ground(p), c1 = ground(p + nrm * halfWidth), c2 = ground(p - nrm * halfWidth);
                raw[i] = math.max(math.min(c0, c1), math.min(math.max(c0, c1), c2));
            }
            var sm = SmoothProfile(raw, S, smoothing);
            for (int i = 0; i < n; i++)
            {
                float a = lo != null ? lo(S[i]) : float.MinValue, b = hi != null ? hi(S[i]) : float.MaxValue;
                sm[i] = math.clamp(sm[i], a, math.max(a, b));
            }
            // A second smoothing after the clamp rounds the knees where the band starts to bind; then the grade limit.
            sm = SmoothProfile(sm, S, smoothing * 0.5f);
            for (int i = 0; i < n; i++)
            {
                float a = lo != null ? lo(S[i]) : float.MinValue;
                sm[i] = math.max(sm[i], a);
            }
            LimitGrade(sm, S, maxGrade);
            H = sm;
        }

        public static float[] SmoothProfile(float[] v, float[] s, float window)
        {
            int n = v.Length;
            var r = new float[n];
            for (int i = 0; i < n; i++)
            {
                float sum = 0f, ws = 0f;
                for (int j = 0; j < n; j++)
                {
                    float d = math.abs(s[j] - s[i]);
                    if (d > window) continue;
                    float x = d / (window + 1e-3f);
                    float wgt = 1f - x * x * (3f - 2f * x);
                    sum += v[j] * wgt; ws += wgt;
                }
                r[i] = ws > 0f ? sum / ws : v[i];
            }
            return r;
        }

        public static void LimitGrade(float[] h, float[] s, float g)
        {
            for (int i = 1; i < h.Length; i++) h[i] = math.clamp(h[i], h[i - 1] - g * (s[i] - s[i - 1]), h[i - 1] + g * (s[i] - s[i - 1]));
            for (int i = h.Length - 2; i >= 0; i--) h[i] = math.clamp(h[i], h[i + 1] - g * (s[i + 1] - s[i]), h[i + 1] + g * (s[i + 1] - s[i]));
        }

        // ───────────── metrics ─────────────

        /// <summary>Smallest radius of curvature (m) over a sliding chord of ±<paramref name="half"/> m (∞ for a straight line).</summary>
        public static float MinRadius(float2[] line, float half = 10f)
        {
            if (line == null || line.Length < 3) return float.PositiveInfinity;
            var s = Arc(line);
            float best = float.PositiveInfinity;
            for (int i = 0; i < line.Length; i++)
            {
                int a = i, b = i;
                while (a > 0 && s[i] - s[a] < half) a--;
                while (b < line.Length - 1 && s[b] - s[i] < half) b++;
                if (s[i] - s[a] < half * 0.7f || s[b] - s[i] < half * 0.7f) continue;
                float r = Circumradius(line[a], line[i], line[b]);
                best = math.min(best, r);
            }
            return best;
        }

        /// <summary>
        /// Zig-zags: the number of times the curve's turning direction flips between bends sharper than
        /// <paramref name="minCurvature"/> (1/m) measured over ±<paramref name="half"/> m. A smooth meander scores ~1 per
        /// wave; a paving edge that follows every wiggle of a real waterline scores dozens.
        /// </summary>
        public static int Wiggles(float2[] line, float half = 10f, float minCurvature = 1f / 150f)
        {
            if (line == null || line.Length < 3) return 0;
            var s = Arc(line);
            int flips = 0, last = 0;
            for (int i = 0; i < line.Length; i++)
            {
                int a = i, b = i;
                while (a > 0 && s[i] - s[a] < half) a--;
                while (b < line.Length - 1 && s[b] - s[i] < half) b++;
                if (s[i] - s[a] < half * 0.7f || s[b] - s[i] < half * 0.7f) continue;
                var u = line[i] - line[a]; var v = line[b] - line[i];
                float cross = u.x * v.y - u.y * v.x;
                float r = Circumradius(line[a], line[i], line[b]);
                if (1f / r < minCurvature) continue;
                int sign = cross > 0f ? 1 : -1;
                if (last != 0 && sign != last) flips++;
                last = sign;
            }
            return flips;
        }

        private static float Circumradius(float2 a, float2 b, float2 c)
        {
            float ab = math.distance(a, b), bc = math.distance(b, c), ca = math.distance(c, a);
            var u = b - a; var v = c - a;
            float area2 = math.abs(u.x * v.y - u.y * v.x);
            if (area2 < 1e-6f) return float.PositiveInfinity;
            return ab * bc * ca / (2f * area2);
        }

        /// <summary>Steepest grade of a height profile along arc length.</summary>
        public static float MaxGrade(float[] h, float[] s)
        {
            float g = 0f;
            for (int i = 1; i < h.Length; i++) { float d = s[i] - s[i - 1]; if (d > 0.2f) g = math.max(g, math.abs(h[i] - h[i - 1]) / d); }
            return g;
        }
    }
}
