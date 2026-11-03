// Placemaker roads layer — organic pattern.
// Adapted from Grid Road Generator (Ti4goc), Core/GridGenerator.Organic.cs; part of Placemaker (GPL-3.0).
// Changes for Placemaker: no static caches or global lock (thread-safe, reentrant), cooperative
// cancellation through GridParameters.Cancel, per-call diagnostics objects instead of statics.
using System;
using System.Collections.Generic;
using Unity.Mathematics;

namespace Placemaker.Core.Layers.Roads.Generators
{
    /// <summary>
    /// Organic pattern (grid family, LoopMode false): a suburban subdivision with winding streets
    /// (user feedback with an example image: "something like this, that also has cul-de-sacs").
    /// A main street (IsAvenue) meanders between two entrances on the perimeter; branches then
    /// grow wherever there is room, in gentle random curves (seed OrganicSeed), and end in a
    /// cul-de-sac (cul-de-sac network + turning circle) or sometimes (OrganicLoopShare) join the
    /// street they run into to form a loop. Streets stay about OrganicStreetSpacing apart, leaving
    /// lots on both sides. Guarantees (see the tests): no crossings, no sharp angles, no overly
    /// short segments, nothing too close to the perimeter except at entrances, everything
    /// connected to the entrances.
    /// </summary>
    public static partial class GridGenerator
    {
        public const float MinOrganicStreetSpacing = 60f;
        public const float MaxOrganicStreetSpacing = 140f;
        public const float OrganicStreetSpacingDefault = 80f;
        public const float OrganicCurvinessDefault = 60f;
        public const float OrganicLoopShareDefault = 30f;
        public const int MinOrganicSeed = 1;
        public const int MaxOrganicSeed = 100;

        /// <summary>Minimum distance (m) from a generated street to the perimeter, entrances excepted.</summary>
        private const float OrganicClearance = 25f;
        /// <summary>A branch stops (cul-de-sac) before getting this close to the perimeter: room for the turning circle.</summary>
        private const float OrganicTipClearance = 35f;
        /// <summary>Step (m) of the walk that traces the streets.</summary>
        private const float OrganicStep = 8f;
        /// <summary>Minimum distance (m) along a street between two junctions, or between a junction and an end.</summary>
        private const float OrganicMinJunctionGap = 45f;
        /// <summary>
        /// Minimum distance (m) between a cul-de-sac tip and any other street, measured on the
        /// polylines; includes margin for the game's actual curves deviating (up to ~8 m).
        /// </summary>
        private const float OrganicTipGap = 48f;
        /// <summary>Minimum length (m) of a cul-de-sac.</summary>
        private const float OrganicMinCulDeSac = 45f;
        /// <summary>Maximum turn (degrees) per step at 100% curviness.</summary>
        private const float OrganicMaxTurnDegrees = 9f;
        /// <summary>Minimum angle (degrees) at which a loop joins another street.</summary>
        private const float OrganicMinLoopAngle = 60f;
        /// <summary>Minimum angle (degrees) between the main street and the perimeter at its entrances.</summary>
        private const float OrganicMinEntryAngle = 55f;

        private static List<RoadSegmentDef> GenerateOrganic(List<float2> polygon, float y, GridParameters parameters)
            => new OrganicBuilder(polygon, y, parameters).Build();

        /// <summary>Perimeter edges bucketed in a grid (fast distance queries).</summary>
        private sealed class PerimeterGrid
        {
            private const float Cell = 25f;
            private readonly List<float2> _polygon;
            private readonly Dictionary<long, List<int>> _cells = new Dictionary<long, List<int>>();

            public PerimeterGrid(List<float2> polygon)
            {
                _polygon = polygon;
                for (int i = 0; i < polygon.Count; i++)
                {
                    float2 a = polygon[i], b = polygon[(i + 1) % polygon.Count];
                    int2 lo = CellOf(math.min(a, b)), hi = CellOf(math.max(a, b));
                    for (int x = lo.x; x <= hi.x; x++)
                    {
                        for (int z = lo.y; z <= hi.y; z++)
                        {
                            long key = Key(x, z);
                            if (!_cells.TryGetValue(key, out List<int> list))
                            {
                                _cells[key] = list = new List<int>();
                            }
                            list.Add(i);
                        }
                    }
                }
            }

            private static int2 CellOf(float2 p) => new int2((int)math.floor(p.x / Cell), (int)math.floor(p.y / Cell));
            private static long Key(int x, int z) => ((long)x << 32) ^ (uint)z;

            /// <summary>Distance (m) from p to the perimeter, capped at `max`.</summary>
            public float Distance(float2 p, float max)
            {
                int2 c = CellOf(p);
                int r = (int)math.ceil(max / Cell);
                float best = max * max;
                for (int x = c.x - r; x <= c.x + r; x++)
                {
                    for (int z = c.y - r; z <= c.y + r; z++)
                    {
                        if (!_cells.TryGetValue(Key(x, z), out List<int> list))
                        {
                            continue;
                        }
                        foreach (int i in list)
                        {
                            best = math.min(best, math.distancesq(ClosestOnSegment(_polygon[i], _polygon[(i + 1) % _polygon.Count], p), p));
                        }
                    }
                }
                return math.sqrt(best);
            }

            /// <summary>Unit direction of the perimeter edge nearest to p.</summary>
            public float2 EdgeDirection(float2 p)
            {
                float best = float.MaxValue;
                float2 direction = new float2(1f, 0f);
                int2 c = CellOf(p);
                for (int x = c.x - 1; x <= c.x + 1; x++)
                {
                    for (int z = c.y - 1; z <= c.y + 1; z++)
                    {
                        if (!_cells.TryGetValue(Key(x, z), out List<int> list))
                        {
                            continue;
                        }
                        foreach (int i in list)
                        {
                            float2 a = _polygon[i], b = _polygon[(i + 1) % _polygon.Count];
                            float d = math.distancesq(ClosestOnSegment(a, b, p), p);
                            if (d < best && math.distancesq(a, b) > 1e-6f)
                            {
                                best = d;
                                direction = math.normalize(b - a);
                            }
                        }
                    }
                }
                return direction;
            }
        }

        /// <summary>A traced street: polyline (one point every OrganicStep m) and junctions (point indices).</summary>
        private sealed class OrganicStreet
        {
            public readonly List<float2> Points = new List<float2>();
            public readonly SortedSet<int> Junctions = new SortedSet<int>();
            public bool Main;
            public bool DeadEnd;

            private float[] _cumulative;

            /// <summary>Cumulative length (m) at point i (recomputed only when the polyline has changed).</summary>
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

            public float2 Tangent(int i)
            {
                int a = math.max(0, i - 1), b = math.min(Points.Count - 1, i + 1);
                return math.normalizesafe(Points[b] - Points[a]);
            }

            /// <summary>True if point i is at least `gap` m (along the street) from every junction and from both ends.</summary>
            public bool FreeAt(int i, float gap)
            {
                float here = Along(i);
                if (here < gap || Along(Points.Count - 1) - here < gap)
                {
                    return false;
                }
                foreach (int j in Junctions)
                {
                    if (math.abs(Along(j) - here) < gap)
                    {
                        return false;
                    }
                }
                return true;
            }
        }

        /// <summary>Spatial index of all street points (street-to-street proximity).</summary>
        private sealed class StreetIndex
        {
            private const float Cell = 20f;
            private readonly Dictionary<long, List<(int street, int point)>> _cells = new Dictionary<long, List<(int, int)>>();
            private readonly List<OrganicStreet> _streets;

            public StreetIndex(List<OrganicStreet> streets) => _streets = streets;

            private static long Key(int x, int z) => ((long)x << 32) ^ (uint)z;
            private static int2 CellOf(float2 p) => new int2((int)math.floor(p.x / Cell), (int)math.floor(p.y / Cell));

            public void Add(int street, int point)
            {
                int2 c = CellOf(_streets[street].Points[point]);
                long key = Key(c.x, c.y);
                if (!_cells.TryGetValue(key, out var list))
                {
                    _cells[key] = list = new List<(int, int)>();
                }
                list.Add((street, point));
            }

            /// <summary>Nearest street point to p within `radius`, excluding those rejected by `skip`.</summary>
            public bool Nearest(float2 p, float radius, Func<int, int, bool> skip, out int street, out int point, out float distance)
            {
                street = -1;
                point = -1;
                distance = radius;
                int2 c = CellOf(p);
                int r = (int)math.ceil(radius / Cell);
                for (int x = c.x - r; x <= c.x + r; x++)
                {
                    for (int z = c.y - r; z <= c.y + r; z++)
                    {
                        if (!_cells.TryGetValue(Key(x, z), out var list))
                        {
                            continue;
                        }
                        foreach ((int s, int i) in list)
                        {
                            if (skip != null && skip(s, i))
                            {
                                continue;
                            }
                            float d = math.distance(_streets[s].Points[i], p);
                            if (d < distance)
                            {
                                distance = d;
                                street = s;
                                point = i;
                            }
                        }
                    }
                }
                return street >= 0;
            }
        }

        private sealed class OrganicBuilder
        {
            private readonly List<float2> _polygon;
            private readonly float _y;
            private readonly float _spacing;
            private readonly float _maxTurn;
            private readonly float _loopShare;
            private readonly System.Random _random;
            private readonly PerimeterGrid _perimeter;
            private readonly List<OrganicStreet> _streets = new List<OrganicStreet>();
            private readonly StreetIndex _index;

            public OrganicBuilder(List<float2> polygon, float y, GridParameters parameters)
            {
                _polygon = polygon;
                _y = y;
                _spacing = math.clamp(parameters.OrganicStreetSpacing > 0f ? parameters.OrganicStreetSpacing : OrganicStreetSpacingDefault,
                    MinOrganicStreetSpacing, MaxOrganicStreetSpacing);
                _maxTurn = math.radians(OrganicMaxTurnDegrees) * math.saturate(parameters.OrganicCurviness / 100f);
                _loopShare = math.saturate(parameters.OrganicLoopShare / 100f);
                _random = new System.Random(math.clamp(parameters.OrganicSeed, MinOrganicSeed, MaxOrganicSeed) * 7919 + 17);
                _perimeter = new PerimeterGrid(polygon);
                _index = new StreetIndex(_streets);
                _cancel = parameters.Cancel;
                _height = parameters.FollowContours ? parameters.HeightAt : null;
                _mainGap = parameters.MainJunctionSpacing;
                _maxGrade = parameters.MaxGrade > 0.005f ? parameters.MaxGrade : 0.1f;
            }

            private readonly System.Threading.CancellationToken _cancel;
            /// <summary>(s34) Ground height when the streets follow the contours (null = the original GRG walk).</summary>
            private readonly Func<float2, float> _height;
            private readonly float _maxGrade;
            /// <summary>(s34) Smallest distance between side junctions on the main street (0 = the GRG 45 m).</summary>
            private readonly float _mainGap;

            /// <summary>Grade (rise/run, signed) of one step from p along heading.</summary>
            private float StepGrade(float2 p, float2 heading, float length = 12f) =>
                (_height(p + heading * length) - _height(p)) / length;

            /// <summary>
            /// (s34) Steers a heading by the terrain: when the climb along it is steeper than ~80 % of the design grade, it turns
            /// toward the contour direction (the side closer to the heading) just enough to bring the climb back, at most
            /// <paramref name="maxTurnDeg"/> per step; on any slope a light pull toward the contours lays the streets into the hill.
            /// </summary>
            private float2 TerrainSteer(float2 p, float2 heading, float maxTurnDeg, float pull)
            {
                if (_height == null) return heading;
                const float e = 6f;
                float2 g = new float2(_height(p + new float2(e, 0f)) - _height(p - new float2(e, 0f)), _height(p + new float2(0f, e)) - _height(p - new float2(0f, e))) / (2f * e);
                float slope = math.length(g);
                if (slope < 0.02f) return heading;
                float2 c = new float2(-g.y, g.x) / slope;
                if (math.dot(c, heading) < 0f) c = -c;
                float target = 0.9f * _maxGrade;
                float sinNow = math.abs(math.dot(heading, g / slope));   // = |grade| / slope
                float2 want = heading;
                if (slope * sinNow > target)
                {
                    float sinWant = math.saturate(target / slope);
                    float2 up = g / slope * math.sign(math.dot(heading, g));
                    want = math.normalizesafe(c * math.sqrt(1f - sinWant * sinWant) + up * sinWant);
                }
                else if (pull > 0f)
                {
                    want = math.normalizesafe(math.lerp(heading, c, pull * math.saturate(slope / _maxGrade)));
                }
                float ang = math.acos(math.clamp(math.dot(heading, want), -1f, 1f));
                float lim = math.radians(maxTurnDeg);
                if (ang <= lim) return want;
                float s = math.sign(heading.x * want.y - heading.y * want.x);
                return Rotate(heading, s * lim);
            }

            private float Random01() => (float)_random.NextDouble();
            private float RandomSigned() => (float)(_random.NextDouble() * 2.0 - 1.0);

            private static float2 Rotate(float2 v, float angle)
            {
                float c = math.cos(angle), s = math.sin(angle);
                return new float2(v.x * c - v.y * s, v.x * s + v.y * c);
            }

            private int AddStreet(OrganicStreet street)
            {
                _streets.Add(street);
                int id = _streets.Count - 1;
                for (int i = 0; i < street.Points.Count; i++)
                {
                    _index.Add(id, i);
                }
                return id;
            }

            public List<RoadSegmentDef> Build()
            {
                if (!TryMainStreet())
                {
                    return new List<RoadSegmentDef>();
                }
                _cancel.ThrowIfCancellationRequested();
                GrowBranches();
                _cancel.ThrowIfCancellationRequested();
                return Emit();
            }

            // ---------------- Main street

            /// <summary>
            /// Main street: between two perimeter entrances at either end of the long axis, it starts
            /// perpendicular to the perimeter and meanders toward the other entrance while keeping
            /// clear of the perimeter. Several attempts (entrances shifted slightly) if one fails.
            /// </summary>
            private bool TryMainStreet()
            {
                float2 axis = LongestAxis(_polygon, 0f);
                // Perimeter points every ~10 m.
                var samples = new List<float2>();
                for (int i = 0; i < _polygon.Count; i++)
                {
                    float2 a = _polygon[i], b = _polygon[(i + 1) % _polygon.Count];
                    int n = math.max(1, (int)math.ceil(math.distance(a, b) / 10f));
                    for (int k = 0; k < n; k++) samples.Add(math.lerp(a, b, (float)k / n));
                }
                samples.Sort((p, q) => math.dot(p, axis).CompareTo(math.dot(q, axis)));
                for (int attempt = 0; attempt < 12; attempt++)
                {
                    // Entrances: among the 12% of points furthest out at each end of the long axis.
                    int band = math.max(1, samples.Count * 12 / 100);
                    float2 a = samples[_random.Next(band)];
                    float2 b = samples[samples.Count - 1 - _random.Next(band)];
                    if (TryWalkMain(a, b, out OrganicStreet street))
                    {
                        AddStreet(street);
                        return true;
                    }
                }
                return false;
            }

            /// <summary>Inward perimeter normal at point p (on the perimeter), or zero if undetermined.</summary>
            private float2 InwardNormal(float2 p)
            {
                float2 edge = _perimeter.EdgeDirection(p);
                float2 normal = new float2(-edge.y, edge.x);
                if (PointInPolygon(p + normal * 5f, _polygon)) return normal;
                if (PointInPolygon(p - normal * 5f, _polygon)) return -normal;
                return float2.zero;
            }

            private bool TryWalkMain(float2 a, float2 b, out OrganicStreet street)
            {
                street = new OrganicStreet { Main = true };
                float2 start = InwardNormal(a), finish = InwardNormal(b);
                if (math.all(start == float2.zero) || math.all(finish == float2.zero))
                {
                    return false;
                }
                float2 p = a, heading = start;
                street.Points.Add(a);
                float turn = 0f;
                float total = math.distance(a, b);
                float minEntrySin = math.sin(math.radians(OrganicMinEntryAngle));
                float entryZone = OrganicClearance / minEntrySin + OrganicStep;
                int maxSteps = (int)(3f * total / OrganicStep) + 20;
                for (int step = 0; step < maxSteps; step++)
                {
                    float toB = math.distance(p, b);
                    if (toB < 2.5f * _spacing && step > 3)
                    {
                        // Arrival: straight to entrance b, at a wide angle to the perimeter.
                        float2 last = math.normalize(b - p);
                        float2 edge = _perimeter.EdgeDirection(b);
                        float sin = math.abs(edge.x * last.y - edge.y * last.x);
                        if (sin >= minEntrySin && math.dot(last, -finish) > 0.5f)
                        {
                            var tail = new List<float2>();
                            int n = math.max(1, (int)math.ceil(toB / OrganicStep));
                            bool clear = true;
                            for (int k = 1; k <= n && clear; k++)
                            {
                                float2 q = k == n ? b : math.lerp(p, b, (float)k / n);
                                clear = math.distance(q, b) <= entryZone || _perimeter.Distance(q, OrganicClearance) >= OrganicClearance;
                                tail.Add(q);
                            }
                            if (clear)
                            {
                                street.Points.AddRange(tail);
                                return true;
                            }
                        }
                    }
                    // Wide gentle curves, strongly pulled toward b and pushed off the perimeter: the main
                    // street stays much straighter than the access streets (user feedback: "the avenue
                    // is too organic, it should be straighter").
                    turn = 0.92f * turn + 0.12f * _maxTurn * RandomSigned();
                    float2 goal = math.normalize(b - p);
                    float pull = math.saturate(0.35f + 0.5f * (1f - toB / total));
                    heading = math.normalize(math.lerp(Rotate(heading, turn), goal, pull * 0.35f));
                    heading = TerrainSteer(p, heading, 7f, 0f);
                    heading = AvoidPerimeter(p, heading);
                    float2 next = p + heading * OrganicStep;
                    bool nearA = math.distance(next, a) < entryZone;
                    if (!PointInPolygon(next, _polygon) || (!nearA && _perimeter.Distance(next, OrganicClearance + 5f) < OrganicClearance + 5f))
                    {
                        return false;
                    }
                    street.Points.Add(next);
                    p = next;
                }
                return false;
            }

            /// <summary>Turns `heading` toward the clearer side when the perimeter is close ahead.</summary>
            private float2 AvoidPerimeter(float2 p, float2 heading)
            {
                float ahead = _perimeter.Distance(p + heading * 30f, 70f);
                if (ahead >= 70f)
                {
                    return heading;
                }
                float left = _perimeter.Distance(p + Rotate(heading, 0.6f) * 30f, 70f);
                float right = _perimeter.Distance(p + Rotate(heading, -0.6f) * 30f, 70f);
                float strength = math.radians(14f) * (1f - ahead / 70f);
                return Rotate(heading, left > right ? strength : -strength);
            }

            // ---------------- Branches

            /// <summary>
            /// Grows branches while there is room left: from every free point of a street, on each
            /// side, if the neighbouring zone (one street spacing away) is empty.
            /// </summary>
            private void GrowBranches()
            {
                for (int pass = 0; pass < 200; pass++)
                {
                    _cancel.ThrowIfCancellationRequested();
                    var candidates = new List<(int street, int point, float side)>();
                    for (int s = 0; s < _streets.Count; s++)
                    {
                        OrganicStreet street = _streets[s];
                        for (int i = 1; i + 1 < street.Points.Count; i += 2)
                        {
                            foreach (float side in new[] { 1f, -1f })
                            {
                                if (SpaceFor(s, i, side))
                                {
                                    candidates.Add((s, i, side));
                                }
                            }
                        }
                        // (s34) A managed main street: a branch may also leave a junction on the other side (a crossroads).
                        if (street.Main && _mainGap > OrganicMinJunctionGap)
                            foreach (int j in street.Junctions)
                                foreach (float side in new[] { 1f, -1f })
                                    if (j > 0 && j + 1 < street.Points.Count && SpaceFor(s, j, side)) candidates.Add((s, j, side));
                    }
                    if (candidates.Count == 0)
                    {
                        return;
                    }
                    // Random (seeded) order; each candidate is revalidated because the state changes with every branch.
                    for (int i = candidates.Count - 1; i > 0; i--)
                    {
                        int j = _random.Next(i + 1);
                        (candidates[i], candidates[j]) = (candidates[j], candidates[i]);
                    }
                    bool grew = false;
                    foreach (var c in candidates)
                    {
                        if (SpaceFor(c.street, c.point, c.side) && TryGrow(c.street, c.point, c.side))
                        {
                            grew = true;
                        }
                    }
                    if (!grew)
                    {
                        return;
                    }
                }
            }

            /// <summary>Cul-de-sac tip (never skipped: its turning circle needs room).</summary>
            private bool IsTip(int street, int point) => _streets[street].DeadEnd && point == _streets[street].Points.Count - 1;

            private float2 Normal(int street, int point, float side)
            {
                float2 t = _streets[street].Tangent(point);
                return side * new float2(-t.y, t.x);
            }

            private bool SpaceFor(int street, int point, float side)
            {
                OrganicStreet s = _streets[street];
                // (s34) The main street keeps its junctions further apart (access management at the source); its
                // existing junctions stay open for a branch on the other side.
                bool managed = s.Main && _mainGap > OrganicMinJunctionGap;
                if (!(managed && s.Junctions.Contains(point)) && !s.FreeAt(point, managed ? _mainGap : OrganicMinJunctionGap))
                {
                    return false;
                }
                float2 probe = s.Points[point] + Normal(street, point, side) * _spacing;
                return PointInPolygon(probe, _polygon)
                    && _perimeter.Distance(probe, OrganicTipClearance + 10f) >= OrganicTipClearance + 10f
                    && !_index.Nearest(probe, 0.75f * _spacing, null, out _, out _, out _);
            }

            private bool TryGrow(int parent, int origin, float side)
            {
                OrganicStreet parentStreet = _streets[parent];
                float originAlong = parentStreet.Along(origin);
                var branch = new OrganicStreet();
                float2 p = parentStreet.Points[origin];
                branch.Points.Add(p);
                float2 heading = Rotate(Normal(parent, origin, side), math.radians(15f) * RandomSigned());
                if (_height != null)
                {
                    // (s34) On a slope a branch leaves its street diagonally (never under 50°) where the climb is gentlest.
                    float2 normal = Normal(parent, origin, side);
                    float best = float.MaxValue;
                    for (int k = -4; k <= 4; k++)
                    {
                        float2 h = Rotate(normal, math.radians(10f * k));
                        float g = math.abs(StepGrade(p, h, 24f)) + 0.002f * math.abs(k);
                        if (g < best) { best = g; heading = h; }
                    }
                }
                float turn = 0f;
                float length = 0f;
                float maxLength = _spacing * (2f + 6f * Random01());
                // At least the cul-de-sac tip distance: an approaching street stops short of it.
                float near = math.max(0.75f * _spacing, OrganicTipGap);
                int loopStreet = -1, loopPoint = -1;

                // Points to ignore in the neighbour search: the parent street near the origin, and any
                // point around the starting junction. Otherwise a neighbouring branch's start, on the
                // parent street ~45 m away, stopped the new branch at its first step (seen with 100 m
                // street spacing: only cul-de-sac stubs on one side).
                float2 originPoint = p;
                bool Skip(int s, int i) => !IsTip(s, i)
                    && ((s == parent && math.abs(parentStreet.Along(i) - originAlong) < OrganicMinJunctionGap + near)
                        || math.distance(_streets[s].Points[i], originPoint) < near + OrganicStep);

                while (true)
                {
                    turn = 0.8f * turn + 0.5f * _maxTurn * RandomSigned();
                    // First steps: start perpendicular (wide angle to the parent street).
                    if (length > 2f * OrganicStep)
                    {
                        heading = Rotate(heading, math.clamp(turn, -_maxTurn, _maxTurn));
                        heading = TerrainSteer(p, heading, length < 5f * OrganicStep ? 24f : 14f, length > _spacing ? 0.1f : 0f);
                        heading = AvoidPerimeter(p, heading);
                    }
                    float2 next = p + heading * OrganicStep;
                    // (s34) Too steep even after steering: the street ends here (a cul-de-sac on the hillside).
                    if (_height != null && length > 4f * OrganicStep && math.abs(StepGrade(p, heading)) > 3f * _maxGrade)
                    {
                        break;
                    }
                    if (!PointInPolygon(next, _polygon) || _perimeter.Distance(next, OrganicTipClearance) < OrganicTipClearance)
                    {
                        break;
                    }
                    // Another street ahead (or the branch itself, further back): cul-de-sac or loop.
                    int own = branch.Points.Count;
                    bool found = _index.Nearest(next, near, Skip, out int hitStreet, out int hitPoint, out _);
                    // The branch itself: only points already far back along the street (a straight
                    // street always has its recent points within `near`).
                    bool selfHit = false;
                    int recent = (int)math.ceil(1.6f * near / OrganicStep);
                    for (int k = 0; k < own - recent && !selfHit; k++)
                    {
                        selfHit = math.distance(branch.Points[k], next) < near;
                    }
                    if (found || selfHit)
                    {
                        if (found && !selfHit && length >= OrganicMinCulDeSac && Random01() < _loopShare
                            && CanLoop(p, hitStreet, hitPoint))
                        {
                            loopStreet = hitStreet;
                            loopPoint = hitPoint;
                        }
                        break;
                    }
                    branch.Points.Add(next);
                    p = next;
                    length += OrganicStep;
                    if (length >= maxLength)
                    {
                        break;
                    }
                }

                if (loopStreet >= 0)
                {
                    float2 q = _streets[loopStreet].Points[loopPoint];
                    int n = math.max(1, (int)math.ceil(math.distance(p, q) / OrganicStep));
                    for (int k = 1; k <= n; k++)
                    {
                        branch.Points.Add(k == n ? q : math.lerp(p, q, (float)k / n));
                    }
                    _streets[loopStreet].Junctions.Add(loopPoint);
                }
                else
                {
                    // Cul-de-sac tip: clear of ALL other streets (turning circle), including those
                    // skipped while growing (around the starting junction).
                    while (branch.Points.Count > 1
                        && _index.Nearest(branch.Points[branch.Points.Count - 1], OrganicTipGap, null, out _, out _, out _))
                    {
                        branch.Points.RemoveAt(branch.Points.Count - 1);
                        length -= OrganicStep;
                    }
                    if (length < OrganicMinCulDeSac)
                    {
                        return false;
                    }
                    branch.DeadEnd = true;
                }
                parentStreet.Junctions.Add(origin);
                AddStreet(branch);
                return true;
            }

            /// <summary>
            /// Whether a loop from p to point `point` of street `street` is possible: a free junction
            /// spot on that street, arrival at a wide angle, and nothing else in the way.
            /// </summary>
            private bool CanLoop(float2 p, int street, int point)
            {
                OrganicStreet target = _streets[street];
                if (!target.FreeAt(point, OrganicMinJunctionGap))
                {
                    return false;
                }
                float2 q = target.Points[point];
                float2 dir = math.normalizesafe(q - p);
                float2 tangent = target.Tangent(point);
                // (s34) A loop link straight up the hill is not a street.
                if (_height != null && math.abs(_height(q) - _height(p)) > 1.25f * _maxGrade * math.max(1f, math.distance(p, q))) return false;
                float sin = math.abs(dir.x * tangent.y - dir.y * tangent.x);
                if (sin < math.sin(math.radians(OrganicMinLoopAngle)))
                {
                    return false;
                }
                // The connector must not pass near any street other than the target (near the aimed point).
                float length = math.distance(p, q);
                for (float t = OrganicStep; t < length - OrganicStep; t += OrganicStep)
                {
                    float2 m = math.lerp(p, q, t / length);
                    if (_index.Nearest(m, 0.4f * _spacing, (s, i) => s == street && math.distance(target.Points[i], q) < length, out _, out _, out _))
                    {
                        return false;
                    }
                    if (_perimeter.Distance(m, OrganicClearance) < OrganicClearance)
                    {
                        return false;
                    }
                    if (_index.Nearest(m, OrganicTipGap, (s, i) => !IsTip(s, i), out _, out _, out _))
                    {
                        return false; // would pass near a cul-de-sac tip
                    }
                }
                return true;
            }

            // ---------------- Segments

            /// <summary>
            /// Each street is split at its junctions, then into 45-90 m curved segments (polyline
            /// tangents). Only two networks (user feedback: "I only want the avenue and the ones that
            /// go to the cul-de-sac"; a third network for access streets, tried for loops and then for
            /// collectors, looked messy in game):
            ///  - main street: avenue network;
            ///  - all other streets: cul-de-sac network (IsLocal), turning circle at cul-de-sac ends.
            /// </summary>
            private List<RoadSegmentDef> Emit()
            {
                var result = new List<RoadSegmentDef>();
                foreach (OrganicStreet street in _streets)
                {
                    var cuts = new SortedSet<int>(street.Junctions) { 0, street.Points.Count - 1 };
                    var cutList = new List<int>(cuts);
                    for (int c = 0; c + 1 < cutList.Count; c++)
                    {
                        int from = cutList[c], to = cutList[c + 1];
                        float pieceLength = street.Along(to) - street.Along(from);
                        int chunks = math.max(1, (int)math.floor(pieceLength / 45f));
                        // Intermediate cut points, as close to equal lengths as possible.
                        var stops = new List<int> { from };
                        for (int k = 1; k < chunks; k++)
                        {
                            float target = street.Along(from) + pieceLength * k / chunks;
                            int best = from;
                            for (int i = from + 1; i < to; i++)
                            {
                                if (math.abs(street.Along(i) - target) < math.abs(street.Along(best) - target)) best = i;
                            }
                            if (best > stops[stops.Count - 1]) stops.Add(best);
                        }
                        stops.Add(to);
                        for (int k = 0; k + 1 < stops.Count; k++)
                        {
                            int i0 = stops[k], i1 = stops[k + 1];
                            float2 a = street.Points[i0], b = street.Points[i1];
                            float2 t0 = street.Tangent(i0), t1 = street.Tangent(i1);
                            bool last = i1 == street.Points.Count - 1;
                            var def = RoadSegmentDef.Arc(new float3(a.x, _y, a.y), new float3(b.x, _y, b.y),
                                new float3(t0.x, 0f, t0.y), new float3(t1.x, 0f, t1.y), isCulDeSacEnd: street.DeadEnd && last);
                            def.IsAvenue = street.Main;
                            def.IsLocal = !street.Main;
                            result.Add(def);
                        }
                    }
                }
                return result;
            }
        }
    }
}
