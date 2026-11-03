// Shoreline treatments of the shore kit. Each works on an along-shore interval [from, to] (arc
// length of the Shoreline chain) and lays things out by distance from the water: positive depths
// are on land, negative ones over the water inside the outline (only when the player drew over it).
//
//   Walk        the continuous walk parallel to the water (promenade / quay apron / beach back path)
//   Promenade   seawall promenade: paved band, railing at the edge (gaps for piers and wharves),
//               benches facing the water, lamps
//   Wharf       the quay: the water next to the shore is FILLED (permanent Raise terraform) up to a
//               quay level above the water, paved and made walkable; bollards along the new edge,
//               boats moored beyond it, cargo clusters and lamps on the quay
//   Pier        a deck out over the water (level, not following the bed), optional T-head, lamps
//               along it, benches at the head, boats moored alongside
//   Boardwalk   a deck just off the shore over the water (or along the bank when the water lies
//               outside the area), with stubs back to land
//   Beach       sand band (surface + terrain paint), umbrella/lounger sets, boats pulled up
//   Reeds       clumps of reeds along the waterline, in the shallows and on the bank
// Every placement goes through one Occupancy on the whole outline (water included), so Reserved is
// respected and treatments keep clear of each other.

using System;
using System.Collections.Generic;
using Placemaker.Core.Geometry;
using Placemaker.Core.Layers.Park;
using Placemaker.Core.Layers.Types.Beach;
using Unity.Mathematics;

namespace Placemaker.Core.Layers.Common.Shore
{
    public sealed class ShoreWork
    {
        public LayerContext Ctx;
        public Shoreline Shore;
        /// <summary>Occupancy on the whole outline (land and the water inside it).</summary>
        public Occupancy Occ;
        public AssetRef PathAsset, Bench, Lamp, Paving, Railing, Boat;
        public NetRole WalkRole = NetRole.Pathway;
        /// <summary>Depth of the walk from the water (0 = no walk yet).</summary>
        public float WalkDepth;
        /// <summary>Along-shore intervals (from, to) kept open at the edge (piers, wharves).</summary>
        public readonly List<float2> Gaps = new List<float2>();
        public readonly List<(float2 a, float2 b)> Walk = new List<(float2 a, float2 b)>();
        public readonly List<float2[]> Fill = new List<float2[]>();
        public int Piers, Bollards, Boats, Benches, Lamps, Railings, Reeds, Props, Wharves, Umbrellas, Loungers, Links;
        public float PierLength, WalkLength, FillArea, QuayHeight = float.NaN;

        public ShoreWork(LayerContext ctx, Shoreline shore)
        {
            Ctx = ctx; Shore = shore;
            Occ = new Occupancy(shore.Outline, ctx.Input.Reserved, ctx.Site);
        }

        public bool Inside => Shore.Mode == ShoreMode.Inside;
        public float Water => Shore.WaterLevel;
        public bool HasWaterLevel => math.isfinite(Shore.WaterLevel);
        public float Hw => Composer.DefaultHalfWidth(WalkRole);

        public bool InGap(float s, float pad = 0f)
        {
            foreach (var g in Gaps) if (s >= g.x - pad && s <= g.y + pad) return true;
            return false;
        }

        public bool InFill(float2 p, float margin)
        {
            foreach (var f in Fill)
                if (Poly.Contains(f, p) && Poly.DistanceToBoundary(f, p) >= margin) return true;
            return false;
        }

        /// <summary>A disc of radius r lies on dry land inside the area.</summary>
        public bool OnLand(float2 p, float r) => Shore.Land.ContainsDisc(p, r) && !Shore.IsWet(p);

        /// <summary>A disc of radius r lies over water inside the area.</summary>
        public bool OnWater(float2 p, float r) => Shore.Outline.ContainsDisc(p, r) && Shore.IsWet(p) && !Shore.Land.Contains(p);
    }

    public static class ShoreTreatments
    {
        private static readonly List<Gate> NoGates = new List<Gate>();

        // ───────────── lines parallel to the water ─────────────

        /// <summary>
        /// Polylines at depth d from the water over [from, to]: on land for d &gt; 0, over the water for
        /// d &lt; 0. Pieces break where the line leaves the land/water or meets something taken.
        /// </summary>
        public static List<float2[]> Line(ShoreWork w, float d, float from, float to, float margin, float minLength, bool checkOcc = true)
        {
            var shore = w.Shore;
            var result = new List<float2[]>();
            var run = new List<float2>();
            void Flush()
            {
                if (run.Count >= 2)
                {
                    // s30: smoothed by arc length (σ 8 m): a walk, boardwalk or railing parallel to the shore never zig-zags with it.
                    var line = run.Count >= 3 ? Types.Beach.ShoreWalk.SmoothLine(run, 8f, 2, 3f) : run.ToArray();
                    if (Shoreline.LengthOf(line) >= minLength) result.Add(line);
                }
                run.Clear();
            }
            const float step = 3f;
            from = math.max(0f, from); to = math.min(shore.Length, to);
            int steps = math.max(1, (int)math.ceil((to - from) / step));
            float2 prev = default; bool havePrev = false;
            for (int i = 0; i <= steps; i++)
            {
                var c = shore.At(from + (to - from) * i / steps, out var inw, out _);
                var q = c + inw * d;
                bool ok = d > 0f
                    ? shore.Land.ContainsDisc(q, margin) && shore.Depth(q, d + 2f) >= d - 1.2f
                    : w.OnWater(q, margin) && shore.Depth(q, -d + 2f) >= -d - 1.2f;
                if (ok && checkOcc && (w.Occ.Discs.Overlaps(q, margin * 0.8f, 0f) || w.Occ.HitsPolygon(q, margin * 0.8f))) ok = false;
                if (ok && havePrev && (math.distance(prev, q) > step * 3f + 2f || (d > 0f && shore.Land.CrossesBoundary(prev, q, false)))) Flush();
                if (ok) { run.Add(q); prev = q; havePrev = true; }
                else { Flush(); havePrev = false; }
            }
            Flush();
            return result;
        }

        /// <summary>The walk parallel to the water at depth d over [from, to]. Returns its length.</summary>
        public static float Walk(ShoreWork w, float d, float from, float to)
        {
            var ctx = w.Ctx;
            float total = 0f;
            foreach (var line in Line(w, d, from, to, w.Hw + 0.4f, 12f))
            {
                var curves = Shoreline.Curves(w.Shore.Land, line, w.WalkRole);
                if (curves.Count == 0) continue;
                ParkPaths.Index(curves, w.Occ.Corridors, 100);
                ParkPaths.Index(curves, w.Occ.Paths, 100);
                total += ParkPaths.Emit(ctx, curves, NoGates, w.PathAsset);
                foreach (var c in curves)
                    for (int i = 1; i < c.Samples.Length; i++) w.Walk.Add((c.Samples[i - 1], c.Samples[i]));
            }
            if (total > 0f) w.WalkDepth = d;
            w.WalkLength += total;
            return total;
        }

        // ───────────── seawall promenade ─────────────

        public static void Promenade(ShoreWork w, float from, float to, float width, bool paved, bool railings, float benchSpacing, float lampSpacing)
        {
            var ctx = w.Ctx; var shore = w.Shore; var occ = w.Occ;
            width = math.min(width, shore.MaxDepth - 1f);
            if (width < 3f || to - from < 6f) return;
            if (paved && AssetAvailability.Require(ctx.Plan, AssetCategories.SurfacePaving, new[] { w.Paving.Id }, Keys.Category(AssetCategories.SurfacePaving)))
                foreach (var ring in shore.Band(0f, width, from, to)) ctx.Surface(ring, SurfaceRole.Paving, w.Paving, AssetCategories.SurfacePaving);
            if (railings) Railing(w, from, to, 0.7f);
            float dw = w.WalkDepth > 0f ? w.WalkDepth : width * 0.6f;
            float walkEdge = dw - w.Hw;
            bool between = walkEdge - 1.2f >= 2.6f + 0.4f;   // bench (r 1.3) between railing and walk
            if (benchSpacing > 0f)
                for (float s = from + benchSpacing * 0.5f; s < to; s += benchSpacing)
                {
                    if (w.InGap(s, 2f)) continue;
                    var c = shore.At(s, out var inw, out _);
                    float bd = between ? (1.0f + walkEdge) * 0.5f : dw + w.Hw + 1.7f;
                    var p = c + inw * bd;
                    if (!w.OnLand(p, 1.3f)) continue;
                    if (LeisureKit.Put(ctx, occ, p, ShoreKit.Yaw(-inw), ObjectRole.Bench, w.Bench, null, 1.3f, Hashing.Mix(ctx.Seed, 0xBE00u + (uint)w.Benches), 0.3f, 0.2f))
                        w.Benches++;
                }
            if (lampSpacing > 0f)
                for (float s = from + lampSpacing * 0.25f; s < to; s += lampSpacing)
                {
                    var c = shore.At(s, out var inw, out _);
                    float ld = walkEdge - 0.8f >= 1.6f ? walkEdge - 0.8f : dw + w.Hw + 0.8f;
                    var p = c + inw * ld;
                    if (!w.OnLand(p, 0.5f)) continue;
                    if (LeisureKit.Put(ctx, occ, p, ShoreKit.Yaw(-inw), ObjectRole.Lamp, w.Lamp, null, 0.5f, Hashing.Mix(ctx.Seed, 0x1A00u + (uint)w.Lamps), 0.3f, 0.2f))
                        w.Lamps++;
                }
        }

        /// <summary>Railing (fence nets) at depth d along [from, to], open at the gaps.</summary>
        public static void Railing(ShoreWork w, float from, float to, float d)
        {
            var ctx = w.Ctx; var shore = w.Shore; var occ = w.Occ;
            var run = new List<float2>();
            void Flush()
            {
                if (run.Count >= 2)
                {
                    var simple = Poly.SimplifyPolyline(run, 0.25f);
                    for (int i = 1; i < simple.Length; i++)
                    {
                        var a = simple[i - 1]; var b = simple[i];
                        int pieces = math.max(1, (int)math.ceil(math.distance(a, b) / Fences.MaxCourse));
                        for (int k = 0; k < pieces; k++)
                        {
                            var p = math.lerp(a, b, (float)k / pieces); var q = math.lerp(a, b, (float)(k + 1) / pieces);
                            if (math.distance(p, q) < 1f) continue;
                            ctx.StraightNet(p, q, NetRole.Fence, w.Railing);
                            occ.TakeCorridor(p, q, 0.3f, 5);
                            w.Railings++;
                        }
                    }
                }
                run.Clear();
            }
            for (float s = from + 0.5f; s <= to - 0.5f; s += 2f)
            {
                var c = shore.At(s, out var inw, out _);
                var q = c + inw * d;
                bool ok = !w.InGap(s, 0.5f) && shore.Land.ContainsDisc(q, 0.35f) && shore.Depth(q, 2f) > d * 0.6f
                    && !occ.Corridors.Overlaps(q, 0.3f, 0.1f) && !occ.Discs.Overlaps(q, 0.3f, 0.1f);
                if (ok && run.Count > 0 && shore.Land.CrossesBoundary(run[run.Count - 1], q, false)) Flush();
                if (ok) run.Add(q); else Flush();
            }
            Flush();
        }

        // ───────────── wharf / quay ─────────────

        /// <summary>
        /// Fills the water next to the shore over [from, to] out to <paramref name="depth"/> into a quay
        /// (only where the area covers water). Returns false when nothing could be filled.
        /// </summary>
        public static bool Wharf(ShoreWork w, float from, float to, float depth, float bollardSpacing, int boats, bool cargo, float lampSpacing, float keepClear = float.NaN)
        {
            var ctx = w.Ctx; var shore = w.Shore;
            if (!w.Inside || !w.HasWaterLevel || to - from < 10f || depth < 4f) return false;
            const float step = 3f;
            int steps = math.max(2, (int)math.ceil((to - from) / step));
            var ring = new List<float2>(steps * 2 + 2);
            var outer = new List<float2>(steps + 1);
            float landH = 0f; int landN = 0;
            for (int i = 0; i <= steps; i++)
            {
                var c = shore.At(from + (to - from) * i / steps, out var inw, out _);
                ring.Add(c + inw * 1.5f);
                outer.Add(c - inw * depth);
                var l = c + inw * 3f;
                if (shore.Land.Contains(l)) { landH += ctx.H(l); landN++; }
            }
            for (int i = outer.Count - 1; i >= 0; i--) ring.Add(outer[i]);
            // Land lies left of the chain, so shore-then-water-back runs clockwise: flip it for the Positive fill rule.
            ring.Reverse();
            var pieces = new List<float2[]>();
            try
            {
                foreach (var r in Poly.Boolean(new[] { (IReadOnlyList<float2>)ring }, new[] { (IReadOnlyList<float2>)shore.Outline.Ring }, BoolOp.Intersection, FillRule.Positive))
                    foreach (var piece in Poly.CutHoles(r, 0.5f))
                    {
                        var clean = Poly.Clean(piece, 0.3f);
                        if (clean.Length >= 3 && Poly.Area(clean) > 40f && Poly.MeanWidth(clean) > 2f) pieces.Add(clean);
                    }
            }
            catch (Exception) { pieces.Clear(); }
            if (pieces.Count == 0) return false;

            float quay = math.max(w.Water + 1.0f, landN > 0 ? landH / landN : w.Water + 1.0f);
            quay = math.min(quay, w.Water + 3f);
            w.QuayHeight = quay;
            foreach (var p in pieces)
            {
                ctx.Plan.Terraform.Add(new PlannedTerraform { Ring = Poly.To3D(p, _ => quay), Kind = TerraformKind.Raise, TargetHeight = quay, Permanent = true });
                w.Fill.Add(p);
                w.FillArea += Poly.Area(p);
                // Walkable quay: paving + a pedestrian area (the fill is the plaza of the harbour).
                if (AssetAvailability.Has(AssetCategories.SurfacePaving)) ctx.Surface(p, SurfaceRole.Paving, w.Paving, AssetCategories.SurfacePaving);
                ctx.NavigationArea(p, AreaRole.PedestrianArea, new AssetRef("", "Pedestrian Area", "Walking Area"));
            }
            ShoreTerrain.Warn(ctx);
            w.Gaps.Add(new float2(from, to));
            w.Wharves++;
            if (math.isfinite(keepClear))
            {
                // The pier's way across the quay stays free of bollards, lamps and cargo.
                var kc = shore.At(keepClear, out var kin, out _);
                w.Occ.TakeCorridor(kc + kin * 0.3f, kc - kin * (depth + 1f), 2.2f, 9);
            }

            // Quay edge: bollards just inside, boats moored beyond.
            string bollard = LeisureKit.Category(ctx, "", AssetCategories.WaterfrontMooring, AssetCategories.Bollard);
            string boat = boats > 0 ? LeisureKit.Category(ctx, w.Boat.Id, AssetCategories.ShoreBoat, AssetCategories.BeachBoat) : null;
            float boatEvery = boats > 0 ? math.max(12f, (to - from) / (boats + 0.5f)) : float.MaxValue;
            float nextBoat = from + boatEvery * 0.5f;
            int placedBoats = 0;
            for (float s = from + 2f; s <= to - 2f; s += 1f)
            {
                var c = shore.At(s, out var inw, out var tan);
                var e = c - inw * depth;
                // Pull the edge in where the outline cut the fill short.
                float pull = 0f;
                while (pull < depth - 2f && !w.InFill(e + inw * (0.9f + pull), 0.45f)) pull += 1f;
                if (!w.InFill(e + inw * (0.9f + pull), 0.45f)) continue;
                var edge = e + inw * pull;
                if (bollard != null && bollardSpacing > 0f && (s - from) % bollardSpacing < 1f)
                    if (ShoreKit.PutAt(ctx, w.Occ, edge + inw * 0.9f, quay, ShoreKit.Yaw(-inw), ObjectRole.Furniture, AssetMix.One(""), bollard, 0.5f,
                        Hashing.Mix(ctx.Seed, 0xB011u + (uint)w.Bollards), 0.2f, 0.1f, 6))
                        w.Bollards++;
                if (boat != null && placedBoats < boats && s >= nextBoat && !(math.abs(s - keepClear) < 10f))
                {
                    var bp = edge - inw * 4f;
                    if (w.OnWater(bp, 3f) && ShoreKit.PutAt(ctx, w.Occ, bp, w.Water, ShoreKit.Yaw(tan), ObjectRole.Furniture, w.Boat, boat, 3f,
                        Hashing.Mix(ctx.Seed, 0xB0A7u + (uint)placedBoats), 0.5f, 0.5f, 7))
                    {
                        placedBoats++;
                        nextBoat = s + boatEvery;
                    }
                }
            }
            w.Boats += placedBoats;

            // Lamps along the quay, cargo clusters further out.
            if (lampSpacing > 0f)
                for (float s = from + lampSpacing * 0.5f; s < to; s += lampSpacing)
                {
                    var c = shore.At(s, out var inw, out _);
                    var p = c - inw * math.min(depth * 0.3f, depth - 2f);
                    if (!w.InFill(p, 0.6f)) continue;
                    if (ShoreKit.PutAt(ctx, w.Occ, p, quay, 0f, ObjectRole.Lamp, w.Lamp, null, 0.5f, Hashing.Mix(ctx.Seed, 0x1A80u + (uint)w.Lamps), 0.3f, 0.2f, 2))
                        w.Lamps++;
                }
            if (cargo && depth >= 8f)
            {
                string cat = LeisureKit.Category(ctx, "", AssetCategories.ShoreCargo, AssetCategories.LogisticsCrate, AssetCategories.WaterfrontNautical);
                if (cat != null)
                {
                    int clusters = math.clamp((int)((to - from) / 30f), 1, 4);
                    for (int k = 0; k < clusters; k++)
                    {
                        float s = from + (to - from) * (k + 0.5f) / clusters + 6f;
                        var c = shore.At(s, out var inw, out var tan);
                        var centre = c - inw * depth * 0.68f;
                        for (int j = 0; j < 5; j++)
                        {
                            uint v = Hashing.Mix(ctx.Seed, 0xCA60u + (uint)(k * 8 + j));
                            var p = centre + tan * (Hashing.Unit(v) - 0.5f) * 4f + inw * (Hashing.Unit(Hashing.Mix(v, 1u)) - 0.5f) * 1.6f;
                            if (!w.InFill(p, 0.8f)) continue;
                            if (ShoreKit.PutAt(ctx, w.Occ, p, quay, Hashing.Unit(Hashing.Mix(v, 2u)) * 6.283f, ObjectRole.Furniture, AssetMix.One(""), cat, 0.7f, v, 0.3f, 0.15f, 3))
                                w.Props++;
                        }
                    }
                }
            }
            return true;
        }

        // ───────────── piers ─────────────

        /// <summary>
        /// A pier at arc length s: a level deck out over the water from the shore (or from the quay edge
        /// when a wharf is there), with an optional T-head, lamps, benches at the head and boats alongside.
        /// </summary>
        public static bool Pier(ShoreWork w, float s, float maxLen, bool head, int boats, bool lamps)
        {
            var ctx = w.Ctx; var shore = w.Shore;
            if (!w.Inside) return false;
            var c = shore.At(s, out var inw, out var tan);
            var dir = -inw;
            // Start at the quay edge when the shore here was filled.
            float startOff = 0f;
            while (startOff < 40f && w.InFill(c + dir * (startOff + 1f), 0.2f)) startOff += 1f;
            var p0 = c + dir * startOff;
            float len = 0f; bool wet = false;
            for (float l = 2f; l <= maxLen; l += 2f)
            {
                var q = p0 + dir * l;
                bool isWet = shore.IsWet(q);
                if (!shore.Outline.ContainsDisc(q, 2.2f) || (!isWet && l > 5f)) break;
                if (w.Occ.Discs.Overlaps(q, 2f, 0.2f) || w.Occ.HitsPolygon(q, 2f)) break;
                wet |= isWet;
                len = l;
            }
            if (len < 8f || !wet) return false;
            var end = p0 + dir * len;
            float rootY = startOff > 0f && math.isfinite(w.QuayHeight) ? w.QuayHeight : ctx.H(c);
            float deck = math.max(rootY, (w.HasWaterLevel ? w.Water : ctx.H(c)) + 1.2f);
            float hw = Composer.DefaultHalfWidth(NetRole.Pathway);
            // Land stub from the walk (or straight from the shore) to the pier root.
            if (w.WalkDepth > 0f)
            {
                var walk = c + inw * w.WalkDepth;
                if (shore.Land.Contains(walk))
                {
                    var landEnd = c + inw * 0.3f;
                    if (math.distance(walk, landEnd) > 1f) ShoreKit.Net(ctx, walk, landEnd, ctx.H(walk), ctx.H(landEnd), NetRole.Pathway, w.PathAsset, true);
                    w.Occ.TakeCorridor(walk, landEnd, hw, 100);
                }
            }
            if (startOff > 0f) ShoreKit.Net(ctx, c + inw * 0.3f, p0, ctx.H(c), deck, NetRole.Pathway, w.PathAsset, true);
            ShoreKit.Net(ctx, startOff > 0f ? p0 : c + inw * 0.3f, end, deck, deck, NetRole.Pathway, w.PathAsset, false);
            w.Occ.TakeCorridor(c, end, hw + 0.3f, 9);
            float total = len;
            var heads = new List<float2>();
            if (head)
                foreach (float sgn in new[] { 1f, -1f })
                {
                    var h = end + tan * sgn * 7f;
                    if (!w.OnWater(h, 1.5f) || w.Occ.Discs.Overlaps(h, 1.5f, 0.2f)) continue;
                    ShoreKit.Net(ctx, end, h, deck, deck, NetRole.Pathway, w.PathAsset, false);
                    w.Occ.TakeCorridor(end, h, hw + 0.3f, 9);
                    heads.Add(h);
                    total += 7f;
                }
            if (lamps)
                for (float l = 6f; l < len - 2f; l += 12f)
                {
                    float sgn = ((int)(l / 12f) & 1) == 0 ? 1f : -1f;
                    var p = p0 + dir * l + tan * sgn * (hw + 0.6f);
                    var o = ctx.Object(p, 0f, ObjectRole.Lamp, w.Lamp, Hashing.Mix(ctx.Seed, 0x1AB0u + (uint)w.Lamps));
                    o.Position.y = deck;
                    w.Occ.Take(p, 0.5f, 2);
                    w.Lamps++;
                }
            // Benches at the head face out over the water.
            foreach (var h in heads)
            {
                var p = h + dir * (hw + 1.4f);
                if (!w.OnWater(p, 1.3f)) continue;
                var o = ctx.Object(p, ShoreKit.Yaw(dir), ObjectRole.Bench, w.Bench, Hashing.Mix(ctx.Seed, 0xBE80u + (uint)w.Benches));
                o.Position.y = deck;
                w.Occ.Take(p, 1.3f, 1);
                w.Benches++;
            }
            if (boats > 0 && w.HasWaterLevel)
            {
                string cat = LeisureKit.Category(ctx, w.Boat.Id, AssetCategories.ShoreBoat, AssetCategories.BeachBoat);
                int placed = 0;
                for (int k = 0; cat != null && k < 6 && placed < boats; k++)
                {
                    float l = len * (0.35f + 0.2f * (k / 2));
                    float sgn = (k & 1) == 0 ? 1f : -1f;
                    var bp = p0 + dir * l + tan * sgn * (hw + 3.2f);
                    if (!w.OnWater(bp, 2.5f)) continue;
                    if (ShoreKit.PutAt(ctx, w.Occ, bp, w.Water, ShoreKit.Yaw(dir), ObjectRole.Furniture, w.Boat, cat, 2.5f,
                        Hashing.Mix(ctx.Seed, 0xB0B0u + (uint)(w.Boats + placed)), 0.3f, 0.3f, 7))
                        placed++;
                }
                w.Boats += placed;
            }
            w.Gaps.Add(new float2(s - 2.5f, s + 2.5f));
            w.Piers++;
            w.PierLength += total;
            return true;
        }

        // ───────────── boardwalk ─────────────

        /// <summary>A deck just off the shore over the water (or a bank path when the water is outside), with stubs to land.</summary>
        public static void Boardwalk(ShoreWork w, float from, float to, float offset, float benchSpacing, float lampSpacing)
        {
            var ctx = w.Ctx; var shore = w.Shore;
            float hw = Composer.DefaultHalfWidth(NetRole.Pathway);
            var lines = w.Inside && w.HasWaterLevel ? Line(w, -offset, from, to, hw + 0.3f, 12f) : new List<float2[]>();
            if (lines.Count > 0)
            {
                float deck = w.Water + 0.8f;
                foreach (var line in lines)
                {
                    var simple = Poly.SimplifyPolyline(line, 0.3f);
                    for (int i = 1; i < simple.Length; i++)
                    {
                        ShoreKit.Net(ctx, simple[i - 1], simple[i], deck, deck, NetRole.Pathway, w.PathAsset, false);
                        w.Occ.TakeCorridor(simple[i - 1], simple[i], hw, 100);
                        w.Occ.Paths.Add(simple[i - 1], simple[i], hw, 100);
                        w.Walk.Add((simple[i - 1], simple[i]));
                        w.WalkLength += math.distance(simple[i - 1], simple[i]);
                    }
                    // Stubs back to land at both ends and every ~60 m.
                    float l = Shoreline.LengthOf(line);
                    int stubs = math.max(2, (int)math.round(l / 60f) + 1);
                    for (int k = 0; k < stubs; k++)
                    {
                        var p = Shoreline.PointOn(line, l * k / (stubs - 1), out _);
                        var c = shore.Nearest(p, out var inw, out _);
                        var landEnd = c + inw * 4f;
                        if (!shore.Land.ContainsDisc(landEnd, hw)) continue;
                        ShoreKit.Net(ctx, p, landEnd, deck, ctx.H(landEnd), NetRole.Pathway, w.PathAsset, false);
                        w.Occ.TakeCorridor(p, landEnd, hw, 100);
                        w.Walk.Add((p, landEnd));
                    }
                }
                w.WalkDepth = 4f;
            }
            else
            {
                // The water lies outside the area: the boardwalk runs along the bank.
                var role = w.WalkRole; w.WalkRole = NetRole.Pathway;
                Walk(w, hw + 1.2f, from, to);
                w.WalkRole = role;
            }
            if (benchSpacing > 0f)
                for (float s = from + benchSpacing * 0.5f; s < to; s += benchSpacing)
                {
                    var c = shore.At(s, out var inw, out _);
                    var p = c + inw * (lines.Count > 0 ? 3f : hw * 2f + 2.9f);
                    if (!w.OnLand(p, 1.3f)) continue;
                    if (LeisureKit.Put(ctx, w.Occ, p, ShoreKit.Yaw(-inw), ObjectRole.Bench, w.Bench, null, 1.3f, Hashing.Mix(ctx.Seed, 0xBEC0u + (uint)w.Benches), 0.3f, 0.2f))
                        w.Benches++;
                }
            if (lampSpacing > 0f)
                for (float s = from + lampSpacing * 0.5f; s < to; s += lampSpacing)
                {
                    var c = shore.At(s, out var inw, out _);
                    var p = c + inw * (lines.Count > 0 ? 1.2f : hw * 2f + 1.4f);
                    if (!w.OnLand(p, 0.5f)) continue;
                    if (LeisureKit.Put(ctx, w.Occ, p, 0f, ObjectRole.Lamp, w.Lamp, null, 0.5f, Hashing.Mix(ctx.Seed, 0x1AC0u + (uint)w.Lamps), 0.3f, 0.2f))
                        w.Lamps++;
                }
        }

        // ───────────── beach edge ─────────────

        public static void Beach(ShoreWork w, float from, float to, float width, float setSpacing, int boats)
        {
            var ctx = w.Ctx; var shore = w.Shore;
            width = math.min(width, shore.MaxDepth - 1f);
            if (width < 4f) return;
            var band = shore.Band(0f, width, from, to);
            string sand = LeisureKit.Category(ctx, "", AssetCategories.SurfaceSand, AssetCategories.SurfaceNatural);
            foreach (var ring in band)
            {
                if (sand != null) ctx.Surface(ring, SurfaceRole.Ground, AssetMix.One(""), sand);
                TerrainPaint.Patch(ctx, ring, 2, 0.9f, 2f);
            }
            string umbrella = LeisureKit.Category(ctx, "", AssetCategories.BeachUmbrella, AssetCategories.CafeSeating);
            string lounger = LeisureKit.Category(ctx, "", AssetCategories.BeachLounger, AssetCategories.Bench);
            if (setSpacing > 0f && (umbrella != null || lounger != null))
            {
                int row = 0;
                for (float d = 4f; d <= width - 2.5f; d += 6f, row++)
                    for (float s = from + setSpacing * (0.5f + 0.5f * (row & 1)); s < to - 2f; s += setSpacing)
                    {
                        var c = shore.At(s, out var inw, out var tan);
                        var p = c + inw * d;
                        if (!w.OnLand(p, 1.5f) || !w.Occ.Free(p, 1.5f, 0.3f, 0.3f)) continue;
                        uint v = Hashing.Mix(ctx.Seed, 0x5A0Du + (uint)(row * 977 + (int)s));
                        if (umbrella != null && ShoreKit.PutAt(ctx, w.Occ, p, float.NaN, Hashing.Unit(v) * 6.283f, ObjectRole.Furniture, AssetMix.One(""), umbrella, 0.6f, v, 0.3f, 0.2f, 3))
                            w.Umbrellas++;
                        if (lounger == null) continue;
                        foreach (float side in new[] { -1f, 1f })
                        {
                            var q = p + tan * side * 1.3f - inw * 0.4f;
                            var role = lounger == AssetCategories.Bench ? ObjectRole.Bench : ObjectRole.Furniture;
                            float r = role == ObjectRole.Bench ? 1.3f : 0.6f;
                            if (role == ObjectRole.Bench) q = p + tan * side * 2.2f;
                            if (!w.OnLand(q, r)) continue;
                            if (ShoreKit.PutAt(ctx, w.Occ, q, float.NaN, ShoreKit.Yaw(-inw), role, AssetMix.One(""), role == ObjectRole.Bench ? null : lounger, r,
                                Hashing.Mix(v, side > 0 ? 7u : 9u), 0.3f, 0.1f, 3))
                                w.Loungers++;
                        }
                    }
            }
            if (boats > 0)
            {
                string cat = LeisureKit.Category(ctx, "", AssetCategories.BeachBoat, AssetCategories.ShoreBoat);
                int placed = 0;
                float every = (to - from) / (boats + 1f);
                for (int k = 1; cat != null && k <= boats; k++)
                {
                    var c = shore.At(from + every * k, out var inw, out var tan);
                    var p = c + inw * 2f;
                    if (!w.OnLand(p, 1.6f)) continue;
                    if (LeisureKit.Put(ctx, w.Occ, p, ShoreKit.Yaw(inw) + 0.4f * (Hashing.Unit(Hashing.Mix(ctx.Seed, (uint)k)) - 0.5f), ObjectRole.Furniture, AssetMix.One(""), cat, 1.6f,
                        Hashing.Mix(ctx.Seed, 0xB0C0u + (uint)k), 0.3f, 0.3f, 3))
                        placed++;
                }
                w.Boats += placed;
            }
        }

        // ───────────── reed edge ─────────────

        public static void Reeds(ShoreWork w, float from, float to, float density, float landDepth = 3f, float waterDepth = 4f)
        {
            var ctx = w.Ctx; var shore = w.Shore;
            if (density <= 0f) return;
            string cat = LeisureKit.Category(ctx, "", AssetCategories.WetlandReed, AssetCategories.Bush);
            if (cat == null) return;
            var role = ObjectRole.Bush;
            uint clump = Hashing.Mix(ctx.Seed, 0x4EEDu);
            var depthAt = ctx.Site?.WaterDepthAt;
            for (float s = from + 1f; s < to - 1f; s += 1.7f)
            {
                if (w.InGap(s, 3f)) continue;
                var c = shore.At(s, out var inw, out var tan);
                for (float d = -waterDepth; d <= landDepth; d += 1.5f)
                {
                    uint v = Hashing.Mix(clump, (int)(s * 3f), (int)(d * 7f + 100f));
                    var p = c + inw * d + tan * (Hashing.Unit(v) - 0.5f) * 1.2f;
                    // Clumps: value noise along the shore, thinning away from the waterline.
                    float chance = Scatter.Clustering(p, 9f, clump, 0.3f) * density * (1f - math.abs(d) / (math.max(landDepth, waterDepth) + 2f));
                    if (Hashing.Unit(Hashing.Mix(v, 3u)) > chance) continue;
                    bool wet = shore.IsWet(p);
                    if (wet ? !(shore.Outline.ContainsDisc(p, 0.8f) && depthAt != null && (depthAt(p) <= 1.2f || d > -2.5f)) : !shore.Land.ContainsDisc(p, 0.8f)) continue;
                    if (!w.Occ.Free(p, 0.8f, 0.3f, 0.1f)) continue;
                    ctx.Object(p, Hashing.Unit(Hashing.Mix(v, 5u)) * 6.283f, role, AssetMix.One(""), v, -1, cat);
                    w.Occ.Take(p, 0.8f, 4);
                    w.Reeds++;
                }
            }
        }

        // ───────────── "Path to it" ─────────────

        /// <summary>Links the shore walk to the host's paths (nearest reserved pathway within reach) at both ends and the middle.</summary>
        public static void Connect(ShoreWork w, float reach = 45f)
        {
            var ctx = w.Ctx;
            if (w.Walk.Count == 0) return;
            var reserved = ctx.Input.Reserved;
            var picks = new List<float2> { w.Walk[0].a, w.Walk[w.Walk.Count - 1].b, w.Walk[w.Walk.Count / 2].a };
            var done = new List<float2>();
            foreach (var p in picks)
            {
                if (!ShoreKit.NearestReservedPath(reserved, p, reach, out var q)) continue;
                if (math.distance(p, q) < 2f) { w.Links++; continue; }
                bool near = false;
                foreach (var d in done) if (math.distance(d, q) < 15f) near = true;
                if (near || !ShoreKit.LinkClear(reserved, p, q, 1.2f)) continue;
                // Never across the water.
                bool wet = false;
                for (int i = 1; i < 8; i++) wet |= w.Shore.IsWet(math.lerp(p, q, i / 8f));
                if (wet) continue;
                ctx.StraightNet(p, q, NetRole.Pathway, w.PathAsset);
                done.Add(q);
                w.Links++;
            }
        }

        private static float2[] Smooth(List<float2> pts, int passes)
        {
            var a = pts.ToArray();
            for (int p = 0; p < passes; p++)
            {
                var b = (float2[])a.Clone();
                for (int i = 1; i + 1 < a.Length; i++) b[i] = a[i - 1] * 0.25f + a[i] * 0.5f + a[i + 1] * 0.25f;
                a = b;
            }
            return a;
        }
    }
}
