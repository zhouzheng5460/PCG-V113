// Placemaker roads — the road ROLES of a layout (street, avenue, local, roundabout ring, perimeter,
// car-free street), what Automatic places for each, their preview colours, and the "road mixes"
// (named combinations of which road goes in each role). s16 / user feedback: "What am I actually
// getting? What are these roads being drawn on my screen?"
//
// Vanilla prefab names are from the 1.6.2f1 catalog dump (docs/ASSET-CURATION.md, catalog-dump.json):
//   "Small Road"  = "Two-Lane Road" (16 m)        "Medium Road" = "Four-Lane Road" (24 m)
//   "Large Road"  = "Six-Lane Road" (32 m)        "Small Road Oneway" = "Two-Lane One-Way Road" (16 m)
// Automatic resolution goes through AssetRef fallbacks (AutoAssets.Set: first LOADED fallback wins),
// so a playset without one of them falls back down the list, then to the category pool.

using System;
using System.Collections.Generic;
using Unity.Mathematics;

namespace Placemaker.Core.Layers.Roads
{
    /// <summary>A vanilla road the roads layer knows by name (display name and width for swatches, gallery and tests).</summary>
    public sealed class KnownRoad
    {
        public string Id;
        public string Name;
        public float Width;
        public KnownRoad(string id, string name, float width) { Id = id; Name = name; Width = width; }
    }

    public static class RoadRoles
    {
        // ── Role ids (legend, swatches, Lang keys "Placemaker.Roads.Role.<id>") ──
        public const string Street = "street";
        public const string Avenue = "avenue";
        public const string Local = "local";
        public const string Roundabout = "roundabout";
        public const string Perimeter = "perimeter";
        public const string CarFree = "carFree";
        /// <summary>(s34) Back lanes and gravel lanes (role Alley).</summary>
        public const string Lane = "lane";

        /// <summary>Display order of the legend.</summary>
        public static readonly string[] Order = { Street, Avenue, Local, Roundabout, Perimeter, Lane, CarFree };

        /// <summary>Legend role of a planned net; null = not a road of the roads layer (fences, parking aisles…).</summary>
        public static string Of(NetRole role)
        {
            switch (role)
            {
                case NetRole.Street: return Street;
                case NetRole.Avenue: return Avenue;
                case NetRole.CulDeSac: return Local;
                case NetRole.Roundabout: return Roundabout;
                case NetRole.PerimeterRoad: return Perimeter;
                case NetRole.Alley: return Lane;
                case NetRole.Pathway: case NetRole.PathwayWide: return CarFree;
                default: return null;
            }
        }

        /// <summary>
        /// Preview colour per role, "#rrggbb" (overlay, gallery, panel legend and mix swatches share it).
        /// Chosen to stay apart on the game's terrain and on the panel: white streets, cyan avenues
        /// (the old avenue colour), green locals, violet rings, orange perimeter, warm-yellow car-free.
        /// </summary>
        public static string Color(string role)
        {
            switch (role)
            {
                case Street: return "#f2f2f2";
                case Avenue: return "#59d9ff";
                case Local: return "#8fe07a";
                case Roundabout: return "#c58cff";
                case Perimeter: return "#ffa64d";
                case Lane: return "#c9a27a";
                case CarFree: return "#ffd173";
                default: return "#cccccc";
            }
        }

        /// <summary>Colour as linear 0..1 rgb (game overlay).</summary>
        public static float3 Rgb(string role) => RgbCache.TryGetValue(role ?? "", out var c) ? c : Parse(Color(role));

        private static readonly Dictionary<string, float3> RgbCache = BuildRgb();

        private static Dictionary<string, float3> BuildRgb()
        {
            var d = new Dictionary<string, float3>(StringComparer.Ordinal);
            foreach (var r in new[] { "street", "avenue", "local", "roundabout", "perimeter", "lane", "carFree" }) d[r] = Parse(Color(r));
            return d;
        }

        private static float3 Parse(string hex)
        {
            int v = Convert.ToInt32(hex.Substring(1), 16);
            return new float3(((v >> 16) & 255) / 255f, ((v >> 8) & 255) / 255f, (v & 255) / 255f);
        }

        public static string LabelKey(string role) => "Placemaker.Roads.Role." + role;

        /// <summary>The road-type control that picks the road of a role.</summary>
        public static string ControlOf(NetRole role)
        {
            switch (role)
            {
                case NetRole.Avenue: return RoadsIds.AvenueAsset;
                case NetRole.Roundabout: return RoadsIds.RoundaboutAsset;
                case NetRole.CulDeSac: return RoadsIds.LocalAsset;
                case NetRole.Pathway: case NetRole.PathwayWide: return RoadsIds.PathAsset;
                case NetRole.PerimeterRoad: return RoadsIds.PerimeterAsset;
                case NetRole.Alley: return RoadsIds.LaneAsset;
                default: return RoadsIds.StreetAsset;
            }
        }

        // ── Vanilla roads (1.6.2f1) ──
        public const string TwoLane = "Small Road";
        public const string FourLane = "Medium Road";
        public const string FourLaneDivided = "Medium Road Divided";
        public const string SixLane = "Large Road";
        public const string TwoLaneOneWay = "Small Road Oneway";
        public const string OneLaneOneWay = "Small Road Oneway - 1 lane";
        public const string ThreeLaneOneWay = "Small Road Oneway - 3 lanes";
        public const string FourLaneOneWay = "Medium Road Oneway";
        public const string Alley = "Alley";
        public const string Gravel = "Gravel Road";
        public const string GravelOneWay = "Gravel Road Oneway";
        public const string PedestrianStreet = "Pedestrian Street";
        public const string PedestrianStreetSmall = "Pedestrian Street Small";
        public const string PavementPath = "Pavement Path";

        public static readonly KnownRoad[] Known =
        {
            new KnownRoad(TwoLane, "Two-Lane Road", 16f),
            new KnownRoad(FourLane, "Four-Lane Road", 24f),
            new KnownRoad(FourLaneDivided, "Four-Lane Divided Road", 24f),
            new KnownRoad(SixLane, "Six-Lane Road", 32f),
            new KnownRoad(TwoLaneOneWay, "Two-Lane One-Way Road", 16f),
            new KnownRoad(OneLaneOneWay, "One-Lane One-Way Road", 16f),
            new KnownRoad(ThreeLaneOneWay, "Three-Lane One-Way Road", 16f),
            new KnownRoad(FourLaneOneWay, "Four-Lane One-Way Road", 24f),
            new KnownRoad(Alley, "Alley", 8f),
            new KnownRoad(Gravel, "Gravel Road", 8f),
            new KnownRoad(GravelOneWay, "Gravel One-Way Road", 8f),
            new KnownRoad(PedestrianStreet, "Pedestrian Street", 16f),
            new KnownRoad(PedestrianStreetSmall, "Small Pedestrian Street", 8f),
            new KnownRoad(PavementPath, "Pavement Path", 3f),
        };

        public static KnownRoad Find(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            foreach (var k in Known) if (k.Id == id) return k;
            return null;
        }

        /// <summary>English vanilla display name of a known road, else the id (gallery / tests; the game sends localised names).</summary>
        public static string NameOf(string id) => Find(id)?.Name ?? id ?? "";

        /// <summary>
        /// What Automatic places for a role, best first (the first one this playset has wins):
        /// streets and the perimeter a Two-Lane Road, avenues a Four-Lane Road, locals (dead ends, small
        /// side streets) the Two-Lane Road too (the smallest ordinary two-way road), roundabout rings a
        /// Two-Lane One-Way Road — two lanes in the ring direction, the same per-direction capacity as the
        /// Four-Lane avenue that feeds it. Car-free streets: the pathway pool (thin Pavement Path first).
        /// </summary>
        public static string[] AutoPicks(NetRole role)
        {
            switch (role)
            {
                case NetRole.Avenue: return new[] { FourLane, FourLaneDivided, TwoLane };
                case NetRole.Roundabout: return new[] { TwoLaneOneWay, ThreeLaneOneWay, OneLaneOneWay };
                case NetRole.Alley: return new[] { Alley, Gravel, TwoLane };
                case NetRole.Pathway: case NetRole.PathwayWide: return Array.Empty<string>();
                default: return new[] { TwoLane };
            }
        }

        /// <summary>The first automatic pick (for swatches / docs).</summary>
        public static string AutoPick(NetRole role)
        {
            var p = AutoPicks(role);
            return p.Length > 0 ? p[0] : "";
        }

        // ── Legend ──

        public sealed class LegendEntry
        {
            public string Role;
            public int Count;
            public double LengthM;
            /// <summary>Most used prefab of the role in the plan ("" = not resolved yet).</summary>
            public string AssetId = "";
            public string Control = "";
        }

        /// <summary>
        /// Roles present in <paramref name="nets"/> with their segment count, total length and main prefab,
        /// in <see cref="Order"/>. Car-free paths count only next to real roads (a park's footpaths are not a road plan).
        /// </summary>
        public static List<LegendEntry> Legend(IEnumerable<PlannedNet> nets)
        {
            var by = new Dictionary<string, LegendEntry>(StringComparer.Ordinal);
            var assetLen = new Dictionary<string, Dictionary<string, double>>(StringComparer.Ordinal);
            bool anyRoad = false;
            if (nets != null)
                foreach (var n in nets)
                {
                    if (n == null) continue;
                    string role = Of(n.Role);
                    if (role == null) continue;
                    if (role != CarFree) anyRoad = true;
                    if (!by.TryGetValue(role, out var e)) by[role] = e = new LegendEntry { Role = role, Control = n.Control ?? ControlOf(n.Role) };
                    double len = LengthXZ(n);
                    e.Count++;
                    e.LengthM += len;
                    string id = n.Asset.Id ?? "";
                    if (!assetLen.TryGetValue(role, out var m)) assetLen[role] = m = new Dictionary<string, double>(StringComparer.Ordinal);
                    m.TryGetValue(id, out double l0);
                    m[id] = l0 + len;
                }
            var r = new List<LegendEntry>();
            if (!anyRoad) return r;
            foreach (var role in Order)
            {
                if (!by.TryGetValue(role, out var e)) continue;
                string best = ""; double bestLen = -1;
                foreach (var kv in assetLen[role])
                    if (kv.Value > bestLen || (kv.Value == bestLen && string.CompareOrdinal(kv.Key, best) < 0)) { best = kv.Key; bestLen = kv.Value; }
                e.AssetId = best;
                e.LengthM = Math.Round(e.LengthM);
                r.Add(e);
            }
            return r;
        }

        public static double LengthXZ(PlannedNet n)
        {
            double len = 0;
            float3 prev = n.A;
            for (int i = 1; i <= 12; i++)
            {
                float t = i / 12f, u = 1f - t;
                float3 p = u * u * u * n.A + 3f * u * u * t * n.B + 3f * u * t * t * n.C + t * t * t * n.D;
                if (math.all(math.isfinite(p))) len += math.distance(prev.xz, p.xz);
                prev = p;
            }
            return len;
        }
    }
}
