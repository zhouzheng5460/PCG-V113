// Restrooms add-on: one or more restroom buildings beside paths (near where people enter),
// spread apart, facing the path; optional kiosk next to each. Without paths in the region they
// go near the outline (street frontage) instead.

using System.Collections.Generic;
using Placemaker.Core.Layers.Common;
using Unity.Mathematics;

namespace Placemaker.Core.Layers.AddOns
{
    [AreaGenerator(120)]
    public sealed class RestroomsAddOn : AddOnLayer
    {
        protected override string Area => "AddOn.Restrooms";
        public override string Id => "restrooms";
        public override string Icon => "Media/Game/Icons/Sewage.svg";

        protected override void DefineControls(ControlSet c)
        {
            c.Slider("count", 3, 1, 6, 1, "count", ControlTier.Essential, "restrooms").Tip();
            c.Toggle("byArea", true, ControlTier.Essential, "restrooms").Tip();
            c.Slider("haEach", 3, 0.5, 10, 0.5, "ha", ControlTier.More, "restrooms").When("byArea=true").Tip();
            c.Asset("asset", AssetCategories.Restroom, ControlTier.Essential, "restrooms");
            c.Toggle("kiosk", false, ControlTier.More, "extras").Tip();
            c.Asset("kioskAsset", AssetCategories.Kiosk, ControlTier.More, "extras").When("kiosk=true");
            c.RandomRange("count", 1, 2);
            c.Fixed("byArea");
            c.Chance("kiosk", 0.4f);
        }

        protected override void PlanCore(LayerContext ctx)
        {
            if (!AssetAvailability.Require(ctx.Plan, AssetCategories.Restroom, new[] { ctx.S("asset") }, Keys.Category(AssetCategories.Restroom)))
                return;
            var occ = Occupancy(ctx);
            var asset = AddOnAssets.Or(ctx.S("asset"), AddOnAssets.Restroom);
            float radius = AddOnAssets.Radius(ctx.Site, ctx.S("asset"), AddOnAssets.Restroom, 2f);
            bool kiosk = ctx.B("kiosk") && AssetAvailability.Require(ctx.Plan, AssetCategories.Kiosk, new[] { ctx.S("kioskAsset") }, Keys.Category(AssetCategories.Kiosk));
            var kioskAsset = AddOnAssets.Or(ctx.S("kioskAsset"), AddOnAssets.Kiosks);
            float kioskRadius = AddOnAssets.Radius(ctx.Site, ctx.S("kioskAsset"), AddOnAssets.Kiosks, 2.5f);
            var paving = AssetMix.One("", AddOnAssets.Paving);
            // One per N hectares (at least one), capped by the count — or exactly the count.
            int wanted = ctx.B("byArea")
                ? math.clamp((int)math.ceil(ctx.LayerRegion.Area / (math.max(0.5f, ctx.F("haEach")) * 10000f) - 0.25f), 1, ctx.I("count"))
                : ctx.I("count");
            float minDistance = math.max(30f, math.sqrt(ctx.LayerRegion.Area) / (wanted + 1));
            // Beside a path, the first near an entrance (people look for one on arrival), the others
            // spread over the network. Without paths: near the outline (street frontage).
            float footprint = radius + (kiosk ? kioskRadius * 2f + 0.5f : 0f);
            var anchors = PathAnchors.Beside(occ, footprint, 6f, 1.2f);
            var spots = new List<float2>();
            var picked = PathAnchors.Pick(anchors, 1, minDistance, true);
            if (picked.Count > 0 && wanted > 1)
            {
                var rest = PathAnchors.Pick(anchors, wanted, minDistance, false);
                picked = rest.Count > 0 && rest[0].Position.Equals(picked[0].Position) ? rest : picked;
            }
            foreach (var a in picked) spots.Add(a.Position);
            if (spots.Count < wanted)
                foreach (var s in Spread(occ, footprint, wanted - spots.Count, occ.Paths.Count > 0 ? Spots.Preference.NearPaths : Spots.Preference.NearBoundary, ctx.Seed, minDistance))
                {
                    bool far = true;
                    foreach (var q in spots) if (math.distance(q, s) < minDistance) far = false;
                    if (far) spots.Add(s);
                }
            int placed = 0;
            foreach (var spot in spots)
            {
                // Face the nearest path (or the region centre): the door should open to where people walk.
                var toward = occ.Paths.Count > 0 && occ.Paths.Nearest(spot, 500f, out var q) >= 0 ? q : ctx.LayerRegion.Centroid;
                float yaw = Facing(spot, toward);
                if (!occ.Free(spot, radius, 0.5f, 0.3f)) continue;
                ctx.Object(spot, yaw, ObjectRole.Furniture, asset, Hashing.Mix(ctx.Seed, (uint)placed), -1, AssetCategories.Restroom);
                occ.Take(spot, radius, 0);
                placed++;
                var fwd = math.normalizesafe(toward - spot, new float2(0, 1));
                var side = new float2(fwd.y, -fwd.x);   // perpendicular to the facing direction
                float kioskSide = 0f;
                if (kiosk)
                    foreach (float s in new[] { 1f, -1f })
                    {
                        var k = spot + side * s * (radius + kioskRadius + 0.5f);
                        if (!occ.Free(k, kioskRadius, 0.5f, 0.3f)) continue;
                        ctx.Object(k, yaw, ObjectRole.Furniture, kioskAsset, Hashing.Mix(ctx.Seed, 0x4B00u + (uint)placed), -1, AssetCategories.Kiosk);
                        occ.Take(k, kioskRadius, 1);
                        kioskSide = s;
                        break;
                    }
                // A small paved forecourt under the block (and its kiosk) reaching the path, a bin by
                // the door on the side away from the kiosk: a facility, not a shed on the grass.
                float halfW = radius + 1.2f + (kioskSide != 0f ? kioskRadius + 0.5f : 0f);
                var padCentre = spot + side * kioskSide * (kioskRadius + 0.5f) * 0.5f + fwd * 0.6f;
                AddOnKit.Pave(ctx, LayerRegion.Rect(padCentre, side, halfW, radius + 1.6f), SurfaceRole.Paving, paving, AssetCategories.SurfacePaving);
                if (occ.Paths.Count > 0) AddOnKit.Link(ctx, toward, spot, radius + 1.4f, 1.3f, paving, AssetCategories.SurfacePaving, 12f);
                float binSide = kioskSide != 0f ? -kioskSide : 1f;
                LeisureKit.Put(ctx, occ, spot + fwd * (radius + 0.9f) + side * binSide * (radius + 0.4f), yaw, ObjectRole.Bin, AssetMix.One("", AddOnAssets.Bin), null, 0.4f,
                    Hashing.Mix(ctx.Seed, 0xB1Bu + (uint)placed), 0.2f, 0.1f);
            }
            if (placed == 0) NoSpace(ctx, "Placemaker.AddOn.Restrooms.Name");
            else if (placed < wanted) ctx.Issue(IssueSeverity.Info, "Placemaker.AddOn.Restrooms.Issue.Fewer", placed, wanted);
            ctx.Stat(Keys.StatRestrooms, placed, Keys.UnitCount);
        }
    }
}
