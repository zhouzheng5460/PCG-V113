// Shared building blocks of the monuments & follies (grand park wave, builder g4).
//
// Every monument is an amenity-style add-on (MonumentAddOn): a host (park, plaza) reserves its
// rectangle at the position the player chose and joins it to the paths ("Path to it"); hosts without
// places (memorial park, formal garden, recipes) let it find its own spot, shrinking the layout when
// the full footprint does not fit. The layouts are drawn in the rectangle's own frame so they stay
// symmetric — monuments must look designed, not scattered.
//
// Terrain (plinths, terraces) is emitted as non-overlapping cells / annulus pieces so the order the
// game applies them in never matters, always blended back to the ORIGINAL ground towards the edge of
// the monument's own rectangle (≤ 28 % slope) — the host's outline is further away still.

using System;
using System.Collections.Generic;
using Placemaker.Core.Geometry;
using Placemaker.Core.Layers.Common;
using Unity.Mathematics;

namespace Placemaker.Core.Layers.AddOns.Monuments
{
    public abstract class MonumentAddOn : AmenityAddOn
    {
        public const string K = "Placemaker.Monuments.";
        public override string Icon => "Media/Game/Icons/Unique.svg";

        private static readonly float[] Shrink = { 1f, 0.82f, 0.68f, 0.55f };

        protected override void PlanCore(LayerContext ctx)
        {
            var occ = Occupancy(ctx);
            OrientedRect site;
            var tags = ctx.Input.RegionTags;
            if (tags != null && Array.IndexOf(tags, Amenities.Place(Id)) >= 0) site = Amenities.RectOf(ctx.LayerRegion);
            else
            {
                var gates = new List<float2>();
                foreach (var m in ctx.Input.Markers) gates.Add(m.Position.xz);
                var size = Footprint(ctx);
                bool found = false;
                site = default;
                for (int i = 0; i < Shrink.Length && !found; i++)
                {
                    ctx.Check();
                    found = Amenities.TrySolve(ctx.LayerRegion, occ, size.x * Shrink[i], size.y * Shrink[i], Spec.DefaultAt, gates, ctx.Seed, null, out site, out _);
                    if (found && i > 0) ctx.Issue(IssueSeverity.Info, K + "Issue.Smaller", NameKey);
                }
                if (!found) { NoSpace(ctx, NameKey); return; }
            }
            var toward = Toward(occ, site);
            Build(ctx, occ, site, toward);
            ctx.Plan.Facts["amenityLength"] = site.HalfLength * 2f;
            ctx.Plan.Facts["amenityWidth"] = site.HalfWidth * 2f;
        }
    }

    public static class MonumentKit
    {
        public const float Slope = 0.28f;

        // ───────────── categories (graceful degradation) ─────────────

        // Art pass a6: vanilla has no statues (centerpiece.statue held clock hands); the old tail
        // "centerpiece" turned every statue into a fountain, scaled up to 4.5×. A figure now falls back to
        // a sculpture, and only then to a fountain (never scaled past life size, see IsFigure).
        public static readonly string[] StatueChain = { AssetCategories.CenterpieceStatue, AssetCategories.MemorialMonument, AssetCategories.MemorialSculpture, AssetCategories.MonumentSculpture, AssetCategories.CenterpieceFountain };
        /// <summary>Small repeated ornaments (statue rings, wings, corners): figures, else classical urns (plant pots).</summary>
        public static readonly string[] OrnamentChain = { AssetCategories.CenterpieceStatue, AssetCategories.MemorialSculpture, AssetCategories.Planter };
        /// <summary>A real figure (scales well); anything else is a stand-in kept near life size.</summary>
        public static bool IsFigure(string cat) => cat == AssetCategories.CenterpieceStatue || cat == AssetCategories.MemorialMonument || cat == AssetCategories.MemorialSculpture;
        /// <summary>Asset and role for an ornament of category <paramref name="cat"/> (urn-like pots when no figures are installed).</summary>
        public static AssetRef OrnamentAsset(string cat, out ObjectRole role)
        {
            role = cat == AssetCategories.Planter ? ObjectRole.Planter : ObjectRole.Centerpiece;
            return cat == AssetCategories.Planter ? AssetMix.One("", "PlantPot01", "FlowerPot01") : new AssetRef("");
        }
        /// <summary>Living-colonnade stand-in: Lombardy-like poplars (columnar), then birches.</summary>
        public static AssetRef ColumnTree => AssetMix.One("", "EU_PoplarTree01", "BirchTree01");
        public static readonly string[] SculptureChain = { AssetCategories.MonumentSculpture, AssetCategories.MemorialSculpture, AssetCategories.CenterpieceStatue, AssetCategories.Centerpiece };
        public static readonly string[] ObeliskChain = { AssetCategories.MonumentObelisk, AssetCategories.MemorialMonument, AssetCategories.CenterpieceStatue };

        /// <summary>First available category of the chain, silently (null when none). Explicit asset → the chain's head.</summary>
        public static string Quiet(string chosen, params string[] chain)
        {
            if (!string.IsNullOrEmpty(chosen)) return chain[0];
            foreach (var c in chain) if (AssetAvailability.Has(c)) return c;
            return null;
        }

        /// <summary>
        /// Column stand-in: real columns, else columnar trees (a "living colonnade"), else statues. Reports
        /// an Info issue when it had to fall back. Returns null when nothing at all is available.
        /// </summary>
        public static string Columns(LayerContext ctx, string chosen, out ObjectRole role)
        {
            role = ObjectRole.Furniture;
            if (!string.IsNullOrEmpty(chosen) || AssetAvailability.Has(AssetCategories.MonumentColumn)) return AssetCategories.MonumentColumn;
            ctx.Issue(IssueSeverity.Info, MonumentAddOn.K + "Issue.NoColumns");
            if (AssetAvailability.Has(AssetCategories.Tree)) { role = ObjectRole.Tree; return AssetCategories.Tree; }
            var statue = Quiet("", StatueChain);
            if (statue != null) { role = ObjectRole.Centerpiece; return statue; }
            return null;
        }

        // ───────────── placement ─────────────

        /// <summary>One object with a scale; checked against the occupancy and taken. Returns the object or null.</summary>
        public static PlannedObject Put(LayerContext ctx, Occupancy occ, float2 p, float yaw, ObjectRole role, AssetRef asset, string category,
            float radius, uint variation, float scale = 1f, float margin = 0.2f, float gap = 0.2f, int kind = 1)
        {
            if (!occ.Free(p, radius, margin, gap)) return null;
            int age = role == ObjectRole.Tree ? 2 : -1;
            var o = ctx.Object(p, yaw, role, asset, variation, age, category);
            o.Scale = scale;
            occ.Take(p, radius, kind);
            return o;
        }

        /// <summary>Where people arrive: the end of a reserved path touching the rectangle (the host's "Path to it"), else the edge point nearest <paramref name="toward"/>.</summary>
        public static float2 Entry(Occupancy occ, OrientedRect site, float2 toward, bool preferEnd = false)
        {
            var ring = site.Ring;
            float best = 1.2f; float2 hit = default; bool found = false;
            foreach (var s in occ.Paths.Segments)
                foreach (var e in new[] { s.A, s.B })
                {
                    float d = Poly.DistanceToBoundary(ring, e);
                    if (d < best) { best = d; hit = e; found = true; }
                }
            if (found) return hit;
            // Nobody arrives by path (standalone): linear layouts start at the end nearest the visitors.
            if (preferEnd) return LocalOf(site, toward).x >= 0f ? site.Local(site.HalfLength, 0f) : site.Local(-site.HalfLength, 0f);
            return ClosestOnRect(site, toward);
        }

        public static float2 ClosestOnRect(OrientedRect r, float2 p)
        {
            var l = new float2(math.dot(p - r.Center, r.Axis), math.dot(p - r.Center, r.Side));
            var c = new float2(math.clamp(l.x, -r.HalfLength, r.HalfLength), math.clamp(l.y, -r.HalfWidth, r.HalfWidth));
            // Inside: push to the nearest side.
            if (math.abs(l.x) <= r.HalfLength && math.abs(l.y) <= r.HalfWidth)
            {
                if (r.HalfLength - math.abs(l.x) < r.HalfWidth - math.abs(l.y)) c.x = math.sign(l.x == 0 ? 1 : l.x) * r.HalfLength;
                else c.y = math.sign(l.y == 0 ? 1 : l.y) * r.HalfWidth;
            }
            return r.Local(c.x, c.y);
        }

        /// <summary>True when a disc of radius <paramref name="margin"/> at p lies inside the rectangle.</summary>
        public static bool Inside(OrientedRect r, float2 p, float margin)
        {
            var l = LocalOf(r, p);
            return math.abs(l.x) <= r.HalfLength - margin && math.abs(l.y) <= r.HalfWidth - margin;
        }

        /// <summary>Yaw that faces direction <paramref name="dir"/> (0 = +Z).</summary>
        public static float Yaw(float2 dir) => math.lengthsq(dir) < 1e-8f ? 0f : math.atan2(dir.x, dir.y);

        /// <summary>Local (u along the axis, v across) of a world point.</summary>
        public static float2 LocalOf(OrientedRect r, float2 p) => new float2(math.dot(p - r.Center, r.Axis), math.dot(p - r.Center, r.Side));

        /// <summary>Straight path (indexed in the occupancy so objects keep off it).</summary>
        public static void Path(LayerContext ctx, Occupancy occ, float2 a, float2 b, NetRole role, AssetRef asset) =>
            LeisureKit.Path(ctx, occ, a, b, role, asset);

        // ───────────── ellipses ─────────────

        public static float2 Ellipse(float2 c, float2 axis, float a, float b, float t) =>
            c + axis * (a * math.cos(t)) + new float2(-axis.y, axis.x) * (b * math.sin(t));

        private static float2 EllipseD(float2 axis, float a, float b, float t) =>
            axis * (-a * math.sin(t)) + new float2(-axis.y, axis.x) * (b * math.cos(t));

        /// <summary>Bézier path along the ellipse from t0 to t1 (|t1−t0| ≤ 90° per piece; split otherwise). Ends are exact ellipse points so pieces share nodes.</summary>
        public static float ArcPath(LayerContext ctx, Occupancy occ, float2 c, float2 axis, float a, float b, float t0, float t1, NetRole role, AssetRef asset)
        {
            int pieces = math.max(1, (int)math.ceil(math.abs(t1 - t0) / (math.PI / 2f) - 1e-4f));
            float length = 0f;
            for (int i = 0; i < pieces; i++)
            {
                float s0 = math.lerp(t0, t1, i / (float)pieces), s1 = math.lerp(t0, t1, (i + 1) / (float)pieces);
                float k = 4f / 3f * math.tan((s1 - s0) / 4f);
                var p0 = Ellipse(c, axis, a, b, s0); var p3 = Ellipse(c, axis, a, b, s1);
                var p1 = p0 + EllipseD(axis, a, b, s0) * k; var p2 = p3 - EllipseD(axis, a, b, s1) * k;
                ctx.CurvedNet(p0, p1, p2, p3, role, asset);
                float hw = Composer.DefaultHalfWidth(role);
                var prev = p0;
                for (int j = 1; j <= 6; j++)
                {
                    var q = Ellipse(c, axis, a, b, math.lerp(s0, s1, j / 6f));
                    occ.TakeCorridor(prev, q, hw, 100);
                    occ.Paths.Add(prev, q, hw, 100);
                    length += math.distance(prev, q);
                    prev = q;
                }
            }
            return length;
        }

        /// <summary>Points on an ellipse ring (CCW polygon) — for surfaces.</summary>
        public static float2[] EllipseRing(float2 c, float2 axis, float a, float b, int sides = 32)
        {
            var r = new float2[sides];
            for (int i = 0; i < sides; i++) r[i] = Ellipse(c, axis, a, b, i * 2f * math.PI / sides);
            return r;
        }

        // ───────────── terrain ─────────────

        /// <summary>Mean ground height of points (sampled on a small grid round the centre).</summary>
        public static float Ref(LayerContext ctx, float2 c, float radius)
        {
            double sum = 0; int n = 0;
            for (int y = -2; y <= 2; y++)
                for (int x = -2; x <= 2; x++) { sum += ctx.H(c + new float2(x, y) * radius * 0.5f); n++; }
            return (float)(sum / n);
        }

        /// <summary>
        /// Terraces a patch: <paramref name="offset"/>(p) = target height relative to <paramref name="refH"/>
        /// (null = untouched), sampled per square cell, clamped by the slope towards the region outline.
        /// Emits one terraform piece per changed cell. Returns the cell count; adds the permanent warning.
        /// </summary>
        public static int Cells(LayerContext ctx, LayerRegion region, float2 min, float2 max, float cell, float refH, Func<float2, float?> offset)
        {
            min = math.max(min, region.Min); max = math.min(max, region.Max);
            int nx = math.max(1, (int)math.ceil((max.x - min.x) / cell)), ny = math.max(1, (int)math.ceil((max.y - min.y) / cell));
            if ((long)nx * ny > 6000) return 0;
            int cells = 0;
            for (int iy = 0; iy < ny; iy++)
            {
                if ((iy & 7) == 0) ctx.Check();
                for (int ix = 0; ix < nx; ix++)
                {
                    var c = min + (new float2(ix, iy) + 0.5f) * cell;
                    if (!region.Contains(c)) continue;
                    var o = offset(c);
                    if (!o.HasValue) continue;
                    float h0 = ctx.H(c);
                    float off = refH + o.Value - h0;
                    float d = math.max(0f, region.BoundaryDistance(c, math.abs(off) / Slope + cell + 1f) - cell * 0.71f);
                    off = math.clamp(off, -Slope * d, Slope * d);
                    if (math.abs(off) < 0.15f) continue;
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
                    ctx.Plan.Terraform.Add(new PlannedTerraform
                    {
                        Ring = Poly.To3D(ring, ctx.H), Kind = off > 0f ? TerraformKind.Raise : TerraformKind.Lower, TargetHeight = h0 + off, Permanent = true,
                    });
                    cells++;
                }
            }
            if (cells > 0) Permanent(ctx);
            return cells;
        }

        /// <summary>
        /// A stepped base (plinth): <paramref name="steps"/> nested regular polygons rising by <paramref name="rise"/>
        /// each, emitted as non-overlapping annulus pieces plus the top. Square = 4 sides aligned with
        /// <paramref name="axis"/>. Height is limited so the base never makes a cliff near the region outline.
        /// Returns the height of the top above the reference (0 when nothing was raised).
        /// </summary>
        public static float Plinth(LayerContext ctx, LayerRegion region, float2 c, float2 axis, float outerHalf, float topHalf, int steps, float rise, int sides)
        {
            steps = math.max(1, steps);
            float d = region.BoundaryDistance(c, outerHalf * 2f + 40f) - outerHalf * 1.42f;
            float maxTotal = math.max(0f, d * 0.45f);
            if (maxTotal < 0.3f) return 0f;
            rise = math.min(rise, maxTotal / steps);
            float refH = Ref(ctx, c, outerHalf);
            float phase = math.atan2(axis.y, axis.x) + (sides == 4 ? math.PI / 4f : 0f);
            float circ = sides == 4 ? math.SQRT2 : 1f / math.cos(math.PI / sides);
            var radii = new float[steps];
            for (int k = 0; k < steps; k++) radii[k] = (steps == 1 ? topHalf : math.lerp(outerHalf, topHalf, k / (float)(steps - 1))) * circ;
            for (int k = 0; k < steps; k++)
            {
                float target = refH + rise * (k + 1);
                if (k == steps - 1)
                {
                    Add(ctx, Regular(c, radii[k], sides, phase), target);
                    break;
                }
                var outer = Regular(c, radii[k], sides, phase); var inner = Regular(c, radii[k + 1], sides, phase);
                for (int i = 0; i < sides; i++)
                    Add(ctx, new[] { outer[i], outer[(i + 1) % sides], inner[(i + 1) % sides], inner[i] }, target);
            }
            // The inner pieces overlap nothing: the top piece is only the innermost polygon above.
            Permanent(ctx);
            return rise * steps;
        }

        private static void Add(LayerContext ctx, float2[] ring, float target)
        {
            ctx.Plan.Terraform.Add(new PlannedTerraform
            {
                Ring = Poly.To3D(Poly.EnsureCcw(ring), ctx.H), Kind = TerraformKind.Raise, TargetHeight = target, Permanent = true,
            });
        }

        public static float2[] Regular(float2 c, float r, int sides, float phase)
        {
            var p = new float2[sides];
            for (int i = 0; i < sides; i++)
            {
                float a = phase + i * 2f * math.PI / sides;
                p[i] = c + new float2(math.cos(a), math.sin(a)) * r;
            }
            return p;
        }

        public static void Permanent(LayerContext ctx)
        {
            foreach (var i in ctx.Plan.Issues) if (i.Key == TerrainShaper.PermanentKey) return;
            ctx.Issue(IssueSeverity.Warning, TerrainShaper.PermanentKey);
        }

        // ───────────── water ─────────────

        /// <summary>A pool: real water (a lowered basin with a water source, only on flat ground) or a water-look surface.</summary>
        public static bool Pool(LayerContext ctx, float2[] ring, bool real, string chosen)
        {
            if (real)
            {
                float lo = float.MaxValue, hi = float.MinValue;
                foreach (var q in ring) { float h = ctx.H(q); lo = math.min(lo, h); hi = math.max(hi, h); }
                if (hi - lo <= 0.6f)
                {
                    float level = lo - 0.2f;
                    var centre = Poly.Centroid(ring);
                    ctx.Plan.Terraform.Add(new PlannedTerraform
                    {
                        Ring = Poly.To3D(ring, ctx.H), Kind = TerraformKind.Lower, TargetHeight = level - 0.8f, Permanent = true,
                        WaterLevel = level, WaterAnchor = new float3(centre.x, level, centre.y),
                    });
                    Permanent(ctx);
                    ctx.Plan.Facts["realWater"] = 1;
                    return true;
                }
                ctx.Issue(IssueSeverity.Info, MonumentAddOn.K + "Issue.WaterNotFlat");
            }
            string cat = LeisureKit.Category(ctx, chosen, AssetCategories.SurfaceWater, AssetCategories.SurfacePaving);
            return cat != null && ctx.Surface(ring, SurfaceRole.Water, AssetMix.One(chosen), cat);
        }
    }
}

namespace Placemaker.Core.Layers.AddOns.Monuments
{
    /// <summary>The monuments as features of hosts without places (memorial park, formal garden): each finds its own spot.</summary>
    public static class MonumentFeatures
    {
        public static readonly string[] All = { BigStatueAddOn.LayerId, ObeliskAddOn.LayerId, TriumphalArchAddOn.LayerId, SculptureWalkAddOn.LayerId, TerraceGardenAddOn.LayerId, FollyAddOn.LayerId };

        /// <summary>The host's own features followed by the monuments.</summary>
        public static System.Collections.Generic.List<FeatureInfo> With(System.Collections.Generic.List<FeatureInfo> own)
        {
            own.AddRange(Own());
            return own;
        }

        public static System.Collections.Generic.IEnumerable<FeatureInfo> Own()
        {
            foreach (var id in All)
                yield return new FeatureInfo { Layer = id, LabelKey = "Placemaker.Feature." + id, TooltipKey = "Placemaker.Feature." + id + ".Tooltip" };
        }
    }
}
