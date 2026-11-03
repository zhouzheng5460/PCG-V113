// 极简 ES module 打包器：把 tsc 编译出来的模块打成一个普通 <script>。
//
// 为什么需要它：ES module 在 file:// 下会被 CORS 拦掉（报的是 "Failed to fetch xxx"，
// 看着像数据没打包）。要"双击即开"就必须消掉 module 这个前提。
//
// 为什么不用 esbuild/rollup：本机没有 npm 网络，装不了。所以只做语句级改写，
// 而且**只认本项目实际用到的语法**——遇到没覆盖的形式直接抛错，而不是产出一个
// 悄悄坏掉的包（页面白屏且无报错，最难查）。
//
// 支持的顶层语法：
//   import { a, b as c } from "…"      → const { a, b: c } = __req(k)
//   import * as ns from "…"            → const ns = __req(k)
//   import "…"                         → 拒绝（副作用导入语义不明）
//   export * from "…"                  → Object.assign(__exports, __req(k))
//   export { a, b as c } from "…"      → 逐名从 __req(k) 取
//   export { a, b as c }               → 本模块声明，导出名 c ↔ 局部名 b
//   export function/class/const/let/var X
//   export default                     → 拒绝
//
// 别名：spec 命中 aliases 时按别名解析（用来把裸模块名 "three" 指到 window.THREE）。

/** 相对 specifier → 相对 jsRoot 的模块键（正斜杠）。 */
export function resolveSpec(fromKey, spec) {
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

/** "a, b as c, d" → [[a,a],[c,b],[d,d]]（导出名, 局部名）。 */
function parseNameList(text) {
  return text
    .split(',')
    .map((s) => s.trim())
    .filter(Boolean)
    .map((s) => {
      const as = /^([\w$]+)\s+as\s+([\w$]+)$/.exec(s);
      return as ? [as[2], as[1]] : [s, s];
    });
}

/**
 * 单个模块的语句级改写。
 * @returns {{ body: string, exports: Array<[string,string]> }}
 *   exports 是 [导出名, 局部表达式] 列表，局部表达式通常就是变量名。
 */
export function transform(key, src, deps, aliases = {}) {
  const exports = [];
  let out = src;

  // ── import { … } from "…"
  out = out.replace(/^import\s*\{([\s\S]*?)\}\s*from\s*["']([^"']+)["'];?/gm, (_m, bindings, spec) => {
    const pairs = parseNameList(bindings).map(([local, imported]) => [imported, local]); // 注意方向
    if (aliases[spec]) {
      const binds = pairs.map(([imported, local]) => `${imported}: ${local}`).join(', ');
      return `const { ${binds} } = ${aliases[spec]};`;
    }
    const k = resolveSpec(key, spec);
    deps.add(k);
    const binds = pairs.map(([imported, local]) => `${imported}: ${local}`).join(', ');
    return `const { ${binds} } = __req(${JSON.stringify(k)});`;
  });

  // ── import * as ns from "…"
  out = out.replace(/^import\s*\*\s*as\s*([\w$]+)\s*from\s*["']([^"']+)["'];?/gm, (_m, ns, spec) => {
    if (aliases[spec]) return `const ${ns} = ${aliases[spec]};`;
    const k = resolveSpec(key, spec);
    deps.add(k);
    return `const ${ns} = __req(${JSON.stringify(k)});`;
  });

  // ── export { a, b as c } from "…"
  out = out.replace(/^export\s*\{([\s\S]*?)\}\s*from\s*["']([^"']+)["'];?/gm, (_m, names, spec) => {
    const k = resolveSpec(key, spec);
    deps.add(k);
    const pairs = parseNameList(names);
    const tmp = `__re${exports.length}`;
    const body = pairs.map(([exp, loc]) => `__exports[${JSON.stringify(exp)}] = ${tmp}[${JSON.stringify(loc)}];`).join(' ');
    return `{ const ${tmp} = __req(${JSON.stringify(k)}); ${body} }`;
  });

  // ── export { a, b as c }
  out = out.replace(/^export\s*\{([\s\S]*?)\};?/gm, (_m, names) => {
    for (const [exp, loc] of parseNameList(names)) exports.push([exp, loc]);
    return '';
  });

  // ── export * from "…"
  out = out.replace(/^export\s*\*\s*from\s*["']([^"']+)["'];?/gm, (_m, spec) => {
    const k = resolveSpec(key, spec);
    deps.add(k);
    return `Object.assign(__exports, __req(${JSON.stringify(k)}));`;
  });

  // ── export function/class/const/let/var X
  out = out.replace(/^export\s+(async\s+)?function\s+([\w$]+)/gm, (_m, asy, n) => {
    exports.push([n, n]);
    return `${asy ?? ''}function ${n}`;
  });
  out = out.replace(/^export\s+(class\s+([\w$]+))/gm, (_m, decl, n) => {
    exports.push([n, n]);
    return decl;
  });
  out = out.replace(/^export\s+((?:const|let|var)\s+([\w$]+))/gm, (_m, decl, n) => {
    exports.push([n, n]);
    return decl;
  });

  // 剩下的 export / 裸 import 一律拒绝
  const left = out.match(/^export\b.*$/gm);
  if (left) throw new Error(`${key}: 有未处理的导出形式 → ${left.slice(0, 2).join(' | ')}`);
  const bare = out.match(/^import\s+["']/gm);
  if (bare) throw new Error(`${key}: 存在副作用 import，本打包器不支持`);

  return { body: out, exports };
}

/**
 * 从入口出发收集并改写所有模块。
 * @returns {{ defs: Map<string,{body:string,exports:Array<[string,string]>}>, order: string[] }}
 */
export function collect({ jsRoot, entry, readFile, exists, aliases = {} }) {
  const defs = new Map();
  const order = [];
  const queue = [entry];
  while (queue.length) {
    const key = queue.shift();
    if (defs.has(key)) continue;
    const file = `${jsRoot}/${key}`;
    if (!exists(file)) throw new Error(`找不到模块文件: ${file}`);
    const deps = new Set();
    const { body, exports } = transform(key, readFile(file), deps, aliases);
    defs.set(key, { body, exports });
    order.push(key);
    for (const d of deps) if (!defs.has(d)) queue.push(d);
  }
  return { defs, order };
}

/**
 * 生成经典脚本。
 * @param {Map} defs  collect() 的结果
 * @param {string} entry 入口模块键（执行它）
 * @param {string} [runtimePrefix] 放在最前面的额外代码（例如 three 全局化脚本）
 */
export function buildScript(defs, entry, runtimePrefix = '') {
  const parts = [];
  if (runtimePrefix) parts.push(runtimePrefix);
  parts.push('var __def = {}, __cache = {};');
  parts.push('function __req(k) {');
  parts.push('  var e = __cache[k]; if (e) return e;');
  parts.push('  e = __cache[k] = {};');
  parts.push('  var f = __def[k];');
  parts.push('  if (!f) throw new Error("模块未打包: " + k);');
  parts.push('  f(e);');
  parts.push('  return e;');
  parts.push('}');

  for (const [key, { body, exports }] of defs) {
    parts.push(`__def[${JSON.stringify(key)}] = function (__exports) {`);
    parts.push(body);
    for (const [exp, loc] of exports) {
      const safe = /^[\w$]+$/.test(loc) ? loc : `(${loc})`;
      parts.push(`__exports[${JSON.stringify(exp)}] = ${safe};`);
    }
    parts.push('};');
  }

  parts.push(`__req(${JSON.stringify(entry)});`);
  return parts.join('\n');
}
