// Asset vocabulary of the "events" group (fairground, amphitheatre). Every category degrades to a
// more general one through LeisureKit.Category chains in the layers ("a food truck, else a kiosk"),
// so an install without event props still builds a sensible site.

namespace Placemaker.Core
{
    public static partial class AssetCategories
    {
        public const string EventRide = "events.ride";               // carousels, big wheels, swing rides, dodgems
        public const string EventTent = "events.tent";               // marquees, party / gazebo tents (not camping tents)
        public const string EventFoodTruck = "events.foodTruck";     // food trucks / carts (also kiosk)
        public const string EventToilet = "events.toilet";           // portable toilet cabins (also restroom)
        public const string EventLightTower = "events.lightTower";   // mobile light towers, lighting masts, truss towers
        public const string EventBarrier = "events.barrier";         // crowd barriers, temporary fence panels
        public const string EventBackstage = "events.backstage";     // containers, trailers, generators, flight cases
        public const string EventScreen = "events.screen";           // cinema / LED screens
        public const string EventSpeaker = "events.speaker";         // PA speakers, sound towers
        public const string EventFestoon = "events.festoon";         // string lights, bunting, festoon poles
        public const string EventChristmas = "events.christmas";     // Christmas trees, decorations
        public const string EventDeckchair = "events.deckchair";     // deckchairs, loungers, beanbags
    }
}
