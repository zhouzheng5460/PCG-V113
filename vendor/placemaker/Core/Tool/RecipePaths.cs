// Step addressing for the UI contract ("0", "0/1", "0/1@block:3", "0/1@block:3/0").
//
//   "0"              the root step
//   "0/2"            child 2 of the root
//   "0/1@block:3"    the region override for region "block:3" held by step "0". Overrides live on
//                    the PARENT (Composer: parent.RegionOverrides[key] replaces whichever child would
//                    run in that region); the child index is informative only and ignored when
//                    resolving. This matches the composer's AreaPlan.StepPath ("childPath@regionKey").
//   ".../0"          children of an override step continue normally.

using System;
using System.Collections.Generic;
using System.Globalization;

namespace Placemaker.Core.Tool
{
    public static class RecipePaths
    {
        public const string Root = "0";

        public sealed class Located
        {
            public RecipeStep Step;
            public RecipeStep Parent;       // null for root
            public int Index = -1;          // index in Parent.Children (or informative index for overrides)
            public string OverrideKey;      // non-null when Step is Parent.RegionOverrides[OverrideKey]
            public string Path = "";
        }

        public static RecipeStep Resolve(Recipe recipe, string path) => Locate(recipe, path)?.Step;

        public static Located Locate(Recipe recipe, string path)
        {
            if (recipe?.Root == null || string.IsNullOrEmpty(path)) return null;
            var segs = path.Split('/');
            if (segs[0] != Root) return null;
            var loc = new Located { Step = recipe.Root, Path = Root };
            for (int i = 1; i < segs.Length; i++)
            {
                string seg = segs[i];
                int at = seg.IndexOf('@');
                string idxText = at >= 0 ? seg.Substring(0, at) : seg;
                if (!int.TryParse(idxText, NumberStyles.None, CultureInfo.InvariantCulture, out int idx)) return null;
                var parent = loc.Step;
                if (at >= 0)
                {
                    string key = seg.Substring(at + 1);
                    if (key.Length == 0 || !parent.RegionOverrides.TryGetValue(key, out var ov) || ov == null) return null;
                    loc = new Located { Step = ov, Parent = parent, Index = idx, OverrideKey = key, Path = loc.Path + "/" + seg };
                }
                else
                {
                    if (idx < 0 || idx >= parent.Children.Count || parent.Children[idx] == null) return null;
                    loc = new Located { Step = parent.Children[idx], Parent = parent, Index = idx, Path = loc.Path + "/" + seg };
                }
            }
            return loc;
        }

        public static string Child(string parentPath, int index) => parentPath + "/" + index.ToString(CultureInfo.InvariantCulture);

        /// <summary>Path of the override for <paramref name="regionKey"/> held by the step at <paramref name="parentPath"/>.</summary>
        public static string Override(string parentPath, RecipeStep parent, string regionKey)
            => parentPath + "/" + OverrideChildIndex(parent, regionKey).ToString(CultureInfo.InvariantCulture) + "@" + regionKey;

        /// <summary>Which child an override visually belongs to: the first child that targets regions (composer semantics).</summary>
        public static int OverrideChildIndex(RecipeStep parent, string regionKey)
        {
            if (parent == null) return 0;
            for (int i = 0; i < parent.Children.Count; i++)
            {
                var c = parent.Children[i];
                if (c != null && !string.IsNullOrEmpty(c.Target) && (c.Target == regionKey || RegionKeyHasTag(regionKey, c.Target))) return i;
            }
            for (int i = 0; i < parent.Children.Count; i++)
                if (parent.Children[i] != null && !string.IsNullOrEmpty(parent.Children[i].Target)) return i;
            return 0;
        }

        // Region keys are "<tag>:<n>" by convention ("block:3", "part:1"), so a child targeting the
        // tag "block" is the one an override of "block:3" replaces.
        private static bool RegionKeyHasTag(string regionKey, string tag)
        {
            int colon = regionKey.IndexOf(':');
            return colon > 0 && colon == tag.Length && regionKey.StartsWith(tag, StringComparison.Ordinal);
        }

        public static string ParentOf(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            int slash = path.LastIndexOf('/');
            return slash < 0 ? null : path.Substring(0, slash);
        }

        /// <summary>Depth-first walk (step, path), including region overrides and their subtrees.</summary>
        public static IEnumerable<KeyValuePair<string, RecipeStep>> Walk(Recipe recipe)
        {
            if (recipe?.Root == null) yield break;
            var stack = new WorkStack<KeyValuePair<string, RecipeStep>>();
            stack.Push(new KeyValuePair<string, RecipeStep>(Root, recipe.Root));
            while (stack.Count > 0)
            {
                var cur = stack.Pop();
                yield return cur;
                var step = cur.Value;
                var keys = new List<string>(step.RegionOverrides.Keys);
                keys.Sort(StringComparer.Ordinal);
                for (int k = keys.Count - 1; k >= 0; k--)
                {
                    var ov = step.RegionOverrides[keys[k]];
                    if (ov != null) stack.Push(new KeyValuePair<string, RecipeStep>(Override(cur.Key, step, keys[k]), ov));
                }
                for (int i = step.Children.Count - 1; i >= 0; i--)
                    if (step.Children[i] != null) stack.Push(new KeyValuePair<string, RecipeStep>(Child(cur.Key, i), step.Children[i]));
            }
        }

        /// <summary>
        /// Composer AreaPlan.StepPath ("0/1@block:3") → the UI step path that owns it. A plain child
        /// runs as "childPath@regionKey"; an override for that region has the same composer path,
        /// so it is preferred when one exists.
        /// </summary>
        public static string OwnerOfPlan(Recipe recipe, string planStepPath)
        {
            if (string.IsNullOrEmpty(planStepPath)) return null;
            int at = planStepPath.LastIndexOf('@');
            if (at < 0) return planStepPath;
            string stepPath = planStepPath.Substring(0, at);
            string region = planStepPath.Substring(at + 1);
            var parentPath = ParentOf(stepPath);
            if (parentPath != null && region.Length > 0)
            {
                var parent = Resolve(recipe, parentPath);
                if (parent != null && parent.RegionOverrides.ContainsKey(region))
                    return Override(parentPath, parent, region);
            }
            return stepPath;
        }
    }
}
