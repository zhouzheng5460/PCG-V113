// Placemaker 的输入契约与输出契约（Plan IR）——两侧唯一共用的类型。
//
// 来源：vendor/placemaker/Core/Contracts.cs（输入）+ tools/oracle/driver/Emit.cs（输出的 JSON 协议）。
// 命名刻意贴近 C#，方便逐字段对照移植；不贴的地方是 JS 习惯（Vec2 用元组）。
//
// 三条硬约定（照抄桥接层注释，别改）：
//   1) 单位一律米。换算必须在调用方做。
//   2) 平面写 [x, z]，+z 是北，两个轴都不翻转（翻了是镜像，车位朝向全反）。
//   3) yaw 绕 +Y，0 = 朝 +Z。

import type { Vec2, Vec3 } from './geom.ts';

// ───────────────────────────────────────────────────── 分区

/** 11 种 zone kind（Core/Layers/Common/ZoneKinds.cs）。 */
export type ZoneKind =
  | 'commercialLow'
  | 'commercialHigh'
  | 'mixed'
  | 'residentialLow'
  | 'residentialRow'
  | 'residentialMedium'
  | 'residentialHigh'
  | 'lowRent'
  | 'officeLow'
  | 'officeHigh'
  | 'industrial';

export const ZONE_KINDS: ZoneKind[] = [
  'commercialLow',
  'commercialHigh',
  'mixed',
  'residentialLow',
  'residentialRow',
  'residentialMedium',
  'residentialHigh',
  'lowRent',
  'officeLow',
  'officeHigh',
  'industrial',
];

/** kind → CS2 prefab id（计划里写 EU，游戏层按城市主题换前缀）。 */
export const ZONE_PREFAB: Record<ZoneKind, string> = {
  commercialLow: 'EU Commercial Low',
  commercialHigh: 'EU Commercial High',
  mixed: 'EU Residential Mixed',
  residentialLow: 'EU Residential Low',
  residentialRow: 'EU Residential Medium Row',
  residentialMedium: 'EU Residential Medium',
  residentialHigh: 'EU Residential High',
  lowRent: 'Residential LowRent',
  officeLow: 'Office Low',
  officeHigh: 'Office High',
  industrial: 'Industrial Manufacturing',
};

/** 界面中文名（docs/C 术语表 §2.3）。 */
export const ZONE_LABEL: Record<ZoneKind, string> = {
  commercialLow: '低密度商业',
  commercialHigh: '高密度商业',
  mixed: '混合用地',
  residentialLow: '低密度住宅',
  residentialRow: '联排住宅',
  residentialMedium: '中密度住宅',
  residentialHigh: '高密度住宅',
  lowRent: '廉租住宅',
  officeLow: '低层办公',
  officeHigh: '高层办公',
  industrial: '工业制造',
};

export function isZoneKind(s: string): s is ZoneKind {
  return (ZONE_KINDS as string[]).includes(s);
}

// ───────────────────────────────────────────────────── 输入

export type AreaSource = 'drawn' | 'block' | 'painted' | 'perimeter';

export type AttachmentKind = 'roadAlongEdge' | 'roadCrossing' | 'roadNode';

/**
 * 边界贴合点。**这是整个项目的关键输入**：Placemaker 靠它判断"这块地临不临街"，
 * 没有它，停车族/商场族直接 ready=no（docs/A §0）。
 */
export interface BoundaryAttachment {
  kind: AttachmentKind;
  /** 对应 NearbyNet.externalId（roadNode 对应节点 id）。 */
  externalId: number;
  /** 沿那条街的曲线参数（0..1）；roadAlongEdge 用 0.5（道路中点）。 */
  externalT: number;
  position: Vec2;
  /** 街道在接触点的单位切向；roadNode 没有方向。 */
  direction?: Vec2;
  roadWidth: number;
  /** 落在环的第几条边（Ring[i] → Ring[i+1]）。 */
  ringSegment: number;
  /** 边内参数 0..1。 */
  ringT: number;
}

export type NetCategory = 'road' | 'pathway' | 'track' | 'other';

/** 附近的一条街/园路，三次贝塞尔。 */
export interface NearbyNet {
  externalId: number;
  a: Vec2;
  b: Vec2;
  c: Vec2;
  d: Vec2;
  width: number;
  category: NetCategory;
  hasSidewalk: boolean;
  isOneWay: boolean;
  hasBikeLane: boolean;
  isHighway: boolean;
  /** km/h，0 = 未知。 */
  speedLimit: number;
}

export type ObstacleKind = 'building' | 'water' | 'utility' | 'other';

/** 避让对象：定向四边形（不是任意多边形）。 */
export interface Obstacle {
  quad: readonly [Vec2, Vec2, Vec2, Vec2];
  kind: ObstacleKind;
}

/** 既有道路的宗地格网：8 m 格，深 ≤6。 */
export interface ZoneGrid {
  position: Vec2;
  direction: Vec2;
  size: readonly [number, number];
}

/** 用户在地界上点的出入口。 */
export interface EdgeMarker {
  /** generator-defined：entrance / exit / access / pedestrian / bike … */
  kind: string;
  ringSegment: number;
  ringT: number;
  position: Vec2;
  crosswalk?: boolean;
}

export interface AreaShape {
  /** 逆时针、隐式闭合、≥3 点、无自交；y = 地形高度。 */
  ring: Vec3[];
  source: AreaSource;
  attachments: BoundaryAttachment[];
}

export interface SiteContext {
  nearbyNets: NearbyNet[];
  obstacles: Obstacle[];
  zoneGrids: ZoneGrid[];
  /** 乘的是**布局距离**（不是物件与道路的真实尺寸）。默认 0.5，见台账 D-009。 */
  layoutScale: number;
  /** 地界到路径/布局的统一退让，设计米（会再乘 layoutScale）。默认 2。 */
  edgeMargin: number;
  /** 地形高度；我们只有常数（台账 D-014）。 */
  heightAt?: (p: Vec2) => number;
}

export type SettingValue = number | boolean | string | string[];

export interface CaseInput {
  shape: AreaShape;
  site: SiteContext;
  markers: EdgeMarker[];
  settings: Record<string, SettingValue>;
  seed: number;
  regionTags: string[];
}

// ───────────────────────────────────────────────────── 输出（Plan IR）

export type SurfaceRole =
  | 'ground'
  | 'paving'
  | 'asphalt'
  | 'grass'
  | 'plaza'
  | 'water'
  | 'decorative';

export type NetRole =
  | 'street'
  | 'avenue'
  | 'culDeSac'
  | 'roundabout'
  | 'perimeterRoad'
  | 'pathway'
  | 'pathwayWide'
  | 'driveAisle'
  | 'driveConnector'
  | 'alley'
  | 'fence'
  | 'other';

export type ObjectRole =
  | 'tree'
  | 'bush'
  | 'planter'
  | 'bench'
  | 'lamp'
  | 'bin'
  | 'centerpiece'
  | 'furniture'
  | 'fencePiece'
  | 'decal'
  | 'parkingBay'
  | 'charger'
  | 'arrow'
  | 'marker'
  | 'animalSpawner'
  | 'accessPoint'
  | 'culDeCap'
  | 'roundaboutIsland'
  | 'other';

export interface PlanSurface {
  role: SurfaceRole;
  y?: number;
  ring: Vec2[];
  /**
   * 洞（可选）。
   *
   * 为什么要有它：布尔差集减掉**内部**障碍物，数学结果就是"一个带洞的多边形"。
   * 序列化层如果只支持单环，就只能把这个洞切开重排（tessellation），
   * 而那是**渲染/序列化的需要**，不是设计结构（docs/I §2.1：142 块面其实是一个连通体）。
   * 所以契约直接支持洞：设计拓扑 → 序列化拓扑 这一步不再需要"切 138 刀"。
   * golden 的面不带洞（它的 SiteSafety 会把面裁成多块），所以这是**可选**字段，向后兼容。
   */
  holes?: Vec2[][];
  category?: string;
}

export interface PlanNet {
  role: NetRole;
  a: Vec2;
  b: Vec2;
  c: Vec2;
  d: Vec2;
  startAttach?: number;
  endAttach?: number;
  category?: string;
}

export interface PlanObject {
  role: ObjectRole;
  x: number;
  z: number;
  y?: number;
  yaw?: number;
  scale?: number;
  category?: string;
}

export interface PlanZoning {
  zoneId: string;
  quad: readonly [Vec2, Vec2, Vec2, Vec2];
}

/**
 * Placemaker 的「工作区域」（`AreaPlan.Areas`）。
 *
 * 和 `surfaces` 的区别：`surfaces` 是**画出来的地皮**（沥青/草地，有材质），
 * `areas` 是**算法自己的分区结果**（`role: 'lot'` = 这块地按生成器的划分被切成的工作面），
 * 下游通常不直接渲染，但它是"生成器到底怎么理解这块地"最直接的证据。
 */
export interface PlanArea {
  role: string;
  ring: Vec2[];
}

/**
 * 具名区域（`AreaPlan.Regions`），key 形如 `lot:0` / `green:3`。
 *
 * 为什么重要：Placemaker 的 `regionTags` 机制就是靠它往下传的 ——
 * 上层把"这一块是绿地"打上 tag，下层生成器按 tag 决定种什么。
 * 前缀（`lot:` / `green:`）是我能拿到的最强的语义线索，所以单独暴露。
 */
export interface PlanRegion {
  key: string;
  tags: string[];
  ring: Vec2[];
}

/** 生成器自报的统计（`Stat.*`）。**这是 oracle 自己数出来的数**，比对时最可信的一档。 */
export interface PlanStat {
  key: string;
  value: number;
  unit: string;
}

export interface PlanPaint {
  layer: number;
  strength: number;
  ring: Vec2[];
}

export interface PlanTerraform {
  kind: 'raise' | 'lower';
  target: number;
  ring: Vec2[];
}

export interface PlanIssue {
  severity: 'info' | 'warning' | 'error';
  key: string;
  args?: (string | number)[];
}

export interface Plan {
  protocol: string;
  generator: string;
  units: string;
  seed: number;
  area: number;
  ready: boolean;
  readyKey?: string;
  markers: { given: number; auto: number };
  elapsedMs?: number;
  settings: Record<string, SettingValue>;
  surfaces: PlanSurface[];
  nets: PlanNet[];
  objects: PlanObject[];
  /** 生成器的工作分区（可选：不是每个生成器都划） */
  areas?: PlanArea[];
  /** 具名区域 + tags（可选） */
  regions?: PlanRegion[];
  zoning: PlanZoning[];
  paint: PlanPaint[];
  terraform: PlanTerraform[];
  /** 生成器自报的统计（可选） */
  stats?: PlanStat[];
  issues: PlanIssue[];
}
