// PlacemakerCore 驱动器（阶段 2）：跑多个"路网场景"，验证这条链：
//   SiteContext.NearbyNets → EntranceSuggest → Markers → CheckReady → Plan → 内部车道连通
//
// 为什么需要多场景：上一版只有一个"四周无路"的地块，EntranceSuggest 直接返回空，
// CheckReady 因此 NOT READY —— 那只验证了"算法正确地说没路"，没验证"有路时能用"。
// 这版把 EntranceSuggest 的三条分支都喂到：
//   1) 有路 + 长临街面 → entry/exit 同一街道（同一街道上离得远）
//   2) 有路 + 短临街面 → 单个双向 entrance
//   3) 无路 + Shape.Attachments → 退化为"沿边界附着"的兜底分支
//
// 用法：PlacemakerDriver [输出目录]      默认 ./out
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using Unity.Mathematics;
using Placemaker.Core;
using Placemaker.Core.Layers.Parking;

namespace Placemaker.Driver
{
    internal sealed class Scenario
    {
        public string Name = "";
        public string Desc = "";
        public float3[] Ring = Array.Empty<float3>();
        public List<NearbyNet> Roads = new List<NearbyNet>();
        public List<BoundaryAttachment> Attachments = new List<BoundaryAttachment>();
        /// <summary>覆盖默认设置（键见 ParkingIds，例如 entranceStyle = "connect" | "alley"）。</summary>
        public Dictionary<string, object> Overrides = new Dictionary<string, object>();
    }

    internal sealed class Result
    {
        public string Name = "";
        public string Desc = "";
        public bool ReadyBefore, ReadyAfter;
        public string Reason = "";
        public int Suggested, Markers;
        public string Kinds = "";
        public int Stalls, Aisles, Objects, Nets, Surfaces;
        public double AisleLen;
        public string Issues = "";
        public long Ms;
        public string Svg = "";
        public int ReadyFail;
        public int ConnComps, OrphanAisles, Connectors, ConnAttached;
        public string AttachIds = "";
    }

    /// <summary>车道网连通性结果。</summary>
    internal sealed class ConnInfo
    {
        public int Components;      // 车辆车道网的连通分量数
        public int OrphanAisles;    // 与"接驳口所在分量"不通的车道段
        public int Connectors;      // DriveConnector 条数
        public int ConnAttached;    // 其中显式挂到既有道路（ExternalId 命中）的条数
        public string AttachIds = "";
    }

    internal static class Program
    {
        private static readonly CultureInfo INV = CultureInfo.InvariantCulture;

        private static int Main(string[] args)
        {
            // 控制台日志里有中文，被 TS 侧的 execFileSync 按 UTF-8 抓走时，
            // 用系统默认代码页会变成乱码（烘焙日志里那串 "????"）。固定成 UTF-8。
            try { Console.OutputEncoding = new UTF8Encoding(false); }
            catch (IOException) { /* 没有控制台（重定向到文件）时忽略 */ }
            catch (System.Security.SecurityException) { /* 同上 */ }

            // 参数：PlacemakerDriver [输出目录] [--sweep] [--no-sweep] [--recipes] [--emit <case.json> <plan.json>]
            //   默认两段都跑：阶段 2 的 7 个停车场景 + 阶段 3 的全量生成器扫描
            //   --sweep     只跑全量扫描
            //   --no-sweep  只跑停车场景
            //   --recipes   只跑配方嵌套
            //   --emit      导出一个通用 Plan JSON（给 TownForge 之类的消费方），不跑其它任何东西
            int emitAt = Array.IndexOf(args, "--emit");
            if (emitAt >= 0)
            {
                if (args.Length < emitAt + 3)
                {
                    Console.Error.WriteLine("[emit] 用法: --emit <case.json> <plan.json>");
                    return 2;
                }
                return Emit.Run(args[emitAt + 1], args[emitAt + 2]);
            }

            // --attachments：只跑 Tool/AttachmentFinder，导出贴合点，作为 TS 移植的对照基准
            int attAt = Array.IndexOf(args, "--attachments");
            if (attAt >= 0)
            {
                if (args.Length < attAt + 3)
                {
                    Console.Error.WriteLine("[attachments] 用法: --attachments <case.json> <attachments.json>");
                    return 2;
                }
                return Attachments.Run(args[attAt + 1], args[attAt + 2]);
            }

            // --generators：反射导出 130 个生成器的 ready/建议归属，供 TS 侧生成家族表
            int genAt = Array.IndexOf(args, "--generators");
            if (genAt >= 0)
            {
                if (args.Length < genAt + 2)
                {
                    Console.Error.WriteLine("[generators] 用法: --generators <generators.json>");
                    return 2;
                }
                return Generators.Run(args[genAt + 1]);
            }

            bool sweepOnly = false, noSweep = false, recipesOnly = false, withRecipes = false;
            string outDirArg = null;
            foreach (var a in args)
            {
                if (a == "--sweep") sweepOnly = true;
                else if (a == "--no-sweep") noSweep = true;
                else if (a == "--recipes") recipesOnly = true;
                else if (a == "--with-recipes") withRecipes = true;
                else if (outDirArg == null) outDirArg = a;
            }

            string outDir = outDirArg ?? Path.Combine(Environment.CurrentDirectory, "out");
            Directory.CreateDirectory(outDir);
            if (sweepOnly) return Sweep.Run(outDir);
            if (recipesOnly) return Recipes.Run(outDir, Recipes.FindRepoRoot());

            Console.WriteLine("=== PlacemakerCore 驱动器 · 路网场景对比 ===");
            Console.WriteLine("输出目录: " + outDir);
            Console.WriteLine();

            var gen = new ParkingLayer();
            Console.WriteLine("[gen] id=" + gen.Id + "  kind=" + gen.Kind + "  version=" + gen.Version +
                              "  面积 " + F(gen.MinArea) + " .. " + F(gen.MaxArea) + " m²");
            Console.WriteLine();

            var ring = Rect(150f, 100f);
            var list = BuildScenarios(ring, out long area);
            Console.WriteLine("[area] 地块 " + F((float)area) + " m²（150 × 100）");
            Console.WriteLine();

            var results = new List<Result>();
            foreach (var sc in list)
            {
                Console.WriteLine("-------------------- " + sc.Name + "  " + sc.Desc);
                results.Add(Run(gen, sc, outDir));
                Console.WriteLine();
            }

            PrintTable(results);
            Console.WriteLine();
            Console.WriteLine("[out] 每个场景一张 SVG: plan-none / plan-street / plan-alley / plan-corner / plan-short / plan-wide / plan-attach");
            Console.WriteLine("[note] 列 \"接驳\"=接驳口数/其中显式挂接到既有道路的渠数；\"连通\"=车辆车道网连通分量数（1 最优）；\"孤立\"=走不到接驳口的车道段数");

            if (noSweep) return 0;
            Console.WriteLine();
            int rc = Sweep.Run(outDir);
            if (withRecipes) { Console.WriteLine(); rc = Recipes.Run(outDir, Recipes.FindRepoRoot()); }
            return rc;
        }

        // ---------------------------------------------------------------- 场景
        private static List<Scenario> BuildScenarios(float3[] ring, out long area)
        {
            area = (long)Math.Round(Math.Abs(SignedArea(ring)));
            var list = new List<Scenario>();

            list.Add(new Scenario
            {
                Name = "none",
                Desc = "无路网（基线，上一轮跑的就是这个）",
                Ring = ring,
            });

            // 南侧一条双向 8 m 街道，中心线 z = -6（路缘距地块边 2 m），长 190 m
            var street = new Scenario
            {
                Name = "street",
                Desc = "南侧街道 190 m（入口+出口同一条街）",
                Ring = ring,
            };
            street.Roads.Add(Line(-20f, -6f, 170f, -6f, 8f, false));
            list.Add(street);

            // 同一条街，但出入口改用 alley 形态：这条分支才会把渠显式挂到既有道路上
            // （默认 connect 形态是 LocalConnect，渠头停在路缘做 free node，不写 Attach）
            var alley = new Scenario
            {
                Name = "alley",
                Desc = "南侧街道 + 出入口 alley 形态（显式挂接）",
                Ring = ring,
            };
            alley.Roads.Add(Line(-20f, -6f, 170f, -6f, 8f, false));
            alley.Overrides["entranceStyle"] = "alley";
            list.Add(alley);

            // 转角：南侧 8 m + 西侧 8 m，两条街在地块西南角外侧交汇
            var corner = new Scenario
            {
                Name = "corner",
                Desc = "西南转角两条街（主街 + 次街）",
                Ring = ring,
            };
            corner.Roads.Add(Line(-20f, -6f, 170f, -6f, 8f, false));
            corner.Roads.Add(Line(-6f, -20f, -6f, 120f, 8f, false));
            list.Add(corner);

            // 短临街面：只有 60 m，短于 minApart(75)，走"单个双向出入口"分支
            var shortFront = new Scenario
            {
                Name = "short",
                Desc = "南侧仅 60 m 短街（单个双向出入口）",
                Ring = ring,
            };
            shortFront.Roads.Add(Line(45f, -6f, 105f, -6f, 8f, false));
            list.Add(shortFront);

            // 宽临街面：240 × 90（周长 660 → minApart = 99 m），240 m 沿街足够把入口和出口拉开
            // 专门覆盖 EntranceSuggest 里"entry/exit 分离在同一条街"的分支（上一个 street 场景覆盖不到）
            var wide = new Scenario
            {
                Name = "wide",
                Desc = "宽地块 240×90，240 m 临街（入口+出口分离）",
                Ring = Rect(240f, 90f),
            };
            wide.Roads.Add(Line(-20f, -6f, 260f, -6f, 8f, false));
            list.Add(wide);

            // 无路网数据，但地块边界带"沿边有路"的附着信息 → EntranceSuggest 的兜底分支
            var attach = new Scenario
            {
                Name = "attach",
                Desc = "无路网快照，仅边界附着（兜底分支）",
                Ring = ring,
            };
            attach.Attachments.Add(new BoundaryAttachment
            {
                Kind = AttachmentKind.RoadAlongEdge,
                ExternalId = 9001,
                ExternalT = 0.5f,
                Position = new float3(75f, 0f, 0f),
                Direction = new float3(1f, 0f, 0f),
                RoadWidth = 8f,
                RingSegment = 0,
                RingT = 0.5f,
            });
            list.Add(attach);

            return list;
        }

        /// <summary>直路：用三次贝塞尔表示，控制点按 1/3、2/3 取，退化即为直线。</summary>
        private static NearbyNet Line(float x0, float z0, float x1, float z1, float width, bool oneWay)
        {
            float3 a = new float3(x0, 0f, z0), d = new float3(x1, 0f, z1);
            return new NearbyNet
            {
                ExternalId = 1000 + (long)(Math.Abs(x0) + Math.Abs(z0) + Math.Abs(x1) + Math.Abs(z1)),
                A = a,
                B = a + (d - a) / 3f,
                C = a + (d - a) * (2f / 3f),
                D = d,
                Width = width,
                Category = NetCategory.Road,
                HasSidewalk = true,
                IsOneWay = oneWay,
                SpeedLimit = 50f,
            };
        }

        // ---------------------------------------------------------------- 单场景执行
        private static Result Run(ParkingLayer gen, Scenario sc, string outDir)
        {
            var r = new Result { Name = sc.Name, Desc = sc.Desc };

            var site = new SiteContext
            {
                LayoutScale = 1f,
                EdgeMargin = SiteContext.DefaultEdgeMargin,
            };
            site.NearbyNets.AddRange(sc.Roads);

            var bag = gen.Defaults();
            foreach (var kv in sc.Overrides) bag.Values[kv.Key] = kv.Value;

            var input = new AreaInput
            {
                Shape = new AreaShape { Ring = sc.Ring, Source = AreaSource.Drawn },
                Site = site,
                Settings = bag,
                Seed = 1234u,
                RegionTags = new[] { "root" },
            };
            input.Shape.Attachments.AddRange(sc.Attachments);

            var ready = gen.CheckReady(input);
            r.ReadyBefore = ready.Ready;
            r.Reason = ready.Ready ? "ok" : (ready.NextStepKey + " / " + ready.ReasonKey);
            Console.WriteLine("  [ready 放置前] " + (ready.Ready ? "READY" : "NOT READY  " + ready.NextStepKey + " " + ready.ReasonKey));

            var sug = gen.SuggestMarkers(input);
            r.Suggested = sug?.Count ?? 0;
            if (sug != null && sug.Count > 0)
            {
                input.Markers.AddRange(sug);
                var sb = new StringBuilder();
                foreach (var m in sug)
                {
                    if (sb.Length > 0) sb.Append(", ");
                    sb.Append(m.Kind).Append("@seg").Append(m.RingSegment).Append(" t=").Append(N(m.RingT));
                }
                r.Kinds = sb.ToString();
                Console.WriteLine("  [markers] 建议 " + sug.Count + " 个: " + r.Kinds);
            }
            else
            {
                Console.WriteLine("  [markers] 无建议");
            }

            var ready2 = gen.CheckReady(input);
            r.ReadyAfter = ready2.Ready;
            r.Markers = input.Markers.Count;
            Console.WriteLine("  [ready 放置后] " + (ready2.Ready ? "READY" : "NOT READY  " + ready2.ReasonKey));

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var plan = gen.Plan(input, CancellationToken.None);
            sw.Stop();
            r.Ms = sw.ElapsedMilliseconds;

            r.Nets = plan.Nets.Count;
            r.Objects = plan.Objects.Count;
            r.Surfaces = plan.Surfaces.Count;
            r.Stalls = (int)Math.Round(StatN(plan, ".Stalls"));
            r.Aisles = CountRole(plan.Nets, NetRole.DriveAisle) + CountRole(plan.Nets, NetRole.DriveConnector);
            r.AisleLen = StatN(plan, ".AisleLength");

            var ci = Analyze(plan.Nets, sc.Roads);
            r.ConnComps = ci.Components;
            r.OrphanAisles = ci.OrphanAisles;
            r.Connectors = ci.Connectors;
            r.ConnAttached = ci.ConnAttached;
            r.AttachIds = ci.AttachIds;

            if (plan.Issues.Count > 0)
            {
                var sb = new StringBuilder();
                foreach (var it in plan.Issues)
                {
                    if (sb.Length > 0) sb.Append(" | ");
                    sb.Append("[").Append(it.Severity).Append("] ").Append(Short(it.Key));
                }
                r.Issues = sb.ToString();
                Console.WriteLine("  [issues] " + r.Issues);
            }
            else
            {
                Console.WriteLine("  [issues] 无");
            }

            Console.WriteLine("  [plan] 车位 " + r.Stalls + "  车道 " + r.Aisles + " 条 / " + N((float)r.AisleLen) +
                              " m  物件 " + r.Objects + "  曲面 " + r.Surfaces + "  " + r.Ms + " ms");
            if (plan.Stats.Count > 0)
            {
                var sb = new StringBuilder("  [stats] ");
                bool first = true;
                foreach (var st in plan.Stats)
                {
                    if (!first) sb.Append("  ");
                    sb.Append(Short(st.Key)).Append('=').Append(st.Value.ToString("0.###", INV));
                    first = false;
                }
                Console.WriteLine(sb.ToString());
            }
            var hist = new StringBuilder("  [nets] ");
            AppendHist(hist, plan.Nets);
            Console.WriteLine(hist.ToString());
            Console.WriteLine("  [conn] 车辆车道网 分量 " + ci.Components + "  接驳口 " + ci.Connectors +
                              "（显式挂接到既有路的渠 " + ci.ConnAttached +
                              (ci.AttachIds.Length > 0 ? " ids=" + ci.AttachIds : "") + "）" +
                              "  孤立车道 " + ci.OrphanAisles);

            r.Svg = Path.Combine(outDir, "plan-" + sc.Name + ".svg");
            File.WriteAllText(r.Svg, BuildSvg(sc.Ring, sc.Roads, plan, sc.Name, sc.Desc), new UTF8Encoding(false));
            Console.WriteLine("  [svg] " + Path.GetFileName(r.Svg) + "  (" + new FileInfo(r.Svg).Length + " 字节)");
            if (plan.HasErrors) r.ReadyFail = 1;
            return r;
        }

        // ---------------------------------------------------------------- 连通性检查
        // 目的：证明"接驳口 → 内部车道"是真连通，而不是图上看像连上。
        // 两路证据：
        //   a) 几何：把每段中心线采样成折线，端点/端点贴采样点（T 型接入）≥1 m 内视为同一节点，
        //      并查集求连通分量；
        //   b) 显式：PlannedNet.StartAttach/EndAttach 若命中我们喂进去的既有道路 ExternalId，
        //      说明这条渠是 NetCourse 真正挂在既有路上，不是悬空的短头。
        private static ConnInfo Analyze(List<PlannedNet> nets, List<NearbyNet> roads)
        {
            var info = new ConnInfo();
            int n = nets.Count;
            if (n == 0) return info;

            var parent = new int[n];
            for (int i = 0; i < n; i++) parent[i] = i;
            int Find(int x)
            {
                while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; }
                return x;
            }
            void Union(int a, int b)
            {
                a = Find(a); b = Find(b);
                if (a != b) parent[a] = b;
            }

            const int S = 8;                 // 每段 8 等分 → 9 个采样点
            const float TOL = 1.0f, TOL2 = TOL * TOL;
            var pts = new float3[n][];
            for (int i = 0; i < n; i++)
            {
                var g = new float3[S + 1];
                var p = nets[i];
                for (int k = 0; k <= S; k++) g[k] = Bezier(p.A, p.B, p.C, p.D, k / (float)S);
                pts[i] = g;
            }

            for (int i = 0; i < n; i++)
            {
                for (int j = i + 1; j < n; j++)
                {
                    if (Find(i) == Find(j)) continue;
                    if (Touch(pts[i], pts[j], S, TOL2) || Touch(pts[j], pts[i], S, TOL2)) Union(i, j);
                }
            }

            // 接驳口：哪条渠是 DriveConnector，它有没有显式挂到既有道路
            var roadIds = new HashSet<long>();
            foreach (var rd in roads) roadIds.Add(rd.ExternalId);
            int connComp = -1;
            var attach = new HashSet<long>();
            for (int i = 0; i < n; i++)
            {
                // attach 可能落在 Alley / Pathway 上（connect 形态走 LocalConnect 不写 attach），所以按"所有渠"统计
                if (nets[i].StartAttach >= 0 && roadIds.Contains(nets[i].StartAttach)) attach.Add(nets[i].StartAttach);
                if (nets[i].EndAttach >= 0 && roadIds.Contains(nets[i].EndAttach)) attach.Add(nets[i].EndAttach);

                if (nets[i].Role == NetRole.DriveConnector)
                {
                    info.Connectors++;
                    if (connComp < 0) connComp = Find(i);
                }
            }
            info.ConnAttached = attach.Count;
            var sb = new StringBuilder();
            foreach (var id in attach)
            {
                if (sb.Length > 0) sb.Append(',');
                sb.Append(id);
            }
            info.AttachIds = sb.ToString();

            // 车辆车道网的分量数与孤立车道
            var comps = new HashSet<int>();
            for (int i = 0; i < n; i++)
            {
                if (!IsVehicle(nets[i].Role)) continue;
                comps.Add(Find(i));
            }
            info.Components = comps.Count;
            if (connComp >= 0)
                for (int i = 0; i < n; i++)
                    if (nets[i].Role == NetRole.DriveAisle && Find(i) != connComp) info.OrphanAisles++;
            return info;
        }

        /// <summary>a 的两个端点是否落在 b 的采样折线上（tol 内）。</summary>
        private static bool Touch(float3[] a, float3[] b, int s, float tol2)
        {
            for (int e = 0; e <= s; e += s)
            {
                var p = a[e];
                for (int k = 0; k <= s; k++)
                {
                    var q = b[k];
                    float dx = p.x - q.x, dy = p.y - q.y, dz = p.z - q.z;
                    if (dx * dx + dy * dy + dz * dz <= tol2) return true;
                }
            }
            return false;
        }

        private static bool IsVehicle(NetRole r)
        {
            return r != NetRole.Pathway && r != NetRole.PathwayWide && r != NetRole.Fence;
        }

        // ---------------------------------------------------------------- 汇总表
        private static void PrintTable(List<Result> rs)
        {
            string[] head = { "场景", "路网", "放置前", "放置后", "建议", "出入口", "车位", "车道", "接驳", "连通", "孤立", "耗时" };
            var rows = new List<string[]>();
            foreach (var r in rs)
            {
                rows.Add(new[]
                {
                    r.Name,
                    r.Desc,
                    r.ReadyBefore ? "READY" : "NOT",
                    r.ReadyAfter ? "READY" : "NOT",
                    r.Suggested.ToString(INV),
                    r.Markers.ToString(INV),
                    r.Stalls.ToString(INV),
                    r.Aisles + "/" + r.AisleLen.ToString("0", INV) + "m",
                    r.Connectors + "/" + r.ConnAttached,
                    r.ConnComps.ToString(INV),
                    r.OrphanAisles.ToString(INV),
                    r.Ms + "ms",
                });
            }
            var w = new int[head.Length];
            for (int i = 0; i < head.Length; i++)
            {
                w[i] = DispW(head[i]);
                foreach (var row in rows) w[i] = Math.Max(w[i], DispW(row[i]));
            }
            var sb = new StringBuilder();
            sb.Append("=== 场景对比 ===\n");
            AppendRow(sb, head, w);
            sb.Append('|').Append(new string('-', w[0] + 2));
            for (int i = 1; i < head.Length; i++) sb.Append('+').Append(new string('-', w[i] + 2));
            sb.Append("|\n");
            foreach (var row in rows) AppendRow(sb, row, w);
            Console.Write(sb.ToString());
        }

        private static void AppendRow(StringBuilder sb, string[] cells, int[] w)
        {
            sb.Append('|');
            for (int i = 0; i < cells.Length; i++)
            {
                sb.Append(' ').Append(cells[i]);
                sb.Append(new string(' ', Math.Max(0, w[i] - DispW(cells[i])))).Append(" |");
            }
            sb.Append('\n');
        }

        private static int DispW(string s)
        {
            int n = 0;
            foreach (var ch in s) n += ch > 0x2E80 ? 2 : 1;
            return n;
        }

        // ---------------------------------------------------------------- 小工具
        private static double StatN(AreaPlan plan, string suffix)
        {
            foreach (var st in plan.Stats)
                if (st.Key != null && st.Key.EndsWith(suffix, StringComparison.Ordinal)) return st.Value;
            return 0;
        }

        private static int CountRole(List<PlannedNet> items, NetRole role)
        {
            int n = 0;
            foreach (var it in items) if (it.Role == role) n++;
            return n;
        }

        private static void AppendHist(StringBuilder sb, List<PlannedNet> nets)
        {
            var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
            foreach (var n in nets)
            {
                var k = n.Role.ToString();
                counts[k] = counts.TryGetValue(k, out var c) ? c + 1 : 1;
            }
            bool first = true;
            foreach (var kv in counts)
            {
                if (!first) sb.Append("  ");
                sb.Append(kv.Key).Append('×').Append(kv.Value);
                first = false;
            }
        }

        private static string Short(string key)
        {
            if (string.IsNullOrEmpty(key)) return "";
            int i = key.LastIndexOf('.');
            return i >= 0 ? key.Substring(i + 1) : key;
        }

        private static string N(float v) { return v.ToString("0.###", INV); }
        private static string F(float v) { return v.ToString("0.#", INV); }

        // ---------------------------------------------------------------- 几何
        internal static float3[] Rect(float w, float h)
        {
            return new[]
            {
                new float3(0f, 0f, 0f),
                new float3(w, 0f, 0f),
                new float3(w, 0f, h),
                new float3(0f, 0f, h),
            };
        }

        private static double SignedArea(float3[] ring)
        {
            double a = 0;
            for (int i = 0; i < ring.Length; i++)
            {
                var p = ring[i];
                var q = ring[(i + 1) % ring.Length];
                a += (double)p.x * q.z - (double)q.x * p.z;
            }
            return a * 0.5;
        }

        internal static float3 Bezier(float3 a, float3 b, float3 c, float3 d, float t)
        {
            float u = 1f - t;
            return a * (u * u * u) + b * (3f * u * u * t) + c * (3f * u * t * t) + d * (t * t * t);
        }

        // ---------------------------------------------------------------- SVG
        internal static string RoleColor(SurfaceRole r)
        {
            switch (r)
            {
                case SurfaceRole.Asphalt: return "#4a4a4a";
                case SurfaceRole.Paving: return "#cfc2a8";
                case SurfaceRole.Grass: return "#86ab63";
                case SurfaceRole.Plaza: return "#e0d5bd";
                case SurfaceRole.Water: return "#6ba3c9";
                case SurfaceRole.Decorative: return "#b08a63";
                default: return "#b9bdb4";
            }
        }

        internal static double NetWidth(NetRole r)
        {
            switch (r)
            {
                case NetRole.Avenue: return 12.0;
                case NetRole.Street: return 8.0;
                case NetRole.PerimeterRoad: return 7.0;
                case NetRole.Roundabout: return 8.0;
                case NetRole.CulDeSac: return 6.0;
                case NetRole.DriveAisle: return 6.0;
                case NetRole.DriveConnector: return 5.0;
                case NetRole.Alley: return 4.0;
                case NetRole.Pathway: return 2.0;
                case NetRole.PathwayWide: return 3.0;
                case NetRole.Fence: return 0.4;
                default: return 4.0;
            }
        }

        internal static string ObjectColor(ObjectRole r)
        {
            switch (r)
            {
                case ObjectRole.Tree: return "#2f7d32";
                case ObjectRole.Bush: return "#5aa35e";
                case ObjectRole.ParkingBay: return "#d94f3d";
                case ObjectRole.Lamp: return "#f2a900";
                case ObjectRole.Bench: return "#8a5a2b";
                case ObjectRole.Bin: return "#6b6b6b";
                case ObjectRole.Marker: return "#1f6feb";
                case ObjectRole.AccessPoint: return "#0aa3a3";
                default: return "#7a7a7a";
            }
        }

        internal static double ObjectRadius(ObjectRole r)
        {
            switch (r)
            {
                case ObjectRole.Tree: return 1.8;
                case ObjectRole.Bush: return 1.1;
                case ObjectRole.ParkingBay: return 0.55;
                default: return 0.7;
            }
        }

        /// <summary>
        /// 通用渲染：任何 AreaPlan + 地块环 + 既有路网 → SVG。
        /// 阶段 2 用它画 7 个停车场景；扫街模式用它画每个生成器（同一个函数，避免两套画法漂移）。
        /// </summary>
        internal static string BuildSvg(float3[] ring, List<NearbyNet> roads, AreaPlan plan, string title, string subtitle)
            => BuildLayersSvg(ring, roads, new[] { plan }, title, subtitle);

        /// <summary>多图层里每条 AreaPlan 的区域描边色（按执行顺序轮转），用来一眼看出"哪个区域是谁填的"。</summary>
        internal static readonly string[] LayerPalette =
        {
            "#b04fbf", "#2f8f8f", "#c9772f", "#5a6fbf", "#a3543f", "#6f8f3f", "#8f5aa3", "#3f7f8f",
        };

        internal static string LayerColor(int index) => LayerPalette[((index % LayerPalette.Length) + LayerPalette.Length) % LayerPalette.Length];

        /// <summary>地形贴图 1..4 → 颜色（原版地图：1 干草 / 2 茂草 / 3 泥土 / 4 沙）。</summary>
        internal static string PaintColor(int layer)
        {
            switch (layer)
            {
                case 1: return "#d9d2a8";
                case 2: return "#8fb36a";
                case 3: return "#a8845c";
                case 4: return "#e4d7ae";
                default: return "#cfd3c8";
            }
        }

        /// <summary>按估算字宽截断（CJK 记 12px、ASCII 记 6.6px，字号 12），避免长中文标题冲出画布。</summary>
        internal static string FitText(string s, double maxPx)
        {
            if (string.IsNullOrEmpty(s)) return "";
            double w = 0;
            for (int i = 0; i < s.Length; i++)
            {
                double cw = s[i] > 0x2E80 ? 12.0 : 6.6;
                if (w + cw > maxPx) return s.Substring(0, Math.Max(0, i - 1)) + "…";
                w += cw;
            }
            return s;
        }

        /// <summary>
        /// 多图层渲染（阶段 4）：一张 recipe 产出的每条 AreaPlan 按执行顺序叠画。填充仍是语义色
        /// （草地/铺装/水面/沥青），区域描边按图层上色，左下角图例列出每层 id、区域键与产出计数。
        /// </summary>
        internal static string BuildLayersSvg(float3[] ring, List<NearbyNet> roads, IList<AreaPlan> plans,
                                              string title, string subtitle)
        {
            float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;
            void Grow(float x, float z)
            {
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (z < minZ) minZ = z;
                if (z > maxZ) maxZ = z;
            }
            void GrowRing(float3[] r)
            {
                if (r == null) return;
                foreach (var p in r) Grow(p.x, p.z);
            }

            foreach (var p in ring) Grow(p.x, p.z);

            // 路网也纳入包围盒（否则街道会画到画布外）
            const int RoadSamples = 16;
            foreach (var rd in roads)
                for (int i = 0; i <= RoadSamples; i++)
                {
                    var p = Bezier(rd.A, rd.B, rd.C, rd.D, i / (float)RoadSamples);
                    Grow(p.x - rd.Width, p.z - rd.Width);
                    Grow(p.x + rd.Width, p.z + rd.Width);
                }

            var list = plans ?? Array.Empty<AreaPlan>();
            foreach (var pl in list)
            {
                if (pl == null) continue;
                foreach (var sf in pl.Surfaces) GrowRing(sf.Ring);
                foreach (var ar in pl.Areas) GrowRing(ar.Ring);
                foreach (var rg in pl.Regions) GrowRing(rg.Ring);
                foreach (var ob in pl.Objects) Grow(ob.Position.x, ob.Position.z);
                foreach (var nt in pl.Nets)
                {
                    Grow(Math.Min(Math.Min(nt.A.x, nt.B.x), Math.Min(nt.C.x, nt.D.x)), Math.Min(Math.Min(nt.A.z, nt.B.z), Math.Min(nt.C.z, nt.D.z)));
                    Grow(Math.Max(Math.Max(nt.A.x, nt.B.x), Math.Max(nt.C.x, nt.D.x)), Math.Max(Math.Max(nt.A.z, nt.B.z), Math.Max(nt.C.z, nt.D.z)));
                }
                foreach (var z in pl.Zoning) { Grow(z.A.x, z.A.z); Grow(z.B.x, z.B.z); Grow(z.C.x, z.C.z); Grow(z.D.x, z.D.z); }
            }

            float w = maxX - minX, h = maxZ - minZ;
            if (w <= 0) w = 1;
            if (h <= 0) h = 1;

            double pad = 14;
            double topPad = 40;                 // 给标题/副标题留出的上边距（否则文字压在路网上）
            double target = 1100;
            double scale = (target - 2 * pad) / Math.Max(w, h);
            double pxW = w * scale + 2 * pad;
            int legendRows = Math.Min(list.Count, 12);
            double legendH = legendRows > 0 ? 16.0 * legendRows + 14 : 0;
            if (list.Count > 12) legendH += 16;
            double pxH = h * scale + 2 * pad + legendH + topPad;

            Func<float, float, string> P = (x, z) =>
                (pad + (x - minX) * scale).ToString("0.##", INV) + "," +
                (topPad + pad + (maxZ - z) * scale).ToString("0.##", INV);

            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
            sb.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"").Append(pxW.ToString("0", INV))
              .Append("\" height=\"").Append(pxH.ToString("0", INV))
              .Append("\" viewBox=\"0 0 ").Append(pxW.ToString("0", INV)).Append(' ').Append(pxH.ToString("0", INV)).Append("\">\n");
            sb.Append("<rect width=\"100%\" height=\"100%\" fill=\"#f7f5f0\"/>\n");

            // 0) 地块外的既有路网（画在地块下面，模拟"街"）
            if (roads.Count > 0)
            {
                sb.Append("<g id=\"site-roads\" fill=\"none\" stroke=\"#2b2b2b\" stroke-opacity=\"0.9\" stroke-linecap=\"round\">\n");
                foreach (var rd in roads)
                {
                    sb.Append("<path d=\"");
                    for (int i = 0; i <= RoadSamples; i++)
                    {
                        var p = Bezier(rd.A, rd.B, rd.C, rd.D, i / (float)RoadSamples);
                        sb.Append(i == 0 ? "M " : " L ").Append(P(p.x, p.z));
                    }
                    sb.Append("\" stroke-width=\"").Append((rd.Width * scale).ToString("0.##", INV)).Append("\"/>\n");
                }
                sb.Append("</g>\n");
            }

            for (int li = 0; li < list.Count; li++)
            {
                var plan = list[li];
                if (plan == null) continue;
                string tag = "L" + li;
                sb.Append("<g id=\"").Append(tag).Append("\" data-layer=\"").Append(Escape(plan.GeneratorId ?? "")).Append("\">\n");

                // 0b) 地形贴图（park 的草地就是这一条通道画出来的：plan.Paint，不是 Surface）
                if (plan.Paint.Count > 0)
                {
                    sb.Append("<g class=\"paint\">\n");
                    foreach (var pt in plan.Paint)
                    {
                        if (pt.Ring == null || pt.Ring.Length < 3 || pt.Layer <= 0) continue;
                        sb.Append("<path d=\"");
                        for (int i = 0; i < pt.Ring.Length; i++)
                            sb.Append(i == 0 ? "M " : " L ").Append(P(pt.Ring[i].x, pt.Ring[i].z));
                        sb.Append(" Z\" fill=\"").Append(PaintColor(pt.Layer))
                          .Append("\" fill-opacity=\"").Append(Math.Max(0.15, Math.Min(1.0, pt.Strength * 0.92)).ToString("0.##", INV))
                          .Append("\" stroke=\"none\"/>\n");
                    }
                    sb.Append("</g>\n");
                }

                // 1) 曲面
                sb.Append("<g class=\"surfaces\">\n");
                foreach (var s in plan.Surfaces)
                {
                    if (s.Ring == null || s.Ring.Length < 3) continue;
                    sb.Append("<path d=\"");
                    for (int i = 0; i < s.Ring.Length; i++)
                        sb.Append(i == 0 ? "M " : " L ").Append(P(s.Ring[i].x, s.Ring[i].z));
                    sb.Append(" Z\" fill=\"").Append(RoleColor(s.Role)).Append("\" stroke=\"none\"/>\n");
                }
                sb.Append("</g>\n");

                // 1b) 分区格（zoning / polish 图层的真正产物：一格一个四边形，不是 surface）
                if (plan.Zoning.Count > 0)
                {
                    sb.Append("<g class=\"zoning\">\n");
                    foreach (var z in plan.Zoning)
                    {
                        sb.Append("<path d=\"M ").Append(P(z.A.x, z.A.z))
                          .Append(" L ").Append(P(z.B.x, z.B.z))
                          .Append(" L ").Append(P(z.C.x, z.C.z))
                          .Append(" L ").Append(P(z.D.x, z.D.z))
                          .Append(" Z\" fill=\"").Append(ZoneColor(z.ZoneId)).Append("\" fill-opacity=\"0.85\"/>\n");
                    }
                    sb.Append("</g>\n");
                }

                // 2) 通行区域
                if (plan.Areas.Count > 0)
                {
                    sb.Append("<g class=\"areas\" fill=\"none\" stroke=\"#2f6fb5\" stroke-width=\"0.8\" stroke-dasharray=\"6 4\">\n");
                    foreach (var a in plan.Areas)
                    {
                        if (a.Ring == null || a.Ring.Length < 3) continue;
                        sb.Append("<path d=\"");
                        for (int i = 0; i < a.Ring.Length; i++)
                            sb.Append(i == 0 ? "M " : " L ").Append(P(a.Ring[i].x, a.Ring[i].z));
                        sb.Append(" Z\"/>\n");
                    }
                    sb.Append("</g>\n");
                }

                // 3) 子区域：描边按图层上色 —— 这张图要回答的就是"父亲的哪个区域被谁填了"
                if (plan.Regions.Count > 0)
                {
                    sb.Append("<g class=\"regions\" fill=\"none\" stroke=\"").Append(LayerColor(li))
                      .Append("\" stroke-width=\"1.1\" stroke-dasharray=\"4 3\" opacity=\"0.9\">\n");
                    foreach (var rr in plan.Regions)
                    {
                        if (rr.Ring == null || rr.Ring.Length < 3) continue;
                        sb.Append("<path d=\"");
                        for (int i = 0; i < rr.Ring.Length; i++)
                            sb.Append(i == 0 ? "M " : " L ").Append(P(rr.Ring[i].x, rr.Ring[i].z));
                        sb.Append(" Z\"/>\n");
                    }
                    sb.Append("</g>\n");
                }

                // 4) 路网（内部车道/接驳，三次贝塞尔）
                if (plan.Nets.Count > 0)
                {
                    sb.Append("<g class=\"nets\" fill=\"none\" stroke=\"#1c1c1c\" stroke-opacity=\"0.85\" stroke-linecap=\"round\">\n");
                    foreach (var n in plan.Nets)
                    {
                        sb.Append("<path d=\"M ").Append(P(n.A.x, n.A.z))
                          .Append(" C ").Append(P(n.B.x, n.B.z))
                          .Append(' ').Append(P(n.C.x, n.C.z))
                          .Append(' ').Append(P(n.D.x, n.D.z))
                          .Append("\" stroke-width=\"").Append((NetWidth(n.Role) * scale).ToString("0.##", INV)).Append("\"/>\n");
                    }
                    sb.Append("</g>\n");
                }

                // 5) 物体
                if (plan.Objects.Count > 0)
                {
                    sb.Append("<g class=\"objects\">\n");
                    foreach (var o in plan.Objects)
                    {
                        sb.Append("<circle cx=\"").Append((pad + (o.Position.x - minX) * scale).ToString("0.##", INV))
                          .Append("\" cy=\"").Append((pad + (maxZ - o.Position.z) * scale).ToString("0.##", INV))
                          .Append("\" r=\"").Append((ObjectRadius(o.Role) * scale).ToString("0.##", INV))
                          .Append("\" fill=\"").Append(ObjectColor(o.Role)).Append("\" fill-opacity=\"0.95\"/>\n");
                    }
                    sb.Append("</g>\n");
                }

                sb.Append("</g>\n");
            }

            // 6) 地块边界
            sb.Append("<path d=\"");
            for (int i = 0; i < ring.Length; i++)
                sb.Append(i == 0 ? "M " : " L ").Append(P(ring[i].x, ring[i].z));
            sb.Append(" Z\" fill=\"none\" stroke=\"#c0392b\" stroke-width=\"2\" stroke-dasharray=\"10 6\"/>\n");

            int totSurf = 0, totNet = 0, totObj = 0, totZone = 0, totReg = 0, totPaint = 0;
            foreach (var pl in list)
            {
                if (pl == null) continue;
                totSurf += pl.Surfaces.Count; totNet += pl.Nets.Count; totObj += pl.Objects.Count;
                totZone += pl.Zoning.Count; totReg += pl.Regions.Count; totPaint += pl.Paint.Count;
            }

            double textW = pxW - 2 * pad;
            sb.Append("<text x=\"").Append(pad.ToString("0", INV)).Append("\" y=\"18")
              .Append("\" font-family=\"monospace\" font-size=\"15\" fill=\"#333\">")
              .Append(Escape(FitText(title, textW * 15.0 / 12.0)))
              .Append("</text>\n");
            sb.Append("<text x=\"").Append(pad.ToString("0", INV)).Append("\" y=\"34")
              .Append("\" font-family=\"monospace\" font-size=\"12\" fill=\"#666\">")
              .Append(Escape(FitText((subtitle.Length > 0 ? subtitle + "  " : "") +
                                     "surfaces=" + totSurf + " nets=" + totNet + " objects=" + totObj +
                                     " paint=" + totPaint + " zoning=" + totZone + " regions=" + totReg, textW)))
              .Append("</text>\n");

            // 7) 图例（左下角，画布已为它留了高度）
            if (legendRows > 0)
            {
                double ly = topPad + h * scale + 2 * pad + 4;
                sb.Append("<g id=\"legend\" font-family=\"monospace\" font-size=\"12\" fill=\"#333\">\n");
                for (int li = 0; li < legendRows; li++)
                {
                    var plan = list[li];
                    double ry = ly + 16 * li;
                    sb.Append("<rect x=\"").Append(pad.ToString("0", INV)).Append("\" y=\"").Append((ry - 9).ToString("0.##", INV))
                      .Append("\" width=\"10\" height=\"10\" fill=\"none\" stroke=\"").Append(LayerColor(li)).Append("\" stroke-width=\"2\"/>\n");
                    string text = "#" + li + " " + (plan?.GeneratorId ?? "?") + " @" + (plan?.StepPath ?? "") +
                                  "  surf=" + (plan?.Surfaces.Count ?? 0) + " net=" + (plan?.Nets.Count ?? 0) +
                                  " obj=" + (plan?.Objects.Count ?? 0) + " reg=" + (plan?.Regions.Count ?? 0);
                    sb.Append("<text x=\"").Append((pad + 16).ToString("0", INV)).Append("\" y=\"").Append(ry.ToString("0.##", INV))
                      .Append("\">").Append(Escape(text)).Append("</text>\n");
                }
                if (list.Count > 12)
                    sb.Append("<text x=\"").Append((pad + 16).ToString("0", INV)).Append("\" y=\"").Append((ly + 16 * 12).ToString("0.##", INV))
                      .Append("\">+").Append(list.Count - 12).Append(" more layers</text>\n");
                sb.Append("</g>\n");
            }

            sb.Append("</svg>\n");
            return sb.ToString();
        }

        internal static string Escape(string s)
        {
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
        }

        // 分区种类 → 颜色：同一个 ZoneId 永远同色，便于对比不同图层的分区构成。
        private static readonly string[] ZonePalette =
        {
            "#c96a6a", "#6a9bc9", "#8fc96a", "#c9b96a", "#a06ac9", "#6ac9b5", "#c98f6a", "#9aa65f",
        };

        internal static string ZoneColor(string id)
        {
            if (string.IsNullOrEmpty(id)) return "#9a9a9a";
            int h = 0;
            foreach (char c in id) h = (h * 31 + c) & 0x7fffffff;
            return ZonePalette[h % ZonePalette.Length];
        }
    }
}
