/**
 * 世界坐标基础算子。
 *
 * 单位：meter
 *   x = East / West
 *   y = North / South   （2D 视图中 y 向上为北）
 * Three.js 映射：(x, 0, y)
 */

export interface Vec2 {
  x: number;
  y: number;
}

export const v2 = (x = 0, y = 0): Vec2 => ({ x, y });

export const add = (a: Vec2, b: Vec2): Vec2 => ({ x: a.x + b.x, y: a.y + b.y });
export const sub = (a: Vec2, b: Vec2): Vec2 => ({ x: a.x - b.x, y: a.y - b.y });
export const mul = (a: Vec2, s: number): Vec2 => ({ x: a.x * s, y: a.y * s });
export const neg = (a: Vec2): Vec2 => ({ x: -a.x, y: -a.y });

export const dot = (a: Vec2, b: Vec2): number => a.x * b.x + a.y * b.y;
export const cross = (a: Vec2, b: Vec2): number => a.x * b.y - a.y * b.x;

export const len = (a: Vec2): number => Math.hypot(a.x, a.y);
export const dist = (a: Vec2, b: Vec2): number => Math.hypot(a.x - b.x, a.y - b.y);
export const dist2 = (a: Vec2, b: Vec2): number => (a.x - b.x) ** 2 + (a.y - b.y) ** 2;

export const lerp = (a: Vec2, b: Vec2, t: number): Vec2 => ({
  x: a.x + (b.x - a.x) * t,
  y: a.y + (b.y - a.y) * t,
});

export const mid = (a: Vec2, b: Vec2): Vec2 => lerp(a, b, 0.5);

export function normalize(a: Vec2): Vec2 {
  const l = len(a);
  return l < 1e-12 ? { x: 0, y: 0 } : { x: a.x / l, y: a.y / l };
}

/** 逆时针旋转（数学正向） */
export function rotate(a: Vec2, rad: number): Vec2 {
  const c = Math.cos(rad);
  const s = Math.sin(rad);
  return { x: a.x * c - a.y * s, y: a.x * s + a.y * c };
}

/** 左法线 */
export const perp = (a: Vec2): Vec2 => ({ x: -a.y, y: a.x });

/** 从 a 指向 b 的角度，范围 (-PI, PI] */
export const angleOf = (a: Vec2, b: Vec2): number => Math.atan2(b.y - a.y, b.x - a.x);

export const deg = (rad: number): number => (rad * 180) / Math.PI;
export const rad = (d: number): number => (d * Math.PI) / 180;

export const eq = (a: Vec2, b: Vec2, eps = 1e-6): boolean =>
  Math.abs(a.x - b.x) <= eps && Math.abs(a.y - b.y) <= eps;

/** 方向吸附到 n 等分角（用于 Shift 角度约束） */
export function snapAngle(a: Vec2, origin: Vec2, stepDeg: number): Vec2 {
  const d = sub(a, origin);
  const l = len(d);
  if (l < 1e-9) return a;
  const ang = Math.atan2(d.y, d.x);
  const step = rad(stepDeg);
  const snapped = Math.round(ang / step) * step;
  return { x: origin.x + Math.cos(snapped) * l, y: origin.y + Math.sin(snapped) * l };
}
