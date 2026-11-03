# Native Web Site Generator —— 停止搬运 C#，改为「原语 + golden 对照」

> 一句话：**Plan JSON 是契约，C# 降级为 oracle，生成链改由 TS 原语承担。**
>
> 这条把「465 个 .cs 逐文件翻成 TS」换成「提炼原语 + 每步用 golden 量差距」。
> 前者是几周的无底洞（而且最后会得到一个"仍然不会绕建筑的 Placemaker"），
> 后者每一步都有分数，随时可以停。

---

## 0. 为什么现在换路

到 2026-11 的第二周，迁移的实际情况是：

| 事实 | 数字 | 含义 |
| --- | --- | --- |
| C# 生成器 | **130 个** `[AreaGenerator]`，128 个能出结果 | 逐个翻译 = 不可行 |
| 已接入生成器 | **31 个**，其中很多是同一套算法的不同参数 | 迁移粒度应该是**原语**不是类 |
| golden 语料 | 54 份真实烘焙输入，**parking 占 19 份** | 最厚的语料也是用户建议的起点 |
| 浏览器可运行 | ❌ 需要 `TS → dotnet → JSON → TS` | 这就是要消灭的东西 |

关键判断（用户提出、我认同）：**Placemaker 不是要被"翻译"，而是要被当成 oracle。**

于是"等价"从一句空话变成一个纯函数判断：

```
同一份输入  ──┬── C#（golden，离线快照）──→ Plan A
              └── TS（原生，正式链）  ──→ Plan B
                                          ↓
                                    自动比对 15 个量
```

---

## 1. 先纠三处我自己搞错的事实

动手之前核对仓库，发现三处与既有认知不符，全部以**实际产物**为准：

| 我先前的说法 | 实际 | 处理 |
| --- | --- | --- |
| Plan 只有 6 个通道 | 实际 `--emit` 产 **9 条通道**：`surfaces / nets / objects / areas / regions / zoning / paint / terraform`，另有 `stats`、`facts`、`unknownRoles` | 补齐 `PlanArea / PlanRegion / PlanStat` 类型（**D-058**） |
| 已有 golden 语料 | 只有 4 份完整 Plan；`ready-baseline.json` 只有 `ready/given/auto` 三个标量 | 新建 `tools/golden/` 烘 54 份（**D-057**） |
| 输入可以直接复用 `toCaseInput` | C# 自己解析输入：`settings` 要与 `Defaults()` 合并（parking **33 项**）、`b/c` 控制点三等分算、`SuggestMarkers` 补出入口 | oracle 必须导出解析后的输入（**D-059**） |

---

## 2. 结构

```
packages/site-gen/
├── src/
│   ├── types.ts          SiteInput / SiteGenerator / GeneratorBackend / CHANNELS（冻结契约）
│   ├── golden.ts         读 tools/golden（只做 IO）
│   ├── backends.ts       两个后端：golden（读快照）/ native（查注册表）
│   ├── metrics.ts        15 个纯函数度量（只吃 Plan，与后端无关）
│   ├── compare.ts        分级判定 exact/close/off/bad/missing + 分数
│   ├── primitives/
│   │   ├── polygon.ts    offsetRing · clipHalfPlane · splitRing · longestEdge
│   │   ├── scatter.ts    scatterAlongEdge · scatterInside · rngOf（确定性）
│   │   └── parking.ts    parkingRows · connectAisles · pathAround
│   ├── generators/
│   │   ├── parking.ts    第一个原生生成器（v1）
│   │   └── index.ts      注册表 —— **列出来才算已迁移**
│   └── cli/compare.ts    比对 CLI（含 --selfcheck 仪器自检）
└── verify.mjs            门禁：协议完整性 + 仪器自检 + 不回退基线

tools/golden/
├── emit.mjs              dotnet --emit × 两次（逐字节稳定性校验）→ plans/
├── plans/<case>.plan.json + <case>.input.json     （54+54，3.0 MB）
└── index.json            契约层事实（通道条数 / ready / 输入规模）
```

**没有第三个后端。** 任何"把 C# 输出缓存一下混进 native"的做法都会让比对失真。

---

## 3. 冻结的契约（`placemaker-plan/1`）

| 通道 | 内容 | parking 实测 |
| --- | --- | --- |
| `surfaces` | 地皮（role: asphalt / grass / paving…） | 44 条 |
| `nets` | 中心线（driveAisle / driveConnector / pathway…） | 62 条 |
| `objects` | 点位（parkingBay / tree / bush / lamp / charger…） | 649 个 |
| `areas` | 生成器的工作分区 | 1 条 `lot` |
| `regions` | 具名区域 + tags（`lot:0` / `green:3`） | 44 条 |
| `zoning` / `paint` / `terraform` | 分区 / 地面刷 / 地形 | 0 / 0 / 0 |
| `stats` | **生成器自报的数**（`Stat.Stalls=288`、`Stat.AisleLength=1102`） | 6 条 |
| `issues` | 诊断 | 0 |

两条硬约定（沿用 `Emit.cs` 的注释，不动）：

1. **单位一律米**，换算在调用方，不在 oracle 里；
2. **平面坐标一律 `[x, z]`**，高度单列 `y`。

> `stats` 是最可信的一档对照：它是 Placemaker **自己数出来的**车位数/车道长/树数，
> 比我从几何反算更接近"原本的意思"。所以它进契约、进比对。

---

## 4. 度量与判定

15 个量，五组：

| 组 | 量 |
| --- | --- |
| 覆盖 | 铺装总面积 / 铺装÷地块 / **铺装互相压住** / 分区÷地块 / 油漆 |
| 路网 | 总长 / **连通块数** / **悬空端点数** / 最长连通网 |
| 物件 | 车位数 / 出入口点 / 物件总数 / 物件密度 /千m² |
| 其它 | 地形改动条数 / 问题条数 |

判定分五档而不是通过/失败：`exact(≤2%) · close(≤20%) · off(≤50%) · bad(>50%) · missing(golden 有、原生完全没有)`。

理由：原生实现第一版不可能对齐，只给"失败"就不知道离成功多远，会退化成"改到不红为止"
——这一路已经这样翻过车（D-029 的 `undefined > 0` 假失败、D-047 树密度方向反了却报 PASS）。

`missing` 单独一档：**沉默的通道缺失最危险**。少写 `paint` 汇总数只差一点，下游整层消失。

### 仪器自检（`--selfcheck`）

拿 golden 冒充 native，必须 **19/19 全 100 分**。不过就说明度量/比对本身有 bug，
后面所有"差距 40%"都不能信。这一条已经写成门禁。

---

## 5. 第一个生成器：`parking`

选它的唯一理由是**语料**：19/54 份 golden。没有 golden 的生成器写完了也无法验证"等价"。

由四个原语拼成，顺序照 Placemaker 自己的 `ParkingLayer`（Zoning → Aisle → Stalls → Access）：

```
offsetRing(地块, 退线 1 m)            → 能用的地
parkingRows(最长边定向, 双面载荷)      → 车道中心线 + 车位
connectAisles()                       → 把碎片接成一张网
pathAround(车道端 → 出入口, 避障)     → 车开得进来
scatterAlongEdge(沿边, 12 m 间距)      → 行道树
```

### 实测演进（19 份用例均分）

| 轮 | 均分 | 关键动作 |
| --- | --- | --- |
| v1 | **29** | 整块草地打底（覆盖 197%、重叠 14027 m²）；`crossEvery` 误解成"每条车道最多 N 个车位" |
| 修铺装 | **55** | 草地改前后边角两片（互补切片）→ 覆盖 99%、重叠 286 |
| 加连网 | **61** | `connectAisles` 接**端点**而非中段：连通块 28→2，最长连通网 33→1346 |
| 修小地块 | **67** | 一条行带都排不下时 `clipVSlab(null,null)` 把整环返回两次 → 草地铺两遍。显式兜底 |
| 布尔 + 绿带 | **68** | 引 `polygon-clipping`（封在 `boolean.ts` 后）；绿带用 `intersectRegion`→`differenceRegion` 切开铺装 |

从 golden 量出来的硬事实（v1 的假设被它们逐条推翻）：

- **车位中心到车道中心线恒为 6.45 m**（p90 6.46、max 6.72）= `aisleWidth/2 + stallLength/2` = 3.5 + 2.95
  → 90° 双面载荷、严格对齐。
- **车道短且多**：60 条、中位 15.3 m、最长 146.6 m、端点度数 `1:2 / 2:20 / 3:18 / 4:7`
  → `crossEvery` 是"每 N 个车位插横向通道"，不是"每条车道最多服务 N 个"。
- **铺装不叠**：草地 3174 + 沥青 11354 = 14527 = 地块面积，重叠 ≈ 0
  → 说明 golden 的沥青是**无洞的多块**，绿地在块与块之间，不是"挖洞"。
- **每米车道挂几个车位**：golden 288/1103 = **0.26**，我原来 438/1108 = **0.40**
  → 差的 50% 不是"排得太密"，而是 golden 的车道网里有一段**不挂车位的环路**（`perimeterRoad: true`）。

### 生成资格（Eligibility）：第一道门，不是放宽误差

用户 2026-11-03 的判断：`part3/part7/part2` 那批碎地的差异**不是停车几何差，是"该不该生成"的决策差**。
原话：**"Golden 是行为参考，不应该成为几何错误的免责条款。"** 我上一轮用"或与 golden 一致"去放宽，正是拿它当免责条款。

`src/eligibility.ts` 按几何判，不用纯面积阈值：

```
单排模组 = stallLength + aisleWidth          = 5.9 + 7.0 = 12.9 m
双排模组 = stallLength + aisleWidth + stallLength = 18.8 m
行数 = 尽量排双排，尾部余深 ≥ 单排模组再补一排
```

**两个概念必须拆开**（这是本轮最重要的设计决定）：

| 字段 | 语义 | 依据 |
| --- | --- | --- |
| `ready` | 图层**能不能跑**（镜像 `Plan.ready`） | Placemaker `CheckReady` |
| `feasible` | 这块地**有没有地方排模组**（决定落不落车位） | 我们自己的几何判据 |

为什么拆：实测 19 例里 **`oracle.ready` 只有 1 例是 false**（part6，160 m² 三角形），
其余 18 例全 true —— **包括它一个车位都不排的那些**。所以拿 `ready` 当"该不该排车位"的判据无效；
反过来若因它恒真就一律排满，就会在 6~14 m 有效进深的碎地上硬塞 10~18 个位。

**阈值是量出来的**（`cli/feasibility.ts` 把 19 例打表）："oracle 完全不排车位"的 7 例有效进深
落在 **6~14 m**，"排了车位"的 12 例落在 **15~127 m**。分界 15 m，物理上正好是单排模组 12.9 + 余量。

### 门禁（`npm run sgen:check`，15 项）

| 层 | 判据 | 实测 | |
| --- | --- | --- | --- |
| 0 | golden 语料 54 份 / 10 通道 / 有输入 dump / 无 elapsedMs | 全部 | ✅ |
| 0 | 仪器自检 golden vs golden 全 100 分 | 19/19 | ✅ |
| 0 | 布尔原语 | 26 项 | ✅ |
| **1** | 不崩溃 | 19/19 | ✅ |
| **1** | deterministic（两次生成逐字节相同） | 19/19 | ✅ |
| **1** | **ready 与 oracle 一致** | **19/19** | ✅ |
| **1** | **可行性判定与 oracle 产出一致** | **19/19** | ✅ |
| **1** | 不可行用例不硬干 | 0 例 | ✅ |
| 2 | 铺装重叠 < 0.5% | 最差 **0.02%** | ✅ |
| 2 | eligible 覆盖 98~102% | **100.0%** | ✅ |
| 2 | 连通块 ≤ 3 | 最差 **2** | ✅ |
| 2 | 车位误差 ≤ max(25%, 4) | 最差 **23%** | ✅ |
| 2 | 车道误差 ≤ max(25%, 20m) | 最差 **48%** | ❌ 1 例（p1） |
| 2 | 真地块均分 ≥ 75 | **86**（4 例） | ✅ |
| 2 | 全部用例均分 ≥ 60 | 68 | ✅ |

### 测试集按 **显式 cohort** 分，不按面积

用户 2026-11-03 拍板："不要让 `PRIMARY_MIN_AREA` 成为长期语义…… 面积阈值可以暂时用来复现实验，
但最终最好把 19 个 case 显式标注 cohort。因为以后很可能出现
`1200 m² 规则矩形 → 完全值得做停车`、`3000 m² 极窄碎片 → 仍然只是 stress`。"

所以 `PRIMARY_MIN_AREA` 已删除，改为 `tools/golden/cohorts.json` 显式标注（54 份全标）：

| cohort | 用例 | golden 车位密度 | 判什么 |
| --- | --- | --- | --- |
| **primary** | p0 / p1 / p42 / p90（+ 其余 35 份非 parking） | 41~50 m²/位（自洽） | 密度 / 车道 / 覆盖 / 分数 |
| **stress** | `p90.partN` 15 份（160~1677 m²） | 112~453 m²/位（退化、彼此不一致） | 不崩 / 确定性 / 不越界 / 重叠≈0 / 拓扑 / 不硬干 |

依据：碎块是把 p90 切成 15 份的产物，1219 m² 上 golden 给 0 位。拿它当密度保真目标会判"能用"的实现失败。
**改判某个用例改 JSON，不改代码。** all-19 均分保留为**观测**，不进 release gate。

### 门禁（15 项，`npm run sgen:check`）

| 层 | 判据 | 实测 |
| --- | --- | --- |
| 0 | 语料 54 份 / 10 通道 / 输入 dump / 无 elapsedMs | ✅ |
| 0 | 仪器自检 golden vs golden | 19/19 全 100 分 ✅ |
| 0 | 布尔原语 | 26 项 ✅ |
| 1 | 不崩溃 / deterministic | 19/19 · 19/19 ✅ |
| 1 | **ready 与 oracle 一致** | **19/19** ✅ |
| 1 | **可行性判定与 oracle 产出一致** | **19/19** ✅ |
| 1 | 不可行用例不硬干 | 0 例 ✅ |
| 2 | 铺装重叠 < 0.5% | **0.02%** ✅ |
| 2 | eligible 覆盖 98~102% | **100.0%** ✅ |
| 2 | 连通块 ≤ 3 | 最差 **2** ✅ |
| 2 | 车位误差 ≤ max(25%, 4) | 最差 **23%** ✅ |
| 2 | 车道误差 ≤ max(25%, 20m) | 最差 **14%** ✅ |
| 2 | primary 均分 ≥ 75 | **86**（4 例）✅ |
| 2 | primary 逐例 ≥ 80 | 79 / 86 / 96 / 84 → **p0 差 1 分** ❌ |

---

## 5.1 **Parking Native V1 — 冻结（PASS）**

```
Parking Native V1 — FROZEN (2026-11-03)
```

| 项 | 值 | 层 |
| --- | --- | --- |
| primary 均分 | **91**（p0 87 / p1 90 / p42 98 / p90 90） | Fidelity |
| 覆盖 | 100.0%（98~102%） | Fidelity |
| 重叠 | **0.02%**（< 0.5%） | Fidelity |
| 车位误差 | 23%（≤ 25%） | Fidelity |
| **有效车道误差** | **18%**（≤ 25%） | Fidelity |
| 连通块 | ≤ 2（≤ 3） | Fidelity |
| 主连通网占比 / 车位可达性 | 全过 | Fidelity |
| feasibility 一致 | **19/19** | Eligibility |
| ready 一致 | **19/19** | Eligibility |
| deterministic | **19/19** | Eligibility |
| 总车道 Δ / 空转 / 悬空端 | 观测与诊断，**不判** | Observation |

### 明确不追求的东西（写在这里，挡住以后"顺手修一下"）

1. **不追求 golden 的 dead aisle 比例**。golden p0 总车道 1124 m 里 **18% 是空转**，
   native 968 m 里只有 **10%**；位/m 我 0.37 vs 它 0.26。为了把 968 补到 1124 而造 150 m 空转是负优化。
2. **不追求 golden 的 perimeter loop**。p0 的 6 个悬空端 = 2 个出入口/服务端 + 4 个外圈行端，
   这是**合法的梯形停车网络**。复制闭环要 +35% 车道 + 占地 + 降低效率。
3. **不追求碎块的逐项一致**。`p90.partN` 那 15 份是退化样本（1219 m² 上 golden 给 0 位），
   只验稳健性（cohort = stress）。
4. **不追求多方向布局**。那是 P2/P3 的事，靠 `splitRegion` 解决，不在 parking 里写特例。

### 指标三层（`npm run sgen:check` 用的口径）

| 层 | 指标 | 作用 |
| --- | --- | --- |
| **Fidelity Gate** | productiveAisle · stalls · coverage · overlap · connectedComponents · 主连通网占比 · 车位可达性 | PASS/FAIL |
| **Quality Diagnostic** | deadAisleRatio · stallsPerAisleMeter · danglingEnds · fragmentation | 只打印 |
| **Oracle Observation** | netTotal | 只显示 Δ |

分数只对 Fidelity 层取均值。**降级不等于藏起来**：Δ 照常打印，只是明确声明"这不是我们希望消除的差异"。

---

### P1.7：先量 dead aisle，再改策略（不写特例）

用户要求："不要直接开始调 `crossEvery=8 → 12`…… 先增加一个诊断量。"

`metrics.ts` 新增（两后端同口径，用 `plan.settings` 推 `serviceDist = stallLength + aisleWidth/2`，
正好是 golden 实测的 6.45 m）：

```
productiveAisle  服务到至少一个车位的车道长度
deadAisle        不服务任何车位的车道长度
deadAisleRatio   空转占比
stallsPerAisleMeter  位/m 车道
```

**诊断量不计分**——golden 自己也造 18% 空转，拿它当分数等于"复制它的浪费"。它们只打印。

量出来的病灶：`p1`（532×82）的 `crossAisle` 连线 **631 m，占总车道 46%**，都是斜穿空地的跨行连接。

| | 车道 | 空转 | 位/m |
| --- | --- | --- | --- |
| **p1 改前** | 1358 (+48%) | 21% | 0.25 |
| **p1 改后** | **910 (−0.7%)** | 11% | 0.37 |
| golden p1 | 916 | 6% | 0.44 |

三处改动（都不是特例）：

1. **只为"真有车位的连续段"铺车道**：原来的顺序是先铺满一段车道、再逐个丢弃放不下的车位，
   于是"车道还在、车位没了"。改为先判定每格能不能放，再按连续段生成车道。
2. **跨行连接只连 u 区间有重叠的两行**：没有重叠就是两块独立停车区，不该硬连（这是 631 m 的来源）。
3. **`maxLink` 用相邻行距的**中位数**×1.5**，不能用 `rowKeys[1]-rowKeys[0]`——
   行距不规则时前两行可能挨得很近，`maxLink` 被压成几米，会误拒绝大多数连接（实测 p0 车道掉 14%）。

### p0 = 79 的根因（**这一条需要你判断**）

| | 车道 | 空转 | 位/m |
| --- | --- | --- | --- |
| golden p0 | 1124 | **18%** | 0.26 |
| native p0 | 968 (−14%) | **10%** | **0.37** |

我少的 14% 车道**正好就是死车道**：我的网络更省、每米服务的车位更多。
分数扣分是因为 `netTotal` 拿"总长对齐 golden"当判据，而 golden 的总长里有 18% 是空转。

两个选项，等你定：

- **A（现状，不改）**：`netTotal` 判总长 → p0 = 79，永远差 1 分。诚实但会一直红。
- **B（改判据）**：把 `netTotal` 换成 `productiveAisle`（两边都只算服务到车位的部分）→
  golden 922 / native 871（−5.5%）→ close。更符合"车道是手段不是目的"，但**这是改判据去适应实现**，
  我不想单方面做。

另外 p0 的悬空端 2→6：我的网络是**梯子**（4 个行端 + 2 个出入口连线），golden 是**闭环**（只有 2 个出入口连线）。
要闭环就得加一条环路，而 p0 的环路占地 24% > 我的 12% 门槛，加了会把总长推到 +35%。

## 6. 下一步（按用户定的 P0–P6 阶梯）

| 阶段 | 内容 | 状态 |
| --- | --- | --- |
| **P0** | `differenceRegion` / `unionRegions` / `intersectRegion`（封在 `primitives/boolean.ts`，唯一 import 处） | ✅ 26 项断言（六个边角用例 + 确定性 + 绕序 + 起点不变） |
| **P1** | 绿岛 / 禁建区：`intersectRegion` 裁走廊 → `differenceRegion` 切开铺装 | ✅ 已接进 parking |
| **P1.5** | **最小规模规则**：碎地不排车位（对齐 Placemaker 的"不干"） | ⬜ 下一个做，解锁车位/车道两条门禁 |
| **P2** | `splitRegion` 按朝向切子区 | ⬜ golden 的多朝向布局就来自它 |
| **P3** | 多方向停车（每个子区各自定向） | ⬜ |
| **P4** | 连通车道网（环路 + 多出口；现在 1~2 块，达标） | ✅ 已达标 |
| **P5** | `bush` / `charger` / 无障碍位 | ✅ bush/charger 已补；无障碍未做 |
| **P6** | 19 例 golden 比对 + 门禁 | ✅ 已接入，68 分 |

---
## 5.2 **Plaza Native V1 — 通过（P3.1）**

```
Plaza Native V1 = Usable Region + Surface Allocation + Semantic Regions + Decoration
```

**没有 PathNetwork** —— 这是 P3.0 解剖出来的事实（9/9 例 `nets = 0`），不是省事。

### 管线

```
site ─ union(obstacles) ─→ usable            （障碍先 union，再一次性 difference）
usable ─ 内缩窄带 ─→ green（贴边）
usable ─ 减 green ─→ paved（主面，可能带洞）
paved 的内缩 ─→ core（focus 落点）
edge = 比 green 宽一圈的带（仍属铺装，放沿边设施）
accessPoint → 只要求落在/贴近铺装连通体（不产中心线）
scatter → 树/灌木/灯/椅/focus，逐个过滤 "在铺装里 且 不在障碍物里"
```

### 契约变更：`PlanSurface.holes`（D-078）

布尔差集减掉**内部**障碍物，数学结果就是**一个带洞的多边形**。序列化层如果只支持单环，
就只能把洞切开重排（tessellation）—— 而那是**渲染的需要**，不是设计结构。
所以 `PlanSurface` 增加可选 `holes`，metrics 的面积与覆盖都扣洞。
这正好落在 docs/I §2.1 说的三层拓扑上：

```
Design topology      带洞的区域（1 个）
Serialization        同一个区域 + holes（不切碎）
Render               triangulation（由下游负责）
```

### 门禁（`npm run sgen:check`，plaza 段 10 项全绿）

| 判据 | 阈值 | 实测 |
| --- | --- | --- |
| 不崩溃 / deterministic | 10/10 | ✅ |
| usable 覆盖 | 98~102% | **最大偏差 0.00%** |
| 铺装重叠 | < 0.5% | **0.00%** |
| 障碍重叠 | = 0 | **0.000%** |
| 铺装连通块 | ≤ 2 | **1** |
| 最大铺装块 | ≥ 90% | **100%** |
| 出入口可达 | 100% | **100%** |
| 物件全在障碍物外 | 100% | **100%** |
| primary 均分 | ≥ 75 | **100**（10 例） |

### 原语复用（硬门禁）

```
── plaza ──
  生成器 223 行 · 顶层定义 5 个
  复用 polygon.ts 230 · boolean.ts 204 · scatter.ts 237（合计 671 行）
  本地函数：num, asRegion, toSurfaceRegion, inObstacles, plaza
PASS — 2 个生成器，0 处违规
```

**0 个新增几何原语**。`plaza.ts` 里全是策略组合（内缩多少、绿带多宽、谁放哪），
没有一行 `clip / offset / difference / intersect` 的自研实现 —— 这是这一轮真正要验的东西。

---

## 6.5 架构原则：原语扩展需要证据

> **Primitive expansion requires evidence from ≥2 generators,
> unless the missing operation is a fundamental geometry operation.**
>
> 原语扩展需要**至少两个生成器**的证据；唯一例外是"缺失的是一个基础几何运算"
> （比如布尔差集、内缩）—— 那种东西本来就不该由某个生成器私藏。

这条是硬约束，理由是它挡住的是**我自己的行为模式**：这一路最容易走偏的就是
"某个生成器不通 → 给它加一个特例 → 下一个生成器再加重写一遍"，最后得到一个 TS 版 Placemaker。

三个执行细则：

1. **先让第二个生成器用现有原语硬做一遍**。做不动的地方记成具体缺口（不是"加个参数"），
   等**两个**生成器都卡在同一件事上，再动原语层。
2. **`splitRegion` 已冻结**。18 项测试过了就不要再加 `"plaza"` / `"radial"` / `"garden"` 策略 ——
   让 Plaza 通过 `evaluator` 去用它。
3. **生成器不许绕开 `primitives/boolean.ts` 直接 import 布尔内核**，
   也不许自己写 `clip/offset/difference/inset` 这类函数。
   这条由 `node packages/site-gen/src/cli/primitives.ts <id>` 检查，已接进门禁（见 §6.6）。

### 6.6 原语复用门禁（`primitives.ts`）

报告每个原生生成器的"复用 vs 自建"：

```
── parking ──
  生成器 337 行 · 顶层定义 3 个
  复用 polygon.ts    230 行 · offsetRing, longestEdge, clipVSlab, rectInFrame, clipHalfPlane
  复用 boolean.ts    199 行 · differenceRegion, intersectRegion, unionRegions, PolygonRegion
  复用 parking.ts    483 行 · parkingRows, pathAround, connectAisles
  复用 scatter.ts    237 行 · rngOf, scatterAlongEdge
  复用合计 1149 行
  本地函数：num, bool, parking
PASS — 1 个生成器，0 处违规
```

判据不是"行数比"，是**有没有第二套几何**：
本地出现几何味函数名（`clip|offset|inset|difference|union|intersect|area|split|...`）
或直接 import `polygon-clipping` → 违规。

---

## 7. 明确不做

1. **不再逐文件翻译 C#**。要什么行为，先把 golden 的输入输出量出来，再写原语。
2. **不新增中间协议**。Plan JSON 就是唯一契约；`SiteInput` 只是它的输入侧对偶。
3. **不把"差不多"当验收**。有分数的就用分数，没分数的先别迁移。
4. **不在没有 golden 的生成器上先写代码**。这是 19 份 parking 语料换来的第一条经验。
