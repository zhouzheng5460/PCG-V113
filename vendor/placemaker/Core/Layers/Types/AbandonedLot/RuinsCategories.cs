// Asset vocabulary for the decay types (abandoned lot, urban ruins) and the name rules that fill it.
//
// Vanilla CS2 has no "ruin" asset family, so these pools are assembled from whatever props read as
// decay: construction/industrial leftovers (concrete blocks, pipes, pallets, barrels, tyres), piles
// and debris, wild plants. All rules are ADDITIVE (Classify returns false) so the same prop keeps its
// general category too (a concrete barrier stays a fence, a tent stays camping). Automatic picks are
// allowed for these pools even though the general classifier excludes "pile"/"debris"/"pallet" from
// park furniture — here that junk is exactly the point.

using System.Collections.Generic;
using Placemaker.Core.Build;

namespace Placemaker.Core
{
    public static partial class AssetCategories
    {
        public const string RuinsRubble = "ruins.rubble";     // rubble / debris / brick & concrete piles
        public const string RuinsJunk = "ruins.junk";         // tyres, barrels, pallets, crates, carts, scrap
        public const string RuinsSlab = "ruins.slab";         // concrete blocks, slabs, pipes, beams, jersey barriers
        public const string RuinsPillar = "ruins.pillar";     // pillars / columns / bridge supports (also slab)
        public const string RuinsWall = "ruins.wall";         // free-standing wall pieces
        public const string RuinsWeeds = "ruins.weeds";       // wild plants: weeds, nettles, thistles, brambles
        public const string RuinsShelter = "ruins.shelter";   // shacks, tarps, makeshift huts
    }
}

namespace Placemaker.Core.Layers.Types.Ruins
{
    public sealed class RuinsCategoryRules : ICategoryRules
    {
        public int Priority => 10;

        private static bool Has(string s, params string[] parts) => AssetHeuristics.ContainsAny(s, parts);

        public bool Classify(string lower, AssetTraits traits, List<string> categories)
        {
            if (Has(lower, "lamp", "light", "sign", "billboard", "advert", "poster", "logo")) return false;
            if ((traits & AssetTraits.Plant) != 0)
            {
                if (Has(lower, "weed", "nettle", "thistle", "bramble", "wild", "tallgrass", "tall grass", "fern", "dandelion", "overgrow", "ivy"))
                    categories.Add(AssetCategories.RuinsWeeds);
                return false;
            }
            if (Has(lower, "rubble", "debris", "brokenconcrete", "broken concrete", "brickpile", "brick pile", "concretepile", "concrete pile",
                "stonepile", "stone pile", "ruin", "collapsed", "wreckage", "demolition"))
                categories.Add(AssetCategories.RuinsRubble);
            bool pillar = Has(lower, "pillar", "column", "bridgesupport", "bridge support", "overpasssupport");
            if (pillar) categories.Add(AssetCategories.RuinsPillar);
            if (pillar || Has(lower, "concreteblock", "concrete block", "slab", "jersey", "concretebarrier", "concrete barrier", "concretepipe", "concrete pipe",
                "culvert", "precast", "girder", "ibeam", "i-beam", "steelbeam", "steel beam", "cinderblock", "cinder block"))
                categories.Add(AssetCategories.RuinsSlab);
            if (lower.Contains("wall") && !Has(lower, "clock", "hanging", "mounted", "shelf", "paper", "decal"))
                categories.Add(AssetCategories.RuinsWall);
            if (Has(lower, "tire", "tyre", "barrel", "oildrum", "oil drum", "drum0", "pallet", "crate", "scrap", "junk", "mattress", "shoppingcart",
                    "shopping cart", "trolley", "wreck", "litter", "garbagebag", "garbage bag", "trashbag", "trash bag", "cardboard", "sofa", "couch",
                    "fridge", "washingmachine") && !Has(lower, "bin", "rack", "shop"))
                categories.Add(AssetCategories.RuinsJunk);
            if (Has(lower, "shack", "shanty", "tarp", "makeshift", "leanto", "lean-to", "hut0", "hovel"))
                categories.Add(AssetCategories.RuinsShelter);
            return false;
        }

        public bool? AutoEligible(string category, string lower, AssetTraits traits)
        {
            if (category == null || !category.StartsWith("ruins.", System.StringComparison.Ordinal)) return null;
            return !Has(lower, "random", "placeholder", "test", "template", "dummy", "advert", "logo", "billboard", "poster", "brand", "chirper");
        }
    }
}
