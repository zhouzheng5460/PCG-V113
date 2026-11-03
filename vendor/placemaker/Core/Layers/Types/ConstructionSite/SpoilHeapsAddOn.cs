// Spoil heaps feature (construction site, quarry): big heaps of dirt, gravel, sand or rubble in the
// open, each a small raised terrain mound (optional, permanent) dressed with pile props and mud
// paint. Heaps keep far enough from the outline that the mound blends back to the original ground.

using Placemaker.Core.Layers.Types.Scrapyard;
using System.Collections.Generic;
using Placemaker.Core.Layers.AddOns;
using Placemaker.Core.Layers.Common;
using Placemaker.Core.Geometry;
using Unity.Mathematics;

namespace Placemaker.Core.Layers.Types.ConstructionSite
{
    [AreaGenerator(162)]
    public sealed class SpoilHeapsAddOn : AddOnLayer
    {
        public const string LayerId = "spoilHeaps";
        private const float Slope = 0.6f;

        protected override string Area => "AddOn.SpoilHeaps";
        public override string Id => LayerId;
        public override string Icon => "Media/Game/Icons/Terraforming/TextureDirt.svg";

        protected override void DefineControls(ControlSet c)
        {
            c.Slider("heaps", 4, 1, 12, 1, "count", ControlTier.Essential, "heaps");
            c.Choice("size", "medium", ControlTier.Essential, "heaps", "small", "medium", "large");
            c.Choice("material", "dirt", ControlTier.Essential, "heaps", "dirt", "gravel", "sand", "rubble");
            c.Toggle("raise", true, ControlTier.More, "heaps").Tip();
            c.Toggle("paint", true, ControlTier.More, "heaps");
            c.Slider("paintLayer", 1, 1, 4, 1, "count", ControlTier.More, "heaps").When("paint=true").Tip();
            c.AssetMulti("pileAssets", AssetCategories.ConstructionPile, ControlTier.More, "heaps");
            c.Fixed("paintLayer");
        }

        // Art pass: whatever the player left automatic gets the curated gritty defaults (GritAssets).
        protected override void PlanCore(LayerContext ctx) { PlanArt(ctx); GritAssets.Apply(ctx.Plan, ctx.Seed); }

        private void PlanArt(LayerContext ctx)
        {
            var occ = Occupancy(ctx);
            var region = ctx.LayerRegion;
            float r = ctx.S("size") == "small" ? 6f : ctx.S("size") == "large" ? 13f : 9f;
            float height = r * 0.28f;
            var ids = ctx.L("pileAssets");
            string cat = ctx.S("material") == "rubble"
                ? SiteKit.Cat(ctx, ids, AssetCategories.ConstructionRubble, AssetCategories.ConstructionPile, AssetCategories.Rock)
                : SiteKit.Cat(ctx, ids, AssetCategories.ConstructionPile, AssetCategories.Rock);
            int wanted = ctx.I("heaps"), placed = 0, props = 0;
            var centres = new List<float2>();
            for (int i = 0; i < wanted; i++)
            {
                ctx.Check();
                float margin = ctx.B("raise") ? height / Slope + 1f : 1f;
                if (!Spots.Find(occ, r, Spots.Preference.Open, Hashing.Mix(ctx.Seed, 0x5B0u + (uint)i), out var c, centres, r * 2f + 4f, margin, 500)) break;
                centres.Add(c);
                if (ctx.B("raise"))
                {
                    var ring = LayerRegion.Circle(c, r * 0.75f, 16, Hashing.Unit(Hashing.Mix(ctx.Seed, (uint)i)) * 6.283f);
                    ctx.Plan.Terraform.Add(new PlannedTerraform
                    {
                        Ring = Poly.To3D(ring, ctx.H), Kind = TerraformKind.Raise, TargetHeight = ctx.H(c) + height, Permanent = true,
                    });
                }
                if (ctx.B("paint")) TerrainPaint.Patch(ctx, LeisureKit.Blob(c, r, Hashing.Mix(ctx.Seed, 0x5B1u + (uint)i), 14, 0.25f), math.clamp(ctx.I("paintLayer"), 1, 4), 0.9f, 2f);
                props += SiteKit.Cluster(ctx, occ, c, 3 + (int)(r / 5f), r * 0.6f, cat, ids, r * 0.25f, 0x5B2u + (uint)i * 41u);
                occ.Take(c, r * 0.85f, SiteKit.KindProp);
                placed++;
            }
            if (placed > 0 && ctx.B("raise")) ctx.Issue(IssueSeverity.Warning, TerrainShaper.PermanentKey);
            if (placed == 0) NoSpace(ctx, "Placemaker.AddOn.SpoilHeaps.Name");
            ctx.Stat("Placemaker.Stat.Heaps", props, Keys.UnitCount);
        }
    }
}
