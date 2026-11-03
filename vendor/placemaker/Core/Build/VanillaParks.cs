// Placemaker — stats of vanilla parks, plazas, playgrounds and sports fields (GPL-3.0).
//
// Read from the CS2 1.6.2f1 prefabs (base game resources.assets, Prefabs_FreeUpdate02.cok, Modern
// Architecture Prefabs.cok) on 2026-10-14 — see docs/PARK-STATS.md for the method and the formulas.
// Values are the prefab components exactly as the game reads them:
//   Attraction.m_Attractiveness, LeisureProvider.m_Efficiency/m_LeisureType, ServiceCoverage
//   m_Range/m_Capacity/m_Magnitude, ServiceConsumption.m_Upkeep (¢/month), PlaceableObject
//   m_ConstructionCost/m_XPReward, Park.m_MaintenancePool, BuildingPrefab.m_LotWidth/m_LotDepth (8 m cells).
// Used to calibrate ParkStatsEstimator and as the "vanilla range" the player can clamp to.

using System.Collections.Generic;

namespace Placemaker.Core.Build
{
    public enum VanillaParkKind { Pocket = 0, Park = 1, Plaza = 2, Playground = 3, Sports = 4, Nature = 5, Unique = 6 }

    /// <summary>Game LeisureType values a park can provide (Game.Agents.LeisureType names).</summary>
    public enum LeisureKind { CityPark = 5, CityBeach = 6, CityIndoors = 3 }

    public sealed class VanillaPark
    {
        public string Id = "";
        public string Name = "";
        public VanillaParkKind Kind;
        public int LotWidth, LotDepth;
        public int Cost, Upkeep, Attractiveness, Leisure;
        public LeisureKind LeisureType;
        public float Range, Capacity, Magnitude;
        public int MaintenancePool, Xp;
        public string Source = "";

        /// <summary>Lot area in m² (one cell = 8 m × 8 m).</summary>
        public double AreaM2 => LotWidth * LotDepth * 64.0;

        /// <summary>Game localisation key of the display name.</summary>
        public string NameKey => "Assets.NAME[" + Id + "]";

        /// <summary>Stat value by ParkStatIds id (NaN for unknown ids).</summary>
        public double Value(string statId)
        {
            switch (statId)
            {
                case ParkStatIds.Attractiveness: return Attractiveness;
                case ParkStatIds.Leisure: return Leisure;
                case ParkStatIds.Range: return Range;
                case ParkStatIds.Capacity: return Capacity;
                case ParkStatIds.Entertainment: return Magnitude;
                case ParkStatIds.Upkeep: return Upkeep;
                default: return double.NaN;
            }
        }
    }

    public static class VanillaParks
    {
        private static VanillaPark P(string id, string name, VanillaParkKind kind, int w, int d, int cost, int upkeep, int attractiveness,
            int leisure, LeisureKind type, float range, float capacity, float magnitude, int pool, int xp, string source) => new VanillaPark
        {
            Id = id, Name = name, Kind = kind, LotWidth = w, LotDepth = d, Cost = cost, Upkeep = upkeep, Attractiveness = attractiveness,
            Leisure = leisure, LeisureType = type, Range = range, Capacity = capacity, Magnitude = magnitude, MaintenancePool = pool,
            Xp = xp, Source = source,
        };

        /// <summary>Every surveyed park (regular and unique), smallest kinds first.</summary>
        public static readonly IReadOnlyList<VanillaPark> All = new List<VanillaPark>
        {
            P("PocketPark01", "Tiny Park", VanillaParkKind.Pocket, 1, 1, 1500, 400, 1, 1, LeisureKind.CityPark, 500f, 200f, 8f, 250, 10, "Base game"),
            P("PocketPark02", "Mini Park", VanillaParkKind.Pocket, 1, 1, 1500, 400, 1, 1, LeisureKind.CityPark, 500f, 200f, 8f, 250, 10, "Base game"),
            P("PocketPark03", "Urban Parkette", VanillaParkKind.Pocket, 2, 1, 2500, 600, 1, 1, LeisureKind.CityPark, 1000f, 400f, 8f, 250, 10, "Base game"),
            P("PocketPark04", "Canopy-Covered Parkette", VanillaParkKind.Pocket, 2, 1, 2500, 600, 1, 1, LeisureKind.CityPark, 1000f, 400f, 8f, 250, 10, "Base game"),
            P("PocketPark05", "Urban Pocket Park", VanillaParkKind.Pocket, 2, 2, 5000, 1000, 1, 1, LeisureKind.CityPark, 2000f, 600f, 10f, 250, 50, "Base game"),
            P("PocketPark06", "Square Pocket Park", VanillaParkKind.Pocket, 2, 2, 5000, 1000, 1, 1, LeisureKind.CityPark, 2000f, 600f, 10f, 250, 50, "Base game"),
            P("PocketPark07", "Canopy-Covered Pocket Park", VanillaParkKind.Pocket, 4, 1, 5000, 1000, 1, 1, LeisureKind.CityPark, 2000f, 600f, 10f, 250, 50, "Base game"),
            P("PocketPark08", "Wide Pocket Park", VanillaParkKind.Pocket, 6, 1, 7500, 1500, 1, 1, LeisureKind.CityPark, 2250f, 700f, 10f, 250, 50, "Base game"),
            P("PocketPark09", "Modern Pocket Park", VanillaParkKind.Pocket, 1, 4, 5000, 1000, 1, 1, LeisureKind.CityPark, 2000f, 600f, 10f, 250, 50, "Base game"),
            P("PocketPark10", "Canopy-Covered Corridor Park", VanillaParkKind.Pocket, 1, 6, 7500, 1500, 1, 1, LeisureKind.CityPark, 2250f, 700f, 10f, 250, 50, "Base game"),
            P("CityPark05", "Tiny City Park", VanillaParkKind.Park, 2, 3, 7500, 1500, 1, 1, LeisureKind.CityPark, 2250f, 700f, 10f, 250, 50, "Base game"),
            P("CityPark01", "Small Park", VanillaParkKind.Park, 4, 4, 10000, 2000, 1, 1, LeisureKind.CityPark, 2500f, 800f, 10f, 250, 50, "Base game"),
            P("BicyclePark01", "Bicycle Park", VanillaParkKind.Park, 4, 4, 10000, 2000, 1, 1, LeisureKind.CityPark, 2500f, 800f, 10f, 250, 50, "Base game"),
            P("DogPark01", "Dog Park", VanillaParkKind.Park, 4, 3, 10000, 2000, 1, 2, LeisureKind.CityPark, 3500f, 800f, 10f, 500, 50, "Base game"),
            P("CityPark06", "Ring Park", VanillaParkKind.Park, 5, 5, 15000, 3000, 3, 2, LeisureKind.CityPark, 3200f, 1000f, 10f, 250, 50, "Base game"),
            P("CityPark09", "Passage Park", VanillaParkKind.Park, 4, 12, 16800, 3360, 4, 2, LeisureKind.CityPark, 2000f, 1000f, 10f, 400, 50, "Base game"),
            P("CityPark08", "Hedge Park", VanillaParkKind.Park, 8, 8, 33600, 6750, 8, 3, LeisureKind.CityPark, 2400f, 1250f, 12f, 400, 100, "Base game"),
            P("CityPark03", "Large City Park", VanillaParkKind.Park, 10, 8, 42000, 8500, 10, 3, LeisureKind.CityPark, 3000f, 1600f, 14f, 500, 100, "Base game"),
            P("CityPark07", "Multipurpose Park", VanillaParkKind.Park, 8, 16, 50000, 10000, 13, 5, LeisureKind.CityPark, 3600f, 1800f, 12f, 450, 150, "Base game"),
            P("CityPark11", "Winding Park", VanillaParkKind.Park, 12, 12, 70000, 14000, 20, 7, LeisureKind.CityPark, 5000f, 2500f, 14f, 625, 150, "Base game"),
            P("ParkCafe01", "Park Cafe", VanillaParkKind.Park, 6, 6, 20000, 4000, 5, 2, LeisureKind.CityPark, 4000f, 1200f, 10f, 250, 50, "Free update"),
            P("DogPark02", "Dog Activity Park", VanillaParkKind.Park, 10, 6, 42000, 8500, 10, 3, LeisureKind.CityPark, 3000f, 1600f, 14f, 500, 100, "Free update"),
            P("RS_CityPark01", "Community City Park", VanillaParkKind.Park, 15, 15, 63000, 15000, 18, 6, LeisureKind.CityPark, 4000f, 2000f, 15f, 600, 180, "Modern Architecture"),
            P("CityPark02", "Small Plaza", VanillaParkKind.Plaza, 6, 6, 20000, 4000, 5, 2, LeisureKind.CityPark, 4000f, 1200f, 10f, 250, 50, "Base game"),
            P("CityPark10", "Terrace Plaza", VanillaParkKind.Plaza, 6, 8, 70000, 14000, 20, 7, LeisureKind.CityPark, 5000f, 2500f, 14f, 625, 150, "Base game"),
            P("SunkenPlaza01", "Business Plaza", VanillaParkKind.Plaza, 8, 10, 90000, 18000, 25, 9, LeisureKind.CityPark, 6000f, 3000f, 16f, 750, 150, "Base game"),
            P("CityPark04", "Large Plaza", VanillaParkKind.Plaza, 12, 16, 56000, 12500, 15, 5, LeisureKind.CityPark, 4000f, 2000f, 14f, 500, 150, "Base game"),
            P("RS_CityPlaza", "Grand City Plaza", VanillaParkKind.Plaza, 28, 20, 128000, 30000, 99, 66, LeisureKind.CityPark, 5000f, 3500f, 14f, 1000, 240, "Modern Architecture"),
            P("Playground01", "Small Playground", VanillaParkKind.Playground, 2, 2, 5000, 1000, 1, 1, LeisureKind.CityPark, 2000f, 600f, 10f, 500, 50, "Base game"),
            P("Playground02", "Large Playground", VanillaParkKind.Playground, 3, 4, 14000, 2500, 5, 5, LeisureKind.CityPark, 2500f, 800f, 14f, 500, 50, "Base game"),
            P("Playground03", "Activity Playground", VanillaParkKind.Playground, 3, 3, 10000, 2000, 1, 1, LeisureKind.CityPark, 2500f, 800f, 10f, 1000, 50, "Free update"),
            P("Playground05", "Play Round", VanillaParkKind.Playground, 3, 3, 10000, 2000, 1, 1, LeisureKind.CityPark, 2500f, 800f, 10f, 250, 50, "Free update"),
            P("Playground04", "Hilly Playground", VanillaParkKind.Playground, 4, 4, 10000, 2000, 1, 1, LeisureKind.CityPark, 2500f, 800f, 10f, 250, 50, "Free update"),
            P("ChirperPark01", "Chirplet Park", VanillaParkKind.Playground, 2, 2, 5000, 1000, 1, 1, LeisureKind.CityPark, 2500f, 600f, 10f, 500, 50, "Free update"),
            P("ChirperPark02", "Chirper Park", VanillaParkKind.Playground, 4, 4, 10000, 2000, 1, 1, LeisureKind.CityPark, 2500f, 800f, 10f, 250, 50, "Free update"),
            P("ChirperPark03", "Chirper Adventure Park", VanillaParkKind.Playground, 6, 8, 33600, 6750, 8, 3, LeisureKind.CityPark, 2400f, 1250f, 12f, 400, 100, "Free update"),
            P("ChirperPark04", "Chirper Fun Time Complex", VanillaParkKind.Playground, 8, 10, 42000, 8500, 10, 3, LeisureKind.CityPark, 3000f, 1600f, 14f, 500, 100, "Free update"),
            P("GymPark01", "Outdoor Gym", VanillaParkKind.Sports, 2, 2, 15000, 3000, 5, 5, LeisureKind.CityPark, 3500f, 1000f, 10f, 1000, 50, "Base game"),
            P("BasketballCourt01", "Basketball Court", VanillaParkKind.Sports, 3, 4, 14000, 2500, 5, 5, LeisureKind.CityPark, 3500f, 800f, 14f, 1000, 50, "Base game"),
            P("TennisCourt01", "Tennis Court", VanillaParkKind.Sports, 4, 7, 14000, 2500, 5, 5, LeisureKind.CityPark, 3500f, 800f, 14f, 1000, 50, "Base game"),
            P("SkatePark01", "Skate Park", VanillaParkKind.Sports, 8, 4, 15000, 3000, 5, 5, LeisureKind.CityPark, 3500f, 1000f, 10f, 1000, 50, "Base game"),
            P("CommunityPool01", "Community Pool", VanillaParkKind.Sports, 7, 7, 80000, 8000, 15, 20, LeisureKind.CityBeach, 3500f, 2000f, 16f, 2000, 150, "Base game"),
            P("BasketballCourt04", "Basketball Center", VanillaParkKind.Sports, 8, 6, 80000, 8000, 15, 20, LeisureKind.CityPark, 3500f, 2000f, 16f, 2000, 150, "Free update"),
            P("FootballField01", "Soccer Field", VanillaParkKind.Sports, 12, 15, 160000, 15000, 25, 20, LeisureKind.CityPark, 4000f, 4000f, 16f, 2000, 300, "Base game"),
            P("AmericanFootballField01", "American Football Field", VanillaParkKind.Sports, 20, 12, 160000, 15000, 25, 20, LeisureKind.CityPark, 4000f, 4000f, 16f, 2000, 300, "Base game"),
            P("CampfireSite01", "Campfire Site", VanillaParkKind.Nature, 8, 14, 30000, 6000, 10, 3, LeisureKind.CityPark, 6000f, 1600f, 10f, 500, 100, "Base game"),
            P("CampfireSite02", "Large Campfire Site", VanillaParkKind.Nature, 11, 10, 30000, 6000, 10, 3, LeisureKind.CityPark, 6000f, 1600f, 10f, 500, 100, "Base game"),
            P("FountainPlaza01", "Fountain Plaza", VanillaParkKind.Unique, 14, 14, 1600000, 80000, 100, 50, LeisureKind.CityPark, 7500f, 10000f, 16f, 4000, 2000, "Base game"),
            P("BotanicalGarden01", "Botanical Garden", VanillaParkKind.Unique, 12, 16, 1600000, 80000, 125, 100, LeisureKind.CityPark, 10000f, 15000f, 20f, 4000, 2000, "Base game"),
            P("WaterPark01", "Water Park", VanillaParkKind.Unique, 12, 6, 800000, 40000, 75, 25, LeisureKind.CityBeach, 5000f, 3000f, 16f, 3000, 1000, "Base game"),
            P("BronzeStatue01", "Bronze Statue", VanillaParkKind.Unique, 9, 9, 3200000, 160000, 50, 25, LeisureKind.CityPark, 5000f, 5000f, 16f, 3000, 4000, "Base game"),
            P("NotreDame", "Notre-Dame", VanillaParkKind.Unique, 10, 25, 5600000, 280000, 175, 125, LeisureKind.CityPark, 7500f, 20000f, 20f, 3500, 7000, "Base game"),
        };

        private static List<VanillaPark> _regular;

        /// <summary>Everything a normal (non-unique, non-signature) park building can be: the "vanilla range".</summary>
        public static IReadOnlyList<VanillaPark> Regular
        {
            get
            {
                if (_regular != null) return _regular;
                var list = new List<VanillaPark>();
                foreach (var p in All) if (p.Kind != VanillaParkKind.Unique) list.Add(p);
                return _regular = list;
            }
        }

        public static VanillaPark Find(string id)
        {
            foreach (var p in All) if (p.Id == id) return p;
            return null;
        }
    }
}
