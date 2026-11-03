// When to re-plan after an edit, and a coarse progress for the "planning…" spinner. Pure, unit-tested.
//
// - Committed edits (toggle a feature, pick a preset, click an asset, finish a drag) re-plan at once.
// - A slider/outline drag re-plans debounced (or only when it ends with the Simple preview detail).
// - A hovered asset re-plans after a short debounce (the player moves along the picker).
// - Progress: 0 while queued, then elapsed / expected (the last plan's duration), capped below 1;
//   the plan being published is what ends "planning", never the estimate.

using System;

namespace Placemaker.Core.Tool
{
    public static class PlanSchedule
    {
        public const float DragDebounce = 0.15f;
        public const float DragDebounceDetailed = 0.06f;
        public const float HoverDebounce = 0.06f;

        /// <summary>Seconds to wait after the last input change before planning; negative = wait until the drag ends.</summary>
        public static float Debounce(bool gesture, ValuePreviewKind preview, bool simpleDetail, bool detailedDetail)
        {
            bool dragging = gesture || preview == ValuePreviewKind.Drag;
            if (dragging) return simpleDetail ? -1f : detailedDetail ? DragDebounceDetailed : DragDebounce;
            if (preview == ValuePreviewKind.Hover) return HoverDebounce;
            return 0f;
        }

        /// <summary>
        /// Coarse progress 0..0.95 for the spinner. <paramref name="runningMs"/> &lt; 0 = queued (not started);
        /// <paramref name="expectedMs"/> = how long the previous plan took (≤ 0 = unknown).
        /// </summary>
        public static double Progress(double runningMs, double expectedMs)
        {
            if (runningMs < 0 || double.IsNaN(runningMs)) return 0;
            double expected = expectedMs > 0 && !double.IsInfinity(expectedMs) ? Math.Max(expectedMs, 50) : 500;
            double p = 0.05 + 0.9 * (1 - Math.Exp(-runningMs / expected));
            return Math.Min(0.95, Math.Round(p, 2));
        }
    }
}
