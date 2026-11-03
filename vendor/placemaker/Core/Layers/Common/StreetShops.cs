// "Street shops" layouts (s12): how a host (park, plaza, market square, leisure types…) gives up land
// for zoned shops along its road frontage, and how the host's own layout weaves around them.
//
// CS2 rules this respects (RESEARCH-NOTES, Parking/Zoning.cs): zone cells are 8 m squares in blocks up
// to 6 cells deep that belong to a ROAD; only cells next to a road carry buildings. So every shop piece
// here is whole cells deep and touches either an existing street (frontage) or an alley this layout
// plans itself (vanilla "Alley", 8 m, which carries zone blocks — PLT built its zoning road from it).
// The edgeZoning feature zones each "shops" region from whichever of its edges faces a road or alley.
//
// Layouts (control "shopLayout"; "band" and "pads" are the pre-s12 values and keep their ids):
//   band     Row       continuous shops along the street (entrance gaps kept)
//   pads     Speckled  small scattered shop pads with lawn between them: the park reaches the street
//   clusters Clusters  bigger shop blocks of uneven depth, wide green fingers between them
//   inward   Facing in shops back onto the street and face an alley that loops behind them (U-shaped,
//                      both ends on the street); the park lies beyond the alley
//   lane     Alley     a dead-end alley runs from the street deep into the area, shops on both sides;
//                      a park path continues from its end (synthetic entrance) so it reads as a lane through
// Amount (control "shopAmount"): none · few · some · lots. Depth: "shopDepth" (8–48 m).
//
// The host plans on (outline − everything carved here): paths, trees and entrances fill the gaps
// between the shops instead of sitting behind a carved strip.

using System;
using System.Collections.Generic;
using Placemaker.Core.Geometry;
using Unity.Mathematics;
using Random = Unity.Mathematics.Random;

namespace Placemaker.Core.Layers.Common
{
    public static class StreetShops
    {
        public const string LayoutId = "shopLayout", AmountId = "shopAmount", DepthId = "shopDepth";
        public const string Row = "band", Speckled = "pads", Clusters = "clusters", Inward = "inward", Lane = "lane";
        public static readonly string[] Layouts = { Row, Speckled, Clusters, Inward, Lane };
        /// <summary>
        /// s14: the Zoning feature on parks/plazas is plain cutouts — Row or Speckled only (user: "literally cutouts for
        /// zoning, no fanciness"). The other layouts live on in the Commercial "Shops & park" type.
        /// </summary>
        public static readonly string[] FeatureLayouts = { Row, Speckled };
        /// <summary>Saved feature layouts from s12 (clusters, facing in, alley) and unknown values plan as Row.</summary>
        public static string FeatureLayout(string layout) => Array.IndexOf(FeatureLayouts, layout) >= 0 ? layout : Row;
        public const string None = "none", Few = "few", Some = "some", Lots = "lots";
        public static readonly string[] Amounts = { None, Few, Some, Lots };

        /// <summary>Vanilla two-way alley (8 m, zone blocks on both sides); the smallest street when missing.</summary>
        public const string AlleyAsset = "Alley";
        public const string AlleyFallback = "Small Road";
        /// <summary>Real half width of the vanilla alley (netWidth 8 m).</summary>
        public const float AlleyHalf = 4f;
        private const float Cell = StreetFrontage.Cell;

        public sealed class AlleyPath
        {
            /// <summary>Polyline: [0] on the street centre line (attached), then corners; or an inner dead end last.</summary>
            public List<float2> Points = new List<float2>();
            public long StartAttach = -1, EndAttach = -1;
            public float StartT, EndT;
        }

        public sealed class Result
        {
            /// <summary>Shop regions (CCW quads, whole cells deep) for the edgeZoning feature.</summary>
            public readonly List<float2[]> Shops = new List<float2[]>();
            /// <summary>What the host gives up (shops + alleys), extended 1 m past the outline where it touches it.</summary>
            public readonly List<float2[]> Carve = new List<float2[]>();
            public readonly List<AlleyPath> Alleys = new List<AlleyPath>();
            /// <summary>Points on the carved outline where a park path should meet the alley (lane ends).</summary>
            public readonly List<float2> Doors = new List<float2>();
            /// <summary>The layout fell back to a row (no street the alley could join).</summary>
            public bool FellBack;
            /// <summary>(s40) The shops are cell plots (snapped by Build/ZoneLots): tag them Build.ZoneLots.CellsTag.</summary>
            public bool CellPlots;
        }

        /// <summary>
        /// Per-type defaults (docs/proposals/s12.md has the audit of every type): urban, people-facing places
        /// start with shops on; nature, memorial, sport-only, industrial and farm types keep them off.
        /// Types not listed: feature off, row, "some".
        /// </summary>
        public static readonly Dictionary<string, (bool on, string layout, string amount)> Defaults =
            new Dictionary<string, (bool, string, string)>(StringComparer.Ordinal)
            {
                // Squares & events (rf-squares, docs/refactor/squares.md): off by default. A square or venue is not a shop row:
                // the cutouts left empty grass where a plot did not zone, took the edge the trees and lawns need, and the
                // shops grew facing the city street with their backs to the square. The player can still turn them on.
                { "plaza", (false, Row, Some) },
                { "marketSquare", (false, Row, Some) },
                { "park", (false, Speckled, Few) },   // rf-parks: shop plots left pale holes round a natural or garden park; the urban pocket turns them on
                { "waterfront", (true, Row, Some) },
                { "fairground", (false, Row, Few) },
                { "amphitheatre", (false, Speckled, Few) },
                { "greenwayPark", (true, Speckled, Few) },
                { "sportsPark", (false, Speckled, Few) },
                { "formalGarden", (false, Row, Some) },
                { "botanicalGarden", (false, Row, Few) },
                { "picnicGrounds", (false, Speckled, Few) },
                { "dogPark", (false, Speckled, Few) },
                { "communityGarden", (false, Speckled, Few) },
                { "adventurePlayground", (false, Speckled, Few) },
                { "abandonedLot", (false, Row, Some) },
                { "urbanRuins", (false, Speckled, Few) },
                { "urbanFarm", (false, Speckled, Few) },
                { "campground", (false, Row, Few) },
            };

        public static (bool on, string layout, string amount) DefaultFor(string layerId) =>
            layerId != null && Defaults.TryGetValue(layerId, out var d) ? d : (false, Row, Some);

        /// <summary>Old saves / unknown values: anything not a known layout plans as a row.</summary>
        public static string NormalizeLayout(string layout) => Array.IndexOf(Layouts, layout) >= 0 ? layout : Row;
        public static string NormalizeAmount(string amount) => Array.IndexOf(Amounts, amount) >= 0 ? amount : Some;

        public struct Run
        {
            public float2 A, Dir, In;
            public float From, To;
            /// <summary>The run starts / ends at a corner where another frontage run continues (a street corner).</summary>
            public bool StartCorner, EndCorner;
            /// <summary>Direction and inward normal of the run continuing after EndCorner.</summary>
            public float2 NextDir, NextIn;
            public float Length => To - From;
            public float2 At(float s, float t) => A + Dir * s + In * t;
        }

        public static Result Plan(LayerRegion region, AreaInput input, IReadOnlyList<float2> gates, string layout, string amount, float depth, uint seed, bool follow = false)
        {
            var res = new Result();
            amount = NormalizeAmount(amount);
            if (region == null || amount == None) return res;
            layout = NormalizeLayout(layout);
            var roads = StreetFrontage.Roads(input);
            if (roads.Count == 0) return res;
            int cells = math.clamp((int)math.floor(depth / Cell + 0.01f), 1, 6);
            var runs = Runs(region, roads, gates, input.Site?.WaterDepthAt, follow);
            if (runs.Count == 0) return res;
            var rng = new Random(seed == 0 ? 1u : seed);
            _water = input.Site?.WaterDepthAt;
            try { Layout(region, input, runs, layout, amount, cells, ref rng, res); }
            finally { _water = null; }
            SnapToCells(region, input, layout, res);
            return res;
        }

        /// <summary>
        /// (s40) Zoning first: every shop plot becomes the exact cells the build will zone for it (Build/ZoneLots: the
        /// roads' real or predicted 8 m grids, the game's depth rule, ≥ 2 columns), as cell rectangles flush with the
        /// curb; a plot no cell of which can be zoned is given back to the host. The carve follows, so the host plans
        /// around exactly the cells that will zone (playtest: paving notches and grass leftovers around the shops).
        /// </summary>
        private static void SnapToCells(LayerRegion region, AreaInput input, string layout, Result res)
        {
            if (res.Shops.Count == 0 || !Build.ZoneGuard.Enabled) return;
            var planned = new ComposedPlan();
            var lanes = new AreaPlan();
            foreach (var alley in res.Alleys)
                for (int i = 0; i + 1 < alley.Points.Count; i++)
                {
                    float2 a = alley.Points[i], b = alley.Points[i + 1];
                    if (math.distance(a, b) < 0.3f) continue;
                    lanes.Nets.Add(new PlannedNet
                    {
                        A = new float3(a.x, 0, a.y), B = new float3(math.lerp(a, b, 1f / 3f).x, 0, math.lerp(a, b, 1f / 3f).y),
                        C = new float3(math.lerp(a, b, 2f / 3f).x, 0, math.lerp(a, b, 2f / 3f).y), D = new float3(b.x, 0, b.y),
                        Role = NetRole.Alley, Asset = new AssetRef(AlleyAsset, AlleyFallback),
                    });
                }
            planned.Layers.Add(lanes);
            var grids = Build.ZoneLots.SiteGrids(input.Site, planned);
            if (grids.Count == 0) return;
            var snapped = Build.ZoneLots.Snap(new[] { region.Ring }, res.Shops, grids, input.Site?.NearbyNets, Build.ZoneGuard.OwnNets(planned, input.Site));
            if (snapped == null) return;
            var shops = new List<float2[]>();
            foreach (var list in snapped) shops.AddRange(list);
            bool rebuild = layout == Row || layout == Speckled || layout == Clusters;
            res.Shops.Clear();
            res.Shops.AddRange(shops);
            res.CellPlots = true;
            if (rebuild) res.Carve.Clear();
            var roads = new List<(float2 a, float2 b, float half)>();
            if (input.Site?.NearbyNets != null)
                foreach (var n in input.Site.NearbyNets)
                    if (n.Category == NetCategory.Road)
                        for (int i = 0; i < 8; i++)
                            roads.Add((Composer.Bezier(n.A, n.B, n.C, n.D, i / 8f).xz, Composer.Bezier(n.A, n.B, n.C, n.D, (i + 1) / 8f).xz, n.Width * 0.5f));
            foreach (var alley in res.Alleys)
                for (int i = 0; i + 1 < alley.Points.Count; i++) roads.Add((alley.Points[i], alley.Points[i + 1], AlleyHalf));
            // The host keeps 1 m off the cells (a path edge touching a cell closes it and every cell behind).
            foreach (var q in shops) res.Carve.Add(FrontToKerb(Build.ZoneLots.Grow(q, Build.ZoneLots.Clearance), roads));
        }

        /// <summary>
        /// The carve of a cell plot: the plot with its road-side edge pushed to 1 m past the kerb. A road rounded up to
        /// whole cells (a 12 m street counts 16 m) leaves a strip between kerb and row 0 the host must not plan on.
        /// </summary>
        private static float2[] FrontToKerb(float2[] q, List<(float2 a, float2 b, float half)> roads)
        {
            if (roads.Count == 0 || q.Length != 4) return q;
            int best = -1; float bestD = float.MaxValue;
            for (int e = 0; e < 4; e++)
            {
                var m = 0.5f * (q[e] + q[(e + 1) % 4]);
                foreach (var r in roads)
                {
                    float d = math.sqrt(SegmentIndex.DistSq(m, r.a, r.b)) - r.half;
                    if (d < bestD) { bestD = d; best = e; }
                }
            }
            if (best < 0 || bestD > 6f) return q;
            var a = q[best]; var b = q[(best + 1) % 4];
            var c = Poly.Centroid(q);
            var dir = math.normalizesafe(b - a); var n = new float2(-dir.y, dir.x);
            if (math.dot(n, 0.5f * (a + b) - c) < 0f) n = -n;
            float push = math.max(0f, bestD) + Build.ZoneLots.Clearance;
            var r2 = (float2[])q.Clone();
            r2[best] = a + n * push; r2[(best + 1) % 4] = b + n * push;
            return r2;
        }

        /// <summary>Water depth of the site while a plan runs (shops never stand in a lake); thread-static: plans run on workers.</summary>
        [ThreadStatic] private static Func<float2, float> _water;

        private static bool Wet(float2 p) => _water != null && _water(p) > 0.05f;

        private static void Layout(LayerRegion region, AreaInput input, List<Run> runs, string layout, string amount, int cells, ref Random rng, Result res)
        {
            switch (layout)
            {
                case Speckled: PlanSpeckled(region, runs, cells, amount, ref rng, res); break;
                case Clusters: PlanClusters(region, runs, cells, amount, ref rng, res); break;
                case Inward:
                    if (!PlanInward(region, input, runs, cells, amount, res)) { res.FellBack = true; PlanRow(region, runs, cells, amount, res); }
                    break;
                case Lane:
                    if (!PlanLane(region, input, runs, cells, amount, res)) { res.FellBack = true; PlanRow(region, runs, cells, amount, res); }
                    break;
                default: PlanRow(region, runs, cells, amount, res); break;
            }
        }

        /// <summary>Road-facing outline runs with the entrance gaps cut out (intervals ≥ 2 cells).</summary>
        public static float Tol = 6f;
        public static List<Run> Runs(LayerRegion region, List<StreetFrontage.RoadLine> roads, IReadOnlyList<float2> gates, Func<float2, float> water, bool follow = false)
        {
            var runs = new List<Run>();
            foreach (var (a, b) in follow ? StreetFrontage.FollowEdges(region.Ring, roads, 2 * Cell, Tol) : StreetFrontage.Edges(region.Ring, roads, 2 * Cell))
            {
                float len = math.distance(a, b);
                var dir = (b - a) / len;
                var inward = new float2(-dir.y, dir.x);
                var free = new List<float2> { new float2(0f, len) };
                if (gates != null)
                    foreach (var g in gates)
                    {
                        if (math.abs(math.dot(g - a, inward)) > 2f) continue;
                        float s = math.dot(g - a, dir);
                        Cut(free, s - StreetFrontage.GapHalfWidth, s + StreetFrontage.GapHalfWidth);
                    }
                // Frontage that runs into water (a street ending at a lake shore) takes no shops.
                if (water != null)
                    for (float s = 0f; s < len; s += 4f)
                        if (water(a + dir * (s + 2f) + inward * 2f) > 0.05f) Cut(free, s - 2f, s + 6f);
                foreach (var iv in free)
                    if (iv.y - iv.x >= 2 * Cell) runs.Add(new Run { A = a, Dir = dir, In = inward, From = iv.x, To = iv.y });
            }
            for (int i = 0; i < runs.Count; i++)
                for (int j = 0; j < runs.Count; j++)
                {
                    if (i == j || math.dot(runs[i].Dir, runs[j].Dir) > 0.95f) continue;
                    if (math.distance(runs[i].At(runs[i].To, 0f), runs[j].At(runs[j].From, 0f)) > 1f) continue;
                    var ri = runs[i]; ri.EndCorner = true; ri.NextDir = runs[j].Dir; ri.NextIn = runs[j].In; runs[i] = ri;
                    var rj = runs[j]; rj.StartCorner = true; runs[j] = rj;
                }
            return runs;
        }

        private static void Cut(List<float2> free, float c0, float c1)
        {
            for (int i = free.Count - 1; i >= 0; i--)
            {
                var iv = free[i];
                if (c1 <= iv.x || c0 >= iv.y) continue;
                free.RemoveAt(i);
                if (iv.x < c0) free.Insert(i, new float2(iv.x, c0));
                if (c1 < iv.y) free.Insert(i + (iv.x < c0 ? 1 : 0), new float2(c1, iv.y));
            }
        }

        // ───────────── row, speckled, clusters: pieces on the street frontage ─────────────

        private static void PlanRow(LayerRegion region, List<Run> runs, int cells, string amount, Result res)
        {
            float f = amount == Few ? 0.4f : amount == Some ? 0.7f : 1f;
            foreach (var r in runs)
            {
                float len = r.Length;
                float want = f >= 1f ? len : math.max(2 * Cell, math.floor(len * f / Cell) * Cell);
                if (want > len) want = len;
                float s0 = r.From + (len - want) * 0.5f, s1 = s0 + want;
                int pieces = math.max(1, (int)math.ceil(want / (3 * Cell)));
                for (int k = 0; k < pieces; k++)
                    FrontPiece(region, r, math.lerp(s0, s1, (float)k / pieces), math.lerp(s0, s1, (float)(k + 1) / pieces), cells, res);
            }
        }

        private static void PlanSpeckled(LayerRegion region, List<Run> runs, int cells, string amount, ref Random rng, Result res)
        {
            // Gap ranges (m) and widest pad (cells): lawn between the pads is what lets the park reach the street.
            float gMin = amount == Few ? 36f : amount == Some ? 14f : 8f;
            float gMax = amount == Few ? 64f : amount == Some ? 30f : 18f;
            int wMax = amount == Lots ? 3 : 2;
            foreach (var r in runs)
            {
                float s = r.From + rng.NextFloat(0f, gMin * 0.5f + 2f);
                while (true)
                {
                    // (s40) Two cells wide at least: the game blocks a lone column of cells (ZoneGuard.ReduceDepths).
                    float w = rng.NextInt(2, wMax + 1) * Cell;
                    if (s + w > r.To + 0.01f) { w = math.floor((r.To - s) / Cell) * Cell; if (w < 2 * Cell) break; }
                    int d = math.min(cells, rng.NextInt(1, math.min(cells, amount == Few ? 2 : 3) + 1));
                    FrontPiece(region, r, s, s + w, d, res);
                    s += w + rng.NextFloat(gMin, gMax);
                }
            }
        }

        private static void PlanClusters(LayerRegion region, List<Run> runs, int cells, string amount, ref Random rng, Result res)
        {
            float w = amount == Lots ? 5 * Cell : 4 * Cell;
            float finger = amount == Few ? 48f : amount == Some ? 24f : 16f;
            // Street corners first: one square block holds the corner (like a corner building), and the
            // clusters along both streets start a green finger away from it.
            float cb = math.clamp(cells, 2, 4) * Cell;
            var corners = new List<float2>();
            if (amount != Few)
                foreach (var r in runs)
                {
                    if (!r.EndCorner) continue;
                    var p = r.At(r.To, 0f);
                    var q = new[] { p - r.Dir * cb, p, p + r.NextDir * cb, p - r.Dir * cb + r.NextDir * cb };
                    if (Poly.SignedArea(q) <= 0f || !Fits(region, q) || OverlapsAny(res.Carve, q)) continue;
                    res.Shops.Add(q);
                    res.Carve.Add(new[] { p - r.Dir * cb - r.In, p - r.In - r.NextIn, p + r.NextDir * cb - r.NextIn, q[3] });
                    corners.Add(p);
                }
            bool Taken(float2 p) { foreach (var c in corners) if (math.distance(c, p) < 1f) return true; return false; }
            // Few: one cluster on each of the longest frontage pieces only (about a third of them).
            var chosen = new List<Run>(runs);
            if (amount == Few)
            {
                chosen.Sort((x, y) => y.Length.CompareTo(x.Length));
                chosen.RemoveRange(math.max(1, (runs.Count + 2) / 3), chosen.Count - math.max(1, (runs.Count + 2) / 3));
                w = 3 * Cell;
                cells = math.max(1, math.min(cells, 3));
            }
            foreach (var r in chosen)
            {
                float cornerGap = cb + math.max(12f, finger * 0.6f);
                float from = r.From + (r.StartCorner && Taken(r.At(r.From, 0f)) ? cornerGap : 0f);
                float to = r.To - (r.EndCorner && Taken(r.At(r.To, 0f)) ? cornerGap : 0f);
                float len = to - from;
                if (len < 2 * Cell) continue;
                int n = amount == Few ? 1 : math.max(1, (int)math.floor((len + finger) / (w + finger)));
                float cw = math.min(w, math.floor(len / Cell) * Cell);
                // Spread the clusters: the green fingers take the slack, so the rhythm reaches both ends.
                float gap = n > 1 ? math.max(finger, (len - n * cw) / (n - 1)) : 0f;
                if (n > 1 && (from + n * cw + (n - 1) * gap) > to + 0.01f) gap = finger;
                float used = n * cw + (n - 1) * gap;
                float s = from + (len - used) * 0.5f;
                for (int k = 0; k < n; k++, s += cw + gap)
                {
                    // Uneven depths: the park's edge steps in and out between and within the clusters.
                    int d = math.clamp(cells + rng.NextInt(-1, 2), 1, 6);
                    if (cw >= 4 * Cell)
                    {
                        float mid = s + math.round(cw / 2f / Cell) * Cell;
                        FrontPiece(region, r, s, mid, d, res);
                        FrontPiece(region, r, mid, s + cw, math.clamp(d + (rng.NextBool() ? 1 : -1), 1, 6), res);
                    }
                    else FrontPiece(region, r, s, s + cw, d, res);
                }
            }
        }

        /// <summary>A piece on the frontage, [s0, s1] along the run, as deep as fits (≤ cells).</summary>
        private static bool FrontPiece(LayerRegion region, Run r, float s0, float s1, int cells, Result res)
        {
            if (s1 - s0 < Cell - 0.01f) return false;
            for (int d = cells; d >= 1; d--)
                for (int trim = 0; trim <= d; trim++)
                    for (int side = 0; side < (trim == 0 ? 1 : 2); side++)
                    {
                        // Pieces of two frontages meet at a corner: never stack shops on shops — shorten the
                        // piece by whole cells at the corner end instead (the row turns the corner as an L).
                        float a = side == 0 ? s0 + trim * Cell : s0, b = side == 0 ? s1 : s1 - trim * Cell;
                        if (b - a < Cell - 0.01f) continue;
                        var q = new[] { r.At(a, 0f), r.At(b, 0f), r.At(b, d * Cell), r.At(a, d * Cell) };
                        if (!Fits(region, q) || OverlapsAny(res.Carve, q)) continue;
                        res.Shops.Add(q);
                        res.Carve.Add(new[] { r.At(a, -1f), r.At(b, -1f), q[2], q[3] });
                        return true;
                    }
            return false;
        }

        private static bool OverlapsAny(List<float2[]> polys, float2[] q)
        {
            foreach (var p in polys) if (ConvexOverlap(p, q, 0.05f)) return true;
            return false;
        }

        /// <summary>Separating-axis test for convex polygons; touching (penetration ≤ tolerance) is no overlap.</summary>
        public static bool ConvexOverlap(float2[] a, float2[] b, float tolerance)
        {
            for (int pass = 0; pass < 2; pass++)
            {
                var p = pass == 0 ? a : b;
                for (int i = 0; i < p.Length; i++)
                {
                    var e = p[(i + 1) % p.Length] - p[i];
                    if (math.lengthsq(e) < 1e-8f) continue;
                    var axis = math.normalize(new float2(-e.y, e.x));
                    float amin = float.MaxValue, amax = float.MinValue, bmin = float.MaxValue, bmax = float.MinValue;
                    foreach (var v in a) { float d = math.dot(v, axis); amin = math.min(amin, d); amax = math.max(amax, d); }
                    foreach (var v in b) { float d = math.dot(v, axis); bmin = math.min(bmin, d); bmax = math.max(bmax, d); }
                    if (amax - bmin <= tolerance || bmax - amin <= tolerance) return false;
                }
            }
            return true;
        }

        // ───────────── facing in: shops between the street and a U-shaped alley ─────────────

        private static bool PlanInward(LayerRegion region, AreaInput input, List<Run> runs, int cells, string amount, Result res)
        {
            var order = new List<Run>(runs);
            order.Sort((a, b) => b.Length.CompareTo(a.Length));
            float minLen = amount == Lots ? 40f : 56f;
            int done = 0;
            for (int i = 0; i < order.Count; i++)
            {
                var r = order[i];
                if (amount == Few && done > 0) break;
                if (r.Length < (done == 0 ? 40f : minLen)) continue;
                if (Unit(region, input, r, cells, res)) done++;
            }
            return done > 0;
        }

        /// <summary>How far an alley junction keeps from a street corner (m).</summary>
        private const float CornerClear = 12f;

        private static bool Unit(LayerRegion region, AreaInput input, Run r, int cells, Result res)
        {
            for (int d = math.min(cells, 3); d >= 1; d--)
            {
                float depth = d * Cell, back = depth + 2 * AlleyHalf + 1f;
                // Alley junctions keep clear of the street corner; where the neighbouring frontage already holds
                // a unit, this one also steps clear of its alley.
                float i0 = r.StartCorner ? CornerClear : 1f, i1 = r.EndCorner ? CornerClear : 1f;
                for (int trim = 0; trim < 4; trim++)
                {
                    float s0 = r.From + i0 + ((trim & 1) != 0 ? back : 0f), s1 = r.To - i1 - ((trim & 2) != 0 ? back : 0f);
                    if (UnitAt(region, input, r, s0, s1, depth, res)) return true;
                }
            }
            return false;
        }

        private static bool UnitAt(LayerRegion region, AreaInput input, Run r, float s0, float s1, float depth, Result res)
        {
            float legA = s0 + AlleyHalf, legB = s1 - AlleyHalf;
            float shop0 = s0 + 2 * AlleyHalf, shop1 = s1 - 2 * AlleyHalf;
            if (shop1 - shop0 < 2 * Cell) return false;
            float alley = depth + AlleyHalf, back = depth + 2 * AlleyHalf + 1f;
            var inner = new[] { r.At(s0, 0f), r.At(s1, 0f), r.At(s1, back), r.At(s0, back) };
            if (!Fits(region, inner) || OverlapsAny(res.Carve, inner)) return false;
            if (!StreetEnd(input, r.At(legA, 0f), -r.In, out var hitA, out long idA, out float tA)) return false;
            if (!StreetEnd(input, r.At(legB, 0f), -r.In, out var hitB, out long idB, out float tB)) return false;
            var path = new AlleyPath { StartAttach = idA, StartT = tA, EndAttach = idB, EndT = tB };
            path.Points.Add(hitA);
            path.Points.Add(r.At(legA, alley));
            path.Points.Add(r.At(legB, alley));
            path.Points.Add(hitB);
            res.Alleys.Add(path);
            res.Carve.Add(new[] { r.At(s0, -1f), r.At(s1, -1f), inner[2], inner[3] });
            int pieces = math.max(1, (int)math.ceil((shop1 - shop0) / (3 * Cell)));
            for (int k = 0; k < pieces; k++)
            {
                float a = math.lerp(shop0, shop1, (float)k / pieces), b = math.lerp(shop0, shop1, (float)(k + 1) / pieces);
                res.Shops.Add(new[] { r.At(a, 0f), r.At(b, 0f), r.At(b, depth), r.At(a, depth) });
            }
            return true;
        }

        // ───────────── alley: a dead-end lane into the area, shops both sides ─────────────

        private static bool PlanLane(LayerRegion region, AreaInput input, List<Run> runs, int cells, string amount, Result res)
        {
            var order = new List<Run>(runs);
            order.Sort((a, b) => b.Length.CompareTo(a.Length));
            int wanted = amount == Lots ? 2 : 1;
            float reach = amount == Few ? 0.35f : amount == Some ? 0.5f : 0.65f;
            int done = 0;
            foreach (var r in order)
            {
                if (done >= wanted) break;
                for (int d = math.min(cells, 2); d >= 1 && done < wanted; d--)
                {
                    float half = AlleyHalf + d * Cell;
                    if (r.Length < 2 * half + Cell) continue;
                    var centres = new List<float>();
                    if (wanted - done >= 2 && r.Length >= 4 * half + 48f) { centres.Add(r.From + r.Length / 3f); centres.Add(r.From + 2f * r.Length / 3f); }
                    else centres.Add(r.From + r.Length * 0.5f);
                    bool any = false;
                    foreach (var c in centres)
                        if (done < wanted && LaneAt(region, input, r, c, d, reach, res)) { done++; any = true; }
                    if (any) break;
                }
            }
            return done > 0;
        }

        private static bool LaneAt(LayerRegion region, AreaInput input, Run r, float c, int cells, float reach, Result res)
        {
            float half = AlleyHalf + cells * Cell;
            // How far the area reaches inward from here (ray march).
            float extent = 0f;
            for (float t = 4f; t < 600f; t += 4f)
            {
                if (!region.Contains(r.At(c, t)) || Wet(r.At(c, t))) break;
                extent = t;
            }
            float len = math.floor(math.clamp(extent * reach, 3 * Cell, extent - 16f) / Cell) * Cell;
            if (len < 3 * Cell) return false;
            for (; len >= 3 * Cell; len -= Cell)
            {
                var rect = new[] { r.At(c - half, 0f), r.At(c + half, 0f), r.At(c + half, len), r.At(c - half, len) };
                if (!Fits(region, rect) || OverlapsAny(res.Carve, rect)) continue;
                if (!StreetEnd(input, r.At(c, 0f), -r.In, out var hit, out long id, out float t)) return false;
                var path = new AlleyPath { StartAttach = id, StartT = t };
                path.Points.Add(hit);
                path.Points.Add(r.At(c, len));
                res.Alleys.Add(path);
                res.Carve.Add(new[] { r.At(c - half, -1f), r.At(c + half, -1f), rect[2], rect[3] });
                res.Doors.Add(r.At(c, len));
                int pieces = math.max(1, (int)math.ceil(len / (3 * Cell)));
                for (int k = 0; k < pieces; k++)
                {
                    float a = len * k / pieces, b = len * (k + 1) / pieces;
                    // Left side (towards −Dir) and right side (towards +Dir) of the lane, whole cells deep.
                    res.Shops.Add(Poly.EnsureCcw(new[] { r.At(c - half, a), r.At(c - AlleyHalf, a), r.At(c - AlleyHalf, b), r.At(c - half, b) }));
                    res.Shops.Add(Poly.EnsureCcw(new[] { r.At(c + AlleyHalf, a), r.At(c + half, a), r.At(c + half, b), r.At(c + AlleyHalf, b) }));
                }
                return true;
            }
            return false;
        }

        /// <summary>
        /// Where an alley leaving the outline at <paramref name="p"/> meets an existing street: the closest
        /// point on the street's centre line (the build snaps the attached end onto it, like parking alleys).
        /// Planned streets of a parent layer cannot take a mid-segment junction, so only existing nets count.
        /// </summary>
        public static bool StreetEnd(AreaInput input, float2 p, float2 outward, out float2 hit, out long id, out float t)
        {
            hit = p; id = -1; t = 0f;
            float best = float.MaxValue;
            var nets = input?.Site?.NearbyNets;
            if (nets == null) return false;
            foreach (var n in nets)
            {
                if (n.Category != NetCategory.Road) continue;
                float reach = math.max(4f, n.Width * 0.5f) + StreetFrontage.Tolerance + 2f;
                float bt = 0f, bd = float.MaxValue;
                for (int i = 0; i <= 64; i++)
                {
                    float tt = i / 64f;
                    float d = math.distancesq(Composer.Bezier(n.A, n.B, n.C, n.D, tt).xz, p);
                    if (d < bd) { bd = d; bt = tt; }
                }
                // Refine around the best sample.
                float lo = math.max(0f, bt - 1f / 64f), hi = math.min(1f, bt + 1f / 64f);
                for (int k = 0; k < 24; k++)
                {
                    float m1 = math.lerp(lo, hi, 1f / 3f), m2 = math.lerp(lo, hi, 2f / 3f);
                    if (math.distancesq(Composer.Bezier(n.A, n.B, n.C, n.D, m1).xz, p) < math.distancesq(Composer.Bezier(n.A, n.B, n.C, n.D, m2).xz, p)) hi = m2; else lo = m1;
                }
                bt = 0.5f * (lo + hi);
                var q = Composer.Bezier(n.A, n.B, n.C, n.D, bt).xz;
                float dist = math.distance(q, p);
                // The street must lie on the outer side and the alley must not hit its very end.
                if (dist > reach || math.dot(q - p, outward) < 0.5f || bt < 0.02f || bt > 0.98f) continue;
                if (dist < best) { best = dist; hit = q; id = n.ExternalId; t = bt; }
            }
            return id >= 0;
        }

        /// <summary>Polygon wholly inside the region (vertices may lie on the outline).</summary>
        public static bool Fits(LayerRegion region, float2[] poly)
        {
            var c = Poly.Centroid(poly);
            if (Wet(c)) return false;
            var s = new float2[poly.Length];
            for (int i = 0; i < poly.Length; i++)
            {
                s[i] = poly[i] + math.normalizesafe(c - poly[i]) * 0.1f;
                if (!region.Contains(s[i]) || Wet(s[i])) return false;
            }
            for (int i = 0; i < s.Length; i++)
            {
                var a = s[i]; var b = s[(i + 1) % s.Length];
                if (region.CrossesBoundary(a, b, false) || !region.Contains(0.5f * (a + b))) return false;
            }
            return true;
        }

        /// <summary>The host's outline minus everything carved: the largest remaining piece (null: nothing sensible left).</summary>
        public static float2[] Carve(LayerRegion region, List<float2[]> carve, float minArea)
        {
            if (carve.Count == 0) return null;
            var clip = new List<IReadOnlyList<float2>>();
            foreach (var q in carve) clip.Add(Poly.EnsureCcw(q));
            List<Region> pieces;
            try { pieces = Poly.Boolean(new[] { (IReadOnlyList<float2>)region.Ring }, clip, BoolOp.Difference); }
            catch (Exception) { return null; }
            float2[] best = null; float bestArea = minArea;
            foreach (var p in pieces)
            {
                if (p?.Outer == null || p.Outer.Length < 3) continue;
                float a = Poly.Area(p.Outer);
                if (a > bestArea) { bestArea = a; best = p.Outer; }
            }
            return best == null ? null : Poly.EnsureCcw(Poly.Clean(best, 0.05f));
        }

        /// <summary>True when a disc of radius r at p overlaps a shop region.</summary>
        public static bool Overlaps(List<float2[]> shops, float2 p, float r)
        {
            foreach (var q in shops)
            {
                if (Poly.Contains(q, p)) return true;
                if (r > 0f && Poly.DistanceToBoundary(q, p) < r) return true;
            }
            return false;
        }
    }
}
