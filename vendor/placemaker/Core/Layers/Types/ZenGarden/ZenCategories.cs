// Zen garden asset vocabulary (type wave, builder 6b). Name patterns follow vanilla CS2 CamelCase
// guesses until catalog-dump.json exists. Each has a fallback in the layer: stone lantern → lamp,
// pruned tree → tree, bamboo → bush, tea house → shelter, stepping stone → small rock; the water
// basin has none (skipped with an Info issue).

using System.Collections.Generic;
using Placemaker.Core.Build;

namespace Placemaker.Core
{
    public static partial class AssetCategories
    {
        public const string ZenLantern = "zen.lantern";             // stone / japanese garden lanterns (also lamp)
        public const string ZenTree = "tree.pruned";                // pines, maples, cherries, junipers (also tree)
        public const string ZenBamboo = "zen.bamboo";               // bamboo clumps (also bush/tree)
        public const string ZenSteppingStone = "zen.steppingStone"; // stepping stones, flagstones, stone slabs
        public const string ZenTeaHouse = "zen.teahouse";           // tea houses, pavilions, small shrines
        public const string ZenBasin = "zen.basin";                 // tsukubai, stone water basins
    }
}

namespace Placemaker.Core.Layers.Types.ZenGarden
{
    public sealed class ZenCategoryRules : ICategoryRules
    {
        public int Priority => 61;

        private static bool Has(string s, params string[] parts) => AssetHeuristics.ContainsAny(s, parts);

        private static readonly string[] LanternNames = { "stonelantern", "stone lantern", "japaneselantern", "japanese lantern", "gardenlantern", "garden lantern", "tourou", "ishidoro", "kasuga", "yukimi", "pagodalantern", "pagoda lantern" };
        private static readonly string[] SteppingNames = { "steppingstone", "stepping stone", "stepstone", "step stone", "flagstone", "stoneslab", "stone slab", "stoneplate", "stone plate", "pavingstone", "paving stone" };
        private static readonly string[] TeaHouseNames = { "teahouse", "tea house", "japanesehouse", "japanese house", "azumaya", "japanesepavilion", "japanese pavilion", "shrine", "pagoda" };
        private static readonly string[] BasinNames = { "tsukubai", "chozubachi", "waterbasin", "water basin", "stonebasin", "stone basin" };
        private static readonly string[] PrunedTreeNames = { "pine", "maple", "cherry", "sakura", "bonsai", "juniper", "japanese", "acer", "ginkgo", "plum" };

        public bool Classify(string lower, AssetTraits traits, List<string> categories)
        {
            bool plant = (traits & AssetTraits.Plant) != 0;
            if (Has(lower, BasinNames)) { categories.Add(AssetCategories.ZenBasin); return true; }
            if (Has(lower, SteppingNames) && !plant) { categories.Add(AssetCategories.ZenSteppingStone); return true; }
            if (Has(lower, LanternNames) && !Has(lower, "pagoda lantern string", "string", "hanging"))
            {
                // Garden lanterns are lamps too (general rules add "lamp" for "lantern").
                categories.Add(AssetCategories.ZenLantern);
                return false;
            }
            if (Has(lower, TeaHouseNames) && !plant && !Has(lower, "lantern")) { categories.Add(AssetCategories.ZenTeaHouse); return true; }
            if (lower.Contains("bamboo"))
            {
                // Bamboo fences and screens are fences, handled by the general rules.
                if (Has(lower, "fence", "wall", "screen", "gate", "pole", "scaffold")) return false;
                categories.Add(AssetCategories.ZenBamboo);
                return !plant;
            }
            if (plant && (traits & AssetTraits.Tree) != 0 && Has(lower, PrunedTreeNames) && !Has(lower, "pineapple"))
                categories.Add(AssetCategories.ZenTree);
            return false;
        }

        public bool? AutoEligible(string category, string lower, AssetTraits traits)
        {
            switch (category)
            {
                case AssetCategories.ZenTree:
                case AssetCategories.ZenBamboo:
                    return !Has(lower, "dead", "stump", "fallen", "pot", "planter", "box");
                case AssetCategories.ZenLantern:
                case AssetCategories.ZenSteppingStone:
                case AssetCategories.ZenTeaHouse:
                case AssetCategories.ZenBasin:
                    return !Has(lower, "random", "placeholder", "advert", "brand", "sign");
                default:
                    return null;
            }
        }
    }
}
