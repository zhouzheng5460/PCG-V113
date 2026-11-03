// Art-pass defaults for golf (2026-10-13): explicit, verified asset ids from the player's catalog
// dump, each with vanilla fallbacks, so a course never depends on what the automatic pools happen
// to pick (the pools gave an 8 m promenade as cart path, "golf flags" that do not exist, an outhouse
// as clubhouse and road-marking decals on mini golf tees).
//  * Surfaces: the Manicured Grass pack (striped fairways and tees, plain greens) and rippled sand for
//    raked bunkers when installed; vanilla Grass/Sand/Concrete otherwise.
//  * Props: vanilla ships no golf props, so the sports-park corner flag (1.5 m) stands in for a pin,
//    flat training cones for tee markers and the dog-park pole (0.9 m) for a disc golf basket.
// Every id is a default only: a player's own pick in the asset controls always wins.

using Placemaker.Core.Layers.Common;

namespace Placemaker.Core.Layers.Types.Golf
{
    internal static class GolfAssets
    {
        public const string Fairway = "Manicured Grass Texture Pack Grass Manicured Grass - Stripes Large Surface";
        public const string Tee = "Manicured Grass Texture Pack Grass Manicured Grass - Stripes Small Surface";
        public const string Green = "Manicured Grass Texture Pack Grass Manicured Grass - Plain Surface";
        public const string Felt = "Manicured Grass Texture Pack Grass Manicured Grass - Plain Surface";
        public const string Bunker = "Sand Surfaces and Beach Decals Sand Sand-01-Small-Ripples Surface";
        public const string Apron = "G87 Urban Surfaces Paving Pack Tiles G87 Paving Slab A Beige Surface";
        public const string Kerb = "Concrete Surface 01";
        public const string TeeLine = "Concrete Surface 02";

        public const string CartPath = "Pavement Path";
        public const string Flag = "SportParkFlag01";
        public const string DiscBasket = "DogParkPole01";
        public const string TeeMarker = "SportParkTrainingCone01";
        public const string Clubhouse = "Gazebo03";
        public const string ProShop = "Kiosk01";
        public const string RangeNet = "FenceIndustrialHigh01";
        public const string LowFence = "FenceResidentialLow01";

        public static AssetRef FairwayRef(string chosen) => Or(chosen, Fairway, "Grass Surface 01");
        public static AssetRef TeeRef(string chosen) => Or(chosen, Tee, "Grass Surface 02", "Grass Surface 01");
        public static AssetRef GreenRef(string chosen) => Or(chosen, Green, "Grass Surface 02", "Grass Surface 01");
        public static AssetRef FeltRef(string chosen) => Or(chosen, Felt, "EAI - Surface Pack Grass Grass Checkered Lawn Surface", "Grass Surface 02");
        public static AssetRef BunkerRef(string chosen) => Or(chosen, Bunker, "EAI - Surface Pack Sand Sand Surface", "Sand Surface 01");
        public static AssetRef ApronRef() => AssetMix.One(Apron, "Tiles Surface 01", "Pavement Surface 02");
        public static AssetRef KerbRef() => AssetMix.One(Kerb, "Concrete Surface 02", "Pavement Surface 01");
        public static AssetRef TeeLineRef() => AssetMix.One(TeeLine, "Concrete Surface 01", "Pavement Surface 01");
        public static AssetRef PathRef(string chosen) => Or(chosen, CartPath);
        public static AssetRef FlagRef(string chosen, bool disc) => disc ? Or(chosen, DiscBasket, "SportParkTrainingPole01") : Or(chosen, Flag, "SportParkTrainingPole01");
        public static AssetRef TeeMarkerRef() => AssetMix.One(TeeMarker, "SportParkTrainingCone02");
        public static AssetRef NetRef(string chosen, bool range) => range ? Or(chosen, RangeNet, "FenceResidentialHigh02") : Or(chosen, LowFence, "FenceResidentialLow02");

        /// <summary>
        /// A golf prop: the player's pick, else the installed golf category (automatic), else the vanilla
        /// stand-in under the category it really belongs to (so "no golf props installed" never plans
        /// golf-category objects the build would drop).
        /// </summary>
        public static (AssetRef asset, string category) Prop(string chosen, string golfCategory, AssetRef standIn, string standInCategory)
        {
            if (!string.IsNullOrEmpty(chosen)) return (AssetMix.One(chosen), golfCategory);
            if (AssetAvailability.Has(golfCategory)) return (AssetMix.One(""), golfCategory);
            return (standIn, standInCategory);
        }

        public static (AssetRef asset, string category) FlagProp(string chosen, bool disc) =>
            Prop(chosen, disc ? AssetCategories.GolfBasket : AssetCategories.GolfFlag, FlagRef("", disc), AssetCategories.Decoration);
        public static (AssetRef asset, string category) TeeMarkerProp() =>
            Prop("", AssetCategories.GolfTee, TeeMarkerRef(), AssetCategories.Sports);

        /// <summary>Default tree species per format (the player's own species list always wins).</summary>
        public static string[] Trees(string subtype)
        {
            switch (subtype)
            {
                case "discGolf": return new[] { "PineTree01", "SpruceTree01", "EuropeanSpruce01", "BirchTree01", "OakTree01" };   // woodland course
                case "drivingRange": return new[] { "EU_PoplarTree01", "NA_LindenTree01" };                                       // screen rows
                case "miniGolf": return new[] { "NA_LondonPlaneTree01", "BirchTree01", "EU_AlderTree01" };
                default: return new[] { "OakTree01", "EU_ChestnutTree01", "NA_LindenTree01", "BirchTree01", "NA_HickoryTree01" }; // parkland specimen trees
            }
        }

        /// <summary>Default shrubs: gorse-like wild greens for courses, flowering ones for mini golf.</summary>
        public static string[] Bushes(string subtype) => subtype == "miniGolf"
            ? new[] { "FlowerBushWild01", "FlowerBushWild02", "GreenBushWild02" }
            : new[] { "GreenBushWild01", "GreenBushWild02", "GreenBushWildAdult01", "FlowerBushWild02" };

        /// <summary>The player's pick, else the art default with its fallbacks.</summary>
        private static AssetRef Or(string chosen, string def, params string[] fallbacks) =>
            string.IsNullOrEmpty(chosen) ? AssetMix.One(def, fallbacks) : AssetMix.One(chosen, def);
    }
}
