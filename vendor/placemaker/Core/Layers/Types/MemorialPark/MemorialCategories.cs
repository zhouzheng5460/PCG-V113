// Memorial park asset vocabulary and its classification rules (type wave, builder 9).
// Monuments and sculptures are also "centerpiece" + "centerpiece.statue" so they behave exactly as
// the leisure rules would have classified them; low path lights are also "lamp".

using System.Collections.Generic;
using Placemaker.Core.Build;

namespace Placemaker.Core
{
    public static partial class AssetCategories
    {
        public const string MemorialMonument = "memorial.monument";   // cenotaphs, obelisks, monuments, statues
        public const string MemorialSculpture = "memorial.sculpture"; // sculptures, art installations, statues
        public const string MemorialPlaque = "memorial.plaque";       // plaques, wreaths, eternal flames
        public const string MemorialFlag = "memorial.flag";           // flagpoles
        public const string LampLow = "lamp.low";                     // bollard / path / ground lights, uplights
    }
}

namespace Placemaker.Core.Layers.Types.MemorialPark
{
    public sealed class MemorialCategoryRules : ICategoryRules
    {
        public int Priority => 40;

        private static bool Has(string s, params string[] parts) => AssetHeuristics.ContainsAny(s, parts);

        public bool Classify(string lower, AssetTraits traits, List<string> categories)
        {
            if ((traits & AssetTraits.Plant) != 0 || Has(lower, "advert", "billboard", "logo")) return false;
            // Small specific things first ("MemorialPlaque" is a plaque, not a monument).
            if (Has(lower, "plaque", "wreath", "eternalflame", "eternal flame"))
            { categories.Add(AssetCategories.MemorialPlaque); return true; }
            if (Has(lower, "flagpole", "flag pole", "flagmast", "flag mast", "flagstaff"))
            { categories.Add(AssetCategories.MemorialFlag); return true; }
            if (Has(lower, "bollardlight", "bollard light", "pathlight", "path light", "gardenlight", "garden light", "groundlight", "ground light",
                "uplight", "spotlight", "lowlight", "low light"))
            { categories.Add(AssetCategories.Lamp); categories.Add(AssetCategories.LampLow); return true; }
            bool standalone = (traits & AssetTraits.HasSubObjects) == 0 && (traits & AssetTraits.HasIcon) != 0;
            if (standalone && !Has(lower, "fountain", "grave", "tomb", "headstone", "memorialstone", "memorial stone", "sign"))
            {
                bool monument = Has(lower, "cenotaph", "obelisk", "monument", "memorial", "statue", "bust");
                bool sculpture = Has(lower, "sculpture", "statue", "artwork", "art installation", "abstract");
                if (monument || sculpture)
                {
                    categories.Add(AssetCategories.Centerpiece);
                    categories.Add(AssetCategories.CenterpieceStatue);
                    if (monument) categories.Add(AssetCategories.MemorialMonument);
                    if (sculpture) categories.Add(AssetCategories.MemorialSculpture);
                    return true;
                }
            }
            return false;
        }

        public bool? AutoEligible(string category, string lower, AssetTraits traits)
        {
            switch (category)
            {
                case AssetCategories.MemorialMonument:
                case AssetCategories.MemorialSculpture:
                case AssetCategories.MemorialPlaque:
                case AssetCategories.MemorialFlag:
                    return !Has(lower, "advert", "logo", "billboard", "screen", "placeholder", "dog", "drinking", "random");
                case AssetCategories.LampLow:
                    return !Has(lower, "flood", "stadium", "sport", "neon", "christmas", "placeholder", "random");
                default:
                    return null;
            }
        }
    }
}
