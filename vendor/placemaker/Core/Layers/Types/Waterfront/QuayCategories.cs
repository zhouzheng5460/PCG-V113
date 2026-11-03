// Quay and pier nets (s20). Research: RESEARCH-NOTES-ADDENDUM "Quays" (rules 77–81).
//
// A CS2 quay is an ordinary pathway/road net with a Bridge component in the Quay build style: placement flag
// ShoreLine, ElevatedIsRaised, and per-side elevation taken from the terrain beside the deck. Wherever the ground
// (or water bed) on a side lies at least the elevation limit below the deck, that side gets a Raised composition —
// the quay WALL. So the wall faces whichever side is low: lay the quay along the waterline with its deck above the
// water, fill the land behind it up to deck height, and the wall stands in the water with no wall on the land side.
// Vanilla pedestrian quays: "Narrow Quay With Pedestrian Path" (QuaySmall01/02, 3 m) and "Wide Quay with Pedestrian
// Path" (QuaySmallWide01/02, 8 m); the bike variants and the road quays (QuayMedium01 16 m, QuayLarge01 24 m) are not
// walks. Leisure piers (LeisurePier01Small01/Medium01/Large01, 3/7/9 m) are elevated wooden decks on pillars.

using System.Collections.Generic;
using Placemaker.Core.Build;

namespace Placemaker.Core
{
    public static partial class AssetCategories
    {
        /// <summary>(s20) Pedestrian quays: a walk with a retaining wall on its low (water) side. Fallback: pathway.</summary>
        public const string NetQuay = "net.quay";
        /// <summary>(s20) Leisure piers / boardwalks: wooden decks on pillars over water. Fallback: pathway.</summary>
        public const string NetPier = "net.pier";
    }
}

namespace Placemaker.Core.Layers.Types.Waterfront
{
    public sealed class QuayCategoryRules : ICategoryRules
    {
        public int Priority => 45;

        public QuayCategoryRules()
        {
            CategoryFallbacks.Register(AssetCategories.NetQuay, AssetCategories.Pathway);
            CategoryFallbacks.Register(AssetCategories.NetPier, AssetCategories.Pathway);
        }

        private static bool Has(string s, params string[] parts) => AssetHeuristics.ContainsAny(s, parts);

        public bool Classify(string lower, AssetTraits traits, List<string> r)
        {
            // Pedestrian nets only: road quays carry traffic, quay/retaining-wall upgrades are fences.
            if ((traits & AssetTraits.Net) == 0 || (traits & AssetTraits.NetFence) != 0 || (traits & AssetTraits.Pathway) == 0) return false;
            if (Has(lower, "invisible", "marker")) return false;
            if (Has(lower, "quay")) r.Add(AssetCategories.NetQuay);
            else if (Has(lower, "pier", "boardwalk", "jetty") && !Has(lower, "pillar")) r.Add(AssetCategories.NetPier);
            return false;
        }

        public bool? AutoEligible(string category, string lower, AssetTraits traits)
        {
            if (category == AssetCategories.NetQuay) return !Has(lower, "bike", "bicycle", "cycle");
            if (category == AssetCategories.NetPier) return !Has(lower, "marina", "empty");
            return null;
        }
    }
}
