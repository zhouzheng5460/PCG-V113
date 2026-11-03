// Ported from ParkManager (tomkpunkt, GPL-3.0), Geometry/PlazaPlanner.cs and PlazaPlan.cs.
//
// Centerpieces (single / mirrored pair / three on the main axis) and an ordered furniture
// arrangement placed either in point-mirrored pairs on a stadium ring around the centre, or as
// groups along the edges (mirrored across the outline's own symmetry axis when it has one).
// Changes: every candidate is also checked against Occupancy (input.Reserved and site
// obstacles), arrangement items carry Placemaker roles/categories/assets, and the
// "density %" became an explicit group count.

using System;
using System.Collections.Generic;
using Placemaker.Core.Layers.Common;
using Unity.Mathematics;

namespace Placemaker.Core.Layers.Plaza
{
    public enum CenterMode { Single = 0, Mirrored = 1, Axis = 2, None = 3 }
    public enum ArrangementMode { AroundCenter = 0, AlongEdges = 1 }

    /// <summary>One slot of the ordered furniture arrangement (e.g. bench – lamp – bench).</summary>
    public struct ArrangementItem
    {
        public string Kind;          // bench, lamp, bin, tree, bush, planter, flowerbed, kiosk
        public ObjectRole Role;
        public string Category;
        public AssetRef Asset;
        public float FootprintRadius;
    }

    public struct PlacedCenterpiece { public float2 Position; public float Radius; }

    public struct PlacedFurniture
    {
        public ArrangementItem Item;
        public int ArrangementId;
        public float2 Position;
        public float Yaw;
    }

    public sealed class PlazaLayout
    {
        public float2 Center;
        public float CenterClearance;
        public readonly List<PlacedCenterpiece> Centerpieces = new List<PlacedCenterpiece>();
        public readonly List<PlacedFurniture> Furniture = new List<PlacedFurniture>();
        public bool CenterpieceDropped;
        /// <summary>Radius of the "core" region around Center.</summary>
        public float CoreRadius;
    }

    public static class PlazaPlanner
    {
        private const float CenterSafetyMargin = 0.75f;
        private const float FurnitureBoundaryClearance = 0.65f;
        private const float EntranceFurnitureClearance = 7f;
        private const int MaximumFurniture = 64;
        private const float ItemGap = 0.55f;

        public struct Options
        {
            public CenterMode CenterMode;
            public float CenterRadius;
            public float CenterSpacing;
            public ArrangementMode ArrangementMode;
            public float ArrangementGap;
            public int Groups;
            public List<ArrangementItem> Arrangement;
            /// <summary>(s18) Centre to use when it has the clearance (the place focal point), before the centroid.</summary>
            public float2? PreferredCenter;
            /// <summary>(s18) Furniture keeps at least this far from the centre (a fountain feature with its seating ring).</summary>
            public float KeepClear;
        }

        public static PlazaLayout Generate(LayerRegion region, Occupancy occ, IReadOnlyList<float2> entrances, Options o)
        {
            var layout = new PlazaLayout();
            float spacing = math.clamp(o.CenterSpacing, 5f, 60f);
            float gap = math.clamp(o.ArrangementGap, 0f, 20f);
            float radius = o.CenterMode == CenterMode.None ? 0f : math.max(0f, o.CenterRadius);
            if (!TryChooseCenter(region, occ, radius, o.PreferredCenter, out var center, out float clearance))
            {
                // The centerpiece is optional: keep the plaza buildable when it does not fit.
                if (radius > 0f) layout.CenterpieceDropped = true;
                radius = 0f;
                TryChooseCenter(region, occ, 0f, o.PreferredCenter, out center, out clearance);
            }
            layout.Center = center;
            layout.CenterClearance = clearance;
            // Without a centerpiece the middle stays open: furniture rings around a reserved core, which
            // the layer hands to children ("core" region) — e.g. a fountain add-on of any size.
            float protectedRadius = radius > 0f ? math.max(2f, radius) + CenterSafetyMargin : math.clamp(clearance * 0.35f, 0.75f, 14f);
            protectedRadius = math.max(protectedRadius, o.KeepClear);
            layout.CoreRadius = radius > 0f ? math.min(clearance * 0.6f, 25f) : protectedRadius - 0.5f;
            var major = MajorAxis(region.Ring, center, out float majorSpan, out float minorSpan);

            if (radius > 0f)
            {
                int count = o.CenterMode == CenterMode.Single ? 1 : o.CenterMode == CenterMode.Mirrored ? 2 : 3;
                float step = o.CenterMode == CenterMode.Mirrored
                    ? math.max(radius * 2f + 2f, spacing) * 0.5f
                    : math.max(radius * 2f + 2f, spacing);
                var positions = new List<float2>();
                if (count == 1) positions.Add(center);
                else if (count == 2) { positions.Add(center - major * step); positions.Add(center + major * step); }
                else { positions.Add(center - major * step); positions.Add(center); positions.Add(center + major * step); }
                bool allFit = true;
                foreach (var p in positions)
                    if (!region.ContainsDisc(p, radius + CenterSafetyMargin) || !occ.Free(p, radius, 0f, CenterSafetyMargin)) { allFit = false; break; }
                if (!allFit && count > 1) { positions.Clear(); positions.Add(center); }
                foreach (var p in positions) layout.Centerpieces.Add(new PlacedCenterpiece { Position = p, Radius = radius });
            }

            var arrangement = o.Arrangement;
            if (arrangement == null || arrangement.Count == 0 || o.Groups <= 0) return layout;
            float maxRadius = 1.35f, width = 0f;
            foreach (var item in arrangement) { maxRadius = math.max(maxRadius, item.FootprintRadius); width += item.FootprintRadius * 2f; }
            width += math.max(0, arrangement.Count - 1) * ItemGap;

            if (o.ArrangementMode == ArrangementMode.AroundCenter)
            {
                int pairs = math.max(1, (o.Groups + 1) / 2);
                pairs = math.min(pairs, math.max(1, MaximumFurniture / (arrangement.Count * 2)));
                Circular(layout, region, occ, entrances, center, protectedRadius, gap, width, maxRadius, majorSpan, minorSpan, pairs, arrangement);
                return layout;
            }

            var candidates = BoundaryCandidates(region.Ring, gap, width, maxRadius);
            bool mirrored = TryMirrorAxis(region.Ring, out var mirrorNormal, out float mirrorOffset);
            int groupSize = arrangement.Count * (mirrored ? 2 : 1);
            int desired = mirrored ? math.max(1, (o.Groups + 1) / 2) : o.Groups;
            desired = math.min(desired, MaximumFurniture / groupSize);
            int accepted = 0;
            for (int i = 0; i < candidates.Count && accepted < desired; i++)
            {
                if (layout.Furniture.Count + groupSize > MaximumFurniture) break;
                if (TryBoundaryGroup(layout, region, occ, candidates[i], arrangement, entrances, gap, i + 1, mirrored, mirrorNormal, mirrorOffset))
                    accepted++;
            }
            return layout;
        }

        private struct Anchor { public float2 Position; public float Rotation; public float2 Tangent; }

        private static void Circular(PlazaLayout layout, LayerRegion region, Occupancy occ, IReadOnlyList<float2> entrances, float2 center,
            float protectedRadius, float spacing, float width, float maxRadius, float majorSpan, float minorSpan, int desiredPairs,
            List<ArrangementItem> arrangement)
        {
            float inner = protectedRadius + spacing + maxRadius + 0.5f;
            float available = math.min(majorSpan, minorSpan) * 0.5f - width * 0.5f - maxRadius;
            float ringStep = math.max(8f, width + 4f);
            var axis = new float2(1f, 0f);
            float extent = 0f;
            var cps = layout.Centerpieces;
            if (cps.Count > 1)
            {
                var span = cps[cps.Count - 1].Position - cps[0].Position;
                extent = math.length(span) * 0.5f;
                if (extent > 0.001f) axis = span / (extent * 2f); else extent = 0f;
            }
            // Try a complete, evenly distributed ring; with fewer pairs recompute every position
            // instead of keeping a prefix of a denser ring.
            for (int pairs = desiredPairs; pairs >= 1; pairs--)
                for (int ring = 0; ring < 3; ring++)
                {
                    float distance = inner + ring * ringStep;
                    if (distance > available + width * 0.5f) break;
                    float pairStep = (math.PI * distance + extent * 2f) / pairs;
                    for (int phase = 0; phase < 8; phase++)
                    {
                        var trial = new List<PlacedFurniture>();
                        for (int pair = 0; pair < pairs; pair++)
                        {
                            var pos = StadiumPoint(center, axis, extent, distance, (pair + phase / 8f) * pairStep, out var outward);
                            var anchor = new Anchor { Position = pos, Rotation = math.atan2(-outward.x, -outward.y), Tangent = new float2(-outward.y, outward.x) };
                            if (!TryPair(trial, layout, region, occ, anchor, arrangement, center, entrances, spacing, pair + 1)) break;
                        }
                        if (trial.Count != pairs * arrangement.Count * 2) continue;
                        layout.Furniture.AddRange(trial);
                        return;
                    }
                }
        }

        private static bool TryPair(List<PlacedFurniture> trial, PlazaLayout layout, LayerRegion region, Occupancy occ, Anchor anchor,
            List<ArrangementItem> arrangement, float2 center, IReadOnlyList<float2> entrances, float spacing, int id)
        {
            float width = 0f;
            foreach (var it in arrangement) width += it.FootprintRadius * 2f;
            width += (arrangement.Count - 1) * ItemGap;
            float offset = -width * 0.5f;
            var proposed = new List<PlacedFurniture>(trial);
            foreach (var item in arrangement)
            {
                offset += item.FootprintRadius;
                var first = anchor.Position + anchor.Tangent * offset;
                var second = center * 2f - first;
                bool round = IsRotationFree(item.Role);
                if (!Valid(first, item.FootprintRadius, layout, region, occ, entrances, proposed, id, ArrangementMode.AroundCenter, spacing)
                    || !Valid(second, item.FootprintRadius, layout, region, occ, entrances, proposed, id, ArrangementMode.AroundCenter, spacing))
                    return false;
                proposed.Add(new PlacedFurniture { Item = item, ArrangementId = id, Position = first, Yaw = Wrap(round ? 0f : anchor.Rotation) });
                proposed.Add(new PlacedFurniture { Item = item, ArrangementId = id, Position = second, Yaw = Wrap(round ? 0f : anchor.Rotation + math.PI) });
                offset += item.FootprintRadius + ItemGap;
            }
            trial.Clear();
            trial.AddRange(proposed);
            return true;
        }

        private static bool TryBoundaryGroup(PlazaLayout layout, LayerRegion region, Occupancy occ, Anchor anchor, List<ArrangementItem> arrangement,
            IReadOnlyList<float2> entrances, float spacing, int id, bool mirrored, float2 mirrorNormal, float mirrorOffset)
        {
            float width = 0f;
            foreach (var it in arrangement) width += it.FootprintRadius * 2f;
            width += (arrangement.Count - 1) * ItemGap;
            float offset = -width * 0.5f;
            var proposed = new List<PlacedFurniture>(layout.Furniture);
            var forward = new float2(math.sin(anchor.Rotation), math.cos(anchor.Rotation));
            var reflectedForward = mirrored ? Reflect(forward, mirrorNormal) : float2.zero;
            foreach (var item in arrangement)
            {
                offset += item.FootprintRadius;
                var first = anchor.Position + anchor.Tangent * offset;
                bool round = IsRotationFree(item.Role);
                if (!Valid(first, item.FootprintRadius, layout, region, occ, entrances, proposed, id, ArrangementMode.AlongEdges, spacing)) return false;
                proposed.Add(new PlacedFurniture { Item = item, ArrangementId = id, Position = first, Yaw = Wrap(round ? 0f : anchor.Rotation) });
                if (mirrored)
                {
                    var second = ReflectPoint(first, mirrorNormal, mirrorOffset);
                    if (!Valid(second, item.FootprintRadius, layout, region, occ, entrances, proposed, id, ArrangementMode.AlongEdges, spacing)) return false;
                    proposed.Add(new PlacedFurniture
                    {
                        Item = item, ArrangementId = id, Position = second,
                        Yaw = Wrap(round ? 0f : math.atan2(reflectedForward.x, reflectedForward.y)),
                    });
                }
                offset += item.FootprintRadius + ItemGap;
            }
            layout.Furniture.Clear();
            layout.Furniture.AddRange(proposed);
            return true;
        }

        private static bool IsRotationFree(ObjectRole role) => role == ObjectRole.Bush || role == ObjectRole.Tree;

        /// <summary>Arc length s on a stadium (half straight `extent`, cap `radius`); s and s + perimeter/2 are mirror partners.</summary>
        private static float2 StadiumPoint(float2 center, float2 axis, float extent, float radius, float s, out float2 outward)
        {
            var normal = new float2(-axis.y, axis.x);
            float quarter = math.PI * 0.5f * radius, straight = extent * 2f;
            float angle, side;
            if (s < quarter) { angle = s / radius; side = 1f; }
            else if ((s -= quarter) < straight) { outward = normal; return center + axis * (extent - s) + normal * radius; }
            else if ((s -= straight) < quarter * 2f) { angle = math.PI * 0.5f + s / radius; side = -1f; }
            else if ((s -= quarter * 2f) < straight) { outward = -normal; return center + axis * (s - extent) - normal * radius; }
            else { angle = math.PI * 1.5f + (s - straight) / radius; side = 1f; }
            outward = axis * math.cos(angle) + normal * math.sin(angle);
            return center + axis * (extent * side) + outward * radius;
        }

        private static List<Anchor> BoundaryCandidates(float2[] ring, float spacing, float width, float maxRadius)
        {
            var result = new List<Anchor>();
            var perEdge = new List<Anchor>[ring.Length];
            int maxPerEdge = 0;
            float endMargin = width * 0.5f + maxRadius + 0.5f;
            float groupSpacing = math.max(10f, width + maxRadius * 2f + 1.25f);
            for (int e = 0; e < ring.Length; e++)
            {
                var a = ring[e];
                var edge = ring[(e + 1) % ring.Length] - a;
                float len = math.length(edge);
                if (len < endMargin * 2f) continue;
                var t = edge / len;
                var inward = new float2(-t.y, t.x);   // ring is CCW
                int count = math.clamp((int)math.floor((len - endMargin * 2f) / groupSpacing) + 1, 1, 8);
                float start = (len - (count - 1) * groupSpacing) * 0.5f;
                var list = new List<Anchor>(count);
                for (int i = 0; i < count; i++)
                    list.Add(new Anchor { Position = a + t * (start + i * groupSpacing) + inward * (spacing + maxRadius), Rotation = math.atan2(inward.x, inward.y), Tangent = t });
                perEdge[e] = list;
                maxPerEdge = math.max(maxPerEdge, count);
            }
            // Visit every side before taking a second position on a long side.
            for (int slot = 0; slot < maxPerEdge; slot++)
                for (int e = 0; e < perEdge.Length; e++)
                    if (perEdge[e] != null && slot < perEdge[e].Count) result.Add(perEdge[e][slot]);
            return result;
        }

        /// <summary>Mirror axis of the outline itself (the centre may be off-axis in concave plazas).</summary>
        private static bool TryMirrorAxis(float2[] ring, out float2 normal, out float offset)
        {
            var mean = float2.zero;
            foreach (var p in ring) mean += p;
            mean /= ring.Length;
            float xx = 0, xy = 0, yy = 0;
            foreach (var p in ring) { var d = p - mean; xx += d.x * d.x; xy += d.x * d.y; yy += d.y * d.y; }
            float angle = 0.5f * math.atan2(2f * xy, xx - yy);
            var majorAxis = new float2(math.cos(angle), math.sin(angle));
            var perpendicular = new float2(-majorAxis.y, majorAxis.x);
            if (MirrorsAcross(ring, perpendicular, out offset)) { normal = perpendicular; return true; }
            if (MirrorsAcross(ring, majorAxis, out offset)) { normal = majorAxis; return true; }
            // Regular polygons can have equal moments with rotated axes; every mirror axis passes
            // through a vertex or an edge midpoint and the vertex mean.
            for (int i = 0; i < ring.Length; i++)
            {
                var vd = ring[i] - mean;
                if (math.lengthsq(vd) > 0.0001f)
                {
                    normal = math.normalizesafe(new float2(-vd.y, vd.x));
                    if (MirrorsAcross(ring, normal, out offset)) return true;
                }
                var md = (ring[i] + ring[(i + 1) % ring.Length]) * 0.5f - mean;
                if (math.lengthsq(md) <= 0.0001f) continue;
                normal = math.normalizesafe(new float2(-md.y, md.x));
                if (MirrorsAcross(ring, normal, out offset)) return true;
            }
            normal = float2.zero; offset = 0f;
            return false;
        }

        private static bool MirrorsAcross(float2[] ring, float2 normal, out float offset)
        {
            float min = float.MaxValue, max = float.MinValue;
            foreach (var p in ring) { float d = math.dot(p, normal); min = math.min(min, d); max = math.max(max, d); }
            offset = (min + max) * 0.5f;
            const float tol = 0.01f;
            for (int i = 0; i < ring.Length; i++)
            {
                var a = ReflectPoint(ring[i], normal, offset);
                var b = ReflectPoint(ring[(i + 1) % ring.Length], normal, offset);
                bool match = false;
                for (int j = 0; j < ring.Length && !match; j++)
                    match = math.distancesq(a, ring[(j + 1) % ring.Length]) <= tol && math.distancesq(b, ring[j]) <= tol;
                if (!match) return false;
            }
            return true;
        }

        private static float2 ReflectPoint(float2 p, float2 n, float offset) => p - n * (2f * (math.dot(p, n) - offset));
        private static float2 Reflect(float2 d, float2 n) => d - n * (2f * math.dot(d, n));

        public static float2 MajorAxis(float2[] ring, float2 center, out float majorSpan, out float minorSpan)
        {
            float xx = 0, xy = 0, yy = 0;
            foreach (var p in ring) { var d = p - center; xx += d.x * d.x; xy += d.x * d.y; yy += d.y * d.y; }
            float angle = 0.5f * math.atan2(2f * xy, xx - yy);
            var axis = new float2(math.cos(angle), math.sin(angle));
            var normal = new float2(-axis.y, axis.x);
            float minA = float.MaxValue, maxA = float.MinValue, minB = float.MaxValue, maxB = float.MinValue;
            foreach (var p in ring)
            {
                var d = p - center;
                float a = math.dot(d, axis), b = math.dot(d, normal);
                minA = math.min(minA, a); maxA = math.max(maxA, a); minB = math.min(minB, b); maxB = math.max(maxB, b);
            }
            majorSpan = maxA - minA; minorSpan = maxB - minB;
            if (minorSpan > majorSpan) { var s = majorSpan; majorSpan = minorSpan; minorSpan = s; return normal; }
            return axis;
        }

        private static bool Valid(float2 p, float footprint, PlazaLayout layout, LayerRegion region, Occupancy occ, IReadOnlyList<float2> entrances,
            List<PlacedFurniture> accepted, int arrangementId, ArrangementMode mode, float spacing)
        {
            float boundaryGap = mode == ArrangementMode.AlongEdges ? spacing : FurnitureBoundaryClearance;
            // Edge candidates sit exactly at spacing + max radius; the small tolerance keeps float noise from rejecting them.
            if (!region.ContainsDisc(p, footprint + boundaryGap - 0.02f)) return false;
            foreach (var e in entrances)
                if (math.distancesq(p, e) < EntranceFurnitureClearance * EntranceFurnitureClearance) return false;
            if (layout.Centerpieces.Count == 0 && layout.CoreRadius > 0f &&
                math.distance(p, layout.Center) < layout.CoreRadius + 0.5f + footprint) return false;
            foreach (var c in layout.Centerpieces)
            {
                float clear = c.Radius + CenterSafetyMargin + footprint + (mode == ArrangementMode.AroundCenter ? spacing : 0.75f);
                if (math.distancesq(p, c.Position) < clear * clear) return false;
            }
            foreach (var a in accepted)
            {
                float combined = footprint + a.Item.FootprintRadius + (arrangementId != 0 && a.ArrangementId == arrangementId ? 0.4f : 1.25f);
                if (math.distancesq(p, a.Position) < combined * combined) return false;
            }
            return occ.Free(p, footprint, 0f, 0.3f);
        }

        /// <summary>Centroid when it has the clearance, else the most open interior point.</summary>
        private static bool TryChooseCenter(LayerRegion region, Occupancy occ, float required, float2? preferred, out float2 center, out float clearance)
        {
            if (preferred.HasValue && region.Contains(preferred.Value))
            {
                center = preferred.Value;
                clearance = occ.Clearance(center, math.cmax(region.Max - region.Min));
                if (clearance >= required + CenterSafetyMargin) return true;
            }
            center = region.Centroid;
            if (region.Contains(center))
            {
                clearance = occ.Clearance(center, math.cmax(region.Max - region.Min));
                if (clearance >= required + CenterSafetyMargin) return true;
            }
            center = region.DeepestPoint(out clearance, (p, cap) => occ.Clearance(p, cap), 24);
            return clearance >= required + CenterSafetyMargin;
        }

        private static float Wrap(float a)
        {
            float full = math.PI * 2f;
            a %= full;
            return a < 0f ? a + full : a;
        }
    }
}
