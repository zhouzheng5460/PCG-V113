// Placemaker — polygon with holes, the result type of every boolean / offset / split operation.

using System;
using System.Collections.Generic;
using Unity.Mathematics;

namespace Placemaker.Core.Geometry
{
    /// <summary>How the inside of a set of rings is decided.</summary>
    public enum FillRule
    {
        /// <summary>Inside where the winding number is not zero (orientation matters: holes run clockwise).</summary>
        NonZero = 0,
        /// <summary>Inside where an odd number of rings cover the point (orientation ignored).</summary>
        EvenOdd = 1,
        /// <summary>Inside where the winding number is positive (counter-clockwise rings add, clockwise rings cut).</summary>
        Positive = 2,
    }

    public enum BoolOp { Union = 0, Intersection = 1, Difference = 2, Xor = 3 }

    /// <summary>Join used where an offset edge leaves a gap (reflex corners of an inset, convex corners of an outset).</summary>
    public enum OffsetJoin { Miter = 0, Round = 1, Square = 2 }

    /// <summary>
    /// One connected polygon: a counter-clockwise outer ring and clockwise hole rings, all simple,
    /// implicitly closed, holes strictly inside the outer ring (they may touch it at single points
    /// only when produced from touching input). CS2 areas cannot have holes — use
    /// <see cref="SurfaceAcceptance.CutHoles(Region)"/> before emitting surfaces.
    /// </summary>
    public sealed class Region
    {
        public float2[] Outer;
        public readonly List<float2[]> Holes = new List<float2[]>();

        public Region(float2[] outer)
        {
            Outer = outer ?? Array.Empty<float2>();
        }

        public Region(float2[] outer, IEnumerable<float2[]> holes) : this(outer)
        {
            if (holes != null) Holes.AddRange(holes);
        }

        public bool HasHoles => Holes.Count > 0;

        /// <summary>Outer area minus hole areas (m²).</summary>
        public float Area
        {
            get
            {
                double a = math.abs(Poly.SignedAreaD(Outer));
                foreach (var h in Holes) a -= math.abs(Poly.SignedAreaD(h));
                return (float)math.max(0, a);
            }
        }

        public float Perimeter
        {
            get
            {
                float p = Poly.Perimeter(Outer);
                foreach (var h in Holes) p += Poly.Perimeter(h);
                return p;
            }
        }

        public bool Contains(float2 p)
        {
            if (!Poly.Contains(Outer, p)) return false;
            foreach (var h in Holes) if (Poly.Contains(h, p)) return false;
            return true;
        }

        /// <summary>Distance from p to the nearest ring (outer or hole).</summary>
        public float DistanceToBoundary(float2 p)
        {
            float d = Poly.DistanceToBoundary(Outer, p);
            foreach (var h in Holes) d = math.min(d, Poly.DistanceToBoundary(h, p));
            return d;
        }

        /// <summary>All rings, outer first. Orientation is preserved (outer CCW, holes CW).</summary>
        public IEnumerable<float2[]> Rings()
        {
            yield return Outer;
            foreach (var h in Holes) yield return h;
        }

        public Region Clone() => new Region((float2[])Outer.Clone(), Holes.ConvertAll(h => (float2[])h.Clone()));

        /// <summary>Forces outer CCW and holes CW (needed for <see cref="FillRule.NonZero"/>/<see cref="FillRule.Positive"/>).</summary>
        public Region Normalized()
        {
            var r = new Region(Poly.EnsureCcw(Outer));
            foreach (var h in Holes) r.Holes.Add(Poly.EnsureCw(h));
            return r;
        }

        public override string ToString() => $"Region({Outer.Length} pts, {Holes.Count} holes, {Area:F1} m²)";
    }
}
