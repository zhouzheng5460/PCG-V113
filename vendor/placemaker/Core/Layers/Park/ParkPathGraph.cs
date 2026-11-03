// Ported from ParkManager (tomkpunkt, GPL-3.0), Geometry/ParkPathPlan.cs.
// Changes: Placemaker namespace; per-layer edge widths; the naturalness score takes the wanted
// loop count from the "paths" control instead of a fixed area rule; corner cutting is replaced
// by tangent-continuous Bézier output in ParkPathPlanner.EmitCurves (CS2 nets curve natively,
// so one curved course reads better than several short straight ones).

using System;
using System.Collections.Generic;
using Placemaker.Core.Layers.Common;
using Unity.Mathematics;

namespace Placemaker.Core.Layers.Park
{
    public enum PathNodeKind { Interior = 0, Gate = 1 }
    public enum PathEdgeKind { Secondary = 0, Primary = 1, GateApproach = 2 }

    public sealed class PathNode
    {
        public int Id;
        public float2 Position;
        public PathNodeKind Kind;
        /// <summary>Index into the gate list for gate nodes, −1 otherwise.</summary>
        public int Gate = -1;
    }

    public sealed class PathEdge
    {
        public int Id;
        public int A, B;
        public PathEdgeKind Kind;
    }

    /// <summary>Planned path network in the XZ plane: nodes (junctions, bends, gates) and straight edges.</summary>
    public sealed class ParkPathGraph
    {
        public readonly List<PathNode> Nodes;
        public readonly List<PathEdge> Edges;
        public readonly float TotalLength;

        private ParkPathGraph(List<PathNode> nodes, List<PathEdge> edges, float length)
        {
            Nodes = nodes; Edges = edges; TotalLength = length;
        }

        public static ParkPathGraph Empty() => new ParkPathGraph(new List<PathNode>(), new List<PathEdge>(), 0f);

        public bool IsEmpty => Edges.Count == 0;

        public static ParkPathGraph FromSegments(IReadOnlyList<float2> segments, IReadOnlyList<float2> gates)
        {
            if (segments == null || segments.Count < 2) return Empty();
            var nodes = new List<PathNode>();
            var edges = new List<PathEdge>();
            var keys = new HashSet<long>();
            for (int i = 0; i + 1 < segments.Count; i += 2)
            {
                int a = FindOrAdd(nodes, segments[i], gates);
                int b = FindOrAdd(nodes, segments[i + 1], gates);
                if (a == b) continue;
                if (!keys.Add(Key(a, b))) continue;
                if (math.distance(nodes[a].Position, nodes[b].Position) < 1f) continue;
                edges.Add(new PathEdge { A = a, B = b });
            }
            Simplify(nodes, edges);
            float total = 0;
            for (int i = 0; i < edges.Count; i++)
            {
                edges[i].Id = i;
                total += math.distance(nodes[edges[i].A].Position, nodes[edges[i].B].Position);
            }
            Classify(nodes, edges);
            return new ParkPathGraph(nodes, edges, total);
        }

        /// <summary>
        /// Removes topology with no useful visible counterpart: non-gate dead ends when there are
        /// several gates (a stub ending in a round cap), and degree-two vertices on virtually
        /// straight runs (every vertex becomes a CS2 net node).
        /// </summary>
        private static void Simplify(List<PathNode> nodes, List<PathEdge> edges)
        {
            if (nodes.Count == 0 || edges.Count == 0) return;
            int gateCount = 0;
            foreach (var n in nodes) if (n.Kind == PathNodeKind.Gate) gateCount++;
            if (gateCount > 1)
            {
                bool changed = true;
                while (changed)
                {
                    changed = false;
                    var degree = Degree(nodes.Count, edges);
                    for (int node = 0; node < nodes.Count; node++)
                    {
                        if (degree[node] != 1 || nodes[node].Kind == PathNodeKind.Gate) continue;
                        for (int e = edges.Count - 1; e >= 0; e--)
                        {
                            if (edges[e].A != node && edges[e].B != node) continue;
                            edges.RemoveAt(e); changed = true; break;
                        }
                    }
                }
            }

            bool collapsed = true;
            while (collapsed)
            {
                collapsed = false;
                var incident = Adjacency(nodes.Count, edges);
                for (int node = 0; node < nodes.Count; node++)
                {
                    if (nodes[node].Kind == PathNodeKind.Gate || incident[node].Count != 2) continue;
                    int fi = incident[node][0], si = incident[node][1];
                    var first = edges[fi]; var second = edges[si];
                    int a = first.A == node ? first.B : first.A;
                    int b = second.A == node ? second.B : second.A;
                    if (a == b || HasEdge(edges, a, b)) continue;
                    var c = nodes[node].Position;
                    var da = math.normalizesafe(nodes[a].Position - c);
                    var db = math.normalizesafe(nodes[b].Position - c);
                    if (math.dot(da, db) > -0.985f
                        || SegDistSq(c, nodes[a].Position, nodes[b].Position) > 0.5625f) continue;
                    edges.RemoveAt(math.max(fi, si));
                    edges.RemoveAt(math.min(fi, si));
                    edges.Add(new PathEdge { A = a, B = b });
                    collapsed = true;
                    break;
                }
            }
            Compact(nodes, edges);
        }

        /// <summary>Lower is better. Penalises stubs, sharp bends, crowded junctions, wrong loop count and poor coverage.</summary>
        public double NaturalnessScore(Common.LayerRegion region, int wantedLoops)
        {
            if (Nodes.Count == 0 || Edges.Count == 0) return double.MaxValue;
            var adjacency = Adjacency(Nodes.Count, Edges);
            double score = Nodes.Count * 0.4 + TotalLength / Common.Layout.Scale * 0.015;
            foreach (var e in Edges)
            {
                float len = math.distance(Nodes[e.A].Position, Nodes[e.B].Position);
                float shortLen = Common.Layout.M(8f), longLen = Common.Layout.M(65f);   // layout size: judge edges at design size
                if (len < shortLen) score += (shortLen - len) / Common.Layout.Scale * 5.0;
                if (len > longLen) score += (len - longLen) / Common.Layout.Scale * 0.35;
            }
            for (int node = 0; node < Nodes.Count; node++)
            {
                int degree = adjacency[node].Count;
                if (degree == 1 && Nodes[node].Kind != PathNodeKind.Gate) score += 75.0;
                if (degree > 3) score += (degree - 3) * 140.0;
                if (degree < 2) continue;
                double minAngle = 180.0;
                for (int a = 0; a < degree; a++)
                    for (int b = a + 1; b < degree; b++)
                    {
                        var va = DirectionFrom(node, Edges[adjacency[node][a]]);
                        var vb = DirectionFrom(node, Edges[adjacency[node][b]]);
                        double angle = Math.Acos(math.clamp(math.dot(va, vb), -1f, 1f)) * 180.0 / Math.PI;
                        minAngle = Math.Min(minAngle, angle);
                    }
                if (degree == 2 && minAngle < 105.0) { double d = 105.0 - minAngle; score += d * d * 0.055; }
                else if (degree >= 3 && minAngle < 50.0) { double d = 50.0 - minAngle; score += d * d * 0.12; }
            }
            int components = Components(adjacency);
            int loops = Math.Max(0, Edges.Count - Nodes.Count + components);
            score += Math.Abs(loops - wantedLoops) * (loops < wantedLoops ? 250.0 : 35.0);   // a missing loop is worse than an extra one
            score += CoveragePenalty(region);
            return score;
        }

        private double CoveragePenalty(Common.LayerRegion region)
        {
            double total = 0; int samples = 0;
            const int steps = 8;
            for (int y = 1; y < steps; y++)
                for (int x = 1; x < steps; x++)
                {
                    var p = math.lerp(region.Min, region.Max, new float2((float)x / steps, (float)y / steps));
                    if (!region.Contains(p)) continue;
                    float nearest = float.MaxValue;
                    foreach (var e in Edges) nearest = math.min(nearest, SegDistSq(p, Nodes[e.A].Position, Nodes[e.B].Position));
                    total += Math.Sqrt(nearest);
                    samples++;
                }
            return samples == 0 ? 0 : total / samples / Common.Layout.Scale * 1.8;
        }

        public List<int>[] Adjacency() => Adjacency(Nodes.Count, Edges);

        public static List<int>[] Adjacency(int count, IReadOnlyList<PathEdge> edges)
        {
            var adj = new List<int>[count];
            for (int i = 0; i < count; i++) adj[i] = new List<int>(3);
            for (int i = 0; i < edges.Count; i++) { adj[edges[i].A].Add(i); adj[edges[i].B].Add(i); }
            return adj;
        }

        private static int[] Degree(int count, IReadOnlyList<PathEdge> edges)
        {
            var d = new int[count];
            foreach (var e in edges) { d[e.A]++; d[e.B]++; }
            return d;
        }

        private static bool HasEdge(IReadOnlyList<PathEdge> edges, int a, int b)
        {
            foreach (var e in edges) if (e.A == a && e.B == b || e.A == b && e.B == a) return true;
            return false;
        }

        private static void Compact(List<PathNode> nodes, List<PathEdge> edges)
        {
            var used = new bool[nodes.Count];
            foreach (var e in edges) { used[e.A] = true; used[e.B] = true; }
            var map = new int[nodes.Count];
            var compact = new List<PathNode>();
            for (int i = 0; i < nodes.Count; i++)
            {
                map[i] = -1;
                if (!used[i]) continue;
                map[i] = compact.Count;
                compact.Add(new PathNode { Id = compact.Count, Position = nodes[i].Position, Kind = nodes[i].Kind, Gate = nodes[i].Gate });
            }
            foreach (var e in edges) { e.A = map[e.A]; e.B = map[e.B]; }
            nodes.Clear();
            nodes.AddRange(compact);
        }

        private float2 DirectionFrom(int node, PathEdge e)
        {
            int other = e.A == node ? e.B : e.A;
            return math.normalizesafe(Nodes[other].Position - Nodes[node].Position);
        }

        private int Components(List<int>[] adjacency)
        {
            var seen = new bool[adjacency.Length];
            int count = 0;
            var stack = new WorkStack<int>();
            for (int s = 0; s < adjacency.Length; s++)
            {
                if (seen[s] || adjacency[s].Count == 0) continue;
                count++;
                stack.Push(s); seen[s] = true;
                while (stack.Count > 0)
                {
                    int n = stack.Pop();
                    foreach (int ei in adjacency[n])
                    {
                        var e = Edges[ei];
                        int o = e.A == n ? e.B : e.A;
                        if (seen[o]) continue;
                        seen[o] = true; stack.Push(o);
                    }
                }
            }
            return Math.Max(1, count);
        }

        private static int FindOrAdd(List<PathNode> nodes, float2 p, IReadOnlyList<float2> gates)
        {
            for (int i = 0; i < nodes.Count; i++)
                if (math.distancesq(nodes[i].Position, p) <= 0.04f) return i;
            var node = new PathNode { Id = nodes.Count, Position = p, Kind = PathNodeKind.Interior };
            if (gates != null)
                for (int g = 0; g < gates.Count; g++)
                    if (math.distancesq(gates[g], p) <= 0.25f) { node.Kind = PathNodeKind.Gate; node.Gate = g; break; }
            nodes.Add(node);
            return nodes.Count - 1;
        }

        /// <summary>Gate approaches and bridges (edges whose removal disconnects the network) are main paths.</summary>
        private static void Classify(List<PathNode> nodes, List<PathEdge> edges)
        {
            var adj = Adjacency(nodes.Count, edges);
            var disc = new int[nodes.Count];
            var low = new int[nodes.Count];
            int time = 0;
            var bridges = new HashSet<int>();
            for (int i = 0; i < nodes.Count; i++)
                if (disc[i] == 0) Bridges(i, -1, adj, edges, disc, low, ref time, bridges);
            for (int i = 0; i < edges.Count; i++)
            {
                var e = edges[i];
                if (nodes[e.A].Kind == PathNodeKind.Gate || nodes[e.B].Kind == PathNodeKind.Gate) e.Kind = PathEdgeKind.GateApproach;
                else if (bridges.Contains(i)) e.Kind = PathEdgeKind.Primary;
                else e.Kind = PathEdgeKind.Secondary;
            }
        }

        // Iterative Tarjan bridge search: recursion depth equals path length, which can exceed a
        // worker thread's small stack on large painted areas.
        private static void Bridges(int root, int parentEdge, List<int>[] adj, List<PathEdge> edges,
            int[] disc, int[] low, ref int time, HashSet<int> bridges)
        {
            var stack = new WorkStack<(int node, int parentEdge, int next)>();
            disc[root] = low[root] = ++time;
            stack.Push((root, parentEdge, 0));
            while (stack.Count > 0)
            {
                var (node, pe, next) = stack.Pop();
                if (next < adj[node].Count)
                {
                    stack.Push((node, pe, next + 1));
                    int ei = adj[node][next];
                    if (ei == pe) continue;
                    var e = edges[ei];
                    int other = e.A == node ? e.B : e.A;
                    if (disc[other] == 0)
                    {
                        disc[other] = low[other] = ++time;
                        stack.Push((other, ei, 0));
                    }
                    else low[node] = Math.Min(low[node], disc[other]);
                }
                else if (pe >= 0)
                {
                    var e = edges[pe];
                    int parent = e.A == node ? e.B : e.A;
                    low[parent] = Math.Min(low[parent], low[node]);
                    if (low[node] > disc[parent]) bridges.Add(pe);
                }
            }
        }

        internal static float SegDistSq(float2 p, float2 a, float2 b) => Common.SegmentIndex.DistSq(p, a, b);

        private static long Key(int a, int b) => ((long)math.min(a, b) << 32) | (uint)math.max(a, b);
    }
}
