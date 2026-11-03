// Placemaker — the Paint bucket's planar graph of wall curves (s43).
//
// Inputs are the nets near the cursor already classified by the game layer (FillWall kind, centre polyline,
// half-width) plus CS2's side connections: a pathway (or a road) may end ON another edge without splitting it
// (Game.Net.ConnectedNode on the host edge). Such an end is a dangling node in a node-to-node graph, the path is
// stripped as a dead end and the block is not split — one reason the bucket "ignored paths". Here the host
// edge is split at the projection of the connected node into sub-edges ending AT that node, so the path closes
// faces like a street does. Sub-edge ids are derived from the host id (SubEdgeId) and stay stable while the
// network does not change; sub-edges have no entity, so the game layer builds their outline from the stored
// geometry (CurbOutline) instead of the entity's curb curves.

using System;
using System.Collections.Generic;
using Unity.Mathematics;

namespace Placemaker.Core.Geometry
{
    /// <summary>One net edge as the bucket sees it: centre polyline A→B (endpoints = node positions).</summary>
    public struct FillCurve
    {
        public long Id, A, B;
        public float2[] Geom;
        public float HalfWidth;
        public FillWall Kind;
        /// <summary>The real edge this piece belongs to (= Id for an unsplit edge).</summary>
        public long Host;
        public bool IsSub => Host != Id;
    }

    /// <summary>A node lying on an edge it does not split (CS2 ConnectedNode).</summary>
    public struct FillSideLink
    {
        public long Host, Node;
        public FillSideLink(long host, long node) { Host = host; Node = node; }
    }

    public sealed class FillGraph
    {
        /// <summary>Links closer than this to a host end are joined to that end node with a short connector.</summary>
        public const float EndSnap = 0.5f;

        public readonly PlanarGraph Graph = new PlanarGraph();
        private readonly Dictionary<long, FillCurve> _edges = new Dictionary<long, FillCurve>();
        public FillWall Walls { get; private set; }

        public IEnumerable<FillCurve> Edges => _edges.Values;
        public int EdgeCount => _edges.Count;

        /// <summary>Id of piece <paramref name="piece"/> of a split host edge (bits 56..62 are never set by entity ids).</summary>
        public static long SubEdgeId(long host, int piece) => host ^ ((long)((piece & 0x7F) | 0x40) << 56);

        public bool TryEdge(long id, out FillCurve e) => _edges.TryGetValue(id, out e);

        /// <summary>
        /// Builds the graph of the curves whose kind is in <paramref name="walls"/>. nodes: positions of every
        /// endpoint (and of side-linked nodes). Never throws for bad input; unusable curves are skipped.
        /// </summary>
        public static FillGraph Build(IEnumerable<FillCurve> curves, IReadOnlyDictionary<long, float2> nodes, IEnumerable<FillSideLink> links, FillWall walls)
        {
            var fg = new FillGraph { Walls = walls };
            var used = new List<FillCurve>();
            var endpoints = new HashSet<long>();
            if (curves != null)
                foreach (var c in curves)
                {
                    if ((c.Kind & walls & FillWalls.Curves) == 0 || c.Geom == null || c.Geom.Length < 2 || c.A == c.B) continue;
                    if (!nodes.ContainsKey(c.A) || !nodes.ContainsKey(c.B)) continue;
                    used.Add(c);
                    endpoints.Add(c.A); endpoints.Add(c.B);
                }
            var byHost = new Dictionary<long, List<long>>();
            if (links != null)
                foreach (var l in links)
                {
                    if (!endpoints.Contains(l.Node) || !nodes.ContainsKey(l.Node)) continue;   // nothing enabled ends there
                    if (!byHost.TryGetValue(l.Host, out var list)) byHost[l.Host] = list = new List<long>();
                    if (!list.Contains(l.Node)) list.Add(l.Node);
                }
            foreach (var n in endpoints) fg.Graph.AddNode(n, nodes[n]);
            foreach (var c in used)
            {
                if (!byHost.TryGetValue(c.Id, out var linked)) { fg.Add(c, c.Id, c.A, c.B, c.Geom); continue; }
                fg.Split(c, linked, nodes);
            }
            fg.Graph.Prepare();
            return fg;
        }

        private void Add(FillCurve host, long id, long a, long b, float2[] geom)
        {
            if (!Graph.AddEdge(id, a, b, geom)) return;
            _edges[id] = new FillCurve { Id = id, A = a, B = b, Geom = geom, HalfWidth = host.HalfWidth, Kind = host.Kind, Host = host.Id };
        }

        private void Split(FillCurve c, List<long> linked, IReadOnlyDictionary<long, float2> nodes)
        {
            var g = c.Geom;
            var cum = new float[g.Length];
            for (int i = 1; i < g.Length; i++) cum[i] = cum[i - 1] + math.distance(g[i - 1], g[i]);
            float total = cum[g.Length - 1];
            var cuts = new List<(float s, long node, int seg, float2 at)>();
            int piece = 0;
            foreach (var n in linked)
            {
                if (n == c.A || n == c.B) continue;
                var p = nodes[n];
                float best = float.MaxValue, bestS = 0; int bestSeg = 0; float2 bestAt = g[0];
                for (int i = 0; i + 1 < g.Length; i++)
                {
                    float2 a = g[i], ab = g[i + 1] - a;
                    float l2 = math.lengthsq(ab);
                    float t = l2 > 1e-12f ? math.saturate(math.dot(p - a, ab) / l2) : 0f;
                    float2 q = a + ab * t;
                    float d = math.distancesq(q, p);
                    if (d < best) { best = d; bestSeg = i; bestS = cum[i] + t * math.sqrt(l2); bestAt = q; }
                }
                if (bestS < EndSnap || bestS > total - EndSnap)
                {
                    // At the host's end: a short connector to that end node keeps the linked node attached.
                    long end = bestS < EndSnap ? c.A : c.B;
                    Add(c, SubEdgeId(c.Id, 0x3F - (piece++ & 0x1F)), n, end, new[] { p, nodes[end] });
                    continue;
                }
                cuts.Add((bestS, n, bestSeg, bestAt));
            }
            if (cuts.Count == 0) { Add(c, c.Id, c.A, c.B, g); return; }
            cuts.Sort((x, y) => x.s.CompareTo(y.s));
            long from = c.A;
            var pts = new List<float2> { g[0] };
            int k = 0, seg = 0;
            foreach (var cut in cuts)
            {
                for (; seg < cut.seg; seg++) pts.Add(g[seg + 1]);
                pts.Add(nodes[cut.node]);
                Add(c, SubEdgeId(c.Id, k++), from, cut.node, pts.ToArray());
                from = cut.node;
                pts = new List<float2> { nodes[cut.node] };
                if (math.distancesq(cut.at, nodes[cut.node]) > 1e-6f) pts.Add(cut.at);
            }
            for (; seg + 1 < g.Length; seg++) pts.Add(g[seg + 1]);
            Add(c, SubEdgeId(c.Id, k), from, c.B, pts.ToArray());
        }

        /// <summary>Face under the point (see <see cref="PlanarGraph.PickFace"/>).</summary>
        public FacePick Pick(float2 point, float maxDistance = 1500f, int maxBoundaryEdges = 4000) => Graph.PickFace(point, maxDistance, maxBoundaryEdges, true);

        /// <summary>
        /// Centre-line ring of a loop of edges (edge ids with the node each one starts at), or null when an edge is
        /// not in this graph.
        /// </summary>
        public float2[] Ring(IReadOnlyList<long> edgeIds, IReadOnlyList<long> fromNodes)
        {
            var ring = new List<float2>();
            for (int i = 0; i < edgeIds.Count; i++)
            {
                if (!_edges.TryGetValue(edgeIds[i], out var e)) return null;
                bool fwd = e.A == fromNodes[i];
                var g = e.Geom;
                for (int k = 0; k < g.Length - 1; k++)
                {
                    var p = fwd ? g[k] : g[g.Length - 1 - k];
                    if (ring.Count == 0 || math.distancesq(ring[ring.Count - 1], p) > 1e-8f) ring.Add(p);
                }
            }
            while (ring.Count > 1 && math.distancesq(ring[0], ring[ring.Count - 1]) <= 1e-8f) ring.RemoveAt(ring.Count - 1);
            return ring.Count >= 3 ? ring.ToArray() : null;
        }

        /// <summary>
        /// The usable land of a loop: its centre ring minus the corridors of every wall edge in its box (the loop
        /// itself, dead ends and separate paths inside), the piece under <paramref name="seed"/>.
        /// </summary>
        public float2[] CurbOutline(float2[] centreRing, float2 seed)
        {
            if (centreRing == null) return null;
            Poly.Bounds(centreRing, out var min, out var max);
            var near = new List<FillCurveGeom>();
            foreach (var e in _edges.Values)
            {
                Poly.Bounds(e.Geom, out var a, out var b);
                if (a.x - e.HalfWidth > max.x || a.y - e.HalfWidth > max.y || b.x + e.HalfWidth < min.x || b.y + e.HalfWidth < min.y) continue;
                near.Add(new FillCurveGeom(e.Geom, e.HalfWidth));
            }
            return FillWalls.CurbOutline(centreRing, near, seed);
        }
    }
}
