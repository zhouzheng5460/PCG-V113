// 资产槽位：**先有槽，再填模型**。
//
// docs/E 的结论是"资产库先做，匹配机制先做，盒子兜底，之后替换模型"。这一层就是那个"槽"：
//   · 每个楼位算出一个 **assetKey**（用途 + 面宽×进深 + 层数），这就是资产的"规格"；
//   · 注册表里有这个 key 就用它（`source: 'prefab'`），没有就退到**程序化 prefab**（`fallback`）；
//   · 命中率必须能报出来 —— 否则"资产库还差多少"永远说不清。
//
// 为什么 key 里带"层数"：同样是 16×16 的地块，1 层和 10 层的楼不可能是同一个模型。
// 为什么带"面宽×进深"：docs/E §4.3 的实测是**按尺寸档匹配**（8/16/24 m 模数），
//   不是按用途随便挑一个 —— 挑错了楼就会穿出地块。
//
// 现在注册表是空的（还没有真实 GLB 资产），所以命中率一定是 0%：
// **这不是缺陷，是当前状态**，面板上要如实写"0% · 全部走程序化 prefab（盒子）"。

import type { SolidFeature } from '../scene/types';
import { Pt } from '../core/geom';
import { u } from '../core/units';
import type { SlotRecord } from './placemaker';
import type { ZoneKind } from './zone';

/**
 * **模数档**：资产库里真实模型会按这些尺寸做（docs/E §4.3 的实测结论 ——
 * 按模数补，不要按样本枚举）。楼位的面宽/进深正好落在这里，才可能"换模型不拉伸"。
 */
export const TIERS = [8, 12, 16, 20, 24, 32, 40, 48] as const;

/** 容差（米）：切出来的楼位是 15.7 还是 16 都算 16 档 */
const TIER_TOL = 0.6;

export const isTier = (v: number): boolean => TIERS.some((t) => Math.abs(v - t) <= TIER_TOL);

/** 这个楼位能不能直接换成真模型（面宽与进深都落在模数档上）。 */
export const modularFit = (s: { width: number; depth: number }): boolean => isTier(s.width) && isTier(s.depth);

export const assetKey = (s: { kind: ZoneKind; width: number; depth: number; floors: number }): string =>
  `${s.kind}|${Math.round(s.width)}x${Math.round(s.depth)}|${s.floors}`;

/**
 * 高楼的"裙房 + 塔楼"拆分阈值（层）。
 *
 * 8 层以上才拆：低层楼拆了反而碎。这个数和 `KIND_SPECS` 里 officeHigh/residentialHigh
 * 的层数下限（10 / 8）对得上，不是随手取的。
 */
export const SPLIT_MIN_FLOORS = 8;
/** 裙房占几层（相对总层数），其余是塔身 */
const PODIUM_SHARE = 0.25;
/** 塔身相对裙房的内退比例（每边收 12%），肉眼能看出"退台" */
const TOWER_INSET = 0.12;

interface Style {
  wall: import('../scene/palette').MatKey;
  roof: import('../scene/palette').MatKey;
}

const STYLE: Record<ZoneKind, Style> = {
  commercialLow: { wall: 'wallPanel', roof: 'roofLight' },
  commercialHigh: { wall: 'wallPanel', roof: 'roofFlat' },
  mixed: { wall: 'wallBrick', roof: 'roofLight' },
  residentialLow: { wall: 'wallConcrete', roof: 'roofDark' },
  residentialRow: { wall: 'wallBrick', roof: 'roofLight' },
  residentialMedium: { wall: 'wallConcrete', roof: 'roofLight' },
  residentialHigh: { wall: 'wallConcrete', roof: 'roofFlat' },
  lowRent: { wall: 'wallConcrete', roof: 'roofDark' },
  officeLow: { wall: 'wallStone', roof: 'roofLight' },
  officeHigh: { wall: 'wallGlass', roof: 'roofFlat' },
  industrial: { wall: 'wallPanel', roof: 'roofMetal' },
};

/** 四边向质心收 k（0~1，1 = 不动）。用于塔身内退。 */
function shrink(q: [number, number][], k: number): [number, number][] {
  const cx = (q[0][0] + q[1][0] + q[2][0] + q[3][0]) / 4;
  const cy = (q[0][1] + q[1][1] + q[2][1] + q[3][1]) / 4;
  return q.map(([x, y]) => [cx + (x - cx) * k, cy + (y - cy) * k] as [number, number]);
}

export interface SlotSolidsResult {
  solids: SolidFeature[];
  /** 面宽与进深都落在模数档上的楼位数 */
  hits: number;
  /** 拆成裙房 + 塔楼的楼位数 */
  split: number;
}

/**
 * 楼位 → 体块。
 *
 * 三种产出：
 *   · **命中资产**：现在注册表为空，走不到；将来在这里换成实例化真模型。
 *   · **高楼拆分**：≥8 层拆成"裙房 + 退台塔身"两块 —— 这是最省成本的"看起来像楼"：
 *     一块盒子永远是盒子，两块一退台就有体量关系（TownForge 的 CBD 预设也是这么做的）。
 *   · **普通楼**：一块盒子。
 */
export function slotsToSolids(slots: SlotRecord[], y0 = 0): SlotSolidsResult {
  const out: SolidFeature[] = [];
  let hits = 0;
  let split = 0;

  for (const s of slots) {
    const st = STYLE[s.kind] ?? STYLE.mixed;
    const foot = (s.foot && s.foot.length >= 3 ? s.foot : s.quad) as [number, number][];
    if (modularFit(s)) hits++;

    const ringOf = (q: [number, number][]): Pt[] => q.map(([x, z]) => new Pt(u(x), u(z)));

    if (s.floors >= SPLIT_MIN_FLOORS) {
      // 裙房：整块基底，做到总高的一部分
      const podiumFloors = Math.max(2, Math.round(s.floors * PODIUM_SHARE));
      const podiumH = podiumFloors * s.floorHeight;
      out.push({
        name: `pm/${s.slotId}/podium`,
        group: `lot/placemaker/${s.kind}`,
        ring: ringOf(foot),
        y0,
        h: u(podiumH),
        wallMat: st.wall,
        roofMat: st.roof,
        kind: 'slot',
        // 裙房也有层：腰线跟着裙房的层数走，不然一段 2 层的基座看起来是一整块
        floors: podiumFloors,
        parapet: false,
      });
      // 塔身：向内退一圈，做到顶
      const towerFoot = shrink(foot, 1 - TOWER_INSET);
      out.push({
        name: `pm/${s.slotId}/tower`,
        group: `lot/placemaker/${s.kind}`,
        ring: ringOf(towerFoot),
        y0: y0 + u(podiumH),
        h: u(s.heightM - podiumH),
        wallMat: st.wall,
        roofMat: st.roof,
        kind: 'slot',
        floors: Math.max(1, s.floors - podiumFloors),
        parapet: true,
      });
      split++;
      continue;
    }

    out.push({
      name: `pm/${s.slotId}`,
      group: `lot/placemaker/${s.kind}`,
      ring: ringOf(foot),
      y0,
      h: u(s.heightM),
      wallMat: st.wall,
      roofMat: st.roof,
      kind: 'slot',
      floors: s.floors,
      parapet: true,
    });
  }

  return { solids: out, hits, split };
}

/** 供 UI 显示：资产槽位的现状。 */
export interface AssetSummary {
  slots: number;
  /** 面宽与进深都落在模数档上的楼位数（这些将来能**直接**换真模型） */
  modular: number;
  /** 命中率（0~1） */
  modularRate: number;
  /** 拆成裙房+塔楼的楼位数 */
  split: number;
  /** 产出体块数 */
  solids: number;
}

export function summarizeAssets(slots: SlotRecord[]): AssetSummary {
  const r = slotsToSolids(slots);
  const modular = slots.filter((s) => modularFit(s)).length;
  return {
    slots: slots.length,
    modular,
    modularRate: slots.length ? modular / slots.length : 0,
    split: r.split,
    solids: r.solids.length,
  };
}
