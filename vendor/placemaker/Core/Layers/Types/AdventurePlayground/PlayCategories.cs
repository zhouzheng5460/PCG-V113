// Adventure playground asset vocabulary and its classification rules (type wave, builder 9).
// Play equipment kinds are subcategories of "playground": every item is ALSO tagged "playground",
// so a zone asking for swings still builds with whatever play equipment is installed.

using System.Collections.Generic;
using Placemaker.Core.Build;

namespace Placemaker.Core
{
    public static partial class AssetCategories
    {
        public const string PlayClimb = "play.climb";       // climbing frames, jungle gyms, rope nets, play towers
        public const string PlaySwing = "play.swing";       // swings, nest swings
        public const string PlaySlide = "play.slide";       // slides
        public const string PlayToddler = "play.toddler";   // spring riders, sandboxes, see-saws, small carousels
        public const string PlayNature = "play.nature";     // logs, stumps, balance beams (natural play props)
    }
}

namespace Placemaker.Core.Layers.Types.AdventurePlayground
{
    /// <summary>Name rules for play equipment kinds (vanilla CamelCase names, lower-cased).</summary>
    public sealed class PlayCategoryRules : ICategoryRules
    {
        public int Priority => 40;

        private static bool Has(string s, params string[] parts) => AssetHeuristics.ContainsAny(s, parts);

        public bool Classify(string lower, AssetTraits traits, List<string> categories)
        {
            if ((traits & AssetTraits.Plant) != 0 || Has(lower, "dog", "sign", "board", "advert")) return false;
            if (Has(lower, "climb", "junglegym", "jungle gym", "monkeybar", "monkey bar", "ropenet", "rope net", "spidernet", "spider net",
                "playtower", "play tower", "playframe", "play frame"))
            { categories.Add(AssetCategories.Playground); categories.Add(AssetCategories.PlayClimb); return true; }
            if (lower.Contains("swing") && !Has(lower, "door", "gate"))
            { categories.Add(AssetCategories.Playground); categories.Add(AssetCategories.PlaySwing); return true; }
            if (lower.Contains("slide") && !Has(lower, "landslide", "door", "window"))
            { categories.Add(AssetCategories.Playground); categories.Add(AssetCategories.PlaySlide); return true; }
            if (Has(lower, "springrider", "spring rider", "rocker", "sandbox", "sandpit", "sand pit", "seesaw", "see saw", "toddler", "carousel", "merrygoround", "merry-go-round"))
            { categories.Add(AssetCategories.Playground); categories.Add(AssetCategories.PlayToddler); return true; }
            if (IsLog(lower) || Has(lower, "stump", "balancebeam", "balance beam", "steppingstone", "stepping stone", "steppingpost"))
            { categories.Add(AssetCategories.PlayNature); return true; }
            return false;
        }

        /// <summary>"log" as a word-ish part, never "logo", "catalog", "logistics"…</summary>
        internal static bool IsLog(string lower) =>
            lower.Contains("log") && !Has(lower, "logo", "catalog", "logist", "blog", "logic", "analog", "dialog");

        public bool? AutoEligible(string category, string lower, AssetTraits traits)
        {
            switch (category)
            {
                case AssetCategories.PlayClimb:
                case AssetCategories.PlaySwing:
                case AssetCategories.PlaySlide:
                case AssetCategories.PlayToddler:
                    return !Has(lower, "dog", "sign", "advert", "logo", "placeholder", "random", "broken");
                case AssetCategories.PlayNature:
                    return !Has(lower, "pile", "truck", "trailer", "placeholder", "random", "sign");
                default:
                    return null;
            }
        }
    }
}
