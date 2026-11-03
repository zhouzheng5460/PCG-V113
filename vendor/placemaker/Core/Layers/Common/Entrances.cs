// Entrance / access-point suggestion and road attachment shared by park, plaza and campground.
//
// ParkManager made the player place every entrance before anything happened. Here the layer
// suggests entrances where the outline meets roads (block-pick attachments or nearby nets),
// so a fresh selection is immediately buildable; markers the player places always win.

using System;
using System.Collections.Generic;
using Unity.Mathematics;

namespace Placemaker.Core.Layers.Common
{
    public struct Gate
    {
        public float2 Position;
        public int Segment;
        public float T;
        /// <summary>Existing net to connect to (BoundaryAttachment/NearbyNet ExternalId), −1 if none.</summary>
        public long AttachId;
        public float AttachT;
        public bool Suggested;
        /// <summary>Entrance kind (EntranceKinds: pedestrian, bike, main, service; legacy "entrance" = pedestrian).</summary>
        public string Kind;
    }

    /// <summary>
    /// Park-family entrance kinds (playtest 4b #6). The player picks them as markers; suggestions pick
    /// them from the net the outline faces (Entrances.FrontageAt):
    ///   pedestrian  footpath entrance (default; pathways and ordinary streets)
    ///   bike        bike + pedestrian entrance: the bike route connects these (bike paths, roads with bike lanes)
    ///   main        grand entrance: wide path into the network, gateway props (big roads)
    ///   service     maintenance access: a short service track (quiet streets, larger parks only)
    /// </summary>
    public static class EntranceKinds
    {
        public const string Pedestrian = "pedestrian", Bike = "bike", Main = "main", Service = "service";
        /// <summary>The kind every park entrance had before typed entrances; read as pedestrian.</summary>
        public const string Legacy = "entrance";
        /// <summary>Park: all four kinds (display order).</summary>
        public static readonly string[] Park = { Pedestrian, Bike, Main, Service };
        /// <summary>Leisure types and campgrounds: no service track planner, so no service kind.</summary>
        public static readonly string[] Leisure = { Pedestrian, Bike, Main };

        /// <summary>Road width (m, composition) from which a road counts as big (medium 4-lane and up).</summary>
        public const float BigRoadWidth = 18f;
        /// <summary>Two-way streets up to this width (m) are quiet enough for a service entrance.</summary>
        public const float QuietRoadWidth = 14f;
        /// <summary>Smallest park (m²) that gets a suggested service entrance.</summary>
        public const float ServiceMinArea = 8000f;
        /// <summary>From this size (m²) a park gets a main entrance on its strongest frontage even without a big road.</summary>
        public const float MainMinArea = 40000f;

        public static string Normalize(string kind) => string.IsNullOrEmpty(kind) || kind == Legacy ? Pedestrian : kind;

        /// <summary>Kinds people walk in through (everything but the service track).</summary>
        public static bool Walks(string kind) => Normalize(kind) != Service;

        /// <summary>Marker options (label, tooltip, vanilla icon) for a Markers control listing <paramref name="kinds"/>.</summary>
        public static ControlSchema WithOptions(ControlSchema c, string[] kinds)
        {
            foreach (var k in kinds)
            {
                string key = "Placemaker.Entrance." + char.ToUpperInvariant(k[0]) + k.Substring(1);
                c.Options.Add(new ControlOption { Value = k, LabelKey = key, TooltipKey = key + ".Tip", Icon = Icon(k) });
            }
            return c;
        }

        /// <summary>Vanilla icons (verified under Cities2_Data/Content/Game/UI/Media/Game/Icons, game 1.6.2f1).</summary>
        public static string Icon(string kind)
        {
            switch (Normalize(kind))
            {
                case Bike: return "Media/Game/Icons/Bicycle.svg";
                case Main: return "Media/Game/Icons/CityPark.svg";
                case Service: return "Media/Game/Icons/ParkMaintenance.svg";
                default: return "Media/Game/Icons/Pathways.svg";
            }
        }
    }

    /// <summary>What an outline point faces: the classified net beside it.</summary>
    public enum Frontage { None = 0, Pathway = 1, Bike = 2, Street = 3, QuietStreet = 4, BigRoad = 5 }

    public static class Entrances
    {
        /// <summary>Max distance from the outline to a road edge for it to count as frontage.</summary>
        public const float FrontageReach = 6f;

        /// <summary>Player markers of <paramref name="kind"/>, else suggestions; never empty for a valid region.</summary>
        public static List<Gate> Resolve(AreaInput input, LayerRegion region, string kind, int wanted)
        {
            var gates = new List<Gate>();
            foreach (var m in input.Markers)
            {
                if (m.Kind != kind) continue;
                var p = region.ClosestOnBoundary(m.Position.xz, out int seg, out float t);
                gates.Add(new Gate { Position = p, Segment = seg, T = t, AttachId = -1, Kind = kind });
            }
            if (gates.Count == 0)
                foreach (var m in Suggest(input, region, kind, wanted))
                    gates.Add(new Gate { Position = m.Position.xz, Segment = m.RingSegment, T = m.RingT, AttachId = -1, Suggested = true, Kind = kind });
            for (int i = 0; i < gates.Count; i++)
            {
                var g = gates[i];
                Attach(input, ref g);
                gates[i] = g;
            }
            return gates;
        }

        /// <summary>Suggested count by area: pocket parks one entrance, large parks up to five.</summary>
        public static int WantedByArea(float area)
        {
            if (area < 2500f) return 1;
            if (area < 15000f) return 2;
            if (area < 40000f) return 3;
            if (area < 100000f) return 4;
            return 5;
        }

        /// <summary>Turns sharper than this (degrees) make an outline vertex a corner entrances keep away from.</summary>
        public const float CornerAngle = 25f;

        /// <summary>How far suggestions keep from corners and road intersections: 18 m, less on small outlines.</summary>
        public static float CornerClear(LayerRegion region) => math.clamp(region.Perimeter / 16f, 4f, 18f);

        /// <summary>
        /// Picks up to <paramref name="wanted"/> outline points facing roads/paths: road nodes/crossings on
        /// the outline, ends of paths that reach it (desire lines) and the middles of frontage runs, then
        /// (playtest 4b) slides every candidate at least CornerClear away from outline corners and road
        /// intersections. Farthest-point selection spreads them; without frontage, the longest edge's middle
        /// and points far from it.
        /// </summary>
        public static List<EdgeMarker> Suggest(AreaInput input, LayerRegion region, string kind, int wanted)
        {
            var result = new List<EdgeMarker>();
            if (region == null || wanted <= 0) return result;
            var candidates = new List<float2>();
            var weights = new List<float>();
            float clear = CornerClear(region);
            var corners = Corners(region);
            var crossings = Intersections(input);
            bool AwayFromCorners(float2 p)
            {
                foreach (var c in corners) if (math.distancesq(c, p) < clear * clear) return false;
                foreach (var c in crossings) if (math.distancesq(c, p) < clear * clear) return false;
                return true;
            }

            // Where a road meets the outline at a node or crossing (nudged off corners below).
            foreach (var a in input.Shape.Attachments)
                if (a.Kind == AttachmentKind.RoadCrossing || a.Kind == AttachmentKind.RoadNode)
                {
                    candidates.Add(region.ClosestOnBoundary(a.Position.xz, out _, out _));
                    weights.Add(2f);
                }
            // A path that ends at the outline is where people already walk in.
            foreach (var n in input.Site?.NearbyNets ?? new List<NearbyNet>())
            {
                if (n.Category != NetCategory.Pathway) continue;
                foreach (var end in new[] { n.A.xz, n.D.xz })
                {
                    if (region.BoundaryDistance(end, FrontageReach + 1f) > FrontageReach) continue;
                    candidates.Add(region.ClosestOnBoundary(end, out _, out _));
                    weights.Add(2.5f);
                }
            }

            // Frontage runs: outline samples within reach of a road (attachment along edge or nearby net).
            var roads = RoadSegments(input);
            float step = math.clamp(region.Perimeter / 400f, 2f, 8f);
            int samples = math.max(8, (int)(region.Perimeter / step));
            var front = new bool[samples];
            for (int i = 0; i < samples; i++)
            {
                var p = region.PointAt(region.Perimeter * i / samples, out _, out _, out _);
                front[i] = roads.Clearance(p, FrontageReach + 0.01f) <= FrontageReach;
            }
            bool all = true; foreach (var f in front) all &= f;
            if (all)
            {
                // Fully surrounded: candidates at the middle of every long edge.
                for (int e = 0; e < region.Ring.Length; e++)
                {
                    var a = region.Ring[e]; var b = region.Ring[(e + 1) % region.Ring.Length];
                    if (math.distance(a, b) < 12f) continue;
                    candidates.Add(math.lerp(a, b, 0.5f)); weights.Add(1f + math.distance(a, b) / 200f);
                }
            }
            else
            {
                int startIdx = 0;
                while (startIdx < samples && front[startIdx]) startIdx++;   // begin at a non-front sample
                int run = -1;
                for (int k = 1; k <= samples; k++)
                {
                    int i = (startIdx + k) % samples;
                    if (front[i] && run < 0) run = k;
                    if ((!front[i] || k == samples) && run >= 0)
                    {
                        int len = k - run;
                        float length = len * region.Perimeter / samples;
                        if (length >= 6f)
                        {
                            int pieces = length > 150f ? 3 : length > 70f ? 2 : 1;
                            for (int q = 0; q < pieces; q++)
                            {
                                float along = (startIdx + run + len * (q + 0.5f) / pieces) * region.Perimeter / samples;
                                candidates.Add(region.PointAt(along, out _, out _, out _));
                                weights.Add(1f + length / 300f);
                            }
                        }
                        run = -1;
                    }
                }
            }

            // Playtest 4b: never in the corners. Frontage candidates closer than CornerClear to an outline
            // corner or a road intersection slide along the outline until clear (and still facing the road),
            // or are dropped.
            bool roadside = candidates.Count > 0;
            for (int i = candidates.Count - 1; i >= 0; i--)
            {
                if (AwayFromCorners(candidates[i])) continue;
                region.ClosestOnBoundary(candidates[i], out int seg, out float t);
                float along = region.AlongOf(seg, t);
                bool moved = false;
                for (float d = 2f; d <= clear * 2.5f && !moved; d += 2f)
                    foreach (float sgn in new[] { 1f, -1f })
                    {
                        var p = region.PointAt(along + sgn * d, out _, out _, out _);
                        if (!AwayFromCorners(p) || roads.Clearance(p, FrontageReach + 0.01f) > FrontageReach) continue;
                        candidates[i] = p; moved = true; break;
                    }
                if (!moved) { candidates.RemoveAt(i); weights.RemoveAt(i); }
            }

            if (candidates.Count == 0)
            {
                // No road anywhere (or only at corners): midpoint of the longest edge, then farthest points from it.
                int longest = 0; float len = 0;
                for (int e = 0; e < region.Ring.Length; e++)
                {
                    float l = math.distance(region.Ring[e], region.Ring[(e + 1) % region.Ring.Length]);
                    if (l > len) { len = l; longest = e; }
                }
                candidates.Add(math.lerp(region.Ring[longest], region.Ring[(longest + 1) % region.Ring.Length], 0.5f));
                weights.Add(1f);
                int extra = math.max(0, wanted - 1);
                for (int i = 0; i < 16 && extra > 0; i++)
                {
                    var p = region.PointAt(region.Perimeter * (i + 0.5f) / 16f, out _, out _, out _);
                    // Off-road outlines (tests, open land) keep their old spread; with roads about, still no corners.
                    if (roadside && !AwayFromCorners(p)) continue;
                    candidates.Add(p);
                    weights.Add(0.5f);
                }
            }

            // Farthest-point selection weighted by candidate strength; first = strongest.
            var chosen = new List<int>();
            int first = 0;
            for (int i = 1; i < candidates.Count; i++) if (weights[i] > weights[first]) first = i;
            chosen.Add(first);
            float minSpacing = math.max(20f, region.Perimeter / (wanted * 3f));
            while (chosen.Count < wanted)
            {
                int best = -1; float bestScore = 0;
                for (int i = 0; i < candidates.Count; i++)
                {
                    if (chosen.Contains(i)) continue;
                    float d = float.MaxValue;
                    foreach (int c in chosen) d = math.min(d, math.distance(candidates[i], candidates[c]));
                    if (d < minSpacing) continue;
                    float score = d * weights[i];
                    if (score > bestScore) { bestScore = score; best = i; }
                }
                if (best < 0) break;
                chosen.Add(best);
            }

            foreach (int c in chosen)
            {
                var p = region.ClosestOnBoundary(candidates[c], out int seg, out float t);
                float y = input.Site?.HeightAt != null ? input.Site.HeightAt(p) : 0f;
                result.Add(new EdgeMarker { Kind = kind, RingSegment = seg, RingT = t, Position = new float3(p.x, y, p.y) });
            }
            return result;
        }

        /// <summary>Outline vertices that turn by more than CornerAngle.</summary>
        public static List<float2> Corners(LayerRegion region)
        {
            var list = new List<float2>();
            var r = region.Ring;
            float cos = math.cos(math.radians(CornerAngle));
            for (int i = 0; i < r.Length; i++)
            {
                var a = math.normalizesafe(r[i] - r[(i + r.Length - 1) % r.Length]);
                var b = math.normalizesafe(r[(i + 1) % r.Length] - r[i]);
                if (math.dot(a, b) < cos) list.Add(r[i]);
            }
            return list;
        }

        /// <summary>Road intersections near the area: node points where three or more net ends meet.</summary>
        public static List<float2> Intersections(AreaInput input)
        {
            var ends = new List<float2>();
            foreach (var n in input.Site?.NearbyNets ?? new List<NearbyNet>())
            {
                if (n.Category == NetCategory.Track) continue;
                ends.Add(n.A.xz); ends.Add(n.D.xz);
            }
            var result = new List<float2>();
            for (int i = 0; i < ends.Count; i++)
            {
                int count = 0;
                for (int j = 0; j < ends.Count; j++) if (math.distancesq(ends[i], ends[j]) < 4f) count++;
                if (count < 3) continue;
                bool dup = false;
                foreach (var q in result) if (math.distancesq(q, ends[i]) < 4f) { dup = true; break; }
                if (!dup) result.Add(ends[i]);
            }
            return result;
        }

        /// <summary>Which corner-to-corner side ring segment <paramref name="seg"/> belongs to.</summary>
        private static int SideIndex(LayerRegion region, List<float2> corners, int seg)
        {
            if (corners.Count == 0) return 0;
            int side = 0;
            for (int i = 1; i <= seg && i < region.Ring.Length; i++)
                if (corners.Contains(region.Ring[i])) side++;
            return side;
        }

        /// <summary>Park-family readiness: at least one entrance marker of <paramref name="kinds"/> (or legacy "entrance").</summary>
        public static Readiness RequireTyped(AreaInput input, string[] kinds)
        {
            if (input?.Markers != null)
                foreach (var m in input.Markers)
                    if (m.Kind == EntranceKinds.Legacy || Array.IndexOf(kinds, m.Kind) >= 0) return new Readiness { Ready = true };
            return new Readiness { Ready = false, NextStepKey = "Placemaker.Entrance.Step.Place", ReasonKey = "Placemaker.Entrance.Reason.Needs" };
        }

        // ───────────────────────── typed entrances (park family) ─────────────────────────

        /// <summary>
        /// Typed entrances: player markers of any of <paramref name="kinds"/> (legacy "entrance" counts as
        /// pedestrian), else typed suggestions (SuggestTyped). Gate.Kind is normalised.
        /// </summary>
        public static List<Gate> ResolveTyped(AreaInput input, LayerRegion region, int wanted, string[] kinds)
        {
            var gates = new List<Gate>();
            foreach (var m in input.Markers)
            {
                string k = EntranceKinds.Normalize(m.Kind);
                if (m.Kind != EntranceKinds.Legacy && Array.IndexOf(kinds, m.Kind) < 0) continue;
                if (Array.IndexOf(kinds, k) < 0) continue;
                var p = region.ClosestOnBoundary(m.Position.xz, out int seg, out float t);
                gates.Add(new Gate { Position = p, Segment = seg, T = t, AttachId = -1, Kind = k });
            }
            if (gates.Count == 0)
            {
                // s9: no entrance placed yet (Build is blocked until one is). The suggestions still shape the
                // layout (so placing them changes little), but never attach to a road; LayerBase trims the path
                // ends at the outline back inside (TrimUnplacedEntrances), so the paths stay internal.
                foreach (var m in SuggestTyped(input, region, wanted, kinds))
                    gates.Add(new Gate { Position = m.Position.xz, Segment = m.RingSegment, T = m.RingT, AttachId = -1, Suggested = true, Kind = m.Kind });
                return gates;
            }
            for (int i = 0; i < gates.Count; i++)
            {
                var g = gates[i];
                Attach(input, ref g);
                gates[i] = g;
            }
            return gates;
        }

        /// <summary>How far inside the outline paths stop while no entrance is placed (m).</summary>
        public static float UnplacedInset => math.max(Layout.M(8f, 5f), Layout.EdgeMargin + 4f);

        /// <summary>
        /// Suggested entrances with kinds picked from the net each one faces: bike paths and roads with bike
        /// lanes → bike; pathways and streets → pedestrian; the widest big road → main (a large park without
        /// one gets its main entrance on a busier, non-quiet street); plus one service entrance on a quiet street, far from the
        /// others, for larger parks. Only kinds in <paramref name="kinds"/> are used.
        /// </summary>
        public static List<EdgeMarker> SuggestTyped(AreaInput input, LayerRegion region, int wanted, string[] kinds)
        {
            var result = Suggest(input, region, EntranceKinds.Pedestrian, wanted);
            if (result.Count == 0) return result;
            // Bike entrances only when the bike route is on (playtest 4b).
            bool bike = Array.IndexOf(kinds, EntranceKinds.Bike) >= 0 && input.Settings != null && input.Settings.B("bikePath", false);
            bool main = Array.IndexOf(kinds, EntranceKinds.Main) >= 0;
            bool service = Array.IndexOf(kinds, EntranceKinds.Service) >= 0;
            var nets = FrontageIndex(input);
            var fronts = new Frontage[result.Count];
            int mainAt = -1; float mainWidth = 0f;
            for (int i = 0; i < result.Count; i++)
            {
                var m = result[i];
                fronts[i] = FrontageAt(input, nets, m.Position.xz, out float width);
                if (fronts[i] == Frontage.Bike && bike) m.Kind = EntranceKinds.Bike;
                else if (fronts[i] == Frontage.BigRoad && main && width > mainWidth) { mainAt = i; mainWidth = width; }
                result[i] = m;
            }
            if (main && mainAt < 0 && region.Area >= EntranceKinds.MainMinArea)
                for (int i = 0; i < result.Count && mainAt < 0; i++)
                    if (result[i].Kind == EntranceKinds.Pedestrian && fronts[i] == Frontage.Street) mainAt = i;   // not on a quiet street: thin paths stay the default
            if (mainAt >= 0 && region.Area >= 2500f)
            {
                var m = result[mainAt]; m.Kind = EntranceKinds.Main; result[mainAt] = m;
            }
            // Bike route on but fewer than two bike-friendly frontages: hint the two farthest-apart entrances as bike.
            int bikes = 0; foreach (var m in result) if (m.Kind == EntranceKinds.Bike) bikes++;
            if (bike && bikes < 2)
            {
                int a = -1, b = -1; float far = 40f;
                for (int i = 0; i < result.Count; i++)
                    for (int j = i + 1; j < result.Count; j++)
                    {
                        if (result[i].Kind == EntranceKinds.Main || result[j].Kind == EntranceKinds.Main) continue;
                        float d = math.distance(result[i].Position.xz, result[j].Position.xz);
                        if (d > far) { far = d; a = i; b = j; }
                    }
                if (a >= 0)
                {
                    var ma = result[a]; ma.Kind = EntranceKinds.Bike; result[a] = ma;
                    var mb = result[b]; mb.Kind = EntranceKinds.Bike; result[b] = mb;
                }
            }
            if (service && region.Area >= EntranceKinds.ServiceMinArea && ServicePoint(input, region, nets, result, out var sp))
                result.Add(sp);
            return result;
        }

        /// <summary>The outline point on a quiet street farthest from the other entrances (at least 30 m away), never in a corner.</summary>
        private static bool ServicePoint(AreaInput input, LayerRegion region, SegmentIndex nets, List<EdgeMarker> others, out EdgeMarker marker)
        {
            marker = default;
            int samples = math.clamp((int)(region.Perimeter / 6f), 16, 160);
            float best = 30f; bool found = false;
            float clear = CornerClear(region);
            var corners = Corners(region); corners.AddRange(Intersections(input));
            for (int i = 0; i < samples; i++)
            {
                var p = region.PointAt(region.Perimeter * (i + 0.5f) / samples, out _, out _, out _);
                float d = float.MaxValue;
                foreach (var o in others) d = math.min(d, math.distance(o.Position.xz, p));
                if (d <= best) continue;
                bool cornered = false;
                foreach (var c in corners) if (math.distancesq(c, p) < clear * clear) { cornered = true; break; }
                if (cornered || FrontageAt(input, nets, p, out _) != Frontage.QuietStreet) continue;
                best = d; found = true;
                var q = region.ClosestOnBoundary(p, out int seg, out float t);
                float y = input.Site?.HeightAt != null ? input.Site.HeightAt(q) : 0f;
                marker = new EdgeMarker { Kind = EntranceKinds.Service, RingSegment = seg, RingT = t, Position = new float3(q.x, y, q.y) };
            }
            return found;
        }

        /// <summary>Nets and attachments as segments; Kind = NearbyNet index, or −(attachment index + 1).</summary>
        public static SegmentIndex FrontageIndex(AreaInput input)
        {
            var idx = new SegmentIndex(12f);
            var nets = input.Site?.NearbyNets ?? new List<NearbyNet>();
            for (int n = 0; n < nets.Count; n++)
            {
                var net = nets[n];
                if (net.Category == NetCategory.Track) continue;
                float hw = math.max(1f, net.Width * 0.5f);
                float3 prev = net.A;
                for (int i = 1; i <= 8; i++)
                {
                    var p = Composer.Bezier(net.A, net.B, net.C, net.D, i / 8f);
                    idx.Add(prev.xz, p.xz, hw, n);
                    prev = p;
                }
            }
            var att = input.Shape.Attachments;
            for (int j = 0; j < att.Count; j++)
            {
                var a = att[j];
                float hw = math.max(1f, a.RoadWidth * 0.5f);
                var dir = math.normalizesafe(a.Direction.xz, new float2(1, 0));
                float reach = a.Kind == AttachmentKind.RoadAlongEdge ? 20f : 4f;
                idx.Add(a.Position.xz - dir * reach, a.Position.xz + dir * reach, hw, -(j + 1));
            }
            return idx;
        }

        /// <summary>The kind of net nearest to outline point <paramref name="p"/> (within FrontageReach of its edge).</summary>
        public static Frontage FrontageAt(AreaInput input, SegmentIndex index, float2 p, out float width)
        {
            width = 0f;
            int s = index.Nearest(p, FrontageReach + 0.01f, out _);
            if (s < 0) return Frontage.None;
            int code = index.Segments[s].Kind;
            if (code < 0)
            {
                width = input.Shape.Attachments[-code - 1].RoadWidth;
                return width >= EntranceKinds.BigRoadWidth ? Frontage.BigRoad : width > 0f && width <= EntranceKinds.QuietRoadWidth ? Frontage.QuietStreet : Frontage.Street;
            }
            var n = input.Site.NearbyNets[code];
            width = n.Width;
            if (n.HasBikeLane) return Frontage.Bike;
            if (n.Category == NetCategory.Pathway) return Frontage.Pathway;
            if (n.Width >= EntranceKinds.BigRoadWidth) return Frontage.BigRoad;
            if (n.Category == NetCategory.Road && n.Width <= EntranceKinds.QuietRoadWidth && !n.IsOneWay) return Frontage.QuietStreet;
            return Frontage.Street;
        }

        /// <summary>Road/path geometry near the area as segments (Béziers sampled), for frontage tests.</summary>
        public static SegmentIndex RoadSegments(AreaInput input)
        {
            var idx = new SegmentIndex(12f);
            foreach (var n in input.Site?.NearbyNets ?? new List<NearbyNet>())
            {
                if (n.Category == NetCategory.Track) continue;
                float hw = math.max(1f, n.Width * 0.5f);
                float3 prev = n.A;
                for (int i = 1; i <= 8; i++)
                {
                    var p = Composer.Bezier(n.A, n.B, n.C, n.D, i / 8f);
                    idx.Add(prev.xz, p.xz, hw, 0);
                    prev = p;
                }
            }
            foreach (var a in input.Shape.Attachments)
            {
                float hw = math.max(1f, a.RoadWidth * 0.5f);
                var dir = math.normalizesafe(a.Direction.xz, new float2(1, 0));
                float reach = a.Kind == AttachmentKind.RoadAlongEdge ? 20f : 4f;
                idx.Add(a.Position.xz - dir * reach, a.Position.xz + dir * reach, hw, 1);
            }
            return idx;
        }

        /// <summary>Links a gate to the closest attachment or nearby net so the game layer can connect the path end.</summary>
        public static void Attach(AreaInput input, ref Gate g)
        {
            float best = 3f;
            foreach (var a in input.Shape.Attachments)
            {
                float d = math.distance(a.Position.xz, g.Position);
                if (d < best) { best = d; g.AttachId = a.ExternalId; g.AttachT = a.ExternalT; }
            }
            if (g.AttachId >= 0) return;
            float bestNet = float.MaxValue;
            foreach (var n in input.Site?.NearbyNets ?? new List<NearbyNet>())
            {
                if (n.Category == NetCategory.Track) continue;
                float reach = n.Width * 0.5f + FrontageReach;
                // Sample the curve finely enough for a few-metre accuracy on typical 30–90 m edges.
                for (int i = 0; i <= 32; i++)
                {
                    float t = i / 32f;
                    var p = Composer.Bezier(n.A, n.B, n.C, n.D, t).xz;
                    float d = math.distance(p, g.Position);
                    if (d <= reach && d < bestNet) { bestNet = d; g.AttachId = n.ExternalId; g.AttachT = t; }
                }
            }
        }
    }
}
