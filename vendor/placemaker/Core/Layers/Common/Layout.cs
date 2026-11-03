// Layout size (FEEDBACK-4, docs/proposals/size.md).
//
// One number — SiteContext.LayoutScale — shrinks (or grows) every layout DISTANCE a generator chooses:
// path loop sizes and boundary clearances, spacing between objects along paths, plaza / feature / place
// footprints, grid pitches, margins. What it never touches: the size of props and nets (assets keep
// their real size), net widths, and layouts with real-world dimensions (parking stalls and aisles,
// roads, regulation sports fields, golf greens and tees). Object-to-object clearances stay footprint
// based (radius + radius), so a smaller layout packs closer but never overlaps more.
//
// Two ways in, one value:
//   * layer code uses LayerContext.Scale / ctx.M(designMetres) / ctx.F(id) (sliders declared with
//     .Layout() are read scaled);
//   * deep static helpers that never see a LayerContext (path planner, path styles, planting) use the
//     ambient Layout.M(...). LayerBase.Plan opens the scope from the input's SiteContext around
//     PlanCore, so it always equals ctx.Scale; [ThreadStatic] keeps parallel plans independent.

using System;
using Unity.Mathematics;

namespace Placemaker.Core.Layers.Common
{
    public static class Layout
    {
        [ThreadStatic] private static float _scale;
        [ThreadStatic] private static float _edge;
        [ThreadStatic] private static bool _set;

        /// <summary>The layout scale of the plan running on this thread (1 outside a plan).</summary>
        public static float Scale => _set ? _scale : 1f;

        /// <summary>Design metres → metres at the current layout scale.</summary>
        public static float M(float designMetres) => designMetres * Scale;

        /// <summary>Design metres → metres, never below a real-size floor (e.g. a footprint).</summary>
        public static float M(float designMetres, float realMin) => math.max(realMin, designMetres * Scale);

        /// <summary>Design area (m²) at the current scale (areas scale with the square).</summary>
        public static float Area(float designM2) => designM2 * Scale * Scale;

        /// <summary>
        /// The area a real area "feels like" at design size: counts chosen by area (loops, gates, cells)
        /// use this so a smaller layout gets more, smaller pieces.
        /// </summary>
        public static float DesignArea(float realM2) => realM2 / (Scale * Scale);

        /// <summary>The one edge margin (real metres): SiteContext.EdgeMargin × scale.</summary>
        public static float EdgeMargin => _set ? _edge : SiteContext.DefaultEdgeMargin;

        /// <summary>Opens the scope for <paramref name="site"/> on this thread until disposed (nests).</summary>
        public static IDisposable Use(SiteContext site)
        {
            float s = site?.Scale ?? 1f;
            float e = math.max(0f, site?.EdgeMargin ?? SiteContext.DefaultEdgeMargin) * s;
            return Use(s, e);
        }

        public static IDisposable Use(float scale, float edgeMargin)
        {
            var restore = new Restore(_set, _scale, _edge);
            _scale = scale > 0f && !float.IsNaN(scale) ? scale : 1f;
            _edge = math.max(0f, edgeMargin);
            _set = true;
            return restore;
        }

        private sealed class Restore : IDisposable
        {
            private readonly bool _wasSet;
            private readonly float _scale, _edge;
            public Restore(bool set, float scale, float edge) { _wasSet = set; _scale = scale; _edge = edge; }
            public void Dispose() { _set = _wasSet; Layout._scale = _scale; Layout._edge = _edge; }
        }
    }
}
