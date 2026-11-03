// rf-shops3 — the ring drive: a loop lane that follows the site's own outline (inset, corners softened), the answer to
// round and odd sites. The user, on the baseline circle: "it looked like a park ... basically no parking". A layout laid
// out from one straight frontage keeps only the rectangle inscribed in a circle (64 % of it) and plants the crescents;
// a ring follows the rim: stores round the outside of the loop facing in, the middle for the lot (strip mall, big box),
// the park (mall park) or the lanes (mall district), spokes from every street to the loop. On a rectangle the same ring
// is the block with rounded corners.

using System;
using System.Collections.Generic;
using Placemaker.Core.Geometry;
using Placemaker.Core.Layers.Common;
using Unity.Mathematics;

namespace Placemaker.Core.Layers.Commercial
{
    public sealed partial class Mall
    {
        /// <summary>
        /// The site's outline inset by <paramref name="inset"/> m and simplified to a drivable loop: edges at least
        /// <paramref name="minEdge"/> m (so a row of plots fits along them), corners sharper than ~40° cut by a chamfer of
        /// <paramref name="round"/> m (a soft corner, its wedge planted). CCW. Null when too small.
        /// </summary>
        public float2[] RingLine(float inset, float round, float minEdge)
        {
            var inner = Region.InsetRing(inset);
            if (inner == null || inner.Length < 3 || Poly.Area(inner) < 900f) return null;
            var simp = new List<float2>(Poly.EnsureCcw(Poly.Simplify(inner, 2.5f)));
            MergeShort(simp, minEdge, 4);
            if (simp.Count < 3) return null;
            // Soften the corners: every turn sharper than 40° becomes a chamfer (two points `round` m back along both edges).
            var res = new List<float2>();
            int m = simp.Count;
            for (int i = 0; i < m; i++)
            {
                var p = simp[(i + m - 1) % m]; var q = simp[i]; var r = simp[(i + 1) % m];
                var d1 = math.normalizesafe(q - p); var d2 = math.normalizesafe(r - q);
                float turn = math.degrees(math.acos(math.clamp(math.dot(d1, d2), -1f, 1f)));
                float c = math.min(round, 0.3f * math.min(math.distance(p, q), math.distance(q, r)));
                if (turn > 40f && c >= 3f) { res.Add(q - d1 * c); res.Add(q + d2 * c); }
                else res.Add(q);
            }
            var ring = Poly.EnsureCcw(res.ToArray());
            return Poly.IsSimple(ring) && Poly.Area(ring) >= 900f ? ring : null;
        }

        /// <summary>
        /// (rf-shops3) Like <see cref="RingLine"/>, but each side of the outline keeps its own distance: <paramref name="insetFor"/>
        /// (a point of the outline, its outward normal) → metres. The outline is simplified first (edges under 10 m merged), every
        /// edge moved in by its own inset and the corners rebuilt as the meeting points of the moved edges (nearly straight
        /// corners take the mean). Falls back to a uniform inset of the largest distance when the result is not a clean loop.
        /// </summary>
        public float2[] RingLineVar(Func<float2, float2, float> insetFor, float round, float minEdge)
        {
            var simp = new List<float2>(Poly.EnsureCcw(Poly.Simplify(Region.Ring, 2.5f)));
            MergeShort(simp, 10f, 3);
            int n = simp.Count;
            if (n < 3) return null;
            var off = new float[n];
            var nrm = new float2[n];
            float maxInset = 0f;
            for (int i = 0; i < n; i++)
            {
                var a = simp[i]; var b = simp[(i + 1) % n];
                var d = math.normalizesafe(b - a);
                nrm[i] = new float2(d.y, -d.x);   // outward (CCW: inside is on the left)
                off[i] = insetFor(math.lerp(a, b, 0.5f), nrm[i]);
                maxInset = math.max(maxInset, off[i]);
            }
            var pts = new List<float2>();
            for (int i = 0; i < n; i++)
            {
                int p = (i + n - 1) % n;
                // Line of the moved edge p: point simp[i] - nrm[p]·off[p], direction dp; of edge i: simp[i] - nrm[i]·off[i], di.
                var dp = new float2(-nrm[p].y, nrm[p].x); var di = new float2(-nrm[i].y, nrm[i].x);
                var qp = simp[i] - nrm[p] * off[p]; var qi = simp[i] - nrm[i] * off[i];
                float cr = Poly.Cross(dp, di);
                if (math.abs(cr) < 0.34f) pts.Add(simp[i] - math.normalizesafe(nrm[p] + nrm[i]) * (0.5f * (off[p] + off[i])));
                else
                {
                    float t = Poly.Cross(qi - qp, di) / cr;
                    pts.Add(qp + dp * t);
                }
            }
            var ring = Poly.EnsureCcw(pts.ToArray());
            bool ok = ring.Length >= 3 && Poly.IsSimple(ring) && Poly.Area(ring) >= 900f;
            if (ok) foreach (var p in ring) if (!Region.Contains(p)) { ok = false; break; }
            if (!ok) return RingLine(maxInset, round, minEdge);
            return Soften(ring, round, minEdge);
        }

        /// <summary>
        /// (rf-shops3) Corner courts: outside each soft corner of a ring drive (a chamfer between two long sides) the rows of both
        /// sides stop short and leave a square of lawn by the street corner. A small car park turned to face the chamfer takes
        /// it (one aisle, entered from the middle of the chamfer), clipped to the site; what it cannot use stays planted.
        /// <paramref name="skip"/> leaves out segments (a strip mall's front corners, kept for pads and the pond). Returns the count.
        /// </summary>
        private static float2 ClosestOnRing(float2[] ring, float2 p)
        {
            float2 best = ring[0]; float bd = float.MaxValue;
            for (int k = 0; k < ring.Length; k++)
            {
                float2 a = ring[k], b = ring[(k + 1) % ring.Length], ab = b - a;
                float l2 = math.lengthsq(ab);
                var q = l2 < 1e-6f ? a : a + ab * math.saturate(math.dot(p - a, ab) / l2);
                float d = math.distancesq(q, p);
                if (d < bd) { bd = d; best = q; }
            }
            return best;
        }

        /// <summary>(rf-shops3) The ground of all the shop plots planned (m²), to compare two layouts of one site.</summary>
        public float ShopArea()
        {
            float a = 0f;
            foreach (var s in Shops) a += Poly.Area(s);
            return a;
        }

        /// <summary>(rf-shops3) A lot whose way in reaches its aisles (a court that would warn "this entrance has no path" is not built).</summary>
        public bool CleanLot(float2[] ring, List<(float2 at, string kind)> entrances, SettingsBag parking, uint salt)
        {
            var t = PlanLot(ring, entrances, parking, salt);
            if (t == null) return false;
            return !Warns(t) && CommitLot(t);
        }

        /// <summary>(rf-shops3) A planned lot the player would be warned about: a way in reaching no aisle, stalls no car can reach.</summary>
        public static bool Warns(LotTry t)
        {
            if (t?.Sub?.Issues == null) return false;
            int noRoad = 0;
            foreach (var i in t.Sub.Issues)
            {
                if (i.Key == null) continue;
                if (i.Key.EndsWith("EntranceNoPath") || i.Key.EndsWith("UnreachableStalls")) return true;
                if (i.Key.EndsWith("EntranceNoRoad")) noRoad++;
            }
            if (noRoad == 0) return false;
            // ways in that reach no lane: a lot no car can enter when none of them does
            int ways = 0;
            foreach (var n in t.Sub.Nets) if (n != null && n.Role == NetRole.DriveConnector) ways++;
            return noRoad >= ways;
        }

        public int CornerCourts(Lane loop, SettingsBag parking, uint salt, Func<int, bool> skip = null)
        {
            int n = loop.Points.Count - 1, made = 0;
            if (n < 4) return 0;
            float Len(int k) => math.distance(loop.Points[((k % n) + n) % n], loop.Points[(((k + 1) % n) + n) % n]);
            for (int i = 0; i < n; i++)
            {
                Ctx.Check();
                if (skip != null && skip(i)) continue;
                float l = Len(i);
                if (l < 9f || l > 34f || Len(i - 1) < 1.4f * l || Len(i + 1) < 1.4f * l) continue;
                var f = Frame(loop.Points[i], loop.Points[(i + 1) % n]);
                // the chamfer turns the loop by about 45° at each end
                var dPrev = math.normalizesafe(loop.Points[i] - loop.Points[((i - 1) % n + n) % n]);
                if (math.dot(dPrev, f.Dir) > 0.94f || math.dot(dPrev, f.Dir) < 0.35f) continue;
                // The leftover by this corner (the square between the two sides' rows and the street corner), entered from the
                // middle of the chamfer; a piece too ragged for a lot is trimmed to the square facing the chamfer and tried again.
                var outward = -f.In;
                var probe = f.At(0.5f * f.To, 0f) + outward * (loop.Half + 3f);
                float2[] piece = null;
                foreach (var q in RestPieces(150f)) if (Poly.Contains(q, probe)) { piece = q; break; }
                if (piece == null) continue;
                var entry = new List<(float2, string)> { (ClosestOnRing(piece, f.At(0.5f * f.To, 0f)), "alley") };
                if (CleanLot(piece, entry, parking, salt + (uint)i * 7u)) { made++; continue; }
                float z = loop.Half + 1.5f;
                var square = Rect(f, 0.5f * f.To - 13f, 0.5f * f.To + 13f, -z, -(z + 30f));
                try
                {
                    var cut = Largest(Poly.Intersection(Poly.EnsureCcw(piece), Poly.EnsureCcw(square)), 250f);
                    if (cut != null && CleanLot(cut, new List<(float2, string)> { (ClosestOnRing(cut, f.At(0.5f * f.To, 0f)), "alley") }, parking, salt + (uint)i * 7u + 3u)) made++;
                }
                catch (Exception) { }
            }
            Ctx.Plan.Facts["cornerCourts"] = (Ctx.Plan.Facts.TryGetValue("cornerCourts", out var c) ? c : 0) + made;
            return made;
        }

        /// <summary>
        /// Merges edges shorter than <paramref name="minEdge"/> (while more than <paramref name="minCount"/> points are left). A short
        /// edge between two long ones that turn (a kerb return the bucket cut across a block's corner) gives way to the corner
        /// itself, where the long edges' lines meet: dropping one of its ends instead skews a whole side of the block by the
        /// cut (a 5 m return slants a 120 m side by 5 m, and every row along it backs onto the street at one end). Otherwise
        /// the end whose triangle with its neighbours is smaller goes.
        /// </summary>
        private static void MergeShort(List<float2> simp, float minEdge, int minCount)
        {
            for (int guard = 0; guard < 96 && simp.Count > minCount; guard++)
            {
                int n = simp.Count, worst = -1; float shortest = float.MaxValue;
                for (int i = 0; i < n; i++)
                {
                    float l = math.distance(simp[i], simp[(i + 1) % n]);
                    if (l < shortest) { shortest = l; worst = i; }
                }
                if (shortest >= minEdge) break;
                int a = worst, b = (worst + 1) % n, pi = (a + n - 1) % n, qi = (b + 1) % n;
                float2 pa = simp[pi], va = simp[a], vb = simp[b], qb = simp[qi];
                var d1 = math.normalizesafe(va - pa); var d2 = math.normalizesafe(qb - vb);
                float cr = Poly.Cross(d1, d2);
                if (math.abs(cr) > 0.25f && math.distance(pa, va) > shortest && math.distance(vb, qb) > shortest)
                {
                    float t = Poly.Cross(vb - va, d2) / cr;
                    var x = va + d1 * t;
                    if (t > -0.5f * shortest && t < 2.5f * shortest && math.distance(x, vb) < 2.5f * shortest)
                    {
                        simp[a] = x;
                        simp.RemoveAt(b);
                        continue;
                    }
                }
                float TriArea(int k) { var p = simp[(k + n - 1) % n]; var q = simp[k]; var r = simp[(k + 1) % n]; return math.abs(Poly.Cross(q - p, r - p)) * 0.5f; }
                simp.RemoveAt(TriArea(a) <= TriArea(b) ? a : b);
            }
        }

        /// <summary>Short edges merged (each at least <paramref name="minEdge"/>), corners sharper than 40° chamfered by <paramref name="round"/> m.</summary>
        private static float2[] Soften(float2[] ring, float round, float minEdge)
        {
            var simp = new List<float2>(Poly.EnsureCcw(ring));
            MergeShort(simp, minEdge, 4);
            if (simp.Count < 3) return null;
            var res = new List<float2>();
            int m = simp.Count;
            for (int i = 0; i < m; i++)
            {
                var p = simp[(i + m - 1) % m]; var q = simp[i]; var r = simp[(i + 1) % m];
                var d1 = math.normalizesafe(q - p); var d2 = math.normalizesafe(r - q);
                float turn = math.degrees(math.acos(math.clamp(math.dot(d1, d2), -1f, 1f)));
                float c = math.min(round, 0.3f * math.min(math.distance(p, q), math.distance(q, r)));
                if (turn > 40f && c >= 3f) { res.Add(q - d1 * c); res.Add(q + d2 * c); }
                else res.Add(q);
            }
            var outRing = Poly.EnsureCcw(res.ToArray());
            return Poly.IsSimple(outRing) && Poly.Area(outRing) >= 900f ? outRing : null;
        }

        /// <summary>
        /// The longest chord of a closed loop along the line through <paramref name="p"/> in direction <paramref name="dir"/>
        /// (the two crossing points, the first at the smaller coordinate along dir). False when the line misses the loop.
        /// </summary>
        public static bool Chord(List<float2> loop, float2 p, float2 dir, out float2 a, out float2 b)
        {
            a = b = p;
            var hits = new List<float>();
            for (int i = 0; i + 1 < loop.Count; i++)
            {
                var u = loop[i]; var v = loop[i + 1];
                var e = v - u;
                float den = Poly.Cross(dir, e);
                if (math.abs(den) < 1e-6f) continue;
                float t = Poly.Cross(u - p, e) / den;      // along dir
                float w = Poly.Cross(u - p, dir) / den;    // along the edge
                if (w >= 0f && w < 1f) hits.Add(t);
            }
            if (hits.Count < 2) return false;
            hits.Sort();
            float best = 0f;
            for (int i = 0; i + 1 < hits.Count; i += 2)
                if (hits[i + 1] - hits[i] > best) { best = hits[i + 1] - hits[i]; a = p + dir * hits[i]; b = p + dir * hits[i + 1]; }
            return best > 1f;
        }

        /// <summary>
        /// A closed loop lane along <paramref name="ring"/> (CCW), starting in the middle of its longest edge. Null when its
        /// corridors are not free or a corner leaves the site.
        /// </summary>
        public Lane RingLoop(float2[] ring, NetRole role, AssetRef asset, float h, float2? startNear = null)
        {
            if (ring == null || ring.Length < 3) return null;
            int n = ring.Length, best = 0; float len = 0f;
            for (int i = 0; i < n; i++) { float l = math.distance(ring[i], ring[(i + 1) % n]); if (l > len) { len = l; best = i; } }
            // (rf-shops3) Or the edge nearest a given point (the back of a strip mall: its front drive stays one straight piece).
            if (startNear.HasValue)
            {
                float bd = float.MaxValue;
                for (int i = 0; i < n; i++)
                {
                    var a = ring[i]; var b = ring[(i + 1) % n];
                    if (math.distance(a, b) < 20f) continue;
                    float dd = math.distance(math.lerp(a, b, 0.5f), startNear.Value);
                    if (dd < bd) { bd = dd; best = i; }
                }
            }
            // (rf-shops3) Starting on a corner: a start in the middle of an edge split it into two short runs of shops.
            var start = ring[best];
            var corners = new List<float2>();
            for (int k = 1; k < n; k++) corners.Add(ring[(best + k) % n]);
            return Loop(start, corners, role, asset, h);
        }

        /// <summary>
        /// Spokes from the streets to a loop: one from the middle of every frontage run at least <paramref name="minRun"/> m long
        /// (two on a run over 200 m), straight in to the loop, kept <paramref name="clear"/> m off the loop's corners. The main
        /// run (index 0) always gets one when it can. Returns the spokes made.
        /// </summary>
        public List<Lane> Spokes(Lane loop, List<StreetShops.Run> runs, NetRole role, AssetRef asset, float h, float minRun = 40f, float clear = 9f, int max = 4)
        {
            var made = new List<Lane>();
            if (loop == null) return made;
            var poly = loop.Points;
            foreach (var r in runs)
            {
                if (made.Count >= max) break;
                if (r.Length < minRun) continue;
                int k = r.Length > 200f ? 2 : 1;
                for (int j = 0; j < k && made.Count < max; j++)
                {
                    float mid = math.lerp(r.From, r.To, (j + 0.5f) / k);
                    // Slide along the run (±12 m) until the spoke meets an edge of the loop away from its corners.
                    Lane got = null;
                    foreach (float shift in new[] { 0f, 6f, -6f, 12f, -12f })
                    {
                        float s = mid + shift;
                        if (s < r.From + CornerClear || s > r.To - CornerClear) continue;
                        var a = r.At(s, 0f);
                        if (!RayHit(poly, a, r.In, out var hit, out int seg)) continue;
                        if (math.distance(hit, poly[seg]) < clear || math.distance(hit, poly[seg + 1]) < clear) continue;
                        bool near = false;
                        foreach (var o in made) if (math.distance(o.Points[o.Points.Count - 1], hit) < 30f) near = true;
                        if (near) continue;
                        got = Connect(End.Street(a, -r.In), null, End.OnLane(loop, hit), role, asset, h);
                        if (got != null) break;
                    }
                    if (got != null) made.Add(got);
                }
            }
            return made;
        }

        /// <summary>The first crossing of the ray from <paramref name="a"/> along <paramref name="dir"/> with the polyline.</summary>
        private static bool RayHit(List<float2> line, float2 a, float2 dir, out float2 hit, out int seg)
        {
            hit = a; seg = -1;
            float best = float.MaxValue;
            var b = a + dir * 2000f;
            for (int i = 0; i + 1 < line.Count; i++)
            {
                if (!Poly.SegmentIntersection(a, b, line[i], line[i + 1], out var p, out float t, out _)) continue;
                float d = t * 2000f;
                if (d > 0.5f && d < best) { best = d; hit = p; seg = i; }
            }
            return seg >= 0;
        }

        /// <summary>
        /// (rf-shops3) How round or odd the site is for a layout drawn from straight frontages: the largest rectangle square to
        /// the main run inside the site, as a share of the site's area (a rectangle ~0.9+, an L or a pinch ~0.6, a circle 0.64).
        /// </summary>
        public float Squareness(StreetShops.Run r)
        {
            var ring = Region.Ring;
            float sMin = float.MaxValue, sMax = float.MinValue, tMin = float.MaxValue, tMax = float.MinValue;
            foreach (var p in ring)
            {
                float s = math.dot(p - r.A, r.Dir), t = math.dot(p - r.A, r.In);
                sMin = math.min(sMin, s); sMax = math.max(sMax, s); tMin = math.min(tMin, t); tMax = math.max(tMax, t);
            }
            // Columns every 4 m: the stretch of each inside the site; the best rectangle over consecutive columns.
            var cols = new List<(float a, float b)>();
            for (float x = sMin + 2f; x <= sMax - 2f; x += 4f)
            {
                float a = float.NaN, b = float.NaN;
                for (float t = tMin - 1f; t <= tMax + 1f; t += 2f)
                    if (Region.Contains(r.At(x, t))) { if (float.IsNaN(a)) a = t; b = t; }
                    else if (!float.IsNaN(a)) break;
                cols.Add((a, b));
            }
            float best = 0f;
            for (int i = 0; i < cols.Count; i++)
            {
                if (float.IsNaN(cols[i].a)) continue;
                float lo = cols[i].a, hi = cols[i].b;
                for (int j = i; j < cols.Count; j++)
                {
                    if (float.IsNaN(cols[j].a)) break;
                    lo = math.max(lo, cols[j].a); hi = math.min(hi, cols[j].b);
                    if (hi <= lo) break;
                    best = math.max(best, (j - i + 1) * 4f * (hi - lo));
                }
            }
            return Region.Area > 1f ? best / Region.Area : 1f;
        }
    }
}
