// Placemaker roads layer — the planned road network between generation and output.
// Holds generated edges as Bézier curves, welds coincident end points into nodes, prunes
// stubs and tracks which nodes connect to existing roads.

using System;
using System.Collections.Generic;
using Placemaker.Core.Geometry;
using Unity.Mathematics;

namespace Placemaker.Core.Layers.Roads
{
    internal sealed class RoadEdge
    {
        public RoadCurve Curve;
        public NetRole Role;
        /// <summary>The D end is a cul-de-sac tip that gets a turning-circle object.</summary>
        public bool CapAtEnd;
        /// <summary>Removed by obstacle avoidance, overlap with an existing road or stub pruning.</summary>
        public bool Removed;
        /// <summary>Never part of the result (collapsed to zero length, duplicate).</summary>
        public bool Discarded;
        public int N0 = -1, N1 = -1;
        /// <summary>(s34) Explicit prefab for this piece (gravel tips, gravel lanes), "" = the role's road.</summary>
        public string AssetOverride = "";
        /// <summary>(s34) Asset category for a car-free piece ("net.trail" for dirt walks), "" = the role default.</summary>
        public string Category = "";
        /// <summary>(s34) Added by the connectivity pass (links an island or the network to the city).</summary>
        public bool Connector;
        /// <summary>(s34) A walkway / cut-through of the pedestrian network (role Pathway).</summary>
        public bool Walk;
        /// <summary>(s34) Dirt look: the walk gets a worn-ground paint corridor (and a trail asset when one exists).</summary>
        public bool Trail;
        /// <summary>(s52) Hillside terraces: index of the terrace street this piece belongs to (its terrace band), -1 = none.</summary>
        public int TerraceStreet = -1;
        /// <summary>(s52) Hillside terraces: a designed link between terraces (its heights are the designed ramp).</summary>
        public bool TerraceLink;

        public RoadEdge(RoadCurve curve, NetRole role, bool capAtEnd = false)
        {
            Curve = curve; Role = role; CapAtEnd = capAtEnd;
        }

        public bool Alive => !Removed && !Discarded;

        /// <summary>(s34) A road cars use (everything but walkways / car-free paths).</summary>
        public bool IsCar => Role != NetRole.Pathway && Role != NetRole.PathwayWide;

        public RoadEdge Clone(RoadCurve curve) => new RoadEdge(curve, Role, CapAtEnd)
        {
            AssetOverride = AssetOverride, Category = Category, Connector = Connector, Walk = Walk, Trail = Trail,
            TerraceStreet = TerraceStreet, TerraceLink = TerraceLink,
        };
    }

    internal sealed class RoadNode
    {
        public float3 Position;
        /// <summary>Existing net element (node or edge ExternalId) this node connects to; -1 = free.</summary>
        public long Attach = -1;
        /// <summary>Curve parameter on the attached edge (0/1 = its start/end node).</summary>
        public float AttachT;
        /// <summary>Counts as connected to the outside (attached, or on an existing-road outline).</summary>
        public bool Anchored;
    }

    internal sealed class RoadNetwork
    {
        /// <summary>End points closer than this are the same node (GRG shares exact floats; transforms add noise).</summary>
        public const float WeldDistance = 0.05f;

        public readonly List<RoadEdge> Edges = new List<RoadEdge>();
        public readonly List<RoadNode> Nodes = new List<RoadNode>();

        /// <summary>(s61) Sharp outline corners (xz): a node there is kept when a crowding junction merges with it.</summary>
        public readonly List<float2> Corners = new List<float2>();

        public bool IsCorner(RoadNode n)
        {
            foreach (var c in Corners) if (math.distancesq(c, n.Position.xz) < 0.09f) return true;
            return false;
        }

        /// <summary>
        /// Rebuilds the node table from edge end points (all non-discarded edges, removed ones too, so
        /// "degree before removal" stays measurable) and writes canonical node positions back into the
        /// curves so coincident ends are bit-identical, which is what makes the game merge them.
        /// Attachment data of previous nodes is kept by position.
        /// </summary>
        public void Weld()
        {
            var old = new List<RoadNode>(Nodes);
            Nodes.Clear();
            var cells = new Dictionary<long, List<int>>();
            int Find(float3 p)
            {
                int cx = (int)math.floor(p.x), cz = (int)math.floor(p.z);
                for (int dx = -1; dx <= 1; dx++)
                    for (int dz = -1; dz <= 1; dz++)
                        if (cells.TryGetValue(Key(cx + dx, cz + dz), out var list))
                            foreach (int id in list)
                                if (math.distancesq(Nodes[id].Position.xz, p.xz) <= WeldDistance * WeldDistance) return id;
                var n = new RoadNode { Position = p };
                foreach (var o in old)
                    if (o.Attach >= 0 || o.Anchored)
                        if (math.distancesq(o.Position.xz, p.xz) <= WeldDistance * WeldDistance)
                        {
                            n.Attach = o.Attach; n.AttachT = o.AttachT; n.Anchored = o.Anchored; n.Position = o.Position;
                            break;
                        }
                Nodes.Add(n);
                long k = Key(cx, cz);
                if (!cells.TryGetValue(k, out var l)) cells[k] = l = new List<int>();
                l.Add(Nodes.Count - 1);
                return Nodes.Count - 1;
            }
            foreach (var e in Edges)
            {
                if (e.Discarded) { e.N0 = e.N1 = -1; continue; }
                e.N0 = Find(e.Curve.A);
                e.N1 = Find(e.Curve.D);
                e.Curve = e.Curve.WithEnds(Nodes[e.N0].Position, Nodes[e.N1].Position);
                if (e.N0 == e.N1) e.Discarded = true;
            }
        }

        private static long Key(int x, int z) => ((long)x << 32) ^ (uint)z;

        public int[] Degrees(bool aliveOnly)
        {
            var d = new int[Nodes.Count];
            foreach (var e in Edges)
            {
                if (e.Discarded || (aliveOnly && e.Removed)) continue;
                d[e.N0]++; d[e.N1]++;
            }
            return d;
        }

        /// <summary>
        /// Removes dead ends that removal created: a node that had ≥2 edges before and has exactly one
        /// alive edge now (and is not anchored) is a stub; its edge goes, and the check repeats along
        /// the chain. Intentional dead ends (cul-de-sacs, streets ending at the outline) had one edge
        /// before and are kept. Fixes GRG leaving half-streets pointing into lakes and buildings.
        /// </summary>
        public int PruneStubs()
        {
            int[] before = Degrees(false);
            int[] after = Degrees(true);
            var incident = Incidence(true);
            var queue = new ListQueue<int>();
            for (int n = 0; n < Nodes.Count; n++)
                if (IsStub(n, before, after)) queue.Enqueue(n);
            int removed = 0;
            while (queue.Count > 0)
            {
                int n = queue.Dequeue();
                if (!IsStub(n, before, after)) continue;
                foreach (int ei in incident[n])
                {
                    var e = Edges[ei];
                    if (!e.Alive) continue;
                    e.Removed = true;
                    removed++;
                    after[e.N0]--; after[e.N1]--;
                    int other = e.N0 == n ? e.N1 : e.N0;
                    if (IsStub(other, before, after)) queue.Enqueue(other);
                    break;
                }
            }
            return removed;
        }

        private bool IsStub(int n, int[] before, int[] after) =>
            after[n] == 1 && before[n] >= 2 && !Nodes[n].Anchored && Nodes[n].Attach < 0;

        public List<int>[] Incidence(bool aliveOnly)
        {
            var inc = new List<int>[Nodes.Count];
            for (int i = 0; i < inc.Length; i++) inc[i] = new List<int>(4);
            for (int i = 0; i < Edges.Count; i++)
            {
                var e = Edges[i];
                if (e.Discarded || (aliveOnly && e.Removed)) continue;
                inc[e.N0].Add(i);
                if (e.N1 != e.N0) inc[e.N1].Add(i);
            }
            return inc;
        }

        /// <summary>
        /// Removes tiny unanchored fragments left after removal (e.g. a short piece isolated between
        /// two obstacles). Whole networks are kept even when nothing anchors them: the player may
        /// connect them later, and an unconnected new district is a legitimate plan.
        /// </summary>
        public int DropFragments(float minLength)
        {
            var inc = Incidence(true);
            var comp = new int[Nodes.Count];
            for (int i = 0; i < comp.Length; i++) comp[i] = -1;
            int removed = 0;
            int c = 0;
            var stack = new ListStack<int>();
            for (int start = 0; start < Nodes.Count; start++)
            {
                if (comp[start] >= 0 || inc[start].Count == 0) continue;
                var members = new List<int>();
                var edgeSet = new HashSet<int>();
                bool anchored = false;
                float length = 0f;
                comp[start] = c; stack.Push(start);
                while (stack.Count > 0)
                {
                    int n = stack.Pop();
                    members.Add(n);
                    anchored |= Nodes[n].Anchored || Nodes[n].Attach >= 0;
                    foreach (int ei in inc[n])
                    {
                        var e = Edges[ei];
                        int o = e.N0 == n ? e.N1 : e.N0;
                        if (comp[o] < 0) { comp[o] = c; stack.Push(o); }
                    }
                }
                foreach (int n in members)
                    foreach (int ei in inc[n])
                        edgeSet.Add(ei);
                foreach (int ei in edgeSet) length += Edges[ei].Curve.LengthXZ(8);
                if (!anchored && length < minLength)
                    foreach (int ei in edgeSet) { Edges[ei].Removed = true; removed++; }
                c++;
            }
            return removed;
        }

        /// <summary>
        /// Merges the two ends of alive edges shorter than minLength (typically after an end point was
        /// snapped onto an existing road). The anchored/attached end keeps its position; other edges of
        /// the merged node follow. GRG simply skipped such edges, which could disconnect a junction
        /// from the road it was meant to join.
        /// </summary>
        public int CollapseShortEdges(float minLength)
        {
            int collapsed = 0;
            for (int pass = 0; pass < 4; pass++)
            {
                bool any = false;
                foreach (var e in Edges)
                {
                    if (!e.Alive) continue;
                    if (math.distance(e.Curve.A.xz, e.Curve.D.xz) >= minLength) continue;
                    var a = Nodes[e.N0];
                    var b = Nodes[e.N1];
                    bool aFixed = a.Attach >= 0 || a.Anchored, bFixed = b.Attach >= 0 || b.Anchored;
                    if (aFixed && bFixed && a.Attach != b.Attach)
                    {
                        // Two different connections a few metres apart: keep the short link rather than
                        // moving a connection onto another road.
                        continue;
                    }
                    RoadNode keep = aFixed || !bFixed ? a : b;
                    // (s61) An outline corner stays where the outline has it: the street end that crowds it moves onto it.
                    bool aCorner = IsCorner(a), bCorner = IsCorner(b);
                    if (aCorner != bCorner && a.Attach < 0 && b.Attach < 0) keep = aCorner ? a : b;
                    RoadNode drop = keep == a ? b : a;
                    int keepId = keep == a ? e.N0 : e.N1, dropId = keep == a ? e.N1 : e.N0;
                    e.Discarded = true;
                    // Re-point every edge that used the dropped node.
                    foreach (var o in Edges)
                    {
                        if (o.Discarded) continue;
                        bool changed = false;
                        float3 na = o.Curve.A, nd = o.Curve.D;
                        if (o.N0 == dropId) { o.N0 = keepId; na = keep.Position; changed = true; }
                        if (o.N1 == dropId) { o.N1 = keepId; nd = keep.Position; changed = true; }
                        if (changed)
                        {
                            o.Curve = o.Curve.WithEnds(na, nd);
                            if (o.N0 == o.N1) o.Discarded = true;
                        }
                    }
                    drop.Position = keep.Position;
                    drop.Attach = keep.Attach; drop.AttachT = keep.AttachT; drop.Anchored = keep.Anchored;
                    collapsed++;
                    any = true;
                }
                if (!any) break;
            }
            DiscardDuplicates();
            return collapsed;
        }

        /// <summary>Two alive edges joining the same nodes with (nearly) the same shape: keep the first.</summary>
        public void DiscardDuplicates()
        {
            var seen = new Dictionary<(int, int), List<RoadEdge>>();
            foreach (var e in Edges)
            {
                if (!e.Alive) continue;
                var k = e.N0 < e.N1 ? (e.N0, e.N1) : (e.N1, e.N0);
                if (!seen.TryGetValue(k, out var list)) { seen[k] = new List<RoadEdge> { e }; continue; }
                float3 mid = e.Curve.Position(0.5f);
                bool dup = false;
                foreach (var o in list)
                    if (math.distance(o.Curve.Position(0.5f).xz, mid.xz) < 2f) { dup = true; break; }
                if (dup) e.Discarded = true; else list.Add(e);
            }
        }
    }
}
