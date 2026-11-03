// Hiking trail feature "Backcountry camp": a small primitive campsite in a clearing near the trail —
// tent pitches around a fire ring, log benches, an optional lean-to, on worn dirt ground.

using Placemaker.Core.Layers.AddOns;
using Placemaker.Core.Layers.Common;
using Unity.Mathematics;

namespace Placemaker.Core.Layers.Types.Hiking
{
    [AreaGenerator(271)]
    public sealed class TrailCampAddOn : AddOnLayer
    {
        public const string LayerId = "trailCamp";

        protected override string Area => "AddOn.TrailCamp";
        public override string Id => LayerId;
        public override string Icon => "Media/Game/Icons/Trees.svg";

        protected override void DefineControls(ControlSet c)
        {
            c.Slider("pitches", 3, 1, 8, 1, "count", ControlTier.Essential, "trailCamp").Tip();
            c.Toggle("fire", true, ControlTier.Essential, "trailCamp");
            c.Toggle("shelter", false, ControlTier.More, "trailCamp").Tip();
            c.Toggle("ground", true, ControlTier.More, "trailCamp");
        }

        protected override void PlanCore(LayerContext ctx)
        {
            var occ = Occupancy(ctx);
            int pitches = ctx.I("pitches");
            float R = 6f + pitches * 0.9f;
            if (!Spots.Find(occ, R, Spots.Preference.NearPaths, Hashing.Mix(ctx.Seed, 0xCA3Bu), out var c, null, 0f, 1f, 2000)
                && !Spots.Find(occ, R * 0.7f, Spots.Preference.Open, Hashing.Mix(ctx.Seed, 0xCA3Cu), out c, null, 0f, 1f, 2000))
            {
                NoSpace(ctx, "Placemaker.AddOn.TrailCamp.Name");
                return;
            }
            if (ctx.B("ground"))
            {
                string cat = AssetAvailability.Has(AssetCategories.SurfaceDirt) ? AssetCategories.SurfaceDirt : AssetCategories.SurfaceNatural;
                ctx.Surface(LeisureKit.Blob(c, R * 0.85f, Hashing.Mix(ctx.Seed, 0xCA3Du), 14, 0.2f), SurfaceRole.Ground, AssetMix.One(""), cat);
            }
            int placed = 0;
            if (ctx.B("fire"))
            {
                // No vanilla fire pit (the camping fallback is a tent): a ring of small stones (art pass a3).
                if (AssetAvailability.Has(AssetCategories.Rock) && occ.Free(c, 1.3f, 0.5f, 0.2f))
                {
                    for (int k = 0; k < 7; k++)
                    {
                        float a = k * 0.8976f;
                        var o = ctx.Object(c + new float2(math.cos(a), math.sin(a)) * 0.85f, a, ObjectRole.Furniture,
                            new AssetRef("", "Rock05", "Rock12", "Rock21"), Hashing.Mix(ctx.Seed, 60u + (uint)k), -1, AssetCategories.Rock);
                        o.Scale = 0.55f;
                    }
                    occ.Take(c, 1.3f, 1);
                    placed++;
                    for (int k = 0; k < 2; k++)
                    {
                        float a = k * math.PI + 0.6f;
                        var p = c + new float2(math.cos(a), math.sin(a)) * 2.8f;
                        if (LeisureKit.Put(ctx, occ, p, Facing(p, c), ObjectRole.Bench, new AssetRef("", "CampgroundBench01", "CampgroundBench02"), null, 1.1f, Hashing.Mix(ctx.Seed, 2u + (uint)k), 0.5f, 0.2f)) placed++;
                    }
                }
            }
            string tent = LeisureKit.Category(ctx, "", AssetCategories.CampingPitch, AssetCategories.Camping);
            if (tent != null)
                foreach (var p in RingPoints(c, R * 0.62f, pitches, Hashing.Unit(Hashing.Mix(ctx.Seed, 7u)) * 6.283f))
                    if (LeisureKit.Put(ctx, occ, p, Facing(p, c), ObjectRole.Furniture, AssetMix.One(""), tent, 1.8f, Hashing.Mix(ctx.Seed, (uint)(p.x * 7f)), 0.5f, 0.3f)) placed++;
            if (ctx.B("shelter"))
            {
                string sh = LeisureKit.Category(ctx, "", AssetCategories.Shelter);
                var p = c + new float2(0f, -R * 0.75f);
                if (sh != null && LeisureKit.Put(ctx, occ, p, Facing(p, c), ObjectRole.Furniture, new AssetRef("", "CampgroundShelter02", "CampgroundShelter01"), sh, 2.4f, Hashing.Mix(ctx.Seed, 9u), 0.5f)) placed++;
            }
            if (placed == 0) NoSpace(ctx, "Placemaker.AddOn.TrailCamp.Name");
            ctx.Stat("Placemaker.Stat.CampPitches", pitches, Keys.UnitCount);
        }
    }
}
