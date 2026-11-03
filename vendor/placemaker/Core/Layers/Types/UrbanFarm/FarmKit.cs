// Geometry and placement helpers shared by the farm types (urban farm, animal park): cutting a
// region into field cells along straight lines, insetting/clipping/carving the cells, parallel
// crop rows clipped to a field, items along a row, boundary hedgerows and tree rows with gaps at
// the entrances. Kept here (not in Common) so the type wave never edits shared files.

using System;
using System.Collections.Generic;
using Placemaker.Core.Geometry;
using Placemaker.Core.Layers.Common;
using Unity.Mathematics;

namespace Placemaker.Core.Layers.Types.UrbanFarm
{
    public static class FarmKit
    {
        public static float2 Perp(float2 d) => new float2(-d.y, d.x);

        public static float Yaw(float2 dir) => math.lengthsq(dir) < 1e-8f ? 0f : math.atan2(dir.x, dir.y);

        /// <summary>Projection range of a ring onto a direction, relative to an origin.</summary>
        public static float2 Span(IReadOnlyList<float2> ring, float2 origin, float2 dir)
        {
            float lo = float.MaxValue, hi = float.MinValue;
            foreach (var v in ring)
            {
                float d = math.dot(v - origin, dir);
                lo = math.min(lo, d); hi = math.max(hi, d);
            }
            return new float2(lo, hi);
        }

        /// <summary>
        /// Offsets k·spacing (+ shift·spacing) from <paramref name="origin"/> along <paramref name="dir"/> that fall
        /// strictly inside the region's projection, at least <paramref name="keep"/> m from either end.
        /// </summary>
        public static List<float> Offsets(LayerRegion region, float2 origin, float2 dir, float spacing, float shift, float keep = 8f)
        {
            var r = new List<float>();
            if (spacing <= 0.5f) return r;
            var span = Span(region.Ring, origin, dir);
            int k0 = (int)math.floor(span.x / spacing) - 1, k1 = (int)math.ceil(span.y / spacing) + 1;
            for (int k = k0; k <= k1 && r.Count < 64; k++)
            {
                float o = (k + shift) * spacing;
                if (o > span.x + keep && o < span.y - keep) r.Add(o);
            }
            return r;
        }

        /// <summary>Every inside piece of the infinite line through p along dir, inset at both ends.</summary>
        public static List<(float2 a, float2 b)> Chords(LayerRegion region, float2 p, float2 dir, float inset, float minLength = 6f)
        {
            var r = new List<(float2, float2)>();
            float reach = math.length(region.Max - region.Min) + 10f;
            foreach (var (s0, s1) in Poly.ClipSegmentToPolygon(region.Ring, p - dir * reach, p + dir * reach))
            {
                float len = math.distance(s0, s1);
                if (len < minLength + 2f * inset) continue;
                var d = (s1 - s0) / len;
                r.Add((s0 + d * inset, s1 - d * inset));
            }
            return r;
        }

        /// <summary>Cuts a ring by infinite lines (point, direction); at most <paramref name="max"/> pieces.</summary>
        public static List<float2[]> SplitByLines(float2[] ring, List<(float2 p, float2 dir)> lines, int max, System.Threading.CancellationToken cancel)
        {
            var pieces = new List<float2[]> { ring };
            foreach (var (p, dir) in lines)
            {
                cancel.ThrowIfCancellationRequested();
                var next = new List<float2[]>();
                foreach (var piece in pieces)
                {
                    // Skip pieces the line misses (cheap projection test) — keeps the arrangement work small.
                    var n = Perp(dir);
                    var span = Span(piece, p, n);
                    if (span.x >= -0.01f || span.y <= 0.01f || next.Count + pieces.Count > max * 2) { next.Add(piece); continue; }
                    List<float2[]> parts;
                    try { parts = Poly.SplitByLine(piece, p, dir); }
                    catch (Exception) { parts = null; }
                    if (parts == null || parts.Count == 0) next.Add(piece);
                    else next.AddRange(parts);
                }
                pieces = next;
            }
            if (pieces.Count > max) pieces.RemoveRange(max, pieces.Count - max);
            return pieces;
        }

        public static float2[] Largest(List<float2[]> rings)
        {
            float2[] best = null; float area = 0f;
            if (rings == null) return null;
            foreach (var r in rings)
            {
                float a = Poly.Area(r);
                if (a > area) { area = a; best = r; }
            }
            return best;
        }

        private static void AddRegions(List<Region> regions, List<float2[]> into, float minArea)
        {
            foreach (var reg in regions)
            {
                if (reg?.Outer == null || reg.Outer.Length < 3) continue;
                if (!reg.HasHoles) { if (Poly.Area(reg.Outer) >= minArea) into.Add(Poly.EnsureCcw(reg.Outer)); continue; }
                foreach (var piece in Poly.CutHoles(reg)) if (Poly.Area(piece) >= minArea) into.Add(piece);
            }
        }

        /// <summary>Pieces of <paramref name="ring"/> inside <paramref name="keep"/>.</summary>
        public static List<float2[]> Clip(float2[] ring, float2[] keep, float minArea)
        {
            var r = new List<float2[]>();
            if (keep == null) { r.Add(ring); return r; }
            try { AddRegions(Poly.Intersection(ring, keep), r, minArea); }
            catch (Exception) { }
            return r;
        }

        /// <summary>Removes <paramref name="blocker"/> from every piece that overlaps its bounds.</summary>
        public static List<float2[]> Carve(List<float2[]> pieces, float2[] blocker, float minArea)
        {
            if (blocker == null || blocker.Length < 3) return pieces;
            Poly.Bounds(blocker, out var bmin, out var bmax);
            var r = new List<float2[]>(pieces.Count);
            foreach (var p in pieces)
            {
                Poly.Bounds(p, out var mn, out var mx);
                if (mx.x < bmin.x || mx.y < bmin.y || mn.x > bmax.x || mn.y > bmax.y) { r.Add(p); continue; }
                try { AddRegions(Poly.Difference(p, blocker), r, minArea); }
                catch (Exception) { r.Add(p); }
            }
            return r;
        }

        /// <summary>Rectangle around a segment (half width either side, extended by the half width at the ends).</summary>
        public static float2[] SegmentRect(float2 a, float2 b, float halfWidth)
        {
            var d = math.normalizesafe(b - a, new float2(1, 0));
            var c = (a + b) * 0.5f;
            return LayerRegion.Rect(c, d, math.distance(a, b) * 0.5f + halfWidth, halfWidth);
        }

        /// <summary>
        /// Parallel rows across a field: lines along <paramref name="dir"/> every <paramref name="spacing"/> m
        /// (first half a spacing in), clipped to the field and shortened by <paramref name="endInset"/> at both ends.
        /// </summary>
        public static List<(float2 a, float2 b)> Rows(float2[] field, float2 dir, float spacing, float endInset)
        {
            var r = new List<(float2, float2)>();
            if (spacing < 0.3f) return r;
            var origin = Poly.Centroid(field);
            var n = Perp(dir);
            var span = Span(field, origin, n);
            var along = Span(field, origin, dir);
            float reach = (along.y - along.x) + 10f;
            int rows = 0;
            for (float o = span.x + spacing * 0.5f; o < span.y && rows < 2000; o += spacing, rows++)
            {
                var p = origin + n * o;
                foreach (var (s0, s1) in Poly.ClipSegmentToPolygon(field, p - dir * reach, p + dir * reach))
                {
                    float len = math.distance(s0, s1);
                    if (len <= endInset * 2f + 0.5f) continue;
                    var d = (s1 - s0) / len;
                    r.Add((s0 + d * endInset, s1 - d * endInset));
                }
            }
            return r;
        }

        /// <summary>
        /// Packs equal rectangles (glasshouses, raised beds, pens) in rows along <paramref name="dir"/>,
        /// each row centred in the part of the field it crosses, so a field just long enough still gets
        /// its full count. Checks the occupancy with its spatial index only (no global scans), so
        /// thousands of beds stay cheap. Takes the rectangles when <paramref name="take"/>.
        /// </summary>
        public static List<OrientedRect> PackRows(LayerRegion field, Occupancy occ, float2 dir, float length, float width,
            float gapAlong, float gapAcross, int max, float margin, bool take)
        {
            var result = new List<OrientedRect>();
            if (field == null || max <= 0) return result;
            var n = Perp(dir);
            var origin = field.Centroid;
            var spanB = Span(field.Ring, origin, n);
            var spanA = Span(field.Ring, origin, dir);
            float reach = (spanA.y - spanA.x) + 10f;
            float pitchB = width + gapAcross, pitchA = length + gapAlong;
            int rowsB = (int)math.floor((spanB.y - spanB.x - 2f * margin + gapAcross) / pitchB);
            if (rowsB <= 0) return result;
            float startB = (spanB.x + spanB.y) * 0.5f - (rowsB - 1) * pitchB * 0.5f;
            float hw = width * 0.5f, hl = length * 0.5f;
            for (int ib = 0; ib < rowsB && result.Count < max; ib++)
            {
                float b = startB + ib * pitchB;
                var lo = Intervals(field.Ring, origin + n * (b - hw - margin), dir, origin, reach);
                var hi = Intervals(field.Ring, origin + n * (b + hw + margin), dir, origin, reach);
                foreach (var a in lo)
                    foreach (var c in hi)
                    {
                        float s0 = math.max(a.x, c.x) + margin, s1 = math.min(a.y, c.y) - margin;
                        int k = (int)math.floor((s1 - s0 + gapAlong) / pitchA);
                        if (k <= 0) continue;
                        float start = (s0 + s1) * 0.5f - (k - 1) * pitchA * 0.5f;
                        for (int ia = 0; ia < k && result.Count < max; ia++)
                        {
                            var r = new OrientedRect { Center = origin + dir * (start + ia * pitchA) + n * b, Axis = dir, HalfLength = hl, HalfWidth = hw };
                            if (!RectFree(field, occ, r)) continue;
                            result.Add(r);
                            if (take) occ.TakeCorridor(r.Local(-hl + math.min(hl, hw), 0f), r.Local(hl - math.min(hl, hw), 0f), hw, 2);
                        }
                    }
            }
            return result;
        }

        private static List<float2> Intervals(float2[] ring, float2 p, float2 dir, float2 origin, float reach)
        {
            var r = new List<float2>();
            foreach (var (s0, s1) in Poly.ClipSegmentToPolygon(ring, p - dir * reach, p + dir * reach))
            {
                float a = math.dot(s0 - origin, dir), b = math.dot(s1 - origin, dir);
                r.Add(new float2(math.min(a, b), math.max(a, b)));
            }
            return r;
        }

        private static bool RectFree(LayerRegion field, Occupancy occ, OrientedRect r)
        {
            foreach (var q in r.Ring) if (!field.Contains(q)) return false;
            float step = math.max(0.8f, r.HalfWidth * 2f);
            for (float u = -r.HalfLength + r.HalfWidth * 0.5f; u <= r.HalfLength - r.HalfWidth * 0.5f + 0.01f; u += step)
                if (!occ.Free(r.Local(u, 0f), r.HalfWidth, 0f, 0.1f)) return false;
            return true;
        }

        /// <summary>
        /// Benches (or lamps) beside this layer's own paths every <paramref name="spacing"/> m, alternating
        /// sides, facing the path. Linear in path length (PathAnchors ranks every candidate, too slow for
        /// a grid of farm tracks).
        /// </summary>
        public static int BesidePaths(LayerContext ctx, Occupancy occ, float spacing, ObjectRole role, AssetRef asset, float radius, uint salt, int max = 400)
        {
            if (spacing <= 0.5f) return 0;
            int n = 0;
            foreach (var w in Walk(occ, spacing, 0.5f))
            {
                if (n >= max) break;
                float sideSign = (w.index & 1) == 0 ? 1f : -1f;
                var nrm = Perp(w.tangent);
                float off = w.halfWidth + radius + 0.35f;
                var p = w.onPath + nrm * sideSign * off;
                if (!occ.Free(p, radius, 0.3f, 0.15f)) { p = w.onPath - nrm * sideSign * off; if (!occ.Free(p, radius, 0.3f, 0.15f)) continue; }
                ctx.Object(p, role == ObjectRole.Bench ? LeisureKit.Yaw(p, w.onPath) : 0f, role, asset, Hashing.Mix(ctx.Seed, salt + (uint)w.index));
                occ.Take(p, radius, 1);
                n++;
            }
            return n;
        }

        /// <summary>
        /// Points every <paramref name="spacing"/> m along this layer's own paths, measured cumulatively over
        /// consecutive segments (curved paths are indexed as many short pieces), starting at phase·spacing.
        /// </summary>
        public static List<(float2 onPath, float2 tangent, float halfWidth, int index)> Walk(Occupancy occ, float spacing, float phase)
        {
            var r = new List<(float2, float2, float, int)>();
            if (spacing <= 0.1f) return r;
            float acc = 0f, next = spacing * phase;
            foreach (var s in occ.Paths.Segments)
            {
                if (s.Kind == Occupancy.KindReserved) continue;
                float len = math.distance(s.A, s.B);
                if (len < 1e-3f) continue;
                var t = (s.B - s.A) / len;
                while (next <= acc + len && r.Count < 5000)
                {
                    r.Add((s.A + t * (next - acc), t, s.HalfWidth, r.Count));
                    next += spacing;
                }
                acc += len;
            }
            return r;
        }

        /// <summary>Role that matches an asset category (so automatic resolution and footprints agree).</summary>
        public static ObjectRole RoleFor(string category)
        {
            switch (category)
            {
                case AssetCategories.Tree: return ObjectRole.Tree;
                case AssetCategories.Bush: return ObjectRole.Bush;
                case AssetCategories.Planter: return ObjectRole.Planter;
                case AssetCategories.Bench: return ObjectRole.Bench;
                case AssetCategories.Bin: return ObjectRole.Bin;
                case AssetCategories.Lamp: return ObjectRole.Lamp;
                default: return ObjectRole.Furniture;
            }
        }

        /// <summary>
        /// Items every <paramref name="spacing"/> m along a closed ring inset <paramref name="inset"/> m from the
        /// outline, skipping <paramref name="gateClear"/> m around each gate. Returns the count placed.
        /// </summary>
        public static int Edge(LayerContext ctx, Occupancy occ, LayerRegion region, float inset, float spacing, List<Gate> gates, float gateClear,
            string category, string[] ids, float radius, uint salt, int ageMin = -1, int ageMax = -1, int max = 2000)
        {
            if (category == null || spacing <= 0.3f) return 0;
            float2[] ring;
            try { ring = Largest(Poly.Offset(region.Ring, inset)); }
            catch (Exception) { ring = null; }
            if (ring == null) return 0;
            var role = RoleFor(category);
            int placed = 0;
            float perimeter = Poly.Perimeter(ring);
            int n = math.min(max, (int)(perimeter / spacing));
            float walked = 0f; int seg = 0;
            float segLen = math.distance(ring[0], ring[1 % ring.Length]);
            for (int i = 0; i < n; i++)
            {
                float target = (i + 0.5f) * spacing;
                while (walked + segLen < target && seg < ring.Length - 1)
                {
                    walked += segLen; seg++;
                    segLen = math.distance(ring[seg], ring[(seg + 1) % ring.Length]);
                }
                var a = ring[seg]; var b = ring[(seg + 1) % ring.Length];
                var t = segLen > 1e-4f ? (b - a) / segLen : new float2(1, 0);
                var p = a + t * math.clamp(target - walked, 0f, segLen);
                bool nearGate = false;
                foreach (var g in gates) if (math.distancesq(g.Position, p) < gateClear * gateClear) { nearGate = true; break; }
                if (nearGate || !occ.Free(p, radius, 0f, 0.1f)) continue;
                uint v = Hashing.Mix(ctx.Seed, salt + (uint)i);
                int age = ageMin >= 0 ? ageMin + (int)(v % (uint)math.max(1, ageMax - ageMin + 1)) : -1;
                ctx.Object(p, role == ObjectRole.Tree ? Hashing.Unit(v) * 6.283f : Yaw(t), role, AssetMix.Pick(ids, v), v, age, category);
                occ.Take(p, radius, role == ObjectRole.Tree ? 3 : 4);
                placed++;
            }
            return placed;
        }

        /// <summary>True when p lies in one of the rings (bounding-box prefilter).</summary>
        public sealed class RingSet
        {
            private readonly List<float2[]> _rings = new List<float2[]>();
            private readonly List<float4> _boxes = new List<float4>();
            public int Count => _rings.Count;
            public void Add(float2[] ring)
            {
                Poly.Bounds(ring, out var mn, out var mx);
                _rings.Add(ring); _boxes.Add(new float4(mn, mx));
            }
            public bool Contains(float2 p, float grow = 0f)
            {
                for (int i = 0; i < _rings.Count; i++)
                {
                    var b = _boxes[i];
                    if (p.x < b.x - grow || p.y < b.y - grow || p.x > b.z + grow || p.y > b.w + grow) continue;
                    if (Poly.Contains(_rings[i], p)) return true;
                    if (grow > 0f && Poly.DistanceToBoundary(_rings[i], p) < grow) return true;
                }
                return false;
            }
        }
    }
}
