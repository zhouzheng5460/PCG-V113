// Entrance suggestions on adjacent roads (new for Placemaker; PLT asked the player to place every
// entrance by hand — "Automatic entrances are not available yet").
//
// Samples the outline every ~2 m, casts outward to the nearest road (same 40 m rule the build uses)
// and scores each spot: close to the road, a minor road rather than an arterial, a two-way road,
// meeting the road squarely, away from road junctions and lot corners. Small lots get one two-way
// entrance. Large lots with a long frontage on the same street get an entry and an exit far apart
// (cars circulate instead of turning at one gap); a large lot whose second-best spot is on another
// street gets a second two-way entrance there. Alleys are never suggested (a junction changes the
// street — only on the player's request). The tool applies these as real, editable markers when the
// player has placed none. Pure and fast (< 5 ms for typical inputs).

using System;
using System.Collections.Generic;
using Unity.Mathematics;

namespace Placemaker.Core.Layers.Parking
{
    internal static class EntranceSuggest
    {
        private struct Candidate
        {
            public int Edge; public double T; public double2 P; public double Score; public float Y; public int Road;
        }

        public static List<EdgeMarker> Suggest(AreaInput input, ParkingSettings s)
        {
            var result = new List<EdgeMarker>();
            var ring3 = input?.Shape?.Ring;
            if (ring3 == null || ring3.Length < 3) return result;
            int n = ring3.Length;
            var ring = new List<double2>(n);
            foreach (var p in ring3) ring.Add(new double2(p.x, p.z));
            bool ccw = PGeo.SignedArea(ring) >= 0;
            double perimeter = PGeo.Perimeter(ring), area = PGeo.Area(ring);
            var roads = Roads.From(input.Site);

            double margin = s.Aisle / 2 + s.StallWidth + 1;
            double step = Math.Max(2.0, perimeter / 400);
            var cands = new List<Candidate>();
            for (int i = 0; i < n; i++)
            {
                var a = ring[i]; var b = ring[(i + 1) % n];
                double len = PGeo.Len(b - a);
                if (len < 2 * margin) continue;
                var tan = (b - a) / len;
                var inward = ccw ? PGeo.Perp(tan) : -PGeo.Perp(tan);
                for (double along = margin; along <= len - margin + 1e-9; along += step)
                {
                    var p = a + tan * along;
                    double score; int roadIdx = -1;
                    if (roads.Count > 0)
                    {
                        if (!Roads.Cast(roads, p, -inward, Roads.SearchDistance, out var hit)) continue;
                        var road = roads[hit.Road]; roadIdx = hit.Road;
                        double gap = hit.Distance - road.Width / 2;
                        if (gap > 15 || gap < -road.Width / 2) continue;
                        score = Math.Max(0, gap);
                        if (road.Width > 20) score += (road.Width - 20) * 0.6;
                        if (road.OneWay) score += 4;
                        score += (1 - Math.Abs(PGeo.Dot(hit.Direction, tan))) * 8;
                        double toEnd = Math.Min(hit.T, 1 - hit.T) * road.Length;
                        if (toEnd < 15) score += (15 - toEnd) * 0.8;
                    }
                    else
                    {
                        // No road data (e.g. drawn in the open): fall back to boundary attachments.
                        if (!NearAttachment(input.Shape.Attachments, p, out double d)) continue;
                        score = d;
                    }
                    double corner = Math.Min(along, len - along);
                    if (corner < 10) score += (10 - corner) * 0.3;
                    score += Math.Abs(along - len / 2) / len * 2;
                    float y = math.lerp(ring3[i].y, ring3[(i + 1) % n].y, (float)(along / len));
                    cands.Add(new Candidate { Edge = i, T = along / len, P = p, Score = score, Y = y, Road = roadIdx });
                }
            }
            if (cands.Count == 0) return result;
            cands.Sort((x, y) => x.Score != y.Score ? x.Score.CompareTo(y.Score) : x.Edge != y.Edge ? x.Edge.CompareTo(y.Edge) : x.T.CompareTo(y.T));
            var first = cands[0];
            if (area > 6000 || perimeter > 320)
            {
                double minApart = Math.Max(40, perimeter * 0.15);
                // entry + exit on the same street (long frontage)
                Candidate? same = null; double sameScore = double.MaxValue;
                Candidate? other = null; double otherScore = double.MaxValue;
                foreach (var c in cands)
                {
                    if (PGeo.Len(c.P - first.P) < minApart) continue;
                    if (first.Road >= 0 && c.Road == first.Road)
                    {
                        if (c.Score < sameScore) { sameScore = c.Score; same = c; }
                    }
                    else
                    {
                        double sc = c.Score + (c.Edge == first.Edge ? 3 : 0);
                        if (sc < otherScore) { otherScore = sc; other = c; }
                    }
                }
                if (same.HasValue)
                {
                    result.Add(Marker(first, ParkingIds.KindEntry));
                    result.Add(Marker(same.Value, ParkingIds.KindExit));
                    return result;
                }
                result.Add(Marker(first));
                if (other.HasValue) result.Add(Marker(other.Value));
                return result;
            }
            result.Add(Marker(first));
            return result;
        }

        private static EdgeMarker Marker(Candidate c, string kind = ParkingIds.KindEntrance) => new EdgeMarker
        {
            Kind = kind, RingSegment = c.Edge, RingT = (float)c.T,
            Position = new float3((float)c.P.x, c.Y, (float)c.P.y),
        };

        private static bool NearAttachment(List<BoundaryAttachment> atts, double2 p, out double dist)
        {
            dist = double.MaxValue;
            if (atts == null) return false;
            foreach (var a in atts)
            {
                if (a.Kind != AttachmentKind.RoadAlongEdge) continue;
                double d = PGeo.Len(new double2(a.Position.x, a.Position.z) - p);
                if (d < dist) dist = d;
            }
            return dist < 30;
        }
    }
}
