// Asset vocabulary of the transport types (rest area, transit plaza) and its name-based
// classification. Real vanilla names are not curated yet (no catalog dump), so the rules use broad
// CamelCase word patterns and stay narrow enough not to steal assets from the general classifier:
// a rule only claims an asset when the name clearly says what it is.

using System.Collections.Generic;
using Placemaker.Core.Build;

namespace Placemaker.Core
{
    public static partial class AssetCategories
    {
        /// <summary>Bus / tram / taxi waiting shelters (also "shelter", but never automatic park shelters).</summary>
        public const string TransportShelter = "transport.shelter";
        /// <summary>Bicycle racks, stands and bike-share docks.</summary>
        public const string BikeRack = "transport.bikeRack";
        /// <summary>Wayfinding: map boards, info totems, direction signposts, timetable poles.</summary>
        public const string Wayfinding = "transport.sign";
        /// <summary>Vending machines, ticket machines (also "kiosk" for explicit picks).</summary>
        public const string Vending = "transport.vending";
        /// <summary>Fuel pumps / dispensers (truck stop fuel island).</summary>
        public const string FuelPump = "transport.fuel";
    }
}

namespace Placemaker.Core.Layers.Types.Transport
{
    public sealed class TransportCategoryRules : ICategoryRules
    {
        public int Priority => 40;

        private static bool Has(string s, params string[] parts) => AssetHeuristics.ContainsAny(s, parts);

        public bool Classify(string lower, AssetTraits traits, List<string> categories)
        {
            if (Has(lower, "advert", "billboard", "poster", "neon", "logo")) return false;
            if (Has(lower, "busshelter", "bus shelter", "busstopshelter", "bus stop shelter", "tramshelter", "tram shelter",
                "taxishelter", "taxi shelter", "waitingshelter", "waiting shelter", "transitshelter", "transit shelter"))
            {
                categories.Add(AssetCategories.TransportShelter);
                categories.Add(AssetCategories.Shelter);
                return true;
            }
            if (Has(lower, "bikerack", "bike rack", "bicyclerack", "bicycle rack", "bikestand", "bike stand", "bicyclestand",
                "bicycle stand", "bicycleparking", "bicycle parking", "bikeparking", "bike parking", "cyclerack", "cycle rack",
                "bikeshare", "bike share", "bikedock", "bike dock", "cyclehoop"))
            {
                categories.Add(AssetCategories.BikeRack);
                return true;
            }
            if (Has(lower, "vending", "snackmachine", "snack machine", "drinkmachine", "drink machine", "sodamachine", "soda machine",
                "ticketmachine", "ticket machine", "ticketvending"))
            {
                categories.Add(AssetCategories.Vending);
                categories.Add(AssetCategories.Kiosk);
                return true;
            }
            if (Has(lower, "fuelpump", "fuel pump", "gaspump", "gas pump", "petrolpump", "petrol pump", "fueldispenser", "fuel dispenser"))
            {
                categories.Add(AssetCategories.FuelPump);
                return true;
            }
            if (Has(lower, "wayfinding", "infoboard", "info board", "informationboard", "information board", "mapboard", "map board",
                "citymap", "city map", "infototem", "info totem", "infopillar", "info pillar", "signpost", "sign post",
                "directionsign", "direction sign", "timetable", "noticeboard", "notice board"))
            {
                categories.Add(AssetCategories.Wayfinding);
                return true;
            }
            return false;
        }

        public bool? AutoEligible(string category, string lower, AssetTraits traits)
        {
            switch (category)
            {
                case AssetCategories.TransportShelter:
                case AssetCategories.BikeRack:
                case AssetCategories.Vending:
                case AssetCategories.FuelPump:
                case AssetCategories.Wayfinding:
                    // Street-attached variants (a bus stop pole snapping to a road) are not free props.
                    if ((traits & AssetTraits.NetAttached) != 0) return false;
                    return !Has(lower, "placeholder", "random", "test", "advert", "billboard", "traffic", "speed", "parking sign");
                default:
                    return null;
            }
        }
    }
}
