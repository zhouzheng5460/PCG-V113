// When the brush (Paint / Block paint) is "live": its cursor circle, Block paint's obstacle shading and hover
// outline are drawn, and a press starts a stroke (s15, lead report: "after painting and switching to Design the
// big brush circle keeps following the mouse"). Pure, unit-tested.

namespace Placemaker.Core.Tool
{
    public static class BrushCursor
    {
        public static bool IsBrushMode(SelectionMode m) => m == SelectionMode.Paint || m == SelectionMode.BlockPaint;

        /// <summary>
        /// True only while the tool is active, in the Area stage, in a brush mode, not placing markers, and the
        /// mouse is over the world (not over the panel or another UI). A stroke already running stays live until
        /// mouse-up so it always finishes cleanly.
        /// </summary>
        public static bool Live(bool toolActive, ToolStage stage, SelectionMode mode, bool placingMarkers, bool overWorld, bool strokeRunning)
        {
            if (!toolActive || !IsBrushMode(mode)) return false;
            if (strokeRunning) return true;
            return stage == ToolStage.Area && !placingMarkers && overWorld;
        }
    }
}
