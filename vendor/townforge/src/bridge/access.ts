// 出入口：复现 `EntranceSuggest`（`Layers/Parking/EntranceSuggest.cs:36-70`）。
//
// 为什么这一步排在资产、标线之前做：**它是 `ready` 的唯一来源。**
// docs/A §0.1 的结论是 `ready ⇔ CheckReady(given>0 ? given : suggest)`，
// 而 130 个生成器里只有 **parking 一族**的 `suggest` 真的碰街道。
// 没有出入口，"这块地引擎收不收"就算不出来；parking 家族也永远跑不起来。
//
// C# 的判据（别凭印象改，docs/A 里记着上一版写错的地方）：
//   1. 从地块边界**朝外打一条 40 m 的射线**，只认**车行道**（`Category == Road`）；
//   2. 命中后算 `gap` = 边界点到**车行道边缘**的距离，
//      要求 `gap ∈ [-w/2, 15]`（w = 该路红线宽）。
//      `-w/2` 允许点在车道里，`15` 允许街整体外移十几米还认；
//   3. 只有在"一条车行道都没有"时，才退回 `RoadAlongEdge < 30 m` 的兜底。
//
// 第 2 条的容差比 `AttachmentFinder` 的 `w/2 + 3` 大一个量级 —— 这不是笔误，
// 实测把街整体外移 11 m，parking 仍然 ready（产出 593 → 590）。

import { Pt } from '../core/geom';
import { buildRoadNet, type RoadEdge, type RoadNet } from '../road/system';
import { ROAD_SPECS } from '../road/spec';
import { UNIT_M } from '../core/units';
import { predictReady } from '../../../../packages/street-rules/src/ready.ts';
import type { Model } from '../town/model';
import type { Patch } from '../town/patch';
import type { LandUse } from '../scene/types';

type V2 = [number, number];

/** 射线长度上限（米）。C# 是 40 m。 */
export const REACH_M = 40;
/** 允许的最大间隙（米）。C# 是 15。 */
export const GAP_MAX_M = 15;
/** 建议出来的开口宽度（米）。C# 按地块面积给，这里取一个规整档。 */
export const DOOR_WIDTH_M = 6;

export interface AccessRecord {
  patchIndex: number;
  /** 出入口位置（米）—— 落在街区边界上 */
  pos: V2;
  /** 朝外方向（单位向量，米制坐标） */
  dir: V2;
  widthM: number;
  /** 到车行道边缘的间隙（米）。负=落在车道里，0=正好贴着 */
  gapM: number;
  /** 命中的那条路的等级 */
  roadCls: RoadEdge['cls'] | null;
  /** 是否满足 `gap ∈ [-w/2, 15]` */
  attached: boolean;
  /** 命中距离（米，到路中线的垂距）—— 排查用 */
  reachM: number;
}

export interface AccessReport {
  accesses: AccessRecord[];
  /** patchIndex → 该地块的出入口数 / 接街数 / 最小间隙 */
  perPatch: Map<number, { count: number; attached: number; gapMin: number }>;
  /** 停车场地块的 ready 判定（这是 parking 家族的真实判据） */
  parkingReady: { patchIndex: number; ready: boolean; by: string }[];
  stats: {
    accessCount: number;
    /** 接街的出入口占比 */
    attachedShare: number;
    /** 有出入口的地块数 / 城内地块数 */
    patchesWithAccess: number;
    patches: number;
    /** 停车场：总数与 ready 数 */
    parkingTotal: number;
    parkingReady: number;
    /** 间隙中位（米） */
    gapMedM: number;
  };
}

const toPt = (p: { x: number; y: number }): Pt => new Pt(p.x, p.y);

/** 点到线段的垂距（世界单位）。 */
function distToSeg(p: Pt, a: Pt, b: Pt): number {
  const dx = b.x - a.x;
  const dy = b.y - a.y;
  const l2 = dx * dx + dy * dy;
  if (l2 < 1e-12) return Math.hypot(p.x - a.x, p.y - a.y);
  let t = ((p.x - a.x) * dx + (p.y - a.y) * dy) / l2;
  t = t < 0 ? 0 : t > 1 ? 1 : t;
  return Math.hypot(p.x - (a.x + dx * t), p.y - (a.y + dy * t));
}

/** 最近的**车行道**边。TownForge 的每条 `RoadEdge` 都是可通车的（arterial/collector/local）。 */
function nearestRoad(net: RoadNet, p: Pt): { edge: RoadEdge; dist: number } | null {
  let best: { edge: RoadEdge; dist: number } | null = null;
  for (const e of net.edges) {
    const d = distToSeg(p, e.a, e.b);
    if (!best || d < best.dist) best = { edge: e, dist: d };
  }
  return best;
}

/**
 * 一个街区 → 一个建议出入口。
 *
 * 采样点取**简化后每条边的中点**（不是顶点）：C# 也是沿边扫候选，
 * 顶点处的法向不稳定（两段边共用一个点），中点最可靠。
 * 取"间隙最小且合法"的那一条；全都不合法就取间隙最小的那条记下来（`attached=false`），
 * 便于面板上说清"为什么这块地收不下"。
 */
function suggestForPatch(patch: Patch, patchIndex: number, net: RoadNet): AccessRecord | null {
  const ring = patch.shape;
  if (ring.length < 3) return null;

  let cx = 0;
  let cy = 0;
  for (const p of ring) {
    cx += p.x;
    cy += p.y;
  }
  cx /= ring.length;
  cy /= ring.length;

  let best: AccessRecord | null = null;
  let bestScore = Infinity;

  for (let i = 0; i < ring.length; i++) {
    const a = ring[i];
    const b = ring[(i + 1) % ring.length];
    const ex = b.x - a.x;
    const ey = b.y - a.y;
    const elen = Math.hypot(ex, ey);
    if (elen < 1e-9) continue;
    // 边中点 + 朝外法向（远离质心）
    const mx = (a.x + b.x) / 2;
    const my = (a.y + b.y) / 2;
    let nx = mx - cx;
    let ny = my - cy;
    const nl = Math.hypot(nx, ny) || 1;
    nx /= nl;
    ny /= nl;

    // 沿法向扫 40 m，找到第一次"贴上车行道"的位置；这里直接用最近边的垂距换算间隙，
    // 因为 TownForge 的街区本来就挨着路，扫射线与取垂距等价，而后者更稳（不会漏在缝隙里）。
    const hit = nearestRoad(net, new Pt(mx + nx * 0.5, my + ny * 0.5));
    if (!hit) continue;
    const reachM = hit.dist * UNIT_M;
    if (reachM > REACH_M + ROAD_SPECS[hit.edge.cls].redLine) continue;

    const halfCarriage = (hit.edge.carriage * UNIT_M) / 2;
    const gapM = reachM - halfCarriage;
    const redLine = ROAD_SPECS[hit.edge.cls].redLine;
    const attached = gapM >= -redLine / 2 && gapM <= GAP_MAX_M;

    // 打分：合法的优先；同类里取间隙更接近 0 的（贴边最自然）
    const score = attached ? Math.abs(gapM) : 1000 + Math.abs(gapM);
    if (score < bestScore) {
      bestScore = score;
      best = {
        patchIndex,
        pos: [mx * UNIT_M, my * UNIT_M],
        dir: [nx, ny],
        widthM: DOOR_WIDTH_M,
        gapM,
        roadCls: hit.edge.cls,
        attached,
        reachM,
      };
    }
  }
  return best;
}

export function buildAccess(model: Model, net?: RoadNet): AccessReport {
  const roadNet = net ?? buildRoadNet(model);
  const accesses: AccessRecord[] = [];
  const perPatch = new Map<number, { count: number; attached: number; gapMin: number }>();
  const parkingReady: AccessReport['parkingReady'] = [];
  let patches = 0;
  let parkingTotal = 0;

  model.patches.forEach((patch, patchIndex) => {
    if (!patch.withinCity) return;
    patches++;
    const rec = suggestForPatch(patch, patchIndex, roadNet);
    if (rec) {
      accesses.push(rec);
      perPatch.set(patchIndex, { count: 1, attached: rec.attached ? 1 : 0, gapMin: rec.gapM });
    }

    // 停车场地块：这是 parking 家族的真实判据（唯一靠街道影响 ready 的一族）
    const use = (patch.ward as unknown as { lot?: { use: LandUse } } | null)?.lot?.use;
    if (use === 'parking') {
      parkingTotal++;
      const pred = predictReady({ generator: 'parking', givenMarkers: 0, suggestOk: rec?.attached === true });
      parkingReady.push({ patchIndex, ready: pred.ready === true, by: pred.by });
    }
  });

  const attachedCount = accesses.filter((a) => a.attached).length;
  const gaps = accesses.map((a) => a.gapM).sort((x, y) => x - y);
  const readyCount = parkingReady.filter((p) => p.ready).length;

  return {
    accesses,
    perPatch,
    parkingReady,
    stats: {
      accessCount: accesses.length,
      attachedShare: accesses.length ? attachedCount / accesses.length : 0,
      patchesWithAccess: perPatch.size,
      patches,
      parkingTotal,
      parkingReady: readyCount,
      gapMedM: gaps.length ? gaps[Math.floor(gaps.length / 2)] : 0,
    },
  };
}

export { toPt };
