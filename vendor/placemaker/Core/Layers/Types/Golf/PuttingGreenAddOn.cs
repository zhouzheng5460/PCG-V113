// Practice putting green: a kidney-shaped green with several cups (flags), an optional chipping
// bunker on its side and a couple of benches facing it. The golf layer offers it on its "practice"
// region next to the clubhouse; any park can use it too (it only needs open lawn).

using Placemaker.Core.Layers.AddOns;
using Placemaker.Core.Layers.Common;
using Unity.Mathematics;

namespace Placemaker.Core.Layers.Types.Golf
{
    [AreaGenerator(247)]
    public sealed class PuttingGreenAddOn : AddOnLayer
    {
        public const string LayerId = "puttingGreen";
        protected override string Area => "AddOn.PuttingGreen";
        public override string Id => LayerId;
        public override string Icon => "Media/Game/Icons/LargeSportsParks.svg";
        public override string[] Accepts => new[] { RegionTags.Green, RegionTags.Core, GolfLayer.PracticeTag };

        protected override void DefineControls(ControlSet c)
        {
            c.Slider("size", 16, 8, 40, 1, "m", ControlTier.Essential, "greens").Tip();
            c.Slider("cups", 6, 1, 12, 1, "count", ControlTier.Essential, "greens");
            c.Toggle("bunker", true, ControlTier.More, "greens").Tip();
            c.Toggle("benches", true, ControlTier.More, "greens");
            c.Asset("greenSurface", AssetCategories.SurfaceGrass, ControlTier.More, "greens");
            c.Asset("flagAsset", AssetCategories.GolfFlag, ControlTier.More, "greens");
            c.RandomRange("size", 10, 24);
        }

        protected override void PlanCore(LayerContext ctx)
        {
            var occ = Occupancy(ctx);
            float want = ctx.F("size") * 0.5f;
            float r = want; float2 c = default; bool found = false;
            for (int i = 0; i < 4 && !found; i++, r *= 0.75f)
                found = r >= 3f && Spots.Find(occ, r + (ctx.B("bunker") ? r * 0.5f + 1f : 1f), Spots.Preference.Centre, Hashing.Mix(ctx.Seed, (uint)i), out c);
            if (!found) { NoSpace(ctx, "Placemaker.AddOn.PuttingGreen.Name"); return; }
            r /= 0.75f;
            if (r > want) r = want;
            uint v = Hashing.Mix(ctx.Seed, 0x9073u);
            var ring = LeisureKit.Blob(c, r, v, 18, 0.2f);
            ctx.Surface(ring, SurfaceRole.Grass, GolfAssets.GreenRef(ctx.S("greenSurface")), AssetCategories.SurfaceGrass);
            occ.Take(c, r, 0);
            int cups = 0;
            var (flagAsset, flag) = GolfAssets.FlagProp(ctx.S("flagAsset"), false);
            {
                int n = ctx.I("cups");
                float phase = Hashing.Unit(v) * 6.283f;
                for (int i = 0; i < n; i++)
                {
                    float a = phase + i * 2.39996f;   // golden angle: cups spread over the green
                    float d = r * 0.65f * math.sqrt((i + 0.5f) / n);
                    ctx.Object(c + new float2(math.cos(a), math.sin(a)) * d, 0f, ObjectRole.Furniture, flagAsset, Hashing.Mix(v, (uint)i), -1, flag);
                    cups++;
                }
            }
            float phase2 = Hashing.Unit(Hashing.Mix(v, 77u)) * 6.283f;
            if (ctx.B("bunker"))
            {
                float br = math.max(1.5f, r * 0.4f);
                var bc = c + new float2(math.cos(phase2), math.sin(phase2)) * (r + br + 0.6f);
                if (occ.Free(bc, br, 0.5f, 0.2f))
                {
                    var b = LeisureKit.Blob(bc, br, Hashing.Mix(v, 5u), 12, 0.25f);
                    if (ctx.Surface(b, SurfaceRole.Decorative, GolfAssets.BunkerRef(""), AssetCategories.SurfaceSand))
                        occ.Take(bc, br, 0);
                }
            }
            int benches = 0;
            if (ctx.B("benches"))
                for (int i = 0; i < 2; i++)
                {
                    float a = phase2 + math.PI * (0.75f + 0.5f * i);
                    var p = c + new float2(math.cos(a), math.sin(a)) * (r + 2.2f);
                    if (LeisureKit.Put(ctx, occ, p, Facing(p, c), ObjectRole.Bench, AssetMix.One(""), null, 1.3f, Hashing.Mix(v, 30u + (uint)i), 0.3f, 0.3f)) benches++;
                }
            ctx.Stat("Placemaker.Stat.Cups", cups, Keys.UnitCount);
            ctx.Stat(Keys.StatBenches, benches, Keys.UnitCount);
        }
    }
}
