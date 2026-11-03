// Name-based classification for the farm categories (see FarmCategories.cs). Runs before the
// built-in leisure/general heuristics (CategoryRules plug-in). Specific farm structures (barn, silo,
// beehive, fruit tree) stop classification; shared props (hay, crates, troughs, crops) only add
// their farm category and let the other rules carry on, so a crate is still a crate for the
// logistics yard and a crop is still a garden crop for the allotments.

using System.Collections.Generic;

namespace Placemaker.Core.Build
{
    public sealed class FarmCategoryRules : ICategoryRules
    {
        public int Priority => 30;

        private static bool Has(string s, params string[] parts) => AssetHeuristics.ContainsAny(s, parts);

        private static readonly string[] FruitNames =
        {
            "apple", "cherry", "pear", "plum", "peach", "apricot", "olive", "orange", "lemon", "lime", "citrus",
            "mandarin", "fig", "almond", "sakura", "blossom", "fruit", "orchard", "quince", "walnut",
        };

        public bool Classify(string lower, AssetTraits t, List<string> r)
        {
            if ((t & AssetTraits.Plant) != 0)
            {
                if ((t & AssetTraits.Tree) != 0 && Has(lower, FruitNames) && !Has(lower, "stump", "dead", "fallen", "log", "pineapple"))
                {
                    r.Add(AssetCategories.Tree);
                    r.Add(AssetCategories.FruitTree);
                    return true;
                }
                if (Has(lower, "wheat", "maize", "corn", "barley", "sunflower", "rapeseed", "canola", "soybean", "hops"))
                {
                    r.Add(AssetCategories.FieldCrop);
                    r.Add(AssetCategories.GardenCrop);
                    return true;
                }
                return false;
            }
            if (Has(lower, "beehive", "bee hive", "apiary", "beebox", "bee box") || lower.EndsWith("hive"))
            { r.Add(AssetCategories.Beehive); return true; }
            if (Has(lower, "silo", "grainbin", "grain bin", "feedbin", "feed bin", "hopper"))
            { r.Add(AssetCategories.FarmSilo); return true; }
            if (Has(lower, "barn", "stable", "cowshed", "cow shed", "byre", "hayloft", "machineshed", "machine shed", "farmshed", "farm shed", "pigsty", "chickencoop", "chicken coop", "henhouse"))
            { r.Add(AssetCategories.FarmBarn); return true; }
            if (Has(lower, "tractor", "harvester", "combine", "plough", "plow", "baler", "seeder", "cultivator", "harrow", "farmtrailer", "farm trailer", "agricultur"))
            { if (!lower.Contains("snow")) { r.Add(AssetCategories.FarmMachinery); return true; } }
            if (Has(lower, "windpump", "wind pump", "windmill") && !Has(lower, "turbine"))
            { r.Add(AssetCategories.FarmWindpump); return true; }

            // Shared props: add the farm category, let the general rules classify them too.
            if (Has(lower, "hay", "bale", "straw")) r.Add(AssetCategories.FarmHay);
            if (Has(lower, "trough", "feeder", "manger")) r.Add(AssetCategories.FarmTrough);
            // Not basketballs, bicycle baskets or roof ladders (catalog-dump: "Basketball01", "Bicycle02Basket01", "LadderRoof01").
            if ((Has(lower, "fruitcrate", "fruit crate", "applecrate", "apple crate", "basket", "produce", "harvest", "ladder", "woodencrate", "wooden crate"))
                && !Has(lower, "basketball", "bicycle", "bike", "roof", "pool", "harvester"))
                r.Add(AssetCategories.FruitCrate);
            if (Has(lower, "wheat", "maize", "sunflower", "cabbage", "lettuce", "pumpkin", "crop", "vegetable") || AssetCuration.HasWord(lower, "corn"))   // "OutdoorCornerSofa" is no corn
                r.Add(AssetCategories.FieldCrop);
            return false;
        }

        public bool? AutoEligible(string category, string lower, AssetTraits traits)
        {
            switch (category)
            {
                case AssetCategories.FarmBarn:
                case AssetCategories.FarmSilo:
                case AssetCategories.FarmMachinery:
                case AssetCategories.FarmWindpump:
                case AssetCategories.Beehive:
                    return !Has(lower, "advert", "billboard", "logo", "banner", "placeholder", "sign", "broken", "ruin");
                case AssetCategories.FarmHay:
                case AssetCategories.FarmTrough:
                case AssetCategories.FruitCrate:
                case AssetCategories.FieldCrop:
                    // A field crop is a plant, never a raised-bed corner piece: vanilla "GardenBed01CornerHigh" read as
                    // rows of grey boxes across every farmstead field (rf-farms, 2026-10-16).
                    return !Has(lower, "advert", "logo", "placeholder", "gardenbed", "garden bed", "planter");
                case AssetCategories.FruitTree:
                    return !Has(lower, "dead", "stump", "placeholder");
                default:
                    return null;
            }
        }
    }
}
