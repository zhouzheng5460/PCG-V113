// 原语 3/4：车位排布 + 站点连线。
//
// 这两个是"停车场 / 广场 / 集市"这类**有骨架**的场地的核心：
//   骨架 = 一组车道中心线（`nets.role = driveAisle`）+ 接到出入口的连线（`driveConnector`）；
//   填充 = 沿骨架两侧排车位（`objects.role = parkingBay`）。
//
// 为什么车位必须挂在骨架上而不是"往面里撒"：
// 车位朝向必须垂直于车道、车位线必须落在铺装内、一辆车要能开出去。这三条都要求
// "先有骨架、再有车位"。Placemaker 自己的 `ParkingLayer` 也是这个顺序（Zoning → Aisle → Stalls），
// 所以这个顺序不是我编的。

import { pointInRing, type Vec2 } from '../../../plan-ir/src/geom.ts';
import { longestEdge, ccwRing } from './polygon.ts';

export interface ParkingRowOptions {
  /** 车位宽（沿车道方向，米） */
  stallWidth: number;
  /** 车位长（垂直车道方向，米） */
  stallLength: number;
  /** 车道净宽（米） */
  aisleWidth: number;
  /** 地块退线（米）—— 行带离边界至少留这么多 */
  setback: number;
  /** 一条车道最多服务几个车位，达到就断开（留横向通道） */
  maxPerRow?: number;
  /** 是否双侧布车位（false = 单侧） */
  doubleLoaded?: boolean;
  /** 行带允许的最小长度（米）—— 短于此不排，避免零碎 */
  minRun?: number;
}

export interface Stall {
  center: Vec2;
  /** 车头朝向（指向车道） */
  heading: Vec2;
  width: number;
  length: number;
  quad: [Vec2, Vec2, Vec2, Vec2];
}

export interface Aisle {
  a: Vec2;
  b: Vec2;
  dir: Vec2;
  normal: Vec2;
  length: number;
  /** 这条车道服务几排车位（1 或 2） */
  sides: number;
}

export interface ParkingBand {
  u0: number;
  u1: number;
  v0: number;
  v1: number;
}

export interface ParkingLayout {
  /** 局部坐标系：origin + (u 沿街, v 朝内) */
  frame: { origin: Vec2; u: Vec2; v: Vec2 };
  aisles: Aisle[];
  stalls: Stall[];
  bands: ParkingBand[];
  /** 布了车位的纵向范围 [v0, v1]（局部坐标）—— 用来算"剩下的草地" */
  vSpan: { v0: number; v1: number };
  /** 实际布了车位的地面面积（m²），用来和地块面积比效率 */
  usedArea: number;
  notes: string[];
}

/**
 * 按行带排车位（双面载荷）。
 *
 * 布局模型：以**最长边**定朝向，沿法向逐条推行带，每条行带 = 车道 + 两侧车位；
 * 行带间距 = `2 × stallLength + aisleWidth`（双面）或 `stallLength + aisleWidth`（单面）。
 *
 * 为什么以最长边定向：golden input 里 `rowAngleMode` 就是 `edge`
 * ——"顺着地块最长的那条边走"。这一条直接决定整个停车场看起来对不对，所以是第一个要对齐的参数。
 *
 * 每一带怎么定宽度：沿 u 每 1 m 采样一次"车道中心点是否在地块内"，得到若干连续段；
 * 段长不足 `minRun` 的直接丢。这样斜边、凹角、L 形都不会被硬塞成整条行带。
 */
export function parkingRows(usable: readonly Vec2[], opts: ParkingRowOptions): ParkingLayout {
  const ring = ccwRing(usable);
  const notes: string[] = [];
  const long = longestEdge(ring);
  // u = 沿最长边，vn = 指向地块内侧（CCW 的左法向）
  const u: Vec2 = long.dir;
  const vn: Vec2 = [-u[1], u[0]];
  const origin: Vec2 = [ring[0][0], ring[0][1]];
  const toLocal = (p: Vec2): Vec2 => [
    (p[0] - origin[0]) * u[0] + (p[1] - origin[1]) * u[1],
    (p[0] - origin[0]) * vn[0] + (p[1] - origin[1]) * vn[1],
  ];
  const toWorld = (uu: number, vv: number): Vec2 => [
    origin[0] + u[0] * uu + vn[0] * vv,
    origin[1] + u[1] * uu + vn[1] * vv,
  ];

  // 局部包围盒：行带的推进会在这上面走
  let uMin = Infinity;
  let uMax = -Infinity;
  let vMin = Infinity;
  let vMax = -Infinity;
  for (const p of ring) {
    const l = toLocal(p);
    if (l[0] < uMin) uMin = l[0];
    if (l[0] > uMax) uMax = l[0];
    if (l[1] < vMin) vMin = l[1];
    if (l[1] > vMax) vMax = l[1];
  }

  const pad = Math.max(0, opts.setback);
  const double = opts.doubleLoaded !== false;
  const stallLen = Math.max(1, opts.stallLength);
  const aisleW = Math.max(1, opts.aisleWidth);
  const stallW = Math.max(0.5, opts.stallWidth);
  const pitch = double ? stallLen * 2 + aisleW : stallLen + aisleW;
  const maxPerRow = opts.maxPerRow ?? 99;
  const minRun = opts.minRun ?? stallW * 2;

  const aisles: Aisle[] = [];
  const stalls: Stall[] = [];
  const bands: ParkingBand[] = [];

  // 第一条车道中心：离地界 pad + stallLen（双面时两侧车位都要放得下）
  let vv = vMin + pad + stallLen;
  let guard = 0;
  while (vv + stallLen + pad <= vMax && guard++ < 400) {
    // 沿 u 采样，找出车道中心线落在地块内的连续段
    const runs: { u0: number; u1: number }[] = [];
    let runStart: number | null = null;
    for (let uu = uMin; uu <= uMax; uu += 1) {
      const inside = pointInRing(toWorld(uu, vv), ring);
      if (inside && runStart === null) runStart = uu;
      if (!inside && runStart !== null) {
        runs.push({ u0: runStart, u1: uu - 1 });
        runStart = null;
      }
    }
    if (runStart !== null) runs.push({ u0: runStart, u1: uMax });

    for (const run of runs) {
      const len = run.u1 - run.u0;
      if (len < minRun) continue;
      // 一条"行"沿 u 铺满；每 `maxPerRow` 个车位插一条**横向通道**（不是"到 N 就换位置"）。
      //
      // 这条是照 golden 实测改的：golden 的 60 条车道中位只有 15.3 m（≈5 个车位），
      // 最长 146.6 m，端点度数分布是 1:2 / 2:20 / 3:18 / 4:7 —— 说明它是**一条长行被
      // 横向通道切成若干段**，而不是"每条车道天生就短"。`crossEvery` 的语义正是
      // "每 N 个车位切一刀"，v1 把它当成"每条车道最多 N 个车位、剩下的不排"，于是
      // 车位数只有一半（131 vs 288）、车道总长只有 1/5（241 vs 1124）。
      const totalStalls = Math.floor(len / stallW);
      if (totalStalls < 1) continue;
      const gapStalls = 1; // 横向通道占 1 个车位宽
      const chunks: { k0: number; k1: number }[] = [];
      let k = 0;
      while (k < totalStalls) {
        const k1 = Math.min(totalStalls, k + maxPerRow);
        chunks.push({ k0: k, k1 });
        k = k1 + gapStalls; // 跳过一条横向通道
      }

      for (const ch of chunks) {
        const nStalls = ch.k1 - ch.k0;
        if (nStalls < 1) continue;
        const u0 = run.u0 + (len - totalStalls * stallW) / 2 + ch.k0 * stallW;
        const u1 = u0 + nStalls * stallW;
        aisles.push({
          a: toWorld(u0, vv),
          b: toWorld(u1, vv),
          dir: u,
          normal: vn,
          length: u1 - u0,
          sides: double ? 2 : 1,
        });
        bands.push({ u0, u1, v0: double ? vv - stallLen : vv, v1: vv + stallLen });

        const signs = double ? [-1, 1] : [1];
        for (let i = 0; i < nStalls; i++) {
          const cu = u0 + (i + 0.5) * stallW;
          for (const sg of signs) {
            const cv = vv + sg * (stallLen / 2);
            const center = toWorld(cu, cv);
            if (!pointInRing(center, ring)) continue;
            const hw = stallW / 2 - 0.02;
            const hl = stallLen / 2 - 0.02;
            const quad: [Vec2, Vec2, Vec2, Vec2] = [
              toWorld(cu - hw, cv - hl),
              toWorld(cu + hw, cv - hl),
              toWorld(cu + hw, cv + hl),
              toWorld(cu - hw, cv + hl),
            ];
            stalls.push({
              center,
              // 车头指向车道（即 sg 的反方向）
              heading: [-u[0] * sg, -u[1] * sg],
              width: stallW,
              length: stallLen,
              quad,
            });
          }
        }
      }
    }
    vv += pitch;
  }

  if (!aisles.length) notes.push('没有任何行带能落在地块内（退线/尺寸不匹配）');
  // 车位行带占用的纵向范围：第一条带的 v0 到最后一条带的 v1
  let spanV0 = Infinity;
  let spanV1 = -Infinity;
  for (const bd of bands) {
    if (bd.v0 < spanV0) spanV0 = bd.v0;
    if (bd.v1 > spanV1) spanV1 = bd.v1;
  }
  if (!bands.length) {
    spanV0 = 0;
    spanV1 = 0;
  }
  return {
    frame: { origin, u, v: vn },
    aisles,
    stalls,
    bands,
    vSpan: { v0: spanV0, v1: spanV1 },
    usedArea: stalls.length * stallW * stallLen,
    notes,
  };
}

/**
 * 把一条直线段打断成若干段，避开障碍物。
 *
 * 用途：车位车道接到出入口时被建筑打断。**被打断的部分直接不画，但要上报**——
 * 悄悄少画一段的结果就是"停车场看起来通不到街上"，而这正是 Placemaker 自己
 * 被诟病的那条（`SiteSafety` 只删不绕）。我们这里把"删了什么"写成 issue，便于对比时看见。
 */
export function pathAround(
  from: Vec2,
  to: Vec2,
  blocked: readonly Vec2[][],
  sampleStep = 1,
): { a: Vec2; b: Vec2 }[] {
  const len = Math.hypot(to[0] - from[0], to[1] - from[1]);
  const n = Math.max(2, Math.ceil(len / sampleStep));
  const segs: { a: Vec2; b: Vec2 }[] = [];
  let start: Vec2 | null = null;
  let prev: Vec2 | null = null;
  for (let i = 0; i <= n; i++) {
    const t = i / n;
    const p: Vec2 = [from[0] + (to[0] - from[0]) * t, from[1] + (to[1] - from[1]) * t];
    const inside = blocked.some((r) => pointInRing(p, r));
    if (!inside) {
      if (start === null) start = p;
      prev = p;
    } else if (start !== null && prev !== null) {
      segs.push({ a: start, b: prev });
      start = null;
      prev = null;
    }
  }
  if (start !== null && prev !== null) segs.push({ a: start, b: prev });
  return segs.filter((s) => Math.hypot(s.b[0] - s.a[0], s.b[1] - s.a[1]) > 1e-3);
}

/**
 * 把一堆互不相连的车道段接成**一张网**。
 *
 * 为什么必须有这一步：实测 v1 的 438 个车位铺得挺满，但路网碎成 **28 块**、
 * 悬空端 56 个、最长连通网只有 33 m —— 而 golden 是 **1 块 / 2 个悬空端 / 1124 m**。
 * 一个停车场如果车道互不相连，车就是停进去开不出来，这在现实中根本不成立。
 *
 * 接法（两层，都只连"本来就该通"的地方）：
 *   1. **同一行内**：一条长行被横向通道切成几段，段与段之间本来就要通 —— 直接连上；
 *   2. **行与行之间**：从最靠前那一行起，每行的第一个段头沿法向接到前一行 —— 形成脊。
 * 这样出来的是一棵树（悬空端只剩叶节点），和 golden 的度数分布（1:2 / 2:20 / 3:18 / 4:7）同构。
 */
export function connectAisles(
  layout: ParkingLayout,
): { a: Vec2; b: Vec2; cross: boolean }[] {
  const fr = layout.frame;
  const toLocal = (p: Vec2): Vec2 => [
    (p[0] - fr.origin[0]) * fr.u[0] + (p[1] - fr.origin[1]) * fr.u[1],
    (p[0] - fr.origin[0]) * fr.v[0] + (p[1] - fr.origin[1]) * fr.v[1],
  ];
  const toWorld = (uu: number, vv: number): Vec2 => [
    fr.origin[0] + fr.u[0] * uu + fr.v[0] * vv,
    fr.origin[1] + fr.u[1] * uu + fr.v[1] * vv,
  ];

  // 按 v（行）分组，行内按 u 排序
  const rows = new Map<number, { u0: number; u1: number }[]>();
  for (const a of layout.aisles) {
    const la = toLocal(a.a);
    const lb = toLocal(a.b);
    const vv = Math.round(((la[1] + lb[1]) / 2) * 100) / 100;
    const arr = rows.get(vv) ?? [];
    arr.push({ u0: Math.min(la[0], lb[0]), u1: Math.max(la[0], lb[0]) });
    rows.set(vv, arr);
  }

  const links: { a: Vec2; b: Vec2; cross: boolean }[] = [];
  const rowKeys = [...rows.keys()].sort((x, y) => x - y);

  // 1) 行内：相邻两段之间补一条横向通道
  for (const vv of rowKeys) {
    const segs = (rows.get(vv) ?? []).slice().sort((p, q) => p.u0 - q.u0);
    for (let i = 1; i < segs.length; i++) {
      const gap = segs[i].u0 - segs[i - 1].u1;
      if (gap <= 0.05) continue;
      links.push({ a: toWorld(segs[i - 1].u1, vv), b: toWorld(segs[i].u0, vv), cross: true });
    }
  }

  // 2) 行间：每行的段头沿法向接到前一行（脊）
  for (let i = 1; i < rowKeys.length; i++) {
    const prev = rowKeys[i - 1];
    const cur = rowKeys[i];
    const prevSegs = (rows.get(prev) ?? []).slice().sort((p, q) => p.u0 - q.u0);
    const curSegs = (rows.get(cur) ?? []).slice().sort((p, q) => p.u0 - q.u0);
    if (!prevSegs.length || !curSegs.length) continue;
    for (const seg of curSegs) {
      // 找一个落在前一行某段范围内的端点来对齐（避免凭空斜穿）
      const uu = seg.u0;
      const host = prevSegs.find((p) => uu >= p.u0 - 0.01 && uu <= p.u1 + 0.01) ?? prevSegs[0];
      const u = Math.min(Math.max(uu, host.u0), host.u1);
      links.push({ a: toWorld(u, prev), b: toWorld(u, cur), cross: true });
    }
  }
  return links;
}
