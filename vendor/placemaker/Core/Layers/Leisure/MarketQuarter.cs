// rf-shops3 — Big market as "the market quarter" (docs/refactor/SHOPS-ARCHITECT.md; the user on Big market: "shouldn't it fill the
// entire area and have more intermingled other things?"). A market that fills the whole site, whatever its shape, laid out from the
// player's entrances the way an architect would:
//   • THE MARKET STREET runs between the two main entrances (or from the one entrance to the far side), leaving each gate square to
//     the edge and passing through the market cross: a fountain (or a planted centre in a game without one) on a paved rondel with
//     benches and four trees. Trees down its middle, lamps, benches and food carts between them, stalls facing it on both sides.
//   • THE CROSS STREET through the market cross and, on bigger sites, side streets every ~56 m on the site's own axes; streets that
//     would run beside the market street are left out. Edge trees and lamps along them.
//   • FOOD COURTS at the crossings: café tables under four trees on an accent square.
//   • BLOCKS between the streets, filled with stall rows to the site's real rim (a circle and an L are filled, not their inscribed
//     rectangle): back-to-back pairs facing the aisles, the outer stalls fronting the streets; where the market street curves
//     through the rows, single stalls line it.
//   • IN EVERY BLOCK a planting island (lawn, trees, shrubs, benches) or a café grove (tables under a square of four trees).
//   • GATES: a threshold pad, bollards across it, bike racks beside it (a bike shelter on big sites), food carts just inside, a
//     signpost, worn ground where the walk meets the street. Car parks open onto the market with the same pad and bollards.
//   • GROUND: a paving composition (PavingKit) — a lighter market floor, the darker frame, the streets in the walk paving, accents
//     at the market cross, the courts and the gates — with bare soil in the tree pits and planting islands (terrain paint, "Ground
//     paint" on), so the market is not one white field.

using System;
using System.Collections.Generic;
using Placemaker.Core.Geometry;
using Placemaker.Core.Layers.Common;
using Placemaker.Core.Layers.Plaza;
using Placemaker.Core.Paint;
using Unity.Mathematics;

namespace Placemaker.Core.Layers.Leisure
{
    internal sealed class MarketQuarter
    {
        /// <summary>A way in for people on the market's outline: a gate the player placed, or a car park's door into the market.</summary>
        public struct Door
        {
            public float2 At, In;
            public int Segment;
            /// <summary>A car park's door (no bike racks, no worn ground to the street).</summary>
            public bool Lot;
            /// <summary>A street in front of it (worn ground to the kerb).</summary>
            public bool Street;
        }

        private sealed class Street
        {
            public List<float2> Line;
            public float Half;
            /// <summary>0 the market street, 1 the cross street, 2 a side street, 3 a gate lane.</summary>
            public int Rank;
            /// <summary>Grid line: along the axis (v = Coord) or across it (u = Coord); null for the market street and gate lanes.</summary>
            public bool? AlongAxis;
            public float Coord;
            /// <summary>A side street that runs on one side of a main street only: the side of the cross street (u) / the market street (v); 0 = both.</summary>
            public int SideU, SideV;
        }

        // Stall: 4.2 m along the row, 3.4 m deep; a pair back to back is 7.2 m deep.
        private const float StallLen = 4.2f, StallDepth = 3.4f, StallGap = 1.0f, PairDepth = StallDepth * 2f + 0.4f;
        private const float MinStrip = 22f;

        private readonly LayerContext _ctx;
        private readonly LayerRegion _r;
        private readonly Occupancy _occ;
        private readonly PavingPlan _pave;
        private readonly PavingPalette _pal;
        private readonly List<Door> _doors;
        private readonly List<float2> _bays;
        private readonly bool _paint;
        private readonly List<float2[]> _avoid;
        private PaintKit _kit;

        private float2 _hub, _a, _s, _focal;
        private float _ins, _fw, _step, _hubR;
        private bool _spineOnGrid;
        private Street _spine;
        private readonly List<Street> _streets = new List<Street>();
        private readonly List<float2> _crossings = new List<float2>();
        private readonly List<float2> _courts = new List<float2>();
        // Ground: bare pits / islands (holes, painted) and the accent pieces, laid in this order before the streets and the field.
        private readonly List<float2[]> _pits = new List<float2[]>();
        private readonly List<float2[]> _islands = new List<float2[]>();
        private readonly List<float2[]> _focalPieces = new List<float2[]>();
        private readonly List<float2[]> _bandPieces = new List<float2[]>();
        private readonly List<float2[]> _accentPieces = new List<float2[]>();
        private readonly List<float2[]> _inlayPieces = new List<float2[]>();
        private readonly List<float2> _treeSpots = new List<float2>();
        private int _trees, _carts, _racks, _bollards, _groves, _plantings, _lamps, _benches, _stalls;
        private readonly string[] _treeIds;
        private readonly bool _treesOk;

        private MarketQuarter(LayerContext ctx, LayerRegion region, Occupancy occ, PavingPlan pave, PavingPalette pal, List<Door> doors, List<float2> bays,
            List<float2[]> avoid)
        {
            _ctx = ctx; _r = region; _occ = occ; _pave = pave; _pal = pal; _doors = doors ?? new List<Door>(); _bays = bays ?? new List<float2>();
            _avoid = avoid ?? new List<float2[]>();
            _paint = ctx.B("groundPaint");
            _treeIds = string.IsNullOrEmpty(ctx.S("treeAsset")) ? PlazaArt.PlaneTrees : new[] { ctx.S("treeAsset") };
            _treesOk = AssetAvailability.Require(ctx.Plan, AssetCategories.Tree, new[] { ctx.S("treeAsset") }, Keys.Category(AssetCategories.Tree));
        }

        /// <summary>Plans the quarter on <paramref name="region"/> (the site minus car parks and service lanes). Returns the stalls placed.</summary>
        public static int Plan(LayerContext ctx, LayerRegion region, Occupancy occ, PavingPlan pave, PavingPalette pal, List<Door> doors, List<float2> bays,
            List<float2[]> avoid, out float2 hub, out float hubRadius)
        {
            var q = new MarketQuarter(ctx, region, occ, pave, pal, doors, bays, avoid);
            q.Run();
            hub = q._hub; hubRadius = q._hubR;
            return q._stalls;
        }

        private void Run()
        {
            _focal = LeisureKit.FocalPoint(_r, out _ins);
            _fw = math.clamp(_ins * 0.05f, 2.2f, 3.6f);
            // Blocks about a fifth of the site across, 44 to 64 m (stalls are real-size props: the grid is in real metres).
            _step = math.clamp(math.sqrt(_r.Area) * 0.2f, 44f, 64f);
            Spine();
            _ctx.Check();
            Grid();
            GateLanes();
            foreach (var st in _streets) Take(st);
            Crossings();
            _ctx.Check();
            // The market cross is open paving where the streets meet: its benches and trees may stand where the streets run through it.
            _streetMode = true;
            try { Hub(); } finally { _streetMode = false; }
            Courts();
            Terminus();
            Gates();
            Bays();
            Islands();
            _ctx.Check();
            StreetTrees();
            FrameTrees();
            PlazaDressing.Lighting(_ctx, _occ, _r, GateList(), _hub, 0f, PlazaArt.ParkLamp);
            _ctx.Check();
            Lattice();
            LineCurves();
            _ctx.Check();
            Ground();
            Facts();
        }

        // ───────────── the market street ─────────────

        private void Spine()
        {
            // The site's own axes (a rectilinear outline), else none (a round one): the grid follows the site, the market street the gates.
            bool square = _r.DominantEdgeAngle(out float edgeAngle, out float coherence) && coherence > 0.35f;
            float2 T;
            var line = new List<float2>();
            var doors = _doors;
            Door? first = null, second = null;
            if (doors.Count >= 2)
            {
                // The two ways in farthest apart (gates count a little more than car park doors). Two ways in side by side on one edge
                // are one entrance: the market street starts there and runs to the far side (the other gets a lane to it).
                int bi = 0, bj = 1; float best = -1f;
                for (int i = 0; i < doors.Count; i++)
                    for (int j = i + 1; j < doors.Count; j++)
                    {
                        float d = math.distance(doors[i].At, doors[j].At) * (doors[i].Lot ? 0.85f : 1f) * (doors[j].Lot ? 0.85f : 1f);
                        if (d > best) { best = d; bi = i; bj = j; }
                    }
                if (math.distance(doors[bi].At, doors[bj].At) < 60f && math.dot(doors[bi].In, doors[bj].In) > 0.7f)
                    first = doors[bi].Lot ? doors[bj] : doors[bi];
                else { first = doors[bi]; second = doors[bj]; }
            }
            else if (doors.Count == 1) first = doors[0];
            if (first.HasValue && second.HasValue)
            {
                var A = first.Value; var B = second.Value;
                T = math.normalizesafe(B.At - A.At, A.In);
                // A chord passing near the middle of the site: a straight market street, its cross on it. Else it bends through the middle.
                float off = math.sqrt(SegmentIndex.DistSq(_focal, A.At, B.At));
                if (off < 0.25f * _ins && math.dot(A.In, B.In) < -0.7f)
                {
                    _hub = ClosestOn(_focal, A.At, B.At);
                    Sample(line, A.At, A.At, B.At, B.At, true);
                }
                else
                {
                    _hub = _focal;
                    float dA = math.distance(A.At, _hub), dB = math.distance(_hub, B.At);
                    Sample(line, A.At, A.At + A.In * dA / 3f, _hub - T * dA / 3f, _hub, true);
                    Sample(line, _hub, _hub + T * dB / 3f, B.At + B.In * dB / 3f, B.At, false);
                }
            }
            else if (first.HasValue)
            {
                var A = first.Value;
                _farEnd = true;
                _hub = _focal;
                if (math.distance(A.At, _hub) < 24f && _r.Contains(A.At + A.In * 24f)) _hub = A.At + A.In * 24f;
                T = math.normalizesafe(math.lerp(A.In, math.normalizesafe(_hub - A.At, A.In), 0.6f), A.In);
                float dA = math.distance(A.At, _hub);
                Sample(line, A.At, A.At + A.In * dA / 3f, _hub - T * dA / 3f, _hub, true);
                var far = FarSide(_hub, T);
                Sample(line, _hub, _hub, far, far, false);
            }
            else
            {
                // No entrance yet (Build waits for one) or a market planted inside another type: along the site's long axis.
                LeisureKit.Frame(_r, out _, out var major, out _, out _, out _);
                _hub = _focal;
                T = major;
                var a = FarSide(_hub, -T); var b = FarSide(_hub, T);
                Sample(line, a, a, b, b, true);
            }
            // A market street whose curve leaves the site falls back to straight legs (an L's inner corner).
            if (!Inside(line, 3f)) Straighten(line);
            // Straight within a metre and a half: lay it exactly straight (the trees and stalls line up).
            float dev = 0f;
            foreach (var p in line) dev = math.max(dev, math.sqrt(SegmentIndex.DistSq(p, line[0], line[line.Count - 1])));
            bool straight = dev < 1.5f;
            if (straight) { var a0 = line[0]; var b0 = line[line.Count - 1]; line.Clear(); Sample(line, a0, a0, b0, b0, true); _hub = ClosestOn(_hub, a0, b0); }
            T = math.normalizesafe(line[line.Count - 1] - line[0], T);
            if (square)
            {
                // The nearest of the site's four axis directions.
                float bestDot = -2f; float2 bestDir = T;
                for (int k = 0; k < 4; k++)
                {
                    float ang = edgeAngle + k * math.PI * 0.5f;
                    var d = new float2(math.cos(ang), math.sin(ang));
                    if (math.dot(d, T) > bestDot) { bestDot = math.dot(d, T); bestDir = d; }
                }
                _a = bestDir;
            }
            else _a = T;
            _s = new float2(-_a.y, _a.x);
            _spineOnGrid = straight && math.abs(math.dot(T, _a)) > 0.998f;
            float half = _r.Area >= 6000f ? 5f : 3.5f;
            _spine = new Street { Line = line, Half = half, Rank = 0 };
            if (_spineOnGrid) { _spine.AlongAxis = true; _spine.Coord = math.dot(_hub - _hub, _s); }
            _streets.Add(_spine);
            _hubR = math.clamp(_ins * 0.16f, 11f, 15f);
            if (_ins < 30f) _hubR = math.max(6f, _ins * 0.3f);
        }

        /// <summary>The boundary point straight ahead of <paramref name="p"/> along <paramref name="dir"/> (the chord's far end).</summary>
        private float2 FarSide(float2 p, float2 dir)
        {
            if (LeisureKit.Chord(_r, p, dir, 0f, out var a, out var b))
                return math.dot(b - p, dir) >= math.dot(a - p, dir) ? b : a;
            return p + dir * 10f;
        }

        private static float2 ClosestOn(float2 p, float2 a, float2 b)
        {
            var d = b - a; float l2 = math.lengthsq(d);
            if (l2 < 1e-6f) return a;
            return a + d * math.saturate(math.dot(p - a, d) / l2);
        }

        private static float2 Bez(float2 a, float2 b, float2 c, float2 d, float t)
        {
            float u = 1f - t;
            return u * u * u * a + 3f * u * u * t * b + 3f * u * t * t * c + t * t * t * d;
        }

        /// <summary>Samples a cubic every ~2 m into <paramref name="line"/> (the first point only when <paramref name="first"/>).</summary>
        private static void Sample(List<float2> line, float2 a, float2 b, float2 c, float2 d, bool first)
        {
            float approx = math.distance(a, b) + math.distance(b, c) + math.distance(c, d);
            int n = math.max(2, (int)math.ceil(approx / 2f));
            for (int i = first ? 0 : 1; i <= n; i++) line.Add(Bez(a, b, c, d, i / (float)n));
        }

        private bool Inside(List<float2> line, float margin)
        {
            float total = 0f; for (int i = 1; i < line.Count; i++) total += math.distance(line[i - 1], line[i]);
            float run = 0f;
            for (int i = 0; i < line.Count; i++)
            {
                if (i > 0) run += math.distance(line[i - 1], line[i]);
                if (run < 6f || total - run < 6f) continue;
                if (!_r.Contains(line[i]) || _r.BoundaryDistance(line[i], margin + 1f) < margin) return false;
            }
            return true;
        }

        private void Straighten(List<float2> line)
        {
            var a = line[0]; var b = line[line.Count - 1];
            line.Clear();
            Sample(line, a, a, _hub, _hub, true);
            Sample(line, _hub, _hub, b, b, false);
        }

        // ───────────── the grid ─────────────

        private void Grid()
        {
            Extents(out float uMin, out float uMax, out float vMin, out float vMax);
            float reach = math.length(_r.Max - _r.Min) + 10f;
            // The cross street through the market cross (u = 0), the whole way across.
            _crossOnGrid = AddGridLine(_hub - _s * reach, _hub + _s * reach, 3.5f, 1, false, 0f, 0, 0) > 0;
            // Side streets every step, staggered across the main streets as in an old market town (T-junctions, not one chequerboard):
            // across the axis (u = const) at k·step on one side of the market street and (k + ½)·step on the other; along the axis
            // (v = const) the same either side of the cross street. Without a straight market street on the grid (it bends through) the
            // cross lines run the whole way; without the cross street the long lines do.
            // Staggered only on big sites (a small one keeps a calm symmetric cross of streets).
            bool staggerU = _spineOnGrid && math.min(-uMin, uMax) >= 2.2f * _step, staggerV = _crossOnGrid && math.min(-vMin, vMax) >= 2.2f * _step;
            foreach (int sv in staggerU ? new[] { -1, 1 } : new[] { 0 })
            {
                float off = sv > 0 ? 0.5f : 0f;
                for (int k = -40; k <= 40; k++)
                {
                    float u = (k + off) * _step;
                    if (math.abs(u) < _step * 0.7f || u < uMin + MinStrip || u > uMax - MinStrip) continue;
                    var c = _hub + _a * u;
                    if (sv == 0) AddGridLine(c - _s * reach, c + _s * reach, 2.5f, 2, false, u, 0, 0);
                    else AddGridLine(c, c + _s * (sv * reach), 2.5f, 2, false, u, 0, sv);
                }
            }
            foreach (int su in staggerV ? new[] { -1, 1 } : new[] { 0 })
            {
                float off = su > 0 ? 0.5f : 0f;
                for (int k = -40; k <= 40; k++)
                {
                    float v = (k + off) * _step;
                    if (math.abs(v) < _step * 0.7f || v < vMin + MinStrip || v > vMax - MinStrip) continue;
                    var c = _hub + _s * v;
                    if (su == 0) AddGridLine(c - _a * reach, c + _a * reach, 2.5f, 2, true, v, 0, 0);
                    else AddGridLine(c, c + _a * (su * reach), 2.5f, 2, true, v, su, 0);
                }
            }
        }

        private bool _crossOnGrid;

        private void Extents(out float uMin, out float uMax, out float vMin, out float vMax)
        {
            uMin = vMin = float.MaxValue; uMax = vMax = float.MinValue;
            foreach (var p in _r.Ring)
            {
                var d = p - _hub;
                uMin = math.min(uMin, math.dot(d, _a)); uMax = math.max(uMax, math.dot(d, _a));
                vMin = math.min(vMin, math.dot(d, _s)); vMax = math.max(vMax, math.dot(d, _s));
            }
        }

        /// <summary>A grid street along a to b, clipped to the site (pieces of 30 m and more). Returns the pieces added.</summary>
        private int AddGridLine(float2 a, float2 b, float half, int rank, bool along, float coord, int sideU, int sideV)
        {
            int added = 0;
            foreach (var (p, q) in Poly.ClipSegmentToPolygon(_r.Ring, a, b))
            {
                float len = math.distance(p, q);
                if (len < 30f) continue;
                var d = math.normalizesafe(q - p);
                // Stop short of the frame (the frame walk runs along the outline); an end on a main street stays on it.
                var p1 = _r.BoundaryDistance(p, 2f) < 0.6f ? p + d * (_fw + 0.5f) : p;
                var q1 = _r.BoundaryDistance(q, 2f) < 0.6f ? q - d * (_fw + 0.5f) : q;
                // Left out where it would run beside the market street, the same way (the market street serves); crossing it is fine.
                int near = 0, n = 0;
                for (float t = 0f; t <= 1.0001f; t += 4f / len)
                {
                    var x = math.lerp(p1, q1, t); n++;
                    if (NearestDir(_spine.Line, x, out var sd) < _spine.Half + half + 10f && math.abs(math.dot(sd, d)) > 0.8f) near++;
                }
                if (near > 0.25f * n) continue;
                var line = new List<float2>();
                Sample(line, p1, p1, q1, q1, true);
                _streets.Add(new Street { Line = line, Half = half, Rank = rank, AlongAxis = along, Coord = coord, SideU = sideU, SideV = sideV });
                added++;
            }
            return added;
        }

        /// <summary>Distance to a polyline and its direction at the nearest point.</summary>
        private static float NearestDir(List<float2> line, float2 p, out float2 dir)
        {
            float best = float.MaxValue; dir = new float2(1f, 0f);
            for (int i = 0; i + 1 < line.Count; i++)
            {
                float d2 = SegmentIndex.DistSq(p, line[i], line[i + 1]);
                if (d2 < best) { best = d2; dir = math.normalizesafe(line[i + 1] - line[i], dir); }
            }
            return math.sqrt(best);
        }

        /// <summary>
        /// Places one object where its disc is free. On a street (<see cref="_streetMode"/>: trees down the middle of the market street,
        /// trees and lamps at the street edges) the streets' own corridors do not count, only what stands there already.
        /// </summary>
        private bool Put(float2 p, float yaw, ObjectRole role, AssetRef asset, string category, float radius, uint v, float margin = 0.3f, float gap = 0.2f, int kind = 1, int age = -1)
        {
            if (!_streetMode) return LeisureKit.Put(_ctx, _occ, p, yaw, role, asset, category, radius, v, margin, gap, kind, age);
            if (!_r.ContainsDisc(p, margin) || _occ.Discs.Overlaps(p, radius, gap) || _occ.HitsPolygon(p, radius)) return false;
            _ctx.Object(p, yaw, role, asset, v, age, string.IsNullOrEmpty(category) ? null : category);
            _occ.Take(p, radius, kind);
            return true;
        }

        private bool _streetMode;
        /// <summary>The market street's far end has no way in (one entrance: it runs on to the far side).</summary>
        private bool _farEnd;

        /// <summary>
        /// Where the market street ends at the far side without a way in: a pocket square closing the view down the street — an accent
        /// pad, two trees, flowerbeds against the edge and benches looking back up the street.
        /// </summary>
        private void Terminus()
        {
            if (!_farEnd || _spine.Line.Count < 3) return;
            var line = _spine.Line;
            var e = line[line.Count - 1];
            var t = math.normalizesafe(e - line[line.Count - 3]);
            var side = new float2(-t.y, t.x);
            var c = e - t * (_fw + 6f);
            if (!_r.Contains(c) || math.distance(c, _hub) < _hubR + 14f) return;
            _accentPieces.Add(LayerRegion.Rect(c, t, 6f, _spine.Half + 3.5f));
            _streetMode = true;
            try
            {
                foreach (int sg in new[] { -1, 1 })
                {
                    Tree(c + side * (sg * (_spine.Half + 1.6f)) + t * 2f, 0x7E00u + (uint)(sg + 1), true);
                    var bp = c - t * 1.5f + side * (sg * 2.2f);
                    if (Put(bp, math.atan2(-t.x, -t.y), ObjectRole.Bench, PlazaArt.Or(_ctx, "benchAsset", PlazaArt.Benches, 0), null, 0.9f, Hashing.Mix(_ctx.Seed, 0x7E10u + (uint)(sg + 1)), 0.2f, 0.1f)) _benches++;
                }
                if (AssetAvailability.Has(AssetCategories.Flowerbed))
                    for (int k = -1; k <= 1; k++)
                        Put(c + t * 3.6f + side * (k * 2.8f), math.atan2(side.x, side.y), ObjectRole.Planter, PlazaArt.Or(_ctx, "flowerbedAsset", PlazaArt.Flowerbeds, (uint)(k + 1)), AssetCategories.Flowerbed,
                            1.2f, Hashing.Mix(_ctx.Seed, 0x7E20u + (uint)(k + 1)), 0.2f, 0.1f);
                Put(c - t * 1.5f, 0f, ObjectRole.Bin, PlazaArt.Bin, null, 0.4f, Hashing.Mix(_ctx.Seed, 0x7E30u), 0.2f, 0.1f);
            }
            finally { _streetMode = false; }
            _occ.Take(c, 6.5f, 3);
            _ctx.Plan.Facts["marketStreetEnd"] = 1;
        }

        private static float DistToLine(List<float2> line, float2 p)
        {
            float best = float.MaxValue;
            for (int i = 0; i + 1 < line.Count; i++) best = math.min(best, SegmentIndex.DistSq(p, line[i], line[i + 1]));
            return math.sqrt(best);
        }

        private float DistToStreets(float2 p, Street except = null)
        {
            float best = float.MaxValue;
            foreach (var st in _streets) if (st != except) best = math.min(best, DistToLine(st.Line, p) - st.Half);
            return best;
        }

        /// <summary>The other ways in: a lane straight in from the gate to the first street it meets (at most 70 m).</summary>
        private void GateLanes()
        {
            foreach (var d in _doors)
            {
                if (math.distance(d.At, _spine.Line[0]) < 3f || math.distance(d.At, _spine.Line[_spine.Line.Count - 1]) < 3f) continue;
                float end = -1f;
                for (float t = 4f; t <= 70f; t += 1f)
                {
                    var p = d.At + d.In * t;
                    if (!_r.Contains(p)) break;
                    if (DistToStreets(p) < 0.5f) { end = t; break; }
                }
                if (end < 0f) continue;
                var line = new List<float2>();
                Sample(line, d.At, d.At, d.At + d.In * end, d.At + d.In * end, true);
                _streets.Add(new Street { Line = line, Half = 2.5f, Rank = 3 });
            }
        }

        private void Take(Street st)
        {
            // Not inside the market cross: an open paved rondel the streets meet at.
            for (int i = 0; i + 1 < st.Line.Count; i++)
                if (math.distance(math.lerp(st.Line[i], st.Line[i + 1], 0.5f), _hub) > _hubR - 1f)
                    _occ.TakeCorridor(st.Line[i], st.Line[i + 1], st.Half, 6);
        }

        private void Crossings()
        {
            for (int i = 0; i < _streets.Count; i++)
                for (int j = i + 1; j < _streets.Count; j++)
                {
                    var A = _streets[i].Line; var B = _streets[j].Line;
                    for (int x = 0; x + 1 < A.Count; x++)
                        for (int y = 0; y + 1 < B.Count; y++)
                            if (Poly.SegmentIntersection(A[x], A[x + 1], B[y], B[y + 1], out var pt, out _, out _))
                            {
                                bool dup = false;
                                foreach (var c in _crossings) if (math.distance(c, pt) < 6f) { dup = true; break; }
                                if (!dup) _crossings.Add(pt);
                            }
                }
        }

        // ───────────── the market cross ─────────────

        private void Hub()
        {
            var ctx = _ctx;
            string kind = ctx.S("centerKind");
            if (string.IsNullOrEmpty(kind) || kind == "none") kind = "fountain";
            float coreR = math.clamp(_ins * 0.1f, 4.5f, 7.5f);
            // Room round the centre for a ring of benches and the four trees (at hubR − 1.8, 1.2 m crowns).
            coreR = math.min(coreR, _hubR - 4.6f);
            if (coreR < 3f) coreR = 3f;
            bool planted = PlazaDressing.CentreAssetsMissing(ctx);
            string sub = PlazaDressing.CenterCategory(kind == "any" ? "fountain" : kind);
            string cat = planted ? null : LeisureKit.Category(ctx, ctx.S("centerpiece"), sub ?? AssetCategories.Centerpiece, AssetCategories.Centerpiece);
            if (planted || cat == null)
            {
                // The clean game has no fountain or statue: a lawn with a specimen tree, shrubs round it, benches looking out.
                PlazaDressing.SpecimenCentre(ctx, _occ, _hub, coreR, _pave, 0x5BE1u);
            }
            else
            {
                var asset = PlazaDressing.CenterAsset(ctx, kind == "any" ? "fountain" : kind);
                // A small market cross takes the small fountain (the grand one is 9 m across).
                if (kind != "statue" && coreR < 5.5f && string.IsNullOrEmpty(ctx.S("centerpiece"))) asset = PlazaArt.SmallFountain;
                ctx.Object(_hub, math.atan2(_a.x, _a.y), ObjectRole.Centerpiece, asset, Hashing.Mix(ctx.Seed, 0xCE0u), -1, cat);
                _occ.Take(_hub, coreR, 0);
                _inlayPieces.Add(LayerRegion.Circle(_hub, coreR, 32, 0f));
                int benches = math.clamp((int)(2f * math.PI * (coreR + 1.6f) / 8f), 4, 8);
                for (int k = 0; k < benches; k++)
                {
                    float ang = math.atan2(_a.y, _a.x) + (k + 0.5f) * 2f * math.PI / benches;
                    var dir = new float2(math.cos(ang), math.sin(ang));
                    if (Put(_hub + dir * (coreR + 1.6f), math.atan2(-dir.x, -dir.y), ObjectRole.Bench, PlazaArt.Or(ctx, "benchAsset", PlazaArt.Benches, 0), null, 0.9f,
                        Hashing.Mix(ctx.Seed, 0xCE10u + (uint)k), 0.2f, 0.1f)) _benches++;
                }
            }
            // Four trees on the diagonals, lamps beside them, the rondel kept clear for the streets that meet here.
            for (int k = 0; k < 4; k++)
            {
                float ang = math.atan2(_a.y, _a.x) + math.PI * 0.25f + k * math.PI * 0.5f;
                var dir = new float2(math.cos(ang), math.sin(ang));
                Tree(_hub + dir * (_hubR - 1.8f), 0x7500u + (uint)k, true);
                var lp = _hub + dir * (_hubR - 1.8f) + new float2(-dir.y, dir.x) * 2.6f;
                if (Put(lp, 0f, ObjectRole.Lamp, PlazaArt.Or(ctx, "lampAsset", PlazaArt.ParkLamp), null, 0.4f, Hashing.Mix(ctx.Seed, 0x7510u + (uint)k), 0.2f, 0.1f)) _lamps++;
            }
            for (int k = 0; k < 2; k++)
            {
                float ang = math.atan2(_a.y, _a.x) + math.PI * 0.25f + k * math.PI;
                var dir = new float2(math.cos(ang), math.sin(ang));
                Put(_hub + dir * (_hubR - 4.6f), 0f, ObjectRole.Bin, PlazaArt.Bin, null, 0.4f, Hashing.Mix(ctx.Seed, 0x7520u + (uint)k), 0.2f, 0.1f);
            }
            _occ.Take(_hub, _hubR, 0);
            _focalPieces.Add(PavingShapes.Polygon(_hub, _hubR, 40, math.atan2(_a.y, _a.x)));
            _bandPieces.AddRange(PavingShapes.Annulus(_hub, _hubR, 1.0f));
            _ctx.AddRegion(LayerRegion.Circle(_hub, coreR, 20), "core", RegionTags.Core, RegionTags.Paved);
        }

        // ───────────── food courts at the crossings ─────────────

        private void Courts()
        {
            const float H = 10f;
            var cands = new List<(float score, float2 p)>();
            foreach (var c in _crossings)
            {
                float dh = math.distance(c, _hub);
                if (dh < _hubR + 22f) continue;
                if (!_r.Contains(c) || _r.BoundaryDistance(c, 40f) < H + 8f) continue;
                // On the market street first, then the cross street; a crossing of two side streets only where nothing else is near.
                bool onSpine = DistToLine(_spine.Line, c) < 1f;
                bool onMain = onSpine;
                foreach (var st in _streets) if (st.Rank == 1 && DistToLine(st.Line, c) < 1f) onMain = true;
                cands.Add(((onSpine ? 0f : onMain ? 500f : 1000f) + dh, c));
            }
            cands.Sort((x, y) => x.score.CompareTo(y.score));
            float spacing = math.max(70f, _step * 1.6f);
            int n = 0;
            foreach (var (_, c) in cands)
            {
                bool near = false;
                foreach (var o in _courts) if (math.distance(o, c) < spacing) { near = true; break; }
                if (near) continue;
                _courts.Add(c);
                uint salt = 0xC0C0u + (uint)(n++ * 16);
                var ring = LayerRegion.Rect(c, _a, H, H);
                _focalPieces.Add(ring);
                _bandPieces.AddRange(PavingShapes.Frame(ring, 0.8f));
                // A tree in each quadrant, a café table under it, benches looking at the crossing.
                for (int k = 0; k < 4; k++)
                {
                    float su = (k & 1) == 0 ? -1f : 1f, sv = (k & 2) == 0 ? -1f : 1f;
                    var tp = c + _a * (su * 6.4f) + _s * (sv * 6.4f);
                    Tree(tp, salt + (uint)k, true);
                    var tbl = c + _a * (su * 6.6f) + _s * (sv * 3.0f);
                    Cafe(tbl, salt + 4u + (uint)k);
                    var bp = c + _a * (su * 3.0f) + _s * (sv * 7.4f);
                    if (Put(bp, LeisureKit.Yaw(bp, c), ObjectRole.Bench, PlazaArt.Or(_ctx, "benchAsset", PlazaArt.Benches, 0), null, 0.9f,
                        Hashing.Mix(_ctx.Seed, salt + 8u + (uint)k), 0.2f, 0.1f)) _benches++;
                }
                Put(c + _a * 8.6f + _s * 8.6f, 0f, ObjectRole.Bin, PlazaArt.Bin, null, 0.4f, Hashing.Mix(_ctx.Seed, salt + 12u), 0.2f, 0.1f);
                // A food cart at one corner, its hatch to the crossing.
                var cp = c - _a * 8.2f + _s * 8.0f;
                FoodCart(cp, math.normalizesafe(c - cp), salt + 13u);
                _occ.Take(c, H - 0.5f, 0);
            }
        }

        private void Cafe(float2 p, uint salt)
        {
            if (!PlazaArt.Available(_ctx, "cafeAsset", AssetCategories.CafeSeating, AssetCategories.Picnic)) return;
            uint v = Hashing.Mix(_ctx.Seed, salt);
            var asset = string.IsNullOrEmpty(_ctx.S("cafeAsset")) ? AssetMix.Pick(CafeSets, v) : AssetMix.One(_ctx.S("cafeAsset"));
            Put(p, Hashing.Unit(v) * 6.283f, ObjectRole.Furniture, asset, AssetCategories.CafeSeating, 1.6f, v, 0.4f, 0.15f, 0);
        }

        private static readonly string[] CafeSets = { "TableSet01", "TableSet02", "TableSet03", "Parasol01", "Parasol02" };

        private bool FoodCart(float2 p, float2 facing, uint salt)
        {
            string cat = AssetAvailability.Has(AssetCategories.EventFoodTruck) ? AssetCategories.EventFoodTruck : AssetAvailability.Has(AssetCategories.Kiosk) ? AssetCategories.Kiosk : null;
            if (cat == null) return false;
            uint v = Hashing.Mix(_ctx.Seed, salt);
            if (!Put(p, math.atan2(facing.x, facing.y), ObjectRole.Furniture, AssetMix.Pick(PlazaArt.Carts, v), cat, 2.1f, v, 0.6f, 0.2f, 0)) return false;
            _carts++;
            return true;
        }

        // ───────────── gates ─────────────

        private void Gates()
        {
            bool big = _r.Area >= 30000f;
            int gi = 0;
            foreach (var d in _doors)
            {
                uint salt = 0x6A70u + (uint)(gi * 32);
                var side = new float2(-d.In.y, d.In.x);
                float w = 8f;
                foreach (var st in _streets)
                    if (DistToLine(st.Line, d.At + d.In * 2f) < 1.5f) w = math.max(w, st.Half * 2f);
                // The threshold: an accent pad from the outline in, kept clear.
                var pad = LayerRegion.Rect(d.At + d.In * 3f, d.In, 3.4f, w * 0.5f + 0.6f);
                _accentPieces.Add(pad);
                _occ.TakeCorridor(d.At, d.At + d.In * 6f, w * 0.5f, 6);
                _ctx.Object(d.At, math.atan2(d.In.x, d.In.y), ObjectRole.AccessPoint, new AssetRef("", "Pedestrian Access Location"), 0);
                // Bollards across the mouth: people walk through, cars do not.
                if (_ctx.B("bollards") && PlazaArt.Available(_ctx, "bollardAsset", AssetCategories.Bollard))
                {
                    var asset = PlazaArt.Or(_ctx, "bollardAsset", PlazaArt.Bollard);
                    int nb = math.max(3, (int)math.floor(w / 1.6f) + 1);
                    for (int k = 0; k < nb; k++)
                    {
                        float o = -w * 0.5f + w * k / (nb - 1);
                        var bp = d.At + d.In * 1.1f + side * o;
                        if (!_r.ContainsDisc(bp, 0.3f)) continue;
                        _ctx.Object(bp, math.atan2(side.x, side.y), ObjectRole.Furniture, asset, Hashing.Mix(_ctx.Seed, salt + (uint)k), -1, AssetCategories.Bollard);
                        _occ.Take(bp, 0.3f, 1);
                        _bollards++;
                    }
                }
                // Flower pots flanking the way in.
                foreach (int sg in new[] { -1, 1 })
                    Put(d.At + d.In * 1.1f + side * (sg * (w * 0.5f + 1.1f)), 0f, ObjectRole.Planter, PlazaArt.Or(_ctx, "planterAsset", PlazaArt.Pots, salt + (uint)(sg + 5)),
                        AssetCategories.Planter, 0.5f, Hashing.Mix(_ctx.Seed, salt + 0x10u + (uint)(sg + 1)), 0.2f, 0.1f);
                int sgn = (gi & 1) == 0 ? 1 : -1;
                if (!d.Lot)
                {
                    // Bike racks on one side of the way in (a bike shelter on the other on big sites).
                    if (AssetAvailability.Has(AssetCategories.BikeRack))
                    {
                        int racks = big ? 7 : 5;
                        for (int k = 0; k < racks; k++)
                        {
                            var rp = d.At + d.In * 2.6f + side * (sgn * (w * 0.5f + 2.6f + k * 1.0f));
                            if (Put(rp, math.atan2(side.x, side.y), ObjectRole.Furniture, AssetMix.Pick(Racks, salt), AssetCategories.BikeRack, 0.45f,
                                Hashing.Mix(_ctx.Seed, salt + 0x20u + (uint)k), 0.3f, 0.05f)) _racks++;
                        }
                        if (big)
                        {
                            var sp = d.At + d.In * 2.8f - side * (sgn * (w * 0.5f + 4.2f));
                            if (Put(sp, math.atan2(-d.In.x, -d.In.y), ObjectRole.Furniture, AssetMix.Pick(BikeShelters, salt), AssetCategories.BikeRack, 2.2f,
                                Hashing.Mix(_ctx.Seed, salt + 0x30u), 0.3f, 0.1f)) _racks++;
                        }
                    }
                    // A signpost at the way in.
                    if (AssetAvailability.Has(AssetCategories.Wayfinding))
                        Put(d.At + d.In * 5.5f - side * (sgn * (w * 0.5f + 1.2f)), math.atan2(d.In.x, d.In.y), ObjectRole.Furniture, PlazaArt.Signpost, AssetCategories.Wayfinding,
                            0.4f, Hashing.Mix(_ctx.Seed, salt + 0x40u), 0.2f, 0.1f);
                }
                else if (AssetAvailability.Has(AssetCategories.Vending))
                {
                    // At a car park's door: a vending machine for the drivers.
                    Put(d.At + d.In * 4.5f + side * (sgn * (w * 0.5f + 1.4f)), math.atan2(-side.x * sgn, -side.y * sgn), ObjectRole.Furniture, PlazaArt.Vending, AssetCategories.Vending,
                        0.6f, Hashing.Mix(_ctx.Seed, salt + 0x44u), 0.2f, 0.1f);
                }
                // Food carts just inside, their hatches to the walk.
                int carts = big ? 2 : 1;
                for (int k = 0; k < carts; k++)
                {
                    float sg = k == 0 ? -sgn : sgn;
                    var cp = d.At + d.In * (9.5f + k * 1.5f) + side * (sg * (w * 0.5f + 2.6f));
                    FoodCart(cp, -side * sg, salt + 0x50u + (uint)k);
                }
                if (d.Street && _paint) Paint().Stroke(new[] { d.At + d.In * 0.8f, d.At - d.In * 3.6f }, TerrainMaterials.Dirt, 0.5f, w * 0.8f, 1.2f, 1f, 0.7f, 0f, 0.25f, 4f, Falloff.Linear);
                gi++;
            }
        }

        private static readonly string[] Racks = { "BikeRack01", "BikeRack02", "BikeRack03" };
        private static readonly string[] BikeShelters = { "BicycleShelter01", "BicycleShelter02", "BicycleShelter03" };

        private List<Gate> GateList()
        {
            var list = new List<Gate>();
            foreach (var d in _doors) list.Add(new Gate { Position = d.At, Segment = d.Segment, AttachId = -1, Kind = MarketEntrances.Access });
            foreach (var b in _bays) list.Add(new Gate { Position = b, Segment = 0, AttachId = -1, Kind = MarketEntrances.Alley });
            return list;
        }

        /// <summary>The service lanes' ends: a loading bay kept clear for the vans, marked in the dark inlay.</summary>
        private void Bays()
        {
            foreach (var b in _bays)
            {
                _r.ClosestOnBoundary(b, out int seg, out _);
                var inward = _r.InwardNormal(seg);
                var c = b + inward * 4f;
                _inlayPieces.Add(LayerRegion.Rect(c, inward, 4f, 4.6f));
                _occ.Take(c, 5.5f, 6);
            }
        }

        // ───────────── islands and groves ─────────────

        /// <summary>
        /// The quarters of the market between its main streets (the market street on the grid splits v, the cross street splits u),
        /// each with the range it covers, whether a range end is a main street's edge, and its own side streets.
        /// </summary>
        private List<Quarter> Quarters()
        {
            Extents(out float uMin, out float uMax, out float vMin, out float vMax);
            var list = new List<Quarter>();
            float crossHalf = 0f;
            foreach (var st in _streets) if (st.Rank == 1 && st.AlongAxis == false) crossHalf = math.max(crossHalf, st.Half);
            foreach (int su in _crossOnGrid ? new[] { -1, 1 } : new[] { 0 })
                foreach (int sv in _spineOnGrid ? new[] { -1, 1 } : new[] { 0 })
                {
                    var q = new Quarter { SU = su, SV = sv };
                    q.U0 = su > 0 ? crossHalf : uMin; q.U1 = su < 0 ? -crossHalf : uMax; q.U0Street = su > 0; q.U1Street = su < 0;
                    q.V0 = sv > 0 ? _spine.Half : vMin; q.V1 = sv < 0 ? -_spine.Half : vMax; q.V0Street = sv > 0; q.V1Street = sv < 0;
                    foreach (var st in _streets)
                    {
                        if (st.Rank != 2) continue;
                        if (st.AlongAxis == false && (st.SideV == 0 || st.SideV == sv) && st.Coord > q.U0 && st.Coord < q.U1) q.ULines.Add((st.Coord, st.Half));
                        if (st.AlongAxis == true && (st.SideU == 0 || st.SideU == su) && st.Coord > q.V0 && st.Coord < q.V1) q.VLines.Add((st.Coord, st.Half));
                    }
                    q.ULines.Sort((x, y) => x.c.CompareTo(y.c));
                    q.VLines.Sort((x, y) => x.c.CompareTo(y.c));
                    list.Add(q);
                }
            return list;
        }

        private sealed class Quarter
        {
            public int SU, SV;
            public float U0, U1, V0, V1;
            public bool U0Street, U1Street, V0Street, V1Street;
            public readonly List<(float c, float h)> ULines = new List<(float c, float h)>();
            public readonly List<(float c, float h)> VLines = new List<(float c, float h)>();
        }

        private void Islands()
        {
            int n = 0;
            foreach (var qt in Quarters())
            {
                var us = new List<float> { qt.U0 }; var vs = new List<float> { qt.V0 };
                foreach (var l in qt.ULines) us.Add(l.c);
                foreach (var l in qt.VLines) vs.Add(l.c);
                us.Add(qt.U1); vs.Add(qt.V1);
                for (int i = 0; i + 1 < us.Count; i++)
                    for (int j = 0; j + 1 < vs.Count; j++)
                    {
                        float cu = 0.5f * (us[i] + us[i + 1]), cv = 0.5f * (vs[j] + vs[j + 1]);
                        float cellU = us[i + 1] - us[i], cellV = vs[j + 1] - vs[j];
                        if (cellU < 30f || cellV < 30f) continue;
                        // What the block gets, by a roll of the dice: a planting island, a café grove, a bigger green, or nothing (a block of stalls).
                        uint roll = Hashing.Mix(_ctx.Seed, (int)math.round(cu), (int)math.round(cv));
                        int what = (int)(roll % 8u);
                        if (what >= 6 && n > 0) continue;
                        float R = math.clamp(math.min(cellU, cellV) * 0.12f, 5f, 8f);
                        if (what == 5) R = math.min(9f, R * 1.25f);
                        // Near the middle of the block, a little off it, else the nearest spot with room (an edge block of a circle or an
                        // L, a block the market cross reaches into).
                        float ju = (Hashing.Unit(Hashing.Mix(roll, 1u)) - 0.5f) * 0.3f, jv = (Hashing.Unit(Hashing.Mix(roll, 2u)) - 0.5f) * 0.3f;
                        float2 c = default; bool found = false; float bestD = float.MaxValue;
                        for (int iu = -3; iu <= 3; iu++)
                            for (int iv = -3; iv <= 3; iv++)
                            {
                                var q = _hub + _a * (cu + (iu * 0.1f + ju) * cellU) + _s * (cv + (iv * 0.1f + jv) * cellV);
                                float off = iu * iu + iv * iv;
                                if (off >= bestD) continue;
                                if (!_r.Contains(q) || _r.BoundaryDistance(q, 40f) < R * 1.6f + _fw + 4f) continue;
                                if (_occ.Clearance(q, 30f) < R * 1.6f + 4f) continue;
                                bool close = false;
                                foreach (var o in _courts) if (math.distance(o, q) < 26f) close = true;
                                if (close) continue;
                                c = q; found = true; bestD = off;
                            }
                        if (!found) continue;
                        if (what <= 2 || what == 5) Planting(c, R, 0x1A00u + (uint)(n * 32), what == 5);
                        else Grove(c, 0x1B00u + (uint)(n * 32));
                        n++;
                    }
            }
        }

        /// <summary>A planting island: an oval of bare ground (lawn painted on) with trees, shrubs round its rim and benches looking out.</summary>
        private void Planting(float2 c, float R, uint salt, bool green = false)
        {
            // An oval along the rows, or (a "green") a rounder, freer shape turned its own way with a clump of trees.
            float r1 = green ? R * 1.25f : R * 1.45f, r2 = green ? R * 1.05f : R * 0.9f;
            float turn = green ? (Hashing.Unit(Hashing.Mix(_ctx.Seed, salt + 3u)) - 0.5f) * 1.2f : 0f;
            var ea = math.cos(turn) * _a + math.sin(turn) * _s; var es = new float2(-ea.y, ea.x);
            float wobAmp = green ? 0.12f : 0.06f, phase = Hashing.Unit(Hashing.Mix(_ctx.Seed, salt)) * 6f;
            var ring = new float2[green ? 22 : 18];
            for (int k = 0; k < ring.Length; k++)
            {
                float ang = k * 2f * math.PI / ring.Length;
                float wob = 1f + wobAmp * math.sin(ang * 3f + phase) + (green ? 0.05f * math.sin(ang * 5f + phase * 2f) : 0f);
                ring[k] = c + ea * (math.cos(ang) * r1 * wob) + es * (math.sin(ang) * r2 * wob);
            }
            _islands.Add(ring);
            if (green)
            {
                // A clump of three or four trees, off centre.
                int clump = R >= 7f ? 4 : 3;
                for (int k = 0; k < clump; k++)
                {
                    float ang = phase + k * 2f * math.PI / clump;
                    Tree(c + ea * (math.cos(ang) * r1 * 0.42f) + es * (math.sin(ang) * r2 * 0.42f), salt + (uint)k, false);
                }
            }
            else
            {
                // Trees along its length, a third in the middle on bigger islands.
                int trees = R >= 6.5f ? 3 : 2;
                for (int k = 0; k < trees; k++)
                {
                    float u = trees == 3 ? (k - 1) * r1 * 0.55f : (k == 0 ? -1f : 1f) * r1 * 0.38f;
                    Tree(c + ea * u, salt + (uint)k, false);
                }
            }
            // Shrubs and flowers round the rim.
            int shrubs = math.clamp((int)(Perimeter(ring) / 2.6f), 6, 26);
            for (int k = 0; k < shrubs; k++)
            {
                float ang = (k + 0.5f) * 2f * math.PI / shrubs;
                var p = c + ea * (math.cos(ang) * (r1 - 0.9f)) + es * (math.sin(ang) * (r2 - 0.9f));
                bool nearTree = false;
                foreach (var t in _treeSpots) if (math.distancesq(t, p) < 4f) { nearTree = true; break; }
                if (nearTree) continue;
                var asset = PlazaArt.Or(_ctx, "bushAsset", (k % 3) == 0 ? PlazaArt.GreenBushes : PlazaArt.CityBushes, (uint)k + salt);
                _ctx.Object(p, ang, ObjectRole.Bush, asset, Hashing.Mix(_ctx.Seed, salt + 0x40u + (uint)k), -1, AssetCategories.Bush);
            }
            _occ.Take(c - ea * (r1 * 0.5f), r2 + 0.4f, 3);
            _occ.Take(c + ea * (r1 * 0.5f), r2 + 0.4f, 3);
            _occ.Take(c, math.max(r2, r1 * 0.6f) + 0.4f, 3);
            // Benches on the long sides, backs to the planting.
            foreach (int sg in new[] { -1, 1 })
            {
                var dir = es * sg;
                var bp = c + dir * (r2 * 1.04f + 0.9f);
                if (Put(bp, math.atan2(dir.x, dir.y), ObjectRole.Bench, PlazaArt.Or(_ctx, "benchAsset", PlazaArt.Benches, 0), null, 0.9f,
                    Hashing.Mix(_ctx.Seed, salt + 0x60u + (uint)(sg + 1)), 0.2f, 0.1f)) _benches++;
            }
            Put(c + ea * (r1 * 1.04f + 0.8f), 0f, ObjectRole.Bin, PlazaArt.Bin, null, 0.4f, Hashing.Mix(_ctx.Seed, salt + 0x70u), 0.2f, 0.1f);
            // Keep the stalls a step off the planting.
            _occ.Take(c - ea * (r1 * 0.55f), r2 + 1.6f, 3);
            _occ.Take(c + ea * (r1 * 0.55f), r2 + 1.6f, 3);
            _occ.Take(c, math.max(r2, r1 * 0.7f) + 1.6f, 3);
            if (_paint)
            {
                Paint().Fill(ring, TerrainMaterials.Lush, 0.55f, 1.0f, 0.4f);
            }
            _plantings++;
        }

        private static float Perimeter(float2[] ring)
        {
            float p = 0f; for (int i = 0; i < ring.Length; i++) p += math.distance(ring[i], ring[(i + 1) % ring.Length]); return p;
        }

        /// <summary>A café grove: four trees on a square of accent paving, café tables between them, pots at the corners.</summary>
        private void Grove(float2 c, uint salt)
        {
            const float g = 4.6f, H = 8.2f;
            var ring = LayerRegion.Rect(c, _a, H, H);
            _focalPieces.Add(ring);
            _bandPieces.AddRange(PavingShapes.Frame(ring, 0.7f));
            for (int k = 0; k < 4; k++)
            {
                float su = (k & 1) == 0 ? -1f : 1f, sv = (k & 2) == 0 ? -1f : 1f;
                Tree(c + _a * (su * g) + _s * (sv * g), salt + (uint)k, true);
            }
            Cafe(c, salt + 8u);
            for (int k = 0; k < 4; k++)
            {
                float ang = math.atan2(_a.y, _a.x) + k * math.PI * 0.5f;
                var dir = new float2(math.cos(ang), math.sin(ang));
                Cafe(c + dir * g, salt + 9u + (uint)k);
            }
            for (int k = 0; k < 4; k++)
            {
                float su = (k & 1) == 0 ? -1f : 1f, sv = (k & 2) == 0 ? -1f : 1f;
                Put(c + _a * (su * (H - 0.8f)) + _s * (sv * (H - 0.8f)), 0f, ObjectRole.Planter, PlazaArt.Or(_ctx, "planterAsset", PlazaArt.Pots, salt + (uint)k),
                    AssetCategories.Planter, 0.5f, Hashing.Mix(_ctx.Seed, salt + 0x20u + (uint)k), 0.2f, 0.1f);
            }
            _occ.Take(c, H * 1.2f, 3);
            _groves++;
        }

        // ───────────── trees, lamps, carts along the streets ─────────────

        /// <summary>A tree in a pit of bare soil (painted) or, without ground paint, in a pit of dark inlay.</summary>
        private bool Tree(float2 p, uint salt, bool pit)
        {
            if (!_treesOk) return false;
            uint v = Hashing.Mix(_ctx.Seed, salt);
            if (!Put(p, Hashing.Unit(v) * 6.283f, ObjectRole.Tree, AssetMix.Pick(_treeIds, v), null, 1.2f, v, 0.6f, 0.1f, 3, 3)) return false;
            _treeSpots.Add(p);
            _trees++;
            if (pit)
            {
                _pits.Add(LayerRegion.Rect(p, _a, 1.15f, 1.15f));
                if (_paint) Paint().Radial(p, TerrainMaterials.Dirt, 0.7f, 0.6f, 1.6f, Falloff.Smooth, 0.15f, 1.5f);
            }
            else if (_paint) Paint().Radial(p, TerrainMaterials.Dirt, 0.45f, 0.6f, 2.2f, Falloff.Smooth, 0.3f, 2f);
            return true;
        }

        private void StreetTrees()
        {
            _streetMode = true;
            try { StreetFurniture(); }
            finally { _streetMode = false; }
        }

        private void StreetFurniture()
        {
            var lamp = PlazaArt.Or(_ctx, "lampAsset", PlazaArt.ParkLamp);
            var bench = PlazaArt.Or(_ctx, "benchAsset", PlazaArt.Benches, 0);
            foreach (var st in _streets)
            {
                if (st.Rank == 3) continue;
                var line = st.Line;
                float total = 0f; for (int i = 1; i < line.Count; i++) total += math.distance(line[i - 1], line[i]);
                if (total < 20f) continue;
                if (st.Rank == 0 && st.Half >= 4.5f)
                {
                    // The market street: trees down the middle every 12 m; between them, by turns, a lamp, benches back to back, a food cart.
                    int gap = 0;
                    for (float s = 12f; s <= total - 12f; s += 12f)
                    {
                        var (p, dir) = Along(line, s);
                        if (Clear(p, 9f)) Tree(p, 0x7700u + (uint)gap, true);
                        var (m, mdir) = Along(line, s + 6f);
                        if (s + 6f > total - 10f || !Clear(m, 7f)) { gap++; continue; }
                        var side = new float2(-mdir.y, mdir.x);
                        switch (gap % 4)
                        {
                            case 0:
                            case 2:
                                if (Put(m, 0f, ObjectRole.Lamp, lamp, null, 0.4f, Hashing.Mix(_ctx.Seed, 0x7800u + (uint)gap), 0.2f, 0.1f)) _lamps++;
                                break;
                            case 1:
                                foreach (int sg in new[] { -1, 1 })
                                    if (Put(m + side * (sg * 0.7f), math.atan2(side.x * sg, side.y * sg), ObjectRole.Bench, bench, null, 0.6f,
                                        Hashing.Mix(_ctx.Seed, 0x7900u + (uint)(gap * 2 + sg + 1)), 0.2f, 0.05f)) _benches++;
                                break;
                            case 3:
                                FoodCart(m, side * ((gap & 4) == 0 ? 1f : -1f), 0x7A00u + (uint)gap);
                                break;
                        }
                        gap++;
                    }
                    continue;
                }
                // Cross and side streets: trees at the edge (both edges on the cross street, one on a side street), lamps between.
                float spacing = st.Rank == 1 ? 14f : 18f;
                int k = 0;
                for (float s = spacing * 0.5f + 4f; s <= total - 6f; s += spacing)
                {
                    var (p, dir) = Along(line, s);
                    var side = new float2(-dir.y, dir.x);
                    foreach (int sg in st.Rank == 1 ? new[] { -1, 1 } : new[] { 1 })
                    {
                        var tp = p + side * (sg * (st.Half + 0.4f));
                        if (Clear(tp, 7f, st)) Tree(tp, 0x7B00u + (uint)(k * 2 + sg + 1) + (uint)st.Rank * 0x100u, true);
                    }
                    var (m, mdir) = Along(line, s + spacing * 0.5f);
                    if ((k & 1) == 0 && s + spacing * 0.5f < total - 6f)
                    {
                        var lside = new float2(-mdir.y, mdir.x);
                        var lp = m + lside * ((st.Rank == 1 ? 1f : -1f) * (st.Half + 0.3f));
                        if (Clear(lp, 5f, st) && Put(lp, 0f, ObjectRole.Lamp, lamp, null, 0.3f, Hashing.Mix(_ctx.Seed, 0x7C00u + (uint)k + (uint)st.Rank * 0x100u), 0.05f, 0.05f)) _lamps++;
                    }
                    k++;
                }
            }
        }

        /// <summary>Clear of the market cross, the courts, the ways in and the crossings (by <paramref name="d"/> m).</summary>
        private bool Clear(float2 p, float d, Street own = null)
        {
            if (!_r.Contains(p) || _r.BoundaryDistance(p, 10f) < _fw + 1.4f) return false;
            if (math.distance(p, _hub) < _hubR + d * 0.5f) return false;
            foreach (var c in _courts) if (math.distance(p, c) < 10f + d * 0.5f) return false;
            foreach (var c in _crossings) if (math.distance(p, c) < d) return false;
            foreach (var g in _doors) if (math.distance(p, g.At) < 9f + d) return false;
            foreach (var b in _bays) if (math.distance(p, b) < 8f + d) return false;
            if (own != null)
                foreach (var st in _streets)
                    if (st != own && DistToLine(st.Line, p) < st.Half + 1.5f) return false;
            return true;
        }

        private static (float2 p, float2 dir) Along(List<float2> line, float at)
        {
            for (int i = 0; i + 1 < line.Count; i++)
            {
                float l = math.distance(line[i], line[i + 1]);
                if (at <= l || i + 2 == line.Count)
                {
                    var d = l > 1e-4f ? (line[i + 1] - line[i]) / l : new float2(1f, 0f);
                    return (line[i] + d * math.clamp(at, 0f, l), d);
                }
                at -= l;
            }
            return (line[0], new float2(1f, 0f));
        }

        /// <summary>Street trees along the outline every ~20 m (a pit cut from the frame at the kerb side), clear of the ways in and the car parks.</summary>
        private void FrameTrees()
        {
            if (_r.Area < 6000f) return;
            int count = (int)(_r.Perimeter / 20f);
            for (int i = 0; i < count; i++)
            {
                var b = _r.PointAt(_r.Perimeter * (i + 0.5f) / count, out _, out var inward, out _);
                var p = b + inward * 1.3f;
                bool skip = false;
                foreach (var d in _doors) if (math.distance(d.At, p) < 12f) skip = true;
                foreach (var bay in _bays) if (math.distance(bay, p) < 12f) skip = true;
                foreach (var a in _avoid) if (Poly.DistanceToBoundary(a, p) < 4f || Poly.Contains(a, p)) skip = true;
                if (skip) continue;
                Tree(p, 0x7D00u + (uint)i, true);
            }
        }

        // ───────────── stalls ─────────────

        private void Lattice()
        {
            if (!PlazaArt.Available(_ctx, "stallAsset", AssetCategories.MarketStall, AssetCategories.Kiosk)) return;
            float aisle = math.max(3f, _ctx.F("aisle"));
            float margin = _fw + 1.3f;
            foreach (var qt in Quarters())
            {
                var bands = Intervals(qt.VLines, qt.V0, qt.V1, qt.V0Street, qt.V1Street);
                var blocks = Intervals(qt.ULines, qt.U0, qt.U1, qt.U0Street, qt.U1Street);
                foreach (var (v0, v1, v0Street, v1Street) in bands)
                {
                    foreach (var vc in Rows(v0, v1, v0Street, v1Street, aisle))
                        foreach (var (u0, u1, u0Street, u1Street) in blocks)
                            foreach (var uc in Columns(u0, u1, u0Street, u1Street))
                            {
                                var centre = _hub + _a * uc + _s * vc;
                                var pair = new OrientedRect { Center = centre, Axis = _a, HalfLength = StallLen * 0.5f, HalfWidth = PairDepth * 0.5f };
                                if (Fits(pair, margin))
                                {
                                    Stall(pair.Local(0f, -(StallDepth * 0.5f + 0.2f)), -_s);
                                    Stall(pair.Local(0f, StallDepth * 0.5f + 0.2f), _s);
                                    continue;
                                }
                                // The rim: one stall of the pair where only one fits (a circle's edge, an L's corner, beside a curve).
                                foreach (int k in new[] { -1, 1 })
                                {
                                    var c1 = pair.Local(0f, k * (StallDepth * 0.5f + 0.2f));
                                    var one = new OrientedRect { Center = c1, Axis = _a, HalfLength = StallLen * 0.5f, HalfWidth = StallDepth * 0.5f };
                                    if (Fits(one, margin)) Stall(c1, _s * k);
                                }
                            }
                    _ctx.Check();
                }
            }
        }

        /// <summary>Intervals between street edges (a range end is a street's edge or the site's extent), with whether each end is a street.</summary>
        private static List<(float a, float b, bool aStreet, bool bStreet)> Intervals(List<(float c, float h)> lines, float lo, float hi, bool loStreet, bool hiStreet)
        {
            var r = new List<(float, float, bool, bool)>();
            float from = lo; bool fromStreet = loStreet;
            foreach (var (c, h) in lines)
            {
                if (c - h > from) r.Add((from, c - h, fromStreet, true));
                from = math.max(from, c + h); fromStreet = true;
            }
            if (hi > from) r.Add((from, hi, fromStreet, hiStreet));
            return r;
        }

        /// <summary>Centres of the stall pairs across a band: fronting the street edges, aisles evened out between two streets.</summary>
        private static List<float> Rows(float a, float b, bool aStreet, bool bStreet, float aisle)
        {
            const float m = 0.7f;
            var list = new List<float>();
            float pitch = PairDepth + aisle;
            if (aStreet && bStreet)
            {
                float w = b - a - 2f * m;
                int n = (int)math.floor((w + aisle) / pitch);
                if (n <= 0) return list;
                float gap = n > 1 ? (w - n * PairDepth) / (n - 1) : 0f;
                float start = a + m;
                if (gap > aisle * 1.7f) { gap = aisle * 1.7f; start = a + m + 0.5f * (w - (n * PairDepth + (n - 1) * gap)); }
                for (int k = 0; k < n; k++) list.Add(start + PairDepth * 0.5f + k * (PairDepth + gap));
            }
            else if (aStreet) for (float v = a + m + PairDepth * 0.5f; v < b; v += pitch) list.Add(v);
            else if (bStreet) for (float v = b - m - PairDepth * 0.5f; v > a; v -= pitch) list.Add(v);
            else
            {
                // No street along the axis (the market street bends through): rows centred on the market cross.
                float v0 = aisle * 0.5f + PairDepth * 0.5f;
                for (float v = v0; v < b; v += pitch) if (v > a) list.Add(v);
                for (float v = v0 - pitch; v > a; v -= pitch) if (v < b) list.Add(v);
            }
            return list;
        }

        /// <summary>Centres of the stalls along a block: from the cross-street edges, evened out between two streets.</summary>
        private static List<float> Columns(float a, float b, bool aStreet, bool bStreet)
        {
            const float m = 0.9f;
            var list = new List<float>();
            float pitch = StallLen + StallGap;
            if (aStreet && bStreet)
            {
                float w = b - a - 2f * m;
                int n = (int)math.floor((w + StallGap) / pitch);
                if (n <= 0) return list;
                float gap = n > 1 ? (w - n * StallLen) / (n - 1) : 0f;
                for (int k = 0; k < n; k++) list.Add(a + m + StallLen * 0.5f + k * (StallLen + gap));
            }
            else if (aStreet) for (float u = a + m + StallLen * 0.5f; u < b; u += pitch) list.Add(u);
            else if (bStreet) for (float u = b - m - StallLen * 0.5f; u > a; u -= pitch) list.Add(u);
            else
            {
                for (float u = pitch * 0.5f; u < b; u += pitch) if (u > a) list.Add(u);
                for (float u = -pitch * 0.5f; u > a; u -= pitch) if (u < b) list.Add(u);
            }
            return list;
        }

        /// <summary>The rectangle lies inside the site (by <paramref name="margin"/>) and clear of everything planned so far.</summary>
        private bool Fits(OrientedRect r, float margin)
        {
            var grown = new OrientedRect { Center = r.Center, Axis = r.Axis, HalfLength = r.HalfLength + margin, HalfWidth = r.HalfWidth + margin };
            var ring = grown.Ring;
            foreach (var q in ring) if (!_r.Contains(q)) return false;
            for (int i = 0; i < 4; i++) if (_r.CrossesBoundary(ring[i], ring[(i + 1) % 4], false)) return false;
            float pu = math.max(0f, r.HalfLength - 0.5f), pv = math.max(0f, r.HalfWidth - 0.5f);
            int nu = math.max(1, (int)math.ceil(2f * pu)), nv = math.max(1, (int)math.ceil(2f * pv));
            for (int i = 0; i <= nu; i++)
                for (int j = 0; j <= nv; j++)
                {
                    var p = r.Local(-pu + 2f * pu * i / nu, -pv + 2f * pv * j / nv);
                    if (_occ.Discs.Overlaps(p, 0.5f, 0.1f) || _occ.Corridors.Overlaps(p, 0.5f, 0.1f) || _occ.HitsPolygon(p, 0.5f)) return false;
                }
            return true;
        }

        private void Stall(float2 p, float2 facing)
        {
            uint v = Hashing.Mix(_ctx.Seed, 0x5700u + (uint)_stalls);
            var asset = PlazaArt.Or(_ctx, "stallAsset", PlazaArt.Stalls, v);
            _ctx.Object(p, math.atan2(facing.x, facing.y), ObjectRole.Furniture, asset, v, -1, AssetCategories.MarketStall);
            var along = new float2(facing.y, -facing.x);
            _occ.Take(p + along * 1.0f, 1.6f, 2);
            _occ.Take(p - along * 1.0f, 1.6f, 2);
            _stalls++;
        }

        /// <summary>Where the market street (or a gate lane) runs across the rows, single stalls line it, facing it.</summary>
        private void LineCurves()
        {
            if (!PlazaArt.Available(_ctx, "stallAsset", AssetCategories.MarketStall, AssetCategories.Kiosk)) return;
            float margin = _fw + 1.3f;
            foreach (var st in _streets)
            {
                if (st.AlongAxis.HasValue) continue;
                if (st.Rank == 3)
                {
                    var dd = st.Line[st.Line.Count - 1] - st.Line[0];
                    float al = math.abs(math.dot(math.normalizesafe(dd), _a));
                    if (al > 0.99f || al < 0.01f) continue;
                }
                var line = st.Line;
                float total = 0f; for (int i = 1; i < line.Count; i++) total += math.distance(line[i - 1], line[i]);
                foreach (int sg in new[] { -1, 1 })
                    for (float s = 2.6f; s <= total - 2.6f; s += StallLen + StallGap * 0.6f)
                    {
                        var (p, dir) = Along(line, s);
                        var side = new float2(-dir.y, dir.x) * sg;
                        var c = p + side * (st.Half + 0.7f + StallDepth * 0.5f);
                        var one = new OrientedRect { Center = c, Axis = dir, HalfLength = StallLen * 0.5f, HalfWidth = StallDepth * 0.5f };
                        if (Fits(one, margin)) Stall(c, -side);
                    }
            }
        }

        // ───────────── ground ─────────────

        private PaintKit Paint()
        {
            if (_kit != null) return _kit;
            _kit = new PaintKit(_ctx.Plan.Paint, _ctx.H, Hashing.Mix(_ctx.Seed, 0x9A17u)) { MaxPaints = 500 };
            TerrainPaint.Warn(_ctx);
            return _kit;
        }

        private void Ground()
        {
            var p = _pal;
            _pave.Axis = _a;
            // Bare soil first (pits and islands: holes in the paving, painted), or dark inlay pits and lawns without ground paint.
            if (_paint)
            {
                _pave.Hole(_pits);
                _pave.Hole(_islands);
            }
            else
            {
                _pave.Add(_pits, SurfaceRole.Plaza, p.Inlay, AssetCategories.SurfacePaving);
                _pave.Add(_islands, SurfaceRole.Grass, new AssetRef("", PavingPalettes.VGrass), AssetCategories.SurfaceGrass);
            }
            _pave.Add(_inlayPieces, SurfaceRole.Plaza, p.Inlay, AssetCategories.SurfacePaving);
            _pave.Add(_bandPieces, SurfaceRole.Plaza, p.Band, AssetCategories.SurfacePaving);
            _pave.Add(_focalPieces, SurfaceRole.Plaza, p.Focal, AssetCategories.SurfacePaving);
            _pave.Add(_accentPieces, SurfaceRole.Plaza, p.Accent, AssetCategories.SurfacePaving);
            // The darker frame along the outline, then the streets in the walk paving; the lighter market floor is the field.
            _pave.Add(PavingShapes.Frame(_r.Ring, _fw), SurfaceRole.Plaza, p.Band, AssetCategories.SurfacePaving);
            foreach (var st in _streets)
            {
                var strip = Strip(st.Line, st.Half);
                if (strip != null) _pave.Add(strip, SurfaceRole.Plaza, p.Path, AssetCategories.SurfacePaving);
            }
        }

        /// <summary>A band of half width <paramref name="half"/> along a polyline (one ring: left side out, right side back).</summary>
        private static float2[] Strip(List<float2> line, float half)
        {
            if (line.Count < 2) return null;
            if (line.Count == 2 || IsStraight(line)) return PavingShapes.Band(line[0], line[line.Count - 1], half * 2f);
            var left = new List<float2>(); var right = new List<float2>();
            for (int i = 0; i < line.Count; i++)
            {
                var d = i == 0 ? line[1] - line[0] : i == line.Count - 1 ? line[i] - line[i - 1] : line[i + 1] - line[i - 1];
                d = math.normalizesafe(d);
                var n = new float2(-d.y, d.x);
                left.Add(line[i] + n * half); right.Add(line[i] - n * half);
            }
            right.Reverse();
            left.AddRange(right);
            var ring = left.ToArray();
            return Poly.IsSimple(ring) ? Poly.EnsureCcw(ring) : null;
        }

        private static bool IsStraight(List<float2> line)
        {
            var a = line[0]; var b = line[line.Count - 1];
            foreach (var q in line) if (SegmentIndex.DistSq(q, a, b) > 0.01f) return false;
            return true;
        }

        private void Facts()
        {
            var f = _ctx.Plan.Facts;
            f["marketStreets"] = _streets.Count;
            f["foodCourts"] = _courts.Count;
            f["cafeGroves"] = _groves;
            f["plantingIslands"] = _plantings;
            f["streetTrees"] = _trees;
            f["foodCarts"] = _carts;
            f["bikeRacks"] = _racks;
            f["gateBollards"] = _bollards;
            f["marketLamps"] = _lamps;
            f["marketBenches"] = _benches;
            f["marketStreetCurved"] = _spineOnGrid ? 0 : 1;
            f["marketCross"] = 1;
        }
    }
}
