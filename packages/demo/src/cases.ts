// 验收用例：四个地块，专门覆盖街道规则里最容易出问题的地方。
//
//   A 四面临街    —— 全部通过，作为基线
//   B L 形        —— 内凹边没有街（不该硬要贴合点），其余合格
//   C 街道偏移    —— 街看着在旁边，但超出容差 → 全边不临街（R1/R7 失败）
//   D 斜街 25°    —— 平行度 0.906 < 0.95 → 看着临街实际不认（近邻未达标）

import { buildAttachmentsExact } from '../../street-rules/src/exact.ts';
import { netFromSegment } from '../../street-rules/src/finder.ts';
import type { CaseInput, NearbyNet } from '../../plan-ir/src/types.ts';
import type { Vec2 as GVec2 } from '../../plan-ir/src/geom.ts';

export interface StreetDef {
  id: number;
  a: GVec2;
  b: GVec2;
  w: number;
  category?: 'road' | 'pathway';
}

export interface DemoCase {
  name: string;
  note: string;
  input: CaseInput;
  ring2d: GVec2[];
  streets: StreetDef[];
}

export const CASES: DemoCase[] = [
  {
    name: 'A · 四面临街',
    note: '矩形地块，四条街贴着四条边。基线用例：应该全部通过。',
    ring2d: [
      [0, 0],
      [150, 0],
      [150, 100],
      [0, 100],
    ],
    streets: [
      { id: 1, a: [0, -4], b: [150, -4], w: 8 },
      { id: 2, a: [154, 0], b: [154, 100], w: 8 },
      { id: 3, a: [150, 104], b: [0, 104], w: 8 },
      { id: 4, a: [-4, 100], b: [-4, 0], w: 8 },
    ],
    input: null as never,
  },
  {
    name: 'B · L 形地块',
    note: '凹形，内凹的那条边没有街——不该要求贴合点，其余边合格。',
    ring2d: [
      [0, 0],
      [120, 0],
      [120, 40],
      [60, 40],
      [60, 90],
      [0, 90],
    ],
    streets: [
      { id: 11, a: [0, -4], b: [120, -4], w: 8 },
      { id: 12, a: [124, 0], b: [124, 40], w: 8 },
      { id: 13, a: [-4, 90], b: [-4, 0], w: 8 },
      { id: 14, a: [60, 94], b: [0, 94], w: 8 },
    ],
    input: null as never,
  },
  {
    name: 'C · 街道偏移 11 m',
    note: '街在边上 11 m 处，超出「路宽/2 + 3 = 7 m」容差。看着在旁边，实际一条边都不认。',
    ring2d: [
      [0, 0],
      [120, 0],
      [120, 80],
      [0, 80],
    ],
    streets: [{ id: 21, a: [0, -11], b: [120, -11], w: 8 }],
    input: null as never,
  },
  {
    name: 'D · 斜街 25°',
    note: '南边有正常街道，北边这条斜了 25°（平行度 0.906）。北边会被判为「近邻未达标」而不是临街。',
    ring2d: [
      [0, 0],
      [160, 0],
      [160, 60],
      [0, 60],
    ],
    streets: [
      { id: 31, a: [0, -4], b: [160, -4], w: 8 },
      { id: 32, a: [-10, 74], b: [170, -10], w: 8 },
    ],
    input: null as never,
  },
];

/** 把用例定义变成真正的 CaseInput（含精确构造出来的贴合点）。 */
export function buildCase(c: DemoCase): CaseInput {
  const ring3 = c.ring2d.map((p) => [p[0], 0, p[1]] as const);
  const nets: NearbyNet[] = c.streets.map((s) =>
    netFromSegment(s.id, s.a, s.b, s.w, { category: s.category ?? 'road', speedLimit: 50 }),
  );
  const exact = buildAttachmentsExact(c.ring2d, nets);
  return {
    shape: { ring: ring3 as never, source: 'block', attachments: exact.attachments },
    site: {
      nearbyNets: nets,
      obstacles: [],
      zoneGrids: [],
      layoutScale: 0.5,
      edgeMargin: 2,
    },
    markers: [],
    settings: {},
    seed: 1,
    regionTags: ['root'],
  };
}

export function allCases(): DemoCase[] {
  for (const c of CASES) if (!c.input) c.input = buildCase(c);
  return CASES;
}

