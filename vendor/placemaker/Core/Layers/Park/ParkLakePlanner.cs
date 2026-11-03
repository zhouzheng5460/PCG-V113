// Ported from ParkManager (tomkpunkt, GPL-3.0), Geometry/ParkLakePlanner.cs.
// Finds the largest open space between paths and shapes an organic lake there: rays cast from
// the most open point stop at a shore strip, are smoothed (never beyond a ray's free length) and
// rejected when only a sliver is free. Changes: clearance queries go through the path segment
// index; the protected clearing is not taken by the lake (it is chosen first, on dry land).

using System;
using System.Collections.Generic;
using Placemaker.Core.Geometry;
using Placemaker.Core.Layers.Common;
using Unity.Mathematics;

namespace Placemaker.Core.Layers.Park
{
    public static class ParkLakePlanner
    {
        public const float ShoreMargin = 8f;
        private const float MinimumRadius = 10f;
        private const float MaximumRadius = 70f;
        private const int OutlineVertices = 48;
        private const float RayStep = 1f;

        /// <param name="sizeFactor">0.2..1 of the free space the lake may take.</param>
        public static float2[] Generate(LayerRegion region, SegmentIndex paths, IReadOnlyList<Gate> gates, float sizeFactor, uint seed)
        {
            float area = region.Area;
            float step = math.max(3f, math.sqrt(area) / 80f);
            var center = float2.zero;
            float bestClearance = 0;
            for (float y = region.Min.y + step * 0.5f; y < region.Max.y; y += step)
                for (float x = region.Min.x + step * 0.5f; x < region.Max.x; x += step)
                {
                    var p = new float2(x, y);
                    float c = Clearance(p, region, paths, gates, MaximumRadius * 2f);
                    if (c <= bestClearance) continue;
                    bestClearance = c; center = p;
                }
            float free = bestClearance - ShoreMargin;
            if (free < MinimumRadius) return Array.Empty<float2>();

            var rng = Hashing.Rng(seed, 0x2545f491u);
            float target = math.min(free * 1.35f, math.min(MaximumRadius, math.sqrt(area) * 0.25f)) * math.clamp(sizeFactor, 0.2f, 1f);
            target = math.max(target, MinimumRadius);
            float aspect = rng.NextFloat(0.6f, 1f), rotation = rng.NextFloat(0f, math.PI);
            int lobes = rng.NextInt(2, 4), fineLobes = rng.NextInt(4, 7);
            float phase = rng.NextFloat(0f, math.PI * 2f), finePhase = rng.NextFloat(0f, math.PI * 2f);

            var limits = new float[OutlineVertices];
            var radii = new float[OutlineVertices];
            for (int i = 0; i < OutlineVertices; i++)
            {
                float angle = i * math.PI * 2f / OutlineVertices;
                float local = angle - rotation;
                float ellipse = 1f / math.sqrt(math.square(math.cos(local)) + math.square(math.sin(local) / aspect));
                float desired = target * ellipse * (1f + 0.18f * math.sin(lobes * angle + phase) + 0.08f * math.sin(fineLobes * angle + finePhase));
                var dir = new float2(math.cos(angle), math.sin(angle));
                float reach = 0;
                while (reach + RayStep <= desired && Clearance(center + dir * (reach + RayStep), region, paths, gates, ShoreMargin + 0.01f) >= ShoreMargin)
                    reach += RayStep;
                limits[i] = reach; radii[i] = reach;
            }
            // Smooth the ray lengths so obstacles bend the shore instead of notching it.
            for (int pass = 0; pass < 3; pass++)
            {
                var smoothed = new float[OutlineVertices];
                for (int i = 0; i < OutlineVertices; i++)
                {
                    float sum = 0, weight = 0;
                    for (int k = -2; k <= 2; k++)
                    {
                        float w = 3f - math.abs(k);
                        sum += radii[(i + k + OutlineVertices) % OutlineVertices] * w; weight += w;
                    }
                    smoothed[i] = math.min(sum / weight, limits[i]);
                }
                radii = smoothed;
            }
            var lake = new List<float2>(OutlineVertices);
            for (int i = 0; i < OutlineVertices; i++)
            {
                float angle = i * math.PI * 2f / OutlineVertices;
                lake.Add(center + new float2(math.cos(angle), math.sin(angle)) * radii[i]);
            }
            var clean = Poly.Clean(lake, 0.3f);
            if (clean.Length < 3 || Poly.Area(clean) < math.PI * MinimumRadius * MinimumRadius * 0.6f || !Poly.IsSimple(clean))
                return Array.Empty<float2>();
            return clean;
        }

        /// <summary>Deepest point of the lake (water source position) and its distance to the shore.</summary>
        public static float2 WaterAnchor(float2[] lake, out float innerRadius)
        {
            var r = LayerRegion.From(lake);
            if (r == null) { innerRadius = 0; return lake.Length > 0 ? lake[0] : float2.zero; }
            return r.DeepestPoint(out innerRadius, null, 16);
        }

        private static float Clearance(float2 p, LayerRegion region, SegmentIndex paths, IReadOnlyList<Gate> gates, float cap)
        {
            if (!region.Contains(p)) return 0f;
            float c = region.BoundaryDistance(p, cap);
            foreach (var g in gates) c = math.min(c, math.distance(p, g.Position));
            return math.min(c, paths.Clearance(p, c));
        }
    }
}
