// Placemaker — the central shelf: which Make-row categories exist, in which order, which types and styles they show (GPL-3.0).
//
// The user (2026-10-15): "fewer, better". Cutting a type or a style means HIDING it, never deleting code, and one place says
// what is hidden so it can come back. The shelf is data: one file per category, Recipes/shelf/<category>.json, so the agents
// working on different categories never edit the same file:
//
//   {
//     "id": "parks",                    the category id (the file name)
//     "name": "Parks",                  its label (English; key Placemaker.UI.MakeGroup.<Id> unless "key" is given)
//     "order": 3,                       position in the Make row
//     "hidden": false,                  true = the whole category is hidden (industry, transport)
//     "groups": ["parks", "gardens"],   the types' own groups ([AreaGenerator(Group = ...)]) shown here
//     "types": [],                      types moved in from another group
//     "hiddenTypes": [],                types of this category hidden from the picker and its search
//     "hiddenStyles": { "park": [] }    styles (preset ids) hidden per type
//   }
//
// Read by the game (the catalog the panel gets: each type's category and shelved flag, each style's shelved flag), by the
// panel (picker and search), by the mock host, and by the game runner (suite catalog.json and Show.ps1), which flags hidden
// things but can still build them by id. Hidden types and styles keep all their code; editing the file brings them back.

using System;
using System.Collections.Generic;

namespace Placemaker.Core.Tool
{
    public sealed class ShelfCategory
    {
        public string Id = "";
        public string Name = "";
        public string Key = "";
        public int Order;
        public bool Hidden;
        public readonly List<string> Groups = new List<string>();
        public readonly List<string> Types = new List<string>();
        public readonly HashSet<string> HiddenTypes = new HashSet<string>(StringComparer.Ordinal);
        public readonly Dictionary<string, HashSet<string>> HiddenStyles = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        /// <summary>The file it came from (for messages).</summary>
        public string Source = "";
    }

    public sealed class Shelf
    {
        /// <summary>Category of a type no shelf file claims (its group is unknown): shown last.</summary>
        public const string Other = "other";
        public const string FolderName = "shelf";

        private readonly List<ShelfCategory> _categories = new List<ShelfCategory>();
        private readonly Dictionary<string, ShelfCategory> _byGroup = new Dictionary<string, ShelfCategory>(StringComparer.Ordinal);
        private readonly Dictionary<string, ShelfCategory> _byType = new Dictionary<string, ShelfCategory>(StringComparer.Ordinal);
        private readonly Dictionary<string, ShelfCategory> _byId = new Dictionary<string, ShelfCategory>(StringComparer.Ordinal);

        /// <summary>Messages about the files (duplicates, unreadable files); empty when all is well.</summary>
        public readonly List<string> Warnings = new List<string>();

        /// <summary>The categories in Make-row order (hidden ones included, flagged).</summary>
        public IReadOnlyList<ShelfCategory> Categories => _categories;

        /// <summary>No shelf files: every group is its own category and nothing is hidden.</summary>
        public bool Empty => _categories.Count == 0;

        /// <summary>The shelf the game and the panel use (set by the game layer at load; empty until then).</summary>
        public static Shelf Current = new Shelf();

        /// <summary>Parses the shelf files (name, text). Unreadable files are skipped with a warning; the first claim of a group or type wins.</summary>
        public static Shelf Parse(IEnumerable<KeyValuePair<string, string>> files)
        {
            var shelf = new Shelf();
            var all = new List<ShelfCategory>();
            if (files != null)
                foreach (var kv in files)
                {
                    try
                    {
                        var c = Read(JsonValue.Parse(kv.Value ?? ""), kv.Key ?? "");
                        if (c != null) all.Add(c);
                    }
                    catch (Exception e) { shelf.Warnings.Add($"{kv.Key}: {e.Message}"); }
                }
            all.Sort((a, b) => a.Order != b.Order ? a.Order.CompareTo(b.Order) : string.CompareOrdinal(a.Id, b.Id));
            foreach (var c in all)
            {
                if (shelf._byId.ContainsKey(c.Id)) { shelf.Warnings.Add($"{c.Source}: category '{c.Id}' is already defined by {shelf._byId[c.Id].Source}; skipped"); continue; }
                shelf._byId[c.Id] = c;
                shelf._categories.Add(c);
                foreach (var g in c.Groups)
                {
                    if (shelf._byGroup.TryGetValue(g, out var other)) shelf.Warnings.Add($"{c.Source}: group '{g}' already belongs to '{other.Id}'");
                    else shelf._byGroup[g] = c;
                }
                foreach (var t in c.Types)
                {
                    if (shelf._byType.TryGetValue(t, out var other)) shelf.Warnings.Add($"{c.Source}: type '{t}' already moved to '{other.Id}'");
                    else shelf._byType[t] = c;
                }
            }
            return shelf;
        }

        private static ShelfCategory Read(JsonValue o, string source)
        {
            if (o == null || !o.IsObject) throw new FormatException("not a JSON object");
            var c = new ShelfCategory { Source = source };
            c.Id = o["id"].AsString("").Trim();
            if (c.Id.Length == 0)
            {
                // The file name is the id when the file does not say.
                var name = source.Replace('\\', '/');
                int slash = name.LastIndexOf('/');
                if (slash >= 0) name = name.Substring(slash + 1);
                if (name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 5);
                c.Id = name;
            }
            if (c.Id.Length == 0) throw new FormatException("no category id");
            c.Name = o["name"].AsString(c.Id);
            c.Key = o["key"].AsString("");
            if (c.Key.Length == 0) c.Key = "Placemaker.UI.MakeGroup." + char.ToUpperInvariant(c.Id[0]) + c.Id.Substring(1);
            c.Order = (int)o["order"].AsNumber(1000);
            c.Hidden = o["hidden"].AsBool(false);
            foreach (var g in o["groups"].Items) { var s = g.AsString("").Trim(); if (s.Length > 0 && !c.Groups.Contains(s)) c.Groups.Add(s); }
            foreach (var t in o["types"].Items) { var s = t.AsString("").Trim(); if (s.Length > 0 && !c.Types.Contains(s)) c.Types.Add(s); }
            foreach (var t in o["hiddenTypes"].Items) { var s = t.AsString("").Trim(); if (s.Length > 0) c.HiddenTypes.Add(s); }
            var hs = o["hiddenStyles"];
            if (hs.IsObject)
                foreach (var kv in hs.Properties)
                {
                    var set = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var s in kv.Value.Items) { var id = s.AsString("").Trim(); if (id.Length > 0) set.Add(id); }
                    if (set.Count > 0) c.HiddenStyles[kv.Key] = set;
                }
            return c;
        }

        /// <summary>The category of a type: a category that moved it in, else the one showing its group, else <see cref="Other"/>
        /// (with no shelf files at all: the group itself).</summary>
        public string CategoryOf(string typeId, string group)
        {
            var c = Find(typeId, group);
            if (c != null) return c.Id;
            if (Empty && !string.IsNullOrEmpty(group)) return group;
            return Other;
        }

        public ShelfCategory Category(string id) => id != null && _byId.TryGetValue(id, out var c) ? c : null;

        private ShelfCategory Find(string typeId, string group)
        {
            if (!string.IsNullOrEmpty(typeId) && _byType.TryGetValue(typeId, out var t)) return t;
            if (!string.IsNullOrEmpty(group) && _byGroup.TryGetValue(group, out var g)) return g;
            return null;
        }

        /// <summary>Hidden from the picker and its search: its category is hidden, or the category hides the type.</summary>
        public bool IsTypeShelved(string typeId, string group)
        {
            var c = Find(typeId, group);
            return c != null && (c.Hidden || (typeId != null && c.HiddenTypes.Contains(typeId)));
        }

        /// <summary>A style (preset id) hidden from the picker and its search. Player styles are never shelved.</summary>
        public bool IsStyleShelved(string typeId, string group, string styleId)
        {
            if (string.IsNullOrEmpty(typeId) || string.IsNullOrEmpty(styleId)) return false;
            var c = Find(typeId, group);
            return c != null && c.HiddenStyles.TryGetValue(typeId, out var set) && set.Contains(styleId);
        }

        /// <summary>contract.ts Catalog.categories: the Make row's categories in order (hidden ones flagged).</summary>
        public JsonValue CategoriesJson()
        {
            var a = JsonValue.Arr();
            foreach (var c in _categories)
            {
                var o = JsonValue.Obj().Set("id", c.Id).Set("name", c.Key).Set("en", c.Name).Set("order", c.Order);
                if (c.Hidden) o.Set("hidden", true);
                a.Add(o);
            }
            return a;
        }
    }
}
