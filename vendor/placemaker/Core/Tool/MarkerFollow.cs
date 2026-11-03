// Placemaker — markers follow the outline (FEEDBACK-2 #2).
//
// When the outline changes (point/edge drag, insert/delete point, block or paint change, road
// clipping) every marker is moved onto the new outline:
//   * same vertex count and at most two vertices moved (a point or edge drag): the marker keeps its
//     RingSegment/RingT, so it slides with the edited edge;
//   * otherwise: the closest point of the new outline to its old position.
// Markers of steps not marked as edited (none since automatic entrances were removed, FEEDBACK-4b)
// follow too, and are dropped when they end up far from every road. Player-placed markers are never
// dropped. Pure: the tool calls it before committing the undo
// step, so the snapshot holds outline and markers together.

using System;
using System.Collections.Generic;
using Unity.Mathematics;

namespace Placemaker.Core.Tool
{
    public static class MarkerFollow
    {
        /// <summary>Automatic markers farther than this from the edge of every road are dropped.</summary>
        public const float MaxAutoRoadDistance = 20f;

        /// <summary>
        /// Moves every marker of every step onto <paramref name="newRing"/>. <paramref name="edited"/>: step
        /// paths whose markers the player placed (kept whatever happens). <paramref name="nets"/> (lazy, may
        /// be null): roads used to drop automatic markers that ended up far from any road.
        /// Returns true when any marker changed.
        /// </summary>
        public static bool Reproject(Recipe recipe, float3[] oldRing, float3[] newRing, ICollection<string> edited,
            Func<IReadOnlyList<NearbyNet>> nets = null)
        {
            if (recipe?.Root == null || newRing == null || newRing.Length < 3) return false;
            bool sameTopology = SameTopology(oldRing, newRing);
            IReadOnlyList<NearbyNet> netList = null;
            bool netsLoaded = false;
            bool changed = false;
            foreach (var kv in RecipePaths.Walk(recipe))
            {
                var step = kv.Value;
                if (step == null || step.Markers.Count == 0) continue;
                bool auto = edited == null || !edited.Contains(kv.Key);
                var list = new List<EdgeMarker>(step.Markers.Count);
                bool stepChanged = false;
                foreach (var m in step.Markers)
                {
                    var moved = Project(m, newRing, sameTopology);
                    if (auto)
                    {
                        if (!netsLoaded) { netsLoaded = true; try { netList = nets?.Invoke(); } catch (Exception) { netList = null; } }
                        if (netList != null && netList.Count > 0 && DistanceToRoads(moved.Position.xz, netList) > MaxAutoRoadDistance)
                        {
                            stepChanged = true;
                            continue;
                        }
                    }
                    if (moved.RingSegment != m.RingSegment || math.abs(moved.RingT - m.RingT) > 1e-5f || math.distancesq(moved.Position, m.Position) > 1e-6f)
                        stepChanged = true;
                    list.Add(moved);
                }
                if (!stepChanged) continue;
                step.Markers = list;
                changed = true;
            }
            return changed;
        }

        public static EdgeMarker Project(EdgeMarker m, float3[] ring, bool keepSegment)
        {
            int n = ring.Length;
            var r = m;
            if (keepSegment && m.RingSegment >= 0 && m.RingSegment < n)
            {
                r.RingT = math.saturate(m.RingT);
                r.Position = math.lerp(ring[m.RingSegment], ring[(m.RingSegment + 1) % n], r.RingT);
                return r;
            }
            if (Composer.ClosestSegment(ring, m.Position.xz, out int seg, out float t, out _))
            {
                r.RingSegment = seg;
                r.RingT = t;
                r.Position = math.lerp(ring[seg], ring[(seg + 1) % n], t);
            }
            return r;
        }

        /// <summary>Same vertex count and at most two vertices moved — a point or edge drag of the same outline.</summary>
        public static bool SameTopology(float3[] a, float3[] b)
        {
            if (a == null || b == null || a.Length != b.Length || a.Length < 3) return false;
            int moved = 0;
            for (int i = 0; i < a.Length; i++)
                if (math.distancesq(a[i].xz, b[i].xz) > 1e-6f && ++moved > 2) return false;
            return true;
        }

        /// <summary>Distance from p to the nearest road edge (0 when on a road).</summary>
        public static float DistanceToRoads(float2 p, IReadOnlyList<NearbyNet> nets)
        {
            float best = float.MaxValue;
            foreach (var net in nets)
            {
                var bez = new Geometry.CubicBezier(net.A, net.B, net.C, net.D);
                float t = bez.ClosestParameterXZ(p);
                float d = math.distance(bez.Position(t).xz, p) - math.max(0f, net.Width) * 0.5f;
                if (d < best) best = d;
            }
            return math.max(0f, best);
        }
    }
}
