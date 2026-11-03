// 构建编辑器：编译 TS → 打成单文件 HTML（双击即开，不需要任何服务）。
//
// 为什么不做 dev server 就够：这台机器上 node 进程会随会话一起死（见 docs/F 的踩坑记录），
// 起服务最后还是要"打开一个地址"，而用户要的是"点开就能用"。所以单文件是主交付形态。
//
// 跑法：node packages/editor-app/build.mjs

import { readFileSync, writeFileSync, mkdirSync, existsSync, statSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { spawnSync } from 'node:child_process';
import { buildScript, collect } from '../../tools/packager.mjs';

const here = dirname(fileURLToPath(import.meta.url));
const root = resolve(here, '../..');
const publicDir = join(here, 'public');
const outHtml = join(here, '编辑器.html');

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
    console.error('找不到 tsc。手动执行：node "<typescript>/bin/tsc" -p tsconfig.editor.json');
    return false;
  }
  const r = spawnSync(tsc.cmd, [...tsc.args, '-p', join(root, 'tsconfig.editor.json')], { encoding: 'utf8', cwd: root });
  if (r.status !== 0) {
    console.error(r.stdout || '', r.stderr || '');
    return false;
  }
  return true;
}

function bundle() {
  const { defs } = collect({
    jsRoot: join(publicDir, 'js'),
    entry: 'editor-app/src/main.js',
    readFile: (p) => readFileSync(p, 'utf8'),
    exists: (p) => existsSync(p),
  });
  const script = buildScript(defs, 'editor-app/src/main.js');

  const three = ''; // 2D 编辑器不需要 three；3D 页签接入时再按需内联（见 docs/G §5 G3）
  const css = readFileSync(join(publicDir, 'style.css'), 'utf8');
  const html0 = readFileSync(join(publicDir, 'index.html'), 'utf8');

  const tag = '<script src="./js/editor-app/src/main.js"></script>';
  if (!html0.includes(tag)) throw new Error('index.html 缺入口 script 标签，内联版需要同步改');

  // 函数式替换：`String.replace(p, s)` 会把 s 里的 `$&`/`$'`/`$1` 当特殊标记展开，
  // 源码里出现这种序列就会把整份文档插进脚本中间（docs/F §1.2 记过这个坑）。
  let html = html0
    .replace('<link rel="stylesheet" href="./style.css"/>', () => `<style>\n${css}\n</style>`)
    .replace(tag, () => `<script>\n${three}${script}\n</script>`);

  mkdirSync(dirname(outHtml), { recursive: true });
  writeFileSync(outHtml, html, 'utf8');
  console.log(`单文件 → packages/editor-app/编辑器.html  ${(statSync(outHtml).size / 1024).toFixed(0)} KB（${defs.size} 个模块）`);
}

function main() {
  if (!compile()) {
    process.exitCode = 1;
    return;
  }
  bundle();
}

main();
