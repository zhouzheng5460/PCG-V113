// s18 (playtest: a 0.15 ha triangle came out as overlapping rings, orange/yellow bands, grass slivers and a
// fountain tower half over the circle's edge — "even micro plazas should still be good looking").
//
// Size tiers. A square is planned for its size, not shrunk from a big one:
//   micro  < 450 m² or inscribed radius < 6 m    one paving field, one thin border band, one focal piece
//   small  < 2000 m² or inscribed radius < 12 m  + a focal pad, a bench ring facing the focal piece, corner trees
//   medium / large                               the full composition (PlazaDressing), with fit rules so rings,
//                                                bands and pads never cut each other (PlazaDressing.Ground)
//
// Compact composition (micro/small) — one clear idea:
//   ground   the palette's field + ONE border band following the outline (inset, so a triangle keeps its
//            triangle), and — when a focal piece stands — a round focal pad that always stays a clear
//            margin inside the band. No rings, axes, grids or lawns: on 300 m² they read as clutter.
//   focal    at the incentre / largest inscribed circle (LeisureKit.FocalPoint): a fountain feature lands
//            there (FountainAddOn looks there first) and the pad is laid to its predicted size; otherwise
//            the square places a piece scaled to fit (grand fountain › small fountain › sculpture › tree).
//   seating  benches on a ring facing the focal piece, in symmetric pairs with the gaps toward the entrances;
//            along the longest edges when no ring fits.
//   lamps    on the edge rhythm: in the corners and evenly along long edges, on the border band.
//   trees    (small) in the corner pockets that would otherwise be dead space.

using System;
using System.Collections.Generic;
using Placemaker.Core.Geometry;
using Placemaker.Core.Layers.AddOns;
using Placemaker.Core.Layers.Common;
using Unity.Mathematics;

namespace Placemaker.Core.Layers.Plaza
{
    public enum PlazaTier { Micro = 0, Small = 1, Medium = 2, Large = 3 }

    public static class PlazaTiers
    {
        public const float MicroArea = 450f, SmallArea = 2000f, MediumArea = 8000f;
        public const float MicroInscribed = 6f, SmallInscribed = 12f;

        public static PlazaTier Of(float area, float inscribed)
        {
            if (area < MicroArea || inscribed < MicroInscribed) return PlazaTier.Micro;
            if (area < SmallArea || inscribed < SmallInscribed) return PlazaTier.Small;
            return area < MediumArea ? PlazaTier.Medium : PlazaTier.Large;
        }

        public static bool IsCompact(PlazaTier t) => t <= PlazaTier.Small;

        /// <summary>Border band width: thin on a micro square, never more than a tenth of its depth.</summary>
        public static float BandWidth(PlazaTier t, float inscribed) =>
            t == PlazaTier.Micro ? math.clamp(inscribed * 0.12f, 0.7f, 1.0f) : math.clamp(inscribed * 0.1f, 1.1f, 1.6f);

        /// <summary>The plaza layer's own fountain-feature settings (PlazaLayer.FeatureList) the pad is sized for.</summary>
        public const string FountainSize = "large";
    }

    internal static class PlazaCompact
    {
        /// <summary>Result of the compact composition (for regions and stats).</summary>
        public struct Result
        {
            public float2 Focal;
            public float CoreRadius;
            public int Centerpieces;
        }

        public static Result Plan(LayerContext ctx, Occupancy occ, LayerRegion region, List<Gate> gates, PavingPlan pave, PavingPalette palette,
            PlazaTier tier, bool fountainChild, float2 axis, ReservedSpace places)
        {
            var res = new Result();
            var focal = LeisureKit.FocalPoint(region, places, out _);
            float room = region.BoundaryDistance(focal);
            float bw = PlazaTiers.BandWidth(tier, room);
            res.Focal = focal;
            float inner = room - bw;                    // focal → inside edge of the border band
            var mode = PlazaLayer.ParseCenterMode(ctx.S("centerMode"));
            bool wantFocal = fountainChild || mode != CenterMode.None;
            float yaw0 = math.atan2(axis.x, axis.y);
            uint variant = Hashing.Mix(ctx.Seed, 0xC0A1u);

            // ── focal piece ──
            float objR = 0f, padR = 0f, keepClear = 0f, ringR = 0f;
            bool seatsOnRing = false;
            // A fountain feature that cannot stand on the focal point (a narrow strip) leaves the focal to the square.
            float basin = 0f; bool alone = true;
            float fApron = fountainChild ? FountainAddOn.PredictApron(ctx.Site, room, PlazaTiers.FountainSize, true, out basin, out alone) : 0f;
            // rf-squares: with no fountain installed the add-on builds nothing: the square plants its focal point instead.
            bool fountainHere = fApron > 0f && !PlazaDressing.CentreAssetsMissing(ctx);
            if (fountainHere)
            {
                // The fountain add-on builds it (with its own bench ring); the pad matches what it will build.
                objR = basin;
                keepClear = fApron + 0.9f;   // its seating ring + the reserved discs of anything the square places
                padR = alone ? basin + 1.2f : fApron - 0.4f;
                if (padR > inner - 1.0f) padR = basin + 1.2f;
                res.Centerpieces = 1;
            }
            else if (wantFocal)
            {
                string kind = ctx.S("centerKind");
                bool chosen = !string.IsNullOrEmpty(ctx.S("centerpiece"));
                // A player's oversized centre (centerSize) that cannot fit is dropped and said so, as on big squares.
                bool tooBig = ctx.F("centerSize") > room;
                AssetRef asset = default; string cat = null; ObjectRole role = ObjectRole.Centerpiece;
                if (!tooBig && AssetAvailability.Require(ctx.Plan, AssetCategories.Centerpiece, new[] { ctx.S("centerpiece") }, Keys.Category(AssetCategories.Centerpiece)))
                {
                    if (chosen)
                    {
                        asset = AssetMix.One(ctx.S("centerpiece"));
                        objR = AssetMix.FootprintRadius(ctx.Site, asset, 3f);
                        cat = PlazaDressing.CenterCategory(kind) ?? AssetCategories.Centerpiece;
                    }
                    else
                    {
                        // Designed pieces, largest that fits with a seating ring round it (else alone).
                        var options = kind == "statue"
                            ? new[] { (PlazaArt.Sculpture, AssetCategories.CenterpieceStatue), (PlazaArt.SmallFountain, AssetCategories.CenterpieceFountain) }
                            : new[] { (PlazaArt.GrandFountain, AssetCategories.CenterpieceFountain), (PlazaArt.SmallFountain, AssetCategories.CenterpieceFountain) };
                        foreach (var (a, c) in options)
                        {
                            float r = AssetMix.FootprintRadius(ctx.Site, a, 3f);
                            if (r + 1.2f > inner - 0.8f) continue;
                            asset = a; cat = c; objR = r; break;
                        }
                    }
                    if (objR <= 0f || objR + 1.2f > inner - 0.8f) { objR = 0f; asset = default; }
                }
                else if (tooBig) ctx.Issue(IssueSeverity.Info, "Placemaker.Plaza.Issue.CenterpieceDoesNotFit");
                if (objR > 0f && occ.Free(focal, objR, 0.5f, 0.3f))
                {
                    ctx.Object(focal, yaw0, role, asset, variant, -1, cat);
                    occ.Take(focal, objR, 0);
                    res.Centerpieces = 1;
                }
                else if (inner >= 3.2f)
                {
                    // No room for a centrepiece: a specimen tree is the focal point.
                    var tree = AssetMix.Pick(string.IsNullOrEmpty(ctx.S("treeAsset")) ? PlazaArt.LimeTrees : new[] { ctx.S("treeAsset") }, 0);
                    objR = LeisureKit.Put(ctx, occ, focal, Hashing.Unit(variant) * 6.28f, ObjectRole.Tree, tree, null, 1.2f, variant, 0.5f, 0.2f, 3, 3) ? 1.4f : 0f;
                }
                else if (inner >= 1.8f)
                {
                    // A tiny square: a cluster of flower pots marks the middle.
                    objR = 0f;
                    for (int k = 0; k < 3; k++)
                    {
                        float a = yaw0 + k * 2.094f;
                        var p = focal + new float2(math.cos(a), math.sin(a)) * 0.7f;
                        uint v = Hashing.Mix(variant, 0x9070u + (uint)k);
                        if (LeisureKit.Put(ctx, occ, p, a, ObjectRole.Planter, PlazaArt.Or(ctx, "planterAsset", PlazaArt.Pots, v), AssetCategories.Planter, 0.45f, v, 0.4f, 0.05f))
                            keepClear = 1.6f;
                    }
                }
                else objR = 0f;
                if (objR > 0f)
                {
                    // Seating ring on the pad's edge when it fits with a clear field round it, else pad + edge benches.
                    ringR = objR + 2.2f;
                    seatsOnRing = tier != PlazaTier.Micro ? ringR + 1.0f <= inner - 1.0f : ringR + 0.8f <= inner - 0.6f;
                    padR = seatsOnRing ? ringR + 0.9f : math.min(objR + 1.2f, inner - 1.0f);
                    keepClear = seatsOnRing ? ringR + 0.8f : padR + 0.5f;
                }
            }
            if (padR > inner - 0.9f) padR = inner - 0.9f;   // the pad never touches the band: a clear field ring between
            if (padR < objR + 0.6f || padR < 1.6f) padR = 0f;
            res.CoreRadius = math.max(padR, keepClear);
            if (keepClear > 0f && fountainHere) occ.Take(focal, keepClear, 0);
            ctx.Check();

            // ── seating ──
            var bench = PlazaArt.Or(ctx, "benchAsset", PlazaArt.Benches, 0);
            int benches = 0;
            if (!fountainHere && objR > 0f && seatsOnRing) benches = BenchRing(ctx, occ, region, gates, focal, ringR, bw, bench, variant);

            // ── a long strip (street plaza, sliver) reads as an allée: trees on its spine, benches between them ──
            PlazaDressing.Extents(region, focal, axis, out float minA, out float maxA, out _, out _);
            bool strip = (maxA - minA) >= room * 2f * 2.6f;
            bool trees = ctx.S("treeLayout") != "none";
            if (strip && trees) benches += Allee(ctx, occ, region, gates, focal, axis, math.max(keepClear, objR + 1f), bw, PlazaLayer.TreeSpecies(palette), bench, variant);
            if (!fountainHere && benches < 2) benches += EdgeBenches(ctx, occ, region, gates, focal, bw, bench, variant, tier == PlazaTier.Micro ? 2 : 4);
            ctx.Check();

            // ── trees in the corner pockets (small squares) ──
            if (tier == PlazaTier.Small && trees && !strip)
                CornerTrees(ctx, occ, region, gates, focal, keepClear, bw, PlazaLayer.TreeSpecies(palette));

            // ── lamps on the edge rhythm ──
            var lampDesigned = palette.Id == GroundPalettes.Modern ? PlazaArt.ModernLamp : PlazaArt.ParkLamp;
            EdgeLamps(ctx, occ, region, gates, focal, keepClear, bw, PlazaArt.Or(ctx, "lampAsset", lampDesigned), tier);
            PlazaDressing.Bollards(ctx, occ, region, gates);

            // ── ground: pad, then the band, the field takes the rest (PavingPlan tiling) ──
            if (padR > 0f) pave.Add(PavingShapes.Polygon(focal, padR, 36, math.atan2(axis.y, axis.x)), SurfaceRole.Plaza, palette.Focal, AssetCategories.SurfacePaving);
            pave.Add(PavingShapes.Frame(region.Ring, bw), SurfaceRole.Plaza, palette.Band, AssetCategories.SurfacePaving);
            ctx.Plan.Facts["plazaTier"] = (int)tier;
            ctx.Plan.Facts["plazaBand"] = bw;
            ctx.Plan.Facts["plazaPad"] = padR;
            return res;
        }

        /// <summary>Benches on a ring facing the focal point, in point-symmetric pairs; gaps face the entrances.</summary>
        private static int BenchRing(LayerContext ctx, Occupancy occ, LayerRegion region, List<Gate> gates, float2 c, float r, float bw, AssetRef bench, uint variant)
        {
            int n = math.clamp((int)(2f * math.PI * r / 5.5f), 2, 8) & ~1;
            float phase = 0f;
            if (gates.Count > 0)
            {
                var g = gates[0].Position - c;
                phase = math.atan2(g.y, g.x) + math.PI / n;   // the first gate looks through a gap
            }
            int placed = 0;
            for (int i = 0; i < n / 2; i++)
            {
                var pair = new float2[2];
                bool ok = true;
                for (int k = 0; k < 2; k++)
                {
                    float a = phase + (i + k * n / 2) * 2f * math.PI / n;
                    pair[k] = c + new float2(math.cos(a), math.sin(a)) * r;
                    if (!region.ContainsDisc(pair[k], bw + 1.2f) || !occ.Free(pair[k], 1.1f, bw + 0.4f, 0.2f) || PlazaDressing.NearGate(pair[k], gates, 3f)) ok = false;
                }
                if (!ok) continue;   // a lone half of a pair reads as a mistake
                foreach (var p in pair)
                {
                    ctx.Object(p, LeisureKit.Yaw(p, c), ObjectRole.Bench, bench, Hashing.Mix(variant, 0xBE00u + (uint)placed));
                    occ.Take(p, 1.1f, 1);
                    placed++;
                }
            }
            return placed;
        }

        /// <summary>Benches at the middle of the longest edges, backs to the edge, facing the focal point.</summary>
        private static int EdgeBenches(LayerContext ctx, Occupancy occ, LayerRegion region, List<Gate> gates, float2 c, float bw, AssetRef bench, uint variant, int max)
        {
            var ring = region.Ring;
            var edges = new List<(float len, int i)>();
            for (int i = 0; i < ring.Length; i++) edges.Add((math.distance(ring[i], ring[(i + 1) % ring.Length]), i));
            edges.Sort((a, b) => b.len.CompareTo(a.len));
            int placed = 0;
            foreach (var (len, i) in edges)
            {
                if (placed >= max || len < 7f) break;
                var a = ring[i]; var b = ring[(i + 1) % ring.Length];
                var inward = region.InwardNormal(i);
                // Two benches flanking the edge's middle on a long edge, one on a short one.
                int count = len >= 22f ? 2 : 1;
                for (int k = 0; k < count && placed < max; k++)
                {
                    float t = count == 1 ? 0.5f : (k == 0 ? 0.36f : 0.64f);
                    var p = math.lerp(a, b, t) + inward * (bw + 1.1f);
                    if (PlazaDressing.NearGate(p, gates, 4f) || !occ.Free(p, 1.1f, bw + 0.4f, 0.3f)) continue;
                    ctx.Object(p, LeisureKit.Yaw(p, p + inward), ObjectRole.Bench, bench, Hashing.Mix(variant, 0xED00u + (uint)placed));
                    occ.Take(p, 1.1f, 1);
                    placed++;
                }
            }
            return placed;
        }

        /// <summary>
        /// Trees in a row on the strip's spine either side of the focal piece (8 m apart), a pair of benches back to
        /// back between neighbours, facing the long sides. Returns benches placed.
        /// </summary>
        private static int Allee(LayerContext ctx, Occupancy occ, LayerRegion region, List<Gate> gates, float2 focal, float2 axis, float clear, float bw,
            string[] species, AssetRef bench, uint variant)
        {
            var ids = string.IsNullOrEmpty(ctx.S("treeAsset")) ? species : new[] { ctx.S("treeAsset") };
            var side = new float2(-axis.y, axis.x);
            const float pitch = 8f;
            int benches = 0, n = 0;
            foreach (float dir in new[] { -1f, 1f })
            {
                float2 prev = focal; bool havePrev = false;
                for (float t = clear + 2.6f; t < 400f; t += pitch)
                {
                    var p = focal + axis * (dir * t);
                    if (!region.ContainsDisc(p, bw + 1.9f)) break;
                    if (PlazaDressing.NearGate(p, gates, 5f)) { havePrev = false; continue; }
                    uint v = Hashing.Mix(ctx.Seed, 0xA11Eu + (uint)n);
                    if (!LeisureKit.Put(ctx, occ, p, Hashing.Unit(v) * 6.28f, ObjectRole.Tree, AssetMix.Pick(ids, 0), null, 1.6f, v, bw + 0.3f, 0.3f, 3, 3)) { havePrev = false; continue; }
                    n++;
                    if (havePrev)
                    {
                        var mid = (prev + p) * 0.5f;
                        foreach (float s in new[] { -1f, 1f })
                        {
                            var bp = mid + side * (s * 0.75f);
                            if (!occ.Free(bp, 1.0f, bw + 0.4f, 0.1f)) continue;
                            ctx.Object(bp, LeisureKit.Yaw(bp, bp + side * s), ObjectRole.Bench, bench, Hashing.Mix(variant, 0xA1B0u + (uint)benches));
                            occ.Take(bp, 1.0f, 1);
                            benches++;
                        }
                    }
                    prev = p; havePrev = true;
                }
            }
            ctx.Plan.Facts["plazaTrees"] = n;
            return benches;
        }

        /// <summary>A tree in each corner pocket deep enough for one (the dead space of a triangle's tips).</summary>
        private static void CornerTrees(LayerContext ctx, Occupancy occ, LayerRegion region, List<Gate> gates, float2 focal, float keepClear, float bw, string[] species)
        {
            var ids = string.IsNullOrEmpty(ctx.S("treeAsset")) ? species : new[] { ctx.S("treeAsset") };
            int n = 0;
            var ring = region.Ring;
            for (int i = 0; i < ring.Length; i++)
            {
                if (Poly.IsReflex(ring, i)) continue;
                // Walk in along the bisector until a crown fits clear of the band.
                var inner = region.InsetVertices(bw + 2.6f, 6f)[i];
                var p = inner;
                bool found = false;
                for (int s = 0; s < 6 && !found; s++)
                {
                    p = math.lerp(inner, focal, s * 0.06f);
                    found = region.ContainsDisc(p, bw + 2.2f);
                }
                if (!found || math.distance(p, focal) < keepClear + 3.2f || PlazaDressing.NearGate(p, gates, 6f)) continue;
                uint v = Hashing.Mix(ctx.Seed, 0xC7E0u + (uint)i);
                if (LeisureKit.Put(ctx, occ, p, Hashing.Unit(v) * 6.28f, ObjectRole.Tree, AssetMix.Pick(ids, 0), null, 2.2f, v, bw + 0.4f, 0.4f, 3, 3)) n++;
            }
            ctx.Plan.Facts["plazaTrees"] = n;
        }

        /// <summary>Lamps on the border band: in the corners, and every ~14 m along long edges.</summary>
        private static void EdgeLamps(LayerContext ctx, Occupancy occ, LayerRegion region, List<Gate> gates, float2 focal, float keepClear, float bw, AssetRef lamp, PlazaTier tier)
        {
            var ring = region.Ring;
            float inset = math.max(0.5f, bw * 0.5f);
            var corners = region.InsetVertices(inset, 4f);
            float pitch = tier == PlazaTier.Micro ? 18f : 14f;
            int n = 0;
            void Lamp(float2 p)
            {
                if (PlazaDressing.NearGate(p, gates, 3.5f) || math.distance(p, focal) < keepClear + 0.9f) return;
                if (LeisureKit.Put(ctx, occ, p, 0f, ObjectRole.Lamp, lamp, null, 0.4f, Hashing.Mix(ctx.Seed, 0x1A70u + (uint)n), inset - 0.2f, 0.6f)) n++;
            }
            for (int i = 0; i < ring.Length; i++)
            {
                // Corners that are not (nearly) straight: a lamp marks the turn.
                if (!Poly.IsReflex(ring, i))
                {
                    var a = ring[(i - 1 + ring.Length) % ring.Length]; var b = ring[i]; var c = ring[(i + 1) % ring.Length];
                    float turn = math.abs(math.dot(math.normalizesafe(b - a), math.normalizesafe(c - b)));
                    if (turn < 0.97f) Lamp(corners[i]);
                }
                var e0 = ring[i]; var e1 = ring[(i + 1) % ring.Length];
                float len = math.distance(e0, e1);
                int k = (int)math.floor(len / pitch);
                if (k < 1) continue;
                var inward = region.InwardNormal(i);
                for (int j = 1; j <= k; j++) Lamp(math.lerp(e0, e1, j / (float)(k + 1)) + inward * inset);
            }
            ctx.Plan.Facts["plazaLamps"] = n;
        }
    }
}
