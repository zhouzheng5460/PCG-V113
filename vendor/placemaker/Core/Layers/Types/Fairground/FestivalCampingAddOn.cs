// Festival camping feature: a campsite in the biggest free piece of an event site — a straight
// lane through it, tent pitches in rows on both sides of the lane (tents / caravans), a toilet
// block at the lane head and a few campfires. Tents come from the camping pitch category.

using System.Collections.Generic;
using Placemaker.Core.Layers.AddOns;
using Placemaker.Core.Layers.Common;
using Unity.Mathematics;

namespace Placemaker.Core.Layers.Types.Events
{
    [AreaGenerator(172)]
    public sealed class FestivalCampingAddOn : AddOnLayer
    {
        public const string LayerId = "festivalCamping";

        protected override string Area => "AddOn.FestivalCamping";
        public override string Id => LayerId;
        public override string Icon => "Media/Game/Icons/Forest.svg";
        public override float MinArea => 200f;

        protected override void DefineControls(ControlSet c)
        {
            c.Slider("pitches", 40, 4, 200, 2, "count", ControlTier.Essential, "festCamping");
            c.Slider("pitch", 6, 4, 12, 0.5, "m", ControlTier.Essential, "festCamping").Tip();
            c.Toggle("toilets", true, ControlTier.More, "festCamping").Tip();
            c.Slider("fires", 3, 0, 12, 1, "count", ControlTier.More, "festCamping");
            c.AssetMulti("tentAssets", AssetCategories.CampingPitch, ControlTier.More, "festCamping");
            c.RandomRange("pitches", 16, 60);
        }

        protected override void PlanCore(LayerContext ctx)
        {
            var tents = ctx.L("tentAssets");
            if (!AssetAvailability.Require(ctx.Plan, AssetCategories.CampingPitch, tents, Keys.Category(AssetCategories.CampingPitch))) return;
            var occ = Occupancy(ctx);
            var region = ctx.LayerRegion;
            float pitch = ctx.F("pitch");
            int wanted = ctx.I("pitches");
            // Camp centre: the most open spot; the lane runs along the region's long axis through it.
            if (!Spots.Find(occ, pitch * 1.5f, Spots.Preference.Open, ctx.Seed, out var c, null, 0f, 1f, 1500)) { NoSpace(ctx, "Placemaker.Feature.festivalCamping"); return; }
            LeisureKit.Frame(region, out _, out var axis, out var side, out _, out _);
            var tentR = pitch * 0.4f;
            int placed = 0;
            // Rows of pitches across a grid centred on c, nearest first (so a small count stays compact).
            var cells = new List<(float d, float2 p)>();
            int reach = math.clamp((int)math.ceil(math.sqrt(wanted) * 1.6f) + 2, 3, 40);
            for (int i = -reach; i <= reach; i++)
                for (int j = -reach; j <= reach; j++)
                {
                    if (j == 0) continue;   // the lane
                    var p = c + axis * (i * pitch) + side * (j * pitch - math.sign(j) * pitch * 0.25f);
                    cells.Add((math.distancesq(p, c), p));
                }
            cells.Sort((x, y) => x.d != y.d ? x.d.CompareTo(y.d) : x.p.x != y.p.x ? x.p.x.CompareTo(y.p.x) : x.p.y.CompareTo(y.p.y));
            var laneA = c; var laneB = c;
            foreach (var (_, p) in cells)
            {
                if (placed >= wanted) break;
                if (!occ.Free(p, tentR, 0.8f, 0.4f)) continue;
                uint v = Hashing.Mix(ctx.Seed, 0xCA00u + (uint)placed);
                float facing = math.dot(p - c, side) > 0 ? math.atan2(-side.x, -side.y) : math.atan2(side.x, side.y);
                ctx.Object(p, facing, ObjectRole.Furniture, AssetMix.Pick(tents, v), v, -1, AssetCategories.CampingPitch);
                occ.Take(p, tentR, 1);
                float u = math.dot(p - c, axis);
                if (u < math.dot(laneA - c, axis)) laneA = c + axis * u;
                if (u > math.dot(laneB - c, axis)) laneB = c + axis * u;
                placed++;
            }
            if (placed == 0) { NoSpace(ctx, "Placemaker.Feature.festivalCamping"); return; }
            // Lane through the camp (kept short of the outline).
            if (math.distance(laneA, laneB) > 4f && region.ContainsDisc(laneA, 1f) && region.ContainsDisc(laneB, 1f) && !region.CrossesBoundary(laneA, laneB, false))
                LeisureKit.Path(ctx, occ, laneA, laneB, NetRole.Pathway, AssetMix.One(""));
            if (ctx.B("toilets"))
            {
                string cat = EventKit.Optional("", AssetCategories.EventToilet, AssetCategories.Restroom);
                EventKit.CabinRow(ctx, occ, cat, AssetMix.One(""), math.clamp(placed / 10, 2, 10), 1.6f, Hashing.Mix(ctx.Seed, 0x7011u), null, 0f);
            }
            int fires = ctx.I("fires");
            if (fires > 0)
            {
                string fire = EventKit.Optional("", AssetCategories.CampingFire);
                var spots = new List<float2>();
                for (int i = 0; i < fires && fire != null; i++)
                {
                    if (!Spots.Find(occ, 2f, Spots.Preference.Open, Hashing.Mix(ctx.Seed, 0xF1u + (uint)i), out var s, spots, pitch * 3f, 1f, 800)) break;
                    if (EventKit.Put(ctx, occ, s, 0f, fire, AssetMix.One(""), 1.2f, Hashing.Mix(ctx.Seed, 0xF100u + (uint)i))) spots.Add(s);
                }
            }
            ctx.Stat("Placemaker.Stat.Pitches", placed, Keys.UnitCount);
        }
    }
}
