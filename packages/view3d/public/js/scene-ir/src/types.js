// SceneIR —— 三维的**唯一输入**，纯数据，不 import 任何 three 类型。
//
// 为什么要有这一层（docs/F §3）：
//   现在 Plan IR 直接变成 three 对象图，于是"要不要重建"这件事只能靠"整场 clear 再重建"来回答。
//   插进 SceneIR 之后，每个节点带一个**几何指纹 `geomKey`**，realizer 才有东西可比：
//   指纹没变 → 复用旧 mesh，连 BufferGeometry 都不碰。
//
// 三条纪律（抄自 PCG `core/sceneIR.ts` + `editor/three/realizer.ts` 抬头）：
//   1. **只吃数据**：这里的类型不许出现 Vector3 / Mesh / Material；
//   2. **换轴只在一个地方发生**：我们本来就是 (x, z) 平面 + y 朝上，realizer 不再翻轴；
//   3. **指纹只包含影响几何的字段**：改颜色、改拾取元数据不该触发重建。
//
// 坐标系：世界 (x, z) 是地面平面，y 是高度，单位米。和 Plan IR 完全一致 —— 不翻轴就不会有
// "看着差不多但朝向全反"那类镜像 bug（台账 Q-004 就是那个坑）。
// ─────────────────────────────────────────── 几何指纹
const r = (v) => Math.round(v * 100) / 100;
/** FNV-1a over a number stream. 只用来比对"变没变"，不要求抗碰撞。 */
function mix(h, v) {
    const n = Math.round(v * 100) | 0;
    h ^= n;
    return Math.imul(h, 16777619) >>> 0;
}
/** 环指纹。**必须包含点数**：同样一串坐标截断成两点和三点的形状完全不同。 */
export function ringKey(ring) {
    let h = 2166136261;
    for (const p of ring) {
        h = mix(h, p[0] * 100);
        h = mix(h, p[1] * 100);
    }
    return `${ring.length}:${h}`;
}
function itemsKey(items) {
    let h = 2166136261;
    for (const it of items) {
        h = mix(h, it.x * 100);
        h = mix(h, it.z * 100);
        h = mix(h, it.y * 100);
        h = mix(h, it.sy * 100);
        h = mix(h, it.sx * 100);
    }
    return `${items.length}:${h}`;
}
/**
 * 几何指纹。**只有影响几何的字段才进来**：
 *   · 颜色不进 —— 改配色不该让整城重建；
 *   · `props` 不进 —— 面板文字变了不该让楼重建；
 *   · 图层不进 —— 图层是"在不在"，不是"长什么样"。
 */
export function geomKey(n) {
    switch (n.type) {
        case 'extrude':
            return `x|${r(n.y0)}|${r(n.y1)}|${ringKey(n.ring)}`;
        case 'surface':
            return `s|${r(n.y)}|${ringKey(n.ring)}`;
        case 'ribbon':
            return `b|${r(n.width)}|${r(n.y)}|${ringKey(n.path)}`;
        case 'line':
            return `l|${r(n.y)}|${n.dashed ? 'd' : '-'}|${n.closed ? 'c' : '-'}|${ringKey(n.path)}`;
        case 'instances':
            return `i|${n.shape}|${itemsKey(n.items)}`;
    }
}
/** 整份 IR 的指纹（按 id 排序后拼接）。用于"同一方案两次打开必须一致"的确定性校验。 */
export function irKey(ir) {
    return ir.nodes
        .map((n) => `${n.id}@${geomKey(n)}`)
        .sort()
        .join('|');
}
