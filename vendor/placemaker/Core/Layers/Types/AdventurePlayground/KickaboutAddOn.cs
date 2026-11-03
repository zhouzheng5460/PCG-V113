// Kickabout lawn (adventure playground feature): an open mown rectangle for ball games with a small
// goal at each end and a couple of benches along one long side. Placed in the most open free spot.

using Placemaker.Core.Layers.AddOns;
using Placemaker.Core.Layers.Common;
using Unity.Mathematics;

namespace Placemaker.Core.Layers.Types.AdventurePlayground
{
    [AreaGenerator(219, Hidden = true)]
    public sealed class KickaboutAddOn : AddOnLayer
    {
        public const string LayerId = "kickabout";
        protected override string Area => "AddOn.Kickabout";
        public override string Id => LayerId;
        public override string Icon => "Media/Game/Icons/SportsParks.svg";

        protected override void DefineControls(ControlSet c)
        {
            c.Slider("length", 26, 14, 44, 2, "m", ControlTier.Essential, "kickabout").Tip();
            c.Toggle("goals", true, ControlTier.Essential, "kickabout");
            c.Toggle("benches", true, ControlTier.More, "kickabout");
            c.Asset("goalAsset", AssetCategories.SportsGoal, ControlTier.More, "kickabout").When("goals=true");
        }

        protected override void DefinePresets()
        {
            Preset("small", ("length", 18.0));
            Preset("pitch", ("length", 36.0));
        }

        protected override void PlanCore(LayerContext ctx)
        {
            var occ = Occupancy(ctx);
            float len = ctx.F("length");
            for (int attempt = 0; attempt < 4; attempt++, len *= 0.8f)
            {
                float wid = len * 0.6f;
                float reach = math.sqrt(len * len + wid * wid) * 0.5f;
                if (!Spots.Find(occ, reach, Spots.Preference.Open, ctx.Seed, out var c, null, 0f, 1.5f, 2000)) continue;
                LeisureKit.Frame(ctx.LayerRegion, out _, out var axis, out _, out _, out _);
                var r = new OrientedRect { Center = c, Axis = axis, HalfLength = len * 0.5f, HalfWidth = wid * 0.5f };
                if (!LeisureKit.RectFits(ctx.LayerRegion, occ, r, 0.5f))
                {
                    r.Axis = r.Side;
                    if (!LeisureKit.RectFits(ctx.LayerRegion, occ, r, 0.5f)) continue;
                }
                if (attempt > 0) ctx.Issue(IssueSeverity.Info, "Placemaker.AddOn.Kickabout.Issue.Smaller");
                Build(ctx, occ, r);
                return;
            }
            NoSpace(ctx, "Placemaker.AddOn.Kickabout.Name");
        }

        private static void Build(LayerContext ctx, Occupancy occ, OrientedRect r)
        {
            int goals = 0;
            if (ctx.B("goals"))
            {
                string cat = LeisureKit.Category(ctx, ctx.S("goalAsset"), AssetCategories.SportsGoal, AssetCategories.Sports);
                if (cat != null)
                    foreach (int s in new[] { -1, 1 })
                    {
                        var p = r.Local(s * (r.HalfLength - 1.2f), 0f);
                        ctx.Object(p, LeisureKit.Yaw(p, r.Center), ObjectRole.Furniture, AssetMix.One(ctx.S("goalAsset")), Hashing.Mix(ctx.Seed, (uint)(s + 5)), -1, cat);
                        goals++;
                    }
            }
            LeisureKit.TakeRect(occ, r);
            if (ctx.B("benches"))
                for (int i = 0; i < 2; i++)
                {
                    var p = r.Local((i - 0.5f) * r.HalfLength * 0.8f, r.HalfWidth + 1.8f);
                    LeisureKit.Put(ctx, occ, p, LeisureKit.Yaw(p, r.Center), ObjectRole.Bench, AssetMix.One(""), null, 1.2f, Hashing.Mix(ctx.Seed, 0xBEu + (uint)i));
                }
            ctx.Plan.Facts["kickaboutGoals"] = goals;
            ctx.Plan.Facts["kickaboutLength"] = r.HalfLength * 2f;
        }
    }
}
