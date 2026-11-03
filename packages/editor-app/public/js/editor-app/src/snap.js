// 捕捉。优先级：对象顶点 > 边中点 > 网格 > 原始点。
//
// 状态栏要显示"捕捉到什么"（`snapLabel`），这不是装饰：**用户必须知道那一下为什么吸住了**。
// 没有标签时，画出来的东西"差 0.3 m"会让人怀疑软件有 bug，其实是他自己开着网格吸附。
import { pointInRing, distToRing } from "./polygon.js";
import { dist, mid } from "./vec.js";
import { areaPaths } from "./model.js";
const PX_TOL = 10;
/** 收集可见且未锁层的所有顶点与中点。锁层的对象不参与捕捉 —— 否则会被看不见的东西吸住。 */
function collectTargets(model, view) {
    const verts = [];
    const mids = [];
    for (const id of model.order) {
        const o = model.objects[id];
        if (!o || !o.visible)
            continue;
        const layer = layerOfKind(o.kind);
        if (!view.layerVisibility[layer] || view.layerLocked[layer])
            continue;
        const paths = areaPaths(o);
        if (paths) {
            for (const r of paths) {
                for (let i = 0; i < r.length; i++) {
                    verts.push({ p: r[i], id });
                    mids.push({ p: mid(r[i], r[(i + 1) % r.length]), id });
                }
            }
        }
        else if (o.kind === 'road') {
            for (const p of o.centerline)
                verts.push({ p, id });
            for (let i = 1; i < o.centerline.length; i++)
                mids.push({ p: mid(o.centerline[i - 1], o.centerline[i]), id });
        }
        else if (o.kind === 'access') {
            verts.push({ p: o.pos, id });
        }
    }
    return { verts, mids };
}
function layerOfKind(kind) {
    const map = {
        site: 'site',
        parcel: 'parcels',
        building: 'buildings',
        road: 'roads',
        access: 'access',
        nobuild: 'landscape',
        ground: 'ground',
    };
    return map[kind] ?? 'site';
}
export function snapPoint(raw, model, view, cam, opts = {}) {
    const tol = cam.meters(PX_TOL);
    const guides = [];
    if (!view.snapEnabled) {
        return { result: { p: applyGrid(raw, view), label: view.gridSnap ? `网格 ${view.gridSize} m` : '' }, guides };
    }
    const { verts, mids } = collectTargets(model, view);
    let best = null;
    let bestD = tol;
    for (const v of verts) {
        const d = dist(raw, v.p);
        if (d < bestD) {
            bestD = d;
            best = { p: v.p, label: '顶点', refId: v.id };
        }
    }
    if (!best) {
        for (const m of mids) {
            const d = dist(raw, m.p);
            if (d < bestD) {
                bestD = d;
                best = { p: m.p, label: '中点', refId: m.id };
            }
        }
    }
    if (best) {
        // 顶点/中点捕捉优先于正交：用户明确指向了一个已有特征点
        if (opts.anchor && best.refId) {
            const t = collectTargets(model, view).verts.find((v) => v.id === best.refId);
            if (t && Math.abs(t.p.x - best.p.x) < 1e-9) {
                guides.push({ a: { x: best.p.x, y: best.p.y - 50 }, b: { x: best.p.x, y: best.p.y + 50 } });
            }
        }
        return { result: best, guides };
    }
    // 正交：从 anchor 出发吸到水平/垂直
    if (opts.anchor && opts.allowOrtho !== false) {
        const dx = Math.abs(raw.x - opts.anchor.x);
        const dy = Math.abs(raw.y - opts.anchor.y);
        if (dx < tol) {
            guides.push({ a: opts.anchor, b: { x: opts.anchor.x, y: raw.y } });
            return { result: { p: { x: opts.anchor.x, y: raw.y }, label: '正交 · 竖直' }, guides };
        }
        if (dy < tol) {
            guides.push({ a: opts.anchor, b: { x: raw.x, y: opts.anchor.y } });
            return { result: { p: { x: raw.x, y: opts.anchor.y }, label: '正交 · 水平' }, guides };
        }
    }
    const g = applyGrid(raw, view);
    return { result: { p: g, label: view.gridSnap ? `网格 ${view.gridSize} m` : '' }, guides };
}
function applyGrid(p, view) {
    if (!view.gridSnap || view.gridSize <= 0)
        return p;
    const s = view.gridSize;
    return { x: Math.round(p.x / s) * s, y: Math.round(p.y / s) * s };
}
/** 命中测试用：这个点是否落在某个面的环内（含边容差）。 */
export function hitRing(paths, p, tol) {
    for (const r of paths) {
        if (pointInRing(r, p))
            return true;
        if (distToRing(r, p) <= tol)
            return true;
    }
    return false;
}
