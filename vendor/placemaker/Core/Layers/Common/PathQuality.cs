// Path quality checks shared by the gallery and the tests (s9: "edge spikes").
//
// Playtest: main paths ran out towards the edge, then a short connector leg ran sideways ALONG the
// outline (or out past it) to reach the nearest entrance. A good plan meets the outline only at an
// entrance, roughly perpendicular, or stays inside. EdgeSpikes counts every stretch of path that
// hugs the outline (within Band m, parallel to the nearest edge) or runs outside it for more than
// MinRun m. Designed perimeter walks (one continuous edge-hugging stretch of PerimeterWalk m or more)
// are exempt.

using System.Collections.Generic;
using Unity.Mathematics;

namespace Placemaker.Core.Layers.Common
{
    public static class PathQuality
    {
        /// <summary>Distance from the outline (m) inside which a parallel path counts as hugging it.</summary>
        public const float Band = 3f;
        /// <summary>Longest hugging stretch (m) that is still fine (a perpendicular approach can graze the band).</summary>
        public const float MinRun = 4f;
        /// <summary>A continuous hugging stretch this long (m) is a designed perimeter walk, not a spike.</summary>
        public const float PerimeterWalk = 30f;
        /// <summary>|cos| between the path and the nearest outline edge above which they are parallel (~25°).</summary>
        public const float ParallelCos = 0.9f;

        public struct Spike
        {
            public float2 From, To;
            public float Length;
            public bool Outside;
        }

        public static bool IsWalk(PlannedNet n) => n != null && (n.Role == NetRole.Pathway || n.Role == NetRole.PathwayWide);

        /// <summary>Edge spikes over every walkable path of a composed plan.</summary>
        public static List<Spike> EdgeSpikes(ComposedPlan plan, float3[] ring)
        {
            var nets = new List<PlannedNet>();
            if (plan?.Layers != null)
                foreach (var l in plan.Layers)
                    if (l?.Nets != null) nets.AddRange(l.Nets);
            return EdgeSpikes(nets, ring);
        }

        /// <summary>Edge spikes over the walkable paths in <paramref name="nets"/> against the outline <paramref name="ring"/>.</summary>
        public static List<Spike> EdgeSpikes(IEnumerable<PlannedNet> nets, float3[] ring)
        {
            var result = new List<Spike>();
            var region = LayerRegion.From(ring);
            if (region == null || nets == null) return result;
            var r = region.Ring;
            var runs = new List<Spike>();
            foreach (var n in nets)
            {
                if (!IsWalk(n)) continue;
                float len = math.distance(n.A.xz, n.B.xz) + math.distance(n.B.xz, n.C.xz) + math.distance(n.C.xz, n.D.xz);
                int steps = math.clamp((int)math.ceil(len / 0.5f), 2, 400);
                float run = 0f; bool outside = false; float2 start = default;
                float2 prev = Composer.Bezier(n.A, n.B, n.C, n.D, 0f).xz;
                for (int i = 1; i <= steps + 1; i++)
                {
                    bool hug = false; float seg = 0f; float2 p = prev;
                    bool outHere = false;
                    if (i <= steps)
                    {
                        p = Composer.Bezier(n.A, n.B, n.C, n.D, i / (float)steps).xz;
                        seg = math.distance(prev, p);
                        var mid = (prev + p) * 0.5f;
                        float best = float.MaxValue; float2 edge = default;
                        for (int k = 0; k < r.Length; k++)
                        {
                            var a = r[k]; var b = r[(k + 1) % r.Length];
                            float d = math.sqrt(SegmentIndex.DistSq(mid, a, b));
                            if (d < best) { best = d; edge = math.normalizesafe(b - a); }
                        }
                        bool inside = region.Contains(mid);
                        outHere = !inside && best > 1f;
                        var dir = seg > 1e-4f ? (p - prev) / seg : float2.zero;
                        hug = outHere || (best < Band && math.abs(math.dot(dir, edge)) > ParallelCos);
                    }
                    if (hug)
                    {
                        if (run <= 0f) start = prev;
                        run += seg; outside |= outHere;
                    }
                    else if (run > 0f)
                    {
                        if (run > 0.25f) runs.Add(new Spike { From = start, To = prev, Length = run, Outside = outside });
                        run = 0f; outside = false;
                    }
                    prev = p;
                }
            }
            // Chain hugging runs that continue across net pieces (a split path, a walk of many courses).
            int count = runs.Count;
            var parent = new int[count];
            for (int i = 0; i < count; i++) parent[i] = i;
            int Find(int i) { while (parent[i] != i) i = parent[i] = parent[parent[i]]; return i; }
            for (int i = 0; i < count; i++)
                for (int j = i + 1; j < count; j++)
                    if (Touch(runs[i], runs[j])) parent[Find(i)] = Find(j);
            var length = new Dictionary<int, float>();
            var outsideAny = new Dictionary<int, bool>();
            for (int i = 0; i < count; i++)
            {
                int root = Find(i);
                length[root] = (length.TryGetValue(root, out var l) ? l : 0f) + runs[i].Length;
                outsideAny[root] = (outsideAny.TryGetValue(root, out var o) && o) || runs[i].Outside;
            }
            foreach (var kv in length)
            {
                if (kv.Value <= MinRun || (!outsideAny[kv.Key] && kv.Value >= PerimeterWalk)) continue;
                // Report the longest piece of the chain (for drawing) with the chain's length.
                Spike best = default;
                for (int i = 0; i < count; i++)
                    if (Find(i) == kv.Key && runs[i].Length > best.Length) best = runs[i];
                best.Length = kv.Value; best.Outside = outsideAny[kv.Key];
                result.Add(best);
            }
            return result;
        }

        private static bool Touch(Spike a, Spike b) =>
            math.distancesq(a.From, b.From) < 1f || math.distancesq(a.From, b.To) < 1f ||
            math.distancesq(a.To, b.From) < 1f || math.distancesq(a.To, b.To) < 1f;
    }
}
