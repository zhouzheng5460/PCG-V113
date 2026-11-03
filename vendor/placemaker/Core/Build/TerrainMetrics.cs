// Placemaker — glitch metrics for built ground (s30, GPL-3.0).
//
// What the playtests (54–57) called glitches, as numbers:
//  * curtain  — the largest height jump between two neighbouring heightmap texels that the build ADDED (the jump of the
//               final ground minus the jump the original ground already had there). CS2 renders a jump of more than about
//               half a metre over one ~3.5 m texel as a stretched vertical sheet ("torn terrain curtain").
//  * seat     — how badly a net sits on the ground: the height difference across its width (a net flattens the ground
//               under it, so a cross slope becomes a little cliff along its edge) and between the net and the ground.
// Used by the tests, the gallery and the in-game debug dump (same code over live heights). Pure.

using System;
using System.Collections.Generic;
using Unity.Mathematics;

namespace Placemaker.Core.Build
{
    public static class TerrainMetrics
    {
        /// <summary>Default texel: the CS2 playable-area heightmap (14 336 m / 4096 px).</summary>
        public const float Texel = 3.5f;
        /// <summary>A jump the build added above this (m per texel) reads as a curtain in game.</summary>
        public const float CurtainLimit = 0.6f;
        /// <summary>Steeper than this between two texels reads as a vertical sheet in game (m/m).</summary>
        public const float SheetGrade = 0.5f;

        public struct Curtain
        {
            /// <summary>Largest added jump between neighbouring texels (m).</summary>
            public float Max;
            public float2 At;
            /// <summary>Texel pairs the build made steeper than <see cref="SheetGrade"/> (they were not before) by adding more than <see cref="CurtainLimit"/> of jump.</summary>
            public int Count;
            public override string ToString() => $"curtain {Max:0.00} m, {Count} sheets";
        }

        /// <summary>
        /// Curtain metric over [min, max] on a texel grid (both axes). Only pairs where the ground changed are counted, and
        /// the original ground's own jump is subtracted (a natural cliff is not ours).
        /// </summary>
        public static Curtain Curtains(Func<float2, float> final, Func<float2, float> original, float2 min, float2 max, float texel = Texel, Func<float2, bool> mask = null)
        {
            var r = new Curtain();
            int nx = math.max(2, (int)math.ceil((max.x - min.x) / texel) + 1), ny = math.max(2, (int)math.ceil((max.y - min.y) / texel) + 1);
            var f = new float[nx * ny]; var o = new float[nx * ny]; var m = new bool[nx * ny];
            for (int j = 0; j < ny; j++)
                for (int i = 0; i < nx; i++)
                {
                    var p = min + new float2(i, j) * texel;
                    int k = j * nx + i;
                    f[k] = final(p); o[k] = original(p);
                    m[k] = mask == null || mask(p);
                }
            for (int j = 0; j < ny; j++)
                for (int i = 0; i < nx; i++)
                {
                    int k = j * nx + i;
                    if (!m[k]) continue;
                    for (int n = 0; n < 2; n++)
                    {
                        int ii = i + (n == 0 ? 1 : 0), jj = j + (n == 1 ? 1 : 0);
                        if (ii >= nx || jj >= ny) continue;
                        int q = jj * nx + ii;
                        if (!m[q]) continue;
                        if (math.abs(f[k] - o[k]) < 0.05f && math.abs(f[q] - o[q]) < 0.05f) continue;
                        float added = math.abs(f[q] - f[k]) - math.abs(o[q] - o[k]);
                        if (added > r.Max) { r.Max = added; r.At = (min + new float2(i, j) * texel + min + new float2(ii, jj) * texel) * 0.5f; }
                        // A pair the build made steeper than SheetGrade (the original was not): over one texel that renders as a sheet.
                        if (math.abs(f[q] - f[k]) > SheetGrade * texel && math.abs(o[q] - o[k]) <= SheetGrade * texel && added > CurtainLimit) r.Count++;
                    }
                }
            return r;
        }

        public struct Seat
        {
            /// <summary>Largest ground difference across a net (edge to edge, m).</summary>
            public float Cross;
            /// <summary>Largest |net height − ground| along a terrain-following net (m).</summary>
            public float Gap;
            public float2 At;
            public override string ToString() => $"cross {Cross:0.00} m, gap {Gap:0.00} m";
        }

        /// <summary>Net kinds for <see cref="Seats"/>: terrain-following, quay (its land side must meet the ground at deck level), elevated deck (skipped).</summary>
        public const int SeatFollow = 0, SeatQuay = 1, SeatDeck = 2;

        /// <summary>
        /// How well nets sit: for each sample along each net (Bézier A..D), the ground at ±half width across it and under it.
        /// Terrain-following nets: the cross difference and |net − ground|. Decks (FollowTerrain = false): only where they
        /// stand beside ground (a quay's land side): the smaller of the two side gaps to the deck height; over water or a bank
        /// below they stand on pillars or a wall by design.
        /// </summary>
        public static Seat Seats(IEnumerable<(float3 a, float3 b, float3 c, float3 d, float halfWidth, int kind)> nets, Func<float2, float> ground)
        {
            var r = new Seat();
            foreach (var n in nets)
                for (int i = 1; i < 12; i++)
                {
                    float t = i / 12f;
                    var p = Bez(n.a, n.b, n.c, n.d, t);
                    var q = Bez(n.a, n.b, n.c, n.d, math.min(1f, t + 0.02f)) - Bez(n.a, n.b, n.c, n.d, math.max(0f, t - 0.02f));
                    var dir = math.normalizesafe(q.xz, new float2(1, 0));
                    var nrm = new float2(-dir.y, dir.x) * n.halfWidth;
                    float g0 = ground(p.xz), g1 = ground(p.xz + nrm), g2 = ground(p.xz - nrm);
                    if (n.kind == SeatDeck) continue;
                    if (n.kind == SeatQuay)
                    {
                        float side = math.min(math.abs(g1 - p.y), math.abs(g2 - p.y));
                        if (side > r.Gap) { r.Gap = side; r.At = p.xz; }
                        continue;
                    }
                    float cross = math.abs(g1 - g2);
                    if (cross > r.Cross) { r.Cross = cross; r.At = p.xz; }
                    // Height gap only at the control points: between them the build re-fits the course to the ground and splits it where
                    // it deviates (BuildService course fitting), so a long straight path over a kink is not a glitch.
                    float gap = i % 4 == 0 ? math.abs(Bez(n.a, n.b, n.c, n.d, t).y - g0) : 0f;
                    if (gap > r.Gap) { r.Gap = gap; if (gap > r.Cross) r.At = p.xz; }
                }
            return r;
        }

        /// <summary>A mask that is false within <paramref name="margin"/> m of any net's width (the net's own cut or wall covers the ground there).</summary>
        public static Func<float2, bool> OffNets(IEnumerable<(float3 a, float3 b, float3 c, float3 d, float halfWidth, int kind)> nets, float margin = 0.5f)
        {
            var segs = new List<(float2 a, float2 b, float r)>();
            foreach (var n in nets)
            {
                var prev = n.a.xz;
                for (int i = 1; i <= 8; i++) { var q = Bez(n.a, n.b, n.c, n.d, i / 8f).xz; segs.Add((prev, q, n.halfWidth + margin)); prev = q; }
            }
            return p =>
            {
                foreach (var s in segs)
                {
                    var ab = s.b - s.a; float l2 = math.lengthsq(ab);
                    float t = l2 > 1e-6f ? math.saturate(math.dot(p - s.a, ab) / l2) : 0f;
                    if (math.distancesq(p, s.a + ab * t) <= s.r * s.r) return false;
                }
                return true;
            };
        }


        private static float3 Bez(float3 a, float3 b, float3 c, float3 d, float t)
        {
            float u = 1f - t;
            return u * u * u * a + 3f * u * u * t * b + 3f * u * t * t * c + t * t * t * d;
        }
    }
}
