// Playground add-on: a cluster of play equipment on a soft surface near a path (parents watch
// from the path and the benches), optionally fenced with one gate towards the path.

using System;
using System.Collections.Generic;
using Placemaker.Core.Layers.Common;
using Unity.Mathematics;

namespace Placemaker.Core.Layers.AddOns
{
    [AreaGenerator(110)]
    public sealed class PlaygroundAddOn : AddOnLayer
    {
        protected override string Area => "AddOn.Playground";
        public override string Id => "playground";
        public override string Icon => "Media/Game/Icons/ParksAndRecreation.svg";

        private static readonly string[] Sizes = { "small", "medium", "large" };

        protected override void DefineControls(ControlSet c)
        {
            c.Choice("size", "medium", ControlTier.Essential, "playground", Sizes);
            c.AssetMulti("equipment", AssetCategories.Playground, ControlTier.Essential, "playground");
            c.Toggle("surface", true, ControlTier.More, "ground");
            c.Asset("surfaceAsset", AssetCategories.SurfaceNatural, ControlTier.More, "ground").When("surface=true");
            c.Toggle("benches", true, ControlTier.More, "extras");
            c.Asset("benchAsset", AssetCategories.Bench, ControlTier.More, "extras").When("benches=true");
            c.Toggle("fence", false, ControlTier.More, "extras");
            c.Asset("fenceAsset", AssetCategories.Fence, ControlTier.More, "extras").When("fence=true");
            c.Chance("fence", 0.3f);
        }

        protected override void DefinePresets()
        {
            Preset("tots", ("size", "small"), ("fence", true));
            Preset("adventure", ("size", "large"), ("fence", false));
        }

        protected override void PlanCore(LayerContext ctx)
        {
            var ids = ctx.L("equipment");
            if (!AssetAvailability.Require(ctx.Plan, AssetCategories.Playground, ids, Keys.Category(AssetCategories.Playground))) return;
            var occ = Occupancy(ctx);
            int sizeIndex = Math.Max(0, Array.IndexOf(Sizes, ctx.S("size")));
            float itemRadius = AssetMix.MaxFootprint(ctx.Site, ids, 3f);
            // small → 3 pieces; below that 2, then 1 (real equipment footprints are large: a lone piece beats "no space").
            for (int si = sizeIndex; si >= -2; si--)
            {
                int items = si < 0 ? 3 + si : si == 0 ? 3 : si == 1 ? 5 : 8;
                // Phyllotaxis packing radius for `items` discs of itemRadius + 1.2 m play space between them.
                float spacing = itemRadius * 2f + 1.2f;
                float cluster = spacing * 0.55f * math.sqrt(items) + itemRadius + 1f;
                float outer = cluster + (ctx.B("benches") ? 3f : 1f);
                if (!Spots.Find(occ, outer, Spots.Preference.NearPaths, ctx.Seed, out var centre, null, 0f, 2f)) continue;
                if (si != sizeIndex) ctx.Issue(IssueSeverity.Info, "Placemaker.AddOn.Playground.Issue.Smaller");
                Build(ctx, occ, centre, items, itemRadius, spacing, cluster, ids);
                return;
            }
            NoSpace(ctx, "Placemaker.AddOn.Playground.Name");
        }

        private static void Build(LayerContext ctx, Occupancy occ, float2 centre, int items, float itemRadius, float spacing,
            float cluster, string[] ids)
        {
            // Where people come from: the nearest path, else the region centre.
            var toward = occ.Paths.Count > 0 && occ.Paths.Nearest(centre, 200f, out var onPath) >= 0 ? onPath : ctx.LayerRegion.Centroid;
            var dir = math.normalizesafe(toward - centre, new float2(0, 1));
            if (ctx.B("surface"))
            {
                // Soft fall surface (bark mulch, else sand) under the equipment; a paved strip joins it to the
                // path where the parents' benches stand.
                ctx.Surface(LayerRegion.Circle(centre, cluster, 20, Hashing.Unit(ctx.Seed)), SurfaceRole.Decorative,
                    AddOnAssets.Or(ctx.S("surfaceAsset"), AddOnAssets.PlaySurface), AssetCategories.SurfaceNatural);
                if (occ.Paths.Count > 0)
                    AddOnKit.Link(ctx, toward, centre, cluster, 1.4f, AssetMix.One("", AddOnAssets.Paving), AssetCategories.SurfacePaving, 12f);
            }

            int placed = 0;
            const float golden = 2.39996323f;
            float phase = Hashing.Unit(Hashing.Mix(ctx.Seed, 7u)) * 2f * math.PI;
            for (int i = 0; i < items * 3 && placed < items; i++)
            {
                float r = spacing * 0.55f * math.sqrt(i + 0.5f);
                if (r + itemRadius > cluster) break;
                float a = phase + i * golden;
                var p = centre + new float2(math.cos(a), math.sin(a)) * r;
                if (!occ.Free(p, itemRadius, 0.5f, 0.6f)) continue;
                uint v = Hashing.Mix(ctx.Seed, (uint)(placed * 7919 + 1));
                ctx.Object(p, Facing(p, centre) + math.PI, ObjectRole.Furniture, AssetMix.Pick(ids, v), v, -1, AssetCategories.Playground);
                occ.Take(p, itemRadius, 0);
                placed++;
            }

            int benches = 0;
            if (ctx.B("benches"))
            {
                var bench = AddOnAssets.Or(ctx.S("benchAsset"), AddOnAssets.Bench);
                // Two to four benches on the path-facing half of the rim, looking at the play area.
                int count = math.clamp(items / 2, 2, 4);
                for (int i = 0; i < count; i++)
                {
                    float a = math.atan2(dir.y, dir.x) + (i - (count - 1) * 0.5f) * 0.55f;
                    var p = centre + new float2(math.cos(a), math.sin(a)) * (cluster + 1.6f);
                    if (!occ.Free(p, 1.3f, 0.3f, 0.2f)) continue;
                    // A bench on a bank tilts: the play area may sit on a slope its rim does not share.
                    if (Common.TerrainSurvey.Grade(ctx.G, p, 1f) > global::Placemaker.Core.Build.SiteHarmony.FurnitureGrade) continue;
                    ctx.Object(p, Facing(p, centre), ObjectRole.Bench, bench, Hashing.Mix(ctx.Seed, 0xBE00u + (uint)i));
                    occ.Take(p, 1.3f, 1);
                    benches++;
                }
            }

            if (ctx.B("fence"))
            {
                var fence = AssetMix.One(ctx.S("fenceAsset"));
                foreach (var run in Fences.Ring(centre, cluster + 0.4f, 10, dir, 3f))
                    ctx.StraightNet(run.c0, run.c1, NetRole.Fence, fence);
            }
            ctx.Stat(Keys.StatPlayItems, placed, Keys.UnitCount);
            ctx.Stat(Keys.StatBenches, benches, Keys.UnitCount);
        }
    }
}
