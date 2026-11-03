// Placemaker — graph algorithms over the existing road network (engine-free).
//
// Adapted from Grid Road Generator (Ti4goc), Core/NetworkGraph.cs: INetworkGraph abstraction,
// Dijkstra path fill between two clicked nodes, and the "hug the wall" closed-boundary trace with
// short-detour (cul-de-sac / turning loop) suppression. Changes: binary-heap Dijkstra (no linear scan),
// deterministic tie-breaks, English documentation. For picking the block under the cursor see
// PlanarGraph.TryPickFace.

using System;
using System.Collections.Generic;
using Unity.Mathematics;

namespace Placemaker.Core.Geometry
{
    /// <summary>
    /// Outgoing edge of a network node: the neighbour reached, the cost for shortest paths (edge
    /// length) and the leaving direction at the node (unit XZ; used by the boundary trace).
    /// </summary>
    public readonly struct GraphEdge<TNode>
    {
        public readonly TNode Neighbor;
        public readonly float Cost;
        public readonly float2 Direction;

        public GraphEdge(TNode neighbor, float cost, float2 direction)
        {
            Neighbor = neighbor;
            Cost = cost;
            Direction = direction;
        }
    }

    /// <summary>
    /// The existing road network, independent of ECS: algorithms are tested on in-memory graphs and
    /// plugged into game entities by an adapter in Game/.
    /// </summary>
    public interface INetworkGraph<TNode>
    {
        /// <summary>Outgoing edges of the node (a new or stable list; callers do not modify it).</summary>
        IReadOnlyList<GraphEdge<TNode>> GetNeighbors(TNode node);
    }

    public static class NetworkGraphAlgorithms
    {
        /// <summary>
        /// Shortest path (Dijkstra, cost = sum of edge lengths) between two nodes of the existing
        /// network. False when no path exists or the search visits more than maxVisitedNodes
        /// (guard on very large networks). outPath includes start and end.
        /// </summary>
        public static bool FindShortestPath<TNode>(INetworkGraph<TNode> graph, TNode start, TNode end,
            List<TNode> outPath, int maxVisitedNodes = 2000)
        {
            outPath.Clear();
            if (graph == null) return false;
            var comparer = EqualityComparer<TNode>.Default;
            if (comparer.Equals(start, end))
            {
                outPath.Add(start);
                return true;
            }

            var visited = new HashSet<TNode>();
            var cameFrom = new Dictionary<TNode, TNode>();
            var costSoFar = new Dictionary<TNode, float> { [start] = 0f };
            var heap = new MinHeap<TNode>();
            heap.Push(start, 0f);
            bool found = false;
            while (heap.Count > 0)
            {
                heap.Pop(out TNode current, out float priority);
                if (!visited.Add(current)) continue;
                if (priority > costSoFar[current]) continue;
                if (comparer.Equals(current, end)) { found = true; break; }
                if (visited.Count > maxVisitedNodes) return false;
                foreach (GraphEdge<TNode> edge in graph.GetNeighbors(current))
                {
                    if (visited.Contains(edge.Neighbor)) continue;
                    float cost = costSoFar[current] + math.max(0f, edge.Cost);
                    if (!costSoFar.TryGetValue(edge.Neighbor, out float existing) || cost < existing)
                    {
                        costSoFar[edge.Neighbor] = cost;
                        cameFrom[edge.Neighbor] = current;
                        heap.Push(edge.Neighbor, cost);
                    }
                }
            }
            if (!found) return false;

            var reversed = new List<TNode>();
            TNode node = end;
            int guard = cameFrom.Count + 2;
            while (!comparer.Equals(node, start))
            {
                reversed.Add(node);
                if (!cameFrom.TryGetValue(node, out node) || guard-- <= 0) return false;
            }
            reversed.Add(start);
            reversed.Reverse();
            outPath.AddRange(reversed);
            return true;
        }

        /// <summary>
        /// Follows the closed boundary of the network from startNode, always taking the smallest
        /// clockwise rotation from the reversed arrival direction (classic planar face walk). outLoop
        /// holds startNode then the loop nodes in order, without repeating startNode. Fails cleanly
        /// (never loops forever) when the start has no edge, a node is a dead end, the walk revisits a
        /// node without closing, or maxNodes is exceeded. Short dead ends and small turning loops
        /// (&lt; 150 m) are ignored while another way continues the main loop.
        /// </summary>
        public static bool TraceBoundary<TNode>(INetworkGraph<TNode> graph, TNode startNode,
            List<TNode> outLoop, int maxNodes = 50)
        {
            outLoop.Clear();
            if (graph == null) return false;
            var comparer = EqualityComparer<TNode>.Default;

            IReadOnlyList<GraphEdge<TNode>> initialNeighbors = graph.GetNeighbors(startNode);
            if (initialNeighbors == null || initialNeighbors.Count == 0) return false;

            // First step: geometry-only choice (smallest clockwise angle from north) so the result
            // never depends on the adapter's iteration order.
            IReadOnlyList<GraphEdge<TNode>> initialCandidates = IgnoreShortDetours(graph, initialNeighbors, startNode);
            GraphEdge<TNode> firstEdge = PickSmallestClockwiseAngle(initialCandidates, new float2(0f, 1f));

            outLoop.Add(startNode);
            var visited = new HashSet<TNode> { startNode };
            TNode current = startNode;
            float2 arrivalDirection = firstEdge.Direction;
            TNode next = firstEdge.Neighbor;

            for (int step = 0; step < maxNodes; step++)
            {
                if (comparer.Equals(next, startNode)) return true;
                if (!visited.Add(next)) return false;
                outLoop.Add(next);

                var candidates = new List<GraphEdge<TNode>>();
                foreach (GraphEdge<TNode> edge in graph.GetNeighbors(next))
                    if (!comparer.Equals(edge.Neighbor, current)) candidates.Add(edge); // no immediate U-turn
                if (candidates.Count == 0) return false;

                IReadOnlyList<GraphEdge<TNode>> filtered = IgnoreShortDetours(graph, candidates, next);
                GraphEdge<TNode> chosen = PickSmallestClockwiseAngle(filtered, -arrivalDirection);
                current = next;
                arrivalDirection = chosen.Direction;
                next = chosen.Neighbor;
            }
            return false;
        }

        /// <summary>Total length (m) below which an excursion from a junction is a short detour to ignore.</summary>
        private const float ShortDetourMaxLength = 150f;
        private const int ShortDetourMaxNodes = 30;

        private static IReadOnlyList<GraphEdge<TNode>> IgnoreShortDetours<TNode>(
            INetworkGraph<TNode> graph, IReadOnlyList<GraphEdge<TNode>> candidates, TNode junction)
        {
            if (candidates.Count <= 1) return candidates;
            var filtered = new List<GraphEdge<TNode>>();
            foreach (GraphEdge<TNode> candidate in candidates)
                if (!IsShortDetour(graph, candidate.Neighbor, junction, candidate.Cost)) filtered.Add(candidate);
            return filtered.Count > 0 ? filtered : candidates;
        }

        /// <summary>
        /// True if every path from viaNode (reached from junction) that does not turn back ends quickly —
        /// in a plain dead end or a small loop closing back on junction — without meeting a real
        /// intersection or exceeding ShortDetourMaxLength.
        /// </summary>
        private static bool IsShortDetour<TNode>(INetworkGraph<TNode> graph, TNode viaNode, TNode junction, float initialCost)
        {
            var comparer = EqualityComparer<TNode>.Default;
            TNode previous = junction;
            TNode current = viaNode;
            float accumulated = initialCost;
            if (accumulated > ShortDetourMaxLength) return false;
            for (int step = 0; step < ShortDetourMaxNodes; step++)
            {
                GraphEdge<TNode>? onlyForward = null;
                int forwardCount = 0;
                foreach (GraphEdge<TNode> edge in graph.GetNeighbors(current))
                {
                    if (comparer.Equals(edge.Neighbor, previous)) continue;
                    forwardCount++;
                    onlyForward = edge;
                    if (forwardCount > 1) break;
                }
                if (forwardCount == 0) return true;
                if (forwardCount > 1) return false;
                accumulated += onlyForward.Value.Cost;
                if (accumulated > ShortDetourMaxLength) return false;
                if (comparer.Equals(onlyForward.Value.Neighbor, junction)) return true;
                previous = current;
                current = onlyForward.Value.Neighbor;
            }
            return false;
        }

        private static GraphEdge<TNode> PickSmallestClockwiseAngle<TNode>(IReadOnlyList<GraphEdge<TNode>> candidates, float2 referenceDirection)
        {
            GraphEdge<TNode> best = candidates[0];
            float bestAngle = ClockwiseAngle(referenceDirection, best.Direction);
            for (int i = 1; i < candidates.Count; i++)
            {
                float angle = ClockwiseAngle(referenceDirection, candidates[i].Direction);
                if (angle < bestAngle) { bestAngle = angle; best = candidates[i]; }
            }
            return best;
        }

        /// <summary>Clockwise rotation (radians, [0, 2π)) taking `from` onto `to`.</summary>
        internal static float ClockwiseAngle(float2 from, float2 to)
        {
            const float TwoPi = 2f * math.PI;
            float theta = math.atan2(from.y, from.x) - math.atan2(to.y, to.x);
            theta %= TwoPi;
            if (theta < 0f) theta += TwoPi;
            return theta;
        }
    }

    /// <summary>Small binary min-heap (net48 has no PriorityQueue). Ties pop in insertion order.</summary>
    internal sealed class MinHeap<T>
    {
        private readonly List<(float p, long seq, T v)> _a = new List<(float, long, T)>();
        private long _seq;
        public int Count => _a.Count;

        private static bool Less((float p, long seq, T v) x, (float p, long seq, T v) y) => x.p < y.p || (x.p == y.p && x.seq < y.seq);

        public void Push(T v, float priority)
        {
            _a.Add((priority, _seq++, v));
            int i = _a.Count - 1;
            while (i > 0)
            {
                int parent = (i - 1) >> 1;
                if (!Less(_a[i], _a[parent])) break;
                var t = _a[i]; _a[i] = _a[parent]; _a[parent] = t; i = parent;
            }
        }

        public void Pop(out T v, out float priority)
        {
            var top = _a[0];
            int last = _a.Count - 1;
            _a[0] = _a[last];
            _a.RemoveAt(last);
            int i = 0;
            while (true)
            {
                int l = 2 * i + 1, r = l + 1, m = i;
                if (l < _a.Count && Less(_a[l], _a[m])) m = l;
                if (r < _a.Count && Less(_a[r], _a[m])) m = r;
                if (m == i) break;
                var t = _a[i]; _a[i] = _a[m]; _a[m] = t; i = m;
            }
            v = top.v;
            priority = top.p;
        }
    }
}
