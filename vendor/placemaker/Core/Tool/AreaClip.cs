// Placemaker — "Keep off existing roads" (FEEDBACK-2 #3).
//
// A drawn or painted area that overlaps existing roads would plant trees on the asphalt. Before
// planning, the corridors of nearby nets (centre line ± half the composition width, which in CS2
// already includes the sidewalks, plus a small edge margin) are subtracted from the outline:
//   * the largest remaining piece is kept; pieces cut off by a road through the area are dropped
//     and reported as an Info issue (the player can select them separately);
//   * thin slivers (a curb-snapped edge grazing the margin) are ignored — the outline is kept as is;
//   * attachments are recomputed against the clipped outline, so entrances sit at the road edge.
// Block and perimeter selections are traced on road edges already and are not clipped by the tool.
// Pure geometry (Poly.Boolean); the tool keeps the removed parts to dim them in the overlay.

using System;
using System.Collections.Generic;
using Placemaker.Core.Geometry;
using Unity.Mathematics;

namespace Placemaker.Core.Tool
{
    public sealed class AreaClipResult
    {
        /// <summary>The outline to plan with (the input shape itself when nothing was cut).</summary>
        public AreaShape Shape;
        public bool Clipped;
        /// <summary>Area removed in total (road corridors + dropped pieces), m².</summary>
        public float RemovedArea;
        /// <summary>Pieces cut off from the kept part by a road running through the area.</summary>
        public int DroppedPieces;
        public float DroppedArea;
        /// <summary>Removed parts (road overlap and dropped pieces) for the overlay.</summary>
        public readonly List<float3[]> Removed = new List<float3[]>();
        /// <summary>Info issue for the summary, or null.</summary>
        public PlanIssue Issue;
    }

    public static class AreaClip
    {
        /// <summary>Extra distance kept from the road's outer edge (m).</summary>
        public const float EdgeMargin = 0.25f;
        /// <summary>Removed parts thinner than this (2·area / perimeter) are slivers and ignored.</summary>
        public const float SliverThickness = 0.75f;
        public const float MinRemovedArea = 2f;
        public const float SampleSpacing = 2f;

        public const string IssueTrimmed = "Placemaker.Issue.KeptOffRoads";
        public const string IssueSplit = "Placemaker.Issue.AreaSplitByRoad";

        public static bool ClipsSource(AreaSource s) => s == AreaSource.Drawn || s == AreaSource.Painted;

        /// <summary>Whether a net counts as an obstacle for the area (roads, tracks…; pedestrian paths are kept).</summary>
        public static bool Blocks(NearbyNet n) => n.Width > 0.1f && n.Category != NetCategory.Pathway;

        public static AreaClipResult Apply(AreaShape shape, IReadOnlyList<NearbyNet> nets, Func<float2, float> heightAt = null, float margin = EdgeMargin)
        {
            var result = new AreaClipResult { Shape = shape };
            if (shape?.Ring == null || shape.Ring.Length < 3 || nets == null || nets.Count == 0) return result;
            var ring = Poly.EnsureCcw(shape.Ring2D());
            float origArea = Poly.Area(ring);
            if (origArea < 1f) return result;
            Poly.Bounds(ring, out var min, out var max);

            var corridors = Corridors(nets, min, max, margin);
            if (corridors.Count == 0) return result;

            List<Region> pieces;
            try { pieces = Poly.Boolean(new IReadOnlyList<float2>[] { ring }, corridors, BoolOp.Difference); }
            catch (Exception) { return result; }
            pieces.RemoveAll(p => p == null || p.Outer == null || p.Outer.Length < 3 || p.Area < 0.5f);
            if (pieces.Count == 0) return result;   // entirely on a road: keep the outline, the plan will report what it can't do
            pieces.Sort((a, b) => b.Area.CompareTo(a.Area));

            var kept = pieces[0];
            float keptArea = kept.Area;
            float removed = origArea - keptArea;
            if (removed < MinRemovedArea) return result;

            // Only slivers along the edge (and nothing dropped): leave the outline alone.
            List<Region> removedParts;
            try { removedParts = Poly.Boolean(new IReadOnlyList<float2>[] { ring }, new IReadOnlyList<float2>[] { kept.Outer }, BoolOp.Difference); }
            catch (Exception) { removedParts = new List<Region>(); }
            bool anyThick = false;
            foreach (var r in removedParts)
            {
                float per = r.Perimeter;
                if (per > 0 && 2f * r.Area / per >= SliverThickness && r.Area >= MinRemovedArea) { anyThick = true; break; }
            }
            if (!anyThick && pieces.Count == 1) return result;

            float2[] outer = kept.Outer;
            if (kept.HasHoles)
            {
                // A road loop entirely inside the area: CS2 areas cannot have holes — open it up and keep the largest part.
                try
                {
                    var cut = Poly.CutHoles(kept);
                    float best = -1;
                    foreach (var c in cut) { float a = Poly.Area(c); if (a > best) { best = a; outer = c; } }
                }
                catch (Exception) { outer = kept.Outer; }
            }
            outer = Poly.EnsureCcw(Poly.RemoveCollinear(Poly.Clean(outer, 0.05f), 1e-4f));
            if (outer.Length < 3) return result;

            float fallbackY = shape.Ring[0].y;
            Func<float2, float> h = heightAt ?? (p => NearestHeight(shape.Ring, p, fallbackY));
            var newRing = Poly.To3D(outer, h);
            var clipped = new AreaShape { Ring = newRing, Source = shape.Source };
            try { clipped.Attachments = AttachmentFinder.Find(newRing, nets); }
            catch (Exception) { clipped.Attachments = new List<BoundaryAttachment>(); }

            result.Shape = clipped;
            result.Clipped = true;
            result.RemovedArea = origArea - Poly.Area(outer);
            for (int i = 1; i < pieces.Count; i++) { result.DroppedPieces++; result.DroppedArea += pieces[i].Area; }
            foreach (var r in removedParts)
                if (r.Outer != null && r.Outer.Length >= 3) result.Removed.Add(Poly.To3D(r.Outer, h));

            float2 at = Poly.Centroid(outer);
            if (removedParts.Count > 0)
            {
                Region biggest = removedParts[0];
                foreach (var r in removedParts) if (r.Area > biggest.Area) biggest = r;
                at = Poly.Centroid(biggest.Outer);
            }
            result.Issue = result.DroppedPieces > 0
                ? new PlanIssue { Severity = IssueSeverity.Info, Key = IssueSplit, Args = new object[] { result.DroppedPieces, Math.Round(result.DroppedArea) }, At = new float3(at.x, h(at), at.y) }
                : new PlanIssue { Severity = IssueSeverity.Info, Key = IssueTrimmed, Args = new object[] { Math.Round(result.RemovedArea) }, At = new float3(at.x, h(at), at.y) };
            return result;
        }

        /// <summary>
        /// Corridor polygons (CCW) of the nets near the box [min, max]: a band along the sampled centre
        /// line at ± (width / 2 + margin), with round caps at both ends (they cover the node area).
        /// </summary>
        public static List<IReadOnlyList<float2>> Corridors(IReadOnlyList<NearbyNet> nets, float2 min, float2 max, float margin)
        {
            var list = new List<IReadOnlyList<float2>>();
            foreach (var n in nets)
            {
                if (!Blocks(n)) continue;
                float hw = n.Width * 0.5f + math.max(0f, margin);
                var bez = new CubicBezier(n.A, n.B, n.C, n.D);
                // Control points bound the curve: cheap reject before sampling.
                float2 bmin = math.min(math.min(n.A.xz, n.B.xz), math.min(n.C.xz, n.D.xz)) - hw;
                float2 bmax = math.max(math.max(n.A.xz, n.B.xz), math.max(n.C.xz, n.D.xz)) + hw;
                if (bmin.x > max.x || bmin.y > max.y || bmax.x < min.x || bmax.y < min.y) continue;

                float len = bez.LengthXZ(0.05f);
                if (len < 0.1f) { list.Add(Disc(n.A.xz, hw)); continue; }
                int count = math.clamp((int)math.ceil(len / SampleSpacing), 1, 256);
                var pts = new float2[count + 1];
                for (int i = 0; i <= count; i++) pts[i] = bez.Position((float)i / count).xz;

                var left = new float2[pts.Length];
                var right = new float2[pts.Length];
                for (int i = 0; i < pts.Length; i++)
                {
                    float2 d = pts[math.min(i + 1, pts.Length - 1)] - pts[math.max(i - 1, 0)];
                    d = math.normalizesafe(d, new float2(1, 0));
                    float2 nrm = new float2(-d.y, d.x);
                    left[i] = pts[i] + nrm * hw;
                    right[i] = pts[i] - nrm * hw;
                }
                var band = new List<float2>(pts.Length * 2);
                for (int i = 0; i < pts.Length; i++) band.Add(right[i]);
                for (int i = pts.Length - 1; i >= 0; i--) band.Add(left[i]);
                list.Add(Poly.EnsureCcw(band.ToArray()));
                list.Add(Disc(pts[0], hw));
                list.Add(Disc(pts[pts.Length - 1], hw));
            }
            return list;
        }

        private static float2[] Disc(float2 c, float r, int sides = 16)
        {
            var d = new float2[sides];
            for (int i = 0; i < sides; i++)
            {
                float a = i * 2f * math.PI / sides;
                d[i] = c + new float2(math.cos(a), math.sin(a)) * r;
            }
            return d;
        }

        private static float NearestHeight(float3[] ring, float2 p, float fallback)
        {
            float best = float.MaxValue, y = fallback;
            foreach (var q in ring)
            {
                float d = math.distancesq(q.xz, p);
                if (d < best) { best = d; y = q.y; }
            }
            return y;
        }
    }
}
