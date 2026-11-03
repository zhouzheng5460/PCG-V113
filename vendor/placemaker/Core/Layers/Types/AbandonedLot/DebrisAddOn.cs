// "Debris & junk" feature: dumped heaps (tyres, barrels, pallets, carts) and loose rubble chunks with
// dirt stains around them. Heaps prefer the outline (stuff gets dumped over the fence from the
// street) or open ground. Offered by the decay types; usable by any recipe.

using Placemaker.Core.Layers.Types.Scrapyard;
using Placemaker.Core.Layers.AddOns;
using Placemaker.Core.Layers.Common;
using Unity.Mathematics;

namespace Placemaker.Core.Layers.Types.Ruins
{
    [AreaGenerator(171)]
    public sealed class DebrisAddOn : AddOnLayer
    {
        public const string LayerId = "debris";

        protected override string Area => "AddOn.Debris";
        public override string Id => LayerId;
        public override string Icon => "Media/Game/Icons/Garbage.svg";

        protected override void DefineControls(ControlSet c)
        {
            c.Slider("heaps", 4, 1, 16, 1, "count", ControlTier.Essential, "debris");
            c.Choice("mix", "mixed", ControlTier.Essential, "debris", "junk", "rubble", "mixed").Tip();
            c.Slider("items", 6, 2, 14, 1, "count", ControlTier.More, "debris").Tip();
            c.Choice("where", "edges", ControlTier.More, "debris", "edges", "open").Tip();
            c.Toggle("stains", true, ControlTier.More, "debris").Tip();
            c.AssetMulti("junkAssets", AssetCategories.RuinsJunk, ControlTier.More, "debris");
            c.AssetMulti("rubbleAssets", AssetCategories.RuinsRubble, ControlTier.More, "debris");
            c.RandomRange("heaps", 2, 8);
        }

        protected override void DefinePresets()
        {
            Preset("litter", ("heaps", 8.0), ("items", 3.0), ("mix", "junk"), ("where", "open"));
            Preset("dumped", ("heaps", 3.0), ("items", 12.0), ("mix", "junk"), ("where", "edges"));
            Preset("rubble", ("heaps", 5.0), ("items", 8.0), ("mix", "rubble"), ("where", "open"));
        }

        // Art pass: whatever the player left automatic gets the curated gritty defaults (GritAssets).
        protected override void PlanCore(LayerContext ctx) { PlanArt(ctx); GritAssets.Apply(ctx.Plan, ctx.Seed); }

        private void PlanArt(LayerContext ctx)
        {
            var occ = Occupancy(ctx);
            var guard = Occupancy(ctx);
            string mix = ctx.S("mix");
            string junk = mix != "rubble" ? DecayKit.Cat(ctx, ctx.L("junkAssets"), AssetCategories.RuinsJunk) : null;
            string rubble = mix != "junk" ? DecayKit.Cat(ctx, ctx.L("rubbleAssets"), AssetCategories.RuinsRubble, AssetCategories.Rock) : null;
            if (junk == null && rubble == null) return;
            var pref = ctx.S("where") == "open" ? Spots.Preference.Open : Spots.Preference.NearBoundary;
            var spots = Spread(occ, 3f, ctx.I("heaps"), pref, Hashing.Mix(ctx.Seed, 0xDEB1u), 9f);
            int items = 0, heaps = 0;
            string dirt = ctx.B("stains") && AssetAvailability.Has(AssetCategories.DecalDirt) ? AssetCategories.DecalDirt : null;
            for (int i = 0; i < spots.Count; i++)
            {
                var c = spots[i];
                uint v = Hashing.Mix(ctx.Seed, 0xDEB2u + (uint)i);
                bool useJunk = junk != null && (rubble == null || (v & 1u) == 0u);
                if (dirt != null)
                    for (int k = 0; k < 3; k++)
                    {
                        float a = Hashing.Unit(Hashing.Mix(v, (uint)k)) * 6.283f;
                        DecayKit.Decal(ctx, guard, c + new float2(math.cos(a), math.sin(a)) * (1.5f + k), a, dirt, Hashing.Mix(v, (uint)k + 9u));
                    }
                int n = useJunk
                    ? DecayKit.JunkCluster(ctx, occ, c, ctx.I("items"), ctx.L("junkAssets"), junk, 0xDEB3u + (uint)i * 17u)
                    : DecayKit.JunkCluster(ctx, occ, c, ctx.I("items"), ctx.L("rubbleAssets"), rubble, 0xDEB4u + (uint)i * 17u);
                items += n;
                if (n > 0) heaps++;
            }
            if (heaps == 0) NoSpace(ctx, "Placemaker.AddOn.Debris.Name");
            ctx.Stat("Placemaker.Stat.JunkItems", items, Keys.UnitCount);
        }
    }
}
