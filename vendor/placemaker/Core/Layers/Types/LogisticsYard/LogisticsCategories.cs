// Asset vocabulary for the logistics yard (builder 12). Every cargo kind is also "logistics.cargo",
// so the "cargo assets" picker and fallbacks can ask for any stored goods at once.
//
// Real prefab names are not known yet (catalog-dump.json pending): the rules below use broad vanilla
// CamelCase name parts ("ShippingContainer01", "PalletStack", "WoodenCrate", "LumberPile",
// "Forklift01", "SemiTrailer") with exclusions for look-alikes (garbage containers, road barriers,
// container cranes that are buildings). Buildings never reach these rules (AssetHeuristics stops
// at the Building trait first).

using System.Collections.Generic;
using Placemaker.Core.Build;

namespace Placemaker.Core
{
    public static partial class AssetCategories
    {
        public const string LogisticsCargo = "logistics.cargo";           // any stored goods (parent of the kinds below)
        public const string LogisticsContainer = "logistics.container";   // shipping containers, container stacks
        public const string LogisticsPallet = "logistics.pallet";         // pallets, pallet stacks, wrapped pallets
        public const string LogisticsCrate = "logistics.crate";           // crates, boxes, cartons, shipments
        public const string LogisticsLumber = "logistics.lumber";         // lumber, timber, planks, log piles
        public const string LogisticsBulk = "logistics.bulk";             // bulk piles (gravel, sand, coal), pipes, bags, coils
        public const string LogisticsBarrel = "logistics.barrel";         // barrels, drums, IBC tanks, gas cylinders
        public const string LogisticsMachine = "logistics.machine";       // forklifts, reach stackers, straddle carriers, yard cranes
        public const string LogisticsTruck = "logistics.truck";           // parked trucks, trailers, semi trailers (props)
        public const string LogisticsShed = "logistics.shed";             // warehouse sheds, storage tents, lean-tos (buildings as props)
        public const string LogisticsCabin = "logistics.cabin";           // site offices, portacabins, guard booths
        public const string LogisticsBarrier = "logistics.barrier";       // boom barriers / gate arms
    }
}

namespace Placemaker.Core.Layers.Types.Logistics
{
    /// <summary>Name rules for the logistics categories. Adds categories and lets the general rules continue.</summary>
    public sealed class LogisticsCategoryRules : ICategoryRules
    {
        public int Priority => 20;

        private static bool Has(string s, params string[] parts) => AssetHeuristics.ContainsAny(s, parts);

        private static readonly string[] NotCargo =
        {
            "garbage", "trash", "waste", "recycl", "dumpster", "litter", "bin0", "glass container", "glasscontainer",
            "advert", "billboard", "logo", "decal", "placeholder", "random", "rubble", "debris", "leaves", "leaf", "snow", "drumkit", "drum kit",
        };

        public bool Classify(string lower, AssetTraits traits, List<string> categories)
        {
            if ((traits & AssetTraits.Plant) != 0) return false;
            if (Has(lower, "advert", "billboard", "placeholder")) return false;

            void Cargo(string kind) { if (!categories.Contains(LogisticsCargoCat)) categories.Add(LogisticsCargoCat); categories.Add(kind); }

            // Machines and vehicles first: "ContainerTruck" is a truck, "PalletJack" a machine.
            if (Has(lower, "forklift", "fork lift", "reachstacker", "reach stacker", "straddle", "pallet jack", "palletjack",
                "telehandler", "yard tractor", "yardtractor", "terminal tractor", "gantry", "loader"))
            { categories.Add(AssetCategories.LogisticsMachine); return false; }
            if (Has(lower, "truck", "trailer", "lorry", "semitruck", "semi truck", "tractor unit", "flatbed") && !Has(lower, "fire", "police", "ambulance", "food", "ice cream", "garbage", "toy"))
            { categories.Add(AssetCategories.LogisticsTruck); return false; }
            if (Has(lower, "boom barrier", "boombarrier", "boomgate", "boom gate", "gate arm", "gatearm", "barrier gate", "barriergate", "parking barrier"))
            { categories.Add(AssetCategories.LogisticsBarrier); return false; }
            if (Has(lower, "portacabin", "porta cabin", "site office", "siteoffice", "office container", "officecontainer",
                "guard booth", "guardbooth", "guardhouse", "guard house", "gatehouse", "gate house", "security booth")
                || (lower.Contains("cabin") && !Has(lower, "playcabin", "play cabin", "log cabin", "logcabin")))
            { categories.Add(AssetCategories.LogisticsCabin); return false; }
            if (Has(lower, "warehouse", "storage tent", "storagetent", "lean-to", "leanto", "lean to", "carport", "storage shed", "storageshed", "hangar", "silo"))
            { categories.Add(AssetCategories.LogisticsShed); return false; }

            if (Has(lower, NotCargo)) return false;
            if (Has(lower, "shipping container", "shippingcontainer", "cargo container", "cargocontainer", "containerstack", "container stack", "container"))
            { Cargo(AssetCategories.LogisticsContainer); return false; }
            if (Has(lower, "pallet")) { Cargo(AssetCategories.LogisticsPallet); return false; }
            if (Has(lower, "crate", "cardboard", "carton", "box pile", "boxpile", "boxes", "shipment", "parcel", "package"))
            { Cargo(AssetCategories.LogisticsCrate); return false; }
            if (Has(lower, "lumber", "timber", "plank", "log pile", "logpile", "logs", "woodpile", "wood pile", "beam pile", "board pile"))
            { Cargo(AssetCategories.LogisticsLumber); return false; }
            if (Has(lower, "barrel", "drum", "ibc", "gas cylinder", "gascylinder", "canister", "oil tank", "oiltank"))
            { Cargo(AssetCategories.LogisticsBarrel); return false; }
            if (Has(lower, "pipe", "gravel pile", "sand pile", "coal pile", "salt pile", "pile", "heap", "sack", "bag stack", "bigbag", "big bag", "coil", "rebar", "bricks", "brick stack"))
            { Cargo(AssetCategories.LogisticsBulk); return false; }
            return false;
        }

        private const string LogisticsCargoCat = AssetCategories.LogisticsCargo;

        public bool? AutoEligible(string category, string lower, AssetTraits traits)
        {
            if (category == null || !category.StartsWith("logistics.", System.StringComparison.Ordinal)) return null;
            // Industrial props are exactly what this type wants, so the general "no crates/trucks" exclusions do not apply.
            if (Has(lower, "random", "placeholder", "template", "test", "dummy", "advert", "billboard", "logo", "broken", "burnt", "wreck")) return false;
            if (category == AssetCategories.LogisticsTruck && Has(lower, "fire", "police", "ambulance", "garbage")) return false;
            return true;
        }
    }
}
