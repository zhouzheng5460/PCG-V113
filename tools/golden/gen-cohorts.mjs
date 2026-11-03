// 生成 `cohorts.json`：**显式标注每个用例的用途**，不用面积阈值猜。
//
// 规则（有明确依据，不是随手写的）：
//   1. 若 `plaza-cohorts.json`（或任何生成时的 cohort 声明）里有，用它 —— 生成时声明的优先；
//   2. 名字形如 `*.partN` 的，是"把一块地切成 N 份"的**退化样本**，标 stress；
//   3. 其余标 primary。
//
// 用户 2026-11-03："不要让 `PRIMARY_MIN_AREA` 成为长期语义…… 显式标注 cohort。"
// 所以这里**唯一**的自动规则是 `.partN`（有客观依据），其余一律显式。
//
// 跑法：node tools/golden/gen-cohorts.mjs

import { existsSync, readFileSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const HERE = dirname(fileURLToPath(import.meta.url));
const index = JSON.parse(readFileSync(join(HERE, 'index.json'), 'utf8'));
const declared = existsSync(join(HERE, 'plaza-cohorts.json'))
  ? JSON.parse(readFileSync(join(HERE, 'plaza-cohorts.json'), 'utf8')).cases
  : {};

const cases = {};
for (const c of index.cases) {
  const plan = JSON.parse(readFileSync(join(HERE, 'plans', `${c.name}.plan.json`), 'utf8'));
  const decl = declared[c.name];
  const cohort = decl?.cohort ?? (/\.part\d+$/.test(c.name) ? 'stress' : 'primary');
  cases[c.name] = {
    generator: c.generator,
    cohort,
    source: c.source ?? 'bake',
    area: Math.round(plan.area),
    ...(decl?.note ? { note: decl.note } : {}),
  };
}

const out = {
  protocol: 'site-gen-cohorts/1',
  note: '显式标注每个 golden 用例的用途。**不要用面积阈值代替这个标注** —— 面积阈值会让"area>=N"偷偷变成业务规则。primary=完整设计样本（判保真）；stress=碎块/退化/稳健性样本（只验不崩/确定性/不越界/不重叠/拓扑）。生成时声明的 cohort 优先，其余只对 .partN 自动标 stress。',
  classes: {
    primary: '完整设计样本：golden 自己在这类样本上自洽（同族密度一致），可以做保真判据',
    stress: '碎块 / 退化输入 / 稳健性样本：golden 在此退化，不拿它当保真目标',
  },
  bySource: index.bySource ?? {},
  cases,
};
writeFileSync(join(HERE, 'cohorts.json'), JSON.stringify(out, null, 1) + '\n');

const byGen = {};
for (const [n, v] of Object.entries(cases)) {
  const k = `${v.generator}/${v.cohort}`;
  byGen[k] = (byGen[k] ?? 0) + 1;
}
console.log(`写出 ${Object.keys(cases).length} 份 cohort`);
for (const [k, v] of Object.entries(byGen).sort()) console.log(`  ${k.padEnd(24)} ${v}`);
