import { Id, SceneObject, areaPaths, objectVertices, objectBounds } from '../core/model';
import { closestPointOnSegment, pathsEdges } from '../core/polygon';
import { Vec2, dist, mid } from '../core/vec';

/**
 * 统一 SnapManager（PRD §23~§28）
 * - 候选点由所有可见对象 + 网格 + 轴向对齐生成
 * - 命中半径用「屏幕像素」，因此任何缩放下手感一致
 * - 同分时：优先级高者胜，再按距离近者胜
 */
export type SnapType =
  | 'vertex'
  | 'endpoint'
  | 'intersection'
  | 'midpoint'
  | 'edge'
  | 'grid'
  | 'axis'
  | 'none';

export const SNAP_PRIORITY: Record<SnapType, number> = {
  vertex: 100,
  endpoint: 95,
  intersection: 90,
  midpoint: 80,
  edge: 70,
  axis: 50,
  grid: 40,
  none: 0,
};

export const SNAP_LABEL: Record<SnapType, string> = {
  vertex: 'Vertex',
  endpoint: 'Endpoint',
  intersection: 'Intersection',
  midpoint: 'Midpoint',
  edge: 'Edge',
  axis: 'Axis',
  grid: 'Grid',
  none: '',
};

/** 屏幕空间捕捉半径（px）——PRD §27 */
export const SNAP_RADIUS_PX = 11;

export interface SnapResult {
  point: Vec2;
  type: SnapType;
  label: string;
  priority: number;
  distance: number;
  /** 捕捉到的对象（用于高亮来源） */
  sourceId?: Id;
}

export interface SnapOptions {
  objects: SceneObject[];
  world: Vec2;
  /** 屏幕像素 → 世界（= radiusPx / scale） */
  radiusWorld: number;
  gridSize: number;
  gridSnap: boolean;
  gridVisible: boolean;
  enabled: boolean;
  excludeIds?: Set<Id>;
  /** 用于 Edge 捕捉时跳过某些对象 */
  edgeSnap?: boolean;
}

export function findSnap(o: SnapOptions): SnapResult | null {
  if (!o.enabled) return null;
  const R = o.radiusWorld;
  const cands: SnapResult[] = [];

  for (const obj of o.objects) {
    if (!obj.visible) continue;
    if (o.excludeIds?.has(obj.id)) continue;
    const b = objectBounds(obj);
    if (!b) continue;

    const paths = areaPaths(obj);
    if (paths) {
      for (const ring of paths) {
        for (const v of ring) {
          pushIfNear(cands, v, 'vertex', o.world, R, obj.id);
        }
      }
      for (const e of pathsEdges(paths)) {
        pushIfNear(cands, mid(e.a, e.b), 'midpoint', o.world, R, obj.id);
      }
      if (o.edgeSnap !== false) {
        for (const e of pathsEdges(paths)) {
          const c = closestPointOnSegment(o.world, e.a, e.b);
          if (c.dist <= R) {
            cands.push({
              point: c.point,
              type: 'edge',
              label: SNAP_LABEL.edge,
              priority: SNAP_PRIORITY.edge,
              distance: c.dist,
              sourceId: obj.id,
            });
          }
        }
      }
    }

    if (obj.kind === 'road') {
      const c = obj.centerline;
      for (let i = 0; i < c.length; i++) {
        const isEnd = i === 0 || i === c.length - 1;
        pushIfNear(cands, c[i], isEnd ? 'endpoint' : 'vertex', o.world, R, obj.id);
        if (i < c.length - 1) pushIfNear(cands, mid(c[i], c[i + 1]), 'midpoint', o.world, R, obj.id);
      }
    }
  }

  // 轴向对齐：与最近顶点同 X 或同 Y
  const nearestVertex = cands
    .filter((c) => c.type === 'vertex' || c.type === 'endpoint')
    .sort((a, b) => a.distance - b.distance)[0];
  if (nearestVertex) {
    const src = o.objects.find((x) => x.id === nearestVertex.sourceId);
    const vs = src ? objectVertices(src) : [];
    let bestAxis: SnapResult | null = null;
    for (const v of vs) {
      const candsAxis: Vec2[] = [
        { x: v.x, y: o.world.y },
        { x: o.world.x, y: v.y },
      ];
      for (const p of candsAxis) {
        const d = dist(p, o.world);
        if (d <= R && (!bestAxis || d < bestAxis.distance)) {
          bestAxis = {
            point: p,
            type: 'axis',
            label: SNAP_LABEL.axis,
            priority: SNAP_PRIORITY.axis,
            distance: d,
            sourceId: src?.id,
          };
        }
      }
    }
    if (bestAxis) cands.push(bestAxis);
  }

  // 网格
  if (o.gridSnap && o.gridSize > 0) {
    const g = {
      x: Math.round(o.world.x / o.gridSize) * o.gridSize,
      y: Math.round(o.world.y / o.gridSize) * o.gridSize,
    };
    pushIfNear(cands, g, 'grid', o.world, R, undefined);
  }

  if (!cands.length) return null;
  cands.sort((a, b) => b.priority - a.priority || a.distance - b.distance);
  return cands[0];
}

function pushIfNear(
  out: SnapResult[],
  p: Vec2,
  type: SnapType,
  world: Vec2,
  R: number,
  sourceId?: Id,
) {
  const d = dist(p, world);
  if (d > R) return;
  out.push({ point: p, type, label: SNAP_LABEL[type], priority: SNAP_PRIORITY[type], distance: d, sourceId });
}
