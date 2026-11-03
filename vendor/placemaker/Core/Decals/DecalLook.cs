// Placemaker — how strong a decal is drawn (s23 decal art pass). GPL-3.0.
//
// User: "a lot more transparent, blend with the terrain … most of them ~80 % transparent; some are fine like the sand
// ones". Each decorative pool has an opacity (DecalPools); the game builds see-through clones per opacity TIER
// (Game/Build/DecalOpacitySystem), so opacities are quantised to a few tiers to keep the clone count small.

using Unity.Mathematics;

namespace Placemaker.Core.Decals
{
    public static class DecalLook
    {
        /// <summary>Opacity tiers in percent (100 = the original prefab).</summary>
        public static readonly int[] Tiers = { 10, 15, 25, 35, 50, 100 };

        /// <summary>Nearest tier (percent) of an opacity 0..1.</summary>
        public static int Tier(float opacity)
        {
            if (!(opacity < 0.995f)) return 100;
            int best = Tiers[0];
            float bestD = float.MaxValue;
            foreach (var t in Tiers)
            {
                float d = math.abs(t / 100f - opacity);
                if (d < bestD) { bestD = d; best = t; }
            }
            return best;
        }

        // ── s37: names of the see-through clones ──
        // The 16:37 build named clones "Placemaker Decal 15% (<original>)" and their surface assets after them. The asset
        // database unescapes asset names with '%' as the escape character (Colossal.IO.AssetDatabase.Internal.EscapeUtils,
        // reserved <>:"/\|?*%.), so "% (" threw UriFormatException in ManagedBatchSystem.CreateMaterial every frame. Clone,
        // render-prefab and surface-asset names now hold only ASCII letters, digits, spaces, '-' and '_'.

        public const string ClonePrefix = "Placemaker Decal ";

        /// <summary>Only ASCII letters, digits, space, '-' and '_' (runs of anything else become one space). When anything had
        /// to change, a short stable hash of the original is appended so two originals never share a name.</summary>
        public static string SafeAssetName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "unnamed";
            var sb = new System.Text.StringBuilder(name.Length);
            bool changed = false, space = false;
            foreach (char c in name)
            {
                bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '-' || c == '_' || c == ' ';
                if (!ok) changed = true;
                if (!ok || c == ' ')
                {
                    if (!space && sb.Length > 0) sb.Append(' ');
                    space = true;
                    continue;
                }
                sb.Append(c);
                space = false;
            }
            var r = sb.ToString().Trim();
            if (r.Length == 0) r = "unnamed";
            if (changed) r += " " + Fnv(name).ToString("x8");
            return r;
        }

        /// <summary>Prefab name of the see-through clone of <paramref name="original"/> at <paramref name="tier"/> percent.</summary>
        public static string CloneName(string original, int tier) => ClonePrefix + tier + " - " + SafeAssetName(original);

        /// <summary>The 16:37-build name (kept as an obsolete identifier of the clone so builds saved with it still load).</summary>
        public static string LegacyCloneName(string original, int tier) => ClonePrefix + tier + "% (" + original + ")";

        private static uint Fnv(string s)
        {
            uint h = 2166136261u;
            foreach (char c in s) { h ^= c; h *= 16777619u; }
            return h;
        }

        /// <summary>Opacity a planned object is drawn with: decorative decals per their pool, everything else 1.</summary>
        public static float OpacityOf(PlannedObject o)
        {
            if (o == null || o.Role != ObjectRole.Decal || DecalCatalog.IsFunctionalCategory(o.Category)) return 1f;
            return DecalPools.Opacity(o.Category);
        }
    }
}
