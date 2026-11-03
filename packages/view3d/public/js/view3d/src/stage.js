// 三维舞台：渲染器、相机（正交为主）、光照、轨道控制、拾取。
//
// 为什么主视图用**正交**而不是透视：
//   透视会把远处 30 m 的楼看成 20 m、把近处 16 m 的看成 24 m —— 量感不可信。
//   我们要靠画面判断"这栋楼几层、比旁边高多少"，那就只能正交。
//   （这条来自 PCG 原项目 editor/three/stage3d.ts 抬头，是踩过的经验，不是偏好。）
//
// 为什么不引 OrbitControls：它是 examples/jsm 里的 ESM，还得再解决一次 "import from 'three'"
// 的别名问题；而我们要的只有"左键转、滚轮缩、右键平移"，四十行就够。
/** 五个视图：等轴/顶/前/右 全正交，透视单独一个。方位角 az 与仰角 el 用角度写，好对照。 */
const PRESETS = {
    iso: { az: 45, el: 38, ortho: true },
    top: { az: 0, el: 89.2, ortho: true },
    front: { az: 0, el: 6, ortho: true },
    right: { az: 90, el: 6, ortho: true },
    persp: { az: 42, el: 30, ortho: false },
};
const D2R = Math.PI / 180;
/** 只有画面真的变了才重绘——不然 rAF 永远满速跑，笔记本风扇会响。 */
export class Stage {
    scene;
    renderer;
    root;
    /** 实际渲染帧数：外部探针用它区分"没渲染"和"渲染了但取景偏了"。 */
    frames = 0;
    preset = 'iso';
    host;
    canvasHost;
    sun;
    grid;
    orthoCam;
    perspCam;
    useOrtho = true;
    aspect = 1;
    target = new THREE.Vector3(0, 0, 0);
    /** 正交：视野半高（世界米）。透视：与目标的距离。两者共用缩放手感。 */
    viewSize = 160;
    dist = 260;
    /** 方位角（绕 Y）与仰角（自水平面向上）；比 phi/theta 好对照预设。 */
    az = -45;
    el = 38;
    dirty = true;
    raf = 0;
    drag = null;
    /** 上一次 frame() 的包围盒 —— 换视图时按新视角重新配视野，不然顶视会空一大半。 */
    lastBounds = null;
    constructor(host) {
        this.host = host;
        this.scene = new THREE.Scene();
        this.scene.background = new THREE.Color(0x0e1216);
        this.scene.fog = new THREE.Fog(0x0e1216, 1400, 3200);
        this.orthoCam = new THREE.OrthographicCamera(-1, 1, 1, -1, 1, 9000);
        this.perspCam = new THREE.PerspectiveCamera(45, 1, 1, 9000);
        this.renderer = new THREE.WebGLRenderer({ antialias: true });
        this.renderer.setPixelRatio(Math.min(devicePixelRatio || 1, 2));
        this.renderer.shadowMap.enabled = true;
        this.renderer.shadowMap.type = THREE.PCFSoftShadowMap;
        this.canvasHost = document.createElement('div');
        this.canvasHost.className = 'canvas3d';
        this.canvasHost.appendChild(this.renderer.domElement);
        host.appendChild(this.canvasHost);
        // 光照：半球光给环境底色，平行光给体型明暗。没有平行光时所有盒子看起来一样平。
        this.scene.add(new THREE.HemisphereLight(0xc6d8ff, 0x33302b, 1.3));
        const sun = new THREE.DirectionalLight(0xfff2e0, 2.2);
        sun.position.set(180, 320, 140);
        sun.castShadow = true;
        sun.shadow.mapSize.set(2048, 2048);
        const sc = sun.shadow.camera;
        sc.near = 10;
        sc.far = 1600;
        sc.left = -420;
        sc.right = 420;
        sc.top = 420;
        sc.bottom = -420;
        sun.shadow.bias = -0.0008;
        this.scene.add(sun, sun.target);
        this.sun = sun;
        this.root = new THREE.Group();
        this.scene.add(this.root);
        this.grid = new THREE.GridHelper(2400, 120, 0x2f3d4d, 0x1c2531);
        this.grid.material.transparent = true;
        this.grid.material.opacity = 0.32;
        this.grid.position.y = -0.02;
        this.scene.add(this.grid);
        this.bindOrbit();
        this.observeResize();
        this.loop();
    }
    /** 当前生效的相机（正交 / 透视二选一）。 */
    get camera() {
        return this.useOrtho ? this.orthoCam : this.perspCam;
    }
    /**
     * 正交视野半高（世界米）。
     * 量楼高时要用它把"屏幕上多少像素"换算回"世界多少米"：
     * pxPerMeter = 画布高 / (2 × orthoSize)。正交下这个比值处处相等，所以量得准。
     */
    get orthoSize() {
        return this.viewSize;
    }
    /** 画布 CSS 像素高度 —— 和 orthoSize 配对使用。 */
    get canvasHeight() {
        return this.renderer.domElement.clientHeight || 1;
    }
    clear() {
        while (this.root.children.length) {
            const c = this.root.children.pop();
            disposeTree(c);
        }
        this.markDirty();
    }
    markDirty() {
        this.dirty = true;
    }
    /** 切视图预设。正交三视（顶/前/右）同时把角度摆正。 */
    setPreset(p) {
        this.preset = p;
        const cfg = PRESETS[p];
        this.useOrtho = cfg.ortho;
        this.az = -cfg.az;
        this.el = cfg.el;
        this.applyCamera();
        // 正交视图按"这个视角下的宽高"重新配视野：
        // 顶视要按地块的长宽，正视要按"长 × 最高楼"，否则同一个 viewSize 套到五个视角
        // 就会出现"等轴刚好、正视缩成一条"。
        if (this.useOrtho && this.lastBounds)
            this.fitToView(this.lastBounds);
        this.renderNow();
    }
    /** 按当前相机视角把包围盒塞满画面（只对正交有意义）。 */
    fitToView(b) {
        const cam = this.camera;
        const inv = cam.matrixWorldInverse;
        const v = new THREE.Vector3();
        let ex = 0;
        let ey = 0;
        for (let i = 0; i < 8; i++) {
            v.set(i & 1 ? b.max[0] : b.min[0], i & 2 ? b.max[1] : b.min[1], i & 4 ? b.max[2] : b.min[2]);
            v.applyMatrix4(inv);
            ex = Math.max(ex, Math.abs(v.x));
            ey = Math.max(ey, Math.abs(v.y));
        }
        this.viewSize = Math.max(ey, ex / Math.max(this.aspect, 0.2), 8) * 1.1;
        this.applyCamera();
    }
    /** 按包围盒把相机拉到能看全的距离。 */
    frame(b) {
        this.lastBounds = b;
        const cx = (b.min[0] + b.max[0]) / 2;
        const cz = (b.min[2] + b.max[2]) / 2;
        const sx = b.max[0] - b.min[0];
        const sz = b.max[2] - b.min[2];
        const radius = Math.max(Math.hypot(sx, sz) / 2, 40);
        const top = Math.max(10, b.max[1]);
        this.target.set(cx, top * 0.3, cz);
        this.viewSize = radius * 1.15;
        this.dist = radius * 2.2;
        const cfg = PRESETS[this.preset];
        this.useOrtho = cfg.ortho;
        this.az = -cfg.az;
        this.el = cfg.el;
        // 阴影相机跟着地块走，否则大地块只有中心有影子
        this.sun.position.set(cx + 180, 320, cz + 140);
        this.sun.target.position.set(cx, 0, cz);
        const sc = this.sun.shadow.camera;
        sc.left = -radius * 1.5;
        sc.right = radius * 1.5;
        sc.top = radius * 1.5;
        sc.bottom = -radius * 1.5;
        sc.far = radius * 14 + 600;
        sc.updateProjectionMatrix();
        this.grid.position.set(cx, -0.02, cz);
        this.applyCamera();
        // 正交：用投影后的真实宽高再收一次视野，保证 rotate 到任何预设都装得下
        if (this.useOrtho)
            this.fitToView(b);
        // 取景后立刻画一帧：无头浏览器里 rAF 可能被节流，等下一帧截图会拿到空白。
        this.renderNow();
    }
    /** 相机方向（单位向量，从目标指向相机）。 */
    dir() {
        const e = this.el * D2R;
        const a = this.az * D2R;
        return new THREE.Vector3(Math.cos(e) * Math.cos(a), Math.sin(e), Math.cos(e) * Math.sin(a));
    }
    applyCamera() {
        const d = this.dir();
        const far = Math.max(this.viewSize * 12, 800);
        const dist = this.useOrtho ? far * 0.5 : this.dist;
        const cam = this.camera;
        cam.position.copy(this.target).addScaledVector(d, dist);
        cam.lookAt(this.target);
        if (this.useOrtho) {
            cam.left = -this.viewSize * this.aspect;
            cam.right = this.viewSize * this.aspect;
            cam.top = this.viewSize;
            cam.bottom = -this.viewSize;
            cam.near = 1;
            cam.far = far * 2.5;
            cam.updateProjectionMatrix();
        }
        cam.updateMatrixWorld();
        this.markDirty();
    }
    bindOrbit() {
        const el = this.renderer.domElement;
        el.addEventListener('contextmenu', (e) => e.preventDefault());
        el.addEventListener('pointerdown', (e) => {
            e.target.setPointerCapture?.(e.pointerId);
            this.drag = { x: e.clientX, y: e.clientY, button: e.button };
        });
        el.addEventListener('pointerup', (e) => {
            e.target.releasePointerCapture?.(e.pointerId);
            this.drag = null;
        });
        el.addEventListener('pointercancel', () => {
            this.drag = null;
        });
        el.addEventListener('pointermove', (e) => {
            const d = this.drag;
            if (!d)
                return;
            const dx = e.clientX - d.x;
            const dy = e.clientY - d.y;
            d.x = e.clientX;
            d.y = e.clientY;
            // 右键 / 中键 / Shift+左键 = 平移；否则旋转
            if (d.button === 2 || d.button === 1 || e.shiftKey) {
                const k = this.viewSize * 0.0022;
                const right = new THREE.Vector3().copy(this.dir()).cross(new THREE.Vector3(0, 1, 0)).normalize();
                const up = new THREE.Vector3().copy(right).cross(this.dir()).normalize();
                this.target.addScaledVector(right, -dx * k).addScaledVector(up, dy * k);
            }
            else {
                // 仰角不许越过正上方：lookAt 在那一点会退化
                this.az -= dx * 0.35;
                this.el = Math.min(89.4, Math.max(-10, this.el + dy * 0.3));
            }
            this.applyCamera();
        });
        el.addEventListener('wheel', (e) => {
            e.preventDefault();
            const k = Math.exp(e.deltaY * 0.0011);
            this.viewSize = Math.min(4000, Math.max(6, this.viewSize * k));
            this.dist = Math.min(8000, Math.max(12, this.dist * k));
            this.applyCamera();
        }, { passive: false });
    }
    observeResize() {
        const fit = () => {
            const w = this.host.clientWidth || 1;
            const h = this.host.clientHeight || 1;
            this.renderer.setSize(w, h, false);
            const cv = this.renderer.domElement;
            cv.style.width = '100%';
            cv.style.height = '100%';
            this.aspect = w / h;
            this.perspCam.aspect = this.aspect;
            this.perspCam.updateProjectionMatrix();
            this.applyCamera();
            this.renderNow();
        };
        new ResizeObserver(fit).observe(this.host);
        fit();
    }
    loop = () => {
        this.raf = requestAnimationFrame(this.loop);
        if (!this.dirty)
            return;
        this.renderNow();
    };
    renderNow() {
        this.frames++;
        this.renderer.render(this.scene, this.camera);
        this.dirty = false;
    }
    /** 屏幕坐标 → 场景对象。正交相机同样支持 Raycaster，用 NDC 换算即可。 */
    pick(clientX, clientY, objects) {
        const cv = this.renderer.domElement;
        const r = cv.getBoundingClientRect();
        const ndc = new THREE.Vector2(((clientX - r.left) / r.width) * 2 - 1, -((clientY - r.top) / r.height) * 2 + 1);
        const rc = new THREE.Raycaster();
        rc.setFromCamera(ndc, this.camera);
        const hits = rc.intersectObjects(objects, false);
        return hits.length ? hits[0].object : null;
    }
    dispose() {
        cancelAnimationFrame(this.raf);
        this.clear();
        this.renderer.dispose();
        this.canvasHost.remove();
    }
}
/** 释放一棵子树的几何与材质，避免反复切换方案时显存涨。 */
function disposeTree(o) {
    o.traverse?.((c) => {
        c.geometry?.dispose?.();
        const m = c.material;
        if (Array.isArray(m))
            m.forEach((x) => x.dispose?.());
        else
            m?.dispose?.();
    });
    if (o.parent)
        o.parent.remove(o);
}
