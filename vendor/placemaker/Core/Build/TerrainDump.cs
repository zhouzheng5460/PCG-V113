// Placemaker — in-game terrain dump (s30, GPL-3.0).
//
// Playtests 54–57 showed glitches the offline gallery never did (curtains where nets met the ground, quay pieces at
// different heights). With "Debug: dump terrain after build" on, every finished build writes what the game really
// built to ModsData/Placemaker/debug/<group>.json: the live heightmap and water depth around the build (sampled from
// TerrainSystem / WaterSystem after Apply), the ground before the build, the planned terraform raster's target, the
// built nets (live curves) and objects, the plan's facts. The gallery renders such a file like its own shore renders
// (hillshade, oblique view, cross sections, curtain and seat metrics): `--dump <file.json>`.
//
// Engine-free (the game samples, this writes and reads); a version number first so the format can grow.

using System;
using System.Collections.Generic;
using System.Globalization;
using Placemaker.Core.Tool;
using Unity.Mathematics;

namespace Placemaker.Core.Build
{
    public sealed class TerrainDump
    {
        public const int CurrentVersion = 1;

        public int Version = CurrentVersion;
        public string Group = "", Name = "", When = "";
        /// <summary>Grid: lower-left corner, cell size (m), size.</summary>
        public float2 Origin;
        public float Cell = 2f;
        public int Nx, Ny;
        /// <summary>Live ground after the build, the ground before it, the planned target (NaN = untouched), water depth.</summary>
        public float[] After = Array.Empty<float>(), Before = Array.Empty<float>(), Planned = Array.Empty<float>(), Water = Array.Empty<float>();
        public float WaterLevel = float.NaN;
        public readonly List<Net> Nets = new List<Net>();
        public readonly List<Obj> Objects = new List<Obj>();
        public readonly List<KeyValuePair<string, double>> Facts = new List<KeyValuePair<string, double>>();
        /// <summary>The build's outline(s) (xz rings).</summary>
        public readonly List<float2[]> Outlines = new List<float2[]>();

        public sealed class Net
        {
            public float3 A, B, C, D;
            public float Width;
            public string Prefab = "";
            /// <summary>Terrain-following (true) or an explicit-height deck/bridge/quay.</summary>
            public bool Follow = true;
            public bool Quay;
        }

        public sealed class Obj
        {
            public float3 Position;
            public float Yaw;
            public string Prefab = "";
        }

        public int Count => Nx * Ny;
        public float2 Pos(int i, int j) => Origin + new float2(i, j) * Cell;

        /// <summary>Bilinear sample of one of the grids (NaN outside, or where a corner is NaN).</summary>
        public float Sample(float[] grid, float2 p)
        {
            if (grid == null || grid.Length != Count || Nx < 2 || Ny < 2) return float.NaN;
            float fx = (p.x - Origin.x) / Cell, fy = (p.y - Origin.y) / Cell;
            if (fx < 0f || fy < 0f || fx > Nx - 1 || fy > Ny - 1) return float.NaN;
            int i = math.min((int)fx, Nx - 2), j = math.min((int)fy, Ny - 2);
            float tx = fx - i, ty = fy - j;
            float a = grid[j * Nx + i], b = grid[j * Nx + i + 1], c = grid[(j + 1) * Nx + i], d = grid[(j + 1) * Nx + i + 1];
            return math.lerp(math.lerp(a, b, tx), math.lerp(c, d, tx), ty);
        }

        public Func<float2, float> AfterAt => p => Clamp(Sample(After, p), p, Before);
        public Func<float2, float> BeforeAt => p => Clamp(Sample(Before, p), p, After);

        private float Clamp(float v, float2 p, float[] other)
        {
            if (math.isfinite(v)) return v;
            float o = Sample(other, p);
            return math.isfinite(o) ? o : 0f;
        }

        /// <summary>Curtains (CS2 texel, off the nets) and net seats of the live ground: the same metrics as the tests.</summary>
        public string Metrics()
        {
            var nets = new List<(float3, float3, float3, float3, float, int)>();
            foreach (var n in Nets) nets.Add((n.A, n.B, n.C, n.D, math.max(0.5f, n.Width * 0.5f), n.Follow ? TerrainMetrics.SeatFollow : n.Quay ? TerrainMetrics.SeatQuay : TerrainMetrics.SeatDeck));
            var max = Origin + new float2(Nx - 1, Ny - 1) * Cell;
            var c = TerrainMetrics.Curtains(AfterAt, BeforeAt, Origin, max, TerrainMetrics.Texel, TerrainMetrics.OffNets(nets));
            var s = TerrainMetrics.Seats(nets, AfterAt);
            return $"{c}; seat {s}";
        }

        // ───────────── JSON ─────────────

        public string ToJson()
        {
            var o = JsonValue.Obj();
            o.Set("version", Version);
            o.Set("group", Group);
            o.Set("name", Name);
            o.Set("when", When);
            o.Set("origin", Vec(Origin));
            o.Set("cell", Cell);
            o.Set("nx", Nx);
            o.Set("ny", Ny);
            o.Set("waterLevel", math.isfinite(WaterLevel) ? JsonValue.Num(Math.Round(WaterLevel, 3)) : JsonValue.Null);
            o.Set("after", Grid(After));
            o.Set("before", Grid(Before));
            o.Set("planned", Grid(Planned));
            o.Set("water", Grid(Water));
            var outlines = JsonValue.Arr();
            foreach (var r in Outlines) { var a = JsonValue.Arr(); foreach (var p in r) a.Add(Vec(p)); outlines.Add(a); }
            o.Set("outlines", outlines);
            var nets = JsonValue.Arr();
            foreach (var n in Nets)
            {
                var j = JsonValue.Obj();
                j.Set("a", Vec(n.A)); j.Set("b", Vec(n.B)); j.Set("c", Vec(n.C)); j.Set("d", Vec(n.D));
                j.Set("width", Math.Round(n.Width, 2)); j.Set("prefab", n.Prefab); j.Set("follow", n.Follow); j.Set("quay", n.Quay);
                nets.Add(j);
            }
            o.Set("nets", nets);
            var objs = JsonValue.Arr();
            foreach (var b in Objects)
            {
                var j = JsonValue.Obj();
                j.Set("p", Vec(b.Position)); j.Set("yaw", Math.Round(b.Yaw, 3)); j.Set("prefab", b.Prefab);
                objs.Add(j);
            }
            o.Set("objects", objs);
            var facts = JsonValue.Obj();
            foreach (var kv in Facts) facts.Set(kv.Key, Math.Round(kv.Value, 4));
            o.Set("facts", facts);
            return o.Write(false);
        }

        public static TerrainDump FromJson(string text)
        {
            var o = JsonValue.Parse(text);
            var d = new TerrainDump
            {
                Version = (int)o["version"].AsNumber(1),
                Group = o["group"].AsString(), Name = o["name"].AsString(), When = o["when"].AsString(),
                Origin = Vec2(o["origin"]), Cell = (float)o["cell"].AsNumber(2), Nx = (int)o["nx"].AsNumber(), Ny = (int)o["ny"].AsNumber(),
                WaterLevel = o["waterLevel"].IsNull ? float.NaN : (float)o["waterLevel"].AsNumber(),
            };
            d.After = ReadGrid(o["after"]); d.Before = ReadGrid(o["before"]); d.Planned = ReadGrid(o["planned"]); d.Water = ReadGrid(o["water"]);
            foreach (var r in o["outlines"].Items) { var ring = new List<float2>(); foreach (var p in r.Items) ring.Add(Vec2(p)); d.Outlines.Add(ring.ToArray()); }
            foreach (var j in o["nets"].Items)
                d.Nets.Add(new Net
                {
                    A = Vec3(j["a"]), B = Vec3(j["b"]), C = Vec3(j["c"]), D = Vec3(j["d"]), Width = (float)j["width"].AsNumber(3),
                    Prefab = j["prefab"].AsString(), Follow = j["follow"].AsBool(true), Quay = j["quay"].AsBool(),
                });
            foreach (var j in o["objects"].Items)
                d.Objects.Add(new Obj { Position = Vec3(j["p"]), Yaw = (float)j["yaw"].AsNumber(), Prefab = j["prefab"].AsString() });
            foreach (var kv in o["facts"].Properties) d.Facts.Add(new KeyValuePair<string, double>(kv.Key, kv.Value.AsNumber()));
            return d;
        }

        private static JsonValue Vec(float2 v) => JsonValue.Arr().Add(Math.Round(v.x, 2)).Add(Math.Round(v.y, 2));
        private static JsonValue Vec(float3 v) => JsonValue.Arr().Add(Math.Round(v.x, 2)).Add(Math.Round(v.y, 3)).Add(Math.Round(v.z, 2));
        private static float2 Vec2(JsonValue a) => a.Count >= 2 ? new float2((float)a.Items[0].AsNumber(), (float)a.Items[1].AsNumber()) : 0f;
        private static float3 Vec3(JsonValue a) => a.Count >= 3 ? new float3((float)a.Items[0].AsNumber(), (float)a.Items[1].AsNumber(), (float)a.Items[2].AsNumber()) : 0f;

        /// <summary>A grid as one compact string of centimetres ("" = NaN), comma separated (a 200 × 200 grid stays ~250 kB).</summary>
        private static JsonValue Grid(float[] g)
        {
            if (g == null || g.Length == 0) return JsonValue.Str("");
            var sb = new System.Text.StringBuilder(g.Length * 5);
            for (int i = 0; i < g.Length; i++)
            {
                if (i > 0) sb.Append(',');
                if (math.isfinite(g[i])) sb.Append(((long)Math.Round(g[i] * 100.0)).ToString(CultureInfo.InvariantCulture));
            }
            return JsonValue.Str(sb.ToString());
        }

        private static float[] ReadGrid(JsonValue v)
        {
            var s = v.AsString();
            if (string.IsNullOrEmpty(s)) return Array.Empty<float>();
            var parts = s.Split(',');
            var g = new float[parts.Length];
            for (int i = 0; i < parts.Length; i++)
                g[i] = parts[i].Length == 0 ? float.NaN : long.Parse(parts[i], CultureInfo.InvariantCulture) / 100f;
            return g;
        }
    }
}
