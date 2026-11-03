// 用地类型：**按城市形态分配 11 类 ZoneKind**（不再用 TownForge 的 5 类去反推）。
//
// 为什么要换：之前是"TownForge 先分 5 类（cbd/商业/住宅/工业/公共）→ 我们再按实测楼高映到 11 类"，
// 结果两种病：
//   1. **类型重复**：住宅全落成 `residentialMedium`、商业全落成 `commercialHigh`——
//      11 类里实际只用到 6~7 类，城市看起来"一片同款"；
//   2. **分布不讲道理**：映射依据是"楼高"，而楼高本来就是我们自己排出来的（循环论证）。
//
// 现在改成：**用地按"离城市中心多远 + 门前是什么路"来定**，这也是真实城市的形态规律
// （同心环 + 主干道商业带），并且**保底保证 11 类都出现**。
//
// 与 Placemaker 的关系：Placemaker 不管"哪块地是什么用途"（那是玩家的活），
// 它只规定"每种用途对应什么尺寸/层数/层高"（`KIND_SPECS`）。所以这一层是我们自己的规划规则，
// 但**产出的口径完全按 Placemaker 的 11 类**，下游（楼位/层数/资产）才能对得上。

import { Pt } from '../core/geom';
import { UNIT_M } from '../core/units';
import type { Model } from '../town/model';
import type { RoadNet } from '../road/system';
import type { ZoneKind } from './zone';

/** 可能出现的 11 类（和 `KIND_SPECS` 一一对应） */
export const ALL_KINDS: ZoneKind[] = [
  'commercialLow',
  'commercialHigh',
  'mixed',
  'residentialLow',
  'residentialRow',
  'residentialMedium',
  'residentialHigh',
  'lowRent',
  'officeLow',
  'officeHigh',
  'industrial',
];

/**
 * 同心环的候选表。q = 到城市中心的距离 / 城市半径。
 *
 * 为什么用"候选 + 哈希挑"而不是"一刀切"：真实城市的同一条环上也是混着的
 * （同一圈里有住宅、有社区商业、有办公）。给 2~3 个候选再按确定性哈希挑，
 * 既不会一刀切，也不会随机到读不出规律。
 */
const BANDS: { q: number; kinds: ZoneKind[] }[] = [
  // 核心：高层办公 + 高密度商业（CBD）
  { q: 0.12, kinds: ['officeHigh', 'commercialHigh', 'officeHigh'] },
  // 内圈：办公/商业混合
  { q: 0.3, kinds: ['commercialHigh', 'officeLow', 'mixed', 'commercialLow'] },
  // 中圈：中高密度住宅 + 混合
  { q: 0.52, kinds: ['residentialHigh', 'residentialMedium', 'mixed', 'residentialMedium'] },
  // 外圈：中低密度住宅
  { q: 0.72, kinds: ['residentialMedium', 'residentialRow', 'residentialLow', 'commercialLow'] },
  // 边缘：低密度 / 廉租 / 工业
  { q: 0.88, kinds: ['residentialLow', 'lowRent', 'residentialRow', 'industrial'] },
  // 最外：工业与廉租
  { q: 1.01, kinds: ['industrial', 'lowRent', 'industrial', 'residentialLow'] },
];

function hash01(s: string): number {
  let h = 2166136261;
  for (let i = 0; i < s.length; i++) {
    h ^= s.charCodeAt(i);
    h = Math.imul(h, 16777619);
  }
  return ((h >>> 0) % 100000) / 100000;
}

export interface KindRecord {
  patchIndex: number;
  kind: ZoneKind;
  /** 到中心的归一化距离（0=中心，1=边缘） */
  q: number;
  /** 门前最靠近哪一级路 */
  roadCls: 'arterial' | 'collector' | 'local' | null;
  /** 是否因为"保底覆盖 11 类"被换过 */
  forced: boolean;
}

export interface LandUseReport {
  kinds: KindRecord[];
  byKind: Record<string, number>;
  /** 用到几类（应该 = 11） */
  distinct: number;
}

/**
 * 给每个"要排建筑"的街区定一个 ZoneKind。
 *
 * 两个规则叠加：
 *   1. **同心环**（主规则）：q 决定候选表；
 *   2. **主干道加成**：贴主干道的街区往商业/混合挪一档 —— 真实城市的商业沿主干道长，不是均匀铺开。
 * 最后做**保底补位**：11 类里缺哪个，就从"最贴近它该在的位置"的街区里换一个过来
 * （换的时候优先换掉重复最多的那一类），保证城市里 11 类都看得到。
 */
export function planLandUse(
  model: Model,
  roadNet: RoadNet | null,
  isZonable: (i: number) => boolean,
): LandUseReport {
  const items: { i: number; cx: number; cy: number; area: number; roadCls: KindRecord['roadCls'] }[] = [];
  const pts: { x: number; y: number }[] = [];
  model.patches.forEach((p, i) => {
    if (!p.withinCity || !isZonable(i)) return;
    let cx = 0;
    let cy = 0;
    for (const v of p.shape) {
      cx += v.x;
      cy += v.y;
    }
    cx /= p.shape.length;
    cy /= p.shape.length;
    items.push({ i, cx: cx * UNIT_M, cy: cy * UNIT_M, area: Math.abs(areaOf(p.shape)) * UNIT_M * UNIT_M, roadCls: null });
    pts.push({ x: cx, y: cy });
  });

  const report: LandUseReport = { kinds: [], byKind: {}, distinct: 0 };
  if (!items.length) return report;

  // 城市中心与半径（用街区质心的平均，别用 model.center —— 那是随种子漂移的参考点）
  let ox = 0;
  let oy = 0;
  for (const it of items) {
    ox += it.cx;
    oy += it.cy;
  }
  ox /= items.length;
  oy /= items.length;
  let radius = 1;
  for (const it of items) radius = Math.max(radius, Math.hypot(it.cx - ox, it.cy - oy));
  radius *= 0.92; // 最外圈的街区也能落在 q≈1 附近，不至于全部挤在最后一档

  const seed = model.seed || 1;
  for (const it of items) {
    const q = Math.min(1, Math.hypot(it.cx - ox, it.cy - oy) / radius);
    const band = BANDS.find((b) => q < b.q) ?? BANDS[BANDS.length - 1];
    const r = hash01(`${seed}:${it.i}:kind`);
    let kind = band.kinds[Math.floor(r * band.kinds.length)] ?? 'mixed';

    // 主干道加成：贴主干道的偏商业。用"最近的一条路"的等级近似（真接街判定在 access 里已做）。
    // 这里只看"这条街上有没有更商业的候选"，没有就不动，避免把小街区硬拗成 CBD。
    const near = nearestRoadCls(roadNet, it.cx, it.cy);
    it.roadCls = near;
    if (near === 'arterial' && q > 0.12 && q < 0.72) {
      if (kind === 'residentialMedium' || kind === 'residentialHigh') kind = r > 0.5 ? 'mixed' : 'commercialLow';
      else if (kind === 'residentialRow') kind = 'commercialLow';
    } else if (near === 'local' && (kind === 'commercialHigh' || kind === 'officeLow') && q > 0.4) {
      kind = 'residentialMedium';
    }

    report.kinds.push({ patchIndex: it.i, kind, q, roadCls: near, forced: false });
  }

  // ── 保底：11 类必须都出现
  const count = (): Record<string, number> => {
    const m: Record<string, number> = {};
    for (const k of report.kinds) m[k.kind] = (m[k.kind] ?? 0) + 1;
    return m;
  };
  for (const want of ALL_KINDS) {
    const c = count();
    if ((c[want] ?? 0) > 0) continue;
    // 在"最贴近这个 kind 该在的位置"的街区里挑一个，且它当前的类型是重复最多的
    const band = BANDS.find((b) => b.kinds.includes(want));
    const targetQ = band ? band.q : 0.5;
    const dup = Object.entries(c).sort((a, b) => b[1] - a[1])[0]?.[0];
    const cand = report.kinds
      .filter((k) => k.kind === dup && !k.forced)
      .sort((a, b) => Math.abs(a.q - targetQ) - Math.abs(b.q - targetQ))[0];
    if (!cand) continue;
    cand.kind = want;
    cand.forced = true;
  }

  const byKind: Record<string, number> = {};
  for (const k of report.kinds) byKind[k.kind] = (byKind[k.kind] ?? 0) + 1;
  report.byKind = byKind;
  report.distinct = Object.keys(byKind).length;
  return report;
}

/** 街区附近最近的一条路的等级（米制坐标） */
function nearestRoadCls(net: RoadNet | null, mx: number, my: number): KindRecord['roadCls'] {
  if (!net) return null;
  let best = Infinity;
  let cls: KindRecord['roadCls'] = null;
  for (const e of net.edges) {
    const ax = e.a.x * UNIT_M;
    const ay = e.a.y * UNIT_M;
    const bx = e.b.x * UNIT_M;
    const by = e.b.y * UNIT_M;
    const dx = bx - ax;
    const dy = by - ay;
    const l2 = dx * dx + dy * dy;
    let t = l2 > 1e-9 ? ((mx - ax) * dx + (my - ay) * dy) / l2 : 0;
    t = t < 0 ? 0 : t > 1 ? 1 : t;
    const d = Math.hypot(mx - (ax + dx * t), my - (ay + dy * t));
    if (d < best) {
      best = d;
      cls = e.cls;
    }
  }
  return cls;
}

function areaOf(ring: Pt[] | { x: number; y: number }[]): number {
  let a = 0;
  for (let i = 0; i < ring.length; i++) {
    const p = ring[i];
    const q = ring[(i + 1) % ring.length];
    a += p.x * q.y - q.x * p.y;
  }
  return a / 2;
}
