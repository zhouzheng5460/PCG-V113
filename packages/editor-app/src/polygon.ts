// 多边形工具。Ring = 一个闭合环；Paths = 一组环（外环 + 洞）。
//
// 两个约定，全项目统一（写错会静默出错，不会报）：
//   1. **面积符号表示方向**：正的 = 逆时针（CCW）= 实体；负的 = 顺时针 = 洞。
//      所以 `pathsArea` 是**代数和**，不是绝对值之和 —— 带洞的面积才对。
//   2. **点的顺序就是边的顺序**：不做隐式闭合，需要闭合的地方自己接上最后一条边。

import { dist, distToSegment, len, sub, type Vec2 } from './vec.ts';

export type Ring = Vec2[];
export type Paths = Ring[];

export interface Bounds {
  minX: number;
  minY: number;
  maxX: number;
  maxY: number;
}

export const emptyBounds = (): Bounds => ({ minX: Infinity, minY: Infinity, maxX: -Infinity, maxY: -Infinity });

export function signedArea(ring: Ring): number {
  let a = 0;
  for (let i = 0; i < ring.length; i++) {
    const p = ring[i];
    const q = ring[(i + 1) % ring.length];
    a += p.x * q.y - q.x * p.y;
  }
  return a / 2;
}

export const ringArea = (ring: Ring): number => Math.abs(signedArea(ring));

/** 带洞面积：外环正、洞负，直接相加就是真面积。 */
export function pathsArea(paths: Paths): number {
  let a = 0;
  for (const r of paths) a += signedArea(r);
  return a;
}

export function ringPerimeter(ring: Ring): number {
  let s = 0;
  for (let i = 0; i < ring.length; i++) s += dist(ring[i], ring[(i + 1) % ring.length]);
  return s;
}

export function pathsPerimeter(paths: Paths): number {
  return paths.reduce((s, r) => s + ringPerimeter(r), 0);
}

export const polylineLength = (pts: Vec2[]): number => {
  let s = 0;
  for (let i = 1; i < pts.length; i++) s += dist(pts[i - 1], pts[i]);
  return s;
};

export function ringCentroid(ring: Ring): Vec2 {
  if (!ring.length) return { x: 0, y: 0 };
  let x = 0;
  let y = 0;
  for (const p of ring) {
    x += p.x;
    y += p.y;
  }
  return { x: x / ring.length, y: y / ring.length };
}

/** 面积加权重心。质心（顶点平均）在凹多边形上会跑到形外，统计用这个。 */
export function ringCenter(ring: Ring): Vec2 {
  const a = signedArea(ring);
  if (Math.abs(a) < 1e-9) return ringCentroid(ring);
  let cx = 0;
  let cy = 0;
  for (let i = 0; i < ring.length; i++) {
    const p = ring[i];
    const q = ring[(i + 1) % ring.length];
    const f = p.x * q.y - q.x * p.y;
    cx += (p.x + q.x) * f;
    cy += (p.y + q.y) * f;
  }
  return { x: cx / (6 * a), y: cy / (6 * a) };
}

export function boundsOfPoints(pts: Vec2[]): Bounds {
  const b = emptyBounds();
  for (const p of pts) {
    if (p.x < b.minX) b.minX = p.x;
    if (p.y < b.minY) b.minY = p.y;
    if (p.x > b.maxX) b.maxX = p.x;
    if (p.y > b.maxY) b.maxY = p.y;
  }
  return b;
}

export function boundsOfPaths(paths: Paths): Bounds {
  return boundsOfPoints(paths.flat());
}

export const boundsValid = (b: Bounds): boolean => Number.isFinite(b.minX) && b.maxX >= b.minX;

export function mergeBounds(a: Bounds, b: Bounds): Bounds {
  return {
    minX: Math.min(a.minX, b.minX),
    minY: Math.min(a.minY, b.minY),
    maxX: Math.max(a.maxX, b.maxX),
    maxY: Math.max(a.maxY, b.maxY),
  };
}

export const boundsContains = (b: Bounds, p: Vec2): boolean =>
  p.x >= b.minX && p.x <= b.maxX && p.y >= b.minY && p.y <= b.maxY;

export function boundsIntersect(a: Bounds, b: Bounds): boolean {
  return !(a.maxX < b.minX || b.maxX < a.minX || a.maxY < b.minY || b.maxY < a.minY);
}

/** 射线法。边界上的点算"内"还是"外"不确定 —— 调用方要么容忍，要么先判距离。 */
export function pointInRing(ring: Ring, p: Vec2): boolean {
  let inside = false;
  for (let i = 0, j = ring.length - 1; i < ring.length; j = i++) {
    const a = ring[i];
    const b = ring[j];
    if (a.y > p.y !== b.y > p.y) {
      const x = ((b.x - a.x) * (p.y - a.y)) / (b.y - a.y) + a.x;
      if (p.x < x) inside = !inside;
    }
  }
  return inside;
}

/** 点是否在 paths 内：奇偶规则（洞里的点算外）。 */
export function pointInPaths(paths: Paths, p: Vec2): boolean {
  let inside = false;
  for (const r of paths) if (pointInRing(r, p)) inside = !inside;
  return inside;
}

export function distToRing(ring: Ring, p: Vec2): number {
  let best = Infinity;
  for (let i = 0; i < ring.length; i++) {
    best = Math.min(best, distToSegment(p, ring[i], ring[(i + 1) % ring.length]));
  }
  return best;
}

export function distToPaths(paths: Paths, p: Vec2): number {
  let best = Infinity;
  for (const r of paths) best = Math.min(best, distToRing(r, p));
  return best;
}

export function ensureCCW(ring: Ring): Ring {
  return signedArea(ring) < 0 ? [...ring].reverse() : [...ring];
}

export function ensureCW(ring: Ring): Ring {
  return signedArea(ring) > 0 ? [...ring].reverse() : [...ring];
}

/** 去掉挨得太近的重复点。闭合环首尾相连也要判。 */
export function cleanRing(ring: Ring, eps = 1e-6): Ring {
  const out: Ring = [];
  for (const p of ring) {
    const last = out[out.length - 1];
    if (last && Math.abs(last.x - p.x) < eps && Math.abs(last.y - p.y) < eps) continue;
    out.push(p);
  }
  while (out.length > 1 && dist(out[0], out[out.length - 1]) < eps) out.pop();
  return out;
}

export function rectRing(x0: number, y0: number, x1: number, y1: number): Ring {
  const ax = Math.min(x0, x1);
  const ay = Math.min(y0, y1);
  const bx = Math.max(x0, x1);
  const by = Math.max(y0, y1);
  return [
    { x: ax, y: ay },
    { x: bx, y: ay },
    { x: bx, y: by },
    { x: ax, y: by },
  ];
}

export function circleRing(c: Vec2, r: number, segments = 32): Ring {
  const out: Ring = [];
  for (let i = 0; i < segments; i++) {
    const a = (i / segments) * Math.PI * 2;
    out.push({ x: c.x + Math.cos(a) * r, y: c.y + Math.sin(a) * r });
  }
  return out;
}

/** 简单多边形判定：非相邻边不相交。自我相交的环所有面积/包含判断都不可信。 */
export function isSimple(ring: Ring): boolean {
  const n = ring.length;
  if (n < 3) return false;
  for (let i = 0; i < n; i++) {
    const a1 = ring[i];
    const a2 = ring[(i + 1) % n];
    for (let j = i + 2; j < n; j++) {
      if (i === 0 && j === n - 1) continue;
      const b1 = ring[j];
      const b2 = ring[(j + 1) % n];
      if (segIntersect(a1, a2, b1, b2)) return false;
    }
  }
  return true;
}

/** 线段是否真相交（不含端点擦碰的端点判定放宽 —— 用在这里只需排除"明显交叉"）。 */
export function segIntersect(a1: Vec2, a2: Vec2, b1: Vec2, b2: Vec2): boolean {
  const d1 = cross(sub(a2, a1), sub(b1, a1));
  const d2 = cross(sub(a2, a1), sub(b2, a1));
  const d3 = cross(sub(b2, b1), sub(a1, b1));
  const d4 = cross(sub(b2, b1), sub(a2, b1));
  return ((d1 > 1e-12 && d2 < -1e-12) || (d1 < -1e-12 && d2 > 1e-12)) && ((d3 > 1e-12 && d4 < -1e-12) || (d3 < -1e-12 && d4 > 1e-12));
}

export const cross = (a: Vec2, b: Vec2): number => a.x * b.y - a.y * b.x;

/**
 * 一组环 → 外环 + 洞。
 * 判定"谁包着谁"用"顶点在另一个环内"的多数票（面积最大的那个当外环）。
 * 不做完整的多层嵌套（外环-岛-洞里的岛），编辑器的输入不需要那么复杂。
 */
export function groupRings(paths: Paths): { outer: Ring; holes: Ring[] }[] {
  const valid = paths.filter((r) => r.length >= 3);
  if (valid.length <= 1) return valid.length ? [{ outer: valid[0], holes: [] }] : [];
  const sorted = [...valid].sort((a, b) => Math.abs(signedArea(b)) - Math.abs(signedArea(a)));
  const outers: { outer: Ring; holes: Ring[] }[] = [];
  const used = new Set<number>();
  for (let i = 0; i < sorted.length; i++) {
    if (used.has(i)) continue;
    const outer = sorted[i];
    used.add(i);
    const holes: Ring[] = [];
    for (let j = i + 1; j < sorted.length; j++) {
      if (used.has(j)) continue;
      const r = sorted[j];
      let inside = 0;
      for (const p of r) if (pointInRing(outer, p)) inside++;
      if (inside > r.length / 2) {
        holes.push(r);
        used.add(j);
      }
    }
    outers.push({ outer, holes });
  }
  return outers;
}

/** 环 → plan-ir / slots 用的元组形式。跨包边界只在这里转。 */
export function ringToTuples(ring: Ring): [number, number][] {
  return ring.map((p) => [p.x, p.y] as [number, number]);
}

export function tuplesToRing(t: readonly (readonly [number, number])[]): Ring {
  return t.map((p) => ({ x: p[0], y: p[1] }));
}

/** 折线 → 带状包络（道路）。端头平切，不封口。 */
export function strokeEnvelope(centerline: Vec2[], width: number): Ring {
  if (centerline.length < 2) return [];
  const half = Math.max(width, 0.2) / 2;
  const left: Vec2[] = [];
  const right: Vec2[] = [];
  for (let i = 0; i < centerline.length; i++) {
    const prev = centerline[Math.max(0, i - 1)];
    const next = centerline[Math.min(centerline.length - 1, i + 1)];
    const d = sub(next, prev);
    const l = len(d) || 1;
    const nx = -d.y / l;
    const ny = d.x / l;
    left.push({ x: centerline[i].x + nx * half, y: centerline[i].y + ny * half });
    right.push({ x: centerline[i].x - nx * half, y: centerline[i].y - ny * half });
  }
  return [...left, ...right.reverse()];
}

/** 屏幕像素容差 → 世界容差。命中测试统一走它，缩放后手感才一致。 */
export const tolOf = (px: number, zoom: number): number => px / Math.max(zoom, 1e-6);
