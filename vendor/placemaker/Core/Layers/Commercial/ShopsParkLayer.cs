// Shops & park: a park woven between shop plots, with an optional small car park on the street. The shop
// layouts are s12's street-shop layouts (StreetShops), which left the park/plaza Zoning feature in s14:
//   row        a continuous row along the street, gaps at the park's entrances
//   speckled   small plots spaced along the street, lawn reaching the street between them
//   clusters   corner blocks and shop clusters of uneven depth with green fingers between them
//   facingIn   plots back onto the street and face a U-shaped lane behind them; the park lies beyond the lane
//   alley      a dead-end lane from the street into the park with plots on both sides; a park path carries on
//              from its end
// The park (Park planner, its own style) plans on what is left, so its paths and trees fill the gaps.

using System.Collections.Generic;
using Placemaker.Core.Layers.Common;
using Unity.Mathematics;

namespace Placemaker.Core.Layers.Commercial
{
    [AreaGenerator(18, Group = Group)]
    public sealed class ShopsParkLayer : CommercialLayer
    {
        protected override string Area => "ShopsPark";
        public override string Id => "shopsPark";
        public override string Icon => "Media/Game/Icons/ParksAndRecreation.svg";

        /// <summary>Control values → StreetShops layouts.</summary>
        public static readonly string[] Layouts = { "row", "speckled", "clusters", "facingIn", "alley" };

        public static string ShopLayout(string v)
        {
            switch (v)
            {
                case "speckled": return StreetShops.Speckled;
                case "clusters": return StreetShops.Clusters;
                case "facingIn": return StreetShops.Inward;
                case "alley": return StreetShops.Lane;
                default: return StreetShops.Row;
            }
        }

        protected override void DefineControls(ControlSet c)
        {
            c.Choice("layout", "clusters", ControlTier.Essential, "layout", Layouts).Tip();
            Shared(c.Choice("amount", "some", ControlTier.Essential, "layout", StreetShops.Few, StreetShops.Some, StreetShops.Lots)).Tip();
            c.Choice("parkStyle", "urban", ControlTier.More, "layout", "urban", "pocket", "natural", "formal", "modern").Tip();
            Shared(c.Choice("lot", "small", ControlTier.Essential, "parking", "none", "small")).Tip();
            // The car park is always one aisle deep (no lot size); its options only matter while there is one.
            CommonControls(c, ZoneKinds.CommercialLow, 16, null, true, "lot=small", green: false, streetShops: true);
            c.Fixed("layout");
        }

        protected override void DefinePresets()
        {
            Preset("clusters", ("layout", "clusters"));
            Preset("facingIn", ("layout", "facingIn"), ("plotDepth", 16.0));
            Preset("alley", ("layout", "alley"), ("plotDepth", 16.0));
            Preset("speckled", ("layout", "speckled"), ("amount", "lots"), ("lot", "none"), ("parkStyle", "natural"));
            Preset("row", ("layout", "row"), ("amount", "some"), ("parkStyle", "formal"), ("zoneKind", ZoneKinds.Mixed));
        }

        protected override void Compose(LayerContext ctx, Mall mall, List<StreetShops.Run> runs)
        {
            var r = runs[0];
            var region = ctx.LayerRegion;
            // A small lot first, in the middle of the main frontage (one aisle, entered from the street).
            if (ctx.S("lot") == "small")
            {
                float w = math.min(48f, r.Length * 0.3f), lt = Mall.LotDepth(1);
                float c = r.From + r.Length * 0.5f;
                if (w >= 26f && mall.Reach(r, c - w * 0.5f, c + w * 0.5f) > lt + 20f)
                {
                    var e = new List<(float2, string)> { (r.At(c, 0f), "entrance") };
                    mall.Lot(Mall.Rect(r, c - w * 0.5f, c + w * 0.5f, 0f, lt), e, Mall.Parking(ctx), 0xD1u);
                }
            }
            // The shop layout on the site minus the lot (its edges are not street frontage any more).
            var host = region;
            if (mall.Lots.Count > 0)
            {
                var rest = StreetShops.Carve(region, new List<float2[]>(mall.Taken), MinArea);
                if (rest != null) host = LayerRegion.From(rest) ?? region;
            }
            var gates = new List<float2>();
            foreach (var m in GeneratorRegistry.Get("park").SuggestMarkers(new AreaInput
                     { Shape = new AreaShape { Ring = Geometry.Poly.To3D(host.Ring, ctx.G) }, Site = ctx.Site, Settings = new SettingsBag() }))
                gates.Add(m.Position.xz);
            var res = StreetShops.Plan(host, ctx.Input, gates, ShopLayout(ctx.S("layout")), ctx.S("amount"), ctx.Raw("plotDepth"), Hashing.Mix(ctx.Seed, 0xD2u), true);
            if (res.FellBack) ctx.Issue(IssueSeverity.Info, FallbackKey);
            foreach (var q in res.Shops) { mall.Shops.Add(q); if (res.CellPlots) mall.CellPlots.Add(q); }
            foreach (var q in res.Carve) mall.Taken.Add(q);
            foreach (var a in res.Alleys)
            {
                var lane = new Mall.Lane { Role = NetRole.Alley, Asset = Mall.AlleyAsset, StartAttach = a.StartAttach, StartT = a.StartT, EndAttach = a.EndAttach, EndT = a.EndT };
                lane.Points.AddRange(a.Points);
                mall.AdoptLane(lane);
            }
            mall.Doors.AddRange(res.Doors);
        }

        protected override bool KeepsPark(LayerContext ctx) => true;

        protected override void FillRest(LayerContext ctx, Mall mall)
        {
            // (s50) The park is the largest leftover; the rest of the leftovers are lot.
            var core = HoldParkCore(mall, 0.42f);
            RestLots(ctx, mall, 0xD90u, 300f, core == null);
            mall.Release(core);
            var doors = Doors(mall);
            uint k = 0;
            foreach (var (piece, strip) in mall.RestSplit(150f))
            {
                ctx.Check();
                bool main = k == 0 && !strip;
                // A walk along the park's edge must not touch the zone cells of a plot beside it: the park keeps off the plots.
                var apart = strip ? new List<float2[]>() : mall.KeepOffPlots(piece, 3.2f);
                var parkRing = apart.Count > 0 ? apart[0] : piece;
                if (strip || (!main && mall.IsStrip(piece)) || !mall.Fill(parkRing, "park", main ? ctx.S("parkStyle") : "pocket", doors, 0xD00u + k)) mall.Buffer(piece, 0xD80u + k);
                k++;
            }
            LotEdges(ctx, mall, 0xDC0u);
        }
    }
}
