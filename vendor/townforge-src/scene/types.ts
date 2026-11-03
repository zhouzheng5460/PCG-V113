// 场景要素：整个生成器只产出这四种要素，2D 画布、three.js 预览、GLB 导出都是它的消费者。
// 这样"编辑器里看到的"与"导出的"不可能不一致 —— 它们读的是同一份数组。

import { Pt } from '../core/geom';
import type { MatKey } from './palette';

/** 贴地薄面：路面、铺装、绿地、标线、车位线 */
export interface SurfaceFeature {
  name: string;
  /** 分组路径，导出后是 glTF 节点名，供 DCC 里整体选中 */
  group: string;
  ring: Pt[];
  mat: MatKey;
  /** 高度偏移，用来解决共面 z-fighting；语义上代表"这一层比地面高多少" */
  y: number;
}

export type SolidKind = 'tower' | 'slab' | 'podium' | 'house' | 'warehouse' | 'canopy' | 'mound';

/** 体块：建筑、雨棚、围墙、树冠以外的实体 */
export interface SolidFeature {
  name: string;
  group: string;
  ring: Pt[];
  y0: number;
  h: number;
  wallMat: MatKey;
  roofMat: MatKey;
  kind: SolidKind;
  /**
   * 顶面**逐顶点**高度（世界单位）。只给 `kind: 'mound'` 用。
   *
   * 有它的时候 `h` 只是一个统计用的最大值，几何完全按 `hs` 走：顶面是 `hs[i]`，
   * 裙边从 `y0` 拉到各自的 `hs`。料堆/台地必须这样画 —— 它们是 Core 的
   * `AiTerrain.Cells` 切出来的缓坡格子，一块 4×4 m 格子里四个角的高度本来就不一样。
   */
  hs?: number[];
  /** 收顶（世界单位）：顶面朝形心缩进这么多，侧面因此带坡。只给 `mound` 用。 */
  inset?: number;
}

export type PropKind =
  | 'tree'
  /** 阔叶树：宽扁树冠，和 `tree`（细高锥）区分开。Placemaker 的公园全靠它 */
  | 'treeRound'
  | 'treeSmall'
  | 'conifer'
  /** 灌木：0.12 半径、0.18 高的一丛，比 `treeSmall` 矮一个量级。
   *  没有它的时候 Placemaker 的 bush 只能映射成 `treeSmall`，一排绿篱看起来像一排松树苗。 */
  | 'shrub'
  | 'lamp'
  | 'lampDouble'
  | 'bench'
  | 'bin'
  | 'sign'
  | 'planter'
  | 'shelter'
  | 'fountain'
  | 'bollard'
  | 'hedge'
  | 'car'
  | 'flagpole'
  /**
   * 集装箱/托盘/料箱：一个略扁的方盒。堆场里的"东西"绝大多数都是它——
   * Placemaker 把这类杂物一律标成 `furniture`，靠资产分类（industrial.container /
   * .pallet / .skip / .scrap…）区分，几何上一个盒子就够读出来。
   */
  | 'crate'
  /** 油桶/轮胎堆：一个矮圆柱。 */
  | 'barrel'
  /** 石块/瓦砾堆：一个矮的多边形疙瘩，比 `crate` 更像自然堆料。 */
  | 'rock'
  /**
   * 雕像/纪念碑：基座 + 一个人像块。
   *
   * `centerpiece` 这个角色过去一律映射成 `fountain`（水盘 + 水柱），
   * 于是市场广场正中间那尊雕像在场景里是一个**蓝色水盘** —— 广场的视觉焦点错了。
   */
  | 'statue'
  /**
   * 废墟残墙：一段 3.2 m × 0.28 m × 1.1 m 的混凝土墙。
   *
   * `abandonedLot` 的 7 段围墙是那块地的全部身份 —— 用 `crate`（1.1 m 的方盒）代替，
   * 一片废墟看起来就是"一堆箱子"。给墙一个**长条**而不是方盒，才读得出来"这儿原来是栋楼"。
   */
  | 'ruinWall';

/** 点状道具：树、路灯、长椅…… 几何在 props.ts 里按 kind 生成，坐标只在这里存一份 */
export interface PropFeature {
  name: string;
  group: string;
  kind: PropKind;
  at: Pt;
  /** 朝向（弧度），0 = +X */
  rot: number;
  /** 尺寸倍率 */
  scale: number;
  /** 是否随机翻转，让树冠不整齐 */
  seed: number;
  /**
   * 长度（世界单位），只对 `hedge` 这类"沿 rot 的短棒"有意义。
   *
   * 有了它，绿篱才能是一段真实长度（一条 12 m 的花坛边），而不是固定的 4.2 m 小段。
   * 给了就不再乘 `scale`（调用方已经把长度算好了），2D/3D 两侧共用这一个值。
   */
  len?: number;
  /**
   * 落地的底高（世界单位）。不给就是地面（`0.022`）。
   *
   * 只在**站在台地上**时用：`terraform` 抬起来的料堆上摆着箱子/油桶，
   * 底面不跟着抬就会陷进土里 —— 这正是以前"抬不抬地形"两难的原因。
   */
  baseY?: number;
}

/** 线性要素：内部道路、建筑入口路、园路、围栏 */
export interface PathFeature {
  name: string;
  group: string;
  pts: Pt[];
  width: number;
  mat: MatKey;
  y: number;
  /** 围栏是竖起来的，需要高度；道路为 0 */
  height?: number;
}

export type LandUse =
  | 'cbd'
  | 'commercial'
  | 'residential'
  | 'industrial'
  | 'civic'
  | 'park'
  | 'parking'
  | 'green'
  /**
   * 禁建 / 保留地：用户画的固定形状区域。
   * 不做分区规划（不参与 CBD/商业/住宅的分档），也不产出建筑，
   * 但它仍然是一个"封闭地块"——照样有地坪、有边界、四周的路照常连。
   */
  | 'reserved';

/** 单个地块的规划结果，UI 要按用途统计与着色 */
export interface LotInfo {
  index: number;
  use: LandUse;
  area: number;
  far: number;
  buildings: number;
  parkingBays: number;
  trees: number;
}

export interface SceneStats {
  surfaces: number;
  solids: number;
  props: number;
  paths: number;
  byUse: Record<string, number>;
  propsByKind: Record<string, number>;
}

export interface Scene {
  surfaces: SurfaceFeature[];
  solids: SolidFeature[];
  props: PropFeature[];
  paths: PathFeature[];
  lots: LotInfo[];
  stats: SceneStats;
}

export function emptyScene(): Scene {
  return {
    surfaces: [],
    solids: [],
    props: [],
    paths: [],
    lots: [],
    stats: { surfaces: 0, solids: 0, props: 0, paths: 0, byUse: {}, propsByKind: {} },
  };
}

// ---------------------------------------------------------------- 几何小工具

export function ringArea(ring: Pt[]): number {
  let s = 0;
  for (let i = 0; i < ring.length; i++) {
    const a = ring[i];
    const b = ring[(i + 1) % ring.length];
    s += a.x * b.y - b.x * a.y;
  }
  return Math.abs(s) / 2;
}

export function ringCenter(ring: Pt[]): Pt {
  let x = 0;
  let y = 0;
  for (const p of ring) {
    x += p.x;
    y += p.y;
  }
  return new Pt(x / ring.length, y / ring.length);
}

/** 把 p 绕 c 旋转 a 弧度 */
export function rotateAbout(p: Pt, c: Pt, a: number): Pt {
  const dx = p.x - c.x;
  const dy = p.y - c.y;
  const cs = Math.cos(a);
  const sn = Math.sin(a);
  return new Pt(c.x + dx * cs - dy * sn, c.y + dx * sn + dy * cs);
}

/** 逆时针（有向面积为正） */
export function toCCW(ring: Pt[]): Pt[] {
  let s = 0;
  for (let i = 0; i < ring.length; i++) {
    const a = ring[i];
    const b = ring[(i + 1) % ring.length];
    s += a.x * b.y - b.x * a.y;
  }
  return s < 0 ? ring.slice().reverse() : ring;
}

export function clamp(v: number, lo: number, hi: number): number {
  return v < lo ? lo : v > hi ? hi : v;
}
