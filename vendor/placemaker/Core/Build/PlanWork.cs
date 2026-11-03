// Placemaker — flattening a ComposedPlan into build work (GPL-3.0).
// Pure: decides categories, ordering and batching so the ECS emitter only has to translate
// each item into one definition.

using System;
using System.Collections.Generic;
using Unity.Mathematics;

namespace Placemaker.Core.Build
{
    /// <summary>Categories the catalog uses for roles that have no shared AssetCategories constant.</summary>
    public static class BuildCategories
    {
        public const string CulDeSacCap = "object.culDeSacCap";
        public const string RoundaboutIsland = "object.roundaboutIsland";
        public const string Marker = "object.marker";
        public const string AnimalSpawner = "object.animalSpawner";
        public const string AccessPoint = "object.accessPoint";
        public const string AreaPedestrian = "area.pedestrian";
        public const string AreaHangaround = "area.hangaround";
        public const string AreaLot = "area.lot";
        public const string Arrow = "parking.arrow";
    }

    public static class RoleCategories
    {
        /// <summary>The net's explicit category when set, else the role's default (g5).</summary>
        public static string ForNet(PlannedNet n) =>
            n == null ? AssetCategories.Pathway : !string.IsNullOrEmpty(n.Category) ? n.Category : ForNet(n.Role);

        public static string ForNet(NetRole role)
        {
            switch (role)
            {
                case NetRole.Street: case NetRole.Avenue: case NetRole.CulDeSac: case NetRole.Roundabout:
                case NetRole.PerimeterRoad: case NetRole.Alley:
                    return AssetCategories.Road;
                case NetRole.Pathway: case NetRole.PathwayWide: return AssetCategories.Pathway;
                case NetRole.DriveAisle: case NetRole.DriveConnector: return AssetCategories.InvisiblePath;
                case NetRole.Fence: return AssetCategories.Fence;
                default: return AssetCategories.Pathway;
            }
        }

        /// <summary>The object's explicit category when set, else the role's default category.</summary>
        public static string ForObject(PlannedObject o) =>
            o == null ? AssetCategories.Decoration : !string.IsNullOrEmpty(o.Category) ? o.Category : ForObject(o.Role);

        public static string ForObject(ObjectRole role)
        {
            switch (role)
            {
                case ObjectRole.Tree: return AssetCategories.Tree;
                case ObjectRole.Bush: return AssetCategories.Bush;
                case ObjectRole.Planter: return AssetCategories.Planter;
                case ObjectRole.Bench: return AssetCategories.Bench;
                case ObjectRole.Lamp: return AssetCategories.Lamp;
                case ObjectRole.Bin: return AssetCategories.Bin;
                case ObjectRole.Centerpiece: return AssetCategories.Centerpiece;
                case ObjectRole.Furniture: return AssetCategories.Decoration;
                case ObjectRole.FencePiece: return AssetCategories.Fence;
                case ObjectRole.Decal: return AssetCategories.Decal;
                case ObjectRole.ParkingBay: return AssetCategories.ParkingDecal;
                case ObjectRole.Charger: return AssetCategories.Charger;
                case ObjectRole.Arrow: return BuildCategories.Arrow;
                case ObjectRole.Marker: return BuildCategories.Marker;
                case ObjectRole.AnimalSpawner: return BuildCategories.AnimalSpawner;
                case ObjectRole.AccessPoint: return BuildCategories.AccessPoint;
                case ObjectRole.CulDeSacCap: return BuildCategories.CulDeSacCap;
                case ObjectRole.RoundaboutIsland: return BuildCategories.RoundaboutIsland;
                default: return AssetCategories.Decoration;
            }
        }

        /// <summary>The surface's explicit category when set, else the role's default category.</summary>
        public static string ForSurface(PlannedSurface s) =>
            s == null ? AssetCategories.SurfaceAny : !string.IsNullOrEmpty(s.Category) ? s.Category : ForSurface(s.Role);

        public static string ForSurface(SurfaceRole role)
        {
            switch (role)
            {
                case SurfaceRole.Grass: return AssetCategories.SurfaceGrass;
                case SurfaceRole.Paving: case SurfaceRole.Plaza: return AssetCategories.SurfacePaving;
                case SurfaceRole.Asphalt: return AssetCategories.SurfaceAsphalt;
                case SurfaceRole.Water: return AssetCategories.SurfaceNatural;
                default: return AssetCategories.SurfaceAny;
            }
        }

        public static string ForArea(AreaRole role)
        {
            switch (role)
            {
                case AreaRole.Hangaround: return BuildCategories.AreaHangaround;
                case AreaRole.Lot: return BuildCategories.AreaLot;
                default: return BuildCategories.AreaPedestrian;
            }
        }
    }

    public enum WorkKind : byte { Net = 0, Object = 1, Surface = 2, Area = 3, Terraform = 4, Zoning = 5 }

    /// <summary>One thing to emit. Index points back into its layer's list for error mapping and exclusion.</summary>
    public struct WorkItem
    {
        public WorkKind Kind;
        public int Layer;          // index into ComposedPlan.Layers
        public int Index;          // index into that layer's list of this kind
        public string Category;
        public AssetRef Asset;
        /// <summary>Generator of the layer ("parking" decides economy/surface-overlay handling).</summary>
        public string GeneratorId;
        /// <summary>Representative XZ position for error mapping.</summary>
        public float2 Anchor;
        /// <summary>From a [AreaGenerator(Loose = true)] layer: built as ordinary city content (no group, no tag).</summary>
        public bool Loose;
    }

    public sealed class BuildWork
    {
        public readonly List<WorkItem> Terraform = new List<WorkItem>();
        public readonly List<WorkItem> Nets = new List<WorkItem>();
        public readonly List<WorkItem> Surfaces = new List<WorkItem>();
        public readonly List<WorkItem> Areas = new List<WorkItem>();
        public readonly List<WorkItem> Objects = new List<WorkItem>();
        public readonly List<WorkItem> Zoning = new List<WorkItem>();
        public bool HasPermanentTerrain;
        public bool HasParking;
        /// <summary>
        /// True when anything comes from a grouped (non-loose) layer. False = a loose build (e.g. a road grid):
        /// no owner lot, no record, no member tags.
        /// </summary>
        public bool AnyGrouped;
        public bool AllLoose => !AnyGrouped;

        public int DefinitionCount => Nets.Count + Surfaces.Count + Areas.Count + Objects.Count;

        public IEnumerable<WorkItem> All()
        {
            foreach (var w in Terraform) yield return w;
            foreach (var w in Nets) yield return w;
            foreach (var w in Surfaces) yield return w;
            foreach (var w in Areas) yield return w;
            foreach (var w in Objects) yield return w;
            foreach (var w in Zoning) yield return w;
        }
    }

    public static class PlanWork
    {
        /// <summary>
        /// (s25) Functional objects the ghost must never emit as optional: parking bays (the stall markings that spawn
        /// the parking lanes), chargers and arrows. Neighbouring ParkingLotDecal01 boxes are 3.2 m wide on 3.0 m stalls;
        /// an optional temporary that overlaps another optional one is cancelled TOGETHER with it (ValidationSystem
        /// CancelOptional), and the instant build adopts the ghost as it is — so every row of stalls vanished in game
        /// (log: "ghost 71: 1823/1823 parts, 0 rejected, 1095 optional objects dropped"; only the 5 m accessible and a
        /// few EV decals survived). Built non-optional, as ParkingLotTool builds them, they stay.
        /// </summary>
        public static bool IsFunctional(ObjectRole role) => role == ObjectRole.ParkingBay || role == ObjectRole.Charger || role == ObjectRole.Arrow;

        /// <summary>
        /// (s44) Objects the build emits with a hair of negative elevation so the game never collides them (s21 decal sink,
        /// BuildService.DecalSink): every decal, and since s44 the stall markings and lane arrows too. Playtest 77: an outdoor
        /// mall's two lots showed "60 parts overlap" in Review and the build failed with OverlapExisting — the 3.2 m wide
        /// ParkingLotDecal01 boxes on 3.0 m stalls (and 6.2 m deep on 5.9 m stalls) meet their neighbours; since s25 they
        /// are never optional and never left out, so nothing could clear the errors. A marking takes no space: it only
        /// projects onto the ground and owns its parking lane, which the game places from the object's transform.
        /// </summary>
        public static bool SinkInBuild(ObjectRole role) => role == ObjectRole.Decal || role == ObjectRole.ParkingBay || role == ObjectRole.Arrow;

        /// <summary>
        /// (s25) May the build's collision retry leave this item out? Never a functional parking piece: the retry matches
        /// errors to plan items by position (2 m), so an errored surface, island or lamp next to a row of stalls used to take
        /// the stall markings with it. A marking that really collides is resolved by the game itself (hidden, never a stop).
        /// </summary>
        public static bool Excludable(ComposedPlan plan, WorkItem w)
        {
            if (w.Kind != WorkKind.Object || plan?.Layers == null || w.Layer < 0 || w.Layer >= plan.Layers.Count) return true;
            var objects = plan.Layers[w.Layer]?.Objects;
            if (objects == null || w.Index < 0 || w.Index >= objects.Count || objects[w.Index] == null) return true;
            return !IsFunctional(objects[w.Index].Role);
        }

        /// <summary>(core) No asset chosen (automatic and nothing resolved): an empty id with no fallback.</summary>
        public static bool IsUnchosen(AssetRef a) => string.IsNullOrEmpty(a.Id) && (a.Fallbacks == null || a.Fallbacks.Length == 0);

        /// <summary>(s25) Planned stall markings (ParkingBay objects with an asset) — the build logs how many it built.</summary>
        public static int StallMarkings(ComposedPlan plan)
        {
            int n = 0;
            if (plan?.Layers == null) return 0;
            foreach (var l in plan.Layers)
                if (l?.Objects != null) foreach (var o in l.Objects) if (o != null && o.Role == ObjectRole.ParkingBay) n++;
            return n;
        }

        /// <summary>
        /// Flattens a composed plan. Items whose key is in <paramref name="excluded"/> are skipped
        /// (auto-exclusion of colliding pieces). Never throws for empty/odd plans.
        /// </summary>
        public static BuildWork Flatten(ComposedPlan plan, ICollection<long> excluded = null)
        {
            var w = new BuildWork();
            if (plan?.Layers == null) return w;
            for (int li = 0; li < plan.Layers.Count; li++)
            {
                var l = plan.Layers[li];
                if (l == null) continue;
                string gen = l.GeneratorId ?? "";
                bool parking = gen == "parking";
                for (int i = 0; i < l.Terraform.Count; i++)
                {
                    var t = l.Terraform[i];
                    if (t?.Ring == null || t.Ring.Length < 3) continue;
                    // Every terraform is permanent in practice: removal cannot restore the old ground.
                    w.HasPermanentTerrain = true;
                    Add(w.Terraform, excluded, new WorkItem { Kind = WorkKind.Terraform, Layer = li, Index = i, GeneratorId = gen, Anchor = Centroid(t.Ring) });
                }
                for (int i = 0; i < l.Nets.Count; i++)
                {
                    var n = l.Nets[i];
                    if (n == null) continue;
                    Add(w.Nets, excluded, new WorkItem
                    {
                        Kind = WorkKind.Net, Layer = li, Index = i, Category = RoleCategories.ForNet(n), Asset = n.Asset,
                        GeneratorId = gen, Anchor = Composer.Bezier(n.A, n.B, n.C, n.D, 0.5f).xz,
                    });
                    if (parking && (n.Role == NetRole.DriveAisle || n.Role == NetRole.DriveConnector)) w.HasParking = true;
                }
                for (int i = 0; i < l.Surfaces.Count; i++)
                {
                    var s = l.Surfaces[i];
                    if (s?.Ring == null || s.Ring.Length < 3) continue;
                    Add(w.Surfaces, excluded, new WorkItem
                    {
                        Kind = WorkKind.Surface, Layer = li, Index = i, Category = RoleCategories.ForSurface(s), Asset = s.Asset,
                        GeneratorId = gen, Anchor = Centroid(s.Ring),
                    });
                }
                for (int i = 0; i < l.Areas.Count; i++)
                {
                    var a = l.Areas[i];
                    if (a?.Ring == null || a.Ring.Length < 3) continue;
                    // (core) A lot area without an explicit asset only reserves land in the plan (other layers keep off it);
                    // it is never built: the automatic lot pool holds only vanilla industry lots (AutoAssets).
                    if (a.Role == AreaRole.Lot && IsUnchosen(a.Asset)) continue;
                    Add(w.Areas, excluded, new WorkItem
                    {
                        Kind = WorkKind.Area, Layer = li, Index = i, Category = RoleCategories.ForArea(a.Role), Asset = a.Asset,
                        GeneratorId = gen, Anchor = Centroid(a.Ring),
                    });
                }
                for (int i = 0; i < l.Objects.Count; i++)
                {
                    var o = l.Objects[i];
                    if (o == null) continue;
                    if (!math.all(math.isfinite(o.Position))) continue;
                    Add(w.Objects, excluded, new WorkItem
                    {
                        Kind = WorkKind.Object, Layer = li, Index = i, Category = RoleCategories.ForObject(o), Asset = o.Asset,
                        GeneratorId = gen, Anchor = o.Position.xz,
                    });
                    if (o.Role == ObjectRole.ParkingBay) w.HasParking = true;
                }
                for (int i = 0; i < l.Zoning.Count; i++)
                {
                    var z = l.Zoning[i];
                    if (z == null || string.IsNullOrEmpty(z.ZoneId)) continue;
                    Add(w.Zoning, excluded, new WorkItem
                    {
                        Kind = WorkKind.Zoning, Layer = li, Index = i, Category = AssetCategories.Zone, Asset = new AssetRef(z.ZoneId),
                        GeneratorId = gen, Anchor = ((z.A + z.B + z.C + z.D) * 0.25f).xz,
                    });
                }
            }
            w.AnyGrouped = AnyGroupedLayer(plan);
            return w;
        }

        /// <summary>Stable key of a work item (layer, kind, index) for exclusion sets across re-materialisations.</summary>
        /// <summary>True when a layer with buildable content comes from a grouped (non-loose) generator.</summary>
        public static bool AnyGroupedLayer(ComposedPlan plan)
        {
            if (plan?.Layers == null) return false;
            foreach (var l in plan.Layers)
            {
                if (l == null) continue;
                bool content = l.Nets.Count + l.Objects.Count + l.Surfaces.Count + l.Areas.Count + l.Terraform.Count + l.Zoning.Count > 0
                    || (l.Paint != null && l.Paint.Count > 0);
                if (content && !GeneratorRegistry.IsLoose(l.GeneratorId)) return true;
            }
            return false;
        }

        public static long Key(WorkItem w) => ((long)w.Layer << 40) | ((long)w.Kind << 32) | (uint)w.Index;

        private static void Add(List<WorkItem> list, ICollection<long> excluded, WorkItem item)
        {
            if (excluded != null && excluded.Contains(Key(item))) return;
            item.Loose = GeneratorRegistry.IsLoose(item.GeneratorId);
            list.Add(item);
        }

        public static float2 Centroid(float3[] ring)
        {
            float2 c = 0;
            if (ring == null || ring.Length == 0) return c;
            foreach (var p in ring) c += p.xz;
            return c / ring.Length;
        }

        /// <summary>Splits <paramref name="count"/> items into frame batches of at most <paramref name="perFrame"/>.</summary>
        public static List<int2> Batches(int count, int perFrame)
        {
            var r = new List<int2>();
            if (count <= 0) return r;
            if (perFrame < 1) perFrame = 1;
            for (int start = 0; start < count; start += perFrame) r.Add(new int2(start, math.min(count, start + perFrame)));
            return r;
        }
    }

    /// <summary>Maps game error type names to localisation keys (Lang/en-US.build.json).</summary>
    public static class BuildErrorKeys
    {
        public const string Prefix = "Placemaker.Build.Error.";
        public const string Other = Prefix + "Other";

        private static readonly HashSet<string> Known = new HashSet<string>(StringComparer.Ordinal)
        {
            "ExceedsCityLimits", "OverlapExisting", "NotEnoughMoney", "SteepSlope", "InWater", "TightCurve",
            "ShortDistance", "LongDistance", "InvalidShape", "SmallArea", "LowElevation", "NotEnoughClearance",
            "NoWater", "ExceedsLotLimits", "AlreadyUpgraded", "PathfindFailed", "NoCargoAccess", "NoRoadAccess",
        };

        public static string For(string errorTypeName) =>
            !string.IsNullOrEmpty(errorTypeName) && Known.Contains(errorTypeName) ? Prefix + errorTypeName : Other;

        /// <summary>Errors that excluding single pieces cannot fix (fail immediately instead of auto-excluding).</summary>
        public static bool IsGlobal(string errorTypeName) => errorTypeName == "NotEnoughMoney";
    }
}
