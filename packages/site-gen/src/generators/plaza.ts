// 生成器 2 / N：`plaza`（公共广场）。
//
// P3.0 侦察结论决定了它的形状（docs/I）：
//   · **Oracle 完全不产 nets**（9/9 例）→ 没有 PathNetwork，circulation 编码在**面**里；
//   · **coverage 由障碍物与形状主导**（规则地块 97~99%、带 4 栋建筑 80.9%、真实例 53.4%）
//     → 门禁不能是"绝对 98~102%"，而是"usable 内的账要平"；
//   · **面片数 ≠ 碎片化**（142 块面但连通块恒为 1）→ 面片数只做观测。
//
// 所以 V1 = 可用区域 → 面分配 → 语义分区 → 装饰。**没有路径网络**。
//
// 几何全部复用原语，本文件只写"策略"：
//   polygon.ts   offsetRing / minAreaBox / clipHalfPlane
//   boolean.ts   unionRegions / differenceRegion / intersectRegion
//   scatter.ts   rngOf / scatterAlongEdge / scatterInside
// 如果这里出现新的 clip/offset/difference 实现，说明原语层没站稳 —— 停下来 review。

import { area as ringArea, centroid, pointInRing, type Vec2 } from '../../../plan-ir/src/geom.ts';
import { emptyPlan } from '../backends.ts';
import { offsetRing, minAreaBox } from '../primitives/polygon.ts';
import { differenceRegion, intersectRegion, unionRegions, regionArea, type PolygonRegion } from '../primitives/boolean.ts';
import { rngOf, scatterAlongEdge, scatterInside, distToRing } from '../primitives/scatter.ts';
import type { Plan, SiteGenerator, SiteInput } from '../types.ts';

const num = (v: unknown, d: number): number => (typeof v === 'number' && Number.isFinite(v) ? v : d);

/** 面 → 区域 */
const asRegion = (ring: Vec2[]): PolygonRegion => ({ outer: ring });

/**
 * 区域 → 面。
 *
 * **不切洞**：布尔差集减掉内部障碍物，数学结果就是一个带洞多边形；
 * 再减一次还是洞，想"切成无洞小块"只能加新的几何算法（切分/三角化）。
 * 按 docs/I §2.1 的三层拓扑，这一步属于**序列化**，不是设计 —— 所以契约直接支持 `holes`，
 * 不为单环去切碎（oracle 那 142 块面其实是同一个连通体）。
 */
function toSurfaceRegion(r: PolygonRegion): { ring: Vec2[]; holes?: Vec2[][] } {
  return r.holes?.length ? { ring: r.outer, holes: r.holes } : { ring: r.outer };
}

/** 点是否落在任一障碍物里 */
function inObstacles(p: Vec2, obs: readonly PolygonRegion[]): boolean {
  return obs.some((o) => pointInRing(p, o.outer));
}

export const plaza: SiteGenerator = {
  id: 'plaza',
  caps: {
    // 没有 nets —— 这是 oracle 的行为，不是我漏了
    channels: ['surfaces', 'objects', 'areas', 'regions', 'stats', 'issues'],
    usesEntrances: true,
    minArea: 20,
    builtFrom: ['offsetRing', 'differenceRegion', 'unionRegions', 'scatterAlongEdge', 'scatterInside'],
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
    const greenM = Math.max(0, num(s.plazaGreenBandM, 1.0));
    const edgeM = Math.max(0, num(s.plazaEdgeBandM, 8));
    const coreInsetM = Math.max(1, num(s.plazaCoreInsetM, 12));
    const treeSpacing = Math.max(4, num(s.plazaTreeSpacingM, 18));
    const lampSpacing = Math.max(8, num(s.plazaLampSpacingM, 34));
    const benchDensity = Math.max(0, num(s.plazaBenchPer1000M2, 0.5));

    // ── 1. 障碍物先 **union**，再一次性 difference（禁止逐栋边减边生成面）
    const obs: PolygonRegion[] = input.obstacles.map((o) => ({
      outer: o.quad.map(([x, z]) => [x, z] as Vec2),
    }));
    const obsU = obs.length ? unionRegions(obs) : [];

    // ── 2. usable = site − union(obstacles)
    const usable = obsU.length ? differenceRegion({ outer: ring }, obsU) : [{ outer: ring }];
    if (!usable.length) {
      plan.ready = false;
      plan.readyKey = 'noUsableArea';
      plan.issues.push({ severity: 'warning', key: 'site.fullyBlocked' });
      return plan;
    }

    const rng = rngOf(input.seed);
    const pavedPieces: PolygonRegion[] = [];
    const greenPieces: PolygonRegion[] = [];
    const corePieces: PolygonRegion[] = [];
    const edgePieces: PolygonRegion[] = [];

    // ── 3. 语义分区：每块可用区域各切成 paved / green（绿地是**贴边窄带**）
    for (const piece of usable) {
      // `offsetRing(ring, dist)` 的约定是「**正数向内**」（parking 也一直用正值）。
      // 写成负数会往外扩，被内部的 grown 判据夹取 —— 第一版就是这样：绿带全是 0。
      // 凹环上仍可能被夹取，所以退一档再试：greenM → 0.6·greenM → 0.8 m，全失败才放弃绿带。
      let inner: ReturnType<typeof offsetRing> | null = null;
      for (const d of [greenM, greenM * 0.6, 0.8].filter((v) => v > 0.05)) {
        const t = offsetRing(piece.outer, d);
        if (!t.clamped && t.ring.length >= 3) {
          inner = t;
          break;
        }
      }
      if (inner && !inner.clamped && inner.ring.length >= 3) {
        // **全用差集，不用交集**。为什么：
        //   `usable` 是"地块减去障碍物"的结果，**带 `holes`**（实测 p90-plaza：外环 13123、8 个洞、净 7505）。
        //   只取 `.outer` 等于把建筑盖回成铺装（覆盖 174.9%、障碍重叠 74.85%）；
        //   而 `intersectRegion(内缩环, 带洞区域)` 对带洞的被减数不可靠（返回无洞的整块）。
        // 差集这条链已经过 26 项布尔测试，且"paved + green = piece"由构造保证：
        //   green = piece − inner，paved = piece − green
        const innerOnly = asRegion(inner.ring);
        const band = differenceRegion(piece, [innerOnly]);
        for (const g of band) greenPieces.push(g);
        const paved = band.length ? differenceRegion(piece, band) : [piece];
        for (const q of paved) pavedPieces.push(q);
      } else {
        // 太窄，铺满；记一条 issue，不静默
        pavedPieces.push(piece);
        plan.issues.push({ severity: 'info', key: 'plaza.noGreenBand', args: [Math.round(regionArea(piece))] });
      }

      // edge 语义带（更宽的一圈，仍属铺装；只用于放沿边设施与 region 标注）
      if (edgeM > greenM) {
        const innerE = offsetRing(piece.outer, edgeM);
        if (innerE && !innerE.clamped) {
          for (const e of differenceRegion(piece, [asRegion(innerE.ring)])) edgePieces.push(e);
        }
      }

      // core：内缩到中心的小面（"centroid + inset"，不做最大内接圆）
      const core = offsetRing(piece.outer, coreInsetM);
      if (core && !core.clamped && core.ring.length >= 3 && Math.abs(ringArea(core.ring)) > 1) {
        corePieces.push(asRegion(core.ring));
      }
    }

    // ── 4. 面：paved 是主面，green 是贴边带
    // 设计拓扑 → 序列化拓扑：带洞区域原样带洞输出（不切碎）
    for (const p of pavedPieces) plan.surfaces.push({ role: 'plaza', ...toSurfaceRegion(p), category: 'surface.paving' });
    for (const g of greenPieces) plan.surfaces.push({ role: 'grass', ...toSurfaceRegion(g), category: 'surface.grass' });

    // ── 5. accessPoint：只要求"落在/贴近铺装连通体"，不生成中心线（oracle 也没有）
    const entrances = input.markers.filter((m) => m.kind.startsWith('entrance') || m.kind.startsWith('access'));
    for (const m of entrances) {
      const p: Vec2 = [m.position[0], m.position[1]];
      plan.objects.push({ role: 'accessPoint', x: p[0], z: p[1] });
      const d = Math.min(...pavedPieces.map((f) => distToRing(p, f.outer)));
      if (d > 12) plan.issues.push({ severity: 'warning', key: 'plaza.accessFarFromPaving', args: [Math.round(d)] });
    }

    // ── 6. 装饰：每个对象都必须 **在可用区里且不在障碍物里**
    const pavedUnion = pavedPieces;
    const insidePaved = (p: Vec2): boolean => pavedUnion.some((f) => pointInRing(p, f.outer));

    // 树：沿铺装内缘（外缘往里一点）
    for (const t of scatterAlongEdge(ring, { spacing: treeSpacing, inset: Math.max(1.5, greenM + 1.5), jitter: 0.5 }, rng)) {
      if (inObstacles(t.p, obsU) || !insidePaved(t.p)) continue;
      plan.objects.push({ role: 'tree', x: t.p[0], z: t.p[1], scale: 0.95 + rng() * 0.35 });
    }
    // 灌木：绿带里
    for (const b of scatterAlongEdge(ring, { spacing: Math.max(2, greenM * 1.4), inset: Math.max(0.8, greenM * 0.5), jitter: 0.3 }, rng)) {
      if (inObstacles(b.p, obsU)) continue;
      const onGreen = greenPieces.some((g) => pointInRing(b.p, g.outer));
      if (!onGreen) continue;
      plan.objects.push({ role: 'bush', x: b.p[0], z: b.p[1], scale: 0.85 + rng() * 0.3 });
    }
    // 灯：沿铺装边内缩 2 m
    for (const l of scatterAlongEdge(ring, { spacing: lampSpacing, inset: Math.max(2.2, greenM + 1), phase: lampSpacing / 2 }, rng)) {
      if (inObstacles(l.p, obsU) || !insidePaved(l.p)) continue;
      plan.objects.push({ role: 'lamp', x: l.p[0], z: l.p[1] });
    }
    // 长椅：面内散布
    for (const f of pavedPieces) {
      const pts = scatterInside(f.outer, { densityPer1000: benchDensity, margin: 2.5 }, rng);
      for (const p of pts) {
        if (inObstacles(p, obsU)) continue;
        plan.objects.push({ role: 'bench', x: p[0], z: p[1], yaw: rng() * Math.PI * 2 });
      }
    }
    // focus：core 的中心（core 不存在就退回最大铺装块的质心）
    {
      const target = corePieces.length
        ? corePieces.slice().sort((a, b) => regionArea(b) - regionArea(a))[0]
        : pavedPieces.slice().sort((a, b) => regionArea(b) - regionArea(a))[0];
      if (target) {
        let c = centroid(target.outer);
        // 凹多边形的质心可能落在外面 —— 落在外面就在顶点里挑"离边界最远"的那个（不引入新原语）
        if (!pointInRing(c, target.outer)) {
          let best = target.outer[0];
          let bd = -1;
          for (const p of target.outer) {
            const d = distToRing(p, target.outer);
            if (d > bd) {
              bd = d;
              best = p;
            }
          }
          c = centroid([best, ...target.outer.filter((p) => p !== best).slice(0, 2)]);
        }
        if (!inObstacles(c, obsU)) plan.objects.push({ role: 'centerpiece', x: c[0], z: c[1] });
      }
    }

    // ── 7. 分区与具名区域
    for (const piece of usable) plan.areas.push({ role: 'pedestrian', ring: piece.outer });
    const allPaved = unionRegions(pavedPieces);
    if (allPaved.length) plan.regions.push({ key: 'paved', tags: input.regionTags.length ? input.regionTags : ['root'], ring: allPaved[0].outer });
    if (corePieces.length) plan.regions.push({ key: 'core', tags: ['focus'], ring: corePieces[0].outer });
    edgePieces.slice(0, 4).forEach((e, i) => plan.regions.push({ key: `edge:${i}`, tags: ['edge'], ring: e.outer }));

    // ── 8. 自报统计（和 oracle 的 `Stat.*` 同口径）
    const bbox = minAreaBox(ring);
    plan.stats = [
      { key: 'Area', value: Math.round(siteArea), unit: 'm2' },
      { key: 'UsableArea', value: Math.round(usable.reduce((a, p) => a + regionArea(p), 0)), unit: 'm2' },
      { key: 'Fountains', value: plan.objects.filter((o) => o.role === 'centerpiece').length, unit: 'count' },
      { key: 'Benches', value: plan.objects.filter((o) => o.role === 'bench').length, unit: 'count' },
      { key: 'Lamps', value: plan.objects.filter((o) => o.role === 'lamp').length, unit: 'count' },
      { key: 'Trees', value: plan.objects.filter((o) => o.role === 'tree').length, unit: 'count' },
      { key: 'DominantLength', value: Math.round(bbox.w), unit: 'm' },
    ];

    plan.ready = true;
    return plan;
  },
};
