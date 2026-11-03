namespace Placemaker.Core
{
    /// <summary>
    /// Shared asset-category vocabulary between layers (which ask for "a bench") and the game
    /// catalog (which classifies prefabs into these). Add new categories here; the catalog maps
    /// unknown categories to an empty list instead of failing.
    /// </summary>
    public static partial class AssetCategories
    {
        // Surfaces (area prefabs of type Surface)
        public const string SurfaceAny = "surface";
        public const string SurfaceGrass = "surface.grass";
        public const string SurfacePaving = "surface.paving";
        public const string SurfaceAsphalt = "surface.asphalt";
        public const string SurfaceNatural = "surface.natural";   // sand, gravel, dirt, forest floor

        // Vegetation
        public const string Tree = "tree";
        public const string Bush = "bush";
        public const string Planter = "planter";
        public const string Flowerbed = "flowerbed";

        // Furniture & features
        public const string Bench = "bench";
        public const string Lamp = "lamp";
        public const string Bin = "bin";
        public const string Centerpiece = "centerpiece";   // fountains, statues, monuments
        public const string Playground = "playground";
        public const string Sports = "sports";             // courts, goals, fitness
        public const string Restroom = "restroom";
        public const string Kiosk = "kiosk";               // food stands, ice cream, small service props
        public const string Shelter = "shelter";           // pavilions, gazebos, picnic shelters
        public const string Picnic = "picnic";             // picnic tables, BBQ
        public const string Camping = "camping";           // tents, campfires, caravans
        public const string CampingPitch = "camping.pitch"; // automatic pitch contents: tents, caravans (subset of camping)
        public const string CampingFire = "camping.fire";   // automatic campfires / fire pits (subset of camping)
        public const string Decoration = "decoration";     // misc props
        public const string Fence = "fence";               // net fences or fence props
        public const string Bollard = "bollard";
        public const string Rock = "rock";                 // rocks, boulders, cliffs (natural decor, "rocky outcrop")

        // Decals (static objects whose meshes are all decal meshes). "decal" = every usable decal;
        // the themed sub-categories are automatic pools for decal scatter.
        public const string Decal = "decal";
        public const string DecalLeaves = "decal.leaves";
        public const string DecalDirt = "decal.dirt";
        public const string DecalCracks = "decal.cracks";
        public const string DecalPuddles = "decal.puddles";
        public const string DecalPaving = "decal.paving";
        public const string DecalMarkings = "decal.markings";
        public const string DecalGraffiti = "decal.graffiti";
        public const string DecalLitter = "decal.litter";    // trash, litter, debris (yards, landfills); falls back to decal.dirt

        // Networks
        public const string Road = "net.road";
        public const string Pathway = "net.pathway";
        public const string InvisiblePath = "net.invisiblePath";

        // Parking
        public const string ParkingDecal = "parking.decal";
        public const string Charger = "parking.charger";

        // Zoning
        public const string Zone = "zone";

        // ───── Leisure workstream (round 4). Subcategories ("a.b") are always ALSO classified as their
        // parent where one exists, so a layer can ask for the specific thing and fall back to the general
        // one. Curation against ModsData/Placemaker/catalog-dump.json happens in Core/Build/LeisureHeuristics.cs;
        // docs/proposals/parkplaza.md lists which element uses which category.

        // Surfaces
        public const string SurfaceSand = "surface.sand";       // beach / play sand
        public const string SurfaceDirt = "surface.dirt";       // dirt, soil, mud: allotment plots, dog runs, trails
        public const string SurfaceWater = "surface.water";     // water-look decorative surfaces (reflecting pools)

        // Garden & park structures
        public const string Hedge = "hedge";                          // hedge pieces / topiary rows (also fence)
        public const string Glasshouse = "garden.glasshouse";         // greenhouse / glasshouse props
        public const string GardenShed = "garden.shed";               // sheds, water tanks, compost bins
        public const string GardenCrop = "garden.crop";               // vegetable rows, crops, raised beds
        public const string Stage = "stage";                          // stage, bandstand, podium
        public const string Lookout = "lookout";                      // lookout / observation tower, viewpoint
        public const string CafeSeating = "seating.cafe";             // cafe tables, parasols, chess tables
        public const string MarketStall = "kiosk.stall";              // market stalls, awnings (also kiosk)
        public const string CenterpieceFountain = "centerpiece.fountain";
        public const string CenterpieceStatue = "centerpiece.statue"; // statues, monuments, clocks, obelisks
        public const string WaterJets = "water.jets";                 // splash pad jets, sprinklers
        public const string Grave = "cemetery.grave";                 // gravestones, tombs

        // Sports (all also "sports")
        public const string SportsGoal = "sports.goal";               // football / handball goals
        public const string SportsBasketball = "sports.basketball";   // hoops
        public const string SportsTennis = "sports.tennis";           // tennis nets / posts
        public const string SportsFitness = "sports.fitness";         // outdoor gym stations
        public const string SportsSkate = "sports.skate";             // ramps, rails, bowls
        public const string SportsStand = "sports.stand";             // bleachers / small stands (never automatic benches)
        public const string FloodLight = "lamp.flood";                // floodlights for pitches

        // Dogs
        public const string DogAgility = "dog.agility";               // jumps, tunnels, weave poles
        public const string DogAmenity = "dog.amenity";               // dog bins, drinking posts
    }
}
