// Native Web Site Generator 的门禁 —— **两层**，按用户 2026-11-03 定的口径。
//
// 层 1 · Eligibility fidelity（资格保真）：native 的 ready 是否与 oracle 一致，
//        以及"该不该排车位"的判断是否与 oracle 的实际产出对得上。
//        **这一层比平均分重要** —— 它一旦稳定，以后所有 Site Generator 都能统一成
//        `checkReady() → generate() → validate()`。
//
// 层 2 · Generation fidelity（生成保真）：**只对 eligible 的用例**比几何与数量。
//        不 eligible 的用例只断言"不许硬干"（车位必须为 0）。
//
// 为什么必须分层：`part3/part7/part2` 那批碎地的差异不是"停车几何算错"，而是
// "该不该生成"的决策差。把两层混在一起，就只能靠放宽误差去兼容 ——
// 而那会把"golden 覆盖 43% / native 44%"这种两边都不合理的状态判成 PASS。
// 用户的原话：**Golden 是行为参考，不应该成为几何错误的免责条款。**
//
// 跑法：node packages/site-gen/verify.mjs

import { existsSync, readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { execFileSync } from 'node:child_process';
import { NATIVE_GENERATORS } from './src/generators/index.ts';
import { loadGoldenPlan, loadIndex, loadSiteInput } from './src/golden.ts';
import { measure } from './src/metrics.ts';
import { checkParkingReady } from './src/eligibility.ts';
import { plazaGate } from './src/plaza-gates.ts';

const HERE = dirname(fileURLToPath(import.meta.url));
const ROOT = join(HERE, '..', '..');
const GOLDEN = join(ROOT, 'tools', 'golden');

let failures = 0;
let total = 0;
function check(name, cond, detail = '') {
  total++;
  if (!cond) {
    failures++;
    console.log(`  FAIL ${name} — ${detail}`);
  } else {
    console.log(`  ok   ${name}${detail ? ' — ' + detail : ''}`);
  }
}

/**
 * **cohort 是显式标注的**（`tools/golden/cohorts.json`），不是面积阈值算出来的。
 *
 * 用户 2026-11-03 的拍板："不要让 `PRIMARY_MIN_AREA` 成为长期语义…… 面积阈值可以暂时用来
 * 复现实验，但最终最好把 19 个 case 显式标注 cohort。因为以后很可能出现
 * `1200 m² 规则矩形 → 完全值得做停车`、`3000 m² 极窄碎片 → 仍然只是 stress`。"
 *
 * 所以：`PRIMARY_MIN_AREA` 已删除，改为读 cohort。要改判某个用例，改 JSON，不改代码。
 * 两类本来是什么：
 *
 *   · `primary` —— 真实地块（≥12000 m²）。golden 在这 4 例上**自洽**：
 *     车位密度 41~50 m²/位（14527→288、16614→403、12491→276、13123→268）。
 *     密度/车道/覆盖保真在这里判。
 *   · `stress` —— `p90-parking.partN` 那 15 个碎块（160~1677 m²）。
 *     它们是把 p90 的地块切成 15 份的产物，golden 在这里的密度是
 *     **112~453 m²/位**（比真地块稀疏 2~9 倍，且彼此毫无一致性），
 *     还在 1219 m² 上给出 0 个车位 —— 那是退化回退，不是一套设计。
 *     所以这批只验**稳健性**（不崩 / 确定性 / 不硬干 / 不重叠 / 覆盖），
 *     不拿它们的密度当保真目标。
 *
 * 判据依据见 docs/H §5 的两张密度表。**如果不同意这个分法，把 PRIMARY_MIN_AREA 设为 0
 * 就退回"全部 19 例都判密度"**，那样均分是 68、且 6 例密度不达标。
 */
const COHORTS = JSON.parse(readFileSync(join(GOLDEN, 'cohorts.json'), 'utf8'));
const cohortOf = (name) => COHORTS.cases[name]?.cohort ?? 'primary';

/**
 * 指标分三层（用户 2026-11-03 拍板 B）：
 *
 * | 层 | 指标 | 作用 |
 * | --- | --- | --- |
 * | **Fidelity Gate** | productiveAisle · stalls · surface coverage · overlap · connectedComponents | 负责 PASS/FAIL |
 * | **Quality Diagnostic** | deadAisleRatio · stallsPerAisleMeter · danglingEnds · fragmentation | 只打印，不判 |
 * | **Oracle Observation** | netTotal | 只显示 Δ，不判 |
 *
 * 为什么把 `netTotal` 降级：它同时奖励**有效车道**和**无效空转车道**，表达不了我们想保真的设计能力。
 * p0 是干净证据 —— golden 1124 m 里 18% 是空转，native 968 m 里只有 10%；
 * 为了把 968 补到 1124 而制造 150 m 空转，模型反而变差。
 * **降级不等于藏起来**：Δ 照样显示，只是明确声明"这种差异不是我们希望消除的差异"。
 *
 * `danglingEnds` 同理：p0 的 6 个悬空端 = 2 个出入口/服务端 + 4 个外圈行端，
 * 这是**合法的梯形停车网络**，不是断网。为了复制 golden 的闭环要付出 +35% 车道 + 占地 + 降低效率，
 * 是明显的负优化。真正该 gate 的是"能不能通"：连通块数 + 出入口可达 + 行带可达。
 */

/** 阈值（用户定的阶梯） */
const T = {
  overlap: 0.005,
  coverageLo: 0.98,
  coverageHi: 1.02,
  components: 3,
  stallRel: 0.25,
  stallAbs: 4,
  aisleRel: 0.25,
  aisleAbs: 20,
  avgScore: 75,
};

// ── 0) 协议完整性 + 仪器自检
const CHANNELS = ['surfaces', 'nets', 'objects', 'areas', 'regions', 'zoning', 'paint', 'terraform', 'stats', 'issues'];
const index = JSON.parse(readFileSync(join(GOLDEN, 'index.json'), 'utf8'));
{
  let badChannel = 0;
  let badInput = 0;
  let badElapsed = 0;
  for (const c of index.cases) {
    const plan = JSON.parse(readFileSync(join(GOLDEN, 'plans', `${c.name}.plan.json`), 'utf8'));
    for (const ch of CHANNELS) if (!Array.isArray(plan[ch])) badChannel++;
    if (plan.elapsedMs !== undefined) badElapsed++;
    const inFile = join(GOLDEN, 'plans', `${c.name}.input.json`);
    if (!existsSync(inFile)) badInput++;
  }
  // 真实烘焙至少 54 份（合成用例是补充，不能替代它）；总数 = bake + synth
  const bake = index.bySource?.bake ?? index.count;
  const synth = index.bySource?.synth ?? 0;
  check(
    'golden 语料完整 / 10 通道 / 有输入 dump / 无 elapsedMs',
    bake >= 54 && index.count === bake + synth && index.channels.length === 10 && badChannel === 0 && badInput === 0 && badElapsed === 0,
    `${index.count} 份（真实 ${bake} + 合成 ${synth}）· 缺通道 ${badChannel} · 缺输入 ${badInput} · 有耗时 ${badElapsed}`,
  );

  const out = execFileSync('node', [join(HERE, 'src', 'cli', 'compare.ts'), '--selfcheck'], { encoding: 'utf8', cwd: ROOT });
  check('仪器自检 golden vs golden 全 100 分', /^PASS/m.test(out), (out.trim().split('\n').pop() ?? '').slice(0, 40));

  const boolOut = execFileSync('node', [join(HERE, 'test', 'boolean.test.ts')], { encoding: 'utf8', cwd: ROOT });
  check('布尔原语 26 项', /^PASS/m.test(boolOut), (boolOut.trim().split('\n').pop() ?? '').slice(0, 40));

  const splitOut = execFileSync('node', [join(HERE, 'test', 'split-region.test.ts')], { encoding: 'utf8', cwd: ROOT });
  check('区域分解原语 18 项', /^PASS/m.test(splitOut), (splitOut.trim().split('\n').pop() ?? '').slice(0, 40));

  // 原语复用硬门禁：生成器不许自建第二套几何、不许绕开 boolean.ts
  const primOut = execFileSync('node', [join(HERE, 'src', 'cli', 'primitives.ts')], { encoding: 'utf8', cwd: ROOT });
  check('原语复用（无第二套几何）', /^PASS/m.test(primOut), (primOut.trim().split('\n').find((l) => l.startsWith('PASS') || l.startsWith('FAIL')) ?? '').slice(0, 44));
}

// ── 层 1 + 层 2
for (const gen of NATIVE_GENERATORS.filter((g) => g.id === 'parking')) {
  const cases = index.cases.filter((c) => c.generator === gen.id && c.ok);
  if (!cases.length) {
    check(`${gen.id} 有 golden 用例`, false, '0 份');
    continue;
  }

  let crash = 0;
  let nondet = 0;
  let readyMismatch = 0;
  let eligibilityMismatch = 0;
  let overlapBad = 0;
  let coverBad = 0;
  let compBad = 0;
  let stallBad = 0;
  let aisleBad = 0;
  let forcedBad = 0;
  let reachBad = 0;
  let unreachableBad = 0;
  const scores = [];
  const primaryScores = [];
  const primaryNames = [];
  const stressScores = [];
  const w = { overlap: 0, comp: 0, stall: 0, aisle: 0, productive: 0, coverLo: 9, coverHi: 0 };
  const notes = [];
  const diag = [];
  const aisleObs = [];

  for (const c of cases) {
    const input = loadSiteInput(c.name);
    const gold = loadGoldenPlan(c.name);
    const gm = measure(gold);
    const oracleReady = gold.ready;
    const oracleHasStalls = (gold.stats?.find((s) => s.key === 'Stat.Stalls')?.value ?? -1) >= 0;

    let a;
    let b;
    try {
      a = gen.generate(input);
      b = gen.generate(input);
    } catch (e) {
      crash++;
      notes.push(`${c.name}: 崩 ${String(e).slice(0, 60)}`);
      continue;
    }
    if (JSON.stringify(a) !== JSON.stringify(b)) nondet++;

    // 层 1 · 资格保真
    if (a.ready !== oracleReady) {
      readyMismatch++;
      notes.push(`${c.name}: ready ${a.ready} vs oracle ${oracleReady}`);
    }
    const elig = checkParkingReady(input);
    if (elig.feasible !== oracleHasStalls) {
      eligibilityMismatch++;
      notes.push(`${c.name}: 可行 ${elig.feasible} vs oracle 有车位 ${oracleHasStalls}`);
    }
    const isPrimary = cohortOf(c.name) === 'primary';

    // 层 2 · 生成保真
    const nm = measure(a);
    if (!elig.feasible) {
      if (nm.parkingBays > 0) {
        forcedBad++;
        notes.push(`${c.name}: 不可行却排了 ${nm.parkingBays} 个位`);
      }
      // 不可行用例不计 coverage / 车位 / 车道（用户口径："不计算 surface coverage"）
      scores.push(scoreOf(gm, nm));
      continue;
    }

    const ov = nm.overlapArea / Math.max(1, nm.siteArea);
    if (ov > T.overlap) overlapBad++;
    w.overlap = Math.max(w.overlap, ov);

    const cov = nm.surfaceTotal / Math.max(1, nm.siteArea);
    if (cov < T.coverageLo || cov > T.coverageHi) coverBad++;
    w.coverLo = Math.min(w.coverLo, cov);
    w.coverHi = Math.max(w.coverHi, cov);

    if (nm.netComponents > T.components) compBad++;
    w.comp = Math.max(w.comp, nm.netComponents);
    // 真正的连通性判据：**能不能通**，不是端点长得像不像 Placemaker
    if (nm.netComponents > 0 && nm.netLongestComponent / Math.max(1, nm.netTotal) < 0.5) reachBad++;
    // 行位可达：至少 90% 的车位要被某条车道服务到
    if (nm.parkingBays > 0 && nm.stallsServed / nm.parkingBays < 0.9) unreachableBad++;

    // 密度保真只在 primary 上判（理由见 PRIMARY_MIN_AREA 的注释）
    if (isPrimary) {
      const ds = Math.abs(nm.parkingBays - gm.parkingBays);
      const rs = gm.parkingBays > 0 ? ds / gm.parkingBays : 0;
      if (rs > T.stallRel && ds > T.stallAbs) {
        stallBad++;
        notes.push(`${c.name}: 车位 ${gm.parkingBays}→${nm.parkingBays}（Δ${ds}）`);
      }
      w.stall = Math.max(w.stall, rs);

      // 总车道长降级为 **Oracle Observation**（不判 PASS/FAIL，只记 Δ）
      const da = Math.abs(nm.netTotal - gm.netTotal);
      const ra = gm.netTotal > 0 ? da / gm.netTotal : 0;
      w.aisle = Math.max(w.aisle, ra);
      aisleObs.push(`${c.name.replace('city24-s8-', '')} ${gm.netTotal.toFixed(0)}→${nm.netTotal.toFixed(0)} (${ra >= 0 ? '+' : ''}${((da / Math.max(1, gm.netTotal)) * 100).toFixed(0)}%)`);

      // **Fidelity Gate 改判 productiveAisle**（服务到车位的车道长度）
      const dp = Math.abs(nm.productiveAisle - gm.productiveAisle);
      const rp = gm.productiveAisle > 0 ? dp / gm.productiveAisle : 0;
      if (rp > T.aisleRel && dp > T.aisleAbs) {
        aisleBad++;
        notes.push(`${c.name}: 有效车道 ${gm.productiveAisle.toFixed(0)}→${nm.productiveAisle.toFixed(0)}（Δ${dp.toFixed(0)}m）`);
      }
      w.productive = Math.max(w.productive, rp);
      // 诊断：悬空端与空转占比（不判）
      diag.push(
        `${c.name.replace('city24-s8-', '')} 悬空端 ${gm.netDeadEnds}→${nm.netDeadEnds} · 空转 ${(gm.deadAisleRatio * 100).toFixed(0)}%→${(nm.deadAisleRatio * 100).toFixed(0)}% · 位/m ${gm.stallsPerAisleMeter.toFixed(2)}→${nm.stallsPerAisleMeter.toFixed(2)}`,
      );
      primaryScores.push(scoreOf(gm, nm));
      primaryNames.push(c.name.replace('city24-s8-', ''));
    }
    stressScores.push(scoreOf(gm, nm));

    scores.push(scoreOf(gm, nm));
  }

  const mean = (a) => Math.round(a.reduce((s, v) => s + v, 0) / Math.max(1, a.length));
  const avg = mean(scores);
  const avgPrimary = mean(primaryScores);
  const eligibleN = cases.length - eligibilityMismatch;
  console.log(
    `\n  [${gen.id}] ${cases.length} 份 · eligible ${eligibleN} · 均分 ${avg}` +
      `（真地块 ${primaryScores.length} 例 **${avgPrimary}** · 碎块 ${stressScores.length} 例 ${mean(stressScores)}）`,
  );

  check(`${gen.id} 不崩溃`, crash === 0, `${cases.length - crash}/${cases.length}`);
  check(`${gen.id} deterministic`, nondet === 0, `${cases.length - nondet}/${cases.length}`);
  check(`${gen.id} ready 与 oracle 一致`, readyMismatch === 0, `${cases.length - readyMismatch}/${cases.length}`);
  check(`${gen.id} 可行性判定与 oracle 产出一致`, eligibilityMismatch === 0, `${cases.length - eligibilityMismatch}/${cases.length}`);
  check(`${gen.id} 不可行用例不硬干`, forcedBad === 0, `${forcedBad} 例硬排了车位`);
  check(`${gen.id} 铺装重叠 < 0.5%`, overlapBad === 0, `最差 ${(w.overlap * 100).toFixed(2)}%`);
  check(
    `${gen.id} eligible 覆盖 98~102%`,
    coverBad === 0,
    `区间 ${(w.coverLo * 100).toFixed(1)}%~${(w.coverHi * 100).toFixed(1)}%`,
  );
  check(`${gen.id} 连通块 ≤ ${T.components}`, compBad === 0, `最差 ${w.comp} 块`);
  check(`${gen.id} 主连通网占比 ≥ 50%（能通）`, reachBad === 0, `${reachBad} 例不成网`);
  check(`${gen.id} 车位可达性 ≥ 90%`, unreachableBad === 0, `${unreachableBad} 例可达性不足`);
  check(`${gen.id} 车位误差 ≤ max(25%, 4)`, stallBad === 0, `最差 ${(w.stall * 100).toFixed(0)}%`);
  check(`${gen.id} 有效车道误差 ≤ max(25%, 20m)`, aisleBad === 0, `最差 ${(w.productive * 100).toFixed(0)}%`);
  // ── Quality Diagnostic（不判）
  if (diag.length) {
    console.log('  [诊断·不判]');
    for (const dd of diag.slice(0, 4)) console.log('    · ' + dd);
  }
  // ── Oracle Observation（不判）
  if (aisleObs.length) {
    console.log('  [观测·不判] 总车道 golden→native：' + aisleObs.join(' · '));
  }
  check(`${gen.id} primary 均分 ≥ ${T.avgScore}`, avgPrimary >= T.avgScore, `实际 ${avgPrimary}（${primaryScores.length} 例）`);
  // 全部 19 例的均分**只作观测**，不进 release gate（用户 2026-11-03：
  // "保留 all-19 overall score，但只作为观测指标，不作为 release gate"）。
  // 留着的意义是算法退化时仍然看得见，而不是把 stress 排除掉之后眼不见为净。
  console.log(`  [观测] ${gen.id} all-${scores.length} 均分 ${avg}（不判）`);

  // primary 逐例分数：用户要求 "primary 4/4 全部 >80"
  const perCase = primaryScores.map((v, i) => `${primaryNames[i]}=${v}`).join(' ');
  const below80 = primaryScores.filter((v) => v < 80).length;
  check(`${gen.id} primary 逐例 ≥ 80`, below80 === 0, `${perCase}（${below80} 例低于 80）`);

  if (notes.length) {
    console.log('  明细：');
    for (const n of notes.slice(0, 12)) console.log('    · ' + n);
    if (notes.length > 12) console.log(`    … 另有 ${notes.length - 12} 条`);
  }
}

// ── Plaza（P3.1）：docs/I 定的 7 条 fidelity 判据
{
  const gen = NATIVE_GENERATORS.find((g) => g.id === 'plaza');
  const cases = index.cases.filter((c) => c.generator === 'plaza' && c.ok);
  if (!gen || !cases.length) {
    check('plaza 已注册且有 golden 用例', false, `${cases.length} 份`);
  } else {
    let crash = 0;
    let nondet = 0;
    let coverBad = 0;
    let overlapBad = 0;
    let obsBad = 0;
    let compBad = 0;
    let largestBad = 0;
    let accessBad = 0;
    let objBad = 0;
    const scores = [];
    const w = { cov: 0, ov: 0, obs: 0, comp: 0, largest: 1, acc: 1, obj: 1 };
    for (const c of cases) {
      const input = loadSiteInput(c.name);
      let a;
      let b;
      try {
        a = gen.generate(input);
        b = gen.generate(input);
      } catch {
        crash++;
        continue;
      }
      if (JSON.stringify(a) !== JSON.stringify(b)) nondet++;
      const g = plazaGate(input, a);
      scores.push(g.score);
      if (g.usableCoverage < 0.98 || g.usableCoverage > 1.02) coverBad++;
      if (g.surfaceOverlap > 0.005) overlapBad++;
      if (g.obstacleOverlap > 1e-6) obsBad++;
      if (g.pavingComponents < 0 || g.pavingComponents > 2) compBad++;
      if (g.largestPavedRatio < 0.9) largestBad++;
      if (g.accessReachable < 0.999) accessBad++;
      if (g.objectsOutsideObstacles < 0.999) objBad++;
      w.cov = Math.max(w.cov, Math.abs(g.usableCoverage - 1));
      w.ov = Math.max(w.ov, g.surfaceOverlap);
      w.obs = Math.max(w.obs, g.obstacleOverlap);
      w.comp = Math.max(w.comp, g.pavingComponents);
      w.largest = Math.min(w.largest, g.largestPavedRatio);
      w.acc = Math.min(w.acc, g.accessReachable);
      w.obj = Math.min(w.obj, g.objectsOutsideObstacles);
    }
    const avg = Math.round(scores.reduce((a2, v) => a2 + v, 0) / Math.max(1, scores.length));
    console.log(`\n  [plaza] ${cases.length} 份 · 均分 ${avg}`);
    check('plaza 不崩溃', crash === 0, `${cases.length - crash}/${cases.length}`);
    check('plaza deterministic', nondet === 0, `${cases.length - nondet}/${cases.length}`);
    check('plaza usable 覆盖 98~102%', coverBad === 0, `最大偏差 ${(w.cov * 100).toFixed(2)}%`);
    check('plaza 铺装重叠 < 0.5%', overlapBad === 0, `最差 ${(w.ov * 100).toFixed(2)}%`);
    check('plaza 障碍重叠 = 0', obsBad === 0, `最差 ${(w.obs * 100).toFixed(3)}%`);
    check('plaza 铺装连通块 ≤ 2', compBad === 0, `最差 ${w.comp} 块`);
    check('plaza 最大铺装块 ≥ 90%', largestBad === 0, `最差 ${(w.largest * 100).toFixed(0)}%`);
    check('plaza 出入口可达 100%', accessBad === 0, `最差 ${(w.acc * 100).toFixed(0)}%`);
    check('plaza 物件全在障碍物外', objBad === 0, `最差 ${(w.obj * 100).toFixed(0)}%`);
    check('plaza primary 均分 ≥ 75', avg >= 75, `实际 ${avg}`);
  }
}


/**
 * 分数 = **Fidelity Gate 指标**的均值（按档给分）。
 *
 * 刻意**不含**已降级的量：`netTotal`（观测）、`netDeadEnds` / `netLongest`（诊断）。
 * 否则"分数"会继续偷偷用旧判据，等于降级没发生。与 `src/compare.ts` 同口径。
 */
function scoreOf(g, n) {
  const grade = (a, b, floor) => {
    if (Math.abs(a - b) <= floor) return 1;
    if (a === 0) return b === 0 ? 1 : 0;
    const x = Math.abs((b - a) / a);
    return x <= 0.02 ? 1 : x <= 0.2 ? 0.7 : x <= 0.5 ? 0.3 : 0;
  };
  const pairs = [
    [g.surfaceTotal, n.surfaceTotal, 5],
    [g.surfaceCoverage, n.surfaceCoverage, 0],
    [g.overlapArea, n.overlapArea, 20],
    [g.zoningCoverage, n.zoningCoverage, 0],
    [g.paintArea, n.paintArea, 20],
    [g.productiveAisle, n.productiveAisle, 20],
    [g.netComponents, n.netComponents, 0],
    [g.parkingBays, n.parkingBays, 2],
    [g.accessPoints, n.accessPoints, 0],
    [g.objectTotal, n.objectTotal, 2],
    [g.objectDensity, n.objectDensity, 0],
    [g.terraformOps, n.terraformOps, 0],
  ];
  return Math.round((pairs.reduce((s, [a, b, f]) => s + grade(a, b, f), 0) / pairs.length) * 100);
}

console.log(`\n${failures === 0 ? 'PASS' : 'FAIL'} — ${total} 项断言，${failures} 项失败`);
if (failures) process.exitCode = 1;
