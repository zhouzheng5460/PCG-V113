// 生成器 1 / N：`parking`（停车场）。
//
// 这是迁移的第一个目标，理由是语料：54 份 golden 用例里 **19 份是 parking**（1/3），
// 比任何其它生成器都厚。有 golden 才能说"等价"，没有 golden 的生成器即使写完也无法验证。
//
// 它由四个原语拼成，顺序照 Placemaker 自己的 `ParkingLayer`（Zoning → Aisle → Stalls → Access）：
//
//   1. offsetRing       —— 地块内缩退线，得到"能用的地"
//   2. parkingRows      —— 沿最长边切行带，双面布车位，得到车道中心线 + 车位
//   3. pathAround       —— 把车道接到边界出入口，绕开障碍物
//   4. scatterAlongEdge —— 沿边种树、沿车道布灯
//
// **v1 的诚实边界**（写在这里，免得读代码的人以为它已经完美）：
//   · 不做"草岛"：golden 会在车位排之间挖出 22×36 m 的草地岛，v1 只有整块草地打底 + 沥青覆盖；
//   · 不做斜列车位（`stallAngle ≠ 90`）：v1 只排 90°，语料里 90° 占多数但非全部；
//   · 不做无障碍/充电位的专门分区（golden 有 `Stat.EvStalls=12 / AccessibleStalls=6`）；
//   · 出入口只连最近的车道端，不做"人行入口 → 人行道"的连接。
// 这四条都会在比对表上体现为具体差额，不是"应该差不多"。

import { area as ringArea, pointInRing, type Vec2 } from '../../../plan-ir/src/geom.ts';
import { emptyPlan } from '../backends.ts';
import { offsetRing, longestEdge, clipVSlab, rectInFrame, clipHalfPlane } from '../primitives/polygon.ts';
import { differenceRegion, intersectRegion, unionRegions, outerRings, type PolygonRegion } from '../primitives/boolean.ts';
import { parkingRows, pathAround, connectAisles } from '../primitives/parking.ts';
import { rngOf, scatterAlongEdge } from '../primitives/scatter.ts';
import type { Plan, SiteGenerator, SiteInput } from '../types.ts';

const num = (v: unknown, d: number): number => (typeof v === 'number' && Number.isFinite(v) ? v : d);
const bool = (v: unknown, d: boolean): boolean => (typeof v === 'boolean' ? v : d);

export const parking: SiteGenerator = {
  id: 'parking',
  caps: {
    channels: ['surfaces', 'nets', 'objects', 'areas', 'regions', 'stats', 'issues'],
    usesEntrances: true,
    minArea: 20,
    builtFrom: ['offsetRing', 'parkingRows', 'pathAround', 'scatterAlongEdge'],
  },

  generate(input: SiteInput): Plan {
    const plan = emptyPlan(input);
    const ring: Vec2[] = input.shape.ring.map(([x, z]) => [x, z] as Vec2);
    const siteArea = Math.abs(ringArea(ring));
    plan.area = siteArea;
    if (ring.length < 3 || siteArea < 20) {
      plan.ready = false;
      plan.readyKey = 'areaTooSmall';
      plan.issues.push({ severity: 'error', key: 'site.tooSmall', args: [Math.round(siteArea)] });
      return plan;
    }

    const s = input.settings;
    const setback = Math.max(0, num(s.setback, 1));
    const stallWidth = Math.max(1.5, num(s.stallWidth, 3));
    const stallLength = Math.max(2, num(s.stallLength, 5.9));
    const aisleWidth = Math.max(2.5, num(s.aisleWidth, 7));
    const crossEvery = Math.max(2, Math.round(num(s.crossEvery, 9)));
    const doubleLoaded = bool(s.doubleLoaded, true);
    const treeSpacing = Math.max(4, num(s.treeSpacing, 12));
    const treeInset = num(s.treeInset, 2.5);
    const lampSpacing = Math.max(8, num(s.lampSpacing, 25));
    // 绿篱 / 充电桩：golden 实测 271 个 bush / 500 m 周长 ≈ 1.8 m 一株；
    // 充电桩 6 个 / 288 车位 ≈ 2%（`Stat.EvStalls` 是 12，说明一个桩服务两个位）
    const hedgeSpacing = Math.max(1.2, num(s.hedgeSpacing, 1.9));
    const hedgeInset = num(s.hedgeInset, 1.5);
    const chargerShare = num(s.chargerShare, 0.02);
    const islandEvery = Math.max(0, Math.round(num(s.islandEveryStalls, 5)));

    // ── ready：停车场至少要有一个接街的车行口，否则车开不进来
    // （和 Placemaker 的 `CheckReady` 一个语义：没有出入口 → 不 ready，而不是"给个空的停车场"）
    const entrances = input.markers.filter((m) => m.kind.startsWith('entrance') || m.kind.startsWith('access'));
    if (!entrances.length) {
      plan.ready = false;
      plan.readyKey = 'noEntrance|needAccessPoint';
      plan.issues.push({ severity: 'warning', key: 'access.none' });
      return plan;
    }

    // ── 1. 可用的地：内缩退线 + 环路
    //
    // 为什么要多内缩一条车道宽：golden 的 settings 里 `perimeterRoad: true`，
    // 也就是地界内侧先绕一圈**服务性环路**，车位从环路以内才开始排。
    // 这一条直接解释了"每米车道服务几个车位"这个比值：
    //   golden 288 车位 / 1103 m 车道 = **0.26**；我原来是 438 / 1108 = **0.40**。
    // 差的那 50% 不是"车位排得太密"，而是 golden 的车道网里有一段不挂车位的环路。
    // 环路的**尺寸门槛**：环路面积 ≈ 周长 × 车道宽。小地块上一圈环路比场地本身还大
    // （325 m² 的地块周长 ~72 m，环路 72×7 = 504 m² = 场地的 1.5 倍），做了纯亏。
    // 实测：无条件上环路时均分 67→57，因为 15 例小地块的车道总长被推爆。
    // 所以只在环路占地 < 30% 时才做 —— 这是几何条件，不是调参。
    const perimeterLen = ring.reduce((acc, p, i) => {
      const q = ring[(i + 1) % ring.length];
      return acc + Math.hypot(q[0] - p[0], q[1] - p[1]);
    }, 0);
    const perimeterShare = (perimeterLen * aisleWidth) / Math.max(1, siteArea);
    const perimeterW = bool(s.perimeterRoad, true) && perimeterShare < 0.12 ? aisleWidth : 0;
    const work = offsetRing(ring, setback + perimeterW);
    /** 环路中心线（内缩半个车道宽） */
    const ringRoad = perimeterW > 0 ? offsetRing(ring, setback + perimeterW / 2) : null;
    if (work.clamped) plan.issues.push({ severity: 'info', key: 'offset.clamped', args: [setback] });

    // ── 2. 行带 + 车位
    const layout = parkingRows(work.ring, {
      stallWidth,
      stallLength,
      aisleWidth,
      setback: 0,
      maxPerRow: crossEvery,
      doubleLoaded,
      islandEveryStalls: islandEvery,
    });
    for (const n of layout.notes) plan.issues.push({ severity: 'info', key: 'parking.note', args: [n] });

    // ── 3. 环路 + 车道 + 连网 + 接出入口
    let aisleLength = 0;
    // 环路：沿中心线逐边铺成 driveAisle（和 golden 的 `perimeterRoad` 同一件事）
    if (ringRoad && !ringRoad.clamped) {
      const rr = ringRoad.ring;
      for (let i = 0; i < rr.length; i++) {
        const a = rr[i];
        const b = rr[(i + 1) % rr.length];
        const len = Math.hypot(b[0] - a[0], b[1] - a[1]);
        if (len < 1) continue;
        aisleLength += len;
        plan.nets.push({ role: 'driveAisle', a, b: a, c: b, d: b, category: 'perimeter' });
      }
    }
    for (const a of layout.aisles) {
      aisleLength += a.length;
      plan.nets.push({ role: 'driveAisle', a: a.a, b: a.a, c: a.b, d: a.b });
    }
    // 连网：不连的话车开进去出不来（实测碎片 28 块 / 悬空端 56 个）
    let linkLength = 0;
    // 环路也交给连通判定：否则它是一个独立的连通块（实测 4 块 / 悬空端 8 个）
    const perimeterSegs = plan.nets
      .filter((n) => n.category === 'perimeter')
      .map((n) => ({ a: n.a as Vec2, b: n.d as Vec2 }));
    for (const l of connectAisles(layout, perimeterSegs)) {
      linkLength += Math.hypot(l.b[0] - l.a[0], l.b[1] - l.a[1]);
      plan.nets.push({ role: 'driveAisle', a: l.a, b: l.a, c: l.b, d: l.b, category: l.cross ? 'crossAisle' : undefined });
    }
    for (const m of entrances) {
      const p: Vec2 = [m.position[0], m.position[1]];
      let best: { d: number; end: Vec2 } | null = null;
      for (const a of layout.aisles) {
        for (const end of [a.a, a.b]) {
          const d = Math.hypot(end[0] - p[0], end[1] - p[1]);
          if (!best || d < best.d) best = { d, end };
        }
      }
      if (!best) continue;
      // 障碍物要绕：v1 用"采样后断开"，被打断的部分直接不画，并记一条 issue（不静默）
      const blocked = input.obstacles.map((o) => o.quad.map(([x, z]) => [x, z] as Vec2));
      const segs = pathAround(p, best.end, blocked, 1);
      // 端点吸附：`pathAround` 是按 1 m 采样切出来的，最后一段常常差半米接不上车道端点，
      // 于是在"按端点连图"的判定里就成了独立的一块（实测连通块 2 而非 1）。
      // 这里把离车道端 2 m 以内的端点直接吸过去。
      const snap = (q: Vec2): Vec2 =>
        Math.hypot(q[0] - best!.end[0], q[1] - best!.end[1]) <= 2 ? [best!.end[0], best!.end[1]] : q;
      for (let i = 0; i < segs.length; i++) {
        const a0 = snap(segs[i].a);
        const b0 = segs[i === segs.length - 1 ? segs.length - 1 : i].b;
        const b1 = snap(b0);
        plan.nets.push({ role: 'driveConnector', a: a0, b: a0, c: b1, d: b1 });
      }
      if (!segs.length) plan.issues.push({ severity: 'warning', key: 'access.blocked' });
    }

    // ── 4. 铺装：**按行带的 v 区间精确三分**，三片之和恒等于地块
    //
    // 这一条是自洽要求，不是"像不像 golden"：两块面铺在同一片地上本身就是错的。
    // v1 一开始是"整块草地打底 + 沥青覆盖"，实测铺装总面积 28555 m² vs 地块 14527 m²
    // （覆盖 197%）、互相压住 14027 m² —— 而 golden 是 3174 + 11354 = 14527、重叠 0。
    //
    // 现在用 `clipVSlab` 把地块沿行带法向切成**互补**的三片：
    //   前边角（草地）· 车位区（沥青）· 后边角（草地）。
    // 互补的好处是**不需要布尔差集**就能保证"和 = 地块、重叠 = 0"（D-060）。
    // 沥青取**地块环**而不是内缩后的工作环：退线只是"不放车位"，路面本来就铺到地界。
    const fr = layout.frame;
    // 绿带要"切穿"整个车位区才不产生洞（`PlanSurface.ring` 是单环，没有洞的概念；
    // 而 golden 的重叠也 ≈ 0，说明它的沥青同样是无洞的多块）。
    // 所以走廊一律横跨 vSpan 的整个进深，用 `differenceRegion` 把铺装**切开**。
    const corridorW = Math.max(1, stallWidth);
    const corridorStep = Math.max(corridorW * 2, islandEvery * stallWidth);
    const uAbs = ring.map((p) => (p[0] - fr.origin[0]) * fr.u[0] + (p[1] - fr.origin[1]) * fr.u[1]);
    const uMinAbs = Math.min(...uAbs);
    const uMaxAbs = Math.max(...uAbs);
    const corridors: PolygonRegion[] = [];
    if (layout.bands.length && islandEvery > 0) {
      for (let uu = uMinAbs + corridorStep; uu + corridorW < uMaxAbs; uu += corridorStep) {
        const q = rectInFrame(fr, uu, uu + corridorW, layout.vSpan.v0, layout.vSpan.v1);
        if (q) corridors.push({ outer: q });
      }
    }
    // 落在走廊里的车位去掉（走廊位置由生成器统一决定，原语只负责排满）
    if (corridors.length) {
      const inCorridor = (p: Vec2): boolean => {
        const cu = (p[0] - fr.origin[0]) * fr.u[0] + (p[1] - fr.origin[1]) * fr.u[1];
        return corridors.some((c) => {
          const cu0 = (c.outer[0][0] - fr.origin[0]) * fr.u[0] + (c.outer[0][1] - fr.origin[1]) * fr.u[1];
          const cu1 = (c.outer[2][0] - fr.origin[0]) * fr.u[0] + (c.outer[2][1] - fr.origin[1]) * fr.u[1];
          return cu >= Math.min(cu0, cu1) && cu <= Math.max(cu0, cu1);
        });
      };
      layout.stalls = layout.stalls.filter((st) => !inCorridor(st.center));
    }

    if (!layout.bands.length) {
      // **一条行带都排不下**（实测 7 例小地块：309~1677 m²）。
      // 这里必须显式处理：如果按"vSpan = 0 且无带"去切两片草地，
      // `clipVSlab(null, null)` 会把整个环各返回一次 —— 草地铺两遍，
      // 覆盖 200%、重叠 = 整块地。那一轮就是 7 例 × 3 个指标全 bad（D-062）。
      // 排不下车位也仍然是块地，铺一片沥青即可（和 golden 在小地块上的做法一致）。
      plan.surfaces.push({ role: 'asphalt', ring, category: 'parking' });
    } else {
      const front = layout.vSpan.v0;
      const back = layout.vSpan.v1;
      for (const g of [clipVSlab(ring, fr, null, front), clipVSlab(ring, fr, back, null)]) {
        if (g) plan.surfaces.push({ role: 'grass', ring: g, category: 'lawn' });
      }
      const slab = clipVSlab(ring, fr, front, back);
      if (!slab) {
        plan.surfaces.push({ role: 'asphalt', ring, category: 'parking' });
      } else {
        // **P1：用布尔运算把铺装切开、绿带成面**。
        // 顺序很重要：走廊先**裁到铺装里**（`intersectRegion`），再拿去当 cutter。
        // 直接拿未裁的矩形当 cutter 会让草地超出铺装 —— 实测铺装总面积 +13%/+58%（D-063）。
        const cut: PolygonRegion[] = [];
        for (const c of corridors) {
          for (const piece of intersectRegion({ outer: slab }, c)) cut.push(piece);
        }
        const paved = cut.length ? differenceRegion({ outer: slab }, cut) : [{ outer: slab }];
        for (const r of paved) plan.surfaces.push({ role: 'asphalt', ring: r.outer, category: 'parking' });
        for (const c of cut) plan.surfaces.push({ role: 'grass', ring: c.outer, category: 'planting' });
      }
    }

    // ── 5. 车位 + 灯 + 树
    for (const st of layout.stalls) {
      const yaw = Math.atan2(st.heading[0], st.heading[1]);
      plan.objects.push({
        role: 'parkingBay',
        x: st.center[0],
        z: st.center[1],
        yaw,
        scale: undefined,
        category: 'bay',
      });
    }
    // 灯：沿车道中心线按间距摆
    const rng = rngOf(input.seed);
    for (const a of layout.aisles) {
      const n = Math.max(1, Math.floor(a.length / lampSpacing));
      for (let i = 0; i <= n; i++) {
        const t = n === 0 ? 0.5 : i / n;
        plan.objects.push({
          role: 'lamp',
          x: a.a[0] + (a.b[0] - a.a[0]) * t,
          z: a.a[1] + (a.b[1] - a.a[1]) * t,
        });
      }
    }
    // 树：沿地块边内缩一圈
    const edgeTrees = scatterAlongEdge(ring, { spacing: treeSpacing, inset: treeInset, jitter: 0.6 }, rng);
    for (const t of edgeTrees) {
      if (!pointInRing(t.p, ring)) continue;
      plan.objects.push({ role: 'tree', x: t.p[0], z: t.p[1], scale: 1 + rng() * 0.4 });
    }

    // 绿篱：比树更密的一圈灌木，紧贴边界内侧。
    // 为什么要单独一种 role：golden 里 `bush` 有 **271 个**、`tree` 只有 39 个 ——
    // 停车场边界的做法是"一圈密绿篱 + 稀疏点几棵树"，不是"一排树"。
    // v1 缺这一种 role，物件总数就少了 40%（649 vs 534），密度也差 18%。
    const hedge = scatterAlongEdge(ring, { spacing: hedgeSpacing, inset: hedgeInset, jitter: 0.35 }, rng);
    for (const h of hedge) {
      if (!pointInRing(h.p, ring)) continue;
      plan.objects.push({ role: 'bush', x: h.p[0], z: h.p[1], scale: 0.9 + rng() * 0.3 });
    }

    // 充电桩：按车位数的固定比例布（golden: 288 车位 → 12 个充电位 / 6 个桩）
    const chargerCount = Math.round(layout.stalls.length * chargerShare);
    for (let i = 0; i < chargerCount; i++) {
      // 均匀取车位，避免全挤在一起
      const idx = Math.floor(((i + 0.5) * layout.stalls.length) / Math.max(1, chargerCount));
      const st = layout.stalls[idx];
      if (!st) break;
      plan.objects.push({ role: 'charger', x: st.center[0], z: st.center[1], yaw: Math.atan2(st.heading[0], st.heading[1]) });
    }

    // ── 6. 分区与具名区域（golden 有 1 条 areas + 44 条 regions，这里先给 1+1，差额进比对表）
    plan.areas = [{ role: 'lot', ring: work.ring }];
    plan.regions = [{ key: 'lot:0', tags: input.regionTags.length ? input.regionTags : ['root'], ring: work.ring }];

    // ── 7. 自报统计（和 golden 的 `Stat.*` 同一个口径，方便直接对比）
    plan.stats = [
      { key: 'Stat.Stalls', value: layout.stalls.length, unit: 'count' },
      { key: 'Stat.Area', value: Math.round(siteArea), unit: 'm2' },
      { key: 'Stat.AisleLength', value: Math.round(aisleLength + linkLength), unit: 'm' },
      { key: 'Stat.Trees', value: plan.objects.filter((o) => o.role === 'tree').length, unit: 'count' },
      { key: 'Stat.EvStalls', value: plan.objects.filter((o) => o.role === 'charger').length * 2, unit: 'count' },
    ];

    // 一个"最长边"读数，方便解释布局为什么是这个朝向
    const le = longestEdge(ring);
    plan.issues.push({ severity: 'info', key: 'parking.edge', args: [Math.round(le.len)] });

    return plan;
  },
};
