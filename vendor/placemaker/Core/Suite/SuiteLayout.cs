// Game runner (docs/teardown/10-ingame-suite.md): the fixture's geometry, pure so it is unit-tested offline.
//
// The suite starts a new game on a vanilla map (the map-less "default world" is incomplete and crashes natively as
// soon as roads appear: runs boot2-5, e1, e3), reshapes the fixture area in place through one heightmap replacement
// (flat at the area's median ground, a slope, rough blocks, easing back into the map's terrain), then lays a grid of
// vanilla roads. Every block of the grid hosts one scenario. Block kinds:
//   Zoned   row 1: residential zoning on the south street side, grown before the scenarios run
//   Survive row 2, column 4: plopped buildings that every build there must leave standing
//   Rough   row NZ-3 (4 in the default 9 x 7 grid), columns 0-2: ±4 m hills inside the block (the roads stay level)
//   Slope   the last two rows: the ground climbs 6 % to the north (z), roads included
//   Flat    everything else
// Plots (SuitePlot) are test land made to order for one build each, in a row east of the grid.
// Street types vary per grid line (4-lane avenue, 2-lane street, pedestrian street) so blocks face mixed roads.

using System;
using System.Collections.Generic;
using Unity.Mathematics;

namespace Placemaker.Core.Suite
{
    public enum SuiteBlockKind { Flat = 0, Zoned = 1, Slope = 2, Rough = 3, Survive = 4, Plot = 5 }

    public sealed class SuiteRoadSegment
    {
        public string Prefab = "";
        public float2 A, B;
        /// <summary>True for lines running along z (constant x).</summary>
        public bool Vertical;
        public int Line;
    }

    public sealed class SuiteBlock
    {
        public int I, J;
        public SuiteBlockKind Kind;
        /// <summary>Road centre lines around the block (west, east, south, north).</summary>
        public float X0, X1, Z0, Z1;
        public string West = "", East = "", South = "", North = "";
        /// <summary>A plot's name (P1, P2...); grid blocks are named by their position.</summary>
        public string Name;
        public string Id => Name ?? ("B" + I + "-" + J);
        public float2 Center => new float2((X0 + X1) * 0.5f, (Z0 + Z1) * 0.5f);

        /// <summary>The land inside the kerbs, given the half widths of the four roads (+ a margin).</summary>
        public float2[] Interior(Func<string, float> halfWidth, float margin = 0f)
        {
            float w = halfWidth(West) + margin, e = halfWidth(East) + margin, s = halfWidth(South) + margin, n = halfWidth(North) + margin;
            return new[] { new float2(X0 + w, Z0 + s), new float2(X1 - e, Z0 + s), new float2(X1 - e, Z1 - n), new float2(X0 + w, Z1 - n) };
        }
    }

    public sealed partial class SuiteLayout
    {
        public const string SmallRoad = "Small Road";
        public const string MediumRoad = "Medium Road";
        public const string PedestrianStreet = "Pedestrian Street";

        /// <summary>TerrainSystem's default heightmap value (8191) on the default 4096 m height scale.</summary>
        public const float DefaultHeight = 8191f / 65535f * 4096f;
        /// <summary>Default Base when no map says otherwise (offline tests): 30 m above the default world's sea level.</summary>
        public const float BaseHeight = DefaultHeight + 30f;
        public const float HeightScale = 4096f;
        public const int HeightmapSize = 4096;
        public const float MapSize = 14336f;
        public const float SlopeGrade = 0.06f;
        public const float RoughAmplitude = 4f;

        public int NX = 9, NZ = 7;
        public float X0 = -700f, Z0 = -500f, PX = 150f, PZ = 140f;

        public float LineX(int i) => X0 + i * PX;
        public float LineZ(int j) => Z0 + j * PZ;

        /// <summary>Street along x = LineX(i).</summary>
        public string VerticalRoad(int i) => i % 4 == 0 ? MediumRoad : i % 4 == 3 ? PedestrianStreet : SmallRoad;
        /// <summary>Street along z = LineZ(j).</summary>
        public string HorizontalRoad(int j) => j % 3 == 0 ? MediumRoad : SmallRoad;

        public SuiteBlockKind Kind(int i, int j)
        {
            if (j >= NZ - 2) return SuiteBlockKind.Slope;
            if (j == RoughRow && i <= 2) return SuiteBlockKind.Rough;
            if (j == 2 && i == 4) return SuiteBlockKind.Survive;
            if (j == 1) return SuiteBlockKind.Zoned;
            return SuiteBlockKind.Flat;
        }

        public List<SuiteBlock> Blocks()
        {
            var r = new List<SuiteBlock>(NX * NZ);
            for (int j = 0; j < NZ; j++)
                for (int i = 0; i < NX; i++)
                    r.Add(new SuiteBlock
                    {
                        I = i, J = j, Kind = Kind(i, j),
                        X0 = LineX(i), X1 = LineX(i + 1), Z0 = LineZ(j), Z1 = LineZ(j + 1),
                        West = VerticalRoad(i), East = VerticalRoad(i + 1), South = HorizontalRoad(j), North = HorizontalRoad(j + 1),
                    });
            return r;
        }

        /// <summary>One segment per block side, junction to junction (shared ends merge into nodes).</summary>
        public List<SuiteRoadSegment> Roads()
        {
            var r = new List<SuiteRoadSegment>();
            for (int i = 0; i <= NX; i++)
                for (int j = 0; j < NZ; j++)
                    r.Add(new SuiteRoadSegment { Prefab = VerticalRoad(i), Vertical = true, Line = i, A = new float2(LineX(i), LineZ(j)), B = new float2(LineX(i), LineZ(j + 1)) });
            for (int j = 0; j <= NZ; j++)
                for (int i = 0; i < NX; i++)
                    r.Add(new SuiteRoadSegment { Prefab = HorizontalRoad(j), Vertical = false, Line = j, A = new float2(LineX(i), LineZ(j)), B = new float2(LineX(i + 1), LineZ(j)) });
            foreach (var p in Plots) r.AddRange(p.RingRoads());
            return r;
        }

        // ───────────── terrain ─────────────
        // The map's own terrain stays outside the fixture: inside FixtureMin..FixtureMax the ground is the layout
        // (Base + slope + rough), and over the next Blend metres it eases back into the map's heights.

        /// <summary>Ground level of the flat blocks (set from the live map: the median ground of the fixture area).</summary>
        public float Base = BaseHeight;
        /// <summary>The terrain's height scale / offset (TerrainSystem.heightScaleOffset): height = offset + value / 65535 * scale.</summary>
        public float HeightScaleM = HeightScale, HeightOffset;
        /// <summary>The playable area the heightmap covers (TerrainSystem.playableOffset / playableArea).</summary>
        public float2 MapOffset = new float2(-MapSize * 0.5f), MapExtent = new float2(MapSize);
        /// <summary>Flat apron around the outer roads, and the width of the ease back into the map's terrain.</summary>
        public float Apron = 40f, Blend = 200f;

        public float2 GridMin => new float2(LineX(0) - Apron, LineZ(0) - Apron);
        public float2 GridMax => new float2(LineX(NX) + Apron, SlopeEnd + Apron);

        /// <summary>Everything the fixture reshapes: the grid and every plot.</summary>
        public float2 FixtureMin { get { var m = GridMin; foreach (var p in Plots) m = math.min(m, p.OuterMin); return m; } }
        public float2 FixtureMax { get { var m = GridMax; foreach (var p in Plots) m = math.max(m, p.OuterMax); return m; } }

        // ───────────── plots ─────────────

        /// <summary>Test land made to order (SuitePlot), in a row east of the grid.</summary>
        public readonly List<SuitePlot> Plots = new List<SuitePlot>();
        public const float PlotGap = 120f;

        /// <summary>Adds a plot after the last one (centred on the grid's middle row), named P1, P2...</summary>
        public SuitePlot AddPlot(SuitePlot p)
        {
            p.Name = "P" + (Plots.Count + 1);
            p.Center = float2.zero;
            float2 relMin = p.OuterMin, relMax = p.OuterMax;   // relative to the centre while it is 0
            // (shops2) Rows: when the map's east edge is reached, a new row starts south of everything laid out so far (east of the
            // grid, where the grid's slope rows do not reach), so a request with many big lands still gets them all.
            int row = Plots.Count == 0 || !_row.TryGetValue(Plots[Plots.Count - 1], out var lr) ? 0 : lr;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                float x = GridMax.x + PlotGap;
                foreach (var q in Plots) if (_row.TryGetValue(q, out var qr) && qr == row) x = math.max(x, q.OuterMax.x + PlotGap);
                float cz = row == 0 ? (LineZ(0) + LineZ(NZ)) * 0.5f : _rowTop[row] - relMax.y;
                p.Center = new float2(x - relMin.x, cz);
                if (attempt == 1 || OnMap(p)) break;
                float top = GridMin.y;
                foreach (var q in Plots) top = math.min(top, q.OuterMin.y);
                row = _rowTop.Count + 1;
                _rowTop[row] = top - PlotGap;
                p.Center = float2.zero;
            }
            _row[p] = row;
            Plots.Add(p);
            return p;
        }

        private readonly Dictionary<SuitePlot, int> _row = new Dictionary<SuitePlot, int>();
        private readonly Dictionary<int, float> _rowTop = new Dictionary<int, float>();

        /// <summary>The plot lies inside the playable map with room for the ease back into the map's terrain.</summary>
        public bool OnMap(SuitePlot plot)
        {
            var lo = MapOffset + Blend;
            var hi = MapOffset + MapExtent - Blend;
            return plot.OuterMin.x >= lo.x && plot.OuterMin.y >= lo.y && plot.OuterMax.x <= hi.x && plot.OuterMax.y <= hi.y;
        }

        /// <summary>1 over the grid's columns, easing to 0 within 60 m east of its apron (the slope rows stop there).</summary>
        private float GridWeight(float x) => 1f - math.smoothstep(GridMax.x, GridMax.x + 60f, x);

        /// <summary>The rough blocks' row (the one below the two slope rows).</summary>
        public int RoughRow => NZ - 3;

        private float SlopeStart => LineZ(NZ - 2);
        private float SlopeEnd => LineZ(NZ) + 60f;

        public float SlopeRise(float z) => SlopeGrade * math.clamp(z - SlopeStart, 0f, SlopeEnd - SlopeStart);

        /// <summary>Hills inside the rough blocks, fading to zero 14 m inside the road centre lines.</summary>
        public float RoughBump(float x, float z)
        {
            int j = RoughRow;
            if (z <= LineZ(j) || z >= LineZ(j + 1)) return 0f;
            for (int i = 0; i <= 2; i++)
            {
                if (Kind(i, j) != SuiteBlockKind.Rough) continue;
                float x0 = LineX(i), x1 = LineX(i + 1);
                if (x <= x0 || x >= x1) continue;
                float d = math.min(math.min(x - x0, x1 - x), math.min(z - LineZ(j), LineZ(j + 1) - z));
                float mask = math.smoothstep(14f, 30f, d);
                return RoughAmplitude * math.sin(x / 11f) * math.cos(z / 9f) * mask;
            }
            return 0f;
        }

        public float Height(float x, float z)
        {
            float h = Base + GridWeight(x) * SlopeRise(z) + RoughBump(x, z);
            foreach (var p in Plots)
            {
                float w = p.Weight(x, z);
                if (w > 0f) h += w * p.Rise(x, z);
            }
            return h;
        }

        /// <summary>World position (x, z) of heightmap texel (px, py) (row = z, column = x).</summary>
        public float2 TexelWorld(int px, int py) => MapOffset + (new float2(px, py) + 0.5f) * (MapExtent / HeightmapSize);

        public ushort Encode(float height) => (ushort)math.clamp((int)math.round((height - HeightOffset) / HeightScaleM * 65535f), 0, 65535);

        public float Decode(ushort value) => HeightOffset + value / 65535f * HeightScaleM;

        /// <summary>Metres from (x, z) to the fixture rectangle (0 inside).</summary>
        public float OutsideFixture(float x, float z)
        {
            var lo = FixtureMin;
            var hi = FixtureMax;
            float dx = math.max(math.max(lo.x - x, x - hi.x), 0f), dz = math.max(math.max(lo.y - z, z - hi.y), 0f);
            return math.sqrt(dx * dx + dz * dz);
        }

        /// <summary>
        /// The full R16 heightmap (row = z, column = x). <paramref name="existing"/> is the map's current heightmap (same
        /// size and encoding): kept beyond the blend, the layout inside the fixture, a smooth ease between. Without it the
        /// whole map is the layout.
        /// </summary>
        public ushort[] Heightmap(ushort[] existing = null)
        {
            int n = HeightmapSize;
            var data = new ushort[n * n];
            if (existing != null && existing.Length != data.Length) throw new ArgumentException("existing heightmap must be " + n + "x" + n);
            float2 cell = MapExtent / n;
            float reach = existing != null ? Blend : float.MaxValue;
            var lo = FixtureMin - reach;
            var hi = FixtureMax + reach;
            for (int py = 0; py < n; py++)
            {
                float z = MapOffset.y + (py + 0.5f) * cell.y;
                int o = py * n;
                bool rowNear = z >= lo.y && z <= hi.y;
                if (existing != null && !rowNear) { Array.Copy(existing, o, data, o, n); continue; }
                for (int px = 0; px < n; px++)
                {
                    float x = MapOffset.x + (px + 0.5f) * cell.x;
                    if (existing != null && (x < lo.x || x > hi.x)) { data[o + px] = existing[o + px]; continue; }
                    float target = Height(x, z);
                    if (existing == null) { data[o + px] = Encode(target); continue; }
                    float d = OutsideFixture(x, z);
                    if (d >= Blend) { data[o + px] = existing[o + px]; continue; }
                    float t = math.smoothstep(0f, Blend, d);
                    data[o + px] = Encode(math.lerp(target, Decode(existing[o + px]), t));
                }
            }
            return data;
        }

        /// <summary>
        /// Restores one block: <paramref name="current"/> (the live heightmap) with every texel between the block's road
        /// centre lines set back to the layout; everything else untouched. Returns the number of texels changed.
        /// </summary>
        public int PatchBlock(ushort[] current, SuiteBlock b)
        {
            int n = HeightmapSize;
            if (current == null || current.Length != n * n) throw new ArgumentException("heightmap must be " + n + "x" + n);
            float2 cell = MapExtent / n;
            int px0 = math.max(0, (int)math.floor((b.X0 - MapOffset.x) / cell.x)), px1 = math.min(n - 1, (int)math.ceil((b.X1 - MapOffset.x) / cell.x));
            int py0 = math.max(0, (int)math.floor((b.Z0 - MapOffset.y) / cell.y)), py1 = math.min(n - 1, (int)math.ceil((b.Z1 - MapOffset.y) / cell.y));
            int changed = 0;
            for (int py = py0; py <= py1; py++)
            {
                float z = MapOffset.y + (py + 0.5f) * cell.y;
                if (z < b.Z0 || z > b.Z1) continue;
                for (int px = px0; px <= px1; px++)
                {
                    float x = MapOffset.x + (px + 0.5f) * cell.x;
                    if (x < b.X0 || x > b.X1) continue;
                    ushort v = Encode(Height(x, z));
                    if (current[py * n + px] != v) { current[py * n + px] = v; changed++; }
                }
            }
            return changed;
        }

        /// <summary>The median of the given ground heights (the fixture's Base: least earth moved).</summary>
        public static float Median(List<float> heights)
        {
            if (heights == null || heights.Count == 0) return BaseHeight;
            var s = new List<float>(heights);
            s.Sort();
            return s[s.Count / 2];
        }
    }
}
