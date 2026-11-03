// Asset vocabulary for the hiking trail (builder 12): trail signs, way markers and map boards.
// Vanilla names are not known yet; the rules match the usual CamelCase parts ("SignPost01",
// "TrailMarker", "InfoBoard", "ParkMapSign") and exclude road/traffic/advertising signs.

using System.Collections.Generic;
using Placemaker.Core.Build;

namespace Placemaker.Core
{
    public static partial class AssetCategories
    {
        /// <summary>Signposts, way markers, information and map boards for trails and parks.</summary>
        public const string TrailSign = "trail.sign";
    }
}

namespace Placemaker.Core.Layers.Types.Hiking
{
    public sealed class TrailCategoryRules : ICategoryRules
    {
        public int Priority => 20;

        private static bool Has(string s, params string[] parts) => AssetHeuristics.ContainsAny(s, parts);

        private static readonly string[] NotTrail =
        {
            "road sign", "roadsign", "traffic", "street sign", "streetsign", "street name", "speed", "stop sign", "stopsign", "yield",
            "parking sign", "highway", "motorway", "advert", "billboard", "neon", "shop", "store", "brand", "logo", "poster", "bus", "tram", "train",
        };

        public bool Classify(string lower, AssetTraits traits, List<string> categories)
        {
            if ((traits & AssetTraits.Plant) != 0 || Has(lower, NotTrail)) return false;
            if (Has(lower, "signpost", "sign post", "fingerpost", "finger post", "trailsign", "trail sign", "trail marker", "trailmarker", "waymark",
                "wayfinding", "way finding", "direction sign", "directionsign", "infoboard", "info board", "information board", "informationboard",
                "information sign", "informationsign", "infosign", "info sign", "mapboard", "map board", "mapsign", "map sign", "park map", "parkmap",
                "trailhead", "trail head", "noticeboard", "notice board", "interpretive"))
            { categories.Add(AssetCategories.TrailSign); return false; }
            return false;
        }

        public bool? AutoEligible(string category, string lower, AssetTraits traits)
        {
            if (category != AssetCategories.TrailSign) return null;
            // Signs are generally never automatic; trail signs are the exception this category exists for.
            return !Has(lower, "random", "placeholder", "template", "test", "dummy", "advert", "billboard", "logo", "broken");
        }
    }
}
