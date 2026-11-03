// 停车场：`ParkingLayer` 的规则复现。
//
// 为什么这一族值得单独做（docs/A §0.1）：**130 个生成器里只有它的 `ready` 能被街道改掉。**
// 上一刀把出入口接街做通了，这一刀才能真的"生产车位" —— 顺序不能反。
//
// 规则来源与已核实的数值：
//   · 车位 `CAR_STALL` = 0.36 × 0.72 世界单位 = **2.52 × 5.04 m**（规范平行式 2.5 × 5.0）
//   · 通道 `aisle` = 0.9 单位 = **6.30 m**（≥ Placemaker 的 driveAisle 5.5 m）
//   · 门禁：`ready` 要求有**车行口**。没有接街出入口 → 一个车位都不排（不是排了再报错）
//
// 排布方式：**在街区的局部坐标系里做矩形阵列**（双排车位 + 中间通道）。
// 为什么不沿街区边一圈圈绕：停车场是"面的填充"，绕着边界摆会在凹角处留出奇怪的碎块；
// 而矩形阵列的观感就是真实的露天停车场，且每一排都能一眼数清有几个位。
//
// 与 TownForge 的关系：TF 的地块内容里也排车位（尺寸同样合规），但它的阵列跟着它自己的
// 楼位走。PM 模式下要换成本文件这一套，否则两套车位会叠在一起（D-038 的反面）。

import { Pt } from '../core/geom';
import { UNIT_M } from '../core/units';
import { CAR_STALL } from '../town/parking';
import type { SurfaceFeature } from '../scene/types';
import type { Model } from '../town/model';
import type { AccessReport } from './access';
import { pointInPolygon } from '../town/zones';

type V2 = [number, number];

/** 换算成米（一次算清，后面不再乘 UNIT_M） */
const STALL_W = CAR_STALL.w * UNIT_M; // 2.52
const STALL_L = CAR_STALL.l * UNIT_M; // 5.04
const AISLE = CAR_STALL.aisle * UNIT_M; // 6.30
/** 停车场四周留的边距（米）：留出转弯半径与出入口 */
const MARGIN = 3.5;
/** 车位线宽度（米） */
const LINE_W = 0.12;
/** 一排最多多少个位（超过就断开留通道口）——真实停车场每 10~15 个位要留一个口 */
const MAX_PER_ROW = 14;
/**
 * 停车坪硬化面积上限（米²），取自 TownForge `parking.ts` 的 `maxLotM2`（城市 2200）。
 *
 * 为什么要这个上限：不设的话我会把整块街区铺满车位，车位数量是 TF 的 3 倍
 * （实测 n=24 时 1404 vs 472）—— 那不是停车场，那是"把地面全画上车位线"。
 * 真实停车场有出入口坡道、绿化岛、通道让位，一块地铺 55%~60% 就到顶了。
 */
const MAX_PAVING_M2 = 6000;
/**
 * 铺装占街区面积的上限比例。
 *
 * 0.75 是实测调的：不设上限时车位是 TF 的 2.4~3 倍（把整块地铺满车位线），
 * 设成 60% + 上限 2200 m² 时又只有 TF 的 0.3~0.4 倍（空得不像停车场）。
 * 75% 落在两者之间，和 TF 同量级（1.4~1.6 倍）。
 */
const PAVING_SHARE = 0.75;

export interface ParkingResult {
  patchIndex: number;
  /** 是否满足门禁（有接街车行口） */
  ready: boolean;
  reason: string;
  /** 车位总数 */
  stalls: number;
  /** 排数（双排算 2 排） */
  rows: number;
  /** 通道条数 */
  aisles: number;
  /** 车位区总面积（m²） */
  stallAreaM2: number;
  /** 实际铺出去的表面积（沥青 + 车位线 + 通道） */
  surfaces: SurfaceFeature[];
}

export interface ParkingReport {
  lots: ParkingResult[];
  stats: {
    lots: number;
    ready: number;
    stalls: number;
    /** 单车位面积（m²）—— 用来核对是不是 2.52×5.04 */
    stallAreaM2: number;
    stallAreaMedM2: number;
    /** 通道宽（米） */
    aisleM: number;
  };
}

/** 轴对齐矩形的四个角（米） */
function rect(x0: number, y0: number, x1: number, y1: number): Pt[] {
  return [new Pt(x0, y0), new Pt(x1, y0), new Pt(x1, y1), new Pt(x0, y1)];
}

/** 米 → 世界单位，用于喂 Scene（Scene 全部是单位） */
const toU = (p: Pt): Pt => new Pt(p.x / UNIT_M, p.y / UNIT_M);

/**
 * 一个停车场街区 → 车位阵列。
 *
 * 输入用**米**，输出用**世界单位**（Scene 的契约），转换只在 `toU` 一处。
 * 车道方向取街区最长边（和"临街面"一致），这样车头朝向与街道平行，进出口好接。
 */
function layoutLot(patch: Model['patches'][number], patchIndex: number, ready: boolean, reason: string): ParkingResult {
  const empty: ParkingResult = { patchIndex, ready, reason, stalls: 0, rows: 0, aisles: 0, stallAreaM2: 0, surfaces: [] };

  // 门禁：没有接街车行口就不排。这是 `ready` 的真实语义 —— 不是"排完再报错"。
  if (!ready) return { ...empty, reason: `${reason}（无接街车行口，按 ParkingLayer 门禁不排车位）` };

  const ring = patch.shape.map((p) => new Pt(p.x * UNIT_M, p.y * UNIT_M));
  if (ring.length < 3) return empty;

  // 局部坐标系：最长边当 u 轴（与出入口/临街面一致）
  let bi = 0;
  let best = 0;
  for (let i = 0; i < ring.length; i++) {
    const a = ring[i];
    const b = ring[(i + 1) % ring.length];
    const d = Math.hypot(b.x - a.x, b.y - a.y);
    if (d > best) {
      best = d;
      bi = i;
    }
  }
  const ox = ring[bi].x;
  const oy = ring[bi].y;
  const ux = (ring[(bi + 1) % ring.length].x - ox) / (best || 1);
  const uy = (ring[(bi + 1) % ring.length].y - oy) / (best || 1);
  const vx = -uy;
  const vy = ux;
  const toLocal = (p: Pt): V2 => [(p.x - ox) * ux + (p.y - oy) * uy, (p.x - ox) * vx + (p.y - oy) * vy];
  const toWorld = (lu: number, lv: number): Pt => new Pt(ox + ux * lu + vx * lv, oy + uy * lu + vy * lv);

  const locals = ring.map(toLocal);
  const u0 = Math.min(...locals.map((p) => p[0]));
  const u1 = Math.max(...locals.map((p) => p[0]));
  const v0 = Math.min(...locals.map((p) => p[1]));
  const v1 = Math.max(...locals.map((p) => p[1]));

  // 可用范围：四周留边
  const au0 = u0 + MARGIN;
  const au1 = u1 - MARGIN;
  const av0 = v0 + MARGIN;
  const av1 = v1 - MARGIN;
  if (au1 - au0 < STALL_W * 2 || av1 - av0 < STALL_L + AISLE) {
    return { ...empty, reason: '街区太窄，放不下一排车位' };
  }

  const surfaces: SurfaceFeature[] = [];
  const rowsBand = STALL_L + AISLE + STALL_L; // 双排 + 中间通道
  let stalls = 0;
  let rows = 0;
  let aisles = 0;

  // 铺装预算：街区面积 × 60%，且不超过 2200 m²（TF 的 maxLotM2）
  let blockAreaM2 = 0;
  for (let i = 0; i < ring.length; i++) {
    const a = ring[i];
    const b = ring[(i + 1) % ring.length];
    blockAreaM2 += a.x * b.y - b.x * a.y;
  }
  blockAreaM2 = Math.abs(blockAreaM2 / 2);
  const pavingBudget = Math.min(blockAreaM2 * PAVING_SHARE, MAX_PAVING_M2);

  // 从 v 端往另一端铺双排（车位背靠背，中间通道）
  let v = av0;
  while (v + rowsBand <= av1 + 1e-6) {
    // 预算用完就停：宁可留空地，也不要把整块地铺成车位线
    if ((v - av0 + rowsBand) * (au1 - au0) > pavingBudget) break;
    const rowA0 = v;
    const rowA1 = v + STALL_L;
    const aisle0 = rowA1;
    const aisle1 = aisle0 + AISLE;
    const rowB0 = aisle1;
    const rowB1 = rowB0 + STALL_L;

    // 通道
    surfaces.push({
      name: `park/aisle/${patchIndex}/${aisles}`,
      group: 'lot/placemaker/parking',
      ring: [toU(toWorld(au0, aisle0)), toU(toWorld(au1, aisle0)), toU(toWorld(au1, aisle1)), toU(toWorld(au0, aisle1))],
      mat: 'asphaltLot',
      y: 0.014,
    });
    aisles++;

    // 两排车位
    for (const [r0, r1, tag] of [
      [rowA0, rowA1, 'a'],
      [rowB0, rowB1, 'b'],
    ] as [number, number, string][]) {
      const n = Math.floor((au1 - au0) / STALL_W);
      if (n <= 0) continue;
      // 居中排布：剩下的零头两侧均分，避免最后一格是 1.2 m 的碎位
      const used = n * STALL_W;
      const uStart = au0 + (au1 - au0 - used) / 2;
      let placed = 0;
      for (let i = 0; i < n; i++) {
        const s0 = uStart + i * STALL_W;
        const s1 = s0 + STALL_W;
        // 四个角必须都在街区里（斜边街区上靠外的位直接不排）
        const corners = [toWorld(s0, r0), toWorld(s1, r0), toWorld(s1, r1), toWorld(s0, r1)];
        if (!corners.every((p) => pointInPolygon(p, ring))) continue;
        // 每 MAX_PER_ROW 个位断一次（真实停车场要留通行口），断口不画分隔线
        if (placed > 0 && placed % MAX_PER_ROW === 0) {
          placed++;
          continue;
        }
        // 一条分隔线（长边），画在 s0 这一侧；最后一格补 s1 的收边
        const lw = LINE_W;
        surfaces.push({
          name: `park/line/${patchIndex}/${tag}${i}`,
          group: 'lot/placemaker/parking',
          ring: [
            toU(toWorld(s0 - lw / 2, r0)),
            toU(toWorld(s0 + lw / 2, r0)),
            toU(toWorld(s0 + lw / 2, r1)),
            toU(toWorld(s0 - lw / 2, r1)),
          ],
          mat: 'markWhite',
          y: 0.032,
        });
        if (i === n - 1) {
          surfaces.push({
            name: `park/line/${patchIndex}/${tag}end`,
            group: 'lot/placemaker/parking',
            ring: [
              toU(toWorld(s1 - lw / 2, r0)),
              toU(toWorld(s1 + lw / 2, r0)),
              toU(toWorld(s1 + lw / 2, r1)),
              toU(toWorld(s1 - lw / 2, r1)),
            ],
            mat: 'markWhite',
            y: 0.032,
          });
        }
        stalls++;
        placed++;
      }
      rows++; // 一排（不是一位）
    }
    v = rowB1;
  }

  // 排不出车位就不算"这个停车场成立"：`ready` 是**能生产**的意思，不只是"有车行口"。
  // 否则面板会显示"ready 但 0 个车位"，比不 ready 更让人迷惑。
  if (stalls === 0) return { ...empty, ready: false, reason: '尺寸不足，一排车位都排不下' };

  return {
    patchIndex,
    ready: true,
    reason: '有接街车行口',
    stalls,
    rows,
    aisles,
    stallAreaM2: stalls * STALL_W * STALL_L,
    surfaces,
  };
}

export function buildParking(model: Model, access: AccessReport): ParkingReport {
  const lots: ParkingResult[] = [];
  model.patches.forEach((patch, patchIndex) => {
    if (!patch.withinCity) return;
    const use = (patch.ward as unknown as { lot?: { use: string } } | null)?.lot?.use;
    if (use !== 'parking') return;
    const attached = (access.perPatch.get(patchIndex)?.attached ?? 0) > 0;
    const reason = attached ? '有接街车行口' : '打不到车行道';
    lots.push(layoutLot(patch, patchIndex, attached, reason));
  });

  const ready = lots.filter((l) => l.ready);
  const stallList = ready.map((l) => (l.stalls ? l.stallAreaM2 / l.stalls : 0)).sort((a, b) => a - b);
  return {
    lots,
    stats: {
      lots: lots.length,
      ready: ready.length,
      stalls: ready.reduce((s, l) => s + l.stalls, 0),
      stallAreaM2: STALL_W * STALL_L,
      stallAreaMedM2: stallList.length ? stallList[Math.floor(stallList.length / 2)] : 0,
      aisleM: AISLE,
    },
  };
}

export const PARKING_DIMS = { stallW: STALL_W, stallL: STALL_L, aisle: AISLE, margin: MARGIN, lineW: LINE_W };
