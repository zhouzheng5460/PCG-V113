// Skip row feature: a line of skips / dumpsters / bottle banks on a concrete hardstanding, side by
// side with their open ends to the drive — the civic-amenity look. Placed along a straight stretch of
// the outline (or freestanding in the open), only where every skip fits.

using System.Collections.Generic;
using Placemaker.Core.Layers.AddOns;
using Placemaker.Core.Layers.Common;
using Placemaker.Core.Layers.Types.Scrapyard;
using Unity.Mathematics;

namespace Placemaker.Core.Layers.Types.Landfill
{
    [AreaGenerator(1320, Hidden = true)]
    public sealed class SkipRowAddOn : AddOnLayer
    {
        public const string LayerId = "skipRow";

        protected override string Area => "AddOn.SkipRow";
        public override string Id => LayerId;
        public override string Icon => "Media/Game/Icons/WasteRecycling.svg";

        protected override void DefineControls(ControlSet c)
        {
            c.Slider("count", 6, 1, 16, 1, "count", ControlTier.Essential, "skips");
            c.Choice("where", "edge", ControlTier.Essential, "skips", "edge", "open").Tip();
            c.Toggle("pad", true, ControlTier.More, "skips").Tip();
            c.AssetMulti("assets", AssetCategories.LandfillSkip, ControlTier.More, "skips");
            c.RandomRange("count", 3, 10);
        }

        protected override void DefinePresets()
        {
            Preset("bottleBanks", ("count", 4.0), ("where", "open"));
            Preset("civicAmenity", ("count", 10.0), ("where", "edge"));
        }

        // Art pass: whatever the player left automatic gets the curated gritty defaults (GritAssets).
        protected override void PlanCore(LayerContext ctx) { PlanArt(ctx); GritAssets.Apply(ctx.Plan, ctx.Seed); }

        private void PlanArt(LayerContext ctx)
        {
            var ids = ctx.L("assets");
            string cat = YardKit.Cat(ctx, ids, AssetCategories.LandfillSkip, AssetCategories.ScrapContainer);
            if (cat == null) return;
            var region = ctx.LayerRegion;
            var occ = Occupancy(ctx);
            int count = ctx.I("count");
            const float pitch = 3.6f, depth = 2.4f;
            float len = count * pitch;
            var slots = new List<float2>();
            float2 dir = new float2(1, 0), face = new float2(0, 1);
            bool found = false;

            if (ctx.S("where") == "edge")
                for (float s = 0f; s < region.Perimeter && !found; s += 4f)
                {
                    var e = region.PointAt(s, out var t, out var inward, out _);
                    var a = e + inward * (depth * 0.5f + 1.2f) + t * (pitch * 0.5f);
                    var b = a + t * (len - pitch);
                    if (TryRow(region, occ, a, b, count, slots)) { dir = t; face = inward; found = true; }
                }
            if (!found)
            {
                region.PrincipalAxes(out _, out var axis, out _, out _);
                if (Spots.Find(occ, len * 0.5f + 1f, Spots.Preference.Open, Hashing.Mix(ctx.Seed, 0x5C0Bu), out var spot, null, 0f, 1f, 800))
                {
                    var a = spot - axis * (len - pitch) * 0.5f;
                    if (TryRow(region, occ, a, a + axis * (len - pitch), count, slots)) { dir = axis; face = new float2(-axis.y, axis.x); found = true; }
                }
            }
            if (!found) { NoSpace(ctx, "Placemaker.AddOn.SkipRow.Name"); return; }

            if (ctx.B("pad"))
            {
                var mid = math.lerp(slots[0], slots[slots.Count - 1], 0.5f);
                YardKit.Patch(ctx, LayerRegion.Rect(mid, dir, len * 0.5f + 0.8f, depth * 0.5f + 1.4f), SurfaceRole.Paving, AssetMix.One(""), AssetCategories.SurfacePaving);
            }
            int placed = 0;
            for (int i = 0; i < slots.Count; i++)
            {
                uint v = Hashing.Mix(ctx.Seed, 0x5C1Cu + (uint)i);
                if (LeisureKit.Put(ctx, occ, slots[i], LeisureKit.Yaw(slots[i], slots[i] + face), ObjectRole.Furniture, AssetMix.Pick(ids, v), cat, 1.6f, v, 0.4f, 0.05f))
                    placed++;
            }
            ctx.Stat("Placemaker.Stat.Skips", placed, Keys.UnitCount);
        }

        private static bool TryRow(LayerRegion region, Occupancy occ, float2 a, float2 b, int count, List<float2> into)
        {
            into.Clear();
            if (!region.Contains(a) || !region.Contains(b) || region.CrossesBoundary(a, b, false)) return false;
            for (int i = 0; i < count; i++)
            {
                var p = count == 1 ? a : math.lerp(a, b, (float)i / (count - 1));
                if (!occ.Free(p, 1.6f, 0.4f, 0.05f)) { into.Clear(); return false; }
                into.Add(p);
            }
            return true;
        }
    }
}
