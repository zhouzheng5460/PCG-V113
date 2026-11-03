// C0.0 · 城市诊断 CLI + **视觉 Gate**
//
// 两件事：
//   1. 打印城市级诊断表（landUseCoherence / neighborHeightJump / roadHierarchyRatio / districtTransition + 常规体检）
//   2. 出一张 **只画规划层** 的图：用途色块 / 道路等级 / 高度热力 三联
//
// 为什么要有这张图（用户 2026-11-03）：
//   "关闭建筑 Mesh，只显示 District 色块 + 道路等级 + 高度热力图。如果这一张二维规划图都不好看，
//    禁止打开 Building / Parking / Plaza 来掩盖问题。"
//
// 跑法：
//   node node_modules/tsx/dist/cli.mjs src/diag/cli.ts            # 默认 15 街区 / seed 682063530
//   node node_modules/tsx/dist/cli.mjs src/diag/cli.ts 24 12345   # 指定规模与种子
//   node node_modules/tsx/dist/cli.mjs src/diag/cli.ts --out path.html

import { mkdirSync, writeFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { Model } from '../town/model';
import { Pt } from '../core/geom';
import { USE_LABEL } from '../town/landuse';
import type { LandUse } from '../scene/types';
import { blocksForVis, edgesForVis, urbanMetrics, type UrbanMetrics } from './urban';

const args = process.argv.slice(2);
const outIdx = args.indexOf('--out');
const outPath = outIdx >= 0 ? resolve(args[outIdx + 1]) : resolve('tools/urban/urban-recon.html');
const nums = args.filter((a) => /^\d+$/.test(a));
const n = nums[0] ? Number(nums[0]) : 15;
const seed = nums[1] ? Number(nums[1]) : 682063530;

const model = new Model({ nPatches: n, seed, plaza: true });
model.generate();

const m = urbanMetrics(model);

// ─────────────────────────────────────────── 表格

const f = (v: number, d = 1): string => (Number.isFinite(v) ? v.toFixed(d) : '—');
const pc = (v: number): string => `${(v * 100).toFixed(0)}%`;

const lines: string[] = [];
const line = (s = ''): void => {
  lines.push(s);
  console.log(s);
};

line(`\n=== 城市诊断 · n=${n} seed=${seed} · ${m.patches} 街区 / ${m.buildings} 栋 ===\n`);

line('── 1. 用途连贯性（landUseCoherence）──');
line(`  相邻同用途比例（邻居里同用途的均值）   ${pc(m.neighborUseAgreement)}   ← 低于 50% 基本就是撒芝麻`);
line(`  代理片区纯度（k-means k=6 内最大用途）  ${pc(m.districtPurity)}   ← 目标 ≥ 70%`);
line(`  用途占比  ${Object.entries(m.useShare).map(([k, v]) => `${USE_LABEL[k as LandUse] ?? k} ${pc(v as number)}`).join(' · ') || '—'}`);

line('\n── 2. 相邻高差（neighborHeightJump）──');
line(`  p50 ${f(m.heightJump.p50)} m · p90 ${f(m.heightJump.p90)} m · max ${f(m.heightJump.max)} m   （${m.heightJump.pairs} 对相邻街区）`);
line(`  楼层高度  mean ${f(m.height.mean)} · p50 ${f(m.height.p50)} · p90 ${f(m.height.p90)} · max ${f(m.height.max)} m`);

line('\n── 3. 道路等级配比（roadHierarchyRatio）──');
line(`  主干 arterial   ${f(m.roadByClass.arterial.share * 100, 0)}%  (${f(m.roadByClass.arterial.length, 0)} m)`);
line(`  次干 collector  ${f(m.roadByClass.collector.share * 100, 0)}%  (${f(m.roadByClass.collector.length, 0)} m)`);
line(`  支路 local      ${f(m.roadByClass.local.share * 100, 0)}%  (${f(m.roadByClass.local.length, 0)} m)`);
line(`  节点 ${m.intersections.nodes} · 断头 ${m.intersections.deadEnds} · 最大度 ${m.intersections.maxDegree}`);

line('\n── 4. 片区过渡（districtTransition）──');
line(`  相邻异用途对 ${m.districtTransition.incompatible} / ${m.districtTransition.pairs} = ${pc(m.districtTransition.share)} 不兼容（工业↔住宅、CBD↔住宅、停车↔公园…）`);

line('\n── 常规体检 ──');
line(`  街区面积  p10 ${f(m.blockArea.p10, 0)} · p50 ${f(m.blockArea.p50, 0)} · p90 ${f(m.blockArea.p90, 0)} · max ${f(m.blockArea.max, 0)} m²`);
line(`  宗地面积  p10 ${f(m.parcelArea.p10, 0)} · p50 ${f(m.parcelArea.p50, 0)} · p90 ${f(m.parcelArea.p90, 0)} · max ${f(m.parcelArea.max, 0)} m²`);
line(`  覆盖率 ${pc(m.coverage)} · FAR≈${f(m.far, 2)} · 开敞 ${pc(m.openSpaceRatio)}（最大连片占 ${pc(m.largestOpenShare)}）`);

// ─────────────────────────────────────────── 门禁（--check）

/**
 * C0 的验收目标（用户 2026-11-03 定）。
 *
 * 现在**全部不达标是正常的** —— 这一版是基线。它的作用是：
 * C0.1 / C0.2 / C0.3 / C0.4 每改一步，都用同一把尺子量，看哪条翻绿了。
 */
if (args.includes('--check')) {
  const T = [
    { k: '同用途纯度 ≥ 70%', v: m.districtPurity, ok: m.districtPurity >= 0.7, got: pc(m.districtPurity) },
    { k: '相邻高差 p90 ≤ 20 m', v: m.heightJump.p90, ok: m.heightJump.p90 <= 20, got: `${f(m.heightJump.p90)} m` },
    { k: '主干 ≥ 8%（有骨架）', v: m.roadByClass.arterial.share, ok: m.roadByClass.arterial.share >= 0.08, got: pc(m.roadByClass.arterial.share) },
    { k: '支路 ≤ 40%（不靠毛细血管）', v: m.roadByClass.local.share, ok: m.roadByClass.local.share <= 0.4, got: pc(m.roadByClass.local.share) },
    { k: '不兼容相邻 ≤ 10%', v: m.districtTransition.share, ok: m.districtTransition.share <= 0.1, got: pc(m.districtTransition.share) },
    { k: '开敞空间 ≥ 10%', v: m.openSpaceRatio, ok: m.openSpaceRatio >= 0.1, got: pc(m.openSpaceRatio) },
  ];
  line('\n── C0 门禁（--check）──');
  for (const t of T) line(`  ${t.ok ? 'ok  ' : 'FAIL'} ${t.k.padEnd(24)} 实际 ${t.got}`);
  const bad = T.filter((t) => !t.ok).length;
  line(`\n${bad === 0 ? 'PASS' : 'FAIL'} — ${T.length} 项，${bad} 项未达标`);
  if (bad) process.exitCode = 1;
}

// ─────────────────────────────────────────── 视觉 Gate

const USE_FILL: Record<string, string> = {
  cbd: '#8e93a8',
  commercial: '#cf7a5a',
  residential: '#d9c07a',
  industrial: '#9a7ab5',
  civic: '#6f9ec4',
  park: '#4f9e5f',
  parking: '#6b7280',
  green: '#5aa86a',
  reserved: '#807a70',
};

const blocks = blocksForVis(model);
const edges = edgesForVis(model);
const pts: Pt[] = blocks.flatMap((b) => b.ring);
const minX = Math.min(...pts.map((p) => p.x));
const maxX = Math.max(...pts.map((p) => p.x));
const minY = Math.min(...pts.map((p) => p.y));
const maxY = Math.max(...pts.map((p) => p.y));
const pad = 40;
const W = 720;
const H = Math.round((W * (maxY - minY)) / Math.max(1, maxX - minX));

const sx = (x: number): number => ((x - minX) / Math.max(1e-6, maxX - minX)) * (W - 2 * pad) + pad;
const sy = (y: number): number => ((maxY - y) / Math.max(1e-6, maxY - minY)) * (H - 2 * pad) + pad;
const pathOf = (ring: Pt[]): string =>
  ring.map((p, i) => `${i ? 'L' : 'M'}${sx(p.x).toFixed(1)},${sy(p.y).toFixed(1)}`).join('') + 'Z';

/** 高度热力：0 → 冷蓝，最高 → 暖红 */
const heat = (h: number, hmax: number): string => {
  if (h <= 0) return 'rgba(255,255,255,0.04)';
  const t = Math.min(1, h / Math.max(1, hmax));
  const r = Math.round(60 + t * 195);
  const g = Math.round(120 - t * 60);
  const b = Math.round(230 - t * 190);
  return `rgb(${r},${g},${b})`;
};

const panel = (title: string, inner: string, note: string): string => `
  <figure>
    <figcaption><b>${title}</b><span>${note}</span></figcaption>
    <svg viewBox="0 0 ${W} ${H}" width="100%" height="auto">${inner}</svg>
  </figure>`;

const usePanel = panel(
  '① 用途色块',
  blocks
    .map((b) => `<path d="${pathOf(b.ring)}" fill="${USE_FILL[b.use ?? 'reserved'] ?? '#807a70'}" fill-opacity="0.78" stroke="rgba(0,0,0,0.45)" stroke-width="0.8"/>`)
    .join(''),
  '颜色 = 用地类型；块内不画建筑',
);

const roadPanel = panel(
  '② 道路等级',
  blocks.map((b) => `<path d="${pathOf(b.ring)}" fill="rgba(255,255,255,0.03)" stroke="none"/>`).join('') +
    edges
      .map((e) => {
        const w = e.cls === 'arterial' ? 5 : e.cls === 'collector' ? 2.4 : 1.1;
        const c = e.cls === 'arterial' ? '#e8b455' : e.cls === 'collector' ? '#9fb0c6' : '#5d6774';
        return `<line x1="${sx(e.a.x).toFixed(1)}" y1="${sy(e.a.y).toFixed(1)}" x2="${sx(e.b.x).toFixed(1)}" y2="${sy(e.b.y).toFixed(1)}" stroke="${c}" stroke-width="${w}" stroke-linecap="round"/>`;
      })
      .join(''),
  '橙=主干 · 银=次干 · 灰=支路',
);

const hmax = Math.max(1, m.height.max);
const heightPanel = panel(
  '③ 高度热力',
  blocks
    .map((b) => `<path d="${pathOf(b.ring)}" fill="${heat(b.height, hmax)}" stroke="rgba(0,0,0,0.35)" stroke-width="0.6"/>`)
    .join(''),
  `冷→暖 = 0 → ${f(hmax, 0)} m`,
);

const html = `<!doctype html><html lang="zh-CN"><head><meta charset="utf-8"/>
<title>城市诊断 · n=${n} seed=${seed}</title>
<style>
  body{margin:0;background:#12151b;color:#e6ebf3;font:13px/1.6 system-ui,"Segoe UI",sans-serif}
  header{padding:18px 24px 8px}
  h1{font-size:16px;margin:0 0 4px}
  .sub{color:#8b95a5;font-size:12px}
  main{display:grid;grid-template-columns:repeat(3,1fr);gap:14px;padding:14px 24px 24px}
  figure{margin:0;background:#171b23;border:1px solid rgba(255,255,255,.07);border-radius:10px;padding:10px}
  figcaption{display:flex;justify-content:space-between;align-items:baseline;margin-bottom:6px;font-size:12px}
  figcaption span{color:#8b95a5;font-size:11px}
  section{padding:0 24px 26px}
  table{border-collapse:collapse;font-size:12px;width:100%;max-width:900px}
  th,td{padding:5px 10px;border-bottom:1px solid rgba(255,255,255,.06);text-align:left}
  th{color:#96a0b0;font-weight:500}
  td.v{font-variant-numeric:tabular-nums;color:#dfe6f0}
  .warn{color:#e8b455}
  .bad{color:#dc6b5a}
</style></head><body>
<header>
  <h1>城市诊断 · n=${n} · seed=${seed}</h1>
  <div class="sub">${m.patches} 街区 · ${m.buildings} 栋 · 街区面积 p50 ${f(m.blockArea.p50, 0)} m²</div>
</header>
<main>${usePanel}${roadPanel}${heightPanel}</main>
<section>
  <table>
    <tr><th>指标</th><th>值</th><th>判据</th></tr>
    <tr><td>相邻同用途比例</td><td class="v ${m.neighborUseAgreement < 0.5 ? 'bad' : ''}">${pc(m.neighborUseAgreement)}</td><td>目标 ≥ 70%</td></tr>
    <tr><td>代理片区纯度</td><td class="v ${m.districtPurity < 0.7 ? 'bad' : ''}">${pc(m.districtPurity)}</td><td>目标 ≥ 70%</td></tr>
    <tr><td>相邻高差 p90</td><td class="v ${m.heightJump.p90 > 40 ? 'bad' : ''}">${f(m.heightJump.p90)} m</td><td>目标 ≤ 40 m</td></tr>
    <tr><td>主干占比</td><td class="v ${m.roadByClass.arterial.share < 0.05 ? 'warn' : ''}">${pc(m.roadByClass.arterial.share)}</td><td>期望 10% 量级</td></tr>
    <tr><td>支路占比</td><td class="v">${pc(m.roadByClass.local.share)}</td><td>期望 30~50%</td></tr>
    <tr><td>不兼容相邻对</td><td class="v ${m.districtTransition.share > 0.15 ? 'bad' : ''}">${pc(m.districtTransition.share)}</td><td>目标 ≤ 10%</td></tr>
    <tr><td>覆盖率 / FAR</td><td class="v">${pc(m.coverage)} / ${f(m.far, 2)}</td><td>—</td></tr>
    <tr><td>开敞空间</td><td class="v">${pc(m.openSpaceRatio)}</td><td>—</td></tr>
  </table>
</section>
</body></html>
`;

mkdirSync(dirname(outPath), { recursive: true });
writeFileSync(outPath, html);
line(`\n[out] ${outPath}`);
