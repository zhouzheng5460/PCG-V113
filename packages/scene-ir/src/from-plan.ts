// Plan IR → SceneIR。
//
// 这一层是**唯一**知道"宗地怎么切成楼位""层数怎么定"的地方；realizer 完全不知道，
// 它只会照着节点画。这个分界很重要：
//   · 想换渲染方式（three / WebGPU / 导出 glTF）→ 只改 realizer；
//   · 想改生成规则（面宽、层数、退线）→ 只改这里。
// 以前这两件事糅在一个 `build.ts` 里，加一个图层就得在渲染循环里写 if。
//
// 两个刻意的取舍：
//   · **只做体块（massing），不做模型替换**。楼 = 楼位 quad 挤出到 层数×层高，
//     和资产库当前的"盒子兜底"一致（台账 Q-010/Q-012）。换真模型是 F4 的事。
//   · **层数按 id 的哈希取**，`Math.random` 会让"同一块地每次打开都不一样"，那是调试灾难。
//     所以 `irKey()` 对同一份方案必须稳定 —— 这也是确定性的验收口径。

import { ccw, sampleBezier, type Vec2 } from '../../plan-ir/src/geom.ts';
import { ZONE_PREFAB } from '../../plan-ir/src/types.ts';
import type { PlanNet, PlanObject, PlanSurface, PlanZoning, ZoneKind } from '../../plan-ir/src/types.ts';
import { KIND_SPECS, cutZoning } from '../../slots/src/cut.ts';
import { ELEV, MISC, NET_COLOR, NET_WIDTH, SURFACE_COLOR, WALK_OVERHANG, ZONE_COLOR } from './palette.ts';
import type { InstanceItem, Layer, SceneIR, SceneNode, SceneStats } from './types.ts';

/** 渲染只需要 Plan 的四个几何通道 —— settings/facts/regions 那些与画面无关。 */
export interface SceneChannels {
  surfaces: PlanSurface[];
  nets: PlanNet[];
  objects: PlanObject[];
  zoning: PlanZoning[];
}

export type LayerToggles = Record<Layer, boolean>;

export interface SceneOptions {
  toggles: LayerToggles;
  /** 楼层哈希的种子。同一份方案必须传同一个值，否则层数会变。 */
  seedKey: string;
  /** 手动覆盖楼位层数，key 是**节点 id**（`bld:z0-b0-r0`）。编辑回路就从这个入口进来。 */
  floors?: Record<string, number>;
}

// ─────────────────────────────────────────── 工具

/** FNV-1a → [0,1)。同一 id 每次结果相同，楼层数才稳定。 */
function hash01(s: string): number {
  let h = 2166136261;
  for (let i = 0; i < s.length; i++) {
    h ^= s.charCodeAt(i);
    h = Math.imul(h, 16777619);
  }
  return ((h >>> 0) % 100000) / 100000;
}

const KIND_BY_PREFAB = new Map<string, ZoneKind>(
  (Object.entries(ZONE_PREFAB) as [ZoneKind, string][]).map(([kind, prefab]) => [prefab, kind]),
);

/** ZoneGrid 的 zoneId 是 prefab 名（"EU Commercial Low"），映射回我们的 kind。 */
export function kindOfZoneId(zoneId: string): ZoneKind | null {
  const direct = KIND_BY_PREFAB.get(zoneId);
  if (direct) return direct;
  const tail = zoneId.replace(/^(EU|NA|AS)\s+/i, '');
  for (const [prefab, k] of KIND_BY_PREFAB) {
    if (prefab.replace(/^(EU|NA|AS)\s+/i, '') === tail) return k;
  }
  return null;
}

function centroidOf(ring: readonly Vec2[]): Vec2 {
  let x = 0;
  let z = 0;
  for (const p of ring) {
    x += p[0];
    z += p[1];
  }
  return [x / ring.length, z / ring.length];
}

/** 三次贝塞尔 → 采样中心线。复用 plan-ir 的 `sampleBezier`，和判定用同一条折线。 */
function bezierOutline(a: Vec2, b: Vec2, c: Vec2, d: Vec2): Vec2[] {
  const { pts } = sampleBezier(a, b, c, d);
  if (pts.length >= 2) return pts;
  return [a, d];
}

/**
 * 宗地的临街方向：取"质心 → 最近街道采样点"的单位向量。
 *
 * 为什么不用 AttachmentFinder：它算的是**地块边界**的贴合点，而宗地是地块内部
 * 切出来的一小块，两者不是同一个环。这里只要一个"从宗地朝向街"的方向，够用了。
 */
function frontDirFor(quad: readonly Vec2[], nets: PlanNet[]): Vec2 {
  const c = centroidOf(quad);
  let best: Vec2 = [1, 0];
  let bestD = Infinity;
  for (const n of nets) {
    if (n.role === 'pathway' || n.role === 'pathwayWide' || n.role === 'fence') continue;
    const pts = bezierOutline(n.a, n.b, n.c, n.d);
    for (const p of pts) {
      const dx = p[0] - c[0];
      const dz = p[1] - c[1];
      const d = dx * dx + dz * dz;
      if (d < bestD) {
        bestD = d;
        const len = Math.hypot(dx, dz) || 1;
        best = [dx / len, dz / len];
      }
    }
  }
  return best;
}

// ─────────────────────────────────────────── 物件分类

const TREE_ROLES = new Set(['tree', 'bush', 'planter']);
const FLAT_ROLES = new Set(['parkingBay', 'decal', 'arrow', 'marker', 'accessPoint', 'culDeCap', 'roundaboutIsland']);

/**
 * 一个物件 → 一条实例变换。
 *
 * 约定（和 realizer 的几何工厂一一对应，写错会变成"树浮在半空"或"埋进地里"）：
 *   · **`y` 是实例的底面**，不是中心。realizer 按 `y + sy/2` 放中心。
 *   · **所有形状的高度都归一化成 1**：锥 `ConeGeometry(1,1)`、柱 `CylinderGeometry(_,_,1)`、
 *     球 `SphereGeometry(0.5)`（直径 1）、盒 `BoxGeometry(1,1,1)`、片 `PlaneGeometry(1,1)`。
 *     于是 `sy` 永远就是"这东西多少米高"，读代码不用在脑子里乘系数。
 *   · `lift` 允许是函数：乔木冠的底 = 树干顶 = `0.1 + 2.2s`，不随 s 线性的话就成常量了。
 */
function itemOf(o: PlanObject, sx: number, sy: number, sz: number, lift: number | ((s: number) => number)): InstanceItem {
  const s = o.scale ?? 1;
  const base = typeof lift === 'function' ? lift(s) : lift;
  return {
    x: o.x,
    z: o.z,
    y: (o.y ?? 0) + base,
    yaw: o.yaw ?? 0,
    sx: sx * s,
    sy: sy * s,
    sz: sz * s,
  };
}

// ─────────────────────────────────────────── 主构建

export function buildSceneIR(ch: SceneChannels, ring: Vec2[], opts: SceneOptions): SceneIR {
  const T = opts.toggles;
  const nodes: SceneNode[] = [];
  const stats: SceneStats = {
    surfaces: 0,
    nets: 0,
    objects: 0,
    markings: 0,
    slots: 0,
    footArea: 0,
    tallest: 0,
    longestStreet: 0,
  };

  // 1) 地块底板 + 轮廓。这是**输入**轮廓挤出来的，不是引擎给的 —— 所以单独一层，
  //    关掉它就能看清"引擎到底给了什么面"。
  if (T.base && ring.length >= 3) {
    nodes.push({
      id: 'base:pad',
      layer: 'base',
      type: 'extrude',
      ring: ccw(ring),
      y0: -0.6,
      y1: ELEV.pad,
      color: MISC.pad,
      props: { role: 'sitePad' },
    });
    nodes.push({
      id: 'base:outline',
      layer: 'base',
      type: 'line',
      path: ring,
      y: ELEV.outline,
      color: MISC.outline,
      closed: true,
    });
  }

  // 2) 铺装 / 草地 / 水面
  if (T.surfaces) {
    ch.surfaces.forEach((s, i) => {
      if (s.ring.length < 3) return;
      nodes.push({
        id: `surface:${i}`,
        layer: 'surfaces',
        type: 'surface',
        ring: s.ring,
        // 洞一路带下去（否则 3D/GLB 里洞会被填平）
        ...(s.holes?.length ? { holes: s.holes } : {}),
        y: ELEV.surface + (s.y ?? 0) * 0.001,
        color: SURFACE_COLOR[s.role] ?? 0x555a60,
        opacity: s.role === 'water' ? 1 : 0.98,
      });
      stats.surfaces++;
    });
  }

  // 3) 街道 / 人行道
  if (T.streets) {
    ch.nets.forEach((n, i) => {
      const w = NET_WIDTH[n.role] ?? NET_WIDTH.other;
      const pts = bezierOutline(n.a, n.b, n.c, n.d);
      if (pts.length < 2) return;
      let len = 0;
      for (let k = 1; k < pts.length; k++) len += Math.hypot(pts[k][0] - pts[k - 1][0], pts[k][1] - pts[k - 1][1]);
      stats.longestStreet = Math.max(stats.longestStreet, len);
      stats.nets++;

      if (n.role === 'fence') {
        // 围墙：窄带抬到 0.5 m 高，看起来像一道墙而不是一条线
        nodes.push({
          id: `net:${i}:fence`,
          layer: 'streets',
          type: 'ribbon',
          path: pts,
          width: Math.max(w, 0.5),
          y: 0.5,
          color: NET_COLOR.fence,
        });
        return;
      }

      if (n.role === 'pathway' || n.role === 'pathwayWide') {
        nodes.push({
          id: `net:${i}:path`,
          layer: 'streets',
          type: 'ribbon',
          path: pts,
          width: w,
          y: ELEV.path,
          color: NET_COLOR[n.role] ?? 0xb9a887,
        });
        return;
      }

      // 街道分带：先铺一条比车行道宽 3.2 m 的浅色带（= 两侧各 1.6 m 人行道），
      // 再把车行道盖在中间。这样一条街才有"路 + 人行道"的读感，
      // 否则只是一条灰色带子，分不出哪是路、哪是地。
      nodes.push({
        id: `net:${i}:walk`,
        layer: 'streets',
        type: 'ribbon',
        path: pts,
        width: w + WALK_OVERHANG * 2,
        y: ELEV.walk,
        color: MISC.walk,
      });
      nodes.push({
        id: `net:${i}:road`,
        layer: 'streets',
        type: 'ribbon',
        path: pts,
        width: w,
        y: ELEV.road,
        color: NET_COLOR[n.role] ?? 0x33393f,
      });
      // 中心线：太窄的路画了反而脏
      if (w >= 9) {
        nodes.push({
          id: `net:${i}:centre`,
          layer: 'streets',
          type: 'line',
          path: pts,
          y: ELEV.centreline,
          color: MISC.centreline,
          opacity: 0.45,
        });
      }
    });
  }

  // 4) 宗地：半透明底面 + 虚线描边
  if (T.zoning) {
    ch.zoning.forEach((z, i) => {
      if (z.quad.length < 3) return;
      nodes.push({
        id: `zone:${i}:fill`,
        layer: 'zoning',
        type: 'surface',
        ring: [...z.quad],
        y: ELEV.zoningFill,
        color: MISC.zoningFill,
        opacity: 0.16,
      });
      nodes.push({
        id: `zone:${i}:outline`,
        layer: 'zoning',
        type: 'line',
        path: [...z.quad],
        y: ELEV.zoningLine,
        color: MISC.zoningLine,
        dashed: true,
        closed: true,
      });
    });
  }

  // 5) 楼块：宗地 → 楼位 → 挤出
  if (T.massing) {
    ch.zoning.forEach((z, i) => {
      const kind = kindOfZoneId(z.zoneId);
      if (!kind) return;
      const dir = frontDirFor(z.quad, ch.nets);
      const cut = cutZoning({ zoneId: z.zoneId, quad: z.quad as never }, kind, dir, `z${i}`);
      for (const slot of cut.slots) {
        const spec = KIND_SPECS[slot.kind];
        const [f0, f1] = spec.floors;
        const auto = f0 + Math.floor(hash01(`${opts.seedKey}:${slot.id}`) * (f1 - f0 + 1));
        const nodeId = `bld:${slot.id}`;
        // 手动覆盖优先：编辑回路改的就是这一个数字，别的楼一个都不该动。
        // key 用**节点 id**（`bld:…`）而不是楼位 id —— 界面上的选中态拿到的就是节点 id，
        // 两边都换成同一个口径，才不会出现"改了但没生效"（第一版就栽在这里）。
        const floors = opts.floors?.[nodeId] ?? auto;
        const h = floors * spec.floorHeight;
        nodes.push({
          id: nodeId,
          layer: 'massing',
          type: 'extrude',
          ring: [...slot.quad],
          // 楼高必须等于 y1-y0：量高校验是这么对账的，多给 2 cm 就会读出 0.44% 的误差
          y0: ELEV.building,
          y1: ELEV.building + h,
          color: ZONE_COLOR[slot.kind] ?? 0xb0a080,
          props: {
            kind: slot.kind,
            floors,
            floorHeight: spec.floorHeight,
            height: h,
            auto,
            /** 自动值的上/下限：面板的层数滑块直接用它，不在这里写死。 */
            fmin: f0,
            fmax: f1,
            overridden: opts.floors?.[nodeId] != null,
            id: slot.id,
            w: slot.width,
            d: slot.depth,
          },
        });
        stats.slots++;
        stats.footArea += slot.width * slot.depth;
        stats.tallest = Math.max(stats.tallest, h);
      }
    });
  }

  // 6) 绿植 / 家具 / 标线。三种绿植分开成节点：一个节点 = 一种形状 = 1 个 draw call。
  if (T.props || T.markings) {
    const pick = (test: (o: PlanObject) => boolean): PlanObject[] => ch.objects.filter(test);
    const isTree = (o: PlanObject): boolean => o.role === 'tree';
    const isBush = (o: PlanObject): boolean => o.role === 'bush';
    const isPlanter = (o: PlanObject): boolean => o.role === 'planter';
    const isFlat = (o: PlanObject): boolean => FLAT_ROLES.has(o.role);
    const isTidy = (o: PlanObject): boolean => !TREE_ROLES.has(o.role) && !FLAT_ROLES.has(o.role);

    if (T.props) {
      // 乔木 = 锥形冠 + 柱形干，两段各自成节点（形状不同，没法塞进一个 InstancedMesh）
      const trees = pick(isTree);
      if (trees.length) {
        nodes.push({
          id: 'prop:trunk',
          layer: 'props',
          type: 'instances',
          shape: 'cylinder',
          color: MISC.trunk,
          castShadow: true,
          items: trees.map((o) => itemOf(o, 1, 2.2, 1, 0.1)),
        });
        nodes.push({
          id: 'prop:canopy',
          layer: 'props',
          type: 'instances',
          shape: 'cone',
          color: MISC.canopy,
          castShadow: true,
          // 冠底接在树干顶上：0.1（离地） + 2.2s（干高）
          items: trees.map((o) => itemOf(o, 1.5, 3.6, 1.5, (s) => 0.1 + 2.2 * s)),
        });
      }
      const bushes = pick(isBush);
      if (bushes.length) {
        nodes.push({
          id: 'prop:bush',
          layer: 'props',
          type: 'instances',
          shape: 'sphere',
          color: MISC.bush,
          castShadow: true,
          items: bushes.map((o) => itemOf(o, 2.2, 1.6, 2.2, (s) => 0.1 * s)),
        });
      }
      const planters = pick(isPlanter);
      if (planters.length) {
        nodes.push({
          id: 'prop:planter',
          layer: 'props',
          type: 'instances',
          shape: 'box',
          color: MISC.planter,
          castShadow: true,
          items: planters.map((o) => itemOf(o, 1.9, 0.9, 1.9, 0)),
        });
      }
      const tidy = pick(isTidy);
      if (tidy.length) {
        nodes.push({
          id: 'prop:misc',
          layer: 'props',
          type: 'instances',
          shape: 'box',
          color: MISC.prop,
          castShadow: true,
          items: tidy.map((o) => itemOf(o, 0.7, 1, 0.7, 0)),
        });
      }
    }

    if (T.markings) {
      const flats = pick(isFlat);
      if (flats.length) {
        nodes.push({
          id: 'mark:flat',
          layer: 'markings',
          type: 'instances',
          shape: 'plane',
          color: MISC.marking,
          items: flats.map((o) => itemOf(o, 2.16, 1, 4.5, ELEV.marking)),
        });
        stats.markings = flats.length;
      }
    }

    stats.objects =
      T.props ? ch.objects.filter((o) => !FLAT_ROLES.has(o.role)).length : 0;
    stats.objects += stats.markings;
  }

  return { nodes, stats };
}
