// Cliff-top walk (s20): what a water type builds where the shore is a cliff (playtest 33: a promenade and a
// terraform grid laid across a rock face). The cliff itself stays natural; the walk runs on the plateau, set back
// to where the ground flattens, on a level bench; railings only where the ground really drops beside it; lookouts
// (a small paved landing on the seaward side with benches facing the sea and a railing along its edge) at intervals.

using System;
using System.Collections.Generic;
using Placemaker.Core.Geometry;
using Placemaker.Core.Layers.Common;
using Placemaker.Core.Layers.Park;
using Unity.Mathematics;

namespace Placemaker.Core.Layers.Types.Beach
{
    public sealed class CliffTopResult
    {
        public readonly List<float2[]> Lines = new List<float2[]>();
        public int Lookouts, Railings, Benches;
    }

    public static class CliffTop
    {
        /// <summary>Railings where the ground beside a walk drops by more than this within 3 m (m).</summary>
        public const float DropForRailing = 1.0f;

        /// <summary>Distance from the water to where the plateau starts (ground flatter than 15 % for 6 m), over [from, to].</summary>
        public static float Setback(ShoreGround g, float from, float to)
        {
            var shore = g.Shore;
            var list = new List<float>();
            for (float s = from; s <= to; s += 6f)
            {
                var w = shore.WaterAt(s, out var inw);
                float found = float.NaN, run = 0f;
                for (float d = 4f; d <= 80f; d += 2f)
                {
                    float grade = g.Survey.GradeAt(w + inw * d);
                    run = grade < 0.15f ? run + 2f : 0f;
                    if (run >= 6f) { found = d - 6f; break; }
                }
                if (math.isfinite(found)) list.Add(found);
            }
            if (list.Count == 0) return 30f;
            list.Sort();
            return list[(int)(list.Count * 0.75f)];
        }

        /// <summary>
        /// The cliff-top walk between arc lengths [from, to]: a level bench walk set back from the edge, railings along real
        /// drops, lookouts every ~<paramref name="lookoutSpacing"/> m. Furniture goes through <paramref name="occ"/>.
        /// </summary>
        public static CliffTopResult Plan(LayerContext ctx, ShoreGround g, Occupancy occ, float from, float to, NetRole role, AssetRef path,
            AssetRef bench, AssetRef railing, AssetRef lamp, AssetRef paving, float lookoutSpacing)
        {
            var res = new CliffTopResult();
            var shore = g.Shore;
            float hw = Composer.DefaultHalfWidth(role);
            // Back from the edge far enough for lookout landings between the cliff top and the walk.
            float set = math.max(Setback(g, from, to), g.CliffBand) + hw + 4.5f;
            // The walk: stations of this run only, on the plateau.
            foreach (var raw in shore.OffsetFromWater(set, hw + 1f, 16f, 3f, p =>
            {
                shore.Nearest(p, out _, out float a);
                return a < from - 1f || a > to + 1f || g.Survey.GradeAt(p) > 0.12f;   // on the flat plateau only
            }))
            foreach (var (line, _) in g.Walk(raw, hw + 0.8f, 4f, 10f, TerrainSurvey.WalkGrade, 1.5f, 16f, cliffTop: true))
            {
                var curves = Shoreline.Curves(shore.Land, line, role);
                if (curves.Count == 0) continue;
                ParkPaths.Index(curves, occ.Corridors, 100);
                ParkPaths.Index(curves, occ.Paths, 100);
                ParkPaths.Emit(ctx, curves, new List<Gate>(), path);
                res.Lines.Add(line);
                float len = Shoreline.LengthOf(line);
                // Lookouts first (the railing along the walk stops at them).
                var lookouts = new List<float2[]>();
                int n = math.max(1, (int)math.floor(len / math.max(30f, lookoutSpacing)));
                for (int k = 0; k < n; k++)
                {
                    float s = (k + 0.5f) * len / n;
                    var p = Shoreline.PointOn(line, s, out var t);
                    var sea = new float2(t.y, -t.x);
                    if (shore.FromWater(p + sea) > shore.FromWater(p)) sea = -sea;
                    // The landing touches the walk's sea-side edge; it must sit on the cliff top, not the face.
                    var c = p + sea * (hw + 0.3f + 1.9f);
                    var rect = new OrientedRect { Center = c, Axis = t, HalfLength = 3.2f, HalfWidth = 1.9f };
                    if (!LeisureKit.RectFits(shore.Land, occ, rect, 0.3f, 0.1f) || g.OnCliff(c) || g.OnCliff(c + sea * 1.9f)) continue;
                    if (TerrainSurvey.Relief(g.H, c, 2.4f) > 1.6f) continue;   // level at the walk's own height below, not a mean that reaches over the edge
                    g.Sculpt.Pad(rect.Ring, g.H(p), 2.5f);
                    if (!ctx.Surface(rect.Ring, SurfaceRole.Paving, paving, AssetCategories.SurfacePaving)) continue;
                    LeisureKit.TakeRect(occ, rect);
                    lookouts.Add(rect.Ring);
                    foreach (float side in new[] { -1.4f, 1.4f })
                        ctx.Object(c + t * side + sea * 0.6f, math.atan2(sea.x, sea.y), ObjectRole.Bench, bench, Hashing.Mix(ctx.Seed, 0xC11Fu + (uint)(k * 7) + (uint)(side > 0 ? 1 : 0)));
                    res.Benches += 2;
                    // A railing along the landing's sea edge only where the ground beyond it really drops.
                    var a = c + sea * 2.2f - t * 3f; var b = c + sea * 2.2f + t * 3f;
                    if (g.H(c + sea * 4.5f) < g.H(c + sea * 2.2f) - DropForRailing)
                    {
                        ctx.StraightNet(a, b, NetRole.Fence, railing);
                        res.Railings++;
                    }
                    LeisureKit.Put(ctx, occ, p - sea * (hw + 1f), 0f, ObjectRole.Lamp, lamp, null, 0.4f, Hashing.Mix(ctx.Seed, 0xC1A0u + (uint)k), 0.3f, 0.1f);
                    res.Lookouts++;
                }
                // Railing along the sea side where the ground drops (the cliff edge comes close): s30 — ONE continuous chain per
                // stretch of drop (dips ≤ 12 m bridged, stretches < 10 m left out, equal ~6 m pieces sharing their nodes), open
                // only at the lookouts (they carry their own railing). It was a scatter of short pieces at different angles.
                var qs = new List<float2>(); var flag = new List<bool>(); var openAt = new List<bool>();
                for (float s = 0f; s <= len; s += 2f)
                {
                    var p = Shoreline.PointOn(line, s, out var t);
                    var sea = new float2(t.y, -t.x);   // land on the left of a chain-ordered line → sea on the right
                    if (shore.FromWater(p + sea) > shore.FromWater(p)) sea = -sea;
                    var q = p + sea * (hw + 0.9f);
                    bool atLookout = false;
                    foreach (var lr in lookouts) if (Poly.Contains(lr, q) || Poly.DistanceToBoundary(lr, q) < 0.6f) { atLookout = true; break; }
                    qs.Add(q);
                    openAt.Add(atLookout);
                    flag.Add(g.H(q + sea * 3f) < g.H(q) - DropForRailing && !atLookout && shore.Land.ContainsDisc(q, 0.3f));
                }
                for (int i = 0, j; i < flag.Count; i = j)
                {
                    j = i + 1;
                    if (flag[i]) continue;
                    while (j < flag.Count && !flag[j]) j++;
                    bool inner = i > 0 && j < flag.Count, open = false;
                    for (int k = i; k < j; k++) open |= openAt[k];
                    if (inner && !open && (j - i) * 2f <= 12f) for (int k = i; k < j; k++) flag[k] = true;
                }
                for (int i = 0, j; i < flag.Count; i = j)
                {
                    j = i + 1;
                    if (!flag[i]) continue;
                    while (j < flag.Count && flag[j]) j++;
                    if ((j - 1 - i) * 2f < 10f) continue;
                    var chain = Poly.ResamplePolyline(qs.GetRange(i, j - i), 6f);
                    for (int k = 1; k < chain.Length; k++)
                    {
                        ctx.StraightNet(chain[k - 1], chain[k], NetRole.Fence, railing);
                        occ.TakeCorridor(chain[k - 1], chain[k], 0.3f, 5);
                        res.Railings++;
                    }
                }
            }
            return res;
        }
    }
}
