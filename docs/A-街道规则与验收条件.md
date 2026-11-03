# 街道规则与验收条件

> Placemaker 不生成街道，它**消费**街道。所以「街道」不是我们的实现细节，而是交给 Placemaker 的**输入契约**——契约错了，130 个生成器全部退化（`ready=no` 或输出一堆莫名空地），而且不会报错。
>
> 本文把这条契约整理成可执行规则，作为 town 算法（Voronoi 地块 + 拓扑寻路 + 内陆缩）的**验收条件**。每条规则都标注了它的 C# 来源，便于回溯。

---

## 0. 四个消费点：为什么街道必须"正好"这样

| 消费点 | 代码位置 | 依赖什么 | 违反后果 |
| --- | --- | --- | --- |
| 沿边可达 | `Core/Tool/AttachmentFinder.cs:99` | `RoadAlongEdge` 且 `RoadWidth > 0` | 该边上的入口/连接不可达 |
| 出入口建议 | `Layers/Parking/EntranceSuggest.cs:36-70` | 40 m 外射线打到**车行道**（`Category == Road`），`gap ∈ [-w/2, 15]`；只有连一条车行道都没有时才退回 `RoadAlongEdge < 30 m` | 停车族「必须有一个车行口」永远不满足 → `ready=no` |
| 边分区铺装 | `Layers/Roads/RoadBlocks.cs:185` | `RoadAlongEdge && RingSegment == seg && RoadWidth > 0` | 该 segment 不铺路网/不分区 |
| 入口路径延伸 | `Layers/Common/Entrances.cs:490` | `RoadAlongEdge ? 20 m : 4 m` | 入口路只走 4 m，接不到街 |

> ⚠️ **本节曾被写错，已就地更正（D-022）。** 原文把「出入口建议」写成「**只**遍历 `RoadAlongEdge`（`reach = 20 m`）」——
> 那是 `EntranceSuggest.cs:126` 的 `NearAttachment`（`d < 30`），它只是 `roads.Count == 0` 时的兜底分支。
> 真实主路径是 `Roads.From` 取车行道 + `Roads.Cast` 打 40 m 外射线，判据 `gap ∈ [-w/2, 15]`，
> 容差比 `AttachmentFinder`（`w/2 + 3`）大一个数量级。实测：街整体外移 11 m，parking 仍 `ready`，产出 593 → 590 个物体。

**但「沿边贴合」并不是 `ready` 的判据。** `ready` 只数出入口 marker（见下方 §0.1），街道只通过
`SuggestMarkers` 间接影响它——而这条路只有 **parking 一族**走得通。

### 0.1 `ready` 由什么决定（反向验证的结论，D-022）

```
given  = input.Markers.Count                  // 烘焙工具塞的 / 玩家点的
sug    = gen.SuggestMarkers(input)            // ① 建议 —— 只有这一步碰街道
if (sug非空 && given==0) input.Markers += sug // ② 建议被写回成真 marker
ready  = gen.CheckReady(input)                // ③ 只数 marker，从不看街道
```

来源：`tools/oracle/driver/Emit.cs:95-111`。`SuggestMarkers` 与 `CheckReady` 是**两套平行分家**，互不通用。
下表 130 个生成器的归属**不是手抄的**：`driver --generators` 反射 `GeneratorRegistry.All`，取"虚方法最近声明点 + 两个 `protected virtual` 开关的实例值"，再由 `packages/street-rules/tools/gen-entrance-classes.mjs` 归类。

| 家族 | 数量 | 建议器 | 碰街道？ | `CheckReady` 判据 | 源码 |
| --- | --- | --- | --- | --- | --- |
| `shop`（stripMall/mallPark/mallDistrict） | 3 | 恒空 | **否** | 车行口：`two>0` 或 `in>0 ∧ out>0` | `CommercialLayer.cs:41` / `MallEntrances.cs:54` |
| `market`（marketSquare） | 1 | 恒空 | **否** | 任意一个口即可 | `MarketSquareLayer.cs:49` / `MarketEntrances.cs:55` |
| `parking` | 1 | `EntranceSuggest` | **是** | 车行口 | `ParkingLayer.cs:235/258` |
| `typed`（park 族） | 27 | `Entrances.Suggest` | **是**（另一套判据） | 任一 typed kind | `Entrances.cs:340` |
| `none` | 97 | 空，或非空但不影响 | 不适用 | 恒 `true` | `LayerBase.cs:428` |
| `roads` | 1 | 恒空 | **否** | 图案 × 地形 | `RoadsLayer.cs:115` |

**所以：`ready` 会被街道改变的生成器，只有 parking 一族。** 商场/市集/路网的 `ready` 与街道零因果；
`typed` 虽然也从街道取建议，但有最长边兜底，街道改变不了结果（推导见 `ready.ts` 顶部，
实测见 §4 的扰动表）。

> `none` 那 97 个里有 13 个（`LeisureLayer` 且 `TypedEntrances=false`，如 `carYard`/`abandonedLot`）
> **会**从街道建议 marker：街上有路时它们的 `auto` 计数会涨，但 `ready` 恒 `true`。
> 这就是批量回归里"auto>0 却恒 ready"的来源，别把它当成反例。


而街道真正的落点在 `LayerBase.cs:521-531`：

```csharp
if (!CheckReady(input).Ready) TrimOutlineEnds(plan, region, unplacedInset);
else if (!CheckReady(带空markers).Ready) { /* 只保留 marker 附近 5 m 内的接轮廓 */ }
```

**`ready=no` 不是失败，是「路径不许接到街上」的降级开关**——布局照样生成，只是路网封在轮廓内。
这才是"静默降级"的精确机制，也是街道契约真正要保证的东西。

> 附带一条：建议出来的口 `AttachId` 恒为 `-1`（`Entrances.cs:371` 的 `Suggested = true, AttachId = -1`），
> 所以即使 `ready=yes`，只有**玩家或烘焙工具给的口**才真的接到街上。实测 53 份 ready=yes 里只有 16 条路径接街，
> 且全部来自 `given>0` 或 pathway 端点当候选的情形。

---

## 1. 几何契约（`AreaShape`）

| 字段 | 要求 | 来源 |
| --- | --- | --- |
| `Ring` | XZ 平面，**逆时针（CCW）**，≥3 点，**隐式闭合**（末点 ≠ 首点） | `Contracts.cs:24` |
| 简单性 | 无自交 | 同上 |
| 高度 | `Ring[i].y` = 该点地形高度 | 同上 |
| 面积 | `SignedArea > 1 m²`，否则整块丢弃 | `RoadNetwork.cs:414` |
| **ring 是路缘线，不是路中线** | 朝内的一侧路缘（curb），保证公园不压路；缺路缘几何才退回中线 | `RoadNetwork.cs:396` |

> **这一点对我们是好消息**：TownForge 的 `getCityBlock()` 已经是「按每条相邻路宽内缩」后的可建区，天然就是路缘线。PCG 侧只要保证地块多边形与道路内缩同源，不要各算一次。

---

## 2. 硬规则（11 条，全部可用脚本断言）

### R1 · 沿边贴合

> 每条临街的 ring 边 `i`，必须在 `sMid` 处产出一个 `RoadAlongEdge`。

| 判据 | 阈值 | 来源 |
| --- | --- | --- |
| 街采样点到该边的法向距离 | ≤ `width/2 + 3 m`（`AlongTolerance`） | `AttachmentFinder.cs:17` |
| 街道切向与该边方向的平行度 | `|dot| ≥ 0.95`（≈ ±18.2°） | `AttachmentFinder.cs:91` |
| 平行重叠长度 | ≥ `min(12 m, 0.4 × 边长)`（`MinAlongLength`） | `AttachmentFinder.cs:19` |
| 街道采样步长 | 弧长约 4 m 一点，夹到 1..64 段 | `AttachmentFinder.cs:130` |

**违反后果**：该边不产生 attachment → 停车族不可达、边分区不铺。

**"重叠长度"是采样算出来的，天生偏小**：它是"最边缘两个采样点"之间的边长投影，
所以真实重叠最多会被少算**一个采样步长**。推论（实测验证过，见 §4）：

- 边长 ≤ 1 个步长（≈4 m）：最多落 1 个采样点 → 重叠恒为 0 → **永不临街**
- 判定"不临街"时，若 `要求 - 实测重叠 < 步长`，只能说"采样分辨不出来"，不能说"不临街"

所以 `probeAlong()` 会返回 `resolvable`，凡是 `overlap + step >= required` 的都标为
"分辨率不足"而不是"不临街"。这一条同时是 R11 的由来。

### R2 · 拐角节点

> ring 的每个「两条街相交」的拐角，产出 `RoadNode`（`RoadWidth = 0`、`RingT` 取投影点）。

来源 `RoadNetwork.cs:439`。角点是「街网连续」的证据，缺了会让 `RoadBlocks` 认为街区不闭合。

### R3 · 精确构造优先，计算结果做交叉校验

block 模式（我们的地块就是 block）应该用**精确构造**：每条街在自己与 ring 最近点的中点出 `RoadAlongEdge`（`ExternalT = 0.5`），每个角点出 `RoadNode`。

再用 `AttachmentFinder.Find()` 从几何重算一遍，**两者逐地块比对**：

- 同一条街落在**同一条边**上（`RingSegment` 一致）—— **error**
- `Position` 距离 ≤ 0.5 m —— **warn**
- 一边有一边没有：先看是不是短边的采样分辨率问题（R1 的 `resolvable`）
  - 分辨率不足 → 记数，不算错
  - 否则 → **error**

> 为什么不能要求"数量严格相等"：实测 54 份真实城市地块，1146 条边里有 195 条临街边短于
> 12 m（其中 58 条 ≤4 m）。这些边在 block 模式下**必然**产出 attachment（环是沿路描的），
> 在画法模式下**必然**产不出（采样点不够）。要求相等就等于让每个真实街区都红。
> 这不是妥协：它把"算法分辨率上限"变成了一条显式的、可数的指标。

> 这是本项目最划算的一个自检：两套独立实现互证，任何一边的几何 bug 都会立刻暴露。
> 实际抓到过的 bug：`pointAt` 吸附到采样点导致贴合点偏 2 m；
> `estOverlap` 与 `surveyEdges` 两套容差口径不一致导致短边上 R1/R3 自相矛盾。

### R4 · 交叉（仅画法地块）

真正**穿过** ring 的街 → `RoadCrossing`，交点去重阈值 1 m（`AttachmentFinder.cs:57`）。block 模式不产，我们的地块默认不需要；但 PCG 允许手绘地块，手绘时必须有。

### R5 · 三次贝塞尔

`NearbyNet` 是三次贝塞尔 `A/B/C/D`。直线时 `B = A + (D−A)/3`、`C = A + 2(D−A)/3`。

- **非有限值会被静默丢弃**（`Sample()` 里 `math.isfinite` 检查）→ NaN 一个点，整条街消失，且不报错
- 曲线街道必须给真实控制点，不能只给两端

### R6 · 宽度大于 0

`RoadWidth > 0` 是 `RoadBlocks` 的硬前提。宽度 0 = 该边永不分区（不是"用默认值"）。

### R7 · 至少一条可达街

否则 `CheckReady` 返回 `NeedsEntrance`。对停车族/商场族/市场族，等价于**整块地不可用**。

### R8 · ZoneGrid 跟随

宗地格网：**8 m 一格**、深 1..6 格（8~48 m）、`Direction` 由 block 指向 road（`Contracts.cs:99`、`ZoneGuard.cs:56`、`ParkingSettings.cs:51`）。

> 只要街的位置或宽度变了，ZoneGrid 必须重算——它是建筑的模数基准，见《建筑预设规范》。

### R9 · 单位与坐标

- 一律**米**
- 平面 `[x, z]`，`+z` = 北，`+x` = 东，**两个轴都不翻转**（翻了是镜像，车位朝向全反，肉眼很难发现）
- `yaw` 绕 +Y，0 = 朝 +Z

### R10 · 兜底语义

- 绕过 `Composer` 直接调 `gen.Plan()` 时，`SiteSafety.Apply` **不会执行**，障碍物收了没人用
- 有楼地块请用「不传 obstacles + 事后按楼裁剪（clip）」而不是「传 obstacles」，否则停车族 53 条车道会被切成 1 条

### R11 · 临街边长度（提醒项，不参与总判定）

> 每条临街边（有点贴着的边）应当 ≥ 12 m；尤其不能 ≤ 4 m。

| 判据 | 阈值 | 理由 |
| --- | --- | --- |
| 临街边长度 | ≥ `MinAlongLength` = 12 m | 重叠要求是 `0.4 × 边长`，短边上很容易被一个采样步长吃掉 |
| 临街边长度 | 不 ≤ 1 个采样步长（≈4 m） | 最多 1 个采样点 → 重叠恒为 0 → 画法模式下永不临街 |

**为什么是提醒而不是错误**：block 模式下这条边确实能拿到 attachment（环沿路描的），
只是换到"画法/涂绘"路径就丢了。但**我们要自己生成路网**，两种模式都得成立，
所以这就是生成器的一条硬约束——只是不该把真实城市判成错。

**实测**：54 份真实地块、1146 条边 → 临街边 <12 m 的 195 条（17.0%），其中 ≤4 m 的 58 条（5.1%）。
全部是两条路之间的斜接短边。这条数据直接支撑了 §5 第 2 条。

---

## 3. 从我们的路网到契约的映射

### 3.1 道路等级 → Placemaker 字段

| 我们的等级 | 宽度 (m) | `NetCategory` | `HasSidewalk` | `IsOneWay` | `SpeedLimit` | 备注 |
| --- | --- | --- | --- | --- | --- | --- |
| primary 主干道 | 12 | `Road` | true | false | 50 | PCG `ROAD_WIDTH.primary` |
| secondary 次干道 | 8 | `Road` | true | false | 40 | 桥接默认值 8 |
| local 支路 | 6 | `Road` | true | false | 30 | |
| service 服务道 | 5 | `Road` | **false** | 可 true | 20 | 停车场环道 |
| pedestrian 步行 | 3 | `Pathway` | false | — | 0 | 不进 `RoadAlongEdge` 的车行判定 |

> 停车族的出入口需要**车行道**；只临步行道的边不算可达。这条要在验收脚本里单独断言。

### 3.2 折线 → 贝塞尔

PCG 的 `RoadObj.centerline` 是折线。转 `NearbyNet` 时：

1. 单段直线：控制点按 1/3、2/3 放（与桥接现状一致）
2. 多段折线（有转折）：**每段单独成一条 net**，不要用一条贝塞尔去拟合折角
3. 圆弧（环岛/弯道）：用四分之一圆的标准 0.5523 手柄系数

**不要**把折角拟合成一条平滑贝塞尔——平行度判据 `|dot| ≥ 0.95` 是按采样切向算的，拟合出来的中段切向会偏离原边方向，反而丢掉 attachment。

### 3.3 当前差距（谁缺什么）

| 需要 | PCG | TownForge | 差距 |
| --- | --- | --- | --- |
| CCW 简单环、米制 | ✅ `polygon.ts` 有绕序与面积 | ✅ `getCityBlock()` | 需统一到「路缘线」口径 |
| 街道显式对象 | ⚠️ 有 `RoadObj`（含 width/sidewalk），但**没有"地块每条邻边 → 一条街"的显式归属** | ✅ `Artery` 带 width + 逐边内缩 | **P1 要补：`parcelStreets()`** |
| 街道分类/单行/限速/自行车道 | ❌ 只有 `RoadClass` | ⚠️ 有横断面推导，无限速语义 | 补字段，允许默认值 |
| 贝塞尔控制点 | ❌ 只有折线 | ⚠️ 同样折线 | 新增 `polylineToBezier()` |
| 拐角节点 | ❌ 无节点概念 | ✅ 拓扑图有顶点 | TownForge 侧直接取度数 ≥2 顶点 |
| ZoneGrid（8 m 模数） | ❌ 无 | ⚠️ 有地块内缩，无宗地格网 | **P1 要补：`zoneGridOf(edge)`** |
| 出入口 → marker | ⚠️ `AccessObj` 有 pos/dir/type | ❌ 无 | 映射 + 语义对齐（车行 vs 步行） |

---

## 4. 验收脚本设计

`tools/validate-case.ts`（P1 交付），对每个地块输出一张违规清单：

```
地块 #12  面积 3120 m²  边数 5
  ✅ R1 沿边贴合      4/5 边通过（边 3 重叠 8.2 m < min(12, 9.6) → 只差 1.4 m）
  ❌ R2 拐角节点      缺 1 个（顶点 2 处两街夹角 21°，平行度不足，被判定为同一条街的折点）
  ✅ R3 精确/计算互验  5 个 attachment 全部一致
  ✅ R6 宽度          最小 5.0 m
  ❌ R7 可达街        边 3 退化后可通车边只剩 1 条，停车族会 NeedsEntrance
  ⚠️ R8 ZoneGrid      3 条边深度 7 格 > 上限 6
  → 结论：blocked（停车族不可用）
```

**验收门（P1 结束必须达到）**：

| 指标 | 目标 | 实测（54 份真实地块） |
| --- | --- | --- |
| R1 每条临街边都有 `RoadAlongEdge` | 100% | 53/54（唯一失败的是切碎后的内部碎片，无临街，见下） |
| R2 每个 ≥2 度顶点都有 `RoadNode` | 100% | 54/54（1059 个街角） |
| R3 精确构造 vs `AttachmentFinder` 计算 | 100% 一致（RingSegment 全等，Position ≤0.5 m） | 54/54（短边按 `resolvable` 分流计） |
| R6 宽度 | 100% > 0 | 54/54 |
| R7 车行可达 | 100% ≥1 条 | 53/54（同上，无临街碎片） |
| R8 宗地深度 | ≤6 格 | 54/54 |
| R11 临街边长度 | 全 ≥12 m | 提醒：195 条 <12 m，58 条 ≤4 m |
| **逐点对照真 C#** | 全字段一致 | **3365/3365 项 = 100%**（673 个贴合点 × 5 字段，含 3 处精确平局） |
| **反向验证** | 我们的判定能提前说出 C# 会不会 `ready` | **54/54 = 100%**；扰动 48/48 = 100%（详见 §0.1 与 D-022） |

唯一那份"无临街"的地块（`p90-parking.part6`）是 **真阳性**：它是"减去建筑后切碎"出来的
内部碎片，Placemaker 对它确实会 `ready=no`。真实流程靠"逐块生成再合并"绕过了它。
这一条正好说明验收器的作用——它不是把正常情况判红，而是**把"这一块没有出路"提前说出来**。

最后一行才是关键：**验收不是"我们的数据格式对不对"，而是"Placemaker 认不认"**。

### 反向验证是怎么做的（`packages/demo/src/reverse.ts`）

对 54 份真实烘焙输入各自真跑 `dotnet --emit`，把引擎自报的 `ready / readyKey / markers.given /
markers.auto` 与我们的预测逐份对照。预测模型在 `packages/street-rules/src/ready.ts`，判据来自
上面 §0.1 的那张家族表。

**为什么不拿 R7 当判据**：R7 说的是"街道够不够用"，`ready` 说的是"引擎收不收这块地"，是两件事。
54 份里 R1/R7 也恰好 54/54 一致，但那是因为样本只有 1 份 `ready=no`——**区分力没被检验**。
真正把它区分开的是扰动实验：8 个真实地块各造 6 种变体（去给定口 / 外移街 / 全变园路 / 删最长街 / **删光所有街**），
看 `ready` 跟不跟着动。每一族都有一条**可被否证**的断言：

| 家族 | 断言 | 变体里 ready 次数 | 成立？ |
| --- | --- | --- | --- |
| `shop`（stripMall/mallPark） | 应等于「有给定口」的次数（2） | 12 次里 2 次 | ✓ |
| `market`（marketSquare） | 同上（1） | 6 次里 1 次 | ✓ |
| `parking` | 应介于 0 与总数之间 —— 街道既抬得起也打得到 | 6 次里 4 次 | ✓ |
| `typed`（park/campground/dogPark） | 应等于总数 —— 恒 ready | 18 次里 18 次 | ✓ |
| `none`（shopsPark） | 应等于总数 —— 恒 ready | 6 次里 6 次 | ✓ |

`parking` 那 2 次 `no` 是"全变园路"和"删光所有街"——没有车行道可打射线，`EntranceSuggest` 给不出口。

`typed` 那 18/18 是本节最有力的一条：**连着把街道全删光，公园族照样 `ready`**。
因为 `Entrances.Suggest` 在候选为空时会兜底"最长边的中点"（`Entrances.cs:239-259`），
只要 ring 合法就永远给得出建议。这证实了 `ready.ts` 里那条判断是**结构性的，不是经验**。

**怎么跑**：`node packages/demo/src/reverse.ts`（加 `--no-emit` 读上次结果不重跑 dotnet）。
报告 → `packages/demo/out/reverse.html`。

**怎么跑**：`node packages/demo/src/batch.ts`（加 `--no-csharp` 跳过 dotnet，只跑静态段）。
夹具在 `tools/batch/`，来源见那里的 README。

---

## 5. 城市级路网生成要满足的方向

由此倒推 TownForge 生成器需要改进的地方（P3 之后的重构输入）：

1. **地块边必须与街道严格平行**——Voronoi 边本身就是两城区的等距线，天然满足；但**内陆缩后要保持平行**（按该边路宽内缩，不能用统一 offset）
2. **避免极短边**：边长 < 12 m 的边无法满足 `MinAlongLength`（且 40% 兜底会把它变成 4.8 m 的极低要求，实际会退化成不可用）。生成时对短边做合并
   - 实测依据：真实 CS2 城市 1146 条边里 195 条（17.0%）临街边 <12 m，其中 58 条 ≤4 m 在画法模式下**永不临街**。这是"真实城市本来就不完美"的直接证据，也是我们要做得更好的地方
3. **避免小夹角**：两街夹角 < 36°（`|dot| < 0.95` 的邻域）会导致 attachment 归属混乱。Voronoi 里长条三角地块要合并
4. **块大小下限**：单块可建面积 < 300 m² 基本只能跑铺装类生成器；< 150 m² 直接不出内容
5. **拐角必须有节点**：拖点后重建（`buildAll`）不能让两个街段在几何上相接但拓扑上断开

这五条会成为 `Model` 的参数校验（生成时就拒绝，而不是等 Placemaker 报 `ready=no`）。
