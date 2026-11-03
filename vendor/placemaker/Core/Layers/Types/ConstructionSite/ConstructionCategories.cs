// Asset vocabulary of the industrial work types (construction site, quarry). Constant names carry a
// Construction/Quarry prefix so they never collide with other type folders' partial declarations.
// Every subcategory degrades through a fallback chain in the layers (e.g. cabin → garden shed →
// shelter), so a site still builds something sensible with whatever is installed.

namespace Placemaker.Core
{
    public static partial class AssetCategories
    {
        public const string ConstructionCabin = "construction.cabin";         // site offices, portacabins, welfare units
        public const string ConstructionToilet = "construction.toilet";       // portable toilets
        public const string ConstructionContainer = "construction.container"; // storage / shipping containers
        public const string ConstructionMachine = "construction.machine";     // excavators, dozers, loaders, rollers, dump trucks
        public const string ConstructionCrane = "construction.crane";         // tower and mobile cranes
        public const string ConstructionBarrier = "construction.barrier";     // jersey barriers, road barriers, barricades
        public const string ConstructionCone = "construction.cone";           // traffic cones, delineators
        public const string ConstructionMaterial = "construction.material";   // pallets, pipes, rebar, bricks, lumber, cable drums
        public const string ConstructionPile = "construction.pile";           // sand / gravel / soil heaps
        public const string ConstructionRubble = "construction.rubble";       // rubble, debris, broken concrete
        public const string ConstructionSkip = "construction.skip";           // skips, dumpsters, roll-off bins
        public const string ConstructionScaffold = "construction.scaffold";   // scaffolding, formwork
        public const string ConstructionSign = "construction.sign";           // roadworks / warning / site signs
        public const string ConstructionGenerator = "construction.generator"; // generators, compressors, fuel tanks
        public const string QuarryPlant = "quarry.plant";                     // crushers, screens, conveyors, hoppers
    }
}
