// Wetland asset vocabulary (type wave, builder 6a). Names are guesses from vanilla CS2 CamelCase
// conventions until catalog-dump.json exists; every layer that asks for one of these falls back to
// a general category (reed → bush, wet tree → tree, hide → lookout → shelter) and reports an Info
// issue when nothing is installed.

using System.Collections.Generic;
using Placemaker.Core.Build;

namespace Placemaker.Core
{
    public static partial class AssetCategories
    {
        public const string WetlandReed = "wetland.reed";         // reeds, cattails, rushes, sedges (also bush)
        public const string WetlandTree = "tree.wet";             // willows, alders, birches, poplars (also tree)
        public const string WetlandHide = "wetland.hide";         // bird hides, observation huts, hunting stands
        public const string WetlandNestBox = "wetland.nestbox";   // bird / bat boxes, insect hotels
    }
}

namespace Placemaker.Core.Layers.Types.Wetland
{
    public sealed class WetlandCategoryRules : ICategoryRules
    {
        public int Priority => 60;

        private static bool Has(string s, params string[] parts) => AssetHeuristics.ContainsAny(s, parts);

        private static readonly string[] ReedNames = { "reed", "cattail", "bulrush", "rushes", "sedge", "papyrus", "typha", "marshgrass", "marsh grass", "waterplant", "water plant" };
        private static readonly string[] HideNames = { "birdhide", "bird hide", "birdwatch", "bird watch", "observationhut", "observation hut", "huntingstand", "hunting stand", "deerstand", "deer stand", "huntingtower", "hunting tower", "fishinghut", "fishing hut" };
        private static readonly string[] WetTreeNames = { "willow", "alder", "birch", "poplar", "swamp", "mangrove", "cypress" };
        private static readonly string[] NestNames = { "birdhouse", "bird house", "nestbox", "nest box", "birdbox", "bird box", "insecthotel", "insect hotel", "bughotel", "bug hotel", "batbox", "bat box" };

        public bool Classify(string lower, AssetTraits traits, List<string> categories)
        {
            if (Has(lower, NestNames)) { categories.Add(AssetCategories.WetlandNestBox); return true; }
            if (Has(lower, HideNames) && !Has(lower, "window")) { categories.Add(AssetCategories.WetlandHide); return true; }
            bool plant = (traits & AssetTraits.Plant) != 0;
            if (Has(lower, ReedNames) && !Has(lower, "breed", "greed", "freed"))
            {
                categories.Add(AssetCategories.WetlandReed);
                // Plants also go on to the general vegetation rules (reeds are bushes too); props stop here.
                return !plant;
            }
            if (plant && (traits & AssetTraits.Tree) != 0 && Has(lower, WetTreeNames)) categories.Add(AssetCategories.WetlandTree);
            return false;
        }

        public bool? AutoEligible(string category, string lower, AssetTraits traits)
        {
            switch (category)
            {
                case AssetCategories.WetlandReed:
                case AssetCategories.WetlandTree:
                    return !Has(lower, "dead", "stump", "fallen", "pot", "planter", "box");
                case AssetCategories.WetlandHide:
                case AssetCategories.WetlandNestBox:
                    return !Has(lower, "random", "placeholder", "advert", "brand");
                default:
                    return null;
            }
        }
    }
}
