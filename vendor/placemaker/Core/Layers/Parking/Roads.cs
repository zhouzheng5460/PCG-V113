// Adjacent-road lookup for entrances. Ported in spirit from ParkingLotTool (KruemelGames, GPL-3.0)
// Tools/ParkingLotNetBuilder.cs: "a ray from the polygon edge in the outward direction finds the
// road within 40 m"; the alley course then runs from the road centreline (split at t), while a
// LocalConnect path ends at the road edge, not its centre (Tools/ParkingLotNetDefinitions.cs).

using System;
using System.Collections.Generic;
using Unity.Mathematics;

namespace Placemaker.Core.Layers.Parking
{
    internal sealed class RoadLine
    {
        public long Id;
        public double Width;
        public bool OneWay;
        public double2[] Pts;   // samples along the Bézier
        /// <summary>(s29) Road surface height at each sample (the captured curve, not the raw terrain).</summary>
        public double[] H;
        public double Length;
    }

    internal struct RoadHit
    {
        public int Road;
        public double2 Point;      // on the road centreline
        public double T;           // Bézier parameter
        public double Distance;    // from the ray origin to the centreline
        public double2 Direction;  // road tangent at the hit
    }

    internal static class Roads
    {
        public const int Samples = 16;
        /// <summary>PLT NetBuilder: the outward ray finds a road within 40 m.</summary>
        public const double SearchDistance = 40;

        public static List<RoadLine> From(SiteContext site)
        {
            var list = new List<RoadLine>();
            if (site?.NearbyNets == null) return list;
            foreach (var n in site.NearbyNets)
            {
                if (n.Category != NetCategory.Road) continue;
                var pts = new double2[Samples + 1];
                var hs = new double[Samples + 1];
                double len = 0;
                for (int i = 0; i <= Samples; i++)
                {
                    var p = Composer.Bezier(n.A, n.B, n.C, n.D, i / (float)Samples);
                    pts[i] = new double2(p.x, p.z);
                    hs[i] = p.y;
                    if (i > 0) len += PGeo.Len(pts[i] - pts[i - 1]);
                }
                if (len < 0.5) continue;
                list.Add(new RoadLine { Id = n.ExternalId, Width = Math.Max(0, n.Width), OneWay = n.IsOneWay, Pts = pts, H = hs, Length = len });
            }
            return list;
        }

        /// <summary>First road centreline hit by the ray origin + dir·s, 0 &lt; s ≤ maxDist.</summary>
        public static bool Cast(List<RoadLine> roads, double2 origin, double2 dir, double maxDist, out RoadHit hit)
        {
            hit = default;
            double best = maxDist;
            bool found = false;
            var far = origin + dir;
            for (int r = 0; r < roads.Count; r++)
            {
                var pts = roads[r].Pts;
                for (int i = 0; i + 1 < pts.Length; i++)
                {
                    if (!PGeo.LineLine(origin, far, pts[i], pts[i + 1], out double s, out double u)) continue;
                    if (u < -1e-9 || u > 1 + 1e-9 || s <= 1e-6 || s > best) continue;
                    best = s; found = true;
                    hit = new RoadHit
                    {
                        Road = r, Point = origin + dir * s, T = (i + Math.Max(0, Math.Min(1, u))) / (pts.Length - 1),
                        Distance = s, Direction = PGeo.Norm(pts[i + 1] - pts[i]),
                    };
                }
            }
            return found;
        }

        /// <summary>Nearest road centreline point within maxDist.</summary>
        /// <summary>(s29) Height of the road surface at a hit (interpolated along the samples).</summary>
        public static double HeightAt(List<RoadLine> roads, RoadHit hit)
        {
            var r = roads[hit.Road];
            if (r.H == null || r.H.Length < 2) return double.NaN;
            double f = Math.Max(0, Math.Min(1, hit.T)) * (r.H.Length - 1);
            int i = Math.Min(r.H.Length - 2, (int)Math.Floor(f));
            return r.H[i] + (r.H[i + 1] - r.H[i]) * (f - i);
        }

        public static bool Nearest(List<RoadLine> roads, double2 p, double maxDist, out RoadHit hit)
        {
            hit = default;
            double best = maxDist;
            bool found = false;
            for (int r = 0; r < roads.Count; r++)
            {
                var pts = roads[r].Pts;
                for (int i = 0; i + 1 < pts.Length; i++)
                {
                    var q = PGeo.ClosestOnSeg(p, pts[i], pts[i + 1], out double u);
                    double d = PGeo.Len(q - p);
                    if (d >= best) continue;
                    best = d; found = true;
                    hit = new RoadHit { Road = r, Point = q, T = (i + u) / (pts.Length - 1), Distance = d, Direction = PGeo.Norm(pts[i + 1] - pts[i]) };
                }
            }
            return found;
        }

        /// <summary>Road for an entrance at boundary point p with inward normal n: outward ray first, nearest as fallback.</summary>
        public static bool ForEntrance(List<RoadLine> roads, double2 p, double2 inward, out RoadHit hit, double maxDist = SearchDistance)
        {
            if (Cast(roads, p, -inward, maxDist, out hit)) return true;
            if (Nearest(roads, p, 12, out hit))
            {
                // only accept a road on the outside of the edge
                return PGeo.Dot(hit.Point - p, inward) <= 0.5;
            }
            return false;
        }
    }
}
