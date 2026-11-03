// Skin（外观）：Slot 的"长什么样"。三种来源，按顺序回退（docs/E §4.1）。
//
//   1) prefab 精确匹配  —— 占地完全相等
//   2) prefab 包含匹配  —— prefab 占地 ≤ slot，补硬质/退线
//   3) 盒子拉伸        —— 任何尺寸都能填，永不缺席
//
// 关键约束：**Skin 不许改几何**。Slot 的 quad 是压楼检测与地面一致性的依据，
// 换模型时一个数都不能动（docs/E 验收 E5、台账 Q-011）。

import type { ZoneKind } from '../../plan-ir/src/types.ts';
import { KIND_SPECS, type Slot } from './cut.ts';

export type SkinSource = 'prefab' | 'box' | 'parametric';

export interface Skin {
  slotId: string;
  source: SkinSource;
  /** 资产 id；盒子为 'box'。 */
  assetId: string;
  floors: number;
  floorHeight: number;
  /** 总高（米）。 */
  height: number;
  /** 渲染用色键（demo 里直接映射成颜色）。 */
  colorKey: ZoneKind;
  /** 与 slot 的占地差（平方米），盒子 = 0。 */
  footprintDelta: number;
  /** 匹配得分，越低越好；盒子为 Infinity。 */
  score: number;
}

/** 资产库条目（E2 的最小形态：先只有尺寸与高度，没有模型）。 */
export interface BuildingAsset {
  id: string;
  kind: ZoneKind;
  w: number;
  d: number;
  floors: number;
  floorHeight: number;
  /** 盒子类资产可以拉伸；prefab 不行。 */
  stretchable: boolean;
  /** 模型键，空 = 还没接模型（走盒子几何）。 */
  meshKey?: string;
}

/**
 * 起步资产库：只给最常用的尺寸，故意不铺满——
 * 这样演示里能看到"精确匹配 / 包含匹配 / 盒子兜底"三层都真实发生。
 */
export const DEFAULT_ASSETS: BuildingAsset[] = [
  { id: 'shop-16x16', kind: 'commercialLow', w: 16, d: 16, floors: 1, floorHeight: 4.5, stretchable: false },
  // 16×8：真实楼位里出现 5 次（39 个中的 5 个），是"沿街单排、进深只有 8 m"的那种地块。
  // 之前没有这一档，它们全部掉到"包含匹配/拉伸"兜底，预制件命中率被这条卡住。
  { id: 'shop-16x8', kind: 'commercialLow', w: 16, d: 8, floors: 1, floorHeight: 4.5, stretchable: false },
  { id: 'shop-24x16', kind: 'commercialLow', w: 24, d: 16, floors: 2, floorHeight: 4.5, stretchable: false },
  { id: 'row-8x16', kind: 'residentialRow', w: 8, d: 16, floors: 3, floorHeight: 3.0, stretchable: false },
  { id: 'apt-24x24', kind: 'residentialMedium', w: 24, d: 24, floors: 5, floorHeight: 3.0, stretchable: false },
  { id: 'office-24x32', kind: 'officeHigh', w: 24, d: 32, floors: 18, floorHeight: 4.0, stretchable: false },
  { id: 'shed-32x32', kind: 'industrial', w: 32, d: 32, floors: 1, floorHeight: 8.0, stretchable: false },
];

/** 32 位字符串哈希，用于给每个 slot 派生稳定随机。 */
export function hash32(s: string): number {
  let h = 2166136261 >>> 0;
  for (let i = 0; i < s.length; i++) {
    h ^= s.charCodeAt(i);
    h = Math.imul(h, 16777619) >>> 0;
  }
  return h >>> 0;
}

/** 与 PCG 同款：mulberry32。地块级种子隔离，「换一版」只影响当前地块。 */
export function mulberry32(seed: number): () => number {
  let a = seed >>> 0;
  return () => {
    a = (a + 0x6d2b79f5) >>> 0;
    let t = a;
    t = Math.imul(t ^ (t >>> 15), t | 1);
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

/** 三层匹配。 */
export function matchSkin(slot: Slot, assets: readonly BuildingAsset[], seed: number): Skin {
  const spec = KIND_SPECS[slot.kind];
  const rng = mulberry32(hash32(`${slot.id}:${seed}`));
  const pool = assets.filter((a) => a.kind === slot.kind);

  let best: { asset: BuildingAsset; score: number; delta: number; exact: boolean } | null = null;
  for (const a of pool) {
    const exact = Math.abs(a.w - slot.width) < 0.05 && Math.abs(a.d - slot.depth) < 0.05;
    const fits = a.w <= slot.width + 0.05 && a.d <= slot.depth + 0.05;
    if (!exact && !fits) continue;
    // 面宽差权重最高：沿街立面错了最显眼
    const score = Math.abs(a.w - slot.width) * 3 + Math.abs(a.d - slot.depth) * 2 + (exact ? 0 : 1);
    const delta = slot.width * slot.depth - a.w * a.d;
    if (!best || score < best.score) best = { asset: a, score, delta, exact };
  }

  if (best) {
    return {
      slotId: slot.id,
      source: 'prefab',
      assetId: best.asset.id,
      floors: best.asset.floors,
      floorHeight: best.asset.floorHeight,
      height: best.asset.floors * best.asset.floorHeight,
      colorKey: slot.kind,
      footprintDelta: Math.round(best.delta * 10) / 10,
      score: best.score,
    };
  }

  // 兜底：盒子拉伸。层数在 kind 区间里随机，但高度只影响外观，不影响占地。
  const floors = spec.floors[0] + Math.floor(rng() * (spec.floors[1] - spec.floors[0] + 1));
  return {
    slotId: slot.id,
    source: 'box',
    assetId: 'box',
    floors,
    floorHeight: spec.floorHeight,
    height: floors * spec.floorHeight,
    colorKey: slot.kind,
    footprintDelta: 0,
    score: Infinity,
  };
}

export function matchAll(slots: readonly Slot[], assets: readonly BuildingAsset[], seed: number): Skin[] {
  return slots.map((s) => matchSkin(s, assets, seed));
}
