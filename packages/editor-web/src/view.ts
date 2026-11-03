// 交互画布的坐标变换 + SVG 拼装。
//
// 与 packages/demo/src/render.ts 的关系：那个是**静态出图**（字符串进字符串出，不响应事件），
// 这里是**可交互**版本。变换规则刻意保持一致（世界 y 朝北、SVG y 朝下、只翻一次），
// 这样截图与静态报告能直接对照，出问题时不用先怀疑是不是两套坐标。
//
// 只做两件事：世界↔屏幕的换算，以及把图元拼成 SVG 字符串。
// 规则判定、状态管理一概不在这里 —— 那些在 app.ts，保证渲染层可以整体替换。

import type { Vec2 } from '../../plan-ir/src/geom.ts';

export interface Bounds {
  minX: number;
  minY: number;
  maxX: number;
  maxY: number;
}

export function boundsOf(pts: readonly Vec2[]): Bounds {
  if (pts.length === 0) return { minX: 0, minY: 0, maxX: 1, maxY: 1 };
  let minX = Infinity;
  let minY = Infinity;
  let maxX = -Infinity;
  let maxY = -Infinity;
  for (const p of pts) {
    if (p[0] < minX) minX = p[0];
    if (p[0] > maxX) maxX = p[0];
    if (p[1] < minY) minY = p[1];
    if (p[1] > maxY) maxY = p[1];
  }
  return { minX, minY, maxX, maxY };
}

/** 世界坐标 ↔ 屏幕坐标。padding 单位是像素。 */
export class View {
  readonly bb: Bounds;
  readonly scale: number;
  readonly pad: number;
  readonly W: number;
  readonly H: number;

  constructor(bb: Bounds, pad = 26, targetW = 720) {
    this.bb = bb;
    this.pad = pad;
    const w = Math.max(1, bb.maxX - bb.minX);
    const h = Math.max(1, bb.maxY - bb.minY);
    this.scale = Math.min((targetW - pad * 2) / w, 1e6);
    this.W = targetW;
    this.H = Math.max(180, Math.round(h * this.scale + pad * 2));
  }

  /** 世界 → 屏幕 x */
  sx(x: number): number {
    return this.pad + (x - this.bb.minX) * this.scale;
  }

  /** 世界 → 屏幕 y。世界 y 是"北"，SVG y 向下 → 只在这里翻一次。 */
  sy(y: number): number {
    return this.pad + (this.bb.maxY - y) * this.scale;
  }

  /** 屏幕 → 世界。命中测试用。 */
  toWorld(px: number, py: number): Vec2 {
    return [(px - this.pad) / this.scale + this.bb.minX, this.bb.maxY - (py - this.pad) / this.scale];
  }

  pt(p: Vec2): Vec2 {
    return [this.sx(p[0]), this.sy(p[1])];
  }
}

const r1 = (n: number): string => n.toFixed(1);

export function polyPath(pts: readonly Vec2[]): string {
  if (pts.length === 0) return '';
  return pts.map((p, i) => `${i === 0 ? 'M' : 'L'}${r1(p[0])} ${r1(p[1])}`).join(' ') + ' Z';
}

/** 一条街在屏幕上的两个端点 + 像素宽度（按真实路宽缩放，最少 3px 否则看不见）。 */
export interface ScreenStreet {
  x1: number;
  y1: number;
  x2: number;
  y2: number;
  wpx: number;
  category: string;
}

export const STREET_FILL: Record<string, string> = {
  road: '#4a4b55',
  pathway: '#3d4a3f',
  track: '#4a4238',
  other: '#44444a',
};

export const STREET_LABEL: Record<string, string> = {
  road: '车行道',
  pathway: '园路',
  track: '土路',
  other: '其它',
};
