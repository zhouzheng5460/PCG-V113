// Sculpted ground (s20): a node grid of target heights that becomes a continuous sloped surface.
//
// Why: playtests 28–34 rated every water type's terrain "blocky": each terraform cell was levelled to ONE height
// (6–12 m flat squares), so a graded bank became a staircase, dunes became lumps and path ramps became cones.
// A Sculpt holds target heights on grid NODES; neighbouring cells share their corners and are emitted as sloped
// quads (PlannedTerraform.Sloped), so what the game builds is the bilinear-like surface through the nodes: no steps.
//
// Typical use (beach, waterfront): new Sculpt over the land + foreshore → Shape (a profile from the water) →
// Smooth → LimitGrade → Pads/Corridors where things stand → FadeOut to the locked border → LimitEarthwork →
// Emit (terraform + ctx.Ground) → Resnap (objects/nets/surfaces onto the final ground).
// Nodes outside the sculptable zone are locked at the original ground, so the surface always meets the
// surroundings smoothly. Pure, deterministic, engine-free.

using System;
using System.Collections.Generic;
using Placemaker.Core.Build;
using Placemaker.Core.Geometry;
using Unity.Mathematics;

namespace Placemaker.Core.Layers.Common
{
    /// <summary>Cut/fill statistics of a sculpt (m, m³).</summary>
    public struct Earthwork
    {
        public float MaxCut, MaxFill;
        public double CutVolume, FillVolume;
        public override string ToString() => $"cut {MaxCut:0.0} m / {CutVolume:0} m³, fill {MaxFill:0.0} m / {FillVolume:0} m³";
    }

    public sealed class Sculpt
    {
        public const float DefaultCell = 4f;
        /// <summary>Cells whose corners all change less than this are not emitted (the ground stays as it is).</summary>
        public const float MinChange = 0.12f;
        private const int MaxNodes = 60000;

        public readonly float2 Origin;
        public readonly float Cell;
        public readonly int Nx, Ny;
        /// <summary>Original ground at each node.</summary>
        public readonly float[] Base;
        /// <summary>Target ground at each node (= Base where nothing changes).</summary>
        public readonly float[] Target;
        /// <summary>Node may change (inside the sculptable zone).</summary>
        public readonly bool[] Free;
        private readonly Func<float2, float> _h;
        // Per cell after Emit: 0 = untouched, 1 = whole quad emitted, 2 = clipped pieces (point test against the clips).
        private byte[] _cells;
        private IReadOnlyList<float2[]> _clips;
        [ThreadStatic] private static float3[] _quad;

        public int Count => Nx * Ny;
        public bool Emitted => _cells != null;

        public Sculpt(Func<float2, float> ground, float2 min, float2 max, float cell, Func<float2, bool> sculptable)
        {
            _h = ground ?? (_ => 0f);
            cell = math.max(1f, cell);
            min -= cell; max += cell;
            while ((long)(math.ceil((max.x - min.x) / cell) + 1) * (long)(math.ceil((max.y - min.y) / cell) + 1) > MaxNodes) cell *= 1.2f;
            Origin = min; Cell = cell;
            Nx = (int)math.ceil((max.x - min.x) / cell) + 1;
            Ny = (int)math.ceil((max.y - min.y) / cell) + 1;
            Base = new float[Nx * Ny];
            Target = new float[Nx * Ny];
            Free = new bool[Nx * Ny];
            for (int k = 0; k < Base.Length; k++)
            {
                var p = Pos(k);
                float h = _h(p);
                if (!math.isfinite(h)) h = 0f;
                Base[k] = Target[k] = h;
                Free[k] = sculptable == null || sculptable(p);
            }
        }

        /// <summary>Grid over a region (plus optional extra rings, e.g. the foreshore), sculptable inside any of them.</summary>
        public static Sculpt Over(LayerContext ctx, LayerRegion region, IReadOnlyList<float2[]> extra = null, float cell = DefaultCell, Func<float2, bool> exclude = null)
        {
            float2 min = region.Min, max = region.Max;
            if (extra != null) foreach (var r in extra) foreach (var q in r) { min = math.min(min, q); max = math.max(max, q); }
            return new Sculpt(ctx.H, min, max, cell, p =>
            {
                if (exclude != null && exclude(p)) return false;
                if (region.Contains(p)) return true;
                if (extra != null) foreach (var r in extra) if (Poly.Contains(r, p)) return true;
                return false;
            });
        }

        public float2 Pos(int k) => Origin + new float2(k % Nx, k / Nx) * Cell;
        public float Offset(int k) => Target[k] - Base[k];

        private bool Locate(float2 p, out int i, out int j, out float tx, out float ty)
        {
            float fx = (p.x - Origin.x) / Cell, fy = (p.y - Origin.y) / Cell;
            i = (int)math.floor(fx); j = (int)math.floor(fy);
            tx = fx - i; ty = fy - j;
            if (i < 0 || j < 0 || i >= Nx - 1 || j >= Ny - 1) return false;
            return true;
        }

        /// <summary>Bilinear sample of a node array (NaN outside the grid).</summary>
        public float Sample(float[] a, float2 p)
        {
            if (!Locate(p, out int i, out int j, out float tx, out float ty)) return float.NaN;
            int k = j * Nx + i;
            return math.lerp(math.lerp(a[k], a[k + 1], tx), math.lerp(a[k + Nx], a[k + Nx + 1], tx), ty);
        }

        /// <summary>Planned ground (the target surface) at p; the original ground outside the grid.</summary>
        public float HeightAt(float2 p)
        {
            float v = Sample(Target, p);
            return math.isfinite(v) ? v : _h(p);
        }

        /// <summary>Planned change (target − original) at p.</summary>
        public float ChangeAt(float2 p)
        {
            float t = Sample(Target, p), b = Sample(Base, p);
            return math.isfinite(t) ? t - b : 0f;
        }

        /// <summary>Steepest grade (rise/run) of the target surface around p over ±r.</summary>
        public float GradeAt(float2 p, float r = -1f)
        {
            if (r <= 0f) r = Cell * 0.5f;
            return TerrainSurvey.Grade(HeightAt, p, r);
        }

        // ───────────── shaping ─────────────

        /// <summary>New target per free node: (position, original ground, current target) → target. NaN keeps the current one.</summary>
        public void Shape(Func<float2, float, float, float> f)
        {
            for (int k = 0; k < Target.Length; k++)
            {
                if (!Free[k]) continue;
                float t = f(Pos(k), Base[k], Target[k]);
                if (math.isfinite(t)) Target[k] = t;
            }
        }

        /// <summary>
        /// Laplacian smoothing of the TARGET over free nodes (locked nodes hold): softens creases where two shapes meet
        /// (beach plane against the bank, a pad against the slope). <paramref name="weight"/> (p) ∈ [0,1] scales the
        /// smoothing per node (null = 1).
        /// </summary>
        public void Smooth(int passes, float strength = 0.5f, Func<float2, float> weight = null)
        {
            float[] w = null;
            if (weight != null) { w = new float[Target.Length]; for (int k = 0; k < w.Length; k++) w[k] = Free[k] ? math.saturate(weight(Pos(k))) : 0f; }
            var tmp = new float[Target.Length];
            for (int pass = 0; pass < passes; pass++)
            {
                Array.Copy(Target, tmp, Target.Length);
                for (int j = 1; j < Ny - 1; j++)
                    for (int i = 1; i < Nx - 1; i++)
                    {
                        int k = j * Nx + i;
                        if (!Free[k]) continue;
                        float s = strength * (w != null ? w[k] : 1f);
                        if (s <= 0f) continue;
                        float avg = (tmp[k - 1] + tmp[k + 1] + tmp[k - Nx] + tmp[k + Nx]) * 0.25f;
                        Target[k] = math.lerp(tmp[k], avg, s);
                    }
            }
        }

        /// <summary>
        /// Limits the grade of what we CHANGED: between neighbours the target may differ by at most maxGrade·cell plus
        /// the original ground's own difference (a natural slope is not ours to fix); the node changed more gives way.
        /// </summary>
        public void LimitGrade(float maxGrade, int passes = 12, bool absolute = false)
        {
            float step = maxGrade * Cell;
            for (int pass = 0; pass < passes; pass++)
            {
                bool changed = false;
                for (int j = 0; j < Ny; j++)
                    for (int i = 0; i < Nx; i++)
                    {
                        int k = j * Nx + i;
                        if (!Free[k]) continue;
                        for (int n = 0; n < 4; n++)
                        {
                            int ii = i + (n == 0 ? 1 : n == 1 ? -1 : 0), jj = j + (n == 2 ? 1 : n == 3 ? -1 : 0);
                            if (ii < 0 || jj < 0 || ii >= Nx || jj >= Ny) continue;
                            int m = jj * Nx + ii;
                            // (s30) absolute: never steeper than maxGrade or the original slope there, whichever is steeper (the sum let a
                            // cut into a 30 % bank reach 60 %, a sheet over one heightmap texel).
                            float allowed = absolute ? math.max(step, math.abs(Base[k] - Base[m])) : step + math.abs(Base[k] - Base[m]);
                            float d = Target[k] - Target[m];
                            if (math.abs(d) <= allowed + 1e-4f) continue;
                            if (math.abs(Offset(k)) < math.abs(Offset(m)) && Free[m]) continue;   // the other node yields on its turn
                            Target[k] = Target[m] + math.sign(d) * allowed;
                            changed = true;
                        }
                    }
                if (!changed) break;
            }
        }

        /// <summary>
        /// A level platform at <paramref name="height"/> inside <paramref name="ring"/>, blending back to the current
        /// target over <paramref name="blend"/> m outside (smoothstep). Used under huts, kiosks, landings, plazas.
        /// </summary>
        public void Pad(IReadOnlyList<float2> ring, float height, float blend)
        {
            if (ring == null || ring.Count < 3 || !math.isfinite(height)) return;
            Poly.Bounds(ring, out var mn, out var mx);
            mn -= blend + Cell; mx += blend + Cell;
            var r = ring is float2[] arr ? arr : new List<float2>(ring).ToArray();
            ForNodes(mn, mx, k =>
            {
                var p = Pos(k);
                bool inside = Poly.Contains(r, p);
                float d = inside ? 0f : Poly.DistanceToBoundary(r, p);
                if (d > blend) return;
                float w = inside ? 1f : 1f - Smooth01(d / math.max(0.01f, blend));
                Target[k] = math.lerp(Target[k], height, w);
            });
        }

        /// <summary>A level disc platform (centre, radius) with a smooth blend.</summary>
        public void PadDisc(float2 c, float radius, float height, float blend)
        {
            ForNodes(c - radius - blend - Cell, c + radius + blend + Cell, k =>
            {
                float d = math.max(0f, math.distance(Pos(k), c) - radius);
                if (d > blend) return;
                float w = d <= 0f ? 1f : 1f - Smooth01(d / math.max(0.01f, blend));
                Target[k] = math.lerp(Target[k], height, w);
            });
        }

        /// <summary>
        /// Levels a strip along a polyline: across the strip the ground is flat at <paramref name="profile"/>(along)
        /// (a promenade bench, a ramp), blending back over <paramref name="blend"/> m beyond the half width.
        /// </summary>
        public void Corridor(IReadOnlyList<float2> line, Func<float, float> profile, float halfWidth, float blend)
        {
            if (line == null || line.Count < 2) return;
            var along = new float[line.Count];
            for (int i = 1; i < line.Count; i++) along[i] = along[i - 1] + math.distance(line[i - 1], line[i]);
            float2 mn = line[0], mx = line[0];
            foreach (var q in line) { mn = math.min(mn, q); mx = math.max(mx, q); }
            float reach = halfWidth + blend + Cell;
            ForNodes(mn - reach, mx + reach, k =>
            {
                var p = Pos(k);
                float best = float.MaxValue, s = 0f;
                for (int i = 1; i < line.Count; i++)
                {
                    var a = line[i - 1]; var ab = line[i] - a;
                    float l2 = math.lengthsq(ab);
                    float t = l2 > 1e-9f ? math.saturate(math.dot(p - a, ab) / l2) : 0f;
                    float d = math.distance(p, a + ab * t);
                    if (d < best) { best = d; s = math.lerp(along[i - 1], along[i], t); }
                }
                if (best > halfWidth + blend) return;
                float h = profile(s);
                if (!math.isfinite(h)) return;
                float w = best <= halfWidth ? 1f : 1f - Smooth01((best - halfWidth) / math.max(0.01f, blend));
                Target[k] = math.lerp(Target[k], h, w);
            });
        }

        /// <summary>
        /// Scales every change by smoothstep(distance to the nearest locked node / falloff): the sculpt dies out
        /// towards the border of the sculptable zone instead of ending in a cliff. Distances by a chamfer transform.
        /// </summary>
        public void FadeOut(float falloff, Func<float2, bool> keep = null, float maxGrade = 0.25f)
        {
            if (falloff <= 0f) return;
            var dist = LockedDistance();
            for (int k = 0; k < Target.Length; k++)
            {
                if (!Free[k]) continue;
                if (keep != null && keep(Pos(k))) continue;
                // Never steeper than maxGrade towards the border either: a 5 m cut needs 20 m to die out, not the fade width.
                float w = Smooth01(dist[k] / falloff);
                float o = Offset(k) * w;
                float cap = maxGrade * math.max(0f, dist[k] - Cell);
                Target[k] = Base[k] + math.clamp(o, -cap, cap);

            }
        }

        /// <summary>
        /// Holds every change within maxGrade × (distance to the nearest locked node): run after smoothing, which
        /// otherwise drags a deep cut back towards the border it was faded away from.
        /// </summary>
        public void CapToBorder(float maxGrade)
        {
            var dist = LockedDistance();
            for (int k = 0; k < Target.Length; k++)
            {
                if (!Free[k]) continue;
                float cap = maxGrade * math.max(0f, dist[k] - Cell);
                Target[k] = Base[k] + math.clamp(Offset(k), -cap, cap);
            }
        }

        /// <summary>Distance (m) from every node to the nearest locked node (two-pass chamfer).</summary>
        public float[] LockedDistance()
        {
            var d = new float[Target.Length];
            const float Big = 1e9f;
            for (int k = 0; k < d.Length; k++) d[k] = Free[k] ? Big : 0f;
            float a = Cell, b = Cell * 1.41421356f;
            for (int j = 0; j < Ny; j++)
                for (int i = 0; i < Nx; i++)
                {
                    int k = j * Nx + i;
                    if (i == 0 || j == 0 || i == Nx - 1 || j == Ny - 1) d[k] = math.min(d[k], Free[k] ? Cell : 0f);
                    if (i > 0) d[k] = math.min(d[k], d[k - 1] + a);
                    if (j > 0) d[k] = math.min(d[k], d[k - Nx] + a);
                    if (i > 0 && j > 0) d[k] = math.min(d[k], d[k - Nx - 1] + b);
                    if (i < Nx - 1 && j > 0) d[k] = math.min(d[k], d[k - Nx + 1] + b);
                }
            for (int j = Ny - 1; j >= 0; j--)
                for (int i = Nx - 1; i >= 0; i--)
                {
                    int k = j * Nx + i;
                    if (i < Nx - 1) d[k] = math.min(d[k], d[k + 1] + a);
                    if (j < Ny - 1) d[k] = math.min(d[k], d[k + Nx] + a);
                    if (i < Nx - 1 && j < Ny - 1) d[k] = math.min(d[k], d[k + Nx + 1] + b);
                    if (i > 0 && j < Ny - 1) d[k] = math.min(d[k], d[k + Nx - 1] + b);
                }
            return d;
        }

        /// <summary>Cut and fill of the current targets.</summary>
        public Earthwork Measure()
        {
            var e = new Earthwork();
            double area = Cell * Cell;
            for (int k = 0; k < Target.Length; k++)
            {
                if (!Free[k]) continue;
                float o = Offset(k);
                if (o < 0f) { e.MaxCut = math.max(e.MaxCut, -o); e.CutVolume += -o * area; }
                else { e.MaxFill = math.max(e.MaxFill, o); e.FillVolume += o * area; }
            }
            return e;
        }

        /// <summary>
        /// Sane earthworks: no node is cut deeper than <paramref name="maxCut"/> or filled higher than
        /// <paramref name="maxFill"/>; the clamp is then relaxed (smoothed) so it leaves no shelf. Returns true when
        /// anything had to be limited.
        /// </summary>
        public bool LimitEarthwork(float maxCut, float maxFill)
        {
            bool limited = false;
            var mask = new bool[Target.Length];
            for (int k = 0; k < Target.Length; k++)
            {
                if (!Free[k]) continue;
                float o = Offset(k);
                float c = math.clamp(o, -maxCut, maxFill);
                if (c != o) { Target[k] = Base[k] + c; limited = true; mask[k] = true; }
            }
            if (!limited) return false;
            // Soften the clamp line (offsets only, around the clamped nodes), then clamp again (smoothing never deepens it).
            var tmp = new float[Target.Length];
            for (int pass = 0; pass < 3; pass++)
            {
                for (int k = 0; k < Target.Length; k++) tmp[k] = Offset(k);
                for (int j = 1; j < Ny - 1; j++)
                    for (int i = 1; i < Nx - 1; i++)
                    {
                        int k = j * Nx + i;
                        if (!Free[k]) continue;
                        bool near = mask[k] || mask[k - 1] || mask[k + 1] || mask[k - Nx] || mask[k + Nx];
                        if (!near) continue;
                        float avg = (tmp[k - 1] + tmp[k + 1] + tmp[k - Nx] + tmp[k + Nx]) * 0.25f;
                        float o = math.clamp(math.lerp(tmp[k], avg, 0.5f), -maxCut, maxFill);
                        Target[k] = Base[k] + o;
                    }
            }
            return true;
        }

        /// <summary>
        /// Roughness: the largest change of grade per metre between neighbouring cells of the CHANGED surface
        /// (|T(i+1) − 2T(i) + T(i−1)| / cell², along x and y), over nodes whose three samples all moved. A terraced
        /// staircase scores high; a smooth bank or a gentle crease scores low.
        /// </summary>
        public float Roughness()
        {
            float worst = 0f;
            float c2 = Cell * Cell;
            for (int j = 1; j < Ny - 1; j++)
                for (int i = 1; i < Nx - 1; i++)
                {
                    int k = j * Nx + i;
                    if (!Free[k] || math.abs(Offset(k)) < MinChange) continue;
                    if (Free[k - 1] && Free[k + 1]) worst = math.max(worst, math.abs(Target[k + 1] - 2f * Target[k] + Target[k - 1]) / c2);
                    if (Free[k - Nx] && Free[k + Nx]) worst = math.max(worst, math.abs(Target[k + Nx] - 2f * Target[k] + Target[k - Nx]) / c2);
                }
            return worst;
        }

        // ───────────── output ─────────────

        /// <summary>
        /// Emits the changed cells as sloped terraform quads clipped to <paramref name="clips"/> (null = the free zone's
        /// whole cells only), registers the new ground with ctx.Ground (what this layer places afterwards stands on
        /// it) and returns the result (HeightAfter). Adds the permanent-change warning when anything changes.
        /// </summary>
        public TerrainResult Emit(LayerContext ctx, IReadOnlyList<float2[]> clips, float minChange = MinChange, int skirt = 0)
        {
            var result = new TerrainResult { HeightAfter = ctx.H };
            int cx = Nx - 1, cy = Ny - 1;
            _cells = new byte[cx * cy];
            // (s30) Skirt: cells within <skirt> cells of a changed cell are emitted too (with their ~zero change), so the build
            // raster's own 4 m bank — which extrapolates the change of the outermost patch — starts where the field is already
            // back on the original ground instead of pushing a berm out past it (a fill in front of a quay wall, a lip at a fade).
            var emit = new bool[cx * cy];
            for (int j = 0; j < cy; j++)
                for (int i = 0; i < cx; i++)
                {
                    int k00 = j * Nx + i;
                    float bg = math.max(math.max(math.abs(Offset(k00)), math.abs(Offset(k00 + 1))), math.max(math.abs(Offset(k00 + Nx)), math.abs(Offset(k00 + Nx + 1))));
                    emit[j * cx + i] = bg >= minChange;
                }
            if (skirt > 0)
            {
                var grown = (bool[])emit.Clone();
                for (int j = 0; j < cy; j++)
                    for (int i = 0; i < cx; i++)
                    {
                        if (!emit[j * cx + i]) continue;
                        for (int dj = -skirt; dj <= skirt; dj++)
                            for (int di = -skirt; di <= skirt; di++)
                            {
                                int ii = i + di, jj = j + dj;
                                if (ii >= 0 && jj >= 0 && ii < cx && jj < cy) grown[jj * cx + ii] = true;
                            }
                    }
                emit = grown;
            }
            // Cells crossed by a clip edge (sampled every third of a cell): only those need polygon clipping.
            var touched = new List<bool[]>();
            if (clips != null)
                foreach (var c in clips)
                {
                    var mask = new bool[cx * cy];
                    for (int e = 0; e < c.Length; e++)
                    {
                        var a = c[e]; var b2 = c[(e + 1) % c.Length];
                        int steps = math.max(1, (int)math.ceil(math.distance(a, b2) / (Cell * 0.3f)));
                        for (int s = 0; s <= steps; s++)
                        {
                            var q = math.lerp(a, b2, s / (float)steps);
                            int qi = (int)math.floor((q.x - Origin.x) / Cell), qj = (int)math.floor((q.y - Origin.y) / Cell);
                            for (int dj = -1; dj <= 1; dj++)
                                for (int di = -1; di <= 1; di++)
                                {
                                    int ii = qi + di, jj = qj + dj;
                                    if (ii >= 0 && jj >= 0 && ii < cx && jj < cy) mask[jj * cx + ii] = true;
                                }
                        }
                    }
                    touched.Add(mask);
                }
            _clips = clips;
            float minO = 0f, maxO = 0f; int cells = 0;
            for (int j = 0; j < cy; j++)
            {
                if ((j & 7) == 0) ctx.Check();
                for (int i = 0; i < cx; i++)
                {
                    int k00 = j * Nx + i, k10 = k00 + 1, k01 = k00 + Nx, k11 = k01 + 1;
                    float o00 = Offset(k00), o10 = Offset(k10), o01 = Offset(k01), o11 = Offset(k11);
                    if (!emit[j * cx + i]) continue;
                    var p00 = Pos(k00); var p10 = Pos(k10); var p11 = Pos(k11); var p01 = Pos(k01);
                    var square = new[] { p00, p10, p11, p01 };
                    float mean = (o00 + o10 + o01 + o11) * 0.25f;
                    bool whole = clips == null;
                    if (!whole)
                        for (int ci = 0; ci < clips.Count; ci++)
                            if (!touched[ci][j * cx + i] && Poly.Contains(clips[ci], (p00 + p11) * 0.5f)) { whole = true; break; }
                    if (whole)
                    {
                        if (clips == null && !(Free[k00] || Free[k10] || Free[k01] || Free[k11])) continue;
                        Add(ctx, square, mean);
                        _cells[j * cx + i] = 1;
                    }
                    else
                    {
                        bool any = false;
                        foreach (var c in clips)
                        {
                            // Sutherland–Hodgman: the (concave) clip ring cut by the convex cell square — O(ring) per
                            // cell, no polygon boolean (the boolean per boundary cell cost ~100 ms on a beach).
                            var piece = ClipToRect(c, p00, p11);
                            if (piece == null) continue;
                            var clean = Poly.Clean(piece, 0.15f);
                            if (clean.Length < 3 || Poly.Area(clean) < 0.5f) continue;
                            Add(ctx, clean, mean);
                            any = true;
                        }
                        if (!any) continue;
                        _cells[j * cx + i] = 2;
                    }
                    minO = math.min(minO, math.min(math.min(o00, o10), math.min(o01, o11)));
                    maxO = math.max(maxO, math.max(math.max(o00, o10), math.max(o01, o11)));
                    cells++;
                }
            }
            result.Cells = cells; result.MinOffset = minO; result.MaxOffset = maxO;
            if (cells == 0) return result;
            Warn(ctx);
            result.HeightAfter = HeightAfter;
            var min = Origin; var max = Origin + new float2(Nx - 1, Ny - 1) * Cell;
            ctx.Ground.Override(min, max, HeightAfter);
            ctx.Plan.Facts["terrainCells"] = cells;
            ctx.Plan.Facts["terrainMaxRaise"] = maxO;
            ctx.Plan.Facts["terrainMaxLower"] = -minO;
            return result;
        }


        /// <summary>Sutherland–Hodgman clip of a polygon to an axis-aligned rectangle; null when nothing remains.</summary>
        public static float2[] ClipToRect(float2[] ring, float2 min, float2 max)
        {
            var poly = new List<float2>(ring);
            for (int side = 0; side < 4 && poly.Count > 0; side++)
            {
                var input = poly;
                poly = new List<float2>(input.Count + 4);
                for (int i = 0; i < input.Count; i++)
                {
                    var cur = input[i]; var prev = input[(i + input.Count - 1) % input.Count];
                    bool ci = Inside(cur, side, min, max), pi = Inside(prev, side, min, max);
                    if (ci)
                    {
                        if (!pi) poly.Add(Cross(prev, cur, side, min, max));
                        poly.Add(cur);
                    }
                    else if (pi) poly.Add(Cross(prev, cur, side, min, max));
                }
            }
            return poly.Count >= 3 ? poly.ToArray() : null;
        }

        private static bool Inside(float2 p, int side, float2 min, float2 max) =>
            side == 0 ? p.x >= min.x : side == 1 ? p.x <= max.x : side == 2 ? p.y >= min.y : p.y <= max.y;

        private static float2 Cross(float2 a, float2 b, int side, float2 min, float2 max)
        {
            float t;
            switch (side)
            {
                case 0: t = (min.x - a.x) / (b.x - a.x); break;
                case 1: t = (max.x - a.x) / (b.x - a.x); break;
                case 2: t = (min.y - a.y) / (b.y - a.y); break;
                default: t = (max.y - a.y) / (b.y - a.y); break;
            }
            return math.lerp(a, b, math.saturate(t));
        }
        private static bool CrossesRing(float2[] ring, float2[] square)
        {
            for (int a = 0; a < 4; a++)
            {
                var p = square[a]; var q = square[(a + 1) % 4];
                for (int i = 0; i < ring.Length; i++)
                    if (Poly.SegmentsIntersect(p, q, ring[i], ring[(i + 1) % ring.Length])) return true;
            }
            return false;
        }

        private void Add(LayerContext ctx, float2[] ring, float meanOffset)
        {
            var r3 = new float3[ring.Length];
            float2 c = 0f;
            for (int v = 0; v < ring.Length; v++)
            {
                float h = Sample(Target, ring[v]);
                if (!math.isfinite(h)) h = _h(ring[v]);
                r3[v] = new float3(ring[v].x, h, ring[v].y);
                c += ring[v];
            }
            c /= ring.Length;
            ctx.Plan.Terraform.Add(new PlannedTerraform
            {
                Ring = r3, Kind = meanOffset > 0f ? TerraformKind.Raise : TerraformKind.Lower,
                TargetHeight = TerraformSurface.At(r3, c), Permanent = true, Sloped = true,
            });
        }

        /// <summary>Ground after Emit: the target surface in emitted cells, the original ground elsewhere.</summary>
        public float HeightAfter(float2 p)
        {
            if (_cells == null || !Locate(p, out int i, out int j, out _, out _)) return _h(p);
            byte c = _cells[j * (Nx - 1) + i];
            if (c == 0) return _h(p);
            if (c == 2)
            {
                bool inside = false;
                foreach (var r in _clips) if (Poly.Contains(r, p)) { inside = true; break; }
                if (!inside) return _h(p);
            }
            // The same surface the build rasters: mean-value interpolation over the cell's corners (not bilinear), so a planned
            // object height equals the built ground even on a strongly twisted cell.
            int k = j * Nx + i;
            var q = _quad ?? (_quad = new float3[4]);
            q[0] = new float3(Pos(k).x, Target[k], Pos(k).y);
            q[1] = new float3(Pos(k + 1).x, Target[k + 1], Pos(k + 1).y);
            q[2] = new float3(Pos(k + Nx + 1).x, Target[k + Nx + 1], Pos(k + Nx + 1).y);
            q[3] = new float3(Pos(k + Nx).x, Target[k + Nx], Pos(k + Nx).y);
            return TerraformSurface.At(q, p);
        }

        /// <summary>
        /// Puts everything this layer planned onto the final ground: objects, surfaces and terrain-following nets
        /// (decks and ramps with FollowTerrain = false keep their heights). Run after Emit.
        /// </summary>
        public static void Resnap(LayerContext ctx)
        {
            var plan = ctx.Plan;
            foreach (var o in plan.Objects) o.Position = ctx.P3(o.Position.xz);
            foreach (var s in plan.Surfaces)
                if (s?.Ring != null) for (int v = 0; v < s.Ring.Length; v++) s.Ring[v].y = ctx.G(s.Ring[v].xz);
            foreach (var a in plan.Areas)
                if (a?.Ring != null) for (int v = 0; v < a.Ring.Length; v++) a.Ring[v].y = ctx.G(a.Ring[v].xz);
            foreach (var n in plan.Nets)
            {
                if (n == null || !n.FollowTerrain) continue;
                n.A.y = ctx.G(n.A.xz); n.B.y = ctx.G(n.B.xz); n.C.y = ctx.G(n.C.xz); n.D.y = ctx.G(n.D.xz);
            }
        }

        private void ForNodes(float2 mn, float2 mx, Action<int> f)
        {
            int i0 = math.max(0, (int)math.floor((mn.x - Origin.x) / Cell)), i1 = math.min(Nx - 1, (int)math.ceil((mx.x - Origin.x) / Cell));
            int j0 = math.max(0, (int)math.floor((mn.y - Origin.y) / Cell)), j1 = math.min(Ny - 1, (int)math.ceil((mx.y - Origin.y) / Cell));
            for (int j = j0; j <= j1; j++)
                for (int i = i0; i <= i1; i++)
                {
                    int k = j * Nx + i;
                    if (Free[k]) f(k);
                }
        }

        /// <summary>The permanent-terrain warning, once per plan.</summary>
        public static void Warn(LayerContext ctx)
        {
            foreach (var i in ctx.Plan.Issues) if (i.Key == TerrainShaper.PermanentKey) return;
            ctx.Issue(IssueSeverity.Warning, TerrainShaper.PermanentKey);
        }

        public static float Smooth01(float t)
        {
            t = math.saturate(t);
            return t * t * (3f - 2f * t);
        }
    }

}
