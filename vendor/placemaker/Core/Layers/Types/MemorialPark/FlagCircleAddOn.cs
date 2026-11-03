// Flag circle (memorial park feature): a small round paved court beside a path with flagpoles on its
// rim, a plaque or wreath in the middle and benches facing in. Optional dressing: without flagpoles
// installed it reports the usual Info and still builds the court.

using Placemaker.Core.Layers.AddOns;
using Placemaker.Core.Layers.Common;
using Unity.Mathematics;

namespace Placemaker.Core.Layers.Types.MemorialPark
{
    [AreaGenerator(220, Hidden = true)]
    public sealed class FlagCircleAddOn : AddOnLayer
    {
        public const string LayerId = "flagCircle";
        protected override string Area => "AddOn.FlagCircle";
        public override string Id => LayerId;
        public override string Icon => "Media/Game/Icons/Unique.svg";

        protected override void DefineControls(ControlSet c)
        {
            c.Slider("flags", 8, 3, 16, 1, "count", ControlTier.Essential, "flagCircle");
            c.Slider("radius", 7, 4, 14, 0.5, "m", ControlTier.Essential, "flagCircle").Layout(4);
            c.Toggle("benches", true, ControlTier.More, "flagCircle");
            c.Asset("flagAsset", AssetCategories.MemorialFlag, ControlTier.More, "flagCircle");
        }

        protected override void DefinePresets()
        {
            Preset("small", ("flags", 5.0), ("radius", 5.0));
            Preset("grand", ("flags", 14.0), ("radius", 11.0));
        }

        protected override void PlanCore(LayerContext ctx)
        {
            var occ = Occupancy(ctx);
            float r = ctx.F("radius");
            for (int attempt = 0; attempt < 4; attempt++, r *= 0.8f)
            {
                if (r < 3.5f) break;
                if (!Spots.Find(occ, r + 1.5f, Spots.Preference.NearPaths, ctx.Seed, out var c, null, 0f, 1.5f, 2000)) continue;
                Build(ctx, occ, c, r);
                return;
            }
            NoSpace(ctx, "Placemaker.AddOn.FlagCircle.Name");
        }

        private static void Build(LayerContext ctx, Occupancy occ, float2 c, float r)
        {
            ctx.Surface(LayerRegion.Circle(c, r, 24), SurfaceRole.Plaza, AssetMix.One(""), AssetCategories.SurfacePaving);
            var toPath = occ.Paths.Count > 0 && occ.Paths.Nearest(c, 300f, out var q) >= 0 ? math.normalizesafe(q - c, new float2(0, 1)) : new float2(0, 1);
            float entry = math.atan2(toPath.y, toPath.x);
            int flags = 0;
            string fc = LeisureKit.Category(ctx, ctx.S("flagAsset"), AssetCategories.MemorialFlag);
            if (fc != null)
            {
                int n = ctx.I("flags");
                for (int i = 0; i < n; i++)
                {
                    // Spread over the rim, leaving the side towards the path open as the way in.
                    float a = entry + 0.6f + i * (2f * math.PI - 1.2f) / math.max(1, n - 1);
                    var p = c + new float2(math.cos(a), math.sin(a)) * (r - 0.8f);
                    ctx.Object(p, LeisureKit.Yaw(p, c), ObjectRole.Furniture, AssetMix.One(ctx.S("flagAsset")), Hashing.Mix(ctx.Seed, 0xF1u + (uint)i), -1, fc);
                    flags++;
                }
            }
            string pc = LeisureKit.Category(ctx, "", AssetCategories.MemorialPlaque, AssetCategories.CenterpieceStatue, AssetCategories.Centerpiece);
            if (pc != null) ctx.Object(c, LeisureKit.Yaw(c, c + toPath), pc == AssetCategories.MemorialPlaque ? ObjectRole.Furniture : ObjectRole.Centerpiece,
                AssetMix.One(""), Hashing.Mix(ctx.Seed, 0x91u), -1, pc);
            if (ctx.B("benches") && r >= 5f)
                foreach (float s in new[] { -1f, 1f })
                {
                    float a = entry + math.PI + s * 0.9f;
                    var p = c + new float2(math.cos(a), math.sin(a)) * (r * 0.55f);
                    ctx.Object(p, LeisureKit.Yaw(p, c), ObjectRole.Bench, AssetMix.One(""), Hashing.Mix(ctx.Seed, 0xBEu + (uint)(s + 2f)));
                }
            occ.Take(c, r, 0);
            ctx.Plan.Facts["flagCircleFlags"] = flags;
        }
    }
}
