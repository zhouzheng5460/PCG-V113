// 楼位 → 场景体块。这是"把 Placemaker 的内容真正画出来"的那一步。
//
// 三个必须做对的地方：
//   1. **单位**：楼位是米制（`bridge/placemaker`），Scene 是世界单位 → 在这里 `u()` 一次。
//   2. **高度**：直接用 `floors × floorHeight`（米）→ `u()`，不再走"任意高度"那条路。
//      `scene/build.ts` 对 TownForge 自己的楼还会做一次规整，但楼位本来就是整层的，
//      规整不会改动它（幂等），所以两条路径出口一致。
//   3. **可分图层**：kind 用 `slot`，对应左栏「Placemaker 楼位」一行，
//      可以和 TownForge 的楼并排显示、分别开关 —— 对照就是靠这个做的。
//
// 材质按 ZoneKind 取，和 TownForge 的用途配色同源（都在 `scene/palette`）：
// 办公玻璃、商业板材、住宅混凝土、工业金属屋面。这样切换内容来源时，
// 颜色不会"整城换了一套"，能专注看肌理差异。

import { Pt } from '../core/geom';
import type { SolidFeature } from '../scene/types';
import type { MatKey } from '../scene/palette';
import { u } from '../core/units';
import type { BridgeReport, SlotRecord } from './placemaker';
import type { ZoneKind } from './zone';

// 楼位 → 体块的实现搬到了 `bridge/assets.ts`（资产槽位 + 高楼拆裙房/塔楼）。
// 这里只保留"给 UI 看的摘要"，避免两处各写一份体块逻辑。
export { slotsToSolids, assetKey, summarizeAssets, modularFit, isTier, TIERS, SPLIT_MIN_FLOORS } from './assets';
export type { AssetSummary, SlotSolidsResult } from './assets';

/** 供 UI 显示：一次桥的总账（楼位数 / 宗地数 / 拟合 / 进深）。 */
export interface PlacemakerSummary {
  zonings: number;
  slots: number;
  fitMed: number;
  depthAvg: number;
  heightMedM: number;
  skippedByShape: number;
  /** 选中地块时：这块地的楼位数、TownForge 的栋数、以及按参数算出的层数与容积率 */
  patch?: {
    slots: number;
    tfBuildings: number;
    fit: number;
    kind: ZoneKind;
    use: string;
    floors: number;
    floorHeight: number;
    heightM: number;
    density: number;
    far: number;
    farActual: number;
    note: string;
  };
}

export function summarizeBridge(r: BridgeReport, patchIndex?: number | null): PlacemakerSummary {
  const hs = r.slots.map((s) => s.heightM).sort((a, b) => a - b);
  const out: PlacemakerSummary = {
    zonings: r.stats.zonings,
    slots: r.stats.slots,
    fitMed: r.stats.fitMed,
    depthAvg: r.stats.depthAvg,
    heightMedM: hs.length ? hs[Math.floor(hs.length / 2)] : 0,
    skippedByShape: r.stats.skippedByShape,
  };
  if (patchIndex != null) {
    const z = r.zonings.find((x) => x.patchIndex === patchIndex);
    if (z) {
      out.patch = {
        slots: r.slots.filter((s) => s.patchIndex === patchIndex).length,
        tfBuildings: z.townforge.buildings,
        fit: z.fitRatio,
        kind: z.kind,
        use: z.use,
        floors: z.plan.floors,
        floorHeight: z.plan.floorHeight,
        heightM: z.plan.heightM,
        density: z.plan.density,
        far: z.plan.far,
        farActual: z.plan.farActual,
        note: z.plan.note,
      };
    }
  }
  return out;
}
