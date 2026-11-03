// Art pass a2: the designer's default asset for every element of the squares (plaza, market square,
// transit plaza…), used whenever the player leaves the matching asset control on "automatic".
//
// Why explicit ids: the automatic category pools of this catalog are polluted (bench → bus stops,
// planters and a school wing; kiosk / seating.cafe / stage → billboards and 128 m supermarkets;
// centerpiece.statue → clock hands; water.jets → a farm sprinkler). Ids verified in the player's
// catalog-dump (2026-10-13); all vanilla except where a fallback chain starts with a modded item.
// Each ref keeps vanilla fallbacks, so a missing item degrades to the next, then to the category.

using Placemaker.Core.Layers.Common;

namespace Placemaker.Core.Layers.Plaza
{
    public static class PlazaArt
    {
        private static AssetRef R(string id, params string[] fallbacks) => new AssetRef(id, fallbacks);

        public static readonly string[] Benches = { "GardenBench02", "GardenBench03", "GardenBench01" };
        public static readonly string[] ModernBenches = { "Pack10-Bench01", "Pack10-Bench03" };
        public static readonly AssetRef ParkLamp = R("EU_LightpolePark01", "LightpolePark01", "NA_LightpolePark01");
        public static readonly AssetRef ModernLamp = R("LightpolePark04", "LightpolePark02", "EU_LightpolePark01");
        public static readonly AssetRef StreetLamp = R("StreetlightSingle01", "LightpoleCommercial01", "EU_LightpolePark01");
        public static readonly AssetRef Bin = R("Trashbin03", "Trashbin01", "Trashbin02");
        public static readonly AssetRef Bollard = R("Bollard01", "Bollard02", "Bollard03");
        public static readonly string[] PlaneTrees = { "NA_LondonPlaneTree01", "NA_LindenTree01" };
        public static readonly string[] LimeTrees = { "NA_LindenTree01", "NA_LondonPlaneTree01" };
        public static readonly string[] ChestnutTrees = { "EU_ChestnutTree01", "NA_LindenTree01" };
        public static readonly string[] GardenTrees = { "NA_LindenTree01", "EU_ChestnutTree01", "VFP JacarandaTree", "VFP CrapeMyrtleTree" };
        public static readonly AssetRef GrandFountain = R("LargeFountain02", "LargeFountain03", "LargeFountain01");
        public static readonly AssetRef SmallFountain = R("SmallFountain03", "SmallFountain01", "SmallFountain02");
        public static readonly AssetRef Sculpture = R("BicycleStatue01", "LargeFountain01");
        public static readonly AssetRef FlagPole = R("FlagPole01", "FlagPole02", "FlagPoleCommercial01");
        public static readonly string[] Flowerbeds = { "Flowerbed01", "Flowerbed02" };
        public static readonly string[] CityBushes = { "FlowerBushCity01", "FlowerBushCity02", "GreenBushCity03", "FlowerBushCity03" };
        public static readonly string[] GreenBushes = { "GreenBushCity03", "GreenBushCity02", "GreenBushCity01" };
        public static readonly AssetRef LowHedge = R("HedgePieceLow01", "HedgePieceLow02", "VFP BoxwoodHedgePiece");
        public static readonly string[] Pots = { "FlowerPot01", "PlantPot01", "FlowerPot02" };
        public static readonly string[] Pergolas = { "Pergola02", "Pergola05", "Pergola01" };
        public static readonly string[] Patios = { "Patio01", "Patio03", "Patio04", "Patio02" };
        public static readonly string[] Parasols = { "Parasol01", "Parasol03", "Parasol02" };
        public static readonly AssetRef TableSet = R("ChirperTableset01", "CampgroundTableset01");
        public static readonly string[] Kiosks = { "Kiosk01", "Kiosk02", "Kiosk03" };
        public static readonly string[] Stalls = { "FoodStall01", "FoodStall02", "FoodStall03" };
        public static readonly string[] Carts = { "FoodCart02", "FoodCart01", "FoodCart03" };
        public static readonly AssetRef Stage = R("StageSmall01", "Gazebo02", "Gazebo01");
        public static readonly AssetRef Signpost = R("ParkSignpost02", "ParkSignpost01", "ParkSignpost03");
        public static readonly AssetRef Vending = R("VendingMachine01", "NewspaperVendingMachine01");

        /// <summary>
        /// Whether a designed element may be placed: always when the player chose an asset, else only
        /// when one of its categories has installed items (a player without them gets the usual
        /// "no assets" info instead of a designer's pick that may not exist either).
        /// </summary>
        public static bool Available(LayerContext ctx, string control, params string[] categories)
        {
            if (!string.IsNullOrEmpty(control) && !string.IsNullOrEmpty(ctx.S(control))) return true;
            foreach (var c in categories) if (AssetAvailability.Has(c)) return true;
            AssetAvailability.Require(ctx.Plan, categories[0], null, Keys.Category(categories[0]));
            return false;
        }

        /// <summary>The player's asset when one is chosen, else the designed default.</summary>
        public static AssetRef Or(LayerContext ctx, string control, AssetRef designed)
        {
            string s = ctx.S(control);
            return string.IsNullOrEmpty(s) ? designed : AssetMix.One(s);
        }

        /// <summary>The player's asset when one is chosen, else one of the designed set (stable per variation).</summary>
        public static AssetRef Or(LayerContext ctx, string control, string[] designed, uint variation)
        {
            string s = ctx.S(control);
            return string.IsNullOrEmpty(s) ? AssetMix.Pick(designed, variation) : AssetMix.One(s);
        }

        /// <summary>The player's list when any, else the designed set.</summary>
        public static string[] OrList(LayerContext ctx, string control, string[] designed)
        {
            var l = ctx.L(control);
            return l != null && l.Length > 0 ? l : designed;
        }
    }
}
