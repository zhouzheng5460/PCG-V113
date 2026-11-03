// 原语 0/4：**多边形布尔运算**。整个包里唯一允许 import `polygon-clipping` 的文件。
//
// 为什么不自己写扫描线（D-061）：缺的不是"会做差集"，而是"在凹多边形、MultiPolygon、洞、
// 共线边、贴边、浮点误差下仍然稳定的差集"。那是一个需要长期维护的内核，不是一个下午的活。
// 所以内核交给成熟实现（Martinez 类算法），**我拥有的是**：
//   · 对外契约（Ring / PolygonRegion / 三个函数）
//   · 输入归一化（绕序、坐标量化、退化环剔除）
//   · **输出规范化 + 稳定排序** ← 这一条是硬要求，见下
//   · 六个边角用例的验收
//
// 为什么"输出规范化"是硬要求：`tools/golden/` 的 54 份快照是靠**逐字节稳定**建立起来的
// （`emit.mjs` 每份 emit 两次比对）。布尔库返回的 MultiPolygon 里，环的**起点**和**顺序**
// 每次可能不同 —— 只要它进了 Plan，快照就会随机漂移，整个 golden 基准作废。
// 所以出口一律做三件事：
//   1. 外环逆时针、洞顺时针（绕序归一）
//   2. 每个环旋到"字典序最小的顶点"作为起点（起点归一）
//   3. 环之间按 (起点, 面积) 稳定排序（顺序归一）
// 归一之后，同一份输入必然得到同一份输出，快照才有意义。
//
// 换内核（WASM Clipper / Clipper2）时只需改这个文件的下半部分，上层 Generator 一行不用动。

import { createRequire } from 'node:module';
import { area as ringArea, isCCW, type Vec2 } from '../../../plan-ir/src/geom.ts';

/**
 * 内核加载方式：`polygon-clipping` 的 ESM 产物把 `splaytree` / `robust-predicates` 留作外部依赖，
 * 而本仓库**没有 install 步骤**（`node xxx.ts` 直接跑）。所以收的是 **UMD 单文件**（依赖已打包，29 KB），
 * 用 `createRequire` 加载。换内核时只改这里的三行。
 *
 * 注意：如果将来这个包要进打包器（vite/rollup），把这里换成
 * `import pc from 'polygon-clipping'` 并加一条依赖即可 —— 上层契约不受影响。
 */
const require = createRequire(import.meta.url);
interface BooleanKernel {
  difference: (a: number[][][][], b: number[][][][]) => number[][][][];
  union: (...a: number[][][][]) => number[][][][];
  intersection: (a: number[][][][], b: number[][][][]) => number[][][][];
}
const pc: BooleanKernel = require('../../vendor/polygon-clipping/index.cjs');

export type Ring = Vec2[];

/** 一个多边形区域：外环 + 可选洞。**不要**把环首尾重复点带进来。 */
export interface PolygonRegion {
  outer: Ring;
  holes?: Ring[];
}

/** 坐标量化步长（米）。0.001 m = 1 mm，远小于任何有意义的几何精度。 */
const SNAP = 1e-3;

/** 面积下限（m²）：小于它的环当噪声丢掉。1e-4 m² = 1 cm²。 */
const AREA_EPS = 1e-4;

type XY = [number, number];

const snapV = (v: number): number => Math.round(v / SNAP) * SNAP;

/** 去掉首尾重复点 + 量化坐标。 */
function cleanRing(ring: readonly Vec2[]): Ring {
  const out: Ring = [];
  for (const p of ring) {
    const q: Vec2 = [snapV(p[0]), snapV(p[1])];
    const last = out[out.length - 1];
    if (last && last[0] === q[0] && last[1] === q[1]) continue;
    out.push(q);
  }
  while (out.length > 1) {
    const f = out[0];
    const l = out[out.length - 1];
    if (f[0] === l[0] && f[1] === l[1]) out.pop();
    else break;
  }
  return out;
}

/** 绕序归一：`wantCCW` 为真则逆时针。 */
function orient(ring: Ring, wantCCW: boolean): Ring {
  const isCcw = isCCW(ring);
  return isCcw === wantCCW ? ring : ring.slice().reverse();
}

/** 起点归一：旋到字典序最小的顶点（同一环的任何等价起点都收敛到同一个）。 */
function rotateToMin(ring: Ring): Ring {
  let best = 0;
  for (let i = 1; i < ring.length; i++) {
    const a = ring[i];
    const b = ring[best];
    if (a[0] < b[0] || (a[0] === b[0] && a[1] < b[1])) best = i;
  }
  return best === 0 ? ring : ring.slice(best).concat(ring.slice(0, best));
}

/** 一个环的稳定排序键：起点 + 面积 + 点数（三者组合足以区分，且完全由几何决定）。 */
function ringKey(ring: Ring): string {
  const a = Math.abs(ringArea(ring)).toFixed(3);
  return `${ring[0][0].toFixed(3)}|${ring[0][1].toFixed(3)}|${a}|${ring.length}`;
}

/**
 * 输出规范化：绕序 / 起点 / 顺序三重归一 + 退化环剔除。
 *
 * 这是本文件存在的**主要理由**。没有它，`difference` 的结果会随实现细节漂移，
 * golden 快照就不再可比。
 */
function normalizeOut(polys: readonly Ring[][]): PolygonRegion[] {
  const out: PolygonRegion[] = [];
  for (const poly of polys) {
    if (!poly.length) continue;
    const outer = rotateToMin(orient(cleanRing(poly[0]), true));
    if (outer.length < 3 || Math.abs(ringArea(outer)) < AREA_EPS) continue;
    const holes: Ring[] = [];
    for (let i = 1; i < poly.length; i++) {
      const h = rotateToMin(orient(cleanRing(poly[i]), false));
      if (h.length < 3 || Math.abs(ringArea(h)) < AREA_EPS) continue;
      holes.push(h);
    }
    // 洞也排序：否则多个洞的先后顺序由库决定
    holes.sort((a, b) => (ringKey(a) < ringKey(b) ? -1 : 1));
    out.push(holes.length ? { outer, holes } : { outer });
  }
  out.sort((a, b) => (ringKey(a.outer) < ringKey(b.outer) ? -1 : 1));
  return out;
}

/**
 * 我自己的契约 → 库的输入（MultiPolygon = Polygon[]，环要闭合即首尾相同）。
 *
 * **退化在这里挡住**：点数 < 3、或面积 ≈ 0（三点共线、全等点）的环一律丢。
 * 不挡的话内核会直接抛 `Input geometry is not a valid Polygon or MultiPolygon`
 * —— 而那种异常往往出现在"某块地退化成一条线"的边角用例上，不该让整个生成器崩。
 * 外环被丢掉时整个区域一起丢（洞无从挂靠）。
 */
function toInput(regions: readonly PolygonRegion[]): XY[][][] {
  const out: XY[][][] = [];
  for (const r of regions) {
    const raw = cleanRing(r.outer);
    if (raw.length < 3 || Math.abs(ringArea(raw)) < AREA_EPS) continue;
    // **入口也要归一绕序**：外环 CCW、洞 CW。
    // 不归一的话，方向相反的输入（比如 C# 那边给出的障碍物四边形）会被内核按"洞"解释，
    // 于是 `difference` 什么都不减 —— 实测 plaza 带障碍物那两例因此覆盖 174.9%、障碍重叠 74.85%。
    const outer = orient(raw, true);
    const rings: XY[][] = [[...outer.map(([x, y]) => [x, y] as XY), [outer[0][0], outer[0][1]] as XY]];
    for (const h of r.holes ?? []) {
      const ch = cleanRing(h);
      if (ch.length < 3 || Math.abs(ringArea(ch)) < AREA_EPS) continue;
      const hh = orient(ch, false);
      rings.push([...hh.map(([x, y]) => [x, y] as XY), [hh[0][0], hh[0][1]] as XY]);
    }
    out.push(rings);
  }
  return out;
}

/** 库的输出 → 我的契约 */
function fromOutput(res: XY[][][]): PolygonRegion[] {
  return normalizeOut(res.map((poly) => poly.map((ring) => ring.map(([x, y]) => [x, y] as Vec2))));
}

/**
 * `subject − cutters`：从主体里挖掉所有 cutter。
 *
 * 用途（按落地顺序）：地块减去铺装 → 草岛；地块减去建筑轮廓 → 禁建区；
 * 广场减去中心景 → 环形铺装。
 */
export function differenceRegion(subject: PolygonRegion, cutters: readonly PolygonRegion[]): PolygonRegion[] {
  const a = toInput([subject]);
  if (!a.length) return [];
  const b = toInput(cutters);
  // cutter 全退化 → 原样返回（走归一化，保证输出形态一致）
  if (!b.length) return normalizeOut(a as unknown as Ring[][]);
  return fromOutput(pc.difference(a as never, b as never) as unknown as XY[][][]);
}

/** 并集：多个区域合成若干互不重叠的区域（重叠部分只算一次）。 */
export function unionRegions(regions: readonly PolygonRegion[]): PolygonRegion[] {
  const input = toInput(regions);
  if (!input.length) return [];
  if (input.length === 1) return normalizeOut(input as unknown as Ring[][]);
  return fromOutput(pc.union(input[0] as never, ...(input.slice(1) as never[])) as unknown as XY[][][]);
}

/** 交集：两块地的公共部分。 */
export function intersectRegion(a: PolygonRegion, b: PolygonRegion): PolygonRegion[] {
  const ia = toInput([a]);
  const ib = toInput([b]);
  if (!ia.length || !ib.length) return [];
  return fromOutput(pc.intersection(ia as never, ib as never) as unknown as XY[][][]);
}

/** 面积（m²，含洞抵扣）。 */
export function regionArea(r: PolygonRegion): number {
  const outer = Math.abs(ringArea(r.outer));
  const holes = (r.holes ?? []).reduce((s, h) => s + Math.abs(ringArea(h)), 0);
  return Math.max(0, outer - holes);
}

/** 把区域摊平成"只取外环"的环列表（需要渲染成单一简单环时用；洞会丢，慎用）。 */
export function outerRings(regions: readonly PolygonRegion[]): Ring[] {
  return regions.map((r) => r.outer);
}

export { snapV as snapCoordinate };
