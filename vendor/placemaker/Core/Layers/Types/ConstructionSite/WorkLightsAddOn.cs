// Work lights feature (construction site, quarry): floodlight towers along the edge, at the corners or
// spread over the open ground, each with a small generator beside it.

using Placemaker.Core.Layers.Types.Scrapyard;
using System.Collections.Generic;
using Placemaker.Core.Layers.AddOns;
using Placemaker.Core.Layers.Common;
using Unity.Mathematics;

namespace Placemaker.Core.Layers.Types.ConstructionSite
{
    [AreaGenerator(163)]
    public sealed class WorkLightsAddOn : AddOnLayer
    {
        public const string LayerId = "workLights";

        protected override string Area => "AddOn.WorkLights";
        public override string Id => LayerId;
        public override string Icon => "Media/Game/Icons/PropsIndustrial.svg";

        protected override void DefineControls(ControlSet c)
        {
            c.Slider("towers", 4, 1, 16, 1, "count", ControlTier.Essential, "lighting");
            c.Choice("placement", "edge", ControlTier.Essential, "lighting", "edge", "corners", "spread");
            c.Toggle("generators", true, ControlTier.More, "lighting").Tip();
            c.AssetMulti("lightAssets", AssetCategories.FloodLight, ControlTier.More, "lighting");
        }

        // Art pass: whatever the player left automatic gets the curated gritty defaults (GritAssets).
        protected override void PlanCore(LayerContext ctx) { PlanArt(ctx); GritAssets.Apply(ctx.Plan, ctx.Seed); }

        private void PlanArt(LayerContext ctx)
        {
            var occ = Occupancy(ctx);
            var region = ctx.LayerRegion;
            var ids = ctx.L("lightAssets");
            string light = SiteKit.Cat(ctx, ids, AssetCategories.FloodLight, AssetCategories.Lamp);
            if (light == null) return;
            string gen = ctx.B("generators") ? SiteKit.Cat(ctx, null, AssetCategories.ConstructionGenerator, AssetCategories.ConstructionMachine) : null;
            int wanted = ctx.I("towers");
            var spots = new List<float2>();
            string mode = ctx.S("placement");
            if (mode == "corners")
            {
                var ring = region.Ring;
                for (int i = 0; i < ring.Length; i++)
                {
                    var a = ring[(i - 1 + ring.Length) % ring.Length]; var b = ring[i]; var c = ring[(i + 1) % ring.Length];
                    float turn = math.abs(math.atan2(Poly2.Cross(b - a, c - b), math.dot(b - a, c - b)));
                    if (turn < 0.5f) continue;
                    var n1 = math.normalizesafe(new float2(-(b - a).y, (b - a).x)); var n2 = math.normalizesafe(new float2(-(c - b).y, (c - b).x));
                    spots.Add(b + math.normalizesafe(n1 + n2, n2) * 4f);
                }
            }
            else if (mode == "edge")
                for (int i = 0; i < wanted; i++)
                {
                    var p = region.PointAt(region.Perimeter * (i + 0.5f) / wanted, out _, out var inward, out _);
                    spots.Add(p + inward * 3.5f);
                }
            else
                for (int i = 0; i < wanted; i++)
                {
                    if (!Spots.Find(occ, 3f, Spots.Preference.Open, Hashing.Mix(ctx.Seed, 0x11u + (uint)i), out var p, spots, math.sqrt(region.Area / math.max(1, wanted)) * 0.6f, 2f, 400)) break;
                    spots.Add(p);
                }
            int placed = 0;
            for (int i = 0; i < spots.Count && placed < wanted; i++)
            {
                var p = spots[i];
                if (!SiteKit.Put(ctx, occ, p, Facing(p, region.Centroid), light, ids, 0.7f, 0x1100u + (uint)i, 1f)) continue;
                placed++;
                if (gen != null)
                {
                    var side = math.normalizesafe(region.Centroid - p);
                    SiteKit.Put(ctx, occ, p + new float2(-side.y, side.x) * 2.4f, Facing(p, region.Centroid), gen, null, 1f, 0x1180u + (uint)i, 0.5f);
                }
            }
            if (placed == 0) NoSpace(ctx, "Placemaker.AddOn.WorkLights.Name");
            ctx.Stat(Keys.StatLamps, placed, Keys.UnitCount);
        }

        private static class Poly2
        {
            public static float Cross(float2 a, float2 b) => a.x * b.y - a.y * b.x;
        }
    }
}
