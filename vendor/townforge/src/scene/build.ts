// 把模型汇总成一份 Scene：道路、标线、地块内容、街道设施。
// 2D 编辑器、three.js 预览、GLB 导出都从这里取数据，三者不可能不一致。

import type { Model } from '../town/model';
import { buildRoadNet, roadSurfaces } from '../road/system';
import { roadMarkings } from '../road/markings';
import { streetFurniture } from '../town/furniture';
import { roadsideRailings } from '../town/railings';
import { ModernWard } from '../town/modern';
import { meters, u } from '../core/units';
import { planHeight, zoneKindOf } from '../bridge/zone';
import { buildBridge, type BridgeReport } from '../bridge/placemaker';
import { slotsToSolids } from '../bridge/solids';
import type { LandUse, LotInfo, Scene, SolidFeature } from './types';
import { emptyScene, toCCW } from './types';

/**
 * 楼从哪来：
 *   · `townforge`   —— 地块内容由 `ModernWard` 排（默认，现状）
 *   · `placemaker`  —— 只画 Placemaker 楼位（`packages/slots` 切成的小地块）
 *   · `both`        —— 两套都画，靠左栏「Placemaker 楼位」那行单独开关做并排对照
 *
 * 只切"楼"，铺装 / 绿地 / 车位 / 树仍然用 TownForge 的 ——
 * 对照的目的是比"路上盖什么"，不是比底图。
 */
export type ContentSource = 'townforge' | 'placemaker' | 'both';

export interface SceneOptions {
  /** 建筑高度倍率 */
  heightScale?: number;
  /** 楼从哪来（见 `ContentSource`） */
  contentSource?: ContentSource;
  /** 是否生成道路标线 */
  markings?: boolean;
  /** 是否生成行道树 / 路灯 */
  furniture?: boolean;
  /** 是否生成地块内容（建筑、铺装、车位） */
  lots?: boolean;
  /** 是否铺地面底板 */
  ground?: boolean;
  /** 是否包含城墙（若模型有） */
}

/**
 * 桥的结果按 (model, revision) 缓存。
 *
 * 为什么必须缓存：`buildBridge` 对每个街区都要跑一次 `cutZoning`，
 * 而 `buildScene` 在 2D 每帧、3D 每次重建、GLB 每次导出都会被调用。
 * 不缓存的话光是开一次三维预览就要重算几百次切分。
 * revision 由 `Model.buildAll()` 递增，所以拖动拓扑点后缓存会自动失效。
 */
let bridgeCache: { model: Model; rev: number; report: BridgeReport } | null = null;

export function bridgeOfModel(model: Model): BridgeReport {
  if (bridgeCache && bridgeCache.model === model && bridgeCache.rev === model.revision) return bridgeCache.report;
  const report = buildBridge(model);
  bridgeCache = { model, rev: model.revision, report };
  return report;
}

export function buildScene(model: Model, opts: SceneOptions = {}): Scene {
  const scene = emptyScene();
  const heightScale = opts.heightScale ?? 1;
  const wantLots = opts.lots !== false;
  const src: ContentSource = opts.contentSource ?? 'townforge';
  const wantTfSolids = src !== 'placemaker';
  const wantPmSolids = src !== 'townforge';
  /**
   * 场地口径要不要跟着 Placemaker 走。
   *
   * 只换"楼"是不够的：TownForge 的地块里还有**按它自己楼位**撒的东西 ——
   * 车位线、停车场沥青、内部路、园路、围栏、车。这些一旦留着，
   * Placemaker 的楼位就会压在 TF 的车位上，或者楼中间横着一条 TF 的内部路，
   * 看起来像"两套内容重叠"（用户核对时就是这么发现的）。
   *
   * 所以只要不是纯 TownForge 模式，场地就过滤成"地面 + 绿化 + 街道家具"：
   * 标线、停车场地、内部路一律不画。真正该由 Placemaker 产出的车位线与园路，
   * 属于 G5（把 parking / park 两个家族的生成规则搬过来），不是这一刀。
   */
  const pmGround = src !== 'townforge';
  /** PM 模式下保留的地面：地形与绿化。硬质铺装保留（城市总得有地），标线与停车场地不保留。 */
  const PM_KEEP_MAT = new Set(['ground', 'grass', 'grassLight', 'grassDark', 'water', 'soil', 'paver', 'concrete', 'brickPave', 'stonePave', 'gravel']);
  /**
   * 这些用途**不排建筑**，所以它们的地块内容不该被 PM 过滤掉。
   *
   * 这一条是对账之后加的（`test/bridge-parking.test.ts`）：停车场的地块里有
   * 690 条车位线 + 7 块沥青场地 + 98 棵树，公园地里有 30 块草地底 ——
   * 这些正是 Placemaker **还没有**对应规则的产出（park 族不出草地底，台账 Q-008；
   * parking 族要出入口接街才能跑，桥里还没有 access）。
   * 一刀切过滤的结果是"PM 模式下停车场变成一片空地"，比两套重叠更糟。
   * 所以：有楼的地块才需要清场地（楼位会压住 TF 的内部路与车位），没楼的地块原样保留。
   */
  const OPEN_USE = new Set(['parking', 'park', 'green', 'reserved']);

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
    for (const patch of model.patches) {
      const ward = patch.ward;
      if (!(ward instanceof ModernWard)) continue;
      const lot = ward.lot;
      if (!lot) continue;

      if (pmGround && lot.use === 'parking') {
        // 停车场：**换成 PM 排的车位阵列**（`bridge/parking.ts`）。
        // 底盘（沥青）仍用 TF 的，只把车位线换成 PM 的 —— 否则两套车位线会叠在一起。
        // 门禁不通过（没有接街车行口）时 PM 一个位都不排，这时**保留 TF 的**，
        // 不要清空：空地比"没有车位的停车场"更难看，而且用户会以为渲染坏了。
        const pmLot = bridgeOfModel(model).parking.lots.find((l) => l.patchIndex === model.patches.indexOf(patch));
        scene.surfaces.push(...lot.surfaces.filter((f) => f.mat !== 'markWhite'));
        if (pmLot?.ready && pmLot.stalls > 0) {
          scene.surfaces.push(...pmLot.surfaces);
        } else {
          scene.surfaces.push(...lot.surfaces.filter((f) => f.mat === 'markWhite'));
        }
        scene.props.push(...lot.props.filter((p) => p.kind !== 'car'));
      } else if (pmGround && (lot.use === 'park' || lot.use === 'green')) {
        // 公园 / 绿地：**分层接管** —— 地表（草地/铺装）仍用 TownForge 的，
        // 园路与树换成我们排的。理由见 Q-008：PM 的 park 族不出草地底，
        // 全换过去公园会变成一块空地。
        const pk = bridgeOfModel(model).park.parks.find((x) => x.patchIndex === model.patches.indexOf(patch));
        scene.surfaces.push(...lot.surfaces);
        if (pk && (pk.paths > 0 || pk.trees > 0)) {
          scene.paths.push(...pk.pathFeatures);
          scene.props.push(...pk.propFeatures);
        } else {
          scene.paths.push(...lot.paths);
          scene.props.push(...lot.props);
        }
      } else if (pmGround && !OPEN_USE.has(lot.use)) {
        // 只留地面与绿化；内部路 / 园路 / 围栏按 TF 楼位画的，PM 笔下一律不画
        scene.surfaces.push(...lot.surfaces.filter((f) => PM_KEEP_MAT.has(f.mat)));
        // 车是 TF 停车方案的产物（车位朝向、阵列间距都是它自己的规则）
        scene.props.push(...lot.props.filter((p) => p.kind !== 'car'));
      } else {
        scene.surfaces.push(...lot.surfaces);
        scene.paths.push(...lot.paths);
        scene.props.push(...lot.props);
      }
      // 楼高出口统一成「层数 × 层高」（米）：
      // TownForge 原来的 h（单位）可以是 2.63 → 18.4 m 这种"半层"高度，
      // 2D 看着没事，但一进 GLB / Placemaker 的楼层体系就说不通（"18.4 m 是几层？"）。
      // 层高取自 KIND_SPECS（住宅 3.0 / 办公 4.0 / 商业 4.5 / 工业 8.0 m），
      // 层数由 `heightScale` 缩放后夹进该用途的层数区间 —— 于是 heightScale 的语义
      // 从"把楼拉高"变成"层数倍数"，量出来的层高永远是真实值。
      for (const s of wantTfSolids ? lot.solids : []) {
        const hM = meters(s.h) * heightScale;
        const kind = zoneKindOf(lot.use, hM);
        const hReg = kind ? planHeight(hM, kind).heightM : hM;
        const scaled: SolidFeature = { ...s, h: u(hReg) };
        scene.solids.push(scaled);
      }

      scene.lots.push({
        index: model.patches.indexOf(patch),
        use: lot.use,
        area: lot.block ? Math.abs(lot.block.square) : 0,
        far: lot.buildings,
        buildings: lot.buildings,
        parkingBays: lot.bays,
        trees: lot.trees,
      } satisfies LotInfo);
    }
  }

  // 3b. Placemaker 楼位（米制桥 → 体块）
  if (wantPmSolids) {
    const bridge = bridgeOfModel(model);
    scene.solids.push(...slotsToSolids(bridge.slots).solids);
  }

  // 4. 街道设施（护栏拆成横杆 path + 立柱 prop，见 town/rails.ts）
  if (opts.furniture !== false) {
    const f = streetFurniture(model, net);
    scene.props.push(...f.props);
    const rail = roadsideRailings(net);
    scene.paths.push(...rail.paths);
    scene.props.push(...rail.props);
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
