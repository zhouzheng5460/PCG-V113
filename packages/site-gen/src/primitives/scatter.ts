// 原语 2/4：散布。沿边 / 面内。
//
// 这一对覆盖了 Placemaker 里绝大多数"种东西"的行为：行道树、灌木带、路灯、长椅、车位标记……
// 它们看起来是十几种生成器，底层都是"沿一条边按间距摆"或者"在一张面里按密度撒"。
//
// 两条必须守住的性质：
//   1. **确定性**：同一个 seed 必须给出同一串点。所以 rng 是自己写的（sfc32），不用 Math.random。
//   2. **不越界**：撒进去的每一点都要过 `pointInRing`。这一条比"分布均匀"重要得多 ——
//      树长到地块外面、苗圃压到路上，都是"一眼可见"的错（用户已经反馈过两次）。

import { pointInRing, type Vec2 } from '../../../plan-ir/src/geom.ts';

/**
 * 确定性伪随机（sfc32）。为什么不直接用 `Math.random`：
 * 生成器必须是纯函数 —— 同一份输入两次调用给出同一份 Plan，否则快照没法当基准（D-057）。
 */
export function rngOf(seed: number): () => number {
  let a = (seed >>> 0) || 1;
  let b = (seed * 1664525 + 1013904223) >>> 0;
  let c = (seed * 22695477 + 1) >>> 0;
  let d = 1;
  return (): number => {
    a >>>= 0;
    b >>>= 0;
    c >>>= 0;
    d >>>= 0;
    let t = (a + b) | 0;
    a = b ^ (b >>> 9);
    b = (c + (c << 3)) | 0;
    c = (c << 21) | (c >>> 11);
    d = (d + 1) | 0;
    t = (t + d) | 0;
    c = (c + t) | 0;
    return ((t >>> 0) % 1000000) / 1000000;
  };
}

/** 环的累计边长（沿边撒点要按弧长，不按顶点） */
function edgeLengths(ring: readonly Vec2[]): { lens: number[]; total: number } {
  const lens: number[] = [];
  let total = 0;
  for (let i = 0; i < ring.length; i++) {
    const a = ring[i];
    const b = ring[(i + 1) % ring.length];
    const l = Math.hypot(b[0] - a[0], b[1] - a[1]);
    lens.push(l);
    total += l;
  }
  return { lens, total };
}

/** 按弧长取环上的一点，同时给出该点处的切线 */
export function pointAtLength(
  ring: readonly Vec2[],
  s: number,
): { p: Vec2; dir: Vec2; edge: number } | null {
  const { lens, total } = edgeLengths(ring);
  if (total <= 1e-9) return null;
  let d = ((s % total) + total) % total;
  for (let i = 0; i < ring.length; i++) {
    if (d <= lens[i] || i === ring.length - 1) {
      const a = ring[i];
      const b = ring[(i + 1) % ring.length];
      const t = lens[i] > 1e-9 ? d / lens[i] : 0;
      const p: Vec2 = [a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t];
      const dl = lens[i] || 1;
      return { p, dir: [(b[0] - a[0]) / dl, (b[1] - a[1]) / dl], edge: i };
    }
    d -= lens[i];
  }
  return null;
}

export interface ScatterAlongOptions {
  /** 间距（米） */
  spacing: number;
  /** 从边界往里偏多少米（负值 = 往外） */
  inset?: number;
  /** 位置抖动（米），0 = 完全规整 */
  jitter?: number;
  /** 沿法向的抖动（米） */
  jitterNormal?: number;
  /** 起点偏移（米），用来错开相邻两排 */
  phase?: number;
  /** 只在这些边段上撒（下标），不给则全部 */
  edges?: number[];
  /** 丢弃比例（0~1），做"稀疏段落"用 */
  skip?: number;
}

/**
 * 沿边散布：按弧长等距取点，可选抖动。
 *
 * 为什么按弧长而不是按顶点：地块边数不定（实测 4~27 个顶点），按顶点会在长边上留大空隙。
 */
export function scatterAlongEdge(
  ring: readonly Vec2[],
  opts: ScatterAlongOptions,
  rng: () => number,
): { p: Vec2; dir: Vec2; normal: Vec2 }[] {
  const { lens, total } = edgeLengths(ring);
  if (total <= 1e-9 || opts.spacing <= 0) return [];
  const inset = opts.inset ?? 0;
  const jitter = opts.jitter ?? 0;
  const jn = opts.jitterNormal ?? 0;
  const skip = opts.skip ?? 0;
  const phase = opts.phase ?? 0;

  const out: { p: Vec2; dir: Vec2; normal: Vec2 }[] = [];
  // 逐边铺（这样"只在这些边上撒"才有意义，且不会跨顶点丢点）
  let s0 = 0;
  for (let i = 0; i < ring.length; i++) {
    const L = lens[i];
    if (L <= 1e-9 || (opts.edges && !opts.edges.includes(i))) {
      s0 += L;
      continue;
    }
    const a = ring[i];
    const b = ring[(i + 1) % ring.length];
    const ux = (b[0] - a[0]) / L;
    const uy = (b[1] - a[1]) / L;
    // CCW 环的左法向朝内
    const nx = -uy;
    const ny = ux;
    const count = Math.max(1, Math.floor(L / opts.spacing));
    const pad = (L - (count - 1) * opts.spacing) / 2;
    for (let k = 0; k < count; k++) {
      if (skip > 0 && rng() < skip) continue;
      let along = pad + k * opts.spacing + phase;
      let normal = inset;
      if (jitter) along += (rng() * 2 - 1) * jitter;
      if (jn) normal += (rng() * 2 - 1) * jn;
      const px = a[0] + ux * along + nx * normal;
      const py = a[1] + uy * along + ny * normal;
      out.push({ p: [px, py], dir: [ux, uy], normal: [nx, ny] });
    }
    s0 += L;
  }
  void s0;
  return out;
}

export interface ScatterInsideOptions {
  /** 目标密度（每 1000 m² 几个） */
  densityPer1000?: number;
  /** 最小间距（米）；给了就按"抖动网格"近似泊松盘 */
  minDist?: number;
  /** 离边界至少多远（米） */
  margin?: number;
  /** 额外要排除的环（比如沥青面），落在里面的点丢掉 */
  avoid?: readonly Vec2[][];
  /** 最多几个（防止密度参数写错时炸内存） */
  max?: number;
}

/**
 * 面内散布：抖动网格。
 *
 * 为什么不用真正的泊松盘采样（Bridson）：抖动网格的视觉密度和它几乎一样，
 * 但代码量是 1/5，而且**密度可控**（`densityPer1000` 直接映射到格距，不是靠拒绝采样碰运气）。
 * 需要严格最小间距时用 `minDist` 覆盖格距即可。
 */
export function scatterInside(
  ring: readonly Vec2[],
  opts: ScatterInsideOptions,
  rng: () => number,
): Vec2[] {
  const areaM2 = Math.abs(ringArea(ring));
  if (areaM2 <= 1e-6) return [];
  const want = opts.max ?? 4000;
  let grid: number;
  if (opts.minDist && opts.minDist > 0) {
    grid = opts.minDist;
  } else {
    const d = opts.densityPer1000 ?? 10;
    // 每格一个点 → 格数 = 密度 × 面积 / 1000
    const n = Math.max(1, (d * areaM2) / 1000);
    grid = Math.sqrt(areaM2 / n);
  }
  if (!Number.isFinite(grid) || grid <= 0.02) return [];
  // 抖动幅度：半格，保证同格内不重叠但能打破网格感
  const jit = grid * 0.42;

  let minX = Infinity;
  let minY = Infinity;
  let maxX = -Infinity;
  let maxY = -Infinity;
  for (const p of ring) {
    if (p[0] < minX) minX = p[0];
    if (p[1] < minY) minY = p[1];
    if (p[0] > maxX) maxX = p[0];
    if (p[1] > maxY) maxY = p[1];
  }

  const out: Vec2[] = [];
  const margin = opts.margin ?? 0;
  for (let y = minY + grid / 2; y <= maxY; y += grid) {
    for (let x = minX + grid / 2; x <= maxX; x += grid) {
      const p: Vec2 = [x + (rng() * 2 - 1) * jit, y + (rng() * 2 - 1) * jit];
      if (!pointInRing(p, ring)) continue;
      // 退线：到边界不足 margin 的点一律丢掉（树压到路上就是这样发生的）
      if (margin > 0 && distToRing(p, ring) < margin) continue;
      if (opts.avoid && opts.avoid.some((a) => pointInRing(p, a))) continue;
      out.push(p);
      if (out.length >= want) return out;
    }
  }
  return out;
}

/** 点到环的最近距离（米） */
export function distToRing(p: Vec2, ring: readonly Vec2[]): number {
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

function ringArea(ring: readonly Vec2[]): number {
  let a = 0;
  for (let i = 0; i < ring.length; i++) {
    const p = ring[i];
    const q = ring[(i + 1) % ring.length];
    a += p[0] * q[1] - q[0] * p[1];
  }
  return a / 2;
}
