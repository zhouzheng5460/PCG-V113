// rf-shops3 — the market square's entrances, as in ParkingLotTool and Placemaker's parking type. The user: "I want to be able to
// set entrances and exits almost exactly like I can in the parking lot tool. And it wouldn't do anything unless I did."
//
// The player places them on the outline; nothing is suggested and Build waits for the first one. A market is for people, so
// any one entrance is enough (a single gate for people makes a market). The kinds:
//   access                      a gate for people: the walk in, a threshold pad, bollards across it, bike racks beside it;
//   entrance / entry / exit     the parking type's car kinds: a small car park at that edge of the market, planned with the
//                               parking planner and entered there (PLT's Zufahrt); its inner edge is a door into the market;
//   alley / alleyIn / alleyOut  the parking type's driveway kinds: a service lane for the stall vans, a small pedestrian street
//                               from a real junction on the street a short way into the market (vans may use pedestrian streets).
// Car and driveway kinds keep the parking type's ids, words and icons, so a player who knows the parking tool knows these.
// A lone one-way car kind (a way in with no way out next to it) makes a two-way car park: a market's car park is entered and
// left at the same edge. One-way driveways make the same two-way service lane (CS2's pedestrian streets are two-way).

using System;
using System.Collections.Generic;
using Placemaker.Core.Geometry;
using Placemaker.Core.Layers.Commercial;
using Placemaker.Core.Layers.Common;
using Unity.Mathematics;

namespace Placemaker.Core.Layers.Leisure
{
    public static class MarketEntrances
    {
        public const string Access = "access";
        public const string Entrance = "entrance", Entry = "entry", Exit = "exit", Alley = "alley", AlleyIn = "alleyIn", AlleyOut = "alleyOut";
        /// <summary>The parking type's footpath: read as a gate for people (not offered: the gate is the market's own word for it).</summary>
        public const string Footpath = "footpath";
        /// <summary>Marker kinds in display order (the first is what "Add an entrance" places).</summary>
        public static readonly string[] Kinds = { Access, Entrance, Entry, Exit, Alley, AlleyIn, AlleyOut };
        private const string K = "Placemaker.Market.";

        public static bool IsKind(string k) => k == Footpath || Array.IndexOf(Kinds, k) >= 0;
        public static bool ForPeople(string k) => k == Access || k == Footpath;
        public static bool ForCars(string k) => k == Entrance || k == Entry || k == Exit;
        public static bool ForService(string k) => k == Alley || k == AlleyIn || k == AlleyOut;

        /// <summary>The market's entrances control (essential, first in the entrances group): kinds with labels, tips and icons.</summary>
        public static ControlSchema Control(ControlSet c, string id)
        {
            var m = c.Markers(id, ControlTier.Essential, Kinds);
            m.Icon = "entrance";
            m.TooltipKey = K + id + ".Tooltip";
            foreach (var k in Kinds)
            {
                // The gate is the market's own; the car and driveway kinds keep the parking type's words (the tips say what they make here).
                string label = k == Access ? K + "Marker.Access" : "Placemaker.Parking.Marker." + Tool.MarkerKinds.Pascal(k);
                m.Options.Add(new ControlOption { Value = k, LabelKey = label, TooltipKey = K + "Marker." + Tool.MarkerKinds.Pascal(k) + ".Tip", Icon = Tool.MarkerKinds.IconFor(k) });
            }
            return m;
        }

        /// <summary>Ready once any entrance of the market's kinds is placed (one gate for people is a market); until then Build waits.</summary>
        public static Readiness Ready(AreaInput input)
        {
            int n = 0;
            if (input?.Markers != null) foreach (var m in input.Markers) if (IsKind(m.Kind)) n++;
            var args = new object[] { 1, n };
            if (n > 0) return new Readiness { Ready = true, ReasonArgs = args };
            return new Readiness { Ready = false, NextStepKey = K + "Step.PlaceEntrance", ReasonKey = K + "Reason.NeedsEntrance", ReasonArgs = args };
        }

        /// <summary>One placed entrance, snapped onto the outline, with the street it faces (when one is within reach).</summary>
        public sealed class Gate
        {
            public string Kind;
            public int Index;
            /// <summary>On the outline, and the inward normal there.</summary>
            public float2 At, In;
            public int Segment;
            public float T;
            public bool HasStreet;
            public long StreetId = -1;
            public float StreetT;
            public float2 StreetPoint;
            /// <summary>What the layout made of it ("gate", "lot", "lane", "none").</summary>
            public string Landed = "none";
            public bool People => ForPeople(Kind);
            public bool Cars => ForCars(Kind);
            public bool Service => ForService(Kind);
        }

        /// <summary>The player's entrances of the market's kinds, snapped onto the outline (marker order). Never suggested.</summary>
        public static List<Gate> Resolve(AreaInput input, LayerRegion region)
        {
            var list = new List<Gate>();
            if (region == null || input?.Markers == null) return list;
            int i = 0;
            foreach (var m in input.Markers)
            {
                if (!IsKind(m.Kind)) continue;
                var p = region.ClosestOnBoundary(m.Position.xz, out int seg, out float t);
                var inward = region.InwardNormal(seg);
                var g = new Gate { Kind = m.Kind == Footpath ? Access : m.Kind, Index = i++, At = p, In = inward, Segment = seg, T = t };
                if (StreetShops.StreetEnd(input, p, -inward, out var hit, out long id, out float st))
                {
                    g.HasStreet = true; g.StreetId = id; g.StreetT = st; g.StreetPoint = hit;
                }
                list.Add(g);
            }
            return list;
        }

        // ───────────── car parks ─────────────

        /// <summary>A car park the market made for one or more car entrances: its land, the door into the market and its stalls.</summary>
        public sealed class CarPark
        {
            public float2[] Ring;
            /// <summary>Middle of its inner edge (where people walk from the cars into the market) and the direction into the market.</summary>
            public float2 Door, Inward;
            public int Capacity;
        }

        /// <summary>
        /// One car park per group of nearby car entrances (on the same side, within 70 m): a lot one or two aisles deep along that
        /// edge of the site, planned by the parking planner (Mall.PlanLot) and entered at the player's markers. Groups that cannot be
        /// planned (no street within reach, no room) stay unbuilt with an Info issue.
        /// </summary>
        public static List<CarPark> CarParks(LayerContext ctx, Mall mall, LayerRegion region, List<Gate> gates)
        {
            var result = new List<CarPark>();
            var cars = new List<Gate>();
            foreach (var g in gates) if (g.Cars) cars.Add(g);
            if (cars.Count == 0) return result;
            cars.Sort((x, y) => region.AlongOf(x.Segment, x.T).CompareTo(region.AlongOf(y.Segment, y.T)));
            var groups = new List<List<Gate>>();
            foreach (var g in cars)
            {
                var last = groups.Count > 0 ? groups[groups.Count - 1] : null;
                var prev = last?[last.Count - 1];
                if (prev != null && math.distance(prev.At, g.At) < 70f && math.dot(prev.In, g.In) > 0.8f) last.Add(g);
                else groups.Add(new List<Gate> { g });
            }
            // The first and the last group may be one group across the ring's start.
            if (groups.Count > 1)
            {
                var first = groups[0]; var end = groups[groups.Count - 1];
                if (math.distance(first[0].At, end[end.Count - 1].At) < 70f && math.dot(first[0].In, end[end.Count - 1].In) > 0.8f)
                {
                    end.AddRange(first);
                    groups.RemoveAt(0);
                }
            }
            var bag = ParkingBag();
            int salt = 0;
            foreach (var group in groups)
            {
                var n = float2.zero; var c = float2.zero;
                foreach (var g in group) { n += g.In; c += g.At; }
                n = math.normalizesafe(n, group[0].In); c /= group.Count;
                var t = new float2(-n.y, n.x);
                float s0 = float.MaxValue, s1 = float.MinValue;
                foreach (var g in group) { float s = math.dot(g.At - c, t); s0 = math.min(s0, s); s1 = math.max(s1, s); }
                // Two-way, or a way in and a way out side by side; else the group is made two-way (a market's car park is left where it is entered).
                bool two = false, inbound = false, outbound = false;
                foreach (var g in group) { if (g.Kind == Entrance) two = true; else if (g.Kind == Entry) inbound = true; else outbound = true; }
                bool keepKinds = two || (inbound && outbound);
                var entrances = new List<(float2, string)>();
                foreach (var g in group) entrances.Add((g.At, keepKinds ? g.Kind : Entrance));
                float wMin = region.Area < 20000f ? 36f : 52f;
                float width = math.min(96f, math.max(wMin, s1 - s0 + 26f));
                int aisles = region.Area < 30000f ? 1 : 2;
                CarPark made = null;
                for (int a = aisles; a >= 1 && made == null; a--)
                    for (float w = width; w >= 28f && made == null; w -= 8f)
                    {
                        float depth = Mall.LotDepth(a);
                        float mid = 0.5f * (s0 + s1);
                        var rect = Poly.EnsureCcw(new[]
                        {
                            c + t * (mid - w * 0.5f) - n * 2f, c + t * (mid + w * 0.5f) - n * 2f,
                            c + t * (mid + w * 0.5f) + n * depth, c + t * (mid - w * 0.5f) + n * depth,
                        });
                        // The market must keep most of the site.
                        if (Poly.Area(rect) > region.Area * 0.3f) continue;
                        var lot = mall.PlanLot(rect, entrances, bag, Hashing.Mix(0xCA7u, (uint)salt));
                        if (lot == null || !mall.CommitLot(lot)) continue;
                        made = new CarPark { Ring = lot.Ring, Door = c + t * mid + n * depth, Inward = n, Capacity = (int)lot.Capacity };
                    }
                salt++;
                foreach (var g in group) g.Landed = made != null ? "lot" : "none";
                if (made != null) result.Add(made);
                else ctx.Issue(IssueSeverity.Info, K + "Issue.NoCarPark");
            }
            return result;
        }

        /// <summary>The market's car parks: planted edges, lamps, no perimeter road of their own (the market is beside them).</summary>
        private static SettingsBag ParkingBag()
        {
            var b = new SettingsBag();
            b.Values["greenery"] = "edges";
            b.Values["lanterns"] = true;
            b.Values["perimeterRoad"] = false;
            b.Values["setback"] = 1.0;
            b.Values["evShare"] = 0.0;
            return b;
        }

        // ───────────── service lanes ─────────────

        /// <summary>A service lane for the stall vans: its land (carved from the market) and the loading point at its end.</summary>
        public sealed class ServiceLane
        {
            public float2[] Ring;
            public float2 Mouth, End, Inward;
        }

        /// <summary>The service lane: a small pedestrian street (vans may drive on it), else the alley.</summary>
        public static readonly AssetRef LaneAsset = new AssetRef("Pedestrian Street Small", "Alley", "Pedestrian Street");
        public const float LaneHalf = 4f;

        /// <summary>
        /// A service lane per driveway marker: from a real junction on the street the marker faces, straight in a short way (up to
        /// 26 m, a third of the depth there), ending at a loading point among the stalls. A marker with no street in front of it
        /// makes nothing (Info issue).
        /// </summary>
        public static List<ServiceLane> ServiceLanes(LayerContext ctx, LayerRegion region, List<Gate> gates, IReadOnlyList<float2[]> taken)
        {
            var result = new List<ServiceLane>();
            foreach (var g in gates)
            {
                if (!g.Service) continue;
                if (!g.HasStreet) { g.Landed = "none"; ctx.Issue(IssueSeverity.Info, K + "Issue.NoStreet"); continue; }
                // How far the site reaches inward here.
                float reach = 0f;
                for (float d = 2f; d < 400f; d += 2f) { if (!region.Contains(g.At + g.In * d)) break; reach = d; }
                float len = math.clamp(reach * 0.33f, 12f, 26f);
                if (reach < 24f) { g.Landed = "none"; ctx.Issue(IssueSeverity.Info, K + "Issue.NoStreet"); continue; }
                var end = g.At + g.In * len;
                var side = new float2(-g.In.y, g.In.x);
                var ring = Poly.EnsureCcw(new[]
                {
                    g.At - g.In * 2f - side * (LaneHalf + 0.4f), g.At - g.In * 2f + side * (LaneHalf + 0.4f),
                    end + g.In * 0.6f + side * (LaneHalf + 0.4f), end + g.In * 0.6f - side * (LaneHalf + 0.4f),
                });
                bool clash = false;
                foreach (var t in taken) if (StreetShops.ConvexOverlap(t, ring, 0.05f)) { clash = true; break; }
                foreach (var l in result) if (StreetShops.ConvexOverlap(l.Ring, ring, 0.05f)) { clash = true; break; }
                if (clash) { g.Landed = "none"; ctx.Issue(IssueSeverity.Info, K + "Issue.NoStreet"); continue; }
                var net = ctx.StraightNet(g.StreetPoint, end, NetRole.Alley, LaneAsset);
                net.StartAttach = g.StreetId;
                net.StartAttachT = g.StreetT;
                // It runs through the market: no zone cells either side of it (vanilla ZonesDisabled, as LaneSides gives our lanes).
                net.Upgrades = new[] { Build.LaneSides.LeftOff, Build.LaneSides.RightOff };
                g.Landed = "lane";
                result.Add(new ServiceLane { Ring = ring, Mouth = g.At, End = end, Inward = g.In });
            }
            return result;
        }

        // ───────────── what is left for the market ─────────────

        /// <summary>The site minus the car parks and service lanes (the largest piece), or the site itself when nothing was taken.</summary>
        public static LayerRegion Rest(LayerRegion region, List<float2[]> taken)
        {
            if (taken == null || taken.Count == 0) return region;
            var clip = new List<IReadOnlyList<float2>>();
            foreach (var t in taken) clip.Add(Poly.EnsureCcw(t));
            try
            {
                var pieces = Poly.Boolean(new[] { (IReadOnlyList<float2>)region.Ring }, clip, BoolOp.Difference);
                float2[] best = null; float area = 0f;
                foreach (var p in pieces)
                {
                    if (p?.Outer == null || p.Outer.Length < 3) continue;
                    float a = Poly.Area(p.Outer);
                    if (a > area) { area = a; best = p.Outer; }
                }
                var rest = best == null ? null : LayerRegion.From(Poly.EnsureCcw(Poly.Clean(best, 0.05f)));
                return rest ?? region;
            }
            catch (Exception) { return region; }
        }
    }
}
