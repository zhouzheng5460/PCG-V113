// 把 vendor/townforge 的 vite 产物打成**单文件 HTML**（双击即开，不需要服务）。
//
// 为什么不清空 dist 直接用 vite 的 index.html：
//   vite 默认输出 `<script type="module" src="./assets/index-xxx.js">`，
//   在 `file://` 下浏览器会拒绝加载**外部** module 脚本（CORS）。
//   但**内联**的 module 脚本没有外部请求，所以把 JS/CSS 全塞进 HTML 就能双击打开。
//   这也和本项目其它产物（规则验证器 / 三维场景 / 编辑器）保持同一种交付形态。
//
// 唯一会失效的是 `?selftest=1`（它 `import('./selftest-*.js')` 懒加载那块），
// 那条路径是自动化测试用的，不是用户功能 —— 需要时用 vite dev 或 http 版。

import { readFileSync, writeFileSync, existsSync, readdirSync, statSync, mkdirSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { spawnSync } from 'node:child_process';

const here = dirname(fileURLToPath(import.meta.url));
const root = resolve(here, '../..');
const appDir = join(root, 'vendor/townforge');
const distDir = join(appDir, 'dist');
const outHtml = join(here, '编辑器.html');

function hasDeps() {
  return existsSync(join(appDir, 'node_modules/vite/bin/vite.js'));
}

function viteBuild() {
  if (!hasDeps()) {
    console.log('· 没有 node_modules，跳过 vite 构建，直接用现有 dist/');
    return true;
  }
  const r = spawnSync(
    process.execPath,
    [join(appDir, 'node_modules/vite/bin/vite.js'), 'build', '--base=./'],
    { cwd: appDir, encoding: 'utf8' },
  );
  const out = `${r.stdout ?? ''}${r.stderr ?? ''}`;
  if (r.status !== 0) {
    console.error(out.split('\n').slice(-20).join('\n'));
    return false;
  }
  const built = out.split('\n').filter((l) => l.includes('dist/'));
  console.log('· vite build ok');
  for (const l of built) console.log('  ' + l.trim());
  return true;
}

function inline() {
  const indexHtml = readFileSync(join(distDir, 'index.html'), 'utf8');

  // 1) CSS
  const cssTag = /<link rel="stylesheet"[^>]*href="\.\/([^"]+\.css)"[^>]*>/;
  const cssMatch = cssTag.exec(indexHtml);
  if (!cssMatch) throw new Error('dist/index.html 里找不到 CSS link（vite 输出格式变了？）');
  const css = readFileSync(join(distDir, cssMatch[1]), 'utf8');

  // 2) 主 JS：必须是**内联** module
  const jsTag = /<script type="module"[^>]*src="\.\/([^"]+\.js)"[^>]*><\/script>/;
  const jsMatch = jsTag.exec(indexHtml);
  if (!jsMatch) throw new Error('dist/index.html 里找不到入口 module 脚本');
  const js = readFileSync(join(distDir, jsMatch[1]), 'utf8');

  // 函数式替换：`String.replace(p, s)` 会把 s 里的 `$&`/`$'`/`$1` 当特殊标记展开，
  // 打包后的代码里出现这种序列就会把整份文档插进脚本中间（docs/F §1.2 记过这个坑）。
  let html = indexHtml
    .replace(cssTag, () => `<style>\n${css}\n</style>`)
    .replace(jsTag, () => `<script type="module">\n${js}\n</script>`);

  // 3) 还有别的 assets（比如懒加载的 selftest）就明确告知，不要假装没有
  const leftovers = readdirSync(join(distDir, 'assets')).filter(
    (f) => !cssMatch[1].endsWith(f) && !jsMatch[1].endsWith(f),
  );
  if (leftovers.length) {
    console.log(`· 未内联的懒加载分块：${leftovers.join(', ')}（仅 ?selftest=1 需要）`);
  }

  mkdirSync(dirname(outHtml), { recursive: true });
  writeFileSync(outHtml, html, 'utf8');
  const kb = (statSync(outHtml).size / 1024).toFixed(0);
  console.log(`· 单文件 → packages/editor-townforge/编辑器.html  ${kb} KB`);
  return { leftovers };
}

function main() {
  if (!existsSync(distDir) && !hasDeps()) {
    console.error('既没有 dist/ 也没有 node_modules，先把 vendor/townforge 的依赖装上');
    process.exitCode = 1;
    return;
  }
  if (!viteBuild()) {
    process.exitCode = 1;
    return;
  }
  inline();
}

main();
