// Asset vocabulary and classification for the animal park: animal shelters (stables, hutches,
// coops, field shelters), water troughs and feeders, hay, aviaries and show jumps. Real prefab
// names are unknown until the catalog dump exists; the rules match broad vanilla-style words and
// the layer always falls back (shelter → barn → shed → pavilion) or skips with an Info issue.

using System.Collections.Generic;
using Placemaker.Core.Build;

namespace Placemaker.Core
{
    public static partial class AssetCategories
    {
        public const string AnimalParkShelter = "animalpark.shelter";   // stables, hutches, coops, sties, field shelters
        public const string AnimalParkTrough = "animalpark.trough";     // water troughs, feeders, drinkers
        public const string AnimalParkHay = "animalpark.hay";           // hay bales, straw, hay racks
        public const string AnimalParkAviary = "animalpark.aviary";     // aviaries, bird cages, dovecotes
        public const string AnimalParkJump = "animalpark.jump";         // show jumps, cavaletti, horse jump poles
    }
}

namespace Placemaker.Core.Layers.Types.AnimalPark
{
    public sealed class AnimalParkCategoryRules : ICategoryRules
    {
        public int Priority => 41;

        private static bool Has(string s, params string[] parts) => AssetHeuristics.ContainsAny(s, parts);

        private static bool Junk(string lower) =>
            Has(lower, "advert", "billboard", "logo", "placeholder", "signboard", "poster", "banner");

        public bool Classify(string lower, AssetTraits traits, List<string> categories)
        {
            if (Junk(lower)) return false;
            if (Has(lower, "aviary", "birdcage", "bird cage", "dovecote", "dove cote", "birdhouse", "bird house", "pigeonloft", "pigeon loft", "volary"))
            { categories.Add(AssetCategories.AnimalParkAviary); return true; }
            if (Has(lower, "showjump", "show jump", "horsejump", "horse jump", "oxer", "cavaletti", "jumppole", "jump pole", "equestrianjump"))
            { categories.Add(AssetCategories.AnimalParkJump); return true; }
            if (Has(lower, "trough", "drinker", "animalfeeder", "animal feeder", "livestockfeeder", "feedbunk", "feed bunk"))
            { categories.Add(AssetCategories.AnimalParkTrough); return true; }
            if (Has(lower, "haybale", "hay bale", "hayrack", "hay rack", "haystack", "hay stack", "straw", "silagebale", "silage bale") ||
                lower.StartsWith("hay", System.StringComparison.Ordinal))
            { categories.Add(AssetCategories.AnimalParkHay); return true; }
            if (Has(lower, "stable", "hutch", "chickencoop", "chicken coop", "henhouse", "hen house", "pigsty", "pig sty", "pigpen", "pig pen",
                    "fieldshelter", "field shelter", "animalshelter", "animal shelter", "goathouse", "goat house", "loosebox", "loose box") ||
                lower.EndsWith("coop", System.StringComparison.Ordinal))
            { categories.Add(AssetCategories.AnimalParkShelter); return true; }
            return false;
        }

        public bool? AutoEligible(string category, string lower, AssetTraits traits)
        {
            switch (category)
            {
                case AssetCategories.AnimalParkShelter:
                case AssetCategories.AnimalParkTrough:
                case AssetCategories.AnimalParkHay:
                case AssetCategories.AnimalParkAviary:
                case AssetCategories.AnimalParkJump:
                    return !Junk(lower) && !Has(lower, "broken", "burnt", "ruin", "random");
                default:
                    return null;
            }
        }
    }
}

namespace Placemaker.Core.Layers.Types.AnimalPark
{
    using Placemaker.Core.Layers.Common;

    /// <summary>
    /// Art-pass defaults (2026-10-13), verified against the player's catalog dump. Vanilla has no animal
    /// park props, and the automatic chains reached the wrong things: a 19 m barn per pen, crates and
    /// gas bottles as "hay", a 55 m fire watch tower as the deer park hide, a brick wall round the
    /// grounds. Each default is an explicit id with fallbacks; installed animal-park assets (or the
    /// player's pick) still win.
    /// </summary>
    internal static class AnimalParkArt
    {
        public const string Hedge = "HedgeHigh01";             // Tall Natural Hedge (fence net)
        public const string WoodFence = "FenceResidentialHigh01"; // Tall Wood Fence
        public const string Straw = "Farming Surfaces & Decals Ground Haristess - 2-1 - StrawFlat1 Surface";
        public const string Yard = "EDT Cobblestone & Gravel Surface Texture Pack Rock Small Gravel Surface";
        public const string ArenaSand = "EAI - Surface Pack Sand Sand Surface";

        public static AssetRef Boundary(string chosen, bool hedge) => !string.IsNullOrEmpty(chosen) ? AssetMix.One(chosen)
            : hedge ? AssetMix.One(Hedge, "HedgeHigh02", WoodFence) : AssetMix.One(WoodFence, "FenceResidentialLow01");

        /// <summary>Pen fences: picket for petting pens, post-and-rail wood for paddocks and deer, low metal for aviaries.</summary>
        public static AssetRef PenFence(string chosen, string kind)
        {
            if (!string.IsNullOrEmpty(chosen)) return AssetMix.One(chosen);
            switch (kind)
            {
                case "petting": return AssetMix.One("FenceResidentialLow01", "FenceIndustrialLow01");
                case "aviary": return AssetMix.One("FenceResidentialLow02", "FenceResidentialLow01");
                case "deer": return AssetMix.One("FenceResidentialHigh01", "FenceIndustrialLow01");
                default: return AssetMix.One("FenceIndustrialLow01", "FenceResidentialLow01");
            }
        }

        /// <summary>Shelter stand-in sized like a hutch (petting), field shelter (grazing) or hay shelter (deer).</summary>
        public static AssetRef ShelterStandIn(string kind)
        {
            switch (kind)
            {
                case "petting": return AssetMix.One("ToolShed03", "FirewoodShelter02");
                case "aviary": return AssetMix.One("ToolShed02", "ToolShed03");
                case "deer": return AssetMix.One("FirewoodShelter02", "CampgroundShelter01");
                // Open wooden field shelter; the campground shelter read as a black dome in the paddocks (rf-farms).
                default: return AssetMix.One("FirewoodShelter02", "ToolShed03");
            }
        }

        public static readonly AssetRef Hay = AssetMix.One("HeyBale02", "HeyBale03", "HeyBale01");
        public static readonly AssetRef Aviary = AssetMix.One("Gazebo01", "Gazebo02", "ChirperGazebo01");
        public static readonly AssetRef Jump = AssetMix.One("DogParkJumpFence01", "DogParkJumpFence02");
        public static readonly AssetRef Hide = AssetMix.One("CampgroundShelter01", "CampgroundShelter02");
        public static readonly AssetRef PicnicTable = AssetMix.One("CampgroundTableset01", "CampgroundTableset02");

        public static string[] Trees(string kind)
        {
            switch (kind)
            {
                case "deer": return new[] { "OakTree01", "BirchTree01", "EU_AlderTree01", "SpruceTree01", "EU_ChestnutTree01" };
                case "aviary": return new[] { "NA_LondonPlaneTree01", "BirchTree01", "AppleTree01" };
                default: return new[] { "OakTree01", "NA_LindenTree01", "EU_ChestnutTree01", "AppleTree01" };
            }
        }

        public static readonly string[] HedgerowShrubs = { "GreenBushWild01", "GreenBushWild02", "FlowerBushWild01", "FlowerBushWild02", "GreenBushWildAdult01" };

        /// <summary>The player's pick, else the installed category (automatic), else the stand-in under its own category.</summary>
        public static (AssetRef asset, string category) Prop(string chosen, string category, AssetRef standIn, string standInCategory)
        {
            if (!string.IsNullOrEmpty(chosen)) return (AssetMix.One(chosen), category);
            if (AssetAvailability.Has(category)) return (AssetMix.One(""), category);
            return (standIn, standInCategory);
        }
    }
}
