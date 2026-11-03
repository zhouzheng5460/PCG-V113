// Native Web Site Generator 的验收门禁。
//
// 三层，缺一不可：
//   1. **仪器自检**：golden 跟自己对，必须 100 分。不过 → 度量/比对有 bug，后面所有数字都不可信。
//   2. **协议完整性**：54 份快照每份都要有 10 个通道 + 输入 dump + 无 elapsedMs。
//   3. **不回退**：每个已迁移生成器的均分不得低于记录下来的基线（数值写在本文件里）。
//
// 第 3 条是关键。这一路吃过太多次"改了之后看起来更好，其实某一项掉回去了"的亏
// （D-047 树密度方向反了还报 PASS、D-052 断言随模型变旧而失效）。基线写死在这里，
// 想涨可以，跌了就是红的。
//
// 跑法：node packages/site-gen/verify.mjs

import { existsSync, readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { execFileSync } from 'node:child_process';

const HERE = dirname(fileURLToPath(import.meta.url));
const ROOT = join(HERE, '..', '..');
const CLI = join(HERE, 'src', 'cli', 'compare.ts');
const GOLDEN = join(ROOT, 'tools', 'golden');

let failures = 0;
let total = 0;
function check(name, cond, detail = '') {
  total++;
  if (!cond) {
    failures++;
    console.log(`  FAIL ${name} ${detail}`);
  }
}

// ── 1) 协议完整性：10 个通道 + 输入 dump + 无 elapsedMs
{
  const CHANNELS = ['surfaces', 'nets', 'objects', 'areas', 'regions', 'zoning', 'paint', 'terraform', 'stats', 'issues'];
  const index = JSON.parse(readFileSync(join(GOLDEN, 'index.json'), 'utf8'));
  check('golden 语料有 54 份', index.count === 54, String(index.count));
  check('golden 协议是 site-gen-golden/1', index.protocol === 'site-gen-golden/1', index.protocol);
  check('通道清单是 10 条', index.channels.length === 10, String(index.channels.length));

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
      if (inp.protocol !== 'placemaker-input/1' || !Array.isArray(inp.shape?.ring) || inp.shape.ring.length < 3) badInput++;
    }
  }
  check('每份快照都有 10 个通道', badChannel === 0, `${badChannel} 个缺失`);
  check('每份快照都有输入 dump', badInput === 0, `${badInput} 份缺失`);
  check('快照里没有 elapsedMs（不可复现的量不许进基准）', badElapsed === 0, `${badElapsed} 份有`);
}

// ── 2) 仪器自检
{
  const out = execFileSync('node', [CLI, '--selfcheck'], { encoding: 'utf8', cwd: ROOT });
  const m = out.match(/PASS — (\d+) 份/);
  check('仪器自检（golden 对自己）全 100 分', /^PASS/m.test(out) && Boolean(m), out.trim().split('\n').pop() ?? '');
}

// ── 3) 不回退：已迁移生成器的均分基线
const BASELINE = {
  parking: 55,
};

{
  const out = execFileSync('node', [CLI, '--json'], { encoding: 'utf8', cwd: ROOT });
  void out;
  const rep = JSON.parse(readFileSync(join(ROOT, 'reports', 'compare.json'), 'utf8'));
  const byGen = new Map();
  for (const r of rep.reports) {
    const arr = byGen.get(r.generator) ?? [];
    arr.push(r);
    byGen.set(r.generator, arr);
  }
  for (const [gen, base] of Object.entries(BASELINE)) {
    const rs = byGen.get(gen);
    if (!rs || !rs.length) {
      check(`${gen} 有比对结果`, false, '没跑出来');
      continue;
    }
    const avg = Math.round(rs.reduce((s, r) => s + r.score, 0) / rs.length);
    check(`${gen} 均分不低于基线 ${base}`, avg >= base, `实际 ${avg}`);
    console.log(`  ${gen}: ${rs.length} 份 · 均分 ${avg}（基线 ${base}）`);
  }
}

console.log(`\n${failures === 0 ? 'PASS' : 'FAIL'} — ${total} 项断言，${failures} 项失败`);
if (failures) process.exitCode = 1;
