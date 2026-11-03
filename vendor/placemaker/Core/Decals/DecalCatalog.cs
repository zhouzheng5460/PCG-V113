// Placemaker — what a decal IS (s21 decal overhaul, docs/DECALS.md). GPL-3.0.
//
// CS2 1.6.2 ships 211 decal objects and every one of them is functional: parking stalls and icons,
// road arrows, letters/numbers, sports courts, playground pads, Chirper logos. Every decorative decal
// a player has (leaves, cracks, stains, puddles, litter, sand, seaweed…) comes from asset packs, named
// "<pack prefix> <item> Decal". This classifier strips the pack prefix and reads the item words; it was
// checked against the real catalog (catalog-dump.json, 973 decals) and the packs' own icons/textures
// (CustomDecals/*/icon.png, decal.json) — see docs/DECALS.md for the census and the confidence notes.
//
// It answers, per decal: family (taxonomy), shape (blob / strip / directional / tile / sheet / tiny,
// from the real footprint when known), the ground it belongs on, its natural orientation, and whether
// it is functional (a marking — never decorative dressing, never removed by the Decals amount).

using System;
using System.Collections.Generic;
using Placemaker.Core.Build;

namespace Placemaker.Core.Decals
{
    public enum DecalFamily
    {
        Unknown = 0,
        // Vegetation litter
        LeafLitter, LeafEdge, PalmLitter, Mulch, Moss, Overgrowth,
        // Ground wear
        Dirt, Dust, Mud, Sand, WetSand, Rocks, Rubble,
        // Stains and water
        Stain, Oil, Leak, Puddle, Wet,
        // Damage
        Crack, Patch,
        // Traces
        Tyre, Footprint,
        // Litter
        Litter, Glass, BeachWrack,
        // Art
        Graffiti,
        // Designed ground (decorative but laid out, not scattered)
        Paving, LawnPattern, GroundSheet,
        // Functional (never dressing)
        Marking, SportsMarking, ParkingStall, FarmTrack, Logo, BuildingOnly,
    }

    public enum DecalShape { Blob = 0, Strip = 1, Directional = 2, Tile = 3, Sheet = 4, Tiny = 5 }

    [Flags]
    public enum DecalGround { None = 0, Paved = 1, Grass = 2, Dirt = 4, Sand = 8, Any = 15 }

    public enum DecalOrientation
    {
        /// <summary>Any yaw (round blobs, scattered items).</summary>
        Random = 0,
        /// <summary>Long axis along the host: path tangent, bay axis, shoreline, wheel line.</summary>
        AlongHost = 1,
        /// <summary>Laid on a lattice in 90° steps (tiles, mowing patterns).</summary>
        Lattice = 2,
        /// <summary>Placed exactly by its layer (markings, stalls, field lines).</summary>
        Fixed = 3,
    }

    /// <summary>
    /// (s23) How a decal's texture ends. Soft = feathered blob that blends anywhere; Item = discrete things on a
    /// transparent ground (leaves, bottles, shells); Shaped = a specific crack, track, band or drip that only reads in
    /// the right spot and direction; Hard = a rectangular texture tile with visible edges (never scattered).
    /// </summary>
    public enum DecalEdge { Soft = 0, Item = 1, Shaped = 2, Hard = 3 }

    public struct DecalSpec
    {
        public DecalFamily Family;
        public DecalEdge Edge;
        public DecalShape Shape;
        public DecalGround Ground;
        public DecalOrientation Orientation;
        public bool Functional;
        /// <summary>2 = item word explicit and checked against the pack's icon, 1 = item word only, 0 = guessed.</summary>
        public int Confidence;

        public override string ToString() => Family + "/" + Shape + (Functional ? " (functional)" : "");
    }

    public static class DecalCatalog
    {
        /// <summary>Pack prefixes whose words would otherwise decide the theme ("Stains and Puddles … Oil 1").</summary>
        private static readonly string[] PackMarkers = { " pack ", " decals ", " decal pack " };

        /// <summary>Packs whose icons/textures were looked at (docs/DECALS.md "Verified by eye").</summary>
        private static readonly string[] VerifiedPacks =
        {
            "g87 stains and puddles", "g87 trash decals", "fallen leaves decals", "ground texture decal pack", "sand surfaces and beach",
            "golden sand beaches", "cracks and damage", "stains and leakage", "asphalt patches", "g87 dirty roads", "manicured grass",
            "vanilla terrain texture", "g87 vanilla curb", "g87 road markings", "realvision decals",
        };

        private static readonly string[] CompoundStems = { "mulch", "moss", "weeds", "rocks", "leaves", "grass", "patch", "dirt", "terrain", "path" };

        /// <summary>The item part of a decal prefab name, lower-case: after the last "G87", else after the pack prefix.</summary>
        public static string ItemPart(string name) => ItemOf(name).ToLowerInvariant();

        /// <summary>The item part of a decal prefab name in its original case (word splitting needs the camel case).</summary>
        public static string ItemOf(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            var lower = name.ToLowerInvariant();
            int g87 = lower.LastIndexOf("g87 ", StringComparison.Ordinal);
            if (g87 >= 0 && g87 + 4 < lower.Length) return name.Substring(g87 + 4);
            int cut = -1;
            foreach (var m in PackMarkers)
            {
                int i = lower.IndexOf(m, StringComparison.Ordinal);
                if (i > 0 && i + m.Length < lower.Length && (cut < 0 || i + m.Length > cut) && i < lower.Length * 0.7f) cut = i + m.Length;
            }
            return cut > 0 ? name.Substring(cut) : name;
        }

        /// <summary>Describes a decal from its prefab name and (optional) footprint size in metres.</summary>
        public static DecalSpec Describe(string name, float sizeX = 0f, float sizeZ = 0f)
        {
            var lower = (name ?? "").ToLowerInvariant();
            var itemCased = ItemOf(name);
            var item = itemCased.ToLowerInvariant();
            var w = new HashSet<string>(AssetHeuristics.Words(itemCased), StringComparer.Ordinal);
            var all = new HashSet<string>(AssetHeuristics.Words(name ?? ""), StringComparer.Ordinal);
            // Classification plug-ins see lower-case names, where camel-case compounds ("LargeMulch01",
            // "ScatteredRocks01") no longer split: distinctive stems count as words inside a compound too.
            foreach (var stem in CompoundStems)
                if (item.IndexOf(stem, StringComparison.Ordinal) >= 0) w.Add(stem);
            var s = new DecalSpec { Family = FamilyOf(lower, item, w, all), Confidence = 1 };
            if (s.Family == DecalFamily.Unknown) s.Confidence = 0;
            else foreach (var p in VerifiedPacks) if (lower.StartsWith(p, StringComparison.Ordinal)) { s.Confidence = 2; break; }
            s.Functional = IsFunctional(s.Family);
            s.Ground = GroundOf(s.Family);
            s.Shape = ShapeOf(s.Family, sizeX, sizeZ);
            s.Orientation = OrientationOf(s.Family, s.Shape);
            s.Edge = EdgeOf(lower, item, w, s.Family, s.Shape);
            return s;
        }

        public static bool IsFunctional(DecalFamily f) =>
            f == DecalFamily.Marking || f == DecalFamily.SportsMarking || f == DecalFamily.ParkingStall || f == DecalFamily.FarmTrack
            || f == DecalFamily.Logo || f == DecalFamily.BuildingOnly;

        /// <summary>
        /// Categories a layer lays out exactly (markings, stalls, court and field lines, farm ruts, paving patterns):
        /// the Decals amount never thins or removes them.
        /// </summary>
        public static bool IsFunctionalCategory(string category)
        {
            if (string.IsNullOrEmpty(category)) return false;
            if (category == AssetCategories.ParkingDecal || category == AssetCategories.DecalMarkings || category == AssetCategories.DecalPaving
                || category == AssetCategories.DecalFarm) return true;
            if (category.StartsWith("decal.court", StringComparison.Ordinal) || category.StartsWith("decal.field", StringComparison.Ordinal)
                || category.StartsWith("decal.line", StringComparison.Ordinal) || category.StartsWith("decal.marking", StringComparison.Ordinal)) return true;
            return false;
        }

        private static bool Any(HashSet<string> w, params string[] words)
        {
            foreach (var x in words) if (w.Contains(x)) return true;
            return false;
        }

        private static DecalFamily FamilyOf(string lower, string item, HashSet<string> w, HashSet<string> all)
        {
            // ── never ground dressing ──
            if (Any(all, "chirper", "logo")) return DecalFamily.Logo;
            if (Any(w, "wall", "roof", "window", "castle", "building", "curb", "cornerstone", "separator", "drain")
                || item.Contains("side building") || item.Contains("wall plant")) return DecalFamily.BuildingOnly;
            if (Any(all, "haristess", "tractortracks", "tractor", "grasstracks", "fieldtracks")) return DecalFamily.FarmTrack;
            if (Any(w, "court", "tennis", "basketball", "football", "multipurpose", "playground", "sport")) return DecalFamily.SportsMarking;
            if (all.Contains("parking") && Any(all, "lot", "icon", "text", "spot", "slot", "blue", "yellow", "disabled", "electric")) return DecalFamily.ParkingStall;
            if (Any(w, "alphabet", "number", "letter", "text", "arrow", "lane", "speed", "chevrons", "chevron", "zigzag", "crosswalk", "zebra",
                    "striping", "marking", "markings", "roadmarkings", "grid", "icon", "stop", "slow", "taxi", "bus", "roundabout", "rzzirrah", "terminal",
                    "livraison", "payant", "keep", "give", "only", "wrong", "dots", "circle", "zig", "kc") && !Any(w, "grass", "manicured")) return DecalFamily.Marking;
            if (all.Contains("roadmarkings")) return DecalFamily.Marking;

            // ── art and litter ──
            if (Any(w, "graffiti", "graff", "mural", "murals")) return DecalFamily.Graffiti;
            if (Any(w, "glass", "bottle") || (w.Contains("mirror") && w.Contains("broken"))) return DecalFamily.Glass;   // "Puddle Mirror" is a puddle
            if (Any(w, "seaweed", "seafoam", "seashells", "shells", "driftwood") || (w.Contains("sea") && w.Contains("trash"))) return DecalFamily.BeachWrack;
            if (all.Contains("beach") && w.Contains("debris")) return DecalFamily.BeachWrack;
            if (Any(w, "litter", "papers", "paper", "cardboard", "trash", "garbage", "rubbish")) return DecalFamily.Litter;
            if (w.Contains("footprint")) return DecalFamily.Footprint;

            // ── traces and water ──
            if ((Any(w, "tire", "tyre") && w.Contains("track")) || (w.Contains("track") && Any(w, "wet", "stain", "short", "long"))) return DecalFamily.Tyre;
            if (w.Contains("oil")) return DecalFamily.Oil;
            if (Any(w, "leakage", "leak")) return DecalFamily.Leak;
            if (Any(w, "pothole", "potholes", "puddle", "puddles"))
            {
                if (w.Contains("sand")) return DecalFamily.WetSand;
                return DecalFamily.Puddle;
            }
            if (w.Contains("wet") && w.Contains("sand")) return DecalFamily.WetSand;
            if (Any(w, "splash", "wet", "wave")) return DecalFamily.Wet;

            // ── damage ──
            if (w.Contains("patch") && Any(w, "asphalt", "tar", "road", "stone", "cobble")) return DecalFamily.Patch;
            if (w.Contains("va") && Any(w, "small", "medium", "large", "xlarge", "xxlarge")) return DecalFamily.Patch;   // G87 vanilla-asphalt fill squares
            if (Any(w, "crack", "cracks", "cracked", "damage", "damaged")) return DecalFamily.Crack;

            // ── vegetation ──
            if (Any(w, "leaf", "leaves", "foliage", "needles", "petals", "twigs"))
            {
                if (w.Contains("palm")) return DecalFamily.PalmLitter;
                if (w.Contains("lined")) return DecalFamily.LeafEdge;
                return DecalFamily.LeafLitter;
            }
            if (w.Contains("mulch")) return DecalFamily.Mulch;
            if (Any(w, "moss", "weeds", "vegetation")) return DecalFamily.Moss;
            if (Any(w, "manicured") || (w.Contains("grass") && Any(w, "vanilla", "terrain", "up")) || (w.Contains("grass") && Any(w, "round", "square", "triangle", "circle")))
                return DecalFamily.LawnPattern;
            if (Any(w, "aerial", "crop", "field", "fields", "park", "curve", "terrain") && !Any(w, "dirt", "sand", "stones")) return DecalFamily.GroundSheet;
            if (w.Contains("grasspatch") || (w.Contains("grass") && Any(w, "patch", "sand", "corner", "side", "path")) || item.Contains("grass decal") || item.Contains("wild garden"))
                return DecalFamily.Overgrowth;

            // ── ground ──
            if (Any(w, "mud", "muddy", "transition") || (Any(w, "dirt", "dirty") && w.Contains("road") && !w.Contains("side"))) return DecalFamily.Mud;
            if (w.Contains("dust")) return DecalFamily.Dust;
            if (w.Contains("debris")) return DecalFamily.Rubble;
            if (Any(w, "stones", "stone", "gravel", "rocks", "pebbles") && !Any(w, "pavement", "cobble")) return DecalFamily.Rocks;
            if (Any(w, "sand", "sandy")) return w.Contains("cobble") ? DecalFamily.Paving : DecalFamily.Sand;
            if (Any(w, "dirt", "dirtpatch", "dirtpath", "dirtterrain", "soil", "earth")) return DecalFamily.Dirt;
            if (Any(w, "stain", "stains", "grime", "rust")) return DecalFamily.Stain;
            if (Any(w, "tiles", "tile", "cobble", "cobbles", "cobblestone", "pavement", "pavers", "sidewalk", "brick", "bricks", "slab", "paving", "flagstone"))
                return DecalFamily.Paving;
            if (Any(w, "road", "asphalt", "rural", "garden", "concreted", "concrete", "side", "texture")) return DecalFamily.GroundSheet;
            return DecalFamily.Unknown;
        }

        /// <summary>
        /// (s23) Edge class from the pack and item words, checked against the packs' textures (docs/DECALS.md): RealVision
        /// ground textures, G87 road-transition strips, asphalt/sidewalk/grass tiles and the G87 "Dirty Pack Leaf" squares
        /// (leaves printed on a grass square) are hard rectangles; G87 stains A–H, dust, wet effects, ground dirt, mulch,
        /// moss, fallen-leaf scatters and golden sand are feathered; cracks, damage, repairs, tracks, bands, drips, potholes
        /// and lined drifts are shaped.
        /// </summary>
        private static DecalEdge EdgeOf(string lower, string item, HashSet<string> w, DecalFamily f, DecalShape shape)
        {
            if (IsFunctional(f) || f == DecalFamily.Paving || f == DecalFamily.LawnPattern || f == DecalFamily.GroundSheet || shape == DecalShape.Sheet) return DecalEdge.Hard;
            if (lower.StartsWith("realvision", StringComparison.Ordinal)) return f == DecalFamily.Graffiti ? DecalEdge.Shaped : DecalEdge.Hard;
            if (w.Contains("transition") || lower.Contains("g87 vanilla asphalt") || lower.Contains("sidewalk") || lower.Contains("light stains - decals")) return DecalEdge.Hard;
            if (lower.Contains("dirty pack leaf")) return DecalEdge.Hard;
            switch (f)
            {
                case DecalFamily.Crack: case DecalFamily.Patch: case DecalFamily.Tyre: case DecalFamily.Leak: case DecalFamily.LeafEdge:
                case DecalFamily.Footprint: case DecalFamily.Graffiti:
                    return DecalEdge.Shaped;
                case DecalFamily.Puddle:
                    return DecalEdge.Shaped;
                case DecalFamily.Litter: case DecalFamily.Glass: case DecalFamily.PalmLitter: case DecalFamily.Rubble:
                    return DecalEdge.Item;
                case DecalFamily.BeachWrack:
                    return lower.Contains("seaweed 0") || lower.Contains("seaweed red") ? DecalEdge.Shaped : DecalEdge.Item;   // G87 seaweed are wrack LINES
                case DecalFamily.Stain:
                    return Any(w, "band", "track", "trio", "lone", "wide", "narrow") ? DecalEdge.Shaped : DecalEdge.Soft;
                case DecalFamily.Wet:
                    return w.Contains("splash") ? DecalEdge.Shaped : DecalEdge.Soft;
                case DecalFamily.WetSand:
                    return w.Contains("band") ? DecalEdge.Shaped : DecalEdge.Soft;
                case DecalFamily.Sand:
                    // Golden Sand base/medium/thick/thin are hard-edged saturated discs in game (user screenshot, s23).
                    if (lower.Contains("golden sand") && !w.Contains("path")) return DecalEdge.Hard;
                    return w.Contains("path") ? DecalEdge.Shaped : DecalEdge.Soft;
                case DecalFamily.Unknown: return DecalEdge.Hard;
                default: return DecalEdge.Soft;
            }
        }

        private static DecalGround GroundOf(DecalFamily f)
        {
            switch (f)
            {
                case DecalFamily.LeafLitter: case DecalFamily.Wet: case DecalFamily.GroundSheet: return DecalGround.Any;
                case DecalFamily.LeafEdge: return DecalGround.Paved | DecalGround.Grass;
                case DecalFamily.PalmLitter: return DecalGround.Sand | DecalGround.Paved;
                case DecalFamily.Mulch: return DecalGround.Grass | DecalGround.Dirt;
                case DecalFamily.Moss: return DecalGround.Paved | DecalGround.Dirt | DecalGround.Grass;
                case DecalFamily.Overgrowth: return DecalGround.Paved | DecalGround.Dirt;
                case DecalFamily.Dirt: return DecalGround.Grass | DecalGround.Dirt | DecalGround.Paved;
                case DecalFamily.Dust: case DecalFamily.Mud: return DecalGround.Dirt | DecalGround.Paved;
                case DecalFamily.Sand: return DecalGround.Sand | DecalGround.Paved;
                case DecalFamily.WetSand: case DecalFamily.BeachWrack: case DecalFamily.Footprint: return DecalGround.Sand;
                case DecalFamily.Rocks: case DecalFamily.Rubble: return DecalGround.Dirt | DecalGround.Grass | DecalGround.Paved;
                case DecalFamily.Puddle: return DecalGround.Paved | DecalGround.Dirt;
                case DecalFamily.Tyre: return DecalGround.Paved | DecalGround.Dirt | DecalGround.Sand;
                case DecalFamily.Litter: case DecalFamily.Glass: return DecalGround.Paved | DecalGround.Grass | DecalGround.Dirt;
                case DecalFamily.LawnPattern: return DecalGround.Grass;
                case DecalFamily.BuildingOnly: case DecalFamily.Unknown: return DecalGround.None;
                default: return DecalGround.Paved;
            }
        }

        private static DecalShape ShapeOf(DecalFamily f, float sx, float sz)
        {
            float lo = Math.Min(sx, sz), hi = Math.Max(sx, sz);
            if (f == DecalFamily.Paving || f == DecalFamily.LawnPattern) return hi >= 16f ? DecalShape.Sheet : DecalShape.Tile;
            if (f == DecalFamily.Tyre || f == DecalFamily.Footprint || f == DecalFamily.LeafEdge || f == DecalFamily.Leak) return DecalShape.Directional;
            if (hi <= 0f) return f == DecalFamily.GroundSheet ? DecalShape.Sheet : DecalShape.Blob;
            if (hi >= 16f) return DecalShape.Sheet;
            if (hi <= 1.2f) return DecalShape.Tiny;
            if (lo > 0f && hi / lo >= 1.8f) return DecalShape.Strip;
            return DecalShape.Blob;
        }

        private static DecalOrientation OrientationOf(DecalFamily f, DecalShape s)
        {
            if (IsFunctional(f)) return DecalOrientation.Fixed;
            if (s == DecalShape.Tile) return DecalOrientation.Lattice;
            if (s == DecalShape.Directional || s == DecalShape.Strip) return DecalOrientation.AlongHost;
            if (f == DecalFamily.Mud || f == DecalFamily.BeachWrack && s != DecalShape.Tiny) return DecalOrientation.AlongHost;
            return DecalOrientation.Random;
        }
    }
}
