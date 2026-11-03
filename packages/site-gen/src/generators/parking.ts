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
import { offsetRing, longestEdge, clipHalfPlane } from '../primitives/polygon.ts';
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

    // ── ready：停车场至少要有一个接街的车行口，否则车开不进来
    // （和 Placemaker 的 `CheckReady` 一个语义：没有出入口 → 不 ready，而不是"给个空的停车场"）
    const entrances = input.markers.filter((m) => m.kind.startsWith('entrance') || m.kind.startsWith('access'));
    if (!entrances.length) {
      plan.ready = false;
      plan.readyKey = 'noEntrance|needAccessPoint';
      plan.issues.push({ severity: 'warning', key: 'access.none' });
      return plan;
    }

    // ── 1. 可用的地：内缩退线
    const work = offsetRing(ring, setback);
    if (work.clamped) plan.issues.push({ severity: 'info', key: 'offset.clamped', args: [setback] });

    // ── 2. 行带 + 车位
    const layout = parkingRows(work.ring, {
      stallWidth,
      stallLength,
      aisleWidth,
      setback: 0,
      maxPerRow: crossEvery,
      doubleLoaded,
    });
    for (const n of layout.notes) plan.issues.push({ severity: 'info', key: 'parking.note', args: [n] });

    // ── 3. 车道 + 连网 + 接出入口
    let aisleLength = 0;
    for (const a of layout.aisles) {
      aisleLength += a.length;
      plan.nets.push({ role: 'driveAisle', a: a.a, b: a.a, c: a.b, d: a.b });
    }
    // 连网：不连的话车开进去出不来（实测碎片 28 块 / 悬空端 56 个）
    let linkLength = 0;
    for (const l of connectAisles(layout)) {
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
      for (const sg of segs) plan.nets.push({ role: 'driveConnector', a: sg.a, b: sg.a, c: sg.b, d: sg.b });
      if (!segs.length) plan.issues.push({ severity: 'warning', key: 'access.blocked' });
    }

    // ── 4. 铺装：**沥青 + 两片边角草地**，绝不整块叠铺
    //
    // 这一条是自洽要求，不是"像不像 golden"：两块面铺在同一片地上本身就是错的。
    // v1 一开始就是"整块草地打底 + 沥青覆盖"，实测铺装总面积 28555 m² vs 地块 14527 m²
    // （覆盖 197%）、互相压住 14027 m² —— 而 golden 是 3174 + 11354 = 14527、重叠 0。
    // 这里改成把地块沿行带的纵向范围切成三片：前边角（草地）/ 车位区（沥青）/ 后边角（草地）。
    // 真正的"车位排之间的草岛"还需要布尔差集，见 D-060。
    const fr = layout.frame;
    const at = (v: number): Vec2 => [fr.origin[0] + fr.v[0] * v, fr.origin[1] + fr.v[1] * v];
    /** 取环上 v ∈ [vLo, vHi] 的那一片；传 null 表示该侧不设限 */
    const strip = (vLo: number | null, vHi: number | null): Vec2[] | null => {
      let r: Vec2[] = ring.map((p) => [p[0], p[1]] as Vec2);
      if (vLo !== null) r = clipHalfPlane(r, at(vLo), fr.v);
      if (r.length >= 3 && vHi !== null) r = clipHalfPlane(r, at(vHi), [-fr.v[0], -fr.v[1]]);
      return r.length >= 3 && Math.abs(ringArea(r)) > 0.5 ? r : null;
    };
    const front = layout.bands.length ? layout.vSpan.v0 : null;
    const back = layout.bands.length ? layout.vSpan.v1 : null;
    for (const g of [strip(null, front), strip(back, null)]) {
      if (g) plan.surfaces.push({ role: 'grass', ring: g, category: 'lawn' });
    }
    if (layout.bands.length) plan.surfaces.push({ role: 'asphalt', ring: work.ring, category: 'parking' });

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

    // ── 6. 分区与具名区域（golden 有 1 条 areas + 44 条 regions，这里先给 1+1，差额进比对表）
    plan.areas = [{ role: 'lot', ring: work.ring }];
    plan.regions = [{ key: 'lot:0', tags: input.regionTags.length ? input.regionTags : ['root'], ring: work.ring }];

    // ── 7. 自报统计（和 golden 的 `Stat.*` 同一个口径，方便直接对比）
    plan.stats = [
      { key: 'Stat.Stalls', value: layout.stalls.length, unit: 'count' },
      { key: 'Stat.Area', value: Math.round(siteArea), unit: 'm2' },
      { key: 'Stat.AisleLength', value: Math.round(aisleLength + linkLength), unit: 'm' },
      { key: 'Stat.Trees', value: plan.objects.filter((o) => o.role === 'tree').length, unit: 'count' },
    ];

    // 一个"最长边"读数，方便解释布局为什么是这个朝向
    const le = longestEdge(ring);
    plan.issues.push({ severity: 'info', key: 'parking.edge', args: [Math.round(le.len)] });

    return plan;
  },
};
