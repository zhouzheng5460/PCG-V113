// SceneIR → three 对象图。**增量**，不 clear 整场。
//
// 三条纪律（docs/F §3，抄自 PCG `editor/three/realizer.ts` 抬头，那是踩过的经验）：
//   1. **只吃 SceneIR**，不读 Plan、不读生成规则 —— 语义与渲染彻底解耦；
//   2. **换轴只在这里发生一次**。我们的 IR 本来就是 (x, z) 地面 + y 高度，和 three 一致，
//      所以这里**不翻轴**。（PCG 是 (x, y) 平面，它必须翻一次；翻两次就是镜像 bug。）
//   3. **增量 patch**：拖一栋楼不该让别的楼消失再出现。
//
// 判据是 `geomKey`（scene-ir/src/types.ts）：指纹一样就复用旧 mesh，连 BufferGeometry 都不碰。
// 颜色不在指纹里，所以**改配色走 material 分支**，不重建几何 —— 这一条让"选中/悬停/换主题"
// 全都变成零几何开销。
import { geomKey } from "../../scene-ir/src/types.js";
import { flatRing, prism, ribbon } from "./geom.js";
// ─────────────────────────────────────────── 共享几何
//
// 五种实例形状，高度一律归一化成 1：于是 IR 里的 `sy` 永远就是"这东西多少米高"。
// 球用 `SphereGeometry(0.5)`（直径 1）而不是半径 1 —— 不然 `sy` 在球上含义会变一半，
// 读代码的人一定会算错一次。
const SHAPES = new Map();
function shapeGeometry(name) {
    const hit = SHAPES.get(name);
    if (hit)
        return hit;
    let g;
    switch (name) {
        case 'cone':
            g = new THREE.ConeGeometry(1, 1, 7);
            break;
        case 'cylinder':
            g = new THREE.CylinderGeometry(0.16, 0.22, 1, 5);
            break;
        case 'sphere':
            g = new THREE.SphereGeometry(0.5, 8, 6);
            break;
        case 'box':
            g = new THREE.BoxGeometry(1, 1, 1);
            break;
        case 'plane':
            g = new THREE.PlaneGeometry(1, 1);
            g.rotateX(-Math.PI / 2);
            break;
    }
    SHAPES.set(name, g);
    return g;
}
// ─────────────────────────────────────────── 材质缓存
//
// key 只包含"会改变外观"的维度。同一份 IR 里几十块铺装共用一份材质，
// draw call 不会少，但显存和材质切换会明显少。
const matCache = new Map();
const selCache = new Map();
function baseMat(color, opacity) {
    const key = `${color}|${opacity}`;
    let m = matCache.get(key);
    if (!m) {
        m = new THREE.MeshStandardMaterial({
            color,
            roughness: 0.92,
            metalness: 0,
            transparent: opacity < 1,
            opacity,
            flatShading: false,
        });
        matCache.set(key, m);
    }
    return m;
}
/** 选中材质：同色 + 黄色自发光。**注意它也是缓存的** —— 选中多少次都只有一份。 */
function selectMat(color) {
    const key = `${color}`;
    let m = selCache.get(key);
    if (!m) {
        m = new THREE.MeshStandardMaterial({
            color,
            roughness: 0.7,
            metalness: 0,
            emissive: new THREE.Color(0xffc83d),
            emissiveIntensity: 0.42,
        });
        selCache.set(key, m);
    }
    return m;
}
const matKeyOf = (n) => {
    if (n.type === 'line')
        return `line|${n.color}|${n.opacity ?? 1}|${n.dashed ? 'd' : '-'}`;
    if (n.type === 'instances')
        return `inst|${n.color}`;
    const op = n.type === 'surface' ? (n.opacity ?? 1) : 1;
    return `${n.color}|${op}`;
};
// ─────────────────────────────────────────── Realizer
export class Realizer {
    group;
    views = new Map();
    selection = null;
    last = { built: 0, reused: 0, removed: 0, total: 0, ms: 0 };
    constructor() {
        this.group = new THREE.Group();
        this.group.name = 'realizer';
    }
    get stats() {
        return this.last;
    }
    /** 当前选中的节点 id。 */
    get selected() {
        return this.selection;
    }
    /** 节点数（不是对象数：一个 instances 节点可能是一万个实例）。 */
    get nodeCount() {
        return this.views.size;
    }
    /** 当前所有节点指纹（确定性校验用：同一方案必须逐字节一致）。 */
    keys() {
        return [...this.views.entries()].map(([id, v]) => `${id}@${v.key}`).sort();
    }
    // ───────────────────────────── 构建
    build(node, key) {
        const mk = matKeyOf(node);
        if (node.type === 'line') {
            const pts = node.path.map((p) => new THREE.Vector3(p[0], node.y, p[1]));
            const geo = new THREE.BufferGeometry().setFromPoints(node.closed && pts.length > 2 ? [...pts, pts[0]] : pts);
            const mat = node.dashed
                ? new THREE.LineDashedMaterial({
                    color: node.color,
                    dashSize: 1.6,
                    gapSize: 1.2,
                    transparent: (node.opacity ?? 1) < 1,
                    opacity: node.opacity ?? 1,
                })
                : new THREE.LineBasicMaterial({
                    color: node.color,
                    transparent: (node.opacity ?? 1) < 1,
                    opacity: node.opacity ?? 1,
                });
            const obj = new THREE.Line(geo, mat);
            if (node.dashed)
                obj.computeLineDistances();
            obj.name = node.id;
            return { id: node.id, node, key, matKey: mk, obj, dispose: () => { geo.dispose(); mat.dispose(); } };
        }
        if (node.type === 'instances') {
            const geo = shapeGeometry(node.shape);
            const mat = baseMat(node.color, 1);
            const inst = new THREE.InstancedMesh(geo, mat, Math.max(1, node.items.length));
            inst.name = node.id;
            inst.castShadow = node.castShadow === true;
            inst.receiveShadow = node.shape === 'plane';
            const dummy = new THREE.Object3D();
            node.items.forEach((it, i) => {
                // IR 的 y 是**底面**，InstancedMesh 的变换原点在几何中心 → 加半个高度
                dummy.position.set(it.x, it.y + it.sy / 2, it.z);
                dummy.rotation.set(0, it.yaw, 0);
                dummy.scale.set(it.sx, it.sy, it.sz);
                dummy.updateMatrix();
                inst.setMatrixAt(i, dummy.matrix);
            });
            inst.count = node.items.length;
            inst.instanceMatrix.needsUpdate = true;
            inst.frustumCulled = false;
            return { id: node.id, node, key, matKey: mk, obj: inst, mesh: inst, dispose: () => inst.dispose() };
        }
        // 剩下的三种都是"面/体"，几何各造一份
        let geo;
        if (node.type === 'extrude')
            geo = prism(node.ring, node.y0, node.y1, node.holes);
        else if (node.type === 'surface')
            geo = flatRing(node.ring, node.y, node.holes);
        else
            geo = ribbon(node.path, node.width, node.y);
        const opacity = node.type === 'surface' ? (node.opacity ?? 1) : 1;
        const mat = baseMat(node.color, opacity);
        const mesh = new THREE.Mesh(geo, mat);
        mesh.name = node.id;
        mesh.castShadow = node.type === 'extrude' && node.layer === 'massing';
        mesh.receiveShadow = true;
        if (node.type === 'extrude')
            mesh.userData = { ...node.props, nodeId: node.id };
        // 楼块自带一圈棱线，平时隐藏：选中时只翻 `visible`，不造几何。
        let edges = null;
        if (node.type === 'extrude' && node.layer === 'massing') {
            const eg = new THREE.EdgesGeometry(geo, 24);
            edges = new THREE.LineSegments(eg, new THREE.LineBasicMaterial({ color: 0xffc83d, transparent: true, opacity: 0.95 }));
            edges.visible = false;
            edges.renderOrder = 20;
            mesh.add(edges);
        }
        return {
            id: node.id,
            node,
            key,
            matKey: mk,
            obj: mesh,
            mesh,
            edges,
            dispose: () => {
                geo.dispose();
                if (edges) {
                    edges.geometry.dispose();
                    edges.material.dispose();
                }
            },
        };
    }
    destroy(v) {
        this.group.remove(v.obj);
        v.dispose();
        this.views.delete(v.id);
    }
    // ───────────────────────────── 更新
    /**
     * 把 IR 同步到对象图。**只重建指纹变了的节点** —— 这是整个 F3 的核心。
     *
     * 返回值里的 `built/reused/removed` 不是调试信息，是**验收数据**：
     * 关一个图层应该是 `removed = 那一层`、`reused = 其余全部`；
     * 选中一栋楼应该是 `built = 0, removed = 0`。
     */
    update(ir) {
        const t0 = performance.now();
        let built = 0;
        let reused = 0;
        let removed = 0;
        const live = new Set();
        for (const node of ir.nodes) {
            live.add(node.id);
            const key = geomKey(node);
            const prev = this.views.get(node.id);
            if (prev && prev.key === key && prev.node.type === node.type) {
                // 形状没变：只更新元数据与材质（配色改了不该重建几何）
                prev.node = node;
                const mk = matKeyOf(node);
                if (prev.matKey !== mk && prev.mesh && node.type !== 'instances') {
                    prev.matKey = mk;
                    const op = node.type === 'surface' ? (node.opacity ?? 1) : 1;
                    prev.mesh.material = baseMat(node.color, op);
                }
                if (node.type === 'extrude' && prev.mesh)
                    prev.mesh.userData = { ...node.props, nodeId: node.id };
                reused++;
                continue;
            }
            if (prev) {
                this.destroy(prev);
                removed++;
            }
            const nv = this.build(node, key);
            this.views.set(nv.id, nv);
            this.group.add(nv.obj);
            built++;
        }
        for (const [id, v] of [...this.views]) {
            if (!live.has(id)) {
                this.destroy(v);
                removed++;
            }
        }
        this.applySelection();
        this.last = { built, reused, removed, total: this.views.size, ms: performance.now() - t0 };
        return this.last;
    }
    // ───────────────────────────── 选中（零几何开销）
    setSelection(id) {
        if (this.selection === id)
            return;
        this.selection = id;
        this.applySelection();
    }
    applySelection() {
        for (const v of this.views.values()) {
            const on = v.id === this.selection;
            if (v.node.type === 'line' || v.node.type === 'instances')
                continue;
            if (v.mesh) {
                const color = v.node.color;
                if (on)
                    v.mesh.material = selectMat(color);
                else {
                    const op = v.node.type === 'surface' ? (v.node.opacity ?? 1) : 1;
                    v.mesh.material = baseMat(color, op);
                }
            }
            if (v.edges)
                v.edges.visible = on;
        }
    }
    // ───────────────────────────── 查询
    /** 可拾取对象：只有楼块。点中铺装/树没有意义。 */
    pickables() {
        const out = [];
        for (const v of this.views.values())
            if (v.node.layer === 'massing' && v.mesh)
                out.push(v.mesh);
        return out;
    }
    nodeBox(id) {
        const v = this.views.get(id);
        if (!v)
            return null;
        return new THREE.Box3().setFromObject(v.obj);
    }
    dispose() {
        for (const v of [...this.views.values()])
            this.destroy(v);
        for (const m of matCache.values())
            m.dispose();
        for (const m of selCache.values())
            m.dispose();
        matCache.clear();
        selCache.clear();
    }
}
