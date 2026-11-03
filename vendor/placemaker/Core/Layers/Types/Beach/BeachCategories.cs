// Asset categories of the water types (beach, waterfront) and their name rules.
//
// Real asset names are not curated yet (no catalog dump); these are broad patterns following vanilla
// CS2 CamelCase naming. Shared words (parasol, bollard) add the beach/waterfront category and let the
// built-in heuristics add theirs too (return false); words that only mean beach things stop there.
// Every layer asks with a fallback chain (umbrella → cafe seating, lounger → bench…) and degrades to
// an Info issue when nothing is installed.

using System.Collections.Generic;
using Placemaker.Core.Build;

namespace Placemaker.Core
{
    public static partial class AssetCategories
    {
        public const string BeachUmbrella = "beach.umbrella";       // beach umbrellas, parasols, sunshades
        public const string BeachLounger = "beach.lounger";         // sun loungers, deck chairs, beach chairs
        public const string BeachLifeguard = "beach.lifeguard";     // lifeguard towers / chairs
        public const string BeachShower = "beach.shower";           // beach showers, foot washes
        public const string BeachHut = "beach.hut";                 // changing huts, beach huts, cabanas
        public const string BeachVolleyball = "beach.volleyball";   // volleyball nets / posts (also sports)
        public const string BeachBoat = "beach.boat";               // rowing boats, kayaks, pedalos on the sand
        public const string WaterfrontMooring = "waterfront.mooring";   // mooring bollards, cleats (also bollard)
        public const string WaterfrontFishing = "waterfront.fishing";   // fishing rods, anglers' props, lobster pots
        public const string WaterfrontNautical = "waterfront.nautical"; // anchors, buoys, life rings, coin telescopes
    }
}

namespace Placemaker.Core.Layers.Types.Beach
{
    public sealed class WaterCategoryRules : ICategoryRules
    {
        public int Priority => 50;

        private static bool Has(string s, params string[] parts) => AssetHeuristics.ContainsAny(s, parts);

        public bool Classify(string lower, AssetTraits traits, List<string> r)
        {
            if (Has(lower, "lifeguard", "life guard", "baywatch", "rescuetower", "rescue tower"))
            { r.Add(AssetCategories.BeachLifeguard); return true; }
            if (Has(lower, "volleyball", "beachvolley"))
            { r.Add(AssetCategories.BeachVolleyball); r.Add(AssetCategories.Sports); return true; }
            if (Has(lower, "sunlounger", "sun lounger", "lounger", "sunbed", "sun bed", "deckchair", "deck chair", "beachchair", "beach chair", "chaise"))
            { r.Add(AssetCategories.BeachLounger); return true; }
            if (Has(lower, "beachumbrella", "beach umbrella", "sunshade", "sun shade") ||
                (Has(lower, "umbrella", "parasol") && !Has(lower, "table", "cafe", "restaurant")))
            { r.Add(AssetCategories.BeachUmbrella); return false; }
            if (Has(lower, "shower", "footwash", "foot wash"))
            { r.Add(AssetCategories.BeachShower); return true; }
            if (Has(lower, "beachhut", "beach hut", "bathinghut", "bathing hut", "changinghut", "changing hut", "changingroom", "changing room",
                    "changingcabin", "beachcabin", "beach cabin", "cabana", "bathhouse"))
            { r.Add(AssetCategories.BeachHut); return true; }
            if (Has(lower, "rowboat", "row boat", "rowingboat", "kayak", "canoe", "pedalo", "dinghy", "smallboat", "small boat", "boatonstand", "beachboat")
                && !Has(lower, "house", "ramp", "way"))
            { r.Add(AssetCategories.BeachBoat); return true; }
            if (Has(lower, "mooring", "cleat", "capstan", "shipbollard", "ship bollard", "harborbollard", "harbourbollard", "quaybollard", "dockbollard"))
            { r.Add(AssetCategories.WaterfrontMooring); r.Add(AssetCategories.Bollard); return true; }
            if (Has(lower, "fishingrod", "fishing rod", "fishingspot", "fishing spot", "angler", "fisherman", "lobsterpot", "lobster pot", "crabpot",
                    "crab pot", "fishtrap", "fish trap", "fishingnet", "fishing net"))
            { r.Add(AssetCategories.WaterfrontFishing); return true; }
            if (Has(lower, "anchor", "buoy", "lifebuoy", "lifering", "life ring", "liferings", "telescope", "binocular", "ropecoil", "rope coil", "ship wheel", "shipwheel"))
            { r.Add(AssetCategories.WaterfrontNautical); return false; }
            return false;
        }

        public bool? AutoEligible(string category, string lower, AssetTraits traits)
        {
            if (category == null || !(category.StartsWith("beach.") || category.StartsWith("waterfront."))) return null;
            // Branded or wall-mounted variants never go into automatic pools.
            if ((traits & (AssetTraits.BrandObject | AssetTraits.WallOrHanging | AssetTraits.NetAttached)) != 0) return false;
            return null;
        }
    }
}
