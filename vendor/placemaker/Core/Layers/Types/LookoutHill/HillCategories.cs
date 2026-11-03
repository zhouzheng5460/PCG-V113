// Lookout-hill asset vocabulary: viewpoint telescopes, information / map boards, sledding run-out
// props (straw bales) and kite-flying props (kites, windsocks). All optional: the hill reports an
// Info issue and leaves the spot empty when nothing of a category is installed.

using System.Collections.Generic;
using Placemaker.Core.Build;

namespace Placemaker.Core
{
    public static partial class AssetCategories
    {
        public const string HillTelescope = "lookout.telescope";   // coin telescopes, binocular viewers
        public const string HillBoard = "lookout.board";           // information / panorama / map boards
        public const string HillBale = "lookout.bale";             // straw / hay bales (sledding run-out)
        public const string HillKite = "lookout.kite";             // kites, windsocks
    }
}

namespace Placemaker.Core.Layers.Types.LookoutHill
{
    public sealed class HillCategoryRules : ICategoryRules
    {
        public int Priority => 40;

        private static readonly (string category, string[] names)[] Rules =
        {
            (AssetCategories.HillTelescope, new[] { "telescope", "binocular", "coinviewer", "coin viewer", "viewfinder", "touristviewer" }),
            (AssetCategories.HillBoard, new[] { "infoboard", "info board", "informationboard", "information board", "mapboard", "map board",
                "panoramaboard", "panorama board", "trailmap", "trail map", "touristinfo", "tourist info" }),
            (AssetCategories.HillBale, new[] { "haybale", "hay bale", "strawbale", "straw bale", "hay_bale", "straw_bale", "baleofhay" }),
            (AssetCategories.HillKite, new[] { "windsock", "wind sock", "kiteflying", "kite0", "kite_", "kites" }),
        };

        private static readonly string[] Excluded = { "observatory", "building", "vehicle", "truck", "trailer" };

        public bool Classify(string lower, AssetTraits traits, List<string> categories)
        {
            if (AssetHeuristics.ContainsAny(lower, Excluded)) return false;
            foreach (var (cat, names) in Rules)
            {
                if (!AssetHeuristics.ContainsAny(lower, names)) continue;
                categories.Add(cat);
                return true;
            }
            return false;
        }

        public bool? AutoEligible(string category, string lower, AssetTraits traits)
        {
            foreach (var (cat, names) in Rules)
                if (cat == category) return AssetHeuristics.ContainsAny(lower, names) && !AssetHeuristics.ContainsAny(lower, Excluded);
            return null;
        }
    }
}
