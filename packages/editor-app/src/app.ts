// 编辑器外壳。**布局与面板内容照 PCG**（`App.tsx` / `Toolbar.tsx` / `Inspector.tsx` /
// `Layers.tsx` / `StatusBar.tsx`），只是不用 React —— 本机没有 npm，用模板字符串 + 事件委托。
//
// 一条纪律：**面板是"读写同一个 model"的视图，不持有自己的状态**。
// 所有数字都从 model 现算（面积/周长/尺寸/中心），所以拖完对象面板立刻是对的；
// 一旦把数字缓存进 DOM，就会出现"拖动后面积还是旧的"这类最难查的 bug。

import { Camera2D } from './camera.ts';
import { EditorController } from './controller.ts';
import { generateParcel } from './gen.ts';
import {
  ACCESS_LABEL,
  KIND_LABEL,
  LAYER_OF,
  ROAD_LABEL,
  ROAD_WIDTH,
  SURFACE_LABEL,
  ZONE_OPTIONS,
  areaPaths,
  isAreaObj,
  objectVertices,
  type BuildingObj,
  type LayerKey,
  type ParcelObj,
  type RoadClass,
  type RoadObj,
  type SceneObject,
  type ZoneKind,
} from './model.ts';
import { boundsOfPaths, boundsValid, pathsArea, pathsPerimeter, ringCenter } from './polygon.ts';
import { DRAW_KIND_LABEL, EMPTY_HINT, Store, TOOL_LABEL, type DrawKind, type ToolId } from './store.ts';
import { fmtArea } from './render2d.ts';
import { DEG } from './vec.ts';

const ICONS: Record<string, string> = {
  select: 'M3 2.5 L10.5 8 L7 8.8 L8.8 12.6 L7.4 13.3 L5.6 9.4 L3 11.6 Z',
  site: 'M2.5 3.5 H13.5 V12.5 H2.5 Z M2.5 6.5 H13.5 M6 3.5 V12.5',
  parcel: 'M3 3 H13 V13 H3 Z',
  building: 'M3.5 13 V4.5 H9 V13 M9 7.5 H12.5 V13 M2 13 H14',
  road: 'M6 2 V14 M10 2 V14 M8 3.5 V5 M8 7 V8.5 M8 10.5 V12',
  gate: 'M2.5 12.5 H6 M10 12.5 H13.5 M6 12.5 V8 H10 V12.5',
  nobuild: 'M3 3 H13 V13 H3 Z M5 5 L11 11 M11 5 L5 11',
  rect: 'M3 4 H13 V12 H3 Z',
  polygon: 'M8 2.5 L13.5 6.5 L11.5 13 H4.5 L2.5 6.5 Z',
  polyline: 'M2.5 12 L6 5 L9 9.5 L13.5 3.5',
  circle: 'M8 2.5 A5.5 5.5 0 1 0 8.01 2.5',
  measure: 'M2.5 6.5 L6.5 2.5 L13.5 9.5 L9.5 13.5 Z M5 8.5 L6.5 7 M7 10.5 L8.5 9 M9 6.5 L10.5 5',
};

const icon = (d: string, fill = false): string =>
  `<svg viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.4" strokeLinecap="round" strokeLinejoin="round"><path d="${d}" fill="${fill ? 'currentColor' : 'none'}" fill-opacity="${fill ? 0.18 : 0}"/></svg>`;

const SEMANTIC: { kind: DrawKind; tool: ToolId; label: string; icon: string }[] = [
  { kind: 'site', tool: 'polygon', label: '园区', icon: 'site' },
  { kind: 'parcel', tool: 'polygon', label: '地块', icon: 'parcel' },
  { kind: 'building', tool: 'rect', label: '建筑', icon: 'building' },
  { kind: 'road', tool: 'polyline', label: '道路', icon: 'road' },
  { kind: 'access', tool: 'polygon', label: '出入口', icon: 'gate' },
  { kind: 'nobuild', tool: 'polygon', label: '禁建', icon: 'nobuild' },
];

const GEOM: { tool: ToolId; label: string; icon: string; key: string }[] = [
  { tool: 'rect', label: '矩形', icon: 'rect', key: 'R' },
  { tool: 'polygon', label: '多边形', icon: 'polygon', key: 'P' },
  { tool: 'polyline', label: '折线', icon: 'polyline', key: 'L' },
  { tool: 'circle', label: '圆', icon: 'circle', key: 'C' },
];

const LAYERS: { key: LayerKey; label: string; hint: string }[] = [
  { key: 'site', label: '园区边界', hint: 'Site' },
  { key: 'parcels', label: '地块', hint: 'Parcels' },
  { key: 'roads', label: '道路', hint: 'Roads' },
  { key: 'buildings', label: '建筑', hint: 'Buildings' },
  { key: 'access', label: '出入口', hint: 'Access' },
  { key: 'ground', label: '地面', hint: 'Ground' },
  { key: 'landscape', label: '禁建 / 景观', hint: 'Landscape' },
];

export class EditorApp {
  private readonly store = new Store();
  private readonly cam = new Camera2D();
  private readonly ctrl: EditorController;
  private readonly canvas: HTMLCanvasElement;
  private readonly ctx: CanvasRenderingContext2D;
  private lastPanelKey = '';
  private lastVersion = -1;
  private viewW = 1;
  private viewH = 1;

  constructor(private readonly host: HTMLElement) {
    host.innerHTML = this.template();
    this.canvas = host.querySelector('#stage-canvas') as HTMLCanvasElement;
    const ctx = this.canvas.getContext('2d');
    if (!ctx) throw new Error('当前浏览器不支持 Canvas 2D');
    this.ctx = ctx;
    this.ctrl = new EditorController(this.canvas, this.store, this.cam, (reseed) => this.generate(reseed));

    this.store.subscribe(() => this.onStoreChange());
    this.bindTop();
    this.bindTools();
    this.bindCanvas();
    this.bindKeys();

    this.resize();
    new ResizeObserver(() => this.resize()).observe(host.querySelector('#stage') as HTMLElement);
    this.ctrl.fitAll();
    this.renderAll();
    this.loop();

    // 无头验证用：从外面读状态、模拟点击，都走这两个钩子
    (window as any).__EDITOR__ = this;
    (window as any).__STORE__ = this.store;
    (window as any).__CAM__ = this.cam;
    (window as any).__CTRL__ = this.ctrl;
  }

  // ───────────────────────────── DOM 模板

  private template(): string {
    const toolBtn = (id: string, label: string, d: string, title: string, fill = false) =>
      `<button class="tool" data-tool="${id}" title="${title}">${icon(d, fill)}${label}</button>`;
    return `
<div class="app">
  <div class="topbar">
    <div class="brand"><div class="brand-mark"></div>Context City PCG<small>2D 空间授权</small></div>
    <input class="project-name" id="proj-name" spellcheck="false"/>
    <div class="spacer"></div>
    <div class="seg">
      <button id="view-2d" class="on">2D Plan</button>
      <button id="view-3d" title="3D 预览（G3）">3D Preview</button>
    </div>
    <button class="btn ghost" id="btn-demo" title="载入示例园区">示例</button>
    <button class="btn ghost" id="btn-new" title="清空项目">新建</button>
    <button class="btn" id="btn-export" title="导出 Project JSON">导出 JSON</button>
    <button class="btn primary" id="btn-gen" title="对选中（或全部）地块做生成 · Shift 点击 = 换一版">生成</button>
  </div>
  <div class="main">
    <div class="toolbar">
      ${toolBtn('select', '选择', ICONS.select, '选择 / 移动 (V)', true)}
      <div class="tool-sep"></div>
      <div class="tool-label">语义</div>
      ${SEMANTIC.map(
        (s) => `<button class="tool sem" data-kind="${s.kind}" data-tool="${s.tool}" title="绘制${s.label}">${icon(ICONS[s.icon])}${s.label}</button>`,
      ).join('')}
      <div class="tool-sep"></div>
      <div class="tool-label">几何</div>
      ${GEOM.map((g) => `<button class="tool geom" data-tool="${g.tool}" title="${g.label} (${g.key})">${icon(ICONS[g.icon])}${g.label}</button>`).join('')}
      ${toolBtn('measure', '测量', ICONS.measure, '测量距离 / 面积 (M)')}
      <div class="tool-sep"></div>
      <div class="tool-label">布尔</div>
      <div class="mini-grid c3">
        <button class="mini" data-bool="union" title="合并 Union（至少 2 个面）">∪</button>
        <button class="mini" data-bool="subtract" title="减去 Subtract（第一个选中为主对象）">−</button>
        <button class="mini" data-bool="intersect" title="取交集 Intersect">∩</button>
      </div>
      <div class="tool-label">偏移</div>
      <div class="mini-grid c2">
        <button class="mini" data-offset="-3" title="向内偏移 3 m">内缩</button>
        <button class="mini" data-offset="3" title="向外偏移 3 m">外扩</button>
      </div>
      <div class="tool-sep"></div>
      <div class="mini-grid c2">
        <button class="mini" id="btn-undo" title="撤销 (Ctrl+Z)">↶</button>
        <button class="mini" id="btn-redo" title="重做 (Ctrl+Shift+Z)">↷</button>
      </div>
    </div>
    <div class="stage-wrap">
      <div class="stage" id="stage">
        <canvas id="stage-canvas" tabindex="0"></canvas>
        <div class="canvas-badge" id="badge"></div>
        <div class="toast" id="toast"></div>
        <div class="stage-3d-note" id="stage-3d-note" hidden>
          <h3>3D 预览（G3）</h3>
          <p>3D 页签下一步接现有 <code>stage.ts</code> + <code>realizer.ts</code>：ProjectModel → SceneIR → 增量渲染，
          正交五视图、聚焦选中、<code>1–5</code> 切视图。</p>
          <p>当前版本只交付 <b>2D 编辑</b>，因为"能画"是产品的地基；3D 是看结果，见 <code>docs/G</code> §5。</p>
          <button class="btn" id="btn-back-2d">回到 2D</button>
        </div>
      </div>
    </div>
    <div class="side">
      <div id="inspector"></div>
      <div id="layers"></div>
    </div>
  </div>
  <div class="statusbar" id="statusbar"></div>
</div>`;
  }

  // ───────────────────────────── 绑定

  private bindTop(): void {
    const name = this.host.querySelector('#proj-name') as HTMLInputElement;
    name.value = this.store.state.model.name;
    name.addEventListener('change', () => {
      this.store.pushHistory('重命名项目');
      this.store.setModel({ ...this.store.state.model, name: name.value });
    });

    (this.host.querySelector('#btn-demo') as HTMLElement).addEventListener('click', () => {
      this.store.loadDemo();
      this.ctrl.fitAll();
    });
    (this.host.querySelector('#btn-new') as HTMLElement).addEventListener('click', () => {
      this.store.newProject();
      this.ctrl.fitAll();
    });
    (this.host.querySelector('#btn-export') as HTMLElement).addEventListener('click', () => {
      const blob = new Blob([this.store.exportJson()], { type: 'application/json' });
      const url = URL.createObjectURL(blob);
      const a = document.createElement('a');
      a.href = url;
      a.download = `${this.store.state.model.name || 'project'}.json`;
      a.click();
      URL.revokeObjectURL(url);
      this.store.showToast('Project JSON 已导出');
    });
    (this.host.querySelector('#btn-gen') as HTMLElement).addEventListener('click', (e) => this.generate((e as MouseEvent).shiftKey));

    (this.host.querySelector('#view-2d') as HTMLElement).addEventListener('click', () => this.setView('2d'));
    (this.host.querySelector('#view-3d') as HTMLElement).addEventListener('click', () => this.setView('3d'));
    const back = this.host.querySelector('#btn-back-2d') as HTMLElement | null;
    back?.addEventListener('click', () => this.setView('2d'));

    (this.host.querySelector('#btn-undo') as HTMLElement).addEventListener('click', () => this.store.undo());
    (this.host.querySelector('#btn-redo') as HTMLElement).addEventListener('click', () => this.store.redo());
  }

  private bindTools(): void {
    const toolbar = this.host.querySelector('.toolbar') as HTMLElement;
    toolbar.addEventListener('click', (e) => {
      const t = (e.target as HTMLElement).closest('button') as HTMLElement | null;
      if (!t) return;
      if (t.dataset.bool) {
        this.store.applyBoolean(t.dataset.bool as 'union' | 'subtract' | 'intersect');
        return;
      }
      if (t.dataset.offset) {
        this.store.applyOffset(Number(t.dataset.offset));
        return;
      }
      if (t.dataset.kind) {
        this.store.setDrawKind(t.dataset.kind as DrawKind);
        this.store.setTool(t.dataset.tool as ToolId);
        return;
      }
      if (t.dataset.tool) this.store.setTool(t.dataset.tool as ToolId);
    });
  }

  private bindCanvas(): void {
    const c = this.canvas;
    c.addEventListener('pointerdown', (e) => {
      c.focus();
      this.ctrl.onPointerDown(e);
    });
    c.addEventListener('pointermove', (e) => this.ctrl.onPointerMove(e));
    c.addEventListener('pointerup', (e) => this.ctrl.onPointerUp(e));
    c.addEventListener('pointerleave', () => this.ctrl.onPointerLeave());
    c.addEventListener('dblclick', (e) => this.ctrl.onDoubleClick(e));
    c.addEventListener('wheel', (e) => this.ctrl.onWheel(e), { passive: false });
    c.addEventListener('contextmenu', (e) => this.ctrl.onContextMenu(e));
  }

  private bindKeys(): void {
    window.addEventListener('keydown', (e) => {
      const t = e.target as HTMLElement | null;
      if (t && /^(INPUT|TEXTAREA|SELECT)$/.test(t.tagName)) return;
      if (this.ctrl.onKeyDown(e)) e.preventDefault();
    });
    window.addEventListener('keyup', (e) => this.ctrl.onKeyUp(e));
  }

  private setView(v: '2d' | '3d'): void {
    this.store.patchSettings({ activeView: v });
    const note = this.host.querySelector('#stage-3d-note') as HTMLElement;
    const cv = this.canvas;
    note.hidden = v !== '3d';
    cv.style.visibility = v === '3d' ? 'hidden' : 'visible';
    (this.host.querySelector('#view-2d') as HTMLElement).classList.toggle('on', v === '2d');
    (this.host.querySelector('#view-3d') as HTMLElement).classList.toggle('on', v === '3d');
  }

  // ───────────────────────────── 生成

  private generate(reseed: boolean): void {
    const { model, view } = this.store.state;
    const sel = view.selectionIds.map((id) => model.objects[id]).filter((o): o is ParcelObj => !!o && o.kind === 'parcel');
    const parcels = sel.length ? sel : model.order.map((id) => model.objects[id]).filter((o): o is ParcelObj => !!o && o.kind === 'parcel');
    if (!parcels.length) {
      this.store.showToast('先画一个地块（P）再生成');
      return;
    }
    this.store.pushHistory(reseed ? '换一版生成' : '生成');
    let total = 0;
    const warnings: string[] = [];
    for (const p of parcels) {
      const target = reseed ? ({ ...p, seed: (p.seed + 1) % 100000 } as ParcelObj) : p;
      if (reseed) this.store.updateObject(p.id, { seed: target.seed } as Partial<SceneObject>);
      const all = Object.values(this.store.state.model.objects);
      const res = generateParcel(target, all);
      this.store.applyGenerated(p.id, res.objects, res.stats);
      total += res.stats.buildings;
      if (res.warning) warnings.push(`${p.name}：${res.warning}`);
    }
    if (warnings.length) this.store.showToast(warnings[0]);
    else this.store.showToast(`生成完成：${total} 栋`);
  }

  // ───────────────────────────── 渲染循环

  private resize(): void {
    const host = this.host.querySelector('#stage') as HTMLElement;
    const r = host.getBoundingClientRect();
    const dpr = Math.min(2, window.devicePixelRatio || 1);
    const w = Math.max(1, r.width);
    const h = Math.max(1, r.height);
    this.canvas.width = Math.round(w * dpr);
    this.canvas.height = Math.round(h * dpr);
    this.canvas.style.width = `${w}px`;
    this.canvas.style.height = `${h}px`;
    this.ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    this.cam.setViewport(w, h);
    this.viewW = w;
    this.viewH = h;
  }

  private loop = (): void => {
    requestAnimationFrame(this.loop);
    if (this.store.state.view.activeView !== '2d') return;
    this.ctrl.render(this.ctx, this.viewW, this.viewH);
  };

  private onStoreChange(): void {
    if (this.store.version === this.lastVersion) return;
    this.lastVersion = this.store.version;
    const key = this.panelKey();
    if (key !== this.lastPanelKey) {
      this.lastPanelKey = key;
      this.renderInspector();
    }
    this.renderLayers();
    this.renderToolStates();
    this.renderBadge();
    this.renderToast();
    this.renderStatus();
    const name = this.host.querySelector('#proj-name') as HTMLInputElement;
    if (document.activeElement !== name) name.value = this.store.state.model.name;
  }

  /** 面板要不要重建：选中变了、有非静默改动、生成统计变了才重建。
   *  拖动（静默）时不重建 —— 否则输入框每次 move 都失焦。 */
  private panelKey(): string {
    const v = this.store.state.view;
    const gens = Object.keys(this.store.state.genStats).length;
    return `${v.selectionIds.join(',')}|${this.store.version - this.store.silentSeq}|${gens}|${v.activeView}`;
  }

  private renderAll(): void {
    this.renderInspector();
    this.renderLayers();
    this.renderToolStates();
    this.renderBadge();
    this.renderStatus();
  }

  private renderToolStates(): void {
    const v = this.store.state.view;
    this.host.querySelectorAll('.tool[data-tool]').forEach((b) => {
      const el = b as HTMLElement;
      if (el.dataset.kind) {
        const on = v.drawKind === el.dataset.kind && v.activeTool !== 'select' && v.activeTool !== 'pan';
        el.classList.toggle('on', on);
      } else {
        el.classList.toggle('on', v.activeTool === el.dataset.tool);
      }
    });
    (this.host.querySelector('#btn-undo') as HTMLElement).classList.toggle('dim', this.store.state.past.length === 0);
    (this.host.querySelector('#btn-redo') as HTMLElement).classList.toggle('dim', this.store.state.future.length === 0);
  }

  private renderBadge(): void {
    const v = this.store.state.view;
    const isDraw = v.activeTool !== 'select' && v.activeTool !== 'pan';
    const parts = [`<b>${TOOL_LABEL[v.activeTool]}</b>`];
    if (isDraw) parts.push(`<span>· 绘制为 ${DRAW_KIND_LABEL[v.drawKind]}</span>`);
    if (v.activeTool === 'select') parts.push('<span>· 拖动移动 / 双击聚焦 / Shift 多选</span>');
    if (isDraw) parts.push('<span>· 右键 / 回车 结束 · Esc 退出</span>');
    (this.host.querySelector('#badge') as HTMLElement).innerHTML = parts.join('');
  }

  private renderToast(): void {
    const box = this.host.querySelector('#toast') as HTMLElement;
    const t = this.store.state.toast;
    if (!t) {
      box.textContent = '';
      box.classList.remove('show');
      return;
    }
    // 长了会横穿画布挡住内容；完整信息在生成面板的读数里，toast 只是提示
    box.textContent = t.length > 46 ? `${t.slice(0, 46)}…` : t;
    box.classList.add('show');
    window.clearTimeout((this as any)._toastTimer);
    (this as any)._toastTimer = window.setTimeout(() => {
      this.store.showToast('');
    }, 2400);
  }

  private renderStatus(): void {
    const h = this.store.state.hint ?? EMPTY_HINT;
    const v = this.store.state.view;
    const sel = v.selectionIds.length;
    const n = this.store.state.model.order.length;
    const fmt = (x: number, d = 2): string => x.toFixed(d);
    const el = this.host.querySelector('#statusbar') as HTMLElement;
    el.innerHTML = `
      <span>X <b>${fmt(h.x)}</b> Y <b>${fmt(h.y)}</b></span><span class="sep"></span>
      <span>长度 <b>${h.length !== undefined ? `${fmt(h.length)} m` : '—'}</b></span>
      <span>角度 <b>${h.angle !== undefined ? `${fmt(h.angle * DEG, 1)}°` : '—'}</b></span>
      <span>面积 <b>${h.area !== undefined ? fmtArea(h.area) : '—'}</b></span><span class="sep"></span>
      <span>Snap: <b class="${h.snapLabel ? 'ok' : ''}">${h.snapLabel || (v.snapEnabled ? 'on' : 'off')}</b></span>
      <span>Grid: <b>${v.gridSize} m</b>${v.gridSnap ? '' : ' (不吸附)'}</span><span class="sep"></span>
      <span>工具 <b>${TOOL_LABEL[v.activeTool]}</b></span>
      <span>选中 <b>${sel}</b></span><span class="sep"></span>
      <span>缩放 <b>${fmt(v.zoom, 2)}</b> px/m</span><span class="sep"></span>
      <span>对象 ${n}</span>`;
  }

  // ───────────────────────────── 图层

  private renderLayers(): void {
    const { model, view } = this.store.state;
    const counts: Record<string, number> = {};
    for (const id of model.order) {
      const o = model.objects[id];
      if (!o) continue;
      const layer = LAYER_OF[o.kind];
      counts[layer] = (counts[layer] ?? 0) + 1;
    }
    const html = `
      <div class="panel">
        <h3>图层 <span class="count">语义层</span></h3>
        ${LAYERS.map((l) => {
          const vis = view.layerVisibility[l.key];
          const lock = view.layerLocked[l.key];
          return `<div class="layer ${vis ? '' : 'off'}">
            <button class="ico ${vis ? 'active' : ''}" data-vis="${l.key}" title="${vis ? '隐藏图层' : '显示图层'}">${vis ? '👁' : '🚫'}</button>
            <button class="ico ${lock ? 'lit' : ''}" data-lock="${l.key}" title="${lock ? '解锁图层' : '锁定图层'}">${lock ? '🔒' : '🔓'}</button>
            <span class="name">${l.label}</span>
            <span class="num">${counts[l.key] ?? 0}</span>
          </div>`;
        }).join('')}
      </div>`;
    const host = this.host.querySelector('#layers') as HTMLElement;
    host.innerHTML = html;
    host.querySelectorAll('[data-vis]').forEach((b) =>
      b.addEventListener('click', () => this.store.toggleLayerVisible((b as HTMLElement).dataset.vis as LayerKey)),
    );
    host.querySelectorAll('[data-lock]').forEach((b) =>
      b.addEventListener('click', () => this.store.toggleLayerLocked((b as HTMLElement).dataset.lock as LayerKey)),
    );
  }

  // ───────────────────────────── 属性面板

  private renderInspector(): void {
    const host = this.host.querySelector('#inspector') as HTMLElement;
    const { model, view } = this.store.state;
    const sel = view.selectionIds.map((id) => model.objects[id]).filter((o): o is SceneObject => !!o);

    if (sel.length === 0) {
      host.innerHTML = this.inspectorEmpty();
      this.bindInspectorEmpty(host);
      return;
    }
    if (sel.length > 1) {
      host.innerHTML = `
        <div class="panel">
          <h3>多选 <span class="count">${sel.length} 个对象</span></h3>
          <div class="empty">布尔以 <b>第一个选中</b> 的对象为主对象；<b>减去</b> 只改主对象，被减体保留。</div>
          <div class="row-btns">
            <button class="btn" data-act="dup">复制</button>
            <button class="btn" data-act="lock">锁定 / 解锁</button>
            <button class="btn" data-act="del">删除</button>
          </div>
        </div>`;
      this.bindActs(host);
      return;
    }

    const o = sel[0];
    const paths = areaPaths(o);
    const b = paths ? boundsOfPaths(paths) : null;
    const area = paths ? pathsArea(paths) : 0;
    const per = paths ? pathsPerimeter(paths) : 0;
    const badge = `<span class="chip ${o.origin === 'generated' ? 'gen' : 'user'}">${o.origin === 'generated' ? 'Generated' : 'Fixed'}</span>${o.locked ? '<span class="chip lock">Locked</span>' : ''}`;

    host.innerHTML = `
      <div class="panel">
        <h3>${KIND_LABEL[o.kind]} <span class="count">${badge}</span></h3>
        <div class="field"><label>名称</label><input id="f-name" type="text" value="${esc(o.name)}"/></div>
        <div class="row-btns">
          <button class="btn" data-act="lock">${o.locked ? '解锁' : '锁定'}</button>
          <button class="btn" data-act="fix">转为固定</button>
          <button class="btn" data-act="del">删除</button>
        </div>
      </div>
      ${
        paths && b && boundsValid(b)
          ? `<div class="panel">
        <h3>几何</h3>
        ${row('面积', fmtArea(area))}
        ${row('周长', `${per.toFixed(2)} m`)}
        ${row('尺寸', `${(b.maxX - b.minX).toFixed(2)} × ${(b.maxY - b.minY).toFixed(2)} m`)}
        ${row('中心', `${ringCenter(paths[0] ?? []).x.toFixed(1)}, ${ringCenter(paths[0] ?? []).y.toFixed(1)}`)}
        ${row('环 / 顶点', `${paths.length} / ${objectVertices(o).length}`)}
      </div>`
          : ''
      }
      ${this.paramsPanel(o)}
      ${this.genPanel(o)}
      ${
        isAreaObj(o)
          ? `<div class="panel"><h3>对象操作</h3><div class="row-btns">
          <button class="btn" data-act="dup">复制 (Ctrl+D)</button>
          <button class="btn" data-offset="-3">内缩 3 m</button>
          <button class="btn" data-offset="3">外扩 3 m</button>
        </div></div>`
          : ''
      }`;

    this.bindActs(host);
    this.bindParams(host, o);
  }

  private inspectorEmpty(): string {
    const { model, view } = this.store.state;
    const counts = { parcel: 0, building: 0, road: 0, access: 0, ground: 0, nobuild: 0 };
    for (const id of model.order) {
      const o = model.objects[id];
      if (!o) continue;
      if (o.kind in counts) counts[o.kind as keyof typeof counts]++;
    }
    return `
      <div class="panel">
        <h3>项目设置</h3>
        ${numField('网格间距 (m)', view.gridSize, 0.5, 2, 'f-grid')}
        ${toggleField('网格显示', view.gridVisible, 'gridVisible')}
        ${toggleField('网格吸附', view.gridSnap, 'gridSnap')}
        ${toggleField('对象吸附', view.snapEnabled, 'snap')}
      </div>
      <div class="panel">
        <h3>选择过滤 <span class="count">Selection Filter</span></h3>
        <div class="field"><label>仅可选</label>
          <select id="f-filter">
            ${[
              ['all', '全部'],
              ['parcels', '地块'],
              ['buildings', '建筑'],
              ['roads', '道路'],
              ['ground', '地面 / 禁建'],
              ['generated', '仅生成物'],
            ]
              .map(([k, v]) => `<option value="${k}" ${view.selectionFilter === k ? 'selected' : ''}>${v}</option>`)
              .join('')}
          </select>
        </div>
      </div>
      <details class="panel" open>
        <summary>模型统计 <span class="count">${model.order.length} 个对象</span></summary>
        ${row('地块', String(counts.parcel))}
        ${row('建筑', String(counts.building))}
        ${row('道路', String(counts.road))}
        ${row('出入口', String(counts.access))}
        ${row('地面', String(counts.ground))}
        ${row('禁建区', String(counts.nobuild))}
      </details>
      <details class="panel">
        <summary>快捷键 <span class="count">Keyboard</span></summary>
        <div class="empty">
          <span class="kbd">V</span> 选择 · <span class="kbd">R</span> 矩形 · <span class="kbd">P</span> 多边形 ·
          <span class="kbd">L</span> 折线 · <span class="kbd">C</span> 圆 · <span class="kbd">M</span> 测量<br/>
          <span class="kbd">F</span> 聚焦选中 · <span class="kbd">Home</span> 全览 · <span class="kbd">Space</span> 拖拽平移<br/>
          <span class="kbd">G</span> 生成地块 · <span class="kbd">Shift+G</span> 换一版<br/>
          <span class="kbd">Shift</span> 多选 / 45° 约束 · <span class="kbd">Ctrl+Z</span> 撤销 · <span class="kbd">Del</span> 删除
        </div>
      </details>`;
  }

  private bindInspectorEmpty(host: HTMLElement): void {
    const g = host.querySelector('#f-grid') as HTMLInputElement | null;
    g?.addEventListener('change', () => this.store.patchSettings({ gridSize: Math.max(0.05, Number(g.value) || 1) }));
    host.querySelectorAll('[data-tg]').forEach((b) =>
      b.addEventListener('click', () => {
        const k = (b as HTMLElement).dataset.tg!;
        const v = this.store.state.view;
        if (k === 'gridVisible') this.store.patchSettings({ gridVisible: !v.gridVisible });
        if (k === 'gridSnap') this.store.patchSettings({ gridSnap: !v.gridSnap });
        if (k === 'snap') this.store.patchSettings({ snapEnabled: !v.snapEnabled });
      }),
    );
    const f = host.querySelector('#f-filter') as HTMLSelectElement | null;
    f?.addEventListener('change', () => this.store.setFilter(f.value as never));
  }

  /** 按类型的参数块 —— 这就是"工具的界面定义"，一项不能少。 */
  private paramsPanel(o: SceneObject): string {
    if (o.kind === 'parcel') {
      return `<div class="panel"><h3>地块参数</h3>
        <div class="field"><label>用地类型</label>
          <select id="p-zone">${Object.entries(ZONE_OPTIONS)
            .map(([k, v]) => `<option value="${k}" ${o.zoneKind === k ? 'selected' : ''}>${v}</option>`)
            .join('')}</select>
        </div>
        ${numField('建筑密度', o.density, 0.05, 2, 'p-density')}
        ${numField('容积率', o.far, 0.1, 2, 'p-far')}
        ${numField('高度下限', o.heightRange[0], 1, 0, 'p-hmin')}
        ${numField('高度上限', o.heightRange[1], 1, 0, 'p-hmax')}
        ${numField('Seed', o.seed, 1, 0, 'p-seed')}
        <div class="empty">密度 / 容积率 / 高度区间 / Seed 都真的进生成算法，不是摆设（docs/G §3）。</div>
      </div>`;
    }
    if (o.kind === 'building') {
      return `<div class="panel"><h3>建筑参数</h3>
        ${numField('高度 (m)', o.height, 0.5, 2, 'b-height')}
        ${numField('层数', o.floors, 1, 0, 'b-floors')}
        <div class="field"><label>风格</label>
          <select id="b-style">
            <option value="geometric" ${o.style === 'geometric' ? 'selected' : ''}>Geometric</option>
            <option value="semiRealistic" ${o.style === 'semiRealistic' ? 'selected' : ''}>Semi-Realistic</option>
          </select>
        </div>
        ${toggleField('固定（生成不再改动）', o.fixed, 'b-fixed')}
        ${o.slotId ? `<div class="empty">由楼位 <code>${esc(o.slotId)}</code> 生成。</div>` : ''}
      </div>`;
    }
    if (o.kind === 'road') {
      const len = o.centerline.reduce((s, p, i) => (i ? s + Math.hypot(p.x - o.centerline[i - 1].x, p.y - o.centerline[i - 1].y) : 0), 0);
      return `<div class="panel"><h3>道路参数</h3>
        <div class="field"><label>道路等级</label>
          <select id="r-class">${Object.entries(ROAD_LABEL)
            .map(([k, v]) => `<option value="${k}" ${o.roadClass === k ? 'selected' : ''}>${v}</option>`)
            .join('')}</select>
        </div>
        ${numField('宽度 (m)', o.width, 0.5, 1, 'r-width')}
        ${toggleField('人行道', o.sidewalk, 'r-side')}
        ${row('中心线长度', `${len.toFixed(2)} m`)}
        ${row('控制点', String(o.centerline.length))}
      </div>`;
    }
    if (o.kind === 'access') {
      return `<div class="panel"><h3>出入口</h3>
        ${numField('开口宽度 (m)', o.width, 0.5, 1, 'a-width')}
        ${numField('朝向 (°)', o.dir * DEG, 15, 1, 'a-dir')}
        <div class="field"><label>类型</label>
          <select id="a-type">${Object.entries(ACCESS_LABEL)
            .map(([k, v]) => `<option value="${k}" ${o.accessType === k ? 'selected' : ''}>${v}</option>`)
            .join('')}</select>
        </div>
        <div class="empty">拖动 2D 里的方向箭头可改朝向（按住 Shift 吸附 45°）。</div>
      </div>`;
    }
    if (o.kind === 'ground') {
      return `<div class="panel"><h3>地面 <span class="count">${SURFACE_LABEL[o.surface]}</span></h3>
        <div class="field"><label>表面</label>
          <select id="g-surface">${Object.entries(SURFACE_LABEL)
            .map(([k, v]) => `<option value="${k}" ${o.surface === k ? 'selected' : ''}>${v}</option>`)
            .join('')}</select>
        </div>
        ${row('铺设面积', fmtArea(Math.abs(pathsArea(areaPaths(o) ?? []))))}
        ${o.generatedFrom ? '<div class="empty">这块地面由地块生成；改表面类型会保留，重新生成会被覆盖。</div>' : ''}
      </div>`;
    }
    return '';
  }

  /** 地块的生成面板：读数只在有统计时显示，撤销回到"没统计"就显示提示，不显示上一次的读数。 */
  private genPanel(o: SceneObject): string {
    if (o.kind !== 'parcel') return '';
    const st = this.store.state.genStats[o.id];
    const count = this.store.state.model.order.reduce((n, id) => {
      const x = this.store.state.model.objects[id];
      return x && x.kind === 'building' && x.generatedFrom === o.id ? n + 1 : n;
    }, 0);
    const pct = (v: number): string => `${(v * 100).toFixed(0)}%`;
    return `<div class="panel">
      <h3>生成（TS v0）</h3>
      <div class="row-btns">
        <button class="btn primary" data-gen="1">生成</button>
        <button class="btn" data-gen="reseed">换一版</button>
        <button class="btn ghost" data-gen="clear" ${count ? '' : 'disabled'}>清除生成</button>
      </div>
      ${
        count === 0
          ? '<div class="empty">尚未生成。生成只写入本图层的 Generated 对象；手绘建筑与"固定"对象不会被改动。</div>'
          : st
            ? `${row('生成建筑', `${count} 栋`)}
               ${row('覆盖率', `${pct(st.coverage)} / 上限 ${pct(o.density)}`)}
               ${row('容积率', `${st.far.toFixed(2)} / 目标 ${o.far.toFixed(2)}`)}
               ${row('最高', `${st.maxHeight.toFixed(1)} m`)}
               ${row('退线', `${st.setback} m`)}
               ${row('退线后可建', fmtArea(st.buildableArea))}
               ${row('净可建', fmtArea(st.freeArea))}
               ${row('街道 / 宗地 / 楼位', `${st.streets} / ${st.zoning} / ${st.slots}`)}`
            : `${row('生成建筑', `${count} 栋（重新生成可查看统计）`)}`
      }
      <div class="empty">v0 只做：脊街 + 沿街宗地 + 楼位。断街交叉、ready 判定在 G5 接 C#。</div>
    </div>`;
  }

  private bindActs(host: HTMLElement): void {
    host.querySelectorAll('[data-act]').forEach((b) =>
      b.addEventListener('click', () => {
        const a = (b as HTMLElement).dataset.act;
        if (a === 'del') this.store.deleteSelected();
        if (a === 'dup') this.store.duplicateSelected();
        if (a === 'lock') this.store.toggleLock();
        if (a === 'fix') this.store.promoteToFixed();
      }),
    );
    host.querySelectorAll('[data-offset]').forEach((b) =>
      b.addEventListener('click', () => this.store.applyOffset(Number((b as HTMLElement).dataset.offset))),
    );
    host.querySelectorAll('[data-gen]').forEach((b) =>
      b.addEventListener('click', () => {
        const m = (b as HTMLElement).dataset.gen;
        const id = this.store.state.view.selectionIds[0];
        if (m === 'clear') this.store.clearGenerated(id);
        else this.generate(m === 'reseed');
      }),
    );
    const name = host.querySelector('#f-name') as HTMLInputElement | null;
    if (name) {
      name.addEventListener('change', () => {
        const id = this.store.state.view.selectionIds[0];
        this.store.pushHistory('重命名');
        this.store.updateObject(id, { name: name.value } as Partial<SceneObject>);
      });
    }
  }

  private bindParams(host: HTMLElement, o: SceneObject): void {
    const commit = (patch: Partial<SceneObject>, label: string): void => {
      this.store.pushHistory(label);
      this.store.updateObject(o.id, patch);
    };
    const num = (id: string, fn: (n: number) => void): void => {
      const el = host.querySelector(`#${id}`) as HTMLInputElement | null;
      if (!el) return;
      el.addEventListener('change', () => {
        const n = Number(el.value);
        if (Number.isFinite(n)) fn(n);
      });
    };

    if (o.kind === 'parcel') {
      const zone = host.querySelector('#p-zone') as HTMLSelectElement | null;
      zone?.addEventListener('change', () => commit({ zoneKind: zone.value as ZoneKind } as Partial<SceneObject>, '修改用地类型'));
      num('p-density', (n) => commit({ density: Math.max(0.05, Math.min(0.95, n)) } as Partial<SceneObject>, '修改密度'));
      num('p-far', (n) => commit({ far: Math.max(0.1, n) } as Partial<SceneObject>, '修改容积率'));
      num('p-hmin', (n) =>
        commit({ heightRange: [n, Math.max(n, o.heightRange[1])] } as Partial<SceneObject>, '修改高度区间'),
      );
      num('p-hmax', (n) =>
        commit({ heightRange: [Math.min(n, o.heightRange[0]), n] } as Partial<SceneObject>, '修改高度区间'),
      );
      num('p-seed', (n) => commit({ seed: Math.round(n) } as Partial<SceneObject>, '修改 Seed'));
    }
    if (o.kind === 'building') {
      num('b-height', (n) => commit({ height: Math.max(1, n) } as Partial<SceneObject>, '修改高度'));
      num('b-floors', (n) => commit({ floors: Math.max(1, Math.round(n)) } as Partial<SceneObject>, '修改层数'));
      const style = host.querySelector('#b-style') as HTMLSelectElement | null;
      style?.addEventListener('change', () => commit({ style: style.value as never } as Partial<SceneObject>, '修改风格'));
    }
    if (o.kind === 'road') {
      const cls = host.querySelector('#r-class') as HTMLSelectElement | null;
      cls?.addEventListener('change', () => {
        const rc = cls.value as RoadClass;
        commit({ roadClass: rc, width: ROAD_WIDTH[rc] } as Partial<SceneObject>, '修改道路等级');
      });
      num('r-width', (n) => commit({ width: Math.max(1, n) } as Partial<SceneObject>, '修改路宽'));
    }
    if (o.kind === 'access') {
      num('a-width', (n) => commit({ width: Math.max(1, n) } as Partial<SceneObject>, '修改开口宽度'));
      num('a-dir', (n) => commit({ dir: n / DEG } as Partial<SceneObject>, '修改朝向'));
      const t = host.querySelector('#a-type') as HTMLSelectElement | null;
      t?.addEventListener('change', () => commit({ accessType: t.value as never } as Partial<SceneObject>, '修改出入口类型'));
    }
    if (o.kind === 'ground') {
      const s = host.querySelector('#g-surface') as HTMLSelectElement | null;
      s?.addEventListener('change', () => commit({ surface: s.value as never } as Partial<SceneObject>, '修改表面'));
    }
    host.querySelectorAll('[data-tg]').forEach((b) =>
      b.addEventListener('click', () => {
        const k = (b as HTMLElement).dataset.tg;
        if (k === 'b-fixed') {
          if ((o as BuildingObj).fixed) commit({ fixed: false } as Partial<SceneObject>, '取消固定');
          else this.store.promoteToFixed(o.id);
        }
        if (k === 'r-side') commit({ sidewalk: !(o as RoadObj).sidewalk } as Partial<SceneObject>, '人行道切换');
      }),
    );
  }
}

// ───────────────────────────── 小组件

const esc = (s: string): string =>
  String(s).replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' })[c]!);

const row = (k: string, v: string): string => `<div class="row"><span class="k">${k}</span><span class="v">${v}</span></div>`;

const numField = (label: string, value: number, step: number, digits: number, id: string): string =>
  `<div class="field"><label>${label}</label><input id="${id}" type="number" step="${step}" value="${value.toFixed(digits)}"/></div>`;

const toggleField = (label: string, on: boolean, key: string): string =>
  `<div class="field"><label>${label}</label><button class="btn ${on ? 'primary' : 'ghost'} sm" data-tg="${key}">${on ? '开' : '关'}</button></div>`;

export function mount(host: HTMLElement): EditorApp {
  return new EditorApp(host);
}

