// Wildlife habitat (feature of the wetland, usable in any green region): quiet corners away from the
// paths get habitat piles — a log/stone pile (small rocks), nest or bat boxes and an insect hotel —
// plus wildlife spawners. Nothing goes near paths (the "quiet distance"), so visitors see the
// habitat from the trail without trampling it.

using System.Collections.Generic;
using Placemaker.Core.Layers.AddOns;
using Placemaker.Core.Layers.Common;
using Unity.Mathematics;

namespace Placemaker.Core.Layers.Types.Wetland
{
    [AreaGenerator(612)]
    public sealed class WildlifeHabitatAddOn : AddOnLayer
    {
        public const string LayerId = "wildlifeHabitat";

        protected override string Area => "AddOn.Wildlife";
        public override string Id => LayerId;
        public override string Icon => "Media/Game/Icons/Animal.svg";
        public override string[] Accepts => new[] { RegionTags.Green, RegionTags.Core, RegionTags.Root };

        protected override void DefineControls(ControlSet c)
        {
            c.Slider("corners", 3, 1, 10, 1, "count", ControlTier.Essential, "wildlife").Tip();
            c.Slider("quiet", 8, 3, 25, 1, "m", ControlTier.Essential, "wildlife").Tip();
            c.Toggle("nestBoxes", true, ControlTier.More, "wildlife").Tip();
            c.Toggle("piles", true, ControlTier.More, "wildlife").Tip();
            c.Toggle("spawners", true, ControlTier.More, "wildlife");
            c.Asset("nestAsset", AssetCategories.WetlandNestBox, ControlTier.More, "wildlife").When("nestBoxes=true");
            c.RandomRange("corners", 2, 5);
        }

        protected override void DefinePresets()
        {
            Preset("birds", ("corners", 4.0), ("piles", false));
            Preset("insects", ("corners", 3.0), ("nestBoxes", true), ("piles", true), ("spawners", false));
        }

        protected override void PlanCore(LayerContext ctx)
        {
            var occ = Occupancy(ctx);
            var region = ctx.LayerRegion;
            float quiet = ctx.F("quiet");
            int wanted = ctx.I("corners");
            float minDistance = math.max(15f, math.sqrt(region.Area) / (wanted + 1));
            // Quiet corners: open spots as far from paths as the quiet distance asks.
            var corners = new List<float2>();
            for (int i = 0; i < wanted * 3 && corners.Count < wanted; i++)
            {
                if (!Spots.Find(occ, 3f, Spots.Preference.Open, Hashing.Mix(ctx.Seed, 0xC0E0u + (uint)i), out var s, corners, minDistance, 1f, 1200)) break;
                if (occ.Paths.Count > 0 && occ.Paths.Clearance(s, quiet + 1f) < quiet) { occ.Take(s, 2f, 9); continue; }
                corners.Add(s);
            }
            string nestCat = ctx.B("nestBoxes") ? LeisureKit.Category(ctx, ctx.S("nestAsset"), AssetCategories.WetlandNestBox) : null;
            string rockCat = ctx.B("piles") && AssetAvailability.Has(AssetCategories.Rock) ? AssetCategories.Rock : null;
            int items = 0;
            for (int i = 0; i < corners.Count; i++)
            {
                var c = corners[i];
                uint v = Hashing.Mix(ctx.Seed, 0xAB17u + (uint)i);
                if (nestCat != null && LeisureKit.Put(ctx, occ, c, Hashing.Unit(v) * 6.283f, ObjectRole.Furniture, AssetMix.One(ctx.S("nestAsset")), nestCat, 1f, v)) items++;
                if (rockCat != null)
                    for (int k = 0; k < 3; k++)
                    {
                        float a = Hashing.Unit(Hashing.Mix(v, (uint)k)) * 6.283f;
                        var p = c + new float2(math.cos(a), math.sin(a)) * (1.8f + k * 0.5f);
                        if (!occ.Free(p, 0.6f, 0.5f, 0.1f)) continue;
                        var o = ctx.Object(p, a, ObjectRole.Furniture, AssetMix.One(""), Hashing.Mix(v, (uint)k + 7u), -1, rockCat);
                        o.Scale = 0.45f;
                        occ.Take(p, 0.6f, 2);
                        items++;
                    }
                if (ctx.B("spawners"))
                {
                    var p = c + new float2(0f, 3.5f);
                    if (occ.Free(p, 1f, 1f, 0.2f))
                    {
                        ctx.Object(p, 0f, ObjectRole.AnimalSpawner, new AssetRef("", "Forest Animal Spawner"), Hashing.Mix(v, 21u));
                        occ.Take(p, 1f, 5);
                    }
                }
            }
            if (corners.Count == 0) NoSpace(ctx, "Placemaker.AddOn.Wildlife.Name");
            ctx.Stat("Placemaker.Stat.HabitatCorners", corners.Count, Keys.UnitCount);
            ctx.Plan.Facts["habitatItems"] = items;
        }
    }
}
