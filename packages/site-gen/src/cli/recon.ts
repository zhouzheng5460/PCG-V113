// 生成器侦察（Recon）：把一个生成器的 golden 产出摊成一张表。
//
// 用户 2026-11-03 的 P3.0 要求："不要先看代码。像 Parking 一样直接从 Plan 反推。"
// 所以这个 CLI **只吃 Plan**，不看任何生成器实现 —— 它同时也是"仪器可复用"的证明：
// 同一个工具跑 parking 和 plaza，不需要为 plaza 改一行。
//
// 输出五组（对应"Plaza 五个设计维度"的量化代理）：
//   access       出入口数量、接铺装率、每个口到最近铺装的距离
//   circulation  铺装连通块、最大连通块占比、区域(regions)结构
//   open-space   铺装面积/占地、最大开阔面
//   edge         沿边的树/灌木密度、边界长度
//   focus        中心物件（centerpiece / 水景）
//
// 跑法：
//   node packages/site-gen/src/cli/recon.ts plaza
//   node packages/site-gen/src/cli/recon.ts parking --json

import { area as ringArea, bounds, dist, pointInRing, type Vec2 } from '../../../plan-ir/src/geom.ts';
import { loadGoldenPlan, loadIndex, loadSiteInput } from '../golden.ts';
import { measure } from '../metrics.ts';
import type { Plan } from '../types.ts';

const args = process.argv.slice(2);
const asJson = args.includes('--json');
const genId = args.find((a) => !a.startsWith('-')) ?? 'plaza';

/** 铺装连通块（1 m 栅格 + 并查集）。不依赖第三方库。 */
function pavingComponents(plan: Plan, step = 1): { count: number; areas: number[]; largest: number } {
  const faces = plan.surfaces.filter((s) => s.ring.length >= 3);
  if (!faces.length) return { count: 0, areas: [], largest: 0 };

  let minX = Infinity;
  let minY = Infinity;
  let maxX = -Infinity;
  let maxY = -Infinity;
  for (const f of faces) {
    const b = bounds(f.ring);
    minX = Math.min(minX, b.minX);
    minY = Math.min(minY, b.minY);
    maxX = Math.max(maxX, b.maxX);
    maxY = Math.max(maxY, b.maxY);
  }
  const w = Math.ceil((maxX - minX) / step) + 1;
  const h = Math.ceil((maxY - minY) / step) + 1;
  if (w * h > 4_000_000) return { count: -1, areas: [], largest: 0 }; // 太大就放弃，不硬算

  const filled = new Uint8Array(w * h);
  for (let j = 0; j < h; j++) {
    for (let i = 0; i < w; i++) {
      const p: Vec2 = [minX + i * step, minY + j * step];
      for (const f of faces) {
        if (pointInRing(p, f.ring)) {
          filled[j * w + i] = 1;
          break;
        }
      }
    }
  }

  const seen = new Int32Array(w * h).fill(-1);
  const areas: number[] = [];
  for (let s = 0; s < filled.length; s++) {
    if (!filled[s] || seen[s] >= 0) continue;
    const id = areas.length;
    let n = 0;
    const stack = [s];
    seen[s] = id;
    while (stack.length) {
      const cur = stack.pop() as number;
      n++;
      const ci = cur % w;
      const cj = (cur - ci) / w;
      for (const [di, dj] of [
        [1, 0],
        [-1, 0],
        [0, 1],
        [0, -1],
      ]) {
        const ni = ci + di;
        const nj = cj + dj;
        if (ni < 0 || nj < 0 || ni >= w || nj >= h) continue;
        const k = nj * w + ni;
        if (filled[k] && seen[k] < 0) {
          seen[k] = id;
          stack.push(k);
        }
      }
    }
    areas.push(n * step * step);
  }
  areas.sort((a, b) => b - a);
  return { count: areas.length, areas, largest: areas[0] ?? 0 };
}

const SUM = (xs: number[]): number => xs.reduce((s, v) => s + v, 0);

const index = loadIndex();
const cases = index.cases.filter((c) => c.generator === genId && c.ok);
if (!cases.length) {
  console.error(`语料里没有 generator=${genId} 的用例`);
  process.exit(1);
}

const rows = cases.map((c) => {
  const plan = loadGoldenPlan(c.name);
  const m = measure(plan);
  const comp = pavingComponents(plan);
  const paving = m.surfaceByRole['plaza'] ?? m.surfaceByRole['asphalt'] ?? 0;
  const green = (m.surfaceByRole['grass'] ?? 0) + (m.surfaceByRole['decorative'] ?? 0);

  // 出入口到最近铺装的距离
  const pts = plan.objects.filter((o) => o.role === 'accessPoint');
  const paveFaces = plan.surfaces.filter((s) => s.ring.length >= 3);
  const nearPave = (o: { x: number; z: number }): number => {
    let best = Infinity;
    for (const f of paveFaces) {
      for (const q of f.ring) best = Math.min(best, dist([o.x, o.z], q));
    }
    return best;
  };
  const accessDists = pts.map(nearPave);

  return {
    name: c.name.replace(/^city\d+-s\d+-/, ''),
    area: plan.area,
    ...m,
    paving,
    green,
    pavingRatio: plan.area > 0 ? paving / plan.area : 0,
    greenRatio: plan.area > 0 ? green / plan.area : 0,
    faceCount: plan.surfaces.length,
    largestFace: Math.max(0, ...plan.surfaces.map((s) => Math.abs(ringArea(s.ring)))),
    compCount: comp.count,
    largestOpen: comp.largest,
    largestOpenRatio: plan.area > 0 ? comp.largest / plan.area : 0,
    accessCount: pts.length,
    accessOnPaving: accessDists.filter((d) => d <= 12).length,
    accessDistMed: accessDists.length ? [...accessDists].sort((a, b) => a - b)[Math.floor(accessDists.length / 2)] : -1,
    focusCount: plan.objects.filter((o) => o.role === 'centerpiece').length,
    areaRoles: (plan.areas ?? []).map((a) => `${a.role}:${Math.abs(ringArea(a.ring)).toFixed(0)}`).join(' '),
    regionKeys: (plan.regions ?? []).map((r) => `${r.key}=${Math.abs(ringArea(r.ring)).toFixed(0)}`).join(' '),
    stats: (plan.stats ?? []).map((s) => `${s.key}=${s.value}`).join(' '),
  };
});

if (asJson) {
  console.log(JSON.stringify(rows, null, 1));
  process.exit(0);
}

const f = (v: number, d = 0): string => (Number.isFinite(v) ? v.toFixed(d) : '—');
const pct = (v: number): string => `${(v * 100).toFixed(0)}%`;

console.log(`\n=== generator=${genId} · ${rows.length} 例 ===\n`);
for (const r of rows) {
  console.log(`── ${r.name} · 地块 ${f(r.area)} m² ──`);
  console.log(`  open-space   铺装 ${f(r.pavingRatio * 100, 1)}% · 绿化 ${f(r.greenRatio * 100, 1)}% · 面片 ${r.faceCount} 块（最大 ${f(r.largestFace)} m²）`);
  console.log(`  circulation  连通块 ${r.compCount} · 最大开阔 ${f(r.largestOpen)} m²（${pct(r.largestOpenRatio)}）`);
  console.log(`  access       出入口 ${r.accessCount} 个 · 接铺装(≤12m) ${r.accessOnPaving} 个 · 中位距离 ${f(r.accessDistMed, 1)} m`);
  console.log(`  focus        中心物件 ${r.focusCount}`);
  console.log(`  edge         树 ${r.objectByRole['tree'] ?? 0} · 灌木 ${r.objectByRole['bush'] ?? 0} · 花池 ${r.objectByRole['planter'] ?? 0} · 长椅 ${r.objectByRole['bench'] ?? 0} · 灯 ${r.objectByRole['lamp'] ?? 0}`);
  console.log(`  通道         surfaces ${r.surfaceByRole ? Object.keys(r.surfaceByRole).length : 0} 类 · nets ${r.netCount} 条（总长 ${f(r.netTotal)} m）· areas ${r.areaRoles || '—'}`);
  console.log(`  regions      ${r.regionKeys || '—'}`);
  console.log(`  stats        ${r.stats || '—'}\n`);
}

// 汇总区间（用来定门禁阈值）
const rng = (xs: number[]): string => {
  const v = xs.filter((x) => Number.isFinite(x)).sort((a, b) => a - b);
  if (!v.length) return '—';
  return `${f(v[0], v[0] < 10 ? 2 : 0)} ~ ${f(v[v.length - 1], v[v.length - 1] < 10 ? 2 : 0)}`;
};
console.log('── 汇总区间（定阈值用）──');
console.log(`  铺装占比     ${rng(rows.map((r) => r.pavingRatio * 100))} %`);
console.log(`  绿化占比     ${rng(rows.map((r) => r.greenRatio * 100))} %`);
console.log(`  连通块       ${rng(rows.map((r) => r.compCount))}`);
console.log(`  最大开阔占比 ${rng(rows.map((r) => r.largestOpenRatio * 100))} %`);
console.log(`  面片数       ${rng(rows.map((r) => r.faceCount))}`);
console.log(`  出入口       ${rng(rows.map((r) => r.accessCount))}`);
console.log(`  物件密度     ${rng(rows.map((r) => r.objectDensity))} /千m²`);
