// Stateless hashing and value noise for deterministic layout decisions.
//
// Value noise is ported from ParkingLotTool (KruemelGames, GPL-3.0),
// Geometry/ParkingVegetation.cs (GroupDensity) and ParkManager (tomkpunkt, GPL-3.0),
// Geometry/ParkDecorationPlanner.cs (BoundaryBelt noise). Hash-per-position instead of a
// shared random stream means a decision at one spot never shifts when an unrelated spot
// changes, which keeps re-plans visually stable while the player drags a slider.

using Unity.Mathematics;

namespace Placemaker.Core.Layers.Common
{
    public static class Hashing
    {
        /// <summary>lowbias32 integer finaliser: good avalanche, cheap, identical on every runtime.</summary>
        public static uint Mix(uint x)
        {
            unchecked
            {
                x ^= x >> 16; x *= 0x7feb352du;
                x ^= x >> 15; x *= 0x846ca68bu;
                x ^= x >> 16;
                return x;
            }
        }

        public static uint Mix(uint a, uint b) => Mix(a ^ Mix(unchecked(b + 0x9E3779B9u)));
        public static uint Mix(uint a, int b, int c) => Mix(Mix(a, unchecked((uint)b)), unchecked((uint)c * 0x85EBCA6Bu));

        /// <summary>Uniform [0,1) from a hash.</summary>
        public static float Unit(uint h) => (h & 0xFFFFFFu) / 16777216f;

        /// <summary>Non-zero seed for Unity.Mathematics.Random.</summary>
        public static Random Rng(uint seed, uint salt) => new Random(NonZero(Mix(seed, salt)));
        public static Random Rng(uint seed, string salt) => new Random(NonZero(Mix(seed, Seeds.Hash(salt))));

        public static uint NonZero(uint x) => x == 0 ? 0x6C8E9CF5u : x;
    }

    public static class ValueNoise
    {
        /// <summary>Smooth 2D value noise in [0,1]; one lattice cell per unit.</summary>
        public static float Sample(float2 p, uint seed)
        {
            int x = (int)math.floor(p.x), y = (int)math.floor(p.y);
            var t = p - new float2(x, y);
            t = t * t * (3f - 2f * t);
            float v00 = Lattice(x, y, seed), v10 = Lattice(x + 1, y, seed);
            float v01 = Lattice(x, y + 1, seed), v11 = Lattice(x + 1, y + 1, seed);
            return math.lerp(math.lerp(v00, v10, t.x), math.lerp(v01, v11, t.x), t.y);
        }

        /// <summary>Two octaves; reads as clumps of varying size rather than a regular blob pattern.</summary>
        public static float Clumps(float2 p, float scale, uint seed)
        {
            var q = p / math.max(0.01f, scale);
            return 0.7f * Sample(q, seed) + 0.3f * Sample(q * 2.7f + 17.3f, Hashing.Mix(seed, 0x2545F491u));
        }

        /// <summary>1D periodic value noise (wraps every <paramref name="period"/> cells), used along perimeters.</summary>
        public static float Periodic(float x, int period, uint seed)
        {
            period = math.max(1, period);
            int cell = (int)math.floor(x);
            float f = x - cell;
            f = f * f * (3f - 2f * f);
            int a = ((cell % period) + period) % period, b = (a + 1) % period;
            return math.lerp(Hashing.Unit(Hashing.Mix(seed, (uint)a)), Hashing.Unit(Hashing.Mix(seed, (uint)b)), f);
        }

        private static float Lattice(int x, int y, uint seed) =>
            Hashing.Unit(Hashing.Mix(seed, x, y));
    }
}
