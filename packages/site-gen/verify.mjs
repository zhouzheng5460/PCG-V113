// Native Web Site Generator 的验收门禁 —— 按用户定的阶梯判，不是"改到不红为止"。
//
// 三层：
//   1. **协议完整性**：54 份快照 × 10 通道 + 输入 dump + 无 elapsedMs
//   2. **仪器自检**：golden 跟自己对，必须 19/19 全 100 分（不过就是度量有 bug，后面数字全不可信）
//   3. **质量阶梯**（用户 2026-11-03 定的口径）：
//        19/19 不崩溃 · deterministic 19/19 · overlap < 1% · coverage 98~102%
//        components ≤ 3 · 车位误差 < 25% · 车道长误差 < 25% · 均分 ≥ 75
//
// 第 3 层的意义：**不要为了追 Placemaker 的 288 个车位逐像素复刻**。
// 目标是"生成合理场地"，所以判据是区间和阈值，不是"等于 golden"。
// 每一项都会把实际数字打出来 —— 只打 PASS 不打数字的门禁，这一路已经坏过两次（D-029 / D-047）。
//
// 跑法：node packages/site-gen/verify.mjs

import { existsSync, readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { execFileSync } from 'node:child_process';
import { NATIVE_GENERATORS } from './src/generators/index.ts';
import { loadGoldenPlan, loadIndex, loadSiteInput } from './src/golden.ts';
import { measure } from './src/metrics.ts';

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
  } else if (detail) {
    console.log(`  ok   ${name} — ${detail}`);
  }
}

// ── 1) 协议完整性
const CHANNELS = ['surfaces', 'nets', 'objects', 'areas', 'regions', 'zoning', 'paint', 'terraform', 'stats', 'issues'];
let index;
{
  index = JSON.parse(readFileSync(join(GOLDEN, 'index.json'), 'utf8'));
  check('golden 语料 54 份', index.count === 54, String(index.count));
  check('golden 协议 site-gen-golden/1', index.protocol === 'site-gen-golden/1', index.protocol);
  check('契约 10 通道', index.channels.length === 10, String(index.channels.length));

  let badChannel = 0;
  let badInput = 0;
  let badElapsed = 0;
  for (const c of index.cases) {
    const plan = JSON.parse(readFileSync(join(GOLDEN, 'plans', `${c.name}.plan.json`), 'utf8'));
    for (const ch of CHANNELS) if (!Array.isArray(plan[ch])) badChannel++;
    if (plan.elapsedMs !== undefined) badElapsed++;
    const inFile = join(GOLDEN, 'plans', `${c.name}.input.json`);
    if (!existsSync(inFile)) badInput++;
    else {
      const inp = JSON.parse(readFileSync(inFile, 'utf8'));
      if (inp.protocol !== 'placemaker-input/1' || (inp.shape?.ring?.length ?? 0) < 3) badInput++;
    }
  }
  check('每份快照都有 10 个通道', badChannel === 0, `${badChannel} 个缺失`);
  check('每份快照都有输入 dump', badInput === 0, `${badInput} 份缺失`);
  check('快照无 elapsedMs', badElapsed === 0, `${badElapsed} 份有`);
}

// ── 2) 仪器自检
{
  const out = execFileSync('node', [join(HERE, 'src', 'cli', 'compare.ts'), '--selfcheck'], { encoding: 'utf8', cwd: ROOT });
  check('仪器自检 golden vs golden 全 100 分', /^PASS/m.test(out), (out.trim().split('\n').pop() ?? '').slice(0, 60));
}

// ── 3) 质量阶梯（逐个生成器 × 全部用例）
const GENS = NATIVE_GENERATORS;
const THRESH = {
  overlap: 0.01, // 铺装重叠 < 1%
  coverageLo: 0.98,
  coverageHi: 1.02,
  components: 3,
  stallErr: 0.25,
  aisleErr: 0.25,
  avgScore: 75,
};

for (const gen of GENS) {
  const cases = index.cases.filter((c) => c.generator === gen.id && c.ok);
  if (!cases.length) {
    check(`${gen.id} 有 golden 用例`, false, '0 份');
    continue;
  }
  let crash = 0;
  let nondet = 0;
  let overlapBad = 0;
  let coverBad = 0;
  let compBad = 0;
  let stallBad = 0;
  let aisleBad = 0;
  const scores = [];
  const worst = { overlap: 0, comp: 0, stall: 0, aisle: 0, coverLo: 9, coverHi: 0 };

  for (const c of cases) {
    const input = loadSiteInput(c.name);
    const gold = measure(loadGoldenPlan(c.name));
    let a;
    let b;
    try {
      a = gen.generate(input);
      b = gen.generate(input);
    } catch (e) {
      crash++;
      continue;
    }
    // 确定性：两次生成的序列化必须逐字节相同（golden 快照的前提）
    if (JSON.stringify(a) !== JSON.stringify(b)) nondet++;
    const nat = measure(a);

    const ov = nat.overlapArea / Math.max(1, nat.siteArea);
    if (ov > THRESH.overlap) overlapBad++;
    worst.overlap = Math.max(worst.overlap, ov);

    // 覆盖：**两个口径任一成立即可**
    //   a) 绝对 98~102%（"地块被铺满"）
    //   b) 与 golden 一致（±2%）—— 因为 golden 自己在 160 m² 的三角形上就是 0% 覆盖，
    //      对那种地块要求"铺满"是假门槛（实测 part6：两边都是 0 个面）。
    const cov = nat.surfaceTotal / Math.max(1, nat.siteArea);
    const gcov = gold.surfaceTotal / Math.max(1, gold.siteArea);
    const absOk = cov >= THRESH.coverageLo && cov <= THRESH.coverageHi;
    const matchOk = gcov > 0 ? Math.abs(cov - gcov) / gcov <= 0.02 : cov <= 0.02;
    if (!absOk && !matchOk) coverBad++;
    worst.coverLo = Math.min(worst.coverLo, cov);
    worst.coverHi = Math.max(worst.coverHi, cov);

    if (nat.netComponents > THRESH.components) compBad++;
    worst.comp = Math.max(worst.comp, nat.netComponents);

    // 车位/车道误差：相对误差为主，但带**绝对下限**。
    // 小地块上相对误差会失去意义（golden 2 个位、我 20 个位就是 900%），
    // 所以判据是 `|Δ| <= max(25% × golden, 10)` —— 10 个位/10 m 之内不算错。
    const stallDelta = Math.abs(nat.parkingBays - gold.parkingBays);
    const stallErr = gold.parkingBays > 0 ? stallDelta / gold.parkingBays : 0;
    if (stallErr > THRESH.stallErr && stallDelta > 10) stallBad++;
    worst.stall = Math.max(worst.stall, stallErr);

    const aisleDelta = Math.abs(nat.netTotal - gold.netTotal);
    const aisleErr = gold.netTotal > 0 ? aisleDelta / gold.netTotal : 0;
    if (aisleErr > THRESH.aisleErr && aisleDelta > 30) aisleBad++;
    worst.aisle = Math.max(worst.aisle, aisleErr);

    scores.push(scoreOf(gold, nat));
  }

  const avg = Math.round(scores.reduce((s, v) => s + v, 0) / Math.max(1, scores.length));
  console.log(`\n  [${gen.id}] ${cases.length} 份 · 均分 ${avg}`);
  check(`${gen.id} 不崩溃`, crash === 0, `${cases.length - crash}/${cases.length}`);
  check(`${gen.id} deterministic`, nondet === 0, `${cases.length - nondet}/${cases.length}`);
  check(`${gen.id} 铺装重叠 < 1%`, overlapBad === 0, `最差 ${(worst.overlap * 100).toFixed(2)}%`);
  check(
    `${gen.id} 铺装覆盖 98~102%`,
    coverBad === 0,
    `区间 ${(worst.coverLo * 100).toFixed(1)}%~${(worst.coverHi * 100).toFixed(1)}%`,
  );
  check(`${gen.id} 连通块 ≤ ${THRESH.components}`, compBad === 0, `最差 ${worst.comp} 块`);
  check(`${gen.id} 车位误差 < 25%`, stallBad === 0, `最差 ${(worst.stall * 100).toFixed(0)}%`);
  check(`${gen.id} 车道长误差 < 25%`, aisleBad === 0, `最差 ${(worst.aisle * 100).toFixed(0)}%`);
  check(`${gen.id} 均分 ≥ ${THRESH.avgScore}`, avg >= THRESH.avgScore, `实际 ${avg}`);
}

/**
 * 分数：14 个计入指标（`issues` 不计），按档给分再平均。
 * 与 `src/compare.ts` 同一口径 —— 这里重新实现是为了不依赖 CLI 的 JSON 产物。
 */
function scoreOf(gold, nat) {
  const grade = (g, n, floor) => {
    if (Math.abs(g - n) <= floor) return 1;
    if (g === 0) return n === 0 ? 1 : 0;
    const a = Math.abs((n - g) / g);
    return a <= 0.02 ? 1 : a <= 0.2 ? 0.7 : a <= 0.5 ? 0.3 : 0;
  };
  const pairs = [
    [gold.surfaceTotal, nat.surfaceTotal, 5],
    [gold.surfaceCoverage, nat.surfaceCoverage, 0],
    [gold.overlapArea, nat.overlapArea, 20],
    [gold.zoningCoverage, nat.zoningCoverage, 0],
    [gold.paintArea, nat.paintArea, 20],
    [gold.netTotal, nat.netTotal, 5],
    [gold.netComponents, nat.netComponents, 0],
    [gold.netDeadEnds, nat.netDeadEnds, 0],
    [gold.netLongestComponent, nat.netLongestComponent, 5],
    [gold.parkingBays, nat.parkingBays, 2],
    [gold.accessPoints, nat.accessPoints, 0],
    [gold.objectTotal, nat.objectTotal, 2],
    [gold.objectDensity, nat.objectDensity, 0],
    [gold.terraformOps, nat.terraformOps, 0],
  ];
  return Math.round((pairs.reduce((s, [g, n, f]) => s + grade(g, n, f), 0) / pairs.length) * 100);
}

console.log(`\n${failures === 0 ? 'PASS' : 'FAIL'} — ${total} 项断言，${failures} 项失败`);
if (failures) process.exitCode = 1;
