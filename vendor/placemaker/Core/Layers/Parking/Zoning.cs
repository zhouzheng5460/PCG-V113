// Building land along the lot's street frontage ("place buildings"). Redesign of ParkingLotTool's
// zoning (KruemelGames, GPL-3.0): Geometry/ParkingGeometry.Randzoning.cs (edge zoning: a chosen
// outline edge gets a band of building land), ParkingGeometry.Zoning.cs (Zoningflaeche: a rectangle
// of 8 m parcels), Geometry/Zellen/Randzoningplan.cs (the band: Strasse/Bauland/Freihaltung),
// ZoningMuendung.cs / ZoningGassenabstand.cs (lanes must stop at the band, never cross it).
//
// What changed and why (docs/proposals/parking.md):
//  * PLT ran its own 8 m "zoning road" (a cloned vanilla Alley with zone blocks) through the lot and
//    let houses grow off it. That road had to be welded into the drive network (Zoningknoten,
//    ZoningRandanschluss, RinglosZoning — ~3k lines) and was the source of most zoning bugs.
//  * Here the band sits on an edge that faces an EXISTING street. CS2 already generates zone blocks
//    6 cells deep along that street; the band only keeps those cells free of parking and tells the
//    game which of them to zone (PlannedZoning). No new road, no junctions, no LocalConnect.
//  * Zoning patches (PLT's rectangles inside the lot) become patches on the frontage: a marker on the
//    outline, N cells wide, same depth. Interior zoning needs a road and belongs to the Roads layer.
//
// Guarantees: the parking layout runs on (outline − bands), so stalls, lanes and surfaces never
// reach a zoned quad; quads stay inside the outline; entrances keep a clear gap through the band.

using System;
using System.Collections.Generic;
using Placemaker.Core.Geometry;
using Unity.Mathematics;

namespace Placemaker.Core.Layers.Parking
{
    /// <summary>A zoned quad in world XZ: A→B along the outline edge, D→C on the lot side.</summary>
    internal struct ZoneQuad
    {
        public double2 A, B, C, D;
        public int Edge;
        public double2[] Ring => new[] { A, B, C, D };
    }

    internal sealed class ZoningResult
    {
        /// <summary>The lot left for parking (world, CCW). Equal to the outline when nothing is zoned.</summary>
        public List<double2> Lot;
        public readonly List<ZoneQuad> Quads = new List<ZoneQuad>();
        /// <summary>Deepest band (m) — entrances on a zoned edge cross it to reach the street.</summary>
        public double MaxDepth;
        public readonly List<PlanIssue> Issues = new List<PlanIssue>();
        public double ZonedArea
        {
            get { double a = 0; foreach (var q in Quads) a += PGeo.Area(q.Ring); return a; }
        }
    }

    internal static class ParkingZoning
    {
        private const string K = "Placemaker.Parking.";
        private const double SampleStep = 2.0;
        /// <summary>A frontage shorter than two cells grows nothing useful.</summary>
        private const double MinRun = 2 * ParkingSettings.ZoneCell;
        /// <summary>Road must run along the edge: |cos| ≥ 0.9 (≈ 25°).</summary>
        private const double ParallelCos = 0.9;
        /// <summary>Clear space each side of an entrance through the band (turning room, sight lines).</summary>
        private const double EntranceClear = 3.0;
        /// <summary>A band thinner than this holds no building.</summary>
        private const double MinBand = 4.0;
        /// <summary>Longest zoned quad; CS2 zone blocks are at most 10 cells wide anyway.</summary>
        private const double MaxQuad = 6 * ParkingSettings.ZoneCell;

        internal struct Run { public int Edge; public double S0, S1, Gap; public long Road; }
        private struct Span { public double S0, S1, Depth; }

        /// <summary>One entrance-like marker that needs a gap in the band (world position + width).</summary>
        public struct Opening { public double2 P; public double Width; }

        public static ZoningResult Plan(List<double2> world, List<RoadLine> roads, ParkingSettings s,
            List<Opening> openings, List<double2> patches, double minLotArea)
        {
            var res = new ZoningResult { Lot = world };
            bool any = s.EdgeZoning != EdgeZoningMode.None || (patches != null && patches.Count > 0);
            if (!any || world == null || world.Count < 3) return res;

            int n = world.Count;
            var runs = Frontage(world, roads);
            var spans = new List<Span>[n];
            for (int i = 0; i < n; i++) spans[i] = new List<Span>();

            double Depth(Run r) => s.ZoningDepthCells * ParkingSettings.ZoneCell - Math.Max(0, r.Gap);

            // ── whole edges ──
            if (s.EdgeZoning != EdgeZoningMode.None)
            {
                var chosen = new List<Run>();
                if (s.EdgeZoning == EdgeZoningMode.All) chosen.AddRange(runs);
                else
                {
                    int best = -1;
                    for (int i = 0; i < runs.Count; i++)
                        if (best < 0 || runs[i].S1 - runs[i].S0 > runs[best].S1 - runs[best].S0 + 1e-6) best = i;
                    if (best >= 0) chosen.Add(runs[best]);
                }
                if (chosen.Count == 0)
                    res.Issues.Add(new PlanIssue { Severity = IssueSeverity.Warning, Key = K + "Issue.ZoningNoStreet" });
                foreach (var r in chosen)
                {
                    double d = Depth(r);
                    if (d < MinBand) { TooFar(res, world, r); continue; }
                    spans[r.Edge].Add(new Span { S0 = r.S0, S1 = r.S1, Depth = d });
                }
            }

            // ── patches ──
            if (patches != null)
                foreach (var p in patches)
                {
                    int e = NearestEdge(world, p, out double along);
                    Run? hit = null;
                    foreach (var r in runs)
                        if (r.Edge == e && along >= r.S0 - 1 && along <= r.S1 + 1) { hit = r; break; }
                    if (!hit.HasValue)
                    {
                        res.Issues.Add(new PlanIssue { Severity = IssueSeverity.Warning, Key = K + "Issue.ZoningPatchNoStreet", At = new float3((float)p.x, 0, (float)p.y) });
                        continue;
                    }
                    var run = hit.Value;
                    double d = Depth(run);
                    if (d < MinBand) { TooFar(res, world, run); continue; }
                    double half = s.ZoningPatchCells * ParkingSettings.ZoneCell / 2;
                    double s0 = Math.Max(run.S0, along - half), s1 = Math.Min(run.S1, along + half);
                    // keep the full width when the patch hits the end of the frontage
                    if (s1 - s0 < 2 * half) { if (s0 <= run.S0 + 1e-9) s1 = Math.Min(run.S1, s0 + 2 * half); else s0 = Math.Max(run.S0, s1 - 2 * half); }
                    if (s1 - s0 >= ParkingSettings.ZoneCell - 1e-6) spans[e].Add(new Span { S0 = s0, S1 = s1, Depth = d });
                }

            // ── merge, cut entrance gaps, build bands and quads ──
            var origin = PGeo.Centroid(world);
            var rects = new List<IReadOnlyList<float2>>();
            var quads = new List<ZoneQuad>();
            for (int e = 0; e < n; e++)
            {
                if (spans[e].Count == 0) continue;
                var a = world[e]; var b = world[(e + 1) % n];
                double len = PGeo.Len(b - a);
                var tan = (b - a) / len; var inward = PGeo.Perp(tan);
                var merged = Merge(spans[e]);
                // entrances on this edge
                var cuts = new List<(double, double)>();
                if (openings != null)
                    foreach (var o in openings)
                    {
                        if (PGeo.DistPointSeg(o.P, a, b) > 1.0) continue;
                        double al = PGeo.Dot(o.P - a, tan);
                        double h = o.Width / 2 + EntranceClear;
                        cuts.Add((al - h, al + h));
                    }
                foreach (var sp in merged)
                {
                    // the lot gives up the whole band (lanes must not run along inside it)…
                    rects.Add(Local(new[] { a + tan * sp.S0, a + tan * sp.S1, a + tan * sp.S1 + inward * sp.Depth, a + tan * sp.S0 + inward * sp.Depth }, origin));
                    res.MaxDepth = Math.Max(res.MaxDepth, sp.Depth);
                    // …but only the parts between entrances are zoned
                    foreach (var piece in Subtract(sp.S0, sp.S1, cuts))
                        AddQuads(quads, world, e, a, tan, inward, piece.Item1, piece.Item2, sp.Depth);
                }
            }
            if (rects.Count == 0) return res;

            // ── lot = outline − bands ──
            var outline = Local(world.ToArray(), origin);
            var regions = Poly.Boolean(new[] { (IReadOnlyList<float2>)outline }, rects, BoolOp.Difference);
            if (regions.Count == 0 || regions[0].Outer.Length < 3 || regions[0].Area < minLotArea)
            {
                res.Issues.Add(new PlanIssue { Severity = IssueSeverity.Warning, Key = K + "Issue.ZoningLeavesNoRoom" });
                res.MaxDepth = 0;
                return res;
            }
            var lot = new List<double2>(regions[0].Outer.Length);
            foreach (var p in regions[0].Outer) lot.Add(new double2(p.x, p.y) + origin);
            lot = PGeo.EnsureCcw(PGeo.Simplify(lot, 1e-3, 1e-6));
            if (lot.Count < 3 || !PGeo.IsSimple(lot))
            {
                res.Issues.Add(new PlanIssue { Severity = IssueSeverity.Warning, Key = K + "Issue.ZoningLeavesNoRoom" });
                res.MaxDepth = 0;
                return res;
            }
            res.Lot = lot;
            res.Quads.AddRange(quads);
            return res;
        }

        private static void TooFar(ZoningResult res, List<double2> world, Run r)
        {
            var a = world[r.Edge]; var b = world[(r.Edge + 1) % world.Count];
            var m = a + PGeo.Norm(b - a) * ((r.S0 + r.S1) / 2);
            res.Issues.Add(new PlanIssue { Severity = IssueSeverity.Warning, Key = K + "Issue.ZoningStreetTooFar", At = new float3((float)m.x, 0, (float)m.y) });
        }

        private static float2[] Local(double2[] pts, double2 origin)
        {
            var r = new float2[pts.Length];
            for (int i = 0; i < pts.Length; i++) r[i] = new float2((float)(pts[i].x - origin.x), (float)(pts[i].y - origin.y));
            return r;
        }

        /// <summary>
        /// Stretches of each outline edge that face a street running alongside (PLT: "a ray from the
        /// polygon edge in the outward direction finds the road within 40 m"), with the largest gap
        /// between outline and street edge on that stretch.
        /// </summary>
        internal static List<Run> Frontage(List<double2> world, List<RoadLine> roads)
        {
            var runs = new List<Run>();
            if (roads == null || roads.Count == 0) return runs;
            int n = world.Count;
            for (int e = 0; e < n; e++)
            {
                var a = world[e]; var b = world[(e + 1) % n];
                double len = PGeo.Len(b - a);
                if (len < MinRun) continue;
                var tan = (b - a) / len; var inward = PGeo.Perp(tan);
                int samples = Math.Max(2, (int)Math.Ceiling((len - 1) / SampleStep));
                bool open = false; Run cur = default;
                double lastAlong = 0;
                for (int k = 0; k <= samples; k++)
                {
                    double along = 0.5 + (len - 1) * k / samples;
                    var p = a + tan * along;
                    bool ok = false; double gap = 0; long road = -1;
                    if (Roads.Cast(roads, p, -inward, Roads.SearchDistance, out var hit))
                    {
                        var rd = roads[hit.Road];
                        gap = hit.Distance - rd.Width / 2;
                        ok = Math.Abs(PGeo.Dot(hit.Direction, tan)) >= ParallelCos && gap > -0.5;
                        road = rd.Id;
                    }
                    if (ok && open && road == cur.Road) { cur.S1 = along; cur.Gap = Math.Max(cur.Gap, gap); }
                    else
                    {
                        if (open && cur.S1 - cur.S0 >= MinRun) runs.Add(cur);
                        open = ok;
                        if (ok) cur = new Run { Edge = e, S0 = Math.Max(0, along - (k == 0 ? 0.5 : SampleStep / 2)), S1 = along, Gap = gap, Road = road };
                    }
                    lastAlong = along;
                }
                if (open)
                {
                    cur.S1 = Math.Min(len, cur.S1 + 0.5);
                    if (cur.S1 - cur.S0 >= MinRun) runs.Add(cur);
                }
            }
            return runs;
        }

        private static int NearestEdge(List<double2> ring, double2 p, out double along)
        {
            int best = 0; double bd = double.MaxValue; along = 0;
            for (int i = 0; i < ring.Count; i++)
            {
                var a = ring[i]; var b = ring[(i + 1) % ring.Count];
                var q = PGeo.ClosestOnSeg(p, a, b, out double t);
                double d = PGeo.Len(q - p);
                if (d < bd - 1e-9) { bd = d; best = i; along = t * PGeo.Len(b - a); }
            }
            return best;
        }

        private static List<Span> Merge(List<Span> spans)
        {
            spans.Sort((x, y) => x.S0.CompareTo(y.S0));
            var r = new List<Span>();
            foreach (var sp in spans)
            {
                if (r.Count > 0 && sp.S0 <= r[r.Count - 1].S1 + 1e-6)
                {
                    var last = r[r.Count - 1];
                    last.S1 = Math.Max(last.S1, sp.S1);
                    last.Depth = Math.Min(last.Depth, sp.Depth);
                    r[r.Count - 1] = last;
                }
                else r.Add(sp);
            }
            return r;
        }

        private static List<(double, double)> Subtract(double s0, double s1, List<(double From, double To)> cuts)
        {
            var parts = new List<(double, double)> { (s0, s1) };
            foreach (var c in cuts)
            {
                var next = new List<(double, double)>();
                foreach (var p in parts)
                {
                    if (c.To <= p.Item1 || c.From >= p.Item2) { next.Add(p); continue; }
                    if (c.From > p.Item1) next.Add((p.Item1, c.From));
                    if (c.To < p.Item2) next.Add((c.To, p.Item2));
                }
                parts = next;
            }
            parts.RemoveAll(p => p.Item2 - p.Item1 < ParkingSettings.ZoneCell - 1e-6);
            return parts;
        }

        /// <summary>
        /// Quads of at most 48 m along the edge; a quad that leaves the outline (concave corners) is
        /// retried cell by cell and only cells fully inside are kept.
        /// </summary>
        private static void AddQuads(List<ZoneQuad> outList, List<double2> world, int edge, double2 a, double2 tan, double2 inward, double s0, double s1, double depth)
        {
            double len = s1 - s0;
            int chunks = Math.Max(1, (int)Math.Ceiling(len / MaxQuad - 1e-9));
            double step = len / chunks;
            for (int c = 0; c < chunks; c++)
            {
                double c0 = s0 + c * step, c1 = c0 + step;
                var q = Quad(edge, a, tan, inward, c0, c1, depth);
                if (Inside(world, q)) { outList.Add(q); continue; }
                int cells = Math.Max(1, (int)Math.Floor((c1 - c0) / ParkingSettings.ZoneCell + 1e-9));
                double cw = (c1 - c0) / cells;
                int runStart = -1;
                for (int k = 0; k <= cells; k++)
                {
                    bool ok = k < cells && Inside(world, Quad(edge, a, tan, inward, c0 + k * cw, c0 + (k + 1) * cw, depth));
                    if (ok && runStart < 0) runStart = k;
                    if (!ok && runStart >= 0)
                    {
                        outList.Add(Quad(edge, a, tan, inward, c0 + runStart * cw, c0 + k * cw, depth));
                        runStart = -1;
                    }
                }
            }
        }

        private static ZoneQuad Quad(int edge, double2 a, double2 tan, double2 inward, double s0, double s1, double depth) => new ZoneQuad
        {
            Edge = edge,
            A = a + tan * s0, B = a + tan * s1, C = a + tan * s1 + inward * depth, D = a + tan * s0 + inward * depth,
        };

        /// <summary>Quad inside the outline: corners inside (5 cm tolerance), no outline edge crossing it.</summary>
        private static bool Inside(List<double2> ring, ZoneQuad q)
        {
            var r = q.Ring;
            foreach (var p in r)
                if (!PGeo.Contains(ring, p) && PGeo.DistToBoundary(ring, p) > 0.05) return false;
            for (int i = 0; i < ring.Count; i++)
            {
                var v = ring[i];
                if (PGeo.StrictlyInsideConvex(r, v, 0.05)) return false;
                var w = ring[(i + 1) % ring.Count];
                for (int j = 0; j < 4; j++)
                    if (PGeo.SegmentsCross(v, w, r[j], r[(j + 1) % 4], 1e-7)) return false;
            }
            return true;
        }
    }
}
