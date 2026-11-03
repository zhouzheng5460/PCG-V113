// 编辑器状态与动作。**照搬 PCG 的 store 语义**（撤销/重做、选中、图层、生成），
// 只是不用 zustand —— 本机没有 npm，用一个 30 行的订阅器就够了。
//
// 一条纪律：**所有会改 model 的动作都必须先 pushHistory(label)**。
// 没有 label 的撤销记录等于没有撤销（用户看到"撤销：未知操作"只会更慌）。
import { addObject, areaPaths, cloneObject, demoProject, emptyProject, isAreaObj, removeObjects, updateObjectIn, } from "./model.js";
import { booleanOp, offsetRing } from "./boolean.js";
export const TOOL_LABEL = {
    select: '选择',
    rect: '矩形',
    polygon: '多边形',
    polyline: '折线',
    circle: '圆',
    measure: '测量',
    pan: '平移',
};
export const TOOL_SHORTCUT = {
    v: 'select',
    r: 'rect',
    p: 'polygon',
    l: 'polyline',
    c: 'circle',
    m: 'measure',
    h: 'pan',
};
export const DRAW_KIND_LABEL = {
    site: '园区范围',
    parcel: '地块',
    building: '建筑',
    road: '道路',
    access: '出入口',
    nobuild: '禁建区',
    ground: '地面',
};
export const EMPTY_HINT = { x: 0, y: 0, snapLabel: '' };
const defaultView = () => ({
    activeView: '2d',
    selectionIds: [],
    activeTool: 'select',
    drawKind: 'parcel',
    measureMode: 'distance',
    snapEnabled: true,
    gridVisible: true,
    gridSnap: true,
    gridSize: 1,
    layerVisibility: { site: true, parcels: true, roads: true, buildings: true, access: true, ground: true, landscape: true },
    layerLocked: { site: false, parcels: false, roads: false, buildings: false, access: false, ground: false, landscape: false },
    selectionFilter: 'all',
    zoom: 5,
    boxSelect: null,
    measurePts: [],
});
const HISTORY_LIMIT = 80;
export class Store {
    state;
    /** 每一次 set 都 +1（渲染循环/面板用它判断"要不要重画"） */
    version = 0;
    /** 静默更新（拖动）次数。面板据此区分"要不要重建 DOM"：拖动时重建会丢输入焦点。 */
    silentSeq = 0;
    listeners = [];
    constructor(model) {
        this.state = {
            model: model ?? demoProject(),
            view: defaultView(),
            past: [],
            future: [],
            hint: EMPTY_HINT,
            toast: '',
            genStats: {},
        };
    }
    subscribe(fn) {
        this.listeners.push(fn);
        return () => {
            this.listeners = this.listeners.filter((f) => f !== fn);
        };
    }
    emit() {
        this.version += 1;
        for (const f of this.listeners)
            f();
    }
    set(patch) {
        this.state = { ...this.state, ...patch };
        this.emit();
    }
    setView(patch) {
        this.set({ view: { ...this.state.view, ...patch } });
    }
    // ───────────────────────────── 历史
    pushHistory(label) {
        const past = [...this.state.past, { label, model: this.state.model }];
        if (past.length > HISTORY_LIMIT)
            past.shift();
        this.set({ past, future: [] });
    }
    undo() {
        const { past, future, model } = this.state;
        if (!past.length) {
            this.showToast('没有可撤销的操作');
            return;
        }
        const last = past[past.length - 1];
        this.set({
            model: last.model,
            past: past.slice(0, -1),
            future: [...future, { label: last.label, model }],
            view: { ...this.state.view, selectionIds: [] },
            toast: `已撤销：${last.label}`,
        });
    }
    redo() {
        const { past, future, model } = this.state;
        if (!future.length) {
            this.showToast('没有可重做的操作');
            return;
        }
        const next = future[future.length - 1];
        this.set({
            model: next.model,
            past: [...past, { label: next.label, model }],
            future: future.slice(0, -1),
            view: { ...this.state.view, selectionIds: [] },
            toast: `已重做：${next.label}`,
        });
    }
    // ───────────────────────────── 视图 / 选中
    select(ids, additive = false) {
        const cur = this.state.view.selectionIds;
        const next = additive ? [...new Set([...cur, ...ids])] : ids;
        this.setView({ selectionIds: next });
    }
    hover(id) {
        if (this.state.view.hoverId === id)
            return;
        this.setView({ hoverId: id });
    }
    setTool(t) {
        this.setView({ activeTool: t, measurePts: [], boxSelect: null, selectionIds: t === 'select' ? this.state.view.selectionIds : [] });
    }
    setDrawKind(k) {
        this.setView({ drawKind: k });
    }
    setFilter(f) {
        this.setView({ selectionFilter: f });
    }
    patchSettings(p) {
        this.setView(p);
    }
    toggleLayerVisible(key) {
        this.setView({ layerVisibility: { ...this.state.view.layerVisibility, [key]: !this.state.view.layerVisibility[key] } });
    }
    toggleLayerLocked(key) {
        this.setView({ layerLocked: { ...this.state.view.layerLocked, [key]: !this.state.view.layerLocked[key] } });
    }
    setHint(h) {
        this.set({ hint: { ...this.state.hint, ...h } });
    }
    showToast(t) {
        this.set({ toast: t });
    }
    setModel(m) {
        this.set({ model: m, view: { ...this.state.view, selectionIds: [] } });
    }
    /**
     * 静默替换模型：**拖动过程中**用，不写历史（历史在拖动开始时已经写好）。
     *
     * 为什么需要它：拖一个对象会连发几十次 move 事件。每次都 pushHistory 的话，
     * 撤销一次只退一个像素 —— 用户会以为撤销坏了。
     */
    replaceModelSilent(m) {
        this.silentSeq += 1;
        this.set({ model: m });
    }
    // ───────────────────────────── 对象
    /** 新建对象（控制器画完就调它）。origin 默认 user。 */
    create(o, label) {
        this.pushHistory(label);
        this.set({ model: addObject(this.state.model, o), view: { ...this.state.view, selectionIds: [o.id] } });
    }
    updateObject(id, patch) {
        this.set({ model: updateObjectIn(this.state.model, id, patch) });
    }
    deleteSelected() {
        const ids = this.state.view.selectionIds;
        if (!ids.length)
            return;
        const deletable = ids.filter((id) => {
            const o = this.state.model.objects[id];
            return o && !o.locked;
        });
        if (!deletable.length) {
            this.showToast('选中的对象被锁定，先解锁');
            return;
        }
        this.pushHistory(`删除 ${deletable.length} 个对象`);
        this.set({
            model: removeObjects(this.state.model, deletable),
            view: { ...this.state.view, selectionIds: [] },
            genStats: stripStats(this.state.genStats, deletable),
        });
    }
    duplicateSelected() {
        const sel = this.state.view.selectionIds;
        if (!sel.length)
            return;
        this.pushHistory('复制');
        let model = this.state.model;
        const ids = [];
        for (const id of sel) {
            const o = model.objects[id];
            if (!o)
                continue;
            const copy = cloneObject(o, { x: 4, y: 4 });
            model = addObject(model, copy);
            ids.push(copy.id);
        }
        this.set({ model, view: { ...this.state.view, selectionIds: ids } });
    }
    toggleLock(id) {
        const ids = id ? [id] : this.state.view.selectionIds;
        if (!ids.length)
            return;
        // 锁定/解锁不进历史：它不是几何改动，进历史会让 Ctrl+Z 变得难以预测
        let model = this.state.model;
        for (const i of ids) {
            const o = model.objects[i];
            if (o)
                model = updateObjectIn(model, i, { locked: !o.locked });
        }
        this.set({ model, toast: '已切换锁定' });
    }
    promoteToFixed(id) {
        const ids = id ? [id] : this.state.view.selectionIds;
        if (!ids.length)
            return;
        this.pushHistory('转为固定');
        let model = this.state.model;
        for (const i of ids) {
            const o = model.objects[i];
            if (o)
                model = updateObjectIn(model, i, { fixed: true, origin: 'user' });
        }
        this.set({ model, toast: '已转为固定：生成算法不再改动它' });
    }
    // ───────────────────────────── 布尔 / 偏移
    applyBoolean(op) {
        const sel = this.state.view.selectionIds;
        const objs = sel.map((id) => this.state.model.objects[id]).filter((o) => !!o && isAreaObj(o));
        if (objs.length < 2) {
            this.showToast('布尔需要至少 2 个面对象');
            return;
        }
        const [main, ...rest] = objs;
        const mainPaths = areaPaths(main);
        if (!mainPaths)
            return;
        let acc = mainPaths;
        for (const o of rest) {
            const p = areaPaths(o);
            if (!p)
                continue;
            acc = booleanOp(op, acc, p);
            if (!acc.length) {
                // 结果为空（减光了 / 没交集）—— 整体回滚，不要把空几何写进项目
                this.showToast(op === 'intersect' ? '两个对象没有交集' : '运算结果为空，已取消');
                return;
            }
        }
        const label = op === 'union' ? '合并' : op === 'subtract' ? '减去' : '取交集';
        this.pushHistory(label);
        let model = this.state.model;
        const patch = main.kind === 'building' ? { footprint: acc } : { boundary: acc };
        model = updateObjectIn(model, main.id, patch);
        // 合并/交集：参与对象被并进主对象；减去：被减体保留（PCG 同规则）
        if (op !== 'subtract')
            model = removeObjects(model, rest.map((o) => o.id));
        this.set({ model, view: { ...this.state.view, selectionIds: [main.id] }, toast: `${label}完成` });
    }
    applyOffset(delta) {
        const sel = this.state.view.selectionIds;
        const objs = sel.map((id) => this.state.model.objects[id]).filter((o) => !!o && isAreaObj(o));
        if (objs.length !== 1) {
            this.showToast('偏移只支持单选一个面对象');
            return;
        }
        const o = objs[0];
        const paths = areaPaths(o);
        if (!paths || paths.length !== 1) {
            this.showToast('带洞的对象暂不支持偏移');
            return;
        }
        const next = offsetRing(paths[0], delta);
        if (!next.length) {
            this.showToast(`${delta < 0 ? '内缩' : '外扩'} ${Math.abs(delta)} m 会让几何自交，已取消`);
            return;
        }
        this.pushHistory(`${delta < 0 ? '内缩' : '外扩'} ${Math.abs(delta)} m`);
        const patch = o.kind === 'building' ? { footprint: next } : { boundary: next };
        this.set({ model: updateObjectIn(this.state.model, o.id, patch), toast: '偏移完成' });
    }
    // ───────────────────────────── 项目
    newProject() {
        this.pushHistory('新建项目');
        this.set({ model: emptyProject(), view: { ...defaultView(), gridSize: this.state.view.gridSize }, genStats: {} });
        this.showToast('已清空');
    }
    loadDemo() {
        this.pushHistory('载入示例');
        this.set({ model: demoProject(), view: defaultView(), genStats: {} });
        this.showToast('已载入示例园区');
    }
    exportJson() {
        return JSON.stringify(this.state.model, null, 2);
    }
    /** 由生成器回写：一次替换某地块的全部生成物（局部重算的唯一入口）。 */
    applyGenerated(parcelId, produce, stats) {
        const model = this.state.model;
        const keep = [];
        for (const id of model.order) {
            const o = model.objects[id];
            if (!o)
                continue;
            const generatedHere = o.generatedFrom === parcelId;
            // fixed 的对象永不被生成算法动 —— 这是产品承诺，不是优化项
            if (generatedHere && !o.fixed)
                continue;
            keep.push(id);
        }
        let next = { ...model, order: keep, objects: {} };
        for (const id of keep)
            next.objects[id] = model.objects[id];
        for (const o of produce)
            next = addObject(next, o);
        this.set({
            model: next,
            genStats: { ...this.state.genStats, [parcelId]: stats },
            toast: `生成完成：${stats.buildings} 栋 · 覆盖率 ${(stats.coverage * 100).toFixed(0)}%`,
        });
    }
    clearGenerated(parcelId) {
        const model = this.state.model;
        const toRemove = model.order.filter((id) => {
            const o = model.objects[id];
            return o && o.generatedFrom === parcelId && !o.fixed;
        });
        if (!toRemove.length) {
            this.showToast('这块地没有生成物');
            return;
        }
        this.pushHistory('清除生成');
        const genStats = { ...this.state.genStats };
        delete genStats[parcelId];
        this.set({ model: removeObjects(model, toRemove), genStats, toast: `已清除 ${toRemove.length} 个生成物` });
    }
}
function stripStats(map, removed) {
    const out = { ...map };
    for (const id of removed)
        delete out[id];
    return out;
}
