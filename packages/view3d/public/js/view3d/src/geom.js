// 二维轮廓 → three 几何体。
//
// 为什么手写而不用 ExtrudeGeometry：
//   1. ExtrudeGeometry 要先做 Shape + 三角化，绕序错了会**静默**翻面（从下面看不见）；
//   2. 楼块要硬边法线，索引几何共享顶点会把侧面磨圆，看起来像果冻；
//   3. 我们只需要"多边形挤出成柱体"这一件事，手写反而短。
//
// 统一约定：入参 ring 一律先规整成**逆时针**（从 +Y 往下看），否则顶/底面法线会反。
import { ccw, sampleBezier } from "../../plan-ir/src/geom.js";
/** 一条闭合多边形环 → 上下两个平面 + 侧墙，硬边法线。 */
export function prism(ring, y0, y1) {
    const r = ccw(ring);
    const n = r.length;
    const pos = [];
    const idx = [];
    for (const p of r)
        pos.push(p[0], y0, p[1]);
    for (const p of r)
        pos.push(p[0], y1, p[1]);
    const contour = r.map((p) => new THREE.Vector2(p[0], p[1]));
    const tris = THREE.ShapeUtils.triangulateShape(contour, []);
    // 绕序：在 (x,z) 里逆时针的多边形，按右手法则法线指向 **-Y**（因为 y 轴朝上、
    // z 轴朝"屏幕外"时，x-z 平面的正方向与常见的 x-y 平面相反）。
    // 第一版直接把三角化结果当顶面用，结果顶面法线朝下 —— 楼顶一片死黑，而侧面正常。
    // 所以：顶面反向一次（法线 +Y），底面用原序（法线 -Y）。
    for (const t of tris)
        idx.push(n + t[0], n + t[2], n + t[1]);
    for (const t of tris)
        idx.push(t[0], t[1], t[2]);
    // 侧墙：每条边一个四边形
    for (let i = 0; i < n; i++) {
        const j = (i + 1) % n;
        idx.push(i, j, n + j, i, n + j, n + i);
    }
    return finishIndexed(pos, idx);
}
/** 单个平面多边形（铺装、草地、水面）。法线朝 +Y —— 地面要能接住光。 */
export function flatRing(ring, y) {
    const r = ccw(ring);
    const pos = [];
    for (const p of r)
        pos.push(p[0], y, p[1]);
    const contour = r.map((p) => new THREE.Vector2(p[0], p[1]));
    const tris = THREE.ShapeUtils.triangulateShape(contour, []);
    const idx = [];
    // 同 prism 的顶面：反向才朝 +Y
    for (const t of tris)
        idx.push(t[0], t[2], t[1]);
    return finishIndexed(pos, idx);
}
/**
 * 中心线 → 带状面（街道、人行道、车道）。
 *
 * 每个采样点按"前后点连线"求切线，再取法向偏移 ±w/2。端头直接平切，
 * 不封口——街道端头要么接别的街道要么在轮廓外，看得见封口反而更假。
 */
export function ribbon(centerline, width, y) {
    const pts = centerline;
    if (pts.length < 2)
        return emptyGeometry();
    const half = width / 2;
    const pos = [];
    const idx = [];
    for (let i = 0; i < pts.length; i++) {
        const prev = pts[Math.max(0, i - 1)];
        const next = pts[Math.min(pts.length - 1, i + 1)];
        let dx = next[0] - prev[0];
        let dz = next[1] - prev[1];
        const len = Math.hypot(dx, dz) || 1;
        dx /= len;
        dz /= len;
        // 左法向
        const nx = -dz;
        const nz = dx;
        pos.push(pts[i][0] + nx * half, y, pts[i][1] + nz * half);
        pos.push(pts[i][0] - nx * half, y, pts[i][1] - nz * half);
    }
    for (let i = 0; i < pts.length - 1; i++) {
        const a = i * 2;
        // 绕序同上：三角带也要让法线朝 +Y，否则整条街背面朝上、暗成一条黑带
        idx.push(a, a + 2, a + 3, a, a + 3, a + 1);
    }
    return finishIndexed(pos, idx);
}
/**
 * 三次贝塞尔 → 采样中心线。
 *
 * 直接复用 plan-ir 的 `sampleBezier`：它按"弧长估 / 4 m、夹到 1..64 段"采样，
 * 和 C# 的 `AttachmentFinder.Sample` 同分布。渲染也用它有两个好处——
 * 画出来的折线就是判定用的那条折线，不会出现"看着贴上了、判没过"；
 * 且密度天然自适应（6 m 的小路 2 段，200 m 的大道 50 段）。
 */
export function bezierOutline(a, b, c, d) {
    const { pts } = sampleBezier(a, b, c, d);
    if (pts.length >= 2)
        return pts;
    return [a, d];
}
/** 把索引几何转成非索引（每三角形独立顶点）→ 硬边法线。 */
function finishIndexed(pos, idx) {
    const g = new THREE.BufferGeometry();
    g.setAttribute('position', new THREE.Float32BufferAttribute(pos, 3));
    g.setIndex(idx);
    const flat = g.toNonIndexed();
    g.dispose();
    flat.computeVertexNormals();
    return flat;
}
export function emptyGeometry() {
    const g = new THREE.BufferGeometry();
    g.setAttribute('position', new THREE.Float32BufferAttribute([], 3));
    return g;
}
/** 多边形的轴对齐包围盒（世界 XZ），用于统计与相机取景。 */
export function ringBounds(rings) {
    let minX = Infinity;
    let minZ = Infinity;
    let maxX = -Infinity;
    let maxZ = -Infinity;
    for (const r of rings) {
        for (const p of r) {
            if (p[0] < minX)
                minX = p[0];
            if (p[1] < minZ)
                minZ = p[1];
            if (p[0] > maxX)
                maxX = p[0];
            if (p[1] > maxZ)
                maxZ = p[1];
        }
    }
    if (!Number.isFinite(minX))
        return null;
    return { min: [minX, minZ], max: [maxX, maxZ] };
}
