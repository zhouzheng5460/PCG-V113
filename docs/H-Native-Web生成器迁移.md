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

### 三轮实测（19 份用例均分）

| 轮 | 均分 | 关键动作 |
| --- | --- | --- |
| v1 首版 | **29** | 整块草地打底 + 沥青覆盖；`crossEvery` 误解成"每条车道最多 N 个车位" |
| 修铺装 | **55** | 草地改成**前后边角两片**（不再整块叠铺）：覆盖 197%→99%，重叠 14027→286 m² |
| 加连网 | **56~58** | `connectAisles`：连通块 28→9，最长连通网 33→243 m |

`crossEvery` 的语义是**"每 N 个车位插一条横向通道"**，不是"每条车道最多服务 N 个车位"。
证据：golden 的 60 条车道中位只有 **15.3 m**（≈5 个车位）、最长 146.6 m、
端点度数分布 `1:2 / 2:20 / 3:18 / 4:7` —— 是"长行被横向通道切段"，不是"天生短"。

另外两条从 golden 量出来的硬事实（v1 已对齐）：

- **车位中心到车道中心线的距离恒为 6.45 m**（golden p90 = 6.46，max 6.72）
  = `aisleWidth/2 + stallLength/2` = 3.5 + 2.95。说明 90° 双面载荷、严格对齐。
- **铺装不叠**：golden 草地 3174 + 沥青 11354 = 14527 = 地块面积，重叠 ~0。

### 剩下的差距（如实记账，不粉饰）

| 项 | golden | native | 原因 |
| --- | --- | --- | --- |
| 车位数 | 288 | 438 | 我铺满整行；golden 用子区拆分 + 环路占了地（`perimeterRoad: true`） |
| 草地 | 3174 m² | 337 m² | 缺"车位排之间的草岛"，需要布尔差集（**D-060**） |
| 连通块 | 1 | 9 | 不规则地块产生多段 u-runs，脊只连了相邻行 |
| `bush` / `charger` | 271 / 6 | 0 / 0 | 没写这两种 role |
| 车道朝向 | 多个方向（30°/120°/170°…） | 单一最长边方向 | golden 会把地块**按子区分别定向**（`splitRegion` 还没做） |

---

## 6. 下一步（按投入产出排）

1. **`differenceRing` 原语**（布尔差集）：解锁草岛、无障碍区、禁停区。这是唯一挡住 3 个量的技术缺口。
   实现路线：射线法 + 交点排序（不经手直骨架），或引入 `polygon-clipping` 并接受"本包有依赖"。
2. **`splitRegion` 按朝向切子区**（用户点名的第 2 个原语）：golden 的多朝向布局就是它的产物。
3. 补 `bush` / `charger` 两种 role，并把 `Stat.EvStalls / AccessibleStalls` 补进 stats。
4. 换下一个生成器：**`plaza` → `formalGarden` → `industrial yard`**（语料 1/1/4 份）。
   语料薄，所以要先用 `oracle --emit` 造更多用例（改 `tools/batch/cases` 加地块形状/种子变体），
   **没有足够样本的生成器不进入迁移队列**。

---

## 7. 明确不做

1. **不再逐文件翻译 C#**。要什么行为，先把 golden 的输入输出量出来，再写原语。
2. **不新增中间协议**。Plan JSON 就是唯一契约；`SiteInput` 只是它的输入侧对偶。
3. **不把"差不多"当验收**。有分数的就用分数，没分数的先别迁移。
4. **不在没有 golden 的生成器上先写代码**。这是 19 份 parking 语料换来的第一条经验。
