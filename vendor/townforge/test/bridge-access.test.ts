// 出入口（`EntranceSuggest`）与 `ready` 的验收。
//
// 这一步是整个 G5 的入口：`ready ⇔ CheckReady(given>0 ? given : suggest)`，
// 而 130 个生成器里只有 **parking 一族**的 `suggest` 真的碰街道（docs/A §0.1）。
// 所以这个测试要证的不是"我们的出入口长得对"，而是**因果链真的通了**：
//   有街 → 打得出射线 → 建议出口 → parking 地块 ready；
//   删光车行道 → 打不出来 → 不 ready。（docs/A §4 里那张扰动表就是这么做的）
//
// 跑法：node node_modules/tsx/dist/cli.mjs test/bridge-access.test.ts

import { Model } from '../src/town/model';
import { buildBridge } from '../src/bridge/placemaker';
import { buildAccess, GAP_MAX_M, REACH_M } from '../src/bridge/access';

let failures = 0;
let total = 0;
function check(name: string, cond: boolean, detail = ''): void {
  total++;
  if (!cond) {
    failures++;
    console.log(`  FAIL ${name} ${detail}`);
  }
}

const q = (a: number[], p: number): number => {
  if (!a.length) return NaN;
  const s = a.slice().sort((x, y) => x - y);
  return s[Math.min(s.length - 1, Math.floor(s.length * p))];
};

// ── 1) 正常情况下：几乎每个街区都打得出口，且间隙落在 C# 的容差里
for (const [n, seed] of [
  [15, 682063530],
  [24, 42],
  [10, 7],
] as [number, number][]) {
  const model = new Model({ nPatches: n, seed, plaza: true });
  model.generate();
  const r = buildBridge(model);
  const a = r.access;
  const label = `n=${n} seed=${seed}`;

  check(`${label} 有出入口`, a.stats.accessCount > 0, String(a.stats.accessCount));
  check(`${label} 出入口覆盖到几乎每个街区`, a.stats.patchesWithAccess / Math.max(1, a.stats.patches) > 0.9,
    `${a.stats.patchesWithAccess}/${a.stats.patches}`);
  check(`${label} 接街比例 ≥ 90%`, a.stats.attachedShare >= 0.9, (a.stats.attachedShare * 100).toFixed(0) + '%');
  // 间隙允许轻微为负：TownForge 的街区边界基本压在车行道边缘上（差几厘米），
  // 正好落在 C# 判据 `gap >= -w/2` 的"点在车道里"那一侧。这是几何事实，不是 bug。
  check(`${label} 间隙中位在 -1~15 m`, a.stats.gapMedM > -1 && a.stats.gapMedM <= GAP_MAX_M, a.stats.gapMedM.toFixed(3));

  // 每条记录本身要自洽：attached 必须等价于 gap 的判据
  const bad = a.accesses.filter((x) => {
    const w = x.roadCls ? { arterial: 28, collector: 20, local: 10.5 }[x.roadCls] : 10.5;
    const ok = x.gapM >= -w / 2 && x.gapM <= GAP_MAX_M;
    return ok !== x.attached;
  });
  check(`${label} attached 与 gap 判据一致`, bad.length === 0, `${bad.length} 条不一致`);

  // 射线不超过 C# 的 40 m（放宽一条红线宽，因为最后一步取的是垂距）
  const tooFar = a.accesses.filter((x) => x.reachM > REACH_M + 28);
  check(`${label} 射线不超 40 m`, tooFar.length === 0, `${tooFar.length} 条超距`);

  // 停车场地块：ready 必须等于"有没有接街的口"
  const pr = a.parkingReady;
  if (pr.length) {
    const wrong = pr.filter((x) => x.ready !== (a.perPatch.get(x.patchIndex)?.attached ?? 0) > 0);
    check(`${label} 停车场 ready = 有接街口`, wrong.length === 0, `${wrong.length} 块不一致`);
    check(`${label} 停车场 ready 数 ≤ 总数`, a.stats.parkingReady <= a.stats.parkingTotal);
    console.log(
      `  ${label}: 出入口 ${a.stats.accessCount} · 接街 ${(a.stats.attachedShare * 100).toFixed(0)}% · 间隙中位 ${a.stats.gapMedM.toFixed(1)} m · 停车场 ready ${a.stats.parkingReady}/${a.stats.parkingTotal}`
    );
  } else {
    console.log(`  ${label}: 出入口 ${a.stats.accessCount} · 接街 ${(a.stats.attachedShare * 100).toFixed(0)}% · 间隙中位 ${a.stats.gapMedM.toFixed(1)} m · 无停车场`);
  }
}

// ── 2) 因果链（docs/A §4 的扰动实验）：把街道全删掉，出入口与 ready 必须一起塌掉
{
  const model = new Model({ nPatches: 15, seed: 682063530, plaza: true });
  model.generate();
  const withRoads = buildAccess(model);
  // 空路网 = "删光所有街道"。直接喂一个空 net，比改 model 更干净，也不会污染缓存。
  const emptyNet = {
    edges: [],
    junctions: [],
    nodeEdges: new Map(),
    nodeJunction: new Map(),
    driveways: [],
  } as unknown as Parameters<typeof buildAccess>[1];
  const noRoads = buildAccess(model, emptyNet);

  check('删光街道后出入口归零', noRoads.stats.accessCount === 0, String(noRoads.stats.accessCount));
  check('删光街道后接街占比归零', noRoads.stats.attachedShare === 0);
  check(
    '删光街道后停车场全部不 ready',
    noRoads.parkingReady.every((x) => !x.ready),
    `${noRoads.parkingReady.filter((x) => x.ready).length} 块仍 ready`
  );
  console.log(
    `  扰动：有街 ${withRoads.stats.accessCount} 个口 / 接街 ${(withRoads.stats.attachedShare * 100).toFixed(0)}% · 停车场 ready ${withRoads.stats.parkingReady}/${withRoads.stats.parkingTotal}` +
      `  →  无街 0 个口 · 停车场 ready 0/${noRoads.stats.parkingTotal}`
  );
  void emptyNet;
}

// ── 3) 判据边界：gap 的容差就是 C# 的两个常数，别在别处再写死一遍
{
  check('REACH_M = 40（C# 的射线长度）', REACH_M === 40, String(REACH_M));
  check('GAP_MAX_M = 15（C# 的容差）', GAP_MAX_M === 15, String(GAP_MAX_M));
}

console.log(`\n${failures === 0 ? 'PASS' : 'FAIL'} — ${total} 项断言，${failures} 项失败`);
process.exitCode = failures === 0 ? 0 : 1;
