// 把 54 份真实烘焙输入各自跑一遍 C# `--emit`，落成 **golden Plan 快照**。
//
// 为什么必须有这一步：`tools/batch/ready-baseline.json` 只存了 `ready/given/auto` 三个标量，
// 而"原生 TS 生成器是否等价于 Placemaker"要比的是**通道内容**（铺了多少沥青、多少车位、
// 园路多长、物件多密）。没有快照就只能比一个布尔值 —— 那等于没比（D-057）。
//
// 快照落盘之后，C# 就从"生成链的一环"降级成"只在需要重烘时才跑的 oracle"：
//   平时：node packages/site-gen/src/cli/compare.ts   → 只读快照，零 dotnet
//   重烘：node tools/golden/emit.mjs                  → 才需要 dotnet
//
// 跑法：
//   node tools/golden/emit.mjs               全部重烘
//   node tools/golden/emit.mjs --only parking 只烘 generator=parking 的
//   node tools/golden/emit.mjs --check       只校验现有快照是否齐全/协议对不对

import { existsSync, mkdirSync, readFileSync, readdirSync, unlinkSync, writeFileSync } from 'node:fs';
import { execFileSync } from 'node:child_process';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const HERE = dirname(fileURLToPath(import.meta.url));
const ROOT = join(HERE, '..', '..');
const CASES = join(ROOT, 'tools', 'batch', 'cases');
/** 合成用例（我们自己造的，用于 golden 语料里没有足够样本的生成器）。与真实烘焙分开记。 */
const SYNTH = join(ROOT, 'tools', 'batch', 'cases-synth');
/** 合成用例（我们自己造的，用于 golden 语料里没有足够样本的生成器）。与真实烘焙分开记。 */
const OUT = join(HERE, 'plans');
const DLL = join(ROOT, 'tools', 'oracle', 'driver', 'bin', 'Release', 'net6.0', 'PlacemakerDriver.dll');

/** Plan 的通道清单 —— 冻结契约：少一个通道就是协议破了 */
const CHANNELS = ['surfaces', 'nets', 'objects', 'areas', 'regions', 'zoning', 'paint', 'terraform', 'stats', 'issues'];
const PROTOCOL = 'placemaker-plan/1';

const args = process.argv.slice(2);
const only = args.includes('--only') ? args[args.indexOf('--only') + 1] : null;
const checkOnly = args.includes('--check');

// 两处都扫：真实烘焙（tools/batch/cases）+ 合成（tools/batch/cases-synth）。
// **来源要记进 index**：合成用例是我们按设计意图造的，论证力和真实烘焙不一样，
// 以后的报告必须能区分，不能混为一谈。
const scan = (dir, source) =>
  existsSync(dir)
    ? readdirSync(dir)
        .filter((f) => f.endsWith('.json'))
        .sort()
        .map((f) => ({ dir, f, source }))
    : [];
const files = [...scan(CASES, 'bake'), ...scan(SYNTH, 'synth')];
const rows = [];
let emitted = 0;

/**
 * 归一化：**把墙钟时间剔掉**。
 *
 * `elapsedMs` 每次 emit 都不一样（实测同一份输入 14.345 vs 17.451 ms），
 * 留着它快照就永远"不一致"，diff 也全是噪声。它不是契约的一部分，是测量值。
 */
function normalize(plan) {
  const { elapsedMs, ...rest } = plan;
  void elapsedMs;
  return JSON.stringify(rest, null, 1) + '\n';
}

/**
 * emit 一次 → { plan, input } 两段归一化文本。
 *
 * input 是 C# **解析之后**的输入（`<out>.input.json`，见 `Emit.SerializeInput`）。
 * 存它的理由：原生 TS 实现必须吃**同一份**输入，否则比的是两个解析器。
 */
function emitOnce(caseFile, outFile) {
  execFileSync('dotnet', [DLL, '--emit', caseFile, outFile], { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] });
  const plan = normalize(JSON.parse(readFileSync(outFile, 'utf8')));
  const inFile = outFile.replace(/\.json$/i, '.input.json');
  const input = existsSync(inFile) ? normalize(JSON.parse(readFileSync(inFile, 'utf8'))) : null;
  try {
    unlinkSync(inFile);
  } catch {
    /* 没写出来就算了，下面会判 */
  }
  return { plan, input };
}

for (const entry of files) {
  const { dir, f, source } = entry;
  const name = f.replace(/\.json$/, '');
  const caseFile = join(dir, f);
  const planFile = join(OUT, `${name}.plan.json`);
  const raw = JSON.parse(readFileSync(caseFile, 'utf8'));
  const generator = raw.generator ?? raw.settings?.generator ?? '?';

  if (only && generator !== only) continue;

  if (checkOnly) {
    if (!existsSync(planFile)) {
      rows.push({ name, generator, source, ok: false, why: '快照缺失' });
      continue;
    }
    const plan = JSON.parse(readFileSync(planFile, 'utf8'));
    const missing = CHANNELS.filter((c) => !Array.isArray(plan[c]));
    const inFile = join(OUT, `${name}.input.json`);
    if (!existsSync(inFile)) {
      rows.push({ name, generator, source, ok: false, why: '缺输入快照' });
      continue;
    }
    rows.push({
      name,
      generator,
      source,
      ok: plan.protocol === PROTOCOL && missing.length === 0 && plan.elapsedMs === undefined,
      why: missing.length
        ? `缺通道 ${missing.join(',')}`
        : plan.protocol !== PROTOCOL
          ? `协议 ${plan.protocol}`
          : plan.elapsedMs !== undefined
            ? '快照里还留着 elapsedMs（不可复现）'
            : '',
      channels: Object.fromEntries(CHANNELS.map((c) => [c, plan[c]?.length ?? 0])),
      ready: plan.ready,
    });
    continue;
  }

  if (!existsSync(DLL)) {
    console.error(`找不到 ${DLL}，先跑 npm run oracle:build`);
    process.exit(1);
  }
  mkdirSync(OUT, { recursive: true });

  // 同一份输入 emit 两次必须得到**逐字节相同**的快照，否则它不能当基准
  const tmp = join(OUT, `.tmp-${name}.json`);
  const A = emitOnce(caseFile, tmp);
  const B = emitOnce(caseFile, tmp);
  try {
    unlinkSync(tmp);
  } catch {
    /* 删不掉不影响结果 */
  }
  const plan = JSON.parse(A.plan);
  const missing = CHANNELS.filter((c) => !Array.isArray(plan[c]));
  // 输入也必须逐字节稳定：它同样会被存成基准
  const stable = A.plan === B.plan && A.input === B.input;
  const hasInput = A.input !== null;
  const ok = stable && hasInput && plan.protocol === PROTOCOL && missing.length === 0;
  writeFileSync(planFile, A.plan);
  if (hasInput) writeFileSync(join(OUT, `${name}.input.json`), A.input);
  rows.push({
    name,
    generator,
    source,
    ok,
    why: !stable
      ? '两次 emit 不一致'
      : !hasInput
        ? '缺输入 dump（--emit 没写 .input.json）'
        : plan.protocol !== PROTOCOL
          ? `协议 ${plan.protocol}`
          : missing.length
            ? `缺通道 ${missing.join(',')}`
            : '',
    ready: plan.ready,
    channels: Object.fromEntries(CHANNELS.map((c) => [c, plan[c].length])),
    input: hasInput ? { ring: JSON.parse(A.input).shape.ring.length, nets: JSON.parse(A.input).nearbyNets.length, settings: Object.keys(JSON.parse(A.input).settings ?? {}).length, markers: JSON.parse(A.input).markers.length } : null,
    bytes: A.plan.length,
  });
  emitted++;
  process.stdout.write(`\r  已烘 ${emitted}`);
}
process.stdout.write('\n');

// 索引：只存"契约层"的事实（通道条数 + ready），不存几何
const index = {
  protocol: 'site-gen-golden/1',
  bySource: rows.reduce((m, r) => ((m[r.source] = (m[r.source] ?? 0) + 1), m), {}),
  bySource: rows.reduce((m, r) => ((m[r.source] = (m[r.source] ?? 0) + 1), m), {}),
  note: '由 tools/golden/emit.mjs 生成。plans/ 是 C# 的权威输出，用来做原生 TS 实现的对照基准。',
  generatedAt: new Date().toISOString().slice(0, 19).replace('T', ' '),
  channels: CHANNELS,
  count: rows.length,
  byGenerator: rows.reduce((m, r) => ((m[r.generator] = (m[r.generator] ?? 0) + 1), m), {}),
  cases: rows,
};
writeFileSync(join(HERE, 'index.json'), JSON.stringify(index, null, 1) + '\n');

const bad = rows.filter((r) => !r.ok);
console.log(`\n${bad.length === 0 ? 'PASS' : 'FAIL'} — ${rows.length} 份，${rows.length - bad.length} 份可用`);
for (const r of bad) console.log(`  ! ${r.name} (${r.generator}) ${r.why}`);
if (bad.length) process.exitCode = 1;
