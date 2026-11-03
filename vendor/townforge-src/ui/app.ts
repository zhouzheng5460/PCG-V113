// 主界面：参数面板 + 2D/3D 视图切换 + GLB 导出

import './style.css';
import { Pt } from '../core/geom';
import { Model, type TownStyle } from '../town/model';
import type { ParkLevel } from '../town/parking';
import type { LayoutKind } from '../town/grid';
import { Zone, makeZone } from '../town/zones';
import { type FieldGuide, makeGuide } from '../town/guide';
import { USE_LABEL, useColor } from '../town/landuse';
import type { LandUse } from '../scene/types';
import { View2D, type DrawMode, type RoadPick, type SelKind } from './view2d';
import { applySnapshot, decodeSnapshot, encodeSnapshot } from './snapshot';
import { View3D } from './view3d';
import { buildTownMeshes } from '../export/build3d';
import { ROAD_SPECS, u } from '../road/spec';
import {
  type TownParams,
  type ParamSpec,
  BLOCK_SPECS,
  BUILDING_SPECS,
  defaultParams,
  resolveParams,
} from '../town/params';
import { exportGLB } from '../export/glb';

/** 街网形态的中文名（与 grid.ts 的 LayoutKind 一一对应） */
const LAYOUT_LABEL: Record<LayoutKind, string> = {
  grid: '方格网',
  field: '场线',
  radial: '放射环',
  tensor: '张量场',
};

/** 停车配比三档的中文名 */
const PARK_LABEL: Record<ParkLevel, string> = {
  low: '低配',
  normal: '常规',
  high: '高配',
};

const LAYOUT_TIP: Record<LayoutKind, string> = {
  grid: '方格网：全局正交街网 + 低频弯曲，主干道 / 次干道 / 支路三级。北美棋盘城形态。',
  field: '场线：旋涡张量场，街道绕中心连续弯曲，街廓仍是闭合地块。巴塞罗那扩展区形态。',
  radial: '放射环：同心环线 + 放射辐条，环半径带谐波起伏并轻微螺旋。华盛顿 / 堪培拉形态。',
  tensor: '张量场：基准方向 + 边界切向 + 中心旋涡/放射 + 禁建区轮廓合成场，沿场追流线，求交后切出封闭街区。全局一致、局部弯曲，街区不再靠随机撒点。',
};

// 错误提示条：白屏时至少能看见原因
let errorShown = false;
window.addEventListener('error', (e) => {
  if (errorShown) return;
  errorShown = true;
  const d = document.createElement('div');
  d.style.cssText =
    'position:fixed;left:0;right:0;bottom:0;background:#c4553f;color:#fff;padding:10px;font:12px ui-monospace,monospace;z-index:999;white-space:pre-wrap';
  d.textContent = `JS ERROR: ${e.message} @ ${e.filename}:${e.lineno}:${e.colno}\n${(e.error as Error)?.stack ?? ''}`;
  document.body.appendChild(d);
});

interface State {
  nPatches: number;
  seed: number;
  plaza: boolean;
  style: TownStyle;
  zones: Zone[];
  guides: FieldGuide[];
  guideStrength: number;
  heightScale: number;
  zoneThreshold: number;
  showVertices: boolean;
  layout: LayoutKind;
  markings: boolean;
  furniture: boolean;
  showBlocks: boolean;
  /** 停车配比三档 */
  parkingLevel: ParkLevel;
  /** 地块 / 建筑 / 绿地参数（完整对象，改动前先过 resolveParams 钳制） */
  params: TownParams;
}

const state: State = {
  nPatches: 15,
  seed: 682063530,
  plaza: true,
  // 三档红线宽直接取 road/spec.ts 的规范表：主干路 28 m（双向六车道）、
  // 次干路 20 m（双向四车道）、支路 10.5 m（双向两车道）。
  // 早先这里写死 2 / 1 / 0.6 单位（14 / 7 / 4.2 m），被 insetForEdge 的
  // minRedLine 下限顶回去，实际断面是 24 / 17 / 8.5 m —— 和表对不上，
  // 六车道的车行道只剩 21 m 里挤出两条挤在一起的人行道。
  style: {
    mainStreetWidth: u(ROAD_SPECS.arterial.redLine),
    regularStreetWidth: u(ROAD_SPECS.collector.redLine),
    alleyWidth: u(ROAD_SPECS.local.redLine),
  },
  zones: [],
  guides: [],
  guideStrength: 0.8,
  heightScale: 1,
  zoneThreshold: 0.35,
  showVertices: false,
  layout: 'tensor',
  markings: true,
  furniture: true,
  showBlocks: false,
  parkingLevel: 'normal',
  params: defaultParams(),
};

let model: Model;
let view2d: View2D;
let view3d: View3D | null = null;
let currentView: '2d' | '3d' = '2d';
/** 当前在 2D 里点中的那条路（侧栏"选中路"滑杆作用的对象） */
let currentRoad: RoadPick | null = null;

const $ = <T extends HTMLElement>(sel: string): T => document.querySelector(sel) as T;

/** 红线宽显示：世界单位 ≈ 7 m */
function formatWidth(w: number): string {
  return `${w.toFixed(1)} (${Math.round(w * 7)}m)`;
}

/**
 * 把"选中的路"同步到侧栏滑杆。
 * 选中 / 取消 / 改完宽度之后都要调 —— 滑杆的上下限随道路等级变
 * （主干道下限 24 m、次干道 17 m、支路 8.5 m，低于下限街角圆角就画不出来）。
 */
function syncRoadRow(road: RoadPick | null): void {
  currentRoad = road;
  const row = document.getElementById('row-artery');
  const input = document.querySelector<HTMLInputElement>('#rng-artery');
  const val = document.getElementById('val-artery');
  const reset = document.querySelector<HTMLButtonElement>('#btn-artery-reset');
  if (!row || !input) return;
  if (!road) {
    row.style.opacity = '.45';
    input.disabled = true;
    if (reset) reset.disabled = true;
    if (val) val.textContent = '—';
    return;
  }
  row.style.opacity = '1';
  input.disabled = false;
  if (reset) reset.disabled = false;
  input.min = road.min.toFixed(1);
  input.max = String(road.max);
  input.value = String(Math.min(road.max, Math.max(road.min, road.width)));
  if (val) val.textContent = formatWidth(road.width) + (road.custom ? ' *' : '');
}

// ---------------------------------------------------------------- 生成

function createModel(): Model {
  const m = new Model({
    nPatches: state.nPatches,
    seed: state.seed,
    plaza: state.plaza,
    zones: state.zones,
    guides: state.guides,
    style: state.style,
    zoneBlockThreshold: state.zoneThreshold,
    layout: state.layout,
    parkingLevel: state.parkingLevel,
    params: state.params,
  });
  m.generate();
  return m;
}

/** 统一的 3D 刷新入口：楼高 + 图层开关一起传，避免各处漏传 */
function refresh3D(): void {
  if (currentView !== '3d' || !view3d) return;
  view3d.setModel(model, {
    heightScale: state.heightScale,
    markings: state.markings,
    furniture: state.furniture,
  });
}

function regenerate(): void {
  try {
    model = createModel();
    // keepView：改种子 / 拖方向线不该把视角甩回全图
    view2d.setModel(model, { keepView: true });
    if (currentView === '3d') refresh3D();
    updateStats();
    syncHashSoon();
  } catch (e) {
    toast(`生成失败：${(e as Error).message}`, true);
  }
}

/** 只重建几何，不重跑 Voronoi（拖点、改路宽走这条） */
function rebuildGeometry(): void {
  try {
    model.rebuild();
    view2d.draw();
    if (currentView === '3d') refresh3D();
    updateStats();
  } catch (e) {
    toast(`重建失败：${(e as Error).message}`, true);
    view2d.draw();
  }
}

function updateZones(zones: Zone[]): void {
  state.zones = zones;
  model.zones = zones;
  model.setZones(zones);
  view2d.setZones(zones);
  view2d.draw();
  renderZoneList();
  updateStats();
  if (currentView === '3d') refresh3D();
}

/**
 * 方向控制线改动后统一走这里。
 * 和禁建区不同，方向线参与的是"切块"这一步（张量场 → 流线 → 面），
 * 生成时才求值，所以必须整城重生，不能只 rebuild 几何。
 */
function updateGuides(guides: FieldGuide[]): void {
  state.guides = guides;
  view2d.setGuides(guides);
  renderGuideList();
  regenerate();
}

function updateStats(): void {
  const s = model.stats();
  const parts = [
    `${s.patches} 地块`,
    `${s.buildings} 建筑`,
  ];
  if (s.blocked > 0) parts.push(`<span style="color:#c4553f">${s.blocked} 禁建</span>`);

  // 第二行是"场景指纹"：同一座城 = 同一组（种子 + 主开关 + 手画物件）。
  // 截图里自带这一行，就不用再靠"我这边和你那边"猜参数了 —— 见过太多次
  // "看起来不一样"其实是种子差一位、或者少了两条方向线。
  const P = state.params.block;
  const fp = [
    `种子 ${state.seed}`,
    `${state.nPatches} 街区`,
    LAYOUT_LABEL[state.layout],
    P.parcelize ? `小地块 ${P.parcelPitchM}m` : '整块地块',
    P.zoneAvoid ? `避障 ${P.zoneAvoidPadM}m` : '避障关',
    P.serviceLoop ? '服务环路' : '',
    state.guides.length > 0 ? `方向线 ${state.guides.length}` : '',
    state.zones.length > 0 ? `禁建 ${state.zones.length}` : '',
  ].filter(Boolean);

  $('#stats').innerHTML =
    `<div>${parts.join(' · ')}</div>` +
    `<div style="opacity:.62;margin-top:2px;font-size:10px">${fp.join(' · ')}</div>`;
  renderLegend();
}

// ---------------------------------------------------------------- 面板

function section(title: string, body: string): string {
  return `<section class="sec"><h3><span class="dot"></span>${title}</h3>${body}</section>`;
}

/**
 * 一行参数滑杆。
 *
 * `input` 只更新数字标签（拖动时不该每帧重跑生成，一次生成 ~400 ms）；
 * 真正的重生成挂在 `change`（松手）上 —— 见 bindSidebar 里的委托监听。
 */
function paramRow(group: 'block' | 'building', sp: ParamSpec): string {
  const v = (state.params[group] as unknown as Record<string, number>)[sp.key];
  const id = `p-${group}-${sp.key}`;
  return `<div class="row"><label>${sp.label}</label>
      <input type="range" class="grow" id="rng-${id}" min="${sp.min}" max="${sp.max}" step="${sp.step}"
             value="${v}" data-pgroup="${group}" data-pkey="${sp.key}"/>
      <span class="val" id="val-${id}">${v}${sp.unit ?? ''}</span>
    </div>`;
}

function buildSidebar(): void {
  // 模板只有现代城市一种；保留这个常量是为了让下面各段的功能开关都走现代分支
  const modern = true;
  const sizes = [
    { n: 6, label: '小镇', desc: '6 片街区' },
    { n: 10, label: '大镇', desc: '10 片街区' },
    { n: 15, label: '小城', desc: '15 片街区' },
    { n: 24, label: '大城', desc: '24 片街区' },
  ];

  const citySection = section(
    '城市',
    `
    <div class="row"><label>规模</label>
      <div class="seg" id="seg-size">
        ${sizes.map((s) => `<button data-n="${s.n}" class="${s.n === state.nPatches ? 'active' : ''}">${s.label}</button>`).join('')}
      </div>
    </div>
    ${
      modern
        ? `<div class="row"><label>布局</label>
      <div class="seg" id="seg-layout">
        ${(Object.keys(LAYOUT_LABEL) as LayoutKind[])
          .map((k) => `<button data-layout="${k}" class="${k === state.layout ? 'active' : ''}">${LAYOUT_LABEL[k]}</button>`)
          .join('')}
      </div>
    </div>
    <div class="row"><label>停车配比</label>
      <div class="seg" id="seg-park">
        ${(['low', 'normal', 'high'] as ParkLevel[])
          .map(
            (k) =>
              `<button data-park="${k}" class="${k === state.parkingLevel ? 'active' : ''}">${PARK_LABEL[k]}</button>`
          )
          .join('')}
      </div>
    </div>`
        : ''
    }
    <div class="row"><label>种子</label>
      <input type="number" id="inp-seed" value="${state.seed}" class="grow" />
      <button class="ghost" id="btn-dice" title="随机种子">随机</button>
    </div>
    <div class="row" style="gap:14px;flex-wrap:wrap">
      <label class="check"><input type="checkbox" id="chk-plaza" ${state.plaza ? 'checked' : ''}/>中心广场</label>
    </div>
    <div class="row" style="margin-top:10px"><button class="ghost grow" id="btn-regen">重新生成</button></div>
    <div class="tip" style="margin-top:8px">${LAYOUT_TIP[state.layout]}</div>
    `
  );

  // 方向控制：只在张量场布局下有意义（只有它会追流线）
  const guideSection = modern
    ? state.layout === 'tensor'
      ? section(
          '方向控制',
          `
        <div class="row"><label>工具</label>
          <div class="seg" id="seg-guide">
            <button data-mode="select" class="active">选择</button>
            <button data-mode="guide">画方向线</button>
          </div>
        </div>
        <div class="row"><label>强度</label>
          <input type="range" id="rng-gstrength" min="0.1" max="1" step="0.05" value="${state.guideStrength}" class="grow"/>
          <span class="val" id="val-gstrength">${state.guideStrength.toFixed(2)}</span>
        </div>
        <div class="zone-list" id="guide-list"></div>
        <div class="tip" style="margin-top:8px">沿"希望街道顺着走"的方向点几个点，双击结束。线两侧约 3 个街区宽的带内，街道会转向这条线；越远越回到全局方向。强度越大越能压过全局基准方向。</div>
        `
        )
      : section(
          '方向控制',
          `
        <div class="tip">方向线只在「张量场」布局下参与切块，当前是「${LAYOUT_LABEL[state.layout]}」。</div>
        <div class="row" style="margin-top:8px"><button class="ghost grow" id="btn-to-tensor">切到张量场</button></div>
        `
        )
    : '';

  // 用地规划：现代模式才有；图例由 renderLegend() 填
  const landSection = modern
    ? section(
        '用地规划',
        `
        <div class="legend" id="legend"></div>
        <div class="tip" style="margin-top:8px">同心分区：中心 CBD → 商业 → 住宅 → 外围工业/绿地，临主干道的街区优先商业。改种子或规模后重新分配。</div>
        `
      )
    : '';

  const roadSection = section(
    modern ? '街道' : '道路',
    `
    <div class="row"><label>${modern ? '主干道' : '主干道'}</label>
      <input type="range" id="rng-main" min="0.6" max="8" step="0.2" value="${state.style.mainStreetWidth}" class="grow"/>
      <span class="val" id="val-main">${state.style.mainStreetWidth.toFixed(1)}</span>
    </div>
    <div class="row"><label>${modern ? '次干道' : '街道'}</label>
      <input type="range" id="rng-regular" min="0.4" max="6" step="0.2" value="${state.style.regularStreetWidth}" class="grow"/>
      <span class="val" id="val-regular">${state.style.regularStreetWidth.toFixed(1)}</span>
    </div>
    <div class="row"><label>${modern ? '支路' : '巷道'}</label>
      <input type="range" id="rng-alley" min="0.2" max="4" step="0.1" value="${state.style.alleyWidth}" class="grow"/>
      <span class="val" id="val-alley">${state.style.alleyWidth.toFixed(1)}</span>
    </div>
    <div class="row" id="row-artery" style="opacity:.45">
      <label>选中路</label>
      <input type="range" id="rng-artery" min="1" max="6" step="0.1" value="2" class="grow" disabled/>
      <span class="val" id="val-artery">—</span>
      <button class="ghost" id="btn-artery-reset" title="恢复按道路等级默认宽度" disabled>↺</button>
    </div>
    <div class="row"><label class="check"><input type="checkbox" id="chk-verts" ${state.showVertices ? 'checked' : ''}/>显示路口</label></div>
    <div class="tip" style="margin-top:6px">${
      modern
        ? '红线宽按 1 单位 ≈ 7 m 折算：主干道 ' +
          u(ROAD_SPECS.arterial.redLine).toFixed(1) + ' ≈ ' + ROAD_SPECS.arterial.redLine +
          ' m（双向六车道），次干道 ' + u(ROAD_SPECS.collector.redLine).toFixed(1) + ' ≈ ' +
          ROAD_SPECS.collector.redLine + ' m（双向四车道），支路 ' +
          u(ROAD_SPECS.local.redLine).toFixed(1) + ' ≈ ' + ROAD_SPECS.local.redLine +
          ' m（双向两车道）。三级的里程比 ≈ 6 : 3 : 1。'
        : '开启路口后可直接拖动顶点改变路网走向；点击任意道路可单独调宽。'
    }</div>
    `
  );

  // 地块 / 建筑参数：只放"安全且有表现力"的那些。
  // 街道断面（红线 / 车道数 / 圆角）刻意不在这里 —— 它有硬耦合，见 params.ts 顶部注释。
  const paramSection = section(
    '地块参数',
    `
    <div class="row"><label>内部路网</label>
      <div class="seg" id="seg-inner">
        <button data-inner="tensor" class="${state.params.block.internalRoads === 'tensor' ? 'active' : ''}">张量主轴</button>
        <button data-inner="path" class="${state.params.block.internalRoads === 'path' ? 'active' : ''}">A* 寻路</button>
      </div>
    </div>
    <div class="row"><label class="check"><input type="checkbox" id="chk-parcel" ${state.params.block.parcelize ? 'checked' : ''}/>街区切成小地块（一街区多栋楼）</label></div>
    <div class="row"><label class="check"><input type="checkbox" id="chk-zone-pad" ${state.params.block.zonePad ? 'checked' : ''}/>禁建区只铺硬地（自建模型）</label></div>
    <div class="row"><label class="check"><input type="checkbox" id="chk-zone-avoid" ${state.params.block.zoneAvoid ? 'checked' : ''}/>路网避让禁建区（绕行 + 让距）</label></div>
    <div class="row"><label class="check"><input type="checkbox" id="chk-loop" ${state.params.block.serviceLoop ? 'checked' : ''}/>禁建区服务环路（区内）</label></div>
    ${BLOCK_SPECS.map((sp) => paramRow('block', sp)).join('')}
    <div class="tip" style="margin-top:6px"><b>「街区切成小地块」是这一版的主开关</b>：打开后每个街区按「小地块边长」做一次张量分割，每个地块单独排一栋楼，基底铺草坪、分割线铺窄混凝土巷 —— 一个街区 3~5 栋楼，绿地从 6% 涨到 30%。关掉就退回旧管线：一整个街区一次规划，基底是石材/广场砖，出来是「一大块硬质 + 一栋大楼」。<br/><b>街区尺度系数</b>（默认 1.5）是另一件事：调大 → 城市道路变稀、街区变大。街区一大，服务它的路就该退出城市路网、变成地块之间的巷道，这正是「切成小地块」在做的。</div>
    ${BUILDING_SPECS.map((sp) => paramRow('building', sp)).join('')}
    <div class="row" style="margin-top:8px"><button class="ghost grow" id="btn-param-reset">恢复默认参数</button></div>
    `
  );

  const zoneSection = section(
    '禁建区',
    `
    <div class="row"><label>工具</label>
      <div class="seg" id="seg-mode">
        <button data-mode="select" class="active">选择</button>
        <button data-mode="rect">矩形</button>
        <button data-mode="circle">圆</button>
        <button data-mode="poly">多边形</button>
      </div>
    </div>
    <div class="zone-list" id="zone-list"></div>
    <div class="tip" style="margin-top:8px">${
      modern
        ? '禁绘区是一个封闭地块：整块轮廓会被切出来单独成块，区内不排建筑、不做用途分档，但照常有地坪、内部路与绿化，内部路接入外围主路。'
        : '禁绘区内的建筑与铺装会被裁掉。'
    }</div>
    `
  );

  const exportSection = section(
    '显示 / 导出',
    `
    <div class="row"><label>楼高</label>
      <input type="range" id="rng-height" min="0.5" max="3" step="0.1" value="${state.heightScale}" class="grow"/>
      <span class="val" id="val-height">${state.heightScale.toFixed(1)}×</span>
    </div>
    <div class="row" style="gap:12px;flex-wrap:wrap">
      <label class="check"><input type="checkbox" id="chk-markings" ${state.markings ? 'checked' : ''}/>道路标线</label>
      <label class="check"><input type="checkbox" id="chk-furniture" ${state.furniture ? 'checked' : ''}/>行道树 / 路灯</label>
    </div>
    <div class="row"><label class="check"><input type="checkbox" id="chk-blocks" ${state.showBlocks ? 'checked' : ''}/>显示地块边界</label></div>
    <div class="row" style="margin-top:10px"><button class="ghost grow" id="btn-export2">导出 GLB</button></div>
    <div class="row" style="margin-top:6px"><button class="ghost grow" id="btn-share">复制场景链接</button></div>
    <div class="tip" style="margin-top:8px">「复制场景链接」把当前这一整座城（种子 / 规模 / 布局 / 所有参数 / 禁建区 / 方向线）编进地址栏。发给别人，对方打开看到的就是同一座城，逐像素对得上 —— 别再用截图对参数了。</div>
    <div class="tip" style="margin-top:8px">导出按层分组：ground / road/{路面,人行道,路口,标线} / lot/&lt;用途&gt; / street。铺装与道具按材质合并成一张大网格，建筑保持独立节点。</div>
    `
  );

  $('#sidebar').innerHTML =
    citySection + guideSection + landSection + roadSection + paramSection + zoneSection + exportSection;

  bindSidebar();
  renderZoneList();
  renderGuideList();
  renderLegend();
}

/** 用地图例：颜色块 + 数量，随模型刷新 */
function renderLegend(): void {
  const el = document.getElementById('legend');
  if (!el) return;
  if (!model) {
    el.innerHTML = '';
    return;
  }
  const counts = new Map<string, number>();
  for (const [, w] of model.lots) counts.set(w.use, (counts.get(w.use) ?? 0) + 1);

  const order: (keyof typeof USE_LABEL)[] = [
    'cbd',
    'commercial',
    'residential',
    'civic',
    'industrial',
    'park',
    'parking',
    'green',
  ];
  el.innerHTML = order
    .map((u) => {
      const [r, g, b] = useColor(u as LandUse);
      const n = counts.get(u) ?? 0;
      const dim = n === 0 ? 'opacity:.35' : 'opacity:1';
      return `<span class="lg-item" style="${dim}"><i style="background:rgb(${Math.round(r * 255)},${Math.round(
        g * 255
      )},${Math.round(b * 255)})"></i>${USE_LABEL[u]}<b>${n}</b></span>`;
    })
    .join('');
}

function renderZoneList(): void {
  const list = $('#zone-list');
  if (!list) return;
  if (state.zones.length === 0) {
    list.innerHTML = `<div class="empty">还没有禁建区。选矩形 / 圆 / 多边形后在图上拖拽或点击绘制。</div>`;
    return;
  }
  list.innerHTML = state.zones
    .map(
      (z, i) => `
      <div class="zone-item ${z.enabled ? 'on' : ''}" data-i="${i}">
        <input type="checkbox" class="chk-zone" data-i="${i}" ${z.enabled ? 'checked' : ''}/>
        <span class="swatch"></span>
        <span class="name">${z.label}</span>
        <button class="icon-btn del-zone" data-i="${i}" title="删除">✕</button>
      </div>`
    )
    .join('');

  list.querySelectorAll<HTMLInputElement>('.chk-zone').forEach((el) => {
    el.addEventListener('change', () => {
      const i = Number(el.dataset.i);
      const zones = state.zones.map((z, k) => (k === i ? { ...z, enabled: el.checked } : z));
      updateZones(zones);
    });
  });
  list.querySelectorAll<HTMLButtonElement>('.del-zone').forEach((el) => {
    el.addEventListener('click', () => {
      const i = Number(el.dataset.i);
      updateZones(state.zones.filter((_, k) => k !== i));
    });
  });
}

/** 方向控制线列表：启用开关 + 点数 + 删除 */
function renderGuideList(): void {
  const list = document.getElementById('guide-list');
  if (!list) return;
  if (state.guides.length === 0) {
    list.innerHTML = `<div class="empty">还没有方向线。点「画方向线」后在图上依次点击，双击结束。</div>`;
    return;
  }
  list.innerHTML = state.guides
    .map(
      (g, i) => `
      <div class="zone-item guide-item ${g.enabled ? 'on' : ''}" data-i="${i}">
        <input type="checkbox" class="chk-guide" data-i="${i}" ${g.enabled ? 'checked' : ''}/>
        <span class="swatch"></span>
        <span class="name">${g.label}</span>
        <span class="gpts">${g.pts.length} 点</span>
        <button class="icon-btn del-guide" data-i="${i}" title="删除">✕</button>
      </div>`
    )
    .join('');

  list.querySelectorAll<HTMLInputElement>('.chk-guide').forEach((el) => {
    el.addEventListener('change', () => {
      const i = Number(el.dataset.i);
      updateGuides(state.guides.map((g, k) => (k === i ? { ...g, enabled: el.checked } : g)));
    });
  });
  list.querySelectorAll<HTMLButtonElement>('.del-guide').forEach((el) => {
    el.addEventListener('click', () => {
      const i = Number(el.dataset.i);
      updateGuides(state.guides.filter((_, k) => k !== i));
    });
  });
}

function bindSidebar(): void {  // 规模
  $('#seg-size')
    .querySelectorAll<HTMLButtonElement>('button')
    .forEach((btn) => {
      btn.addEventListener('click', () => {
        state.nPatches = Number(btn.dataset.n);
        $('#seg-size').querySelectorAll('button').forEach((b) => b.classList.toggle('active', b === btn));
        regenerate();
      });
    });

  // 布局（只在现代城市下存在）
  const segLayout = document.querySelector<HTMLElement>('#seg-layout');
  if (segLayout) {
    segLayout.querySelectorAll<HTMLButtonElement>('button').forEach((btn) => {
      btn.addEventListener('click', () => {
        const k = btn.dataset.layout as LayoutKind;
        if (state.layout === k) return;
        state.layout = k;
        segLayout.querySelectorAll('button').forEach((b) => b.classList.toggle('active', b === btn));
        regenerate();
        buildSidebar();
      });
    });
  }

  // 内部路网形态（张量主轴直切 / 栅格 A* 寻路）
  const segInner = document.querySelector<HTMLElement>('#seg-inner');
  if (segInner) {
    segInner.querySelectorAll<HTMLButtonElement>('button').forEach((btn) => {
      btn.addEventListener('click', () => {
        const k = btn.dataset.inner as 'tensor' | 'path';
        if (state.params.block.internalRoads === k) return;
        state.params = resolveParams({ block: { ...state.params.block, internalRoads: k } });
        segInner.querySelectorAll('button').forEach((b) => b.classList.toggle('active', b === btn));
        regenerate();
      });
    });
  }

  // 禁建区身份：空场地（放自己的模型）/ 封闭街区（旧管线）
  // 街区切分：这一版的主开关
  const parcelChk = document.querySelector<HTMLInputElement>('#chk-parcel');
  parcelChk?.addEventListener('change', (e) => {
    const on = (e.target as HTMLInputElement).checked;
    state.params = resolveParams({ block: { ...state.params.block, parcelize: on } });
    regenerate();
  });

  const padChk = document.querySelector<HTMLInputElement>('#chk-zone-pad');
  padChk?.addEventListener('change', (e) => {
    const on = (e.target as HTMLInputElement).checked;
    state.params = resolveParams({ block: { ...state.params.block, zonePad: on } });
    regenerate();
  });

  // 禁区避障开关：开 = 路网绕着禁区走、留一圈让距；关 = 旧做法（街道与禁区平行、紧贴红线）
  const avoidChk = document.querySelector<HTMLInputElement>('#chk-zone-avoid');
  avoidChk?.addEventListener('change', (e) => {
    const on = (e.target as HTMLInputElement).checked;
    state.params = resolveParams({ block: { ...state.params.block, zoneAvoid: on } });
    regenerate();
  });

  // 地块内服务环路开关（关掉就只剩城市路网 + 张量主轴，用来对比密度）
  const loopChk = document.querySelector<HTMLInputElement>('#chk-loop');
  loopChk?.addEventListener('change', (e) => {
    const on = (e.target as HTMLInputElement).checked;
    state.params = resolveParams({ block: { ...state.params.block, serviceLoop: on } });
    regenerate();
  });

  // 地块 / 建筑参数：拖动只改标签，松手才重跑生成（一次生成 ~400 ms）
  const sidebar = document.querySelector<HTMLElement>('#sidebar');
  if (sidebar) {
    const apply = (el: HTMLInputElement) => {
      const g = el.dataset.pgroup as 'block' | 'building';
      const k = el.dataset.pkey as string;
      const v = Number(el.value);
      const next = JSON.parse(JSON.stringify(state.params)) as TownParams;
      (next[g] as unknown as Record<string, number>)[k] = v;
      state.params = resolveParams(next);
      const val = document.getElementById(`val-${el.id.replace(/^rng-/, '')}`);
      if (val) {
        const sp = (g === 'block' ? BLOCK_SPECS : BUILDING_SPECS).find((x) => x.key === k);
        const shown = (state.params[g] as unknown as Record<string, number>)[k];
        if (val) val.textContent = `${shown}${sp?.unit ?? ''}`;
      }
    };
    sidebar.addEventListener('input', (e) => {
      const el = e.target as HTMLInputElement;
      if (el.dataset.pgroup) apply(el);
    });
    sidebar.addEventListener('change', (e) => {
      const el = e.target as HTMLInputElement;
      if (el.dataset.pgroup) {
        apply(el);
        regenerate();
      }
    });
    // 参数可能被钳制（拖到边界外 / 区间颠倒），重生成后把滑杆值同步回真实值
    sidebar.addEventListener('pointerup', () => {
      for (const g of ['block', 'building'] as const) {
        const specs = g === 'block' ? BLOCK_SPECS : BUILDING_SPECS;
        for (const sp of specs) {
          const el = document.querySelector<HTMLInputElement>(`#rng-p-${g}-${sp.key}`);
          if (!el) continue;
          const v = (state.params[g] as unknown as Record<string, number>)[sp.key];
          if (Math.abs(Number(el.value) - v) > 1e-9) el.value = String(v);
        }
      }
    });
    document.querySelector<HTMLButtonElement>('#btn-param-reset')?.addEventListener('click', () => {
      state.params = defaultParams();
      buildSidebar();
      regenerate();
    });
  }

  // 停车配比（低 / 常规 / 高，只换倍率，不暴露具体数值）
  const segPark = document.querySelector<HTMLElement>('#seg-park');
  if (segPark) {
    segPark.querySelectorAll<HTMLButtonElement>('button').forEach((btn) => {
      btn.addEventListener('click', () => {
        const k = btn.dataset.park as ParkLevel;
        if (state.parkingLevel === k) return;
        state.parkingLevel = k;
        segPark.querySelectorAll('button').forEach((b) => b.classList.toggle('active', b === btn));
        regenerate();
      });
    });
  }

  // 种子
  const seedInput = $<HTMLInputElement>('#inp-seed');
  seedInput.addEventListener('change', () => {
    const v = Math.abs(parseInt(seedInput.value, 10) || 1) % 2147483647;
    state.seed = v || 1;
    seedInput.value = String(state.seed);
    regenerate();
  });
  $('#btn-dice').addEventListener('click', () => {
    state.seed = Math.floor(Math.random() * 2_000_000_000) + 1;
    seedInput.value = String(state.seed);
    regenerate();
  });

  // 要素开关
  const plazaChk = document.querySelector<HTMLInputElement>('#chk-plaza');
  plazaChk?.addEventListener('change', (e) => {
    state.plaza = (e.target as HTMLInputElement).checked;
    regenerate();
  });

  $('#btn-regen').addEventListener('click', regenerate);

  // 禁建区工具
  bindModeSeg('#seg-mode');
  // 方向线工具（两段控件共用同一个绘制模式，靠 setDrawMode 同步高亮）
  bindModeSeg('#seg-guide');

  // 方向线强度：作用于所有方向线
  const gstr = document.querySelector<HTMLInputElement>('#rng-gstrength');
  if (gstr) {
    gstr.addEventListener('input', () => {
      state.guideStrength = Number(gstr.value);
      const val = document.getElementById('val-gstrength');
      if (val) val.textContent = state.guideStrength.toFixed(2);
      view2d.guideStrength = state.guideStrength;
    });
    gstr.addEventListener('change', () => {
      if (state.guides.length === 0) return;
      updateGuides(state.guides.map((g) => ({ ...g, strength: state.guideStrength })));
    });
  }

  const toTensor = document.querySelector<HTMLButtonElement>('#btn-to-tensor');
  if (toTensor) {
    toTensor.addEventListener('click', () => {
      state.layout = 'tensor';
      regenerate();
      buildSidebar();
      toast('已切到张量场布局，可以画方向线了');
    });
  }

  // 标线 / 街道设施
  const layer = (id: string, key: 'markings' | 'furniture') => {
    $<HTMLInputElement>(id).addEventListener('change', (e) => {
      state[key] = (e.target as HTMLInputElement).checked;
      view2d.setSceneOptions({ [key]: state[key] });
      refresh3D();
    });
  };
  layer('#chk-markings', 'markings');
  layer('#chk-furniture', 'furniture');

  // 路宽
  const bindWidth = (
    id: string,
    valId: string,
    key: keyof TownStyle
  ) => {
    const input = $<HTMLInputElement>(id);
    input.addEventListener('input', () => {
      $(valId).textContent = Number(input.value).toFixed(1);
    });
    input.addEventListener('change', () => {
      state.style[key] = Number(input.value);
      model.style[key] = Number(input.value);
      model.rebuild();
      view2d.draw();
      updateStats();
      if (currentView === '3d') refresh3D();
    });
  };
  bindWidth('#rng-main', '#val-main', 'mainStreetWidth');
  bindWidth('#rng-regular', '#val-regular', 'regularStreetWidth');
  bindWidth('#rng-alley', '#val-alley', 'alleyWidth');

  // 选中某条路后单独调它的红线宽（其余路不受影响）
  // 只有这一处写 model.widthOverrides，地块内缩与道路横断面都读它，
  // 所以调完只需要 rebuild 一次，路面、人行道、街角圆角一起变。
  const artery = $<HTMLInputElement>('#rng-artery');
  const arteryRow = document.getElementById('row-artery');
  const arteryVal = document.getElementById('val-artery');
  const arteryReset = document.querySelector<HTMLButtonElement>('#btn-artery-reset');
  artery.addEventListener('input', () => {
    if (arteryVal) arteryVal.textContent = formatWidth(Number(artery.value));
  });
  artery.addEventListener('change', () => {
    const road = currentRoad;
    if (!road) return;
    model.setEdgeWidth(road.a, road.b, Number(artery.value));
    model.rebuild();
    view2d.invalidate();
    view2d.refreshRoadSelection();
    view2d.draw();
    updateStats();
    if (currentView === '3d') refresh3D();
  });
  arteryReset?.addEventListener('click', () => {
    const road = currentRoad;
    if (!road) return;
    model.setEdgeWidth(road.a, road.b, null);
    model.rebuild();
    view2d.invalidate();
    view2d.refreshRoadSelection();
    view2d.draw();
    updateStats();
    if (currentView === '3d') refresh3D();
  });

  // 显示
  $<HTMLInputElement>('#chk-verts').addEventListener('change', (e) => {
    view2d.showVertices = (e.target as HTMLInputElement).checked;
    view2d.draw();
  });
  $<HTMLInputElement>('#chk-blocks').addEventListener('change', (e) => {
    state.showBlocks = (e.target as HTMLInputElement).checked;
    view2d.showBlocks = state.showBlocks;
    view2d.draw();
  });

  const height = $<HTMLInputElement>('#rng-height');
  height.addEventListener('input', () => {
    state.heightScale = Number(height.value);
    $('#val-height').textContent = `${state.heightScale.toFixed(1)}×`;
  });
  height.addEventListener('change', () => {
    if (currentView === '3d') refresh3D();
  });

  $('#btn-export2').addEventListener('click', doExport);

  // 复制场景链接：优先走剪贴板；不给权限（http 非 localhost / 老浏览器）
  // 就把链接摊在 toast 里让用户自己复制 —— 总比"点了没反应"强。
  document.querySelector<HTMLButtonElement>('#btn-share')?.addEventListener('click', () => {
    const url = sceneUrl();
    try {
      history.replaceState(null, '', url);
    } catch {
      /* ignore */
    }
    const done = () => toast(`场景链接已复制（${url.length} 字符）`);
    const fail = () => toast(url, false);
    if (navigator.clipboard?.writeText) navigator.clipboard.writeText(url).then(done, fail);
    else fail();
  });

  // 侧栏重建后元素是新的，把当前选中路的状态回填一次
  syncRoadRow(currentRoad);
}

function setHint(mode: DrawMode): void {
  const hints: Record<DrawMode, string> = {
    select: '点选禁绘区 / 方向线后用浮动工具条移动·旋转·缩放·改形状 · 勾选「显示路口」可拖拓扑点 · 滚轮缩放 · 右键平移',
    rect: '按住左键拖出矩形禁区 · Esc 取消',
    circle: '按住左键，从圆心拖出半径 · Esc 取消',
    poly: '依次点击顶点，双击闭合 · Esc 取消',
    guide: '沿想控制的走向依次点击，双击结束 · 方向线只影响线两侧一条带',
  };
  $('#hint').textContent = hints[mode];
}

/**
 * 切换绘制模式并同步两个分段控件的高亮。
 * 侧栏有两组工具（禁建区 / 方向线），各自都带一个「选择」，
 * 统一从这里改，避免出现"两个都亮着"的错位。
 */
function setDrawMode(mode: DrawMode): void {
  // 高亮与提示由 onModeChanged 回填，这里只管切模式
  view2d.setMode(mode);
}

/** 只改高亮和底部提示，不动绘制模式（由 View2D 的 onModeChanged 驱动，避免回环） */
function syncModeButtons(mode: DrawMode): void {
  document.querySelectorAll<HTMLButtonElement>('#seg-mode button, #seg-guide button').forEach((b) => {
    b.classList.toggle('active', b.dataset.mode === mode);
  });
  setHint(mode);
}

function bindModeSeg(sel: string): void {
  const seg = document.querySelector<HTMLElement>(sel);
  if (!seg) return;
  seg.querySelectorAll<HTMLButtonElement>('button').forEach((btn) => {
    btn.addEventListener('click', () => setDrawMode(btn.dataset.mode as DrawMode));
  });
}

// ---------------------------------------------------------------- 导出

function doExport(): void {
  try {
    const prims = buildTownMeshes(model, {
      heightScale: state.heightScale,
      markings: state.markings,
      furniture: state.furniture,
    });
    const glb = exportGLB(prims, 'TownForge');
    const blob = new Blob([glb.bytes as unknown as BlobPart], { type: 'model/gltf-binary' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = `town_${state.nPatches}_${state.seed}.glb`;
    a.click();
    setTimeout(() => URL.revokeObjectURL(url), 2000);
    toast(`已导出 ${(glb.bytes.byteLength / 1024 / 1024).toFixed(2)} MB · ${glb.triangles} 三角形 · ${glb.meshes} 网格`);
  } catch (e) {
    toast(`导出失败：${(e as Error).message}`, true);
  }
}

let toastTimer = 0;
function toast(text: string, isErr = false): void {
  let el = document.getElementById('toast');
  if (!el) {
    el = document.createElement('div');
    el.id = 'toast';
    document.body.appendChild(el);
  }
  el.textContent = text;
  el.className = isErr ? 'on err' : 'on';
  clearTimeout(toastTimer);
  toastTimer = window.setTimeout(() => {
    el!.className = isErr ? 'err' : '';
  }, 2600);
}

// ---------------------------------------------------------------- 视图切换

function switchView(view: '2d' | '3d'): void {
  currentView = view;
  document.querySelectorAll<HTMLButtonElement>('.tab').forEach((b) => b.classList.toggle('active', b.dataset.view === view));

  const canvas = $('#canvas2d');
  const mount = $('#view3d');

  if (view === '3d') {
    canvas.style.display = 'none';
    mount.classList.add('on');
    view2d.setActive(false);
    if (!view3d) view3d = new View3D(mount);
    refresh3D();
  } else {
    mount.classList.remove('on');
    canvas.style.display = 'block';
    view2d.setActive(true);
    view2d.draw();
  }
}

// ---------------------------------------------------------------- 场景快照
//
// 编解码在 ui/snapshot.ts 里（不碰 DOM，能在 node 里跑回归），这里只做接线：
//   - 启动时读 `#s=…`，先还原状态再生成；没有链接就看上次留下的自动存档
//   - 每次重生成后把当前场景写回地址栏（replaceState，不进历史栈）并存一份
//   - 面板里的「复制场景链接」直接把地址栏那条复制走
//
// 自动存档（localStorage）是保命的：手画的方向线和禁区只活在内存里，
// 一刷新就没了 —— 调参调半天、刷新一下回到默认，这种损失比 bug 更让人恼火。

const LS_KEY = 'townforge.scene.v1';

/** 地址栏里的快照编码（`#s=…`）；没有就返回 null */
function hashSnapshot(): string | null {
  const h = location.hash.replace(/^#/, '');
  return h.startsWith('s=') ? h.slice(2) : null;
}

function sceneUrl(): string {
  return `${location.origin}${location.pathname}#s=${encodeSnapshot(state)}`;
}

let hashSyncTimer = 0;
/** 重生成之后把当前场景写回地址栏 + 本地存档（节流：拖方向线会连续触发） */
function syncHashSoon(): void {
  window.clearTimeout(hashSyncTimer);
  hashSyncTimer = window.setTimeout(() => {
    const code = encodeSnapshot(state);
    try {
      history.replaceState(null, '', `${location.origin}${location.pathname}#s=${code}`);
    } catch {
      /* file:// 下 replaceState 会抛，忽略 */
    }
    try {
      localStorage.setItem(LS_KEY, code);
    } catch {
      /* 隐私模式 / 配额满，忽略 */
    }
  }, 350);
}

// ---------------------------------------------------------------- 启动

/**
 * 还原场景状态的入口，优先级：`#s=` 快照 > `?seed=` 这类零散参数 > 本地自动存档。
 * 返回 true 表示状态已被完整还原，调用方不要再让问号参数覆盖。
 */
function applyUrlParams(): boolean {
  // 快照优先：`#s=…` 里的就是"完整场景"，比 ?seed= 这类零散参数权威。
  const snap = hashSnapshot();
  if (snap) {
    const decoded = decodeSnapshot(snap);
    if (decoded && applySnapshot(state, decoded)) return true;
    toast('场景链接解析失败，已退回默认场景', true);
  }

  // 有问号参数时以问号参数为准（截图 / 自测脚本都靠它），
  // 这时候不能拿自动存档去覆盖 —— 否则 ?seed=123 永远看不到 123 那座城。
  if (location.search.length > 1) return false;

  // 两者都没有：接上次的自动存档。手画的方向线 / 禁区不该因为刷新就蒸发。
  try {
    const saved = localStorage.getItem(LS_KEY);
    const decoded = saved ? decodeSnapshot(saved) : null;
    if (decoded && applySnapshot(state, decoded)) return true;
  } catch {
    /* 隐私模式读不到，忽略 */
  }
  return false;
}

/** 问号参数（?seed= / ?n= / ?layout= …）——原本的 applyUrlParams 主体 */
function applyQueryParams(): void {
  const p = new URLSearchParams(location.search);
  const num = (k: string) => (p.has(k) ? Number(p.get(k)) : null);

  const n = num('n');
  if (n) state.nPatches = n;
  const seed = num('seed');
  if (seed) state.seed = seed;

  if (p.get('zone') === '1') {
    state.zones = [
      makeZone('demo-rect', '矩形禁区 1', {
        kind: 'rect',
        a: new Pt(-52, -18),
        b: new Pt(-14, 26),
      }),
      makeZone('demo-circle', '圆形禁区 2', { kind: 'circle', c: new Pt(38, 34), r: 22 }),
    ];
  }
  if (p.get('verts') === '1') state.showVertices = true;
  // ?guides=1 放两条示例方向线：一条斜穿西部，一条让东南角转个向
  if (p.get('guides') === '1') {
    state.guides = [
      makeGuide('demo-a', '方向线 1', [new Pt(-66, -34), new Pt(-20, 6), new Pt(4, 40)]),
      makeGuide('demo-b', '方向线 2', [new Pt(30, -46), new Pt(58, 6)]),
    ];
  }
  // ?layout=grid|field|radial|tensor
  const lay = p.get('layout');
  if (lay === 'grid' || lay === 'field' || lay === 'radial' || lay === 'tensor') state.layout = lay;
  if (p.get('markings') === '0') state.markings = false;
  if (p.get('furniture') === '0') state.furniture = false;
}

function boot(): void {
  if (!applyUrlParams()) applyQueryParams();

  const canvas = $<HTMLCanvasElement>('#canvas2d');

  // 先建模型再建侧栏：侧栏里的用地图例要读 model.lots，
  // 顺序反了会在首屏抛 TypeError，整页停在那里（画布全空）。
  try {
    model = createModel();
  } catch (e) {
    toast(`初始生成失败：${(e as Error).message}`, true);
    model = createModel();
  }

  buildSidebar();
  setHint('select');

  view2d = new View2D(canvas, model, {
    onRebuild: rebuildGeometry,
    onZonesChanged: updateZones,
    onGuidesChanged: (guides) => updateGuides(guides),
    onSelection: (p) => syncRoadRow(p.road),
    onStatus: (text) => {
      $('#hint').textContent = text;
    },
    onModeChanged: (mode) => syncModeButtons(mode),
  });
  view2d.setZones(state.zones);
  view2d.setGuides(state.guides);
  view2d.guideStrength = state.guideStrength;
  view2d.showVertices = state.showVertices;
  view2d.setSceneOptions({ markings: state.markings, furniture: state.furniture });
  view2d.showBlocks = state.showBlocks;
  const chkVerts = document.querySelector<HTMLInputElement>('#chk-verts');
  if (chkVerts) chkVerts.checked = state.showVertices;
  view2d.draw();
  updateStats();
  // 首屏就把当前场景写进地址栏：这样一来"地址栏里那条"永远是用户眼前这座城
  syncHashSoon();

  // ?sel=1 自动选中第一个禁绘区（没有禁区就选第一条方向线），方便截图 / 演示浮动工具条
  const q = new URLSearchParams(location.search);
  if (q.get('sel') === '1' || q.get('selftest') === '1') {
    const pick: { kind: SelKind; id: string } | null =
      state.zones.length > 0
        ? { kind: 'zone', id: state.zones[0].id }
        : state.guides.length > 0
          ? { kind: 'guide', id: state.guides[0].id }
          : null;
    if (pick) {
      view2d.selectById(pick.kind, pick.id);
      view2d.draw();
    }
  }
  // ?selftest=1 跑一遍拖拽交互自测（动态载入，正常使用不会打到这个包）
  if (q.get('selftest') === '1') {
    void import('./selftest').then((m) =>
      m.runSelfTest({ view: view2d, canvas, zones: () => state.zones, guides: () => state.guides })
    );
  }

  document.querySelectorAll<HTMLButtonElement>('.tab').forEach((btn) => {
    btn.addEventListener('click', () => switchView(btn.dataset.view as '2d' | '3d'));
  });
  $('#btn-export').addEventListener('click', doExport);

  document.addEventListener('keydown', (e) => {
    if (e.key === 'Escape') {
      setDrawMode('select');
    }
    if (e.key === 'v' && (e.ctrlKey || e.metaKey) === false) {
      const chk = $<HTMLInputElement>('#chk-verts');
      chk.checked = !chk.checked;
      chk.dispatchEvent(new Event('change'));
    }
  });

  if (new URLSearchParams(location.search).get('view') === '3d') switchView('3d');

  // 首屏提示
  toast('拖拽顶点改路网，画禁区限制建造，右上角导出 GLB');
}

boot();
