// Ground patches feature (grand park wave, g3): designed patches of different ground in a park —
// dirt, mown grass, meadow, rocky area, gravel, sand. Each kind has its natural finish:
//
//   dirt     terrain paint (feathered), a few dirt decals          (finish "surface": + dirt surface core)
//   mown     a tidy oval of grass surface, the terrain under it un-painted to the base grass
//   meadow   a loose, widely feathered paint patch (tall-grass layer) with low wildflower bushes
//   rocky    paint + a rock group (one or two big stones, smaller ones around)
//   gravel   a gravel/natural surface core in a painted halo
//   sand     a sand surface core in a painted halo
//
// Blended borders: paint is laid as three concentric rings (outer 35 %, middle 70 %, core 100 %), and
// hard-edged surfaces sit INSIDE the painted halo, so no patch ends in a hard line on the lawn.
// Position ("arrange"): scattered over open ground, a drift along the long axis, along the edges, or a
// cluster of touching patches around one focal point. "Path to it" joins dirt/gravel/sand/mown patches
// to the nearest path. Trees may stand on patches (ground is painted under them); paths, places and
// Reserved never get covered. Missing surfaces/rocks/decals degrade to paint only with an Info issue.

using System;
using System.Collections.Generic;
using Placemaker.Core.Geometry;
using Placemaker.Core.Layers.Common;
using Unity.Mathematics;

namespace Placemaker.Core.Layers.AddOns.Ground
{
    [AreaGenerator(147)]
    public sealed class GroundPatchesAddOn : AddOnLayer
    {
        public const string LayerId = "ground";
        public static readonly string[] Kinds = { "meadow", "mown", "rocky", "dirt", "gravel", "sand" };

        protected override string Area => "AddOn.Ground";
        public override string Id => LayerId;
        public override string Icon => "Media/Game/Icons/Terraforming/TextureDirt.svg";

        protected override void DefineControls(ControlSet c)
        {
            c.Slider("dirt", 1, 0, 6, 1, "count", ControlTier.Essential, "patches").Tip();
            c.Slider("mown", 1, 0, 6, 1, "count", ControlTier.Essential, "patches").Tip();
            c.Slider("meadow", 1, 0, 6, 1, "count", ControlTier.Essential, "patches").Tip();
            c.Slider("rocky", 0, 0, 6, 1, "count", ControlTier.Essential, "patches").Tip();
            c.Slider("gravel", 0, 0, 6, 1, "count", ControlTier.Essential, "patches").Tip();
            c.Slider("sand", 0, 0, 6, 1, "count", ControlTier.Essential, "patches").Tip();
            c.Choice("size", "medium", ControlTier.Essential, "patches", "small", "medium", "large");
            c.Choice("arrange", "scattered", ControlTier.Essential, "patches", "scattered", "drift", "edges", "cluster").Tip();

            c.Choice("finish", "auto", ControlTier.More, "look", "auto", "paint", "surface").Tip();
            c.Slider("blend", 3, 1, 8, 0.5, "m", ControlTier.More, "look").Tip();
            c.Slider("roughness", 25, 0, 50, 5, "%", ControlTier.More, "look").Tip();
            c.Toggle("pathTo", false, ControlTier.More, "look").Tip();

            c.Toggle("rocks", true, ControlTier.More, "dressing").Tip();
            c.Toggle("decals", true, ControlTier.More, "dressing").Tip();
            c.Toggle("wildflowers", true, ControlTier.More, "dressing").Tip();

            c.Slider("dirtLayer", 1, 1, 4, 1, "count", ControlTier.More, "layers").Tip();
            c.Slider("meadowLayer", 2, 1, 4, 1, "count", ControlTier.More, "layers");
            c.Slider("rockyLayer", 3, 1, 4, 1, "count", ControlTier.More, "layers");
            c.Slider("gravelLayer", 1, 1, 4, 1, "count", ControlTier.More, "layers");
            c.Slider("sandLayer", 4, 1, 4, 1, "count", ControlTier.More, "layers");

            c.Asset("dirtSurface", AssetCategories.SurfaceDirt, ControlTier.More, "assets");
            c.Asset("grassSurface", AssetCategories.SurfaceGrass, ControlTier.More, "assets");
            c.Asset("gravelSurface", AssetCategories.SurfaceNatural, ControlTier.More, "assets");
            c.Asset("sandSurface", AssetCategories.SurfaceSand, ControlTier.More, "assets");
            c.AssetMulti("rockAssets", AssetCategories.Rock, ControlTier.More, "assets");

            foreach (var k in Kinds) c.RandomRange(k, 0, 2);
            foreach (var id in new[] { "dirtLayer", "meadowLayer", "rockyLayer", "gravelLayer", "sandLayer", "blend", "finish" }) c.Fixed(id);
        }

        protected override void DefinePresets()
        {
            Preset("mixed", ("dirt", 1.0), ("mown", 1.0), ("meadow", 1.0), ("rocky", 1.0), ("gravel", 0.0), ("sand", 0.0), ("arrange", "scattered"));
            Preset("meadowMown", ("dirt", 0.0), ("mown", 2.0), ("meadow", 3.0), ("rocky", 0.0), ("gravel", 0.0), ("sand", 0.0),
                ("arrange", "drift"), ("size", "large"), ("blend", 5.0));
            Preset("rockGarden", ("dirt", 0.0), ("mown", 0.0), ("meadow", 1.0), ("rocky", 3.0), ("gravel", 2.0), ("sand", 0.0),
                ("arrange", "cluster"), ("roughness", 35.0));
            Preset("sandGravel", ("dirt", 0.0), ("mown", 1.0), ("meadow", 0.0), ("rocky", 0.0), ("gravel", 2.0), ("sand", 2.0),
                ("arrange", "scattered"), ("roughness", 10.0), ("pathTo", true), ("finish", "surface"));
            Preset("wornLawn", ("dirt", 4.0), ("mown", 0.0), ("meadow", 0.0), ("rocky", 0.0), ("gravel", 1.0), ("sand", 0.0),
                ("arrange", "edges"), ("size", "small"), ("roughness", 45.0));
        }

        private struct Patch { public string Kind; public float2 C; public float R; public uint Seed; }

        protected override void PlanCore(LayerContext ctx)
        {
            var region = ctx.LayerRegion;
            var kinds = Sequence(ctx);
            if (kinds.Count == 0) { ctx.Issue(IssueSeverity.Info, "Placemaker.AddOn.Ground.Issue.NothingChosen"); return; }
            // Placement ignores trees and bushes (ground is painted under them); everything else blocks.
            var groundReserved = ctx.Input.Reserved?.Clone() ?? new ReservedSpace();
            groundReserved.Discs.RemoveAll(d => d.Tag == nameof(ObjectRole.Tree) || d.Tag == nameof(ObjectRole.Bush));
            var place = new Occupancy(region, groundReserved, ctx.Site);
            var occ = Occupancy(ctx);
            float blend = ctx.F("blend");
            float rough = ctx.F("roughness") / 100f;
            float baseR = ctx.S("size") == "small" ? 5f : ctx.S("size") == "large" ? 15f : 9f;
            baseR = math.min(baseR, math.sqrt(region.Area) * 0.22f);
            var patches = Arrange(ctx, place, kinds, baseR, blend, rough);
            ctx.Check();

            float painted = 0f;
            int made = 0;
            foreach (var p in patches)
            {
                EmitPatch(ctx, occ, p, blend, rough);
                place.Take(p.C, p.R, 3);
                painted += math.PI * p.R * p.R;
                made++;
            }
            if (ctx.B("pathTo")) PathsTo(ctx, occ, patches);
            if (made == 0) NoSpace(ctx, "Placemaker.AddOn.Ground.Name");
            else if (made < kinds.Count) ctx.Issue(IssueSeverity.Info, "Placemaker.AddOn.Ground.Issue.Fewer", made, kinds.Count);
            ctx.Plan.Facts["groundPatches"] = made;
            ctx.Stat("Placemaker.Stat.GroundPatches", made, Keys.UnitCount);
            ctx.Stat("Placemaker.Stat.GroundArea", Math.Round(painted), Keys.UnitM2);
        }

        /// <summary>Kinds to place, interleaved so neighbours differ (meadow, mown, rocky, dirt, gravel, sand, meadow…).</summary>
        private static List<string> Sequence(LayerContext ctx)
        {
            var left = new Dictionary<string, int>();
            int total = 0;
            foreach (var k in Kinds) { left[k] = math.max(0, ctx.I(k)); total += left[k]; }
            var r = new List<string>();
            while (r.Count < total)
                foreach (var k in Kinds)
                    if (left[k] > 0) { r.Add(k); left[k]--; }
            return r;
        }

        private static float KindScale(string kind)
        {
            switch (kind)
            {
                case "meadow": return 1.5f;
                case "mown": return 1.25f;
                case "rocky": return 0.8f;
                case "gravel": return 0.8f;
                case "sand": return 0.9f;
                default: return 0.7f;
            }
        }

        // ───────────────────────── position ─────────────────────────

        private static List<Patch> Arrange(LayerContext ctx, Occupancy place, List<string> kinds, float baseR, float blend, float rough)
        {
            var region = ctx.LayerRegion;
            string arrange = ctx.S("arrange");
            var result = new List<Patch>();
            var taken = new List<float2>();
            LeisureKit.Frame(region, out var centre, out var axis, out var side, out float major, out float minor);
            float2 focal = centre;
            bool haveFocal = false;
            for (int i = 0; i < kinds.Count; i++)
            {
                string kind = kinds[i];
                uint seed = Hashing.Mix(ctx.Seed, 0x6A0Du + (uint)i * 131u);
                bool ok = false;
                // Try the full size, then smaller.
                foreach (float shrink in new[] { 1f, 0.7f, 0.5f })
                {
                    float r = math.max(2.5f, baseR * KindScale(kind) * shrink);
                    // The core (surface, full paint) stays clear of everything; the soft outer ring may fade over a path edge.
                    float core = r * (1f + rough);
                    float margin = r * (1f + math.min(0.5f, rough * 1.3f)) + blend * 0.75f + 0.5f;
                    float2 spot = default;
                    switch (arrange)
                    {
                        case "drift":
                        {
                            // Evenly along the long axis, alternating sides: a designed sweep through the park.
                            float u = kinds.Count == 1 ? 0.5f : (i + 0.5f) / kinds.Count;
                            var basePt = centre + axis * (u - 0.5f) * major * 0.8f + side * minor * 0.18f * ((i & 1) == 0 ? 1f : -1f);
                            ok = TryNear(place, basePt, r, core, margin, seed, out spot);
                            break;
                        }
                        case "cluster":
                        {
                            if (!haveFocal)
                            {
                                ok = Spots.Find(place, core, Spots.Preference.Centre, seed, out spot, taken, 0f, margin);
                                if (ok) { focal = spot; haveFocal = true; }
                                break;
                            }
                            // Touching the patches already placed, on a ring around the focal patch.
                            float ring = (result[0].R + r) * (1f + rough) + 0.3f + (result.Count > 3 ? r * 1.6f : 0f);
                            for (int a = 0; a < 16 && !ok; a++)
                            {
                                float ang = Hashing.Unit(seed) * 6.283f + a * 6.283f / 16f;
                                var q = focal + new float2(math.cos(ang), math.sin(ang)) * ring;
                                if (place.Free(q, core, margin, 0.1f) && FarFrom(result, q, r, 0.75f)) { spot = q; ok = true; }
                            }
                            break;
                        }
                        case "edges":
                            ok = Spots.Find(place, core, Spots.Preference.NearBoundary, seed, out spot, taken, r * 2.2f, margin);
                            break;
                        default:
                            ok = Spots.Find(place, core, Spots.Preference.Open, seed, out spot, taken, r * 2.5f, margin);
                            break;
                    }
                    if (ok && !FarFrom(result, spot, r, arrange == "cluster" ? 0.75f : 1.1f)) ok = false;
                    if (!ok) continue;
                    result.Add(new Patch { Kind = kind, C = spot, R = r, Seed = seed });
                    taken.Add(spot);
                    place.Take(spot, core, 3);
                    break;
                }
            }
            return result;
        }

        private static bool TryNear(Occupancy place, float2 p, float r, float core, float margin, uint seed, out float2 spot)
        {
            for (int k = 0; k < 14; k++)
            {
                float a = Hashing.Unit(Hashing.Mix(seed, (uint)k)) * 6.283f;
                var q = k == 0 ? p : p + new float2(math.cos(a), math.sin(a)) * r * 0.4f * k;
                if (place.Free(q, core, margin, 0.3f)) { spot = q; return true; }
            }
            spot = p;
            return false;
        }

        private static bool FarFrom(List<Patch> placed, float2 p, float r, float factor)
        {
            foreach (var q in placed) if (math.distance(q.C, p) < (q.R + r) * factor) return false;
            return true;
        }

        // ───────────────────────── emission ─────────────────────────

        private void EmitPatch(LayerContext ctx, Occupancy occ, Patch p, float blend, float rough)
        {
            string finish = ctx.S("finish");
            switch (p.Kind)
            {
                case "mown":
                {
                    // Tidy oval: a grass surface with a clean edge, the terrain under it brought back to base grass.
                    var oval = Ellipse(p.C, p.R, p.R * 0.72f, Hashing.Unit(p.Seed) * math.PI, p.Seed, rough * 0.25f);
                    Feather(ctx, p, 0, blend * 0.6f, rough * 0.25f, oval);
                    if (finish != "paint") SurfaceCore(ctx, Ellipse(p.C, p.R - blend * 0.3f, (p.R - blend * 0.3f) * 0.72f, Hashing.Unit(p.Seed) * math.PI, p.Seed, rough * 0.25f),
                        SurfaceRole.Grass, ctx.S("grassSurface"), AssetCategories.SurfaceGrass);
                    break;
                }
                case "meadow":
                {
                    Feather(ctx, p, ctx.I("meadowLayer"), blend * 1.5f, math.min(0.5f, rough * 1.3f), null);
                    if (ctx.B("wildflowers")) Wildflowers(ctx, occ, p);
                    break;
                }
                case "rocky":
                {
                    Feather(ctx, p, ctx.I("rockyLayer"), blend, rough, null);
                    if (finish == "surface") SurfaceCore(ctx, Blob(p, p.R - blend * 0.5f, rough), SurfaceRole.Ground, ctx.S("gravelSurface"), AssetCategories.SurfaceNatural);
                    if (ctx.B("rocks")) Rocks(ctx, occ, p);
                    break;
                }
                case "gravel":
                {
                    float rr = rough * 0.5f;
                    Feather(ctx, p, ctx.I("gravelLayer"), blend, rr, null);
                    if (finish != "paint") SurfaceCore(ctx, Blob(p, p.R - blend * 0.4f, rr), SurfaceRole.Ground, ctx.S("gravelSurface"), AssetCategories.SurfaceNatural);
                    break;
                }
                case "sand":
                {
                    float rr = rough * 0.6f;
                    Feather(ctx, p, ctx.I("sandLayer"), blend, rr, null);
                    if (finish != "paint") SurfaceCore(ctx, Blob(p, p.R - blend * 0.4f, rr), SurfaceRole.Ground, ctx.S("sandSurface"), AssetCategories.SurfaceSand, AssetCategories.SurfaceNatural);
                    break;
                }
                default:   // dirt
                {
                    Feather(ctx, p, ctx.I("dirtLayer"), blend, rough, null);
                    if (finish == "surface") SurfaceCore(ctx, Blob(p, p.R - blend * 0.5f, rough), SurfaceRole.Ground, ctx.S("dirtSurface"), AssetCategories.SurfaceDirt, AssetCategories.SurfaceNatural);
                    if (ctx.B("decals")) DirtDecals(ctx, p);
                    break;
                }
            }
        }

        private static float2[] Blob(Patch p, float r, float rough) => LeisureKit.Blob(p.C, math.max(1f, r), p.Seed, 18, rough);

        private static float2[] Ellipse(float2 c, float rx, float ry, float angle, uint seed, float jitter)
        {
            const int sides = 20;
            var pts = new float2[sides];
            var ax = new float2(math.cos(angle), math.sin(angle)); var ay = new float2(-ax.y, ax.x);
            for (int i = 0; i < sides; i++)
            {
                float a = i * 2f * math.PI / sides;
                float k = 1f - jitter + 2f * jitter * Hashing.Unit(Hashing.Mix(seed, (uint)i + 11u));
                pts[i] = c + (ax * math.cos(a) * rx + ay * math.sin(a) * ry) * k;
            }
            return pts;
        }

        /// <summary>
        /// Three concentric paint rings (35 / 70 / 100 %) so the border fades into the lawn. Layer 0 erases
        /// the map's extra textures (base grass). <paramref name="shape"/> overrides the blob outline (scaled).
        /// </summary>
        private static void Feather(LayerContext ctx, Patch p, int layer, float blend, float rough, float2[] shape)
        {
            layer = math.clamp(layer, 0, 4);
            var rings = new (float r, float strength)[] { (p.R + blend * 0.5f, 0.35f), (p.R - blend * 0.25f, 0.7f), (p.R - blend, 1f) };
            foreach (var (r, strength) in rings)
            {
                if (r < 1f) continue;
                float2[] ring;
                if (shape != null)
                {
                    float k = r / p.R;
                    ring = new float2[shape.Length];
                    for (int i = 0; i < shape.Length; i++) ring[i] = p.C + (shape[i] - p.C) * k;
                }
                else ring = LeisureKit.Blob(p.C, r, p.Seed, 18, rough);
                var clean = Poly.Clean(ring, 0.05f);
                if (clean.Length < 3 || !Poly.IsSimple(clean)) continue;
                ctx.Plan.Paint.Add(new PlannedPaint { Ring = Poly.To3D(clean, ctx.H), Layer = layer, Strength = strength });
            }
            bool warned = false;
            foreach (var i in ctx.Plan.Issues) if (i.Key == TerrainPaint.PermanentKey) { warned = true; break; }
            if (!warned) ctx.Issue(IssueSeverity.Warning, TerrainPaint.PermanentKey);
        }

        /// <summary>A hard-edged surface inside the painted halo; skipped (paint only) when no surface is installed.</summary>
        private static void SurfaceCore(LayerContext ctx, float2[] ring, SurfaceRole role, string chosen, params string[] chain)
        {
            string cat = LeisureKit.Category(ctx, chosen, chain);
            if (cat == null) return;
            var clean = Poly.Clean(ring, 0.05f);
            if (clean.Length < 3 || !Poly.IsSimple(clean)) return;
            ctx.Surface(Poly.EnsureCcw(clean), role, AssetMix.One(chosen), cat);
        }

        private static void Rocks(LayerContext ctx, Occupancy occ, Patch p)
        {
            var ids = ctx.L("rockAssets");
            if (!AssetAvailability.Require(ctx.Plan, AssetCategories.Rock, ids, Keys.Category(AssetCategories.Rock))) return;
            float rockR = AssetMix.MaxFootprint(ctx.Site, ids, 1.2f);
            // One or two big stones off-centre, then smaller ones on a golden-angle spiral, smaller towards the rim.
            const float golden = 2.39996323f;
            int n = math.clamp((int)(p.R * 1.2f), 4, 16);
            for (int i = 0; i < n; i++)
            {
                uint v = Hashing.Mix(p.Seed, (uint)i + 400u);
                float rr = p.R * 0.75f * math.sqrt((i + 0.3f) / n);
                float a = i * golden + Hashing.Unit(p.Seed) * 6.283f;
                var q = p.C + new float2(math.cos(a), math.sin(a)) * rr;
                float scale = i < 2 ? 1.4f + 0.5f * Hashing.Unit(v) : 0.5f + 0.7f * Hashing.Unit(v) * (1f - rr / p.R);
                if (!occ.Free(q, rockR * scale, 0.5f, 0.1f)) continue;
                var o = ctx.Object(q, Hashing.Unit(Hashing.Mix(v, 5u)) * 6.283f, ObjectRole.Furniture, AssetMix.Pick(ids, v), v, -1, AssetCategories.Rock);
                o.Scale = scale;
                occ.Take(q, rockR * scale, 2);
            }
        }

        private static void Wildflowers(LayerContext ctx, Occupancy occ, Patch p)
        {
            if (!AssetAvailability.Has(AssetCategories.Bush)) return;
            int n = math.clamp((int)(math.PI * p.R * p.R / 55f), 3, 16);
            for (int i = 0; i < n * 3 && n > 0; i++)
            {
                uint v = Hashing.Mix(p.Seed, (uint)i + 800u);
                float a = Hashing.Unit(v) * 6.283f;
                float rr = p.R * 0.85f * math.sqrt(Hashing.Unit(Hashing.Mix(v, 1u)));
                var q = p.C + new float2(math.cos(a), math.sin(a)) * rr;
                if (!occ.Free(q, 1.1f, 0.5f, 0.2f)) continue;
                var o = ctx.Object(q, Hashing.Unit(Hashing.Mix(v, 2u)) * 6.283f, ObjectRole.Bush, AssetMix.One(""), v);
                o.Scale = 0.55f + 0.3f * Hashing.Unit(Hashing.Mix(v, 3u));
                occ.Take(q, 1.1f, 1);
                n--;
            }
        }

        private static void DirtDecals(LayerContext ctx, Patch p)
        {
            string cat = AssetAvailability.Has(AssetCategories.DecalDirt) ? AssetCategories.DecalDirt
                : AssetAvailability.Has(AssetCategories.Decal) ? AssetCategories.Decal : null;
            if (cat == null) return;
            int n = math.clamp((int)(math.PI * p.R * p.R / 30f), 2, 10);
            for (int i = 0; i < n; i++)
            {
                uint v = Hashing.Mix(p.Seed, (uint)i + 1200u);
                float a = Hashing.Unit(v) * 6.283f;
                float rr = p.R * 0.7f * math.sqrt(Hashing.Unit(Hashing.Mix(v, 1u)));
                var q = p.C + new float2(math.cos(a), math.sin(a)) * rr;
                ctx.Object(q, Hashing.Unit(Hashing.Mix(v, 2u)) * 6.283f, ObjectRole.Decal, AssetMix.One(""), v, -1, cat);
            }
        }

        /// <summary>"Path to it": a narrow path from the nearest existing path to the rim of each walkable patch.</summary>
        private static void PathsTo(LayerContext ctx, Occupancy occ, List<Patch> patches)
        {
            int made = 0;
            foreach (var p in patches)
            {
                if (p.Kind == "meadow" || p.Kind == "rocky") continue;
                if (occ.Paths.Nearest(p.C, p.R + 30f, out var q) < 0) continue;
                float d = math.distance(q, p.C);
                if (d <= p.R + 1f) continue;   // already beside a path
                var dir = (p.C - q) / d;
                var rim = p.C - dir * (p.R * 0.6f);
                // Start on the path's edge, not its centreline.
                var start = q + dir * 0.1f;
                bool ok = true;
                float len = math.distance(start, rim);
                int k = math.max(2, (int)(len / 1.5f));
                for (int i = 1; i < k && ok; i++)
                {
                    var s = math.lerp(start, rim, i / (float)k);
                    if (!ctx.LayerRegion.ContainsDisc(s, 1.5f) || occ.Discs.Overlaps(s, 1f) || occ.HitsPolygon(s, 1f)) ok = false;
                }
                if (!ok) continue;
                ctx.StraightNet(start, rim, NetRole.Pathway, AssetMix.One(""));
                occ.TakeCorridor(start, rim, Composer.DefaultHalfWidth(NetRole.Pathway), 100);
                made++;
            }
            if (made > 0) ctx.Plan.Facts["groundPaths"] = made;
        }
    }
}
