// Beach bar / café terrace (feature of the beach and the waterfront): a kiosk beside a path with a
// small terrace in front of it — cafe tables in a fan facing away from the counter, shaded by
// umbrellas when wanted. Placed near the paths so it is reachable; spread when several are wanted.

using Placemaker.Core.Layers.AddOns;
using Placemaker.Core.Layers.Common;
using Unity.Mathematics;

namespace Placemaker.Core.Layers.Types.Beach
{
    [AreaGenerator(71, Hidden = true, Group = "water")]
    public sealed class BeachBarAddOn : AddOnLayer
    {
        public const string LayerId = "beachBar";

        protected override string Area => "AddOn.BeachBar";
        public override string Id => LayerId;
        public override string Icon => "Media/Game/Icons/Commercial.svg";

        protected override void DefineControls(ControlSet c)
        {
            c.Slider("count", 1, 1, 4, 1, "count", ControlTier.Essential, "beachBar");
            c.Slider("tables", 4, 0, 10, 1, "count", ControlTier.Essential, "beachBar").Tip();
            c.Toggle("umbrellas", true, ControlTier.More, "beachBar").Tip();
            c.Asset("asset", AssetCategories.Kiosk, ControlTier.More, "beachBar");
            c.Asset("tableAsset", AssetCategories.CafeSeating, ControlTier.More, "beachBar");
            c.RandomRange("count", 1, 2);
        }

        protected override void DefinePresets()
        {
            Preset("bar", ("tables", 4.0), ("umbrellas", true));
            Preset("terrace", ("tables", 8.0), ("umbrellas", false));
        }

        protected override void PlanCore(LayerContext ctx)
        {
            string kiosk = LeisureKit.Category(ctx, ctx.S("asset"), AssetCategories.Kiosk);
            if (kiosk == null) return;
            var occ = Occupancy(ctx);
            int wanted = ctx.I("count"), tables = ctx.I("tables");
            string tableCat = tables > 0 ? LeisureKit.Category(ctx, ctx.S("tableAsset"), AssetCategories.CafeSeating, AssetCategories.Picnic) : null;
            string shade = ctx.B("umbrellas") ? LeisureKit.Category(ctx, "", AssetCategories.BeachUmbrella, AssetCategories.CafeSeating) : null;
            var spots = Spread(occ, 7f, wanted, Spots.Preference.NearPaths, Hashing.Mix(ctx.Seed, 0xBA40u), math.max(25f, math.sqrt(ctx.LayerRegion.Area) / (wanted + 1)));
            int placed = 0, seats = 0;
            foreach (var c in spots)
            {
                // The counter faces the nearest path so guests walk up to it; the terrace lies in between.
                float2 toward = c + new float2(0, 1);
                if (occ.Paths.Count > 0 && occ.Paths.Nearest(c, 60f, out var onPath) >= 0) toward = onPath;
                var fwd = math.normalizesafe(toward - c, new float2(0, 1));
                var counter = c - fwd * 3.5f;
                uint v = Hashing.Mix(ctx.Seed, 0xBA50u + (uint)placed);
                if (!LeisureKit.Put(ctx, occ, counter, LeisureKit.Yaw(counter, toward), ObjectRole.Furniture, AssetMix.One(ctx.S("asset")), kiosk, 2.5f, v, 0.5f, 0.3f))
                    continue;
                placed++;
                if (tableCat == null) continue;
                // Tables in a fan between the counter and the path.
                var side = new float2(-fwd.y, fwd.x);
                for (int k = 0; k < tables; k++)
                {
                    int row = k / 4, col = k % 4;
                    var q = c + fwd * (row * 2.6f) + side * ((col - 1.5f) * 2.4f);
                    if (LeisureKit.Put(ctx, occ, q, LeisureKit.Yaw(q, counter), ObjectRole.Furniture, AssetMix.One(ctx.S("tableAsset")), tableCat, 1f, Hashing.Mix(v, (uint)k + 1u), 0.4f, 0.15f))
                        seats++;
                    if (shade != null && (k & 1) == 1)
                        LeisureKit.Put(ctx, occ, q + side * 1.2f + fwd * 1.3f, 0f, ObjectRole.Furniture, AssetMix.One(""), shade, 0.5f, Hashing.Mix(v, (uint)k + 50u), 0.4f, 0.05f);
                }
            }
            if (placed == 0) NoSpace(ctx, "Placemaker.AddOn.BeachBar.Name");
            ctx.Stat("Placemaker.Stat.Kiosks", placed, Keys.UnitCount);
            ctx.Stat("Placemaker.Stat.CafeTables", seats, Keys.UnitCount);
        }
    }
}
