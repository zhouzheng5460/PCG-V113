// Park & plaza amenities (type wave, builder 11): volleyball sand pit, basketball half-court, table
// tennis, outdoor chess, fitness stations, splash pad, bandstand / gazebo, dog run, noticeboard.
//
// How they compose: a host (park, plaza) offers each amenity as a feature with two host options,
// "<id>At" (centre / edge / near entrance / corner) and — in parks — "<id>Path" ("Path to it").
// When the feature is on, the host reserves an oriented rectangle of the amenity's footprint at
// the chosen position (Amenities.TrySolve), keeps planting and furniture out of it, joins it to the
// nearest path with a short spur, and hands it to the amenity layer as the place region
// "place:<id>". The amenity then lays itself out inside that rectangle (AmenityAddOn.Build).
// Without a host place (a recipe, a campground lawn) the amenity finds its own spot with the same
// solver at its default position.

using System;
using System.Collections.Generic;
using Placemaker.Core.Geometry;
using Placemaker.Core.Layers.Common;
using Unity.Mathematics;

namespace Placemaker.Core
{
    /// <summary>Amenity asset categories (round 5). Subcategories are also classified as their parent where one exists.</summary>
    public static partial class AssetCategories
    {
        public const string SportsVolleyball = "sports.volleyball";     // volleyball nets / posts
        public const string SportsTableTennis = "sports.tableTennis";   // outdoor ping-pong tables
        public const string GamesChess = "games.chess";                 // chess / board-game tables (also cafe seating)
        public const string StageBandstand = "stage.bandstand";         // bandstands (also stage, shelter)
        public const string SignNoticeboard = "sign.noticeboard";       // community noticeboards, park info / map boards
    }
}

namespace Placemaker.Core.Layers.AddOns
{
    /// <summary>Footprint and default position of one amenity.</summary>
    public sealed class AmenitySpec
    {
        public string Layer;
        /// <summary>Place size (long side × short side), metres — the amenity plus its own margin.</summary>
        public float Length, Width;
        /// <summary>Default position: "centre", "edge", "entrance" or "corner".</summary>
        public string DefaultAt;
        public bool PathByDefault = true;
        /// <summary>Smaller places (long × short) tried in order when this size fits nowhere (sports fields: "scaled to fit").</summary>
        public float2[] Smaller = Array.Empty<float2>();
    }

    public static class Amenities
    {
        public static readonly string[] Positions = { "centre", "edge", "entrance", "corner" };

        /// <summary>Sports fields first (AddOns/Fields: the big places get the first pick of the ground), then the amenities.</summary>
        public static readonly AmenitySpec[] All = Join(Fields.FieldSpecs.All, new[]
        {
            new AmenitySpec { Layer = "volleyball", Length = 22f, Width = 14f, DefaultAt = "edge" },
            new AmenitySpec { Layer = "basketball", Length = 19f, Width = 17f, DefaultAt = "corner" },
            new AmenitySpec { Layer = "tableTennis", Length = 13f, Width = 9f, DefaultAt = "centre" },
            new AmenitySpec { Layer = "chess", Length = 11f, Width = 10f, DefaultAt = "centre" },
            new AmenitySpec { Layer = "fitness", Length = 16f, Width = 11f, DefaultAt = "edge" },
            new AmenitySpec { Layer = "splashPad", Length = 18f, Width = 18f, DefaultAt = "centre" },
            new AmenitySpec { Layer = "bandstand", Length = 26f, Width = 22f, DefaultAt = "centre" },
            new AmenitySpec { Layer = "dogRun", Length = 34f, Width = 24f, DefaultAt = "corner" },
            new AmenitySpec { Layer = "noticeboard", Length = 6f, Width = 4f, DefaultAt = "entrance" },
            // Monuments & follies (grand park wave, g4): see AddOns/Monuments.
            new AmenitySpec { Layer = "bigStatue", Length = 36f, Width = 26f, DefaultAt = "centre" },
            new AmenitySpec { Layer = "folly", Length = 72f, Width = 54f, DefaultAt = "edge" },
            new AmenitySpec { Layer = "obelisk", Length = 24f, Width = 24f, DefaultAt = "entrance" },
            new AmenitySpec { Layer = "triumphalArch", Length = 30f, Width = 16f, DefaultAt = "entrance" },
            new AmenitySpec { Layer = "sculptureWalk", Length = 64f, Width = 18f, DefaultAt = "edge" },
            new AmenitySpec { Layer = "terraceGarden", Length = 60f, Width = 50f, DefaultAt = "centre" },
        });

        private static AmenitySpec[] Join(AmenitySpec[] a, AmenitySpec[] b)
        {
            var r = new AmenitySpec[a.Length + b.Length];
            a.CopyTo(r, 0);
            b.CopyTo(r, a.Length);
            return r;
        }

        public static AmenitySpec Spec(string layer)
        {
            foreach (var s in All) if (s.Layer == layer) return s;
            return null;
        }

        public static string Place(string layer) => Composer.PlacePrefix + layer;
        public static string AtControl(string layer) => layer + "At";
        public static string PathControl(string layer) => layer + "Path";

        /// <summary>Region tags the hosts emit: one place per amenity.</summary>
        public static string[] PlaceTags(string[] only = null)
        {
            var r = new List<string>();
            foreach (var s in All) if (only == null || Array.IndexOf(only, s.Layer) >= 0) r.Add(Place(s.Layer));
            return r.ToArray();
        }

        /// <summary>Host controls for every amenity: position (+ "Path to it" when <paramref name="paths"/>), each in the amenity's group.</summary>
        public static void DefineHostControls(ControlSet c, bool paths, string[] only = null)
        {
            foreach (var s in All)
            {
                if (only != null && Array.IndexOf(only, s.Layer) < 0) continue;
                c.Choice(AtControl(s.Layer), s.DefaultAt, ControlTier.More, s.Layer, Positions).Tip();
                c.Fixed(AtControl(s.Layer));
                if (!paths) continue;
                c.Toggle(PathControl(s.Layer), s.PathByDefault, ControlTier.More, s.Layer).Tip();
                c.Fixed(PathControl(s.Layer));
            }
        }

        /// <summary>FeatureInfo for an amenity on a host (target = its place, options = the host's position / path controls).</summary>
        public static FeatureInfo Feature(string host, string layer, bool paths) => new FeatureInfo
        {
            Layer = layer, Target = Place(layer), LabelKey = "Placemaker.Feature." + layer, TooltipKey = "Placemaker.Feature." + layer + ".Tooltip",
            Options = paths ? new[] { AtControl(layer), PathControl(layer) } : new[] { AtControl(layer) },
        };

        /// <summary>
        /// The rectangle of an amenity at the wanted position: the free spot nearest the anchor that fits
        /// the whole footprint (plus margin) inside the region, clear of everything in the occupancy and of
        /// <paramref name="blocked"/> (lake, plaza…). Other anchors are tried when the wanted one has no room.
        /// </summary>
        public static bool TrySolve(LayerRegion region, Occupancy occ, float length, float width, string at, IReadOnlyList<float2> gates,
            uint seed, Func<float2, bool> blocked, out OrientedRect rect, out bool moved, float margin = 1f, bool shrink = true)
        {
            rect = default; moved = false;
            if (region == null) return false;
            // Sports fields (AmenitySpec.Smaller): when the full size fits nowhere, the next smaller size is tried
            // ("scaled to fit"); a cheap free-room probe skips sizes that cannot fit at all.
            var ladder = shrink ? WithSmaller(length, width) : null;
            if (ladder == null && length < 40f) return SolveOne(region, occ, length, width, at, gates, seed, blocked, out rect, out moved, margin);
            var grid = new Fields.FieldKit.ClearanceGrid(region, occ, width * 0.5f + 4f);
            float room = grid.Max;
            if (ladder == null) return Fields.FieldKit.MayFit(room, length, width) && SolveOne(region, occ, length, width, at, gates, seed, blocked, out rect, out moved, margin, grid);
            if (Fields.FieldKit.MayFit(room, length, width) && SolveOne(region, occ, length, width, at, gates, seed, blocked, out rect, out moved, margin, grid)) return true;
            foreach (var s in ladder.Smaller)
                if (Fields.FieldKit.MayFit(room, s.x, s.y) && SolveOne(region, occ, s.x, s.y, at, gates, seed, blocked, out rect, out moved, margin, grid)) return true;
            return false;
        }

        private static AmenitySpec WithSmaller(float length, float width)
        {
            foreach (var s in All)
                if (s.Smaller != null && s.Smaller.Length > 0 && math.abs(s.Length - length) < 0.01f && math.abs(s.Width - width) < 0.01f) return s;
            return null;
        }

        private static bool SolveOne(LayerRegion region, Occupancy occ, float length, float width, string at, IReadOnlyList<float2> gates,
            uint seed, Func<float2, bool> blocked, out OrientedRect rect, out bool moved, float margin, Fields.FieldKit.ClearanceGrid grid = null)
        {
            rect = default; moved = false;
            var order = new List<string> { at };
            foreach (var p in Positions) if (p != at) order.Add(p);
            for (int k = 0; k < order.Count; k++)
            {
                // Every target of the anchor (each long edge, each corner, each entrance) is searched and the
                // spot closest to its own target wins: "edge" means hugging an edge, not the first edge tried.
                bool found = false; float best = float.MaxValue;
                foreach (var (target, axis) in Targets(region, order[k], gates, length, width, margin, seed))
                    if (Search(region, occ, target, axis, length, width, margin, blocked, out var r, best, grid) && math.distance(r.Center, target) < best - 0.01f)
                    {
                        best = math.distance(r.Center, target);
                        rect = r; found = true;
                        if (best < 0.5f) break;
                    }
                if (!found) continue;
                moved = k > 0;
                return true;
            }
            return false;
        }

        private static List<(float2 p, float2 axis)> Targets(LayerRegion region, string at, IReadOnlyList<float2> gates, float length, float width, float margin, uint seed)
        {
            var list = new List<(float2, float2)>();
            region.PrincipalAxes(out var centroid, out var major, out _, out _);
            int n = region.Ring.Length;
            switch (at)
            {
                case "entrance":
                    if (gates != null)
                        foreach (var g in gates)
                        {
                            var p = region.ClosestOnBoundary(g, out int seg, out _);
                            var a = region.Ring[seg]; var b = region.Ring[(seg + 1) % n];
                            // Leave room for the approach path between the gate and the amenity.
                            list.Add((p + region.InwardNormal(seg) * (width * 0.5f + margin + 4f) + math.normalizesafe(b - a) * (length * 0.5f + 3f), math.normalizesafe(b - a, major)));
                        }
                    break;
                case "edge":
                {
                    // Longest edges first; the amenity's long side lies along the edge.
                    var edges = new List<(float len, int i)>();
                    for (int i = 0; i < n; i++) edges.Add((math.distance(region.Ring[i], region.Ring[(i + 1) % n]), i));
                    edges.Sort((x, y) => x.len != y.len ? y.len.CompareTo(x.len) : x.i.CompareTo(y.i));
                    for (int k = 0; k < edges.Count && k < 4; k++)
                    {
                        int i = edges[k].i;
                        var a = region.Ring[i]; var b = region.Ring[(i + 1) % n];
                        list.Add((math.lerp(a, b, 0.5f) + region.InwardNormal(i) * (width * 0.5f + margin + 1f), math.normalizesafe(b - a, major)));
                    }
                    break;
                }
                case "corner":
                {
                    var corners = new List<(float score, int i)>();
                    bool ccw = Poly.SignedArea(region.Ring) > 0f;
                    for (int i = 0; i < n; i++)
                    {
                        var prev = region.Ring[(i + n - 1) % n]; var p = region.Ring[i]; var next = region.Ring[(i + 1) % n];
                        var d0 = math.normalizesafe(p - prev); var d1 = math.normalizesafe(next - p);
                        float cross = d0.x * d1.y - d0.y * d1.x;
                        if (ccw ? cross <= 0.05f : cross >= -0.05f) continue;   // convex corners only
                        float tie = Hashing.Unit(Hashing.Mix(seed, (uint)i + 1u)) * 0.01f;
                        corners.Add((math.dot(d0, d1) + tie, i));
                    }
                    corners.Sort((x, y) => x.score != y.score ? x.score.CompareTo(y.score) : x.i.CompareTo(y.i));
                    for (int k = 0; k < corners.Count && k < 4; k++)
                    {
                        int i = corners[k].i;
                        var prev = region.Ring[(i + n - 1) % n]; var p = region.Ring[i]; var next = region.Ring[(i + 1) % n];
                        var inA = math.normalizesafe(prev - p); var inB = math.normalizesafe(next - p);
                        var axis = math.distance(prev, p) >= math.distance(p, next) ? -inA : inB;
                        // Tuck the rectangle into the corner: half its length along one side, half its width along the other.
                        var sideIn = math.distance(prev, p) >= math.distance(p, next) ? inB : inA;
                        var along = math.distance(prev, p) >= math.distance(p, next) ? inA : inB;
                        list.Add((p + along * (length * 0.5f + margin + 1f) + sideIn * (width * 0.5f + margin + 1f), axis));
                    }
                    break;
                }
                default:
                {
                    var c = region.Contains(centroid) ? centroid : region.DeepestPoint(out _, null, 16);
                    list.Add((c, major));
                    break;
                }
            }
            return list;
        }

        private static bool Search(LayerRegion region, Occupancy occ, float2 target, float2 axis, float length, float width, float margin,
            Func<float2, bool> blocked, out OrientedRect rect, float limit = float.MaxValue, Fields.FieldKit.ClearanceGrid grid = null)
        {
            rect = default;
            axis = math.normalizesafe(axis, new float2(1, 0));
            float hl = length * 0.5f, hw = width * 0.5f;
            float reach = math.min(math.cmax(region.Max - region.Min), 60f + length);
            float step = math.max(2f, math.min(length, width) / 12f);   // 2 m for amenities, coarser for sports fields
            int rings = (int)math.ceil(reach / step);
            int tests = 0;
            for (int ring = 0; ring <= rings && ring * step <= limit; ring++)
            {
                // Square rings of candidates around the target, nearest first (deterministic order).
                int side = ring * 2;
                int count = ring == 0 ? 1 : side * 4;
                for (int k = 0; k < count; k++)
                {
                    int ix, iy;
                    if (ring == 0) { ix = 0; iy = 0; }
                    else
                    {
                        int edge = k / side, o = k % side;
                        switch (edge)
                        {
                            case 0: ix = -ring + o; iy = -ring; break;
                            case 1: ix = ring; iy = -ring + o; break;
                            case 2: ix = ring - o; iy = ring; break;
                            default: ix = -ring; iy = ring - o; break;
                        }
                    }
                    var c = target + new float2(ix, iy) * step;
                    if (grid != null && grid.UpperBound(c) < math.min(hw, hl) * 0.9f) continue;   // sports fields: coarse bound first
                    if (!region.Contains(c)) continue;
                    if (occ.Clearance(c, hw) < math.min(hw, hl) * 0.9f) continue;
                    for (int turn = 0; turn < 2; turn++)
                    {
                        var ax = turn == 0 ? axis : new float2(-axis.y, axis.x);
                        var r = new OrientedRect { Center = c, Axis = ax, HalfLength = hl, HalfWidth = hw };
                        if (blocked != null && Blocked(r, blocked)) continue;
                        if (++tests > (length >= 40f ? 300 : 900)) return false;
                        if (length >= 40f && !Fields.FieldKit.QuickFree(occ, r, margin)) continue;   // big places: cheap rejection first
                        if (!LeisureKit.RectFits(region, occ, r, margin)) continue;
                        rect = r;
                        return true;
                    }
                }
            }
            return false;
        }

        private static bool Blocked(OrientedRect r, Func<float2, bool> blocked)
        {
            for (int u = -2; u <= 2; u++)
                for (int v = -2; v <= 2; v++)
                    if (blocked(r.Local(r.HalfLength * u * 0.5f, r.HalfWidth * v * 0.5f))) return true;
            return false;
        }

        /// <summary>Claims the rectangle (plus margin) in an occupancy.</summary>
        public static void Take(Occupancy occ, OrientedRect r, float margin, int kind)
        {
            var grown = new OrientedRect { Center = r.Center, Axis = r.Axis, HalfLength = r.HalfLength + margin, HalfWidth = r.HalfWidth + margin };
            LeisureKit.TakeRect(occ, grown, kind);
            // TakeRect is a capsule along the long axis: its round ends leave the corners open — close them.
            foreach (var q in grown.Ring) occ.Take(q, math.min(grown.HalfWidth, 3f), kind);
        }

        /// <summary>The oriented rectangle of a place region (a 4-corner ring), else the region's principal frame.</summary>
        public static OrientedRect RectOf(LayerRegion region)
        {
            var ring = Poly.Clean(region.Ring, 0.2f);
            if (ring.Length == 4)
            {
                float l0 = math.distance(ring[0], ring[1]), l1 = math.distance(ring[1], ring[2]);
                var axis = l0 >= l1 ? math.normalizesafe(ring[1] - ring[0]) : math.normalizesafe(ring[2] - ring[1]);
                return new OrientedRect { Center = Poly.Centroid(ring), Axis = axis, HalfLength = math.max(l0, l1) * 0.5f, HalfWidth = math.min(l0, l1) * 0.5f };
            }
            LeisureKit.Frame(region, out var c, out var ax, out var side, out _, out _);
            float minA = float.MaxValue, maxA = float.MinValue, minB = float.MaxValue, maxB = float.MinValue;
            foreach (var v in region.Ring)
            {
                var d = v - c;
                minA = math.min(minA, math.dot(d, ax)); maxA = math.max(maxA, math.dot(d, ax));
                minB = math.min(minB, math.dot(d, side)); maxB = math.max(maxB, math.dot(d, side));
            }
            // Inscribed-ish: the frame box shrunk to what the region actually contains around the centre.
            var box = new OrientedRect { Center = c + ax * (minA + maxA) * 0.5f + side * (minB + maxB) * 0.5f, Axis = ax, HalfLength = (maxA - minA) * 0.5f, HalfWidth = (maxB - minB) * 0.5f };
            for (int i = 0; i < 12; i++)
            {
                bool inside = true;
                foreach (var q in box.Ring) if (!region.Contains(q)) { inside = false; break; }
                if (inside) break;
                box.HalfLength *= 0.88f; box.HalfWidth *= 0.88f;
            }
            return box;
        }
    }
}
