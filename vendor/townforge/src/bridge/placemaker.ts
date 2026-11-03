// 桥：TownForge 的街区 → Placemaker 的宗地（**全用米**）→ 我们的楼位切分。
//
// 这一层的唯一职责是"换口径"，不做设计决策：
//   1. 几何：单位 → 米（`core/units` 是唯一换算点）；
//   2. 形状：TownForge 的街区是多边形（张量场切出来的，边数不定），
//      Placemaker 的 `PlanZoning.quad` 是四边形 → 取**最小面积外接矩形**；
//   3. 用途：LandUse → ZoneKind（`bridge/zone`）；
//   4. 楼位：交给 `packages/slots` 的 `cutZoning`（和 C# 烘焙结果同一套规则）。
//
// 为什么用"最小面积外接矩形"而不是直接拿多边形：
//   `PlanZoning` 契约就是 quad（`readonly [Vec2, Vec2, Vec2, Vec2]`），
//   C# 的 ZoneGrid 也是按格切出的四边形。拿多边形硬塞会逼着下游改契约。
//   代价是"凹街区会被外接矩形撑大"，所以每个宗地都回报 `fitRatio = 四边形面积 / 街区面积`，
//   比值低于 0.85 的会在统计里标出来 —— 不静默失真。
//
// 这条桥是**只读**的：它不改 TownForge 的几何，只读 model 产出 Placemaker 口径的数据。
// 真正的替换（用我们的楼位替换 modern.ts 的地块内容）是下一步，见 docs/G §3.1。

import { KIND_SPECS, cutZoning } from '../../../../packages/slots/src/cut.ts';
import type { ZoneKind } from '../../../../packages/plan-ir/src/types.ts';
import { ZONE_PREFAB } from '../../../../packages/plan-ir/src/types.ts';
import { UNIT_M, sqMeters, u } from '../core/units';
import { buildRoadNet, type RoadClass } from '../road/system';
import { ROAD_SPECS } from '../road/spec';
import { NET_WIDTH } from '../../../../packages/scene-ir/src/palette.ts';
import { Pt } from '../core/geom';
import { area as ptArea, intersect as ptIntersect, pointInPolygon } from '../town/zones';
import {
  CELL_M,
  blocksOfRoad,
  cellCentre,
  filterRects,
  gridDepth,
  mergeRects,
  reduceDepths,
  zoningRects,
  type CellGrid,
} from './zonegrid';
import { buildAccess, type AccessReport } from './access';
import { buildParking, type ParkingReport } from './parking';
import { buildPark, type ParkReport } from './park';
import { planLandUse, type LandUseReport } from './landuse';
import type { Model } from '../town/model';
import type { LandUse } from '../scene/types';
import { kindPlanOf, planFloors, zoneKindOf } from './zone';

/**
 * 四边形拟合比的下限。低于它就**不产宗地**，只记账。
 *
 * 为什么是跳过而不是硬塞：`PlanZoning.quad` 是契约的四边形，而张量场切出来的街区
 * 有三角形和楔形。把三角形套上外接矩形，被撑出来的那块会把楼排到隔壁街区的土地上；
 * 而 Placemaker 那边没有任何机制能发现"这个宗地是假的"。宁可少一块地，不可静默错位。
 *
 * 0.6 是实测取的：中位 0.88，楔形街区落在 0.41~0.50。取 0.6 能挡住楔形，又不误杀五边形。
 */
export const MIN_FIT_RATIO = 0.6;

type XY = { x: number; y: number };
/** Placemaker 的平面坐标是 [x, z]，单位米。这里 y 就是 z（TownForge 的 2D 平面 = 地面）。 */
type V2 = [number, number];

export interface ZoningRecord {
  /** 街区在 model.patches 里的下标，用来和 2D 画面对上 */
  patchIndex: number;
  use: LandUse;
  kind: ZoneKind;
  zoneLabel: string;
  /** 主条带（米，CCW）。完整宗地见 `strips` —— 一个街区通常切出 2~4 条沿街进深带 */
  quad: [V2, V2, V2, V2];
  /** 该街区的全部宗地条带（米，CCW） */
  strips: [V2, V2, V2, V2][];
  /** 街道方向（米制单位向量），决定哪条边是临街面 */
  streetDir: V2;
  /** 四边形面积（m²） */
  quadAreaM2: number;
  /** 街区自身面积（m²） */
  patchAreaM2: number;
  /** 四边形 / 街区 —— 凹街区会被外接矩形撑大，这个比值就是失真量 */
  fitRatio: number;
  /** TownForge 自己在这块地上盖了几栋、基底多少 m²、最高多少 m */
  townforge: { buildings: number; solids: number; footM2: number; maxHM: number };
  /** Placemaker 口径的规划参数与最终层数（由参数算出，不是实测反推） */
  plan: {
    density: number;
    far: number;
    /** 实际落地的容积率 = 基底面积 × 层数 / 用地面积 */
    farActual: number;
    heightRange: [number, number];
    floors: number;
    floorHeight: number;
    heightM: number;
    /** 是"容积率封顶"还是"限高封顶" */
    note: string;
  };
}

export interface SlotRecord {
  patchIndex: number;
  slotId: string;
  kind: ZoneKind;
  /** 楼位的**地的**轮廓（米，CCW）—— 铺装/退线的范围 */
  lotQuad: [V2, V2, V2, V2];
  /** 楼的 footprint（米，CCW）= 楼位内收后再**裁到街区里**的结果（可能不是四边形） */
  quad: [V2, V2, V2, V2];
  /** 实际画的楼轮廓（米）。被街区边界裁过的会多于 4 个点 —— 这正是"不压路"的实现 */
  foot: V2[];
  /** 面宽 × 进深（米） */
  width: number;
  depth: number;
  /** 是否落在 8 m 模数上 */
  moduleWidth: boolean;
  moduleDepth: boolean;
  /** 该楼位的层数与总高（米），已按 KIND_SPECS 规整 */
  floors: number;
  floorHeight: number;
  heightM: number;
}

/**
 * 一条街道在 Placemaker 口径下的样子。
 *
 * 两个口径不一样，必须都说出来：
 *   · TownForge 按 `road/spec.ts` 的规范表给红线宽（主干 28 / 次干 20 / 支路 10.5 m）；
 *   · Placemaker 按 CS2 的路型给名义宽度（avenue 17 / street 12 / alley 4 m）。
 * 差 8~11 m，直接换算会把路画窄，所以**保留 TF 的实际宽度**，
 * role 只作为等级标签（`ready` 的 `EntranceSuggest` 用的是实际 w，见 docs/A §0）。
 */
export interface NetRecord {
  cls: RoadClass;
  /** Placemaker 侧的路型（PlanNet.role 的取值） */
  role: 'avenue' | 'street';
  /** TF 的实际红线宽（米） */
  widthM: number;
  /** Placemaker 同名路型的名义宽度（米）—— 差额就是两套规范的口径差 */
  nominalM: number;
  lengthM: number;
}

export interface BridgeReport {
  zonings: ZoningRecord[];
  slots: SlotRecord[];
  /** 被跳过的街区（公园 / 停车场 / 绿地 / 禁建区：它们本来就不排建筑） */
  skipped: { patchIndex: number; use: LandUse; reason: string }[];
  /** 街道（米制，带 Placemaker 路型标签） */
  nets: NetRecord[];
  /** 出入口：`EntranceSuggest` 的复现结果 + 停车场地块的 ready */
  access: AccessReport;
  /** 停车场：`ParkingLayer` 的车位阵列（真的排出车位，不只是判 ready） */
  parking: ParkingReport;
  /** 公园 / 绿地：`ParkLayer` 的园路与树（分层接管：地表仍用 TownForge 的，见 Q-008） */
  park: ParkReport;
  /** 用地：按城市形态分配的 11 类（含保底覆盖） */
  landUseReport: LandUseReport;
  stats: {
    patches: number;
    zonings: number;
    slots: number;
    /** 四边形拟合比的最低值与中位数 */
    fitMin: number;
    fitMed: number;
    /** 进深落在 8 m 模数上的楼位占比 */
    depthOnModule: number;
    /** 平均进深（米） */
    depthAvg: number;
    /** TownForge 楼栋数 vs 我们的楼位数 */
    tfBuildings: number;
    ourSlots: number;
    /** 被钳制层数的楼位占比（用途/高度档位选错的信号） */
    clampedShare: number;
    /** 因为"太不方"而没产宗地的街区数 */
    skippedByShape: number;
    /** 进深与最近 8 m 格的偏差 ≤ 4 m（半格）的楼位占比 */
    depthNearModule: number;
    /** 楼位基底面积之和 / 街区面积之和（真实城市大约 30%~55%） */
    coverShare: number;
    /** 因为裁完太小/大半在街区外被丢掉的楼位（"不压路"的代价，越小越好） */
    droppedOutside: number;
    /** 被街区边界裁过（而不是整块保留）的楼数 */
    clippedFoot: number;
    /** 停车场：车位总数 */
    parkingStalls: number;
    /** 公园 / 绿地：树与园路 */
    parkTrees: number;
    parkPaths: number;
    /** 出入口：总数 / 接街占比 / 停车场 ready 数 */
    accessCount: number;
    attachedShare: number;
    parkingTotal: number;
    parkingReady: number;
    /** 街道条数与总里程（米） */
    netCount: number;
    netLengthM: number;
    /** 街道宽度中位（米） */
    netWidthMedM: number;
    /** 宗地面积中位 / 最大（m²）—— "地块够不够大"就靠这两个数 */
    parcelAreaMedM2: number;
    parcelAreaMaxM2: number;
    /** 宗地面积标准差 / 均值（0 = 全是同一尺寸，那正是上一轮"复印机城市"的样子） */
    parcelAreaCv: number;
    /** 宗地三段计数：原始提取 / 合并后 / 过最小面积后 */
    rectsRaw: number;
    rectsMerged: number;
    rectsFinal: number;
    /** 由 ≥2 次提取拼成的地块占比（"地块不是一次分割产生"的直接证据） */
    composedShare: number;
    /** 宗地覆盖街区面积的比例（地块占地的用地率） */
    parcelCoverShare: number;
  };
}

// ─────────────────────────────────────────── 几何：多边形 → 最小面积外接矩形

/** 多边形面积（单位²，带符号）。 */
function areaOf(ring: XY[]): number {
  let a = 0;
  for (let i = 0; i < ring.length; i++) {
    const p = ring[i];
    const q = ring[(i + 1) % ring.length];
    a += p.x * q.y - q.x * p.y;
  }
  return a / 2;
}

/**
 * 最小面积外接矩形（旋转卡壳的朴素版：把每条边当一次轴）。
 *
 * 为什么不用凸包 + 卡壳：街区顶点中位数 4~6 个（张量场切出来多是四边形/五边形），
 * O(n²) 和 O(n log n) 在这一层没有区别，而朴素版容易读、不容易错。
 */
function minAreaRect(ring: XY[]): XY[] {
  let best: { area: number; quad: XY[] } | null = null;
  for (let i = 0; i < ring.length; i++) {
    const a = ring[i];
    const b = ring[(i + 1) % ring.length];
    const dx = b.x - a.x;
    const dy = b.y - a.y;
    const len = Math.hypot(dx, dy);
    if (len < 1e-9) continue;
    const ux = dx / len;
    const uy = dy / len;
    // (ux,uy) 为一个轴，( -uy,ux ) 为另一个轴
    let minU = Infinity;
    let maxU = -Infinity;
    let minV = Infinity;
    let maxV = -Infinity;
    for (const p of ring) {
      const pu = p.x * ux + p.y * uy;
      const pv = -p.x * uy + p.y * ux;
      if (pu < minU) minU = pu;
      if (pu > maxU) maxU = pu;
      if (pv < minV) minV = pv;
      if (pv > maxV) maxV = pv;
    }
    const area = (maxU - minU) * (maxV - minV);
    if (!best || area < best.area) {
      const corner = (cu: number, cv: number): XY => ({ x: cu * ux - cv * uy, y: cu * uy + cv * ux });
      best = {
        area,
        quad: [corner(minU, minV), corner(maxU, minV), corner(maxU, maxV), corner(minU, maxV)],
      };
    }
  }
  return best ? best.quad : ring.slice(0, 4);
}

/**
 * 楼占楼位的比例（按用途）。
 *
 * **这是"宗地 ≠ 楼"的那一步。** `cutZoning` 给出的是**楼位**（可以理解成划拨出去的地块），
 * 它会把宗地铺满；如果直接把楼位当楼的轮廓，整座城就变成一张灰色地毯
 * （第一版就是这样：覆盖 85~91%，比 TownForge 自排密一倍）。
 * 真实开发里楼只占楼位的一部分，剩下的退线、院场、车位、绿化。
 *
 * 数值参考 TownForge 自己的 `cover`（cbd 0.42 / 商业 0.46 / 住宅 0.4 / 工业 0.5），
 * 这样两套内容放在一起时"密度感"是同一个量级，对照才有意义。
 */
const COVER: Record<ZoneKind, number> = {
  // 这些数必须和 `zone.ts` 的 `USE_PLAN.density` **对齐**：层数是
  // `far × 宗地面积 ÷ 基底面积` 算出来的，基底又 = 宗地 × COVER，
  // 于是 `层数 = far / COVER`。两者不一致时层数会偏（实测 CBD 从 10 层变成 12 层）。
  commercialLow: 0.46,
  commercialHigh: 0.46,
  mixed: 0.44,
  residentialLow: 0.4,
  residentialRow: 0.4,
  residentialMedium: 0.4,
  residentialHigh: 0.4,
  lowRent: 0.4,
  officeLow: 0.4,
  officeHigh: 0.42,
  industrial: 0.5,
};

/** 把四边形按面积比例 k 向中心收（k=1 不动）。用于"楼位 → 楼的 footprint"。 */
function shrinkQuad(q: [V2, V2, V2, V2], k: number): [V2, V2, V2, V2] {
  const cx = (q[0][0] + q[1][0] + q[2][0] + q[3][0]) / 4;
  const cz = (q[0][1] + q[1][1] + q[2][1] + q[3][1]) / 4;
  return q.map(([x, z]) => [cx + (x - cx) * k, cz + (z - cz) * k] as V2) as [V2, V2, V2, V2];
}

const EMPTY_ACCESS: AccessReport = {
  accesses: [],
  perPatch: new Map(),
  parkingReady: [],
  stats: {
    accessCount: 0,
    attachedShare: 0,
    patchesWithAccess: 0,
    patches: 0,
    parkingTotal: 0,
    parkingReady: 0,
    gapMedM: 0,
  },
};

const toM = (p: XY): V2 => [p.x * UNIT_M, p.y * UNIT_M];
const toMXY = (p: XY): XY => ({ x: p.x * UNIT_M, y: p.y * UNIT_M });

function norm(v: V2): V2 {
  const l = Math.hypot(v[0], v[1]) || 1;
  return [v[0] / l, v[1] / l];
}

const quadToPts = (q: readonly V2[]): Pt[] => q.map(([x, z]) => new Pt(x, z));

/**
 * 一个楼位/楼落在街区里的比例（0~1）。
 *
 * **这是"建筑不许压路"的最后一道闸。** 楼位是从街区边推出来的，理论上不会越界；
 * 但外接矩形、斜边、凹角都会让个别楼位伸出街区半步，到图上就是楼压在沥青上
 * （用户核对时一眼看见的就是这个）。
 * 所以每个楼位切出来之后都做一次布尔求交，落在街区内的面积不足 90% 的直接丢掉，
 * 并记进 `droppedOutside` —— 少一栋，胜过一栋盖在马路中间。
 */
function insideRatio(q: readonly V2[], blockM: Pt[]): number {
  const quad = quadToPts(q);
  const a = Math.abs(ptArea(quad));
  if (a < 1e-6) return 0;
  const rings = ptIntersect(quad, blockM, 1e-4);
  if (!rings.length) return 0;
  let inside = 0;
  for (const r of rings) inside += Math.abs(ptArea(r));
  return Math.min(1, inside / a);
}

// ─────────────────────────────────────────── 主入口

const CELL_M_HALF = CELL_M / 2;

/** 环的包围盒（米），用于"格心是否落在街区"的第一步快速排除 */
function ringBox(ring: Pt[]): { minX: number; minY: number; maxX: number; maxY: number } {
  let minX = Infinity;
  let minY = Infinity;
  let maxX = -Infinity;
  let maxY = -Infinity;
  for (const p of ring) {
    if (p.x < minX) minX = p.x;
    if (p.y < minY) minY = p.y;
    if (p.x > maxX) maxX = p.x;
    if (p.y > maxY) maxY = p.y;
  }
  return { minX, minY, maxX, maxY };
}

/**
 * 格子归属的排序策略 —— **这是一个会显著改变城市密度的参数，不是随手写的细节**。
 *
 * 一块街区会被 9~20 个 grid 同时覆盖（每个都来自某条路的一侧）。8 m 方格只能归一家，
 * 谁先到谁拿，于是"排序"直接决定了街区被切成什么形状的地块。
 *
 * 六种都实测过（n=15，5 个种子取均值；条带 = 宗地数）：
 *
 * | 策略 | 条带 | p90 | max | 面积 CV | 楼位 | 楼栋占地 |
 * | --- | --- | --- | --- | --- | --- | --- |
 * | `order`       | 353 | 1152 | 3215 | 0.33 | 1315 | 28% |
 * | `y`           | 412 |  845 | 1998 | 0.43 |  950 | 19% |
 * | `y-long`      | 343 | 1459 | 4843 | 0.80 | 1106 | 24% |
 * | **`long`**    | 317 | 1536 | 4843 | 0.62 | 1242 | 28% |
 * | `atomic`      | 223 | 1613 | 4685 | 0.43 | 1014 | 23% |
 * | `atomic-fill` | 318 | 1536 | 4621 | 0.58 | 1196 | 27% |
 *
 * `long`（沿路格数多的 grid 优先）：地块最大、离散度最高、覆盖率不掉，所以选它。
 * `y`（"离自己那条路最近"）反而**最差** —— 短 grid 先抢门口，把长边整条带咬碎，
 * 这正好说明 D-049 当时"按 y 优先更好"的直觉是错的。
 *
 * 保留全部六种是为了让测试能把每种都跑一遍、把数字打出来比 —— 上一轮我凭感觉改了这个顺序，
 * 楼位从 1308 跳到 1824 却没发现（D-049）。
 */
export type ClaimPolicy = 'order' | 'y' | 'y-long' | 'long' | 'long-y' | 'atomic' | 'atomic-fill';

export interface BridgeOptions {
  /** 格子归属排序，默认 `long`（实测最优，见 `ClaimPolicy`） */
  claimPolicy?: ClaimPolicy;
}

export function buildBridge(model: Model, opts: BridgeOptions = {}): BridgeReport {
  const claimPolicy = opts.claimPolicy ?? 'long';
  const zonings: ZoningRecord[] = [];
  const nets: NetRecord[] = [];

  // ── 道路网提前建：zone 格网、nets、access 三处都要它
  let roadNet: ReturnType<typeof buildRoadNet> | null = null;
  try {
    roadNet = buildRoadNet(model);
  } catch {
    roadNet = null;
  }

  // ── C# 的 zone 格网：每条路两侧各推一组 8 m 格子（`ZoneBlockPredictor`）
  const grids: CellGrid[] = [];
  if (roadNet) {
    for (const e of roadNet.edges) {
      grids.push(
        ...blocksOfRoad(
          [e.a.x * UNIT_M, e.a.y * UNIT_M],
          [e.b.x * UNIT_M, e.b.y * UNIT_M],
          e.carriage * UNIT_M,
        ),
      );
    }
  }

  /**
   * 格子 → 街区：**格心**落在哪个街区里就归哪个。
   *
   * 为什么要先全局去重：弯曲路段两侧的 block 会重叠（C# 用 `CellVisible` 把输的那格藏掉）。
   * 我们不去重的话，同一格会被两块街区各算一次，楼位就会叠。
   */
  const cellsByPatch = new Map<number, { gridIndex: number; x: number; y: number }[]>();
  if (grids.length) {
    const ringsM = model.patches.map((p) => (p.withinCity ? p.shape.map((q) => new Pt(q.x * UNIT_M, q.y * UNIT_M)) : null));
    const boxes = ringsM.map((r) => (r ? ringBox(r) : null));

    /**
     * 已被占用的格子（8 m 方块）。
     *
     * **归属规则：离自己那条路最近的一侧赢**（C# 的 `CellVisible` 就是这个语义）。
     * 之前是"先遍历到谁就归谁"，于是四个方向的格网互相切，把街区切成 2~3 格宽的碎片 ——
     * `cutZoning` 只能按"窄宗地整块补位"，结果每栋楼都只有 19×20 m，栋数却是 TF 的 3.3 倍。
     * 按 y（排号，0 = 贴着自己那条路）从小到大占位，就等于"每条路先拿自己门口的那几排"，
     * 街区的中间归离它最近的那条路，带状宗地就干净了。
     */
    const taken = new Map<string, { cx: number; cy: number; d: V2; h: number }[]>();
    const bucket = (x: number, y: number): string => `${Math.floor(x / 8)},${Math.floor(y / 8)}`;
    const overlaps = (cx: number, cy: number): boolean => {
      const bx = Math.floor(cx / 8);
      const by = Math.floor(cy / 8);
      for (let i = -1; i <= 1; i++) {
        for (let j = -1; j <= 1; j++) {
          const list = taken.get(`${bx + i},${by + j}`);
          if (!list) continue;
          for (const c of list) {
            const r: V2 = [c.d[1], -c.d[0]];
            const dx = cx - c.cx;
            const dy = cy - c.cy;
            if (Math.abs(dx * c.d[0] + dy * c.d[1]) <= c.h && Math.abs(dx * r[0] + dy * r[1]) <= c.h) return true;
          }
        }
      }
      return false;
    };

    // 1) 收集全部格子，并按 `claimPolicy` 排序（见 `ClaimPolicy` 的注释：这个顺序是密度参数）
    const all: { gi: number; x: number; y: number; c: V2; d: V2 }[] = [];
    for (let gi = 0; gi < grids.length; gi++) {
      const g = grids[gi];
      const l = Math.hypot(g.dir[0], g.dir[1]) || 1;
      const dn: V2 = [g.dir[0] / l, g.dir[1] / l];
      for (let x = 0; x < g.sx; x++) {
        for (let y = 0; y < g.sy; y++) {
          all.push({ gi, x, y, c: cellCentre(g, x, y), d: dn });
        }
      }
    }
    // 稳定排序（`Array.prototype.sort` 在 Node 下已稳定）：同分时保持生成顺序
    if (claimPolicy === 'y') all.sort((a, b) => a.y - b.y);
    else if (claimPolicy === 'long') all.sort((a, b) => grids[b.gi].sx - grids[a.gi].sx);
    else if (claimPolicy === 'long-y')
      all.sort((a, b) => grids[b.gi].sx - grids[a.gi].sx || a.y - b.y);
    else if (claimPolicy === 'y-long')
      all.sort((a, b) => a.y - b.y || grids[b.gi].sx - grids[a.gi].sx);

    /**
     * `atomic`：**整块 grid 一次占位，占不到就整块放弃**。
     *
     * 动机：逐格抢占会"啃"——一个长 grid 的门口几排被短 grid 咬掉几格，
     * 剩下的就是一个豁口，`peelRects` 只能绕开它，于是又切出一堆小地块。
     * 整块占位下，一个 grid 要么原样拿到一整条 6 排的方整带，要么干脆没有。
     * 代价是空的边角变多（覆盖率会掉），所以必须用数字说话，不能凭感觉。
     */
    if (claimPolicy === 'atomic' || claimPolicy === 'atomic-fill') {
      const order = grids.map((_, i) => i).sort((a, b) => grids[b].sx - grids[a].sx);
      for (const gi of order) {
        const g = grids[gi];
        const cs: { x: number; y: number; c: V2 }[] = [];
        let ok = true;
        for (let x = 0; x < g.sx && ok; x++) {
          for (let y = 0; y < g.sy && ok; y++) {
            const c = cellCentre(g, x, y);
            if (overlaps(c[0], c[1])) ok = false;
            cs.push({ x, y, c });
          }
        }
        if (!ok) continue;
        // 整块的格心必须全在同一个街区里（跨街区的带不认，那是格网越界的信号）
        let pi = -1;
        for (const it of cs) {
          let hit = -1;
          for (let k = 0; k < ringsM.length; k++) {
            const bx = boxes[k];
            if (!bx) continue;
            if (it.c[0] < bx.minX || it.c[0] > bx.maxX || it.c[1] < bx.minY || it.c[1] > bx.maxY) continue;
            if (pointInPolygon(new Pt(it.c[0], it.c[1]), ringsM[k]!)) {
              hit = k;
              break;
            }
          }
          if (hit < 0 || (pi >= 0 && hit !== pi)) {
            ok = false;
            break;
          }
          pi = hit;
        }
        if (!ok || pi < 0) continue;
        for (const it of cs) {
          const key = bucket(it.c[0], it.c[1]);
          const rec = { cx: it.c[0], cy: it.c[1], d: norm(g.dir), h: CELL_M_HALF };
          const arr0 = taken.get(key);
          if (arr0) arr0.push(rec);
          else taken.set(key, [rec]);
          const arr = cellsByPatch.get(pi);
          const cell = { gridIndex: gi, x: it.x, y: it.y };
          if (arr) arr.push(cell);
          else cellsByPatch.set(pi, [cell]);
        }
      }
    }

    // 2) 按顺序占位：已经有一格压住这里就跳过（说明更靠路的格子已经占了）
    //
    // `atomic-fill`：原子轮已经铺好了方整的带，这一轮**只补空**（`overlaps` 会挡住已占的格）。
    // 于是既有"整条带一次成型"的大地块，又不会像纯 `atomic` 那样把边角全扔掉。
    const fill = claimPolicy === 'atomic-fill' ? all.slice().sort((a, b) => a.y - b.y || grids[b.gi].sx - grids[a.gi].sx) : claimPolicy === 'atomic' ? [] : all;
    for (const it of fill) {
      if (overlaps(it.c[0], it.c[1])) continue;
      let hit = -1;
      for (let pi = 0; pi < ringsM.length; pi++) {
        const bx = boxes[pi];
        if (!bx) continue;
        if (it.c[0] < bx.minX || it.c[0] > bx.maxX || it.c[1] < bx.minY || it.c[1] > bx.maxY) continue;
        if (pointInPolygon(new Pt(it.c[0], it.c[1]), ringsM[pi]!)) {
          hit = pi;
          break;
        }
      }
      if (hit < 0) continue;
      const key = bucket(it.c[0], it.c[1]);
      const arr0 = taken.get(key);
      const rec = { cx: it.c[0], cy: it.c[1], d: it.d, h: CELL_M_HALF };
      if (arr0) arr0.push(rec);
      else taken.set(key, [rec]);

      const arr = cellsByPatch.get(hit);
      if (arr) arr.push({ gridIndex: it.gi, x: it.x, y: it.y });
      else cellsByPatch.set(hit, [{ gridIndex: it.gi, x: it.x, y: it.y }]);
    }
  }

  // ── 深度规则：C# `ReduceDepths` 要在**全部** grid 上跑一次（它会跨块读邻居的排号），
  //    所以这一步必须放在"格 → 街区"之后、宗地提取之前。
  const reduced: number[][] = grids.map(() => []);
  if (grids.length) {
    const occGlobal: Set<string>[] = grids.map(() => new Set<string>());
    for (const list of cellsByPatch.values()) {
      for (const c of list) occGlobal[c.gridIndex]?.add(`${c.x},${c.y}`);
    }
    for (let gi = 0; gi < grids.length; gi++) reduced[gi] = gridDepth(occGlobal[gi]!, grids[gi]);
    reduceDepths(grids, reduced);
  }

  // ── 用地：按城市形态给 11 类（不再拿 TownForge 的 5 类反推，见 bridge/landuse.ts）
  const isZonable = (i: number): boolean => {
    const p = model.patches[i];
    if (!p || !p.withinCity) return false;
    const u = (p.ward as unknown as { lot?: { use: string } } | null)?.lot?.use;
    return u === 'cbd' || u === 'commercial' || u === 'residential' || u === 'industrial' || u === 'civic';
  };
  const landUse = planLandUse(model, roadNet, isZonable);
  const kindOfPatch = new Map<number, ZoneKind>();
  for (const k of landUse.kinds) kindOfPatch.set(k.patchIndex, k.kind);

  const slots: SlotRecord[] = [];
  let fitMin = Infinity;
  let fitSum = 0;
  let clamped = 0;
  let tfBuildings = 0;
  let depthSum = 0;
  let depthOnModule = 0;
  let nearModule = 0;
  let droppedOutside = 0;
  let clippedFoot = 0;
  let slotAreaM2 = 0;
  let patchAreaSumM2 = 0;
  let skippedByShape = 0;
  /** 宗地三段计数：原始提取 → 合并后 → 过最小面积后（三个数一起看才知道"并了多少、丢了几个"） */
  let rectsRaw = 0;
  let rectsMerged = 0;
  let rectsFinal = 0;
  /** 由 ≥2 次提取拼成的地块数（"不是一次分割就成"的直接证据） */
  let rectsComposed = 0;
  const parcelAreasM2: number[] = [];
  const skipped: { patchIndex: number; use: LandUse; reason: string }[] = [];

  model.patches.forEach((patch, patchIndex) => {
    const ward = patch.ward as unknown as { lot?: { use: LandUse; solids: { h: number; ring?: XY[] }[]; buildings: number } } | null;
    const lot = ward?.lot;
    if (!patch.withinCity) return;
    if (!lot) {
      skipped.push({ patchIndex, use: 'reserved', reason: '没有地块内容（城内但未规划）' });
      return;
    }

    const solids = lot.solids ?? [];
    const heights = solids.map((s) => s.h * UNIT_M);
    const maxHM = heights.length ? Math.max(...heights) : 0;
    const medHM = median(heights);
    // 用途 → ZoneKind：用**规划参数**的高度区间中点判档，不再拿实测楼高反推。
    // 拿实测楼高是循环论证（楼高本来就是我们自己排出来的），换成参数才是规划口径。
    // 用途：**按城市形态规划出来的 11 类**（bridge/landuse.ts）。
    // 拿不到（比如这块地不在规划范围内）才退回"按 use + 楼高反推"的老办法。
    const kind = kindOfPatch.get(patchIndex) ?? zoneKindOf(lot.use, medHM);
    if (!kind) {
      skipped.push({ patchIndex, use: lot.use, reason: `${lot.use} 不排建筑，不进 zoning 通道` });
      return;
    }

    const ring: XY[] = patch.shape.map((p) => ({ x: p.x, y: p.y }));
    /** 街区环（世界单位）：推带与裁剪用 */
    const blockRing: Pt[] = patch.shape.map((p) => new Pt(p.x, p.y));
    /** 同一个环，换成米：楼位是米制的，越界检查必须在米下做 */
    const blockRingM: Pt[] = patch.shape.map((p) => new Pt(p.x * UNIT_M, p.y * UNIT_M));
    const patchAreaM2 = Math.abs(sqMeters(areaOf(ring)));

    // ── 宗地：**按 C# 的 ZoneGrid 格子划**（不再"沿街推带"）
    //
    // Placemaker 的 zoning 不是一个任意四边形，而是"道路自带的 8 m 格网里，
    // 被划成同一用途的一串连续格子"。所以流程是：
    //   道路 → ZoneBlockPredictor 预测 zone block → 每格求格心 → 格心落在街区里的格子算本街区
    //   → 取满的最大同深矩形（`zoningRects`）→ 共线相接的并成一块（`mergeRects`）
    //   → 按用途过最小面积（`filterRects`）→ 每条矩形就是一个 PlanZoning
    // 这样宗地的朝向、8 m 模数、深度上限（6 格 = 48 m）全部由格子几何保证，
    // 不需要我再"推一条带再裁"。
    //
    // **顺序不能反**：过滤必须放在合并之后。先过滤会把"1 列 + 邻居 9 列"里的那 1 列
    // 在合并前丢掉，那条满深带就永远短一截 —— 而"地块偏小"正是上一轮的病根（D-053）。
    // 面积下限按用途给（1 格 = 64 m²）：工业要大块，住宅可以小
    const MIN_CELLS: Record<string, number> = {
      industrial: 12,
      commercialHigh: 8,
      officeHigh: 8,
      officeLow: 6,
      commercialLow: 6,
      mixed: 6,
      residentialHigh: 6,
      residentialMedium: 4,
      residentialRow: 4,
      residentialLow: 4,
      lowRent: 4,
    };
    const rawRects = zoningRects(cellsByPatch.get(patchIndex) ?? [], grids, reduced);
    const mergedRects = mergeRects(rawRects);
    const rects = filterRects(mergedRects, {
      minCols: 2,
      minRows: 2,
      minCells: MIN_CELLS[kind] ?? 4,
    });
    rectsRaw += rawRects.length;
    rectsMerged += mergedRects.length;
    rectsFinal += rects.length;
    for (const r of rects) {
      if (r.sources > 1) rectsComposed++;
      parcelAreasM2.push(Math.abs(areaOf(r.quad.map(([x, z]) => ({ x, y: z })))));
    }    if (!rects.length) {
      skipped.push({
        patchIndex,
        use: lot.use,
        reason: '没有落在本街区里的 zone 格（道路没给这块地格网）',
      });
      return;
    }

    // 拟合比只作为**报告**：街区面积 / 它的 zone 格网覆盖面积。不再拿它当门槛。
    const zoneAreaM2 = rects.reduce((sum, r) => sum + Math.abs(areaOf(r.quad.map(([x, z]) => ({ x, y: z })))), 0);
    const fitRatio = zoneAreaM2 > 1 ? Math.min(1, patchAreaM2 / zoneAreaM2) : 0;

    // 层数由**规划参数**算：容积率 × 宗地面积 ÷ 基底面积，再被限高与用途层数区间夹住。
    const coverForPlan = kindPlanOf(kind).density;
    const plan = planFloors({
      use: lot.use,
      kind,
      footAreaM2: zoneAreaM2 * coverForPlan,
      landAreaM2: zoneAreaM2,
    });

    let footM2 = 0;
    for (const sd of solids) if (sd.ring) footM2 += Math.abs(sqMeters(areaOf(sd.ring as XY[])));

    fitMin = Math.min(fitMin, fitRatio);
    fitSum += fitRatio;

    patchAreaSumM2 += patchAreaM2;
    zonings.push({
      patchIndex,
      use: lot.use,
      kind,
      zoneLabel: kind,
      // `quad` 只放第一块格子矩形（用于对照/展示）；完整宗地在 `strips` 里
      quad: rects[0].quad,
      strips: rects.map((r) => r.quad),
      streetDir: rects[0].toRoad,
      quadAreaM2: zoneAreaM2,
      patchAreaM2,
      fitRatio,
      townforge: { buildings: lot.buildings ?? 0, solids: solids.length, footM2, maxHM },
      plan: {
        density: plan.plan.density,
        far: plan.plan.far,
        farActual: zoneAreaM2 > 0 ? (zoneAreaM2 * coverForPlan * plan.floors) / zoneAreaM2 : 0,
        heightRange: plan.plan.heightRange,
        floors: plan.floors,
        floorHeight: plan.floorHeight,
        heightM: plan.heightM,
        note: plan.note,
      },
    });

    tfBuildings += lot.buildings ?? 0;

    if (plan.clamped) clamped++;

    // ── 楼位：**每块格子矩形各切一次**（米制契约）。层数用上面按参数算出的 `plan`。
    for (let si = 0; si < rects.length; si++) {
      const rc = rects[si];
      const cut = cutZoning({ zoneId: ZONE_PREFAB[kind], quad: rc.quad }, kind, rc.toRoad, `p${patchIndex}s${si}`);
      for (const slot of cut.slots) {
        const lotQuad: [V2, V2, V2, V2] = [
          [slot.quad[0][0], slot.quad[0][1]],
          [slot.quad[1][0], slot.quad[1][1]],
          [slot.quad[2][0], slot.quad[2][1]],
          [slot.quad[3][0], slot.quad[3][1]],
        ];
        // 楼位 → 楼：按用途的覆盖比例收一圈（面积 × COVER → 边长 × √COVER）
        // 覆盖率 = 这个 kind 的规划密度（`KIND_PLAN`）。**只有一处定义** ——
        // 之前这里另有一张 `COVER` 表，两边数不一致，导致"实际容积率 ≠ 规划容积率"（差 5~15%）。
        const cover = coverForPlan;
        const footQuad = shrinkQuad(lotQuad, Math.sqrt(cover));

        // 不许压路：楼的 footprint 裁到街区里。
        //
        // 为什么是"裁"而不是"丢"（前一版就是丢）：楼位是从街区边推出来的矩形近似，
        // 端头/斜边总有半个楼位伸到街区外；直接丢会丢掉 1/3 的楼（覆盖率掉到 18~20%），
        // 而裁一刀只损失那几个平方米 —— 这正是 TownForge 自己的做法（"先摆后裁"，
        // 见 README §4.3：建筑原型先摆，再与可建区布尔求交，面积占比 < 52% 才丢）。
        const footRings = ptIntersect(quadToPts(footQuad), blockRingM, 1e-4);
        if (!footRings.length) {
          droppedOutside++;
          continue;
        }
        let footRing = footRings[0];
        let footA = Math.abs(ptArea(footRing));
        for (const r of footRings) {
          const a = Math.abs(ptArea(r));
          if (a > footA) {
            footA = a;
            footRing = r;
          }
        }
        const rawA = Math.abs(ptArea(quadToPts(footQuad)));
        if (rawA > 1e-6 && footA / rawA < 0.5) {
          // 裁掉一半以上说明这个楼位大半在街区外（斜边/楔形的角），留着也是怪形状
          droppedOutside++;
          continue;
        }
        if (footA < 40) {
          // 小于 40 m² 的碎块不盖楼（40 m² ≈ 一个车位，盖上去只是一块灰斑）
          droppedOutside++;
          continue;
        }
        if (footA < rawA * 0.995) clippedFoot++;
        slots.push({
          patchIndex,
          slotId: slot.id,
          kind,
          lotQuad,
          quad: footQuad,
          foot: footRing.map((q) => [q.x, q.y] as V2),
          width: slot.width,
          depth: slot.depth,
          moduleWidth: slot.moduleWidth,
          moduleDepth: slot.moduleDepth,
          floors: plan.floors,
          floorHeight: plan.floorHeight,
          heightM: plan.heightM,
        });
        depthSum += slot.depth;
        // 覆盖率按**楼的 footprint**算（收缩之后的），不是按楼位的地
        const fx = [footQuad[0][0], footQuad[1][0], footQuad[2][0], footQuad[3][0]];
        const fz = [footQuad[0][1], footQuad[1][1], footQuad[2][1], footQuad[3][1]];
        let fa = 0;
        for (let k = 0; k < 4; k++) fa += fx[k] * fz[(k + 1) % 4] - fx[(k + 1) % 4] * fz[k];
        slotAreaM2 += Math.abs(fa / 2);
        if (slot.moduleDepth) depthOnModule++;
        if (Math.abs(slot.depth - Math.round(slot.depth / 8) * 8) <= 4) nearModule++;
      }
    }
  });

  // ── 街道：TF 的每个边 → Placemaker 的 PlanNet（米）
  // 注意宽度取 **TF 的实际红线宽**（28/20/10.5 m），不是 PM 的名义宽度（17/12 m）。
  // 桥的契约是"把几何如实交给 PM"，不是"按 PM 的目录重画道路"。
  try {
    const net = roadNet;
    if (net) for (const e of net.edges) {
      const role: NetRecord['role'] = e.cls === 'arterial' ? 'avenue' : 'street';
      nets.push({
        cls: e.cls,
        role,
        widthM: ROAD_SPECS[e.cls].redLine,
        nominalM: NET_WIDTH[role] ?? 0,
        lengthM: e.len * UNIT_M,
      });
    }
  } catch {
    /* 路网没建起来就跳过：街道不是这条桥的必要条件 */
  }
  const widths = nets.map((n) => n.widthM).sort((a, b) => a - b);

  // ── 出入口：复现 EntranceSuggest。没有它，parking 家族跑不起来、ready 也算不出来。
  const access = roadNet ? buildAccess(model, roadNet) : EMPTY_ACCESS;
  const parking = buildParking(model, access);
  const park = buildPark(model);


  return {
    zonings,
    slots,
    nets,
    access,
    parking,
    park,
    landUseReport: landUse,
    skipped,
    stats: {
      patches: model.patches.filter((p) => p.withinCity).length,
      zonings: zonings.length,
      slots: slots.length,
      fitMin: Number.isFinite(fitMin) ? fitMin : 0,
      fitMed: zonings.length ? fitSum / zonings.length : 0,
      depthOnModule: slots.length ? depthOnModule / slots.length : 0,
      depthAvg: slots.length ? depthSum / slots.length : 0,
      tfBuildings,
      ourSlots: slots.length,
      // 被夹的**宗地**占比（不是楼位占比）：夹的判定发生在"整块地一个层数"这一步
      clampedShare: zonings.length ? clamped / zonings.length : 0,
      skippedByShape,
      depthNearModule: slots.length ? nearModule / slots.length : 0,
      coverShare: patchAreaSumM2 > 0 ? slotAreaM2 / patchAreaSumM2 : 0,
      droppedOutside,
      clippedFoot,
      parkingStalls: parking.stats.stalls,
      parkTrees: park.stats.trees,
      parkPaths: park.parks.reduce((n, p) => n + p.paths, 0),
      accessCount: access.stats.accessCount,
      attachedShare: access.stats.attachedShare,
      parkingTotal: access.stats.parkingTotal,
      parkingReady: access.stats.parkingReady,
      netCount: nets.length,
      netLengthM: nets.reduce((sum, n) => sum + n.lengthM, 0),
      netWidthMedM: widths.length ? widths[Math.floor(widths.length / 2)] : 0,
      parcelAreaMedM2: median(parcelAreasM2),
      parcelAreaMaxM2: parcelAreasM2.length ? Math.max(...parcelAreasM2) : 0,
      parcelAreaCv: cv(parcelAreasM2),
      rectsRaw,
      rectsMerged,
      rectsFinal,
      composedShare: rectsFinal > 0 ? rectsComposed / rectsFinal : 0,
      parcelCoverShare: patchAreaSumM2 > 0 ? parcelAreasM2.reduce((s, a) => s + a, 0) / patchAreaSumM2 : 0,
    },
  };
}

function median(a: number[]): number {
  if (!a.length) return 0;
  const s = a.slice().sort((x, y) => x - y);
  return s[Math.floor(s.length / 2)];
}

/** 变异系数（标准差 / 均值）。0 = 所有值一样大；用它一眼看出"是不是复印机城市"。 */
function cv(a: number[]): number {
  if (a.length < 2) return 0;
  const m = a.reduce((s, v) => s + v, 0) / a.length;
  if (m <= 0) return 0;
  const v = a.reduce((s, x) => s + (x - m) * (x - m), 0) / a.length;
  return Math.sqrt(v) / m;
}

/** 给 UI / 调试用：把桥的结果压成一行摘要。 */
export function bridgeSummary(r: BridgeReport): string {
  const s = r.stats;
  return `${s.patches} 街区 → ${s.zonings} 宗地（跳过 ${s.skippedByShape} 块太不方）→ ${s.slots} 楼位 · 拟合中位 ${(s.fitMed * 100).toFixed(0)}% · 进深 ${s.depthAvg.toFixed(1)} m（${(s.depthNearModule * 100).toFixed(0)}% 贴近 8 m 格）`;
}
