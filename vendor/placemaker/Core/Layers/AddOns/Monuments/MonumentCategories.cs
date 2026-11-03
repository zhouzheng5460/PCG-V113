// Monuments & follies asset vocabulary (grand park wave, builder g4) and its classification rules.
//
// Columns, arches and obelisks are the building blocks of the follies. Vanilla CS2 has few of them, so
// every layer degrades: columns → columnar trees ("living colonnade") → statues; arches → a pair of
// columns → a pair of statues; obelisks → monuments → statues. Rules ADD categories and return false
// so the memorial / leisure classifiers still tag the same assets with their own categories.

using System.Collections.Generic;
using Placemaker.Core.Build;

namespace Placemaker.Core
{
    public static partial class AssetCategories
    {
        public const string MonumentColumn = "monument.column";       // columns, pillars, pilasters
        public const string MonumentArch = "monument.arch";           // arches, archways, triumphal gates, pergola arches
        public const string MonumentObelisk = "monument.obelisk";     // obelisks, needles
        public const string MonumentSculpture = "monument.sculpture"; // sculptures / statues for walks (varied per object)
    }
}

namespace Placemaker.Core.Layers.AddOns.Monuments
{
    public sealed class MonumentCategoryRules : ICategoryRules, IVarietyCategories
    {
        public int Priority => 45;

        private static bool Has(string s, params string[] parts) => AssetHeuristics.ContainsAny(s, parts);

        private static readonly string[] Never = { "advert", "billboard", "logo", "sign", "lamp", "light", "pipe", "power", "pylon", "bridge", "chimney", "placeholder" };

        public IEnumerable<string> VarietyCategories => new[] { AssetCategories.MonumentSculpture };

        private static readonly string[] NetPillars =
            { "pier", "subway", "train", "station", "elevated", "virtual", "vertical pillar", "horizontal pillar", "pillar top", "pedestrian", "bridge", "highway", "road" };

        public bool Classify(string lower, AssetTraits traits, List<string> categories)
        {
            if ((traits & (AssetTraits.Plant | AssetTraits.Building)) != 0 || Has(lower, Never)) return false;
            if (Has(lower, "obelisk")) categories.Add(AssetCategories.MonumentObelisk);
            if (Has(lower, "column", "pillar", "pilaster", "colonnade", "peristyle"))
                categories.Add(AssetCategories.MonumentColumn);
            if (Has(lower, "archway", "triumphal", "gateway", "arcade", "pergolaarch", "pergola arch", "rosearch", "rose arch", "stonearch", "stone arch")
                || IsArchWord(lower))
                categories.Add(AssetCategories.MonumentArch);
            if (Has(lower, "sculpture", "statue", "artwork", "art installation", "bust") && !Has(lower, "fountain", "grave", "tomb"))
                categories.Add(AssetCategories.MonumentSculpture);
            return false;
        }

        /// <summary>"arch" as a word ("Arch01", "Park_Arch", "MarbleArch"), not "archive", "march", "search"…</summary>
        private static bool IsArchWord(string lower)
        {
            if (Has(lower, "archive", "architect", "march", "search", "starch", "parch", "monarch", "archer", "archery", "larch")) return false;
            int i = lower.IndexOf("arch", System.StringComparison.Ordinal);
            if (i < 0) return false;
            int end = i + 4;
            return end >= lower.Length || !char.IsLetter(lower[end]) || lower[end] == 'w';
        }

        public bool? AutoEligible(string category, string lower, AssetTraits traits)
        {
            switch (category)
            {
                case AssetCategories.MonumentColumn:
                    // Art pass a6: every "pillar" in the vanilla catalog is a bridge / pier / subway support
                    // (Pier01Pillar, SubwayStationElevated02Pillar, "Pillar Top 16"…): never a park column.
                    if (Has(lower, NetPillars)) return false;
                    return !Has(lower, "advert", "logo", "billboard", "screen", "placeholder", "random", "broken", "ruin", "damaged");
                case AssetCategories.CenterpieceStatue:
                case AssetCategories.Centerpiece:
                    // Clock hands (NotreDameClockPointers, TrainStation01ClockPointers01) are building parts.
                    return Has(lower, "pointer", "clockhand", "clock hand") ? false : (bool?)null;
                case AssetCategories.MonumentArch:
                case AssetCategories.MonumentObelisk:
                case AssetCategories.MonumentSculpture:
                    return !Has(lower, "advert", "logo", "billboard", "screen", "placeholder", "random", "broken", "ruin", "damaged");
                default:
                    return null;
            }
        }
    }
}
