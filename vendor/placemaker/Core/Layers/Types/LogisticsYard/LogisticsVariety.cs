// Variety categories of this type (g5 integration): heaps, junk, crops and natural scatter look stamped
// when every automatic object of the category is the same prefab, so AutoAssets varies them by
// PlannedObject.Variation (up to AutoAssets.MaxVariety prefabs per step).

using System.Collections.Generic;
using Placemaker.Core.Build;

namespace Placemaker.Core.Layers.Types.LogisticsYard
{
    public sealed class LogisticsVariety : IVarietyCategories
    {
        public IEnumerable<string> VarietyCategories => new[]
        {
            AssetCategories.LogisticsCargo,
            AssetCategories.LogisticsContainer,
            AssetCategories.LogisticsPallet,
            AssetCategories.LogisticsCrate,
            AssetCategories.LogisticsLumber,
            AssetCategories.LogisticsBulk,
            AssetCategories.LogisticsBarrel,
        };
    }
}
