// Base for add-on layers: small features dropped into a region another layer already filled.
// Add-ons only ever place into free space (Occupancy = input.Reserved + site obstacles + what
// they placed themselves) and register everything they place in their plan, so the composer
// reserves it for later siblings. When a category has no installed assets they report an Info
// issue and place nothing instead of failing.

using System.Collections.Generic;
using Placemaker.Core.Layers.Common;
using Unity.Mathematics;

namespace Placemaker.Core.Layers.AddOns
{
    public abstract class AddOnLayer : LayerBase
    {
        public override LayerKind Kind => LayerKind.AddOn;
        public override float MinArea => 20f;

        protected Occupancy Occupancy(LayerContext ctx) => new Occupancy(ctx.LayerRegion, ctx.Input.Reserved, ctx.Site);

        protected static void NoSpace(LayerContext ctx, string what) =>
            ctx.Issue(IssueSeverity.Warning, Keys.NoSpace, what);

        /// <summary>Yaw that makes an object at <paramref name="from"/> face <paramref name="to"/> (0 = +Z).</summary>
        protected static float Facing(float2 from, float2 to)
        {
            var d = to - from;
            return math.lengthsq(d) < 1e-6f ? 0f : math.atan2(d.x, d.y);
        }

        /// <summary>Points on a ring around a centre, evenly spaced with a phase.</summary>
        protected static List<float2> RingPoints(float2 centre, float radius, int count, float phase)
        {
            var r = new List<float2>(count);
            for (int i = 0; i < count; i++)
            {
                float a = phase + i * 2f * math.PI / math.max(1, count);
                r.Add(centre + new float2(math.cos(a), math.sin(a)) * radius);
            }
            return r;
        }

        /// <summary>Picks spread-out spots (at least minDistance apart). Spots are not taken; the caller places into them.</summary>
        protected static List<float2> Spread(Occupancy occ, float radius, int count, Spots.Preference pref, uint seed, float minDistance)
        {
            var result = new List<float2>();
            for (int i = 0; i < count; i++)
            {
                if (!Spots.Find(occ, radius, pref, Hashing.Mix(seed, (uint)i), out var spot, result, minDistance)) break;
                result.Add(spot);
            }
            return result;
        }
    }
}
