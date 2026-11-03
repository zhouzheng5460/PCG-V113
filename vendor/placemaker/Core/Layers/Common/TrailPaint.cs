// Placemaker — paths painted into the terrain (s33, docs/TERRAIN-PAINT.md). GPL-3.0.
//
// A painted trail is a dirt stroke in the ground texture (frayed edge, breathing width, a trampled dry-grass verge)
// instead of a path net. Cims never walk on paint: a trail is either VISUAL ONLY or paired with an invisible
// pedestrian path net when the playset has one ("Invisible Pedestrian Path", vanilla but hidden; the PLT variants).
// Never the automatic invisible-path pool: it ranks the drivable car/road paths first. A desire line painted as worn
// ground goes through the same stroke (knockout: a lawn tint under it gives way, so it stays a path, not a smudge).

using System.Collections.Generic;
using Placemaker.Core.Paint;
using Unity.Mathematics;

namespace Placemaker.Core.Layers.Common
{
    public static class TrailPaint
    {
        /// <summary>Invisible pedestrian path prefabs, best first (only these: never a drivable invisible path).</summary>
        public static readonly string[] WalkableIds = { "Invisible Pedestrian Path", "PLT Invisible Pedestrian Path", "PLT Flat Invisible Pedestrian Path" };
        public const string VisualOnlyKey = "Placemaker.Paint.Issue.TrailVisualOnly";

        /// <summary>The invisible pedestrian path this playset has, or null.</summary>
        public static string WalkableNet(SiteContext site)
        {
            if (site?.Assets == null) return null;
            foreach (var id in WalkableIds) if (site.Assets.ContainsKey(id)) return id;
            return null;
        }

        /// <summary>
        /// Paints one trail along <paramref name="line"/>: a dirt stroke <paramref name="width"/> m wide (frayed, breathing),
        /// fading in over <paramref name="endFade"/> m at both ends (0 = ends join other paths), and a faint dry-grass verge.
        /// </summary>
        public static void Stroke(PaintKit kit, IReadOnlyList<float2> line, float width, float strength = 0.8f, float endFade = 0f, int layer = TerrainMaterials.Dirt)
        {
            if (kit == null || line == null || line.Count < 2) return;
            kit.Stroke(line, layer, strength, width, math.min(1f, width * 0.35f), 1f, 1f, endFade, 0.28f, math.max(4f, width * 2.5f), Falloff.Linear, knockout: true);
            kit.Stroke(line, TerrainMaterials.Dry, strength * 0.35f, width + 3.5f, 1.8f, 1f, 1f, endFade, 0.2f, 7f);
        }

        /// <summary>A shortcut people wore into the lawn between <paramref name="a"/> and <paramref name="b"/> (slight bow, faded ends).</summary>
        public static void DesireLine(PaintKit kit, float2 a, float2 b, float width, float strength, int layer, uint seed)
        {
            float len = math.distance(a, b);
            if (len < 2f) return;
            var nrm = new float2(-(b - a).y, (b - a).x) / len;
            var line = new List<float2>();
            float bow = (Hashing.Unit(seed) - 0.5f) * 0.08f * len;
            for (int i = 0; i <= 8; i++)
            {
                float t = i / 8f;
                line.Add(math.lerp(a, b, t) + nrm * bow * math.sin(t * math.PI));
            }
            kit.Stroke(line, layer, strength, width, math.min(0.9f, width * 0.4f), 1f, 1f, math.min(3f, len * 0.2f), 0.32f, 4f, Falloff.Linear, knockout: true);
        }
    }
}
