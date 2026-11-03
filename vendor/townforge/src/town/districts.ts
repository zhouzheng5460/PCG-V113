// C0.1 · 真 District 模型（**不用 k-means**）
//
// 用户 2026-11-03 的判定："UI 的 k-means 可以继续存在，但城市核心的 District 应该是 PCG 真实数据。"
//
// 为什么 k-means 不行：它只看地块质心的分布，是**数据驱动**的聚类，没有城市规划含义 ——
// 换个种子，A/B/C/D 的含义就变了，而且它不产生 polygon（不能当场/高度的载体）。
//
// 这里换成**同心环 + 扇形锚点**的结构模型（现代城市最稳的一套经验规律）：
//
//   ① 锚点：中心 1 个 + 黄金角螺旋上的 K-1 个（半径按 0.42 / 0.72 / 0.88 递进）
//   ② 每个地块归属最近锚点 → 得到 K 个片区（**确定性**，与地块数无关）
//   ③ 片区按"到中心平均半径"排名定角色：
//        rank0        → cbd          中心商务
//        rank1        → mixed        过渡混合
//        末位         → industrial   外围工业
//        倒数第二     → suburban     城郊
//        中间偏外一个 → park         绿地片区（保证城市有像样的开敞空间）
//        其余         → residential
//   ④ 片区内的用途**由片区角色决定**（带很小的确定性扰动），
//      这是"撒芝麻 → 成片"的关键：用途不再是每块地各自抽签。
//
// polygon = 成员地块的并集（用 zones 里已有的 `unionAll`，不引入新几何）。

import { Pt } from '../core/geom';
import type { Model } from './model';
import type { Patch } from './patch';
import type { LandUse } from '../scene/types';
import { unionAll } from './zones';

export type DistrictType = 'cbd' | 'mixed' | 'residential' | 'industrial' | 'suburban' | 'park';

export interface UrbanDistrict {
  id: string;
  type: DistrictType;
  /** 成员地块的并集轮廓 —— C0.2 的场、C0.3 的高度都以它为载体 */
  polygon: Pt[];
  center: Pt;
  density: 'low' | 'mid' | 'high';
  heightBand: 'low' | 'mid' | 'high';
  greenRatio: number;
  /** 0~1，片区内的道路强度（C0.4 会用） */
  roadIntensity: number;
  patches: Patch[];
}

/** 片区类型 → 密度/高度/绿地的默认档（后续 C0.2 的场会在此基础上调制） */
const TYPE_PROFILE: Record<DistrictType, { density: 'low' | 'mid' | 'high'; heightBand: 'low' | 'mid' | 'high'; greenRatio: number; roadIntensity: number }> = {
  cbd: { density: 'high', heightBand: 'high', greenRatio: 0.04, roadIntensity: 0.95 },
  mixed: { density: 'high', heightBand: 'mid', greenRatio: 0.08, roadIntensity: 0.8 },
  residential: { density: 'mid', heightBand: 'mid', greenRatio: 0.16, roadIntensity: 0.55 },
  industrial: { density: 'low', heightBand: 'low', greenRatio: 0.1, roadIntensity: 0.6 },
  suburban: { density: 'low', heightBand: 'low', greenRatio: 0.38, roadIntensity: 0.3 },
  park: { density: 'low', heightBand: 'low', greenRatio: 0.85, roadIntensity: 0.2 },
};

/** 稳定的 0~1 哈希（按质心量化），不用全局 Rng：地块顺序变了也不会串味 */
function hash01(p: Pt): number {
  const x = Math.round(p.x * 7.31);
  const y = Math.round(p.y * 13.17);
  let h = (x * 73856093) ^ (y * 19349663);
  h = (h ^ (h >>> 13)) >>> 0;
  return (h % 100000) / 100000;
}

/**
 * 规划片区。
 *
 * @param types 可选的强类型序列（调试/测试用）；不给就按半径排名自动定
 */
export function planDistricts(model: Model, types?: DistrictType[]): UrbanDistrict[] {
  const inside = model.patches.filter((p) => p.withinCity && !p.blocked);
  if (!inside.length) return [];

  const R = Math.max(1, model.cityRadius);
  const K = Math.max(3, Math.min(8, Math.round(inside.length / 8)));

  // ① 锚点：中心 + 黄金角螺旋；半径分档让片区有"内环 / 中环 / 外环"的层次
  const rings = [0, 0.42, 0.42, 0.72, 0.72, 0.88, 0.88, 0.95];
  const anchors: Pt[] = [];
  for (let k = 0; k < K; k++) {
    const ang = (k * 137.507764) * (Math.PI / 180);
    const r = R * (rings[k] ?? 0.9);
    anchors.push(new Pt(Math.cos(ang) * r, Math.sin(ang) * r));
  }

  // ② 归属最近锚点
  const groups: Patch[][] = Array.from({ length: K }, () => []);
  for (const p of inside) {
    const c = p.shape.centroid;
    let best = 0;
    let bd = Infinity;
    for (let k = 0; k < K; k++) {
      const d = (c.x - anchors[k].x) ** 2 + (c.y - anchors[k].y) ** 2;
      if (d < bd) {
        bd = d;
        best = k;
      }
    }
    groups[best].push(p);
  }

  // 空组丢掉，剩下的按"平均半径"排名
  const kept = groups
    .map((g, k) => ({ g, k }))
    .filter((x) => x.g.length)
    .map((x) => {
      const rs = x.g.map((p) => p.shape.centroid.length / R).sort((a, b) => a - b);
      return { ...x, r: rs[Math.floor(rs.length / 2)] };
    })
    .sort((a, b) => a.r - b.r);

  // ③ 角色配额：中心 CBD → 过渡混合 → 中间住宅 → 外围绿地 → 城郊 → 工业
  const n = kept.length;
  const roleOf: DistrictType[] = kept.map((_, i) => {
    if (types && types[i]) return types[i];
    if (i === 0) return 'cbd';
    if (i === 1 && n >= 5) return 'mixed';
    if (i === n - 1) return 'industrial';
    if (i === n - 2 && n >= 4) return 'suburban';
    if (i === n - 3 && n >= 6) return 'park';
    return 'residential';
  });

  // ③b 小片区并进相邻片区（<2 块地的片区读不出结构，只是噪声）
  const out: UrbanDistrict[] = [];
  for (let i = 0; i < kept.length; i++) {
    const g = kept[i].g;
    if (g.length < 2 && out.length) {
      out[out.length - 1].patches.push(...g);
      continue;
    }
    const cx = g.reduce((a, p) => a + p.shape.centroid.x, 0) / g.length;
    const cy = g.reduce((a, p) => a + p.shape.centroid.y, 0) / g.length;
    const prof = TYPE_PROFILE[roleOf[i]];
    out.push({
      id: `d${out.length}`,
      type: roleOf[i],
      polygon: unionAll(g.map((p) => p.shape as unknown as Pt[])),
      center: new Pt(cx, cy),
      ...prof,
      patches: g,
    });
  }

  // 并完之后可能只剩一个片区：至少保证城中心真的是 CBD
  return out;
}

/** 地块 → 所属片区 */
export function districtMap(districts: readonly UrbanDistrict[]): Map<Patch, UrbanDistrict> {
  const m = new Map<Patch, UrbanDistrict>();
  for (const d of districts) for (const p of d.patches) m.set(p, d);
  return m;
}

/**
 * 片区角色 + 确定性扰动 → 地块用途。
 *
 * 这里是"撒芝麻 → 成片"的关键：用途由**所属片区**决定，扰动只在小比例上，
 * 所以一片住宅里 88% 还是住宅；而以前是每块地各自掷骰子。
 */
export function useForDistrict(d: UrbanDistrict, patch: Patch): LandUse {
  const h = hash01(patch.shape.centroid);
  switch (d.type) {
    case 'cbd':
      return h < 0.85 ? 'cbd' : h < 0.96 ? 'commercial' : 'civic';
    case 'mixed':
      return h < 0.55 ? 'commercial' : h < 0.85 ? 'residential' : 'civic';
    case 'residential':
      return h < 0.88 ? 'residential' : h < 0.94 ? 'civic' : 'park';
    case 'industrial':
      return h < 0.88 ? 'industrial' : 'parking';
    case 'suburban':
      return h < 0.55 ? 'residential' : h < 0.85 ? 'green' : 'park';
    case 'park':
      return h < 0.7 ? 'park' : 'green';
  }
}

export const DISTRICT_LABEL: Record<DistrictType, string> = {
  cbd: '中央商务',
  mixed: '混合',
  residential: '住宅',
  industrial: '工业',
  suburban: '城郊',
  park: '绿地',
};
