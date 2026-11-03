// Placemaker roads layer (s52) — "Hillside terraces": the street layout.
//
// The user's picture (image 83): winding streets laid on the hill's contour lines, as with the vanilla Continuous tool —
// but with the ground terraced under them first. One terrace = the street, the zone cells on both sides (1–6 × 8 m)
// and a level shoulder; between terraces a bank (graded ≤ the bank slope, or a steep "wall" face).
//
//   survey      the ground on a 3–12 m grid, smoothed (σ ≥ 12 m: the lumps go, the hill stays), its slope, how much of
//               the area is sloped (≥ the flat threshold) — a flat site gets straight rows and no terraform at all.
//   spacing     a terrace band is W = 2·(road half + zone depth + shoulder) wide; on ground of slope g the next street up
//               must be S = W·a / (a − g) away for the bank between them to stay at the mean bank slope a. The level
//               step is ΔH = g·S at the site's reference slope (60th percentile of the sloped ground): contour streets at
//               a constant height interval, as real hill towns are laid out. Where the hill is steeper the terraces
//               narrow (fewer level cells, measured per station from the neighbours), where it is gentler the banks
//               are wider and gentler.
//   streets     the contour lines of the smoothed ground at those levels (marching squares), clipped to the area,
//               ends that run along the outline trimmed, smoothed by arc length until the radius is a street's, pieces
//               too close to an accepted street or a building dropped, short pieces dropped. Streets are level (their
//               contour height): the terrace is level along them too.
//   neighbours  from every station a ray up the slope and one down to the next street: the terrace band on each side
//               shrinks to what the spacing allows after the bank (TerraceStreet.BandUp / BandDown).
//
// Links between terraces and the bank walks are TerraceRoutes (TerraceRoutes.cs); the ground is TerraceField.
// Pure, deterministic, cancellable.

using System;
using System.Collections.Generic;
using System.Threading;
using Placemaker.Core.Geometry;
using Unity.Mathematics;

namespace Placemaker.Core.Layers.Roads.Terraces
{
    /// <summary>One contour street (or one row on flat ground).</summary>
    internal sealed class TerraceStreet
    {
        public List<float2> P;
        public bool Closed;
        /// <summary>Level index (0 = lowest) and its value of the level function (the height on a slope).</summary>
        public int Level;
        public float Phi;
        /// <summary>Designed height per point (level on a slope; the smoothed ground on a flat site).</summary>
        public float[] H;
        /// <summary>Arc length per point; for a loop one extra entry: the total length.</summary>
        public float[] S;
        public float2[] T, Up;
        /// <summary>Half width of the level band on the uphill / downhill side (m).</summary>
        public float[] BandUp, BandDown;
        /// <summary>Neighbouring street up / down the slope (-1 = none) and its distance.</summary>
        public int[] UpNbr, DownNbr;
        public float[] UpDist, DownDist;
        public float Length;
        /// <summary>Stations where links join (the street is split there).</summary>
        public readonly List<float> Junctions = new List<float>();

        public int Count => P.Count;

        public void Finish(Func<float2, float2> gradient)
        {
            int n = P.Count;
            S = new float[Closed ? n + 1 : n];
            for (int i = 1; i < n; i++) S[i] = S[i - 1] + math.distance(P[i - 1], P[i]);
            Length = Closed ? S[n - 1] + math.distance(P[n - 1], P[0]) : S[n - 1];
            if (Closed) S[n] = Length;
            T = Lines.Tangents(P, Closed);
            Up = new float2[n];
            // The uphill side, decided once per street (the level function increases there); a street never flips sides.
            float vote = 0f;
            for (int i = 0; i < n; i++)
            {
                var nrm = new float2(-T[i].y, T[i].x);
                vote += math.dot(nrm, gradient(P[i]));
            }
            float sign = vote >= 0f ? 1f : -1f;
            for (int i = 0; i < n; i++) Up[i] = new float2(-T[i].y, T[i].x) * sign;
        }

        /// <summary>Station wrapped onto the street (loops) or clamped (open).</summary>
        public float Wrap(float s)
        {
            if (Closed) { s %= Length; if (s < 0f) s += Length; return s; }
            return math.clamp(s, 0f, Length);
        }

        /// <summary>Along-street distance between two stations (the short way round on a loop).</summary>
        public float Gap(float a, float b)
        {
            float d = math.abs(Wrap(a) - Wrap(b));
            return Closed ? math.min(d, Length - d) : d;
        }

        /// <summary>Segment index and t at station s.</summary>
        public void Locate(float s, out int seg, out float t)
        {
            s = Wrap(s);
            int n = P.Count;
            int last = Closed ? n : n - 1;   // number of segments
            int lo = 0, hi = last - 1;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) >> 1;
                if (S[mid] <= s) lo = mid; else hi = mid - 1;
            }
            seg = lo;
            float len = (seg + 1 < S.Length ? S[seg + 1] : Length) - S[seg];
            t = len > 1e-6f ? math.saturate((s - S[seg]) / len) : 0f;
        }

        public float2 PointAt(float s)
        {
            Locate(s, out int seg, out float t);
            return math.lerp(P[seg], P[(seg + 1) % P.Count], t);
        }

        public float HeightAt(float s)
        {
            Locate(s, out int seg, out float t);
            return math.lerp(H[seg], H[(seg + 1) % P.Count], t);
        }

        public float2 TangentAt(float s)
        {
            Locate(s, out int seg, out float t);
            return math.normalizesafe(math.lerp(T[seg], T[(seg + 1) % P.Count], t), T[seg]);
        }

        public float2 UpAt(float s)
        {
            Locate(s, out int seg, out float t);
            return math.normalizesafe(math.lerp(Up[seg], Up[(seg + 1) % P.Count], t), Up[seg]);
        }

        public float BandAt(float s, bool up)
        {
            Locate(s, out int seg, out float t);
            var b = up ? BandUp : BandDown;
            return math.lerp(b[seg], b[(seg + 1) % P.Count], t);
        }

        /// <summary>Station of the closest point to q (and its distance).</summary>
        public float StationOf(float2 q, out float dist)
        {
            dist = Lines.Closest(P, Closed, q, out int seg, out float t);
            float len = (seg + 1 < S.Length ? S[seg + 1] : Length) - S[seg];
            return S[seg] + t * len;
        }

        /// <summary>Points of the street between stations a and b (a &lt; b; on a loop b may pass the start), ends interpolated.</summary>
        public List<float2> Between(float a, float b, out List<float> stations)
        {
            var pts = new List<float2> { PointAt(a) };
            stations = new List<float> { a };
            int n = P.Count;
            // Vertices strictly between a and b, by unwrapped station (a loop's second lap covers b past the start).
            int laps = Closed ? 2 : 1;
            for (int lap = 0; lap < laps; lap++)
                for (int k = 0; k < n; k++)
                {
                    float s = S[k] + lap * Length;
                    if (s > a + 0.25f && s < b - 0.25f) { pts.Add(P[k]); stations.Add(s); }
                }
            pts.Add(PointAt(b)); stations.Add(b);
            return pts;
        }
    }

    internal sealed class TerraceLayout
    {
        public bool Sloped;
        /// <summary>Level step: metres of height on a slope, metres across the rows on a flat site.</summary>
        public float LevelStep;
        /// <summary>Planned street spacing (m) at the reference slope.</summary>
        public float Spacing;
        public float RefGrade;
        /// <summary>The reference slope is steeper than the banks allow at this terrace depth: the terraces narrow.</summary>
        public bool TooSteep;
        /// <summary>Share of the area steeper than the flat threshold.</summary>
        public float SlopedShare;
        public readonly List<TerraceStreet> Streets = new List<TerraceStreet>();
        public readonly List<TerraceRoute> Links = new List<TerraceRoute>();
        public readonly List<TerraceRoute> Walks = new List<TerraceRoute>();
        /// <summary>Smoothed ground and its slope (rise/run) over the area.</summary>
        public ScalarGrid Ground, Slope;
        /// <summary>The level function (smoothed ground on a slope, distance across the rows on a flat site).</summary>
        public ScalarGrid Phi;
        public float2[] Boundary;
        /// <summary>Why candidate links and walks were turned down (diagnostics: tests, gallery).</summary>
        public readonly Dictionary<string, int> Rejects = new Dictionary<string, int>();
        public void Reject(string why) { Rejects.TryGetValue(why, out int n); Rejects[why] = n + 1; }

        /// <summary>Natural slope of the smoothed ground at p (rise/run).</summary>
        public float SlopeAt(float2 p) => Slope.Sample(p);

        public static TerraceLayout Build(float2[] boundary, Func<float2, float> heightAt, TerraceOptions o, RoadObstacles obstacles, CancellationToken cancel)
        {
            var lay = new TerraceLayout { Boundary = boundary };
            Poly.Bounds(boundary, out float2 bmin, out float2 bmax);
            float ext = math.cmax(bmax - bmin);
            float cell = math.clamp(ext / 160f, 3f, 12f);
            var g = ScalarGrid.Over(bmin - 30f, bmax + 30f, cell);
            for (int j = 0; j < g.Ny; j++)
                for (int i = 0; i < g.Nx; i++)
                {
                    float h = heightAt(g.Pos(i, j));
                    g.V[j * g.Nx + i] = math.isfinite(h) ? h : 0f;
                }
            g.Blur(math.max(12f, 2f * cell));
            lay.Ground = g;
            var slope = g.CloneEmpty();
            for (int j = 0; j < g.Ny; j++)
                for (int i = 0; i < g.Nx; i++)
                    slope.V[j * g.Nx + i] = math.length(g.Gradient(g.Pos(i, j)));
            lay.Slope = slope;
            cancel.ThrowIfCancellationRequested();

            // ── survey ──
            var inside = new List<int>();
            for (int j = 0; j < g.Ny; j++)
                for (int i = 0; i < g.Nx; i++)
                    if (Poly.Contains(boundary, g.Pos(i, j))) inside.Add(j * g.Nx + i);
            if (inside.Count < 4) return lay;
            var slopes = new List<float>();
            var heights = new List<float>();
            foreach (int k in inside)
            {
                heights.Add(g.V[k]);
                if (slope.V[k] >= o.FlatSlope) slopes.Add(slope.V[k]);
            }
            heights.Sort();
            float relief = Pct(heights, 0.97f) - Pct(heights, 0.03f);
            lay.SlopedShare = slopes.Count / (float)inside.Count;
            lay.Sloped = lay.SlopedShare >= 0.2f && relief >= 3f;

            // ── level function and spacing ──
            var phi = g.CloneEmpty();
            if (lay.Sloped)
            {
                Array.Copy(g.V, phi.V, g.V.Length);
                slopes.Sort();
                float gRef = Pct(slopes, 0.6f);
                float a = o.MeanBank;
                if (gRef > 0.8f * a) { lay.TooSteep = true; gRef = 0.8f * a; }
                lay.RefGrade = gRef;
                float w = 2f * o.Band;
                lay.Spacing = w * a / math.max(0.01f, a - gRef);
                lay.LevelStep = math.max(2f, gRef * lay.Spacing);
            }
            else
            {
                // Rows along the area's long axis, back to back: a street, its zone cells, the next street's cells.
                float2 axis = math.normalizesafe(RoadBlocks.PrincipalAxis(boundary), new float2(1, 0));
                float2 across = new float2(-axis.y, axis.x);
                for (int j = 0; j < g.Ny; j++)
                    for (int i = 0; i < g.Nx; i++) phi.V[j * g.Nx + i] = math.dot(g.Pos(i, j), across);
                lay.Spacing = lay.LevelStep = 2f * (o.StreetHalf + o.ZoneDepth);
            }
            lay.Phi = phi;
            float lo = float.MaxValue, hi = float.MinValue;
            foreach (int k in inside) { lo = math.min(lo, phi.V[k]); hi = math.max(hi, phi.V[k]); }
            cancel.ThrowIfCancellationRequested();

            // ── levels: the offset that keeps the most street ──
            float minLen = math.max(90f, 1.2f * o.Band);
            float bestScore = -1f; float bestOffset = 0.5f;
            const int Offsets = 6;
            for (int f = 0; f < Offsets; f++)
            {
                float off = (f + 0.5f) / Offsets;
                float score = 0f;
                for (float lv = lo + off * lay.LevelStep; lv < hi; lv += lay.LevelStep)
                    foreach (var (pts, _) in Trace(phi, lv, boundary, minLen, o)) score += Lines.Length(pts);
                if (score > bestScore + 1f) { bestScore = score; bestOffset = off; }
                cancel.ThrowIfCancellationRequested();
            }

            // ── streets ──
            var accepted = new SegGrid(24f);
            var scratch = new List<int>();
            int level = 0;
            for (float lv = lo + bestOffset * lay.LevelStep; lv < hi; lv += lay.LevelStep, level++)
            {
                var pieces = Trace(phi, lv, boundary, minLen, o);
                pieces.Sort((x, y) => Lines.Length(y.pts).CompareTo(Lines.Length(x.pts)));
                foreach (var (raw, closed) in pieces)
                {
                    cancel.ThrowIfCancellationRequested();
                    foreach (var (pts, loop) in Smooth(raw, closed, boundary, o, minLen))
                        foreach (var run in Spaced(pts, loop, lay, level, lv, accepted, obstacles, o, minLen, scratch))
                        {
                            var st = new TerraceStreet { P = run.pts, Closed = run.loop, Level = level, Phi = lv };
                            st.Finish(phi.Gradient);
                            st.H = new float[st.Count];
                            if (lay.Sloped) for (int k = 0; k < st.Count; k++) st.H[k] = lv;
                            else FollowGround(st, heightAt);
                            int id = lay.Streets.Count;
                            lay.Streets.Add(st);
                            accepted.AddLine(st.P, st.Closed, id);
                        }
                }
            }
            cancel.ThrowIfCancellationRequested();
            Neighbours(lay, o, cancel);
            return lay;
        }

        private static float Pct(List<float> sorted, float q)
        {
            if (sorted.Count == 0) return 0f;
            int i = math.clamp((int)math.floor(q * (sorted.Count - 1)), 0, sorted.Count - 1);
            return sorted[i];
        }

        /// <summary>Contour pieces of one level inside the area (≥ 3 m in), ends running along the outline trimmed.</summary>
        private static List<(List<float2> pts, bool closed)> Trace(ScalarGrid phi, float level, float2[] boundary, float minLen, TerraceOptions o)
        {
            var r = new List<(List<float2>, bool)>();
            var closed = new List<bool>();
            var lines = phi.Contours(level, closed);
            for (int li = 0; li < lines.Count; li++)
            {
                var line = Lines.Resample(lines[li], 4f, closed[li]);
                if (line.Count < 3) continue;
                var pieces = Lines.ClipInside(line, closed[li], boundary, 3f);
                bool whole = pieces.Count == 1 && closed[li] && pieces[0].Count == line.Count;
                foreach (var p in pieces)
                {
                    var q = whole ? p : TrimAlongOutline(p, boundary, o);
                    if (q.Count >= 3 && Lines.Length(q, whole) >= minLen) r.Add((q, whole));
                }
            }
            return r;
        }

        /// <summary>
        /// A contour that runs out along the outline (within half a terrace of it, nearly parallel) would leave its terrace
        /// half outside the area: its ends are cut back to where it leaves the outline.
        /// </summary>
        private static List<float2> TrimAlongOutline(List<float2> p, float2[] boundary, TerraceOptions o)
        {
            float near = math.max(18f, o.Band * 0.6f);
            bool Along(int i, int dirSign)
            {
                float2 q = p[i];
                if (Poly.DistanceToBoundary(boundary, q) > near) return false;
                Poly.ClosestPointOnBoundary(boundary, q, out int seg, out _);
                float2 bd = math.normalizesafe(boundary[(seg + 1) % boundary.Length] - boundary[seg]);
                int j = math.clamp(i + dirSign, 0, p.Count - 1);
                float2 ld = math.normalizesafe(p[j] - p[i]);
                return math.abs(math.dot(bd, ld)) > 0.8f;
            }
            int a = 0, b = p.Count - 1;
            while (a < b && Along(a, 1)) a++;
            while (b > a && Along(b, -1)) b--;
            return b - a >= 2 ? p.GetRange(a, b - a + 1) : new List<float2>();
        }

        /// <summary>Smoothed until the tightest bend is a street's radius; a loop too small to be a street goes.</summary>
        private static List<(List<float2> pts, bool loop)> Smooth(List<float2> raw, bool closed, float2[] boundary, TerraceOptions o, float minLen)
        {
            var r = new List<(List<float2>, bool)>();
            if (closed)
            {
                float area = Poly.Area(raw);
                if (math.sqrt(area / math.PI) < o.StreetRadius * 1.25f) return r;
            }
            List<float2> best = raw;
            float sigma = 2.5f;   // points (4 m apart)
            for (int it = 0; it < 7; it++)
            {
                var s = Lines.Smooth(raw, sigma, closed);
                best = s;
                if (Lines.MinRadius(s, closed, 12f) >= o.StreetRadius) break;
                sigma *= 1.45f;
            }
            best = Lines.Resample(best, 4f, closed);
            // Smoothing may pull a piece across the outline: keep the inside runs.
            var pieces = Lines.ClipInside(best, closed, boundary, 2f);
            bool whole = closed && pieces.Count == 1 && pieces[0].Count == best.Count;
            foreach (var p in pieces)
                if (p.Count >= 3 && Lines.Length(p, whole) >= minLen) r.Add((p, whole));
            return r;
        }

        /// <summary>
        /// Runs of a street that keep their distance: from accepted streets (two road widths and the bank the level step
        /// needs; a same-level street a whole terrace) and from buildings. A loop that has to give way becomes open runs.
        /// </summary>
        private static List<(List<float2> pts, bool loop)> Spaced(List<float2> pts, bool loop, TerraceLayout lay, int level, float lv, SegGrid accepted,
            RoadObstacles obstacles, TerraceOptions o, float minLen, List<int> scratch)
        {
            float bank = lay.Sloped ? lay.LevelStep / math.max(0.1f, o.BankSlope) : 0f;
            float minSep = lay.Sloped ? 2f * (o.StreetHalf + TerraceOptions.Shoulder) + bank : 2f * (o.StreetHalf + 8f);
            int n = pts.Count;
            var ok = new bool[n];
            bool any = false, all = true;
            for (int i = 0; i < n; i++)
            {
                float d = accepted.Nearest(pts[i], minSep + 1f, null, scratch, out _);
                // A building keeps its ground: the street passes it by its half width, a verge and the bank its cut or fill needs.
                float bankRun = lay.Sloped ? math.abs(lv - lay.Ground.Sample(pts[i])) / math.max(0.1f, o.BankSlope) + 3f : 0f;
                bool free = d >= minSep && (obstacles == null || !obstacles.BlockedAt(pts[i], o.StreetHalf + 2f + bankRun));
                ok[i] = free;
                any |= free; all &= free;
            }
            var r = new List<(List<float2>, bool)>();
            if (all) { r.Add((pts, loop)); return r; }
            if (!any) return r;
            int start = 0;
            if (loop) { while (ok[start]) start++; }   // begin at a blocked point so runs do not wrap
            List<float2> cur = null;
            int count = loop ? n + 1 : n;
            for (int k = 0; k < count; k++)
            {
                int i = (start + k) % n;
                if (ok[i]) { if (cur == null) cur = new List<float2>(); cur.Add(pts[i]); }
                else if (cur != null) { Flush(cur); cur = null; }
            }
            if (cur != null) Flush(cur);
            return r;

            void Flush(List<float2> run)
            {
                // Keep a little away from what blocked it (the run ends inside the free part).
                if (run.Count > 6) { run.RemoveAt(0); run.RemoveAt(run.Count - 1); }
                if (run.Count >= 3 && Lines.Length(run) >= minLen) r.Add((run, false));
            }
        }

        /// <summary>Flat site: the street follows the smoothed ground (no terraform there).</summary>
        private static void FollowGround(TerraceStreet st, Func<float2, float> heightAt)
        {
            int n = st.Count;
            var raw = new float[n];
            for (int i = 0; i < n; i++) raw[i] = heightAt(st.P[i]);
            int rad = 7;   // ±28 m
            for (int i = 0; i < n; i++)
            {
                float sum = 0f; int c = 0;
                for (int k = -rad; k <= rad; k++)
                {
                    int j = i + k;
                    if (st.Closed) j = ((j % n) + n) % n;
                    else if (j < 0 || j >= n) continue;
                    sum += raw[j]; c++;
                }
                st.H[i] = sum / c;
            }
        }

        /// <summary>
        /// Rays up and down the slope from every station to the next street; the level band on each side is what the gap
        /// leaves after the bank the height step needs, split between the two terraces (never wider than the full band,
        /// never narrower than the road and its shoulder).
        /// </summary>
        private static void Neighbours(TerraceLayout lay, TerraceOptions o, CancellationToken cancel)
        {
            var idx = new SegGrid(20f);
            for (int s = 0; s < lay.Streets.Count; s++) idx.AddLine(lay.Streets[s].P, lay.Streets[s].Closed, s);
            var scratch = new List<int>();
            float maxRay = math.max(3f * lay.Spacing, 4f * o.Band);
            float a = o.MeanBank;
            float minBand = o.StreetHalf + TerraceOptions.Shoulder;
            for (int s = 0; s < lay.Streets.Count; s++)
            {
                cancel.ThrowIfCancellationRequested();
                var st = lay.Streets[s];
                int n = st.Count;
                st.UpNbr = new int[n]; st.DownNbr = new int[n];
                st.UpDist = new float[n]; st.DownDist = new float[n];
                st.BandUp = new float[n]; st.BandDown = new float[n];
                for (int i = 0; i < n; i++)
                {
                    for (int side = 0; side < 2; side++)
                    {
                        float2 dir = side == 0 ? st.Up[i] : -st.Up[i];
                        int hit = idx.Ray(st.P[i], dir, maxRay, null, out float dist, scratch);
                        int nb = hit >= 0 ? idx.Owner[hit] : -1;
                        float band = o.Band;
                        if (nb >= 0)
                        {
                            float2 hp = st.P[i] + dir * dist;
                            var other = lay.Streets[nb];
                            float hOther = other.HeightAt(other.StationOf(hp, out _));
                            float dh = lay.Sloped ? math.abs(hOther - st.H[i]) : 0f;
                            float bankRun = dh / math.max(0.05f, a);
                            band = math.clamp((dist - bankRun) * 0.5f, minBand, o.Band);
                            if (!lay.Sloped) band = math.clamp(dist * 0.5f, minBand, o.StreetHalf + o.ZoneDepth);
                        }
                        else if (!lay.Sloped) band = o.StreetHalf + o.ZoneDepth;
                        if (side == 0) { st.UpNbr[i] = nb; st.UpDist[i] = nb >= 0 ? dist : float.PositiveInfinity; st.BandUp[i] = band; }
                        else { st.DownNbr[i] = nb; st.DownDist[i] = nb >= 0 ? dist : float.PositiveInfinity; st.BandDown[i] = band; }
                    }
                }
                // A terrace edge follows the street smoothly: a running minimum (a narrow spot narrows its surroundings a
                // little), then a moving average.
                SmoothBand(st.BandUp, st.Closed, minBand);
                SmoothBand(st.BandDown, st.Closed, minBand);
            }
        }

        private static void SmoothBand(float[] b, bool closed, float minBand)
        {
            int n = b.Length;
            var tmp = new float[n];
            const int R = 3;   // ±12 m
            for (int i = 0; i < n; i++)
            {
                float m = b[i];
                for (int k = -R; k <= R; k++)
                {
                    int j = i + k;
                    if (closed) j = ((j % n) + n) % n; else if (j < 0 || j >= n) continue;
                    m = math.min(m, b[j]);
                }
                tmp[i] = m;
            }
            for (int i = 0; i < n; i++)
            {
                float sum = 0f; int c = 0;
                for (int k = -R; k <= R; k++)
                {
                    int j = i + k;
                    if (closed) j = ((j % n) + n) % n; else if (j < 0 || j >= n) continue;
                    sum += tmp[j]; c++;
                }
                b[i] = math.max(minBand, sum / c);
            }
        }
    }
}
