// Viewpoint: a few look-out spots along the rim of a region, each a bench facing outward with a
// telescope beside it and an information board behind. The lookout hill offers it on its summit;
// in any park it lines the edge facing the view out of the park.

using System.Collections.Generic;
using Placemaker.Core.Layers.AddOns;
using Placemaker.Core.Layers.Common;
using Unity.Mathematics;

namespace Placemaker.Core.Layers.Types.LookoutHill
{
    [AreaGenerator(248)]
    public sealed class ViewpointAddOn : AddOnLayer
    {
        public const string LayerId = "viewpoint";
        protected override string Area => "AddOn.Viewpoint";
        public override string Id => LayerId;
        public override string Icon => "Media/Game/Icons/TerrainHeight.svg";
        public override string[] Accepts => new[] { RegionTags.Green, RegionTags.Paved, RegionTags.Core, LookoutHillLayer.SummitTag };

        protected override void DefineControls(ControlSet c)
        {
            c.Slider("spots", 2, 1, 6, 1, "count", ControlTier.Essential, "summit");
            c.Toggle("telescope", false, ControlTier.Essential, "summit").Tip();   // off: no vanilla telescope exists (art pass a3)
            c.Toggle("board", true, ControlTier.More, "summit").Tip();
            c.Asset("telescopeAsset", AssetCategories.HillTelescope, ControlTier.More, "summit").When("telescope=true");
            c.Asset("boardAsset", AssetCategories.HillBoard, ControlTier.More, "summit").When("board=true");
        }

        protected override void PlanCore(LayerContext ctx)
        {
            var occ = Occupancy(ctx);
            var region = ctx.LayerRegion;
            var centre = region.Centroid;
            int wanted = ctx.I("spots");
            // Candidate spots on the rim, evenly around; each needs room for the bench and its extras.
            var candidates = new List<float2>();
            float phase = Hashing.Unit(ctx.Seed) * 6.283f;
            for (int i = 0; i < 24; i++)
            {
                float a = phase + i * math.PI / 12f;
                var dir = new float2(math.cos(a), math.sin(a));
                // Walk outward from the centre to the last point that keeps 1.2 m from the outline.
                float2 last = centre; bool any = false;
                for (float s = 0.5f; s < 400f; s += 0.5f)
                {
                    var p = centre + dir * s;
                    if (!region.ContainsDisc(p, 1.2f)) break;
                    last = p; any = true;
                }
                if (any) candidates.Add(last);
            }
            var chosen = new List<float2>();
            float minGap = math.max(3f, region.Perimeter / math.max(1, wanted) * 0.5f);
            string scope = ctx.B("telescope") ? LeisureKit.Category(ctx, ctx.S("telescopeAsset"), AssetCategories.HillTelescope) : null;
            // No vanilla panorama board: the park information signs stand in while the board asset is automatic (art pass a3).
            bool pickedBoard = !string.IsNullOrEmpty(ctx.S("boardAsset"));
            string board = !ctx.B("board") ? null : pickedBoard ? AssetCategories.HillBoard : LeisureKit.Category(ctx, "", AssetCategories.TrailSign);
            var boardAsset = pickedBoard ? AssetMix.One(ctx.S("boardAsset")) : new AssetRef("", "ParkInfosign02", "ParkInfosign03");
            int extras = 0;
            for (int i = 0; i < candidates.Count && chosen.Count < wanted; i++)
            {
                var p = candidates[(i * 7) % candidates.Count];
                bool far = true;
                foreach (var q in chosen) if (math.distance(p, q) < minGap) { far = false; break; }
                if (!far || !occ.Free(p, 1f, 0.3f, 0.3f)) continue;
                var outward = math.normalizesafe(p - centre, new float2(0, 1));
                var side = new float2(-outward.y, outward.x);
                uint v = Hashing.Mix(ctx.Seed, (uint)i + 0x71Eu);
                ctx.Object(p, Facing(p, p + outward), ObjectRole.Bench, new AssetRef("", "Pack10-Bench01", "GardenBench01"), v);
                occ.Take(p, 1f, 1);
                chosen.Add(p);
                if (scope != null && LeisureKit.Put(ctx, occ, p + side * 1.6f, Facing(p, p + outward), ObjectRole.Furniture, AssetMix.One(ctx.S("telescopeAsset")), scope, 0.4f,
                    Hashing.Mix(v, 1u), 0.3f, 0.2f)) extras++;
                if (board != null && LeisureKit.Put(ctx, occ, p - outward * 1.8f - side * 0.8f, Facing(p, p + outward), ObjectRole.Furniture, boardAsset, board, 0.5f,
                    Hashing.Mix(v, 2u), 0.3f, 0.2f)) extras++;
            }
            if (chosen.Count == 0) NoSpace(ctx, "Placemaker.AddOn.Viewpoint.Name");
            ctx.Stat(Keys.StatBenches, chosen.Count, Keys.UnitCount);
            ctx.Plan.Facts["viewpoints"] = chosen.Count;
            ctx.Plan.Facts["viewpointExtras"] = extras;
        }
    }
}
