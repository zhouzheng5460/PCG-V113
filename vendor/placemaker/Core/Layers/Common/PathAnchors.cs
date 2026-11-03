// Candidate spots beside the paths a parent layer left (reserved Pathway corridors), ranked by how
// close they are to where people enter (path ends on the outline). Used by features that belong
// "on the way": restrooms, shops/kiosks, food stands. Without paths, spots along the outline.

using System.Collections.Generic;
using Unity.Mathematics;

namespace Placemaker.Core.Layers.Common
{
    public struct PathAnchor
    {
        public float2 Position;
        /// <summary>Closest point on the path (the object faces it).</summary>
        public float2 Toward;
        /// <summary>Path direction at the anchor.</summary>
        public float2 Tangent;
        /// <summary>Distance to the nearest entrance (path end on the outline); large when unknown.</summary>
        public float GateDistance;
    }

    public static class PathAnchors
    {
        /// <summary>Path ends on (within 1.5 m of) the region outline: where people come in.</summary>
        public static List<float2> Entrances(Occupancy occ)
        {
            var r = new List<float2>();
            var region = occ.LayerRegion;
            foreach (var s in occ.Paths.Segments)
                foreach (var end in new[] { s.A, s.B })
                {
                    if (region.BoundaryDistance(end, 2f) > 1.5f) continue;
                    bool near = false;
                    foreach (var q in r) if (math.distancesq(q, end) < 25f) { near = true; break; }
                    if (!near) r.Add(end);
                }
            return r;
        }

        /// <summary>
        /// Spots for a disc of <paramref name="radius"/> beside the paths (both sides, every
        /// <paramref name="step"/> m), free in the occupancy, sorted by entrance distance (nearest first).
        /// Falls back to spots just inside the outline when the region has no paths.
        /// </summary>
        public static List<PathAnchor> Beside(Occupancy occ, float radius, float step, float gapToPath = 1f)
        {
            var result = new List<PathAnchor>();
            var region = occ.LayerRegion;
            var gates = Entrances(occ);
            if (occ.Paths.Count > 0)
            {
                foreach (var s in occ.Paths.Segments)
                {
                    float len = math.distance(s.A, s.B);
                    if (len < 0.5f) continue;
                    var t = (s.B - s.A) / len;
                    var n = new float2(-t.y, t.x);
                    // Short segments (sampled curves) still get their midpoint: otherwise a meandering path has no anchors.
                    for (float along = math.min(step * 0.5f, len * 0.5f); along < len; along += step)
                    {
                        var onPath = s.A + t * along;
                        for (int side = -1; side <= 1; side += 2)
                        {
                            var p = onPath + n * side * (s.HalfWidth + gapToPath + radius);
                            if (!occ.Free(p, radius, radius, 0.3f)) continue;   // the whole footprint inside the area
                            result.Add(new PathAnchor { Position = p, Toward = onPath, Tangent = t, GateDistance = Nearest(gates, p) });
                        }
                    }
                }
            }
            else
            {
                float per = region.Perimeter;
                for (float along = 0; along < per; along += step)
                {
                    var b = region.PointAt(along, out var tan, out var inward, out _);
                    var p = b + inward * (radius + 1.5f);
                    if (!occ.Free(p, radius, 0.5f, 0.3f)) continue;
                    result.Add(new PathAnchor { Position = p, Toward = b, Tangent = tan, GateDistance = 1e6f });
                }
            }
            result.Sort((x, y) => x.GateDistance != y.GateDistance ? x.GateDistance.CompareTo(y.GateDistance)
                : x.Position.x != y.Position.x ? x.Position.x.CompareTo(y.Position.x) : x.Position.y.CompareTo(y.Position.y));
            return result;
        }

        private static float Nearest(List<float2> pts, float2 p)
        {
            float best = 1e6f;
            foreach (var q in pts) best = math.min(best, math.distance(p, q));
            return best;
        }

        /// <summary>
        /// Greedy spread pick: the first anchor (nearest an entrance) then each next one at least
        /// <paramref name="minDistance"/> from all chosen. When <paramref name="nearEntrancesFirst"/> is
        /// false, the pick prefers anchors far from those already chosen (even spread).
        /// </summary>
        public static List<PathAnchor> Pick(List<PathAnchor> anchors, int count, float minDistance, bool nearEntrancesFirst)
        {
            var chosen = new List<PathAnchor>();
            if (anchors.Count == 0 || count <= 0) return chosen;
            if (nearEntrancesFirst)
            {
                foreach (var a in anchors)
                {
                    if (chosen.Count >= count) break;
                    bool ok = true;
                    foreach (var c in chosen) if (math.distancesq(c.Position, a.Position) < minDistance * minDistance) { ok = false; break; }
                    if (ok) chosen.Add(a);
                }
                return chosen;
            }
            chosen.Add(anchors[0]);
            while (chosen.Count < count)
            {
                int best = -1; float bestD = minDistance;
                for (int i = 0; i < anchors.Count; i++)
                {
                    float d = float.MaxValue;
                    foreach (var c in chosen) d = math.min(d, math.distance(c.Position, anchors[i].Position));
                    if (d > bestD) { bestD = d; best = i; }
                }
                if (best < 0) break;
                chosen.Add(anchors[best]);
            }
            return chosen;
        }
    }
}
