// 规划区（PlanningRegion）—— 用户对城市规划器施加的**空间约束**。
//
// 为什么它不是 UI District：`UIDistrict A/B/C/D` 是 k-means 派生的只读选择组，
// 没有 polygon、不能画、不能改顶点、不持久化。而用户需要画一块蓝色区域并真正编辑它。
// 那是两个东西，不能共用一个词（见 docs/G §3.18）。
//
// 这个类型也是后面 Urban Composer 的输入：
//   City Defaults → PlanningRegion → Parcel
// 后一级覆盖前一级，`resolveFor()` 负责算最终值和**来源**（面板要显示"这个值来自哪一层"）。

import { Pt } from '../core/geom';
import { Polygon } from '../core/polygon';
import type { LandUse } from '../scene/types';
import type { PatchEdit } from './patch';

export type Level3 = 'low' | 'mid' | 'high';

/**
 * 规划区的覆盖值。
 *
 * **只放模型真有的旋钮**。TF 没有"每块地的密度"参数，所以 `density` 落成 `parcelTargetA`
 * （目标宗地面积：越小越密）；高度落成 `heightK`。朝向（orientation）暂时**不放** ——
 * 模型里没有对应入口，放上去就是空控件（这一路吃过"面板撒谎"的亏，D-052）。
 */
export interface PlanningOverride {
  use?: LandUse;
  density?: Level3;
  heightBand?: Level3;
  /** 区内绿地占比（0~1）：按面积从小到大挑地块设为 park */
  greenRatio?: number;
}

export interface PlanningRegion {
  id: string;
  ring: Pt[];
  /** manual = 用户画的；generated = 由自动片区"转换为规划区"得来 */
  source: 'manual' | 'generated';
  overrides: PlanningOverride;
  /** 越大越优先；重叠时高优先级生效 */
  priority: number;
  locked: boolean;
  label: string;
}

/** 密度档 → 目标宗地面积（越小越密） */
const DENSITY_TARGET_A: Record<Level3, number> = { low: 90, mid: 60, high: 40 };
/** 高度档 → 高度倍率 */
const HEIGHT_K: Record<Level3, number> = { low: 0.55, mid: 1, high: 1.8 };

export const DENSITY_LABEL: Record<Level3, string> = { low: '低', mid: '中', high: '高' };
export const HEIGHT_LABEL: Record<Level3, string> = { low: '低层', mid: '中层', high: '高层' };

let seq = 0;
export function makeRegion(ring: Pt[], overrides: PlanningOverride = {}, source: 'manual' | 'generated' = 'manual'): PlanningRegion {
  seq += 1;
  return {
    id: `pr${seq}`,
    ring,
    source,
    overrides,
    priority: 0,
    locked: false,
    label: `规划区 ${String(seq).padStart(2, '0')}`,
  };
}

/** 点落在哪个规划区里：priority 高优先，同优先级后加的优先 */
export function regionAt(regions: readonly PlanningRegion[], p: Pt): PlanningRegion | null {
  let best: PlanningRegion | null = null;
  for (const r of regions) {
    if (r.ring.length < 3) continue;
    if (!new Polygon(r.ring).contains(p)) continue;
    if (!best || r.priority > best.priority) best = r;
  }
  return best;
}

/** 一层解析结果：值 + 来源 */
export interface ResolvedField<T> {
  value: T | undefined;
  from: 'parcel' | 'region' | 'city';
  /** 来源的显示名（"地块覆盖" / "规划区 03" / "城市默认"） */
  source: string;
}

export interface ResolvedPatch {
  use: ResolvedField<LandUse>;
  height: ResolvedField<number>;
  targetA: ResolvedField<number>;
  /** 实际要写进模型的 PatchEdit（已合并三层） */
  edit: PatchEdit;
  /** 命中他的规划区（没有则 null） */
  region: PlanningRegion | null;
}

const pick = <T>(
  parcel: T | undefined,
  region: T | undefined,
  cityDefault: T | undefined,
  regionName: string,
): ResolvedField<T> => {
  if (parcel !== undefined) return { value: parcel, from: 'parcel', source: '地块覆盖' };
  if (region !== undefined) return { value: region, from: 'region', source: regionName };
  return { value: cityDefault, from: 'city', source: '城市默认' };
};

/**
 * 算一块地的最终覆盖值。
 *
 * 顺序固定为 `parcel > region > city`，并且**每个字段独立**：
 * 地块只覆盖了用途时，高度仍然继承规划区 —— 这就是用户要的"值 + 来源"。
 *
 * @param cityTargetA 城市默认的目标宗地面积（`params.block.parcelTargetA`）
 */
export function resolveFor(
  centroid: Pt,
  regions: readonly PlanningRegion[],
  parcelEdit: PatchEdit | undefined,
  cityTargetA: number,
): ResolvedPatch {
  const region = regionAt(regions, centroid);
  const rName = region ? region.label : '城市默认';
  const r = region?.overrides ?? {};

  const regionTargetA = r.density ? DENSITY_TARGET_A[r.density] : undefined;
  const regionHeightK = r.heightBand ? HEIGHT_K[r.heightBand] : undefined;

  const use = pick<LandUse>(parcelEdit?.use, r.use, undefined, rName);
  const height = pick<number>(parcelEdit?.heightK, regionHeightK, 1, rName);
  const targetA = pick<number>(parcelEdit?.parcelTargetA, regionTargetA, cityTargetA, rName);

  // 合并成 PatchEdit：只写有意义的字段，避免把 undefined 塞进模型
  const edit: PatchEdit = {};
  if (use.value !== undefined) edit.use = use.value;
  if (height.value !== undefined && height.value !== 1) edit.heightK = height.value;
  if (targetA.value !== undefined && targetA.from !== 'city') edit.parcelTargetA = targetA.value;
  if (parcelEdit?.pattern) edit.pattern = parcelEdit.pattern;

  return { use, height, targetA, edit, region };
}

/**
 * 规划区里的绿地占比 → 把区内一批地块标成 park。
 *
 * 只在**没有更具体来源**的地块上生效（地块级 use 已指定的不动）。
 * 选择规则：按地块质心的 x+y 排序取前 N 个 —— 确定性，不引入随机数。
 */
export function applyRegionGreen(
  patches: readonly { centroid: Pt; key: string }[],
  regions: readonly PlanningRegion[],
  parcelEdits: ReadonlyMap<string, PatchEdit>,
): Map<string, LandUse> {
  const out = new Map<string, LandUse>();
  for (const r of regions) {
    const ratio = r.overrides.greenRatio;
    if (!ratio || ratio <= 0) continue;
    const inside = patches.filter((p) => r.ring.length >= 3 && new Polygon(r.ring).contains(p.centroid));
    if (!inside.length) continue;
    const n = Math.max(0, Math.round(inside.length * Math.min(1, ratio)));
    const ordered = inside.slice().sort((a, b) => a.centroid.x + a.centroid.y - (b.centroid.x + b.centroid.y));
    for (let i = 0; i < n; i++) {
      const key = ordered[i].key;
      if (parcelEdits.get(key)?.use) continue; // 地块级已指定 → 不动
      out.set(key, 'park');
    }
  }
  return out;
}
