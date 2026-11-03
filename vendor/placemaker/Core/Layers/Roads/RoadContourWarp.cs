// Placemaker roads layer (s34) — "Follow contours" for the straight patterns (grid, superblocks).
//
// Playtest (image 62): the contour lines showed the generated network ignoring the hill. Turning the grid so its long
// streets run along the dominant contour direction (AlignToTerrain) only helps on an even slope; on a hill or in a
// valley the contours curve. Here every long street (a run of grid edges along the contour axis) is laid onto one
// contour: its nodes slide along the slope (at most a third of the block size, smoothly along the street) to the run's
// median height, and the street is redrawn as one smooth curve through them. Cross streets follow their nodes, so
// they climb between the contour streets; the grade check / steep pruning deals with the ones that end up too steep.
// Nothing moves when the result would cross itself.

using System;
using System.Collections.Generic;
using Placemaker.Core.Geometry;
using Unity.Mathematics;

namespace Placemaker.Core.Layers.Roads
{
    internal static class RoadContourWarp
    {
        /// <summary>(s61) Mean slope (rise/run) below which the ground is flat for this purpose: a strict grid stays straight (a 1 % tilt used to bend every street).</summary>
        public const float MinSlope = 0.035f;

        /// <summary>Lays the runs along <paramref name="axis"/> onto contour lines. Returns how many nodes moved.</summary>
        public static int Apply(RoadNetwork net, Func<float2, float> height, float2 axis, float spacing, float2[] boundary)
        {
            if (height == null || net.Edges.Count == 0) return 0;
            axis = math.normalizesafe(axis);
            bool Along(RoadEdge e) => e.Alive && e.IsCar && math.abs(math.dot(math.normalizesafe(e.Curve.D.xz - e.Curve.A.xz), axis)) > 0.9f;

            // Resolution: contour streets bend between the junctions too.
            int count = net.Edges.Count;
            for (int i = 0; i < count; i++)
            {
                var e = net.Edges[i];
                if (!Along(e)) continue;
                float len = e.Curve.LengthXZ(8);
                int pieces = (int)math.ceil(len / 30f);
                if (pieces < 2) continue;
                var whole = e.Curve;
                e.Curve = whole.Sub(0f, 1f / pieces);
                bool cap = e.CapAtEnd;
                e.CapAtEnd = false;
                for (int k = 1; k < pieces; k++)
                {
                    var piece = e.Clone(whole.Sub((float)k / pieces, (float)(k + 1) / pieces));
                    piece.CapAtEnd = cap && k == pieces - 1;
                    net.Edges.Add(piece);
                }
            }
            net.Weld();
            var snapshot = new List<RoadCurve>(net.Edges.Count);
            foreach (var e in net.Edges) snapshot.Add(e.Curve);
            var oldPos = new List<float3>(net.Nodes.Count);
            foreach (var n in net.Nodes) oldPos.Add(n.Position);

            var inc = RoadGraph.Incidence(net, Along);
            var comp = RoadGraph.Components(net, Along, out int runs);
            var targets = new float[runs];
            for (int r = 0; r < runs; r++)
            {
                var hs = new List<float>();
                for (int n = 0; n < comp.Length; n++) if (comp[n] == r) hs.Add(height(net.Nodes[n].Position.xz));
                hs.Sort();
                targets[r] = hs.Count > 0 ? hs[hs.Count / 2] : 0f;
            }
            float maxShift = 0.34f * math.max(30f, spacing);
            var shift = new float2[net.Nodes.Count];
            var movable = new bool[net.Nodes.Count];
            for (int n = 0; n < net.Nodes.Count; n++)
            {
                if (comp[n] < 0) continue;
                var node = net.Nodes[n];
                if (RoadConnectivity.Attached(node)) continue;
                if (boundary != null && boundary.Length >= 3 && Poly.DistanceToBoundary(boundary, node.Position.xz) < 4f) continue;
                movable[n] = true;
                float2 p = node.Position.xz, start = p;
                for (int it = 0; it < 6; it++)
                {
                    const float e = 4f;
                    float2 g = new float2(height(p + new float2(e, 0f)) - height(p - new float2(e, 0f)), height(p + new float2(0f, e)) - height(p - new float2(0f, e))) / (2f * e);
                    float gg = math.lengthsq(g);
                    if (gg < 1e-4f) break;
                    float dh = targets[comp[n]] - height(p);
                    p += g * (dh / gg) * 0.7f;
                    float2 d = p - start;
                    // Slide across the street only (along the slope), never along it.
                    float2 across = new float2(-axis.y, axis.x);
                    d = across * math.clamp(math.dot(d, across), -maxShift, maxShift);
                    p = start + d;
                }
                shift[n] = p - start;
            }
            // Smooth the shifts along each run so the street bends, never zigzags.
            for (int pass = 0; pass < 3; pass++)
            {
                var next = (float2[])shift.Clone();
                for (int n = 0; n < shift.Length; n++)
                {
                    if (!movable[n]) continue;
                    float2 sum = shift[n] * 2f;
                    float w = 2f;
                    foreach (int ei in inc[n])
                    {
                        int o = RoadGraph.Other(net.Edges[ei], n);
                        sum += shift[o];
                        w += 1f;
                    }
                    next[n] = sum / w;
                }
                for (int n = 0; n < shift.Length; n++) if (movable[n]) shift[n] = next[n];
            }
            int moved = 0;
            for (int n = 0; n < net.Nodes.Count; n++)
            {
                if (!movable[n] || math.lengthsq(shift[n]) < 0.01f) continue;
                var p = net.Nodes[n].Position;
                p.x += shift[n].x; p.z += shift[n].y;
                if (boundary != null && boundary.Length >= 3 && !Poly.Contains(boundary, p.xz)) continue;
                net.Nodes[n].Position = p;
                moved++;
            }
            foreach (var e in net.Edges)
            {
                if (e.N0 < 0) continue;
                e.Curve = RoadCurve.Straight(net.Nodes[e.N0].Position, net.Nodes[e.N1].Position).IsStraight() && e.Curve.IsStraight()
                    ? RoadCurve.Straight(net.Nodes[e.N0].Position, net.Nodes[e.N1].Position)
                    : e.Curve.WithEnds(net.Nodes[e.N0].Position, net.Nodes[e.N1].Position);
            }
            // Smooth curves along the runs: tangent at a node = direction from its previous to its next run node.
            for (int i = 0; i < net.Edges.Count; i++)
            {
                var e = net.Edges[i];
                if (e.N0 < 0 || !Along(e)) continue;
                float len = math.distance(e.Curve.A.xz, e.Curve.D.xz);
                float2 t0 = RunTangent(net, inc, e.N0, i, e.Curve.D.xz - e.Curve.A.xz);
                float2 t1 = RunTangent(net, inc, e.N1, i, e.Curve.D.xz - e.Curve.A.xz);
                var a = e.Curve.A; var d = e.Curve.D;
                e.Curve = new RoadCurve(a, a + new float3(t0.x, 0f, t0.y) * len / 3f, d - new float3(t1.x, 0f, t1.y) * len / 3f, d);
            }
            if (SelfCrossing(net))
            {
                for (int i = 0; i < net.Edges.Count; i++) net.Edges[i].Curve = snapshot[i];
                for (int n = 0; n < net.Nodes.Count; n++) net.Nodes[n].Position = oldPos[n];
                return 0;
            }
            net.Weld();
            return moved;
        }

        /// <summary>Unit direction of the run through a node, oriented like <paramref name="dir"/>.</summary>
        private static float2 RunTangent(RoadNetwork net, List<int>[] inc, int node, int edge, float2 dir)
        {
            float2 sum = float2.zero;
            foreach (int ei in inc[node])
            {
                var e = net.Edges[ei];
                int o = RoadGraph.Other(e, node);
                float2 v = math.normalizesafe(net.Nodes[o].Position.xz - net.Nodes[node].Position.xz);
                // Neighbours behind count backwards.
                sum += math.dot(v, dir) >= 0f ? v : -v;
            }
            if (math.lengthsq(sum) < 1e-6f) return math.normalizesafe(dir);
            sum = math.normalizesafe(sum);
            return math.dot(sum, dir) >= 0f ? sum : -sum;
        }

        private static bool SelfCrossing(RoadNetwork net)
        {
            var index = new RoadGraph.EdgeIndex(net, RoadGraph.Car);
            var inc = RoadGraph.Incidence(net, RoadGraph.Any);
            for (int i = 0; i < net.Edges.Count; i++)
            {
                var e = net.Edges[i];
                if (!RoadGraph.Car(e)) continue;
                var ignore = new HashSet<int>(inc[e.N0]);
                foreach (int k in inc[e.N1]) ignore.Add(k);
                if (index.Crosses(RoadGraph.Sample(e.Curve, 3f), 0.5f, ignore)) return true;
            }
            return false;
        }
    }
}
