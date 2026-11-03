// Placemaker roads layer (s52) — "Hillside terraces": the roads up and down the hill, and the bank walks.
//
// Links join a terrace street to the next one up (the first street a ray up the slope meets). Every link has a level
// landing where it meets a street (junctions on terraces are level) and climbs at most just under the steepest-road
// setting in between:
//   direct      straight up the slope when that is gentle enough; otherwise a diagonal that leaves the street at 70°,
//               runs across the slope and arrives at 70° further along the next street — as far along as the climb needs.
//   serpentine  switchbacks: a landing straight up, a quarter turn, legs along the slope joined by hairpins (centre-line
//               radius ≥ TerraceOptions.HairpinRadius, climbing at half the grade in the turn), a quarter turn and a
//               landing straight up into the next street. As many legs as fit between the two streets.
//   spiral      long diagonals that always turn the same way round the hill, each starting where the last one arrived
//               (a crossroads on every terrace): one road winding up the whole hill.
// Bank walks (the walkways amount): a level path across the zone cells to the bank foot, one ramp along the bank face at
// ≤ 7.5 % following the street's curve (a zig-zag with landings only where its legs part enough), a level path across the
// next terrace's cells to its street; where the step is too deep for that, a straight path across the terraces at ≤ 7.5 %.
//
// Every route is checked: inside the area, clear of buildings, never crossing a street or another route, clear of the
// streets between its ends, arriving at a sensible angle. Pure, deterministic.

using System;
using System.Collections.Generic;
using System.Threading;
using Placemaker.Core.Geometry;
using Unity.Mathematics;

namespace Placemaker.Core.Layers.Roads.Terraces
{
    internal sealed class TerraceRoute
    {
        public readonly List<float2> P = new List<float2>();
        public readonly List<float> H = new List<float>();
        /// <summary>Climb weight of the segment ending at point i (0 = level landing, 0.5 = turn, 1 = ramp); W[0] unused.</summary>
        public readonly List<float> W = new List<float>();
        /// <summary>The planned edge must break at point i (a profile kink, a turn's end).</summary>
        public readonly List<bool> Break = new List<bool>();
        public int From = -1, To = -1;
        public float FromS, ToS;
        public bool Walk, Trail;
        public string Kind = "";
        public int Hairpins;
        /// <summary>Tightest centre-line radius of the route's turns (m).</summary>
        public float MinRadius = float.PositiveInfinity;
        /// <summary>Steepest designed grade (rise/run).</summary>
        public float Grade;
        /// <summary>Spiral: the side (along the from-street's direction) the route ran to.</summary>
        public int Side;

        public float Length => Lines.Length(P);

        public void Start(float2 p) { P.Clear(); W.Clear(); Break.Clear(); H.Clear(); P.Add(p); W.Add(0f); Break.Add(true); }

        public void Line(float2 b, float weight, float step = 4f)
        {
            float2 a = P[P.Count - 1];
            float len = math.distance(a, b);
            if (len < 0.05f) return;
            Break[Break.Count - 1] = true;
            int n = math.max(1, (int)math.ceil(len / step));
            for (int i = 1; i <= n; i++) { P.Add(math.lerp(a, b, i / (float)n)); W.Add(weight); Break.Add(false); }
            Break[Break.Count - 1] = true;
        }

        /// <summary>Arc round centre c, radius r, from angle a0 to a1 (radians; the sign of a1 − a0 is the turn direction).</summary>
        public void Arc(float2 c, float r, float a0, float a1, float weight)
        {
            Break[Break.Count - 1] = true;
            float span = a1 - a0;
            int n = math.max(2, (int)math.ceil(math.abs(span) * r / 3f));
            for (int i = 1; i <= n; i++)
            {
                float a = a0 + span * i / n;
                P.Add(c + new float2(math.cos(a), math.sin(a)) * r);
                W.Add(weight);
                // Break every 45° so each planned edge is a short, accurate arc.
                Break.Add(i == n || (i * 4 % n == 0 && math.abs(span) > 1.6f));
            }
            Break[Break.Count - 1] = true;
            MinRadius = math.min(MinRadius, r);
        }

        /// <summary>Corner-rounding polyline through few vertices: every inner corner becomes an arc of radius ≤ r (legs permitting).</summary>
        public void Rounded(IReadOnlyList<float2> v, float r, float weight)
        {
            Start(v[0]);
            float2 cur = v[0];
            for (int i = 1; i < v.Count - 1; i++)
            {
                float2 a = v[i - 1], b = v[i], c = v[i + 1];
                float2 u = math.normalizesafe(b - a), w = math.normalizesafe(c - b);
                float turn = math.acos(math.clamp(math.dot(u, w), -1f, 1f));
                if (turn < 0.02f) continue;
                float tl = r * math.tan(turn * 0.5f);
                float room = 0.48f * math.min(math.distance(a, b), math.distance(b, c));
                float rr = r;
                if (tl > room) { tl = room; rr = tl / math.tan(turn * 0.5f); }
                float2 p0 = b - u * tl, p1 = b + w * tl;
                Line(p0, weight);
                float cross = u.x * w.y - u.y * w.x;
                float2 nrm = cross > 0f ? new float2(-u.y, u.x) : new float2(u.y, -u.x);
                float2 centre = p0 + nrm * rr;
                float a0 = math.atan2(p0.y - centre.y, p0.x - centre.x);
                float a1 = math.atan2(p1.y - centre.y, p1.x - centre.x);
                float span = a1 - a0;
                if (cross > 0f) { while (span < 0f) span += 2f * math.PI; }
                else { while (span > 0f) span -= 2f * math.PI; }
                Arc(centre, rr, a0, a0 + span, weight);
                cur = p1;
            }
            Line(v[v.Count - 1], weight);
        }

        /// <summary>
        /// Level landings at both ends (junctions on terraces are level), then heights h0 → h1 spread by climb weight.
        /// Returns the steepest grade.
        /// </summary>
        public float Profile(float h0, float h1, float landing)
        {
            InsertAt(landing);
            InsertAt(Length - landing);
            var s = Lines.Stations(P);
            float total = s[s.Length - 1];
            for (int i = 1; i < P.Count; i++)
            {
                float mid = (s[i - 1] + s[i]) * 0.5f;
                if (mid < landing || mid > total - landing) W[i] = 0f;
            }
            for (int i = 1; i + 1 < P.Count; i++) if (W[i] != W[i + 1]) Break[i] = true;
            float tw = 0f;
            for (int i = 1; i < P.Count; i++) tw += W[i] * (s[i] - s[i - 1]);
            H.Clear();
            float acc = 0f;
            H.Add(h0);
            for (int i = 1; i < P.Count; i++)
            {
                acc += W[i] * (s[i] - s[i - 1]);
                H.Add(tw > 1e-3f ? math.lerp(h0, h1, acc / tw) : math.lerp(h0, h1, s[i] / math.max(1e-3f, total)));
            }
            Grade = tw > 1e-3f ? math.abs(h1 - h0) / tw : 0f;
            return Grade;
        }

        /// <summary>Inserts a vertex at station s (no-op near an existing one).</summary>
        public void InsertAt(float s)
        {
            if (s <= 0.3f) return;
            float acc = 0f;
            for (int i = 1; i < P.Count; i++)
            {
                float len = math.distance(P[i - 1], P[i]);
                if (acc + len > s)
                {
                    float t = (s - acc) / math.max(1e-6f, len);
                    if (t * len < 0.3f || (1f - t) * len < 0.3f) { Break[t < 0.5f ? i - 1 : i] = true; return; }
                    P.Insert(i, math.lerp(P[i - 1], P[i], t));
                    W.Insert(i, W[i]);
                    Break.Insert(i, true);
                    return;
                }
                acc += len;
            }
        }
    }

    internal static class TerraceRoutes
    {
        private sealed class Ctx
        {
            public TerraceLayout Lay;
            public TerraceOptions O;
            public RoadObstacles Obstacles;
            public SegGrid Streets, Routes;
            public List<int> Scratch = new List<int>();
            public float MaxRay;
        }

        private struct Hit
        {
            public int Street;
            public float Dist, Station;
            public float2 At;
        }

        /// <summary>Plans the links between terraces, then the bank walks.</summary>
        public static void Plan(TerraceLayout lay, TerraceOptions o, RoadObstacles obstacles, CancellationToken cancel)
        {
            if (lay.Streets.Count == 0) return;
            var c = new Ctx { Lay = lay, O = o, Obstacles = obstacles, Streets = new SegGrid(20f), Routes = new SegGrid(20f) };
            for (int s = 0; s < lay.Streets.Count; s++) c.Streets.AddLine(lay.Streets[s].P, lay.Streets[s].Closed, s);
            c.MaxRay = math.max(3f * lay.Spacing, 4f * o.Band);
            Links(c, cancel);
            Rescue(c, cancel);
            if (o.Walks > 0.01f && lay.Sloped) Walks(c, cancel);
        }

        // ───────────────────────── links ─────────────────────────

        private static void Links(Ctx c, CancellationToken cancel)
        {
            var lay = c.Lay; var o = c.O;
            var order = new List<int>();
            for (int i = 0; i < lay.Streets.Count; i++) order.Add(i);
            order.Sort((a, b) => lay.Streets[a].Level != lay.Streets[b].Level ? lay.Streets[a].Level.CompareTo(lay.Streets[b].Level)
                : lay.Streets[b].Length.CompareTo(lay.Streets[a].Length));
            // Spiral: where a link arrived, the next one leaves (same side).
            var continueAt = new Dictionary<int, List<(float s, int side)>>();
            foreach (int i in order)
            {
                cancel.ThrowIfCancellationRequested();
                var st = lay.Streets[i];
                if (o.Links == TerraceLinkStyle.Spiral && continueAt.TryGetValue(i, out var cont))
                    foreach (var (s0, side) in cont)
                    {
                        var r = TryLink(c, i, s0, side, true);
                        if (r != null) Accept(c, r, continueAt);
                    }
                foreach (var (nb, s0, s1) in Runs(c, i))
                {
                    float len = s1 - s0;
                    int linked = 0;
                    foreach (var r in c.Lay.Links) if (r.From == i && r.To == nb) linked++;
                    int want = math.max(1, (int)math.floor(len / o.LinkSpacing + 0.5f));
                    float pitch = len / want;
                    float stagger = (st.Level & 1) == 1 ? 0.25f * pitch : 0f;
                    for (int m = 0; m < want && linked < want; m++)
                    {
                        float target = s0 + (m + 0.5f) * pitch + stagger;
                        if (target > s1) target -= pitch * 0.5f;
                        var r = Search(c, i, s0, s1, target, math.min(pitch * 0.45f, o.LinkSpacing * 0.4f));
                        if (r != null) { Accept(c, r, continueAt); linked++; }
                    }
                    // Every pair of terraces that face each other gets at least one link.
                    if (linked == 0)
                        for (float s = s0 + 10f; s <= s1 - 10f && linked == 0; s += 12f)
                        {
                            var r = TryLinkBothSides(c, i, s);
                            if (r != null && r.To == nb) { Accept(c, r, continueAt); linked++; }
                        }
                }
            }
        }

        /// <summary>
        /// Streets the regular stations left without a link to the rest (a short piece, a street whose stations all clashed):
        /// every station is tried, up and down the slope, until the piece is joined. What still stays alone is left to the
        /// network's connectivity pass (a straight link or, failing that, dropped with a notice).
        /// </summary>
        private static void Rescue(Ctx c, CancellationToken cancel)
        {
            var lay = c.Lay;
            int n = lay.Streets.Count;
            if (n < 2) return;
            for (int round = 0; round < n; round++)
            {
                var parent = new int[n];
                for (int i = 0; i < n; i++) parent[i] = i;
                int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
                foreach (var l in lay.Links) { int a = Find(l.From), b = Find(l.To); if (a != b) parent[math.max(a, b)] = math.min(a, b); }
                var size = new Dictionary<int, float>();
                for (int i = 0; i < n; i++) { int r = Find(i); size.TryGetValue(r, out float s0); size[r] = s0 + lay.Streets[i].Length; }
                int main = -1; float best = -1f;
                foreach (var kv in size) if (kv.Value > best) { best = kv.Value; main = kv.Key; }
                bool any = false;
                for (int j = 0; j < n; j++)
                {
                    cancel.ThrowIfCancellationRequested();
                    if (Find(j) == main) continue;
                    var st = lay.Streets[j];
                    TerraceRoute found = null;
                    for (float s = 15f; s < st.Length - 15f && found == null; s += 12f)
                    {
                        // Up from this street to one in the main network.
                        var up = TryLinkBothSides(c, j, s);
                        if (up != null && Find(up.To) == main) { found = up; break; }
                        // Down: from the street below whose ray up lands here.
                        float2 p = st.PointAt(s), dn = -st.UpAt(s);
                        int seg = c.Streets.Ray(p, dn, c.MaxRay, null, out float dist, c.Scratch);
                        if (seg < 0) continue;
                        int i = c.Streets.Owner[seg];
                        if (i == j || Find(i) != main) continue;
                        float si = lay.Streets[i].StationOf(p + dn * dist, out _);
                        var r = TryLinkBothSides(c, i, si);
                        if (r != null && r.To == j) found = r;
                    }
                    if (found == null) continue;
                    Accept(c, found, null);
                    any = true;
                    break;   // recompute the components
                }
                if (!any) return;
            }
        }

        private static void Accept(Ctx c, TerraceRoute r, Dictionary<int, List<(float, int)>> continueAt)
        {
            int id = c.Lay.Links.Count;
            c.Lay.Links.Add(r);
            c.Lay.Streets[r.From].Junctions.Add(r.FromS);
            c.Lay.Streets[r.To].Junctions.Add(r.ToS);
            c.Routes.AddLine(r.P, false, id);
            if (c.O.Links == TerraceLinkStyle.Spiral && continueAt != null)
            {
                if (!continueAt.TryGetValue(r.To, out var l)) continueAt[r.To] = l = new List<(float, int)>();
                // The side along the next street that keeps turning the same way round the hill.
                var from = c.Lay.Streets[r.From]; var to = c.Lay.Streets[r.To];
                float same = math.dot(from.TangentAt(r.FromS), to.TangentAt(r.ToS));
                l.Add((r.ToS, same >= 0f ? r.Side : -r.Side));
            }
        }

        /// <summary>Stretches of a street whose up-ray meets the same higher street at a link-able distance: (street, s0, s1).</summary>
        private static List<(int nb, float s0, float s1)> Runs(Ctx c, int i)
        {
            var st = c.Lay.Streets[i];
            var r = new List<(int, float, float)>();
            int n = st.Count;
            float minD = 2f * TerraceOptions.Landing + 6f, maxD = c.MaxRay;
            int Key(int k)
            {
                int nb = st.UpNbr[k];
                if (nb < 0 || nb == i || st.UpDist[k] < minD || st.UpDist[k] > maxD) return -1;
                if (c.Lay.Streets[nb].Level <= st.Level) return -1;
                return nb;
            }
            int start = 0;
            if (st.Closed)
            {
                // Start where the key changes, so a run never wraps.
                start = -1;
                for (int k = 0; k < n; k++) if (Key(k) != Key((k + n - 1) % n)) { start = k; break; }
                if (start < 0)
                {
                    int key = Key(0);
                    if (key >= 0) r.Add((key, 0f, st.Length * 0.999f));
                    return r;
                }
            }
            int curKey = -1; float curS = 0f, lastS = 0f;
            int count = n;
            for (int q = 0; q < count; q++)
            {
                int k = (start + q) % n;
                float s = st.S[k] + (st.Closed && k < start ? st.Length : 0f);
                int key = Key(k);
                if (key != curKey)
                {
                    if (curKey >= 0 && lastS - curS > 8f) r.Add((curKey, curS, lastS));
                    curKey = key; curS = s;
                }
                lastS = s;
            }
            if (curKey >= 0 && lastS - curS > 8f) r.Add((curKey, curS, lastS));
            // Keep away from open street ends (a junction right at the end of a street reads as a corner, not a link).
            if (!st.Closed)
                for (int q = r.Count - 1; q >= 0; q--)
                {
                    var (nb, a, b) = r[q];
                    a = math.max(a, 25f); b = math.min(b, st.Length - 25f);
                    if (b - a < 4f) r.RemoveAt(q); else r[q] = (nb, a, b);
                }
            return r;
        }

        /// <summary>The first link that builds and checks out, trying stations outward from the target.</summary>
        private static TerraceRoute Search(Ctx c, int i, float s0, float s1, float target, float window)
        {
            for (float d = 0f; d <= window; d += 8f)
                for (int sign = 0; sign < 2; sign++)
                {
                    if (d == 0f && sign == 1) continue;
                    float s = target + (sign == 0 ? d : -d);
                    if (s < s0 || s > s1) continue;
                    var r = TryLinkBothSides(c, i, s);
                    if (r != null) return r;
                }
            return null;
        }

        private static TerraceRoute TryLinkBothSides(Ctx c, int i, float s)
        {
            // Alternate the first side tried with the station so links do not all lean one way (spiral keeps one side).
            int first = c.O.Links == TerraceLinkStyle.Spiral ? 1 : ((int)(s / 37f) & 1) == 0 ? 1 : -1;
            // A spiral prefers its own turning direction but would rather turn the other way than leave a terrace unlinked.
            return TryLink(c, i, s, first, false) ?? TryLink(c, i, s, -first, false);
        }

        private static TerraceRoute TryLink(Ctx c, int i, float s, int side, bool continuation)
        {
            var lay = c.Lay; var o = c.O;
            var st = lay.Streets[i];
            s = st.Wrap(s);
            float minJ = math.max(50f, 3f * o.LinkHalf + 24f);
            foreach (float j in st.Junctions)
                if (st.Gap(j, s) < minJ && !(continuation && st.Gap(j, s) < 0.5f)) { lay.Reject("link: junction spacing"); return null; }
            if (!RayUp(c, i, s, out var hit)) { lay.Reject("link: no street up"); return null; }
            var to = lay.Streets[hit.Street];
            if (to.Level <= st.Level) { lay.Reject("link: not higher"); return null; }
            TerraceRoute r = null;
            switch (o.Links)
            {
                case TerraceLinkStyle.Serpentine:
                    r = Serpentine(c, i, s, hit, side) ?? Diagonal(c, i, s, hit, side, 0.92f, false) ?? Straight(c, i, s, hit);
                    break;
                case TerraceLinkStyle.Spiral:
                    r = Diagonal(c, i, s, hit, side, 0.8f, true) ?? Diagonal(c, i, s, hit, side, 0.92f, true) ?? Serpentine(c, i, s, hit, side) ?? Straight(c, i, s, hit);
                    break;
                default:
                    r = Straight(c, i, s, hit) ?? Diagonal(c, i, s, hit, side, 0.92f, false) ?? Serpentine(c, i, s, hit, side);
                    break;
            }
            if (r == null) { lay.Reject("link: no route " + o.Links); return null; }
            r.Side = side;
            // The arrival respects the next street's junction spacing too.
            foreach (float j in to.Junctions) if (to.Gap(j, r.ToS) < minJ) { lay.Reject("link: arrival spacing"); return null; }
            return r;
        }

        private static bool RayUp(Ctx c, int i, float s, out Hit hit)
        {
            hit = default;
            var st = c.Lay.Streets[i];
            float2 p = st.PointAt(s), up = st.UpAt(s);
            int seg = c.Streets.Ray(p, up, c.MaxRay, null, out float dist, c.Scratch);
            if (seg < 0) return false;
            int nb = c.Streets.Owner[seg];
            if (nb == i) return false;
            hit.Street = nb; hit.Dist = dist; hit.At = p + up * dist;
            hit.Station = c.Lay.Streets[nb].StationOf(hit.At, out _);
            return true;
        }

        /// <summary>Straight up the slope (the ray), when the climb fits at the grade.</summary>
        private static TerraceRoute Straight(Ctx c, int i, float s, Hit hit)
        {
            var lay = c.Lay; var o = c.O;
            var st = lay.Streets[i]; var to = lay.Streets[hit.Street];
            float h0 = st.HeightAt(s), h1 = to.HeightAt(hit.Station);
            float need = math.abs(h1 - h0) / (0.92f * o.MaxGrade) + 2f * TerraceOptions.Landing;
            if (hit.Dist < need) return null;
            var r = new TerraceRoute { From = i, To = hit.Street, FromS = s, ToS = hit.Station, Kind = "direct" };
            r.Start(st.PointAt(s));
            r.Line(to.PointAt(hit.Station), 1f);
            r.Profile(h0, h1, TerraceOptions.Landing);
            return Valid(c, r, o.LinkHalf) ? r : null;
        }

        /// <summary>
        /// Leaves the street at 70°, runs across the slope, arrives at 70°: as far along the next street as the climb needs at
        /// <paramref name="gradeShare"/> of the steepest road.
        /// </summary>
        private static TerraceRoute Diagonal(Ctx c, int i, float s, Hit hit, int side, float gradeShare, bool spiral)
        {
            var lay = c.Lay; var o = c.O;
            var st = lay.Streets[i]; var to = lay.Streets[hit.Street];
            float h0 = st.HeightAt(s);
            float2 p = st.PointAt(s), t0 = st.TangentAt(s) * side, up0 = st.UpAt(s);
            float sinA = math.sin(math.radians(70f)), cosA = math.cos(math.radians(70f));
            float2 dOut = math.normalize(up0 * sinA + t0 * cosA);
            // The 70° legs carry the link out of the terrace band before it turns across the slope: the long diagonal runs in the
            // bank, never along a terrace (where it would climb metres above the level cells beside it), nor hugging a street.
            float clearRun = o.LinkHalf + o.StreetHalf + 2f;
            float lcOut = TerraceOptions.Landing + math.max(clearRun, 0.9f * st.BandAt(s, true)) / sinA + 6f;
            // Direction along the next street that continues the same way.
            float along = math.dot(to.TangentAt(hit.Station), t0) >= 0f ? 1f : -1f;
            TerraceRoute best = null;
            float maxX = math.min(6f * hit.Dist + 200f, to.Closed ? to.Length * 0.45f : to.Length);
            // The along offset the climb needs: start from the straight-line estimate, walk on until the route is long enough,
            // then try a few offsets past it for one that checks out.
            float h0Est = to.HeightAt(hit.Station);
            float needEst = math.abs(h0Est - h0) / (gradeShare * o.MaxGrade) + 2f * TerraceOptions.Landing;
            float x0 = math.max(10f, 10f * math.floor(0.8f * math.sqrt(math.max(0f, needEst * needEst - hit.Dist * hit.Dist)) / 10f));
            int validTries = 0;
            for (float x = x0; x <= maxX && validTries < 5; x += 10f)
            {
                float sq = hit.Station + along * x;
                if (!to.Closed && (sq < 25f || sq > to.Length - 25f)) { lay.Reject("diag: past the street end"); break; }
                sq = to.Wrap(sq);
                float2 q = to.PointAt(sq), tq = to.TangentAt(sq) * along, upq = to.UpAt(sq);
                float2 dIn = math.normalize(upq * sinA + tq * cosA);   // travel direction when arriving
                float h1 = to.HeightAt(sq);
                float need = math.abs(h1 - h0) / (gradeShare * o.MaxGrade) + 2f * TerraceOptions.Landing;
                float lcIn = TerraceOptions.Landing + math.max(clearRun, 0.9f * to.BandAt(sq, false)) / sinA + 6f;
                // Both legs fit between the streets (the diagonal never runs back down the slope).
                float legsUp = (lcOut + lcIn) * sinA, room = 0.85f * hit.Dist;
                float shrink = legsUp > room ? room / legsUp : 1f;
                float lo1 = math.max(TerraceOptions.Landing + 4f, lcOut * shrink), li1 = math.max(TerraceOptions.Landing + 4f, lcIn * shrink);
                float2 m0 = p + dOut * lo1, m1 = q - dIn * li1;
                if (math.dot(m1 - m0, up0) < -2f || math.dot(m1 - m0, t0) <= 0f) { lay.Reject("diag: runs back"); continue; }   // never back down the slope or back along
                // Cheap length bound first: the route is at least as long as its legs and the chord between them.
                if (lo1 + li1 + math.distance(m0, m1) < need * 0.97f) { lay.Reject("diag: short"); continue; }
                var v = new List<float2> { p, m0 };
                // The run across the slope follows the street it left (an offset of it), so on a hill's curving contours it winds
                // round with them instead of cutting a chord across the next terrace.
                // (The frame is the longer street of the two, a ring before an open one: a short street has no shape to follow.)
                Across(to.Closed || to.Length > st.Length ? to : st, m0, m1, v, 0.9f * o.StreetRadius);
                v.Add(m1);
                v.Add(q);
                var r = new TerraceRoute { From = i, To = hit.Street, FromS = s, ToS = sq, Kind = spiral ? "spiral" : "diagonal" };
                r.Rounded(v, o.StreetRadius, 1f);
                float len = r.Length;
                if (len < need) continue;
                // Legs too short for a proper bend make a hook: not a road anyone would build.
                if (r.MinRadius < 0.6f * o.StreetRadius) { lay.Reject("diagonal: tight bend"); validTries++; continue; }
                r.Profile(h0, h1, TerraceOptions.Landing);
                if (r.Grade > o.MaxGrade * 1.001f) { lay.Reject("diag: grade"); continue; }
                validTries++;
                if (!Valid(c, r, o.LinkHalf, math.max(lcOut, lcIn) + 0.6f * o.StreetRadius)) continue;
                best = r;
                break;   // the shortest that climbs gently enough
            }
            if (best == null) lay.Reject(spiral ? "spiral: none" : "diagonal: none");
            return best;
        }

        /// <summary>
        /// Intermediate points from a to b as an offset of street <paramref name="st"/>: station and distance from the street
        /// both run linearly (one point every ~10 m). On a straight street this is the straight line a–b.
        /// </summary>
        private static void Across(TerraceStreet st, float2 a, float2 b, List<float2> into, float skip)
        {
            float sa = st.StationOf(a, out float da), sb = st.StationOf(b, out float db);
            // Off the end of an open street there is nothing to follow: the straight chord.
            if (!st.Closed && (sa < 1f || sb < 1f || sa > st.Length - 1f || sb > st.Length - 1f)) return;
            float2 ua = st.UpAt(sa), ub = st.UpAt(sb);
            if (math.dot(a - st.PointAt(sa), ua) < 0f) da = -da;
            if (math.dot(b - st.PointAt(sb), ub) < 0f) db = -db;
            float span = sb - sa;
            if (st.Closed)
            {
                // The short way round (the run never laps a ring).
                if (span > 0.5f * st.Length) span -= st.Length;
                else if (span < -0.5f * st.Length) span += st.Length;
            }
            float len = math.distance(a, b);
            int n = math.clamp((int)math.ceil(len / 10f), 1, 200);
            for (int k = 1; k < n; k++)
            {
                float t = k / (float)n;
                float s = st.Wrap(sa + span * t);
                var pt = st.PointAt(s) + st.UpAt(s) * math.lerp(da, db, t);
                // Room for the bends at both ends (the legs need their length for a full radius).
                if (math.distance(pt, a) < skip || math.distance(pt, b) < skip) continue;
                into.Add(pt);
            }
        }

        /// <summary>
        /// Switchbacks between the two streets (see the file header). Stacked legs need a bank between them no steeper than the
        /// banks elsewhere: the climb between two legs at their open ends (both legs and the hairpin) over the gap between their
        /// verges. Tight hairpins that would need a cliff between the legs are widened (fewer, longer legs) until it fits.
        /// </summary>
        private static TerraceRoute Serpentine(Ctx c, int i, float s, Hit hit, int side)
        {
            var lay = c.Lay; var o = c.O;
            float yMin = TerraceOptions.Landing + 2f;
            float bank = o.BankSlope * 0.85f;
            float verge = o.LinkHalf + 1.5f;
            bool tried = false;
            for (float rad = o.HairpinRadius; rad <= 0.5f * (hit.Dist - 2f * yMin) + 0.01f; rad += 4f)
            {
                int m = (int)math.floor((hit.Dist - 2f * yMin) / (2f * rad));
                if (m < 1) break;
                tried = true;
                var r = SerpentineAt(c, i, s, hit, side, rad, m, out float leg);
                if (r == null) continue;
                // Bank between stacked legs (only when there are two or more).
                if (m > 1 && r.Grade * (2f * leg + 0.5f * math.PI * rad) > bank * (2f * rad - 2f * verge)) { lay.Reject("serpentine: legs too close"); continue; }
                return Valid(c, r, o.LinkHalf) ? r : null;
            }
            if (!tried) lay.Reject("serpentine: too close");
            return null;
        }

        private static TerraceRoute SerpentineAt(Ctx c, int i, float s, Hit hit, int side, float rad, int m, out float leg)
        {
            var lay = c.Lay; var o = c.O;
            var st = lay.Streets[i]; var to = lay.Streets[hit.Street];
            leg = 0f;
            float yMin = TerraceOptions.Landing + 2f;
            float D = hit.Dist;
            float h0 = st.HeightAt(s);
            float h1Est = to.HeightAt(hit.Station);
            float2 p = st.PointAt(s), T = st.TangentAt(s) * side, U = st.UpAt(s);
            float2 W(float x, float y) => p + T * x + U * y;
            float y0 = (D - 2f * rad * m) * 0.5f;
            float turnLen = 0.5f * math.PI * rad * 2f + math.PI * rad * (m - 1);
            for (int attempt = 0; attempt < 3; attempt++)
            {
                float y1 = y0;
                float fixedW = (y0 - TerraceOptions.Landing) + (y1 - TerraceOptions.Landing) + 0.5f * turnLen;
                leg = (math.abs(h1Est - h0) / (0.92f * o.MaxGrade) - fixedW) / m;
                leg = math.max(leg, 6f);
                if (leg > 280f) { lay.Reject("serpentine: legs too long"); return null; }
                var r = new TerraceRoute { From = i, To = hit.Street, FromS = s, Kind = "serpentine", Hairpins = m - 1 };
                r.Start(W(0f, 0f));
                r.Line(W(0f, y0), 1f);
                // Quarter turn into the first leg.
                QuarterIn(r, p, T, U, rad, y0);
                float y = y0 + rad;
                bool right = true;   // heading +x
                for (int k = 0; k < m; k++)
                {
                    float xe = right ? rad + leg : rad;
                    r.Line(W(xe, y), 1f);
                    if (k < m - 1)
                    {
                        Hairpin(r, p, T, U, rad, xe, y, right);
                        y += 2f * rad;
                        right = !right;
                    }
                }
                // Quarter turn up and the landing straight into the next street.
                float xEnd = right ? rad + leg : rad;
                float xUp = QuarterOut(r, p, T, U, rad, xEnd, y, right);
                float2 from = W(xUp, y + rad);
                int seg = c.Streets.Ray(from, U, c.MaxRay, null, out float dist, c.Scratch);
                if (seg < 0 || c.Streets.Owner[seg] != hit.Street) { lay.Reject("serpentine: misses the street"); return null; }
                if (dist < yMin) { lay.Reject("serpentine: short landing"); return null; }
                float2 q = from + U * dist;
                float sq = to.StationOf(q, out _);
                r.Line(to.PointAt(sq), 1f);
                r.ToS = sq;
                float h1 = to.HeightAt(sq);
                if (math.abs(dist - y1) > 4f && attempt < 2)
                {
                    // Re-plan the legs for the real last landing.
                    y0 = math.max(yMin, y0 + (dist - y1) * 0.25f);
                    h1Est = h1;
                    continue;
                }
                r.Profile(h0, h1, TerraceOptions.Landing);
                if (r.Grade > o.MaxGrade * 1.05f) { lay.Reject("serpentine: grade"); return null; }
                return r;
            }
            return null;
        }

        // Local frame helpers: x along T (the side), y along U (up the slope); angles measured in the local frame and mapped.
        private static float2 Map(float2 p, float2 T, float2 U, float2 local) => p + T * local.x + U * local.y;

        private static void ArcLocal(TerraceRoute r, float2 p, float2 T, float2 U, float2 c, float rad, float a0, float a1, float weight)
        {
            float span = a1 - a0;
            int n = math.max(2, (int)math.ceil(math.abs(span) * rad / 3f));
            r.Break[r.Break.Count - 1] = true;
            for (int k = 1; k <= n; k++)
            {
                float a = a0 + span * k / n;
                r.P.Add(Map(p, T, U, c + new float2(math.cos(a), math.sin(a)) * rad));
                r.W.Add(weight);
                r.Break.Add(k == n || (k * 4 % n == 0 && math.abs(span) > 1.6f));
            }
            r.Break[r.Break.Count - 1] = true;
            r.MinRadius = math.min(r.MinRadius, rad);
        }

        /// <summary>From heading +y at (0, y0) to heading +x at (rad, y0 + rad).</summary>
        private static void QuarterIn(TerraceRoute r, float2 p, float2 T, float2 U, float rad, float y0) =>
            ArcLocal(r, p, T, U, new float2(rad, y0), rad, math.PI, 0.5f * math.PI, 0.5f);

        /// <summary>Semicircle at the end of a leg (at x, y heading ±x) to the next leg 2·rad higher, heading back.</summary>
        private static void Hairpin(TerraceRoute r, float2 p, float2 T, float2 U, float rad, float x, float y, bool headingRight)
        {
            var c = new float2(x, y + rad);
            if (headingRight) ArcLocal(r, p, T, U, c, rad, -0.5f * math.PI, 0.5f * math.PI, 0.5f);
            else ArcLocal(r, p, T, U, c, rad, -0.5f * math.PI, -1.5f * math.PI, 0.5f);
        }

        /// <summary>From heading ±x at (x, y) to heading +y; returns the x of the way up.</summary>
        private static float QuarterOut(TerraceRoute r, float2 p, float2 T, float2 U, float rad, float x, float y, bool headingRight)
        {
            var c = new float2(x, y + rad);
            if (headingRight) { ArcLocal(r, p, T, U, c, rad, -0.5f * math.PI, 0f, 0.5f); return x + rad; }
            ArcLocal(r, p, T, U, c, rad, -0.5f * math.PI, -math.PI, 0.5f);
            return x - rad;
        }

        /// <summary>
        /// A route is buildable: inside the area, clear of buildings, crossing no street and no other route, keeping its
        /// distance from streets except at its two junctions, arriving at ≥ 50°.
        /// </summary>
        private static bool Valid(Ctx c, TerraceRoute r, float halfWidth, float endClearMin = 0f)
        {
            var lay = c.Lay; var o = c.O;
            int n = r.P.Count;
            if (n < 2) return false;
            var s = Lines.Stations(r.P);
            float total = s[n - 1];
            float endClear = math.max(endClearMin, TerraceOptions.Landing + halfWidth + o.StreetHalf + 2f);
            float clear = halfWidth + o.StreetHalf + 1.5f;
            for (int k = 1; k < n - 1; k++)
            {
                var q = r.P[k];
                if (!Poly.Contains(lay.Boundary, q) || Poly.DistanceToBoundary(lay.Boundary, q) < math.max(2f, halfWidth * 0.5f)) { lay.Reject("check: outside"); return false; }
                // Clear of buildings by the bank its cut or fill needs (their ground stays as it is).
                float bankRun = r.H.Count == n ? math.abs(r.H[k] - lay.Ground.Sample(q)) / math.max(0.1f, o.BankSlope) + 3f : 0f;
                if (c.Obstacles != null && c.Obstacles.BlockedAt(q, halfWidth + 1f + bankRun)) { lay.Reject("check: building"); return false; }
                if (s[k] > endClear && s[k] < total - endClear)
                {
                    if (c.Streets.Nearest(q, clear, null, c.Scratch, out _) < clear) { lay.Reject("check: near street"); return false; }
                    if (c.Routes.Nearest(q, 2f * halfWidth + 2f, null, c.Scratch, out _) < 2f * halfWidth + 2f
                        || (r.H.Count == n && !RouteClear(c, q, r.H[k], halfWidth + 1.5f))) { lay.Reject("check: near route"); return false; }
                }
            }
            for (int k = 0; k + 1 < n; k++)
            {
                bool nearStart = s[k + 1] < endClear, nearEnd = s[k] > total - endClear;
                int from = r.From, to = r.To;
                if (c.Streets.Crosses(r.P[k], r.P[k + 1], ow => (nearStart && ow == from) || (nearEnd && ow == to), c.Scratch)) { lay.Reject("check: crosses street"); return false; }
                if (!nearStart && !nearEnd && c.Routes.Crosses(r.P[k], r.P[k + 1], null, c.Scratch)) { lay.Reject("check: crosses route"); return false; }
            }
            // Arrival angle.
            var toSt = lay.Streets[r.To];
            float2 last = math.normalizesafe(r.P[n - 1] - r.P[math.max(0, n - 3)]);
            if (math.abs(math.dot(last, toSt.TangentAt(r.ToS))) > 0.643f) { lay.Reject("check: arrival angle"); return false; }   // < 50°
            return true;
        }

        // ───────────────────────── bank walks ─────────────────────────

        /// <summary>A walk keeps this far (m, along the street) from a link's junction: the corner is the link's.</summary>
        private const float WalkGap = 32f;

        private static void Walks(Ctx c, CancellationToken cancel)
        {
            var lay = c.Lay; var o = c.O;
            float spacing = math.lerp(520f, 150f, math.saturate(o.Walks));
            int serial = 0;
            for (int i = 0; i < lay.Streets.Count; i++)
            {
                cancel.ThrowIfCancellationRequested();
                var st = lay.Streets[i];
                foreach (var (nb, s0, s1) in Runs(c, i))
                {
                    float len = s1 - s0;
                    int want = (int)math.floor(len / spacing + 0.35f);
                    if (want < 1) continue;
                    float pitch = len / want;
                    for (int m = 0; m < want; m++)
                    {
                        float target = s0 + (m + 0.5f) * pitch + ((st.Level & 1) == 1 ? 0.3f * pitch : 0f);
                        if (target > s1) target -= 0.5f * pitch;
                        // Nearest to the target first, as far as the whole run (a walk wants a gap between the links).
                        for (float d = 0f; d <= len; d += d < pitch * 0.5f ? 8f : 16f)
                        {
                            TerraceRoute w = null;
                            for (int sign = 0; sign < 2 && w == null; sign++)
                            {
                                float s = target + (sign == 0 ? d : -d);
                                if (s < s0 || s > s1) continue;
                                int side = ((serial + sign) & 1) == 0 ? 1 : -1;
                                w = Walk(c, i, s, side) ?? Walk(c, i, s, -side) ?? StairlessCut(c, i, s, side) ?? StairlessCut(c, i, s, -side);
                            }
                            if (w == null) continue;
                            bool trail = o.WalkStyle == "trail" || (o.WalkStyle == "mixed" && (serial & 1) == 1);
                            w.Trail = trail;
                            serial++;
                            int id = 100000 + lay.Walks.Count;
                            lay.Walks.Add(w);
                            c.Routes.AddLine(w.P, false, id);
                            break;
                        }
                    }
                }
            }
        }

        /// <summary>Across the cells to the bank foot, a ramp along the bank face (zig-zag when long), across the next cells.</summary>
        private static TerraceRoute Walk(Ctx c, int i, float s, int side)
        {
            var lay = c.Lay; var o = c.O;
            var st = lay.Streets[i];
            s = st.Wrap(s);
            foreach (float j in st.Junctions) if (st.Gap(j, s) < WalkGap) { lay.Reject("walk: junction spacing"); return null; }
            if (!RayUp(c, i, s, out var hit)) return null;
            var to = lay.Streets[hit.Street];
            if (to.Level <= st.Level) return null;
            float2 p = st.PointAt(s), T = st.TangentAt(s) * side, U = st.UpAt(s);
            float hw = o.StreetHalf, cf = o.CrossFall;
            float yf = st.BandAt(s, true);
            float bdTo = to.BandAt(hit.Station, false);
            float yt = hit.Dist - bdTo;
            if (yt - yf < 4f) return null;
            float hFoot = st.HeightAt(s) + cf * math.max(0f, yf - hw);
            float hTop = to.HeightAt(hit.Station) - cf * math.max(0f, bdTo - hw);
            float dh = hTop - hFoot;
            float run = math.abs(dh) / TerraceOptions.WalkGrade;
            float rise = yt - yf;
            float along = run > rise ? math.sqrt(run * run - rise * rise) : 0f;
            // One long ramp along the bank face where it fits (a path along a terrace wall), a zig-zag only beyond.
            int legs = math.max(1, (int)math.ceil(along / 220f));
            // A zig-zag turns on a short level landing (the legs part there instead of meeting in a V).
            const float Turn = 6f;
            float riseRamps = rise - (legs - 1) * Turn;
            if (riseRamps < 3f * legs) { lay.Reject("walk: no room for the turns"); return null; }
            float legAlong = legs > 1 ? math.sqrt(math.max(0f, (run / legs) * (run / legs) - (riseRamps / legs) * (riseRamps / legs))) : along;
            if (legAlong > 220f) { lay.Reject("walk: bank too long"); return null; }
            // Stacked legs leave a steeper bank between them: they must part by ≥ ~25° (rise/run ≥ 0.225 per leg).
            if (legs > 1 && riseRamps / legs / math.max(1f, legAlong) < 0.225f) { lay.Reject("walk: zig-zag too flat"); return null; }
            // The walk's frame follows its street: x along it (on the chosen side), y up the slope from it — on a hill's curving
            // contours the ramp stays in its bank instead of cutting a chord across a terrace.
            bool off = false;
            float2 W(float x, float y)
            {
                float sx = s + side * x;
                if (!st.Closed && (sx < 5f || sx > st.Length - 5f)) { off = true; sx = math.clamp(sx, 0f, st.Length); }
                sx = st.Wrap(sx);
                return st.PointAt(sx) + st.UpAt(sx) * y;
            }
            // On a curve the offset the ramp runs at is shorter (inside) or longer (outside) than the street: stretch the run so
            // the real ramp climbs at the walk grade.
            {
                float ym = 0.5f * (yf + yt), target = legAlong;
                for (int it = 0; it < 2 && legAlong > 1f; it++)
                {
                    float real = 0f; float2 prev = W(0f, ym);
                    for (int qi = 1; qi <= 12; qi++) { float2 cur = W(legAlong * qi / 12f, ym); real += math.distance(prev, cur); prev = cur; }
                    if (real < 1f) break;
                    legAlong *= math.clamp(target / real, 0.6f, 2f);
                }
                if (legAlong > 260f) { lay.Reject("walk: bank too long"); return null; }
                off = false;
            }
            var r = new TerraceRoute { From = i, To = hit.Street, FromS = s, Walk = true, Kind = "walk" };
            r.Start(p);
            r.H.Add(st.HeightAt(s));
            float fx = 0f, fy = 0f;
            void Add(float xB, float yB, Func<float, float> h, float step)
            {
                // Dense frame points, then heights by the real distance along them (an even climb on a curve too).
                int k = math.max(2, (int)math.ceil((math.abs(xB - fx) + math.abs(yB - fy)) / step));
                var pts = new List<float2>(k + 1) { r.P[r.P.Count - 1] };
                for (int u = 1; u <= k; u++) { float t = u / (float)k; pts.Add(W(math.lerp(fx, xB, t), math.lerp(fy, yB, t))); }
                var st2 = Lines.Stations(pts);
                float tot = math.max(1e-3f, st2[st2.Length - 1]);
                r.Break[r.Break.Count - 1] = true;
                for (int u = 1; u <= k; u++) { r.P.Add(pts[u]); r.H.Add(h(st2[u] / tot)); r.W.Add(1f); r.Break.Add(false); }
                r.Break[r.Break.Count - 1] = true;
                fx = xB; fy = yB;
            }
            float h0 = st.HeightAt(s);
            // Across the cells (the terrace plane rises gently to the bank foot).
            Add(0f, yf, t => h0 + cf * math.max(0f, math.lerp(0f, yf, t) - hw), 4f);
            float x = 0f, yCur = yf;
            for (int leg = 0; leg < legs; leg++)
            {
                float yB = yCur + riseRamps / legs;
                float hA = hFoot + dh * leg / legs, hB = hFoot + dh * (leg + 1) / legs;
                float xB = (leg & 1) == 0 ? legAlong : 0f;
                float hAa = hA, hBb = hB;
                Add(xB, yB, t => math.lerp(hAa, hBb, t), 4f);
                yCur = yB;
                x = xB;
                if (leg < legs - 1)
                {
                    Add(xB, yCur + Turn, t => hBb, 3f);
                    yCur += Turn;
                }
            }
            if (off) { lay.Reject("walk: past the street end"); return null; }
            // The ramp's real climb (the frame stretches on a curve): never past the walk grade.
            for (int k = 1; k < r.P.Count; k++)
            {
                float l = math.distance(r.P[k - 1], r.P[k]);
                if (l > 0.3f && math.abs(r.H[k] - r.H[k - 1]) / l > RoadWalkways.MaxWalkGrade) { lay.Reject("walk: steep on the curve"); return null; }
            }
            // Up across the next terrace's cells to its street.
            float2 from = W(x, yt);
            float sxEnd = st.Wrap(s + side * x);
            int seg = c.Streets.Ray(from, st.UpAt(sxEnd), c.MaxRay, null, out float dist, c.Scratch);
            if (seg < 0 || c.Streets.Owner[seg] != hit.Street || dist > bdTo + 12f) return null;
            float2 q = from + st.UpAt(sxEnd) * dist;
            float sq = to.StationOf(q, out _);
            foreach (float j in to.Junctions) if (to.Gap(j, sq) < WalkGap) { lay.Reject("walk: junction spacing"); return null; }
            float h1 = to.HeightAt(sq);
            float hTop2 = hTop;
            {
                float2 a = r.P[r.P.Count - 1], b = to.PointAt(sq);
                int k = math.max(1, (int)math.ceil(math.distance(a, b) / 4f));
                r.Break[r.Break.Count - 1] = true;
                for (int u = 1; u <= k; u++) { r.P.Add(math.lerp(a, b, u / (float)k)); r.H.Add(math.lerp(hTop2, h1, u / (float)k)); r.W.Add(1f); r.Break.Add(false); }
                r.Break[r.Break.Count - 1] = true;
            }
            r.ToS = sq;
            // Steepest piece.
            for (int k = 1; k < r.P.Count; k++)
            {
                float l = math.distance(r.P[k - 1], r.P[k]);
                if (l > 0.5f) r.Grade = math.max(r.Grade, math.abs(r.H[k] - r.H[k - 1]) / l);
            }
            if (r.Grade > RoadWalkways.MaxWalkGrade) return null;
            return ValidWalk(c, r) ? r : null;
        }

        /// <summary>
        /// Where the bank is too short for a ramp along it (a deep step between terraces), the walk climbs straight across from
        /// street to street at ≤ 7.5 %, diagonally through the terraces like the stepped lanes of a hill town (vanilla has no
        /// stair path): the terraces give way around it. Never flatter than 30° to the streets.
        /// </summary>
        private static TerraceRoute StairlessCut(Ctx c, int i, float s, int side)
        {
            var lay = c.Lay; var o = c.O;
            var st = lay.Streets[i];
            s = st.Wrap(s);
            foreach (float j in st.Junctions) if (st.Gap(j, s) < WalkGap) { lay.Reject("walk: junction spacing"); return null; }
            if (!RayUp(c, i, s, out var hit)) return null;
            var to = lay.Streets[hit.Street];
            if (to.Level <= st.Level) return null;
            // A level stub straight off each street, clear of its road and verge, before the climb starts: the walk never
            // climbs beside a road (their corridors would clash at different heights).
            float stub = o.StreetHalf + 5f;
            float h0 = st.HeightAt(s), h1 = to.HeightAt(hit.Station);
            float hA = h0 + o.CrossFall * math.max(0f, stub - o.StreetHalf);
            float2 p = st.PointAt(s), pA = p + st.UpAt(s) * stub, t0 = st.TangentAt(s) * side;
            float need = math.abs(h1 - h0) / (0.96f * TerraceOptions.WalkGrade);
            // At ≥ 30° to the streets: shallower, it would run along the terrace it cuts for too long.
            if (need > 2f * (hit.Dist - 2f * stub)) { lay.Reject("walk: too steep to cut across"); return null; }
            float along = math.dot(to.TangentAt(hit.Station), t0) >= 0f ? 1f : -1f;
            float x = math.sqrt(math.max(0f, need * need - (hit.Dist - 2f * stub) * (hit.Dist - 2f * stub))) + 4f;
            float sq = 0f, len = 0f, hB = 0f;
            float2 q = default, qB = default;
            bool ok = false;
            // On a curving street the chord to the point x along is shorter than planned: reach a little further until it climbs gently.
            for (int tries = 0; tries < 6 && !ok; tries++)
            {
                sq = hit.Station + along * x;
                if (!to.Closed && (sq < 20f || sq > to.Length - 20f)) { lay.Reject("walk: past the street end"); return null; }
                sq = to.Wrap(sq);
                q = to.PointAt(sq);
                qB = q - to.UpAt(sq) * stub;
                h1 = to.HeightAt(sq);
                hB = h1 - o.CrossFall * math.max(0f, stub - o.StreetHalf);
                len = math.distance(pA, qB);
                if (math.abs(hB - hA) / len <= 0.96f * TerraceOptions.WalkGrade) ok = true;
                else x += math.abs(hB - hA) / (0.96f * TerraceOptions.WalkGrade) - len + 5f;
            }
            if (!ok) { lay.Reject("walk: cut never gentle"); return null; }
            foreach (float j in to.Junctions) if (to.Gap(j, sq) < WalkGap) { lay.Reject("walk: junction spacing"); return null; }
            var r = new TerraceRoute { From = i, To = hit.Street, FromS = s, ToS = sq, Walk = true, Kind = "walk-cut" };
            r.Start(p);
            r.H.Add(h0);
            void Seg(float2 a, float2 b, float ha, float hb, float step)
            {
                int k = math.max(1, (int)math.ceil(math.distance(a, b) / step));
                r.Break[r.Break.Count - 1] = true;
                for (int u = 1; u <= k; u++) { r.P.Add(math.lerp(a, b, u / (float)k)); r.H.Add(math.lerp(ha, hb, u / (float)k)); r.W.Add(1f); r.Break.Add(false); }
                r.Break[r.Break.Count - 1] = true;
            }
            Seg(p, pA, h0, hA, 3f);
            Seg(pA, qB, hA, hB, 4f);
            Seg(qB, q, hB, h1, 3f);
            r.Grade = math.abs(hB - hA) / len;
            // It must leave the stubs at a walkable angle; past them it is clear of its streets once the gap it opens (at that
            // angle) covers both corridors and the bank its own climb needs.
            float2 dir = math.normalizesafe(qB - pA);
            float cosOut = math.abs(math.dot(dir, st.TangentAt(s))), cosIn = math.abs(math.dot(dir, to.TangentAt(sq)));
            if (cosOut > 0.97f || cosIn > 0.97f) { lay.Reject("walk: cut too shallow"); return null; }
            float sinMin = math.sqrt(1f - math.max(cosOut, cosIn) * math.max(cosOut, cosIn));
            float opens = sinMin - TerraceOptions.WalkGrade / math.max(0.1f, o.BankSlope);
            if (opens < 0.12f) { lay.Reject("walk: cut too shallow"); return null; }
            if (!ValidWalk(c, r, stub + 4f)) return null;
            return r;
        }

        /// <summary>
        /// A walk at height h passes a street with room for the bank between them: the street's road strip (half width + 1.5 m)
        /// and the walk's (<paramref name="half"/>) stay apart by the height difference over the bank slope.
        /// </summary>
        private static bool HeightClear(Ctx c, float2 q, float h, float half, int from = -1, int to = -1)
        {
            var o = c.O;
            float reach = o.StreetHalf + 1.5f + half + 30f;
            c.Streets.Query(q - reach, q + reach, c.Scratch);
            foreach (int id in c.Scratch)
            {
                float d = math.sqrt(Seg.DistSq(q, c.Streets.A[id], c.Streets.B[id], out _));
                float need = o.StreetHalf + 1.5f + half + 1f;
                if (math.isfinite(h))
                {
                    var st = c.Lay.Streets[c.Streets.Owner[id]];
                    float sh = st.H[math.clamp(c.Streets.Index[id], 0, st.Count - 1)];
                    // The walk on its own terrace (crossing its cells to or from its street) is level with it.
                    int ow = c.Streets.Owner[id];
                    if ((ow == from || ow == to) && math.abs(h - sh) < 0.75f) continue;
                    need += math.abs(h - sh) / math.max(0.1f, o.BankSlope);
                }
                if (d < need) return false;
            }
            return true;
        }

        /// <summary>The same against the planned links and walks (their heights where they pass).</summary>
        private static bool RouteClear(Ctx c, float2 q, float h, float half)
        {
            if (!math.isfinite(h)) return true;
            var o = c.O;
            float reach = o.LinkHalf + 1.5f + half + 30f;
            c.Routes.Query(q - reach, q + reach, c.Scratch);
            foreach (int id in c.Scratch)
            {
                int owner = c.Routes.Owner[id];
                var route = owner >= 100000 ? c.Lay.Walks[owner - 100000] : c.Lay.Links[owner];
                float rh = route.H.Count > 0 ? route.H[math.clamp(c.Routes.Index[id], 0, route.H.Count - 1)] : h;
                float rHalf = route.Walk ? 2.5f : o.LinkHalf + 1.5f;
                float d = math.sqrt(Seg.DistSq(q, c.Routes.A[id], c.Routes.B[id], out _));
                if (d < rHalf + half + 1f + math.abs(h - rh) / math.max(0.1f, o.BankSlope)) return false;
            }
            return true;
        }

        private static bool ValidWalk(Ctx c, TerraceRoute r, float endClearMin = 0f)
        {
            var lay = c.Lay; var o = c.O;
            int n = r.P.Count;
            var s = Lines.Stations(r.P);
            float total = s[n - 1];
            float endClear = math.max(endClearMin, o.StreetHalf + 3f);
            float clear = o.StreetHalf + 2.5f;
            for (int k = 1; k < n - 1; k++)
            {
                var q = r.P[k];
                if (!Poly.Contains(lay.Boundary, q) || Poly.DistanceToBoundary(lay.Boundary, q) < 2f) { lay.Reject("walk: outside"); return false; }
                float walkBank = r.H.Count == n ? math.abs(r.H[k] - lay.Ground.Sample(q)) / math.max(0.1f, o.BankSlope) + 2f : 0f;
                if (c.Obstacles != null && c.Obstacles.BlockedAt(q, 2f + walkBank)) { lay.Reject("walk: building"); return false; }
                if (s[k] > endClear && s[k] < total - endClear)
                {
                    // Room for the bank between the walk and a street or road at another height (the corridors never clash).
                    float h = r.H.Count == n ? r.H[k] : float.NaN;
                    if (c.Streets.Nearest(q, clear, null, c.Scratch, out _) < clear || !HeightClear(c, q, h, 2.5f, r.From, r.To)) { lay.Reject("walk: near street"); return false; }
                    if (c.Routes.Nearest(q, o.LinkHalf + 4f, null, c.Scratch, out _) < o.LinkHalf + 4f || !RouteClear(c, q, h, 2.5f)) { lay.Reject("walk: near route"); return false; }
                }
            }
            for (int k = 0; k + 1 < n; k++)
            {
                bool nearStart = s[k + 1] < endClear, nearEnd = s[k] > total - endClear;
                int from = r.From, to = r.To;
                if (c.Streets.Crosses(r.P[k], r.P[k + 1], ow => (nearStart && ow == from) || (nearEnd && ow == to), c.Scratch)) { lay.Reject("walk: crosses street"); return false; }
                if (c.Routes.Crosses(r.P[k], r.P[k + 1], null, c.Scratch)) { lay.Reject("walk: crosses route"); return false; }
            }
            return true;
        }
    }
}
