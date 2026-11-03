// rf-shops3 — the mall park planned from the player's entrances: the loop follows the site (rounded corners; round round a
// circle, an L round an L), small shops outside it facing the park, the park inside. Cars come in only where the player put
// an entrance (a drive or a driveway to the loop, through the ring of shops); footpath entrances become walks that cross
// the loop at a crosswalk and carry on into the park. Commons and Family park drive round on a road with parking on both
// sides (the town square: cars parked along the park), Garden square on a quiet alley. Food carts wait at the park gates;
// Garden square and Family park have a pond.

using System.Collections.Generic;
using Placemaker.Core.Geometry;
using Placemaker.Core.Layers.Common;
using Unity.Mathematics;

namespace Placemaker.Core.Layers.Commercial
{
    public sealed partial class MallParkLayer
    {
        public const string RingFact = "parkRing";
        /// <summary>The loop with angled parking on both sides (vanilla), else plain parking, else the shop lane.</summary>
        public static readonly string[] ParkingRoads = { "Small Road - Double Sided Parking Angled", "Small Road - Double Sided Parking" };

        protected override bool UsesEntrances => true;

        protected override bool ComposeFromEntrances(LayerContext ctx, Mall mall, List<StreetShops.Run> runs)
        {
            if (ctx.S("loop") == "pedestrian") return false;   // the hidden car-free ring keeps its own layout
            // A road with parking both sides needs room (24 m wide): on a small or thin site the loop is the alley.
            // It is kept when it holds at least 70 % of the shops the alley ring would (a circle's short sides hold none round a wide road).
            // (A ring too small for rows of the asked depth all round tries two-cell rows: the loop moves out 8 m on every side.)
            bool Ring(bool parking)
            {
                if (Cells(ctx) <= 2) return RingPark(ctx, mall, runs, parking, 99);
                var pm = PlanMark.Of(ctx.Plan); var mm = mall.Mark();
                float deep = RingPark(ctx, mall, runs, parking, 99) ? mall.ShopArea() : 0f;
                pm.Restore(ctx.Plan); mall.Rewind(mm); mall.ResetDesign();
                float shallow = RingPark(ctx, mall, runs, parking, 2) ? mall.ShopArea() : 0f;
                if (shallow > 0f && shallow >= 1.1f * deep) return true;
                pm.Restore(ctx.Plan); mall.Rewind(mm); mall.ResetDesign();
                return deep > 0f && RingPark(ctx, mall, runs, parking, 99);
            }
            if (ctx.S("loopRoad") != "parking") return Ring(false);
            var planMark = PlanMark.Of(ctx.Plan); var mallMark = mall.Mark();
            int alleyShops = Ring(false) ? mall.Shops.Count : 0;
            planMark.Restore(ctx.Plan); mall.Rewind(mallMark);
            mall.ResetDesign();
            if (Ring(true) && mall.Shops.Count >= 0.7f * alleyShops) return true;
            planMark.Restore(ctx.Plan); mall.Rewind(mallMark);
            mall.ResetDesign();
            return Ring(false);
        }

        private static (AssetRef asset, float half, NetRole role) LoopRoad(LayerContext ctx, bool parking)
        {
            if (parking)
            {
                var assets = ctx.Site?.Assets;
                foreach (var id in ParkingRoads)
                {
                    if (assets != null && !assets.ContainsKey(id)) continue;
                    float w = assets != null && assets.TryGetValue(id, out var info) && info.NetWidth > 0.5f ? info.NetWidth : 24f;
                    if (assets != null && assets.TryGetValue(id, out var zi) && zi.ZoneBlocks == 2) continue;
                    return (new AssetRef(id, ParkingRoads[1], StreetShops.AlleyFallback), w * 0.5f, NetRole.Street);
                }
            }
            var (a, h) = Mall.ShopLane(ctx);
            return (a, h, NetRole.Alley);
        }

        private static bool RingPark(LayerContext ctx, Mall mall, List<StreetShops.Run> runs, bool parkingRoad, int cellsCap)
        {
            var planMark = PlanMark.Of(ctx.Plan); var mallMark = mall.Mark();
            bool Fail(int step) { planMark.Restore(ctx.Plan); mall.Rewind(mallMark); ctx.Plan.Facts["parkRingFail"] = step; return false; }
            var r = StripMallLayer.MainFrame(ctx, mall, runs);
            mall.MainDir = r.Dir;
            var (asset, h, role) = LoopRoad(ctx, parkingRoad);
            int inner = math.min(Cells(ctx), cellsCap);
            float d = inner * Mall.Cell;
            mall.AnchorWeight = 1.3f;
            float InsetFor(float2 p, float2 nOut) => mall.RowInset(p, nOut, h, d);
            var line = mall.RingLineVar(InsetFor, 8f, 30f);
            if (line == null) return Fail(1);
            // The park needs room: at least 30 m across inside the loop.
            var parkRegion = LayerRegion.From(line);
            if (parkRegion == null) return Fail(2);
            parkRegion.DeepestPoint(out float clear, null, 16);
            if (clear < 15f + h) return Fail(3);
            var loop = mall.RingLoop(line, role, asset, h, r.At(0f, 0f) + r.In * 1e5f);
            if (loop == null) return Fail(4);
            bool RowSeg(int i) => math.distance(loop.Points[i], loop.Points[i + 1]) >= 2f * Mall.Cell + 2f * h + 2f;

            // The ring of shops (held while the lots are planned), outside the loop facing the park.
            for (int i = 0; i + 1 < loop.Points.Count; i++)
            {
                if (!RowSeg(i)) continue;
                var f = Mall.Frame(loop.Points[i], loop.Points[i + 1]);
                for (float trim = h + 0.5f; trim <= h + 16f; trim += 2f)
                {
                    if (f.To - 2f * trim < 8f) break;
                    if (mall.HoldRow(Mall.Rect(f, trim, f.To - trim, -h, -(Mall.ZoneOffset(h) + d + 1f))) != null) break;
                }
            }
            // The park: everything inside the loop, kept until the lots are planned.
            float2[] park = null;
            try
            {
                float best = 0f;
                foreach (var p in Poly.Offset(line, h + 0.3f)) if (p != null && p.Length >= 3 && Poly.Area(p) > best) { best = Poly.Area(p); park = Poly.EnsureCcw(p); }
            }
            catch (System.Exception) { park = null; }
            if (park == null) return Fail(5);
            // (rf-shops3) Gate courts: where a car entrance meets an alley loop, a small car park opens off the loop on the park side,
            // straight on from the drive (people park, then walk into the park or round to the shops); the park keeps the rest.
            // A parking loop has its own bays; a park under 4000 m2 keeps all its ground.
            var gateCourts = new List<(float2[] lot, float2 at)>();
            if (!parkingRoad && Poly.Area(park) > 4000f && mall.Gates != null)
                foreach (var g in mall.Gates)
                {
                    if (!g.Cars || gateCourts.Count >= 2) continue;
                    if (!mall.FirstLane(g.At, g.In, 260f, out var ln, out var at) || ln != loop) continue;
                    int seg = 0; float bd = float.MaxValue;
                    for (int k = 0; k + 1 < loop.Points.Count; k++)
                    {
                        float dd = SegmentIndex.DistSq(at, loop.Points[k], loop.Points[k + 1]);
                        if (dd < bd) { bd = dd; seg = k; }
                    }
                    var f = Mall.Frame(loop.Points[seg], loop.Points[seg + 1]);
                    float s0 = math.dot(at - f.A, f.Dir), z = h + 1.5f;
                    var rect = Mall.Rect(f, s0 - 21f, s0 + 21f, z, z + 30f);
                    float2[] lot = null;
                    try { lot = Mall.Largest(Poly.Boolean(new[] { (IReadOnlyList<float2>)park }, new[] { (IReadOnlyList<float2>)Poly.EnsureCcw(rect) }, BoolOp.Intersection), 380f); }
                    catch (System.Exception) { lot = null; }
                    if (lot == null) continue;
                    float2[] rest = null;
                    try { rest = Mall.Largest(Poly.Boolean(new[] { (IReadOnlyList<float2>)park }, new[] { (IReadOnlyList<float2>)Poly.EnsureCcw(rect) }, BoolOp.Difference), 2000f); }
                    catch (System.Exception) { rest = null; }
                    if (rest == null) continue;
                    park = Poly.EnsureCcw(rest);
                    gateCourts.Add((lot, f.At(s0, z)));
                }
            int before = mall.Taken.Count;
            mall.Take(park);
            var parkParts = mall.Taken.GetRange(before, mall.Taken.Count - before);

            // The player's entrances: drives and driveways to the loop, walks to it (and on into the park).
            mall.RouteGates();
            ctx.Check();
            // Lots on what is left round the ring (the corners), entered from the loop.
            mall.LotBudget = float.MaxValue;
            int courts = 0;
            foreach (var (lot, at) in gateCourts)
                if (mall.CleanLot(lot, new List<(float2, string)> { (at, "alley") }, Mall.Parking(ctx), 0x7E0u + (uint)courts)) courts++;
            ctx.Plan.Facts["gateCourts"] = courts;
            RestLots(ctx, mall, 0x7B0u, 400f, false, false);
            mall.ReleaseRows();
            int rows = mall.LaneRows(loop, inner, null, -1, 0f, 0f, 3, 2, RowSeg);
            // the corners outside the loop: parking for the shops and the park (a corner court facing each soft corner)
            mall.CornerCourts(loop, Mall.Parking(ctx), 0x7D0u);
            foreach (var p in parkParts) mall.Taken.Remove(p);
            ctx.Check();

            // Doors of the park: where walks from the entrances meet the loop, and the middle of every long side.
            var doors = new List<(Mall.Lane, float2)>();
            foreach (var g in mall.Gates)
                if (g.Landed == "walk" || g.Landed == "lane" || g.Landed == "drive")
                {
                    var (_, hit) = LandingOf(mall, loop, g);
                    if (hit.HasValue) doors.Add((loop, hit.Value));
                }
            for (int i = 0; i + 1 < loop.Points.Count; i++)
                if (math.distance(loop.Points[i], loop.Points[i + 1]) >= 50f) doors.Add((loop, math.lerp(loop.Points[i], loop.Points[i + 1], 0.5f)));
            PlantPark(ctx, mall, park, doors, 0x7A1u);
            // Food carts at the park gates (inside, beside the walk in).
            if (mall.X.Trucks > 0)
            {
                int carts = 0;
                foreach (var (lane, at) in doors)
                {
                    if (carts >= mall.X.Trucks) break;
                    var inward = math.normalizesafe(Poly.Centroid(park) - at);
                    var side = new float2(-inward.y, inward.x);
                    carts += mall.FoodCarts(at + inward * (h + 4.5f) + side * 5f, side, 1, 4f, -inward, 0x7C0u + (uint)carts);
                }
            }
            ctx.Plan.Facts[RingFact] = 1;
            ctx.Plan.Facts["ringRows"] = rows;
            ctx.Plan.Facts["parkingLoop"] = parkingRoad ? 1 : 0;
            return mall.Shops.Count > 0 || Fail(9);
        }

        /// <summary>Where a gate's way met the loop (the node the walk or drive ends on).</summary>
        private static (Mall.Lane, float2?) LandingOf(Mall mall, Mall.Lane loop, ShopEntrances.Gate g)
        {
            float best = 60f; float2? at = null;
            foreach (var j in loop.Joins)
            {
                float dd = math.distance(j, g.At);
                if (dd < best && math.dot(j - g.At, g.In) > 0f) { best = dd; at = j; }
            }
            return (loop, at);
        }

        /// <summary>The park inside the loop: the type's park style, its walks opening at the doors, the fountain and playground in their places, a pond where the style has one.</summary>
        private static void PlantPark(LayerContext ctx, Mall mall, float2[] park, List<(Mall.Lane, float2)> doors, uint salt)
        {
            var places = new List<PlannedRegion>();
            var children = ctx.B("playground") ? new[] { "playground" } : new string[0];
            var targets = ctx.B("fountain") ? new[] { Placemaker.Core.Layers.Park.ParkLayer.PlazaPlace } : new string[0];
            bool pond = mall.X.Pond;
            bool parked = mall.FillOpen(park, "park", ctx.S("parkStyle"), doors, null, salt, b =>
            {
                b.Values["fence"] = false;
                b.Values["belt"] = false;
                b.Values["naturalGround"] = false;
                b.Values["plazaShape"] = "circle";
                b.Values["plazaAt"] = "centre";
                if (pond) { b.Values["lake"] = true; b.Values["lakeSize"] = 35.0; }
            }, false, children, targets, places);
            if (!parked) parked = mall.FillOpen(park, "plaza", "green", doors, null, salt + 1u, null, false);
            if (!parked) { mall.Green(park, salt + 4u); return; }
            uint s = salt + 2u;
            foreach (var place in places)
            {
                var ring = Poly.To2D(place.Ring);
                if (place.Key == Placemaker.Core.Layers.Park.ParkLayer.PlazaPlace)
                {
                    ctx.Surface(ring, SurfaceRole.Plaza, new AssetRef(""), AssetCategories.SurfacePaving);
                    mall.AddOn(ring, "fountain", s++);
                }
                else if (place.Key.EndsWith("playground")) mall.AddOn(ring, "playground", s++);
            }
            if (ctx.B("fountain") && !places.Exists(p => p.Key == Placemaker.Core.Layers.Park.ParkLayer.PlazaPlace)) mall.AddOn(park, "fountain", s++);
            if (ctx.B("playground") && !places.Exists(p => p.Key.EndsWith("playground"))) mall.AddOn(park, "playground", s++, b => b.Values["size"] = "small");
        }
    }
}
