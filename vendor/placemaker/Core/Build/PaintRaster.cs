// Placemaker — terrain texture paint as strength fields and brush stamps (GPL-3.0).
//
// CS2's material brush (TerrainMaterialSystem.ApplyBrush) blits a brush TEXTURE into the user splatmap. The
// PaintSplatmap kernel (s33: DXBC disassembled, docs/TERRAIN-PAINT.md) keeps ONE 4-BIT WEIGHT PER MATERIAL per texel
// (R8 = Extra1 | Extra2 << 4, G8 = Extra3 | Extra4 << 4): PaintDither writes w = max(old, brushAlpha × opacity) for the
// painted material only, quantised floor(w·15 + 0.25|0.75) with a 2×2 checkerboard; EraseDither writes
// w = min(old, 1 − brushAlpha) for the tool's channels (Clear: all four). Materials are independent channels, painting
// is a max (repainting the same value changes nothing), and the terrain shader blends the weights over the procedural
// base. So any per-point strength is one call per square stamp: the plan describes a field (PlannedPaint ring +
// feather/noise/gradient, or a Core/Paint PaintShape), this file rasterises it.
//
//   * One grid per terrain layer, world aligned (origin on a multiple of Align = one splatmap texel, 3.5 m on a
//     14.336 km map, cells 1.75 m = half a texel), so stamps of one layer never overlap and their borders fall
//     between splatmap texels. Paints of the same layer merge by max (as the kernel does).
//   * Square stamps of at most TileCells cells; empty stamps are dropped. If the stamp or cell budget is
//     exceeded the cell doubles (coarser but bounded) — a build never applies more than MaxStamps brushes.
//   * Order: erases first (clear-all, then per-material erases), then paints. Overlapping materials keep both weights;
//     a Knockout paint weakens the OTHER materials under it so a path through a tinted lawn stays clean.
// Pure and deterministic: the game builds the stamps on a worker thread and applies one per frame.

using System;
using System.Collections.Generic;
using System.Threading;
using Placemaker.Core.Layers.Common;
using Unity.Mathematics;

namespace Placemaker.Core.Build
{
    /// <summary>One brush application: a square N×N strength mask (row 0 = min z) covering [Min, Min + Size].</summary>
    public sealed class PaintStamp
    {
        public int Layer;
        /// <summary>(s33) Erase this material (Layer 1..4) instead of painting it; Layer 0 always clears everything.</summary>
        public bool Erase;
        public float2 Min;
        public float Cell;
        public int N;
        public float[] Mask = Array.Empty<float>();
        public float Peak;
        public int Painted;   // cells with strength > 1/255

        public float Size => N * Cell;
        public float2 Center => Min + Size * 0.5f;

        /// <summary>Bilinear strength at a world point (0 outside).</summary>
        public float Sample(float2 p)
        {
            var g = (p - Min) / Cell - 0.5f;
            if (g.x < -0.5f || g.y < -0.5f || g.x > N - 0.5f || g.y > N - 0.5f) return 0f;
            int x0 = (int)math.floor(g.x), y0 = (int)math.floor(g.y);
            float fx = g.x - x0, fy = g.y - y0;
            float V(int x, int y) => x < 0 || y < 0 || x >= N || y >= N ? 0f : Mask[y * N + x];
            return math.lerp(math.lerp(V(x0, y0), V(x0 + 1, y0), fx), math.lerp(V(x0, y0 + 1), V(x0 + 1, y0 + 1), fx), fy);
        }
    }

    public sealed class PaintRasterOptions
    {
        /// <summary>Base cell (m). 1.75 = half a splatmap texel on a vanilla 14.336 km playable area.</summary>
        public float Cell = 1.75f;
        /// <summary>Grid origin multiple (m): one splatmap texel, so stamp borders fall between texels.</summary>
        public float Align = 3.5f;
        /// <summary>Largest stamp side in cells (texture side). 512² = 1 MB RGBA32, a few ms to upload.</summary>
        public int TileCells = 512;
        public int MaxStamps = 24;
        /// <summary>Budget over all layers; more cells → the cell doubles.</summary>
        public long MaxCells = 4L << 20;
        public float MaxCell = 14f;
        public CancellationToken Cancel;
    }

    /// <summary>The strength field of one PlannedPaint (shared by the raster, the gallery and the tests).</summary>
    public static class PaintField
    {
        public const float LegacyEdge = 1.5f;
        public const float DefaultNoiseScale = 8f;

        public static float Smooth01(float x)
        {
            x = math.saturate(x);
            return x * x * (3f - 2f * x);
        }

        /// <summary>Noise sample in [0,1] for the paint at p (0.5 when the paint has no noise).</summary>
        public static float NoiseAt(PlannedPaint p, float2 at) =>
            p.Noise > 0f ? ValueNoise.Clumps(at, p.NoiseScale > 0f ? p.NoiseScale : DefaultNoiseScale, Hashing.Mix(p.Seed, 0x7A1Bu)) : 0.5f;

        /// <summary>Edge factor from the signed distance to the ring (positive inside).</summary>
        public static float Edge(PlannedPaint p, float inside, float noise)
        {
            if (p.Feather <= 0f) return Smooth01((inside + LegacyEdge) / (2f * LegacyEdge));
            if (inside <= 0f) return 0f;
            float pull = p.Noise > 0f ? 2f * p.Noise * noise : 0f;
            return Smooth01((inside - pull) / p.Feather);
        }

        /// <summary>Gradient factor from the distance to <see cref="PlannedPaint.GradientFrom"/> (1 when there is none).</summary>
        public static float Gradient(PlannedPaint p, float lineDistance, float noise)
        {
            if (p.GradientFrom == null || p.GradientFrom.Length == 0) return 1f;
            float d = lineDistance + (p.Noise > 0f ? (noise * 2f - 1f) * p.Noise : 0f);
            float t = Smooth01((d - p.GradientFull) / math.max(0.01f, p.GradientFade));
            return math.lerp(1f, math.saturate(p.GradientMin), t);
        }

        /// <summary>How far outside the ring the field can be non-zero (legacy edge only).</summary>
        public static float OutsideReach(PlannedPaint p) => p.Feather > 0f ? 0f : LegacyEdge;

        /// <summary>Distance inside the ring beyond which the edge factor is 1 (for capped distance queries).</summary>
        public static float InsideReach(PlannedPaint p) => p.Feather > 0f ? p.Feather + 2f * math.max(0f, p.Noise) + 0.01f : LegacyEdge;

        /// <summary>Reference (slow, exact) strength at a point: Strength × edge × gradient (or Strength × Shape).</summary>
        public static float StrengthAt(PlannedPaint p, float2 at)
        {
            if (p?.Ring == null || p.Ring.Length < 3) return 0f;
            if (p.Shape != null) return math.saturate(p.Strength) * p.Shape.Sample(at);
            var ring = new float2[p.Ring.Length];
            for (int i = 0; i < ring.Length; i++) ring[i] = p.Ring[i].xz;
            float d = TerraformDabs.DistanceToBoundary(ring, at);
            float inside = ReservedSpace.PointInPolygon(at, ring) ? d : -d;
            float n = NoiseAt(p, at);
            float e = Edge(p, inside, n);
            if (e <= 0f) return 0f;
            float g = 1f;
            if (p.GradientFrom != null && p.GradientFrom.Length > 0)
            {
                float best = float.MaxValue;
                if (p.GradientFrom.Length == 1) best = math.distance(at, p.GradientFrom[0]);
                for (int i = 0; i + 1 < p.GradientFrom.Length; i++)
                    best = math.min(best, math.sqrt(ReservedSpace.DistSqToSegment(at, p.GradientFrom[i], p.GradientFrom[i + 1])));
                g = Gradient(p, best, n);
            }
            return math.saturate(p.Strength) * e * g;
        }
    }

    public static class PaintRaster
    {
        private const float MinValue = 1f / 255f;

        private sealed class LayerGrid
        {
            public int Layer, Order;
            public bool Erase;
            public float2 BMin, BMax;
            public List<PlannedPaint> Paints = new List<PlannedPaint>();
            public float[] Grid, Knock;
            public float2 Origin;
            public int W, H;
        }

        /// <summary>Support box of one paint (ring, intersected with a bounded shape), padded by what the legacy edge paints outside.</summary>
        internal static bool Support(PlannedPaint p, out float2 mn, out float2 mx)
        {
            mn = float.MaxValue; mx = float.MinValue;
            foreach (var v in p.Ring)
            {
                if (!math.isfinite(v.x) || !math.isfinite(v.z)) return false;
                mn = math.min(mn, v.xz); mx = math.max(mx, v.xz);
            }
            if (p.Shape != null)
            {
                if (p.Shape.Bounded) { mn = math.max(mn, p.Shape.Min); mx = math.min(mx, p.Shape.Max); }
                mn -= 0.5f; mx += 0.5f;
            }
            else
            {
                float reach = PaintField.OutsideReach(p) + 0.5f;
                mn -= reach; mx += reach;
            }
            return mx.x > mn.x && mx.y > mn.y;
        }

        /// <summary>Stamps for every paint, budgeted (see file header). Never null; invalid paints are skipped.</summary>
        public static List<PaintStamp> Build(IReadOnlyList<PlannedPaint> paints, PaintRasterOptions o = null)
        {
            o = o ?? new PaintRasterOptions();
            var result = new List<PaintStamp>();
            if (paints == null || paints.Count == 0) return result;

            // 1. Group by layer; bounds include what the legacy edge paints outside the ring.
            var layers = new List<LayerGrid>();
            for (int i = 0; i < paints.Count; i++)
            {
                var p = paints[i];
                if (p?.Ring == null || p.Ring.Length < 3 || !(p.Strength > 0f)) continue;
                int layer = math.clamp(p.Layer, 0, 4);
                bool erase = layer == 0 || p.Erase;
                if (!Support(p, out var smin, out var smax)) continue;
                LayerGrid g = null;
                foreach (var l in layers) if (l.Layer == layer && l.Erase == erase) { g = l; break; }
                if (g == null)
                {
                    // Erases first (clear-all, then per material), then paints by first appearance.
                    g = new LayerGrid { Layer = layer, Erase = erase, Order = layer == 0 ? -100 : erase ? -50 + layer : layers.Count, BMin = float.MaxValue, BMax = float.MinValue };
                    layers.Add(g);
                }
                g.BMin = math.min(g.BMin, smin); g.BMax = math.max(g.BMax, smax);
                g.Paints.Add(p);
            }
            layers.RemoveAll(l => l.Paints.Count == 0);
            if (layers.Count == 0) return result;
            layers.Sort((a, b) => a.Order.CompareTo(b.Order));

            // 2. Cell size within the budgets.
            float cell = math.max(0.25f, o.Cell);
            int tile = math.max(16, o.TileCells);
            while (true)
            {
                long cells = 0, stamps = 0;
                foreach (var l in layers)
                {
                    Frame(l, cell, o.Align, out _, out int w, out int h);
                    cells += (long)w * h;
                    int t = TileSide(w, h, tile);
                    stamps += (long)((w + t - 1) / t) * ((h + t - 1) / t);
                }
                if ((cells <= o.MaxCells && stamps <= o.MaxStamps) || cell * 2f > o.MaxCell) break;
                cell *= 2f;
            }

            // 3. Rasterise each layer (max-merge); knockout paints also into the layer's knockout grid.
            foreach (var l in layers)
            {
                o.Cancel.ThrowIfCancellationRequested();
                Frame(l, cell, o.Align, out l.Origin, out l.W, out l.H);
                l.Grid = new float[(long)l.W * l.H];
                foreach (var p in l.Paints)
                {
                    Rasterise(p, l.Grid, l.Origin, cell, l.W, l.H, o.Cancel);
                    if (p.Knockout && !l.Erase)
                    {
                        l.Knock = l.Knock ?? new float[(long)l.W * l.H];
                        Rasterise(p, l.Knock, l.Origin, cell, l.W, l.H, o.Cancel);
                    }
                }
            }
            // 4. Knockouts (s33): a paint marked Knockout weakens every OTHER material under it (other × (1 − k)).
            //    Grids share cell and alignment, so another layer's cell is an integer offset away.
            foreach (var l in layers)
            {
                if (l.Erase) continue;
                foreach (var k in layers)
                {
                    if (k == l || k.Erase || k.Knock == null) continue;
                    int dx = (int)math.round((k.Origin.x - l.Origin.x) / cell), dy = (int)math.round((k.Origin.y - l.Origin.y) / cell);
                    for (int y = 0; y < l.H; y++)
                    {
                        int ky = y - dy;
                        if (ky < 0 || ky >= k.H) continue;
                        for (int x = 0; x < l.W; x++)
                        {
                            int kx = x - dx;
                            if (kx < 0 || kx >= k.W) continue;
                            float kv = k.Knock[(long)ky * k.W + kx];
                            if (kv > 0f) l.Grid[(long)y * l.W + x] *= 1f - kv;
                        }
                    }
                }
            }
            // 5. Cut into square stamps.
            foreach (var l in layers)
            {
                var origin = l.Origin; int w = l.W, h = l.H; var grid = l.Grid;
                l.Knock = null;
                int t = TileSide(w, h, tile);
                for (int ty = 0; ty < h; ty += t)
                    for (int tx = 0; tx < w; tx += t)
                    {
                        var s = new PaintStamp { Layer = l.Layer, Erase = l.Erase, Cell = cell, N = t, Min = origin + new float2(tx, ty) * cell, Mask = new float[t * t] };
                        for (int y = 0; y < t && ty + y < h; y++)
                            for (int x = 0; x < t && tx + x < w; x++)
                            {
                                float v = grid[(long)(ty + y) * w + tx + x];
                                if (v < MinValue) continue;
                                s.Mask[y * t + x] = v;
                                s.Peak = math.max(s.Peak, v);
                                s.Painted++;
                            }
                        if (s.Painted > 0) result.Add(s);
                    }
                l.Grid = null;
            }
            return result;
        }

        private static int TileSide(int w, int h, int tile) => math.max(1, math.min(tile, math.max(w, h)));

        private static void Frame(LayerGrid l, float cell, float align, out float2 origin, out int w, out int h)
        {
            float a = math.max(cell, align);
            // Keep the origin on a multiple of both the alignment and the cell.
            a = math.ceil(a / cell) * cell;
            origin = math.floor(l.BMin / a) * a;
            var size = l.BMax - origin;
            w = math.max(1, (int)math.ceil(size.x / cell));
            h = math.max(1, (int)math.ceil(size.y / cell));
        }

        /// <summary>Writes max(existing, strength) for one paint into the grid.</summary>
        public static void Rasterise(PlannedPaint p, float[] grid, float2 origin, float cell, int w, int h, CancellationToken cancel = default)
        {
            if (p.Shape != null) { RasteriseShape(p, grid, origin, cell, w, h, cancel); return; }
            var ring = new float2[p.Ring.Length];
            float2 mn = float.MaxValue, mx = float.MinValue;
            for (int i = 0; i < ring.Length; i++) { ring[i] = p.Ring[i].xz; mn = math.min(mn, ring[i]); mx = math.max(mx, ring[i]); }
            float outside = PaintField.OutsideReach(p), insideCap = PaintField.InsideReach(p);
            var edges = new SegmentIndex(math.clamp(insideCap, 4f, 32f));
            for (int i = 0; i < ring.Length; i++) edges.Add(ring[i], ring[(i + 1) % ring.Length], 0f);
            SegmentIndex line = null;
            float lineCap = 0f;
            if (p.GradientFrom != null && p.GradientFrom.Length > 0)
            {
                lineCap = p.GradientFull + p.GradientFade + 2f * math.max(0f, p.Noise) + 1f;
                line = new SegmentIndex(math.clamp(lineCap * 0.25f, 4f, 32f));
                if (p.GradientFrom.Length == 1) line.Add(p.GradientFrom[0], p.GradientFrom[0], 0f);
                for (int i = 0; i + 1 < p.GradientFrom.Length; i++) line.Add(p.GradientFrom[i], p.GradientFrom[i + 1], 0f);
            }
            float strength = math.saturate(p.Strength);
            int x0 = math.max(0, (int)math.floor((mn.x - outside - origin.x) / cell));
            int x1 = math.min(w - 1, (int)math.ceil((mx.x + outside - origin.x) / cell));
            int y0 = math.max(0, (int)math.floor((mn.y - outside - origin.y) / cell));
            int y1 = math.min(h - 1, (int)math.ceil((mx.y + outside - origin.y) / cell));
            var xs = new List<float>(16);
            for (int y = y0; y <= y1; y++)
            {
                if ((y & 31) == 0) cancel.ThrowIfCancellationRequested();
                float cz = origin.y + (y + 0.5f) * cell;
                // Scanline crossings → inside intervals (even-odd).
                xs.Clear();
                for (int i = 0; i < ring.Length; i++)
                {
                    var a = ring[i]; var b = ring[(i + 1) % ring.Length];
                    if ((a.y > cz) == (b.y > cz)) continue;
                    xs.Add(a.x + (cz - a.y) / (b.y - a.y) * (b.x - a.x));
                }
                xs.Sort();
                int k = 0;
                for (int x = x0; x <= x1; x++)
                {
                    float cx = origin.x + (x + 0.5f) * cell;
                    while (k < xs.Count && xs[k] <= cx) k++;
                    bool inside = (k & 1) == 1;
                    if (!inside && outside <= 0f) continue;
                    var at = new float2(cx, cz);
                    float d = edges.Clearance(at, inside ? insideCap : outside + 0.01f);
                    if (!inside && d >= outside) continue;
                    float n = PaintField.NoiseAt(p, at);
                    float e = PaintField.Edge(p, inside ? d : -d, n);
                    if (e <= 0f) continue;
                    float g = line == null ? 1f : PaintField.Gradient(p, line.Clearance(at, lineCap), n);
                    float v = strength * e * g;
                    long idx = (long)y * w + x;
                    if (v > grid[idx]) grid[idx] = v;
                }
            }
        }

        /// <summary>(s33) A shape paint: Strength × Shape at every cell centre of its support box (max-merge).</summary>
        private static void RasteriseShape(PlannedPaint p, float[] grid, float2 origin, float cell, int w, int h, CancellationToken cancel)
        {
            if (!Support(p, out var mn, out var mx)) return;
            float strength = math.saturate(p.Strength);
            if (!(strength > 0f)) return;
            var shape = p.Shape;
            int x0 = math.max(0, (int)math.floor((mn.x - origin.x) / cell));
            int x1 = math.min(w - 1, (int)math.ceil((mx.x - origin.x) / cell));
            int y0 = math.max(0, (int)math.floor((mn.y - origin.y) / cell));
            int y1 = math.min(h - 1, (int)math.ceil((mx.y - origin.y) / cell));
            for (int y = y0; y <= y1; y++)
            {
                if ((y & 31) == 0) cancel.ThrowIfCancellationRequested();
                float cz = origin.y + (y + 0.5f) * cell;
                for (int x = x0; x <= x1; x++)
                {
                    float v = strength * shape.Sample(new float2(origin.x + (x + 0.5f) * cell, cz));
                    if (!(v > 0f)) continue;
                    long idx = (long)y * w + x;
                    if (v > grid[idx]) grid[idx] = v;
                }
            }
        }

        /// <summary>Area (m²) actually painted (strength above 1/255), for summaries.</summary>
        public static double PaintedArea(IReadOnlyList<PaintStamp> stamps)
        {
            double a = 0;
            if (stamps != null) foreach (var s in stamps) a += s.Painted * (double)s.Cell * s.Cell;
            return a;
        }

        /// <summary>Composite strength of a layer at a point (stamps of one layer never overlap). Erase stamps are ignored.</summary>
        public static float Sample(IReadOnlyList<PaintStamp> stamps, int layer, float2 p)
        {
            float v = 0f;
            foreach (var s in stamps) if (s.Layer == layer && !s.Erase) v = math.max(v, s.Sample(p));
            return v;
        }

        /// <summary>
        /// Coarse preview of the stamps for overlays: axis-aligned rectangles (row runs of equal level) per
        /// layer and strength level 1..levels. Coarsens until at most <paramref name="maxRects"/> remain.
        /// </summary>
        public static List<(int layer, int level, float2[] ring)> PreviewRects(IReadOnlyList<PaintStamp> stamps, int levels = 4, float previewCell = 4f, int maxRects = 4000)
        {
            var result = new List<(int, int, float2[])>();
            if (stamps == null || stamps.Count == 0) return result;
            levels = math.max(1, levels);
            for (int attempt = 0; attempt < 6; attempt++, previewCell *= 2f)
            {
                result.Clear();
                foreach (var s in stamps)
                {
                    int b = math.max(1, (int)math.round(previewCell / s.Cell));
                    int n = (s.N + b - 1) / b;
                    float c = s.Cell * b;
                    for (int y = 0; y < n; y++)
                    {
                        int runStart = -1, runLevel = 0;
                        for (int x = 0; x <= n; x++)
                        {
                            int level = 0;
                            if (x < n)
                            {
                                float sum = 0; int cnt = 0;
                                for (int yy = y * b; yy < math.min(s.N, (y + 1) * b); yy++)
                                    for (int xx = x * b; xx < math.min(s.N, (x + 1) * b); xx++) { sum += s.Mask[yy * s.N + xx]; cnt++; }
                                float avg = cnt == 0 ? 0f : sum / cnt;
                                level = avg < 0.04f ? 0 : math.clamp((int)math.ceil(avg * levels - 0.001f), 1, levels);
                            }
                            if (level == runLevel && x < n) continue;
                            if (runLevel > 0)
                            {
                                var a = s.Min + new float2(runStart, y) * c;
                                var e = s.Min + new float2(x, y + 1) * c;
                                result.Add((s.Erase ? 0 : s.Layer, runLevel, new[] { a, new float2(e.x, a.y), e, new float2(a.x, e.y) }));
                            }
                            runStart = x; runLevel = level;
                        }
                    }
                }
                if (result.Count <= maxRects) break;
            }
            return result;
        }
    }
}
