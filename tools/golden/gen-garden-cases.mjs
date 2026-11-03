// FormalGarden 补充用例（P4.0 侦察用）。
//
// 只有一个真实烘焙用例，且**没有障碍物** —— 于是"障碍物进入后是裁剪、重排还是放弃构图"
// 这个问题无法回答。这里补两例：一例带障碍物，一例非矩形（验对称/朝向是否依赖场地形状）。
// settings 保持为空（走 Defaults()），seed 统一 1234。
//
// 跑法：node tools/golden/gen-garden-cases.mjs

import { mkdirSync, writeFileSync, rmSync, existsSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const HERE = dirname(fileURLToPath(import.meta.url));
const ROOT = join(HERE, '..', '..');
const OUT = join(ROOT, 'tools', 'batch', 'cases-synth');
if (!existsSync(OUT)) mkdirSync(OUT, { recursive: true });

const ccw = (ring) => {
  let a = 0;
  for (let i = 0; i < ring.length; i++) {
    const p = ring[i];
    const q = ring[(i + 1) % ring.length];
    a += p[0] * q[1] - q[0] * p[1];
  }
  return a >= 0 ? ring : [...ring].reverse();
};
const rect = (x0, z0, x1, z1) => ccw([[x0, z0], [x1, z0], [x1, z1], [x0, z1]]);
const street = (id, a, b, w = 12) => ({ id, a, b, width: w, category: 'road', sidewalk: true, speed: 50 });

const CASES = [
  {
    name: 'garden-r0-rect',
    cohort: 'primary',
    note: '100×80 规则矩形、无障碍。对照真实用例，看主轴与花坛排布是否只由场地决定。',
    ring: rect(0, 0, 100, 80),
    streets: [street(1, [0, -6], [100, -6]), street(2, [106, 0], [106, 80]), street(3, [-6, 80], [-6, 0])],
  },
  {
    name: 'garden-r1-obstacles',
    cohort: 'primary-critical',
    note: '120×100 地块内有 2 栋既有建筑。**这是唯一能回答"障碍物进入后如何反应"的用例。**',
    ring: rect(0, 0, 120, 100),
    streets: [street(1, [0, -6], [120, -6]), street(2, [126, 0], [126, 100])],
    obstacles: [
      { quad: [[16, 60], [40, 60], [40, 84], [16, 84]], kind: 'building' },
      { quad: [[78, 14], [104, 14], [104, 38], [78, 38]], kind: 'building' },
    ],
  },
  {
    name: 'garden-r2-lshape',
    cohort: 'primary',
    note: 'L 形（100×50 + 40×40）。看对称轴/主轴在地块两段走向不同时怎么选。',
    ring: ccw([[0, 0], [100, 0], [100, 50], [40, 50], [40, 90], [0, 90]]),
    streets: [street(1, [0, -6], [100, -6]), street(2, [-6, 90], [-6, 0]), street(3, [40, 96], [0, 96])],
  },
];

for (const c of CASES) {
  writeFileSync(
    join(OUT, `${c.name}.json`),
    JSON.stringify(
      {
        generator: 'formalGarden',
        seed: 1234,
        note: c.note,
        settings: {},
        shape: { ring: c.ring },
        streets: c.streets,
        ...(c.obstacles ? { obstacles: c.obstacles } : {}),
      },
      null,
      1,
    ) + '\n',
  );
}

// cohort 声明（生成时标注，不按面积事后猜）
const declPath = join(HERE, 'garden-cohorts.json');
const prev = existsSync(declPath) ? JSON.parse((await import('node:fs')).readFileSync(declPath, 'utf8')) : { cases: {} };
for (const c of CASES) prev.cases[c.name] = { generator: 'formalGarden', cohort: c.cohort, note: c.note };
writeFileSync(declPath, JSON.stringify({ protocol: 'site-gen-cohorts/1', cases: prev.cases }, null, 1) + '\n');

console.log(`写出 ${CASES.length} 份 formalGarden 用例`);
for (const c of CASES) console.log(`  ${c.cohort.padEnd(17)} ${c.name}`);
