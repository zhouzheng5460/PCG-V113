// Entrance connectors (s9, playtest "edge spikes"): a path meets the outline only at an entrance and
// arrives roughly perpendicular to the edge. The old connectors joined each gate to the nearest point
// of the network, which for a spine ending near the edge (or running beside it) was a short leg
// sideways ALONG the outline. Here the connector leaves the gate straight inward along the edge
// normal; it either meets the network on that line (a clean T) or, after a short straight approach,
// turns at most ~50° off the normal to the nearest reachable network point.

using System;
using System.Collections.Generic;
using Placemaker.Core.Geometry;
using Unity.Mathematics;

namespace Placemaker.Core.Layers.Common
{
    public static class GateApproach
    {
        /// <summary>Largest angle (degrees) between the inward edge normal and the turned leg.</summary>
        public const float MaxTurn = 50f;

        /// <summary>Inward normal of the outline at gate point <paramref name="gate"/>.</summary>
        public static float2 Inward(LayerRegion region, float2 gate)
        {
            region.ClosestOnBoundary(gate, out int seg, out _);
            return region.InwardNormal(seg);
        }

        /// <summary>
        /// Connector polyline from <paramref name="gate"/> (on the outline) to the network <paramref name="spine"/>:
        /// [gate, hit] when the inward normal meets it, else [gate, approach, foot] with a straight approach of
        /// <paramref name="approach"/> m and a leg within MaxTurn of the normal. Null when nothing is reachable
        /// inside the area (or <paramref name="clear"/> rejects every leg). Ends on the network already (within
        /// 1 m) return [gate] only.
        /// </summary>
        public static List<float2> Route(LayerRegion region, float2 gate, IReadOnlyList<(float2 a, float2 b)> spine, float approach,
            Func<float2, float2, bool> clear = null)
        {
            if (region == null || spine == null || spine.Count == 0) return null;
            var n = Inward(region, gate);
            float near = float.MaxValue;
            foreach (var (a, b) in spine) near = math.min(near, math.distance(SegmentIndex.ClosestPoint(gate, a, b), gate));
            if (near <= 1f) return new List<float2> { gate };

            bool Ok(float2 p, float2 q) => !region.CrossesBoundary(p, q, true) && (clear == null || clear(p, q));
            // A leg must not cross the network before its own end, nor run alongside a path (within 2.5 m)
            // before its last few metres.
            bool Free(float2 p, float2 q)
            {
                float len = math.distance(p, q);
                if (len < 1.5f) return true;
                var dir = (q - p) / len;
                var s0 = p; var s1 = q - dir * 1.2f;
                foreach (var (a, b) in spine)
                {
                    if (Poly.SegmentsIntersect(s0, s1, a, b)) return false;
                    if (SegmentIndex.DistSq(a, s0, s1) < 0.09f || SegmentIndex.DistSq(b, s0, s1) < 0.09f) return false;
                }
                for (float s = 1f; s < len - 4f; s += 1f)
                {
                    var x = p + dir * s;
                    foreach (var (a, b) in spine)
                        if (SegmentIndex.DistSq(q, a, b) > 0.0025f && SegmentIndex.DistSq(x, a, b) < 6.25f) return false;   // the host itself is fine

                }
                return true;
            }

            // 0. A network end straight in front of the gate (an axis aimed at it): extend that end to the gate.
            float bestEnd = float.MaxValue; float2 end = default;
            foreach (var (a, b) in spine)
                foreach (var e in new[] { a, b })
                {
                    var v = e - gate;
                    float along = math.dot(v, n), lateral = math.abs(v.x * n.y - v.y * n.x);
                    if (along > 0.5f && lateral < 1f && along < bestEnd && Ok(gate + n * 0.05f, e) && Free(gate, e)) { bestEnd = along; end = e; }
                }

            // 1. Straight in along the normal: first crossing with the network.
            float bestT = float.MaxValue;
            foreach (var (a, b) in spine)
                if (Ray(gate, n, a, b, out float t) && t > 0.5f && t < bestT) bestT = t;
            if (bestEnd < float.MaxValue && bestEnd <= bestT + 0.5f) return new List<float2> { gate, end };
            if (bestT < float.MaxValue)
            {
                var hit = gate + n * bestT;
                if (Ok(gate + n * 0.05f, hit) && Free(gate, hit)) return new List<float2> { gate, hit };
            }

            // 2. Short straight approach, then the nearest network point within MaxTurn of the normal.
            float cos = math.cos(math.radians(MaxTurn));
            // Last resort: a deeper approach (well clear of the edge band), then any leg that does not head back out.
            foreach (var (app, limit) in new[] { (approach, cos), (approach * 0.5f, cos), (approach * 2f, cos), (approach * 2f, 0.05f), (approach * 3f, 0.05f) })
            {
                var from = gate + n * app;
                if (app >= bestT - 1f || !region.Contains(from) || !Ok(gate + n * 0.05f, from) || !Free(gate, from)) continue;
                // Candidates within MaxTurn of the normal, nearest first; the first clean leg wins.
                var cands = new List<(float l, float2 q)>();
                foreach (var (a, b) in spine)
                {
                    float len = math.distance(a, b);
                    int steps = math.max(1, (int)math.ceil(len));
                    for (int i = 0; i <= steps; i++)
                    {
                        var q = math.lerp(a, b, i / (float)steps);
                        var d = q - from;
                        float l = math.length(d);
                        if (l < 0.5f) return new List<float2> { gate, q };
                        if (math.dot(d / l, n) >= limit) cands.Add((l, q));
                    }
                }
                cands.Sort((x, y) => x.l.CompareTo(y.l));
                int tries = 0;
                foreach (var (l, q) in cands)
                {
                    if (++tries > 400) break;
                    if (Ok(from, q) && Free(from, q)) return new List<float2> { gate, from, q };
                }
            }
            return null;
        }

        /// <summary>
        /// Connector from a gate to one destination point: straight when it heads within MaxTurn of the edge
        /// normal, else a straight approach first ([gate, approach, target]). Null when it would leave the area.
        /// </summary>
        public static List<float2> ToPoint(LayerRegion region, float2 gate, float2 target, float approach)
        {
            var n = Inward(region, gate);
            var d = target - gate;
            float len = math.length(d);
            if (len < 0.5f) return null;
            if (math.dot(d / len, n) >= math.cos(math.radians(MaxTurn)))
                return region.CrossesBoundary(gate, target, true) ? null : new List<float2> { gate, target };
            var from = gate + n * approach;
            if (!region.Contains(from) || region.CrossesBoundary(gate, from, true) || region.CrossesBoundary(from, target, false)) return null;
            return new List<float2> { gate, from, target };
        }

        /// <summary>Ray p + t·dir against segment a–b; t is the distance along the (unit) ray.</summary>
        public static bool Ray(float2 p, float2 dir, float2 a, float2 b, out float t)
        {
            t = 0f;
            var s = b - a;
            float den = dir.x * s.y - dir.y * s.x;
            if (math.abs(den) < 1e-6f) return false;
            var w = a - p;
            t = (w.x * s.y - w.y * s.x) / den;
            float u = (w.x * dir.y - w.y * dir.x) / den;
            return t >= 0f && u >= -1e-4f && u <= 1f + 1e-4f;
        }
    }
}
