// Placemaker — the zone blocks CS2 will give a road this build adds (s26).
//
// Playtest s26: plots facing a lane the build adds (Shopping plaza, Commercial types) were drawn as floating quads and
// never zoned — the lane's zone blocks do not exist at design time, ZoneGuard had no cells to snap to, the blockers
// kept clear of the planned quad only and the real cells (aligned to the lane's edge) straddled them. This is a port
// of Game.Zones.BlockSystem.CreateBlocks (decompiled 1.6.2f1) for one edge without neighbours: the curve is cut into
// pieces by curvature, each piece's chord gets whole 8 m cells centred on it, front edge on the road side offset by
// the road width rounded up to cells (ZoneUtils.GetCellWidth), 6 rows deep, split into blocks of ≤ 10 cells. Ends
// that continue straight into another edge (FindContinuousEdge) and half-aligned ends are not modelled: such ends are
// extended like free ends. After the build's Apply the game layer re-reads the live blocks (ZoneGuard.Refresh), so
// the prediction only has to be right for the preview and the plan.

using System;
using System.Collections.Generic;
using Unity.Mathematics;

namespace Placemaker.Core.Build
{
    public static class ZoneBlockPredictor
    {
        /// <summary>ZoneUtils.GetCellWidth.</summary>
        public static int CellWidth(float roadWidth) => (int)math.ceil(roadWidth / 8f - 0.01f);

        /// <summary>Road roles whose prefabs carry zone blocks (RoadFlags.EnableZoning on vanilla roads, alleys included).</summary>
        public static bool Zones(NetRole role)
        {
            switch (role)
            {
                case NetRole.Street: case NetRole.Avenue: case NetRole.CulDeSac: case NetRole.Roundabout: case NetRole.PerimeterRoad: case NetRole.Alley:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>Both sides' blocks of a road edge (A→D cubic Bézier, XZ) of <paramref name="width"/> metres.</summary>
        public static List<ZoneGrid> Blocks(float3 A, float3 B, float3 C, float3 D, float width)
        {
            var r = new List<ZoneGrid>();
            if (!(width > 0f)) return r;
            int cw = CellWidth(width);
            var c = new[] { A.xz, B.xz, C.xz, D.xz };
            Side(c, cw, r);
            Side(new[] { D.xz, C.xz, B.xz, A.xz }, cw, r);
            return r;
        }

        private static float2 Right(float2 v) => new float2(v.y, -v.x);

        private static float2 Pos(float2[] c, float t)
        {
            float u = 1f - t;
            return u * u * u * c[0] + 3f * u * u * t * c[1] + 3f * u * t * t * c[2] + t * t * t * c[3];
        }

        private static float2 Tangent(float2[] c, float t)
        {
            float u = 1f - t;
            var d = 3f * u * u * (c[1] - c[0]) + 6f * u * t * (c[2] - c[1]) + 3f * t * t * (c[3] - c[2]);
            if (math.lengthsq(d) < 1e-10f) d = c[3] - c[0];
            return d;
        }

        /// <summary>A point of the curve offset to the right by <paramref name="d"/> (exact per sample, like NetUtils.OffsetCurveLeftSmooth(−d)).</summary>
        private static float2 Off(float2[] c, float t, float d)
        {
            var tan = math.normalizesafe(Tangent(c, t));
            return Pos(c, t) + Right(tan) * d;
        }

        private static float OffLength(float2[] c, float d, float t0 = 0f, float t1 = 1f)
        {
            float len = 0f;
            var prev = Off(c, t0, d);
            for (int i = 1; i <= 32; i++)
            {
                var p = Off(c, math.lerp(t0, t1, i / 32f), d);
                len += math.distance(prev, p);
                prev = p;
            }
            return len;
        }

        private static void Side(float2[] curve, int cellWidth, List<ZoneGrid> into)
        {
            float2 v0 = math.normalizesafe(Tangent(curve, 0f)), v1 = math.normalizesafe(Tangent(curve, 1f));
            if (math.lengthsq(v0) < 0.5f || math.lengthsq(v1) < 0.5f) return;
            float num2 = cellWidth * 4f;
            float num3 = OffLength(curve, num2);
            if (num3 < 8f) return;
            float num4 = math.degrees(math.acos(math.clamp(math.dot(v0, v1), -1f, 1f)) * 8f / num3);
            float num5 = 2f / math.sqrt(math.clamp(num4 / 15f, 0.0001f, 1f)) * 8f;
            int pieces = math.max(1, (int)(num3 / num5));
            for (int i = 0; i < pieces; i++)
            {
                float t0 = i / (float)pieces, t1 = (i + 1) / (float)pieces;
                float2 value = math.normalizesafe(Tangent(curve, t0)), value2 = math.normalizesafe(Tangent(curve, t1));
                float2 a = Off(curve, t0, num2), d = Off(curve, t1, num2);
                float2 value3 = d - a;
                if (math.lengthsq(value3) < 1e-6f) continue;
                value3 = math.normalize(value3);
                // Free ends (no continuous neighbour edge): extended by half the road width minus a cell.
                float ext = num2 - (cellWidth > 1 ? 8f : 0f);
                if (i == 0) a -= value3 * ext;
                if (i == pieces - 1) d += value3 * ext;
                float2 float6 = Right(value), float7 = Right(value2), float8 = Right(value3);
                float num9 = MaxDot(curve, num2, t0, t1, float8) - math.dot(a, float8);
                float upper = math.distance(a, d);
                float d6 = math.dot(float6, float8), d7 = math.dot(float7, float8);
                if (math.abs(d6) > 1e-4f) a += float6 * math.clamp(num9 / d6, 0f, upper);
                if (math.abs(d7) > 1e-4f) d += float7 * math.clamp(num9 / d7, 0f, upper);
                int cells = (int)math.floor((math.length(d - a) + 0.1f) / 8f);
                if (cells < 2) continue;
                float2 mid = math.lerp(a, d, 0.5f);
                a = mid - value3 * (cells * 4f);
                num9 = MaxDot(curve, num2, t0, t1, float8) - math.dot(a, float8);
                a += float8 * (num9 + 24f);
                int chunks = (cells + 10 - 1) / 10;
                for (int j = 0; j < chunks; j++)
                {
                    int n12 = j * cells / chunks, n13 = (j + 1) * cells / chunks;
                    into.Add(new ZoneGrid { Position = a + value3 * ((n12 + n13) * 4f), Direction = -float8, Size = new int2(n13 - n12, 6) });
                }
            }
        }

        private static float MaxDot(float2[] c, float off, float t0, float t1, float2 dir)
        {
            float m = float.MinValue;
            for (int i = 0; i <= 16; i++) m = math.max(m, math.dot(Off(c, math.lerp(t0, t1, i / 16f), off), dir));
            return m;
        }
    }
}
