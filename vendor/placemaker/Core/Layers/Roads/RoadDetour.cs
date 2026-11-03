// Placemaker roads layer (s61) — obstacle avoidance that resolves before it drops.
//
// Playtest: "several grid pieces near the markers are missing" and a perimeter that did not close: a building or a
// pond near a street removed the WHOLE street piece (up to 90 m of perimeter). Now a blocked piece first tries to
// bend around the obstacle (its middle shifted sideways by 6–36 m, inward for the perimeter, clear of every obstacle,
// inside the area, crossing nothing); only a piece that cannot bypass it is removed — and then it is reported with
// where and why (Avoid obstacles), never silently.

using System;
using System.Collections.Generic;
using Placemaker.Core.Geometry;
using Unity.Mathematics;

namespace Placemaker.Core.Layers.Roads
{
    internal static class RoadDetour
    {
        public sealed class Result
        {
            public int Removed, Bypassed, Trimmed;
            /// <summary>Where each removed piece was (its middle) and its role.</summary>
            public readonly List<(float2 at, NetRole role)> RemovedAt = new List<(float2, NetRole)>();
        }

        private static readonly float[] Offsets = { 6f, 10f, 15f, 21f, 28f, 36f };

        /// <summary>
        /// Handles every blocked alive edge accepted by <paramref name="eligible"/>: bypass it, else remove it.
        /// <paramref name="boundary"/> keeps a bypass inside the area.
        /// </summary>
        public static Result Run(RoadNetwork net, RoadObstacles obstacles, Predicate<RoadEdge> eligible, Func<RoadEdge, float> halfWidth, float2[] boundary)
        {
            var res = new Result();
            if (obstacles == null || obstacles.IsEmpty) return res;
            RoadGraph.EdgeIndex index = null;
            List<int>[] inc = null;
            for (int i = 0; i < net.Edges.Count; i++)
            {
                var e = net.Edges[i];
                if (!e.Alive || (eligible != null && !eligible(e))) continue;
                float hw = halfWidth != null ? halfWidth(e) : 0f;
                if (!obstacles.Blocked(e.Curve, hw)) continue;
                if (index == null)
                {
                    index = new RoadGraph.EdgeIndex(net, RoadGraph.Any);
                    inc = RoadGraph.Incidence(net, RoadGraph.Any);
                }
                if (TryBypass(net, e, i, obstacles, hw, boundary, index, inc))
                {
                    res.Bypassed++;
                    continue;
                }
                // A street blocked only at one end (a house next to the outline) is shortened to the clear part instead.
                if (e.Role != NetRole.PerimeterRoad && TryTrim(e, obstacles, hw))
                {
                    res.Trimmed++;
                    continue;
                }
                e.Removed = true;
                res.Removed++;
                res.RemovedAt.Add((e.Curve.Position(0.5f).xz, e.Role));
            }
            return res;
        }

        /// <summary>
        /// A piece blocked only over its first or last stretch (at most 40 %): cut back to just past the obstacle, keeping at
        /// least 30 m. The new free end becomes a cul-de-sac (or goes, if short) in the dead-end pass.
        /// </summary>
        private static bool TryTrim(RoadEdge e, RoadObstacles obstacles, float hw)
        {
            if (e.Walk) return false;
            var c = e.Curve;
            float len = c.LengthXZ(8);
            if (len < 60f) return false;
            int samples = math.max(8, (int)math.ceil(len / 4f));
            var blocked = new bool[samples + 1];
            int first = -1, last = -1;
            for (int k = 0; k <= samples; k++)
            {
                blocked[k] = obstacles.BlockedAt(c.Position((float)k / samples).xz, hw);
                if (!blocked[k]) continue;
                if (first < 0) first = k;
                last = k;
            }
            if (first < 0) return false;
            for (int k = first; k <= last; k++) if (!blocked[k]) return false;   // one blocked stretch only
            float slack = 8f;
            int slackSamples = (int)math.ceil(slack / (len / samples));
            if (first == 0 && last < samples * 0.4f)
            {
                int cut = math.min(samples, last + 1 + slackSamples);
                if (len * (samples - cut) / samples < 30f) return false;
                e.Curve = c.Sub((float)cut / samples, 1f);
                return true;
            }
            if (last == samples && first > samples * 0.6f)
            {
                int cut = math.max(0, first - 1 - slackSamples);
                if (len * cut / samples < 30f) return false;
                e.Curve = c.Sub(0f, (float)cut / samples);
                e.CapAtEnd = false;
                return true;
            }
            return false;
        }

        private static bool TryBypass(RoadNetwork net, RoadEdge e, int ei, RoadObstacles obstacles, float hw, float2[] boundary,
            RoadGraph.EdgeIndex index, List<int>[] inc)
        {
            if (e.N0 < 0 || e.N1 < 0 || e.Walk) return false;
            var c = e.Curve;
            float len = c.LengthXZ(8);
            float2 chord = c.D.xz - c.A.xz;
            float chordLen = math.length(chord);
            if (chordLen < 20f) return false;
            float2 n = new float2(-chord.y, chord.x) / chordLen;
            bool straight = c.IsStraight();
            var ignore = new HashSet<int> { ei };
            if (e.N0 < inc.Length) foreach (int k in inc[e.N0]) ignore.Add(k);
            if (e.N1 < inc.Length) foreach (int k in inc[e.N1]) ignore.Add(k);
            foreach (float off in Offsets)
            {
                if (off > 0.4f * len) break;
                foreach (float side in new[] { 1f, -1f })
                {
                    float o = off / 0.75f * side;
                    float3 shift = new float3(n.x, 0f, n.y) * o;
                    // A straight piece bulges from 40 % / 60 % of its length (a flatter, wider bump); a curved one keeps its controls.
                    float3 b0 = straight ? math.lerp(c.A, c.D, 0.4f) : c.B, c0 = straight ? math.lerp(c.A, c.D, 0.6f) : c.C;
                    var cand = new RoadCurve(c.A, b0 + shift, c0 + shift, c.D);
                    // Not a hook: the ends may turn at most ~58° off the chord.
                    if (RoadGraph.Angle(cand.TangentXZ(0f), chord) > 58f || RoadGraph.Angle(cand.TangentXZ(1f), chord) > 58f) continue;
                    if (obstacles.Blocked(cand, hw)) continue;
                    var line = RoadGraph.Sample(cand, 3f);
                    bool inside = true;
                    foreach (var p in line)
                        if (!Poly.Contains(boundary, p) && Poly.DistanceToBoundary(boundary, p) > 1f) { inside = false; break; }
                    if (!inside) continue;
                    // Clear of the boundary by the road's body unless it is the perimeter itself (which sits on it).
                    if (index.Crosses(line, 3f, ignore)) continue;
                    e.Curve = cand;
                    return true;
                }
            }
            return false;
        }
    }
}
