// s53 — "Placemaker anarchy": which planned objects overlap ANOTHER planned object (their own build).
//
// The game rejects an object whose collision box meets another's (OverlapExisting) — including the pieces of one and the
// same Placemaker build (stall markings 3.2 m wide on a 3.0 m pitch, bushes in a bed, a lamp beside a bin). Those are our
// own planning, so with "Ignore collisions in my builds" the build keeps them: the error is not a reason to leave anything
// out. An error on an object that overlaps nothing of ours is a collision with the city (building, road, foreign prop) and
// stays a real error. Pure geometry (Declutter footprints) so it is unit-tested.

using System.Collections.Generic;
using Unity.Mathematics;

namespace Placemaker.Core.Build
{
    public sealed class OwnOverlap
    {
        private const float Cell = 8f;
        private readonly Dictionary<long, List<int>> _grid = new Dictionary<long, List<int>>();
        private readonly List<Declutter.Footprint> _fps = new List<Declutter.Footprint>();
        private readonly List<int> _layerOf = new List<int>();
        private readonly List<int> _indexOf = new List<int>();
        private readonly Dictionary<long, int> _slot = new Dictionary<long, int>();

        public int Footprints => _fps.Count;

        public OwnOverlap(ComposedPlan plan, IReadOnlyDictionary<string, AssetInfo> assets)
        {
            if (plan?.Layers == null) return;
            for (int li = 0; li < plan.Layers.Count; li++)
            {
                var l = plan.Layers[li];
                if (l?.Objects == null) continue;
                for (int i = 0; i < l.Objects.Count; i++)
                {
                    var o = l.Objects[i];
                    if (o == null || o.Role == ObjectRole.Marker || !Declutter.TryFootprint(o, assets, out var f)) continue;
                    int id = _fps.Count;
                    _fps.Add(f); _layerOf.Add(li); _indexOf.Add(i);
                    _slot[((long)li << 32) | (uint)i] = id;
                    int r = (int)math.ceil(f.Radius / Cell);
                    int cx = (int)math.floor(f.Center.x / Cell), cz = (int)math.floor(f.Center.y / Cell);
                    for (int x = cx - r; x <= cx + r; x++)
                        for (int z = cz - r; z <= cz + r; z++)
                        {
                            long k = Key(x, z);
                            if (!_grid.TryGetValue(k, out var list)) _grid[k] = list = new List<int>();
                            list.Add(id);
                        }
                }
            }
        }

        private static long Key(int x, int z) => ((long)x << 32) | (uint)z;

        /// <summary>True when object (layer, index) overlaps another planned object (needs asset bounds for both).</summary>
        public bool Overlaps(int layer, int index, float gap = 0f)
        {
            if (!_slot.TryGetValue(((long)layer << 32) | (uint)index, out int id)) return false;
            var f = _fps[id];
            int cx = (int)math.floor(f.Center.x / Cell), cz = (int)math.floor(f.Center.y / Cell);
            if (!_grid.TryGetValue(Key(cx, cz), out var near)) return false;
            foreach (int other in near)
                if (other != id && Declutter.Overlaps(f, _fps[other], gap)) return true;
            return false;
        }
    }
}
