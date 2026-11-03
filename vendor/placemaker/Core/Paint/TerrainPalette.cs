// Placemaker — the map's terrain paint materials as a palette (s33, docs/TERRAIN-PAINT.md). GPL-3.0.
//
// CS2 1.6.2f1 has exactly four paintable terrain materials ("Terrain Material Extra 1..4", TerrainMaterialType
// Extra1..Extra4) plus the Clear tool. Their textures come from the map's TerrainRenderSettingsPrefab
// (m_Extra1..4Diffuse); every vanilla map uses the one shipped prefab "DefaultTerrainRenderSettings", whose slots
// hold the temperate set measured below (BC7 albedo, mean of the 64² mip, sRGB). A custom map can pick ANY texture
// for any slot in the editor (Environment → Terrain rendering → duplicate & edit), e.g. the arid / mediterranean /
// tropic / alpine sets that ship in Blob_Environment, or its own. Mods cannot add a fifth material: the user
// splatmap stores four 4-bit weights (R = Extra1 | Extra2 << 4, G = Extra3 | Extra4 << 4).
//
// Plans keep writing the vanilla INTENT (TerrainMaterials 1 Dry grass, 2 Lush = forest floor, 3 Dirt, 4 Sand). The
// game captures the current map's slots (texture names + measured mean colours) into SiteContext.Terrain; Remap then
// sends every intent to the slot that matches it on this map — by texture name first ("sand", "dirt", "forest"…),
// else by the closest colour (CIE Lab). On a vanilla map Remap is the identity.

using System;
using System.Collections.Generic;
using System.Text;
using Unity.Mathematics;

namespace Placemaker.Core.Paint
{
    /// <summary>What a paint should look like (the planner's intent). Values = the vanilla slot (TerrainMaterials).</summary>
    public enum GroundTone { Dry = 1, Lush = 2, Dirt = 3, Sand = 4 }

    /// <summary>One paintable material slot of the current map.</summary>
    public sealed class TerrainSlot
    {
        /// <summary>1..4 = Extra1..Extra4 (the Placemaker paint layer).</summary>
        public int Index;
        /// <summary>Diffuse texture asset name ("Grass_Dry_Temperate_01_Basecolor"), "" when unknown.</summary>
        public string Texture = "";
        /// <summary>Mean albedo, sRGB 0..1.</summary>
        public float3 Color;
        /// <summary>Shader blend settings of the slot (TerrainRenderSettingsPrefab m_ExtraN*): how it sits on the base.</summary>
        public float DirtOverride, SplatRange = 1f, BlurDepth = 0.1f, HeightOffset;
        /// <summary>Colour measured from the texture (or a known texture), false = assumed.</summary>
        public bool Measured;

        public TerrainSlot Clone() => (TerrainSlot)MemberwiseClone();
    }

    /// <summary>A texture measured for the census (docs/TERRAIN-PAINT.md).</summary>
    public struct KnownTexture
    {
        public string Name;
        public float3 Color;
        public KnownTexture(string name, int r, int g, int b) { Name = name; Color = new float3(r, g, b) / 255f; }
    }

    public sealed class TerrainPalette
    {
        /// <summary>Render settings prefab name ("DefaultTerrainRenderSettings"), "" = assumed vanilla.</summary>
        public string Name = "";
        /// <summary>Index 1..4; [0] unused (Clear).</summary>
        public readonly TerrainSlot[] Slots = new TerrainSlot[5];
        /// <summary>Procedural base layers (grass on flat ground, dirt on uneven ground, rock on slopes), sRGB 0..1.</summary>
        public float3 BaseGrass, BaseDirt, BaseRock;

        // ── census (1.6.2f1, BC7 albedo means) ──
        public static readonly float3 VanillaGrass = new float3(68, 75, 54) / 255f;    // Grass_temperate_01
        public static readonly float3 VanillaDirt = new float3(97, 89, 82) / 255f;     // Dirt_temperate_01
        public static readonly float3 VanillaRock = new float3(106, 100, 94) / 255f;   // Rock_temperate_01

        /// <summary>Every terrain diffuse that ships with 1.6.2f1 (Blob_Environment), measured. Name match is case-insensitive.</summary>
        public static readonly KnownTexture[] Known =
        {
            new KnownTexture("Grass_Dry_Temperate_01_Basecolor", 79, 79, 64),
            new KnownTexture("Forest_temperate_01_Basecolor", 59, 62, 39),
            new KnownTexture("Light_dirt_temperate_01_Basecolor", 97, 89, 78),
            new KnownTexture("Sand_temperate_01_Basecolor", 119, 112, 102),
            new KnownTexture("Grass_temperate_01_Basecolor", 68, 75, 54),
            new KnownTexture("Grass_fertile_temperate_01_Basecolor", 70, 81, 56),
            new KnownTexture("Dirt_temperate_01_Basecolor", 97, 89, 82),
            new KnownTexture("Rock_temperate_01_Basecolor", 106, 100, 94),
            new KnownTexture("Grass_arid_01_Basecolor", 129, 108, 77),
            new KnownTexture("Grass_dry_arid_01_Basecolor", 144, 114, 91),
            new KnownTexture("Grass_fertile_arid_01_Basecolor", 104, 94, 72),
            new KnownTexture("Dirt_arid_01_Basecolor", 162, 134, 116),
            new KnownTexture("Sand_arid_01_Basecolor", 161, 133, 120),
            new KnownTexture("Rock_arid_01_Basecolor", 124, 83, 62),
            new KnownTexture("Rocky_ground_arid_01_Basecolor", 138, 93, 71),
            new KnownTexture("Grass_mediterranean_01_Basecolor", 106, 97, 73),
            new KnownTexture("Grass_dry_mediterranean_01_Basecolor", 129, 105, 84),
            new KnownTexture("Grass_fertile_mediterranean_01_Basecolor", 95, 93, 67),
            new KnownTexture("Dirt_mediterranean_01_Basecolor", 139, 117, 101),
            new KnownTexture("Sand_mediterranean_01_Basecolor", 155, 133, 117),
            new KnownTexture("Rock_mediterranean_01_Basecolor", 140, 122, 109),
            new KnownTexture("Rockydirt_mediterranean_01_Basecolor", 133, 118, 104),
            new KnownTexture("Grass_tropic_01_Basecolor", 64, 72, 50),
            new KnownTexture("Grass_dry_tropic_01_Basecolor", 76, 75, 56),
            new KnownTexture("Forest_tropic_01_Basecolor", 59, 65, 41),
            new KnownTexture("Dirt_tropic_01_Basecolor", 101, 84, 70),
            new KnownTexture("Rocky_dirt_tropic_01_Basecolor", 92, 84, 71),
            new KnownTexture("Sand_tropic_01_Basecolor", 165, 151, 125),
            new KnownTexture("Rock_tropic_01_Basecolor", 99, 92, 84),
            new KnownTexture("Grass_dry_alpine_01_Basecolor", 77, 77, 62),
            new KnownTexture("Gravel_alpine_Basecolor_01", 97, 94, 88),
            new KnownTexture("Rocky_ground_alpine_01_Basecolor", 110, 109, 106),
            new KnownTexture("Sand_alpine_01_Basecolor", 115, 111, 104),
            new KnownTexture("Rock_alpine_01_Basecolor", 85, 84, 83),
            // Pre-1.6 (legacy format) base textures still in Blob.cok.
            new KnownTexture("Grass_Temperate_BaseColor", 53, 66, 35),
            new KnownTexture("Dirt_Temperate_BaseColor", 107, 99, 90),
            new KnownTexture("Cliff_Temperate_BaseColor", 99, 91, 80),
        };

        /// <summary>Known colour of a texture name (null when not in the census).</summary>
        public static float3? KnownColor(string texture)
        {
            if (string.IsNullOrEmpty(texture)) return null;
            foreach (var k in Known) if (string.Equals(k.Name, texture, StringComparison.OrdinalIgnoreCase)) return k.Color;
            return null;
        }

        /// <summary>The slots of DefaultTerrainRenderSettings (every vanilla map), with their shader blend settings.</summary>
        public static TerrainPalette Vanilla()
        {
            var p = new TerrainPalette { Name = "DefaultTerrainRenderSettings", BaseGrass = VanillaGrass, BaseDirt = VanillaDirt, BaseRock = VanillaRock };
            p.Slots[1] = new TerrainSlot { Index = 1, Texture = "Grass_Dry_Temperate_01_Basecolor", Color = new float3(79, 79, 64) / 255f, DirtOverride = 0f, SplatRange = 0.28f, BlurDepth = 0.18f, HeightOffset = -0.09f, Measured = true };
            p.Slots[2] = new TerrainSlot { Index = 2, Texture = "Forest_temperate_01_Basecolor", Color = new float3(59, 62, 39) / 255f, DirtOverride = 0f, SplatRange = 0.35f, BlurDepth = 0.22f, HeightOffset = 0.09f, Measured = true };
            p.Slots[3] = new TerrainSlot { Index = 3, Texture = "Light_dirt_temperate_01_Basecolor", Color = new float3(97, 89, 78) / 255f, DirtOverride = 0f, SplatRange = 0.30f, BlurDepth = 0.08f, HeightOffset = 0.20f, Measured = true };
            p.Slots[4] = new TerrainSlot { Index = 4, Texture = "Sand_temperate_01_Basecolor", Color = new float3(119, 112, 102) / 255f, DirtOverride = 0.48f, SplatRange = 0.16f, BlurDepth = 0.04f, HeightOffset = 0.07f, Measured = true };
            return p;
        }

        private static readonly TerrainPalette s_vanilla = Vanilla();

        /// <summary>The palette to plan with: the captured one when it has all four slots, else vanilla.</summary>
        public static TerrainPalette OrVanilla(TerrainPalette p) => p != null && p.Complete ? p : s_vanilla;

        public bool Complete
        {
            get { for (int i = 1; i <= 4; i++) if (Slots[i] == null) return false; return true; }
        }

        /// <summary>The slot textures are exactly the vanilla ones (Remap is the identity).</summary>
        public bool IsVanilla
        {
            get
            {
                if (!Complete) return true;
                for (int i = 1; i <= 4; i++)
                    if (!string.Equals(Slots[i].Texture, s_vanilla.Slots[i].Texture, StringComparison.OrdinalIgnoreCase)) return false;
                return true;
            }
        }

        /// <summary>Colour (sRGB 0..1) of a paint layer on this map: 1..4 = the slot, 0 = the procedural grass.</summary>
        public float3 ColorOf(int layer) => layer >= 1 && layer <= 4 && Slots[layer] != null ? Slots[layer].Color : BaseGrass;

        // ───────────────────────── intent → slot ─────────────────────────

        /// <summary>Texture-name words per tone, strongest first. "grass" alone is not a tone (plain grass = lush-ish).</summary>
        private static readonly string[][] ToneWords =
        {
            null,
            new[] { "dry", "straw", "hay", "meadow", "wither", "steppe" },
            new[] { "forest", "lush", "moss", "leaf", "leaves", "mulch", "fertile", "jungle", "grass" },
            new[] { "dirt", "soil", "mud", "earth", "loam", "trail" },
            new[] { "sand", "beach", "dune", "gravel", "pebble", "shingle" },
        };

        /// <summary>Vanilla colour of each tone: the target when no texture name says which slot is which.</summary>
        public static float3 ToneColor(GroundTone t) => s_vanilla.Slots[(int)t].Color;

        private int[] _map;

        /// <summary>The slot (1..4) that best shows <paramref name="tone"/> on this map.</summary>
        public int SlotFor(GroundTone tone)
        {
            var map = _map ?? (_map = BuildMap());
            int t = (int)tone;
            return t >= 1 && t <= 4 ? map[t] : t;
        }

        /// <summary>Plan layer (vanilla intent: 0 clear, 1 Dry, 2 Lush, 3 Dirt, 4 Sand) → this map's slot.</summary>
        public int Remap(int intent) => intent >= 1 && intent <= 4 ? SlotFor((GroundTone)intent) : intent;

        /// <summary>How well slot <paramref name="tone"/>'s pick matches the vanilla look (CIE76 ΔE; ~0 on vanilla maps, &gt; 25 = clearly different).</summary>
        public float Mismatch(GroundTone tone)
        {
            int s = SlotFor(tone);
            return Slots[s] == null ? 0f : DeltaE(Slots[s].Color, ToneColor(tone));
        }

        /// <summary>The slot whose colour is closest to <paramref name="srgb"/> (e.g. dark mulch).</summary>
        public int Nearest(float3 srgb)
        {
            int best = 1; float bd = float.MaxValue;
            for (int i = 1; i <= 4; i++)
            {
                if (Slots[i] == null) continue;
                float d = DeltaE(Slots[i].Color, srgb);
                if (d < bd - 1e-4f) { bd = d; best = i; }
            }
            return best;
        }

        private int[] BuildMap()
        {
            var map = new int[5];
            for (int t = 1; t <= 4; t++) map[t] = t;
            if (!Complete || IsVanilla) return map;
            var byName = new int[5];
            for (int t = 1; t <= 4; t++) byName[t] = NameMatch(t);
            for (int t = 1; t <= 4; t++)
            {
                if (byName[t] > 0) { map[t] = byName[t]; continue; }
                // Colour: the nearest slot, preferring one no other tone claimed by name (ties → the vanilla index).
                var target = ToneColor((GroundTone)t);
                int best = t; float bd = float.MaxValue;
                for (int i = 1; i <= 4; i++)
                {
                    float d = DeltaE(Slots[i].Color, target);
                    bool claimed = false;
                    for (int o = 1; o <= 4; o++) if (o != t && byName[o] == i) claimed = true;
                    if (claimed) d += 12f;
                    if (i == t) d -= 0.5f;
                    if (d < bd) { bd = d; best = i; }
                }
                map[t] = best;
            }
            return map;
        }

        /// <summary>The slot whose texture name says <paramref name="tone"/> (strongest word wins, 0 = none).</summary>
        private int NameMatch(int tone)
        {
            int best = 0, bestRank = int.MaxValue;
            for (int i = 1; i <= 4; i++)
            {
                string n = (Slots[i].Texture ?? "").ToLowerInvariant();
                if (n.Length == 0) continue;
                int rank = Rank(n, tone);
                if (rank < 0) continue;
                // A word of a stronger-claiming tone in the same name (e.g. "grass_dry": dry beats grass) disqualifies.
                bool other = false;
                for (int o = 1; o <= 4; o++) if (o != tone) { int r = Rank(n, o); if (r >= 0 && r < rank) other = true; }
                if (other) continue;
                if (rank < bestRank) { bestRank = rank; best = i; }
            }
            return best;
        }

        private static int Rank(string name, int tone)
        {
            var words = ToneWords[tone];
            for (int k = 0; k < words.Length; k++) if (name.Contains(words[k])) return k;
            return -1;
        }

        // ───────────────────────── colour ─────────────────────────

        /// <summary>CIE76 colour difference of two sRGB colours (0..1).</summary>
        public static float DeltaE(float3 a, float3 b) => math.distance(Lab(a), Lab(b));

        public static float3 Linear(float3 srgb)
        {
            float3 c = math.saturate(srgb);
            return math.select(math.pow((c + 0.055f) / 1.055f, 2.4f), c / 12.92f, c <= 0.04045f);
        }

        public static float3 Srgb(float3 linear)
        {
            float3 c = math.saturate(linear);
            return math.select(1.055f * math.pow(c, 1f / 2.4f) - 0.055f, c * 12.92f, c <= 0.0031308f);
        }

        public static float3 Lab(float3 srgb)
        {
            var l = Linear(srgb);
            float x = (0.4124f * l.x + 0.3576f * l.y + 0.1805f * l.z) / 0.95047f;
            float y = 0.2126f * l.x + 0.7152f * l.y + 0.0722f * l.z;
            float z = (0.0193f * l.x + 0.1192f * l.y + 0.9505f * l.z) / 1.08883f;
            float F(float t) => t > 0.008856f ? math.pow(t, 1f / 3f) : 7.787f * t + 16f / 116f;
            float fx = F(x), fy = F(y), fz = F(z);
            return new float3(116f * fy - 16f, 500f * (fx - fy), 200f * (fy - fz));
        }

        public static string Hex(float3 srgb)
        {
            var c = (int3)math.round(math.saturate(srgb) * 255f);
            return "#" + c.x.ToString("x2") + c.y.ToString("x2") + c.z.ToString("x2");
        }

        /// <summary>One log line: slot textures, colours and where each intent goes.</summary>
        public string Describe()
        {
            var sb = new StringBuilder();
            sb.Append(string.IsNullOrEmpty(Name) ? "(unknown settings)" : Name).Append(": ");
            for (int i = 1; i <= 4; i++)
            {
                var s = Slots[i];
                if (i > 1) sb.Append(", ");
                if (s == null) { sb.Append(i).Append("=?"); continue; }
                sb.Append(i).Append('=').Append(string.IsNullOrEmpty(s.Texture) ? "?" : s.Texture).Append(' ').Append(Hex(s.Color)).Append(s.Measured ? "" : "~");
            }
            sb.Append("; intents dry→").Append(SlotFor(GroundTone.Dry)).Append(" lush→").Append(SlotFor(GroundTone.Lush))
                .Append(" dirt→").Append(SlotFor(GroundTone.Dirt)).Append(" sand→").Append(SlotFor(GroundTone.Sand));
            return sb.ToString();
        }

        /// <summary>
        /// Sends every paint of the plan to this map's slot for its intent (no-op on vanilla maps). Run once per compose,
        /// after every layer and the dressing pass painted. Idempotence is not needed (called exactly once).
        /// </summary>
        public static void Remap(ComposedPlan plan, TerrainPalette palette)
        {
            if (plan?.Layers == null || palette == null || !palette.Complete || palette.IsVanilla) return;
            foreach (var l in plan.Layers)
                if (l?.Paint != null)
                    foreach (var p in l.Paint)
                        if (p != null && p.Layer >= 1 && p.Layer <= 4) p.Layer = palette.Remap(p.Layer);
        }
    }
}
