// BMX dirt jumps: lines of take-off/landing mound pairs shaped with terraform, on a strip of painted
// dirt, with an optional roll-in hill at the start. Each mound is two nested ellipses (shoulder at
// ~45 % height, crest at full height) so the brush falloff turns them into round humps. Lines are
// always placed ≥ 6 m inside the outline, so the terrain is back at the original height at the edge.
// Used by the urbanRuins type and the "dirtJumps" feature.

using System.Collections.Generic;
using Placemaker.Core.Geometry;
using Placemaker.Core.Layers.Common;
using Placemaker.Core.Layers.Types.Ruins;
using Unity.Mathematics;

namespace Placemaker.Core.Layers.Types.UrbanRuins
{
    public static class DirtJumps
    {
        public const float LineWidth = 7f;
        public const float EdgeMargin = 6f;
        /// <summary>Region a host carves out for the dirtJumps feature (a composer "place": siblings keep out).</summary>
        public const string Place = "place:jumps";

        /// <summary>
        /// Host side of the feature: when a dirtJumps child is on, claim a free rectangle for
        /// <paramref name="lines"/> lines (shrinking to one short line when space is tight), keep the
        /// host's own scatter out of it and hand it over as the "place:jumps" region.
        /// </summary>
        public static FeatureInfo InPlace(FeatureInfo f) { f.Target = Place; return f; }

        public static bool CarvePlace(LayerContext ctx, LayerRegion region, Occupancy occ, Zones zones, float2 near, float2 axis, int lines)
        {
            if (!ctx.Input.HasChild(DirtJumpsAddOn.LayerId)) return false;
            for (int l = math.max(1, lines); l >= 1; l--)
                foreach (int jumps in new[] { 4, 3, 2 })
                {
                    float hl = LineLength(jumps, 9f, jumps >= 3) * 0.5f + EdgeMargin + 0.5f;
                    float hw = (l * LineWidth + (l - 1) * 4f) * 0.5f + EdgeMargin + 0.5f;
                    if (!DecayKit.FindRect(region, occ, zones, near, axis, hl, hw, 1f, Hashing.Mix(ctx.Seed, 0x91ACu + (uint)(l * 8 + jumps)), out var r, 100)) continue;
                    zones.Add(r.Ring);
                    occ.TakeCorridor(r.Local(-r.HalfLength, 0f), r.Local(r.HalfLength, 0f), r.HalfWidth, 5);
                    ctx.AddRegion(r.Ring, Place, Place);
                    return true;
                }
            ctx.Issue(IssueSeverity.Info, "Placemaker.AddOn.DirtJumps.Issue.NoPlace");
            return false;
        }

        /// <summary>Length of a line with <paramref name="jumps"/> pairs (roll-in hill + run-up + pairs + run-out).</summary>
        public static float LineLength(int jumps, float spacing, bool startHill) => (startHill ? 9f : 0f) + 5f + jumps * spacing + 3f;

        /// <summary>Emits one line inside <paramref name="r"/> (along r.Axis). Returns the jump pairs built.</summary>
        public static int BuildLine(LayerContext ctx, OrientedRect r, int jumps, float height, float spacing, bool startHill, bool paint, int paintLayer, uint seed)
        {
            float u = -r.HalfLength;
            if (startHill)
            {
                Mound(ctx, r.Local(u + 4.5f, 0f), r.Axis, 4f, 3.2f, math.min(4f, height * 1.5f), 3);
                u += 9f;
            }
            u += 5f;
            int built = 0;
            for (int k = 0; k < jumps; k++)
            {
                if (u + spacing > r.HalfLength + 0.01f) break;
                uint v = Hashing.Mix(seed, (uint)k);
                float h = height * (0.85f + 0.3f * Hashing.Unit(v));
                // Lip first, landing a gap later; the landing is longer and a touch lower.
                Mound(ctx, r.Local(u + spacing * 0.2f, 0f), r.Axis, 2.2f, 1.8f, h, 2);
                Mound(ctx, r.Local(u + spacing * 0.68f, 0f), r.Axis, 3f, 2f, h * 0.9f, 2);
                u += spacing;
                built++;
            }
            if (paint)
            {
                var strip = new OrientedRect { Center = r.Center, Axis = r.Axis, HalfLength = r.HalfLength, HalfWidth = math.min(r.HalfWidth, LineWidth * 0.5f) };
                TerrainPaint.Patch(ctx, strip.Ring, paintLayer, 1.8f, 1.5f);   // (rf-sports) full-strength dirt: at 0.45 the track barely showed
            }
            if (built > 0) WarnPermanent(ctx);
            return built;
        }

        /// <summary>An elliptical hump of nested raise rings (outermost lowest).</summary>
        public static void Mound(LayerContext ctx, float2 c, float2 axis, float halfLength, float halfWidth, float height, int rings)
        {
            float baseH = ctx.H(c);
            for (int i = 0; i < rings; i++)
            {
                float f = 1f - i / (float)rings;              // 1, 0.67, 0.33…
                float h = height * (i + 1) / rings;
                if (rings == 2) h = i == 0 ? height * 0.45f : height;
                var ring = Ellipse(c, axis, halfLength * f, halfWidth * f, 12);
                ctx.Plan.Terraform.Add(new PlannedTerraform
                {
                    Ring = Poly.To3D(ring, ctx.H), Kind = TerraformKind.Raise, TargetHeight = baseH + h, Permanent = true,
                });
            }
        }

        /// <summary>A shallow dish of nested lower rings (DIY bowl), deepest in the middle.</summary>
        public static void Dish(LayerContext ctx, float2 c, float radius, float depth)
        {
            float baseH = ctx.H(c);
            ctx.Plan.Facts["intentionalPit"] = 1;   // (rf-sports) a skate bowl is dug on purpose: the pit guard filled it back in
            for (int i = 0; i < 2; i++)
            {
                float f = i == 0 ? 1f : 0.6f;
                ctx.Plan.Terraform.Add(new PlannedTerraform
                {
                    Ring = Poly.To3D(LayerRegion.Circle(c, radius * f, 20), ctx.H), Kind = TerraformKind.Lower,
                    TargetHeight = baseH - depth * (i == 0 ? 0.5f : 1f), Permanent = true,
                });
            }
            WarnPermanent(ctx);
        }

        public static float2[] Ellipse(float2 c, float2 axis, float a, float b, int sides)
        {
            var side = new float2(-axis.y, axis.x);
            var r = new float2[sides];
            for (int i = 0; i < sides; i++)
            {
                float t = i * 2f * math.PI / sides;
                r[i] = c + axis * (math.cos(t) * a) + side * (math.sin(t) * b);
            }
            return r;
        }

        public static void WarnPermanent(LayerContext ctx)
        {
            foreach (var i in ctx.Plan.Issues) if (i.Key == TerrainShaper.PermanentKey) return;
            ctx.Issue(IssueSeverity.Warning, TerrainShaper.PermanentKey);
        }

        /// <summary>
        /// Places up to <paramref name="lines"/> parallel lines in free space near <paramref name="near"/>.
        /// Lines shorten to what fits (≥ 2 pairs). Returns the jump pairs built; rects are added to zones.
        /// </summary>
        public static int PlaceLines(LayerContext ctx, LayerRegion region, Occupancy occ, Zones zones, float2 near, float2 axis, int lines, int maxJumps,
            float height, float spacing, bool startHill, bool paint, int paintLayer, uint seed, List<OrientedRect> placed = null)
        {
            int total = 0;
            var at = near;
            for (int l = 0; l < lines; l++)
            {
                bool ok = false;
                for (int jumps = maxJumps; jumps >= 2 && !ok; jumps--)
                {
                    for (int hill = startHill ? 1 : 0; hill >= 0 && !ok; hill--)
                    {
                        float len = LineLength(jumps, spacing, hill == 1);
                        if (!DecayKit.FindRect(region, occ, zones, at, axis, len * 0.5f, LineWidth * 0.5f, EdgeMargin, Hashing.Mix(seed, (uint)l), out var r, 120)) continue;
                        total += BuildLine(ctx, r, jumps, height, spacing, hill == 1, paint, paintLayer, Hashing.Mix(seed, (uint)l + 100u));
                        zones.Add(r.Ring);
                        occ.TakeCorridor(r.Local(-r.HalfLength, 0f), r.Local(r.HalfLength, 0f), r.HalfWidth, 5);
                        placed?.Add(r);
                        at = r.Center + r.Side * (LineWidth + 4f);
                        axis = r.Axis;
                        ok = true;
                    }
                }
                if (!ok) break;
            }
            return total;
        }
    }
}
