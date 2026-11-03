// Placemaker roads — "road mixes": named combinations of which road goes in each role (s16, lead request).
// The mix is one choice control (RoadsIds.RoadMix) that WRITES the per-role road-type controls and the
// street/avenue upgrade toggles; its own value is DERIVED from them (any other combination = "custom"),
// so old saves, presets and manual picks always show the right mix. The planner never reads the mix.
// Only vanilla 1.6.2f1 prefabs (RoadRoles.Known); a playset without one falls back via AssetRef fallbacks.

using System;
using System.Collections.Generic;

namespace Placemaker.Core.Layers.Roads
{
    public sealed class RoadMix
    {
        public string Id;
        public string Street = "", Local = "", Avenue = "", Roundabout = "", Perimeter = "";
        /// <summary>Upgrade toggles switched ON (every other upgrade of <see cref="RoadMixes.UpgradeIds"/> is off).</summary>
        public string[] Upgrades = Array.Empty<string>();
        public bool Has(string upgrade) => Array.IndexOf(Upgrades, upgrade) >= 0;
    }

    public static class RoadMixes
    {
        public const string Automatic = "auto";
        public const string Custom = "custom";

        /// <summary>Road-type controls a mix sets (the car-free path stays the player's own choice).</summary>
        public static readonly string[] RoleIds =
            { RoadsIds.StreetAsset, RoadsIds.LocalAsset, RoadsIds.AvenueAsset, RoadsIds.RoundaboutAsset, RoadsIds.PerimeterAsset };

        public static readonly string[] UpgradeIds =
        {
            RoadsIds.StreetTrees, RoadsIds.StreetGrass, RoadsIds.StreetWideSidewalks, RoadsIds.StreetBikeLanes,
            RoadsIds.AvenueMedianTrees, RoadsIds.AvenueMedianGrass, RoadsIds.AvenueTrees, RoadsIds.AvenueGrass, RoadsIds.AvenueBikeLanes,
        };

        private static RoadMix M(string id, string street, string local, string avenue, string roundabout, string perimeter, params string[] upgrades) =>
            new RoadMix { Id = id, Street = street, Local = local, Avenue = avenue, Roundabout = roundabout, Perimeter = perimeter, Upgrades = upgrades };

        /// <summary>Every mix in display order ("custom" is not a mix: it is whatever the player picked).</summary>
        public static readonly RoadMix[] All =
        {
            // Automatic = the standard mix: Two-Lane streets, Four-Lane avenues, Two-Lane One-Way rings (RoadRoles.AutoPicks).
            M(Automatic, "", "", "", "", ""),
            // Calm streets with trees and grass verges; even the avenues stay two-lane.
            M("quiet", RoadRoles.TwoLane, RoadRoles.TwoLane, RoadRoles.TwoLane, RoadRoles.OneLaneOneWay, RoadRoles.TwoLane,
                RoadsIds.StreetTrees, RoadsIds.StreetGrass),
            // Narrow alleys between the blocks, Two-Lane roads for the main lines and the edge.
            M("alleys", RoadRoles.Alley, RoadRoles.Alley, RoadRoles.TwoLane, RoadRoles.OneLaneOneWay, RoadRoles.TwoLane),
            // Pedestrian streets inside; cars stay on the Two-Lane avenues and perimeter.
            M("pedestrian", RoadRoles.PedestrianStreet, RoadRoles.PedestrianStreetSmall, RoadRoles.TwoLane, RoadRoles.OneLaneOneWay, RoadRoles.TwoLane),
            // Tree-lined streets, divided Four-Lane avenues with a planted median.
            M("boulevards", RoadRoles.TwoLane, RoadRoles.TwoLane, RoadRoles.FourLaneDivided, RoadRoles.TwoLaneOneWay, RoadRoles.TwoLane,
                RoadsIds.StreetTrees, RoadsIds.AvenueMedianTrees, RoadsIds.AvenueTrees),
            // Busy core: Four-Lane streets, Six-Lane avenues, three-lane rings (matching three lanes per direction).
            M("downtown", RoadRoles.FourLane, RoadRoles.TwoLane, RoadRoles.SixLane, RoadRoles.ThreeLaneOneWay, RoadRoles.FourLane),
            // The standard mix with bike lanes on every street and avenue.
            M("bike", RoadRoles.TwoLane, RoadRoles.TwoLane, RoadRoles.FourLane, RoadRoles.TwoLaneOneWay, RoadRoles.TwoLane,
                RoadsIds.StreetBikeLanes, RoadsIds.AvenueBikeLanes),
            // Gravel lanes for villages and farmland, a Two-Lane road for the through routes.
            M("country", RoadRoles.Gravel, RoadRoles.Gravel, RoadRoles.TwoLane, RoadRoles.GravelOneWay, RoadRoles.TwoLane),
        };

        /// <summary>Choice values of the mix control: every mix, then "custom".</summary>
        public static string[] Values()
        {
            var v = new string[All.Length + 1];
            for (int i = 0; i < All.Length; i++) v[i] = All[i].Id;
            v[All.Length] = Custom;
            return v;
        }

        public static RoadMix Find(string id)
        {
            foreach (var m in All) if (m.Id == id) return m;
            return null;
        }

        public static string RoleValue(RoadMix m, string controlId)
        {
            switch (controlId)
            {
                case RoadsIds.StreetAsset: return m.Street;
                case RoadsIds.LocalAsset: return m.Local;
                case RoadsIds.AvenueAsset: return m.Avenue;
                case RoadsIds.RoundaboutAsset: return m.Roundabout;
                case RoadsIds.PerimeterAsset: return m.Perimeter;
                default: return "";
            }
        }

        /// <summary>Writes a mix's picks and upgrades into <paramref name="s"/>; returns the ids it set (empty for "custom"/unknown).</summary>
        public static string[] Apply(SettingsBag s, string mixId)
        {
            var m = Find(mixId);
            if (m == null || s == null) return Array.Empty<string>();
            var ids = new List<string>();
            foreach (var id in RoleIds) { s.Values[id] = RoleValue(m, id); ids.Add(id); }
            foreach (var id in UpgradeIds) { s.Values[id] = m.Has(id); ids.Add(id); }
            return ids.ToArray();
        }

        /// <summary>The mix that matches the current picks exactly, else "custom". Missing values read as defaults ("" / off).</summary>
        public static string Match(SettingsBag s)
        {
            if (s == null) return Automatic;
            foreach (var m in All)
            {
                bool ok = true;
                foreach (var id in RoleIds)
                    if (s.S(id, "") != RoleValue(m, id)) { ok = false; break; }
                if (ok)
                    foreach (var id in UpgradeIds)
                        if (s.B(id, false) != m.Has(id)) { ok = false; break; }
                if (ok) return m.Id;
            }
            return Custom;
        }

        /// <summary>The road a role gets under a mix: its pick, else Automatic's first choice (perimeter: the street's).</summary>
        public static string Effective(RoadMix m, NetRole role)
        {
            string v;
            switch (role)
            {
                case NetRole.Avenue: v = m.Avenue; break;
                case NetRole.Roundabout: v = m.Roundabout; break;
                case NetRole.CulDeSac: v = m.Local; break;
                case NetRole.PerimeterRoad: v = !string.IsNullOrEmpty(m.Perimeter) ? m.Perimeter : m.Street; break;
                default: v = m.Street; break;
            }
            return !string.IsNullOrEmpty(v) ? v : RoadRoles.AutoPick(role);
        }

        /// <summary>Swatch of a mix: street, avenue and local stripes in role colours and real widths.</summary>
        public static List<OptionSwatch> Swatch(RoadMix m)
        {
            OptionSwatch S(string role, NetRole netRole, bool green, bool bike) => new OptionSwatch
            {
                Role = role, Color = RoadRoles.Color(role), Width = RoadRoles.Find(Effective(m, netRole))?.Width ?? 16f, Green = green, Bike = bike,
            };
            return new List<OptionSwatch>
            {
                S(RoadRoles.Street, NetRole.Street, m.Has(RoadsIds.StreetTrees) || m.Has(RoadsIds.StreetGrass), m.Has(RoadsIds.StreetBikeLanes)),
                S(RoadRoles.Avenue, NetRole.Avenue, m.Has(RoadsIds.AvenueTrees) || m.Has(RoadsIds.AvenueMedianTrees) || m.Has(RoadsIds.AvenueGrass) || m.Has(RoadsIds.AvenueMedianGrass),
                    m.Has(RoadsIds.AvenueBikeLanes)),
                S(RoadRoles.Local, NetRole.CulDeSac, m.Has(RoadsIds.StreetTrees) || m.Has(RoadsIds.StreetGrass), m.Has(RoadsIds.StreetBikeLanes)),
            };
        }
    }
}
