// Game runner: the small pure helpers it needs (frame-time stats for the step log, the shot settle test).

using System.Collections.Generic;
using Unity.Mathematics;

namespace Placemaker.Core.Suite
{
    public static class SuiteStats
    {
        /// <summary>Max and 95th percentile of frame times in milliseconds.</summary>
        public static (float Max, float P95, int Count) FrameStats(List<float> ms)
        {
            if (ms == null || ms.Count == 0) return (0f, 0f, 0);
            var copy = new List<float>(ms);
            copy.Sort();
            int idx = (int)math.clamp(math.ceil(copy.Count * 0.95f) - 1, 0, copy.Count - 1);
            return (copy[copy.Count - 1], copy[idx], copy.Count);
        }

        /// <summary>A pair of captures counts as settled when few pixels changed and no tile changed much.</summary>
        public static bool PairSettled(int changed16, float tileMax, int changedLimit, float tileLimit) => changed16 <= changedLimit && tileMax <= tileLimit;
    }
}
