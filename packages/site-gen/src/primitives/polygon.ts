// 原语 1/4：多边形裁剪与内缩。
//
// 这两个是**所有区域类生成器的地基**：停车场要知道"哪块地能用"（内缩 + 避让），
// 广场要知道"中心区在哪"（再内缩一圈），花园要知道"沿边的种植带"（外环减内环）。
//
// 为什么自己做而不引 `polygon-clipping`：本包要求**零第三方依赖**（能直接 `node xxx.ts` 跑，
// 不依赖 node_modules）。而我们真正需要的运算只有两种：
//   · 半平面裁剪（Sutherland–Hodgman）—— 行带、退线、分割都靠它；
//   · 内缩（每条边平移 + 相邻边求交）。
// 布尔差集（挖洞）确实做不了，所以**凡是需要差集的地方一律改成"分层叠画"**：
// 先铺整块草地，再把沥青画在上面。这和 golden 的做法一致（golden 也是整块 grass + asphalt 覆盖），
// 所以不会引入口径差。

import { area as ringArea, bounds, isCCW, type Vec2 } from '../../../plan-ir/src/geom.ts';

/** 把环变成逆时针（内部一律按 CCW 处理，法向才有一致的"朝内"） */
export function ccwRing(ring: readonly Vec2[]): Vec2[] {
  return isCCW(ring) ? ring.map((p) => [p[0], p[1]] as Vec2) : ring.map((p) => [p[0], p[1]] as Vec2).reverse();
}

/**
 * 半平面裁剪：保留 `dot(p - at, n) >= 0` 的一侧。
 *
 * 这是 Sutherland–Hodgman 的一步。行带（沿街方向切条）、退线（把一侧推出去）、
 * 分割（一刀切两半）都只需要这一步。
 */
export function clipHalfPlane(ring: readonly Vec2[], at: Vec2, n: Vec2): Vec2[] {
  const out: Vec2[] = [];
  const inside = (p: Vec2): number => (p[0] - at[0]) * n[0] + (p[1] - at[1]) * n[1];
  for (let i = 0; i < ring.length; i++) {
    const a = ring[i];
    const b = ring[(i + 1) % ring.length];
    const da = inside(a);
    const db = inside(b);
    if (da >= 0) out.push([a[0], a[1]] as Vec2);
    if ((da > 0 && db < 0) || (da < 0 && db > 0)) {
      const t = da / (da - db);
      out.push([a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t]);
    }
  }
  return out;
}

/** 两条直线求交（参数式），平行返回 null */
function lineIntersect(p1: Vec2, d1: Vec2, p2: Vec2, d2: Vec2): Vec2 | null {
  const den = d1[0] * d2[1] - d1[1] * d2[0];
  if (Math.abs(den) < 1e-9) return null;
  const t = ((p2[0] - p1[0]) * d2[1] - (p2[1] - p1[1]) * d2[0]) / den;
  return [p1[0] + d1[0] * t, p1[1] + d1[1] * t];
}

/**
 * 内缩（偏移）一圈：每条边朝内平移 `dist`，相邻边求交得到新顶点。
 *
 * **只在凸/近凸多边形上可靠**。凹角、窄颈会在内缩后自交 —— 这里用"面积必须变小且不为 0"
 * 兜底：不满足就退回原环并把 `clamped` 标出来，不静默产出一个自交的怪环。
 * 真要处理凹多边形的内缩，得做直骨架（straight skeleton），那是另一个量级的工作（见 D-060）。
 */
export function offsetRing(
  ring: readonly Vec2[],
  dist: number,
): { ring: Vec2[]; offset: number; clamped: boolean } {
  const src = ccwRing(ring);
  if (src.length < 3 || dist === 0) return { ring: src, offset: 0, clamped: false };

  // CCW 环：沿边方向 (b-a) 的左法向就是"朝内"
  const n = src.length;
  const lines: { p: Vec2; d: Vec2 }[] = [];
  for (let i = 0; i < n; i++) {
    const a = src[i];
    const b = src[(i + 1) % n];
    const dx = b[0] - a[0];
    const dy = b[1] - a[1];
    const len = Math.hypot(dx, dy);
    if (len < 1e-9) continue;
    const ux = dx / len;
    const uy = dy / len;
    // 左法向 = (-uy, ux)
    lines.push({ p: [a[0] - uy * dist, a[1] + ux * dist], d: [ux, uy] });
  }
  if (lines.length < 3) return { ring: src, offset: 0, clamped: true };

  const out: Vec2[] = [];
  for (let i = 0; i < lines.length; i++) {
    const prev = lines[(i - 1 + lines.length) % lines.length];
    const cur = lines[i];
    const hit = lineIntersect(prev.p, prev.d, cur.p, cur.d);
    // 平行相邻边（共线折点）：直接用当前边的起点
    out.push(hit ?? ([cur.p[0], cur.p[1]] as Vec2));
  }

  const a0 = ringArea(src);
  const a1 = ringArea(out);
  const grown = a1 <= 1e-6 || a1 > a0 + 1e-6;
  // 内缩后自交的判据：面积掉得比"整条周长 × dist"还多，说明环翻了
  const perimeter = src.reduce((s, p, i) => {
    const q = src[(i + 1) % src.length];
    return s + Math.hypot(q[0] - p[0], q[1] - p[1]);
  }, 0);
  const overCut = a0 - a1 > perimeter * dist * 1.5;
  if (grown || overCut) return { ring: src, offset: 0, clamped: true };
  return { ring: out, offset: dist, clamped: false };
}

/** 环的包围盒 */
export function ringBounds(ring: readonly Vec2[]): { minX: number; minY: number; maxX: number; maxY: number } {
  return bounds(ring);
}

/** 环面积（m²，绝对值） */
export const ringAreaOf = (ring: readonly Vec2[]): number => ringArea(ring);

/**
 * 一刀切两半：返回 `dot(p-at,n)>=0` 那侧和另一侧。
 *
 * 用于"先按最长的边定朝向，再把地块切成行带"。比"逐行扫描求交点"更稳：
 * 出来的两块都是真正的多边形，不会出现重复顶点。
 */
export function splitRing(ring: readonly Vec2[], at: Vec2, n: Vec2): { pos: Vec2[]; neg: Vec2[] } {
  const neg: Vec2 = [-n[0], -n[1]];
  return { pos: clipHalfPlane(ring, at, n), neg: clipHalfPlane(ring, at, neg) };
}

/** 最长边：返回端点与方向。停车场/广场都用它定朝向（`rowAngleMode: 'edge'`）。 */
export function longestEdge(ring: readonly Vec2[]): { a: Vec2; b: Vec2; dir: Vec2; len: number } {
  let best = { a: ring[0], b: ring[1 % ring.length], len: -1 };
  for (let i = 0; i < ring.length; i++) {
    const a = ring[i];
    const b = ring[(i + 1) % ring.length];
    const len = Math.hypot(b[0] - a[0], b[1] - a[1]);
    if (len > best.len) best = { a, b, len };
  }
  const dx = best.b[0] - best.a[0];
  const dy = best.b[1] - best.a[1];
  const l = Math.hypot(dx, dy) || 1;
  return { a: best.a, b: best.b, dir: [dx / l, dy / l], len: best.len };
}
