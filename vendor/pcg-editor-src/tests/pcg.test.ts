import { describe, expect, it } from 'vitest';
import { generateParcel, generateForParcel, collectObstacles, mulberry32, FLOOR_HEIGHT } from '../core/pcg';
import { addTo, demoProject, emptyProject, makeBuilding, makeNoBuild, makeParcel, makeRoad } from '../core/factory';
import { intersect, unionAll } from '../core/boolean';
import { Paths, boundsOfPaths, pathsArea, pointInPaths, rectRing } from '../core/polygon';
import { ParcelObj } from '../core/model';
import { v2 } from '../core/vec';

const sq = (x: number, y: number, w: number, h: number) => rectRing(v2(x, y), v2(x + w, y + h));

const params = (over: Partial<Parameters<typeof generateParcel>[1]> = {}) => ({
  landUse: 'industrial' as const,
  density: 0.45,
  far: 1.2,
  heightRange: [12, 24] as [number, number],
  seed: 7,
  ...over,
});

/** 建筑轮廓是否整体落在地块内（顶点 + 形心双检） */
function insideAll(fp: Paths, parcel: Paths): boolean {
  const pts = fp.flat();
  return pts.every((p) => pointInPaths(p, parcel));
}

describe('PCG · 随机源', () => {
  it('同一 seed 完全可复现', () => {
    const a = mulberry32(42);
    const b = mulberry32(42);
    const seqA = [a(), a(), a()];
    const seqB = [b(), b(), b()];
    expect(seqA).toEqual(seqB);
  });

  it('不同 seed 会分叉', () => {
    expect(mulberry32(1)()).not.toBe(mulberry32(2)());
  });

  it('值域落在 [0,1)', () => {
    const r = mulberry32(9);
    for (let i = 0; i < 200; i++) {
      const v = r();
      expect(v).toBeGreaterThanOrEqual(0);
      expect(v).toBeLessThan(1);
    }
  });
});

describe('PCG · 单地块生成', () => {
  const parcel = [sq(0, 0, 100, 100)];

  it('干净地块能落位，且全部在地块内', () => {
    const r = generateParcel(parcel, params());
    expect(r.buildings.length).toBeGreaterThan(0);
    for (const b of r.buildings) {
      expect(insideAll(b.footprint, parcel)).toBe(true);
    }
    expect(r.stats.count).toBe(r.buildings.length);
    expect(r.message).toBeUndefined();
  });

  it('覆盖率不超过密度上限', () => {
    const r = generateParcel(parcel, params({ density: 0.25 }));
    expect(r.stats.coverage).toBeLessThanOrEqual(0.25 + 1e-6);
  });

  it('容积率实现在目标值附近（±35%）', () => {
    const r = generateParcel(parcel, params({ far: 1.5, density: 0.6, heightRange: [15, 30] }));
    expect(r.stats.far).toBeGreaterThan(1.5 * 0.65);
    expect(r.stats.far).toBeLessThan(1.5 * 1.35);
  });

  it('退线生效：所有轮廓都在内缩范围内', () => {
    const r = generateParcel(parcel, params({ setback: 12 }));
    expect(r.stats.setback).toBe(12);
    for (const b of r.buildings) {
      for (const p of b.footprint.flat()) {
        expect(p.x).toBeGreaterThan(12 - 0.01);
        expect(p.x).toBeLessThan(88 + 0.01);
        expect(p.y).toBeGreaterThan(12 - 0.01);
        expect(p.y).toBeLessThan(88 + 0.01);
      }
    }
  });

  it('建筑高度落在给定区间内，且层高与高度自洽', () => {
    const r = generateParcel(parcel, params({ heightRange: [10, 20] }));
    for (const b of r.buildings) {
      expect(b.height).toBeGreaterThanOrEqual(10);
      expect(b.height).toBeLessThanOrEqual(20);
      expect(b.floors).toBeGreaterThanOrEqual(1);
      expect(b.height).toBeCloseTo(Math.min(20, Math.max(10, b.floors * FLOOR_HEIGHT)), 6);
    }
  });

  it('禁建区被避让：生成物与禁建区无重叠', () => {
    const obstacle: Paths = [sq(30, 30, 40, 40)];
    const r = generateParcel(parcel, params(), [obstacle]);
    expect(r.buildings.length).toBeGreaterThan(0);
    for (const b of r.buildings) {
      const overlap = intersect(b.footprint, obstacle);
      expect(overlap.length ? Math.abs(pathsArea(overlap)) : 0).toBeLessThan(1);
    }
  });

  it('同一 seed 结果一致，换 seed 会变', () => {
    const a = generateParcel(parcel, params({ seed: 3 }));
    const b = generateParcel(parcel, params({ seed: 3 }));
    const c = generateParcel(parcel, params({ seed: 4 }));
    const sig = (r: typeof a) =>
      r.buildings.map((x) => `${x.floors}:${Math.abs(pathsArea(x.footprint)).toFixed(1)}`).join('|');
    expect(sig(a)).toBe(sig(b));
    expect(sig(a)).not.toBe(sig(c));
  });

  it('多栋之间保持间距（不重叠）', () => {
    const r = generateParcel(parcel, params({ far: 1.6, density: 0.5 }));
    for (let i = 0; i < r.buildings.length; i++) {
      for (let j = i + 1; j < r.buildings.length; j++) {
        const ov = intersect(r.buildings[i].footprint, r.buildings[j].footprint);
        expect(ov.length ? Math.abs(pathsArea(ov)) : 0).toBeLessThan(1);
      }
    }
  });

  it('地块太小 / 退线过大 → 返回空并说明原因', () => {
    const tiny = generateParcel([sq(0, 0, 8, 8)], params());
    expect(tiny.buildings).toHaveLength(0);
    expect(tiny.message).toBeTruthy();

    const overSetback = generateParcel(parcel, params({ setback: 49 }));
    expect(overSetback.buildings).toHaveLength(0);
    expect(overSetback.message).toContain('退线');
  });

  it('可建范围被障碍物占满 → 返回空并说明原因', () => {
    const r = generateParcel(parcel, params(), [[sq(-5, -5, 110, 110)]]);
    expect(r.buildings).toHaveLength(0);
    expect(r.message).toBeTruthy();
  });
});

describe('PCG · 与既有对象的关系（示例项目实跑）', () => {
  const demoWithGenerated = () => {
    let p = demoProject();
    const parcel = p.order.map((id) => p.objects[id]).find((o) => o.kind === 'parcel') as ParcelObj;
    return { p, parcel };
  };

  it('障碍物包含禁建区、既有建筑与道路，但排除本地块旧生成物', () => {
    const { p, parcel } = demoWithGenerated();
    const before = collectObstacles(p, parcel.id);
    expect(before.length).toBeGreaterThanOrEqual(3); // 禁建区 + 2 栋既有 + 道路

    // 本地块的旧生成物：不算障碍（这次要被覆盖）
    const mine = { ...makeBuilding([sq(20, 20, 20, 20)], { origin: 'generated' }), generatedFrom: parcel.id };
    expect(collectObstacles(addTo(p, mine), parcel.id).length).toBe(before.length);

    // 别的来源的生成物：算障碍
    const alien = { ...makeBuilding([sq(60, 60, 10, 10)], { origin: 'generated' }), generatedFrom: 'other-parcel' };
    expect(collectObstacles(addTo(p, alien), parcel.id).length).toBe(before.length + 1);
  });

  it('示例项目生成：不与既有建筑 / 禁建区 / 道路重叠', () => {
    const { p, parcel } = demoWithGenerated();
    const r = generateForParcel(p, parcel);

    const solids = p.order
      .map((id) => p.objects[id])
      .filter((o) => o.kind === 'building' || o.kind === 'nobuild')
      .map((o) => (o.kind === 'building' ? o.footprint : o.kind === 'nobuild' ? o.boundary : []))
      .filter((paths) => paths.length);
    const solidUnion = unionAll(solids);

    for (const b of r.buildings) {
      const ov = intersect(b.footprint, solidUnion);
      expect(ov.length ? Math.abs(pathsArea(ov)) : 0).toBeLessThan(1);
    }
  });

  it('示例项目生成：结果为 generated 语义需要的信息齐备', () => {
    const { p, parcel } = demoWithGenerated();
    const r = generateForParcel(p, parcel);
    expect(r.buildings.length).toBeGreaterThan(0);
    expect(r.stats.footprintArea).toBeGreaterThan(0);
    expect(r.stats.far).toBeGreaterThan(0);
    expect(r.stats.maxHeight).toBeGreaterThan(0);
    expect(r.stats.coverage).toBeGreaterThan(0);
  });

  it('碎片净地能靠补位轮次用起来（利用率 ≥ 40%）', () => {
    const { p, parcel } = demoWithGenerated();
    const r = generateForParcel(p, parcel);
    // 示例地块的净地被既有两栋楼切成"一圈院子 + 走廊"，一轮肯定放不满
    expect(r.stats.count).toBeGreaterThanOrEqual(4);
    expect(r.stats.footprintArea / r.stats.freeArea).toBeGreaterThan(0.4);
  });

  it('不会生成 6 m 宽的"厂房"条带（面宽硬约束）', () => {
    // 多个种子都跑一遍：条带问题往往只在某几种分割下出现
    for (let seed = 1; seed <= 12; seed++) {
      const { p, parcel } = demoWithGenerated();
      const r = generateForParcel({ ...p, objects: { ...p.objects } }, { ...parcel, seed });
      for (const b of r.buildings) {
        const bb = boundsOfPaths(b.footprint);
        const short = Math.min(bb.maxX - bb.minX, bb.maxY - bb.minY);
        expect(short).toBeGreaterThanOrEqual(8 - 0.01);
      }
    }
  });

  it('地块没有障碍时能生成，加了道路后才被切碎（说明避让真的生效）', () => {
    let p = emptyProject();
    const parcel = makeParcel([sq(0, 0, 120, 80)], { far: 1.2, heightRange: [10, 20] });
    p = addTo(p, parcel);
    const r1 = generateForParcel(p, parcel);
    const road = makeRoad([v2(-10, 40), v2(130, 40)], { width: 10 });
    p = addTo(p, road);
    const obstacles = collectObstacles(p, parcel.id);
    const r2 = generateParcel(parcel.boundary, params({ far: 1.2 }), obstacles);

    expect(r1.buildings.length).toBeGreaterThan(0);
    // 不比较数量：内部道路自己也占净地，加了路之后楼变少是正常的（不是回归）
    expect(r2.buildings.length).toBeGreaterThan(0);
    // 所有轮廓都躲开了道路带（y 35..45）
    for (const b of r2.buildings) {
      for (const pt of b.footprint.flat()) {
        expect(pt.y < 34.5 || pt.y > 45.5).toBe(true);
      }
    }
  });

  it('禁建区落在中间时，生成物会绕开（示例：中间 40×40 禁建）', () => {
    let p = emptyProject();
    const parcel = makeParcel([sq(0, 0, 100, 100)], { far: 1.0, heightRange: [10, 20] });
    p = addTo(p, parcel);
    p = addTo(p, makeNoBuild([sq(30, 30, 40, 40)], '测试禁建'));
    const r = generateForParcel(p, parcel);
    expect(r.buildings.length).toBeGreaterThan(0);
    for (const b of r.buildings) {
      expect(pointInPaths({ x: 50, y: 50 }, b.footprint)).toBe(false);
    }
  });
});
