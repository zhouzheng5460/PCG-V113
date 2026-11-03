// Placemaker — what the dressing pass knows about a composed plan (s21). GPL-3.0.
//
// One read-only snapshot of everything planned, built once per Compose: trees (crowns, conifers), net
// corridors (paths, aisles, roads, tracks) with real half widths, ground polygons by kind (paved,
// sand, dirt, water — surfaces, terrain paint, planned lakes) and the site's water. Spatial grids keep
// every query local, so dressing a 5 ha plan stays in the low milliseconds.

using System;
using System.Collections.Generic;
using Placemaker.Core.Geometry;
using Placemaker.Core.Layers.Common;
using Unity.Mathematics;

namespace Placemaker.Core.Decals
{
    public enum GroundKind { Grass = 0, Paved = 1, Dirt = 2, Sand = 3, Water = 4 }

    internal sealed class DressingWorld
    {
        public struct Tree { public float2 P; public float Crown; public bool Conifer; }
        public struct Seg { public float2 A, B; public float Half; public NetRole Role; }
        public sealed class Poly { public float2[] Ring; public float2 Min, Max; public GroundKind Kind; }

        public readonly LayerRegion Root;
        public readonly SiteContext Site;
        public readonly List<Tree> Trees = new List<Tree>();
        public readonly List<Seg> Segs = new List<Seg>();
        public readonly List<Poly> Polys = new List<Poly>();
        public readonly List<AreaPlan> Plans = new List<AreaPlan>();
        /// <summary>Zoning cutouts (Cutouts.Of): no decal reaches into one.</summary>
        public readonly List<float2[]> Cutouts;
        /// <summary>(s28) Every planned surface, grass included: terrain paint under one is hidden.</summary>
        public readonly List<Poly> Covers = new List<Poly>();
        private readonly Grid _treeGrid = new Grid(8f), _segGrid = new Grid(8f), _polyGrid = new Grid(16f), _coverGrid = new Grid(16f);
        private readonly Func<float2, bool> _wet;

        private static readonly string[] ConiferWords = { "pine", "spruce", "fir", "conifer", "cedar", "larch", "cypress", "yew", "hemlock", "juniper", "thuja" };

        public DressingWorld(ComposedPlan plan, AreaShape area, SiteContext site)
        {
            Site = site;
            Root = LayerRegion.From(area?.Ring);
            Cutouts = Layers.Common.Cutouts.Of(plan);
            foreach (var l in plan.Layers)
            {
                if (l == null) continue;
                Plans.Add(l);
                foreach (var o in l.Objects)
                {
                    if (o == null || o.Role != ObjectRole.Tree) continue;
                    var t = new Tree { P = o.Position.xz, Crown = CrownOf(o, site), Conifer = IsConifer(o) };
                    _treeGrid.Add(t.P, t.Crown, Trees.Count);
                    Trees.Add(t);
                }
                foreach (var n in l.Nets)
                {
                    if (n == null || n.Role == NetRole.Fence) continue;
                    float hw = HalfWidth(n, site);
                    var prev = n.A.xz;
                    for (int i = 1; i <= 8; i++)
                    {
                        var p = Composer.Bezier(n.A, n.B, n.C, n.D, i / 8f).xz;
                        var s = new Seg { A = prev, B = p, Half = hw, Role = n.Role };
                        _segGrid.AddBox(math.min(prev, p) - hw, math.max(prev, p) + hw, Segs.Count);
                        Segs.Add(s);
                        prev = p;
                    }
                }
                foreach (var s in l.Surfaces)
                {
                    if (s?.Ring == null || s.Ring.Length < 3) continue;
                    var k = KindOf(s);
                    AddCover(Geometry.Poly.To2D(s.Ring), k);
                    if (k == GroundKind.Grass) continue;
                    AddPoly(Geometry.Poly.To2D(s.Ring), k);
                }
                foreach (var pt in l.Paint)
                {
                    if (pt?.Ring == null || pt.Ring.Length < 3 || pt.Strength < 0.35f || pt.Shape != null || pt.Erase) continue;
                    if (pt.Layer == TerrainMaterials.Dirt) AddPoly(Geometry.Poly.To2D(pt.Ring), GroundKind.Dirt);
                    else if (pt.Layer == TerrainMaterials.Sand) AddPoly(Geometry.Poly.To2D(pt.Ring), GroundKind.Sand);
                }
                foreach (var t in l.Terraform)
                    if (t?.Ring != null && t.Ring.Length >= 3 && !float.IsNaN(t.WaterLevel)) AddPoly(Geometry.Poly.To2D(t.Ring), GroundKind.Water);
            }
            if (Root != null && site?.WaterDepthAt != null)
            {
                var tf = new List<PlannedTerraform>();
                foreach (var l in plan.Layers) if (l?.Terraform != null) tf.AddRange(l.Terraform);
                var ground = TerrainField.FromTerraform(tf, site.HeightAt) ?? site.HeightAt;
                try { _wet = Layers.Types.Beach.Shoreline.WetTest(site, Root.Min - 40f, Root.Max + 40f, ground); } catch (Exception) { _wet = null; }
            }
        }

        private void AddPoly(float2[] ring, GroundKind k)
        {
            if (ring == null || ring.Length < 3) return;
            var p = new Poly { Ring = ring, Kind = k, Min = ring[0], Max = ring[0] };
            foreach (var v in ring) { p.Min = math.min(p.Min, v); p.Max = math.max(p.Max, v); }
            _polyGrid.AddBox(p.Min, p.Max, Polys.Count);
            Polys.Add(p);
        }

        private void AddCover(float2[] ring, GroundKind k)
        {
            if (ring == null || ring.Length < 3) return;
            var p = new Poly { Ring = ring, Kind = k, Min = ring[0], Max = ring[0] };
            foreach (var v in ring) { p.Min = math.min(p.Min, v); p.Max = math.max(p.Max, v); }
            _coverGrid.AddBox(p.Min, p.Max, Covers.Count);
            Covers.Add(p);
        }

        /// <summary>(s28) Under a planned surface (any kind, grass included) or a net: terrain texture paint would not show.</summary>
        public bool Covered(float2 p)
        {
            if (NetClearance(p, 0.5f, out _) < 0f) return true;
            foreach (int i in _coverGrid.Near(p, 0f))
            {
                var q = Covers[i];
                if (p.x < q.Min.x || p.y < q.Min.y || p.x > q.Max.x || p.y > q.Max.y) continue;
                if (Geometry.Poly.Contains(q.Ring, p)) return true;
            }
            return false;
        }

        public static GroundKind KindOf(PlannedSurface s)
        {
            string cat = s.Category ?? "";
            if (s.Role == SurfaceRole.Water || cat.StartsWith("surface.water", StringComparison.Ordinal)) return GroundKind.Water;
            if (cat.StartsWith("surface.sand", StringComparison.Ordinal)) return GroundKind.Sand;
            if (cat.StartsWith("surface.dirt", StringComparison.Ordinal) || cat.StartsWith("surface.bark", StringComparison.Ordinal)
                || cat.StartsWith("surface.crop", StringComparison.Ordinal) || cat.StartsWith("surface.natural", StringComparison.Ordinal)
                || cat.StartsWith("surface.clay", StringComparison.Ordinal)) return GroundKind.Dirt;
            if (cat.StartsWith("surface.grass", StringComparison.Ordinal)) return GroundKind.Grass;
            if (cat.StartsWith("surface.paving", StringComparison.Ordinal) || cat.StartsWith("surface.asphalt", StringComparison.Ordinal)
                || cat.StartsWith("surface.gravel", StringComparison.Ordinal) || cat.StartsWith("surface.rock", StringComparison.Ordinal)
                || cat.StartsWith("surface.rubber", StringComparison.Ordinal) || cat.StartsWith("surface.track", StringComparison.Ordinal)) return GroundKind.Paved;
            switch (s.Role)
            {
                case SurfaceRole.Paving: case SurfaceRole.Asphalt: case SurfaceRole.Plaza: case SurfaceRole.Decorative: return GroundKind.Paved;
                case SurfaceRole.Ground: return GroundKind.Dirt;
                default: return GroundKind.Grass;
            }
        }

        public static float HalfWidth(PlannedNet n, SiteContext site)
        {
            if (n != null && !string.IsNullOrEmpty(n.Asset.Id) && site?.Assets != null && site.Assets.TryGetValue(n.Asset.Id, out var info) && info.NetWidth > 0.5f)
                return info.NetWidth * 0.5f;
            return n == null ? 1.5f : Composer.DefaultHalfWidth(n.Role);
        }

        internal static float CrownOf(PlannedObject o, SiteContext site)
        {
            float s = o.Scale > 0.05f ? o.Scale : 1f;
            if (!string.IsNullOrEmpty(o.Asset.Id) && site?.Assets != null && site.Assets.TryGetValue(o.Asset.Id, out var info))
            {
                float w = math.cmax(info.BoundsMax.xz - info.BoundsMin.xz) * 0.5f, h = info.BoundsMax.y - info.BoundsMin.y;
                return math.clamp(math.max(w, h * 0.28f), 1.5f, 7f) * s;
            }
            return (o.Age >= 0 && o.Age <= 1 ? 2.5f : 3.6f) * s;
        }

        internal static bool IsConifer(PlannedObject o)
        {
            var id = ((o.Asset.Id ?? "") + " " + (o.Category ?? "")).ToLowerInvariant();
            foreach (var w in ConiferWords) if (id.Contains(w)) return true;
            return false;
        }

        // ───────────────────────── queries ─────────────────────────

        /// <summary>Distance to the nearest tree trunk within <paramref name="cap"/> (cap when none); also the tree.</summary>
        public float NearestTree(float2 p, float cap, out int index)
        {
            index = -1;
            float best = cap;
            foreach (int i in _treeGrid.Near(p, cap))
            {
                float d = math.distance(Trees[i].P, p);
                if (d < best) { best = d; index = i; }
            }
            return best;
        }

        /// <summary>Signed clearance from net corridors (negative = on a net), capped; role of the nearest.</summary>
        public float NetClearance(float2 p, float cap, out NetRole role, Func<NetRole, bool> filter = null)
        {
            role = NetRole.Pathway;
            float best = cap;
            foreach (int i in _segGrid.Near(p, cap))
            {
                var s = Segs[i];
                if (filter != null && !filter(s.Role)) continue;
                float d = math.sqrt(ReservedSpace.DistSqToSegment(p, s.A, s.B)) - s.Half;
                if (d < best) { best = d; role = s.Role; }
            }
            return best;
        }

        public bool InPoly(float2 p, GroundKind kind)
        {
            foreach (int i in _polyGrid.Near(p, 0f))
            {
                var q = Polys[i];
                if (q.Kind != kind || p.x < q.Min.x || p.y < q.Min.y || p.x > q.Max.x || p.y > q.Max.y) continue;
                if (Geometry.Poly.Contains(q.Ring, p)) return true;
            }
            return false;
        }

        /// <summary>Inside a zoning cutout or within <paramref name="margin"/> of one.</summary>
        public bool NearCutout(float2 p, float margin)
        {
            foreach (var q in Cutouts)
                if (Geometry.Poly.Contains(q, p) || Geometry.Poly.DistanceToBoundary(q, p) < margin) return true;
            return false;
        }

        public bool IsWater(float2 p)
        {
            // s20: the real, final waterline (terrain vs the robust water level, after the plan's terraform).
            if (_wet != null) { try { if (_wet(p)) return true; } catch (Exception) { } return InPoly(p, GroundKind.Water); }
            if (Site?.WaterDepthAt != null)
            {
                float d;
                try { d = Site.WaterDepthAt(p); } catch (Exception) { d = 0f; }
                if (d > 0.05f) return true;
            }
            return InPoly(p, GroundKind.Water);
        }

        /// <summary>What lies at p: water, a net or paved surface (paved), sand, dirt, else grass.</summary>
        public GroundKind GroundAt(float2 p)
        {
            if (IsWater(p)) return GroundKind.Water;
            if (NetClearance(p, 0.5f, out _) < 0f || InPoly(p, GroundKind.Paved)) return GroundKind.Paved;
            if (InPoly(p, GroundKind.Sand)) return GroundKind.Sand;
            if (InPoly(p, GroundKind.Dirt)) return GroundKind.Dirt;
            return GroundKind.Grass;
        }

        // ───────────────────────── grid ─────────────────────────

        internal sealed class Grid
        {
            private readonly float _cell;
            private readonly Dictionary<long, List<int>> _cells = new Dictionary<long, List<int>>();
            private readonly List<int> _scratch = new List<int>();
            private readonly HashSet<int> _seen = new HashSet<int>();
            public Grid(float cell) { _cell = cell; }

            private static long Key(int x, int y) => ((long)x << 32) ^ (uint)y;

            public void Add(float2 p, float r, int v) => AddBox(p - r, p + r, v);

            public void AddBox(float2 min, float2 max, int v)
            {
                int x0 = (int)math.floor(min.x / _cell), x1 = (int)math.floor(max.x / _cell);
                int y0 = (int)math.floor(min.y / _cell), y1 = (int)math.floor(max.y / _cell);
                if ((long)(x1 - x0 + 1) * (y1 - y0 + 1) > 4096) { x1 = x0 + 63; y1 = y0 + 63; }
                for (int x = x0; x <= x1; x++)
                    for (int y = y0; y <= y1; y++)
                    {
                        long k = Key(x, y);
                        if (!_cells.TryGetValue(k, out var l)) _cells[k] = l = new List<int>(4);
                        l.Add(v);
                    }
            }

            /// <summary>Distinct values whose boxes touch the square of radius r around p (list reused: copy before nesting).</summary>
            public List<int> Near(float2 p, float r)
            {
                _scratch.Clear(); _seen.Clear();
                int x0 = (int)math.floor((p.x - r) / _cell), x1 = (int)math.floor((p.x + r) / _cell);
                int y0 = (int)math.floor((p.y - r) / _cell), y1 = (int)math.floor((p.y + r) / _cell);
                for (int x = x0; x <= x1; x++)
                    for (int y = y0; y <= y1; y++)
                        if (_cells.TryGetValue(Key(x, y), out var l))
                            foreach (var v in l) if (_seen.Add(v)) _scratch.Add(v);
                return _scratch;
            }
        }
    }
}
