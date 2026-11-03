// 按建筑把地块切碎，好让路网型生成器在"没有楼的那部分"上独立工作。
//
// 为什么需要：SiteSafety 的语义是**删**不是**绕**——一条车道碰到建筑就整条丢弃。
// 一块 53 条车道的停车场，只要压上几栋楼，最后可能只剩 1 条（实测如此）。
// 正确做法是别让它面对"有楼的地块"，而是先把地块切成若干块没有楼的小地，逐块生成再合并。
//
// 为什么用网格而不是一次多边形差集：`difference(lot, buildings)` 对"中间压一栋楼"的结果是
// **带洞的多边形**，而 Placemaker 的 `shape` 只认单环。取外环等于把楼又盖回去，
// 取内环就把整块地丢了。
//
// 所以流程是：格子 ∩ 地块 → 挖掉建筑 → 无洞的留着，还带洞的**四等分再试**（最多几层）。
// 这样楼边上的地能保住（挖完变 L 形），只有楼本身占的那一小块会被切到最后仍带洞而丢弃。

import polygonClipping from 'polygon-clipping';

export interface SplitOptions {
  /** 起始网格边长（米）。默认 40。 */
  gridSize?: number;
  /** 递归细分的最大层数。默认 3（40 → 20 → 10）。 */
  maxDepth?: number;
  /** 低于这个面积（m²）的碎块直接丢。默认 80。 */
  minAreaM2?: number;
}

export interface SplitResult {
  /** 每块都是简单多边形（无洞），坐标与输入同一套米制。按面积降序。 */
  parts: [number, number][][];
  /** 递归到最深层仍带洞而丢弃的块数（通常就是建筑本身占的位置）。 */
  uncuttable: number;
  /** 太小而丢弃的块数。 */
  skippedTiny: number;
  /** 完全在地块外的格数。 */
  skippedOutside: number;
}

function ringArea(r: [number, number][]): number {
  if (r.length < 3) return 0;
  let s = 0;
  for (let i = 0, j = r.length - 1; i < r.length; j = i++) s += r[j][0] * r[i][1] - r[i][0] * r[j][1];
  return Math.abs(s) / 2;
}

/** 去掉首尾重合点与相邻重复点 —— 直接喂给 shape 会被 Core 当成退化环。 */
function normalizeRing(r: [number, number][]): [number, number][] {
  const out: [number, number][] = [];
  for (const p of r) {
    const last = out[out.length - 1];
    if (last && Math.abs(last[0] - p[0]) < 1e-9 && Math.abs(last[1] - p[1]) < 1e-9) continue;
    out.push([p[0], p[1]]);
  }
  if (out.length >= 2) {
    const f = out[0], l = out[out.length - 1];
    if (Math.abs(f[0] - l[0]) < 1e-9 && Math.abs(f[1] - l[1]) < 1e-9) out.pop();
  }
  return out;
}

/**
 * 网格剖分。返回的每块都满足：完全落在地块内、不含建筑、面积够大、是简单多边形。
 *
 * 格子边界对齐到地块包围盒左下角，所以同一块地每次切出来一定一样（可复现）。
 */
export function splitLot(
  ring: [number, number][],
  quads: [number, number][][],
  opts: SplitOptions = {},
): SplitResult {
  const gridSize = opts.gridSize ?? 40;
  const maxDepth = opts.maxDepth ?? 3;
  const minAreaM2 = opts.minAreaM2 ?? 80;

  const res: SplitResult = { parts: [], uncuttable: 0, skippedTiny: 0, skippedOutside: 0 };
  if (ring.length < 3) return res;

  const xs = ring.map((p) => p[0]);
  const ys = ring.map((p) => p[1]);
  const x0 = Math.min(...xs), x1 = Math.max(...xs);
  const y0 = Math.min(...ys), y1 = Math.max(...ys);

  const nx = Math.max(1, Math.ceil((x1 - x0) / gridSize));
  const ny = Math.max(1, Math.ceil((y1 - y0) / gridSize));
  const cw = (x1 - x0) / nx;
  const ch = (y1 - y0) / ny;

  const bld = quads.filter((q) => q.length >= 3).map((q) => [q] as [number, number][][]);
  const lotPoly = [[ring]] as [number, number][][][];

  /** 一块矩形地：裁到地块内、挖掉建筑、带洞就四等分再试。 */
  const process = (cell: [number, number][], depth: number): void => {
    let piece: [number, number][][][];
    try {
      piece = polygonClipping.intersection([cell] as never, lotPoly as never) as unknown as [number, number][][][];
    } catch {
      return;
    }
    if (piece.length === 0) {
      if (depth === 0) res.skippedOutside++;
      return;
    }

    let rem: [number, number][][][];
    try {
      rem = bld.length === 0
        ? piece
        : (polygonClipping.difference(piece as never, ...(bld as never[])) as unknown as [number, number][][][]);
    } catch {
      rem = piece;
    }

    for (const poly of rem) {
      // 无洞：直接可用
      if (poly.length === 1) {
        const r = normalizeRing(poly[0]);
        if (r.length < 3) continue;
        if (ringArea(r) < minAreaM2) { res.skippedTiny++; continue; }
        res.parts.push(r);
        continue;
      }
      // 还带洞：格子还能再分就四等分递归，否则丢掉（通常是建筑本身的位置）
      if (depth >= maxDepth) { res.uncuttable++; continue; }
      const cx = (cell[0][0] + cell[2][0]) / 2;
      const cy = (cell[0][1] + cell[2][1]) / 2;
      const quads4: [number, number][][] = [
        [[cell[0][0], cell[0][1]], [cx, cell[0][1]], [cx, cy], [cell[0][0], cy]],
        [[cx, cell[0][1]], [cell[2][0], cell[0][1]], [cell[2][0], cy], [cx, cy]],
        [[cell[0][0], cy], [cx, cy], [cx, cell[2][1]], [cell[0][0], cell[2][1]]],
        [[cx, cy], [cell[2][0], cy], [cell[2][0], cell[2][1]], [cx, cell[2][1]]],
      ];
      for (const q of quads4) process(q, depth + 1);
    }
  };

  for (let iy = 0; iy < ny; iy++) {
    for (let ix = 0; ix < nx; ix++) {
      const cell: [number, number][] = [
        [x0 + ix * cw, y0 + iy * ch],
        [x0 + (ix + 1) * cw, y0 + iy * ch],
        [x0 + (ix + 1) * cw, y0 + (iy + 1) * ch],
        [x0 + ix * cw, y0 + (iy + 1) * ch],
      ];
      process(cell, 0);
    }
  }

  res.parts.sort((a, b) => ringArea(b) - ringArea(a));
  return res;
}

/** 切完之后总共还能用的面积（m²）。用来和原地块面积对比，知道切碎损失了多少。 */
export function partsArea(parts: [number, number][][]): number {
  return parts.reduce((s, p) => s + ringArea(p), 0);
}
