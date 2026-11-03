// PlacemakerCore 驱动器（阶段 3）：把 Core 里所有 [AreaGenerator] 排成一排，逐个跑一遍。
//
// 为什么值得这么做：阶段 2 只证明了 ParkingLayer 能脱离游戏跑。但 Core 里有 130 多个图层，
// 它们共享同一套 IAreaGenerator 契约（Plan(AreaInput, CancellationToken) → AreaPlan）。
// 如果契约真是"纯函数"，那本来就不该只对停车层成立——一次全量扫，能直接回答三个问题：
//   1) 哪些图层真的能脱离游戏生成内容（而不是只能编译）；
//   2) 哪些图层必须依赖"上层已铺好的地面/子区域"，单独跑就没有输入；
//   3) 哪些图层在缺少游戏资产目录时会抛异常——那是接游戏前必须补的接口。
//
// 每个生成器跑两个变体：
//   bare    孤立地块，四周无路
//   street  南侧贴一条双向街（真实情形：地块临街）
// 之所以两个都跑：阶段 2 已经踩过一次——无路时 CheckReady 判 NOT READY 是"正确地说不行"，
// 只看一个变体会把"需要临街"误判成"跑不动"。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using Unity.Mathematics;
using Placemaker.Core;

namespace Placemaker.Driver
{
    /// <summary>一次运行（某个变体）的结果。</summary>
    internal sealed class SweepRun
    {
        public string Variant = "";
        public bool Ready;
        public string ReadyKey = "";
        public int Markers;
        public int Nets, Objects, Surfaces, Areas, Regions, Terraform, Zoning;
        public int Err, Warn, Info;
        public string IssueKeys = "";
        public string StatSummary = "";
        public int Facts;
        public long Ms;
        public string Status = "OK";      // OK | EMPTY | REFUSED | THROW | TIMEOUT
        public string Error = "";
        public AreaPlan Plan;
    }

    /// <summary>一个生成器的全部结果。</summary>
    internal sealed class SweepRow
    {
        public int Order;
        public string Id = "";
        public string Type = "";
        public string Ns = "";
        public string Kind = "?";
        public string Group = "";
        public bool Hidden;
        public int Controls, Presets, Features;
        public float MinArea, MaxArea;
        public double Area, W, H;
        public string Unavailable = "";
        public string CtorError = "";
        public IAreaGenerator Gen;
        public SweepRun Bare, Street, Grid, Wet;
        public string Svg = "";

        /// <summary>四个语境的运行结果（顺序即报告顺序）。</summary>
        public IEnumerable<SweepRun> All()
        {
            if (Bare != null) yield return Bare;
            if (Street != null) yield return Street;
            if (Grid != null) yield return Grid;
            if (Wet != null) yield return Wet;
        }

        /// <summary>渲染时用的那一版：越接近真实工地越优先（有街 → 有街+分区网格 → 有水 → 孤立）。</summary>
        public SweepRun Pick()
        {
            foreach (var r in new[] { Street, Grid, Wet, Bare })
                if (r != null && Weight(r) > 0) return r;
            return Street ?? Bare ?? Grid ?? Wet;
        }

        public static int Weight(SweepRun r)
        {
            if (r == null) return 0;
            return r.Surfaces + r.Nets + r.Objects + r.Areas + r.Regions + r.Terraform + r.Zoning;
        }

        private static int Rank(string s)
        {
            switch (s)
            {
                case "OK": return 0;
                case "REFUSED": return 1;
                case "EMPTY": return 2;
                case "TIMEOUT": return 3;
                default: return 4;
            }
        }

        /// <summary>整行的最终判定（四个语境里最好的那个）。</summary>
        public string Verdict
        {
            get
            {
                if (Gen == null) return "CTOR-FAIL";
                int best = 9;
                foreach (var r in All()) best = Math.Min(best, Rank(r.Status));
                return best == 0 ? "OK" : best == 1 ? "REFUSED" : best == 2 ? "EMPTY"
                     : best == 3 ? "TIMEOUT" : best == 4 ? "THROW" : "EMPTY";
            }
        }
    }

    internal static class Sweep
    {
        private static readonly CultureInfo INV = CultureInfo.InvariantCulture;
        private static readonly TimeSpan Budget = TimeSpan.FromSeconds(8);

        // ---------------------------------------------------------------- 入口
        public static int Run(string outDir)
        {
            var svgDir = Path.Combine(outDir, "sweep");
            Directory.CreateDirectory(svgDir);

            Console.WriteLine("=== 全量扫描：[AreaGenerator] 逐个跑 ===");
            var rows = Discover();
            Console.WriteLine("[sweep] 发现并实例化 " + rows.Count + " 个生成器");
            int failCtor = rows.Count(r => r.Gen == null);
            if (failCtor > 0) Console.WriteLine("[sweep] 其中 " + failCtor + " 个无法无参构造（已记录原因）");
            Console.WriteLine();

            foreach (var r in rows)
            {
                RunRow(r);
                ConsoleOut(r);
            }

            Console.WriteLine();
            RenderAll(rows, svgDir);
            string md = Path.Combine(outDir, "sweep.md");
            string csv = Path.Combine(outDir, "sweep.csv");
            File.WriteAllText(md, BuildMarkdown(rows), new UTF8Encoding(false));
            File.WriteAllText(csv, BuildCsv(rows), new UTF8Encoding(false));
            Console.WriteLine("[out] " + md);
            Console.WriteLine("[out] " + csv);
            Console.WriteLine("[out] svg " + svgDir);

            PrintSummary(rows);
            return 0;
        }

        // ---------------------------------------------------------------- 发现
        private static List<SweepRow> Discover()
        {
            var asm = typeof(IAreaGenerator).Assembly;
            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray(); }

            var rows = new List<SweepRow>();
            var seenIds = new HashSet<string>(StringComparer.Ordinal);
            int dup = 0;

            foreach (var t in types)
            {
                if (t.IsAbstract || t.IsInterface || t.IsGenericTypeDefinition) continue;
                if (!typeof(IAreaGenerator).IsAssignableFrom(t)) continue;
                var attr = (AreaGeneratorAttribute)Attribute.GetCustomAttribute(t, typeof(AreaGeneratorAttribute));
                if (attr == null) continue;

                var row = new SweepRow
                {
                    Order = attr.Order,
                    Hidden = attr.Hidden,
                    Group = attr.Group ?? "",
                    Type = t.Name,
                    Ns = t.Namespace ?? "",
                    Kind = "?",
                };

                if (t.GetConstructor(Type.EmptyTypes) == null)
                {
                    row.CtorError = "没有无参构造函数";
                    rows.Add(row);
                    continue;
                }
                try
                {
                    var g = (IAreaGenerator)Activator.CreateInstance(t);
                    row.Gen = g;
                    row.Id = g.Id ?? "";
                    row.Kind = g.Kind.ToString();
                    row.Controls = g.Controls?.Count ?? 0;
                    row.Presets = g.Presets?.Count ?? 0;
                    row.Features = (g as IHasFeatures)?.Features?.Count ?? 0;
                    row.MinArea = g.MinArea;
                    row.MaxArea = g.MaxArea;
                }
                catch (Exception ex)
                {
                    row.CtorError = Flatten(ex);
                    rows.Add(row);
                    continue;
                }

                if (!string.IsNullOrEmpty(row.Id) && !seenIds.Add(row.Id)) { dup++; continue; }
                rows.Add(row);
            }

            if (dup > 0) Console.WriteLine("[sweep] 跳过 id 重复的别名 " + dup + " 个（同一 id 只跑一次）");

            return rows.OrderBy(r => r.Order).ThenBy(r => r.Id, StringComparer.Ordinal).ToList();
        }

        // ---------------------------------------------------------------- 单行
        private static void RunRow(SweepRow r)
        {
            if (r.Gen == null) return;

            r.Area = PickArea(r.MinArea, r.MaxArea);
            double w = Math.Sqrt(r.Area * 4.0 / 3.0);
            r.W = w;
            r.H = r.Area / w;
            var ring = Rect((float)r.W, (float)r.H);

            r.Bare = Exec(r, ring, null, "bare");
            r.Street = Exec(r, ring, Streets((float)r.W, (float)r.H), "street");
            // grid / wet 是"补上下文"的对照：如果某个图层在前两版里是空的，
            // 用它来判断到底是"依赖上层数据"还是"只差一个输入"。
            r.Grid = Exec(r, ring, Streets((float)r.W, (float)r.H), "grid", ZoneGridsFor((float)r.W, (float)r.H), null);
            r.Wet = Exec(r, ring, Streets((float)r.W, (float)r.H), "wet", null, WaterAt((float)r.H));

            // 只留渲染要用的那份，其余几何丢掉（130 多个 plan 全留着没意义）
            var pick = r.Pick();
            foreach (var run in r.All())
                if (!ReferenceEquals(run, pick)) run.Plan = null;
        }

        private static double PickArea(float minArea, float maxArea)
        {
            double a = Math.Max(minArea, 6000.0);   // 至少给 6000 m²，小图层才铺得开
            a *= 3.0;
            a = Math.Min(a, 40000.0);               // 太大只是慢
            a = Math.Min(a, Math.Max(maxArea * 0.8, minArea * 1.2));
            a = Math.Max(a, minArea * 1.2);
            return a;
        }

        private static SweepRun Exec(SweepRow r, float3[] ring, List<NearbyNet> roads, string variant,
                                     List<ZoneGrid> grids = null, Func<float2, float> water = null)
        {
            var run = new SweepRun { Variant = variant };
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var input = BuildInput(r, ring, roads, grids, water);
                var ready = r.Gen.CheckReady(input);
                run.Ready = ready.Ready;
                run.ReadyKey = ready.Ready ? "" : (ready.NextStepKey + "|" + ready.ReasonKey);
                var sug = r.Gen.SuggestMarkers(input);
                if (sug != null && sug.Count > 0) input.Markers.AddRange(sug);
                run.Markers = input.Markers.Count;

                using (var cts = new CancellationTokenSource(Budget))
                {
                    var plan = r.Gen.Plan(input, cts.Token);
                    run.Plan = plan;
                    Count(plan, run);
                }
            }
            catch (OperationCanceledException)
            {
                run.Status = "TIMEOUT";
                run.Error = "超过 " + Budget.TotalSeconds.ToString("0", INV) + "s 未完成（图层未响应取消令牌）";
            }
            catch (Exception ex)
            {
                run.Status = "THROW";
                run.Error = Flatten(ex);
            }
            sw.Stop();
            run.Ms = sw.ElapsedMilliseconds;

            if (run.Status == "OK")
            {
                if (SweepRow.Weight(run) > 0) run.Status = "OK";
                else run.Status = run.Err > 0 ? "REFUSED" : "EMPTY";
            }
            return run;
        }

        private static AreaInput BuildInput(SweepRow r, float3[] ring, List<NearbyNet> roads,
                                            List<ZoneGrid> grids = null, Func<float2, float> water = null)
        {
            var site = new SiteContext
            {
                LayoutScale = 1f,
                EdgeMargin = SiteContext.DefaultEdgeMargin,
            };
            if (roads != null) site.NearbyNets.AddRange(roads);
            if (grids != null) site.ZoneGrids.AddRange(grids);
            if (water != null) site.WaterDepthAt = water;
            return new AreaInput
            {
                Shape = new AreaShape { Ring = ring, Source = AreaSource.Drawn },
                Site = site,
                Settings = r.Gen.Defaults(),
                Seed = 1234u,
                RegionTags = new[] { "root" },
            };
        }

        private static void Count(AreaPlan plan, SweepRun run)
        {
            if (plan == null) { run.Status = "NULL"; return; }
            run.Nets = plan.Nets.Count;
            run.Objects = plan.Objects.Count;
            run.Surfaces = plan.Surfaces.Count;
            run.Areas = plan.Areas.Count;
            run.Regions = plan.Regions.Count;
            run.Terraform = plan.Terraform.Count;
            run.Zoning = plan.Zoning.Count;
            run.Facts = plan.Facts.Count;

            var keys = new StringBuilder();
            foreach (var it in plan.Issues)
            {
                switch (it.Severity)
                {
                    case IssueSeverity.Error: run.Err++; break;
                    case IssueSeverity.Warning: run.Warn++; break;
                    default: run.Info++; break;
                }
                if (keys.Length < 200)
                {
                    if (keys.Length > 0) keys.Append(" | ");
                    keys.Append(Short(it.Key));
                }
            }
            run.IssueKeys = keys.ToString();

            var stats = new StringBuilder();
            foreach (var s in plan.Stats)
            {
                if (stats.Length > 0) stats.Append(' ');
                stats.Append(Short(s.Key)).Append('=').Append(s.Value.ToString("0.#", INV));
            }
            run.StatSummary = stats.ToString();
        }

        // ---------------------------------------------------------------- 输出
        private static void ConsoleOut(SweepRow r)
        {
            var line = new StringBuilder("  ");
            line.Append(r.Verdict.PadRight(9));
            line.Append(Fit(r.Id, 20));
            line.Append(Fit(r.Kind, 7));
            if (r.Gen == null) { line.Append("  ").Append(r.CtorError); Console.WriteLine(line.ToString()); return; }

            var p = r.Pick();
            if (p == null) { line.Append("  (无运行)"); Console.WriteLine(line.ToString()); return; }
            line.Append("面").Append(Fit(p.Surfaces.ToString(INV), 4));
            line.Append(" 网").Append(Fit(p.Nets.ToString(INV), 4));
            line.Append(" 物").Append(Fit(p.Objects.ToString(INV), 5));
            line.Append(" 区").Append(Fit(p.Regions.ToString(INV), 3));
            line.Append(" 分").Append(Fit(p.Zoning.ToString(INV), 5));
            line.Append("  ").Append(Fit(p.Ms + "ms", 8));
            line.Append(" area=").Append(r.Area.ToString("0", INV));
            if (p.Err + p.Warn + p.Info > 0)
                line.Append("  [E").Append(p.Err).Append("/W").Append(p.Warn).Append("/I").Append(p.Info).Append("] ").Append(p.IssueKeys);
            if (p.Status == "THROW" || p.Status == "TIMEOUT") line.Append("  ").Append(p.Status).Append(": ").Append(p.Error);
            Console.WriteLine(line.ToString());
        }

        private static void RenderAll(List<SweepRow> rows, string svgDir)
        {
            int ok = 0;
            foreach (var r in rows)
            {
                var p = r.Pick();
                if (p?.Plan == null) continue;
                if (SweepRow.Weight(p) == 0) continue;
                var ring = Rect((float)r.W, (float)r.H);
                // bare 之外的三种语境都带那条街，否则画出来的接驳口会悬空
                var roads = p.Variant == "bare" ? null : Streets((float)r.W, (float)r.H);
                string name = Safe(r.Id) + ".svg";
                try
                {
                    var svg = Placemaker.Driver.Program.BuildSvg(ring, roads ?? new List<NearbyNet>(), p.Plan,
                        r.Id + " (" + r.Kind + ")", r.Area.ToString("0", INV) + " m2 " + p.Variant);
                    File.WriteAllText(Path.Combine(svgDir, name), svg, new UTF8Encoding(false));
                    r.Svg = "sweep/" + name;
                    ok++;
                }
                catch (Exception ex)
                {
                    r.Svg = "";
                    Console.WriteLine("  [svg-fail] " + r.Id + ": " + Flatten(ex));
                }
            }
            Console.WriteLine("[svg] 渲染 " + ok + " 张");
        }

        private static string BuildMarkdown(List<SweepRow> rows)
        {
            var sb = new StringBuilder();
            sb.Append("# Placemaker Core 全量生成器扫描\n\n");
            sb.Append("自动生成，勿手改。用 `PlacemakerDriver --sweep <outDir>` 复跑。\n\n");
            sb.Append("- 生成器总数：**").Append(rows.Count).Append("**\n");
            sb.Append("- 每个生成器在 4 种语境下各跑一次（同一地块、同一种子 1234）：\n");
            sb.Append("  - `bare`  孤立地块，四周无路\n");
            sb.Append("  - `street` 南侧贴一条 8 m 双向街\n");
            sb.Append("  - `grid`  street + 覆盖全地块的分区网格（`ZoneGrid`，8 m 格）\n");
            sb.Append("  - `wet`   street + 北侧 38% 水面（水深 1.6 m）\n");
            sb.Append("- 判定：`OK` 有内容 / `REFUSED` 无内容但有 Error issue / `EMPTY` 无内容无报错 / `THROW` 抛异常 / `TIMEOUT` 超时\n\n");

            int ok = rows.Count(r => r.Verdict == "OK");
            int refused = rows.Count(r => r.Verdict == "REFUSED");
            int empty = rows.Count(r => r.Verdict == "EMPTY");
            int bad = rows.Count(r => r.Verdict == "THROW" || r.Verdict == "TIMEOUT" || r.Verdict == "CTOR-FAIL" || r.Verdict == "NULL");
            sb.Append("| 判定 | 数量 |\n|---|---|\n");
            sb.Append("| OK 有内容 | ").Append(ok).Append(" |\n");
            sb.Append("| REFUSED 拒绝 | ").Append(refused).Append(" |\n");
            sb.Append("| EMPTY 空 | ").Append(empty).Append(" |\n");
            sb.Append("| THROW / TIMEOUT / 构造失败 | ").Append(bad).Append(" |\n\n");

            sb.Append("## 有产出的（").Append(ok).Append("）\n\n");
            sb.Append("| # | id | kind | 控件 | 面积 m² | 最佳语境 | 面 | 网 | 物 | 区域 | 分区 | issue | 耗时 |\n");
            sb.Append("|---|---|---|---|---|---|---|---|---|---|---|---|---|\n");
            foreach (var r in rows.Where(x => x.Verdict == "OK"))
            {
                var p = r.Pick();
                sb.Append("| ").Append(r.Order)
                  .Append(" | `").Append(r.Id).Append('`')
                  .Append(" | ").Append(r.Kind)
                  .Append(" | ").Append(r.Controls)
                  .Append(" | ").Append(r.Area.ToString("0", INV))
                  .Append(" | ").Append(p.Variant)
                  .Append(" | ").Append(p.Surfaces)
                  .Append(" | ").Append(p.Nets)
                  .Append(" | ").Append(p.Objects)
                  .Append(" | ").Append(p.Regions)
                  .Append(" | ").Append(p.Zoning)
                  .Append(" | ").Append(Esc(p.IssueKeys))
                  .Append(" | ").Append(p.Ms + "ms")
                  .Append(" |\n");
            }

            var rest = rows.Where(x => x.Verdict != "OK").ToList();
            sb.Append("\n## 没产出的（").Append(rest.Count).Append("）：四语境逐项证据\n\n");
            if (rest.Count == 0) sb.Append("（无）\n");
            else
            {
                sb.Append("| # | id | kind | bare | street | grid | wet | issue / 错误 |\n");
                sb.Append("|---|---|---|---|---|---|---|---|\n");
                foreach (var r in rest)
                {
                    sb.Append("| ").Append(r.Order)
                      .Append(" | `").Append(r.Id).Append('`')
                      .Append(" | ").Append(r.Kind)
                      .Append(" | ").Append(Mark(r.Bare))
                      .Append(" | ").Append(Mark(r.Street))
                      .Append(" | ").Append(Mark(r.Grid))
                      .Append(" | ").Append(Mark(r.Wet))
                      .Append(" | ").Append(Esc(Note(r)))
                      .Append(" |\n");
                }
            }
            return sb.ToString();
        }

        private static string Mark(SweepRun r)
        {
            if (r == null) return "-";
            switch (r.Status)
            {
                case "OK": return "OK " + SweepRow.Weight(r);
                case "REFUSED": return "REFUSED";
                case "EMPTY": return "空";
                case "TIMEOUT": return "超时";
                case "THROW": return "抛异常";
                default: return r.Status;
            }
        }

        private static string Note(SweepRow r)
        {
            if (r.Gen == null) return r.CtorError;
            var p = r.Pick();
            if (p == null) return "";
            var s = p.IssueKeys;
            if (p.Status == "THROW" || p.Status == "TIMEOUT") s = p.Status + ": " + p.Error;
            return s;
        }

        private static string BuildCsv(List<SweepRow> rows)
        {
            var sb = new StringBuilder();
            sb.Append("id,type,namespace,kind,order,hidden,group,controls,presets,features,minArea,maxArea,area,variant,status,ready,markers,"
                    + "surfaces,nets,objects,areas,regions,terraform,zoning,err,warn,info,ms,issueKeys,stats,error\n");
            foreach (var r in rows)
            {
                string prefix = Csv(r.Id) + ',' + Csv(r.Type) + ',' + Csv(r.Ns) + ',' + Csv(r.Kind) + ',' + r.Order + ','
                              + (r.Hidden ? 1 : 0) + ',' + Csv(r.Group) + ',' + r.Controls + ',' + r.Presets + ','
                              + r.Features + ',' + N(r.MinArea) + ',' + N(r.MaxArea) + ',' + N(r.Area) + ',';
                if (r.Gen == null)
                {
                    sb.Append(prefix).Append("none,CTOR-FAIL,")
                      .Append("0,0,0,0,0,0,0,0,0,0,0,0,0,,")
                      .Append(Csv(r.CtorError)).Append('\n');
                    continue;
                }
                foreach (var run in r.All())
                {
                    sb.Append(prefix).Append(Csv(run.Variant)).Append(',').Append(run.Status).Append(',')
                      .Append(run.Ready ? 1 : 0).Append(',').Append(run.Markers).Append(',')
                      .Append(run.Surfaces).Append(',').Append(run.Nets).Append(',').Append(run.Objects).Append(',')
                      .Append(run.Areas).Append(',').Append(run.Regions).Append(',').Append(run.Terraform).Append(',')
                      .Append(run.Zoning).Append(',').Append(run.Err).Append(',').Append(run.Warn).Append(',')
                      .Append(run.Info).Append(',').Append(run.Ms).Append(',')
                      .Append(Csv(run.IssueKeys)).Append(',').Append(Csv(run.StatSummary)).Append(',')
                      .Append(Csv(run.Error)).Append('\n');
                }
            }
            return sb.ToString();
        }

        private static void PrintSummary(List<SweepRow> rows)
        {
            Console.WriteLine("=== 汇总 ===");
            foreach (var g in rows.GroupBy(r => r.Verdict).OrderByDescending(g => g.Count()))
                Console.WriteLine("  " + Fit(g.Key, 12) + g.Count());
            Console.WriteLine();
            foreach (var g in rows.Where(r => r.Gen != null).GroupBy(r => r.Kind).OrderBy(g => g.Key))
            {
                int ok = g.Count(r => r.Verdict == "OK");
                Console.WriteLine("  按 kind  " + Fit(g.Key, 10) + ok + " / " + g.Count() + " 有内容");
            }

            // 语境依赖：哪一层是"喂了某个输入才活过来"的
            int onlyGrid = rows.Count(r => !IsOk(r.Bare) && !IsOk(r.Street) && IsOk(r.Grid));
            int onlyWet = rows.Count(r => !IsOk(r.Bare) && !IsOk(r.Street) && !IsOk(r.Grid) && IsOk(r.Wet));
            int bareOnly = rows.Count(r => IsOk(r.Bare));
            if (onlyGrid > 0 || onlyWet > 0)
                Console.WriteLine("  语境依赖  bare 就能跑=" + bareOnly + "  需分区网格=" + onlyGrid + "  需水面=" + onlyWet);

            var slow = rows.Where(r => r.Gen != null)
                           .Select(r => new { r.Id, Ms = r.All().Max(x => x.Ms) })
                           .OrderByDescending(x => x.Ms).Take(5).ToList();
            if (slow.Count > 0)
                Console.WriteLine("  最慢  " + string.Join(", ", slow.Select(x => x.Id + "=" + x.Ms + "ms")));
            var busy = rows.Where(r => r.Pick() != null)
                           .OrderByDescending(r => SweepRow.Weight(r.Pick())).Take(5)
                           .Select(r => r.Id + "=" + SweepRow.Weight(r.Pick()));
            Console.WriteLine("  产出最多  " + string.Join(", ", busy));
        }

        private static bool IsOk(SweepRun r) => r != null && r.Status == "OK";

        // ---------------------------------------------------------------- 几何
        private static float3[] Rect(float w, float h)
        {
            return new[]
            {
                new float3(0f, 0f, 0f),
                new float3(w, 0f, 0f),
                new float3(w, 0f, h),
                new float3(0f, 0f, h),
            };
        }

        /// <summary>南侧 z = -8 铺一条双向街，比地块宽出 40 m（出入口能落在街的中段，而不是街尾）。</summary>
        private static List<NearbyNet> Streets(float w, float h)
        {
            float3 a = new float3(-20f, 0f, -8f);
            float3 d = new float3(w + 20f, 0f, -8f);
            var list = new List<NearbyNet>
            {
                new NearbyNet
                {
                    ExternalId = 9001,
                    A = a,
                    B = a + (d - a) / 3f,
                    C = a + (d - a) * (2f / 3f),
                    D = d,
                    Width = 8f,
                    Category = NetCategory.Road,
                    HasSidewalk = true,
                    IsOneWay = false,
                    SpeedLimit = 50f,
                },
            };
            return list;
        }

        /// <summary>
        /// 一块覆盖整个地块的分区网格（ZoneGuard.Cell = 8 m）。Position 是网格中心，Direction (0,-1)
        /// 让第 0 行贴着南侧那条街——和 ZoneGuardCurveTests 里"南侧街道"的写法一致。
        /// Cells = null 表示"全部可建"，正好是我们离线场景想要的。
        /// </summary>
        private static List<ZoneGrid> ZoneGridsFor(float w, float h)
        {
            int nx = Math.Max(1, (int)Math.Ceiling(w / 8.0));
            int ny = Math.Max(1, (int)Math.Ceiling(h / 8.0));
            return new List<ZoneGrid>
            {
                new ZoneGrid
                {
                    Position = new float2(w * 0.5f, h * 0.5f),
                    Direction = new float2(0f, -1f),
                    Size = new int2(nx, ny),
                },
            };
        }

        /// <summary>北侧 38% 是水面（水深 1.6 m）：给"临水"图层一个真实的岸线。</summary>
        private static Func<float2, float> WaterAt(float h)
        {
            float edge = h * 0.62f;
            return p => p.y > edge ? 1.6f : 0f;
        }

        // ---------------------------------------------------------------- 小工具
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
            var parts = key.Split('.');
            if (parts.Length <= 2) return key;
            return parts[parts.Length - 2] + "." + parts[parts.Length - 1];
        }

        private static string Safe(string id)
        {
            var sb = new StringBuilder();
            foreach (char c in id) sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
            return sb.Length == 0 ? "gen" : sb.ToString();
        }

        private static string Csv(string s)
        {
            s = s ?? "";
            if (s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0) return s;
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        }

        private static string Esc(string s) => (s ?? "").Replace("|", "\\|");

        private static string N(float v) => v.ToString("0.###", INV);

        private static string N(double v) => v.ToString("0.###", INV);

        private static string Fit(string s, int width)
        {
            s = s ?? "";
            if (s.Length >= width) return s.Substring(0, width - 1) + " ";
            return s.PadRight(width);
        }
    }
}
