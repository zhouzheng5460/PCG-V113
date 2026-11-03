// Asset choice helpers shared by all park/plaza/add-on layers.
//
// Layers never hard-code prefab names as the only option (ARCHITECTURE rule 5). A placement
// carries an AssetRef plus a Variation hash:
//   * the player picked one or several assets → Id = one of them (deterministic per variation),
//     the others become fallbacks;
//   * nothing picked (automatic) → Id = "" and the game catalog chooses an item of the
//     category, using PlannedObject.Variation to pick among them so automatic mixes still vary
//     and stay stable across re-plans (see docs/proposals/parkplaza.md).

using System;
using System.Collections.Generic;
using System.Threading;
using Unity.Mathematics;

namespace Placemaker.Core.Layers.Common
{
    public static class AssetMix
    {
        /// <summary>One asset out of the player's list (or automatic when the list is empty).</summary>
        public static AssetRef Pick(string[] ids, uint variation, params string[] builtInFallbacks)
        {
            if (ids == null || ids.Length == 0) return new AssetRef("", builtInFallbacks ?? Array.Empty<string>());
            int n = 0;
            foreach (var id in ids) if (!string.IsNullOrEmpty(id)) n++;
            if (n == 0) return new AssetRef("", builtInFallbacks ?? Array.Empty<string>());
            int pick = (int)(variation % (uint)n);
            string chosen = null;
            var fallbacks = new List<string>(n - 1 + (builtInFallbacks?.Length ?? 0));
            int k = 0;
            foreach (var id in ids)
            {
                if (string.IsNullOrEmpty(id)) continue;
                if (k++ == pick) chosen = id; else fallbacks.Add(id);
            }
            if (builtInFallbacks != null) fallbacks.AddRange(builtInFallbacks);
            return new AssetRef(chosen, fallbacks.ToArray());
        }

        /// <summary>Single-asset control: "" = automatic.</summary>
        public static AssetRef One(string id, params string[] builtInFallbacks) =>
            new AssetRef(id ?? "", builtInFallbacks ?? Array.Empty<string>());

        /// <summary>
        /// Approximate footprint radius (XZ) of an asset from the catalog snapshot, or the fallback
        /// when automatic / unknown.
        /// </summary>
        public static float FootprintRadius(SiteContext site, AssetRef asset, float fallback)
        {
            if (site?.Assets == null || string.IsNullOrEmpty(asset.Id)) return fallback;
            if (!site.Assets.TryGetValue(asset.Id, out var info)) return fallback;
            var ext = math.max(math.abs(info.BoundsMin.xz), math.abs(info.BoundsMax.xz));
            float r = math.length(ext);
            return r > 0.05f && math.isfinite(r) ? r : fallback;
        }

        /// <summary>Largest footprint among the player's choices (automatic → fallback).</summary>
        public static float MaxFootprint(SiteContext site, string[] ids, float fallback)
        {
            if (ids == null || ids.Length == 0) return fallback;
            float r = 0;
            foreach (var id in ids) r = math.max(r, FootprintRadius(site, new AssetRef(id), fallback));
            return r > 0 ? r : fallback;
        }
    }

    /// <summary>
    /// Which asset categories have at least one installed item. Core cannot see the catalog, so the
    /// game layer publishes an immutable snapshot whenever the catalog changes
    /// (<c>AssetAvailability.Publish(counts)</c>). Unknown → assumed available, so tests, the UI mock
    /// and a not-yet-ready catalog never suppress content.
    /// </summary>
    public static class AssetAvailability
    {
        private static Dictionary<string, int> _counts;

        /// <summary>Atomically replaces the snapshot (category → item count). Thread-safe for readers.</summary>
        public static void Publish(IDictionary<string, int> counts)
        {
            var copy = counts == null ? null : new Dictionary<string, int>(counts, StringComparer.Ordinal);
            Interlocked.Exchange(ref _counts, copy);
        }

        [ThreadStatic] private static Dictionary<string, int> _scoped;

        /// <summary>
        /// Overrides the snapshot for the current thread until disposed (tests, previews of "what if").
        /// </summary>
        public static IDisposable Scope(IDictionary<string, int> counts)
        {
            var previous = _scoped;
            _scoped = counts == null ? null : new Dictionary<string, int>(counts, StringComparer.Ordinal);
            return new Restore(previous);
        }

        private sealed class Restore : IDisposable
        {
            private readonly Dictionary<string, int> _previous;
            public Restore(Dictionary<string, int> previous) { _previous = previous; }
            public void Dispose() => _scoped = _previous;
        }

        /// <summary>(s21) Items of a category in the snapshot: -1 = no snapshot (tests, before the first scan), 0 = none.</summary>
        public static int CountOf(string category)
        {
            var snap = _scoped ?? Volatile.Read(ref _counts);
            if (snap == null || string.IsNullOrEmpty(category)) return -1;
            return snap.TryGetValue(category, out var n) ? n : 0;
        }

        public static bool Has(string category)
        {
            var snap = _scoped ?? Volatile.Read(ref _counts);
            if (snap == null || string.IsNullOrEmpty(category)) return true;
            return !snap.TryGetValue(category, out var n) || n > 0;
        }

        /// <summary>
        /// True when the layer can place something of this category: the player chose explicit assets,
        /// or the catalog has items of it. Otherwise adds the shared "no assets installed" info issue.
        /// </summary>
        public static bool Require(AreaPlan plan, string category, string[] chosen, string categoryLabelKey)
        {
            if (chosen != null)
                foreach (var id in chosen) if (!string.IsNullOrEmpty(id)) return true;
            if (Has(category)) return true;
            plan.Issues.Add(new PlanIssue
            {
                Severity = IssueSeverity.Info,
                Key = Keys.NoAssetsIssue,
                Args = new object[] { categoryLabelKey },
            });
            return false;
        }
    }
}
