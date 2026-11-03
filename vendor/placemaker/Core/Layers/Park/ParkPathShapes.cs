// Path styles of round 5 (type wave, builder 11): designed figures instead of "a network that
// connects the entrances". Every style returns a ParkPathGraph built from segments, so curves,
// furniture, bins, spurs and the plaza clip work unchanged.
//
//   riverside     two paths along the banks of a winding ribbon down the park's long axis; the
//                 banks meet at both ends (no dead ends) and the ribbon stays open lawn
//   figureEight   a lemniscate along the long axis: two lobes that cross at the centre
//   spokeRing     a ring path following the outline inset, a small hub roundel at the deepest
//                 point and 4–6 spokes between them (with a plaza: the spokes run to the plaza)
//   woodlandTrail one narrow irregular loop through the interior, wiggling at a short wavelength
//   boardwalk     an angular loop of straight runs with dog-leg jogs (round the pond when there is one)
//   promenade     a straight wide avenue down the long axis with parallel side walks, joined at the ends
//   circuit       a smooth perimeter loop inside the planting belt (a walking / jogging circuit)
//
// Rules shared by all: every vertex passes `ok` (inside with clearance, out of the lake and plaza),
// no segment crosses the outline or another segment except at a shared node, entrances join the
// figure at the nearest point they can reach without crossing it. When a figure does not fit
// even after shrinking, the style returns an empty graph and the park falls back to the planner.

using System;
using System.Collections.Generic;
using Placemaker.Core.Geometry;
using Placemaker.Core.Layers.Common;
using Unity.Mathematics;

namespace Placemaker.Core.Layers.Park
{
    public static partial class ParkPathStyles
    {
        /// <summary>Styles drawn with straight runs (no Bézier smoothing through bends).</summary>
        public static bool Straight(string style) => style == "formal" || style == "boardwalk" || style == "promenade";

        // ───────────── riverside ─────────────

        public static ParkPathGraph Riverside(LayerRegion region, IReadOnlyList<float2> gates, uint seed, Func<float2, bool> ok)
        {
            if (!Spine(region, ok, out var c, out var axis, out var side, out var a, out var b, out float halfWidth)) return ParkPathGraph.Empty();
            float len = math.distance(a, b);
            if (len < Layout.M(50f) || halfWidth < Layout.M(12f)) return ParkPathGraph.Empty();
            for (int attempt = 0; attempt < 5; attempt++)
            {
                float shrink = 1f - attempt * 0.18f;
                float amp = math.clamp(halfWidth * 0.32f, Layout.M(3f), Layout.M(26f)) * shrink;
                float bank = math.clamp(halfWidth * 0.22f, Layout.M(5f), Layout.M(16f)) * shrink;
                int n = math.clamp((int)math.round(len / Layout.M(18f)), 6, 26);
                float waves = math.max(1f, math.round(len / math.max(Layout.M(60f), halfWidth * 2.6f)));
                float phase = Hashing.Unit(Hashing.Mix(seed, 0x51u)) * math.PI;
                var left = new List<float2>(); var right = new List<float2>();
                bool good = true;
                for (int i = 0; i <= n && good; i++)
                {
                    float t = i / (float)n;
                    var basePt = math.lerp(a, b, t);
                    // Taper at the ends so both banks meet in one node there.
                    float taper = math.sin(math.PI * t);
                    float jitter = 0.8f + 0.4f * Hashing.Unit(Hashing.Mix(seed, (uint)i + 7u));
                    float m = amp * taper * math.sin(phase + waves * math.PI * t) * jitter;
                    float w = bank * math.sqrt(taper);
                    var l = basePt + side * (m + w);
                    var r = basePt + side * (m - w);
                    if (i == 0 || i == n) { l = r = basePt; }
                    if (!ok(l) || !ok(r)) good = false;
                    left.Add(l); right.Add(r);
                }
                if (!good) continue;
                var segs = new List<float2>();
                var pts = new List<float2>();
                for (int i = 1; i <= n; i++) { segs.Add(left[i - 1]); segs.Add(left[i]); }
                for (int i = 1; i <= n; i++) { segs.Add(right[i - 1]); segs.Add(right[i]); }
                for (int i = 0; i <= n; i++) { pts.Add(left[i]); if (i > 0 && i < n) pts.Add(right[i]); }
                if (!SegmentsOk(region, segs, ok)) continue;
                // A footbridge across the ribbon in the middle (a crossing path between the banks).
                int mid = n / 2;
                if (n >= 8 && math.distance(left[mid], right[mid]) > Layout.M(6f)) { segs.Add(left[mid]); segs.Add(right[mid]); }
                JoinGates(region, gates, pts, segs, ok);
                return ParkPathGraph.FromSegments(segs, gates);
            }
            return ParkPathGraph.Empty();
        }

        // ───────────── figure-eight ─────────────

        public static ParkPathGraph FigureEight(LayerRegion region, IReadOnlyList<float2> gates, Func<float2, bool> ok)
        {
            if (!Spine(region, ok, out var c, out var axis, out var side, out var a0, out var b0, out float halfWidth)) return ParkPathGraph.Empty();
            float halfLen = math.min(math.distance(c, a0), math.distance(c, b0));
            float a = halfLen * 0.95f;
            const int N = 32;   // multiple of 4: t = π/2 and 3π/2 land exactly on the crossing
            for (int attempt = 0; attempt < 7; attempt++, a *= 0.86f)
            {
                if (a < Layout.M(16f)) break;
                // Lemniscate of Bernoulli; the lobes are widened to use the park's width.
                float ky = math.clamp(halfWidth * 0.85f / (0.354f * a), 0.7f, 2.4f);
                var pts = new List<float2>();
                bool good = true;
                for (int i = 0; i < N && good; i++)
                {
                    float t = i * 2f * math.PI / N;
                    float s = math.sin(t), co = math.cos(t);
                    float den = 1f + s * s;
                    var p = c + axis * (a * co / den) + side * (ky * a * s * co / den);
                    if (i == N / 4 || i == 3 * N / 4) p = c;
                    if (!ok(p)) good = false;
                    pts.Add(p);
                }
                if (!good) continue;
                var segs = new List<float2>();
                for (int i = 0; i < N; i++) { segs.Add(pts[i]); segs.Add(pts[(i + 1) % N]); }
                if (!SegmentsOk(region, segs, ok)) continue;
                JoinGates(region, gates, pts, segs, ok);
                return ParkPathGraph.FromSegments(segs, gates);
            }
            return ParkPathGraph.Empty();
        }

        // ───────────── spoke & ring ─────────────

        public static ParkPathGraph SpokeRing(LayerRegion region, IReadOnlyList<float2> gates, float2[] plaza, Func<float2, bool> ok)
        {
            var hub = region.DeepestPoint(out float inscribed, null, 16);
            bool plazaHub = plaza != null && plaza.Length >= 3;
            if (plazaHub) hub = Poly.Centroid(plaza);
            if (inscribed < Layout.M(18f)) return ParkPathGraph.Empty();
            int spokes = math.clamp((int)math.round(math.sqrt(Layout.DesignArea(region.Area)) / 45f) + 3, 4, 6);
            for (int attempt = 0; attempt < 4; attempt++)
            {
                float depth = math.max(Layout.M(6f), inscribed * (0.32f + attempt * 0.08f));
                var ring = LargestInset(region, depth, Layout.M(16f));
                if (ring == null || ring.Length < 6 || !Poly.Contains(ring, hub)) continue;
                bool good = true;
                foreach (var p in ring) if (!ok(p)) { good = false; break; }
                if (!good) continue;
                float hubR = plazaHub ? 0f : math.clamp(inscribed * 0.12f, Layout.M(5f), Layout.M(10f));
                float phase = math.atan2(ring[0].y - hub.y, ring[0].x - hub.x);
                var segs = new List<float2>();
                var hubPts = new List<float2>();
                if (hubR > 0f)
                {
                    for (int k = 0; k < spokes * 2; k++)
                    {
                        float ang = phase + k * math.PI / spokes;
                        hubPts.Add(hub + new float2(math.cos(ang), math.sin(ang)) * hubR);
                    }
                    for (int k = 0; k < hubPts.Count; k++) { segs.Add(hubPts[k]); segs.Add(hubPts[(k + 1) % hubPts.Count]); }
                }
                // Spokes: from the hub (or the plaza centre — clipped at its edge later) out to the ring.
                var cuts = new List<(int edge, float t, float2 p)>();
                for (int k = 0; k < spokes && good; k++)
                {
                    float ang = phase + k * 2f * math.PI / spokes;
                    var dir = new float2(math.cos(ang), math.sin(ang));
                    var from = hubR > 0f ? hubPts[k * 2] : hub;
                    if (!RayHitsRing(ring, from, dir, out int edge, out float t, out var hit)) { good = false; break; }
                    cuts.Add((edge, t, hit));
                    segs.Add(from); segs.Add(hit);
                }
                if (!good) continue;
                var loopPts = InsertCuts(ring, cuts);
                for (int i = 0; i < loopPts.Count; i++) { segs.Add(loopPts[i]); segs.Add(loopPts[(i + 1) % loopPts.Count]); }
                if (!SegmentsOk(region, segs, plazaHub ? (Func<float2, bool>)(p => region.ContainsDisc(p, 2f)) : ok)) continue;
                JoinGates(region, gates, loopPts, segs, ok);
                return ParkPathGraph.FromSegments(segs, gates);
            }
            return ParkPathGraph.Empty();
        }

        // ───────────── woodland trail ─────────────

        public static ParkPathGraph WoodlandTrail(LayerRegion region, IReadOnlyList<float2> gates, uint seed, Func<float2, bool> ok)
        {
            var c = region.DeepestPoint(out float inscribed, null, 16);
            if (inscribed < Layout.M(10f) || !ok(c)) return ParkPathGraph.Empty();
            int k = math.clamp((int)math.round(math.sqrt(Layout.DesignArea(region.Area)) / 30f) + 4, 5, 10);
            float phase = Hashing.Unit(Hashing.Mix(seed, 0x7A11u)) * 2f * math.PI;
            float far = math.length(region.Max - region.Min);
            var way = new List<float2>();
            for (int i = 0; i < k; i++)
            {
                uint h = Hashing.Mix(seed, (uint)i + 0x300u);
                float ang = phase + (i + (Hashing.Unit(h) - 0.5f) * 0.5f) * 2f * math.PI / k;
                var dir = new float2(math.cos(ang), math.sin(ang));
                float reach = 0f;
                for (float d = 2f; d < far; d += 2f) { if (!ok(c + dir * d)) break; reach = d; }
                float r = reach * (0.5f + 0.32f * Hashing.Unit(Hashing.Mix(h, 3u)));
                if (r < Layout.M(7f)) continue;
                way.Add(c + dir * r);
            }
            if (way.Count < 4) return ParkPathGraph.Empty();
            // Pull waypoints in until each leg stays inside (concave outlines).
            for (int pass = 0; pass < 4; pass++)
            {
                bool changed = false;
                for (int i = 0; i < way.Count; i++)
                {
                    var p = way[i]; var q = way[(i + 1) % way.Count];
                    if (LegOk(region, p, q, ok)) continue;
                    way[i] = math.lerp(c, p, 0.75f); changed = true;
                }
                if (!changed) break;
            }
            var segs = new List<float2>();
            for (int i = 0; i < way.Count; i++)
            {
                var p = way[i]; var q = way[(i + 1) % way.Count];
                if (!LegOk(region, p, q, ok)) return ParkPathGraph.Empty();
                AddMeander(segs, p, q, Layout.M(4.5f), Layout.M(13f), Hashing.Mix(seed, (uint)i + 0x900u), ok);
            }
            if (!SegmentsOk(region, segs, ok))
            {
                segs.Clear();
                for (int i = 0; i < way.Count; i++) { segs.Add(way[i]); segs.Add(way[(i + 1) % way.Count]); }
                if (!SegmentsOk(region, segs, ok)) return ParkPathGraph.Empty();
            }
            var pts = new List<float2>();
            for (int i = 0; i < segs.Count; i += 2) pts.Add(segs[i]);
            JoinGates(region, gates, pts, segs, ok);
            return ParkPathGraph.FromSegments(segs, gates);
        }

        // ───────────── boardwalk ─────────────

        public static ParkPathGraph Boardwalk(LayerRegion region, IReadOnlyList<float2> gates, float2[] lake, uint seed, Func<float2, bool> ok)
        {
            for (int attempt = 0; attempt < 4; attempt++)
            {
                float2[] loop;
                if (lake != null && lake.Length >= 3)
                {
                    // Round the pond, close to the water (boardwalks hug the shore).
                    loop = LargestInset(lake, -(3.5f + attempt * 1.5f), 0f);
                }
                else
                {
                    region.DeepestPoint(out float inscribed, null, 12);
                    if (inscribed < Layout.M(14f)) return ParkPathGraph.Empty();
                    loop = LargestInset(region, math.max(Layout.M(7f), inscribed * (0.3f + attempt * 0.1f)), 0f);
                }
                if (loop == null || loop.Length < 3) continue;
                // Straight runs with a dog-leg every ~22 m (boards are laid straight, never curved).
                var simple = Poly.Simplify(loop, 3f);
                if (simple.Length < 3) continue;
                var pts = new List<float2>();
                for (int i = 0; i < simple.Length; i++)
                {
                    var p = simple[i]; var q = simple[(i + 1) % simple.Length];
                    pts.Add(p);
                    float len = math.distance(p, q);
                    int pieces = (int)math.floor(len / Layout.M(22f));
                    if (pieces < 2) continue;
                    var n = math.normalizesafe(new float2(-(q - p).y, (q - p).x));
                    for (int j = 1; j < pieces; j++)
                    {
                        float jog = ((j + i) & 1) == 0 ? Layout.M(2.5f) : -Layout.M(2.5f);
                        var basePt = math.lerp(p, q, j / (float)pieces);
                        var x = basePt + n * jog;
                        pts.Add(ok(x) ? x : basePt);
                    }
                }
                bool good = true;
                foreach (var p in pts) if (!ok(p)) { good = false; break; }
                if (!good) continue;
                var segs = new List<float2>();
                for (int i = 0; i < pts.Count; i++) { segs.Add(pts[i]); segs.Add(pts[(i + 1) % pts.Count]); }
                if (!SegmentsOk(region, segs, ok)) continue;
                JoinGates(region, gates, pts, segs, ok);
                return ParkPathGraph.FromSegments(segs, gates);
            }
            return ParkPathGraph.Empty();
        }

        // ───────────── promenade ─────────────

        /// <summary>Avenue + side walks. <paramref name="avenue"/> receives the avenue's end points (its curves are made wide).</summary>
        public static ParkPathGraph Promenade(LayerRegion region, IReadOnlyList<float2> gates, Func<float2, bool> ok, out float2 avenueA, out float2 avenueB)
        {
            avenueA = avenueB = float2.zero;
            if (!Spine(region, ok, out var c, out var axis, out var side, out var a, out var b, out float halfWidth)) return ParkPathGraph.Empty();
            if (math.distance(a, b) < Layout.M(30f)) return ParkPathGraph.Empty();
            avenueA = a; avenueB = b;
            var segs = new List<float2>();
            int n = math.max(1, (int)math.round(math.distance(a, b) / Layout.M(24f)));
            var spine = new List<float2>();
            for (int i = 0; i <= n; i++) spine.Add(math.lerp(a, b, i / (float)n));
            for (int i = 1; i <= n; i++) { segs.Add(spine[i - 1]); segs.Add(spine[i]); }
            var pts = new List<float2>(spine);
            // Side walks where the park is wide enough (one each side, else one, else the avenue alone).
            float offset = math.clamp(halfWidth * 0.55f, Layout.M(9f), Layout.M(30f));
            foreach (float sgn in new[] { 1f, -1f })
            {
                if (halfWidth < Layout.M(16f)) break;
                var o = side * offset * sgn;
                float shorten = math.min(Layout.M(10f), math.distance(a, b) * 0.15f);
                var sa = a + axis * shorten + o; var sb = b - axis * shorten + o;
                if (!ok(sa) || !ok(sb) || !LegOk(region, sa, sb, ok)) continue;
                var walk = new List<float2>();
                for (int i = 0; i <= n; i++) walk.Add(math.lerp(sa, sb, i / (float)n));
                var trial = new List<float2>(segs);
                for (int i = 1; i <= n; i++) { trial.Add(walk[i - 1]); trial.Add(walk[i]); }
                trial.Add(a); trial.Add(sa); trial.Add(b); trial.Add(sb);
                if (!SegmentsOk(region, trial, ok)) continue;
                segs = trial;
                pts.AddRange(walk);
            }
            JoinGates(region, gates, pts, segs, ok);
            return ParkPathGraph.FromSegments(segs, gates);
        }

        // ───────────── circuit ─────────────

        public static ParkPathGraph Circuit(LayerRegion region, IReadOnlyList<float2> gates, Func<float2, bool> ok)
        {
            region.DeepestPoint(out float inscribed, null, 12);
            if (inscribed < Layout.M(10f)) return ParkPathGraph.Empty();
            for (int attempt = 0; attempt < 4; attempt++)
            {
                float depth = math.min(math.clamp(math.sqrt(region.Area) * 0.08f, Layout.M(7f), Layout.M(16f)) + attempt * Layout.M(3f), inscribed * 0.6f);
                var loop = LargestInset(region, depth, 15f);
                if (loop == null || loop.Length < 5) continue;
                loop = Poly.SmoothLoop(loop, 2);
                bool good = true;
                foreach (var p in loop) if (!ok(p)) { good = false; break; }
                if (!good) continue;
                var segs = new List<float2>();
                for (int i = 0; i < loop.Length; i++) { segs.Add(loop[i]); segs.Add(loop[(i + 1) % loop.Length]); }
                if (!SegmentsOk(region, segs, ok)) continue;
                JoinGates(region, gates, new List<float2>(loop), segs, ok);
                return ParkPathGraph.FromSegments(segs, gates);
            }
            return ParkPathGraph.Empty();
        }

        // ───────────── shared helpers ─────────────

        /// <summary>Long-axis chord through the park centre (inset from the outline), the frame, and the usable half width.</summary>
        private static bool Spine(LayerRegion region, Func<float2, bool> ok, out float2 c, out float2 axis, out float2 side,
            out float2 a, out float2 b, out float halfWidth)
        {
            LeisureKit.Frame(region, out c, out axis, out side, out _, out _);
            a = b = c; halfWidth = 0f;
            if (!ok(c)) c = region.DeepestPoint(out _, null, 16);
            if (!ok(c)) return false;
            if (!LeisureKit.Chord(region, c, axis, 8f, out a, out b)) return false;
            // Walk the ends in until they pass `ok` (plaza, lake, clearance).
            for (int i = 0; i < 40 && !ok(a); i++) a = math.lerp(a, c, 0.06f);
            for (int i = 0; i < 40 && !ok(b); i++) b = math.lerp(b, c, 0.06f);
            if (!ok(a) || !ok(b)) return false;
            if (LeisureKit.Chord(region, c, side, 4f, out var s0, out var s1))
                halfWidth = math.min(math.distance(c, s0), math.distance(c, s1));
            return true;
        }

        /// <summary>Largest ring of the outline offset inward by <paramref name="depth"/> (negative = outward), resampled (0 = as is).</summary>
        private static float2[] LargestInset(LayerRegion region, float depth, float resample) => LargestInset(region.Ring, depth, resample);

        private static float2[] LargestInset(float2[] ring, float depth, float resample)
        {
            List<float2[]> rings;
            try { rings = Poly.Offset(ring, depth); } catch (Exception) { return null; }
            float2[] best = null; float area = 0f;
            foreach (var r in rings) { float a = Poly.Area(r); if (a > area) { area = a; best = r; } }
            if (best == null) return null;
            best = Poly.EnsureCcw(best);
            return resample > 0f ? Poly.Resample(best, resample) : best;
        }

        private static bool LegOk(LayerRegion region, float2 a, float2 b, Func<float2, bool> ok)
        {
            if (region.CrossesBoundary(a, b, false)) return false;
            float len = math.distance(a, b);
            int k = math.max(1, (int)math.ceil(len / 3f));
            for (int i = 0; i <= k; i++) if (!ok(math.lerp(a, b, i / (float)k))) return false;
            return true;
        }

        /// <summary>Every segment stays inside and passes `ok` along its length, and no two segments cross except at shared ends.</summary>
        private static bool SegmentsOk(LayerRegion region, List<float2> segs, Func<float2, bool> ok)
        {
            for (int i = 0; i + 1 < segs.Count; i += 2)
                if (!LegOk(region, segs[i], segs[i + 1], ok)) return false;
            return !AnyCrossing(segs);
        }

        public static bool AnyCrossing(List<float2> segs)
        {
            for (int i = 0; i + 1 < segs.Count; i += 2)
                for (int j = i + 2; j + 1 < segs.Count; j += 2)
                    if (CrossesProperly(segs[i], segs[i + 1], segs[j], segs[j + 1])) return true;
            return false;
        }

        private static bool CrossesProperly(float2 a, float2 b, float2 c, float2 d)
        {
            const float eps = 0.25f;
            if (math.distancesq(a, c) < eps || math.distancesq(a, d) < eps || math.distancesq(b, c) < eps || math.distancesq(b, d) < eps) return false;
            return Poly.SegmentsIntersect(a, b, c, d);
        }

        /// <summary>
        /// Joins each entrance to the nearest figure point it can reach in a straight line without
        /// crossing a path or the outline (the gate end itself is on the outline). Returns the count joined.
        /// </summary>
        private static int JoinGates(LayerRegion region, IReadOnlyList<float2> gates, List<float2> points, List<float2> segs, Func<float2, bool> ok)
        {
            int joined = 0;
            var order = new List<(float d, int i)>();
            bool Leg(float2 a, float2 b)
            {
                float len = math.distance(a, b);
                bool fromGate = region.BoundaryDistance(a, 1f) < 0.5f;
                for (float s = fromGate ? 4f : 1f; s < len; s += 3f) if (!ok(math.lerp(a, b, s / len))) return false;
                for (int j = 0; j + 1 < segs.Count; j += 2)
                    if (CrossesProperly(a, b, segs[j], segs[j + 1])) return false;
                return true;
            }
            foreach (var g in gates)
            {
                // s9: straight in from the edge first (GateApproach); the nearest-point join only as a fallback,
                // and then only heading inward (never a leg sideways along the outline).
                int before = segs.Count;
                ParkPathStyles.JoinGates(region, segs, new[] { g }, 0, 1, Leg);
                if (segs.Count > before) { joined++; continue; }
                var inward = GateApproach.Inward(region, g);
                order.Clear();
                for (int i = 0; i < points.Count; i++) order.Add((math.distancesq(g, points[i]), i));
                order.Sort((x, y) => x.d != y.d ? x.d.CompareTo(y.d) : x.i.CompareTo(y.i));
                for (int k = 0; k < order.Count && k < 24; k++)
                {
                    var p = points[order[k].i];
                    if (region.CrossesBoundary(g, p, true) || math.dot(math.normalizesafe(p - g), inward) < 0.5f) continue;
                    bool clear = true;
                    float len = math.distance(g, p);
                    for (float s = 4f; s < len && clear; s += 3f) if (!ok(math.lerp(g, p, s / len))) clear = false;
                    for (int j = 0; j + 1 < segs.Count && clear; j += 2)
                        if (CrossesProperly(g, p, segs[j], segs[j + 1])) clear = false;
                    if (!clear) continue;
                    segs.Add(g); segs.Add(p);
                    joined++;
                    break;
                }
            }
            return joined;
        }

        private static bool RayHitsRing(float2[] ring, float2 from, float2 dir, out int edge, out float t, out float2 hit)
        {
            edge = -1; t = 0f; hit = from;
            float best = float.MaxValue;
            float far = 1e4f;
            for (int i = 0; i < ring.Length; i++)
            {
                var p = ring[i]; var q = ring[(i + 1) % ring.Length];
                if (!Poly.SegmentIntersection(from, from + dir * far, p, q, out var x, out float tr, out float te)) continue;
                float d = tr * far;
                if (d > 1f && d < best) { best = d; edge = i; t = te; hit = x; }
            }
            return edge >= 0;
        }

        /// <summary>The ring's vertices with the cut points inserted in order along each edge.</summary>
        private static List<float2> InsertCuts(float2[] ring, List<(int edge, float t, float2 p)> cuts)
        {
            var result = new List<float2>();
            for (int i = 0; i < ring.Length; i++)
            {
                result.Add(ring[i]);
                var here = new List<(float t, float2 p)>();
                foreach (var c in cuts) if (c.edge == i) here.Add((c.t, c.p));
                here.Sort((x, y) => x.t.CompareTo(y.t));
                foreach (var h in here)
                {
                    // Merge with a vertex closer than 2 m instead of making a sliver edge.
                    if (math.distance(h.p, result[result.Count - 1]) < 2f) { result[result.Count - 1] = h.p; continue; }
                    if (math.distance(h.p, ring[(i + 1) % ring.Length]) < 2f) continue;
                    result.Add(h.p);
                }
            }
            // A cut merged into the next vertex: make sure each cut point is a vertex.
            foreach (var c in cuts)
            {
                bool present = false;
                foreach (var p in result) if (math.distancesq(p, c.p) < 0.01f) { present = true; break; }
                if (!present)
                {
                    int best = 0; float bd = float.MaxValue;
                    for (int i = 0; i < result.Count; i++) { float d = math.distancesq(result[i], c.p); if (d < bd) { bd = d; best = i; } }
                    result[best] = c.p;
                }
            }
            return result;
        }
    }
}
