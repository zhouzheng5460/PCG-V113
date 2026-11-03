// ProjectModel —— 编辑器的骨架。**照搬 PCG**（`vendor/pcg-editor-src/core/model.ts`），
// 只改一处：`parcel.landUse`（6 类）换成 `parcel.zoneKind`（我们的 11 类）。
//
// 为什么必须换：`landUse` 是个"给美术看"的粗分类，而我们的生成算法
// （slots / KIND_SPECS / 资产匹配）需要的是"这块地盖什么"的精确口径 ——
// 11 类 ZoneKind 直接决定面宽、进深、层数区间与层高。留着两套分类只会让面板说谎。
// 详见 docs/G §3 的映射表。
//
// 三条语义铁律（PCG PRD §39/§41/§70，不能丢）：
//   1. `origin`: 用户画的 user / 生成的 generated —— 永远可区分，颜色也不同
//   2. `fixed`: 生成物可"转为固定"，此后生成算法不再改动它
//   3. `generatedFrom`: 生成物记住"我是哪块地生成的"，重算时**只清这一块**

import { ZONE_LABEL } from '../../plan-ir/src/types.ts';
import type { Paths, Ring } from './polygon.ts';
import type { Vec2 } from './vec.ts';

export type Id = string;

export type Origin = 'user' | 'generated';

export type ObjKind = 'site' | 'parcel' | 'building' | 'road' | 'access' | 'nobuild' | 'ground';

export type LayerKey = 'site' | 'parcels' | 'roads' | 'buildings' | 'access' | 'ground' | 'landscape';

/** 我们的 11 类用地（和 Placemaker 的 ZoneGrid prefab 一一对应）。 */
export type ZoneKind =
  | 'commercialLow'
  | 'commercialHigh'
  | 'mixed'
  | 'residentialLow'
  | 'residentialRow'
  | 'residentialMedium'
  | 'residentialHigh'
  | 'lowRent'
  | 'officeLow'
  | 'officeHigh'
  | 'industrial';

export const ZONE_KINDS: ZoneKind[] = [
  'commercialLow',
  'commercialHigh',
  'mixed',
  'residentialLow',
  'residentialRow',
  'residentialMedium',
  'residentialHigh',
  'lowRent',
  'officeLow',
  'officeHigh',
  'industrial',
];

/** 面板下拉用（中文名直接取 plan-ir 的表，避免三处各写一份）。 */
export const ZONE_OPTIONS: Record<string, string> = Object.fromEntries(
  ZONE_KINDS.map((k) => [k, ZONE_LABEL[k]]),
);

export type BuildingStyle = 'geometric' | 'semiRealistic';
export type RoadClass = 'primary' | 'secondary' | 'local' | 'service' | 'pedestrian';
export type AccessType = 'main' | 'service' | 'pedestrian';
export type GroundSurface = 'paving' | 'lawn' | 'parking' | 'water' | 'dirt';

interface CommonObj {
  id: Id;
  kind: ObjKind;
  name: string;
  origin: Origin;
  locked: boolean;
  visible: boolean;
  fixed: boolean;
  generatedFrom?: Id;
}

export interface SiteObj extends CommonObj {
  kind: 'site';
  boundary: Paths;
}

export interface ParcelObj extends CommonObj {
  kind: 'parcel';
  boundary: Paths;
  /** 用地类型（11 类）。决定面宽/进深/层数区间/层高。 */
  zoneKind: ZoneKind;
  /** 建筑密度上限 0~1：切楼位时最多占多少地。 */
  density: number;
  /** 容积率目标：`far × 地块面积 / 基底面积` 反过来夹层数。 */
  far: number;
  /** 高度区间（米），夹取层的上下限。 */
  heightRange: [number, number];
  seed: number;
}

export interface BuildingObj extends CommonObj {
  kind: 'building';
  footprint: Paths;
  height: number;
  style: BuildingStyle;
  archetype: string;
  floors: number;
  /** 由哪个楼位生成的（`z0-b0-r0`）。人手画的没有。 */
  slotId?: string;
}

export interface RoadObj extends CommonObj {
  kind: 'road';
  centerline: Vec2[];
  roadClass: RoadClass;
  /** 路面总宽（m）。 */
  width: number;
  sidewalk: boolean;
}

export interface AccessObj extends CommonObj {
  kind: 'access';
  pos: Vec2;
  width: number;
  /** 朝向角，弧度；0 = +X。 */
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

export type SceneObject = SiteObj | ParcelObj | BuildingObj | RoadObj | AccessObj | NoBuildObj | GroundObj;

export type AreaObj = SiteObj | ParcelObj | BuildingObj | NoBuildObj | GroundObj;

export interface ProjectSettings {
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
  order: Id[];
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
  ground: '地面',
};

export const SURFACE_LABEL: Record<GroundSurface, string> = {
  paving: '硬质铺装',
  lawn: '绿地',
  parking: '停车场',
  water: '水面',
  dirt: '土面',
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

export const ACCESS_LABEL: Record<AccessType, string> = {
  main: '主入口',
  service: '服务入口',
  pedestrian: '人行入口',
};

export function isAreaObj(o: SceneObject): o is AreaObj {
  return o.kind === 'site' || o.kind === 'parcel' || o.kind === 'building' || o.kind === 'nobuild' || o.kind === 'ground';
}

export function areaPaths(o: SceneObject): Paths | null {
  if (!isAreaObj(o)) return null;
  if (o.kind === 'building') return o.footprint;
  return o.boundary;
}

/** 所有可拖的顶点（顶点编辑 / 捕捉都用它）。 */
export function objectVertices(o: SceneObject): Vec2[] {
  const p = areaPaths(o);
  if (p) return p.flat();
  if (o.kind === 'road') return o.centerline;
  if (o.kind === 'access') return [o.pos];
  return [];
}

export const defaultSettings = (): ProjectSettings => ({
  gridSize: 1,
  gridVisible: true,
  gridSnap: true,
  snapEnabled: true,
  unit: 'm',
});

let idSeq = 0;
export function newId(prefix: string): Id {
  idSeq += 1;
  return `${prefix}_${Date.now().toString(36)}${idSeq.toString(36)}`;
}

/**
 * 建对象时补默认字段，避免每处 new 都写全 8 个字段。
 *
 * 泛型 `<K extends ObjKind>` 是必须的：返回 `CommonObj` 的话 `kind` 会被拓宽成
 * `ObjKind`，于是 `{...commonDefaults('building', x), footprint}` 就不是 `BuildingObj` 了 ——
 * 44 个字面量类型会在每个调用处报错。这个坑写一次就够了。
 */
export function commonDefaults<K extends ObjKind>(kind: K, name: string): CommonDefaults<K> {
  return { id: newId(kind), kind, name, origin: 'user', locked: false, visible: true, fixed: false };
}

export interface CommonDefaults<K extends ObjKind> {
  id: Id;
  kind: K;
  name: string;
  origin: Origin;
  locked: boolean;
  visible: boolean;
  fixed: boolean;
  generatedFrom?: Id;
}

export function emptyProject(name = '未命名项目'): ProjectModel {
  return { version: 1, id: newId('proj'), name, objects: {}, order: [], settings: defaultSettings() };
}

export function addObject(model: ProjectModel, o: SceneObject): ProjectModel {
  return { ...model, objects: { ...model.objects, [o.id]: o }, order: [...model.order, o.id] };
}

export function updateObjectIn(model: ProjectModel, id: Id, patch: Partial<SceneObject>): ProjectModel {
  const cur = model.objects[id];
  if (!cur) return model;
  return { ...model, objects: { ...model.objects, [id]: { ...cur, ...patch } as SceneObject } };
}

export function removeObjects(model: ProjectModel, ids: Id[]): ProjectModel {
  const strip = new Set(ids);
  const objects: Record<Id, SceneObject> = {};
  for (const [k, v] of Object.entries(model.objects)) if (!strip.has(k)) objects[k] = v;
  return { ...model, objects, order: model.order.filter((id) => !strip.has(id)) };
}

export function cloneObject(o: SceneObject, shift: Vec2): SceneObject {
  const copy = JSON.parse(JSON.stringify(o)) as SceneObject;
  copy.id = newId(o.kind);
  copy.name = `${o.name} 副本`;
  copy.origin = 'user';
  copy.fixed = false;
  delete copy.generatedFrom;
  const move = (p: Vec2): Vec2 => ({ x: p.x + shift.x, y: p.y + shift.y });
  if (isAreaObj(copy)) {
    const paths = copy.kind === 'building' ? copy.footprint : copy.boundary;
    const next = paths.map((r: Ring) => r.map(move));
    if (copy.kind === 'building') copy.footprint = next;
    else copy.boundary = next;
  } else if (copy.kind === 'road') {
    copy.centerline = copy.centerline.map(move);
  } else if (copy.kind === 'access') {
    copy.pos = move(copy.pos);
  }
  return copy;
}

// ─────────────────────────────────────────── 示例项目

/**
 * 示例园区：一块 L 形园区 + 两块地块 + 一条主路 + 两个出入口 + 一片绿地。
 *
 * 为什么要有它：PCG 的顶栏有「示例」按钮，作用不是好看的 demo，而是
 * **让第一次打开的人三秒内看到"这套东西能做什么"**。空画布 + 空面板是最差的第一次体验。
 * 尺寸按真实小镇街区给（约 160 × 120 m），这样生成出来的楼是真尺度。
 */
export function demoProject(): ProjectModel {
  const m = emptyProject('示例园区');
  const site: SiteObj = {
    ...commonDefaults('site', '园区 A'),
    origin: 'user',
    boundary: [
      [
        { x: 0, y: 0 },
        { x: 160, y: 0 },
        { x: 160, y: 80 },
        { x: 90, y: 80 },
        { x: 90, y: 120 },
        { x: 0, y: 120 },
      ],
    ],
  };
  const road: RoadObj = {
    ...commonDefaults('road', '主路'),
    origin: 'user',
    centerline: [
      { x: -10, y: 60 },
      { x: 80, y: 60 },
      { x: 175, y: 62 },
    ],
    roadClass: 'secondary',
    width: 8,
    sidewalk: true,
  };
  const p1: ParcelObj = {
    ...commonDefaults('parcel', '地块 1 · 商业'),
    origin: 'user',
    boundary: [
      [
        { x: 12, y: 14 },
        { x: 76, y: 14 },
        { x: 76, y: 48 },
        { x: 12, y: 48 },
      ],
    ],
    zoneKind: 'commercialLow',
    density: 0.45,
    far: 0.9,
    heightRange: [6, 15],
    seed: 1,
  };
  const p2: ParcelObj = {
    ...commonDefaults('parcel', '地块 2 · 住宅'),
    origin: 'user',
    boundary: [
      [
        { x: 100, y: 14 },
        { x: 150, y: 14 },
        { x: 150, y: 48 },
        { x: 100, y: 48 },
      ],
    ],
    zoneKind: 'residentialMedium',
    density: 0.4,
    far: 1.2,
    heightRange: [9, 24],
    seed: 2,
  };
  const g1: GroundObj = {
    ...commonDefaults('ground', '中心绿地'),
    origin: 'user',
    boundary: [
      [
        { x: 12, y: 70 },
        { x: 80, y: 70 },
        { x: 80, y: 110 },
        { x: 12, y: 110 },
      ],
    ],
    surface: 'lawn',
  };
  const a1: AccessObj = {
    ...commonDefaults('access', '园区主入口'),
    origin: 'user',
    pos: { x: 80, y: 60 },
    width: 8,
    dir: 0,
    accessType: 'main',
    priority: 1,
  };
  const a2: AccessObj = {
    ...commonDefaults('access', '地块 1 车行口'),
    origin: 'user',
    pos: { x: 40, y: 48 },
    width: 6,
    dir: -Math.PI / 2,
    accessType: 'service',
    priority: 2,
  };
  for (const o of [site, road, p1, p2, g1, a1, a2] as SceneObject[]) addTo(m, o);
  return m;
}

function addTo(m: ProjectModel, o: SceneObject): void {
  m.objects[o.id] = o;
  m.order.push(o.id);
}
