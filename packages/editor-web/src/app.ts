// 规则验证器 —— 单页应用主逻辑。
//
// 目的很明确：把**看不见的验收规则**变成可拖、可看、当场变色的东西。
// 之前 R1–R11 只能读控制台文本、ready 只能读反向验证报告、楼位只能看静态 SVG；
// 现在拖一条街出去，右边的 R7 和 ready 预测会立刻变。
//
// 三种验证同时在场，这是它存在的理由：
//   1. 规则层  —— validateCase 的 R1–R11，逐条亮灯
//   2. 判定层  —— ready.ts 的家族模型，与 C# 基线（build.mjs 固化的）对照
//   3. 几何层  —— 楼位切分 E1–E6，直接画在同一个画布上
//
// 不做什么：不调 dotnet、不写文件、不持久化。它是**验证器**不是编辑器，
// 编辑能力只保留到"拖动几何看规则反应"为止。

import { signedArea, type Vec2 } from '../../plan-ir/src/geom.ts';
import { ZONE_PREFAB, type CaseInput, type NearbyNet } from '../../plan-ir/src/types.ts';
import { findAttachments, netFromSegment } from '../../street-rules/src/finder.ts';
import { validateCase, type RuleResult } from '../../street-rules/src/rules.ts';
import { entranceReachable, type EntranceReach } from '../../street-rules/src/entrance.ts';
import { entranceClass, predictReady, type EntranceClass } from '../../street-rules/src/ready.ts';
import { cutZoning, type Slot } from '../../slots/src/cut.ts';
import { DEFAULT_ASSETS, matchAll } from '../../slots/src/skin.ts';
import { validateSlots } from '../../slots/src/validate.ts';
import { drawCanvas, drawIso, type Handle, type Street, type ZoningQuad } from './canvas.ts';
import { STREET_LABEL } from './view.ts';

// ─────────────────────────────────────────────── 数据

interface CSharpInfo {
  ready: boolean;
  reason: string;
  given: number;
  auto: number;
}

interface RawCase {
  id: string;
  generator: string;
  seed: number;
  ring: [number, number][];
  streets: { id: number; a: [number, number]; b: [number, number]; width: number; category?: string }[];
  markers: { kind: string; at: [number, number] }[];
  settings: Record<string, unknown>;
  csharp: CSharpInfo | null;
  zoning: { zoneId: string; quad: [number, number][] }[] | null;
}

interface Bundle {
  generatedAt: string;
  cases: RawCase[];
}

// ─────────────────────────────────────────────── 编辑状态

interface Doc {
  id: string;
  generator: string;
  seed: number;
  ring: Vec2[];
  streets: Street[];
  zoning: ZoningQuad[] | null;
  csharp: CSharpInfo | null;
  /** 用户手放的出入口个数（0/1）。这是演示 ready 翻转最直接的那根杠杆。 */
  given: number;
  showGrid: boolean;
  showPoints: boolean;
  showSlots: boolean;
  dirty: boolean;
}

function docFrom(raw: RawCase): Doc {
  let ring: Vec2[] = raw.ring.map((p) => [p[0], p[1]] as Vec2);
  if (signedArea(ring) < 0) ring = [...ring].reverse();
  return {
    id: raw.id,
    generator: raw.generator,
    seed: raw.seed,
    ring,
    streets: raw.streets.map((s) => ({
      id: s.id,
      a: [s.a[0], s.a[1]] as Vec2,
      b: [s.b[0], s.b[1]] as Vec2,
      width: s.width,
      category: (s.category as Street['category']) ?? 'road',
    })),
    zoning: raw.zoning ? raw.zoning.map((z) => ({ zoneId: z.zoneId, quad: z.quad.map((p) => [p[0], p[1]] as Vec2) })) : null,
    csharp: raw.csharp,
    given: raw.csharp?.given ?? 0,
    showGrid: false,
    showPoints: true,
    showSlots: true,
    dirty: false,
  };
}

// ─────────────────────────────────────────────── 结算

const KIND_BY_PREFAB = new Map<string, string>((Object.entries(ZONE_PREFAB) as [string, string][]).map(([k, v]) => [v, k]));

function kindOfZoneId(zoneId: string): string | null {
  const hit = KIND_BY_PREFAB.get(zoneId);
  if (hit) return hit;
  const tail = zoneId.replace(/^(EU|NA|AS)\s+/i, '');
  for (const [prefab, k] of KIND_BY_PREFAB) {
    if (prefab.replace(/^(EU|NA|AS)\s+/i, '') === tail) return k;
  }
  return null;
}

interface SlotReport {
  zid: string;
  kind: string;
  n: number;
  area: number;
  waste: number;
  checks: { id: string; title: string; ok: boolean; summary: string }[];
}

interface Calc {
  rules: RuleResult[];
  ok: boolean;
  nets: NearbyNet[];
  atts: { position: Vec2; direction: Vec2 | null; kind: string }[];
  badEdges: number[];
  family: EntranceClass;
  ent: EntranceReach | null;
  pred: { ready: boolean | null; by: string };
  slots: Slot[];
  heights: number[];
  slotReports: SlotReport[];
  slotArea: number;
  zoningArea: number;
  streetCount: number;
  areaM2: number;
}

function compute(doc: Doc): Calc {
  const nets = doc.streets.map((s) =>
    netFromSegment(s.id, s.a as never, s.b as never, s.width, { category: s.category as never }),
  );
  const input: CaseInput = {
    shape: { ring: doc.ring.map((p) => [p[0], 0, p[1]] as [number, number, number]), source: 'block', attachments: [] },
    site: { nearbyNets: nets, obstacles: [], zoneGrids: [], layoutScale: 0.5, edgeMargin: 2 },
    markers: (doc.given > 0 ? [{ kind: 'entrance', position: [doc.ring[0][0], 0, doc.ring[0][1]] }] : []) as never,
    settings: {},
    seed: doc.seed,
    regionTags: ['root'],
  };

  const rep = validateCase(input);
  const family = entranceClass(doc.generator);
  const ent = family === 'parking' ? entranceReachable(doc.ring, nets) : null;
  const pred = predictReady({ generator: doc.generator, givenMarkers: doc.given, suggestOk: ent?.reachable ?? false });

  const atts = findAttachments(doc.ring as never, nets as never) as unknown as {
    position: Vec2;
    direction: Vec2 | null;
    kind: string;
  }[];

  // 判"该临街却没有贴合点"的边：边上确实有街压着（nearMiss/along）却没出点
  const badEdges: number[] = [];
  for (let i = 0; i < rep.edges.length; i++) {
    const e = rep.edges[i];
    if ((e.along.length > 0 || e.nearMiss) && e.along.length === 0) badEdges.push(i);
  }

  // ── 楼位切分（只有自带 zoning 的 4 份真实方案能算）
  const slots: Slot[] = [];
  const heights: number[] = [];
  const slotReports: SlotReport[] = [];
  let slotArea = 0;
  let zoningArea = 0;

  if (doc.zoning && doc.zoning.length) {
    for (let i = 0; i < doc.zoning.length; i++) {
      const z = doc.zoning[i];
      const kind = kindOfZoneId(z.zoneId);
      const quadArea = Math.abs(signedArea(z.quad));
      zoningArea += quadArea;
      if (!kind) {
        slotReports.push({ zid: `z${i}`, kind: '—', n: 0, area: 0, waste: quadArea, checks: [{ id: 'ZONE', title: 'zone 映射', ok: false, summary: `zoneId「${z.zoneId}」映不到 ZoneKind` }] });
        continue;
      }
      const c: Vec2 = [
        (z.quad[0][0] + z.quad[1][0] + z.quad[2][0] + z.quad[3][0]) / 4,
        (z.quad[0][1] + z.quad[1][1] + z.quad[2][1] + z.quad[3][1]) / 4,
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
      const cut = cutZoning({ zoneId: z.zoneId, quad: z.quad as never }, kind as never, bestDir, `z${i}`);
      slots.push(...cut.slots);

      const skins = matchAll(cut.slots, DEFAULT_ASSETS, doc.seed);
      const srep = validateSlots(cut.slots, quadArea, skins, [z.quad as [Vec2, Vec2, Vec2, Vec2]]);
      const a = cut.slots.reduce((s, x) => s + x.width * x.depth, 0);
      slotArea += a;
      slotReports.push({
        zid: `z${i}`,
        kind,
        n: cut.slots.length,
        area: a,
        waste: quadArea - a,
        checks: srep.checks.map((c2) => ({ id: c2.id, title: c2.title, ok: c2.ok, summary: c2.summary })),
      });
    }
    // 高度：给等轴图用；没有资产时按 8 m 估
    const allSkins = matchAll(slots, DEFAULT_ASSETS, doc.seed);
    for (const s of allSkins) heights.push(s.height);
  }

  return {
    rules: rep.rules,
    ok: rep.ok,
    nets,
    atts,
    badEdges,
    family,
    ent,
    pred,
    slots,
    heights,
    slotReports,
    slotArea,
    zoningArea,
    streetCount: doc.streets.length,
    areaM2: Math.abs(signedArea(doc.ring)),
  };
}

// ─────────────────────────────────────────────── 面板

const FAMILY_CN: Record<string, string> = {
  shop: '商场族',
  market: '市集族',
  parking: '停车族',
  typed: '公园族',
  none: '无要求',
  roads: '路网族',
  unknown: '未核实',
};

const FAMILY_NOTE: Record<string, string> = {
  shop: '建议器恒空 → ready 只认给定出入口，与街道无关',
  market: '建议器恒空 → ready 只认给定出入口，与街道无关',
  parking: '建议器走 40 m 外射线 → 街道能决定 ready',
  typed: '建议器有最长边兜底 → 恒 ready',
  none: 'CheckReady 恒 true',
  roads: 'ready 看图案×地形 → 与街道无关，本工具不预测',
  unknown: '反射表里没有这个 id，不预测',
};

function ruleRow(r: RuleResult): string {
  const cls = r.ok ? 'ok' : r.id === 'R10' || r.id === 'R11' ? 'warn' : 'bad';
  const mark = r.ok ? '✓' : r.id === 'R10' || r.id === 'R11' ? '!' : '✗';
  return `<li class="rule ${cls}" title="${escapeAttr(r.summary)}">
    <span class="rid">${r.id}</span>
    <span class="rmark">${mark}</span>
    <span class="rt">${r.title}</span>
    <span class="rs">${r.summary}</span>
  </li>`;
}

function escapeAttr(s: string): string {
  return s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/"/g, '&quot;');
}

function drawPanel(doc: Doc, c: Calc): string {
  const cs = doc.csharp;
  const predReady = c.pred.ready;
  // 基线是对**原始输入**的判定。一旦用户动过几何，拿它比"一致"就没意义了
  // —— 不一致不等于我们判错，只说明输入变了。所以这里显式分三种情况。
  const changed = doc.dirty;
  const agree = !changed && cs && predReady !== null ? predReady === cs.ready : null;
  const rulesOk = c.rules.filter((r) => r.ok).length;

  const agreeBadge = changed
    ? '<span class="badge mut">已改动 · 基线不适用</span>'
    : agree === null
      ? '<span class="badge mut">无 C# 基线</span>'
      : agree
        ? '<span class="badge ok">与 C# 一致</span>'
        : '<span class="badge bad">与 C# 不一致</span>';

  const slotBlock = doc.zoning
    ? `<h3>楼位切分 <span class="mut">${c.slots.length} 个位 / 宗地 ${doc.zoning.length} 块</span></h3>
       <div class="kv"><span>宗地面积</span><b>${c.zoningArea.toFixed(0)} m²</b></div>
       <div class="kv"><span>楼位面积</span><b>${c.slotArea.toFixed(0)} m²</b></div>
       <div class="kv"><span>边角料</span><b class="${c.zoningArea > 0 && (c.zoningArea - c.slotArea) / c.zoningArea > 0.3 ? 'bad' : ''}">${
         c.zoningArea > 0 ? (((c.zoningArea - c.slotArea) / c.zoningArea) * 100).toFixed(1) : '0'
       }%</b></div>
       ${c.slotReports
         .map(
           (s) =>
             `<div class="zrow"><code>${s.zid}</code> <span class="mut">${s.kind}</span> → <b>${s.n}</b> 位${s.checks
               .filter((x) => !x.ok)
               .map((x) => ` <span class="bad">${x.id}✗</span>`)
               .join('')}</div>`,
         )
         .join('')}
       <label class="chk"><input type="checkbox" id="cbSlots" ${doc.showSlots ? 'checked' : ''}/> 画布显示楼位</label>`
    : `<h3>楼位切分</h3><p class="mut small">这份地块没有自带宗地（zoning）。楼位切分需要真实方案的 zoning，
       目前只有 mallPark / outdoorMall / shopsPark / stripMall 四份带。换一个看看。</p>`;

  return `
<div class="pblock">
  <div class="ptitle">
    <b>${doc.id}</b>
    <span class="badge fam">${FAMILY_CN[c.family] ?? c.family}</span>
    ${doc.dirty ? '<span class="badge warn">已改动</span>' : ''}
  </div>
  <div class="mut small">生成器 <code>${doc.generator}</code>｜街道 ${c.streetCount} 条｜面积 ${c.areaM2.toFixed(0)} m²</div>
  <div class="mut small">${FAMILY_NOTE[c.family] ?? ''}</div>
</div>

<div class="pblock">
  <h3>ready 判定 ${agreeBadge}</h3>
  <div class="ready">
    <div class="rcol">
      <div class="rlabel">我们预测</div>
      <div class="rval ${predReady === null ? 'mut' : predReady ? 'ok' : 'bad'}">${
        predReady === null ? '—' : predReady ? 'ready' : 'not ready'
      }</div>
    </div>
    <div class="rcol">
      <div class="rlabel">C# 引擎</div>
      <div class="rval ${cs ? (cs.ready ? 'ok' : 'bad') : 'mut'}">${cs ? (cs.ready ? 'ready' : 'not ready') : '—'}</div>
    </div>
  </div>
  <div class="mut small">${escapeAttr(c.pred.by)}</div>
  ${cs ? `<div class="mut small">原始输入下的引擎侧：给定 ${cs.given} 个口，建议器补 ${cs.auto} 个${cs.reason ? `｜${cs.reason}` : ''}</div>` : ''}
  <label class="chk"><input type="checkbox" id="cbGiven" ${doc.given > 0 ? 'checked' : ''}/> 放一个给定出入口</label>
  <p class="mut small">勾上它，看 ready 怎么翻 —— 商场/市集族只认这个，街道怎么拖都没用。</p>
</div>

<div class="pblock">
  <h3>街道规则 <span class="mut">${rulesOk}/${c.rules.length}</span></h3>
  <ul class="rules">${c.rules.map(ruleRow).join('')}</ul>
</div>

<div class="pblock">
  ${slotBlock}
</div>

<label class="chk"><input type="checkbox" id="cbGrid" ${doc.showGrid ? 'checked' : ''}/> 显示 8 m 模数网格</label>
<label class="chk"><input type="checkbox" id="cbPoints" ${doc.showPoints ? 'checked' : ''}/> 显示贴合点</label>
<button id="btnReset" ${doc.dirty ? '' : 'disabled'}>还原这份地块</button>
`;
}

// ─────────────────────────────────────────────── 应用

class App {
  private bundle!: Bundle;
  private doc!: Doc;
  private calc!: Calc;
  private handles: Handle[] = [];
  private view!: import('./view.ts').View;
  private W = 720;
  private H = 480;
  private drag: { handle: Handle; moved: boolean } | null = null;
  /** 拖动期间的冻结视图（见 canvas.drawCanvas 的注释）。 */
  private dragView: import('./view.ts').View | null = null;
  private filter = '';

  private el = {
    list: () => document.getElementById('list')!,
    canvas: () => document.getElementById('canvas')!,
    panel: () => document.getElementById('panel')!,
    iso: () => document.getElementById('iso')!,
    meta: () => document.getElementById('meta')!,
  };

  async boot(): Promise<void> {
    const res = await fetch('./data/bundle.json');
    this.bundle = (await res.json()) as Bundle;
    const first = this.bundle.cases.find((c) => c.zoning) ?? this.bundle.cases[0];
    this.doc = docFrom(first);
    this.renderList();
    this.renderAll();
    this.bindGlobal();
    this.el.meta().textContent = `${this.bundle.cases.length} 份真实地块 · 数据打包于 ${this.bundle.generatedAt}`;
  }

  private renderList(): void {
    const q = this.filter.toLowerCase();
    const byGen = new Map<string, RawCase[]>();
    for (const c of this.bundle.cases) {
      if (q && !c.id.toLowerCase().includes(q) && !c.generator.toLowerCase().includes(q)) continue;
      const a = byGen.get(c.generator) ?? [];
      a.push(c);
      byGen.set(c.generator, a);
    }
    const groups = [...byGen.entries()].sort((a, b) => b[1].length - a[1].length || a[0].localeCompare(b[0]));
    this.el.list().innerHTML = groups
      .map(
        ([gen, arr]) => `<div class="lgroup">
  <div class="lgh">${gen} <span class="mut">${arr.length}</span></div>
  ${arr
    .map(
      (c) =>
        `<button class="litem ${c.id === this.doc?.id ? 'on' : ''}" data-id="${c.id}">
      <span class="lid">${c.id.replace(/^city\d+-s\d+-/, '')}</span>
      ${c.zoning ? '<span class="tag">宗地</span>' : ''}
      ${c.csharp ? (c.csharp.ready ? '<span class="tag ok">ready</span>' : '<span class="tag bad">no</span>') : ''}
    </button>`,
    )
    .join('')}
</div>`,
      )
      .join('');

    for (const b of this.el.list().querySelectorAll<HTMLButtonElement>('.litem')) {
      b.onclick = () => {
        const raw = this.bundle.cases.find((c) => c.id === b.dataset.id);
        if (!raw) return;
        this.doc = docFrom(raw);
        this.renderList();
        this.renderAll();
      };
    }
  }

  private renderAll(): void {
    this.calc = compute(this.doc);
    const cr = drawCanvas(
      {
        ring: this.doc.ring,
        streets: this.doc.streets,
        zoning: this.doc.zoning,
        slots: this.calc.slots,
        attachments: this.calc.atts,
        badEdges: this.calc.badEdges,
        showGrid: this.doc.showGrid,
        showPoints: this.doc.showPoints,
        showSlots: this.doc.showSlots,
      },
      this.W,
      this.dragView ?? undefined,
    );
    this.handles = cr.handles;
    this.view = cr.view;
    this.W = cr.W;
    this.H = cr.H;
    this.el.canvas().innerHTML = cr.svg;

    this.el.panel().innerHTML = drawPanel(this.doc, this.calc);
    this.bindPanel();

    this.el.iso().innerHTML = this.doc.zoning
      ? drawIso(this.calc.slots, this.calc.heights, this.doc.ring, 520)
      : `<div class="empty">这份地块没有宗地<br/><span class="mut small">楼位切分需要真实方案的 zoning</span></div>`;

    const svg = this.el.canvas().querySelector('svg')!;
    svg.addEventListener('pointerdown', (e) => this.onDown(e as PointerEvent));
    svg.addEventListener('pointermove', (e) => this.onMove(e as PointerEvent));
    svg.addEventListener('pointerup', () => this.onUp());
    svg.addEventListener('pointerleave', () => this.onUp());
  }

  private bindPanel(): void {
    const g = document.getElementById('cbGiven') as HTMLInputElement | null;
    if (g)
      g.onchange = () => {
        this.doc.given = g.checked ? 1 : 0;
        this.doc.dirty = true;
        this.renderAll();
      };
    const grid = document.getElementById('cbGrid') as HTMLInputElement | null;
    if (grid)
      grid.onchange = () => {
        this.doc.showGrid = grid.checked;
        this.renderAll();
      };
    const pts = document.getElementById('cbPoints') as HTMLInputElement | null;
    if (pts)
      pts.onchange = () => {
        this.doc.showPoints = pts.checked;
        this.renderAll();
      };
    const slots = document.getElementById('cbSlots') as HTMLInputElement | null;
    if (slots)
      slots.onchange = () => {
        this.doc.showSlots = slots.checked;
        this.renderAll();
      };
    const reset = document.getElementById('btnReset') as HTMLButtonElement | null;
    if (reset)
      reset.onclick = () => {
        const raw = this.bundle.cases.find((c) => c.id === this.doc.id);
        if (!raw) return;
        const keep = { showGrid: this.doc.showGrid, showPoints: this.doc.showPoints, showSlots: this.doc.showSlots };
        this.doc = docFrom(raw);
        Object.assign(this.doc, keep);
        this.renderAll();
      };
  }

  private toLocal(e: PointerEvent): [number, number] {
    const svg = this.el.canvas().querySelector('svg')!;
    const r = svg.getBoundingClientRect();
    return [((e.clientX - r.left) / r.width) * this.W, ((e.clientY - r.top) / r.height) * this.H];
  }

  private onDown(e: PointerEvent): void {
    const [px, py] = this.toLocal(e);
    let best: Handle | null = null;
    let bestD = 14; // 命中半径（屏幕像素）
    for (const h of this.handles) {
      const sx = this.view.sx(h.p[0]);
      const sy = this.view.sy(h.p[1]);
      const d = Math.hypot(sx - px, sy - py);
      if (d < bestD) {
        bestD = d;
        best = h;
      }
    }
    if (!best) return;
    this.drag = { handle: best, moved: false };
    this.dragView = this.view;
    (e.target as Element).setPointerCapture?.(e.pointerId);
  }

  private onMove(e: PointerEvent): void {
    if (!this.drag) return;
    const [px, py] = this.toLocal(e);
    const w = this.view.toWorld(px, py);
    const h = this.drag.handle;

    if (h.kind === 'street-end' && h.streetIdx !== undefined && h.end !== undefined) {
      const s = this.doc.streets[h.streetIdx];
      if (h.end === 0) s.a = w;
      else s.b = w;
      h.p = w;
    } else if (h.kind === 'street-mid' && h.streetIdx !== undefined) {
      const s = this.doc.streets[h.streetIdx];
      const old: Vec2 = [(s.a[0] + s.b[0]) / 2, (s.a[1] + s.b[1]) / 2];
      const dx = w[0] - old[0];
      const dy = w[1] - old[1];
      s.a = [s.a[0] + dx, s.a[1] + dy];
      s.b = [s.b[0] + dx, s.b[1] + dy];
      h.p = [old[0] + dx, old[1] + dy];
    } else if (h.kind === 'ring-vtx' && h.vtx !== undefined) {
      this.doc.ring[h.vtx] = w;
      h.p = w;
    }

    this.drag.moved = true;
    this.doc.dirty = true;
    this.renderAll();
  }

  private onUp(): void {
    if (!this.drag) return;
    this.drag = null;
    // 松手后解除冻结并重绘一次，让视图回到自适应的缩放
    this.dragView = null;
    this.renderAll();
  }

  private bindGlobal(): void {
    const inp = document.getElementById('search') as HTMLInputElement | null;
    if (inp)
      inp.oninput = () => {
        this.filter = inp.value.trim();
        this.renderList();
      };
    if (innerWidth > 1100) this.W = 760;
  }
}

new App().boot();
