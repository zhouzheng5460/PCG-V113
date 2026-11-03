// 桥的验收：TownForge 的街区 → Placemaker 的宗地（米）→ 我们的楼位，逐条对账。
//
// 这个测试的全部意义是：**"以米为准"必须能被量出来**，不能只是注释里的一句话。
// 所以每条断言都指向一个具体数字：
//   · 单位真源只有一个（UNIT_M 只在一处定义）
//   · 宗地四边形与街区的拟合比（凹街区会被外接矩形撑大，失真必须可读）
//   · 楼位进深落在 8 m 模数上的比例
//   · 楼高 = 层数 × 层高，且层高等于 KIND_SPECS
//   · 层数落在该用途的区间里
//
// 跑法：node node_modules/tsx/dist/cli.mjs test/bridge.test.ts

import { readdirSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { Model } from '../src/town/model';
import { buildScene } from '../src/scene/build';
import { buildBridge, bridgeSummary } from '../src/bridge/placemaker';
import { summarizeAssets, SPLIT_MIN_FLOORS } from '../src/bridge/assets';
import { area as ptArea, intersect as ptIntersect } from '../src/town/zones';
import { Pt } from '../src/core/geom';
import { planHeight, zoneKindOf } from '../src/bridge/zone';
import { KIND_SPECS } from '../../../packages/slots/src/cut.ts';
import { UNIT_M, meters, sqMeters, u } from '../src/core/units';

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

// ── 1) 单位真源：UNIT_M 只允许在 core/units.ts 里定义
{
  const files: string[] = [];
  const walk = (dir: string): void => {
    for (const e of readdirSync(dir, { withFileTypes: true })) {
      const p = join(dir, e.name);
      if (e.isDirectory()) walk(p);
      else if (e.name.endsWith('.ts')) files.push(p);
    }
  };
  walk('src');
  const defined = files.filter((f) => /export const UNIT_M\s*=/.test(readFileSync(f, 'utf8')));
  check('UNIT_M 只有一个定义处', defined.length === 1, defined.join(','));
  check('定义处是 core/units.ts', defined[0]?.replace(/\\/g, '/').endsWith('src/core/units.ts'), defined[0] ?? '');
  const ratio = meters(1);
  check('1 单位 = 7 m', ratio === 7, String(ratio));
  check('u/meters 互逆', Math.abs(meters(u(28)) - 28) < 1e-9);
  check('面积换算与长度一致', Math.abs(sqMeters(1) - 49) < 1e-9);
}

// ── 宗地规模的聚合统计（跨规模 × 跨种子）——
// 为什么不用"逐种子"当门槛：实测 `long` 策略在 682063530 上最大宗地从 2688 涨到 5376、
// CV 从 0.34 涨到 0.64，但在 998877 上反而从 1920 掉到 1600。逐种子卡死就会变成
// "只在某几个种子上成立"的假门槛（D-029 的教训：门禁必须打数字，而且要打得准）。
const parcelAgg: { max: number; cv: number; composed: number; slots: number }[] = [];

// ── 2) 三个规模各跑一遍，桥必须稳定
for (const n of [10, 15, 24]) {
  for (const seed of [7, 42]) {
    const model = new Model({ nPatches: n, seed, plaza: true });
    model.generate();
    const r = buildBridge(model);
    const label = `n=${n} seed=${seed}`;

    check(`${label} 有街区`, r.stats.patches > 0, String(r.stats.patches));
    check(`${label} 有宗地`, r.stats.zonings > 0, String(r.stats.zonings));
    check(`${label} 有楼位`, r.stats.slots > 0, String(r.stats.slots));

    // 拟合比：低于 MIN_FIT_RATIO 的街区**不产宗地**（硬套会把楼排到街区外），
    // 所以这里断言两件事：进来的宗地都够方；被挡在外面的街区不能太多。
    check(`${label} 四边形拟合中位 ≥ 0.85`, r.stats.fitMed >= 0.85, r.stats.fitMed.toFixed(3));
    check(`${label} 进入的宗地都 ≥ 0.6`, r.stats.fitMin >= 0.6 - 1e-9, r.stats.fitMin.toFixed(3));
    const skipShare = r.stats.skippedByShape / Math.max(1, r.stats.patches);
    check(`${label} 因形状被跳过 ≤ 15%`, skipShare <= 0.15, (skipShare * 100).toFixed(0) + '%');
    // 被跳过的街区必须逐条写明原因（不许"少了几块没人知道"）
    check(
      `${label} 跳过都有原因`,
      r.skipped.every((x) => x.reason.length > 0),
      String(r.skipped.length)
    );

    // 覆盖率：楼位基底 / 街区面积。真实城市的沿街开发大约 30%~55%，
    // 第一版把整个街区当宗地时是 ~100%（城市糊成一片灰），这条就是防它复发的。
    // 覆盖率带放宽到 15%~65%：按深度分带 + 最小面积之后，宗地只覆盖街区 60~70%，
  // 建筑再按密度缩，落点就在 20%~35%（这正是"有院子"的表现，见 D-050）
  check(`${label} 楼位覆盖率 15%~65%`, r.stats.coverShare >= 0.15 && r.stats.coverShare <= 0.65, (r.stats.coverShare * 100).toFixed(0) + '%');

    // 米制合理性：进深"整格或单排铺满到边"都合法（台账 D-019）
    check(`${label} 进深贴近 8 m 格 ≥ 95%`, r.stats.depthNearModule >= 0.95, (r.stats.depthNearModule * 100).toFixed(0) + '%');
    check(`${label} 平均进深 8~32 m`, r.stats.depthAvg >= 8 && r.stats.depthAvg <= 32, r.stats.depthAvg.toFixed(1));
    const widths = r.slots.map((s) => s.width);
    check(`${label} 最小面宽 ≥ 6 m`, Math.min(...widths) >= 6 - 1e-6, String(Math.min(...widths).toFixed(2)));
    check(`${label} 最大面宽 ≤ 60 m`, Math.max(...widths) <= 60 + 1e-6, String(Math.max(...widths).toFixed(2)));

    // 楼高 = 层数 × 层高，层高 = KIND_SPECS，层数在区间内
    for (const s of r.slots) {
      const spec = KIND_SPECS[s.kind];
      if (Math.abs(s.heightM - s.floors * s.floorHeight) > 1e-9) {
        check(`${label} 楼高=层数×层高`, false, `${s.slotId} ${s.heightM} vs ${s.floors}×${s.floorHeight}`);
        break;
      }
      if (s.floorHeight !== spec.floorHeight) {
        check(`${label} 层高取自 KIND_SPECS`, false, `${s.kind} ${s.floorHeight} vs ${spec.floorHeight}`);
        break;
      }
      if (s.floors < spec.floors[0] || s.floors > spec.floors[1]) {
        check(`${label} 层数在区间内`, false, `${s.kind} ${s.floors} ∉ [${spec.floors}]`);
        break;
      }
    }
    check(`${label} 层高/层数一致`, true);

    // 宗地四边形必须是米制、四边闭合、方向一致
    for (const z of r.zonings) {
      if (z.quad.length !== 4) {
        check(`${label} 宗地是四边形`, false);
        break;
      }
      const [a, b, c, d] = z.quad;
      const side = (p: [number, number], qq: [number, number]): number => Math.hypot(p[0] - qq[0], p[1] - qq[1]);
      // 一个街区通常切出 2~4 条沿街进深带，`quadAreaM2` 是它们的和
      let area = 0;
      for (const q of z.strips) {
        let a2 = 0;
        for (let i = 0; i < 4; i++) {
          const p = q[i];
          const r2 = q[(i + 1) % 4];
          a2 += p[0] * r2[1] - r2[0] * p[1];
        }
        area += Math.abs(a2 / 2);
      }
      if (Math.abs(area - z.quadAreaM2) > Math.max(1e-6, z.quadAreaM2 * 1e-3)) {
        check(`${label} 宗地面积为米²`, false, `${area.toFixed(1)} vs ${z.quadAreaM2.toFixed(1)}`);
        break;
      }
      // 边长是米：一条边 10~200 m，不可能是 1~2（那说明还在用世界单位）
      const sides = [side(a, b), side(b, c), side(c, d), side(d, a)];
      if (sides.some((x) => x < 2 || x > 400)) {
        check(`${label} 宗地边长是米`, false, sides.map((x) => x.toFixed(1)).join('/'));
        break;
      }
    }
    check(`${label} 宗地几何合法`, true);

    // ── 建筑不许压路：每栋楼的 foot 必须几乎 100% 落在街区里
    {
      let worst = 1;
      let worstId = '';
      let outside = 0;
      for (const sl of r.slots) {
        const patch = model.patches[sl.patchIndex];
        if (!patch) continue;
        const block = patch.shape.map((q) => new Pt(q.x * UNIT_M, q.y * UNIT_M));
        const foot = (sl.foot ?? sl.quad).map(([x, z]) => new Pt(x, z));
        const fa = Math.abs(ptArea(foot));
        if (fa < 1e-6) continue;
        const rings = ptIntersect(foot, block, 1e-6);
        let inside = 0;
        for (const rr of rings) inside += Math.abs(ptArea(rr));
        const ratio = Math.min(1, inside / fa);
        if (ratio < 0.98) outside++;
        if (ratio < worst) {
          worst = ratio;
          worstId = sl.slotId;
        }
      }
      check(`${label} 楼不压路（foot 在街区内 ≥98%）`, outside === 0, `${outside} 栋越界，最差 ${worstId} ${(worst * 100).toFixed(1)}%`);
    }

    // ── 用地：11 类必须全覆盖（保底补位），且不是"每格一块"
    {
      check(`${label} 用地 11 类全覆盖`, r.landUseReport.distinct === 11, `${r.landUseReport.distinct} 类`);
      // 宗地数应当明显少于"每格一块"：按深度分带 + 最小面积之后，一块地是若干整列合并出来的
      const zonable = model.patches.filter((p) => p.withinCity && (p.ward as unknown as { lot?: { use?: string } } | null)?.lot).length;
      check(`${label} 宗地是合并出来的（≤ 街区数 ×1.2）`, r.zonings.length <= zonable * 1.2, `${r.zonings.length} vs ${zonable}`);
      // 每块宗地至少 4 格（256 m²）且至少 2 列（16 m 宽）
      let tooSmall = 0;
      for (const z of r.zonings) {
        for (const q of z.strips) {
          const a = Math.abs((q[1][0] - q[0][0]) * (q[3][1] - q[0][1]) - (q[1][1] - q[0][1]) * (q[3][0] - q[0][0]));
          const cellsN = a / 64;
          const w1 = Math.hypot(q[1][0] - q[0][0], q[1][1] - q[0][1]);
          if (cellsN < 3.5 || w1 < 15.5) tooSmall++;
        }
      }
      check(`${label} 宗地满足最小面积/最小列宽`, tooSmall === 0, `${tooSmall} 块过小`);
    }

    // ── 资产槽位：楼位 → 体块的拆分数必须自洽
    {
      const a = summarizeAssets(r.slots);
      check(`${label} 体块数 = 楼位 + 拆分数`, a.solids === a.slots + a.split, `${a.solids} vs ${a.slots}+${a.split}`);
      const tall = r.slots.filter((x) => x.floors >= SPLIT_MIN_FLOORS).length;
      check(`${label} 拆分的都是高楼`, a.split === tall, `${a.split} vs ${tall}`);
      check(`${label} 模数命中率是 0~1 的数`, a.modularRate >= 0 && a.modularRate <= 1);
      check(`${label} 模数命中数 ≤ 楼位数`, a.modular <= a.slots);
      check(`${label} 模数命中率不至于为 0`, a.modularRate > 0.05, (a.modularRate * 100).toFixed(1) + '%');
      console.log(
        `      资产槽位：模数命中 ${(a.modularRate * 100).toFixed(0)}%（${a.modular}/${a.slots}）· 拆塔楼 ${a.split}/${a.slots}`
      );
    }

    // ── 宗地必须是**格子矩形**（C# ZoneGrid 的几何）：边长都是 8 m 的倍数、深 ≤ 6 格
    {
      let badEdge = 0;
      let badDepth = 0;
      for (const z of r.zonings) {
        for (const q of z.strips) {
          const sides = [0, 1].map((i) => Math.hypot(q[(i + 1) % 4][0] - q[i][0], q[(i + 1) % 4][1] - q[i][1]));
          for (const sd of sides) {
            if (Math.abs(sd / 8 - Math.round(sd / 8)) > 1e-6) badEdge++;
          }
          // 深边（两条短边之一）不超过 6 格
          const minSide = Math.min(...sides);
          if (minSide > 6 * 8 + 1e-6) badDepth++;
        }
      }
      check(`${label} 宗地边长都是 8 m 的倍数`, badEdge === 0, `${badEdge} 条边不是`);
      check(`${label} 宗地进深 ≤ 6 格（48 m）`, badDepth === 0, `${badDepth} 块过深`);
    }

    // ── 宗地要"够大、够不一样、不是一次分割就成"（回应"地块偏小 / 城市像复印机"）
    //
    // 三条下限都来自 C#，不是我自己定的：
    //   · 宽 ≥ 2 格：`Core/Layers/Common/StreetShops.cs:317` —— "Two cells wide at least:
    //     the game blocks a lone column of cells (ZoneGuard.ReduceDepths)"；
    //   · 深 ≥ 2 格：`Core/Build/ZoneGuard.cs:71` —— `public const int MinRows = 2;`
    //   · 相邻同深同向的并成一块：`ReduceDepths` 的注释 —— "Neighbour blocks of the same road
    //     continue the row (GetDepth past the end of the block)"。
    {
      let tooNarrow = 0;
      let tooShallow = 0;
      let overlapPairs = 0;
      const areas: number[] = [];
      const quads: { q: [number, number][]; pi: number }[] = [];
      for (const z of r.zonings) {
        for (const q of z.strips) {
          const w = Math.hypot(q[1][0] - q[0][0], q[1][1] - q[0][1]);
          const h = Math.hypot(q[2][0] - q[1][0], q[2][1] - q[1][1]);
          if (Math.min(w, h) / 8 < 2 - 1e-6) {
            if (w <= h) tooNarrow++;
            else tooShallow++;
          }
          areas.push(Math.abs(ptArea(q.map(([x, y]) => new Pt(x, y)))));
          quads.push({ q: q as unknown as [number, number][], pi: z.patchIndex });
        }
      }
      check(`${label} 宗地宽 ≥ 2 格（16 m）`, tooNarrow === 0, `${tooNarrow} 块过窄`);
      check(`${label} 宗地深 ≥ 2 格（16 m）`, tooShallow === 0, `${tooShallow} 块过浅`);

      // 同一街区里两块宗地的中心不许重合（合并算错就会叠在一起）
      for (let i = 0; i < quads.length; i++) {
        for (let j = i + 1; j < quads.length; j++) {
          if (quads[i].pi !== quads[j].pi) continue;
          const c = (qq: [number, number][]): [number, number] => [
            qq.reduce((s, p) => s + p[0], 0) / 4,
            qq.reduce((s, p) => s + p[1], 0) / 4,
          ];
          const [ax, ay] = c(quads[i].q);
          const [bx, by] = c(quads[j].q);
          if (Math.hypot(ax - bx, ay - by) < 1e-6) overlapPairs++;
        }
      }
      check(`${label} 同街区宗地中心不重合`, overlapPairs === 0, `${overlapPairs} 对`);

      // 不是"一块地 = 一次分割"：必须有相当比例的宗地是拼出来的
      check(
        `${label} 有拼接宗地（不是全都一次分割）`,
        r.stats.composedShare > 0,
        `${(r.stats.composedShare * 100).toFixed(1)}%`,
      );
      // 面积要有分布（上一轮 p50 == p90，全是 1152 m²）
      check(`${label} 宗地面积变异系数 > 0.25（不是复印机）`, r.stats.parcelAreaCv > 0.25, r.stats.parcelAreaCv.toFixed(2));
      // 中位不是判据（上一轮也是 1152），**最大值**才是：合并成功的标志是出现真正的大地块
      check(`${label} 宗地面积中位 ≥ 640 m²（10 格）`, r.stats.parcelAreaMedM2 >= 640, r.stats.parcelAreaMedM2.toFixed(0));
      check(`${label} 已有大地块（≥ 1600 m² = 25 格）`, r.stats.parcelAreaMaxM2 >= 1600, r.stats.parcelAreaMaxM2.toFixed(0));
      parcelAgg.push({
        max: r.stats.parcelAreaMaxM2,
        cv: r.stats.parcelAreaCv,
        composed: r.stats.composedShare,
        slots: r.stats.slots,
      });
      console.log(
        `      宗地面积 m²：中位 ${r.stats.parcelAreaMedM2.toFixed(0)} · 最大 ${r.stats.parcelAreaMaxM2.toFixed(0)} · CV ${r.stats.parcelAreaCv.toFixed(2)} · 拼接 ${(r.stats.composedShare * 100).toFixed(0)}% · 提取 ${r.stats.rectsRaw}→合并 ${r.stats.rectsMerged}→过筛 ${r.stats.rectsFinal}`,
      );
    }

    // ── 层数必须由**规划参数**算出来，而不是拿实测楼高反推
    {
      const byUse = new Map<string, number[]>(); // 现在是按 kind 分组
      let badRange = 0;
      let badFar = 0;
      for (const z of r.zonings) {
        const spec = KIND_SPECS[z.kind];
        if (z.plan.floors < spec.floors[0] || z.plan.floors > spec.floors[1]) badRange++;
        if (z.plan.far > 0 && Math.abs(z.plan.farActual - z.plan.far) / z.plan.far > 0.1) badFar++;
        const arr = byUse.get(z.kind) ?? [];
        arr.push(z.plan.floors);
        byUse.set(z.kind, arr);
      }
      check(`${label} 层数落在用途区间内`, badRange === 0, `${badRange} 块越界`);
      check(`${label} 实际容积率 ≈ 规划容积率（±10%）`, badFar === 0, `${badFar} 块偏离`);
      // 同一用途层数应当一致（容积率/密度是常数 → far/density 与地块大小无关）
      const varying = [...byUse.entries()].filter(([, arr]) => new Set(arr).size > 1).map(([u]) => u);
      check(`${label} 同 kind 层数稳定`, varying.length === 0, varying.join(','));
      // 中心商务区必须比住宅高、住宅比工业高（顺序 sanity）
      const med = (a: number[]): number => (a.length ? a.slice().sort((x, y) => x - y)[Math.floor(a.length / 2)] : NaN);
      const f = (u: string): number => med(byUse.get(u) ?? []);
      if (byUse.has('cbd') && byUse.has('residential')) {
        check(`${label} CBD 层数 > 住宅层数`, f('cbd') > f('residential'), `${f('cbd')} vs ${f('residential')}`);
      }
      console.log(
        `      层数（按用途）：${[...byUse.entries()].map(([u, a]) => `${u}=${med(a)}F`).join(' · ')}`
      );
    }

    console.log(`  ${label}: ${bridgeSummary(r)}`);
    console.log(
      `      宗地 ${r.stats.zonings} · 楼位 ${r.stats.slots}（TownForge ${r.stats.tfBuildings} 栋）· 钳制 ${(r.stats.clampedShare * 100).toFixed(0)}% · 覆盖 ${(r.stats.coverShare * 100).toFixed(0)}% · 纯模数 ${(r.stats.depthOnModule * 100).toFixed(0)}%`,
    );
  }
}

// ── 宗地规模的跨种子聚合：这才是"城市整体不再像复印机"的判据
{
  const mean = (f: (x: (typeof parcelAgg)[number]) => number): number =>
    parcelAgg.reduce((s, x) => s + f(x), 0) / Math.max(1, parcelAgg.length);
  const maxAvg = mean((x) => x.max);
  const cvAvg = mean((x) => x.cv);
  const composedAvg = mean((x) => x.composed);
  check('聚合：最大宗地均值 ≥ 3500 m²', maxAvg >= 3500, maxAvg.toFixed(0));
  check('聚合：面积变异系数均值 ≥ 0.4', cvAvg >= 0.4, cvAvg.toFixed(2));
  check('聚合：拼接宗地均值 > 1%', composedAvg > 0.01, (composedAvg * 100).toFixed(1) + '%');
  console.log(
    `  聚合（${parcelAgg.length} 组）：最大宗地均值 ${maxAvg.toFixed(0)} m² · CV 均值 ${cvAvg.toFixed(2)} · 拼接均值 ${(composedAvg * 100).toFixed(1)}%`,
  );
}

// ── 3) 场景出口：楼高全部等于"层数 × 层高"
for (const n of [15, 24]) {
  const model = new Model({ nPatches: n, seed: 11, plaza: true });
  model.generate();
  const scene = buildScene(model);
  const hsM = scene.solids.map((s) => meters(s.h));
  let bad = 0;
  let layerM: number[] = [];
  for (const s of scene.solids) {
    const hM = meters(s.h);
    // 找最贴近的层高（3 / 4 / 4.5 / 8 m）
    const cands = [3, 4, 4.5, 8];
    const best = cands.map((fh) => ({ fh, err: Math.abs(hM - Math.round(hM / fh) * fh) })).sort((a, b) => a.err - b.err)[0];
    if (best.err > 1e-6) bad++;
    else layerM.push(best.fh);
  }
  check(`n=${n} 楼高都是整层`, bad === 0, `${bad} 栋不是整层`);
  check(`n=${n} 有楼`, scene.solids.length > 0, String(scene.solids.length));
  check(`n=${n} 楼高中位 6~60 m`, q(hsM, 0.5) >= 6 && q(hsM, 0.5) <= 60, q(hsM, 0.5).toFixed(1));
  const byFh: Record<string, number> = {};
  for (const fh of layerM) byFh[`${fh}m`] = (byFh[`${fh}m`] ?? 0) + 1;
  console.log(`  n=${n} 场景 ${scene.solids.length} 栋 · 楼高 ${q(hsM, 0).toFixed(1)}/${q(hsM, 0.5).toFixed(1)}/${q(hsM, 1).toFixed(1)} m · 层高分布 ${JSON.stringify(byFh)}`);
}

// ── 4) 用途映射：9 种 LandUse 的每一条都有明确去处
{
  const cases: [string, number, string | null][] = [
    ['cbd', 40, 'officeHigh'],
    ['cbd', 15, 'commercialHigh'],
    ['commercial', 8, 'commercialLow'],
    ['commercial', 20, 'commercialHigh'],
    ['residential', 9, 'residentialRow'],
    ['residential', 18, 'residentialMedium'],
    ['residential', 30, 'residentialHigh'],
    ['industrial', 10, 'industrial'],
    ['civic', 12, 'officeLow'],
    ['park', 0, null],
    ['parking', 0, null],
    ['green', 0, null],
    ['reserved', 0, null],
  ];
  for (const [use, h, want] of cases) {
    const got = zoneKindOf(use as never, h);
    check(`用途 ${use}@${h}m → ${want ?? '不产宗地'}`, got === want, String(got));
  }
  // 规整函数自身
  const p = planHeight(18.4, 'residentialMedium');
  check('规整 18.4 m → 6 层 × 3 m', p.floors === 6 && p.heightM === 18, `${p.floors}×${p.floorHeight}`);
  const low = planHeight(3.5, 'residentialRow');
  check('规整 3.5 m 被抬到层数下限', low.floors === KIND_SPECS.residentialRow.floors[0] && low.clamped, String(low.floors));
}

console.log(`\n${failures === 0 ? 'PASS' : 'FAIL'} — ${total} 项断言，${failures} 项失败`);
process.exitCode = failures === 0 ? 0 : 1;
