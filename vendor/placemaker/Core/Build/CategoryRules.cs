// Plug-in point for asset classification. Each type folder can ship its own rules
// (a class implementing ICategoryRules) without editing the shared classifiers; rules are
// discovered by reflection and run before the built-in leisure/general heuristics.

using System;
using System.Collections.Generic;
using System.Linq;

namespace Placemaker.Core.Build
{
    public interface ICategoryRules
    {
        /// <summary>Higher runs first. Built-in heuristics run after all rules.</summary>
        int Priority { get; }

        /// <summary>
        /// Add categories for a visible static object (lower = lower-cased prefab name).
        /// Return true to stop further classification, false to let others add more.
        /// </summary>
        bool Classify(string lower, AssetTraits traits, List<string> categories);

        /// <summary>Automatic-pool eligibility for a category this rule owns; null = no opinion.</summary>
        bool? AutoEligible(string category, string lower, AssetTraits traits);
    }

    public static class CategoryRules
    {
        private static List<ICategoryRules> _all;

        public static IReadOnlyList<ICategoryRules> All
        {
            get
            {
                if (_all != null) return _all;
                var list = new List<ICategoryRules>();
                foreach (var t in typeof(CategoryRules).Assembly.GetTypes())
                {
                    if (t.IsAbstract || t.IsInterface || !typeof(ICategoryRules).IsAssignableFrom(t)) continue;
                    try { list.Add((ICategoryRules)Activator.CreateInstance(t)); } catch (Exception) { /* skip broken rule */ }
                }
                _all = list.OrderByDescending(r => r.Priority).ToList();
                return _all;
            }
        }

        public static bool Classify(string lower, AssetTraits traits, List<string> categories)
        {
            // Every rule runs: type rules are additive among themselves (a hay bale is both an
            // animal-park and a lookout-hill prop). A rule returning true only stops the built-in
            // general heuristics afterwards.
            bool stop = false;
            foreach (var rule in All)
            {
                try { if (rule.Classify(lower, traits, categories)) stop = true; }
                catch (Exception) { /* a faulty rule must never break the catalog */ }
            }
            // Type rules are additive only: the general heuristics always run too, so a barrier stays a
            // fence for everyone even when a type also tags it as its own prop. (stop is kept for logging.)
            return false && stop;
        }

        public static bool? AutoEligible(string category, string lower, AssetTraits traits)
        {
            foreach (var rule in All)
            {
                try { var v = rule.AutoEligible(category, lower, traits); if (v.HasValue) return v; }
                catch (Exception) { }
            }
            return null;
        }
    }
}

namespace Placemaker.Core.Build
{
    /// <summary>
    /// Optional companion to ICategoryRules: categories whose automatic picks should VARY per object
    /// (piles, junk, crops, wrecks…) instead of one prefab per build. Discovered by reflection.
    /// </summary>
    public interface IVarietyCategories
    {
        System.Collections.Generic.IEnumerable<string> VarietyCategories { get; }
    }

    public static class VarietyRegistry
    {
        private static System.Collections.Generic.HashSet<string> _set;

        public static bool Contains(string category)
        {
            if (category == null) return false;
            if (_set == null)
            {
                var s = new System.Collections.Generic.HashSet<string>(System.StringComparer.Ordinal);
                foreach (var t in typeof(VarietyRegistry).Assembly.GetTypes())
                {
                    if (t.IsAbstract || t.IsInterface || !typeof(IVarietyCategories).IsAssignableFrom(t)) continue;
                    try { foreach (var c in ((IVarietyCategories)System.Activator.CreateInstance(t)).VarietyCategories) if (c != null) s.Add(c); }
                    catch (System.Exception) { }
                }
                _set = s;
            }
            return _set.Contains(category);
        }
    }
}
