// PCG 数据 → Placemaker 输入（CaseInput）。
//
// 这一步是整个合并的"接缝"：PCG 只知道"我画了一条路、一个地块、一个出入口"，
// Placemaker 要的是"带宽度和类型的贝塞尔街道 + 边界贴合点 + 8 m 宗地格网"。
// 转换规则全部来自 docs/A §3，这里只负责实现。
//
// 三条必须守住的约定：
//   1) 一条折线道路 → 每段一条 NearbyNet（CS2 里一条道路边就是一个 net），
//      共享同一个道路 id，用段号区分：id = roadId × 1000 + segIdx
//   2) 步行道 category = pathway，**不算车行可达**（停车族的出入口只认车行道）
//   3) 宗地格网（ZoneGrid）的 direction 是"由地块指向道路"，不是道路方向

import { dist, dot, ccw, norm, perp, pointInRing, type Vec2 } from '../../plan-ir/src/geom.ts';
import { area } from '../../plan-ir/src/geom.ts';
import { netFromSegment } from '../../street-rules/src/finder.ts';
import { buildAttachmentsExact } from '../../street-rules/src/exact.ts';
import type {
  BoundaryAttachment,
  CaseInput,
  EdgeMarker,
  Obstacle,
  ZoneGrid,
  ZoneKind,
} from '../../plan-ir/src/types.ts';
import { DRIVABLE_CLASSES, type PcgAccess, type PcgBuilding, type PcgParcel, type PcgRoad } from './model.ts';

/** 道路离地块超过这个距离就不塞进 NearbyNets（Placemaker 也只用临界的几条）。 */
const NET_SEARCH = 40;

export function numId(s: string): number {
  let h = 2166136261 >>> 0;
  for (let i = 0; i < s.length; i++) {
    h ^= s.charCodeAt(i);
    h = Math.imul(h, 16777619) >>> 0;
  }
  return h % 900000;
}

export interface StreetBuildResult {
  nets: import('../../plan-ir/src/types.ts').NearbyNet[];
  /** 每段道路落在哪条 net 上，便于回溯"这条边属于哪条路"。 */
  segmentOf: Map<number, { roadId: string; segIdx: number }>;
  dropped: number;
}

/**
 * 折线道路 → 贝塞尔街道。
 * 每条折线段的控制点放在 1/3、2/3（直线）；**不要**把折角拟合成一条平滑曲线，
 * 那样平行度判据会在中段失效（docs/A §3.2、计划 §7 第 9 条）。
 */
export function parcelStreets(parcelRing: readonly Vec2[], roads: readonly PcgRoad[]): StreetBuildResult {
  const nets: StreetBuildResult['nets'] = [];
  const segmentOf = new Map<number, { roadId: string; segIdx: number }>();
  let dropped = 0;

  const baseId = numId('tmp');
  for (const road of roads) {
    const rid = numId(road.id) || baseId;
    for (let i = 0; i + 1 < road.centerline.length; i++) {
      const a = road.centerline[i];
      const b = road.centerline[i + 1];
      if (dist(a, b) < 0.5) continue;
      const id = rid * 1000 + i;
      // 只保留地块附近的街道：到任一地块顶点的距离，够近才进
      const near = nearestDistToRing(a, b, parcelRing) <= road.width / 2 + NET_SEARCH;
      if (!near) {
        dropped++;
        continue;
      }
      nets.push(
        netFromSegment(id, a, b, road.width, {
          category: road.roadClass === 'pedestrian' ? 'pathway' : 'road',
          hasSidewalk: road.sidewalk,
          speedLimit: road.roadClass === 'primary' ? 50 : road.roadClass === 'secondary' ? 40 : 30,
        }),
      );
      segmentOf.set(id, { roadId: road.id, segIdx: i });
    }
  }
  return { nets, segmentOf, dropped };
}

function nearestDistToRing(a: Vec2, b: Vec2, ring: readonly Vec2[]): number {
  let best = Infinity;
  for (let t = 0; t <= 1.0001; t += 0.1) {
    const p: Vec2 = [a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t];
    for (let i = 0; i < ring.length; i++) {
      const q = ring[i];
      const r = ring[(i + 1) % ring.length];
      best = Math.min(best, dist(p, q), segDist(p, q, r));
    }
  }
  return best;
}

function segDist(p: Vec2, a: Vec2, b: Vec2): number {
  const ab = [b[0] - a[0], b[1] - a[1]] as Vec2;
  const l2 = dot(ab, ab);
  const t = l2 < 1e-9 ? 0 : Math.max(0, Math.min(1, dot([p[0] - a[0], p[1] - a[1]], ab) / l2));
  return dist(p, [a[0] + ab[0] * t, a[1] + ab[1] * t]);
}

/**
 * 由一条临街边生成 8 m 宗地格网。
 *
 * 语义（与 Core/Contracts.cs:99 对齐）：
 *   Size.x 格 = 沿街方向的格数，Size.y 格 = 从街边往里（地块内侧）的进深格数
 *   Direction = **由地块指向道路**（ZoneGrid 注释原话："points from the block towards its road"）
 *   Position = 格网中心
 *
 * 注意这里生成的是"我们自己地块将来会被划分的宗地"，交给 Placemaker 是为了让它的
 * zoning patch 落在 8 m 格上（ZoneGuard 会按格吸附），从而与我们的建筑位模数一致。
 */
export function zoneGridOf(edgeA: Vec2, edgeB: Vec2, cells: number, inward: Vec2): ZoneGrid {
  const CELL = 8;
  const len = dist(edgeA, edgeB);
  const raw = Math.max(0, Math.floor(len / CELL + 1e-6)) || 1;
  const u = norm([edgeB[0] - edgeA[0], edgeB[1] - edgeA[1]]);
  const v = norm(inward);
  const nx = raw;
  const ny = Math.max(1, Math.min(6, Math.round(cells)));
  const w = nx * CELL;
  const d = ny * CELL;
  const center: Vec2 = [
    edgeA[0] + (u[0] * w) / 2 + v[0] * (d / 2),
    edgeA[1] + (u[1] * w) / 2 + v[1] * (d / 2),
  ];
  // 朝向：由地块指向道路 = inward 的反向
  return { position: center, direction: [-v[0], -v[1]], size: [nx, ny] };
}

/** 格网的四个角（渲染与校验用）。 */
export function zoneGridCorners(g: ZoneGrid): Vec2[] {
  const CELL = 8;
  const towardRoad = norm(g.direction);
  const u = perp(towardRoad); // 沿街方向
  const w = g.size[0] * CELL;
  const d = g.size[1] * CELL;
  // 从格网远端（远离道路）开始
  const far: Vec2 = [g.position[0] - towardRoad[0] * (d / 2) - u[0] * (w / 2), g.position[1] - towardRoad[1] * (d / 2) - u[1] * (w / 2)];
  return [
    far,
    [far[0] + u[0] * w, far[1] + u[1] * w],
    [far[0] + u[0] * w + towardRoad[0] * d, far[1] + u[1] * w + towardRoad[1] * d],
    [far[0] + towardRoad[0] * d, far[1] + towardRoad[1] * d],
  ];
}

/** 8 m 格网拆成小格（渲染用）。 */
export function zoneGridCells(g: ZoneGrid): Vec2[][] {
  const CELL = 8;
  const towardRoad = norm(g.direction);
  const u = perp(towardRoad);
  const out: Vec2[][] = [];
  for (let ix = 0; ix < g.size[0]; ix++) {
    for (let iy = 0; iy < g.size[1]; iy++) {
      const far: Vec2 = [
        g.position[0] - towardRoad[0] * (g.size[1] * CELL) / 2 - u[0] * (g.size[0] * CELL) / 2,
        g.position[1] - towardRoad[1] * (g.size[1] * CELL) / 2 - u[1] * (g.size[0] * CELL) / 2,
      ];
      const o: Vec2 = [far[0] + u[0] * ix * CELL + towardRoad[0] * iy * CELL, far[1] + u[1] * ix * CELL + towardRoad[1] * iy * CELL];
      out.push([
        o,
        [o[0] + u[0] * CELL, o[1] + u[1] * CELL],
        [o[0] + (u[0] + towardRoad[0]) * CELL, o[1] + (u[1] + towardRoad[1]) * CELL],
        [o[0] + towardRoad[0] * CELL, o[1] + towardRoad[1] * CELL],
      ]);
    }
  }
  return out;
}

/** 建筑轮廓 → 避让四边形。非四边形先退化成外接矩形（拆 quad 的完整规则在 docs/B §3.4）。 */
export function buildingObstacles(buildings: readonly PcgBuilding[]): Obstacle[] {
  const out: Obstacle[] = [];
  for (const b of buildings) {
    if (b.ring.length < 3) continue;
    if (area(b.ring) < 0.5) continue;
    if (b.ring.length === 4) {
      out.push({ quad: [b.ring[0], b.ring[1], b.ring[2], b.ring[3]], kind: 'building' });
      continue;
    }
    let minX = Infinity, minY = Infinity, maxX = -Infinity, maxY = -Infinity;
    for (const p of b.ring) {
      minX = Math.min(minX, p[0]);
      minY = Math.min(minY, p[1]);
      maxX = Math.max(maxX, p[0]);
      maxY = Math.max(maxY, p[1]);
    }
    out.push({
      quad: [
        [minX, minY],
        [maxX, minY],
        [maxX, maxY],
        [minX, maxY],
      ],
      kind: 'building',
    });
  }
  return out;
}

/** PCG 的出入口 → Placemaker 的 marker。 */
export function accessMarkers(accesses: readonly PcgAccess[], ring: readonly Vec2[]): EdgeMarker[] {
  const out: EdgeMarker[] = [];
  for (const a of accesses) {
    // 找到离这个出入口最近的那条边
    let seg = 0;
    let t = 0;
    let best = Infinity;
    for (let i = 0; i < ring.length; i++) {
      const p0 = ring[i];
      const p1 = ring[(i + 1) % ring.length];
      const ab: Vec2 = [p1[0] - p0[0], p1[1] - p0[1]];
      const l2 = dot(ab, ab);
      const tt = l2 < 1e-9 ? 0 : Math.max(0, Math.min(1, dot([a.pos[0] - p0[0], a.pos[1] - p0[1]], ab) / l2));
      const q: Vec2 = [p0[0] + ab[0] * tt, p0[1] + ab[1] * tt];
      const d = dist(q, a.pos);
      if (d < best) {
        best = d;
        seg = i;
        t = tt;
      }
    }
    out.push({
      kind: a.accessType === 'main' ? 'entrance' : a.accessType === 'service' ? 'access' : 'pedestrian',
      ringSegment: seg,
      ringT: t,
      position: a.pos,
    });
  }
  return out;
}

export interface ParcelToCaseOptions {
  /** 宗地进深格数（1..6），用于生成 ZoneGrid。 */
  depthCells?: number;
  layoutScale?: number;
  edgeMargin?: number;
  settings?: Record<string, number | boolean | string | string[]>;
  seed?: number;
  /** 保留建筑：传 true 时建筑进 obstacles（记得同时开 clipMode，见 docs/D D-010）。 */
  keepBuildings?: boolean;
}

export interface ParcelCase {
  input: CaseInput;
  attachments: BoundaryAttachment[];
  zoneGrids: ZoneGrid[];
  /** 每条边的朝向内侧法向（生成宗地格网与切楼位时都要用）。 */
  inwardNormals: Vec2[];
  notes: string[];
}

/**
 * 主入口：一个 PCG 地块 + 周边道路 + 出入口 → 完整的 CaseInput。
 * 这是 P1 的核心交付物（计划里的 `parcelStreets()` + `zoneGridOf()` 合体）。
 */
export function parcelToCase(
  parcel: PcgParcel,
  roads: readonly PcgRoad[],
  accesses: readonly PcgAccess[] = [],
  buildings: readonly PcgBuilding[] = [],
  opts: ParcelToCaseOptions = {},
): ParcelCase {
  const notes: string[] = [];
  const ring = ccw(parcel.ring);
  if (!pointInRing(ring[0], ring)) notes.push('地界绕序被纠正为逆时针');

  const { nets, dropped } = parcelStreets(ring, roads);
  if (dropped) notes.push(`有 ${dropped} 段道路离地块太远（>${NET_SEARCH} m），未纳入`);

  // 每条边的内侧法向：地块逆时针时，左法向就是内侧
  const inwardNormals: Vec2[] = [];
  for (let i = 0; i < ring.length; i++) {
    const a = ring[i];
    const b = ring[(i + 1) % ring.length];
    inwardNormals.push(norm(perp([b[0] - a[0], b[1] - a[1]])));
  }

  // 宗地格网：只给"有车行道贴着"的边生成
  const zoneGrids: ZoneGrid[] = [];
  const depthCells = opts.depthCells ?? 3;
  for (let i = 0; i < ring.length; i++) {
    const a = ring[i];
    const b = ring[(i + 1) % ring.length];
    if (dist(a, b) < 8) continue;
    const horiz = nets.filter((n) => {
      // 这条街道是否贴着这条边：用中点距离粗判（精确判定交给 R1）
      const mid: Vec2 = [(a[0] + b[0]) / 2, (a[1] + b[1]) / 2];
      return segDist(mid, n.a, n.d) <= n.width / 2 + 3;
    });
    if (!horiz.length) continue;
    zoneGrids.push(zoneGridOf(a, b, depthCells, inwardNormals[i]));
  }

  const exact = buildAttachmentsExact(ring, nets);
  if (exact.attachments.length === 0) notes.push('没有构造出任何贴合点：这条地块喂给 Placemaker 会 ready=no');

  const markers = accessMarkers(accesses, ring);
  const drivable = roads.filter((r) => DRIVABLE_CLASSES.includes(r.roadClass));
  if (drivable.length === 0) notes.push('周边没有车行道，停车族/商场族会用不了');

  const input: CaseInput = {
    shape: { ring: ring.map((p) => [p[0], 0, p[1]] as const) as never, source: 'block', attachments: exact.attachments },
    site: {
      nearbyNets: nets,
      obstacles: opts.keepBuildings ? buildingObstacles(buildings) : [],
      zoneGrids,
      layoutScale: opts.layoutScale ?? 0.5,
      edgeMargin: opts.edgeMargin ?? 2,
    },
    markers,
    settings: opts.settings ?? {},
    seed: opts.seed ?? 1,
    regionTags: ['block'],
  };

  return { input, attachments: exact.attachments, zoneGrids, inwardNormals, notes };
}

/** 地块的 zone kind（PCG 用地类型 → Placemaker 分区），见 docs/B §4。 */
export function zoneKindOf(parcel: PcgParcel, farFromCenter = false): ZoneKind {
  switch (parcel.landUse) {
    case 'industrial':
      return 'industrial';
    case 'urban':
      return farFromCenter ? 'residentialMedium' : 'mixed';
    case 'rural':
      return 'residentialLow';
    case 'civic':
      return 'officeLow';
    case 'construction':
    case 'park':
    default:
      return 'commercialLow';
  }
}
