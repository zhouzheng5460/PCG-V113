// Shoreline analysis shared by the water types (beach, waterfront).
//
// Everything along a shore is laid out by "how far from the water": sand bands, the back path,
// the promenade, railings, dunes. So the first job is finding the water side of the area:
//   * Inside — water lies inside the outline (the player drew over the waterline): the land is the
//              dry part (grid of WaterDepthAt samples, grown back to the outline), the shore is the
//              piece of its outline that touches the water. Piers may reach out over the water.
//   * Edge   — the outline runs along water outside it: outline edges whose outward probes are wet.
//   * Assumed — no water found (or the player chose a side): edges facing the chosen direction
//              ("away from the road" when roads are known, else the longest edge).
// The shore is then one ordered polyline (the longest contiguous run of shore edges, small gaps
// closed) with smoothed inward normals; Depth(p) = distance to it. Offsets of that polyline give
// lines parallel to the water (paths, rows, fences) and bands between two depths (sand, promenade).

using System;
using System.Collections.Generic;
using Placemaker.Core.Geometry;
using Placemaker.Core.Layers.Common;
using Placemaker.Core.Layers.Park;
using Unity.Mathematics;

namespace Placemaker.Core.Layers.Types.Beach
{
    public enum ShoreMode { Edge = 0, Inside = 1, Assumed = 2 }

    /// <summary>(s20) The bank rising from the water: low (a beach can meet the water), a bank (sculpt a slope or a quay), a cliff (leave it).</summary>
    public enum ShoreKind { Low = 0, Bank = 1, Cliff = 2 }

    public sealed class Shoreline
    {
        public const string NoWaterKey = "Placemaker.Shore.Issue.NoWater";
        public const string WaterInsideKey = "Placemaker.Shore.Issue.WaterInside";
        public const float WetDepth = 0.05f;
        public static readonly string[] Sides = { "auto", "awayFromRoad", "north", "east", "south", "west" };

        public ShoreMode Mode;
        /// <summary>The dry part the type builds on (the whole region unless water lies inside it).</summary>
        public LayerRegion Land;
        /// <summary>The original region (piers may reach out over water inside it).</summary>
        public LayerRegion Outline;
        /// <summary>Ordered points along the land outline at the water (land on the left).</summary>
        public float2[] Chain;
        /// <summary>Smoothed unit normals pointing inland, one per chain point.</summary>
        public float2[] Inward;
        /// <summary>Arc length at each chain point.</summary>
        public float[] Along;
        public float Length;
        public float2 WaterDir;
        /// <summary>Water surface height when water was found, else NaN.</summary>
        public float WaterLevel = float.NaN;
        /// <summary>Largest distance from the shore inside the land (sampled).</summary>
        public float MaxDepth;
        public bool Closed;

        private readonly SegmentIndex _chain = new SegmentIndex(8f);
        private readonly SegmentIndex _other = new SegmentIndex(8f);
        private Func<float2, float> _depthAt = _ => 0f;
        private Func<float2, float> _ground = _ => 0f;
        /// <summary>(s20) The water level is trusted for the waterline (heights below it, next to water, are wet).</summary>
        private bool _levelWaterline;

        /// <summary>Distance from the shore polyline (capped).</summary>
        public float Depth(float2 p, float cap = 2000f) => _chain.Clearance(p, cap);

        /// <summary>Distance to the outline away from the water (where terrain must blend back), capped.</summary>
        public float EdgeDistance(float2 p, float cap = 2000f) => _other.Count == 0 ? cap : _other.Clearance(p, cap);

        /// <summary>
        /// (s20) Under water at p. The game's water snapshot is a coarse grid (8–16 m, bilinear), so its zero line can
        /// sit a whole cell off the real shore. The terrain grid is finer, so near water (any depth in the snapshot)
        /// the ground below the water LEVEL decides; deep snapshot water is always wet, a dry snapshot always dry.
        /// </summary>
        public bool IsWet(float2 p) => Wet(_depthAt, _ground, WaterLevel, _levelWaterline, p);
        /// <summary>(s20) The water level agrees with the terrain (dry ground lies above it): heights may be compared with it.</summary>
        public bool LevelTrusted => _levelWaterline;

        private static bool Wet(Func<float2, float> depth, Func<float2, float> ground, float level, bool useLevel, float2 p)
        {
            float d = depth(p);
            if (!useLevel) return d > WetDepth;
            if (d > 0.6f) return true;
            if (d <= 1e-4f) return false;
            return ground(p) < level - 0.02f;
        }

        /// <summary>
        /// Robust water surface height around the region: the median of ground + depth over clearly wet samples
        /// (depth &gt; 0.3 m, so the snapshot's blurred margin does not bias it). NaN when no water.
        /// </summary>
        public static float RobustLevel(LayerContext ctx, LayerRegion region, float margin, out bool consistent)
        {
            consistent = false;
            if (region == null) return float.NaN;
            return RobustLevel(ctx.Site, region.Min - margin, region.Max + margin, out consistent);
        }

        /// <summary>The same over a box of a site (callers without a layer context: the decal dressing pass).</summary>
        public static float RobustLevel(SiteContext site, float2 min, float2 max, out bool consistent)
        {
            consistent = false;
            var depth = site?.WaterDepthAt;
            if (depth == null) return float.NaN;
            Func<float2, float> H = site.HeightAt ?? (_ => 0f);
            float step = math.max(3f, math.sqrt((max.x - min.x) * (max.y - min.y) / 3000f));
            var levels = new List<float>();
            var dry = new List<float>();
            for (float y = min.y + step * 0.5f; y < max.y; y += step)
                for (float x = min.x + step * 0.5f; x < max.x; x += step)
                {
                    var p = new float2(x, y);
                    float d = depth(p);
                    if (d > 0.3f) levels.Add(H(p) + d);
                    // Dry ground next to clearly wet water (within ~10 m) tells whether the heights agree with the level.
                    else if (d <= 1e-4f && (depth(p + new float2(10f, 0f)) > 0.3f || depth(p - new float2(10f, 0f)) > 0.3f || depth(p + new float2(0f, 10f)) > 0.3f || depth(p - new float2(0f, 10f)) > 0.3f)) dry.Add(H(p));
                }
            if (levels.Count == 0) return float.NaN;
            levels.Sort();
            float level = levels[levels.Count / 2];
            // Trust heights only when the dry ground mostly lies above the water (a fixture or a stale snapshot may not).
            int below = 0;
            foreach (var h in dry) if (h < level - 0.05f) below++;
            consistent = dry.Count == 0 || below <= dry.Count * 0.2f;
            return level;
        }

        /// <summary>
        /// (s20) A water test for anything that is not a shore layer (decal dressing): the robust level and, where the
        /// heights agree with it, the given ground (e.g. the ground after the plan's terraform: a carved beach face is under
        /// water, so wet-sand bands follow the REAL, final waterline). Null when the site has no water.
        /// </summary>
        public static Func<float2, bool> WetTest(SiteContext site, float2 min, float2 max, Func<float2, float> ground)
        {
            var depth = site?.WaterDepthAt;
            if (depth == null) return null;
            float level = RobustLevel(site, min, max, out bool consistent);
            if (!math.isfinite(level)) return null;
            var g = ground ?? site.HeightAt;
            return p => Wet(depth, g, level, consistent, p);
        }

        /// <summary>Signed distance from the shore polyline: negative on the water side of the land (foreshore).</summary>
        public float SignedDepth(float2 p, float cap = 2000f)
        {
            float d = Depth(p, cap);
            if (Land.Contains(p)) return d;
            // (s20) Outside the land: on the water side only when it lies seaward of the nearest shore point (a point beyond
            // the outline away from the water, e.g. an entrance on the back edge, is inland).
            var q = Nearest(p, out var inw, out _);
            return math.dot(p - q, inw) >= -0.01f ? d : -d;
        }

        // ───────────── foreshore (s13) ─────────────

        /// <summary>Probed water edge points (filled by <see cref="Foreshore"/>), in chain order.</summary>
        public List<float2> Waterline = new List<float2>();

        /// <summary>
        /// The strip between the land's shore edge and the real water, outside the land, reaching
        /// <paramref name="intoWater"/> m past the waterline, so sand and the graded slope actually meet (and go
        /// under) the water: a player never draws exactly on the waterline. Uses the measured waterline (Gap, s20);
        /// points without water within <paramref name="reach"/> m, or rejected by <paramref name="include"/> (cliffs),
        /// get no strip. Simple CCW rings (overlapping the land by 0.3 m); also fills <see cref="Waterline"/>.
        /// </summary>
        public List<float2[]> Foreshore(float reach, float intoWater, Func<int, bool> include = null)
        {
            var result = new List<float2[]>();
            Waterline = new List<float2>();
            if (Chain == null || Chain.Length < 2 || Mode == ShoreMode.Assumed || Gap == null) return result;
            int n = Chain.Length;
            var w = new float[n];
            bool any = false;
            for (int i = 0; i < n; i++)
            {
                w[i] = math.isfinite(Gap[i]) && Gap[i] <= reach && (include == null || include(i)) ? Gap[i] : -1f;
                if (w[i] >= 0f) { any = true; Waterline.Add(Chain[i] - Inward[i] * w[i]); }
            }
            if (!any) return result;
            // Smooth the widths (only between found points) so the strip edge is calm.
            for (int pass = 0; pass < 3; pass++)
            {
                var t = (float[])w.Clone();
                for (int i = 0; i < n; i++)
                {
                    if (w[i] < 0f) continue;
                    float s = w[i] * 2f, c = 2f;
                    if (i > 0 && w[i - 1] >= 0f) { s += w[i - 1]; c += 1f; }
                    if (i + 1 < n && w[i + 1] >= 0f) { s += w[i + 1]; c += 1f; }
                    t[i] = s / c;
                }
                w = t;
            }
            int k = 0;
            while (k < n)
            {
                if (w[k] < 0f) { k++; continue; }
                int a = k;
                while (k < n && w[k] >= 0f) k++;
                int b = k - 1;
                if (b - a < 1) continue;
                var ring = new List<float2>();
                for (int i = a; i <= b; i++) ring.Add(Chain[i] + Inward[i] * 0.3f);
                for (int i = b; i >= a; i--) ring.Add(Chain[i] - Inward[i] * (w[i] + intoWater));
                try
                {
                    var clean = Poly.Clean(ring, 0.3f);
                    if (clean.Length < 3) continue;
                    foreach (var r in Poly.UnionAll(new[] { (IReadOnlyList<float2>)clean }))
                        foreach (var piece in Poly.CutHoles(r, 0.5f))
                        {
                            var c = Poly.Clean(piece, 0.3f);
                            if (c.Length >= 3 && Poly.Area(c) > 4f) result.Add(c);
                        }
                }
                catch (Exception) { }
            }
            return result;
        }

        // ───────────── analysis ─────────────

        /// <summary>
        /// The dry part of <paramref name="region"/> when the player drew over existing water (more than 2 % wet),
        /// else the region itself. For nature types that lay out their own paths and planting (wetland, trails):
        /// nothing of theirs belongs in a lake they were drawn across (art pass a3).
        /// </summary>
        public static LayerRegion DryLand(LayerContext ctx, LayerRegion region)
        {
            var depth = ctx.Site?.WaterDepthAt;
            if (depth == null || region == null) return region;
            var size = region.Max - region.Min;
            float step = math.max(3f, math.sqrt(size.x * size.y / 2500f));
            int wet = 0, total = 0;
            for (float y = region.Min.y + step * 0.5f; y < region.Max.y; y += step)
                for (float x = region.Min.x + step * 0.5f; x < region.Max.x; x += step)
                {
                    var p = new float2(x, y);
                    if (!region.Contains(p)) continue;
                    total++;
                    if (depth(p) > WetDepth) wet++;
                }
            if (wet == 0 || wet < total * 0.02f) return region;
            var land = DryPart(ctx, region, depth, step);
            return land != null && land.Area < region.Area * 0.98f ? land : region;
        }

        public static Shoreline Analyze(LayerContext ctx, LayerRegion region, string side)
        {
            var s = new Shoreline { Outline = region, Land = region };
            if (ctx.Site?.WaterDepthAt != null) s._depthAt = ctx.Site.WaterDepthAt;
            s._ground = ctx.H;
            bool auto = string.IsNullOrEmpty(side) || side == "auto";
            // s20: one robust water level (median of clearly wet samples around the area, not the mean over the
            // blurred margin) and, when the ground agrees with it, a waterline read from the terrain heights.
            s.WaterLevel = RobustLevel(ctx, region, 40f, out bool consistent);
            s._levelWaterline = math.isfinite(s.WaterLevel) && consistent;
            Func<float2, float> depth = p => s.IsWet(p) ? 1f : 0f;

            // 1. Water inside the outline: keep the dry part.
            var size = region.Max - region.Min;
            float step = math.max(3f, math.sqrt(size.x * size.y / 2500f));
            int wet = 0, total = 0; double level = 0;
            for (float y = region.Min.y + step * 0.5f; y < region.Max.y; y += step)
            {
                ctx.Check();
                for (float x = region.Min.x + step * 0.5f; x < region.Max.x; x += step)
                {
                    var p = new float2(x, y);
                    if (!region.Contains(p)) continue;
                    total++;
                    if (s.IsWet(p)) { wet++; level += ctx.H(p) + s._depthAt(p); }
                }
            }
            if (wet > 0 && !math.isfinite(s.WaterLevel)) s.WaterLevel = (float)(level / wet);
            if (wet > 0 && wet >= total * 0.02f)
            {
                var land = DryPart(ctx, region, depth, step);
                if (land != null && land.Area < region.Area * 0.98f)
                {
                    s.Land = land;
                    s.Mode = ShoreMode.Inside;
                    if (wet >= total * 0.5f) ctx.Issue(IssueSeverity.Info, WaterInsideKey);
                }
            }

            // 2. Shore edges of the land outline.
            var pts = Poly.Resample(s.Land.Ring, 3f, true);
            int n = pts.Length;
            if (n < 3) return null;
            var outN = new float2[n];
            var len = new float[n];
            var wetEdge = new bool[n];
            int wetCount = 0; double wetLevel = 0; float wetLen = 0;
            for (int i = 0; i < n; i++)
            {
                var a = pts[i]; var b = pts[(i + 1) % n];
                var dir = math.normalizesafe(b - a, new float2(1, 0));
                outN[i] = new float2(dir.y, -dir.x);
                len[i] = math.distance(a, b);
                var m = (a + b) * 0.5f;
                foreach (float reach in new[] { 1.5f, 5f, 12f, 22f, 34f })
                {
                    var q = m + outN[i] * reach;
                    float d = s._depthAt(q);
                    if (s.IsWet(q)) { wetEdge[i] = true; wetCount++; wetLevel += ctx.H(q) + d; wetLen += len[i]; break; }
                }
            }
            if (wetCount > 0 && float.IsNaN(s.WaterLevel)) s.WaterLevel = (float)(wetLevel / wetCount);

            bool[] flags;
            bool compass = !auto && side != "awayFromRoad";
            if (s.Mode == ShoreMode.Inside || (!compass && wetLen >= 8f))
            {
                flags = wetEdge;
                if (s.Mode != ShoreMode.Inside) s.Mode = ShoreMode.Edge;
            }
            else
            {
                var w = Direction(ctx, s.Land, side, out bool fromRoad);
                flags = new bool[n];
                int bestI = 0; float bestDot = float.MinValue;
                for (int i = 0; i < n; i++)
                {
                    float dot = math.dot(outN[i], w);
                    flags[i] = dot > 0.4f;
                    if (dot > bestDot) { bestDot = dot; bestI = i; }
                }
                flags[bestI] = true;
                s.Mode = wetLen >= 8f ? ShoreMode.Edge : ShoreMode.Assumed;
                if (auto || (side == "awayFromRoad" && !fromRoad)) ctx.Issue(IssueSeverity.Info, NoWaterKey);
            }
            CloseGaps(flags, len, 10f);

            // 3. Longest run of shore edges (cyclic).
            int start = 0, count = 0;
            bool all = true;
            foreach (var f in flags) if (!f) { all = false; break; }
            if (all) { s.Closed = true; start = 0; count = n; }
            else
            {
                float best = -1f;
                for (int i = 0; i < n; i++)
                {
                    if (!flags[i] || flags[(i - 1 + n) % n]) continue;
                    float l = 0; int c = 0;
                    for (int k = i; flags[k % n] && c < n; k++) { l += len[k % n]; c++; }
                    if (l > best) { best = l; start = i; count = c; }
                }
                if (count == 0) { start = 0; count = 1; }
            }
            var inChain = new bool[n];
            var chain = new float2[count + 1];
            var inward = new float2[count + 1];
            var waterDir = float2.zero;
            for (int k = 0; k < count; k++)
            {
                int e = (start + k) % n;
                inChain[e] = true;
                chain[k] = pts[e];
                waterDir += outN[e] * len[e];
            }
            chain[count] = pts[(start + count) % n];
            for (int k = 0; k <= count; k++)
            {
                int ePrev = (start + k - 1 + n) % n, eNext = (start + k) % n;
                float2 nrm = float2.zero;
                if (k > 0 || s.Closed) nrm -= outN[ePrev];
                if (k < count || s.Closed) nrm -= outN[eNext];
                inward[k] = math.normalizesafe(nrm, -outN[eNext % n]);
            }
            // Smooth the normals so offsets of a jagged painted outline stay calm.
            for (int pass = 0; pass < 6; pass++)
            {
                var tmp = (float2[])inward.Clone();
                for (int k = 0; k <= count; k++)
                {
                    int a = k - 1, b = k + 1;
                    if (s.Closed) { a = (a + count) % count; b = b % count; }
                    else { a = math.max(0, a); b = math.min(count, b); }
                    tmp[k] = math.normalizesafe(inward[a] * 0.25f + inward[k] * 0.5f + inward[b] * 0.25f, inward[k]);
                }
                inward = tmp;
            }
            s.Chain = chain;
            s.Inward = inward;
            s.Along = new float[count + 1];
            for (int k = 1; k <= count; k++) s.Along[k] = s.Along[k - 1] + math.distance(chain[k - 1], chain[k]);
            s.Length = s.Along[count];
            s.WaterDir = math.normalizesafe(waterDir, new float2(0, -1));
            for (int k = 0; k < count; k++) s._chain.Add(chain[k], chain[k + 1], 0f, k);
            for (int i = 0; i < n; i++) if (!inChain[i]) s._other.Add(pts[i], pts[(i + 1) % n], 0f, i);

            // Deepest point from the shore (grid sample).
            float md = 0f;
            float st = math.max(2f, math.sqrt(s.Land.Area / 1500f));
            for (float y = s.Land.Min.y + st * 0.5f; y < s.Land.Max.y; y += st)
                for (float x = s.Land.Min.x + st * 0.5f; x < s.Land.Max.x; x += st)
                {
                    var p = new float2(x, y);
                    if (s.Land.Contains(p)) md = math.max(md, s.Depth(p));
                }
            s.MaxDepth = md;
            s.MeasureWaterline(ctx);
            ctx.Plan.Facts["shoreLength"] = s.Length;
            ctx.Plan.Facts["shoreMode"] = (int)s.Mode;
            ctx.Plan.Facts["shoreCliffShare"] = s.CliffShare;
            ctx.Plan.Facts["shoreWaterline"] = s.HasWaterline ? 1 : 0;
            if (math.isfinite(s.WaterLevel)) ctx.Plan.Facts["shoreWaterLevel"] = s.WaterLevel;   // s30: for the terrain dump and gallery
            return s;
        }

        // ───────────── the real waterline and the bank profile (s20) ─────────────

        /// <summary>How far past the land's shore edge the real water is searched for (m).</summary>
        public const float MaxGap = 45f;

        /// <summary>Per chain point: distance from the land's shore edge out to the real waterline (m, smoothed); NaN = no water found.</summary>
        public float[] Gap;
        /// <summary>Per chain point: what the bank rising from the water is like there.</summary>
        public ShoreKind[] Kinds;
        /// <summary>Length shares of cliff and low shore along the chain.</summary>
        public float CliffShare, LowShare;
        public bool HasWaterline;

        /// <summary>
        /// Finds the real waterline in front of every chain point (probe seawards, then bisect the wet/dry change) and
        /// classifies the bank behind it from the terrain profile: low (beach-able), bank, or cliff. Players never draw
        /// exactly on the water, and the old 12 m probe plus the 16 m water grid left beaches 10–25 m short of it.
        /// </summary>
        private void MeasureWaterline(LayerContext ctx)
        {
            int n = Chain.Length;
            Gap = new float[n];
            Kinds = new ShoreKind[n];
            for (int k = 0; k < n; k++)
            {
                Gap[k] = float.NaN;
                if (Mode == ShoreMode.Assumed) continue;
                var o = -Inward[k];
                if (IsWet(Chain[k] + o * 0.25f)) { Gap[k] = 0f; continue; }
                float prev = 0f;
                for (float r = 1f; r <= MaxGap; r += 1f)
                {
                    if (!IsWet(Chain[k] + o * r)) { prev = r; continue; }
                    float lo = prev, hi = r;
                    for (int it = 0; it < 5; it++)
                    {
                        float mid = (lo + hi) * 0.5f;
                        if (IsWet(Chain[k] + o * mid)) hi = mid; else lo = mid;
                    }
                    Gap[k] = (lo + hi) * 0.5f;
                    break;
                }
            }
            // Smooth the found gaps along the chain (a coarse snapshot gives a ragged line).
            for (int pass = 0; pass < 3; pass++)
            {
                var t = (float[])Gap.Clone();
                for (int k = 0; k < n; k++)
                {
                    if (!math.isfinite(Gap[k])) continue;
                    float sum = Gap[k] * 2f, c = 2f;
                    for (int d = -2; d <= 2; d++)
                    {
                        if (d == 0) continue;
                        int m = k + d;
                        if (Closed) m = (m + n) % n;
                        if (m < 0 || m >= n || !math.isfinite(Gap[m])) continue;
                        sum += Gap[m]; c += 1f;
                    }
                    t[k] = sum / c;
                }
                Gap = t;
            }
            float cliff = 0f, low = 0f, total = 0f;
            for (int k = 0; k < n; k++)
            {
                HasWaterline |= math.isfinite(Gap[k]);
                Kinds[k] = Classify(ctx, WaterPointAt(k), Inward[k]);
            }
            // Majority filter (±2 points ≈ ±6 m) so a single odd sample does not flip the kind.
            var kinds = (ShoreKind[])Kinds.Clone();
            for (int k = 0; k < n; k++)
            {
                var votes = new int[3];
                for (int d = -2; d <= 2; d++)
                {
                    int m = math.clamp(k + d, 0, n - 1);
                    votes[(int)Kinds[m]]++;
                }
                int best = (int)Kinds[k];
                for (int v = 0; v < 3; v++) if (votes[v] > votes[best]) best = v;
                kinds[k] = (ShoreKind)best;
            }
            Kinds = kinds;
            for (int k = 0; k + 1 < n; k++)
            {
                float l = Along[k + 1] - Along[k];
                total += l;
                if (Kinds[k] == ShoreKind.Cliff) cliff += l;
                if (Kinds[k] == ShoreKind.Low) low += l;
            }
            CliffShare = total > 0f ? cliff / total : 0f;
            LowShare = total > 0f ? low / total : 0f;
        }

        /// <summary>Bank profile from the waterline inland: rise within 12 / 24 m and the steepest 4 m stretch.</summary>
        private ShoreKind Classify(LayerContext ctx, float2 w, float2 inw)
        {
            float l0 = math.isfinite(WaterLevel) ? WaterLevel : ctx.H(w);
            float rise12 = 0f, rise24 = 0f, steep = 0f;
            float prev = ctx.H(w);
            for (float t = 2f; t <= 24f; t += 2f)
            {
                float h = ctx.H(w + inw * t);
                if (t <= 12f) rise12 = math.max(rise12, h - l0);
                rise24 = math.max(rise24, h - l0);
                if (t >= 4f) steep = math.max(steep, (h - ctx.H(w + inw * (t - 4f))) / 4f);
                prev = h;
            }
            if (rise12 >= 5f || (steep >= 0.9f && rise24 >= 4f)) return ShoreKind.Cliff;
            if (rise24 >= 2.5f || steep >= 0.35f) return ShoreKind.Bank;
            return ShoreKind.Low;
        }

        private float2 WaterPointAt(int k) => Chain[k] - Inward[k] * (math.isfinite(Gap[k]) ? Gap[k] : 0f);

        /// <summary>(s30) Index k of the chain segment holding arc length s (binary search; was a linear scan per call).</summary>
        private int SegmentOf(float s)
        {
            int lo = 0, hi = Chain.Length - 2;
            if (hi <= 0) return 0;
            while (lo < hi)
            {
                int m = (lo + hi) >> 1;
                if (Along[m + 1] < s) lo = m + 1; else hi = m;
            }
            return lo;
        }

        private int Segment(float s, out float t)
        {
            s = math.clamp(s, 0f, Length);
            int k = SegmentOf(s);
            float seg = math.max(1e-5f, Along[k + 1] - Along[k]);
            t = math.saturate((s - Along[k]) / seg);
            return k;
        }

        /// <summary>Distance from the land's shore edge to the real water at arc length s (0 where unknown).</summary>
        public float GapAt(float s)
        {
            if (Gap == null) return 0f;
            int k = Segment(s, out float t);
            float a = math.isfinite(Gap[k]) ? Gap[k] : 0f, b = math.isfinite(Gap[k + 1]) ? Gap[k + 1] : a;
            if (!math.isfinite(Gap[k])) a = b;
            return math.lerp(a, b, t);
        }

        public ShoreKind KindAt(float s)
        {
            if (Kinds == null) return ShoreKind.Low;
            int k = Segment(s, out float t);
            return t < 0.5f ? Kinds[k] : Kinds[k + 1];
        }

        /// <summary>
        /// Signed distance from the REAL waterline (positive inland, negative out over the water): the land's shore
        /// distance plus the gap to the water in front of it. Beach and waterfront profiles are laid out by this.
        /// </summary>
        public float FromWater(float2 p)
        {
            var q = Nearest(p, out var inw, out float along);
            float d = math.distance(p, q);
            return (Land.Contains(p) || math.dot(p - q, inw) >= -0.01f ? d : -d) + GapAt(along);
        }

        /// <summary>The real waterline point in front of arc length s.</summary>
        public float2 WaterAt(float s, out float2 inward)
        {
            var c = At(s, out inward, out _);
            return c - inward * GapAt(s);
        }

        /// <summary>The real waterline as a polyline (resampled ~2 m), or the chain when no water was found.</summary>
        public float2[] WaterEdge(float spacing = 2f)
        {
            int steps = math.max(1, (int)math.ceil(Length / spacing));
            var pts = new float2[steps + 1];
            for (int i = 0; i <= steps; i++) pts[i] = WaterAt(Length * i / steps, out _);
            return pts;
        }

        /// <summary>
        /// Region minus the wet cells. The water is traced on a slightly enlarged outline (the raster
        /// keeps 1.5 cells off its own border), so the wet pieces are exact at the waterline and reach
        /// past the real outline; the land keeps its real edges everywhere else.
        /// </summary>
        private static LayerRegion DryPart(LayerContext ctx, LayerRegion region, Func<float2, float> depth, float step)
        {
            try
            {
                float cell = math.max(1.5f, step * 0.5f);
                var grownOutline = Poly.Offset(region.Ring, -cell * 2f);
                var traced = grownOutline.Count > 0 ? LayerRegion.From(grownOutline[0]) : null;
                if (traced == null) return null;
                var wetPieces = FreeSpace.Pieces(traced, p => depth(p) <= WetDepth, cell, 30f, ctx.Cancel);
                if (wetPieces.Count == 0) return null;
                var wet = new List<IReadOnlyList<float2>>();
                foreach (var w in wetPieces) wet.Add(w);
                var dry = Poly.Boolean(new[] { region.Ring }, wet, BoolOp.Difference);
                LayerRegion best = null;
                foreach (var r in dry)
                    foreach (var ring in Poly.CutHoles(r, 1f))
                    {
                        var lr = LayerRegion.From(ring);
                        if (lr != null && (best == null || lr.Area > best.Area)) best = lr;
                    }
                return best != null && best.Area > 300f ? best : null;
            }
            catch (Exception) { return null; }
        }

        /// <summary>Direction towards the (assumed) water: compass choice, away from the road, else the longest edge.</summary>
        private static float2 Direction(LayerContext ctx, LayerRegion land, string side, out bool fromRoad)
        {
            fromRoad = false;
            switch (side)
            {
                case "north": return new float2(0, 1);
                case "east": return new float2(1, 0);
                case "south": return new float2(0, -1);
                case "west": return new float2(-1, 0);
            }
            var roads = Entrances.RoadSegments(ctx.Input);
            var c = land.Centroid;
            if (roads.Count > 0 && roads.Nearest(c, 1e5f, out var q) >= 0 && math.distancesq(q, c) > 1f)
            {
                fromRoad = true;
                return math.normalize(c - q);
            }
            int best = 0; float bestLen = -1f;
            for (int i = 0; i < land.Ring.Length; i++)
            {
                float l = math.distance(land.Ring[i], land.Ring[(i + 1) % land.Ring.Length]);
                if (l > bestLen + 0.01f) { bestLen = l; best = i; }
            }
            return -land.InwardNormal(best);
        }

        /// <summary>Flags short unflagged runs between flagged edges (a jetty notch, a sampling miss).</summary>
        private static void CloseGaps(bool[] flags, float[] len, float maxGap)
        {
            int n = flags.Length;
            bool any = false, none = true;
            foreach (var f in flags) { any |= f; none &= f; }
            if (!any || none) return;
            for (int i = 0; i < n; i++)
            {
                if (flags[i] || !flags[(i - 1 + n) % n]) continue;
                float l = 0; int c = 0;
                while (!flags[(i + c) % n] && c < n) { l += len[(i + c) % n]; c++; }
                if (l <= maxGap) for (int k = 0; k < c; k++) flags[(i + k) % n] = true;
            }
        }

        // ───────────── queries ─────────────

        // (s30) Segment bounding boxes and a warm start for Nearest on the sculpt grid (2 m nodes: ~10–20 k queries per plan).
        private float4[] _segBox;
        private int _lastNearest;

        /// <summary>
        /// <see cref="Nearest"/> with pruning: starts from the previous query's segment (neighbouring grid nodes share it) and
        /// skips every segment whose bounding box is farther than the best hit. Same result.
        /// </summary>
        public float2 NearestFast(float2 p, out float2 inward, out float along)
        {
            int n = Chain.Length - 1;
            if (n < 1) { inward = Inward.Length > 0 ? Inward[0] : new float2(0, 1); along = 0f; return Chain.Length > 0 ? Chain[0] : p; }
            if (_segBox == null)
            {
                _segBox = new float4[n];
                for (int k = 0; k < n; k++) { var mn = math.min(Chain[k], Chain[k + 1]); var mx = math.max(Chain[k], Chain[k + 1]); _segBox[k] = new float4(mn, mx); }
            }
            float Dist(int k, out float t)
            {
                var a = Chain[k]; var ab = Chain[k + 1] - a; float l2 = math.lengthsq(ab);
                t = l2 > 1e-9f ? math.saturate(math.dot(p - a, ab) / l2) : 0f;
                return math.distancesq(p, a + ab * t);
            }
            int bk = math.clamp(_lastNearest, 0, n - 1);
            float best = Dist(bk, out float bt);
            for (int k = 0; k < n; k++)
            {
                var b = _segBox[k];
                float dx = math.max(0f, math.max(b.x - p.x, p.x - b.z)), dy = math.max(0f, math.max(b.y - p.y, p.y - b.w));
                if (dx * dx + dy * dy > best) continue;
                float d = Dist(k, out float t);
                if (d < best || d == best && k < bk) { best = d; bk = k; bt = t; }
            }
            _lastNearest = bk;
            inward = math.normalizesafe(math.lerp(Inward[bk], Inward[bk + 1], bt), Inward[bk]);
            along = Along[bk] + (Along[bk + 1] - Along[bk]) * bt;
            return Chain[bk] + (Chain[bk + 1] - Chain[bk]) * bt;
        }

        /// <summary>Closest shore point, its inland normal and arc length.</summary>
        public float2 Nearest(float2 p, out float2 inward, out float along)
        {
            if (Chain.Length >= 2) return NearestFast(p, out inward, out along);
            float best = float.MaxValue; float2 bestP = Chain[0]; inward = Inward[0]; along = 0f;
            for (int k = 0; k + 1 < Chain.Length; k++)
            {
                var a = Chain[k]; var b = Chain[k + 1];
                var ab = b - a; float l2 = math.lengthsq(ab);
                float t = l2 > 1e-9f ? math.saturate(math.dot(p - a, ab) / l2) : 0f;
                var q = a + ab * t;
                float d = math.distancesq(p, q);
                if (d < best)
                {
                    best = d; bestP = q;
                    inward = math.normalizesafe(math.lerp(Inward[k], Inward[k + 1], t), Inward[k]);
                    along = Along[k] + (Along[k + 1] - Along[k]) * t;
                }
            }
            return bestP;
        }

        /// <summary>Shore point at arc length s with its inland normal and unit tangent (land on the left).</summary>
        public float2 At(float s, out float2 inward, out float2 tangent)
        {
            s = math.clamp(s, 0f, Length);
            int k = SegmentOf(s);
            float seg = math.max(1e-5f, Along[k + 1] - Along[k]);
            float t = math.saturate((s - Along[k]) / seg);
            inward = math.normalizesafe(math.lerp(Inward[k], Inward[k + 1], t), Inward[k]);
            tangent = new float2(inward.y, -inward.x);
            return math.lerp(Chain[k], Chain[k + 1], t);
        }

        /// <summary>Arc lengths of <paramref name="count"/> evenly spread stations, <paramref name="endInset"/> metres from the ends.</summary>
        public List<float> Stations(int count, float endInset, float phase = 0.5f)
        {
            var r = new List<float>();
            float usable = Length - (Closed ? 0f : 2f * endInset);
            if (count <= 0 || usable <= 0f) return r;
            float o = Closed ? 0f : endInset;
            for (int i = 0; i < count; i++) r.Add(o + usable * (i + phase) / count);
            return r;
        }

        /// <summary>
        /// Lines parallel to the shore at depth <paramref name="d"/>: pieces that stay inside the land
        /// (margin) and are not closer to another part of the shore; smoothed, resampled at ~spacing.
        /// </summary>
        public List<float2[]> Offset(float d, float margin, float minLength, float spacing = 4f)
        {
            var result = new List<float2[]>();
            var run = new List<float2>();
            void Flush()
            {
                if (run.Count >= 2)
                {
                    var line = Smooth(run, 2);
                    float l = 0; for (int i = 1; i < line.Length; i++) l += math.distance(line[i - 1], line[i]);
                    if (l >= minLength) result.Add(Poly.ResamplePolyline(line, spacing));
                }
                run.Clear();
            }
            float stepS = math.max(1.5f, math.min(spacing, 3f));
            int steps = math.max(1, (int)math.ceil(Length / stepS));
            float2 prev = default; bool havePrev = false;
            for (int i = 0; i <= steps; i++)
            {
                var c = At(Length * i / steps, out var inw, out _);
                var q = c + inw * d;
                bool ok = Land.ContainsDisc(q, margin) && Depth(q, d + 2f) >= d - 1.2f;
                if (ok && havePrev && (math.distance(prev, q) > stepS * 3f + 2f || Land.CrossesBoundary(prev, q, false))) Flush();
                if (ok) { run.Add(q); prev = q; havePrev = true; }
                else { Flush(); havePrev = false; }
            }
            Flush();
            return result;
        }

        /// <summary>
        /// (s20) Lines parallel to the REAL waterline at <paramref name="dw"/> m inland of it (FromWater): pieces inside the
        /// land (margin) and not closer to another part of the water; smoothed, resampled at ~spacing. Beach and waterfront
        /// rows are measured from the water, not from wherever the player's outline happens to run. A negative margin: the
        /// line need not lie on the land (quays and boardwalks at the water; the reject predicate decides).
        /// </summary>
        public List<float2[]> OffsetFromWater(float dw, float margin, float minLength, float spacing = 4f, Func<float2, bool> reject = null)
        {
            var result = new List<float2[]>();
            float stepS = math.max(1.5f, math.min(spacing, 3f));
            int steps = math.max(1, (int)math.ceil(Length / stepS));
            // 1. The raw parallel curve over the whole shore; 2. loops cut out (an offset wider than a bend's radius
            // folds over itself — playtest 28's "eye" island); 3. smoothed; 4. split where it leaves the land or is
            // rejected, short interruptions bridged so a walk stays ONE line.
            var raw = new List<float2>(steps + 1);
            for (int i = 0; i <= steps; i++)
            {
                var w = WaterAt(Length * i / steps, out var inw);
                raw.Add(w + inw * dw);
            }
            var line = RemoveLoops(raw);
            line = new List<float2>(Smooth(line, 4));
            line = RemoveLoops(line);
            var ok = new bool[line.Count];
            for (int i = 0; i < line.Count; i++)
                ok[i] = (margin < 0f || Land.ContainsDisc(line[i], margin)) && FromWater(line[i]) >= dw * 0.6f - 1f - math.max(0f, -dw) * 0.5f && (reject == null || !reject(line[i]));
            // Bridge gaps of up to ~9 m whose chord stays inside the land.
            for (int i = 0; i < line.Count; i++)
            {
                if (ok[i] || i == 0 || !ok[i - 1]) continue;
                int j = i;
                while (j < line.Count && !ok[j]) j++;
                if (j >= line.Count) break;
                float gap = 0f;
                for (int k = i; k <= j; k++) gap += math.distance(line[k - 1], line[k]);
                if (gap <= 9f && (margin < 0f || !Land.CrossesBoundary(line[i - 1], line[j], false) && Land.ContainsDisc(math.lerp(line[i - 1], line[j], 0.5f), margin * 0.5f)))
                    for (int k = i; k < j; k++) ok[k] = true;
                i = j;
            }
            var run = new List<float2>();
            void Flush()
            {
                if (run.Count >= 2)
                {
                    var sm = Smooth(run, 2);
                    float l = 0; for (int i = 1; i < sm.Length; i++) l += math.distance(sm[i - 1], sm[i]);
                    if (l >= minLength) result.Add(Poly.ResamplePolyline(sm, spacing));
                }
                run.Clear();
            }
            for (int i = 0; i < line.Count; i++)
            {
                if (ok[i] && margin >= 0f && run.Count > 0 && Land.CrossesBoundary(run[run.Count - 1], line[i], false)) Flush();
                if (ok[i]) run.Add(line[i]); else Flush();
            }
            Flush();
            return result;
        }

        /// <summary>
        /// (s20) Cuts the loops out of a polyline: wherever two non-adjacent segments cross, the points between are dropped
        /// and the crossing point kept. A parallel offset of a tight bend never folds into an "eye" island again.
        /// </summary>
        public static List<float2> RemoveLoops(IReadOnlyList<float2> pts)
        {
            var p = new List<float2>(pts);
            for (int guard = 0; guard < 64; guard++)
            {
                bool cut = false;
                for (int i = 0; i + 1 < p.Count && !cut; i++)
                    for (int j = p.Count - 2; j > i + 1; j--)
                    {
                        if (!Poly.SegmentIntersection(p[i], p[i + 1], p[j], p[j + 1], out var x, out _, out _)) continue;
                        p.RemoveRange(i + 1, j - i);
                        p.Insert(i + 1, x);
                        cut = true;
                        break;
                    }
                if (!cut) break;
            }
            return p;
        }

        /// <summary>(s20) Number of crossings between non-adjacent segments of a polyline (0 = simple).</summary>
        public static int SelfIntersections(IReadOnlyList<float2> p)
        {
            int n = 0;
            for (int i = 0; i + 1 < p.Count; i++)
                for (int j = i + 2; j + 1 < p.Count; j++)
                    if (Poly.SegmentsIntersect(p[i], p[i + 1], p[j], p[j + 1])) n++;
            return n;
        }

        /// <summary>(s20) The land between dw0 and dw1 m from the real waterline (StripFromWater ∩ land), simple CCW pieces.</summary>
        public List<float2[]> BandFromWater(float dw0, float dw1)
        {
            var result = new List<float2[]>();
            foreach (var strip in StripFromWater(dw0, dw1, 0f, -1f, 4f))
                try
                {
                    foreach (var r in Poly.Boolean(new[] { (IReadOnlyList<float2>)strip }, new[] { (IReadOnlyList<float2>)Land.Ring }, BoolOp.Intersection, FillRule.Positive))
                        foreach (var piece in Poly.CutHoles(r, 0.5f))
                        {
                            var clean = Poly.Clean(piece, 0.3f);
                            if (clean.Length >= 3 && Poly.Area(clean) > 8f) result.Add(clean);
                        }
                }
                catch (Exception) { }
            return result;
        }

        /// <summary>
        /// (s20) The strip between dw0 and dw1 m from the real waterline (negative = out over the water), NOT clipped to
        /// the land: one simple ring per run of stations [fromAlong, toAlong]. Callers clip (paint reaches into the water).
        /// </summary>
        public List<float2[]> StripFromWater(float dw0, float dw1, float fromAlong = 0f, float toAlong = -1f, float step = 2f)
        {
            var result = new List<float2[]>();
            if (toAlong < 0f) toAlong = Length;
            if (dw1 <= dw0 + 0.5f || toAlong - fromAlong < 1f) return result;
            int steps = math.max(2, (int)math.ceil((toAlong - fromAlong) / math.max(1f, step)));
            var outer = new List<float2>(steps + 1);
            var inner = new List<float2>(steps + 1);
            for (int i = 0; i <= steps; i++)
            {
                var w = WaterAt(fromAlong + (toAlong - fromAlong) * i / steps, out var inw);
                outer.Add(w + inw * dw0);
                inner.Add(w + inw * dw1);
            }
            var ring = new List<float2>(outer);
            for (int i = inner.Count - 1; i >= 0; i--) ring.Add(inner[i]);
            try
            {
                foreach (var r in Poly.UnionAll(new[] { (IReadOnlyList<float2>)Poly.Clean(ring, 0.3f) }))
                    foreach (var piece in Poly.CutHoles(r, 0.5f))
                    {
                        var clean = Poly.Clean(piece, 0.3f);
                        if (clean.Length >= 3 && Poly.Area(clean) > 8f) result.Add(clean);
                    }
            }
            catch (Exception) { }
            return result;
        }

        /// <summary>Simple CCW pieces of the land between depths d0 and d1 (d0 ≤ 0 reaches the shore).</summary>
        public List<float2[]> Band(float d0, float d1, float fromAlong = 0f, float toAlong = -1f)
        {
            var result = new List<float2[]>();
            if (toAlong < 0f) toAlong = Length;
            if (d1 <= d0 + 0.5f || toAlong - fromAlong < 1f) return result;
            if (d0 <= 0.01f) d0 = -3f;
            int steps = math.max(2, (int)math.ceil((toAlong - fromAlong) / 2f));
            var ring = new List<float2>(steps * 2 + 2);
            var inner = new List<float2>(steps + 1);
            for (int i = 0; i <= steps; i++)
            {
                var c = At(fromAlong + (toAlong - fromAlong) * i / steps, out var inw, out _);
                ring.Add(c + inw * d0);
                inner.Add(c + inw * d1);
            }
            for (int i = inner.Count - 1; i >= 0; i--) ring.Add(inner[i]);
            try
            {
                foreach (var r in Poly.Boolean(new[] { (IReadOnlyList<float2>)ring }, new[] { (IReadOnlyList<float2>)Land.Ring }, BoolOp.Intersection, FillRule.Positive))
                    foreach (var piece in Poly.CutHoles(r, 0.5f))
                    {
                        var clean = Poly.Clean(piece, 0.3f);
                        if (clean.Length >= 3 && Poly.Area(clean) > 8f) result.Add(clean);
                    }
            }
            catch (Exception) { }
            return result;
        }

        private static float2[] Smooth(List<float2> pts, int passes)
        {
            var a = pts.ToArray();
            for (int p = 0; p < passes; p++)
            {
                var b = (float2[])a.Clone();
                for (int i = 1; i + 1 < a.Length; i++) b[i] = a[i - 1] * 0.25f + a[i] * 0.5f + a[i + 1] * 0.25f;
                a = b;
            }
            return a;
        }

        // ───────────── polylines → paths ─────────────

        /// <summary>Point at arc length s along a polyline, with its unit tangent.</summary>
        public static float2 PointOn(float2[] line, float s, out float2 tangent)
        {
            for (int i = 1; i < line.Length; i++)
            {
                float l = math.distance(line[i - 1], line[i]);
                if (s <= l || i == line.Length - 1)
                {
                    tangent = math.normalizesafe(line[i] - line[i - 1], new float2(1, 0));
                    return line[i - 1] + tangent * math.clamp(s, 0f, l);
                }
                s -= l;
            }
            tangent = new float2(1, 0);
            return line[0];
        }

        public static float LengthOf(float2[] line)
        {
            float l = 0; for (int i = 1; i < line.Length; i++) l += math.distance(line[i - 1], line[i]);
            return l;
        }

        /// <summary>
        /// Smooth Bézier courses through a polyline (knots every ~knotSpacing m, Catmull-Rom tangents).
        /// A course that strays from the polyline or the land falls back to straight pieces.
        /// </summary>
        public static List<PathCurve> Curves(LayerRegion land, float2[] line, NetRole role, float knotSpacing = 24f)
        {
            var curves = new List<PathCurve>();
            if (line == null || line.Length < 2) return curves;
            float hw = Composer.DefaultHalfWidth(role);
            float avg = LengthOf(line) / math.max(1, line.Length - 1);
            int stride = math.max(1, (int)math.round(knotSpacing / math.max(0.5f, avg)));
            var knots = new List<int>();
            for (int i = 0; i < line.Length - 1; i += stride) knots.Add(i);
            if (knots[knots.Count - 1] != line.Length - 1)
            {
                if (line.Length - 1 - knots[knots.Count - 1] < stride / 3 && knots.Count > 1) knots[knots.Count - 1] = line.Length - 1;
                else knots.Add(line.Length - 1);
            }
            float2 Tan(int i)
            {
                int a = math.max(0, i - 1), b = math.min(line.Length - 1, i + 1);
                return math.normalizesafe(line[b] - line[a], new float2(1, 0));
            }
            for (int k = 0; k + 1 < knots.Count; k++)
            {
                int i0 = knots[k], i1 = knots[k + 1];
                var a = line[i0]; var d = line[i1];
                float l = math.distance(a, d);
                if (l < 0.8f) continue;
                var c = new PathCurve { A = a, B = a + Tan(i0) * l / 3f, C = d - Tan(i1) * l / 3f, D = d, Role = role, HalfWidth = hw };
                ParkPaths.Sample(c);
                if (Follows(land, c, line, i0, i1)) { curves.Add(c); continue; }
                // Straight pieces through the original points.
                for (int i = i0; i < i1; i++)
                {
                    var p = line[i]; var q = line[i + 1];
                    if (math.distance(p, q) < 0.5f) continue;
                    var s = new PathCurve { A = p, B = math.lerp(p, q, 1f / 3f), C = math.lerp(p, q, 2f / 3f), D = q, Role = role, HalfWidth = hw };
                    ParkPaths.Sample(s);
                    curves.Add(s);
                }
            }
            return curves;
        }

        private static bool Follows(LayerRegion land, PathCurve c, float2[] line, int i0, int i1)
        {
            for (int i = 0; i < c.Samples.Length; i++)
            {
                var p = c.Samples[i];
                if (!land.Contains(p)) return false;
                if (i > 0 && land.CrossesBoundary(c.Samples[i - 1], p, false)) return false;
                float best = float.MaxValue;
                for (int k = i0; k < i1; k++) best = math.min(best, SegmentIndex.DistSq(p, line[k], line[k + 1]));
                if (best > 1.2f * 1.2f) return false;
            }
            return true;
        }
    }
}
