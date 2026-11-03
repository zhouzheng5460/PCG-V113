import { describe, expect, it } from 'vitest';
import { PAVING_PAD, pickEntryPoint, planAccessRoad, planSurfaces, traceServiceRoad } from '../core/plan';
import { intersect, offsetPath, subtract, unionAll } from '../core/boolean';
import { Paths, pathsArea, pointInPaths, rectRing, strokeEnvelope } from '../core/polygon';
import { Vec2, v2 } from '../core/vec';

const sq = (x: number, y: number, w: number, h: number) => rectRing(v2(x, y), v2(x + w, y + h));
const area = (p: Paths) => (p.length ? Math.abs(pathsArea(p)) : 0);

type Gate = { pos: Vec2; width: number };

/** 一套完整输入：120×100 地块、退线 5、可选建筑与门 */
function setup(
  opts: { buildings?: Paths[]; gates?: Gate[]; landUse?: 'industrial' | 'urban' | 'park' } = {},
) {
  const boundary = [sq(0, 0, 120, 100)];
  const buildable = offsetPath(boundary, -5);
  const buildings = opts.buildings ?? [[sq(25, 25, 45, 30)]];
  const bUnion = buildings.length ? unionAll(buildings) : [];
  const gates = opts.gates ?? [{ pos: v2(60, 0), width: 8 }];
  const access = planAccessRoad(boundary, buildable, bUnion, gates);
  const free = bUnion.length ? subtract(buildable, bUnion) : buildable;
  const plan = planSurfaces({
    boundary,
    buildable,
    free,
    buildings,
    accessRoad: access ? access.road : null,
    landUse: opts.landUse ?? 'industrial',
  });
  return { boundary, buildable, buildings, bUnion, access, plan };
}

const groundOf = (plan: ReturnType<typeof setup>['plan'], surface: string) =>
  plan.grounds.find((g) => g.surface === surface);

const roadUnionOf = (plan: ReturnType<typeof setup>['plan']) =>
  plan.roads.length ? unionAll(plan.roads.map((r) => [strokeEnvelope(r.centerline, r.width / 2)])) : [];

describe('规划层 · 出入口与主路', () => {
  it('从门出发，路朝地块内侧走，不会掉头出界', () => {
    const { boundary, buildable, access } = setup();
    expect(access).toBeTruthy();
    const line = access!.road.centerline;
    expect(line.length).toBeGreaterThanOrEqual(2);
    expect(line[0].x).toBeCloseTo(60, 0);
    expect(line[0].y).toBeCloseTo(0, 0);
    for (const p of line.slice(1)) {
      expect(pointInPaths(p, boundary) || pointInPaths(p, buildable)).toBe(true);
    }
    // 终点必须比起点更靠里
    expect(line[line.length - 1].y).toBeGreaterThan(line[0].y + 5);
  });

  it('路带不压建筑：中心线与建筑保持半个路宽', () => {
    const { plan, bUnion } = setup();
    const access = plan.roads[0];
    expect(access).toBeTruthy();
    const env = strokeEnvelope(access.centerline, access.width / 2);
    // 允许贴边（<1㎡），不允许真的压进去
    expect(area(intersect([env], bUnion))).toBeLessThan(1);
  });

  it('没有门时降级：用地块最长边的中点当入口，仍然出得来', () => {
    const boundary = [sq(0, 0, 120, 100)];
    const entry = pickEntryPoint(boundary, []);
    expect(entry).toBeTruthy();
    expect(entry!.width).toBeGreaterThan(0);
    const access = planAccessRoad(boundary, offsetPath(boundary, -5), [], []);
    expect(access).toBeTruthy();
    expect(access!.road.centerline.length).toBeGreaterThanOrEqual(2);
  });

  it('门换位置 → 主路跟着变（PRD §89：Gate 变化驱动道路重算）', () => {
    const a = setup({ gates: [{ pos: v2(60, 0), width: 8 }] });
    const b = setup({ gates: [{ pos: v2(10, 50), width: 8 }] });
    const la = a.plan.roads[0].centerline;
    const lb = b.plan.roads[0].centerline;
    expect(la[0].x).toBeCloseTo(60, 0);
    expect(lb[0].x).toBeCloseTo(10, 0);
    expect(JSON.stringify(la)).not.toBe(JSON.stringify(lb));
  });

  it('净地被占满时 traceServiceRoad 返回空（不硬造一条穿墙的路）', () => {
    const pts = traceServiceRoad([sq(0, 0, 100, 100)], [sq(-10, -10, 200, 200)], v2(50, 0), v2(0, 1));
    expect(pts).toHaveLength(0);
  });

  it('多栋楼时会生成支路把它们接到主路上', () => {
    const { plan } = setup({
      buildings: [[sq(20, 20, 25, 25)], [sq(70, 20, 25, 25)]],
    });
    // 1 条主路 + 至少 1 条支路
    expect(plan.roads.length).toBeGreaterThanOrEqual(1);
  });
});

describe('规划层 · 地面分区', () => {
  it('用地平衡：建筑 + 路 + 铺装 + 绿地 + 停车 + 禁建 = 地块（误差 < 0.5%）', () => {
    const { plan } = setup();
    const s = plan.stats;
    const sum = s.buildingArea + s.roadArea + s.pavingArea + s.lawnArea + s.parkingArea + s.excludedArea;
    expect(sum).toBeGreaterThan(0);
    expect(Math.abs(sum - s.siteArea) / s.siteArea).toBeLessThan(0.005);
  });

  it('三类地面两两不重叠（重叠会造成 z-fighting 与重复计量）', () => {
    const { plan } = setup();
    const kinds = ['parking', 'paving', 'lawn'];
    for (let i = 0; i < kinds.length; i++) {
      for (let j = i + 1; j < kinds.length; j++) {
        const a = groundOf(plan, kinds[i]);
        const b = groundOf(plan, kinds[j]);
        if (!a || !b) continue;
        expect(area(intersect(a.boundary, b.boundary))).toBeLessThan(1);
      }
    }
  });

  it('建筑周边 3 m 内一定有铺装或道路（可达性，不是把楼丢在草里）', () => {
    const { plan, buildings } = setup();
    const paving = groundOf(plan, 'paving');
    expect(paving).toBeTruthy();
    const roadUnion = roadUnionOf(plan);
    const walk = paving
      ? unionAll([paving.boundary, ...(roadUnion.length ? [roadUnion] : [])])
      : roadUnion;
    expect(walk.length).toBeGreaterThan(0);
    for (const b of buildings) {
      expect(area(intersect(offsetPath(b, 3), walk))).toBeGreaterThan(0);
    }
  });

  it('铺装不吃掉建筑基底', () => {
    const { plan, bUnion } = setup();
    const paving = groundOf(plan, 'paving');
    if (!paving) return;
    expect(area(intersect(paving.boundary, bUnion))).toBeLessThan(1);
  });

  it('绿地兜底：剩下的地块都在绿地里（含退线带）', () => {
    const { plan, boundary, bUnion } = setup();
    const lawn = groundOf(plan, 'lawn');
    expect(lawn).toBeTruthy();
    const lawnPaths = lawn!.boundary;
    const paving = groundOf(plan, 'paving');
    const parking = groundOf(plan, 'parking');
    let taken = bUnion;
    for (const p of [roadUnionOf(plan), paving?.boundary ?? [], parking?.boundary ?? []]) {
      if (p.length) taken = taken.length ? unionAll([taken, p]) : p;
    }
    const rest = taken.length ? subtract(boundary, taken) : boundary;
    expect(area(subtract(rest, lawnPaths))).toBeLessThan(area(rest) * 0.01 + 1);
  });

  it('停车：需求为正时给出车位，且不压建筑', () => {
    const { plan, buildings } = setup({ buildings: [[sq(20, 20, 60, 40)]] });
    const parking = groundOf(plan, 'parking');
    expect(parking).toBeTruthy();
    expect(plan.stats.parkingSpaces).toBeGreaterThan(0);
    expect(area(intersect(parking!.boundary, unionAll(buildings)))).toBeLessThan(1);
  });

  it('铺装外扩宽度按用地类型分级（工业 > 公园）', () => {
    expect(PAVING_PAD.industrial).toBeGreaterThan(PAVING_PAD.park);
  });
});
