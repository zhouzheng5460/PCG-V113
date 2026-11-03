// Small shared building blocks for the leisure types (botanical garden, sports park, dog park,
// allotments, market square…): checked object placement with a category, packing oriented
// rectangles (courts, plots, parterres, stalls) along the region's principal axes, rows of items
// along a segment, straight path spines, and category fallback chains ("a stage, else a shelter,
// else a centrepiece") so a type still builds something sensible with whatever is installed.

using System;
using System.Collections.Generic;
using Placemaker.Core.Geometry;
using Unity.Mathematics;

namespace Placemaker.Core.Layers.Common
{
    /// <summary>An oriented rectangle: centre, unit long axis, half extents.</summary>
    public struct OrientedRect
    {
        public float2 Center, Axis;
        public float HalfLength, HalfWidth;
        public float2 Side => new float2(-Axis.y, Axis.x);
        public float2[] Ring => LayerRegion.Rect(Center, Axis, HalfLength, HalfWidth);
        public float2 Local(float u, float v) => Center + Axis * u + Side * v;
        public float Yaw => math.atan2(Axis.x, Axis.y);
    }

    public static class LeisureKit
    {
        /// <summary>
        /// First category of the chain that has assets installed (or the first when the player chose
        /// an asset). Adds the "no assets" Info issue only when nothing in the chain is available.
        /// Returns null when nothing can be placed.
        /// </summary>
        public static string Category(LayerContext ctx, string chosenAsset, params string[] chain)
        {
            if (!string.IsNullOrEmpty(chosenAsset)) return chain[0];
            foreach (var c in chain) if (AssetAvailability.Has(c)) return c;
            AssetAvailability.Require(ctx.Plan, chain[0], null, Keys.Category(chain[0]));
            return null;
        }

        /// <summary>Places one object when its disc is free; takes the space. Returns false when it did not fit.</summary>
        public static bool Put(LayerContext ctx, Occupancy occ, float2 p, float yaw, ObjectRole role, AssetRef asset, string category,
            float radius, uint variation, float margin = 0.3f, float gap = 0.2f, int kind = 1, int age = -1)
        {
            if (!occ.Free(p, radius, margin, gap)) return false;
            ctx.Object(p, yaw, role, asset, variation, age, string.IsNullOrEmpty(category) ? null : category);
            occ.Take(p, radius, kind);
            return true;
        }

        /// <summary>
        /// (s18) The natural focal point of a place: the centroid when it is (nearly) as deep as the shape
        /// allows, else the near-deepest point closest to the centroid — the incentre of a triangle, the
        /// middle of a long strip's medial line (its "deepest point" alone could sit anywhere along it).
        /// Plazas centre their focal piece here and the fountain add-on looks here first, so the
        /// fountain and the paving round it agree. <paramref name="inscribed"/> = largest inscribed radius.
        /// </summary>
        public static float2 FocalPoint(LayerRegion region, out float inscribed)
        {
            var deep = region.DeepestPoint(out inscribed, null, 32);
            var c = region.Centroid;
            if (inscribed <= 0f) return deep;
            if (region.Contains(c) && region.BoundaryDistance(c, inscribed + 1f) >= inscribed * 0.92f) return c;
            float want = inscribed * 0.92f;
            var best = deep; float bestD = math.distance(deep, c);
            var size = region.Max - region.Min;
            float step = math.max(0.25f, math.cmax(size) / 64f);
            for (float y = region.Min.y; y <= region.Max.y; y += step)
                for (float x = region.Min.x; x <= region.Max.x; x += step)
                {
                    var p = new float2(x, y);
                    float d = math.distance(p, c);
                    if (d >= bestD || !region.Contains(p) || region.BoundaryDistance(p, want + 0.01f) < want) continue;
                    best = p; bestD = d;
                }
            return best;
        }

        /// <summary>
        /// (s18) <see cref="FocalPoint(LayerRegion, out float)"/> of what is left of <paramref name="region"/> once the
        /// reserved polygons (places: street-shop land, amenity pads) are taken out — the same ground the parent
        /// laid out, so a child feature and its parent agree on the focal point. Largest remaining piece wins.
        /// </summary>
        public static float2 FocalPoint(LayerRegion region, ReservedSpace reserved, out float inscribed)
        {
            if (reserved != null && reserved.Polygons.Count > 0)
            {
                var clips = new List<IReadOnlyList<float2>>();
                foreach (var p in reserved.Polygons)
                {
                    if (p == null || p.Length < 3) continue;
                    Poly.Bounds(p, out var mn, out var mx);
                    if (mx.x < region.Min.x || mx.y < region.Min.y || mn.x > region.Max.x || mn.y > region.Max.y) continue;
                    clips.Add(p);
                }
                if (clips.Count > 0)
                {
                    try
                    {
                        var rest = Poly.Boolean(new[] { (IReadOnlyList<float2>)region.Ring }, clips, BoolOp.Difference);
                        Region best = null;
                        foreach (var r in rest) if (r?.Outer != null && (best == null || r.Area > best.Area)) best = r;
                        var lr = best != null && best.Area > region.Area * 0.05f ? LayerRegion.From(best.Outer) : null;
                        if (lr != null) return FocalPoint(lr, out inscribed);
                    }
                    catch (Exception) { }
                }
            }
            return FocalPoint(region, out inscribed);
        }

        public static float Yaw(float2 from, float2 to)
        {
            var d = to - from;
            return math.lengthsq(d) < 1e-6f ? 0f : math.atan2(d.x, d.y);
        }

        /// <summary>Region's principal frame (centre, long axis, spans).</summary>
        public static void Frame(LayerRegion region, out float2 centre, out float2 axis, out float2 side, out float majorSpan, out float minorSpan)
        {
            region.PrincipalAxes(out centre, out axis, out majorSpan, out minorSpan);
            side = new float2(-axis.y, axis.x);
            // Centre on the bounding box of the projection (the centroid of an L-shape is off-centre).
            float minA = float.MaxValue, maxA = float.MinValue, minB = float.MaxValue, maxB = float.MinValue;
            foreach (var v in region.Ring)
            {
                var d = v - centre;
                minA = math.min(minA, math.dot(d, axis)); maxA = math.max(maxA, math.dot(d, axis));
                minB = math.min(minB, math.dot(d, side)); maxB = math.max(maxB, math.dot(d, side));
            }
            var mid = centre + axis * (minA + maxA) * 0.5f + side * (minB + maxB) * 0.5f;
            if (region.Contains(mid)) centre = mid;
        }

        /// <summary>
        /// Frame for axial layouts that aims at the entrances (s9): the main axis runs along the inward normal of
        /// the first gate when the outline is near-square (else it stays the long axis), and both axes shift
        /// sideways (up to a quarter of the span) so they pass through the first gate facing them. Without
        /// gates, the plain Frame.
        /// </summary>
        public static void FrameToward(LayerRegion region, IReadOnlyList<float2> gates, out float2 centre, out float2 axis, out float2 side,
            out float majorSpan, out float minorSpan)
        {
            Frame(region, out centre, out axis, out side, out majorSpan, out minorSpan);
            if (gates == null || gates.Count == 0) return;
            var n0 = GateApproach.Inward(region, gates[0]);
            if (minorSpan >= majorSpan * 0.75f && math.abs(math.dot(n0, side)) > math.abs(math.dot(n0, axis)))
            {
                var t = axis; axis = side; side = -t;
                var s = majorSpan; majorSpan = minorSpan; minorSpan = s;
            }
            // Shift each axis line through the first gate facing it (gate normal along that axis).
            bool shiftedMain = false, shiftedCross = false;
            foreach (var g in gates)
            {
                var n = GateApproach.Inward(region, g);
                var d = g - centre;
                if (!shiftedMain && math.abs(math.dot(n, axis)) > 0.85f)
                {
                    float off = math.dot(d, side);
                    if (math.abs(off) <= minorSpan * 0.25f && region.Contains(centre + side * off)) { centre += side * off; }
                    shiftedMain = true;
                }
                else if (!shiftedCross && math.abs(math.dot(n, side)) > 0.85f)
                {
                    float off = math.dot(d, axis);
                    if (math.abs(off) <= majorSpan * 0.25f && region.Contains(centre + axis * off)) { centre += axis * off; }
                    shiftedCross = true;
                }
            }
        }

        /// <summary>True when the rectangle (plus margin) lies inside the region and is free in the occupancy.</summary>
        public static bool RectFits(LayerRegion region, Occupancy occ, OrientedRect r, float margin, float gap = 0.3f)
        {
            var grown = new OrientedRect { Center = r.Center, Axis = r.Axis, HalfLength = r.HalfLength + margin, HalfWidth = r.HalfWidth + margin };
            foreach (var q in grown.Ring) if (!region.Contains(q)) return false;
            var ring = grown.Ring;
            for (int i = 0; i < 4; i++)
                if (region.CrossesBoundary(ring[i], ring[(i + 1) % 4], false)) return false;
            // Sample the rectangle with small discs (spacing ≤ 1.5 m) against the occupancy.
            const float step = 1.5f, probe = 0.8f;
            for (float u = -r.HalfLength; u <= r.HalfLength + 0.01f; u += math.min(step, math.max(0.5f, r.HalfLength)))
                for (float v = -r.HalfWidth; v <= r.HalfWidth + 0.01f; v += math.min(step, math.max(0.5f, r.HalfWidth)))
                {
                    var p = r.Local(u, v);
                    if (occ.Discs.Overlaps(p, probe, gap) || occ.Corridors.Overlaps(p, 0.05f, gap) || occ.HitsPolygon(p, probe)) return false;
                }
            // Corridors also must not cross the rectangle between samples.
            var rr = r.Ring;
            foreach (var s in occ.Corridors.Segments)
                for (int i = 0; i < 4; i++)
                    if (Poly.SegmentsIntersect(s.A, s.B, rr[i], rr[(i + 1) % 4])) return false;
            return true;
        }

        /// <summary>Claims a rectangle in the occupancy (discs on a grid; corridors along the long sides).</summary>
        public static void TakeRect(Occupancy occ, OrientedRect r, int kind = 2)
        {
            float hw = r.HalfWidth;
            occ.TakeCorridor(r.Local(-r.HalfLength, 0f), r.Local(r.HalfLength, 0f), hw, kind);
        }

        /// <summary>
        /// Packs rectangles of the given size in rows across the region's principal frame (row-major,
        /// alternate rows offset by half a pitch when <paramref name="stagger"/>), keeping a gap between
        /// them. Deterministic; at most <paramref name="max"/>. Rectangles are taken in the occupancy.
        /// </summary>
        public static List<OrientedRect> Pack(LayerRegion region, Occupancy occ, float2 centre, float2 axis, float length, float width,
            float gapAlong, float gapAcross, int max, float margin, bool stagger = false, bool alongMajor = true)
        {
            var result = new List<OrientedRect>();
            if (max <= 0) return result;
            var a = alongMajor ? axis : new float2(-axis.y, axis.x);
            var s = new float2(-a.y, a.x);
            float minA = float.MaxValue, maxA = float.MinValue, minB = float.MaxValue, maxB = float.MinValue;
            foreach (var v in region.Ring)
            {
                var d = v - centre;
                minA = math.min(minA, math.dot(d, a)); maxA = math.max(maxA, math.dot(d, a));
                minB = math.min(minB, math.dot(d, s)); maxB = math.max(maxB, math.dot(d, s));
            }
            float pitchA = length + gapAlong, pitchB = width + gapAcross;
            // Centre the lattice on the frame centre so symmetric shapes pack symmetrically.
            int na0 = (int)math.floor(minA / pitchA) - 1, na1 = (int)math.ceil(maxA / pitchA) + 1;
            int nb0 = (int)math.floor(minB / pitchB) - 1, nb1 = (int)math.ceil(maxB / pitchB) + 1;
            for (int ib = nb0; ib <= nb1 && result.Count < max; ib++)
            {
                float off = stagger && (ib & 1) != 0 ? pitchA * 0.5f : 0f;
                for (int ia = na0; ia <= na1 && result.Count < max; ia++)
                {
                    var c = centre + a * (ia * pitchA + off) + s * (ib * pitchB);
                    var r = new OrientedRect { Center = c, Axis = a, HalfLength = length * 0.5f, HalfWidth = width * 0.5f };
                    if (!RectFits(region, occ, r, margin)) continue;
                    result.Add(r);
                    TakeRect(occ, r);
                }
            }
            return result;
        }

        /// <summary>Points every <paramref name="spacing"/> metres along a→b (ends inset by half a spacing).</summary>
        public static List<float2> Along(float2 a, float2 b, float spacing)
        {
            var r = new List<float2>();
            float len = math.distance(a, b);
            if (len < 0.01f) return r;
            int n = math.max(1, (int)math.floor(len / math.max(0.5f, spacing)));
            for (int i = 0; i < n; i++) r.Add(math.lerp(a, b, (i + 0.5f) / n));
            return r;
        }

        /// <summary>
        /// The chord of a line through <paramref name="p"/> in <paramref name="dir"/> that lies inside the
        /// region (longest inside piece containing p, else longest), inset by <paramref name="inset"/> at both ends.
        /// </summary>
        public static bool Chord(LayerRegion region, float2 p, float2 dir, float inset, out float2 a, out float2 b)
        {
            float reach = math.length(region.Max - region.Min) + 10f;
            var pieces = Poly.ClipSegmentToPolygon(region.Ring, p - dir * reach, p + dir * reach);
            a = b = p;
            float best = -1f;
            foreach (var (s0, s1) in pieces)
            {
                float len = math.distance(s0, s1);
                bool holds = SegmentIndex.DistSq(p, s0, s1) < 0.25f;
                float score = len + (holds ? 1e6f : 0f);
                if (score > best) { best = score; a = s0; b = s1; }
            }
            if (best < 0f) return false;
            var d = math.normalizesafe(b - a);
            if (math.distance(a, b) <= inset * 2f + 2f) return false;
            a += d * inset; b -= d * inset;
            return true;
        }

        /// <summary>Straight path piece emitted and indexed (paths are reserved for later layers by the composer).</summary>
        /// <summary>(s18) Whether a band of half width <paramref name="hw"/> from a to b runs over a taken disc.</summary>
        public static bool Crosses(Occupancy occ, float2 a, float2 b, float hw)
        {
            int n = math.max(2, (int)math.ceil(math.distance(a, b) / math.max(0.5f, hw)));
            for (int i = 0; i <= n; i++)
                if (occ.Discs.Overlaps(math.lerp(a, b, i / (float)n), hw, 0f)) return true;
            return false;
        }

        public static void Path(LayerContext ctx, Occupancy occ, float2 a, float2 b, NetRole role, AssetRef asset, int kind = 100)
        {
            if (math.distance(a, b) < 1f) return;
            ctx.StraightNet(a, b, role, asset);
            occ.TakeCorridor(a, b, Composer.DefaultHalfWidth(role), kind);
            occ.Paths.Add(a, b, Composer.DefaultHalfWidth(role), kind);
        }

        /// <summary>Scatter trees/bushes in free space (value-noise clumps). Returns the count placed.</summary>
        public static int Plant(LayerContext ctx, Occupancy occ, LayerRegion region, int wanted, ObjectRole role, string[] ids, float radius,
            float clumpScale, float floor, uint salt, Func<float2, bool> reject = null, int ageMin = 1, int ageMax = 3, string category = null)
        {
            if (wanted <= 0) return 0;
            int placed = 0;
            uint clump = Hashing.Mix(ctx.Seed, salt);
            foreach (var p in Scatter.Candidates(region, math.max(1.5f, radius * 1.4f), Hashing.Mix(ctx.Seed, salt + 1u), math.min(60000, wanted * 30 + 200), ctx.Cancel))
            {
                if (placed >= wanted) break;
                float chance = Scatter.Clustering(p, Layout.M(clumpScale, 4f), clump, floor);   // clump size is a layout distance
                if (Hashing.Unit(Hashing.Mix(clump, (int)(p.x * 13f), (int)(p.y * 7f))) > chance) continue;
                if (reject != null && reject(p)) continue;
                if (!occ.Free(p, radius, radius * 0.6f, 0.4f)) continue;
                uint v = Hashing.Mix(ctx.Seed, salt * 31u + (uint)placed);
                int age = role == ObjectRole.Tree ? ageMin + (int)(v % (uint)math.max(1, ageMax - ageMin + 1)) : -1;
                ctx.Object(p, Hashing.Unit(v) * 6.283f, role, AssetMix.Pick(ids, v), v, age, category);
                occ.Take(p, radius, role == ObjectRole.Tree ? 3 : 4);
                placed++;
            }
            return placed;
        }

        /// <summary>Irregular blob polygon (radius r with ±jitter), CCW.</summary>
        public static float2[] Blob(float2 c, float r, uint seed, int sides = 14, float jitter = 0.3f)
        {
            var pts = new float2[sides];
            float phase = Hashing.Unit(seed) * 6.283f;
            for (int i = 0; i < sides; i++)
            {
                float a = phase + i * 2f * math.PI / sides;
                float k = 1f - jitter + 2f * jitter * ValueNoise.Periodic(i * 3f / sides * 3f, 3 * 3, seed);
                pts[i] = c + new float2(math.cos(a), math.sin(a)) * r * k;
            }
            return pts;
        }

        /// <summary>Gate positions as plain points.</summary>
        public static List<float2> Points(List<Gate> gates)
        {
            var r = new List<float2>();
            foreach (var g in gates) r.Add(g.Position);
            return r;
        }

        /// <summary>A region shrunk to keep only what lies inside the outline and the given clip ring (largest piece), or null.</summary>
        public static LayerRegion Intersect(LayerRegion region, float2[] clip)
        {
            try
            {
                var pieces = Poly.Intersection(region.Ring, clip);
                if (pieces.Count == 0 || pieces[0].Outer == null) return null;
                return LayerRegion.From(pieces[0].Outer);
            }
            catch (Exception) { return null; }
        }
    }
}
