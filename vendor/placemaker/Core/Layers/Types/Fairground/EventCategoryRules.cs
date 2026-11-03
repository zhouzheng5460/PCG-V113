// Name-based classification for the "events" categories (vanilla CS2 prefabs use English
// CamelCase names, matched lower-cased). Rules only ADD categories and never stop classification,
// so a container is still whatever the industrial rules make of it, and a portable toilet is
// still a restroom. Parent categories are added where one exists (food truck → kiosk, portable
// toilet → restroom, light tower → floodlight) so general layers can use them too.

using System.Collections.Generic;

namespace Placemaker.Core.Build
{
    public sealed class EventCategoryRules : ICategoryRules
    {
        public int Priority => 40;

        private static bool Has(string s, params string[] parts) => AssetHeuristics.ContainsAny(s, parts);

        private static void Add(List<string> r, string c) { if (!r.Contains(c)) r.Add(c); }

        public bool Classify(string lower, AssetTraits traits, List<string> r)
        {
            if (string.IsNullOrEmpty(lower) || (traits & AssetTraits.Building) != 0) return false;
            if (Has(lower, "carousel", "merrygoround", "merry-go-round", "merry go round", "ferris", "bigwheel", "big wheel", "observationwheel",
                    "rollercoaster", "roller coaster", "swingride", "swing ride", "chairoplane", "dodgem", "bumpercar", "bumper car",
                    "helterskelter", "helter skelter", "teacup", "amusementride", "amusement ride", "funfair", "fairgroundride", "drop tower", "droptower"))
                Add(r, AssetCategories.EventRide);
            if (Has(lower, "marquee", "partytent", "party tent", "eventtent", "event tent", "popuptent", "pop-up tent", "popup tent",
                    "gazebotent", "gazebo tent", "festivaltent", "festival tent", "beertent", "beer tent"))
                Add(r, AssetCategories.EventTent);
            if (Has(lower, "foodtruck", "food truck", "foodcart", "food cart", "foodvan", "food van", "icecreamtruck", "ice cream truck", "icecreamvan", "streetfood"))
            { Add(r, AssetCategories.EventFoodTruck); Add(r, AssetCategories.Kiosk); }
            if (Has(lower, "portabletoilet", "portable toilet", "portaloo", "portapotty", "porta potty", "chemicaltoilet", "chemical toilet", "toiletcabin", "toilet cabin", "toiletbooth"))
            { Add(r, AssetCategories.EventToilet); Add(r, AssetCategories.Restroom); }
            if (Has(lower, "lighttower", "light tower", "lightingtower", "lighting tower", "lightmast", "light mast", "lighttruss", "light truss", "mobilelight", "mobile light"))
            { Add(r, AssetCategories.EventLightTower); Add(r, AssetCategories.FloodLight); }
            if (Has(lower, "crowdbarrier", "crowd barrier", "temporaryfence", "temporary fence", "fencepanel", "fence panel", "mobilefence", "mobile fence",
                    "herasfence", "heras", "policebarrier", "police barrier", "crowdcontrol", "crowd control", "metalbarrier", "metal barrier"))
                Add(r, AssetCategories.EventBarrier);
            if (Has(lower, "flightcase", "flight case", "roadcase", "road case", "generator", "cablereel", "cable reel", "cabledrum", "cable drum",
                    "sitecabin", "site cabin", "portacabin", "officecontainer", "office container", "trailer", "shippingcontainer", "shipping container"))
                Add(r, AssetCategories.EventBackstage);
            if (Has(lower, "cinemascreen", "cinema screen", "moviescreen", "movie screen", "projectionscreen", "projection screen", "ledscreen", "led screen",
                    "ledwall", "led wall", "videowall", "video wall", "outdoorscreen", "outdoor screen", "jumbotron"))
                Add(r, AssetCategories.EventScreen);
            if (Has(lower, "loudspeaker", "speakerstack", "speaker stack", "speakertower", "speaker tower", "soundsystem", "sound system", "pa speaker", "paspeaker")
                || (lower.Contains("speaker") && !lower.Contains("speakers corner")))
                Add(r, AssetCategories.EventSpeaker);
            if (Has(lower, "stringlight", "string light", "festoon", "fairylight", "fairy light", "bunting", "garland", "lightstring", "light string", "partylight", "party light"))
                Add(r, AssetCategories.EventFestoon);
            if (Has(lower, "christmas", "xmas", "advent", "nativity", "reindeer", "snowman", "santa"))
                Add(r, AssetCategories.EventChristmas);
            if (Has(lower, "deckchair", "deck chair", "sunlounger", "sun lounger", "lounger", "beanbag", "bean bag", "lawnchair", "lawn chair", "campchair", "camp chair"))
                Add(r, AssetCategories.EventDeckchair);
            return false;
        }

        public bool? AutoEligible(string category, string lower, AssetTraits traits)
        {
            switch (category)
            {
                case AssetCategories.EventRide:
                case AssetCategories.EventTent:
                case AssetCategories.EventFoodTruck:
                case AssetCategories.EventToilet:
                case AssetCategories.EventLightTower:
                case AssetCategories.EventBarrier:
                case AssetCategories.EventBackstage:
                case AssetCategories.EventSpeaker:
                case AssetCategories.EventFestoon:
                case AssetCategories.EventChristmas:
                case AssetCategories.EventDeckchair:
                    return (traits & AssetTraits.Placeholder) == 0 && !Has(lower, "placeholder", "random", "broken", "destroyed", "wreck");
                case AssetCategories.EventScreen:
                    return (traits & AssetTraits.Placeholder) == 0 && !Has(lower, "placeholder", "advert", "logo", "billboard", "sign");
                default:
                    return null;
            }
        }
    }
}
