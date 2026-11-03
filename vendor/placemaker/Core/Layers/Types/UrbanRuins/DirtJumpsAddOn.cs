// "Dirt jumps" feature: kids and BMX riders dig jump lines into any leftover ground — offered by the
// abandoned lot and the urban ruins (and usable by any recipe). See DirtJumps for the shaping.

using Placemaker.Core.Layers.Types.Scrapyard;
using Placemaker.Core.Layers.AddOns;
using Placemaker.Core.Layers.Common;
using Placemaker.Core.Layers.Types.Ruins;
using Unity.Mathematics;

namespace Placemaker.Core.Layers.Types.UrbanRuins
{
    [AreaGenerator(172)]
    public sealed class DirtJumpsAddOn : AddOnLayer
    {
        public const string LayerId = "dirtJumps";

        protected override string Area => "AddOn.DirtJumps";
        public override string Id => LayerId;
        public override string Icon => "Media/Game/Icons/TerrainHeight.svg";
        public override float MinArea => 300f;

        protected override void DefineControls(ControlSet c)
        {
            c.Slider("lines", 2, 1, 4, 1, "count", ControlTier.Essential, "jumps");
            c.Slider("height", 1.5, 0.5, 3, 0.25, "m", ControlTier.Essential, "jumps").Tip();
            c.Slider("jumps", 4, 2, 8, 1, "count", ControlTier.More, "jumps").Tip();
            c.Slider("spacing", 9, 6, 14, 0.5, "m", ControlTier.More, "jumps").Tip();
            c.Toggle("startHill", true, ControlTier.More, "jumps").Tip();
            c.Toggle("paint", true, ControlTier.More, "jumps");
            c.Slider("paintLayer", 3, 1, 4, 1, "count", ControlTier.More, "jumps").When("paint=true").Tip();
            c.Fixed("paintLayer");
        }

        protected override void DefinePresets()
        {
            Preset("beginner", ("lines", 1.0), ("height", 1.0), ("jumps", 3.0), ("spacing", 8.0));
            Preset("pro", ("lines", 3.0), ("height", 2.5), ("jumps", 6.0), ("spacing", 12.0));
        }

        // Art pass: whatever the player left automatic gets the curated gritty defaults (GritAssets).
        protected override void PlanCore(LayerContext ctx) { PlanArt(ctx); GritAssets.Apply(ctx.Plan, ctx.Seed); }

        private void PlanArt(LayerContext ctx)
        {
            var region = ctx.LayerRegion;
            var occ = Occupancy(ctx);
            region.PrincipalAxes(out var centre, out var axis, out _, out _);
            var deep = region.DeepestPoint(out _, null, 14);
            int built = DirtJumps.PlaceLines(ctx, region, occ, new Zones(), deep, axis, ctx.I("lines"), ctx.I("jumps"), ctx.F("height"), ctx.F("spacing"),
                ctx.B("startHill"), ctx.B("paint"), ctx.I("paintLayer"), Hashing.Mix(ctx.Seed, 0xD127u));
            if (built == 0) NoSpace(ctx, "Placemaker.AddOn.DirtJumps.Name");
            ctx.Stat("Placemaker.Stat.Jumps", built, Keys.UnitCount);
            ctx.Plan.Facts["jumps"] = built;
        }
    }
}
