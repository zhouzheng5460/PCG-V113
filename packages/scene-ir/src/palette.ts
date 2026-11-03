// 配色、宽度、叠层高度 —— 全是纯数字，和 three 无关。
//
// 三条约束，都不是审美问题：
//   1. **颜色只为"分辨图层"**，不做主题、不做美术调优（docs/F §6.2）。所以这张表要短。
//   2. **叠层高度必须有确定顺序**（`ELEV`）。同一个 y 上放两块面 = z-fighting，
//      表现为"远处闪、转一下就变"。这一条 CITYSKLINE 已经踩过：地面叠层没有统一表，
//      最后靠肉眼调 y 值一个个试出来。
//   3. **宽度表是"路型 → 米"的字典**。Placemaker 的 net 不带宽度字段，只能按 role 查 ——
//      查不到就给 `other` 的 8 m，别让它变成 0（0 宽的 ribbon 会退化成一条线，还会报 NaN）。

import type { Layer } from './types.ts';

/**
 * 地面叠层。**顺序就是谁压谁**，从下往上。
 * 加新面之前先在这里占一个位置，不要在调用处随手写 `0.1`。
 */
export const ELEV = {
  /** 地块底板顶面 */
  pad: 0.06,
  /** 铺装 / 草地 / 水面 */
  surface: 0.03,
  /** 地块轮廓线 */
  outline: 0.1,
  /** 人行道（比车行道宽 3.2 m，先铺） */
  walk: 0.1,
  /** 园路 / 步道（不是车行道，所以压在人行道下面一点，免得盖住路缘） */
  path: 0.09,
  /** 地面标线（车位线 / 箭头 / decal） */
  marking: 0.12,
  /** 楼块底面。楼高从这里往上量 —— 所以楼高 = `y1 - y0`，一分不多一分不少。 */
  building: 0.1,
  /** 车行道（盖在人行道上面） */
  road: 0.13,
  /** 宗地底色（要能盖住路面，否则"这块地是商业"看不出来） */
  zoningFill: 0.14,
  /** 车道中心线 */
  centreline: 0.15,
  /** 宗地虚线框 */
  zoningLine: 0.16,
} as const;

/** 图层 → 中文名 + 一句"它在 Plan IR 里是什么"，直接显示在右栏提示里。 */
export const LAYER_INFO: Record<Layer, { label: string; hint: string }> = {
  base: { label: '地块底板', hint: '输入轮廓挤出 —— 让地块浮在网格上，不是引擎给的' },
  surfaces: { label: '铺装 / 草地 / 水面', hint: 'Plan.surfaces —— 引擎铺下去的地面' },
  streets: { label: '街道 / 人行道', hint: 'Plan.nets —— 贝塞尔中心线，按 role 取宽度，两侧各带 1.6 m 人行道' },
  zoning: { label: '宗地', hint: 'Plan.zoning —— 只有带 zone grid 的方案才有' },
  massing: { label: '楼块（体块）', hint: '宗地 → 楼位 → 按层数区间挤出。不是真模型，是 massing' },
  props: { label: '树 / 家具', hint: 'Plan.objects —— 乔木、灌木、花池、其余家具，实例化渲染' },
  markings: { label: '地面标线', hint: 'Plan.objects —— 车位线 / 箭头 / decal，贴地扁片' },
};

export const SURFACE_COLOR: Record<string, number> = {
  ground: 0x353c43,
  paving: 0x8f939a,
  asphalt: 0x4a525a,
  grass: 0x3f6b46,
  plaza: 0x9a9384,
  water: 0x2f628f,
  decorative: 0x7d7160,
};

export const NET_COLOR: Record<string, number> = {
  street: 0x2b333d,
  avenue: 0x28303a,
  culDeSac: 0x2e363f,
  roundabout: 0x28303a,
  perimeterRoad: 0x2f3740,
  pathway: 0xb9a887,
  pathwayWide: 0xb2a181,
  driveAisle: 0x303841,
  driveConnector: 0x303841,
  alley: 0x323a43,
  fence: 0x5a4a3a,
  other: 0x384049,
};

/** 宽度（米）。和 CS2 的路型对应；查不到用 `other`。 */
export const NET_WIDTH: Record<string, number> = {
  street: 12,
  avenue: 17,
  culDeSac: 9,
  roundabout: 12,
  perimeterRoad: 10,
  pathway: 2.6,
  pathwayWide: 4.2,
  driveAisle: 5.5,
  driveConnector: 6.5,
  alley: 4,
  fence: 0.35,
  other: 8,
};

export const ZONE_COLOR: Record<string, number> = {
  commercialLow: 0xc9b489,
  commercialHigh: 0xb8a271,
  mixed: 0xc2a892,
  residentialLow: 0xb98d78,
  residentialRow: 0xb28570,
  residentialMedium: 0xa87d6a,
  residentialHigh: 0x9c7362,
  lowRent: 0xa8896f,
  officeLow: 0x8fa3b8,
  officeHigh: 0x7d95ae,
  industrial: 0x8a8f96,
};

export const MISC = {
  pad: 0x20262c,
  outline: 0x4d5a68,
  walk: 0x9aa1a8,
  centreline: 0x7c8794,
  zoningFill: 0x8b7bd8,
  zoningLine: 0xa795ff,
  canopy: 0x3f7a4a,
  trunk: 0x4a3a2c,
  bush: 0x4b8a55,
  planter: 0x8a7f6d,
  marking: 0xd8dde3,
  prop: 0x9aa4ae,
  /** 选中：只换材质，不动几何。 */
  select: 0xffc83d,
} as const;

/** 人行道比车行道每侧宽出的米数。 */
export const WALK_OVERHANG = 1.6;
