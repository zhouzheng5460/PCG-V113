// Net categories of the park path hierarchy (grand park wave, g3): bike paths and dirt/gravel trails.
//
// Vanilla CS2 has no dedicated bicycle net in every build, and real prefab names are not curated yet
// (no catalog dump), so both tiers degrade gracefully: the layer emits the net with role PathwayWide /
// Pathway and an AUTOMATIC asset whose fallbacks are the likely prefab names below. AutoAssets picks
// the first fallback that is loaded; when none is, the ordinary pathway pool is used (a wide footpath
// for the bike route, a plain footpath for trails). A player can always pick an asset explicitly.
//
// Classification: the rules below tag nets by name. NOTE (docs/proposals/types-g3.md): today
// AssetHeuristics returns from its net branch before CategoryRules run, so these rules only take
// effect once the net branch also consults CategoryRules; until then the asset pickers for these
// categories may be empty and the name fallbacks do the work.

using System.Collections.Generic;
using Placemaker.Core.Build;

namespace Placemaker.Core
{
    public static partial class AssetCategories
    {
        /// <summary>Bicycle paths / cycle tracks (nets). Fallback: wide pathway.</summary>
        public const string BikePath = "net.bike";
        /// <summary>Dirt, gravel and hiking trails (nets). Fallback: pathway.</summary>
        public const string TrailPath = "net.trail";
    }
}

namespace Placemaker.Core.Layers.Park
{
    public static class ParkPathNets
    {
        /// <summary>Likely bike-path prefab names (first loaded wins).</summary>
        public static readonly string[] BikeNames =
        {
            // Vanilla (Bikes update): the two-way bike path, then the pedestrian path with bike lanes.
            "TwoWayBikePath", "PedestrianPathWithBikeLanes", "Bicycle Path", "Bike Path", "Cycle Path", "Cycle Track",
        };

        /// <summary>Likely dirt / gravel trail prefab names (first loaded wins).</summary>
        public static readonly string[] TrailNames =
        {
            // No vanilla gravel footpath is known; these catch common custom names (else the pathway pool).
            "Gravel Pathway", "Gravel Path", "Dirt Path", "Dirt Pathway", "Hiking Trail", "Trail Path", "Gravel Footpath",
            "Forest Path", "Natural Path",
        };

        public static AssetRef Bike(string chosen) => new AssetRef(chosen ?? "", BikeNames);
        public static AssetRef Trail(string chosen) => new AssetRef(chosen ?? "", TrailNames);
    }

    public sealed class ParkPathCategoryRules : ICategoryRules
    {
        public int Priority => 40;

        private static bool Has(string s, params string[] parts) => AssetHeuristics.ContainsAny(s, parts);

        public bool Classify(string lower, AssetTraits traits, List<string> r)
        {
            // Nets only: a bike rack or a "trail sign" prop is not a path.
            if ((traits & AssetTraits.Net) == 0 || (traits & AssetTraits.NetFence) != 0) return false;
            if (Has(lower, "invisible", "marker")) return false;
            if (Has(lower, "bicycle", "bike", "cycle"))
            {
                r.Add(AssetCategories.BikePath);
                return false;
            }
            if ((traits & AssetTraits.Pathway) != 0 && Has(lower, "gravel", "dirt", "trail", "hiking", "forest", "natural", "mud"))
                r.Add(AssetCategories.TrailPath);
            return false;
        }

        public bool? AutoEligible(string category, string lower, AssetTraits traits) => null;
    }
}
