// Asset categories and name rules for the scrapyard type (and anything gritty: the landfill, a
// construction site or an abandoned lot can ask for the same things). Names are the fallback only —
// vanilla CS2 props are CamelCase ("ShippingContainer01", "Pallet02", "OilBarrel01", "TirePile01",
// "CarWreck01", "Forklift01", "ConcreteBarrier01"), custom assets use every convention, so patterns
// are lower-case substrings with exclusions for the obvious false friends (dog-park tyre jumps,
// tyre swings, rain barrels, garbage containers, log cabins…).

using System.Collections.Generic;
using Placemaker.Core.Build;

namespace Placemaker.Core
{
    public static partial class AssetCategories
    {
        public const string ScrapHeap = "industrial.scrap";           // scrap metal, junk heaps, debris piles
        public const string ScrapWreck = "industrial.wreck";          // car wrecks, burnt-out / crushed cars
        public const string ScrapTire = "industrial.tire";            // tyres, tyre stacks and piles
        public const string ScrapContainer = "industrial.container";  // shipping / cargo containers
        public const string ScrapPallet = "industrial.pallet";        // pallets, crates, wooden boxes
        public const string ScrapBarrel = "industrial.barrel";        // oil drums, barrels, canisters, gas bottles
        public const string ScrapMachine = "industrial.machine";      // forklifts, cranes, loaders, crushers (props)
        public const string ScrapCabin = "industrial.cabin";          // site offices, portacabins, guard booths
        public const string ScrapBarrier = "industrial.barrier";      // concrete blocks, jersey barriers, cones
    }
}

namespace Placemaker.Core.Layers.Types.Scrapyard
{
    /// <summary>Short aliases used by the yard kit.</summary>
    public static class ScrapyardCategories
    {
        public const string Heap = AssetCategories.ScrapHeap;
        public const string Wreck = AssetCategories.ScrapWreck;
        public const string Tire = AssetCategories.ScrapTire;
        public const string Container = AssetCategories.ScrapContainer;
        public const string Pallet = AssetCategories.ScrapPallet;
        public const string Barrel = AssetCategories.ScrapBarrel;
        public const string Machine = AssetCategories.ScrapMachine;
        public const string Cabin = AssetCategories.ScrapCabin;
        public const string Barrier = AssetCategories.ScrapBarrier;

        public static readonly string[] All = { Heap, Wreck, Tire, Container, Pallet, Barrel, Machine, Cabin, Barrier };
    }

    /// <summary>Name rules for the scrapyard categories (run before the leisure/general heuristics).</summary>
    public sealed class ScrapyardCategoryRules : ICategoryRules
    {
        public int Priority => 50;

        private static bool Has(string s, params string[] parts) => AssetHeuristics.ContainsAny(s, parts);

        public bool Classify(string lower, AssetTraits traits, List<string> categories)
        {
            if ((traits & (AssetTraits.Plant | AssetTraits.Tree | AssetTraits.Building)) != 0) return false;
            // Play and pet equipment made of tyres/barrels belongs to the leisure pools.
            if (Has(lower, "dog", "swing", "playground", "play ", "playset", "playcabin", "toy")) return false;

            if (Has(lower, "wreck", "burnt car", "burntcar", "burned car", "scrapcar", "scrap car", "junkcar", "junk car",
                    "abandonedcar", "abandoned car", "crushedcar", "crushed car", "carcass", "carbody", "car body", "rustycar", "rusty car"))
            { categories.Add(AssetCategories.ScrapWreck); return true; }
            if (Has(lower, "tire", "tyre") && !Has(lower, "retire", "tired"))
            { categories.Add(AssetCategories.ScrapTire); return true; }
            if (Has(lower, "container") && !Has(lower, "garbage", "trash", "waste", "recycl", "bin", "plant", "flower", "water", "glass", "cloth", "bottle", "skip", "dumpster"))
            { categories.Add(AssetCategories.ScrapContainer); return true; }
            if (Has(lower, "forklift", "fork lift", "excavator", "bulldozer", "digger", "wheelloader", "wheel loader", "frontloader", "front loader",
                    "dumper", "compactor", "reachstacker", "reach stacker", "straddle", "crusher", "baler", "material handler", "scrap handler",
                    "crane") && !Has(lower, "bird", "origami"))
            { categories.Add(AssetCategories.ScrapMachine); return true; }
            if (Has(lower, "scrap", "junk", "debris", "wreckage", "metalpile", "metal pile", "girder", "pipepile", "pipe pile", "pipes pile", "rebar"))
            { categories.Add(AssetCategories.ScrapHeap); return true; }
            if (Has(lower, "pallet", "crate", "woodenbox", "wooden box", "cargobox", "cargo box", "shipment", "cardboardbox", "cardboard box"))
            { categories.Add(AssetCategories.ScrapPallet); return true; }
            if ((Has(lower, "barrel", "oildrum", "oil drum", "drum", "canister", "gascylinder", "gas cylinder", "gasbottle", "gas bottle", "jerrycan", "jerry can")
                 && !Has(lower, "rain", "drummer", "drumkit", "drum kit")))
            { categories.Add(AssetCategories.ScrapBarrel); return true; }
            if (Has(lower, "portacabin", "porta cabin", "portakabin", "siteoffice", "site office", "containeroffice", "office container",
                    "constructiontrailer", "construction trailer", "site trailer", "sitetrailer", "guardbooth", "guard booth", "guardhouse",
                    "security booth", "securitybooth", "weighbridge", "gatehouse") ||
                (lower.Contains("cabin") && !Has(lower, "log cabin", "logcabin", "cable")))
            { categories.Add(AssetCategories.ScrapCabin); return true; }
            if (Has(lower, "jersey", "concretebarrier", "concrete barrier", "concreteblock", "concrete block", "legoblock", "lego block",
                    "roadblock", "road block", "trafficcone", "traffic cone", "constructionbarrier", "construction barrier") ||
                (lower.Contains("cone") && !Has(lower, "pine", "ice", "icecream", "cones of")) ||
                (lower.Contains("barrier") && !Has(lower, "noise", "sound", "gatebarrier", "parking")))
            { categories.Add(AssetCategories.ScrapBarrier); return true; }
            return false;
        }

        public bool? AutoEligible(string category, string lower, AssetTraits traits)
        {
            if (System.Array.IndexOf(ScrapyardCategories.All, category) < 0) return null;
            // Brand/hanging/water props are excluded before rules run; here only junk that never reads right.
            return !Has(lower, "test", "chirper", "logo", "advert", "billboard");
        }
    }
}
