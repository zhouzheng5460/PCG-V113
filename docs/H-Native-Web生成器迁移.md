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

### 测试集必须分两类（**这是一个需要你确认的决定**）

| 类 | 用例 | 面积 | golden 车位密度 | 用途 |
| --- | --- | --- | --- | --- |
| **primary** | `p0 / p1 / p42 / p90`（4 例） | 12~17k m² | **41~50 m²/位**（非常一致） | 密度/车道/覆盖保真 |
| **stress** | `p90.partN`（15 例） | 160~1677 m² | **112~453 m²/位**（稀疏 2~9 倍，彼此无一致性；1219 m² 上给 0 位） | 只验稳健性 |

判据：碎块是把 p90 的地块切成 15 份的产物，golden 在那里**不是一套设计，是退化回退**。
拿它当密度保真目标，会惩罚一个实际能用的生成器（我 47 个位 vs 它 10 个位，而 1604 m² 的 L 形地
排 47 个位在几何上完全成立）。

- 真地块 4 例：p0=86 / p1=76 / p42=96 / p90=84 → **均 86**
- 碎块 15 例：均 64（个别 100）

**不同意就把 `verify.mjs` 的 `PRIMARY_MIN_AREA` 设为 0**，退回"全部 19 例都判密度"，
那样均分 68、6 例密度不达标 —— 数字都留着，改一行即可切换。

### 剩下唯一不达标的：`p1` 车道长 +48%

`p1` 是 **532×82 m** 的狭长地块（紧致度 0.38）。golden 916 m 车道 / 403 车位 = **0.44 位/m**；
我 1358 m / 335 位 = **0.25 位/m**。也就是说我的行带**更长但更空**（同样长方，我切得更碎）。
最可能的差异点是 `crossEvery` 在狭长地块上的分段策略 —— 需要下一轮单独查，不靠调参解决。

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
## 7. 明确不做

1. **不再逐文件翻译 C#**。要什么行为，先把 golden 的输入输出量出来，再写原语。
2. **不新增中间协议**。Plan JSON 就是唯一契约；`SiteInput` 只是它的输入侧对偶。
3. **不把"差不多"当验收**。有分数的就用分数，没分数的先别迁移。
4. **不在没有 golden 的生成器上先写代码**。这是 19 份 parking 语料换来的第一条经验。
