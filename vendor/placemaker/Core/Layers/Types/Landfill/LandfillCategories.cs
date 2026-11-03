// Asset categories and name rules for the landfill type: garbage (bags, rubbish heaps), skips and
// dumpsters, material piles (soil, compost, gravel), bales of recyclables and landfill-gas vents.
// Street litter bins stay in the general "bin" pool: a rubbish heap is not a bin and vice versa.

using System.Collections.Generic;
using Placemaker.Core.Build;

namespace Placemaker.Core
{
    public static partial class AssetCategories
    {
        public const string LandfillGarbage = "industrial.garbage";   // garbage bags, rubbish heaps, litter piles
        public const string LandfillSkip = "industrial.skip";         // skips, dumpsters, bottle banks, recycling containers
        public const string LandfillPile = "industrial.pile";         // soil, compost, gravel, sand heaps
        public const string LandfillBale = "industrial.bale";         // bales of cardboard, plastic, hay
        public const string LandfillVent = "industrial.vent";         // landfill-gas wells, vent pipes, flares
    }
}

namespace Placemaker.Core.Layers.Types.Landfill
{
    public static class LandfillCategories
    {
        public static readonly string[] All =
        {
            AssetCategories.LandfillGarbage, AssetCategories.LandfillSkip, AssetCategories.LandfillPile,
            AssetCategories.LandfillBale, AssetCategories.LandfillVent,
        };
    }

    /// <summary>Name rules for the landfill categories (after the scrapyard rules: tyre piles are tyres).</summary>
    public sealed class LandfillCategoryRules : ICategoryRules
    {
        public int Priority => 45;

        private static bool Has(string s, params string[] parts) => AssetHeuristics.ContainsAny(s, parts);

        public bool Classify(string lower, AssetTraits traits, List<string> categories)
        {
            if ((traits & (AssetTraits.Plant | AssetTraits.Tree | AssetTraits.Building)) != 0) return false;
            // "EU_Railing01ExtraShort" is no trash: drop the "extrashort" length suffix before word matching.
            lower = lower.Replace("extrashort", "extra short");

            if (Has(lower, "dumpster", "skip container", "skipcontainer", "skip bin", "skipbin", "roll-off", "rolloff",
                    "garbagecontainer", "garbage container", "wastecontainer", "waste container", "trashcontainer", "trash container",
                    "recyclingcontainer", "recycling container", "recyclingbin", "recycling bin", "bottlebank", "bottle bank",
                    "glasscontainer", "glass container", "clothescontainer", "clothes container", "underground container")
                || lower.StartsWith("skip", System.StringComparison.Ordinal) || lower.Contains(" skip") || lower.Contains("_skip"))
            { categories.Add(AssetCategories.LandfillSkip); return true; }

            // Small street bins belong to the general bin pool.
            if (Has(lower, "trashcan", "trash can", "garbagecan", "garbage can", "litterbin", "litter bin", "wastebin", "waste bin",
                    "trashbin", "trash bin", "garbagebin", "garbage bin", "wastebasket", "dogbin", "dog bin")) return false;

            if (Has(lower, "garbage", "trash", "rubbish", "litter", "refuse", "binbag", "bin bag", "trashbag", "trash bag", "waste pile", "wastepile", "mattress"))
            { categories.Add(AssetCategories.LandfillGarbage); return true; }
            if (Has(lower, "bale", "cardboard", "paperstack", "paper stack", "plasticbundle", "plastic bundle"))
            { categories.Add(AssetCategories.LandfillBale); return true; }
            if (Has(lower, "gaswell", "gas well", "flare stack", "flarestack", "gas flare", "standpipe", "vent pipe", "ventpipe", "landfill vent", "landfillvent")
                || (lower.Contains("vent") && !Has(lower, "event", "advent", "prevent", "invent", "ventil")))
            { categories.Add(AssetCategories.LandfillVent); return true; }
            if ((Has(lower, "pile", "heap", "mound", "stockpile") && !Has(lower, "compostbin", "compost bin", "woodpile pallet"))
                || Has(lower, "compost", "topsoil", "mulch"))
            {
                if (Has(lower, "compostbin", "compost bin")) return false;
                categories.Add(AssetCategories.LandfillPile); return true;
            }
            return false;
        }

        public bool? AutoEligible(string category, string lower, AssetTraits traits)
        {
            if (System.Array.IndexOf(LandfillCategories.All, category) < 0) return null;
            return !Has(lower, "test", "chirper", "logo", "advert", "billboard");
        }
    }
}
