/**
 * Stage3D —— 3D 预览舞台（PRD §6 §69 §70）
 *
 * 职责边界很硬：
 * - 只**读** ProjectModel 生成 SceneIR，只**写** 选择状态回 store；
 * - 不持有任何编辑逻辑（不许在这里改几何）；
 * - 切到 2D 不停机、不 dispose：停 rAF，场景与相机原样保留（§69）。
 *
 * 相机：默认正交等轴 45°/38°，可切 顶/前/右/透视。
 * 主建筑用正交，是为了「量感」正确 —— 透视会把 30m 的楼看成 50m。
 */
import * as THREE from 'three';
import { OrbitControls } from 'three/examples/jsm/controls/OrbitControls.js';
import { Id } from '../../core/model';
import { buildSceneIR } from '../../core/sceneIR';
import { useEditor } from '../store';
import { ThreeRealizer } from './realizer';

export type ViewPreset = 'iso' | 'top' | 'front' | 'right' | 'persp';

export const PRESET_LABEL: Record<ViewPreset, string> = {
  iso: '等轴',
  top: '顶视',
  front: '前视',
  right: '右视',
  persp: '透视',
};

/** 方位角 az（0=正南看向北，90=正东看向西）+ 仰角 el */
const ANGLES: Record<ViewPreset, { az: number; el: number }> = {
  iso: { az: 45, el: 38 },
  top: { az: 0, el: 89.4 },
  front: { az: 0, el: 0 },
  right: { az: 90, el: 0 },
  persp: { az: 42, el: 30 },
};

const BG = 0x0d0f13;
// 太阳方位：three 坐标。这里对应「光从画面左上打来」，影子落到右下，最符合读图习惯
const SUN_DIR = new THREE.Vector3(-0.55, 1, -0.62).normalize();

function deg(d: number): number {
  return (d * Math.PI) / 180;
}

export class Stage3D {
  readonly canvas: HTMLCanvasElement;

  private renderer: THREE.WebGLRenderer;
  private scene = new THREE.Scene();
  private realizer = new ThreeRealizer();

  private ortho: THREE.OrthographicCamera;
  private persp: THREE.PerspectiveCamera;
  private activeCam: THREE.OrthographicCamera | THREE.PerspectiveCamera;
  private controls: OrbitControls | null = null;

  private sun: THREE.DirectionalLight;
  private ground: THREE.Mesh;
  private grid: THREE.GridHelper | null = null;
  private north: THREE.ArrowHelper;

  private raycaster = new THREE.Raycaster();
  private pointer = new THREE.Vector2();

  private container: HTMLElement | null = null;
  private ro: ResizeObserver | null = null;
  private raf = 0;
  private running = false;

  /** 相机状态（跨 2D/3D 切换保留） */
  private target = new THREE.Vector3(80, 12, -55);
  private dir = new THREE.Vector3(0.62, 0.62, 0.62).normalize();
  private radius = 120;
  /** 透视相机到目标的取景距离（由 fitBox 按 fov 解算） */
  private perspDist = 400;
  private preset: ViewPreset = 'iso';
  private usePersp = false;

  private unsubscribe: (() => void) | null = null;
  private lastModel: unknown = null;
  private lastSel: Id[] = [];
  private lastHover: Id | undefined;
  private firstFit = true;

  private down: { t: number; x: number; y: number } | null = null;
  private statsDirty = 0;

  /** 供 UI 读取的场景统计 */
  stats = { buildings: 0, generated: 0, fixed: 0, footprintArea: 0, maxHeight: 0, nodes: 0 };
  /** 结构变化计数器，UI 可据此刷新 */
  version = 0;
  onStats?: (s: Stage3D['stats'], v: number) => void;

  constructor() {
    this.canvas = document.createElement('canvas');
    this.canvas.style.display = 'block';
    this.canvas.style.width = '100%';
    this.canvas.style.height = '100%';
    this.canvas.style.touchAction = 'none';

    this.renderer = new THREE.WebGLRenderer({ canvas: this.canvas, antialias: true, alpha: false });
    this.renderer.setPixelRatio(Math.min(window.devicePixelRatio || 1, 2));
    this.renderer.setClearColor(BG, 1);
    this.renderer.shadowMap.enabled = true;
    this.renderer.shadowMap.type = THREE.PCFSoftShadowMap;
    this.renderer.toneMapping = THREE.ACESFilmicToneMapping;
    this.renderer.toneMappingExposure = 0.96;

    this.scene.background = new THREE.Color(BG);

    const aspect = 1.6;
    this.ortho = new THREE.OrthographicCamera(-100, 100, 100, -100, 0.5, 6000);
    this.persp = new THREE.PerspectiveCamera(42, aspect, 0.5, 6000);
    this.activeCam = this.ortho;

    /* 光：半球补光 + 一盏可控太阳（带阴影） */
    this.scene.add(new THREE.HemisphereLight(0xc4d8ff, 0x181b20, 0.8));
    const amb = new THREE.AmbientLight(0xffffff, 0.18);
    this.scene.add(amb);

    this.sun = new THREE.DirectionalLight(0xfff1dc, 1.45);
    this.sun.castShadow = true;
    this.sun.shadow.mapSize.set(2048, 2048);
    this.sun.shadow.bias = -0.0006;
    this.sun.shadow.normalBias = 0.6;
    this.scene.add(this.sun);
    this.scene.add(this.sun.target);

    /* 承接阴影的暗色地面（比地坪略低，避免 z-fighting） */
    const gGeo = new THREE.PlaneGeometry(2000, 2000);
    gGeo.rotateX(-Math.PI / 2);
    this.ground = new THREE.Mesh(
      gGeo,
      new THREE.MeshStandardMaterial({ color: 0x0a0c10, roughness: 1, metalness: 0 }),
    );
    this.ground.position.y = -0.08;
    this.ground.receiveShadow = true;
    this.scene.add(this.ground);

    this.north = new THREE.ArrowHelper(new THREE.Vector3(0, 0, -1), new THREE.Vector3(), 30, 0xf2b544, 8, 5);
    this.scene.add(this.north);

    this.scene.add(this.realizer.group);

    this.buildGrid(10);
    this.setupControls(this.ortho);
    this.syncFromStore(true);

    this.canvas.addEventListener('pointerdown', this.onPointerDown);
    this.canvas.addEventListener('pointermove', this.onPointerMove);
    this.canvas.addEventListener('pointerup', this.onPointerUp);
    this.canvas.addEventListener('pointerleave', this.onPointerLeave);
    this.canvas.addEventListener('wheel', this.onWheel, { passive: true });

    this.unsubscribe = useEditor.subscribe((s, prev) => this.diff(s, prev));
  }

  /* ------------------------------ 生命周期 ------------------------------ */

  attach(el: HTMLElement): void {
    this.container = el;
    el.appendChild(this.canvas);
    if (!this.ro) this.ro = new ResizeObserver(() => this.resize());
    this.ro.observe(el);
    this.resize();
    this.start();
  }

  detach(): void {
    this.stop();
    this.ro?.disconnect();
    this.ro = null;
    if (this.canvas.parentElement) this.canvas.parentElement.removeChild(this.canvas);
    this.container = null;
  }

  start(): void {
    if (this.running) return;
    this.running = true;
    const loop = () => {
      if (!this.running) return;
      this.controls?.update();
      this.renderer.render(this.scene, this.activeCam);
      this.raf = requestAnimationFrame(loop);
    };
    this.raf = requestAnimationFrame(loop);
  }

  stop(): void {
    this.running = false;
    if (this.raf) cancelAnimationFrame(this.raf);
    this.raf = 0;
  }

  resize(): void {
    const el = this.container;
    if (!el) return;
    const w = Math.max(1, el.clientWidth);
    const h = Math.max(1, el.clientHeight);
    this.renderer.setSize(w, h, false);
    this.persp.aspect = w / h;
    this.applyFrustum();
  }

  dispose(): void {
    this.stop();
    this.unsubscribe?.();
    this.unsubscribe = null;
    this.controls?.dispose();
    this.realizer.dispose();
    this.renderer.dispose();
    this.canvas.removeEventListener('pointerdown', this.onPointerDown);
    this.canvas.removeEventListener('pointermove', this.onPointerMove);
    this.canvas.removeEventListener('pointerup', this.onPointerUp);
    this.canvas.removeEventListener('pointerleave', this.onPointerLeave);
    this.canvas.removeEventListener('wheel', this.onWheel);
  }

  /* ------------------------------- 场景同步 ------------------------------- */

  private diff(s: ReturnType<typeof useEditor.getState>, prev: ReturnType<typeof useEditor.getState>): void {
    if (s.model !== prev.model) {
      this.syncFromStore(s.model !== prev.model);
      return;
    }
    if (s.view.selectionIds !== prev.view.selectionIds) {
      this.lastSel = s.view.selectionIds;
      this.realizer.setSelection(this.lastSel);
    }
    if (s.view.hoverId !== prev.view.hoverId) {
      this.lastHover = s.view.hoverId;
      this.realizer.setHover(this.lastHover);
    }
    if (s.view.focusObjectId && s.view.focusObjectId !== prev.view.focusObjectId) {
      this.focusObject(s.view.focusObjectId);
    }
    if (s.view.gridSize !== prev.view.gridSize) this.buildGrid(s.view.gridSize);
  }

  /** 重建 SceneIR（纯数据）→ 增量 patch 到 three */
  private syncFromStore(force = false): void {
    const st = useEditor.getState();
    const model = st.model;
    if (!force && model === this.lastModel) return;
    this.lastModel = model;

    const ir = buildSceneIR(model);
    this.realizer.update(ir);
    this.realizer.setSelection(st.view.selectionIds);
    this.realizer.setHover(st.view.hoverId);
    this.lastSel = st.view.selectionIds;
    this.lastHover = st.view.hoverId;

    this.stats = { ...ir.stats, nodes: ir.nodes.length };
    this.version++;
    this.onStats?.(this.stats, this.version);

    if (this.firstFit) {
      this.firstFit = false;
      this.frameAll();
      return;
    }
    // 视域随模型长大（只放大不缩小，避免编辑时画面乱跳）
    const b = ir.bounds;
    if (Number.isFinite(b.minX) && Number.isFinite(b.maxX)) {
      const w = b.maxX - b.minX;
      const h = b.maxY - b.minY;
      const need = Math.max(w, h) * 0.75 + 20;
      if (need > this.radius) {
        this.radius = need;
        this.applyFrustum();
      }
    }
  }

  /* ------------------------------- 相机 ------------------------------- */

  private setupControls(cam: THREE.OrthographicCamera | THREE.PerspectiveCamera): void {
    this.controls?.dispose();
    const c = new OrbitControls(cam, this.canvas);
    c.enableDamping = true;
    c.dampingFactor = 0.09;
    c.screenSpacePanning = true;
    c.minPolarAngle = 0.02;
    c.maxPolarAngle = Math.PI / 2 - 0.02;
    c.mouseButtons = {
      LEFT: THREE.MOUSE.ROTATE,
      MIDDLE: THREE.MOUSE.DOLLY,
      RIGHT: THREE.MOUSE.PAN,
    };
    c.touches = { ONE: THREE.TOUCH.ROTATE, TWO: THREE.TOUCH.DOLLY_PAN };
    c.target.copy(this.target);
    cam.position.copy(this.target).add(this.dir.clone().multiplyScalar(this.camDistance()));
    cam.lookAt(this.target);
    c.addEventListener('change', () => {
      this.target.copy(c.target);
      this.dir.copy(cam.position).sub(c.target).normalize();
    });
    c.update();
    this.controls = c;
    this.applyFrustum();
  }

  private camDistance(): number {
    if (this.usePersp) return this.perspDist;
    return this.radius * 3 + 50;
  }

  private applyFrustum(): void {
    const aspect = this.container ? this.container.clientWidth / Math.max(1, this.container.clientHeight) : 1.6;
    // radius = 正交视锥的半高（米）；取景时由 fitBox 精确算出，这里只做投影
    const halfH = this.radius;
    this.ortho.left = -halfH * aspect;
    this.ortho.right = halfH * aspect;
    this.ortho.top = halfH;
    this.ortho.bottom = -halfH;
    const dist = this.camDistance();
    this.ortho.near = 0.5;
    this.ortho.far = dist * 2 + this.radius * 8 + 500;
    this.ortho.updateProjectionMatrix();

    this.persp.aspect = aspect;
    this.persp.near = 0.5;
    this.persp.far = dist * 3 + this.radius * 8 + 500;
    this.persp.updateProjectionMatrix();

    // 阴影范围跟随模型
    const s = this.sun.shadow.camera;
    const ext = Math.max(40, this.radius * 1.35);
    s.left = -ext;
    s.right = ext;
    s.top = ext;
    s.bottom = -ext;
    s.near = 1;
    s.far = this.radius * 8 + 400;
    s.updateProjectionMatrix();
    this.sun.position.copy(this.target).add(SUN_DIR.clone().multiplyScalar(Math.max(120, this.radius * 2.2)));
    this.sun.target.position.copy(this.target);
    this.sun.target.updateMatrixWorld();

    this.ground.position.set(this.target.x, -0.08, this.target.z);
  }

  /**
   * 精确取景：把包围盒 8 个角点投到相机坐标系，量出需要的半高。
   * 比「用包围球外扩」省掉一半空白 —— 正交投影下角点 x/y 与相机距离无关，
   * 所以这一步不需要迭代，也不会因视口比例变化而失效。
   */
  private fitBox(box: THREE.Box3): void {
    const aspect = this.container ? this.container.clientWidth / Math.max(1, this.container.clientHeight) : 1.6;
    const cam = this.ortho;
    cam.position.copy(this.target).add(this.dir.clone().multiplyScalar(this.camDistance()));
    cam.lookAt(this.target);
    cam.updateMatrixWorld(true);
    const inv = new THREE.Matrix4().copy(cam.matrixWorldInverse);

    let maxUp = 0;
    let maxRight = 0;
    const c = new THREE.Vector3();
    for (let i = 0; i < 8; i++) {
      c.set(i & 1 ? box.max.x : box.min.x, i & 2 ? box.max.y : box.min.y, i & 4 ? box.max.z : box.min.z);
      c.applyMatrix4(inv);
      maxRight = Math.max(maxRight, Math.abs(c.x));
      maxUp = Math.max(maxUp, Math.abs(c.y));
    }
    // 竖向够了横向未必（横向可视 = halfH * aspect）
    this.radius = Math.max(4, Math.max(maxUp, maxRight / aspect) * 1.1);

    /*
     * 透视取景距离：不能沿用正交的半径。
     * 对每个角点，要求 |横向偏移| ≤ tan(fov/2)·(d − 沿视深偏移)，
     * 反解出 d ≥ 沿视深 + 横向/tan，取所有角点的最大值即可 —— 一次算完，不用迭代。
     */
    this.persp.position.copy(this.target).add(this.dir.clone().multiplyScalar(1000));
    this.persp.lookAt(this.target);
    this.persp.updateMatrixWorld(true);
    const e = this.persp.matrixWorld.elements;
    const right = new THREE.Vector3(e[0], e[1], e[2]).normalize();
    const up = new THREE.Vector3(e[4], e[5], e[6]).normalize();
    const tanV = Math.tan(deg(this.persp.fov / 2));
    const tanH = tanV * aspect;
    let dReq = 0;
    for (let i = 0; i < 8; i++) {
      c.set(i & 1 ? box.max.x : box.min.x, i & 2 ? box.max.y : box.min.y, i & 4 ? box.max.z : box.min.z);
      c.sub(this.target);
      const along = c.dot(this.dir);
      const lateral = Math.max(Math.abs(c.dot(up)) / tanV, Math.abs(c.dot(right)) / tanH);
      dReq = Math.max(dReq, along + lateral);
    }
    this.perspDist = Math.max(10, dReq * 1.06);
  }

  private applyPreset(p: ViewPreset): void {
    this.preset = p;
    this.usePersp = p === 'persp';
    const a = ANGLES[p];
    const az = deg(a.az);
    const el = deg(a.el);
    this.dir.set(Math.sin(az) * Math.cos(el), Math.sin(el), Math.cos(az) * Math.cos(el)).normalize();

    const cam = this.usePersp ? this.persp : this.ortho;
    if (cam !== this.activeCam) {
      this.activeCam = cam;
      this.setupControls(cam);
    }
    cam.position.copy(this.target).add(this.dir.clone().multiplyScalar(this.camDistance()));
    cam.lookAt(this.target);
    if (this.controls) {
      this.controls.target.copy(this.target);
      this.controls.update();
    }
    this.applyFrustum();
  }

  setPreset(p: ViewPreset): void {
    this.applyPreset(p);
  }

  getPreset(): ViewPreset {
    return this.preset;
  }

  /** 全览：按模型包围盒（含高度）精确取景 */
  frameAll(): void {
    const st = useEditor.getState();
    const ir = buildSceneIR(st.model);
    const b = ir.bounds;
    const valid = Number.isFinite(b.minX) && Number.isFinite(b.maxX) && b.maxX > b.minX;
    const maxH = Math.max(ir.stats.maxHeight, 6);

    const box = new THREE.Box3();
    if (valid) {
      box.min.set(b.minX, 0, -b.maxY);
      box.max.set(b.maxX, maxH, -b.minY);
      // 略抬高中心，让地面/阴影也在画面里（否则贴地对象会被裁掉一半）
      this.target.set((b.minX + b.maxX) / 2, maxH * 0.42, -(b.minY + b.maxY) / 2);
    } else {
      box.min.set(-80, 0, -60);
      box.max.set(80, 20, 60);
      this.target.set(0, 6, 0);
    }

    // 先摆指北针并把它的位置并进取景盒，否则它会被裁到画面外
    this.placeNorthArrow(box);
    this.fitBox(box);
    this.applyPreset(this.preset);
    this.buildGrid(useEditor.getState().view.gridSize);
  }

  /** 指北针摆到模型东南角外侧，别压在图上；同时把包围盒撑大保证它入画 */
  private placeNorthArrow(box: THREE.Box3): void {
    const size = box.getSize(new THREE.Vector3());
    const len = Math.max(12, Math.max(size.x, size.z) * 0.16);
    this.north.setLength(len, len * 0.4, len * 0.26);
    const pos = new THREE.Vector3(box.max.x + len * 0.55, 0.3, box.max.z + len * 0.55);
    this.north.position.copy(pos);
    box.max.x = Math.max(box.max.x, pos.x + len * 0.3);
    box.max.z = Math.max(box.max.z, pos.z + len * 0.3);
  }

  /** 聚焦单个对象（双击 / F） */
  focusObject(id: Id): void {
    const box = this.realizer.nodeBox(id);
    if (!box || box.isEmpty()) return;
    const sphere = box.getBoundingSphere(new THREE.Sphere());
    this.target.copy(sphere.center);
    this.fitBox(box);
    this.applyPreset(this.preset);
  }

  /* ------------------------------- 网格 ------------------------------- */

  private buildGrid(step: number): void {
    if (this.grid) {
      this.scene.remove(this.grid);
      this.grid.geometry.dispose();
      (this.grid.material as THREE.Material).dispose();
      this.grid = null;
    }
    const s = Math.max(2, step);
    const size = Math.max(200, Math.ceil((this.radius * 4) / s) * s);
    const div = Math.min(400, Math.round(size / s));
    const g = new THREE.GridHelper(size, div, 0x2a313b, 0x1a1f26);
    const mat = g.material as THREE.LineBasicMaterial;
    mat.transparent = true;
    mat.opacity = 0.7;
    g.position.y = -0.02;
    this.grid = g;
    this.scene.add(g);
  }

  /* ------------------------------- 拾取 ------------------------------- */

  private pick(ev: PointerEvent | MouseEvent): Id | undefined {
    const rect = this.canvas.getBoundingClientRect();
    this.pointer.x = ((ev.clientX - rect.left) / rect.width) * 2 - 1;
    this.pointer.y = -((ev.clientY - rect.top) / rect.height) * 2 + 1;
    this.raycaster.setFromCamera(this.pointer, this.activeCam);
    const hits = this.raycaster.intersectObjects(this.realizer.pickables(), true);
    for (const h of hits) {
      let o: THREE.Object3D | null = h.object;
      while (o) {
        const sid = o.userData?.sourceId as Id | undefined;
        if (sid) return sid;
        o = o.parent;
      }
    }
    return undefined;
  }

  private isPickable(id: Id): boolean {
    const st = useEditor.getState();
    const o = st.model.objects[id];
    if (!o || !o.visible) return false;
    return true;
  }

  /* ------------------------------- 事件 ------------------------------- */

  private onPointerDown = (e: PointerEvent): void => {
    if (e.button !== 0) return;
    this.down = { t: performance.now(), x: e.clientX, y: e.clientY };
  };

  private onPointerMove = (e: PointerEvent): void => {
    if (this.down) return;
    const id = this.pick(e);
    const next = id && this.isPickable(id) ? id : undefined;
    const st = useEditor.getState();
    if (next !== st.view.hoverId) st.hover(next);
  };

  private onPointerLeave = (): void => {
    this.down = null;
    const st = useEditor.getState();
    if (st.view.hoverId) st.hover(undefined);
  };

  private onWheel = (): void => {
    // OrbitControls 已处理缩放，这里只保证下一帧重算视域相关显示
    this.statsDirty++;
  };

  private onPointerUp = (e: PointerEvent): void => {
    const d = this.down;
    this.down = null;
    if (!d || e.button !== 0) return;
    const moved = Math.hypot(e.clientX - d.x, e.clientY - d.y);
    if (moved > 5) return; // 拖动 = 转视角，不选中

    const now = performance.now();
    const isDouble = now - d.t < 340;
    const st = useEditor.getState();
    const id = this.pick(e);

    if (!id) {
      if (!e.shiftKey) st.select([]);
      return;
    }
    if (isDouble) {
      st.select([id]);
      this.focusObject(id);
      return;
    }
    st.select([id], e.shiftKey);
  };

  /* ------------------------------ 对外接口 ------------------------------ */

  get camera(): THREE.OrthographicCamera | THREE.PerspectiveCamera {
    return this.activeCam;
  }

  get sceneObject(): THREE.Scene {
    return this.scene;
  }

  get realizerRef(): ThreeRealizer {
    return this.realizer;
  }

  /** 世界点（米）→ 画布屏幕坐标；E2E 与调试用 */
  screenOfWorld(x: number, y: number, h = 0): { x: number; y: number } {
    const rect = this.canvas.getBoundingClientRect();
    const v = new THREE.Vector3(x, h, -y).project(this.activeCam);
    return { x: rect.left + ((v.x + 1) / 2) * rect.width, y: rect.top + ((1 - v.y) / 2) * rect.height };
  }

  /** 与世界坐标（米）对齐的射线检测，E2E 用 */
  pickWorld(x: number, y: number): Id | undefined {
    const p = this.screenOfWorld(x, y);
    return this.pick({ clientX: p.x, clientY: p.y } as PointerEvent);
  }
}

/* --------------------------- 单例（跨视图保活） --------------------------- */

let singleton: Stage3D | null = null;

export function getStage3D(): Stage3D {
  if (!singleton) {
    singleton = new Stage3D();
    if (import.meta.env.DEV) {
      (window as unknown as Record<string, unknown>).__stage3d = singleton;
    }
  }
  return singleton;
}

export function disposeStage3D(): void {
  singleton?.dispose();
  singleton = null;
}
