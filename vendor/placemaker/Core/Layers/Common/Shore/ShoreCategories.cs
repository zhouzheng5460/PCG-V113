// Asset vocabulary of the shore kit (grand park wave, g2). Names are broad vanilla-style CamelCase
// guesses until the catalog dump exists; every caller asks with a fallback chain (moored boat →
// beach boat, boathouse → beach hut → shelter, cargo → crate → nautical) and degrades to an Info
// issue when nothing is installed.

using System.Collections.Generic;
using Placemaker.Core.Build;

namespace Placemaker.Core
{
    public static partial class AssetCategories
    {
        public const string ShoreBoat = "shore.boat";           // moored boats on the water: sailboats, yachts, motor and fishing boats
        public const string ShoreBoathouse = "shore.boathouse"; // boathouses, boat sheds, rowing club huts (buildings as props)
        public const string ShoreCargo = "shore.cargo";         // quay cargo: crates, barrels, nets, rope coils, fish boxes
    }
}

namespace Placemaker.Core.Layers.Common.Shore
{
    public sealed class ShoreCategoryRules : ICategoryRules
    {
        public int Priority => 55;

        private static bool Has(string s, params string[] parts) => AssetHeuristics.ContainsAny(s, parts);

        private static readonly string[] BoatNames =
        {
            "sailboat", "sail boat", "sailingboat", "yacht", "motorboat", "motor boat", "speedboat", "speed boat", "fishingboat", "fishing boat",
            "fishingvessel", "trawler", "houseboat", "mooredboat", "moored boat", "smallboat", "small boat", "dinghy", "rowboat", "row boat", "skiff",
        };
        private static readonly string[] BoathouseNames = { "boathouse", "boat house", "boatshed", "boat shed", "rowingclub", "rowing club", "boatrental", "boat rental" };
        private static readonly string[] CargoNames = { "fishbox", "fish box", "fishcrate", "barrel", "ropecoil", "rope coil", "fishingnet", "fishing net", "cargo", "lobsterpot", "lobster pot" };

        public bool Classify(string lower, AssetTraits traits, List<string> categories)
        {
            if (Has(lower, BoathouseNames)) { categories.Add(AssetCategories.ShoreBoathouse); return true; }
            if (Has(lower, BoatNames) && !Has(lower, "trailer", "ramp", "lift", "billboard", "sign", "toy"))
            {
                categories.Add(AssetCategories.ShoreBoat);
                return false;   // a rowing boat is also a beach boat for the beach rules
            }
            if (Has(lower, CargoNames) && !Has(lower, "container ship", "containership", "truck")) { categories.Add(AssetCategories.ShoreCargo); return false; }
            return false;
        }

        public bool? AutoEligible(string category, string lower, AssetTraits traits)
        {
            if (category == null || !category.StartsWith("shore.")) return null;
            if ((traits & (AssetTraits.BrandObject | AssetTraits.WallOrHanging)) != 0) return false;
            return !Has(lower, "random", "placeholder", "advert", "wreck", "sunken", "burnt");
        }
    }
}
