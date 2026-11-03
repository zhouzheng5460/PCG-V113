// Placemaker — the preview's layers and legend (s22).
//
// Playtest s22 (a curved park between four streets): "It's getting crazy; hard to even see what's happening …
// I need a legend: a little window where I can hover an entry and it hides everything except that thing." Every
// element the overlay draws belongs to exactly ONE legend layer (Of(...)); the legend lists the layers present in
// the plan with their swatch colour (shared with the overlay) and a count / length / area; the player hovers a
// layer to isolate it and clicks to hide / show it. Zone-blocked cells and decals are off by default (noise).
// Pure: the game overlay, the UI JSON (UiModels.Legend), the mock host and the tests all use it.

using System;
using System.Collections.Generic;
using Placemaker.Core.Geometry;
using Placemaker.Core.Layers.Roads;
using Unity.Mathematics;

namespace Placemaker.Core.Build
{
    public static class PreviewLegend
    {
        // ── Layer ids (save format of the per-session visibility: never rename) ──
        public const string Paths = "paths";
        public const string Trees = "trees";
        public const string Planting = "planting";
        public const string Water = "water";
        public const string Surfaces = "surfaces";
        public const string Objects = "objects";
        public const string Features = "features";
        public const string Parking = "parking";
        public const string Terrain = "terrain";
        public const string Cutouts = "cutouts";
        public const string Blocked = "blocked";
        public const string Decals = "decals";
        public const string Entrances = "entrances";
        /// <summary>(s61) The ring markers of the plan's located issues: red = left out / blocked, orange = a warning.</summary>
        public const string Issues = "issues";
        /// <summary>Roads by role: "road:street", "road:avenue", … (RoadRoles ids).</summary>
        public const string RoadPrefix = "road:";

        /// <summary>Display order; roads (by RoadRoles.Order) come right after paths.</summary>
        public static readonly string[] Order =
        {
            Paths, "road:" + RoadRoles.Street, "road:" + RoadRoles.Avenue, "road:" + RoadRoles.Local, "road:" + RoadRoles.Roundabout,
            "road:" + RoadRoles.Perimeter, "road:" + RoadRoles.Lane, "road:" + RoadRoles.CarFree,
            Features, Trees, Planting, Water, Surfaces, Objects, Parking, Entrances, Issues, Terrain, Cutouts, Blocked, Decals,
        };

        /// <summary>Layers that start hidden (noise most of the time): the legend's click shows them.</summary>
        public static bool DefaultVisible(string id) => id != Blocked && id != Decals;

        public static string Road(string role) => RoadPrefix + role;
        public static bool IsRoad(string id) => id != null && id.StartsWith(RoadPrefix, StringComparison.Ordinal);

        public static string LabelKey(string id) =>
            IsRoad(id) ? RoadRoles.LabelKey(id.Substring(RoadPrefix.Length)) : "Placemaker.Legend." + id;

        /// <summary>Swatch / overlay colour "#rrggbb" of a layer (roads: the s16 role colours).</summary>
        public static string Color(string id)
        {
            if (IsRoad(id)) return RoadRoles.Color(id.Substring(RoadPrefix.Length));
            switch (id)
            {
                case Paths: return "#ffd173";
                case Trees: return "#45b84f";
                case Planting: return "#8fd65e";
                case Water: return "#4a8cf2";
                case Surfaces: return "#d9cdb4";
                case Objects: return "#b8a6ff";
                case Features: return "#ff9a40";
                case Parking: return "#6699ff";
                case Terrain: return "#ff8c33";   // fill; cut = Color2
                case Cutouts: return "#4d8cff";
                case Blocked: return "#d9443a";
                case Decals: return "#bfa680";
                case Entrances: return "#ffd633";
                case Issues: return "#ff9a1f";   // warning; left-out / blocked = Color2
                default: return "#cccccc";
            }
        }

        /// <summary>Second swatch colour (terrain: cut), or null.</summary>
        public static string Color2(string id) => id == Terrain ? TerrainHeat.CutHex : id == Issues ? BlockedHex : null;

        /// <summary>(s61) Marker colour of an issue that dropped / blocked something.</summary>
        public const string BlockedHex = "#e5322d";

        public static float3 Rgb(string id) => Hex(Color(id));

        public static float3 Hex(string hex)
        {
            if (string.IsNullOrEmpty(hex) || hex.Length < 7 || hex[0] != '#') return new float3(0.8f);
            int v = Convert.ToInt32(hex.Substring(1, 6), 16);
            return new float3(((v >> 16) & 255) / 255f, ((v >> 8) & 255) / 255f, (v & 255) / 255f);
        }

        // ───────────── classification: every planned element → exactly one layer ─────────────

        private static readonly string[] FeaturePrefixes =
        {
            AssetCategories.Playground, "play", AssetCategories.Sports, "dog.", AssetCategories.Picnic, AssetCategories.CafeSeating,
            AssetCategories.Kiosk, AssetCategories.Restroom, AssetCategories.Shelter, AssetCategories.Camping, AssetCategories.Centerpiece,
            AssetCategories.Stage, AssetCategories.Lookout, AssetCategories.WaterJets, "cemetery", "memorial", "monument",
            AssetCategories.Glasshouse, AssetCategories.GardenShed,
        };

        private static readonly string[] PlantingPrefixes =
        {
            AssetCategories.Bush, AssetCategories.Planter, AssetCategories.Flowerbed, AssetCategories.Hedge, AssetCategories.GardenCrop,
        };

        private static bool Starts(string s, string[] prefixes)
        {
            if (string.IsNullOrEmpty(s)) return false;
            foreach (var p in prefixes) if (s.StartsWith(p, StringComparison.Ordinal)) return true;
            return false;
        }

        public static string Of(PlannedObject o)
        {
            if (o == null) return Objects;
            var cat = o.Category ?? "";
            switch (o.Role)
            {
                case ObjectRole.Tree: return Trees;
                case ObjectRole.Bush: case ObjectRole.Planter: return Planting;
                case ObjectRole.Decal: return Decals;
                case ObjectRole.Centerpiece: return Features;
                case ObjectRole.ParkingBay: case ObjectRole.Charger: case ObjectRole.Arrow: return Parking;
                case ObjectRole.Marker: case ObjectRole.AccessPoint: case ObjectRole.AnimalSpawner: return Entrances;
            }
            if (cat.StartsWith(AssetCategories.Decal, StringComparison.Ordinal) || cat == AssetCategories.ParkingDecal) return Decals;
            if (Starts(cat, FeaturePrefixes)) return Features;
            if (Starts(cat, PlantingPrefixes)) return Planting;
            return Objects;
        }

        /// <param name="layerGenerator">GeneratorId of the AreaPlan the net belongs to: a roads layer's pathways are car-free streets.</param>
        public static string Of(PlannedNet n, string layerGenerator)
        {
            if (n == null) return Paths;
            switch (n.Role)
            {
                case NetRole.Pathway: case NetRole.PathwayWide:
                    return layerGenerator == RoadsLayer.LayerId ? Road(RoadRoles.CarFree) : Paths;
                // (s34) A roads layer's alleys are its back lanes.
                case NetRole.Alley: return layerGenerator == RoadsLayer.LayerId ? Road(RoadRoles.Lane) : Parking;
                case NetRole.DriveAisle: case NetRole.DriveConnector: return Parking;
                case NetRole.Fence: return Objects;
            }
            var role = RoadRoles.Of(n.Role);
            return role != null ? Road(role) : Paths;
        }

        public static string Of(PlannedSurface s) => s != null && s.Role == SurfaceRole.Water ? Water : Surfaces;
        public static string Of(PlannedArea a) => Surfaces;
        public static string Of(PlannedPaint p) => Surfaces;
        public static string Of(PlannedTerraform t) => Terrain;
        public static string Of(PlannedZoning z) => Cutouts;
        public static string Of(ZoneCellMark c) => c.State == ZoneCellState.Blocked ? Blocked : Cutouts;

        // ───────────── the legend of a plan ─────────────

        public sealed class Entry
        {
            public string Id = "";
            public string LabelKey = "";
            public string Color = "";
            public string Color2;
            public int Count;
            public double LengthM;
            public double AreaM2;
            /// <summary>Terrain: deepest cut / highest fill in metres (0 when none).</summary>
            public double MinDelta, MaxDelta;
            /// <summary>(s61) Issues: how many of them left something out or blocked it (the red ones).</summary>
            public int Blocked;
            /// <summary>Roads: most used prefab ("" = not resolved yet) and the control that picks it.</summary>
            public string AssetId = "";
            public string Control = "";
            public bool DefaultOn = true;
        }

        /// <summary>
        /// The layers present in <paramref name="plan"/> (in <see cref="Order"/>) with their counts. Entrances come
        /// from the recipe (markers are not part of the plan): <paramref name="entrances"/> = how many are placed.
        /// </summary>
        public static List<Entry> Compute(ComposedPlan plan, int entrances = 0)
        {
            var by = new Dictionary<string, Entry>(StringComparer.Ordinal);
            var roadAssets = new Dictionary<string, Dictionary<string, double>>(StringComparer.Ordinal);
            Entry E(string id)
            {
                if (!by.TryGetValue(id, out var e))
                    by[id] = e = new Entry { Id = id, LabelKey = LabelKey(id), Color = Color(id), Color2 = Color2(id), DefaultOn = DefaultVisible(id) };
                return e;
            }
            if (plan != null)
            {
                foreach (var l in plan.Layers)
                {
                    if (l == null) continue;
                    foreach (var n in l.Nets)
                    {
                        if (n == null) continue;
                        var id = Of(n, l.GeneratorId);
                        var e = E(id);
                        double len = RoadRoles.LengthXZ(n);
                        e.Count++;
                        e.LengthM += len;
                        if (IsRoad(id))
                        {
                            if (string.IsNullOrEmpty(e.Control)) e.Control = n.Control ?? RoadRoles.ControlOf(n.Role);
                            if (!roadAssets.TryGetValue(id, out var m)) roadAssets[id] = m = new Dictionary<string, double>(StringComparer.Ordinal);
                            string a = n.Asset.Id ?? "";
                            m.TryGetValue(a, out double l0);
                            m[a] = l0 + len;
                        }
                    }
                    foreach (var o in l.Objects) if (o != null) E(Of(o)).Count++;
                    foreach (var s in l.Surfaces)
                        if (s?.Ring != null && s.Ring.Length >= 3) { var e = E(Of(s)); e.Count++; e.AreaM2 += Poly.Area(Poly.To2D(s.Ring)); }
                    foreach (var a in l.Areas)
                        if (a?.Ring != null && a.Ring.Length >= 3) E(Of(a)).Count++;
                    foreach (var p in l.Paint)
                        if (p?.Ring != null && p.Ring.Length >= 3) { var e = E(Of(p)); e.Count++; e.AreaM2 += p.AreaEstimate > 0f ? p.AreaEstimate : Poly.Area(Poly.To2D(p.Ring)); }
                    foreach (var z in l.Zoning)
                        if (z != null && (plan.ZoneCells == null || plan.ZoneCells.Count == 0))
                        { var e = E(Cutouts); e.Count++; e.AreaM2 += Poly.Area(new[] { z.A.xz, z.B.xz, z.C.xz, z.D.xz }); }
                }
                if (plan.ZoneCells != null)
                    foreach (var c in plan.ZoneCells)
                    {
                        var e = E(Of(c));
                        e.Count++;
                        e.AreaM2 += c.Clipped != null ? Poly.Area(c.Clipped) : ZoneGuard.Cell * ZoneGuard.Cell;
                    }
                var heat = plan.TerrainHeat;
                if (heat != null && heat.Rings.Count > 0)
                {
                    var e = E(Terrain);
                    e.Count = heat.Rings.Count;
                    e.AreaM2 = heat.ChangedM2;
                    e.MinDelta = Math.Round(heat.MinDelta, 1);
                    e.MaxDelta = Math.Round(heat.MaxDelta, 1);
                }
                else
                {
                    // No heat field (no site heights): still list the terraform so it can be isolated.
                    int n = 0;
                    foreach (var l in plan.Layers) if (l != null) foreach (var t in l.Terraform) if (t?.Ring != null && t.Ring.Length >= 3) n++;
                    if (n > 0) E(Terrain).Count = n;
                }
            }
            if (entrances > 0) E(Entrances).Count = entrances;
            if (plan != null)
                foreach (var i in plan.AllIssues())
                {
                    if (i?.At == null) continue;
                    var e = E(Issues);
                    e.Count++;
                    if (i.Dropped || i.Severity == IssueSeverity.Error) e.Blocked++;
                }

            var r = new List<Entry>();
            foreach (var id in Order)
            {
                if (!by.TryGetValue(id, out var e)) continue;
                if (IsRoad(id) && roadAssets.TryGetValue(id, out var m))
                {
                    string best = ""; double bestLen = -1;
                    foreach (var kv in m)
                        if (kv.Value > bestLen || (kv.Value == bestLen && string.CompareOrdinal(kv.Key, best) < 0)) { best = kv.Key; bestLen = kv.Value; }
                    e.AssetId = best;
                }
                e.LengthM = Math.Round(e.LengthM);
                e.AreaM2 = Math.Round(e.AreaM2);
                r.Add(e);
            }
            return r;
        }

        // ───────────── visibility (hover isolates, click toggles) ─────────────

        /// <summary>
        /// Is <paramref name="id"/> drawn? While a legend entry is hovered only that layer is; otherwise the player's
        /// choice (<paramref name="overrides"/>: id → visible), else the layer's default.
        /// </summary>
        public static bool Visible(string id, string hover, IReadOnlyDictionary<string, bool> overrides)
        {
            if (!string.IsNullOrEmpty(hover)) return id == hover;
            if (overrides != null && overrides.TryGetValue(id ?? "", out var v)) return v;
            return DefaultVisible(id);
        }
    }

    /// <summary>
    /// (s22) The terraform preview as a heat map: height change (target − ground before the build) sampled on a
    /// coarse grid inside the area and quantised into bands — cut in blues, fill in oranges/reds, more opaque the
    /// bigger the change, nothing below <see cref="MinChange"/>. Replaces the dashed outline of every terraform
    /// polygon (dozens of thick orange boxes in the s22 playtest).
    /// </summary>
    public sealed class TerrainHeat
    {
        /// <summary>Changes smaller than this (m) are not drawn: terrain smoothing noise, not a design decision.</summary>
        public const float MinChange = 0.25f;
        /// <summary>Lower bounds of the bands 1..4 (m): band 4 is every change of 3 m or more.</summary>
        public static readonly float[] Bands = { 0.25f, 0.75f, 1.5f, 3f };
        public const int MaxBand = 4;
        public const int MaxCells = 6000;
        public const float MinCell = 3f;
        public const string FillHex = "#ff8c33", CutHex = "#4da6ff";

        public float Cell;
        /// <summary>Band rings: band > 0 fill, band &lt; 0 cut (|band| 1..4). Rings are clipped to the area.</summary>
        public readonly List<(int band, float2[] ring)> Rings = new List<(int band, float2[] ring)>();
        public float MinDelta, MaxDelta;
        public double ChangedM2;

        /// <summary>Band of a height change: 0 = nothing, ±1…±4.</summary>
        public static int BandOf(float delta)
        {
            if (!math.isfinite(delta)) return 0;
            float a = math.abs(delta);
            if (a < MinChange) return 0;
            int b = 1;
            for (int i = 1; i < Bands.Length; i++) if (a >= Bands[i]) b = i + 1;
            return delta > 0 ? b : -b;
        }

        /// <summary>Colour (#rrggbb) and fill alpha of a band: fill orange → red, cut light → deep blue; alpha grows with |band|.</summary>
        public static (string hex, float alpha) Style(int band)
        {
            int a = math.clamp(math.abs(band), 1, MaxBand);
            float alpha = 0.10f + 0.07f * (a - 1);
            if (band > 0)
            {
                switch (a)
                {
                    case 1: return ("#ffc266", alpha);
                    case 2: return (FillHex, alpha);
                    case 3: return ("#ff5c2e", alpha);
                    default: return ("#e0301e", alpha);
                }
            }
            switch (a)
            {
                case 1: return ("#8fd0ff", alpha);
                case 2: return (CutHex, alpha);
                case 3: return ("#2f7fff", alpha);
                default: return ("#2448d8", alpha);
            }
        }

        /// <summary>Height change at p: the last terraform item covering p wins (as the composer's TerrainField).</summary>
        public static float DeltaAt(IReadOnlyList<PlannedTerraform> items, IReadOnlyList<float2[]> rings, float2 p, float ground)
        {
            for (int i = items.Count - 1; i >= 0; i--)
            {
                var r = rings[i];
                if (r == null || !Poly.Contains(r, p)) continue;
                var t = items[i];
                float d = TerraformSurface.TargetAt(t, p) - ground;
                if (t.Kind == TerraformKind.Lower) d = math.min(0f, d);
                else if (t.Kind == TerraformKind.Raise) d = math.max(0f, d);
                return d;
            }
            return 0f;
        }

        /// <summary>
        /// The heat map of <paramref name="plan"/>'s terraform over <paramref name="area"/> (the outline), against
        /// the ground before the build. Null when nothing changes by <see cref="MinChange"/> or more.
        /// </summary>
        public static TerrainHeat Build(ComposedPlan plan, float2[] area, Func<float2, float> ground)
        {
            if (plan == null || area == null || area.Length < 3 || ground == null) return null;
            var items = new List<PlannedTerraform>();
            var rings = new List<float2[]>();
            var mins = new List<float2>();
            var maxs = new List<float2>();
            float2 bmin = new float2(float.MaxValue), bmax = new float2(float.MinValue);
            foreach (var l in plan.Layers)
            {
                if (l?.Terraform == null) continue;
                foreach (var t in l.Terraform)
                {
                    if (t?.Ring == null || t.Ring.Length < 3) continue;
                    var r = Poly.To2D(t.Ring);
                    Poly.Bounds(r, out var mn, out var mx);
                    items.Add(t); rings.Add(r); mins.Add(mn); maxs.Add(mx);
                    bmin = math.min(bmin, mn); bmax = math.max(bmax, mx);
                }
            }
            if (items.Count == 0) return null;
            Poly.Bounds(area, out var amin, out var amax);
            bmin = math.max(bmin, amin); bmax = math.min(bmax, amax);
            if (bmax.x <= bmin.x || bmax.y <= bmin.y) return null;
            float2 size = bmax - bmin;
            float cell = math.max(MinCell, (float)Math.Sqrt(size.x * size.y / MaxCells));
            int nx = math.max(1, (int)math.ceil(size.x / cell)), ny = math.max(1, (int)math.ceil(size.y / cell));
            while (nx * ny > MaxCells * 2) { cell *= 1.25f; nx = (int)math.ceil(size.x / cell); ny = (int)math.ceil(size.y / cell); }

            // Bucket the items by row so a cell only tests the items whose bounds reach its row.
            var rowItems = new List<int>[ny];
            for (int i = 0; i < items.Count; i++)
            {
                int y0 = math.max(0, (int)math.floor((mins[i].y - bmin.y) / cell)), y1 = math.min(ny - 1, (int)math.floor((maxs[i].y - bmin.y) / cell));
                for (int y = y0; y <= y1; y++) (rowItems[y] ?? (rowItems[y] = new List<int>())).Add(i);
            }

            var heat = new TerrainHeat { Cell = cell };
            var bands = new int[nx];
            var subItems = new List<PlannedTerraform>();
            var subRings = new List<float2[]>();
            for (int y = 0; y < ny; y++)
            {
                Array.Clear(bands, 0, nx);
                var list = rowItems[y];
                if (list == null) continue;
                float cy = bmin.y + (y + 0.5f) * cell;
                for (int x = 0; x < nx; x++)
                {
                    var p = new float2(bmin.x + (x + 0.5f) * cell, cy);
                    subItems.Clear(); subRings.Clear();
                    foreach (int i in list)
                        if (p.x >= mins[i].x && p.x <= maxs[i].x) { subItems.Add(items[i]); subRings.Add(rings[i]); }
                    if (subItems.Count == 0) continue;
                    float d = DeltaAt(subItems, subRings, p, ground(p));
                    int b = BandOf(d);
                    if (b == 0) continue;
                    if (!Poly.Contains(area, p) && !CellTouches(area, p, cell)) continue;
                    bands[x] = b;
                    heat.MinDelta = math.min(heat.MinDelta, d);
                    heat.MaxDelta = math.max(heat.MaxDelta, d);
                }
                // Runs of one band along the row → one rectangle each, clipped to the area.
                int run = 0;
                while (run < nx)
                {
                    int b = bands[run];
                    if (b == 0) { run++; continue; }
                    int end = run;
                    while (end + 1 < nx && bands[end + 1] == b) end++;
                    var rect = new[]
                    {
                        new float2(bmin.x + run * cell, bmin.y + y * cell), new float2(bmin.x + (end + 1) * cell, bmin.y + y * cell),
                        new float2(bmin.x + (end + 1) * cell, bmin.y + (y + 1) * cell), new float2(bmin.x + run * cell, bmin.y + (y + 1) * cell),
                    };
                    foreach (var piece in Clip(rect, area))
                    {
                        heat.Rings.Add((b, piece));
                        heat.ChangedM2 += Poly.Area(piece);
                    }
                    run = end + 1;
                }
            }
            return heat.Rings.Count > 0 ? heat : null;
        }

        private static bool CellTouches(float2[] area, float2 p, float cell)
        {
            float h = cell * 0.5f;
            return Poly.Contains(area, p + new float2(h, h)) || Poly.Contains(area, p + new float2(-h, h))
                || Poly.Contains(area, p + new float2(h, -h)) || Poly.Contains(area, p + new float2(-h, -h));
        }

        /// <summary>The parts of <paramref name="rect"/> inside <paramref name="area"/> (the rectangle itself when wholly inside).</summary>
        public static List<float2[]> Clip(float2[] rect, float2[] area)
        {
            var r = new List<float2[]>();
            bool all = true;
            foreach (var p in rect) if (!Poly.Contains(area, p)) { all = false; break; }
            if (all)
            {
                // A concave outline may still cut through a rectangle whose corners are all inside.
                bool crosses = false;
                for (int i = 0; i < 4 && !crosses; i++)
                    for (int j = 0; j < area.Length; j++)
                        if (Poly.SegmentsIntersect(rect[i], rect[(i + 1) % 4], area[j], area[(j + 1) % area.Length])) { crosses = true; break; }
                if (!crosses) { r.Add(rect); return r; }
            }
            List<Region> parts;
            try { parts = Poly.Intersection(rect, area); } catch (Exception) { return r; }
            foreach (var part in parts)
            {
                if (part?.Outer == null || part.Outer.Length < 3 || Poly.Area(part.Outer) < 0.25f) continue;
                if (part.HasHoles)
                {
                    List<float2[]> cut;
                    try { cut = Poly.CutHoles(part); } catch (Exception) { continue; }
                    foreach (var c in cut) if (c != null && c.Length >= 3) r.Add(Poly.EnsureCcw(c));
                }
                else r.Add(Poly.EnsureCcw(part.Outer));
            }
            return r;
        }
    }
}
