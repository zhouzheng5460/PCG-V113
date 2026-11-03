// 用地类型与层高：把 TownForge 的 9 种 LandUse 翻译成 Placemaker 的 11 种 ZoneKind，
// 并把楼高规整成「层数 × 层高」。
//
// 为什么必须翻译，而不是各留一套：
//   · TownForge 的 LandUse 是**内容规划**的粗分类（cbd / commercial / residential / …），
//     它决定"这块地摆什么形状的楼"，但不知道"楼层多高、一层几米"。
//   · Placemaker 的 ZoneKind 是**城市规则**的细分类，`KIND_SPECS` 直接给出
//     目标面宽 / 进深 / 层数区间 / 层高 —— 这正是"楼必须与宗地尺寸一致"的来源（docs/E）。
//   · 两套并存 = 面板一个说法、导出另一个说法。所以只留 Placemaker 的口径，
//     TownForge 的 use 只作为"翻译输入"。
//
// 高度为什么按层数规整：
//   原来 TownForge 的 h（单位）可以直接是 2.63 → 18.4 m 这种"半层"高度，
//   2D 看没问题，但一旦进 GLB / 进 Placemaker 的楼层体系就说不通：
//   "18.4 m 是几层？" 所以出口统一成 `层数 × 层高`（米），层高取自 KIND_SPECS。

import { KIND_SPECS } from '../../../../packages/slots/src/cut.ts';
import type { ZoneKind } from '../../../../packages/plan-ir/src/types.ts';
import type { LandUse } from '../scene/types';

export type { ZoneKind };

/** 这些用途本来就不排建筑，不需要宗地，也不进 Placemaker 的 zoning 通道。 */
const NO_BUILDING: ReadonlySet<LandUse> = new Set<LandUse>(['park', 'parking', 'green', 'reserved']);

/**
 * 用途 + 实测楼高 → ZoneKind。
 *
 * 为什么按楼高细分而不是一对一写死：TownForge 的 `commercial` 既可能是沿街 2 层板楼，
 * 也可能是 12 层商业综合体，二者在 Placemaker 里分属 `commercialLow` / `commercialHigh`
 * （层数区间 [1,2] 与 [3,8]，层高都是 4.5 m）。按实测高度分流，比按用途名硬猜准。
 *
 * 分界值取的是 KIND_SPECS 的区间边界换算成米：
 *   低层商业 2 层 × 4.5 = 9 m 以下 → commercialLow；以上 → commercialHigh
 *   住宅 ≤3 层 → 联排（residentialRow），≤6 层 → 中密度（residentialMedium），再高 → 高密度
 */
export function zoneKindOf(use: LandUse, heightM: number): ZoneKind | null {
  if (NO_BUILDING.has(use)) return null;
  switch (use) {
    case 'cbd':
      // 中央商务区：核心区高层办公，矮一点的（< 4 层）算高密度商业
      return heightM >= 28 ? 'officeHigh' : 'commercialHigh';
    case 'commercial':
      return heightM > 12 ? 'commercialHigh' : 'commercialLow';
    case 'residential':
      if (heightM <= 12) return 'residentialRow';
      if (heightM <= 21) return 'residentialMedium';
      return 'residentialHigh';
    case 'industrial':
      return 'industrial';
    case 'civic':
      // 公共设施主楼：低层办公最贴（2~4 层、层高 4 m）
      return 'officeLow';
    default:
      return null;
  }
}

/**
 * 一个用途的**规划参数**：密度上限、容积率上限、高度区间（米）。
 *
 * 这三个数不是拍出来的，来源是 TownForge 自己的 `PARCEL_STYLE`
 * （`modern.ts` 的 `hMin/hMax/cover`），高度按 7 m/单位换成米 ——
 * 于是两套内容在城市强度上是**同一口径**，对比时不会出现"一边 8 层一边 2 层"的假差异。
 *
 * `far` 由 `density × 目标层数` 反推（见下方的构造），不是独立拍一个数：
 * 容积率、密度、层数三者本来就满足 `far ≈ density × floors`，先固定两个再算第三个才自洽。
 */
export interface UsePlan {
  /** 建筑密度上限：基底面积 / 用地面积 */
  density: number;
  /** 容积率上限：总建筑面积 / 用地面积 */
  far: number;
  /** 高度区间（米） */
  heightRange: [number, number];
}

/** 有建筑的 5 类用途（公园/停车/绿地/禁建不排楼，不在这里） */
type BuiltUse = 'cbd' | 'commercial' | 'residential' | 'industrial' | 'civic';

/** 密度与高度区间：照抄 TownForge 的 `PARCEL_STYLE`（hMin/hMax 单位 × 7 = 米） */
const PLAN_SEED: Record<BuiltUse, { density: number; hRange: [number, number] }> = {
  cbd: { density: 0.42, hRange: [22.4, 49.0] },
  commercial: { density: 0.46, hRange: [11.9, 21.0] },
  residential: { density: 0.4, hRange: [14.7, 25.2] },
  industrial: { density: 0.5, hRange: [8.4, 16.8] },
  civic: { density: 0.4, hRange: [10.5, 20.3] },
};

function buildPlan(use: BuiltUse): UsePlan {
  const seed = PLAN_SEED[use];
  const mid = (seed.hRange[0] + seed.hRange[1]) / 2;
  const kind = zoneKindOf(use as LandUse, mid);
  const spec = kind ? KIND_SPECS[kind] : null;
  const fh = spec?.floorHeight ?? 3;
  // 目标层数：高度区间中点 / 层高，然后**先夹进用途允许的层数区间**，再反推容积率。
  // 顺序很要紧：先用未夹的层数去算 far，得到的是"这个用途达不到的容积率"，
  // 于是每块地的层数都会被上限夹住（实测 60% 的宗地被夹），等于这一步没生效。
  const raw = Math.max(1, Math.round(mid / fh));
  const floors = spec ? Math.max(spec.floors[0], Math.min(spec.floors[1], raw)) : raw;
  return { density: seed.density, far: Math.round(seed.density * floors * 100) / 100, heightRange: seed.hRange };
}

export const USE_PLAN: Record<BuiltUse, UsePlan> = {
  cbd: buildPlan('cbd'),
  commercial: buildPlan('commercial'),
  residential: buildPlan('residential'),
  industrial: buildPlan('industrial'),
  civic: buildPlan('civic'),
};

export function usePlanOf(use: LandUse): UsePlan | null {
  return (USE_PLAN as Record<string, UsePlan>)[use] ?? null;
}

/**
 * **按 ZoneKind 的规划参数**（11 类各自一套）。
 *
 * 为什么还要一套 kind 级的：`USE_PLAN` 是 TownForge 的 5 类（cbd/商业/住宅/工业/公共），
 * 而我们的 11 类里同一 use 下还有细分（商业低/高、住宅低/排/中/高、办公低/高）。
 * 既然用地改成按城市形态分配 11 类（`bridge/landuse.ts`），参数就必须能按 kind 取，
 * 否则"低密度住宅"和"高密度住宅"会拿到同一个容积率 —— 那又变回"用地没生效"。
 *
 * 数值来源：层数区间取自 `KIND_SPECS`（这是 Placemaker 的口径），密度按 kind 给（越高的越低）。
 * 容积率仍按 `density × 目标层数` 反推（和 `USE_PLAN` 同一套推导）。
 */
const KIND_DENSITY: Record<ZoneKind, number> = {
  commercialLow: 0.46,
  commercialHigh: 0.42,
  mixed: 0.44,
  residentialLow: 0.44,
  residentialRow: 0.46,
  residentialMedium: 0.4,
  residentialHigh: 0.34,
  lowRent: 0.44,
  officeLow: 0.4,
  officeHigh: 0.34,
  industrial: 0.5,
};

/** 高度区间（米）= 该 kind 的层数区间 × 层高，再按用途给一点上下浮动 */
function kindHeightRange(kind: ZoneKind): [number, number] {
  const spec = KIND_SPECS[kind];
  const [f0, f1] = spec.floors;
  return [f0 * spec.floorHeight, f1 * spec.floorHeight];
}

export const KIND_PLAN: Record<ZoneKind, UsePlan> = Object.fromEntries(
  (Object.keys(KIND_DENSITY) as ZoneKind[]).map((k) => {
    const density = KIND_DENSITY[k];
    const hr = kindHeightRange(k);
    const floors = Math.max(1, Math.round((hr[0] + hr[1]) / 2 / KIND_SPECS[k].floorHeight));
    return [k, { density, far: Math.round(density * floors * 100) / 100, heightRange: hr }];
  }),
) as Record<ZoneKind, UsePlan>;

export function kindPlanOf(kind: ZoneKind): UsePlan {
  return KIND_PLAN[kind] ?? USE_PLAN.civic;
}

export interface PlannedHeight {
  /** 规整后的层数 */
  floors: number;
  /** 层高（米），取自 KIND_SPECS */
  floorHeight: number;
  /** 规整后的总高（米）= floors × floorHeight */
  heightM: number;
  /** 是否被钳制（原高度落在区间外） */
  clamped: boolean;
  /** 原始高度（米），供对照与统计 */
  rawM: number;
}

/**
 * 把任意高度（米）规整成「层数 × 层高」。
 *
 * 层数一律夹进 `KIND_SPECS[kind].floors` 区间 —— 这是 Placemaker 的口径：
 * 楼高不是自由值，是"这个用途允许盖几层"。被钳制的数量会进统计（`clamped`），
 * 不静默改数：如果一大半都被钳制，说明用途映射或高度档位选错了，得回头看。
 */
export function planHeight(rawM: number, kind: ZoneKind): PlannedHeight {
  const spec = KIND_SPECS[kind];
  const [f0, f1] = spec.floors;
  const fh = spec.floorHeight;
  const want = Math.round(rawM / fh);
  const floors = Math.max(f0, Math.min(f1, Math.max(1, want)));
  return {
    floors,
    floorHeight: fh,
    heightM: floors * fh,
    clamped: want < f0 || want > f1,
    rawM,
  };
}

/**
 * 由**规划参数**算层数（不再是"拿实测楼高反推"）。
 *
 * 推导顺序（每一步都有理由）：
 *   1. **容积率的约束**：`far × 用地面积` 是这块钱允许的总建筑面积，
 *      除以基底面积就得到"平均能盖几层" —— 这是城市规则里真正的上限；
 *   2. **高度的约束**：`heightRange[1] / 层高` 是物理天花板（限高）；
 *   3. 取两者的较小值，再夹进 `KIND_SPECS[kind].floors`（用途本身允许的层数区间）。
 *
 * 一个附带的好性质：`far / density` 与地块大小无关，
 * 所以**同一用途、同一密度下，层数是稳定的**，地块大小只改变"盖几栋"，不改变"盖多高"。
 * 这正是规划里想要的行为（密度控制比容积率控制更稳）。
 */
export function planFloors(opts: {
  /** 用途（可选）：只用于报告，参数一律按 kind 取 */
  use?: LandUse;
  kind: ZoneKind;
  /** 该地块所有楼位的基底面积之和（米²） */
  footAreaM2: number;
  /**
   * **宗地面积**（米²）—— 注意不是"整个街区"。
   *
   * 我们只在沿街进深带里开发（约占街区的 30%~65%），如果把整个街区当分母去追平容积率，
   * 层数会被抬到用途区间之外（实测 50% 的宗地被夹）。所以容积率按**宗地**这个口径算：
   * 这才是"这块地被划拨出去的地"真正承担的开发量。街区级口径会小 0.6~0.7 倍。
   */
  landAreaM2: number;
}): {
  floors: number;
  floorHeight: number;
  heightM: number;
  plan: UsePlan;
  byFar: number;
  byHeight: number;
  note: string;
  /** 原始推导值是否落在用途层数区间外（被夹了） */
  clamped: boolean;
} {
  const spec = KIND_SPECS[opts.kind];
  const plan = kindPlanOf(opts.kind);
  const foot = Math.max(1, opts.footAreaM2);
  const byFar = (plan.far * opts.landAreaM2) / foot;
  const byHeight = plan.heightRange[1] / spec.floorHeight;
  const hardMax = Math.max(spec.floors[0], Math.min(spec.floors[1], Math.floor(byHeight)));
  const raw = Math.min(byFar, byHeight);
  const floors = Math.max(spec.floors[0], Math.min(hardMax, Math.round(raw)));
  return {
    floors,
    floorHeight: spec.floorHeight,
    heightM: floors * spec.floorHeight,
    plan,
    byFar,
    byHeight,
    note: byFar <= byHeight ? '容积率封顶' : '限高封顶',
    clamped: Math.round(raw) < spec.floors[0] || Math.round(raw) > spec.floors[1],
  };
}

/** 用途 → Placemaker 的中文名，面板/统计共用（避免两处各写一份）。 */
export const ZONE_CN: Record<ZoneKind, string> = {
  commercialLow: '低密度商业',
  commercialHigh: '高密度商业',
  mixed: '混合用地',
  residentialLow: '低密度住宅',
  residentialRow: '联排住宅',
  residentialMedium: '中密度住宅',
  residentialHigh: '高密度住宅',
  lowRent: '廉租住宅',
  officeLow: '低层办公',
  officeHigh: '高层办公',
  industrial: '工业制造',
};

export { KIND_SPECS };
