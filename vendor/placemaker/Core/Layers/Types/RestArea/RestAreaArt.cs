// Art-pass defaults for the rest area (2026-10-13), verified against the player's catalog dump.
// The automatic pools gave picnic "tables" that were grills or blankets, makeshift tents as camp
// fires, a brick wall round the dog run and building-sized "lookouts" (water tower deck, fire watch
// tower) at the scenic overlook. Each default is an explicit id with vanilla fallbacks; the
// player's own pick in the asset controls always wins.

using Placemaker.Core.Layers.Common;

namespace Placemaker.Core.Layers.Types.Transport
{
    internal static class RestAreaArt
    {
        public static AssetRef Table(LayerContext ctx) => Or(ctx.S("tableAsset"), "CampgroundTableset01", "CampgroundTableset02");
        public static AssetRef Shelter(LayerContext ctx) => Or(ctx.S("shelterAsset"), "CampgroundShelter02", "CampgroundShelter01", "Gazebo01");
        public static AssetRef Kiosk(LayerContext ctx) => Or(ctx.S("kioskAsset"), "Kiosk01", "Kiosk02");
        public static AssetRef Bench(LayerContext ctx) => Or(ctx.S("benchAsset"), "GardenBench01", "GardenBench02");
        public static AssetRef Lamp(LayerContext ctx) => Or(ctx.S("lampAsset"), "LightpolePark01", "EU_LightpolePark01");
        public static AssetRef DogFence(LayerContext ctx) => Or(ctx.S("fenceAsset"), "FenceResidentialLow01", "FenceResidentialLow02");
        public static readonly AssetRef Grill = AssetMix.One("Grill01", "Grill02", "Grill03");
        public static readonly AssetRef Restroom = AssetMix.One("Outhouse02", "Outhouse01");
        public static readonly AssetRef MapBoard = AssetMix.One("ParkInfosign03", "ParkInfosign01", "ParkSignpost01");
        public static readonly AssetRef ViewShelter = AssetMix.One("Gazebo02", "Gazebo01", "CampgroundShelter01");
        public static readonly AssetRef Court = AssetMix.One("EAI - Surface Pack Tiles Grey Paving Stone Surface", "Tiles Surface 01", "Pavement Surface 02");
        public static readonly string[] Vending = { "VendingMachine01", "VendingMachine02", "VendingMachine04" };
        public static readonly string[] Trees = { "NA_LindenTree01", "OakTree01", "BirchTree01", "NA_LondonPlaneTree01", "PineTree01" };

        private static AssetRef Or(string chosen, string def, params string[] fallbacks) =>
            string.IsNullOrEmpty(chosen) ? AssetMix.One(def, fallbacks) : AssetMix.One(chosen, def);
    }
}
