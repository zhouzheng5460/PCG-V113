// Placemaker — sloped terraform patches (s20, GPL-3.0).
//
// Playtests 28–34 ("blocky", "terraced", "pyramids"): every terraform polygon used to be levelled to ONE height,
// so a sculpted bank was a staircase of 6–12 m flat cells. A sloped patch (PlannedTerraform.Sloped) carries its
// target in the ring's vertex heights instead; the target at a point is the mean-value interpolation of those
// heights (Floater 2003): exact at the vertices, linear along every edge, smooth inside. Neighbouring grid quads
// share their corner heights, so the sculpted ground is continuous across cells. Pure, used by the build raster,
// TerrainField, site safety cuts, the overlay and the gallery.

using System;
using System.Collections.Generic;
using Unity.Mathematics;

namespace Placemaker.Core.Build
{
    public static class TerraformSurface
    {
        /// <summary>Target ground of a terraform item at <paramref name="p"/> (flat items: TargetHeight).</summary>
        public static float TargetAt(PlannedTerraform t, float2 p) =>
            t != null && t.Sloped && t.Ring != null && t.Ring.Length >= 3 ? At(t.Ring, p) : t?.TargetHeight ?? 0f;

        [ThreadStatic] private static float2[] _d;
        [ThreadStatic] private static float[] _r, _t;

        /// <summary>Mean-value interpolation of the ring's y at p (p inside or on the ring; outside it extrapolates smoothly).</summary>
        public static float At(float3[] ring, float2 p)
        {
            int n = ring.Length;
            if (n == 0) return 0f;
            if (n < 3) return ring[0].y;
            if (_d == null || _d.Length < n) { _d = new float2[n + 8]; _r = new float[n + 8]; _t = new float[n + 8]; }
            var d = _d; var r = _r; var t = _t;
            for (int i = 0; i < n; i++)
            {
                d[i] = ring[i].xz - p;
                r[i] = math.length(d[i]);
                if (r[i] < 1e-4f) return ring[i].y;
            }
            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;
                float cross = d[i].x * d[j].y - d[i].y * d[j].x;
                float dot = math.dot(d[i], d[j]);
                // On the edge (i, j): linear along it.
                if (math.abs(cross) < 1e-5f * r[i] * r[j] && dot < 0f)
                {
                    float u = r[i] / (r[i] + r[j]);
                    return math.lerp(ring[i].y, ring[j].y, u);
                }
                float den = r[i] * r[j] + dot;
                // Signed tan(α/2) = cross / (r_i r_j + dot); stable for |α| < π.
                t[i] = den > 1e-9f ? cross / den : (r[i] * r[j] - dot) / (cross == 0f ? 1e-9f : cross);
            }
            double sw = 0, sh = 0;
            for (int i = 0; i < n; i++)
            {
                int k = (i - 1 + n) % n;
                double w = (t[k] + t[i]) / r[i];
                sw += w; sh += w * ring[i].y;
            }
            if (math.abs(sw) < 1e-12) { float m = 0; foreach (var v in ring) m += v.y; return m / n; }
            return (float)(sh / sw);
        }

        /// <summary>Ring vertex heights (y) of a sloped item, else null (flat item).</summary>
        public static float[] VertexHeights(PlannedTerraform t)
        {
            if (t == null || !t.Sloped || t.Ring == null || t.Ring.Length < 3) return null;
            var h = new float[t.Ring.Length];
            for (int i = 0; i < h.Length; i++) h[i] = t.Ring[i].y;
            return h;
        }

        /// <summary>
        /// Outline of a set of terraform items: edges not shared by two items (quantised to 1 cm). A sculpted grid of
        /// sloped quads then draws as its outer boundary in the preview instead of hundreds of dashed squares.
        /// </summary>
        public static List<(float3 a, float3 b)> BoundaryEdges(IEnumerable<PlannedTerraform> items)
        {
            var count = new Dictionary<(long, long, long, long), int>();
            var edges = new List<(float3 a, float3 b, (long, long, long, long) key)>();
            long Q(float v) => (long)math.round(v * 100f);
            foreach (var t in items)
            {
                if (t?.Ring == null || t.Ring.Length < 3) continue;
                for (int i = 0; i < t.Ring.Length; i++)
                {
                    var a = t.Ring[i]; var b = t.Ring[(i + 1) % t.Ring.Length];
                    long ax = Q(a.x), az = Q(a.z), bx = Q(b.x), bz = Q(b.z);
                    var key = ax < bx || (ax == bx && az <= bz) ? (ax, az, bx, bz) : (bx, bz, ax, az);
                    count[key] = count.TryGetValue(key, out int c) ? c + 1 : 1;
                    edges.Add((a, b, key));
                }
            }
            var result = new List<(float3, float3)>();
            foreach (var e in edges) if (count[e.key] == 1) result.Add((e.a, e.b));
            return result;
        }
    }
}
