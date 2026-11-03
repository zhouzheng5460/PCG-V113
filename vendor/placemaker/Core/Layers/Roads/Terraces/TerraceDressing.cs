// Placemaker roads layer (s52) — "Hillside terraces": what the banks look like and where the zones go.
//
//   paint   the banks the build makes (changed ground steeper than ~12 %): dry grass in soft patches, worn dirt on the
//           steepest faces (a "wall" style face is mostly dirt) — s33 toolkit fields (one baked bank mask × noise), so a
//           whole hillside is two paints. The terraces keep the map's lawn.
//   trees   clumps of trees on the banks, never on a terrace, a road corridor or a walk.
//   (zones: pre-zoning only puts cells on level ground, per zone block column as many rows from the road as lie flat
//           — ≤ the cross-fall + 2.5 %, or the flat threshold — never more than the terrace's cells: RoadPreZone.Rects.)
// Pure and deterministic.

using System;
using System.Collections.Generic;
using Placemaker.Core.Geometry;
using Placemaker.Core.Layers.Common;
using Placemaker.Core.Paint;
using Unity.Mathematics;

namespace Placemaker.Core.Layers.Roads.Terraces
{
    internal static class TerraceDressing
    {
        /// <summary>Bank strength per grid node (0..1) of the built field: changed ground steeper than ~12 %.</summary>
        private static float[] BankMask(TerraceGround g, float lo, float hi)
        {
            var sc = g.Grid;
            var m = new float[sc.Count];
            int nx = sc.Nx, ny = sc.Ny;
            for (int j = 1; j < ny - 1; j++)
                for (int i = 1; i < nx - 1; i++)
                {
                    int k = j * nx + i;
                    if (!sc.Free[k] || g.Terrace[k] || g.Corridor[k]) continue;
                    float change = math.abs(sc.Offset(k));
                    if (change < 0.25f) continue;
                    float gx = (sc.Target[k + 1] - sc.Target[k - 1]) / (2f * sc.Cell), gy = (sc.Target[k + nx] - sc.Target[k - nx]) / (2f * sc.Cell);
                    float slope = math.sqrt(gx * gx + gy * gy);
                    m[k] = Sculpt.Smooth01((slope - lo) / math.max(1e-3f, hi - lo)) * math.saturate(change / 0.8f);
                }
            return m;
        }

        /// <summary>Dry grass and worn dirt on the banks. Returns the number of paints.</summary>
        public static int Paint(AreaPlan plan, TerraceGround g, float2[] outline, TerraceOptions o)
        {
            if (g?.Grid == null || g.Cells == 0) return 0;
            var sc = g.Grid;
            var kit = new PaintKit(plan.Paint, g.HeightAfter, Hashing.Mix(o.Seed, 0x7E44ACEu)) { Outline = outline, MaxPaints = 4 };
            var bank = PaintShape.Grid(sc.Origin, sc.Cell, sc.Nx, sc.Ny, BankMask(g, 0.10f, 0.22f));
            var face = PaintShape.Grid(sc.Origin, sc.Cell, sc.Nx, sc.Ny, BankMask(g, o.Wall ? 0.2f : 0.27f, o.Wall ? 0.3f : 0.4f));
            int before = plan.Paint.Count;
            kit.Shape(bank.Mul(PaintShape.Noise(22f, 0.72f, 0.25f, kit.NextSeed())), TerrainMaterials.Dry, 0.6f);
            kit.Shape(face.Mul(PaintShape.Noise(10f, o.Wall ? 0.85f : 0.55f, 0.3f, kit.NextSeed())), TerrainMaterials.Dirt, o.Wall ? 0.75f : 0.5f);
            return plan.Paint.Count - before;
        }

        /// <summary>Tree clumps on the banks. Returns the count.</summary>
        public static int Trees(AreaPlan plan, TerraceGround g, float2[] outline, TerraceOptions o, RoadNetwork net, Func<NetRole, float> hw, RoadObstacles obstacles)
        {
            if (g?.Grid == null || g.Cells == 0) return 0;
            var sc = g.Grid;
            var mask = BankMask(g, 0.12f, 0.2f);
            // Roads and walks keep their verges clear.
            var idx = new SegGrid(16f);
            var halves = new List<float>();
            var pts = new List<float3>();
            foreach (var e in net.Edges)
            {
                if (!e.Alive) continue;
                pts.Clear();
                e.Curve.Flatten(pts, null, 4f, 8f);
                float h = (e.Walk ? 1.5f : hw(e.Role)) + 3f;
                int id = halves.Count; halves.Add(h);
                for (int k = 0; k + 1 < pts.Count; k++) idx.Add(pts[k].xz, pts[k + 1].xz, id, k);
            }
            var scratch = new List<int>();
            uint seed = Hashing.Mix(o.Seed, 0x7EE5u);
            float spacing = 9f;
            float bankArea = 0f;
            foreach (var v in mask) bankArea += v;
            bankArea *= sc.Cell * sc.Cell;
            int cap = math.min(1600, (int)(bankArea / 110f));
            int placed = 0;
            Poly.Bounds(outline, out float2 mn, out float2 mx);
            for (float z = mn.y; z <= mx.y && placed < cap; z += spacing)
                for (float x = mn.x; x <= mx.x && placed < cap; x += spacing)
                {
                    uint h = Hashing.Mix(seed, (int)math.floor(x * 7f), (int)math.floor(z * 3f));
                    var p = new float2(x, z) + (new float2(Hashing.Unit(h), Hashing.Unit(Hashing.Mix(h))) - 0.5f) * spacing * 0.8f;
                    float b = sc.Sample(mask, p);
                    if (!(b > 0.55f)) continue;
                    if (ValueNoise.Clumps(p, 38f, seed) < 0.48f) continue;
                    if (!Poly.Contains(outline, p) || Poly.DistanceToBoundary(outline, p) < 3f) continue;
                    if (obstacles != null && obstacles.BlockedAt(p, 4f)) continue;
                    bool clear = true;
                    idx.Query(p - 16f, p + 16f, scratch);
                    foreach (int si in scratch)
                        if (Seg.DistSq(p, idx.A[si], idx.B[si], out _) < halves[idx.Owner[si]] * halves[idx.Owner[si]]) { clear = false; break; }
                    if (!clear) continue;
                    uint v2 = Hashing.Mix(h, 0x51u);
                    plan.Objects.Add(new PlannedObject
                    {
                        Position = new float3(p.x, g.HeightAfter(p), p.y), Yaw = Hashing.Unit(v2) * 6.2831853f, Role = ObjectRole.Tree,
                        Asset = new AssetRef(""), Category = AssetCategories.Tree, Variation = v2, Age = 1 + (int)(v2 % 3u),
                    });
                    placed++;
                }
            return placed;
        }
    }
}
