// Placemaker — the dressing rules, one per family (s21). See Dressing.cs for the overview.
//
// Numbers are expectations per host at Decals = 100 %, family = 100 %, Lived-in, before the style
// weight of the decal's kind (Dressing.StyleWeights). Priority orders the budget: what defines the
// host (oil in a bay, litter under a tree, a worn goalmouth) beats ambient scatter.

using System;
using System.Collections.Generic;
using Placemaker.Core.Build;
using Placemaker.Core.Layers.Common;
using Unity.Mathematics;
using Kind = Placemaker.Core.Decals.Dressing.Kind;

namespace Placemaker.Core.Decals
{
    internal static class Rules
    {
        private const int ForbidPaved = 1 << (int)GroundKind.Paved;
        private const int ForbidSand = 1 << (int)GroundKind.Sand;
        private const int ForbidGrass = 1 << (int)GroundKind.Grass;

        internal sealed class Ctx
        {
            public readonly DressingWorld W;
            public readonly AreaPlan L;
            public readonly int Layer;
            public readonly DressSettings S;
            public readonly List<Dressing.Candidate> Out;
            public readonly uint Seed;
            /// <summary>The plan that IS the fill step (open-ground scatter runs once per step, not per feature).</summary>
            public readonly bool Main;
            public readonly LayerRegion Region;

            public Ctx(DressingWorld w, AreaPlan l, int layer, DressSettings s, List<Dressing.Candidate> output, uint seed)
            {
                W = w; L = l; Layer = layer; S = s; Out = output; Seed = seed;
                Main = l.GeneratorId == s.LayerId && !s.MainTaken;
                if (Main) s.MainTaken = true;
                Region = (s.Ring != null && s.Ring.Length >= 3 ? LayerRegion.From(s.Ring) : null) ?? w.Root;
            }

            /// <summary>Expected amount for a family and a kind: master × family × style weight.</summary>
            public float A(DressFamily f, Kind k) => S.Master * S.Of(f) * Dressing.Weight(S.Style, k);

            public uint H(int a, int b = 0, int c = 0) => Hashing.Mix(Hashing.Mix(Seed, a, b), unchecked((uint)c * 0x9E3779B1u + 7u));
            public static uint H2(uint h, int k) => Hashing.Mix(h, unchecked((uint)k + 0x632BE5ABu));

            public static bool Roll(uint h, float p) => p > 0f && Hashing.Unit(h) < p;

            /// <summary>Integer count with the fractional part as a probability.</summary>
            public static int Count(uint h, float expected)
            {
                if (!(expected > 0f)) return 0;
                int n = (int)math.floor(expected);
                return n + (Hashing.Unit(h) < expected - n ? 1 : 0);
            }

            public static float U(uint h) => Hashing.Unit(h);
            public static float Yaw(float2 dir) => math.atan2(dir.x, dir.y);
            public static float RandomYaw(uint h) => Hashing.Unit(h) * math.PI * 2f;

            public void Emit(float2 p, float yaw, string pool, float priority, uint h, int forbid = 0)
            {
                if (!Dressing.Allowed(pool, S.Style)) return;
                Out.Add(new Dressing.Candidate { P = p, Yaw = yaw, Pool = pool, Priority = priority, Hash = h, Layer = Layer, Forbid = forbid });
            }
        }

        public static void Run(Ctx c)
        {
            if (c.S.Of(DressFamily.Trees) > 0f) Trees(c);
            if (c.S.Of(DressFamily.Paths) > 0f) Paths(c);
            if (c.S.Of(DressFamily.Paved) > 0f) Paved(c);
            if (c.S.Of(DressFamily.Parking) > 0f) Parking(c);
            if (c.S.Of(DressFamily.Play) > 0f) Play(c);
            if (c.S.Of(DressFamily.Sports) > 0f) Sports(c);
            if (c.S.Of(DressFamily.Ground) > 0f && c.Main) Ground(c);
            if (c.Main && (c.S.Of(DressFamily.Ground) > 0f || c.S.Of(DressFamily.Paved) > 0f || c.S.Of(DressFamily.Shore) > 0f || c.S.Of(DressFamily.Works) > 0f)) Seams(c);
            if (c.S.Of(DressFamily.Shore) > 0f && c.Main) Shore(c);
            if (c.S.Of(DressFamily.Works) > 0f) Works(c);
            if (c.S.Of(DressFamily.Ruins) > 0f) Ruins(c);
            if (c.S.Of(DressFamily.Garden) > 0f) Garden(c);
        }

        private static string Cat(PlannedObject o) => RoleCategories.ForObject(o) ?? "";

        private static float2 Forward(float yaw) { math.sincos(yaw, out float s, out float co); return new float2(s, co); }
        private static float2 Perp(float2 t) => new float2(-t.y, t.x);

        private static float Footprint(PlannedObject o, SiteContext site, float fallback)
        {
            if (!string.IsNullOrEmpty(o.Asset.Id) && site?.Assets != null && site.Assets.TryGetValue(o.Asset.Id, out var info))
            {
                float r = math.cmax(info.BoundsMax.xz - info.BoundsMin.xz) * 0.5f;
                if (r > 0.1f) return r * (o.Scale > 0.05f ? o.Scale : 1f);
            }
            return fallback;
        }

        // ───────────────────────── Trees: litter under the crown ─────────────────────────

        private static void Trees(Ctx c)
        {
            float leaves = c.A(DressFamily.Trees, Kind.Leaves);
            if (leaves <= 0f) return;
            for (int i = 0; i < c.L.Objects.Count; i++)
            {
                var o = c.L.Objects[i];
                if (o == null || (o.Role != ObjectRole.Tree && o.Role != ObjectRole.Bush)) continue;
                if (o.Role == ObjectRole.Bush && Cat(o).StartsWith(AssetCategories.Hedge, StringComparison.Ordinal)) continue;
                bool tree = o.Role == ObjectRole.Tree;
                float crown = tree ? DressingWorld.CrownOf(o, c.W.Site) : 1.2f;
                bool conifer = DressingWorld.IsConifer(o);
                var p = o.Position.xz;
                uint h = c.H(i, 11);
                // A grown tree drops a litter bed about its crown; young ones and shrubs a little.
                float expected = tree ? 1.0f * math.clamp(crown / 3.6f, 0.5f, 1.4f) : 0.12f;   // s28: sparser
                int n = Ctx.Count(h, expected * leaves);
                for (int k = 0; k < n; k++)
                {
                    uint hk = Ctx.H2(h, k + 1);
                    float ang = Ctx.U(Ctx.H2(hk, 1)) * math.PI * 2f;
                    float dist = math.sqrt(Ctx.U(Ctx.H2(hk, 2))) * crown * 0.85f;
                    var q = p + new float2(math.cos(ang), math.sin(ang)) * dist;
                    // s23: the litter bed fades toward paths and paving — only a few leaves are carried onto them.
                    if (c.W.GroundAt(q) == GroundKind.Paved) continue;   // leaf clumps on a path read as dirt (user, s23)
                    // Conifers drop needles (mulch-like beds); broadleaves leaves, with a mulch ring at some trunks.
                    string pool = conifer ? DecalPools.Mulch
                        : tree && dist < crown * 0.35f && Ctx.U(Ctx.H2(hk, 3)) < 0.3f ? DecalPools.Mulch : AssetCategories.DecalLeaves;
                    c.Emit(q, Ctx.RandomYaw(Ctx.H2(hk, 4)), pool, (tree ? 3f : 1.4f) - k * 0.25f, hk);
                }
            }
        }

        // ───────────────────────── Paths: edges, cracks, junctions, entrances ─────────────────────────

        private static bool IsPath(NetRole r) => r == NetRole.Pathway || r == NetRole.PathwayWide;

        private static void Paths(Ctx c)
        {
            float aLeaves = c.A(DressFamily.Paths, Kind.Leaves), aWear = c.A(DressFamily.Paths, Kind.Wear), aCrack = c.A(DressFamily.Paths, Kind.Crack),
                aMoss = c.A(DressFamily.Paths, Kind.Moss), aPuddle = c.A(DressFamily.Paths, Kind.Puddle), aLitter = c.A(DressFamily.Paths, Kind.Litter);
            var ends = new List<(float2 p, float2 dir, float hw, bool attached)>();
            // s23 (user: "be much more conscious of where they are"): wear and dust follow feet — near junctions, entrances
            // and benches — instead of lining every metre of path.
            var traffic = Traffic(c);
            for (int ni = 0; ni < c.L.Nets.Count; ni++)
            {
                var n = c.L.Nets[ni];
                if (n == null || !IsPath(n.Role)) continue;
                float hw = DressingWorld.HalfWidth(n, c.W.Site);
                int s = 0;
                foreach (var (p, t) in Along(n, 3.5f, c.H(ni, 21)))
                {
                    uint h = c.H(ni, 22, s++);
                    float treeD = c.W.NearestTree(p, 9f, out _);
                    float shade = treeD < 8f ? 1f - treeD / 8f : 0f;
                    float feet = TrafficAt(traffic, p, 9f);
                    var nrm = Perp(t);
                    for (int side = -1; side <= 1; side += 2)
                    {
                        uint hs = Ctx.H2(h, side + 5);
                        var edge = nrm * side;
                        if (Ctx.Roll(Ctx.H2(hs, 1), (0.7f * shade) * aLeaves))
                            c.Emit(p + edge * (hw + 0.25f), Ctx.Yaw(t), DecalPools.LeavesEdge, 2.2f + shade, Ctx.H2(hs, 2));
                        if (feet > 0f && Ctx.Roll(Ctx.H2(hs, 3), 0.45f * feet * aWear))
                            c.Emit(p + edge * (hw + 0.6f), Ctx.Yaw(t), DecalPools.Wear, 1.6f + feet, Ctx.H2(hs, 4), ForbidPaved);
                        if (shade > 0f && Ctx.Roll(Ctx.H2(hs, 5), 0.14f * shade * aMoss))
                            c.Emit(p + edge * math.max(0.2f, hw - 0.3f), Ctx.Yaw(t), DecalPools.Moss, 1.8f, Ctx.H2(hs, 6));
                    }
                    float lat = (Ctx.U(Ctx.H2(h, 9)) - 0.5f) * hw * 1.2f;
                    // Cracks run along the path edge (where paving breaks), oriented with it.
                    if (Ctx.Roll(Ctx.H2(h, 10), 0.08f * aCrack))
                        c.Emit(p + nrm * (Ctx.U(Ctx.H2(h, 11)) < 0.5f ? -1f : 1f) * math.max(0.3f, hw - 0.5f), Ctx.Yaw(t), AssetCategories.DecalCracks, 1.7f, Ctx.H2(h, 12));
                    if (Ctx.Roll(Ctx.H2(h, 13), 0.02f * aPuddle))
                        c.Emit(p + nrm * lat * 0.4f, Ctx.Yaw(t), AssetCategories.DecalPuddles, 1.2f, Ctx.H2(h, 14));
                    if (Ctx.Roll(Ctx.H2(h, 15), 0.03f * aLitter))
                        c.Emit(p + nrm * lat, Ctx.RandomYaw(Ctx.H2(h, 16)), AssetCategories.DecalLitter, 1.0f, Ctx.H2(h, 17));
                }
                ends.Add((n.A.xz, math.normalizesafe(n.B.xz - n.A.xz, math.normalizesafe(n.D.xz - n.A.xz)), hw, n.StartAttach >= 0));
                ends.Add((n.D.xz, math.normalizesafe(n.C.xz - n.D.xz, math.normalizesafe(n.A.xz - n.D.xz)), hw, n.EndAttach >= 0));
            }
            Junctions(c, ends, aWear, aCrack, aLeaves);
        }

        /// <summary>Where feet cut the verge: path junctions and free ends (entrances). Benches and tables have their own wear (Play).</summary>
        private static List<float2> Traffic(Ctx c)
        {
            var ends = new Dictionary<long, (float2 p, int n)>();
            foreach (var l in c.W.Plans)
                foreach (var n in l.Nets)
                {
                    if (n == null || !IsPath(n.Role)) continue;
                    foreach (var e in new[] { n.A.xz, n.D.xz })
                    {
                        long k = ((long)math.floor(e.x * 2f) << 32) ^ (uint)math.floor(e.y * 2f);
                        ends[k] = ends.TryGetValue(k, out var v) ? (v.p, v.n + 1) : (e, 1);
                    }
                }
            var pts = new List<float2>();
            foreach (var v in ends.Values)
                if (v.n >= 3 || (v.n == 1 && c.W.Root.BoundaryDistance(v.p, 5f) < 4f)) pts.Add(v.p);
            return pts;
        }

        /// <summary>1 at a traffic point, fading to 0 at <paramref name="reach"/> m.</summary>
        private static float TrafficAt(List<float2> pts, float2 p, float reach)
        {
            float best = reach;
            foreach (var q in pts) best = math.min(best, math.distance(p, q));
            return 1f - best / reach;
        }

        /// <summary>Corner-cutting wear at junctions (people short-cut every corner) and scuffed entrances.</summary>
        private static void Junctions(Ctx c, List<(float2 p, float2 dir, float hw, bool attached)> ends, float aWear, float aCrack, float aLeaves)
        {
            var used = new bool[ends.Count];
            for (int i = 0; i < ends.Count; i++)
            {
                if (used[i]) continue;
                var group = new List<int> { i };
                used[i] = true;
                for (int j = i + 1; j < ends.Count; j++)
                    if (!used[j] && math.distancesq(ends[i].p, ends[j].p) < 0.6f) { group.Add(j); used[j] = true; }
                var J = ends[i].p;
                uint h = c.H((int)math.floor(J.x * 4f), (int)math.floor(J.y * 4f), 31);
                float hw = 0f;
                foreach (var g in group) hw = math.max(hw, ends[g].hw);
                if (group.Count >= 3)
                {
                    group.Sort((a, b) => math.atan2(ends[a].dir.y, ends[a].dir.x).CompareTo(math.atan2(ends[b].dir.y, ends[b].dir.x)));
                    for (int k = 0; k < group.Count; k++)
                    {
                        var d1 = ends[group[k]].dir; var d2 = ends[group[(k + 1) % group.Count]].dir;
                        float gap = math.atan2(d1.x * d2.y - d1.y * d2.x, math.dot(d1, d2));
                        if (gap < 0f) gap += math.PI * 2f;
                        if (gap > math.radians(150f)) continue;   // a straight-through side has no corner to cut
                        var bis = math.normalizesafe(d1 + d2, Perp(d1));
                        uint hk = Ctx.H2(h, k);
                        if (Ctx.Roll(hk, 0.35f * aWear))   // s28: fewer
                            c.Emit(J + bis * (hw + 1.2f), Ctx.Yaw(Perp(bis)), DecalPools.Wear, 2.6f, Ctx.H2(hk, 1), ForbidPaved);
                    }
                    if (Ctx.Roll(Ctx.H2(h, 40), 0.35f * aCrack))
                        c.Emit(J, Ctx.RandomYaw(Ctx.H2(h, 41)), AssetCategories.DecalCracks, 1.9f, Ctx.H2(h, 42));
                }
                else if (group.Count == 1 && (ends[i].attached || c.W.Root.BoundaryDistance(J, 4f) < 3.5f))
                {
                    // An entrance: trodden verges on both sides, dirt and leaves carried onto the path.
                    var t = -ends[i].dir; var nrm = Perp(t);
                    for (int side = -1; side <= 1; side += 2)
                        if (Ctx.Roll(Ctx.H2(h, 50 + side), 0.4f * aWear))   // s28: fewer
                            c.Emit(J + nrm * side * (hw + 0.9f) + t * 1.5f, Ctx.Yaw(t), DecalPools.Wear, 2.7f, Ctx.H2(h, 52 + side), ForbidPaved);
                    if (Ctx.Roll(Ctx.H2(h, 55), 0.4f * aLeaves))
                        c.Emit(J + t * 2.5f, Ctx.Yaw(t), DecalPools.LeavesEdge, 2.0f, Ctx.H2(h, 56));
                }
            }
        }

        /// <summary>Points every ~<paramref name="step"/> m along a net (jittered, never at the very ends) with the unit tangent.</summary>
        internal static IEnumerable<(float2 p, float2 t)> Along(PlannedNet n, float step, uint seed)
        {
            const int N = 24;
            var pts = new float2[N + 1];
            var cum = new float[N + 1];
            for (int i = 0; i <= N; i++)
            {
                pts[i] = Composer.Bezier(n.A, n.B, n.C, n.D, i / (float)N).xz;
                if (i > 0) cum[i] = cum[i - 1] + math.distance(pts[i - 1], pts[i]);
            }
            float len = cum[N];
            if (!(len > 0.5f)) yield break;
            int count = math.max(1, (int)math.floor(len / step));
            int seg = 0;
            for (int k = 0; k < count; k++)
            {
                float s = (k + 0.25f + 0.5f * Hashing.Unit(Hashing.Mix(seed, (uint)k))) * len / count;
                while (seg < N - 1 && cum[seg + 1] < s) seg++;
                float segLen = math.max(1e-4f, cum[seg + 1] - cum[seg]);
                float u = math.saturate((s - cum[seg]) / segLen);
                var t = math.normalizesafe(pts[seg + 1] - pts[seg], new float2(0, 1));
                yield return (math.lerp(pts[seg], pts[seg + 1], u), t);
            }
        }

        // ───────────────────────── Paved: plazas and hard ground ─────────────────────────

        private static void Paved(Ctx c)
        {
            var S = c.S;
            float amt = S.Master * S.Of(DressFamily.Paved);
            for (int si = 0; si < c.L.Surfaces.Count; si++)
            {
                var s = c.L.Surfaces[si];
                if (s?.Ring == null || s.Ring.Length < 3 || DressingWorld.KindOf(s) != GroundKind.Paved) continue;
                // (s25) Lot asphalt is dressed by the Parking family (bays, aisles) only — the paved scatter doubled it.
                if (s.Role == SurfaceRole.Asphalt) continue;
                var reg = LayerRegion.From(s.Ring);
                if (reg == null || reg.Area < 20f) continue;
                if (S.Style != DressStyle.Gritty) break;   // s23: open paving stays clean unless the place is meant to be gritty
                bool asphalt = s.Role == SurfaceRole.Asphalt || (s.Category ?? "").StartsWith("surface.asphalt", StringComparison.Ordinal);
                Scatter(c, reg, amt, si * 7 + 101, new[]
                {
                    (AssetCategories.DecalCracks, 0.5f * Dressing.Weight(S.Style, Kind.Crack), 1.4f, 0),
                    (DecalPools.Stain, 0.55f * Dressing.Weight(S.Style, Kind.Stain), 1.5f, 0),
                    (DecalPools.Patch, (asphalt ? 0.2f : 0.05f) * Dressing.Weight(S.Style, Kind.Crack), 1.2f, 0),
                    (AssetCategories.DecalPuddles, 0.06f * Dressing.Weight(S.Style, Kind.Puddle), 1.1f, 0),
                    (AssetCategories.DecalLitter, 0.12f * Dressing.Weight(S.Style, Kind.Litter), 1.0f, 0),
                    (DecalPools.Glass, 0.05f * Dressing.Weight(S.Style, Kind.Glass), 0.9f, 0),
                    (AssetCategories.DecalGraffiti, 0.012f * Dressing.Weight(S.Style, Kind.Graffiti), 0.8f, 0),
                }, 14f, q => reg.BoundaryDistance(q, 4f) < 3f ? 1.7f : 0.45f);   // s23: wear gathers at the paving's edges, the middle stays clean
            }
            // Hot spots on paving: where people sit, eat, throw things away and splash.
            float aStain = c.A(DressFamily.Paved, Kind.Stain), aLitter = c.A(DressFamily.Paved, Kind.Litter), aWet = c.A(DressFamily.Paved, Kind.Puddle) + 0.4f * amt;
            for (int i = 0; i < c.L.Objects.Count; i++)
            {
                var o = c.L.Objects[i];
                if (o == null) continue;
                var p = o.Position.xz;
                if (c.W.GroundAt(p) != GroundKind.Paved) continue;
                uint h = c.H(i, 61);
                string cat = Cat(o);
                switch (o.Role)
                {
                    case ObjectRole.Bench:
                        if (Ctx.Roll(h, 0.4f * aStain)) c.Emit(p + Forward(o.Yaw) * 0.8f, o.Yaw + math.PI * 0.5f, DecalPools.Stain, 2.2f, Ctx.H2(h, 1));
                        if (Ctx.Roll(Ctx.H2(h, 2), 0.15f * aLitter)) c.Emit(p + Forward(o.Yaw) * 1.1f, Ctx.RandomYaw(Ctx.H2(h, 3)), AssetCategories.DecalLitter, 1.6f, Ctx.H2(h, 4));
                        break;
                    case ObjectRole.Bin:
                        if (Ctx.Roll(h, 0.5f * aLitter + 0.1f * amt)) c.Emit(p + Jitter(h, 0.7f), Ctx.RandomYaw(Ctx.H2(h, 5)), AssetCategories.DecalLitter, 2.3f, Ctx.H2(h, 6));
                        if (Ctx.Roll(Ctx.H2(h, 7), 0.4f * aStain)) c.Emit(p + Jitter(Ctx.H2(h, 8), 0.6f), Ctx.RandomYaw(Ctx.H2(h, 9)), DecalPools.Stain, 2.0f, Ctx.H2(h, 10));
                        break;
                    case ObjectRole.Centerpiece:
                    {
                        float r = Footprint(o, c.W.Site, 5.5f);
                        string idl = (o.Asset.Id ?? "").ToLowerInvariant();
                        bool fountain = cat.Contains("fountain") || cat.Contains("water") || idl.Contains("fountain") || idl.Contains("water");
                        int n = Ctx.Count(h, (fountain ? 7f : 3f) * (fountain ? aWet : aStain));
                        for (int k = 0; k < n; k++)
                        {
                            uint hk = Ctx.H2(h, 20 + k);
                            float ang = (k + Ctx.U(hk) * 0.6f) / math.max(1, n) * math.PI * 2f;
                            var dir = new float2(math.cos(ang), math.sin(ang));
                            c.Emit(p + dir * (r + 0.4f + Ctx.U(Ctx.H2(hk, 1)) * 1.4f), Ctx.Yaw(Perp(dir)), fountain ? DecalPools.Wet : DecalPools.Stain, 2.6f, Ctx.H2(hk, 2));
                        }
                        break;
                    }
                    default:
                        if (cat.StartsWith("kiosk", StringComparison.Ordinal) || cat.StartsWith("seating.cafe", StringComparison.Ordinal)
                            || cat.Contains("foodTruck") || cat.Contains("market") || cat.StartsWith(AssetCategories.Picnic, StringComparison.Ordinal))
                        {
                            int n = Ctx.Count(h, 2.2f * aStain);
                            for (int k = 0; k < n; k++)
                                c.Emit(p + Jitter(Ctx.H2(h, 30 + k), 2.5f), Ctx.RandomYaw(Ctx.H2(h, 40 + k)), DecalPools.Stain, 2.1f, Ctx.H2(h, 50 + k));
                            if (Ctx.Roll(Ctx.H2(h, 60), 0.5f * aLitter)) c.Emit(p + Jitter(Ctx.H2(h, 61), 2f), Ctx.RandomYaw(Ctx.H2(h, 62)), AssetCategories.DecalLitter, 1.8f, Ctx.H2(h, 63));
                        }
                        break;
                }
            }
        }

        private static float2 Jitter(uint h, float r)
        {
            float a = Hashing.Unit(Hashing.Mix(h, 0x51u)) * math.PI * 2f, d = math.sqrt(Hashing.Unit(Hashing.Mix(h, 0x52u))) * r;
            return new float2(math.cos(a), math.sin(a)) * d;
        }

        /// <summary>
        /// Clustered scatter over a region: <paramref name="kinds"/> = (pool, expected per 100 m², priority, forbid mask).
        /// Value-noise clumps (patches of wear, not an even sprinkle); <paramref name="clump"/> = clump size (m).
        /// </summary>
        private static void Scatter(Ctx c, LayerRegion reg, float amount, int salt, (string pool, float per100, float pri, int forbid)[] kinds, float clump, Func<float2, float> bias = null)
        {
            if (amount <= 0f || reg == null) return;
            float total = 0f;
            foreach (var k in kinds) total += k.per100;
            if (total <= 0f) return;
            float expected = reg.Area / 100f * total * amount;
            if (expected < 0.05f) return;
            const float cell = 2.5f;
            int max = math.min(40000, (int)(reg.Area / (cell * cell)) + 8);
            var cands = Layers.Common.Scatter.Candidates(reg, cell, c.H(salt, 71), max, default);
            if (cands.Count == 0) return;
            // Clumping concentrates the same total into noise highs.
            float perCand = expected / cands.Count;
            uint clumpSeed = c.H(salt, 72);
            for (int i = 0; i < cands.Count; i++)
            {
                var p = cands[i];
                uint h = c.H(salt, 73, i);
                float cl = Layers.Common.Scatter.Clustering(p, clump, clumpSeed, 0.05f) * (bias != null ? bias(p) : 1f);
                if (!Ctx.Roll(h, perCand * cl * 1.9f)) continue;
                float pick = Ctx.U(Ctx.H2(h, 1)) * total;
                foreach (var k in kinds)
                {
                    pick -= k.per100;
                    if (pick > 0f) continue;
                    c.Emit(p, Ctx.RandomYaw(Ctx.H2(h, 2)), k.pool, k.pri, Ctx.H2(h, 3), k.forbid);
                    break;
                }
            }
        }

        // ───────────────────────── Parking: bays, aisles, entrances ─────────────────────────

        /// <summary>
        /// (s25) Share of bays with an oil blot at the default amount. Playtest: "too many decals, non-transparent, too
        /// prominent" — the s21 rates (42 % oil, a drip, tyre marks, stains every 7 m of aisle) covered the lot in opaque
        /// blobs and hid the stall lines. A lot reads as clean asphalt with the odd faint mark; the amount sliders and the
        /// gritty style still add more.
        /// </summary>
        public const float OilShare = 0.08f;

        private static void Parking(Ctx c)
        {
            float aOil = c.A(DressFamily.Parking, Kind.Oil), aTyre = c.A(DressFamily.Parking, Kind.Tyre), aCrack = c.A(DressFamily.Parking, Kind.Crack),
                aStain = c.A(DressFamily.Parking, Kind.Stain), aLitter = c.A(DressFamily.Parking, Kind.Litter), aGlass = c.A(DressFamily.Parking, Kind.Glass);
            int bay = 0;
            for (int i = 0; i < c.L.Objects.Count; i++)
            {
                var o = c.L.Objects[i];
                if (o == null || o.Role != ObjectRole.ParkingBay) continue;
                bay++;
                // +Z (yaw) points from the aisle to the back of the stall (PlanBuilder convention).
                var axis = Forward(o.Yaw); var side = Perp(axis);
                float len = 5.3f, wid = 2.6f;
                if (!string.IsNullOrEmpty(o.Asset.Id) && c.W.Site?.Assets != null && c.W.Site.Assets.TryGetValue(o.Asset.Id, out var info))
                {
                    float sz = info.BoundsMax.z - info.BoundsMin.z, sx = info.BoundsMax.x - info.BoundsMin.x;
                    if (sz > 3.5f && sz < 9f) len = sz;
                    if (sx > 1.8f && sx < 4.5f) wid = sx;
                }
                var p = o.Position.xz;
                uint h = c.H(i, 81);
                // Nose-in parking: the engine stands over the back third; drips run along the bay.
                if (Ctx.Roll(h, OilShare * aOil))
                    c.Emit(p + axis * (len * (0.1f + 0.22f * Ctx.U(Ctx.H2(h, 1)))) + side * ((Ctx.U(Ctx.H2(h, 2)) - 0.5f) * wid * 0.35f),
                        o.Yaw + (Ctx.U(Ctx.H2(h, 3)) - 0.5f) * 0.35f, DecalPools.Oil, 3f, Ctx.H2(h, 4));
                if (Ctx.Roll(Ctx.H2(h, 5), 0.01f * aOil))
                    c.Emit(p - axis * (len * 0.12f) + side * ((Ctx.U(Ctx.H2(h, 6)) - 0.5f) * wid * 0.4f), o.Yaw, DecalPools.Oil, 2.4f, Ctx.H2(h, 7));
                // Turning in: tyre marks at the stall mouth.
                if (Ctx.Roll(Ctx.H2(h, 8), 0.015f * aTyre))
                    c.Emit(p - axis * (len * 0.42f) + side * ((Ctx.U(Ctx.H2(h, 9)) - 0.5f) * wid * 0.5f), o.Yaw, DecalPools.Tyre, 1.8f, Ctx.H2(h, 10));
                if (Ctx.Roll(Ctx.H2(h, 11), 0.01f * aCrack))
                    c.Emit(p + Jitter(Ctx.H2(h, 12), wid * 0.4f), Ctx.RandomYaw(Ctx.H2(h, 13)), DecalPools.Patch, 1.3f, Ctx.H2(h, 14));
                // Rows against the edge collect what blows and what gets thrown: litter and (gritty) glass at the back.
                var back = p + axis * (len * 0.5f + 0.7f);
                if (c.W.Root.BoundaryDistance(back, 4f) < 3.5f)
                {
                    if (Ctx.Roll(Ctx.H2(h, 15), 0.03f * aLitter)) c.Emit(back, Ctx.RandomYaw(Ctx.H2(h, 16)), AssetCategories.DecalLitter, 1.5f, Ctx.H2(h, 17));
                    if (Ctx.Roll(Ctx.H2(h, 18), 0.02f * aGlass)) c.Emit(back + side * 0.8f, Ctx.RandomYaw(Ctx.H2(h, 19)), DecalPools.Glass, 1.5f, Ctx.H2(h, 20));
                }
            }
            // Aisles: stains along the drive line, repairs, wheel lines; tyre marks at turns and entrances.
            var ends = new List<(float2 p, float2 dir, bool attached)>();
            for (int ni = 0; ni < c.L.Nets.Count; ni++)
            {
                var n = c.L.Nets[ni];
                if (n == null || (n.Role != NetRole.DriveAisle && n.Role != NetRole.DriveConnector)) continue;
                int s = 0;
                foreach (var (p, t) in Along(n, 7f, c.H(ni, 91)))
                {
                    uint h = c.H(ni, 92, s++);
                    var nrm = Perp(t);
                    if (Ctx.Roll(h, 0.025f * aStain)) c.Emit(p + nrm * (Ctx.U(Ctx.H2(h, 1)) - 0.5f) * 2f, Ctx.Yaw(t), DecalPools.Stain, 1.4f, Ctx.H2(h, 2));
                    if (Ctx.Roll(Ctx.H2(h, 3), 0.01f * aCrack)) c.Emit(p + nrm * (Ctx.U(Ctx.H2(h, 4)) - 0.5f) * 2.5f, Ctx.RandomYaw(Ctx.H2(h, 5)), DecalPools.Patch, 1.3f, Ctx.H2(h, 6));
                    if (Ctx.Roll(Ctx.H2(h, 7), 0.01f * aTyre)) c.Emit(p + nrm * (Ctx.U(Ctx.H2(h, 8)) < 0.5f ? -1.2f : 1.2f), Ctx.Yaw(t), DecalPools.Tyre, 1.4f, Ctx.H2(h, 9));
                }
                ends.Add((n.A.xz, math.normalizesafe(n.D.xz - n.A.xz), n.StartAttach >= 0));
                ends.Add((n.D.xz, math.normalizesafe(n.A.xz - n.D.xz), n.EndAttach >= 0));
            }
            var used = new bool[ends.Count];
            for (int i = 0; i < ends.Count; i++)
            {
                if (used[i]) continue;
                var group = new List<int> { i };
                used[i] = true;
                for (int j = i + 1; j < ends.Count; j++)
                    if (!used[j] && math.distancesq(ends[i].p, ends[j].p) < 1f) { group.Add(j); used[j] = true; }
                var J = ends[i].p;
                uint h = c.H((int)math.floor(J.x * 4f), (int)math.floor(J.y * 4f), 95);
                bool entrance = false, turn = false;
                foreach (var g in group) if (ends[g].attached) entrance = true;
                if (group.Count >= 2)
                    for (int a = 0; a < group.Count && !turn; a++)
                        for (int b = a + 1; b < group.Count; b++)
                            if (math.abs(math.dot(ends[group[a]].dir, ends[group[b]].dir)) < 0.8f) { turn = true; break; }
                if (!entrance && !turn) continue;
                float scale = entrance ? 0.3f : 0.05f;
                foreach (var g in group)
                {
                    var d = ends[g].dir;
                    uint hg = Ctx.H2(h, g);
                    if (Ctx.Roll(hg, scale * aTyre)) c.Emit(J + d * 3f + Perp(d) * (Ctx.U(Ctx.H2(hg, 1)) - 0.5f) * 2f, Ctx.Yaw(d), DecalPools.Tyre, 2.5f, Ctx.H2(hg, 2));
                }
                if (Ctx.Roll(Ctx.H2(h, 9), 0.08f * aStain)) c.Emit(J, Ctx.RandomYaw(Ctx.H2(h, 10)), DecalPools.Stain, 2.2f, Ctx.H2(h, 11));
            }
        }

        // ───────────────────────── Play & picnic & seating ─────────────────────────

        private static void Play(Ctx c)
        {
            float aWear = c.A(DressFamily.Play, Kind.Wear), aLitter = c.A(DressFamily.Play, Kind.Litter);
            for (int i = 0; i < c.L.Objects.Count; i++)
            {
                var o = c.L.Objects[i];
                if (o == null) continue;
                var p = o.Position.xz;
                string cat = Cat(o);
                uint h = c.H(i, 111);
                if (cat.StartsWith(AssetCategories.Playground, StringComparison.Ordinal) || cat.StartsWith("play.", StringComparison.Ordinal))
                {
                    // Feet wear the ground bare under swings, slides and climbing frames.
                    float r = Footprint(o, c.W.Site, 1.5f);
                    int n = Ctx.Count(h, 1.1f * aWear * math.clamp(r / 1.5f, 0.6f, 2f));
                    for (int k = 0; k < n; k++)
                        c.Emit(p + Jitter(Ctx.H2(h, k), r * 0.6f), Ctx.RandomYaw(Ctx.H2(h, 10 + k)), DecalPools.Wear, 2.4f - k * 0.2f, Ctx.H2(h, 20 + k), ForbidPaved);
                }
                else if (cat.StartsWith(AssetCategories.Picnic, StringComparison.Ordinal))
                {
                    if (Ctx.Roll(h, 0.7f * aWear)) c.Emit(p + Jitter(Ctx.H2(h, 1), 0.4f), o.Yaw, DecalPools.Wear, 2.3f, Ctx.H2(h, 2), ForbidPaved);
                    if (Ctx.Roll(Ctx.H2(h, 3), 0.25f * aLitter)) c.Emit(p + Jitter(Ctx.H2(h, 4), 1.5f), Ctx.RandomYaw(Ctx.H2(h, 5)), AssetCategories.DecalLitter, 1.6f, Ctx.H2(h, 6));
                }
                else if (cat.StartsWith("camping", StringComparison.Ordinal) || cat.StartsWith("dog.", StringComparison.Ordinal))
                {
                    if (Ctx.Roll(h, 0.65f * aWear)) c.Emit(p + Jitter(Ctx.H2(h, 1), 0.6f), Ctx.RandomYaw(Ctx.H2(h, 2)), cat.Contains("fire") ? DecalPools.Rocks : DecalPools.Wear, 2.2f, Ctx.H2(h, 3), ForbidPaved);
                }
                else if (o.Role == ObjectRole.Bench)
                {
                    // Where feet rest in front of a bench on grass or gravel.
                    var front = p + Forward(o.Yaw) * 0.85f;
                    if (c.W.GroundAt(front) != GroundKind.Paved && Ctx.Roll(h, 0.5f * aWear))
                        c.Emit(front, o.Yaw + math.PI * 0.5f, DecalPools.Wear, 2.1f, Ctx.H2(h, 1), ForbidPaved);
                }
                else if (o.Role == ObjectRole.Bin)
                {
                    if (Ctx.Roll(h, 0.35f * aLitter + 0.05f)) c.Emit(p + Jitter(Ctx.H2(h, 1), 0.8f), Ctx.RandomYaw(Ctx.H2(h, 2)), AssetCategories.DecalLitter, 1.7f, Ctx.H2(h, 3));
                }
            }
        }

        // ───────────────────────── Sports: goalmouths, hoops, stations ─────────────────────────

        private static void Sports(Ctx c)
        {
            float aWear = c.A(DressFamily.Sports, Kind.Wear), aStain = c.A(DressFamily.Sports, Kind.Stain), aLitter = c.A(DressFamily.Sports, Kind.Litter),
                aGraf = c.A(DressFamily.Sports, Kind.Graffiti);
            var goals = new List<int>();
            for (int i = 0; i < c.L.Objects.Count; i++)
                if (c.L.Objects[i] != null && Cat(c.L.Objects[i]).StartsWith(AssetCategories.SportsGoal, StringComparison.Ordinal)) goals.Add(i);
            var centre = float2.zero;
            foreach (var g in goals) centre += c.L.Objects[g].Position.xz;
            if (goals.Count > 0) centre /= goals.Count;
            for (int i = 0; i < c.L.Objects.Count; i++)
            {
                var o = c.L.Objects[i];
                if (o == null) continue;
                string cat = Cat(o);
                var p = o.Position.xz;
                uint h = c.H(i, 121);
                if (cat.StartsWith(AssetCategories.SportsGoal, StringComparison.Ordinal))
                {
                    // The goalmouth: the keeper's patch and the penalty spot area, along the pitch axis.
                    var dir = math.lengthsq(centre - p) > 4f && goals.Count > 1 ? math.normalize(centre - p) : Forward(o.Yaw);
                    int n = Ctx.Count(h, 2.2f * aWear);
                    for (int k = 0; k < n; k++)
                    {
                        uint hk = Ctx.H2(h, k);
                        c.Emit(p + dir * (1.0f + 1.8f * k + Ctx.U(hk) * 0.8f) + Perp(dir) * (Ctx.U(Ctx.H2(hk, 1)) - 0.5f) * 1.6f,
                            Ctx.Yaw(Perp(dir)), DecalPools.Wear, 2.8f - k * 0.3f, Ctx.H2(hk, 2), ForbidPaved);
                    }
                }
                else if (cat.StartsWith(AssetCategories.SportsBasketball, StringComparison.Ordinal))
                {
                    if (Ctx.Roll(h, 0.6f * aStain)) c.Emit(p + Forward(o.Yaw) * 1.6f, Ctx.RandomYaw(Ctx.H2(h, 1)), DecalPools.Stain, 2.0f, Ctx.H2(h, 2));
                }
                else if (cat.StartsWith(AssetCategories.SportsFitness, StringComparison.Ordinal))
                {
                    if (Ctx.Roll(h, 0.75f * aWear)) c.Emit(p + Jitter(Ctx.H2(h, 1), 0.4f), Ctx.RandomYaw(Ctx.H2(h, 2)), DecalPools.Wear, 2.2f, Ctx.H2(h, 3), ForbidPaved);
                }
                else if (cat.StartsWith(AssetCategories.SportsSkate, StringComparison.Ordinal))
                {
                    if (Ctx.Roll(h, 0.5f * aStain)) c.Emit(p + Jitter(Ctx.H2(h, 1), 2.5f), Ctx.RandomYaw(Ctx.H2(h, 2)), DecalPools.Tyre, 1.8f, Ctx.H2(h, 3));
                    if (Ctx.Roll(Ctx.H2(h, 4), 0.35f * aGraf)) c.Emit(p + Jitter(Ctx.H2(h, 5), 3f), Ctx.RandomYaw(Ctx.H2(h, 6)), AssetCategories.DecalGraffiti, 1.6f, Ctx.H2(h, 7));
                }
                else if (cat.StartsWith(AssetCategories.SportsStand, StringComparison.Ordinal))
                {
                    if (Ctx.Roll(h, 0.45f * aLitter + 0.1f)) c.Emit(p + Forward(o.Yaw) * 1.5f + Jitter(Ctx.H2(h, 1), 1f), Ctx.RandomYaw(Ctx.H2(h, 2)), AssetCategories.DecalLitter, 1.7f, Ctx.H2(h, 3));
                }
            }
        }

        // ───────────────────────── Ground: lawns, dirt, sand ─────────────────────────

        private static readonly HashSet<string> NaturalTypes = new HashSet<string>(StringComparer.Ordinal)
        { "natureReserve", "hikingTrail", "lookoutHill", "wetland", "quarry", "campground", "greenwayPark" };

        private static void Ground(Ctx c)
        {
            var S = c.S;
            float amt = S.Master * S.Of(DressFamily.Ground);
            if (amt <= 0f) return;
            var reg = c.Region;
            bool natural = NaturalTypes.Contains(S.LayerId);
            const float cell = 4f;
            int max = math.min(40000, (int)(reg.Area / (cell * cell)) + 8);
            var cands = Layers.Common.Scatter.Candidates(reg, cell, c.H(131, 1), max, default);
            uint clump = c.H(131, 2);
            float perArea = cell * cell / 100f;
            float wLeaves = Dressing.Weight(S.Style, Kind.Leaves), wWear = Dressing.Weight(S.Style, Kind.Wear), wMud = Dressing.Weight(S.Style, Kind.Mud),
                wRocks = Dressing.Weight(S.Style, Kind.Rocks), wMoss = Dressing.Weight(S.Style, Kind.Moss), wLitter = Dressing.Weight(S.Style, Kind.Litter),
                wWrack = Dressing.Weight(S.Style, Kind.Wrack);
            for (int i = 0; i < cands.Count; i++)
            {
                var p = cands[i];
                uint h = c.H(131, 3, i);
                var g = c.W.GroundAt(p);
                if (g == GroundKind.Water || g == GroundKind.Paved) continue;
                float cl = Layers.Common.Scatter.Clustering(p, 16f, clump, 0.05f) * 1.9f;
                (string pool, float per100)[] kinds;
                switch (g)
                {
                    case GroundKind.Dirt:
                        kinds = new[] { (DecalPools.Mud, 0.22f * wMud), (DecalPools.Wear, 0.18f * wWear), (DecalPools.Rocks, (natural ? 0.18f : 0.08f) * wRocks),
                            (AssetCategories.DecalPuddles, 0.03f * wMud), (AssetCategories.DecalLitter, 0.02f * wLitter) };
                        break;
                    case GroundKind.Sand:
                        // Sand on sand is invisible: sand ground gets wrack and footprints; sand DUST goes where sand meets paving (Seams).
                        kinds = new[] { (DecalPools.Sand, 0f), (DecalPools.Beach, 0.1f * wWrack), (DecalPools.Footprints, 0.08f), (AssetCategories.DecalLitter, 0.015f * wLitter) };
                        break;
                    default:
                        float treeD = c.W.NearestTree(p, 10f, out _);
                        float shade = treeD < 9f ? 1f - treeD / 9f : 0f;
                        // s23: open lawn is not scattered at random — only shade (leaves, moss) and natural ground (stones).
                        kinds = new[] { (AssetCategories.DecalLeaves, 0.06f * shade * wLeaves), (DecalPools.Wear, 0f),
                            (DecalPools.Moss, (natural ? 0.04f : 0.04f * shade) * wMoss), (DecalPools.Rocks, (natural ? 0.06f : 0f) * wRocks),
                            (AssetCategories.DecalLitter, 0f) };
                        break;
                }
                float total = 0f;
                foreach (var k in kinds) total += k.per100;
                if (!Ctx.Roll(h, total * perArea * amt * cl)) continue;
                float pick = Ctx.U(Ctx.H2(h, 1)) * total;
                foreach (var k in kinds)
                {
                    pick -= k.per100;
                    if (pick > 0f) continue;
                    c.Emit(p, Ctx.RandomYaw(Ctx.H2(h, 2)), k.pool, 1.0f, Ctx.H2(h, 3), ForbidPaved);
                    break;
                }
            }
        }

        // ───────────────────────── Seams: where one ground meets another ─────────────────────────

        /// <summary>
        /// (s23, user: the sand dusting spreading over paving at an edge was "perfect — you almost don't notice it"). Along
        /// every sand or dirt area's outline where it meets paving or a path, the ground is carried over: sand dust or dirt
        /// dust on the paving side, along the edge, in soft runs. The most effective decal there is.
        /// </summary>
        private static void Seams(Ctx c)
        {
            float amt = c.S.Master * math.max(math.max(c.S.Of(DressFamily.Ground), c.S.Of(DressFamily.Paved)), math.max(c.S.Of(DressFamily.Shore), c.S.Of(DressFamily.Works)));
            if (amt <= 0f) return;
            float wWear = Dressing.Weight(c.S.Style, Kind.Wear);
            uint run = c.H(301, 1);
            for (int pi = 0; pi < c.W.Polys.Count; pi++)
            {
                var poly = c.W.Polys[pi];
                if (poly.Kind != GroundKind.Sand && poly.Kind != GroundKind.Dirt) continue;
                bool sand = poly.Kind == GroundKind.Sand;
                var ring = Geometry.Poly.EnsureCcw(poly.Ring);
                for (int i = 0; i < ring.Length; i++)
                {
                    var a = ring[i]; var b = ring[(i + 1) % ring.Length];
                    float len = math.distance(a, b);
                    if (len < 0.5f) continue;
                    var t = (b - a) / len;
                    var outward = new float2(t.y, -t.x);   // CCW ring: right of the edge is outside
                    for (float d = 1.25f; d < len; d += 2.5f)
                    {
                        var p = a + t * d;
                        if (!c.Region.Contains(p)) continue;
                        var outside = p + outward * 1.5f;
                        if (c.W.GroundAt(outside) != GroundKind.Paved) continue;
                        uint h = c.H(pi, i, (int)(d * 4f) + 305);
                        // Soft runs along the edge (value noise), not an even dotted line.
                        float runs = ValueNoise.Sample((p + 500f) / 7f, run);
                        float prob = (sand ? 0.75f : 0.5f * wWear) * amt * (0.35f + runs);
                        int count = Ctx.Count(h, prob);
                        for (int k = 0; k < count; k++)
                            c.Emit(p + outward * (0.6f + 0.7f * k + Ctx.U(Ctx.H2(h, k)) * 0.4f), Ctx.Yaw(t), sand ? DecalPools.Sand : DecalPools.Wear, 2.9f - k * 0.2f, Ctx.H2(h, 10 + k));
                    }
                }
            }
        }

        // ───────────────────────── Shore: waterline bands and wrack ─────────────────────────

        private static void Shore(Ctx c)
        {
            float aWrack = c.A(DressFamily.Shore, Kind.Wrack), aLitter = c.A(DressFamily.Shore, Kind.Litter), aMud = c.A(DressFamily.Shore, Kind.Mud);
            if (aWrack <= 0f) return;
            var reg = c.Region;
            const float step = 2.5f;
            var size = reg.Max - reg.Min;
            if (size.x * size.y / (step * step) > 60000f) return;
            // s30: a waterfront is not sand by default (playtest 57: pale sand and wet-sand blotches on the promenade paving);
            // only a beach is, elsewhere only ground painted or surfaced as sand.
            bool beachy = c.S.LayerId == "beach";
            // Cheap probe first: no water anywhere near the step (10 m grid) → no shore.
            bool any = false;
            for (float y = reg.Min.y - 4f; y <= reg.Max.y + 4f && !any; y += 10f)
                for (float x = reg.Min.x - 4f; x <= reg.Max.x + 4f && !any; x += 10f)
                    if (c.W.IsWater(new float2(x, y))) any = true;
            if (!any) return;
            var dirs = new float2[8];
            for (int k = 0; k < 8; k++) { float a = k * math.PI / 4f; dirs[k] = new float2(math.cos(a), math.sin(a)); }
            var taken = new HashSet<long>();
            int idx = 0;
            for (float y = reg.Min.y + step * 0.5f; y < reg.Max.y; y += step)
                for (float x = reg.Min.x + step * 0.5f; x < reg.Max.x; x += step)
                {
                    var p = new float2(x, y);
                    if (!reg.Contains(p) || c.W.IsWater(p)) continue;
                    var toWater = float2.zero;
                    int wet = 0;
                    foreach (var d in dirs)
                        if (c.W.IsWater(p + d * 3f)) { toWater += d; wet++; }
                    if (wet == 0 || wet > 6) continue;
                    long cellKey = ((long)math.floor(x / 4f) << 32) ^ (uint)math.floor(y / 4f);
                    if (!taken.Add(cellKey)) continue;
                    idx++;
                    var w = math.normalizesafe(toWater);
                    var t = Perp(w);
                    // Distance to the water along w: wet bands lie about a metre above the waterline.
                    float dWater = 3f;
                    for (float st = 0.5f; st <= 3f; st += 0.5f) if (c.W.IsWater(p + w * st)) { dWater = st; break; }
                    uint h = c.H((int)math.floor(x * 2f), (int)math.floor(y * 2f), 141);
                    var g = c.W.GroundAt(p);
                    bool sand = beachy || g == GroundKind.Sand;
                    if (sand)
                    {
                        if (Ctx.Roll(h, 0.6f * aWrack)) c.Emit(p + w * (dWater - 1.2f), Ctx.Yaw(t), DecalPools.WetSand, 2.5f, Ctx.H2(h, 1), ForbidPaved);
                        if (Ctx.Roll(Ctx.H2(h, 2), 0.4f * aWrack)) c.Emit(p - w * (1f + 2f * Ctx.U(Ctx.H2(h, 3))), Ctx.Yaw(t), DecalPools.Beach, 2.3f, Ctx.H2(h, 4), ForbidPaved);
                        if (Ctx.Roll(Ctx.H2(h, 5), 0.08f * aLitter)) c.Emit(p - w * (2f + 3f * Ctx.U(Ctx.H2(h, 6))), Ctx.RandomYaw(Ctx.H2(h, 7)), AssetCategories.DecalLitter, 1.4f, Ctx.H2(h, 8));
                        if (Ctx.Roll(Ctx.H2(h, 9), 0.05f * aWrack)) c.Emit(p - w * (2f + 4f * Ctx.U(Ctx.H2(h, 10))), Ctx.Yaw(w), DecalPools.Footprints, 1.2f, Ctx.H2(h, 11));
                    }
                    else
                    {
                        // A grassy or earthy bank: damp ground, mud where the bank is trodden, moss.
                        if (Ctx.Roll(h, 0.25f * aWrack)) c.Emit(p + w * (dWater - 1.2f), Ctx.Yaw(t), DecalPools.Wet, 1.8f, Ctx.H2(h, 1), ForbidPaved);
                        if (Ctx.Roll(Ctx.H2(h, 2), 0.15f * aMud)) c.Emit(p, Ctx.Yaw(t), DecalPools.Mud, 1.6f, Ctx.H2(h, 3), ForbidPaved);
                        if (Ctx.Roll(Ctx.H2(h, 4), 0.12f * aWrack)) c.Emit(p - w * 1.2f, Ctx.RandomYaw(Ctx.H2(h, 5)), DecalPools.Moss, 1.4f, Ctx.H2(h, 6), ForbidPaved);
                    }
                }
        }

        // ───────────────────────── Works: tracks, gates, machinery, heaps ─────────────────────────

        private static readonly string[] MachineWords = { "machine", "machinery", "vehicle", "truck", "forklift", "crane", "generator", "excavator", "tractor", "wreck", "car" };
        private static readonly string[] HeapWords = { "pile", "heap", "material", "landfill", "scrap", "junk", "bale", "spoil", "garbage", "skip" };

        private static void Works(Ctx c)
        {
            float aTyre = c.A(DressFamily.Works, Kind.Tyre), aMud = c.A(DressFamily.Works, Kind.Mud), aOil = c.A(DressFamily.Works, Kind.Oil),
                aPuddle = c.A(DressFamily.Works, Kind.Puddle), aStain = c.A(DressFamily.Works, Kind.Stain);
            for (int ni = 0; ni < c.L.Nets.Count; ni++)
            {
                var n = c.L.Nets[ni];
                if (n == null || n.Role == NetRole.Fence) continue;
                bool drive = n.Role != NetRole.Pathway && n.Role != NetRole.PathwayWide;
                float wheel = drive ? 1.1f : 0.8f;
                int s = 0;
                foreach (var (p, t) in Along(n, 4.5f, c.H(ni, 151)))
                {
                    uint h = c.H(ni, 152, s++);
                    var nrm = Perp(t);
                    // Both wheel lines.
                    for (int side = -1; side <= 1; side += 2)
                        if (Ctx.Roll(Ctx.H2(h, side + 3), (drive ? 0.32f : 0.15f) * aTyre))
                            c.Emit(p + nrm * side * wheel, Ctx.Yaw(t), DecalPools.Tyre, 2.2f, Ctx.H2(h, side + 6));
                    if (Ctx.Roll(Ctx.H2(h, 10), 0.13f * aMud)) c.Emit(p + nrm * (Ctx.U(Ctx.H2(h, 11)) - 0.5f) * 1.5f, Ctx.Yaw(t), DecalPools.Mud, 1.8f, Ctx.H2(h, 12));
                    if (Ctx.Roll(Ctx.H2(h, 13), 0.07f * aPuddle)) c.Emit(p + nrm * wheel * (Ctx.U(Ctx.H2(h, 14)) < 0.5f ? -1f : 1f), Ctx.Yaw(t), AssetCategories.DecalPuddles, 1.6f, Ctx.H2(h, 15));
                    if (drive && Ctx.Roll(Ctx.H2(h, 16), 0.04f * aOil)) c.Emit(p + Jitter(Ctx.H2(h, 17), 1f), Ctx.RandomYaw(Ctx.H2(h, 18)), DecalPools.Oil, 1.4f, Ctx.H2(h, 19));
                }
                // Gates: mud carried in and out, tyre marks fanning from the road.
                for (int end = 0; end < 2; end++)
                {
                    bool attached = end == 0 ? n.StartAttach >= 0 : n.EndAttach >= 0;
                    var e = end == 0 ? n.A.xz : n.D.xz;
                    if (!attached && c.W.Root.BoundaryDistance(e, 4f) > 3f) continue;
                    var inward = math.normalizesafe(end == 0 ? n.D.xz - n.A.xz : n.A.xz - n.D.xz);
                    uint h = c.H(ni, 160 + end);
                    for (int k = 0; k < 3; k++)
                        if (Ctx.Roll(Ctx.H2(h, k), 0.8f * aMud))
                            c.Emit(e + inward * (2.5f + 3.5f * k) + Perp(inward) * (Ctx.U(Ctx.H2(h, 10 + k)) - 0.5f) * 2f,
                                Ctx.Yaw(inward), k == 0 ? DecalPools.Mud : DecalPools.Tyre, 3f - k * 0.2f, Ctx.H2(h, 20 + k));
                }
            }
            // Haul roads and yard tracks drawn as ground surfaces or terrain paint (long and narrow): wheel lines along them.
            var tracks = new List<float2[]>();
            foreach (var s in c.L.Surfaces)
                if (s?.Ring != null && s.Ring.Length >= 3 && DressingWorld.KindOf(s) != GroundKind.Water) tracks.Add(Geometry.Poly.To2D(s.Ring));
            foreach (var pt in c.L.Paint)
                if (pt?.Ring != null && pt.Ring.Length >= 3 && pt.Layer != TerrainMaterials.Erase && pt.Shape == null && !pt.Erase) tracks.Add(Geometry.Poly.To2D(pt.Ring));
            for (int ti = 0; ti < tracks.Count; ti++)
            {
                var reg = LayerRegion.From(tracks[ti]);
                if (reg == null) continue;
                reg.PrincipalAxes(out var centre, out var major, out float majorSpan, out float minorSpan);
                if (!(minorSpan <= 12f && majorSpan >= 12f && majorSpan >= minorSpan * 2.5f)) continue;
                var nrm = Perp(major);
                int k = 0;
                for (float t = -majorSpan * 0.5f + 2f; t < majorSpan * 0.5f - 2f; t += 4.5f, k++)
                {
                    var p = centre + major * t;
                    if (!reg.Contains(p)) continue;
                    uint h = c.H(ti, 165, k);
                    for (int side = -1; side <= 1; side += 2)
                        if (Ctx.Roll(Ctx.H2(h, side + 3), 0.32f * aTyre))
                            c.Emit(p + nrm * side * 1.1f, Ctx.Yaw(major), DecalPools.Tyre, 2.2f, Ctx.H2(h, side + 6));
                    if (Ctx.Roll(Ctx.H2(h, 10), 0.12f * aMud)) c.Emit(p, Ctx.Yaw(major), DecalPools.Mud, 1.8f, Ctx.H2(h, 11));
                    if (Ctx.Roll(Ctx.H2(h, 12), 0.08f * aPuddle)) c.Emit(p + nrm * 1.1f * (Ctx.U(Ctx.H2(h, 13)) < 0.5f ? -1f : 1f), Ctx.Yaw(major), AssetCategories.DecalPuddles, 1.6f, Ctx.H2(h, 14));
                }
            }
            for (int i = 0; i < c.L.Objects.Count; i++)
            {
                var o = c.L.Objects[i];
                if (o == null || o.Role == ObjectRole.Decal) continue;
                string cat = Cat(o).ToLowerInvariant(), id = (o.Asset.Id ?? "").ToLowerInvariant();
                var p = o.Position.xz;
                uint h = c.H(i, 171);
                if (AssetHeuristics.ContainsAny(cat, MachineWords) || AssetHeuristics.ContainsAny(id, MachineWords))
                {
                    if (Ctx.Roll(h, 0.55f * aOil)) c.Emit(p + Jitter(Ctx.H2(h, 1), 1.2f), Ctx.RandomYaw(Ctx.H2(h, 2)), DecalPools.Oil, 2.6f, Ctx.H2(h, 3));
                    if (Ctx.Roll(Ctx.H2(h, 4), 0.3f * aStain)) c.Emit(p + Jitter(Ctx.H2(h, 5), 2f), Ctx.RandomYaw(Ctx.H2(h, 6)), DecalPools.Stain, 1.9f, Ctx.H2(h, 7));
                }
                else if (AssetHeuristics.ContainsAny(cat, HeapWords) || AssetHeuristics.ContainsAny(id, HeapWords))
                {
                    float r = Footprint(o, c.W.Site, 2.5f);
                    int n = Ctx.Count(h, 1.2f * aMud);
                    for (int k = 0; k < n; k++)
                    {
                        float a = Ctx.U(Ctx.H2(h, 10 + k)) * math.PI * 2f;
                        var d = new float2(math.cos(a), math.sin(a));
                        c.Emit(p + d * (r + 0.8f), Ctx.Yaw(Perp(d)), k % 2 == 0 ? DecalPools.Mud : DecalPools.Rocks, 2.0f, Ctx.H2(h, 20 + k));
                    }
                }
            }
            if (!c.Main) return;
            // The yard itself: dust and grime between everything.
            float amt = c.S.Master * c.S.Of(DressFamily.Works);
            Scatter(c, c.Region, amt, 181, new[]
            {
                (DecalPools.Mud, 0.16f * Dressing.Weight(c.S.Style, Kind.Mud), 0.9f, 0),
                (DecalPools.Stain, 0.10f * Dressing.Weight(c.S.Style, Kind.Stain), 0.9f, 0),
                (AssetCategories.DecalPuddles, 0.04f * Dressing.Weight(c.S.Style, Kind.Puddle), 0.8f, 0),
                (DecalPools.Rocks, 0.05f * Dressing.Weight(c.S.Style, Kind.Rocks), 0.8f, 0),
                (AssetCategories.DecalLitter, 0.04f * Dressing.Weight(c.S.Style, Kind.Litter), 0.7f, 0),
            }, 20f);
        }

        // ───────────────────────── Ruins: decay on slabs, rubble, walls ─────────────────────────

        private static void Ruins(Ctx c)
        {
            var S = c.S;
            float amt = S.Master * S.Of(DressFamily.Ruins);
            for (int si = 0; si < c.L.Surfaces.Count; si++)
            {
                var s = c.L.Surfaces[si];
                if (s?.Ring == null || s.Ring.Length < 3 || DressingWorld.KindOf(s) != GroundKind.Paved) continue;
                var reg = LayerRegion.From(s.Ring);
                if (reg == null || reg.Area < 12f) continue;
                Scatter(c, reg, amt, si * 13 + 201, new[]
                {
                    (AssetCategories.DecalCracks, 0.9f * Dressing.Weight(S.Style, Kind.Crack), 1.9f, 0),
                    (DecalPools.Patch, 0.2f * Dressing.Weight(S.Style, Kind.Crack), 1.5f, 0),
                    (DecalPools.Moss, 0.45f * Dressing.Weight(S.Style, Kind.Moss), 1.7f, 0),
                    (DecalPools.Rocks, 0.3f * Dressing.Weight(S.Style, Kind.Rocks), 1.4f, 0),
                    (DecalPools.Stain, 0.35f * Dressing.Weight(S.Style, Kind.Stain), 1.3f, 0),
                    (DecalPools.Glass, 0.2f * Dressing.Weight(S.Style, Kind.Glass), 1.2f, 0),
                    (AssetCategories.DecalLitter, 0.22f * Dressing.Weight(S.Style, Kind.Litter), 1.1f, 0),
                    (AssetCategories.DecalGraffiti, 0.05f * Dressing.Weight(S.Style, Kind.Graffiti), 1.0f, 0),
                }, 10f);
            }
            float aRocks = c.A(DressFamily.Ruins, Kind.Rocks), aGlass = c.A(DressFamily.Ruins, Kind.Glass), aMoss = c.A(DressFamily.Ruins, Kind.Moss),
                aGraf = c.A(DressFamily.Ruins, Kind.Graffiti);
            for (int i = 0; i < c.L.Objects.Count; i++)
            {
                var o = c.L.Objects[i];
                if (o == null || o.Role == ObjectRole.Decal) continue;
                string cat = Cat(o);
                var p = o.Position.xz;
                uint h = c.H(i, 211);
                if (cat.Contains("rubble") || cat.Contains("junk") || cat.Contains("pile") || cat.Contains("slab"))
                {
                    float r = Footprint(o, c.W.Site, 2f);
                    int n = Ctx.Count(h, 2f * aRocks);
                    for (int k = 0; k < n; k++)
                    {
                        float a = Ctx.U(Ctx.H2(h, 10 + k)) * math.PI * 2f;
                        c.Emit(p + new float2(math.cos(a), math.sin(a)) * (r * 0.7f + 0.6f + Ctx.U(Ctx.H2(h, 20 + k))), Ctx.RandomYaw(Ctx.H2(h, 30 + k)), DecalPools.Rocks, 2.2f, Ctx.H2(h, 40 + k));
                    }
                    if (Ctx.Roll(Ctx.H2(h, 50), 0.3f * aGlass)) c.Emit(p + Jitter(Ctx.H2(h, 51), r + 1.5f), Ctx.RandomYaw(Ctx.H2(h, 52)), DecalPools.Glass, 1.8f, Ctx.H2(h, 53));
                }
                else if (cat.Contains("ruins.wall") || o.Role == ObjectRole.FencePiece || cat == AssetCategories.Fence)
                {
                    // Moss and weeds at the foot of a wall, graffiti dust and paint below it.
                    if (Ctx.Roll(h, 0.35f * aMoss)) c.Emit(p + Perp(Forward(o.Yaw)) * 0.6f, o.Yaw, DecalPools.Moss, 1.9f, Ctx.H2(h, 1));
                    if (Ctx.Roll(Ctx.H2(h, 2), 0.1f * aGraf)) c.Emit(p + Forward(o.Yaw) * 1.6f, o.Yaw, AssetCategories.DecalGraffiti, 1.5f, Ctx.H2(h, 3));
                }
                else if (cat.Contains("weeds"))
                {
                    if (Ctx.Roll(h, 0.5f * aMoss)) c.Emit(p + Jitter(Ctx.H2(h, 1), 0.6f), Ctx.RandomYaw(Ctx.H2(h, 2)), DecalPools.Moss, 1.8f, Ctx.H2(h, 3));
                }
            }
        }

        // ───────────────────────── Garden: mulch and hedges ─────────────────────────

        private static void Garden(Ctx c)
        {
            float aMulch = c.A(DressFamily.Garden, Kind.Leaves), aWear = c.A(DressFamily.Garden, Kind.Wear), aStain = c.A(DressFamily.Garden, Kind.Stain);
            for (int i = 0; i < c.L.Objects.Count; i++)
            {
                var o = c.L.Objects[i];
                if (o == null) continue;
                string cat = Cat(o);
                var p = o.Position.xz;
                uint h = c.H(i, 221);
                if (cat.StartsWith(AssetCategories.Hedge, StringComparison.Ordinal))
                {
                    var side = Perp(Forward(o.Yaw)) * (Ctx.U(Ctx.H2(h, 1)) < 0.5f ? -1f : 1f);
                    if (Ctx.Roll(h, 0.3f * aMulch)) c.Emit(p + side * 0.9f, o.Yaw, DecalPools.LeavesEdge, 1.6f, Ctx.H2(h, 2));
                }
                else if (o.Role == ObjectRole.Bush || cat.StartsWith(AssetCategories.Flowerbed, StringComparison.Ordinal))
                {
                    int n = Ctx.Count(h, (cat.StartsWith(AssetCategories.Flowerbed, StringComparison.Ordinal) ? 1.2f : 0.45f) * aMulch);
                    for (int k = 0; k < n; k++)
                        c.Emit(p + Jitter(Ctx.H2(h, 10 + k), 1.2f), Ctx.RandomYaw(Ctx.H2(h, 20 + k)), DecalPools.Mulch, 2.0f, Ctx.H2(h, 30 + k));
                }
                else if (o.Role == ObjectRole.Planter)
                {
                    if (Ctx.Roll(h, 0.25f * aStain)) c.Emit(p + Jitter(Ctx.H2(h, 1), 0.5f), Ctx.RandomYaw(Ctx.H2(h, 2)), DecalPools.Stain, 1.4f, Ctx.H2(h, 3));
                }
                else if (cat.StartsWith(AssetCategories.GardenCrop, StringComparison.Ordinal) || cat.StartsWith("farm.crop", StringComparison.Ordinal))
                {
                    if (Ctx.Roll(h, 0.3f * aWear)) c.Emit(p + Jitter(Ctx.H2(h, 1), 1f), o.Yaw, DecalPools.Mud, 1.5f, Ctx.H2(h, 2), ForbidPaved);
                }
            }
        }
    }
}
