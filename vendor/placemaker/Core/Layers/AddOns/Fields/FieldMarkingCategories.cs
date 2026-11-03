// Field markings (playtest: a running track's lines came out as yellow/black caution hatching tiled
// along the lanes). The general "decal.markings" pool is a road/parking vocabulary — its best-ranked
// items are no-parking striping, chevrons, zig-zags and arrows — so sports fields never draw from it.
// They use their own pools:
//
//  * decal.fieldline — thin WHITE line pieces (vanilla: "G87 UK Terminal Line White"). LinePainter
//    chains them along every line of a field. Anything hazard-like (caution, striping, zig-zag,
//    chevrons, keep-clear, coloured lines, arrows, letters) is excluded. No line decal installed →
//    no lines at all (never a fallback to another decal pool: the game has none for these ids).
//  * decal.courtbasketball — whole FIBA court decals (vanilla BasketballCourtDecal01/03, 28 × 15 m),
//    stamped once instead of painting the lines piece by piece.

using System.Collections.Generic;
using Placemaker.Core.Build;

namespace Placemaker.Core
{
    public static partial class AssetCategories
    {
        /// <summary>Thin white line decal pieces for sports field markings (never hazard / road markings).</summary>
        public const string DecalFieldLine = "decal.fieldline";
        /// <summary>A whole basketball court marking decal (28 × 15 m).</summary>
        public const string DecalCourtBasketball = "decal.courtbasketball";
    }
}

namespace Placemaker.Core.Layers.AddOns.Fields
{
    public sealed class FieldMarkingCategoryRules : ICategoryRules
    {
        public int Priority => 45;

        /// <summary>Lower-case name parts that make a decal unfit for a sports field line.</summary>
        public static readonly string[] Hazard =
        {
            "hazard", "caution", "warning", "danger", "striping", "stripe", "zigzag", "zig zag", "chevron", "keep clear", "kc ",
            "nopark", "no park", "parking", "arrow", "yellow", "red", "blue", "orange", "black", "bus", "taxi", "stop", "give way",
            "alphabet", "letter", "number", "text", "speed", "icon", "ban", "track", "tire", "tyre", "grid",
        };

        private static bool Has(string s, params string[] parts) => AssetHeuristics.ContainsAny(s, parts);

        /// <summary>True for a white line piece suitable for painting field lines.</summary>
        public static bool IsFieldLine(string lower)
        {
            if (string.IsNullOrEmpty(lower) || Has(lower, Hazard)) return false;
            bool line = Has(lower, " line", "line ", "lines", "fieldline", "courtline", "sportline", "pitchline");
            return line && lower.Contains("white");
        }

        public static bool IsBasketballCourt(string lower) =>
            !string.IsNullOrEmpty(lower) && lower.Contains("basketball") && lower.Contains("court") && lower.Contains("decal");

        public bool Classify(string lower, AssetTraits traits, List<string> categories)
        {
            if ((traits & AssetTraits.DecalMesh) == 0) return false;
            if (IsFieldLine(lower)) categories.Add(AssetCategories.DecalFieldLine);
            if (IsBasketballCourt(lower)) categories.Add(AssetCategories.DecalCourtBasketball);
            return false;
        }

        public bool? AutoEligible(string category, string lower, AssetTraits traits)
        {
            switch (category)
            {
                case AssetCategories.DecalFieldLine: return IsFieldLine(lower);
                case AssetCategories.DecalCourtBasketball: return IsBasketballCourt(lower);
                default: return null;
            }
        }
    }
}
