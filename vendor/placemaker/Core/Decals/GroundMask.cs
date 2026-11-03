// Placemaker — where bare terrain shows inside a fill step, baked into a paint mask (s33). GPL-3.0.
//
// Terrain paint only shows where the terrain itself shows: not under a planned surface (grass included), a net, paving,
// sand, water or a zoning cutout. The s28 wear asked that per sample point; the s33 toolkit paints FIELDS, so the
// question is baked once per fill step into a grid (half a splatmap texel, 1.75 m; coarser on huge areas) by scanline
// filling the covers and stamping the net corridors, then used as a PaintShape (bilinear, so its edges are a cell soft).
// The outline itself is feathered with a little noise, so a painted lawn blends into the land around it. The grid is
// immutable once built (safe for the preview and the build to sample at the same time).

using System;
using System.Collections.Generic;
using System.Threading;
using Placemaker.Core.Geometry;
using Placemaker.Core.Layers.Common;
using Placemaker.Core.Paint;
using Unity.Mathematics;

namespace Placemaker.Core.Decals
{
    internal sealed class GroundMask
    {
        public const float BaseCell = 1.75f;
        public const int MaxCells = 360_000;

        public readonly float2 Origin;
        public readonly float Cell;
        public readonly int W, H;
        public readonly float[] Values;
        public readonly PaintShape Shape;

        private GroundMask(float2 origin, float cell, int w, int h, float[] v)
        {
            Origin = origin; Cell = cell; W = w; H = h; Values = v;
            Shape = PaintShape.Grid(origin, cell, w, h, v);
        }

        /// <summary>Mask value (0..1, bilinear) at a point.</summary>
        public float At(float2 p) => Shape.Sample(p);

        /// <summary>Share of the step's area where bare terrain shows (0..1).</summary>
        public float OpenShare
        {
            get
            {
                double s = 0; int n = 0;
                foreach (var v in Values) { if (v > 0f) n++; s += v; }
                return n == 0 ? 0f : (float)(s / Values.Length);
            }
        }

        public static GroundMask Build(DressingWorld w, LayerRegion region, float edgeFeather, uint seed, CancellationToken cancel = default)
        {
            var ring = region.Ring;
            float2 min = region.Min - 1f, max = region.Max + 1f;
            float cell = BaseCell;
            var size = max - min;
            double cells = (double)(size.x / cell) * (size.y / cell);
            if (cells > MaxCells) cell *= (float)Math.Sqrt(cells / MaxCells);
            int gw = Math.Max(1, (int)Math.Ceiling(size.x / cell)), gh = Math.Max(1, (int)Math.Ceiling(size.y / cell));
            var v = new float[gw * gh];

            // 1. Inside the outline, feathered (noisy) at it.
            var outline = PaintShape.Polygon(ring, math.max(0.5f, edgeFeather), math.max(0f, edgeFeather * 0.35f), 10f, seed);
            for (int y = 0; y < gh; y++)
            {
                if ((y & 31) == 0) cancel.ThrowIfCancellationRequested();
                for (int x = 0; x < gw; x++) v[y * gw + x] = outline.Sample(min + (new float2(x, y) + 0.5f) * cell);
            }
            // 2. Covers: every planned surface, paved / sand / water ground.
            foreach (var c in w.Covers) Fill(v, min, cell, gw, gh, c.Ring, 0f);
            foreach (var p in w.Polys)
                if (p.Kind == GroundKind.Paved || p.Kind == GroundKind.Sand || p.Kind == GroundKind.Water) Fill(v, min, cell, gw, gh, p.Ring, 0f);
            // 3. Net corridors (the net covers the terrain; a verge paints right up to it).
            foreach (var s in w.Segs)
            {
                float r = s.Half + 0.2f;
                var lo = (int2)math.floor((math.min(s.A, s.B) - r - min) / cell);
                var hi = (int2)math.floor((math.max(s.A, s.B) + r - min) / cell);
                lo = math.max(lo, 0); hi = math.min(hi, new int2(gw - 1, gh - 1));
                for (int y = lo.y; y <= hi.y; y++)
                    for (int x = lo.x; x <= hi.x; x++)
                    {
                        var c = min + (new float2(x, y) + 0.5f) * cell;
                        if (ReservedSpace.DistSqToSegment(c, s.A, s.B) < r * r) v[y * gw + x] = 0f;
                    }
            }
            // 4. Zoning cutouts (+1.5 m).
            foreach (var q in w.Cutouts)
            {
                Fill(v, min, cell, gw, gh, q, 0f);
                float2 qmin = q[0], qmax = q[0];
                foreach (var p in q) { qmin = math.min(qmin, p); qmax = math.max(qmax, p); }
                var lo = math.max((int2)math.floor((qmin - 1.5f - min) / cell), 0);
                var hi = math.min((int2)math.floor((qmax + 1.5f - min) / cell), new int2(gw - 1, gh - 1));
                for (int y = lo.y; y <= hi.y; y++)
                    for (int x = lo.x; x <= hi.x; x++)
                    {
                        int i = y * gw + x;
                        if (v[i] <= 0f) continue;
                        if (Poly.DistanceToBoundary(q, min + (new float2(x, y) + 0.5f) * cell) < 1.5f) v[i] = 0f;
                    }
            }
            // 5. Water (real waterline after the plan's terraform), sampled on 2×2 blocks.
            for (int y = 0; y < gh; y += 2)
            {
                if ((y & 31) == 0) cancel.ThrowIfCancellationRequested();
                for (int x = 0; x < gw; x += 2)
                {
                    bool any = false;
                    for (int dy = 0; dy < 2 && y + dy < gh; dy++)
                        for (int dx = 0; dx < 2 && x + dx < gw; dx++) if (v[(y + dy) * gw + x + dx] > 0f) any = true;
                    if (!any) continue;
                    var c = min + (new float2(x, y) + 1f) * cell;
                    if (!w.IsWater(c)) continue;
                    for (int dy = 0; dy < 2 && y + dy < gh; dy++)
                        for (int dx = 0; dx < 2 && x + dx < gw; dx++) v[(y + dy) * gw + x + dx] = 0f;
                }
            }
            return new GroundMask(min, cell, gw, gh, v);
        }

        /// <summary>Scanline: sets every cell whose centre is inside <paramref name="ring"/> to <paramref name="value"/>.</summary>
        internal static void Fill(float[] v, float2 origin, float cell, int w, int h, float2[] ring, float value)
        {
            if (ring == null || ring.Length < 3) return;
            float2 mn = ring[0], mx = ring[0];
            foreach (var p in ring) { mn = math.min(mn, p); mx = math.max(mx, p); }
            int y0 = Math.Max(0, (int)Math.Floor((mn.y - origin.y) / cell)), y1 = Math.Min(h - 1, (int)Math.Ceiling((mx.y - origin.y) / cell));
            if (y1 < y0) return;
            var xs = new List<float>(8);
            for (int y = y0; y <= y1; y++)
            {
                float cz = origin.y + (y + 0.5f) * cell;
                xs.Clear();
                for (int i = 0; i < ring.Length; i++)
                {
                    var a = ring[i]; var b = ring[(i + 1) % ring.Length];
                    if ((a.y > cz) == (b.y > cz)) continue;
                    xs.Add(a.x + (cz - a.y) / (b.y - a.y) * (b.x - a.x));
                }
                xs.Sort();
                for (int k = 0; k + 1 < xs.Count; k += 2)
                {
                    int x0 = Math.Max(0, (int)Math.Ceiling((xs[k] - origin.x) / cell - 0.5f));
                    int x1 = Math.Min(w - 1, (int)Math.Floor((xs[k + 1] - origin.x) / cell - 0.5f));
                    for (int x = x0; x <= x1; x++) v[y * w + x] = value;
                }
            }
        }
    }
}
