# 界面中文规范

> 合并后 UI 会新增大量来自 C# 的标识符：143 个生成器名（`carYard` / `stripMall`）、每个生成器 30~60 个设置键（`stallAngle` / `dressDesirePaths`）、11 个分区名（`EU Commercial Low`）、30 多个角色名（`driveAisle` / `decal`）、还有 `ready=false` 时的原因键（`NeedsEntrance`）。
>
> **这些英文不能漏到界面上。** 规范只有一条底线：**界面上零英文；缺词必须报错，不许静默回退。**

---

## 1. 六条规则

| # | 规则 | 说明 |
| --- | --- | --- |
| N1 | **界面零英文** | 按钮、标签、提示、报错、状态、空态、tooltip 一律中文。仅有的例外：物理单位（m、m²、km/h）与资产编号 |
| N2 | **优先行业词，不直译** | `stallAngle` 不叫"车位角度"，叫「车位排列」并给三个选项「横列 / 纵列 / 斜列」；`driveAisle` 叫「车行通道」不叫"驾驶过道" |
| N3 | **缺词必须报错** | `t('gen.carYard')` 找不到 → 控制台 error + CI 失败 + 界面显示 `⟪缺词:gen.carYard⟫`，**不回退英文**。理由：静默回退等于永远发现不了 |
| N4 | **一概念一词** | 全局词表唯一。`aisle` 只能叫「车行通道」，不许某处「车道」某处「通道」 |
| N5 | **分组不按英文 key 排序** | 按业务顺序或拼音排序；否则界面上分组看起来像随机打乱 |
| N6 | **范围与危险操作写清楚后果** | `stallAngle` 改变会重排全部车位；`--keep`/`--clip` 切换会改变整块布局 → 旁边给一句中文说明，不要只放开关 |

---

## 2. 四张术语表

### 2.1 生成器名（143 个，先覆盖已接入的 31 个）

命名模式：**`<场地类型>` 或 `<场地类型>（<变体>）`**，不用"生成器"后缀。

| 英文 id | 中文名 | 备注 |
| --- | --- | --- |
| `parking` | 停车场 | 地面停车，最基础 |
| `carYard` | 汽车堆场 | 报废车堆放 |
| `junkYard` | 杂物堆场 | |
| `scrapyard` | 废料场 | |
| `runningTrack` | 田径场 | |
| `pitch` | 球场 | 足球/橄榄球 |
| `plaza` | 广场 | 硬质铺装 |
| `park` | 公园 | 通用绿地 |
| `greenwayPark` | 带状公园 | 线性绿地 |
| `picnicGrounds` | 野餐地 | |
| `natureReserve` | 自然保护区 | 几乎不铺地面 |
| `botanicalGarden` | 植物园 | |
| `dogPark` | 宠物公园 | |
| `cemeteryGrounds` | 墓园 | |
| `communityGarden` | 社区菜园 | |
| `campground` | 露营地 | |
| `marketSquare` | 集市广场 | 需要车行门 |
| `stripMall` | 沿街商业 | 需要车行门 |
| `mallPark` | 商场公园 | 需要车行门 |
| `outdoorMall` | 露天商业街 | |
| `shopsPark` | 商业绿地 | |
| `abandonedLot` | 废弃空地 | |
| `amphitheatre` | 露天剧场 | |
| `adventurePlayground` | 冒险游乐场 | |
| `formalGarden` | 规则式花园 | |
| `memorialPark` | 纪念公园 | |
| `landfill` | 垃圾填埋场 | |
| `logisticsYard` | 物流堆场 | |
| `constructionSite` | 施工场地 | |
| `highStreet` | 商业主街 | |

> 剩余 112 个在 P2 建表时补齐。**规则**：写不出行业词就先留 `⟪待定:<id>⟫`，不允许猜一个看起来通顺的。

### 2.2 设置项（以 `parking` 的 40 项为例，展示命名法）

| 英文键 | 中文标签 | 控件 | 单位 | 备注 |
| --- | --- | --- | --- | --- |
| `stallAngle` | 车位排列 | Choice | — | 横列 / 纵列 / 斜列（原值是 90/0/45 角度） |
| `stallWidth` | 车位宽 | Slider | m | |
| `stallLength` | 车位长 | Slider | m | |
| `aisleWidth` | 车行通道宽 | Slider | m | |
| `perimeterRoad` | 沿边环道 | Toggle | — | 关掉后地块内部可能不可达 |
| `crossEvery` | 横向通道间隔 | Slider | m | |
| `setback` | 临街退线 | Slider | m | |
| `zoningDepth` | 宗地进深 | Slider | 格 | 1~6 格，1 格 = 8 m |
| `zoneType` | 宗地类型 | Choice | — | 见 2.3 |
| `edgeZoning` | 沿边宗地 | Choice | — | 无 / 单侧 / 双侧 |
| `accessibleShare` | 无障碍车位占比 | Slider | % | |
| `evShare` | 充电车位占比 | Slider | % | |
| `greenery` | 绿化形式 | Choice | — | 无 / 绿篱 / 隔离带 |
| `medianDepth` | 隔离带宽度 | Slider | m | |
| `plantingDensity` | 种植密度 | Slider | % | |
| `lanternSpacing` | 路灯间距 | Slider | m | |
| `grading` | 场地找坡 | Toggle | — | |
| `fee` | 停车收费 | Slider | 元/时 | |
| `dressStyle` | 场地状态 | Choice | — | 整洁 / 使用中 / 荒废 |
| `dressPaintWear` | 标线磨损 | Slider | % | |
| `dressDesirePaths` | 踩踏小径 | Toggle | — | 抄近路形成的土路 |
| `dressMulch` | 树池覆盖物 | Toggle | — | |
| `rowAngleMode` | 排布基准 | Choice | — | 沿边 / 固定角度 |
| `rowEndPlanting` | 排端种植 | Toggle | — | |
| `trees` / `bushes` | 乔木 / 灌木 | AssetMulti | — | 空 = 自动搭配 |

### 2.3 分区名（11 种）

见《建筑预设规范》§2 的表。原则：**中文名里不出现 EU / NA 主题前缀**；主题切换是独立的「城市风格」设置，不与分区名混在一起。

### 2.4 角色与分类（`role` / `category`）

| role | 中文 | role | 中文 |
| --- | --- | --- | --- |
| `asphalt` | 沥青 | `parkingBay` | 车位 |
| `paving` | 铺装 | `charger` | 充电桩 |
| `grass` | 草地 | `decal` | 标线贴花 |
| `plaza` | 广场铺装 | `marker` | 出入口标记 |
| `water` | 水面 | `centerpiece` | 中心景石/雕塑 |
| `street` | 城市道路 | `furniture` | 场地杂物（按分类细分） |
| `avenue` | 林荫道 | `tree` / `bush` / `planter` | 乔木 / 灌木 / 花箱 |
| `perimeterRoad` | 沿边环道 | `bench` / `lamp` / `bin` | 长椅 / 路灯 / 垃圾桶 |
| `driveAisle` | 车行通道 | `fence` / `fencePiece` | 围栏 / 栏杆 |
| `driveConnector` | 连通车道 | `animalSpawner` | 动物活动点 |
| `pathway` | 园路 | `accessPoint` | 通行节点 |
| `alley` | 巷道 | `arrow` | 导引箭头 |

**`furniture` 的特殊处理**：它是通用角色，必须看 `category` 才准。映射遗漏的后果很具体——CITYSKYLINE 里"一律映射成长椅"导致自然保护区长了 314 张长椅（那些其实是`rock` 石块）。查不到的分类显示为「场地杂物（`<分类>`）」，**不许默认成任何具体物件**。

---

## 3. 状态与报错也必须中文

`ready=false` 时 C# 返回 `NextStepKey|ReasonKey`（`Emit.cs:118`），这两个 key 会直接进状态栏。必须建映射：

| key | 中文提示 |
| --- | --- |
| `NeedsEntrance` | 需要一个车行出入口 |
| `TooSmall` | 地块面积不足 |
| `WrongShape` | 地块形状不支持该生成器 |
| `Marker` / `entry` | 请在临街边放置出入口 |
| `Crosswalk` | 出入口需正对街道 |

状态栏文案模板：**「不能用：{原因}。{下一步}」**，例如「不能用：需要一个车行出入口。请在临街边点击放置。」

---

## 4. 落地方式

```
packages/i18n/
├── gen.zh.ts        生成器名（143）
├── settings.zh.ts   设置项（按生成器分组，含单位与控件类型）
├── zone.zh.ts       分区（11）
├── role.zh.ts       角色与分类
├── status.zh.ts     ready / 原因 / 报错
└── index.ts         t()，缺词报错（N3）
```

三条工程约束：

1. **`t()` 不接受字面量英文作为 fallback 参数**——API 上就不给这个口子
2. CI 步骤 `pnpm i18n:check`：扫遍所有 `t('...')` 调用，与四张表比对，缺一条就失败
3. 设置面板的**控件类型来自 C# 的 `ControlKind`**（Slider / Toggle / Choice / Asset / AssetMulti / Markers / Arrangement）、**分组来自 `ControlTier`**（Essential=常用、More=更多）。中文规范只负责标签，不负责结构——结构跟着算法走

---

## 5. 检查清单（P2 验收）

- [ ] 截图通读：任一界面无英文单词（单位除外）
- [ ] 未知生成器 id 显示 `⟪待定:id⟫`，不显示英文
- [ ] 缺词时 CI 失败（人为删一条验证）
- [ ] `ready=false` 的 12 种原因全部有中文
- [ ] 同一概念在 2D 画布、Inspector、状态栏、3D 面板用同一个词
- [ ] 数字格式统一：`7 m`、`32 m²`、`45°`、`50%`（数字与单位间一个半角空格，度数不带空格）
