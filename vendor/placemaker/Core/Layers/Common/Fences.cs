// Boundary fence runs with gaps at entrances. Ported from ParkManager (tomkpunkt, GPL-3.0),
// Geometry/ParkDecorationPlanner.cs (SampleFence / InsetFenceBoundary / CutFenceGap).
// CS2 fences are nets, so each run becomes a NetRole.Fence course; shared corners are inset
// together (offsetting edge centres separately would split adjacent fence nodes).

using System.Collections.Generic;
using Unity.Mathematics;

namespace Placemaker.Core.Layers.Common
{
    public static class Fences
    {
        public const float Inset = 0.2f;
        /// <summary>Longest single fence course; longer runs are split so the net tool never rejects a segment.</summary>
        public const float MaxCourse = 60f;

        public static List<float2x2> Runs(LayerRegion region, IReadOnlyList<Gate> gates, float gapHalfWidth)
        {
            var runs = new List<float2x2>();
            var inset = region.InsetVertices(Inset, 3f);
            int n = region.Ring.Length;
            for (int e = 0; e < n; e++)
            {
                var a = inset[e]; var b = inset[(e + 1) % n];
                float len = math.distance(a, b);
                if (len < 1f) continue;
                var t = (b - a) / len;
                var spans = new List<float2> { new float2(0, len) };
                if (gates != null)
                    foreach (var g in gates)
                    {
                        float proj = math.dot(g.Position - a, t);
                        var closest = a + t * math.clamp(proj, 0f, len);
                        if (math.distancesq(g.Position, closest) > 4f) continue;
                        Cut(spans, proj - gapHalfWidth, proj + gapHalfWidth, len);
                    }
                spans.Sort((x, y) => x.x.CompareTo(y.x));
                foreach (var sp in spans)
                {
                    float runLen = sp.y - sp.x;
                    if (runLen < 0.5f) continue;
                    int pieces = math.max(1, (int)math.ceil(runLen / MaxCourse));
                    for (int k = 0; k < pieces; k++)
                    {
                        float f0 = sp.x + runLen * k / pieces, f1 = sp.x + runLen * (k + 1) / pieces;
                        // Corners must be bit-identical: CS2 merges course nodes only at EQUAL positions
                        // (GenerateNodesSystem NodeKey), so a + t·len ≠ b would leave every corner open.
                        var p0 = k == 0 && sp.x <= 0f ? a : a + t * f0;
                        var p1 = k == pieces - 1 && sp.y >= len ? b : a + t * f1;
                        runs.Add(new float2x2(p0, p1));
                    }
                }
            }
            return runs;
        }

        /// <summary>Closed fence around a circle (playground, campfire ring), with one gap facing <paramref name="gapDirection"/>.</summary>
        public static List<float2x2> Ring(float2 centre, float radius, int sides, float2 gapDirection, float gapWidth)
        {
            var runs = new List<float2x2>();
            float gapAngle = math.atan2(gapDirection.y, gapDirection.x);
            float halfGap = gapWidth / math.max(1f, radius) * 0.5f;
            for (int i = 0; i < sides; i++)
            {
                float a0 = gapAngle + halfGap + i * (2f * math.PI - 2f * halfGap) / sides;
                float a1 = gapAngle + halfGap + (i + 1) * (2f * math.PI - 2f * halfGap) / sides;
                runs.Add(new float2x2(centre + new float2(math.cos(a0), math.sin(a0)) * radius, centre + new float2(math.cos(a1), math.sin(a1)) * radius));
            }
            return runs;
        }

        /// <summary>
        /// Prop-fence fallback: pieces needed to cover a course of <paramref name="length"/> with no gap
        /// (pieces may overlap slightly; the first and last end exactly at the course ends = the corners).
        /// </summary>
        public static int PieceCount(float length, float piece)
        {
            if (!(length > 0f) || !(piece > 0f)) return 0;
            return math.max(1, (int)math.ceil(length / piece - 0.05f));
        }

        /// <summary>
        /// FEEDBACK-4: pieces that fit a course WITHOUT overlapping (the game hides/cancels one of every two
        /// overlapping props). Each piece sits centred in an equal cell of length/count ≥ piece + gap, so
        /// small, even gaps replace the overlaps. Used by the build for prop fences and hedge pieces.
        /// </summary>
        public static int SpacedPieceCount(float length, float piece, float gap = Placemaker.Core.Build.Declutter.Gap)
        {
            if (!(length > 0f) || !(piece > 0f)) return 0;
            return math.max(1, (int)math.floor(length / (piece + gap) + 1e-4f));
        }

        private static void Cut(List<float2> spans, float g0, float g1, float len)
        {
            g0 = math.clamp(g0, 0f, len); g1 = math.clamp(g1, 0f, len);
            if (g1 <= g0) return;
            for (int i = spans.Count - 1; i >= 0; i--)
            {
                var r = spans[i];
                if (g1 <= r.x || g0 >= r.y) continue;
                spans.RemoveAt(i);
                if (g0 - r.x >= 0.5f) spans.Add(new float2(r.x, g0));
                if (r.y - g1 >= 0.5f) spans.Add(new float2(g1, r.y));
            }
        }
    }
}
