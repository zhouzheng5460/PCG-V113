// Curated defaults for the shared add-ons (art pass a6, 2026-10-13). Every list was picked from the
// player's real catalog dump (vanilla first; modded surface packs first only where a vanilla item
// follows as fallback). Used when the player left the asset control on "automatic": the shared
// automatic pools are uniform per category (one prefab per step) and sometimes wrong for leisure
// (water.jets = an agricultural sprinkler rig; sports.fitness = one chin-up bar; picnic = blankets
// before tables), so each feature names what it actually wants. Explicit ids that are not installed
// fall back through the list, then to the category (AutoAssets).

using Placemaker.Core.Layers.Common;

namespace Placemaker.Core.Layers.AddOns
{
    public static class AddOnAssets
    {
        // ── ground ──
        /// <summary>Light stone slabs for feature aprons (fountain, bandstand, kiosks, tables).</summary>
        public static readonly string[] Paving =
        {
            "G87 Urban Surfaces Paving Pack Tiles G87 Paving Slab A Beige Surface",
            "EAI - Surface Pack Tiles Grey Paving Stone Surface",
            "Tiles Surface 01", "Pavement Surface 01",
        };

        /// <summary>Warm herringbone/clay for a garden-like forecourt (bandstand, terrace).</summary>
        public static readonly string[] GardenPaving =
        {
            "G87 Urban Surfaces Paving Pack Brick G87 Clay Paver A Surface",
            "EAI - Surface Pack Tiles Herringbone Grey Paving Stone 001 Surface",
            "Tiles Surface 02", "Pavement Surface 01",
        };

        /// <summary>Splash pad deck: pale slabs that read wet next to the fountain.</summary>
        public static readonly string[] SplashDeck =
        {
            "G87 Urban Surfaces Paving Pack Tiles G87 Paving Slab A White Surface",
            "Tiles Surface 03", "Concrete Surface 01",
        };

        /// <summary>Soft play surface: bark mulch, else sand.</summary>
        public static readonly string[] PlaySurface = { "EAI - Surface Pack Ground Mulch Surface", "Sand Surface 01" };

        /// <summary>Planting soil under a flower border.</summary>
        public static readonly string[] BedSoil = { "EAI - Surface Pack Ground Mulch Surface", "Forestry Surface 01" };

        /// <summary>Fine gravel for fitness / dog-run floors.</summary>
        public static readonly string[] FineGravel =
        {
            "Terrain Surfaces Pack Rock gaga TTP - Small Pebbles M Surface",
            "EDT Cobblestone & Gravel Surface Texture Pack Rock Small Gravel Surface",
            "Sand Surface 02",
        };

        // ── objects ──
        /// <summary>Outdoor gym: seven different vanilla stations (the fitness pool only knows the chin-up bar).</summary>
        public static readonly string[] Fitness =
        {
            "GymParkChinUpBar01", "GymParkChestPress01", "GymParkParallelBars01", "GymParkDipStation01",
            "GymParkRower01", "GymParkPushUpBars01", "GymParkPaddleBike01",
        };

        /// <summary>Picnic tables (the picnic pool leads with blankets).</summary>
        public static readonly string[] PicnicTables = { "CampgroundTableset01", "CampgroundTableset02" };
        public static readonly string[] Grills = { "Grill01", "Grill03", "Grill02" };

        /// <summary>Park kiosks and food stands (never billboards or vending machines).</summary>
        public static readonly string[] Kiosks = { "Kiosk01", "Kiosk02", "Kiosk03", "FoodStall01", "FoodStall02", "FoodCart01" };

        public static readonly string[] Restroom = { "Outhouse02", "Outhouse01", "PortableToilet01" };

        /// <summary>No bandstand prefab ships: an open octagonal gazebo is the closest silhouette.</summary>
        public static readonly string[] Bandstand = { "Gazebo03", "Gazebo02", "Gazebo01" };
        public static readonly string[] Gazebo = { "Gazebo01", "Gazebo02", "ChirperGazebo01" };
        public static readonly string[] ConcertStage = { "StageSmall01", "Gazebo03" };

        /// <summary>Low ground-level fountain at the heart of a splash pad (no real jets are installed).</summary>
        public static readonly string[] SplashFountain = { "LargeFountain01", "SmallFountain03" };
        public static readonly string[] SmallFountains = { "SmallFountain01", "SmallFountain02", "SmallFountain03" };
        public static readonly string[] LargeFountains = { "LargeFountain02", "LargeFountain03", "LargeFountain01" };

        /// <summary>A garden bench (backrest, timber) for every leisure seat.</summary>
        public static readonly string[] Bench = { "GardenBench01", "GardenBench02", "Pack10-Bench01" };
        public static readonly string[] Bin = { "Trashbin02", "Trashbin01" };

        /// <summary>Planted beds: vanilla flowerbeds first, then the modular garden-bed set.</summary>
        public static readonly string[] Flowerbeds = { "Flowerbed01", "Flowerbed02" };

        /// <summary>The player's choice, else the curated default (first = preferred, rest = fallbacks).</summary>
        public static AssetRef Or(string chosen, string[] defaults) =>
            string.IsNullOrEmpty(chosen) ? AssetMix.One("", defaults) : AssetMix.One(chosen);

        /// <summary>Variety among the player's list, else among the curated list.</summary>
        public static AssetRef PickOr(string[] chosen, string[] defaults, uint variation)
        {
            bool any = false;
            if (chosen != null) foreach (var id in chosen) if (!string.IsNullOrEmpty(id)) { any = true; break; }
            return AssetMix.Pick(any ? chosen : defaults, variation);
        }

        /// <summary>Explicit footprint radius of the curated default (for spacing) when the player chose nothing.</summary>
        public static float Radius(SiteContext site, string chosen, string[] defaults, float fallback)
        {
            if (!string.IsNullOrEmpty(chosen)) return AssetMix.FootprintRadius(site, new AssetRef(chosen), fallback);
            foreach (var id in defaults)
            {
                float r = AssetMix.FootprintRadius(site, new AssetRef(id), -1f);
                if (r > 0f) return r;
            }
            return fallback;
        }
    }
}
