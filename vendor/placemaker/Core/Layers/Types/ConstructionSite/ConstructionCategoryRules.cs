// Classification for the construction / quarry categories (plug-in, discovered by CategoryRules).
// Name patterns follow vanilla CS2 CamelCase prefab names ("TrafficCone01", "ConstructionBarrier02",
// "PortableToilet01"…), matched on the lower-cased name. Rules only ADD categories and never stop
// classification, so the general heuristics (fence, restroom, rock…) and other type folders' rules
// still see the same asset. Decals are classified before plug-ins (mud/dirt decals use decal.dirt).

using System.Collections.Generic;
using Placemaker.Core.Build;

namespace Placemaker.Core.Layers.Types.ConstructionSite
{
    public sealed class ConstructionCategoryRules : ICategoryRules
    {
        public int Priority => 20;

        private static bool Has(string s, params string[] parts) => AssetHeuristics.ContainsAny(s, parts);

        public bool Classify(string lower, AssetTraits traits, List<string> r)
        {
            if (string.IsNullOrEmpty(lower)) return false;
            bool site = Has(lower, "construction", "roadwork", "road work", "worksite", "work site", "building site");

            if (Has(lower, "towercrane", "tower crane", "mobilecrane", "mobile crane", "constructioncrane", "construction crane")
                || (lower.Contains("crane") && !Has(lower, "bird", "origami", "harbor", "harbour", "port", "gantry", "ship")))
                r.Add(AssetCategories.ConstructionCrane);
            else if (Has(lower, "excavator", "bulldozer", "dozer", "digger", "backhoe", "wheelloader", "wheel loader", "frontloader",
                         "front loader", "roadroller", "road roller", "steamroller", "compactor", "cementmixer", "cement mixer",
                         "concretemixer", "concrete mixer", "dumptruck", "dump truck", "tipper", "grader", "drillrig", "drill rig",
                         "asphaltpaver", "paver machine", "skidsteer", "skid steer", "telehandler", "dredger")
                     || (site && Has(lower, "vehicle", "machine", "truck", "tractor")))
                r.Add(AssetCategories.ConstructionMachine);

            if (Has(lower, "portacabin", "porta cabin", "siteoffice", "site office", "sitecabin", "site cabin", "officecontainer",
                    "office container", "containeroffice", "workershut", "workers hut", "welfareunit", "welfare unit", "constructionoffice",
                    "construction office", "constructionhut", "construction hut", "constructioncabin", "construction cabin", "sitehut"))
                r.Add(AssetCategories.ConstructionCabin);
            if (Has(lower, "portabletoilet", "portable toilet", "portaloo", "porta potty", "portapotty", "chemicaltoilet", "chemical toilet",
                    "toiletcabin", "toilet cabin", "porta-john", "portajohn"))
                r.Add(AssetCategories.ConstructionToilet);
            if (Has(lower, "shippingcontainer", "shipping container", "storagecontainer", "storage container", "cargocontainer", "cargo container")
                || (lower.Contains("container") && site))
                r.Add(AssetCategories.ConstructionContainer);

            if (Has(lower, "jerseybarrier", "jersey barrier", "roadbarrier", "road barrier", "barricade", "roadblock", "road block",
                    "constructionbarrier", "construction barrier", "waterbarrier", "water barrier", "trafficbarrier", "traffic barrier")
                || (lower.Contains("barrier") && !Has(lower, "sound", "noise", "crash", "guard")))
                r.Add(AssetCategories.ConstructionBarrier);
            if ((Has(lower, "trafficcone", "traffic cone", "roadcone", "road cone", "safetycone", "safety cone", "delineator")
                 || (lower.Contains("cone") && !Has(lower, "pinecone", "pine cone", "icecream", "ice cream", "conifer", "silicone", "cornet"))))
                r.Add(AssetCategories.ConstructionCone);

            if (Has(lower, "pallet", "pipestack", "pipe stack", "pipes", "rebar", "brickstack", "brick stack", "bricks", "planks", "lumber",
                    "timberstack", "timber stack", "girder", "steelbeam", "steel beam", "cementbag", "cement bag", "sandbag", "sand bag",
                    "cabledrum", "cable drum", "cablereel", "cable reel", "spool", "buildingmaterial", "building material",
                    "constructionmaterial", "construction material", "concretepipe", "concrete pipe", "formwork", "concreteblock", "concrete block"))
                r.Add(AssetCategories.ConstructionMaterial);
            if (Has(lower, "scaffold")) r.Add(AssetCategories.ConstructionScaffold);

            bool rubble = Has(lower, "rubble", "debris", "brokenconcrete", "broken concrete", "concretechunk", "concrete chunk", "demolition");
            if (rubble) r.Add(AssetCategories.ConstructionRubble);
            if (rubble || Has(lower, "sandpile", "sand pile", "gravelpile", "gravel pile", "dirtpile", "dirt pile", "soilpile", "soil pile",
                    "stockpile", "heap", "aggregate", "spoil") || (lower.Contains("pile") && !Has(lower, "pillow", "compile")))
                r.Add(AssetCategories.ConstructionPile);

            if (Has(lower, "dumpster", "skip0", "skipbin", "skip bin", "rolloff", "roll-off", "wastecontainer", "waste container",
                    "rubbishcontainer", "rubbish container", "constructionskip", "construction skip"))
                r.Add(AssetCategories.ConstructionSkip);
            if (Has(lower, "roadworksign", "roadwork sign", "road work sign", "constructionsign", "construction sign", "menatwork", "men at work",
                    "warningsign", "warning sign", "detoursign", "detour sign", "danger sign", "dangersign", "nodiving", "no swimming", "noswimming")
                || (site && lower.Contains("sign")))
                r.Add(AssetCategories.ConstructionSign);
            if (Has(lower, "generator", "compressor", "fueltank", "fuel tank", "dieseltank", "diesel tank", "lightingtower", "lighting tower"))
                r.Add(AssetCategories.ConstructionGenerator);
            if (Has(lower, "crusher", "conveyor", "hopper", "screeningplant", "screening plant", "gravelplant", "gravel plant",
                    "aggregateplant", "aggregate plant", "quarry", "stonecrush"))
                r.Add(AssetCategories.QuarryPlant);
            return false;
        }

        public bool? AutoEligible(string category, string lower, AssetTraits traits)
        {
            if (category == null || !(category.StartsWith("construction.", System.StringComparison.Ordinal) || category == AssetCategories.QuarryPlant))
                return null;
            if (Has(lower, "random", "placeholder", "test", "chirper", "logo", "advert", "brand", "toy", "miniature")) return false;
            // Snow/winter variants look wrong on a muddy site in summer maps; keep them for explicit picks only.
            if (category == AssetCategories.ConstructionPile && Has(lower, "snow", "salt", "leaves", "leaf")) return false;
            return true;
        }
    }
}
