// Placemaker — s49: the raw obstacle set around a block, kept so that switching Smart edges / a brush rule only re-filters it.
//
// Reading the game's search trees (buildings, lots, zone cells, receipts) is the expensive part of a brush rule change;
// the rules only decide which of the gathered polygons count. The cache holds every kind, whatever the rules say,
// for one box and one "ignored group" and answers any smaller box asked for with the same group while it is fresh.
// Pure Core (no ECS) so the keys, the box test and the per-kind counts are unit tested.

using System;
using System.Collections.Generic;
using System.Text;
using Unity.Mathematics;

namespace Placemaker.Core.Geometry
{
    public sealed class ObstacleCache
    {
        /// <summary>A cached set older than this (s) is read again even when the caller accepts a cache.</summary>
        public const float MaxAge = 120f;

        private float2 _min, _max;
        private int _ignore;
        private float _at = float.NegativeInfinity;
        private List<BrushObstacle> _all;

        public bool HasData => _all != null;

        public void Clear() { _all = null; _at = float.NegativeInfinity; }

        public void Store(float2 min, float2 max, int ignoreGroup, float now, List<BrushObstacle> all)
        {
            _min = min; _max = max; _ignore = ignoreGroup; _at = now; _all = all ?? new List<BrushObstacle>();
        }

        /// <summary>The cached obstacles touching [min, max] (a new list), or null when the cache cannot answer.</summary>
        public List<BrushObstacle> TryGet(float2 min, float2 max, int ignoreGroup, float now, bool fresh)
        {
            if (_all == null || fresh || ignoreGroup != _ignore || now - _at > MaxAge || now < _at) return null;
            if (min.x < _min.x || min.y < _min.y || max.x > _max.x || max.y > _max.y) return null;
            var list = new List<BrushObstacle>(_all.Count);
            foreach (var o in _all)
            {
                if (o.Ring == null || o.Ring.Length == 0) continue;
                Poly.Bounds(o.Ring, out var a, out var b);
                if (a.x > max.x || a.y > max.y || b.x < min.x || b.y < min.y) continue;
                list.Add(o);
            }
            return list;
        }
    }

    /// <summary>Counts and area per obstacle kind — the "what is the brush looking at" debug line.</summary>
    public struct ObstacleStats
    {
        public const int Kinds = 7;
        public int[] Count;
        public float[] Area;

        public static ObstacleStats Of(IEnumerable<BrushObstacle> obstacles)
        {
            var s = new ObstacleStats { Count = new int[Kinds], Area = new float[Kinds] };
            if (obstacles == null) return s;
            foreach (var o in obstacles)
            {
                int k = (int)o.Kind;
                if (k < 0 || k >= Kinds || o.Ring == null || o.Ring.Length < 3) continue;
                s.Count[k]++;
                s.Area[k] += math.abs(Poly.SignedArea(o.Ring));
            }
            return s;
        }

        public int Total { get { int n = 0; if (Count != null) foreach (var c in Count) n += c; return n; } }
        public float TotalArea { get { float a = 0; if (Area != null) foreach (var c in Area) a += c; return a; } }

        public override string ToString()
        {
            if (Count == null) return "none";
            var names = new[] { "roads", "buildings", "lots", "zonedBuilt", "emptyZone", "myBuilds", "zonedUnbuilt" };
            var sb = new StringBuilder();
            for (int i = 0; i < Kinds; i++) sb.Append(names[i]).Append('=').Append(Count[i]).Append(" (").Append(Area[i].ToString("0")).Append(" m2) ");
            sb.Append("total=").Append(Total).Append(" (").Append(TotalArea.ToString("0")).Append(" m2)");
            return sb.ToString();
        }
    }
}
