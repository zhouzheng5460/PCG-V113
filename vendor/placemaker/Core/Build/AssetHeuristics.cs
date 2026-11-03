// Placemaker — name/trait heuristics for asset classification (GPL-3.0).
// Adapted from ParkManager's ParkAssetCatalogSystem (tomkpunkt, GPL-3.0). Gameplay components
// decide first (the game layer passes them in as AssetTraits); names are only the fallback,
// because custom assets name things in every language and convention.

using System;
using System.Collections.Generic;

namespace Placemaker.Core.Build
{
    [Flags]
    public enum AssetTraits
    {
        None = 0,
        StaticObject = 1 << 0,
        Building = 1 << 1,
        Plant = 1 << 2,
        Tree = 1 << 3,
        BenchActivity = 1 << 4,
        StreetLight = 1 << 5,
        NetFence = 1 << 6,
        Placeholder = 1 << 7,
        HasMesh = 1 << 8,
        HasSubLane = 1 << 9,           // object with sub lanes (parking decal = real stall)
        ParkingLaneSubLane = 1 << 10,  // one of the sub lanes is a parking lane
        HasIcon = 1 << 11,
        HasSubObjects = 1 << 12,
        Surface = 1 << 13,             // area prefab of type Surface
        SpaceArea = 1 << 14,           // area prefab of type Space
        LotArea = 1 << 15,
        Road = 1 << 16,
        Pathway = 1 << 17,
        Zone = 1 << 18,
        Charger = 1 << 19,             // EV charger (ChargingStation-like data)
        Marker = 1 << 20,              // marker object (invisible in game)
        Net = 1 << 21,
        // Component signals for automatic eligibility (FEEDBACK-2 #1). Read by the game layer.
        BrandObject = 1 << 22,         // BrandObjectData: advertising / company branding props
        NetAttached = 1 << 23,         // PlacementFlags RoadSide/RoadNode/RoadEdge/NetObject: street furniture (signs, signals)
        WallOrHanging = 1 << 24,       // PlacementFlags Wall/Hanging: building dressing, not free-standing
        WaterPlacement = 1 << 25,      // PlacementFlags Shoreline/Floating/Underwater/Waterway
        ExerciseActivity = 1 << 26,    // activity locations for push-ups, pull-ups…: outdoor gym
        Vanilla = 1 << 27,             // built-in content (prefab.isBuiltin)
        DecalMesh = 1 << 28,           // every mesh of the object is a decal (RenderPrefab with DecalProperties -> MeshFlags.Decal)
        // Pool hygiene (art-pass follow-up): never automatic, whatever the name says.
        BuildingPart = 1 << 29,        // BuildingExtensionPrefab / service upgrade: a piece of a building, not a prop
        Vehicle = 1 << 30,             // a parked-vehicle prop ("Prop_Tractor01", CarPrefab/TrainCarPrefab… icons)
        ActivityProp = 1 << 31,        // a citizen's hand-held activity prop (chainsaw, broom, ball, fishing rod)
    }

    public static class AssetHeuristics
    {
        private static readonly string[] PlanterNames = { "planter", "flowerpot", "flower pot", "plantpot", "plant pot", "plantbox", "plant box", "potted" };   // not "pot0": "ParkMaintenanceDepot01"

        public static bool ContainsAny(string value, params string[] parts)
        {
            foreach (var p in parts) if (value.IndexOf(p, StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        /// <summary>
        /// Every category this prefab belongs to (an asset can be several, e.g. a bush is also a
        /// planter candidate). Empty list = not useful to Placemaker.
        /// </summary>
        public static List<string> Classify(string name, AssetTraits t)
        {
            var r = ClassifyRaw(name, t);
            // Type rules and the general heuristics may add the same category twice ("tree" from the
            // farm rules and again here): duplicates doubled items in the automatic pools (playtest 4b).
            for (int i = r.Count - 1; i >= 0; i--)
                if (r[i] == null || r.IndexOf(r[i]) != i) r.RemoveAt(i);
            return r;
        }

        private static List<string> ClassifyRaw(string name, AssetTraits t)
        {
            var r = new List<string>(2);
            if (string.IsNullOrWhiteSpace(name)) return r;
            var lower = name.ToLowerInvariant();
            if ((t & AssetTraits.Placeholder) != 0 || lower.Contains("placeholder")) return r;

            if ((t & AssetTraits.Zone) != 0) { r.Add(AssetCategories.Zone); return r; }

            if ((t & AssetTraits.Surface) != 0)
            {
                r.Add(AssetCategories.SurfaceAny);
                var s = SurfaceKind(lower);
                if (s != null) r.Add(s);
                LeisureHeuristics.SurfaceExtra(lower, r);
                SurfaceCategoryRules.Classify(lower, r);   // type-defined surface kinds (rubber, bark, clay, track…)
                return r;
            }
            if ((t & AssetTraits.SpaceArea) != 0)
            {
                if (ContainsAny(lower, "hangaround", "hang around")) r.Add(BuildCategories.AreaHangaround);
                else r.Add(BuildCategories.AreaPedestrian);
                return r;
            }
            if ((t & AssetTraits.LotArea) != 0) { r.Add(BuildCategories.AreaLot); return r; }

            if ((t & AssetTraits.Net) != 0)
            {
                if ((t & AssetTraits.NetFence) != 0) r.Add(AssetCategories.Fence);
                else if ((t & AssetTraits.Pathway) != 0)
                {
                    if (ContainsAny(lower, "invisible", "marker")) r.Add(AssetCategories.InvisiblePath);
                    else r.Add(AssetCategories.Pathway);
                }
                else if ((t & AssetTraits.Road) != 0)
                {
                    if (ContainsAny(lower, "invisible")) r.Add(AssetCategories.InvisiblePath);
                    else r.Add(AssetCategories.Road);
                }
                // Type rules see nets too (g5): net.bike / net.trail tiers of the park path hierarchy. Additive,
                // and only "net.*" categories: object rules were written for props and must not pull a net
                // into an object pool.
                var extra = new List<string>(2);
                CategoryRules.Classify(lower, t, extra);
                foreach (var c in extra)
                    if (c != null && c.StartsWith("net.", StringComparison.Ordinal) && !r.Contains(c)) r.Add(c);
                return r;
            }

            if ((t & AssetTraits.StaticObject) == 0 || (t & AssetTraits.Building) != 0) return r;

            // Parking decals: a static object whose sub lane is a parking lane spawns a working stall.
            if ((t & AssetTraits.ParkingLaneSubLane) != 0)
            {
                r.Add(AssetCategories.ParkingDecal);
                // Bicycle racks / stands / shelters spawn bike parking lanes: they are the real racks of
                // "transport.bikeRack" (whose name pool only held parking-hall signs), never car stalls.
                if (IsBikeName(lower)) r.Add(AssetCategories.BikeRack);
                return r;
            }
            if ((t & AssetTraits.Charger) != 0 || (lower.Contains("charger") && lower.Contains("parking")))
            {
                r.Add(AssetCategories.Charger);
                return r;
            }

            // Decals: every mesh is a decal mesh (component signal, not the name).
            if ((t & AssetTraits.DecalMesh) != 0)
            {
                if ((t & AssetTraits.HasMesh) == 0 || ContainsAny(lower, "random", "placeholder")) return r;
                r.Add(AssetCategories.Decal);
                var theme = DecalTheme(name);
                if (theme != null) r.Add(theme);
                GroundCoverCategoryRules.ClassifyDecal(lower, r);   // farm wheel-rut decals (decal.farm, s8)
                // Type rules may add their own decal pools (sports field lines); additive, "decal.*" only.
                var decalExtra = new List<string>(2);
                CategoryRules.Classify(lower, t, decalExtra);
                foreach (var c in decalExtra)
                    if (c != null && c.StartsWith("decal.", StringComparison.Ordinal) && !r.Contains(c)) r.Add(c);
                return r;
            }

            // Name-identified special objects used by roads (mesh-less markers are allowed here).
            if (lower.StartsWith("culdesac", StringComparison.Ordinal)) { r.Add(BuildCategories.CulDeSacCap); return r; }
            if (lower.Contains("roundabout")) { r.Add(BuildCategories.RoundaboutIsland); return r; }
            if (ContainsAny(lower, "animal spawner", "animalspawner", "wildlife spawn")) { r.Add(BuildCategories.AnimalSpawner); return r; }

            // Everything below must be visible: mesh-less static objects crash when spawned mid-game.
            if ((t & AssetTraits.HasMesh) == 0) return r;
            if (ContainsAny(lower, "random", "source", "effect", "particle")) return r;
            if (CategoryRules.Classify(lower, t, r)) return r;
            if (LeisureHeuristics.Classify(lower, t, r)) return r;

            if ((t & AssetTraits.Plant) != 0)
            {
                if (ContainsAny(lower, PlanterNames)) { r.Add(AssetCategories.Planter); return r; }
                if (ContainsAny(lower, "flowerbed", "flower bed")) { r.Add(AssetCategories.Flowerbed); return r; }
                // Vanilla shrubs are tree prefabs too ("GreenBushWild01", "FlowerBushWild02"): a plant
                // named bush/shrub (or a "…Wild" flower) without "tree" is a bush, never a tree (playtest 4b).
                bool shrub = !lower.Contains("tree") && (ContainsAny(lower, "bush", "shrub") || (lower.Contains("wild") && (t & AssetTraits.Tree) != 0));
                if ((t & AssetTraits.Tree) != 0 && !shrub)
                {
                    if (ContainsAny(lower, "stump", "dead", "fallen", "log")) return r;
                    r.Add(AssetCategories.Tree);
                }
                // "...BushCity..." are shrubs in concrete planter boxes: planters, never bushes (playtest 2).
                else if (IsPlanterBoxName(lower)) r.Add(AssetCategories.Planter);
                else { r.Add(AssetCategories.Bush); r.Add(AssetCategories.Planter); }
                return r;
            }
            if ((t & AssetTraits.BenchActivity) != 0 || IsBenchName(lower)) { r.Add(AssetCategories.Bench); return r; }
            if ((t & AssetTraits.StreetLight) != 0 || ContainsAny(lower, "lamp", "lantern", "streetlight", "street light"))
            {
                r.Add(AssetCategories.Lamp);
                return r;
            }
            if (ContainsAny(lower, "fence", "railing", "hedge", "barrier")) { r.Add(AssetCategories.Fence); return r; }
            if (ContainsAny(lower, "bollard")) { r.Add(AssetCategories.Bollard); return r; }
            if (ContainsAny(lower, "trashbin", "trash bin", "trashcan", "trash can", "wastebin", "waste bin",
                "garbagebin", "garbage bin", "litterbin", "litter bin", "recycling bin")) { r.Add(AssetCategories.Bin); return r; }
            if (ContainsAny(lower, PlanterNames)) { r.Add(AssetCategories.Planter); return r; }
            if (ContainsAny(lower, "flowerbed", "flower bed")) { r.Add(AssetCategories.Flowerbed); return r; }
            if (ContainsAny(lower, "playground", "swing", "slide", "seesaw", "sandbox", "climbing", "carousel", "spring rider", "playcabin", "play cabin", "playhouse"))
            { r.Add(AssetCategories.Playground); return r; }
            if (ContainsAny(lower, "basketball", "tennis", "football", "soccer", "volleyball", "fitness", "goal", "court", "skate", "pingpong", "table tennis", "outdoor gym"))
            { r.Add(AssetCategories.Sports); return r; }
            if (IsRestroomName(name)) { r.Add(AssetCategories.Restroom); return r; }
            if (IsRockName(name)) { r.Add(AssetCategories.Rock); return r; }
            if (ContainsAny(lower, "picnic", "bbq", "barbecue", "grill")) { r.Add(AssetCategories.Picnic); return r; }
            if ((t & AssetTraits.ExerciseActivity) != 0) { r.Add(AssetCategories.Sports); return r; }
            if (ContainsAny(lower, "campfire", "camp fire", "firepit", "fire pit", "bonfire"))
            { r.Add(AssetCategories.Camping); r.Add(AssetCategories.CampingFire); return r; }
            if (AssetCuration.HasWord(name, "tent", "tents", "caravan", "camper", "campervan"))   // words: "EU_DoNotEnter01" is no tent
            { r.Add(AssetCategories.Camping); r.Add(AssetCategories.CampingPitch); return r; }
            if (ContainsAny(lower, "camping")) { r.Add(AssetCategories.Camping); return r; }
            if (ContainsAny(lower, "kiosk", "food stand", "foodstand", "ice cream", "icecream", "hotdog", "vendor", "stall", "foodtruck", "food truck"))
            { r.Add(AssetCategories.Kiosk); return r; }
            if (ContainsAny(lower, "pavilion", "gazebo", "shelter", "pergola", "canopy", "bandstand")) { r.Add(AssetCategories.Shelter); return r; }
            if ((t & AssetTraits.HasSubObjects) == 0 && (t & AssetTraits.HasIcon) != 0
                && ContainsAny(lower, "fountain", "statue", "monument", "sculpture", "obelisk"))
            { r.Add(AssetCategories.Centerpiece); return r; }
            if (ContainsAny(lower, "sign", "poster", "billboard", "advert", "decal", "shadow", "broken", "ruin", "graffiti")) return r;
            if ((t & AssetTraits.HasIcon) != 0 && ContainsAny(lower, "park", "plaza", "garden", "decor", "flag", "bird", "rock", "stone", "boulder"))
                r.Add(AssetCategories.Decoration);
            return r;
        }

        // ───────────────────────── automatic eligibility (FEEDBACK-2 #1) ─────────────────────────

        /// <summary>Never picked automatically for any object category (explicit picks still may).</summary>
        private static readonly string[] AutoExcludedNames =
        {
            "advert", "billboard", "poster", "banner", "sign", "board", "sandwich", "menu", "logo", "neon", "screen",
            "stadium", "spectator", "bleacher", "grandstand", "tribune", "standlarge", "standsmall",   // ad stands ("Stand01 - Brand"): AssetCuration.NeverAutomatic
            "construction", "roadwork", "road work", "traffic", "cone", "barricade", "scaffold", "excavat",
            "crate", "pallet", "cardboard", "pile", "debris", "rubble", "garbage bag", "trashbag", "dumpster",
            "vehicle", "truck", "trailer", "forklift", "boat", "atm", "mailbox", "phonebooth", "phone booth", "vending",
            "random", "placeholder", "template", "lightsource", "light source", "chirper", "dummy", "test",
        };

        /// <summary>Per-category extra exclusions for automatic picks.</summary>
        private static string[] AutoExcludedFor(string category)
        {
            switch (category)
            {
                case AssetCategories.Bench: return new[] { "sport", "tennis", "dugout", "pier", "bus", "stop", "car seat", "toilet" };
                case AssetCategories.Fence: return new[] { "barrier", "construction", "temporary", "security", "crowd", "jump", "industrial", "barbed", "police", "random" };
                case AssetCategories.Lamp: return new[] { "flood", "stadium", "sport", "wall", "neon", "christmas", "decoration" };
                case AssetCategories.Centerpiece: return new[] { "drinking", "dog", "logo" };
                case AssetCategories.Playground: return new[] { "dog" };
                case AssetCategories.Bin: return new[] { "container", "dumpster", "industrial" };
                case AssetCategories.Shelter: return new[] { "bus", "tram", "taxi", "bicycle", "bike", "smoking", "stop" };
                default: return Array.Empty<string>();
            }
        }

        /// <summary>Decoration is a grab bag: only these are safe to scatter automatically.</summary>
        private static readonly string[] AutoDecorationNames = { "birdbath", "bird bath", "sculpture" };

        /// <summary>
        /// May an item of <paramref name="category"/> be picked when the player chose nothing? Component
        /// data first (brand/advertising, wall/hanging, street-attached and water placement are never
        /// automatic for objects), names as a fallback with exclusion lists. Nets, surfaces and areas
        /// are always eligible except fences, which share the name exclusions.
        /// </summary>
        public static bool AutoEligible(string category, string name, AssetTraits t)
        {
            if (string.IsNullOrEmpty(category) || string.IsNullOrWhiteSpace(name)) return false;
            var lower = name.ToLowerInvariant();
            if ((t & AssetTraits.Placeholder) != 0 || lower.Contains("placeholder")) return false;
            bool isNet = (t & AssetTraits.Net) != 0;
            bool isArea = (t & (AssetTraits.Surface | AssetTraits.SpaceArea | AssetTraits.LotArea | AssetTraits.Zone)) != 0;
            if (category == AssetCategories.Fence)
            {
                if (ContainsAny(lower, AutoExcludedFor(category))) return false;
                return isNet || ContainsAny(lower, "fence", "railing", "hedge");
            }
            if (isNet)
            {
                // Bike nets may carry PathwayData: never an automatic footpath (g5 / g3 request).
                if (category == AssetCategories.Pathway && IsBikeName(lower)) return false;
                var netRuled = CategoryRules.AutoEligible(category, lower, t);
                if (netRuled.HasValue) return netRuled.Value;
                return true;
            }
            if (isArea) return true;
            // Objects.
            if ((t & (AssetTraits.BrandObject | AssetTraits.WallOrHanging | AssetTraits.WaterPlacement)) != 0) return false;
            // Shared hygiene for every pool, present and future: building parts, hand-held activity props,
            // vehicles in leisure pools, advertising/shop props, bridge pillars, per-category mismatches.
            if (AssetCuration.NeverAutomatic(category, name, t)) return false;
            if (category == AssetCategories.ParkingDecal && IsBikeName(lower)) return false;   // bike racks are not car stalls
            // Leisure pools never take machinery ("AgricultureSprinklerSystem" as splash-pad jets).
            if (AssetCuration.IsMachineryForLeisure(category, lower)) return false;
            // Decals: curated themes (AssetCuration) — natural park looks, never letters or road markings.
            if (category == AssetCategories.DecalFarm) return GroundCoverCategoryRules.DecalAuto(lower);
            if (category == AssetCategories.Decal || category.StartsWith("decal.", StringComparison.Ordinal))
            {
                var decalRuled = CategoryRules.AutoEligible(category, lower, t);
                if (decalRuled.HasValue) return decalRuled.Value;
                return AssetCuration.DecalAutoEligible(category, name);
            }
            // Built-in prop names are CamelCase without spaces; "HighSchool03 Rooftop Garden" or
            // "TrainStation02 Ticket Machines" are building extensions that only look like props (4b).
            if ((t & AssetTraits.Vanilla) != 0 && name.IndexOf(' ') >= 0) return false;
            // Core categories: vanilla allow-lists, modded name fallbacks minus known mismatches.
            var curated = AssetCuration.AutoEligible(category, name, t);
            if (curated == false) return false;
            if (curated == true && (t & AssetTraits.Vanilla) != 0) return true;
            var ruled = CategoryRules.AutoEligible(category, lower, t);
            if (ruled.HasValue) return ruled.Value;
            if (LeisureHeuristics.AutoEligible(category, lower, out bool leisure)) return leisure;
            if ((t & AssetTraits.NetAttached) != 0 && category != AssetCategories.Lamp && category != AssetCategories.Bollard
                && category != AssetCategories.ParkingDecal && category != AssetCategories.Charger) return false;
            if (category == AssetCategories.ParkingDecal || category == AssetCategories.Charger || category.StartsWith("object.", StringComparison.Ordinal))
                return true;   // functional parking/road objects: classified by sub lanes / names already
            if (ContainsAny(lower, AutoExcludedNames)) return false;
            if (ContainsAny(lower, AutoExcludedFor(category))) return false;
            switch (category)
            {
                case AssetCategories.Bush:
                case AssetCategories.Tree:
                    // Vegetation pools: real plants only, never planter boxes/pots or dead plants (playtest 2).
                    return !IsPlanterBoxName(lower) && !ContainsAny(lower, "dead", "stump", "fallen");
                case AssetCategories.Restroom:
                    return IsRestroomName(name);
                case AssetCategories.Rock:
                    return !ContainsAny(lower, "cliff");   // cliffs are huge; explicit picks only
                case AssetCategories.Bench:
                    // A real bench: says so, or has a bench-sitting spot and is not just "a seat".
                    return lower.Contains("bench") || lower.Contains("bank") ||
                           ((t & AssetTraits.BenchActivity) != 0 && !lower.Contains("seat") && !lower.Contains("chair"));
                case AssetCategories.Decoration:
                    return ContainsAny(lower, AutoDecorationNames);
                default:
                    return true;
            }
        }

        /// <summary>
        /// Vanilla park palettes suitable for dressing a general park: themed facilities (sports grounds,
        /// skate/dog/gym parks, water parks, schools) bring signs, stands and equipment that do not belong
        /// in an ordinary park.
        /// </summary>
        public static bool IsGeneralParkPalette(string paletteOrParkName) => AssetCuration.IsGeneralParkPalette(paletteOrParkName);

        /// <summary>Name split into lower-case words at camel-case, digit and punctuation boundaries.</summary>
        public static List<string> Words(string name)
        {
            var words = new List<string>();
            if (string.IsNullOrEmpty(name)) return words;
            var cur = new System.Text.StringBuilder();
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                if (!char.IsLetter(c))
                {
                    if (cur.Length > 0) { words.Add(cur.ToString()); cur.Clear(); }
                    continue;
                }
                bool boundary = cur.Length > 0 && char.IsUpper(c) &&
                    (char.IsLower(name[i - 1]) || (i + 1 < name.Length && char.IsLower(name[i + 1]) && char.IsUpper(name[i - 1])));
                if (boundary) { words.Add(cur.ToString()); cur.Clear(); }
                cur.Append(char.ToLowerInvariant(c));
            }
            if (cur.Length > 0) words.Add(cur.ToString());
            return words;
        }

        /// <summary>
        /// Real restrooms only. "wc" must be its own word: "WindowCleaningPlatform" contains "wC" across a
        /// word boundary and was classified as a restroom (playtest 2).
        /// </summary>
        public static bool IsRestroomName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            var lower = name.ToLowerInvariant();
            if (ContainsAny(lower, "window", "cleaning", "paper", "brush", "sign")) return false;
            if (ContainsAny(lower, "toilet", "restroom", "outhouse", "latrine", "lavator", "portapotty", "porta potty", "privy",
                "bathroom", "washroom", "comfort station")) return true;
            foreach (var w in Words(name)) if (w == "wc" || w == "loo") return true;
            return false;
        }

        /// <summary>Natural rocks: rocks, boulders, cliffs (not rockets, brands, tombstones, standing stones).</summary>
        public static bool IsRockName(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Contains(" - ")) return false;   // "Billboard01 - GrindARock": brand variants
            if (ContainsAny(name.ToLowerInvariant(), "billboard", "poster", "sign", "screen", "pylon", "stand0", "rocket")) return false;
            foreach (var w in Words(name))
                if (w == "rock" || w == "rocks" || w == "boulder" || w == "boulders" || w == "cliff" || w == "cliffs" || w == "outcrop") return true;
            return false;
        }

        /// <summary>Shrubs in a concrete box / pot (vanilla "GreenBushCity01", "FlowerBushCity02", "PlantPot01").</summary>
        public static bool IsPlanterBoxName(string lower) =>
            ContainsAny(lower, PlanterNames) || ContainsAny(lower, "bushcity", "bush city", "citybush", "city bush", "gardenbed", "garden bed", "box", "planter", "potted");

        /// <summary>Theme sub-category of a decal by name (null = no theme; still in "decal").</summary>
        /// <summary>Theme sub-category of a decal by name (null = no theme; still in "decal"). Word-based, last theme word wins (AssetCuration).</summary>
        public static string DecalTheme(string name) => AssetCuration.DecalTheme(name);

        public static string SurfaceKind(string lower)
        {
            if (ContainsAny(lower, "grass", "lawn", "meadow", "turf")) return AssetCategories.SurfaceGrass;
            if (ContainsAny(lower, "asphalt", "tarmac", "parking")) return AssetCategories.SurfaceAsphalt;
            if (ContainsAny(lower, "pavement", "paving", "tile", "cobble", "brick", "concrete", "plaza", "stone")) return AssetCategories.SurfacePaving;
            if (ContainsAny(lower, "sand", "gravel", "dirt", "soil", "forest", "mud", "rock", "clay", "earth")) return AssetCategories.SurfaceNatural;
            return null;
        }

        public static bool IsBikeName(string lower) => ContainsAny(lower, "bike", "bicycle", "cycle");

        // ───────────────────────── prefab-kind signals (pool hygiene) ─────────────────────────

        /// <summary>Traits implied by a prefab class name (the game passes every class of the prefab's hierarchy).</summary>
        public static AssetTraits TraitsFromTypeName(string typeName)
        {
            switch (typeName)
            {
                case "BuildingExtensionPrefab": return AssetTraits.BuildingPart;
                case "ActivityPropPrefab": return AssetTraits.ActivityProp;
                case "VehiclePrefab": case "CarPrefab": case "CarTrailerPrefab": case "CarTractorPrefab": case "TrainPrefab":
                case "TrainCarPrefab": case "TrainEnginePrefab": case "AircraftPrefab": case "AirplanePrefab": case "HelicopterPrefab":
                case "WatercraftPrefab": case "RocketPrefab": case "BicyclePrefab":
                    return AssetTraits.Vehicle;
                default: return AssetTraits.None;
            }
        }

        /// <summary>
        /// Traits implied by an icon url: Asset Icon Library and the thumbnail camera name the prefab class
        /// ("coui://ail/CarPrefab.Tractor01.png", "thumbnail://ThumbnailCamera/BuildingExtensionPrefab/…"),
        /// which also identifies static props made from vehicles ("Prop_Tractor01").
        /// </summary>
        public static AssetTraits TraitsFromIcon(string icon)
        {
            if (string.IsNullOrEmpty(icon)) return AssetTraits.None;
            var t = AssetTraits.None;
            int i = 0;
            while ((i = icon.IndexOf("Prefab", i, StringComparison.Ordinal)) > 0)
            {
                int s = i;
                while (s > 0 && char.IsLetter(icon[s - 1])) s--;
                t |= TraitsFromTypeName(icon.Substring(s, i + 6 - s));
                i += 6;
            }
            return t;
        }

        private static readonly string[] VehicleWords =
        {
            "truck", "trailer", "tractor", "harvester", "excavator", "loader", "vehicle", "rocket", "airplane", "aircraft", "helicopter",
            "ship", "train", "snowplow", "sprayer", "spreader", "plough", "sowing", "forwarder", "engine", "bus", "van", "taxi", "car",
            "forklift", "bulldozer", "crane", "boat", "tram", "motorcycle", "scooter",
        };

        /// <summary>A static prop made from a vehicle: "Prop_" + a vehicle word ("Prop_Trashbin01_Empty" is a bin).</summary>
        public static bool IsVehicleName(string name)
        {
            if (string.IsNullOrEmpty(name) || !name.StartsWith("Prop_", StringComparison.OrdinalIgnoreCase)) return false;
            var words = Words(name);
            // Loads of a vehicle ("Prop_CoalTruck01_CoalPile01_Full") are piles, not vehicles.
            foreach (var w in words) if (w == "pile" || w == "props") return false;
            foreach (var w in words) foreach (var v in VehicleWords) if (w == v) return true;
            return false;
        }

        public static bool IsBenchName(string lower)
        {
            // "bank" alone also matches advertised financial brands (PM measured this).
            if (!ContainsAny(lower, "bench", "seat", "parkbank", "gardenbank", "sitzbank", "streetbank")) return false;
            return !ContainsAny(lower, "advert", "billboard", "poster", "banner", "display", "sign", "adstand",
                "commercial", "logo", "screen", "adboard", "car seat", "toilet");
        }

        /// <summary>Score for picking the automatic default of a category (higher = better).</summary>
        public static int DefaultScore(string category, string name, float netWidth = 0f)
        {
            if (string.IsNullOrEmpty(name)) return int.MinValue;
            var lower = name.ToLowerInvariant();
            int score = 0;
            if (ContainsAny(lower, "placeholder")) score -= 500;
            switch (category)
            {
                case AssetCategories.SurfaceAny:
                case AssetCategories.SurfaceGrass:
                    if (lower.Contains("grass")) score += 100;
                    if (lower.Contains("01")) score += 10;
                    if (ContainsAny(lower, "pavement", "sand")) score -= 30;
                    break;
                case AssetCategories.SurfacePaving:
                    if (lower.Contains("pavement")) score += 100;
                    if (lower.Contains("01")) score += 10;
                    break;
                case AssetCategories.SurfaceAsphalt:
                    if (lower.Contains("asphalt")) score += 100;
                    if (lower.Contains("01")) score += 10;
                    break;
                case AssetCategories.SurfaceNatural:
                    if (lower.Contains("gravel")) score += 60;
                    if (lower.Contains("sand")) score += 40;
                    break;
                case AssetCategories.Tree:
                    if (ContainsAny(lower, "deciduous", "oak", "linden", "maple")) score += 40;
                    break;
                case AssetCategories.Bush:
                    if (lower.Contains("bush")) score += 50;
                    break;
                case AssetCategories.Bench:
                    if (lower.Contains("bench")) score += 50;
                    if (lower.Contains("garden")) score += 40;
                    if (ContainsAny(lower, "sport", "tennis", "stadium", "pier")) score -= 100;
                    if (lower.Contains("park")) score += 30;
                    break;
                case AssetCategories.Lamp:
                    if (lower.Contains("park")) score += 30;
                    if (lower.Contains("pathway") || lower.Contains("pedestrian")) score += 20;
                    break;
                case AssetCategories.Fence:
                    if (lower.Contains("fence")) score += 50;
                    if (lower.Contains("residentialpiecelow")) score += 250;
                    else if (lower.Contains("piece")) score += 100;
                    break;
                case AssetCategories.Bin:
                    if (ContainsAny(lower, "trash", "waste")) score += 50;
                    if (lower.Contains("park")) score += 25;
                    break;
                case AssetCategories.Centerpiece:
                    if (lower.Contains("fountain")) score += 100;
                    if (lower.Contains("statue")) score += 80;
                    if (ContainsAny(lower, "plaza", "central")) score += 30;
                    break;
                case AssetCategories.Pathway:
                    if (lower.Contains("gravel")) score -= 10;
                    if (IsBikeName(lower)) score -= 500;   // a footpath must not become a bike path
                    if (lower.StartsWith("pedestrian", StringComparison.Ordinal) || lower.Contains("pathway")) score += 40;
                    score -= (int)(netWidth * 2);   // narrow by default
                    // FEEDBACK-4: the thin vanilla "Pavement Path" leads the pool; wide walks, bridges, piers and quays never do.
                    if (lower.Contains("pavement path")) score += 120;
                    if (lower.Contains("wide")) score -= 60;
                    if (ContainsAny(lower, "bridge", "pier", "quay", "tunnel", "elevated")) score -= 300;
                    break;
                case AssetCategories.InvisiblePath:
                    // Drivable invisible road paths for parking (PLT used these vanilla names).
                    if (lower.StartsWith("invisible road path", StringComparison.Ordinal)) score += 100;
                    if (lower.StartsWith("invisible car path", StringComparison.Ordinal)) score += 80;
                    if (lower.Contains("1xtwoway")) score += 20;
                    if (lower.Contains("oneway")) score -= 30;
                    break;
                case AssetCategories.Road:
                    if (lower.Contains("small road") || lower.Contains("small")) score += 50;
                    if (lower.Contains("highway") || lower.Contains("highway")) score -= 200;
                    if (ContainsAny(lower, "oneway", "one-way", "tram", "bus", "gravel", "alley")) score -= 40;
                    break;
                case AssetCategories.ParkingDecal:
                    if (lower == "parkinglotdecal01") score += 200;
                    if (ContainsAny(lower, "disabled", "electric", "ev")) score -= 50;
                    break;
                case AssetCategories.Charger:
                    if (lower.Contains("parkinglotcharger")) score += 100;
                    break;
                case BuildCategories.CulDeSacCap:
                    if (lower.Contains("medium")) score += 20;
                    if (lower.EndsWith("02", StringComparison.Ordinal)) score += 10;
                    break;
                case BuildCategories.RoundaboutIsland:
                    if (lower.Contains("medium")) score += 20;
                    break;
            }
            if (ContainsAny(lower, "invisible", "marker") && category != AssetCategories.InvisiblePath) score -= 200;
            // Curated order: allow-listed built-ins in list order, decals by natural look (playtest 4b).
            score += AssetCuration.Score(category, name);
            score += GroundCoverCategoryRules.Score(category, lower);
            if (category == AssetCategories.Decal || category.StartsWith("decal.", StringComparison.Ordinal))
                score += AssetCuration.DecalScore(category, name);
            return score;
        }

        /// <summary>Palette tag for an asset seen in a vanilla park's sub-objects.</summary>
        public static string PaletteTag(string parkName) => "palette:" + (parkName ?? "");
    }
}
