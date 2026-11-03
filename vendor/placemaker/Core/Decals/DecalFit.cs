// Placemaker — turn resolved decals so their long side lies the way the plan meant (s21). GPL-3.0.
//
// Plans place decorative decals by intent: Yaw = the direction the decal's long axis should follow
// (path tangent, bay axis, shoreline, wheel line). Which prefab is used is decided later by the
// automatic resolver, and packs disagree on axes (a 2×6 puddle band is long in Z, a 10×5 tar patch or
// an 8×4 seaweed line is long in X). Run once per plan right after automatic assets are resolved (the
// game's AssetCatalogSystem.ResolveAutomatic, the gallery): a decal whose prefab is clearly longer in X
// is turned 90° so its long axis takes the planned direction. Round decals are left alone.

using System.Collections.Generic;
using Unity.Mathematics;

namespace Placemaker.Core.Decals
{
    public static class DecalFit
    {
        /// <summary>Long/short ratio from which a decal counts as elongated.</summary>
        public const float Elongated = 1.15f;

        /// <summary>Orients every decorative decal of the plan (long-axis convention). Returns how many were turned.</summary>
        public static int Orient(ComposedPlan plan, IReadOnlyDictionary<string, AssetInfo> assets)
        {
            if (plan?.Layers == null || assets == null) return 0;
            int turned = 0;
            foreach (var l in plan.Layers)
            {
                if (l?.Objects == null) continue;
                foreach (var o in l.Objects)
                {
                    if (o == null || o.Role != ObjectRole.Decal || !DecalPools.FollowsLongAxis(o.Category)) continue;
                    if (string.IsNullOrEmpty(o.Asset.Id) || !assets.TryGetValue(o.Asset.Id, out var info)) continue;
                    if (NeedsQuarterTurn(info.BoundsMin, info.BoundsMax))
                    {
                        o.Yaw = Wrap(o.Yaw + math.PI * 0.5f);
                        turned++;
                    }
                }
            }
            return turned;
        }

        /// <summary>True when the prefab's footprint is clearly longer in X than in Z.</summary>
        public static bool NeedsQuarterTurn(float3 min, float3 max)
        {
            float sx = max.x - min.x, sz = max.z - min.z;
            return sx > 0.01f && sz > 0.01f && sx > sz * Elongated;
        }

        /// <summary>World direction of a decal's long axis for a yaw and prefab bounds (after DecalFit).</summary>
        public static float2 LongAxis(float yaw, float3 min, float3 max)
        {
            math.sincos(yaw, out float s, out float c);
            float sx = max.x - min.x, sz = max.z - min.z;
            // Unity RotateY: local +X → (cos, −sin), local +Z → (sin, cos).
            return sx > sz ? new float2(c, -s) : new float2(s, c);
        }

        private static float Wrap(float a)
        {
            const float twoPi = math.PI * 2f;
            a %= twoPi;
            return a < 0f ? a + twoPi : a;
        }
    }
}
