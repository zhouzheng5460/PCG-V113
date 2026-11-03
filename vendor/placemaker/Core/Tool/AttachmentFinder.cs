// Placemaker — where existing roads meet a drawn or painted outline.
//
// Block and perimeter selections know their bounding roads exactly (they were traced on the
// road graph). Drawn and painted outlines only have geometry, so this computes the same
// BoundaryAttachment list from the NearbyNet snapshot: roads crossing the outline (entrances,
// connections) and roads running along an edge (frontage for edge zoning, parking access).

using System.Collections.Generic;
using Unity.Mathematics;

namespace Placemaker.Core.Tool
{
    public static class AttachmentFinder
    {
        /// <summary>Extra distance beyond the half road width that still counts as "along" the outline.</summary>
        public const float AlongTolerance = 3f;
        /// <summary>Minimum parallel overlap for an along-edge attachment (metres; also capped at 40 % of the edge).</summary>
        public const float MinAlongLength = 12f;

        public static List<BoundaryAttachment> Find(float3[] ring, IReadOnlyList<NearbyNet> nets)
        {
            var result = new List<BoundaryAttachment>();
            if (ring == null || ring.Length < 3 || nets == null) return result;
            int n = ring.Length;
            foreach (var net in nets)
            {
                var samples = Sample(net, out var ts);
                if (samples.Count < 2) continue;
                float width = math.max(0f, net.Width);

                // Crossings
                var crossings = new List<float3>();
                for (int i = 0; i < n; i++)
                {
                    float2 a = ring[i].xz, b = ring[(i + 1) % n].xz;
                    for (int k = 0; k + 1 < samples.Count; k++)
                    {
                        float2 c = samples[k].xz, d = samples[k + 1].xz;
                        if (!SegmentIntersection(a, b, c, d, out float s, out float u)) continue;
                        float3 at = math.lerp(ring[i], ring[(i + 1) % n], s);
                        bool dup = false;
                        foreach (var p in crossings) if (math.distance(p.xz, at.xz) < 1f) { dup = true; break; }
                        if (dup) continue;
                        crossings.Add(at);
                        float2 dir = math.normalizesafe(d - c);
                        result.Add(new BoundaryAttachment
                        {
                            Kind = AttachmentKind.RoadCrossing,
                            ExternalId = net.ExternalId,
                            ExternalT = math.lerp(ts[k], ts[k + 1], u),
                            Position = at,
                            Direction = new float3(dir.x, 0, dir.y),
                            RoadWidth = width,
                            RingSegment = i,
                            RingT = s,
                        });
                    }
                }

                // Along an edge
                float tol = width * 0.5f + AlongTolerance;
                for (int i = 0; i < n; i++)
                {
                    float2 a = ring[i].xz, b = ring[(i + 1) % n].xz;
                    float2 ab = b - a;
                    float len = math.length(ab);
                    if (len < 1f) continue;
                    float2 dirAB = ab / len;
                    float sMin = float.MaxValue, sMax = float.MinValue;
                    for (int k = 0; k < samples.Count; k++)
                    {
                        float2 p = samples[k].xz;
                        float s = math.dot(p - a, dirAB) / len;
                        if (s < 0f || s > 1f) continue;
                        float dist = math.abs(math.dot(p - a, new float2(-dirAB.y, dirAB.x)));
                        if (dist > tol) continue;
                        int k0 = math.max(0, k - 1), k1 = math.min(samples.Count - 1, k + 1);
                        float2 tangent = math.normalizesafe(samples[k1].xz - samples[k0].xz);
                        if (math.abs(math.dot(tangent, dirAB)) < 0.95f) continue;
                        sMin = math.min(sMin, s);
                        sMax = math.max(sMax, s);
                    }
                    if (sMax < sMin) continue;
                    float overlap = (sMax - sMin) * len;
                    if (overlap < math.min(MinAlongLength, 0.4f * len)) continue;
                    float sMid = (sMin + sMax) * 0.5f;
                    float2 mid = a + ab * sMid;
                    int best = 0;
                    float bestD = float.MaxValue;
                    for (int k = 0; k < samples.Count; k++)
                    {
                        float d = math.distancesq(samples[k].xz, mid);
                        if (d < bestD) { bestD = d; best = k; }
                    }
                    int b0 = math.max(0, best - 1), b1 = math.min(samples.Count - 1, best + 1);
                    float2 tdir = math.normalizesafe(samples[b1].xz - samples[b0].xz);
                    result.Add(new BoundaryAttachment
                    {
                        Kind = AttachmentKind.RoadAlongEdge,
                        ExternalId = net.ExternalId,
                        ExternalT = ts[best],
                        Position = math.lerp(ring[i], ring[(i + 1) % n], sMid),
                        Direction = new float3(tdir.x, 0, tdir.y),
                        RoadWidth = width,
                        RingSegment = i,
                        RingT = sMid,
                    });
                }
            }
            return result;
        }

        private static List<float3> Sample(NearbyNet net, out List<float> ts)
        {
            ts = new List<float>();
            var pts = new List<float3>();
            if (!math.all(math.isfinite(net.A)) || !math.all(math.isfinite(net.D))) return pts;
            float approx = math.distance(net.A, net.B) + math.distance(net.B, net.C) + math.distance(net.C, net.D);
            int steps = math.clamp((int)math.ceil(approx / 4f), 1, 64);
            for (int i = 0; i <= steps; i++)
            {
                float t = (float)i / steps;
                ts.Add(t);
                pts.Add(Composer.Bezier(net.A, net.B, net.C, net.D, t));
            }
            return pts;
        }

        /// <summary>Proper intersection of segments ab and cd (touching endpoints count once, at s in [0,1)).</summary>
        public static bool SegmentIntersection(float2 a, float2 b, float2 c, float2 d, out float s, out float u)
        {
            s = u = 0;
            float2 r = b - a, q = d - c;
            float den = r.x * q.y - r.y * q.x;
            if (math.abs(den) < 1e-9f) return false;
            float2 w = c - a;
            s = (w.x * q.y - w.y * q.x) / den;
            u = (w.x * r.y - w.y * r.x) / den;
            return s >= 0f && s < 1f && u >= 0f && u <= 1f;
        }
    }
}
