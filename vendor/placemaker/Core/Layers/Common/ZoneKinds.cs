// s14: zone types the player picks by name ("Low commercial") instead of a raw zone asset.
//
// Vanilla zone prefab names (catalog dump 1.6.2f1, ModsData/Placemaker/catalog-dump.json, "vanilla": true):
//   themed:   "EU|NA Commercial Low/High", "EU|NA Residential Low/Medium/Medium Row/High/Mixed"
//   neutral:  "Residential LowRent", "Office Low", "Office High", "Industrial Manufacturing"
// The unprefixed "Commercial Low" / "Residential Low"… prefabs have no localised name (legacy, likely no
// buildings), so the themed ones are used. Plans name the EU prefab; the game layer swaps the prefix for
// the city's theme (ThemePrefab.assetPrefix) when that prefab exists (AssetCatalogSystem.Resolve).
// PlannedZoning with an empty ZoneId zones nothing (PlanWork skips it) — that is why a kind is always set.

using System;

namespace Placemaker.Core.Layers.Common
{
    public static class ZoneKinds
    {
        public const string CommercialLow = "commercialLow", CommercialHigh = "commercialHigh";
        public const string ResidentialLow = "residentialLow", ResidentialRow = "residentialRow", ResidentialMedium = "residentialMedium";
        public const string ResidentialHigh = "residentialHigh", Mixed = "mixed", LowRent = "lowRent";
        public const string OfficeLow = "officeLow", OfficeHigh = "officeHigh", Industrial = "industrial";

        /// <summary>Display order of the zone type choice.</summary>
        public static readonly string[] All =
        {
            CommercialLow, CommercialHigh, Mixed, ResidentialLow, ResidentialRow, ResidentialMedium, ResidentialHigh, LowRent,
            OfficeLow, OfficeHigh, Industrial,
        };

        public const string Default = CommercialLow;

        /// <summary>Zone prefab of a kind (EU name for themed zones); unknown kinds → low density commercial.</summary>
        public static string PrefabId(string kind)
        {
            switch (kind)
            {
                case CommercialHigh: return "EU Commercial High";
                case ResidentialLow: return "EU Residential Low";
                case ResidentialRow: return "EU Residential Medium Row";
                case ResidentialMedium: return "EU Residential Medium";
                case ResidentialHigh: return "EU Residential High";
                case Mixed: return "EU Residential Mixed";
                case LowRent: return "Residential LowRent";
                case OfficeLow: return "Office Low";
                case OfficeHigh: return "Office High";
                case Industrial: return "Industrial Manufacturing";
                default: return "EU Commercial Low";
            }
        }

        /// <summary>The zone id to plan: an explicit asset wins, else the kind's prefab.</summary>
        public static string Resolve(string kind, string explicitAsset) =>
            !string.IsNullOrEmpty(explicitAsset) ? explicitAsset : PrefabId(kind);

        /// <summary>
        /// Same zone for a city theme: "EU Commercial Low" with prefix "NA" → "NA Commercial Low". Ids without a
        /// theme prefix, or an empty prefix, come back unchanged.
        /// </summary>
        public static string ForTheme(string id, string themePrefix)
        {
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(themePrefix)) return id;
            foreach (var p in new[] { "EU ", "NA " })
                if (id.StartsWith(p, StringComparison.Ordinal))
                    return themePrefix.Trim() + " " + id.Substring(p.Length);
            return id;
        }

        public static bool IsKind(string kind) => Array.IndexOf(All, kind) >= 0;
    }
}
