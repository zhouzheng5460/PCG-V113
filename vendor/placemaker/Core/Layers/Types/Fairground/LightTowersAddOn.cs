// Light towers feature (events): mobile lighting masts spread over the open ground of an event
// site — at the edges of the open space, beside the paths — so the crowd field and the avenues are
// lit at night. Light tower, else floodlight, else a plain lamp.

using System.Collections.Generic;
using Placemaker.Core.Layers.AddOns;
using Placemaker.Core.Layers.Common;
using Unity.Mathematics;

namespace Placemaker.Core.Layers.Types.Events
{
    [AreaGenerator(171)]
    public sealed class LightTowersAddOn : AddOnLayer
    {
        public const string LayerId = "lightTowers";

        protected override string Area => "AddOn.LightTowers";
        public override string Id => LayerId;
        public override string Icon => "Media/Game/Icons/Lighting.svg";

        protected override void DefineControls(ControlSet c)
        {
            c.Slider("density", 2, 0.5, 8, 0.5, "perHa", ControlTier.Essential, "lighting").Tip();
            c.Slider("spacing", 30, 12, 80, 2, "m", ControlTier.Essential, "lighting").Layout(10).Tip();
            c.Choice("where", "paths", ControlTier.More, "lighting", "paths", "edges", "open").Tip();
            c.Asset("asset", AssetCategories.EventLightTower, ControlTier.More, "lighting");
            c.RandomRange("density", 1, 4);
            c.Fixed("where");
        }

        protected override void PlanCore(LayerContext ctx)
        {
            string chosen = ctx.S("asset");
            string cat = LeisureKit.Category(ctx, chosen, AssetCategories.EventLightTower, AssetCategories.FloodLight, AssetCategories.Lamp);
            if (cat == null) return;
            var occ = Occupancy(ctx);
            int wanted = math.clamp((int)math.round(ctx.LayerRegion.Area / 10000f * ctx.F("density")), 1, 60);
            float spacing = ctx.F("spacing");
            var asset = AssetMix.One(chosen);
            var spots = new List<float2>();
            string where = ctx.S("where");
            if (where == "paths" && occ.Paths.Count > 0)
            {
                foreach (var a in PathAnchors.Pick(PathAnchors.Beside(occ, 0.8f, 6f, 0.6f), wanted, spacing, false)) spots.Add(a.Position);
            }
            else
            {
                var pref = where == "open" ? Spots.Preference.Open : Spots.Preference.NearBoundary;
                for (int i = 0; i < wanted; i++)
                {
                    if (!Spots.Find(occ, 0.8f, pref, Hashing.Mix(ctx.Seed, (uint)i), out var s, spots, spacing, 1f, 1200)) break;
                    spots.Add(s);
                }
            }
            int n = 0;
            var centre = ctx.LayerRegion.Centroid;
            foreach (var p in spots)
                if (EventKit.Put(ctx, occ, p, LeisureKit.Yaw(p, centre), cat, asset, 0.8f, Hashing.Mix(ctx.Seed, 0x1700u + (uint)n), 0.5f, 0.2f)) n++;
            if (n == 0) NoSpace(ctx, "Placemaker.Feature.lightTowers");
            ctx.Stat(Keys.StatLamps, n, Keys.UnitCount);
        }
    }
}
