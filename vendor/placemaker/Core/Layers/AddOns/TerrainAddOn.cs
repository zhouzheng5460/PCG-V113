// Terrain feature: gentle mounds, rolling hills, terraces, sunken lawn, raised platform, an
// amphitheatre bowl, berms along the roads, or flatten. Always permanent (warned), always blended
// back to the original ground at the outline (TerrainShaper). Places and reserved polygons (a
// park's pond basin or plaza) are left untouched.
//
// s31 (playtest: a park on a bumpy hill with this feature on "went insane"):
//  * shapes are a smooth sloped surface (TerrainShaper Sloped), no 12 m level cells (their steps were the curtains);
//  * decorative shapes (mounds, hills, berms) fade out near the parent's paths and furniture: a mound never crosses a
//    path (Build/SiteHarmony masks what is left and grades the paths, pads and surfaces afterwards);
//  * on ground that is already rough the decoration scales back, or is skipped with an Info issue.

using System;
using Placemaker.Core.Layers.Common;
using Unity.Mathematics;

namespace Placemaker.Core.Layers.AddOns
{
    [AreaGenerator(140)]
    public sealed class TerrainAddOn : AddOnLayer
    {
        public const string LayerId = "terrain";

        /// <summary>Roughness (m, RMS of the ground about its ±8 m local mean) where decoration starts to scale back / is skipped.</summary>
        public const float RoughStart = 0.25f, RoughSkip = 0.75f;

        public const string IssueRough = "Placemaker.AddOn.Terrain.Issue.RoughGround";
        public const string IssueScaled = "Placemaker.AddOn.Terrain.Issue.ScaledBack";

        protected override string Area => "AddOn.Terrain";
        public override string Id => LayerId;
        public override string Icon => "Media/Game/Icons/TerrainHeight.svg";
        public override float MinArea => 200f;

        protected override void DefineControls(ControlSet c)
        {
            c.Choice("shape", "mounds", ControlTier.Essential, "terrain",
                "mounds", "hills", "terraces", "terracedDown", "sunken", "raised", "bowl", "berms", "flatten").Tip();
            c.Slider("height", 2, 0.5, 8, 0.5, "m", ControlTier.Essential, "terrain").Tip();
            // (mounds: the noise humps reach the full height only at their peaks)
            c.Slider("scale", 30, 10, 80, 5, "m", ControlTier.More, "terrain").Layout(8).Tip();
            c.Slider("maxSlope", 20, 5, 50, 5, "%", ControlTier.More, "terrain").Tip();
            c.Slider("falloff", 18, 6, 60, 2, "m", ControlTier.More, "terrain").Layout(4).Tip();
            c.Slider("levels", 3, 2, 4, 1, "count", ControlTier.More, "terrain");
            c.RandomRange("height", 1, 3);
            c.Fixed("shape");
        }

        protected override void DefinePresets()
        {
            Preset("gentle", ("shape", "mounds"), ("height", 1.5), ("scale", 25.0));
            Preset("rolling", ("shape", "hills"), ("height", 3.0), ("scale", 45.0));
            Preset("terraced", ("shape", "terraces"), ("height", 3.0), ("levels", 3.0));
            Preset("amphitheatre", ("shape", "bowl"), ("height", 3.0));
            Preset("flat", ("shape", "flatten"));
        }

        protected override void PlanCore(LayerContext ctx)
        {
            var region = ctx.LayerRegion;
            string shape = ctx.S("shape");
            var reserved = ctx.Input.Reserved;
            var mode = TerrainSpec.Parse(shape);
            bool decorative = mode == TerrainMode.Mounds || mode == TerrainMode.Hills || mode == TerrainMode.Berms;
            float height = ctx.F("height");
            if (decorative)
            {
                float rough = Roughness(ctx, region);
                ctx.Plan.Facts["terrainRoughness"] = rough;
                float scale = 1f - math.saturate((rough - RoughStart) / (RoughSkip - RoughStart));
                if (scale < 0.2f)
                {
                    ctx.Issue(IssueSeverity.Info, IssueRough);
                    ctx.Stat("Placemaker.Stat.TerrainChange", 0, Keys.UnitM);
                    return;
                }
                if (scale < 0.95f)
                {
                    height *= scale;
                    ctx.Issue(IssueSeverity.Info, IssueScaled);
                }
            }
            var spec = new TerrainSpec
            {
                Mode = mode, Down = shape == "terracedDown", Height = height, Scale = ctx.F("scale"),
                MaxSlopePct = ctx.F("maxSlope"), Falloff = ctx.F("falloff"), Levels = ctx.I("levels"), Seed = Hashing.Mix(ctx.Seed, 0x7E44u),
                RowSpacing = 4f, Sloped = true,
                Skip = p =>
                {
                    if (reserved == null) return false;
                    foreach (var poly in reserved.Polygons) if (ReservedSpace.PointInPolygon(p, poly)) return true;
                    return false;
                },
                Weight = decorative ? AwayFromUse(reserved) : null,
            };
            if (spec.Mode == TerrainMode.Bowl) spec.Focal = BowlFocal(region, spec.Falloff);
            if (spec.Mode == TerrainMode.Berms)
            {
                spec.Frontage = StreetFrontage.Edges(region.Ring, StreetFrontage.Roads(ctx.Input));
                if (spec.Frontage.Count == 0) ctx.Issue(IssueSeverity.Info, "Placemaker.AddOn.Terrain.Issue.NoRoads");
            }
            var r = TerrainShaper.Shape(ctx, region, spec);
            if (r.Cells == 0) ctx.Issue(IssueSeverity.Info, "Placemaker.AddOn.Terrain.Issue.Nothing");
            ctx.Stat("Placemaker.Stat.TerrainChange", System.Math.Round(math.max(r.MaxOffset, -r.MinOffset), 1), Keys.UnitM);
        }

        /// <summary>
        /// 0 on and near the parent's paths (half width + 3 m) and furniture (+ 1.5 m), rising to 1 over 8 m / 5 m: decoration
        /// keeps off what people walk and sit on. Trees and bushes do not count (a tree on a mound is fine).
        /// </summary>
        public static Func<float2, float> AwayFromUse(ReservedSpace reserved)
        {
            if (reserved == null) return null;
            var corridors = reserved.Corridors.ToArray();
            string tree = ObjectRole.Tree.ToString(), bush = ObjectRole.Bush.ToString();
            var discs = reserved.Discs.FindAll(d => d.Tag != tree && d.Tag != bush).ToArray();
            return p =>
            {
                float w = 1f;
                foreach (var c in corridors)
                {
                    float d = math.sqrt(ReservedSpace.DistSqToSegment(p, c.A, c.B)) - c.HalfWidth - 3f;
                    if (d < 8f) w = math.min(w, Smooth01(d / 8f));
                    if (w <= 0f) return 0f;
                }
                foreach (var c in discs)
                {
                    float d = math.distance(p, c.Center) - c.Radius - 1.5f;
                    if (d < 5f) w = math.min(w, Smooth01(d / 5f));
                    if (w <= 0f) return 0f;
                }
                return w;
            };
        }

        /// <summary>RMS (m) of the ground about its local mean (5 × 5 box of a 4 m grid, ±8 m) inside the region: bumpiness, not slope.</summary>
        public static float Roughness(LayerContext ctx, LayerRegion region)
        {
            var s = TerrainSurvey.Of(ctx, region, 8f, 4f);
            double sq = 0; int n = 0;
            for (int j = 2; j < s.Ny - 2; j++)
                for (int i = 2; i < s.Nx - 2; i++)
                {
                    var p = s.Origin + new float2(i, j) * s.Cell;
                    if (!region.Contains(p)) continue;
                    // The box mean of a plane is its centre value (symmetric box), so a steady slope is not "rough".
                    float sum = 0f;
                    for (int b = -2; b <= 2; b++)
                        for (int a = -2; a <= 2; a++) sum += s.Height[(j + b) * s.Nx + i + a];
                    float r = s.Height[j * s.Nx + i] - sum / 25f;
                    sq += r * r; n++;
                }
            return n == 0 ? 0f : (float)math.sqrt(sq / n);
        }

        private static float Smooth01(float t)
        {
            t = math.saturate(t);
            return t * t * (3f - 2f * t);
        }

        /// <summary>The bowl's stage: the middle of the longest outline edge, nudged inside.</summary>
        public static float2 BowlFocal(LayerRegion region, float inset)
        {
            int best = 0; float len = 0;
            for (int i = 0; i < region.Ring.Length; i++)
            {
                float l = math.distance(region.Ring[i], region.Ring[(i + 1) % region.Ring.Length]);
                if (l > len) { len = l; best = i; }
            }
            var mid = math.lerp(region.Ring[best], region.Ring[(best + 1) % region.Ring.Length], 0.5f);
            return mid + region.InwardNormal(best) * math.max(4f, inset * 0.5f);
        }
    }
}
