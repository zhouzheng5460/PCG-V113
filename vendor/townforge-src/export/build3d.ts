// 从 Scene 构建 3D 网格数据。
// 坐标系：地图平面 (x, y) → 3D (x, 0, y)，高度沿 +Y。这样从上方俯视与 2D 编辑器完全一致。

import { Pt } from '../core/geom';
import type { Model } from '../town/model';
import { buildScene, type SceneOptions } from '../scene/build';
import { MAT } from '../scene/palette';
import type { Primitive } from './mesh';
import { flatPolygon, extrudeSolid, terraceSolid, cylinder, wallSolid, mergePrimitives } from './mesh';
import { buildProp } from './props';
import { bandBetween } from '../road/system';

export type { Primitive } from './mesh';

export interface Build3DOptions extends SceneOptions {
  /** 是否包含城墙/塔楼 */
}

/** 场景 → 三角形。这是 2D 预览与 GLB 导出的唯一出口 */
export function buildTownMeshes(model: Model, opts: Build3DOptions = {}): Primitive[] {
  const scene = buildScene(model, opts);
  const out: Primitive[] = [];

  // 铺装 / 标线 / 车位线 / 道具按 (分组, 颜色) 合并成大网格。
  // 不合并的话一座 124 街区的城会导出上万个节点，GLB 体积几乎全花在节点开销上
  // （三角形只有四万，文件却接近 10 MB）。建筑保持独立，方便在 Blender 里逐栋选。
  const flats = new Map<string, Primitive[]>();
  const addFlat = (prim: Primitive | null) => {
    if (!prim) return;
    const key = `${prim.group}|${prim.color.join(',')}`;
    const list = flats.get(key);
    if (list) list.push(prim);
    else flats.set(key, [prim]);
  };

  // 贴地要素：铺装、路面、标线、车位线
  for (const s of scene.surfaces) addFlat(flatPolygon(s.ring, s.y, s.name, s.group, MAT[s.mat]));

  // 建筑与围墙体块
  for (const s of scene.solids) {
    // 台地/料堆：顶面按顶点高度走，不能用统一高度挤出
    if (s.kind === 'mound' && s.hs && s.hs.length === s.ring.length) {
      out.push(...terraceSolid(s.ring, s.hs, s.y0, s.name, s.group, MAT[s.roofMat], MAT[s.wallMat], s.inset ?? 0));
      continue;
    }
    out.push(...extrudeSolid(s.ring, s.y0, s.h, s.name, s.group, MAT[s.roofMat], MAT[s.wallMat]));
  }

  // 线性要素：内部道路、入口路、园路、围栏
  for (const p of scene.paths) {
    if (p.height && p.height > 0.05) {
      const w = wallSolid(p.pts, p.width, p.y, p.height, p.name, p.group, MAT[p.mat], false);
      if (w) out.push(w);
    } else {
      const band = bandBetween(p.pts, p.width / 2, -p.width / 2);
      if (!band) continue;
      addFlat(flatPolygon(band, p.y, p.name, p.group, MAT[p.mat]));
    }
  }

  // 道具
  // 站在台地上的道具自带 baseY（料堆顶面），其余落在默认地面
  for (const p of scene.props) for (const prim of buildProp(p, p.baseY ?? 0.022)) addFlat(prim);

  for (const [key, list] of flats) {
    const group = list[0].group;
    const merged = mergePrimitives(list, `${group}_${list.length}`, group);
    if (merged) out.push(merged);
    void key;
  }

  return out;
}

/** 兼容旧调用：只要平面几何时给一个空实现 */
export function flatOnly(ring: Pt[], y: number, name: string, group: string, color: [number, number, number]) {
  return flatPolygon(ring, y, name, group, color);
}
