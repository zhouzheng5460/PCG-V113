// Farmstead art defaults (rf-farms, 2026-10-16), checked in the game runner's clean profile. Vanilla has
// farm props only as parts of the agriculture buildings; the automatic chains reached the wrong things: raised-bed
// corner pieces as "crops", a 16 m irrigation rig parked in the yard as "machinery", gazebos as field shelters and
// no silo at all. Each default is an explicit vanilla id; installed farm assets (or the player's pick) still win.

using Placemaker.Core.Layers.Common;

namespace Placemaker.Core.Layers.Types.Farmstead
{
    internal static class FarmArt
    {
        /// <summary>Tractor trailers with their loads, parked nose-in along the yard front.</summary>
        public static readonly string[] Trailers =
        {
            "TractorTrailer01_CropPile02", "TractorTrailer03_CropPile02", "TractorTrailer01_CropPile03", "TractorTrailer03_CropPile01",
        };

        /// <summary>Feed bins: the tall vanilla water tanks, in a row at the yard end.</summary>
        public static readonly AssetRef SiloStandIn = AssetMix.One("WaterTank03", "WaterTank02");

        /// <summary>The irrigation rig that stands in the big arable fields (arable farms).</summary>
        public const string Irrigation = "AgricultureSprinklerSystem";
    }
}
