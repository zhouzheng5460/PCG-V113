// Placemaker → TownForge 的桥。
//
// Placemaker（C#，GPL-3.0）是一个**室外场地设计器**：它产出铺装面、园路、道具、车位、
// 分区框，但**不产建筑**——`ObjectRole` 里根本没有 Building，建筑在它那里只是"要避开的障碍物"。
// 所以这条桥接管的不是"整块地的内容"，而是「地块内容里的室外那一半」：
//
//   TownForge 负责            Placemaker 接管
//   ─────────────────         ──────────────────
//   建筑体块 / 屋顶 / 墙面  ←  保留
//   铺装 / 绿化 / 车位      ←  换成 128 个生成器里的任意一个
//
// 协议见 `PlacemakerPlan`：C# 端 `PlacemakerDriver --emit <case.json> <plan.json>` 产出。
//
// 三条硬约定，写在这里免得后面又踩：
//
//   1) **单位**。Plan 里一律是米（Placemaker 的 MinArea / CheckReady / minApart 全按米算，
//      换算必须在调用方做，否则"这块地能不能跑"和"这块地多大"会来自两套单位）。
//      进到这里统一除 `UNIT_M` 变成世界单位。
//
//   2) **坐标**。Plan 的平面坐标是 `[x, z]`，和 TownForge 的 `Pt(x, y)` 一一对应
//      （3D 里 Pt.y 就是 world z）。**不要翻转任何一个轴**——Placemaker 的 +z 和 TownForge 的
//      +y 都是"北"，翻一下就成镜像图，而且镜像后所有车位朝向都反了，很难一眼看出来。
//
//   3) **朝向**。`yaw` 弧度绕 +Y，0 = 朝 +Z。转成 TownForge 的 `rot`（2D 画布弧度，0 = +X）时
//      是 `rot = yaw - π/2`，因为 2D 里 0 度指向 +X、3D 里 0 度指向 +Z。
//
// 车位是**点**不是面（Placemaker 只给 `{x, z, yaw, scale}`），必须自己重塑成车位线，
// 见 `baysToMarkings()` —— 那一段是本模块唯一有算法的地方。

import { Pt } from '../core/geom';
import { UNIT_M } from '../road/spec';
import type { MatKey } from '../scene/palette';
import type { PathFeature, PropFeature, PropKind, SurfaceFeature } from '../scene/types';
import { toCCW } from '../scene/types';
import { fitHedgeBars, type HedgeBar } from './hedge';

// ─────────────────────────────────────────────────────────── Plan JSON 类型

/** Plan 里一个平面点：`[x, z]`，单位米。 */
export type PlanPoint = [number, number];

export type PlanSurfaceRole = 'ground' | 'paving' | 'asphalt' | 'grass' | 'plaza' | 'water' | 'decorative';

export type PlanNetRole =
  | 'street' | 'avenue' | 'culDeSac' | 'roundabout' | 'perimeterRoad'
  | 'pathway' | 'pathwayWide'
  | 'driveAisle' | 'driveConnector' | 'alley'
  | 'fence' | 'other';

export type PlanObjectRole =
  | 'tree' | 'bush' | 'planter'
  | 'bench' | 'lamp' | 'bin' | 'centerpiece' | 'furniture' | 'fencePiece' | 'decal'
  | 'parkingBay' | 'charger' | 'arrow'
  | 'marker' | 'animalSpawner' | 'accessPoint' | 'culDeSacCap' | 'roundaboutIsland'
  | 'other';

export interface PlanSurface {
  role: PlanSurfaceRole;
  /** 地面高度（米）。铺装基本都是 0。 */
  y?: number;
  ring: PlanPoint[];
  category?: string;
}

/** 一条渠：三次贝塞尔，直线时 b/c 就在 a→d 的三等分点上。 */
export interface PlanNet {
  role: PlanNetRole;
  a: PlanPoint;
  b: PlanPoint;
  c: PlanPoint;
  d: PlanPoint;
  /** >= 0 = 这一端显式接到既有道路（对应 NearbyNet.ExternalId）；-1 = 自由端。 */
  startAttach?: number;
  endAttach?: number;
  category?: string;
}

export interface PlanObject {
  role: PlanObjectRole;
  x: number;
  z: number;
  y?: number;
  /** 绕 +Y 的弧度，0 = 朝 +Z。 */
  yaw?: number;
  scale?: number;
  elevation?: number;
  roll?: number;
  pitch?: number;
  /**
   * 资产分类（`AssetCategories`），Driver 直接抄过来的字符串。
   *
   * **不能不看**：`furniture` 是通用角色 —— 700 件堆场杂物和公园里的一张野餐桌都是它；
   * `decal` 里既有 1.5 m 的球场白线，也有 68 m 的整场贴花。只有分类能区分。
   * Core 那边 `RoleCategories.ForObject` 对通用角色是**强制**要分类的，所以这个字段基本都在。
   */
  category?: string;
}

export interface PlanRegion {
  key: string;
  tags: string[];
  ring: PlanPoint[];
}

export interface PlanZoning {
  zoneId: string;
  /** 分区四边形：A/B/C/D 四个角。 */
  quad: [PlanPoint, PlanPoint, PlanPoint, PlanPoint];
}

export interface PlanPaint {
  layer: number;
  strength: number;
  ring: PlanPoint[];
}

export interface PlanIssue {
  severity: 'info' | 'warning' | 'error';
  key: string;
  args?: (string | number)[];
}

export interface PlacemakerPlan {
  protocol: string;
  generator: string;
  units: 'meters';
  seed: number;
  area: number;
  ready: boolean;
  readyKey?: string;
  markers?: { given: number; auto: number };
  elapsedMs?: number;
  settings?: Record<string, unknown>;
  error?: string;
  empty?: string;
  /**
   * Driver 统计的「角色名没映射上」的元素数（按通道分开）。
   * Core 加了新枚举值而 Driver/桥接没跟上时，这些元素会被静默丢掉——读到非零就该报警。
   */
  unknownRoles?: { surfaces?: number; nets?: number; objects?: number; areas?: number };
  surfaces: PlanSurface[];
  nets: PlanNet[];
  objects: PlanObject[];
  areas: { role: string; ring: PlanPoint[] }[];
  regions: PlanRegion[];
  zoning: PlanZoning[];
  paint: PlanPaint[];
  terraform: { kind: string; target: number; permanent: boolean; ring: PlanPoint[]; hs?: number[] }[];
  stats: { key: string; value: number; unit?: string }[];
  facts: Record<string, number>;
  issues: PlanIssue[];
}

// ─────────────────────────────────────────────────────────── 映射表

/** 铺装角色 → 材质。停车坪单独用 asphaltLot，比车行道略深一点就够，再深就读成"空洞"了。 */
const MAT_BY_SURFACE: Record<PlanSurfaceRole, MatKey> = {
  ground: 'ground',
  paving: 'paver',
  asphalt: 'asphaltLot',
  grass: 'grass',
  plaza: 'stonePave',
  water: 'water',
  decorative: 'gravel',
};

/** 铺装基准高度。**不按 role 分层，而是按数组顺序递增** —— Placemaker 是先铺大底再铺细节的，
 *  按顺序叠上去才能让"后铺的压在先铺的上面"这件事成立。role 之间靠这点微小高差不够，
 *  所以步长取 2e-4（39 个面一共抬 8e-3，仍在人行道 0.019 之下）。 */
const SURFACE_Y0 = 0.008;
const SURFACE_DY = 2e-4;

/** 车位线高度。和 TownForge 自己画的 lot 车位线一致（modern.ts 用 0.0305）。 */
const BAY_LINE_Y = 0.0305;

/** 渠角色 → [宽度(米), 材质, 竖起来的高度(0 = 平铺)]。 */
const NET_SPEC: Record<PlanNetRole, [number, MatKey, number]> = {
  street: [8, 'asphalt', 0],
  avenue: [14, 'asphalt', 0],
  culDeSac: [7, 'asphalt', 0],
  roundabout: [8, 'asphalt', 0],
  perimeterRoad: [6, 'asphalt', 0],
  pathway: [2, 'paver', 0],
  pathwayWide: [3.5, 'paver', 0],
  driveAisle: [5.5, 'asphalt', 0],
  driveConnector: [6, 'asphalt', 0],
  alley: [4, 'gravel', 0],
  fence: [0.18, 'fence', 1.4],
  other: [3, 'concrete', 0],
};

/** 对象角色 → TownForge 的 PropKind。null = 不画（贴花走标线通道，点标记没有几何意义）。 */
const PROP_BY_OBJECT: Record<PlanObjectRole, PropKind | null> = {
  tree: 'treeRound',
  // 灌木必须是 `shrub`（1.3 m 高的一丛），不能是 `treeSmall`（4.3 m 的小树）：
  // Placemaker 的 bush 在花园里是几百个排成行当绿篱用的，映射成小树就成了"一排松树苗"。
  bush: 'shrub',
  planter: 'planter',
  bench: 'bench',
  lamp: 'lamp',
  bin: 'bin',
  centerpiece: 'fountain',
  // `furniture` 是通用角色，真正的造型在下面的分类表里；这里只是"分类也没写"时的兜底。
  // 兜底选盒子而不是长椅：长椅是最容易被误读的一种（一堆工业废料变成一排长椅），
  // 一个灰盒子至少读成"一垛东西"，错得没那么显眼。
  furniture: 'crate',
  fencePiece: 'bollard',
  decal: null,            // 走 markings 通道（见 DECAL_MARKING）
  parkingBay: null,       // 单独走 baysToMarkings
  charger: 'bollard',
  arrow: null,
  marker: null,
  animalSpawner: null,
  accessPoint: null,
  culDeSacCap: null,
  roundaboutIsland: null,
  other: null,
};

/**
 * 通用角色 `furniture` 的资产分类 → 道具。
 *
 * 为什么必须做这张表：Placemaker 把"任何带资产的杂项"都叫 `furniture`，
 * 20 个已烘方案里它出现了 25 种分类、2500 多件。原来一律映射成长椅，于是
 * 自然保护区长出 314 张长椅（那是 `rock` 石块）、车场里 809 张（那是护栏和废车）。
 *
 * 键就是 Core 的 `AssetCategories` 字符串。缺席的分类走兜底 `crate` ——
 * 新分类出来时画面会多一个灰盒子，而不是多一排长椅，也不会静默消失。
 */
const FURNITURE_BY_CATEGORY: Record<string, PropKind> = {
  // —— 绿地 / 公园 / 营地
  picnic: 'bench',
  'seating.cafe': 'bench',
  bench: 'bench',
  bin: 'bin',
  'dog.amenity': 'bin',
  'garden.crop': 'planter',      // 菜畦/花圃 = 抬起来的一格土，正是 planter
  flowerbed: 'planter',
  'garden.shed': 'shelter',
  'garden.glasshouse': 'shelter',
  shelter: 'shelter',
  kiosk: 'shelter',
  'kiosk.stall': 'shelter',
  stage: 'shelter',
  'camping.pitch': 'shelter',    // 帐篷：读成一个小棚子，比一张长椅接近
  'camping.fire': 'rock',        // 火塘：一圈石头
  'sign.noticeboard': 'sign',
  'trail.sign': 'sign',
  'transport.bikeRack': 'bollard',
  'sports.goal': 'bollard',      // 球门：只有两根柱子是认得出的，横梁留给以后
  'sports.stand': 'crate',       // 看台：一个台块
  'dog.agility': 'crate',
  // —— 堆场 / 工业 / 施工
  'industrial.wreck': 'car',     // 报废车：TownForge 本来就有 `car`
  'industrial.barrel': 'barrel',
  'industrial.tire': 'barrel',   // 轮胎堆：矮圆柱
  'industrial.container': 'crate',
  'industrial.pallet': 'crate',
  'industrial.skip': 'crate',
  'industrial.scrap': 'crate',
  'industrial.garbage': 'crate',
  'industrial.bale': 'crate',
  'industrial.barrier': 'crate', // 水泥隔离墩：矮方盒
  'industrial.machine': 'crate',
  'industrial.cabin': 'crate',
  'industrial.pile': 'rock',
  'industrial.vent': 'bollard',
  'construction.rubble': 'rock',
  'construction.material': 'crate',
  'construction.toilet': 'crate',
  'construction.generator': 'crate',
  rock: 'rock',
  // —— 废弃地块（`abandonedLot`）
  // 这一族的"身份"全在 ruins.* 上：112 丛杂草 + 30 堆瓦砾 + 7 段残墙 + 9 处涂鸦。
  // 残墙原来落进兜底 `crate`，一块废墟就变成"一堆 1.1 m 的方盒子"，读不出这儿曾有楼。
  'ruins.wall': 'ruinWall',
  'ruins.rubble': 'rock',
  'ruins.junk': 'crate',
  // —— 广场 / 街道家具
  // `bollard` 直接就是路桩，别再落兜底：市场广场有 162 个，排成列是那个广场的秩序所在。
  bollard: 'bollard',
  'commercial.sign': 'sign',
  'lamp.flood': 'lamp',
  restroom: 'shelter',
};

/**
 * 中心物 `centerpiece` 的资产分类 → 道具。
 *
 * 一律画成喷泉的代价：市场广场正中间那尊 `centerpiece.statue` 变成**蓝色水盘**。
 * 中心物是一块地的视觉焦点，错了比旁边几十棵树错了更刺眼。
 */
const CENTERPIECE_BY_CATEGORY: Record<string, PropKind> = {
  'centerpiece.fountain': 'fountain',
  'centerpiece.statue': 'statue',
  /** 没写分类（只有 `centerpiece`）时按喷泉画：这是该角色最常见的一档。 */
  centerpiece: 'fountain',
};

/**
 * 贴花分类 → 标线尺寸（米）。
 *
 * 贴花在 Plan 里只有"点位 + 朝向"，尺寸在资产网格里、离线拿不到。所以只能按分类给：
 * - `decal.fieldline`：球场白线。Core 沿线**每隔 `lineSpacing`（默认 1.5 m）放一件**，
 *   所以按一小段窄条画。段长取 1.7 m 而不是 1.5 m：实测相邻两件正好相距 1.50 m，
 *   按 1.5 画就是"首尾对接"，接缝在细线上会看成一串虚线（渲染器按像素光栅化，
 *   对接处两边都只覆盖半个像素）。多出的 0.2 m 让它们轻微重叠 —— 同色同高，看不出叠。
 * - `decal.markings`：整块半场贴花（68 m × 52.5 m 的足球半场纹理）。**不能画成实心面**
 *   （贴花自带透明，铺实心就是一块白板），只画外框：两半拼起来正好得到球场轮廓 + 中线。
 * - 其余分类（落叶 / 裂纹 / 水渍 / 垃圾）是地形贴图性质，没有几何可言，不画。
 */
const DECAL_MARKING: Record<string, { len: number; w: number }> = {
  'decal.fieldline': { len: 1.7, w: 0.14 },
  'parking.decal': { len: 1.7, w: 0.14 },
  'decal.markings': { len: 68, w: 52.5 },
};

// ─────────────────────────────────────────────────────────── 选项与结果

export interface BridgeOptions {
  /** 地块左下角在世界里的位置（世界单位）。Plan 的 (0,0) 会落在这里。 */
  origin: Pt;
  /** 额外缩放，默认 1（= 严格按 UNIT_M 换算）。只在需要微调地块大小时用。 */
  scale?: number;
  /** 导出分组前缀，默认 'pm'。同一块地多个生成器时用不同前缀，DCC 里能分选中。 */
  group?: string;
  /** 是否画车位线，默认 true。 */
  bayLines?: boolean;
  /**
   * 是否把成排的灌木拟合成绿篱，默认 true。
   *
   * Placemaker 排绿篱时整条线共用一个 yaw，随手撒的灌木每颗 yaw 都不同，所以这个
   * 判断只看 yaw、不猜距离（细节见 `bridge/hedge.ts` 的头注释）。关掉就退回"一颗一颗小锥"。
   */
  hedges?: boolean;
  /** 是否画 net（通道、园路、围栏），默认 true。 */
  nets?: boolean;
  /** 是否画 terraform 抬起的台地（按顶点高度出成有体积的台体）。默认 true。 */
  terraform?: boolean;
  /**
   * 多矮的台地直接丢掉（米），默认 0.15。
   *
   * 堆场里一片料堆的羽化格子从 0.2 m 起，低于 0.15 m 的格子抬起来看不见，
   * 白占三角形。设 0 就是一块不丢。
   */
  minMoundHeight?: number;
  /** 最多画多少条车位分隔线（护栏，防止一个巨型停车场把面数打爆）。默认 4000。 */
  maxBayLines?: number;
  /**
   * 是否画标线类贴花（球场白线、整场贴花外框），默认 true。
   * 关掉就是老行为：球场上只剩草皮和球门，一条线都没有。
   */
  markings?: boolean;
  /** 标线贴花最多画多少条（护栏），默认 6000。 */
  maxMarkings?: number;
  /**
   * 是否把 `zoning` 宗地铺成砾石地面（默认 true）。
   *
   * 宗地 = 留给建筑的地，按定义不铺装。不画的话，`stripMall` 这种生成器在场景里
   * 只剩 30% 的地面有内容，剩下 27% 是一块没来由的草坪 —— 看起来像"规划失败"，
   * 实际是"那块地等着盖楼"。关掉就回到老行为。
   */
  zoning?: boolean;
  /**
   * 地块环（米，Plan 坐标系，同 `caseJson.shape.ring`）。
   *
   * 给了就在最底层铺一层草地。**这不是可选的装饰**：Placemaker 里地面是地形，
   * 生成器只管"往上画"——`park` / `greenwayPark` / `picnicGrounds` / `natureReserve`
   * 的 `surfaces` 干脆是空的，只出园路和树。不补底的话，桥接结果是一堆树漂在裸地上。
   *
   * 环从哪儿来：烘焙时写进了 meta 的 `lotRing`（见 tools/bake-plan.ts）。
   */
  lotRing?: PlanPoint[];
  /** 底层草地的材质，默认 `grass`。 */
  groundMat?: MatKey;
  /** 树里换成针叶的比例（0~1），默认 1/3。Placemaker 不区分树种，靠这个制造疏密变化。 */
  coniferRatio?: number;
}

export interface BridgeResult {
  surfaces: SurfaceFeature[];
  props: PropFeature[];
  paths: PathFeature[];
  /**
   * 抬起来的台地 / 料堆（`terraform` 里的 raise 环）。
   *
   * 为什么不是 `surfaces`：这些环**逐顶点带高度**，是一块有体积的台体，
   * 不是一片贴地的面。以前当面无脑平铺，255 个环按 0.2 mm 步长往上叠 ——
   * 那是一片互相穿插的悬空薄板，深度缓冲上是必然的 z-fighting。
   */
  mounds: MoundFeature[];
  /** 被丢弃的面（自交、退化、点数不足）。非 0 说明 Plan 几何有问题，值得看一眼。 */
  dropped: number;
  /** 车位重塑的统计，给控制台/测试断言用。 */
  bays: { total: number; rows: number; lines: number };
  /**
   * 贴花 → 标线的统计：
   * `strips` 白线条数、`frames` 整场贴花外框个数、`skipped` 非标线贴花（落叶/裂纹/水渍）个数。
   */
  markings: { strips: number; frames: number; skipped: number };
  /**
   * 台地的统计：
   * `raised` 出成台体的环数、`lowered` 落成水面的下挖环数、
   * `skipped` 太矮（< 0.15 m，看不见）或几何退化的环数、`maxH` 最高台地（米）。
   */
  terrain: { raised: number; lowered: number; skipped: number; maxH: number };
  /** 本次桥接用到的生成器 id 与种子，便于复现。 */
  meta: { generator: string; seed: number; area: number; ready: boolean };
}

/** 一块抬起来的台地：`ring` 与 `hs` 一一对应，`hs` 是顶面每个顶点的高度（世界单位）。 */
export interface MoundFeature {
  name: string;
  group: string;
  ring: Pt[];
  hs: number[];
  mat: MatKey;
}

// ─────────────────────────────────────────────────────────── 主函数

/**
 * 角色名对不上时的报警。**只有真丢了东西才说话**，正常路径一句多余输出都没有。
 *
 * 两个来源：
 * - `plan.unknownRoles`：Driver 侧 RoleName 落到兜底 "other" 的计数（Core 新枚举值）。
 * - `extra`：桥接映射表里根本没有的 role（JSON 比代码新）。
 */
function warnUnknownRoles(plan: PlacemakerPlan, extra: Map<string, number>): void {
  const u = plan.unknownRoles;
  const nSurf = u?.surfaces ?? 0;
  const nNet = u?.nets ?? 0;
  const nObj = u?.objects ?? 0;
  const nArea = u?.areas ?? 0;
  const fromDriver = nSurf + nNet + nObj + nArea;
  if (fromDriver > 0) {
    console.warn(
      `[bridge] ${plan.generator}: Driver 报告 ${fromDriver} 个角色名未映射的元素` +
        `（surf ${nSurf} / net ${nNet} / obj ${nObj} / area ${nArea}），它们不会出现在场景里。` +
        `若 Core 新增了角色，需同步 Emit.cs 的 RoleName 与本文件的映射表。`,
    );
  }
  if (extra.size > 0) {
    const list = [...extra.entries()].map(([r, n]) => `${r}×${n}`).join('、');
    console.warn(`[bridge] ${plan.generator}: plan.objects 里有映射表不认识的角色（${list}），已丢弃。`);
  }
}

/**
 * 地面底色：给 `paint` 通道一个最小的落地方式。
 *
 * `paint` 是 CS2 的"刷地表材质"：1 干草 / 2 林地 / 3 泥土 / 4 沙，`strength` 是覆盖强度。
 * 它不是几何，是一张 splat 权重图 —— 照原样画成实心面会得到一块纯色板，
 * 而且 200 多块叠在一起必然 z-fighting。所以这里只取**一条**信息：
 * 「整块地大面积的底色是什么」，用它决定最底层地面（本来固定是草地）的材质。
 *
 * 收益很直接：`carYard` / `junkYard` 的铺装只占 21%，剩下 79% 原来是一片草坪 ——
 * 而 Core 明确把这 79% 刷成了泥土（layer 3 @ 0.85，覆盖整个地块）。改成土地之后
 * 才读得出"这是一个堆场"，而且**一个面都不多**。
 */
function groundMatFromPaint(plan: PlacemakerPlan): MatKey | null {
  const total = plan.area > 0 ? plan.area : 0;
  if (total <= 0 || plan.paint.length === 0) return null;
  let best: { layer: number; area: number } | null = null;
  for (const q of plan.paint) {
    if ((q.strength ?? 1) < 0.8) continue;            // 轻轻刷一层的是细节，不改底色
    const a = Math.abs(polyArea(q.ring));
    if (a < total * 0.2) continue;                    // 小于地块 20% 的算补丁
    if (!best || a > best.area) best = { layer: q.layer, area: a };
  }
  if (!best) return null;
  switch (best.layer) {
    case 3: return 'soil';        // 泥土
    case 4: return 'gravel';      // 沙
    case 2: return 'grassDark';   // 林地地表（比草地深一档）
    case 1: return 'grassDark';   // 干草：偏灰绿，用深草近似
    default: return null;         // 0 = 擦除
  }
}

function polyArea(ring: PlanPoint[]): number {
  let s = 0;
  for (let i = 0; i < ring.length; i++) {
    const a = ring[i], b = ring[(i + 1) % ring.length];
    s += a[0] * b[1] - b[0] * a[1];
  }
  return s / 2;
}

/**
 * 顶点高度只在**顶点之间真有高差**时才用，否则整块按 `target` 抬平。
 *
 * 实测（这一条踩过）：堆场/废料场的 terraform 环顶点高度**全是 0**，高度在
 * `target` 里 —— 那是"这个格子整体抬到 h"，`Ring[i].y` 根本没被填。只有
 * AiTerrain 的羽化路径才会写非零顶点高度。
 *
 * 所以判据是**顶点之间的极差**，不是"和 target 差多少"：拿后者去判，全零的环
 * 会被当成"坡度 0 m、target 2.6 m"的斜坡，画出来高度是 0 —— 料堆原地消失。
 */
function slopedHs(hs: number[] | undefined, target: number): number[] | undefined {
  if (!hs || hs.length === 0) return undefined;
  let lo = Infinity;
  let hi = -Infinity;
  for (const h of hs) {
    if (h < lo) lo = h;
    if (h > hi) hi = h;
  }
  return hi - lo > 0.05 ? hs : undefined;
}

/** 射线法：点是否在环内（与 `clip.ts` 的 `inside` 同源，这里收 `Pt`）。 */
function pointInRing(p: Pt, ring: Pt[]): boolean {
  let hit = false;
  for (let i = 0, j = ring.length - 1; i < ring.length; j = i++) {
    const a = ring[i], b = ring[j];
    if ((a.y > p.y) !== (b.y > p.y) && p.x < ((b.x - a.x) * (p.y - a.y)) / (b.y - a.y) + a.x) hit = !hit;
  }
  return hit;
}

export function bridgePlan(plan: PlacemakerPlan, opts: BridgeOptions): BridgeResult {
  const sc = opts.scale ?? 1;
  const g = opts.group ?? 'pm';
  const k = (1 / UNIT_M) * sc;

  const toPt = (p: PlanPoint): Pt => new Pt(p[0] * k + opts.origin.x, p[1] * k + opts.origin.y);

  const surfaces: SurfaceFeature[] = [];
  const props: PropFeature[] = [];
  const paths: PathFeature[] = [];
  const mounds: MoundFeature[] = [];
  const terrain = { raised: 0, lowered: 0, skipped: 0, maxH: 0 };
  let dropped = 0;
  /** plan.objects 里出现、但映射表不认识的 role → 个数。收完在结尾统一报一次。 */
  const unknownObjRoles = new Map<string, number>();

  /** 折算出世界单位的环；点数不足或退化返回 null。 */
  const ringOf = (pts: PlanPoint[] | undefined): Pt[] | null => {
    if (!pts || pts.length < 3) return null;
    const out: Pt[] = [];
    for (const p of pts) {
      const q = toPt(p);
      const last = out[out.length - 1];
      // 换算 + 缩放之后可能产生重合点（1e-6 级别），留着会让 earcut 出零宽裂缝
      if (last && Math.abs(last.x - q.x) < 1e-6 && Math.abs(last.y - q.y) < 1e-6) continue;
      out.push(q);
    }
    if (out.length >= 3) {
      const f = out[0], l = out[out.length - 1];
      if (Math.abs(f.x - l.x) < 1e-6 && Math.abs(f.y - l.y) < 1e-6) out.pop();
    }
    if (out.length < 3) return null;
    if (!simpleRing(out)) return null;
    return toCCW(out);
  };

  /**
   * 带顶点高度的环。`ringOf` 会去重合点、必要时反转绕序，`hs` 必须**跟着做同样的操作**。
   * 一旦错位，顶点高度就落到别的角上 —— 一块 4 m 见方的料堆会拧成麻花。
   * 高度单位沿用调用方给的口径（米），换算在外面做。
   */
  const ringOfH = (
    pts: PlanPoint[] | undefined,
    hs: number[] | undefined,
    fallback: number,
  ): { ring: Pt[]; hs: number[] } | null => {
    if (!pts || pts.length < 3) return null;
    const pick = (i: number): number => {
      const v = hs?.[i];
      if (Number.isFinite(v)) return v as number;
      const v0 = hs?.[0];
      return Number.isFinite(v0) ? (v0 as number) : fallback;
    };
    const ring: Pt[] = [];
    const hh: number[] = [];
    for (let i = 0; i < pts.length; i++) {
      const q = toPt(pts[i]);
      const last = ring[ring.length - 1];
      if (last && Math.abs(last.x - q.x) < 1e-6 && Math.abs(last.y - q.y) < 1e-6) continue;
      ring.push(q);
      hh.push(pick(i));
    }
    if (ring.length >= 3) {
      const f = ring[0], l = ring[ring.length - 1];
      if (Math.abs(f.x - l.x) < 1e-6 && Math.abs(f.y - l.y) < 1e-6) { ring.pop(); hh.pop(); }
    }
    if (ring.length < 3) return null;
    let s = 0;
    for (let i = 0; i < ring.length; i++) {
      const a = ring[i], b = ring[(i + 1) % ring.length];
      s += a.x * b.y - b.x * a.y;
    }
    // toCCW 会反转；这里不能用它，否则 hs 留在原地、高度错位
    if (s < 0) { ring.reverse(); hh.reverse(); }
    // 料堆的环常带共线点（网格切出来的），simpleRing 会拒；只挡自交，共线交给 earcut
    return { ring, hs: hh };
  };

  // 0) 草地底：铺在最下面一层，比所有铺装低一档。
  //    为什么需要：Placemaker 管"往上画"，地面是地形。绿地类生成器大多不铺地面，
  //    不补底出来就是裸地上一堆树。铺装类（parking / formalGarden）自己铺满整块地，
  //    底下的草被完全盖住，代价只是多一个和地块同形的多边形。
  if (opts.lotRing) {
    const ring = ringOf(opts.lotRing);
    if (ring) {
      surfaces.push({
        name: `${g}_ground`,
        group: `${g}/surface/ground`,
        ring,
        mat: opts.groundMat ?? groundMatFromPaint(plan) ?? 'grass',
        y: SURFACE_Y0 - SURFACE_DY,
      });
    } else {
      dropped++;
    }
  }

  // 1) 铺装面：按数组顺序叠高度
  plan.surfaces.forEach((s, i) => {
    const ring = ringOf(s.ring);
    if (!ring) { dropped++; return; }
    surfaces.push({
      name: `${g}_surf_${i}_${s.role}`,
      group: `${g}/surface/${s.role}`,
      ring,
      mat: MAT_BY_SURFACE[s.role] ?? 'ground',
      y: SURFACE_Y0 + i * SURFACE_DY,
    });
  });

  // 1b) 宗地（zoning）：地是留给建筑的，按定义**不铺装**
  //
  // 这条通道以前整类丢掉，代价是"覆盖率看着很低"：stripMall 只铺了 30%，
  // 剩下 27%（3341 m²）是 `EU Commercial Low` 宗地 —— 在 CS2 里那块地是用来长商铺的，
  // 地面本来就不铺。丢掉之后整块地读成「草坪 + 角落里一个停车场」，
  // 而真相是「停车场 + 一排待建商铺的空地」。
  //
  // 处理方式：把宗地四边形铺一层砾石，**不铺装材质**。砾石在这里的意思很明确 ——
  // 场地平整过、等着盖，既不像草坪（以为没规划过）也不像广场（以为已经能用）。
  // 分组带 zoneId，2D 出图和 DCC 里都能按分区单独选中/统计。
  // 高度压在铺装之上（core 不会让宗地压在步道上），下面 terraform 顺延。
  if (opts.zoning !== false) {
    plan.zoning.forEach((z, i) => {
      const ring = ringOf(z.quad);
      if (!ring) { dropped++; return; }
      const zone = z.zoneId.replace(/[^\w]+/g, '_');
      surfaces.push({
        name: `${g}_zone_${i}_${zone}`,
        group: `${g}/zoning/${zone}`,
        ring,
        mat: 'gravel',
        y: SURFACE_Y0 + (plan.surfaces.length + i) * SURFACE_DY,
      });
    });
  }

  // 2) terraform：抬起来的台地/料堆 + 挖下去的水面
  //
  // 这条通道以前整类当"贴地薄板"处理：每个环铺一层土色面，按数组序号抬 0.2 mm。
  // 三个后果，最后一个是致命的：
  //   1. 2.6 m 高的垃圾堆和 0.3 m 高的格子长得一模一样（都只是一块平地）；
  //   2. 堆在地面上的 65 块薄板只差 0.2 mm —— 深度缓冲上是必然的 z-fighting 条纹；
  //   3. 摆在这些堆上的箱子/油桶仍站在 y≈0，堆一抬高就全埋进土里。
  //
  // 现在的口径：`raise` 出成**有体积的台体**（顶面逐顶点高度 + 裙边落回地面），
  // `lower` 只补一层水面（TownForge 的地面是一整块平面，没有"挖坑"这回事 ——
  // 减出来的高度只能靠水面表达，园池的水本来也在 `surfaces.water` 里）。
  if (opts.terraform !== false) {
    const minHm = opts.minMoundHeight ?? 0.15;
    // 有真水面（`surfaces.water`）的生成器不用再补那层下挖水面：它压在
    // 水面**之下**，永远是块看不见的几何。园池就是这种（水面已经铺好）。
    const hasWater = plan.surfaces.some((s) => s.role === 'water');
    plan.terraform.forEach((t, i) => {
      if (t.kind === 'level') return;
      if (t.kind === 'lower') {
        const ring = ringOf(t.ring);
        if (!ring) { dropped++; terrain.skipped++; return; }
        if (hasWater) return;
        surfaces.push({
          name: `${g}_pond_${i}`,
          group: `${g}/terraform/lower`,
          ring,
          mat: 'water',
          // 水面比铺装低一档：它是一块被挖出来的水面，不该压在人行铺装之上。
          y: SURFACE_Y0 - SURFACE_DY * 2,
        });
        terrain.lowered++;
        return;
      }
      const m = ringOfH(t.ring, slopedHs(t.hs, t.target), t.target);
      if (!m) { dropped++; terrain.skipped++; return; }
      const hsW = m.hs.map((h) => h * k);
      const maxH = Math.max(...hsW);
      const maxHm = maxH / k;   // 换回米，只用于门槛判断和统计
      if (maxHm < minHm) { terrain.skipped++; return; }
      mounds.push({
        name: `${g}_mound_${i}`,
        group: `${g}/terraform/raise`,
        ring: m.ring,
        hs: hsW,
        mat: 'soil',
      });
      terrain.raised++;
      if (maxHm > terrain.maxH) terrain.maxH = maxHm;
    });
  }

  // 台地高度查询：摆在料堆上的箱子/油桶要跟着抬，否则堆一抬高它们就埋进土里。
  // Core 只在少数对象上给了 `y`（大部分没有），所以这里自己按环查。
  const moundBoxes = mounds.map((m) => {
    let x0 = Infinity, y0 = Infinity, x1 = -Infinity, y1 = -Infinity;
    for (const v of m.ring) {
      if (v.x < x0) x0 = v.x;
      if (v.y < y0) y0 = v.y;
      if (v.x > x1) x1 = v.x;
      if (v.y > y1) y1 = v.y;
    }
    let peak = 0;
    for (const h of m.hs) if (h > peak) peak = h;
    return { m, x0, y0, x1, y1, peak };
  });
  /**
   * 道具脚下的台地高度：在**中心 + 半径 0.5 m 的四个点**上取最高的那格。
   *
   * 只查中心的话，一件压在格子边界上的东西会拿到低的那格，然后被邻居的
   * 2 m 墙面从中间劈开；取最高只会让它略微悬空 —— 悬空看得见，劈开看不见。
   */
  const moundTopAt = (p: Pt, rad: number): number => {
    let best = -Infinity;
    const probes = rad > 0
      ? [p, new Pt(p.x + rad, p.y), new Pt(p.x - rad, p.y), new Pt(p.x, p.y + rad), new Pt(p.x, p.y - rad)]
      : [p];
    for (const q of probes) {
      for (const b of moundBoxes) {
        if (q.x < b.x0 || q.x > b.x1 || q.y < b.y0 || q.y > b.y1) continue;
        if (!pointInRing(q, b.m.ring)) continue;
        // 取环的最高点而不是插值：宁可让道具在斜坡上微微悬空，
        // 也绝不让它陷进堆里 —— 陷进去就是彻底看不见。
        if (b.peak > best) best = b.peak;
      }
    }
    return best === -Infinity ? 0 : best;
  };

  // 3) 渠：贝塞尔 → 折线
  if (opts.nets !== false) {
    plan.nets.forEach((n, i) => {
      const spec = NET_SPEC[n.role] ?? NET_SPEC.other;
      const a = toPt(n.a), b = toPt(n.b), c = toPt(n.c), d = toPt(n.d);
      const pts = sampleBezier(a, b, c, d);
      if (pts.length < 2) { dropped++; return; }
      paths.push({
        name: `${g}_net_${i}_${n.role}`,
        group: `${g}/net/${n.role}`,
        pts,
        width: (spec[0] / UNIT_M) * sc,
        mat: spec[1],
        y: 0.014,
        ...(spec[2] > 0 ? { height: (spec[2] / UNIT_M) * sc } : {}),
      });
    });
  }

  // 4) 道具
  //
  // 树要混：Placemaker 的 `tree` 不区分树种（只有一个 scale），一律按阔叶树画会丢掉
  // TownForge 自己的疏密变化，一律按针叶画就成了苗圃。按**树的序号**每 n 棵换一棵针叶
  // （不用对象下标，因为对象数组里树可能被别的角色隔开）。
  let treeSeen = 0;
  const coniferEvery = Math.max(2, Math.round(1 / Math.max(0.05, Math.min(1, opts.coniferRatio ?? 1 / 3))));

  // 先算绿篱：成排的灌木不画成一颗颗小锥，而是连成一段（判断依据见 bridge/hedge.ts）
  const bushIdx = plan.objects.map((o, i) => (o.role === 'bush' ? i : -1)).filter((i) => i >= 0);
  const hedge = opts.hedges === false
    ? { bars: [] as HedgeBar[], inRun: bushIdx.map(() => false) }
    : fitHedgeBars(bushIdx.map((i) => plan.objects[i]));
  const absorbed = new Set<number>();
  bushIdx.forEach((objIdx, n) => { if (hedge.inRun[n]) absorbed.add(objIdx); });

  hedge.bars.forEach((bar, i) => {
    const a = toPt(bar.a), b = toPt(bar.b);
    const len = Math.hypot(b.x - a.x, b.y - a.y);
    if (len < 1e-4) return;
    props.push({
      name: `${g}_hedge_${i}`,
      group: `${g}/prop/hedge`,
      kind: 'hedge',
      at: new Pt((a.x + b.x) / 2, (a.y + b.y) / 2),
      rot: Math.atan2(b.y - a.y, b.x - a.x),
      scale: 1,
      seed: i,
      len,
    });
  });

  plan.objects.forEach((o, i) => {
    if (absorbed.has(i)) return;
    // 映射表里根本没有这个 role —— 不是"故意不画"（那种是映射成 null），
    // 而是数据比代码新（Core 加了角色，桥接没跟上）。别静默吞掉，记下来结尾报。
    if (!(o.role in PROP_BY_OBJECT)) {
      unknownObjRoles.set(o.role, (unknownObjRoles.get(o.role) ?? 0) + 1);
      return;
    }
    let kind = PROP_BY_OBJECT[o.role];
    if (o.role === 'furniture') kind = FURNITURE_BY_CATEGORY[o.category ?? ''] ?? 'crate';
    else if (o.role === 'centerpiece') kind = CENTERPIECE_BY_CATEGORY[o.category ?? ''] ?? 'fountain';
    if (!kind) return;
    if (o.role === 'tree') {
      treeSeen++;
      if (treeSeen % coniferEvery === 0) kind = 'conifer';
    }
    const p = toPt([o.x, o.z]);
    // 站在台地上的道具：底高跟着堆顶抬。
    //
    // 两个来源，取大的那个：
    // - `o.y`：Core **自己给的**高度（`scrapyard` 194/509、`carYard` 96/809 带这个字段，
    //   数值与 terraform 的 target 一一对得上，比如 2.6、4.0）。这是权威值。
    // - `moundTopAt`：Core 没给的那些（占了多数），按台地环自己查。
    //
    // 取大：宁可略微悬空，也绝不让它埋进土里 —— 埋进去就是彻底看不见。
    // `+0.002` 是为了不跟台体顶面共面（z-fighting）。
    const authoredM = opts.terraform === false ? 0 : (o.y ?? 0);
    const fromMoundM = moundTopAt(p, 0.5 * k) / k;
    const baseM = Math.max(authoredM, fromMoundM);
    const baseY = baseM > 0.005 ? baseM * k + 0.002 : 0;
    props.push({
      name: `${g}_obj_${i}_${o.role}`,
      group: `${g}/prop/${kind}`,
      kind,
      at: p,
      rot: yawToRot(o.yaw ?? 0),
      scale: o.scale ?? 1,
      seed: i,
      ...(baseY > 1e-6 ? { baseY: baseY + 0.002 } : {}),
    });
  });

  // 5) 贴花 → 标线
  //
  // Placemaker 的标线不是"线"，是沿线撒的一串贴花（间距 `lineSpacing`，默认 1.5 m）。
  // 不处理的话，一块足球场在场景里只有草皮和四个灯杆 —— 线全没了，读不出是什么场地。
  // 这里按分类把点位重新摊成窄条（尺寸口径见 DECAL_MARKING 的头注释）。
  const markings = { strips: 0, frames: 0, skipped: 0 };
  const markCap = opts.maxMarkings ?? 6000;
  if (opts.markings !== false) {
    /** 一条标线：中点到中点，宽 0.14 m，和车位线同高同材质。 */
    const pushStrip = (a: Pt, b: Pt): boolean => {
      const ring = bayLineRing(a, b, (0.14 / UNIT_M) * sc / 2);
      if (!ring) { dropped++; return false; }
      surfaces.push({ name: `${g}_mark_${surfaces.length}`, group: `${g}/marking/field`, ring, mat: 'markWhite', y: BAY_LINE_Y });
      return true;
    };
    const ext = (c: Pt, ux: number, uy: number, l: number, w: number) => new Pt(c.x + ux * l + uy * w, c.y + uy * l - ux * w);

    for (const o of plan.objects) {
      if (o.role !== 'decal') continue;
      const spec = DECAL_MARKING[o.category ?? ''];
      if (!spec) { markings.skipped++; continue; }
      if (markings.strips + markings.frames * 4 >= markCap) { markings.skipped++; continue; }

      const c = toPt([o.x, o.z]);
      const rot = yawToRot(o.yaw ?? 0);
      const ux = Math.cos(rot), uy = Math.sin(rot);
      const hl = (spec.len / UNIT_M) * sc / 2;   // 沿 rot 的半长
      const hw = (spec.w / UNIT_M) * sc / 2;     // 垂直 rot 的半宽

      if (spec.w > 1) {
        // 整块贴花：只画外框（4 条）。贴花纹理自带透明，铺成实心面就是一块白板。
        pushStrip(ext(c, ux, uy, -hl, hw), ext(c, ux, uy, hl, hw));
        pushStrip(ext(c, ux, uy, -hl, -hw), ext(c, ux, uy, hl, -hw));
        pushStrip(ext(c, ux, uy, -hl, -hw), ext(c, ux, uy, -hl, hw));
        pushStrip(ext(c, ux, uy, hl, -hw), ext(c, ux, uy, hl, hw));
        markings.frames++;                        // `frames` 是贴花件数，不是条数
      } else if (pushStrip(ext(c, ux, uy, -hl, 0), ext(c, ux, uy, hl, 0))) {
        markings.strips++;                        // `strips` 只数白线条数
      }
    }
  }

  // 6) 车位：点 → 分隔线
  const bayRows = opts.bayLines === false ? [] : baysToMarkings(plan.objects, { k, origin: opts.origin });
  const lines = bayRows.reduce((s, r) => s + r.lines.length + r.fronts.length, 0);
  const cap = opts.maxBayLines ?? 4000;
  if (lines <= cap) {
    let n = 0;
    for (const row of bayRows) {
      for (const [p0, p1] of row.lines) {
        const ring = bayLineRing(p0, p1, 0.12 / UNIT_M);
        if (!ring) { dropped++; continue; }
        surfaces.push({
          name: `${g}_bay_${n++}`,
          group: `${g}/marking/bay`,
          ring,
          mat: 'markWhite',
          y: BAY_LINE_Y,
        });
      }
      // 每排两侧的前缘线：让车位读成"一整排"，而不是一根根孤立的针
      for (const [f0, f1] of row.fronts) {
        const ring = bayLineRing(f0, f1, 0.12 / UNIT_M);
        if (ring) {
          surfaces.push({
            name: `${g}_bayfront_${n++}`,
            group: `${g}/marking/bay`,
            ring,
            mat: 'markWhite',
            y: BAY_LINE_Y,
          });
        }
      }
    }
  } else {
    // 超过护栏就整批不画：宁可没有车位线，也不要一座 5000 面的"线城"
    dropped += lines;
  }

  warnUnknownRoles(plan, unknownObjRoles);

  return {
    surfaces,
    props,
    paths,
    mounds,
    dropped,
    bays: { total: countBays(plan.objects), rows: bayRows.length, lines: Math.min(lines, cap) },
    markings,
    terrain,
    meta: {
      generator: plan.generator,
      seed: plan.seed,
      area: plan.area,
      ready: plan.ready,
    },
  };
}

// ─────────────────────────────────────────────────────────── 车位重塑

interface BayRow {
  /** 排内所有车位的中心（世界坐标） */
  centers: Pt[];
  /** 分隔线：相邻车位中点，垂直于排列方向，长度 = 车位进深 */
  lines: [Pt, Pt][];
  /** 前缘线：排的两侧各一条，沿排列方向。两端必须**同侧**，
   *  写成 (lo, fixed-½深) → (hi, fixed+½深) 会得到一条斜穿整排的对角线。 */
  fronts: [Pt, Pt][];
}

/**
 * 把 `parkingBay` 点重塑成车位线。
 *
 * 为什么必须重塑：Placemaker 的车位是**点**（CS2 里车位是个 zone cell，不是对象），
 * 只有 `{x, z, yaw, scale}`。而我们要的是画在地上的线。直接每个点画一个矩形会得到
 * 313 个互相重叠的方块；画成线才对。
 *
 * 做法（利用了实测到的两个性质，见 `tmp` 里的探查）：
 *   - 车位朝向只有 4 个值（0°/90°/180°/270°），所以按 yaw 分 4 组就够；
 *   - 同一组里，垂直于排列方向的坐标只有少数几个值（本例 5 个 = 5 排），
 *     所以按该坐标聚类就能把车位切成排。
 *
 * 排内相邻两个车位的**中点**正好是分隔线所在位置，所以宽度不用猜——从间距直接得来。
 * 只有进深要猜：取宽度的 2 倍（标准车位 2.5×5.0）。
 */
export function baysToMarkings(
  objects: PlanObject[],
  opts: { k?: number; origin?: Pt } = {},
): BayRow[] {
  const k = opts.k ?? 1 / UNIT_M;
  const off = opts.origin;
  const bays = objects.filter((o) => o.role === 'parkingBay');
  if (bays.length === 0) return [];

  // 按朝向分 4 组。yaw 归一到 [0, 2π) 之后四舍五入到 90° —— 量化误差不会让车位错组，
  // 因为 Placemaker 的车位本来就只有这 4 个方向。
  const groups = new Map<number, PlanObject[]>();
  for (const b of bays) {
    const q = Math.round(norm2pi(b.yaw ?? 0) / (Math.PI / 2)) % 4;
    const list = groups.get(q) ?? [];
    list.push(b);
    groups.set(q, list);
  }

  const rows: BayRow[] = [];
  for (const [quadrant, list] of groups) {
    // 0 / 2 → 车位朝 ±Z，排列沿 X（"横排"）；1 / 3 → 排列沿 Z（"竖排"）
    const alongX = quadrant === 0 || quadrant === 2;
    const perp = (b: PlanObject) => (alongX ? b.z : b.x);   // 垂直于排列方向的坐标
    const along = (b: PlanObject) => (alongX ? b.x : b.z);  // 排列方向上的坐标

    // 按 perp 聚类（容差 0.6 m：一排车位在垂直于排列方向上必然共线）
    const sorted = [...list].sort((a, b) => perp(a) - perp(b));
    let bucket: PlanObject[] = [];
    const flush = () => {
      if (bucket.length === 0) return;
      // 一段"排"里可能夹着断口（斜地块上轴对齐排被边界切开，会留下几十米的空档），
      // 所以 buildRow 返回的是**若干连续段**，而不是一整排。
      for (const row of buildRow(bucket, alongX, perp, along, k, off)) rows.push(row);
      bucket = [];
    };
    for (const b of sorted) {
      if (bucket.length > 0 && Math.abs(perp(b) - perp(bucket[0])) > 0.6) flush();
      bucket.push(b);
    }
    flush();
  }
  return rows;
}

/** 把一排里断开的车位切成连续段，每段单独画线。 */
function buildRow(
  bucket: PlanObject[],
  alongX: boolean,
  perp: (b: PlanObject) => number,
  along: (b: PlanObject) => number,
  k: number,
  off?: Pt,
): BayRow[] {
  if (bucket.length === 0) return [];
  const items = [...bucket].sort((a, b) => along(a) - along(b));
  const ox = off?.x ?? 0, oy = off?.y ?? 0;
  const M = (x: number, z: number) => new Pt(x * k + ox, z * k + oy);

  // 车位宽：取相邻间距的中位数。中位数而不是平均 —— 斜地块上跨段的长间隔会把平均拉飞。
  const gaps: number[] = [];
  for (let i = 1; i < items.length; i++) {
    const d = along(items[i]) - along(items[i - 1]);
    if (d > 1.2 && d < 6) gaps.push(d);
  }
  gaps.sort((a, b) => a - b);
  const w = gaps.length > 0 ? Math.min(4, Math.max(2, gaps[gaps.length >> 1])) : 2.5;
  const depth = w * 2;
  const fixed = perp(items[0]);

  // 按 along 的连续性切段：间距超过 1.6 个车位宽就是断口。
  // 不切的话，一条"前缘线"会横穿整块地把中间的车位排全划掉 —— 图上非常显眼。
  const segs: PlanObject[][] = [];
  let cur: PlanObject[] = [items[0]];
  for (let i = 1; i < items.length; i++) {
    if (along(items[i]) - along(items[i - 1]) > w * 1.6) {
      segs.push(cur);
      cur = [];
    }
    cur.push(items[i]);
  }
  segs.push(cur);

  const out: BayRow[] = [];
  for (const seg of segs) {
    if (seg.length === 0) continue;
    const lo = along(seg[0]);
    const hi = along(seg[seg.length - 1]);

    // 分隔线：每段 N+1 条（首尾各一条 + 每个车位之间一条）
    const lines: [Pt, Pt][] = [];
    for (const a of [lo - w / 2, ...seg.map((b) => along(b) + w / 2)]) {
      const x = alongX ? a : fixed;
      const z = alongX ? fixed : a;
      const p0 = alongX ? M(x, z - depth / 2) : M(x - depth / 2, z);
      const p1 = alongX ? M(x, z + depth / 2) : M(x + depth / 2, z);
      lines.push([p0, p1]);
    }

    // 前缘：段的两侧各一条，沿排列方向。两端必须同侧 —— 写成
    // (lo, fixed-½深) → (hi, fixed+½深) 会得到一条斜穿整段的对角线。
    const lo2 = lo - w / 2, hi2 = hi + w / 2;
    const fronts: [Pt, Pt][] = [fixed - depth / 2, fixed + depth / 2].map((s) => {
      const a = alongX ? M(lo2, s) : M(s, lo2);
      const b = alongX ? M(hi2, s) : M(s, hi2);
      return [a, b] as [Pt, Pt];
    });

    out.push({ centers: seg.map((b) => M(b.x, b.z)), lines, fronts });
  }
  return out;
}

/** 线段 → 细长矩形环（两端各留半宽，做成平头而不是斜头）。 */
function bayLineRing(a: Pt, b: Pt, halfW: number): Pt[] | null {
  const dx = b.x - a.x, dy = b.y - a.y;
  const len = Math.hypot(dx, dy);
  if (len < 1e-4) return null;
  const ux = dx / len, uy = dy / len;
  const nx = -uy * halfW, ny = ux * halfW;
  const ex = ux * halfW, ey = uy * halfW;
  // 顺序是 -n 一侧走到底、再回 +n 一侧 —— 这样环是 CCW，和 `ringOf()` 出来的面一致。
  // 反过来（+n 先走）是 CW：下游 mesh.ts 会 ensureCCW 兜住，但那就成了"桥接的约定要靠
  // 别人擦屁股"，一旦有人直接拿 ring 去用就会整片朝下。
  const ring = [
    new Pt(a.x - ex - nx, a.y - ey - ny),
    new Pt(b.x + ex - nx, b.y + ey - ny),
    new Pt(b.x + ex + nx, b.y + ey + ny),
    new Pt(a.x - ex + nx, a.y - ey + ny),
  ];
  return ring;
}

function countBays(objects: PlanObject[]): number {
  let n = 0;
  for (const o of objects) if (o.role === 'parkingBay') n++;
  return n;
}

// ─────────────────────────────────────────────────────────── 小工具

/** 三次贝塞尔采样成折线。段数按弦长走，直线只要 2 个点。 */
export function sampleBezier(a: Pt, b: Pt, c: Pt, d: Pt): Pt[] {
  const chord = Pt.distance(a, d);
  // 控制点偏离弦的程度决定弯曲度；直线（b/c 在弦上）直接给 2 点
  const bulge = Math.abs(segDist(b, a, d)) + Math.abs(segDist(c, a, d));
  if (bulge < 1e-3 || chord < 1e-6) return [a, d];
  const n = Math.min(24, Math.max(4, Math.ceil(chord / 2)));
  const out: Pt[] = [];
  for (let i = 0; i <= n; i++) {
    const t = i / n, s = 1 - t;
    out.push(
      new Pt(
        s * s * s * a.x + 3 * s * s * t * b.x + 3 * s * t * t * c.x + t * t * t * d.x,
        s * s * s * a.y + 3 * s * s * t * b.y + 3 * s * t * t * c.y + t * t * t * d.y,
      ),
    );
  }
  return out;
}

function segDist(p: Pt, a: Pt, b: Pt): number {
  const dx = b.x - a.x, dy = b.y - a.y;
  const L = dx * dx + dy * dy;
  if (L < 1e-12) return Pt.distance(p, a);
  const t = Math.max(0, Math.min(1, ((p.x - a.x) * dx + (p.y - a.y) * dy) / L));
  return Math.hypot(p.x - (a.x + t * dx), p.y - (a.y + t * dy));
}

/** yaw（3D，0 = +Z）→ rot（2D 画布，0 = +X）。 */
export function yawToRot(yaw: number): number {
  return yaw - Math.PI / 2;
}

function norm2pi(v: number): number {
  const t = v % (Math.PI * 2);
  return t < 0 ? t + Math.PI * 2 : t;
}

/**
 * 环是否简单（无自交）。
 *
 * 为什么不用 `zones.isSimpleRing`：那版对"顶点恰好落在另一条边上"放过（cross < 1e-9），
 * 布尔量化之后这种贴合很常见。但铺装面是要三角化的，顶点压在对边上 earcut 会吐零宽裂缝，
 * 导出 glTF 后就是绕序异常。这里按符号严格相异判，和 modern.ts 的 simpleRing 同源。
 */
function simpleRing(ring: Pt[]): boolean {
  const n = ring.length;
  if (n < 3) return false;
  const cr = (o: Pt, a: Pt, b: Pt) => (a.x - o.x) * (b.y - o.y) - (a.y - o.y) * (b.x - o.x);
  for (let i = 0; i < n; i++) {
    const a = ring[i], b = ring[(i + 1) % n], c = ring[(i + 2) % n];
    if (Math.abs(cr(a, b, c)) < 1e-12) return false;   // 三点共线 → 退化
  }
  // 非相邻边相交检测。面都不大（几十个点），O(n²) 足够。
  for (let i = 0; i < n; i++) {
    const a1 = ring[i], a2 = ring[(i + 1) % n];
    for (let j = i + 2; j < n; j++) {
      if (i === 0 && j === n - 1) continue;
      const b1 = ring[j], b2 = ring[(j + 1) % n];
      if (segmentsCross(a1, a2, b1, b2)) return false;
    }
  }
  return true;
}

function segmentsCross(a1: Pt, a2: Pt, b1: Pt, b2: Pt): boolean {
  const d = (o: Pt, a: Pt, b: Pt) => (a.x - o.x) * (b.y - o.y) - (a.y - o.y) * (b.x - o.x);
  const d1 = d(b1, b2, a1), d2 = d(b1, b2, a2), d3 = d(a1, a2, b1), d4 = d(a1, a2, b2);
  return ((d1 > 0) !== (d2 > 0)) && ((d3 > 0) !== (d4 > 0));
}
