// Plan 的**纯函数度量**。两个后端产出 Plan 之后，只靠这一层比。
//
// 三条设计约束：
//
// 1. **不做绝对判据，只做相对判据。** 每个数都要拿去跟 golden 比，"应该是多少"由
//    Placemaker 决定，不由我拍。上一轮的教训（D-050 / D-056）就是我自己定了"覆盖率该 30%"，
//    结果指标对了、城市还是不对。
// 2. **度量必须与后端无关。** 只吃 `Plan`，不知道 Plan 是谁产的。否则"原生生成器"
//    和"golden 生成器"会被各自的口径美化，比对就失去意义。
// 3. **每个数都要能被一句话解释。** 指标名字里带单位，并且写明它对应 Placemaker 的哪个概念。
//
// 依赖：只用 `plan-ir/geom.ts`（零第三方依赖）。重叠这种需要布尔运算的量改用**栅格采样**，
// 精度 0.5 m 对"两套实现差多少"完全够，而且不用引入 polygon-clipping。

import { area as ringArea, bounds, dist, pointInRing } from '../../plan-ir/src/geom.ts';
import type { Plan } from './types.ts';

/** 栅格采样步长（米）。0.5 m 对 100 m 级地块是 200×200，几万格，毫秒级。 */
const RASTER_STEP = 0.5;

export interface PlanMetrics {
  /** 地块面积（m²），来自 Plan.area */
  siteArea: number;
  /** 各 role 的铺装面积（m²）—— 对应 Placemaker 的 `surfaces` 通道 */
  surfaceByRole: Record<string, number>;
  surfaceTotal: number;
  /** 各 role 的中心线总长（m）—— 对应 `nets`（园路 / 车位车道 / 围墙） */
  netByRole: Record<string, number>;
  netTotal: number;
  netCount: number;
  /** 各 role 的物件数 —— 对应 `objects`（树 / 车位 / 灯 / 出入口点） */
  objectByRole: Record<string, number>;
  objectTotal: number;
  /** 每 1000 m² 的物件数 */
  objectDensity: number;
  /** 车位（`objects.parkingBay`）—— 停车场族的硬指标 */
  parkingBays: number;
  /** 出入口点（`objects.accessPoint`），以及输入侧给了几个标记 */
  accessPoints: number;
  markersGiven: number;
  markersAuto: number;
  /** 规划分区面积与占比（`zoning`） */
  zoningArea: number;
  zoningCoverage: number;
  /** 油漆（`paint`）总面积 —— CS2 的地面刷，我们只当"额外覆盖"看 */
  paintArea: number;
  /** 地形改动条数（`terraform`） */
  terraformOps: number;
  /** 不同 role 的铺装互相压住的面积（m²）。golden 自己也不是 0，所以只比值 */
  overlapArea: number;
  /** 铺装对地块的覆盖率（= surfaceTotal / siteArea），可能 >1（刻意叠层） */
  surfaceCoverage: number;
  /**
   * 路网连通性：把 `nets` 的端点按 0.5 m 归并成图，数连通块与悬空端。
   * 对应 Placemaker 里"园路必须连成一片、且接到出入口"这条不成文规则。
   */
  netComponents: number;
  netDeadEnds: number;
  /** 最长的连通路网（m）—— 断成碎片的方案这个数会很小 */
  netLongestComponent: number;
  /** `issues` 按严重度计数 */
  issues: { info: number; warning: number; error: number };
  /** 是否 ready（Placemaker 自报） */
  ready: boolean;
}

const addTo = (m: Record<string, number>, k: string | undefined, v: number): void => {
  const key = k ?? '(none)';
  m[key] = (m[key] ?? 0) + v;
};

/** 净长：`nets` 的 (a→d) 直线距离。曲线控制点 b/c 不参与，保证两个后端可同口径比。 */
const netLength = (n: Plan['nets'][number]): number => dist(n.a, n.d);

/** 端点归并键（0.5 m） */
const nodeKey = (x: number, y: number): string => `${Math.round(x * 2)},${Math.round(y * 2)}`;

/**
 * 铺装重叠面积（栅格采样）。
 *
 * 为什么要这个数：两套实现最容易出现的差异不是"少画一块"，而是"多画一块压在上面"
 * （比如沥青铺满整块地、草地又铺一遍）。这种错在条目数上看不出来，在画面上一眼可见。
 * 但它**不是"必须为 0"**——Placemaker 自己也会刻意叠层，所以只跟 golden 比值。
 */
function overlapArea(plan: Plan, b: { minX: number; minY: number; maxX: number; maxY: number }): number {
  const rings = plan.surfaces.filter((s) => s.ring.length >= 3);
  if (rings.length < 2) return 0;
  let hit = 0;
  let cells = 0;
  for (let y = b.minY; y <= b.maxY; y += RASTER_STEP) {
    for (let x = b.minX; x <= b.maxX; x += RASTER_STEP) {
      cells++;
      let n = 0;
      for (const s of rings) {
        if (pointInRing([x, y], s.ring)) {
          n++;
          if (n >= 2) {
            hit++;
            break;
          }
        }
      }
    }
  }
  void cells;
  return hit * RASTER_STEP * RASTER_STEP;
}

/** 路网连通性：端点图上的连通块与悬空端 */
function connectivity(plan: Plan): { components: number; deadEnds: number; longest: number } {
  const edges = plan.nets
    .map((n) => ({ a: nodeKey(n.a[0], n.a[1]), b: nodeKey(n.d[0], n.d[1]), len: netLength(n) }))
    .filter((e) => e.a !== e.b && e.len > 1e-6);
  if (!edges.length) return { components: 0, deadEnds: 0, longest: 0 };

  const parent = new Map<string, string>();
  const find = (k: string): string => {
    let r = k;
    while (parent.get(r) !== r) r = parent.get(r) as string;
    // 路径压缩
    let c = k;
    while (parent.get(c) !== r) {
      const nx = parent.get(c) as string;
      parent.set(c, r);
      c = nx;
    }
    return r;
  };
  const union = (a: string, b: string): void => {
    if (!parent.has(a)) parent.set(a, a);
    if (!parent.has(b)) parent.set(b, b);
    const ra = find(a);
    const rb = find(b);
    if (ra !== rb) parent.set(ra, rb);
  };
  const degree = new Map<string, number>();
  const bump = (k: string): void => void degree.set(k, (degree.get(k) ?? 0) + 1);

  for (const e of edges) {
    union(e.a, e.b);
    bump(e.a);
    bump(e.b);
  }

  const lenByRoot = new Map<string, number>();
  for (const e of edges) {
    const r = find(e.a);
    lenByRoot.set(r, (lenByRoot.get(r) ?? 0) + e.len);
  }
  let deadEnds = 0;
  for (const d of degree.values()) if (d === 1) deadEnds++;
  return {
    components: lenByRoot.size,
    deadEnds,
    longest: Math.max(...lenByRoot.values()),
  };
}

export function measure(plan: Plan): PlanMetrics {
  const surfaceByRole: Record<string, number> = {};
  for (const s of plan.surfaces) if (s.ring.length >= 3) addTo(surfaceByRole, s.role, ringArea(s.ring));

  const netByRole: Record<string, number> = {};
  for (const n of plan.nets) addTo(netByRole, n.role, netLength(n));

  const objectByRole: Record<string, number> = {};
  for (const o of plan.objects) addTo(objectByRole, o.role, 1);

  const siteArea = plan.area > 0 ? plan.area : 1;
  const b = plan.surfaces.length
    ? plan.surfaces.reduce(
        (acc, s) => {
          const r = bounds(s.ring);
          return {
            minX: Math.min(acc.minX, r.minX),
            minY: Math.min(acc.minY, r.minY),
            maxX: Math.max(acc.maxX, r.maxX),
            maxY: Math.max(acc.maxY, r.maxY),
          };
        },
        { minX: Infinity, minY: Infinity, maxX: -Infinity, maxY: -Infinity },
      )
    : { minX: 0, minY: 0, maxX: 0, maxY: 0 };

  const conn = connectivity(plan);
  const issues = { info: 0, warning: 0, error: 0 };
  for (const i of plan.issues) issues[i.severity]++;

  const surfaceTotal = Object.values(surfaceByRole).reduce((s, v) => s + v, 0);
  const netTotal = Object.values(netByRole).reduce((s, v) => s + v, 0);
  const objectTotal = plan.objects.length;

  return {
    siteArea: plan.area,
    surfaceByRole,
    surfaceTotal,
    netByRole,
    netTotal,
    netCount: plan.nets.length,
    objectByRole,
    objectTotal,
    objectDensity: (objectTotal / siteArea) * 1000,
    parkingBays: objectByRole['parkingBay'] ?? 0,
    accessPoints: objectByRole['accessPoint'] ?? 0,
    markersGiven: plan.markers?.given ?? 0,
    markersAuto: plan.markers?.auto ?? 0,
    zoningArea: plan.zoning.reduce((s, z) => s + ringArea(z.quad), 0),
    zoningCoverage: plan.zoning.reduce((s, z) => s + ringArea(z.quad), 0) / siteArea,
    paintArea: plan.paint.reduce((s, p) => s + ringArea(p.ring), 0),
    terraformOps: plan.terraform.length,
    overlapArea: overlapArea(plan, b),
    surfaceCoverage: surfaceTotal / siteArea,
    netComponents: conn.components,
    netDeadEnds: conn.deadEnds,
    netLongestComponent: conn.longest,
    issues,
    ready: plan.ready,
  };
}

/**
 * 扁平化成"可比的标量列表"。
 *
 * 为什么要扁平：比对表要能一眼扫过 20 个数，而嵌套结构在表格里没法对齐。
 * key 里带单位，`group` 用来分块打印。
 */
export interface MetricRow {
  key: string;
  label: string;
  group: string;
  value: number;
}

export function rows(m: PlanMetrics): MetricRow[] {
  const out: MetricRow[] = [
    { key: 'surfaceTotal', label: '铺装总面积 m²', group: '覆盖', value: m.surfaceTotal },
    { key: 'surfaceCoverage', label: '铺装 / 地块', group: '覆盖', value: m.surfaceCoverage },
    { key: 'overlapArea', label: '铺装互相压住 m²', group: '覆盖', value: m.overlapArea },
    { key: 'zoningCoverage', label: '分区 / 地块', group: '覆盖', value: m.zoningCoverage },
    { key: 'paintArea', label: '油漆 m²', group: '覆盖', value: m.paintArea },
    { key: 'netTotal', label: '路网总长 m', group: '路网', value: m.netTotal },
    { key: 'netComponents', label: '路网连通块数', group: '路网', value: m.netComponents },
    { key: 'netDeadEnds', label: '悬空端点数', group: '路网', value: m.netDeadEnds },
    { key: 'netLongest', label: '最长连通网 m', group: '路网', value: m.netLongestComponent },
    { key: 'parkingBays', label: '车位数', group: '物件', value: m.parkingBays },
    { key: 'accessPoints', label: '出入口点', group: '物件', value: m.accessPoints },
    { key: 'objectTotal', label: '物件总数', group: '物件', value: m.objectTotal },
    { key: 'objectDensity', label: '物件密度 /千m²', group: '物件', value: m.objectDensity },
    { key: 'terraformOps', label: '地形改动条数', group: '其它', value: m.terraformOps },
    { key: 'issues', label: '问题条数', group: '其它', value: m.issues.info + m.issues.warning + m.issues.error },
  ];
  return out;
}

/**
 * 按 role 的明细差集（哪个 role 多了/少了多少）。
 *
 * 汇总数（比如物件总数一样）会掩盖"树多了 100 棵、车位少了 100 个"，
 * 所以每个通道还要按 role 摊开比。
 */
export function roleDiff(
  a: Record<string, number>,
  b: Record<string, number>,
): { role: string; golden: number; native: number }[] {
  const keys = new Set([...Object.keys(a), ...Object.keys(b)]);
  return [...keys]
    .map((role) => ({ role, golden: a[role] ?? 0, native: b[role] ?? 0 }))
    .filter((r) => Math.abs(r.golden - r.native) > 1e-6)
    .sort((x, y) => Math.abs(y.golden - y.native) - Math.abs(x.golden - x.native));
}
