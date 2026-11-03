// Shared building blocks of the "events" types (fairground, amphitheatre): tight rectangle
// claims, arcs of path, annular sectors, street trimming against what is already taken, category
// fallback chains that stay quiet for optional extras, and object placement that keeps the
// catalog category for the game's automatic asset choice.

using System;
using System.Collections.Generic;
using Placemaker.Core.Geometry;
using Placemaker.Core.Layers.Common;
using Unity.Mathematics;

namespace Placemaker.Core.Layers.Types.Events
{
    public static class EventKit
    {
        /// <summary>
        /// Claims an oriented rectangle almost exactly: parallel corridors along the long axis, ends inset
        /// by the corridor half width so the capsules never poke out past the short sides.
        /// </summary>
        public static void TakeRectTight(Occupancy occ, OrientedRect r, int kind = 2)
        {
            float h = math.clamp(math.min(r.HalfWidth, r.HalfLength), 0.5f, 1.5f);
            int lanes = math.max(1, (int)math.ceil(r.HalfWidth / h));
            float u = math.max(0f, r.HalfLength - h);
            for (int i = 0; i < lanes; i++)
            {
                float v = lanes == 1 ? 0f : -r.HalfWidth + h + (2f * r.HalfWidth - 2f * h) * i / (lanes - 1);
                occ.TakeCorridor(r.Local(-u, v), r.Local(u, v), h, kind);
            }
        }

        /// <summary>True when p lies inside the rectangle grown by <paramref name="margin"/>.</summary>
        public static bool InRect(OrientedRect r, float2 p, float margin = 0f)
        {
            var d = p - r.Center;
            return math.abs(math.dot(d, r.Axis)) <= r.HalfLength + margin && math.abs(math.dot(d, r.Side)) <= r.HalfWidth + margin;
        }

        /// <summary>First category of the chain with installed assets; null (no issue) when none — for optional extras.</summary>
        public static string Optional(string chosen, params string[] chain)
        {
            if (!string.IsNullOrEmpty(chosen)) return chain[0];
            foreach (var c in chain) if (AssetAvailability.Has(c)) return c;
            return null;
        }

        /// <summary>Object role matching a category (benches / lamps / trees keep their own roles).</summary>
        public static ObjectRole RoleOf(string category)
        {
            switch (category)
            {
                case AssetCategories.Bench: return ObjectRole.Bench;
                case AssetCategories.Lamp: return ObjectRole.Lamp;
                case AssetCategories.Tree: return ObjectRole.Tree;
                case AssetCategories.Bush: return ObjectRole.Bush;
                case AssetCategories.Bin: return ObjectRole.Bin;
                case AssetCategories.Centerpiece:
                case AssetCategories.CenterpieceFountain:
                case AssetCategories.CenterpieceStatue: return ObjectRole.Centerpiece;
                default: return ObjectRole.Furniture;
            }
        }

        /// <summary>Category to store on the object: null for roles whose default category is that category.</summary>
        public static string Stored(string category) =>
            category == AssetCategories.Bench || category == AssetCategories.Lamp || category == AssetCategories.Tree ||
            category == AssetCategories.Bush || category == AssetCategories.Bin ? null : category;

        /// <summary>Checked placement of a categorised item (role derived from the category).</summary>
        public static bool Put(LayerContext ctx, Occupancy occ, float2 p, float yaw, string category, AssetRef asset, float radius, uint variation,
            float margin = 0.5f, float gap = 0.2f, int kind = 1)
        {
            if (category == null) return false;
            var role = RoleOf(category);
            return LeisureKit.Put(ctx, occ, p, yaw, role, asset, Stored(category), radius, variation, margin, gap, kind, role == ObjectRole.Tree ? 2 : -1);
        }

        /// <summary>Unchecked placement inside space the caller already claimed (a food court, a backstage yard).</summary>
        public static PlannedObject Place(LayerContext ctx, float2 p, float yaw, string category, AssetRef asset, uint variation)
        {
            var role = RoleOf(category);
            return ctx.Object(p, yaw, role, asset, variation, role == ObjectRole.Tree ? 2 : -1, Stored(category));
        }

        public static float2 Dir(float angle) => new float2(math.cos(angle), math.sin(angle));

        /// <summary>Angle of a direction (radians, atan2(y, x)).</summary>
        public static float Angle(float2 d) => math.atan2(d.y, d.x);

        /// <summary>Wraps an angle difference to (−π, π].</summary>
        public static float Wrap(float a)
        {
            while (a > math.PI) a -= 2f * math.PI;
            while (a <= -math.PI) a += 2f * math.PI;
            return a;
        }

        /// <summary>CCW ring of an annular sector (angles a0 &lt; a1, radians), arcs sampled every ≤ <paramref name="step"/> m.</summary>
        public static float2[] Sector(float2 c, float r0, float r1, float a0, float a1, float step = 2.5f)
        {
            var pts = new List<float2>();
            int n1 = math.max(2, (int)math.ceil(r1 * (a1 - a0) / step));
            for (int i = 0; i <= n1; i++) pts.Add(c + Dir(a0 + (a1 - a0) * i / n1) * r1);
            if (r0 < 0.3f) pts.Add(c);
            else
            {
                int n0 = math.max(2, (int)math.ceil(r0 * (a1 - a0) / step));
                for (int i = n0; i >= 0; i--) pts.Add(c + Dir(a0 + (a1 - a0) * i / n0) * r0);
            }
            return Poly.EnsureCcw(pts.ToArray());
        }

        /// <summary>
        /// Circular arc of path as cubic Béziers (≤ 45° each), emitted, indexed as corridors and walkable
        /// paths. Returns the chord pieces (for gate connections).
        /// </summary>
        public static List<(float2 a, float2 b)> Arc(LayerContext ctx, Occupancy occ, float2 c, float r, float a0, float a1, NetRole role, AssetRef asset)
        {
            var chords = new List<(float2, float2)>();
            float sweep = a1 - a0;
            if (r < 1f || math.abs(sweep) < 1e-3f) return chords;
            int pieces = math.max(1, (int)math.ceil(math.abs(sweep) / (math.PI / 4f)));
            float hw = Composer.DefaultHalfWidth(role);
            for (int i = 0; i < pieces; i++)
            {
                float s0 = a0 + sweep * i / pieces, s1 = a0 + sweep * (i + 1) / pieces;
                float k = 4f / 3f * math.tan((s1 - s0) / 4f) * r;
                var p0 = c + Dir(s0) * r; var p3 = c + Dir(s1) * r;
                var t0 = new float2(-math.sin(s0), math.cos(s0)); var t1 = new float2(-math.sin(s1), math.cos(s1));
                ctx.CurvedNet(p0, p0 + t0 * k, p3 - t1 * k, p3, role, asset);
                float2 prev = p0;
                for (int j = 1; j <= 6; j++)
                {
                    var q = c + Dir(s0 + (s1 - s0) * j / 6f) * r;
                    occ.TakeCorridor(prev, q, hw, 100);
                    occ.Paths.Add(prev, q, hw, 100);
                    chords.Add((prev, q));
                    prev = q;
                }
            }
            return chords;
        }

        /// <summary>
        /// The longest piece of a→b around <paramref name="keep"/> (a point on it) whose samples are not
        /// blocked, or false when keep itself is blocked or the piece is shorter than <paramref name="minLength"/>.
        /// </summary>
        public static bool Trim(float2 a, float2 b, float2 keep, Func<float2, bool> blocked, float minLength, out float2 ta, out float2 tb)
        {
            ta = a; tb = b;
            float len = math.distance(a, b);
            if (len < 0.5f) return false;
            var d = (b - a) / len;
            float k = math.clamp(math.dot(keep - a, d), 0f, len);
            if (blocked(a + d * k)) return false;
            const float step = 1f;
            float lo = k, hi = k;
            while (lo - step >= 0f && !blocked(a + d * (lo - step))) lo -= step;
            if (lo < step && !blocked(a)) lo = 0f;
            while (hi + step <= len && !blocked(a + d * (hi + step))) hi += step;
            if (len - hi < step && !blocked(b)) hi = len;
            if (hi - lo < minLength) return false;
            ta = a + d * lo; tb = a + d * hi;
            return true;
        }

        /// <summary>Rectangle ring clipped to the region (largest piece), or null.</summary>
        public static float2[] Clip(LayerRegion region, float2[] ring)
        {
            bool inside = true;
            foreach (var q in ring) if (!region.Contains(q)) { inside = false; break; }
            if (inside) return ring;
            var r = LeisureKit.Intersect(region, ring);
            return r?.Ring;
        }

        /// <summary>Worn-ground paint over a ring (clipped to the region).</summary>
        public static void Wear(LayerContext ctx, LayerRegion region, float2[] ring, int layer, float strength)
        {
            var clipped = Clip(region, ring);
            if (clipped != null && Poly.Area(clipped) > 4f) TerrainPaint.Patch(ctx, clipped, layer, strength, 2f);
        }

        /// <summary>Unit normal of the nearest walkable path at p (pointing from the path to p), or false.</summary>
        public static bool PathFrame(Occupancy occ, float2 p, float cap, out float2 toward, out float2 tangent)
        {
            toward = default; tangent = new float2(1, 0);
            int i = occ.Paths.Nearest(p, cap, out var closest);
            if (i < 0) return false;
            var s = occ.Paths.Segments[i];
            tangent = math.normalizesafe(s.B - s.A, new float2(1, 0));
            toward = closest;
            return true;
        }

        /// <summary>
        /// A block of identical cabins (portable toilets…) in a row beside the nearest path, facing it.
        /// Returns the number placed.
        /// </summary>
        public static int CabinRow(LayerContext ctx, Occupancy occ, string category, AssetRef asset, int count, float pitch, uint seed,
            IReadOnlyList<float2> avoid, float avoidDistance, Spots.Preference pref = Spots.Preference.NearPaths)
        {
            if (category == null || count <= 0) return 0;
            float half = count * pitch * 0.5f + 0.6f;
            if (!Spots.Find(occ, half, pref, seed, out var c, avoid, avoidDistance, 1f, 1500)) return 0;
            float2 tangent = new float2(1, 0), face = c + new float2(0, 1);
            if (PathFrame(occ, c, 80f, out var toward, out var tan)) { tangent = tan; face = toward; }
            var normal = math.normalizesafe(face - c, new float2(0, 1));
            float yaw = math.atan2(normal.x, normal.y);
            int n = 0;
            for (int i = 0; i < count; i++)
            {
                var p = c + tangent * ((i - (count - 1) * 0.5f) * pitch);
                if (!occ.Free(p, pitch * 0.45f, 0.5f, 0.05f)) continue;
                Place(ctx, p, yaw, category, asset, Hashing.Mix(seed, (uint)i));
                occ.Take(p, pitch * 0.45f, 1);
                n++;
            }
            return n;
        }

        /// <summary>Gate connections to the nearest spine point that never run through anything already claimed (sampled before the path end).</summary>
        public static void ConnectGates(LayerContext ctx, Occupancy occ, LayerRegion region, List<Gate> gates, List<(float2 a, float2 b)> spine, AssetRef asset)
        {
            float approach = math.max(ctx.M(8f, 5f), ctx.EdgeMargin + 4f);
            foreach (var g in gates)
            {
                // s9: straight in from the entrance (GateApproach), clear of the stalls; never sideways along the edge.
                bool Clear(float2 p, float2 q)
                {
                    float len = math.distance(p, q);
                    if (len < 1e-3f) return true;
                    var step = (q - p) / len;
                    for (float t = 1.5f; t < len - 3f; t += 1.5f)
                    {
                        var s = p + step * t;
                        if (occ.Discs.Overlaps(s, 1.6f) || occ.Corridors.Overlaps(s, 1.6f)) return false;
                    }
                    return true;
                }
                var route = GateApproach.Route(region, g.Position, spine, approach, Clear);
                if (route != null)
                {
                    for (int i = 1; i < route.Count; i++) LeisureKit.Path(ctx, occ, route[i - 1], route[i], NetRole.Pathway, asset);
                    continue;
                }
                var inward = GateApproach.Inward(region, g.Position);
                var feet = new List<(float d, float2 q)>();
                foreach (var (a, b) in spine)
                {
                    var q = SegmentIndex.ClosestPoint(g.Position, a, b);
                    feet.Add((math.distance(q, g.Position), q));
                }
                feet.Sort((x, y) => x.d.CompareTo(y.d));
                foreach (var (d, q) in feet)
                {
                    if (d <= 1f) break;
                    if (region.CrossesBoundary(g.Position, q, true)) continue;
                    bool clear = true;
                    var dir = (q - g.Position) / d;
                    if (math.dot(dir, inward) < 0.5f) continue;   // never a leg along the outline
                    for (float t = 1.5f; t < d - 3f; t += 1.5f)
                    {
                        var p = g.Position + dir * t;
                        if (occ.Discs.Overlaps(p, 1.6f) || occ.Corridors.Overlaps(p, 1.6f)) { clear = false; break; }
                    }
                    if (!clear) continue;
                    LeisureKit.Path(ctx, occ, g.Position, q, NetRole.Pathway, asset);
                    break;
                }
            }
        }
        /// <summary>Sum of placed objects with the given category (or role when category is null).</summary>
        public static int Count(AreaPlan plan, string category)
        {
            int n = 0;
            foreach (var o in plan.Objects) if (o.Category == category) n++;
            return n;
        }
    }
}
