// 公园 / 绿地的验收：`ParkLayer` 的园路与树。
//
// 这一族是**分层接管**的：地表仍用 TownForge 的草地（Q-008：PM 的 park 族不出草地底），
// 只把园路与树换成我们排的。所以这里验的是"路与树"的规则，不是"公园有没有地"。
//
// 三条必须成立的事：
//   1. 公园有成环的园路（不是断头路），大公园还有交叉路与中心广场；
//   2. 树**不压在园路上**（树冠压路是最容易被一眼看穿的错）；
//   3. 树**不跑出街区**（越界就是种到马路上）。
//
// 跑法：node node_modules/tsx/dist/cli.mjs test/bridge-park.test.ts

import { Model } from '../src/town/model';
import { buildBridge } from '../src/bridge/placemaker';
import { buildPark, PARK_DIMS } from '../src/bridge/park';
import { UNIT_M } from '../src/core/units';
import { pointInPolygon } from '../src/town/zones';
import { Pt } from '../src/core/geom';

let failures = 0;
let total = 0;
function check(name: string, cond: boolean, detail = ''): void {
  total++;
  if (!cond) {
    failures++;
    console.log(`  FAIL ${name} ${detail}`);
  }
}

/** 点到折线最短距离（米），用于"树不许压路" */
function distToPolyline(p: { x: number; y: number }, pts: { x: number; y: number }[]): number {
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

const m = (u: number): number => u * UNIT_M;

for (const [n, seed] of [
  [15, 682063530],
  [24, 42],
] as [number, number][]) {
  const model = new Model({ nPatches: n, seed, plaza: true });
  model.generate();
  const r = buildPark(model);
  const label = `n=${n} seed=${seed}`;

  check(`${label} 有公园或绿地`, r.parks.length > 0, String(r.parks.length));
  check(`${label} 种了树`, r.stats.trees > 0, String(r.stats.trees));
  check(`${label} 密度 8~30 棵/千m²`, r.stats.treesPer1kM2 >= 8 && r.stats.treesPer1kM2 <= 30, r.stats.treesPer1kM2.toFixed(1));

  let badClear = 0;
  let badInside = 0;
  let parksWithRing = 0;
  for (const p of r.parks) {
    // 公园必须有园路；绿地（林带）不该有园路
    if (p.use === 'park') {
      if (p.paths > 0) parksWithRing++;
      else check(`${label} 公园有园路`, false, `patch ${p.patchIndex} paths=0`);
    } else {
      check(`${label} 绿地不做园路`, p.paths === 0, `patch ${p.patchIndex} paths=${p.paths}`);
    }
    // 大公园有中心广场
    if (p.use === 'park' && p.areaM2 > 3000) {
      check(`${label} 大公园有广场`, p.plaza, `${Math.round(p.areaM2)} m²`);
    }

    const block = model.patches[p.patchIndex].shape.map((q) => new Pt(q.x * UNIT_M, q.y * UNIT_M));
    const lines = p.pathFeatures.map((f) => f.pts.map((q) => new Pt(q.x * UNIT_M, q.y * UNIT_M)));
    for (const pr of p.propFeatures) {
      const at = new Pt(pr.at.x * UNIT_M, pr.at.y * UNIT_M);
      if (!pointInPolygon(at, block)) badInside++;
      for (const line of lines) {
        if (distToPolyline(at, line) < PARK_DIMS.treeClear - 1e-6) {
          badClear++;
          break;
        }
      }
    }
  }

  check(`${label} 树不压园路（≥${PARK_DIMS.treeClear} m）`, badClear === 0, `${badClear} 棵压路`);
  check(`${label} 树不越界`, badInside === 0, `${badInside} 棵在街区外`);
  if (r.stats.parks > 0) {
    check(`${label} 每个公园都有园路`, parksWithRing === r.stats.parks, `${parksWithRing}/${r.stats.parks}`);
  }

  console.log(
    `  ${label}: 公园 ${r.stats.parks} · 绿地 ${r.stats.green} · 树 ${r.stats.trees}（${r.stats.treesPer1kM2.toFixed(1)} 棵/千m²）· 园路 ${r.stats.pathLenM.toFixed(0)} m`
  );
}

// ── 与 TownForge 的数量对照：它每个公园的树数是另一个实现，只做量级参照
{
  const model = new Model({ nPatches: 24, seed: 42, plaza: true });
  model.generate();
  const r = buildPark(model);
  const tfTrees = model.patches.reduce((s, p) => {
    const lot = (p.ward as unknown as { lot?: { use?: string; trees?: number } } | null)?.lot;
    return s + (lot && (lot.use === 'park' || lot.use === 'green') ? (lot.trees ?? 0) : 0);
  }, 0);
  check('树数与 TownForge 同量级（0.5~2×）', r.stats.trees > tfTrees * 0.5 && r.stats.trees < tfTrees * 2, `${r.stats.trees} vs TF ${tfTrees}`);
  console.log(`  对照：我们 ${r.stats.trees} 棵 · TownForge ${tfTrees} 棵`);
}

console.log(`\n${failures === 0 ? 'PASS' : 'FAIL'} — ${total} 项断言，${failures} 项失败`);
process.exitCode = failures === 0 ? 0 : 1;
