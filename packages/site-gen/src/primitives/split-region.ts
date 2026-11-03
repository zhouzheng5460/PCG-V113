// 原语 5/5：**区域分解**（`splitRegion`）。
//
// 目的只有一个（用户 2026-11-03 定的 P2 范围）：
//   > **允许同一个地块不同区域使用不同的 parking frame。**
//
// 现在所有行带类生成器都是"整个 polygon → 找最长边 → 全场一个方向"，
// 遇到 L 形 / 弧形 / 两段走向的地块就必然有一段排歪。V1 不做通用自动规划器，
// 只解决"哪些区域更适合方向 A、哪些更适合 B"。
//
// 三条设计约束：
//
// 1. **接口通用，语义由调用方给。** 评分不叫 `capacity` 而叫 `usableScore` —— 否则这个原语
//    会被 parking 语义污染。由生成器传 `evaluator`：
//      parking : 估算车位数 − 车道成本
//      plaza   : 可用空地 − 路径碎片度
//      garden  : 可用种植面 − 边品质
// 2. **切法必须保覆盖、不重叠。** 只允许**半平面切**（一条直线把区域切成两块）——
//    这样"两块之和 = 原区域"由构造保证，不需要事后校验。
// 3. **确定性 + 稳定顺序。** 候选顺序固定、比较用严格 `>`；输出按质心的 (x,y) 排序。
//    否则同一输入的 partition 顺序会漂，快照不可复现。

import { area as ringArea, centroid, type Vec2 } from '../../../plan-ir/src/geom.ts';
import { clipHalfPlane } from './polygon.ts';
import { regionArea, type PolygonRegion, type Ring } from './boolean.ts';

/** 方向（弧度，等价类按 mod π 看待：一条行带的朝向是轴，不是向量） */
export type Orientation = number;

export type SplitStrategy =
  | {
      type: 'orientation';
      /** 候选朝向（弧度）。调用方给，**最多 3 个**（主导边 / 次主导边 / 街道向） */
      directions: Orientation[];
    }
  | {
      type: 'axis';
      /** 沿这个轴切成 `count` 条 */
      axis: Vec2;
      count?: number;
    };

/**
 * 分区评分器。**由生成器提供**，本文件不认识任何 parking / plaza 的概念。
 *
 * @param region 候选分区
 * @param orientation 该分区被分配的朝向（弧度）
 * @param area 分区面积（m²）
 * @returns 分数，越大越好；负数也无所谓（只要同一 evaluator 内部可比）
 */
export type PartitionEvaluator = (region: PolygonRegion, orientation: Orientation, area: number) => number;

export interface RegionPartition {
  region: PolygonRegion;
  /** 该分区的朝向（弧度）；`axis` 策略下为 0 */
  orientation: Orientation;
  area: number;
  usableScore: number;
}

/** 方向归一化到 [0, π)：轴没有正负之分 */
const normAxis = (a: number): number => {
  const m = Math.PI;
  let x = a % m;
  if (x < 0) x += m;
  return Math.round(x * 1e6) / 1e6;
};

/** 用一条直线（法向 `n`，过 `at`）把区域切成两半；空的那半返回 null */
function cutHalf(region: PolygonRegion, at: Vec2, n: Vec2): { pos: PolygonRegion | null; neg: PolygonRegion | null } {
  const pos = clipHalfPlane(region.outer, at, n);
  const neg = clipHalfPlane(region.outer, at, [-n[0], -n[1]]);
  const mk = (outer: Vec2[]): PolygonRegion | null => {
    if (outer.length < 3) return null;
    const r: PolygonRegion = { outer: outer as Ring };
    if (Math.abs(ringArea(outer)) < 0.01) return null;
    return r;
  };
  return { pos: mk(pos), neg: mk(neg) };
}

/** 区域沿某个法向的投影范围 */
function extentAlong(region: PolygonRegion, n: Vec2): { lo: number; hi: number } {
  let lo = Infinity;
  let hi = -Infinity;
  for (const p of region.outer) {
    const t = p[0] * n[0] + p[1] * n[1];
    if (t < lo) lo = t;
    if (t > hi) hi = t;
  }
  return { lo, hi };
}

/**
 * 把一个区域分解成若干"各自有朝向"的子区域。
 *
 * `orientation` 策略的算法（V1，刻意简单）：
 *   1. 先算"不切"时的最好朝向（每个候选各评一次，取最高）；
 *   2. 对每个**有序**候选对 (A,B)、每条**垂直于 A 的切割线**（在 A 的投影范围内取 5 个位置）：
 *        切两半 → 每半各自选最好的朝向 → 两半 `usableScore` 之和；
 *   3. 严格优于"不切"才采用，否则整块保留。
 *
 * 为什么切线与 A 垂直：A 是"这一侧该顺着走"的方向，垂直于它切，等于把"顺着 A 走不通的那一头"
 * 分给 B。这是"两段走向不同的地块"最直接的处理方式。
 *
 * 不追求最优：候选对 ≤ 3×2，位置 5 个，总共 ≤ 30 次评分。够快、够确定。
 */
export function splitRegion(
  region: PolygonRegion,
  strategy: SplitStrategy,
  evaluator: PartitionEvaluator,
): RegionPartition[] {
  const totalArea = regionArea(region);
  if (totalArea <= 0.01) return [];

  if (strategy.type === 'axis') {
    const n = Math.max(1, Math.min(32, strategy.count ?? 2));
    const ax: Vec2 = strategy.axis;
    const { lo, hi } = extentAlong(region, ax);
    const parts: RegionPartition[] = [];
    for (let i = 0; i < n; i++) {
      const a = lo + ((hi - lo) * i) / n;
      const b = lo + ((hi - lo) * (i + 1)) / n;
      let r: PolygonRegion | null = { outer: region.outer };
      const c1 = cutHalf(r, [ax[0] * a, ax[1] * a], ax); // 保留 a 以上
      r = c1.pos;
      if (!r) continue;
      const c2 = cutHalf(r, [ax[0] * b, ax[1] * b], [-ax[0], -ax[1]]);
      r = c2.pos;
      if (!r) continue;
      const area = regionArea(r);
      parts.push({ region: r, orientation: 0, area, usableScore: evaluator(r, 0, area) });
    }
    return stableOrder(parts);
  }

  const dirs = dedupeDirs(strategy.directions);
  if (!dirs.length) {
    return [{ region, orientation: 0, area: totalArea, usableScore: evaluator(region, 0, totalArea) }];
  }

  // 1) 不切
  let best: { score: number; parts: RegionPartition[] } = { score: -Infinity, parts: [] };
  for (const d of dirs) {
    const sc = evaluator(region, d, totalArea);
    if (sc > best.score) {
      best = { score: sc, parts: [{ region, orientation: d, area: totalArea, usableScore: sc }] };
    }
  }

  // 2) 切一刀。
  //
  // 切线与「A、B 两个方向的角平分线」垂直，而不是垂直 A。
  // 要分开的是"适合 A 的那一片"和"适合 B 的那一片"，分界天然落在角平分线的垂线上。
  // 只垂直 A 切，只能切出沿 A 的条带 —— 对 L 形这种两段走向的地块会切出一条 415 m² 的碎边（实测）。
  for (const a of dirs) {
    for (const b of dirs) {
      if (a === b) continue;
      const ua: Vec2 = [Math.cos(a), Math.sin(a)];
      const ub: Vec2 = [Math.cos(b), Math.sin(b)];
      const bis: Vec2 = [ua[0] + ub[0], ua[1] + ub[1]];
      const bl = Math.hypot(bis[0], bis[1]);
      if (bl < 1e-6) continue;
      const n: Vec2 = [bis[0] / bl, bis[1] / bl];
      const { lo, hi } = extentAlong(region, n);
      if (hi - lo < 1) continue;
      for (const t of [0.3, 0.4, 0.5, 0.6, 0.7]) {
        const at: Vec2 = [n[0] * (lo + (hi - lo) * t), n[1] * (lo + (hi - lo) * t)];
        const { pos, neg } = cutHalf(region, at, n);
        if (!pos || !neg) continue;

        const pa = bestOf(pos, dirs, evaluator);
        const pb = bestOf(neg, dirs, evaluator);
        const sum = pa.usableScore + pb.usableScore;
        // 相对容差：float 误差会让"打平"的切分被当成改进（实测方正地块因此被切两半）
        if (sum > best.score * (1 + 1e-6) + 1e-9) best = { score: sum, parts: [pa, pb] };
      }
    }
  }

  return stableOrder(best.parts);
}

/** 在候选方向里给一块区域挑最优朝向 */
function bestOf(region: PolygonRegion, dirs: number[], ev: PartitionEvaluator): RegionPartition {
  const area = regionArea(region);
  let best: RegionPartition = { region, orientation: dirs[0], area, usableScore: ev(region, dirs[0], area) };
  for (const d of dirs.slice(1)) {
    const sc = ev(region, d, area);
    if (sc > best.usableScore) best = { region, orientation: d, area, usableScore: sc };
  }
  return best;
}

function dedupeDirs(dirs: number[]): number[] {
  const seen = new Set<number>();
  const out: number[] = [];
  for (const d of dirs) {
    const k = normAxis(d);
    if (seen.has(k)) continue;
    seen.add(k);
    out.push(k);
    if (out.length >= 3) break; // 最多 3 个候选（用户定的）
  }
  return out;
}

/** 稳定顺序：按质心 (x, y) 升序。同一输入两次结果必须逐字节一致。 */
function stableOrder(parts: RegionPartition[]): RegionPartition[] {
  return parts
    .map((p) => ({ p, c: centroid(p.region.outer) }))
    .sort((u, v) => {
      const dx = Math.round(u.c[0] * 100) - Math.round(v.c[0] * 100);
      if (dx !== 0) return dx;
      return Math.round(u.c[1] * 100) - Math.round(v.c[1] * 100);
    })
    .map((x) => x.p);
}

/** 分区总覆盖（用于校验"全覆盖、不重叠"） */
export function partitionCoverage(parts: readonly RegionPartition[], region: PolygonRegion): number {
  const total = regionArea(region);
  if (total <= 0) return 1;
  return parts.reduce((s, p) => s + p.area, 0) / total;
}
