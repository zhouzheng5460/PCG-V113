// Classification for the amenity categories (round 5), discovered by CategoryRules via reflection.
// Runs before the built-in leisure heuristics so the specific things win; every match also keeps the
// broader categories the leisure heuristics used to give it (chess tables stay cafe seating and
// picnic, bandstands stay stage and shelter), so nothing that worked before loses assets.
// Noticeboards are signs: only explicit board names qualify, and adverts never do (FEEDBACK-2 #1:
// no sign clutter in automatic pools).

using System.Collections.Generic;
using Placemaker.Core.Build;

namespace Placemaker.Core.Layers.AddOns
{
    public sealed class AmenityCategoryRules : ICategoryRules
    {
        public int Priority => 40;

        private static bool Has(string s, params string[] parts) => AssetHeuristics.ContainsAny(s, parts);

        private static readonly string[] BoardNames =
            { "noticeboard", "notice board", "bulletin", "infoboard", "info board", "information board", "informationboard", "mapboard", "map board", "parkmap", "park map", "trailmap", "trail map" };

        private static readonly string[] AdvertNames = { "advert", "billboard", "logo", "screen", "poster", "banner", "neon" };

        // Farm irrigation (AgricultureSprinklerSystem, tractor sprayers) matches "sprinkler"/"spray" but is
        // a 16 m machine: never an automatic splash-pad jet (art pass a6; no farm machinery in parks).
        private static readonly string[] FarmNames = { "agricultur", "farm", "tractor", "irrigation", "crop", "harvest", "fertiliz" };

        public bool Classify(string lower, AssetTraits traits, List<string> categories)
        {
            if (string.IsNullOrEmpty(lower)) return false;
            if (lower.Contains("volleyball")) { categories.Add(AssetCategories.Sports); categories.Add(AssetCategories.SportsVolleyball); return true; }
            if (Has(lower, "tabletennis", "table tennis", "pingpong", "ping pong", "ping-pong"))
            { categories.Add(AssetCategories.Sports); categories.Add(AssetCategories.SportsTableTennis); return true; }
            if (lower.Contains("chess"))
            {
                categories.Add(AssetCategories.GamesChess); categories.Add(AssetCategories.CafeSeating); categories.Add(AssetCategories.Picnic);
                return true;
            }
            if (lower.Contains("bandstand"))
            {
                categories.Add(AssetCategories.StageBandstand); categories.Add(AssetCategories.Stage); categories.Add(AssetCategories.Shelter);
                return true;
            }
            if (Has(lower, BoardNames) && !Has(lower, AdvertNames)) { categories.Add(AssetCategories.SignNoticeboard); return true; }
            return false;
        }

        public bool? AutoEligible(string category, string lower, AssetTraits traits)
        {
            switch (category)
            {
                case AssetCategories.SignNoticeboard: return Has(lower, BoardNames) && !Has(lower, AdvertNames);
                case AssetCategories.WaterJets: return Has(lower, FarmNames) ? false : (bool?)null;
                case AssetCategories.SportsVolleyball:
                case AssetCategories.SportsTableTennis:
                case AssetCategories.GamesChess:
                case AssetCategories.StageBandstand:
                    return !Has(lower, AdvertNames) && !lower.Contains("placeholder");
                default: return null;
            }
        }
    }
}
