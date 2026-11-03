// PlacemakerCore 驱动器（阶段 5）：把一次 Plan 的结果导成**通用 JSON**，供外部引擎（TownForge）消费。
//
// 为什么需要它：Core 的输出是 7 条通道（surfaces / nets / objects / areas / regions / zoning / paint），
// 每条通道的对象都带着 CS2 特有的字段（AssetRef、Control、Category、Upgrades…）。
// 外部引擎一个都用不上，它要的是"给我一堆环和点"。
// 所以这里做一次**有损但稳定**的降维：只保留几何 + 角色，其余丢掉。
//
// 协议上有两条硬约定：
//   1) 单位一律是**米**。Core 的 MinArea / CheckReady / minApart 全是米制，换算必须在调用方做，
//      不能在这里做 —— 否则"这个地块能不能跑"和"这块地多大"会来自两套单位。
//   2) 平面坐标一律写成 [x, z]。Core 的 float3 里 y 是高度，xz 才是平面；
//      拍平成二维时只取 xz，高度单列在各自的 y 字段里。
//
// 用法：
//   PlacemakerDriver --emit <case.json> <plan.json>
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using Unity.Mathematics;
using Placemaker.Core;

namespace Placemaker.Driver
{
    /// <summary>一次 emit 的结果摘要（给控制台看）。</summary>
    internal sealed class EmitReport
    {
        public string Generator = "";
        public uint Seed;
        public double Area;
        public bool Ready = true;
        public string ReadyKey = "";
        public int AutoMarkers, GivenMarkers;
        public int Surf, Nets, Obj, Areas, Reg, Zone, Paint, Terr;
        /// <summary>传进来的障碍物条数（建筑轮廓，见 CaseJson 的 obstacles）。</summary>
        public int Obs;
        /// <summary>SiteSafety 的统计：压在地界内的建筑数、被裁开的环、被整条丢弃的条目。</summary>
        public int BldInArea, SafetyCut, SafetyDrop, SafetyObj, SafetySurf;
        /// <summary>
        /// RoleName 落到兜底 "other" 的次数，按通道分开记。
        /// Core 新加一个枚举值而这里没跟上时，消费方会静默丢掉该元素——所以这个数必须能被看见。
        /// </summary>
        public int UnkSurf, UnkNet, UnkObj, UnkArea;
        public int UnkTotal => UnkSurf + UnkNet + UnkObj + UnkArea;
        public int Err, Warn;
        public long Ms;
        public string Error = "";
    }

    internal static class Emit
    {
        private static readonly CultureInfo INV = CultureInfo.InvariantCulture;
        private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

        // ---------------------------------------------------------------- 入口
        public static int Run(string casePath, string outPath)
        {
            if (!File.Exists(casePath))
            {
                Console.Error.WriteLine("[emit] 找不到输入文件: " + casePath);
                return 2;
            }

            var rep = new EmitReport();
            AreaPlan plan = null;
            AreaInput input = null;
            IAreaGenerator gen = null;
            string json;
            // 留着给 SerializeInput：JsonElement 随 doc 释放而失效，必须提前取成字符串
            string waterRaw = null;

            try
            {
                using (var doc = JsonDocument.Parse(File.ReadAllText(casePath, Encoding.UTF8)))
                {
                    var root = doc.RootElement;
                    if (root.TryGetProperty("water", out var wEl) && wEl.ValueKind == JsonValueKind.Object)
                        waterRaw = wEl.GetRawText();
                    string id = Str(root, "generator", "");
                    rep.Generator = id;
                    if (string.IsNullOrEmpty(id))
                    {
                        Console.Error.WriteLine("[emit] 缺少 \"generator\" 字段");
                        return 2;
                    }

                    gen = Find(id);
                    if (gen == null)
                    {
                        Console.Error.WriteLine("[emit] 没有 id 为 \"" + id + "\" 的生成器（大小写敏感）");
                        return 3;
                    }

                    input = BuildInput(gen, root, rep);
                    rep.Area = Math.Abs(Pricing.PolygonArea(input.Shape.Ring));
                    rep.GivenMarkers = input.Markers.Count;

                    // 顺序很重要：先让生成器按临街情况建议出入口，再判 CheckReady。
                    // 反过来的话，parking 这类"必须有一个车行口"的图层在用户还没点出入口时
                    // 一律 not ready —— 而游戏里工具一打开就已经把建议口画出来了，此刻是 ready 的。
                    var sug = gen.SuggestMarkers(input);
                    if (sug != null && sug.Count > 0)
                    {
                        if (input.Markers.Count == 0) input.Markers.AddRange(sug);
                        rep.AutoMarkers = sug.Count;
                    }

                    var ready = gen.CheckReady(input);
                    rep.Ready = ready.Ready;
                    rep.ReadyKey = ready.Ready ? "" : (ready.NextStepKey + "|" + ready.ReasonKey);

                    if (ready.Ready)
                    {
                        using (var cts = new CancellationTokenSource(Budget))
                            plan = gen.Plan(input, cts.Token);

                        // 障碍物避让只在 Composer.Compose 的末尾跑（placemaker-master/Core/Composition.cs:229），
                        // 而单图层走的是 gen.Plan()，那条路径根本不经过 composer —— 障碍物收下了却没人看。
                        // 这里把这一层包成 ComposedPlan 再补跑同一趟 SiteSafety，行为和游戏里一致：
                        // 压到建筑地界的路/对象/铺装/区域被丢弃或裁开。
                        if (plan != null && input.Site.Obstacles.Count > 0)
                        {
                            var composed = new ComposedPlan();
                            composed.Layers.Add(plan);
                            var safe = Placemaker.Core.Build.SiteSafety.Apply(composed, input.Shape, input.Site);
                            rep.BldInArea = safe.BuildingsInArea;
                            rep.SafetyCut = safe.Cut;
                            rep.SafetyDrop = safe.LeftOut;
                            rep.SafetyObj = safe.Objects;
                            rep.SafetySurf = safe.Surfaces;
                        }
                    }

                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    json = Serialize(root, gen, input, plan, rep, sw);
                    sw.Stop();
                    rep.Ms = sw.ElapsedMilliseconds;
                }
            }
            catch (OperationCanceledException)
            {
                rep.Error = "超过 " + Budget.TotalSeconds.ToString("0", INV) + "s 未完成";
                json = ErrorJson(rep);
            }
            catch (Exception ex)
            {
                rep.Error = Flatten(ex);
                json = ErrorJson(rep);
            }

            string dir = Path.GetDirectoryName(Path.GetFullPath(outPath));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(outPath, json, new UTF8Encoding(false));

            // 同一份输入 → 两个后端。所以 oracle 必须把自己的解析结果也落盘（见 SerializeInput 的注释）
            if (input != null && gen != null)
            {
                string inputPath = outPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                    ? outPath.Substring(0, outPath.Length - 5) + ".input.json"
                    : outPath + ".input.json";
                File.WriteAllText(inputPath, SerializeInput(gen, input, waterRaw), new UTF8Encoding(false));
            }

            Console.WriteLine("[emit] " + rep.Generator + "  seed=" + rep.Seed +
                              "  area=" + rep.Area.ToString("0", INV) + "m²" +
                              "  ready=" + (rep.Ready ? "yes" : "no " + rep.ReadyKey) +
                              "  surf=" + rep.Surf + " net=" + rep.Nets + " obj=" + rep.Obj +
                              " decal=" + "" + " zone=" + rep.Zone + " paint=" + rep.Paint +
                              (rep.Obs > 0 ? " obs=" + rep.Obs : "") +
                              // 注意这里是**障碍块数**，不是栋数：多角楼会拆成好几块，Core 只按质心 4 m 去重
                              (rep.Obs > 0 ? "  避让: 压界 " + rep.BldInArea + " 块, 丢 " + rep.SafetyDrop + " 条 / 裁 " + rep.SafetyCut + " 环" : "") +
                              (rep.UnkTotal > 0 ? "  !! 未知角色 surf=" + rep.UnkSurf + " net=" + rep.UnkNet + " obj=" + rep.UnkObj + " area=" + rep.UnkArea : "") +
                              "  " + rep.Ms + "ms" + (rep.Error.Length > 0 ? "  ERROR " + rep.Error : ""));
            if (rep.UnkTotal > 0)
                Console.Error.WriteLine("[emit] 警告: " + rep.Generator + " 产出 " + rep.UnkTotal +
                                        " 个角色名未映射的元素（Core 加了新枚举值而 Driver 没跟上）。" +
                                        "消费方会丢弃它们，请补 RoleName/_ => \"other\" 的对端处理。");
            Console.WriteLine("[out] " + Path.GetFullPath(outPath));
            return rep.Error.Length > 0 ? 1 : 0;
        }

        // ---------------------------------------------------------------- 取生成器
        /// <summary>
        /// 按 id 单独实例化，而不是走 GeneratorRegistry.All —— 后者用 Activator.CreateInstance 一次建全部，
        /// 任何一个 ctor 抛异常就整体失败（阶段 3 踩过）。这里只碰目标那一个。
        /// </summary>
        private static IAreaGenerator Find(string id)
        {
            var asm = typeof(IAreaGenerator).Assembly;
            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray(); }

            foreach (var t in types)
            {
                if (t.IsAbstract || t.IsInterface || t.IsGenericTypeDefinition) continue;
                if (!typeof(IAreaGenerator).IsAssignableFrom(t)) continue;
                if (Attribute.GetCustomAttribute(t, typeof(AreaGeneratorAttribute)) == null) continue;
                if (t.GetConstructor(Type.EmptyTypes) == null) continue;
                try
                {
                    var g = (IAreaGenerator)Activator.CreateInstance(t);
                    if (g.Id == id) return g;
                }
                catch { /* 构造失败的生成器直接跳过，找下一个 */ }
            }
            return null;
        }

        // ---------------------------------------------------------------- 输入
        private static AreaInput BuildInput(IAreaGenerator gen, JsonElement root, EmitReport rep)
        {
            var shape = new AreaShape { Source = AreaSource.Drawn };
            shape.Ring = ReadRing(root, "shape") ?? Rect(60f, 40f);

            var site = new SiteContext
            {
                LayoutScale = 1f,
                EdgeMargin = SiteContext.DefaultEdgeMargin,
            };

            if (root.TryGetProperty("streets", out var streets) && streets.ValueKind == JsonValueKind.Array)
                foreach (var s in streets.EnumerateArray())
                    site.NearbyNets.Add(ReadStreet(s));

            if (root.TryGetProperty("zoneGrids", out var grids) && grids.ValueKind == JsonValueKind.Array)
                foreach (var g in grids.EnumerateArray())
                    site.ZoneGrids.Add(ReadZoneGrid(g));

            // 建筑轮廓。给了就等于告诉 Core「这些东西不许碰」——
            // Composer.Compose 末尾会调 SiteSafety.Apply，按这批四边形把路、对象、
            // 铺装、区域、地形、粉刷全部裁掉或丢弃（见 placemaker-master/Core/Build/SiteSafety.cs）。
            // 不给 = 这块地整个换掉，旧建筑跟着消失。
            if (root.TryGetProperty("obstacles", out var obs) && obs.ValueKind == JsonValueKind.Array)
                foreach (var o in obs.EnumerateArray())
                {
                    var ob = ReadObstacle(o);
                    if (ob.HasValue) { site.Obstacles.Add(ob.Value); rep.Obs++; }
                }

            if (root.TryGetProperty("water", out var w) && w.ValueKind == JsonValueKind.Object)
            {
                // 只有一档：整块地以北/以东某条线为界，界外是水。够用且能被 JSON 表达。
                char axis = Str(w, "axis", "z")[0];
                float at = (float)Num(w, "at", 0);
                float depth = (float)Num(w, "depth", 1.6f);
                bool above = Str(w, "side", "above") == "above";
                site.WaterDepthAt = p =>
                {
                    float v = axis == 'z' ? p.y : p.x;
                    bool inside = above ? v > at : v < at;
                    return inside ? depth : 0f;
                };
            }

            var bag = gen.Defaults();
            if (root.TryGetProperty("settings", out var st) && st.ValueKind == JsonValueKind.Object)
                foreach (var p in st.EnumerateObject())
                    bag.Values[p.Name] = ReadSetting(p.Value);

            uint seed = (uint)Num(root, "seed", 1234);
            rep.Seed = seed;

            var input = new AreaInput
            {
                Shape = shape,
                Site = site,
                Settings = bag,
                Seed = seed,
                RegionTags = ReadStringArray(root, "regionTags") ?? new[] { "root" },
            };

            if (root.TryGetProperty("markers", out var mk) && mk.ValueKind == JsonValueKind.Array)
                foreach (var m in mk.EnumerateArray())
                    input.Markers.Add(ReadMarker(m, shape.Ring));

            if (root.TryGetProperty("childTargets", out var ct) && ct.ValueKind == JsonValueKind.Array)
                input.ChildTargets = ReadStringArray(root, "childTargets");
            if (root.TryGetProperty("childLayers", out var cl) && cl.ValueKind == JsonValueKind.Array)
                input.ChildLayers = ReadStringArray(root, "childLayers");

            return input;
        }

        /// <summary>环支持三种写法：[x,z] 数组、{x,z} 对象、{x,y,z} 对象（y 忽略）。</summary>
        private static float3[] ReadRing(JsonElement root, string name)
        {
            if (!root.TryGetProperty(name, out var sh)) return null;
            JsonElement arr;
            if (sh.ValueKind == JsonValueKind.Object)
            {
                if (!sh.TryGetProperty("ring", out arr)) return null;
            }
            else arr = sh;

            if (arr.ValueKind != JsonValueKind.Array) return null;
            var list = new List<float3>();
            foreach (var p in arr.EnumerateArray())
            {
                if (p.ValueKind == JsonValueKind.Array && p.GetArrayLength() >= 2)
                    list.Add(new float3(p[0].GetSingle(), 0f, p[1].GetSingle()));
                else if (p.ValueKind == JsonValueKind.Object)
                    list.Add(new float3((float)Num(p, "x", 0), 0f, (float)Num(p, "z", 0)));
            }
            return list.Count >= 3 ? list.ToArray() : null;
        }

        private static NearbyNet ReadStreet(JsonElement s)
        {
            var a = Vec2(s, "a") ?? new float2(-20f, -8f);
            var b = Vec2(s, "b") ?? new float2(20f, -8f);
            var A = new float3(a.x, 0f, a.y);
            var D = new float3(b.x, 0f, b.y);
            var cat = Str(s, "category", "road");
            return new NearbyNet
            {
                ExternalId = (long)Num(s, "id", 1),
                A = A,
                B = A + (D - A) / 3f,
                C = A + (D - A) * (2f / 3f),
                D = D,
                Width = (float)Num(s, "width", 8f),
                Category = cat == "pathway" ? NetCategory.Pathway
                         : cat == "track" ? NetCategory.Track
                         : cat == "other" ? NetCategory.Other
                         : NetCategory.Road,
                HasSidewalk = Bool(s, "sidewalk", true),
                IsOneWay = Bool(s, "oneWay", false),
                IsHighway = Bool(s, "highway", false),
                SpeedLimit = (float)Num(s, "speed", 50f),
            };
        }

        /// <summary>
        /// 一条障碍物：`{ "quad": [[x,z],[x,z],[x,z],[x,z]], "kind": "building" }`。
        ///
        /// Core 的 Obstacle 就是一条定向四边形（A/B/C/D），不是任意多边形，所以多出来的形状
        /// 得在调用侧先拆开（见 tools/bake-plan.ts 的 buildingQuads）。这里只做读取和校验：
        /// 点不够、或者面积小到没意义的直接丢掉 —— SiteSafety 反正也会无视 &lt; 0.5 m² 的。
        /// </summary>
        private static Obstacle? ReadObstacle(JsonElement o)
        {
            if (o.ValueKind != JsonValueKind.Object) return null;
            if (!o.TryGetProperty("quad", out var q) || q.ValueKind != JsonValueKind.Array) return null;
            if (q.GetArrayLength() < 4) return null;

            var pts = new float2[4];
            for (int i = 0; i < 4; i++)
            {
                var e = q[i];
                if (e.ValueKind != JsonValueKind.Array || e.GetArrayLength() < 2) return null;
                pts[i] = new float2(e[0].GetSingle(), e[1].GetSingle());
            }

            // 面积（鞋带公式）：退化成一个点或一条线的轮廓直接丢
            float area2 = 0f;
            for (int i = 0; i < 4; i++)
            {
                var p = pts[i];
                var r = pts[(i + 1) % 4];
                area2 += p.x * r.y - r.x * p.y;
            }
            if (Math.Abs(area2) * 0.5f < 0.5f) return null;

            var kind = Str(o, "kind", "building");
            return new Obstacle
            {
                A = pts[0], B = pts[1], C = pts[2], D = pts[3],
                Kind = kind == "water" ? ObstacleKind.Water
                     : kind == "utility" ? ObstacleKind.Utility
                     : kind == "other" ? ObstacleKind.Other
                     : ObstacleKind.Building,
            };
        }

        private static ZoneGrid ReadZoneGrid(JsonElement g)
        {
            var c = Vec2(g, "center") ?? new float2(0f, 0f);
            var d = Vec2(g, "direction") ?? new float2(0f, -1f);
            int nx = (int)Num(g, "nx", 1), ny = (int)Num(g, "ny", 1);
            return new ZoneGrid
            {
                Position = c,
                Direction = d,
                Size = new int2(Math.Max(1, nx), Math.Max(1, ny)),
                Cells = null,   // null = 全部可建
            };
        }

        private static EdgeMarker ReadMarker(JsonElement m, float3[] ring)
        {
            string kind = Str(m, "kind", "entrance");
            int seg = (int)Num(m, "segment", 0);
            float t = (float)Num(m, "t", 0.5f);
            var pos = Vec2(m, "at");

            float3 p;
            if (pos.HasValue) p = new float3(pos.Value.x, 0f, pos.Value.y);
            else
            {
                seg = Math.Max(0, Math.Min(seg, ring.Length - 1));
                var a = ring[seg];
                var b = ring[(seg + 1) % ring.Length];
                t = Math.Max(0f, Math.Min(1f, t));
                p = a + (b - a) * t;
            }

            return new EdgeMarker
            {
                Kind = kind,
                RingSegment = seg,
                RingT = t,
                Position = p,
                Crosswalk = Bool(m, "crosswalk", false),
            };
        }

        private static object ReadSetting(JsonElement v)
        {
            switch (v.ValueKind)
            {
                case JsonValueKind.True: return true;
                case JsonValueKind.False: return false;
                case JsonValueKind.String: return v.GetString();
                case JsonValueKind.Number: return v.GetDouble();
                case JsonValueKind.Array:
                    var arr = new List<string>();
                    foreach (var e in v.EnumerateArray())
                        arr.Add(e.ValueKind == JsonValueKind.String ? e.GetString() : e.ToString());
                    return arr.ToArray();
                default: return null;
            }
        }

        // ---------------------------------------------------------------- 输出
        private static string Serialize(JsonElement root, IAreaGenerator gen, AreaInput input,
                                        AreaPlan plan, EmitReport rep, System.Diagnostics.Stopwatch sw)
        {
            var sb = new StringBuilder(1 << 20);
            var inv = INV;

            void Raw(string s) => sb.Append(s);
            void Key(string k) { sb.Append('"').Append(k).Append("\":"); }
            void Num2(double v) => sb.Append(v.ToString("0.####", inv));
            void Str2(string v) => sb.Append('"').Append(JsonEnc(v)).Append('"');
            void Vec3(float3 v) { sb.Append('['); Num2(R3(v.x)); sb.Append(','); Num2(R3(v.z)); sb.Append(']'); }
            void Ring(float3[] r)
            {
                sb.Append('[');
                for (int i = 0; i < r.Length; i++) { if (i > 0) sb.Append(','); Vec3(r[i]); }
                sb.Append(']');
            }
            // 未知角色计数：写进 plan JSON，让消费方不用猜"为什么少了几个元素"
            void UnknownRoles()
            {
                Key("unknownRoles"); sb.Append("{\"surfaces\":").Append(rep.UnkSurf)
                    .Append(",\"nets\":").Append(rep.UnkNet)
                    .Append(",\"objects\":").Append(rep.UnkObj)
                    .Append(",\"areas\":").Append(rep.UnkArea).Append('}');
                Raw(",");
            }

            sb.Append('{');
            Key("protocol"); Str2("placemaker-plan/1"); Raw(",");
            Key("generator"); Str2(gen.Id); Raw(",");
            Key("units"); Str2("meters"); Raw(",");
            Key("seed"); Num2(input.Seed); Raw(",");
            Key("area"); Num2(rep.Area); Raw(",");
            Key("ready"); Raw(rep.Ready ? "true" : "false"); Raw(",");
            if (!rep.Ready) { Key("readyKey"); Str2(rep.ReadyKey); Raw(","); }
            Key("markers"); { sb.Append("{\"given\":").Append(rep.GivenMarkers).Append(",\"auto\":").Append(rep.AutoMarkers).Append('}'); Raw(","); }
            Key("elapsedMs"); Num2(sw.Elapsed.TotalMilliseconds); Raw(",");
            // ---- settings：回显本次实际用的值，消费方据此知道"这是用哪套参数跑的"
            Key("settings");
            SerializeSettings(sb, input.Settings);
            Raw(",");

            if (plan == null)
            {
                Key("empty"); Str2(rep.Ready ? "plan returned null" : "layer not ready"); Raw(",");
                Key("surfaces"); Raw("[]"); Raw(",");
                Key("nets"); Raw("[]"); Raw(",");
                Key("objects"); Raw("[]"); Raw(",");
                Key("areas"); Raw("[]"); Raw(",");
                Key("regions"); Raw("[]"); Raw(",");
                Key("zoning"); Raw("[]"); Raw(",");
                Key("paint"); Raw("[]"); Raw(",");
                Key("terraform"); Raw("[]"); Raw(",");
                UnknownRoles();
                Key("stats"); Raw("[]"); Raw(",");
                Key("facts"); Raw("{}"); Raw(",");
                Key("issues"); Raw("[]");
                sb.Append('}');
                return sb.ToString();
            }

            // ---- surfaces
            Key("surfaces"); Raw("[");
            for (int i = 0; i < plan.Surfaces.Count; i++)
            {
                var s = plan.Surfaces[i];
                if (i > 0) Raw(",");
                var rn = RoleName(s.Role);
                if (rn == "other") rep.UnkSurf++;
                Raw("{\"role\":"); Str2(rn);
                Raw(",\"y\":"); Num2(R3(s.Ring.Length > 0 ? s.Ring[0].y : 0f));
                Raw(",\"ring\":"); Ring(s.Ring);
                if (!string.IsNullOrEmpty(s.Category)) { Raw(",\"category\":"); Str2(s.Category); }
                Raw("}");
            }
            Raw("]"); Raw(",");
            rep.Surf = plan.Surfaces.Count;

            // ---- nets（一次贝塞尔，A/B/C/D 都留着，采样交给消费方）
            Key("nets"); Raw("[");
            for (int i = 0; i < plan.Nets.Count; i++)
            {
                var n = plan.Nets[i];
                if (i > 0) Raw(",");
                var rn = RoleName(n.Role);
                if (rn == "other") rep.UnkNet++;
                Raw("{\"role\":"); Str2(rn);
                Raw(",\"a\":"); Vec3(n.A);
                Raw(",\"b\":"); Vec3(n.B);
                Raw(",\"c\":"); Vec3(n.C);
                Raw(",\"d\":"); Vec3(n.D);
                Raw(",\"startAttach\":"); sb.Append(n.StartAttach);
                Raw(",\"endAttach\":"); sb.Append(n.EndAttach);
                if (!string.IsNullOrEmpty(n.Category)) { Raw(",\"category\":"); Str2(n.Category); }
                Raw("}");
            }
            Raw("]"); Raw(",");
            rep.Nets = plan.Nets.Count;

            // ---- objects
            Key("objects"); Raw("[");
            for (int i = 0; i < plan.Objects.Count; i++)
            {
                var o = plan.Objects[i];
                if (i > 0) Raw(",");
                var rn = RoleName(o.Role);
                if (rn == "other") rep.UnkObj++;
                Raw("{\"role\":"); Str2(rn);
                Raw(",\"x\":"); Num2(R3(o.Position.x));
                Raw(",\"z\":"); Num2(R3(o.Position.z));
                if (Math.Abs(o.Position.y) > 1e-4) { Raw(",\"y\":"); Num2(R3(o.Position.y)); }
                Raw(",\"yaw\":"); Num2(Math.Round(o.Yaw, 4));
                if (Math.Abs(o.Scale - 1f) > 1e-4) { Raw(",\"scale\":"); Num2(Math.Round(o.Scale, 3)); }
                if (Math.Abs(o.Elevation) > 1e-4) { Raw(",\"elevation\":"); Num2(Math.Round(o.Elevation, 3)); }
                if (Math.Abs(o.Roll) > 1e-4) { Raw(",\"roll\":"); Num2(Math.Round(o.Roll, 3)); }
                if (Math.Abs(o.Pitch) > 1e-4) { Raw(",\"pitch\":"); Num2(Math.Round(o.Pitch, 3)); }
                // 资产分类（AssetCategories）。**必须带上**：`Furniture` 是通用角色，700 多个堆场杂物
                // 和公园里的一张野餐桌都是它，不靠分类区分的话消费方只能一律画成同一种道具。
                // 贴花同理：球场白线（decal.fieldline）、半场贴花（decal.markings）、
                // 随手撒的落叶（decal.leaves）长得一样，几何差一个量级。
                if (!string.IsNullOrEmpty(o.Category)) { Raw(",\"category\":"); Str2(o.Category); }
                Raw("}");
            }
            Raw("]"); Raw(",");
            rep.Obj = plan.Objects.Count;

            // ---- areas
            Key("areas"); Raw("[");
            for (int i = 0; i < plan.Areas.Count; i++)
            {
                var a = plan.Areas[i];
                if (i > 0) Raw(",");
                var rn = RoleName(a.Role);
                if (rn == "other") rep.UnkArea++;
                Raw("{\"role\":"); Str2(rn);
                Raw(",\"ring\":"); Ring(a.Ring); Raw("}");
            }
            Raw("]"); Raw(",");
            rep.Areas = plan.Areas.Count;

            // ---- regions
            Key("regions"); Raw("[");
            for (int i = 0; i < plan.Regions.Count; i++)
            {
                var r = plan.Regions[i];
                if (i > 0) Raw(",");
                Raw("{\"key\":"); Str2(r.Key ?? "");
                Raw(",\"tags\":[");
                for (int k = 0; k < r.Tags.Length; k++) { if (k > 0) Raw(","); Str2(r.Tags[k]); }
                Raw("]");
                Raw(",\"ring\":"); Ring(r.Ring); Raw("}");
            }
            Raw("]"); Raw(",");
            rep.Reg = plan.Regions.Count;

            // ---- zoning
            Key("zoning"); Raw("[");
            for (int i = 0; i < plan.Zoning.Count; i++)
            {
                var z = plan.Zoning[i];
                if (i > 0) Raw(",");
                Raw("{\"zoneId\":"); Str2(z.ZoneId ?? "");
                Raw(",\"quad\":"); Raw("[");
                Vec3(z.A); Raw(","); Vec3(z.B); Raw(","); Vec3(z.C); Raw(","); Vec3(z.D);
                Raw("]}");
            }
            Raw("]"); Raw(",");
            rep.Zone = plan.Zoning.Count;

            // ---- paint（地形贴图：park 的草地走这条通道，不是 Surfaces）
            Key("paint"); Raw("[");
            for (int i = 0; i < plan.Paint.Count; i++)
            {
                var p = plan.Paint[i];
                if (i > 0) Raw(",");
                Raw("{\"layer\":"); sb.Append(p.Layer);
                Raw(",\"strength\":"); Num2(Math.Round(p.Strength, 3));
                Raw(",\"ring\":"); Ring(p.Ring); Raw("}");
            }
            Raw("]"); Raw(",");
            rep.Paint = plan.Paint.Count;

            // ---- terraform
            Key("terraform"); Raw("[");
            for (int i = 0; i < plan.Terraform.Count; i++)
            {
                var t = plan.Terraform[i];
                if (i > 0) Raw(",");
                Raw("{\"kind\":"); Str2(t.Kind.ToString().ToLowerInvariant());
                Raw(",\"target\":"); Num2(double.IsNaN(t.TargetHeight) ? 0 : Math.Round(t.TargetHeight, 3));
                Raw(",\"permanent\":"); Raw(t.Permanent ? "true" : "false");
                // 顶点高度：Ring() 走 Vec3，只写 x/z，而 terraform 的**缓坡只在顶点高度里**。
                // 不补这一项，一块 2.6 m 高的料堆和一块 0.3 m 高的格子长得完全一样 ——
                // 都只是"一个四边形"，消费方只能拿 target 当平地画。
                if (t.Ring != null && t.Ring.Length > 0)
                {
                    Raw(",\"hs\":[");
                    for (int j = 0; j < t.Ring.Length; j++)
                    {
                        if (j > 0) Raw(",");
                        Num2(Math.Round(float.IsNaN(t.Ring[j].y) ? 0f : t.Ring[j].y, 2));
                    }
                    Raw("]");
                }
                Raw(",\"ring\":"); Ring(t.Ring); Raw("}");
            }
            Raw("]"); Raw(",");
            rep.Terr = plan.Terraform.Count;

            UnknownRoles();

            // ---- stats / facts / issues
            Key("stats"); Raw("[");
            for (int i = 0; i < plan.Stats.Count; i++)
            {
                var s = plan.Stats[i];
                if (i > 0) Raw(",");
                Raw("{\"key\":"); Str2(Short(s.Key));
                Raw(",\"value\":"); Num2(Math.Round(s.Value, 4));
                if (!string.IsNullOrEmpty(s.Unit)) { Raw(",\"unit\":"); Str2(s.Unit); }
                Raw("}");
            }
            Raw("]"); Raw(",");

            Key("facts"); Raw("{");
            bool firstFact = true;
            foreach (var kv in plan.Facts)
            {
                if (!firstFact) Raw(","); firstFact = false;
                Str2(kv.Key); Raw(":"); Num2(Math.Round(kv.Value, 4));
            }
            Raw("}"); Raw(",");

            Key("issues"); Raw("[");
            for (int i = 0; i < plan.Issues.Count; i++)
            {
                var it = plan.Issues[i];
                if (i > 0) Raw(",");
                if (it.Severity == IssueSeverity.Warning) rep.Warn++;
                else if (it.Severity == IssueSeverity.Error) rep.Err++;
                Raw("{\"severity\":"); Str2(it.Severity.ToString().ToLowerInvariant());
                Raw(",\"key\":"); Str2(Short(it.Key));
                if (it.Args != null && it.Args.Length > 0)
                {
                    Raw(",\"args\":[");
                    for (int k = 0; k < it.Args.Length; k++)
                    {
                        if (k > 0) Raw(",");
                        var a = it.Args[k];
                        if (a is double || a is float || a is int || a is long) Num2(Convert.ToDouble(a, inv));
                        else Str2(a?.ToString() ?? "");
                    }
                    Raw("]");
                }
                Raw("}");
            }
            Raw("]");

            sb.Append('}');
            return sb.ToString();
        }

        /// <summary>
        /// 把 C# **解析之后**的输入原样导出来（`&lt;plan&gt;.input.json`）。
        ///
        /// 为什么 oracle 必须导出输入：原生 TS 实现要跟 C# 比输出，前提是**输入完全一致**。
        /// 而输入是 C# 自己从 `shape + streets + settings` 解析出来的：
        /// `settings` 是 `gen.Defaults()` 与用例值的合并结果（parking 有 33 项），
        /// `B/C` 控制点是从 `A/D` 三等分算出来的，`markers` 还可能被 `SuggestMarkers` 补过。
        /// 让 TS 侧再解析一遍，就变成了"两份解析器比谁解析得对"，而不是"两份生成器比谁画得对"。
        ///
        /// 所以约定：**喂给原生后端的输入 = 这个文件**，解析只在 C# 里发生（D-059）。
        /// </summary>
        private static string SerializeInput(IAreaGenerator gen, AreaInput input, string waterRaw)
        {
            var sb = new StringBuilder(1 << 18);

            void Key(string k) => sb.Append('"').Append(k).Append("\":");
            void N(double v) => sb.Append(R3(v).ToString("0.####", INV));
            void S(string v) => sb.Append('"').Append(JsonEnc(v)).Append('"');
            void P(float3 p) { sb.Append('['); N(p.x); sb.Append(','); N(p.z); sb.Append(']'); }
            void P2(float2 p) { sb.Append('['); N(p.x); sb.Append(','); N(p.y); sb.Append(']'); }

            sb.Append('{');
            Key("protocol"); S("placemaker-input/1"); sb.Append(',');
            Key("generator"); S(gen.Id); sb.Append(',');
            Key("seed"); N(input.Seed); sb.Append(',');

            Key("shape"); sb.Append('{');
            Key("ring"); sb.Append('[');
            for (int i = 0; i < input.Shape.Ring.Length; i++) { if (i > 0) sb.Append(','); P(input.Shape.Ring[i]); }
            sb.Append("]},");

            Key("layoutScale"); N(input.Site.LayoutScale); sb.Append(',');
            Key("edgeMargin"); N(input.Site.EdgeMargin); sb.Append(',');

            Key("nearbyNets"); sb.Append('[');
            for (int i = 0; i < input.Site.NearbyNets.Count; i++)
            {
                var n = input.Site.NearbyNets[i];
                if (i > 0) sb.Append(',');
                sb.Append('{');
                Key("externalId"); sb.Append(n.ExternalId.ToString(INV)); sb.Append(',');
                Key("a"); P(n.A); sb.Append(',');
                Key("b"); P(n.B); sb.Append(',');
                Key("c"); P(n.C); sb.Append(',');
                Key("d"); P(n.D); sb.Append(',');
                Key("width"); N(n.Width); sb.Append(',');
                Key("category"); S(n.Category switch
                {
                    NetCategory.Pathway => "pathway",
                    NetCategory.Track => "track",
                    NetCategory.Other => "other",
                    _ => "road",
                }); sb.Append(',');
                Key("hasSidewalk"); sb.Append(n.HasSidewalk ? "true" : "false"); sb.Append(',');
                Key("isOneWay"); sb.Append(n.IsOneWay ? "true" : "false"); sb.Append(',');
                Key("isHighway"); sb.Append(n.IsHighway ? "true" : "false"); sb.Append(',');
                Key("speedLimit"); N(n.SpeedLimit);
                sb.Append('}');
            }
            sb.Append("],");

            Key("zoneGrids"); sb.Append('[');
            for (int i = 0; i < input.Site.ZoneGrids.Count; i++)
            {
                var g = input.Site.ZoneGrids[i];
                if (i > 0) sb.Append(',');
                sb.Append('{');
                Key("center"); P2(g.Position); sb.Append(',');
                Key("direction"); P2(g.Direction); sb.Append(',');
                Key("nx"); sb.Append(g.Size.x.ToString(INV)); sb.Append(',');
                Key("ny"); sb.Append(g.Size.y.ToString(INV));
                sb.Append('}');
            }
            sb.Append("],");

            Key("obstacles"); sb.Append('[');
            for (int i = 0; i < input.Site.Obstacles.Count; i++)
            {
                var o = input.Site.Obstacles[i];
                if (i > 0) sb.Append(',');
                sb.Append('{');
                Key("quad"); sb.Append('[');
                P2(o.A); sb.Append(','); P2(o.B); sb.Append(','); P2(o.C); sb.Append(','); P2(o.D);
                sb.Append("],");
                Key("kind"); S(o.Kind switch
                {
                    ObstacleKind.Water => "water",
                    ObstacleKind.Utility => "utility",
                    ObstacleKind.Other => "other",
                    _ => "building",
                });
                sb.Append('}');
            }
            sb.Append("],");

            Key("settings");
            SerializeSettings(sb, input.Settings);
            sb.Append(',');

            Key("markers"); sb.Append('[');
            for (int i = 0; i < input.Markers.Count; i++)
            {
                var m = input.Markers[i];
                if (i > 0) sb.Append(',');
                sb.Append('{');
                Key("kind"); S(m.Kind); sb.Append(',');
                Key("ringSegment"); sb.Append(m.RingSegment.ToString(INV)); sb.Append(',');
                Key("ringT"); N(m.RingT); sb.Append(',');
                Key("position"); P(m.Position); sb.Append(',');
                Key("crosswalk"); sb.Append(m.Crosswalk ? "true" : "false");
                sb.Append('}');
            }
            sb.Append("],");

            Key("regionTags"); sb.Append('[');
            if (input.RegionTags != null)
                for (int i = 0; i < input.RegionTags.Length; i++) { if (i > 0) sb.Append(','); S(input.RegionTags[i]); }
            sb.Append("],");

            // water 是闭包，导不出函数；把用例里的原始块原样带上，让 TS 侧能复现同一个 WaterDepthAt
            Key("water");
            sb.Append(string.IsNullOrEmpty(waterRaw) ? "null" : waterRaw);

            sb.Append('}');
            return sb.ToString();
        }

        private static void SerializeSettings(StringBuilder sb, SettingsBag bag)
        {
            sb.Append('{');
            bool first = true;
            foreach (var kv in bag.Values)
            {
                if (!first) sb.Append(','); first = false;
                sb.Append('"').Append(JsonEnc(kv.Key)).Append("\":");
                switch (kv.Value)
                {
                    case bool b: sb.Append(b ? "true" : "false"); break;
                    case string s: sb.Append('"').Append(JsonEnc(s)).Append('"'); break;
                    case string[] arr:
                        sb.Append('[');
                        for (int i = 0; i < arr.Length; i++) { if (i > 0) sb.Append(','); sb.Append('"').Append(JsonEnc(arr[i])).Append('"'); }
                        sb.Append(']');
                        break;
                    case null: sb.Append("null"); break;
                    default:
                        try { sb.Append(Convert.ToDouble(kv.Value, INV).ToString("0.####", INV)); }
                        catch { sb.Append('"').Append(JsonEnc(kv.Value.ToString())).Append('"'); }
                        break;
                }
            }
            sb.Append('}');
        }

        private static string ErrorJson(EmitReport rep)
        {
            var sb = new StringBuilder();
            sb.Append("{\"protocol\":\"placemaker-plan/1\",\"generator\":\"").Append(JsonEnc(rep.Generator))
              .Append("\",\"units\":\"meters\",\"error\":\"").Append(JsonEnc(rep.Error))
              .Append("\",\"surfaces\":[],\"nets\":[],\"objects\":[],\"areas\":[],\"regions\":[],\"zoning\":[],\"paint\":[],\"terraform\":[],\"stats\":[],\"facts\":{},\"issues\":[]}");
            return sb.ToString();
        }

        // ---------------------------------------------------------------- 角色名（对消费方稳定的小驼峰）
        private static string RoleName(SurfaceRole r) => r switch
        {
            SurfaceRole.Ground => "ground",
            SurfaceRole.Paving => "paving",
            SurfaceRole.Asphalt => "asphalt",
            SurfaceRole.Grass => "grass",
            SurfaceRole.Plaza => "plaza",
            SurfaceRole.Water => "water",
            SurfaceRole.Decorative => "decorative",
            // 兜底必须写 "other" 而不是某个合法值：否则新角色会冒充 ground/grass 混过去，
            // 消费方既认不出、也不会报警（7 个枚举已全列，这里当前不可达）。
            _ => "other",
        };

        private static string RoleName(NetRole r) => r switch
        {
            NetRole.Street => "street",
            NetRole.Avenue => "avenue",
            NetRole.CulDeSac => "culDeSac",
            NetRole.Roundabout => "roundabout",
            NetRole.PerimeterRoad => "perimeterRoad",
            NetRole.Pathway => "pathway",
            NetRole.PathwayWide => "pathwayWide",
            NetRole.DriveAisle => "driveAisle",
            NetRole.DriveConnector => "driveConnector",
            NetRole.Alley => "alley",
            NetRole.Fence => "fence",
            _ => "other",
        };

        private static string RoleName(ObjectRole r) => r switch
        {
            ObjectRole.Tree => "tree",
            ObjectRole.Bush => "bush",
            ObjectRole.Planter => "planter",
            ObjectRole.Bench => "bench",
            ObjectRole.Lamp => "lamp",
            ObjectRole.Bin => "bin",
            ObjectRole.Centerpiece => "centerpiece",
            ObjectRole.Furniture => "furniture",
            ObjectRole.FencePiece => "fencePiece",
            ObjectRole.Decal => "decal",
            ObjectRole.ParkingBay => "parkingBay",
            ObjectRole.Charger => "charger",
            ObjectRole.Arrow => "arrow",
            ObjectRole.Marker => "marker",
            ObjectRole.AnimalSpawner => "animalSpawner",
            ObjectRole.AccessPoint => "accessPoint",
            ObjectRole.CulDeSacCap => "culDeSacCap",
            ObjectRole.RoundaboutIsland => "roundaboutIsland",
            _ => "other",
        };

        private static string RoleName(AreaRole r) => r switch
        {
            AreaRole.PedestrianArea => "pedestrian",
            AreaRole.Hangaround => "hangaround",
            AreaRole.Lot => "lot",
            _ => "other",
        };

        // ---------------------------------------------------------------- 小工具
        private static float3[] Rect(float w, float h) => new[]
        {
            new float3(0f, 0f, 0f),
            new float3(w, 0f, 0f),
            new float3(w, 0f, h),
            new float3(0f, 0f, h),
        };

        private static double R3(double v) => Math.Round(v, 3);

        private static string Flatten(Exception ex)
        {
            var sb = new StringBuilder();
            for (var e = ex; e != null; e = e.InnerException)
            {
                if (sb.Length > 0) sb.Append(" <- ");
                sb.Append(e.GetType().Name).Append(": ").Append((e.Message ?? "").Replace('\r', ' ').Replace('\n', ' '));
                if (sb.Length > 320) break;
            }
            var s = sb.ToString();
            return s.Length > 340 ? s.Substring(0, 340) + "..." : s;
        }

        private static string Short(string key)
        {
            if (string.IsNullOrEmpty(key)) return "";
            if (key.StartsWith("Placemaker.", StringComparison.Ordinal)) key = key.Substring("Placemaker.".Length);
            var i = key.IndexOf('.');
            return i >= 0 ? key.Substring(i + 1) : key;
        }

        private static string JsonEnc(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length + 8);
            foreach (var ch in s)
            {
                switch (ch)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (ch < 0x20) sb.Append("\\u").Append(((int)ch).ToString("x4"));
                        else sb.Append(ch);
                        break;
                }
            }
            return sb.ToString();
        }

        private static string Str(JsonElement e, string name, string fallback)
        {
            if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString();
            return fallback;
        }

        private static double Num(JsonElement e, string name, double fallback)
        {
            if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number)
                return v.GetDouble();
            return fallback;
        }

        private static bool Bool(JsonElement e, string name, bool fallback)
        {
            if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v))
            {
                if (v.ValueKind == JsonValueKind.True) return true;
                if (v.ValueKind == JsonValueKind.False) return false;
            }
            return fallback;
        }

        private static float2? Vec2(JsonElement e, string name)
        {
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return null;
            if (v.ValueKind == JsonValueKind.Array && v.GetArrayLength() >= 2)
                return new float2(v[0].GetSingle(), v[1].GetSingle());
            if (v.ValueKind == JsonValueKind.Object)
                return new float2((float)Num(v, "x", 0), (float)Num(v, "z", 0));
            return null;
        }

        private static string[] ReadStringArray(JsonElement e, string name)
        {
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return null;
            if (v.ValueKind != JsonValueKind.Array) return null;
            var list = new List<string>();
            foreach (var x in v.EnumerateArray())
                if (x.ValueKind == JsonValueKind.String) list.Add(x.GetString());
            return list.ToArray();
        }
    }
}
