// Hole routing: lays a sequence of holes into the area like a course architect would — each hole a
// tee → (dogleg bend) → green corridor that fits inside the outline with out-of-bounds margin, keeps a
// safety gap to every earlier hole (and to Reserved/obstacles via Occupancy), and starts a short walk
// from the previous green. Greedy with a one-hole look-ahead over tee candidates: for every candidate
// tee around the previous green, every direction and bend, the longest length (≤ the hole's target)
// that fits wins that direction; the best scoring hole overall is kept. Scores prefer the target
// length (a par mix of short and long holes), room around the green for the next hole, the requested
// dogleg share and — for "loop"/"out and back" routings — greens that drift back home on the last
// holes, which is how real courses end next to the clubhouse.

using System.Collections.Generic;
using System.Threading;
using Placemaker.Core.Geometry;
using Placemaker.Core.Layers.Common;
using Unity.Mathematics;

namespace Placemaker.Core.Layers.Types.Golf
{
    public sealed class GolfHole
    {
        public int Number;
        public float2 Tee, Bend, Green;
        public bool Dogleg;
        public float Length;
        public int Par;
        /// <summary>Centre line tee → green, resampled every few metres (smoothed at the bend).</summary>
        public float2[] Line = new float2[0];

        public float2 TeeDir => math.normalizesafe(Line.Length > 1 ? Line[1] - Line[0] : new float2(0, 1), new float2(0, 1));
        /// <summary>Direction the ball travels into the green.</summary>
        public float2 ApproachDir => math.normalizesafe(Line.Length > 1 ? Green - Line[math.max(0, Line.Length - 3)] : new float2(0, 1), new float2(0, 1));

        /// <summary>Point and unit tangent at fraction f (0 = tee, 1 = green) of the centre line.</summary>
        public float2 At(float f, out float2 tangent)
        {
            f = math.saturate(f);
            float target = f * Length, run = 0f;
            for (int i = 0; i + 1 < Line.Length; i++)
            {
                float len = math.distance(Line[i], Line[i + 1]);
                if (run + len >= target || i + 2 == Line.Length)
                {
                    tangent = math.normalizesafe(Line[i + 1] - Line[i], new float2(0, 1));
                    return Line[i] + tangent * math.clamp(target - run, 0f, len);
                }
                run += len;
            }
            tangent = TeeDir;
            return Tee;
        }
    }

    public struct RoutingSpec
    {
        public int Holes;
        /// <summary>Average hole length (m); individual holes vary around it.</summary>
        public float Length, MinLength;
        /// <summary>Corridor half width (fairway + rough the ball needs).</summary>
        public float HalfWidth;
        public float GreenRadius;
        /// <summary>Clearance to the outline (out of bounds) and between holes.</summary>
        public float EdgeMargin, Gap;
        /// <summary>Walk from a green to the next tee.</summary>
        public float TeeOffset;
        public float DoglegChance;
        /// <summary>"loop", "outAndBack" or "free".</summary>
        public string Style;
        public uint Seed;
    }

    public static class GolfRouting
    {
        public const int CorridorKind = 50;
        public const int GreenKind = 51;
        public const int TeeKind = 52;

        // Par mix: short and long holes alternate, like a real card (multipliers of the average length).
        private static readonly float[] Mix = { 1.0f, 0.62f, 1.3f, 0.9f, 1.12f, 0.55f, 1.0f, 1.25f, 0.82f };
        private static readonly float[] Fractions = { 1f, 0.84f, 0.7f, 0.58f, 0.48f, 0.4f, 0.33f };

        private struct Candidate
        {
            public float Score;
            public float2 Tee, Bend, Green;
            public bool Dogleg;
        }

        public static List<GolfHole> Route(LayerRegion region, Occupancy occ, IReadOnlyList<float2> firstTees, float2 home, RoutingSpec spec,
            CancellationToken cancel)
        {
            var holes = new List<GolfHole>();
            if (spec.Holes <= 0 || firstTees == null || firstTees.Count == 0) return holes;
            float scale = math.max(30f, math.sqrt(region.Area));
            var tees = new List<float2>(firstTees);
            float2 prevDir = math.normalizesafe(region.Centroid - home, new float2(0, 1));
            for (int i = 0; i < spec.Holes; i++)
            {
                cancel.ThrowIfCancellationRequested();
                float target = math.max(spec.MinLength, spec.Length * Mix[i % Mix.Length]);
                bool wantDogleg = Hashing.Unit(Hashing.Mix(spec.Seed, (uint)i + 0xD06u)) < spec.DoglegChance;
                float progress = spec.Holes <= 1 ? 1f : i / (float)(spec.Holes - 1);
                var best = new Candidate { Score = float.MinValue };
                for (int t = 0; t < tees.Count; t++)
                {
                    var tee = tees[t];
                    if (!occ.Free(tee, 3f, 3f + spec.EdgeMargin * 0.5f, 0.5f)) continue;
                    Evaluate(region, occ, tee, prevDir, target, wantDogleg, progress, home, scale, spec, i, ref best);
                }
                // Dead end next to the last green (boxed in by earlier holes or a corner): look further
                // afield before giving up — a longer walk to the next tee beats a 3-hole "course" on a
                // site with room for nine (art pass: 8 ha routed only 3 of 9 holes).
                if (best.Score == float.MinValue)
                {
                    var from = holes.Count > 0 ? holes[holes.Count - 1].Green : home;
                    foreach (float r in new[] { 45f, 70f, 100f, 140f, 200f })
                    {
                        for (int k = 0; k < 16; k++)
                        {
                            float a = k * math.PI / 8f + r * 0.01f;
                            var tee = from + new float2(math.cos(a), math.sin(a)) * r;
                            if (!region.Contains(tee) || !occ.Free(tee, 3f, 3f + spec.EdgeMargin * 0.5f, spec.Gap)) continue;
                            Evaluate(region, occ, tee, prevDir, target, wantDogleg, progress, home, scale, spec, i, ref best);
                        }
                        if (best.Score != float.MinValue) break;
                    }
                }
                if (best.Score == float.MinValue) break;
                var hole = Make(best, i + 1, spec);
                holes.Add(hole);
                Take(occ, hole, spec);
                prevDir = hole.ApproachDir;
                tees = NextTees(hole, spec);
            }
            return holes;
        }

        /// <summary>Candidate tees around a green: beside and behind it, a short walk away.</summary>
        private static List<float2> NextTees(GolfHole h, RoutingSpec spec)
        {
            var r = new List<float2>();
            var ap = h.ApproachDir;
            float d = spec.GreenRadius + spec.TeeOffset;
            // Ahead-left/right first (the walk continues forward), then the sides, then behind.
            foreach (float deg in new[] { 35f, -35f, 80f, -80f, 0f, 125f, -125f })
            {
                float a = math.radians(deg);
                var dir = new float2(ap.x * math.cos(a) - ap.y * math.sin(a), ap.x * math.sin(a) + ap.y * math.cos(a));
                r.Add(h.Green + dir * d);
            }
            return r;
        }

        private static void Evaluate(LayerRegion region, Occupancy occ, float2 tee, float2 prevDir, float target, bool wantDogleg, float progress,
            float2 home, float scale, RoutingSpec spec, int index, ref Candidate best)
        {
            const int Angles = 24;
            float phase = Hashing.Unit(Hashing.Mix(spec.Seed, (uint)index + 0xA9u)) * (2f * math.PI / Angles);
            float bendDeg = 24f + 14f * Hashing.Unit(Hashing.Mix(spec.Seed, (uint)index + 0xBE7u));
            for (int k = 0; k < Angles; k++)
            {
                float a = phase + k * 2f * math.PI / Angles;
                var d = new float2(math.cos(a), math.sin(a));
                for (int bend = 0; bend < 3; bend++)
                {
                    if (bend > 0 && target < spec.MinLength * 1.6f) continue;   // short holes are straight
                    float sign = bend == 1 ? 1f : -1f;
                    foreach (var f in Fractions)
                    {
                        float len = target * f;
                        if (len < spec.MinLength) break;
                        float2 b, g;
                        if (bend == 0) { b = tee + d * (len * 0.5f); g = tee + d * len; }
                        else
                        {
                            float rad = math.radians(bendDeg) * sign;
                            var d2 = new float2(d.x * math.cos(rad) - d.y * math.sin(rad), d.x * math.sin(rad) + d.y * math.cos(rad));
                            b = tee + d * (len * 0.6f);
                            g = b + d2 * (len * 0.4f);
                        }
                        if (!Fits(region, occ, tee, b, g, len, spec, index > 0)) continue;
                        float score = -math.abs(len - target) / target * 2f;
                        score += 0.5f * math.min(occ.Clearance(g, target), target) / target;
                        // Holes that hug the outline leave the interior in one piece for the holes still to come.
                        score += 1.2f * (1f - math.saturate(region.BoundaryDistance(b, scale) / (scale * 0.2f)));
                        if ((bend > 0) == wantDogleg) score += 0.3f;
                        switch (spec.Style)
                        {
                            case "loop":
                                if (progress > 0.45f) score -= math.distance(g, home) / scale * (progress - 0.45f) * 4f;
                                break;
                            case "outAndBack":
                                score += (progress < 0.5f ? 1f : -1.5f) * math.distance(g, home) / scale;
                                break;
                        }
                        // Gentle preference against firing straight back along the previous hole.
                        if (math.dot(d, prevDir) < -0.8f) score -= 0.15f;
                        score += Hashing.Unit(Hashing.Mix(spec.Seed, (uint)(index * 997 + k * 7 + bend))) * 0.05f;
                        if (score > best.Score) best = new Candidate { Score = score, Tee = tee, Bend = b, Green = g, Dogleg = bend > 0 };
                        break;   // longest fitting length for this direction/bend found
                    }
                }
            }
        }

        /// <summary>Corridor half width at distance s from the tee: narrow at the tee boxes, full over the landing zone.</summary>
        public static float HalfWidthAt(float s, float len, RoutingSpec spec)
        {
            float tee = math.min(spec.HalfWidth, 4f);
            float t = math.saturate(s / math.max(1f, len * 0.22f));
            return math.lerp(tee, spec.HalfWidth, t * t * (3f - 2f * t));
        }

        private static bool Fits(LayerRegion region, Occupancy occ, float2 tee, float2 b, float2 g, float len, RoutingSpec spec, bool skipTeeStart)
        {
            // Cheap rejects first: the green, then the bend.
            float rg = spec.GreenRadius;
            if (!region.ContainsDisc(g, rg + spec.EdgeMargin)) return false;
            if (!occ.Free(g, rg + 2f, rg + spec.EdgeMargin, spec.Gap)) return false;
            if (!region.Contains(b)) return false;
            float l1 = math.distance(tee, b), l2 = math.distance(b, g);
            float step = math.max(4f, spec.HalfWidth * 0.6f);
            float skip = skipTeeStart ? 2f : 0f;
            for (float s = skip; s < l1 + l2 - rg; s += step)
            {
                var p = s <= l1 ? math.lerp(tee, b, s / math.max(1e-3f, l1)) : math.lerp(b, g, (s - l1) / math.max(1e-3f, l2));
                float hw = HalfWidthAt(s, len, spec);
                // The tee end may sit a short walk from the previous green / clubhouse; the full safety gap applies down the hole.
                float gap = math.lerp(0.5f, spec.Gap, math.saturate((s - 4f) / 12f));
                if (!occ.Free(p, hw, hw + spec.EdgeMargin, gap)) return false;
            }
            return true;
        }

        private static GolfHole Make(Candidate c, int number, RoutingSpec spec)
        {
            var line = new List<float2> { c.Tee };
            if (c.Dogleg)
            {
                var d1 = math.normalizesafe(c.Bend - c.Tee); var d2 = math.normalizesafe(c.Green - c.Bend);
                float rc = math.min(math.max(spec.HalfWidth * 2.5f, 18f), math.min(math.distance(c.Tee, c.Bend), math.distance(c.Bend, c.Green)) * 0.45f);
                var p1 = c.Bend - d1 * rc; var p2 = c.Bend + d2 * rc;
                line.Add(p1);
                for (int k = 1; k < 6; k++)
                {
                    float t = k / 6f, u = 1f - t;
                    line.Add(u * u * p1 + 2f * u * t * c.Bend + t * t * p2);
                }
                line.Add(p2);
            }
            line.Add(c.Green);
            var resampled = Poly.ResamplePolyline(line, 5f);
            float length = 0f;
            for (int i = 0; i + 1 < resampled.Length; i++) length += math.distance(resampled[i], resampled[i + 1]);
            return new GolfHole
            {
                Number = number, Tee = c.Tee, Bend = c.Bend, Green = c.Green, Dogleg = c.Dogleg, Line = resampled, Length = length,
                Par = ParFor(length, spec),
            };
        }

        /// <summary>Par by length (men's guideline: ≤ 230 m par 3, ≤ 430 m par 4, else 5); scaled for short formats.</summary>
        public static int ParFor(float length, RoutingSpec spec)
        {
            if (spec.Length < 110f) return length > spec.Length * 1.35f ? 4 : 3;   // pitch & putt, disc golf
            return length <= 230f ? 3 : length <= 430f ? 4 : 5;
        }

        /// <summary>Claims the hole corridor (piecewise with its width), its green and the tee boxes.</summary>
        public static void Take(Occupancy occ, GolfHole h, RoutingSpec spec)
        {
            float run = 0f;
            for (int i = 0; i + 1 < h.Line.Length; i++)
            {
                float len = math.distance(h.Line[i], h.Line[i + 1]);
                float hw = HalfWidthAt(run + len * 0.5f, h.Length, spec);
                occ.TakeCorridor(h.Line[i], h.Line[i + 1], hw, CorridorKind);
                run += len;
            }
            occ.Take(h.Green, spec.GreenRadius + 2f, GreenKind);
            occ.Take(h.Tee, 3f, TeeKind);
        }
    }
}
