// 批量回归：拿**真实城市**的 54 份烘焙输入 + 4 份真实方案，把整套验收跑一遍。
//
// 为什么需要它：前面四个用例（A/B/C/D）是我们自己造的，形状规整、直来直去。
// 真实城市的地块是斜的、被切碎的、边宽 10.5/20 m 混排的——只有在这种几何上，
// 「贴合点算法是不是真能对上 C#」「切楼会不会切出 3 m 的畸形块」才会暴露。
//
// 夹具来源（`tools/batch/`，见那里的 README）：
//   cases/  ← CITYSKYLINE `townforge-enhance/tmp/pm-cases/*.json`
//             每份 = { generator, seed, shape.ring, streets[] }，是烘焙时的**原始输入**
//   plans/  ← CITYSKYLINE `src/bridge/data/*.plan.json` 里有 zoning 的四份
//             （mallPark 5 / outdoorMall 8 / shopsPark 12 / stripMall 6 → 共 31 块宗地）
//
// 四段：
//   一 · 54 份真实地块跑 R1–R10，汇总每条规则的通过率
//   二 · 54 份逐点与真 C# 对照（--attachments），按字段归类不一致项
//   三 · 4 份真实方案的 31 块真实宗地 → 切楼位 → E1–E6
//   四 · HTML 报告
//
// 跑法：node packages/demo/src/batch.ts            全跑
//       node packages/demo/src/batch.ts --no-csharp 跳过 dotnet（只跑静态段，秒级）
//       node packages/demo/src/batch.ts --only=mall 只跑名字含 mall 的
//
// 产物：packages/demo/out/batch.html

import { execFileSync } from 'node:child_process';
import { readFileSync, writeFileSync, readdirSync, mkdirSync, existsSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

import { area, signedArea, type Vec2 } from '../../plan-ir/src/geom.ts';
import { ZONE_PREFAB, type CaseInput, type NearbyNet, type ZoneKind } from '../../plan-ir/src/types.ts';
import { validateCase, type RuleResult } from '../../street-rules/src/rules.ts';
import { findAttachments, netFromSegment } from '../../street-rules/src/finder.ts';
import { compareAttachments, type CSharpAttachments, type CompareRow } from '../../street-rules/src/oracle.ts';
import { cutZoning, type Slot } from '../../slots/src/cut.ts';
import { DEFAULT_ASSETS, matchAll } from '../../slots/src/skin.ts';
import { validateSlots, checkInsideZoning, kindDepthHint } from '../../slots/src/validate.ts';
import { kindColor, svg2D, svgIso, type Scene2D } from './render.ts';

const here = dirname(fileURLToPath(import.meta.url));
const root = resolve(here, '../../..');
const caseDir = join(root, 'tools/batch/cases');
const planDir = join(root, 'tools/batch/plans');
const outDir = join(root, 'packages/demo/out');
const tmpDir = join(root, 'tmp/batch');
const dll = join(root, 'tools/oracle/driver/bin/Release/net6.0/PlacemakerDriver.dll');

const argv = process.argv.slice(2);
const noCsharp = argv.includes('--no-csharp');
const onlyArg = argv.find((a) => a.startsWith('--only='));
const only = onlyArg ? onlyArg.slice('--only='.length) : '';

// ─────────────────────────────────────────────── 夹具类型（烘焙时的原始格式）

interface RawStreet {
  id: number;
  a: [number, number];
  b: [number, number];
  width: number;
  category?: 'road' | 'pathway' | 'track' | 'other';
  sidewalk?: boolean;
  speed?: number;
  oneWay?: boolean;
  highway?: boolean;
}

interface RawCase {
  generator: string;
  seed: number;
  shape: { ring: [number, number][] };
  streets: RawStreet[];
  settings?: Record<string, unknown>;
}

interface RawPlanZoning {
  zoneId: string;
  quad: [number, number][];
}

interface RawPlan {
  generator: string;
  seed: number;
  ready: boolean;
  surfaces: { role: string; ring: [number, number][] }[];
  nets: { role: string }[];
  objects: unknown[];
  zoning: RawPlanZoning[];
}

// ─────────────────────────────────────────────── 小工具

const lines: string[] = [];
const log = (s = ''): void => {
  lines.push(s);
  console.log(s);
};

const pct = (a: number, b: number): string => (b === 0 ? '—' : `${((a / b) * 100).toFixed(1)}%`);

/** zoneId（"EU Commercial Low"）→ ZoneKind。ZONE_PREFAB 是 kind→prefab，这里反查。 */
const KIND_BY_PREFAB = new Map<string, ZoneKind>(
  (Object.entries(ZONE_PREFAB) as [ZoneKind, string][]).map(([k, v]) => [v, k]),
);
function kindOfZoneId(zoneId: string): ZoneKind | null {
  const hit = KIND_BY_PREFAB.get(zoneId);
  if (hit) return hit;
  // 前缀宽松匹配：游戏层会按城市主题换 "EU"/"NA" 前缀，主题一换不该整批失效
  const tail = zoneId.replace(/^(EU|NA|AS)\s+/i, '');
  for (const [prefab, k] of KIND_BY_PREFAB) {
    if (prefab.replace(/^(EU|NA|AS)\s+/i, '') === tail) return k;
  }
  return null;
}

/** 把烘焙输入变成契约输入。ring 已实测全为逆时针（54/54），这里仍留一道兜底。 */
function toCaseInput(raw: RawCase): { input: CaseInput; ring2d: Vec2[]; nets: NearbyNet[]; flipped: boolean } {
  let ring2d: Vec2[] = raw.shape.ring.map((p) => [p[0], p[1]] as Vec2);
  const flipped = signedArea(ring2d) < 0;
  if (flipped) ring2d = [...ring2d].reverse();

  const nets = raw.streets.map((s) =>
    netFromSegment(s.id, s.a, s.b, s.width, {
      category: s.category ?? 'road',
      hasSidewalk: s.sidewalk ?? true,
      isOneWay: s.oneWay ?? false,
      isHighway: s.highway ?? false,
      speedLimit: s.speed ?? 50,
    }),
  );

  const input: CaseInput = {
    shape: {
      ring: ring2d.map((p) => [p[0], 0, p[1]] as [number, number, number]),
      source: 'block',
      // 真实流程里贴合点是 RoadNetwork 直接给的（docs/A §0）；这里留空，
      // 让 R3 拿「精确构造 vs 几何反推」互验，R4 也就自然没有穿越点可数。
      attachments: [],
    },
    site: { nearbyNets: nets, obstacles: [], zoneGrids: [], layoutScale: 0.5, edgeMargin: 2 },
    markers: [],
    settings: {},
    seed: raw.seed,
    regionTags: ['root'],
  };
  return { input, ring2d, nets, flipped };
}

// ─────────────────────────────────────────────── 一 · 静态段

interface CaseRow {
  name: string;
  generator: string;
  nEdges: number;
  nStreets: number;
  nAlongEdges: number;
  nAtt: number;
  nNode: number;
  nearMiss: number;
  rules: RuleResult[];
  pass: number;
  ok: boolean;
  areaM2: number;
  minWidth: number;
  /** 环几何签名：用来发现"同一街区被多个生成器重复烘焙"。 */
  ringSig: string;
  /** 临街边短于 12 m 的条数（R11 的统计口径，advisory）。 */
  shortFrontage: number;
  /** 其中短于等于一个采样步长（4 m）的条数：画法模式下重叠恒为 0，永不临街。 */
  deadFrontage: number;
  /** 只有 C# 段才会填。 */
  cmp?: { rows: CompareRow[]; onlyTs: string[]; onlyCs: string[]; pass: number; ties: number; csCount: number; tsCount: number };
  csharpErr?: string;
}

function runStatic(names: string[]): CaseRow[] {
  const rows: CaseRow[] = [];
  for (const name of names) {
    const raw = JSON.parse(readFileSync(join(caseDir, name), 'utf8')) as RawCase;
    const { input, ring2d, nets } = toCaseInput(raw);
    const rep = validateCase(input);
    const att = findAttachments(ring2d, nets);

    rows.push({
      name: name.replace(/\.json$/, ''),
      generator: raw.generator,
      nEdges: ring2d.length,
      nStreets: nets.length,
      nAlongEdges: rep.edges.filter((e) => e.along.length > 0).length,
      nAtt: att.filter((a) => a.kind === 'roadAlongEdge').length,
      nNode: rep.exact.corners.length,
      nearMiss: rep.edges.filter((e) => e.nearMiss).length,
      rules: rep.rules,
      pass: rep.pass,
      ok: rep.ok,
      areaM2: area(ring2d),
      minWidth: nets.length ? Math.min(...nets.map((n) => n.width)) : 0,
      ringSig: ring2d.map((p) => `${p[0].toFixed(1)},${p[1].toFixed(1)}`).join(';'),
      shortFrontage: rep.edges.filter((e) => (e.along.length > 0 || e.nearMiss) && e.length < 12).length,
      deadFrontage: rep.edges.filter((e) => (e.along.length > 0 || e.nearMiss) && e.length <= 4).length,
    });
  }
  return rows;
}

// ─────────────────────────────────────────────── 二 · 与真 C# 对照

function runCSharp(rows: CaseRow[], names: string[]): void {
  if (!existsSync(dll)) {
    log(`  ! 找不到 ${dll}，先跑 npm run oracle:build`);
    return;
  }
  if (!existsSync(tmpDir)) mkdirSync(tmpDir, { recursive: true });

  for (let i = 0; i < rows.length; i++) {
    const row = rows[i];
    const caseFile = join(caseDir, names[i]);
    const outFile = join(tmpDir, `${row.name}.att.json`);
    try {
      execFileSync('dotnet', [dll, '--attachments', caseFile, outFile], {
        encoding: 'utf8',
        stdio: ['ignore', 'pipe', 'pipe'],
      });
      const cs = JSON.parse(readFileSync(outFile, 'utf8')) as CSharpAttachments;

      const raw = JSON.parse(readFileSync(caseFile, 'utf8')) as RawCase;
      const { ring2d, nets } = toCaseInput(raw);
      const ts = findAttachments(ring2d, nets);
      // 传 nets 让比对能识别 externalT 的"平局等价"（见 oracle.ts 注释）
      const cmp = compareAttachments(ts, cs, 0.01, nets);
      row.cmp = {
        rows: cmp.rows,
        onlyTs: cmp.onlyTs,
        onlyCs: cmp.onlyCs,
        pass: cmp.pass,
        ties: cmp.ties,
        csCount: cs.count,
        tsCount: ts.length,
      };
    } catch (e) {
      row.csharpErr = (e as Error).message.split('\n')[0];
    }
  }
}

// ─────────────────────────────────────────────── 三 · 真实宗地 → 楼位

interface PlanRow {
  name: string;
  generator: string;
  nZonings: number;
  zoningArea: number;
  slotArea: number;
  waste: number;
  kinds: string[];
  checks: { id: string; title: string; ok: boolean; summary: string }[];
  ok: boolean;
  slots: Slot[];
  svg2d: string;
  svgIso: string;
  notes: string[];
  unmapped: string[];
}

function runPlans(): PlanRow[] {
  if (!existsSync(planDir)) return [];
  const files = readdirSync(planDir).filter((f) => f.endsWith('.plan.json')).sort();
  const out: PlanRow[] = [];

  for (const f of files) {
    const name = f.replace(/\.plan\.json$/, '');
    if (only && !name.includes(only)) continue;

    const plan = JSON.parse(readFileSync(join(planDir, f), 'utf8')) as RawPlan;
    if (!plan.zoning || plan.zoning.length === 0) continue;

    // 找同一份方案的输入（有 streets，用来定"哪边临街"）
    const caseFile = join(caseDir, `${name}.json`);
    let ring2d: Vec2[] = [];
    let nets: NearbyNet[] = [];
    if (existsSync(caseFile)) {
      const raw = JSON.parse(readFileSync(caseFile, 'utf8')) as RawCase;
      const built = toCaseInput(raw);
      ring2d = built.ring2d;
      nets = built.nets;
    }
    const atts = nets.length ? findAttachments(ring2d, nets).filter((a) => a.kind === 'roadAlongEdge' && a.direction) : [];

    const slots: Slot[] = [];
    const notes: string[] = [];
    const unmapped: string[] = [];
    let zoningArea = 0;

    plan.zoning.forEach((z, i) => {
      const quad = z.quad.map((p) => [p[0], p[1]] as Vec2) as [Vec2, Vec2, Vec2, Vec2];
      zoningArea += area(quad);
      const kind = kindOfZoneId(z.zoneId);
      if (!kind) {
        unmapped.push(z.zoneId || '(空 zoneId)');
        return;
      }
      // 临街方向取"离这块宗地最近的沿边贴合点"的切向——真实方案里宗地就是沿这条街铺开的
      const c: Vec2 = [
        (quad[0][0] + quad[1][0] + quad[2][0] + quad[3][0]) / 4,
        (quad[0][1] + quad[1][1] + quad[2][1] + quad[3][1]) / 4,
      ];
      let bestDir: Vec2 = [1, 0];
      let bestD = Infinity;
      for (const a of atts) {
        const d = Math.hypot(a.position[0] - c[0], a.position[1] - c[1]);
        if (d < bestD) {
          bestD = d;
          bestDir = a.direction ?? [1, 0];
        }
      }
      const cut = cutZoning({ zoneId: z.zoneId, quad }, kind, bestDir, `z${i}`);
      slots.push(...cut.slots);
      notes.push(...cut.notes);
    });

    const skins = matchAll(slots, DEFAULT_ASSETS, plan.seed);
    // E3 必须拿原始宗地四边形核验，否则它只是个摆设（validate.ts 里保留了这个入口）
    const rep = validateSlots(slots, zoningArea, skins, plan.zoning.map((z) => z.quad));
    const checks = rep.checks;

    const scene: Scene2D = {
      parcel: ring2d,
      streets: nets.map((n) => ({ a: [n.a[0], n.a[1]] as Vec2, b: [n.d[0], n.d[1]] as Vec2, w: n.width })),
      attachments: [],
      zoning: plan.zoning.map((z) => ({ quad: z.quad.map((p) => [p[0], p[1]] as Vec2) })),
      slots: slots.map((s) => ({
        quad: s.quad.map((p) => [p[0], p[1]] as Vec2),
        color: kindColor(s.kind),
        dashed: !s.moduleWidth,
      })),
    };

    const kinds = [...new Set(slots.map((s) => s.kind))];
    out.push({
      name,
      generator: plan.generator,
      nZonings: plan.zoning.length,
      zoningArea,
      slotArea: slots.reduce((s, x) => s + x.width * x.depth, 0),
      waste: rep.checks.find((c) => c.id === 'E4') ? (zoningArea - slots.reduce((s, x) => s + x.width * x.depth, 0)) / Math.max(1, zoningArea) : 0,
      kinds,
      checks,
      ok: rep.ok && unmapped.length === 0,
      slots,
      svg2d: svg2D(scene, 460),
      svgIso: svgIso(
        slots.map((s, i) => ({ quad: s.quad.map((p) => [p[0], p[1]] as Vec2), h: skins[i].height, color: kindColor(s.kind) })),
        460,
        ring2d.length ? { quad: ring2d, h: 0.4, color: '#2b2b34' } : undefined,
      ),
      notes,
      unmapped,
    });
  }
  return out;
}

// ─────────────────────────────────────────────── 报告

function renderHtml(rows: CaseRow[], plans: PlanRow[], csharpRan: boolean): string {
  const ruleIds = ['R1', 'R2', 'R3', 'R4', 'R5', 'R6', 'R7', 'R8', 'R9', 'R10', 'R11'];
  const rulePass = ruleIds.map((id) => ({
    id,
    title: rows[0]?.rules.find((r) => r.id === id)?.title ?? '',
    ok: rows.filter((r) => r.rules.find((x) => x.id === id)?.ok).length,
  }));

  const totalCmpRows = rows.reduce((s, r) => s + (r.cmp?.rows.length ?? 0), 0);
  const totalCmpPass = rows.reduce((s, r) => s + (r.cmp?.pass ?? 0), 0);
  const byField = new Map<string, { pass: number; fail: number }>();
  for (const r of rows) {
    for (const row of r.cmp?.rows ?? []) {
      const cur = byField.get(row.field) ?? { pass: 0, fail: 0 };
      if (row.pass) cur.pass++;
      else cur.fail++;
      byField.set(row.field, cur);
    }
  }
  const onlyTs = rows.reduce((s, r) => s + (r.cmp?.onlyTs.length ?? 0), 0);
  const onlyCs = rows.reduce((s, r) => s + (r.cmp?.onlyCs.length ?? 0), 0);
  const totalTies = rows.reduce((s, r) => s + (r.cmp?.ties ?? 0), 0);

  const bad = rows.filter((r) => !r.ok);
  const slotTotal = plans.reduce((s, p) => s + p.slots.length, 0);
  const slotBad = plans.filter((p) => !p.ok);

  const errs = rows.flatMap((r) =>
    r.rules.flatMap((rule) =>
      rule.violations
        .filter((v) => v.level === 'error')
        .map((v) => ({ name: r.name, rule: rule.id, message: v.message, evidence: v.evidence ?? '' })),
    ),
  );
  const warns = rows.flatMap((r) =>
    r.rules.flatMap((rule) =>
      rule.violations
        .filter((v) => v.level === 'warn')
        .map((v) => ({ name: r.name, rule: rule.id, message: v.message, evidence: v.evidence ?? '' })),
    ),
  );

  const css = `
:root{--bg:#f6f5f1;--fg:#1d1c1a;--mut:#6b6862;--line:#dcd8ce;--card:#fff;
--ok:#2f7d54;--bad:#b3402f;--warn:#a5762a;--acc:#3a5f8a}
*{box-sizing:border-box}
body{margin:0;background:var(--bg);color:var(--fg);font:14px/1.65 "Microsoft YaHei","PingFang SC",system-ui,sans-serif;padding:28px 32px 80px}
h1{font-size:22px;margin:0 0 4px}
h2{font-size:17px;margin:34px 0 10px;padding-bottom:6px;border-bottom:2px solid var(--line)}
.sub{color:var(--mut);font-size:13px;margin-bottom:22px}
.cards{display:flex;gap:12px;flex-wrap:wrap;margin:14px 0 6px}
.card{background:var(--card);border:1px solid var(--line);border-radius:8px;padding:12px 16px;min-width:132px}
.card .v{font-size:24px;font-weight:700;line-height:1.2}
.card .k{color:var(--mut);font-size:12px}
table{border-collapse:collapse;width:100%;background:var(--card);font-size:13px;margin-top:8px}
table.sum{width:auto}
th,td{border:1px solid var(--line);padding:5px 8px;text-align:left;vertical-align:top}
th{background:#eceae3;font-weight:600;white-space:nowrap}
td.n{text-align:right;font-variant-numeric:tabular-nums}
tr.fail td{background:#fdf3f1}
.ok{color:var(--ok);font-weight:600}
.bad{color:var(--bad);font-weight:600}
.warn{color:var(--warn)}
.mut{color:var(--mut)}
.bar{background:#eceae3;border-radius:3px;height:9px;overflow:hidden;min-width:90px}
.bar>i{display:block;height:100%;background:var(--acc)}
.bar>i.full{background:var(--ok)}
.bar>i.low{background:var(--bad)}
.grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(440px,1fr));gap:18px;margin-top:12px}
.panel{background:var(--card);border:1px solid var(--line);border-radius:8px;padding:12px}
.panel h3{margin:0 0 6px;font-size:14px}
.panel .meta{color:var(--mut);font-size:12px;margin-bottom:6px}
.panel svg{display:block;width:100%;height:auto}
.two{display:flex;gap:8px}
.two>div{flex:1;min-width:0}
code{background:#eceae3;padding:0 3px;border-radius:3px;font-size:12px}
`;

  const shortAll = rows.reduce((s2, r) => s2 + r.shortFrontage, 0);
  const deadAll = rows.reduce((s2, r) => s2 + r.deadFrontage, 0);
  const ruleBars = rulePass
    .map((r) => {
      if (r.id === 'R11') {
        return `<tr><td><b>R11</b> ${r.title} <span class="mut">（提醒项，不参与判定）</span>
<br><span class="mut">临街边 &lt;12 m 共 ${shortAll} 条，其中 ≤4 m 的 ${deadAll} 条（画法模式下永不临街）</span></td>
<td class="n">—</td><td style="width:140px"></td><td class="n">—</td></tr>`;
      }
      const ratio = rows.length ? r.ok / rows.length : 0;
      const cls = ratio === 1 ? 'full' : ratio < 0.9 ? 'low' : '';
      return `<tr><td><b>${r.id}</b> ${r.title}</td><td class="n">${r.ok}/${rows.length}</td>
<td style="width:140px"><div class="bar"><i class="${cls}" style="width:${(ratio * 100).toFixed(1)}%"></i></div></td>
<td class="n">${pct(r.ok, rows.length)}</td></tr>`;
    })
    .join('');

  const fieldRows = [...byField.entries()]
    .map(([f, v]) => {
      const t = v.pass + v.fail;
      return `<tr><td><code>${f}</code></td><td class="n">${v.pass}/${t}</td><td class="n ${v.fail ? 'bad' : 'ok'}">${pct(v.pass, t)}</td></tr>`;
    })
    .join('');

  const caseTable = rows
    .map((r) => {
      const cmpCell = !csharpRan || !r.cmp
        ? '<span class="mut">—</span>'
        : r.cmp.rows.length === r.cmp.pass && !r.cmp.onlyTs.length && !r.cmp.onlyCs.length
          ? `<span class="ok">${r.cmp.pass}/${r.cmp.rows.length}</span>`
          : `<span class="bad">${r.cmp.pass}/${r.cmp.rows.length}</span>`;
      return `<tr class="${r.ok ? '' : 'fail'}">
<td>${r.name}</td><td>${r.generator}</td>
<td class="n">${r.areaM2.toFixed(0)}</td><td class="n">${r.nEdges}</td><td class="n">${r.nStreets}</td>
<td class="n">${r.nAlongEdges}/${r.nEdges}</td><td class="n">${r.nAtt}</td><td class="n">${r.nNode}</td>
<td class="n ${r.nearMiss ? 'warn' : 'mut'}">${r.nearMiss}</td>
<td class="n ${r.ok ? 'ok' : 'bad'}">${r.pass}/${r.rules.length}</td>
<td>${cmpCell}</td></tr>`;
    })
    .join('');

  const errRows = errs.length
    ? errs
        .map(
          (e) =>
            `<tr class="fail"><td>${e.name}</td><td><b>${e.rule}</b></td><td>${e.message}</td><td class="mut">${e.evidence}</td></tr>`,
        )
        .join('')
    : '<tr><td colspan="4" class="ok">无阻断项</td></tr>';

  const warnRows = warns.length
    ? warns
        .slice(0, 60)
        .map(
          (e) =>
            `<tr><td>${e.name}</td><td><b>${e.rule}</b></td><td>${e.message}</td><td class="mut">${e.evidence}</td></tr>`,
        )
        .join('') + (warns.length > 60 ? `<tr><td colspan="4" class="mut">… 另有 ${warns.length - 60} 条</td></tr>` : '')
    : '<tr><td colspan="4" class="ok">无警告</td></tr>';

  const planPanels = plans
    .map((p) => {
      const chk = p.checks
        .map((c) => `<div>${c.ok ? '<span class="ok">✓</span>' : '<span class="bad">✗</span>'} <b>${c.id}</b> ${c.title} — ${c.summary}</div>`)
        .join('');
      return `<div class="panel">
<h3>${p.name} <span class="mut">· ${p.generator}</span></h3>
<div class="meta">宗地 ${p.nZonings} 块 / ${p.zoningArea.toFixed(0)} m²　楼位 ${p.slots.length} 个 / ${p.slotArea.toFixed(0)} m²　类型 ${p.kinds.join('、') || '—'}</div>
<div class="two"><div>${p.svg2d}</div><div>${p.svgIso}</div></div>
<div class="meta" style="margin-top:6px">${chk}</div>
${p.unmapped.length ? `<div class="bad">zoneId 无对应类型：${p.unmapped.join('、')}</div>` : ''}
${p.notes.length ? `<div class="mut">注：${[...new Set(p.notes)].join('；')}</div>` : ''}
</div>`;
    })
    .join('');

  return `<!doctype html><html lang="zh-CN"><meta charset="utf-8">
<title>批量回归 · 真实城市 54 份地块</title><style>${css}</style>
<h1>批量回归 · 真实城市地块</h1>
<div class="sub">夹具：CITYSKYLINE <code>city24-s8</code> 真实烘焙输入 54 份 · 真实方案 4 份（31 块宗地）。${csharpRan ? '含与真 C# <code>AttachmentFinder</code> 的逐点对照。' : '本次跳过 C# 对照（--no-csharp）。'}<br>
54 份里不同地块几何只有 <b>${new Set(rows.map((r) => r.ringSig)).size}</b> 个——同一街区被多个生成器重复烘焙，别当成 ${rows.length} 个独立街区看。</div>

<div class="cards">
  <div class="card"><div class="v">${rows.length}</div><div class="k">真实地块</div></div>
  <div class="card"><div class="v ${bad.length ? 'bad' : 'ok'}">${rows.length - bad.length}/${rows.length}</div><div class="k">R1–R10 全通过</div></div>
  <div class="card"><div class="v ${onlyTs + onlyCs + (totalCmpRows - totalCmpPass) ? 'bad' : 'ok'}">${csharpRan ? pct(totalCmpPass, totalCmpRows) : '—'}</div><div class="k">与 C# 字段一致率</div></div>
  <div class="card"><div class="v">${slotTotal}</div><div class="k">真实宗地切出楼位</div></div>
  <div class="card"><div class="v ${slotBad.length ? 'bad' : 'ok'}">${plans.length - slotBad.length}/${plans.length}</div><div class="k">E1–E6 全通过方案</div></div>
</div>

<h2>一 · 十一条规则在真实地块上的通过率</h2>
<table class="sum"><tr><th>规则</th><th>通过</th><th></th><th style="width:76px">比例</th></tr>${ruleBars}</table>

<h2>二 · 与真 C# 的逐点对照</h2>
${csharpRan
      ? `<table class="sum"><tr><th>字段</th><th>一致</th><th style="width:76px">比例</th></tr>${fieldRows}
<tr><td>仅 TS 有的贴合点</td><td class="n ${onlyTs ? 'bad' : 'ok'}">${onlyTs}</td><td></td></tr>
<tr><td>仅 C# 有的贴合点</td><td class="n ${onlyCs ? 'bad' : 'ok'}">${onlyCs}</td><td></td></tr>
<tr><td>其中 <code>externalT</code> 精确平局（等价）</td><td class="n warn">${totalTies}</td><td class="mut">见下注</td></tr></table>
<p class="mut">比对用的输入就是烘焙时喂给 C# 的原始 JSON（<code>tools/batch/cases</code>），两边零转换。
合计 ${rows.length} 个地块、${rows.reduce((s, r) => s + r.nAtt, 0)} 个沿边贴合点。<br>
"精确平局"：沿边贴合点的 <code>externalT</code> 取"离边中点最近的采样点"，中点正好落在两采样点的垂直平分线上时两者<b>完全等距</b>，
C# 由 float32 的 <code>distancesq</code> 噪声决定取哪个，我们定向取前一个（台账 D-018）。位置完全一致，只是记的参数差一格。</p>`
      : '<p class="mut">本次跳过。</p>'}

<h2>三 · 逐地块明细</h2>
<table><tr><th>地块</th><th>生成器</th><th>面积 m²</th><th>边</th><th>街</th><th>临街边</th><th>沿边点</th><th>街角</th><th>近邻未达标</th><th>规则</th><th>C# 一致</th></tr>${caseTable}</table>

<h2>四 · 阻断项</h2>
<table><tr><th>地块</th><th>规则</th><th>说明</th><th>证据</th></tr>${errRows}</table>

<h2>五 · 警告（"看着临街实际不认"之类）</h2>
<table><tr><th>地块</th><th>规则</th><th>说明</th><th>证据</th></tr>${warnRows}</table>

<h2>六 · 真实宗地 → 楼位</h2>
${planPanels || '<p class="mut">没有可用的方案。</p>'}
</div></html>`;
}

// ─────────────────────────────────────────────── main

function main(): void {
  if (!existsSync(outDir)) mkdirSync(outDir, { recursive: true });

  const names = readdirSync(caseDir)
    .filter((f) => f.endsWith('.json'))
    .filter((f) => (only ? f.includes(only) : true))
    .sort();

  log('══ 一 · 真实地块 R1–R11 ══');
  log(`  夹具 ${names.length} 份（tools/batch/cases）`);
  const rows = runStatic(names);

  const ruleIds = ['R1', 'R2', 'R3', 'R4', 'R5', 'R6', 'R7', 'R8', 'R9', 'R10', 'R11'];
  for (const id of ruleIds) {
    const okN = rows.filter((r) => r.rules.find((x) => x.id === id)?.ok).length;
    const title = rows[0]?.rules.find((r) => r.id === id)?.title ?? '';
    if (id === 'R11') {
      const short = rows.reduce((s2, r) => s2 + r.shortFrontage, 0);
      const dead = rows.reduce((s2, r) => s2 + r.deadFrontage, 0);
      log(`  · R11 ${title}（提醒项，不参与判定）— 临街边 <12 m 共 ${short} 条，其中 ≤4 m 的 ${dead} 条`);
      continue;
    }
    log(`  ${okN === rows.length ? '✓' : '✗'} ${id} ${title} — ${okN}/${rows.length}（${pct(okN, rows.length)}）`);
  }
  log(`  全通过 ${rows.length - rows.filter((r) => !r.ok).length}/${rows.length}`);

  const uniqueRings = new Set(rows.map((r) => r.ringSig)).size;
  log(`  去重后不同地块几何 ${uniqueRings} 个 —— 同一街区被多个生成器重复烘焙，不是 ${rows.length} 个独立街区`);

  const attTotal = rows.reduce((s, r) => s + r.nAtt, 0);
  const edgeTotal = rows.reduce((s, r) => s + r.nEdges, 0);
  const edgeAlong = rows.reduce((s, r) => s + r.nAlongEdges, 0);
  log(`  合计 ${edgeTotal} 条边，其中 ${edgeAlong} 条临街（${pct(edgeAlong, edgeTotal)}）；沿边贴合点 ${attTotal} 个；街角 ${rows.reduce((s, r) => s + r.nNode, 0)} 个`);
  log(`  「近邻未达标」边 ${rows.reduce((s, r) => s + r.nearMiss, 0)} 条 —— 这些是看着临街、实际不被认的`);
  const noFrontage = rows.filter((r) => r.nAlongEdges === 0);
  if (noFrontage.length) {
    log(`  无任何临街边的地块 ${noFrontage.length} 份：${noFrontage.map((r) => r.name).join('、')}`);
    log(`    ↑ 这些是真阳性（不是误报）：切碎后的内部碎片，Placemaker 会判 ready=no。真实流程靠"逐块生成再合并"兜住了它`);
  }

  log('');
  log('══ 二 · 与真 C# 逐点对照 ══');
  if (noCsharp) {
    log('  （--no-csharp，跳过）');
  } else {
    runCSharp(rows, names);
    let cmpRows = 0;
    let cmpPass = 0;
    const byField = new Map<string, { pass: number; fail: number }>();
    const offenders: { name: string; text: string }[] = [];
    for (const r of rows) {
      if (r.csharpErr) {
        log(`  ! ${r.name} 调用失败：${r.csharpErr}`);
        continue;
      }
      if (!r.cmp) continue;
      cmpRows += r.cmp.rows.length;
      cmpPass += r.cmp.pass;
      for (const row of r.cmp.rows) {
        const cur = byField.get(row.field) ?? { pass: 0, fail: 0 };
        if (row.pass) cur.pass++;
        else cur.fail++;
        byField.set(row.field, cur);
      }
      const bads = r.cmp.rows.filter((x) => !x.pass);
      if (bads.length || r.cmp.onlyTs.length || r.cmp.onlyCs.length) {
        offenders.push({
          name: r.name,
          text: [
            ...bads.slice(0, 4).map((b) => `${b.key} ${b.field}：TS ${b.ts} vs C# ${b.cs}`),
            ...r.cmp.onlyTs.slice(0, 3).map((k) => `${k} 只在 TS`),
            ...r.cmp.onlyCs.slice(0, 3).map((k) => `${k} 只在 C#`),
          ].join('；'),
        });
      }
    }
    log(`  比对 ${cmpRows} 项，一致 ${cmpPass}（${pct(cmpPass, cmpRows)}）`);
    for (const [f, v] of byField) log(`    ${f}：${v.pass}/${v.pass + v.fail}${v.fail ? `  ✗ ${v.fail}` : ''}`);
    const onlyTs = rows.reduce((s, r) => s + (r.cmp?.onlyTs.length ?? 0), 0);
    const onlyCs = rows.reduce((s, r) => s + (r.cmp?.onlyCs.length ?? 0), 0);
    const ties = rows.reduce((s, r) => s + (r.cmp?.ties ?? 0), 0);
    log(`  数量差：仅 TS ${onlyTs} 个，仅 C# ${onlyCs} 个`);
    if (ties) log(`  其中 ${ties} 处 externalT 属于**精确平局**（同一位置，C# 由 float32 噪声选了另一个采样点），按等价处理`);
    if (offenders.length) {
      log(`  不一致的地块 ${offenders.length} 份：`);
      for (const o of offenders.slice(0, 12)) log(`    ✗ ${o.name}：${o.text}`);
      if (offenders.length > 12) log(`    … 另有 ${offenders.length - 12} 份`);
    } else {
      log('  ✓ 全部一致');
    }
  }

  log('');
  log('══ 三 · 真实宗地 → 楼位 ══');
  const plans = runPlans();
  for (const p of plans) {
    log(`  ${p.name}｜宗地 ${p.nZonings} 块 ${p.zoningArea.toFixed(0)} m² → 楼位 ${p.slots.length} 个 ${p.slotArea.toFixed(0)} m²（${p.kinds.join('、')}）`);
    for (const c of p.checks) log(`    ${c.ok ? '✓' : '✗'} ${c.id} ${c.title} — ${c.summary}`);
    if (p.unmapped.length) log(`    ✗ zoneId 无对应类型：${p.unmapped.join('、')}`);
    if (p.slots.length) log(`    进深提示：${kindDepthHint(p.slots[0].kind)}`);
  }

  log('');
  const html = renderHtml(rows, plans, !noCsharp);
  const outPath = join(outDir, 'batch.html');
  writeFileSync(outPath, html, 'utf8');
  log(`报告 → ${outPath}`);
}

main();
