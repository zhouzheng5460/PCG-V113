// 把 three 的 ESM 构建产物转成一段经典脚本（挂到 window.THREE）。
//
// 为什么：ES module 在 file:// 下会被 CORS 拦；要"双击即开的三维页面"就必须没有 module。
// three 的 build 产物结构很简单：
//   three.core.js     —— 几何/材质/数学类，末尾**一个** export 列表
//   three.module.js   —— 渲染器与少数扩展，开头 import core，另有 re-export 与末尾 export
//
// 为什么**必须分成两个作用域**：第一版把两个文件直接拼进同一个 IIFE，立刻炸在
//   SyntaxError: Identifier '_m1$1' has already been declared
// —— rollup 给两个产物各自生成了同名的内部辅助变量（`_m1$1` 这种）。所以：
//   core  → 自己一个 IIFE，返回命名空间 __CORE
//   module→ 自己一个 IIFE，开头用 `var X = __CORE["X"]` 把需要的名字接进来，
//           末尾只返回**它自己声明的**导出名
//   最后 Object.assign({}, __CORE, __MOD) —— core 的 re-export 由这一步天然覆盖，
//   不需要在 module 作用域里再声明一遍。
//
// 跑法：node tools/inline-three.mjs
// 产物：packages/view3d/public/js/vendor/three.inline.js

import { readFileSync, writeFileSync, mkdirSync, existsSync, statSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const root = resolve(here, '..');

function findThree() {
  const candidates = [
    join(root, 'node_modules/three/build'),
    'C:/Users/Administrator/WorkBuddy/2026-10-28-14-22-16/node_modules/three/build',
  ];
  for (const c of candidates) if (existsSync(join(c, 'three.module.js'))) return c;
  throw new Error('找不到 three 的 build 目录，请把 three 放到项目 node_modules 或改路径');
}

/** "A, B as C" → [['A','A'], ['B','C']]（源名, 本地名）。 */
const pairsOf = (list) =>
  list
    .split(',')
    .map((s) => s.trim())
    .filter(Boolean)
    .map((s) => {
      const as = /^([\w$]+)\s+as\s+([\w$]+)$/.exec(s);
      return as ? [as[1], as[2]] : [s, s];
    });

/** 去掉模块语法，收集：导出名、导入对、顶层声明名。 */
function prep(kind, src) {
  const exportNames = new Set();
  const importedPairs = [];
  let code = src;

  // 1) import { A as B } from "…"  —— 记下映射，稍后由外层用 __CORE 接上
  code = code.replace(/^import\s*\{([\s\S]*?)\}\s*from\s*["'][^"']+["'];?/gm, (_m, list) => {
    for (const p of pairsOf(list)) importedPairs.push(p);
    return '';
  });

  // 2) export { A, B as C } from "…"  —— re-export：只收名字，不产生本地声明
  code = code.replace(/^export\s*\{([\s\S]*?)\}\s*from\s*["'][^"']+["'];?/gm, (_m, list) => {
    for (const [src2, exp] of pairsOf(list)) exportNames.add(exp === src2 ? src2 : exp);
    return '';
  });

  // 3) export { A, B as C };  —— 末尾导出列表
  code = code.replace(/^export\s*\{([\s\S]*?)\};?/gm, (_m, list) => {
    for (const [, exp] of pairsOf(list)) exportNames.add(exp);
    return '';
  });

  // 4) 零散 export 前缀
  code = code.replace(/^export\s+(class|function|const|let|var|async\s+function)\b/gm, '$1');

  const left = code.match(/^(export|import)\b.*$/gm);
  if (left) throw new Error(`${kind}: 还有未处理的模块语句 → ${left.slice(0, 2).join(' | ')}`);

  // 顶层声明名（rollup 产物把顶层声明都顶在行首）
  const declared = new Set();
  for (const m of code.matchAll(/^(?:class|function|async\s+function|const|let|var)\s+([\w$]+)/gm)) declared.add(m[1]);

  return { code, exportNames, importedPairs, declared };
}

const nsLiteral = (names) => [...names].map((n) => `${JSON.stringify(n)}: ${n}`).join(', ');

function main() {
  const dir = findThree();
  const core = prep('core', readFileSync(join(dir, 'three.core.js'), 'utf8'));
  const mod = prep('module', readFileSync(join(dir, 'three.module.js'), 'utf8'));

  // core 的所有导出名必须都在 core 里声明过，否则返回到处是 undefined
  const coreMissing = [...core.exportNames].filter((n) => !core.declared.has(n));
  if (coreMissing.length) throw new Error(`core: 这些导出名在文件里没声明 → ${coreMissing.slice(0, 8).join(', ')}`);

  // module 从 core 借的名字，core 必须真有
  const badImport = mod.importedPairs.filter(([from]) => !core.exportNames.has(from));
  if (badImport.length) throw new Error(`module: 从 core 借不到 → ${badImport.slice(0, 8).map((p) => p[0]).join(', ')}`);

  // module 自己的导出名：要么它自己声明了，要么是 core 的 re-export（由合并覆盖）
  const modOwn = [...mod.exportNames].filter((n) => mod.declared.has(n));
  const orphan = [...mod.exportNames].filter((n) => !mod.declared.has(n) && !core.exportNames.has(n));
  if (orphan.length) throw new Error(`module: 这些导出名既没声明也不在 core 里 → ${orphan.slice(0, 8).join(', ')}`);

  const modHead = mod.importedPairs
    .map(([from, to]) => `var ${to} = __CORE[${JSON.stringify(from)}];`)
    .join('\n');

  const out = `// 由 tools/inline-three.mjs 自动生成，不要手改。
// 源：three 的 build/three.core.js + build/three.module.js（ESM → 经典脚本）
// core ${core.exportNames.size} 个导出 / module 自声明 ${modOwn.length} 个 → window.THREE
(function () {
var __CORE = (function () {
${core.code}
return { ${nsLiteral(core.exportNames)} };
})();

var __MOD = (function () {
${modHead}
${mod.code}
return { ${nsLiteral(modOwn)} };
})();

window.THREE = Object.assign({}, __CORE, __MOD);
})();
`;

  const dst = join(root, 'packages/view3d/public/js/vendor/three.inline.js');
  mkdirSync(dirname(dst), { recursive: true });
  writeFileSync(dst, out, 'utf8');
  const keys = new Set([...core.exportNames, ...modOwn]);
  console.log(
    `three → packages/view3d/public/js/vendor/three.inline.js  ${(statSync(dst).size / 1024 / 1024).toFixed(2)} MB  （${keys.size} 个符号）`,
  );
}

main();
