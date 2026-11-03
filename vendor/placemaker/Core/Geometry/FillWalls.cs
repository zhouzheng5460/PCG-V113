// Placemaker — what the Paint bucket (Block mode) treats as a WALL (s43).
//
// Playtest: "it's not following paths; some streets it's even ignoring; it goes over streets and roads and
// paths. I want to customize what it sees and doesn't see". The bucket traces the face of a planar graph; until
// s43 that graph held car roads only. Now the player picks the walls:
//   * curve walls (Roads, Paths, Bike paths, Rail & tram) are edges of the planar graph (FillGraph): a path
//     between two roads splits the block exactly like a street does;
//   * area walls (Water, Lots & buildings, Zoned) are polygons cut out of the traced face afterwards, keeping
//     the piece under the click (a wall keeps the fill on the side it started, unlike "Keep off", which keeps
//     the largest piece).
// The curb line comes from each net's own half-width (corridor = centre line ± half the composition width).

using System;
using System.Collections.Generic;
using Unity.Mathematics;

namespace Placemaker.Core.Geometry
{
    [Flags]
    public enum FillWall
    {
        None = 0,
        /// <summary>Every car road: streets, avenues, highways, alleys, gravel, one-way, pedestrian streets.</summary>
        Roads = 1,
        /// <summary>Pedestrian paths (pathways), shared paths.</summary>
        Paths = 2,
        /// <summary>Bicycle-only paths.</summary>
        Bikes = 4,
        /// <summary>Train, tram and subway tracks at ground level.</summary>
        Tracks = 8,
        Water = 16,
        /// <summary>Building lots, other lots (parks, extractors, other mods) and other Placemaker builds.</summary>
        Lots = 32,
        /// <summary>Zoned cells.</summary>
        Zoned = 64,
    }

    /// <summary>A polyline the bucket may stop at (centre line A→B with its half-width).</summary>
    public struct FillCurveGeom
    {
        public float2[] Geom;
        public float HalfWidth;
        public FillCurveGeom(float2[] geom, float halfWidth) { Geom = geom; HalfWidth = halfWidth; }
    }

    public static class FillWalls
    {
        /// <summary>Defaults: every line on the ground that separates land (roads, paths, bike paths, tracks) and water.</summary>
        public const FillWall Default = FillWall.Roads | FillWall.Paths | FillWall.Bikes | FillWall.Tracks | FillWall.Water;
        public const FillWall Curves = FillWall.Roads | FillWall.Paths | FillWall.Bikes | FillWall.Tracks;
        public const FillWall Areas = FillWall.Water | FillWall.Lots | FillWall.Zoned;
        public const FillWall All = Curves | Areas;

        /// <summary>Pieces thinner than this many m² are ignored.</summary>
        public const float MinPieceArea = 4f;

        public static bool Has(FillWall set, FillWall w) => (set & w) != 0;

        /// <summary>The polygon obstacles a wall set counts (roads are curve walls, not polygons).</summary>
        public static bool Counts(FillWall set, BrushObstacleKind k)
        {
            switch (k)
            {
                case BrushObstacleKind.Building:
                case BrushObstacleKind.Lot:
                case BrushObstacleKind.PlacemakerBuild: return Has(set, FillWall.Lots);
                case BrushObstacleKind.UnbuiltZonedCell:
                case BrushObstacleKind.ZonedCell: return Has(set, FillWall.Zoned);
                default: return false;
            }
        }

        /// <summary>
        /// Corridor polygons (CCW) of the curves near [min, max]: the polyline ± half-width with round caps at both
        /// ends (they cover the junction area, like the CS2 node geometry).
        /// </summary>
        public static List<IReadOnlyList<float2>> Corridors(IEnumerable<FillCurveGeom> curves, float2 min, float2 max, float margin = 0f)
        {
            var list = new List<IReadOnlyList<float2>>();
            if (curves == null) return list;
            foreach (var c in curves)
            {
                var g = c.Geom;
                if (g == null || g.Length < 2) continue;
                float hw = math.max(0.05f, c.HalfWidth + margin);
                Poly.Bounds(g, out var bmin, out var bmax);
                if (bmin.x - hw > max.x || bmin.y - hw > max.y || bmax.x + hw < min.x || bmax.y + hw < min.y) continue;
                var pts = Densify(g, 4f);
                var left = new float2[pts.Count];
                var right = new float2[pts.Count];
                for (int i = 0; i < pts.Count; i++)
                {
                    float2 d = math.normalizesafe(pts[math.min(i + 1, pts.Count - 1)] - pts[math.max(i - 1, 0)], new float2(1, 0));
                    float2 n = new float2(-d.y, d.x);
                    left[i] = pts[i] + n * hw;
                    right[i] = pts[i] - n * hw;
                }
                var band = new List<float2>(pts.Count * 2);
                for (int i = 0; i < pts.Count; i++) band.Add(right[i]);
                for (int i = pts.Count - 1; i >= 0; i--) band.Add(left[i]);
                list.Add(Poly.EnsureCcw(band.ToArray()));
                list.Add(Disc(pts[0], hw));
                list.Add(Disc(pts[pts.Count - 1], hw));
            }
            return list;
        }

        /// <summary>
        /// The usable land of a face: its centre-line ring minus the corridors of the walls around and inside it,
        /// the piece containing <paramref name="seed"/> (the nearest piece when the seed is on a wall). Null when
        /// nothing usable is left.
        /// </summary>
        public static float2[] CurbOutline(float2[] centreRing, IReadOnlyList<FillCurveGeom> curves, float2 seed)
        {
            if (centreRing == null || centreRing.Length < 3) return null;
            var ring = Poly.EnsureCcw(centreRing);
            Poly.Bounds(ring, out var min, out var max);
            var corridors = Corridors(curves, min, max);
            if (corridors.Count == 0) return Finish(ring, 1f);
            List<Region> pieces;
            try { pieces = Poly.Boolean(new IReadOnlyList<float2>[] { ring }, corridors, BoolOp.Difference); }
            catch (Exception) { return null; }
            return PickPiece(pieces, seed, out _);
        }

        /// <summary>
        /// Area walls: the ring minus the wall polygons, the piece on the click's side (containing the seed, else
        /// the nearest). Returns the ring itself (same instance) when no wall touches it; null when nothing usable
        /// is left. Walls enclosed by the piece are opened with a 1 m corridor (CS2 areas have no holes).
        /// </summary>
        public static float2[] ClipToSeed(float2[] ring, IReadOnlyList<float2[]> walls, float2 seed, out int touched)
        {
            touched = 0;
            if (ring == null || ring.Length < 3 || walls == null || walls.Count == 0) return ring;
            var pieces = ObstacleCut.Pieces(ring, walls, out touched);
            if (pieces == null) return ring;
            return PickPiece(pieces, seed, out _);
        }

        /// <summary>The piece under the seed (or nearest to it) as one simple CCW ring.</summary>
        public static float2[] PickPiece(List<Region> pieces, float2 seed, out int index)
        {
            index = -1;
            if (pieces == null) return null;
            float best = float.MaxValue;
            for (int i = 0; i < pieces.Count; i++)
            {
                var p = pieces[i];
                if (p?.Outer == null || p.Outer.Length < 3 || p.Area < MinPieceArea) continue;
                float d = p.Contains(seed) ? -1f : (Poly.Contains(p.Outer, seed) ? 0f : Poly.DistanceToBoundary(p.Outer, seed));
                if (d < best) { best = d; index = i; }
            }
            if (index < 0) return null;
            var kept = pieces[index];
            float2[] outer = kept.Outer;
            if (kept.HasHoles)
            {
                List<float2[]> cut;
                try { cut = Poly.CutHoles(kept, ObstacleCut.Corridor); }
                catch (Exception) { cut = new List<float2[]>(); }
                outer = null;
                float bestD = float.MaxValue;
                foreach (var c in cut)
                {
                    if (c == null || c.Length < 3 || Poly.Area(c) < MinPieceArea) continue;
                    float d = Poly.Contains(c, seed) ? -Poly.Area(c) : Poly.DistanceToBoundary(c, seed);
                    if (d < bestD) { bestD = d; outer = c; }
                }
                if (outer == null) return null;
            }
            return Finish(outer, MinPieceArea);
        }

        private static float2[] Finish(float2[] outer, float minArea)
        {
            outer = Poly.Clean(outer, SurfaceAcceptance.MinEdge + 0.05f);
            if (outer.Length < 3) return null;
            outer = Poly.EnsureCcw(Poly.RemoveCollinear(outer, 1e-4f));
            if (outer.Length < 3 || !Poly.IsSimple(outer) || Poly.Area(outer) < minArea) return null;
            return outer;
        }

        /// <summary>
        /// Water as wall polygons: cells of <paramref name="cell"/> m inside the ring's box where the predicate says
        /// "under water", merged into row runs (rectangles slightly overlapping so the union has no seams).
        /// </summary>
        public static List<float2[]> WaterRects(IReadOnlyList<float2> ring, Func<float2, bool> underWater, float cell = 4f, int maxCells = 60000)
        {
            var list = new List<float2[]>();
            if (ring == null || ring.Count < 3 || underWater == null) return list;
            Poly.Bounds(ring, out var min, out var max);
            min -= cell; max += cell;
            int nx = (int)math.ceil((max.x - min.x) / cell), ny = (int)math.ceil((max.y - min.y) / cell);
            if (nx <= 0 || ny <= 0) return list;
            if ((long)nx * ny > maxCells)
            {
                cell *= (float)math.sqrt((double)nx * ny / maxCells);
                nx = (int)math.ceil((max.x - min.x) / cell); ny = (int)math.ceil((max.y - min.y) / cell);
            }
            const float pad = 0.05f;
            for (int j = 0; j < ny; j++)
            {
                float y0 = min.y + j * cell;
                int run = -1;
                for (int i = 0; i <= nx; i++)
                {
                    bool wet = i < nx && underWater(new float2(min.x + (i + 0.5f) * cell, y0 + 0.5f * cell));
                    if (wet && run < 0) run = i;
                    else if (!wet && run >= 0)
                    {
                        float x0 = min.x + run * cell - pad, x1 = min.x + i * cell + pad;
                        list.Add(new[] { new float2(x0, y0 - pad), new float2(x1, y0 - pad), new float2(x1, y0 + cell + pad), new float2(x0, y0 + cell + pad) });
                        run = -1;
                    }
                }
            }
            return list;
        }

        private static List<float2> Densify(float2[] g, float step)
        {
            var pts = new List<float2>(g.Length + 4) { g[0] };
            for (int i = 1; i < g.Length; i++)
            {
                float len = math.distance(g[i - 1], g[i]);
                if (len < 1e-4f) continue;
                int n = math.clamp((int)math.ceil(len / step), 1, 256);
                // Straight segments keep their ends only; curves were already sampled by the caller.
                if (g.Length == 2) n = 1;
                for (int k = 1; k <= n; k++) pts.Add(math.lerp(g[i - 1], g[i], (float)k / n));
            }
            if (pts.Count == 1) pts.Add(g[g.Length - 1] + new float2(0.01f, 0));
            return pts;
        }

        private static float2[] Disc(float2 c, float r, int sides = 16)
        {
            var d = new float2[sides];
            for (int i = 0; i < sides; i++)
            {
                float a = i * 2f * math.PI / sides;
                d[i] = c + new float2(math.cos(a), math.sin(a)) * r;
            }
            return d;
        }
    }
}
