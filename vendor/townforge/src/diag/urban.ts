// C0.0 · 城市诊断（Urban Recon）
//
// 为什么先做这个：Parking / Plaza 那两轮最大的经验是**先量、不猜**。
// 现在城市看起来"一塌糊涂"，但到底塌在哪一层还没有数字。这一层只负责量，不改生成。
//
// 四个头部指标（用户 2026-11-03 指定）：
//   · landUseCoherence   用途是否成片（还是在撒芝麻）
//   · neighborHeightJump 相邻街区高差（那张图里 20m 挨着 180m 就是它）
//   · roadHierarchyRatio 道路等级配比（只有毛细血管没有骨架）
//   · districtTransition 相邻片区的兼容性（CBD 直接贴低密住宅）
//
// 其余是常规体检：FAR / 覆盖率 / 层高分布 / 街区与宗地面积分布 / 开敞空间。
//
// **不引入新几何**：只用 Model 已有的 patches / wards / edgeClass。

import { Pt } from '../core/geom';
import { Polygon } from '../core/polygon';
import { edgeKey, type EdgeClass } from '../town/edgeclass';
import type { Model } from '../town/model';
import type { Patch } from '../town/patch';
import { area as ringArea } from '../town/zones';
import { meters, sqMeters } from '../core/units';
import type { LandUse } from '../scene/types';

// ─────────────────────────────────────────── 指标定义

export interface HeightStats {
  mean: number;
  p50: number;
  p90: number;
  max: number;
}

export interface UrbanMetrics {
  // 规模
  patches: number;
  buildings: number;
  builtBlocks: number;
  /** 地块面积分布（m²） */
  blockArea: { p10: number; p50: number; p90: number; max: number };
  /** 宗地（楼位）面积分布：用 building footprint 面积近似 */
  parcelArea: { p10: number; p50: number; p90: number; max: number };
  /** 楼栋基底占街区面积比 */
  coverage: number;
  /** 总建筑面积 / 街区总面积 */
  far: number;

  // 高度
  height: HeightStats;
  /** 相邻街区高差（m）：median / p90 / max */
  heightJump: HeightStats & { pairs: number };

  // 用途
  useShare: Partial<Record<LandUse, number>>;
  /** 每块地"邻居里同用途的比例"的均值 —— 0.5 以下基本就是撒芝麻 */
  neighborUseAgreement: number;
  /** 代理片区（k-means k=6）内的最大用途占比均值 —— 没有真片区模型时的替代口径 */
  districtPurity: number;
  /** 相邻但用途不同的地块对里，"不兼容"组合的占比 */
  districtTransition: { pairs: number; incompatible: number; share: number };

  // 道路
  roadLength: number;
  roadByClass: Record<EdgeClass, { length: number; share: number }>;
  /** 交叉口度数分布：度为 1 的端点 = 断头路 */
  intersections: { nodes: number; deadEnds: number; maxDegree: number };

  // 开敞
  /** 绿地 / 公园 / 保留地占街区面积比 */
  openSpaceRatio: number;
  /** 连片开敞空间（同用途相邻连通块）里最大一块占总开敞的比例 */
  largestOpenShare: number;
}

// ─────────────────────────────────────────── 工具

const USE_ALL: LandUse[] = ['cbd', 'commercial', 'residential', 'industrial', 'civic', 'park', 'parking', 'green', 'reserved'];

/** 用途兼容矩阵：不兼容 = 直接贴在一起会明显难看 */
const INCOMPATIBLE: [LandUse, LandUse][] = [
  ['industrial', 'residential'],
  ['industrial', 'civic'],
  ['industrial', 'park'],
  ['cbd', 'residential'],
  ['cbd', 'reserved'],
  ['parking', 'park'],
  ['parking', 'residential'],
];

const isIncompatible = (a: LandUse, b: LandUse): boolean =>
  INCOMPATIBLE.some(([x, y]) => (x === a && y === b) || (x === b && y === a));

const pct = (xs: number[], p: number): number => {
  if (!xs.length) return 0;
  const v = xs.slice().sort((a, b) => a - b);
  return v[Math.min(v.length - 1, Math.floor(v.length * p))];
};

const mean = (xs: number[]): number => (xs.length ? xs.reduce((a, b) => a + b, 0) / xs.length : 0);

/**
 * 街区最高楼高（**米**）。
 *
 * 注意换算：模型内部是**世界单位**（`UNIT_M = 7` m/单位），
 * 直接当米用会差 7 倍 —— 第一版就这样把 23 m 的楼报成 3.3 m，还顺带把 FAR 压到 0.13。
 */
const heightOf = (p: Patch): number => {
  const w = p.ward as unknown as { lot?: { solids?: { h: number }[] } } | null;
  const solids = w?.lot?.solids;
  if (!solids?.length) return 0;
  let h = 0;
  for (const s of solids) if (s.h > h) h = s.h;
  return meters(h);
};

const footprintOf = (p: Patch): number => {
  const w = p.ward as unknown as { lot?: { solids?: { ring: Pt[] }[] } } | null;
  const solids = w?.lot?.solids;
  if (!solids?.length) return 0;
  return sqMeters(solids.reduce((a, s) => a + Math.abs(ringArea(s.ring)), 0));
};

const useOf = (p: Patch): LandUse | null => {
  const w = p.ward as unknown as { use?: LandUse } | null;
  return w?.use ?? null;
};

const patchArea = (p: Patch): number => sqMeters(Math.abs(ringArea(p.shape as unknown as Pt[])));

/** 枚举唯一无向边：地块边会被两个街区各报一次，用 edgeKey 去重 */
function uniqueEdges(model: Model): { a: Pt; b: Pt; cls: EdgeClass; len: number }[] {
  const seen = new Set<string>();
  const out: { a: Pt; b: Pt; cls: EdgeClass; len: number }[] = [];
  for (const p of model.patches) {
    if (!p.withinCity) continue;
    const ring = p.shape as unknown as Pt[];
    for (let i = 0; i < ring.length; i++) {
      const a = ring[i];
      const b = ring[(i + 1) % ring.length];
      const k = edgeKey(a, b);
      if (seen.has(k)) continue;
      seen.add(k);
      out.push({ a, b, cls: model.edgeClass.get(k) ?? 'local', len: meters(Math.hypot(b.x - a.x, b.y - a.y)) });
    }
  }
  return out;
}

/** 确定性 k-means（k=6，固定初值）—— 只当"片区"的代理，别当真空片区模型（那是 C0.1） */
function proxyClusters(model: Model, k = 6): Patch[][] {
  const ps = model.patches.filter((p) => p.withinCity && !p.blocked);
  if (!ps.length) return [];
  const pts = ps.map((p) => {
    const c = new Polygon(p.shape as unknown as Pt[]);
    let x = 0;
    let y = 0;
    for (const v of c) {
      x += v.x;
      y += v.y;
    }
    return { p, x: x / c.length, y: y / c.length };
  });
  const ordered = pts.slice().sort((a, b) => a.x - b.x || a.y - b.y);
  const kk = Math.min(k, pts.length);
  const centers = Array.from({ length: kk }, (_, i) => {
    const o = ordered[Math.floor(((i + 0.5) / kk) * ordered.length)];
    return { x: o.x, y: o.y };
  });
  const assign = new Array(pts.length).fill(0);
  for (let it = 0; it < 12; it++) {
    let moved = false;
    for (let i = 0; i < pts.length; i++) {
      let best = 0;
      let bd = Infinity;
      for (let c = 0; c < kk; c++) {
        const d = (pts[i].x - centers[c].x) ** 2 + (pts[i].y - centers[c].y) ** 2;
        if (d < bd) {
          bd = d;
          best = c;
        }
      }
      if (assign[i] !== best) {
        assign[i] = best;
        moved = true;
      }
    }
    for (let c = 0; c < kk; c++) {
      const mine = pts.filter((_, i) => assign[i] === c);
      if (!mine.length) continue;
      centers[c] = { x: mean(mine.map((m) => m.x)), y: mean(mine.map((m) => m.y)) };
    }
    if (!moved) break;
  }
  const groups: Patch[][] = Array.from({ length: kk }, () => []);
  pts.forEach((q, i) => groups[assign[i]].push(q.p));
  return groups.filter((g) => g.length);
}

// ─────────────────────────────────────────── 主函数

export function urbanMetrics(model: Model, opts: { clusterK?: number } = {}): UrbanMetrics {
  const patches = model.patches.filter((p) => p.withinCity);
  const built = patches.filter((p) => heightOf(p) > 0);

  // 面积
  const blockAreas = patches.map(patchArea);
  const footAreas = built.map(footprintOf).filter((a) => a > 1);
  const totalBlock = blockAreas.reduce((a, b) => a + b, 0);
  const totalFoot = footAreas.reduce((a, b) => a + b, 0);

  // 高度
  const heights = built.map(heightOf);
  // 楼层数：TF 层高约 3 m（桥里也是这个口径），这里只做"高差"用途
  const hStats: HeightStats = {
    mean: mean(heights),
    p50: pct(heights, 0.5),
    p90: pct(heights, 0.9),
    max: heights.length ? Math.max(...heights) : 0,
  };

  // 相邻高差 + 用途相邻
  const jumps: number[] = [];
  let usePairs = 0;
  let incompatible = 0;
  let agreementSum = 0;
  let agreementN = 0;
  const seenPair = new Set<string>();
  for (const p of patches) {
    const nb = model.getNeighbours(p) ?? [];
    const myUse = useOf(p);
    let same = 0;
    let cnt = 0;
    for (const q of nb) {
      if (!q.withinCity) continue;
      const k = [p.shape, q.shape].map((s) => (s as unknown as Pt[])[0]).map((v) => `${Math.round(v.x)},${Math.round(v.y)}`).sort().join('|');
      const once = !seenPair.has(k);
      if (once) seenPair.add(k);

      const h1 = heightOf(p);
      const h2 = heightOf(q);
      if (h1 > 0 && h2 > 0 && once) jumps.push(Math.abs(h1 - h2));

      const u1 = myUse;
      const u2 = useOf(q);
      if (u1 && u2) {
        cnt++;
        if (u1 === u2) same++;
        if (once) {
          usePairs++;
          if (isIncompatible(u1, u2)) incompatible++;
        }
      }
    }
    if (cnt) {
      agreementSum += same / cnt;
      agreementN++;
    }
  }

  // 用途占比
  const tally = new Map<LandUse, number>();
  for (const p of patches) {
    const u = useOf(p);
    if (u) tally.set(u, (tally.get(u) ?? 0) + 1);
  }
  const totalUse = [...tally.values()].reduce((a, b) => a + b, 0) || 1;
  const useShare: Partial<Record<LandUse, number>> = {};
  for (const u of USE_ALL) if (tally.has(u)) useShare[u] = (tally.get(u) as number) / totalUse;

  // 代理片区纯度
  const clusters = proxyClusters(model, opts.clusterK ?? 6);
  const purities = clusters.map((g) => {
    const t = new Map<LandUse, number>();
    for (const p of g) {
      const u = useOf(p);
      if (u) t.set(u, (t.get(u) ?? 0) + 1);
    }
    const tot = [...t.values()].reduce((a, b) => a + b, 0);
    return tot ? Math.max(...t.values()) / tot : 0;
  });

  // 道路
  const edges = uniqueEdges(model);
  const roadLength = edges.reduce((a, e) => a + e.len, 0);
  const byClass: Record<EdgeClass, number> = { arterial: 0, collector: 0, local: 0 };
  const nodeDeg = new Map<string, number>();
  for (const e of edges) {
    byClass[e.cls] += e.len;
    for (const v of [e.a, e.b]) {
      const k = `${Math.round(v.x * 100)},${Math.round(v.y * 100)}`;
      nodeDeg.set(k, (nodeDeg.get(k) ?? 0) + 1);
    }
  }
  const roadByClass = {
    arterial: { length: byClass.arterial, share: roadLength ? byClass.arterial / roadLength : 0 },
    collector: { length: byClass.collector, share: roadLength ? byClass.collector / roadLength : 0 },
    local: { length: byClass.local, share: roadLength ? byClass.local / roadLength : 0 },
  };
  const degs = [...nodeDeg.values()];

  // 开敞空间
  const openUse = new Set<LandUse>(['park', 'green', 'reserved']);
  const openPatches = patches.filter((p) => {
    const u = useOf(p);
    return u ? openUse.has(u) : false;
  });
  const openArea = openPatches.reduce((a, p) => a + patchArea(p), 0);
  // 最大连片：在开敞地块的邻接图上做连通块
  const openSet = new Set(openPatches);
  const seen = new Set<Patch>();
  let largest = 0;
  for (const p of openPatches) {
    if (seen.has(p)) continue;
    let area = 0;
    const stack = [p];
    seen.add(p);
    while (stack.length) {
      const cur = stack.pop() as Patch;
      area += patchArea(cur);
      for (const q of model.getNeighbours(cur) ?? []) {
        if (openSet.has(q) && !seen.has(q)) {
          seen.add(q);
          stack.push(q);
        }
      }
    }
    if (area > largest) largest = area;
  }

  return {
    patches: patches.length,
    buildings: built.reduce((a, p) => a + ((p.ward as unknown as { geometry?: unknown[] }).geometry?.length ?? 0), 0),
    builtBlocks: built.length,
    blockArea: { p10: pct(blockAreas, 0.1), p50: pct(blockAreas, 0.5), p90: pct(blockAreas, 0.9), max: blockAreas.length ? Math.max(...blockAreas) : 0 },
    parcelArea: { p10: pct(footAreas, 0.1), p50: pct(footAreas, 0.5), p90: pct(footAreas, 0.9), max: footAreas.length ? Math.max(...footAreas) : 0 },
    coverage: totalBlock ? totalFoot / totalBlock : 0,
    // FAR ≈ 总基底面积 × (平均楼高 / 层高) / 街区面积；层高按 3 m（与桥同口径）
    far: totalBlock ? (totalFoot * (hStats.p50 / 3)) / totalBlock : 0,
    height: hStats,
    heightJump: { mean: mean(jumps), p50: pct(jumps, 0.5), p90: pct(jumps, 0.9), max: jumps.length ? Math.max(...jumps) : 0, pairs: jumps.length },
    useShare,
    neighborUseAgreement: agreementN ? agreementSum / agreementN : 0,
    districtPurity: mean(purities),
    districtTransition: { pairs: usePairs, incompatible, share: usePairs ? incompatible / usePairs : 0 },
    roadLength,
    roadByClass,
    intersections: {
      nodes: degs.length,
      deadEnds: degs.filter((d) => d === 1).length,
      maxDegree: degs.length ? Math.max(...degs) : 0,
    },
    openSpaceRatio: totalBlock ? openArea / totalBlock : 0,
    largestOpenShare: openArea ? largest / openArea : 0,
  };
}

/** 每个街区导出成可视化用的一行 */
export interface BlockVis {
  ring: Pt[];
  use: LandUse | null;
  height: number;
}
export function blocksForVis(model: Model): BlockVis[] {
  return model.patches
    .filter((p) => p.withinCity)
    .map((p) => ({ ring: (p.shape as unknown as Pt[]).slice(), use: useOf(p), height: heightOf(p) }));
}

export interface EdgeVis {
  a: Pt;
  b: Pt;
  cls: EdgeClass;
}
export function edgesForVis(model: Model): EdgeVis[] {
  return uniqueEdges(model).map((e) => ({ a: e.a, b: e.b, cls: e.cls }));
}
