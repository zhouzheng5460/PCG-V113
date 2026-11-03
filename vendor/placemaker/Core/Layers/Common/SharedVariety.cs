// Shared variety categories (g5 integration) not owned by one type folder: allotment / market
// garden crops vary per plot or row (layers pass one Variation per plot), never per plant.

using System.Collections.Generic;
using Placemaker.Core.Build;

namespace Placemaker.Core.Layers.Common
{
    public sealed class SharedVariety : IVarietyCategories
    {
        public IEnumerable<string> VarietyCategories => new[] { AssetCategories.GardenCrop };
    }
}
