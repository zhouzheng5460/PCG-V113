import { Bounds, Paths, boundsOfPaths } from './polygon';
import { Vec2 } from './vec';

export type Id = string;

/** 用户绘制 vs PCG 生成 —— 必须随时可区分（PRD §39 / 原则 8） */
export type Origin = 'user' | 'generated';

export type ObjKind = 'site' | 'parcel' | 'building' | 'road' | 'access' | 'nobuild' | 'ground';

export type LayerKey =
  | 'site'
  | 'parcels'
  | 'roads'
  | 'buildings'
  | 'access'
  | 'ground'
  | 'landscape';

export type LandUse = 'urban' | 'industrial' | 'rural' | 'construction' | 'park' | 'civic';
export type BuildingStyle = 'geometric' | 'semiRealistic';
export type RoadClass = 'primary' | 'secondary' | 'local' | 'service' | 'pedestrian';
export type AccessType = 'main' | 'service' | 'pedestrian';
/**
 * 地面表面类型。
 * `parking` 单独一类而不是揉进 paving：停车场的观感（沥青 + 车位线）、
 * 面积统计、以及"能不能种树"都跟硬质铺装不一样，PRD §1 也是分开列的。
 */
export type GroundSurface = 'paving' | 'lawn' | 'parking' | 'water' | 'dirt';

interface CommonObj {
  id: Id;
  kind: ObjKind;
  name: string;
  origin: Origin;
  locked: boolean;
  visible: boolean;
  /** PCG 生成对象被 Promote 后变为 fixed（PRD §41） */
  fixed: boolean;
  /**
   * PCG 溯源：本对象由哪个对象"生成"出来。
   * 重新生成某个地块时，先把 generatedFrom === 该地块 的对象整体清掉（局部重算，PRD §70）。
   */
  generatedFrom?: Id;
}

export interface SiteObj extends CommonObj {
  kind: 'site';
  boundary: Paths;
}

export interface ParcelObj extends CommonObj {
  kind: 'parcel';
  boundary: Paths;
  landUse: LandUse;
  /** 建筑密度 0~1 */
  density: number;
  /** 容积率 */
  far: number;
  /** 高度区间 (m) */
  heightRange: [number, number];
  seed: number;
}

export interface BuildingObj extends CommonObj {
  kind: 'building';
  footprint: Paths;
  height: number;
  style: BuildingStyle;
  archetype: string;
  /** 结构形态，PCG 只读；V1 仅占位 */
  floors: number;
}

export interface RoadObj extends CommonObj {
  kind: 'road';
  centerline: Vec2[];
  roadClass: RoadClass;
  /** 路面总宽 (m) */
  width: number;
  sidewalk: boolean;
}

export interface AccessObj extends CommonObj {
  kind: 'access';
  pos: Vec2;
  width: number;
  /** 朝向角，弧度；0 = +X（东） */
  dir: number;
  accessType: AccessType;
  priority: number;
}

export interface NoBuildObj extends CommonObj {
  kind: 'nobuild';
  boundary: Paths;
}

export interface GroundObj extends CommonObj {
  kind: 'ground';
  boundary: Paths;
  surface: GroundSurface;
}

export type SceneObject =
  | SiteObj
  | ParcelObj
  | BuildingObj
  | RoadObj
  | AccessObj
  | NoBuildObj
  | GroundObj;

/** 面状对象（可参与布尔 / 偏移 / 顶点编辑） */
export type AreaObj = SiteObj | ParcelObj | BuildingObj | NoBuildObj | GroundObj;

export interface BackgroundImage {
  src: string;
  opacity: number;
  /** 图片左上角对应的世界坐标 */
  origin: Vec2;
  /** 世界米 / 图片像素 */
  metersPerPixel: number;
  /** 弧度 */
  rotation: number;
  locked: boolean;
  visible: boolean;
  widthPx: number;
  heightPx: number;
}

export interface ProjectSettings {
  /** 真实吸附单位 (m) */
  gridSize: number;
  gridVisible: boolean;
  gridSnap: boolean;
  snapEnabled: boolean;
  unit: 'm';
}

export interface ProjectModel {
  version: 1;
  id: Id;
  name: string;
  objects: Record<Id, SceneObject>;
  /** 绘制 / 命中顺序 */
  order: Id[];
  background: BackgroundImage | null;
  settings: ProjectSettings;
}

export const LAYER_OF: Record<ObjKind, LayerKey> = {
  site: 'site',
  parcel: 'parcels',
  building: 'buildings',
  road: 'roads',
  access: 'access',
  nobuild: 'landscape',
  ground: 'ground',
};

export const KIND_LABEL: Record<ObjKind, string> = {
  site: '园区范围',
  parcel: '地块',
  building: '建筑',
  road: '道路',
  access: '出入口',
  nobuild: '禁建区',
  // 不叫「铺装」了：地面现在同时承担铺装 / 绿地 / 停车，叫铺装会让属性面板说谎
  ground: '地面',
};

/**
 * 地面表面类型的显示名。
 * 2D 图例、属性面板下拉、3D 统计都从这里取，避免三处各写一份中文然后对不上。
 */
export const SURFACE_LABEL: Record<GroundSurface, string> = {
  paving: '硬质铺装',
  lawn: '绿地',
  parking: '停车场',
  water: '水面',
  dirt: '土面',
};

export const LANDUSE_LABEL: Record<LandUse, string> = {
  urban: '城市',
  industrial: '工业',
  rural: '乡村',
  construction: '工地',
  park: '公园',
  civic: '公共',
};

export const ROAD_LABEL: Record<RoadClass, string> = {
  primary: '主干道',
  secondary: '次干道',
  local: '支路',
  service: '服务道路',
  pedestrian: '人行道',
};

export const ROAD_WIDTH: Record<RoadClass, number> = {
  primary: 12,
  secondary: 8,
  local: 6,
  service: 5,
  pedestrian: 3,
};

export function isAreaObj(o: SceneObject): o is AreaObj {
  return (
    o.kind === 'site' ||
    o.kind === 'parcel' ||
    o.kind === 'building' ||
    o.kind === 'nobuild' ||
    o.kind === 'ground'
  );
}

export function areaPaths(o: SceneObject): Paths | null {
  if (!isAreaObj(o)) return null;
  if (o.kind === 'building') return o.footprint;
  return o.boundary;
}

export function objectBounds(o: SceneObject): Bounds | null {
  const p = areaPaths(o);
  if (p) return boundsOfPaths(p);
  if (o.kind === 'road') return boundsOfPaths([o.centerline]);
  if (o.kind === 'access') {
    return boundsOfPaths([
      [
        { x: o.pos.x - o.width / 2, y: o.pos.y - o.width / 2 },
        { x: o.pos.x + o.width / 2, y: o.pos.y + o.width / 2 },
      ],
    ]);
  }
  return null;
}

/** 所有顶点（供 Snap / 顶点编辑） */
export function objectVertices(o: SceneObject): Vec2[] {
  const p = areaPaths(o);
  if (p) return p.flat();
  if (o.kind === 'road') return o.centerline;
  if (o.kind === 'access') return [o.pos];
  return [];
}

export const defaultProjectSettings = (): ProjectSettings => ({
  gridSize: 1,
  gridVisible: true,
  gridSnap: true,
  snapEnabled: true,
  unit: 'm',
});
