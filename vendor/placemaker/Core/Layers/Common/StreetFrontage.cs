// Street frontage of a region: the parts of its outline that face a road (existing nets near the
// area, or roads an earlier layer of the recipe planned). Shared by
//   * "Shops along the street" (parks, plazas, campgrounds, leisure types): StreetShops lays out shop
//     regions of whole 8 m zone cells (row, speckled, clusters, facing in, alley) on this frontage and
//     hands them to the edgeZoning feature as "shops" regions (entrances keep a clear gap);
//   * the terrain feature's berms along roads;
//   * plaza bollards at road edges.
// The frontage test is the one EdgeZoningAddOn uses (adapted from Grid Road Generator, Ti4goc).
// Parking keeps its own built-in zoning (Parking/Zoning.cs), which follows the same rules.

using System;
using System.Collections.Generic;
using Placemaker.Core.Geometry;
using Placemaker.Core.Layers.Roads;
using Unity.Mathematics;

namespace Placemaker.Core.Layers.Common
{
    public static class StreetFrontage
    {
        public const string ShopsTag = "shops";
        public const float Cell = 8f;
        /// <summary>Frontage = outline within road half-width + this of a road centre line.</summary>
        public const float Tolerance = 4f;
        /// <summary>Clear gap kept each side of an entrance through a shop band.</summary>
        public const float GapHalfWidth = 5f;

        public struct RoadLine { public float2 A, B; public float HalfWidth; }

        /// <summary>Road centre lines next to the region: nets reserved by earlier layers, and existing roads.</summary>
        public static List<RoadLine> Roads(AreaInput input)
        {
            var list = new List<RoadLine>();
            if (input?.Reserved != null)
                foreach (var c in input.Reserved.Corridors)
                    if (IsRoadTag(c.Tag)) list.Add(new RoadLine { A = c.A, B = c.B, HalfWidth = c.HalfWidth });
            if (input?.Site?.NearbyNets != null)
                foreach (var nn in input.Site.NearbyNets)
                {
                    if (nn.Category != NetCategory.Road) continue;
                    var curve = new RoadCurve(nn.A, nn.B, nn.C, nn.D);
                    float hw = nn.Width > 0f ? nn.Width * 0.5f : Composer.DefaultHalfWidth(NetRole.Street);
                    float2 prev = curve.A.xz;
                    for (int i = 1; i <= 8; i++)
                    {
                        float2 p = curve.Position(i / 8f).xz;
                        list.Add(new RoadLine { A = prev, B = p, HalfWidth = hw });
                        prev = p;
                    }
                }
            return list;
        }

        public static bool IsRoadTag(string tag) =>
            tag == nameof(NetRole.Street) || tag == nameof(NetRole.Avenue) || tag == nameof(NetRole.CulDeSac)
            || tag == nameof(NetRole.Roundabout) || tag == nameof(NetRole.PerimeterRoad);

        public static bool Fronts(float2 p, List<RoadLine> roads)
        {
            foreach (var r in roads)
                if (math.sqrt(SegmentIndex.DistSq(p, r.A, r.B)) <= r.HalfWidth + Tolerance) return true;
            return false;
        }

        /// <summary>Distance from p to the nearest road-facing part of the outline (cap when none).</summary>
        public static float DistanceToFrontage(float2 p, List<(float2 a, float2 b)> frontage, float cap)
        {
            float best = cap * cap;
            foreach (var f in frontage) best = math.min(best, SegmentIndex.DistSq(p, f.a, f.b));
            return math.sqrt(best);
        }

        /// <summary>Outline pieces (≈ 4 m resolution, merged) that face a road. CCW ring: inside on the left.</summary>
        public static List<(float2 a, float2 b)> Edges(float2[] ring, List<RoadLine> roads, float minLength = 4f)
        {
            var result = new List<(float2, float2)>();
            if (roads == null || roads.Count == 0 || ring == null || ring.Length < 3) return result;
            int n = ring.Length;
            for (int i = 0; i < n; i++)
            {
                float2 a = ring[i], b = ring[(i + 1) % n];
                float len = math.distance(a, b);
                if (len < 0.5f) continue;
                int steps = math.max(1, (int)math.ceil(len / 4f));
                int runStart = -1;
                for (int k = 0; k <= steps; k++)
                {
                    bool on = k < steps && Fronts(math.lerp(a, b, (k + 0.5f) / steps), roads);
                    if (on && runStart < 0) runStart = k;
                    if (!on && runStart >= 0)
                    {
                        var p0 = math.lerp(a, b, (float)runStart / steps);
                        var p1 = math.lerp(a, b, (float)k / steps);
                        if (math.distance(p0, p1) >= minLength) result.Add((p0, p1));
                        runStart = -1;
                    }
                }
            }
            return result;
        }

        /// <summary>
        /// (s46) Outline pieces facing a road, followed along a CURVE: consecutive ring edges that face the road are chained
        /// and cut into straight chords (a chord may stray <paramref name="tolerance"/> m from the outline at most), each
        /// chord moved inward so it lies inside the region. <see cref="Edges"/> keeps every ring edge on its own, so an
        /// outline that follows a curved street in short chords (5 m vertices) yielded no piece of the minimum length at
        /// all — no frontage, and the commercial types fell back to a park (playtest s46).
        /// </summary>
        public static List<(float2 a, float2 b)> FollowEdges(float2[] ring, List<RoadLine> roads, float minLength = 4f, float tolerance = 2.5f)
        {
            var result = new List<(float2, float2)>();
            var pieces = Edges(ring, roads, 0.5f);
            if (pieces.Count == 0) return result;
            var chains = new List<List<float2>>();
            foreach (var (a, b) in pieces)
            {
                if (chains.Count > 0 && math.distance(chains[chains.Count - 1][chains[chains.Count - 1].Count - 1], a) < 0.05f) chains[chains.Count - 1].Add(b);
                else chains.Add(new List<float2> { a, b });
            }
            // The outline's start may sit inside a chain: join the last chain to the first.
            if (chains.Count > 1 && math.distance(chains[chains.Count - 1][chains[chains.Count - 1].Count - 1], chains[0][0]) < 0.05f)
            {
                chains[chains.Count - 1].AddRange(chains[0].GetRange(1, chains[0].Count - 1));
                chains.RemoveAt(0);
            }
            foreach (var chain in chains)
            {
                int last = chain.Count - 1;
                if (last > 3 && math.distance(chain[0], chain[last]) < 0.05f)
                {
                    // A frontage all round the outline: cut it at the point farthest from its start.
                    int far = 1; float fd = 0f;
                    for (int k = 1; k < last; k++) { float dd = math.distance(chain[0], chain[k]); if (dd > fd) { fd = dd; far = k; } }
                    Chords(chain, 0, far, minLength, tolerance, result);
                    Chords(chain, far, last, minLength, tolerance, result);
                }
                else Chords(chain, 0, last, minLength, tolerance, result);
            }
            return result;
        }

        private static void Chords(List<float2> pts, int i, int j, float minLength, float tolerance, List<(float2, float2)> result)
        {
            float2 a = pts[i], b = pts[j];
            float len = math.distance(a, b);
            if (len < 0.01f) return;
            var d = (b - a) / len; var n = new float2(-d.y, d.x);   // inside is on the left
            float inward = 0f, outward = 0f; int worst = -1; float worstAbs = 0f;
            for (int k = i + 1; k < j; k++)
            {
                float s = math.dot(pts[k] - a, n);
                inward = math.max(inward, s); outward = math.max(outward, -s);
                if (math.abs(s) > worstAbs) { worstAbs = math.abs(s); worst = k; }
            }
            if (worst >= 0 && inward + outward > tolerance)
            {
                Chords(pts, i, worst, minLength, tolerance, result);
                Chords(pts, worst, j, minLength, tolerance, result);
                return;
            }
            if (len < minLength) return;
            result.Add((a + n * inward, b + n * inward));
        }
    }
}
