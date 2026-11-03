// 生成资格（Eligibility）—— **第一道门**，独立于生成。
//
// 为什么要单独一层：`part3/part7/part2` 那批碎地不是"停车几何算错"，而是
// **"这块地到底该不该生成停车场"** 的决策差。把决策混进生成里，就只能靠放宽误差去兼容，
// 而那会把"golden 覆盖 43% / native 44%"这种两边都不合理的状态判成 PASS。
//
// 判据不用纯面积（`area < 1600 → false` 是错的）：同样的面积，长条形和方块形的可行性差很多。
// 这里用的是**能不能把停车模组放进有效进深**：
//
//   单排模组 = stallLength + aisleWidth           （5.9 + 7.0 = 12.9 m）
//   双排模组 = stallLength + aisleWidth + stallLength （5.9 + 7.0 + 5.9 = 18.8 m）
//
// 长/宽取自**最小面积外接矩形**，不是轴对齐包围盒 —— 后者会被 45° 斜边骗
// （细长斜地块的 bbox 又大又方，实际没有一个方向排得下行带）。
//
// 阈值不是拍出来的：先跑 `cli/feasibility.ts` 把 19 个 parking 用例打成表，
// 拿 `oracleReady / oracleStalls` 找出分界，再回填到本文件的 `RULES`。
// 见 docs/H §5 的表。

import { area as ringArea, type Vec2 } from '../../plan-ir/src/geom.ts';
import { minAreaBox } from './primitives/polygon.ts';
import type { SiteInput } from './types.ts';

const num = (v: unknown, d: number): number => (typeof v === 'number' && Number.isFinite(v) ? v : d);

export interface ParkingFeasibility {
  /** 地块面积（m²） */
  area: number;
  /** 最小外接矩形的长边（"主要长度"） */
  dominantLength: number;
  /** 最小外接矩形的短边（"主要进深"） */
  dominantDepth: number;
  /** 地块面积 / 外接矩形面积 —— 1 = 方正，越小越碎 */
  compactness: number;
  /** 扣掉两侧退线后的可用进深 */
  usableDepth: number;
  /** 扣掉两端退线后的可用长度 */
  usableLength: number;
  /** 单排模组进深（车位 + 车道） */
  moduleSingle: number;
  /** 双排模组进深（车位 + 车道 + 车位） */
  moduleDouble: number;
  /** 能排下几排行带（双面 / 尾部单面都算） */
  estimatedRows: number;
  /** 其中单面（只服务一侧）的排数 —— 尾部余深用得上 */
  estimatedSingleRows: number;
  /** 估算容量（车位） */
  estimatedCapacity: number;
  /** 边界出入口数（来自 oracle 解析后的 markers） */
  entranceCount: number;
  /** 附近街道数 */
  streetCount: number;
  /** 障碍物数（建筑轮廓） */
  obstacles: number;
}

/**
 * 两个概念必须分开，**这是本文件最重要的设计决定**：
 *
 * | 字段 | 语义 | 谁决定 |
 * | --- | --- | --- |
 * | `ready` | 这个图层**能不能跑**（镜像 `Plan.ready` 的语义） | Placemaker 的 `CheckReady` |
 * | `feasible` | 这块地**有没有地方排停车模组** | 我们自己的几何判据 |
 *
 * 为什么必须分开：实测 19 个 golden 用例里，`oracle.ready` **只有 1 例是 false**
 * （`part6`，160 m² 的三角形），其余 18 例全是 true —— **包括它一个车位都不排的那些**。
 * 所以：
 *   · 拿 `ready` 当"该不该生成停车场"的判据 → 无效，它几乎恒真；
 *   · 反过来，如果因为它恒真就"一律排满"，就会在 6~14 m 有效进深的碎地上硬塞 10~18 个位。
 * 拆开之后：`ready` 对齐 oracle 的语义（行为参考），`feasible` 管我们要不要落车位（几何事实）。
 */
export interface ParkingEligibility {
  /** 图层能不能跑 —— 对齐 oracle 的 `ready` 语义 */
  ready: boolean;
  /** 有没有地方排模组 —— 决定"落车位"还是"只铺地" */
  feasible: boolean;
  feasibility: ParkingFeasibility;
  /** 不可行的原因（逐条，要上报到 `issues`） */
  reasons: string[];
}

export function parkingFeasibility(input: SiteInput): ParkingFeasibility {
  const ring: Vec2[] = input.shape.ring.map(([x, z]) => [x, z] as Vec2);
  const siteArea = Math.abs(ringArea(ring));
  const s = input.settings;
  const setback = Math.max(0, num(s.setback, 1));
  const stallWidth = Math.max(1.5, num(s.stallWidth, 3));
  const stallLength = Math.max(2, num(s.stallLength, 5.9));
  const aisleWidth = Math.max(2.5, num(s.aisleWidth, 7));
  const crossEvery = Math.max(2, Math.round(num(s.crossEvery, 9)));

  const box = ring.length >= 3 ? minAreaBox(ring) : { w: 0, h: 0, u: [1, 0] as Vec2, cx: 0, cy: 0 };
  const dominantLength = box.w;
  const dominantDepth = box.h;
  const compactness = box.w * box.h > 1e-6 ? siteArea / (box.w * box.h) : 0;

  const usableDepth = Math.max(0, dominantDepth - 2 * setback);
  const usableLength = Math.max(0, dominantLength - 2 * setback);

  const moduleSingle = stallLength + aisleWidth;
  const moduleDouble = stallLength * 2 + aisleWidth;
  // 行数：先尽量排双面（2 个车位 + 1 条车道），尾部余深够一排单面（车位 + 车道）就再补一排。
  // 这一条是实测补的：part7 有效进深 **15.1 m**，排不满双面模组 18.8，但放得下单面 12.9 ——
  // oracle 在那里确实排了车位（1 个）。只按双面算会把这类地误判成"不可行"。
  let remain = usableDepth;
  let doubleRows = 0;
  if (moduleDouble > 0) {
    while (remain >= moduleDouble) {
      doubleRows++;
      remain -= moduleDouble;
    }
  }
  const singleRows = remain >= moduleSingle ? 1 : 0;
  const estimatedRows = doubleRows + singleRows;

  // 容量估算：双排服务两侧、单排服务一侧；每侧按 usableLength 全铺算
  const stallsPerRow = usableLength > 0 ? Math.floor(usableLength / stallWidth) : 0;
  const estimatedCapacity = Math.max(0, stallsPerRow * (doubleRows * 2 + singleRows));

  return {
    area: siteArea,
    dominantLength,
    dominantDepth,
    compactness,
    usableDepth,
    usableLength,
    moduleSingle,
    moduleDouble,
    estimatedRows,
    estimatedSingleRows: singleRows,
    estimatedCapacity,
    entranceCount: input.markers.length,
    streetCount: input.nearbyNets.length,
    obstacles: input.obstacles.length,
  };
}

/**
 * 判据表。**这些数从 19 个 golden 用例反推**，不是拍脑袋：
 *
 * 见 `cli/feasibility.ts` 打出来的表 —— 把 `oracleReady / oracleStalls` 与
 * `estimatedCapacity / usableDepth` 并排看，分界就出来了。
 */
export const RULES = {
  /**
   * 有效进深下限。**这个数是量出来的，不是拍的**：
   * 19 例里"oracle 完全不排车位"的 7 例，有效进深落在 **6~14 m**；
   * "oracle 排了车位"的 12 例，落在 **15~127 m**。分界正好在 15。
   * 物理意义也对得上：单排模组 = 车位 5.9 + 车道 7.0 = 12.9 m，15 m 略有余量。
   */
  minUsableDepth: 15,
  /** 估算容量低于这个数 → 不值得做（一块地上 2~3 个位不成其为停车场） */
  minCapacity: 4,
  /** 必须有出入口：没有车行口就排不出可用的停车 */
  requireEntrance: true,
  /** `ready` 的最小地块面积（镜像 oracle：160 m² 的三角形它就判 false） */
  minArea: 200,
};

/**
 * 生成资格判定。
 *
 * 返回的 `reasons` 要一路带到 `Plan.issues` —— "为什么不生成"必须可读，
 * 否则下游只会看到一块空地，然后以为是渲染坏了（沿用 D-037 的教训）。
 */
export function checkParkingReady(input: SiteInput): ParkingEligibility {
  const f = parkingFeasibility(input);
  const reasons: string[] = [];

  // ── feasible：几何上有没有地方排停车模组（决定"落不落车位"）
  if (f.usableDepth < RULES.minUsableDepth) reasons.push(`usableDepth<${RULES.minUsableDepth}m`);
  if (f.estimatedCapacity < RULES.minCapacity) reasons.push(`capacity<${RULES.minCapacity}`);
  const feasible = reasons.length === 0;

  // ── ready：图层能不能跑（镜像 oracle 语义，**不因"排不下车位"而 false**）
  const runReasons: string[] = [];
  if (RULES.requireEntrance && f.entranceCount === 0) runReasons.push('noEntrance');
  if (f.area < RULES.minArea) runReasons.push(`area<${RULES.minArea}`);

  return { ready: runReasons.length === 0, feasible, feasibility: f, reasons: [...runReasons, ...reasons] };
}
