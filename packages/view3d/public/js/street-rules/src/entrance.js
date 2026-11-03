// 出入口可达性 —— `EntranceSuggest` 的逐行移植（docs/A §0 第二个消费点）。
//
// 为什么单独一块：Placemaker 里**有两个互不相同的"临街"判据**，容差差一个量级。
//
//   1) AttachmentFinder（贴合点）  沿边容差 = 路宽/2 + 3 m
//      → 决定"这条边算不算临街"，管边分区、入口路延伸
//   2) EntranceSuggest（出入口）   外向射线 40 m，命中后 gap = 距离 - 路宽/2 ≤ 15 m
//      → 决定"能不能画出车行口"，管 ready=no
//
// 而且第 2 条**根本不看贴合点**：只要 `Site.NearbyNets` 里有 category=Road 的街，
// 它就从环上的点沿外法向打射线；只有**一条车道都没有**时，才退回"离 RoadAlongEdge
// 贴合点 < 30 m"这个兜底（EntranceSuggest.cs:65 的 else 分支）。
//
// 这个区别实测过：把 54 份真实地块的街道整体外移 11 m（远超声纳 7 m），
// 引擎仍然 ready，而且产出量几乎不变（parking 593 → 590 个物件）。
// 早先 docs/A 写的"只遍历 RoadAlongEdge（reach 20 m）"是错的，已改。
import { bezier, dist, norm, perp, sub } from "../../plan-ir/src/geom.js";
/** `Roads.SearchDistance`：外法向射线最长打 40 m。 */
export const ENTRANCE_SEARCH_DISTANCE = 40;
/** `gap = 距离 - 路宽/2` 的上限；下限是 `-路宽/2`（射线起点压在路上也算）。 */
export const ENTRANCE_MAX_GAP = 15;
/** `Roads.Samples`：每条街折成 16 段。 */
export const ENTRANCE_SAMPLES = 16;
/**
 * 候选点离边两端的退让（`margin = Aisle/2 + StallWidth + 1`）。
 *
 * 这不是一个"通用常数"——它来自停车族的默认设置（`aisleWidth = 7`、`stallWidth = 3`），
 * 所以默认 7.5。别的生成器族如果用自己的 margin，调用方传进来。
 * 引擎里这是**硬前提**：`if (len < 2 * margin) continue` —— 边太短则整条边不参与。
 */
export const DEFAULT_MARGIN = 7.5;
/** `Roads.From`：只取 category == 'road' 的街，折成 16 段。 */
function roadSegs(nets) {
    const out = [];
    for (const n of nets) {
        if (n.category !== 'road')
            continue;
        const pts = [];
        let len = 0;
        for (let i = 0; i <= ENTRANCE_SAMPLES; i++) {
            const p = bezier(n.a, n.b, n.c, n.d, i / ENTRANCE_SAMPLES);
            pts.push(p);
            if (i > 0)
                len += dist(pts[i], pts[i - 1]);
        }
        if (len < 0.5)
            continue;
        out.push({ netId: n.externalId, width: Math.max(0, n.width), pts, len });
    }
    return out;
}
/**
 * 从 origin 沿 dir 打射线，找最近的相交线段（`Roads.Cast` 的移植）。
 * 返回 { distance, netId, direction } 或 null。
 */
function castRay(segs, origin, dir, maxDist) {
    let best = maxDist;
    let found = null;
    const far = [origin[0] + dir[0], origin[1] + dir[1]];
    for (const s of segs) {
        for (let i = 0; i + 1 < s.pts.length; i++) {
            const c = s.pts[i];
            const d = s.pts[i + 1];
            // LineLine(origin, far, c, d) → s 沿射线（单位方向即米），u 沿线段
            const r = [far[0] - origin[0], far[1] - origin[1]];
            const q = [d[0] - c[0], d[1] - c[1]];
            const den = r[0] * q[1] - r[1] * q[0];
            if (Math.abs(den) < 1e-12)
                continue;
            const w = [c[0] - origin[0], c[1] - origin[1]];
            const sRay = (w[0] * q[1] - w[1] * q[0]) / den;
            const u = (w[0] * r[1] - w[1] * r[0]) / den;
            if (u < -1e-9 || u > 1 + 1e-9)
                continue;
            if (sRay <= 1e-6 || sRay > best)
                continue;
            best = sRay;
            found = {
                distance: sRay,
                netId: s.netId,
                direction: norm(sub(d, c)),
                width: s.width,
            };
        }
    }
    return found;
}
/**
 * 能不能在这块地上画出一个车行出入口。
 *
 * 语义照抄 `EntranceSuggest.Suggest` 的**准入条件**（不照抄它的打分排序——
 * 我们只需要"能不能"，不需要"哪个点最好"）。
 */
export function entranceReachable(ring, nets, opts = {}) {
    const margin = opts.margin ?? DEFAULT_MARGIN;
    const searchDistance = opts.searchDistance ?? ENTRANCE_SEARCH_DISTANCE;
    const maxGap = opts.maxGap ?? ENTRANCE_MAX_GAP;
    const empty = {
        reachable: false,
        point: null,
        netId: -1,
        gap: NaN,
        distance: NaN,
        ringSegment: -1,
        ringT: 0,
        candidates: 0,
        hasLongEnoughEdge: false,
        note: '',
    };
    const n = ring.length;
    if (n < 3)
        return { ...empty, note: '环少于 3 点' };
    const segs = roadSegs(nets);
    if (segs.length === 0) {
        // 引擎此时才退回"离贴合点 < 30 m"的兜底；我们这里如实说明，不做二次判断
        return { ...empty, note: '没有任何 category=road 的街道，引擎会退回「离贴合点 <30 m」兜底' };
    }
    // 步长与引擎一致：max(2, 周长/400)
    let perimeter = 0;
    for (let i = 0; i < n; i++)
        perimeter += dist(ring[i], ring[(i + 1) % n]);
    const step = Math.max(2, perimeter / 400);
    let candidates = 0;
    let longEnough = false;
    for (let i = 0; i < n; i++) {
        const a = ring[i];
        const b = ring[(i + 1) % n];
        const len = dist(a, b);
        if (len < 2 * margin)
            continue;
        longEnough = true;
        const tan = norm(sub(b, a));
        // 逆时针环的内法向 = perp(tan)（左侧）；外法向取反
        const inward = perp(tan);
        const outward = [-inward[0], -inward[1]];
        for (let along = margin; along <= len - margin + 1e-9; along += step) {
            const t = along / len;
            const p = [a[0] + tan[0] * along, a[1] + tan[1] * along];
            const hit = castRay(segs, p, outward, searchDistance);
            candidates++;
            if (!hit)
                continue;
            const gap = hit.distance - hit.width / 2;
            // 引擎的准入：gap ∈ [-width/2, 15]
            if (gap > maxGap || gap < -hit.width / 2)
                continue;
            // 只需要"能不能画出来"，不需要最优候选 —— 命中即返回
            return {
                reachable: true,
                point: p,
                netId: hit.netId,
                gap,
                distance: hit.distance,
                ringSegment: i,
                ringT: t,
                candidates,
                hasLongEnoughEdge: true,
                note: '',
            };
        }
    }
    return {
        ...empty,
        candidates,
        hasLongEnoughEdge: longEnough,
        note: longEnough
            ? `所有候选点都打不到 40 m 内的车行道，或 gap > ${maxGap} m`
            : `没有任何一条边长到能放出口（需 ≥ ${(2 * margin).toFixed(1)} m）`,
    };
}
