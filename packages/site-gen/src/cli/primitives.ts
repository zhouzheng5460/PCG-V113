// Primitive 使用报告：**回答"这个生成器是复用原语，还是又写了一套几何"**。
//
// 用户 2026-11-03 定的硬门禁：
//   > Plaza Native V1 不允许新增第二套 boolean / split / scatter / connectivity 实现。
//
// 这个工具做静态分析（读源码的 import 与函数定义），不执行任何生成：
//   · 它 import 了哪些 `primitives/*`（以及这些文件各多少行）
//   · 生成器自己多少行、自己定义了哪些函数
//   · **可疑信号**：生成器里出现"几何味"的函数名（area / clip / offset / difference / inset /
//     union / intersect / hull / centroid …）—— 那通常意味着又写了一套几何
//
// 判据不是"行数比"，而是"有没有第二套几何"。行数只是辅助。
//
// 跑法：
//   node packages/site-gen/src/cli/primitives.ts parking
//   node packages/site-gen/src/cli/primitives.ts            （列全部已注册生成器）

import { existsSync, readFileSync } from 'node:fs';
import { dirname, join, relative } from 'node:path';
import { fileURLToPath } from 'node:url';
import { NATIVE_GENERATORS } from '../generators/index.ts';

const HERE = dirname(fileURLToPath(import.meta.url));
const PKG = join(HERE, '..', '..');
const SRC = join(PKG, 'src');

/** 生成器文件位置：src/generators/<id>.ts */
const genFile = (id: string): string => join(SRC, 'generators', `${id}.ts`);

interface ImportInfo {
  spec: string;
  names: string[];
}

/** 粗读 import（够用：我们的 import 都是一行一条，或者带花括号的多名） */
function parseImports(code: string): ImportInfo[] {
  const out: ImportInfo[] = [];
  const re = /import\s+(?:type\s+)?(?:([\s\S]*?)\s+from\s+)?['"]([^'"]+)['"]/g;
  let m: RegExpExecArray | null;
  while ((m = re.exec(code))) {
    const clause = (m[1] ?? '').trim();
    const spec = m[2];
    const names: string[] = [];
    const braced = clause.match(/\{([\s\S]*?)\}/);
    if (braced) {
      for (const part of braced[1].split(',')) {
        const n = part.trim().replace(/^type\s+/, '').split(/\s+as\s+/)[0].trim();
        if (n) names.push(n);
      }
    }
    const def = clause.match(/^([A-Za-z_$][\w$]*)/);
    if (def && !clause.startsWith('{')) names.push(`default:${def[1]}`);
    out.push({ spec, names });
  }
  return out;
}

/** 生成器里定义的顶层函数（名字 + 起始行） */
function localFunctions(code: string): string[] {
  const names: string[] = [];
  const re = /^(?:export\s+)?(?:function|const)\s+([A-Za-z_$][\w$]*)\s*(?:=\s*\(|=?\s*<|\(|:)/gm;
  let m: RegExpExecArray | null;
  while ((m = re.exec(code))) names.push(m[1]);
  return names;
}

/** "几何味"的名字：出现这些，说明可能又写了一套几何 */
const GEOMETRY_SMELL = /^(clip|offset|inset|difference|union|intersect|hull|centroid|area|perimeter|triangul|winding|orient|boolean|subtract|split|erase|cut)/i;

function linesOf(file: string): number {
  try {
    return readFileSync(file, 'utf8').split('\n').length;
  } catch {
    return 0;
  }
}

const args = process.argv.slice(2);
const targets = args.filter((a) => !a.startsWith('-'));
const ids = targets.length ? targets : NATIVE_GENERATORS.map((g) => g.id);

let failures = 0;

for (const id of ids) {
  const f = genFile(id);
  if (!existsSync(f)) {
    console.log(`\n── ${id} ──\n  ! 找不到 ${relative(PKG, f)}`);
    continue;
  }
  const code = readFileSync(f, 'utf8');
  const imports = parseImports(code);
  const primImports = imports.filter((i) => i.spec.includes('primitives/'));
  const fns = localFunctions(code);

  console.log(`\n── ${id} ──`);
  console.log(`  生成器 ${linesOf(f)} 行 · 顶层定义 ${fns.length} 个`);

  if (!primImports.length) {
    console.log(`  ! 没有 import 任何 primitives/* —— 这个生成器是自成一体的几何实现`);
    failures++;
  } else {
    let primLines = 0;
    for (const p of primImports) {
      const file = join(SRC, 'primitives', `${p.spec.split('/').pop()!.replace(/\.ts$/, '')}.ts`);
      const n = linesOf(file);
      primLines += n;
      console.log(`  复用 ${p.spec.replace('../primitives/', '').padEnd(18)} ${String(n).padStart(4)} 行 · 用到的名字：${p.names.filter((x) => !x.startsWith('default:')).join(', ') || '（类型）'}`);
    }
    console.log(`  复用合计 ${primLines} 行`);
  }

  // 可疑：自己写了几何函数
  const suspicious = fns.filter((n) => GEOMETRY_SMELL.test(n));
  if (suspicious.length) {
    console.log(`  ! 可疑几何函数：${suspicious.join(', ')}`);
    failures++;
  } else {
    console.log(`  本地函数：${fns.join(', ') || '（无）'}`);
  }

  // 硬门禁：不允许直接 import 布尔内核以外的实现；也不允许绕过 boolean.ts
  const bypass = imports.find((i) => i.spec.includes('polygon-clipping'));
  if (bypass) {
    console.log(`  ! 直接 import 了布尔内核（应只经 primitives/boolean.ts）`);
    failures++;
  }
}

console.log(
  `\n${failures === 0 ? 'PASS' : 'FAIL'} — ${ids.length} 个生成器，${failures} 处违规`,
);
console.log('说明：判据是"有没有第二套几何"，不是行数比。出现几何味函数名或绕过 boolean.ts 即为违规。');
if (failures) process.exitCode = 1;
