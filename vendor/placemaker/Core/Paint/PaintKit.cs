// Placemaker — terrain paint toolkit: emit PaintShapes as PlannedPaints (s33, docs/TERRAIN-PAINT.md). GPL-3.0.
//
// One PaintKit per layer (or dressing group): it knows where paints go (an AreaPlan's Paint list), the ground height,
// a seed, an optional MASK (where bare terrain shows: off paths, surfaces, water, cutouts — paint under a surface is
// invisible and only costs permanence) and a paint budget. Every emitter returns the PlannedPaint (null when the
// budget is spent or the shape is empty), so callers can tune it further (Knockout…).
//
//   Fill        polygon, feathered + noisy edge                  ("paint the whole thing", "half-texture" at 0.5)
//   Stroke      polyline: width, feather, start→end strength, faded ends, wobble   (painted trails, verges, ruts)
//   Radial      sphere / disc gradient with a falloff curve      (mulch under a tree, worn ground round a bench)
//   Gradient    linear gradient across a polygon                 (lawn drying toward the sunny edge, beach → dune)
//   Blend       two materials A → B along a gradient shape       (sand fading into dry grass, dirt into lush)
//   Speckle     noise patches covering a share of a polygon      (patchy lawn, gravelly dirt)
//   Erase       lower one material (or all: layer 0) back toward the procedural base
//   Shape       any composed PaintShape
// Strength is the material weight (0..1, stored as 4 bits: steps of 1/15). Deterministic.

using System;
using System.Collections.Generic;
using Placemaker.Core.Geometry;
using Placemaker.Core.Layers.Common;
using Unity.Mathematics;

namespace Placemaker.Core.Paint
{
    public sealed class PaintKit
    {
        private readonly List<PlannedPaint> _into;
        private readonly Func<float2, float> _height;
        private readonly uint _seed;
        private int _serial;

        /// <summary>Applied to every paint (×): where the terrain actually shows. Null = everywhere.</summary>
        public PaintShape Mask;
        /// <summary>Default outline of emitted paints (the area): their ring never reaches past it. Null = the shape's box.</summary>
        public IReadOnlyList<float2> Outline;
        /// <summary>Most paints this kit emits; later requests are counted in <see cref="Dropped"/>.</summary>
        public int MaxPaints = 400;
        /// <summary>Multiplies every strength (the "Ground paint" amount × style).</summary>
        public float Amount = 1f;
        /// <summary>Strengths are clamped to this (faint dressing never paints solid).</summary>
        public float MaxStrength = 1f;
        public int Count, Dropped;
        /// <summary>Fields that turned out empty (fully masked) and were not emitted.</summary>
        public int Skipped;

        /// <summary>
        /// Area (m²) where <paramref name="shape"/> exceeds <paramref name="threshold"/>, sampled on a grid of at most 64² over the
        /// support box ∩ the outline's box (step ≥ 1.75 m, half a splatmap texel). 0 = paints nothing.
        /// </summary>
        public static float Coverage(PaintShape shape, IReadOnlyList<float2> outline, float threshold)
        {
            float2 mn = shape.Min, mx = shape.Max;
            if (outline != null && outline.Count >= 3)
            {
                float2 omn = outline[0], omx = outline[0];
                for (int i = 1; i < outline.Count; i++) { omn = math.min(omn, outline[i]); omx = math.max(omx, outline[i]); }
                mn = math.max(mn, omn); mx = math.min(mx, omx);
            }
            if (!math.all(math.isfinite(mn)) || !math.all(math.isfinite(mx)) || mx.x <= mn.x || mx.y <= mn.y) return 0f;
            float step = math.max(1.75f, math.cmax(mx - mn) / 64f);
            int nx = math.max(1, (int)math.ceil((mx.x - mn.x) / step)), ny = math.max(1, (int)math.ceil((mx.y - mn.y) / step));
            int hits = 0;
            for (int y = 0; y < ny; y++)
                for (int x = 0; x < nx; x++)
                    if (shape.Sample(mn + (new float2(x, y) + 0.5f) * step) > threshold) hits++;
            return hits * step * step;
        }

        public PaintKit(List<PlannedPaint> into, Func<float2, float> height, uint seed)
        {
            _into = into ?? throw new ArgumentNullException(nameof(into));
            _height = height;
            _seed = seed == 0 ? 0x9E3779B9u : seed;
        }

        /// <summary>A fresh deterministic seed for the next primitive.</summary>
        public uint NextSeed() => Hashing.Mix(_seed, (uint)(++_serial));

        // ───────────────────────── emitters ─────────────────────────

        /// <summary>Any shape. Ring = its support box (or <paramref name="outline"/> when given).</summary>
        public PlannedPaint Shape(PaintShape shape, int layer, float strength, IReadOnlyList<float2> outline = null, bool knockout = false)
        {
            if (shape == null) return null;
            if (Mask != null) shape = shape.Mul(Mask);
            if ((outline == null || outline.Count < 3) && Outline != null && Outline.Count >= 3)
            {
                outline = Outline;
                shape = shape.Mul(PaintShape.Polygon(Outline, 0.3f));
            }

            if (!shape.Bounded && (outline == null || outline.Count < 3)) return null;
            if (shape.Empty) return null;
            float s = math.min(MaxStrength, math.saturate(strength) * math.max(0f, Amount));
            if (!(s > 1f / 30f)) return null;   // below the kernel's quantisation (4 bits + dither): paints nothing
            if (Count >= MaxPaints) { Dropped++; return null; }
            // The ring is bookkeeping (bounds, permanent area, cutting): kept a hair inside the outline it came from.
            float2[] ring = outline != null && outline.Count >= 3 ? Pull(Copy(outline), 0.05f) : Box(shape.Min, shape.Max);
            // Probe the field: a masked-out field paints nothing and must not count as a (permanent) paint.
            float area = Coverage(shape, ring, 1f / (30f * s));
            if (area <= 0f) { Skipped++; return null; }
            var p = new PlannedPaint
            {
                Ring = Poly.To3D(ring, Height), Layer = math.clamp(layer, 0, 4), Strength = s, Shape = shape, Seed = NextSeed(), Knockout = knockout,
                AreaEstimate = area,
            };
            _into.Add(p);
            Count++;
            return p;
        }

        /// <summary>Paint a whole polygon: full <paramref name="feather"/> m inside the outline, <paramref name="noise"/> m of natural edge.</summary>
        public PlannedPaint Fill(IReadOnlyList<float2> ring, int layer, float strength, float feather = 2f, float noise = 0.8f, float noiseScale = 0f, bool knockout = false)
        {
            if (ring == null || ring.Count < 3) return null;
            return Shape(PaintShape.Polygon(ring, feather, noise, noiseScale, NextSeed()), layer, strength, ring, knockout);
        }

        /// <summary>A painted line (trail, verge, rut). See <see cref="PaintShape.Stroke"/>.</summary>
        public PlannedPaint Stroke(IReadOnlyList<float2> line, int layer, float strength, float width, float feather, float start = 1f, float end = 1f,
            float endFade = 0f, float wobble = 0f, float wobbleScale = 0f, Falloff along = Falloff.Linear, bool knockout = false)
        {
            if (line == null || line.Count < 1 || !(width > 0f)) return null;
            return Shape(PaintShape.Stroke(line, width, feather, start, end, along, endFade, wobble, wobbleScale, NextSeed()), layer, strength, null, knockout);
        }

        /// <summary>Radial ("sphere") gradient round a point. Outline = a 12-gon of the outer radius.</summary>
        public PlannedPaint Radial(float2 centre, int layer, float strength, float inner, float outer, Falloff falloff = Falloff.Smooth,
            float noise = 0f, float noiseScale = 0f, float aspect = 1f, float angle = 0f, bool knockout = false)
        {
            if (!(outer > 0f)) return null;
            var shape = PaintShape.Radial(centre, inner, outer, falloff, noise, noiseScale, NextSeed(), aspect, angle);
            return Shape(shape, layer, strength, Circle(centre, (outer * math.max(1f, aspect) + noise) / math.cos(math.PI / 12f), 12), knockout);
        }

        /// <summary>Linear gradient across a polygon: <paramref name="startValue"/> at <paramref name="from"/> → <paramref name="endValue"/> at <paramref name="to"/>.</summary>
        public PlannedPaint Gradient(IReadOnlyList<float2> ring, int layer, float strength, float2 from, float2 to, float startValue = 1f, float endValue = 0f,
            Falloff falloff = Falloff.Smooth, float feather = 2f, float noise = 0.8f)
        {
            if (ring == null || ring.Count < 3) return null;
            var shape = PaintShape.Polygon(ring, feather, noise, 0f, NextSeed()).Mul(PaintShape.Linear(from, to, falloff, startValue, endValue));
            return Shape(shape, layer, strength, ring);
        }

        /// <summary>
        /// Two materials over one area, A where <paramref name="t"/> is 0 and B where it is 1 (complementary weights, so the
        /// sum stays <paramref name="strength"/>). Returns the B paint (A is emitted first).
        /// </summary>
        public PlannedPaint Blend(PaintShape area, PaintShape t, int layerA, int layerB, float strength, IReadOnlyList<float2> outline = null)
        {
            if (area == null || t == null) return null;
            PaintShape.Split(area, t, out var a, out var b);
            Shape(a, layerA, strength, outline);
            return Shape(b, layerB, strength, outline);
        }

        /// <summary>Patches covering about <paramref name="coverage"/> of a polygon (size ~<paramref name="scale"/> m).</summary>
        public PlannedPaint Speckle(IReadOnlyList<float2> ring, int layer, float strength, float scale, float coverage, float softness = 0.3f, float feather = 2f)
        {
            if (ring == null || ring.Count < 3) return null;
            var shape = PaintShape.Polygon(ring, feather, 0f).Mul(PaintShape.Noise(scale, coverage, softness, NextSeed()));
            return Shape(shape, layer, strength, ring);
        }

        /// <summary>
        /// Lower <paramref name="layer"/> (1..4; 0 = every material) to at most 1 − strength inside the polygon: back toward the
        /// procedural base. Also wipes the PLAYER's earlier paint there — callers offer it as an explicit option.
        /// </summary>
        public PlannedPaint Erase(IReadOnlyList<float2> ring, int layer, float strength = 1f, float feather = 2f)
        {
            if (ring == null || ring.Count < 3) return null;
            float keepAmount = Amount; Amount = 1f;
            var mask = Mask; Mask = null;   // an erase is not limited to bare terrain (nothing shows there anyway)
            var p = Shape(PaintShape.Polygon(ring, feather, 0f), layer, strength, ring);
            Mask = mask; Amount = keepAmount;
            if (p != null) p.Erase = true;
            return p;
        }

        // ───────────────────────── helpers ─────────────────────────

        private float Height(float2 p)
        {
            if (_height == null) return 0f;
            try { return _height(p); } catch (Exception) { return 0f; }
        }

        private static float2[] Copy(IReadOnlyList<float2> r)
        {
            var a = new float2[r.Count];
            for (int i = 0; i < a.Length; i++) a[i] = r[i];
            return a;
        }

        public static float2[] Box(float2 min, float2 max) => new[] { min, new float2(max.x, min.y), max, new float2(min.x, max.y) };

        /// <summary>Moves every vertex <paramref name="d"/> m toward the centroid (keeps the ring strictly inside its outline).</summary>
        private static float2[] Pull(float2[] r, float d)
        {
            float2 c = float2.zero;
            foreach (var v in r) c += v;
            c /= r.Length;
            for (int i = 0; i < r.Length; i++) r[i] += math.normalizesafe(c - r[i]) * math.min(d, math.distance(c, r[i]) * 0.5f);
            return r;
        }


        public static float2[] Circle(float2 c, float r, int n = 12)
        {
            var ring = new float2[n];
            for (int i = 0; i < n; i++) { float a = i * math.PI * 2f / n; ring[i] = c + new float2(math.cos(a), math.sin(a)) * r; }
            return ring;
        }
    }
}
