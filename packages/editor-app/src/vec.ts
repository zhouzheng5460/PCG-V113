// 二维基础量。**用 {x, y} 对象而不是 [x, y] 元组**，和 PCG 原项目一致 ——
// 编辑器的几何代码里到处是 p.x / p.y，元组写法会让整份代码读起来像在解密。
// 交给生成算法（plan-ir / slots）时再转成元组，只在边界转换一次。

export interface Vec2 {
  x: number;
  y: number;
}

export const v2 = (x = 0, y = 0): Vec2 => ({ x, y });
export const clone = (p: Vec2): Vec2 => ({ x: p.x, y: p.y });
export const add = (a: Vec2, b: Vec2): Vec2 => ({ x: a.x + b.x, y: a.y + b.y });
export const sub = (a: Vec2, b: Vec2): Vec2 => ({ x: a.x - b.x, y: a.y - b.y });
export const mul = (a: Vec2, k: number): Vec2 => ({ x: a.x * k, y: a.y * k });
export const mid = (a: Vec2, b: Vec2): Vec2 => ({ x: (a.x + b.x) / 2, y: (a.y + b.y) / 2 });
export const dot = (a: Vec2, b: Vec2): number => a.x * b.x + a.y * b.y;
export const len = (a: Vec2): number => Math.hypot(a.x, a.y);
export const dist = (a: Vec2, b: Vec2): number => Math.hypot(a.x - b.x, a.y - b.y);

export function norm(a: Vec2): Vec2 {
  const l = Math.hypot(a.x, a.y) || 1;
  return { x: a.x / l, y: a.y / l };
}

/** 左法向（逆时针转 90°）。道路包络、边界推墙都用它。 */
export const perp = (a: Vec2): Vec2 => ({ x: -a.y, y: a.x });

export const DEG = 180 / Math.PI;
export const RAD = Math.PI / 180;

/** 从 a 指向 b 的方位角（弧度，0 = +X）。 */
export const angleOf = (a: Vec2, b: Vec2): number => Math.atan2(b.y - a.y, b.x - a.x);

/** 绕原点旋转。 */
export function rotate(p: Vec2, rad: number): Vec2 {
  const c = Math.cos(rad);
  const s = Math.sin(rad);
  return { x: p.x * c - p.y * s, y: p.x * s + p.y * c };
}

/** 绕 c 旋转。 */
export function rotateAround(p: Vec2, c: Vec2, rad: number): Vec2 {
  const r = rotate(sub(p, c), rad);
  return { x: r.x + c.x, y: r.y + c.y };
}

/** 点到线段距离。命中测试用它，比点到直线距离可靠（否则延长线上的点也算命中）。 */
export function distToSegment(p: Vec2, a: Vec2, b: Vec2): number {
  const abx = b.x - a.x;
  const aby = b.y - a.y;
  const l2 = abx * abx + aby * aby;
  if (l2 === 0) return dist(p, a);
  let t = ((p.x - a.x) * abx + (p.y - a.y) * aby) / l2;
  t = Math.max(0, Math.min(1, t));
  return Math.hypot(p.x - (a.x + t * abx), p.y - (a.y + t * aby));
}

/** 把向量吸附到最近的 45° 倍数（Shift 约束）。 */
export function snapAngle(from: Vec2, to: Vec2, stepDeg = 45): Vec2 {
  const d = sub(to, from);
  const l = len(d);
  if (l < 1e-9) return clone(to);
  const a = Math.round(Math.atan2(d.y, d.x) / (stepDeg * RAD)) * stepDeg * RAD;
  return { x: from.x + Math.cos(a) * l, y: from.y + Math.sin(a) * l };
}
