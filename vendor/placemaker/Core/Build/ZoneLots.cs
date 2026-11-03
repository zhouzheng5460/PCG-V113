// Placemaker — zoning-first shop plots (s40).
//
// Playtest s40 (image 70): "There's a mismatch between what gets planned and where the zones actually are. Shouldn't it
// be snug against where it knows it can zone?" Hosts planned shop plots as free rectangles along the frontage; the
// zone cells the game gives a road sit on that road's own 8 m grid, so ZoneGuard zoned only the cells whose centres
// fell in a plot: cells covered part of a plot, poked out of others, some plots got none — paving notches, grass
// leftovers and gaps around the buildings that grew.
//
// Now the plots ARE the cells: the layouts still choose WHERE shops go (amount, pattern, depth, entrance gaps), then
// every candidate plot is replaced by the exact cells ZoneGuard will zone for it — the same computation (captured
// blocks of existing roads, predicted blocks of planned lanes, the game's depth rule, ≥ 2 columns) — as cell-aligned
// rectangles flush with the curb. The host plans around exactly those cells; ZoneGuard's later snap finds them as
// they are.

using System;
using System.Collections.Generic;
using Placemaker.Core.Geometry;
using Unity.Mathematics;

namespace Placemaker.Core.Build
{
    public static class ZoneLots
    {
        /// <summary>
        /// Region tag of a plot that IS cells (snapped by ZoneLots): the edgeZoning planner zones it exactly (its rect
        /// shrunk QuadShrink, the Marquee rule takes precisely these cells) instead of re-deriving quads from its edges.
        /// </summary>
        public const string CellsTag = "zoneCells";

        /// <summary>The zoning quad of a cell plot: the cell rectangle shrunk by ZoneGuard.QuadShrink (CCW).</summary>
        public static float2[] ZoningQuad(float2[] cells)
        {
            var r = Poly.EnsureCcw((float2[])cells.Clone());
            var o = new float2[r.Length];
            for (int i = 0; i < r.Length; i++)
            {
                var e1 = math.normalizesafe(r[(i + 1) % r.Length] - r[i]);
                var e0 = math.normalizesafe(r[i] - r[(i + r.Length - 1) % r.Length]);
                o[i] = r[i] + e1 * ZoneGuard.QuadShrink - e0 * ZoneGuard.QuadShrink;
            }
            return o;
        }

        /// <summary>
        /// The zone blocks the build's zoning will meet: the captured blocks of existing roads, the predicted blocks of
        /// the roads in <paramref name="planned"/> (lanes this build adds) and — only when nothing was captured at all
        /// (tests, gallery, a city without blocks loaded) — the predicted blocks of the existing roads around.
        /// </summary>
        public static List<ZoneGrid> SiteGrids(SiteContext site, ComposedPlan planned)
        {
            var grids = new List<ZoneGrid>();
            if (site?.ZoneGrids != null) grids.AddRange(site.ZoneGrids);
            bool captured = grids.Count > 0;
            if (planned != null) grids.AddRange(ZoneGuard.PredictedGrids(planned, site));
            if (!captured && site?.NearbyNets != null)
                foreach (var n in site.NearbyNets)
                {
                    if (n.Category != NetCategory.Road || !(n.Width > 0.5f)) continue;
                    foreach (var g in ZoneBlockPredictor.Blocks(n.A, n.B, n.C, n.D, n.Width))
                    {
                        var p = g;
                        p.Predicted = true;
                        p.Existing = true;
                        grids.Add(p);
                    }
                }
            return grids;
        }

        /// <summary>
        /// The plots that will really zone: for every candidate plot the cell rectangles (whole cells of one block,
        /// curb-flush, rows from row 0) ZoneGuard zones for it, all candidates computed together (dedupe, widening,
        /// depth rule). Index i of the result = candidate i (an empty list: no cell of it can be zoned).
        /// </summary>
        public static List<List<float2[]>> Snap(IReadOnlyList<float2[]> footprints, IReadOnlyList<float2[]> plots, IReadOnlyList<ZoneGrid> grids,
            IReadOnlyList<NearbyNet> nets, IReadOnlyList<(float2[] line, float half)> own, IReadOnlyList<float2[]> taken = null)
        {
            var result = new List<List<float2[]>>();
            for (int i = 0; i < (plots?.Count ?? 0); i++) result.Add(new List<float2[]>());
            if (plots == null || plots.Count == 0 || grids == null || grids.Count == 0) return result;
            var ids = new List<string>();
            foreach (var _ in plots) ids.Add("z");
            ZoneGuard.Result r;
            // Land given to something else ends a column like a building lot does.
            try { r = ZoneGuard.Compute(footprints, plots, ids, taken, grids, nets, true, own); }
            catch (Exception) { return null; }
            foreach (var zr in r.Zoning)
                if (zr.Quad >= 0 && zr.Quad < result.Count) result[zr.Quad].Add(Poly.EnsureCcw(Unshrink(zr.Corners)));
            return result;
        }

        /// <summary>Host content keeps this far off the cells of a plot (m): flush, just enough that a path edge never touches a cell (s48; was 2.5).</summary>
        public const float Clearance = 0.2f;

        /// <summary>A convex ring grown outward by <paramref name="by"/> (corners moved along both edges).</summary>
        public static float2[] Grow(float2[] ring, float by)
        {
            var r = Poly.EnsureCcw((float2[])ring.Clone());
            var o = new float2[r.Length];
            for (int i = 0; i < r.Length; i++)
            {
                var e1 = math.normalizesafe(r[(i + 1) % r.Length] - r[i]);
                var e0 = math.normalizesafe(r[i] - r[(i + r.Length - 1) % r.Length]);
                o[i] = r[i] - e1 * by + e0 * by;
            }
            return o;
        }

        /// <summary>The exact cell rectangle of a ZoneGuard zoning rect (those are shrunk by QuadShrink for the Marquee).</summary>
        public static float2[] Unshrink(float2[] rect)
        {
            var r = new float2[rect.Length];
            for (int i = 0; i < rect.Length; i++)
            {
                var e1 = math.normalizesafe(rect[(i + 1) % rect.Length] - rect[i]);
                var e0 = math.normalizesafe(rect[i] - rect[(i + rect.Length - 1) % rect.Length]);
                r[i] = rect[i] - e1 * ZoneGuard.QuadShrink + e0 * ZoneGuard.QuadShrink;
            }
            return r;
        }
    }
}
