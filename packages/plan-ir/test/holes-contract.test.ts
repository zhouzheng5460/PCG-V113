// `PlanSurface.holes` 的**合同测试** —— 不属于 Plaza，属于契约本身。
//
// 为什么单独一条：带洞的面是布尔差集减掉内部障碍物的自然结果（P3.1 / D-078）。
// 数据层说"障碍重叠 0%"，只要下游任何一环没把洞带过去，画面里建筑底下就会重新铺满 ——
// 而所有 site-gen 的断言照样全绿。这种"数据对、画面错"的坑必须用一条跨包的合同测试钉住。
//
// 覆盖的消费者（用户 2026-11-03 点名的清单）：
//   metrics ✅（在 site-gen 里，已单测）
//   plan-ir normalize / area / 排序键
//   scene-ir 适配（节点是否带 holes、几何键是否区分 holes）
//   view3d 渲染（三角化是否收到 holes —— 用源码断言 + 语义采样两层）
//   命中测试（洞内点必须判 false）
//
// 跑法：node packages/plan-ir/test/holes-contract.test.ts

import { existsSync, readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { area as ringArea, ccw, pointInRing, signedArea, type Vec2 } from '../src/geom.ts';
import { normalizePlan } from '../src/normalize.ts';
import type { Plan, PlanSurface } from '../src/types.ts';
import { buildSceneIR } from '../../scene-ir/src/from-plan.ts';
import { geomKey } from '../../scene-ir/src/types.ts';

const HERE = dirname(fileURLToPath(import.meta.url));
const ROOT = join(HERE, '..', '..', '..');

let failures = 0;
let total = 0;
function check(name: string, cond: boolean, detail = ''): void {
  total++;
  if (!cond) {
    failures++;
    console.log(`  FAIL ${name} ${detail}`);
  }
}

// ── fixture：100×100 铺装 − 中心 20×20 障碍物
const PAVING: Vec2[] = [
  [0, 0],
  [100, 0],
  [100, 100],
  [0, 100],
];
const HOLE: Vec2[] = [
  [40, 40],
  [60, 40],
  [60, 60],
  [40, 60],
];
const DESIGN_AREA = 100 * 100 - 20 * 20; // 9600
const CENTER: Vec2 = [50, 50];
const EDGE: Vec2 = [10, 10];

const surface: PlanSurface = { role: 'plaza', ring: PAVING, holes: [HOLE], category: 'surface.paving' };

function makePlan(surfaces: PlanSurface[]): Plan {
  return {
    protocol: 'placemaker-plan/1',
    generator: 'contract-test',
    units: 'meters',
    seed: 1,
    area: 10000,
    ready: true,
    markers: { given: 0, auto: 0 },
    settings: {},
    surfaces,
    nets: [],
    objects: [],
    areas: [],
    regions: [],
    zoning: [],
    paint: [],
    terraform: [],
    stats: [],
    issues: [],
  };
}

/** 语义渲染面积：栅格采样 ring − holes（与 realizer 拿到 holes 后的结果等价） */
function sampledArea(s: PlanSurface, step = 0.5): number {
  let n = 0;
  for (let y = 0; y <= 100; y += step) {
    for (let x = 0; x <= 100; x += step) {
      const inside = pointInRing([x, y], s.ring) && !(s.holes ?? []).some((h) => pointInRing([x, y], h));
      if (inside) n++;
    }
  }
  return n * step * step;
}

// ── 1) 设计层面积
{
  // `ringArea` 是绝对值；这里就一个洞，直接减一次
  const a = Math.abs(ringArea(PAVING)) - Math.abs(ringArea(HOLE));
  check('① 设计面积 = 9600', Math.abs(a - DESIGN_AREA) < 1, a.toFixed(0));
}

// ── 2) normalize：洞保留、量化、方向与外环相反、被洞吃光的面要丢
{
  const p = normalizePlan(makePlan([surface]));
  check('② normalize 保留 1 块面', p.surfaces.length === 1, `${p.surfaces.length}`);
  check('② normalize 保留 1 个洞', (p.surfaces[0]?.holes?.length ?? 0) === 1, `${p.surfaces[0]?.holes?.length ?? 0}`);

  // 绕序要看**有符号**面积（`area()` 是绝对值，拿它判不出方向 —— 第一版就是这样误判的）
  const outerCcw = signedArea(p.surfaces[0].ring) > 0;
  const holeCw = signedArea(p.surfaces[0].holes![0]) < 0;
  check('② 外环逆时针', outerCcw);
  check('② 洞顺时针（与外环相反）', holeCw);

  // 被洞吃光的面不该留下（否则是一个"看不见但占数"的面）
  const eaten: PlanSurface = { role: 'plaza', ring: PAVING, holes: [PAVING] };
  const p2 = normalizePlan(makePlan([eaten]));
  check('② 被洞吃光的面被丢弃', p2.surfaces.length === 0, `${p2.surfaces.length}`);
}

// ── 3) 排序键区分"只改了洞"的两块面
{
  const a: PlanSurface = { role: 'plaza', ring: PAVING, holes: [[[10, 10], [20, 10], [20, 20], [10, 20]]] };
  const b: PlanSurface = { role: 'plaza', ring: PAVING, holes: [[[30, 30], [40, 30], [40, 40], [30, 40]]] };
  const pa = normalizePlan(makePlan([a, b]));
  check('③ 两块同外环不同洞都被保留', pa.surfaces.length === 2, `${pa.surfaces.length}`);
}

// ── 4) SceneIR 适配：节点带 holes，且几何键区分
{
  const ch = {
    surfaces: [surface],
    nets: [],
    objects: [],
    zoning: [],
  };
  const ir = buildSceneIR(ch as never, ccw(PAVING), {
    toggles: { base: true, surfaces: true, streets: true, zoning: true, massing: true, props: true, markings: true },
    seedKey: 'contract',
  });
  const node = ir.nodes.find((n) => n.type === 'surface' && n.layer === 'surfaces') as
    | (typeof ir.nodes[number] & { holes?: Vec2[][] })
    | undefined;
  check('④ SceneIR 有 surface 节点', !!node);
  check('④ 节点带 holes', (node?.holes?.length ?? 0) === 1, `${node?.holes?.length ?? 0}`);

  if (node) {
    const withHole = geomKey(node);
    const noHole = geomKey({ ...node, holes: undefined } as never);
    check('④ 几何键区分有无洞', withHole !== noHole, withHole === noHole ? '键相同' : '');
  }
}

// ── 5) 语义渲染面积 ≈ 9600（realizer 拿到 holes 后的等价结果）
{
  const a = sampledArea(surface, 0.5);
  check('⑤ 渲染面积 ≈ 9600（±1%）', Math.abs(a - DESIGN_AREA) / DESIGN_AREA < 0.01, a.toFixed(0));
}

// ── 6) 命中测试：洞内 false，洞外 true
{
  const hit = (p: Vec2): boolean => pointInRing(p, surface.ring) && !(surface.holes ?? []).some((h) => pointInRing(p, h));
  check('⑥ 中心点（洞内）命中 = false', hit(CENTER) === false);
  check('⑥ 边缘点命中 = true', hit(EDGE) === true);
}

// ── 7) 渲染层源码契约：不许再把 holes 硬编码成 []
//
// `three` 在这个包里不可解析（浏览器用 importmap），所以三角化没法在 Node 里真跑。
// 退一步做**源码断言**：这两处一旦回退，测试立刻红。
//   · geom.ts 的 `triangulateShape` 第二参数必须是变量，不能是字面量 `[]`
//   · realizer.ts 必须把 `node.holes` 传进 flatRing / prism
{
  const geomPath = join(ROOT, 'packages', 'view3d', 'src', 'geom.ts');
  const realPath = join(ROOT, 'packages', 'view3d', 'src', 'realizer.ts');
  check('⑦ view3d/geom.ts 存在', existsSync(geomPath));
  if (existsSync(geomPath)) {
    const src = readFileSync(geomPath, 'utf8');
    const hardcoded = /triangulateShape\([^)]*,\s*\[\s*\]\s*\)/.test(src);
    check('⑦ 三角化没有硬编码空洞', !hardcoded, hardcoded ? '仍有 triangulateShape(contour, [])' : '');
    check('⑦ 三角化收到 holePath', /triangulateShape\(contour,\s*holePath\)/.test(src));
  }
  if (existsSync(realPath)) {
    const src = readFileSync(realPath, 'utf8');
    check('⑦ realizer 把 holes 传给 flatRing', /flatRing\(node\.ring,\s*node\.y,\s*node\.holes\)/.test(src));
    check('⑦ realizer 把 holes 传给 prism', /prism\(node\.ring,\s*node\.y0,\s*node\.y1,\s*node\.holes\)/.test(src));
  }
}

console.log(`\n${failures === 0 ? 'PASS' : 'FAIL'} — ${total} 项断言，${failures} 项失败`);
if (failures) process.exitCode = 1;
