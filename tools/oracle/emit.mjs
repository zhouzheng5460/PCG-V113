// 跑一次真实的 Placemaker：写用例 → dotnet emit → 落到 tmp/plans/。
//
// 这是"基准"的来源。TS 侧任何移植都要拿它的输出做对照（docs/A §5 的 L1/L2 分级）。
// 前置：npm run oracle:build（编译 .NET 内核，约 18 秒）。

import { execFileSync } from 'node:child_process';
import { existsSync, mkdirSync, writeFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const root = resolve(here, '../..');
const dll = join(root, 'tools/oracle/driver/bin/Release/net6.0/PlacemakerDriver.dll');
const caseDir = join(root, 'tmp/cases');
const planDir = join(root, 'tmp/plans');

if (!existsSync(dll)) {
  console.error(`找不到 ${dll}\n先跑：npm run oracle:build`);
  process.exit(1);
}
mkdirSync(caseDir, { recursive: true });
mkdirSync(planDir, { recursive: true });

// 150 × 100 的地块，南侧一条 8 m 街（中线在 y = -4，路缘正好压在地界上）
const ring = [
  [0, 0],
  [150, 0],
  [150, 100],
  [0, 100],
];
const streets = [{ id: 9001, a: [-20, -4], b: [170, -4], width: 8, sidewalk: true, speed: 50 }];

const cases = [
  {
    name: 'parking',
    body: { generator: 'parking', seed: 1234, shape: { ring }, streets },
  },
  {
    // 带宗地：edgeZoning 用 "all"（不是 "both"），zoningDepth 单位是 8 m 格
    name: 'parking-zoned',
    body: { generator: 'parking', seed: 1234, shape: { ring }, streets, settings: { edgeZoning: 'all', zoningDepth: 3 } },
  },
  {
    name: 'park-150x100',
    body: { generator: 'park', seed: 7, shape: { ring }, streets },
  },
];

for (const c of cases) {
  const casePath = join(caseDir, `${c.name}.json`);
  const planPath = join(planDir, `${c.name}.json`);
  writeFileSync(casePath, JSON.stringify(c.body, null, 2), 'utf8');
  const out = execFileSync('dotnet', [dll, '--emit', casePath, planPath], { encoding: 'utf8' });
  console.log(out.trim().split('\n')[0]);
}
