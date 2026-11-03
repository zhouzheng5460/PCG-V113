// Cricket ground: an oval outfield inside a boundary rope (semi-axes from about 70 × 63 m for a county
// ground down to 36 × 33 m for a school ground) with the pitch in the middle along the long axis: a
// 20.12 m pitch on a 22 × 3.05 m rolled dirt strip set in a mown square, real creases (bowling,
// popping, return), the 27.43 m (30-yard) fielding circle as disc markers, sight screens behind both
// ends, a pavilion on the arrival side with benches round the boundary, optional floodlights.

using System.Collections.Generic;
using Placemaker.Core.Layers.Common;
using Unity.Mathematics;

namespace Placemaker.Core.Layers.AddOns.Fields
{
    [AreaGenerator(185)]
    public sealed class CricketAddOn : FieldAddOn
    {
        protected override string Area => "Field.Cricket";
        public override string Id => "cricketGround";

        public const float Stumps = 10.06f;   // half of 20.12 m
        public const float Circle30 = 27.43f;

        protected override FieldFormat[] Formats(LayerContext ctx) => FieldSpecs.Cricket;

        protected override void DefineControls(ControlSet c)
        {
            c.Choice("format", "auto", ControlTier.Essential, "field", WithAuto(FieldSpecs.Cricket)).Tip();
            c.Choice("boundary", "rope", ControlTier.Essential, "field", "rope", "fence", "none").Tip();
            c.Toggle("square", true, ControlTier.More, "field").Tip();
            c.Asset("stripAsset", AssetCategories.SurfaceDirt, ControlTier.More, "field");
            LineControls(c);
            c.Toggle("fieldingCircle", true, ControlTier.More, "lines").When("lines=true").Tip();
            c.Toggle("sightScreens", true, ControlTier.More, "boundary").Tip();
            c.Asset("fenceAsset", AssetCategories.Fence, ControlTier.More, "boundary");
            c.Toggle("pavilion", true, ControlTier.More, "spectators").Tip();
            c.Toggle("benches", true, ControlTier.More, "spectators");
            c.Asset("benchAsset", AssetCategories.Bench, ControlTier.More, "spectators").When("benches=true");
            c.Asset("standAsset", AssetCategories.SportsStand, ControlTier.More, "spectators");
            LightControls(c, "lighting", false, "false");
            GroundControls(c);
            PlacementControls(c);
            c.Chance("floodlights", 0.15f);
        }

        protected override void DefinePresets()
        {
            Preset("village", ("format", "village"), ("boundary", "rope"), ("pavilion", true), ("floodlights", false), ("fieldingCircle", false));
            Preset("club", ("format", "auto"), ("boundary", "rope"), ("pavilion", true), ("floodlights", false));
            Preset("county", ("format", "county"), ("boundary", "fence"), ("pavilion", true), ("floodlights", true));
            Preset("schoolField", ("format", "school"), ("boundary", "none"), ("pavilion", false), ("sightScreens", false), ("fieldingCircle", false));
        }

        protected override void Lay(FieldJob j)
        {
            var ctx = j.Ctx;
            var place = j.Place;
            var f = Frame.Of(place);
            float margin = j.Format == "school" ? 5f : j.Format == "village" ? 6f : 7f;
            float A = place.HalfLength - margin, B = place.HalfWidth - margin;
            var fence = AssetMix.One(ctx.S("fenceAsset"));

            // Strip and square.
            FieldKit.Surface(ctx, f.Box(-11f, -1.525f, 11f, 1.525f), SurfaceRole.Ground, ctx.S("stripAsset"), AssetCategories.SurfaceDirt, AssetCategories.SurfaceNatural);
            if (ctx.B("square") && AssetAvailability.Has(AssetCategories.SurfaceGrass))
            {
                float su = 13.5f, sv = math.min(10f, B * 0.3f);
                FieldKit.Surface(ctx, f.Box(-su, 1.525f, su, sv), SurfaceRole.Grass, "", AssetCategories.SurfaceGrass);
                FieldKit.Surface(ctx, f.Box(-su, -sv, su, -1.525f), SurfaceRole.Grass, "", AssetCategories.SurfaceGrass);
                FieldKit.Surface(ctx, f.Box(11f, -1.525f, su, 1.525f), SurfaceRole.Grass, "", AssetCategories.SurfaceGrass);
                FieldKit.Surface(ctx, f.Box(-su, -1.525f, -11f, 1.525f), SurfaceRole.Grass, "", AssetCategories.SurfaceGrass);
            }

            // Creases and stumps.
            var l = j.Lines;
            for (int e = -1; e <= 1; e += 2)
            {
                l.Seg(f, e * Stumps, -1.32f, e * Stumps, 1.32f);
                l.Seg(f, e * (Stumps - 1.22f), -1.83f, e * (Stumps - 1.22f), 1.83f);
                for (int s = -1; s <= 1; s += 2) l.Seg(f, e * (Stumps - 1.22f), s * 1.32f, e * (Stumps + 1.22f), s * 1.32f);
                l.Spot(f, e * Stumps, 0f, f.YawOf(new float2(0, 1)));
            }
            // The 30-yard fielding circle: disc markers every ~5 m (two semicircles joined by straights).
            if (ctx.B("fieldingCircle") && Stumps + Circle30 < A - 4f && Circle30 < B - 4f)
            {
                for (int e = -1; e <= 1; e += 2)
                    for (int i = 0; i <= 16; i++)
                    {
                        float a = (e > 0 ? -0.5f : 0.5f) * math.PI + math.PI * i / 16f;   // the outward half round each set of stumps
                        l.Spot(f, e * Stumps + math.cos(a) * Circle30, math.sin(a) * Circle30);
                    }
                for (int s = -1; s <= 1; s += 2)
                    for (float u = -Stumps + 5f; u < Stumps; u += 5f) l.Spot(f, u, s * Circle30);
            }

            // Boundary: rope (painted), low fence (with a gate towards the path) or nothing.
            var ellipse = Ellipse(f, A, B, 48);
            string boundary = ctx.S("boundary");
            if (boundary == "rope") l.Path(ellipse, true);
            else if (boundary == "fence")
            {
                var big = Ellipse(f, A + 1.2f, B + 1.2f, 40);
                int gate = 0; float best = float.MaxValue;
                for (int i = 0; i < big.Length; i++) { float d = math.distancesq(big[i], j.Toward); if (d < best) { best = d; gate = i; } }
                var run = new List<float2>();
                for (int k = 1; k < big.Length; k++) run.Add(big[(gate + k) % big.Length]);
                FieldKit.FenceRun(ctx, run, fence);
            }
            if (ctx.B("sightScreens"))
                for (int e = -1; e <= 1; e += 2)
                {
                    float u = e * math.min(A + 2.5f, place.HalfLength - 1.5f);
                    ctx.StraightNet(f.P(u, -4f), f.P(u, 4f), NetRole.Fence, fence);
                }

            // Pavilion on the arrival side, benches round that half of the boundary.
            var tl = f.Local(j.Toward);
            float th = math.atan2(tl.y / math.max(1f, B), tl.x / math.max(1f, A));
            float2 OnRing(float angle, float grow) => f.P(math.cos(angle) * (A + grow), math.sin(angle) * (B + grow));
            if (ctx.B("pavilion"))
            {
                string cat = FieldKit.Cat(ctx, "", AssetCategories.Shelter, AssetCategories.Restroom, AssetCategories.Kiosk);
                var p = OnRing(th, math.min(3.4f, margin - 3.3f));
                if (!FieldKit.Equip(j, p, LeisureKit.Yaw(p, f.O), cat, "", 2.8f, 0xA71u))
                    FieldKit.Equip(j, OnRing(th + 0.25f, math.min(3.4f, margin - 3.3f)), LeisureKit.Yaw(p, f.O), cat, "", 2.8f, 0xA72u);
                FieldKit.Stands(j, OnRing(th - 0.16f, 3f), OnRing(th - 0.32f, 3f), f.O, 6f, 1.4f);
                FieldKit.Stands(j, OnRing(th + 0.16f, 3f), OnRing(th + 0.32f, 3f), f.O, 6f, 1.4f);
            }
            if (ctx.B("benches"))
            {
                var pts = new List<float2>();
                for (int i = -4; i <= 4; i++)
                {
                    if (math.abs(i) <= 1) continue;   // the pavilion's stretch
                    pts.Add(OnRing(th + i * 0.2f, 2.2f));
                }
                FieldKit.Benches(j, pts, f.O);
            }
            if (ctx.B("floodlights"))
            {
                var pts = new List<float2>();
                for (int i = 0; i < 6; i++) pts.Add(OnRing(math.radians(30f + 60f * i), math.min(4f, margin - 1f)));
                FieldKit.Floodlights(j, pts, f.O);
            }
            ctx.Plan.Facts["boundaryLong"] = A;
            ctx.Plan.Facts["boundaryShort"] = B;
        }

        private static float2[] Ellipse(Frame f, float a, float b, int n)
        {
            var r = new float2[n];
            for (int i = 0; i < n; i++) { float t = i * 2f * math.PI / n; r[i] = f.P(math.cos(t) * a, math.sin(t) * b); }
            return r;
        }
    }
}
