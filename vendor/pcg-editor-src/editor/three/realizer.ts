/**
 * ThreeRealizer —— SceneIR → three.js 对象图（PRD §68 §70）
 *
 * 三条纪律：
 * 1. **只吃 SceneIR**，不读 ProjectModel：语义与渲染彻底解耦；
 * 2. **换轴只在这里发生一次**：(x, y, z) → (x, z, −y)，别处不许再翻；
 * 3. **增量 patch**，不 clear 整个场景：拖一个顶点不该让别的楼消失再出现。
 *
 * 对象的几何只在「形状/高度」真的变了才重建（geomKey 比对），
 * 选中 / 悬停只切材质，零几何开销。
 */
import * as THREE from 'three';
import { Id } from '../../core/model';
import { Ring, ensureCCW } from '../../core/polygon';
import { ExtrudeNode, LineNode, MarkerNode, RibbonNode, SceneIR, SceneNode, ShapeSpec, ribbonEnvelope } from '../../core/sceneIR';
import { Vec2 } from '../../core/vec';

/* --------------------------------- 换轴 --------------------------------- */

/** 2D 世界 (x, y) → three (x, 0, −y)；z 抬到高度 */
export function toV3(p: Vec2, h = 0): THREE.Vector3 {
  return new THREE.Vector3(p.x, h, -p.y);
}

/** 世界坐标 → three 坐标（供相机定位、指针换算复用） */
export function worldToThree(p: Vec2, h = 0): THREE.Vector3 {
  return toV3(p, h);
}

/* -------------------------------- 材质表 -------------------------------- */

interface KindStyle {
  color: number;
  roughness: number;
  metalness: number;
  opacity?: number;
}

const STYLE: Record<string, KindStyle> = {
  building: { color: 0xadbacd, roughness: 0.86, metalness: 0.04 },
  'building.generated': { color: 0x8e9bad, roughness: 0.92, metalness: 0.02 },
  parcel: { color: 0x2b3341, roughness: 1, metalness: 0 },
  ground: { color: 0x3a3f44, roughness: 1, metalness: 0 },
  // 三种铺装必须一眼能分辨：硬质偏冷灰、绿地偏绿、停车偏黑（沥青感）
  'ground.paving': { color: 0x6e7883, roughness: 0.95, metalness: 0 },
  'ground.lawn': { color: 0x3f6b47, roughness: 1, metalness: 0 },
  'ground.parking': { color: 0x2f343b, roughness: 1, metalness: 0 },
  'ground.water': { color: 0x2f5568, roughness: 0.25, metalness: 0.1 },
  'ground.dirt': { color: 0x5a4a38, roughness: 1, metalness: 0 },
  nobuild: { color: 0x5c2f30, roughness: 1, metalness: 0 },
  // 沥青路面：比铺装暗一档，比停车场亮一档，夹在中间才看得出"这是路"
  road: { color: 0x474f5a, roughness: 0.95, metalness: 0 },
  access: { color: 0x4ade80, roughness: 0.6, metalness: 0 },
  site: { color: 0xf2b544, roughness: 0.7, metalness: 0 },
};

/** 材质 key：同一 key 复用一份材质，所以 key 必须覆盖所有会改颜色的维度 */
function styleKeyOf(node: SceneNode): string {
  if (node.kind === 'building' && node.origin === 'generated') return 'building.generated';
  if (node.kind === 'ground' && node.type === 'surface') return `ground.${node.surface ?? 'paving'}`;
  return node.kind;
}

function styleOf(node: SceneNode): KindStyle {
  return STYLE[styleKeyOf(node)] ?? STYLE.parcel;
}

/* ------------------------------- 几何构建 ------------------------------- */

/** ShapeSpec → THREE.Shape（外环 CCW、洞 CW，符合 three 的填充约定） */
function toShape(spec: ShapeSpec): THREE.Shape {
  const outer = ensureCCW(spec.outer);
  const shape = new THREE.Shape(outer.map((p) => new THREE.Vector2(p.x, p.y)));
  for (const hole of spec.holes) {
    const cw = ensureCCW(hole).slice().reverse();
    shape.holes.push(new THREE.Path(cw.map((p) => new THREE.Vector2(p.x, p.y))));
  }
  return shape;
}

/** 由环直接构面（道路包络、站点等单环情形） */
function ringToShape(ring: Ring): THREE.Shape {
  return toShape({ outer: ring, holes: [] });
}

function extrudeGeometry(node: ExtrudeNode): THREE.BufferGeometry {
  const geo = new THREE.ExtrudeGeometry(node.shapes.map(toShape), {
    depth: node.height,
    bevelEnabled: false,
    curveSegments: 1,
    steps: 1,
  });
  // 挤出方向 +z → 抬到 +y（世界高度）
  geo.rotateX(-Math.PI / 2);
  geo.computeVertexNormals();
  return geo;
}

function surfaceGeometry(shapes: ShapeSpec[]): THREE.BufferGeometry {
  const geo = new THREE.ShapeGeometry(shapes.map(toShape));
  geo.rotateX(-Math.PI / 2);
  geo.computeVertexNormals();
  return geo;
}

function ribbonGeometry(node: RibbonNode): THREE.BufferGeometry {
  const env = ribbonEnvelope(node);
  const geo = new THREE.ShapeGeometry([ringToShape(env)]);
  geo.rotateX(-Math.PI / 2);
  return geo;
}

/** 出入口箭头：局部 +y 为前进方向，随后按 dir 绕 y 轴转 */
function markerGeometry(node: MarkerNode): THREE.BufferGeometry {
  const w = Math.max(1.2, node.width);
  const d = Math.max(2.4, w * 0.9);
  const s = new THREE.Shape();
  s.moveTo(-w / 2, -d * 0.5);
  s.lineTo(w / 2, -d * 0.5);
  s.lineTo(w / 2, d * 0.15);
  s.lineTo(0, d * 0.5);
  s.lineTo(-w / 2, d * 0.15);
  s.closePath();
  const geo = new THREE.ShapeGeometry(s);
  geo.rotateX(-Math.PI / 2);
  return geo;
}

function lineGeometry(node: LineNode): THREE.BufferGeometry {
  const pts: THREE.Vector3[] = [];
  for (const ring of node.paths) {
    if (ring.length < 2) continue;
    for (let i = 0; i < ring.length; i++) {
      const a = ring[i];
      const b = ring[(i + 1) % ring.length];
      pts.push(toV3(a, node.elevation), toV3(b, node.elevation));
    }
  }
  return new THREE.BufferGeometry().setFromPoints(pts);
}

/* ------------------------------ 几何指纹 ------------------------------ */

const r3 = (v: number) => Math.round(v * 1000) / 1000;

function ringKey(ring: Ring): string {
  let h = 0;
  for (let i = 0; i < ring.length; i++) {
    h = (h * 31 + (r3(ring[i].x) * 7 + r3(ring[i].y) * 13)) | 0;
  }
  return `${ring.length}.${h}`;
}

function shapesKey(shapes: ShapeSpec[]): string {
  return shapes
    .map((s) => `${ringKey(s.outer)}[${s.holes.map(ringKey).join(',')}]`)
    .join(';');
}

/** 只有影响几何的字段才进指纹：改名、改锁不会触发重建 */
export function geomKey(node: SceneNode): string {
  switch (node.type) {
    case 'extrude':
      return `x|${r3(node.height)}|${shapesKey(node.shapes)}`;
    case 'surface':
      return `s|${node.role}|${r3(node.elevation)}|${shapesKey(node.shapes)}`;
    case 'ribbon':
      return `r|${r3(node.width)}|${r3(node.elevation)}|${ringKey(node.centerline as Ring)}`;
    case 'line':
      return `l|${r3(node.elevation)}|${node.paths.map(ringKey).join(',')}`;
    case 'marker':
      return `m|${r3(node.pos.x)}|${r3(node.pos.y)}|${r3(node.dir)}|${r3(node.width)}`;
  }
}

/* -------------------------------- 视图 -------------------------------- */

interface NodeView {
  id: Id;
  node: SceneNode;
  key: string;
  obj: THREE.Object3D;
  mesh?: THREE.Mesh;
  edges?: THREE.LineSegments;
  dispose: () => void;
}

export class ThreeRealizer {
  readonly group = new THREE.Group();
  private views = new Map<Id, NodeView>();
  private baseMats = new Map<string, THREE.MeshStandardMaterial>();
  private selMats = new Map<string, THREE.MeshStandardMaterial>();
  private lineMat: THREE.LineBasicMaterial;
  private lineSelMat: THREE.LineBasicMaterial;
  private edgeIdle: THREE.LineBasicMaterial;
  private edgeHover: THREE.LineBasicMaterial;
  private edgeSel: THREE.LineBasicMaterial;
  private selection: Id[] = [];
  private hoverId?: Id;

  constructor() {
    this.group.name = 'realizer';
    this.lineMat = new THREE.LineBasicMaterial({ color: STYLE.site.color, transparent: true, opacity: 0.9 });
    this.lineSelMat = new THREE.LineBasicMaterial({ color: 0x22d3ee, transparent: true, opacity: 1 });
    this.edgeIdle = new THREE.LineBasicMaterial({ color: 0xffffff, transparent: true, opacity: 0.16 });
    this.edgeHover = new THREE.LineBasicMaterial({ color: 0x22d3ee, transparent: true, opacity: 0.5 });
    this.edgeSel = new THREE.LineBasicMaterial({ color: 0x22d3ee, transparent: true, opacity: 0.95 });
  }

  /* ---------------------------- 材质工厂 ---------------------------- */

  private baseMat(key: string, style: KindStyle): THREE.MeshStandardMaterial {
    let m = this.baseMats.get(key);
    if (!m) {
      m = new THREE.MeshStandardMaterial({
        color: style.color,
        roughness: style.roughness,
        metalness: style.metalness,
        side: THREE.DoubleSide,
      });
      this.baseMats.set(key, m);
    }
    return m;
  }

  private selMat(key: string, style: KindStyle): THREE.MeshStandardMaterial {
    let m = this.selMats.get(key);
    if (!m) {
      m = new THREE.MeshStandardMaterial({
        color: style.color,
        roughness: style.roughness,
        metalness: style.metalness,
        side: THREE.DoubleSide,
        emissive: new THREE.Color(0x22d3ee),
        emissiveIntensity: 0.24,
      });
      this.selMats.set(key, m);
    }
    return m;
  }

  private matKey(node: SceneNode): string {
    return styleKeyOf(node);
  }

  /* ------------------------------ 构建 ------------------------------ */

  private build(node: SceneNode, key: string): NodeView {
    const mk = this.matKey(node);
    const style = styleOf(node);
    const base = this.baseMat(mk, style);

    if (node.type === 'line') {
      const geo = lineGeometry(node);
      const obj = new THREE.LineSegments(geo, this.lineMat);
      obj.userData.sourceId = node.sourceId;
      obj.renderOrder = 2;
      return {
        id: node.id,
        node,
        key,
        obj,
        dispose: () => geo.dispose(),
      };
    }

    let geo: THREE.BufferGeometry;
    if (node.type === 'extrude') geo = extrudeGeometry(node);
    else if (node.type === 'surface') geo = surfaceGeometry(node.shapes);
    else if (node.type === 'ribbon') geo = ribbonGeometry(node);
    else geo = markerGeometry(node);

    const mesh = new THREE.Mesh(geo, base);
    mesh.userData.sourceId = node.sourceId;
    mesh.castShadow = node.type === 'extrude';
    mesh.receiveShadow = node.type !== 'extrude';
    if (node.type === 'surface') mesh.position.y = node.elevation;
    if (node.type === 'ribbon') mesh.position.y = node.elevation;
    if (node.type === 'marker') {
      // 箭头几何在局部原点生成，必须自己搬到 pos —— 漏了这步会全部堆在世界原点
      mesh.position.set(node.pos.x, node.elevation, -node.pos.y);
      mesh.rotation.y = node.dir - Math.PI / 2;
    }

    const obj = new THREE.Group();
    obj.name = `node:${node.id}`;
    obj.userData.sourceId = node.sourceId;
    obj.add(mesh);

    const edgeGeo = new THREE.EdgesGeometry(geo, 24);
    const edges = new THREE.LineSegments(edgeGeo, this.edgeIdle);
    edges.position.copy(mesh.position);
    edges.rotation.copy(mesh.rotation);
    edges.renderOrder = 3;
    obj.add(edges);

    const view: NodeView = {
      id: node.id,
      node,
      key,
      obj,
      mesh,
      edges,
      dispose: () => {
        geo.dispose();
        edgeGeo.dispose();
        obj.remove(mesh, edges);
      },
    };

    mesh.material = base;
    return view;
  }

  private removeView(v: NodeView): void {
    this.group.remove(v.obj);
    v.dispose();
    this.views.delete(v.id);
  }

  /* ------------------------------ 更新 ------------------------------ */

  /** SceneIR → 对象图；只重建指纹变了的节点 */
  update(ir: SceneIR): void {
    const live = new Set<Id>();
    for (const node of ir.nodes) {
      live.add(node.id);
      const key = geomKey(node);
      const prev = this.views.get(node.id);
      if (prev && prev.key === key && prev.node.type === node.type) {
        prev.node = node;
        if (prev.mesh) prev.mesh.userData.sourceId = node.sourceId;
        continue;
      }
      if (prev) this.removeView(prev);
      const view = this.build(node, key);
      this.views.set(node.id, view);
      this.group.add(view.obj);
    }
    for (const [id, v] of Array.from(this.views)) {
      if (!live.has(id)) this.removeView(v);
    }
    this.applyState();
  }

  setSelection(ids: Id[]): void {
    this.selection = ids;
    this.applyState();
  }

  setHover(id?: Id): void {
    this.hoverId = id;
    this.applyState();
  }

  private applyState(): void {
    const sel = new Set(this.selection);
    for (const v of this.views.values()) {
      const on = sel.has(v.id);
      const hover = this.hoverId === v.id;
      if (v.mesh) {
        const mk = this.matKey(v.node);
        v.mesh.material = on ? this.selMat(mk, styleOf(v.node)) : this.baseMat(mk, styleOf(v.node));
      }
      if (v.edges) v.edges.material = on ? this.edgeSel : hover ? this.edgeHover : this.edgeIdle;
      if (v.node.type === 'line' && v.obj instanceof THREE.LineSegments) {
        v.obj.material = on || hover ? this.lineSelMat : this.lineMat;
      }
    }
  }

  /* ------------------------------ 查询 ------------------------------ */

  /** 供 Raycaster 使用：所有可命中对象 */
  pickables(): THREE.Object3D[] {
    const out: THREE.Object3D[] = [];
    for (const v of this.views.values()) out.push(v.obj);
    return out;
  }

  /** 节点世界包围盒（3D，含高度） */
  nodeBox(id: Id): THREE.Box3 | null {
    const v = this.views.get(id);
    if (!v) return null;
    return new THREE.Box3().setFromObject(v.obj);
  }

  nodeCount(): number {
    return this.views.size;
  }

  dispose(): void {
    for (const v of Array.from(this.views.values())) this.removeView(v);
    for (const m of this.baseMats.values()) m.dispose();
    for (const m of this.selMats.values()) m.dispose();
    this.lineMat.dispose();
    this.lineSelMat.dispose();
    this.edgeIdle.dispose();
    this.edgeHover.dispose();
    this.edgeSel.dispose();
    this.baseMats.clear();
    this.selMats.clear();
  }
}
