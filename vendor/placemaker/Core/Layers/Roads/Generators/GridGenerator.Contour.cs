// Placemaker roads layer — contour (terrain-following) pattern.
// Adapted from Grid Road Generator (Ti4goc), Core/GridGenerator.Contour.cs; part of Placemaker (GPL-3.0).
// Changes for Placemaker: no static caches or global lock (thread-safe, reentrant), cooperative
// cancellation through GridParameters.Cancel, per-call diagnostics objects instead of statics.
using System;
using System.Collections.Generic;
using Unity.Mathematics;

namespace Placemaker.Core.Layers.Roads.Generators
{
    /// <summary>
    /// Contour pattern (grid family, LoopMode false): streets follow the terrain's contour lines
    /// (almost no grade), roughly ContourSpacing apart in plan, joined by straight uphill links
    /// (IsAvenue) with a capped grade (ContourMaxGrade). Street ends join the perimeter when they reach
    /// it at an open angle; otherwise they stop as a plain dead end (no turning circle, per user
    /// feedback; see EmitCurve). Flat terrain: nothing (no contour lines to follow).
    /// Guarantees (see the tests): no crossings, open angles, nothing too close to the perimeter
    /// except at connections, everything connected to the perimeter, uphill links under the max grade.
    /// </summary>
    public static partial class GridGenerator
    {
        public const float MinContourSpacing = 60f;
        public const float MaxContourSpacing = 150f;
        public const float ContourSpacingDefault = 90f;
        public const float MinContourConnectorSpacing = 150f;
        public const float MaxContourConnectorSpacing = 500f;
        public const float ContourConnectorSpacingDefault = 250f;

        private const float ContourClearance = 25f;
        private const float ContourTipClearance = 30f;
        private const float ContourTipGap = 48f;
        private const float ContourStep = 8f;
        private const float ContourMinPiece = 80f;
        /// <summary>Reach (x spacing) of a link between a cul-de-sac tip and the street ahead of it (see JoinDeadEnds).</summary>
        private const float ContourJoinReach = 1.6f;
        /// <summary>Minimum distance (x spacing) to another street along a close pass (see AcceptContour).</summary>
        private const float ContourLooseGap = 0.4f;
        /// <summary>Maximum length (x spacing) of a close pass that is kept without cutting the street.</summary>
        private const float ContourMaxBridge = 1.5f;
        private const float ContourJunctionGap = 45f;
        /// <summary>Maximum number of waves of parallel streets used to fill gaps (see FillGaps).</summary>
        private const int ContourFillPasses = 8;
        /// <summary>Minimum length (m) of a link between a level street and the perimeter.</summary>
        private const float ContourMinLink = 40f;
        /// <summary>Maximum grade (fraction) of an uphill link between two level streets.</summary>
        private const float ContourMaxGrade = 0.15f;
        private const float ContourMinJunctionAngle = 45f;
        /// <summary>Departure and arrival angle (degrees) of a curved uphill link on the level streets.</summary>
        private const float ContourCurveAngle = 55f;
        /// <summary>Minimum angle (degrees) between a curved uphill link's chord and the streets it joins.</summary>
        private const float ContourMinCurveChordAngle = 22f;
        private const float ContourMinPerimeterAngle = 50f;
        /// <summary>Below this relief (m) or this median slope the terrain is flat: nothing to follow.</summary>
        private const float ContourMinRelief = 4f;
        private const float ContourMinSlope = 0.015f;

        /// <summary>True if the Contour pattern has nothing to follow on this terrain (flat, or no terrain supplied).</summary>
        public static bool IsTerrainFlat(IReadOnlyList<float3> selectedNodePositions, Func<float2, float> heightAt)
        {
            if (heightAt == null || selectedNodePositions == null || selectedNodePositions.Count < 3)
            {
                return true;
            }
            var polygon = new List<float2>();
            foreach (float3 p in selectedNodePositions) polygon.Add(p.xz);
            float[] signature = TerrainSignature(polygon, heightAt);
            float lo = float.MaxValue, hi = float.MinValue;
            foreach (float h in signature)
            {
                lo = math.min(lo, h);
                hi = math.max(hi, h);
            }
            return hi - lo < ContourMinRelief;
        }

        /// <summary>Heights on a 7×7 grid over the bounding box: changes whenever the terrain changes.</summary>
        private static float[] TerrainSignature(List<float2> polygon, Func<float2, float> heightAt)
        {
            float2 min = new float2(float.MaxValue), max = new float2(float.MinValue);
            foreach (float2 p in polygon)
            {
                min = math.min(min, p);
                max = math.max(max, p);
            }
            var signature = new float[49];
            for (int i = 0; i < 7; i++)
            {
                for (int j = 0; j < 7; j++)
                {
                    signature[i * 7 + j] = math.round(heightAt(math.lerp(min, max, new float2(i / 6f, j / 6f))) * 10f) / 10f;
                }
            }
            return signature;
        }

        private static List<RoadSegmentDef> GenerateContour(List<float2> polygon, float y, GridParameters parameters)
        {
            if (parameters.HeightAt == null)
            {
                return new List<RoadSegmentDef>();
            }
            return new ContourBuilder(polygon, y, parameters).Build();
        }

        /// <summary>Intersection of segments a→b and c→d: t along a→b (strictly interior).</summary>
        private static bool SegmentIntersection(float2 a, float2 b, float2 c, float2 d, out float t)
        {
            t = 0f;
            float2 r = b - a, s = d - c;
            float den = r.x * s.y - r.y * s.x;
            if (math.abs(den) < 1e-9f)
            {
                return false;
            }
            float2 w = c - a;
            t = (w.x * s.y - w.y * s.x) / den;
            float u = (w.x * r.y - w.y * r.x) / den;
            return t > 1e-5f && t < 1f - 1e-5f && u >= 0f && u < 1f;
        }

        private sealed class ContourStreet
        {
            public readonly List<float2> Points = new List<float2>();
            public readonly SortedSet<int> Junctions = new SortedSet<int>();
            /// <summary>Height (m) of the level street: uphill links go towards a higher street.</summary>
            public float Level;
            public bool Closed;
            public bool Connector;
            /// <summary>Curved uphill link (steep slope, see AddConnectors): departure and arrival directions.</summary>
            public bool Curved;
            public float2 StartDirection, EndDirection;
            public bool StartOnPerimeter, EndOnPerimeter;
            public bool StartDead, EndDead;
            public bool Removed;
            public readonly List<int> Links = new List<int>();

            private float[] _cumulative;

            public float Along(int i)
            {
                if (_cumulative == null || _cumulative.Length != Points.Count)
                {
                    _cumulative = new float[Points.Count];
                    for (int k = 1; k < Points.Count; k++)
                    {
                        _cumulative[k] = _cumulative[k - 1] + math.distance(Points[k - 1], Points[k]);
                    }
                }
                return _cumulative[i];
            }

            public float Length => Points.Count > 0 ? Along(Points.Count - 1) : 0f;

            public float2 Tangent(int i)
            {
                int n = Points.Count;
                int a = Closed ? (i - 1 + n) % n : math.max(0, i - 1);
                int b = Closed ? (i + 1) % n : math.min(n - 1, i + 1);
                return math.normalizesafe(Points[b] - Points[a]);
            }

            /// <summary>Point i is at least `gap` m (along the street) from the ends and from every junction.</summary>
            public bool FreeAt(int i, float gap)
            {
                float here = Along(i), total = Length;
                if (!Closed && (here < gap || total - here < gap))
                {
                    return false;
                }
                foreach (int j in Junctions)
                {
                    float d = math.abs(Along(j) - here);
                    if (Closed) d = math.min(d, total - d);
                    if (d < gap) return false;
                }
                return true;
            }
        }

        private sealed class ContourBuilder
        {
            private readonly List<float2> _polygon;
            private readonly float _y;
            private readonly float _spacing;
            private readonly float _connectorSpacing;
            private readonly Func<float2, float> _height;
            private readonly PerimeterGrid _perimeter;
            private readonly List<ContourStreet> _streets = new List<ContourStreet>();
            private readonly Dictionary<long, List<(int street, int point)>> _index = new Dictionary<long, List<(int, int)>>();
            private const float IndexCell = 20f;

            // Height grid
            private float2 _origin;
            private float _cell;
            private int _nx, _nz;
            private float[] _h;
            private bool[] _inside;

            private readonly System.Threading.CancellationToken _cancel;

            public ContourBuilder(List<float2> polygon, float y, GridParameters parameters)
            {
                _cancel = parameters.Cancel;
                _polygon = polygon;
                _y = y;
                _spacing = math.clamp(parameters.ContourSpacing > 0f ? parameters.ContourSpacing : ContourSpacingDefault, MinContourSpacing, MaxContourSpacing);
                _connectorSpacing = math.clamp(parameters.ContourConnectorSpacing > 0f ? parameters.ContourConnectorSpacing : ContourConnectorSpacingDefault,
                    MinContourConnectorSpacing, MaxContourConnectorSpacing);
                _height = parameters.HeightAt;
                _perimeter = new PerimeterGrid(polygon);
            }

            // ---------------- Street point index

            private static long Key(int x, int z) => ((long)x << 32) ^ (uint)z;
            private static int2 CellOf(float2 p) => new int2((int)math.floor(p.x / IndexCell), (int)math.floor(p.y / IndexCell));

            private void IndexStreet(int s)
            {
                for (int i = 0; i < _streets[s].Points.Count; i++)
                {
                    int2 c = CellOf(_streets[s].Points[i]);
                    long key = Key(c.x, c.y);
                    if (!_index.TryGetValue(key, out var list)) _index[key] = list = new List<(int, int)>();
                    list.Add((s, i));
                }
            }

            private void RebuildIndex()
            {
                _index.Clear();
                for (int s = 0; s < _streets.Count; s++)
                {
                    if (!_streets[s].Removed) IndexStreet(s);
                }
            }

            private bool Near(float2 p, float radius, Func<int, int, bool> skip)
            {
                int2 c = CellOf(p);
                int r = (int)math.ceil(radius / IndexCell);
                for (int x = c.x - r; x <= c.x + r; x++)
                {
                    for (int z = c.y - r; z <= c.y + r; z++)
                    {
                        if (!_index.TryGetValue(Key(x, z), out var list)) continue;
                        foreach ((int s, int i) in list)
                        {
                            // Index is rebuilt after cuts (RebuildIndex): a point removed in the meantime is ignored.
                            if (_streets[s].Removed || i >= _streets[s].Points.Count || (skip != null && skip(s, i))) continue;
                            if (math.distancesq(_streets[s].Points[i], p) < radius * radius) return true;
                        }
                    }
                }
                return false;
            }

            // ---------------- Construction

            public List<RoadSegmentDef> Build()
            {
                if (!SampleTerrain(out float levelStep, out float minHeight, out float maxHeight))
                {
                    return new List<RoadSegmentDef>();
                }
                // Levels twice as tight as the target spacing on the median slope: where the slope is gentler
                // the contours spread apart and every other one still fits (AcceptContour keeps
                // 0.7 × spacing between streets), instead of leaving empty bands.
                float step = 0.5f * levelStep;
                for (float level = minHeight + 0.5f * step; level < maxHeight; level += step)
                {
                    _cancel.ThrowIfCancellationRequested();
                    foreach (List<float2> line in ContourLines(level, out List<bool> closed))
                    {
                        AcceptContour(line, closed[0], level);
                        closed.RemoveAt(0);
                    }
                }
                FillGaps();
                ResolveEnds();
                RebuildIndex();
                _cancel.ThrowIfCancellationRequested();
                JoinDeadEnds();
                AddConnectors();
                _cancel.ThrowIfCancellationRequested();
                AddPerimeterLinks();
                ConnectIslands();
                _cancel.ThrowIfCancellationRequested();
                if (!KeepConnected())
                {
                    return new List<RoadSegmentDef>();
                }
                return Emit();
            }

            /// <summary>Smoothed height grid over the bounding box, interior cells, step between levels.</summary>
            private bool SampleTerrain(out float levelStep, out float minHeight, out float maxHeight)
            {
                levelStep = minHeight = maxHeight = 0f;
                float2 min = new float2(float.MaxValue), max = new float2(float.MinValue);
                foreach (float2 p in _polygon)
                {
                    min = math.min(min, p);
                    max = math.max(max, p);
                }
                float2 size = max - min;
                _cell = math.clamp(math.sqrt(size.x * size.y) / 160f, 5f, 12f);
                _origin = min - _cell;
                _nx = (int)math.ceil((size.x + 2f * _cell) / _cell) + 1;
                _nz = (int)math.ceil((size.y + 2f * _cell) / _cell) + 1;
                _h = new float[_nx * _nz];
                for (int j = 0; j < _nz; j++)
                {
                    for (int i = 0; i < _nx; i++)
                    {
                        _h[j * _nx + i] = _height(Node(i, j));
                    }
                }
                // Smoothing (two 3×3 passes): contour lines without jaggies from fine terrain detail.
                for (int pass = 0; pass < 2; pass++)
                {
                    var smoothed = new float[_h.Length];
                    for (int j = 0; j < _nz; j++)
                    {
                        for (int i = 0; i < _nx; i++)
                        {
                            float sum = 0f;
                            int count = 0;
                            for (int dj = -1; dj <= 1; dj++)
                            {
                                for (int di = -1; di <= 1; di++)
                                {
                                    int x = i + di, z = j + dj;
                                    if (x < 0 || z < 0 || x >= _nx || z >= _nz) continue;
                                    sum += _h[z * _nx + x];
                                    count++;
                                }
                            }
                            smoothed[j * _nx + i] = sum / count;
                        }
                    }
                    _h = smoothed;
                }
                // Interior nodes (row-by-row scan).
                _inside = new bool[_h.Length];
                for (int j = 0; j < _nz; j++)
                {
                    float z = _origin.y + j * _cell;
                    var crossings = new List<float>();
                    for (int k = 0; k < _polygon.Count; k++)
                    {
                        float2 a = _polygon[k], b = _polygon[(k + 1) % _polygon.Count];
                        if ((a.y <= z && z < b.y) || (b.y <= z && z < a.y))
                        {
                            crossings.Add(a.x + (z - a.y) / (b.y - a.y) * (b.x - a.x));
                        }
                    }
                    crossings.Sort();
                    for (int k = 0; k + 1 < crossings.Count; k += 2)
                    {
                        int from = (int)math.ceil((crossings[k] - _origin.x) / _cell);
                        int to = (int)math.floor((crossings[k + 1] - _origin.x) / _cell);
                        for (int i = math.max(0, from); i <= math.min(_nx - 1, to); i++) _inside[j * _nx + i] = true;
                    }
                }
                var slopes = new List<float>();
                minHeight = float.MaxValue;
                maxHeight = float.MinValue;
                for (int j = 1; j + 1 < _nz; j++)
                {
                    for (int i = 1; i + 1 < _nx; i++)
                    {
                        int k = j * _nx + i;
                        if (!_inside[k]) continue;
                        minHeight = math.min(minHeight, _h[k]);
                        maxHeight = math.max(maxHeight, _h[k]);
                        float gx = (_h[k + 1] - _h[k - 1]) / (2f * _cell);
                        float gz = (_h[k + _nx] - _h[k - _nx]) / (2f * _cell);
                        slopes.Add(math.sqrt(gx * gx + gz * gz));
                    }
                }
                if (slopes.Count == 0 || maxHeight - minHeight < ContourMinRelief)
                {
                    return false;
                }
                slopes.Sort();
                float medianSlope = slopes[slopes.Count / 2];
                if (medianSlope < ContourMinSlope)
                {
                    return false;
                }
                // Levels spaced so two neighbouring streets are ~_spacing apart in plan on a median slope.
                levelStep = _spacing * medianSlope;
                return true;
            }

            private float2 Node(int i, int j) => _origin + new float2(i, j) * _cell;

            /// <summary>Contour lines at `level` (marching squares) as polylines; closed[k] is true for a loop.</summary>
            private List<List<float2>> ContourLines(float level, out List<bool> closed)
            {
                // One crossing point per grid edge: key (i, j, horizontal?).
                var position = new Dictionary<long, float2>();
                var links = new Dictionary<long, List<long>>();
                long EdgeKey(int i, int j, bool horizontal) => ((long)(j * _nx + i) << 1) | (horizontal ? 1L : 0L);
                float V(int i, int j) => _h[j * _nx + i];
                long Crossing(int i, int j, bool horizontal)
                {
                    long key = EdgeKey(i, j, horizontal);
                    if (!position.ContainsKey(key))
                    {
                        float a = V(i, j), b = horizontal ? V(i + 1, j) : V(i, j + 1);
                        float t = math.saturate((level - a) / (b - a));
                        position[key] = Node(i, j) + (horizontal ? new float2(t * _cell, 0f) : new float2(0f, t * _cell));
                    }
                    return key;
                }
                void Link(long a, long b)
                {
                    if (!links.TryGetValue(a, out var la)) links[a] = la = new List<long>();
                    if (!links.TryGetValue(b, out var lb)) links[b] = lb = new List<long>();
                    la.Add(b);
                    lb.Add(a);
                }
                for (int j = 0; j + 1 < _nz; j++)
                {
                    for (int i = 0; i + 1 < _nx; i++)
                    {
                        float v0 = V(i, j), v1 = V(i + 1, j), v2 = V(i + 1, j + 1), v3 = V(i, j + 1);
                        int code = (v0 > level ? 1 : 0) | (v1 > level ? 2 : 0) | (v2 > level ? 4 : 0) | (v3 > level ? 8 : 0);
                        if (code == 0 || code == 15) continue;
                        long bottom = 0, right = 0, top = 0, left = 0;
                        bool hasBottom = (code & 1) != ((code >> 1) & 1), hasRight = ((code >> 1) & 1) != ((code >> 2) & 1);
                        bool hasTop = ((code >> 2) & 1) != ((code >> 3) & 1), hasLeft = ((code >> 3) & 1) != (code & 1);
                        if (hasBottom) bottom = Crossing(i, j, true);
                        if (hasRight) right = Crossing(i + 1, j, false);
                        if (hasTop) top = Crossing(i, j + 1, true);
                        if (hasLeft) left = Crossing(i, j, false);
                        var edges = new List<long>();
                        if (hasBottom) edges.Add(bottom);
                        if (hasRight) edges.Add(right);
                        if (hasTop) edges.Add(top);
                        if (hasLeft) edges.Add(left);
                        if (edges.Count == 2)
                        {
                            Link(edges[0], edges[1]);
                        }
                        else if (edges.Count == 4)
                        {
                            // Saddle: disambiguate with the value at the cell centre.
                            bool centreHigh = 0.25f * (v0 + v1 + v2 + v3) > level;
                            bool corner0High = v0 > level;
                            if (centreHigh == corner0High)
                            {
                                Link(bottom, right);
                                Link(top, left);
                            }
                            else
                            {
                                Link(bottom, left);
                                Link(top, right);
                            }
                        }
                    }
                }
                // Chain into polylines: first from the ends (degree 1), then the loops.
                var lines = new List<List<float2>>();
                var loops = new List<bool>();
                var used = new HashSet<long>();
                void Walk(long start, bool loop)
                {
                    var line = new List<float2>();
                    long previous = -1, current = start;
                    while (true)
                    {
                        used.Add(current);
                        line.Add(position[current]);
                        long next = -1;
                        foreach (long n in links[current])
                        {
                            if (n != previous && !used.Contains(n))
                            {
                                next = n;
                                break;
                            }
                        }
                        if (next < 0) break;
                        previous = current;
                        current = next;
                    }
                    if (line.Count >= 3)
                    {
                        lines.Add(line);
                        loops.Add(loop);
                    }
                }
                foreach (var pair in links)
                {
                    if (pair.Value.Count == 1 && !used.Contains(pair.Key)) Walk(pair.Key, false);
                }
                foreach (var pair in links)
                {
                    if (!used.Contains(pair.Key)) Walk(pair.Key, true);
                }
                closed = loops;
                return lines;
            }

            /// <summary>Resamples a polyline every ContourStep m, then smooths it (moving average, ends fixed).</summary>
            private static List<float2> Resample(List<float2> line, bool loop)
            {
                var source = new List<float2>(line);
                if (loop) source.Add(line[0]);
                var result = new List<float2> { source[0] };
                float carry = 0f;
                for (int k = 1; k < source.Count; k++)
                {
                    float2 a = source[k - 1], b = source[k];
                    float length = math.distance(a, b);
                    float t = ContourStep - carry;
                    while (t <= length)
                    {
                        result.Add(math.lerp(a, b, t / length));
                        t += ContourStep;
                    }
                    carry = length - (t - ContourStep);
                }
                if (!loop && math.distance(result[result.Count - 1], source[source.Count - 1]) > 1f)
                {
                    result.Add(source[source.Count - 1]);
                }
                if (loop && result.Count > 1 && math.distance(result[0], result[result.Count - 1]) < 0.5f * ContourStep)
                {
                    result.RemoveAt(result.Count - 1);
                }
                for (int pass = 0; pass < 3; pass++)
                {
                    var smoothed = new List<float2>(result);
                    int n = result.Count;
                    for (int k = 0; k < n; k++)
                    {
                        if (!loop && (k < 2 || k > n - 3)) continue;
                        float2 sum = float2.zero;
                        for (int d = -2; d <= 2; d++) sum += result[((k + d) % n + n) % n];
                        smoothed[k] = sum / 5f;
                    }
                    result = smoothed;
                }
                return result;
            }

            /// <summary>
            /// Keeps the parts of a contour line that lie inside the shape, away from the perimeter and from
            /// already accepted level streets (otherwise contours bunch up where the slope is steep).
            /// </summary>
            private void AcceptContour(List<float2> line, bool loop, float level)
            {
                List<float2> points = Resample(line, loop);
                if (points.Count < 3) return;
                // Two thresholds (user feedback, with screenshot: "the roads lose continuity"): a street only
                // starts and ends far from the others (0.7 x spacing), but along a street already started a
                // closer pass (down to 0.4 x) is kept if it is short (ContourMaxBridge x spacing), so the
                // contour is no longer chopped into isolated segments wherever it brushes its neighbour.
                bool Inside(float2 p) => PointInPolygon(p, _polygon) && _perimeter.Distance(p, ContourClearance) >= ContourClearance;
                var strict = new bool[points.Count];
                var loose = new bool[points.Count];
                bool allStrict = true;
                for (int k = 0; k < points.Count; k++)
                {
                    bool inside = Inside(points[k]);
                    loose[k] = inside && !Near(points[k], ContourLooseGap * _spacing, null);
                    strict[k] = loose[k] && !Near(points[k], 0.7f * _spacing, null);
                    allStrict &= strict[k];
                }
                if (loop && allStrict)
                {
                    var ring = new ContourStreet { Level = level, Closed = true };
                    ring.Points.AddRange(points);
                    if (ring.Length >= 3f * ContourMinPiece)
                    {
                        _streets.Add(ring);
                        IndexStreet(_streets.Count - 1);
                    }
                    return;
                }
                // Closed loop: start at a rejected point so a kept portion is not split in two.
                int start = 0;
                if (loop)
                {
                    start = Array.IndexOf(loose, false);
                    if (start < 0) start = Array.IndexOf(strict, false);
                }
                int maxBridge = (int)math.ceil(ContourMaxBridge * _spacing / ContourStep);
                var run = new List<float2>();
                var runStrict = new List<bool>();
                void Flush()
                {
                    // Ends too close to other streets are trimmed; close passes that are too long: cut.
                    int a = 0, b = run.Count - 1;
                    while (a <= b && !runStrict[a]) a++;
                    while (b >= a && !runStrict[b]) b--;
                    var piece = new List<float2>();
                    int soft = 0;
                    for (int k = a; k <= b; k++)
                    {
                        if (runStrict[k])
                        {
                            soft = 0;
                        }
                        else if (++soft > maxBridge)
                        {
                            // Pass too long: end the piece before it, resume at the next point that meets the strict gap.
                            piece.RemoveRange(piece.Count - (soft - 1), soft - 1);
                            Keep(piece);
                            piece = new List<float2>();
                            while (k <= b && !runStrict[k]) k++;
                            soft = 0;
                            if (k > b) break;
                        }
                        piece.Add(run[k]);
                    }
                    Keep(piece);
                    run = new List<float2>();
                    runStrict = new List<bool>();
                }
                void Keep(List<float2> piece)
                {
                    if (piece.Count < 2) return;
                    float length = 0f;
                    for (int k = 1; k < piece.Count; k++) length += math.distance(piece[k - 1], piece[k]);
                    if (length < ContourMinPiece) return;
                    var street = new ContourStreet { Level = level };
                    street.Points.AddRange(piece);
                    _streets.Add(street);
                    IndexStreet(_streets.Count - 1);
                }
                int total = points.Count + (loop ? 1 : 0);
                for (int step = 0; step < total; step++)
                {
                    int k = (start + step) % points.Count;
                    if (loose[k])
                    {
                        run.Add(points[k]);
                        runStrict.Add(strict[k]);
                    }
                    else Flush();
                }
                Flush();
            }

            /// <summary>
            /// Remaining gaps (near-flat plateau, a shelf between two slopes: no contour line to follow):
            /// parallel streets to the neighbouring ones, at the desired spacing, spread outward step by
            /// step (AcceptContour keeps them only away from existing streets and the perimeter).
            /// </summary>
            private void FillGaps()
            {
                int from = 0;
                for (int pass = 0; pass < ContourFillPasses; pass++)
                {
                    int count = _streets.Count;
                    for (int s = from; s < count; s++)
                    {
                        ContourStreet street = _streets[s];
                        if (street.Removed || street.Connector || street.Points.Count < 3) continue;
                        foreach (float side in new[] { 1f, -1f })
                        {
                            var line = new List<float2>(street.Points.Count);
                            float height = 0f;
                            for (int i = 0; i < street.Points.Count; i++)
                            {
                                float2 t = street.Tangent(i);
                                float2 q = street.Points[i] + side * _spacing * new float2(-t.y, t.x);
                                line.Add(q);
                                height += _height(q);
                            }
                            AcceptContour(line, street.Closed, height / line.Count);
                        }
                    }
                    if (_streets.Count == count) break;
                    from = count;
                }
            }

            /// <summary>
            /// Street ends: extended straight to the perimeter if they reach it quickly and at an open angle;
            /// otherwise a cul-de-sac, shortened until it clears the space a turning circle would need.
            /// </summary>
            private void ResolveEnds()
            {
                for (int s = 0; s < _streets.Count; s++)
                {
                    ContourStreet street = _streets[s];
                    if (street.Closed) continue;
                    foreach (bool atEnd in new[] { false, true })
                    {
                        if (TryReachPerimeter(s, atEnd))
                        {
                            if (atEnd) street.EndOnPerimeter = true; else street.StartOnPerimeter = true;
                            continue;
                        }
                        // Cul-de-sac: the tip must be far from the perimeter and from other streets.
                        while (street.Points.Count > 2)
                        {
                            float2 tip = atEnd ? street.Points[street.Points.Count - 1] : street.Points[0];
                            int self = s;
                            bool clear = _perimeter.Distance(tip, ContourTipClearance) >= ContourTipClearance
                                && !Near(tip, ContourTipGap, (other, i) => other == self);
                            if (clear) break;
                            street.Points.RemoveAt(atEnd ? street.Points.Count - 1 : 0);
                        }
                        if (atEnd) street.EndDead = true; else street.StartDead = true;
                    }
                    if (street.Length < ContourMinPiece)
                    {
                        street.Removed = true;
                    }
                }
            }

            private bool TryReachPerimeter(int s, bool atEnd)
            {
                ContourStreet street = _streets[s];
                int n = street.Points.Count;
                if (n < 2) return false;
                // Street may have been shortened to two points from its other end (seen in game log: index out of range).
                int back = math.min(2, n - 1);
                float2 tip = atEnd ? street.Points[n - 1] : street.Points[0];
                float2 dir = atEnd ? math.normalizesafe(street.Points[n - 1] - street.Points[n - 1 - back])
                    : math.normalizesafe(street.Points[0] - street.Points[back]);
                float best = float.MaxValue;
                int edge = -1;
                for (int k = 0; k < _polygon.Count; k++)
                {
                    float2 a = _polygon[k], b = _polygon[(k + 1) % _polygon.Count];
                    if (SegmentIntersection(tip, tip + dir * 70f, a, b, out float t) && t * 70f < best)
                    {
                        best = t * 70f;
                        edge = k;
                    }
                }
                if (edge < 0) return false;
                float2 edgeDir = math.normalize(_polygon[(edge + 1) % _polygon.Count] - _polygon[edge]);
                if (math.abs(edgeDir.x * dir.y - edgeDir.y * dir.x) < math.sin(math.radians(ContourMinPerimeterAngle))) return false;
                float2 hit = tip + dir * best;
                int stepsCount = (int)math.ceil(best / ContourStep);
                for (int k = 1; k < stepsCount; k++)
                {
                    float2 q = math.lerp(tip, hit, (float)k / stepsCount);
                    int self = s;
                    if (Near(q, 0.5f * _spacing, (other, i) => other == self)) return false;
                }
                var extension = new List<float2>();
                for (int k = 1; k <= stepsCount; k++) extension.Add(k == stepsCount ? hit : math.lerp(tip, hit, (float)k / stepsCount));
                if (atEnd)
                {
                    street.Points.AddRange(extension);
                }
                else
                {
                    extension.Reverse();
                    street.Points.InsertRange(0, extension);
                }
                return true;
            }

            /// <summary>
            /// Uphill links: along each level street, every _connectorSpacing m, a straight street to the
            /// next higher level street, under the max grade and with open angles at both ends.
            /// </summary>
            private void AddConnectors()
            {
                float minSin = math.sin(math.radians(ContourMinJunctionAngle));
                float minCurveSin = math.sin(math.radians(ContourMinCurveChordAngle));
                int count = _streets.Count;
                for (int a = 0; a < count; a++)
                {
                    ContourStreet lower = _streets[a];
                    if (lower.Removed || lower.Connector) continue;
                    float length = lower.Length;
                    for (float target = 0.5f * _connectorSpacing; target < length; target += _connectorSpacing)
                    {
                        int i = 0;
                        while (i + 1 < lower.Points.Count && lower.Along(i) < target) i++;
                        if (!lower.FreeAt(i, ContourJunctionGap)) continue;
                        float2 p = lower.Points[i];
                        float hp = _height(p);
                        float2 tangentP = lower.Tangent(i);
                        int bestStreet = -1, bestPoint = -1;
                        float bestDistance = float.MaxValue;
                        bool bestCurved = false;
                        float2 bestStart = default, bestEnd = default;
                        for (int b = 0; b < count; b++)
                        {
                            ContourStreet upper = _streets[b];
                            // Nearest higher level street (the level just above may have been dropped where the slope
                            // bunches the contours); passing over another street is ruled out by ConnectorClear.
                            if (upper.Removed || upper.Connector || upper.Level <= lower.Level) continue;
                            for (int j = 0; j < upper.Points.Count; j++)
                            {
                                float2 q = upper.Points[j];
                                float d = math.distance(p, q);
                                if (d >= bestDistance || d > 3f * _spacing || d < 0.5f * _spacing) continue;
                                float2 dir = (q - p) / d;
                                if (math.abs(_height(q) - hp) / d > ContourMaxGrade) continue;
                                float2 tq = upper.Tangent(j);
                                float sinP = math.abs(dir.x * tangentP.y - dir.y * tangentP.x), sinQ = math.abs(dir.x * tq.y - dir.y * tq.x);
                                bool curved = sinP < minSin || sinQ < minSin;
                                // Steep slope (user feedback: no uphill link above ~15% grade): the chord would have
                                // to run at a slant to stay ≤ 15%, too shallow for a junction, so use an S-shaped
                                // link that leaves and joins the streets at ContourCurveAngle.
                                if (curved && (sinP < minCurveSin || sinQ < minCurveSin)) continue;
                                float score = curved ? d * 1.25f : d;
                                if (score >= bestDistance) continue;
                                if (!upper.FreeAt(j, ContourJunctionGap)) continue;
                                float2 startDir = default, endDir = default;
                                if (curved)
                                {
                                    startDir = CurveDirection(tangentP, dir);
                                    endDir = CurveDirection(tq, dir);
                                    if (!CurveClear(p, startDir, endDir, q, a, b)) continue;
                                }
                                else if (!ConnectorClear(p, q, a, b)) continue;
                                bestDistance = score;
                                bestStreet = b;
                                bestPoint = j;
                                bestCurved = curved;
                                bestStart = startDir;
                                bestEnd = endDir;
                            }
                        }
                        if (bestStreet < 0) continue;
                        var connector = new ContourStreet { Connector = true, Level = lower.Level, Curved = bestCurved, StartDirection = bestStart, EndDirection = bestEnd };
                        float2 end = _streets[bestStreet].Points[bestPoint];
                        int steps = (int)math.ceil(math.distance(p, end) / ContourStep);
                        for (int k = 0; k <= steps; k++)
                        {
                            float t = (float)k / steps;
                            connector.Points.Add(k == 0 ? p : k == steps ? end
                                : bestCurved ? CurvePoint(p, bestStart, bestEnd, end, t) : math.lerp(p, end, t));
                        }
                        _streets.Add(connector);
                        int id = _streets.Count - 1;
                        IndexStreet(id);
                        lower.Junctions.Add(i);
                        _streets[bestStreet].Junctions.Add(bestPoint);
                        connector.Links.Add(a);
                        connector.Links.Add(bestStreet);
                        lower.Links.Add(id);
                        _streets[bestStreet].Links.Add(id);
                    }
                }
            }

            /// <summary>
            /// Links to the perimeter: from each level street, every _connectorSpacing m, a straight
            /// street to the perimeter when nothing lies in between (outermost street at that spot),
            /// under the max grade (at a slant if the slope requires it). Without them, a hill lying
            /// entirely inside the area yields only closed loops, never connected to the perimeter.
            /// </summary>
            private void AddPerimeterLinks()
            {
                float maxLength = 2.5f * _spacing;
                float minPerimeterSin = math.sin(math.radians(ContourMinPerimeterAngle));
                var hits = new List<float2>();
                int count = _streets.Count;
                for (int a = 0; a < count; a++)
                {
                    ContourStreet street = _streets[a];
                    if (street.Removed || street.Connector) continue;
                    float length = street.Length;
                    for (float target = 0.25f * _connectorSpacing; target < length; target += _connectorSpacing)
                    {
                        int i = 0;
                        while (i + 1 < street.Points.Count && street.Along(i) < target) i++;
                        if (!street.FreeAt(i, ContourJunctionGap)) continue;
                        float2 p = street.Points[i];
                        float hp = _height(p);
                        float2 tangent = street.Tangent(i);
                        float best = float.MaxValue;
                        float2 bestHit = default;
                        foreach (float degrees in new[] { 90f, 75f, 105f, 60f, 120f, 50f, 130f })
                        {
                            foreach (float side in new[] { 1f, -1f })
                            {
                                float angle = math.radians(degrees) * side;
                                float2 dir = new float2(tangent.x * math.cos(angle) - tangent.y * math.sin(angle),
                                    tangent.x * math.sin(angle) + tangent.y * math.cos(angle));
                                float hitDistance = float.MaxValue;
                                int edge = -1;
                                for (int k = 0; k < _polygon.Count; k++)
                                {
                                    float2 e0 = _polygon[k], e1 = _polygon[(k + 1) % _polygon.Count];
                                    if (SegmentIntersection(p, p + dir * maxLength, e0, e1, out float t) && t * maxLength < hitDistance)
                                    {
                                        hitDistance = t * maxLength;
                                        edge = k;
                                    }
                                }
                                if (edge < 0 || hitDistance >= best || hitDistance < ContourMinLink) continue;
                                float2 edgeDir = math.normalizesafe(_polygon[(edge + 1) % _polygon.Count] - _polygon[edge]);
                                if (math.abs(edgeDir.x * dir.y - edgeDir.y * dir.x) < minPerimeterSin) continue;
                                float2 hit = p + dir * hitDistance;
                                if (math.abs(_height(hit) - hp) / hitDistance > ContourMaxGrade) continue;
                                bool spaced = true;
                                foreach (float2 h in hits) spaced &= math.distance(h, hit) >= ContourJunctionGap * 1.5f;
                                if (!spaced || !PerimeterLinkClear(p, hit, a)) continue;
                                best = hitDistance;
                                bestHit = hit;
                            }
                        }
                        if (best == float.MaxValue) continue;
                        var link = new ContourStreet { Connector = true, Level = street.Level, EndOnPerimeter = true };
                        int steps = (int)math.ceil(best / ContourStep);
                        for (int k = 0; k <= steps; k++) link.Points.Add(k == 0 ? p : k == steps ? bestHit : math.lerp(p, bestHit, (float)k / steps));
                        _streets.Add(link);
                        int id = _streets.Count - 1;
                        IndexStreet(id);
                        street.Junctions.Add(i);
                        link.Links.Add(a);
                        street.Links.Add(id);
                        hits.Add(bestHit);
                    }
                }
            }

            /// <summary>Straight link p→hit (hit on the perimeter): inside the shape, without brushing another street.</summary>
            private bool PerimeterLinkClear(float2 p, float2 hit, int streetP)
            {
                float length = math.distance(p, hit);
                for (float t = ContourStep; t < length - ContourStep * 0.5f; t += ContourStep)
                {
                    float2 m = math.lerp(p, hit, t / length);
                    if (!PointInPolygon(m, _polygon)) return false;
                    float fromP = t, fromHit = length - t;
                    // Runs along the perimeter only near the arrival point (where the link meets it at an open angle).
                    if (fromHit > ContourClearance + ContourStep && _perimeter.Distance(m, ContourClearance) < ContourClearance) return false;
                    if (Near(m, math.min(ContourClearance, fromP), (s, i) => s == streetP && math.distance(_streets[s].Points[i], p) < fromP + 1f))
                    {
                        return false;
                    }
                }
                return ClearOfTips(p, hit);
            }

            /// <summary>
            /// Cul-de-sac tips keep their distance (ContourTipGap) from every street, links included.
            /// Straight link a→b, or polyline `path` for a curved uphill link.
            /// </summary>
            private bool ClearOfTips(float2 a, float2 b, List<float2> path = null)
            {
                foreach (ContourStreet other in _streets)
                {
                    if (other.Removed || other.Connector || other.Points.Count == 0) continue;
                    foreach ((bool dead, float2 tip) in new[] { (other.StartDead, other.Points[0]), (other.EndDead, other.Points[other.Points.Count - 1]) })
                    {
                        if (!dead || math.distancesq(tip, a) < 1e-4f || math.distancesq(tip, b) < 1e-4f) continue;
                        float d = float.MaxValue;
                        if (path == null)
                        {
                            d = DistanceToSegment(tip, a, b);
                        }
                        else
                        {
                            for (int k = 1; k < path.Count; k++) d = math.min(d, DistanceToSegment(tip, path[k - 1], path[k]));
                        }
                        if (d < ContourTipGap) return false;
                    }
                }
                return true;
            }

            private static float DistanceToSegment(float2 p, float2 a, float2 b)
            {
                float2 ab = b - a;
                float t = math.saturate(math.dot(p - a, ab) / math.max(math.lengthsq(ab), 1e-6f));
                return math.distance(p, a + t * ab);
            }

            /// <summary>
            /// Departure (or arrival) direction of a curved uphill link: at ContourCurveAngle to the street
            /// (tangent), on the chord's side and in its direction.
            /// </summary>
            private static float2 CurveDirection(float2 tangent, float2 chord)
            {
                float2 along = math.dot(tangent, chord) >= 0f ? tangent : -tangent;
                float2 normal = new float2(-along.y, along.x);
                if (math.dot(normal, chord) < 0f) normal = -normal;
                float angle = math.radians(ContourCurveAngle);
                return math.normalize(along * math.cos(angle) + normal * math.sin(angle));
            }

            /// <summary>Point t of the curved uphill link (cubic, arms at a third of the chord, close to the game's curve).</summary>
            private static float2 CurvePoint(float2 a, float2 startDir, float2 endDir, float2 b, float t)
            {
                float arm = math.distance(a, b) / 3f;
                float2 c1 = a + startDir * arm, c2 = b - endDir * arm;
                float u = 1f - t;
                return u * u * u * a + 3f * u * u * t * c1 + 3f * u * t * t * c2 + t * t * t * b;
            }

            /// <summary>Curved uphill link is clear: same rule as ConnectorClear, along the curve.</summary>
            private bool CurveClear(float2 p, float2 startDir, float2 endDir, float2 q, int streetP, int streetQ)
            {
                int steps = (int)math.ceil(math.distance(p, q) / ContourStep);
                float2 previous = p;
                float fromP = 0f;
                float total = 0f;
                var points = new List<float2>(steps + 1) { p };
                for (int k = 1; k <= steps; k++)
                {
                    float2 m = CurvePoint(p, startDir, endDir, q, (float)k / steps);
                    total += math.distance(previous, m);
                    previous = m;
                    points.Add(m);
                }
                for (int k = 1; k < steps; k++)
                {
                    float2 m = points[k];
                    fromP += math.distance(points[k - 1], m);
                    float fromQ = total - fromP;
                    if (!PointInPolygon(m, _polygon) || _perimeter.Distance(m, ContourClearance) < ContourClearance) return false;
                    float along = fromP, back = fromQ;
                    if (Near(m, math.min(ContourClearance, math.min(along, back)), (s, i) =>
                            (s == streetP && math.distance(_streets[s].Points[i], p) < along + 1f)
                            || (s == streetQ && math.distance(_streets[s].Points[i], q) < back + 1f)))
                    {
                        return false;
                    }
                }
                return ClearOfTips(p, q, points);
            }

            /// <summary>Straight uphill link p→q: inside the shape, away from the perimeter and other streets (excluding its two ends).</summary>
            private bool ConnectorClear(float2 p, float2 q, int streetP, int streetQ)
            {
                float length = math.distance(p, q);
                for (float t = ContourStep; t < length - ContourStep * 0.5f; t += ContourStep)
                {
                    float2 m = math.lerp(p, q, t / length);
                    if (!PointInPolygon(m, _polygon) || _perimeter.Distance(m, ContourClearance) < ContourClearance) return false;
                    float fromP = t, fromQ = length - t;
                    if (Near(m, math.min(ContourClearance, math.min(fromP, fromQ)), (s, i) =>
                            (s == streetP && math.distance(_streets[s].Points[i], p) < fromP + 1f)
                            || (s == streetQ && math.distance(_streets[s].Points[i], q) < fromQ + 1f)))
                    {
                        return false;
                    }
                }
                return ClearOfTips(p, q);
            }

            /// <summary>Streets connected to the perimeter (directly or through uphill links/links).</summary>
            private HashSet<int> Reached()
            {
                var reached = new HashSet<int>();
                var stack = new List<int>();
                for (int s = 0; s < _streets.Count; s++)
                {
                    if (!_streets[s].Removed && (_streets[s].StartOnPerimeter || _streets[s].EndOnPerimeter)) stack.Add(s);
                }
                while (stack.Count > 0)
                {
                    int s = stack[stack.Count - 1];
                    stack.RemoveAt(stack.Count - 1);
                    if (!reached.Add(s)) continue;
                    foreach (int t in _streets[s].Links)
                    {
                        if (!_streets[t].Removed) stack.Add(t);
                    }
                }
                return reached;
            }

            /// <summary>
            /// Islands: level streets that no uphill link (always upward) nor perimeter link has
            /// connected; they would be removed (KeepConnected), leaving large gaps (replayed on real
            /// terrain: 52 streets out of 112). Each island gets the shortest link to an already
            /// connected street, upward OR downward, same rules (grade ≤ 15%, open angles, clearance);
            /// repeat while it still connects something.
            /// </summary>
            private void ConnectIslands()
            {
                // Candidates (island → connected street) are kept up to date between rounds instead of all
                // being recomputed (user feedback: Contour at 60 m on a large painted area took several seconds
                // here): initially every nearby pair, then only those towards newly connected streets. Each
                // round takes the shortest one still valid and free, as before.
                var candidates = new List<(float d, int a, int i, int b, int j)>();
                HashSet<int> reached = Reached();
                AddIslandCandidates(candidates, reached, reached);
                for (int round = 0; round < 64; round++)
                {
                    candidates.Sort((x, y) => x.d.CompareTo(y.d));
                    int bestA = -1, bestB = -1, bestI = -1, bestJ = -1;
                    float bestDistance = 0f;
                    // A rejected candidate stays rejected: later rounds only add links (more obstacles, more
                    // junctions), never remove any, so it is dropped.
                    int k0 = 0;
                    for (; k0 < candidates.Count; k0++)
                    {
                        (float d, int a, int i, int b, int j) = candidates[k0];
                        if (reached.Contains(a) || !IslandCandidate(a, i, b, j, out _)) continue;
                        if (!ConnectorClear(_streets[a].Points[i], _streets[b].Points[j], a, b)) continue;
                        bestDistance = d;
                        bestA = a; bestB = b; bestI = i; bestJ = j;
                        break;
                    }
                    candidates.RemoveRange(0, math.min(k0 + 1, candidates.Count));
                    if (bestA < 0) return;
                    float2 from = _streets[bestA].Points[bestI], to = _streets[bestB].Points[bestJ];
                    var connector = new ContourStreet { Connector = true, Level = _streets[bestA].Level };
                    int steps = (int)math.ceil(bestDistance / ContourStep);
                    for (int k = 0; k <= steps; k++) connector.Points.Add(k == 0 ? from : k == steps ? to : math.lerp(from, to, (float)k / steps));
                    _streets.Add(connector);
                    int id = _streets.Count - 1;
                    IndexStreet(id);
                    _streets[bestA].Junctions.Add(bestI);
                    ContourStreet target = _streets[bestB];
                    if (bestJ == 0 && target.StartDead) target.StartDead = false;
                    else if (bestJ == target.Points.Count - 1 && target.EndDead) target.EndDead = false;
                    else target.Junctions.Add(bestJ);
                    connector.Links.Add(bestA);
                    connector.Links.Add(bestB);
                    _streets[bestA].Links.Add(id);
                    _streets[bestB].Links.Add(id);

                    HashSet<int> now = Reached();
                    var added = new HashSet<int>();
                    foreach (int r in now)
                    {
                        if (!reached.Contains(r)) added.Add(r);
                    }
                    reached = now;
                    candidates.RemoveAll(c => reached.Contains(c.a));
                    AddIslandCandidates(candidates, reached, added);
                }
            }

            // Coarse index (cells the size of a link's reach) of street points, for ConnectIslands.
            private Dictionary<long, List<(int street, int point)>> _coarse;
            private float _coarseCell;

            /// <summary>
            /// Nearby (island point, point on a `targets` street) pairs: start from the target streets'
            /// points (few after the first round) and look for island points in the 3 x 3 neighbouring
            /// cells of the coarse index.
            /// </summary>
            private void AddIslandCandidates(List<(float d, int a, int i, int b, int j)> candidates, HashSet<int> reached, HashSet<int> targets)
            {
                float reach = 4f * _spacing, minD = 0.4f * _spacing;
                if (_coarse == null)
                {
                    _coarseCell = reach;
                    _coarse = new Dictionary<long, List<(int, int)>>();
                    for (int s = 0; s < _streets.Count; s++)
                    {
                        ContourStreet street = _streets[s];
                        if (street.Removed || street.Connector) continue;
                        for (int k = 0; k < street.Points.Count; k++)
                        {
                            float2 q = street.Points[k];
                            long key = Key((int)math.floor(q.x / _coarseCell), (int)math.floor(q.y / _coarseCell));
                            if (!_coarse.TryGetValue(key, out var list)) _coarse[key] = list = new List<(int, int)>();
                            list.Add((s, k));
                        }
                    }
                }
                foreach (int b in targets)
                {
                    ContourStreet other = _streets[b];
                    if (other.Removed || other.Connector) continue;
                    for (int j = 0; j < other.Points.Count; j++)
                    {
                        float2 q = other.Points[j];
                        int cx0 = (int)math.floor(q.x / _coarseCell), cz0 = (int)math.floor(q.y / _coarseCell);
                        for (int cx = cx0 - 1; cx <= cx0 + 1; cx++)
                        for (int cz = cz0 - 1; cz <= cz0 + 1; cz++)
                        {
                            if (!_coarse.TryGetValue(Key(cx, cz), out var cellPoints)) continue;
                            foreach ((int a, int i) in cellPoints)
                            {
                                if (i % 3 != 0 || reached.Contains(a)) continue;
                                float d2 = math.distancesq(_streets[a].Points[i], q);
                                if (d2 > reach * reach || d2 < minD * minD) continue;
                                if (IslandCandidate(a, i, b, j, out float d)) candidates.Add((d, a, i, b, j));
                            }
                        }
                    }
                }
            }

            /// <summary>Possible link (clearance test excluded) from point i of island a to point j of street b.</summary>
            private bool IslandCandidate(int a, int i, int b, int j, out float d)
            {
                d = 0f;
                ContourStreet island = _streets[a], other = _streets[b];
                if (other.Connector || other.Removed || j >= other.Points.Count || i >= island.Points.Count) return false;
                if (!island.FreeAt(i, ContourJunctionGap)) return false;
                float minSin = math.sin(math.radians(ContourMinJunctionAngle));
                float2 p = island.Points[i], q = other.Points[j];
                d = math.distance(p, q);
                if (d > 4f * _spacing || d < 0.4f * _spacing) return false;
                float2 dir = (q - p) / d;
                if (math.abs(_height(q) - _height(p)) / d > ContourMaxGrade) return false;
                float2 tangentP = island.Tangent(i);
                if (math.abs(dir.x * tangentP.y - dir.y * tangentP.x) < minSin) return false;
                int last = other.Points.Count - 1;
                // Cul-de-sac tip: the link extends it (the cul-de-sac disappears); elsewhere, a T-junction.
                bool tip = (j == 0 && other.StartDead) || (j == last && other.EndDead);
                float2 tq = other.Tangent(j);
                if (tip)
                {
                    // The street continues into the link: no hairpin fold-back (> 40° opening).
                    float2 outward = j == 0 ? -tq : tq;
                    return math.dot(outward, -dir) >= -0.77f;
                }
                return math.abs(dir.x * tq.y - dir.y * tq.x) >= minSin && other.FreeAt(j, ContourJunctionGap);
            }

            /// <summary>
            /// Cul-de-sac tips joined to the nearest street ahead of them (user feedback, with screenshot:
            /// "the roads lose continuity"): to another cul-de-sac tip (the street continues) or to the
            /// middle of a street (T-junction), within ContourJoinReach x spacing, in the street's
            /// direction (±50°), grade ≤ 15%, open angles and a clear path.
            /// </summary>
            private void JoinDeadEnds()
            {
                float reach = ContourJoinReach * _spacing;
                float minSin = math.sin(math.radians(ContourMinJunctionAngle));
                float aheadCos = math.cos(math.radians(50f));
                int count = _streets.Count;
                for (int s = 0; s < count; s++)
                {
                    foreach (bool atEnd in new[] { false, true })
                    {
                        ContourStreet street = _streets[s];
                        if (street.Removed || street.Connector || street.Closed || street.Points.Count < 3) break;
                        if (!(atEnd ? street.EndDead : street.StartDead)) continue;
                        int n = street.Points.Count;
                        int tipIndex = atEnd ? n - 1 : 0;
                        float2 p = street.Points[tipIndex];
                        float2 outward = atEnd ? math.normalizesafe(p - street.Points[n - 3]) : math.normalizesafe(p - street.Points[2]);
                        float hp = _height(p);
                        float bestScore = float.MaxValue, bestD = 0f;
                        int bestB = -1, bestJ = -1;
                        bool bestTip = false;
                        int2 cell = CellOf(p);
                        int r = (int)math.ceil(reach / IndexCell);
                        for (int cx = cell.x - r; cx <= cell.x + r; cx++)
                        for (int cz = cell.y - r; cz <= cell.y + r; cz++)
                        {
                            if (!_index.TryGetValue(Key(cx, cz), out var cellPoints)) continue;
                            foreach ((int b, int j) in cellPoints)
                            {
                                if (b == s) continue;
                                ContourStreet other = _streets[b];
                                if (other.Removed || other.Connector || j >= other.Points.Count) continue;
                                float2 q = other.Points[j];
                                float d = math.distance(p, q);
                                if (d > reach || d < 15f) continue;
                                float2 dir = (q - p) / d;
                                if (math.dot(dir, outward) < aheadCos) continue;
                                if (math.abs(_height(q) - hp) / d > ContourMaxGrade) continue;
                                int last = other.Points.Count - 1;
                                bool tip = !other.Closed && ((j == 0 && other.StartDead) || (j == last && other.EndDead));
                                if (tip)
                                {
                                    // The two tips face each other: the street continues.
                                    float2 otherOut = j == 0 ? math.normalizesafe(q - other.Points[math.min(2, last)])
                                        : math.normalizesafe(q - other.Points[math.max(0, last - 2)]);
                                    if (math.dot(otherOut, -dir) < aheadCos) continue;
                                }
                                else
                                {
                                    float2 tq = other.Tangent(j);
                                    if (math.abs(dir.x * tq.y - dir.y * tq.x) < minSin) continue;
                                    if (!other.FreeAt(j, ContourJunctionGap)) continue;
                                }
                                // Continuity first: a facing cul-de-sac tip counts as closer.
                                float score = tip ? 0.7f * d : d;
                                if (score >= bestScore) continue;
                                if (!ConnectorClear(p, q, s, b)) continue;
                                bestScore = score;
                                bestD = d;
                                bestB = b; bestJ = j; bestTip = tip;
                            }
                        }
                        if (bestB < 0) continue;
                        float2 to = _streets[bestB].Points[bestJ];
                        var connector = new ContourStreet { Connector = true, Level = street.Level };
                        int steps = math.max(1, (int)math.ceil(bestD / ContourStep));
                        for (int k = 0; k <= steps; k++) connector.Points.Add(k == 0 ? p : k == steps ? to : math.lerp(p, to, (float)k / steps));
                        _streets.Add(connector);
                        int id = _streets.Count - 1;
                        IndexStreet(id);
                        if (atEnd) street.EndDead = false; else street.StartDead = false;
                        ContourStreet target = _streets[bestB];
                        if (bestTip)
                        {
                            if (bestJ == 0) target.StartDead = false; else target.EndDead = false;
                        }
                        else
                        {
                            target.Junctions.Add(bestJ);
                        }
                        connector.Links.Add(s);
                        connector.Links.Add(bestB);
                        street.Links.Add(id);
                        target.Links.Add(id);
                    }
                }
            }

            /// <summary>Keeps only what is connected to the perimeter (otherwise unreachable). False if nothing is.</summary>
            private bool KeepConnected()
            {
                // List used as a stack: Stack<T> was ambiguous in the original mod project (System vs mscorlib).
                var reached = new HashSet<int>();
                var stack = new List<int>();
                for (int s = 0; s < _streets.Count; s++)
                {
                    if (!_streets[s].Removed && (_streets[s].StartOnPerimeter || _streets[s].EndOnPerimeter)) stack.Add(s);
                }
                while (stack.Count > 0)
                {
                    int s = stack[stack.Count - 1];
                    stack.RemoveAt(stack.Count - 1);
                    if (!reached.Add(s)) continue;
                    foreach (int t in _streets[s].Links)
                    {
                        if (!_streets[t].Removed) stack.Add(t);
                    }
                }
                for (int s = 0; s < _streets.Count; s++)
                {
                    if (!reached.Contains(s)) _streets[s].Removed = true;
                }
                return reached.Count > 0;
            }

            // ---------------- Segments

            private List<RoadSegmentDef> Emit()
            {
                var result = new List<RoadSegmentDef>();
                foreach (ContourStreet street in _streets)
                {
                    if (street.Removed) continue;
                    if (street.Connector)
                    {
                        EmitStraight(result, street);
                        continue;
                    }
                    var cuts = new List<int>(street.Junctions);
                    if (street.Closed)
                    {
                        if (cuts.Count == 0) cuts.Add(0);
                        if (cuts.Count == 1) cuts.Add((cuts[0] + street.Points.Count / 2) % street.Points.Count);
                        cuts.Sort();
                        for (int c = 0; c < cuts.Count; c++)
                        {
                            int from = cuts[c], to = cuts[(c + 1) % cuts.Count];
                            var indices = new List<int>();
                            for (int k = from; ; k = (k + 1) % street.Points.Count)
                            {
                                indices.Add(k);
                                if (k == to && indices.Count > 1) break;
                            }
                            EmitCurve(result, street, indices, false, false);
                        }
                        continue;
                    }
                    cuts.Add(0);
                    cuts.Add(street.Points.Count - 1);
                    cuts.Sort();
                    for (int c = 0; c + 1 < cuts.Count; c++)
                    {
                        if (cuts[c] == cuts[c + 1]) continue;
                        var indices = new List<int>();
                        for (int k = cuts[c]; k <= cuts[c + 1]; k++) indices.Add(k);
                        EmitCurve(result, street, indices, c == 0 && street.StartDead, c + 2 == cuts.Count && street.EndDead);
                    }
                }
                return result;
            }

            /// <summary>Part of a level street between two junctions, as curved segments of 45 to 90 m; cul-de-sac tips at the end of a segment.</summary>
            private void EmitCurve(List<RoadSegmentDef> result, ContourStreet street, List<int> indices, bool deadStart, bool deadEnd)
            {
                var along = new float[indices.Count];
                for (int k = 1; k < indices.Count; k++) along[k] = along[k - 1] + math.distance(street.Points[indices[k - 1]], street.Points[indices[k]]);
                float length = along[indices.Count - 1];
                int chunks = math.max(1, (int)math.floor(length / 45f));
                var stops = new List<int> { 0 };
                for (int c = 1; c < chunks; c++)
                {
                    float target = length * c / chunks;
                    int best = 0;
                    for (int k = 1; k + 1 < indices.Count; k++)
                    {
                        if (math.abs(along[k] - target) < math.abs(along[best] - target)) best = k;
                    }
                    if (best > stops[stops.Count - 1]) stops.Add(best);
                }
                stops.Add(indices.Count - 1);
                for (int c = 0; c + 1 < stops.Count; c++)
                {
                    int i0 = indices[stops[c]], i1 = indices[stops[c + 1]];
                    float2 a = street.Points[i0], b = street.Points[i1];
                    float2 t0 = street.Tangent(i0), t1 = street.Tangent(i1);
                    bool first = c == 0, last = c + 2 == stops.Count;
                    if (first && deadStart)
                    {
                        // The dead end goes at the segment's End (where the turning circle used to sit): reverse the segment.
                        (a, b) = (b, a);
                        (t0, t1) = (-t1, -t0);
                    }
                    // No turning circle (user feedback: "the cul-de-sacs aren't needed here, just roads that
                    // follow the contour lines"): a level street end that does not reach the perimeter
                    // simply stops.
                    result.Add(RoadSegmentDef.Arc(new float3(a.x, _y, a.y), new float3(b.x, _y, b.y),
                        new float3(t0.x, 0f, t0.y), new float3(t1.x, 0f, t1.y)));
                }
            }

            private void EmitStraight(List<RoadSegmentDef> result, ContourStreet street)
            {
                if (street.Curved)
                {
                    // S-shaped uphill link: a single curved segment (the chord stays under 3 × spacing).
                    float2 s0 = street.Points[0], s1 = street.Points[street.Points.Count - 1];
                    var arc = RoadSegmentDef.Arc(new float3(s0.x, _y, s0.y), new float3(s1.x, _y, s1.y),
                        new float3(street.StartDirection.x, 0f, street.StartDirection.y), new float3(street.EndDirection.x, 0f, street.EndDirection.y));
                    arc.IsAvenue = true;
                    result.Add(arc);
                    return;
                }
                float2 a = street.Points[0], b = street.Points[street.Points.Count - 1];
                int pieces = math.max(1, (int)math.ceil(math.distance(a, b) / 90f));
                for (int k = 0; k < pieces; k++)
                {
                    float2 p = math.lerp(a, b, (float)k / pieces), q = k + 1 == pieces ? b : math.lerp(a, b, (float)(k + 1) / pieces);
                    if (k == 0) p = a;
                    result.Add(new RoadSegmentDef(new float3(p.x, _y, p.y), new float3(q.x, _y, q.y), isHorizontal: false, isAvenue: true));
                }
            }
        }
    }
}
