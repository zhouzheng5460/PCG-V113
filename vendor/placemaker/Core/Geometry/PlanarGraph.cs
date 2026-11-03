// Placemaker — in-memory planar road graph with cursor-aware block picking.
//
// Grid Road Generator picks a block by double-clicking a node and walking "right-hand" from it,
// which cannot choose WHICH block at an X junction. Here the player hovers a point inside the block:
// the nearest road segment visible from the cursor (no other road in between) necessarily bounds
// the cursor's face, so the walk starts on the half-edge that has the cursor on its left and the
// face is traced exactly (planar half-edge walk). If that walk turns out to be the outside of a
// separate road component (e.g. a loop road inside the block), that component is set aside as a
// hole and the search continues outwards.

using System;
using System.Collections.Generic;
using Unity.Mathematics;

namespace Placemaker.Core.Geometry
{
    public enum FacePickStatus
    {
        Ok = 0,
        /// <summary>No edges in the graph.</summary>
        EmptyGraph = 1,
        /// <summary>No road within the search distance in any direction.</summary>
        NoRoadNearby = 2,
        /// <summary>The point is not enclosed by roads (outside the network).</summary>
        OutsideNetwork = 3,
        /// <summary>The face has more boundary edges than allowed (usually an open, unclosed area).</summary>
        TooLarge = 4,
        /// <summary>The face boundary could not be turned into a usable ring.</summary>
        Degenerate = 5,
    }

    /// <summary>Result of <see cref="PlanarGraph.PickFace"/>.</summary>
    public sealed class FacePick
    {
        public FacePickStatus Status;
        /// <summary>Block outline along road centrelines (edge geometry), CCW, simple. Empty unless Ok.</summary>
        public float2[] Ring = Array.Empty<float2>();
        /// <summary>Boundary nodes in ring order.</summary>
        public readonly List<long> NodeIds = new List<long>();
        /// <summary>Boundary edges in ring order, with the traversal direction (true = edge's A→B).</summary>
        public readonly List<long> EdgeIds = new List<long>();
        public readonly List<bool> EdgeForward = new List<bool>();
        /// <summary>Dead-end roads reaching into the block (removed from the ring).</summary>
        public readonly List<long> InteriorEdgeIds = new List<long>();
        /// <summary>Outlines (CW) of road components lying entirely inside the block.</summary>
        public readonly List<float2[]> Holes = new List<float2[]>();

        public bool Ok => Status == FacePickStatus.Ok;
        public float Area => Poly.Area(Ring);
        public Region ToRegion() => new Region(Ring, Holes);
    }

    /// <summary>
    /// Road network snapshot for selection: nodes with positions, edges with optional polyline
    /// geometry (sampled curve, A→B, endpoints included). Build it from the nets near the cursor;
    /// exclude elevated/tunnel edges (crossings without a node would break planarity). Implements
    /// <see cref="INetworkGraph{TNode}"/> for Dijkstra path fill and the node boundary trace.
    /// Queries build lazy indices: call <see cref="Prepare"/> once after the last edit before querying
    /// from several threads; edits are never thread-safe.
    /// </summary>
    public sealed class PlanarGraph : INetworkGraph<long>
    {
        private sealed class Node
        {
            public long Id;
            public float2 Pos;
            public readonly List<int> Out = new List<int>(4); // half-edges, sorted CCW when built
        }

        private sealed class Edge
        {
            public long Id;
            public int A, B;
            public float2[] Geom; // A → B, endpoints = node positions
            public float Length;
        }

        private readonly Dictionary<long, int> _nodeIndex = new Dictionary<long, int>();
        private readonly HashSet<long> _edgeIds = new HashSet<long>();
        private readonly List<Node> _nodes = new List<Node>();
        private readonly List<Edge> _edges = new List<Edge>();
        private bool _dirty = true;
        private int[] _posInOut;
        private int[] _component;

        /// <summary>Walks with less signed area (m²) than this are trees / outsides, never faces.</summary>
        public const double TreeAreaEpsilon = 0.5;

        public int NodeCount => _nodes.Count;
        public int EdgeCount => _edges.Count;

        public void Clear()
        {
            _nodeIndex.Clear(); _edgeIds.Clear(); _nodes.Clear(); _edges.Clear();
            _dirty = true;
        }

        /// <summary>Adds a node (re-adding an id moves it).</summary>
        public void AddNode(long id, float2 position)
        {
            if (_nodeIndex.TryGetValue(id, out int i)) { _nodes[i].Pos = position; _dirty = true; return; }
            _nodeIndex[id] = _nodes.Count;
            _nodes.Add(new Node { Id = id, Pos = position });
            _dirty = true;
        }

        public bool HasNode(long id) => _nodeIndex.ContainsKey(id);

        public bool TryGetNodePosition(long id, out float2 position)
        {
            position = default;
            if (!_nodeIndex.TryGetValue(id, out int i)) return false;
            position = _nodes[i].Pos;
            return true;
        }

        /// <summary>
        /// Adds an undirected edge between two existing nodes. geometry (optional) runs from a to b;
        /// its ends are snapped to the node positions. False for unknown nodes, self-loops, duplicate
        /// ids or non-finite geometry.
        /// </summary>
        public bool AddEdge(long id, long a, long b, IReadOnlyList<float2> geometry = null)
        {
            if (_edgeIds.Contains(id) || !_nodeIndex.TryGetValue(a, out int ia) || !_nodeIndex.TryGetValue(b, out int ib) || ia == ib) return false;
            var pts = new List<float2> { _nodes[ia].Pos };
            if (geometry != null)
                for (int k = 1; k + 1 < geometry.Count; k++)
                {
                    if (!Poly.IsFinite(geometry[k])) return false;
                    if (math.distancesq(geometry[k], pts[pts.Count - 1]) > 1e-8f) pts.Add(geometry[k]);
                }
            if (pts.Count > 1 && math.distancesq(pts[pts.Count - 1], _nodes[ib].Pos) <= 1e-8f) pts.RemoveAt(pts.Count - 1);
            pts.Add(_nodes[ib].Pos);
            if (pts.Count == 2 && math.distancesq(pts[0], pts[1]) <= 1e-8f) return false;
            float len = 0;
            for (int k = 1; k < pts.Count; k++) len += math.distance(pts[k - 1], pts[k]);
            _edgeIds.Add(id);
            _edges.Add(new Edge { Id = id, A = ia, B = ib, Geom = pts.ToArray(), Length = len });
            _dirty = true;
            return true;
        }

        /// <summary>Builds the angular order and components now (afterwards queries only read).</summary>
        public void Prepare() => Build();

        // ───────────────────────────── INetworkGraph ─────────────────────────────

        public IReadOnlyList<GraphEdge<long>> GetNeighbors(long node)
        {
            Build();
            var result = new List<GraphEdge<long>>();
            if (!_nodeIndex.TryGetValue(node, out int i)) return result;
            foreach (int h in _nodes[i].Out)
            {
                var e = _edges[h >> 1];
                result.Add(new GraphEdge<long>(_nodes[Dst(h)].Id, e.Length, StartDirection(h)));
            }
            return result;
        }

        // ───────────────────────────── half-edges ─────────────────────────────

        private int Org(int h) => (h & 1) == 0 ? _edges[h >> 1].A : _edges[h >> 1].B;
        private int Dst(int h) => (h & 1) == 0 ? _edges[h >> 1].B : _edges[h >> 1].A;

        private float2 GeomPoint(int h, int k)
        {
            var g = _edges[h >> 1].Geom;
            return (h & 1) == 0 ? g[k] : g[g.Length - 1 - k];
        }

        private float2 StartDirection(int h)
        {
            var g = _edges[h >> 1].Geom;
            float2 o = GeomPoint(h, 0);
            for (int k = 1; k < g.Length; k++)
            {
                float2 d = GeomPoint(h, k) - o;
                if (math.lengthsq(d) > 1e-6f) return math.normalize(d);
            }
            return new float2(1, 0);
        }

        private void Build()
        {
            if (!_dirty) return;
            foreach (var n in _nodes) n.Out.Clear();
            int nh = _edges.Count * 2;
            var angle = new double[nh];
            var angle2 = new double[nh];
            for (int h = 0; h < nh; h++)
            {
                float2 d = StartDirection(h);
                angle[h] = math.atan2(d.y, d.x);
                // Tie-break for edges leaving in the same direction: where they are heading further on.
                float2 far = GeomPoint(h, _edges[h >> 1].Geom.Length - 1) - GeomPoint(h, 0);
                angle2[h] = math.atan2(far.y, far.x);
                _nodes[Org(h)].Out.Add(h);
            }
            _posInOut = new int[nh];
            foreach (var n in _nodes)
            {
                n.Out.Sort((p, q) => angle[p] != angle[q] ? angle[p].CompareTo(angle[q]) : angle2[p] != angle2[q] ? angle2[p].CompareTo(angle2[q]) : p.CompareTo(q));
                for (int k = 0; k < n.Out.Count; k++) _posInOut[n.Out[k]] = k;
            }
            // Connected components (union-find over edges).
            var parent = new int[_nodes.Count];
            for (int i = 0; i < parent.Length; i++) parent[i] = i;
            int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
            foreach (var e in _edges) { int a = Find(e.A), b = Find(e.B); if (a != b) parent[a] = b; }
            _component = new int[_nodes.Count];
            for (int i = 0; i < _component.Length; i++) _component[i] = Find(i);
            _dirty = false;
        }

        /// <summary>Next half-edge around the face on the left (clockwise-adjacent to the twin at the far node).</summary>
        private int Next(int h)
        {
            var list = _nodes[Dst(h)].Out;
            int k = _posInOut[h ^ 1];
            return list[(k - 1 + list.Count) % list.Count];
        }

        // ───────────────────────────── face picking ─────────────────────────────

        /// <summary>
        /// The block (planar face of the road graph) containing point. Rays in 8 directions find the
        /// nearest road visible from the point (within maxDistance); the face is walked from there.
        /// Dead ends reaching into the block are stripped (reported in InteriorEdgeIds) when
        /// stripDeadEnds is set; road components inside the block are reported as Holes.
        /// Never throws; check Status.
        /// </summary>
        public FacePick PickFace(float2 point, float maxDistance = 1500f, int maxBoundaryEdges = 4000, bool stripDeadEnds = true)
        {
            var result = new FacePick();
            if (_edges.Count == 0) { result.Status = FacePickStatus.EmptyGraph; return result; }
            if (!Poly.IsFinite(point)) { result.Status = FacePickStatus.NoRoadNearby; return result; }
            Build();
            var excluded = new HashSet<int>();
            bool anyHit = false;
            for (int attempt = 0; attempt < 64; attempt++)
            {
                if (!NearestVisibleHalfEdge(point, maxDistance, excluded, out int h0))
                {
                    result.Status = anyHit ? FacePickStatus.OutsideNetwork : FacePickStatus.NoRoadNearby;
                    return result;
                }
                anyHit = true;
                var cycle = WalkFace(h0, maxBoundaryEdges);
                if (cycle == null) { result.Status = FacePickStatus.TooLarge; return result; }
                double area = CycleArea(cycle);
                // s43: a tree (open-ended paths, a lone dead end) walks out and back: its area is zero up to
                // rounding, which must never count as a face around the point.
                if (area <= TreeAreaEpsilon)
                {
                    // Outside of a road component that does not enclose the point: look past it.
                    excluded.Add(_component[Org(h0)]);
                    continue;
                }
                return Finish(result, point, cycle, stripDeadEnds, maxBoundaryEdges);
            }
            result.Status = FacePickStatus.OutsideNetwork;
            return result;
        }

        private FacePick Finish(FacePick result, float2 point, List<int> cycle, bool stripDeadEnds, int maxEdges)
        {
            if (stripDeadEnds)
            {
                // Out-and-back pairs (h followed by its twin) are dead ends inside the block.
                var stack = new List<int>(cycle.Count);
                foreach (int h in cycle)
                {
                    if (stack.Count > 0 && stack[stack.Count - 1] == (h ^ 1)) { result.InteriorEdgeIds.Add(_edges[h >> 1].Id); stack.RemoveAt(stack.Count - 1); }
                    else stack.Add(h);
                }
                while (stack.Count >= 2 && stack[0] == (stack[stack.Count - 1] ^ 1))
                {
                    result.InteriorEdgeIds.Add(_edges[stack[0] >> 1].Id);
                    stack.RemoveAt(stack.Count - 1);
                    stack.RemoveAt(0);
                }
                if (stack.Count >= 2) cycle = stack;
            }

            var ring = new List<float2>();
            foreach (int h in cycle)
            {
                result.NodeIds.Add(_nodes[Org(h)].Id);
                result.EdgeIds.Add(_edges[h >> 1].Id);
                result.EdgeForward.Add((h & 1) == 0);
                int len = _edges[h >> 1].Geom.Length;
                for (int k = 0; k < len - 1; k++)
                {
                    float2 p = GeomPoint(h, k);
                    if (ring.Count == 0 || math.distancesq(ring[ring.Count - 1], p) > 1e-8f) ring.Add(p);
                }
            }
            while (ring.Count > 1 && math.distancesq(ring[0], ring[ring.Count - 1]) <= 1e-8f) ring.RemoveAt(ring.Count - 1);
            var outer = Poly.EnsureCcw(ring.ToArray());
            if (!Poly.IsSimple(outer))
            {
                // The face touches itself (blocks joined at one node, leftover spikes): keep the piece under the cursor.
                outer = null;
                foreach (var reg in Poly.Normalize(ring, FillRule.NonZero))
                    if (reg.Contains(point) || Poly.Contains(reg.Outer, point)) { outer = reg.Outer; break; }
            }
            if (outer == null || outer.Length < 3) { result.Status = FacePickStatus.Degenerate; return result; }
            result.Ring = outer;

            // Road components entirely inside the block become holes (their outer boundary).
            var faceComp = _component[Org(cycle[0])];
            var seen = new HashSet<int> { faceComp };
            for (int i = 0; i < _nodes.Count; i++)
            {
                int comp = _component[i];
                if (!seen.Add(comp)) continue;
                if (!Poly.Contains(outer, _nodes[i].Pos)) continue;
                var hole = ComponentOutline(comp, maxEdges, result.InteriorEdgeIds);
                if (hole != null) result.Holes.Add(hole);
            }
            result.Status = FacePickStatus.Ok;
            return result;
        }

        /// <summary>Outer boundary (CW ring) of a component; trees (no area) only report their edges as interior.</summary>
        private float2[] ComponentOutline(int comp, int maxEdges, List<long> interior)
        {
            var visited = new HashSet<int>();
            List<int> worst = null;
            double worstArea = double.MaxValue;
            for (int h = 0; h < _edges.Count * 2; h++)
            {
                if (_component[Org(h)] != comp || visited.Contains(h)) continue;
                var cyc = WalkFace(h, maxEdges);
                if (cyc == null) return null;
                foreach (int x in cyc) visited.Add(x);
                double a = CycleArea(cyc);
                if (a < worstArea) { worstArea = a; worst = cyc; }
            }
            if (worst == null) return null;
            if (worstArea > -1e-3)
            {
                foreach (int h in worst) if ((h & 1) == 0) interior.Add(_edges[h >> 1].Id);
                return null;
            }
            var ring = new List<float2>();
            foreach (int h in worst)
            {
                int len = _edges[h >> 1].Geom.Length;
                for (int k = 0; k < len - 1; k++) ring.Add(GeomPoint(h, k));
            }
            var cleaned = Poly.Clean(ring, 1e-3f);
            return cleaned.Length >= 3 ? Poly.EnsureCw(cleaned) : null;
        }

        private List<int> WalkFace(int h0, int maxEdges)
        {
            var cycle = new List<int>();
            int h = h0;
            do
            {
                cycle.Add(h);
                if (cycle.Count > maxEdges) return null;
                h = Next(h);
            } while (h != h0);
            return cycle;
        }

        private double CycleArea(List<int> cycle)
        {
            double a = 0;
            double2 o = _nodes[Org(cycle[0])].Pos;
            foreach (int h in cycle)
            {
                int len = _edges[h >> 1].Geom.Length;
                for (int k = 0; k < len - 1; k++)
                {
                    double2 p = (double2)GeomPoint(h, k) - o, q = (double2)GeomPoint(h, k + 1) - o;
                    a += p.x * q.y - q.x * p.y;
                }
            }
            return a * 0.5;
        }

        /// <summary>
        /// Half-edge (with the point on its left) of the nearest road segment hit by one of 8 rays from
        /// the point. The open segment from the point to that hit crosses no road, so the hit edge
        /// bounds the point's face.
        /// </summary>
        private bool NearestVisibleHalfEdge(float2 point, float maxDistance, HashSet<int> excludedComponents, out int halfEdge)
        {
            halfEdge = -1;
            double best = maxDistance;
            double2 p = point;
            for (int r = 0; r < 8; r++)
            {
                // Slightly irrational angles so rays never run exactly through grid-aligned nodes.
                double ang = r * math.PI / 4 + 0.0137;
                double2 dir = new double2(math.cos(ang), math.sin(ang));
                for (int e = 0; e < _edges.Count; e++)
                {
                    var edge = _edges[e];
                    if (excludedComponents.Contains(_component[edge.A])) continue;
                    var g = edge.Geom;
                    for (int k = 0; k + 1 < g.Length; k++)
                    {
                        double2 a = g[k], s = (double2)g[k + 1] - a;
                        double den = dir.x * s.y - dir.y * s.x;
                        if (den == 0) continue;
                        double2 ap = a - p;
                        double t = (ap.x * s.y - ap.y * s.x) / den;
                        double u = (ap.x * dir.y - ap.y * dir.x) / den;
                        if (t <= 0 || t >= best || u < 0 || u > 1) continue;
                        best = t;
                        // Point on the left of the segment's forward direction → forward half-edge.
                        double side = s.x * (p.y - a.y) - s.y * (p.x - a.x);
                        halfEdge = side > 0 ? 2 * e : 2 * e + 1;
                    }
                }
            }
            return halfEdge >= 0;
        }
    }
}
