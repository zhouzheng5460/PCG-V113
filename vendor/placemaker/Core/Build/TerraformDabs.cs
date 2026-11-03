// Placemaker — brush dab placement for terraforming (GPL-3.0).
// Adapted from ParkManager's ParkLakePlanner.ExcavationDabs/WaterAnchor (tomkpunkt, GPL-3.0).

using System.Collections.Generic;
using Unity.Mathematics;

namespace Placemaker.Core.Build
{
    public static class TerraformDabs
    {
        /// <summary>
        /// Brush centres covering the ring's interior on a grid (spacing 0.6 r), kept at least
        /// inset×r from the shore so the brush falloff shapes a bank instead of cutting outside.
        /// Falls back to a smaller inset for thin shapes; never returns null.
        /// </summary>
        public static List<float2> Dabs(float2[] ring, float brushRadius)
        {
            var result = new List<float2>();
            if (ring == null || ring.Length < 3 || !(brushRadius > 0.5f)) return result;
            float2 min = ring[0], max = ring[0];
            foreach (var p in ring) { min = math.min(min, p); max = math.max(max, p); }
            float spacing = brushRadius * 0.6f;
            foreach (var inset in new[] { 0.7f, 0.4f, 0f })
            {
                float minimum = brushRadius * inset;
                for (float y = min.y + spacing * 0.5f; y < max.y; y += spacing)
                    for (float x = min.x + spacing * 0.5f; x < max.x; x += spacing)
                    {
                        var p = new float2(x, y);
                        if (ReservedSpace.PointInPolygon(p, ring) && DistanceToBoundary(ring, p) >= minimum) result.Add(p);
                    }
                if (result.Count > 0) break;
            }
            if (result.Count == 0) result.Add(Anchor(ring, out _));
            return result;
        }

        /// <summary>Deepest-inside point (grid search) and its distance to the boundary.</summary>
        public static float2 Anchor(float2[] ring, out float innerRadius)
        {
            innerRadius = 0;
            if (ring == null || ring.Length == 0) return 0;
            float2 min = ring[0], max = ring[0], centroid = 0;
            foreach (var p in ring) { min = math.min(min, p); max = math.max(max, p); centroid += p; }
            centroid /= ring.Length;
            float2 best = centroid;
            float bestD = ReservedSpace.PointInPolygon(centroid, ring) ? DistanceToBoundary(ring, centroid) : -1;
            const int steps = 24;
            for (int i = 0; i <= steps; i++)
                for (int j = 0; j <= steps; j++)
                {
                    var p = math.lerp(min, max, new float2(i / (float)steps, j / (float)steps));
                    if (!ReservedSpace.PointInPolygon(p, ring)) continue;
                    float d = DistanceToBoundary(ring, p);
                    if (d > bestD) { bestD = d; best = p; }
                }
            innerRadius = math.max(0, bestD);
            return best;
        }


        public struct Dab { public float2 P; public float Target; }

        /// <summary>
        /// Dabs for every terraform polygon of a composed plan. A feature that levels its own pad inside
        /// a terraformed parent (sports pad on a hill, play mound in a terraced park) plans on the
        /// parent's new ground; both target lists used to be dabbed at once and fought until the build
        /// timed out. Later layers win: a dab of an earlier layer is dropped where it lies inside, or
        /// within half a brush radius of, a polygon of a later layer.
        /// </summary>
        /// <param name="items">(ring, target, layer index) in plan order; NaN targets are skipped.</param>
        public static List<Dab> Merge(IReadOnlyList<(float2[] ring, float target, int layer)> items, float brushRadius)
        {
            var result = new List<Dab>();
            if (items == null) return result;
            int maxLayer = int.MinValue;
            foreach (var it in items) if (it.ring != null && it.ring.Length >= 3 && math.isfinite(it.target)) maxLayer = math.max(maxLayer, it.layer);
            float margin = brushRadius * 0.5f;
            // Bucket grid of the polygons for the "covered by a later layer" test.
            float cell = math.max(8f, brushRadius * 2f);
            var grid = new Dictionary<long, List<int>>();
            var boxes = new (float2 min, float2 max)[items.Count];
            for (int i = 0; i < items.Count; i++)
            {
                var r = items[i].ring;
                if (r == null || r.Length < 3 || !math.isfinite(items[i].target)) continue;
                float2 min = r[0], max = r[0];
                foreach (var p in r) { min = math.min(min, p); max = math.max(max, p); }
                min -= margin; max += margin;
                boxes[i] = (min, max);
                int x0 = (int)math.floor(min.x / cell), x1 = (int)math.floor(max.x / cell);
                int y0 = (int)math.floor(min.y / cell), y1 = (int)math.floor(max.y / cell);
                if ((long)(x1 - x0 + 1) * (y1 - y0 + 1) > 4096) { Bucket(grid, long.MinValue, i); continue; }
                for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++) Bucket(grid, Key(x, y), i);
            }
            bool CoveredLater(float2 p, int layer)
            {
                if (layer >= maxLayer) return false;
                foreach (var key in new[] { Key((int)math.floor(p.x / cell), (int)math.floor(p.y / cell)), long.MinValue })
                {
                    if (!grid.TryGetValue(key, out var list)) continue;
                    foreach (int j in list)
                    {
                        if (items[j].layer <= layer) continue;
                        var b = boxes[j];
                        if (p.x < b.min.x || p.y < b.min.y || p.x > b.max.x || p.y > b.max.y) continue;
                        var ring = items[j].ring;
                        if (ReservedSpace.PointInPolygon(p, ring) || DistanceToBoundary(ring, p) < margin) return true;
                    }
                }
                return false;
            }
            for (int i = 0; i < items.Count; i++)
            {
                var it = items[i];
                if (it.ring == null || it.ring.Length < 3 || !math.isfinite(it.target)) continue;
                foreach (var p in Dabs(it.ring, brushRadius))
                    if (!CoveredLater(p, it.layer)) result.Add(new Dab { P = p, Target = it.target });
            }
            return result;
        }

        private static long Key(int x, int y) => ((long)x << 32) ^ (uint)y;

        private static void Bucket(Dictionary<long, List<int>> grid, long key, int i)
        {
            if (!grid.TryGetValue(key, out var list)) grid[key] = list = new List<int>(2);
            list.Add(i);
        }

        public static float DistanceToBoundary(float2[] ring, float2 p)
        {
            float best = float.MaxValue;
            for (int i = 0; i < ring.Length; i++)
                best = math.min(best, math.sqrt(ReservedSpace.DistSqToSegment(p, ring[i], ring[(i + 1) % ring.Length])));
            return best;
        }
    }
}
