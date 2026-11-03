// 比对 CLI：golden vs native，把差距打成一张表。
//
// 三个用法，按使用频率排：
//
//   node packages/site-gen/src/cli/compare.ts                跑全部已迁移生成器 × 全部用例
//   node packages/site-gen/src/cli/compare.ts parking        只跑一个
//   node packages/site-gen/src/cli/compare.ts --selfcheck    用 golden 冒充 native —— 必须 100 分
//
// `--selfcheck` 是**仪器自检**：如果 golden 跟自己对不上 100 分，那说明度量/比对有 bug，
// 后面所有"差距 40%"的数字都不能信。这一路踩过的 D-029（假失败）、D-047（方向反了却 PASS）
// 都是仪器没自检造成的。

import { writeFileSync, mkdirSync } from 'node:fs';
import { join } from 'node:path';
import { compare, formatReport, type ComparisonReport } from '../compare.ts';
import { goldenBackend, nativeBackend } from '../backends.ts';
import { casesOf, generatorsInCorpus, loadGoldenPlan, loadIndex, loadSiteInput, REPO_ROOT } from '../golden.ts';
import { NATIVE_GENERATORS } from '../generators/index.ts';
import type { Plan } from '../types.ts';

const args = process.argv.slice(2);
const selfCheck = args.includes('--selfcheck');
const jsonOut = args.includes('--json');
const only = args.find((a) => !a.startsWith('-'));

const index = loadIndex();
const corpus = generatorsInCorpus(index);

const goldenAvail = new Set(corpus.map((c) => c.id));
const golden = goldenBackend(goldenAvail);
const native = nativeBackend(NATIVE_GENERATORS);

const targets = only ? [only] : NATIVE_GENERATORS.map((g) => g.id);
const reports: ComparisonReport[] = [];
let skippedNoGolden: string[] = [];
let skippedNoCases = 0;

for (const gen of targets) {
  const cases = casesOf(index, gen);
  if (!cases.length) {
    skippedNoGolden.push(gen);
    continue;
  }
  if (!native.has(gen) && !selfCheck) {
    skippedNoGolden.push(`${gen}（未实现）`);
    continue;
  }
  for (const c of cases) {
    if (!c.ok) {
      skippedNoCases++;
      continue;
    }
    const input = loadSiteInput(c.name);
    const gold = loadGoldenPlan(c.name);
    // selfcheck：拿 golden 当 native，验证"度量 + 比对"本身没 bug
    const cand: Plan = selfCheck ? loadGoldenPlan(c.name) : native.generate(gen, input);
    reports.push(compare(gen, gold, cand));
  }
}

if (jsonOut) {
  const dir = join(REPO_ROOT, 'reports');
  mkdirSync(dir, { recursive: true });
  const f = join(dir, selfCheck ? 'selfcheck.json' : 'compare.json');
  writeFileSync(f, JSON.stringify({ generatedAt: new Date().toISOString(), reports }, null, 1));
  console.log(`写盘 ${f}`);
  process.exit(0);
}

if (selfCheck) {
  const bad = reports.filter((r) => r.score !== 100);
  console.log(`── 仪器自检：golden 对自己 ──`);
  for (const r of reports.slice(0, 3)) console.log(formatReport(r));
  console.log(
    `\n${bad.length === 0 ? 'PASS' : 'FAIL'} — ${reports.length} 份，${reports.length - bad.length} 份 100 分`,
  );
  if (bad.length) {
    for (const r of bad) console.log(`  ! ${r.generator} 只有 ${r.score} 分`);
    process.exitCode = 1;
  }
  process.exit();
}

// 正常模式：每个生成器一张表
for (const gen of targets) {
  const rs = reports.filter((r) => r.generator === gen);
  if (!rs.length) continue;
  for (const r of rs) console.log(formatReport(r));
  const avg = Math.round(rs.reduce((s, r) => s + r.score, 0) / rs.length);
  const counts = { exact: 0, close: 0, off: 0, bad: 0, missing: 0 };
  for (const r of rs) for (const m of r.metrics) if (m.key !== 'issues') counts[m.verdict]++;
  console.log(
    `  ▸ ${gen}：${rs.length} 份 · 均分 ${avg} · exact ${counts.exact} / close ${counts.close} / off ${counts.off} / bad ${counts.bad} / missing ${counts.missing}\n`,
  );
}

// 全局进度：语料里有多少生成器、原生实现了几个
console.log('── 迁移进度 ──');
console.log(`  语料生成器 ${corpus.length} 个 / ${index.count} 份用例`);
console.log(`  原生已实现 ${NATIVE_GENERATORS.length} 个：${NATIVE_GENERATORS.map((g) => g.id).join(', ') || '（无）'}`);
const notImpl = corpus.filter((c) => !native.has(c.id));
console.log(
  `  未实现 ${notImpl.length} 个（按语料厚度排）：${notImpl
    .slice(0, 10)
    .map((c) => `${c.id}(${c.cases})`)
    .join(' · ')}`,
);
if (skippedNoGolden.length) console.log(`  跳过：${skippedNoGolden.join(', ')}`);
if (skippedNoCases) console.log(`  有 ${skippedNoCases} 份用例快照不可用`);
