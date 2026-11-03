// Ground height after a layer's terraform: the original ground plus override patches.
//
// Why: features of a layer that reshapes the ground (a raised forecourt, a mound, a terraced garden)
// used to plan on the ORIGINAL ground (SiteContext.HeightAt), so the preview showed their objects
// floating or buried and their own levelling fought the parent's. The field answers "what will the
// ground be here after this layer's terraform" so
//  * the layer itself places objects/nets/surfaces on the new ground (LayerContext.P3 / G), and
//  * the composer hands the new ground to the layer's children (AreaPlan.HeightAfter → child
//    SiteContext.HeightAt), so features plan on it too.
// The build still snaps every object to the real terrain after terraform has been applied
// (BuildService: terraform → nets → objects), so plan heights only matter for previews and for
// layers that compare heights (levelling pads, water levels).

using System;
using System.Collections.Generic;
using Unity.Mathematics;

namespace Placemaker.Core.Layers.Common
{
    public sealed class TerrainField
    {
        private struct Patch
        {
            public float2 Min, Max;
            public Func<float2, float> Height;
            public Func<float2, bool> Covers;
        }

        private readonly Func<float2, float> _base;
        private readonly List<Patch> _patches = new List<Patch>();

        public TerrainField(Func<float2, float> baseHeight) { _base = baseHeight ?? (_ => 0f); }

        /// <summary>The ground before any override.</summary>
        public Func<float2, float> Base => _base;
        public bool HasOverrides => _patches.Count > 0;
        public int Count => _patches.Count;

        /// <summary>
        /// Overrides the ground inside [min, max] (optionally only where <paramref name="covers"/> is
        /// true) with <paramref name="height"/>. Later overrides win where they overlap. A height that
        /// returns NaN leaves the point to earlier overrides / the base.
        /// </summary>
        public void Override(float2 min, float2 max, Func<float2, float> height, Func<float2, bool> covers = null)
        {
            if (height == null) return;
            _patches.Add(new Patch { Min = math.min(min, max), Max = math.max(min, max), Height = height, Covers = covers });
        }

        /// <summary>A flat pad at <paramref name="height"/> inside a polygon.</summary>
        public void OverridePolygon(IReadOnlyList<float2> ring, float height)
        {
            if (ring == null || ring.Count < 3 || !math.isfinite(height)) return;
            var poly = new float2[ring.Count];
            float2 min = float.MaxValue, max = float.MinValue;
            for (int i = 0; i < poly.Length; i++) { poly[i] = ring[i]; min = math.min(min, poly[i]); max = math.max(max, poly[i]); }
            Override(min, max, _ => height, p => ReservedSpace.PointInPolygon(p, poly));
        }

        /// <summary>A shaped region (TerrainShaper/YardKit/SiteKit results all expose HeightAfter).</summary>
        public void Override(LayerRegion region, Func<float2, float> heightAfter)
        {
            if (region == null || heightAfter == null) return;
            Override(region.Min, region.Max, heightAfter, region.Contains);
        }

        public float HeightAt(float2 p)
        {
            for (int i = _patches.Count - 1; i >= 0; i--)
            {
                var q = _patches[i];
                if (p.x < q.Min.x || p.y < q.Min.y || p.x > q.Max.x || p.y > q.Max.y) continue;
                if (q.Covers != null && !q.Covers(p)) continue;
                float h = q.Height(p);
                if (math.isfinite(h)) return h;
            }
            return _base(p);
        }

        /// <summary>
        /// Ground after a list of planned terraform polygons (later entries win), else the base. Used
        /// by the composer when a terraforming layer did not publish its own AreaPlan.HeightAfter:
        /// every Level/Raise/Lower polygon is levelled to its TargetHeight by the build.
        /// Null when there is nothing to override.
        /// </summary>
        public static Func<float2, float> FromTerraform(IReadOnlyList<PlannedTerraform> terraform, Func<float2, float> baseHeight)
        {
            if (terraform == null || terraform.Count == 0) return null;
            var index = new TerraformIndex(terraform);
            if (index.Count == 0) return null;
            var b = baseHeight ?? (_ => 0f);
            return p => index.TryHeight(p, out float h) ? h : b(p);
        }

        /// <summary>Bucket grid over terraform polygons so per-point lookups stay cheap for ~1500 cells.</summary>
        private sealed class TerraformIndex
        {
            private readonly List<(float2[] ring, float target, float2 min, float2 max, PlannedTerraform item)> _items = new List<(float2[], float, float2, float2, PlannedTerraform)>();
            private readonly Dictionary<long, List<int>> _cells = new Dictionary<long, List<int>>();
            private readonly float _cell;

            public int Count => _items.Count;

            public TerraformIndex(IReadOnlyList<PlannedTerraform> terraform)
            {
                double size = 0;
                foreach (var t in terraform)
                {
                    if (t?.Ring == null || t.Ring.Length < 3 || !math.isfinite(t.TargetHeight)) continue;
                    var ring = new float2[t.Ring.Length];
                    float2 min = float.MaxValue, max = float.MinValue;
                    for (int i = 0; i < ring.Length; i++) { ring[i] = t.Ring[i].xz; min = math.min(min, ring[i]); max = math.max(max, ring[i]); }
                    _items.Add((ring, t.TargetHeight, min, max, t.Sloped ? t : null));
                    size += math.cmax(max - min);
                }
                _cell = _items.Count == 0 ? 16f : math.max(4f, (float)(size / _items.Count) * 2f);
                for (int i = 0; i < _items.Count; i++)
                {
                    var it = _items[i];
                    int x0 = (int)math.floor(it.min.x / _cell), x1 = (int)math.floor(it.max.x / _cell);
                    int y0 = (int)math.floor(it.min.y / _cell), y1 = (int)math.floor(it.max.y / _cell);
                    // A huge polygon (a whole-lot level) would flood the grid: cap and fall back to a scan list.
                    if ((long)(x1 - x0 + 1) * (y1 - y0 + 1) > 4096) { Add(long.MinValue, i); continue; }
                    for (int y = y0; y <= y1; y++)
                        for (int x = x0; x <= x1; x++) Add(Key(x, y), i);
                }
            }

            private void Add(long key, int i)
            {
                if (!_cells.TryGetValue(key, out var list)) _cells[key] = list = new List<int>(2);
                list.Add(i);
            }

            private static long Key(int x, int y) => ((long)x << 32) ^ (uint)y;

            public bool TryHeight(float2 p, out float h)
            {
                int best = -1;
                if (_cells.TryGetValue(Key((int)math.floor(p.x / _cell), (int)math.floor(p.y / _cell)), out var list)) best = Scan(list, p, best);
                if (_cells.TryGetValue(long.MinValue, out var big)) best = Scan(big, p, best);
                h = best < 0 ? 0f : _items[best].item != null ? Build.TerraformSurface.At(_items[best].item.Ring, p) : _items[best].target;
                return best >= 0;
            }

            private int Scan(List<int> list, float2 p, int best)
            {
                // Later terraform entries win (children are planned after their parents).
                for (int k = list.Count - 1; k >= 0; k--)
                {
                    int i = list[k];
                    if (i <= best) continue;
                    var it = _items[i];
                    if (p.x < it.min.x || p.y < it.min.y || p.x > it.max.x || p.y > it.max.y) continue;
                    if (ReservedSpace.PointInPolygon(p, it.ring)) { best = i; break; }
                }
                return best;
            }
        }
    }
}
