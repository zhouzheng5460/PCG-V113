// Placemaker roads layer — obstacle avoidance.
// Adapted from Grid Road Generator (Ti4goc), Systems/GridRoadToolSystem.FreeArea.cs
// (RemoveObstacleSegments / Blocked / InsideBuilding): water deeper than 0.3 m, building
// footprints + 4 m, utility objects (pylons) + 8 m. Engine queries are replaced by the
// SiteContext snapshot. Unlike GRG, removal is followed by stub pruning (RoadNetwork.PruneStubs).

using System;
using System.Collections.Generic;
using Unity.Mathematics;

namespace Placemaker.Core.Layers.Roads
{
    internal sealed class RoadObstacles
    {
        public const float WaterDepth = 0.3f;
        public const float BuildingMargin = 4f;
        public const float UtilityMargin = 8f;
        public const float OtherMargin = 2f;
        public const float WaterQuadMargin = 2f;
        private const float SampleStep = 4f;

        private struct Quad { public float2 A, B, C, D; public float Margin; public float2 Min, Max; public bool Building; }

        private readonly List<Quad> _quads = new List<Quad>();
        /// <summary>Obstacles too large for the grid index (checked directly).</summary>
        private readonly List<int> _huge = new List<int>();
        private readonly Func<float2, float> _water;
        private readonly Dictionary<long, List<int>> _cells = new Dictionary<long, List<int>>();
        private const float Cell = 32f;

        public RoadObstacles(SiteContext site, float2 areaMin, float2 areaMax)
        {
            _water = site?.WaterDepthAt;
            if (site?.Obstacles == null) return;
            float2 mn = areaMin - 64f, mx = areaMax + 64f;
            foreach (var o in site.Obstacles)
            {
                if (!(math.all(math.isfinite(o.A)) && math.all(math.isfinite(o.B)) && math.all(math.isfinite(o.C)) && math.all(math.isfinite(o.D)))) continue;
                float margin = o.Kind == ObstacleKind.Building ? BuildingMargin
                    : o.Kind == ObstacleKind.Utility ? UtilityMargin
                    : o.Kind == ObstacleKind.Water ? WaterQuadMargin : OtherMargin;
                var q = new Quad { A = o.A, B = o.B, C = o.C, D = o.D, Margin = margin, Building = o.Kind == ObstacleKind.Building };
                q.Min = math.min(math.min(o.A, o.B), math.min(o.C, o.D)) - margin;
                q.Max = math.max(math.max(o.A, o.B), math.max(o.C, o.D)) + margin;
                if (q.Max.x < mn.x || q.Max.y < mn.y || q.Min.x > mx.x || q.Min.y > mx.y) continue;
                int id = _quads.Count;
                _quads.Add(q);
                int x0 = (int)math.floor(q.Min.x / Cell), x1 = (int)math.floor(q.Max.x / Cell);
                int y0 = (int)math.floor(q.Min.y / Cell), y1 = (int)math.floor(q.Max.y / Cell);
                if ((long)(x1 - x0 + 1) * (y1 - y0 + 1) > 4096) { _huge.Add(id); continue; }
                for (int x = x0; x <= x1; x++)
                    for (int y = y0; y <= y1; y++)
                    {
                        long k = ((long)x << 32) ^ (uint)y;
                        if (!_cells.TryGetValue(k, out var l)) _cells[k] = l = new List<int>();
                        l.Add(id);
                    }
            }
        }

        public bool IsEmpty => _quads.Count == 0 && _water == null;

        /// <summary>
        /// Does the curve come within an obstacle's margin? <paramref name="halfWidth"/> (s34) is the road's half width
        /// (+ clearance) for building lots: the composer's site safety drops any net whose BODY touches a lot, so a centre
        /// line 4 m off a farmhouse is not enough for a 16 m street (the playtest's islands next to the ruin).
        /// </summary>
        public bool Blocked(RoadCurve curve, float halfWidth = 0f)
        {
            float len = curve.LengthXZ(8);
            int samples = math.max(2, (int)math.ceil(len / SampleStep));
            for (int k = 0; k <= samples; k++)
            {
                float3 p = curve.Position((float)k / samples);
                if (BlockedAt(p.xz, halfWidth)) return true;
            }
            return false;
        }

        public bool BlockedAt(float2 p, float halfWidth = 0f)
        {
            if (_water != null)
            {
                float d = _water(p);
                if (math.isfinite(d) && d > WaterDepth) return true;
            }
            long k = ((long)(int)math.floor(p.x / Cell) << 32) ^ (uint)(int)math.floor(p.y / Cell);
            if (halfWidth > 0f)
            {
                // Wider than one grid cell's slack: look at the neighbouring cells too.
                int r = (int)math.ceil(halfWidth / Cell);
                int cx = (int)math.floor(p.x / Cell), cy = (int)math.floor(p.y / Cell);
                for (int x = cx - r; x <= cx + r; x++)
                    for (int y = cy - r; y <= cy + r; y++)
                        if (_cells.TryGetValue(((long)x << 32) ^ (uint)y, out var l))
                            foreach (int id in l) if (Inside(_quads[id], p, _quads[id].Building ? halfWidth : 0f)) return true;
            }
            else if (_cells.TryGetValue(k, out var list))
                foreach (int id in list) if (Inside(_quads[id], p, 0f)) return true;
            foreach (int id in _huge) if (Inside(_quads[id], p, _quads[id].Building ? halfWidth : 0f)) return true;
            return false;
        }

        private static bool Inside(Quad q, float2 p, float extra)
        {
            if (p.x < q.Min.x - extra || p.y < q.Min.y - extra || p.x > q.Max.x + extra || p.y > q.Max.y + extra) return false;
            // Convex quad of either winding: inside when all edge cross products share a sign.
            float c1 = Poly2Cross(q.A, q.B, p), c2 = Poly2Cross(q.B, q.C, p), c3 = Poly2Cross(q.C, q.D, p), c4 = Poly2Cross(q.D, q.A, p);
            bool inside = (c1 >= 0 && c2 >= 0 && c3 >= 0 && c4 >= 0) || (c1 <= 0 && c2 <= 0 && c3 <= 0 && c4 <= 0);
            if (inside) return true;
            float m = q.Margin + extra;
            float m2 = m * m;
            return Seg.DistSq(p, q.A, q.B, out _) <= m2 || Seg.DistSq(p, q.B, q.C, out _) <= m2
                || Seg.DistSq(p, q.C, q.D, out _) <= m2 || Seg.DistSq(p, q.D, q.A, out _) <= m2;
        }

        private static float Poly2Cross(float2 a, float2 b, float2 p) => (b.x - a.x) * (p.y - a.y) - (b.y - a.y) * (p.x - a.x);

        /// <summary>Marks blocked alive edges removed; returns how many. <paramref name="halfWidth"/>: the edge's body (s34).</summary>
        public int RemoveBlocked(RoadNetwork net, Predicate<RoadEdge> eligible = null, Func<RoadEdge, float> halfWidth = null)
        {
            if (IsEmpty) return 0;
            int removed = 0;
            foreach (var e in net.Edges)
            {
                if (!e.Alive || (eligible != null && !eligible(e))) continue;
                if (Blocked(e.Curve, halfWidth != null ? halfWidth(e) : 0f)) { e.Removed = true; removed++; }
            }
            return removed;
        }
    }
}
