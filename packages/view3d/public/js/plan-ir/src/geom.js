// 平面几何基础算子。全部米制，Vec2 = [x, y]，与 Placemaker 的 [x, z] 一一对应。
// 只放"两边都要用"的东西：环的面积/绕序/自交、线段相交、最近点、贝塞尔。
//
// 约定（与 docs/A 一致）：
//   · 面积为正 = 逆时针（CCW）
//   · 环隐式闭合，末点不得等于首点
//   · 所有比较带 EPS，避免 float64/float32 在临界值上的分歧（见台账 D-016）
export const EPS = 1e-9;
/** 几何量化精度：1 mm，与 PCG boolean.ts 的量化口径一致。 */
export const QUANT = 1e-3;
export const v2 = (x, y) => [x, y];
export const add = (a, b) => [a[0] + b[0], a[1] + b[1]];
export const sub = (a, b) => [a[0] - b[0], a[1] - b[1]];
export const mul = (a, k) => [a[0] * k, a[1] * k];
export const dot = (a, b) => a[0] * b[0] + a[1] * b[1];
export const cross = (a, b) => a[0] * b[1] - a[1] * b[0];
export const len = (a) => Math.hypot(a[0], a[1]);
export const dist = (a, b) => Math.hypot(a[0] - b[0], a[1] - b[1]);
export function norm(a) {
    const l = len(a);
    return l < EPS ? [0, 0] : [a[0] / l, a[1] / l];
}
/** 左法向（逆时针旋转 90°）。 */
export const perp = (a) => [-a[1], a[0]];
export const lerp2 = (a, b, t) => [a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t];
/** 逆时针为正的鞋带面积。 */
export function signedArea(ring) {
    let s = 0;
    for (let i = 0, j = ring.length - 1; i < ring.length; j = i++) {
        s += ring[j][0] * ring[i][1] - ring[i][0] * ring[j][1];
    }
    return s / 2;
}
export const area = (ring) => Math.abs(signedArea(ring));
/** 统一成逆时针（保持首点不变，只反序）。 */
export function ccw(ring) {
    const copy = ring.map((p) => [p[0], p[1]]);
    return signedArea(copy) < 0 ? copy.reverse() : copy;
}
export function isCCW(ring) {
    return signedArea(ring) > 0;
}
export function perimeter(ring) {
    let s = 0;
    for (let i = 0; i < ring.length; i++)
        s += dist(ring[i], ring[(i + 1) % ring.length]);
    return s;
}
export function centroid(ring) {
    const a = signedArea(ring);
    if (Math.abs(a) < EPS) {
        const n = ring.length || 1;
        let sx = 0;
        let sy = 0;
        for (const p of ring) {
            sx += p[0];
            sy += p[1];
        }
        return [sx / n, sy / n];
    }
    let cx = 0;
    let cy = 0;
    for (let i = 0, j = ring.length - 1; i < ring.length; j = i++) {
        const f = ring[j][0] * ring[i][1] - ring[i][0] * ring[j][1];
        cx += (ring[j][0] + ring[i][0]) * f;
        cy += (ring[j][1] + ring[i][1]) * f;
    }
    return [cx / (6 * a), cy / (6 * a)];
}
export function bounds(ring) {
    let minX = Infinity;
    let minY = Infinity;
    let maxX = -Infinity;
    let maxY = -Infinity;
    for (const p of ring) {
        if (p[0] < minX)
            minX = p[0];
        if (p[1] < minY)
            minY = p[1];
        if (p[0] > maxX)
            maxX = p[0];
        if (p[1] > maxY)
            maxY = p[1];
    }
    return { minX, minY, maxX, maxY };
}
/** 射线法。边界上的点不保证一致，调用方自己留容差。 */
export function pointInRing(p, ring) {
    let inside = false;
    for (let i = 0, j = ring.length - 1; i < ring.length; j = i++) {
        const a = ring[i];
        const b = ring[j];
        if (a[1] > p[1] !== b[1] > p[1]) {
            const x = ((b[0] - a[0]) * (p[1] - a[1])) / (b[1] - a[1]) + a[0];
            if (p[0] < x)
                inside = !inside;
        }
    }
    return inside;
}
/** 闭区间投影参数 t（夹到 [0,1]）与距离。 */
export function closestOnSegment(p, a, b) {
    const ab = sub(b, a);
    const l2 = dot(ab, ab);
    const t = l2 < EPS ? 0 : Math.min(1, Math.max(0, dot(sub(p, a), ab) / l2));
    const point = lerp2(a, b, t);
    return { t, point, dist: dist(p, point) };
}
/** 环上最近点：返回所在边序号、边内参数、最近点、距离。 */
export function closestOnRing(p, ring) {
    let best = { seg: -1, t: 0, point: ring[0], dist: Infinity };
    for (let i = 0; i < ring.length; i++) {
        const r = closestOnSegment(p, ring[i], ring[(i + 1) % ring.length]);
        if (r.dist < best.dist)
            best = { seg: i, t: r.t, point: r.point, dist: r.dist };
    }
    return best;
}
/**
 * 线段严格相交（端点接触只算一次，s ∈ [0,1)、u ∈ [0,1]）。
 * 移植自 Core/Tool/AttachmentFinder.cs:SegmentIntersection，行为保持一致。
 */
export function segmentIntersection(a, b, c, d) {
    const r = sub(b, a);
    const q = sub(d, c);
    const den = cross(r, q);
    if (Math.abs(den) < 1e-9)
        return null;
    const w = sub(c, a);
    const s = cross(w, q) / den;
    const u = cross(w, r) / den;
    if (s >= 0 && s < 1 && u >= 0 && u <= 1)
        return { s, u };
    return null;
}
/** O(n²) 自交检测。地块点数很少，够用且不会漏。 */
export function isSimple(ring) {
    const n = ring.length;
    if (n < 3)
        return false;
    for (let i = 0; i < n; i++) {
        for (let j = i + 1; j < n; j++) {
            if (j === i || (j + 1) % n === i || (i + 1) % n === j)
                continue;
            if (segmentIntersection(ring[i], ring[(i + 1) % n], ring[j], ring[(j + 1) % n]))
                return false;
        }
    }
    return true;
}
/** 三次贝塞尔。t ∈ [0,1]。 */
export function bezier(a, b, c, d, t) {
    const u = 1 - t;
    const w0 = u * u * u;
    const w1 = 3 * u * u * t;
    const w2 = 3 * u * t * t;
    const w3 = t * t * t;
    return [
        a[0] * w0 + b[0] * w1 + c[0] * w2 + d[0] * w3,
        a[1] * w0 + b[1] * w1 + c[1] * w2 + d[1] * w3,
    ];
}
/**
 * 贝塞尔采样。步长与 C# 一致：弧长估 / 4 m，夹到 1..64 段
 * （Core/Tool/AttachmentFinder.cs:Sample）。采样点必须与 C# 同分布，
 * 否则"沿边贴合"的判定会在临界地块上分叉。
 */
export function sampleBezier(a, b, c, d) {
    if (!isFinitePoint(a) || !isFinitePoint(d))
        return { pts: [], ts: [] };
    const approx = dist(a, b) + dist(b, c) + dist(c, d);
    const steps = Math.min(64, Math.max(1, Math.ceil(approx / 4)));
    const pts = [];
    const ts = [];
    for (let i = 0; i <= steps; i++) {
        const t = i / steps;
        ts.push(t);
        pts.push(bezier(a, b, c, d, t));
    }
    return { pts, ts };
}
export function isFinitePoint(p) {
    return Number.isFinite(p[0]) && Number.isFinite(p[1]);
}
export function isFiniteRing(ring) {
    return ring.length >= 3 && ring.every(isFinitePoint);
}
/** 量化到 1 mm（原地返回新值）。 */
export const q = (n) => Math.round(n / QUANT) * QUANT;
export function quantizeRing(ring) {
    return ring.map((p) => [q(p[0]), q(p[1])]);
}
/**
 * 两个多边形的对称差面积占比（0 = 完全重合，1 = 毫无交集）。
 *
 * 用栅格采样而不是布尔求交：对比的是"两个引擎的输出"，形状可能有自交、
 * 可能有一堆近似重合的边，布尔库在这种输入上会互相打架。采样法对脏数据更稳，
 * 代价是精度——200×200 网格在 100 m 量级地块上误差约 0.5%，足够判 L2 容差（2%）。
 */
export function symmetricDiffRatio(a, b, gridN = 200) {
    const asRings = Array.isArray(a[0][0]) ? a : [a];
    const bsRings = Array.isArray(b[0][0]) ? b : [b];
    const all = [...asRings, ...bsRings];
    let minX = Infinity;
    let minY = Infinity;
    let maxX = -Infinity;
    let maxY = -Infinity;
    for (const r of all) {
        const bb = bounds(r);
        minX = Math.min(minX, bb.minX);
        minY = Math.min(minY, bb.minY);
        maxX = Math.max(maxX, bb.maxX);
        maxY = Math.max(maxY, bb.maxY);
    }
    const w = maxX - minX;
    const h = maxY - minY;
    if (w <= 0 || h <= 0)
        return 0;
    const dx = w / gridN;
    const dy = h / gridN;
    let inA = 0;
    let inB = 0;
    let both = 0;
    for (let i = 0; i < gridN; i++) {
        const x = minX + (i + 0.5) * dx;
        for (let j = 0; j < gridN; j++) {
            const y = minY + (j + 0.5) * dy;
            const p = [x, y];
            const ra = asRings.some((r) => pointInRing(p, r));
            const rb = bsRings.some((r) => pointInRing(p, r));
            if (ra)
                inA++;
            if (rb)
                inB++;
            if (ra && rb)
                both++;
        }
    }
    const union = inA + inB - both;
    if (union === 0)
        return 0;
    return (union - both) / union;
}
export function frameAt(origin, dir) {
    const u = norm(dir);
    return { origin, u, v: perp(u) };
}
export function toLocal(f, p) {
    const d = sub(p, f.origin);
    return [dot(d, f.u), dot(d, f.v)];
}
export function toWorld(f, p) {
    return [f.origin[0] + f.u[0] * p[0] + f.v[0] * p[1], f.origin[1] + f.u[1] * p[0] + f.v[1] * p[1]];
}
/** 矩形环（CCW），从局部坐标构造后转回世界。 */
export function rectRing(f, u0, u1, v0, v1) {
    return [
        toWorld(f, [u0, v0]),
        toWorld(f, [u1, v0]),
        toWorld(f, [u1, v1]),
        toWorld(f, [u0, v1]),
    ];
}
