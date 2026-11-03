// Logistics yard feature "Break area": where the yard crew sits — a cabin (or shed), picnic tables
// under a small shelter, a bin and a tree or two, tucked into leftover yard space near the edge.

using Placemaker.Core.Layers.AddOns;
using Placemaker.Core.Layers.Common;
using Unity.Mathematics;

namespace Placemaker.Core.Layers.Types.Logistics
{
    [AreaGenerator(261)]
    public sealed class YardBreakAddOn : AddOnLayer
    {
        public const string LayerId = "yardBreak";

        protected override string Area => "AddOn.YardBreak";
        public override string Id => LayerId;
        public override string Icon => "Media/Game/Icons/PropsIndustrial.svg";

        protected override void DefineControls(ControlSet c)
        {
            c.Slider("tables", 2, 1, 6, 1, "count", ControlTier.Essential, "yardBreak");
            c.Toggle("cabin", true, ControlTier.Essential, "yardBreak").Tip();
            c.Toggle("shelter", true, ControlTier.More, "yardBreak");
            c.Toggle("trees", true, ControlTier.More, "yardBreak");
        }

        protected override void PlanCore(LayerContext ctx)
        {
            var occ = Occupancy(ctx);
            int tables = ctx.I("tables");
            float radius = 5f + tables * 0.8f;
            if (!Spots.Find(occ, radius, Spots.Preference.NearBoundary, Hashing.Mix(ctx.Seed, 0xB4EAu), out var c, null, 0f, 0.5f, 2000)
                && !Spots.Find(occ, radius * 0.6f, Spots.Preference.Open, Hashing.Mix(ctx.Seed, 0xB4EBu), out c, null, 0f, 0.5f, 2000))
            {
                NoSpace(ctx, "Placemaker.AddOn.YardBreak.Name");
                return;
            }
            var region = ctx.LayerRegion;
            var toEdge = region.ClosestOnBoundary(c, out _, out _);
            var back = math.normalizesafe(toEdge - c, new float2(0, 1));
            var side = new float2(-back.y, back.x);
            int placed = 0;

            if (ctx.B("cabin"))
            {
                string cat = LeisureKit.Category(ctx, "", AssetCategories.LogisticsCabin, AssetCategories.GardenShed);
                if (cat != null && LeisureKit.Put(ctx, occ, c + back * (radius * 0.55f), Facing(c + back, c), ObjectRole.Furniture, AssetMix.One(""), cat, 2.2f, Hashing.Mix(ctx.Seed, 1u), 0.5f))
                    placed++;
            }
            if (ctx.B("shelter"))
            {
                string cat = LeisureKit.Category(ctx, "", AssetCategories.Shelter);
                if (cat != null && LeisureKit.Put(ctx, occ, c + side * (radius * 0.5f), Facing(c + side, c), ObjectRole.Furniture, AssetMix.One(""), cat, 2.5f, Hashing.Mix(ctx.Seed, 2u), 0.5f))
                    placed++;
            }
            string picnic = LeisureKit.Category(ctx, "", AssetCategories.Picnic, AssetCategories.Bench);
            if (picnic != null)
                for (int i = 0; i < tables; i++)
                {
                    var p = c - back * 1.5f + side * ((i - (tables - 1) * 0.5f) * 3.2f);
                    var role = picnic == AssetCategories.Bench ? ObjectRole.Bench : ObjectRole.Furniture;
                    if (LeisureKit.Put(ctx, occ, p, Facing(p, p + back), role, AssetMix.One(""), role == ObjectRole.Bench ? null : picnic, 1.2f, Hashing.Mix(ctx.Seed, 10u + (uint)i), 0.5f, 0.2f))
                        placed++;
                }
            LeisureKit.Put(ctx, occ, c - back * 1.5f + side * (tables * 1.6f + 1.5f), 0f, ObjectRole.Bin, AssetMix.One(""), null, 0.5f, Hashing.Mix(ctx.Seed, 30u), 0.4f);
            if (ctx.B("trees"))
                for (int i = 0; i < 2; i++)
                {
                    var p = c - side * (radius * 0.6f) + back * (i == 0 ? 2f : -3f);
                    LeisureKit.Put(ctx, occ, p, 0f, ObjectRole.Tree, AssetMix.One(""), null, 2.4f, Hashing.Mix(ctx.Seed, 40u + (uint)i), 1f, 0.3f, 3, 2);
                }
            if (placed == 0) NoSpace(ctx, "Placemaker.AddOn.YardBreak.Name");
        }
    }
}
