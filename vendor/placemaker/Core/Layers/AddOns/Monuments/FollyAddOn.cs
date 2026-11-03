// Folly — "Palace of Fine Arts" style, composed of props (no building):
//
//   rotunda    a ring of columns (scaled up) round a paved floor at the back of the rectangle, a big
//              statue or fountain in the middle, statues on a ring outside, arches where the walk passes
//   lagoon     an elliptical pool in front of the rotunda (water-look surface, or real water on flat
//              ground), with reeds / shrubs at its ends — the rotunda is seen across the water
//   colonnade  the walk loops round the lagoon and THROUGH the rotunda (passing in front of the central
//              statue); its back and side arcs are lined by columns on both sides, so it is a
//              peristyle you walk through; the front of the loop is an open promenade with lamps and
//              benches facing the water
//
// Columns degrade to columnar trees (a living colonnade) and then statues; arches are skipped when none
// are installed (the gaps in the ring are the openings). All in the rectangle's own frame: symmetric.

using System.Collections.Generic;
using Placemaker.Core.Layers.Common;
using Unity.Mathematics;

namespace Placemaker.Core.Layers.AddOns.Monuments
{
    [AreaGenerator(175)]
    public sealed class FollyAddOn : MonumentAddOn
    {
        public const string LayerId = "folly";
        protected override string Area => "AddOn.Folly";
        public override string Id => LayerId;

        protected override void DefineControls(ControlSet c)
        {
            c.Slider("columns", 12, 6, 24, 1, "count", ControlTier.Essential, "rotunda").Tip();
            c.Slider("columnScale", 150, 75, 300, 25, "%", ControlTier.Essential, "rotunda").Tip();
            c.Asset("columnAsset", AssetCategories.MonumentColumn, ControlTier.Essential, "rotunda");
            c.Choice("centre", "statue", ControlTier.More, "rotunda", "statue", "fountain", "none");
            c.Slider("statues", 6, 0, 12, 1, "count", ControlTier.More, "rotunda");
            c.Asset("statueAsset", AssetCategories.CenterpieceStatue, ControlTier.More, "rotunda");
            c.Toggle("arches", true, ControlTier.More, "rotunda").Tip();
            c.Asset("archAsset", AssetCategories.MonumentArch, ControlTier.More, "rotunda").When("arches=true");
            c.Toggle("arms", true, ControlTier.Essential, "colonnade").Tip();
            c.Slider("armSpan", 110, 40, 160, 10, "deg", ControlTier.More, "colonnade").When("arms=true").Tip();
            c.Slider("columnSpacing", 4, 2.5, 8, 0.5, "m", ControlTier.More, "colonnade").When("arms=true");
            c.Toggle("lagoon", true, ControlTier.Essential, "lagoon").Tip();
            c.Slider("lagoonSize", 100, 50, 100, 5, "%", ControlTier.More, "lagoon").When("lagoon=true");
            c.Toggle("realWater", false, ControlTier.More, "lagoon").When("lagoon=true").Tip();
            c.Asset("waterAsset", AssetCategories.SurfaceWater, ControlTier.More, "lagoon").When("lagoon=true");
            c.Toggle("planting", true, ControlTier.More, "extras").Tip();
            c.Toggle("lamps", true, ControlTier.More, "extras");
            c.Slider("benches", 6, 0, 12, 1, "count", ControlTier.More, "extras");
            c.Asset("pavingAsset", AssetCategories.SurfacePaving, ControlTier.More, "ground");
            c.Asset("pathAsset", AssetCategories.Pathway, ControlTier.More, "ground");
            c.Fixed("realWater");
            c.RandomRange("columns", 8, 16);
        }

        protected override void DefinePresets()
        {
            Preset("palace", ("columns", 12.0), ("arms", true), ("armSpan", 110.0), ("lagoon", true), ("centre", "statue"), ("statues", 6.0));
            Preset("belvedere", ("columns", 10.0), ("arms", false), ("lagoon", true), ("lagoonSize", 80.0), ("centre", "fountain"), ("statues", 4.0));
            Preset("peristyle", ("columns", 16.0), ("arms", true), ("armSpan", 160.0), ("columnSpacing", 3.0), ("lagoon", false), ("centre", "statue"), ("statues", 8.0));
        }

        private struct Frame
        {
            public float2 C, Lat, Fw;   // centre, lateral axis, towards the visitors
            public float2 E(float2 c, float a, float b, float t) => MonumentKit.Ellipse(c, Lat, a, b, t);
        }

        protected override void Build(LayerContext ctx, Occupancy occ, OrientedRect site, float2 toward)
        {
            var entry = MonumentKit.Entry(occ, site, toward);
            // The long side faces the visitors: the arms spread wide, the depth holds rotunda + lagoon.
            var fw = site.Side * SideSign(site, entry);
            var f = new Frame { C = site.Center, Fw = fw, Lat = new float2(fw.y, -fw.x) };   // perp(Lat) = Fw
            float hl = site.HalfLength, hw = site.HalfWidth;
            if (hw < 12f || hl < 16f) { NoSpace(ctx, NameKey); return; }
            float colScale = ctx.F("columnScale") / 100f;
            const float walkHalf = 1.5f;

            // Rotunda at the back, the walk ellipse from inside the rotunda to the front promenade.
            float Rr = math.clamp(hw * 0.42f, 6f, 14f);
            var R = f.C - fw * (hw - Rr - 2.2f);
            float back = math.dot(R - f.C, fw) + Rr * 0.42f;   // walk passes in front of the central statue
            float front = hw - 3f;
            var W = f.C + fw * ((back + front) * 0.5f);
            float bw = (front - back) * 0.5f, aw = hl - 4f;

            // Ground: rotunda floor and the promenade belt are paved; the walk itself is a path.
            var paving = AssetMix.One(ctx.S("pavingAsset"));
            ctx.Surface(LayerRegion.Circle(R, Rr + 1.6f, 32), SurfaceRole.Paving, paving, AssetCategories.SurfacePaving);

            // Lagoon: inside the walk loop, clear of the rotunda.
            float2[] lagoon = null;
            float La = 0f, Lb = 0f; float2 Lc = default;
            if (ctx.B("lagoon"))
            {
                float k = ctx.F("lagoonSize") / 100f;
                float y0 = math.dot(R - f.C, fw) + Rr + 2.5f, y1 = front - walkHalf - 3f;
                Lb = (y1 - y0) * 0.5f * k; Lc = f.C + fw * ((y0 + y1) * 0.5f + (y1 - y0) * 0.5f * (1f - k));
                La = (aw - walkHalf - 4f) * k;
                // Keep it inside the loop with a margin for the inner colonnade.
                for (int it = 0; it < 12 && !InsideEllipse(f, W, aw, bw, Lc, La, Lb, 0.82f); it++) { La *= 0.92f; Lb *= 0.96f; }
                if (Lb > 3f && La > 5f)
                {
                    lagoon = MonumentKit.EllipseRing(Lc, f.Lat, La, Lb, 32);
                    MonumentKit.Pool(ctx, lagoon, ctx.B("realWater"), ctx.S("waterAsset"));
                    for (int i = 0; i < 24; i++)
                        occ.Take(f.E(Lc, La * 0.75f, Lb * 0.6f, i * math.PI / 12f), math.min(La, Lb) * 0.45f, 5);
                    occ.Take(Lc, math.min(La, Lb), 5);
                    ctx.Plan.Facts["lagoonArea"] = math.PI * La * Lb;
                }
            }

            // The walk loop: node angles at the rotunda crossings, the arm ends, the front.
            float tRot = CrossingAngle(f, W, aw, bw, R, Rr);
            float span = ctx.B("arms") ? math.radians(ctx.F("armSpan")) : 0f;
            float tArm = math.min(-math.PI / 2f + tRot + span, math.PI / 2f - 0.35f);
            var nodes = new List<float> { -math.PI / 2f - tRot, -math.PI / 2f + tRot, math.PI / 2f };
            if (span > 0f) { nodes.Add(tArm); nodes.Add(-math.PI - tArm); }
            for (int i = 0; i < nodes.Count; i++) nodes[i] = Wrap(nodes[i]);
            nodes.Sort();
            var pathAsset = AssetMix.One(ctx.S("pathAsset"));
            float length = 0f;
            for (int i = 0; i < nodes.Count; i++)
            {
                float t0 = nodes[i], t1 = i + 1 < nodes.Count ? nodes[i + 1] : nodes[0] + 2f * math.PI;
                length += MonumentKit.ArcPath(ctx, occ, W, f.Lat, aw, bw, t0, t1, NetRole.Pathway, pathAsset);
            }
            // Spur from the entry to the nearest node of the loop.
            float best = float.MaxValue; float2 node = default;
            foreach (var t in nodes) { var q = f.E(W, aw, bw, t); float dd = math.distance(q, entry); if (dd < best) { best = dd; node = q; } }
            if (best > 1f) MonumentKit.Path(ctx, occ, entry, node, NetRole.Pathway, pathAsset);
            ctx.Stat(Keys.StatPathLength, math.round(length), Keys.UnitM);
            ctx.Check();

            // Centrepiece in the rotunda (before the columns so nothing crowds it).
            var statueAsset = AssetMix.One(ctx.S("statueAsset"));
            string statueCat = MonumentKit.Quiet(ctx.S("statueAsset"), MonumentKit.StatueChain);
            string centre = ctx.S("centre");
            if (centre == "fountain")
            {
                string fc = MonumentKit.Quiet("", AssetCategories.CenterpieceFountain, AssetCategories.Centerpiece);
                if (fc != null) MonumentKit.Put(ctx, occ, R, MonumentKit.Yaw(fw), ObjectRole.Centerpiece, new AssetRef(""), fc, 2.5f, Hashing.Mix(ctx.Seed, 0xF0u), 1.5f, 0.2f, 0.1f);
            }
            else if (centre == "statue" && statueCat != null)
            {
                float s = MonumentKit.IsFigure(statueCat) || !string.IsNullOrEmpty(ctx.S("statueAsset")) ? math.max(1.5f, colScale * 1.4f) : 1f;
                var o = MonumentKit.Put(ctx, occ, R, MonumentKit.Yaw(fw), ObjectRole.Centerpiece, statueAsset, statueCat, 2f, Hashing.Mix(ctx.Seed, 0x57Au), s, 0.2f, 0.1f);
                if (o == null) ctx.Issue(IssueSeverity.Info, K + "Issue.NoRoomForCentre");
            }

            // Arches where the walk crosses the ring (astride the walk; not checked against it).
            int arches = 0;
            if (ctx.B("arches"))
            {
                string ac = MonumentKit.Quiet(ctx.S("archAsset"), AssetCategories.MonumentArch);
                if (ac != null)
                    foreach (var t in new[] { -math.PI / 2f - tRot, -math.PI / 2f + tRot })
                    {
                        var p = f.E(W, aw, bw, t);
                        var tan = math.normalizesafe(f.E(W, aw, bw, t + 0.01f) - p);
                        var a = ctx.Object(p, MonumentKit.Yaw(tan), ObjectRole.Furniture, AssetMix.One(ctx.S("archAsset")), Hashing.Mix(ctx.Seed, 0xA4Cu), -1, ac);
                        a.Scale = colScale;
                        arches++;
                    }
            }
            ctx.Plan.Facts["arches"] = arches;

            // Rotunda ring of columns.
            string colCat = MonumentKit.Columns(ctx, ctx.S("columnAsset"), out var colRole);
            var colAsset = colRole == ObjectRole.Tree && string.IsNullOrEmpty(ctx.S("columnAsset")) ? MonumentKit.ColumnTree : AssetMix.One(ctx.S("columnAsset"));
            float colR = colRole == ObjectRole.Tree ? 1.2f : 0.55f * math.max(1f, colScale * 0.7f);
            float colS = colRole == ObjectRole.Tree ? 1f : colScale;
            int ring = 0, colonnade = 0;
            if (colCat != null)
            {
                int n = ctx.I("columns");
                float phase = math.atan2(fw.y, fw.x) + math.PI / n;
                for (int i = 0; i < n; i++)
                {
                    float a = phase + i * 2f * math.PI / n;
                    var p = R + new float2(math.cos(a), math.sin(a)) * Rr;
                    if (MonumentKit.Put(ctx, occ, p, a, colRole, colAsset, colCat, colR, Hashing.Mix(ctx.Seed, 0xC01u), colS, 0.3f, 0.15f) != null) ring++;
                }
                // Colonnade arms: pairs either side of the walk, from the rotunda to the arm ends.
                if (span > 0f)
                {
                    float spacing = ctx.F("columnSpacing");
                    for (int sgn = -1; sgn <= 1; sgn += 2)
                    {
                        float tStart = -math.PI / 2f + sgn * tRot, tEnd = sgn > 0 ? tArm : -math.PI - tArm;
                        float t = tStart; var prev = f.E(W, aw, bw, t);
                        float acc = spacing * 0.5f;
                        int guard = 0;
                        while ((sgn > 0 ? t < tEnd : t > tEnd) && guard++ < 4000)
                        {
                            t += sgn * 0.004f;
                            var p = f.E(W, aw, bw, t);
                            acc += math.distance(p, prev); prev = p;
                            if (acc < spacing) continue;
                            acc = 0f;
                            if (math.distance(p, R) < Rr + colR + 1.5f) continue;
                            var tan = math.normalizesafe(f.E(W, aw, bw, t + 0.01f) - p);
                            var nrm = new float2(-tan.y, tan.x);
                            for (int s = -1; s <= 1; s += 2)
                            {
                                var q = p + nrm * s * (walkHalf + 1.3f + colR);
                                if (!MonumentKit.Inside(site, q, colR + 0.3f)) continue;
                                if (lagoon != null && InsideEllipse(f, Lc, La + 1f, Lb + 1f, q)) continue;
                                if (MonumentKit.Put(ctx, occ, q, MonumentKit.Yaw(tan), colRole, colAsset, colCat, colR, Hashing.Mix(ctx.Seed, 0xC02u), colS, 0.3f, 0.1f) != null) colonnade++;
                            }
                        }
                    }
                }
            }
            ctx.Plan.Facts["rotundaColumns"] = ring;
            ctx.Plan.Facts["colonnadeColumns"] = colonnade;

            // Statues on a ring outside the columns, behind and beside (not where the walk passes).
            int statues = 0;
            // The ring takes ornaments: chosen statues, else figures, else urns (never a ring of fountains).
            string ringCat = string.IsNullOrEmpty(ctx.S("statueAsset")) ? MonumentKit.Quiet("", MonumentKit.OrnamentChain) : statueCat;
            var ringRole = ObjectRole.Centerpiece;
            var ringAsset = statueAsset;
            if (string.IsNullOrEmpty(ctx.S("statueAsset"))) ringAsset = MonumentKit.OrnamentAsset(ringCat, out ringRole);
            if (ringCat != null && ctx.I("statues") > 0)
            {
                int n = ctx.I("statues");
                float baseA = math.atan2(-fw.y, -fw.x);
                for (int i = 0; i < n; i++)
                {
                    float a = baseA + (n == 1 ? 0f : (i / (float)(n - 1) - 0.5f) * math.radians(200f));
                    var p = R + new float2(math.cos(a), math.sin(a)) * (Rr + 2.4f);
                    if (!MonumentKit.Inside(site, p, 1f)) continue;
                    if (MonumentKit.Put(ctx, occ, p, a, ringRole, ringAsset, ringCat, 0.9f, Hashing.Mix(ctx.Seed, 0x5701u + (uint)i),
                        ringRole == ObjectRole.Planter ? 1f : math.max(1f, colScale * 0.8f), 0.3f, 0.1f) != null) statues++;
                }
            }
            ctx.Plan.Facts["statues"] = statues;
            ctx.Check();

            // Promenade: lamps and benches facing the water along the front of the loop.
            int benches = 0;
            int wantBenches = ctx.I("benches");
            for (int i = 0; i < 12; i++)
            {
                float t = math.PI / 2f + (i / 11f - 0.5f) * 1.6f;
                var p = f.E(W, aw, bw, t);
                var inward = math.normalizesafe(W - p);
                if (ctx.B("lamps") && (i % 3) == 1)
                    MonumentKit.Put(ctx, occ, p - inward * (walkHalf + 0.9f), 0f, ObjectRole.Lamp, new AssetRef(""), AssetCategories.Lamp, 0.4f, Hashing.Mix(ctx.Seed, 0x1A0u + (uint)i), 1f, 0.3f);
                if (benches < wantBenches && (i % 2) == 0)
                {
                    var q = p + inward * (walkHalf + 1.2f);
                    if (lagoon != null && InsideEllipse(f, Lc, La + 0.6f, Lb + 0.6f, q)) continue;
                    if (MonumentKit.Put(ctx, occ, q, MonumentKit.Yaw(lagoon != null ? Lc - q : inward), ObjectRole.Bench, new AssetRef(""), null, 1f, Hashing.Mix(ctx.Seed, 0xBE0u + (uint)i), 1f, 0.3f) != null) benches++;
                }
            }
            ctx.Stat(Keys.StatBenches, benches, Keys.UnitCount);

            if (ctx.B("planting")) Planting(ctx, occ, site, f, R, Rr, lagoon != null, Lc, La, Lb);
            ctx.Stat("Placemaker.Stat.Monuments", 1, Keys.UnitCount);
        }

        /// <summary>Reeds / shrubs at the lagoon's ends, a screen of trees behind the rotunda and the arms.</summary>
        private static void Planting(LayerContext ctx, Occupancy occ, OrientedRect site, Frame f, float2 R, float Rr, bool lagoon, float2 Lc, float La, float Lb)
        {
            if (lagoon)
            {
                string bc = MonumentKit.Quiet("", AssetCategories.Bush);
                if (bc != null)
                    for (int s = -1; s <= 1; s += 2)
                        for (int k = -2; k <= 2; k++)
                        {
                            float t = (s > 0 ? 0f : math.PI) + k * 0.12f;
                            var q = f.E(Lc, La + 0.9f, Lb + 0.9f, t);
                            MonumentKit.Put(ctx, occ, q, t, ObjectRole.Bush, new AssetRef(""), bc, 0.7f, Hashing.Mix(ctx.Seed, 0x8EEu + (uint)(k + 2 + (s + 1) * 5)), 1f, 0.3f, 0.1f);
                        }
            }
            if (!AssetAvailability.Has(AssetCategories.Tree)) return;
            // Trees in the back corners and behind the rotunda: the dark backdrop the columns read against.
            float hl = site.HalfLength, hw = site.HalfWidth;
            int n = 0;
            for (float u = -hl + 3f; u <= hl - 3f; u += 7f)
            {
                var q = f.C + f.Lat * u - f.Fw * (hw - 2.6f);
                if (MonumentKit.Put(ctx, occ, q, 0f, ObjectRole.Tree, new AssetRef(""), null, 2.2f, Hashing.Mix(ctx.Seed, 0x7E0u + (uint)n), 1f, 0.5f, 0.4f) != null) n++;
            }
            for (int s = -1; s <= 1; s += 2)
                for (float v = -hw + 3f; v <= hw * 0.2f; v += 7f)
                {
                    var q = f.C + f.Lat * s * (hl - 2.6f) + f.Fw * v;
                    if (MonumentKit.Put(ctx, occ, q, 0f, ObjectRole.Tree, new AssetRef(""), null, 2.2f, Hashing.Mix(ctx.Seed, 0x7E0u + (uint)n), 1f, 0.5f, 0.4f) != null) n++;
                }
            ctx.Plan.Facts["trees"] = n;
        }

        /// <summary>Half-angle (from the back point t = −π/2) at which the walk ellipse leaves the rotunda circle.</summary>
        private static float CrossingAngle(Frame f, float2 W, float aw, float bw, float2 R, float Rr)
        {
            float lo = 0f, hi = math.PI / 2f;
            for (int i = 0; i < 30; i++)
            {
                float mid = (lo + hi) * 0.5f;
                if (math.distance(f.E(W, aw, bw, -math.PI / 2f + mid), R) < Rr) lo = mid; else hi = mid;
            }
            return math.max(0.05f, (lo + hi) * 0.5f);
        }

        private static bool InsideEllipse(Frame f, float2 c, float a, float b, float2 p)
        {
            var d = p - c;
            float u = math.dot(d, f.Lat) / math.max(0.1f, a), v = math.dot(d, f.Fw) / math.max(0.1f, b);
            return u * u + v * v <= 1f;
        }

        /// <summary>True when the inner ellipse lies inside the outer one scaled by <paramref name="k"/>.</summary>
        private static bool InsideEllipse(Frame f, float2 oc, float oa, float ob, float2 ic, float ia, float ib, float k)
        {
            for (int i = 0; i < 24; i++)
                if (!InsideEllipse(f, oc, oa * k, ob * k, f.E(ic, ia, ib, i * math.PI / 12f))) return false;
            return true;
        }

        private static float Wrap(float a)
        {
            while (a < -math.PI) a += 2f * math.PI;
            while (a >= math.PI) a -= 2f * math.PI;
            return a;
        }
    }
}
