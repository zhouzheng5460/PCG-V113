// 停车场 / 公园的迁移对账。
//
// 为什么先验这两类（用户指定）：它们在 Placemaker 里**不是"地块内容"，是独立的生成器家族**
//   · parking 家族：`ParkingLayer`（130 个生成器里唯一能靠街道改动 `ready` 的家族，见 docs/A §0.1）
//   · park 家族：`ParkLayer` / `PlazaLayer` / `carYard` / `abandonedLot` …（恒 ready，只影响 marker 数）
// 而 TownForge 里它们只是两个 `LandUse`（`parking` / `park` / `green`），由 `modern.ts` 的地块内容函数排。
// 所以"迁移"不是搬几何，是搬**规则**，得先把两边的规则摊开对一遍。
//
// 这个测试做三件事：
//   1. 断言它们现在**没有**被桥接管（skipped 里逐条写原因）——不许静默变成空地；
//   2. 量 TF 已经做对的尺寸（车位 2.52×5.04 m、通道 6.3 m），和 PM/规范口径逐项比；
//   3. 输出一张"谁有什么、缺什么"的对账表，作为 G5 的输入清单。
//
// 跑法：node node_modules/tsx/dist/cli.mjs test/bridge-parking.test.ts

import { Model } from '../src/town/model';
import { buildBridge } from '../src/bridge/placemaker';
import { buildAccess } from '../src/bridge/access';
import { buildParking, PARKING_DIMS } from '../src/bridge/parking';
import { CAR_STALL, TRUCK_STALL } from '../src/town/parking';
import { UNIT_M } from '../src/core/units';
import type { LandUse } from '../src/scene/types';

let failures = 0;
let total = 0;
function check(name: string, cond: boolean, detail = ''): void {
  total++;
  if (!cond) {
    failures++;
    console.log(`  FAIL ${name} ${detail}`);
  }
}

const OPEN: LandUse[] = ['parking', 'park', 'green'];
const m = (u: number): number => u * UNIT_M;

// ── 1) 车位尺寸：TF 的常量 vs Placemaker / 规范口径
{
  const w = m(CAR_STALL.w);
  const l = m(CAR_STALL.l);
  const aisle = m(CAR_STALL.aisle);
  console.log(`车位（小汽车）：${w.toFixed(2)} × ${l.toFixed(2)} m，通道 ${aisle.toFixed(2)} m`);
  // 规范平行式小客车位 2.5×5.0 m；PM 的 parking 生成器也用这个口径
  check('车位宽 ≈ 2.5 m', Math.abs(w - 2.5) < 0.1, w.toFixed(2));
  check('车位长 ≈ 5.0 m', Math.abs(l - 5.0) < 0.1, l.toFixed(2));
  check('通道 ≥ 5.5 m（PM driveAisle）', aisle >= 5.5, aisle.toFixed(2));
  const tw = m(TRUCK_STALL.w);
  const tl = m(TRUCK_STALL.l);
  console.log(`车位（货车）：${tw.toFixed(2)} × ${tl.toFixed(2)} m，通道 ${m(TRUCK_STALL.aisle).toFixed(2)} m`);
  check('货车位 ≈ 3.5 × 18 m（已含单位换算）', tw > 6 && tl > 17, `${tw.toFixed(1)}×${tl.toFixed(1)}`);
}

// ── 2) 三个规模各跑一遍：这两类必须被"记账式跳过"，不许静默消失
const summary: Record<string, { n: number; bays: number; trees: number; mats: Record<string, number>; props: Record<string, number>; grass: number }> = {};

for (const [n, seed] of [
  [15, 682063530],
  [24, 42],
  [10, 7],
] as [number, number][]) {
  const model = new Model({ nPatches: n, seed, plaza: true });
  model.generate();
  const r = buildBridge(model);
  const label = `n=${n} seed=${seed}`;

  // 这两类地块在桥里必须出现在 skipped 里（带 use 和原因）
  const openSkipped = r.skipped.filter((s) => OPEN.includes(s.use));
  check(`${label} 停车场/公园/绿地逐条记账`, openSkipped.length > 0, String(openSkipped.length));
  check(
    `${label} 跳过原因写清楚了`,
    openSkipped.every((s) => s.reason.includes(s.use) && s.reason.includes('不排建筑')),
    openSkipped[0]?.reason ?? ''
  );

  // 它们也不能混进宗地 / 楼位
  const zoningsFromOpen = r.zonings.filter((z) => OPEN.includes(z.use));
  check(`${label} 停车场/公园没产宗地`, zoningsFromOpen.length === 0, String(zoningsFromOpen.length));

  // 统计 TF 在这些地块上到底产出了什么
  for (const patch of model.patches) {
    const lot = (patch.ward as unknown as { lot?: { use: LandUse; bays: number; trees: number; surfaces: { mat: string }[]; props: { kind: string }[] } } | null)?.lot;
    if (!lot || !OPEN.includes(lot.use)) continue;
    const s = (summary[lot.use] ??= { n: 0, bays: 0, trees: 0, mats: {}, props: {}, grass: 0 });
    s.n++;
    s.bays += lot.bays;
    s.trees += lot.trees;
    for (const su of lot.surfaces) {
      s.mats[su.mat] = (s.mats[su.mat] ?? 0) + 1;
      if (su.mat === 'grass' || su.mat === 'grassLight' || su.mat === 'grassDark') s.grass++;
    }
    for (const pr of lot.props) s.props[pr.kind] = (s.props[pr.kind] ?? 0) + 1;
  }
}

console.log('\n=== 对账表（TownForge 侧实际产出）===');
for (const use of OPEN) {
  const s = summary[use];
  if (!s) {
    console.log(`${use}: 本项目这一版没有该用途地块`);
    continue;
  }
  const mats = Object.entries(s.mats).map(([k, v]) => `${k}×${v}`).join(' ');
  const props = Object.entries(s.props).map(([k, v]) => `${k}×${v}`).join(' ');
  console.log(
    `${use}: ${s.n} 块 · 车位 ${s.bays} · 树 ${s.trees} · 草地底 ${s.grass}\n    surfaces: ${mats || '—'}\n    props: ${props || '—'}`
  );
}

// ── 3) 结论断言：谁能直接接管、谁需要先补规则
{
  const park = summary.park;
  const parking = summary.parking;
  const green = summary.green;

  // 停车场：TF 真的排了车位，说明"车位阵列"这套规则存在且尺寸合规 → PM 接管时不用改尺寸
  check('停车场地块排出了车位', !!parking && parking.bays > 0, String(parking?.bays ?? 0));
  // 公园：TF 有草地底（PM 的 park 族不出草地底，台账 Q-008）→ 这是 PM 侧要补的规则
  check('公园地块有草地底（TF 有，PM 缺）', !!park && park.grass > 0, String(park?.grass ?? 0));
  // 绿地：同理
  if (green) check('绿地地块有草/树', green.grass > 0 || green.trees > 0, `grass=${green.grass} trees=${green.trees}`);
}

// ── 4) PM 自己的停车场生成器（`bridge/parking.ts`）：真的排车位，不只是判 ready
{
  const w = PARKING_DIMS.stallW;
  const l = PARKING_DIMS.stallL;
  check('车位宽 = 2.52 m', Math.abs(w - 2.52) < 1e-9, w.toFixed(3));
  check('车位长 = 5.04 m', Math.abs(l - 5.04) < 1e-9, l.toFixed(3));
  check('通道 = 6.30 m（≥ PM driveAisle 5.5）', PARKING_DIMS.aisle >= 5.5, PARKING_DIMS.aisle.toFixed(2));

  let lotsTotal = 0;
  let stallsTotal = 0;
  for (const [n, seed] of [
    [15, 682063530],
    [24, 42],
  ] as [number, number][]) {
    const m = new Model({ nPatches: n, seed, plaza: true });
    m.generate();
    const access = buildAccess(m);
    const pk = buildParking(m, access);
    lotsTotal += pk.stats.lots;
    stallsTotal += pk.stats.stalls;

    const label = `n=${n} seed=${seed}`;
    check(`${label} 有停车场`, pk.stats.lots > 0, String(pk.stats.lots));
    // 门禁：ready 的必须有车位，不 ready 的必须一个位都没有
    const bad = pk.lots.filter((x) => (x.ready && x.stalls === 0) || (!x.ready && x.stalls > 0));
    check(`${label} ready ⇒ 有车位`, bad.length === 0, `${bad.length} 块不一致`);
    // 每个 ready 的车位面积必须正好是 2.52×5.04
    const badArea = pk.lots.filter((x) => x.ready && Math.abs(x.stallAreaM2 - x.stalls * w * l) > 1e-6);
    check(`${label} 车位面积 = 2.52×5.04`, badArea.length === 0, `${badArea.length} 块偏差`);
    // 车位不许比 TF 的少太多（TF 是成熟实现，只是一个量级参照）
    const tfBays = m.patches.reduce((s2, p) => s2 + ((p.ward as unknown as { lot?: { bays?: number } } | null)?.lot?.bays ?? 0), 0);
    check(`${label} 车位数与 TF 同量级（0.4~2.5×）`, pk.stats.stalls > tfBays * 0.4 && pk.stats.stalls < tfBays * 2.5, `${pk.stats.stalls} vs TF ${tfBays}`);
    console.log(`  ${label}: 停车场 ${pk.stats.lots} 块（ready ${pk.stats.ready}）· 车位 ${pk.stats.stalls} · TF 同类 ${tfBays}`);
  }

  // 门禁的反面：没有街道 → 全部不 ready、一个位都不排
  {
    const m = new Model({ nPatches: 15, seed: 682063530, plaza: true });
    m.generate();
    const emptyNet = { edges: [], junctions: [], nodeEdges: new Map(), nodeJunction: new Map(), driveways: [] } as unknown as Parameters<typeof buildAccess>[1];
    const noRoad = buildAccess(m, emptyNet);
    const pk = buildParking(m, noRoad);
    check('无街道 ⇒ 停车场全部不 ready', pk.lots.every((x) => !x.ready), `${pk.lots.filter((x) => x.ready).length} 块 ready`);
    check('无街道 ⇒ 一个车位都不排', pk.stats.stalls === 0, String(pk.stats.stalls));
    console.log(`  无街道：停车场 ${pk.stats.lots} 块 · ready 0 · 车位 0（门禁生效）`);
  }
  console.log(`  合计：停车场 ${lotsTotal} 块 · 车位 ${stallsTotal}`);
}

console.log('\n=== 迁移结论 ===');
console.log('parking：尺寸已合规（2.52×5.04 m / 通道 6.3 m），PM 接管只需补「车位阵列 + 出入口接街」；');
console.log('         注意 parking 是 130 个生成器里**唯一**靠街道影响 ready 的家族（docs/A §0.1），');
console.log('         桥必须先输出 nets 与 access 才能驱动它 —— nets 已就绪，access 还没有。');
console.log('parking（PM 侧已实现）：2.52×5.04 m 车位 + 6.30 m 通道 + 双排阵列；门禁 = 接街车行口（无口一个位都不排）。');
console.log('park/green：PM 的 park 族**不出草地底**（Q-008），接管前必须补这条，否则公园会变成空地；');
console.log('         同时 PM 的 park 族恒 ready，街道怎么改都影响不了它，所以这块的街道规则不承压。');
console.log('      结论：这两类现在**不适合**直接换成 PM 生成 —— 会丢草地底与车位线。');
console.log('            保留 TownForge 的地面与车位（PM 模式已经保留绿化、去掉车位线，需要改回来）。');

console.log(`\n${failures === 0 ? 'PASS' : 'FAIL'} — ${total} 项断言，${failures} 项失败`);
process.exitCode = failures === 0 ? 0 : 1;
