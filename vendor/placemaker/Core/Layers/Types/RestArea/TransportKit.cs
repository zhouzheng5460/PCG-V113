// Shared building blocks of the transport types: a parking lot planned by the Parking layer on a
// sub-polygon (one vehicle entrance, merged into the host plan), straight footpath routing that
// stays inside a concave outline, and a few placement helpers.
//
// The parking planner is reused as-is through the registry: the host cuts a lot polygon that
// touches its own outline at the vehicle entrance, runs "parking" on it and keeps everything it
// produced (surfaces, aisles, stalls, chargers, lamps, planting, the lot area). The host then plans
// the rest of its outline without the lot.

using System;
using System.Collections.Generic;
using Placemaker.Core.Geometry;
using Placemaker.Core.Layers.Common;
using Unity.Mathematics;

namespace Placemaker.Core.Layers.Types.Transport
{
    public sealed class SubLot
    {
        /// <summary>The lot polygon (host outline ∩ cut).</summary>
        public float2[] Ring;
        /// <summary>The cut used (reaches past the host outline), so host − cut is exactly the rest.</summary>
        public float2[] Cut;
        public int Stalls, EvStalls;
        /// <summary>Lot trees, bushes and lanterns (dropped where a host footpath starts at the lot edge).</summary>
        public readonly List<PlannedObject> Plants = new List<PlannedObject>();
    }

    public static class FeatureInfoExtensions
    {
        /// <summary>Uses an existing label key (the add-on keys live with the park / plaza).</summary>
        public static FeatureInfo Relabel(this FeatureInfo f, string labelKey) { f.LabelKey = labelKey; return f; }
    }

    public static class TransportKit
    {
        public const string ParkingLayerId = "parking";
        public const string NoLotKey = "Placemaker.Transport.Issue.NoLot";

        /// <summary>Largest outer ring of a boolean result (holes ignored), or null below <paramref name="minArea"/>.</summary>
        public static float2[] Largest(List<Region> pieces, float minArea)
        {
            float2[] best = null; float bestArea = minArea;
            if (pieces == null) return null;
            foreach (var r in pieces)
            {
                if (r?.Outer == null || r.Outer.Length < 3) continue;
                float a = Poly.Area(r.Outer);
                if (a > bestArea) { bestArea = a; best = r.Outer; }
            }
            return best == null ? null : Poly.EnsureCcw(Poly.Clean(best, 0.05f));
        }

        public static float2[] Intersect(float2[] a, float2[] b, float minArea)
        {
            try { return Largest(Poly.Intersection(a, b), minArea); }
            catch (Exception) { return null; }
        }

        public static float2[] Subtract(float2[] a, float2[] b, float minArea)
        {
            try { return Largest(Poly.Difference(a, b), minArea); }
            catch (Exception) { return null; }
        }

        /// <summary>
        /// Plans a parking lot with the Parking layer on (host ∩ cut), entered at <paramref name="entrance"/>,
        /// and merges it into the host plan. Returns null (and an Info issue) when the lot cannot be built.
        /// </summary>
        public static SubLot PlanLot(LayerContext ctx, float2[] cut, float2 entrance, SettingsBag parking, uint salt)
        {
            var gen = GeneratorRegistry.Get(ParkingLayerId);
            var ring = Intersect(ctx.LayerRegion.Ring, cut, 0f);
            if (gen == null || ring == null || Poly.Area(ring) < math.max(gen.MinArea, 500f))
            {
                ctx.Issue(IssueSeverity.Info, NoLotKey);
                return null;
            }
            var bag = gen.Defaults();
            foreach (var kv in parking.Values) bag.Values[kv.Key] = kv.Value;
            var input = new AreaInput
            {
                Shape = new AreaShape { Ring = Poly.To3D(ring, ctx.H), Source = AreaSource.Drawn },
                Site = ctx.Site,
                Markers = new List<EdgeMarker> { new EdgeMarker { Kind = "entrance", Position = ctx.P3(entrance) } },
                Settings = bag,
                Seed = Hashing.Mix(ctx.Seed, salt),
                RegionTags = new[] { RegionTags.Lot },
                Reserved = ctx.Input.Reserved,
            };
            AreaPlan sub;
            try { sub = gen.Plan(input, ctx.Cancel); }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { sub = null; }
            int stalls = 0, ev = 0;
            if (sub != null)
            {
                sub.Facts.TryGetValue("capacity", out var c);
                sub.Facts.TryGetValue("evCapacity", out var e);
                stalls = (int)c; ev = (int)e;
            }
            if (sub == null || sub.HasErrors || stalls <= 0)
            {
                ctx.Issue(IssueSeverity.Info, NoLotKey);
                return null;
            }
            var plan = ctx.Plan;
            plan.Surfaces.AddRange(sub.Surfaces);
            int netOffset = plan.Nets.Count;   // s58: objects index their net in the sub-plan
            plan.Nets.AddRange(sub.Nets);
            // s58: the lot is graded (level tiers tied to the street): its terraform goes with its nets and objects, which already stand on the graded ground.
            plan.Terraform.AddRange(sub.Terraform);
            plan.Paint.AddRange(sub.Paint);
            plan.Areas.AddRange(sub.Areas);
            plan.Zoning.AddRange(sub.Zoning);
            var lot = new SubLot { Ring = ring, Cut = cut, Stalls = stalls, EvStalls = ev };
            foreach (var o in sub.Objects)
            {
                if (o.AttachedNet >= 0) o.AttachedNet += netOffset;
                if (o.Role == ObjectRole.Tree || o.Role == ObjectRole.Bush || o.Role == ObjectRole.Lamp) lot.Plants.Add(o);
                plan.Objects.Add(o);
            }
            foreach (var i in sub.Issues)
                if (i.Severity != IssueSeverity.Error && i.Key != "Placemaker.Parking.Issue.NoEntrance") plan.Issues.Add(i);
            plan.Stats.AddRange(sub.Stats);
            plan.Facts["capacity"] = stalls;
            plan.Facts["evCapacity"] = ev;
            return lot;
        }

        /// <summary>Parking objects and drive nets as taken space of the host occupancy (the lot lies outside its region).</summary>
        public static void Occupy(Occupancy occ, AreaPlan plan)
        {
            foreach (var n in plan.Nets)
                if (n.Role == NetRole.DriveAisle || n.Role == NetRole.DriveConnector || n.Role == NetRole.Alley)
                    occ.TakeCorridor(n.A.xz, n.D.xz, Composer.DefaultHalfWidth(n.Role), Occupancy.KindReserved);
            foreach (var o in plan.Objects)
                if (o.Role != ObjectRole.Decal) occ.Take(o.Position.xz, TestRadius(o.Role), Occupancy.KindReserved);
        }

        private static float TestRadius(ObjectRole role) => role == ObjectRole.Tree ? 2.5f : role == ObjectRole.Bush ? 1.1f : 0.6f;

        /// <summary>Drops lot planting that would stand on (or crowd) one of the host's footpaths.</summary>
        public static void ClearPlantsFromPaths(AreaPlan plan, SubLot lot, Occupancy occ)
        {
            if (lot == null || lot.Plants.Count == 0 || occ.Paths.Count == 0) return;
            foreach (var o in lot.Plants)
            {
                float r = o.Role == ObjectRole.Tree ? 2.7f : o.Role == ObjectRole.Bush ? 1.4f : 0.8f;
                if (occ.Paths.Overlaps(o.Position.xz, r, 0.3f)) plan.Objects.Remove(o);
            }
        }

        /// <summary>
        /// Straight footpath a→b inside the region, via the region's deepest point when the direct
        /// line leaves a concave outline. Emitted and indexed. Returns the last point reached (b) or a.
        /// </summary>
        public static bool Route(LayerContext ctx, Occupancy occ, float2 a, float2 b, AssetRef asset, NetRole role = NetRole.Pathway)
        {
            var region = ctx.LayerRegion;
            if (math.distance(a, b) < 2f) return false;
            if (Clear(region, occ, a, b)) { LeisureKit.Path(ctx, occ, a, b, role, asset); return true; }
            // One bend: the shortest a→v→b over a coarse grid of interior points (concave outlines, L-shapes).
            if (OneBend(region, occ, a, b, out var via))
            {
                LeisureKit.Path(ctx, occ, a, via, role, asset);
                LeisureKit.Path(ctx, occ, via, b, role, asset);
                return true;
            }
            return false;
        }

        private static bool OneBend(LayerRegion region, Occupancy occ, float2 a, float2 b, out float2 via)
        {
            via = region.DeepestPoint(out _, null, 14);
            var candidates = new List<(float cost, float2 p)> { (math.distance(a, via) + math.distance(via, b), via) };
            var size = region.Max - region.Min;
            float step = math.max(4f, math.sqrt(size.x * size.y / 400f));
            for (float y = region.Min.y + step * 0.5f; y < region.Max.y; y += step)
                for (float x = region.Min.x + step * 0.5f; x < region.Max.x; x += step)
                {
                    var p = new float2(x, y);
                    if (!region.ContainsDisc(p, 3f)) continue;
                    candidates.Add((math.distance(a, p) + math.distance(p, b), p));
                }
            candidates.Sort((l, r) => l.cost != r.cost ? l.cost.CompareTo(r.cost) : l.p.x != r.p.x ? l.p.x.CompareTo(r.p.x) : l.p.y.CompareTo(r.p.y));
            int tries = 0;
            foreach (var (_, p) in candidates)
            {
                if (++tries > 120) break;
                if (Clear(region, occ, a, p) && Clear(region, occ, p, b)) { via = p; return true; }
            }
            return false;
        }

        private static bool Clear(LayerRegion region, Occupancy occ, float2 a, float2 b)
        {
            if (region.CrossesBoundary(a, b, true)) return false;
            // Midpoints inside (CrossesBoundary allows touching at a).
            if (!region.Contains(math.lerp(a, b, 0.5f))) return false;
            // Never through taken things (except other paths, which a path may join).
            float len = math.distance(a, b);
            int k = math.max(1, (int)(len / 3f));
            for (int i = 1; i < k; i++)
            {
                var p = math.lerp(a, b, i / (float)k);
                // Paths start at an edge point chosen free (FreeEdgePoint); its own neighbourhood is not tested.
                if (math.distance(p, a) > 3f && occ.Discs.Overlaps(p, 1f, 0f)) return false;
                if (occ.HitsPolygon(p, 1f)) return false;
            }
            return true;
        }

        /// <summary>
        /// Outline point of <paramref name="region"/> nearest <paramref name="target"/> with no tall lot object
        /// (tree, bush, lantern, charger) within <paramref name="clear"/> metres: where a footpath can leave the car park.
        /// </summary>
        public static float2 FreeEdgePoint(LayerRegion region, float2 target, AreaPlan plan, float clear)
        {
            var p0 = region.ClosestOnBoundary(target, out int seg, out float t);
            float along0 = region.AlongOf(seg, t);
            bool Free(float2 p)
            {
                foreach (var o in plan.Objects)
                    if ((o.Role == ObjectRole.Tree || o.Role == ObjectRole.Bush || o.Role == ObjectRole.Lamp || o.Role == ObjectRole.Charger)
                        && math.distancesq(o.Position.xz, p) < clear * clear) return false;
                return true;
            }
            if (Free(p0)) return p0;
            for (float d = 1.5f; d < math.min(40f, region.Perimeter * 0.5f); d += 1.5f)
                foreach (float s in new[] { 1f, -1f })
                {
                    var p = region.PointAt(along0 + s * d, out _, out _, out _);
                    if (Free(p)) return p;
                }
            return p0;
        }

        /// <summary>Point of the region farthest from <paramref name="from"/> with at least <paramref name="clearance"/> free around it.</summary>
        public static bool FarSpot(Occupancy occ, float2 from, float clearance, out float2 spot)
        {
            var region = occ.LayerRegion;
            spot = region.Centroid;
            var size = region.Max - region.Min;
            float step = math.max(2f, math.sqrt(size.x * size.y / 1600f));
            float best = -1f;
            for (float y = region.Min.y + step * 0.5f; y < region.Max.y; y += step)
                for (float x = region.Min.x + step * 0.5f; x < region.Max.x; x += step)
                {
                    var p = new float2(x, y);
                    if (!region.Contains(p)) continue;
                    if (occ.Clearance(p, clearance + 0.5f) < clearance) continue;
                    float d = math.distance(p, from);
                    if (d > best) { best = d; spot = p; }
                }
            return best >= 0f;
        }

        /// <summary>True when every corner of the rectangle lies inside the region and no edge crosses the outline.</summary>
        public static bool RectInside(LayerRegion region, OrientedRect r)
        {
            var ring = r.Ring;
            foreach (var q in ring) if (!region.Contains(q)) return false;
            for (int i = 0; i < 4; i++) if (region.CrossesBoundary(ring[i], ring[(i + 1) % 4], false)) return false;
            return true;
        }

        /// <summary>Region frame along an outline edge: the edge piece's start, tangent, inward normal and the region's spans.</summary>
        public static void EdgeFrame(LayerRegion region, float2 a, float2 b, out float2 t, out float2 n, out float uMin, out float uMax, out float vMax)
        {
            t = math.normalizesafe(b - a, new float2(1, 0));
            n = new float2(-t.y, t.x);
            if (!region.Contains(math.lerp(a, b, 0.5f) + n * 0.5f)) n = -n;
            uMin = float.MaxValue; uMax = float.MinValue; vMax = 0f;
            foreach (var p in region.Ring)
            {
                float u = math.dot(p - a, t), v = math.dot(p - a, n);
                uMin = math.min(uMin, u); uMax = math.max(uMax, u); vMax = math.max(vMax, v);
            }
        }

        /// <summary>Rectangle in an edge frame from u0..u1 along the edge and v0..v1 inward.</summary>
        public static float2[] FrameRect(float2 origin, float2 t, float2 n, float u0, float u1, float v0, float v1) => new[]
        {
            origin + t * u0 + n * v0, origin + t * u1 + n * v0, origin + t * u1 + n * v1, origin + t * u0 + n * v1,
        };

        /// <summary>Copy of a settings bag with the given values set.</summary>
        public static SettingsBag Bag(params (string key, object value)[] values)
        {
            var b = new SettingsBag();
            foreach (var (k, v) in values) b.Values[k] = v;
            return b;
        }
    }
}
