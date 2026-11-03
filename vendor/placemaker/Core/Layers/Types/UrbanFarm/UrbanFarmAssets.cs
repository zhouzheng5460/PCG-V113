// Asset vocabulary and classification for the urban farm (and the farm add-ons it shares with the
// animal park). Real prefab names are not known yet (no catalog dump), so the rules match broad
// vanilla-style CamelCase words. Every layer asks for these through a fallback chain
// ("a vine, else a hedge piece, else a bush"), so an empty category only thins the look.

using System.Collections.Generic;
using Placemaker.Core.Build;

namespace Placemaker.Core
{
    public static partial class AssetCategories
    {
        public const string CityFarmVine = "urbanfarm.vine";            // grapevines, hop bines, trained vines
        public const string CityFarmLavender = "urbanfarm.lavender";    // lavender, heather, purple field flowers
        public const string CityFarmTrellis = "urbanfarm.trellis";      // trellis frames, stakes, bean poles, vine posts
        public const string CityFarmBarn = "urbanfarm.barn";            // barns, farm sheds, packing sheds as props
        public const string CityFarmProp = "urbanfarm.prop";            // crates, pallets, sacks, barrels, wheelbarrows
        public const string CityFarmMachine = "urbanfarm.machine";      // tractors, ploughs, farm trailers as props
        public const string CityFarmScarecrow = "urbanfarm.scarecrow";
        public const string CityFarmBeehive = "urbanfarm.beehive";      // beehives, apiary boxes
    }
}

namespace Placemaker.Core.Layers.Types.UrbanFarm
{
    /// <summary>Name rules for the urban-farm categories (runs before the built-in heuristics).</summary>
    public sealed class UrbanFarmCategoryRules : ICategoryRules
    {
        public int Priority => 40;

        private static bool Has(string s, params string[] parts) => AssetHeuristics.ContainsAny(s, parts);

        private static bool Junk(string lower) =>
            Has(lower, "advert", "billboard", "logo", "placeholder", "signboard", "poster", "banner");

        public bool Classify(string lower, AssetTraits traits, List<string> categories)
        {
            if (Junk(lower)) return false;
            if (Has(lower, "beehive", "bee hive", "apiary", "honeybox", "honey box"))
            { categories.Add(AssetCategories.CityFarmBeehive); return true; }
            if (Has(lower, "scarecrow")) { categories.Add(AssetCategories.CityFarmScarecrow); return true; }
            if (Has(lower, "lavender", "heather"))
            { categories.Add(AssetCategories.CityFarmLavender); return true; }
            if (Has(lower, "grapevine", "grape vine", "vineyard", "grape", "vinerow", "vine row") || lower.StartsWith("vine", System.StringComparison.Ordinal))
            { categories.Add(AssetCategories.CityFarmVine); return true; }
            if (Has(lower, "trellis", "beanpole", "bean pole", "vinepost", "vine post", "plantstake", "plant stake", "espalier"))
            { categories.Add(AssetCategories.CityFarmTrellis); return true; }
            if (Has(lower, "tractor", "plough", "plow", "harvester", "farmtrailer", "farm trailer", "haywagon", "hay wagon", "cultivator", "seeder"))
            { categories.Add(AssetCategories.CityFarmMachine); return true; }
            if (Has(lower, "barn", "farmshed", "farm shed", "packingshed", "packing shed", "pole shed", "poleshed"))
            { categories.Add(AssetCategories.CityFarmBarn); return true; }
            if (Has(lower, "crate", "pallet", "sack", "wheelbarrow", "barrel", "wateringcan", "watering can", "produce", "harvestbox", "harvest box", "milkchurn", "milk churn"))
            { categories.Add(AssetCategories.CityFarmProp); return false; }
            return false;
        }

        public bool? AutoEligible(string category, string lower, AssetTraits traits)
        {
            switch (category)
            {
                case AssetCategories.CityFarmVine:
                case AssetCategories.CityFarmLavender:
                case AssetCategories.CityFarmTrellis:
                case AssetCategories.CityFarmBarn:
                case AssetCategories.CityFarmProp:
                case AssetCategories.CityFarmMachine:
                case AssetCategories.CityFarmScarecrow:
                case AssetCategories.CityFarmBeehive:
                    return !Junk(lower) && !Has(lower, "broken", "burnt", "ruin", "random");
                default:
                    return null;
            }
        }
    }
}
