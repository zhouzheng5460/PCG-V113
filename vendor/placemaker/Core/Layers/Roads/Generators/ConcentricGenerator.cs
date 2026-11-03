// Placemaker roads layer — concentric and radial patterns.
// Adapted from Grid Road Generator (Ti4goc), Core/ConcentricGenerator.cs; part of Placemaker (GPL-3.0).
// Changes for Placemaker: no static caches or global lock (thread-safe, reentrant), cooperative
// cancellation through GridParameters.Cancel, per-call diagnostics objects instead of statics.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Unity.Mathematics;

namespace Placemaker.Core.Layers.Roads.Generators
{
    /// <summary>
    /// Concentric mode (user feedback: "a mode that copies the outer shape inwards, where we can
    /// choose the number of layers"): N rings of roads that follow the shape of the selected
    /// perimeter, linked to each other and to the perimeter by short spokes.
    ///
    /// Why not an edge-by-edge offset (the old Adaptive mode, removed): offsetting each edge and
    /// re-joining the corners (miter join) self-intersects as soon as a corner is concave, a narrow
    /// part closes up or the shape splits in two — each case needed its own guard, and there was
    /// always one left. Here each ring is the level curve "distance to perimeter = k × spacing" of
    /// a distance field computed on a grid (marching squares): by construction it is the true
    /// inward offset, which can neither cross itself nor leave the perimeter, keeps sharp corners
    /// at convex corners, rounds off in concave corners, and splits/disappears naturally when the
    /// shape becomes too narrow.
    ///
    /// Spacing is not a setting: it is computed so that the N rings fill the shape up to its centre
    /// (maximum depth / (N + 1)), while respecting MinLayerSpacing.
    /// </summary>
    public static class ConcentricGenerator
    {
        public const int MinLayers = 1;
        public const int MaxLayersLimit = 10;
        public const int MinConnections = 2;
        public const int MaxConnections = 12;

        /// <summary>
        /// Minimum gap (m) between two neighbouring rings, measured at the tightest point (rings
        /// squeeze together in narrow parts of the shape) — a narrower strip would no longer be
        /// buildable. Determines the maximum number of rings (see MaxLayers).
        /// </summary>
        public const float MinLayerSpacing = 40f;

        /// <summary>Distance-field cell sizes (m): fine on a small area, capped on a large one to stay fast.</summary>
        private const float MinCellSize = 1.5f;
        private const float MaxCellSize = 8f;
        private const int TargetCellsPerAxis = 220;

        public static List<RoadSegmentDef> Generate(IReadOnlyList<float3> selectedNodePositions, int layers, int connections,
            ConcentricDiagnostics diagnostics = null, CancellationToken cancel = default)
        {
            if (selectedNodePositions == null || selectedNodePositions.Count < 2)
            {
                return new List<RoadSegmentDef>();
            }
            layers = math.clamp(layers, MinLayers, MaxLayersLimit);
            connections = math.clamp(connections, MinConnections, MaxConnections);

            return GenerateUncached(selectedNodePositions, layers, connections, diagnostics, cancel);
        }

        private static List<RoadSegmentDef> GenerateUncached(IReadOnlyList<float3> nodes, int layers, int connections,
            ConcentricDiagnostics diagnostics, CancellationToken cancel)
        {
            var segments = new List<RoadSegmentDef>();
            if (!TryGetField(nodes, out DistanceField field, out List<float2> polygon, out float y))
            {
                return segments;
            }

            // Never more rings than the shape allows (see MaxLayersFor) — the panel already caps
            // the slider at this limit; this covers a higher saved value.
            int layerCount = math.min(layers, MaxLayersFor(field, polygon, cancel));
            if (layerCount < 1)
            {
                return segments;
            }
            var corners = new HashSet<(float, float)>();
            List<List<List<float2>>> levels = BuildLevels(field, polygon, layerCount, corners, out float spacing);
            if (levels == null)
            {
                return segments;
            }

            if (diagnostics != null)
            {
                diagnostics.Rings = levels.GetRange(1, levels.Count - 1);
                diagnostics.RingLoopCounts = new int[levels.Count - 1];
                for (int k = 1; k < levels.Count; k++)
                {
                    diagnostics.RingLoopCounts[k - 1] = levels[k].Count;
                }
            }
            cancel.ThrowIfCancellationRequested();

            EmitRadialChains(segments, levels, connections, spacing, y, cancel);

            for (int k = 1; k < levels.Count; k++)
            {
                foreach (List<float2> loop in levels[k])
                {
                    EmitSmoothLoop(segments, loop, corners, y);
                }
            }
            return segments;
        }

        /// <summary>
        /// Maximum number of rings for this perimeter (user feedback: "there has to be a ring limit
        /// depending on the perimeter size, and the panel simply blocks beyond the limit"): the
        /// largest N for which, even at the tightest point, two neighbouring rings stay at least
        /// MinLayerSpacing apart. 0 if the shape is too small for a single ring. Memoised in
        /// DistanceField.MaxLayersCache, i.e. per field built for this call (there is no static
        /// cache).
        /// </summary>
        public static int MaxLayers(IReadOnlyList<float3> selectedNodePositions)
        {
            if (selectedNodePositions == null || selectedNodePositions.Count < 2)
            {
                return MaxLayersLimit;
            }
            return TryGetField(selectedNodePositions, out DistanceField field, out List<float2> polygon, out float _)
                ? MaxLayersFor(field, polygon)
                : 0;
        }

        private static int MaxLayersFor(DistanceField field, List<float2> polygon, CancellationToken cancel = default)
        {
            if (field.MaxLayersCache >= 0)
            {
                return field.MaxLayersCache;
            }
            // Upper bound: the AVERAGE spacing must already reach MinLayerSpacing.
            int upper = math.min(MaxLayersLimit, (int)math.floor(field.RingDepth / MinLayerSpacing) - 1);
            int result = 0;
            for (int layerCount = upper; layerCount >= 1; layerCount--)
            {
                cancel.ThrowIfCancellationRequested();
                List<List<List<float2>>> levels = BuildLevels(field, polygon, layerCount, new HashSet<(float, float)>(), out float _);
                if (levels != null && levels.Count - 1 == layerCount && MinGapBetweenLevels(levels) >= MinLayerSpacing)
                {
                    result = layerCount;
                    break;
                }
            }
            field.MaxLayersCache = result;
            return result;
        }

        /// <summary>Distance field of the perimeter (built per call, not cached) and the cleaned polygon.</summary>
        private static bool TryGetField(IReadOnlyList<float3> nodes, out DistanceField field, out List<float2> polygon, out float y)
        {
            field = null;
            y = 0f;
            polygon = new List<float2>(nodes.Count);
            foreach (float3 p in nodes)
            {
                y += p.y;
                polygon.Add(p.xz);
            }
            y /= nodes.Count;

            if (polygon.Count == 2)
            {
                float2 mn = math.min(polygon[0], polygon[1]);
                float2 mx = math.max(polygon[0], polygon[1]);
                polygon = new List<float2> { mn, new float2(mx.x, mn.y), mx, new float2(mn.x, mx.y) };
            }
            polygon = CleanPolygon(polygon);
            if (polygon.Count < 3 || math.abs(SignedArea(polygon)) < 1f)
            {
                return false;
            }

            field = DistanceField.Build(polygon);
            return field.MaxDepth > 0f;
        }

        /// <summary>
        /// Rings = level curves of a smooth field that is 0 on the perimeter and 1 on the "spine"
        /// of the shape (see DistanceField.RingField): each ring goes round the whole shape without
        /// cutting itself or ending in a point, tighter in narrow parts and wider in wide parts.
        /// AVERAGE spacing = depth / (N+1). levels[0] = the perimeter itself. null if the N
        /// requested rings cannot be obtained.
        /// </summary>
        private static List<List<List<float2>>> BuildLevels(DistanceField field, List<float2> polygon, int layerCount,
            HashSet<(float, float)> corners, out float spacing)
        {
            spacing = field.RingDepth / (layerCount + 1);
            var levels = new List<List<List<float2>>> { new List<List<float2>> { polygon } };
            for (int k = 1; k <= layerCount; k++)
            {
                float level = field.LevelForDepth(k * spacing);
                var loops = new List<List<float2>>();
                foreach (List<float2> raw in field.ExtractContours(field.RingField, level))
                {
                    List<float2> loop = SmoothAndResample(raw, field, corners, spacing);
                    if (loop.Count < 3 || math.abs(SignedArea(loop)) < MinLayerSpacing * MinLayerSpacing)
                    {
                        continue; // scrap too small to hold a buildable zone
                    }
                    loops.Add(loop);
                }
                if (loops.Count == 0)
                {
                    return null;
                }
                levels.Add(loops);
            }
            return levels;
        }

        /// <summary>Smallest gap between a ring and the level just outside it (the perimeter for the first ring).</summary>
        private static float MinGapBetweenLevels(List<List<List<float2>>> levels)
        {
            float min = float.MaxValue;
            for (int k = 1; k < levels.Count; k++)
            {
                foreach (List<float2> loop in levels[k])
                {
                    for (int i = 0; i < loop.Count; i++)
                    {
                        if (NearestOnLevel(levels[k - 1], loop[i], out int _, out float2 q))
                        {
                            min = math.min(min, math.distance(loop[i], q));
                        }
                    }
                }
            }
            return min;
        }

        /// <summary>Minimum turn (degrees) for a point to be a corner — and only where the perimeter itself has a sharp corner (see DistanceField.SharpWeightAt).</summary>
        private const float CornerTurnDegrees = 30f;

        /// <summary>Slack (degrees) between the turn of a ring corner and the turn of the perimeter corner it inherits from.</summary>
        private const float CornerTurnSlackDegrees = 20f;

        /// <summary>Target distance (m) between two vertices of a curved ring after resampling.</summary>
        private const float RingSampleStep = 25f;

        /// <summary>
        /// Turns the raw level curve (one point per grid cell, stair-stepped) into a smooth ring:
        /// finds the REAL corners (strong turn AND in the wake of a sharp perimeter corner),
        /// smooths everything else, then resamples at a regular step between corners. Replaces the
        /// old Douglas-Peucker, which left irregularly spaced vertices: on a tight curve the angle
        /// between two chords exceeded the corner threshold and the ring stayed broken there (user
        /// feedback: "the curves lack fluidity"). Retained corners are added to `corners` (exact
        /// positions of the returned vertices).
        /// </summary>
        private static List<float2> SmoothAndResample(List<float2> raw, DistanceField field, HashSet<(float, float)> corners, float spacing)
        {
            int n = raw.Count;
            if (n < 6)
            {
                return new List<float2>(raw);
            }

            // Turn measured over a window of a few metres (the raw points are too close together
            // and stair-stepped for a point-to-point angle).
            int window = math.max(2, (int)math.round(6f / field.CellSize));
            window = math.min(window, n / 4);
            var turn = new float[n];
            for (int i = 0; i < n; i++)
            {
                float2 dIn = math.normalizesafe(raw[i] - raw[(i - window + n) % n]);
                float2 dOut = math.normalizesafe(raw[(i + window) % n] - raw[i]);
                turn[i] = math.degrees(math.acos(math.clamp(math.dot(dIn, dOut), -1f, 1f)));
            }
            var isCorner = new bool[n];
            int cornerCount = 0;
            for (int i = 0; i < n; i++)
            {
                // An inherited corner turns as much as the perimeter corner it comes from (a square
                // gives square rings). User feedback (persistent spikes, real perimeter replayed in
                // a test): the influence zone of an OBTUSE perimeter corner (two streets crossing
                // at 150°) covers a wide band down to the deepest part of the shape, and ring
                // spikes of 110-128° lying in it were kept as "corners", so never rounded
                // (LimitCurvature leaves corners fixed).
                if (turn[i] < CornerTurnDegrees || field.SharpWeightAt(raw[i]) < 0.5f
                    || turn[i] > field.SharpTurnAt(raw[i]) + CornerTurnSlackDegrees)
                {
                    continue;
                }
                bool localMax = true;
                for (int o = -window; o <= window && localMax; o++)
                {
                    int j = (i + o + n) % n;
                    if (o != 0 && (turn[j] > turn[i] || (turn[j] == turn[i] && isCorner[j])))
                    {
                        localMax = false;
                    }
                }
                if (localMax)
                {
                    isCorner[i] = true;
                    cornerCount++;
                }
            }

            // Laplacian smoothing (corners pinned): removes the marching-squares staircase.
            var points = new List<float2>(raw);
            var next = new float2[n];
            for (int pass = 0; pass < 8; pass++)
            {
                for (int i = 0; i < n; i++)
                {
                    next[i] = isCorner[i]
                        ? points[i]
                        : 0.25f * points[(i - 1 + n) % n] + 0.5f * points[i] + 0.25f * points[(i + 1) % n];
                }
                for (int i = 0; i < n; i++)
                {
                    points[i] = next[i];
                }
            }

            // Resample at a regular step, segment by segment between two corners (or the whole
            // ring).
            int startIndex = 0;
            if (cornerCount > 0)
            {
                while (!isCorner[startIndex])
                {
                    startIndex++;
                }
            }
            var result = new List<float2>();
            int runStart = startIndex;
            int visited = 0;
            while (visited < n)
            {
                // Segment [runStart .. next corner (or back to the start)].
                var run = new List<float2> { points[runStart] };
                int idx = runStart;
                do
                {
                    idx = (idx + 1) % n;
                    run.Add(points[idx]);
                    visited++;
                }
                while (idx != startIndex && !isCorner[idx]);

                float runLength = 0f;
                for (int i = 0; i + 1 < run.Count; i++)
                {
                    runLength += math.distance(run[i], run[i + 1]);
                }
                int intervals = math.max(1, (int)math.ceil(runLength / RingSampleStep));
                if (cornerCount == 0)
                {
                    intervals = math.max(intervals, 8);
                }
                float step = runLength / intervals;
                result.Add(run[0]);
                if (isCorner[runStart])
                {
                    corners.Add((run[0].x, run[0].y));
                }
                float target = step;
                float walked = 0f;
                for (int i = 0; i + 1 < run.Count && result.Count < 100000; i++)
                {
                    float edge = math.distance(run[i], run[i + 1]);
                    while (walked + edge >= target - 1e-3f && target < runLength - step * 0.5f)
                    {
                        float t = edge > 1e-6f ? (target - walked) / edge : 0f;
                        result.Add(math.lerp(run[i], run[i + 1], t));
                        target += step;
                    }
                    walked += edge;
                }
                runStart = idx;
                if (idx == startIndex)
                {
                    break;
                }
            }

            LimitCurvature(result, corners, spacing * MinRingRadiusFactor);

            // Drop near-collinear vertices (straight sides): fewer useless nodes.
            for (int pass = 0; pass < 2; pass++)
            {
                for (int i = result.Count - 1; i >= 0 && result.Count > 4; i--)
                {
                    float2 p = result[i];
                    if (corners.Contains((p.x, p.y)))
                    {
                        continue;
                    }
                    float2 a = result[(i - 1 + result.Count) % result.Count];
                    float2 b = result[(i + 1) % result.Count];
                    float2 d1 = math.normalizesafe(p - a);
                    float2 d2 = math.normalizesafe(b - p);
                    if (math.dot(d1, d2) > math.cos(math.radians(0.5f)) && math.distance(a, b) < 400f)
                    {
                        result.RemoveAt(i);
                    }
                }
            }
            return result;
        }

        /// <summary>Minimum curvature radius of a ring (outside real corners), as a fraction of the average spacing between rings.</summary>
        private const float MinRingRadiusFactor = 0.5f;

        /// <summary>
        /// Rounds off the spikes that remain on a ring (user feedback: "there are still some spikes
        /// in other complex shapes"): when the perimeter turns in a wide curve (e.g. a U-shaped
        /// street) inside a shape wider than that curve's radius, a ring deeper than that radius
        /// closes into a spike — the former blur of the field in ComputeRingField only rounded over
        /// ~¼ of the depth, and the half-width fraction only helps where the shape itself narrows.
        /// Local Laplacian smoothing, repeated while a vertex turns tighter than `minRadius`
        /// (circle through its two neighbours): the spike recedes towards the inside of its curve
        /// (away from the perimeter) until it becomes an arc of that radius. Real corners
        /// (inherited from a sharp perimeter corner) stay fixed: a square stays square.
        /// </summary>
        internal static void LimitCurvature(List<float2> loop, HashSet<(float, float)> corners, float minRadius)
        {
            int n = loop.Count;
            if (n < 5 || minRadius <= 0f)
            {
                return;
            }
            var fixedPoint = new bool[n];
            for (int i = 0; i < n; i++)
            {
                fixedPoint[i] = corners.Contains((loop[i].x, loop[i].y));
            }
            float maxCurvature = 1f / minRadius;
            var move = new bool[n];
            var next = new float2[n];
            for (int pass = 0; pass < 400; pass++)
            {
                bool any = false;
                for (int i = 0; i < n; i++)
                {
                    move[i] = false;
                }
                for (int i = 0; i < n; i++)
                {
                    if (fixedPoint[i])
                    {
                        continue;
                    }
                    float2 a = loop[(i - 1 + n) % n];
                    float2 p = loop[i];
                    float2 b = loop[(i + 1) % n];
                    float2 dIn = math.normalizesafe(p - a);
                    float2 dOut = math.normalizesafe(b - p);
                    float cos = math.dot(dIn, dOut);
                    float sin = math.abs(dIn.x * dOut.y - dIn.y * dOut.x);
                    float chord = math.distance(a, b);
                    // Curvature of the circle through a, p, b: 2 sin(turn) / |ab|; beyond a 90°
                    // turn (cos < 0) it is a spike anyway.
                    bool tooSharp = cos < 0f || (chord > 1e-4f && 2f * sin / chord > maxCurvature);
                    if (tooSharp)
                    {
                        move[(i - 1 + n) % n] = true;
                        move[i] = true;
                        move[(i + 1) % n] = true;
                        any = true;
                    }
                }
                if (!any)
                {
                    return;
                }
                for (int i = 0; i < n; i++)
                {
                    next[i] = move[i] && !fixedPoint[i]
                        ? 0.5f * loop[i] + 0.25f * (loop[(i - 1 + n) % n] + loop[(i + 1) % n])
                        : loop[i];
                }
                for (int i = 0; i < n; i++)
                {
                    loop[i] = next[i];
                }
            }
        }

        /// <summary>
        /// Ring made of true curves (RoadSegmentDef.IsArc; the game fits the curve from the
        /// tangents with NetUtils.FitCurve): tangent at each vertex = average of the two adjacent
        /// segments (Catmull-Rom), except at real corners (inherited from a sharp perimeter corner)
        /// where each side keeps its own direction. Without this, a curved ring shows as straight
        /// facets broken at every vertex.
        /// </summary>
        private static void EmitSmoothLoop(List<RoadSegmentDef> segments, List<float2> loop, HashSet<(float, float)> corners, float y)
        {
            int n = loop.Count;
            var incoming = new float2[n];
            var outgoing = new float2[n];
            for (int i = 0; i < n; i++)
            {
                float2 dIn = math.normalizesafe(loop[i] - loop[(i - 1 + n) % n]);
                float2 dOut = math.normalizesafe(loop[(i + 1) % n] - loop[i]);
                // Only real corners (inherited from a sharp perimeter corner, see
                // SmoothAndResample) keep a break; everything else is smoothed, whatever the angle
                // between two segments (user feedback: "the curves lack fluidity").
                if (corners.Contains((loop[i].x, loop[i].y)))
                {
                    incoming[i] = dIn;
                    outgoing[i] = dOut;
                }
                else
                {
                    float2 t = math.normalizesafe(dIn + dOut, dOut);
                    incoming[i] = t;
                    outgoing[i] = t;
                }
            }
            // Kept vertices (user feedback: "when the mod generates the curves, it creates too many
            // nodes" — one node every ~25 m, no resampling): real corners, spoke joints (already
            // emitted, see EmitRadialChains) and, in between, only enough vertices for each segment
            // to stay a faithful circular arc (see CanSpanAsArc) — the game rebuilds the curve from
            // the tangents (NetUtils.FitCurve).
            var joints = new HashSet<(float, float)>();
            foreach (RoadSegmentDef s in segments)
            {
                joints.Add((s.Start.x, s.Start.z));
                joints.Add((s.End.x, s.End.z));
            }
            var keep = new bool[n];
            int first = -1;
            for (int i = 0; i < n; i++)
            {
                keep[i] = corners.Contains((loop[i].x, loop[i].y)) || joints.Contains((loop[i].x, loop[i].y));
                if (keep[i] && first < 0)
                {
                    first = i;
                }
            }
            if (first < 0)
            {
                first = 0;
                keep[0] = true;
            }
            var kept = new List<int> { first };
            int current = first;
            int walked = 0;
            while (walked < n)
            {
                int end = (current + 1) % n;
                int steps = 1;
                // Extend the segment while the next vertex is not forced and the arc still fits.
                while (!keep[end] && steps + 1 <= n - walked
                    && CanSpanAsArc(loop, current, (end + 1) % n, outgoing[current], incoming[(end + 1) % n]))
                {
                    end = (end + 1) % n;
                    steps++;
                }
                walked += steps;
                current = end;
                if (current == first)
                {
                    break;
                }
                kept.Add(current);
            }

            // No segment shorter than MinJunctionSegmentLength attached to a junction (spoke joint
            // or corner): the game rejects it (its "Overlapping objects" error, seen on the real
            // perimeter). An ordinary vertex too close to a junction is removed; the arc then goes
            // straight from the junction to the next vertex.
            bool removedStub = true;
            while (removedStub && kept.Count > 3)
            {
                removedStub = false;
                for (int k = 0; k < kept.Count && kept.Count > 3; k++)
                {
                    int vertex = kept[k];
                    if (keep[vertex])
                    {
                        continue;
                    }
                    int previous = kept[(k - 1 + kept.Count) % kept.Count];
                    int next = kept[(k + 1) % kept.Count];
                    bool nearPrevious = keep[previous] && math.distance(loop[previous], loop[vertex]) < MinJunctionSegmentLength;
                    bool nearNext = keep[next] && math.distance(loop[next], loop[vertex]) < MinJunctionSegmentLength;
                    if (nearPrevious || nearNext)
                    {
                        kept.RemoveAt(k);
                        removedStub = true;
                        k--;
                    }
                }
            }

            for (int k = 0; k < kept.Count; k++)
            {
                int i = kept[k];
                int j = kept[(k + 1) % kept.Count];
                float2 a = loop[i];
                float2 b = loop[j];
                if (math.distance(a, b) < 0.01f)
                {
                    continue;
                }
                float2 chord = math.normalizesafe(b - a);
                float2 ta = outgoing[i];
                float2 tb = incoming[j];
                var start = new float3(a.x, y, a.y);
                var end = new float3(b.x, y, b.y);
                // Near-straight segment: ordinary straight line.
                if (math.dot(ta, chord) > 0.9995f && math.dot(tb, chord) > 0.9995f)
                {
                    segments.Add(new RoadSegmentDef(start, end, isHorizontal: false));
                }
                else
                {
                    segments.Add(RoadSegmentDef.Arc(start, end, new float3(ta.x, 0f, ta.y), new float3(tb.x, 0f, tb.y)));
                }
            }
        }

        /// <summary>
        /// Minimum length (m) of a ring segment touching a junction. Initially the width of a road
        /// (16 m); seen in game (collision diagnostics log): 16-20 m segments attached to a
        /// junction were part of every rejection — the game's junction takes up more room.
        /// </summary>
        private const float MinJunctionSegmentLength = 35f;

        /// <summary>Maximum deviation (m) between the smoothed ring and the arc that replaces several of its vertices.</summary>
        private const float ArcFitTolerance = 2f;
        /// <summary>Maximum chord (m) of a curved ring segment.</summary>
        private const float MaxArcChord = 300f;
        /// <summary>Maximum turn (degrees) of a single curved segment: beyond it, FitCurve departs from the circle.</summary>
        private const float MaxArcTurnDegrees = 90f;

        /// <summary>
        /// True if the vertices from loop[from] to loop[to] (in loop order) fit on ONE circular arc
        /// leaving loop[from] with tangent `startTangent` and arriving at loop[to] with tangent
        /// `endTangent` (within 6°): then a single curved segment is enough.
        /// </summary>
        private static bool CanSpanAsArc(List<float2> loop, int from, int to, float2 startTangent, float2 endTangent)
        {
            int n = loop.Count;
            float2 a = loop[from];
            float2 b = loop[to];
            float2 ab = b - a;
            float chordLength = math.length(ab);
            if (chordLength < 0.01f || chordLength > MaxArcChord)
            {
                return false;
            }
            float2 chord = ab / chordLength;
            // End tangent of a circular arc: the start tangent mirrored about the chord.
            float2 arcEnd = 2f * math.dot(startTangent, chord) * chord - startTangent;
            if (math.dot(startTangent, chord) <= 0f
                || math.dot(arcEnd, endTangent) < math.cos(math.radians(6f))
                || math.dot(startTangent, arcEnd) < math.cos(math.radians(MaxArcTurnDegrees)))
            {
                return false;
            }
            float2 normal = new float2(-startTangent.y, startTangent.x);
            float side = math.dot(ab, normal);
            bool straight = math.abs(side) < 1e-3f * chordLength;
            float radius = straight ? 0f : chordLength * chordLength / (2f * side);
            float2 centre = a + normal * radius;
            for (int i = (from + 1) % n; i != to; i = (i + 1) % n)
            {
                float2 p = loop[i];
                float error = straight
                    ? math.abs(math.dot(p - a, new float2(-chord.y, chord.x)))
                    : math.abs(math.distance(p, centre) - math.abs(radius));
                if (error > ArcFitTolerance || math.dot(p - a, chord) < -ArcFitTolerance || math.dot(p - b, chord) > ArcFitTolerance)
                {
                    return false;
                }
            }
            return true;
        }

        // ------------------------------------------------------------------
        // Radial pattern: roundabout at the centre + straight avenues
        // ------------------------------------------------------------------

        public const int MinRadialAvenues = 3;
        public const int MaxRadialAvenues = 16;
        public const float MinRoundaboutRadius = 25f;
        public const float MaxRoundaboutRadius = 150f;

        /// <summary>Minimum gap (m) between two avenue joints on the roundabout: below it, two junctions are glued together (see MinJunctionSegmentLength).</summary>
        private const float MinAvenueJointSpacing = MinJunctionSegmentLength + 5f;

        /// <summary>Minimum length (m) of an avenue between the roundabout and the perimeter.</summary>
        private const float MinAvenueLength = 40f;

        /// <summary>Maximum length (m) of an avenue segment: a long avenue is laid as several aligned segments.</summary>
        private const float MaxAvenuePieceLength = 200f;

        /// <summary>Step (degrees) of the rotation of the whole avenue set when searching for open-angle arrivals on the perimeter.</summary>
        private const float AvenuePivotStepDegrees = 2f;

        /// <summary>
        /// Radial pattern (user feedback: "let's keep just the avenues and not the layers, and in
        /// the centre generate a roundabout, not an asset but a circle"): a roundabout — a real
        /// circle of road — centred on the deepest point of the shape (centre of the largest
        /// inscribed circle, always inside, even for an irregular shape), and `avenues` straight
        /// avenues (IsAvenue, collector network) spread at equal angles, from the roundabout to the
        /// perimeter. The first one aims at the farthest perimeter point (major axis). The radius
        /// is enlarged if the avenues would be too crowded on the roundabout, and reduced if the
        /// shape is too small (with fewer avenues then). The avenues stay evenly spaced on the
        /// roundabout: the whole set pivots so that as many as possible reach the perimeter at an
        /// open angle; an avenue that still arrives at a closed angle is omitted (the others keep
        /// their place).
        /// </summary>
        /// <summary>
        /// Effective roundabout radius and effective number of avenues: large enough to space out
        /// the joints (MinAvenueJointSpacing), small enough to leave room for avenues
        /// (MinAvenueLength). False if the shape is too small for a roundabout and avenues.
        /// </summary>
        private static bool RadialLayout(DistanceField field, ref int avenues, ref float radius)
        {
            avenues = math.clamp(avenues, MinRadialAvenues, MaxRadialAvenues);
            radius = math.clamp(radius, MinRoundaboutRadius, MaxRoundaboutRadius);
            radius = math.max(radius, avenues * MinAvenueJointSpacing / (2f * math.PI));
            float maxRadius = field.MaxDepth - MinAvenueLength;
            if (radius > maxRadius)
            {
                radius = maxRadius;
                avenues = math.min(avenues, (int)math.floor(2f * math.PI * radius / MinAvenueJointSpacing));
            }
            return radius >= 0.6f * MinRoundaboutRadius && avenues >= 2;
        }

        /// <summary>Distance (m) from the centre to the farthest perimeter point: the Radial rings extend that far.</summary>
        private static float FarthestReach(List<float2> polygon, float2 centre)
        {
            float best = 0f;
            foreach (float2 q in polygon)
            {
                best = math.max(best, math.distance(q, centre));
            }
            return best;
        }

        /// <summary>
        /// Maximum number of rings around the roundabout: at least MinLayerSpacing between rings,
        /// spread out to the farthest perimeter point (see GenerateRadial).
        /// </summary>
        private static int RadialMaxLayersFor(DistanceField field, List<float2> polygon, float radius)
        {
            float reach = FarthestReach(polygon, field.DeepestPoint);
            return math.clamp((int)math.floor((reach - radius) / MinLayerSpacing) - 1, 0, MaxLayersLimit);
        }

        /// <summary>Maximum number of rings of the Radial pattern for this perimeter and these settings — upper bound of the Layers slider.</summary>
        public static int RadialMaxLayers(IReadOnlyList<float3> selectedNodePositions, int avenues, float roundaboutRadius)
        {
            if (selectedNodePositions == null || selectedNodePositions.Count < 2
                || !TryGetField(selectedNodePositions, out DistanceField field, out List<float2> polygon, out float _)
                || !RadialLayout(field, ref avenues, ref roundaboutRadius))
            {
                return 0;
            }
            return RadialMaxLayersFor(field, polygon, roundaboutRadius);
        }

        public static List<RoadSegmentDef> GenerateRadial(IReadOnlyList<float3> selectedNodePositions, int avenues, float roundaboutRadius, int layers = 0,
            RadialDiagnostics diagnostics = null, CancellationToken cancel = default)
            => GenerateRadialUncached(selectedNodePositions, avenues, roundaboutRadius, layers, diagnostics, cancel);

        private static List<RoadSegmentDef> GenerateRadialUncached(IReadOnlyList<float3> selectedNodePositions, int avenues, float roundaboutRadius, int layers,
            RadialDiagnostics diagnostics, CancellationToken cancel)
        {
            var segments = new List<RoadSegmentDef>();
            if (selectedNodePositions == null || selectedNodePositions.Count < 2
                || !TryGetField(selectedNodePositions, out DistanceField field, out List<float2> polygon, out float y))
            {
                return segments;
            }
            float radius = roundaboutRadius;
            if (!RadialLayout(field, ref avenues, ref radius))
            {
                return segments; // shape too small for a roundabout and avenues
            }
            float2 centre = field.DeepestPoint;

            // Rings: circles concentric with the roundabout (user feedback: "following the
            // roundabout and not the outer road"), evenly spaced out to the farthest perimeter
            // point ("they have to fill the whole perimeter, even if it is only half a circle"):
            // where the circle leaves the shape, only its inside parts are laid, as arcs that join
            // the perimeter road.
            float reach = FarthestReach(polygon, centre);
            layers = math.clamp(layers, 0, RadialMaxLayersFor(field, polygon, radius));
            float ringSpacing = layers > 0 ? (reach - radius) / (layers + 1) : 0f;

            // --- Avenues: equal spacing on the roundabout, the whole set pivots (see below).
            float2 farthest = polygon[0];
            foreach (float2 q in polygon)
            {
                if (math.distancesq(q, centre) > math.distancesq(farthest, centre))
                {
                    farthest = q;
                }
            }
            float baseAngle = math.atan2(farthest.y - centre.y, farthest.x - centre.x);
            float gap = 2f * math.PI / avenues;

            // All avenues keep the same angular gap on the roundabout (user feedback: "the lines
            // have to be the same distance from each other when they reach the roundabout"): the
            // WHOLE set pivots, never a single avenue. Among the rotations (steps of
            // AvenuePivotStepDegrees, over one full gap), pick the one that lets the most avenues
            // reach the perimeter at an open angle; on a tie, the one closest to the major axis.
            List<(float angle, float length)> AvenuesAt(float rotation)
            {
                var result = new List<(float angle, float length)>();
                for (int k = 0; k < avenues; k++)
                {
                    float angle = baseAngle + rotation + k * gap;
                    float2 direction = new float2(math.cos(angle), math.sin(angle));
                    if (!FirstPerimeterHit(polygon, centre, direction, out float t) || t - radius < MinAvenueLength)
                    {
                        continue;
                    }
                    if (MeetsRingAtOpenAngle(polygon, centre + direction * t, -direction))
                    {
                        result.Add((angle, t));
                    }
                }
                return result;
            }

            var accepted = AvenuesAt(0f);
            float step = math.radians(AvenuePivotStepDegrees);
            for (float offset = step; offset <= 0.5f * gap + 1e-4f && accepted.Count < avenues; offset += step)
            {
                foreach (float sign in new[] { 1f, -1f })
                {
                    var candidate = AvenuesAt(sign * offset);
                    if (candidate.Count > accepted.Count)
                    {
                        accepted = candidate;
                    }
                }
            }
            if (accepted.Count == 0)
            {
                return segments;
            }

            float2 At(float r, float angle) => centre + r * new float2(math.cos(angle), math.sin(angle));
            var avenueEnds = accepted.Select(av => At(av.length, av.angle)).ToList();

            // --- Rings: the parts of the circle inside the shape, never grazing the perimeter.
            // User feedback (collision diagnostics log, 150 m roundabout, 10 rings): a ring passed
            // 12-14 m from the perimeter road without crossing it, and the game snapped a node onto
            // it (8 m segment on the existing road, overlap). A generated road stays
            // PerimeterClearance away from the perimeter, except where it crosses it cleanly
            // (open-angle arrival). keepFrom / keepTo: approach zone (m) of an end connected to the
            // perimeter, where the arc must not be split (an intermediate node there would be too
            // close to the perimeter).
            var ringArcs = new List<(float r, float from, float to, bool full, float keepFrom, float keepTo)>();
            var ringRadii = new List<float>();
            // Ring extensions (see PerimeterTail): beyond the last crossed avenue (`cross`) up to
            // the clear end of the run (`runEnd`), `dir` = +1 towards increasing angles.
            var tailCandidates = new List<(float r, float cross, float runEnd, float dir)>();
            // Approach zone of a (straight) avenue towards the perimeter, which it meets at an open
            // angle.
            float approach = PerimeterClearance / math.sin(math.radians(MinSpokeRingAngleDegrees));
            for (int k = 1; k <= layers; k++)
            {
                float r = radius + k * ringSpacing;
                ringRadii.Add(r);
                foreach ((float from, float to, bool full) in InsideArcs(polygon, centre, r))
                {
                    // Samples along the arc: "grazing" if too close to the perimeter, except in the
                    // approach zone of an end that crosses the perimeter at an open angle.
                    float startMinRun = 0f, endMinRun = 0f;
                    float startApproach = full ? 0f : PerimeterApproach(polygon, centre, r, from, avenueEnds, out startMinRun);
                    float endApproach = full ? 0f : PerimeterApproach(polygon, centre, r, to, avenueEnds, out endMinRun);
                    bool startOnPerimeter = startApproach > 0f;
                    bool endOnPerimeter = endApproach > 0f;
                    int count = math.max(8, (int)math.ceil((to - from) * r / ClearanceSampleStep));
                    var clear = new bool[count + 1];
                    for (int i = 0; i <= count; i++)
                    {
                        float angle = from + (to - from) * i / count;
                        float along = (angle - from) * r;
                        float before = (to - angle) * r;
                        bool inApproach = (startOnPerimeter && along < startApproach) || (endOnPerimeter && before < endApproach);
                        clear[i] = inApproach || IsClearOfPerimeter(field, polygon, At(r, angle));
                    }
                    bool anyBlocked = Array.IndexOf(clear, false) >= 0;
                    if (full && !anyBlocked)
                    {
                        ringArcs.Add((r, from, to, true, 0f, 0f));
                        continue;
                    }
                    // Full circle but grazing in places: walk it starting from a grazing point.
                    float baseFrom = from;
                    if (full)
                    {
                        int firstBlocked = Array.IndexOf(clear, false);
                        baseFrom = from + (to - from) * firstBlocked / count;
                        var rotated = new bool[count + 1];
                        for (int i = 0; i <= count; i++)
                        {
                            rotated[i] = clear[(firstBlocked + i) % count];
                        }
                        clear = rotated;
                    }
                    float span = to - from;
                    // Runs of clear samples: each gives an arc, whose ends are on the perimeter
                    // (original end, clear) or pulled back to the nearest avenue.
                    for (int i = 0; i <= count; i++)
                    {
                        if (!clear[i])
                        {
                            continue;
                        }
                        int j = i;
                        while (j + 1 <= count && clear[j + 1])
                        {
                            j++;
                        }
                        float runFrom = baseFrom + span * i / count;
                        float runTo = baseFrom + span * j / count;
                        bool keepsStart = !full && i == 0 && startOnPerimeter;
                        bool keepsEnd = !full && j == count && endOnPerimeter;
                        var crossing = accepted.Where(av => av.length > r + 1f && AngleWithin(av.angle, runFrom, runTo))
                            .Select(av => runFrom + Wrap(av.angle - runFrom)).OrderBy(x => x).ToList();
                        // Avenues crossed in the approach zone of an end on the perimeter are too
                        // close to it (exempted samples): the arc does not go to the perimeter
                        // (that avenue node would be on the arc) and is not pulled back to them
                        // either.
                        if (!full && i == 0 && startOnPerimeter
                            && crossing.RemoveAll(x => (x - runFrom) * r < startApproach) > 0)
                        {
                            keepsStart = false;
                        }
                        if (!full && j == count && endOnPerimeter
                            && crossing.RemoveAll(x => (runTo - x) * r < endApproach) > 0)
                        {
                            keepsEnd = false;
                        }
                        float start = keepsStart ? runFrom : (crossing.Count > 0 ? crossing[0] : float.NaN);
                        float end = keepsEnd ? runTo : (crossing.Count > 0 ? crossing[crossing.Count - 1] : float.NaN);
                        // Arc end up to the neighbouring avenue: long enough for the geometry of
                        // both nodes (the cutback grows as the angle with the perimeter closes),
                        // otherwise pulled back to the avenue.
                        if (keepsStart && crossing.Count > 0 && (crossing[0] - start) * r < startMinRun)
                        {
                            start = crossing[0];
                            keepsStart = false;
                        }
                        if (keepsEnd && crossing.Count > 0 && (end - crossing[crossing.Count - 1]) * r < endMinRun)
                        {
                            end = crossing[crossing.Count - 1];
                            keepsEnd = false;
                        }
                        // Arc from one side of the perimeter to the other with no avenue: the
                        // cutbacks of both nodes must fit on it.
                        float minLength = MinJunctionSegmentLength;
                        if (keepsStart && keepsEnd && crossing.Count == 0)
                        {
                            minLength = math.max(startMinRun, endMinRun) + math.min(startMinRun, endMinRun)
                                - MinJunctionSegmentLength - 2f * NodeCutback(1f, 0f);
                        }
                        if (!float.IsNaN(start) && !float.IsNaN(end) && (end - start) * r >= minLength)
                        {
                            ringArcs.Add((r, start, end, false, keepsStart ? startApproach : 0f, keepsEnd ? endApproach : 0f));
                            if (!keepsStart && crossing.Count > 0)
                            {
                                tailCandidates.Add((r, crossing[0], runFrom, -1f));
                            }
                            if (!keepsEnd && crossing.Count > 0)
                            {
                                tailCandidates.Add((r, crossing[crossing.Count - 1], runTo, 1f));
                            }
                        }
                        i = j;
                    }
                }
            }

            bool OnRing(float r, float angle) => ringArcs.Exists(arc => math.abs(arc.r - r) < 1e-3f
                && (arc.full || AngleWithin(angle, arc.from - 1e-4f, arc.to + 1e-4f)));

            // --- Avenues: roundabout, then each crossed ring, then the perimeter — as long as they
            // stay clear of the perimeter (an avenue that grazes it before reaching it stops at the
            // last ring before that point).
            foreach ((float angle, float length) in accepted)
            {
                float clearUntil = length;
                for (float t = radius; t < length - approach; t += ClearanceSampleStep)
                {
                    if (!IsClearOfPerimeter(field, polygon, At(t, angle)))
                    {
                        clearUntil = t;
                        break;
                    }
                }
                var stops = new List<float> { radius };
                foreach (float r in ringRadii)
                {
                    if (r < clearUntil && r < length && OnRing(r, angle))
                    {
                        stops.Add(r);
                    }
                }
                // Up to the perimeter if the avenue stays clear and the last segment is long
                // enough.
                if (clearUntil >= length && length - stops[stops.Count - 1] >= MinAvenueLength)
                {
                    stops.Add(length);
                }
                for (int st = 0; st + 1 < stops.Count; st++)
                {
                    float from = stops[st];
                    float to = stops[st + 1];
                    int pieces = math.max(1, (int)math.ceil((to - from) / MaxAvenuePieceLength));
                    for (int piece = 0; piece < pieces; piece++)
                    {
                        float2 a = At(from + (to - from) * piece / pieces, angle);
                        float2 b = At(piece == pieces - 1 ? to : from + (to - from) * (piece + 1) / pieces, angle);
                        if (piece == 0) a = At(from, angle);
                        segments.Add(new RoadSegmentDef(new float3(a.x, y, a.y), new float3(b.x, y, b.y), isHorizontal: false, isAvenue: true));
                    }
                }
            }

            // --- Roundabout and rings: exact arcs between successive joints. Arc ends are snapped
            // to the EXACT avenue points (joints): recomputed from an angle wrapped into [0, 2π),
            // they differed by a rounding error and the node was no longer shared.
            var joints = segments.SelectMany(sg => new[] { sg.Start.xz, sg.End.xz }).ToList();
            var roundaboutStops = accepted.Select(av => av.angle).ToList();
            EmitCircle(segments, centre, radius, roundaboutStops, y, joints, roundabout: true);
            // Actual avenue joints on each ring (an avenue stopped earlier does not cross it).
            var avenueJointSet = new HashSet<(float, float)>(joints.Select(j => (j.x, j.y)));
            foreach (var arc in ringArcs)
            {
                var stops = accepted.Where(av => (arc.full || AngleWithin(av.angle, arc.from - 1e-4f, arc.to + 1e-4f))
                        && joints.Exists(j => math.distancesq(j, At(arc.r, av.angle)) < 0.01f))
                    .Select(av => av.angle).ToList();
                if (arc.full)
                {
                    EmitCircle(segments, centre, arc.r, stops, y, joints);
                }
                else
                {
                    var angles = new List<float> { arc.from, arc.to };
                    angles.AddRange(stops.Select(x => arc.from + Wrap(x - arc.from)).Where(x => x > arc.from + 1e-4f && x < arc.to - 1e-4f));
                    angles.Sort();
                    // Forced split at the exit of the approach zone (pieces of at most 60°):
                    // without it, the regular split in EmitCircleArc could put a node 13 m from the
                    // perimeter (closed angle).
                    float maxPiece = math.PI / 3f - 1e-3f;
                    if (arc.keepFrom > 0f && angles[1] - angles[0] > maxPiece)
                    {
                        angles.Insert(1, angles[0] + arc.keepFrom / arc.r);
                    }
                    int last = angles.Count - 1;
                    if (arc.keepTo > 0f && angles[last] - angles[last - 1] > maxPiece)
                    {
                        angles.Insert(last, angles[last] - arc.keepTo / arc.r);
                    }
                    for (int i = 0; i + 1 < angles.Count; i++)
                    {
                        EmitCircleArc(segments, centre, arc.r, angles[i], angles[i + 1], y, joints);
                    }
                }
            }

            // --- Ring extensions to the perimeter. User feedback ("there are still layers that do
            // not go all the way to the end"): where the ring runs almost parallel to the
            // perimeter, no open-angle crossing exists and the ring stopped at the last avenue. It
            // now continues as long as it stays clear, then turns towards the perimeter with a
            // curve that meets it at an open angle (see PerimeterTail).
            var perimeterJoints = new List<float2>(avenueEnds);
            foreach (var arc in ringArcs)
            {
                if (arc.keepFrom > 0f) perimeterJoints.Add(At(arc.r, arc.from));
                if (arc.keepTo > 0f) perimeterJoints.Add(At(arc.r, arc.to));
            }
            var ringSpans = ringArcs.Select(a => (a.r, a.from, a.to, a.full)).ToList();
            var acceptedTailSamples = new List<float2>();
            foreach (var tail in tailCandidates.OrderByDescending(tc => math.abs(tc.runEnd - tc.cross) * tc.r))
            {
                if (!joints.Exists(jt => math.distancesq(jt, At(tail.r, tail.cross)) < 0.01f))
                {
                    continue; // the avenue does not reach this ring: no joint to attach to
                }
                if (PerimeterTail(polygon, centre, tail.r, tail.cross, tail.runEnd, tail.dir, accepted, ringSpans,
                        segments, perimeterJoints, avenueEnds, acceptedTailSamples, out float tailEnd, out float2 q, out float2 qTangent))
                {
                    EmitCircleArc(segments, centre, tail.r, math.min(tail.cross, tailEnd), math.max(tail.cross, tailEnd), y, joints);
                    float2 pt = At(tail.r, tailEnd);
                    float2 tf = tail.dir * new float2(-math.sin(tailEnd), math.cos(tailEnd));
                    segments.Add(RoadSegmentDef.Arc(new float3(pt.x, y, pt.y), new float3(q.x, y, q.y), new float3(tf.x, 0f, tf.y), new float3(qTangent.x, 0f, qTangent.y)));
                    perimeterJoints.Add(q);
                }
            }

            if (diagnostics != null)
            {
                diagnostics.Centre = centre;
                diagnostics.Radius = radius;
                diagnostics.RingRadii = ringRadii.ToArray();
            }
            return segments;
        }

        /// <summary>True if p is inside the closed outline `loop` (even-odd rule).</summary>
        private static bool PointInLoop(float2 p, List<float2> loop)
        {
            bool inside = false;
            for (int i = 0, j = loop.Count - 1; i < loop.Count; j = i++)
            {
                float2 a = loop[i];
                float2 b = loop[j];
                if ((a.y > p.y) != (b.y > p.y) && p.x < (b.x - a.x) * (p.y - a.y) / (b.y - a.y) + a.x)
                {
                    inside = !inside;
                }
            }
            return inside;
        }

        /// <summary>Minimum distance (m) between a generated road and the perimeter road, except at a clean crossing.</summary>
        private const float PerimeterClearance = 25f;

        /// <summary>Step (m) of the samples used to check this clearance.</summary>
        private const float ClearanceSampleStep = 6f;

        /// <summary>
        /// True if p is at least PerimeterClearance from the perimeter. Reads the distance grid
        /// (instant); computes the exact distance to the outline only when the grid gives a value
        /// close to the threshold (its accuracy is about one cell) — without this, the Radial
        /// clearance check cost more than half a second per generation.
        /// </summary>
        private static bool IsClearOfPerimeter(DistanceField field, List<float2> polygon, float2 p)
        {
            float depth = field.DepthAt(p);
            float margin = 2f * field.CellSize;
            if (depth >= PerimeterClearance + margin)
            {
                return true;
            }
            if (depth < PerimeterClearance - margin)
            {
                return false;
            }
            return DistanceToLoop(polygon, p) >= PerimeterClearance;
        }

        /// <summary>Distance (m) from point p to the closed outline `loop`.</summary>
        private static float DistanceToLoop(List<float2> loop, float2 p)
        {
            float best = float.MaxValue;
            for (int i = 0; i < loop.Count; i++)
            {
                best = math.min(best, math.distancesq(ClosestOnSegment(loop[i], loop[(i + 1) % loop.Count], p), p));
            }
            return math.sqrt(best);
        }

        /// <summary>
        /// Minimum angle (degrees), at the crossing point, between a Radial ring arc and the
        /// perimeter road it joins. The game has no minimum angle as such
        /// (Game.Net.ValidationHelpers): two segments sharing a node are never tested against each
        /// other (no OverlapExisting), and roads have no minimum length (m_EdgeLengthRange.min =
        /// 0). What breaks at a closed angle is the node geometry: its cutback along each road
        /// (NodeCutback) grows as 1/tan(θ/2), and if a segment is shorter than that cutback its
        /// geometry inverts (InvalidShape). The real constraint is therefore the available length
        /// (see PerimeterApproach); this floor only rules out near-tangent joints (cutback > 90 m,
        /// degenerate lane connections).
        /// </summary>
        private const float MinArcPerimeterAngleDegrees = 15f;

        /// <summary>
        /// Cutback (m) of a node's geometry along a road that crosses another at the angle whose
        /// sine/cosine are given: the edge of one road crosses the edge of the other at (w₁ +
        /// w₂·cos θ) / sin θ from the node; with w₁ + w₂ = PerimeterClearance (assumed combined
        /// widths, margin included), w₁ = w₂ gives (PerimeterClearance / 2) / tan(θ/2).
        /// </summary>
        private static float NodeCutback(float sin, float cos) => 0.5f * PerimeterClearance * (1f + cos) / math.max(sin, 1e-3f);

        /// <summary>
        /// Arc end of the circle (centre, r) at angle `angle`, on the perimeter: length (m) of its
        /// approach zone — where being close to the perimeter is expected — if it can connect
        /// there, 0 otherwise. The angle is measured AT the crossing point, between the circle's
        /// tangent and the outline edge (user feedback: "there are still places where there is no
        /// continuity" — previously measured over 50 m as for a straight road, it rejected arcs
        /// that cross a curved perimeter correctly). The rest of the arc is checked against
        /// PerimeterClearance. `minRun`: minimum length (m) of the arc end between the perimeter
        /// and the first avenue — perimeter node cutback + allowed segment + cutback (≈ right
        /// angle) of the avenue node, and at least the approach zone (otherwise that avenue node
        /// would be too close to the perimeter, where the game would snap it onto the existing
        /// road).
        /// </summary>
        private static float PerimeterApproach(List<float2> polygon, float2 centre, float r, float angle, List<float2> avenueEnds, out float minRun)
        {
            minRun = 0f;
            float2 point = centre + r * new float2(math.cos(angle), math.sin(angle));
            if (avenueEnds.Exists(e => math.distance(e, point) < MinAvenueJointSpacing))
            {
                return 0f;
            }
            float2 tangent = new float2(-math.sin(angle), math.cos(angle));
            int bestEdge = 0;
            float best = float.MaxValue;
            for (int i = 0; i < polygon.Count; i++)
            {
                float d = math.distancesq(ClosestOnSegment(polygon[i], polygon[(i + 1) % polygon.Count], point), point);
                if (d < best)
                {
                    best = d;
                    bestEdge = i;
                }
            }
            float2 edge = math.normalizesafe(polygon[(bestEdge + 1) % polygon.Count] - polygon[bestEdge]);
            float sin = math.abs(tangent.x * edge.y - tangent.y * edge.x);
            if (sin < math.sin(math.radians(MinArcPerimeterAngleDegrees)))
            {
                return 0f;
            }
            float approach = PerimeterClearance / sin + ClearanceSampleStep;
            if (approach / r > math.PI / 3f - 1e-3f)
            {
                return 0f;
            }
            float cos = math.abs(math.dot(tangent, edge));
            minRun = math.max(approach, NodeCutback(sin, cos) + MinJunctionSegmentLength + NodeCutback(1f, 0f));
            return approach;
        }

        /// <summary>Minimum distance (m) to the perimeter of the point where a ring extension turns towards it.</summary>
        private const float TailTurnClearance = 32f;

        /// <summary>Minimum gap (m) between an extension curve and the other generated roads.</summary>
        private const float TailRoadSpacing = 20f;

        /// <summary>
        /// Extension of a ring (centre, r) from the avenue at angle `cross` in direction `dir`, at
        /// most up to `runEnd`: the ring continues to the last point that is TailTurnClearance from
        /// the perimeter (`tailEnd`), then a circular curve (tangent to the ring) turns towards the
        /// perimeter and meets it at `q` (arrival tangent `qTangent`). Rejected if the segment or
        /// the curve is too short for the node geometry (see NodeCutback), if the arrival is under
        /// 40°, too close to another perimeter joint, or if the curve passes close to another road
        /// (avenues, other rings, extensions already accepted).
        /// </summary>
        private static bool PerimeterTail(List<float2> polygon, float2 centre, float r, float cross, float runEnd, float dir,
            List<(float angle, float length)> avenues, List<(float r, float from, float to, bool full)> rings,
            List<RoadSegmentDef> segments, List<float2> perimeterJoints, List<float2> avenueEnds, List<float2> acceptedSamples,
            out float tailEnd, out float2 q, out float2 qTangent)
        {
            q = default;
            qTangent = default;
            float2 At(float angle) => centre + r * new float2(math.cos(angle), math.sin(angle));
            float angleStep = ClearanceSampleStep / r;
            tailEnd = runEnd;
            while ((tailEnd - cross) * dir > 0f && DistanceToLoop(polygon, At(tailEnd)) < TailTurnClearance)
            {
                tailEnd -= dir * angleStep;
            }
            float tailLength = (tailEnd - cross) * dir * r;
            if (tailLength < MinJunctionSegmentLength)
            {
                return false;
            }
            float lo = math.min(cross, tailEnd), hi = math.max(cross, tailEnd);
            // No avenue may cross the extension (it would have no node there).
            if (avenues.Exists(av => av.length > r - 1f && AngleWithin(av.angle, lo + 1e-4f, hi - 1e-4f)))
            {
                return false;
            }
            float2 p = At(tailEnd);
            float2 tf = dir * new float2(-math.sin(tailEnd), math.cos(tailEnd));
            float2 n = ClosestOnLoop(polygon, p, out float2 edge);
            float h = math.distance(n, p);
            if (math.dot(edge, tf) < 0f)
            {
                edge = -edge;
            }
            // Circular curve tangent to the ring up to the perimeter. The advance along the
            // perimeter sets the turn: √3·h gives a 60° turn (radius 2h) on a straight edge; on a
            // curved edge or near another joint, other advances are tried.
            var tailSamples = new List<float2>();
            int arcCount = math.max(2, (int)math.ceil(tailLength / 4f));
            for (int i = 0; i <= arcCount; i++)
            {
                tailSamples.Add(At(cross + (tailEnd - cross) * i / arcCount));
            }
            if (tailSamples.Exists(a => acceptedSamples.Exists(b => math.distancesq(a, b) < TailRoadSpacing * TailRoadSpacing)))
            {
                return false;
            }
            foreach (float advance in TailAdvanceFactors)
            {
                if (TryTailCurve(polygon, centre, r, p, tf, n + edge * (advance * h), rings, segments, perimeterJoints, avenueEnds, acceptedSamples,
                        out q, out qTangent, out List<float2> curve))
                {
                    acceptedSamples.AddRange(tailSamples);
                    acceptedSamples.AddRange(curve);
                    return true;
                }
            }
            return false;
        }

        /// <summary>Advances (× distance to the perimeter) tried for the curve of a ring extension.</summary>
        private static readonly float[] TailAdvanceFactors = { 1.732f, 1.3f, 2.3f, 1f, 3f, 0.7f };

        /// <summary>
        /// Curve of a ring extension (see PerimeterTail) from p (tangent tf) to the perimeter point
        /// closest to `target`: circular arc, arrival at 40° or more, long enough for the node
        /// cutback, away from the other perimeter joints and the other roads.
        /// </summary>
        private static bool TryTailCurve(List<float2> polygon, float2 centre, float r, float2 p, float2 tf, float2 target,
            List<(float r, float from, float to, bool full)> rings, List<RoadSegmentDef> segments, List<float2> perimeterJoints,
            List<float2> avenueEnds, List<float2> acceptedSamples, out float2 q, out float2 qTangent, out List<float2> curve)
        {
            qTangent = default;
            curve = null;
            q = ClosestOnLoop(polygon, target, out float2 qEdge);
            // Arrival too close to another perimeter joint: if it is an avenue's arrival, the curve
            // joins it at the same node (user feedback: the corners left empty were at the foot of
            // an avenue, where the ring meets the perimeter); otherwise rejected.
            float2 end = q;
            var near = perimeterJoints.Where(j => math.distance(j, end) < MinAvenueJointSpacing).Distinct().ToList();
            bool snapped = false;
            if (near.Count > 0)
            {
                if (near.Count > 1 || !avenueEnds.Exists(a => math.distancesq(a, near[0]) < 0.01f))
                {
                    return false;
                }
                q = near[0];
                ClosestOnLoop(polygon, q, out qEdge);
                snapped = true;
            }
            float2 chord = q - p;
            float chordLength = math.length(chord);
            if (chordLength < 1f)
            {
                return false;
            }
            chord /= chordLength;
            float along = math.dot(chord, tf);
            if (along < math.cos(math.radians(50f)))
            {
                return false; // turn of more than 100°: not a reasonable curve
            }
            qTangent = 2f * along * chord - tf;
            float sin = math.abs(qTangent.x * qEdge.y - qTangent.y * qEdge.x);
            float cos = math.abs(math.dot(qTangent, qEdge));
            if (sin < math.sin(math.radians(40f)) || chordLength < NodeCutback(sin, cos) + 10f)
            {
                return false;
            }
            // At an avenue node: open angle with the avenue too, and the samples near the node are
            // exempt from the avenue spacing (they join it).
            float avenueExemption = 0f;
            if (snapped)
            {
                float cosAvenue = math.dot(qTangent, math.normalize(q - centre));
                if (cosAvenue > math.cos(math.radians(35f)))
                {
                    return false;
                }
                avenueExemption = TailRoadSpacing / math.max(math.sqrt(math.saturate(1f - cosAvenue * cosAvenue)), 0.3f) + 5f;
            }
            // Curve samples (quadratic Bézier, control point at the intersection of the tangents).
            float2 control = p + tf * (0.5f * chordLength / along);
            var points = new List<float2>();
            int curveCount = math.max(4, (int)math.ceil(chordLength / 4f));
            for (int i = 1; i <= curveCount; i++)
            {
                float t = (float)i / curveCount;
                points.Add((1 - t) * (1 - t) * p + 2 * (1 - t) * t * control + t * t * q);
            }
            foreach (float2 c in points)
            {
                float toEnd = math.distance(c, q);
                if (toEnd > 0.5f && (!PointInLoop(c, polygon) || DistanceToLoop(polygon, c) < math.min(PerimeterClearance - 3f, 0.6f * toEnd)))
                {
                    return false;
                }
                foreach (RoadSegmentDef s in segments)
                {
                    if (s.IsAvenue && toEnd >= avenueExemption && math.distance(ClosestOnSegment(s.Start.xz, s.End.xz, c), c) < TailRoadSpacing)
                    {
                        return false;
                    }
                }
                float ca = math.atan2(c.y - centre.y, c.x - centre.x);
                float cr = math.distance(c, centre);
                foreach (var ring in rings)
                {
                    if (math.abs(ring.r - r) > 1e-3f && math.abs(cr - ring.r) < TailRoadSpacing && (ring.full || AngleWithin(ca, ring.from, ring.to)))
                    {
                        return false;
                    }
                }
                if (acceptedSamples.Exists(b => math.distancesq(c, b) < TailRoadSpacing * TailRoadSpacing))
                {
                    return false;
                }
            }
            curve = points;
            return true;
        }

        /// <summary>Point of the closed outline closest to p, and the (unit) direction of its edge.</summary>
        private static float2 ClosestOnLoop(List<float2> loop, float2 p, out float2 edgeDirection)
        {
            float best = float.MaxValue;
            float2 result = loop[0];
            edgeDirection = new float2(1f, 0f);
            for (int i = 0; i < loop.Count; i++)
            {
                float2 a = loop[i];
                float2 b = loop[(i + 1) % loop.Count];
                if (math.distancesq(a, b) < 1e-6f)
                {
                    continue;
                }
                float2 c = ClosestOnSegment(a, b, p);
                float d = math.distancesq(c, p);
                if (d < best)
                {
                    best = d;
                    result = c;
                    edgeDirection = math.normalize(b - a);
                }
            }
            return result;
        }

        /// <summary>Angle wrapped into [0, 2π).</summary>
        private static float Wrap(float angle)
        {
            float twoPi = 2f * math.PI;
            angle %= twoPi;
            return angle < 0f ? angle + twoPi : angle;
        }

        /// <summary>True if `angle` is within the interval [from, to] travelled counter-clockwise (to - from ≤ 2π).</summary>
        private static bool AngleWithin(float angle, float from, float to)
        {
            return Wrap(angle - from) <= to - from;
        }

        /// <summary>
        /// Parts of the circle (centre, r) inside the polygon: angle intervals [from, to] (to >
        /// from, counter-clockwise) between two crossings with the outline; full = whole circle
        /// inside (from = 0, to = 2π).
        /// </summary>
        private static List<(float from, float to, bool full)> InsideArcs(List<float2> polygon, float2 centre, float r)
        {
            var crossings = new List<float>();
            for (int i = 0; i < polygon.Count; i++)
            {
                float2 a = polygon[i] - centre;
                float2 d = polygon[(i + 1) % polygon.Count] - polygon[i];
                // |a + t d|² = r²
                float qa = math.dot(d, d);
                float qb = 2f * math.dot(a, d);
                float qc = math.dot(a, a) - r * r;
                float discriminant = qb * qb - 4f * qa * qc;
                if (qa < 1e-8f || discriminant < 0f)
                {
                    continue;
                }
                float sq = math.sqrt(discriminant);
                foreach (float t in new[] { (-qb - sq) / (2f * qa), (-qb + sq) / (2f * qa) })
                {
                    if (t >= 0f && t < 1f)
                    {
                        float2 q = a + t * d;
                        crossings.Add(Wrap(math.atan2(q.y, q.x)));
                    }
                }
            }
            var result = new List<(float, float, bool)>();
            if (crossings.Count == 0)
            {
                if (PointInLoop(centre + new float2(r, 0f), polygon))
                {
                    result.Add((0f, 2f * math.PI, true));
                }
                return result;
            }
            crossings.Sort();
            for (int i = 0; i < crossings.Count; i++)
            {
                float from = crossings[i];
                float to = i + 1 < crossings.Count ? crossings[i + 1] : crossings[0] + 2f * math.PI;
                if (to - from < 1e-4f)
                {
                    continue;
                }
                float middle = 0.5f * (from + to);
                if (PointInLoop(centre + r * new float2(math.cos(middle), math.sin(middle)), polygon))
                {
                    result.Add((from, to, false));
                }
            }
            return result;
        }

        /// <summary>Full circle (centre, r) split at the angles `stops` (see EmitCircleArc for the segment length).</summary>
        private static void EmitCircle(List<RoadSegmentDef> segments, float2 centre, float r, List<float> stops, float y, List<float2> joints, bool roundabout = false)
        {
            var angles = stops.Select(Wrap).Distinct().OrderBy(x => x).ToList();
            if (angles.Count == 0)
            {
                angles.Add(0f);
            }
            for (int i = 0; i < angles.Count; i++)
            {
                float from = angles[i];
                float to = i + 1 < angles.Count ? angles[i + 1] : angles[0] + 2f * math.PI;
                EmitCircleArc(segments, centre, r, from, to, y, joints, roundabout);
            }
        }

        /// <summary>Exact arc of the circle (centre, r) from `from` to `to` (counter-clockwise), as curved segments of at most 60° with exact tangents.</summary>
        private static void EmitCircleArc(List<RoadSegmentDef> segments, float2 centre, float r, float from, float to, float y, List<float2> joints, bool roundabout = false)
        {
            float2 Snap(float2 q)
            {
                foreach (float2 j in joints)
                {
                    if (math.distancesq(j, q) < 0.01f)
                    {
                        return j;
                    }
                }
                return q;
            }
            // At most 60° per segment (NetUtils.FitCurve follows the circle well up to that), with
            // a rounding margin: an arc of exactly 45° must not be split into two 22.5° segments.
            int pieces = math.max(1, (int)math.ceil((to - from) / (math.PI / 3f) - 1e-3f));
            for (int i = 0; i < pieces; i++)
            {
                float a0 = from + (to - from) * i / pieces;
                float a1 = i == pieces - 1 ? to : from + (to - from) * (i + 1) / pieces;
                float2 p0 = Snap(centre + r * new float2(math.cos(a0), math.sin(a0)));
                float2 p1 = Snap(centre + r * new float2(math.cos(a1), math.sin(a1)));
                var t0 = new float3(-math.sin(a0), 0f, math.cos(a0));
                var t1 = new float3(-math.sin(a1), 0f, math.cos(a1));
                RoadSegmentDef arc = RoadSegmentDef.Arc(new float3(p0.x, y, p0.y), new float3(p1.x, y, p1.y), t0, t1);
                arc.IsRoundabout = roundabout;
                segments.Add(arc);
            }
        }

        /// <summary>Distance `bestT` along the ray (origin, direction) to the first perimeter edge it hits; false if it hits none.</summary>
        private static bool FirstPerimeterHit(List<float2> polygon, float2 origin, float2 direction, out float bestT)
        {
            bestT = float.MaxValue;
            for (int i = 0; i < polygon.Count; i++)
            {
                float2 a = polygon[i];
                float2 edge = polygon[(i + 1) % polygon.Count] - a;
                float denominator = direction.x * edge.y - direction.y * edge.x;
                if (math.abs(denominator) < 1e-6f)
                {
                    continue;
                }
                float2 w = a - origin;
                float t = (w.x * edge.y - w.y * edge.x) / denominator;
                float u = (w.x * direction.y - w.y * direction.x) / denominator;
                if (t > 1e-3f && u >= 0f && u < 1f && t < bestT)
                {
                    bestT = t;
                }
            }
            return bestT < float.MaxValue;
        }

        // ------------------------------------------------------------------
        // Spokes
        // ------------------------------------------------------------------

        /// <summary>
        /// Spokes as continuous chains from the perimeter towards the centre (user feedback: "the
        /// connections appear very close to each other instead of appearing in various corners of
        /// the perimeter" — the chains used to start from the INNERMOST ring, small and stretched
        /// on an elongated shape, and all ended up grouped in the middle). `connections` points are
        /// spread evenly on the first ring (the one following the perimeter); each is linked to the
        /// perimeter (nearest point = perpendicular), then projected onto the next ring inwards,
        /// and so on. Two chains that converge (towards the tip of an inner ring) end at the same
        /// node. Arrival points are INSERTED as ring vertices so that spoke and ring share exactly
        /// the same node; those arriving on the perimeter (levels[0]) are connected to it by the
        /// game layer. A ring that no chain reaches (a separate branch of the shape) gets its own
        /// points, linked outwards.
        /// </summary>
        private static void EmitRadialChains(List<RoadSegmentDef> segments, List<List<List<float2>>> levels,
            int connections, float spacing, float y, CancellationToken cancel)
        {
            s_CumulativeLengths = null;
            try
            {
                EmitRadialChainsCore(segments, levels, connections, spacing, y, cancel);
            }
            finally
            {
                s_CumulativeLengths = null;
            }
        }

        private static void EmitRadialChainsCore(List<RoadSegmentDef> segments, List<List<List<float2>>> levels,
            int connections, float spacing, float y, CancellationToken cancel)
        {
            float mergeDistance = spacing * 0.8f;
            // The actual gap between rings varies around `spacing` (wider in wide parts of the
            // shape, tighter in narrow ones).
            float maxSpokeLength = spacing * 4f;

            var chainPoints = new List<List<List<float2>>>();
            for (int k = 0; k < levels.Count; k++)
            {
                cancel.ThrowIfCancellationRequested();
                var perLoop = new List<List<float2>>();
                foreach (List<float2> _ in levels[k])
                {
                    perLoop.Add(new List<float2>());
                }
                chainPoints.Add(perLoop);
            }

            // True if the straight segment a -> b crosses a ring of `level` or an already placed
            // spoke, away from its own ends.
            bool CrossesSomething(float2 a, float2 b, List<List<float2>> level)
            {
                foreach (List<float2> loop in level)
                {
                    for (int i = 0; i < loop.Count; i++)
                    {
                        if (SegmentsCrossAwayFromEnds(a, b, loop[i], loop[(i + 1) % loop.Count]))
                        {
                            return true;
                        }
                    }
                }
                foreach (RoadSegmentDef spoke in segments)
                {
                    if (SegmentsCrossAwayFromEnds(a, b, spoke.Start.xz, spoke.End.xz))
                    {
                        return true;
                    }
                }
                return false;
            }

            // Finds on `target` the joint for a spoke starting at p: the nearest point, then step
            // by step along the ring (up to 1.5 × the spacing), the first one whose spoke is short
            // enough, arrives at an open angle at both ends and crosses nothing. `avoid`
            // (optional): nodes already placed on `target`; the joint must stay at least half the
            // spacing away from them, and never closer than MinJunctionSegmentLength (otherwise a
            // too-short segment between two junctions). `targetLevel` >= 0 (spoke towards the
            // centre): among the valid joints, prefers the nearest one from which the rest of the
            // chain, simulated on the rings as they are at that moment, still reaches the innermost
            // ring (user feedback: "the connection does not go to the centre", then "the road is
            // not continuous"). Otherwise, the first valid one.
            bool FindSpokeTarget(float2 p, List<float2> source, List<float2> target, float2 nearest, List<float2> avoid, int targetLevel, out float2 result)
            {
                if (targetLevel >= 0 && targetLevel + 1 < levels.Count
                    && FindSpokeTargetCore(p, source, target, nearest, avoid, targetLevel, out result))
                {
                    return true;
                }
                return FindSpokeTargetCore(p, source, target, nearest, avoid, -1, out result);
            }

            bool FindSpokeTargetCore(float2 p, List<float2> source, List<float2> target, float2 nearest, List<float2> avoid, int targetLevel, out float2 result)
            {
                result = nearest;
                // With look-ahead: if no joint leads all the way to the centre, keep the one that
                // leads furthest rather than the first one found (which sometimes led to a dead
                // end).
                bool hasPartial = false;
                int bestDepth = -1;
                // Bounded cost (generation reruns on every panel setting change): at most
                // MaxSimulatedCandidates simulations per choice, only one per arrival vertex.
                int simulations = 0;
                var simulated = new HashSet<(float, float)>();
                bool hasFirstValid = false;
                float2 firstValid = nearest;
                float2 bestPartial = nearest;
                float along = ArcPosition(target, nearest);
                float maxShift = SpokeSearchSpacings * spacing;
                for (float shift = 0f; shift <= maxShift; shift += SpokeSearchStep)
                {
                    foreach (float sign in shift == 0f ? new[] { 1f } : new[] { -1f, 1f })
                    {
                        float2 candidate = SnappedJoint(target, PointAtArc(target, along + sign * shift));
                        float length = math.distance(p, candidate);
                        if (length > maxSpokeLength || length < 1f)
                        {
                            continue;
                        }
                        if (avoid != null && avoid.Exists(node => math.distance(node, candidate) < math.max(0.5f * spacing, MinJunctionSegmentLength + 5f)))
                        {
                            continue;
                        }
                        if (!MeetsRingAtOpenAngle(target, candidate, p - candidate) || !MeetsRingAtOpenAngle(source, p, candidate - p))
                        {
                            continue;
                        }
                        if (shift > 0f && (CrossesSomething(p, candidate, new List<List<float2>> { source, target })))
                        {
                            continue;
                        }
                        if (targetLevel >= 0)
                        {
                            if (!hasFirstValid)
                            {
                                hasFirstValid = true;
                                firstValid = candidate;
                            }
                            if (!simulated.Add((candidate.x, candidate.y)))
                            {
                                continue;
                            }
                            if (simulations++ >= MaxSimulatedCandidates)
                            {
                                result = hasPartial ? bestPartial : firstValid;
                                return true;
                            }
                            int depth = SimulatedChainDepth(candidate, target, targetLevel);
                            if (depth < levels.Count - 1 - targetLevel)
                            {
                                if (depth > bestDepth)
                                {
                                    bestDepth = depth;
                                    bestPartial = candidate;
                                    hasPartial = true;
                                }
                                continue;
                            }
                        }
                        result = candidate;
                        return true;
                    }
                }
                if (hasPartial)
                {
                    result = bestPartial;
                    return true;
                }
                return false;
            }

            // True if a spoke starting at p (on `loop`, level k) finds a valid joint on level `to`
            // (see FindSpokeTarget), without inserting anything.
            bool CanReach(float2 p, List<float2> loop, int to)
            {
                return NearestOnLevel(levels[to], p, out int targetLoop, out float2 nearest)
                    && FindSpokeTargetCore(p, loop, levels[to][targetLoop], nearest, null, -1, out float2 _);
            }

            // Number of rings a chain starting at p (on `loop`, level k) would cross towards the
            // centre, following the same rule as the construction (see Project), without inserting
            // anything.
            int SimulatedChainDepth(float2 p, List<float2> loop, int k)
            {
                float2 current = p;
                List<float2> currentLoop = loop;
                int depth = 0;
                for (int level = k + 1; level < levels.Count; level++)
                {
                    if (!NearestOnLevel(levels[level], current, out int targetLoop, out float2 nearest)
                        || !FindSpokeTargetCore(current, currentLoop, levels[level][targetLoop], nearest, null, -1, out float2 next))
                    {
                        break;
                    }
                    // Same rules as Project for a chain already placed nearby: join it (it already
                    // reaches the centre) if the angle is open, otherwise move away from it.
                    List<float2> targetPoints = chainPoints[level][targetLoop];
                    float2 joined = next;
                    bool nearChain = targetPoints.Exists(existing =>
                    {
                        joined = existing;
                        return math.distance(existing, next) < mergeDistance;
                    });
                    if (nearChain)
                    {
                        if (JoinsNodeAtOpenAngle(joined, current, levels[level][targetLoop]))
                        {
                            return levels.Count - 1 - k;
                        }
                        if (!FindSpokeTargetCore(current, currentLoop, levels[level][targetLoop], nearest, targetPoints, -1, out next))
                        {
                            break;
                        }
                    }
                    current = next;
                    currentLoop = levels[level][targetLoop];
                    depth++;
                }
                return depth;
            }

            // Start point of a chain: the spread point `seed`, or the nearest one along the ring
            // (up to `maxShift`) from which the chain can reach the outside AND, in one piece, the
            // innermost ring. User feedback: "the connection does not go to the centre" (chain
            // starting on the axis of a lobe, blocked by the folds of the rings), then "the road is
            // not continuous" when a blocked spoke restarted from another point of the ring — hence
            // choosing the start upstream, over the whole simulated chain.
            float2 ViableSeed(List<float2> loop, float2 seed, int k, float maxShift)
            {
                float along = ArcPosition(loop, seed);
                int fullDepth = levels.Count - 1 - k;
                float2 best = seed;
                int bestDepth = -1;
                int simulations = 0;
                var simulated = new HashSet<(float, float)>();
                for (float shift = 0f; shift <= maxShift && simulations < 2 * MaxSimulatedCandidates; shift += SpokeSearchStep)
                {
                    foreach (float sign in shift == 0f ? new[] { 1f } : new[] { -1f, 1f })
                    {
                        float2 candidate = SnappedJoint(loop, PointAtArc(loop, along + sign * shift));
                        if (!simulated.Add((candidate.x, candidate.y)) || !CanReach(candidate, loop, k - 1))
                        {
                            continue;
                        }
                        simulations++;
                        int depth = SimulatedChainDepth(candidate, loop, k);
                        if (depth >= fullDepth)
                        {
                            return candidate;
                        }
                        if (depth > bestDepth)
                        {
                            bestDepth = depth;
                            best = candidate;
                        }
                    }
                }
                return best;
            }

            // True if a spoke p -> node would make an open angle with the ring at node and with
            // every spoke already joined at node (see MinSpokeRingAngleDegrees).
            bool JoinsNodeAtOpenAngle(float2 node, float2 p, List<float2> ring)
            {
                float2 incoming = p - node;
                if (!MeetsRingAtOpenAngle(ring, node, incoming))
                {
                    return false;
                }
                float2 direction = math.normalizesafe(incoming);
                float cosLimit = math.cos(math.radians(MinSpokeRingAngleDegrees));
                foreach (RoadSegmentDef spoke in segments)
                {
                    float2 other;
                    if (math.all(spoke.Start.xz == node))
                    {
                        other = spoke.End.xz;
                    }
                    else if (math.all(spoke.End.xz == node))
                    {
                        other = spoke.Start.xz;
                    }
                    else
                    {
                        continue;
                    }
                    if (math.dot(math.normalizesafe(other - node), direction) > cosLimit)
                    {
                        return false;
                    }
                }
                return true;
            }

            void AddSpoke(float2 a, float2 b)
            {
                segments.Add(new RoadSegmentDef(new float3(a.x, y, a.y), new float3(b.x, y, b.y), isHorizontal: false));
            }

            // Projects p (on a ring of level `from`) to level `to` (to = from ± 1); returns the
            // arrival node (merged or inserted), or null if too far. `sourceLoop`: the ring (level
            // `to` ± 1) that p lies on — the spoke must also leave it at an open angle (see
            // MeetsRingAtOpenAngle).
            float2? Project(float2 p, List<float2> sourceLoop, int to, bool inward)
            {
                if (!NearestOnLevel(levels[to], p, out int targetLoop, out float2 nearest))
                {
                    return null;
                }
                // Nearest point first; if it falls in the fold of a ring (closed angle, see
                // MeetsRingAtOpenAngle), the spoke slides along the target ring to the first point
                // where it arrives at an open angle on both sides without crossing anything. User
                // feedback: stopping there left spokes that did not reach the centre ("the
                // connection does not go to the centre") — the spoke is merely a little skewed in
                // those areas.
                int lookaheadLevel = inward ? to : -1;
                if (!FindSpokeTarget(p, sourceLoop, levels[to][targetLoop], nearest, null, lookaheadLevel, out float2 q))
                {
                    return null;
                }
                if (to == 0)
                {
                    return q; // perimeter = existing road, connected by the game layer
                }
                List<float2> targetPoints = chainPoints[to][targetLoop];
                foreach (float2 existing in targetPoints)
                {
                    if (math.distance(existing, q) < mergeDistance)
                    {
                        // Two converging chains meet at the same node — only if the new spoke
                        // arrives there at an open angle, with the ring AND with the spokes already
                        // joined there (seen on the real perimeter: two spokes at 23° at the same
                        // node). Otherwise, rather than stopping (user feedback: "the connection
                        // does not go to the centre"), the chain looks for its own joint, away from
                        // the nodes already placed on this ring, and continues towards the centre.
                        if (JoinsNodeAtOpenAngle(existing, p, levels[to][targetLoop]))
                        {
                            return existing;
                        }
                        if (!FindSpokeTarget(p, sourceLoop, levels[to][targetLoop], nearest, targetPoints, lookaheadLevel, out q))
                        {
                            return null;
                        }
                        break;
                    }
                }
                q = InsertOnLoop(levels[to][targetLoop], q, snapDistance: JointSnapDistance);
                targetPoints.Add(q);
                return q;
            }

            // Chain nodes already extended towards the centre.
            var extended = new HashSet<(float, float)>();

            // Extends the chain through p (on `loop`, level k) to the centre, one whole chain at a
            // time: each joint is chosen knowing the chains already placed (see FindSpokeTarget and
            // its simulation of the rest). When built ring by ring, a chain placed later
            // invalidated another chain's planned path, which then stopped.
            void ExtendChain(float2 p, List<float2> loop, int k)
            {
                if (!extended.Add((p.x, p.y)))
                {
                    return;
                }
                float2 current = p;
                List<float2> currentLoop = loop;
                for (int level = k + 1; level < levels.Count; level++)
                {
                    float2? inner = Project(current, currentLoop, level, inward: true);
                    if (!inner.HasValue)
                    {
                        break;
                    }
                    AddSpoke(current, inner.Value);
                    if (!extended.Add((inner.Value.x, inner.Value.y)))
                    {
                        break; // joined a chain already extended to the centre
                    }
                    NearestOnLevel(levels[level], inner.Value, out int nextLoop, out float2 _);
                    current = inner.Value;
                    currentLoop = levels[level][nextLoop];
                }
            }

            for (int k = 1; k < levels.Count; k++)
            {
                cancel.ThrowIfCancellationRequested();
                for (int li = 0; li < levels[k].Count; li++)
                {
                    List<float2> loop = levels[k][li];
                    List<float2> points = chainPoints[k][li];
                    if (points.Count == 0)
                    {
                        // Ring that no chain reaches (the first one, or an isolated branch): its
                        // own start points, linked outwards (to the perimeter for the first ring).
                        // Each is chosen AFTER the previous chains have been laid down to the
                        // centre (see ViableSeed), then its own chain is laid in turn.
                        float length = LoopLength(loop);
                        int count = math.clamp((int)math.floor(length / (1.5f * spacing)), 1, connections);
                        float maxSeedShift = 0.5f * length / count;
                        foreach (float2 seed in SeedPoints(loop, count, spacing))
                        {
                            float2 start = InsertOnLoop(loop, ViableSeed(loop, seed, k, maxSeedShift), snapDistance: JointSnapDistance);
                            if (points.Exists(existing => math.distance(existing, start) < 1e-3f))
                            {
                                continue;
                            }
                            points.Add(start);
                            float2? outer = Project(start, loop, k - 1, inward: false);
                            if (outer.HasValue)
                            {
                                AddSpoke(outer.Value, start);
                            }
                            if (k + 1 < levels.Count)
                            {
                                ExtendChain(start, loop, k);
                            }
                        }
                    }

                    // Chains that arrived on this ring from outside and are not yet extended.
                    if (k + 1 < levels.Count)
                    {
                        foreach (float2 p in points.ToArray())
                        {
                            ExtendChain(p, loop, k);
                        }
                    }
                }
            }
        }

        /// <summary>Step (m) of the search for a spoke joint along the target ring (see FindSpokeTarget).</summary>
        private const float SpokeSearchStep = 8f;

        /// <summary>Maximum number of candidate joints whose chain continuation is simulated per choice (see FindSpokeTarget): bounds generation time.</summary>
        private const int MaxSimulatedCandidates = 200;

        /// <summary>Reach of this search, on each side of the nearest point, in number of ring spacings.</summary>
        private const float SpokeSearchSpacings = 3f;

        /// <summary>Arc-length position (m) of the point of `loop` closest to `q`.</summary>
        private static float ArcPosition(List<float2> loop, float2 q)
        {
            float bestDistance = float.MaxValue;
            float along = 0f;
            float walked = 0f;
            for (int i = 0; i < loop.Count; i++)
            {
                float2 a = loop[i];
                float2 b = loop[(i + 1) % loop.Count];
                float2 closest = ClosestOnSegment(a, b, q);
                float d = math.distancesq(closest, q);
                if (d < bestDistance)
                {
                    bestDistance = d;
                    along = walked + math.distance(a, closest);
                }
                walked += math.distance(a, b);
            }
            return along;
        }

        /// <summary>Position the joint at `point` will take once inserted (same rule as InsertOnLoop: existing vertex within JointSnapDistance).</summary>
        private static float2 SnappedJoint(List<float2> loop, float2 point)
        {
            int bestEdge = 0;
            float bestDistance = float.MaxValue;
            float2 bestPoint = point;
            for (int i = 0; i < loop.Count; i++)
            {
                float2 projected = ClosestOnSegment(loop[i], loop[(i + 1) % loop.Count], point);
                float d = math.distancesq(projected, point);
                if (d < bestDistance)
                {
                    bestDistance = d;
                    bestEdge = i;
                    bestPoint = projected;
                }
            }
            float2 a = loop[bestEdge];
            float2 b = loop[(bestEdge + 1) % loop.Count];
            float toA = math.distance(a, bestPoint);
            float toB = math.distance(b, bestPoint);
            if (math.min(toA, toB) < JointSnapDistance)
            {
                return toA <= toB ? a : b;
            }
            return bestPoint;
        }

        /// <summary>True if [a1, a2] and [b1, b2] cross cleanly, more than 1 m from their ends.</summary>
        private static bool SegmentsCrossAwayFromEnds(float2 a1, float2 a2, float2 b1, float2 b2)
        {
            float2 r = a2 - a1;
            float2 s2 = b2 - b1;
            float denominator = r.x * s2.y - r.y * s2.x;
            if (math.abs(denominator) < 1e-6f)
            {
                return false;
            }
            float2 w = b1 - a1;
            float t = (w.x * s2.y - w.y * s2.x) / denominator;
            float u = (w.x * r.y - w.y * r.x) / denominator;
            float marginA = 1f / math.max(math.length(r), 1e-3f);
            float marginB = 1f / math.max(math.length(s2), 1e-3f);
            return t > marginA && t < 1f - marginA && u > marginB && u < 1f - marginB;
        }

        /// <summary>Minimum angle (degrees) between a spoke and each of the two branches of the ring it joins.</summary>
        private const float MinSpokeRingAngleDegrees = 50f;

        /// <summary>Distances (m) along the ring, on either side of the joint, at which this angle is measured.</summary>
        private static readonly float[] SpokeAngleProbeReaches = { 10f, 20f, 35f, 50f };

        /// <summary>
        /// True if a spoke arriving on `loop` at point `q`, in direction `towardOther`, makes an
        /// open angle there with BOTH branches of the ring. User feedback (in-game screenshot,
        /// "Overlapping objects" error): on a lobe, both spokes followed the lobe axis and passed
        /// through the tip of each ring, which folds there; the spoke entered the fold at ~44° to
        /// each branch and the road edges overlapped near the junction.
        /// </summary>
        private static bool MeetsRingAtOpenAngle(List<float2> loop, float2 q, float2 towardOther)
        {
            float2 direction = math.normalizesafe(towardOther);
            if (loop.Count < 3 || math.lengthsq(direction) < 1e-6f)
            {
                return true;
            }
            // Position of q along the ring (arc length).
            float bestDistance = float.MaxValue;
            float along = 0f;
            float walked = 0f;
            for (int i = 0; i < loop.Count; i++)
            {
                float2 a = loop[i];
                float2 b = loop[(i + 1) % loop.Count];
                float2 closest = ClosestOnSegment(a, b, q);
                float d = math.distancesq(closest, q);
                if (d < bestDistance)
                {
                    bestDistance = d;
                    along = walked + math.distance(a, closest);
                }
                walked += math.distance(a, b);
            }
            float cosLimit = math.cos(math.radians(MinSpokeRingAngleDegrees));
            // Several distances along each branch: a ring may leave at an open angle and then curl
            // back towards the spoke (seen in game, collision diagnostics log: 54° at the node but
            // 35° at 40 m, roads side by side and rejected by the game).
            foreach (float reach in SpokeAngleProbeReaches)
            {
                foreach (float sign in new[] { -1f, 1f })
                {
                    float2 branch = math.normalizesafe(PointAtArc(loop, along + sign * reach) - q);
                    if (math.dot(branch, direction) > cosLimit)
                    {
                        return false;
                    }
                }
            }
            return true;
        }

        /// <summary>
        /// `count` points spread evenly along the ring, choosing the start offset that keeps them
        /// farthest from the marked corners — a spoke starting from a corner would head diagonally
        /// towards one of the two outer sides.
        /// </summary>
        private static List<float2> SeedPoints(List<float2> loop, int count, float spacing)
        {
            float length = LoopLength(loop);
            var corners = new List<float>();
            float arc = 0f;
            for (int i = 0; i < loop.Count; i++)
            {
                float2 prev = loop[(i - 1 + loop.Count) % loop.Count];
                float2 curr = loop[i];
                float2 next = loop[(i + 1) % loop.Count];
                float2 d1 = math.normalizesafe(curr - prev);
                float2 d2 = math.normalizesafe(next - curr);
                if (math.dot(d1, d2) < math.cos(math.radians(35f)))
                {
                    corners.Add(arc);
                }
                arc += math.distance(curr, next);
            }

            float step = length / count;
            float bestOffset = step * 0.5f;
            float bestScore = float.MinValue;
            const int candidates = 24;
            for (int c = 0; c < candidates; c++)
            {
                float offset = step * c / candidates;
                float score = float.MaxValue;
                for (int i = 0; i < count; i++)
                {
                    float s = offset + i * step;
                    foreach (float corner in corners)
                    {
                        float d = math.abs(s - corner);
                        d = math.min(d, length - d);
                        score = math.min(score, d);
                    }
                }
                if (score > bestScore)
                {
                    bestScore = score;
                    bestOffset = offset;
                }
            }

            var result = new List<float2>(count);
            for (int i = 0; i < count; i++)
            {
                result.Add(PointAtArc(loop, bestOffset + i * step));
            }
            return result;
        }

        /// <summary>
        /// Cumulative lengths per ring (cumulative[i] = arc length at vertex i, last element =
        /// total length), recomputed as soon as the ring gains a vertex (see InsertOnLoop, the only
        /// modification while spokes are laid). PointAtArc is called hundreds of thousands of times
        /// by the chain simulation (see FindSpokeTarget): walking the whole ring on each call made
        /// generation slow (~2 s with 12 spokes). Per thread, because tests may generate in
        /// parallel; reset at the start and end of each EmitRadialChains call, so nothing survives
        /// between calls.
        /// </summary>
        [ThreadStatic]
        private static Dictionary<List<float2>, float[]> s_CumulativeLengths;

        private static float[] CumulativeLengths(List<float2> loop)
        {
            if (s_CumulativeLengths == null)
            {
                s_CumulativeLengths = new Dictionary<List<float2>, float[]>();
            }
            if (s_CumulativeLengths.TryGetValue(loop, out float[] cached) && cached.Length == loop.Count + 1)
            {
                return cached;
            }
            var cumulative = new float[loop.Count + 1];
            for (int i = 0; i < loop.Count; i++)
            {
                cumulative[i + 1] = cumulative[i] + math.distance(loop[i], loop[(i + 1) % loop.Count]);
            }
            s_CumulativeLengths[loop] = cumulative;
            return cumulative;
        }

        private static float2 PointAtArc(List<float2> loop, float s)
        {
            float[] cumulative = CumulativeLengths(loop);
            float length = cumulative[loop.Count];
            if (length <= 0f)
            {
                return loop[0];
            }
            s = ((s % length) + length) % length;
            // Last vertex i with cumulative[i] <= s (binary search).
            int lo = 0;
            int hi = loop.Count - 1;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (cumulative[mid] <= s)
                {
                    lo = mid;
                }
                else
                {
                    hi = mid - 1;
                }
            }
            float2 a = loop[lo];
            float2 b = loop[(lo + 1) % loop.Count];
            float edge = cumulative[lo + 1] - cumulative[lo];
            return edge > 0f ? math.lerp(a, b, (s - cumulative[lo]) / edge) : a;
        }

        /// <summary>
        /// Distance (m) under which a spoke connects to an existing ring vertex instead of
        /// inserting a new one next to it. User feedback (in-game screenshot, "Overlapping objects"
        /// error on the real perimeter): with 3 m, joints landed 3-15 m from a neighbouring vertex
        /// and left tiny segments attached to the junction, which the game rejects. Ring vertices
        /// are about RingSampleStep (25 m) apart: a spoke shifts by at most a dozen metres,
        /// invisible at the scale of a ring.
        /// </summary>
        private const float JointSnapDistance = 15f;

        /// <summary>
        /// Inserts `point` (assumed to be on the ring) as a vertex, on the nearest edge. Within
        /// `snapDistance` of an existing vertex, reuses that vertex. Returns the EXACT vertex
        /// position, to be reused as-is as the spoke end.
        /// </summary>
        private static float2 InsertOnLoop(List<float2> loop, float2 point, float snapDistance)
        {
            int bestEdge = 0;
            float bestDistance = float.MaxValue;
            float2 bestPoint = point;
            for (int i = 0; i < loop.Count; i++)
            {
                float2 projected = ClosestOnSegment(loop[i], loop[(i + 1) % loop.Count], point);
                float d = math.distancesq(projected, point);
                if (d < bestDistance)
                {
                    bestDistance = d;
                    bestEdge = i;
                    bestPoint = projected;
                }
            }
            float2 a = loop[bestEdge];
            float2 b = loop[(bestEdge + 1) % loop.Count];
            // The nearer of the two ends (not the first one under the threshold): otherwise a point
            // that IS vertex b was "snapped" to a, a neighbour within snapDistance — the spoke then
            // started from a different vertex than the one chosen (and simulated) by
            // FindSpokeTarget.
            float toA = math.distance(a, bestPoint);
            float toB = math.distance(b, bestPoint);
            if (math.min(toA, toB) < snapDistance)
            {
                return toA <= toB ? a : b;
            }
            loop.Insert(bestEdge + 1, bestPoint);
            return bestPoint;
        }

        private static bool NearestOnLevel(List<List<float2>> loops, float2 point, out int loopIndex, out float2 nearest)
        {
            loopIndex = -1;
            nearest = point;
            float best = float.MaxValue;
            for (int li = 0; li < loops.Count; li++)
            {
                List<float2> loop = loops[li];
                for (int i = 0; i < loop.Count; i++)
                {
                    float2 q = ClosestOnSegment(loop[i], loop[(i + 1) % loop.Count], point);
                    float d = math.distancesq(q, point);
                    if (d < best)
                    {
                        best = d;
                        nearest = q;
                        loopIndex = li;
                    }
                }
            }
            return loopIndex >= 0;
        }

        // ------------------------------------------------------------------
        // Geometry
        // ------------------------------------------------------------------

        private static float2 ClosestOnSegment(float2 a, float2 b, float2 p)
        {
            float2 ab = b - a;
            float lenSq = math.lengthsq(ab);
            if (lenSq < 1e-8f)
            {
                return a;
            }
            float t = math.clamp(math.dot(p - a, ab) / lenSq, 0f, 1f);
            return a + t * ab;
        }

        private static float LoopLength(List<float2> loop)
        {
            float length = 0f;
            for (int i = 0; i < loop.Count; i++)
            {
                length += math.distance(loop[i], loop[(i + 1) % loop.Count]);
            }
            return length;
        }

        private static float SignedArea(List<float2> polygon)
        {
            float area = 0f;
            for (int i = 0; i < polygon.Count; i++)
            {
                float2 a = polygon[i];
                float2 b = polygon[(i + 1) % polygon.Count];
                area += a.x * b.y - b.x * a.y;
            }
            return area * 0.5f;
        }

        private static List<float2> CleanPolygon(List<float2> pts)
        {
            var result = new List<float2>(pts.Count);
            foreach (float2 p in pts)
            {
                if (result.Count == 0 || math.distance(result[result.Count - 1], p) > 0.01f)
                {
                    result.Add(p);
                }
            }
            if (result.Count > 1 && math.distance(result[0], result[result.Count - 1]) <= 0.01f)
            {
                result.RemoveAt(result.Count - 1);
            }

            // Back-and-forth spikes in the outline: see GridGenerator.RemoveBacktracks. The game
            // layer already removes them when it builds the curve-aware perimeter
            // (BuildCurveAwarePerimeterPositions); redone here for any direct caller (tests,
            // another perimeter source). Seen in practice: a 179° turn over 5 m passed for an
            // ultra-acute sharp corner whose influence zone (ComputeSharpCornerWeights) covered the
            // whole shape — every ring spike in it was kept as a "corner", never rounded.
            GridGenerator.RemoveBacktracks(result);
            return result;
        }

        /// <summary>Douglas-Peucker on a closed polyline (split in two at the point farthest from the first).</summary>
        internal static List<float2> SimplifyClosed(List<float2> loop, float tolerance)
        {
            if (loop.Count < 4)
            {
                return new List<float2>(loop);
            }
            int far = 0;
            float farDistance = -1f;
            for (int i = 1; i < loop.Count; i++)
            {
                float d = math.distancesq(loop[0], loop[i]);
                if (d > farDistance)
                {
                    farDistance = d;
                    far = i;
                }
            }
            var keep = new bool[loop.Count];
            keep[0] = true;
            keep[far] = true;
            SimplifyRange(loop, 0, far, tolerance, keep);
            SimplifyRange(loop, far, loop.Count, tolerance, keep);
            var result = new List<float2>();
            for (int i = 0; i < loop.Count; i++)
            {
                if (keep[i])
                {
                    result.Add(loop[i]);
                }
            }
            return result;
        }

        /// <summary>Douglas-Peucker between indices first and last (last may equal loop.Count = back to vertex 0).</summary>
        private static void SimplifyRange(List<float2> loop, int first, int last, float tolerance, bool[] keep)
        {
            if (last - first < 2)
            {
                return;
            }
            float2 a = loop[first];
            float2 b = loop[last % loop.Count];
            int index = -1;
            float maxDistance = tolerance;
            for (int i = first + 1; i < last; i++)
            {
                float d = math.distance(ClosestOnSegment(a, b, loop[i]), loop[i]);
                if (d > maxDistance)
                {
                    maxDistance = d;
                    index = i;
                }
            }
            if (index < 0)
            {
                return;
            }
            keep[index] = true;
            SimplifyRange(loop, first, index, tolerance, keep);
            SimplifyRange(loop, index, last, tolerance, keep);
        }

        // ------------------------------------------------------------------
        // Distance field + marching squares
        // ------------------------------------------------------------------

        private sealed class DistanceField
        {
            public float CellSize;
            public float MaxDepth;
            /// <summary>Grid point farthest from the perimeter (centre of the largest inscribed circle).</summary>
            public float2 DeepestPoint;
            /// <summary>See ComputeConnectedDepth.</summary>
            public float ConnectedDepth;
            /// <summary>See ConcentricGenerator.MaxLayersFor; -1 = not computed yet. Lives on this per-call field, not in a static cache.</summary>
            public int MaxLayersCache = -1;
            private float2 _origin;
            private int _nx;
            private int _nz;
            private float[] _values; // signed distance to the perimeter: > 0 inside
            private float[] _sharpWeight; // see ComputeSharpCornerWeights; null = no sharp corner
            private float[] _sharpTurn; // turn (degrees) of the perimeter corner that dominates _sharpWeight

            public static DistanceField Build(List<float2> polygon)
            {
                float2 mn = polygon[0];
                float2 mx = polygon[0];
                foreach (float2 p in polygon)
                {
                    mn = math.min(mn, p);
                    mx = math.max(mx, p);
                }
                float span = math.cmax(mx - mn);
                float cell = math.clamp(span / TargetCellsPerAxis, MinCellSize, MaxCellSize);

                var field = new DistanceField { CellSize = cell };
                // One row of margin all around (negative values): every contour is closed.
                field._origin = mn - cell;
                field._nx = (int)math.ceil((mx.x - mn.x) / cell) + 3;
                field._nz = (int)math.ceil((mx.y - mn.y) / cell) + 3;
                field._values = new float[field._nx * field._nz];

                int n = polygon.Count;
                var edgeA = new float2[n];
                var edgeAB = new float2[n];
                var edgeInvLenSq = new float[n];
                for (int i = 0; i < n; i++)
                {
                    edgeA[i] = polygon[i];
                    edgeAB[i] = polygon[(i + 1) % n] - polygon[i];
                    float lenSq = math.lengthsq(edgeAB[i]);
                    edgeInvLenSq[i] = lenSq > 1e-8f ? 1f / lenSq : 0f;
                }

                // Spatial index of the edges in buckets: for a given point, buckets are visited in
                // wider and wider rings and the search stops as soon as the best distance found is
                // smaller than the minimum possible distance to the next ring — instead of testing
                // every edge for every point (1.1 s on a roundabout densified to 525 points, versus
                // a few tens of ms this way).
                float bucketSize = math.max(cell * 8f, span / 32f);
                int bx = (int)math.ceil((field._nx * cell) / bucketSize) + 1;
                int bz = (int)math.ceil((field._nz * cell) / bucketSize) + 1;
                var buckets = new List<int>[bx * bz];
                for (int e = 0; e < n; e++)
                {
                    float2 lo = math.min(edgeA[e], edgeA[e] + edgeAB[e]) - field._origin;
                    float2 hi = math.max(edgeA[e], edgeA[e] + edgeAB[e]) - field._origin;
                    int x0 = math.clamp((int)math.floor(lo.x / bucketSize), 0, bx - 1);
                    int x1 = math.clamp((int)math.floor(hi.x / bucketSize), 0, bx - 1);
                    int z0 = math.clamp((int)math.floor(lo.y / bucketSize), 0, bz - 1);
                    int z1 = math.clamp((int)math.floor(hi.y / bucketSize), 0, bz - 1);
                    for (int zz = z0; zz <= z1; zz++)
                    {
                        for (int xx = x0; xx <= x1; xx++)
                        {
                            ref List<int> list = ref buckets[zz * bx + xx];
                            if (list == null)
                            {
                                list = new List<int>(4);
                            }
                            list.Add(e);
                        }
                    }
                }
                var visitedStamp = new int[n];
                int stamp = 0;

                var crossings = new List<float>();
                float maxDepth = 0f;
                for (int j = 0; j < field._nz; j++)
                {
                    // Inside/outside by scanning the row (even-odd, same rule as
                    // GridGenerator.PointInPolygon): x-coordinates where the row crosses the
                    // polygon.
                    float rowY = field._origin.y + j * cell;
                    crossings.Clear();
                    for (int e = 0; e < n; e++)
                    {
                        float2 a = edgeA[e];
                        float2 b = a + edgeAB[e];
                        if ((a.y > rowY) != (b.y > rowY))
                        {
                            crossings.Add((b.x - a.x) * (rowY - a.y) / (b.y - a.y) + a.x);
                        }
                    }
                    crossings.Sort();
                    int crossingIndex = 0;

                    for (int i = 0; i < field._nx; i++)
                    {
                        float2 p = field._origin + new float2(i, j) * cell;
                        while (crossingIndex < crossings.Count && crossings[crossingIndex] <= p.x)
                        {
                            crossingIndex++;
                        }
                        bool inside = (crossingIndex & 1) == 1;

                        stamp++;
                        float best = float.MaxValue;
                        int pbx = math.clamp((int)math.floor((p.x - field._origin.x) / bucketSize), 0, bx - 1);
                        int pbz = math.clamp((int)math.floor((p.y - field._origin.y) / bucketSize), 0, bz - 1);
                        int maxRing = math.max(bx, bz);
                        for (int ring = 0; ring <= maxRing; ring++)
                        {
                            for (int zz = pbz - ring; zz <= pbz + ring; zz++)
                            {
                                if (zz < 0 || zz >= bz)
                                {
                                    continue;
                                }
                                bool edgeRow = zz == pbz - ring || zz == pbz + ring;
                                int step = edgeRow ? 1 : 2 * ring;
                                for (int xx = pbx - ring; xx <= pbx + ring; xx += math.max(step, 1))
                                {
                                    if (xx < 0 || xx >= bx)
                                    {
                                        continue;
                                    }
                                    List<int> list = buckets[zz * bx + xx];
                                    if (list == null)
                                    {
                                        continue;
                                    }
                                    foreach (int e in list)
                                    {
                                        if (visitedStamp[e] == stamp)
                                        {
                                            continue;
                                        }
                                        visitedStamp[e] = stamp;
                                        float2 a = edgeA[e];
                                        float2 ab = edgeAB[e];
                                        float t = math.clamp(math.dot(p - a, ab) * edgeInvLenSq[e], 0f, 1f);
                                        float distSq = math.lengthsq(p - (a + t * ab));
                                        if (distSq < best)
                                        {
                                            best = distSq;
                                        }
                                    }
                                }
                            }
                            // Every bucket in the next ring is at least ring × bucketSize away.
                            float reach = ring * bucketSize;
                            if (best <= reach * reach)
                            {
                                break;
                            }
                        }

                        float value = math.sqrt(best);
                        if (!inside)
                        {
                            value = -value;
                        }
                        field._values[j * field._nx + i] = value;
                        if (value > maxDepth)
                        {
                            maxDepth = value;
                            field.DeepestPoint = p;
                        }
                    }
                }
                field.MaxDepth = maxDepth;
                field.ComputeSharpCornerWeights(polygon);
                field.ConnectedDepth = field.ComputeConnectedDepth();
                field.ComputeRingField();
                return field;
            }

            /// <summary>
            /// Greatest depth up to which the region {distance ≥ depth} stays in one piece — beyond
            /// it, a narrow "waist" of the shape separates two lobes and the rings would split in
            /// two. Merge tree: grid points are added from deepest to shallowest (union-find), and
            /// the depth of the LAST merge between two parts both large enough to hold a ring is
            /// kept (small bumps of the field, which merge right away, are ignored).
            /// </summary>
            private float ComputeConnectedDepth()
            {
                var order = new List<int>();
                for (int idx = 0; idx < _values.Length; idx++)
                {
                    if (_values[idx] > 0f)
                    {
                        order.Add(idx);
                    }
                }
                order.Sort((a, b) => _values[b].CompareTo(_values[a]));

                var parent = new int[_values.Length];
                var size = new int[_values.Length];
                for (int idx = 0; idx < parent.Length; idx++)
                {
                    parent[idx] = -1; // not added yet
                }
                int Find(int x)
                {
                    while (parent[x] != x)
                    {
                        parent[x] = parent[parent[x]];
                        x = parent[x];
                    }
                    return x;
                }

                int significantCells = math.max(4, (int)(math.PI * MinLayerSpacing * MinLayerSpacing / (CellSize * CellSize)));
                float connectedDepth = MaxDepth;
                foreach (int idx in order)
                {
                    parent[idx] = idx;
                    size[idx] = 1;
                    int i = idx % _nx;
                    int j = idx / _nx;
                    for (int nIdx = 0; nIdx < 4; nIdx++)
                    {
                        int ni = i + (nIdx == 0 ? 1 : nIdx == 1 ? -1 : 0);
                        int nj = j + (nIdx == 2 ? 1 : nIdx == 3 ? -1 : 0);
                        if (ni < 0 || nj < 0 || ni >= _nx || nj >= _nz)
                        {
                            continue;
                        }
                        int neighbor = nj * _nx + ni;
                        if (parent[neighbor] < 0)
                        {
                            continue;
                        }
                        int ra = Find(idx);
                        int rb = Find(neighbor);
                        if (ra == rb)
                        {
                            continue;
                        }
                        if (size[ra] >= significantCells && size[rb] >= significantCells)
                        {
                            connectedDepth = _values[idx];
                        }
                        if (size[ra] < size[rb])
                        {
                            (ra, rb) = (rb, ra);
                        }
                        parent[rb] = ra;
                        size[ra] += size[rb];
                    }
                }
                return connectedDepth;
            }

            /// <summary>Signed distance (m) to the perimeter at point p, interpolated on the grid (> 0 inside).</summary>
            public float DepthAt(float2 p)
            {
                float2 g = (p - _origin) / CellSize;
                int i = math.clamp((int)math.floor(g.x), 0, _nx - 2);
                int j = math.clamp((int)math.floor(g.y), 0, _nz - 2);
                float fx = math.saturate(g.x - i);
                float fz = math.saturate(g.y - j);
                float v00 = _values[j * _nx + i];
                float v10 = _values[j * _nx + i + 1];
                float v01 = _values[(j + 1) * _nx + i];
                float v11 = _values[(j + 1) * _nx + i + 1];
                return math.lerp(math.lerp(v00, v10, fx), math.lerp(v01, v11, fx), fz);
            }

            /// <summary>Sharp-corner weight (see ComputeSharpCornerWeights) at the nearest grid point.</summary>
            public float SharpWeightAt(float2 p)
            {
                if (_sharpWeight == null)
                {
                    return 0f;
                }
                int i = math.clamp((int)math.round((p.x - _origin.x) / CellSize), 0, _nx - 1);
                int j = math.clamp((int)math.round((p.y - _origin.y) / CellSize), 0, _nz - 1);
                return _sharpWeight[j * _nx + i];
            }

            /// <summary>Turn (degrees) of the sharp perimeter corner this point depends on (see SharpWeightAt); 0 when there is no corner.</summary>
            public float SharpTurnAt(float2 p)
            {
                if (_sharpTurn == null)
                {
                    return 0f;
                }
                int i = math.clamp((int)math.round((p.x - _origin.x) / CellSize), 0, _nx - 1);
                int j = math.clamp((int)math.round((p.y - _origin.y) / CellSize), 0, _nz - 1);
                return _sharpTurn[j * _nx + i];
            }

            /// <summary>
            /// Weight 0..1 per grid point: 1 in the "wake" of a SHARP convex perimeter corner (its
            /// inward bisector), 0 elsewhere. See ComputeRingField: where the weight is 1, the ring
            /// keeps the sharp corner inherited from the perimeter (a square stays a square);
            /// elsewhere, spikes that only appear at depth (end of a rounded shape) are rounded.
            /// </summary>
            private void ComputeSharpCornerWeights(List<float2> polygon)
            {
                int n = polygon.Count;
                float windingSign = math.sign(SignedArea(polygon));
                float cornerCos = math.cos(math.radians(CornerTurnDegrees));
                var corners = new List<(float2 position, float sinHalfAngle, float turnDegrees)>();
                for (int i = 0; i < n; i++)
                {
                    float2 prev = polygon[(i - 1 + n) % n];
                    float2 curr = polygon[i];
                    float2 next = polygon[(i + 1) % n];
                    float2 dIn = math.normalizesafe(curr - prev);
                    float2 dOut = math.normalizesafe(next - curr);
                    float cross = dIn.x * dOut.y - dIn.y * dOut.x;
                    if (math.dot(dIn, dOut) >= cornerCos || cross * windingSign <= 0f)
                    {
                        continue; // not a sharp corner, or a concave corner (rounded naturally)
                    }
                    // Interior angle = π - turn angle; half-angle for the bisector.
                    float turn = math.acos(math.clamp(math.dot(dIn, dOut), -1f, 1f));
                    float halfInterior = 0.5f * (math.PI - turn);
                    corners.Add((curr, math.max(math.sin(halfInterior), 0.05f), math.degrees(turn)));
                }
                if (corners.Count == 0)
                {
                    return;
                }

                _sharpWeight = new float[_values.Length];
                _sharpTurn = new float[_values.Length];
                for (int j = 0; j < _nz; j++)
                {
                    for (int i = 0; i < _nx; i++)
                    {
                        int idx = j * _nx + i;
                        float depth = _values[idx];
                        if (depth <= 0f)
                        {
                            continue;
                        }
                        float2 p = Position(i, j);
                        float weight = 0f;
                        float turnOfBest = 0f;
                        foreach ((float2 c, float sinHalf, float cornerTurn) in corners)
                        {
                            // On a corner's bisector, distance to the corner = depth /
                            // sin(half-angle).
                            float q = math.distance(p, c) * sinHalf / depth;
                            float w = 1f - math.smoothstep(1.1f, 1.5f, q);
                            if (w > weight || (w == weight && w > 0f && cornerTurn > turnOfBest))
                            {
                                weight = w;
                                turnOfBest = cornerTurn;
                            }
                        }
                        _sharpWeight[idx] = weight;
                        _sharpTurn[idx] = turnOfBest;
                    }
                }
            }

            /// <summary>Average depth (m) of the spine: the deepest possible ring (see LevelForDepth).</summary>
            public float RingDepth;

            /// <summary>
            /// Field whose level curves are the rings: 0 on the perimeter, 1 on the spine (see
            /// ComputeRingField), blended near sharp perimeter corners with the exact distance (see
            /// ComputeSharpCornerWeights) so that a square keeps sharp corners.
            /// </summary>
            public float[] RingField;

            // Table level -> average depth of the points at that level (increasing), used to choose
            // the levels that give a regular average spacing.
            private float[] _levelDepth;
            private const int LevelBins = 256;

            /// <summary>Level of the smooth field whose average depth is `depth` (m).</summary>
            public float LevelForDepth(float depth)
            {
                for (int b = 1; b < LevelBins; b++)
                {
                    if (_levelDepth[b] >= depth)
                    {
                        float d0 = _levelDepth[b - 1];
                        float d1 = _levelDepth[b];
                        float t = d1 > d0 ? (depth - d0) / (d1 - d0) : 0f;
                        return (b - 1 + math.saturate(t)) / (LevelBins - 1);
                    }
                }
                return 1f;
            }

            /// <summary>
            /// Ring field G = F / H: F = exact distance to the perimeter, H = local half-width of
            /// the shape = depth of the "spine" point reached by following the steepest ascent of
            /// the distance field. The spine is the ridge of the field, kept only where it is at
            /// least as deep as the narrowest waist (0.97 × ConnectedDepth): one piece across the
            /// whole shape, without the ridge branches that head off to the corners and lobe tips.
            /// Each level curve is therefore at a fixed FRACTION of the local half-width — it goes
            /// round the whole shape without cutting itself or stopping in a point where the shape
            /// narrows (it tightens there), and stays parallel to straight sides (all points of a
            /// straight side ascend to the same place on the spine, so H is constant there). Folds
            /// in the distance field produce spiky rings: they are rounded afterwards, ring by ring
            /// (LimitCurvature), not by a blur of the field, which rounded the whole shape.
            /// </summary>
            private void ComputeRingField()
            {
                int count = _values.Length;
                float spineLevel = 0.97f * math.min(ConnectedDepth, MaxDepth);
                var spine = new bool[count];
                int spineCount = 0;
                int maxIdx = 0;
                for (int j = 0; j < _nz; j++)
                {
                    for (int i = 0; i < _nx; i++)
                    {
                        int idx = j * _nx + i;
                        if (_values[idx] > _values[maxIdx])
                        {
                            maxIdx = idx;
                        }
                        if (_values[idx] >= spineLevel && IsRidge(i, j))
                        {
                            spine[idx] = true;
                            spineCount++;
                        }
                    }
                }
                if (spineCount == 0)
                {
                    spine[maxIdx] = true;
                }
                // Smoothed half-width (blur restricted to the inside of the shape): raw, it jumps
                // from one value to another where two parts of different widths meet (e.g. a narrow
                // passage opening onto a lobe), and the rings bunched up against that jump line
                // (only a few metres apart).
                float[] halfWidth = MaskedBlur(HalfWidthByAscent(spine), math.max(2f, 0.5f * ConnectedDepth / CellSize));

                // EXACT distance, no blur (user feedback: "it is creating more and more circular
                // shapes instead of keeping the original shape"). A Gaussian blur of the distance
                // field (σ = ¼ of the depth, ~150 m on a large shape) rounded the spikes, but also
                // the whole shape: inner rings tended towards circles. It had only ever been seen
                // in game by accident — a false corner (a back-and-forth in the perimeter, see
                // CleanPolygon) forced the corner weight to 1 everywhere, hence the exact distance.
                // Spikes are now rounded locally, ring by ring (LimitCurvature), without touching
                // the rest.
                var ring = new float[count];
                for (int idx = 0; idx < count; idx++)
                {
                    if (_values[idx] <= 0f)
                    {
                        ring[idx] = -1f;
                        continue;
                    }
                    ring[idx] = math.saturate(_values[idx] / math.max(halfWidth[idx], CellSize));
                }
                RingField = ring;

                // Average depth per level (increasing table), and spine depth.
                var sum = new double[LevelBins];
                var hits = new int[LevelBins];
                for (int idx = 0; idx < count; idx++)
                {
                    if (_values[idx] <= 0f)
                    {
                        continue;
                    }
                    int bin = math.clamp((int)math.round(math.saturate(ring[idx]) * (LevelBins - 1)), 0, LevelBins - 1);
                    sum[bin] += _values[idx];
                    hits[bin]++;
                }
                _levelDepth = new float[LevelBins];
                float previous = 0f;
                for (int b = 0; b < LevelBins; b++)
                {
                    float mean = b == 0 ? 0f : hits[b] > 0 ? (float)(sum[b] / hits[b]) : previous;
                    _levelDepth[b] = math.max(mean, previous);
                    previous = _levelDepth[b];
                }
                RingDepth = _levelDepth[LevelBins - 1];
            }

            /// <summary>
            /// Gaussian blur restricted to the inside of the shape (value > 0): weighted average of
            /// the inside points only, so that the outside (0) does not pull values down near the
            /// edge.
            /// </summary>
            private float[] MaskedBlur(float[] source, float sigmaCells)
            {
                int count = source.Length;
                var weighted = new float[count];
                var mask = new float[count];
                for (int idx = 0; idx < count; idx++)
                {
                    if (_values[idx] > 0f)
                    {
                        weighted[idx] = source[idx];
                        mask[idx] = 1f;
                    }
                }
                float[] blurredValue = GaussianBlur(weighted, sigmaCells);
                float[] blurredMask = GaussianBlur(mask, sigmaCells);
                var result = new float[count];
                for (int idx = 0; idx < count; idx++)
                {
                    result[idx] = blurredMask[idx] > 1e-4f ? blurredValue[idx] / blurredMask[idx] : source[idx];
                }
                return result;
            }

            /// <summary>Separable Gaussian blur (standard deviation in cells), edges extended with the edge value.</summary>
            private float[] GaussianBlur(float[] source, float sigmaCells)
            {
                int radius = math.max(1, (int)math.ceil(3f * sigmaCells));
                // Large radius (large shape, e.g. an area painted with the Brush: kernel of ~300
                // cells, tens of millions of operations, up to 0.5 s in game for each new shape):
                // three successive box blurs instead, cost independent of the radius, nearly
                // identical result.
                if (radius > BoxBlurFromRadius)
                {
                    return BoxGaussian(source, sigmaCells);
                }
                var kernel = new float[2 * radius + 1];
                float total = 0f;
                for (int k = -radius; k <= radius; k++)
                {
                    float value = math.exp(-0.5f * k * k / (sigmaCells * sigmaCells));
                    kernel[k + radius] = value;
                    total += value;
                }
                for (int k = 0; k < kernel.Length; k++)
                {
                    kernel[k] /= total;
                }

                var temp = new float[source.Length];
                for (int j = 0; j < _nz; j++)
                {
                    for (int i = 0; i < _nx; i++)
                    {
                        float acc = 0f;
                        for (int k = -radius; k <= radius; k++)
                        {
                            int ii = math.clamp(i + k, 0, _nx - 1);
                            acc += kernel[k + radius] * source[j * _nx + ii];
                        }
                        temp[j * _nx + i] = acc;
                    }
                }
                var result = new float[source.Length];
                for (int j = 0; j < _nz; j++)
                {
                    for (int i = 0; i < _nx; i++)
                    {
                        float acc = 0f;
                        for (int k = -radius; k <= radius; k++)
                        {
                            int jj = math.clamp(j + k, 0, _nz - 1);
                            acc += kernel[k + radius] * temp[jj * _nx + i];
                        }
                        result[j * _nx + i] = acc;
                    }
                }
                return result;
            }

            /// <summary>Gaussian kernel radius (cells) above which GaussianBlur switches to box blurs.</summary>
            private const int BoxBlurFromRadius = 12;

            /// <summary>
            /// Approximates a Gaussian blur with three box blurs (running sums), edges extended
            /// with the edge value like GaussianBlur. Box widths are chosen so that the total
            /// variance matches the Gaussian's.
            /// </summary>
            private float[] BoxGaussian(float[] source, float sigma)
            {
                const int passes = 3;
                float ideal = math.sqrt(12f * sigma * sigma / passes + 1f);
                int lower = (int)math.floor(ideal);
                if (lower % 2 == 0) lower--;
                int upper = lower + 2;
                int smaller = (int)math.round((12f * sigma * sigma - passes * lower * lower - 4f * passes * lower - 3f * passes) / (-4f * lower - 4f));
                float[] a = (float[])source.Clone();
                float[] b = new float[source.Length];
                for (int pass = 0; pass < passes; pass++)
                {
                    int r = ((pass < smaller ? lower : upper) - 1) / 2;
                    BoxPass(a, b, r, true);
                    BoxPass(b, a, r, false);
                }
                return a;
            }

            /// <summary>Running average of radius r along rows (horizontal) or columns.</summary>
            private void BoxPass(float[] from, float[] to, int r, bool horizontal)
            {
                int lines = horizontal ? _nz : _nx, length = horizontal ? _nx : _nz;
                int stride = horizontal ? 1 : _nx;
                float inv = 1f / (2 * r + 1);
                for (int line = 0; line < lines; line++)
                {
                    int start = horizontal ? line * _nx : line;
                    float At(int k) => from[start + math.clamp(k, 0, length - 1) * stride];
                    float acc = 0f;
                    for (int k = -r; k <= r; k++) acc += At(k);
                    for (int k = 0; k < length; k++)
                    {
                        to[start + k * stride] = acc * inv;
                        acc += At(k + r + 1) - At(k - r);
                    }
                }
            }

            /// <summary>
            /// Local half-width (m) of each point: depth of the spine point reached by following
            /// the steepest ascent of the distance field (computed in one pass, from deepest to
            /// shallowest). A point that ascends to a local maximum off the spine (a small bump)
            /// takes that maximum's depth.
            /// </summary>
            private float[] HalfWidthByAscent(bool[] spine)
            {
                int count = _values.Length;
                var result = new float[count];
                var order = new List<int>(count);
                for (int idx = 0; idx < count; idx++)
                {
                    if (_values[idx] > 0f)
                    {
                        order.Add(idx);
                    }
                }
                order.Sort((a, b) => _values[b].CompareTo(_values[a]));
                foreach (int idx in order)
                {
                    if (spine[idx])
                    {
                        result[idx] = _values[idx];
                        continue;
                    }
                    int i = idx % _nx;
                    int j = idx / _nx;
                    float best = _values[idx];
                    int bestIdx = -1;
                    for (int dj = -1; dj <= 1; dj++)
                    {
                        for (int di = -1; di <= 1; di++)
                        {
                            int ni = i + di;
                            int nj = j + dj;
                            if ((di == 0 && dj == 0) || ni < 0 || nj < 0 || ni >= _nx || nj >= _nz)
                            {
                                continue;
                            }
                            int n = nj * _nx + ni;
                            // Slope (not raw value): a diagonal neighbour is farther away.
                            float slope = (_values[n] - _values[idx]) / ((di != 0 && dj != 0) ? 1.4142f : 1f);
                            if (_values[n] > _values[idx] && (bestIdx < 0 || slope > best))
                            {
                                best = slope;
                                bestIdx = n;
                            }
                        }
                    }
                    result[idx] = bestIdx >= 0 ? result[bestIdx] : _values[idx];
                }
                return result;
            }

            /// <summary>Ridge point: local maximum of the distance field in at least one direction.</summary>
            private bool IsRidge(int i, int j)
            {
                float v = _values[j * _nx + i];
                float tolerance = CellSize * 0.01f;
                for (int d = 0; d < 4; d++)
                {
                    int di = d == 1 ? 0 : 1;
                    int dj = d == 0 ? 0 : d == 3 ? -1 : 1;
                    int i1 = i + di;
                    int j1 = j + dj;
                    int i2 = i - di;
                    int j2 = j - dj;
                    if (i1 < 0 || j1 < 0 || i2 < 0 || j2 < 0 || i1 >= _nx || i2 >= _nx || j1 >= _nz || j2 >= _nz)
                    {
                        continue;
                    }
                    if (v + tolerance >= _values[j1 * _nx + i1] && v + tolerance >= _values[j2 * _nx + i2])
                    {
                        return true;
                    }
                }
                return false;
            }

            private float2 Position(int i, int j) => _origin + new float2(i, j) * CellSize;

            /// <summary>Closed level curves "value = level" (marching squares, ambiguities resolved by the centre value).</summary>
            public List<List<float2>> ExtractContours(float[] values, float level)
            {
                float Value(int i, int j) => values[j * _nx + i];

                // Key of a contour point = the grid edge it crosses: (i, j, 0) horizontal edge
                // (i,j)-(i+1,j), (i, j, 1) vertical edge (i,j)-(i,j+1).
                var neighbors = new Dictionary<(int, int, int), List<(int, int, int)>>();
                var points = new Dictionary<(int, int, int), float2>();

                void Link((int, int, int) k1, (int, int, int) k2)
                {
                    AddNeighbor(neighbors, k1, k2);
                    AddNeighbor(neighbors, k2, k1);
                    EnsurePoint(points, values, k1, level);
                    EnsurePoint(points, values, k2, level);
                }

                for (int j = 0; j + 1 < _nz; j++)
                {
                    for (int i = 0; i + 1 < _nx; i++)
                    {
                        float va = Value(i, j);
                        float vb = Value(i + 1, j);
                        float vc = Value(i + 1, j + 1);
                        float vd = Value(i, j + 1);
                        int code = (va > level ? 1 : 0) | (vb > level ? 2 : 0) | (vc > level ? 4 : 0) | (vd > level ? 8 : 0);
                        if (code == 0 || code == 15)
                        {
                            continue;
                        }
                        var bottom = (i, j, 0);
                        var top = (i, j + 1, 0);
                        var left = (i, j, 1);
                        var right = (i + 1, j, 1);
                        bool centerInside = (va + vb + vc + vd) * 0.25f > level;
                        switch (code)
                        {
                            case 1: case 14: Link(left, bottom); break;
                            case 2: case 13: Link(bottom, right); break;
                            case 3: case 12: Link(left, right); break;
                            case 4: case 11: Link(right, top); break;
                            case 6: case 9: Link(bottom, top); break;
                            case 7: case 8: Link(left, top); break;
                            case 5:
                                if (centerInside) { Link(bottom, right); Link(top, left); }
                                else { Link(left, bottom); Link(right, top); }
                                break;
                            case 10:
                                if (centerInside) { Link(left, bottom); Link(right, top); }
                                else { Link(bottom, right); Link(top, left); }
                                break;
                        }
                    }
                }

                var loops = new List<List<float2>>();
                var visited = new HashSet<(int, int, int)>();
                foreach (var start in neighbors.Keys)
                {
                    if (visited.Contains(start))
                    {
                        continue;
                    }
                    var loop = new List<float2>();
                    var previous = start;
                    var current = start;
                    while (true)
                    {
                        visited.Add(current);
                        loop.Add(points[current]);
                        List<(int, int, int)> next = neighbors[current];
                        var candidate = next[0].Equals(previous) && next.Count > 1 ? next[1] : next[0];
                        if (candidate.Equals(start) || visited.Contains(candidate))
                        {
                            break;
                        }
                        previous = current;
                        current = candidate;
                    }
                    if (loop.Count >= 3)
                    {
                        loops.Add(loop);
                    }
                }
                return loops;
            }

            private static void AddNeighbor(Dictionary<(int, int, int), List<(int, int, int)>> neighbors, (int, int, int) key, (int, int, int) other)
            {
                if (!neighbors.TryGetValue(key, out List<(int, int, int)> list))
                {
                    list = new List<(int, int, int)>(2);
                    neighbors[key] = list;
                }
                list.Add(other);
            }

            private void EnsurePoint(Dictionary<(int, int, int), float2> points, float[] values, (int, int, int) key, float level)
            {
                if (points.ContainsKey(key))
                {
                    return;
                }
                (int i, int j, int dir) = key;
                int i2 = dir == 0 ? i + 1 : i;
                int j2 = dir == 0 ? j : j + 1;
                float v1 = values[j * _nx + i];
                float v2 = values[j2 * _nx + i2];
                float t = math.abs(v2 - v1) > 1e-6f ? math.saturate((level - v1) / (v2 - v1)) : 0.5f;
                points[key] = math.lerp(Position(i, j), Position(i2, j2), t);
            }
        }
    }

    /// <summary>Per-call diagnostics of ConcentricGenerator.Generate (tests and tooling only).</summary>
    public sealed class ConcentricDiagnostics
    {
        /// <summary>Loops of each ring, outermost first.</summary>
        public List<List<List<float2>>> Rings;
        /// <summary>Number of loops per ring, outermost first.</summary>
        public int[] RingLoopCounts;
    }

    /// <summary>Per-call diagnostics of ConcentricGenerator.GenerateRadial (tests and tooling only).</summary>
    public sealed class RadialDiagnostics
    {
        public float2 Centre;
        public float Radius;
        public float[] RingRadii;
    }
}
