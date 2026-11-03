# 建筑预设规范

> Placemaker 的 `ObjectRole` 里**没有 Building**——它是纯室外场地设计器，建筑只以「要避让的障碍物四边形」出现。
>
> 所以建筑由我们生成，但**必须与它的地面规则同源**，否则会出现两种典型穿帮：楼站在砾石宗地上（两套系统各说各话），或者楼"没有地"（Placemaker 没给这块地 zoning，看起来像规划失败）。
>
> 本文定义：建筑预设怎么分组、尺寸怎么走模数、朝向怎么对齐、以及和 Placemaker 三种建筑接口的对应关系。

---

## 0. 先分清三层（否则会问错问题）

| 层 | 是否产建筑 | 机制 |
| --- | --- | --- |
| Placemaker 内核（`Core/`） | ❌ 完全不产 | 只给 `PlannedZoning { A,B,C,D, ZoneId }` |
| CS2 游戏层（`Game/`，我们不搬） | ✅ 产 | **固定预设匹配**：按地块格子数从 prefab 池挑能放下的楼 |
| TownForge（`modern.ts`） | ✅ 产 | **参数化**：从地块 bbox 算数量/进深/高度，不查表 |

本文 §3 的"建筑预设表"因此**不是"建筑模型清单"，而是"建筑位（Slot）的尺寸规则"**。几何与外观如何分层、如何先盒子占位再换模型，见《E · 建筑资产库与匹配机制》。

---

## 1. Placemaker 对建筑的三种接口

| 接口 | 类型 | 含义 | 我们怎么用 |
| --- | --- | --- | --- |
| **Obstacle** | `{quad: [A,B,C,D], kind}` | **已有**建筑/水面/管线，生成器必须避让 | 固定建筑、保留建筑、手绘禁建区 |
| **PlannedZoning** | `{zoneId, quad}` | **将来**要盖楼的地，按 zone 类型着色 | 建筑落位的**唯一合法区域** |
| **ZoneGrid** | `{position, direction, nx, ny≤6}` | 8 m  клетк 的路侧宗地格网 | 建筑模数与朝向的基准 |

三条硬事实：

1. `Obstacle` 是**定向四边形**，不是任意多边形（`Emit.cs:329`）。L 形建筑必须事先拆成多个 quad。
2. 面积 < **0.5 m²** 的障碍物被 `SiteSafety` 无视（`Emit.cs:350`）——拆 quad 时不能把建筑拆成碎片。
3. ZoneGrid 的格子是 **8 m**（`ZoneGuard.cs:56`），深度 **1..6 格 = 8~48 m**（`ParkingSettings.cs:51`）。这是建筑进深的合法区间。

---

## 2. 分区类型（11 种，必背）

来自 `Core/Layers/Common/ZoneKinds.cs`。这是建筑的**分组键**，也是 UI 下拉框的取值。

| kind | prefab id | 中文名（UI） | 典型建筑 |
| --- | --- | --- | --- |
| `commercialLow` | EU Commercial Low | 低密度商业 | 沿街商铺、便利店、小餐饮 |
| `commercialHigh` | EU Commercial High | 高密度商业 | 商场、写字楼裙房 |
| `mixed` | EU Residential Mixed | 混合用地 | 底商上住 |
| `residentialLow` | EU Residential Low | 低密度住宅 | 独栋、双拼 |
| `residentialRow` | EU Residential Medium Row | 联排住宅 | 沿街连续排屋 |
| `residentialMedium` | EU Residential Medium | 中密度住宅 | 多层公寓 |
| `residentialHigh` | EU Residential High | 高密度住宅 | 高层住宅塔 |
| `lowRent` | Residential LowRent | 廉租住宅 | 板式住宅（低标准） |
| `officeLow` | Office Low | 低层办公 | 办公园区 |
| `officeHigh` | Office High | 高层办公 | 塔楼 |
| `industrial` | Industrial Manufacturing | 工业制造 | 厂房、仓库 |

> 空 `zoneId` = **不划任何宗地**（`PlanWork` 直接跳过），所以 kind 永远要有一个值。默认 `commercialLow`。

---

## 3. 建筑预设表（我们的设计决策，受上面三条硬约束）

### 3.1 尺寸必须走 8 m 模数

| 参数 | 取值规则 | 约束来源 |
| --- | --- | --- |
| 进深 | **8 / 16 / 24 / 32 m**（1/2/3/4 格），最大 48 m | ZoneGrid 8 m 格、深 ≤6 |
| 面宽 | 8 m 的整数倍（8/16/24/32…），最小 8 m | 与相邻楼拼排时缝宽才整齐 |
| 层高 | 统一 3.6 m（PCG 现值），工业/仓储例外 8 m | PCG `FLOOR_HEIGHT` |
| 层数 | 按 zone kind 区间，见下表 | 密度语义 |
| 退线（front） | ≥ `EdgeMargin × LayoutScale`，默认 2 m × 0.5 = **1 m** | `SiteContext.EdgeMargin = 2 m`、`LayoutScale` 默认 0.5 |
| 退线（侧/后） | 0（贴排）/ 4 m（独立） | — |
| 朝向 | 与临街边切向平行，误差 ≤ **1°** | 否则 8 m 宗地格对不齐 |

### 3.2 十一类预设（= Slot 的尺寸规则，不是模型清单）

> 下表定义的是**建筑位（Slot）**：切成多大、多深、朝哪。长什么样由 Skin 决定（盒子 / prefab / 参数化），见《E · 建筑资产库与匹配机制》。

| kind | 进深 | 面宽 | 层数 | 层高 | 封闭度 | 备注 |
| --- | --- | --- | --- | --- | --- | --- |
| 低密度商业 | 16 | 8~24 | 1~2 | 4.5 | 独立 | 前场停车，退线 4 m |
| 高密度商业 | 32~48 | 24~48 | 3~8 | 4.5 | 贴排 | 裙房 + 塔楼，裙房进深 32 |
| 混合用地 | 16 | 8~16 | 3~6 | 3.6 | 贴排 | 底商 4.5 m 层高 + 上部 3.6 |
| 低密度住宅 | 16 | 8~16 | 2~3 | 3.0 | 独立 | 前后院 |
| 联排住宅 | 16 | 8 | 2~3 | 3.0 | **贴排** | 面宽固定 8，连续排布 |
| 中密度住宅 | 16~24 | 16~32 | 4~6 | 3.0 | 贴排/独立 | — |
| 高密度住宅 | 24~32 | 16~24 | 8~20 | 3.0 | 独立塔 | 塔楼占地小，周边留绿地 |
| 廉租住宅 | 16 | 24~48 | 4~6 | 3.0 | 贴排板式 | — |
| 低层办公 | 24 | 16~32 | 2~4 | 4.0 | 独立 | 园区式 |
| 高层办公 | 32~48 | 24~32 | 10~30 | 4.0 | 独立塔 | 最大进深 48（6 格上限） |
| 工业制造 | 32~48 | 32~64 | 1~3 | 8.0 | 独立 | 大跨，屋面可用 |

> 表中数值是**预设默认值**，全部可被生成器参数覆盖；不可覆盖的只有：进深 8 m 模数、≤48 m、朝向平行街、退线 ≥ EdgeMargin。

### 3.3 与地面规则的三个对齐点

| 对齐点 | 规则 | 违反的可见后果 |
| --- | --- | --- |
| **落在 zoning 内** | 建筑 footprint 必须被某块 `PlannedZoning.quad` 包含（允许 0.1 m 容差） | 楼站在砾石/草地上，看着像规划失败 |
| **不压铺装与车位** | 建筑与 `surfaces(role≠zoning)` 的交集 = **0 m²** | 车位被楼压住 / 楼悬在沥青上 |
| **不越路缘** | footprint 到 `ring` 的最近距离 ≥ `EdgeMargin × LayoutScale` | 楼压人行道 |

### 3.4 L 形与凹形怎么拆

`Obstacle` 只有四边形。拆法直接影响避让结果：

1. 优先沿**最长直边**切成若干矩形，切口落在 8 m 模数上
2. 每块面积 ≥ 0.5 m²（低于此值会被无视，等于留了个可以穿过去的洞）
3. 拆出来的块**互相之间不重叠**（重叠会让 `SiteSafety` 重复裁剪，把中间切空）
4. 拆完的并集面积 = 原建筑面积（容差 0.1 m²）

---

## 4. PCG 用地类型 → Placemaker zone kind

PCG 现在是 6 类 `LandUse`，需要映射过去（映射是设计决策，标注了依据强弱）：

| PCG LandUse | 主 zone kind | 依据 | 置信 |
| --- | --- | --- | --- |
| `urban` | `mixed` / `residentialMedium` / `commercialLow`（按临街等级与容积率细分） | 容积率高→mixed，临主路→commercialLow | 中 |
| `industrial` | `industrial` | 直接对应 | 高 |
| `rural` | `residentialLow` | 低密度 | 中 |
| `construction` | 不划 zone，改用 `constructionSite` / `abandonedLot` 生成器 | 它本来就是"待建" | 高 |
| `park` | 不划 zone（绿地族不需要），临街边可给 `commercialLow` 做 edge zoning | 绿地生成器 surfaces 为空，靠补草地底 | 高 |
| `civic` | `officeLow`（暂定） | 无公共设施 zone；用广场/纪念类生成器更合适 | **低，待验证** |

> `civic` 这条要在 P2 用真实方案验证：如果广场/纪念族能自己撑起这块地，就不该强加 zone。

---

## 5. 验收条件

| 编号 | 指标 | 阈值 |
| --- | --- | --- |
| B1 | 建筑 footprint ⊂ 某块 zoning quad | 100%（容差 0.1 m） |
| B2 | 建筑 ∩ 非 zoning 铺装 | 0 m² |
| B3 | 建筑到 ring 最近距离 | ≥ `EdgeMargin × LayoutScale` |
| B4 | 进深对 8 m 模数的偏差 | ≤ 0.05 m |
| B5 | 朝向与临街边切向偏差 | ≤ 1° |
| B6 | 拆 quad 后并集面积误差 | ≤ 0.1 m² |
| B7 | 拆出碎块（<0.5 m²）数量 | 0 |
| B8 | 保留建筑场景压楼面积 | **0.00 m²**（CITYSKYLINE 已达标的基线） |

B8 是回归红线：CITYSKYLINE 在「4 塔楼占 43% 地块」的工况下做到 0.00 m² 重叠，合并后不许退化。

---

## 6. 与现有代码的接法

```
PCG 侧（保留）                      新增                         Placemaker 侧（消费）
─────────────                      ────                         ──────────────────
LandUse + 密度/容积率 ──┐
                        ├─▶ zoneKindOf(parcel) ──▶ PlannedZoning
临街等级（roadClass） ───┘                                │
                                                          ├─▶ ZoneGrid（8 m 模数）
建筑预设表 ──▶ placeBuildings(zoningQuads) ──▶ Building ──┘
                        │
                        └─▶ buildingQuads() ──▶ Obstacle[]（仅 --keep 模式）
```

关键实现约定：

- `placeBuildings()` **必须**读 Placemaker 返回的 zoning quad，不能自己按地块边重新算一遍退线（那就是两套规则，迟早对不齐）
- 建筑对象保留 PCG 的 `fixed / generated` 区分与 `generatedFrom`，保证「换一版」只重算当前地块
- 高度由 PCG 的「容积率反解层数」算，但**层数下限被 zone kind 的区间夹住**（低密度商业不可能反解出 10 层）
