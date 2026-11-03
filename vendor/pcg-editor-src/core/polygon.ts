import {
  Vec2,
  angleOf,
  dist,
  lerp,
  mid,
  sub,
  v2,
} from './vec';

/** 闭合环：首尾不重复，最后一点自动连回第一点 */
export type Ring = Vec2[];
/** 一组环；ring[0] 为外环，其余为洞（Clipper 约定，方向相反） */
export type Paths = Ring[];

export interface Bounds {
  minX: number;
  minY: number;
  maxX: number;
  maxY: number;
}

export const emptyBounds = (): Bounds => ({
  minX: Infinity,
  minY: Infinity,
  maxX: -Infinity,
  maxY: -Infinity,
});

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

export function boundsValid(b: Bounds): boolean {
  return Number.isFinite(b.minX) && b.maxX >= b.minX && b.maxY >= b.minY;
}

export const boundsWidth = (b: Bounds): number => b.maxX - b.minX;
export const boundsHeight = (b: Bounds): number => b.maxY - b.minY;
export const boundsCenter = (b: Bounds): Vec2 => ({
  x: (b.minX + b.maxX) / 2,
  y: (b.minY + b.maxY) / 2,
});

export function expandBounds(b: Bounds, d: number): Bounds {
  return { minX: b.minX - d, minY: b.minY - d, maxX: b.maxX + d, maxY: b.maxY + d };
}

export function unionBounds(a: Bounds | null, b: Bounds | null): Bounds {
  if (!a) return b ?? emptyBounds();
  if (!b) return a;
  return {
    minX: Math.min(a.minX, b.minX),
    minY: Math.min(a.minY, b.minY),
    maxX: Math.max(a.maxX, b.maxX),
    maxY: Math.max(a.maxY, b.maxY),
  };
}

/** 变宽后的包围盒（用于 3D 聚焦 / 2D fit） */
export function boundsAspect(b: Bounds): number {
  const w = boundsWidth(b);
  const h = boundsHeight(b);
  return h < 1e-9 ? w : w / h;
}

/** 有符号面积：逆时针为正 */
export function signedArea(ring: Ring): number {
  let s = 0;
  for (let i = 0, n = ring.length; i < n; i++) {
    const a = ring[i];
    const b = ring[(i + 1) % n];
    s += a.x * b.y - b.x * a.y;
  }
  return s / 2;
}

export const ringArea = (ring: Ring): number => Math.abs(signedArea(ring));

/** 环集合面积：外环 - 洞（按 Clipper 的相反方向约定，用带符号面积求和更稳） */
export function pathsArea(paths: Paths): number {
  let s = 0;
  for (const r of paths) s += signedArea(r);
  return Math.abs(s);
}

export function ringPerimeter(ring: Ring, closed = true): number {
  let p = 0;
  const n = ring.length;
  const last = closed ? n : n - 1;
  for (let i = 0; i < last; i++) p += dist(ring[i], ring[(i + 1) % n]);
  return p;
}

/** 多边形形心（面积加权）；退化时返回顶点均值 */
export function ringCentroid(ring: Ring): Vec2 {
  const a = signedArea(ring);
  const n = ring.length;
  if (Math.abs(a) < 1e-9 || n < 3) {
    const s = ring.reduce((acc, p) => ({ x: acc.x + p.x, y: acc.y + p.y }), v2());
    return n ? { x: s.x / n, y: s.y / n } : v2();
  }
  let cx = 0;
  let cy = 0;
  for (let i = 0; i < n; i++) {
    const p = ring[i];
    const q = ring[(i + 1) % n];
    const f = p.x * q.y - q.x * p.y;
    cx += (p.x + q.x) * f;
    cy += (p.y + q.y) * f;
  }
  return { x: cx / (6 * a), y: cy / (6 * a) };
}

export interface Edge {
  a: Vec2;
  b: Vec2;
  /** 所属环索引 */
  ring: number;
  /** 环内边索引 */
  index: number;
}

export function ringEdges(ring: Ring, ringIndex = 0): Edge[] {
  const out: Edge[] = [];
  const n = ring.length;
  for (let i = 0; i < n; i++) {
    out.push({ a: ring[i], b: ring[(i + 1) % n], ring: ringIndex, index: i });
  }
  return out;
}

export function pathsEdges(paths: Paths): Edge[] {
  const out: Edge[] = [];
  paths.forEach((r, i) => out.push(...ringEdges(r, i)));
  return out;
}

/** 射线法：点是否在单个环内 */
export function pointInRing(p: Vec2, ring: Ring): boolean {
  let inside = false;
  const n = ring.length;
  for (let i = 0, j = n - 1; i < n; j = i++) {
    const a = ring[i];
    const b = ring[j];
    const intersects =
      a.y > p.y !== b.y > p.y &&
      p.x < ((b.x - a.x) * (p.y - a.y)) / (b.y - a.y) + a.x;
    if (intersects) inside = !inside;
  }
  return inside;
}

/** 奇偶规则：自动处理洞 */
export function pointInPaths(p: Vec2, paths: Paths): boolean {
  let inside = false;
  for (const r of paths) if (pointInRing(p, r)) inside = !inside;
  return inside;
}

export function closestPointOnSegment(p: Vec2, a: Vec2, b: Vec2): { point: Vec2; t: number; dist: number } {
  const ab = sub(b, a);
  const l2 = ab.x * ab.x + ab.y * ab.y;
  if (l2 < 1e-12) return { point: a, t: 0, dist: dist(p, a) };
  let t = ((p.x - a.x) * ab.x + (p.y - a.y) * ab.y) / l2;
  t = Math.max(0, Math.min(1, t));
  const point = lerp(a, b, t);
  return { point, t, dist: dist(p, point) };
}

/** 最近边（含投影点），用于 Edge Snap 与边选中 */
export function closestEdge(p: Vec2, paths: Paths): (Edge & { point: Vec2; t: number; dist: number }) | null {
  let best: (Edge & { point: Vec2; t: number; dist: number }) | null = null;
  for (const e of pathsEdges(paths)) {
    const c = closestPointOnSegment(p, e.a, e.b);
    if (!best || c.dist < best.dist) best = { ...e, ...c };
  }
  return best;
}

/** 线段是否真相交（不含仅端点接触） */
export function segmentsIntersect(a: Vec2, b: Vec2, c: Vec2, d: Vec2): boolean {
  const o1 = orient(a, b, c);
  const o2 = orient(a, b, d);
  const o3 = orient(c, d, a);
  const o4 = orient(c, d, b);
  return o1 * o2 < 0 && o3 * o4 < 0;
}

export function orient(a: Vec2, b: Vec2, c: Vec2): number {
  return (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x);
}

/** 自交检测：返回所有真交叉点对（用于 Invalid Geometry 反馈） */
export function selfIntersections(ring: Ring): { p: Vec2; i: number; j: number }[] {
  const edges = ringEdges(ring);
  const out: { p: Vec2; i: number; j: number }[] = [];
  for (let i = 0; i < edges.length; i++) {
    for (let j = i + 2; j < edges.length; j++) {
      if (i === 0 && j === edges.length - 1) continue; // 相邻边
      const e1 = edges[i];
      const e2 = edges[j];
      if (segmentsIntersect(e1.a, e1.b, e2.a, e2.b)) {
        out.push({ p: segmentIntersection(e1.a, e1.b, e2.a, e2.b) ?? mid(e1.a, e2.a), i, j });
      }
    }
  }
  return out;
}

/** 两线段交点；平行返回 null */
export function segmentIntersection(a: Vec2, b: Vec2, c: Vec2, d: Vec2): Vec2 | null {
  const r = sub(b, a);
  const s = sub(d, c);
  const denom = r.x * s.y - r.y * s.x;
  if (Math.abs(denom) < 1e-12) return null;
  const t = ((c.x - a.x) * s.y - (c.y - a.y) * s.x) / denom;
  const u = ((c.x - a.x) * r.y - (c.y - a.y) * r.x) / denom;
  if (t < 0 || t > 1 || u < 0 || u > 1) return null;
  return { x: a.x + r.x * t, y: a.y + r.y * t };
}

/** 去掉重合/共线点，保证几何干净 */
export function cleanRing(ring: Ring, eps = 1e-6): Ring {
  let pts = ring.slice();
  // 去重相邻
  const dedup: Ring = [];
  for (const p of pts) {
    if (!dedup.length || dist(dedup[dedup.length - 1], p) > eps) dedup.push(p);
  }
  if (dedup.length > 1 && dist(dedup[0], dedup[dedup.length - 1]) <= eps) dedup.pop();
  pts = dedup;
  if (pts.length < 3) return pts;
  // 去共线
  const out: Ring = [];
  const n = pts.length;
  for (let i = 0; i < n; i++) {
    const prev = pts[(i - 1 + n) % n];
    const cur = pts[i];
    const next = pts[(i + 1) % n];
    const area = Math.abs(orient(prev, cur, next));
    const l = dist(prev, next);
    if (l < eps || area / l > eps) out.push(cur);
  }
  return out.length >= 3 ? out : pts;
}

export function ensureCCW(ring: Ring): Ring {
  return signedArea(ring) < 0 ? ring.slice().reverse() : ring;
}

export function rectRing(a: Vec2, b: Vec2): Ring {
  const minX = Math.min(a.x, b.x);
  const maxX = Math.max(a.x, b.x);
  const minY = Math.min(a.y, b.y);
  const maxY = Math.max(a.y, b.y);
  // CCW
  return [
    { x: minX, y: minY },
    { x: maxX, y: minY },
    { x: maxX, y: maxY },
    { x: minX, y: maxY },
  ];
}

export function circleRing(center: Vec2, radius: number, segments = 48): Ring {
  const out: Ring = [];
  for (let i = 0; i < segments; i++) {
    const a = (i / segments) * Math.PI * 2;
    out.push({ x: center.x + Math.cos(a) * radius, y: center.y + Math.sin(a) * radius });
  }
  return out;
}

/** 平移 / 仿射（供 Move、Rotate、Scale 复用） */
export function transformRing(ring: Ring, fn: (p: Vec2) => Vec2): Ring {
  return ring.map(fn);
}

export function translateRing(ring: Ring, d: Vec2): Ring {
  return ring.map((p) => ({ x: p.x + d.x, y: p.y + d.y }));
}

export function rotateRing(ring: Ring, origin: Vec2, angle: number): Ring {
  const c = Math.cos(angle);
  const s = Math.sin(angle);
  return ring.map((p) => {
    const dx = p.x - origin.x;
    const dy = p.y - origin.y;
    return { x: origin.x + dx * c - dy * s, y: origin.y + dx * s + dy * c };
  });
}

export function scaleRing(ring: Ring, origin: Vec2, sx: number, sy = sx): Ring {
  return ring.map((p) => ({
    x: origin.x + (p.x - origin.x) * sx,
    y: origin.y + (p.y - origin.y) * sy,
  }));
}

/** 折线（可含重复点）→ 边长与角度信息 */
export function polylineLegs(pts: Vec2[]): { from: Vec2; to: Vec2; length: number; angle: number }[] {
  const out: { from: Vec2; to: Vec2; length: number; angle: number }[] = [];
  for (let i = 0; i < pts.length - 1; i++) {
    out.push({
      from: pts[i],
      to: pts[i + 1],
      length: dist(pts[i], pts[i + 1]),
      angle: angleOf(pts[i], pts[i + 1]),
    });
  }
  return out;
}

export function polylineLength(pts: Vec2[]): number {
  return polylineLegs(pts).reduce((s, l) => s + l.length, 0);
}

/** 折线按半宽生成道路包络（2D Live Preview 用，不走 Clipper，速度优先） */
export function strokeEnvelope(centerline: Vec2[], halfWidth: number): Ring {
  if (centerline.length < 2) return [];
  const left: Vec2[] = [];
  const right: Vec2[] = [];
  for (let i = 0; i < centerline.length; i++) {
    const prev = centerline[Math.max(0, i - 1)];
    const next = centerline[Math.min(centerline.length - 1, i + 1)];
    const dx = next.x - prev.x;
    const dy = next.y - prev.y;
    const l = Math.hypot(dx, dy) || 1;
    const nx = -dy / l;
    const ny = dx / l;
    left.push({ x: centerline[i].x + nx * halfWidth, y: centerline[i].y + ny * halfWidth });
    right.push({ x: centerline[i].x - nx * halfWidth, y: centerline[i].y - ny * halfWidth });
  }
  // 统一成 CCW：Clipper(NonZero) 与 three 的 Shape 都要求外环逆时针
  return ensureCCW([...left, ...right.reverse()]);
}

/* ------------------------- 带洞多边形（环的分组） ------------------------- */

export interface RingGroup {
  outer: Ring;
  holes: Ring[];
}

/**
 * 环的内部代表点：形心若在环内直接用；否则退化为「边中点向形心靠拢 3%」的采样。
 * 凹环 / U 形环的形心可能落在环外，不能只信形心。
 */
export function interiorPoint(ring: Ring): Vec2 {
  const c = ringCentroid(ring);
  if (pointInRing(c, ring)) return c;
  for (let i = 0; i < ring.length; i++) {
    const a = ring[i];
    const b = ring[(i + 1) % ring.length];
    const mid = { x: (a.x + b.x) / 2, y: (a.y + b.y) / 2 };
    const p = { x: mid.x + (c.x - mid.x) * 0.03, y: mid.y + (c.y - mid.y) * 0.03 };
    if (pointInRing(p, ring)) return p;
  }
  return c;
}

/**
 * 把 Clipper 输出的环集合拆成「外环 + 洞」。
 *
 * 判据：**按面积从大到小**逐一寻找「包含它的最小环」作为父级，
 * 嵌套层数为偶数即外环、奇数即洞（even-odd 语义）。
 *
 * 只跟面积更大的环比包含关系是关键：洞的形心往往与外环形心重合
 * （回字形楼、带庭院的地块），用形心做双向包含测试会把外环也判成洞。
 */
export function groupRings(paths: Paths): RingGroup[] {
  const rings = paths.filter((r) => r.length >= 3);
  if (!rings.length) return [];

  const meta = rings.map((ring, i) => ({
    i,
    ring,
    area: Math.abs(signedArea(ring)),
    probe: interiorPoint(ring),
  }));

  const depth = new Array<number>(meta.length).fill(0);
  const parent = new Array<number>(meta.length).fill(-1);

  // 大 → 小：处理某个环时，它的所有候选父级都已算好层数
  const desc = [...meta].sort((a, b) => b.area - a.area);
  for (const m of desc) {
    let best = -1;
    for (const o of meta) {
      if (o.i === m.i || o.area <= m.area) continue;
      if (!pointInRing(m.probe, o.ring)) continue;
      if (best < 0 || o.area < meta[best].area) best = o.i;
    }
    parent[m.i] = best;
    depth[m.i] = best < 0 ? 0 : depth[best] + 1;
  }

  const groups: RingGroup[] = [];
  const slot = new Map<number, number>();
  meta.forEach((m) => {
    if (depth[m.i] % 2 === 0) {
      slot.set(m.i, groups.length);
      groups.push({ outer: m.ring, holes: [] });
    }
  });
  meta.forEach((m) => {
    if (depth[m.i] % 2 === 0) return;
    const s = slot.get(parent[m.i]);
    if (s !== undefined) groups[s].holes.push(m.ring);
  });

  return groups;
}

/** 环组 → Paths（even-odd 语义下 Clipper 能正确理解洞） */
export function groupToPaths(g: RingGroup): Paths {
  return [g.outer, ...g.holes];
}
