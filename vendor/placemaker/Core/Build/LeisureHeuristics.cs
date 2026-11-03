// Placemaker — classification for the leisure categories of round 4 (decals, sports, dog park,
// gardens, stage, lookout, cafe seating, market stalls, centrepiece kinds, graves). Kept apart from
// AssetHeuristics so curation against ModsData/Placemaker/catalog-dump.json can refine these rules
// without touching the general classifier. Names are the fallback only: vanilla prefabs use
// English CamelCase ("BasketballHoop01"), so matching is on the lower-cased name.
//
// Every subcategory is also tagged with its parent category where one exists ("sports.goal" is
// also "sports"), so a layer asking for the specific thing can fall back to the general one.

using System;
using System.Collections.Generic;

namespace Placemaker.Core.Build
{
    public static class LeisureHeuristics
    {
        private static bool Has(string s, params string[] parts) => AssetHeuristics.ContainsAny(s, parts);

        /// <summary>Extra surface subcategories (sand, dirt, water look). Added next to the general ones.</summary>
        public static void SurfaceExtra(string lower, List<string> r)
        {
            if (Has(lower, "sand", "beach")) r.Add(AssetCategories.SurfaceSand);
            if (Has(lower, "dirt", "soil", "mud", "earth", "farmland")) r.Add(AssetCategories.SurfaceDirt);
            if (Has(lower, "water", "pond", "pool")) r.Add(AssetCategories.SurfaceWater);
        }

        /// <summary>
        /// Classifies a visible static object into the leisure categories. Returns true when it
        /// decided (the general classifier then stops); false = not a leisure asset, carry on.
        /// </summary>
        public static bool Classify(string lower, AssetTraits t, List<string> r)
        {
            // Decals and rocks are classified by AssetHeuristics (component signals / IsRockName).
            if (Has(lower, "hedge", "topiary"))
            {
                r.Add(AssetCategories.Hedge);
                if (!lower.Contains("topiary")) r.Add(AssetCategories.Fence);
                else r.Add(AssetCategories.Bush);
                return true;
            }

            // Sports, specific before general.
            if (Has(lower, "bleacher", "grandstand", "tribune", "spectator", "stadiumseat", "stadium seat", "standseat"))
            { r.Add(AssetCategories.SportsStand); return true; }
            if (Has(lower, "floodlight", "flood light", "stadiumlight", "stadium light", "sportslight", "sports light", "sportlight"))
            { r.Add(AssetCategories.FloodLight); return true; }
            if (Has(lower, "dog"))
            {
                if (Has(lower, "agility", "jump", "tunnel", "hurdle", "weave", "ramp", "hoop", "seesaw", "obstacle", "tire", "tyre", "platform"))
                { r.Add(AssetCategories.DogAgility); return true; }
                if (Has(lower, "bin", "trash", "waste", "fountain", "water", "bowl", "station", "bag"))
                { r.Add(AssetCategories.DogAmenity); return true; }
            }
            if (Has(lower, "basketball", "hoop")) { r.Add(AssetCategories.Sports); r.Add(AssetCategories.SportsBasketball); return true; }
            if (lower.Contains("tennis") && !Has(lower, "table tennis", "tabletennis", "pingpong", "ping pong"))
            { r.Add(AssetCategories.Sports); r.Add(AssetCategories.SportsTennis); return true; }
            if (Has(lower, "goal", "soccer", "football", "handball"))
            { r.Add(AssetCategories.Sports); r.Add(AssetCategories.SportsGoal); return true; }
            if (Has(lower, "skate", "halfpipe", "half pipe", "grindrail", "grind rail", "quarterpipe", "funbox"))
            { r.Add(AssetCategories.Sports); r.Add(AssetCategories.SportsSkate); return true; }
            if (Has(lower, "fitness", "outdoor gym", "outdoorgym", "pullup", "pull-up", "situp", "exercise") || (t & AssetTraits.ExerciseActivity) != 0)
            { r.Add(AssetCategories.Sports); r.Add(AssetCategories.SportsFitness); return true; }

            // Gardens.
            if (Has(lower, "greenhouse", "glasshouse", "glass house", "conservatory", "orangery"))
            { r.Add(AssetCategories.Glasshouse); return true; }
            if (Has(lower, "shed", "watertank", "water tank", "rainbarrel", "rain barrel", "compost", "tool box", "toolbox"))
            { r.Add(AssetCategories.GardenShed); return true; }
            if (Has(lower, "crop", "vegetable", "cabbage", "lettuce", "pumpkin", "tomato", "allotment", "raised bed", "raisedbed", "veggie", "potato", "carrot"))
            { r.Add(AssetCategories.GardenCrop); return true; }

            // Structures and seating.
            if (Has(lower, "bandstand")) { r.Add(AssetCategories.Stage); r.Add(AssetCategories.Shelter); return true; }
            if (Has(lower, "stage", "podium", "amphitheat")) { r.Add(AssetCategories.Stage); return true; }
            if (Has(lower, "lookout", "observation", "viewpoint", "watchtower", "watch tower", "binocular", "viewing"))
            { r.Add(AssetCategories.Lookout); return true; }
            if (Has(lower, "chess")) { r.Add(AssetCategories.CafeSeating); r.Add(AssetCategories.Picnic); return true; }
            // Campground / picnic table sets are the picnic tables the vanilla "picnic" pool lacked (art a2).
            if (Has(lower, "picnictable", "picnic table", "campgroundtableset", "campground tableset")) r.Add(AssetCategories.Picnic);
            if (Has(lower, "cafe", "café", "patio", "parasol", "umbrella", "bistro", "tableset", "table set", "outdoortable", "outdoor table", "terrace"))
            { r.Add(AssetCategories.CafeSeating); return true; }
            if (Has(lower, "market", "stall", "awning"))
            { r.Add(AssetCategories.Kiosk); r.Add(AssetCategories.MarketStall); return true; }
            if (Has(lower, "splash", "sprinkler", "waterjet", "water jet", "jetfountain", "jet fountain", "spray"))
            { r.Add(AssetCategories.WaterJets); return true; }
            if (Has(lower, "grave", "tomb", "headstone", "crypt", "mausoleum", "memorialstone", "memorial stone"))
            { r.Add(AssetCategories.Grave); return true; }

            // Centrepiece kinds (same visibility rule as the general classifier: a single standalone prop with an icon).
            if ((t & AssetTraits.HasSubObjects) == 0 && (t & AssetTraits.HasIcon) != 0)
            {
                if (lower.Contains("fountain") && !Has(lower, "drinking"))
                { r.Add(AssetCategories.Centerpiece); r.Add(AssetCategories.CenterpieceFountain); return true; }
                if (Has(lower, "statue", "monument", "sculpture", "obelisk", "clock", "memorial", "bust"))
                { r.Add(AssetCategories.Centerpiece); r.Add(AssetCategories.CenterpieceStatue); return true; }
            }
            return false;
        }

        /// <summary>
        /// Automatic eligibility for leisure categories that the general exclusion lists would reject
        /// (stands and floodlights contain "stadium"). Returns true when it decided.
        /// </summary>
        public static bool AutoEligible(string category, string lower, out bool eligible)
        {
            eligible = false;
            switch (category)
            {
                case AssetCategories.SportsStand:
                case AssetCategories.FloodLight:
                    eligible = !Has(lower, "advert", "billboard", "logo", "banner", "sign", "screen", "placeholder");
                    return true;
                case AssetCategories.Stage:
                case AssetCategories.Lookout:
                    eligible = !Has(lower, "advert", "billboard", "logo", "banner", "screen", "placeholder");
                    return true;
                case AssetCategories.CenterpieceFountain:
                case AssetCategories.CenterpieceStatue:
                    eligible = !Has(lower, "drinking", "dog", "logo", "advert");
                    return true;
                default:
                    return false;
            }
        }
    }
}
