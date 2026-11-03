// 网格基元：把 2D 要素变成 3D 三角形。
//
// 坐标系固定：地图 (x, y) → 3D (x, 0, y)，高度沿 +Y。
// 这样从上方俯视和 2D 编辑器完全一致，导进 DCC 也不用转轴。
//
// 手性提醒（踩过坑）：earcut 对 CCW 输入输出 CCW 三角形，映射到 XZ 平面后
// 外法线朝 −Y，所以索引必须两两交换；侧面顶点的顺序同理必须是
// a_bot → b_top → b_bot。

import earcut from 'earcut';
import { Pt } from '../core/geom';

export interface Primitive {
  name: string;
  group: string;
  positions: number[];
  normals: number[];
  indices: number[];
  /** sRGB 0~1 */
  color: [number, number, number];
  doubleSided?: boolean;
}

/**
 * 退化三角形阈值（面积的一半，世界单位²）。
 *
 * earcut 在近共线的三个点上会吐出零面积三角形 —— 渲染上什么都不画，
 * 但它的面法线是零向量，绕序自检 `dot <= 0` 会判成翻转。
 * 一条 5 km 的中心线被切成 5000 段虚线时，这种三角一两块是常态，
 * 与其放宽断言，不如在这里就不产出无用的三角形。
 */
const DEGEN_AREA2 = 1e-9;

export function triangulate(ring: Pt[]): number[] {
  if (ring.length < 3) return [];
  const flat: number[] = [];
  for (const p of ring) flat.push(p.x, p.y);
  const idx = earcut(flat, undefined, 2);
  const out: number[] = [];
  for (let i = 0; i < idx.length; i += 3) {
    const a = idx[i];
    const b = idx[i + 2];
    const c = idx[i + 1];
    const ax = flat[a * 2];
    const ay = flat[a * 2 + 1];
    const area2 = (flat[b * 2] - ax) * (flat[c * 2 + 1] - ay) - (flat[c * 2] - ax) * (flat[b * 2 + 1] - ay);
    if (Math.abs(area2) <= DEGEN_AREA2) continue;
    out.push(a, b, c);
  }
  return out;
}

export function ensureCCW(ring: Pt[]): Pt[] {
  let s = 0;
  for (let i = 0; i < ring.length; i++) {
    const a = ring[i];
    const b = ring[(i + 1) % ring.length];
    s += a.x * b.y - b.x * a.y;
  }
  return s < 0 ? ring.slice().reverse() : ring;
}

function dedupeRing(ring: Pt[]): Pt[] {
  return ring.filter((p, i, arr) => i === 0 || Pt.distance(p, arr[i - 1]) > 1e-6);
}

/** 贴地平面多边形 */
export function flatPolygon(
  ring: Pt[],
  y: number,
  name: string,
  group: string,
  color: [number, number, number]
): Primitive | null {
  const clean = ensureCCW(dedupeRing(ring));
  if (clean.length < 3) return null;
  const idx = triangulate(clean);
  if (idx.length === 0) return null;

  const positions: number[] = [];
  const normals: number[] = [];
  for (const p of clean) {
    positions.push(p.x, y, p.y);
    normals.push(0, 1, 0);
  }
  return { name, group, positions, normals, indices: idx, color };
}

/** 挤出体块：顶面与侧面分成两个图元，便于分别换材质 */
export function extrudeSolid(
  ring: Pt[],
  y0: number,
  height: number,
  name: string,
  group: string,
  roofColor: [number, number, number],
  wallColor: [number, number, number],
  noRoof = false
): Primitive[] {
  const clean = ensureCCW(dedupeRing(ring));
  if (clean.length < 3 || height <= 1e-6) return [];
  const top = triangulate(clean);
  if (top.length === 0) return [];

  const n = clean.length;
  const out: Primitive[] = [];

  if (!noRoof) {
    const topPos: number[] = [];
    const topNrm: number[] = [];
    for (const p of clean) {
      topPos.push(p.x, y0 + height, p.y);
      topNrm.push(0, 1, 0);
    }
    out.push({ name: `${name}_roof`, group, positions: topPos, normals: topNrm, indices: top.slice(), color: roofColor });
  }

  const pos: number[] = [];
  const normals: number[] = [];
  const indices: number[] = [];
  for (let i = 0; i < n; i++) {
    const a = clean[i];
    const b = clean[(i + 1) % n];
    const dx = b.x - a.x;
    const dy = b.y - a.y;
    const len = Math.hypot(dx, dy);
    if (len < 1e-9) continue;
    const nx = dy / len;
    const ny = -dx / len;

    const base = pos.length / 3;
    pos.push(a.x, y0, a.y, b.x, y0, b.y, b.x, y0 + height, b.y, a.x, y0 + height, a.y);
    for (let k = 0; k < 4; k++) normals.push(nx, 0, ny);
    indices.push(base, base + 2, base + 1, base, base + 3, base + 2);
  }

  if (indices.length > 0) out.push({ name: `${name}_wall`, group, positions: pos, normals, indices, color: wallColor });
  return out;
}

/**
 * 台地 / 料堆：顶面**逐顶点**给高度，裙边各自落到 `y0`。
 *
 * 为什么不能用 `extrudeSolid`（统一高度）：
 * Core 的 `AiTerrain.Cells` 把 AI 地块切成"核心环 + 约 4 m 的羽化格子"，
 * **每个顶点带自己的高度**，靠这点高度差做出缓坡。统一高度画出来的是一块
 * 平顶石墩，一片料堆就成了 65 个台阶 —— 或者更糟，退回以前那种贴地薄板。
 *
 * `hs` 与 `ring` 必须同步清洗（去重合点、统一绕序）：两者一旦错位，
 * 顶点高度就落到别的角上，一块方形料堆会拧成麻花。
 */
export function terraceSolid(
  ring: Pt[],
  hs: number[],
  y0: number,
  name: string,
  group: string,
  topColor: [number, number, number],
  wallColor: [number, number, number],
  /**
   * 收顶（世界单位）：顶面每个顶点朝形心缩进这么多，侧面因此带一点坡。
   *
   * 为什么要收：Core 的台地是 4 m 见方的格子，而一辆废车长 4.8 m。顶面不收、
   * 侧面垂直，车就悬在崖边上，看着像穿模；收了顶以后坡从地面爬上来接住它。
   * 收多少由调用方按高度算（见 `src/scene/build.ts`），默认 0 = 直上直下。
   */
  inset = 0
): Primitive[] {
  if (ring.length < 3) return [];
  const pick = (i: number) => (Number.isFinite(hs[i]) ? hs[i] : (Number.isFinite(hs[0]) ? hs[0] : y0));

  // 去重合点（含首尾闭合的那一对），hs 跟着一起走
  const R0: Pt[] = [];
  const H0: number[] = [];
  for (let i = 0; i < ring.length; i++) {
    const p = ring[i];
    const last = R0[R0.length - 1];
    if (last && Math.abs(last.x - p.x) < 1e-6 && Math.abs(last.y - p.y) < 1e-6) continue;
    R0.push(p);
    H0.push(pick(i));
  }
  while (R0.length > 1) {
    const f = R0[0];
    const l = R0[R0.length - 1];
    if (Math.abs(f.x - l.x) < 1e-6 && Math.abs(f.y - l.y) < 1e-6) { R0.pop(); H0.pop(); } else break;
  }
  if (R0.length < 3) return [];

  let s = 0;
  for (let i = 0; i < R0.length; i++) {
    const a = R0[i];
    const b = R0[(i + 1) % R0.length];
    s += a.x * b.y - b.x * a.y;
  }
  const R = s < 0 ? R0.slice().reverse() : R0;
  const H = s < 0 ? H0.slice().reverse() : H0;

  const top = triangulate(R);
  if (top.length === 0) return [];

  // 收顶：每个顶点朝形心挪 inset，最多挪到自身距形心的 40%（防止高瘦形状缩成一点）
  let T = R;
  if (inset > 1e-6) {
    let cx = 0;
    let cy = 0;
    for (const q of R) {
      cx += q.x;
      cy += q.y;
    }
    cx /= R.length;
    cy /= R.length;
    T = R.map((q) => {
      const dx = cx - q.x;
      const dy = cy - q.y;
      const d = Math.hypot(dx, dy);
      if (d < 1e-9) return q;
      const m = Math.min(inset, d * 0.4) / d;
      return new Pt(q.x + dx * m, q.y + dy * m);
    });
  }

  const topPos: number[] = [];
  const topNrm: number[] = [];
  for (let i = 0; i < R.length; i++) {
    topPos.push(T[i].x, H[i], T[i].y);
    topNrm.push(0, 1, 0);
  }
  const out: Primitive[] = [
    { name: `${name}_cap`, group, positions: topPos, normals: topNrm, indices: top, color: topColor },
  ];

  const n = R.length;
  const pos: number[] = [];
  const normals: number[] = [];
  const indices: number[] = [];
  for (let i = 0; i < n; i++) {
    const a = R[i];
    const b = R[(i + 1) % n];
    const ha = H[i];
    const hb = H[(i + 1) % n];
    // 两端都在地下的边不用画墙
    if (ha <= y0 + 1e-6 && hb <= y0 + 1e-6) continue;
    const dx = b.x - a.x;
    const dy = b.y - a.y;
    const len = Math.hypot(dx, dy);
    if (len < 1e-9) continue;
    const nx = dy / len;
    const ny = -dx / len;
    const ta = T[i];
    const tb = T[(i + 1) % n];
    const base = pos.length / 3;
    pos.push(a.x, y0, a.y, b.x, y0, b.y, tb.x, hb, tb.y, ta.x, ha, ta.y);
    for (let k2 = 0; k2 < 4; k2++) normals.push(nx, 0, ny);
    indices.push(base, base + 2, base + 1, base, base + 3, base + 2);
  }
  if (indices.length > 0) out.push({ name: `${name}_wall`, group, positions: pos, normals, indices, color: wallColor });
  return out;
}

/** 圆柱（灯杆、树干、水塔） */
export function cylinder(
  c: Pt,
  r: number,
  y0: number,
  h: number,
  name: string,
  group: string,
  color: [number, number, number],
  seg = 8
): Primitive {
  const positions: number[] = [];
  const normals: number[] = [];
  const indices: number[] = [];

  for (let i = 0; i <= seg; i++) {
    const ang = (i / seg) * Math.PI * 2;
    const nx = Math.cos(ang);
    const ny = Math.sin(ang);
    positions.push(c.x + nx * r, y0, c.y + ny * r);
    normals.push(nx, 0, ny);
    positions.push(c.x + nx * r, y0 + h, c.y + ny * r);
    normals.push(nx, 0, ny);
  }
  for (let i = 0; i < seg; i++) {
    const a = i * 2;
    indices.push(a, a + 3, a + 2, a, a + 1, a + 3);
  }

  const centerIdx = positions.length / 3;
  positions.push(c.x, y0 + h, c.y);
  normals.push(0, 1, 0);
  for (let i = 0; i <= seg; i++) {
    const ang = (i / seg) * Math.PI * 2;
    positions.push(c.x + Math.cos(ang) * r, y0 + h, c.y + Math.sin(ang) * r);
    normals.push(0, 1, 0);
  }
  for (let i = 0; i < seg; i++) indices.push(centerIdx, centerIdx + i + 2, centerIdx + i + 1);

  return { name, group, positions, normals, indices, color };
}

/** 圆锥（树冠、塔顶）。tipUp=false 时锥尖朝下，做双层树冠的第二层 */
export function cone(
  c: Pt,
  r: number,
  y0: number,
  h: number,
  name: string,
  group: string,
  color: [number, number, number],
  seg = 6,
  tipUp = true
): Primitive {
  const positions: number[] = [];
  const normals: number[] = [];
  const indices: number[] = [];

  const apexY = tipUp ? y0 + h : y0;
  const baseY = tipUp ? y0 : y0 + h;
  const apex = positions.length / 3;
  positions.push(c.x, apexY, c.y);
  normals.push(0, tipUp ? 1 : -1, 0);

  for (let i = 0; i < seg; i++) {
    const a0 = (i / seg) * Math.PI * 2;
    const a1 = ((i + 1) / seg) * Math.PI * 2;
    const p0 = new Pt(c.x + Math.cos(a0) * r, c.y + Math.sin(a0) * r);
    const p1 = new Pt(c.x + Math.cos(a1) * r, c.y + Math.sin(a1) * r);

    // 侧面法线：水平方向取两点平均外法线，竖直分量 = 锥面斜率 r/h。
    // 尖朝上时法线朝外上方（+Y），尖朝下时朝外下方。
    const mx = (Math.cos(a0) + Math.cos(a1)) / 2;
    const my = (Math.sin(a0) + Math.sin(a1)) / 2;
    const ml = Math.hypot(mx, my) || 1;
    const slope = r / Math.max(h, 1e-6);
    const nx = mx / ml;
    const ny = my / ml;
    const up = tipUp ? slope : -slope;
    const nl = Math.hypot(nx, up, ny) || 1;

    const base = positions.length / 3;
    positions.push(p0.x, baseY, p0.y, p1.x, baseY, p1.y);
    normals.push(nx / nl, up / nl, ny / nl, nx / nl, up / nl, ny / nl);

    // 手性：XZ 平面上角度增大是顺时针，所以 (apex, p1, p0) 才是外向
    if (tipUp) indices.push(apex, base + 1, base);
    else indices.push(apex, base, base + 1);
  }

  return { name, group, positions, normals, indices, color };
}

/** 轴对齐（可绕 Y 旋转）长方体 */
export function box(
  c: Pt,
  w: number,
  d: number,
  y0: number,
  h: number,
  rot: number,
  name: string,
  group: string,
  color: [number, number, number],
  roofColor?: [number, number, number]
): Primitive[] {
  const cos = Math.cos(rot);
  const sin = Math.sin(rot);
  const cs: Pt[] = [
    new Pt(-w / 2, -d / 2),
    new Pt(w / 2, -d / 2),
    new Pt(w / 2, d / 2),
    new Pt(-w / 2, d / 2),
  ];
  const ring = cs.map((p) => new Pt(c.x + p.x * cos - p.y * sin, c.y + p.x * sin + p.y * cos));
  return extrudeSolid(ring, y0, h, name, group, roofColor ?? color, color);
}

/**
 * 沿折线生成有厚度的墙体。
 * 不能把整圈当成一个环带去三角化 —— 闭合环带是自交的，earcut 会吐出
 * 横穿全图的大三角形。这里按段生成再合并，两端各延长一点防裂缝。
 */
export function wallSolid(
  path: Pt[],
  thickness: number,
  y0: number,
  h: number,
  name: string,
  group: string,
  color: [number, number, number],
  closed = true
): Primitive | null {
  const pos: number[] = [];
  const nrm: number[] = [];
  const idx: number[] = [];
  const hw = thickness / 2;

  const appendBox = (ring: Pt[]) => {
    const clean = ensureCCW(ring);
    if (clean.length < 3) return;
    const topIdx = triangulate(clean);
    if (topIdx.length === 0) return;

    const topBase = pos.length / 3;
    for (const p of clean) {
      pos.push(p.x, y0 + h, p.y);
      nrm.push(0, 1, 0);
    }
    for (const i of topIdx) idx.push(topBase + i);

    const n = clean.length;
    for (let i = 0; i < n; i++) {
      const a = clean[i];
      const b = clean[(i + 1) % n];
      const dx = b.x - a.x;
      const dy = b.y - a.y;
      const len = Math.hypot(dx, dy);
      if (len < 1e-9) continue;
      const nx = dy / len;
      const ny = -dx / len;
      const base = pos.length / 3;
      pos.push(a.x, y0, a.y, b.x, y0, b.y, b.x, y0 + h, b.y, a.x, y0 + h, a.y);
      for (let k = 0; k < 4; k++) nrm.push(nx, 0, ny);
      idx.push(base, base + 2, base + 1, base, base + 3, base + 2);
    }
  };

  const last = closed ? path.length : path.length - 1;
  for (let i = 0; i < last; i++) {
    const a = path[i];
    const b = path[(i + 1) % path.length];
    const d = b.subtract(a);
    const len = d.length;
    if (len < 1e-6) continue;
    const nx = -d.y / len;
    const ny = d.x / len;
    const ext = d.norm(hw * 0.7);
    const a2 = a.subtract(ext);
    const b2 = b.add(ext);

    appendBox([
      new Pt(a2.x + nx * hw, a2.y + ny * hw),
      new Pt(b2.x + nx * hw, b2.y + ny * hw),
      new Pt(b2.x - nx * hw, b2.y - ny * hw),
      new Pt(a2.x - nx * hw, a2.y - ny * hw),
    ]);
  }

  if (idx.length === 0) return null;
  return { name, group, positions: pos, normals: nrm, indices: idx, color };
}

/**
 * 合并多个图元（减少 draw call / 节点数）。
 *
 * 关键优化：合并时按「量化后的坐标 + 法线」做**顶点焊接**。相邻多边形共享边界、
 * 法线又相同（例如整片沥青路面法线都是 +Y），焊接后顶点数能显著下降 ——
 * 这直接决定 BIN chunk 里 POSITION/NORMAL 的体量（每顶点 24 字节）。
 * 只焊「坐标与法线都一致」的顶点，几何与外观完全不变，只是索引更省。
 */
export function mergePrimitives(prims: Primitive[], name: string, group: string): Primitive | null {
  const positions: number[] = [];
  const normals: number[] = [];
  const indices: number[] = [];
  // 位置量化 1e-4 世界单位、法线量化 1e-3 —— 足以吸收布尔切块留下的浮点尾差，
  // 又远小于任何可见尺度，焊不坏形状。
  const QP = 1e4;
  const QN = 1e3;
  const weld = new Map<string, number>();
  let color: [number, number, number] = [0.7, 0.7, 0.7];

  for (const p of prims) {
    const localMap = new Map<number, number>();
    const vc = p.positions.length / 3;
    for (let i = 0; i < vc; i++) {
      const x = p.positions[i * 3];
      const y = p.positions[i * 3 + 1];
      const z = p.positions[i * 3 + 2];
      const nx = p.normals[i * 3];
      const ny = p.normals[i * 3 + 1];
      const nz = p.normals[i * 3 + 2];
      const key = `${Math.round(x * QP)},${Math.round(y * QP)},${Math.round(z * QP)},${Math.round(nx * QN)},${Math.round(ny * QN)},${Math.round(nz * QN)}`;
      let ni = weld.get(key);
      if (ni === undefined) {
        ni = positions.length / 3;
        positions.push(x, y, z);
        normals.push(nx, ny, nz);
        weld.set(key, ni);
      }
      localMap.set(i, ni);
    }
    // 焊接可能把极薄的三角形塌成零面积（共享边被并、三个点共线）。这类三角形
    // 渲染不出任何像素，却会让绕序自检（dot<=0）误报。与 `triangulate` 的
    // DEGEN_AREA2 约定一致，这里按面积丢弃退化三角。
    const DEGEN2 = 1e-18; // |cross|² 阈值，对应 |cross|<=1e-9
    for (let t = 0; t < p.indices.length; t += 3) {
      const ia = localMap.get(p.indices[t])!;
      const ib = localMap.get(p.indices[t + 1])!;
      const ic = localMap.get(p.indices[t + 2])!;
      if (ia === ib || ib === ic || ia === ic) continue;
      const ax = positions[ia * 3];
      const ay = positions[ia * 3 + 1];
      const az = positions[ia * 3 + 2];
      const e1x = positions[ib * 3] - ax;
      const e1y = positions[ib * 3 + 1] - ay;
      const e1z = positions[ib * 3 + 2] - az;
      const e2x = positions[ic * 3] - ax;
      const e2y = positions[ic * 3 + 1] - ay;
      const e2z = positions[ic * 3 + 2] - az;
      const nx2 = e1y * e2z - e1z * e2y;
      const ny2 = e1z * e2x - e1x * e2z;
      const nz2 = e1x * e2y - e1y * e2x;
      if (nx2 * nx2 + ny2 * ny2 + nz2 * nz2 <= DEGEN2) continue;
      indices.push(ia, ib, ic);
    }
    color = p.color;
  }
  if (indices.length === 0) return null;
  return { name, group, positions, normals, indices, color };
}
