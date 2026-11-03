// PlacemakerCore 驱动器 · 阶段 4：Recipe 父/子图层嵌套
//
// 为什么需要这一阶段：阶段 3 证明了 130 个生成器"各自能跑"，但那只验证了单层。
// 真正要把库接到 TownForge 上，产出的是**一整块街区**：路网先铺，块地交给公园，
// 公园中心挖出广场再交给广场层，临街条带交给分区层……这条链只有走 Composer 才能验证。
//
// 本阶段跑三类东西，全部经由 RecipeJson.Parse（和游戏读存档同一条代码路径）：
//   A. 手写嵌套 recipe（落到 out/recipes-cases/*.json）—— 覆盖 Composition.cs 头注释里的每一种组合；
//   B. 仓库自带的 71 个 Recipes/*.json —— 验证真实文件兼容性；
//   C. 两个刻意写坏的 recipe —— 验证"一层失败不拖垮整张 recipe"的隔离约定。
//
// 每个用例除了画图，还做三项**结构断言**（这是本阶段相对"看着图挺像"的硬指标）：
//   1) target 命中：recipe 里写了 target "block"，就要真的匹配到 > 0 个区域；
//   2) 区域消费：父层产出的每个区域，是否有子步骤真的落进去（没被消费的块 = 空洞）；
//   3) 归属包含：子层对象有多少落在它被指派的那个区域环内（100% 才算"填在该填的地方"）。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using Placemaker.Core;
using Placemaker.Core.Geometry;
using Placemaker.Core.Tool;
using Unity.Mathematics;

namespace Placemaker.Driver
{
    internal sealed class RecipeCase
    {
        public string Id = "";
        public string Desc = "";
        /// <summary>非空 = 手写 JSON（同时会被写成文件）；空 = 用 FilePath 指向的仓库自带 recipe。</summary>
        public string Json;
        public string FilePath;
        /// <summary>
        /// 预期结果：ok = 必须无 Error 且 target 全命中；error = 必须报 Error（失败隔离用例）；
        /// miss = 必须出现"声明了 target 却匹配不到区域"（负向用例）。
        /// </summary>
        public string Expect = "ok";
    }

    internal sealed class LayerRow
    {
        public int Index;
        public string Id = "";
        public string StepPath = "";
        public string ParentPath = "";
        public string RegionKey = "";
        public int Surf, Nets, Obj, Areas, Reg, Zone, Terr, Paint, Dec;
        public int Err, Warn;
        public string IssueKeys = "";
        public long Ms;
        public double SurfArea;
        public int Inside, InsideTotal;         // 归属包含
        public int Emitted, Unfilled;            // 本层产出的区域 / 其中没有子步骤落进去的
    }

    internal sealed class CaseResult
    {
        public RecipeCase Case;
        public Recipe Recipe;
        public bool Crashed;
        public string Error = "";
        public List<AreaPlan> Plans = new List<AreaPlan>();
        public List<LayerRow> Layers = new List<LayerRow>();
        public string ComposerIssues = "";
        public long Ms;
        public int RegionsEmitted, RegionsFilled;
        public int TargetsDeclared, TargetsHit;
        public string TargetDetail = "";
        public int Surf, Nets, Obj, Areas, Zone, Paint, Err, Warn, Decals;
        public double SurfArea;
        public bool Crash => Crashed;
        /// <summary>issue 短名 → 出现次数（给汇总页画"最常见的告警"）。</summary>
        public Dictionary<string, int> IssueHist = new Dictionary<string, int>(StringComparer.Ordinal);

        /// <summary>是否达到用例自己声明的预期（c09/c10 期望报错、c11 期望 target 落空，都算"通过"）。</summary>
        public bool AsExpected
        {
            get
            {
                switch (Case.Expect)
                {
                    case "error": return Err > 0 || Crashed;
                    case "miss": return TargetsHit < TargetsDeclared;
                    default:
                        return !Crashed && Err == 0 && (TargetsDeclared == 0 || TargetsHit == TargetsDeclared);
                }
            }
        }

        /// <summary>意外失败：既没达到预期，又不是负向用例（这才是真正要看的红灯）。</summary>
        public bool Unexpected => !AsExpected;
    }

    internal static class Recipes
    {
        private static readonly CultureInfo INV = CultureInfo.InvariantCulture;
        private static readonly TimeSpan Budget = TimeSpan.FromSeconds(25);

        internal static int Run(string outDir, string repoRoot)
        {
            string svgDir = Path.Combine(outDir, "recipes");
            string caseDir = Path.Combine(outDir, "recipes-cases");
            Directory.CreateDirectory(svgDir);
            Directory.CreateDirectory(caseDir);
            // 清掉上一次的产物，避免改名/删例之后留下僵尸图（c03 曾从 ..._edges 改名成 ..._zoning）
            foreach (var old in Directory.GetFiles(svgDir, "*.svg")) File.Delete(old);
            foreach (var old in Directory.GetFiles(svgDir, "*.png")) File.Delete(old);

            Console.WriteLine("=== PlacemakerCore 驱动器 · Recipe 嵌套（阶段 4）===");
            Console.WriteLine("输出目录: " + outDir);
            Console.WriteLine();

            Console.WriteLine("[registry] GeneratorRegistry.All → " + ProbeRegistry());
            Console.WriteLine();

            // ------------------------------------------------ 站点（和阶段 2 的 street 场景同源）
            float3[] ring = Program.Rect(200f, 140f);
            var nearby = new List<NearbyNet> { Street(200f) };
            var grids = ZoneGrids(200f, 140f);
            var site = new SiteContext { LayoutScale = 1f, EdgeMargin = SiteContext.DefaultEdgeMargin };
            site.NearbyNets.AddRange(nearby);
            site.ZoneGrids.AddRange(grids);
            var shape = new AreaShape { Ring = ring, Source = AreaSource.Drawn };
            double siteArea = Pricing.PolygonArea(ring);

            Console.WriteLine("[site] 200 × 140 = " + F(siteArea) + " m²，南侧一条 8 m 双向街（ExternalId 9001），分区网格 " +
                              grids[0].Size.x + " × " + grids[0].Size.y + " 格");
            Console.WriteLine();

            // ------------------------------------------------ 用例
            var custom = CustomCases();
            var shipped = ShippedCases(repoRoot);
            var cases = new List<RecipeCase>();
            cases.AddRange(custom);
            cases.AddRange(shipped);

            Console.WriteLine("[cases] 手写嵌套 " + custom.Count + " 个 + 仓库自带 " + shipped.Count + " 个");
            Console.WriteLine();

            var results = new List<CaseResult>();
            foreach (var c in cases)
            {
                var r = Exec(c, shape, site, siteArea);
                results.Add(r);
                ConsoleOut(r);
                if (c.Json != null)
                    File.WriteAllText(Path.Combine(caseDir, Safe(c.Id) + ".json"), c.Json, new UTF8Encoding(false));
                string svg = Program.BuildLayersSvg(ring, nearby, r.Plans, c.Id + "  " + c.Desc,
                    Verdict(r) + "  layers=" + r.Layers.Count +
                    "  regions " + r.RegionsFilled + "/" + r.RegionsEmitted +
                    "  targets " + r.TargetsHit + "/" + r.TargetsDeclared);
                File.WriteAllText(Path.Combine(svgDir, Safe(c.Id) + ".svg"), svg, new UTF8Encoding(false));
            }

            // 清掉已经不存在的手写用例的 json（用例改名后别留孤儿），但不动仍在用的文件
            var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in custom) keep.Add(Safe(c.Id) + ".json");
            foreach (var old in Directory.GetFiles(caseDir, "*.json"))
                if (!keep.Contains(Path.GetFileName(old))) File.Delete(old);

            File.WriteAllText(Path.Combine(outDir, "recipes.md"), BuildMarkdown(results, siteArea), new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(outDir, "recipes.csv"), BuildCsv(results), new UTF8Encoding(false));
            PrintSummary(results);
            Console.WriteLine();
            Console.WriteLine("[out] " + svgDir + "/*.svg     每个用例一张多图层图（图例 = #i 图层 id @区域键）");
            Console.WriteLine("[out] " + caseDir + "/*.json   手写用例的配方原文，改这个再跑即可");
            Console.WriteLine("[out] recipes.md / recipes.csv");
            return 0;
        }

        // ================================================================ 执行一个用例
        private static CaseResult Exec(RecipeCase c, AreaShape shape, SiteContext site, double siteArea)
        {
            var r = new CaseResult { Case = c };
            var sw = Stopwatch.StartNew();

            try
            {
                string text = c.Json ?? File.ReadAllText(c.FilePath, Encoding.UTF8);
                text = text.TrimStart('\uFEFF');
                r.Recipe = RecipeJson.Parse(text).Recipe;
            }
            catch (Exception ex)
            {
                r.Crashed = true;
                r.Error = "解析失败: " + Flatten(ex);
                sw.Stop(); r.Ms = sw.ElapsedMilliseconds;
                return r;
            }

            ComposedPlan composed = null;
            var layerClock = Stopwatch.StartNew();
            try
            {
                using (var cts = new CancellationTokenSource(Budget))
                    composed = Composer.Compose(r.Recipe, shape, site, cts.Token);
            }
            catch (OperationCanceledException)
            {
                r.Error = "超过 " + Budget.TotalSeconds.ToString("0", INV) + "s（图层未响应取消令牌）";
            }
            catch (Exception ex)
            {
                r.Crashed = true;
                r.Error = Flatten(ex);
            }
            layerClock.Stop();
            sw.Stop(); r.Ms = sw.ElapsedMilliseconds;
            if (composed == null) return r;

            r.Plans.AddRange(composed.Layers);

            // 装配层行
            foreach (var plan in r.Plans)
            {
                var row = new LayerRow
                {
                    Index = r.Layers.Count,
                    Id = plan.GeneratorId ?? "?",
                    StepPath = plan.StepPath ?? "",
                    Surf = plan.Surfaces.Count,
                    Nets = plan.Nets.Count,
                    Obj = plan.Objects.Count,
                    Areas = plan.Areas.Count,
                    Reg = plan.Regions.Count,
                    Zone = plan.Zoning.Count,
                    Terr = plan.Terraform.Count,
                    Paint = plan.Paint.Count,
                };
                int at = row.StepPath.IndexOf('@');
                row.ParentPath = at >= 0 ? row.StepPath.Substring(0, at) : row.StepPath;
                row.RegionKey = at >= 0 ? row.StepPath.Substring(at + 1) : "";
                row.ParentPath = ParentOf(row.ParentPath);

                foreach (var s in plan.Surfaces)
                    if (s.Ring != null && s.Ring.Length >= 3) row.SurfArea += Pricing.PolygonArea(s.Ring);
                // park 的草地走的是 Paint 通道而不是 Surface：不算进来，"覆盖率"会误报 0%
                foreach (var pt in plan.Paint)
                    if (pt.Ring != null && pt.Ring.Length >= 3) row.SurfArea += Pricing.PolygonArea(pt.Ring);

                double ownMs = 0;
                foreach (var kv in plan.Timings) ownMs += kv.Value;
                row.Ms = (long)Math.Round(ownMs);

                var keys = new StringBuilder();
                foreach (var it in plan.Issues)
                {
                    if (it.Severity == IssueSeverity.Error) row.Err++;
                    else if (it.Severity == IssueSeverity.Warning) row.Warn++;
                    string sk = Short(it.Key);
                    r.IssueHist.TryGetValue(sk, out int prev);
                    r.IssueHist[sk] = prev + 1;
                    if (keys.Length < 160)
                    {
                        if (keys.Length > 0) keys.Append(" ");
                        keys.Append(Short(it.Key));
                    }
                }
                foreach (var o in plan.Objects) if (o.Role == ObjectRole.Decal) row.Dec++;
                row.IssueKeys = keys.ToString();
                r.Layers.Add(row);

                r.Surf += row.Surf; r.Nets += row.Nets; r.Obj += row.Obj; r.Areas += row.Areas;
                r.Zone += row.Zone; r.Paint += row.Paint; r.Err += row.Err; r.Warn += row.Warn;
                r.Decals += row.Dec;
                r.SurfArea += row.SurfArea;
            }

            // 1) target 声明与命中：recipe 里写的每个 target，父层到底有没有这种区域
            var targets = new List<string>();
            CollectTargets(r.Recipe.Root, targets);
            r.TargetsDeclared = targets.Count;
            foreach (var t in targets)
            {
                int hit = 0;
                foreach (var plan in r.Plans)
                    foreach (var reg in plan.Regions)
                        if (Composer.Matches(t, reg)) hit++;
                if (hit > 0) r.TargetsHit++;
                r.TargetDetail += (r.TargetDetail.Length > 0 ? "  " : "") + t + "=" + hit;
            }

            // 2) 区域消费：只统计"被某个 target 点过名"的区域。
            //    green:0 / paved / core 这类是给子层看的说明性区域，没人点名属正常，不该算空洞。
            var plannedKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var plan in r.Plans)
            {
                int at = (plan.StepPath ?? "").IndexOf('@');
                if (at >= 0) plannedKeys.Add(plan.StepPath.Substring(at + 1));
            }
            foreach (var row in r.Layers)
            {
                var plan = r.Plans[row.Index];
                int unfilled = 0;
                var names = new StringBuilder();
                foreach (var reg in plan.Regions)
                {
                    bool wanted = false;
                    foreach (var t in targets) if (Composer.Matches(t, reg)) { wanted = true; break; }
                    if (!wanted) continue;
                    row.Emitted++;
                    r.RegionsEmitted++;
                    if (plannedKeys.Contains(reg.Key ?? "")) { r.RegionsFilled++; continue; }
                    unfilled++;
                    if (names.Length < 70) { if (names.Length > 0) names.Append(","); names.Append(reg.Key); }
                }
                row.Unfilled = unfilled;
                if (unfilled > 0) row.IssueKeys = (row.IssueKeys + " 空洞:" + names).Trim();
            }

            // 3) 归属包含：子层对象落在它被指派的那个区域环里的比例。
            //    同一父步骤会给多个区域各跑一次（6 个块 → 6 个 park，StepPath 的路径部分相同），
            //    所以"父层"不唯一：取能装下最多对象的那个候选环，再看实际比例。
            foreach (var row in r.Layers)
            {
                if (row.RegionKey.Length == 0 || row.RegionKey == RegionTags.Root) continue;
                var plan = r.Plans[row.Index];
                if (plan.Objects.Count == 0) continue;
                int best = 0;
                foreach (var host in FindRegions(r.Plans, row.ParentPath, row.RegionKey))
                {
                    if (host == null || host.Length < 3) continue;
                    var ring2 = Poly.To2D(host);
                    int inside = 0;
                    foreach (var o in plan.Objects)
                        if (Poly.Contains(ring2, o.Position.xz)) inside++;
                    if (inside > best) best = inside;
                }
                if (best == 0) continue;
                row.Inside = best;
                row.InsideTotal = plan.Objects.Count;
            }

            var top = new StringBuilder();
            if (composed.Issues != null)
                foreach (var it in composed.Issues)
                {
                    if (it.Severity == IssueSeverity.Error) r.Err++;
                    else if (it.Severity == IssueSeverity.Warning) r.Warn++;
                    if (top.Length < 200)
                    {
                        if (top.Length > 0) top.Append(" | ");
                        top.Append(Short(it.Key));
                        if (it.Args != null && it.Args.Length > 0) top.Append("(" + string.Join(",", it.Args) + ")");
                    }
                }
            r.ComposerIssues = top.ToString();

            return r;
        }

        /// <summary>"0/1" → "0"；"0" → ""（根）。</summary>
        private static string ParentOf(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            int i = path.LastIndexOf('/');
            return i < 0 ? "" : path.Substring(0, i);
        }

        /// <summary>
        /// 在父层（StepPath 的 path 部分 == parentPath）里找所有 Key == regionKey 的区域环。
        /// 同一父步骤会对多个区域各跑一次，所以这里可能不止一个候选。
        /// </summary>
        private static List<float3[]> FindRegions(List<AreaPlan> plans, string parentPath, string regionKey)
        {
            var hit = new List<float3[]>();
            foreach (var plan in plans)
            {
                var sp = plan.StepPath ?? "";
                int at = sp.IndexOf('@');
                string p = at >= 0 ? sp.Substring(0, at) : sp;
                if (p != parentPath) continue;
                foreach (var reg in plan.Regions)
                    if (reg.Key == regionKey && reg.Ring != null && reg.Ring.Length >= 3) hit.Add(reg.Ring);
            }
            if (hit.Count == 0)
                foreach (var plan in plans)
                    foreach (var reg in plan.Regions)
                        if (reg.Key == regionKey && reg.Ring != null && reg.Ring.Length >= 3) hit.Add(reg.Ring);
            return hit;
        }

        private static void CollectTargets(RecipeStep step, List<string> into)
        {
            if (step == null) return;
            if (!string.IsNullOrEmpty(step.Target) && !into.Contains(step.Target)) into.Add(step.Target);
            foreach (var c in step.Children) CollectTargets(c, into);
            foreach (var o in step.RegionOverrides.Values) CollectTargets(o, into);
        }

        // ================================================================ 手写用例
        private static List<RecipeCase> CustomCases()
        {
            var list = new List<RecipeCase>();
            void Add(string id, string desc, string json, string expect = "ok") =>
                list.Add(new RecipeCase { Id = id, Desc = desc, Json = json.Trim(), Expect = expect });

            // 1) add-on：子层不写 target → 在父层同一个区域里加东西（Composition 头注释第 2 行）
            Add("c01_park_restrooms", "park ─ restrooms（同区域 add-on）", @"
{
  ""formatVersion"": 1, ""id"": ""c01_park_restrooms"", ""name"": ""park + restrooms"",
  ""root"": {
    ""layer"": ""park"", ""seed"": 21,
    ""settings"": { ""style"": ""natural"", ""treeDensity"": 28 },
    ""children"": [ { ""layer"": ""restrooms"", ""seed"": 22 } ]
  }
}");

            // 2) 路网 → 公园（target "block"）：最典型的一张街区 recipe
            Add("c02_roads_park", "roads ─ park（target block）", @"
{
  ""formatVersion"": 1, ""id"": ""c02_roads_park"", ""name"": ""roads with parks"",
  ""root"": {
    ""layer"": ""roads"", ""seed"": 31,
    ""settings"": { ""pattern"": ""grid"", ""gridSpacing"": 42 },
    ""children"": [ { ""layer"": ""park"", ""target"": ""block"", ""seed"": 32 } ]
  }
}");

            // 3) 路网 → 公园（块）+ 分区（块）：一条 recipe 两个子层打同一个 target
            //    注：老写法 roads ─ edgeZoning(edge) 已不可用 —— RecipeJson.Read 里的 RoadsLayer.Migrate
            //    会把"target=edge 的 edgeZoning 子层"删掉（该功能被 preZone 取代，而 preZone 现在也关着）。
            //    所以给路网配分区只能整体铺 zoning* 图层（见 c03 / c12）。
            Add("c03_roads_park_zoning", "roads ─┬ park（block） └ zoningSuburb（block）", @"
{
  ""formatVersion"": 1, ""id"": ""c03_roads_park_zoning"", ""name"": ""roads + parks + zoning"",
  ""root"": {
    ""layer"": ""roads"", ""seed"": 41,
    ""settings"": { ""pattern"": ""grid"", ""gridSpacing"": 44 },
    ""children"": [
      { ""layer"": ""park"", ""target"": ""block"", ""seed"": 42 },
      { ""layer"": ""zoningSuburb"", ""target"": ""block"", ""seed"": 43 }
    ]
  }
}");

            // 4) regionOverrides：同一批块里挑一块换成广场
            Add("c04_roads_override_plaza", "roads ─ park（block），block:1 被 regionOverrides 换成 plaza", @"
{
  ""formatVersion"": 1, ""id"": ""c04_roads_override_plaza"", ""name"": ""roads with one plaza block"",
  ""root"": {
    ""layer"": ""roads"", ""seed"": 51,
    ""settings"": { ""pattern"": ""grid"", ""gridSpacing"": 44 },
    ""children"": [ { ""layer"": ""park"", ""target"": ""block"", ""seed"": 52 } ],
    ""regionOverrides"": { ""block:1"": { ""layer"": ""plaza"", ""seed"": 53 } }
  }
}");

            // 5) split → 两半，各填一种
            Add("c05_split_park_plaza", "split（55%）─┬ park（part:0） └ plaza（part:1）", @"
{
  ""formatVersion"": 1, ""id"": ""c05_split_park_plaza"", ""name"": ""split park | plaza"",
  ""root"": {
    ""layer"": ""split"", ""seed"": 61,
    ""settings"": { ""ratio"": 55, ""angle"": 0, ""parts"": 2, ""divider"": ""path"" },
    ""children"": [
      { ""layer"": ""park"", ""target"": ""part:0"", ""seed"": 62 },
      { ""layer"": ""plaza"", ""target"": ""part:1"", ""seed"": 63 }
    ]
  }
}");

            // 6) 公园 ─ 广场（target "place:plaza"）：父层因为子层要它，主动把广场挖出来
            Add("c06_park_plaza_place", "park ─ plaza（target place:plaza，父层挖地）", @"
{
  ""formatVersion"": 1, ""id"": ""c06_park_plaza_place"", ""name"": ""park with central plaza"",
  ""root"": {
    ""layer"": ""park"", ""seed"": 71,
    ""settings"": { ""plazaSize"": 18, ""plazaAt"": ""centre"" },
    ""children"": [ { ""layer"": ""plaza"", ""target"": ""place:plaza"", ""seed"": 72 } ]
  }
}");

            // 7) 三层：路网 → 切分 → 公园/广场。验证 MaxDepth 与 StepPath 链路
            Add("c07_roads_split_parks", "roads ─ split（block）─┬ park（part:0） └ plaza（part:1）", @"
{
  ""formatVersion"": 1, ""id"": ""c07_roads_split_parks"", ""name"": ""roads, half park half plaza"",
  ""root"": {
    ""layer"": ""roads"", ""seed"": 81,
    ""settings"": { ""pattern"": ""grid"", ""gridSpacing"": 46 },
    ""children"": [
      {
        ""layer"": ""split"", ""target"": ""block"", ""seed"": 82,
        ""settings"": { ""ratio"": 60, ""angle"": 90, ""parts"": 2, ""divider"": ""path"" },
        ""children"": [
          { ""layer"": ""park"", ""target"": ""part:0"", ""seed"": 83 },
          { ""layer"": ""plaza"", ""target"": ""part:1"", ""seed"": 84 }
        ]
      }
    ]
  }
}");

            // 8) decals 作为子层：**这是本阶段的一个负向发现** —— 落叶在 s33 之后改走地形贴图，
            //    "decals" 图层在当前版本无论单跑还是进 recipe 都出 0 件（见汇总的 decal= 列）。
            Add("c08_park_decals", "park ─ decals（实测 0 件：落叶已改地形贴图，该图层当前空转）", @"
{
  ""formatVersion"": 1, ""id"": ""c08_park_decals"", ""name"": ""park with leaf decals"",
  ""root"": {
    ""layer"": ""park"", ""seed"": 91,
    ""settings"": { ""style"": ""natural"", ""treeDensity"": 40 },
    ""children"": [ { ""layer"": ""decals"", ""seed"": 92, ""settings"": { ""theme"": ""leaves"", ""density"": 0.8 } } ]
  }
}");

            // 9) 失败隔离：子层 id 不存在，父层与另一个兄弟层必须照常出图
            Add("c09_unknown_child", "roads ─┬ noSuchLayer（错误） └ park（block）", @"
{
  ""formatVersion"": 1, ""id"": ""c09_unknown_child"", ""name"": ""broken child"",
  ""root"": {
    ""layer"": ""roads"", ""seed"": 101,
    ""settings"": { ""pattern"": ""grid"", ""gridSpacing"": 44 },
    ""children"": [
      { ""layer"": ""noSuchLayer"", ""target"": ""block"" },
      { ""layer"": ""park"", ""target"": ""block"", ""seed"": 102 }
    ]
  }
}", "error");

            // 10) 根层就坏：必须只有一条 Error issue，不抛异常、不产出任何层
            Add("c10_unknown_root", "根层 id 不存在（预期：1 条 Error，0 层）", @"
{
  ""formatVersion"": 1, ""id"": ""c10_unknown_root"", ""name"": ""broken root"",
  ""root"": { ""layer"": ""noSuchLayer"", ""settings"": {} }
}", "error");

            // 11) 目标落空：target 写成了父层根本不产出的标签
            Add("c11_target_miss", "park ─ plaza（target 写成 block，公园不产出 block → 必须报 0）", @"
{
  ""formatVersion"": 1, ""id"": ""c11_target_miss"", ""name"": ""target that never matches"",
  ""root"": {
    ""layer"": ""park"", ""seed"": 111,
    ""children"": [ { ""layer"": ""plaza"", ""target"": ""block"", ""seed"": 112 } ]
  }
}", "miss");

            // 12) 现实里最常见的一张"开发地块"：一半是可建分区，一半是公园
            Add("c12_split_zoning_park", "split（60%）─┬ zoningTownCentre（part:0） └ park（part:1）", @"
{
  ""formatVersion"": 1, ""id"": ""c12_split_zoning_park"", ""name"": ""half zoned lots, half park"",
  ""root"": {
    ""layer"": ""split"", ""seed"": 121,
    ""settings"": { ""ratio"": 60, ""angle"": 0, ""parts"": 2, ""divider"": ""none"" },
    ""children"": [
      { ""layer"": ""zoningTownCentre"", ""target"": ""part:0"", ""seed"": 122 },
      { ""layer"": ""park"", ""target"": ""part:1"", ""seed"": 123 }
    ]
  }
}");

            // 13) 同 c03，只把两个子层的顺序反过来：先分区、后建园。
            //     Composer 按 Children 顺序跑、先跑的先占地，所以这条 recipe 的产物和 c03 不该一样。
            Add("c13_roads_zoning_first", "roads ─┬ zoningSuburb（block） └ park（block）  ← 只是顺序反了", @"
{
  ""formatVersion"": 1, ""id"": ""c13_roads_zoning_first"", ""name"": ""roads + zoning then parks"",
  ""root"": {
    ""layer"": ""roads"", ""seed"": 41,
    ""settings"": { ""pattern"": ""grid"", ""gridSpacing"": 44 },
    ""children"": [
      { ""layer"": ""zoningSuburb"", ""target"": ""block"", ""seed"": 43 },
      { ""layer"": ""park"", ""target"": ""block"", ""seed"": 42 }
    ]
  }
}");

            return list;
        }

        // ================================================================ 仓库自带 recipe
        private static List<RecipeCase> ShippedCases(string repoRoot)
        {
            var list = new List<RecipeCase>();
            string dir = Path.Combine(repoRoot, "Recipes");
            if (!Directory.Exists(dir))
            {
                Console.WriteLine("[warn] 找不到 " + dir + "（跳过自带 recipe）");
                return list;
            }
            var files = Directory.GetFiles(dir, "*.json");
            Array.Sort(files, StringComparer.Ordinal);
            foreach (var f in files)
                list.Add(new RecipeCase
                {
                    Id = "ship_" + Path.GetFileNameWithoutExtension(f),
                    Desc = "仓库自带 recipe",
                    FilePath = f,
                });
            return list;
        }

        /// <summary>从 outputs 目录往上找 placemaker-master/placemaker-master。</summary>
        internal static string FindRepoRoot()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            for (int i = 0; i < 10 && d != null; i++, d = d.Parent)
            {
                string c = Path.Combine(d.FullName, "placemaker-master", "placemaker-master", "Recipes");
                if (Directory.Exists(c)) return Path.Combine(d.FullName, "placemaker-master", "placemaker-master");
            }
            return null;
        }

        // ================================================================ 站点构造
        private static NearbyNet Street(float w)
        {
            float3 a = new float3(-20f, 0f, -6f);
            float3 d = new float3(w + 20f, 0f, -6f);
            return new NearbyNet
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
            };
        }

        private static List<ZoneGrid> ZoneGrids(float w, float h)
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

        // ================================================================ 输出
        private static void ConsoleOut(CaseResult r)
        {
            string head = "[" + r.Case.Id + "]  " + r.Case.Desc;
            string stat = "  layers=" + r.Layers.Count +
                          "  targets=" + r.TargetsHit + "/" + r.TargetsDeclared +
                          "  被点名区域=" + r.RegionsFilled + "/" + r.RegionsEmitted +
                          "  obj=" + r.Obj + "  net=" + r.Nets + "  decal=" + r.Decals +
                          "  " + r.Ms + "ms  " + Verdict(r);
            Console.WriteLine(head);
            Console.WriteLine("    " + stat);
            if (r.Error.Length > 0) Console.WriteLine("    ! " + r.Error);
            if (r.ComposerIssues.Length > 0) Console.WriteLine("    composer: " + r.ComposerIssues);

            int shown = Math.Min(r.Layers.Count, 8);
            for (int i = 0; i < shown; i++)
            {
                var L = r.Layers[i];
                var sb = new StringBuilder();
                sb.Append("    #").Append(L.Index.ToString("00", INV)).Append(' ').Append(L.Id.PadRight(15));
                sb.Append(" @").Append(L.RegionKey.PadRight(12));
                sb.Append(" s").Append(L.Surf).Append(" n").Append(L.Nets).Append(" o").Append(L.Obj)
                  .Append(" a").Append(L.Areas).Append(" r").Append(L.Reg);
                if (L.Zone > 0) sb.Append(" z").Append(L.Zone);
                if (L.Terr > 0) sb.Append(" t").Append(L.Terr);
                if (L.Paint > 0) sb.Append(" p").Append(L.Paint);
                if (L.InsideTotal > 0) sb.Append("  in=").Append(L.Inside).Append("/").Append(L.InsideTotal);
                if (L.Err + L.Warn > 0) sb.Append("  !e").Append(L.Err).Append(" w").Append(L.Warn);
                if (L.IssueKeys.Length > 0) sb.Append("  ").Append(L.IssueKeys);
                Console.WriteLine(sb.ToString());
            }
            if (r.Layers.Count > shown) Console.WriteLine("    … 另 " + (r.Layers.Count - shown) + " 层");
            Console.WriteLine();
        }

        private static void PrintSummary(List<CaseResult> rs)
        {
            int pass = 0, fail = 0, crash = 0, neg = 0, layers = 0, unfilled = 0, missTarget = 0;
            long ms = 0;
            foreach (var r in rs)
            {
                if (r.Case.Expect != "ok") neg++;
                if (r.Crashed) crash++;
                else if (r.AsExpected) pass++;
                else fail++;
                layers += r.Layers.Count;
                ms += r.Ms;
                unfilled += r.RegionsEmitted - r.RegionsFilled;
                missTarget += r.TargetsDeclared - r.TargetsHit;
            }
            Console.WriteLine("=== 汇总 ===");
            Console.WriteLine("  用例 " + rs.Count + "（PASS " + pass + " / FAIL " + fail + " / CRASH " + crash + "）  共 " +
                              layers + " 层  用时 " + ms + " ms");
            Console.WriteLine("  其中负向用例 " + neg + " 个（c09 子层 id 不存在、c10 根层 id 不存在、c11 target 落空）");
            Console.WriteLine("  被 target 点名却没人填的区域 " + unfilled + " 个；写了 target 却匹配不到区域 " + missTarget + " 次");
            Console.WriteLine("  FAIL 只有一种含义：**没达到用例自己声明的预期**（负向用例该报错却没报错也算 FAIL）。");

            var hist = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var r in rs)
                foreach (var kv in r.IssueHist)
                {
                    hist.TryGetValue(kv.Key, out int prev);
                    hist[kv.Key] = prev + kv.Value;
                }
            if (hist.Count > 0)
            {
                var top = new List<KeyValuePair<string, int>>(hist);
                top.Sort((a, b) => b.Value.CompareTo(a.Value));
                Console.WriteLine();
                Console.WriteLine("  最常见的 issue（短名 / 次数）：");
                for (int i = 0; i < top.Count && i < 10; i++)
                    Console.WriteLine("    " + top[i].Key.PadRight(22) + top[i].Value);
            }
        }

        private static string BuildMarkdown(List<CaseResult> rs, double siteArea)
        {
            var sb = new StringBuilder();
            sb.Append("# 阶段 4 · Recipe 父/子图层嵌套\n\n");
            sb.Append("站点 200 × 140 = ").Append(F(siteArea)).Append(" m²，南侧一条 8 m 双向街，8 m 分区网格全覆盖。\n");
            sb.Append("所有用例都走 `RecipeJson.Parse` → `Composer.Compose`，和游戏加载存档是同一条代码路径。\n\n");
            sb.Append("## 总表\n\n");
            sb.Append("| 用例 | 说明 | 层 | target 命中 | 被点名区域已填 | 对象 | 路网 | 面 | 分区格 | 涂装 | 用时 | 结果 |\n");
            sb.Append("|---|---|--:|--:|--:|--:|--:|--:|--:|--:|--:|---|\n");
            foreach (var r in rs)
            {
                sb.Append("| `").Append(r.Case.Id).Append("` | ").Append(Esc(r.Case.Desc))
                  .Append(" | ").Append(r.Layers.Count)
                  .Append(" | ").Append(r.TargetsHit).Append("/").Append(r.TargetsDeclared)
                  .Append(" | ").Append(r.RegionsFilled).Append("/").Append(r.RegionsEmitted)
                  .Append(" | ").Append(r.Obj)
                  .Append(" | ").Append(r.Nets)
                  .Append(" | ").Append(r.Surf)
                  .Append(" | ").Append(r.Zone)
                  .Append(" | ").Append(r.Paint)
                  .Append(" | ").Append(r.Ms).Append(" ms")
                  .Append(" | ").Append(Verdict(r)).Append(" |\n");
            }
            sb.Append('\n');

            foreach (var r in rs)
            {
                sb.Append("## ").Append(r.Case.Id).Append(" — ").Append(r.Case.Desc).Append("\n\n");
                if (r.Error.Length > 0) sb.Append("**错误**：").Append(r.Error).Append("\n\n");
                if (r.ComposerIssues.Length > 0) sb.Append("**Composer issue**：`").Append(r.ComposerIssues).Append("`\n\n");
                if (r.TargetDetail.Length > 0) sb.Append("**target 命中**：`").Append(r.TargetDetail).Append("`\n\n");
                sb.Append("对象 ").Append(r.Obj).Append("（其中 decal ").Append(r.Decals).Append("）、路网 ").Append(r.Nets)
                  .Append(" 条、面 ").Append(r.Surf).Append("、分区格 ").Append(r.Zone).Append("、地形涂装 ").Append(r.Paint)
                  .Append(" 块（合计 ").Append(F(r.SurfArea)).Append(" m²，含相互重叠，仅供参考）\n\n");
                if (r.Layers.Count == 0) { sb.Append("（无图层产出）\n\n"); continue; }

                sb.Append("| # | 图层 | 区域键 | 面 | 路网 | 对象 | 通行区 | 区域 | 分区 | 地形 | 归属包含 | issue |\n");
                sb.Append("|--:|---|---|--:|--:|--:|--:|--:|--:|--:|--:|---|\n");
                foreach (var L in r.Layers)
                {
                    sb.Append("| ").Append(L.Index).Append(" | `").Append(L.Id).Append("` | `").Append(L.RegionKey).Append("`")
                      .Append(" | ").Append(L.Surf).Append(" | ").Append(L.Nets).Append(" | ").Append(L.Obj)
                      .Append(" | ").Append(L.Areas).Append(" | ").Append(L.Reg).Append(" | ").Append(L.Zone)
                      .Append(" | ").Append(L.Terr)
                      .Append(" | ").Append(L.InsideTotal > 0 ? (L.Inside + "/" + L.InsideTotal) : "—")
                      .Append(" | ").Append(Esc(L.IssueKeys)).Append(" |\n");
                }
                sb.Append('\n');
            }
            return sb.ToString();
        }

        private static string BuildCsv(List<CaseResult> rs)
        {
            var sb = new StringBuilder();
            sb.Append("case,desc,layers,regions_filled,regions_emitted,targets_hit,targets_declared,obj,nets,surf,surf_area,ms,ok,err,warn\n");
            foreach (var r in rs)
                sb.Append(Csv(r.Case.Id)).Append(',').Append(Csv(r.Case.Desc)).Append(',')
                  .Append(r.Layers.Count).Append(',').Append(r.RegionsFilled).Append(',').Append(r.RegionsEmitted).Append(',')
                  .Append(r.TargetsHit).Append(',').Append(r.TargetsDeclared).Append(',')
                  .Append(r.Obj).Append(',').Append(r.Nets).Append(',').Append(r.Surf).Append(',')
                  .Append(r.SurfArea.ToString("0.##", INV)).Append(',').Append(r.Ms).Append(',')
                  .Append(Verdict(r)).Append(',')
                  .Append(r.Err).Append(',').Append(r.Warn).Append('\n');

            sb.Append('\n');
            sb.Append("# 分图层\n");
            sb.Append("case,idx,layer,step_path,region_key,surf,nets,obj,areas,regions,zone,terraform,paint,inside,inside_total,ms\n");
            foreach (var r in rs)
                foreach (var L in r.Layers)
                    sb.Append(Csv(r.Case.Id)).Append(',').Append(L.Index).Append(',').Append(Csv(L.Id)).Append(',')
                      .Append(Csv(L.StepPath)).Append(',').Append(Csv(L.RegionKey)).Append(',')
                      .Append(L.Surf).Append(',').Append(L.Nets).Append(',').Append(L.Obj).Append(',')
                      .Append(L.Areas).Append(',').Append(L.Reg).Append(',').Append(L.Zone).Append(',')
                      .Append(L.Terr).Append(',').Append(L.Paint).Append(',')
                      .Append(L.Inside).Append(',').Append(L.InsideTotal).Append(',').Append(L.Ms).Append('\n');
            return sb.ToString();
        }

        // ================================================================ 小工具
        private static string Verdict(CaseResult r)
        {
            if (r.Crashed) return "CRASH";
            if (r.Case.Expect != "ok") return r.AsExpected ? "PASS·负向" : "FAIL·负向";
            return r.AsExpected ? "PASS" : "FAIL";
        }

        private static string ProbeRegistry()
        {
            try
            {
                var all = GeneratorRegistry.All;
                return all.Count + " 个生成器（Activator.CreateInstance 全部成功）";
            }
            catch (Exception ex)
            {
                return "FAILED: " + Flatten(ex);
            }
        }

        private static string Flatten(Exception ex)
        {
            var sb = new StringBuilder();
            for (var e = ex; e != null; e = e.InnerException)
            {
                if (sb.Length > 0) sb.Append(" <- ");
                sb.Append(e.GetType().Name).Append(": ").Append(e.Message);
            }
            return sb.ToString();
        }

        private static string Short(string key)
        {
            if (string.IsNullOrEmpty(key)) return "";
            int i = key.LastIndexOf('.');
            return i >= 0 ? key.Substring(i + 1) : key;
        }

        private static string Safe(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
                sb.Append(char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_');
            return sb.ToString();
        }

        private static string Csv(string s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";
        private static string Esc(string s) => (s ?? "").Replace("|", "\\|");
        private static string F(double v) => v.ToString("0.#", INV);
        private static string Pct(double part, double whole) => whole <= 0 ? "—" : (part * 100.0 / whole).ToString("0.0", INV) + "%";
    }
}
