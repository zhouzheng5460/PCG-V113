// Base of the amenity add-ons (see Amenities.cs): resolve the rectangle (the host's place, or an own
// spot), work out where people arrive from (the nearest path), then lay the amenity out inside.
// Helpers keep every amenity in the same visual language: a ground pad inset from the place edge,
// furniture on the side people arrive from, fences with one gate facing the path.

using System;
using System.Collections.Generic;
using Placemaker.Core.Geometry;
using Placemaker.Core.Layers.Common;
using Unity.Mathematics;

namespace Placemaker.Core.Layers.AddOns
{
    public abstract class AmenityAddOn : AddOnLayer
    {
        public override string Icon => "Media/Game/Icons/ParksAndRecreation.svg";
        public AmenitySpec Spec => Amenities.Spec(Id);

        /// <summary>Lays the amenity out inside <paramref name="site"/>; <paramref name="toward"/> = where people arrive from.</summary>
        protected abstract void Build(LayerContext ctx, Occupancy occ, OrientedRect site, float2 toward);

        /// <summary>Own-spot size (length, width) when there is no host place; a host place has the spec size.</summary>
        protected virtual float2 Footprint(LayerContext ctx) => new float2(Spec.Length, Spec.Width);

        protected override void PlanCore(LayerContext ctx)
        {
            var occ = Occupancy(ctx);
            OrientedRect site;
            if (InOwnPlace(ctx.Input)) site = Amenities.RectOf(ctx.LayerRegion);
            else
            {
                var spec = Spec;
                var gates = new List<float2>();
                foreach (var m in ctx.Input.Markers) gates.Add(m.Position.xz);
                var size = Footprint(ctx);
                if (!Amenities.TrySolve(ctx.LayerRegion, occ, size.x, size.y, spec.DefaultAt, gates, ctx.Seed, null, out site, out _))
                {
                    NoSpace(ctx, NameKey);
                    return;
                }
            }
            var toward = Toward(occ, site);
            Build(ctx, occ, site, toward);
            ctx.Plan.Facts["amenityLength"] = site.HalfLength * 2f;
            ctx.Plan.Facts["amenityWidth"] = site.HalfWidth * 2f;
        }

        private bool InOwnPlace(AreaInput input)
        {
            if (input.RegionTags == null) return false;
            return Array.IndexOf(input.RegionTags, Amenities.Place(Id)) >= 0;
        }

        /// <summary>The nearest point of a reserved path (where people come from), else the middle of a long side.</summary>
        protected static float2 Toward(Occupancy occ, OrientedRect site)
        {
            if (occ.Paths.Count > 0 && occ.Paths.Nearest(site.Center, 150f, out var onPath) >= 0) return onPath;
            return site.Local(0f, -site.HalfWidth * 3f);
        }

        /// <summary>Local (u along the axis, v across) of a world point.</summary>
        protected static float2 LocalOf(OrientedRect r, float2 p) => new float2(math.dot(p - r.Center, r.Axis), math.dot(p - r.Center, r.Side));

        /// <summary>+1/−1: the long side (v sign) facing <paramref name="toward"/>.</summary>
        protected static float SideSign(OrientedRect r, float2 toward) => LocalOf(r, toward).y >= 0f ? 1f : -1f;

        /// <summary>+1/−1: the short end (u sign) facing <paramref name="toward"/>.</summary>
        protected static float EndSign(OrientedRect r, float2 toward) => LocalOf(r, toward).x >= 0f ? 1f : -1f;

        protected static OrientedRect Sub(OrientedRect r, float2 localCentre, float halfLength, float halfWidth) => new OrientedRect
        {
            Center = r.Local(localCentre.x, localCentre.y), Axis = r.Axis, HalfLength = halfLength, HalfWidth = halfWidth,
        };

        /// <summary>A ground surface (first available category of the chain) over a rectangle.</summary>
        protected static bool Pad(LayerContext ctx, float2[] ring, SurfaceRole role, string chosenAsset, params string[] chain)
        {
            string cat = LeisureKit.Category(ctx, chosenAsset, chain);
            if (cat == null) return false;
            return ctx.Surface(ring, role, AssetMix.One(chosenAsset), cat);
        }

        protected static bool Put(LayerContext ctx, Occupancy occ, float2 p, float yaw, ObjectRole role, AssetRef asset, string category, float radius, uint v) =>
            LeisureKit.Put(ctx, occ, p, yaw, role, asset, category, radius, v, 0.2f, 0.2f);

        /// <summary>
        /// Painted court lines: the court's outline in thin white line decals, plus a line across the court
        /// through each local point given (centre line, free-throw line…). Silent when no line decals are installed.
        /// </summary>
        protected static int Markings(LayerContext ctx, OrientedRect court, IEnumerable<float2> local, uint seed)
        {
            if (!AssetAvailability.Has(AssetCategories.DecalFieldLine)) return 0;
            var f = new Fields.Frame(court.Center, court.Axis);
            var lines = new Fields.LinePainter();
            float a = court.HalfLength, b = court.HalfWidth;
            lines.Box(f, -a, -b, a, b);
            foreach (var l in local)
                if (math.abs(l.x) < a - 0.3f) lines.Seg(f, l.x, -b, l.x, b);
            return lines.Emit(ctx, 1f, seed);
        }

        /// <summary>
        /// Fence round a rectangle with one gate (gap) on the side nearest <paramref name="gateToward"/>, centred
        /// where that point projects onto the side. Returns the gate's centre.
        /// </summary>
        protected static float2 RectFence(LayerContext ctx, OrientedRect r, float2 gateToward, float gap, AssetRef fence)
        {
            var ring = r.Ring;
            int best = 0; float bd = float.MaxValue;
            for (int i = 0; i < 4; i++)
            {
                float d = math.distancesq(math.lerp(ring[i], ring[(i + 1) % 4], 0.5f), gateToward);
                if (d < bd) { bd = d; best = i; }
            }
            float2 gate = math.lerp(ring[best], ring[(best + 1) % 4], 0.5f);
            for (int i = 0; i < 4; i++)
            {
                var a = ring[i]; var b = ring[(i + 1) % 4];
                if (i != best) { ctx.StraightNet(a, b, NetRole.Fence, fence); continue; }
                float len = math.distance(a, b);
                var dir = (b - a) / math.max(1e-4f, len);
                float t = math.clamp(math.dot(gateToward - a, dir), gap * 0.5f + 1f, len - gap * 0.5f - 1f);
                if (len < gap + 2f) t = len * 0.5f;
                gate = a + dir * t;
                var g0 = a + dir * (t - gap * 0.5f); var g1 = a + dir * (t + gap * 0.5f);
                if (t - gap * 0.5f > 0.5f) ctx.StraightNet(a, g0, NetRole.Fence, fence);
                if (len - (t + gap * 0.5f) > 0.5f) ctx.StraightNet(g1, b, NetRole.Fence, fence);
            }
            return gate;
        }

        /// <summary>Two to four benches in a row on one long side, facing the middle.</summary>
        protected static int BenchRow(LayerContext ctx, Occupancy occ, OrientedRect r, float side, float v, float span, int count, uint seed)
        {
            var bench = AssetMix.One(ctx.S("benchAsset"));
            int placed = 0;
            for (int i = 0; i < count; i++)
            {
                float u = count == 1 ? 0f : (i / (float)(count - 1) - 0.5f) * span;
                var p = r.Local(u, side * v);
                if (Put(ctx, occ, p, LeisureKit.Yaw(p, r.Local(u, 0f)), ObjectRole.Bench, bench, null, 1.2f, Hashing.Mix(seed, 0xBE00u + (uint)i))) placed++;
            }
            return placed;
        }

        protected static float2[] Disc(float2 c, float r) => LayerRegion.Circle(c, r, 20);
    }
}
