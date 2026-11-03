// 交互控制器。**照搬 PCG `editor/controller.ts` 的行为**，按我们的对象模型重写。
//
// 一次指针操作的生命周期只有两种：
//   1. **拖拽型**（矩形/圆/框选/移动/顶点/平移）：down 记锚点 → move 更新 → up 提交；
//   2. **点击型**（多边形/折线/测量/出入口）：每次 down 落一个点，右键或回车结束。
// 把这两类分开之后，工具再多也不会互相打架 —— 这是整个控制器最重要的一条结构。
//
// 三个容易忘但用户一定会碰到的细节：
//   · `Space` 按住 + 拖 = 平移（不切工具，松手还回原工具）
//   · 拖动超过 3 px 才算拖，否则算点击（否则点选时会意外挪动对象）
//   · `Esc` 是分层的：先撤销正在画的东西，再切回选择工具，最后才清空选中
import { snapPoint, hitRing } from "./snap.js";
import { LAYER_OF, ROAD_WIDTH, areaPaths, commonDefaults, isAreaObj, } from "./model.js";
import { boundsOfPaths, boundsValid, cleanRing, circleRing, distToPaths, isSimple, pointInPaths, polylineLength, rectRing, ringArea, strokeEnvelope, } from "./polygon.js";
import { TOOL_SHORTCUT } from "./store.js";
import { DEG, dist, snapAngle } from "./vec.js";
import { render2d } from "./render2d.js";
const DRAG_PX = 3;
export class EditorController {
    canvas;
    store;
    cam;
    onGenerate;
    draft = null;
    spaceDown = false;
    shiftDown = false;
    cursorWorld = { x: 0, y: 0 };
    snap = null;
    guides = [];
    measureCursor;
    downScreen = null;
    moved = false;
    constructor(canvas, store, cam, onGenerate) {
        this.canvas = canvas;
        this.store = store;
        this.cam = cam;
        this.onGenerate = onGenerate;
    }
    // ───────────────────────────── 渲染
    render(ctx, w, h) {
        const { model, view } = this.store.state;
        const measure = this.draft?.t === 'measure' ? { pts: this.draft.pts, cursor: this.measureCursor, mode: view.measureMode } : null;
        render2d({
            ctx,
            width: w,
            height: h,
            cam: this.cam,
            model,
            view,
            previews: this.buildPreviews(),
            snap: this.snap,
            guides: this.guides,
            measure,
        });
    }
    buildPreviews() {
        const d = this.draft;
        const v = this.store.state.view;
        if (!d)
            return [];
        const out = [];
        if (d.t === 'rect') {
            const ring = rectRing(d.a.x, d.a.y, d.b.x, d.b.y);
            out.push({ kind: 'ring', ring, label: ringLabel(ring, v.drawKind) });
            out.push({ kind: 'polyline', pts: [d.a, { x: d.b.x, y: d.a.y }, d.b, { x: d.a.x, y: d.b.y }, d.a] });
        }
        else if (d.t === 'circle') {
            const ring = circleRing(d.c, d.r, 48);
            out.push({ kind: 'ring', ring, label: ringLabel(ring, v.drawKind) });
            out.push({ kind: 'polyline', pts: [d.c, { x: d.c.x + d.r, y: d.c.y }], label: `R ${d.r.toFixed(2)} m` });
        }
        else if (d.t === 'poly' || d.t === 'line') {
            const pts = d.pts;
            if (pts.length)
                out.push({ kind: 'polyline', pts: [...pts, this.cursorWorld] });
            if (d.t === 'poly' && pts.length >= 2) {
                const ring = [...pts, this.cursorWorld];
                if (ring.length >= 3)
                    out.push({ kind: 'ring', ring, label: ringLabel(ring, v.drawKind), ghost: true });
            }
            if (d.t === 'line' && pts.length >= 1) {
                const len = polylineLength([...pts, this.cursorWorld]);
                const ang = Math.atan2(this.cursorWorld.y - pts[pts.length - 1].y, this.cursorWorld.x - pts[pts.length - 1].x);
                out.push({
                    kind: 'polyline',
                    pts: [pts[pts.length - 1], this.cursorWorld],
                    label: `${len.toFixed(1)} m · ${(ang * DEG).toFixed(0)}°`,
                    width: ROAD_WIDTH.local,
                });
            }
        }
        // 框选的矩形由 view.boxSelect 画，这里不重复
        return out;
    }
    // ───────────────────────────── 指针
    onPointerDown(e) {
        // 合成事件（无头验证）里 pointerId 可能不是活动指针，setPointerCapture 会抛
        try {
            this.canvas.setPointerCapture(e.pointerId);
        }
        catch {
            /* 拿不到捕获也不影响：拖动靠 pointermove 的目标就是画布本身 */
        }
        this.canvas.focus();
        this.downScreen = { x: e.clientX, y: e.clientY };
        this.moved = false;
        this.shiftDown = e.shiftKey;
        const raw = this.worldOf(e);
        const v = this.store.state.view;
        // 中键 / 右键 / 空格 = 平移，优先级最高（任何工具下都能平移）
        if (e.button === 1 || (e.button === 0 && this.spaceDown) || v.activeTool === 'pan') {
            this.draft = { t: 'pan', last: { x: e.clientX, y: e.clientY } };
            return;
        }
        if (e.button === 2) {
            // 右键：结束当前绘制（PCG 的手感），没有绘制就什么也不做
            this.finishDraft();
            return;
        }
        if (e.button !== 0)
            return;
        const snapped = this.applySnap(raw, e.shiftKey);
        switch (v.activeTool) {
            case 'select':
                this.downSelect(raw, e.shiftKey);
                return;
            case 'rect':
                this.draft = { t: 'rect', a: snapped, b: snapped };
                return;
            case 'circle':
                this.draft = { t: 'circle', c: snapped, r: 0 };
                return;
            case 'polygon':
                if (this.draft?.t === 'poly') {
                    // 点回第一个点 = 闭合
                    if (this.draft.pts.length >= 3 && dist(snapped, this.draft.pts[0]) <= this.cam.meters(10)) {
                        this.closePolygon();
                        return;
                    }
                    this.draft.pts.push(snapped);
                }
                else {
                    this.draft = { t: 'poly', pts: [snapped] };
                }
                return;
            case 'polyline':
                if (this.draft?.t === 'line')
                    this.draft.pts.push(snapped);
                else
                    this.draft = { t: 'line', pts: [snapped] };
                return;
            case 'measure':
                if (this.draft?.t === 'measure')
                    this.draft.pts.push(snapped);
                else
                    this.draft = { t: 'measure', pts: [snapped] };
                return;
        }
    }
    onPointerMove(e) {
        const raw = this.worldOf(e);
        if (this.downScreen && !this.moved) {
            if (Math.hypot(e.clientX - this.downScreen.x, e.clientY - this.downScreen.y) > DRAG_PX)
                this.moved = true;
        }
        this.cursorWorld = raw;
        const d = this.draft;
        if (d?.t === 'pan') {
            const dx = e.clientX - d.last.x;
            const dy = e.clientY - d.last.y;
            d.last = { x: e.clientX, y: e.clientY };
            this.cam.panBy(dx, dy);
            this.store.patchSettings({ zoom: this.cam.zoom });
            return;
        }
        const v = this.store.state.view;
        // 选择工具下悬停高亮：这是"点得中"的第一层反馈，没有它整个编辑器像块铁板
        if (!d && v.activeTool === 'select') {
            const hit = this.hitTest(raw);
            this.store.hover(hit?.id);
            this.setCursor(hit ? 'move' : 'default');
        }
        else if (!d && v.activeTool !== 'select') {
            this.setCursor('crosshair');
        }
        const snapped = this.applySnap(raw, e.shiftKey, this.anchorOf());
        if (d?.t === 'rect')
            d.b = snapped;
        else if (d?.t === 'circle')
            d.r = dist(d.c, snapped);
        else if (d?.t === 'poly' || d?.t === 'line' || d?.t === 'measure') {
            if (d.t === 'measure')
                this.measureCursor = snapped;
        }
        else if (d?.t === 'move') {
            if (this.moved)
                this.applyMove(d, snapped);
        }
        else if (d?.t === 'vertex') {
            if (this.moved)
                this.applyVertex(d, snapped, e.shiftKey);
        }
        else if (d?.t === 'box') {
            d.b = snapped;
            this.store.patchSettings({ boxSelect: { minX: Math.min(d.a.x, d.b.x), minY: Math.min(d.a.y, d.b.y), maxX: Math.max(d.a.x, d.b.x), maxY: Math.max(d.a.y, d.b.y) } });
        }
        const hint = this.hintFor(snapped);
        this.store.setHint(hint);
    }
    onPointerUp(e) {
        try {
            this.canvas.releasePointerCapture(e.pointerId);
        }
        catch {
            /* 同上 */
        }
        const d = this.draft;
        this.downScreen = null;
        if (d?.t === 'pan') {
            this.draft = null;
            return;
        }
        if (d?.t === 'rect') {
            this.draft = null;
            const ring = cleanRing(rectRing(d.a.x, d.a.y, d.b.x, d.b.y));
            if (ring.length >= 3 && ringArea(ring) > 0.01)
                this.commitArea(ring);
            else
                this.store.showToast('矩形太小，已取消');
            return;
        }
        if (d?.t === 'circle') {
            this.draft = null;
            if (d.r > 0.1)
                this.commitArea(circleRing(d.c, d.r, 48));
            return;
        }
        if (d?.t === 'move' || d?.t === 'vertex') {
            this.draft = null;
            return;
        }
        if (d?.t === 'box') {
            this.draft = null;
            const b = { minX: Math.min(d.a.x, d.b.x), minY: Math.min(d.a.y, d.b.y), maxX: Math.max(d.a.x, d.b.x), maxY: Math.max(d.a.y, d.b.y) };
            this.store.patchSettings({ boxSelect: null });
            const ids = this.selectInBox(b, e.shiftKey);
            this.store.select(ids, e.shiftKey);
            return;
        }
        // 点击型工具在 up 时不做提交
    }
    onPointerLeave() {
        this.store.hover(undefined);
        this.snap = null;
        this.guides = [];
        this.store.setHint({ snapLabel: '' });
    }
    onDoubleClick(e) {
        const raw = this.worldOf(e);
        const hit = this.hitTest(raw);
        if (hit) {
            this.store.select([hit.id]);
            this.focusObject(hit.id);
        }
        else {
            this.fitAll();
        }
    }
    onWheel(e) {
        e.preventDefault();
        const anchor = this.screenOf(e);
        const factor = Math.exp(-e.deltaY * 0.0016);
        this.cam.zoomBy(factor, anchor);
        this.store.patchSettings({ zoom: this.cam.zoom });
    }
    onContextMenu(e) {
        e.preventDefault();
        this.finishDraft();
    }
    // ───────────────────────────── 键盘
    onKeyDown(e) {
        const st = this.store;
        if (e.key === ' ')
            this.spaceDown = true;
        const k = e.key.toLowerCase();
        const mod = e.ctrlKey || e.metaKey;
        if (mod && k === 'z' && !e.shiftKey) {
            st.undo();
            return true;
        }
        if ((mod && k === 'y') || (mod && e.shiftKey && k === 'z')) {
            st.redo();
            return true;
        }
        if (mod && k === 'd') {
            st.duplicateSelected();
            return true;
        }
        if (e.key === 'Escape') {
            if (this.draft && this.draft.t !== 'measure') {
                this.draft = null;
                this.guides = [];
                st.setHint({ message: undefined, warning: false });
            }
            else if (this.draft) {
                this.draft = null;
            }
            else if (st.state.view.activeTool !== 'select') {
                st.setTool('select');
            }
            else {
                st.select([]);
            }
            return true;
        }
        if (e.key === 'Enter') {
            this.finishDraft();
            return true;
        }
        if (e.key === 'Delete' || e.key === 'Backspace') {
            st.deleteSelected();
            return true;
        }
        if (k === 'f') {
            this.focusSelection();
            return true;
        }
        if (e.key === 'Home') {
            this.fitAll();
            return true;
        }
        if (k === 'g' && !mod) {
            // G 生成 / Shift+G 换一版：和顶栏按钮走同一条路
            this.onGenerate(e.shiftKey);
            return true;
        }
        if (e.key === 'Shift') {
            this.shiftDown = true;
            return false;
        }
        if (!mod) {
            const tool = TOOL_SHORTCUT[k];
            if (tool) {
                this.draft = null;
                st.setTool(tool);
                return true;
            }
        }
        return false;
    }
    onKeyUp(e) {
        if (e.key === ' ')
            this.spaceDown = false;
        if (e.key === 'Shift')
            this.shiftDown = false;
    }
    // ───────────────────────────── 选择 / 移动
    downSelect(raw, shift) {
        const st = this.store;
        const v = st.state.view;
        // 1) 单选且未锁 → 先判顶点
        if (v.selectionIds.length === 1) {
            const o = st.state.model.objects[v.selectionIds[0]];
            if (o && !o.locked) {
                const vh = this.hitVertex(o, raw);
                if (vh) {
                    this.draft = { t: 'vertex', id: o.id, pathIndex: vh.pathIndex, index: vh.index, start: raw, orig: o, moved: false };
                    return;
                }
            }
        }
        // 2) 命中对象 → 拖动移动
        const hit = this.hitTest(raw);
        if (hit) {
            if (!v.selectionIds.includes(hit.id))
                st.select([hit.id], shift);
            const ids = shift && v.selectionIds.includes(hit.id) ? v.selectionIds : [hit.id];
            const orig = new Map();
            for (const id of ids) {
                const o = st.state.model.objects[id];
                if (o && !o.locked)
                    orig.set(id, JSON.parse(JSON.stringify(o)));
            }
            if (orig.size)
                this.draft = { t: 'move', start: raw, orig, moved: false };
            return;
        }
        // 3) 空白 → 框选
        if (!shift)
            st.select([]);
        this.draft = { t: 'box', a: raw, b: raw };
    }
    applyMove(d, raw) {
        let dx = raw.x - d.start.x;
        let dy = raw.y - d.start.y;
        if (this.shiftDown) {
            // Shift = 只沿主方向移动（和绘制时的 45° 约束一致）
            if (Math.abs(dx) > Math.abs(dy))
                dy = 0;
            else
                dx = 0;
        }
        const wasMoved = d.moved;
        d.moved = Math.abs(dx) > 1e-9 || Math.abs(dy) > 1e-9;
        if (!d.moved)
            return;
        // 历史要记"拖动前"的状态：所以第一次移动时先 pushHistory（此刻 model 还没被改），
        // 之后再静默替换 model。顺序反了的话 Ctrl+Z 会回到拖动后的状态 —— 看着像撤销失效。
        if (!wasMoved)
            this.store.pushHistory('移动对象');
        const model = this.store.state.model;
        const objects = { ...model.objects };
        for (const [id, orig] of d.orig)
            objects[id] = translate(orig, { x: dx, y: dy });
        this.store.replaceModelSilent({ ...model, objects });
    }
    applyVertex(d, raw, shift) {
        let p = raw;
        if (shift) {
            const paths = areaPaths(d.orig);
            const ring = paths?.[d.pathIndex];
            if (d.orig.kind === 'road') {
                const pts = d.orig.centerline;
                const prev = pts[Math.max(0, d.index - 1)];
                p = snapAngle(prev, p);
            }
            else if (ring) {
                const prev = ring[(d.index - 1 + ring.length) % ring.length];
                p = snapAngle(prev, p);
            }
        }
        if (!d.moved)
            this.store.pushHistory('移动顶点');
        d.moved = true;
        const model = this.store.state.model;
        const o = model.objects[d.id];
        if (!o)
            return;
        const paths = areaPaths(o);
        const next = JSON.parse(JSON.stringify(o));
        if (next.kind === 'road') {
            next.centerline = next.centerline.map((q, i) => (i === d.index ? p : q));
        }
        else if (next.kind === 'access') {
            next.pos = p;
        }
        else if (paths) {
            const np = areaPaths(next);
            if (np?.[d.pathIndex])
                np[d.pathIndex][d.index] = p;
        }
        const objects = { ...model.objects, [d.id]: next };
        this.store.replaceModelSilent({ ...model, objects });
    }
    // ───────────────────────────── 提交
    finishDraft() {
        const d = this.draft;
        if (!d)
            return;
        if (d.t === 'poly') {
            this.closePolygon();
            return;
        }
        if (d.t === 'line') {
            this.draft = null;
            if (d.pts.length >= 2)
                this.commitRoad(cleanPts(d.pts));
            else
                this.store.showToast('折线至少两个点');
            return;
        }
        if (d.t === 'measure') {
            this.draft = null;
            this.measureCursor = undefined;
            this.store.setHint({ length: undefined, area: undefined });
            return;
        }
        this.draft = null;
    }
    closePolygon() {
        const d = this.draft;
        if (d?.t !== 'poly')
            return;
        const ring = cleanRing(d.pts);
        this.draft = null;
        if (ring.length < 3) {
            this.store.showToast('多边形至少三个点');
            return;
        }
        if (!isSimple(ring)) {
            this.store.showToast('多边形自交，已取消');
            return;
        }
        this.commitArea(ring);
    }
    commitArea(ring) {
        const st = this.store;
        const kind = st.state.view.drawKind;
        const ccw = ringArea(ring) > 0 ? ring : [...ring].reverse();
        if (kind === 'access') {
            this.placeAccess(ring[0]);
            return;
        }
        if (kind === 'road') {
            this.store.showToast('道路请用折线工具（L）');
            return;
        }
        const label = { site: '园区', parcel: '地块', building: '建筑', nobuild: '禁建区', ground: '地面' }[kind] ?? '对象';
        let o;
        if (kind === 'building') {
            const b = {
                ...commonDefaults('building', `${label} ${countOf(st.state.model, 'building') + 1}`),
                footprint: [ccw],
                height: 9,
                floors: 3,
                style: 'geometric',
                archetype: 'generic',
            };
            o = b;
        }
        else if (kind === 'site') {
            const s = { ...commonDefaults('site', `${label} ${countOf(st.state.model, 'site') + 1}`), boundary: [ccw] };
            o = s;
        }
        else if (kind === 'nobuild') {
            const n = { ...commonDefaults('nobuild', `${label} ${countOf(st.state.model, 'nobuild') + 1}`), boundary: [ccw] };
            o = n;
        }
        else if (kind === 'ground') {
            const g = {
                ...commonDefaults('ground', `${label} ${countOf(st.state.model, 'ground') + 1}`),
                boundary: [ccw],
                surface: 'paving',
            };
            o = g;
        }
        else {
            const p = {
                ...commonDefaults('parcel', `地块 ${countOf(st.state.model, 'parcel') + 1}`),
                boundary: [ccw],
                zoneKind: 'commercialLow',
                density: 0.45,
                far: 1,
                heightRange: [6, 18],
                seed: Math.floor(Math.random() * 9999),
            };
            o = p;
        }
        st.create(o, `绘制${KIND_CN[o.kind]}`);
    }
    commitRoad(pts) {
        const st = this.store;
        const o = {
            ...commonDefaults('road', `道路 ${countOf(st.state.model, 'road') + 1}`),
            centerline: pts,
            roadClass: 'local',
            width: ROAD_WIDTH.local,
            sidewalk: true,
        };
        st.create(o, '绘制道路');
    }
    placeAccess(pos) {
        const st = this.store;
        const o = {
            ...commonDefaults('access', `出入口 ${countOf(st.state.model, 'access') + 1}`),
            pos,
            width: 6,
            dir: 0,
            accessType: 'main',
            priority: 1,
        };
        st.create(o, '放置出入口');
    }
    // ───────────────────────────── 命中
    hitTest(raw) {
        const { model, view } = this.store.state;
        const tol = this.cam.meters(6);
        const filter = view.selectionFilter;
        const candidates = [...model.order].reverse();
        for (const id of candidates) {
            const o = model.objects[id];
            if (!o || !o.visible)
                continue;
            const layer = LAYER_OF[o.kind];
            if (!view.layerVisibility[layer] || view.layerLocked[layer])
                continue;
            if (filter === 'parcels' && o.kind !== 'parcel')
                continue;
            if (filter === 'buildings' && o.kind !== 'building')
                continue;
            if (filter === 'roads' && o.kind !== 'road')
                continue;
            if (filter === 'ground' && o.kind !== 'ground' && o.kind !== 'nobuild')
                continue;
            if (filter === 'generated' && o.origin !== 'generated')
                continue;
            if (this.hits(o, raw, tol))
                return o;
        }
        return null;
    }
    hits(o, p, tol) {
        const paths = areaPaths(o);
        if (paths)
            return hitRing(paths, p, tol);
        if (o.kind === 'road') {
            // 先判包络宽度，再退化成中心线容差：窄路也点得中
            const env = strokeEnvelope(o.centerline, o.width);
            if (env.length >= 3 && pointInPaths([env], p))
                return true;
            return distToPaths([o.centerline], p) <= Math.max(tol, o.width / 2);
        }
        if (o.kind === 'access')
            return dist(p, o.pos) <= Math.max(1.2, o.width / 2) + tol;
        return false;
    }
    hitVertex(o, p) {
        const tol = this.cam.meters(8);
        const paths = areaPaths(o);
        if (paths) {
            for (let pi = 0; pi < paths.length; pi++) {
                for (let i = 0; i < paths[pi].length; i++) {
                    if (dist(p, paths[pi][i]) <= tol)
                        return { pathIndex: pi, index: i };
                }
            }
            return null;
        }
        if (o.kind === 'road') {
            for (let i = 0; i < o.centerline.length; i++)
                if (dist(p, o.centerline[i]) <= tol)
                    return { pathIndex: 0, index: i };
        }
        if (o.kind === 'access' && dist(p, o.pos) <= tol)
            return { pathIndex: 0, index: 0 };
        return null;
    }
    selectInBox(b, _additive) {
        const { model, view } = this.store.state;
        const out = [];
        for (const id of model.order) {
            const o = model.objects[id];
            if (!o || !o.visible)
                continue;
            const layer = LAYER_OF[o.kind];
            if (!view.layerVisibility[layer] || view.layerLocked[layer])
                continue;
            const paths = areaPaths(o);
            if (paths) {
                const ob = boundsOfPaths(paths);
                if (boundsValid(ob) && ob.minX >= b.minX && ob.maxX <= b.maxX && ob.minY >= b.minY && ob.maxY <= b.maxY)
                    out.push(id);
            }
            else if (o.kind === 'access') {
                if (o.pos.x >= b.minX && o.pos.x <= b.maxX && o.pos.y >= b.minY && o.pos.y <= b.maxY)
                    out.push(id);
            }
            else if (o.kind === 'road') {
                const ob = boundsOfPaths([o.centerline]);
                if (boundsValid(ob) && ob.minX >= b.minX && ob.maxX <= b.maxX && ob.minY >= b.minY && ob.maxY <= b.maxY)
                    out.push(id);
            }
        }
        return out;
    }
    // ───────────────────────────── 捕捉 / 相机
    applySnap(raw, shift, anchor) {
        const { model, view } = this.store.state;
        const { result, guides } = snapPoint(raw, model, view, this.cam, { anchor, allowOrtho: !shift });
        this.snap = result;
        this.guides = guides;
        return result.p;
    }
    anchorOf() {
        const d = this.draft;
        if (d?.t === 'poly' || d?.t === 'line' || d?.t === 'measure')
            return d.pts[d.pts.length - 1];
        return undefined;
    }
    hintFor(p) {
        const d = this.draft;
        let length;
        let angle;
        let area;
        let message;
        if (d?.t === 'rect') {
            const ring = rectRing(d.a.x, d.a.y, d.b.x, d.b.y);
            area = ringArea(ring);
            length = dist(d.a, d.b);
            angle = Math.atan2(d.b.y - d.a.y, d.b.x - d.a.x);
        }
        else if (d?.t === 'circle') {
            area = Math.PI * d.r * d.r;
            length = d.r * 2 * Math.PI;
        }
        else if (d?.t === 'poly' && d.pts.length) {
            const ring = [...d.pts, p];
            if (ring.length >= 3)
                area = Math.abs(ringArea(ring));
        }
        else if (d?.t === 'line' && d.pts.length) {
            const pts = [...d.pts, p];
            length = polylineLength(pts);
            const last = d.pts[d.pts.length - 1];
            angle = Math.atan2(p.y - last.y, p.x - last.x);
        }
        else if (d?.t === 'measure') {
            const pts = [...d.pts, p];
            if (this.store.state.view.measureMode === 'distance')
                length = polylineLength(pts);
            else if (pts.length >= 3)
                area = Math.abs(ringArea(pts));
        }
        void message;
        return { x: p.x, y: p.y, snapLabel: this.snap?.label ?? '', length, angle, area };
    }
    fitAll() {
        const { model } = this.store.state;
        const all = [];
        for (const id of model.order) {
            const o = model.objects[id];
            if (!o)
                continue;
            const paths = areaPaths(o);
            if (paths)
                all.push(...paths);
            else if (o.kind === 'road')
                all.push(o.centerline);
            else if (o.kind === 'access')
                all.push([o.pos]);
        }
        const b = all.length ? boundsOfPaths(all) : null;
        if (b && boundsValid(b))
            this.cam.fit(b);
        else {
            this.cam.cx = 0;
            this.cam.cy = 0;
            this.cam.zoom = 5;
        }
        this.store.patchSettings({ zoom: this.cam.zoom });
    }
    focusSelection() {
        const ids = this.store.state.view.selectionIds;
        if (!ids.length) {
            this.fitAll();
            return;
        }
        this.focusObject(ids[0]);
    }
    focusObject(id) {
        const o = this.store.state.model.objects[id];
        if (!o)
            return;
        const paths = areaPaths(o);
        const rings = paths ? [...paths] : o.kind === 'road' ? [o.centerline] : o.kind === 'access' ? [[o.pos]] : [];
        const b = boundsOfPaths(rings);
        if (!boundsValid(b))
            return;
        const pad = 6;
        this.cam.fit({ minX: b.minX - pad, minY: b.minY - pad, maxX: b.maxX + pad, maxY: b.maxY + pad }, 0.12);
        this.store.patchSettings({ zoom: this.cam.zoom });
    }
    getSnap() {
        return this.snap;
    }
    // ───────────────────────────── 杂项
    worldOf(e) {
        const r = this.canvas.getBoundingClientRect();
        return this.cam.toWorld({ x: e.clientX - r.left, y: e.clientY - r.top });
    }
    screenOf(e) {
        const r = this.canvas.getBoundingClientRect();
        return { x: e.clientX - r.left, y: e.clientY - r.top };
    }
    setCursor(c) {
        if (this.canvas.style.cursor !== c)
            this.canvas.style.cursor = c;
    }
}
const KIND_CN = {
    site: '园区',
    parcel: '地块',
    building: '建筑',
    road: '道路',
    access: '出入口',
    nobuild: '禁建区',
    ground: '地面',
};
function countOf(model, kind) {
    let n = 0;
    for (const id of model.order)
        if (model.objects[id]?.kind === kind)
            n++;
    return n;
}
function translate(o, d) {
    const next = JSON.parse(JSON.stringify(o));
    const move = (p) => ({ x: p.x + d.x, y: p.y + d.y });
    const paths = areaPaths(next);
    if (paths) {
        const moved = paths.map((r) => r.map(move));
        if (next.kind === 'building')
            next.footprint = moved;
        else if (isAreaObj(next))
            next.boundary = moved;
    }
    else if (next.kind === 'road') {
        next.centerline = next.centerline.map(move);
    }
    else if (next.kind === 'access') {
        next.pos = move(next.pos);
    }
    return next;
}
function cleanPts(pts) {
    const out = [];
    for (const p of pts) {
        const last = out[out.length - 1];
        if (last && dist(last, p) < 1e-6)
            continue;
        out.push(p);
    }
    return out;
}
function ringLabel(ring, kind) {
    const a = Math.abs(ringArea(ring));
    if (kind === 'road')
        return `${polylineLength(ring).toFixed(1)} m`;
    return `${a.toFixed(1)} m² · ${KIND_CN[kind] ?? kind}`;
}
