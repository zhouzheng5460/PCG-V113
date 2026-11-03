/**
 * SceneIR —— 3D 的唯一输入（PRD §59 §68）
 *
 * 这一层是**纯数据**：只有环、高度、角色，没有任何 three.js 类型。
 * ThreeRealizer 负责把它变成 BufferGeometry；PCG / 规划逻辑永远不进入渲染层。
 *
 * 坐标：世界 (x=东, y=北, z=高)，单位 m。
 * 交给 three 时由 Realizer 统一换轴：(x, y, z) → (x, z, −y)。
 */
import { BuildingObj, GroundSurface, Id, ObjKind, Origin, ProjectModel, areaPaths, objectBounds } from './model';
import { Bounds, Paths, Ring, emptyBounds, groupRings, signedArea, strokeEnvelope } from './polygon';
import { Vec2 } from './vec';

/** 一个带洞的面：外环 + 洞 */
export interface ShapeSpec {
  outer: Ring;
  holes: Ring[];
}

/** 地面叠层高度，避免 z-fighting。顺序 = 谁压谁 */
export const ELEV = {
  parcel: 0.02,
  lawn: 0.03,
  paving: 0.035,
  parking: 0.04,
  nobuild: 0.045,
  road: 0.05,
  site: 0.06,
  access: 0.07,
} as const;

interface NodeBase {
  /** 渲染节点 id，V1 一个对象一个节点 */
  id: string;
  /** 反向指回 ProjectModel */
  sourceId: Id;
  kind: ObjKind;
  origin: Origin;
  locked: boolean;
  label: string;
}

export interface ExtrudeNode extends NodeBase {
  type: 'extrude';
  shapes: ShapeSpec[];
  height: number;
  /** 建筑用途，供风格化预留 */
  archetype: string;
}

export interface SurfaceNode extends NodeBase {
  type: 'surface';
  role: 'parcel' | 'ground' | 'nobuild';
  shapes: ShapeSpec[];
  elevation: number;
  /** 仅 ground：铺装 / 绿地 / 停车……决定材质与叠层高度 */
  surface?: GroundSurface;
}

export interface LineNode extends NodeBase {
  type: 'line';
  role: 'site';
  paths: Paths;
  elevation: number;
}

export interface RibbonNode extends NodeBase {
  type: 'ribbon';
  centerline: Vec2[];
  width: number;
  elevation: number;
}

export interface MarkerNode extends NodeBase {
  type: 'marker';
  pos: Vec2;
  dir: number;
  width: number;
  elevation: number;
}

export type SceneNode = ExtrudeNode | SurfaceNode | LineNode | RibbonNode | MarkerNode;

export interface SceneIR {
  nodes: SceneNode[];
  bounds: Bounds;
  stats: {
    buildings: number;
    generated: number;
    fixed: number;
    footprintArea: number;
    maxHeight: number;
  };
}

/* --------------------------- Paths → ShapeSpec --------------------------- */

/**
 * 环集合 → 「外环 + 洞」列表（PRD §68）。
 * 实现放在 polygon.groupRings，3D 与 PCG 共用同一套嵌套判定，避免两处逻辑分叉。
 */
export const toShapes = groupRings;

/** 某种铺装表面在地面上的叠层高度 */
function groundElevation(surface: GroundSurface): number {
  if (surface === 'lawn') return ELEV.lawn;
  if (surface === 'parking') return ELEV.parking;
  return ELEV.paving;
}

/* ------------------------------ IR 构建 ------------------------------ */

export function buildSceneIR(model: ProjectModel): SceneIR {
  const nodes: SceneNode[] = [];
  let bounds: Bounds = emptyBounds();
  let footprintArea = 0;
  let maxHeight = 0;
  let buildings = 0;
  let generated = 0;
  let fixed = 0;

  for (const id of model.order) {
    const o = model.objects[id];
    if (!o || !o.visible) continue;

    const ob = objectBounds(o);
    if (ob) {
      bounds.minX = Math.min(bounds.minX, ob.minX);
      bounds.minY = Math.min(bounds.minY, ob.minY);
      bounds.maxX = Math.max(bounds.maxX, ob.maxX);
      bounds.maxY = Math.max(bounds.maxY, ob.maxY);
    }
    if (o.origin === 'generated') generated++;
    if (o.fixed) fixed++;

    const base: NodeBase = {
      id: o.id,
      sourceId: o.id,
      kind: o.kind,
      origin: o.origin,
      locked: o.locked,
      label: o.name,
    };

    switch (o.kind) {
      case 'building': {
        const b = o as BuildingObj;
        const shapes = toShapes(b.footprint);
        if (!shapes.length) break;
        buildings++;
        maxHeight = Math.max(maxHeight, b.height);
        footprintArea += shapes.reduce(
          (s, sp) => s + Math.abs(signedArea(sp.outer)) - sp.holes.reduce((h, r) => h + Math.abs(signedArea(r)), 0),
          0,
        );
        nodes.push({ ...base, type: 'extrude', shapes, height: b.height, archetype: b.archetype });
        break;
      }
      case 'parcel':
      case 'nobuild': {
        const paths = areaPaths(o);
        if (!paths) break;
        const shapes = toShapes(paths);
        if (!shapes.length) break;
        nodes.push({
          ...base,
          type: 'surface',
          role: o.kind,
          shapes,
          elevation: ELEV[o.kind],
        });
        break;
      }
      case 'ground': {
        const shapes = toShapes(o.boundary);
        if (!shapes.length) break;
        nodes.push({
          ...base,
          type: 'surface',
          role: 'ground',
          shapes,
          elevation: groundElevation(o.surface),
          surface: o.surface,
        });
        break;
      }
      case 'site': {
        const paths = areaPaths(o);
        if (!paths) break;
        nodes.push({ ...base, type: 'line', role: 'site', paths, elevation: ELEV.site });
        break;
      }
      case 'road': {
        const env = strokeEnvelope(o.centerline, o.width / 2);
        if (env.length < 3) break;
        nodes.push({
          ...base,
          type: 'ribbon',
          centerline: o.centerline,
          width: o.width,
          elevation: ELEV.road,
        });
        break;
      }
      case 'access': {
        nodes.push({
          ...base,
          type: 'marker',
          pos: o.pos,
          dir: o.dir,
          width: o.width,
          elevation: ELEV.access,
        });
        break;
      }
    }
  }

  return {
    nodes,
    bounds,
    stats: {
      buildings,
      generated,
      fixed,
      footprintArea,
      maxHeight,
    },
  };
}

/** 道路包络（供 Realizer 直接用，避免它自己算几何） */
export function ribbonEnvelope(node: RibbonNode): Ring {
  return strokeEnvelope(node.centerline, node.width / 2);
}

export function isAreaNode(n: SceneNode): n is SurfaceNode {
  return n.type === 'surface';
}
