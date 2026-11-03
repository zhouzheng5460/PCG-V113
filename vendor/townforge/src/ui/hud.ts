// 要素清点：给左栏每个图层后面那个小数字提供数据。
//
// 这里以前是一整套"HUD 分析"——场景总量 / 用地构成 / 生成规则体检 / 道路分级里程，
// 带指标区间着色和一句结论。用户核对时说的是"移除HUD分析"：那几组数字调参时没人看，
// 而它默认展开置顶，把真正天天要点的"显示控制"挤到了首屏之外。
//
// 所以只留下**唯一还被用到的那一份数据**：每个图层名下有多少个要素（面 / 体 / 道具 / 线），
// 左栏列表靠它数。`measureCover`（栅格覆盖率，48 ms）和路网统计一并不再算 ——
// 它们只服务于已删掉的那几个面板，每次重生成白烧 60~80 ms。

import type { Model } from '../town/model';
import { buildScene, type SceneOptions } from '../scene/build';
import { layerOfPath, layerOfProp, layerOfSolid, layerOfSurface } from '../scene/layers';

export interface Diagnostics {
  /** 各类要素数量 */
  counts: { solids: number; props: number; surfaces: number; paths: number };
  /** 每个图层有多少个要素（左栏列表上的小数字） */
  layerCounts: Record<string, number>;
}

/**
 * 把一座城清点一遍。调用点是"重生成 / 重建"，不进逐帧循环。
 *
 * `opts` 必须从调用方传进来：楼的来源（`contentSource`）不同，
 * 体块数量就不同 —— 拿默认参数清点会让左栏「Placemaker 楼位」永远显示 0，
 * 而画面上明明画着 1008 栋（这个假 0 我们真踩过一次）。
 */
export function diagnose(model: Model, opts: SceneOptions = {}): Diagnostics {
  const scene = buildScene(model, opts);

  const layerCounts: Record<string, number> = {};
  const bump = (id: string) => {
    layerCounts[id] = (layerCounts[id] ?? 0) + 1;
  };
  for (const s of scene.surfaces) bump(layerOfSurface(s));
  for (const s of scene.solids) bump(layerOfSolid(s));
  for (const p of scene.props) bump(layerOfProp(p));
  for (const p of scene.paths) bump(layerOfPath(p));

  return {
    counts: {
      solids: scene.solids.length,
      props: scene.props.length,
      surfaces: scene.surfaces.length,
      paths: scene.paths.length,
    },
    layerCounts,
  };
}
