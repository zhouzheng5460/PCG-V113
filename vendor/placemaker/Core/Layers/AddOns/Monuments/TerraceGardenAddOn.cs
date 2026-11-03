// Terrace garden: a circle or oval of concentric terraces round a central statue or fountain.
//
//   levels     2–4 treads stepping down to the centre (sunken), up to it (raised: a garden mount) or
//              flat (a parterre); the outermost tread is at ground level, so the garden is blended at
//              its rim by construction, and the cells are clamped towards the outline anyway
//   spokes     4–8 radial walks from the inner ring to the outer ring (stairs where they cross the
//              steps); the entrance spoke runs on to where people arrive
//   oval rings walks on the innermost and the outermost tread, as ellipse arcs between the spokes, so
//              every crossing is a shared node
//   planting   clipped hedges along the step edges (gaps at the spokes), flowerbeds on the middle
//              treads, a ring of trees outside the rim, lamps at the outer junctions, benches facing in
//   mini amphitheatre — one sector (opposite the entrance) is carved into finer rows instead of
//              terraces: benches on arcs facing a small stage at the foot, the centrepiece behind it
//
// The terrace logic follows the amphitheatre type's bowl (rows as level cells relative to a reference
// height, clamped by slope towards the outline) but on elliptical radii.

using System.Collections.Generic;
using Placemaker.Core.Layers.Common;
using Unity.Mathematics;

namespace Placemaker.Core.Layers.AddOns.Monuments
{
    [AreaGenerator(176)]
    public sealed class TerraceGardenAddOn : MonumentAddOn
    {
        public const string LayerId = "terraceGarden";
        protected override string Area => "AddOn.TerraceGarden";
        public override string Id => LayerId;
        private const float RowDepth = 2.5f, Cell = 2.5f;

        protected override void DefineControls(ControlSet c)
        {
            c.Choice("shape", "oval", ControlTier.Essential, "terraces", "oval", "circle");
            c.Choice("direction", "sunken", ControlTier.Essential, "terraces", "sunken", "raised", "flat").Tip();
            c.Slider("levels", 3, 2, 4, 1, "count", ControlTier.Essential, "terraces");
            c.Slider("levelHeight", 0.8, 0.3, 1.5, 0.1, "m", ControlTier.More, "terraces");
            c.Slider("spokes", 6, 4, 8, 1, "count", ControlTier.Essential, "terraces").Tip();
            c.Toggle("rings", true, ControlTier.More, "terraces").Tip();
            c.Choice("centre", "statue", ControlTier.Essential, "centre", "statue", "fountain");
            c.Slider("size", 200, 100, 400, 25, "%", ControlTier.More, "centre").Tip();
            c.Asset("centreAsset", AssetCategories.Centerpiece, ControlTier.More, "centre");
            c.Toggle("amphitheatre", true, ControlTier.Essential, "amphitheatre").Tip();
            c.Toggle("stage", true, ControlTier.More, "amphitheatre").When("amphitheatre=true");
            c.Toggle("hedges", true, ControlTier.More, "planting");
            c.Asset("hedgeAsset", AssetCategories.Hedge, ControlTier.More, "planting").When("hedges=true");
            c.Toggle("flowers", true, ControlTier.More, "planting");
            c.Toggle("trees", true, ControlTier.More, "planting");
            c.Toggle("lamps", true, ControlTier.More, "planting");
            c.Asset("pavingAsset", AssetCategories.SurfacePaving, ControlTier.More, "ground");
            c.Asset("pathAsset", AssetCategories.Pathway, ControlTier.More, "ground");
            c.Chance("amphitheatre", 0.6f);
        }

        protected override void DefinePresets()
        {
            Preset("italian", ("shape", "oval"), ("direction", "sunken"), ("levels", 3.0), ("spokes", 6.0), ("centre", "statue"), ("amphitheatre", true));
            Preset("theatre", ("shape", "circle"), ("direction", "sunken"), ("levels", 4.0), ("levelHeight", 0.7), ("spokes", 4.0), ("centre", "fountain"), ("amphitheatre", true));
            Preset("mount", ("shape", "circle"), ("direction", "raised"), ("levels", 3.0), ("levelHeight", 1.0), ("spokes", 4.0), ("centre", "statue"), ("size", 300.0), ("amphitheatre", false));
            Preset("parterre", ("shape", "oval"), ("direction", "flat"), ("levels", 3.0), ("spokes", 8.0), ("centre", "fountain"), ("amphitheatre", false), ("trees", false));
        }

        private sealed class Garden
        {
            public float2 C, Lat, Fw;
            public float A, B, Rho0;
            public int Levels;
            public float H, Sign;              // level height, −1 sunken / +1 raised / 0 flat
            public float[] Spokes;             // ellipse parameters, sorted, entrance first
            public bool Amphi;
            public float S0, S1;               // amphitheatre sector (parameters, S0 < S1)
            public float RhoStage, RowRise; public int Rows;

            public float2 At(float rho, float t) => C + Lat * (A * rho * math.cos(t)) + Fw * (B * rho * math.sin(t));

            public void Polar(float2 p, out float rho, out float t)
            {
                var d = p - C;
                float u = math.dot(d, Lat) / A, v = math.dot(d, Fw) / B;
                rho = math.sqrt(u * u + v * v);
                t = math.atan2(v, u);
            }

            public float Edge(int k) => Rho0 + (1f - Rho0) * k / Levels;          // step line k (0 = plaza edge, Levels = rim)
            public float Mid(int k) => (Edge(k - 1) + Edge(k)) * 0.5f;           // tread k (1 = innermost)

            public bool InSector(float t, float margin = 0f)
            {
                if (!Amphi) return false;
                float x = t;
                while (x < S0 - margin) x += 2f * math.PI;
                return x <= S1 + margin;
            }

            /// <summary>Level offset (relative to the reference) at p, null outside the garden.</summary>
            public float? Offset(float2 p)
            {
                Polar(p, out float rho, out float t);
                if (rho > 1f) return null;
                if (Sign == 0f) return null;
                float total = Levels * H;
                if (Amphi && Sign < 0f && InSector(t) && rho >= RhoStage)
                {
                    int j = (int)math.floor((rho - RhoStage) / ((1f - RhoStage) / Rows));
                    return -total + math.min(Rows, j + 1) * RowRise;
                }
                if (rho <= Rho0) return Sign * total;
                int k = math.clamp((int)math.ceil((rho - Rho0) / ((1f - Rho0) / Levels)), 1, Levels);
                return Sign * (Levels - k) * H;
            }
        }

        protected override void Build(LayerContext ctx, Occupancy occ, OrientedRect site, float2 toward)
        {
            var region = ctx.LayerRegion;
            var entry = MonumentKit.Entry(occ, site, toward);
            const float margin = 3f;
            float A = site.HalfLength - margin, B = site.HalfWidth - margin;
            if (ctx.S("shape") == "circle") A = B = math.min(A, B);
            if (B < 9f) { NoSpace(ctx, NameKey); return; }
            var g = new Garden
            {
                C = site.Center, Lat = site.Axis, Fw = site.Side, A = A, B = B,
                Levels = ctx.I("levels"), H = ctx.F("levelHeight"),
                Sign = ctx.S("direction") == "sunken" ? -1f : ctx.S("direction") == "raised" ? 1f : 0f,
            };
            float plazaR = math.max(5f, B * 0.24f) + ctx.F("size") / 100f * 0.6f;
            g.Rho0 = math.min(0.42f, plazaR / B);
            // Steps no deeper than the treads are wide (a terrace, not a cliff).
            float tread = (1f - g.Rho0) * B / g.Levels;
            if (g.Sign != 0f) g.H = math.min(g.H, tread * 0.25f);

            // Spokes: one points at the entrance; the rest evenly round.
            int spokes = ctx.I("spokes");
            g.Polar(entry, out _, out float te);
            g.Spokes = new float[spokes];
            for (int i = 0; i < spokes; i++) g.Spokes[i] = Wrap(te + i * 2f * math.PI / spokes);

            // Amphitheatre sector: between the two spokes opposite the entrance.
            bool wantAmphi = ctx.B("amphitheatre");
            if (wantAmphi && g.Sign > 0f) { ctx.Issue(IssueSeverity.Info, K + "Issue.AmphiNeedsSunken"); wantAmphi = false; }
            if (wantAmphi)
            {
                // Centred opposite the entrance: odd counts take the gap there; even counts take the two gaps
                // either side of the opposite spoke, which becomes the central aisle.
                float step = 2f * math.PI / spokes;
                bool even = spokes % 2 == 0;
                float a0 = te + (even ? spokes / 2 - 1 : (spokes - 1) / 2) * step;
                g.S0 = Wrap(a0); g.S1 = g.S0 + (even ? 2f : 1f) * step;
                g.RhoStage = g.Rho0 + 3.5f / B;
                g.Rows = math.max(2, (int)math.floor((1f - g.RhoStage) * B / RowDepth));
                g.RowRise = g.Levels * g.H / g.Rows;
                g.Amphi = g.RhoStage < 0.8f;
            }
            ctx.Plan.Facts["terraceLevels"] = g.Sign == 0f ? 0 : g.Levels;

            // 1. Terrain.
            if (g.Sign != 0f)
            {
                float refH = MonumentKit.Ref(ctx, g.C, B * 0.8f);
                var reach = new float2(A, A) + 1f;
                int cells = MonumentKit.Cells(ctx, region, g.C - reach, g.C + reach, Cell, refH, g.Offset);
                ctx.Plan.Facts["terrainCells"] = cells;
            }
            ctx.Check();

            // 2. Ground: the central plaza (and the stage) paved.
            var paving = AssetMix.One(ctx.S("pavingAsset"));
            ctx.Surface(MonumentKit.EllipseRing(g.C, g.Lat, A * g.Rho0, B * g.Rho0, 32), SurfaceRole.Paving, paving, AssetCategories.SurfacePaving);
            if (g.Amphi && ctx.B("stage"))
            {
                var stage = new List<float2> ();
                for (int i = 0; i <= 8; i++) stage.Add(g.At(g.RhoStage - 0.3f / B, math.lerp(g.S0 + 0.12f, g.S1 - 0.12f, i / 8f)));
                for (int i = 8; i >= 0; i--) stage.Add(g.At(g.Rho0, math.lerp(g.S0 + 0.12f, g.S1 - 0.12f, i / 8f)));
                ctx.Surface(stage, SurfaceRole.Decorative, paving, AssetCategories.SurfacePaving);
                ctx.Plan.Facts["stage"] = 1;
            }

            // 3. Paths: oval rings (inner + outer tread) as arcs between the spokes; spokes between them.
            var pathAsset = AssetMix.One(ctx.S("pathAsset"));
            var sorted = (float[])g.Spokes.Clone();
            System.Array.Sort(sorted);
            float inner = g.Mid(1), outer = g.Mid(g.Levels);
            bool rings = ctx.B("rings");
            float length = 0f;
            if (rings)
                foreach (var rho in g.Levels > 1 ? new[] { inner, outer } : new[] { outer })
                    for (int i = 0; i < sorted.Length; i++)
                    {
                        float t0 = sorted[i], t1 = i + 1 < sorted.Length ? sorted[i + 1] : sorted[0] + 2f * math.PI;
                        // The amphitheatre's rows replace the inner ring in its sector; the outer ring is its top promenade.
                        if (rho == inner && g.Amphi && g.InSector((t0 + t1) * 0.5f)) continue;
                        length += MonumentKit.ArcPath(ctx, occ, g.C, g.Lat, A * rho, B * rho, t0, t1, NetRole.Pathway, pathAsset);
                    }
            float spokeIn = rings ? inner : g.Rho0, spokeOut = rings ? outer : 1f;
            foreach (var t in g.Spokes)
            {
                var a = g.At(spokeIn, t); var b = g.At(spokeOut, t);
                MonumentKit.Path(ctx, occ, a, b, NetRole.Pathway, pathAsset);
                length += math.distance(a, b);
            }
            // The entrance spoke runs on to where people arrive.
            var gate = g.At(spokeOut, g.Spokes[0]);
            if (math.distance(gate, entry) > 1f) { MonumentKit.Path(ctx, occ, gate, entry, NetRole.Pathway, pathAsset); length += math.distance(gate, entry); }
            ctx.Stat(Keys.StatPathLength, math.round(length), Keys.UnitM);
            ctx.Check();

            // 4. Centrepiece.
            string centre = ctx.S("centre");
            string chosen = ctx.S("centreAsset");
            string cat = centre == "fountain" ? LeisureKit.Category(ctx, chosen, AssetCategories.CenterpieceFountain, AssetCategories.Centerpiece)
                : LeisureKit.Category(ctx, chosen, MonumentKit.StatueChain);
            float scale = ctx.F("size") / 100f;
            if (cat != null)
            {
                float r = math.min(A, B) * g.Rho0 * 0.55f;
                var o = ctx.Object(g.C, MonumentKit.Yaw(entry - g.C), ObjectRole.Centerpiece, AssetMix.One(chosen), Hashing.Mix(ctx.Seed, 0xCE0u), -1, cat);
                o.Scale = centre == "fountain" ? math.min(scale, 2f) : string.IsNullOrEmpty(chosen) && !MonumentKit.IsFigure(cat) ? math.min(scale, 1.25f) : scale;
                occ.Take(g.C, r, 0);
            }

            // 5. Amphitheatre seats: benches on each row's arc, facing the stage.
            int seats = 0;
            if (g.Amphi)
            {
                var focus = g.At(g.Rho0, (g.S0 + g.S1) * 0.5f);
                var bench = new AssetRef("");
                float step = (1f - g.RhoStage) / g.Rows;
                for (int j = 0; j < g.Rows; j++)
                {
                    float rho = g.RhoStage + (j + 0.5f) * step;
                    if (rings && math.abs(rho - outer) * B < 2.5f) continue;
                    float arc = (g.S1 - g.S0) * rho * (A + B) * 0.5f;
                    int n = math.max(1, (int)(arc / 3.2f));
                    for (int i = 0; i < n; i++)
                    {
                        float t = math.lerp(g.S0, g.S1, (i + 0.5f) / n);
                        var p = g.At(rho, t);
                        if (MonumentKit.Put(ctx, occ, p, MonumentKit.Yaw(focus - p), ObjectRole.Bench, bench, null, 1f, Hashing.Mix(ctx.Seed, 0x5EA0u + (uint)(j * 64 + i)), 1f, 0.3f, 0.15f) != null) seats++;
                    }
                }
                ctx.Plan.Facts["amphiRows"] = g.Rows;
            }
            ctx.Plan.Facts["seats"] = seats;
            ctx.Check();

            // 6. Planting.
            int hedges = 0, beds = 0, trees = 0;
            if (ctx.B("hedges") && g.Levels > 1)
            {
                string hc = string.IsNullOrEmpty(ctx.S("hedgeAsset")) ? MonumentKit.Quiet("", AssetCategories.Hedge, AssetCategories.Bush) : AssetCategories.Hedge;
                if (hc != null)
                    for (int k = 1; k < g.Levels; k++)
                    {
                        float rho = g.Edge(k) + 0.9f / B;          // just on the upper / outer side of the step line
                        foreach (var p in Along(g, rho, 1.8f))
                        {
                            g.Polar(p, out _, out float t);
                            if (g.InSector(t, 0.05f)) continue;
                            if (MonumentKit.Put(ctx, occ, p, 0f, hc == AssetCategories.Bush ? ObjectRole.Bush : ObjectRole.Furniture, AssetMix.One(ctx.S("hedgeAsset")), hc, 0.6f,
                                Hashing.Mix(ctx.Seed, 0x4ED0u), 1f, 0.2f, 0.15f) != null) hedges++;
                        }
                    }
            }
            if (ctx.B("flowers"))
            {
                string fc = MonumentKit.Quiet("", AssetCategories.Flowerbed, AssetCategories.Bush);
                if (fc != null)
                    for (int k = 1; k <= g.Levels; k++)
                    {
                        if (rings && (k == 1 || k == g.Levels)) continue;
                        foreach (var p in Along(g, g.Mid(k), 4.5f))
                        {
                            g.Polar(p, out _, out float t);
                            if (g.InSector(t, 0.05f)) continue;
                            if (MonumentKit.Put(ctx, occ, p, t, fc == AssetCategories.Bush ? ObjectRole.Bush : ObjectRole.Planter, new AssetRef(""), fc, 0.9f,
                                Hashing.Mix(ctx.Seed, 0xF10u + (uint)beds), 1f, 0.2f, 0.2f) != null) beds++;
                        }
                    }
                if (!rings || g.Levels <= 2)
                    // Flowers ring the central plaza when there are no middle treads.
                    foreach (var p in Along(g, g.Rho0 + 1.2f / B, 3.5f))
                    {
                        g.Polar(p, out _, out float t);
                        if (g.InSector(t, 0.05f)) continue;
                        if (MonumentKit.Put(ctx, occ, p, t, fc == AssetCategories.Bush ? ObjectRole.Bush : ObjectRole.Planter, new AssetRef(""), fc ?? AssetCategories.Bush, 0.7f,
                            Hashing.Mix(ctx.Seed, 0xF20u + (uint)beds), 1f, 0.2f, 0.2f) != null) beds++;
                    }
            }
            if (ctx.B("trees") && AssetAvailability.Has(AssetCategories.Tree))
                foreach (var p in Along(g, 1f + 2.2f / B, 8f))
                    if (MonumentKit.Inside(site, p, 2f) && MonumentKit.Put(ctx, occ, p, 0f, ObjectRole.Tree, new AssetRef(""), null, 2f, Hashing.Mix(ctx.Seed, 0x7E0u + (uint)trees), 1f, 0.5f, 0.3f) != null)
                        trees++;
            if (ctx.B("lamps"))
                foreach (var t in g.Spokes)
                {
                    var p = g.At(outer, t + 2.6f / (outer * B));
                    MonumentKit.Put(ctx, occ, p + math.normalizesafe(p - g.C) * 2.4f, 0f, ObjectRole.Lamp, new AssetRef(""), AssetCategories.Lamp, 0.4f, Hashing.Mix(ctx.Seed, 0x1A0u), 1f, 0.3f);
                }
            // Benches round the inner ring, facing the centrepiece.
            int benches = 0;
            if (rings && g.Levels > 1)
                for (int i = 0; i < sorted.Length; i++)
                {
                    float t0 = sorted[i], t1 = i + 1 < sorted.Length ? sorted[i + 1] : sorted[0] + 2f * math.PI;
                    float tm = (t0 + t1) * 0.5f;
                    if (g.InSector(tm)) continue;
                    var p = g.At(inner - 2.6f / B, tm);
                    if (MonumentKit.Put(ctx, occ, p, MonumentKit.Yaw(g.C - p), ObjectRole.Bench, new AssetRef(""), null, 1f, Hashing.Mix(ctx.Seed, 0xBE0u + (uint)i), 1f, 0.3f) != null) benches++;
                }
            ctx.Plan.Facts["hedges"] = hedges;
            ctx.Plan.Facts["flowerbeds"] = beds;
            ctx.Stat(Keys.StatBenches, benches + seats, Keys.UnitCount);
            ctx.Stat(Keys.StatTrees, trees, Keys.UnitCount);
            ctx.Stat("Placemaker.Stat.Seats", seats, Keys.UnitCount);
        }

        /// <summary>Points every <paramref name="spacing"/> metres (approximately) round the ellipse at normalised radius rho.</summary>
        private static IEnumerable<float2> Along(Garden g, float rho, float spacing)
        {
            float a = g.A * rho, b = g.B * rho;
            float perimeter = math.PI * (3f * (a + b) - math.sqrt((3f * a + b) * (a + 3f * b)));
            int n = math.max(4, (int)(perimeter / spacing));
            for (int i = 0; i < n; i++) yield return g.At(rho, (i + 0.5f) * 2f * math.PI / n);
        }

        private static float Wrap(float a)
        {
            while (a < -math.PI) a += 2f * math.PI;
            while (a >= math.PI) a -= 2f * math.PI;
            return a;
        }
    }
}
