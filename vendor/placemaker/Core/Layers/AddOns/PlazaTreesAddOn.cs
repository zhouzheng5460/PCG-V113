// Plaza trees add-on: shade for a paved square. Two layouts:
//   grid     a regular tree grid aligned with the square's long axis (boulevard-square look)
//   islands  a few round green islands (grass surface) with one to three trees each
// Everything goes into free space only, so the plaza's centrepiece, furniture and access stay clear.

using System.Collections.Generic;
using Placemaker.Core.Layers.Common;
using Unity.Mathematics;

namespace Placemaker.Core.Layers.AddOns
{
    [AreaGenerator(145)]
    public sealed class PlazaTreesAddOn : AddOnLayer
    {
        public const string LayerId = "plazatrees";

        protected override string Area => "AddOn.PlazaTrees";
        public override string Id => LayerId;
        public override string Icon => "Media/Game/Icons/Forest.svg";
        public override string[] Accepts => new[] { RegionTags.Paved };

        private const float TreeRadius = 1.5f;

        protected override void DefineControls(ControlSet c)
        {
            c.Choice("layout", "grid", ControlTier.Essential, "trees", "grid", "islands").Tip();
            c.Slider("spacing", 10, 6, 24, 1, "m", ControlTier.Essential, "trees").Layout(6).Tip();
            c.AssetMulti("species", AssetCategories.Tree, ControlTier.More, "trees");
            c.Asset("grass", AssetCategories.SurfaceGrass, ControlTier.More, "trees").When("layout=islands");
            c.RandomRange("spacing", 8, 14);
        }

        protected override void DefinePresets()
        {
            Preset("grid", ("layout", "grid"), ("spacing", 10.0));
            Preset("islands", ("layout", "islands"), ("spacing", 14.0));
        }

        protected override void PlanCore(LayerContext ctx)
        {
            var ids = ctx.L("species");
            if (!AssetAvailability.Require(ctx.Plan, AssetCategories.Tree, ids, Keys.Category(AssetCategories.Tree))) return;
            var occ = Occupancy(ctx);
            float spacing = math.max(4f, ctx.F("spacing"));
            int placed = ctx.S("layout") == "islands" ? Islands(ctx, occ, ids, spacing) : Grid(ctx, occ, ids, spacing);
            if (placed == 0) NoSpace(ctx, "Placemaker.AddOn.PlazaTrees.Name");
            ctx.Stat(Keys.StatTrees, placed, Keys.UnitCount);
        }

        private static int Grid(LayerContext ctx, Occupancy occ, string[] ids, float spacing)
        {
            var region = ctx.LayerRegion;
            region.PrincipalAxes(out var centre, out var major, out var majorSpan, out var minorSpan);
            var minor = new float2(-major.y, major.x);
            // Rows centred on the square so the grid looks intentional, not clipped at one side.
            int nu = (int)math.floor(majorSpan / spacing) + 1, nv = (int)math.floor(minorSpan / spacing) + 1;
            int placed = 0;
            for (int j = 0; j < nv; j++)
                for (int i = 0; i < nu; i++)
                {
                    var p = centre + major * ((i - (nu - 1) * 0.5f) * spacing) + minor * ((j - (nv - 1) * 0.5f) * spacing);
                    if (!occ.Free(p, TreeRadius, 2f, 0.6f)) continue;
                    uint v = Hashing.Mix(ctx.Seed, (uint)(j * 7919 + i + 1));
                    ctx.Object(p, Hashing.Unit(v) * 2f * math.PI, ObjectRole.Tree, AssetMix.Pick(ids, v), v, 2);
                    occ.Take(p, TreeRadius, 0);
                    placed++;
                }
            return placed;
        }

        private static int Islands(LayerContext ctx, Occupancy occ, string[] ids, float spacing)
        {
            float islandRadius = math.clamp(spacing * 0.3f, 2.5f, 6f);
            int wanted = math.clamp((int)(ctx.LayerRegion.Area / (spacing * spacing * 4f)), 1, 24);
            var grass = AssetMix.One(ctx.S("grass"));
            int placed = 0;
            var centres = Spread(occ, islandRadius + 1f, wanted, Spots.Preference.Open, ctx.Seed, spacing);
            for (int k = 0; k < centres.Count; k++)
            {
                var c = centres[k];
                ctx.Surface(LayerRegion.Circle(c, islandRadius, 20, Hashing.Unit(Hashing.Mix(ctx.Seed, (uint)k)) * math.PI), SurfaceRole.Grass, grass);
                occ.Take(c, islandRadius, 1);
                int trees = islandRadius >= 4f ? 3 : islandRadius >= 3f ? 2 : 1;
                float phase = Hashing.Unit(Hashing.Mix(ctx.Seed, 0x15u + (uint)k)) * 2f * math.PI;
                var spots = trees == 1 ? new List<float2> { c } : RingPoints(c, islandRadius * 0.45f, trees, phase);
                foreach (var p in spots)
                {
                    uint v = Hashing.Mix(ctx.Seed, (uint)(k * 31 + placed + 1));
                    ctx.Object(p, Hashing.Unit(v) * 2f * math.PI, ObjectRole.Tree, AssetMix.Pick(ids, v), v, 2);
                    placed++;
                }
            }
            return placed;
        }
    }
}
