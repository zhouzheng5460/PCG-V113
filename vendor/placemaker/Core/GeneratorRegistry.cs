using System;
using System.Collections.Generic;
using System.Linq;

namespace Placemaker.Core
{
    /// <summary>Marks an IAreaGenerator for discovery. Order = position in the "Make" row.</summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    public sealed class AreaGeneratorAttribute : Attribute
    {
        public int Order { get; }
        /// <summary>Not offered in the Make row (still usable by recipes and features), e.g. split, pond.</summary>
        public bool Hidden { get; set; }
        /// <summary>Make-row group: "parks", "gardens", "squares", "sports", "water", "industrial", "farms", "events", "transport", "parking", "commercial", "roads", "nature".</summary>
        public string Group { get; set; } = "";
        /// <summary>
        /// Loose build (user: "a grid of roads is just roads"): what this layer builds becomes ordinary
        /// city content — no owner lot, no group record, no member tags; bulldozed/upgraded per piece.
        /// </summary>
        public bool Loose { get; set; }
        public AreaGeneratorAttribute(int order) { Order = order; }
    }

    /// <summary>
    /// Finds every generator in this assembly by attribute, so each generator lives in its own
    /// folder without a central list to edit.
    /// </summary>
    public static class GeneratorRegistry
    {
        private static IReadOnlyList<IAreaGenerator> _all;

        public static IReadOnlyList<IAreaGenerator> All
        {
            get
            {
                if (_all != null) return _all;
                _all = typeof(GeneratorRegistry).Assembly.GetTypes()
                    .Where(t => !t.IsAbstract && typeof(IAreaGenerator).IsAssignableFrom(t))
                    .Select(t => (t, attr: (AreaGeneratorAttribute)Attribute.GetCustomAttribute(t, typeof(AreaGeneratorAttribute))))
                    .Where(x => x.attr != null)
                    .OrderBy(x => x.attr.Order)
                    .Select(x => (IAreaGenerator)Activator.CreateInstance(x.t))
                    .ToList();
                return _all;
            }
        }

        /// <summary>True when the layer must not get its own card in the Make row.</summary>
        public static bool IsHidden(IAreaGenerator g)
        {
            if (g == null) return true;
            var attr = (AreaGeneratorAttribute)Attribute.GetCustomAttribute(g.GetType(), typeof(AreaGeneratorAttribute));
            return attr != null && attr.Hidden;
        }

        private static Dictionary<string, bool> _loose;

        /// <summary>True when the generator with this id is marked [AreaGenerator(Loose = true)]. Unknown ids are grouped.</summary>
        public static bool IsLoose(string id)
        {
            if (string.IsNullOrEmpty(id)) return false;
            var map = _loose;
            if (map == null)
            {
                map = new Dictionary<string, bool>(StringComparer.Ordinal);
                foreach (var g in All)
                {
                    var attr = (AreaGeneratorAttribute)Attribute.GetCustomAttribute(g.GetType(), typeof(AreaGeneratorAttribute));
                    map[g.Id] = attr != null && attr.Loose;
                }
                _loose = map;
            }
            return map.TryGetValue(id, out bool loose) && loose;
        }

        public static IAreaGenerator Get(string id)
        {
            foreach (var g in All) if (g.Id == id) return g;
            return null;
        }
    }
}

namespace Placemaker.Core
{
    public static class GeneratorGroups
    {
        /// <summary>Make-row group from [AreaGenerator(Group=…)]; "" when unset (UI falls back to id words).</summary>
        public static string Of(IAreaGenerator g)
        {
            if (g == null) return "";
            var attr = (AreaGeneratorAttribute)System.Attribute.GetCustomAttribute(g.GetType(), typeof(AreaGeneratorAttribute));
            return attr?.Group ?? "";
        }
    }
}
