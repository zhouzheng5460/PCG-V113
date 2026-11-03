// Baseball / softball diamond in a square place: home plate tucked into the corner nearest where people
// arrive, the two foul lines running along the place's sides, the outfield fence an arc at the real
// distance (99 m major, 80 m community, 67 m softball, 61 m Little League, 46 m tee-ball). Real infield:
// 27.43 m (90 ft) or 18.29 m (60 ft) bases, the pitcher's rubber at 18.44 / 14.02 / 13.11 m, a skinned
// dirt infield cut round the grass diamond (all dirt for softball and tee-ball), a mound, the home-plate
// circle and a warning track inside the fence. Backstop behind home, sideline and outfield fences,
// dugouts, bleachers along the lines, floodlights outside the foul lines. Lines: foul lines, batter's
// boxes, on-deck circles, bases and plate as marking decals.

using System.Collections.Generic;
using Placemaker.Core.Geometry;
using Placemaker.Core.Layers.Common;
using Unity.Mathematics;

namespace Placemaker.Core.Layers.AddOns.Fields
{
    [AreaGenerator(181)]
    public sealed class BaseballAddOn : FieldAddOn
    {
        protected override string Area => "Field.Baseball";
        public override string Id => "baseballDiamond";

        private const float C = 0.70710678f;

        protected override FieldFormat[] Formats(LayerContext ctx) => FieldSpecs.Baseball;

        /// <summary>Bases, rubber, fence, skin radius (from the rubber), margin behind home, grass infield.</summary>
        private struct Diamond { public float Bases, Rubber, Fence, Skin, Margin, Mound; public bool Grass; }

        private static Diamond Of(string format)
        {
            switch (format)
            {
                case "major": return new Diamond { Bases = 27.43f, Rubber = 18.44f, Fence = 99f, Skin = 28.96f, Margin = 10f, Mound = 2.74f, Grass = true };
                case "softball": return new Diamond { Bases = 18.29f, Rubber = 13.11f, Fence = 67f, Skin = 18.3f, Margin = 8f, Mound = 0f, Grass = false };
                case "littleLeague": return new Diamond { Bases = 18.29f, Rubber = 14.02f, Fence = 61f, Skin = 15.24f, Margin = 7f, Mound = 1.8f, Grass = true };
                case "teeBall": return new Diamond { Bases = 15.24f, Rubber = 11.6f, Fence = 46f, Skin = 13f, Margin = 6f, Mound = 0f, Grass = false };
                default: return new Diamond { Bases = 27.43f, Rubber = 18.44f, Fence = 80f, Skin = 26f, Margin = 9f, Mound = 2.74f, Grass = true };
            }
        }

        protected override void DefineControls(ControlSet c)
        {
            c.Choice("format", "auto", ControlTier.Essential, "field", WithAuto(FieldSpecs.Baseball)).Tip();
            c.Toggle("infieldDirt", true, ControlTier.Essential, "field").Tip();
            c.Asset("dirtAsset", AssetCategories.SurfaceDirt, ControlTier.More, "field").When("infieldDirt=true");
            c.Toggle("warningTrack", true, ControlTier.More, "field").Tip();
            LineControls(c);
            c.Toggle("backstop", true, ControlTier.More, "boundary").Tip();
            c.Toggle("fence", true, ControlTier.More, "boundary").Tip();
            c.Asset("fenceAsset", AssetCategories.Fence, ControlTier.More, "boundary");
            c.Toggle("dugouts", true, ControlTier.More, "spectators").Tip();
            c.Toggle("stands", true, ControlTier.More, "spectators").Tip();
            c.Asset("standAsset", AssetCategories.SportsStand, ControlTier.More, "spectators");
            c.Asset("benchAsset", AssetCategories.Bench, ControlTier.More, "spectators");
            LightControls(c, "lighting", true, "none");
            GroundControls(c);
            PlacementControls(c);
            c.Chance("warningTrack", 0.7f);
        }

        protected override void DefinePresets()
        {
            Preset("ballpark", ("format", "major"), ("stands", true), ("floodlights", "six"), ("warningTrack", true));
            Preset("community", ("format", "auto"), ("stands", true), ("floodlights", "four"));
            Preset("littleLeague", ("format", "littleLeague"), ("stands", true), ("floodlights", "none"));
            Preset("sandlot", ("format", "softball"), ("stands", false), ("dugouts", false), ("fence", false), ("floodlights", "none"), ("warningTrack", false));
        }

        protected override void Lay(FieldJob j)
        {
            var ctx = j.Ctx;
            var d = Of(j.Format);
            var place = j.Place;
            // Home plate in the corner nearest the arrival side; foul lines along the two sides from there.
            var ring = place.Ring;
            int ci = 0; float best = float.MaxValue;
            for (int i = 0; i < 4; i++) { float dd = math.distancesq(ring[i], j.Toward); if (dd < best) { best = dd; ci = i; } }
            var corner = ring[ci];
            var d1 = math.normalizesafe(ring[(ci + 1) % 4] - corner);
            var d2 = math.normalizesafe(ring[(ci + 3) % 4] - corner);
            float m = d.Margin;
            var f = new Frame(corner + (d1 + d2) * m, d1 + d2);
            float b = d.Bases, R = math.min(d.Fence, math.min(math.distance(ring[ci], ring[(ci + 1) % 4]), math.distance(ring[ci], ring[(ci + 3) % 4])) - m - 2f);
            var rubber = new float2(d.Rubber, 0f);
            float rh = 3.96f * math.min(1f, b / 27.43f) + 0.3f;
            float o = b > 20f ? 3f : 2f;   // skin beyond the foul lines
            float w = b > 20f ? 1.6f : 1.2f;   // base-path dirt inside the lines

            // Skinned infield.
            if (ctx.B("infieldDirt"))
            {
                string chosen = ctx.S("dirtAsset");
                var chain = new[] { AssetCategories.SurfaceDirt, AssetCategories.SurfaceNatural, AssetCategories.SurfaceSand };
                if (d.Grass)
                {
                    for (int s = -1; s <= 1; s += 2) FieldKit.Surface(ctx, World(f, SkinHalf(s, b, rubber, d.Skin, o, w, rh)), SurfaceRole.Ground, chosen, chain);
                    if (d.Mound > 0f) FieldKit.Surface(ctx, World(f, Disc(rubber, d.Mound, 18)), SurfaceRole.Ground, chosen, chain);
                }
                else FieldKit.Surface(ctx, World(f, SkinWhole(rubber, d.Skin, o, rh)), SurfaceRole.Ground, chosen, chain);
            }
            float q = m - 1.2f;   // sideline fences, outside the foul lines
            float spread = math.asin(math.saturate(q / R));
            if (ctx.B("warningTrack") && R > 50f)
            {
                var band = new List<float2>();
                float a0 = -math.PI * 0.25f - spread * 0.8f, a1 = -a0;
                int n = 28;
                for (int i = 0; i <= n; i++) { float a = math.lerp(a0, a1, i / (float)n); band.Add(new float2(math.cos(a), math.sin(a)) * (R - 0.6f)); }
                for (int i = n; i >= 0; i--) { float a = math.lerp(a0, a1, i / (float)n); band.Add(new float2(math.cos(a), math.sin(a)) * (R - 4.6f)); }
                FieldKit.Surface(ctx, World(f, band), SurfaceRole.Ground, ctx.S("dirtAsset"), AssetCategories.SurfaceDirt, AssetCategories.SurfaceNatural);
            }

            // Lines: foul lines, batter's boxes, on-deck circles, plate, bases, rubber (softball: pitching circle).
            var l = j.Lines;
            for (int s = -1; s <= 1; s += 2) l.Seg(f, 0f, 0f, (R - 0.8f) * C, s * (R - 0.8f) * C);
            float bk = math.min(1f, b / 27.43f) * 0.15f + 0.85f;
            for (int s = -1; s <= 1; s += 2)
            {
                l.Box(f, -0.9f * bk, s * 0.37f, 0.9f * bk, s * (0.37f + 1.22f * bk));
                l.Circle(f, new float2(-0.15f * m, s * 0.55f * m), 0.76f);
            }
            l.Spot(f, 0f, 0f);
            l.Spot(f, b * C, -b * C); l.Spot(f, b * 1.41421f, 0f); l.Spot(f, b * C, b * C);
            l.Spot(f, d.Rubber, 0f, f.YawOf(new float2(0, 1)));
            if (d.Mound <= 0f) l.Circle(f, rubber, 2.44f);

            // Backstop, sideline and outfield fences (gaps by the dugouts are the gates).
            var fence = AssetMix.One(ctx.S("fenceAsset"));
            float rs = m - 1.5f;
            if (ctx.B("backstop"))
            {
                var arc = new List<float2>();
                for (int i = 0; i <= 8; i++) { float a = math.radians(125f + 110f * i / 8f); arc.Add(f.P(new float2(math.cos(a), math.sin(a)) * rs)); }
                FieldKit.FenceRun(ctx, arc, fence);
            }
            if (ctx.B("fence"))
            {
                for (int s = -1; s <= 1; s += 2)
                {
                    var n = new float2(-C, s * C); var dir = new float2(C, s * C);
                    float tEnd = math.sqrt(math.max(1f, R * R - q * q));
                    FieldKit.FenceRun(ctx, new[] { f.P(n * q + dir * (rs * 0.6f + 2.5f)), f.P(n * q + dir * tEnd) }, fence);
                }
                var outfield = new List<float2>();
                float e0 = -math.PI * 0.25f - spread, e1 = -e0;
                int segs = math.max(6, (int)(R * (e1 - e0) / 7f));
                for (int i = 0; i <= segs; i++) { float a = math.lerp(e0, e1, i / (float)segs); outfield.Add(f.P(new float2(math.cos(a), math.sin(a)) * R)); }
                FieldKit.FenceRun(ctx, outfield, fence);
            }

            // Dugouts (shelters or team benches) between home and first / third, bleachers further along.
            for (int s = -1; s <= 1; s += 2)
            {
                var n = new float2(-C, s * C); var dir = new float2(C, s * C);
                if (ctx.B("dugouts"))
                {
                    var p = f.P(n * (m * 0.55f) + dir * (b * 0.45f));
                    string cat = FieldKit.Cat(ctx, "", AssetCategories.Shelter, AssetCategories.Bench);
                    if (cat == AssetCategories.Shelter) FieldKit.Equip(j, p, LeisureKit.Yaw(p, f.P(dir * (b * 0.45f))), cat, "", 1.8f, 0xD600u + (uint)(s + 1));
                    else FieldKit.Benches(j, new[] { p, f.P(n * (m * 0.55f) + dir * (b * 0.45f + 2.6f)) }, f.P(dir * (b * 0.45f)));
                }
                if (ctx.B("stands"))
                    FieldKit.Stands(j, f.P(n * (m - 2.4f) + dir * (b * 0.8f)), f.P(n * (m - 2.4f) + dir * (b * 1.6f)), f.P(dir * b), 7f, 1.6f);
            }
            string lights = ctx.S("floodlights");
            if (lights != "none")
            {
                var pts = new List<float2>();
                for (int s = -1; s <= 1; s += 2)
                {
                    var n = new float2(-C, s * C); var dir = new float2(C, s * C);
                    pts.Add(f.P(n * (m - 0.6f) + dir * (R * 0.3f)));
                    pts.Add(f.P(n * (m - 0.6f) + dir * (R * 0.75f)));
                    if (lights == "six") pts.Add(f.P(new float2(math.cos(s * 0.42f), math.sin(s * 0.42f)) * (R + 1.6f)));
                }
                FieldKit.Floodlights(j, pts, f.P(rubber.x * 1.8f, 0f));
            }
            ctx.Plan.Facts["fenceDistance"] = R;
            ctx.Plan.Facts["baseDistance"] = b;
        }

        private static float2[] World(Frame f, List<float2> local)
        {
            var r = new float2[local.Count];
            for (int i = 0; i < local.Count; i++) r[i] = f.P(local[i]);
            return r;
        }

        private static List<float2> Disc(float2 c, float r, int n)
        {
            var l = new List<float2>(n);
            for (int i = 0; i < n; i++) { float a = i * 2f * math.PI / n; l.Add(c + new float2(math.cos(a), math.sin(a)) * r); }
            return l;
        }

        /// <summary>Where the skin's outer arc meets the line o metres outside foul line s (local), and its angle round the rubber.</summary>
        private static float2 SkinCorner(int s, float2 rubber, float skin, float o, out float angle)
        {
            var n = new float2(-C, s * C); var dir = new float2(C, s * C);
            var w = n * o - rubber;
            float wd = math.dot(w, dir);
            float t = -wd + math.sqrt(math.max(0f, wd * wd - (math.dot(w, w) - skin * skin)));
            var x = n * o + dir * t;
            angle = math.atan2(x.y - rubber.y, x.x - rubber.x);
            return x;
        }

        /// <summary>Where the line o metres outside foul line s meets the home-plate circle.</summary>
        private static float2 HomeCorner(int s, float o, float rh, out float angle)
        {
            var n = new float2(-C, s * C); var dir = new float2(C, s * C);
            float t = rh > o + 0.2f ? math.sqrt(rh * rh - o * o) : 0.5f;
            var y = n * o + dir * t;
            angle = math.atan2(y.y, y.x);
            return y;
        }

        /// <summary>One half (side s) of a skinned infield round a grass diamond: outer arc, the line outside the foul line, the plate circle, the grass edge.</summary>
        private static List<float2> SkinHalf(int s, float b, float2 rubber, float skin, float o, float w, float rh)
        {
            var pts = new List<float2>();
            SkinCorner(s, rubber, skin, o, out float ax);
            int n = 14;
            for (int i = 0; i <= n; i++) { float a = ax * i / n; pts.Add(rubber + new float2(math.cos(a), math.sin(a)) * skin); }
            var y = HomeCorner(s, o, rh, out float ay);
            pts.Add(y);
            float back = s * math.PI;
            for (int i = 1; i <= 6; i++) { float a = math.lerp(ay, back, i / 6f); pts.Add(new float2(math.cos(a), math.sin(a)) * rh); }
            float inset = w * 1.41421f;
            pts.Add(new float2(math.max(inset, 0.6f), 0f));
            pts.Add(new float2(b * C, s * (b * C - inset)));
            pts.Add(new float2(b * 1.41421f - inset, 0f));
            return pts;
        }

        /// <summary>An all-dirt infield (softball, tee-ball).</summary>
        private static List<float2> SkinWhole(float2 rubber, float skin, float o, float rh)
        {
            var pts = new List<float2>();
            SkinCorner(1, rubber, skin, o, out float a1);
            SkinCorner(-1, rubber, skin, o, out float a0);
            int n = 24;
            for (int i = 0; i <= n; i++) { float a = math.lerp(a1, a0, i / (float)n); pts.Add(rubber + new float2(math.cos(a), math.sin(a)) * skin); }
            HomeCorner(-1, o, rh, out float h0);
            HomeCorner(1, o, rh, out float h1);
            float end = h1 - 2f * math.PI;   // round the back of the plate, from the first-base side to the third-base side
            for (int i = 0; i <= 8; i++) { float a = math.lerp(h0, end, i / 8f); pts.Add(new float2(math.cos(a), math.sin(a)) * rh); }
            return pts;
        }
    }
}
