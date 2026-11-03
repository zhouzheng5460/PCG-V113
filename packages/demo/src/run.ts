// 单方案验收（细节图 + 逐点对照）：把"我们自己的路网数据"喂进契约，并拿真 C# 当对照。
//
// 新增两件事：
//   1) C# vs TS 的贴合点逐点比对（真 AttachmentFinder 当基准，不再自己对自己）
//   2) PCG 数据 → CaseInput 的适配器（`parcelToCase`）：地块 + 折线道路 + 出入口 + 建筑
//
// 跑法：node packages/demo/src/run.ts
// 产物：packages/demo/out/round2.html

import { execFileSync } from 'node:child_process';
import { readFileSync, writeFileSync, mkdirSync, existsSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

import { type Vec2 } from '../../plan-ir/src/geom.ts';
import { invariants, formatDiff, diffL1 } from '../../plan-ir/src/normalize.ts';
import { validateCase, formatReport } from '../../street-rules/src/rules.ts';
import { findAttachments } from '../../street-rules/src/finder.ts';
import { compareAttachments, formatCompare, type CSharpAttachments } from '../../street-rules/src/oracle.ts';
import { cutPlan } from '../../slots/src/cut.ts';
import { DEFAULT_ASSETS, matchAll } from '../../slots/src/skin.ts';
import { validateSlots, fingerprint } from '../../slots/src/validate.ts';
import { parcelToCase, zoneGridCells, zoneKindOf } from '../../pcg-bridge/src/case.ts';
import type { PcgParcel, PcgRoad } from '../../pcg-bridge/src/model.ts';
import { allCases } from './cases.ts';
import { svg2D, svgIso, kindColor, type Box3, type Scene2D } from './render.ts';
import type { Plan } from '../../plan-ir/src/types.ts';

const here = dirname(fileURLToPath(import.meta.url));
const root = resolve(here, '../../..');
const outDir = join(root, 'packages/demo/out');
const planPath = join(root, 'tmp/plans/parking-zoned.json');
const dll = join(root, 'tools/oracle/driver/bin/Release/net6.0/PlacemakerDriver.dll');

const lines: string[] = [];
const log = (s = ''): void => {
  lines.push(s);
  console.log(s);
};

function main(): void {
  if (!existsSync(outDir)) mkdirSync(outDir, { recursive: true });
  mkdirSync(join(root, 'tmp/cases'), { recursive: true });
  mkdirSync(join(root, 'tmp/plans'), { recursive: true });

  // ─────────────────────────────── 1. 街道契约（沿用第一轮的四个用例）
  log('══ 一 · 街道契约 R1–R11 ══');
  const cases = allCases();
  const caseResults = cases.map((c) => {
    const rep = validateCase(c.input);
    log('');
    log(`── ${c.name}｜${c.note}`);
    log(formatReport(rep));
    return { c, rep };
  });

  // ─────────────────────────────── 2. 真 C# 对照
  log('');
  log('══ 二 · 与真 C# 的贴合点对照（AttachmentFinder 当基准）══');
  const hasOracle = existsSync(dll);
  const compareRows: { name: string; text: string; ok: boolean }[] = [];

  if (!hasOracle) {
    log(`缺少 ${dll}，跳过（先跑 npm run oracle:build）`);
  } else {
    for (const { c } of caseResults) {
      // 写一份 C# 认得的 case（只读 shape.ring 与 streets）
      const caseFile = join(root, `tmp/cases/att-${safeName(c.name)}.json`);
      const outFile = join(root, `tmp/plans/att-${safeName(c.name)}.json`);
      writeFileSync(
        caseFile,
        JSON.stringify(
          {
            shape: { ring: c.ring2d },
            streets: c.streets.map((s) => ({ id: s.id, a: s.a, b: s.b, width: s.w, sidewalk: true, speed: 50 })),
          },
          null,
          1,
        ),
        'utf8',
      );
      execFileSync('dotnet', [dll, '--attachments', caseFile, outFile], { encoding: 'utf8' });

      const cs = JSON.parse(readFileSync(outFile, 'utf8')) as CSharpAttachments;
      const ts = findAttachments(c.ring2d, c.input.site.nearbyNets);
      const cmp = compareAttachments(ts, cs, 0.01, c.input.site.nearbyNets);
      const ok = cmp.fail === 0 && cmp.onlyTs.length === 0 && cmp.onlyCs.length === 0;
      const text = formatCompare(c.name, cmp);
      log(`  ${text.split('\n').join('\n  ')}`);
      log(`    C# 点数 ${cs.count}｜TS 点数 ${ts.length}`);
      compareRows.push({ name: c.name, text, ok });
    }
  }

  // ─────────────────────────────── 3. 真实方案 + 楼位
  log('');
  log('══ 三 · 真实 Placemaker 方案 → 楼位 ══');
  let plan: Plan | null = null;
  let cutSlotsHtml = '';
  let isoHtml = '';

  if (existsSync(planPath)) {
    plan = JSON.parse(readFileSync(planPath, 'utf8')) as Plan;
    const ring2d: Vec2[] = [
      [0, 0],
      [150, 0],
      [150, 100],
      [0, 100],
    ];
    const kind = 'commercialLow' as const;
    const cut = cutPlan(plan.zoning, kind, [1, 0]);
    const skins = matchAll(cut.slots, DEFAULT_ASSETS, plan.seed);
    // E3 需要原始宗地四边形，直接传进去（不传就会显示"跳过"，不会假装通过）
    const slotRep = validateSlots(cut.slots, cut.zoningArea, skins, plan!.zoning.map((z) => z.quad));

    log(`生成器 ${plan.generator}｜面 ${plan.surfaces.length}｜渠 ${plan.nets.length}｜物件 ${plan.objects.length}｜宗地 ${plan.zoning.length}`);
    log(`楼位 ${cut.slots.length} 个（${[...new Set(cut.slots.map((s) => `${s.width}×${s.depth}`))].join('、')}），边角料 ${cut.waste.toFixed(1)} m²，指纹 ${slotRep.fingerprint}`);
    for (const c of slotRep.checks) log(`  ${c.ok ? '✓' : '✗'} ${c.id} ${c.title} — ${c.summary}`);

    const sceneReal: Scene2D = {
      parcel: ring2d,
      streets: [
        { a: [0, -4], b: [150, -4], w: 8 },
        { a: [154, 0], b: [154, 100], w: 8 },
        { a: [150, 104], b: [0, 104], w: 8 },
        { a: [-4, 100], b: [-4, 0], w: 8 },
      ],
      attachments: cases[0].input.shape.attachments.map((a) => ({ p: a.position, kind: a.kind })),
      surfaces: plan.surfaces.map((s) => ({ ring: s.ring, role: s.role })),
      zoning: plan.zoning.map((z) => ({ quad: [...z.quad] as Vec2[] })),
      slots: cut.slots.map((s) => ({ quad: [...s.quad] as Vec2[], color: kindColor(s.kind), dashed: !s.moduleWidth })),
    };
    cutSlotsHtml = svg2D(sceneReal, 620);
    isoHtml = svgIso(
      cut.slots.map((s, i) => ({ quad: [...s.quad] as Vec2[], h: skins[i].height, color: kindColor(s.kind) })),
      620,
      { quad: ring2d, h: 0.4, color: '#2b2b34' },
    );

    // L1 自检
    const inv = invariants(plan);
    log('');
    log('L1 不变量自检：');
    log(formatDiff(diffL1(inv, invariants(plan))));
    log(`  面 ${Object.entries(inv.surfaceAreaByRole).map(([k, v]) => `${k} ${v.toFixed(0)}m²`).join('，')}｜渠 ${inv.netCount} 条 ${Object.values(inv.netLengthByRole).reduce((a, b) => a + b, 0).toFixed(0)} m｜宗地 ${inv.zoningArea} m²`);
  }

  // ─────────────────────────────── 4. PCG 数据 → 契约（适配器）
  log('');
  log('══ 四 · PCG 数据 → 契约（parcelStreets + zoneGridOf）══');

  // 一个"PCG 里画出来的"地块：L 形、四面环路、北侧路带一个折点（考验多段折线）
  const pcgParcel: PcgParcel = {
    id: 'P-2041',
    landUse: 'urban',
    ring: [
      [0, 0],
      [160, 0],
      [160, 60],
      [90, 60],
      [90, 110],
      [0, 110],
    ],
  };
  const pcgRoads: PcgRoad[] = [
    { id: 'road-s', centerline: [[0, -4], [160, -4]], roadClass: 'secondary', width: 8, sidewalk: true },
    { id: 'road-e', centerline: [[164, 0], [164, 60]], roadClass: 'local', width: 6, sidewalk: true },
    { id: 'road-n', centerline: [[160, 114], [80, 116], [0, 114]], roadClass: 'secondary', width: 8, sidewalk: true },
    { id: 'road-w', centerline: [[-4, 110], [-4, 0]], roadClass: 'primary', width: 12, sidewalk: true },
    // 一条园路穿过地块内部（不算车行可达，用来看 R7 不会误判）
    { id: 'path-1', centerline: [[40, -4], [40, 110]], roadClass: 'pedestrian', width: 3, sidewalk: false },
  ];

  const built = parcelToCase(
    pcgParcel,
    pcgRoads,
    [{ id: 'ac-1', pos: [76, 0], width: 8, dir: Math.PI / 2, accessType: 'main' }],
    [
      { id: 'b-1', ring: [[20, 20], [56, 20], [56, 46], [20, 46]] },
      { id: 'b-2', ring: [[100, 20], [140, 20], [140, 50], [100, 50]] },
    ],
    { depthCells: 3, keepBuildings: false, seed: 5 },
  );

  log(`地块 ${pcgParcel.id}（${pcgParcel.landUse} → ${zoneKindOf(pcgParcel)}）面积 ${areaOf(pcgParcel.ring).toFixed(0)} m²`);
  log(`道路 ${pcgRoads.length} 条 → 街道段 ${built.input.site.nearbyNets.length} 条（折线按段拆）`);
  log(`贴合点 ${built.attachments.length} 个（沿边 ${built.attachments.filter((a) => a.kind === 'roadAlongEdge').length}，节点 ${built.attachments.filter((a) => a.kind === 'roadNode').length}）`);
  log(`宗地格网 ${built.zoneGrids.length} 个：${built.zoneGrids.map((g) => `${g.size[0]}×${g.size[1]} 格`).join('、')}`);
  log(`出入口 → marker ${built.input.markers.length} 个（${built.input.markers.map((m) => m.kind).join('、')}）`);
  for (const n of built.notes) log(`  注：${n}`);

  const adapterRep = validateCase(built.input);
  log('');
  log(formatReport(adapterRep));

  const adapterScene: Scene2D = {
    parcel: pcgParcel.ring as Vec2[],
    streets: pcgRoads.map((r) => ({ a: r.centerline[0], b: r.centerline[r.centerline.length - 1], w: r.width })),
    attachments: built.attachments.map((a) => ({ p: a.position, kind: a.kind })),
    gridCells: built.zoneGrids.flatMap((g) => zoneGridCells(g)),
    badEdges: adapterRep.edges.filter((e) => e.along.length > 0 && !built.attachments.some((a) => a.kind === 'roadAlongEdge' && a.ringSegment === e.seg)).map((e) => e.seg),
    nearEdges: adapterRep.edges.filter((e) => e.along.length === 0 && e.nearMiss).map((e) => e.seg),
  };

  // 同一份 PCG 数据也交给真 C#
  let adapterCompare = '';
  if (hasOracle) {
    const caseFile = join(root, 'tmp/cases/att-pcg-adapter.json');
    const outFile = join(root, 'tmp/plans/att-pcg-adapter.json');
    writeFileSync(
      caseFile,
      JSON.stringify(
        {
          shape: { ring: pcgParcel.ring },
          streets: built.input.site.nearbyNets.map((n) => ({
            id: n.externalId,
            a: n.a,
            b: n.d,
            width: n.width,
            category: n.category,
            sidewalk: n.hasSidewalk,
          })),
        },
        null,
        1,
      ),
      'utf8',
    );
    execFileSync('dotnet', [dll, '--attachments', caseFile, outFile], { encoding: 'utf8' });
    const cs = JSON.parse(readFileSync(outFile, 'utf8')) as CSharpAttachments;
    const ts = findAttachments(pcgParcel.ring as Vec2[], built.input.site.nearbyNets);
    const cmp = compareAttachments(ts, cs, 0.01, built.input.site.nearbyNets);
    adapterCompare = formatCompare('PCG 适配器地块', cmp);
    log('');
    log(adapterCompare.split('\n').join('\n  '));
  }

  // ─────────────────────────────── 5. 出图
  const caseScenes = caseResults.map(({ c, rep }) => {
    const hasAlong = (seg: number) => c.input.shape.attachments.some((a) => a.kind === 'roadAlongEdge' && a.ringSegment === seg);
    return {
      name: c.name,
      note: c.note,
      svg: svg2D(
        {
          parcel: c.ring2d as Vec2[],
          streets: c.streets.map((s) => ({ a: s.a as Vec2, b: s.b as Vec2, w: s.w })),
          attachments: c.input.shape.attachments.map((a) => ({ p: a.position, kind: a.kind })),
          badEdges: rep.edges.filter((e) => e.along.length > 0 && !hasAlong(e.seg)).map((e) => e.seg),
          nearEdges: rep.edges.filter((e) => e.along.length === 0 && e.nearMiss).map((e) => e.seg),
        },
        420,
      ),
      report: rep,
    };
  });

  const extra: { title: string; html: string }[] = [];

  extra.push({
    title: 'C# vs TS 贴合点对照',
    html: compareRows.length
      ? `<table><thead><tr><th style="width:220px">用例</th><th style="width:70px">结果</th><th>逐项</th></tr></thead><tbody>${compareRows
          .map(
            (r) =>
              `<tr class="${r.ok ? 'ok' : 'bad'}"><td>${r.name}</td><td>${r.ok ? '一致' : '有差异'}</td><td><pre style="max-height:160px;margin:0">${esc(r.text)}</pre></td></tr>`,
          )
          .join('')}</tbody></table>`
      : '<p style="color:#8a8a96">未编译 C# 基准，跳过。</p>',
  });

  extra.push({
    title: 'PCG 数据 → 契约（适配器）',
    html: `<div class="grid"><div class="card">${svg2D(adapterScene, 620)}<p>蓝色网格 = 由临街边生成的 8 m 宗地格；青色点 = 贴合点；灰带 = PCG 折线道路</p></div>
      <div class="card"><pre style="margin:0">${esc(
        [
          `地块 ${pcgParcel.id}｜${pcgParcel.landUse} → ${zoneKindOf(pcgParcel)}｜${areaOf(pcgParcel.ring).toFixed(0)} m²`,
          `道路 ${pcgRoads.length} 条 → 街道段 ${built.input.site.nearbyNets.length} 条`,
          `贴合点 ${built.attachments.length}（沿边 ${built.attachments.filter((a) => a.kind === 'roadAlongEdge').length}，节点 ${built.attachments.filter((a) => a.kind === 'roadNode').length}）`,
          `宗地格网 ${built.zoneGrids.map((g) => `${g.size[0]}×${g.size[1]}`).join('、')}`,
          `marker ${built.input.markers.map((m) => m.kind).join('、')}`,
          '',
          formatReport(adapterRep),
          adapterCompare ? '\n' + adapterCompare : '',
        ].join('\n'),
      )}</pre></div></div>`,
  });

  const html = page('第二轮验收', lines, caseScenes, [
    { title: '真实方案 2D', svg: cutSlotsHtml, note: 'Placemaker 铺装/渠/宗地 + 我们切的楼位（斜纹=非模数补位块）' },
    { title: '真实方案 等轴', svg: isoHtml, note: '楼位按 8 m 模数切，高度来自资产匹配（或盒子兜底）' },
  ], extra);

  writeFileSync(join(outDir, 'round2.html'), html, 'utf8');
  log('');
  log(`报告 → ${join(outDir, 'round2.html')}`);
}

function areaOf(ring: readonly Vec2[]): number {
  let s = 0;
  for (let i = 0, j = ring.length - 1; i < ring.length; j = i++) s += ring[j][0] * ring[i][1] - ring[i][0] * ring[j][1];
  return Math.abs(s / 2);
}

const safeName = (s: string): string => s.replace(/[^\w\u4e00-\u9fa5-]/g, '').slice(0, 24) || 'case';
const esc = (s: string): string => s.replace(/&/g, '&amp;').replace(/</g, '&lt;');

interface CaseCard {
  name: string;
  note: string;
  svg: string;
  report: ReturnType<typeof validateCase>;
}

function page(
  title: string,
  consoleLines: string[],
  cases: CaseCard[],
  big: { title: string; svg: string; note: string }[],
  extra: { title: string; html: string }[],
): string {
  const ruleRow = (r: CaseCard['report']['rules'][number]) =>
    `<tr class="${r.ok ? 'ok' : 'bad'}"><td>${r.id}</td><td>${r.title}</td><td>${r.ok ? '通过' : '失败'}</td><td>${r.summary}${
      r.violations.length
        ? '<ul>' + r.violations.map((v) => `<li class="v-${v.level}">${v.message}${v.evidence ? `<code>${v.evidence}</code>` : ''}</li>`).join('') + '</ul>'
        : ''
    }</td></tr>`;

  return `<!doctype html>
<html lang="zh-CN"><head><meta charset="utf-8"><title>${title}</title>
<style>
  :root{--bg:#12121a;--panel:#1b1b21;--line:#2c2c36;--text:#e8e6e1;--dim:#8a8a96;--ok:#5dcaa5;--bad:#e24b4a;--warn:#ef9f27;--accent:#9b8bf0}
  *{box-sizing:border-box}
  body{margin:0;background:var(--bg);color:var(--text);font:14px/1.6 "Microsoft YaHei","PingFang SC",system-ui,sans-serif}
  header{padding:24px 28px 8px}
  h1{margin:0 0 4px;font-size:20px;font-weight:500}
  .sub{color:var(--dim);font-size:13px}
  main{padding:8px 28px 40px;display:grid;gap:18px}
  section{background:var(--panel);border:1px solid var(--line);border-radius:12px;padding:16px 18px}
  h2{font-size:15px;font-weight:500;margin:0 0 10px}
  h3{font-size:13px;font-weight:500;margin:14px 0 6px;color:var(--dim)}
  table{border-collapse:collapse;width:100%;font-size:13px}
  th,td{text-align:left;padding:6px 8px;border-bottom:1px solid var(--line);vertical-align:top}
  th{color:var(--dim);font-weight:400}
  tr.ok td:nth-child(2),tr.ok td:nth-child(3){color:var(--ok)}
  tr.bad td:nth-child(2),tr.bad td:nth-child(3){color:var(--bad)}
  ul{margin:4px 0 0;padding-left:18px}
  li{color:var(--warn)}
  li.v-error{color:var(--bad)}
  li.v-info{color:var(--dim)}
  code{color:var(--accent);background:#00000055;padding:1px 5px;border-radius:4px;margin-left:6px;font-size:12px}
  .grid{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:16px}
  .grid4{display:grid;grid-template-columns:repeat(4,minmax(0,1fr));gap:14px}
  .card{background:#17171d;border:1px solid var(--line);border-radius:10px;padding:10px}
  .card p{margin:8px 0 0;color:var(--dim);font-size:12px}
  pre{background:#0e0e14;border:1px solid var(--line);border-radius:8px;padding:12px;overflow:auto;font-size:12px;line-height:1.55;color:#c9c7c1;max-height:460px}
  details summary{cursor:pointer;color:var(--dim);font-size:13px;margin:8px 0}
</style></head>
<body>
<header>
  <h1>${title} · 契约对照 + 自有路网接入</h1>
  <div class="sub">基准：真实 Placemaker 内核（.NET 6，0 错误 0 警告）｜对照：Core/Tool/AttachmentFinder 逐点</div>
</header>
<main>
  <section>
    <h2>四个街道用例</h2>
    <div class="grid4">
      ${cases.map((c) => `<div class="card">${c.svg}<p><b>${c.name}</b><br>${c.note}</p></div>`).join('')}
    </div>
    <h3>逐条规则</h3>
    <table><thead><tr><th style="width:52px">编号</th><th style="width:130px">规则</th><th style="width:60px">结果</th><th>证据</th></tr></thead>
    <tbody>${cases.map((c) => `<tr><td colspan="4" style="color:var(--dim);padding-top:14px">${c.name}</td></tr>` + c.report.rules.map(ruleRow).join('')).join('')}</tbody></table>
  </section>
  ${extra.map((e) => `<section><h2>${e.title}</h2>${e.html}</section>`).join('\n  ')}
  <section>
    <h2>${big.map((b) => b.title).join(' / ')}</h2>
    <div class="grid">${big.map((b) => `<div class="card">${b.svg}<p>${b.note}</p></div>`).join('')}</div>
  </section>
  <section>
    <h2>运行日志</h2>
    <details><summary>展开 / 收起</summary><pre>${esc(consoleLines.join('\n'))}</pre></details>
  </section>
</main></body></html>`;
}

main();
