// 归一化 + 差分。
//
// 为什么不能直接比 JSON：
//   1) 浮点尾数不同（0.10000000000000001 vs 0.1）——无害但让 diff 全红
//   2) 元素顺序不同——两个引擎的遍历顺序本来就不一样
//   3) 环绕序不同——同一块地的环可能顺时针也可能逆时针
//   4) **随机序列不同**（C# 走 Unity.Mathematics，TS 走 mulberry32）——这是硬差异，
//      所以差分必须分三级（见 docs/A §5、台账 D-015）：
//        L1 结构不变量：计数/面积/长度/拓扑 —— 必须一致
//        L2 几何近似：对称差占比 —— 容差内即可
//        L3 逐值一致：只对无随机纯函数（inset/split/clip）适用

import { area, ccw, centroid, dist, q, quantizeRing } from './geom.ts';
import type { Plan, PlanNet, PlanObject, PlanSurface } from './types.ts';

/** 浮点噪声阈值：1 mm。与 geom 的 QUANT 一致。 */
export const TOL = 1e-3;

export function normalizePlan(plan: Plan): Plan {
  const surfaces = plan.surfaces
    .map((s) => {
      // 洞也要归一：量化 + **顺时针**（与外环相反）。不归一的话，
      // 同一份几何换个输入顺序就会算出不同的 key，快照不再稳定。
      const holes = (s.holes ?? [])
        .map((h) => quantizeRing(ccw(h)).slice().reverse())
        .filter((h) => h.length >= 3 && area(h) > 1e-4);
      const ring = quantizeRing(ccw(s.ring));
      return { ...s, ring, ...(holes.length ? { holes } : { holes: undefined }), y: s.y === undefined ? 0 : q(s.y) };
    })
    // 有效面积 = 外环 − 洞；被洞吃光的表面要丢掉（否则会留下一个"看不见但占数"的面）
    .filter((s) => s.ring.length >= 3 && area(s.ring) - (s.holes ?? []).reduce((a, h) => a + area(h), 0) > 0.01)
    .sort((a, b) => keyOf(a) .localeCompare(keyOf(b)));

  const nets = plan.nets.map((n) => normalizeNet(n)).sort((a, b) => keyOf(a).localeCompare(keyOf(b)));

  const objects = plan.objects
    .map((o) => ({ ...o, x: q(o.x), z: q(o.z), yaw: o.yaw === undefined ? undefined : q(o.yaw) }))
    .sort((a, b) => keyOf(a).localeCompare(keyOf(b)));

  const zoning = plan.zoning
    .map((z) => ({ ...z, quad: quantizeRing(ccw(z.quad)) as unknown as Plan['zoning'][number]['quad'] }))
    .sort((a, b) => keyOf(a).localeCompare(keyOf(b)));

  return { ...plan, surfaces, nets, objects, zoning };
}

function normalizeNet(n: PlanNet): PlanNet {
  return {
    ...n,
    a: [q(n.a[0]), q(n.a[1])],
    b: [q(n.b[0]), q(n.b[1])],
    c: [q(n.c[0]), q(n.c[1])],
    d: [q(n.d[0]), q(n.d[1])],
  };
}

/** 排序键：角色 + 质心（量化到 0.1 m）+ 洞签名。 */
function keyOf(e: PlanSurface | PlanNet | PlanObject | { quad: readonly (readonly [number, number])[] }): string {
  const role = 'role' in e ? (e.role as string) : 'zoning';
  const cat = 'category' in e && e.category ? e.category : '';
  let c: readonly [number, number];
  if ('ring' in e) c = centroid(e.ring);
  else if ('quad' in e) c = centroid(e.quad as readonly (readonly [number, number])[]);
  else if ('x' in e) c = [e.x, e.z];
  else c = centroid([e.a, e.b, e.c, e.d]);
  // **洞要进排序键**：只改了洞的两块面不能被当成同一个（否则 normalize 之后顺序不稳）
  const holes = (e as { holes?: readonly (readonly [number, number])[][] }).holes;
  const hk = Array.isArray(holes)
    ? holes.map((h) => centroid(h).map((v) => (Math.round(v * 10) / 10).toFixed(1)).join(',')).join(';')
    : '';
  return `${role}\u0000${cat}\u0000${(Math.round(c[0] * 10) / 10).toFixed(1)}\u0000${(Math.round(c[1] * 10) / 10).toFixed(1)}\u0000${hk}`;
}

/** L1 结构不变量。这些数必须与 C# 一致，否则行为不等价。 */
export interface Invariants {
  ready: boolean;
  surfaceCount: number;
  surfaceAreaByRole: Record<string, number>;
  netCount: number;
  netLengthByRole: Record<string, number>;
  objectCount: number;
  objectCountByRole: Record<string, number>;
  zoningCount: number;
  zoningArea: number;
  totalFootprintArea: number;
}

export function invariants(plan: Plan): Invariants {
  const surfArea: Record<string, number> = {};
  for (const s of plan.surfaces) surfArea[s.role] = (surfArea[s.role] ?? 0) + area(s.ring);

  const netLen: Record<string, number> = {};
  for (const n of plan.nets) {
    // 折线近似长度：控制多边形的一半是常见低估，这里用 8 段采样保证与 C# 采样同阶
    const l = approxBezierLength(n);
    netLen[n.role] = (netLen[n.role] ?? 0) + l;
  }

  const objRole: Record<string, number> = {};
  for (const o of plan.objects) objRole[o.role] = (objRole[o.role] ?? 0) + 1;

  let zoningArea = 0;
  for (const z of plan.zoning) zoningArea += area(z.quad);

  const round = (r: Record<string, number>) => {
    const out: Record<string, number> = {};
    for (const k of Object.keys(r).sort()) out[k] = Math.round(r[k] * 100) / 100;
    return out;
  };

  return {
    ready: plan.ready,
    surfaceCount: plan.surfaces.length,
    surfaceAreaByRole: round(surfArea),
    netCount: plan.nets.length,
    netLengthByRole: round(netLen),
    objectCount: plan.objects.length,
    objectCountByRole: round(objRole),
    zoningCount: plan.zoning.length,
    zoningArea: Math.round(zoningArea * 100) / 100,
    totalFootprintArea:
      Math.round(((surfArea.paving ?? 0) + (surfArea.asphalt ?? 0) + (surfArea.plaza ?? 0)) * 100) / 100,
  };
}

export function approxBezierLength(n: { a: readonly [number, number]; b: readonly [number, number]; c: readonly [number, number]; d: readonly [number, number] }): number {
  let l = 0;
  let prev = n.a;
  for (let i = 1; i <= 8; i++) {
    const t = i / 8;
    const u = 1 - t;
    const p: readonly [number, number] = [
      n.a[0] * u * u * u + n.b[0] * 3 * u * u * t + n.c[0] * 3 * u * t * t + n.d[0] * t * t * t,
      n.a[1] * u * u * u + n.b[1] * 3 * u * u * t + n.c[1] * 3 * u * t * t + n.d[1] * t * t * t,
    ];
    l += dist(prev, p);
    prev = p;
  }
  return l;
}

export interface DiffRow {
  field: string;
  expected: unknown;
  actual: unknown;
  pass: boolean;
  note?: string;
}

/**
 * L1 差分。计数必须完全相等；面积/长度允许相对误差（默认 2%，
 * 因为浮点与随机抽样顺序都会带来微小差别，但**计数**不接受任何容差）。
 */
export function diffL1(expected: Invariants, actual: Invariants, relTol = 0.02): DiffRow[] {
  const rows: DiffRow[] = [];
  const eq = (field: string, a: unknown, b: unknown) =>
    rows.push({ field, expected: a, actual: b, pass: JSON.stringify(a) === JSON.stringify(b) });

  eq('ready', expected.ready, actual.ready);
  eq('surfaceCount', expected.surfaceCount, actual.surfaceCount);
  eq('netCount', expected.netCount, actual.netCount);
  eq('objectCount', expected.objectCount, actual.objectCount);
  eq('zoningCount', expected.zoningCount, actual.zoningCount);

  diffNumMap('surfaceAreaByRole', expected.surfaceAreaByRole, actual.surfaceAreaByRole, relTol, rows);
  diffNumMap('netLengthByRole', expected.netLengthByRole, actual.netLengthByRole, relTol, rows);
  diffNumMap('objectCountByRole', expected.objectCountByRole, actual.objectCountByRole, 0, rows);
  diffNumWithTol('zoningArea', expected.zoningArea, actual.zoningArea, relTol, rows);
  diffNumWithTol('totalFootprintArea', expected.totalFootprintArea, actual.totalFootprintArea, relTol, rows);

  return rows;
}

function diffNumMap(field: string, a: Record<string, number>, b: Record<string, number>, relTol: number, rows: DiffRow[]): void {
  const keys = [...new Set([...Object.keys(a), ...Object.keys(b)])].sort();
  for (const k of keys) {
    const va = a[k] ?? 0;
    const vb = b[k] ?? 0;
    const pass = relTol === 0 ? va === vb : Math.abs(va - vb) <= Math.max(0.01, Math.abs(va) * relTol);
    rows.push({ field: `${field}.${k}`, expected: va, actual: vb, pass });
  }
}

function diffNumWithTol(field: string, a: number, b: number, relTol: number, rows: DiffRow[]): void {
  const pass = Math.abs(a - b) <= Math.max(0.01, Math.abs(a) * relTol);
  rows.push({ field, expected: a, actual: b, pass });
}

/** 把差分结果渲染成给人看的表（验收脚本直接打印）。 */
export function formatDiff(rows: DiffRow[]): string {
  const bad = rows.filter((r) => !r.pass);
  const lines: string[] = [];
  lines.push(`L1 结构不变量：${rows.length - bad.length}/${rows.length} 通过`);
  if (bad.length) {
    for (const r of bad) lines.push(`  ✗ ${r.field}  期望 ${JSON.stringify(r.expected)}  实际 ${JSON.stringify(r.actual)}`);
  }
  return lines.join('\n');
}

/** 稳定序列化：键排序、数值四舍五入到 1e-4，用于入库当基准。 */
export function stableStringify(value: unknown): string {
  const walk = (v: unknown): unknown => {
    if (typeof v === 'number') return Math.round(v * 1e4) / 1e4;
    if (Array.isArray(v)) return v.map(walk);
    if (v && typeof v === 'object') {
      const o: Record<string, unknown> = {};
      for (const k of Object.keys(v as Record<string, unknown>).sort()) o[k] = walk((v as Record<string, unknown>)[k]);
      return o;
    }
    return v;
  };
  return JSON.stringify(walk(value), null, 1);
}
