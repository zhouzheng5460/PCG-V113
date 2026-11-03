// Art direction for the beach (art pass a3): explicit vanilla assets per style, used whenever the player
// left the matching asset control on automatic. Every list is vanilla (checked against the catalog dump);
// a playset without one of them falls back to the next id, then to the category pool (AutoAssets).
// Automatic pools are too broad for a beach: "beach.hut" resolves to a 40 m changing-room building and the
// lookout fallback of lifeguard towers to a 56 m fire-station upgrade, so neither is ever asked for here.

namespace Placemaker.Core.Layers.Types.Beach
{
    public static class BeachArt
    {
        /// <summary>Rippled beach sand (surface pack) first, vanilla sand behind it.</summary>
        public static readonly string[] Sand =
        {
            "Sand Surfaces and Beach Decals Sand Sand-01-Small-Ripples Surface", "Golden Sand Beaches Texture Pack Sand Golden Sand Base Surface",
            "Sand Surface 01", "Sand Surface 02",
        };

        /// <summary>
        /// Landings at the end of the beach accesses (s13): a wooden deck from the EAI surface pack when installed, else
        /// vanilla paving tiles. Small (5.5 × 4 m), so the access path ends ON something instead of in the sand.
        /// </summary>
        public static readonly string[] Landing = { "EAI - Surface Pack Wood Wood 002 Surface", "EAI - Surface Pack Wood Wood 001 Surface", "Tiles Surface 01", "Pavement Surface 01" };

        /// <summary>Lifeguard post (s13): vanilla has no lifeguard tower; the tall tennis umpire chair (3.2 m) reads as one.</summary>
        public const string LifeguardChair = "TennisUmpireChair01";
        public const string Lifebuoy = "LifeBuoyStation01";
        public const string Flag = "FlagPole01";

        public static readonly string[] Umbrellas = { "Parasol01", "Parasol02", "Parasol03" };
        public static readonly string[] Loungers = { "DeckChair02", "DeckChair03", "DeckChair01" };
        public static readonly string[] Kiosks = { "PierKiosk01", "PierKiosk02", "Kiosk01" };
        public static readonly string[] Benches = { "Pack10-Bench01", "Pack10-Bench03", "GardenBench01" };
        public static readonly string[] Lamps = { "LightpolePark02", "LightpolePark01" };
        public static readonly string[] Bins = { "Trashbin02", "Trashbin01" };

        /// <summary>(s20) Railings along real drops (cliff-top walks, lookouts): low concrete with a metal rail, never the 2 m pool fence.</summary>
        public static readonly string[] Railings = { "EU_FenceResidentialLow05", "EU_FenceResidentialLow01" };
        /// <summary>(s20) Lookout landings on a cliff top.</summary>
        public static readonly string[] Lookout = { "Pavement Surface 02", "Tiles Surface 01", "Pavement Surface 01" };
        /// <summary>(s20) Sand fences (off by default): vanilla has no timber sand fence; the plainest low fence net.</summary>
        public static readonly string[] SandFence = { "EU_FenceResidentialLow01", "EU_FenceResidentialLow03" };

        /// <summary>Marram-like low green clumps on the dunes (no flowers: dunes read as one grey-green mass).</summary>
        public static readonly string[] DuneGrass = { "GreenBushWildChild01", "GreenBushWildTeen01", "FlowerBushWildChild02" };
        public static readonly string[] Shrubs = { "GreenBushWild01", "GreenBushWildAdult01", "FlowerBushWild01" };

        public static string[] Trees(string style, string backdrop)
        {
            if (style == "river") return new[] { "EU_PoplarTree01", "EU_AlderTree01", "BirchTree01", "OakTree01" };
            if (style == "cove") return new[] { "PineTree01", "SpruceTree01", "OakTree01" };
            if (style == "volleyball") return new[] { "CoconutPalmTree01", "FloridaPalmTree01" };
            if (backdrop == "lawn") return new[] { "CoconutPalmTree01", "RoyalPalmTree01", "FloridaPalmTree01" };
            return new[] { "PineTree01", "PineTree01", "EuropeanSpruce01" };   // dunes: maritime pines
        }

        /// <summary>The player's list, or the curated one when the control is on automatic.</summary>
        public static string[] Or(string[] picked, string[] curated) => picked != null && picked.Length > 0 ? picked : curated;
        public static string[] Or(string picked, string[] curated) => string.IsNullOrEmpty(picked) ? curated : new[] { picked };
    }
}
