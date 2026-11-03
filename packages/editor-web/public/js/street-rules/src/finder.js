// 《街道规则》的几何计算部分——AttachmentFinder 的逐行移植。
//
// 来源：vendor/placemaker/Core/Tool/AttachmentFinder.cs
// 三个常量必须与 C# 完全一致，否则"这条边算不算临街"会在临界地块上分叉：
//   AlongTolerance = 3   超出路宽一半之外还认多远的距离
//   MinAlongLength = 12  平行重叠的最小长度（还会被 40% 边长兜底）
//   平行度 = 0.95        |dot(街切向, 边方向)|
//
// 注意：ring 是**路缘线**不是路中线（RoadNetwork.cs:396）。所以"街道贴着地界"
// 指的是街的中线与 ring 的距离 ≈ 路宽/2，而不是 0。这一点搞错，所有边都会判成不临街。
import { bezier, dist, lerp2, norm, perp, sampleBezier, segmentIntersection, sub, dot, } from "../../plan-ir/src/geom.js";
export const ALONG_TOLERANCE = 3;
export const MIN_ALONG_LENGTH = 12;
export const PARALLEL_MIN = 0.95;
export function sampleNet(net) {
    return sampleBezier(net.a, net.b, net.c, net.d);
}
export function probeAlong(net, a, b) {
    const len = dist(a, b);
    const dirAB = norm(sub(b, a));
    const tol = Math.max(0, net.width) * 0.5 + ALONG_TOLERANCE;
    const { pts } = sampleNet(net);
    const step = samplingStep(net);
    let sMin = Infinity;
    let sMax = -Infinity;
    let nearMin = Infinity;
    let nearMax = -Infinity;
    let samples = 0;
    let parallelMax = 0;
    for (let k = 0; k < pts.length; k++) {
        const p = pts[k];
        const s = dot(sub(p, a), dirAB) / len;
        if (s < 0 || s > 1)
            continue;
        if (Math.abs(dot(sub(p, a), perp(dirAB))) > tol)
            continue;
        samples++;
        const k0 = Math.max(0, k - 1);
        const k1 = Math.min(pts.length - 1, k + 1);
        const par = Math.abs(dot(norm(sub(pts[k1], pts[k0])), dirAB));
        parallelMax = Math.max(parallelMax, par);
        nearMin = Math.min(nearMin, s);
        nearMax = Math.max(nearMax, s);
        if (par < PARALLEL_MIN)
            continue;
        sMin = Math.min(sMin, s);
        sMax = Math.max(sMax, s);
    }
    const overlap = sMax >= sMin ? (sMax - sMin) * len : 0;
    const nearOverlap = nearMax >= nearMin ? (nearMax - nearMin) * len : 0;
    const required = Math.min(MIN_ALONG_LENGTH, 0.4 * len);
    return {
        len,
        sMin,
        sMax,
        overlap,
        nearMin,
        nearMax,
        nearOverlap,
        samples,
        parallelMax,
        required,
        step,
        // 差得比一个采样步长还少 → 真值可能够，只是采样看不出来
        resolvable: overlap + step >= required,
    };
}
/**
 * 从街道几何算出全部贴合点（画法地块走这条）。
 * block 模式用 exact.ts 的精确构造，两者互为校验（docs/A R3）。
 */
export function findAttachments(ring, nets) {
    const out = [];
    if (ring.length < 3)
        return out;
    const n = ring.length;
    for (const net of nets) {
        const { pts, ts } = sampleNet(net);
        if (pts.length < 2)
            continue;
        const width = Math.max(0, net.width);
        // ── 穿越：真正的交点（去重阈值 1 m，与 C# 一致）
        const crossings = [];
        for (let i = 0; i < n; i++) {
            const a = ring[i];
            const b = ring[(i + 1) % n];
            for (let k = 0; k + 1 < pts.length; k++) {
                const hit = segmentIntersection(a, b, pts[k], pts[k + 1]);
                if (!hit)
                    continue;
                const at = lerp2(a, b, hit.s);
                if (crossings.some((p) => dist(p, at) < 1))
                    continue;
                crossings.push(at);
                const dir = norm(sub(pts[k + 1], pts[k]));
                out.push({
                    kind: 'roadCrossing',
                    externalId: net.externalId,
                    externalT: ts[k] + (ts[k + 1] - ts[k]) * hit.u,
                    position: at,
                    direction: dir,
                    roadWidth: width,
                    ringSegment: i,
                    ringT: hit.s,
                });
            }
        }
        // ── 沿边：平行地贴着某条边跑
        for (let i = 0; i < n; i++) {
            const a = ring[i];
            const b = ring[(i + 1) % n];
            const p = probeAlong(net, a, b);
            if (p.len < 1)
                continue;
            if (p.sMax < p.sMin)
                continue;
            if (p.overlap < p.required)
                continue;
            const sMid = (p.sMin + p.sMax) * 0.5;
            const mid = lerp2(a, b, sMid);
            // 取离中点最近的采样点，用它邻域的切向当方向。
            //
            // 平局要**定向**地选前一个：mid 落在两个采样点正中间是常态（边长正好是
            // 4 m 的整数倍时），此时 C# 的 `d < bestD` 严格小于会保留先出现的那个。
            // 我们这里用 double，距离算出来会有一点点差异，有时会让后一个"稍微更近"，
            // 于是外插参数从 0.48 跳成 0.52（差一格采样 = 4 m）。
            // 加 1 µm 的死区把这类浮点噪声压掉，同时也把 TS 与 C# 对齐。
            const TIE_EPS = 1e-6;
            let best = 0;
            let bestD = Infinity;
            for (let k = 0; k < pts.length; k++) {
                const d = dist(pts[k], mid);
                if (d < bestD - TIE_EPS) {
                    bestD = d;
                    best = k;
                }
            }
            const b0 = Math.max(0, best - 1);
            const b1 = Math.min(pts.length - 1, best + 1);
            const tdir = norm(sub(pts[b1], pts[b0]));
            out.push({
                kind: 'roadAlongEdge',
                externalId: net.externalId,
                externalT: ts[best],
                position: mid,
                direction: tdir,
                roadWidth: width,
                ringSegment: i,
                ringT: sMid,
            });
        }
    }
    return out;
}
/** 逐边扫描，给出"这条边到底临不临街"的证据，而不仅仅是一个布尔。 */
export function surveyEdges(ring, nets) {
    const n = ring.length;
    const result = [];
    for (let i = 0; i < n; i++) {
        result.push({ seg: i, length: dist(ring[i], ring[(i + 1) % n]), along: [], crossed: [], nearMiss: null });
    }
    for (const net of nets) {
        const { pts } = sampleNet(net);
        if (pts.length < 2)
            continue;
        for (let i = 0; i < n; i++) {
            const a = ring[i];
            const b = ring[(i + 1) % n];
            const p = probeAlong(net, a, b);
            if (p.len < 1)
                continue;
            if (p.sMax >= p.sMin) {
                if (p.overlap >= p.required) {
                    result[i].along.push({
                        netId: net.externalId,
                        width: net.width,
                        overlap: p.overlap,
                        parallel: p.parallelMax,
                        sMid: (p.sMin + p.sMax) * 0.5,
                    });
                }
                else if (!result[i].nearMiss) {
                    result[i].nearMiss = {
                        netId: net.externalId,
                        parallel: p.parallelMax,
                        overlap: p.overlap,
                        required: p.required,
                        resolvable: p.resolvable,
                    };
                }
            }
            else if (p.nearMax >= p.nearMin && !result[i].nearMiss) {
                result[i].nearMiss = {
                    netId: net.externalId,
                    parallel: p.parallelMax,
                    overlap: p.nearOverlap,
                    required: p.required,
                    resolvable: p.resolvable,
                };
            }
            for (let k = 0; k + 1 < pts.length; k++) {
                if (segmentIntersection(a, b, pts[k], pts[k + 1]) && !result[i].crossed.includes(net.externalId)) {
                    result[i].crossed.push(net.externalId);
                }
            }
        }
    }
    for (const r of result)
        r.along.sort((x, y) => y.overlap - x.overlap);
    return result;
}
/** 折线 → 每段一条贝塞尔（直线时控制点在 1/3、2/3）。 */
export function netFromSegment(id, a, b, width, opts = {}) {
    const third = [a[0] + (b[0] - a[0]) / 3, a[1] + (b[1] - a[1]) / 3];
    const twoThird = [a[0] + ((b[0] - a[0]) * 2) / 3, a[1] + ((b[1] - a[1]) * 2) / 3];
    return {
        externalId: id,
        a,
        b: third,
        c: twoThird,
        d: b,
        width,
        category: opts.category ?? 'road',
        hasSidewalk: opts.hasSidewalk ?? true,
        isOneWay: opts.isOneWay ?? false,
        hasBikeLane: opts.hasBikeLane ?? false,
        isHighway: opts.isHighway ?? false,
        speedLimit: opts.speedLimit ?? 50,
    };
}
/** 街道在 t 处的单位切向；**直接求贝塞尔**，不要拿采样点插值（会差一格 ≈4 m）。 */
export function netPointAt(net, t) {
    return bezier(net.a, net.b, net.c, net.d, t);
}
/**
 * 采样步长（米）。
 *
 * C# 的 Sample() 用 `clamp(ceil(近似长度/4), 1, 64)` 个**分段**，所以真实步长是
 * 近似长度/steps，不一定是 4。这个值决定了"沿边重叠"能被分辨到多细：
 * 离散采样算出来的重叠，最大会**少算一个步长**。R3 用它判断
 * "到底是真不临街，还是采样分辨率不够"。
 */
export function samplingStep(net) {
    const approx = approxBezierLength(net);
    const steps = Math.min(64, Math.max(1, Math.ceil(approx / 4)));
    return approx / steps;
}
function approxBezierLength(net) {
    return dist(net.a, net.b) + dist(net.b, net.c) + dist(net.c, net.d);
}
/** 街道在 t 处的单位切向。 */
export function netTangent(net, t = 0.5) {
    const { pts, ts } = sampleNet(net);
    let i = 0;
    for (let k = 0; k < ts.length; k++)
        if (ts[k] <= t)
            i = k;
    const a = pts[Math.max(0, i - 1)];
    const b = pts[Math.min(pts.length - 1, i + 1)];
    return norm(sub(b, a));
}
