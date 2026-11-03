// s45 — the Review cost bar by friendly category (playtest: one "Outdoor mall 100%" bucket says nothing). Every
// priced element of the plan (objects, nets, surfaces, areas — exactly what Pricing.Of charges) is put into one bucket
// a layman understands: planting, paths, roads, parking, shops, water, play, ground, extras, other.
using System;
using System.Collections.Generic;
using System.Linq;

namespace Placemaker.Core.Build
{
    public static class CostBuckets
    {
        public const string Planting = "planting", Paths = "paths", Roads = "roads", Parking = "parking", Shops = "shops",
            Water = "water", Play = "play", Ground = "ground", Extras = "extras", Other = "other";

        public struct Bucket { public string Id; public double Cost; public double Upkeep; }

        public static string Of(PlannedObject o)
        {
            var c = (o.Category ?? "").ToLowerInvariant();
            if (c.Length > 0)
            {
                if (c.StartsWith("tree") || c.StartsWith("bush") || c.StartsWith("planter") || c.StartsWith("flowerbed") || c.StartsWith("hedge") || c.StartsWith("garden")) return Planting;
                if (c.StartsWith("kiosk") || c.StartsWith("seating.cafe") || c.StartsWith("shop") || c.StartsWith("market") || c.StartsWith("commercial")) return Shops;
                if (c.StartsWith("centerpiece.fountain") || c.StartsWith("water")) return Water;
                if (c.StartsWith("playground") || c.StartsWith("sports") || c.StartsWith("stage")) return Play;
                if (c.StartsWith("rock") || c.StartsWith("decal")) return Ground;
                if (c.StartsWith("parking")) return Parking;
            }
            switch (o.Role)
            {
                case ObjectRole.Tree: case ObjectRole.Bush: case ObjectRole.Planter: return Planting;
                case ObjectRole.ParkingBay: case ObjectRole.Charger: case ObjectRole.Arrow: return Parking;
                case ObjectRole.Decal: return Ground;
                case ObjectRole.Bench: case ObjectRole.Lamp: case ObjectRole.Bin: case ObjectRole.Centerpiece:
                case ObjectRole.Furniture: case ObjectRole.FencePiece: return Extras;
                default: return Other;
            }
        }

        public static string Of(PlannedNet n)
        {
            switch (n.Role)
            {
                case NetRole.Pathway: case NetRole.PathwayWide: return Paths;
                case NetRole.DriveAisle: case NetRole.DriveConnector: case NetRole.Alley: return Parking;
                case NetRole.Fence: return Extras;
                default: return Roads;
            }
        }

        public static string Of(PlannedSurface s)
        {
            var c = (s.Category ?? "").ToLowerInvariant();
            if (c.StartsWith("surface.water")) return Water;
            switch (s.Role)
            {
                case SurfaceRole.Grass: return Planting;
                case SurfaceRole.Water: return Water;
                case SurfaceRole.Asphalt: return Parking;
                case SurfaceRole.Paving: case SurfaceRole.Plaza: case SurfaceRole.Decorative: return Paths;
                default: return Ground;
            }
        }

        public static string Of(PlannedArea a) => a.Role == AreaRole.PedestrianArea ? Paths : Other;

        /// <summary>Cost and upkeep per bucket, most expensive first (empty buckets left out). `price(assetId, qty)`.</summary>
        public static List<Bucket> Sum(ComposedPlan plan, Func<string, double, (double cost, double upkeep)> price)
        {
            var map = new Dictionary<string, Bucket>(StringComparer.Ordinal);
            void Add(string bucket, string assetId, double qty)
            {
                if (string.IsNullOrEmpty(assetId)) return;
                var p = price(assetId, qty);
                if (p.cost <= 0 && p.upkeep <= 0) return;
                map.TryGetValue(bucket, out var b);
                b.Id = bucket;
                b.Cost += p.cost;
                b.Upkeep += p.upkeep;
                map[bucket] = b;
            }
            if (plan != null)
                foreach (var l in plan.Layers)
                {
                    if (l == null) continue;
                    foreach (var o in l.Objects) Add(Of(o), o.Asset.Id, 1);
                    foreach (var n in l.Nets) Add(Of(n), n.Asset.Id, Pricing.BillableNetLength(Pricing.CurveLength(n)));
                    foreach (var s in l.Surfaces) Add(Of(s), s.Asset.Id, Pricing.PolygonArea(s.Ring));
                    foreach (var a in l.Areas) Add(Of(a), a.Asset.Id, Pricing.PolygonArea(a.Ring));
                }
            return map.Values.OrderByDescending(b => b.Cost).ThenBy(b => b.Id, StringComparer.Ordinal).ToList();
        }

        /// <summary>The same split with the catalog prices Pricing.Of uses (so the buckets add up to the cost).</summary>
        public static List<Bucket> Sum(ComposedPlan plan, Dictionary<string, AssetInfo> assets) =>
            Sum(plan, (id, qty) => assets != null && assets.TryGetValue(id, out var info) ? (info.Cost * qty, info.Upkeep * qty) : (0d, 0d));
    }
}
