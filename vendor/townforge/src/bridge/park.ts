// 公园 / 绿地：`ParkLayer` 的规则复现（**分层接管**，不接管草地底）。
//
// 为什么只接管"路 + 树"，不接管草地：
//   Placemaker 的 park 族**不出草地底**（台账 Q-008，实测 `park` 出 18 条渠 / 172 个物件、
//   一个面都没有）。直接把公园换成 PM 生成就会变成一块空地 —— 这不是"更准确"，是"更难看"。
//   所以分层：**地表仍由 TownForge 铺**（草地 / 铺装），**园路与树由我们排**。
//   等哪天 PM 的草地底补上了（Q-008 关闭），再把地表也接过来。
//
// 规则要点（按 `ParkLayer` / `PlazaLayer` 的行为归纳）：
//   · 公园必须有**环路**：沿边界内退回一圈，宽度 2.4 m（园路标准 2~3 m）
//   · 面积够大（> 3000 m²）时加十字园路 + 中心广场（圆盘，半径 8~12 m）
//   · 树**避开**路面与广场：离路中线 < 2.8 m 不种（树冠会压到路上）
//   · 树按"抖动网格"撒：纯随机会结块，纯网格太机械
//   · `green`（绿地）只种树不做园路 —— 它是"林带"，不是"公园"

import { Pt } from '../core/geom';
import { UNIT_M } from '../core/units';
import { pointInPolygon } from '../town/zones';
import type { PathFeature, PropFeature } from '../scene/types';
import type { Model } from '../town/model';

type V2 = [number, number];

/** 环路内退（米） */
const RING_INSET = 4;
/** 环路宽（米） */
const RING_W = 2.4;
/** 十字园路宽（米） */
const CROSS_W = 1.8;
/** 中心广场半径（米）与触发面积（m²） */
const PLAZA_R = 9;
const PLAZA_MIN_AREA = 3000;
/** 树与路中线的最小距离（米） */
const TREE_CLEAR = 2.8;
/** 抖动网格间距（米）—— 由 treeDensity 换算，这里给上下限 */
const GRID_MIN = 6;
const GRID_MAX = 14;

export interface ParkResult {
  patchIndex: number;
  use: 'park' | 'green';
  /** 园路条数（环 + 十字 + 广场外圈） */
  paths: number;
  trees: number;
  /** 有没有中心广场 */
  plaza: boolean;
  /** 公园面积（m²） */
  areaM2: number;
  /** 园路总长（米） */
  pathLenM: number;
  pathFeatures: PathFeature[];
  propFeatures: PropFeature[];
}

export interface ParkReport {
  parks: ParkResult[];
  stats: {
    parks: number;
    green: number;
    trees: number;
    pathLenM: number;
    /** 树 ÷ 千平方米（校正过密度后应该是同一个量级） */
    treesPer1kM2: number;
  };
}

const toU = (p: Pt): Pt => new Pt(p.x / UNIT_M, p.y / UNIT_M);
const toM = (p: { x: number; y: number }): Pt => new Pt(p.x * UNIT_M, p.y * UNIT_M);

function polyArea(ring: Pt[]): number {
  let a = 0;
  for (let i = 0; i < ring.length; i++) {
    const p = ring[i];
    const q = ring[(i + 1) % ring.length];
    a += p.x * q.y - q.x * p.y;
  }
  return Math.abs(a / 2);
}

/** 点到折线的最短距离（米） */
function distToPolyline(p: Pt, pts: Pt[]): number {
  let best = Infinity;
  for (let i = 0; i < pts.length - 1; i++) {
    const a = pts[i];
    const b = pts[i + 1];
    const dx = b.x - a.x;
    const dy = b.y - a.y;
    const l2 = dx * dx + dy * dy;
    let t = l2 > 1e-12 ? ((p.x - a.x) * dx + (p.y - a.y) * dy) / l2 : 0;
    t = t < 0 ? 0 : t > 1 ? 1 : t;
    best = Math.min(best, Math.hypot(p.x - (a.x + dx * t), p.y - (a.y + dy * t)));
  }
  return best;
}

/** 简单可重复的伪随机（同种子同结果，和项目其它地方一致） */
function hash01(s: string): number {
  let h = 2166136261;
  for (let i = 0; i < s.length; i++) {
    h ^= s.charCodeAt(i);
    h = Math.imul(h, 16777619);
  }
  return ((h >>> 0) % 100000) / 100000;
}

/** 把一个 Pt 环按质心缩放（内退的粗实现：够用，因为公园街区接近凸形） */
function shrinkRing(ring: Pt[], d: number): Pt[] {
  let cx = 0;
  let cy = 0;
  for (const p of ring) {
    cx += p.x;
    cy += p.y;
  }
  cx /= ring.length;
  cy /= ring.length;
  const out: Pt[] = [];
  for (const p of ring) {
    const vx = cx - p.x;
    const vy = cy - p.y;
    const l = Math.hypot(vx, vy) || 1;
    // 内退 d 米：沿"指向质心"的方向走 d（顶点越靠外退得越自然）
    out.push(new Pt(p.x + (vx / l) * d, p.y + (vy / l) * d));
  }
  return out;
}

function layoutPark(patch: Model['patches'][number], patchIndex: number, use: 'park' | 'green', seed: number): ParkResult {
  const ringU = patch.shape.map((p) => new Pt(p.x, p.y));
  const ring = ringU.map(toM);
  const areaM2 = polyArea(ring);
  const pathFeatures: PathFeature[] = [];
  const propFeatures: PropFeature[] = [];

  const lineFeats: Pt[][] = []; // 用来做"树避路"的判定（米制）

  // ── 公园：环路（必须）+ 十字 + 广场（大公园）
  let plaza = false;
  if (use === 'park' && areaM2 > 800) {
    const ringPath = shrinkRing(ring, RING_INSET);
    if (ringPath.length >= 3) {
      const closed = [...ringPath, ringPath[0]];
      pathFeatures.push({
        name: `park/ring/${patchIndex}`,
        group: 'lot/placemaker/park',
        pts: closed.map(toU),
        width: RING_W / UNIT_M,
        mat: 'paver',
        y: 0.02,
      });
      lineFeats.push(closed);
    }

    let cx = 0;
    let cy = 0;
    for (const p of ring) {
      cx += p.x;
      cy += p.y;
    }
    cx /= ring.length;
    cy /= ring.length;

    if (areaM2 > PLAZA_MIN_AREA) {
      // 十字园路：从质心连到环路的两组对角
      const cross = [
        [new Pt(cx - 40, cy), new Pt(cx + 40, cy)],
        [new Pt(cx, cy - 40), new Pt(cx, cy + 40)],
      ];
      for (let k = 0; k < cross.length; k++) {
        pathFeatures.push({
          name: `park/cross/${patchIndex}/${k}`,
          group: 'lot/placemaker/park',
          pts: cross[k].map(toU),
          width: CROSS_W / UNIT_M,
          mat: 'paver',
          y: 0.021,
        });
        lineFeats.push(cross[k]);
      }
      // 中心广场：一个圆盘（用 16 边形近似，Scene 只认环）
      const disc: Pt[] = [];
      for (let i = 0; i < 16; i++) {
        const a = (i / 16) * Math.PI * 2;
        disc.push(new Pt(cx + Math.cos(a) * PLAZA_R, cy + Math.sin(a) * PLAZA_R));
      }
      pathFeatures.push({
        name: `park/plaza/${patchIndex}`,
        group: 'lot/placemaker/park',
        pts: disc.map(toU),
        width: 0.01,
        mat: 'stonePave',
        y: 0.022,
      });
      // 广场外圈也要算进"树不能贴"的集合：只按半径排除时，树会贴在铺装边缘 2.1 m 处
      // （缝隙够站人但树冠压上去了），测试里 2.8 m 的净距就过不了。
      lineFeats.push([...disc, disc[0]]);
      plaza = true;
    }
  }

  // ── 树：抖动网格，避开路面与广场
  // 密度：公园每 1000 m² 约 22 棵（和 TownForge 的 green.treeDensity 默认值同量级），
  // 绿地（林带）略密一点。
  const per1k = use === 'park' ? 20 : 26;
  const want = Math.max(2, Math.round((areaM2 / 1000) * per1k));
  // 网格间距直接用"每棵树摊到的面积"开方：want 棵要铺满 area，格子就该是 sqrt(area/want)。
  // 之前乘了 1.6 想给抖动留余量，结果格子比需要的粗 60%，格子数本身就不够 want
  // —— 症状是"公园越大树越稀"（8900 m² 想种 178 棵，实际只排得下 112 个格）。
  const grid = Math.max(GRID_MIN, Math.min(GRID_MAX, Math.sqrt(areaM2 / want)));
  const treeKinds: PropFeature['kind'][] = ['tree', 'tree', 'treeSmall', 'conifer'];

  let placed = 0;
  let guard = 0;
  let cxAll = 0;
  let cyAll = 0;
  for (const p of ring) {
    cxAll += p.x;
    cyAll += p.y;
  }
  cxAll /= ring.length;
  cyAll /= ring.length;

  // 扫描范围要按面积算，不能写死 ±40 格：大公园会把迭代次数耗光（guard 打满）就提前收工，
  // 结果是"公园越大树越稀"——实测 8900 m² 的公园只种了 69 棵（目标 178）。
  const half = Math.ceil(Math.sqrt(areaM2) / grid) + 2;
  for (let gy = -half; gy <= half && placed < want && guard < 40000; gy++) {
    for (let gx = -half; gx <= half && placed < want && guard < 40000; gx++) {
      guard++;
      const jx = (hash01(`${seed}:${patchIndex}:x${gx}:y${gy}`) - 0.5) * grid * 0.7;
      const jy = (hash01(`${seed}:${patchIndex}:y${gx}:x${gy}`) - 0.5) * grid * 0.7;
      const p = new Pt(cxAll + gx * grid + jx, cyAll + gy * grid + jy);
      if (!pointInPolygon(p, ring)) continue;
      // 避路：离任一条园路中线 < TREE_CLEAR 就不种
      let ok = true;
      for (const line of lineFeats) {
        if (distToPolyline(p, line) < TREE_CLEAR) {
          ok = false;
          break;
        }
      }
      if (!ok) continue;
      // 避广场
      if (plaza && Math.hypot(p.x - cxAll, p.y - cyAll) < PLAZA_R + TREE_CLEAR) continue;
      const r = hash01(`${seed}:${patchIndex}:k${gx}:${gy}`);
      propFeatures.push({
        name: `park/tree/${patchIndex}/${placed}`,
        group: 'lot/placemaker/park',
        kind: treeKinds[Math.floor(r * treeKinds.length)],
        at: toU(p),
        scale: 0.85 + r * 0.5,
        rot: r * Math.PI * 2,
        // 每棵树自己的随机种子：2D/3D/GLB 三处取到同一棵树的同一个形态
        seed: Math.floor(r * 100000),
      });
      placed++;
    }
  }

  let len = 0;
  for (const l of lineFeats) {
    for (let i = 0; i < l.length - 1; i++) len += Math.hypot(l[i + 1].x - l[i].x, l[i + 1].y - l[i].y);
  }

  return {
    patchIndex,
    use,
    paths: pathFeatures.length,
    trees: propFeatures.length,
    plaza,
    areaM2,
    pathLenM: len,
    pathFeatures,
    propFeatures,
  };
}

export function buildPark(model: Model): ParkReport {
  const parks: ParkResult[] = [];
  let pathLenM = 0;
  model.patches.forEach((patch, patchIndex) => {
    if (!patch.withinCity) return;
    const use = (patch.ward as unknown as { lot?: { use: string } } | null)?.lot?.use;
    if (use !== 'park' && use !== 'green') return;
    const r = layoutPark(patch, patchIndex, use, model.seed || 1);
    pathLenM += r.pathLenM;
    parks.push(r);
  });

  const trees = parks.reduce((s, p) => s + p.trees, 0);
  const area = parks.reduce((s, p) => s + p.areaM2, 0);
  return {
    parks,
    stats: {
      parks: parks.filter((p) => p.use === 'park').length,
      green: parks.filter((p) => p.use === 'green').length,
      trees,
      pathLenM,
      treesPer1kM2: area > 0 ? (trees / area) * 1000 : 0,
    },
  };
}

export const PARK_DIMS = { ringInset: RING_INSET, ringW: RING_W, crossW: CROSS_W, plazaR: PLAZA_R, treeClear: TREE_CLEAR };
export type { V2 };
