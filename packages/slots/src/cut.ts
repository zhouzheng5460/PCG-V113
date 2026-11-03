// Slot 切分（docs/E §3.3）：把 Placemaker 给的 zoning 四边形切成建筑位。
//
// 两条来自实测的硬事实，决定了这里为什么这么写：
//   1) **进深是整格**：Placemaker 的宗地深度按 8 m 格算（zoningDepth 1..6 格），
//      实测 3 格 = 24.0 m，一分不差。
//   2) **面宽不是整格**：宗地沿街是被"均分"出来的，实测 150 m 的边切成
//      34.5 / 34.5 / 34 / 34（还让出了一个 13 m 的出入口），根本不是 8 的倍数。
//
// 所以切分策略不能是"把四边形按 8 m 铺满"，而是：
//   面宽按目标面宽取整块 → 余量 <8 m 就留作侧院（不硬塞）→ 只有余量 ≥8 m 才补一块
//   进深严格按格数
//
// 边角料"不硬塞"是有代价的（浪费面积），但它避免了 3 m × 5 m 的畸形楼
// （那种楼在 3D 里一眼假，而且会让压楼检测失灵）。

import {
  area,
  centroid,
  dist,
  dot,
  frameAt,
  norm,
  perp,
  pointInRing,
  rectRing,
  sub,
  toLocal,
  type Frame,
  type Vec2,
} from '../../plan-ir/src/geom.ts';
import type { PlanZoning, ZoneKind } from '../../plan-ir/src/types.ts';

export interface KindSpec {
  /** 目标面宽（米），会按 8 m 取整。 */
  targetWidth: number;
  /** 单排进深（米），8 m 模数。 */
  depth: number;
  /** 该类型允许的最大进深（多排时按此拆）。 */
  maxDepth: number;
  /** 层数区间。 */
  floors: [number, number];
  /** 层高（米）。 */
  floorHeight: number;
}

/** 十一类预设（docs/B §3.2）。数值是默认值，可被生成器覆盖。 */
export const KIND_SPECS: Record<ZoneKind, KindSpec> = {
  commercialLow: { targetWidth: 16, depth: 16, maxDepth: 24, floors: [1, 2], floorHeight: 4.5 },
  commercialHigh: { targetWidth: 32, depth: 32, maxDepth: 48, floors: [3, 8], floorHeight: 4.5 },
  mixed: { targetWidth: 16, depth: 16, maxDepth: 24, floors: [3, 6], floorHeight: 3.6 },
  residentialLow: { targetWidth: 16, depth: 16, maxDepth: 16, floors: [2, 3], floorHeight: 3.0 },
  residentialRow: { targetWidth: 8, depth: 16, maxDepth: 16, floors: [2, 3], floorHeight: 3.0 },
  residentialMedium: { targetWidth: 24, depth: 24, maxDepth: 24, floors: [4, 6], floorHeight: 3.0 },
  residentialHigh: { targetWidth: 24, depth: 32, maxDepth: 32, floors: [8, 20], floorHeight: 3.0 },
  lowRent: { targetWidth: 32, depth: 16, maxDepth: 16, floors: [4, 6], floorHeight: 3.0 },
  officeLow: { targetWidth: 24, depth: 24, maxDepth: 24, floors: [2, 4], floorHeight: 4.0 },
  officeHigh: { targetWidth: 24, depth: 32, maxDepth: 48, floors: [10, 30], floorHeight: 4.0 },
  industrial: { targetWidth: 32, depth: 32, maxDepth: 48, floors: [1, 3], floorHeight: 8.0 },
};

export const CELL = 8;

export interface Slot {
  id: string;
  kind: ZoneKind;
  zoningRef: string;
  /** 面宽 × 进深（米）。面宽沿街，进深朝内。 */
  width: number;
  depth: number;
  /** 绕 +Y 的弧度，0 = 朝 +Z。 */
  yaw: number;
  /** footprint 四角，逆时针。 */
  quad: [Vec2, Vec2, Vec2, Vec2];
  row: number;
  bay: number;
  /** 局部坐标（只用于校验：同一 frame 下的解析判断比采样准）。 */
  localBox: readonly [number, number, number, number];
  /** 是否落在 8 m 模数上（面宽非模数 = 补位块）。 */
  moduleWidth: boolean;
  moduleDepth: boolean;
  /**
   * 进深"铺满到宗地边界"。
   *
   * 只出现一种情况：宗地只有一排、且进深余量 ≥ 半格（4 m）。
   * 此时不进格取整，直接用到边——后面没有第二排要跟它对齐，留 4 m 后院只是白扔地。
   */
  depthFilled: boolean;
}

export interface CutResult {
  slots: Slot[];
  /** 被放弃的余量（进深方向或面宽方向），平方米。 */
  waste: number;
  /** 宗地面积。 */
  zoningArea: number;
  notes: string[];
}

/** 找朝向街道的那条边：方向最平行、且左侧含形心（保证逆时针朝内）。 */
export function pickFrontEdge(quad: readonly Vec2[], streetDir: Vec2): { edge: number; u: Vec2; v: Vec2 } | null {
  const c = centroid(quad);
  let best: { edge: number; u: Vec2; v: Vec2; par: number } | null = null;

  for (let i = 0; i < quad.length; i++) {
    const a = quad[i];
    const b = quad[(i + 1) % quad.length];
    const u = norm(sub(b, a));
    const v = perp(u);
    const inside = dot(sub(c, a), v) > 0;
    if (!inside) continue;
    const par = Math.abs(dot(u, norm(streetDir)));
    if (!best || par > best.par) best = { edge: i, u, v, par };
  }
  return best ? { edge: best.edge, u: best.u, v: best.v } : null;
}

/**
 * 一个宗地 → 若干建筑位。
 * 面宽按目标面宽铺，进深按格数；多排时按 maxDepth 拆。
 */
export function cutZoning(zoning: PlanZoning, kind: ZoneKind, streetDir: Vec2, idPrefix = 'z'): CutResult {
  const spec = KIND_SPECS[kind];
  const quad = zoning.quad;
  const notes: string[] = [];

  const front = pickFrontEdge(quad, streetDir);
  if (!front) {
    return { slots: [], waste: area(quad), zoningArea: area(quad), notes: ['宗地绕序异常，找不到临街边'] };
  }

  const frame = frameAt(quad[front.edge], front.u);
  const locals = quad.map((p) => toLocal(frame, p));
  const uMin = Math.min(...locals.map((p) => p[0]));
  const uMax = Math.max(...locals.map((p) => p[0]));
  const vMin = Math.min(...locals.map((p) => p[1]));
  const vMax = Math.max(...locals.map((p) => p[1]));

  // 临街边在 v ≈ 0，向内是 +v
  const frontV = locals[front.edge][1];
  const inward = Math.abs(vMax - frontV) > Math.abs(vMin - frontV) ? 1 : -1;
  const v0 = frontV;
  const v1 = inward > 0 ? vMax : vMin;
  const depthSpan = Math.abs(v1 - v0);

  if (depthSpan < CELL) {
    return { slots: [], waste: area(quad), zoningArea: area(quad), notes: [`进深 ${depthSpan.toFixed(1)} m < 1 格，不切`] };
  }

  // 进深：按格取整；不足一格当边角料
  const depthCells = Math.max(1, Math.floor(depthSpan / CELL + 1e-6));
  const rows = Math.max(1, Math.ceil((depthCells * CELL) / spec.maxDepth));
  let rowDepth = (depthCells * CELL) / rows;

  // 只有一排 + 进深余量 ≥ 半格 → 直接铺满到边。
  //
  // 实测真实宗地的进深是 17.00 / 25.00 / 33.00 / 14.00 / 22.00 / 12.75 / 8.50，
  // **根本不是 8 的整数倍**（早先"进深必是整格"的结论来自合成用例，被真实数据否掉了）。
  // 一条 14 m 深的宗地若按格取整只给 8 m 的楼，白扔 6 m（43%）——shopsPark 就是这么
  // 浪费到 46.9% 的。退半格（<4 m）仍然当后院，那点深度盖不了东西。
  const depthFilled = rows === 1 && depthCells === 1 && depthSpan - CELL >= CELL / 2;
  if (depthFilled) {
    rowDepth = Math.round(depthSpan * 10) / 10;
    notes.push(`单排宗地进深 ${depthSpan.toFixed(1)} m，余量 ≥ 半格，直接铺满（不进格取整）`);
  }
  const depthModule = Math.abs(rowDepth - Math.round(rowDepth / CELL) * CELL) < 0.05;

  // 面宽：目标宽度铺，余量处理
  const targetW = Math.max(CELL, Math.round(spec.targetWidth / CELL) * CELL);
  const uSpan = uMax - uMin;

  if (uSpan < CELL - 0.05) {
    return { slots: [], waste: area(quad), zoningArea: area(quad), notes: [`面宽 ${uSpan.toFixed(1)} m < 1 格，不切`] };
  }

  const widths: number[] = [];
  if (uSpan < targetW) {
    // 宗地本身就比目标面宽还窄：铺一块"有多少用多少"的楼，标成补位块。
    // 这里早先填的是 targetW —— 真实城市的窄宗地（实测最窄 8.56 m）会被塞进
    // 16 m 的楼，越界 7.4 m，并且和左右邻宗地的楼互相重叠。
    // E2/E3 在 31 块真实宗地上就是这么爆出来的（重叠对 10~66，越界 3~11）。
    widths.push(Math.round(uSpan * 10) / 10);
    notes.push(`面宽 ${uSpan.toFixed(1)} m < 目标 ${targetW} m，整块当一块补位`);
  } else {
    const bays = Math.floor(uSpan / targetW + 1e-6);
    for (let i = 0; i < bays; i++) widths.push(targetW);
    const rest = uSpan - bays * targetW;
    if (rest >= CELL) {
      widths.push(Math.round(rest * 10) / 10);
      notes.push(`余量 ${rest.toFixed(1)} m ≥ 1 格，补一块非模数面宽`);
    } else if (rest > 0.05) {
      notes.push(`余量 ${rest.toFixed(1)} m < 1 格，留作侧院（不硬塞）`);
    }
  }
  const widthModule = (w: number) => Math.abs(w - Math.round(w / CELL) * CELL) < 0.05;

  const slots: Slot[] = [];
  let cursor = uMin;
  for (let bay = 0; bay < widths.length; bay++) {
    const w = widths[bay];
    for (let row = 0; row < rows; row++) {
      const vv0 = v0 + inward * row * rowDepth;
      const vv1 = vv0 + inward * rowDepth;
      const lo = Math.min(vv0, vv1);
      const hi = Math.max(vv0, vv1);
      const ring = rectRing(frame, cursor, cursor + w, lo, hi);
      slots.push({
        id: `${idPrefix}-b${bay}-r${row}`,
        kind,
        // zoningRef 必须是**宗地唯一 id**，不能写 zoneId：
        // 同一个方案的 5 块宗地 zoneId 全是 "EU Commercial Low"，
        // 拿它当归属键会让 E2 跨宗地两两比较局部框（各自的 frame 不同），
        // 报出一堆根本不存在的重叠。实测 mallPark 报 10 对、shopsPark 报 66 对，全是假的。
        zoningRef: idPrefix,
        width: Math.round(w * 10) / 10,
        depth: Math.round(rowDepth * 10) / 10,
        // yaw = 建筑局部 +Z 的朝向 = 朝地块内侧的法向（立面与街道平行）
        yaw: Math.atan2(frame.v[0], frame.v[1]),
        quad: [ring[0], ring[1], ring[2], ring[3]],
        row,
        bay,
        localBox: [cursor, cursor + w, lo, hi],
        moduleWidth: widthModule(w),
        moduleDepth: depthModule,
        depthFilled,
      });
    }
    cursor += w;
  }

  const usedArea = slots.reduce((s, sl) => s + sl.width * sl.depth, 0);
  const waste = Math.max(0, area(quad) - usedArea);

  return { slots, waste, zoningArea: area(quad), notes };
}

/** 批量：每块宗地按同一个 kind 切。 */
export function cutPlan(zonings: readonly PlanZoning[], kind: ZoneKind, streetDir: Vec2): CutResult {
  const all: Slot[] = [];
  const notes: string[] = [];
  let waste = 0;
  let zoningArea = 0;
  zonings.forEach((z, i) => {
    const r = cutZoning(z, kind, streetDir, `z${i}`);
    all.push(...r.slots);
    notes.push(...r.notes);
    waste += r.waste;
    zoningArea += r.zoningArea;
  });
  return { slots: all, waste, zoningArea, notes };
}

/** 点是否在四个角围成的四边形内。 */
export function quadContains(quad: readonly Vec2[], p: Vec2, tol = 0.1): boolean {
  if (pointInRing(p, quad)) return true;
  // 边界容差：到任一条边的距离 ≤ tol 也算在内
  for (let i = 0; i < quad.length; i++) {
    const a = quad[i];
    const b = quad[(i + 1) % quad.length];
    const ab = sub(b, a);
    const t = Math.max(0, Math.min(1, dot(sub(p, a), ab) / Math.max(1e-9, dot(ab, ab))));
    const q: Vec2 = [a[0] + ab[0] * t, a[1] + ab[1] * t];
    if (dist(p, q) <= tol) return true;
  }
  return false;
}

export type { Frame };
