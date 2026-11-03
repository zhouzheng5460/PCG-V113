// PCG 侧数据模型的最小投影。
//
// 只声明适配器真正要读的字段，不 import PCG 的 React/Zustand 代码——
// 否则为了读一个 road.width 会把整个编辑器拖进编译依赖。
// 字段名与 vendor/pcg-editor-src/core/model.ts 保持一致，方便对照。

import type { Vec2 } from '../../plan-ir/src/geom.ts';

export type RoadClass = 'primary' | 'secondary' | 'local' | 'service' | 'pedestrian';

/** PCG 的道路对象（RoadObj）。centerline 是折线，米制。 */
export interface PcgRoad {
  id: string;
  centerline: Vec2[];
  roadClass: RoadClass;
  width: number;
  sidewalk: boolean;
}

export type AccessType = 'main' | 'service' | 'pedestrian';

/** PCG 的出入口对象（AccessObj）。dir 是弧度，0 = +X。 */
export interface PcgAccess {
  id: string;
  pos: Vec2;
  width: number;
  dir: number;
  accessType: AccessType;
}

/** 建筑（只取轮廓，用来当避让对象）。 */
export interface PcgBuilding {
  id: string;
  ring: Vec2[];
}

export type LandUse = 'urban' | 'industrial' | 'rural' | 'construction' | 'park' | 'civic';

export interface PcgParcel {
  id: string;
  ring: Vec2[];
  landUse: LandUse;
}

/** PCG 的道路等级宽度（vendor/pcg-editor-src/core/model.ts:ROAD_WIDTH）。 */
export const ROAD_WIDTH: Record<RoadClass, number> = {
  primary: 12,
  secondary: 8,
  local: 6,
  service: 5,
  pedestrian: 3,
};

/** 车行道才满足 Placemaker 的出入口需求；步行道不算。 */
export const DRIVABLE_CLASSES: RoadClass[] = ['primary', 'secondary', 'local', 'service'];
