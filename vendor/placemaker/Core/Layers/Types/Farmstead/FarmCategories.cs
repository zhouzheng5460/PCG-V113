// Asset vocabulary for the farm types (farmstead, orchard park) and the shared farm add-ons
// (apiary, farm stand). Vanilla CS2 has farm props (silos, barns-as-props, hay bales, tractors,
// troughs) mostly as sub-objects of the agriculture extractor buildings; the standalone ones that
// exist are classified by name below (FarmCategoryRules). Every layer asks through a fallback
// chain ("a silo, else a garden shed") and degrades to an Info issue when nothing is installed.

namespace Placemaker.Core
{
    public static partial class AssetCategories
    {
        public const string FarmBarn = "farm.barn";             // barns, stables, cowsheds, pole barns, machine sheds
        public const string FarmSilo = "farm.silo";             // tower silos, grain bins, feed hoppers
        public const string FarmMachinery = "farm.machinery";   // tractors, harvesters, ploughs, balers, farm trailers
        public const string FarmHay = "farm.hay";               // hay / straw bales and stacks
        public const string FarmTrough = "farm.trough";         // water troughs, hay feeders, mangers
        public const string FarmWindpump = "farm.windpump";     // wind pumps, small windmills, water towers on farms
        public const string Beehive = "farm.beehive";           // beehives, apiary boxes
        public const string FruitCrate = "farm.crate";          // fruit crates, harvest baskets, ladders, produce boxes
        public const string FieldCrop = "farm.crop";            // field crops (wheat, maize, sunflowers…); also garden.crop
        public const string FruitTree = "tree.fruit";           // apple, cherry, pear, olive, citrus… (also tree)
    }
}
