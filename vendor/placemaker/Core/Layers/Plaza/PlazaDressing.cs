// Plaza depth (playtest 2: "just a concrete slab with a thing in the middle"; playtest 3: "a flat grey
// slab with a small fountain and a ring of benches — boring"). Art pass a2: every square is now a
// composition — a paved ground laid as a tiling of real stones (PavingKit: frame band, rondel round the
// focal point, axes from the entrances, pattern bands, tree pits), planting cut out of that ground
// (parterre lawns, flower beds, grove islands, grass edge), and furniture that faces something
// (benches round the focal point, under the trees looking into the square, along the lawns).
//
//   Levels       terraced up / down, sunken, raised (TerrainShaper, permanent, blended at the outline);
//                ramps = short paths from each access point inward; pots line the level edges.
//   Amphitheatre stage at the middle of the longest edge, bench rows on arcs facing it, aisles,
//                rows optionally raised (bowl terraform limited to the seating sector).
//   Green        parterre lawns, flower beds, grove islands, trees (edge allée / bosque grid / groves),
//                grass or gravel edge.
//   Water        reflecting pool (water-look surface; real water only on flat ground, opt-in), fountains.
//   Shade        pergolas.  Activity: cafe terraces, market stalls, games tables, performance spot.
//   Identity     centrepiece kind, paving pattern (surface bands), feature lighting, bollards.

using System;
using System.Collections.Generic;
using Placemaker.Core.Geometry;
using Placemaker.Core.Layers.Common;
using Unity.Mathematics;

namespace Placemaker.Core.Layers.Plaza
{
    internal static class PlazaDressing
    {
        private const string K = "Placemaker.Plaza.";

        // ───────────── frame & palette ─────────────

        /// <summary>
        /// The square's own axis: the direction carrying the most outline length (mod 90°). A square's
        /// principal axes are degenerate (diagonal for a perfect square); its edges are not.
        /// </summary>
        public static float2 SquareAxis(LayerRegion region)
        {
            const int bins = 36;
            var w = new float[bins];
            var ring = region.Ring;
            for (int i = 0; i < ring.Length; i++)
            {
                var d = ring[(i + 1) % ring.Length] - ring[i];
                float len = math.length(d);
                if (len < 0.01f) continue;
                float a = math.atan2(d.y, d.x);
                float m = a - math.floor(a / (math.PI * 0.5f)) * (math.PI * 0.5f);   // 0..90°
                int b = math.clamp((int)(m / (math.PI * 0.5f) * bins), 0, bins - 1);
                w[b] += len; w[(b + 1) % bins] += len * 0.5f; w[(b + bins - 1) % bins] += len * 0.5f;
            }
            int best = 0;
            for (int i = 1; i < bins; i++) if (w[i] > w[best]) best = i;
            // The exact direction of the longest edge in the winning bin (the bins only choose).
            float bestLen = -1f, ang = (best + 0.5f) / bins * math.PI * 0.5f;
            for (int i = 0; i < ring.Length; i++)
            {
                var d = ring[(i + 1) % ring.Length] - ring[i];
                float len = math.length(d);
                if (len < 0.01f) continue;
                float a = math.atan2(d.y, d.x);
                float m = a - math.floor(a / (math.PI * 0.5f)) * (math.PI * 0.5f);
                int b = math.clamp((int)(m / (math.PI * 0.5f) * bins), 0, bins - 1);
                if (math.min(math.abs(b - best), bins - math.abs(b - best)) <= 1 && len > bestLen) { bestLen = len; ang = m; }
            }
            return new float2(math.cos(ang), math.sin(ang));
        }

        /// <summary>The square's half extents along <paramref name="axis"/> / its normal, measured from <paramref name="c"/>.</summary>
        public static void Extents(LayerRegion region, float2 c, float2 axis, out float minA, out float maxA, out float minB, out float maxB)
        {
            var side = new float2(-axis.y, axis.x);
            minA = minB = float.MaxValue; maxA = maxB = float.MinValue;
            foreach (var v in region.Ring)
            {
                var d = v - c;
                minA = math.min(minA, math.dot(d, axis)); maxA = math.max(maxA, math.dot(d, axis));
                minB = math.min(minB, math.dot(d, side)); maxB = math.max(maxB, math.dot(d, side));
            }
        }

        /// <summary>
        /// (s18) The square's ground palette: the "palette" control, Automatic = <see cref="AutoPalette"/>, with the
        /// player's per-role surface picks on top ("surface" = the field).
        /// </summary>
        public static PavingPalette Palette(LayerContext ctx) => GroundPalettes.Resolve(ctx.Settings, AutoPalette(ctx), "surface");

        /// <summary>
        /// What Automatic means for a square: a pre-s18 save's paving style (warm → terracotta, so it looks as it
        /// did), else the square's character — calm stone by default, never the orange/ochre look.
        /// </summary>
        public static string AutoPalette(LayerContext ctx)
        {
            switch (ctx.S("paveStyle"))
            {
                case "civic": return GroundPalettes.Classic;
                case "warm": return GroundPalettes.Terracotta;
                case "garden": return GroundPalettes.Garden;
                case "modern": return GroundPalettes.Modern;
                case "cobble": return GroundPalettes.Cobble;
            }
            string levels = ctx.S("levels");
            if (ctx.F("lawns") >= 15f) return GroundPalettes.Garden;
            if (ctx.I("stalls") > 0) return GroundPalettes.Cobble;
            if (ctx.I("cafe") > 0) return GroundPalettes.BrickStone;
            if (!string.IsNullOrEmpty(levels) && levels != "flat") return GroundPalettes.Modern;
            if (ctx.S("centerKind") == "fountain") return GroundPalettes.Sandstone;
            return GroundPalettes.Classic;
        }

        public static float FrameWidth(LayerRegion region)
        {
            region.DeepestPoint(out float ins, null, 12);
            return math.clamp(ins * 0.06f, 1.6f, 3f);
        }

        // ───────────── ground ─────────────

        /// <summary>
        /// The paved design (claims in this order: tree pits, rondel, its ring, entrance axes, frame band,
        /// pattern). Planting/water elements added to <paramref name="pave"/> earlier claim before all of it.
        /// </summary>
        public static void Ground(LayerContext ctx, PavingPlan pave, LayerRegion region, List<Gate> gates, float2 centre, float rondel,
            PavingPalette p, List<float2> pits, float2 axis, bool axes)
        {
            float fw = FrameWidth(region);
            foreach (var t in pits)
                pave.Add(LayerRegion.Rect(t, axis, 1.1f, 1.1f), SurfaceRole.Plaza, p.Inlay, AssetCategories.SurfacePaving);
            // s18 fit rules: every ring round the focal point stays a clear metre inside the frame band and inside the
            // border inlay line, so nothing cuts across anything (playtest: rings overlapping each other and the border).
            float depth = region.Contains(centre) ? region.BoundaryDistance(centre) : 0f;
            float lineInset = fw + 2.4f;
            bool borderLine = ctx.S("pattern") == "border";
            float outer = 0f, outerRingAt = 0f;
            if (rondel >= 2f)
            {
                float limit = depth - fw - 1f;
                bool ring = rondel + 1.2f <= limit;
                bool outerRing = ring && rondel >= 6f && rondel + 4.1f <= limit;
                if (borderLine && outerRing && rondel + 4.1f > depth - lineInset - 0.8f - 1f) outerRing = false;
                float pad = math.min(rondel, limit);
                if (pad >= 2f)
                {
                    float phase = math.atan2(axis.y, axis.x);
                    pave.Add(PavingShapes.Polygon(centre, pad, 40, phase), SurfaceRole.Plaza, p.Focal, AssetCategories.SurfacePaving);
                    outer = pad;
                    if (ring) { pave.Add(PavingShapes.Annulus(centre, pad, 1.2f), SurfaceRole.Plaza, p.Band, AssetCategories.SurfacePaving); outer = pad + 1.2f; }
                    if (outerRing) outerRingAt = pad + 3.2f;
                }
            }
            float walkEnd = outer;
            if (outerRingAt > 0f) outer = outerRingAt + 0.9f;
            // The border line only where the rondel leaves it whole.
            if (borderLine && outer > 0f && outer + 1f > depth - lineInset - 0.8f) borderLine = false;
            if (axes && gates.Count > 0)
            {
                // Walks from each entrance to the rondel's edge (laid before the outer ring, which they cut through).
                float aw = math.clamp(fw * 1.4f, 2.6f, 4f);
                bool kerbed = region.Area >= PlazaTiers.MediumArea;   // dark kerbs only on big squares: calmer below
                var kerbs = new List<float2[]>();
                var bands = new List<float2[]>();
                foreach (var g in gates)
                {
                    var inward = region.InwardNormal(g.Segment);
                    var a = g.Position - inward * 1f;
                    var dir = math.normalizesafe(centre - a, inward);
                    var b = walkEnd > 0f ? centre - dir * math.max(0f, walkEnd - 0.6f) : centre;
                    if (math.distance(a, b) < 4f) continue;
                    var side = new float2(-dir.y, dir.x);
                    if (kerbed)
                    {
                        var k1 = PavingShapes.Band(a + side * (aw * 0.5f + 0.3f), b + side * (aw * 0.5f + 0.3f), 0.6f);
                        var k2 = PavingShapes.Band(a - side * (aw * 0.5f + 0.3f), b - side * (aw * 0.5f + 0.3f), 0.6f);
                        if (k1 != null) kerbs.Add(k1);
                        if (k2 != null) kerbs.Add(k2);
                    }
                    var band = PavingShapes.Band(a, b, aw);
                    if (band != null) bands.Add(band);
                }
                pave.Add(kerbs, SurfaceRole.Plaza, p.Band, AssetCategories.SurfacePaving);
                pave.Add(bands, SurfaceRole.Plaza, p.Path, AssetCategories.SurfacePaving);
            }
            if (outerRingAt > 0f) pave.Add(PavingShapes.Annulus(centre, outerRingAt, 0.9f), SurfaceRole.Plaza, p.Inlay, AssetCategories.SurfacePaving);
            pave.Add(PavingShapes.Frame(region.Ring, fw), SurfaceRole.Plaza, p.Band, AssetCategories.SurfacePaving);

            switch (ctx.S("pattern"))
            {
                case "grid":
                    pave.Add(PavingShapes.Grid(region, centre, axis, math.max(12f, ctx.M(28f)), 0.8f), SurfaceRole.Plaza, p.Band, AssetCategories.SurfacePaving);
                    break;
                case "border":
                {
                    if (!borderLine) break;
                    var inner = PavingShapes.Inset(region.Ring, lineInset);
                    var line = new List<float2[]>();
                    foreach (var r in inner) line.AddRange(PavingShapes.Frame(r, 0.8f));
                    pave.Add(line, SurfaceRole.Plaza, p.Inlay, AssetCategories.SurfacePaving);
                    break;
                }
                case "scatter":
                {
                    // Large slabs laid as a loose chequer: every other cell of a 9 m lattice.
                    var side = new float2(-axis.y, axis.x);
                    float pitch = math.max(10f, ctx.M(28f));
                    float reach = math.length(region.Max - region.Min);
                    int n = (int)math.ceil(reach / pitch);
                    var tiles = new List<float2[]>();
                    for (int i = -n; i <= n; i++)
                        for (int j = -n; j <= n; j++)
                        {
                            if (((i + j) & 1) != 0) continue;
                            var c = centre + axis * (i * pitch) + side * (j * pitch);
                            if (math.distance(c, centre) < math.max(rondel, outer) + pitch * 0.32f + 1f || !region.ContainsDisc(c, fw + 1f + pitch * 0.32f)) continue;
                            tiles.Add(LayerRegion.Rect(c, axis, pitch * 0.22f, pitch * 0.22f));
                        }
                    pave.Add(tiles, SurfaceRole.Plaza, p.Accent, AssetCategories.SurfacePaving);
                    break;
                }
            }
        }

        // ───────────── levels ─────────────

        public static void Levels(LayerContext ctx, Occupancy occ, LayerRegion region, List<Gate> gates, NetRole ramps = NetRole.PathwayWide)
        {
            string mode = ctx.S("levels");
            if (mode == "flat" || string.IsNullOrEmpty(mode)) return;
            region.DeepestPoint(out float inscribed, null, 12);
            int count = ctx.I("levelCount");
            float falloff = math.clamp(inscribed * 0.25f, 4f, 16f);
            var spec = new TerrainSpec
            {
                Mode = mode == "sunken" ? TerrainMode.Sunken : mode == "raised" ? TerrainMode.Raised : TerrainMode.Terraces,
                Down = mode == "terracedDown", Height = ctx.F("levelHeight"), Levels = count, MaxSlopePct = 35f, Falloff = falloff,
                Seed = Hashing.Mix(ctx.Seed, 0x1E7Eu),
            };
            var result = TerrainShaper.Shape(ctx, region, spec);
            if (result.Cells == 0) return;
            // Ramps: a short path from each access point inward across the level edges.
            var ramp = AssetMix.One("");
            foreach (var g in gates)
            {
                var inward = region.InwardNormal(g.Segment);
                float len = math.min(inscribed * 0.7f, 28f);
                var end = g.Position + inward * len;
                if (!region.ContainsDisc(end, 1f)) continue;
                // s18: a ramp stops short of whatever stands in its way (a small square's focal pad).
                float hw = Composer.DefaultHalfWidth(ramps);
                while (len > 4f && LeisureKit.Crosses(occ, g.Position + inward * 1.5f, end, hw)) { len -= 2f; end = g.Position + inward * len; }
                if (len <= 4f) continue;
                LeisureKit.Path(ctx, occ, g.Position, end, ramps, ramp);
            }
            if (!ctx.B("levelPlanters")) return;
            // Low green bushes line each level edge (inset rings), leaving the ramps open: a soft balustrade.
            int levels = spec.Mode == TerrainMode.Terraces ? count : 2;
            float band = math.max(4f, (inscribed - falloff * 0.5f) / levels);
            int placed = 0;
            for (int k = 1; k < levels; k++)
            {
                float d = falloff * 0.5f + band * k;
                if (d > inscribed - 2f) break;
                var ring = region.InsetRing(d);   // rf-squares: robust on chamfered corners
                if (ring == null) continue;
                var r = LayerRegion.From(ring);
                if (r == null) continue;
                for (float s = 0; s < r.Perimeter; s += 3.5f)
                {
                    var p = r.PointAt(s, out var t, out _, out _);
                    uint v = Hashing.Mix(ctx.Seed, 0x1E00u + (uint)placed);
                    if (LeisureKit.Put(ctx, occ, p, math.atan2(t.x, t.y), ObjectRole.Bush, PlazaArt.Or(ctx, "planterAsset", PlazaArt.GreenBushes, v), AssetCategories.Bush, 0.8f, v, 0.5f, 0.4f))
                        placed++;
                }
            }
        }

        // ───────────── amphitheatre & performance ─────────────

        public static void Amphitheatre(LayerContext ctx, Occupancy occ, LayerRegion region, bool keepCentre, float centreClear)
        {
            if (!ctx.B("amphitheatre")) return;
            int best = 0; float len = 0;
            for (int i = 0; i < region.Ring.Length; i++)
            {
                float l = math.distance(region.Ring[i], region.Ring[(i + 1) % region.Ring.Length]);
                if (l > len) { len = l; best = i; }
            }
            var inward = region.InwardNormal(best);
            // The bowl sits well into the square (its back rows near the middle), not pressed to the edge.
            region.DeepestPoint(out float ins, null, 12);
            float bowl = 10f + ctx.I("rows") * 3.2f;
            var edgeMid = math.lerp(region.Ring[best], region.Ring[(best + 1) % region.Ring.Length], 0.5f);
            var focal = edgeMid + inward * math.clamp((ins - bowl) * 0.6f, 6f, 24f);
            if (!region.ContainsDisc(focal, 3f)) { ctx.Issue(IssueSeverity.Info, K + "Issue.NoAmphitheatre"); return; }
            var centre = region.Centroid;
            float stageR = 4f;
            if (PlazaArt.Available(ctx, "stageAsset", AssetCategories.Stage, AssetCategories.Shelter, AssetCategories.Centerpiece))
                ctx.Object(focal, math.atan2(inward.x, inward.y), ObjectRole.Furniture, PlazaArt.Or(ctx, "stageAsset", PlazaArt.Stage), Hashing.Mix(ctx.Seed, 0x57A6u), -1, AssetCategories.Stage);
            occ.Take(focal, stageR, 0);
            int rows = ctx.I("rows");
            float r0 = stageR + 6f, spacing = 3.2f;
            float baseAngle = math.atan2(inward.y, inward.x);
            const float halfSpan = 1.05f;   // ≈ ±60°
            int benches = 0;
            float rMax = r0;
            for (int row = 0; row < rows; row++)
            {
                float r = r0 + row * spacing;
                float step = 2.6f / r;
                for (float a = -halfSpan; a <= halfSpan + 1e-3f; a += step)
                {
                    // Radial aisles: centre and both thirds.
                    if (math.abs(a) < 0.07f || math.abs(math.abs(a) - halfSpan * 0.55f) < 0.05f) continue;
                    var p = focal + new float2(math.cos(baseAngle + a), math.sin(baseAngle + a)) * r;
                    if (keepCentre && math.distance(p, centre) < centreClear) continue;
                    uint v = Hashing.Mix(ctx.Seed, 0xA4F0u + (uint)benches);
                    if (LeisureKit.Put(ctx, occ, p, LeisureKit.Yaw(p, focal), ObjectRole.Bench, PlazaArt.Or(ctx, "benchAsset", PlazaArt.Benches, 0), null, 1.1f,
                        v, 0.8f, 0.1f, 1)) { benches++; rMax = r; }
                }
            }
            if (benches == 0) { ctx.Issue(IssueSeverity.Info, K + "Issue.NoAmphitheatre"); return; }
            // Lamps at the ends of every other row frame the bowl.
            for (int row = 1; row < rows; row += 2)
                foreach (float sgn in new[] { -1f, 1f })
                {
                    float r = r0 + row * spacing, a = baseAngle + sgn * (halfSpan + 0.12f);
                    var p = focal + new float2(math.cos(a), math.sin(a)) * r;
                    LeisureKit.Put(ctx, occ, p, 0f, ObjectRole.Lamp, PlazaArt.Or(ctx, "lampAsset", PlazaArt.ParkLamp), null, 0.4f, Hashing.Mix(ctx.Seed, 0xA500u + (uint)row), 0.5f, 0.1f);
                }
            ctx.Stat("Placemaker.Stat.Seats", benches, Keys.UnitCount);
            if (!ctx.B("amphiRaise")) return;
            float reach = rMax + 2f;
            TerrainShaper.Shape(ctx, region, new TerrainSpec
            {
                Mode = TerrainMode.Bowl, Focal = focal, RowSpacing = spacing, Height = math.min(4f, rows * 0.6f), MaxSlopePct = 40f, Falloff = 4f,
                Skip = p =>
                {
                    var d = p - focal; float dist = math.length(d);
                    if (dist > reach || dist < r0 - 2f) return true;
                    float ang = math.atan2(d.y, d.x) - baseAngle;
                    while (ang > math.PI) ang -= 2f * math.PI;
                    while (ang < -math.PI) ang += 2f * math.PI;
                    return math.abs(ang) > halfSpan + 0.15f;
                },
            });
        }

        public static void PerformanceSpot(LayerContext ctx, Occupancy occ, LayerRegion region)
        {
            if (!ctx.B("performance")) return;
            if (!Spots.Find(occ, 8f, Spots.Preference.NearBoundary, Hashing.Mix(ctx.Seed, 0x9E7Fu), out var c, null, 0f, 1f, budget: 1500)) return;
            var b = region.ClosestOnBoundary(c, out _, out _);
            var toEdge = math.normalizesafe(b - c, new float2(0, 1));
            var stage = c + toEdge * 4f;
            LeisureKit.Put(ctx, occ, stage, LeisureKit.Yaw(stage, c), ObjectRole.Furniture, PlazaArt.Or(ctx, "stageAsset", PlazaArt.Stage), AssetCategories.Stage, 3.5f,
                Hashing.Mix(ctx.Seed, 0x9E80u), 0.3f, 0.2f, 0);
            float baseA = math.atan2(-toEdge.y, -toEdge.x);
            for (int i = -2; i <= 2; i++)
            {
                var p = stage + new float2(math.cos(baseA + i * 0.38f), math.sin(baseA + i * 0.38f)) * 8f;
                LeisureKit.Put(ctx, occ, p, LeisureKit.Yaw(p, stage), ObjectRole.Bench, PlazaArt.Or(ctx, "benchAsset", PlazaArt.Benches, 0), null, 1.1f, Hashing.Mix(ctx.Seed, 0x9E90u + (uint)(i + 3)));
            }
        }

        // ───────────── water ─────────────

        public static void Pool(LayerContext ctx, Occupancy occ, LayerRegion region, float2 avoid, float avoidRadius, PavingPlan pave, PavingPalette palette)
        {
            if (!ctx.B("pool")) return;
            var axis = SquareAxis(region);
            LeisureKit.Frame(region, out var centre, out _, out _, out float major, out float minor);
            float halfL = math.clamp(major * 0.18f, 4f, 22f), halfW = math.clamp(minor * 0.08f, 1.5f, 5f);
            OrientedRect best = default; bool found = false;
            foreach (float shift in new[] { 0.22f, -0.22f, 0.35f, -0.35f, 0f })
            {
                var r = new OrientedRect { Center = centre + axis * (major * shift), Axis = axis, HalfLength = halfL, HalfWidth = halfW };
                if (avoidRadius > 0f && math.distance(r.Center, avoid) < avoidRadius + halfL) continue;
                if (!LeisureKit.RectFits(region, occ, r, 2f)) continue;
                best = r; found = true; break;
            }
            if (!found) { ctx.Issue(IssueSeverity.Info, K + "Issue.NoPool"); return; }
            LeisureKit.TakeRect(occ, best);
            bool real = ctx.B("realWater");
            if (real)
            {
                float lo = float.MaxValue, hi = float.MinValue;
                foreach (var q in best.Ring) { float h = ctx.H(q); lo = math.min(lo, h); hi = math.max(hi, h); }
                if (hi - lo > 0.5f) { real = false; ctx.Issue(IssueSeverity.Info, K + "Issue.PoolNotFlat"); }
                else
                {
                    float level = lo - 0.2f;
                    ctx.Plan.Terraform.Add(new PlannedTerraform
                    {
                        Ring = Poly.To3D(best.Ring, ctx.H), Kind = TerraformKind.Lower, TargetHeight = level - 0.6f, Permanent = true,
                        WaterLevel = level, WaterAnchor = new float3(best.Center.x, level, best.Center.y),
                    });
                    ctx.Issue(IssueSeverity.Warning, K + "Issue.PoolPermanent");
                }
            }
            if (!real)
            {
                string cat = LeisureKit.Category(ctx, ctx.S("poolSurface"), AssetCategories.SurfaceWater, AssetCategories.SurfacePaving);
                if (cat != null) pave.Add(best.Ring, SurfaceRole.Decorative, AssetMix.One(ctx.S("poolSurface")), cat);
            }
            // A stone kerb round the water, benches along both long sides looking at it.
            var kerb = new OrientedRect { Center = best.Center, Axis = best.Axis, HalfLength = best.HalfLength + 0.8f, HalfWidth = best.HalfWidth + 0.8f };
            pave.Add(kerb.Ring, SurfaceRole.Plaza, palette.Band, AssetCategories.SurfacePaving);
            int i = 0;
            foreach (int side in new[] { -1, 1 })
                for (float u = -halfL + 3f; u <= halfL - 3f; u += 6f)
                {
                    var p = best.Local(u, side * (halfW + 2.4f));
                    LeisureKit.Put(ctx, occ, p, LeisureKit.Yaw(p, best.Local(u, 0f)), ObjectRole.Bench, PlazaArt.Or(ctx, "benchAsset", PlazaArt.Benches, 0), null, 1.1f, Hashing.Mix(ctx.Seed, 0x9001u + (uint)i++));
                }
        }

        /// <summary>Splash jets: without a chosen jet asset, a row of small fountains (the jet category holds farm sprinklers).</summary>
        public static void Jets(LayerContext ctx, Occupancy occ)
        {
            if (!ctx.B("jets")) return;
            if (!Spots.Find(occ, 6f, Spots.Preference.Open, Hashing.Mix(ctx.Seed, 0x5E75u), out var c, null, 0f, 1f, budget: 1500)) return;
            string chosen = ctx.S("jetAsset");
            if (!string.IsNullOrEmpty(chosen))
            {
                var asset = AssetMix.One(chosen);
                int n = 0;
                for (float y = -4.5f; y <= 4.5f; y += 1.8f)
                    for (float x = -4.5f; x <= 4.5f; x += 1.8f)
                    {
                        if (math.length(new float2(x, y)) > 4.6f) continue;
                        ctx.Object(c + new float2(x, y), 0f, ObjectRole.Furniture, asset, Hashing.Mix(ctx.Seed, (uint)n++ + 0x5E00u), -1, AssetCategories.WaterJets);
                    }
            }
            else
                for (int k = -1; k <= 1; k++)
                    ctx.Object(c + new float2(k * 4f, 0f), 0f, ObjectRole.Centerpiece, PlazaArt.SmallFountain, Hashing.Mix(ctx.Seed, 0x5E10u + (uint)(k + 1)), -1, AssetCategories.CenterpieceFountain);
            occ.Take(c, 6f, 2);
        }

        // ───────────── green ─────────────

        /// <summary>
        /// Parterre lawns: up to four rounded rectangles set symmetrically in the quadrants between the
        /// axes, each with flowerbeds at its corners, a tree in the big ones and benches on the side
        /// facing the middle of the square (backs to the lawn, looking at the focal point).
        /// </summary>
        public static void Lawns(LayerContext ctx, Occupancy occ, LayerRegion region, float2 centre, float2 axis, float rondel, PavingPlan pave)
        {
            float pct = ctx.F("lawns");
            if (pct <= 0f) return;
            var side = new float2(-axis.y, axis.x);
            Extents(region, centre, axis, out float minA, out float maxA, out float minB, out float maxB);
            float fw = FrameWidth(region);
            float spanA = math.min(-minA, maxA) - fw - 2f, spanB = math.min(-minB, maxB) - fw - 2f;
            float inner = math.max(rondel + 3.5f, 4f);
            if (spanA - inner < 6f || spanB - inner < 6f) { ctx.Issue(IssueSeverity.Info, K + "Issue.NoLawn"); return; }
            // Each quadrant parterre covers a share of the quadrant set by the lawn percentage.
            float k = math.sqrt(math.saturate(pct / 45f));
            float qa = (spanA + inner) * 0.5f, qb = (spanB + inner) * 0.5f;
            float ha = (spanA - inner) * 0.5f * k, hb = (spanB - inner) * 0.5f * k;
            if (ha < 3f || hb < 3f) { ha = math.max(ha, 3f); hb = math.max(hb, 3f); }
            var bench = PlazaArt.Or(ctx, "benchAsset", PlazaArt.Benches, 0);
            int made = 0;
            // One size for all four (symmetry beats a slightly bigger lawn in one quarter).
            OrientedRect Quarter(int q, float s)
            {
                float sa = (q & 1) == 0 ? -1f : 1f, sb = (q & 2) == 0 ? -1f : 1f;
                return new OrientedRect { Center = centre + axis * (sa * qa) + side * (sb * qb), Axis = axis, HalfLength = ha * s, HalfWidth = hb * s };
            }
            float scale = 0f; int bestFits = 0;
            foreach (float s in new[] { 1f, 0.85f, 0.72f, 0.6f })
            {
                int fitsAll = 0;
                for (int q = 0; q < 4; q++) if (LeisureKit.RectFits(region, occ, Quarter(q, s), fw + 1f)) fitsAll++;
                if (fitsAll > bestFits) { bestFits = fitsAll; scale = s; }
                if (fitsAll == 4) break;
            }
            var fit = new bool[4];
            for (int q = 0; q < 4 && scale > 0f; q++) fit[q] = LeisureKit.RectFits(region, occ, Quarter(q, scale), fw + 1f);
            for (int q = 0; q < 4 && scale > 0f; q++)
            {
                var rect = Quarter(q, scale);
                // Only point-symmetric pairs: a lone lawn in one quarter reads as a mistake.
                if (!fit[q] || !fit[3 - q] || rect.HalfLength < 2.5f || rect.HalfWidth < 2.5f) continue;
                uint v = Hashing.Mix(ctx.Seed, 0x1B00u + (uint)q);
                float round = math.min(2.5f, math.min(rect.HalfLength, rect.HalfWidth) * 0.4f);
                pave.Add(Poly.SmoothCorners(rect.Ring, round, 1.2f), SurfaceRole.Grass, new AssetRef("", PavingPalettes.VGrass), AssetCategories.SurfaceGrass);
                LeisureKit.TakeRect(occ, new OrientedRect { Center = rect.Center, Axis = rect.Axis, HalfLength = rect.HalfLength, HalfWidth = rect.HalfWidth + 0.4f });
                // Flowerbeds in the four corners, a specimen tree in the middle of a big lawn.
                for (int cnr = 0; cnr < 4; cnr++)
                {
                    float ca = (cnr & 1) == 0 ? -1f : 1f, cb = (cnr & 2) == 0 ? -1f : 1f;
                    var p = rect.Local(ca * (rect.HalfLength - 1.8f), cb * (rect.HalfWidth - 1.8f));
                    ctx.Object(p, math.atan2(axis.x, axis.y), ObjectRole.Planter, PlazaArt.Or(ctx, "flowerbedAsset", PlazaArt.Flowerbeds, v + (uint)cnr),
                        Hashing.Mix(v, (uint)cnr), -1, AssetCategories.Flowerbed);
                }
                if (rect.HalfLength >= 5f && rect.HalfWidth >= 5f)
                    ctx.Object(rect.Center, Hashing.Unit(v) * 6.28f, ObjectRole.Tree, PlazaArt.Or(ctx, "treeAsset", PlazaArt.GardenTrees, v), v, 3);
                // Benches along the two sides that face the middle of the square, backs to the lawn.
                var toCentre = centre - rect.Center;
                for (int sideK = 0; sideK < 2; sideK++)
                {
                    bool alongA = sideK == 0;
                    var n = alongA ? side * math.sign(math.dot(toCentre, side)) : axis * math.sign(math.dot(toCentre, axis));
                    float half = alongA ? rect.HalfLength : rect.HalfWidth;
                    float off = (alongA ? rect.HalfWidth : rect.HalfLength) + 1.9f;
                    var tang = new float2(-n.y, n.x);
                    int seats = math.clamp((int)(half * 2f / 7f), 1, 3);
                    for (int s = 0; s < seats; s++)
                    {
                        float u = (s - (seats - 1) * 0.5f) * 6f;
                        var p = rect.Center + n * off + tang * u;
                        LeisureKit.Put(ctx, occ, p, math.atan2(n.x, n.y), ObjectRole.Bench, bench, null, 1.1f, Hashing.Mix(v, 0x40u + (uint)(s + sideK * 8)), 0.3f, 0.1f);
                    }
                }
                made++;
            }
            // rf-squares: the quarter parterres need the entrance axes on the square's axes; with the access points the
            // tool suggests round a city block (6 to 8, anywhere on the outline) every quarter is crossed by a walk and
            // none fit ("green" built a bare slab in the game runner). Then the lawns fill what the walks leave.
            if (made == 0) made = FreeLawns(ctx, occ, region, centre, rondel, pave, pct);
            if (made == 0) ctx.Issue(IssueSeverity.Info, K + "Issue.NoLawn");
            ctx.Plan.Facts["lawns"] = made;
        }

        /// <summary>
        /// (rf-squares) Lawns in the free ground between the walks, the centre and the edge allée: each free piece
        /// shrunk evenly (rounded) until the lawns cover the asked share of the square, a specimen tree in the big ones
        /// and flowerbeds round it. Works whatever the number and position of the entrances. Returns the lawns made.
        /// </summary>
        private static int FreeLawns(LayerContext ctx, Occupancy occ, LayerRegion region, float2 centre, float rondel, PavingPlan pave, float pct)
        {
            float fw = FrameWidth(region);
            float inner = math.max(rondel + 3.5f, 4f), edge = fw + 2f;
            var pieces = FreeSpace.Pieces(region,
                p => math.distance(p, centre) < inner || region.BoundaryDistance(p, edge + 1f) < edge || occ.Corridors.Overlaps(p, 1.5f) || !occ.Free(p, 0.8f, 0f, 0.2f),
                2f, 60f, ctx.Cancel);
            if (pieces.Count == 0) return 0;
            float target = math.saturate(pct / 100f) * region.Area;
            List<float2[]> Shrunk(float d)
            {
                var res = new List<float2[]>();
                foreach (var piece in pieces)
                    foreach (var r in Poly.Offset(piece, d, OffsetJoin.Round, 4f, 0.25f))
                        if (r.Outer != null && r.Outer.Length >= 3 && Poly.Area(r.Outer) >= 40f) res.Add(r.Outer);
                return res;
            }
            float Total(List<float2[]> rings) { float a = 0f; foreach (var r in rings) a += Poly.Area(r); return a; }
            // Find the even shrink (at least 1 m: a paved margin round every lawn) that gives the asked cover.
            float lo = 1f, hi = 14f;
            var lawns = Shrunk(lo);
            if (Total(lawns) > target)
                for (int it = 0; it < 6; it++)
                {
                    float mid = (lo + hi) * 0.5f;
                    var m = Shrunk(mid);
                    if (Total(m) > target) { lo = mid; lawns = m; } else hi = mid;
                }
            ctx.Check();
            var bench = PlazaArt.Or(ctx, "benchAsset", PlazaArt.Benches, 0);
            int made = 0;
            foreach (var ring in lawns)
            {
                if (made >= 24) break;
                var lr = LayerRegion.From(ring);
                if (lr == null) continue;
                uint v = Hashing.Mix(ctx.Seed, 0x1C00u + (uint)made);
                pave.Add(ring, SurfaceRole.Grass, new AssetRef("", PavingPalettes.VGrass), AssetCategories.SurfaceGrass);
                var deep = lr.DeepestPoint(out float ins, null, 16);
                if (ins >= 3.5f)
                {
                    ctx.Object(deep, Hashing.Unit(v) * 6.28f, ObjectRole.Tree, PlazaArt.Or(ctx, "treeAsset", PlazaArt.GardenTrees, v), v, 3);
                    for (int k = 0; k < 4 && ins >= 5f; k++)
                    {
                        float a = k * 1.5708f + Hashing.Unit(v) * 1.5f;
                        var p = deep + new float2(math.cos(a), math.sin(a)) * 3.2f;
                        ctx.Object(p, a, ObjectRole.Planter, PlazaArt.Or(ctx, "flowerbedAsset", PlazaArt.Flowerbeds, v + (uint)k), Hashing.Mix(v, (uint)k), -1, AssetCategories.Flowerbed);
                    }
                }
                // The lawn is taken: furniture and beds placed later keep off it.
                var min = lr.Min; var max = lr.Max;
                for (float x = min.x + 1f; x <= max.x; x += 2.5f)
                    for (float y = min.y + 1f; y <= max.y; y += 2.5f)
                    {
                        var p = new float2(x, y);
                        if (lr.Contains(p)) occ.Take(p, 1.6f, 3);
                    }
                // A bench at the lawn's point nearest the middle of the square, looking out over it.
                var near = lr.ClosestOnBoundary(centre, out _, out _);
                var outDir = math.normalizesafe(centre - near);
                LeisureKit.Put(ctx, occ, near + outDir * 1.2f, math.atan2(outDir.x, outDir.y), ObjectRole.Bench, bench, null, 1.0f, Hashing.Mix(v, 0x51u), 0.3f, 0.1f);
                made++;
            }
            ctx.Plan.Facts["freeLawns"] = made;
            return made;
        }

        /// <summary>
        /// Flower beds. The first four are round beds in the middle of the quadrants between the axes
        /// (a ring of flowerbeds round a tree or bush, four benches facing it) — they echo the centre and
        /// fill the open quarters; the rest are long planted islands along the edges.
        /// </summary>
        public static void Beds(LayerContext ctx, Occupancy occ, LayerRegion region, float2 centre, float2 axis, float rondel, PavingPlan pave)
        {
            int clusters = ctx.I("beds");
            if (clusters <= 0) return;
            var side = new float2(-axis.y, axis.x);
            Extents(region, centre, axis, out float minA, out float maxA, out float minB, out float maxB);
            float fw = FrameWidth(region);
            float spanA = math.min(-minA, maxA) - fw - 4f, spanB = math.min(-minB, maxB) - fw - 4f;
            float inner = math.max(rondel + 4f, 6f);
            float qa = (spanA + inner) * 0.5f, qb = (spanB + inner) * 0.5f;
            float r = math.clamp(math.min(spanA - inner, spanB - inner) * 0.16f, 2.5f, 5f);
            var bench = PlazaArt.Or(ctx, "benchAsset", PlazaArt.Benches, 0);
            var grass = new AssetRef("", PavingPalettes.VGrass);
            int made = 0;
            var spots = new List<float2>();
            for (int q = 0; q < 4 && made < clusters && spanA - inner > r * 2f + 4f && spanB - inner > r * 2f + 4f; q++)
            {
                float sa = (q & 1) == 0 ? -1f : 1f, sb = (q & 2) == 0 ? -1f : 1f;
                var c = centre + axis * (sa * qa) + side * (sb * qb);
                if (!region.ContainsDisc(c, r + 3f) || !occ.Free(c, r + 2.4f, 0.5f, 0.2f)) continue;
                uint v = Hashing.Mix(ctx.Seed, 0xBE00u + (uint)q);
                pave.Add(PavingShapes.Polygon(c, r, 28), SurfaceRole.Grass, grass, AssetCategories.SurfaceGrass);
                int ring = math.clamp((int)(r * 1.6f), 4, 8);
                for (int k = 0; k < ring; k++)
                {
                    float a = k * 2f * math.PI / ring;
                    var p = c + new float2(math.cos(a), math.sin(a)) * (r - 1.1f);
                    ctx.Object(p, a, ObjectRole.Planter, PlazaArt.Or(ctx, "flowerbedAsset", PlazaArt.Flowerbeds, v + (uint)k), Hashing.Mix(v, (uint)k), -1, AssetCategories.Flowerbed);
                }
                if (r >= 3.5f) ctx.Object(c, Hashing.Unit(v) * 6.28f, ObjectRole.Tree, PlazaArt.Or(ctx, "treeAsset", PlazaArt.LimeTrees, v), v, 2);
                else ctx.Object(c, 0f, ObjectRole.Bush, PlazaArt.Or(ctx, "bushAsset", PlazaArt.CityBushes, v), Hashing.Mix(v, 9u), -1, AssetCategories.Bush);
                occ.Take(c, r + 0.3f, 3);
                for (int k = 0; k < 4; k++)
                {
                    float a = math.atan2(axis.y, axis.x) + math.PI * 0.25f + k * math.PI * 0.5f;
                    var dir = new float2(math.cos(a), math.sin(a));
                    var p = c + dir * (r + 1.5f);
                    LeisureKit.Put(ctx, occ, p, math.atan2(-dir.x, -dir.y), ObjectRole.Bench, bench, null, 1.1f, Hashing.Mix(v, 0x30u + (uint)k), 0.5f, 0.1f);
                }
                spots.Add(c);
                made++;
            }
            for (int i = made; i < clusters; i++)
            {
                if (!Spots.Find(occ, 4f, Spots.Preference.NearBoundary, Hashing.Mix(ctx.Seed, 0xBED0u + (uint)i), out var c, spots, 14f, fw + 1f, budget: 1500)) break;
                spots.Add(c);
                // The bed runs parallel to the nearest edge.
                region.ClosestOnBoundary(c, out int seg, out _);
                var t = math.normalizesafe(region.Ring[(seg + 1) % region.Ring.Length] - region.Ring[seg], axis);
                var rect = new OrientedRect { Center = c, Axis = t, HalfLength = 3.2f, HalfWidth = 1.4f };
                pave.Add(Poly.SmoothCorners(rect.Ring, 0.9f, 0.8f), SurfaceRole.Grass, grass, AssetCategories.SurfaceGrass);
                uint v = Hashing.Mix(ctx.Seed, 0xBE40u + (uint)i);
                for (int k = -1; k <= 1; k++)
                    ctx.Object(rect.Local(k * 1.7f, 0f), math.atan2(t.x, t.y), ObjectRole.Planter, PlazaArt.Or(ctx, "flowerbedAsset", PlazaArt.Flowerbeds, v + (uint)(k + 1)),
                        Hashing.Mix(v, (uint)(k + 1)), -1, AssetCategories.Flowerbed);
                foreach (int e in new[] { -1, 1 })
                    ctx.Object(rect.Local(e * 2.6f, 0f), 0f, ObjectRole.Bush, PlazaArt.Or(ctx, "bushAsset", PlazaArt.CityBushes, v + (uint)(e + 5)), Hashing.Mix(v, (uint)(e + 7)), -1, AssetCategories.Bush);
                LeisureKit.TakeRect(occ, new OrientedRect { Center = c, Axis = t, HalfLength = 3.6f, HalfWidth = 1.8f });
                made++;
            }
            ctx.Plan.Facts["beds"] = made;
        }

        /// <summary>Pots of flowers along the edges, in pairs either side of a regular rhythm.</summary>
        public static void EdgePlanters(LayerContext ctx, Occupancy occ, LayerRegion region, List<Gate> gates)
        {
            int count = ctx.I("planters");
            if (count <= 0) return;
            float step = region.Perimeter / count;
            float inset = FrameWidth(region) + 0.9f;
            for (int i = 0; i < count; i++)
            {
                var b = region.PointAt(step * (i + 0.5f), out var t, out var inward, out _);
                if (NearGate(b, gates, 5f)) continue;
                var p = b + inward * inset;
                uint v = Hashing.Mix(ctx.Seed, 0xED00u + (uint)i);
                LeisureKit.Put(ctx, occ, p, math.atan2(t.x, t.y), ObjectRole.Planter, PlazaArt.Or(ctx, "planterAsset", PlazaArt.Pots, v), AssetCategories.Planter, 0.6f, v, 0.4f, 0.2f);
            }
        }

        /// <summary>
        /// Trees. edge: an allée just inside the frame with a bench between every other pair, looking
        /// into the square; grid: a bosque on the outer belt; groves: planted islands with a bench ring.
        /// Paved trees go to <paramref name="pits"/> (they get a tree pit in the ground design).
        /// </summary>
        public static void Trees(LayerContext ctx, Occupancy occ, LayerRegion region, List<Gate> gates, float2 centre, float coreRadius, float2 axis,
            List<float2> pits, PavingPlan pave, string[] species)
        {
            string layout = ctx.S("treeLayout");
            if (layout == "none" || string.IsNullOrEmpty(layout)) return;
            var ids = string.IsNullOrEmpty(ctx.S("treeAsset")) ? species : new[] { ctx.S("treeAsset") };
            var bench = PlazaArt.Or(ctx, "benchAsset", PlazaArt.Benches, 0);
            int n = 0;
            float fw = FrameWidth(region);
            bool Tree(float2 p, bool paved)
            {
                if (math.distance(p, centre) < coreRadius + 2f || NearGate(p, gates, 6f)) return false;
                uint v = Hashing.Mix(ctx.Seed, 0x7E00u + (uint)n);
                if (!LeisureKit.Put(ctx, occ, p, Hashing.Unit(v) * 6.28f, ObjectRole.Tree, AssetMix.Pick(ids, (uint)(layout == "groves" ? n : 0)), null, 2.2f, v, 1f, 0.3f, 3, 3)) return false;
                if (paved) pits.Add(p);
                n++;
                return true;
            }
            switch (layout)
            {
                case "edge":
                {
                    float pitch = 9f, inset = fw + 2.8f;
                    // rf-squares: a true offset; the per-vertex inset folded over on a bucket-filled block's chamfered
                    // corners and the whole allée was dropped (no trees on any plaza in the game runner).
                    var inner = region.InsetRing(inset);
                    var r = inner != null ? LayerRegion.From(inner) : null;
                    if (r == null) break;
                    int count = math.max(1, (int)math.round(r.Perimeter / pitch));
                    float step = r.Perimeter / count;
                    float2 prev = default; bool havePrev = false; int k = 0;
                    for (int i = 0; i < count; i++)
                    {
                        var p = r.PointAt(step * (i + 0.5f), out var t, out var inward, out _);
                        bool ok = Tree(p, true);
                        if (ok && havePrev && (k++ & 1) == 0 && math.distance(prev, p) < pitch * 1.4f)
                        {
                            var mid = (prev + p) * 0.5f;
                            var into = math.normalizesafe(centre - mid, inward);
                            LeisureKit.Put(ctx, occ, mid + into * 0.6f, math.atan2(into.x, into.y), ObjectRole.Bench, bench, null, 1.1f, Hashing.Mix(ctx.Seed, 0x7B00u + (uint)i), 0.5f, 0.1f);
                        }
                        prev = p; havePrev = ok;
                    }
                    break;
                }
                case "grid":
                {
                    // Bosquets: a block of trees on a 7 m lattice in each quarter between the axes, the
                    // inner rows of each block lined with benches looking onto the axis beside them.
                    var side = new float2(-axis.y, axis.x);
                    Extents(region, centre, axis, out float minA, out float maxA, out float minB, out float maxB);
                    const float pitch = 7f;
                    float inner = math.max(coreRadius + 3f, 6.5f);
                    int rows = math.clamp((int)((math.min(math.min(-minA, maxA), math.min(-minB, maxB)) - fw - 3f - inner) / pitch) + 1, 1, 4);
                    for (int q = 0; q < 4; q++)
                    {
                        float sa = (q & 1) == 0 ? -1f : 1f, sb = (q & 2) == 0 ? -1f : 1f;
                        for (int i = 0; i < rows; i++)
                            for (int j = 0; j < rows; j++)
                            {
                                float u = sa * (inner + i * pitch), w = sb * (inner + j * pitch);
                                var p = centre + axis * u + side * w;
                                if (!region.ContainsDisc(p, fw + 2.5f)) continue;
                                if (!Tree(p, true)) continue;
                                if (i == 0 && (j & 1) == 0)
                                    LeisureKit.Put(ctx, occ, p - axis * (sa * 3.9f), math.atan2(-axis.x * sa, -axis.y * sa), ObjectRole.Bench, bench, null, 1.1f, Hashing.Mix(ctx.Seed, 0x7C00u + (uint)n), 0.3f, 0.1f);
                                if (j == 0 && (i & 1) == 1)
                                    LeisureKit.Put(ctx, occ, p - side * (sb * 3.9f), math.atan2(-side.x * sb, -side.y * sb), ObjectRole.Bench, bench, null, 1.1f, Hashing.Mix(ctx.Seed, 0x7D00u + (uint)n), 0.3f, 0.1f);
                            }
                    }
                    break;
                }
                default:   // groves: planted islands
                    for (int g = 0; g < 4; g++)
                    {
                        if (!Spots.Find(occ, 7.5f, Spots.Preference.NearBoundary, Hashing.Mix(ctx.Seed, 0x6200u + (uint)g), out var c, null, 0f, fw + 1f, budget: 1500)) break;
                        uint v = Hashing.Mix(ctx.Seed, 0x6300u + (uint)g);
                        pave.Add(LeisureKit.Blob(c, 6.2f, v, 16, 0.12f), SurfaceRole.Grass, new AssetRef("", PavingPalettes.VGrass), AssetCategories.SurfaceGrass);
                        for (int k = 0; k < 4; k++)
                        {
                            float a = k * 1.5708f + g;
                            Tree(c + new float2(math.cos(a), math.sin(a)) * (k == 0 ? 0f : 3.4f), false);
                        }
                        for (int k = 0; k < 5; k++)
                        {
                            float a = k * 1.2566f + g + 0.6f;
                            var bp = c + new float2(math.cos(a), math.sin(a)) * 2.6f;
                            ctx.Object(bp, a, ObjectRole.Bush, PlazaArt.Or(ctx, "bushAsset", PlazaArt.CityBushes, v + (uint)k), Hashing.Mix(v, (uint)k), -1, AssetCategories.Bush);
                        }
                        occ.Take(c, 6.2f, 3);
                        // Benches round the island, backs to the green, looking out over the square.
                        for (int k = 0; k < 3; k++)
                        {
                            float a = k * 2.094f + g * 0.7f;
                            var dir = new float2(math.cos(a), math.sin(a));
                            LeisureKit.Put(ctx, occ, c + dir * 7.6f, math.atan2(dir.x, dir.y), ObjectRole.Bench, bench, null, 1.1f, Hashing.Mix(v, 0x50u + (uint)k), 0.5f, 0.1f);
                        }
                    }
                    break;
            }
            ctx.Plan.Facts["plazaTrees"] = n;
        }

        /// <summary>A grass (or gravel) edge strip round the square, the paving's frame sits inside it.</summary>
        public static void Border(LayerContext ctx, LayerRegion region, PavingPlan pave)
        {
            string border = ctx.S("border");
            if (border == "none" || string.IsNullOrEmpty(border)) return;
            bool grass = border == "grass";
            pave.Add(PavingShapes.Frame(region.Ring, 3f), grass ? SurfaceRole.Grass : SurfaceRole.Decorative,
                grass ? new AssetRef("", PavingPalettes.VGrass) : AssetMix.One(""), grass ? AssetCategories.SurfaceGrass : AssetCategories.SurfaceNatural);
        }

        // ───────────── shade & activity ─────────────

        public static void Shade(LayerContext ctx, Occupancy occ)
        {
            int n = ctx.I("shade");
            if (n <= 0 || !PlazaArt.Available(ctx, "shelterAsset", AssetCategories.Shelter)) return;
            var spots = new List<float2>();
            for (int i = 0; i < n; i++)
            {
                uint v = Hashing.Mix(ctx.Seed, 0x5A00u + (uint)i);
                var asset = PlazaArt.Or(ctx, "shelterAsset", PlazaArt.Pergolas, v);
                float r = AssetMix.FootprintRadius(ctx.Site, asset, 3.2f);
                if (!Spots.Find(occ, r + 1.5f, Spots.Preference.NearBoundary, Hashing.Mix(ctx.Seed, 0x5AD0u + (uint)i), out var c, spots, r * 5f, 2f, budget: 1500)) break;
                spots.Add(c);
                var b = occ.LayerRegion.ClosestOnBoundary(c, out _, out _);
                if (!LeisureKit.Put(ctx, occ, c, LeisureKit.Yaw(b, c), ObjectRole.Furniture, asset, AssetCategories.Shelter, r, v, 0.5f, 0.3f, 0)) continue;
                // A bench under it, facing the square.
                LeisureKit.Put(ctx, occ, c + math.normalizesafe(c - b) * (r + 1.2f), LeisureKit.Yaw(b, c), ObjectRole.Bench,
                    PlazaArt.Or(ctx, "benchAsset", PlazaArt.Benches, 0), null, 1.1f, Hashing.Mix(v, 1u), 0.3f, 0.1f);
            }
        }

        /// <summary>Cafe terraces: a patio set (or table sets under parasols) against the edge, a planter at each end.</summary>
        public static void Cafe(LayerContext ctx, Occupancy occ)
        {
            int n = ctx.I("cafe");
            if (n <= 0 || !PlazaArt.Available(ctx, "cafeAsset", AssetCategories.CafeSeating, AssetCategories.Picnic)) return;
            var region = occ.LayerRegion;
            var spots = new List<float2>();
            bool chosen = !string.IsNullOrEmpty(ctx.S("cafeAsset"));
            for (int i = 0; i < n; i++)
            {
                uint v = Hashing.Mix(ctx.Seed, 0xCA00u + (uint)i);
                var asset = PlazaArt.Or(ctx, "cafeAsset", PlazaArt.Patios, v);
                float r = AssetMix.FootprintRadius(ctx.Site, asset, chosen ? 2.5f : 4.6f);
                if (!Spots.Find(occ, r + 1f, Spots.Preference.NearBoundary, Hashing.Mix(ctx.Seed, 0xCAF0u + (uint)i), out var c, spots, r * 3.5f, FrameWidth(region) + 0.5f, budget: 1500)) break;
                spots.Add(c);
                var b = region.ClosestOnBoundary(c, out int seg, out _);
                var t = math.normalizesafe(region.Ring[(seg + 1) % region.Ring.Length] - region.Ring[seg]);
                LeisureKit.Put(ctx, occ, c, math.atan2(t.x, t.y), ObjectRole.Furniture, asset, AssetCategories.CafeSeating, r, v, 0.5f, 0.2f, 0);
                foreach (int e in new[] { -1, 1 })
                    LeisureKit.Put(ctx, occ, c + t * (e * (r + 0.9f)), 0f, ObjectRole.Planter, PlazaArt.Or(ctx, "planterAsset", PlazaArt.Pots, v + (uint)e), AssetCategories.Planter, 0.5f,
                        Hashing.Mix(v, (uint)(e + 2)), 0.2f, 0.1f);
            }
        }

        public static void Stalls(LayerContext ctx, Occupancy occ, LayerRegion region, PavingPlan pave = null, PavingPalette palette = null)
        {
            int n = ctx.I("stalls");
            if (n <= 0 || !PlazaArt.Available(ctx, "stallAsset", AssetCategories.MarketStall, AssetCategories.Kiosk)) return;
            LeisureKit.Frame(region, out var c, out _, out _, out _, out _);
            int placed = StallBlock(ctx, occ, region, c, SquareAxis(region), n, 4.5f, "stallAsset", 0x57A0u, out var block);
            if (placed > 0 && pave != null && palette != null)
                pave.Add(new OrientedRect { Center = block.Center, Axis = block.Axis, HalfLength = block.HalfLength + 2f, HalfWidth = block.HalfWidth + 1f }.Ring, SurfaceRole.Plaza, palette.Accent, AssetCategories.SurfacePaving);
            if (placed == 0) { ctx.Issue(IssueSeverity.Info, K + "Issue.NoStalls"); return; }
            ctx.Stat("Placemaker.Stat.Stalls", placed, Keys.UnitCount);
        }

        /// <summary>
        /// A market block centred on <paramref name="c"/>: rows of stalls back to back, each pair of rows
        /// facing its own aisle, the block grown outward from the middle (nearest slots first, measured
        /// in the block's own proportions so it stays a rectangle). A lamp at every third aisle end.
        /// Returns the stalls placed; <paramref name="block"/> = the block's oriented bounds.
        /// </summary>
        public static int StallBlock(LayerContext ctx, Occupancy occ, LayerRegion region, float2 c, float2 axis, int n, float aisle, string assetControl, uint salt,
            out OrientedRect block)
        {
            block = default;
            var side = new float2(-axis.y, axis.x);
            const float len = 4.2f, depth = 3.4f, gap = 1.0f;
            float pairDepth = depth * 2f + 0.4f, pitchV = pairDepth + aisle, pitchU = len + gap;
            float fw = FrameWidth(region);
            Extents(region, c, axis, out float minA, out float maxA, out float minB, out float maxB);
            var cands = new List<(float score, OrientedRect r, int row)>();
            int nu = (int)math.ceil(math.max(-minA, maxA) / pitchU) + 1, nv = (int)math.ceil(math.max(-minB, maxB) / pitchV) + 1;
            float ar = math.max(1f, (maxA - minA) / math.max(1f, maxB - minB));
            for (int iv = -nv; iv <= nv; iv++)
                for (int iu = -nu; iu <= nu; iu++)
                {
                    var pc = c + axis * (iu * pitchU) + side * (iv * pitchV + pitchV * 0.5f);
                    var r = new OrientedRect { Center = pc, Axis = axis, HalfLength = len * 0.5f, HalfWidth = pairDepth * 0.5f };
                    float score = math.max(math.abs(iu * pitchU) / ar, math.abs(iv * pitchV + pitchV * 0.5f));
                    cands.Add((score, r, iv));
                }
            cands.Sort((x, y) => x.score.CompareTo(y.score));
            int placed = 0;
            float bMinA = float.MaxValue, bMaxA = float.MinValue, bMinB = float.MaxValue, bMaxB = float.MinValue;
            var lamp = PlazaArt.Or(ctx, "lampAsset", PlazaArt.ParkLamp);
            var rows = new HashSet<int>();
            foreach (var (_, r, row) in cands)
            {
                if (placed >= n) break;
                if (!LeisureKit.RectFits(region, occ, r, fw + 0.8f)) continue;
                LeisureKit.TakeRect(occ, r);
                for (int k = -1; k <= 1 && placed < n; k += 2)
                {
                    var p = r.Local(0f, k * (depth * 0.5f + 0.2f));
                    float yaw = math.atan2(side.x * k, side.y * k);   // facing its aisle
                    uint v = Hashing.Mix(ctx.Seed, salt + (uint)placed);
                    var asset = PlazaArt.Or(ctx, assetControl, (placed % 5) == 4 ? PlazaArt.Carts : PlazaArt.Stalls, v);
                    ctx.Object(p, yaw, ObjectRole.Furniture, asset, v, -1, AssetCategories.MarketStall);
                    placed++;
                }
                var d = r.Center - c;
                float u = math.dot(d, axis), w = math.dot(d, side);
                bMinA = math.min(bMinA, u - r.HalfLength); bMaxA = math.max(bMaxA, u + r.HalfLength);
                bMinB = math.min(bMinB, w - r.HalfWidth); bMaxB = math.max(bMaxB, w + r.HalfWidth);
                rows.Add(row);
            }
            if (placed == 0) return 0;
            block = new OrientedRect
            {
                Center = c + axis * ((bMinA + bMaxA) * 0.5f) + side * ((bMinB + bMaxB) * 0.5f), Axis = axis,
                HalfLength = (bMaxA - bMinA) * 0.5f, HalfWidth = (bMaxB - bMinB) * 0.5f,
            };
            // Lamps at both ends of every aisle between the rows.
            int li = 0;
            foreach (var row in rows)
                foreach (float e in new[] { -1f, 1f })
                {
                    float w = row * pitchV + pitchV * 0.5f + pairDepth * 0.5f + aisle * 0.5f;
                    var p = c + axis * (e > 0 ? bMaxA + 1.2f : bMinA - 1.2f) + side * w;
                    if (w > bMaxB) continue;
                    LeisureKit.Put(ctx, occ, p, 0f, ObjectRole.Lamp, lamp, null, 0.4f, Hashing.Mix(ctx.Seed, salt + 0x900u + (uint)li++), 0.5f, 0.1f);
                }
            return placed;
        }

        public static void Games(LayerContext ctx, Occupancy occ)
        {
            if (!ctx.B("games") || !PlazaArt.Available(ctx, "", AssetCategories.CafeSeating, AssetCategories.Picnic)) return;
            if (!Spots.Find(occ, 5f, Spots.Preference.NearBoundary, Hashing.Mix(ctx.Seed, 0x6A3Eu), out var c, null, 0f, 2f, budget: 1500)) return;
            for (int k = 0; k < 4; k++)
            {
                var p = c + new float2(k % 2 == 0 ? -2.4f : 2.4f, k < 2 ? -2.4f : 2.4f);
                LeisureKit.Put(ctx, occ, p, 0f, ObjectRole.Furniture, PlazaArt.TableSet, AssetCategories.CafeSeating, 1.2f, Hashing.Mix(ctx.Seed, 0x6A00u + (uint)k), 0.5f, 0.2f);
            }
        }

        // ───────────── identity ─────────────

        /// <summary>
        /// (rf-squares) No centrepiece can be built: the player chose none and no fountain or statue is installed (the
        /// clean game has none; they come with asset packs). The square then plants its centre instead of leaving an
        /// empty paved disc.
        /// </summary>
        public static bool CentreAssetsMissing(LayerContext ctx) =>
            string.IsNullOrEmpty(ctx.S("centerpiece")) && !AssetAvailability.Has(AssetCategories.Centerpiece);

        private static readonly AssetRef SpecimenTree = new AssetRef("OakTree01", "EU_ChestnutTree01", "NA_LindenTree01");

        /// <summary>
        /// (rf-squares) The planted centre used when no centrepiece is installed: a round lawn inside the rondel with a
        /// specimen tree in the middle (three on a big rondel), a ring of flowering shrubs and benches round the lawn
        /// looking out over the square. Returns the radius it took.
        /// </summary>
        public static float SpecimenCentre(LayerContext ctx, Occupancy occ, float2 c, float radius, PavingPlan pave, uint salt)
        {
            radius = math.max(radius, 3.5f);
            float lawn = radius - 1.1f;
            if (pave != null) pave.Add(LayerRegion.Circle(c, lawn, 32), SurfaceRole.Grass, new AssetRef("", PavingPalettes.VGrass), AssetCategories.SurfaceGrass);
            uint v = Hashing.Mix(ctx.Seed, salt);
            if (lawn >= 7.5f)
                for (int k = 0; k < 3; k++)
                {
                    float a = k * 2.0944f + 0.5f;
                    ctx.Object(c + new float2(math.cos(a), math.sin(a)) * (lawn * 0.42f), Hashing.Unit(v + (uint)k) * 6.28f, ObjectRole.Tree, SpecimenTree, Hashing.Mix(v, (uint)k), 3);
                }
            else ctx.Object(c, Hashing.Unit(v) * 6.28f, ObjectRole.Tree, SpecimenTree, v, 3);
            // Shrubs on the lawn's rim.
            if (lawn >= 3f)
            {
                int shrubs = math.clamp((int)(2f * math.PI * (lawn - 0.9f) / 2.6f), 6, 28);
                for (int k = 0; k < shrubs; k++)
                {
                    float a = k * 2f * math.PI / shrubs;
                    var p = c + new float2(math.cos(a), math.sin(a)) * (lawn - 0.9f);
                    ctx.Object(p, a, ObjectRole.Bush, PlazaArt.Or(ctx, "bushAsset", PlazaArt.CityBushes, (uint)k), Hashing.Mix(v, 0x40u + (uint)k), -1, AssetCategories.Bush);
                }
            }
            occ.Take(c, radius, 0);
            // Benches just outside the lawn, backs to it.
            int benches = math.clamp((int)(2f * math.PI * (radius + 0.8f) / 9f), 4, 8);
            var bench = PlazaArt.Or(ctx, "benchAsset", PlazaArt.Benches, 0);
            for (int k = 0; k < benches; k++)
            {
                float a = (k + 0.5f) * 2f * math.PI / benches;
                var dir = new float2(math.cos(a), math.sin(a));
                LeisureKit.Put(ctx, occ, c + dir * (radius + 0.8f), math.atan2(dir.x, dir.y), ObjectRole.Bench, bench, null, 1.0f, Hashing.Mix(v, 0x80u + (uint)k), 0.2f, 0.1f);
            }
            ctx.Plan.Facts["specimenCentre"] = 1;
            return radius;
        }

        public static string CenterCategory(string kind) =>
            kind == "fountain" ? AssetCategories.CenterpieceFountain : kind == "statue" ? AssetCategories.CenterpieceStatue : null;

        /// <summary>The designed centrepiece for a kind ("statue" = the sculpture: the statue category holds clock hands).</summary>
        public static AssetRef CenterAsset(LayerContext ctx, string kind)
        {
            string s = ctx.S("centerpiece");
            if (!string.IsNullOrEmpty(s)) return AssetMix.One(s);
            return kind == "statue" ? PlazaArt.Sculpture : kind == "fountain" ? PlazaArt.GrandFountain : PlazaArt.SmallFountain;
        }

        /// <summary>Lamps in a steady rhythm just inside the frame (every square); "feature" adds a ring round the centre.</summary>
        public static void Lighting(LayerContext ctx, Occupancy occ, LayerRegion region, List<Gate> gates, float2 centre, float coreRadius, AssetRef designed)
        {
            var lamp = PlazaArt.Or(ctx, "lampAsset", designed);
            int n = 0;
            bool feature = ctx.S("lighting") == "feature";
            if (feature && coreRadius > 0.5f)
            {
                float ring = math.max(5f, coreRadius + 2.2f);
                for (int i = 0; i < 8; i++)
                {
                    float a = i * math.PI / 4f + math.PI / 8f;
                    var p = centre + new float2(math.cos(a), math.sin(a)) * ring;
                    if (LeisureKit.Put(ctx, occ, p, a, ObjectRole.Lamp, lamp, null, 0.4f, Hashing.Mix(ctx.Seed, 0x1160u + (uint)i), 0.5f, 0.1f)) n++;
                }
            }
            float pitch = feature ? 13f : 18f, inset = FrameWidth(region) * 0.5f;
            int count = math.max(4, (int)math.round(region.Perimeter / pitch));
            float step = region.Perimeter / count;
            for (int i = 0; i < count; i++)
            {
                var b = region.PointAt(step * i, out _, out var inward, out _);
                if (NearGate(b, gates, 3.5f)) continue;
                if (LeisureKit.Put(ctx, occ, b + inward * inset, 0f, ObjectRole.Lamp, lamp, null, 0.4f, Hashing.Mix(ctx.Seed, 0x1170u + (uint)n), 0.3f, 0.1f)) n++;
            }
        }

        public static void Bollards(LayerContext ctx, Occupancy occ, LayerRegion region, List<Gate> gates)
        {
            if (!ctx.B("bollards")) return;
            var edges = StreetFrontage.Edges(region.Ring, StreetFrontage.Roads(ctx.Input));
            if (edges.Count == 0) { ctx.Issue(IssueSeverity.Info, K + "Issue.NoRoadEdge"); return; }
            if (!PlazaArt.Available(ctx, "bollardAsset", AssetCategories.Bollard)) return;
            var asset = PlazaArt.Or(ctx, "bollardAsset", PlazaArt.Bollard);
            int n = 0;
            foreach (var (a, b) in edges)
            {
                var dir = math.normalizesafe(b - a);
                var inward = new float2(-dir.y, dir.x);
                foreach (var p0 in LeisureKit.Along(a, b, 2.2f))
                {
                    var p = p0 + inward * 0.7f;
                    if (LeisureKit.Put(ctx, occ, p, math.atan2(dir.x, dir.y), ObjectRole.Furniture, asset, AssetCategories.Bollard, 0.25f,
                        Hashing.Mix(ctx.Seed, 0xB011u + (uint)n), 0.2f, 0.15f)) n++;
                }
            }
        }

        /// <summary>Legacy decal pattern (paving decals) — squares now lay real paving bands (Ground).</summary>
        public static void Pattern(LayerContext ctx, Occupancy occ, LayerRegion region)
        {
            string pattern = ctx.S("pattern");
            if (pattern == "none" || string.IsNullOrEmpty(pattern)) return;
            if (!AssetAvailability.Require(ctx.Plan, AssetCategories.DecalPaving, null, Keys.Category(AssetCategories.DecalPaving))) return;
            LeisureKit.Frame(region, out var c, out var axis, out var side, out _, out _);
            float yaw = math.atan2(axis.x, axis.y);
            int n = 0;
            void Decal(float2 p, float y)
            {
                if (n >= 1500 || !region.ContainsDisc(p, 0.5f) || occ.Discs.Overlaps(p, 0.6f, 0f)) return;
                ctx.Object(p, y, ObjectRole.Decal, AssetMix.One(""), Hashing.Mix(ctx.Seed, 0xDEC0u + (uint)n), -1, AssetCategories.DecalPaving);
                n++;
            }
            switch (pattern)
            {
                case "grid":
                {
                    float reach = math.length(region.Max - region.Min);
                    for (float u = -reach; u <= reach; u += 5f)
                        for (float v = -reach; v <= reach; v += 5f)
                            Decal(c + axis * u + side * v, yaw);
                    break;
                }
                case "border":
                    for (float s = 0; s < region.Perimeter; s += 2.5f)
                    {
                        var b = region.PointAt(s, out var t, out var inward, out _);
                        Decal(b + inward * 2f, math.atan2(t.x, t.y));
                    }
                    break;
                default:
                    foreach (var p in Scatter.Candidates(region, 4f, Hashing.Mix(ctx.Seed, 0xDE5Cu), math.min(1500, (int)(region.Area / 30f)), ctx.Cancel))
                        Decal(p, yaw + (Hashing.Mix(ctx.Seed, (int)p.x, (int)p.y) % 4u) * 1.5708f);
                    break;
            }
        }

        public static bool NearGate(float2 p, List<Gate> gates, float d)
        {
            foreach (var g in gates) if (math.distancesq(g.Position, p) < d * d) return true;
            return false;
        }
    }
}
