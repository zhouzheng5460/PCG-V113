// Placemaker roads layer (s34) — access management on main roads.
//
// Playtest (image 61): the main street of a generated village had a side street every 40–60 m. A designer keeps the
// junctions on a main road far apart (here: "Junction spacing on main roads", default 120 m) and lets the side
// streets T into the local network instead. Junctions are kept by importance (the city's connections, crossings that
// continue on both sides, the side street serving the most road), the rest are cut back: the side street stops short
// of the main road in a cul-de-sac (a walkway keeps the short cut for people), or a short stub disappears. A cut that
// would strand part of the network is undone — that junction stays.

using System;
using System.Collections.Generic;
using Unity.Mathematics;

namespace Placemaker.Core.Layers.Roads
{
    internal static class RoadAccess
    {
        /// <summary>A side street cut back from a main road: walkway from its new end to the main road.</summary>
        public struct Cut
        {
            public float3 From;   // the side street's new end (or the far node when the whole piece went)
            public float3 To;     // the junction node on the main road
        }

        public static bool IsMain(RoadEdge e) => e.Role == NetRole.Avenue;

        /// <summary>
        /// Thins the side junctions of main roads to <paramref name="spacing"/> metres (0 = off). Returns the cuts made.
        /// </summary>
        public static List<Cut> Apply(RoadNetwork net, float spacing, float setback, float minCulDeSac, Action<RoadNetwork> relink = null)
        {
            var cuts = new List<Cut>();
            if (!(spacing > 1f)) return cuts;
            var inc = RoadGraph.Incidence(net, RoadGraph.Car);
            var lengths = RoadGraph.Lengths(net);
            var access = new List<(float2 at, bool forced, float weight)>();
            for (int n = 0; n < net.Nodes.Count; n++)
            {
                int mains = 0, sides = 0;
                foreach (int ei in inc[n]) if (IsMain(net.Edges[ei])) mains++; else sides++;
                if (mains == 0 || sides == 0) continue;
                bool forced = RoadConnectivity.Attached(net.Nodes[n]) || mains != 2;
                float weight = sides >= 2 ? 100000f : 0f;
                foreach (int ei in inc[n]) if (!IsMain(net.Edges[ei])) weight += SideReach(net, inc, lengths, n, ei, 3000f);
                access.Add((net.Nodes[n].Position.xz, forced, weight));
            }
            if (access.Count < 2) return cuts;
            access.Sort((a, b) => a.forced != b.forced ? (a.forced ? -1 : 1) : b.weight.CompareTo(a.weight));

            // Node indices change at every weld: junctions are tracked by position.
            var keptAt = new List<float2>();
            foreach (var (at, forced, _) in access)
            {
                int n = RoadGraph.NodeAt(net, at, 0.05f);
                if (n < 0) continue;
                var kept = new List<int>();
                foreach (var k in keptAt) { int kn = RoadGraph.NodeAt(net, k, 0.05f); if (kn >= 0) kept.Add(kn); }
                if (forced || !TooClose(net, inc, lengths, n, kept, spacing)) { keptAt.Add(at); continue; }
                // Try to cut every side street of this junction back from the main road.
                var snapshot = Snapshot(net);
                int before = Components(net);
                var made = new List<Cut>();
                var sides = new List<int>();
                foreach (int ei in inc[n]) if (!IsMain(net.Edges[ei])) sides.Add(ei);
                float3 junction = net.Nodes[n].Position;
                foreach (int ei in sides) made.Add(CutBack(net, ei, n, junction, setback, minCulDeSac));
                net.Weld();
                // A branch that only reached the network through this junction is linked to its neighbours instead
                // (never back onto the main road within the spacing); if that fails the junction stays.
                if (Components(net) > before && relink != null) relink(net);
                if (Components(net) <= before)
                {
                    cuts.AddRange(made);
                }
                else
                {
                    Restore(net, snapshot);
                    net.Weld();
                    keptAt.Add(at);
                }
                inc = RoadGraph.Incidence(net, RoadGraph.Car);
                lengths = RoadGraph.Lengths(net);
            }
            return cuts;
        }

        /// <summary>Length of side road reachable from the side edge without passing a main road (capped).</summary>
        private static float SideReach(RoadNetwork net, List<int>[] inc, float[] lengths, int junction, int edge, float cap)
        {
            var seen = new HashSet<int> { junction };
            var stack = new List<int>();
            float total = lengths[edge];
            int start = RoadGraph.Other(net.Edges[edge], junction);
            if (!seen.Add(start)) return total;
            stack.Add(start);
            var edges = new HashSet<int> { edge };
            while (stack.Count > 0 && total < cap)
            {
                int n = stack[stack.Count - 1];
                stack.RemoveAt(stack.Count - 1);
                foreach (int ei in inc[n])
                {
                    var e = net.Edges[ei];
                    if (IsMain(e) || !edges.Add(ei)) continue;
                    total += lengths[ei];
                    int o = RoadGraph.Other(e, n);
                    if (seen.Add(o)) stack.Add(o);
                }
            }
            return math.min(total, cap);
        }

        /// <summary>Is a kept junction within <paramref name="spacing"/> along the main roads from n?</summary>
        private static bool TooClose(RoadNetwork net, List<int>[] inc, float[] lengths, int n, List<int> kept, float spacing)
        {
            if (kept.Count == 0) return false;
            var keptSet = new HashSet<int>(kept);
            var mainInc = new List<int>[inc.Length];
            for (int i = 0; i < inc.Length; i++)
            {
                mainInc[i] = new List<int>(2);
                foreach (int ei in inc[i]) if (IsMain(net.Edges[ei])) mainInc[i].Add(ei);
            }
            var dist = RoadGraph.Distances(net, mainInc, n, spacing, lengths);
            foreach (int k in kept)
                if (k != n && dist[k] < spacing * 0.92f) return true;
            return false;
        }

        private static Cut CutBack(RoadNetwork net, int ei, int junction, float3 junctionPos, float setback, float minCulDeSac)
        {
            var e = net.Edges[ei];
            float len = RoadGraph.Length(e);
            int other = RoadGraph.Other(e, junction);
            float3 otherPos = net.Nodes[other].Position;
            if (len < setback + minCulDeSac || !e.Alive)
            {
                e.Removed = true;
                return new Cut { From = otherPos, To = junctionPos };
            }
            bool atStart = e.N0 == junction;
            float t = RoadGraph.ParamAt(e.Curve, setback, fromEnd: !atStart);
            int ci = RoadGraph.Split(net, ei, t);
            var copy = net.Edges[ci];
            float3 cutPoint = e.Curve.D;
            if (atStart)
            {
                // e = junction→cut (goes), copy = cut→other: turn it so the cut is its tip.
                e.Removed = true;
                copy.Curve = copy.Curve.Reversed();
                copy.CapAtEnd = true;
            }
            else
            {
                // e = other→cut (stays, tip at the cut), copy = cut→junction (goes).
                copy.Removed = true;
                e.CapAtEnd = true;
            }
            return new Cut { From = cutPoint, To = junctionPos };
        }

        private static int Components(RoadNetwork net)
        {
            RoadGraph.Components(net, RoadGraph.Car, out int count);
            return count;
        }

        private struct EdgeState
        {
            public RoadCurve Curve;
            public bool Cap, Removed, Discarded;
        }

        private static (int count, EdgeState[] states) Snapshot(RoadNetwork net)
        {
            var s = new EdgeState[net.Edges.Count];
            for (int i = 0; i < s.Length; i++)
            {
                var e = net.Edges[i];
                s[i] = new EdgeState { Curve = e.Curve, Cap = e.CapAtEnd, Removed = e.Removed, Discarded = e.Discarded };
            }
            return (net.Edges.Count, s);
        }

        private static void Restore(RoadNetwork net, (int count, EdgeState[] states) snap)
        {
            if (net.Edges.Count > snap.count) net.Edges.RemoveRange(snap.count, net.Edges.Count - snap.count);
            for (int i = 0; i < snap.count; i++)
            {
                var e = net.Edges[i];
                var st = snap.states[i];
                e.Curve = st.Curve; e.CapAtEnd = st.Cap; e.Removed = st.Removed; e.Discarded = st.Discarded;
            }
        }

        /// <summary>
        /// Access rule for links added later (connectivity): a link may join a main road only where no side junction is
        /// within the spacing (Euclidean, a conservative stand-in for the distance along the road).
        /// </summary>
        public static Func<RoadEdge, float2, bool> JoinRule(RoadNetwork net, float spacing)
        {
            if (!(spacing > 1f)) return null;
            int stamp = -1;
            var junctions = new List<float2>();
            return (edge, p) =>
            {
                if (!IsMain(edge)) return true;
                if (stamp != net.Edges.Count)
                {
                    stamp = net.Edges.Count;
                    junctions.Clear();
                    var inc = RoadGraph.Incidence(net, RoadGraph.Car);
                    for (int n = 0; n < net.Nodes.Count; n++)
                    {
                        if (inc[n].Count < 3) continue;
                        bool onMain = false;
                        foreach (int ei in inc[n]) onMain |= IsMain(net.Edges[ei]);
                        if (onMain) junctions.Add(net.Nodes[n].Position.xz);
                    }
                }
                foreach (var j in junctions)
                    if (math.distance(j, p) < spacing * 0.9f) return false;
                return true;
            };
        }
    }
}
