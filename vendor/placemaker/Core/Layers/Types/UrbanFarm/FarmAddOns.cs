// Retired duplicates (g5 integration): the urban farm's bee yard ("honeyHives") and farm stall
// ("farmStall") were the same features as the farmstead's "apiary" and "farmStand". Those two
// implementations are kept; these ids stay loadable as hidden aliases (settings are translated by
// LayerAliases, and loaded recipes/receipts are normalised to the canonical ids).

using Placemaker.Core.Layers.Common;

namespace Placemaker.Core.Layers.Types.UrbanFarm
{
    [AreaGenerator(132, Hidden = true)]
    public sealed class HoneyHivesAddOn : LayerAlias
    {
        public const string LayerId = "honeyHives";
        public HoneyHivesAddOn() : base(LayerId) { }
    }

    [AreaGenerator(133, Hidden = true)]
    public sealed class FarmStallAddOn : LayerAlias
    {
        public const string LayerId = "farmStall";
        public FarmStallAddOn() : base(LayerId) { }
    }
}
