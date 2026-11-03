// Terrain texture painting (PlannedPaint, built by the game layer right after terraform). Used for
// "natural ground" patches, rocky outcrops, worn ground and beaches: the terrain itself changes texture,
// which reads far more natural than a surface polygon. Painting is permanent (warned).
//
// The game paints a per-texel strength field (Core/Build/PaintRaster → one material-brush stamp per square
// tile), so a paint can feather, follow a gradient (full strength at the water fading inland) and carry edge
// noise like the vanilla "Paint texture" tool. Layers: TerrainMaterials (vanilla maps: 1 Dry grass,
// 2 Lush grass, 3 Dirt, 4 Sand; 0 erases back to the procedural base).

using System.Collections.Generic;
using Placemaker.Core.Geometry;
using Unity.Mathematics;

namespace Placemaker.Core.Layers.Common
{
    public static class TerrainPaint
    {
        public const string PermanentKey = "Placemaker.Paint.Issue.Permanent";

        /// <summary>
        /// A patch with a soft edge: the outline at half strength feathered over <paramref name="blend"/> m (with a
        /// little edge noise) and the inset core at full strength. Two paints per patch (merged by max in the game).
        /// </summary>
        public static bool Patch(LayerContext ctx, IReadOnlyList<float2> ring, int layer, float strength = 1f, float blend = 2f)
        {
            var clean = Poly.Clean(ring, 0.05f);
            if (clean.Length < 3) return false;
            layer = math.clamp(layer, 1, 4);
            float feather = math.max(1.5f, blend);
            ctx.Plan.Paint.Add(new PlannedPaint
            {
                Ring = Poly.To3D(clean, ctx.H), Layer = layer, Strength = math.saturate(strength * 0.5f),
                Feather = feather, Noise = math.clamp(blend * 0.3f, 0f, 2f), Seed = SeedOf(ctx, clean),
            });
            var region = LayerRegion.From(clean);
            if (region != null && blend > 0f)
            {
                var inset = region.InsetVertices(blend);
                if (Poly.SignedArea(inset) > 1f && Poly.IsSimple(inset))
                    ctx.Plan.Paint.Add(new PlannedPaint { Ring = Poly.To3D(inset, ctx.H), Layer = layer, Strength = math.saturate(strength), Feather = 1.5f });
            }
            Warn(ctx);
            return true;
        }

        /// <summary>
        /// One texture field: <paramref name="strength"/> inside the ring, feathered over <paramref name="feather"/> m
        /// at the outline with <paramref name="noise"/> m of natural edge wander. With <paramref name="gradientFrom"/>
        /// (e.g. the waterline) the strength is full within <paramref name="full"/> m of it and fades over
        /// <paramref name="fade"/> m to <paramref name="min"/> × strength.
        /// </summary>
        public static PlannedPaint Field(LayerContext ctx, IReadOnlyList<float2> ring, int layer, float strength, float feather, float noise,
            float2[] gradientFrom = null, float full = 0f, float fade = 0f, float min = 0f, uint salt = 0u)
        {
            var clean = Poly.Clean(ring, 0.05f);
            if (clean.Length < 3 || math.abs(Poly.SignedArea(clean)) < 1f) return null;
            var p = new PlannedPaint
            {
                Ring = Poly.To3D(clean, ctx.H), Layer = math.clamp(layer, 0, 4), Strength = math.saturate(strength),
                Feather = math.max(0.5f, feather), Noise = math.max(0f, noise), NoiseScale = math.max(4f, feather * 1.2f),
                Seed = Hashing.Mix(SeedOf(ctx, clean), salt),
            };
            if (gradientFrom != null && gradientFrom.Length > 0)
            {
                p.GradientFrom = (float2[])gradientFrom.Clone();
                p.GradientFull = math.max(0f, full);
                p.GradientFade = math.max(0.5f, fade);
                p.GradientMin = math.saturate(min);
            }
            ctx.Plan.Paint.Add(p);
            Warn(ctx);
            return p;
        }

        public static void Warn(LayerContext ctx)
        {
            foreach (var i in ctx.Plan.Issues) if (i.Key == PermanentKey) return;
            ctx.Issue(IssueSeverity.Warning, PermanentKey);
        }

        private static uint SeedOf(LayerContext ctx, float2[] ring)
        {
            var c = ring[0];
            return Hashing.Mix(ctx.Seed, (int)math.floor(c.x * 4f), (int)math.floor(c.y * 4f));
        }
    }
}
