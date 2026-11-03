// Retired layer ids (g5 integration). Two type builders wrote the same feature twice (apiary vs
// honeyHives, farmStand vs farmStall). One implementation stays; the other id must keep working
// because layer ids and control ids are save format (recipes, presets, build receipts).
//
//  * LayerAliases.Normalize rewrites a loaded RecipeStep tree to the canonical ids, translating the
//    settings (and locked ids) to the canonical layer's controls — so the UI shows the canonical
//    feature toggle with the player's values.
//  * LayerAlias is a hidden generator registered under the retired id that plans with the canonical
//    layer, so anything that was not normalised (an old receipt replayed directly) still plans.

using System;
using System.Collections.Generic;
using System.Threading;

namespace Placemaker.Core.Layers.Common
{
    public static class LayerAliases
    {
        public sealed class Alias
        {
            public string From = "", To = "";
            /// <summary>(old control id, new control id).</summary>
            public (string from, string to)[] Renames = Array.Empty<(string, string)>();
            /// <summary>(new control id, old value, new value) for choices whose options were renamed.</summary>
            public (string control, string from, string to)[] Values = Array.Empty<(string, string, string)>();
            /// <summary>Extra translation after renames (may be null).</summary>
            public Action<SettingsBag, SettingsBag> Extra;
        }

        private static readonly Dictionary<string, Alias> Map = new Dictionary<string, Alias>(StringComparer.Ordinal);

        static LayerAliases()
        {
            Register(new Alias
            {
                From = "honeyHives", To = "apiary",
                Renames = new[] { ("count", "hives"), ("wildflowers", "flowers") },
                Values = new[] { ("layout", "circle", "ring") },
            });
            Register(new Alias
            {
                From = "farmStall", To = "farmStand",
                Renames = new[] { ("stalls", "count"), ("stallAsset", "standAsset") },
                // The stall's picnic tables are the stand's seating.
                Extra = (old, bag) => { if (old.Values.ContainsKey("tables")) bag.Values["seating"] = old.Num("tables", 0) > 0; bag.Values.Remove("tables"); },
            });
        }

        public static void Register(Alias a)
        {
            if (a == null || string.IsNullOrEmpty(a.From) || string.IsNullOrEmpty(a.To) || a.From == a.To) return;
            lock (Map) Map[a.From] = a;
        }

        public static bool IsAlias(string id) { lock (Map) return id != null && Map.ContainsKey(id); }

        /// <summary>The id that implements <paramref name="id"/> (itself when it is not retired).</summary>
        public static string Canonical(string id)
        {
            lock (Map) return id != null && Map.TryGetValue(id, out var a) ? a.To : id;
        }

        public static Alias Of(string id) { lock (Map) return id != null && Map.TryGetValue(id, out var a) ? a : null; }

        /// <summary>Settings of the retired layer in the canonical layer's vocabulary. Idempotent.</summary>
        public static SettingsBag Translate(string fromId, SettingsBag settings)
        {
            var a = Of(fromId);
            if (a == null || settings == null) return settings;
            var bag = settings.Clone();
            foreach (var (from, to) in a.Renames)
                if (bag.Values.TryGetValue(from, out var v)) { bag.Values.Remove(from); if (!bag.Values.ContainsKey(to)) bag.Values[to] = v; }
            foreach (var (control, from, to) in a.Values)
                if (bag.Values.TryGetValue(control, out var v) && v is string s && s == from) bag.Values[control] = to;
            a.Extra?.Invoke(settings, bag);
            return bag;
        }

        public static string TranslateControl(string fromId, string control)
        {
            var a = Of(fromId);
            if (a == null) return control;
            foreach (var (from, to) in a.Renames) if (from == control) return to;
            return control;
        }

        /// <summary>Rewrites retired layer ids in a step tree (children and region overrides). Returns the number of rewrites.</summary>
        public static int Normalize(RecipeStep step, int depth = 0)
        {
            if (step == null || depth > 16) return 0;
            int n = 0;
            var a = Of(step.Layer);
            if (a != null)
            {
                step.Settings = Translate(step.Layer, step.Settings) ?? new SettingsBag();
                var locked = new HashSet<string>(StringComparer.Ordinal);
                foreach (var id in step.Locked) locked.Add(TranslateControl(step.Layer, id));
                step.Locked = locked;
                step.Layer = a.To;
                n++;
            }
            // Retired control ids of a live layer (s10: the sports park's facility controls became feature children).
            n += Leisure.SportsParkLayer.Migrate(step);
            n += Roads.RoadsLayer.Migrate(step);   // s34: the roads type's old edge-zoning feature → "Pre-zone new roads"
            foreach (var c in step.Children) n += Normalize(c, depth + 1);
            foreach (var kv in step.RegionOverrides) n += Normalize(kv.Value, depth + 1);
            return n;
        }

        public static int Normalize(Recipe recipe) => recipe == null ? 0 : Normalize(recipe.Root);
    }

    /// <summary>
    /// A hidden generator under a retired id that delegates to the canonical layer with translated
    /// settings. Subclass with [AreaGenerator(order, Hidden = true)] and a parameterless constructor.
    /// </summary>
    public abstract class LayerAlias : IAreaGenerator
    {
        private readonly string _id;
        protected LayerAlias(string retiredId) { _id = retiredId; }

        private IAreaGenerator Target => GeneratorRegistry.Get(LayerAliases.Canonical(_id));

        public string Id => _id;
        public string NameKey => Target?.NameKey ?? "";
        public string Icon => Target?.Icon ?? "";
        public LayerKind Kind => Target?.Kind ?? LayerKind.AddOn;
        public string[] Accepts => Target?.Accepts ?? Array.Empty<string>();
        public string[] Emits => Target?.Emits ?? Array.Empty<string>();
        public int Version => Target?.Version ?? 1;
        public IReadOnlyList<ControlSchema> Controls => Target?.Controls ?? Array.Empty<ControlSchema>();
        public IReadOnlyList<PresetInfo> Presets => Target?.Presets ?? Array.Empty<PresetInfo>();
        public SettingsBag Defaults() => Target?.Defaults() ?? new SettingsBag();
        public float MinArea => Target?.MinArea ?? 20f;
        public float MaxArea => Target?.MaxArea ?? 4_000_000f;

        public Readiness CheckReady(AreaInput input) => Target?.CheckReady(Translated(input)) ?? new Readiness();
        public List<EdgeMarker> SuggestMarkers(AreaInput input) => Target?.SuggestMarkers(Translated(input)) ?? new List<EdgeMarker>();

        public SettingsBag Randomize(SettingsBag current, uint seed, ICollection<string> locked)
        {
            var t = Target;
            if (t == null) return current;
            var lk = new List<string>();
            if (locked != null) foreach (var id in locked) lk.Add(LayerAliases.TranslateControl(_id, id));
            return t.Randomize(LayerAliases.Translate(_id, current), seed, lk);
        }

        public AreaPlan Plan(AreaInput input, CancellationToken cancel)
        {
            var t = Target;
            if (t == null) return new AreaPlan { GeneratorId = _id };
            var plan = t.Plan(Translated(input), cancel) ?? new AreaPlan();
            return plan;
        }

        private AreaInput Translated(AreaInput input)
        {
            if (input == null) return null;
            return new AreaInput
            {
                Shape = input.Shape, Site = input.Site, Markers = input.Markers, Seed = input.Seed,
                Settings = LayerAliases.Translate(_id, input.Settings) ?? new SettingsBag(),
                RegionTags = input.RegionTags, Reserved = input.Reserved,
                ChildLayers = input.ChildLayers, ChildTargets = input.ChildTargets,
            };
        }
    }
}
