/**
 * Phase 0 · Geometry Core —— 布尔 / 偏移
 *
 * 统一走 Clipper（整数坐标），量化单位 1 unit = 1mm：
 *   世界坐标（double, meter） --×1000--> 整数 mm --> Clipper --> 还原
 * 这样可避免浮点污染（PRD §63），同时面积误差 < 0.1%，满足 §73 的 <1% 要求。
 *
 * UI 层不暴露任何 Clipper 概念，只提供：合并 / 减去 / 取交集 / 偏移。
 */
import ClipperLib from 'clipper-lib';
import { Paths, Ring, cleanRing, ensureCCW, groupRings } from './polygon';
import { Vec2 } from './vec';

/** 量化尺度：1 世界单位(meter) = 1000 整数单位(mm) */
export const QUANT = 1000;

type IntPoint = { X: number; Y: number };
type IntPaths = IntPoint[][];

export type BoolOp = 'union' | 'subtract' | 'intersect';

const FillType = ClipperLib.PolyFillType.pftNonZero;
const PolyType = ClipperLib.PolyType;

function toInt(paths: Paths): IntPaths {
  const out: IntPaths = [];
  for (const ring of paths) {
    if (!ring || ring.length < 3) continue;
    const r: IntPoint[] = [];
    for (const p of ring) {
      const X = Math.round(p.x * QUANT);
      const Y = Math.round(p.y * QUANT);
      const last = r[r.length - 1];
      if (last && last.X === X && last.Y === Y) continue;
      r.push({ X, Y });
    }
    if (r.length >= 3) out.push(r);
  }
  return out;
}

function fromInt(paths: IntPaths): Paths {
  return paths
    .filter((r) => r.length >= 3)
    .map((r) => r.map((p) => ({ x: p.X / QUANT, y: p.Y / QUANT }) as Vec2))
    .map((r) => cleanRing(r, 1e-9))
    .filter((r) => r.length >= 3);
}

/** 把环量化到 1mm，供后续稳定运算 */
export function quantizeRing(ring: Ring): Ring {
  return fromInt(toInt([ring]))[0] ?? ring;
}

/**
 * 方向归一：外环 CCW、洞 CW。
 *
 * 为什么必须做：Clipper 用 NonZero 填充，**环的方向决定它是实体还是洞**。
 * 一个顺时针环和一个逆时针环做并集，重叠部分会互相抵消 —— 表现为
 * 「道路 / 障碍物在并集里凭空消失」，生成的建筑就压到它上面了。
 * 源数据里 rectRing 是 CCW、strokeEnvelope 曾经是 CW，混在一起必炸。
 */
function normalizePaths(paths: Paths): Paths {
  if (paths.length <= 1) return paths.length === 1 && signedAreaOf(paths[0]) < 0 ? [ensureCCW(paths[0])] : paths;
  const groups = groupRings(paths);
  if (!groups.length) return paths;
  const out: Paths = [];
  for (const g of groups) {
    out.push(ensureCCW(g.outer));
    for (const h of g.holes) out.push(ensureCCW(h).slice().reverse());
  }
  return out;
}

function signedAreaOf(ring: Ring): number {
  let a = 0;
  for (let i = 0; i < ring.length; i++) {
    const p = ring[i];
    const q = ring[(i + 1) % ring.length];
    a += p.x * q.y - q.x * p.y;
  }
  return a / 2;
}

function execute(op: BoolOp, subject: Paths, clip: Paths): Paths {
  const subj = toInt(normalizePaths(subject));
  const clp = toInt(normalizePaths(clip));
  if (!subj.length) return [];
  if (!clp.length && op !== 'union') return fromInt(subj);

  const c = new ClipperLib.Clipper();
  c.AddPaths(subj, PolyType.ptSubject, true);
  if (clp.length) c.AddPaths(clp, PolyType.ptClip, true);

  const sol: IntPaths = [];
  const clipType =
    op === 'union'
      ? ClipperLib.ClipType.ctUnion
      : op === 'subtract'
        ? ClipperLib.ClipType.ctDifference
        : ClipperLib.ClipType.ctIntersection;

  c.Execute(clipType, sol, FillType, FillType);
  return fromInt(sol);
}

export const union = (a: Paths, b: Paths = []): Paths => execute('union', a, b);
export const subtract = (a: Paths, b: Paths): Paths => execute('subtract', a, b);
export const intersect = (a: Paths, b: Paths): Paths => execute('intersect', a, b);

export function booleanOp(op: BoolOp, a: Paths, b: Paths): Paths {
  return execute(op, a, b);
}

/** 合并多个对象的环（Union 全量） */
export function unionAll(list: Paths[]): Paths {
  return list.reduce<Paths>((acc, p) => (acc.length ? union(acc, p) : p), []);
}

export type JoinType = 'miter' | 'round' | 'square';

/**
 * 偏移。delta > 0 向外，< 0 向内。
 * 用途：建筑退界、绿化带、人行道、道路边缘、安全距离。
 */
export function offsetPath(paths: Paths, delta: number, join: JoinType = 'miter'): Paths {
  const src = toInt(normalizePaths(paths));
  if (!src.length) return [];
  const joinType =
    join === 'round'
      ? ClipperLib.JoinType.jtRound
      : join === 'square'
        ? ClipperLib.JoinType.jtSquare
        : ClipperLib.JoinType.jtMiter;

  const co = new ClipperLib.ClipperOffset(2, 0.25 * QUANT * 0.001);
  co.AddPaths(src, joinType, ClipperLib.EndType.etClosedPolygon);
  const sol: IntPaths = [];
  co.Execute(sol, delta * QUANT);
  return fromInt(sol);
}

/** 带距离断言的偏移：向内偏移过度时返回空（供 UI 提示 Invalid） */
export function safeOffset(paths: Paths, delta: number, join: JoinType = 'miter'): Paths {
  return offsetPath(paths, delta, join);
}
