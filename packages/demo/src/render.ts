// 出图：2D 平面 + 等轴体块。纯字符串拼 SVG，不引任何依赖。
//
// 为什么不用 three：这一轮要验证的是**契约与几何**，不是渲染管线。
// SVG 能直接把"哪条边被判定为临街""哪块宗地切出了几个楼位"画成可读的证据，
// 比在 3D 里转半天找问题快得多。

import { bounds, type Vec2 } from '../../plan-ir/src/geom.ts';

const ROLE_FILL: Record<string, string> = {
  asphalt: '#3a3d47',
  grass: '#2c4a33',
  paving: '#4a4d58',
  plaza: '#4f4658',
  water: '#24405c',
  decorative: '#3f3a4a',
  ground: '#26262e',
};

const KIND_COLOR: Record<string, string> = {
  commercialLow: '#c98a3f',
  commercialHigh: '#d4682f',
  mixed: '#b57a4a',
  residentialLow: '#7fa653',
  residentialRow: '#8fb35e',
  residentialMedium: '#6c9a48',
  residentialHigh: '#4f8a3d',
  lowRent: '#93a35c',
  officeLow: '#5c8fb3',
  officeHigh: '#3f7aa6',
  industrial: '#8a8577',
};

export const kindColor = (k: string): string => KIND_COLOR[k] ?? '#8a8577';

export interface Scene2D {
  parcel: Vec2[];
  streets: { a: Vec2; b: Vec2; w: number }[];
  attachments: { p: Vec2; kind: string }[];
  /** 被判为"临街但缺贴合点"的边序号，画红。 */
  badEdges?: number[];
  /** "旁边有街但不算临街"的边序号，画橙虚线。 */
  nearEdges?: number[];
  surfaces?: { ring: Vec2[]; role: string }[];
  zoning?: { quad: Vec2[] }[];
  slots?: { quad: Vec2[]; color: string; dashed?: boolean }[];
  /** 8 m 宗地格（每格一个四边形），画成细网格。 */
  gridCells?: Vec2[][];
}

const ptsToPath = (pts: readonly Vec2[]): string =>
  pts.map((p, i) => `${i === 0 ? 'M' : 'L'}${p[0].toFixed(2)} ${p[1].toFixed(2)}`).join(' ') + ' Z';

export function svg2D(sc: Scene2D, size = 560): string {
  const all: Vec2[] = [...sc.parcel];
  for (const s of sc.streets) all.push(s.a, s.b);
  for (const a of sc.attachments) all.push(a.p);
  for (const s of sc.surfaces ?? []) all.push(...s.ring);
  for (const z of sc.zoning ?? []) all.push(...z.quad);
  for (const s of sc.slots ?? []) all.push(...s.quad);
  for (const c of sc.gridCells ?? []) all.push(...c);

  const bb = bounds(all);
  const w = Math.max(1, bb.maxX - bb.minX);
  const h = Math.max(1, bb.maxY - bb.minY);
  const pad = 18;
  const scale = Math.min((size - pad * 2) / w, (size - pad * 2) / h);
  // 画布按地块长宽比收缩，避免上下大片空白
  const H = Math.max(140, Math.round((h * scale + pad * 2) / 1));
  const sx = (x: number) => pad + (x - bb.minX) * scale;
  // 世界 y 是"北"，SVG y 向下 → 翻一次；只在这里翻，别的地方不许翻
  const sy = (y: number) => pad + (bb.maxY - y) * scale;

  const out: string[] = [];
  out.push(`<svg viewBox="0 0 ${size} ${H}" width="100%" role="img">`);

  // 底色
  out.push(`<rect x="0" y="0" width="${size}" height="${H}" fill="#1b1b21"/>`);

  // 面（先画，作为底）
  for (const s of sc.surfaces ?? []) {
    const d = s.ring.map((p, i) => `${i === 0 ? 'M' : 'L'}${sx(p[0])} ${sy(p[1])}`).join(' ') + ' Z';
    out.push(`<path d="${d}" fill="${ROLE_FILL[s.role] ?? '#2a2a32'}" stroke="none"/>`);
  }

  // 宗地（虚线框 + 半透明填充）
  for (const z of sc.zoning ?? []) {
    const d = z.quad.map((p, i) => `${i === 0 ? 'M' : 'L'}${sx(p[0])} ${sy(p[1])}`).join(' ') + ' Z';
    out.push(`<path d="${d}" fill="#6c5ce733" stroke="#9b8bf0" stroke-width="1" stroke-dasharray="4 3"/>`);
  }

  // 地块边界
  out.push(
    `<path d="${sc.parcel.map((p, i) => `${i === 0 ? 'M' : 'L'}${sx(p[0])} ${sy(p[1])}`).join(' ')} Z" fill="none" stroke="#e8e6e1" stroke-width="1.6"/>`,
  );

  // 街道（按宽度画成带）
  for (const s of sc.streets) {
    out.push(
      `<line x1="${sx(s.a[0])}" y1="${sy(s.a[1])}" x2="${sx(s.b[0])}" y2="${sy(s.b[1])}" stroke="#55565f" stroke-width="${Math.max(2, s.w * scale)}" stroke-linecap="butt"/>`,
    );
    out.push(
      `<line x1="${sx(s.a[0])}" y1="${sy(s.a[1])}" x2="${sx(s.b[0])}" y2="${sy(s.b[1])}" stroke="#8d8e98" stroke-width="1" stroke-dasharray="6 4"/>`,
    );
  }

  // 楼位
  for (const s of sc.slots ?? []) {
    out.push(
      `<path d="${ptsToPath(s.quad.map((p) => [sx(p[0]), sy(p[1])] as Vec2))}" fill="${s.color}" fill-opacity="0.55" stroke="${s.color}" stroke-width="${s.dashed ? 1 : 0.8}" ${s.dashed ? 'stroke-dasharray="3 3"' : ''}/>`,
    );
  }

  // 8 m 宗地格
  for (const c of sc.gridCells ?? []) {
    out.push(
      `<path d="${ptsToPath(c.map((p) => [sx(p[0]), sy(p[1])] as Vec2))}" fill="#7fa6c818" stroke="#7fa6c8" stroke-width="0.6"/>`,
    );
  }

  // 宗地边框再描一遍（压在楼位之上，否则整块被楼位盖住就看不出边界）
  for (const z of sc.zoning ?? []) {
    const d = z.quad.map((p, i) => `${i === 0 ? 'M' : 'L'}${sx(p[0])} ${sy(p[1])}`).join(' ') + ' Z';
    out.push(`<path d="${d}" fill="none" stroke="#b9a9ff" stroke-width="1.1" stroke-dasharray="5 4"/>`);
  }

  // 近邻未达标的边（橙虚线）
  for (const i of sc.nearEdges ?? []) {
    const a = sc.parcel[i];
    const b = sc.parcel[(i + 1) % sc.parcel.length];
    out.push(
      `<line x1="${sx(a[0])}" y1="${sy(a[1])}" x2="${sx(b[0])}" y2="${sy(b[1])}" stroke="#ef9f27" stroke-width="2.5" stroke-dasharray="7 5" stroke-linecap="round"/>`,
    );
  }

  // 出问题的边（红）
  for (const i of sc.badEdges ?? []) {
    const a = sc.parcel[i];
    const b = sc.parcel[(i + 1) % sc.parcel.length];
    out.push(
      `<line x1="${sx(a[0])}" y1="${sy(a[1])}" x2="${sx(b[0])}" y2="${sy(b[1])}" stroke="#e24b4a" stroke-width="3" stroke-linecap="round"/>`,
    );
  }

  // 贴合点
  for (const a of sc.attachments) {
    const r = a.kind === 'roadNode' ? 3 : 4.5;
    const fill = a.kind === 'roadNode' ? '#9b8bf0' : a.kind === 'roadCrossing' ? '#5dcaa5' : '#22d3ee';
    out.push(`<circle cx="${sx(a.p[0])}" cy="${sy(a.p[1])}" r="${r}" fill="${fill}" stroke="#12121a" stroke-width="1"/>`);
  }

  out.push('</svg>');
  return out.join('\n');
}

export interface Box3 {
  quad: Vec2[];
  h: number;
  color: string;
}

/** 等轴投影：sx = (x - z)·cos30，sy = (x + z)·sin30 − y。 */
export function svgIso(boxes: Box3[], size = 560, base?: Box3): string {
  const COS = 0.866;
  const SIN = 0.5;
  const proj = (x: number, y: number, z: number): [number, number] => [(x - z) * COS, (x + z) * SIN - y];

  const allBoxes = base ? [base, ...boxes] : boxes;
  const all: [number, number][] = [];
  for (const b of allBoxes) {
    for (const p of b.quad) {
      all.push(proj(p[0], 0, p[1]));
      all.push(proj(p[0], b.h, p[1]));
    }
  }
  if (all.length === 0) return `<svg viewBox="0 0 ${size} ${size}"></svg>`;

  const minX = Math.min(...all.map((p) => p[0]));
  const maxX = Math.max(...all.map((p) => p[0]));
  const minY = Math.min(...all.map((p) => p[1]));
  const maxY = Math.max(...all.map((p) => p[1]));
  const pad = 20;
  const scale = Math.min((size - pad * 2) / Math.max(1, maxX - minX), (size - pad * 2) / Math.max(1, maxY - minY));
  const tx = (p: [number, number]): [number, number] => [pad + (p[0] - minX) * scale, pad + (p[1] - minY) * scale];
  const H = Math.max(140, Math.round(maxY - minY) * scale + pad * 2);

  const out: string[] = [];
  out.push(`<svg viewBox="0 0 ${size} ${H}" width="100%" role="img">`);
  out.push(`<rect x="0" y="0" width="${size}" height="${H}" fill="#1b1b21"/>`);

  /** 画一个盒子：四面墙 + 顶面。 */
  const drawBox = (b: Box3): void => {
    const walls: string[] = [];
    for (let i = 0; i < b.quad.length; i++) {
      const p0 = b.quad[i];
      const p1 = b.quad[(i + 1) % b.quad.length];
      const a0 = tx(proj(p0[0], 0, p0[1]));
      const a1 = tx(proj(p1[0], 0, p1[1]));
      const b0 = tx(proj(p0[0], b.h, p0[1]));
      const b1 = tx(proj(p1[0], b.h, p1[1]));
      walls.push(
        `<path d="M${a0[0]} ${a0[1]} L${a1[0]} ${a1[1]} L${b1[0]} ${b1[1]} L${b0[0]} ${b0[1]} Z" fill="${shade(b.color, 0.55)}" stroke="${shade(b.color, 0.35)}" stroke-width="0.5"/>`,
      );
    }
    const top = b.quad.map((p) => tx(proj(p[0], b.h, p[1])));
    const d = top.map((p, i) => `${i === 0 ? 'M' : 'L'}${p[0]} ${p[1]}`).join(' ') + ' Z';
    out.push(walls.join(''));
    out.push(`<path d="${d}" fill="${b.color}" stroke="${shade(b.color, 0.4)}" stroke-width="0.8"/>`);
  };

  // 地面永远先画（它面积最大，混进排序会把楼压掉）
  if (base) drawBox(base);

  // 其余按"远离观察者 → 靠近"排序
  const sorted = [...boxes].sort((a, b) => {
    const ca = a.quad.reduce((s, p) => s + p[0] + p[1], 0) / a.quad.length;
    const cb = b.quad.reduce((s, p) => s + p[0] + p[1], 0) / b.quad.length;
    return ca - cb;
  });
  for (const b of sorted) drawBox(b);

  out.push('</svg>');
  return out.join('\n');
}

/** 简单调色：k<1 变暗。 */
function shade(hex: string, k: number): string {
  const m = /^#([0-9a-f]{6})$/i.exec(hex);
  if (!m) return hex;
  const n = parseInt(m[1], 16);
  const r = Math.round(((n >> 16) & 255) * k);
  const g = Math.round(((n >> 8) & 255) * k);
  const b = Math.round((n & 255) * k);
  return `#${((r << 16) | (g << 8) | b).toString(16).padStart(6, '0')}`;
}
