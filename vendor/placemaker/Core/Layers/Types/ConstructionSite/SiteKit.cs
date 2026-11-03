// Shared building blocks of the industrial work types (construction site, quarry, spoil heaps):
// a height-field sculptor that turns any target function into terraform cells blended back to the
// original ground at the outline, ribbon tracks (haul roads) as surfaces + mud paint, rows of props
// along a line (cones, barriers, conveyors), heap clusters, and mud decal scatter. Everything goes
// through Occupancy so input.Reserved is respected and nothing overlaps.

using System;
using System.Collections.Generic;
using Placemaker.Core.Geometry;
using Placemaker.Core.Layers.Common;
using Unity.Mathematics;

namespace Placemaker.Core.Layers.Types.ConstructionSite
{
    public sealed class SculptResult
    {
        public int Cells;
        public float MaxCut, MaxFill;
        /// <summary>Side of the square terraform cells (a cell changes ground up to half a diagonal from its centre).</summary>
        public float CellSize;
        public Func<float2, float> HeightAfter;
    }

    public static class SiteKit
    {
        public const int MaxCells = 2500;
        public const int KindProp = 7;
        public const int KindTrack = 6;

        /// <summary>
        /// First category of the chain that is available (or the first when the player picked assets).
        /// Adds the shared "no assets" Info issue and returns null when nothing can be placed.
        /// </summary>
        public static string Cat(LayerContext ctx, string[] chosen, params string[] chain)
        {
            if (chosen != null)
                foreach (var id in chosen) if (!string.IsNullOrEmpty(id)) return chain[0];
            return LeisureKit.Category(ctx, "", chain);
        }

        /// <summary>Role an object of this category is planned with (bins/lamps/benches keep their own role).</summary>
        public static ObjectRole RoleFor(string category)
        {
            switch (category)
            {
                case AssetCategories.Lamp: case AssetCategories.FloodLight: return ObjectRole.Lamp;
                case AssetCategories.Bench: return ObjectRole.Bench;
                case AssetCategories.Bin: return ObjectRole.Bin;
                case AssetCategories.Tree: return ObjectRole.Tree;
                case AssetCategories.Bush: return ObjectRole.Bush;
                default: return ObjectRole.Furniture;
            }
        }

        /// <summary>Places one prop of a category (role derived from it) when its disc is free.</summary>
        public static bool Put(LayerContext ctx, Occupancy occ, float2 p, float yaw, string category, string[] ids, float radius, uint salt, float margin = 0.5f)
        {
            if (category == null) return false;
            uint v = Hashing.Mix(ctx.Seed, salt);
            var role = RoleFor(category);
            string cat = role == ObjectRole.Furniture || role == ObjectRole.Lamp ? category : null;
            return LeisureKit.Put(ctx, occ, p, yaw, role, AssetMix.Pick(ids, v), cat, radius, v, margin, 0.25f, KindProp);
        }

        // ───────────── terrain ─────────────

        /// <summary>
        /// Samples <paramref name="target"/> (p, original height → absolute target, NaN = leave) on a
        /// grid of square cells and emits one permanent terraform polygon per changed cell. Every
        /// change is clamped to <paramref name="maxSlopePct"/> × the distance to the outline, so the
        /// shape always blends back to the original ground at the edge (never a cliff at the border).
        /// </summary>
        public static SculptResult Sculpt(LayerContext ctx, LayerRegion region, Func<float2, float, float> target, float maxSlopePct, float minCell = 8f)
        {
            var result = new SculptResult { HeightAfter = ctx.H };
            float cell = math.max(minCell, math.sqrt(region.Area / MaxCells));
            result.CellSize = cell;
            int nx = math.max(1, (int)math.ceil((region.Max.x - region.Min.x) / cell));
            int ny = math.max(1, (int)math.ceil((region.Max.y - region.Min.y) / cell));
            var offset = new float[nx * ny];
            float slope = math.max(0.05f, maxSlopePct / 100f);
            for (int iy = 0; iy < ny; iy++)
            {
                ctx.Check();
                for (int ix = 0; ix < nx; ix++)
                {
                    var c = region.Min + (new float2(ix, iy) + 0.5f) * cell;
                    if (!region.Contains(c)) continue;
                    float h0 = ctx.H(c);
                    float t = target(c, h0);
                    if (float.IsNaN(t) || float.IsInfinity(t)) continue;
                    float o = t - h0;
                    if (math.abs(o) < 0.2f) continue;
                    var square = LayerRegion.Rect(c, new float2(1, 0), cell * 0.5f, cell * 0.5f);
                    float2[] ring = square;
                    bool whole = true;
                    foreach (var q in square) if (!region.Contains(q)) { whole = false; break; }
                    if (!whole)
                    {
                        var pieces = Poly.Intersection(square, region.Ring);
                        if (pieces.Count == 0 || pieces[0].Outer == null || pieces[0].Outer.Length < 3) continue;
                        ring = pieces[0].Outer;
                    }
                    float d = region.BoundaryDistance(c);
                    if (!whole) d = math.min(d, region.BoundaryDistance(Poly.Centroid(ring)));
                    o = math.clamp(o, -slope * d, slope * d);
                    if (math.abs(o) < 0.2f) continue;
                    offset[iy * nx + ix] = o;
                    ctx.Plan.Terraform.Add(new PlannedTerraform
                    {
                        Ring = Poly.To3D(ring, ctx.H), Kind = o > 0f ? TerraformKind.Raise : TerraformKind.Lower,
                        TargetHeight = h0 + o, Permanent = true,
                    });
                    result.Cells++;
                    result.MaxCut = math.max(result.MaxCut, -o);
                    result.MaxFill = math.max(result.MaxFill, o);
                }
            }
            if (result.Cells > 0)
            {
                bool warned = false;
                foreach (var i in ctx.Plan.Issues) if (i.Key == TerrainShaper.PermanentKey) { warned = true; break; }
                if (!warned) ctx.Issue(IssueSeverity.Warning, TerrainShaper.PermanentKey);
                var min = region.Min; var off = offset; int w = nx, h = ny; var H = (Func<float2, float>)ctx.H;
                result.HeightAfter = p =>
                {
                    int ix = (int)math.floor((p.x - min.x) / cell), iy = (int)math.floor((p.y - min.y) / cell);
                    if (ix < 0 || iy < 0 || ix >= w || iy >= h) return H(p);
                    return H(p) + off[iy * w + ix];
                };
                ctx.Plan.Facts["terrainCells"] = result.Cells;
                ctx.Plan.Facts["terrainMaxLower"] = result.MaxCut;
                ctx.Plan.Facts["terrainMaxRaise"] = result.MaxFill;
            }
            return result;
        }

        /// <summary>Smooth 0..1 step.</summary>
        public static float Smooth(float e0, float e1, float x)
        {
            if (e1 < e0) return 1f - Smooth(e1, e0, x);   // reversed edges: falls from 1 to 0
            if (e1 == e0) return x >= e0 ? 1f : 0f;
            float t = math.saturate((x - e0) / (e1 - e0));
            return t * t * (3f - 2f * t);
        }

        // ───────────── tracks ─────────────

        /// <summary>
        /// A haul road / track along a polyline: one ribbon surface (clipped to the region, split where
        /// CS2 needs it), optional mud paint under it, and an occupancy corridor so props keep off it.
        /// Returns the ribbon rings.
        /// </summary>
        public static List<float2[]> Track(LayerContext ctx, Occupancy occ, LayerRegion region, IReadOnlyList<float2> line, float width,
            string surfaceCategory, AssetRef asset, int paintLayer)
        {
            var rings = new List<float2[]>();
            if (line == null || line.Count < 2) return rings;
            float hw = width * 0.5f;
            var rects = new List<float2[]>();
            for (int i = 0; i + 1 < line.Count; i++)
            {
                var a = line[i]; var b = line[i + 1];
                float len = math.distance(a, b);
                if (len < 0.2f) continue;
                var axis = (b - a) / len;
                rects.Add(LayerRegion.Rect((a + b) * 0.5f, axis, len * 0.5f + hw * 0.6f, hw));
                occ.TakeCorridor(a, b, hw, KindTrack);
            }
            if (rects.Count == 0) return rings;
            List<Region> union;
            try { union = Poly.UnionAll(rects); }
            catch (Exception) { return rings; }
            foreach (var u in union)
            {
                if (u.Outer == null || u.Outer.Length < 3) continue;
                List<Region> inside;
                try { inside = Poly.Intersection(u.Outer, region.Ring); }
                catch (Exception) { continue; }
                foreach (var piece in inside)
                {
                    if (piece.Outer == null || piece.Outer.Length < 3 || Poly.Area(piece.Outer) < 4f) continue;
                    var ring = Poly.Simplify(piece.Outer, 0.15f);
                    if (ring.Length < 3) continue;
                    rings.Add(ring);
                }
            }
            foreach (var ring in rings)
            {
                if (!string.IsNullOrEmpty(surfaceCategory)) ctx.Surface(ring, SurfaceRole.Ground, asset, surfaceCategory);
                if (paintLayer > 0) TerrainPaint.Patch(ctx, ring, paintLayer, 0.85f, 1.5f);
            }
            return rings;
        }

        // ───────────── rows, clusters, decals ─────────────

        /// <summary>Props every <paramref name="spacing"/> m along a→b, yawed along the line. Returns the count placed.</summary>
        public static int Row(LayerContext ctx, Occupancy occ, float2 a, float2 b, float spacing, string category, string[] ids, float radius, uint salt,
            bool across = false)
        {
            if (category == null || spacing <= 0.5f) return 0;
            int n = 0, k = 0;
            var d = math.normalizesafe(b - a);
            var dir = across ? new float2(-d.y, d.x) : d;
            float yaw = math.atan2(dir.x, dir.y);
            foreach (var p in LeisureKit.Along(a, b, spacing))
                if (Put(ctx, occ, p, yaw, category, ids, radius, salt + (uint)k++ * 7919u, 0.3f)) n++;
            return n;
        }

        /// <summary>A cluster of heaps/props around a centre (golden-angle spiral, jittered). Returns the count placed.</summary>
        public static int Cluster(LayerContext ctx, Occupancy occ, float2 centre, int count, float spread, string category, string[] ids, float radius, uint salt)
        {
            if (category == null || count <= 0) return 0;
            int n = 0;
            var rng = Hashing.Rng(ctx.Seed, salt);
            for (int i = 0; i < count * 3 && n < count; i++)
            {
                float r = spread * math.sqrt((i + 0.5f) / (count * 1.5f));
                float a = i * 2.39996f + rng.NextFloat(-0.3f, 0.3f);
                var p = centre + new float2(math.cos(a), math.sin(a)) * math.min(r, spread);
                if (Put(ctx, occ, p, rng.NextFloat(0f, 6.283f), category, ids, radius, salt + (uint)i * 31u)) n++;
            }
            return n;
        }

        /// <summary>
        /// Mud / dirt decals scattered (clustered) over the free part of a ring. Decals never reserve
        /// space but keep off placed props, reserved space and the outline. Returns the count placed.
        /// </summary>
        public static int Decals(LayerContext ctx, Occupancy occ, IReadOnlyList<float2> ring, float perHundredM2, string category, uint salt,
            Func<float2, bool> reject = null, int cap = 400)
        {
            if (category == null || perHundredM2 <= 0f || ring == null || ring.Count < 3) return 0;
            var area = LayerRegion.From(ring);
            if (area == null) return 0;
            int wanted = math.min(cap, (int)(area.Area / 100f * perHundredM2));
            if (wanted <= 0) return 0;
            int n = 0;
            uint clump = Hashing.Mix(ctx.Seed, salt);
            foreach (var p in Scatter.Candidates(area, 1.5f, Hashing.Mix(ctx.Seed, salt + 1u), wanted * 6 + 50, ctx.Cancel))
            {
                if (n >= wanted) break;
                if (Hashing.Unit(Hashing.Mix(clump, (int)(p.x * 11f), (int)(p.y * 5f))) > Scatter.Clustering(p, 14f, clump, 0.3f)) continue;
                if (reject != null && reject(p)) continue;
                if (!occ.LayerRegion.ContainsDisc(p, 0.8f) || occ.Discs.Overlaps(p, 0.6f, 0f) || occ.HitsPolygon(p, 0.6f)) continue;
                uint v = Hashing.Mix(ctx.Seed, salt * 17u + (uint)n);
                ctx.Object(p, Hashing.Unit(v) * 6.283f, ObjectRole.Decal, new AssetRef(""), v, -1, category);
                n++;
            }
            return n;
        }

        // ───────────── polygons ─────────────

        /// <summary>Largest piece of the ring offset inwards by <paramref name="inward"/>, or null.</summary>
        public static float2[] Inset(IReadOnlyList<float2> ring, float inward)
        {
            try
            {
                var pieces = Poly.Offset(ring, inward);
                float2[] best = null; float bestArea = 0f;
                foreach (var p in pieces)
                {
                    float a = Poly.Area(p);
                    if (a > bestArea) { bestArea = a; best = p; }
                }
                return best == null || best.Length < 3 ? null : Poly.EnsureCcw(best);
            }
            catch (Exception) { return null; }
        }

        /// <summary>Point at arc length <paramref name="s"/> along a closed ring (wraps) and its tangent.</summary>
        public static float2 AlongRing(float2[] ring, float s, out float2 tangent)
        {
            float per = Poly.Perimeter(ring);
            tangent = new float2(1, 0);
            if (ring.Length < 2 || per <= 0f) return ring.Length > 0 ? ring[0] : float2.zero;
            s = ((s % per) + per) % per;
            for (int i = 0; i < ring.Length; i++)
            {
                var a = ring[i]; var b = ring[(i + 1) % ring.Length];
                float len = math.distance(a, b);
                if (s <= len || i == ring.Length - 1)
                {
                    tangent = math.normalizesafe(b - a, tangent);
                    return math.lerp(a, b, len > 0f ? math.saturate(s / len) : 0f);
                }
                s -= len;
            }
            return ring[0];
        }

        /// <summary>Arc length of the point of the ring closest to p.</summary>
        public static float ArcOf(float2[] ring, float2 p)
        {
            float best = float.MaxValue, at = 0f, acc = 0f;
            for (int i = 0; i < ring.Length; i++)
            {
                var a = ring[i]; var b = ring[(i + 1) % ring.Length];
                var q = SegmentIndex.ClosestPoint(p, a, b);
                float d = math.distancesq(p, q);
                if (d < best) { best = d; at = acc + math.distance(a, q); }
                acc += math.distance(a, b);
            }
            return at;
        }
    }
}
