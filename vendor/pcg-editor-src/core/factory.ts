import { Paths, rectRing } from './polygon';
import { Vec2, v2 } from './vec';
import {
  AccessObj,
  AccessType,
  BuildingObj,
  GroundObj,
  GroundSurface,
  Id,
  NoBuildObj,
  ObjKind,
  ParcelObj,
  ProjectModel,
  RoadClass,
  RoadObj,
  SceneObject,
  SiteObj,
  defaultProjectSettings,
} from './model';
import { ROAD_WIDTH } from './model';

let _n = 0;
export const uid = (prefix = 'o'): Id =>
  `${prefix}_${Date.now().toString(36)}${(_n++).toString(36)}`;

const common = (kind: ObjKind, name: string) => ({
  id: uid(kind),
  kind,
  name,
  origin: 'user' as const,
  locked: false,
  visible: true,
  fixed: false,
});

export function makeSite(boundary: Paths, name = '园区范围'): SiteObj {
  return { ...common('site', name), kind: 'site', boundary };
}

export function makeParcel(
  boundary: Paths,
  opts: Partial<Pick<ParcelObj, 'landUse' | 'density' | 'far' | 'heightRange' | 'seed' | 'name'>> = {},
): ParcelObj {
  return {
    ...common('parcel', opts.name ?? '地块'),
    kind: 'parcel',
    boundary,
    landUse: opts.landUse ?? 'industrial',
    density: opts.density ?? 0.35,
    far: opts.far ?? 0.8,
    heightRange: opts.heightRange ?? [9, 18],
    seed: opts.seed ?? Math.floor(Math.random() * 1e6),
  };
}

export function makeBuilding(
  footprint: Paths,
  opts: Partial<Pick<BuildingObj, 'height' | 'style' | 'archetype' | 'name' | 'floors' | 'fixed' | 'origin'>> = {},
): BuildingObj {
  const height = opts.height ?? 12;
  return {
    ...common('building', opts.name ?? '建筑'),
    kind: 'building',
    origin: opts.origin ?? 'user',
    fixed: opts.fixed ?? false,
    footprint,
    height,
    style: opts.style ?? 'geometric',
    archetype: opts.archetype ?? 'block',
    floors: opts.floors ?? Math.max(1, Math.round(height / 4)),
  };
}

export function makeRoad(
  centerline: Vec2[],
  opts: Partial<Pick<RoadObj, 'roadClass' | 'width' | 'sidewalk' | 'name' | 'origin'>> = {},
): RoadObj {
  const roadClass: RoadClass = opts.roadClass ?? 'local';
  return {
    ...common('road', opts.name ?? '道路'),
    kind: 'road',
    origin: opts.origin ?? 'user',
    centerline,
    roadClass,
    width: opts.width ?? ROAD_WIDTH[roadClass],
    sidewalk: opts.sidewalk ?? true,
  };
}

export function makeAccess(
  pos: Vec2,
  opts: Partial<Pick<AccessObj, 'width' | 'dir' | 'accessType' | 'priority' | 'name'>> = {},
): AccessObj {
  return {
    ...common('access', opts.name ?? '出入口'),
    kind: 'access',
    pos,
    width: opts.width ?? 8,
    dir: opts.dir ?? 0,
    accessType: (opts.accessType ?? 'main') as AccessType,
    priority: opts.priority ?? 1,
  };
}

export function makeNoBuild(boundary: Paths, name = '禁建区'): NoBuildObj {
  return { ...common('nobuild', name), kind: 'nobuild', boundary };
}

export function makeGround(boundary: Paths, surface: GroundSurface = 'paving'): GroundObj {
  return { ...common('ground', '铺装'), kind: 'ground', boundary, surface };
}

export function emptyProject(name = '未命名项目'): ProjectModel {
  return {
    version: 1,
    id: uid('proj'),
    name,
    objects: {},
    order: [],
    background: null,
    settings: defaultProjectSettings(),
  };
}

export function addTo(p: ProjectModel, obj: SceneObject): ProjectModel {
  return {
    ...p,
    objects: { ...p.objects, [obj.id]: obj },
    order: [...p.order, obj.id],
  };
}

/** 首次进入时的示例：一个工业园 + 主体建筑 + 主路 + 出入口 */
export function demoProject(): ProjectModel {
  let p = emptyProject('示例 · 工业园');
  const site = makeSite([rectRing(v2(0, 0), v2(160, 110))], '园区边界');
  // 固定 seed：示例项目每次打开都是同一份布局，截图与 E2E 才有可比性
  const parcel = makeParcel([rectRing(v2(8, 8), v2(152, 102))], { landUse: 'industrial', name: '地块 A', seed: 2024 });
  /*
   * 主体建筑（固定）压在场地中西部，既有辅楼守东北，高压走廊切东南角。
   * 注意这是**密度很高**的演示场地：留给 PCG 的是"一圈院子 + 一条 12 m 夹缝"，
   * 这是刻意的——Phase 5 要能证明"在既有限制里塞进配套楼"，而不是在空地上堆房子。
   * 代价是内部路只能长成"主路 + 少量引道"，成规模的路网要靠 Phase 6 的路网生成。
   */
  const main = makeBuilding([rectRing(v2(28, 40), v2(92, 84))], {
    height: 14,
    name: '主体建筑',
    fixed: true,
    archetype: 'factory',
  });
  const annex = makeBuilding([rectRing(v2(104, 62), v2(138, 90))], {
    height: 9,
    name: '既有辅楼',
    fixed: true,
  });
  const noBuild = makeNoBuild([rectRing(v2(104, 14), v2(140, 40))], '高压走廊');
  const road = makeRoad(
    [
      v2(2, 24),
      v2(80, 24),
      v2(150, 24),
    ],
    { roadClass: 'secondary', name: '园区主路' },
  );
  const gate = makeAccess(v2(80, 24), { width: 10, dir: 0, name: '主入口' });

  for (const o of [site, parcel, main, annex, noBuild, road, gate] as SceneObject[]) {
    p = addTo(p, o);
  }
  return p;
}
