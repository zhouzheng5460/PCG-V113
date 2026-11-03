// Placemaker — decals must match the colour of what they lie on (s28 decal art pass). GPL-3.0.
//
// User (screenshot 58): "Still seeing decals that are squares/shapes not meant to be. Decals should MATCH THE COLOUR of the
// thing they're on, across the board." Orange-brown dirt discs at path junctions and under lamps on a green lawn, a dark
// brown splotch over a light path. Soft filter (lead: "doesn't necessarily need to color match"): automatic decals must not clearly clash with the host ground — lawn →
// green grass tones only, paving → light neutral greys, asphalt → dark neutral greys, sand → sand tones, dirt → dirt
// tones. A decal's colour is its texture's measured mean (DecalColours.g.cs); host colours are the same texture space
// (CS2 grass, pavement, asphalt, golden sand and soil base maps). Compatible = close in brightness AND in hue/chroma; a
// decal much darker than its host (dark blots) is never compatible.

using Unity.Mathematics;

namespace Placemaker.Core.Decals
{
    public struct Rgb
    {
        public float R, G, B, Coverage;
        public Rgb(float r, float g, float b, float coverage = 1f) { R = r; G = g; B = b; Coverage = coverage; }
        public float Luma => 0.3f * R + 0.59f * G + 0.11f * B;
        /// <summary>Opponent chroma (R−G, G−B): hue and saturation independent of brightness.</summary>
        public float2 Chroma => new float2(R - G, G - B);
        public override string ToString() => $"({R:0},{G:0},{B:0})";
    }

    /// <summary>Ground a decal is meant to lie on, with its colour.</summary>
    public enum HostTone { Lawn = 0, Paving = 1, Asphalt = 2, Sand = 3, Dirt = 4 }

    public static partial class DecalColours
    {
        /// <summary>Host colours in base-map texture space (CS2 lawn, light pavement, asphalt, golden sand, soil).</summary>
        public static Rgb Host(HostTone t)
        {
            switch (t)
            {
                case HostTone.Lawn: return new Rgb(62, 70, 33);
                case HostTone.Paving: return new Rgb(135, 132, 125);
                case HostTone.Asphalt: return new Rgb(58, 58, 58);
                case HostTone.Sand: return new Rgb(132, 116, 96);
                default: return new Rgb(70, 52, 35);
            }
        }

        /// <summary>How much darker (0..255) than its host a decal may be before it reads as a dark blot (not on asphalt).</summary>
        public const float MaxDarker = 60f;
        /// <summary>How much brighter than its host a decal may be.</summary>
        public const float MaxBrighter = 45f;
        /// <summary>Below this coverage a decal is sparse specks (scattered leaves, grit) that read as texture, not a disc.</summary>
        public const float SpeckleCoverage = 0.1f;
        /// <summary>Largest chroma distance (0..255) a decal may have from its host.</summary>
        public const float MaxChromaDelta = 30f;

        /// <summary>The measured colour of a pack decal by prefab name; false when unknown.</summary>
        public static bool TryGet(string prefabName, out Rgb colour)
        {
            colour = default;
            if (string.IsNullOrEmpty(prefabName)) return false;
            // "<pack> <category> <item> Decal": try every split point of the name for a measured "<category> <item>".
            var name = prefabName.EndsWith(" Decal", System.StringComparison.Ordinal) ? prefabName.Substring(0, prefabName.Length - 6) : prefabName;
            for (int i = name.IndexOf(' '); i >= 0; i = name.IndexOf(' ', i + 1))
                if (Measured.TryGetValue(name.Substring(i + 1), out colour)) return true;
            return Measured.TryGetValue(name, out colour);
        }

        /// <summary>Lower-case lookup (classification plug-ins see lower-case names).</summary>
        public static bool TryGetIgnoreCase(string prefabName, out Rgb colour)
        {
            if (TryGet(prefabName, out colour)) return true;
            colour = default;
            if (string.IsNullOrEmpty(prefabName)) return false;
            if (_lower == null)
            {
                var d = new System.Collections.Generic.Dictionary<string, Rgb>(System.StringComparer.Ordinal);
                foreach (var kv in Measured) d[kv.Key.ToLowerInvariant()] = kv.Value;
                _lower = d;
            }
            var name = prefabName.ToLowerInvariant();
            if (name.EndsWith(" decal", System.StringComparison.Ordinal)) name = name.Substring(0, name.Length - 6);
            for (int i = name.IndexOf(' '); i >= 0; i = name.IndexOf(' ', i + 1))
                if (_lower.TryGetValue(name.Substring(i + 1), out colour)) return true;
            return _lower.TryGetValue(name, out colour);
        }

        private static System.Collections.Generic.Dictionary<string, Rgb> _lower;

        /// <summary>(s28, lead: colour match is a soft filter, not a hard rule) False only for a clear clash with the host
        /// ground (dark brown or orange on lawn or light paving); decals with no measured colour pass (they are hyper
        /// transparent anyway).</summary>
        public static bool NoClash(string prefabName, HostTone host) => !TryGetIgnoreCase(prefabName, out var c) || Compatible(c, host);

        /// <summary>True when a decal of this colour blends into the host ground.</summary>
        public static bool Compatible(Rgb decal, HostTone host)
        {
            if (decal.Coverage < SpeckleCoverage) return true;
            var h = Host(host);
            float dl = decal.Luma - h.Luma;
            if (dl > MaxBrighter) return false;
            if (dl < -MaxDarker && host != HostTone.Asphalt) return false;   // stains darker than asphalt are just stains
            return math.distance(decal.Chroma, h.Chroma) <= MaxChromaDelta;   // hue clash: orange/brown on green, brown on grey
        }
    }
}
