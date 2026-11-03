// s53 — stall marking size.
//
// The vanilla stall decals (ParkingLotDecal01 / ParkingLotElectricDecal01) have a 3.2 x 6.2 m collision box on a 3.0 x 5.9 m
// stall, so every neighbouring pair overlapped and the game reported OverlapExisting. The marking is scaled to fit its stall
// (a hair inside it) so rows of stalls do not meet even without "Ignore collisions in my builds".

using System;

namespace Placemaker.Core.Layers.Parking
{
    internal static class ParkingMarkings
    {
        /// <summary>Measured collision box of the regular and electric marking (the accessible one is 5.0 x 6.2).</summary>
        public const double RegularWidth = 3.2, RegularLength = 6.2, DisabledWidth = 5.0, DisabledLength = 6.2;

        /// <summary>Clear space left between neighbouring markings.</summary>
        public const double Gap = 0.03;

        /// <summary>Never shrink a marking below this share of its size (it would stop looking like a stall).</summary>
        public const double MinScale = 0.85;

        public static float Scale(bool disabled, double stallWidth, double stallLength)
        {
            double w = disabled ? DisabledWidth : RegularWidth, l = disabled ? DisabledLength : RegularLength;
            double s = Math.Min(1.0, Math.Min((stallWidth - Gap) / w, (stallLength - Gap) / l));
            if (double.IsNaN(s)) return 1f;
            return (float)Math.Max(MinScale, s);
        }
    }
}
