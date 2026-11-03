// Tea house (feature of the zen garden, usable in any park): a tea house on the most secluded open
// spot, its waiting bench, a stone water basin with a lantern, and a stepping-stone approach from the
// nearest path — the roji setting in miniature.

using Placemaker.Core.Layers.AddOns;
using Placemaker.Core.Layers.Common;

namespace Placemaker.Core.Layers.Types.ZenGarden
{
    [AreaGenerator(622)]
    public sealed class TeaHouseAddOn : AddOnLayer
    {
        public const string LayerId = "teaHouse";

        protected override string Area => "AddOn.TeaHouse";
        public override string Id => LayerId;
        public override string Icon => "Media/Game/Icons/ParksAndRecreation.svg";

        protected override void DefineControls(ControlSet c)
        {
            c.Asset("asset", AssetCategories.ZenTeaHouse, ControlTier.Essential, "structures");
            c.Toggle("bench", true, ControlTier.Essential, "structures").Tip();
            c.Toggle("basin", true, ControlTier.More, "structures").Tip();
            c.Toggle("lantern", true, ControlTier.More, "structures");
            c.Toggle("stones", true, ControlTier.More, "structures").Tip();
        }

        protected override void DefinePresets()
        {
            Preset("roji", ("bench", true), ("basin", true), ("lantern", true), ("stones", true));
            Preset("pavilion", ("bench", false), ("basin", false), ("lantern", true), ("stones", false));
        }

        protected override void PlanCore(LayerContext ctx)
        {
            string cat = LeisureKit.Category(ctx, ctx.S("asset"), AssetCategories.ZenTeaHouse, AssetCategories.Shelter);
            if (cat == null) return;
            var occ = Occupancy(ctx);
            var gates = PathAnchors.Entrances(occ);
            if (!ZenKit.TeaHouse(ctx, occ, gates, AssetMix.One(ctx.S("asset")), cat, ctx.B("bench"), ctx.B("basin"), ctx.B("lantern"), ctx.B("stones"),
                    (p, r) => occ.HitsPolygon(p, r), out _))
                NoSpace(ctx, "Placemaker.AddOn.TeaHouse.Name");
            else ctx.Stat("Placemaker.Stat.TeaHouses", 1, Keys.UnitCount);
        }
    }
}
