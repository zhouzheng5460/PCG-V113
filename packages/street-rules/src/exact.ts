// block 模式的**精确构造**：地块是街区，街道就是围着它的那些路。
//
// 与 finder.ts 的分工（docs/A R3）：
//   · finder 从几何反推"这条边临不临街"——画法地块用它
//   · exact 直接从"哪条街贴着哪条边"构造——block 地块用它，结果更干净
//   两者必须一致；不一致就说明几何或拓扑有一边错了，这比任何单侧自检都敏感。
//
// 规则来自 Game/Selection/RoadNetwork.cs:396：
//   "one RoadAlongEdge per road (at its middle) and one RoadNode per corner node."
//   即：每条街在它与地界最近段的中点出一个沿边贴合点（externalT=0.5），每个街角出一个节点。

import { closestOnRing, bezier, dist, sampleBezier, type Vec2 } from '../../plan-ir/src/geom.ts';
import type { BoundaryAttachment, NearbyNet } from '../../plan-ir/src/types.ts';
import { netTangent, probeAlong, ALONG_TOLERANCE } from './finder.ts';

export interface Corner {
  /** ring 顶点序号。 */
  vertex: number;
  ringSegment: number;
  ringT: number;
  position: Vec2;
  /** 在这个角上交汇的街道 id（≥2 才算街角）。 */
  netIds: number[];
}

/** 找出"两条街交汇"的地界顶点。判定：顶点同时靠近 ≥2 条街。 */
export function findCorners(ring: readonly Vec2[], nets: readonly NearbyNet[]): Corner[] {
  const n = ring.length;
  const out: Corner[] = [];
  const samples = nets.map((net) => ({ net, pts: sampleBezier(net.a, net.b, net.c, net.d).pts }));

  for (let i = 0; i < n; i++) {
    const v = ring[i];
    const touching: number[] = [];
    for (const { net, pts } of samples) {
      const tol = Math.max(0, net.width) * 0.5 + ALONG_TOLERANCE;
      let near = false;
      for (const p of pts) {
        if (dist(p, v) <= tol) {
          near = true;
          break;
        }
      }
      if (near) touching.push(net.externalId);
    }
    if (touching.length >= 2) {
      out.push({
        vertex: i,
        ringSegment: (i - 1 + n) % n,
        ringT: 1,
        position: v,
        netIds: touching,
      });
    }
  }
  return out;
}

export interface ExactResult {
  attachments: BoundaryAttachment[];
  corners: Corner[];
  /** 每条街落在哪条边上（没贴上的不出现）。 */
  alongByNet: Map<number, { seg: number; sMid: number; overlap: number }>;
}

/** 精确构造：每条街 → 一个 roadAlongEdge；每个街角 → 一个 roadNode。 */
export function buildAttachmentsExact(ring: readonly Vec2[], nets: readonly NearbyNet[]): ExactResult {
  const n = ring.length;
  const attachments: BoundaryAttachment[] = [];
  const alongByNet = new Map<number, { seg: number; sMid: number; overlap: number }>();

  for (const net of nets) {
    const mid = pointAt(net, 0.5);
    const hit = closestOnRing(mid, ring);
    if (hit.seg < 0) continue;
    const len = dist(ring[hit.seg], ring[(hit.seg + 1) % n]);
    // 沿边贴合的前提：街的中线离这条边足够近（≤ 路宽/2 + 3）
    const tol = Math.max(0, net.width) * 0.5 + ALONG_TOLERANCE;
    if (hit.dist > tol) continue;
    // 重叠长度用同一份探测（probeAlong）算，别在这里另写一套——
    // 两份口径对不上时，短边上会出现"R1 说临街、R3 说不临街"（实测 54 份真实地块里有 3 份）。
    const probe = probeAlong(net, ring[hit.seg], ring[(hit.seg + 1) % n]);
    const overlap = Math.min(len, probe.overlap);
    if (overlap < Math.min(12, 0.4 * len)) continue;

    alongByNet.set(net.externalId, { seg: hit.seg, sMid: hit.t, overlap });
    attachments.push({
      kind: 'roadAlongEdge',
      externalId: net.externalId,
      externalT: 0.5,
      position: hit.point,
      direction: netTangent(net, 0.5),
      roadWidth: net.width,
      ringSegment: hit.seg,
      ringT: hit.t,
    });
  }

  const corners = findCorners(ring, nets);
  for (const c of corners) {
    attachments.push({
      kind: 'roadNode',
      externalId: c.netIds[0],
      externalT: 0,
      position: c.position,
      roadWidth: 0,
      ringSegment: c.ringSegment,
      ringT: c.ringT,
    });
  }

  return { attachments, corners, alongByNet };
}

/** 街在 t 处的点。必须直接求贝塞尔，**不能吸附到采样点**——吸附会让 100 m 的街取到 index 13/25 = 0.52，位置偏 2 m。 */
export function pointAt(net: NearbyNet, t: number): Vec2 {
  return bezier(net.a, net.b, net.c, net.d, t);
}
