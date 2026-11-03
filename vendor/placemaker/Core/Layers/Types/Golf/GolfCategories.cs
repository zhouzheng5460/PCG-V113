// Golf asset vocabulary and its name rules. Vanilla CS2 ships few (if any) golf props, so every
// golf category degrades gracefully: the layer asks for the specific thing first, falls back along a
// short chain (flag → nothing, bay → shelter, clubhouse → restroom → shelter) and reports an Info
// issue when nothing is installed. Patterns are deliberately narrow ("golfflag", not "flag") so
// generic flagpoles and wind turbines never end up on a putting green.

using System.Collections.Generic;
using Placemaker.Core.Build;

namespace Placemaker.Core
{
    public static partial class AssetCategories
    {
        public const string Golf = "golf";                         // parent of every golf prop
        public const string GolfFlag = "golf.flag";                // pin flags on greens
        public const string GolfTee = "golf.tee";                  // tee markers, ball washers, hole signs
        public const string GolfCart = "golf.cart";                // parked golf carts (props)
        public const string GolfBasket = "golf.basket";            // disc golf chain baskets
        public const string GolfMini = "golf.mini";                // mini golf obstacles (windmill, loop, castle…)
        public const string GolfBay = "golf.bay";                  // driving range hitting bays / tee line roofs
        public const string GolfClubhouse = "golf.clubhouse";      // clubhouse, pro shop
    }
}

namespace Placemaker.Core.Layers.Types.Golf
{
    public sealed class GolfCategoryRules : ICategoryRules
    {
        public int Priority => 40;

        private static readonly (string category, string[] names)[] Rules =
        {
            (AssetCategories.GolfBasket, new[] { "discgolf", "disc golf", "frisbeegolf", "frisbee golf", "disc_golf", "chainbasket", "chain basket" }),
            (AssetCategories.GolfMini, new[] { "minigolf", "mini golf", "mini_golf", "crazygolf", "crazy golf", "puttputt", "putt putt", "adventuregolf" }),
            (AssetCategories.GolfCart, new[] { "golfcart", "golf cart", "golf_cart", "golfbuggy", "golf buggy" }),
            (AssetCategories.GolfBay, new[] { "drivingrange", "driving range", "driving_range", "hittingbay", "hitting bay", "teebay", "golfbay", "golf bay" }),
            (AssetCategories.GolfClubhouse, new[] { "golfclub", "golf club", "clubhouse", "club house", "proshop", "pro shop" }),
            (AssetCategories.GolfTee, new[] { "teemarker", "tee marker", "golftee", "golf tee", "ballwasher", "ball washer", "golfsign", "golf sign" }),
            (AssetCategories.GolfFlag, new[] { "golfflag", "golf flag", "golf_flag", "pinflag", "pin flag", "holeflag", "hole flag", "golfhole", "golf hole", "golfpin" }),
        };

        // Things whose names contain the patterns but are no golf props.
        private static readonly string[] Excluded = { "turbine", "powerplant", "power plant", "vehicle", "trailer", "sign_road" };

        public bool Classify(string lower, AssetTraits traits, List<string> categories)
        {
            if (AssetHeuristics.ContainsAny(lower, Excluded)) return false;
            foreach (var (cat, names) in Rules)
            {
                if (!AssetHeuristics.ContainsAny(lower, names)) continue;
                categories.Add(AssetCategories.Golf);
                categories.Add(cat);
                return true;
            }
            return false;
        }

        public bool? AutoEligible(string category, string lower, AssetTraits traits)
        {
            if (category == AssetCategories.Golf) return lower.Contains("golf");
            foreach (var (cat, names) in Rules)
                if (cat == category) return AssetHeuristics.ContainsAny(lower, names) && !AssetHeuristics.ContainsAny(lower, Excluded);
            return null;
        }
    }
}
