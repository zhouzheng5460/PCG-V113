// 构建三维场景查看器：编译 TS + 打包通道数据 + 出内联单文件。
//
// 数据来源（都是"引擎真的跑出来的"，不是我们编的）：
//   tools/batch/plans/*.plan.json  —— 4 份带宗地（zoning）的真实方案，有楼可看
//   tmp/reverse/*.json             —— 其余真实烘焙产物（surfaces / nets / objects，无宗地）
//   tools/batch/cases/*.json       —— 只取 shape.ring：emit 产物里不含输入轮廓
//
// 跑法：node packages/view3d/build3d.mjs
// 产物：packages/view3d/public/data/scenes.js
//       packages/view3d/public/js/**            （编译后的 ES module）
//       packages/view3d/三维场景.html            （自包含单文件，双击即开）

import { readFileSync, writeFileSync, readdirSync, mkdirSync, existsSync, statSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { spawnSync } from 'node:child_process';
import { buildScript, collect } from '../../tools/packager.mjs';

const here = dirname(fileURLToPath(import.meta.url));
const root = resolve(here, '../..');

const publicDir = join(here, 'public');
const caseDir = join(root, 'tools/batch/cases');
const planDir = join(root, 'tools/batch/plans');
const reverseDir = join(root, 'tmp/reverse');
const dataFile = join(publicDir, 'data/scenes.js');
const vendorThree = join(publicDir, 'js/vendor/three.inline.js');
const outHtml = join(here, '三维场景.html');

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
    console.error('找不到 tsc。手动执行：node "<typescript>/bin/tsc" -p tsconfig.view3d.json');
    return false;
  }
  const r = spawnSync(tsc.cmd, [...tsc.args, '-p', join(root, 'tsconfig.view3d.json')], { encoding: 'utf8', cwd: root });
  if (r.status !== 0) {
    console.error(r.stdout || '', r.stderr || '');
    return false;
  }
  return true;
}

/** 只保留渲染用得到的字段：settings/facts/regions/stats 加起来是大头。 */
function pickChannels(src) {
  const arr = (v) => (Array.isArray(v) ? v : []);
  return {
    surfaces: arr(src.surfaces).map((s) => ({ role: s.role, y: s.y, ring: s.ring })),
    nets: arr(src.nets).map((n) => ({ role: n.role, a: n.a, b: n.b, c: n.c, d: n.d })),
    objects: arr(src.objects).map((o) =>
      // undefined 字段进 JSON 会变成 null，体积白涨；有值才写
      Object.fromEntries(Object.entries({ role: o.role, x: o.x, z: o.z, y: o.y, yaw: o.yaw, scale: o.scale }).filter(([, v]) => v !== undefined)),
    ),
    zoning: arr(src.zoning).map((z) => ({ zoneId: z.zoneId, quad: z.quad })),
  };
}

function buildData() {
  const scenes = [];
  const seen = new Set();

  // 1) 优先放带宗地的方案（有楼可看）
  if (existsSync(planDir)) {
    for (const f of readdirSync(planDir)) {
      if (!f.endsWith('.plan.json')) continue;
      const id = f.replace(/\.plan\.json$/, '');
      const plan = JSON.parse(readFileSync(join(planDir, f), 'utf8'));
      const casePath = join(caseDir, `${id}.json`);
      if (!existsSync(casePath)) continue;
      const cs = JSON.parse(readFileSync(casePath, 'utf8'));
      scenes.push({
        id,
        generator: plan.generator,
        ready: !!plan.ready,
        area: plan.area ?? 0,
        ring: cs.shape.ring,
        plan: pickChannels(plan),
      });
      seen.add(id);
    }
  }

  // 2) 其余真实烘焙产物
  if (existsSync(reverseDir)) {
    for (const f of readdirSync(reverseDir).sort()) {
      if (!f.endsWith('.json') || f.includes('.case.')) continue;
      const id = f.replace(/\.json$/, '');
      if (seen.has(id)) continue;
      const casePath = join(caseDir, `${id}.json`);
      if (!existsSync(casePath)) continue;
      let plan;
      try {
        plan = JSON.parse(readFileSync(join(reverseDir, f), 'utf8'));
      } catch {
        continue;
      }
      if (!plan.surfaces && !plan.nets) continue;
      const cs = JSON.parse(readFileSync(casePath, 'utf8'));
      scenes.push({
        id,
        generator: plan.generator,
        ready: !!plan.ready,
        area: plan.area ?? 0,
        ring: cs.shape.ring,
        plan: pickChannels(plan),
      });
      seen.add(id);
    }
  }

  if (!scenes.length) throw new Error('一个场景都没找到，先跑 npm run oracle:emit 和 reverse');
  mkdirSync(dirname(dataFile), { recursive: true });
  const payload = { generatedAt: new Date().toISOString().slice(0, 19).replace('T', ' '), scenes };
  writeFileSync(dataFile, `window.__SCENES__ = ${JSON.stringify(payload)};\n`, 'utf8');

  const withZoning = scenes.filter((s) => s.plan.zoning.length > 0).length;
  const kb = Math.round(statSync(dataFile).size / 1024);
  console.log(`通道数据 ${scenes.length} 份（带宗地 ${withZoning}）→ public/data/scenes.js  ${kb} KB`);
  return { count: scenes.length, withZoning };
}

function buildHtml() {
  if (!existsSync(vendorThree)) throw new Error('缺 three.inline.js，先跑 node tools/inline-three.mjs');

  const { defs } = collect({
    jsRoot: join(publicDir, 'js'),
    entry: 'view3d/src/app.js',
    readFile: (p) => readFileSync(p, 'utf8'),
    exists: (p) => existsSync(p),
  });
  const appScript = buildScript(defs, 'view3d/src/app.js');

  const three = readFileSync(vendorThree, 'utf8');
  const data = readFileSync(dataFile, 'utf8');
  const css = readFileSync(join(publicDir, 'style.css'), 'utf8');
  const html0 = readFileSync(join(publicDir, 'index.html'), 'utf8');

  const tags = [
    '<script src="./js/vendor/three.inline.js"></script>',
    '<script src="./data/scenes.js"></script>',
    '<script src="./js/view3d/src/app.js"></script>',
  ];
  for (const t of tags) if (!html0.includes(t)) throw new Error(`index.html 缺 ${t}，内联版需要同步改`);

  // 注意：静态内容一律用函数式替换。
  // `String.replace(pattern, string)` 会把替换串里的 `$&`、`$'`、`$1` 当特殊标记展开，
  // 而 three 的源码里真的会蹦出 `$'` 这种序列 —— 一展开就把整份文档插进了脚本中间，
  // 症状是 "SyntaxError: Invalid or unexpected token" 且页面全白。函数式替换不做展开。
  const html = html0
    .replace('<link rel="stylesheet" href="./style.css"/>', () => `<style>\n${css}\n</style>`)
    .replace(tags[0], () => `<script>\n${three}\n</script>`)
    .replace(tags[1], () => `<script>\n${data}\n</script>`)
    .replace(tags[2], () => `<script>\n${appScript}\n</script>`);

  writeFileSync(outHtml, html, 'utf8');
  console.log(
    `内联版 → packages/view3d/三维场景.html  ${(statSync(outHtml).size / 1024 / 1024).toFixed(2)} MB  （${defs.size} 个模块）`,
  );
}

function main() {
  if (!compile()) {
    process.exitCode = 1;
    return;
  }
  buildData();
  buildHtml();
}

main();
