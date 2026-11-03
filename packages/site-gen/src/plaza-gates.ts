// Plaza 的门禁度量（P3.1）。
//
// 判据按用户 2026-11-03 定的清单，**刻意不含"和 oracle 像不像"**：
//
//   usable coverage 98~102% · surface overlap <0.5% · obstacle overlap = 0
//   paving components ≤2 · largest paved ≥90% · accessPoint reachable 100%
//   objects outside obstacles 100%
//
// 只作观测的：surface patch count / region count / 精确物件数 / oracle 的面片切分。
// 理由见 docs/I：142 块面其实是 1 个连通体，面片数是**序列化方式**不是设计结构。

import { area as ringArea, dist, pointInRing, type Vec2 } from '../../plan-ir/src/geom.ts';
import { differenceRegion, intersectRegion, unionRegions, regionArea, type PolygonRegion } from './primitives/boolean.ts';
import type { Plan, SiteInput } from './types.ts';

const toRegion = (ring: readonly Vec2[]): PolygonRegion => ({ outer: ring as Vec2[] });

/** 铺装/绿地按 role 归组 */
const PAVED_ROLES = new Set(['plaza', 'asphalt', 'paving']);
const GREEN_ROLES = new Set(['grass', 'decorative']);

export interface PlazaGateReport {
  siteArea: number;
  /** 障碍物并集的面积（与地块相交的部分） */
  obstacleArea: number;
  usableArea: number;
  pavedArea: number;
  greenArea: number;
  /** (paved + green) / usable —— 门禁 98~102% */
  usableCoverage: number;
  /** 铺装/绿地互相压住的面积占 usable 的比例 */
  surfaceOverlap: number;
  /** 铺装/绿地压到障碍物里的面积占 usable 的比例 —— 门禁 = 0 */
  obstacleOverlap: number;
  /** 铺装的连通块数（1 m 栅格） */
  pavingComponents: number;
  /** 最大连通块 / 铺装总面积 */
  largestPavedRatio: number;
  /** 落在铺装 12 m 内的出入口占比 */
  accessReachable: number;
  /** 不在障碍物内的物件占比 */
  objectsOutsideObstacles: number;
  /** 观测：面片数（不进分） */
  surfacePatchCount: number;
  /** 观测：region 数（不进分） */
  regionCount: number;
  /**
   * **结构性分数**（0~100）：只看上面那些"合法性"判据。
   *
   * 刻意**不叫 fidelity**：它证明的是"这是一个合法的广场"，不是"这是 Placemaker 会设计的那个广场"。
   * 与 oracle 的设计相似度另算（`fidelityVs`），并且只作观测、不进 release gate。
   */
  structuralScore: number;
}

/** 1 m 栅格连通块（只看给定面片集合） */
function componentsOf(
  faces: Vec2[][],
  step = 1,
  withHoles?: readonly { ring: readonly Vec2[]; holes?: readonly (readonly Vec2[])[] }[],
): { count: number; largest: number; total: number } {
  if (!faces.length) return { count: 0, largest: 0, total: 0 };
  let minX = Infinity;
  let minY = Infinity;
  let maxX = -Infinity;
  let maxY = -Infinity;
  for (const f of faces) {
    for (const p of f) {
      if (p[0] < minX) minX = p[0];
      if (p[1] < minY) minY = p[1];
      if (p[0] > maxX) maxX = p[0];
      if (p[1] > maxY) maxY = p[1];
    }
  }
  const w = Math.ceil((maxX - minX) / step) + 1;
  const h = Math.ceil((maxY - minY) / step) + 1;
  if (w * h > 6_000_000) return { count: -1, largest: 0, total: 0 };

  const filled = new Uint8Array(w * h);
  let total = 0;
  for (let j = 0; j < h; j++) {
    for (let i = 0; i < w; i++) {
      const p: Vec2 = [minX + i * step, minY + j * step];
      const hit = faces.some((f, i) => {
        const src = withHoles?.[i];
        if (src?.holes?.length) return pointInRing(p, f) && !src.holes.some((h) => pointInRing(p, h));
        return pointInRing(p, f);
      });
      if (hit) {
        filled[j * w + i] = 1;
        total++;
      }
    }
  }
  const seen = new Int32Array(w * h).fill(-1);
  let best = 0;
  let count = 0;
  for (let s = 0; s < filled.length; s++) {
    if (!filled[s] || seen[s] >= 0) continue;
    count++;
    let n = 0;
    const stack = [s];
    seen[s] = count;
    while (stack.length) {
      const cur = stack.pop() as number;
      n++;
      const ci = cur % w;
      const cj = (cur - ci) / w;
      for (const [di, dj] of [
        [1, 0],
        [-1, 0],
        [0, 1],
        [0, -1],
      ]) {
        const ni = ci + di;
        const nj = cj + dj;
        if (ni < 0 || nj < 0 || ni >= w || nj >= h) continue;
        const k = nj * w + ni;
        if (filled[k] && seen[k] < 0) {
          seen[k] = count;
          stack.push(k);
        }
      }
    }
    if (n > best) best = n;
  }
  return { count, largest: best * step * step, total: total * step * step };
}

export function plazaGate(input: SiteInput, plan: Plan): PlazaGateReport {
  const ring: Vec2[] = input.shape.ring.map(([x, z]) => [x, z] as Vec2);
  const siteArea = Math.abs(ringArea(ring));

  const obs = input.obstacles.map((o) => toRegion(o.quad.map(([x, z]) => [x, z] as Vec2)));
  const obsU = obs.length ? unionRegions(obs) : [];
  const obsInSite = obsU.length ? intersectRegion(toRegion(ring), { outer: obsU[0].outer }) : [];

  const usable = obsU.length ? differenceRegion(toRegion(ring), obsU) : [{ outer: ring }];
  const usableArea = usable.reduce((a, r) => a + regionArea(r), 0);
  const obstacleArea = usum(obsInSite.map(regionArea));

  const paved = plan.surfaces.filter((s) => PAVED_ROLES.has(s.role));
  const green = plan.surfaces.filter((s) => GREEN_ROLES.has(s.role));
  // 面可能带洞（布尔差集减掉内部障碍物的自然结果），面积与覆盖都要扣洞
  const areaOf = (s: { ring: readonly Vec2[]; holes?: readonly (readonly Vec2[])[] }): number =>
    Math.abs(ringArea(s.ring)) - (s.holes ?? []).reduce((a, h) => a + Math.abs(ringArea(h)), 0);
  const insideOf = (p: Vec2, s: { ring: readonly Vec2[]; holes?: readonly (readonly Vec2[])[] }): boolean =>
    pointInRing(p, s.ring) && !(s.holes ?? []).some((h) => pointInRing(p, h));
  const pavedArea = usum(paved.map(areaOf));
  const greenArea = usum(green.map(areaOf));

  // 面片互相压住的面积
  let overlap = 0;
  const all = [...paved, ...green].map((s) => (s.holes?.length ? { outer: s.ring as Vec2[], holes: s.holes as Vec2[][] } : toRegion(s.ring)));
  for (let i = 0; i < all.length; i++) {
    for (let j = i + 1; j < all.length; j++) {
      // 先用包围盒粗筛，避免 N² 次布尔运算
      if (!bboxOverlap(all[i].outer, all[j].outer)) continue;
      for (const r of intersectRegion(all[i], all[j])) overlap += regionArea(r);
    }
  }

  // 压到障碍物上的面积
  let obsOverlap = 0;
  for (const f of all) {
    for (const o of obsU) {
      if (!bboxOverlap(f.outer, o.outer)) continue;
      for (const r of intersectRegion(f, o)) obsOverlap += regionArea(r);
    }
  }

  const comp = componentsOf(paved.map((s) => s.ring as Vec2[]), 1, paved);
  const acc = plan.objects.filter((o) => o.role === 'accessPoint');
  const accessReachable = acc.length
    ? acc.filter((o) => {
        const p: Vec2 = [o.x, o.z];
        return paved.some((s) => distToRingLocal(p, s.ring as Vec2[]) <= 12);
      }).length / acc.length
    : 1;

  const objs = plan.objects.filter((o) => o.role !== 'accessPoint');
  const objectsOutside = objs.length ? objs.filter((o) => !obsU.some((r) => pointInRing([o.x, o.z], r.outer))).length / objs.length : 1;

  const usableCoverage = usableArea > 0 ? (pavedArea + greenArea) / usableArea : 0;
  const surfaceOverlap = usableArea > 0 ? overlap / usableArea : 0;
  const obstacleOverlap = usableArea > 0 ? obsOverlap / usableArea : 0;
  // 栅格连通块可能因边界格略微超出面片面积，夹到 1 免得出现 108% 这种读数
  const largestPavedRatio = pavedArea > 0 ? Math.min(1, comp.largest / pavedArea) : 0;

  // 门禁派生分：只对 fidelity 项打分（不含面片数、不含"像不像 oracle"）
  const band = (v: number, lo: number, hi: number): number => (v >= lo && v <= hi ? 1 : v >= lo - 0.05 && v <= hi + 0.05 ? 0.5 : 0);
  const items = [
    band(usableCoverage, 0.98, 1.02),
    surfaceOverlap <= 0.005 ? 1 : surfaceOverlap <= 0.02 ? 0.5 : 0,
    obstacleOverlap <= 1e-6 ? 1 : 0,
    comp.count >= 0 && comp.count <= 2 ? 1 : 0,
    largestPavedRatio >= 0.9 ? 1 : largestPavedRatio >= 0.75 ? 0.5 : 0,
    accessReachable >= 0.999 ? 1 : 0,
    objectsOutside >= 0.999 ? 1 : 0,
  ];
  const structuralScore = Math.round((usum(items) / items.length) * 100);

  return {
    siteArea,
    obstacleArea,
    usableArea,
    pavedArea,
    greenArea,
    usableCoverage,
    surfaceOverlap,
    obstacleOverlap,
    pavingComponents: comp.count,
    largestPavedRatio,
    accessReachable,
    objectsOutsideObstacles: objectsOutside,
    surfacePatchCount: plan.surfaces.length,
    regionCount: (plan.regions ?? []).length,
    structuralScore,
  };
}

const usum = (xs: number[]): number => xs.reduce((a, b) => a + b, 0);

function bboxOverlap(a: readonly Vec2[], b: readonly Vec2[]): boolean {
  const f = (r: readonly Vec2[]): [number, number, number, number] => {
    let x0 = Infinity;
    let y0 = Infinity;
    let x1 = -Infinity;
    let y1 = -Infinity;
    for (const p of r) {
      if (p[0] < x0) x0 = p[0];
      if (p[1] < y0) y0 = p[1];
      if (p[0] > x1) x1 = p[0];
      if (p[1] > y1) y1 = p[1];
    }
    return [x0, y0, x1, y1];
  };
  const [ax0, ay0, ax1, ay1] = f(a);
  const [bx0, by0, bx1, by1] = f(b);
  return ax1 > bx0 && bx1 > ax0 && ay1 > by0 && by1 > ay0;
}

/** 点到环的最近距离（本地实现，避免为了这个再 import 一次；几何本身在 primitives/scatter 里已有同名函数） */
function distToRingLocal(p: Vec2, ring: readonly Vec2[]): number {
  let best = Infinity;
  for (let i = 0; i < ring.length; i++) {
    const a = ring[i];
    const b = ring[(i + 1) % ring.length];
    const dx = b[0] - a[0];
    const dy = b[1] - a[1];
    const l2 = dx * dx + dy * dy;
    let t = l2 > 1e-12 ? ((p[0] - a[0]) * dx + (p[1] - a[1]) * dy) / l2 : 0;
    t = t < 0 ? 0 : t > 1 ? 1 : t;
    const d = Math.hypot(p[0] - (a[0] + dx * t), p[1] - (a[1] + dy * t));
    if (d < best) best = d;
  }
  return best;
}

void dist;

/**
 * **设计相似度**（观测，不进 gate）。
 *
 * 结构性分数只回答"合不合法"；这一组回答"像不像 Placemaker 的设计"。
 * 按用户 2026-11-03：不用现在追 95 分，但**别让 100 分被误读成 1:1 复刻**。
 */
export interface PlazaFidelity {
  /** 铺装 / usable：oracle vs native */
  pavedRatio: [number, number];
  /** 绿化 / usable */
  greenRatio: [number, number];
  /** 面片数（纯观测，明确不进分） */
  patchCount: [number, number];
  /** region 数 */
  regionCount: [number, number];
  /** 物件密度 / 千 m² */
  objectDensity: [number, number];
  /** 障碍响应：障碍面积占地块比（两边应当一致，因为它来自输入） */
  obstacleRatio: [number, number];
}

export function fidelityVs(gold: Plan, native: Plan, goldGate: PlazaGateReport, nativeGate: PlazaGateReport): PlazaFidelity {
  const r = (a: number, b: number): number => (b > 0 ? a / b : 0);
  return {
    pavedRatio: [r(goldGate.pavedArea, goldGate.usableArea), r(nativeGate.pavedArea, nativeGate.usableArea)],
    greenRatio: [r(goldGate.greenArea, goldGate.usableArea), r(nativeGate.greenArea, nativeGate.usableArea)],
    patchCount: [gold.surfaces.length, native.surfaces.length],
    regionCount: [(gold.regions ?? []).length, (native.regions ?? []).length],
    objectDensity: [
      (gold.objects.length / Math.max(1, gold.area)) * 1000,
      (native.objects.length / Math.max(1, native.area)) * 1000,
    ],
    obstacleRatio: [r(goldGate.obstacleArea, goldGate.siteArea), r(nativeGate.obstacleArea, nativeGate.siteArea)],
  };
}
