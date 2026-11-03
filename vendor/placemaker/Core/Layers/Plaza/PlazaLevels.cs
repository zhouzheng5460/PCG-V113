// Plaza level variants of round 5 (type wave, builder 11), next to TerrainShaper's terraces /
// sunken / raised (which follow the outline). These are architectural: the levels are laid out on
// the plaza's own axes, so the step lines are straight and the stairs line up.
//
//   split        two halves along the long axis, one up and one down: a split-level square
//   cascade      2–4 levels stepping along the long axis (a square on a slope, Spanish-steps style)
//   podium       a raised rectangular platform in the middle (a statue / bandstand plinth)
//   sunkenCourt  a lowered rectangular court in the middle (a sunken garden / cafe court)
//
// Same rules as TerrainShaper (playtest 2, item 6): square cells of ~brush size, every change
// blended back to the ORIGINAL ground at the outline over `falloff` metres, the step between two
// neighbouring cells limited to the max slope, permanent and warned. Step lines get planters
// (with gaps at the stairs); stairs are short wide path flights across each step line, placed off
// the centre line so the centrepiece keeps the middle.

using System;
using System.Collections.Generic;
using Placemaker.Core.Geometry;
using Placemaker.Core.Layers.Common;
using Unity.Mathematics;

namespace Placemaker.Core.Layers.Plaza
{
    internal static class PlazaLevels
    {
        public static readonly string[] Modes = { "split", "cascade", "podium", "sunkenCourt" };

        public static bool Handles(string mode) => Array.IndexOf(Modes, mode) >= 0;

        /// <summary>Shapes the plaza and adds stairs and step planters. Rectangles in <paramref name="keepLevel"/> (amenities) sit on one level.</summary>
        public static int Shape(LayerContext ctx, Occupancy occ, LayerRegion region, string mode, IReadOnlyList<float2[]> keepLevel, NetRole flight = NetRole.PathwayWide)
        {
            // Art pass a2: the square's own edge axis (a square's principal axes are diagonal), so steps run with the paving.
            LeisureKit.Frame(region, out var centre, out _, out _, out _, out _);
            var axis = PlazaDressing.SquareAxis(region);
            var side = new float2(-axis.y, axis.x);
            float minA = float.MaxValue, maxA = float.MinValue, minB = float.MaxValue, maxB = float.MinValue;
            foreach (var v in region.Ring)
            {
                var d = v - centre;
                minA = math.min(minA, math.dot(d, axis)); maxA = math.max(maxA, math.dot(d, axis));
                minB = math.min(minB, math.dot(d, side)); maxB = math.max(maxB, math.dot(d, side));
            }
            float spanA = maxA - minA, spanB = maxB - minB;
            if (spanA < 24f || spanB < 16f) { ctx.Issue(IssueSeverity.Info, "Placemaker.Plaza.Issue.TooSmallForLevels"); return 0; }
            region.DeepestPoint(out float inscribed, null, 12);
            float falloff = math.clamp(inscribed * 0.25f, 4f, 14f);
            float h = math.max(0.5f, ctx.F("levelHeight"));
            int levels = mode == "cascade" ? math.clamp(ctx.I("levelCount"), 2, 4) : 2;
            float midA = (minA + maxA) * 0.5f, midB = (minB + maxB) * 0.5f;
            // Inner rectangle (podium / sunken court): 56 % of each span, centred on the frame box.
            float ia = spanA * 0.28f, ib = spanB * 0.28f;

            float LevelAt(float u, float v)
            {
                switch (mode)
                {
                    case "split": return u >= midA ? 0.5f * h : -0.5f * h;
                    case "cascade":
                    {
                        int k = math.clamp((int)math.floor((u - minA) / (spanA / levels)), 0, levels - 1);
                        return (k - (levels - 1) * 0.5f) * h;
                    }
                    case "podium": return math.abs(u - midA) <= ia && math.abs(v - midB) <= ib ? h : 0f;
                    default: return math.abs(u - midA) <= ia && math.abs(v - midB) <= ib ? -h : 0f;
                }
            }

            float2 Local(float2 p) => new float2(math.dot(p - centre, axis), math.dot(p - centre, side));
            float mean = TerrainShaper.MeanHeight(ctx, region);
            Func<float2, float> target = p =>
            {
                // An amenity keeps the level under its centre over its whole footprint (no step through a court).
                if (keepLevel != null)
                    foreach (var r in keepLevel)
                        if (Poly.Contains(r, p) || ParkSiteDistance(r, p) < 2f)
                        {
                            var lc = Local(Poly.Centroid(r));
                            return mean + LevelAt(lc.x, lc.y);
                        }
                var l = Local(p);
                return mean + LevelAt(l.x, l.y);
            };
            int cells = Emit(ctx, region, target, falloff, 35f);
            if (cells == 0) return 0;

            // Step lines (local segments) and the stair flights across them.
            var steps = new List<(float2 a, float2 b)>();
            switch (mode)
            {
                case "split": steps.Add((new float2(midA, minB), new float2(midA, maxB))); break;
                case "cascade":
                    for (int k = 1; k < levels; k++) { float u = minA + spanA * k / levels; steps.Add((new float2(u, minB), new float2(u, maxB))); }
                    break;
                default:
                    steps.Add((new float2(midA - ia, midB - ib), new float2(midA + ia, midB - ib)));
                    steps.Add((new float2(midA + ia, midB - ib), new float2(midA + ia, midB + ib)));
                    steps.Add((new float2(midA + ia, midB + ib), new float2(midA - ia, midB + ib)));
                    steps.Add((new float2(midA - ia, midB + ib), new float2(midA - ia, midB - ib)));
                    break;
            }
            float2 World(float2 l) => centre + axis * l.x + side * l.y;
            var flights = new List<(float2 a, float2 b)>();
            var ramp = AssetMix.One("");
            if (mode == "split" || mode == "cascade")
            {
                // Two flights per step line, a quarter of the width off the centre line.
                foreach (var (a, b) in steps)
                    foreach (float f in new[] { -0.25f, 0.25f })
                    {
                        float v = midB + spanB * f;
                        var p0 = World(new float2(a.x - 3.5f, v)); var p1 = World(new float2(a.x + 3.5f, v));
                        if (region.ContainsDisc(p0, 2f) && region.ContainsDisc(p1, 2f)) flights.Add((p0, p1));
                    }
            }
            else
            {
                // One flight in the middle of each side of the platform / court.
                foreach (var (a, b) in steps)
                {
                    var m = (a + b) * 0.5f;
                    var outward = math.normalizesafe(m - new float2(midA, midB));
                    var p0 = World(m - outward * 3f); var p1 = World(m + outward * 4f);
                    if (region.ContainsDisc(p0, 2f) && region.ContainsDisc(p1, 2f)) flights.Add((p0, p1));
                }
            }
            foreach (var (a, b) in flights)
                if (!LeisureKit.Crosses(occ, a, b, Composer.DefaultHalfWidth(flight))) LeisureKit.Path(ctx, occ, a, b, flight, ramp);   // s18: never through the focal pad   // s18: small squares use narrow flights
            ctx.Plan.Facts["levelFlights"] = flights.Count;
            ctx.Plan.Facts["levelSteps"] = steps.Count;

            if (!ctx.B("levelPlanters")) return cells;
            // Art pass a2: low green bushes read as a balustrade (designed default, not the planter pool),
            // and the middle of the square stays clear for its centrepiece.
            float keepClear = math.max(PlazaLayer.FountainClearRadius, ctx.F("centerSize") + 4f) + 5f;
            var squareCentre = region.Centroid;
            int placed = 0;
            foreach (var (a, b) in steps)
            {
                var wa = World(a); var wb = World(b);
                // Planters sit on the upper side of the line, 1 m back: they read as the balustrade.
                float len = math.distance(wa, wb);
                var dir = (wb - wa) / math.max(1e-3f, len);
                for (float s = 2f; s < len - 1f; s += 4.5f)
                {
                    var p = wa + dir * s;
                    if (!region.ContainsDisc(p, falloff * 0.5f) || math.distance(p, squareCentre) < keepClear) continue;
                    bool nearFlight = false;
                    foreach (var (fa, fb) in flights) if (SegmentIndex.DistSq(p, fa, fb) < 16f) { nearFlight = true; break; }
                    if (nearFlight) continue;
                    uint v = Hashing.Mix(ctx.Seed, 0x1E50u + (uint)placed);
                    if (LeisureKit.Put(ctx, occ, p, math.atan2(dir.x, dir.y), ObjectRole.Bush, PlazaArt.Or(ctx, "planterAsset", PlazaArt.GreenBushes, v), AssetCategories.Bush, 0.8f, v, 0.5f, 0.4f))
                        placed++;
                }
            }
            ctx.Plan.Facts["levelPlanters"] = placed;
            return cells;
        }

        private static float ParkSiteDistance(float2[] ring, float2 p)
        {
            float best = float.MaxValue;
            for (int i = 0; i < ring.Length; i++) best = math.min(best, SegmentIndex.DistSq(p, ring[i], ring[(i + 1) % ring.Length]));
            return math.sqrt(best);
        }

        /// <summary>
        /// Emits level cells for an absolute target height field: blended to the original ground at the
        /// outline, slope-limited between neighbours, interior only. Returns the number of cells.
        /// </summary>
        public static int Emit(LayerContext ctx, LayerRegion region, Func<float2, float> target, float falloff, float maxSlopePct)
        {
            float cell = math.max(TerrainShaper.DefaultCell * 0.75f, math.sqrt(region.Area / TerrainShaper.MaxCells));
            int nx = math.max(1, (int)math.ceil((region.Max.x - region.Min.x) / cell));
            int ny = math.max(1, (int)math.ceil((region.Max.y - region.Min.y) / cell));
            var offset = new float[nx * ny];
            var baseH = new float[nx * ny];
            var dist = new float[nx * ny];
            var inside = new bool[nx * ny];
            region.DeepestPoint(out float inscribed, null, 12);
            falloff = math.max(cell, falloff);
            for (int iy = 0; iy < ny; iy++)
            {
                if ((iy & 15) == 0) ctx.Check();
                for (int ix = 0; ix < nx; ix++)
                {
                    int i = iy * nx + ix;
                    var c = region.Min + (new float2(ix, iy) + 0.5f) * cell;
                    if (!region.Contains(c)) continue;
                    inside[i] = true;
                    float h0 = ctx.H(c);
                    float d = region.BoundaryDistance(c, inscribed + 1f);
                    dist[i] = d;
                    float t = math.saturate(d / falloff);
                    float blend = t * t * (3f - 2f * t);
                    baseH[i] = h0;
                    offset[i] = (target(c) - h0) * blend;
                }
            }
            float slope = math.max(0.05f, maxSlopePct / 100f);
            float maxStep = slope * cell;
            for (int i = 0; i < offset.Length; i++)
                if (inside[i]) offset[i] = math.clamp(offset[i], -slope * dist[i], slope * dist[i]);
            for (int pass = 0; pass < 8; pass++)
            {
                bool changed = false;
                for (int iy = 0; iy < ny; iy++)
                    for (int ix = 0; ix < nx; ix++)
                    {
                        int i = iy * nx + ix;
                        if (!inside[i]) continue;
                        float t = baseH[i] + offset[i];
                        for (int k = 0; k < 4; k++)
                        {
                            int jx = ix + (k == 0 ? 1 : k == 1 ? -1 : 0), jy = iy + (k == 2 ? 1 : k == 3 ? -1 : 0);
                            if (jx < 0 || jy < 0 || jx >= nx || jy >= ny) continue;
                            int j = jy * nx + jx;
                            if (!inside[j]) continue;
                            float tj = baseH[j] + offset[j];
                            if (math.abs(t - tj) <= maxStep || math.abs(t - baseH[i]) <= math.abs(offset[j])) continue;
                            t = tj + math.sign(t - tj) * maxStep;
                            changed = true;
                        }
                        offset[i] = t - baseH[i];
                    }
                if (!changed) break;
            }
            int cells = 0;
            float maxO = 0f, minO = 0f;
            for (int iy = 0; iy < ny; iy++)
                for (int ix = 0; ix < nx; ix++)
                {
                    int i = iy * nx + ix;
                    if (!inside[i] || math.abs(offset[i]) < 0.2f) continue;
                    var c = region.Min + (new float2(ix, iy) + 0.5f) * cell;
                    var square = LayerRegion.Rect(c, new float2(1, 0), cell * 0.5f, cell * 0.5f);
                    float2[] ring = square;
                    bool whole = true;
                    foreach (var q in square) if (!region.Contains(q)) { whole = false; break; }
                    if (!whole)
                    {
                        var pieces = Poly.Intersection(square, region.Ring);
                        if (pieces.Count == 0 || pieces[0].Outer == null || pieces[0].Outer.Length < 3) continue;
                        ring = pieces[0].Outer;
                    }
                    ctx.Plan.Terraform.Add(new PlannedTerraform
                    {
                        Ring = Poly.To3D(ring, ctx.H), Kind = offset[i] > 0f ? TerraformKind.Raise : TerraformKind.Lower,
                        TargetHeight = baseH[i] + offset[i], Permanent = true,
                    });
                    maxO = math.max(maxO, offset[i]); minO = math.min(minO, offset[i]);
                    cells++;
                }
            if (cells > 0)
            {
                ctx.Issue(IssueSeverity.Warning, TerrainShaper.PermanentKey);
                ctx.Plan.Facts["terrainCells"] = cells;
                ctx.Plan.Facts["terrainMaxRaise"] = maxO;
                ctx.Plan.Facts["terrainMaxLower"] = -minO;
            }
            return cells;
        }
    }
}
