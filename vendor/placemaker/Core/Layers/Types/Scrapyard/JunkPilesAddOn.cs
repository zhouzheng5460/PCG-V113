// Junk piles feature: a few heaps of scrap, tyres, drums or garbage dropped into free space, each
// with oil/dirt decals round its foot and a stray item or two that rolled off. Meant for any gritty
// type (scrapyard, landfill, abandoned lot, construction site), but it works in a park too — for
// the player who wants a neglected corner.

using System.Collections.Generic;
using Placemaker.Core.Layers.AddOns;
using Placemaker.Core.Layers.Common;
using Unity.Mathematics;

namespace Placemaker.Core.Layers.Types.Scrapyard
{
    [AreaGenerator(1310, Hidden = true)]
    public sealed class JunkPilesAddOn : AddOnLayer
    {
        public const string LayerId = "junkPiles";
        public static readonly string[] Mixes = { "mixed", "scrap", "tires", "drums", "garbage" };

        protected override string Area => "AddOn.JunkPiles";
        public override string Id => LayerId;
        public override string Icon => "Media/Game/Icons/PropsIndustrial.svg";

        protected override void DefineControls(ControlSet c)
        {
            c.Slider("piles", 4, 1, 20, 1, "count", ControlTier.Essential, "junk");
            c.Choice("mix", "mixed", ControlTier.Essential, "junk", Mixes).Tip();
            c.Choice("size", "medium", ControlTier.Essential, "junk", "small", "medium", "large");
            c.Toggle("grime", true, ControlTier.More, "junk").Tip();
            c.Toggle("strays", true, ControlTier.More, "junk").Tip();
            c.AssetMulti("assets", AssetCategories.ScrapHeap, ControlTier.More, "junk");
            c.RandomRange("piles", 2, 8);
        }

        protected override void DefinePresets()
        {
            Preset("neglected", ("piles", 3.0), ("mix", "mixed"), ("size", "small"));
            Preset("tyreMountain", ("piles", 2.0), ("mix", "tires"), ("size", "large"));
        }

        private static string[] Chain(string mix)
        {
            switch (mix)
            {
                case "scrap": return new[] { AssetCategories.ScrapHeap, AssetCategories.ScrapWreck };
                case "tires": return new[] { AssetCategories.ScrapTire, AssetCategories.ScrapHeap };
                case "drums": return new[] { AssetCategories.ScrapBarrel, AssetCategories.ScrapPallet };
                case "garbage": return new[] { AssetCategories.LandfillGarbage, AssetCategories.ScrapHeap };
                default: return new[] { AssetCategories.ScrapHeap, AssetCategories.ScrapTire, AssetCategories.ScrapBarrel, AssetCategories.ScrapPallet };
            }
        }

        // Art pass: whatever the player left automatic gets the curated gritty defaults (GritAssets).
        protected override void PlanCore(LayerContext ctx) { PlanArt(ctx); GritAssets.Apply(ctx.Plan, ctx.Seed); }

        private void PlanArt(LayerContext ctx)
        {
            var ids = ctx.L("assets");
            var chain = Chain(ctx.S("mix"));
            string first = YardKit.Cat(ctx, ids, chain);
            if (first == null) return;
            var occ = Occupancy(ctx);
            var reservedOnly = Occupancy(ctx);
            float R = ctx.S("size") == "small" ? 2.5f : ctx.S("size") == "large" ? 7f : 4f;
            var spots = Spread(occ, R + 1f, ctx.I("piles"), Spots.Preference.Open, Hashing.Mix(ctx.Seed, 0x7A4Bu), R * 3f);
            int items = 0, piles = 0;
            bool mixed = ctx.S("mix") == "mixed";
            string dirt = YardKit.CatQuiet(AssetCategories.DecalLitter, AssetCategories.DecalDirt, AssetCategories.Decal);
            foreach (var c in spots)
            {
                uint s = Hashing.Mix(ctx.Seed, (uint)piles * 613u + 5u);
                // A mixed pile is mostly one thing with a bit of everything else.
                string cat = first;
                if (mixed)
                {
                    var pick = chain[(int)(s % (uint)chain.Length)];
                    if (AssetAvailability.Has(pick)) cat = pick;
                }
                int n = (int)(R * R * 0.6f) + 3;
                int got = YardKit.Heap(ctx, occ, c, R, n, cat == chain[0] ? ids : null, cat, cat == AssetCategories.ScrapWreck ? 2.4f : 1.3f, s);
                if (got == 0) continue;
                items += got; piles++;
                if (ctx.B("grime")) YardKit.Grime(ctx, reservedOnly, c, R + 2.5f, (int)(R * 2f) + 2, dirt, Hashing.Mix(s, 9u));
                if (ctx.B("strays"))
                    for (int i = 0; i < 3; i++)
                    {
                        uint v = Hashing.Mix(s, 20u + (uint)i);
                        float a = Hashing.Unit(v) * 6.283f;
                        var p = c + new float2(math.cos(a), math.sin(a)) * (R + 1.5f + 2f * Hashing.Unit(Hashing.Mix(v, 1u)));
                        string sc = YardKit.CatQuiet(chain[(i + 1) % chain.Length], first);
                        if (sc != null && LeisureKit.Put(ctx, occ, p, a, ObjectRole.Furniture, AssetMix.One(""), sc, 0.7f, v, 0.4f, 0.2f)) items++;
                    }
            }
            if (piles == 0) NoSpace(ctx, "Placemaker.AddOn.JunkPiles.Name");
            ctx.Stat("Placemaker.Stat.Heaps", piles, Keys.UnitCount);
            ctx.Stat("Placemaker.Stat.JunkItems", items, Keys.UnitCount);
        }
    }
}
