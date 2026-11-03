// Small builder for IHasFeatures lists (see Core.FeatureInfo).

using System.Collections.Generic;

namespace Placemaker.Core.Layers.Common
{
    internal sealed class FeatureList
    {
        private readonly List<FeatureInfo> _list = new List<FeatureInfo>();

        public FeatureList Add(string layer, string target = "", bool defaultOn = false, params (string id, object value)[] preset)
        {
            var f = new FeatureInfo { Layer = layer, Target = target ?? "", DefaultOn = defaultOn };
            if (preset != null) foreach (var (id, value) in preset) f.Preset.Values[id] = value;
            _list.Add(f);
            return this;
        }

        public IReadOnlyList<FeatureInfo> Build() => _list.AsReadOnly();
    }
}
