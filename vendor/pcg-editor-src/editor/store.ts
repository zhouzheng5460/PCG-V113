import { create } from 'zustand';
import { booleanOp, offsetPath, unionAll } from '../core/boolean';
import { addTo, demoProject, emptyProject, makeBuilding, makeGround, makeRoad, uid } from '../core/factory';
import {
  AreaObj,
  BackgroundImage,
  LayerKey,
  Id,
  LAYER_OF,
  ParcelObj,
  ProjectModel,
  SceneObject,
  areaPaths,
  isAreaObj,
} from '../core/model';
import { Bounds, boundsOfPaths, unionBounds } from '../core/polygon';
import { translateRing } from '../core/polygon';
import { BUILDING_NOUN, PcgStats, generateForParcel } from '../core/pcg';
import { EditorViewState, SelectionFilter, ToolId, DrawKind, defaultViewState } from './viewstate';
import { calibrate, calibrationWarning, fitBackgroundToView, backgroundBounds } from '../core/background';
import { Vec2 } from '../core/vec';
import { camera } from './camera';

export interface HistoryEntry {
  label: string;
  model: ProjectModel;
}

export interface Hint {
  x: number;
  y: number;
  snapLabel: string;
  length?: number;
  angle?: number;
  area?: number;
  message?: string;
  warning?: boolean;
}

const EMPTY_HINT: Hint = { x: 0, y: 0, snapLabel: '' };

export interface CalibState {
  a?: Vec2;
  b?: Vec2;
}

export interface EditorStore {
  model: ProjectModel;
  view: EditorViewState;
  past: HistoryEntry[];
  future: HistoryEntry[];
  hint: Hint;
  /** 最近一次操作提示 */
  toast?: string;
  /** 非空 = 正在两点标定 */
  calib: CalibState | null;
  /**
   * 每个地块最近一次生成的统计（覆盖率 / FAR / 栋数）。
   * 放在 store 而不是 Inspector 里现算：生成一次几毫秒，但面板每帧渲染都算就白烧 CPU。
   * 地块被删/项目重置时顺手丢掉对应项。
   */
  genStats: Record<Id, PcgStats>;

  setModel: (m: ProjectModel) => void;
  pushHistory: (label: string) => void;
  undo: () => void;
  redo: () => void;

  select: (ids: Id[], additive?: boolean) => void;
  hover: (id?: Id) => void;
  setTool: (t: ToolId) => void;
  setDrawKind: (k: DrawKind) => void;
  setFilter: (f: SelectionFilter) => void;

  updateObject: (id: Id, patch: Partial<SceneObject>) => void;
  deleteSelected: () => void;
  duplicateSelected: () => void;
  applyBoolean: (op: 'union' | 'subtract' | 'intersect') => void;
  applyOffset: (delta: number) => void;
  toggleLock: (id?: Id) => void;
  promoteToFixed: (id?: Id) => void;

  /** Phase 5：单地块 PCG。reseed=true 时先换种子再生成（"换一版"） */
  generateParcel: (parcelId?: Id, reseed?: boolean) => void;
  /** 清掉某地块的所有生成物（保留用户手绘） */
  clearGenerated: (parcelId?: Id) => void;

  patchSettings: (p: Partial<EditorViewState>) => void;
  toggleLayerVisible: (key: LayerKey) => void;
  toggleLayerLocked: (key: LayerKey) => void;

  setHint: (h: Partial<Hint>) => void;
  showToast: (t: string) => void;

  setBackground: (src: string, widthPx: number, heightPx: number) => void;
  updateBackground: (patch: Partial<BackgroundImage>, label?: string, silent?: boolean) => void;
  removeBackground: () => void;
  startCalibration: () => void;
  cancelCalibration: () => void;
  setCalibPoint: (p: Vec2) => void;
  applyCalibration: (realMeters: number) => void;

  newProject: () => void;
  loadDemo: () => void;
}

const LIMIT = 120;

export const useEditor = create<EditorStore>((set, get) => ({
  model: demoProject(),
  view: defaultViewState(),
  past: [],
  future: [],
  hint: EMPTY_HINT,
  calib: null,
  genStats: {},

  setModel: (m) => set({ model: m }),

  pushHistory: (label) =>
    set((s) => ({
      past: [...s.past, { label, model: s.model }].slice(-LIMIT),
      future: [],
    })),

  undo: () =>
    set((s) => {
      const prev = s.past[s.past.length - 1];
      if (!prev) return s;
      return {
        past: s.past.slice(0, -1),
        future: [...s.future, { label: prev.label, model: s.model }],
        model: prev.model,
        view: {
          ...s.view,
          selectionIds: s.view.selectionIds.filter((id) => !!prev.model.objects[id]),
        },
      };
    }),

  redo: () =>
    set((s) => {
      const next = s.future[s.future.length - 1];
      if (!next) return s;
      return {
        future: s.future.slice(0, -1),
        past: [...s.past, { label: next.label, model: s.model }],
        model: next.model,
        view: {
          ...s.view,
          selectionIds: s.view.selectionIds.filter((id) => !!next.model.objects[id]),
        },
      };
    }),

  select: (ids, additive = false) =>
    set((s) => {
      const next = additive
        ? mergeIds(s.view.selectionIds, ids)
        : ids;
      return { view: { ...s.view, selectionIds: next } };
    }),

  hover: (id) => set((s) => (s.view.hoverId === id ? s : { view: { ...s.view, hoverId: id } })),

  setTool: (t) =>
    set((s) => ({
      view: { ...s.view, activeTool: t },
      hint: { ...s.hint, message: undefined, warning: false },
    })),

  setDrawKind: (k) => set((s) => ({ view: { ...s.view, drawKind: k } })),
  setFilter: (f) => set((s) => ({ view: { ...s.view, selectionFilter: f } })),

  updateObject: (id, patch) =>
    set((s) => {
      const obj = s.model.objects[id];
      if (!obj) return s;
      return { model: { ...s.model, objects: { ...s.model.objects, [id]: { ...obj, ...patch } as SceneObject } } };
    }),

  deleteSelected: () => {
    const { view, model } = get();
    const ids = view.selectionIds.filter((id) => {
      const o = model.objects[id];
      if (!o) return false;
      const layer = LAYER_OF[o.kind];
      return !o.locked && !view.layerLocked[layer];
    });
    if (!ids.length) return;
    get().pushHistory('删除对象');
    // 删地块要连坐：它生成的建筑失去归属后既不能重算也不能被"清除生成"，
    // 只会变成一堆没人管的固定楼。
    const doomed = new Set(ids);
    for (const id of model.order) {
      const o = model.objects[id];
      if (o && o.generatedFrom && doomed.has(o.generatedFrom)) doomed.add(id);
    }
    const objects = { ...model.objects };
    for (const id of doomed) delete objects[id];
    const genStats = { ...get().genStats };
    for (const id of doomed) delete genStats[id];
    set((s) => ({
      model: { ...s.model, objects, order: s.model.order.filter((i) => !doomed.has(i)) },
      view: { ...s.view, selectionIds: [] },
      genStats,
    }));
  },

  duplicateSelected: () => {
    const { view, model } = get();
    const ids = view.selectionIds.filter((id) => model.objects[id]);
    if (!ids.length) return;
    get().pushHistory('复制对象');
    let m = model;
    const created: Id[] = [];
    for (const id of ids) {
      const src = m.objects[id];
      if (!src) continue;
      const clone = structuredClone(src) as SceneObject;
      clone.id = uid(src.kind);
      clone.name = `${src.name} 副本`;
      clone.origin = 'user';
      const d = { x: 2, y: -2 };
      if (isAreaObj(clone)) {
        const p = areaPaths(clone);
        if (p) {
          const moved = p.map((r) => translateRing(r, d));
          if (clone.kind === 'building') clone.footprint = moved;
          else clone.boundary = moved;
        }
      }
      if (clone.kind === 'road') clone.centerline = clone.centerline.map((p) => ({ x: p.x + d.x, y: p.y + d.y }));
      if (clone.kind === 'access') clone.pos = { x: clone.pos.x + d.x, y: clone.pos.y + d.y };
      m = addTo(m, clone);
      created.push(clone.id);
    }
    set((s) => ({ model: m, view: { ...s.view, selectionIds: created } }));
  },

  applyBoolean: (op) => {
    const { view, model } = get();
    const sel = view.selectionIds
      .map((id) => model.objects[id])
      .filter((o): o is AreaObj => !!o && isAreaObj(o));
    if (sel.length < 2) {
      get().showToast('布尔运算需要至少选择 2 个面状对象');
      return;
    }
    const subject = sel[0];
    const subjectPaths = areaPaths(subject) ?? [];
    const restPaths = sel.slice(1).map((o) => areaPaths(o) ?? []);

    let result;
    if (op === 'union') result = unionAll([subjectPaths, ...restPaths]);
    else if (op === 'subtract') result = booleanOp('subtract', subjectPaths, unionAll(restPaths));
    else result = booleanOp('intersect', subjectPaths, unionAll(restPaths));

    if (!result.length) {
      get().showToast('布尔结果为空（几何未相交）');
      return;
    }

    get().pushHistory(op === 'union' ? '合并' : op === 'subtract' ? '减去' : '取交集');
    const objects = { ...model.objects };
    const updated = { ...subject } as AreaObj;
    if (updated.kind === 'building') (updated as any).footprint = result;
    else (updated as any).boundary = result;
    objects[updated.id] = updated;

    // 语义：Union / Intersect 的参与对象被合并进主对象；
    // Subtract 的裁剪对象只是「工具」，必须保留（例如 大地块 − 建筑 = 庭院，建筑不能消失）。
    const removed = op === 'subtract' ? [] : sel.slice(1).map((o) => o.id);
    for (const id of removed) delete objects[id];

    set((s) => ({
      model: {
        ...s.model,
        objects,
        order: s.model.order.filter((i) => !removed.includes(i)),
      },
      view: { ...s.view, selectionIds: [updated.id] },
    }));
    get().showToast('布尔完成，可 Ctrl+Z 撤销');
  },

  applyOffset: (delta) => {
    const { view, model } = get();
    const sel = view.selectionIds
      .map((id) => model.objects[id])
      .filter((o): o is AreaObj => !!o && isAreaObj(o));
    if (sel.length !== 1) {
      get().showToast('偏移需要且仅需要 1 个面状对象');
      return;
    }
    const src = sel[0];
    const paths = areaPaths(src) ?? [];
    const out = offsetPath(paths, delta);
    if (!out.length) {
      get().showToast(`向内偏移 ${Math.abs(delta)}m 后几何为空`);
      return;
    }
    get().pushHistory(`偏移 ${delta}m`);
    const objects = { ...model.objects };
    const updated = { ...src } as AreaObj;
    if (updated.kind === 'building') (updated as any).footprint = out;
    else (updated as any).boundary = out;
    objects[updated.id] = updated;
    set((s) => ({ model: { ...s.model, objects } }));
    get().showToast(`已偏移 ${delta > 0 ? '外扩' : '内缩'} ${Math.abs(delta)}m`);
  },

  toggleLock: (id) => {
    const ids = id ? [id] : get().view.selectionIds;
    if (!ids.length) return;
    get().pushHistory('锁定切换');
    set((s) => {
      const objects = { ...s.model.objects };
      for (const i of ids) {
        const o = objects[i];
        if (o) objects[i] = { ...o, locked: !o.locked };
      }
      return { model: { ...s.model, objects } };
    });
  },

  promoteToFixed: (id) => {
    const ids = id ? [id] : get().view.selectionIds;
    if (!ids.length) return;
    get().pushHistory('转为固定');
    set((s) => {
      const objects = { ...s.model.objects };
      for (const i of ids) {
        const o = objects[i];
        if (o) objects[i] = { ...o, fixed: true, origin: 'user' };
      }
      return { model: { ...s.model, objects } };
    });
    get().showToast('已转为固定对象，后续生成不会再改动它');
  },

  generateParcel: (parcelId, reseed) => {
    const st = get();
    const parcels = st.model.order
      .map((id) => st.model.objects[id])
      .filter((o): o is ParcelObj => !!o && o.kind === 'parcel');

    const picked = parcelId ? st.model.objects[parcelId] : undefined;
    const selected = st.view.selectionIds
      .map((id) => st.model.objects[id])
      .find((o) => o?.kind === 'parcel');
    const target = (picked?.kind === 'parcel' ? picked : selected?.kind === 'parcel' ? selected : parcels.length === 1 ? parcels[0] : undefined) as
      | ParcelObj
      | undefined;

    if (!target) {
      st.showToast(parcels.length > 1 ? '有多个地块，请先选中要生成的那一个' : '先画一个地块再生成');
      return;
    }

    const parcel: ParcelObj = reseed ? { ...target, seed: target.seed + 1 } : target;
    const res = generateForParcel(st.model, parcel);
    if (!res.buildings.length) {
      st.showToast(res.message ?? '没有可生成的位置');
      return;
    }

    // 局部重算（PRD §70）：只清掉"由这个地块生成"的旧对象，手绘与固定对象一律不动。
    // 注意这里不再只清 building —— 道路与地面同样是生成物，漏清就会越堆越多。
    const keepIds = st.model.order.filter((id) => {
      const o = st.model.objects[id];
      return !(o && o.generatedFrom === target.id);
    });
    const objects: Record<Id, SceneObject> = {};
    for (const id of keepIds) {
      const o = st.model.objects[id];
      if (o) objects[id] = o;
    }
    if (parcel.seed !== target.seed) objects[parcel.id] = parcel;

    let next: ProjectModel = { ...st.model, objects, order: keepIds };

    /*
     * 落地顺序 = 视图的叠放顺序：地面 → 道路 → 建筑。
     * 2D 命中测试取"最后画在上面的那个"，建筑必须排在最后才能被优先选中；
     * 3D 靠 elevation 分层，与加入顺序无关，但保持同一顺序便于排查。
     */
    const groundObjs: SceneObject[] = res.grounds.map((g) => ({
      ...makeGround(g.boundary, g.surface),
      name: g.name,
      origin: 'generated',
      generatedFrom: parcel.id,
    }));
    const roadObjs: SceneObject[] = res.roads.map((r, i) => ({
      ...makeRoad(r.centerline, { roadClass: r.roadClass, width: r.width, name: r.name }),
      name: r.name === '内部主路' ? '内部主路' : `支路 ${i}`,
      origin: 'generated',
      generatedFrom: parcel.id,
    }));
    const buildingObjs: SceneObject[] = res.buildings.map((b, i) => ({
      ...makeBuilding(b.footprint, {
        height: b.height,
        floors: b.floors,
        archetype: b.archetype,
        origin: 'generated',
        name: `${BUILDING_NOUN[parcel.landUse]} ${i + 1}`,
      }),
      generatedFrom: parcel.id,
    }));
    const added: SceneObject[] = [...groundObjs, ...roadObjs, ...buildingObjs];
    for (const o of added) next = addTo(next, o);

    st.pushHistory(`生成地块「${parcel.name}」`);
    set({
      model: next,
      view: { ...st.view, selectionIds: [parcel.id], hoverId: undefined },
      genStats: { ...st.genStats, [parcel.id]: res.stats },
    });
    const bits = [`${res.buildings.length} 栋`];
    if (res.roads.length) bits.push(`${res.roads.length} 条路`);
    if (res.grounds.length) bits.push(`${res.grounds.length} 块地面`);
    bits.push(`覆盖率 ${(res.stats.coverage * 100).toFixed(0)}%`);
    bits.push(`FAR ${res.stats.far.toFixed(2)}`);
    if (res.stats.parkingSpaces > 0) bits.push(`${res.stats.parkingSpaces} 车位`);
    st.showToast(`生成 ${bits.join(' · ')}`);
  },

  clearGenerated: (parcelId) => {
    const st = get();
    const targets = parcelId
      ? [parcelId]
      : st.view.selectionIds.filter((id) => st.model.objects[id]?.kind === 'parcel');
    if (!targets.length) {
      st.showToast('请先选中一个地块');
      return;
    }
    const owner = new Set(targets);
    const keepIds = st.model.order.filter((id) => {
      const o = st.model.objects[id];
      return !(o && o.kind === 'building' && o.generatedFrom && owner.has(o.generatedFrom));
    });
    if (keepIds.length === st.model.order.length) {
      st.showToast('这个地块还没有生成物');
      return;
    }
    const objects: Record<Id, SceneObject> = {};
    for (const id of keepIds) {
      const o = st.model.objects[id];
      if (o) objects[id] = o;
    }
    st.pushHistory('清除生成建筑');
    const genStats = { ...st.genStats };
    for (const id of targets) delete genStats[id];
    set({ model: { ...st.model, objects, order: keepIds }, genStats });
    st.showToast(`已清除 ${st.model.order.length - keepIds.length} 栋生成建筑`);
  },

  patchSettings: (p) => set((s) => ({ view: { ...s.view, ...p } })),

  toggleLayerVisible: (key: LayerKey) =>
    set((s) => ({
      view: {
        ...s.view,
        layerVisibility: { ...s.view.layerVisibility, [key]: !s.view.layerVisibility[key] },
      },
    })),

  toggleLayerLocked: (key: LayerKey) =>
    set((s) => ({
      view: {
        ...s.view,
        layerLocked: { ...s.view.layerLocked, [key]: !s.view.layerLocked[key] },
      },
    })),

  setHint: (h) => set((s) => ({ hint: { ...s.hint, ...h } })),
  showToast: (t) => set({ toast: t }),

  setBackground: (src, widthPx, heightPx) => {
    const bounds = camera.visibleBounds();
    const bg = fitBackgroundToView(widthPx, heightPx, camera.center, bounds.maxX - bounds.minX);
    bg.src = src;
    get().pushHistory('导入背景图');
    set((s) => ({
      model: { ...s.model, background: bg },
      calib: {}, // §18 导入后直接进入标定模式
      view: { ...s.view, bgEditing: true },
    }));
    get().showToast('背景图已导入 · 请在图上点两点并输入实际距离完成标定');
  },

  updateBackground: (patch, label, silent) => {
    if (!silent) get().pushHistory(label ?? '调整背景图');
    set((s) =>
      s.model.background
        ? { model: { ...s.model, background: { ...s.model.background, ...patch } } }
        : s,
    );
  },

  removeBackground: () => {
    if (!get().model.background) return;
    get().pushHistory('删除背景图');
    set((s) => ({ model: { ...s.model, background: null }, calib: null, view: { ...s.view, bgEditing: false } }));
  },

  startCalibration: () => {
    if (!get().model.background) {
      get().showToast('先导入背景图');
      return;
    }
    get().updateBackground({ locked: false }, '解锁标定');
    set({ calib: {}, hint: { ...get().hint, message: '标定模式：在背景图上点击两点（Esc 取消）', warning: false } });
  },

  cancelCalibration: () =>
    set((s) => ({ calib: null, hint: { ...s.hint, message: undefined, warning: false } })),

  setCalibPoint: (p) => {
    const c = get().calib;
    if (!c) return;
    if (!c.a) set({ calib: { a: p } });
    else if (!c.b) set({ calib: { a: c.a, b: p } });
    else set({ calib: { a: p } });
  },

  applyCalibration: (realMeters) => {
    const { calib, model } = get();
    const bg = model.background;
    if (!calib?.a || !calib?.b || !bg) {
      get().showToast('请先在图上点两个点');
      return;
    }
    const res = calibrate(bg, calib.a, calib.b, realMeters);
    if (!res) {
      get().showToast('标定失败：两点重合或距离无效');
      return;
    }
    get().pushHistory('比例标定');
    set((s) => ({
      model: {
        ...s.model,
        background: bg
          ? {
              ...bg,
              origin: res.origin,
              metersPerPixel: res.metersPerPixel,
              locked: true, // §21 标定完成后锁定
            }
          : null,
      },
      calib: null,
      view: { ...s.view, bgEditing: false },
      hint: { ...s.hint, message: undefined, warning: false },
    }));
    const warn = calibrationWarning(realMeters, res.pixelDistance);
    get().showToast(
      warn
        ? `标定完成（有疑问）：${warn}`
        : `标定完成：1px = ${(res.metersPerPixel * 100).toFixed(2)} cm，背景已锁定`,
    );
  },

  newProject: () =>
    set({
      model: emptyProject(),
      past: [],
      future: [],
      calib: null,
      genStats: {},
      view: { ...defaultViewState(), activeTool: 'select' },
    }),

  loadDemo: () =>
    set({
      model: demoProject(),
      past: [],
      future: [],
      calib: null,
      genStats: {},
      view: defaultViewState(),
    }),
}));

function mergeIds(cur: Id[], add: Id[]): Id[] {
  const out = cur.slice();
  for (const id of add) {
    const i = out.indexOf(id);
    if (i >= 0) out.splice(i, 1);
    else out.push(id);
  }
  return out;
}

/** 当前可见对象（受层可见性 + 选择过滤器影响，供命中测试使用） */
export function pickableObjects(model: ProjectModel, view: EditorViewState): SceneObject[] {
  return model.order
    .map((id) => model.objects[id])
    .filter((o): o is SceneObject => !!o)
    .filter((o) => o.visible && view.layerVisibility[LAYER_OF[o.kind]])
    .filter((o) => {
      switch (view.selectionFilter) {
        case 'parcels':
          return o.kind === 'parcel';
        case 'buildings':
          return o.kind === 'building';
        case 'roads':
          return o.kind === 'road';
        case 'ground':
          return o.kind === 'ground' || o.kind === 'nobuild';
        case 'generated':
          return o.origin === 'generated';
        default:
          return true;
      }
    });
}

export function backgroundWorldBounds(model: ProjectModel) {
  return model.background ? backgroundBounds(model.background) : null;
}

export function selectionBounds(model: ProjectModel, ids: Id[]): Bounds | null {
  let b: Bounds | null = null;
  for (const id of ids) {
    const o = model.objects[id];
    if (!o) continue;
    const paths = areaPaths(o);
    if (paths) b = unionBounds(b, boundsOfPaths(paths));
    else if (o.kind === 'road') b = unionBounds(b, boundsOfPaths([o.centerline]));
    else if (o.kind === 'access')
      b = unionBounds(b, {
        minX: o.pos.x - 1,
        minY: o.pos.y - 1,
        maxX: o.pos.x + 1,
        maxY: o.pos.y + 1,
      });
  }
  return b;
}

// 开发期挂到 window，便于自动化验证与手工调试（生产构建不生效）
if (import.meta.env.DEV && typeof window !== 'undefined') {
  (window as unknown as Record<string, unknown>).__editor = useEditor;
}

if (import.meta.env.DEV && typeof window !== 'undefined') {
  // 供自动化测试直接校验换算
  void import('../core/background').then((m) => {
    (window as unknown as Record<string, unknown>).__bg = m;
  });
}
