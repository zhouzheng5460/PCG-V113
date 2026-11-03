// golden 语料的读取层。
//
// 语料由 `tools/golden/emit.mjs` 生成，成对存在：
//   plans/<case>.plan.json   —— C# 的权威输出
//   plans/<case>.input.json  —— C# **解析之后**的输入（原生后端吃这一份，见 D-059）
//   index.json               —— 契约层事实（通道条数 / ready / 输入规模）
//
// 这一层刻意只做 IO，不做判断：判在 `compare.ts`，读在这里。

import { existsSync, readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import type { Plan, SiteInput } from './types.ts';

const HERE = dirname(fileURLToPath(import.meta.url));
/** 仓库根：packages/site-gen/src → ../../../ */
export const REPO_ROOT = join(HERE, '..', '..', '..');
export const GOLDEN_DIR = join(REPO_ROOT, 'tools', 'golden');
export const PLAN_DIR = join(GOLDEN_DIR, 'plans');

export interface GoldenIndexCase {
  name: string;
  generator: string;
  ok: boolean;
  why: string;
  ready: boolean;
  channels: Record<string, number>;
  input: { ring: number; nets: number; settings: number; markers: number } | null;
  bytes: number;
}

export interface GoldenIndex {
  protocol: string;
  generatedAt: string;
  channels: string[];
  count: number;
  byGenerator: Record<string, number>;
  cases: GoldenIndexCase[];
}

export function loadIndex(): GoldenIndex {
  const f = join(GOLDEN_DIR, 'index.json');
  if (!existsSync(f)) {
    throw new Error(`找不到 ${f}。先跑 node tools/golden/emit.mjs 烘 golden 语料。`);
  }
  return JSON.parse(readFileSync(f, 'utf8')) as GoldenIndex;
}

export function loadGoldenPlan(name: string): Plan {
  return JSON.parse(readFileSync(join(PLAN_DIR, `${name}.plan.json`), 'utf8')) as Plan;
}

export function loadSiteInput(name: string): SiteInput {
  return JSON.parse(readFileSync(join(PLAN_DIR, `${name}.input.json`), 'utf8')) as SiteInput;
}

/** 某生成器在语料里有哪几份用例 */
export function casesOf(index: GoldenIndex, generator: string): GoldenIndexCase[] {
  return index.cases.filter((c) => c.generator === generator);
}

/** 语料里出现过的生成器（按用例数降序） */
export function generatorsInCorpus(index: GoldenIndex): { id: string; cases: number }[] {
  return Object.entries(index.byGenerator)
    .map(([id, cases]) => ({ id, cases }))
    .sort((a, b) => b.cases - a.cases);
}
