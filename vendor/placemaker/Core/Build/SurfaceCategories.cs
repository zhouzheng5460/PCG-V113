// Custom surface categories (g5 integration). Surfaces are classified before ICategoryRules run
// (AssetHeuristics.Classify returns early for area prefabs), so types could not add play rubber,
// bark mulch, clay courts or running-track surfaces. ISurfaceCategoryRules is the surface
// counterpart of ICategoryRules: discovered by reflection, additive, never stops the built-in kinds.
//
// A custom category may be empty in a playset (no "rubber" surface installed). CategoryFallbacks
// lets automatic resolution walk to a parent category instead of leaving the surface unresolved:
// surface.track → surface.rubber → surface.paving, surface.bark → surface.dirt → surface.natural…

using System;
using System.Collections.Generic;
using System.Linq;

namespace Placemaker.Core.Build
{
    public static class SurfaceCategories
    {
        public const string Rubber = "surface.rubber";   // safety / playground rubber, EPDM, tartan
        public const string Bark = "surface.bark";       // bark mulch, wood chips
        public const string Clay = "surface.clay";       // clay courts, red earth, cinder
        public const string Track = "surface.track";     // running / athletics track
        public const string Gravel = "surface.gravel";   // gravel, shingle, crushed stone
        public const string Rock = "surface.rock";       // rock, scree, boulder ground
    }

    /// <summary>Optional companion to ICategoryRules: extra categories for SURFACE prefabs.</summary>
    public interface ISurfaceCategoryRules
    {
        /// <summary>Add categories for a surface prefab (lower = lower-cased prefab name). Additive only.</summary>
        void ClassifySurface(string lower, List<string> categories);
    }

    public static class SurfaceCategoryRules
    {
        private static List<ISurfaceCategoryRules> _all;

        public static IReadOnlyList<ISurfaceCategoryRules> All
        {
            get
            {
                if (_all != null) return _all;
                var list = new List<ISurfaceCategoryRules>();
                foreach (var t in typeof(SurfaceCategoryRules).Assembly.GetTypes())
                {
                    if (t.IsAbstract || t.IsInterface || !typeof(ISurfaceCategoryRules).IsAssignableFrom(t)) continue;
                    try { list.Add((ISurfaceCategoryRules)Activator.CreateInstance(t)); } catch (Exception) { }
                }
                _all = list.OrderBy(r => r.GetType().FullName, StringComparer.Ordinal).ToList();
                return _all;
            }
        }

        public static void Classify(string lower, List<string> categories)
        {
            if (string.IsNullOrEmpty(lower) || categories == null) return;
            foreach (var rule in All)
            {
                try { rule.ClassifySurface(lower, categories); }
                catch (Exception) { /* a faulty rule must never break the catalog */ }
            }
        }
    }

    /// <summary>The shared surface kinds every type may use (rubber, bark, clay, track).</summary>
    public sealed class SharedSurfaceRules : ISurfaceCategoryRules
    {
        public void ClassifySurface(string lower, List<string> r)
        {
            if (AssetHeuristics.ContainsAny(lower, "rubber", "epdm", "safety surf", "softfall")
                || (AssetHeuristics.ContainsAny(lower, "playground", "play surface") && !AssetHeuristics.ContainsAny(lower, "sand", "grass", "bark")))
                AddOnce(r, SurfaceCategories.Rubber);
            if (AssetHeuristics.ContainsAny(lower, "bark", "mulch", "woodchip", "wood chip", "wood_chip"))
                AddOnce(r, SurfaceCategories.Bark);
            if (AssetHeuristics.ContainsAny(lower, "clay", "cinder", "red earth", "terre battue"))
                AddOnce(r, SurfaceCategories.Clay);
            if (AssetHeuristics.ContainsAny(lower, "gravel", "shingle", "crushed", "pebble"))
                AddOnce(r, SurfaceCategories.Gravel);
            if (AssetHeuristics.ContainsAny(lower, "rock", "scree", "boulder", "cliff"))
                AddOnce(r, SurfaceCategories.Rock);
            if (AssetHeuristics.ContainsAny(lower, "running track", "runningtrack", "athletic", "tartan", "race track", "track surface"))
                AddOnce(r, SurfaceCategories.Track);
        }

        private static void AddOnce(List<string> r, string c) { if (!r.Contains(c)) r.Add(c); }
    }

    /// <summary>
    /// Parent categories for automatic resolution when a category has no installed asset. Types may
    /// register their own (e.g. "surface.green" → surface.grass); first registration wins.
    /// </summary>
    public static class CategoryFallbacks
    {
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, string> Parents = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { SurfaceCategories.Track, SurfaceCategories.Rubber },
            { SurfaceCategories.Rubber, AssetCategories.SurfacePaving },
            { SurfaceCategories.Bark, AssetCategories.SurfaceDirt },
            { SurfaceCategories.Clay, AssetCategories.SurfaceDirt },
            { AssetCategories.SurfaceDirt, AssetCategories.SurfaceNatural },
            { AssetCategories.SurfaceSand, AssetCategories.SurfaceNatural },
            { SurfaceCategories.Gravel, AssetCategories.SurfaceNatural },
            { SurfaceCategories.Rock, AssetCategories.SurfaceNatural },
            { AssetCategories.DecalLitter, AssetCategories.DecalDirt },
            // Farm ground (s8, GroundCover): specific crop looks walk to any farmland, then to dirt.
            { AssetCategories.SurfaceCropLavender, AssetCategories.SurfaceCropFlower },
            { AssetCategories.SurfaceCropYellow, AssetCategories.SurfaceCropFlower },
            { AssetCategories.SurfaceCropFlower, AssetCategories.SurfaceCropGreen },
            { AssetCategories.SurfaceCropRows, AssetCategories.SurfaceCropSoil },
            { AssetCategories.SurfaceCropPlowed, AssetCategories.SurfaceCropSoil },
            { AssetCategories.SurfaceCropSoil, AssetCategories.SurfaceCrop },
            { AssetCategories.SurfaceCropGreen, AssetCategories.SurfaceCrop },
            { AssetCategories.SurfaceCropStraw, AssetCategories.SurfaceCrop },
            { AssetCategories.SurfaceCrop, AssetCategories.SurfaceDirt },
            // Park path tiers (g3): custom bike / trail nets fall back to the footpath pool.
            { "net.bike", AssetCategories.Pathway },
            { "net.trail", AssetCategories.Pathway },
            // (rf-shops3) Plot planting (trees and shrubs on the shops types' own plots) is ordinary planting.
            { "tree.plot", AssetCategories.Tree },
            { "bush.plot", AssetCategories.Bush },
        };

        public static void Register(string category, string parent)
        {
            if (string.IsNullOrEmpty(category) || string.IsNullOrEmpty(parent) || category == parent) return;
            lock (Gate) if (!Parents.ContainsKey(category)) Parents[category] = parent;
        }

        public static string ParentOf(string category)
        {
            if (category == null) return null;
            lock (Gate) return Parents.TryGetValue(category, out var p) ? p : null;
        }

        /// <summary>The category followed by its parents (cycle-safe, at most 8 steps).</summary>
        public static IEnumerable<string> Chain(string category)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var c = category; c != null && seen.Add(c) && seen.Count <= 8; c = ParentOf(c)) yield return c;
        }
    }
}
