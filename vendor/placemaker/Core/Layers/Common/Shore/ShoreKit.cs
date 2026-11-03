// Shore kit (grand park wave, g2): water awareness any layer can call.
//
//   Survey      cheap WaterDepthAt sampling: how much of the area is wet, how much of its outline
//               looks out over water, and the water surface height.
//   CarveShore  the host hook ("Shore" feature of park / plaza): when the area is partly in water (or
//               runs along it) the host keeps the dry land minus a shore band, and hands the band plus
//               the water inside the outline to the shore feature as "place:shore" regions. A host that
//               does not touch water is left untouched, so the feature can be on by default.
//   Treatments  ShoreTreatments.cs: pier, wharf (terraformed quay with bollards and moored boats),
//               boardwalk, seawall promenade, beach edge, reed edge — all laid out along a Shoreline
//               (Types/Beach/Shoreline.cs) by distance from the water.
// Everything that stands on a deck or a filled quay gets that height explicitly (objects are planned
// at the original, underwater terrain height otherwise).

using System;
using System.Collections.Generic;
using Placemaker.Core.Geometry;
using Placemaker.Core.Layers.Types.Beach;
using Unity.Mathematics;

namespace Placemaker.Core.Layers.Common.Shore
{
    /// <summary>What a quick look at the water around an area found.</summary>
    public struct WaterSurvey
    {
        public int Samples, Wet;
        public float WetFraction;
        /// <summary>Outline length (m) whose outward probes are wet.</summary>
        public float WetEdgeLength;
        /// <summary>Mean water surface height, NaN when no water was found.</summary>
        public float WaterLevel;

        public bool Inside => WetFraction >= ShoreKit.MinInsideFraction;
        public bool Edge => WetEdgeLength >= ShoreKit.MinEdgeLength;
        public bool Any => Inside || Edge;
    }

    public static class ShoreKit
    {
        public const string LayerId = "shore";
        public const string Place = "place:shore";
        public const string WaterTag = "water";
        public const float MinInsideFraction = 0.03f;
        public const float MinEdgeLength = 15f;
        /// <summary>Depth of the land band handed to the shore feature (promenade, quay apron).</summary>
        public const float DefaultBand = 12f;
        public const string TooWetKey = "Placemaker.Shore.Issue.TooWet";

        public static bool IsWet(SiteContext site, float2 p) => site?.WaterDepthAt != null && site.WaterDepthAt(p) > Shoreline.WetDepth;

        /// <summary>Samples ~600 points inside and probes outward along the outline (1.5, 5, 12 m).</summary>
        public static WaterSurvey Survey(SiteContext site, LayerRegion region, Func<float2, float> height)
        {
            var s = new WaterSurvey { WaterLevel = float.NaN };
            if (site?.WaterDepthAt == null || region == null) return s;
            var depth = site.WaterDepthAt;
            height = height ?? (_ => 0f);
            float step = math.max(3f, math.sqrt(region.Area / 600f));
            double level = 0; int levels = 0;
            for (float y = region.Min.y + step * 0.5f; y < region.Max.y; y += step)
                for (float x = region.Min.x + step * 0.5f; x < region.Max.x; x += step)
                {
                    var p = new float2(x, y);
                    if (!region.Contains(p)) continue;
                    s.Samples++;
                    float d = depth(p);
                    if (d > Shoreline.WetDepth) { s.Wet++; level += height(p) + d; levels++; }
                }
            s.WetFraction = s.Samples > 0 ? (float)s.Wet / s.Samples : 0f;
            var pts = Poly.Resample(region.Ring, 4f, true);
            for (int i = 0; i < pts.Length; i++)
            {
                var a = pts[i]; var b = pts[(i + 1) % pts.Length];
                var dir = math.normalizesafe(b - a, new float2(1, 0));
                var outN = new float2(dir.y, -dir.x);
                var m = (a + b) * 0.5f;
                foreach (float reach in new[] { 1.5f, 5f, 12f })
                {
                    var q = m + outN * reach;
                    float d = depth(q);
                    if (d <= Shoreline.WetDepth) continue;
                    s.WetEdgeLength += math.distance(a, b);
                    level += height(q) + d; levels++;
                    break;
                }
            }
            if (levels > 0) s.WaterLevel = (float)(level / levels);
            return s;
        }

        /// <summary>
        /// Host hook for the "Shore" feature. When a child targets <see cref="Place"/> and the region
        /// touches water, the host's region becomes the dry land minus a shore band, and the band plus
        /// the water inside the outline become "place:shore" regions. Returns true when it carved.
        /// </summary>
        public static bool CarveShore(LayerContext ctx, float band = DefaultBand)
        {
            if (ctx?.Input == null || !ctx.Input.HasChildTarget(Place)) return false;
            var region = ctx.LayerRegion;
            var survey = Survey(ctx.Site, region, ctx.H);
            if (!survey.Any) return false;
            ctx.Check();
            int issuesBefore = ctx.Plan.Issues.Count;
            Shoreline shore;
            try { shore = Shoreline.Analyze(ctx, region, "auto"); }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { shore = null; }
            // The host only borrows the analysis: its notes are the feature's business.
            if (ctx.Plan.Issues.Count > issuesBefore) ctx.Plan.Issues.RemoveRange(issuesBefore, ctx.Plan.Issues.Count - issuesBefore);
            ctx.Plan.Facts.Remove("shoreLength");
            ctx.Plan.Facts.Remove("shoreMode");
            if (shore == null || shore.Mode == ShoreMode.Assumed) return false;

            float d = math.min(band, shore.MaxDepth * 0.4f);
            var host = shore.Land;
            if (d >= 4f)
            {
                var strips = shore.Band(0f, d);
                if (strips.Count > 0) host = Largest(Poly.Boolean(new[] { (IReadOnlyList<float2>)shore.Land.Ring }, ToList(strips), BoolOp.Difference)) ?? host;
            }
            if (host == null || host.Area < math.max(150f, region.Area * 0.08f))
            {
                ctx.Issue(IssueSeverity.Info, TooWetKey);
                return false;
            }
            var pieces = new List<float2[]>();
            try
            {
                foreach (var r in Poly.Boolean(new[] { (IReadOnlyList<float2>)region.Ring }, new[] { (IReadOnlyList<float2>)host.Ring }, BoolOp.Difference))
                    foreach (var ring in Poly.CutHoles(r, 0.5f))
                    {
                        var clean = Poly.Clean(ring, 0.3f);
                        if (clean.Length >= 3 && Poly.Area(clean) >= 40f && Poly.MeanWidth(clean) > 1.5f) pieces.Add(clean);
                    }
            }
            catch (Exception) { pieces.Clear(); }
            if (pieces.Count == 0) return false;
            ctx.LayerRegion = host;
            for (int i = 0; i < pieces.Count; i++) ctx.AddRegion(pieces[i], Place + ":" + i, Place, WaterTag);
            ctx.Plan.Facts["shorePlaces"] = pieces.Count;
            ctx.Plan.Facts["shoreBand"] = d;
            ctx.Plan.Facts["shoreWetShare"] = survey.WetFraction;
            return true;
        }

        /// <summary>The "Shore" feature a host lists (auto-activating: on by default, idle away from water).</summary>
        public static FeatureInfo Feature(params (string key, object value)[] preset)
        {
            var f = new FeatureInfo
            {
                Layer = LayerId, Target = Place, DefaultOn = true, LabelKey = "Placemaker.Feature.shore",
                TooltipKey = "Placemaker.Feature.shore.Tooltip",
            };
            foreach (var (k, v) in preset) f.Preset.Values[k] = v;
            return f;
        }

        // ───────────── small helpers ─────────────

        private static List<IReadOnlyList<float2>> ToList(List<float2[]> rings)
        {
            var r = new List<IReadOnlyList<float2>>();
            foreach (var x in rings) r.Add(x);
            return r;
        }

        internal static LayerRegion Largest(List<Region> regions)
        {
            LayerRegion best = null;
            foreach (var r in regions)
                foreach (var ring in Poly.CutHoles(r, 0.5f))
                {
                    var lr = LayerRegion.From(ring);
                    if (lr != null && (best == null || lr.Area > best.Area)) best = lr;
                }
            return best;
        }

        /// <summary>A net course at an explicit height (deck over water, quay on fill).</summary>
        public static PlannedNet Net(LayerContext ctx, float2 a, float2 b, float ya, float yb, NetRole role, AssetRef asset, bool follow, float arch = 0f)
        {
            var p1 = math.lerp(a, b, 1f / 3f); var p2 = math.lerp(a, b, 2f / 3f);
            var n = new PlannedNet
            {
                A = new float3(a.x, ya, a.y),
                B = new float3(p1.x, math.lerp(ya, yb, 1f / 3f) + arch, p1.y),
                C = new float3(p2.x, math.lerp(ya, yb, 2f / 3f) + arch, p2.y),
                D = new float3(b.x, yb, b.y),
                Role = role, Asset = asset, FollowTerrain = follow,
            };
            ctx.Plan.Nets.Add(n);
            return n;
        }

        /// <summary>Places an object at an explicit height when its disc is free; takes the space.</summary>
        public static bool PutAt(LayerContext ctx, Occupancy occ, float2 p, float y, float yaw, ObjectRole role, AssetRef asset, string category,
            float radius, uint variation, float margin = 0.3f, float gap = 0.2f, int kind = 1)
        {
            if (!occ.Free(p, radius, margin, gap)) return false;
            var o = ctx.Object(p, yaw, role, asset, variation, -1, string.IsNullOrEmpty(category) ? null : category);
            if (math.isfinite(y)) o.Position.y = y;
            occ.Take(p, radius, kind);
            return true;
        }

        public static float Yaw(float2 dir) => math.atan2(dir.x, dir.y);

        /// <summary>Clears ReservedSpace discs and polygons (not corridors) along a segment — for links that end on a path.</summary>
        public static bool LinkClear(ReservedSpace reserved, float2 a, float2 b, float r)
        {
            if (reserved == null) return true;
            float len = math.distance(a, b);
            int n = math.max(2, (int)math.ceil(len / 1.5f));
            for (int i = 1; i < n; i++)
            {
                var p = math.lerp(a, b, (float)i / n);
                foreach (var d in reserved.Discs)
                    if (math.distancesq(p, d.Center) < (r + d.Radius) * (r + d.Radius)) return false;
                foreach (var poly in reserved.Polygons)
                    if (ReservedSpace.PointInPolygon(p, poly)) return false;
            }
            return true;
        }

        /// <summary>Nearest point on a reserved walkable path (Pathway corridors), or false.</summary>
        public static bool NearestReservedPath(ReservedSpace reserved, float2 p, float cap, out float2 q)
        {
            q = p; float best = cap * cap; bool found = false;
            if (reserved == null) return false;
            foreach (var c in reserved.Corridors)
            {
                if (c.Tag != nameof(NetRole.Pathway) && c.Tag != nameof(NetRole.PathwayWide)) continue;
                var x = SegmentIndex.ClosestPoint(p, c.A, c.B);
                float d = math.distancesq(p, x);
                if (d < best) { best = d; q = x; found = true; }
            }
            return found;
        }
    }
}
