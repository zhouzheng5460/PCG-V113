# PCG × PCG-CITYSKYLINE 合并计划

> 目标：沿用 PCG 的交互与界面定义，把核心「参数化生成 + 算法」换成 CITYSKYLINE 那条路线（Placemaker 内核），并逐步把 C# 替换为 TypeScript，最终得到一条纯前端、可交互编辑的场地程序化生成流水线。

配套文档（四份都是"可执行规则"，不是说明文）：

| 文档 | 作用 | 服务对象 |
| --- | --- | --- |
| [A · 街道规则与验收条件](docs/A-街道规则与验收条件.md) | 街道作为 Placemaker 输入契约的 10 条硬规则 + 验收脚本 | town 算法（Voronoi / 寻路 / 内陆缩） |
| [B · 建筑预设规范](docs/B-建筑预设规范.md) | 11 类分区、8 m 模数、朝向与退线、L 形拆 quad | 建筑生成与地面一致性 |
| [E · 建筑资产库与匹配机制](docs/E-建筑资产库与匹配机制.md) | Slot / Skin 分层、三层匹配回退、盒子先铺满 | 建筑外观与渐进替换 |
| [C · 界面中文规范](docs/C-界面中文规范.md) | 零英文、术语四表、缺词报错 | 全部 UI |
| [D · 功能舍弃与问题台账](docs/D-功能舍弃与问题台账.md) | 17 条舍弃/降级 + 9 条问题 + 4 条风险 | 全项目，冲突裁决依据 |

---

## 0. 现状盘点（实测，非估计）

| 项 | PCG（`-PCG-` / context-city-pcg） | PCG-CITYSKYLINE |
| --- | --- | --- |
| 语言/栈 | TypeScript 10,300 行；React 18 + Zustand + three 0.186 + clipper-lib | ① TS ~21,000 行（TownForge）② C# 259,266 行（Placemaker Core） |
| 完成度 | Phase 0/1/2/4/5 ✅，Phase 3/6~9 ⬜ | 内核库编译 0 错 0 警；130 生成器反射跑通 128 有输出；82 配方全渲染 |
| 测试 | 85 单测 + 81 项 e2e（puppeteer） | 38 条自检 + 生成器全量扫描 + 桥接/绿篱/绿地/堆场/避让/GLB/score 七套脚本 |
| 核心资产 | 2D 编辑器（工具状态机 1117 行、渲染 932 行）、`plan.ts`(764) 入口+BFS 主路+支路网+地面分区、`pcg.ts`(668) 退线/避让/分割/落位/层数、SceneIR→three | Placemaker 466 个 Core 文件分 7 通道；TownForge 的 Voronoi 地块 + 拓扑寻路 + 地块内缩 + 现代内容层(4140) + 道路横断面 + GLB 导出 + 桥接层(~2,000) |
| 缺口 | 无「街道」显式模型、无出入口 marker、zoning、terraform、贴花、资产分类 | 无编辑器（只能 CLI 烘焙 → 读 JSON 静态预览） |

C# 内部规模分布（决定移植顺序的依据）：

| 目录 | 行数 | 说明 |
| --- | --- | --- |
| Layers（334 文件 / 143 生成器） | 115,590 | 生成器主体，按家族可拆：Roads 25k、Commercial 9.6k、Parking 7.1k、Leisure 6.5k、Plaza 6.1k、Park 5.7k、Beach 4k、Terraces 3.8k、Industrial 3.6k、Scrapyard 2k、Fields 2.5k |
| Build | 19,854 | Plan 模型 / Composer / 通道装配 —— **所有生成器共用骨架，移植优先级最高** |
| Neighbours | 15,389 | 邻接/临街判定 |
| Tool | 13,062 | CS2 工具层，大部分可不移 |
| Suite | 12,488 | 配方/组合，部分要被 TS 的编排层替代 |
| Geometry | 7,459 | float3/环/多边形/Clipper —— 地基 |
| Decals | 4,102 | 标线/贴花 |
| Ai | 3,902 | 游戏侧 AI，**不移植** |
| Types | ~36,000（在 Layers 内） | CS2 资产 / 升级 / 枚举定义，**必须剥离** |

结论：259k 行不是「一个移植任务」，真正要搬的大约 **60k~90k 行**，其余是 CS2 游戏耦合（资产定义、升级、AI、工具 UI），目标是「有损降维」而不是「逐行翻译」——这一点 `Emit.cs` 已经做对了：只保留几何 + 角色 + 分类。

---

## 1. 目标架构：一条契约，三个可替换后端

```
┌─────────────── PCG 编辑器（保留） ───────────────┐
│ 2D 描图 / 工具状态机 / 吸附 / 撤销 / Inspector / 3D │
└──────────────────────┬───────────────────────────┘
                       │  CaseInput ⟶  Plan IR
              ┌────────▼─────────┐
              │   Plan IR 契约    │  placemaker-plan/1
              │ surfaces nets     │  + zod schema + normalize
              │ objects zoning    │  + golden diff
              │ paint terraform   │
              └────────▲─────────┘
        ┌──────────────┼──────────────┐
   legacy TS       C# 基准(临时)     planner-ts（终点）
   PCG plan/pcg    dotnet --emit     纯浏览器
```

**唯一铁律**：编辑器和 3D 舞台只认 `Plan IR`，不认识任何后端类型。这条不立，后面全是胶水。

---

## 2. 五个必须先拍板的决策

### D1 · 契约先立，代码后动

`bridge/placemaker.ts` 里的 `PlacemakerPlan` 已经是事实标准，把它提成独立包（`packages/plan-ir`）：

- 类型 + zod 校验 + `normalize()`（环绕序统一 CCW、坐标 round 到 1e-4、元素按 role/category/质心排序）
- 差分工具 `diffPlan(a, b)`：分 L1/L2/L3 三级（见 §5）
- 之后**任何**生成结果都必须先过 schema 才能进场景

### D2 · C# 做「对照基准」而不是「一次性翻译源」

- C# 侧冻结为只读子目录（`placemaker-master/`），加版本戳；Core 一改，TS 的基准就漂移
- 每次 TS 移植一小块，就跑一次 `oracle:sweep`（130 生成器 × 3 形状 × 2 种子）
- 这是整个项目**唯一**能防止「移植到底像不像」靠感觉判断的手段

### D3 · 单位与坐标：以米为准，只换算一次

- PCG 已经是米制（`x=东, y=北`），Placemaker 也是米制、平面写 `[x,z]`
- TownForge 是 `UNIT_M = 7` 世界单位。合并后**统一米**，换算只在 GLB 导出与 3D realizer 的 `toV3()` 发生一次
- 明令禁止：`rot = yaw − π/2` 这类换轴散落在业务代码里（cityskyline 里它是桥接层集中处理的，必须保持集中）

### D4 · 职责重划：谁拥有路网

这是两个项目**语义上最硬的冲突**，必须在 P1 解决，否则后面全是补丁：

| 概念 | PCG 现状 | Placemaker 模型 | 合并后归属 |
| --- | --- | --- | --- |
| 城市级路网 | `planAccessRoad` + 支路网（自己生成） | 不生成，只**吃**街道作为输入约束 | TownForge 的 Voronoi 路网（Phase 3 多地块时启用） |
| 地块内车道/环路/停车 aisle | `planAccessRoad` | `nets` 通道的 `driveAisle / perimeterRoad / driveConnector` | **Placemaker** |
| 入口 | `pickEntryPoint`（几何规则） | `markers`（`SuggestMarkers` + 用户点选） | **Placemaker 建议 + 用户确认** |
| 建筑 | PCG 自己长（体块 + 层数反解） | **不产建筑**，只以障碍物四边形避让 | 留在 TS 内容层（PCG 层数反解 + TownForge modern.ts 的建筑族） |
| 地面/铺装/绿地/车位 | `planSurfaces` 三档 | `surfaces` + `zoning` + `paint` | **Placemaker** |

一句话：**Placemaker 接管「这块地上室外那一半」，建筑与城市骨架留在 TS。**

### D5 · Inspector 从「硬编码字段」改成「按 schema 生成」

Placemaker 每个生成器有 30~60 个 settings（`parking` 就 40 个：stallAngle / aisleWidth / setback / accessibleShare / evShare / greenery / dressStyle…）。PCG 现在是手写 `地块参数 / 建筑参数 / 道路参数` 三组。

做法：C# 侧已经能 dump settings（`Emit.cs` 的 `settings` 字段），把它扩成 `schema`（类型/范围/枚举/单位/默认值/分组），TS 侧用一套通用控件渲染（slider / select / toggle / asset-picker / color）。**界面框架沿用 PCG，字段来自算法**——这才是「沿用交互定义」的正确落法。

### D6 · 街道是我们的**交付物**，不是实现细节

Placemaker 不生成街道，它消费街道。四个消费点全部依赖「沿边贴合」这一个几何事实：

- `EntranceSuggest` **只**看 `RoadAlongEdge`，reach = 20 m → 停车族能不能跑，全看这个
- `RoadBlocks` 要求 `RoadAlongEdge && RingSegment == seg && RoadWidth > 0` → 边分区铺不铺
- `Entrances` 沿边入口路径 20 m、非沿边只有 4 m

所以《A · 街道规则与验收条件》里的 10 条规则（R1 沿边贴合三阈值、R2 拐角节点、R3 精确构造 vs `AttachmentFinder` 双实现互验…）直接作为 town 算法的**验收条件**，而不是"以后再说"的完善项。违反的后果不是报错，是**静默退化**——`ready=no` 或者出一片莫名空地。

### D7 · 建筑吃 Placemaker 的 zoning，不自己算退线

建筑必须落在 `PlannedZoning.quad` 内，进深走 **8 m 模数**（1~6 格 = 8~48 m），朝向与临街边平行（≤1°）。

反面代价很具体：建筑自己按地块边算一次退线，就会和 Placemaker 的宗地格错开——楼站在砾石上，或者楼"没有地"看起来像规划失败。所以 `placeBuildings()` 的输入必须是 Placemaker 返回的 zoning，不是地块多边形。详见《B · 建筑预设规范》。

### D8 · 界面中文为第一语言，零英文，缺词报错

新增的英文来源有四处：143 个生成器名、每生成器 30~60 个设置键、11 个分区名、`ready=false` 的 `NeedsEntrance` 类原因键。

三条底线：界面零英文（单位除外）、优先行业词不直译、**`t()` 找不到词必须报错并让 CI 失败**（不回退英文）。理由很直接：静默回退 = 永远发现不了缺词。详见《C · 界面中文规范》。

### D9 · 冲突一律以 Placemaker 为准，但必须记账

原则简单，执行要留痕：**能移植的照搬 → 不能移植的降级并记账 → 降级后产生的连锁问题也记账**。已经有 17 条舍弃/降级（如 `ZoneGrid.Cells` 位图置空、`LayoutScale` 必须显式传、`SiteSafety` 用 clip 绕开）、11 条由此产生的问题。见《D · 功能舍弃与问题台账》，编号永久不复用。

### D10 · 建筑 = Slot（几何契约）+ Skin（可替换外观）

Placemaker 不产建筑（只给 `PlannedZoning`），原版 CS2 是游戏层按格子数从 prefab 池**匹配**，TownForge 是**参数化**—三种机制在不同层。我们取两者之长：

- **Slot**：由 `zoning.quad` 按 8 m 模数切出，决定全部几何（占地、退线、压楼、朝向）。确定性，永不变。
- **Skin**：可插拔——盒子（任何尺寸都能拉伸，永不缺席）→ prefab（资产库精确/包含匹配）→ 参数化（兜底）。

顺序是**盒子先铺满全城 → prefab 逐个替换 → 换不上的盒子兜底**。这样永远不会出现"缺资产导致场景空一块"，也保证换模型时几何指纹逐位不变。详见《E · 建筑资产库与匹配机制》。

---

## 3. 阶段计划

### P0 · 契约与基准（1 周）

| 动作 | 产物 |
| --- | --- |
| 建 pnpm monorepo | `packages/editor` `plan-ir` `planner-legacy` `planner-ts` `content` `scene3d` `export`；`tools/oracle` |
| 抽 Plan IR + zod + normalize + diff | `packages/plan-ir/src/{types,schema,normalize,diff}.ts` |
| 把 C# driver 包成 CLI | `pnpm oracle --case x.json --out y.json`（`dotnet run --project placemaker-core-lib/Driver`） |
| 建黄金语料 | 130 生成器 × 3 形状（矩形/凹多边形/带斜边）× 2 种子 ≈ 780 份；大份只存**指纹 + 抽样**，控制在仓库 20MB 内 |
| 基线自检 | `pnpm golden:diff` 在「C# vs C#」上恒等（先证明工具本身不撒谎） |

**Gate**：`oracle:sweep` 130/130 跑通；`golden:diff` 自检恒等；CI 能跑。

### P1 · 接缝改造（1.5 周）

1. **补 CaseInput 缺失的三件套**（这是「改造前期算法以满足第三方库需求」的实质内容）：
   - `streets[]`：PCG 已有 `RoadObj`（含 `width` / `sidewalk`），但没把「地块每条邻边 → 一条街」显式化。新写 `parcelStreets(model, parcel)`，输出 `{id, a, b, width, sidewalk, speed}`
   - `markers[]`：`access` 对象（`AccessType: main/service/pedestrian`）→ `{kind: entrance|serviceEntrance|pedestrianAccess, x, z, yaw}`
   - `obstacles[]`：`building`（`fixed`）+ `nobuild` 区 → 建筑轮廓四边形（复用 `collectObstacles`）
2. **生成入口改造**：`generateForParcel` → `backend.plan(input)`，接口 `PlannerBackend`；`legacy` 与 `placemaker` 两实现并存，UI 上一个下拉切换（**只给开发用，不给最终用户**）
3. **回灌改造**：Plan IR → `ProjectModel` 的 `Generated` 对象（保留 `generatedFrom` 语义，保证「换一版」只清当前地块、不动手绘与固定建筑）
4. **租户隔离**：地块级种子 `hash(projectSeed, parcelId, reseedCount)`，保证 `Shift+G` 只影响选中的一块

**Gate**：85 单测全绿；街道契约验收 100%（A 文档 R1–R10，含「精确构造 vs `AttachmentFinder` 计算」逐地块一致）；同一地块两 backend 各出一次图，2D/3D 都正常；e2e 81 项通过。

### P1.5 · 规则验证器（1~2 天）——把验收规则变成可拖的东西

**它不是编辑器，是验证器。** P2 那 11 项能力里有 7 项是渲染通道（paint / terraform / decal / furniture / zoning…），
而那些通道**只有 C# 会产出**，TS 生成器要到 P3 才有。现在把 P2 做掉，等于给一个即将被替换的引擎写查看器。

P1 结束时真正"看不见"的东西不在渲染层，在规则层：

| 现在只能读 | 应该能看见 |
| --- | --- |
| R1–R11 的控制台文本 | 哪条边被判临街、贴合点在哪、哪条边该有却没有 |
| `ready` 的反向验证报告 | 拖走一条街，预测和 C# 基线当场一起变 |
| 楼位的静态 SVG | 宗地与楼位的关系、边角料到底在哪 |

**交付**：`packages/editor-web`，单页，纯 TS 零 dotnet（`ready` 基线由 `--emit` 产物固化成 JSON）。

| 层 | 复用 | 判据 |
| --- | --- | --- |
| 规则层 | `street-rules/rules.ts` 的 `validateCase` | R1–R11 逐条亮灯 |
| 判定层 | `street-rules/ready.ts` 的 `predictReady` | 与 C# 基线并排对照 |
| 几何层 | `slots/cut.ts` + `validate.ts` | E1–E6 + 楼位画在画布上 |

**Gate**：54 份真实地块全部可载入；拖街道/拖角点后三层同步刷新；那份 `ready=no` 的碎片
（`p90-parking.part6`）在页面上的判定与 C# 基线一致（R1/R7 红、`not ready`）。

**和 P2 的关系**：这个页面是 P2 的骨架。P2 往里补渲染通道和生成器选择器即可，不用推倒重来。

### P1.6 · 三维场景查看器（**F1+F2+F3 已完成**，后续见 docs/F）

规则验证器回答"这条街合不合规"，但用户真正想看的往往是"**这块地会长成什么样**"。
这两件事需要不同的界面，不是同一个页面做漂亮一点就能兼得。

已落地：`packages/view3d`，把 Plan IR 的四个几何通道经 **`packages/scene-ir`** 立成 three 场景，
54 份真实方案可切换，单文件 2.95 MB 双击即开、零服务、零 dotnet。

F2 补"量感"：**正交为主**的五视图预设、街道分带、三类绿植、标线单独一层、
点楼块看层数/层高/总高/面宽×进深。

F3 补"增量"：`SceneIR` 带几何指纹 `geomKey`，realizer 按指纹比对 —— **改 1 栋楼的层数 =
新建 1 · 复用 114 · 删 1 · 2.2 ms**，选中只切材质（零几何重建），同一方案两次打开指纹逐字节相同。
它同时把 `app.ts` 里最后一行图元代码也清掉了：几何在 realizer，规则在 scene-ir。

现在还差**一级**：**资产替换**（F4）。楼还是盒子。
完整路线、验收门槛、以及踩过的八个坑（`$'` 替换爆炸、绕序翻面、脏标记截图、three 双产物重名、
`cos(仰角)` 量高错 21%、五视图共用 `viewSize`、两套 id 口径静默失效、楼高多 2 cm 门禁没拦住）
见 **`docs/F-三维生成路线图.md`**。

**Gate（已过）**：4 份带宗地方案能数出楼块；`park` 面板报"地面 0 块"与台账 Q-008 一致；
`file://` 下零 pageerror；等轴量高误差 2e-6%（屏幕投影 6.6e-14%）；
增量三组断言全绿（改层数 / 选中 / 关图层），离线 4 份方案 `changed = 1`。
**下一步做 F4 的资产替换** —— 依赖 docs/E 的尺寸阶梯先补齐 8/16/24 m 三档模数。

### P2 · 交互与渲染补齐（3 周）——把 CITYSKYLINE 踩过的 15 个坑搬进 PCG

按「没有它就跑不通」排序：

| # | 能力 | 关键约束（来自 CITYSKYLINE 实测） |
| --- | --- | --- |
| 1 | 出入口 marker 工具 | 商场族/市场族**必须有车行门**否则整块 `ready=no`；公园族的 `entrance` 是步行门，**不能无条件塞** |
| 2 | 障碍物层 + 裁剪模式 | `SiteSafety` 的语义是「删」不是「绕」：停车族 53 条车道会被切成 1 条。解法是「别让 Core 知道有楼」，生成后按楼裁剪（`--clip`），车道恢复到 39 条 |
| 3 | zoning 宗地图层 | 它不是可丢通道，`stripMall` 有 27% 面积是它；丢了那块地是一片没来由的草坪 |
| 4 | paint → 底色 | ≥地块 20% 且强度 ≥0.8 才换底；照原样画实心面必然 z-fighting |
| 5 | terraform → 台体 | 顶点高度是 0，高度在 `target` 里，判据用**顶点极差**；`raise` 要出有体积的台体，道具 `baseY` 必须跟随 |
| 6 | decal 摊条 | 标线是沿线撒的贴花串，按分类给尺寸、摊成 1.7m 窄条（互相重叠，不重叠就是虚线）；整场贴花只画外框 |
| 7 | furniture → category 查表 | 一律映射长椅的后果：自然保护区长了 314 张长椅（那是石块）。查不到走 `crate` 兜底 |
| 8 | 绿地族补草地底 | `park/greenwayPark/picnicGrounds/natureReserve` 的 surfaces 为空 |
| 9 | 生成器选择器 | 130 个先接已验的 31 个（parking 1 + 绿地/花园 19 + 堆场/球场 5 + 铺装/商业 6） |
| 10 | settings schema 表单 | 见 D5 |
| 11 | polish* 防误判 | 表面精修层给裸地输出 0 元素却返回 `ready=yes` |

**Gate**：31 份已接入方案在 PCG 编辑器里逐份打开，与 CITYSKYLINE 的 SVG 输出做几何对照；建筑验收 B1–B8 全过（压楼 0.00 m²）；`pnpm i18n:check` 通过、界面截图无英文；差异逐条记账（预期差异只剩「建筑由谁生成」）。

### P3 · 内核 TS 化（10~12 周，按族交付）

严格自下而上，每层先过基准再上一层：

| 批次 | 内容 | C# 参照 | 量级 |
| --- | --- | --- | --- |
| T0 | 生产者接口六件套：Input → SuggestMarkers → CheckReady → Plan(channels) → SiteSafety → 量化输出 | `Contracts.cs` / `Composition.cs` / `Emit.cs` | 2k |
| T1 | 数学几何：float3/float2、环、绕序、面积、偏移内缩、Clipper 桥 | Geometry 7.5k + `Compatibility/UnityMathematics.cs` | 6~8k |
| T2 | **Plan 模型 + Composer + 7 通道骨架** | Build 19.8k 的核心 | 8~10k |
| T3 | 路网子集：地块内道路/车道/环路/人行道（砍 CS2 外部寻路、升级、AI） | Roads 25k 的子集 | 10~12k |
| T4 | 生成器家族：Parking 7.1k → Park 5.7k → Plaza 6.1k → Commercial 9.6k → Leisure 6.5k → Scrapyard 2k → Fields 2.5k | Layers | 30~40k |
| T5 | 剥离与收尾：Types 只留 role/category/size 三件套；AddOns/Common 按需 | Types/Common | 不计 |

每族交付定义（四条全过才算完）：

1. `golden:diff` L1 结构不变量 100% 一致
2. L2 几何近似在容差内（对称差 ≤ 2%，对象数 ±10%）
3. 浏览器 `pnpm dev` 里可交互生成，无 dotnet
4. bundle 增量在预算内（单族 ≤ 150KB gzip，全量 ≤ 1.2MB）

### P4 · C# 退役（1 周）

- 从 build 链路摘掉 `dotnet`；`npm run dev` 不再需要 .NET SDK
- CI 保留 oracle 只做历史回归（不阻塞发布）
- 验收：130/130 生成器 + 82 配方在纯 TS 上跑通

### P5 · 产品化（并行，4 周+）

多地块（PCG Phase 3 未做）→ 用 TownForge 的 Voronoi + 拓扑寻路，Placemaker 只做地块内；**建筑资产库三阶段**（E1 slot 切分 1 周 → E2 约 30 个尺寸变体 1~2 周 → E3 模型逐个替换，持续）；风格系统；GLB 导出（搬 `export/glb.ts`，手写 glTF 2.0 不依赖 three exporter）；性能（Web Worker + 增量 patch，C# 单块 8ms，TS 预计 5~20x，必须设预算超时）。

总排期：**约 16~18 人周**（单人 4 个月）到「31 个生成器 + 编辑器闭环」；130 个全量再加 3~4 个月。

---

## 4. 移植顺序为什么这么定

1. **先骨架后家族**：Composer/Plan 模型是所有 143 个生成器的公共依赖，先错就得返工 143 次
2. **先已接入的 31 个**：它们有 CITYSKYLINE 的渲染结果做对照，验收成本最低
3. **Roads 拆着搬**：25k 行里真正和「地块内车道」有关的不多，全搬会把 CS2 的外部路网寻路一起拖进来
4. **Types 坚决剥离**：36k 行资产/升级定义搬过去，TS 会变成垃圾场，而且没有渲染价值

---

## 5. 对比分级（决定「像不像」的唯一标准）

| 级别 | 内容 | 判定 |
| --- | --- | --- |
| **L1 结构不变量** | 按 role/category 聚合的元素计数、铺装/绿地/建筑覆盖率、网线总长、路网拓扑度数分布、无自交、压楼面积、`ready` 状态与 `readyKey` | **必须 100% 一致** |
| **L2 几何近似** | 同种子下对称差面积比、Hausdorff 距离、对象数量 | 对称差 ≤ 2%，数量 ±10% |
| **L3 逐值一致** | 仅用于无随机纯函数：`inset`/`offset`/`splitByArea`/`clip`/`area` | 1e-4 米 |

**为什么不是逐字节**：C# 走 Unity.Mathematics 的随机与浮点，TS 走 mulberry32，序列必然不同。强行对齐 = 白烧几周。L1 保证「行为等价」，L3 保证「纯函数精确」，这就够了。

---

## 6. 风险与前置约束

| 风险 | 说明 | 对策 |
| --- | --- | --- |
| **许可证** | Placemaker 与 TownGeneratorOS 都是 **GPL-3.0**，合并后整仓必须 GPL-3.0。**已确认个人学习、无商业应用 → 不构成阻碍** | 仓库保留上游 LICENSE 与来源声明；若日后转商用，此条复活 |
| 范围失控 | 259k 行 = 一条产品线，不是一次重构 | 按族计价、按族验收；每族独立可交付 |
| 双写漂移 | C# 一改，TS 基准失效 | `placemaker-master/` 冻结只读 + 版本戳；只做一处修改 |
| 语义冲突 | PCG「道路是对象」vs Placemaker「道路是输入约束」 | D4 职责重划，P1 定死 |
| 确定性 | 「换一版」必须只影响当前地块 | 地块级种子哈希，随机流按地块隔离 |
| 浏览器性能 | 单块 8ms(C#) → TS 可能 40~160ms，全城 24 块 → 秒级卡顿 | Worker + 分块 + 30s 预算超时（对齐 C# 的 Budget）+ 结果缓存 |
| 测试成本 | 130 × 3 × 2 语料维护 | 只存指纹与抽样；小体积高频跑，大体积夜间跑 |

---

## 7. 明确不要做的事

1. **不要**在前端跑 .NET / WASM 版 CLR——体积与冷启动都不划算，且与「去 C#」目标相反
2. **不要**一次移植 259k 行——族为单位，做完一族上一族
3. **不要**让两套生成链并行超过 P3——超过一个季度必然漂移成两个产品
4. **不要**追求与 C# 逐字节一致——见 §5
5. **不要**把 CS2 的 `AssetRef / Upgrades / Category` 语义搬进 TS——只保留 role/category/size 的有损降维（`Emit.cs` 已经证明这层够用）
6. **不要**把建筑塞给 Placemaker——它的 `ObjectRole` 里根本没有 Building，硬塞只会得到一堆「被避让出来的空地」
7. **不要**让建筑自己算退线——建筑必须吃 Placemaker 的 `zoning.quad`，两套退线规则迟早对不齐（地基错位是最难查的 bug）
8. **不要**让英文进界面，也不要用「缺词回退英文」兜底——那样永远发现不了缺词
9. **不要**用平滑贝塞尔去拟合折线道路的折角——平行度判据按采样切向算，拟合后中段会偏离，反而丢掉 attachment

---

## 8. 第一个可执行的下一步（今天就能做）

1. 确认 `-PCG-` 仓库是否需要转为公开/或本地为准（GitHub 上该地址当前 404，本地有完整副本 `2026-10-28-14-22-16`，`.git` 被改名为 `.git_WRONG_REPO_BACKUP`，remote 指向 `-PCG-.git`）
2. 建 monorepo，把两份代码原样搬进 `packages/editor` 与 `packages/placemaker-legacy`（先不合并，只共处）
3. 写 `packages/plan-ir` 的类型 + zod + normalize（半天）
4. 跑通 `pnpm oracle`（`dotnet run --project Driver`）并烘 3 份 plan.json 进来当第一组语料
5. 拿这 3 份 plan 在 PCG 的 3D 舞台里画出来——**这是合并后第一张图**，先证明「IR 能驱动现有渲染」这件事成立
