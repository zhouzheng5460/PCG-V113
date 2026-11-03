// Variety categories of this type (g5 integration): heaps, junk, crops and natural scatter look stamped
// when every automatic object of the category is the same prefab, so AutoAssets varies them by
// PlannedObject.Variation (up to AutoAssets.MaxVariety prefabs per step).

using System.Collections.Generic;
using Placemaker.Core.Build;

namespace Placemaker.Core.Layers.Types.Farmstead
{
    public sealed class FarmVariety : IVarietyCategories
    {
        public IEnumerable<string> VarietyCategories => new[]
        {
            AssetCategories.FarmHay,
            AssetCategories.FruitCrate,
            AssetCategories.FieldCrop,
            AssetCategories.FruitTree,
        };
    }
}
