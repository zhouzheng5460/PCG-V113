// Placemaker roads layer — tree (cul-de-sac) pattern.
// Adapted from Grid Road Generator (Ti4goc), Core/GridGenerator.Tree.cs; part of Placemaker (GPL-3.0).
// Changes for Placemaker: no static caches or global lock (thread-safe, reentrant), cooperative
// cancellation through GridParameters.Cancel, per-call diagnostics objects instead of statics.
using System.Collections.Generic;
using Unity.Mathematics;

namespace Placemaker.Core.Layers.Roads.Generators
{
    /// <summary>
    /// Tree cul-de-sac pattern (grid family, LoopMode false): a collector (IsAvenue) along the
    /// shape's long axis, joined to the perimeter at both ends; perpendicular branches (main
    /// network) on both sides, offset by half a spacing (T junctions) and never reaching the
    /// perimeter; cul-de-sacs (IsCulDeSacEnd: cul-de-sac network and turning circle) in pairs
    /// along each branch. A branch ends at its last cul-de-sac pair. All traffic goes through the
    /// collector.
    /// </summary>
    public static partial class GridGenerator
    {
        public const float MinTreeBranchSpacing = 160f;
        public const float MaxTreeBranchSpacing = 400f;
        public const float TreeBranchSpacingDefault = 240f;
        public const float MinTreeCulDeSacSpacing = 60f;
        public const float MaxTreeCulDeSacSpacing = 150f;
        public const float TreeCulDeSacSpacingDefault = 80f;
        public const float MinTreeCulDeSacLength = 40f;
        public const float MaxTreeCulDeSacLength = 150f;
        public const float TreeCulDeSacLengthDefault = 90f;

        /// <summary>Minimum distance (m) between a generated street and the perimeter road, junctions excepted (same value as the radial pattern).</summary>
        private const float TreeClearance = 25f;
        /// <summary>Distance (m) between a street tip and the perimeter: room for the turning circle.</summary>
        private const float TreeTipClearance = 30f;
        /// <summary>Gap (m) between the tips of two facing cul-de-sacs (neighbouring branches).</summary>
        private const float TreeFacingTipGap = 40f;
        private const float TreeSampleStep = 6f;

        private static float TreeSetting(float value, float fallback, float min, float max) =>
            math.clamp(value > 0f ? value : fallback, min, max);

        private static List<RoadSegmentDef> GenerateTree(List<float2> polygon, float y, GridParameters parameters)
        {
            var segments = new List<RoadSegmentDef>();
            float branchSpacing = TreeSetting(parameters.TreeBranchSpacing, TreeBranchSpacingDefault, MinTreeBranchSpacing, MaxTreeBranchSpacing);
            float culDeSacSpacing = TreeSetting(parameters.TreeCulDeSacSpacing, TreeCulDeSacSpacingDefault, MinTreeCulDeSacSpacing, MaxTreeCulDeSacSpacing);
            float culDeSacLength = TreeSetting(parameters.TreeCulDeSacLength, TreeCulDeSacLengthDefault, MinTreeCulDeSacLength, MaxTreeCulDeSacLength);
            // Cul-de-sacs of two neighbouring branches (same side, branchSpacing apart) face each other.
            culDeSacLength = math.min(culDeSacLength, 0.5f * (branchSpacing - TreeFacingTipGap));

            // Frame: u along the long axis (collector), v across it.
            float2 axis = LongestAxis(polygon, parameters.AngleOffsetDegrees);
            // (s34) Follow contours: on a hillside (not a round hill) the collector runs along the slope, the branches climb off it.
            if (parameters.FollowContours && parameters.HeightAt != null && TryContourDirection(polygon, parameters.HeightAt, out float2 contour, out float aniso) && aniso > 0.35f)
            {
                float a = math.atan2(contour.y, contour.x) + math.radians(parameters.AngleOffsetDegrees);
                axis = new float2(math.cos(a), math.sin(a));
            }
            float2 side = new float2(-axis.y, axis.x);
            float2 origin = polygon[0];
            var local = new List<float2>(polygon.Count);
            float vMin = float.MaxValue, vMax = float.MinValue;
            foreach (float2 p in polygon)
            {
                var lp = new float2(math.dot(p - origin, axis), math.dot(p - origin, side));
                local.Add(lp);
                vMin = math.min(vMin, lp.y);
                vMax = math.max(vMax, lp.y);
            }

            // Collector: the line (v = const) with the longest unbroken run through the shape, within
            // the central half; on a tie, the most central one.
            float mid = 0.5f * (vMin + vMax);
            float height = vMax - vMin;
            float spineV = mid;
            float2 spine = default;
            float bestScore = float.MinValue;
            const int candidates = 40;
            for (int k = 0; k <= candidates; k++)
            {
                float v = math.lerp(vMin + 0.25f * height, vMax - 0.25f * height, (float)k / candidates);
                foreach (float2 interval in ClipLineToPolygon(local, axisIsU: false, position: v))
                {
                    float score = (interval.y - interval.x) - 0.5f * math.abs(v - mid);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        spineV = v;
                        spine = interval;
                    }
                }
            }
            if (bestScore == float.MinValue || spine.y - spine.x < 2f * MinTreeCulDeSacSpacing)
            {
                return segments;
            }

            // Branches: one side every branchSpacing, the other offset by half a spacing (T junctions).
            float margin = math.max(0.5f * branchSpacing, TreeClearance + culDeSacLength);
            var branches = new List<(float u, float dir)>();
            float usable = spine.y - spine.x - 2f * margin;
            if (usable >= 0f)
            {
                int count = (int)math.floor(usable / branchSpacing) + 1;
                float first = 0.5f * (spine.x + spine.y) - 0.5f * (count - 1) * branchSpacing;
                for (int k = 0; k < count; k++)
                {
                    branches.Add((first + k * branchSpacing, 1f));
                }
                for (int k = 0; k <= count; k++)
                {
                    float u = first + (k - 0.5f) * branchSpacing;
                    if (u >= spine.x + margin - 1e-3f && u <= spine.y - margin + 1e-3f)
                    {
                        branches.Add((u, -1f));
                    }
                }
            }

            var edges = new TreeEdgeIndex(local);
            var collectorJunctions = new SortedDictionary<float, float3>();
            float3 World(float u, float v) => ToWorld(origin, axis, side, u, v, y);

            foreach ((float u, float dir) in branches)
            {
                // Usable branch length: up to the perimeter (clear band), minus the margin.
                if (!TryFreeRun(edges, new float2(u, spineV), new float2(0f, dir), out float branchRun))
                {
                    continue;
                }
                float maxBranch = branchRun - TreeTipClearance;

                // Cul-de-sac pairs along the branch.
                var junctions = new List<float3>();
                var culDeSacs = new List<RoadSegmentDef>();
                for (float t = culDeSacSpacing; t <= maxBranch + 1e-3f; t += culDeSacSpacing)
                {
                    var at = new float2(u, spineV + dir * t);
                    float3 junction = World(at.x, at.y);
                    bool any = false;
                    foreach (float along in new[] { 1f, -1f })
                    {
                        if (!TryFreeRun(edges, at, new float2(along, 0f), out float run))
                        {
                            continue;
                        }
                        float length = math.min(culDeSacLength, run - TreeTipClearance);
                        if (length < MinTreeCulDeSacLength)
                        {
                            continue;
                        }
                        culDeSacs.Add(new RoadSegmentDef(junction, World(at.x + along * length, at.y), isHorizontal: true, isCulDeSacEnd: true));
                        any = true;
                    }
                    if (any)
                    {
                        junctions.Add(junction);
                    }
                }
                if (junctions.Count == 0)
                {
                    continue; // branch without cul-de-sacs: nothing to serve
                }

                // Branch: from the collector to its last cul-de-sac pair, split at each pair.
                if (!collectorJunctions.TryGetValue(u, out float3 start))
                {
                    start = World(u, spineV);
                    collectorJunctions[u] = start;
                }
                float3 previous = start;
                foreach (float3 junction in junctions)
                {
                    segments.Add(new RoadSegmentDef(previous, junction, isHorizontal: false));
                    previous = junction;
                }
                segments.AddRange(culDeSacs);
            }

            // Collector: end to end, split at each branch.
            var stops = new List<float3> { World(spine.x, spineV) };
            stops.AddRange(collectorJunctions.Values);
            stops.Add(World(spine.y, spineV));
            for (int i = 0; i + 1 < stops.Count; i++)
            {
                segments.Add(new RoadSegmentDef(stops[i], stops[i + 1], isHorizontal: true, isAvenue: true));
            }
            return segments;
        }

        /// <summary>
        /// Outline edges bucketed into TreeClearance-wide slices on each of the two axes, so a query
        /// along a line (u = const or v = const) only checks the edges in its band (user feedback
        /// "extremely slow" on the old fishbone pattern: scanning all ~1000 edges for every street cost
        /// hundreds of ms per generation).
        /// </summary>
        private sealed class TreeEdgeIndex
        {
            private readonly List<float2> _local;
            private readonly Dictionary<int, List<int>>[] _buckets = { new Dictionary<int, List<int>>(), new Dictionary<int, List<int>>() };

            public TreeEdgeIndex(List<float2> local)
            {
                _local = local;
                for (int i = 0; i < local.Count; i++)
                {
                    float2 a = local[i], b = local[(i + 1) % local.Count];
                    for (int axis = 0; axis < 2; axis++)
                    {
                        int lo = Bucket(math.min(a[axis], b[axis]));
                        int hi = Bucket(math.max(a[axis], b[axis]));
                        for (int k = lo; k <= hi; k++)
                        {
                            if (!_buckets[axis].TryGetValue(k, out List<int> list))
                            {
                                _buckets[axis][k] = list = new List<int>();
                            }
                            list.Add(i);
                        }
                    }
                }
            }

            private static int Bucket(float value) => (int)math.floor(value / TreeClearance);

            public float2 A(int edge) => _local[edge];
            public float2 B(int edge) => _local[(edge + 1) % _local.Count];

            /// <summary>Edges whose extent on axis `axis` (0 = u, 1 = v) touches [position - margin, position + margin].</summary>
            public List<int> Near(int axis, float position, float margin)
            {
                var result = new List<int>();
                var seen = new HashSet<int>();
                for (int k = Bucket(position - margin); k <= Bucket(position + margin); k++)
                {
                    if (!_buckets[axis].TryGetValue(k, out List<int> list))
                    {
                        continue;
                    }
                    foreach (int edge in list)
                    {
                        float2 a = A(edge), b = B(edge);
                        if (math.min(a[axis], b[axis]) <= position + margin && math.max(a[axis], b[axis]) >= position - margin && seen.Add(edge))
                        {
                            result.Add(edge);
                        }
                    }
                }
                return result;
            }
        }

        /// <summary>
        /// Street length (m) available from `from` in local-axis direction `direction`: up to where
        /// the line leaves the shape or the first point closer than TreeClearance to the perimeter.
        /// False if `from` is not inside the shape.
        /// </summary>
        private static bool TryFreeRun(TreeEdgeIndex edges, float2 from, float2 direction, out float run)
        {
            run = 0f;
            bool alongU = direction.x != 0f;
            int across = alongU ? 1 : 0; // axis held constant along the line
            int along = 1 - across;
            float position = from[across];
            float t0 = from[along];
            float sign = direction[along];
            List<int> near = edges.Near(across, position, TreeClearance);

            // Shape exit: crossings of the line with the edges (half-open rule, as in
            // ClipLineToPolygon), then parity tells whether `from` is inside.
            var crossings = new List<float>();
            foreach (int edge in near)
            {
                float2 a = edges.A(edge), b = edges.B(edge);
                float c1 = a[across], c2 = b[across];
                if (!((c1 <= position && position < c2) || (c2 <= position && position < c1)))
                {
                    continue;
                }
                float f = (position - c1) / (c2 - c1);
                crossings.Add(a[along] + f * (b[along] - a[along]));
            }
            int before = 0;
            float end = sign > 0f ? float.MaxValue : float.MinValue;
            foreach (float c in crossings)
            {
                if (c < t0) before++;
                if (sign > 0f && c > t0) end = math.min(end, c);
                if (sign < 0f && c < t0) end = math.max(end, c);
            }
            if (before % 2 == 0 || end == float.MaxValue || end == float.MinValue)
            {
                return false; // `from` outside the shape
            }
            float available = math.abs(end - t0);
            run = available;
            // Each edge's extent along the line, widened by TreeClearance: a sample outside that
            // extent cannot be too close to the edge (cheap test first).
            var lo = new float[near.Count];
            var hi = new float[near.Count];
            for (int i = 0; i < near.Count; i++)
            {
                float2 a = edges.A(near[i]), b = edges.B(near[i]);
                lo[i] = math.min(a[along], b[along]) - TreeClearance;
                hi[i] = math.max(a[along], b[along]) + TreeClearance;
            }
            for (float t = TreeSampleStep; t < available; t += TreeSampleStep)
            {
                float2 q = from + direction * t;
                float qa = q[along];
                for (int i = 0; i < near.Count; i++)
                {
                    if (qa < lo[i] || qa > hi[i])
                    {
                        continue;
                    }
                    if (math.distancesq(ClosestOnSegment(edges.A(near[i]), edges.B(near[i]), q), q) < TreeClearance * TreeClearance)
                    {
                        run = t;
                        return true;
                    }
                }
            }
            return true;
        }

        private static float2 ClosestOnSegment(float2 a, float2 b, float2 p)
        {
            float2 ab = b - a;
            float lengthSq = math.lengthsq(ab);
            float t = lengthSq < 1e-9f ? 0f : math.saturate(math.dot(p - a, ab) / lengthSq);
            return a + t * ab;
        }

        /// <summary>
        /// Long axis of the shape, rotated by angleOffsetDegrees: the direction along which the shape
        /// is NARROWEST across (minimum-width band), not the direction of greatest extent, which for a
        /// rectangle would be the diagonal.
        /// </summary>
        private static float2 LongestAxis(List<float2> polygon, float angleOffsetDegrees)
        {
            float bestAngle = 0f;
            float bestWidth = float.MaxValue;
            for (int tenths = 0; tenths < 1800; tenths += 5)
            {
                float a = math.radians(tenths * 0.1f);
                var across = new float2(-math.sin(a), math.cos(a));
                float lo = float.MaxValue, hi = float.MinValue;
                foreach (float2 p in polygon)
                {
                    float t = math.dot(p, across);
                    lo = math.min(lo, t);
                    hi = math.max(hi, t);
                }
                if (hi - lo < bestWidth - 1e-3f)
                {
                    bestWidth = hi - lo;
                    bestAngle = a;
                }
            }
            float angle = bestAngle + math.radians(angleOffsetDegrees);
            return new float2(math.cos(angle), math.sin(angle));
        }
    }
}
