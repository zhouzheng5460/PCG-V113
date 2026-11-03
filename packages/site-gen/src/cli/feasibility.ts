// 把 parking 的 golden 用例打成一张表，用来**反推判据**，而不是猜阈值。
//
// 用户 2026-11-03 的要求：
//   "P1.5 最好不要猜。直接把 54 个 case 打成表 …… 然后找分界。这一步甚至不用 ML。"
//
// 本表并排三组数：
//   · 几何（area / 外接矩形长宽 / 紧致度 / 有效进深 / 估算容量）—— 我们自己的度量
//   · oracle 行为（ready / 车位数 / 车道总长 / 铺面数）—— Placemaker 的答案
//   · native 行为（ready / 车位数）—— 我们现在的答案
// 分界就藏在"几何"与"oracle 行为"的对应关系里。
//
// 跑法：node packages/site-gen/src/cli/feasibility.ts [生成器]
//      加 --csv 输出逗号分隔（方便贴表格）

import { loadGoldenPlan, loadIndex, loadSiteInput } from '../golden.ts';
import { measure } from '../metrics.ts';
import { parkingFeasibility } from '../eligibility.ts';
import { parking } from '../generators/parking.ts';
import type { SiteGenerator } from '../types.ts';

const args = process.argv.slice(2);
const csv = args.includes('--csv');
const genId = args.find((a) => !a.startsWith('-')) ?? 'parking';

const GEN: Record<string, SiteGenerator> = { parking };
const gen = GEN[genId];
if (!gen) {
  console.error(`没有原生生成器 ${genId}`);
  process.exit(1);
}

interface Row {
  name: string;
  area: number;
  len: number;
  dep: number;
  compact: number;
  usableDepth: number;
  rows: number;
  cap: number;
  ent: number;
  streets: number;
  oracleReady: boolean;
  oracleStalls: number;
  oracleAisle: number;
  oracleSurf: number;
  nativeReady: boolean;
  nativeStalls: number;
}

const index = loadIndex();
const rows: Row[] = [];

for (const c of index.cases) {
  if (c.generator !== genId || !c.ok) continue;
  const input = loadSiteInput(c.name);
  const gold = loadGoldenPlan(c.name);
  const gm = measure(gold);
  const f = parkingFeasibility(input);
  const stat = (k: string): number => gold.stats?.find((s) => s.key === k)?.value ?? -1;
  let nativeStalls = 0;
  let nativeReady = true;
  try {
    const np = gen.generate(input);
    nativeReady = np.ready;
    nativeStalls = measure(np).parkingBays;
  } catch {
    nativeReady = false;
    nativeStalls = -1;
  }
  rows.push({
    name: c.name.replace(/^city\d+-s\d+-/, '').replace(/-parking/, ''),
    area: f.area,
    len: f.dominantLength,
    dep: f.dominantDepth,
    compact: f.compactness,
    usableDepth: f.usableDepth,
    rows: f.estimatedRows,
    cap: f.estimatedCapacity,
    ent: f.entranceCount,
    streets: f.streetCount,
    oracleReady: gold.ready,
    oracleStalls: stat('Stat.Stalls'),
    oracleAisle: stat('Stat.AisleLength'),
    oracleSurf: gm.surfaceTotal,
    nativeReady,
    nativeStalls,
  });
}

rows.sort((a, b) => a.area - b.area);

if (csv) {
  console.log(
    'case,area,domLen,domDep,compact,usableDep,estRows,estCap,ent,streets,oracleReady,oracleStalls,oracleAisle,oracleSurf,nativeReady,nativeStalls',
  );
  for (const r of rows) {
    console.log(
      [
        r.name,
        r.area.toFixed(0),
        r.len.toFixed(1),
        r.dep.toFixed(1),
        r.compact.toFixed(2),
        r.usableDepth.toFixed(1),
        r.rows,
        r.cap,
        r.ent,
        r.streets,
        r.oracleReady,
        r.oracleStalls,
        r.oracleAisle.toFixed(0),
        r.oracleSurf.toFixed(0),
        r.nativeReady,
        r.nativeStalls,
      ].join(','),
    );
  }
  process.exit(0);
}

const p = (v: number, w: number, d = 0): string => (v < 0 ? '—' : v.toFixed(d)).padStart(w);
console.log(
  '用例'.padEnd(16),
  '面积'.padStart(7),
  '长×宽'.padStart(15),
  '紧致'.padStart(5),
  '有效深'.padStart(7),
  '排'.padStart(3),
  '估容'.padStart(5),
  '口'.padStart(3),
  '│',
  'ready'.padStart(6),
  '车位'.padStart(5),
  '车道'.padStart(6),
  '│',
  'nRdy'.padStart(5),
  '车位'.padStart(5),
);
for (const r of rows) {
  console.log(
    r.name.padEnd(16),
    p(r.area, 7),
    `${r.len.toFixed(0)}×${r.dep.toFixed(0)}`.padStart(15),
    r.compact.toFixed(2).padStart(5),
    p(r.usableDepth, 7, 1),
    String(r.rows).padStart(3),
    String(r.cap).padStart(5),
    String(r.ent).padStart(3),
    '│',
    String(r.oracleReady).padStart(6),
    p(r.oracleStalls, 5),
    p(r.oracleAisle, 6),
    '│',
    String(r.nativeReady).padStart(5),
    p(r.nativeStalls, 5),
  );
}

// 分界探索：oracle 产出"有意义的停车"（≥1 个车位 或 ≥1 个 Stat）之前之后，几何量各是多少
const withStats = rows.filter((r) => r.oracleStalls >= 0);
const noStats = rows.filter((r) => r.oracleStalls < 0);
const stat = (xs: Row[], f: (r: Row) => number): string => {
  if (!xs.length) return '—';
  const v = xs.map(f).sort((a, b) => a - b);
  return `${v[0].toFixed(0)}~${v[v.length - 1].toFixed(0)}`;
};
console.log('\n── 分界线索 ──');
console.log(`oracle 有 Stat.Stalls 的 ${withStats.length} 例：面积 ${stat(withStats, (r) => r.area)} · 有效深 ${stat(withStats, (r) => r.usableDepth)} · 估容 ${stat(withStats, (r) => r.cap)}`);
console.log(`oracle 无 Stat.Stalls 的 ${noStats.length} 例：面积 ${stat(noStats, (r) => r.area)} · 有效深 ${stat(noStats, (r) => r.usableDepth)} · 估容 ${stat(noStats, (r) => r.cap)}`);
