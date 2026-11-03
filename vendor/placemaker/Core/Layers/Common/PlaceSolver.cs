// Place solver: turns a described place ("a round plaza in the centre, 15 % of the park, 12–80 m")
// into a ring inside a parent region (DESIGN.md §9 — places are described, not drawn).
//
// Pure and deterministic. The place keeps `Margin` metres from the parent outline and never
// overlaps the reserved polygons. When the wanted size does not fit it shrinks in steps down to
// MinM; below that it fails with a reason instead of returning something odd.
//
// Anchors:
//   centre    nearest the area centroid (or its deepest point when the centroid is outside)
//   corner    tucked into the sharpest convex corner (ties: nearest `Toward`, else the seed)
//   edge      against the middle of the longest straight run of the outline, aligned to it
//   entrance  next to the first entrance point (falls back to edge without entrances)

using System;
using System.Collections.Generic;
using Placemaker.Core.Geometry;
using Unity.Mathematics;

namespace Placemaker.Core.Layers.Common
{
    public enum PlaceShape { Circle = 0, Square = 1, Rectangle = 2 }
    public enum PlaceAnchor { Centre = 0, Corner = 1, Edge = 2, Entrance = 3 }
    public enum PlaceFailure { None = 0, InvalidParent = 1, NoRoom = 2 }

    public struct PlaceRule
    {
        public PlaceShape Shape;
        public PlaceAnchor Anchor;
        /// <summary>Share of the parent area the place should cover, in percent (1..90).</summary>
        public float SizePct;
        /// <summary>Smallest / largest characteristic size in metres (diameter, side, long side).</summary>
        public float MinM, MaxM;
        /// <summary>Rectangle long:short ratio (≥ 1). Ignored for circle and square.</summary>
        public float Aspect;
        /// <summary>Clear distance kept from the parent outline (room for a path or hedge).</summary>
        public float Margin;

        public static PlaceRule Default => new PlaceRule
        {
            Shape = PlaceShape.Circle, Anchor = PlaceAnchor.Centre, SizePct = 15f, MinM = 12f, MaxM = 80f, Aspect = 1.6f, Margin = 3f,
        };

        public static PlaceShape ParseShape(string s) =>
            s == "square" ? PlaceShape.Square : s == "rectangle" ? PlaceShape.Rectangle : PlaceShape.Circle;

        public static PlaceAnchor ParseAnchor(string s) =>
            s == "corner" ? PlaceAnchor.Corner : s == "edge" ? PlaceAnchor.Edge : s == "entrance" ? PlaceAnchor.Entrance : PlaceAnchor.Centre;
    }

    public sealed class PlaceResult
    {
        public bool Ok;
        public PlaceFailure Failure;
        /// <summary>CCW ring of the place (empty on failure).</summary>
        public float2[] Ring = Array.Empty<float2>();
        public float2 Center;
        /// <summary>Unit direction of the place's long axis.</summary>
        public float2 Axis = new float2(1, 0);
        /// <summary>Characteristic size actually used (diameter / side / long side), metres.</summary>
        public float Size;
        /// <summary>True when the place had to be made smaller than asked to fit.</summary>
        public bool Shrunk;
        /// <summary>Localisation key describing the failure ("" when Ok).</summary>
        public string ReasonKey = "";
    }

    public static class PlaceSolver
    {
        public const string ReasonInvalid = "Placemaker.Place.Issue.InvalidParent";
        public const string ReasonNoRoom = "Placemaker.Place.Issue.NoRoom";
        private const int MaxCandidates = 6000;
        private const int CircleSides = 32;

        /// <param name="parent">Parent outline (any winding).</param>
        /// <param name="reserved">Polygons the place must not overlap (may be null).</param>
        /// <param name="toward">Entrance points: target of the entrance anchor and tie-break for corners (may be null).</param>
        public static PlaceResult Solve(IReadOnlyList<float2> parent, IReadOnlyList<float2[]> reserved, PlaceRule rule,
            IReadOnlyList<float2> toward = null, uint seed = 1)
        {
            var region = LayerRegion.From(parent);
            if (region == null) return Fail(PlaceFailure.InvalidParent);
            return Solve(region, reserved, rule, toward, seed);
        }

        public static PlaceResult Solve(LayerRegion region, IReadOnlyList<float2[]> reserved, PlaceRule rule,
            IReadOnlyList<float2> toward = null, uint seed = 1)
        {
            if (region == null || region.Area < 1f) return Fail(PlaceFailure.InvalidParent);
            rule = Sanitize(rule);
            var blockers = new List<float2[]>();
            if (reserved != null)
                foreach (var r in reserved)
                    if (r != null && r.Length >= 3) blockers.Add(r);

            // Wanted dimensions from the area share, clamped to min/max.
            float wantedArea = region.Area * rule.SizePct / 100f;
            float size = BaseSize(rule, wantedArea);

            var targets = Targets(region, rule, toward, seed);
            bool shrunk = false;
            for (int attempt = 0; attempt < 24; attempt++)
            {
                if (size < rule.MinM - 1e-3f) break;
                foreach (var t in targets)
                {
                    if (TryAt(region, blockers, rule, size, t, out var result))
                    {
                        result.Shrunk = shrunk;
                        return result;
                    }
                }
                // Shrink and retry; the last try is exactly the minimum.
                float next = size * 0.88f;
                if (size > rule.MinM && next < rule.MinM) next = rule.MinM;
                size = next;
                shrunk = true;
            }
            return Fail(PlaceFailure.NoRoom);
        }

        private static PlaceRule Sanitize(PlaceRule r)
        {
            r.SizePct = math.clamp(float.IsNaN(r.SizePct) ? 15f : r.SizePct, 0.5f, 90f);
            r.MinM = math.max(2f, float.IsNaN(r.MinM) ? 12f : r.MinM);
            r.MaxM = math.max(r.MinM, float.IsNaN(r.MaxM) ? 80f : r.MaxM);
            r.Aspect = math.clamp(float.IsNaN(r.Aspect) || r.Aspect <= 0f ? 1.6f : r.Aspect, 1f, 4f);
            if (r.Aspect < 1f) r.Aspect = 1f / r.Aspect;
            r.Margin = math.clamp(float.IsNaN(r.Margin) ? 3f : r.Margin, 0.5f, 30f);
            return r;
        }

        /// <summary>Characteristic size for an area: circle diameter, square side, rectangle long side.</summary>
        public static float BaseSize(PlaceRule rule, float area)
        {
            float s;
            switch (rule.Shape)
            {
                case PlaceShape.Circle: s = 2f * math.sqrt(area / math.PI); break;
                case PlaceShape.Rectangle: s = math.sqrt(area * rule.Aspect); break;
                default: s = math.sqrt(area); break;
            }
            return math.clamp(s, rule.MinM, rule.MaxM);
        }

        /// <summary>Ring of the shape centred at c with the given long axis.</summary>
        public static float2[] Shape(PlaceRule rule, float size, float2 c, float2 axis)
        {
            switch (rule.Shape)
            {
                case PlaceShape.Circle:
                    return LayerRegion.Circle(c, size * 0.5f, CircleSides, math.atan2(axis.y, axis.x));
                case PlaceShape.Rectangle:
                {
                    float shortSide = math.max(rule.MinM * 0.5f, size / rule.Aspect);
                    return LayerRegion.Rect(c, axis, size * 0.5f, math.min(size, shortSide) * 0.5f);
                }
                default:
                    return LayerRegion.Rect(c, axis, size * 0.5f, size * 0.5f);
            }
        }

        private struct Target { public float2 Point; public float2 Axis; public float Reach; }

        /// <summary>Anchor points to try, best first, each with the axis the place aligns to.</summary>
        private static List<Target> Targets(LayerRegion region, PlaceRule rule, IReadOnlyList<float2> toward, uint seed)
        {
            region.PrincipalAxes(out var centroid, out var major, out _, out _);
            var list = new List<Target>();
            float far = math.cmax(region.Max - region.Min) * 2f;
            var anchor = rule.Anchor;
            if (anchor == PlaceAnchor.Entrance && (toward == null || toward.Count == 0)) anchor = PlaceAnchor.Edge;
            switch (anchor)
            {
                case PlaceAnchor.Centre:
                {
                    var c = region.Contains(centroid) ? centroid : region.DeepestPoint(out _, null, 16);
                    list.Add(new Target { Point = c, Axis = major, Reach = far });
                    break;
                }
                case PlaceAnchor.Corner:
                {
                    var corners = Corners(region, toward, seed);
                    foreach (var (p, axis) in corners) list.Add(new Target { Point = p, Axis = axis, Reach = -1f });
                    // Last resort: anywhere, nearest the best corner.
                    if (corners.Count > 0) list.Add(new Target { Point = corners[0].p, Axis = corners[0].axis, Reach = far });
                    else list.Add(new Target { Point = centroid, Axis = major, Reach = far });
                    break;
                }
                case PlaceAnchor.Edge:
                {
                    var (mid, dir) = LongestRun(region);
                    list.Add(new Target { Point = mid, Axis = dir, Reach = far });
                    break;
                }
                case PlaceAnchor.Entrance:
                {
                    foreach (var e in toward)
                    {
                        var p = region.ClosestOnBoundary(e, out int seg, out _);
                        var a = region.Ring[seg]; var b = region.Ring[(seg + 1) % region.Ring.Length];
                        list.Add(new Target { Point = p, Axis = math.normalizesafe(b - a, major), Reach = -1f });
                    }
                    var p0 = region.ClosestOnBoundary(toward[0], out int s0, out _);
                    var a0 = region.Ring[s0]; var b0 = region.Ring[(s0 + 1) % region.Ring.Length];
                    list.Add(new Target { Point = p0, Axis = math.normalizesafe(b0 - a0, major), Reach = far });
                    break;
                }
            }
            return list;
        }

        /// <summary>Convex corners sharper than 160°, sharpest first; ties nearest `toward`, then by seed.</summary>
        private static List<(float2 p, float2 axis)> Corners(LayerRegion region, IReadOnlyList<float2> toward, uint seed)
        {
            var ring = region.Ring;
            int n = ring.Length;
            bool ccw = Poly.SignedArea(ring) > 0f;
            var scored = new List<(float score, float tie, int i, float2 axis)>();
            for (int i = 0; i < n; i++)
            {
                var prev = ring[(i + n - 1) % n]; var p = ring[i]; var next = ring[(i + 1) % n];
                var d0 = p - prev; var d1 = next - p;
                float l0 = math.length(d0), l1 = math.length(d1);
                if (l0 < 1e-3f || l1 < 1e-3f) continue;
                float cross = d0.x * d1.y - d0.y * d1.x;
                bool convex = ccw ? cross > 0f : cross < 0f;
                if (!convex) continue;
                float turn = math.degrees(math.acos(math.clamp(math.dot(d0 / l0, d1 / l1), -1f, 1f)));
                float interior = 180f - turn;
                if (interior > 160f) continue;
                // Corners between two long sides read as corners; a short chamfer edge does not.
                float sides = math.min(l0, l1);
                float score = interior - math.min(sides, 40f) * 0.25f;
                float tie;
                if (toward != null && toward.Count > 0)
                {
                    tie = float.MaxValue;
                    foreach (var t in toward) tie = math.min(tie, math.distance(t, p));
                }
                else tie = Hashing.Unit(Hashing.Mix(seed, (uint)i + 1u));
                var axis = l0 >= l1 ? d0 / l0 : d1 / l1;
                scored.Add((math.round(score / 5f) * 5f, tie, i, axis));
            }
            scored.Sort((a, b) => a.score != b.score ? a.score.CompareTo(b.score) : a.tie != b.tie ? a.tie.CompareTo(b.tie) : a.i.CompareTo(b.i));
            var r = new List<(float2, float2)>();
            for (int k = 0; k < scored.Count && k < 6; k++) r.Add((ring[scored[k].i], scored[k].axis));
            return r;
        }

        /// <summary>Midpoint and direction of the longest nearly straight run of outline edges.</summary>
        private static (float2 mid, float2 dir) LongestRun(LayerRegion region)
        {
            var ring = region.Ring;
            int n = ring.Length;
            float bestLen = -1f; float2 bestMid = region.Centroid, bestDir = new float2(1, 0);
            for (int start = 0; start < n; start++)
            {
                var a = ring[start];
                var dir = math.normalizesafe(ring[(start + 1) % n] - a, new float2(1, 0));
                // Only start a run where the previous edge turns away (avoids counting each run n times).
                var prevDir = math.normalizesafe(a - ring[(start + n - 1) % n], dir);
                if (math.dot(prevDir, dir) > 0.966f && n > 3) continue;
                float len = 0; int k = start; float2 end = a;
                for (int steps = 0; steps < n; steps++)
                {
                    var p = ring[k]; var q = ring[(k + 1) % n];
                    var d = math.normalizesafe(q - p, dir);
                    if (math.dot(d, dir) < 0.966f) break;   // > 15° turn ends the run
                    len += math.distance(p, q); end = q; k = (k + 1) % n;
                    if (k == start) break;
                }
                if (len > bestLen) { bestLen = len; bestMid = (a + end) * 0.5f; bestDir = math.normalizesafe(end - a, dir); }
            }
            return (bestMid, bestDir);
        }

        private static bool TryAt(LayerRegion region, List<float2[]> blockers, PlaceRule rule, float size, Target target, out PlaceResult result)
        {
            result = null;
            var axis = math.normalizesafe(target.Axis, new float2(1, 0));
            // Inscribed radius of the shape (quick necessary test) and sample spacing on its outline.
            float inner = rule.Shape == PlaceShape.Rectangle ? math.max(rule.MinM * 0.5f, size / rule.Aspect) * 0.5f : size * 0.5f;
            inner = math.min(inner, size * 0.5f);
            float need = inner + rule.Margin;
            var span = region.Max - region.Min;
            float step = math.max(math.max(size / 10f, 0.75f), math.sqrt(span.x * span.y / MaxCandidates));
            float reach = target.Reach > 0f ? target.Reach : size * 1.25f + rule.Margin + 6f;
            float2 lo = math.max(region.Min, target.Point - reach), hi = math.min(region.Max, target.Point + reach);
            var candidates = new List<(float d, float2 p)>();
            for (float y = lo.y + step * 0.5f; y <= hi.y; y += step)
                for (float x = lo.x + step * 0.5f; x <= hi.x; x += step)
                {
                    var p = new float2(x, y);
                    float d = math.distancesq(p, target.Point);
                    if (d > reach * reach) continue;
                    candidates.Add((d, p));
                }
            // Also the exact target (centred places land exactly on the centroid).
            candidates.Add((0f, target.Point));
            candidates.Sort((a, b) => a.d != b.d ? a.d.CompareTo(b.d) : a.p.x != b.p.x ? a.p.x.CompareTo(b.p.x) : a.p.y.CompareTo(b.p.y));
            int checks = 0;
            foreach (var (_, c) in candidates)
            {
                if (!region.ContainsDisc(c, need)) continue;
                var ring = Shape(rule, size, c, axis);
                if (++checks > 4000) break;
                if (!Fits(region, blockers, ring, rule.Margin)) continue;
                result = new PlaceResult
                {
                    Ok = true, Ring = Poly.EnsureCcw(ring), Center = c, Axis = axis, Size = size,
                };
                return true;
            }
            return false;
        }

        /// <summary>The ring stays Margin inside the parent and clear of every blocker.</summary>
        public static bool Fits(LayerRegion region, IReadOnlyList<float2[]> blockers, float2[] ring, float margin)
        {
            float spacing = math.clamp(margin * 1.5f, 0.5f, 3f);
            Poly.Bounds(ring, out var mn, out var mx);
            for (int i = 0; i < ring.Length; i++)
            {
                var a = ring[i]; var b = ring[(i + 1) % ring.Length];
                int k = math.max(1, (int)math.ceil(math.distance(a, b) / spacing));
                for (int j = 0; j < k; j++)
                {
                    var p = math.lerp(a, b, j / (float)k);
                    if (!region.ContainsDisc(p, margin)) return false;
                    if (blockers != null)
                        foreach (var bl in blockers)
                            if (Poly.Contains(bl, p)) return false;
                }
            }
            // Notches of the parent (or blockers) poking into the place between samples.
            foreach (var v in region.Ring)
                if (v.x >= mn.x && v.y >= mn.y && v.x <= mx.x && v.y <= mx.y && Poly.Contains(ring, v)) return false;
            if (blockers != null)
                foreach (var bl in blockers)
                    foreach (var v in bl)
                        if (v.x >= mn.x && v.y >= mn.y && v.x <= mx.x && v.y <= mx.y && Poly.Contains(ring, v)) return false;
            return true;
        }

        private static PlaceResult Fail(PlaceFailure f) => new PlaceResult
        {
            Ok = false, Failure = f, ReasonKey = f == PlaceFailure.InvalidParent ? ReasonInvalid : ReasonNoRoom,
        };
    }
}
