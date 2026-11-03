# PCG × Placemaker 合并工程

把 PCG 的交互与界面，换成 Placemaker（Cities: Skylines II 场地设计器内核）的生成能力，并逐步把 C# 换成 TypeScript。

规划见 [`PCG-CITYSKYLINE-合并计划.md`](PCG-CITYSKYLINE-合并计划.md)，规则见 [`docs/`](docs/)：

| 文档 | 内容 |
| --- | --- |
| [A · 街道规则与验收条件](docs/A-街道规则与验收条件.md) | 街道作为输入契约的 11 条规则（R1–R10 硬性 + R11 提醒） |
| [B · 建筑预设规范](docs/B-建筑预设规范.md) | 11 类分区、8 m 模数、朝向与退线 |
| [C · 界面中文规范](docs/C-界面中文规范.md) | 零英文、术语四表、缺词报错 |
| [D · 功能舍弃与问题台账](docs/D-功能舍弃与问题台账.md) | 23 条舍弃/降级 + 15 条问题 + 4 条风险 |
| [E · 建筑资产库与匹配机制](docs/E-建筑资产库与匹配机制.md) | Slot / Skin 分层、三层匹配回退 |

## 目录

```
packages/
  plan-ir/        输入契约 + Plan IR 类型、校验、归一化、L1 差分
  street-rules/   街道契约：AttachmentFinder 移植 + 精确构造 + R1–R11 验收 + ready 判定模型
  slots/          zoning → 建筑位（Slot）；Skin 三层匹配；E1–E6 验收
  pcg-bridge/     PCG 数据 → 契约（折线道路拆段、宗地格网、出入口、建筑障碍）
  demo/           用例 + SVG 出图 + 单方案验收（run.ts）+ 批量回归（batch.ts）+ 反向验证（reverse.ts）
  editor-web/     规则验证器：单页应用，拖街道实时看 R1–R11 / ready / 楼位（见那里 README）
tools/oracle/     C# 基准：用 glob 编译 vendor 里的原始 Core（只读）+ --emit / --attachments
tools/batch/      真实城市夹具：54 份烘焙输入 + 4 份含宗地方案 + ready 基线
vendor/
  placemaker/Core/     Placemaker 内核源码（GPL-3.0，只读派生源）
  townforge-src/       TownForge 参考实现（GLB 导出、路网、内容层）
  pcg-editor-src/      PCG 编辑器源码快照（React + Canvas2D + three）
tmp/plans/        真实 emit 出来的基准方案（JSON）
```

## 跑起来

```bash
# 1) 编译 C# 内核（0 错误 0 警告，约 18 秒）
npm run oracle:build

# 2) 生成基准方案（三个用例）
npm run oracle:emit

# 3) 单方案验收 + 出图（控制台报告 + packages/demo/out/round2.html）
npm run demo

# 4) 批量回归：54 份真实地块 + 4 份真实方案（约 1 分钟，含 54 次 dotnet 调用）
node packages/demo/src/batch.ts          # → packages/demo/out/batch.html
node packages/demo/src/batch.ts --no-csharp   # 跳过 C# 对照，秒级
node packages/demo/src/batch.ts --only=mall   # 只跑名字含 mall 的

# 5) 反向验证：我们能不能提前说出 C# 会不会 ready（约 3 分钟，含 102 次 dotnet 调用）
node packages/demo/src/reverse.ts        # → packages/demo/out/reverse.html
node packages/demo/src/reverse.ts --no-emit   # 读上次结果，只重算扰动实验

# 6) 规则验证器（浏览器）：拖街道实时看 R1–R11 / ready / 楼位
node packages/editor-web/build.mjs       # 编译 TS + 打包夹具 + 出内联版（约 5 秒）
#    然后直接双击 packages/editor-web/规则验证器.html —— 不需要任何服务
#    要边改边看就改用 http 版：node packages/editor-web/serve.mjs  → :8765

# 7) 生成器家族表（130 个生成器的 ready 归属）
tools/oracle/driver/bin/Debug/net6.0/PlacemakerDriver.exe --generators tools/batch/generators.json
node packages/street-rules/tools/gen-entrance-classes.mjs   # → packages/street-rules/src/entrance-classes.generated.ts
#    一条命令重跑反射 + 归类：node packages/street-rules/tools/gen-entrance-classes.mjs --refresh

# 8) 产品：TownForge 编辑器（vendor/townforge 是真源码，只读参考）
node packages/editor-townforge/build.mjs    # 跑 vite 构建，再把 CSS/JS 内联成单文件
node packages/editor-townforge/verify.mjs   # 无头 Edge：10 项断言（左/右栏、画布有墨迹、3D 挂载、GLB 导出、零报错）
node packages/editor-townforge/verify-content.mjs  # 内容来源三档：TownForge 396 / Placemaker 886 / 并排 1282（13 项断言）
cd vendor/townforge && node node_modules/tsx/dist/cli.mjs test/bridge-parking.test.ts  # 停车场/公园迁移对账（16 项）
cd vendor/townforge && node node_modules/tsx/dist/cli.mjs test/bridge-access.test.ts   # 出入口接街 + ready 因果链（29 项）
cd vendor/townforge && node node_modules/tsx/dist/cli.mjs test/bridge-park.test.ts     # 公园园路与树（22 项）
# 米制桥的验收（239 项断言：单位唯一 / 宗地拟合 / 进深 8 m 格 / 楼高=层数×层高 / 宗地合并与最小面积）：
cd vendor/townforge && node node_modules/tsx/dist/cli.mjs test/bridge.test.ts
# 深度规则的单元测试（C# `ReduceDepths` 逐行移植，含"孤立 1 列深坑"病态场景）：
cd vendor/townforge && node node_modules/tsx/dist/cli.mjs test/zonegrid-depth.test.ts
cd vendor/townforge && node node_modules/tsx/dist/cli.mjs test/gen.test.ts   # 原有 53 次生成回归
#    然后双击 packages/editor-townforge/编辑器.html —— 不需要任何服务（779 KB）
#    依赖已装在 2026-10-29-10-34-07/townforge/node_modules，vendor 里是指向它的符号链接

# 9) 三维场景（看"这块地会长成什么样"，而不是"规则过不过"）
node packages/scene-ir/verify.mjs       # 离线验增量层：改一栋楼必须只变 1 个节点（零浏览器）
node tools/inline-three.mjs              # three 的 ESM 产物 → 经典脚本（只跑一次）
node packages/view3d/build3d.mjs         # 编译 + 打包通道数据 + 出内联版
#    然后双击 packages/view3d/三维场景.html —— 不需要任何服务
node packages/view3d/verify.mjs          # 无头 Edge：切视图 / 点选 / 量高 / 增量 / 确定性 + 截图

# 类型检查（用 PCG 自带的 tsc，本机无 npm 网络）
#   tsconfig.json       → 含 node 侧的包（demo / 脚本）
#   tsconfig.web.json   → 浏览器侧的包（plan-ir / street-rules / slots / editor-web）
TSC="C:/Users/Administrator/WorkBuddy/2026-10-28-14-22-16/node_modules/typescript/bin/tsc"
node "$TSC" -p tsconfig.json && node "$TSC" -p tsconfig.web.json
```

> 本机没有 npm 网络，所以没有 `node_modules`：`node` 直接跑 `.ts`（Node 22 的类型擦除），
> 因此**只能用可擦除语法**（不能用 `enum`、`namespace`、构造函数参数属性）。
> `node:*` 的类型是手写的最小声明（`packages/demo/src/node-shims.d.ts`）。

## 已完成

| 项 | 状态 | 证据 |
| --- | --- | --- |
| C# 内核脱离游戏可编译 | ✅ | `vendor/placemaker/Core` 466 文件，0 错误 0 警告 |
| 真实方案可生成 | ✅ | `parking` 15000 m² / 面 31 / 渠 32 / 物件 542 / 宗地 4，17 ms |
| Plan IR 契约 + 校验 + 归一化 + L1 差分 | ✅ | `packages/plan-ir` |
| 街道契约 R1–R11 | ✅ | 四个合成用例全通过（C/D 是故意造的坏例子，按预期拦下） |
| 与**真 C#** `AttachmentFinder` 逐点对照 | ✅ | **3365/3365 项 = 100%**：54 份真实地块、673 个贴合点 × 5 字段 |
| PCG 数据 → 契约（`parcelStreets` / `zoneGridOf`） | ✅ | L 形地块 + 5 条路（含 1 条园路）→ 6 段街道、4 个格网、30/30 项一致 |
| zoning → 建筑位（Slot） | ✅ | 31 块真实宗地 → 40 个楼位，E1–E6 全绿 |
| 资产匹配三层回退 | ✅ | 占位永不缺席；预制件命中率随库增长 |
| 换 Skin 几何指纹不变 | ✅ | 空资产库重算，指纹不变 |
| **反向验证：预测 C# 的 `ready`** | ✅ | **54/54 = 100%**；扰动 48/48 = 100%（`reverse.html`） |
| **规则验证器（浏览器）** | ✅ | 54 份地块可拖拽；R1–R11 / ready / 楼位三层同屏实时结算；**内联单文件，双击即开** |
| **130 个生成器的 ready 归属** | ✅ | `--generators` 反射 → 12 种组合、`unknown` 0；家族表由脚本生成，不再手抄（`entrance-classes.generated.ts`） |
| **三维场景查看器** | ✅ | 54 份真实方案可切换；4 份带宗地能出楼块；**正交五视图 + 点选楼块看层数/尺寸**；单文件 2.95 MB 双击即开（`docs/F`） |
| **产品：TownForge 编辑器** | ✅ | 真源码 vendored（51 TS / 1.1 MB，26000 行）；张量场城市生成器：**99 地块 · 396 建筑**、8 组图层显隐、7 段参数面板、GLB 导出 10.9 MB；单文件 779 KB 双击即开；10/10 断言（`docs/G`） |
| **SceneIR 增量层（F3）** | ✅ | 改 1 栋楼的层数 = **新建 1 · 复用 114 · 删 1 · 2.2 ms**；选中零几何重建；同一方案两次打开指纹逐字节相同（`irKey` 3905） |

## 实测发现（已回写文档）

1. **真实宗地不是规则的**。31 块真实宗地进深实测 17.00 / 25.00 / 33.00 / 14.00 / 22.00 / 12.75 / 8.50——**全都不是 8 的倍数**。早先"进深必是整格"的结论来自我们自己造的合成用例（那样的用例太干净），已被真实数据否掉，台账 D-019。
2. **短边是采样分辨率的地板**。1146 条真实 block 边里 195 条临街边 <12 m（17.0%），其中 58 条 ≤4 m——画法模式下**永不临街**（1 个采样点 → 重叠恒为 0）。这不是 bug，是算法的分辨率上限，但它是街道生成器的硬约束 → 新增规则 R11。
3. **"把宗地当矩形、按目标面宽硬塞"会全面崩**。31 块真实宗地上表现为：重叠 10~66 对、越界 3~11 个、浪费 46.9%。根因三条，已全部修掉（docs/E §3.3），现在 E2/E3 是 0。
4. **`zoningRef` 不能等于 `zoneId`**。同一方案 5 块宗地的 `zoneId` 全是 `"EU Commercial Low"`，拿它当归属键会让重叠检测跨宗地比较各自的局部框，报出**不存在**的重叠（台账 D-021）。
5. **`externalT` 有精确平局**。它取"离边中点最近的采样点"，中点在两采样点垂直平分线上时两者完全等距，C# 由 float32 噪声决定取哪个。我们定向取前一个，比对器把这类差异记为"平局等价"（673 个里 3 个，0.45%），台账 D-018。
6. **`pointAt` 不能吸附到采样点**：100 m 的街按 4 m 采样得 26 点，中点吸附到 index 13 = 0.52，位置偏 2 m——精确构造与反推互验立刻抓出来了。
7. **`edgeZoning` 的取值是 `none | oneSide | all`**（不是 `both`）；写错会静默得到 0 块宗地。
8. **公园族 `surfaces` 确实是 0**：`park` 出 18 条渠、172 个物件，一个面都没有 → 必须补草地底（台账 Q-008）。
9. **`ready` 只数出入口 marker，从不看街道**。真实因果链是 `ready ⇔ CheckReady(given>0 ? given : suggest)`（`Emit.cs:95-111`），而 `suggest` 只有 `parking` 和 `typed` 两族非空。**街道规则能影响 `ready` 的生成器只有 parking 一族**；商场（stripMall/mallPark/mallDistrict）和市集（marketSquare）的 `SuggestMarkers` **恒返回空列表**，街道怎么改都改不动它们的 `ready`（台账 D-022）。
10. **街道真正的作用点不是 `ready`，是「路径许不许接街」**。`LayerBase.cs:521` 的 `TrimOutlineEnds` 才是那个开关：`ready=no` 时所有碰到轮廓的路终点全部缩回去——**布局照样生成，只是路网封在轮廓内**。这就是之前反复看到的"静默降级"的精确机制。
11. **`ready=yes` 不等于"路接上了街"**。建议出来的口 `AttachId` 恒为 `-1`（`Entrances.cs:371`），只有玩家或烘焙工具给的口才真接街。实测 53 份 `ready=yes` 里只有 16 条路径接街（台账 D-023）。
12. **`EntranceSuggest` 不是"20 m 内沿边贴合"**，是"40 m 外射线打车行道，`gap ∈ [-w/2, 15]`"。`docs/A §0` 曾把它写成前者（引用的是 `NearAttachment` 那个兜底分支），据此写出来的第一版判据全错。容差比 `AttachmentFinder` 的 `w/2+3` 大一个数量级：街整体外移 11 m，parking 仍 `ready`，产出 593 → 590（台账 Q-015）。

13. **手抄 130 行家族表会漂移，而且已经漂过一次**。`ready.ts` 原表把 `plaza` 归成 `typed`，实际 `PlazaLayer.CheckReady` 恒 true 且建议走的是 `Entrances.Suggest` 不是 `SuggestTyped`。改成反射生成后：`CheckReady` 的声明点只有 11 种、和两个开关组合起来只有 **12 种**，`unknown` 从 98 → **0**（台账 D-024）。
14. **正交里"1 米 = 多少像素"对竖直方向要乘 `cos(仰角)`**。第一版量楼高直接用 `画布高 / (2×视野半高)`，误差稳定停在 21.2% —— 恰好是 `cos(38°) = 0.788`。看着像渲染把楼压扁了，其实是量法错了。改成"在该楼屏幕位置现场标定 1 m 竖直线"，误差降到 6.6e-14%（`verify.mjs` 自动断言，F2 的 Gate）。
15. **门禁要打印数值，不能只报过/不过**。F3 重构时楼块 `y1` 写成 `h + ELEV.marking`、`y0` 是 `ELEV.marking - 0.02`，几何比标注高 2 cm（4.5 m 楼上 = 0.44%）。阈值是"< 2%"，所以**它过了**；是"这个数上一轮 2e-6%、这一轮 4e-3"才对不上。修复后在离线校验里改成**精确相等**断言 `y1-y0 == 层数×层高`（台账 D-027）。
16. **同一份数据两套 id 口径会静默失效**。层数覆盖表用楼位 id（`z0-b0-r0`）、查询用节点 id（`bld:z0-b0-r0`），结果是"拖了滑块什么都没变"，0 次重建、不报错。统一成节点 id 后正常（台账 D-026）。

## 下一步

- ~~**反向验证**：比对 `ready`~~ ✅ 已完成，54/54 + 扰动 48/48
- ~~**`typed` 族判据未移植**~~ ✅ 已解决，但**不是靠移植**：`Entrances.Suggest` 在候选为空时会兜底「最长边的中点」，所以公园族恒 ready。扰动实验里把街道全删光，18/18 仍然 ready，证实了这一点（`ready.ts` 顶部有推导）
- ~~**规则验证器**~~ ✅ 已完成，见 `packages/editor-web`
- ~~**剩下 98 个生成器的家族分类**~~ ✅ 已解决：不靠源码级人工分类，靠 `--generators` 反射（虚方法声明点 + 保护开关实例值）+ 12 条规则 → `unknown` 0。**夹具预算现在可以只花在真正要跑的少数族上**
- ~~**三维与 SceneIR 增量层**~~ ✅ 已完成 F1/F2/F3，见 `docs/F`。**下一级是资产替换（F4）**
- ~~**产品级编辑器**~~ ✅ **拿对源码了**：真产品是 TownForge（`vendor/townforge`），已能构建、能跑、能导 GLB，见 `docs/G`。
  我手搓的 `packages/editor-app` **已废弃**（基于错分支 + 错产品定义，手绘地块/建筑那一套）。
- ~~**G2 单位与桥**~~ ✅ 已完成：单位真源 `core/units.ts`（7 m/单位，唯一一处定义）；
  `bridge/{zone,placemaker}.ts` 把街区转成**米制**宗地并用 `packages/slots` 切楼位；
  楼高出口统一成「层数 × 层高」。实测：宗地拟合中位 88~90%、进深 100% 贴近 8 m 格、
  **104 项断言 0 失败**，53 次生成回归 0 失败（`docs/G §3.2`）。现已涨到 **239 + 8 项**（见文末"宗地真的合并"）
- ~~**G3 内容替换（第一刀）**~~ ✅ 右栏「内容来源」三档：**TownForge 396 栋 / Placemaker 886 楼位 / 并排 1282**；
  楼位进深带按街区四边推、楼只占楼位的 37%~41%（与自排同量级）；13 项断言 + 110 项桥断言全绿（`docs/G §3.3`）
- **下一步 G3.5**：**单块地**切换内容来源（现在还是全城一起切）；把地块面板的 `density / far / heightRange`
  真正接进桥（现在层数是"实测楼高反推"的间接口径）
- ~~**出入口接街（G5 第一刀）**~~ ✅ `bridge/access.ts` 复现 `EntranceSuggest`：
  **40 m 射线、`gap ∈ [-w/2,15]`**。三个规模接街率 **100%**、停车场 ready 2/2·3/3·1/1；
  喂空路网（=删光街道）→ 出入口 0、ready 0/2 —— **因果链可测**（`docs/G §3.7`）
- ~~**层数改由规划参数算**~~ ✅ `USE_PLAN`（密度/容积率/高度区间，来自 TownForge 的 `PARCEL_STYLE`）→
  `层数 = clamp(min(far×宗地÷基底, 限高÷层高))`。实测 **CBD 10F / 住宅 6F / 商业 4F / 公共 4F / 工业 2F**，
  同用途完全一致、被夹宗地 **0%**（`docs/G §3.8`）
- ~~**停车场生成器**~~ ✅ `bridge/parking.ts` 复现 `ParkingLayer`：**2.52×5.04 m 车位 / 6.30 m 通道 / 双排阵列**，
  门禁 = 接街车行口（无口一个位都不排）。实测 **417（TF 321）/ 539（TF 472）**；喂空路网 → 0 车位（`docs/G §3.9`）
- ~~**公园 / 绿地**~~ ✅ **分层接管**：地表仍用 TF 的草地（Q-008），园路与树用我们排的
  （环路内退 4 m、宽 2.4 m；>3000 m² 加十字路 + 9 m 广场；树 20 棵/千m²、离路 ≥2.8 m）。
  实测 514 / 922 棵（TF 720，1.28×）；树不压路、不越界（`docs/G §3.10`）
- ~~**`ZoneGrid` 切宗地**~~ ✅ 按 C# `ZoneGuard` + `ZoneBlockPredictor` 原样移植：
  **8 m 格 × 6 排深 × 每块 ≤10 格**，格心公式照抄。实测进深 **98% 精确整格**、
  拟合 **0.99**、**0 块因形状跳过**；但楼位变多（1301 vs TF 396 = 3.3×）——CS2 式划区的必然结果（`docs/G §3.11`）
- ~~**楼位/栋 读数 + 资产槽位**~~ ✅ 面板显示 `楼位 1301 / 自排 396 · 3.3×` 与
  `资产槽位 注册 0 · 命中 0% · 拆塔楼 195`；`bridge/assets.ts` 立起
  `assetKey = 用途|面宽×进深|层数` 的槽位机制（命中就用预制件，否则程序化 prefab）；
  ≥8 层的楼拆成**裙房 + 退台塔身**（15%/8% 的楼位被拆，体块 +15%）。170 项断言全绿（`docs/G §3.12`）
- ~~**程序化预制件**~~ ✅ 体块出口加 `detail`：**每层外挑 3 cm 腰线 + 0.5 m 女儿墙**（只加三角形、不加对象），
  ≥8 层拆裙房+塔身；资产槽位改成**模数命中 97%~100%**（面宽×进深落在 8/12/16/20/24/32/40/48 档上）——
  这条数据直接印证 docs/E 的"按模数补资产"（`docs/G §3.13`）
- ~~**用地按城市形态 + 宗地按深度分带**~~ ✅ 查了 C# `docs/proposals/s40.md`：**"the plots ARE the cells"、
  pad ≥ 2 格宽、dedupe > 0.1 m**。据此重写：宗地按**列深度分带**（同深度相邻列合并）+ 最小面积/最小列宽；
  用地改成**同心环 + 主干道加成 + 11 类保底全覆盖**。实测：宗地覆盖 92%→**69%**（有院子）、
  建筑覆盖 36%→**23~29%**、楼位尺寸分化（工业 24×23 / 联排 8×16 / 办公 24×26）、11 类全覆盖（`docs/G §3.15`）
- ~~**宗地真的合并（不止一次分割）**~~ ✅ 移植了 C# `ZoneGuard.ReduceDepths`（`ZoneGuard.cs:1231`，注释写明
  是游戏 `Game.Zones.CellOverlapJobs` 的反编译移植）：**游戏是"削平"列深、不是"切开"**，而且 `Get` 会
  越过本块去读同一条路相邻块的排号。配合"取满的最大同深矩形 + 逐层剥"提取、`mergeRects`（共线+同深+相接）、
  过滤挪到合并之后，以及把**格子归属排序提成 `ClaimPolicy` 参数**（6 种实测选优 → 取 `long`）。
  实测：p90 1152→**1536**、最大 2688→**5376**（种子均值 6592）、面积 CV 0.34→**0.64**（均值 0.79）、
  拼接占比 0~2%→**1.8~13%**、条带 359→**317**（`docs/G §3.16`）
- **下一步（密度）**：真正的问题已定位为 **D-056 —— `cutZoning` 按 `maxDepth` 把一整块地折成 2~3 排**，
  48 m 进深的地块被楼从街到背铺满（楼位离路平均 17.9~23.3 m），没有后院。覆盖率 28% 是对的（比 TF 的 40% 还低），
  密的是**排数**。改法：按名义 `depth` 落楼、余深留作后院（对齐 docs/B §3.2 的"前后院"）
- **下一步**：真资产替换 —— 需要 GLB 模型；模数命中 97% 说明"按档做模型"能覆盖几乎全部楼位
- **补足夹具样本**：现在 54 份只有 20 个不同几何（台账 Q-013），130 个生成器里只有 32 个有真实烘焙样本。家族虽已全覆盖，但**"这类地块实际跑起来什么样"仍只有 32 个有证据**
- **资产库尺寸阶梯**：真实面宽只有 8.6~16 m（中位 16）、进深 8~24 m（中位 14），共 11 种 w×d 组合；先补 8/16/24 m 三档模数（docs/E §4.3）
- 待查：D-017（哪 2 个生成器无输出）、D-005（配方语料能否脱离游戏实体）、`civic` 用地的 zone 映射

---

## 仓库

PCG-V113 · <https://github.com/zhouzheng5460/PCG-V113>

```bash
npm run oracle:build        # 需要 .NET 6+：把 Placemaker Core 编成 oracle 驱动
npm run golden:emit         # 用 oracle 烘 54 份 golden 快照（含输入）
npm run sgen:selfcheck      # 仪器自检：golden 对自己，必须 19/19 全 100 分
npm run sgen:compare        # golden vs 原生 TS：逐项差距
npm run sgen:check          # 门禁：协议完整性 + 自检 + 不回退基线
cd vendor/townforge && node node_modules/tsx/dist/cli.mjs test/bridge.test.ts   # 239 项桥断言
```

`vendor/townforge/node_modules` 是指向本机工作区的符号链接，**已加进 `.gitignore`**；
首次克隆后在该目录自行 `npm install`。
