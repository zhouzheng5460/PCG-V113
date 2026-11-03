/**
 * PCG · 单地块生成（PRD Phase 5）
 *
 * 这一层是**纯函数**：输入地块边界 + 语义参数 + 障碍物，输出一组建筑轮廓与高度。
 * 不碰 store、不碰 three、不碰 React —— 这样才能单测、才能换种子复现。
 *
 * 生成流程（每一步都必须是几何可验证的，不能"画得像"就行）：
 *
 *   1. 退线      地块边界内缩 setback → 可建范围（buildable）
 *   2. 避让      减去禁建区 / 既有建筑 / 道路 → 净可建范围（free）
 *   3. 分割      把 free 沿长边递归对半切，直到每块接近"单栋目标面积"
 *   4. 落位      每块先内缩 GAP/2（留建筑间距），再用面积二分内缩到目标面积
 *   5. 高度      由容积率反解层数：Σ(基底×层数×层高) ≈ 地块面积×FAR，再按高度区间夹取
 *
 * 为什么用"递归对半切"而不是"拉网格"：地块边界往往不规则（示例项目里就有高压走廊、
 * 两栋既有建筑切出来的碎片），网格会把建筑甩出边界或压到既有建筑上；
 * 对半切永远发生在已有形状内部，天然不会越界（PRD 原则 4：硬约束优先）。
 */
import { intersect, offsetPath, subtract, unionAll } from './boolean';
import { LandUse, ProjectModel, ParcelObj, SceneObject } from './model';
import {
  Paths,
  RingGroup,
  boundsOfPaths,
  groupRings,
  groupToPaths,
  pathsArea,
  rectRing,
  strokeEnvelope,
} from './polygon';
import { Vec2, v2 } from './vec';
import { PlannedGround, PlannedRoad, ROAD_PLAN, planAccessRoad, planBranches, planSurfaces, roadEnvelope } from './plan';

/** 层高（m）：层数与高度换算的唯一常数，别散落在各处 */
export const FLOOR_HEIGHT = 3.6;

/** 建筑间距（m）：相邻两栋各退一半 */
const GAP = 6;
/** 单栋最小基底（㎡）：生成出 20 ㎡的"楼"没有意义 */
const MIN_FOOTPRINT = 120;
/**
 * 单栋最小面宽（m）。
 * 只卡面积不够：既有建筑外侧的条带会算出 6 m × 24 m 的"厂房"，面积达标但根本不成立。
 * 用地块外包矩形的最短边做粗暴但管用的面宽代理（L 形、凸形都不会被误杀）。
 */
const MIN_WIDTH = 8;
/** 单栋目标基底（㎡）：厂房 / 楼栋的量级取中 */
const TARGET_PER_BUILDING = 1400;
const MAX_BUILDINGS = 8;
const MAX_DEPTH = 4;

/** 退线默认值（m）：V1 按用地类型给经验值，后续应由控规参数驱动 */
export const DEFAULT_SETBACK: Record<LandUse, number> = {
  urban: 5,
  industrial: 6,
  rural: 4,
  construction: 3,
  park: 8,
  civic: 6,
};

/** 生成建筑的名字词根：让 Inspector 里一眼看出是什么 */
export const BUILDING_NOUN: Record<LandUse, string> = {
  urban: '楼栋',
  industrial: '厂房',
  rural: '农房',
  construction: '临时设施',
  park: '服务建筑',
  civic: '公共建筑',
};

/** 形态原型（交给 Phase 8 风格层） */
export const BUILDING_ARCHETYPE: Record<LandUse, string> = {
  urban: 'residential',
  industrial: 'factory',
  rural: 'farmhouse',
  construction: 'shed',
  park: 'pavilion',
  civic: 'civic',
};

export interface PcgParams {
  landUse: LandUse;
  /** 建筑密度上限 0~1（覆盖率不得超过它） */
  density: number;
  /** 容积率 */
  far: number;
  heightRange: [number, number];
  seed: number;
  /** 覆盖默认退线 */
  setback?: number;
}

export interface PcgBuilding {
  footprint: Paths;
  height: number;
  floors: number;
  archetype: string;
}

export interface PcgStats {
  setback: number;
  parcelArea: number;
  buildableArea: number;
  freeArea: number;
  footprintArea: number;
  /** 实际建筑密度 */
  coverage: number;
  /** 实际容积率 */
  far: number;
  maxHeight: number;
  count: number;
  /* ---- Phase 5 的其余四路产出（PRD §85） ---- */
  /** 全部建筑基底（既有固定 + 本次生成），做用地平衡用 */
  buildingArea: number;
  roadCount: number;
  roadArea: number;
  pavingArea: number;
  lawnArea: number;
  parkingArea: number;
  parkingSpaces: number;
  /** 禁建区等不参与地面分区的面积 */
  excludedArea: number;
}

export interface PcgResult {
  buildings: PcgBuilding[];
  /** 内部道路（入口主路 + 支路） */
  roads: PlannedRoad[];
  /** 地面分区：停车 / 铺装 / 绿地 */
  grounds: PlannedGround[];
  stats: PcgStats;
  /** 生成不出来时的原因（直接给用户看） */
  message?: string;
  /** 生成了但有取舍时的说明（例如"停车位不够，已缩小停车场"） */
  notes?: string[];
}

/** 生成可选的上下文：不传也能跑（只出建筑），传了才会规划路与地面 */
export interface PcgExtras {
  /** 既有建筑基底（不含本次要覆盖的生成物） */
  buildings?: Paths[];
  /** 禁建区 */
  excluded?: Paths[];
  /** 出入口 */
  gates?: { pos: Vec2; width: number }[];
}

/* --------------------------------- 随机 -------------------------------- */

/** mulberry32：小而稳的可复现 PRNG（同一个 seed 永远同一份结果） */
export function mulberry32(seed: number): () => number {
  let a = seed >>> 0;
  return () => {
    a = (a + 0x6d2b79f5) >>> 0;
    let t = a;
    t = Math.imul(t ^ (t >>> 15), t | 1);
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

const clamp = (v: number, lo: number, hi: number) => (v < lo ? lo : v > hi ? hi : v);

/* ------------------------------ 几何工具 ------------------------------ */

/** 外包矩形的最短边：面宽的粗略代理 */
function shortSide(paths: Paths): number {
  const b = boundsOfPaths(paths);
  return Math.min(b.maxX - b.minX, b.maxY - b.minY);
}

/**
 * 把多边形内缩到指定面积（二分找内缩距离）。
 * 用途：让每栋楼的基底落在"目标面积"附近，而不是把整块净地填满。
 */
export function insetToArea(paths: Paths, targetArea: number): Paths {
  const a0 = pathsArea(paths);
  if (a0 <= 0) return [];
  if (a0 <= targetArea) return paths;

  const b = boundsOfPaths(paths);
  let lo = 0;
  let hi = Math.min(b.maxX - b.minX, b.maxY - b.minY) / 2 - 0.4;
  if (hi <= 0) return [];
  let best: Paths = paths;

  for (let i = 0; i < 10; i++) {
    const mid = (lo + hi) / 2;
    const p = offsetPath(paths, -mid);
    const a = p.length ? pathsArea(p) : 0;
    if (a >= targetArea) {
      best = p;
      lo = mid;
    } else {
      hi = mid;
    }
  }
  return best;
}

/** 沿包围盒长边切一刀，取其中一侧（用 1 m 外扩避免退化边界） */
function sliceHalf(g: RingGroup, vertical: boolean, t: number, keepLeft: boolean): Paths {
  const b = boundsOfPaths([g.outer]);
  const pad = 1;
  const cutX = b.minX + (b.maxX - b.minX) * t;
  const cutY = b.minY + (b.maxY - b.minY) * t;
  const rect = vertical
    ? keepLeft
      ? rectRing(v2(b.minX - pad, b.minY - pad), v2(cutX, b.maxY + pad))
      : rectRing(v2(cutX, b.minY - pad), v2(b.maxX + pad, b.maxY + pad))
    : keepLeft
      ? rectRing(v2(b.minX - pad, b.minY - pad), v2(b.maxX + pad, cutY))
      : rectRing(v2(b.minX - pad, cutY), v2(b.maxX + pad, b.maxY + pad));
  return intersect(groupToPaths(g), [rect]);
}

/**
 * 按**面积**对半切（不是按包围盒）。
 * 不规则地块上按 bbox 切，很容易切出「一半是细长条」的废地；按面积二分
 * 能保证两块地面积相当，切出来的用地更接近真实用地。
 */
export function splitByArea(g: RingGroup, fraction: number): [Paths, Paths] {
  const b = boundsOfPaths([g.outer]);
  const vertical = b.maxX - b.minX >= b.maxY - b.minY;
  const lo0 = vertical ? b.minX : b.minY;
  const hi0 = vertical ? b.maxX : b.maxY;
  const whole = Math.abs(pathsArea(groupToPaths(g))) || 1;

  let lo = lo0;
  let hi = hi0;
  for (let i = 0; i < 9; i++) {
    const mid = (lo + hi) / 2;
    const t = (mid - lo0) / (hi0 - lo0 || 1);
    const a = Math.abs(pathsArea(sliceHalf(g, vertical, t, true)));
    if (a / whole < fraction) lo = mid;
    else hi = mid;
  }
  const t = ((lo + hi) / 2 - lo0) / (hi0 - lo0 || 1);
  return [sliceHalf(g, vertical, t, true), sliceHalf(g, vertical, t, false)];
}

/** 递归对半切：把大块切成若干"接近单栋目标面积"的块 */
export function subdivide(groups: RingGroup[], target: number, depth: number, rand: () => number): RingGroup[] {
  const out: RingGroup[] = [];
  for (const g of groups) {
    const a = Math.abs(pathsArea(groupToPaths(g)));
    if (a <= target * 1.55 || depth >= MAX_DEPTH) {
      out.push(g);
      continue;
    }
    const [left, right] = splitByArea(g, 0.5 + (rand() - 0.5) * 0.16);
    const kids = [...groupRings(left), ...groupRings(right)];
    if (!kids.length) {
      out.push(g);
      continue;
    }
    out.push(...subdivide(kids, target, depth + 1, rand));
  }
  return out;
}

/**
 * 带洞的块 → 若干实心块。
 * 用洞的包围盒把块十字切开（上下左右四条），洞被"挖掉"后剩下的每条都不含该洞；
 * 若还有其它洞就递归。这样得到的基底一定是简单多边形，不会出现环形楼。
 */
export function solidPieces(g: RingGroup, depth = 0): RingGroup[] {
  if (!g.holes.length) return [g];
  if (depth > 4) return [];

  const b = boundsOfPaths([g.outer]);
  const hb = boundsOfPaths([g.holes[0]]);
  const pad = 1;
  const rects: Paths = [];
  const pushRect = (x1: number, y1: number, x2: number, y2: number) => {
    if (x2 - x1 < 0.5 || y2 - y1 < 0.5) return;
    rects.push(rectRing(v2(x1, y1), v2(x2, y2)));
  };
  pushRect(b.minX - pad, b.minY - pad, b.maxX + pad, hb.minY);
  pushRect(b.minX - pad, hb.maxY, b.maxX + pad, b.maxY + pad);
  pushRect(b.minX - pad, hb.minY - pad, hb.minX, hb.maxY + pad);
  pushRect(hb.maxX, hb.minY - pad, b.maxX + pad, hb.maxY + pad);

  const out: RingGroup[] = [];
  for (const r of rects) {
    const piece = intersect(groupToPaths(g), [r]);
    for (const sub of groupRings(piece)) out.push(...solidPieces(sub, depth + 1));
  }
  return out;
}

/* ------------------------------ 障碍物收集 ------------------------------ */
/**
 * 生成时必须避让的东西：禁建区 + 既有建筑 + 道路。
 * 注意排除"本地块上一次生成的建筑" —— 它们本来就要被这次覆盖掉。
 */
export function collectObstacles(model: ProjectModel, parcelId: string): Paths[] {
  const out: Paths[] = [];
  for (const id of model.order) {
    const o: SceneObject | undefined = model.objects[id];
    if (!o || !o.visible) continue;

    if (o.kind === 'nobuild') {
      out.push(o.boundary);
    } else if (o.kind === 'building') {
      // 同一地块上一次生成的建筑会被这次覆盖，不算障碍物
      if (o.generatedFrom !== parcelId) out.push(o.footprint);
    } else if (o.kind === 'road') {
      const env = strokeEnvelope(o.centerline, o.width / 2);
      if (env.length >= 3) out.push([env]);
    }
  }
  return out;
}

/* -------------------------------- 主流程 -------------------------------- */

function emptyStats(setback: number, parcelArea: number, buildableArea: number, freeArea: number): PcgStats {
  return {
    setback,
    parcelArea,
    buildableArea,
    freeArea,
    footprintArea: 0,
    coverage: 0,
    far: 0,
    maxHeight: 0,
    count: 0,
    buildingArea: 0,
    roadCount: 0,
    roadArea: 0,
    pavingArea: 0,
    lawnArea: 0,
    parkingArea: 0,
    parkingSpaces: 0,
    excludedArea: 0,
  };
}

export function generateParcel(
  boundary: Paths,
  params: PcgParams,
  obstacles: Paths[] = [],
  extras: PcgExtras = {},
): PcgResult {
  const rand = mulberry32((params.seed ?? 1) >>> 0);
  const setback = Math.max(0, params.setback ?? DEFAULT_SETBACK[params.landUse] ?? 5);
  const parcelArea = Math.abs(pathsArea(boundary));

  if (!boundary.length || parcelArea <= 0) {
    return {
      buildings: [],
      roads: [],
      grounds: [],
      stats: emptyStats(setback, parcelArea, 0, 0),
      message: '地块没有有效轮廓',
    };
  }

  /* 1. 退线 */
  const buildable = setback > 0 ? offsetPath(boundary, -setback) : boundary;
  const buildableArea = buildable.length ? Math.abs(pathsArea(buildable)) : 0;
  if (!buildable.length || buildableArea < MIN_FOOTPRINT) {
    return {
      buildings: [],
      roads: [],
      grounds: [],
      stats: emptyStats(setback, parcelArea, buildableArea, 0),
      message: `退线 ${setback} m 后已无可建范围（地块太小）`,
    };
  }

  /* 2. 避让 */
  // 注意：障碍物是「一个 Paths = 一组环」，所以要判环里的点数而不是环的个数
  const obs = obstacles.filter((paths) => paths.some((ring) => ring.length >= 3));
  const obsUnion = obs.length ? unionAll(obs) : [];
  const freeNoRoad = obsUnion.length ? subtract(buildable, obsUnion) : buildable;
  const freeArea = freeNoRoad.length ? Math.abs(pathsArea(freeNoRoad)) : 0;
  if (freeArea < MIN_FOOTPRINT) {
    return {
      buildings: [],
      roads: [],
      grounds: [],
      stats: emptyStats(setback, parcelArea, buildableArea, freeArea),
      message: '可建范围已被占满（禁建区 / 既有建筑 / 道路）',
    };
  }

  /*
   * 2.5 先规划路，再落建筑（PRD §85：Gate + Road → Building Group）。
   *
   * 顺序不能反：先盖楼再抠路，会出现"路从两栋楼中间挤过去"或"路末端顶在墙上"。
   * 先定路，建筑自然沿路两侧排布，也才有"每栋楼都有车道"这个结果。
   * 开路时只避让建筑与禁建区，不避让已有道路——与外部道路重叠本来就是"接上"。
   *
   * 支路也在这里定，而不是等建筑落完再连：
   * 楼一旦铺下去，空隙就被填掉了，事后再拉支路只能塞进剩下的缝里——
   * 示例项目里那样只连出 2 条 6~16 m 的死胡同。先有路网，楼才会沿路排。
   * 代价是支路要拿"既有建筑 + 禁建区"当墙（生成物还不存在），这是对的：
   * 固定建筑本来就不可协商。
   */
  const blocked = [
    ...(extras.buildings ?? []),
    ...(extras.excluded ?? []),
  ].filter((p) => p.some((ring) => ring.length >= 3));
  const blockedUnion = blocked.length ? unionAll(blocked) : [];
  const access = planAccessRoad(boundary, buildable, blockedUnion, extras.gates ?? []);
  const roads = access ? [access.road] : [];
  if (access) {
    /*
     * 支路的"墙"要用 obsUnion 而不是 blockedUnion：
     * obsUnion 里还有既有道路。主路可以压着外部道路画（那叫接上），
     * 但支路不需要——支路一头扎进外部道路等于没开，只会多出一条 9 m 的死胡同。
     */
    roads.push(...planBranches(access.road.centerline, ROAD_PLAN.spurWidth, buildable, obsUnion));
  }
  const roadUnion = roads.length ? unionAll(roads.map(roadEnvelope)) : [];
  const free = roadUnion.length ? subtract(freeNoRoad, roadUnion) : freeNoRoad;
  const netArea = free.length ? Math.abs(pathsArea(free)) : 0;
  if (netArea < MIN_FOOTPRINT) {
    return {
      buildings: [],
      roads,
      grounds: [],
      stats: emptyStats(setback, parcelArea, buildableArea, netArea),
      message: '开出内部道路后已无足够净地可建',
    };
  }

  /* 3. 目标量：先由高度区间定"要盖几层"，再由 FAR 反解总基底面积，最后三重夹取 */
  const [hMin, hMax] = params.heightRange;
  const avgH = clamp((hMin + hMax) / 2, FLOOR_HEIGHT * 1.5, 300);
  const floorAreaTarget = parcelArea * Math.max(0.1, params.far);
  const fMin = Math.max(1, Math.floor(hMin / FLOOR_HEIGHT));
  const fMax = Math.max(fMin, Math.round(hMax / FLOOR_HEIGHT));
  // 计划层数：受高度区间约束 → 层数定了，需要的基底 = 总建筑面积 / 层数
  const floorsPlanned = clamp(Math.round(avgH / FLOOR_HEIGHT), fMin, fMax);
  const coverageCap = parcelArea * clamp(params.density, 0.05, 0.85);
  const totalFootprint = Math.min(floorAreaTarget / floorsPlanned, netArea * 0.92, coverageCap);
  if (totalFootprint < MIN_FOOTPRINT) {
    return {
      buildings: [],
      roads,
      grounds: [],
      stats: emptyStats(setback, parcelArea, buildableArea, netArea),
      message: '参数算出的建筑量太小（可提高容积率或密度）',
    };
  }

  const approx = clamp(Math.round(totalFootprint / TARGET_PER_BUILDING), 1, MAX_BUILDINGS);
  const per = totalFootprint / approx;

  /*
   * 4. 划地 → 拆成实心可用块 → 「越大越先盖」（排序带种子抖动）依次落位，直到预算用满。
   *
   * 只取每块地最大的实心块会严重浪费走廊状用地（本例地块就是被既有建筑切出的
   * 一圈院子），所以把每块地拆出的实心块全部纳入候选，再按面积排队。
   * 剩下的地就是院子 / 堆场 / 停车 —— 不是每寸地都要盖满。
   *
   * 一轮放不完很正常：既有建筑外侧常剩下 5~8 m 宽的条带，按 6 m 间距内缩后直接归零。
   * 所以做「补位」：把已落位的基底从净地里挖掉，用更小的目标面积再排一轮，
   * 间距也逐轮收窄。最多 3 轮 —— 再多就是在往缝里塞火柴盒了。
   */
  const picked: Paths[] = [];
  let budget = totalFootprint;
  let remaining = free;
  let targetPer = per;

  for (let pass = 0; pass < 3 && budget >= MIN_FOOTPRINT; pass++) {
    const gap = Math.max(3, GAP * Math.pow(0.62, pass));
    const minArea = pass === 0 ? MIN_FOOTPRINT : 200;
    const leaves = subdivide(groupRings(remaining), targetPer, 0, rand);

    const lots: { paths: Paths; area: number; key: number }[] = [];
    for (const leaf of leaves) {
      const solids = (leaf.holes.length ? solidPieces(leaf) : [leaf]).filter((g) => g.holes.length === 0);
      for (const s of solids) {
        const paths = groupToPaths(s);
        const area = Math.abs(pathsArea(paths));
        if (area < minArea) continue;
        // key 里掺种子：换 seed 时大块之间的先后顺序会变，布局随之变化
        lots.push({ paths, area, key: area * (0.8 + rand() * 0.4) });
      }
    }
    lots.sort((a, b) => b.key - a.key);

    let placed = 0;
    for (const lot of lots) {
      if (budget < minArea) break;
      const inner = offsetPath(lot.paths, -gap / 2);
      if (!inner.length) continue;
      const ia = Math.abs(pathsArea(inner));
      if (ia < minArea) continue;

      // 上限 1.8×目标（避免一个巨块吞掉全部预算）；下限只挡真正的碎地，
      // 不按 per 的比例挡 —— 走廊状用地本来就只能放小体量建筑
      const target = Math.min(ia, budget, Math.max(targetPer * 1.8, minArea) * (0.9 + rand() * 0.2));
      if (target < minArea) continue;

      const fp = insetToArea(inner, target);
      const fa = fp.length ? Math.abs(pathsArea(fp)) : 0;
      if (fa < minArea) continue;
      if (shortSide(fp) < MIN_WIDTH) continue; // 条带里的火柴盒：面积够也不成立
      picked.push(fp);
      budget -= fa;
      placed++;
    }

    if (!placed) break; // 这一轮一块都放不下，再收窄也只是制造碎渣
    if (budget < MIN_FOOTPRINT) break;
    // 下一轮的地 = 净地 - 已落位基底（不含 GAP，GAP 由下一轮的 offset 负责）
    remaining = subtract(free, unionAll(picked));
    if (!remaining.length || Math.abs(pathsArea(remaining)) < MIN_FOOTPRINT * 2) break;
    targetPer = Math.max(MIN_FOOTPRINT, targetPer * 0.6);
  }

  if (!picked.length) {
    return {
      buildings: [],
      roads,
      grounds: [],
      stats: emptyStats(setback, parcelArea, buildableArea, netArea),
      message: '净可建范围太碎（被道路 / 禁建区切成小块），无法落位',
    };
  }

  /*
   * 硬约束兜底：抖动与"填满剩余小块"都可能让覆盖率略超密度上限。
   * 超了就整体等比收缩 —— 密度上限是强制的（PRD 原则 4：硬约束优先）。
   */
  let footprintArea = picked.reduce((s, fp) => s + Math.abs(pathsArea(fp)), 0);
  let kept = picked;
  if (footprintArea > coverageCap * 1.005) {
    const scale = coverageCap / footprintArea;
    kept = picked
      .map((fp) => insetToArea(fp, Math.abs(pathsArea(fp)) * scale))
      .filter((fp) => fp.length && Math.abs(pathsArea(fp)) >= MIN_FOOTPRINT && shortSide(fp) >= MIN_WIDTH);
    footprintArea = kept.reduce((s, fp) => s + Math.abs(pathsArea(fp)), 0);
  }
  if (!kept.length) {
    return {
      buildings: [],
      roads,
      grounds: [],
      stats: emptyStats(setback, parcelArea, buildableArea, netArea),
      message: '压缩到密度上限后已无有效基底（可提高密度）',
    };
  }

  /* 5. 高度：Σ(基底×层数) ≈ 地块面积×FAR → 层数 = 总建筑面积 / 总基底 */
  const floorsBase = floorAreaTarget / footprintArea;

  const buildings: PcgBuilding[] = kept.map((footprint) => {
    const floors = clamp(Math.round(floorsBase * (0.88 + rand() * 0.24)), fMin, fMax);
    const height = clamp(floors * FLOOR_HEIGHT, hMin, hMax);
    return { footprint, height, floors, archetype: BUILDING_ARCHETYPE[params.landUse] };
  });

  const realFloorArea = buildings.reduce((s, b) => s + Math.abs(pathsArea(b.footprint)) * b.floors, 0);

  /*
   * 6. 地面（PRD §85：Parking / Ground）。
   * 建筑落位之后才知道铺装贴哪一圈、停车放得下多少，所以这一步必须放在最后。
   * 道路本身在 2.5 已经定好了，这里只是把路带交回给分区去扣减。
   * 既有的固定建筑也参与分区——它们脚下的地同样要铺装、要接路。
   */
  const allFootprints = [...(extras.buildings ?? []), ...buildings.map((b) => b.footprint)];
  const surfaces = planSurfaces({
    boundary,
    buildable,
    free: freeNoRoad,
    excluded: extras.excluded?.length ? unionAll(extras.excluded) : [],
    buildings: allFootprints,
    accessRoad: access ? access.road : null,
    branches: roads.slice(1),
    landUse: params.landUse,
  });

  const notes: string[] = [];
  if (!access) notes.push('没有可用出入口（Access），内部道路未生成');
  else if (surfaces.roads.length <= 1) notes.push('建筑都紧邻主路，未额外开支路');
  if (surfaces.stats.parkingSpaces === 0) notes.push('未找到合适的停车场地');

  return {
    buildings,
    roads: surfaces.roads,
    grounds: surfaces.grounds,
    stats: {
      setback,
      parcelArea,
      buildableArea,
      freeArea: netArea,
      footprintArea,
      coverage: footprintArea / parcelArea,
      far: realFloorArea / parcelArea,
      maxHeight: buildings.reduce((m, b) => Math.max(m, b.height), 0),
      count: buildings.length,
      buildingArea: surfaces.stats.buildingArea,
      roadCount: surfaces.roads.length,
      roadArea: surfaces.stats.roadArea,
      pavingArea: surfaces.stats.pavingArea,
      lawnArea: surfaces.stats.lawnArea,
      parkingArea: surfaces.stats.parkingArea,
      parkingSpaces: surfaces.stats.parkingSpaces,
      excludedArea: surfaces.stats.excludedArea,
    },
    notes: notes.length ? notes : undefined,
  };
}

/**
 * 规划输入的结构化收集（比 collectObstacles 多区分出建筑 / 禁建区 / 出入口）。
 * 之所以新开一个函数而不是改 collectObstacles：后者被测试与其他调用方按
 * 「一个 Paths = 一组环」的约定使用，语义不同不该混。
 */
export interface PcgInputs {
  /** 建筑要避让的全部区域（含道路带） */
  obstacles: Paths[];
  /** 既有建筑基底（不含本地块上一次的生成物） */
  buildings: Paths[];
  /** 禁建区 */
  excluded: Paths[];
  /** 出入口 */
  gates: { pos: Vec2; width: number }[];
}

export function collectPcgInputs(model: ProjectModel, parcelId: string): PcgInputs {
  const inputs: PcgInputs = { obstacles: [], buildings: [], excluded: [], gates: [] };
  for (const id of model.order) {
    const o: SceneObject | undefined = model.objects[id];
    if (!o || !o.visible) continue;

    if (o.kind === 'nobuild') {
      inputs.obstacles.push(o.boundary);
      inputs.excluded.push(o.boundary);
    } else if (o.kind === 'building') {
      if (o.generatedFrom === parcelId) continue; // 这次要被覆盖，不算障碍
      inputs.obstacles.push(o.footprint);
      inputs.buildings.push(o.footprint);
    } else if (o.kind === 'road') {
      // 同批次生成的内部道路也是"这次要覆盖的"，不能当成外部道路再避让一遍
      if (o.generatedFrom === parcelId) continue;
      const env = strokeEnvelope(o.centerline, o.width / 2);
      if (env.length >= 3) inputs.obstacles.push([env]);
    } else if (o.kind === 'access') {
      inputs.gates.push({ pos: o.pos, width: o.width });
    }
  }
  return inputs;
}

/** 从地块对象直接生成（store 用） */
export function generateForParcel(model: ProjectModel, parcel: ParcelObj): PcgResult {
  const inputs = collectPcgInputs(model, parcel.id);
  return generateParcel(
    parcel.boundary,
    {
      landUse: parcel.landUse,
      density: parcel.density,
      far: parcel.far,
      heightRange: parcel.heightRange,
      seed: parcel.seed,
    },
    inputs.obstacles,
    { buildings: inputs.buildings, excluded: inputs.excluded, gates: inputs.gates },
  );
}
