// 把夹具打包成编辑器能一次 load 的 bundle.json，并顺带编译 TS。
//
// 为什么需要一个打包步骤（而不是编辑器直接 fetch 54 个文件）：
//   1. 54 次请求在本地 file:// 下跨目录会踩 CORS，单文件没这问题
//   2. `ready` 对照散在 tmp/reverse/（emit 产物，随时可删），顺手固化到
//      tools/batch/ready-baseline.json —— 编辑器从此不依赖 tmp/
//
// 跑法：node packages/editor-web/build.mjs
// 产物：packages/editor-web/public/js/**      （编译后的 ES module）
//       packages/editor-web/public/data/bundle.json
//       tools/batch/ready-baseline.json        （固化，可入库）

import { readFileSync, writeFileSync, readdirSync, mkdirSync, existsSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { spawnSync } from 'node:child_process';
import { buildStandalone } from './standalone.mjs';

const here = dirname(fileURLToPath(import.meta.url));
const root = resolve(here, '../..');

const caseDir = join(root, 'tools/batch/cases');
const planDir = join(root, 'tools/batch/plans');
const reverseDir = join(root, 'tmp/reverse');
const outDir = join(here, 'public/data');
const baselinePath = join(root, 'tools/batch/ready-baseline.json');

/**
 * 找 tsc。本机没有 node_modules（无 npm 网络），所以按三条路探：
 *   1. 项目内 node_modules（若有）
 *   2. 本机已知的 TypeScript 安装（这台机器的既有约定，见根 README）
 *   3. PATH 里的 tsc
 * 找不到就报错并给出手动命令，不静默跳过 —— 跳过会产出"看起来正常但没编译"的目录。
 */
function resolveTsc() {
  const candidates = [
    join(root, 'node_modules/typescript/bin/tsc'),
    'C:/Users/Administrator/WorkBuddy/2026-10-28-14-22-16/node_modules/typescript/bin/tsc',
  ];
  for (const c of candidates) if (existsSync(c)) return { cmd: process.execPath, args: [c] };
  const probe = spawnSync('tsc', ['--version'], { encoding: 'utf8' });
  if (!probe.error && /Version/.test(probe.stdout ?? '')) return { cmd: 'tsc', args: [] };
  return null;
}

function compile() {
  const tsc = resolveTsc();
  if (!tsc) {
    console.error('找不到 tsc。请手动执行：');
    console.error('  node "<typescript>/bin/tsc" -p tsconfig.web.json');
    process.exitCode = 1;
    return false;
  }
  const r = spawnSync(tsc.cmd, [...tsc.args, '-p', join(root, 'tsconfig.web.json')], { encoding: 'utf8', cwd: root });
  if (r.status !== 0) {
    console.error(r.stdout || '', r.stderr || '');
    process.exitCode = 1;
    return false;
  }
  return true;
}


/** Reason.NeedsEntrance → 中文。和 reverse.ts 里那份保持同步。 */
const REASON_CN = {
  'Reason.NeedsEntrance': '需要一个出入口',
  'Reason.NeedsRoad': '需要临街',
  'Reason.NeedsAccess': '需要车辆可达',
  'Reason.TooSmall': '地块太小',
  'Reason.AreaTooSmall': '面积不足',
  'Reason.NoFit': '放不下',
  'Reason.Blocked': '被挡死',
  'Reason.NeedsGround': '需要已有地面',
};

const reasonOf = (key) => {
  if (!key) return '';
  const last = key.split('|').pop() ?? key;
  const i = last.lastIndexOf('Reason.');
  return i >= 0 ? last.slice(i) : last;
};

/** 从 tmp/reverse 读 emit 产物，给还没固化过的地块补 ready 对照。 */
function loadReverseMap() {
  const map = new Map();
  if (!existsSync(reverseDir)) return map;
  for (const f of readdirSync(reverseDir)) {
    if (!f.endsWith('.json') || f.includes('.case.')) continue;
    let d;
    try {
      d = JSON.parse(readFileSync(join(reverseDir, f), 'utf8'));
    } catch {
      continue;
    }
    if (typeof d.ready !== 'boolean') continue;
    map.set(f.replace(/\.json$/, ''), {
      ready: d.ready,
      reason: reasonOf(d.readyKey ?? ''),
      given: d.markers?.given ?? 0,
      auto: d.markers?.auto ?? 0,
    });
  }
  return map;
}

function main() {
  if (!compile()) return;
  if (!existsSync(outDir)) mkdirSync(outDir, { recursive: true });

  const revMap = loadReverseMap();

  // 含宗地的真实方案（4 份）：zoning 是楼位切分的输入，ready 是引擎判定
  const planMap = new Map();
  for (const f of readdirSync(planDir)) {
    if (!f.endsWith('.plan.json')) continue;
    const d = JSON.parse(readFileSync(join(planDir, f), 'utf8'));
    const id = f.replace(/\.plan\.json$/, '');
    planMap.set(id, {
      generator: d.generator,
      seed: d.seed,
      ready: !!d.ready,
      markerInfo: d.markers ?? null,
      zoning: (d.zoning ?? []).map((z) => ({ zoneId: z.zoneId, quad: z.quad })),
    });
  }

  const cases = [];
  for (const f of readdirSync(caseDir).sort()) {
    if (!f.endsWith('.json')) continue;
    const id = f.replace(/\.json$/, '');
    const raw = JSON.parse(readFileSync(join(caseDir, f), 'utf8'));

    // ready 对照优先取 plan.json（含 zoning 的那 4 份），否则取 reverse 的 emit 产物。
    const plan = planMap.get(id);
    let csharp = null;
    if (plan) {
      csharp = {
        ready: plan.ready,
        reason: plan.ready ? '' : 'Reason.NeedsEntrance',
        given: plan.markerInfo?.given ?? 0,
        auto: plan.markerInfo?.auto ?? 0,
      };
    } else if (revMap.has(id)) {
      csharp = revMap.get(id);
    }

    cases.push({
      id,
      generator: raw.generator,
      seed: raw.seed,
      ring: raw.shape.ring,
      streets: raw.streets,
      markers: raw.markers ?? [],
      settings: raw.settings ?? {},
      csharp,
      zoning: plan?.zoning ?? null,
    });
  }

  const bundle = {
    generatedAt: new Date().toISOString().slice(0, 19).replace('T', ' '),
    cases,
  };
  writeFileSync(join(outDir, 'bundle.json'), JSON.stringify(bundle), 'utf8');

  // 固化 ready 基线：编辑器与后续脚本都不再依赖 tmp/
  const baseline = {
    note: '由 packages/editor-web/build.mjs 生成。每份地块在 C# 引擎里的 ready 判定（含 markers.given/auto）。',
    generatedAt: bundle.generatedAt,
    entries: Object.fromEntries(cases.filter((c) => c.csharp).map((c) => [c.id, c.csharp])),
  };
  writeFileSync(baselinePath, JSON.stringify(baseline, null, 1), 'utf8');

  const withCs = cases.filter((c) => c.csharp).length;
  const withZoning = cases.filter((c) => c.zoning).length;
  const kb = (JSON.stringify(bundle).length / 1024).toFixed(0);
  console.log(`打包 ${cases.length} 份地块 → ${kb} KB`);
  console.log(`  含 C# ready 对照 ${withCs}｜含宗地 ${withZoning}`);
  console.log(`  基线固化 → tools/batch/ready-baseline.json`);
  console.log(`  产物 → packages/editor-web/public/data/bundle.json`);

  // 顺手产出内联版：单文件、无需服务、双击即开（原因见 standalone.mjs 抬头）
  try {
    buildStandalone();
  } catch (e) {
    console.error('内联版打包失败（http 版仍可用）：' + e.message);
    process.exitCode = 1;
  }
  void REASON_CN;
}

main();
