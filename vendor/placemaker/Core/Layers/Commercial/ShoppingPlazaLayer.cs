// Shopping plaza / market hall: a U-shaped lane enters from the street and wraps a central court; shop rows line
// the outside of the U, all facing the court across the lane; parking fills the frontage beside the shops.
// Court styles (presets):
//   plaza    a paved square (Plaza planner: fountain, trees, benches) open to the street
//   market   a market square (Market square planner: stalls round a centrepiece)
//   court    a shared parking court (Parking planner), entered from the street and from the lane — two shop
//            rows facing a central lot with a lane around it
//   green    a small park

using System.Collections.Generic;
using Placemaker.Core.Layers.Common;
using Unity.Mathematics;

namespace Placemaker.Core.Layers.Commercial
{
    [AreaGenerator(16, Group = Group)]
    public sealed class ShoppingPlazaLayer : CommercialLayer
    {
        protected override string Area => "ShoppingPlaza";
        public override string Id => "shoppingPlaza";
        public override string Icon => "Media/Game/Icons/PropsCommercial.svg";

        public static readonly string[] Courts = { "plaza", "market", "court", "green", "foodCourt" };

        protected override void DefineControls(ControlSet c)
        {
            c.Choice("court", "plaza", ControlTier.Essential, "layout", Courts).Tip();
            c.Slider("courtWidth", 48, 28, 96, 4, "m", ControlTier.Essential, "layout").Tip();
            c.Slider("courtDepth", 48, 28, 96, 4, "m", ControlTier.More, "layout");
            c.Toggle("sideLots", true, ControlTier.More, "parking").Tip();
            CommonControls(c);
            c.Fixed("court");
        }

        protected override void DefinePresets()
        {
            Preset("plaza", ("court", "plaza"));
            Preset("market", ("court", "market"), ("courtWidth", 56.0), ("courtDepth", 56.0));
            Preset("court", ("court", "court"), ("courtWidth", 52.0), ("courtDepth", 44.0));
            Preset("green", ("court", "green"), ("courtWidth", 60.0), ("courtDepth", 60.0), ("zoneKind", ZoneKinds.Mixed));
            // (s32) An open-air food hall: a café square with food stands and their seating round the edge, a fountain in the middle.
            Preset("foodCourt", ("court", "foodCourt"), ("courtWidth", 52.0), ("courtDepth", 48.0), ("plotDepth", 16.0));
        }

        protected override void Compose(LayerContext ctx, Mall mall, List<StreetShops.Run> runs)
        {
            var r = runs[0];
            var (laneAsset, h) = Mall.ShopLane(ctx);
            int cells = Cells(ctx);
            float d = cells * Mall.Cell;
            float w = ctx.Raw("courtWidth"), depth = ctx.Raw("courtDepth");
            float from = r.From + (r.StartCorner ? Mall.CornerClear : 2f), to = r.To - (r.EndCorner ? Mall.CornerClear : 2f);
            float c = 0.5f * (from + to);
            // Shrink the court until the U with its shop rows fits the frontage and the depth.
            for (int guard = 0; guard < 24; guard++)
            {
                float half = w * 0.5f + 2 * h + d;
                bool wide = c - half >= r.From + 1f && c + half <= r.To - 1f;
                bool deep = mall.Reach(r, c - half, c + half) >= depth + 2 * h + d + 1f;
                if (wide && deep) break;
                if (!deep && cells > 2) { cells--; d = cells * Mall.Cell; continue; }
                if (!wide && w > 28f) { w -= 4f; continue; }
                if (!deep && depth > 28f) { depth -= 4f; continue; }
                if (cells > 1) { cells--; d = cells * Mall.Cell; continue; }
                break;
            }
            float l = c - w * 0.5f - h, rr = c + w * 0.5f + h;   // leg centre lines
            var lane = mall.AddLane(r.At(l, 0f), -r.In, new[] { r.At(l, depth + h), r.At(rr, depth + h) }, r.At(rr, 0f), -r.In, NetRole.Alley, laneAsset, h);
            if (lane == null)
            {
                if (mall.Extra) return;   // (shops) another frontage: nothing left behind

                mall.ShopRow(r, from, to, 0f, cells);
                ctx.Issue(IssueSeverity.Info, FallbackKey);
                return;
            }
            // Shop rows outside the U, facing in: left and right legs, then the back run across the full width.
            ShopsAlongLeg(mall, r, l - h, -1f, depth + 2 * h, cells);
            ShopsAlongLeg(mall, r, rr + h, 1f, depth + 2 * h, cells);
            mall.ShopRow(r, l - h, rr + h, depth + 2 * h, cells);

            // The court.
            var core = Mall.Rect(r, c - w * 0.5f, c + w * 0.5f, 0f, depth);
            string kind = ctx.S("court");
            var door = new List<float2> { r.At(c, depth) };
            bool ok;
            switch (kind)
            {
                case "court":
                {
                    var entrances = new List<(float2, string)> { (r.At(c, 0f), "entrance"), (r.At(c - w * 0.5f, depth * 0.5f), "alley"), (r.At(c + w * 0.5f, depth * 0.5f), "alley") };
                    ok = mall.Lot(core, entrances, Mall.Parking(ctx), 0xB1u);
                    break;
                }
                case "market": ok = mall.Fill(core, "marketSquare", "weekly", door, 0xB2u); break;
                case "green": ok = mall.Fill(core, "park", "pocket", door, 0xB3u); break;
                case "foodCourt":
                    ok = mall.Fill(core, "plaza", "cafe", door, 0xB6u);
                    if (ok)
                    {
                        mall.AddOn(core, "shops", 0xB7u, b => { b.Values["count"] = 6.0; b.Values["where"] = "edges"; b.Values["seating"] = true; });
                        mall.AddOn(core, "kiosk", 0xB8u, b => { b.Values["count"] = 2.0; b.Values["benches"] = true; });
                    }
                    break;
                default: ok = mall.Fill(core, "plaza", null, door, 0xB4u); break;
            }
            if (!ok) mall.Fill(core, "plaza", null, door, 0xB5u);

            // Parking beside the shops on the remaining frontage, entered from the street.
            if (ctx.B("sideLots"))
            {
                float lt = Mall.LotDepth(Mall.Aisles(ctx.S("lotSize")));
                lt = math.min(lt, mall.Reach(r, from, to) - 2f);
                uint salt = 0xB10u;
                foreach (var (s0, s1) in new[] { (from, l - h - d - 2f), (rr + h + d + 2f, to) })
                {
                    if (s1 - s0 < 28f || lt < 18f) continue;
                    var e = new List<(float2, string)>();
                    LotEntrances(e, r, s0, s1, 0f, "entrance");
                    mall.Lot(Mall.Rect(r, s0, s1, 0f, lt), e, Mall.Parking(ctx), salt++);
                }
                // No room beside the shops: parking behind them, off the street at the back (a market hall's service side).
                int front = kind == "court" ? 1 : 0;
                if (mall.Lots.Count <= front)
                    foreach (var back in runs)
                        if (math.dot(back.In, r.In) < -0.7f && mall.FrontLot(back, 90f, Mall.Aisles(ctx.S("lotSize")), Mall.Parking(ctx), salt++)) break;
            }
        }

        /// <summary>Plots along a leg: the leg edge at s = edge, plots reaching away from it (dir), from the street to t1.</summary>
        private static void ShopsAlongLeg(Mall mall, StreetShops.Run r, float edge, float dir, float t1, int cells)
        {
            int n = math.max(1, (int)math.ceil(t1 / (3 * Mall.Cell)));
            for (int k = 0; k < n; k++)
            {
                float a = t1 * k / n, b = t1 * (k + 1) / n;
                for (int dd = cells; dd >= 1; dd--)
                    if (mall.Shop(Mall.Rect(r, edge, edge + dir * dd * Mall.Cell, a, b))) break;
            }
        }
    }
}
