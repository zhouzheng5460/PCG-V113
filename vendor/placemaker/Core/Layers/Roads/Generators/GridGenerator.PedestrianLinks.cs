// Placemaker roads layer — pedestrian links between cul-de-sacs.
// Adapted from Grid Road Generator (Ti4goc), Core/GridGenerator.PedestrianLinks.cs; part of Placemaker (GPL-3.0).
// Changes for Placemaker: no static caches or global lock (thread-safe, reentrant), cooperative
// cancellation through GridParameters.Cancel, per-call diagnostics objects instead of statics.
using System.Collections.Generic;
using Unity.Mathematics;

namespace Placemaker.Core.Layers.Roads.Generators
{
    /// <summary>
    /// Pedestrian links between cul-de-sacs (user feedback: "pedestrian streets and cycle paths as a
    /// third network"): as in real subdivisions, the tip of each cul-de-sac is joined by a short path
    /// to the nearest street node ahead of it, so pedestrians and cyclists no longer have to walk
    /// around the whole block.
    /// </summary>
    public static partial class GridGenerator
    {
        /// <summary>Maximum length (m) of a pedestrian link.</summary>
        public const float PedestrianLinkMaxLength = 90f;
        private const float PedestrianLinkMinLength = 15f;
        /// <summary>Maximum deviation (degrees) between the cul-de-sac direction and the link.</summary>
        private const float PedestrianLinkMaxTurn = 70f;
        /// <summary>Minimum angle (degrees) between the link and the streets at the target node.</summary>
        private const float PedestrianLinkMinJunctionAngle = 30f;

        /// <summary>
        /// Adds to `segments` a link (RoadSegmentDef.IsPedestrian) from each cul-de-sac tip
        /// (IsCulDeSacEnd, tip = End) to an existing node of another street, or to the perimeter,
        /// without crossing any street or leaving the shape. At most one link per target node.
        /// </summary>
        internal static void AddPedestrianLinks(List<RoadSegmentDef> segments, List<float2> polygon, float y)
        {
            // Nodes and their outgoing tangents (for the angle check at the target node).
            var nodes = new Dictionary<float3, List<float2>>();
            void AddDirection(float3 node, float2 direction)
            {
                if (!nodes.TryGetValue(node, out var list)) nodes[node] = list = new List<float2>();
                list.Add(math.normalizesafe(direction));
            }
            var polylines = new List<(float2[] points, float2 min, float2 max)>(segments.Count);
            foreach (RoadSegmentDef s in segments)
            {
                float2[] points = Sample(s);
                float2 min = points[0], max = points[0];
                foreach (float2 q in points)
                {
                    min = math.min(min, q);
                    max = math.max(max, q);
                }
                polylines.Add((points, min, max));
                AddDirection(s.Start, points[1] - points[0]);
                AddDirection(s.End, points[points.Length - 2] - points[points.Length - 1]);
            }

            var used = new HashSet<float3>();
            var links = new List<RoadSegmentDef>();
            float minSin = math.sin(math.radians(PedestrianLinkMinJunctionAngle));
            float maxTurnCos = math.cos(math.radians(PedestrianLinkMaxTurn));
            foreach (RoadSegmentDef culDeSac in segments)
            {
                if (!culDeSac.IsCulDeSacEnd) continue;
                float3 tip = culDeSac.End;
                float2[] own = Sample(culDeSac);
                float2 forward = math.normalizesafe(own[own.Length - 1] - own[own.Length - 2]);

                float bestDistance = PedestrianLinkMaxLength;
                float3 best = default;
                bool found = false;
                // Targets in range, nearest first: nodes of other streets, plus the perimeter point
                // straight ahead. The first one that qualifies is the best.
                var targets = new List<(float3 point, bool onPerimeter, float distance)>();
                foreach (float3 node in nodes.Keys)
                {
                    float d = math.distance(node.xz, tip.xz);
                    if (!node.Equals(tip) && d < PedestrianLinkMaxLength && d >= PedestrianLinkMinLength) targets.Add((node, false, d));
                }
                float2 perimeterPoint = ClosestPerimeterPoint(polygon, tip.xz + forward * 0.01f, forward);
                targets.Add((new float3(perimeterPoint.x, y, perimeterPoint.y), true, math.distance(perimeterPoint, tip.xz)));
                targets.Sort((a, b) => a.distance.CompareTo(b.distance));

                foreach ((float3 target, bool onPerimeter, float d) in targets)
                {
                    if (d >= bestDistance || d < PedestrianLinkMinLength || used.Contains(target)) continue;
                    float2 delta = target.xz - tip.xz;
                    float2 direction = delta / d;
                    if (math.dot(direction, forward) < maxTurnCos) continue;
                    if (!onPerimeter && nodes.TryGetValue(target, out var outgoing))
                    {
                        bool open = true;
                        foreach (float2 o in outgoing)
                        {
                            open &= math.abs(o.x * direction.y - o.y * direction.x) >= minSin || math.dot(o, -direction) < 0f;
                        }
                        if (!open) continue;
                    }
                    if (!PathClear(tip.xz, target.xz, polylines, polygon)) continue;
                    bestDistance = d;
                    best = target;
                    found = true;
                    break;
                }
                if (!found) continue;
                used.Add(best);
                var link = new RoadSegmentDef(tip, best, isHorizontal: false);
                link.IsPedestrian = true;
                links.Add(link);
            }
            segments.AddRange(links);
        }

        /// <summary>Sample points along a segment's curve (a cubic approximating the game's curve).</summary>
        private static float2[] Sample(RoadSegmentDef s, int count = 8)
        {
            var points = new float2[count + 1];
            float2 a = s.Start.xz, b = s.End.xz;
            if (!s.IsArc)
            {
                for (int k = 0; k <= count; k++) points[k] = math.lerp(a, b, (float)k / count);
                return points;
            }
            float arm = math.distance(a, b) / 3f;
            float2 c1 = a + math.normalizesafe(s.StartTangent.xz) * arm, c2 = b - math.normalizesafe(s.EndTangent.xz) * arm;
            for (int k = 0; k <= count; k++)
            {
                float t = (float)k / count, u = 1f - t;
                points[k] = u * u * u * a + 3f * u * u * t * c1 + 3f * u * t * t * c2 + t * t * t * b;
            }
            return points;
        }

        /// <summary>First perimeter point hit when moving from `from` along `direction` (otherwise `from` itself).</summary>
        private static float2 ClosestPerimeterPoint(List<float2> polygon, float2 from, float2 direction)
        {
            float best = float.MaxValue;
            float2 hit = from;
            for (int i = 0; i < polygon.Count; i++)
            {
                float2 a = polygon[i], b = polygon[(i + 1) % polygon.Count];
                if (SegmentIntersection(from, from + direction * PedestrianLinkMaxLength * 2f, a, b, out float t) && t < best)
                {
                    best = t;
                    hit = from + direction * PedestrianLinkMaxLength * 2f * t;
                }
            }
            return hit;
        }

        /// <summary>Straight path a→b stays inside the shape and crosses no street (ignoring its two ends).</summary>
        private static bool PathClear(float2 a, float2 b, List<(float2[] points, float2 min, float2 max)> polylines, List<float2> polygon)
        {
            float2 middle = (a + b) * 0.5f;
            if (!PointInPolygon(middle, polygon)) return false;
            float2 lo = math.min(a, b), hi = math.max(a, b);
            foreach ((float2[] points, float2 min, float2 max) in polylines)
            {
                if (math.any(max < lo) || math.any(min > hi)) continue;
                for (int k = 1; k < points.Length; k++)
                {
                    if (StrictCross(a, b, points[k - 1], points[k])) return false;
                }
            }
            return true;
        }

        private static bool StrictCross(float2 a1, float2 a2, float2 b1, float2 b2)
        {
            float Orient(float2 a, float2 b, float2 c) => (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x);
            float d1 = Orient(b1, b2, a1), d2 = Orient(b1, b2, a2), d3 = Orient(a1, a2, b1), d4 = Orient(a1, a2, b2);
            const float e = 1e-2f;
            return ((d1 > e && d2 < -e) || (d1 < -e && d2 > e)) && ((d3 > e && d4 < -e) || (d3 < -e && d4 > e));
        }
    }
}
