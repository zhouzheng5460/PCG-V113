// Placemaker — robust rings for the owner lot (GPL-3.0).
//
// Playtest: "the game rejected the owner lot; building without it" → that build could not be selected
// or bulldozed as one thing. Lots validate with min node distance 4 m (snap 8 × 0.5), no spikes/necks,
// simple (RESEARCH-NOTES-ADDENDUM "Area shape validation"). Candidates get progressively simpler; the
// build retries with the next level when the game still rejects the shape.

using System.Collections.Generic;
using Placemaker.Core.Geometry;
using Unity.Mathematics;

namespace Placemaker.Core.Build
{
    public static class OwnerRings
    {
        public const int Levels = 4;

        /// <summary>
        /// Owner lot ring at <paramref name="level"/>: 0 = outline repaired, 1 = simplified (3 m) and
        /// repaired, 2 = convex hull, 3 = minimum-area bounding rectangle of the hull inset by 0.5 m.
        /// Every returned ring passes AreaRings.IsValid(lot distance); a level that cannot produce one
        /// falls through to the next. Null when no level ≥ <paramref name="level"/> works.
        /// </summary>
        public static float2[] Candidate(IReadOnlyList<float2> outline, int level)
        {
            if (outline == null || outline.Count < 3) return null;
            for (int l = math.max(0, level); l < Levels; l++)
            {
                float2[] ring = null;
                switch (l)
                {
                    case 0: ring = AreaRings.MakeValid(outline, AreaRings.LotMinNodeDistance); break;
                    case 1: ring = AreaRings.MakeValid(Poly.Simplify(outline, 3f), AreaRings.LotMinNodeDistance); break;
                    case 2: ring = AreaRings.MakeValid(AreaRings.ConvexHull(outline), AreaRings.LotMinNodeDistance); break;
                    case 3: ring = AreaRings.MakeValid(BoundingRect(AreaRings.ConvexHull(outline), 0.5f), AreaRings.LotMinNodeDistance); break;
                }
                if (ring != null && ring.Length >= 3 && AreaRings.IsValid(ring, AreaRings.LotMinNodeDistance + AreaRings.Margin)) return ring;
            }
            return null;
        }

        /// <summary>Minimum-area rectangle around a convex hull (rotating edges), shrunk by <paramref name="inset"/> when large enough.</summary>
        public static float2[] BoundingRect(float2[] hull, float inset)
        {
            if (hull == null || hull.Length < 3) return null;
            float best = float.MaxValue;
            float2[] result = null;
            for (int i = 0; i < hull.Length; i++)
            {
                var e = hull[(i + 1) % hull.Length] - hull[i];
                if (math.lengthsq(e) < 1e-6f) continue;
                var u = math.normalize(e);
                var v = new float2(-u.y, u.x);
                float minU = float.MaxValue, maxU = float.MinValue, minV = float.MaxValue, maxV = float.MinValue;
                foreach (var p in hull)
                {
                    float a = math.dot(p, u), b = math.dot(p, v);
                    minU = math.min(minU, a); maxU = math.max(maxU, a); minV = math.min(minV, b); maxV = math.max(maxV, b);
                }
                float area = (maxU - minU) * (maxV - minV);
                if (area >= best) continue;
                best = area;
                float iu = (maxU - minU) > 4f * inset + 8f ? inset : 0f, iv = (maxV - minV) > 4f * inset + 8f ? inset : 0f;
                minU += iu; maxU -= iu; minV += iv; maxV -= iv;
                result = new[] { u * minU + v * minV, u * maxU + v * minV, u * maxU + v * maxV, u * minU + v * maxV };
            }
            return result;
        }
    }
}
