// 建筑轮廓 → Core 认的定向四边形（`SiteContext.Obstacles`）。
//
// 为什么要这一步：Core 的 `Obstacle` 只有四条边（A/B/C/D），而 TownForge 的体块轮廓
// 有 4~7 个点（切角、L 形、长条）。**不能**拿最小外接矩形糊过去 —— 实测 4 点轮廓的
// 外接矩形平均就大 13%，最大差一倍，等于凭空封掉一块本来能用的地。
//
// 所以：4 点原样传；多于 4 点先三角化，每个三角形补成四边形（第四点取 a-c 中点，
// 落在边上，面积不变，也不会产生零长边让 Core 的偏移算法出错）。
//
// 拿到这批四边形之后，Core 在 `Composer.Compose` 末尾跑 `SiteSafety.Apply`
// （placemaker-master/Core/Build/SiteSafety.cs）：压到建筑地界的路、对象、铺装、
// 区域、地形、粉刷会被**丢弃或裁开**。注意它的语义是"删"不是"绕"——
// 一条车道横穿楼，结果是整条没了，而不是改道。

import { triangulate } from '../export/mesh';
import type { Pt } from '../core/geom';

/** 一个四边形，米制、`[x, z]`，与 Plan 的坐标约定一致。 */
export type Quad = [number, number][];

export interface PlanObstacle {
  kind: 'building';
  quad: Quad;
}

/** 迷你多边形面积（鞋带公式，绝对值）。米² */
export function quadArea(q: Quad): number {
  let s = 0;
  for (let i = 0; i < q.length; i++) {
    const a = q[i], b = q[(i + 1) % q.length];
    s += a[0] * b[1] - b[0] * a[1];
  }
  return Math.abs(s) / 2;
}

/**
 * 一条轮廓 → 若干四边形。4 点原样返回；更多点三角化后逐块补成四边形。
 * `toM` 负责单位换算和原点平移（和桥接用同一套，别在这里另算）。
 */
export function buildingQuads(ring: Pt[], toM: (p: Pt) => [number, number]): Quad[] {
  if (ring.length < 3) return [];
  const m = ring.map(toM);
  if (m.length === 4) return [m];

  const quads: Quad[] = [];
  const idx = triangulate(ring);   // 已按面积过滤掉退化三角形
  for (let i = 0; i < idx.length; i += 3) {
    const a = m[idx[i]], b = m[idx[i + 1]], c = m[idx[i + 2]];
    quads.push([a, b, c, [(a[0] + c[0]) / 2, (a[1] + c[1]) / 2]]);
  }
  return quads;
}

/**
 * 一批体块轮廓 → 给 Core 的障碍表。
 *
 * `minArea` 是"小到什么程度不值得避让"：Core 那边 `SiteSafety.Protected` 会跳过
 * 0.5 m² 以下的四边形，这里先按整栋的占地过滤一遍，免得一堆碎块白占带宽。
 */
export function buildingObstacles(
  solids: { ring: Pt[] }[],
  toM: (p: Pt) => [number, number],
  minArea = 2,
): PlanObstacle[] {
  const out: PlanObstacle[] = [];
  for (const s of solids) {
    const quads = buildingQuads(s.ring, toM);
    const area = quads.reduce((acc, q) => acc + quadArea(q), 0);
    if (area < minArea) continue;
    for (const quad of quads) out.push({ kind: 'building', quad });
  }
  return out;
}
