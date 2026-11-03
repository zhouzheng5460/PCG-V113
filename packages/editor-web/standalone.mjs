// 把编辑器打成一个自包含 HTML（双击即开，不需要任何服务）。
//
// 为什么需要这个：`serve.mjs` 起的是"本地 http 服务"，但 node 进程会随启动它的
// 终端/会话退出而被回收 —— 用户遇到过 ERR_CONNECTION_REFUSED，就是这个原因。
// 换成本地服务之外的办法（WMI 创建进程、计划任务）都被安全策略挡了，
// 那就别跟沙箱较劲：直接把"必须用 http"这个前提消掉。
//
// ES module + fetch 在 file:// 下会被 CORS 拦掉，所以这里做两件事：
//   1. 把 20 个编译后的 ES module 打成一个普通 <script>（经典脚本，无 CORS）
//   2. 把 bundle.json 内联成 window.__BUNDLE__，并短路那次 fetch
// 不引入打包器、不改源码：只做语句级改写，且只处理本项目实际用到的语法子集。
//
// 跑法：node packages/editor-web/standalone.mjs
// 产物：packages/editor-web/规则验证器.html

import { readFileSync, writeFileSync, existsSync, statSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const jsRoot = join(here, 'public/js');
const publicDir = join(here, 'public');
const ENTRY = 'editor-web/src/app.js';
const OUT = join(here, '规则验证器.html');

/** 相对 specifier → 相对 jsRoot 的模块键（正斜杠）。 */
function resolveSpec(fromKey, spec) {
  const dir = fromKey.includes('/') ? fromKey.slice(0, fromKey.lastIndexOf('/')) : '';
  const parts = (dir ? dir.split('/') : []).concat(spec.split('/'));
  const out = [];
  for (const p of parts) {
    if (p === '' || p === '.') continue;
    if (p === '..') out.pop();
    else out.push(p);
  }
  return out.join('/');
}

/**
 * 单个模块的语句级改写。只处理本项目实际出现的四种顶层语法：
 *   import { a, b as c } from "…"   import * as ns from "…"
 *   export * from "…"               export function/const/let/var/class X
 * 出现别的导出形式就直接抛错 —— 宁可构建失败，也不要产出一个"看起来能跑"的残包。
 */
function transform(key, src, deps) {
  const names = [];
  let out = src;

  out = out.replace(/^import\s*\{([\s\S]*?)\}\s*from\s*["']([^"']+)["'];?/gm, (_m, bindings, spec) => {
    const k = resolveSpec(key, spec);
    deps.add(k);
    const list = bindings
      .split(',')
      .map((s) => s.trim())
      .filter(Boolean)
      .map((s) => {
        const as = /^(\w+)\s+as\s+(\w+)$/.exec(s);
        return as ? `${as[1]}: ${as[2]}` : s;
      })
      .join(', ');
    return `const { ${list} } = __req(${JSON.stringify(k)});`;
  });

  out = out.replace(/^import\s*\*\s*as\s*(\w+)\s*from\s*["']([^"']+)["'];?/gm, (_m, ns, spec) => {
    const k = resolveSpec(key, spec);
    deps.add(k);
    return `const ${ns} = __req(${JSON.stringify(k)});`;
  });

  out = out.replace(/^export\s*\*\s*from\s*["']([^"']+)["'];?/gm, (_m, spec) => {
    const k = resolveSpec(key, spec);
    deps.add(k);
    return `Object.assign(__exports, __req(${JSON.stringify(k)}));`;
  });

  out = out.replace(/^export\s+(async\s+)?function\s+(\w+)/gm, (_m, asy, n) => {
    names.push(n);
    return `${asy ?? ''}function ${n}`;
  });

  out = out.replace(/^export\s+(class\s+(\w+))/gm, (_m, decl, n) => {
    names.push(n);
    return decl;
  });

  out = out.replace(/^export\s+((?:const|let|var)\s+(\w+))/gm, (_m, decl, n) => {
    names.push(n);
    return decl;
  });

  // 剩下的 export 一律不认：说明出现了没被覆盖的语法。
  const left = out.match(/^export\b.*$/gm);
  if (left) throw new Error(`${key}: 有未处理的导出形式 → ${left.join(' | ')}`);
  // 裸 import（副作用导入）也拒绝，因为不清楚该不该执行。
  const bare = out.match(/^import\s+["']/gm);
  if (bare) throw new Error(`${key}: 存在副作用 import，本打包器不支持`);

  return { body: out, names };
}

function collect() {
  const defs = new Map();
  const seen = new Set();
  const queue = [ENTRY];
  while (queue.length) {
    const key = queue.shift();
    if (seen.has(key)) continue;
    seen.add(key);
    const file = join(jsRoot, key);
    if (!existsSync(file)) throw new Error(`找不到模块文件: ${file}`);
    const deps = new Set();
    const { body, names } = transform(key, readFileSync(file, 'utf8'), deps);
    defs.set(key, { body, names });
    for (const d of deps) if (!seen.has(d)) queue.push(d);
  }
  return defs;
}

function buildScript(defs) {
  const parts = [];
  parts.push('var __def = {}, __cache = {};');
  parts.push('function __req(k) {');
  parts.push('  var e = __cache[k]; if (e) return e;');
  parts.push('  e = __cache[k] = {};');
  parts.push('  var f = __def[k];');
  parts.push('  if (!f) throw new Error("模块未打包: " + k);');
  parts.push('  f(e);');
  parts.push('  return e;');
  parts.push('}');

  for (const [key, { body, names }] of defs) {
    parts.push(`__def[${JSON.stringify(key)}] = function (__exports) {`);
    parts.push(body);
    for (const n of names) parts.push(`__exports.${n} = ${n};`);
    parts.push('};');
  }

  parts.push(`__req(${JSON.stringify(ENTRY)});`);
  return parts.join('\n');
}

/** fetch('./data/bundle.json') → 直接读内联的 window.__BUNDLE__。源码一行不改。 */
function buildShim(bundle) {
  return [
    'window.__BUNDLE__ = ' + JSON.stringify(bundle) + ';',
    '(function () {',
    '  var real = window.fetch ? window.fetch.bind(window) : null;',
    '  window.fetch = function (u, o) {',
    '    var s = String(u);',
    '    if (s.indexOf("bundle.json") >= 0) {',
    '      return Promise.resolve(new Response(JSON.stringify(window.__BUNDLE__),',
    '        { headers: { "content-type": "application/json" } }));',
    '    }',
    '    return real ? real(u, o) : Promise.reject(new Error("no fetch"));',
    '  };',
    '})();',
  ].join('\n');
}

export function buildStandalone() {
  const bundlePath = join(publicDir, 'data/bundle.json');
  if (!existsSync(bundlePath)) throw new Error(`缺 ${bundlePath}，先跑 build.mjs`);
  const bundle = JSON.parse(readFileSync(bundlePath, 'utf8'));

  const defs = collect();
  const html0 = readFileSync(join(publicDir, 'index.html'), 'utf8');
  const css = readFileSync(join(publicDir, 'style.css'), 'utf8');

  const script = [buildShim(bundle), buildScript(defs)].join('\n');

  const linkTag = '<link rel="stylesheet" href="./style.css"/>';
  const modTag = '<script type="module" src="./js/editor-web/src/app.js"></script>';
  if (!html0.includes(linkTag) || !html0.includes(modTag)) {
    throw new Error('index.html 的样式/入口标签变了，standalone 打包需要同步改');
  }

  // 静态内容一律用函数式替换：`String.replace(p, s)` 会把 s 里的 `$&` / `$'` / `$1`
  // 当特殊标记展开。编辑器自己的代码里目前没出现这类序列，但数据一旦带上（比如
  // 某个生成器 id 里有 `$`），就会把整份文档插进脚本中间，症状是白屏 + 语法错误。
  const html = html0
    .replace(linkTag, () => `<style>\n${css}\n</style>`)
    .replace(modTag, () => `<script>\n${script}\n</script>`)
    .replace('</head>', () => '<meta name="standalone" content="内联版：无需本地服务，双击即可打开"/>\n</head>');

  writeFileSync(OUT, html, 'utf8');
  const kb = (statSync(OUT).size / 1024).toFixed(0);
  console.log(`内联版 → packages/editor-web/${'规则验证器.html'}  ${kb} KB  （${defs.size} 个模块）`);
  return OUT;
}

if (process.argv[1] && pathToFileURL(resolve(process.argv[1])).href === import.meta.url) {
  buildStandalone();
}
