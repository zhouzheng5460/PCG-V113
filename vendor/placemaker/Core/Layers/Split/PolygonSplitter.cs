// Cutting a simple polygon into parts along chords. A chord is a segment between two points on
// the outline that stays inside the polygon; splitting along it always yields exactly two simple
// polygons whose areas sum to the original — the property the split layer relies on.

using System;
using System.Collections.Generic;
using Placemaker.Core.Geometry;
using Placemaker.Core.Layers.Common;
using Unity.Mathematics;

namespace Placemaker.Core.Layers.Split
{
    public struct Chord
    {
        public float2 A, B;
        public int SegA, SegB;
    }

    public static class PolygonSplitter
    {
        /// <summary>
        /// Splits a CCW ring along the chord A (on edge segA) → B (on edge segB). `left` is the part on
        /// the left of A→B (the ring walked from B back to A), `right` the other one.
        /// </summary>
        public static bool SplitByChord(float2[] ring, Chord chord, out float2[] left, out float2[] right)
        {
            left = right = null;
            int n = ring.Length;
            if (chord.SegA == chord.SegB || math.distance(chord.A, chord.B) < 0.5f) return false;
            if (!ChordInside(ring, chord)) return false;
            // Walking the CCW ring from A to B keeps the interior on the left of the ring, i.e. that
            // part lies to the right of the chord direction A→B.
            var r = new List<float2> { chord.A };
            for (int i = (chord.SegA + 1) % n; ; i = (i + 1) % n)
            {
                r.Add(ring[i]);
                if (i == chord.SegB) break;
            }
            r.Add(chord.B);
            var l = new List<float2> { chord.B };
            for (int i = (chord.SegB + 1) % n; ; i = (i + 1) % n)
            {
                l.Add(ring[i]);
                if (i == chord.SegA) break;
            }
            l.Add(chord.A);
            left = Poly.Clean(l, 0.05f);
            right = Poly.Clean(r, 0.05f);
            return left.Length >= 3 && right.Length >= 3;
        }

        /// <summary>No proper crossing with the outline and the midpoint inside.</summary>
        public static bool ChordInside(float2[] ring, Chord c)
        {
            int n = ring.Length;
            for (int i = 0; i < n; i++)
            {
                if (i == c.SegA || i == c.SegB) continue;
                if (ProperIntersect(c.A, c.B, ring[i], ring[(i + 1) % n])) return false;
            }
            for (int s = 1; s < 8; s++)
            {
                var p = math.lerp(c.A, c.B, s / 8f);
                if (!Poly.Contains(ring, p) && Poly.DistanceToBoundary(ring, p) > 0.05f) return false;
            }
            return true;
        }

        private static bool ProperIntersect(float2 a, float2 b, float2 c, float2 d)
        {
            const float eps = 1e-5f;
            float abC = Poly.Cross(b - a, c - a), abD = Poly.Cross(b - a, d - a);
            float cdA = Poly.Cross(d - c, a - c), cdB = Poly.Cross(d - c, b - c);
            return ((abC > eps && abD < -eps) || (abC < -eps && abD > eps)) && ((cdA > eps && cdB < -eps) || (cdA < -eps && cdB > eps));
        }

        /// <summary>
        /// The longest inside chord of the line {p : dot(p, n) = s}. Crossings use a half-open rule,
        /// so a line through a vertex is counted once and chords pair up correctly.
        /// </summary>
        public static bool LineChord(float2[] ring, float2 normal, float s, out Chord chord)
        {
            chord = default;
            var dir = new float2(-normal.y, normal.x);
            var hits = new List<(float u, float2 p, int seg)>();
            int n = ring.Length;
            for (int i = 0; i < n; i++)
            {
                var a = ring[i]; var b = ring[(i + 1) % n];
                float da = math.dot(a, normal) - s, db = math.dot(b, normal) - s;
                if ((da > 0f) == (db > 0f)) continue;
                float t = da / (da - db);
                var p = math.lerp(a, b, t);
                hits.Add((math.dot(p, dir), p, i));
            }
            if (hits.Count < 2) return false;
            hits.Sort((x, y) => x.u.CompareTo(y.u));
            float best = 0f;
            for (int k = 0; k + 1 < hits.Count; k += 2)
            {
                float len = hits[k + 1].u - hits[k].u;
                if (len <= best) continue;
                best = len;
                chord = new Chord { A = hits[k].p, SegA = hits[k].seg, B = hits[k + 1].p, SegB = hits[k + 1].seg };
            }
            return best > 0.5f;
        }

        /// <summary>
        /// Cuts `ring` with a line of the given normal so the part on the low side (dot &lt; s) has
        /// `targetArea` (binary search on s). Returns low and high parts.
        /// </summary>
        public static bool SplitByArea(float2[] ring, float2 normal, float targetArea, out float2[] low, out float2[] high)
        {
            low = high = null;
            float min = float.MaxValue, max = float.MinValue;
            foreach (var p in ring) { float d = math.dot(p, normal); min = math.min(min, d); max = math.max(max, d); }
            float lo = min, hi = max;
            float2[] bestLow = null, bestHigh = null;
            float bestErr = float.MaxValue;
            for (int iter = 0; iter < 48; iter++)
            {
                float s = (lo + hi) * 0.5f;
                if (!LineChord(ring, normal, s, out var chord) || !SplitByChord(ring, chord, out var left, out var right))
                {
                    // Degenerate position (tangent to a vertex): nudge.
                    s += (hi - lo) * 1e-3f;
                    if (!LineChord(ring, normal, s, out chord) || !SplitByChord(ring, chord, out left, out right)) { lo = s; continue; }
                }
                // The left/right labelling follows chord direction; classify by centroid instead.
                bool leftIsLow = math.dot(Poly.Centroid(left), normal) < math.dot(Poly.Centroid(right), normal);
                var l = leftIsLow ? left : right; var h = leftIsLow ? right : left;
                float area = Poly.Area(l);
                float err = math.abs(area - targetArea);
                if (err < bestErr) { bestErr = err; bestLow = l; bestHigh = h; }
                if (err <= targetArea * 1e-4f) break;
                if (area < targetArea) lo = s; else hi = s;
            }
            low = bestLow; high = bestHigh;
            return low != null;
        }
    }
}
