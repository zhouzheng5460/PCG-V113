// 把模型汇总成一份 Scene：道路、标线、地块内容、街道设施。
// 2D 编辑器、three.js 预览、GLB 导出都从这里取数据，三者不可能不一致。

import type { Model } from '../town/model';
import { buildRoadNet, roadSurfaces } from '../road/system';
import { roadMarkings } from '../road/markings';
import { streetFurniture } from '../town/furniture';
import { ModernWard } from '../town/modern';
import type { LandUse, LotInfo, Scene, SolidFeature } from './types';
import { emptyScene, toCCW } from './types';
import { isTreeKind } from './propStyle';
import { Pt } from '../core/geom';
import { bridgePlan, type BridgeResult, type PlacemakerPlan, type PlanPoint } from '../bridge/placemaker';

/**
 * 一块由 Placemaker 接管的地。
 *
 * Placemaker 是室外场地设计器（108 个生成器：停车、公园、广场、运动场、堆场…），
 * 它**不产建筑** —— 所以这里接管的是"地块内部的室外那一半"：
 * 铺装、车位、通道、绿化、道具。建筑要不要留由 `keepBuildings` 决定。
 */
export interface PlacemakerLot {
  /** C# 端 `--emit` 产出的 Plan JSON（见 tools/bake-plan.ts） */
  plan: PlacemakerPlan;
  /** Plan 的 (0,0) 对应的世界坐标 —— meta.json 里的 `origin` */
  origin: Pt;
  /** 这块地是第几个 patch；给了就把该 patch 的 TownForge 内容让出来 */
  patchIndex?: number;
  /**
   * 是否保留该地块原本的建筑体块（默认 false）。
   * true = TownForge 继续盖楼、Placemaker 只铺地；false = 整块接管，连楼一起换掉。
   * 停车坪 / 公园这类本来就没有楼的用途用 false。
   */
  keepBuildings?: boolean;
  /**
   * 地块环（米，Plan 坐标系）—— meta.json 里的 `lotRing`。
   *
   * 绿地类生成器（park / greenwayPark / picnicGrounds / natureReserve）**不铺地面**，
   * 只出园路和树。不给这一环，接出来的就是"一堆树漂在裸地上"。
   */
  lotRing?: PlanPoint[];
  /** 导出分组前缀，默认 `pm` */
  group?: string;
}

export interface SceneOptions {
  /** 建筑高度倍率 */
  heightScale?: number;
  /** 是否生成道路标线 */
  markings?: boolean;
  /** 是否生成行道树 / 路灯 */
  furniture?: boolean;
  /** 是否生成地块内容（建筑、铺装、车位） */
  lots?: boolean;
  /** 是否铺地面底板 */
  ground?: boolean;
  /** Placemaker 烘焙方案的注入点。见 src/bridge/placemaker.ts。 */
  placemaker?: PlacemakerLot[];
}

/**
 * 桥接结果缓存。
 *
 * 为什么要有：每次拖拓扑点都会 `buildAll()` + `buildScene()`，而 Plan JSON 是静态的 ——
 * 同一份 Plan 桥 300 多次纯属浪费（一次约 5 ms × 24 块地 = 120 ms，拖一下就能感觉到）。
 * 键用 Plan 的对象身份 + origin，因为 JSON 导入后引用是稳定的。
 */
const bridgeCache = new WeakMap<PlacemakerPlan, Map<string, BridgeResult>>();

function bridgeCached(lot: PlacemakerLot): BridgeResult {
  let byKey = bridgeCache.get(lot.plan);
  if (!byKey) { byKey = new Map(); bridgeCache.set(lot.plan, byKey); }
  const group = lot.group ?? 'pm';
  // lotRing 也进键：同一份 Plan 理论上可以铺到不同的地块环上（换个摆放位置），
  // 只按 plan + origin + group 缓存会让第二块地拿到第一块地的草地底。
  const r0 = lot.lotRing?.[0];
  const key = `${lot.origin.x},${lot.origin.y},${group},${lot.lotRing?.length ?? 0}@${r0?.[0] ?? 0},${r0?.[1] ?? 0}`;
  const hit = byKey.get(key);
  if (hit) return hit;
  const res = bridgePlan(lot.plan, { origin: lot.origin, group, lotRing: lot.lotRing });
  byKey.set(key, res);
  return res;
}

/** 生成器 id → 用途分类，只用于统计面板与配色，不影响几何。 */
function useOfGenerator(id: string): LandUse {
  if (id === 'parking') return 'parking';
  if (/park|garden|green|forest|wood|meadow|nature/i.test(id)) return 'park';
  if (/plaza|square|market|transit|civic|sport|play|pitch|court|track/i.test(id)) return 'civic';
  if (/zoning|mall|shop|retail|street|commercial/i.test(id)) return 'commercial';
  if (/industrial|yard|quarry|landfill|logistics|container|work/i.test(id)) return 'industrial';
  return 'green';
}

export function buildScene(model: Model, opts: SceneOptions = {}): Scene {
  const scene = emptyScene();
  const heightScale = opts.heightScale ?? 1;
  const wantLots = opts.lots !== false;
  const pmLots = opts.placemaker ?? [];
  // Placemaker 接管了哪些 patch（没给 patchIndex 的就不抢，纯叠加）
  const taken = new Set<number>();
  for (const l of pmLots) if (l.patchIndex !== undefined) taken.add(l.patchIndex);

  // 1. 地面底板：只覆盖城内，避免地块铺满整张图
  if (opts.ground !== false) {
    for (const patch of model.patches) {
      if (!patch.withinCity) continue;
      if (patch.shape.length < 3) continue;
      scene.surfaces.push({
        name: 'ground',
        group: 'ground',
        ring: toCCW(Array.from(patch.shape)),
        mat: 'ground',
        y: -0.02,
      });
    }
  }

  // 2. 道路网 + 标线
  const net = buildRoadNet(model);
  roadSurfaces(net, scene.surfaces);
  if (opts.markings !== false) roadMarkings(net, scene.surfaces);

  // 3. 地块内容
  if (wantLots) {
    for (let pi = 0; pi < model.patches.length; pi++) {
      const patch = model.patches[pi];
      const ward = patch.ward;
      if (!(ward instanceof ModernWard)) continue;
      const lot = ward.lot;
      if (!lot) continue;

      // 被 Placemaker 接管的这块：铺装/车位/道具一律让出去（它铺得比我们细）。
      // 建筑默认也一起让掉 —— 停车坪、公园本来就没有楼；
      // 需要"Placemaker 铺地 + TownForge 盖楼"的组合时给 keepBuildings: true。
      if (taken.has(pi)) {
        const keep = pmLots.some((l) => l.patchIndex === pi && l.keepBuildings === true);
        if (keep) for (const s of lot.solids) scene.solids.push({ ...s, h: s.h * heightScale });
        continue;
      }

      scene.surfaces.push(...lot.surfaces);
      scene.paths.push(...lot.paths);
      scene.props.push(...lot.props);
      for (const s of lot.solids) {
        const scaled: SolidFeature = { ...s, h: s.h * heightScale };
        scene.solids.push(scaled);
      }

      scene.lots.push({
        index: pi,
        use: lot.use,
        area: lot.block ? Math.abs(lot.block.square) : 0,
        far: lot.buildings,
        buildings: lot.buildings,
        parkingBays: lot.bays,
        trees: lot.trees,
      } satisfies LotInfo);
    }
  }

  // 3.5 Placemaker 接管的地块：铺装走 Plan，不再走 modern.ts
  for (const l of pmLots) {
    const r = bridgeCached(l);
    scene.surfaces.push(...r.surfaces);
    scene.paths.push(...r.paths);
    scene.props.push(...r.props);
    // 台地/料堆是体块（顶面逐顶点高度），不是面 —— 走 solids 才不会跟铺装争同一个 y
    for (const m of r.mounds) {
      let peak = 0;
      for (const h of m.hs) if (h > peak) peak = h;
      scene.solids.push({
        name: m.name,
        group: m.group,
        ring: m.ring,
        y0: 0,
        h: peak,
        wallMat: m.mat,
        roofMat: m.mat,
        kind: 'mound',
        hs: m.hs,
        // 收顶 = 高度的 45%：4 m 的格子上就是不到 1.2 m 的坡，够把悬在崖边的
        // 废车接住，又不至于把一格 16 m² 削成锥子
        inset: peak * 0.45,
      });
    }
    scene.lots.push({
      index: l.patchIndex ?? -1,
      use: useOfGenerator(l.plan.generator),
      // Plan 的 area 是米²，其余地块统计是世界单位² —— 这里统一成米² 交给展示层自己折算
      area: l.plan.area,
      far: 0,
      buildings: 0,
      parkingBays: r.bays.total,
      trees: r.props.reduce((n, p) => n + (isTreeKind(p.kind) ? 1 : 0), 0),
    } satisfies LotInfo);
  }

  // 4. 街道设施
  if (opts.furniture !== false) {
    const f = streetFurniture(model, net);
    scene.props.push(...f.props);
  }

  // 5. 统计
  const byUse: Record<string, number> = {};
  for (const l of scene.lots) byUse[l.use] = (byUse[l.use] ?? 0) + 1;
  const propsByKind: Record<string, number> = {};
  for (const p of scene.props) propsByKind[p.kind] = (propsByKind[p.kind] ?? 0) + 1;

  scene.stats = {
    surfaces: scene.surfaces.length,
    solids: scene.solids.length,
    props: scene.props.length,
    paths: scene.paths.length,
    byUse,
    propsByKind,
  };

  return scene;
}

/** 供 UI 显示用的用途名 */
export function useSummary(scene: Scene): { use: LandUse; n: number }[] {
  return Object.entries(scene.stats.byUse)
    .map(([use, n]) => ({ use: use as LandUse, n }))
    .sort((a, b) => b.n - a.n);
}
