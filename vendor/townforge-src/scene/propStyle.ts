// 道具在 2D 里长什么样 —— 一处定义，两个渲染器共用
// （`render/svg.ts` 出图给测试看、`ui/view2d.ts` 画实时画布）。
//
// 为什么要抽出来：这两处原来各写一份 if 链，种类一多必然漂——
// 同一块公园在编辑器里是绿点、在导出的 SVG 里只有一个圆点、在 3D 里是针叶树，
// 三种读法。更糟的是 `hedge` / `planter` / `fountain` 两个 2D 渲染器都**不画**，
// Placemaker 接进来的花坛和绿篱在编辑器里直接消失，看着像"没铺东西"。
//
// `Record<PropKind, PropStyle>` 是刻意的：新增道具种类时 TS 会直接报缺项。
// 半径和长度都是**世界单位**（1 单位 = 7 m），不是像素——缩放由渲染器管。

import type { MatKey } from './palette';
import type { PropFeature, PropKind } from './types';

export interface PropStyle {
  /** 圆点半径（世界单位）。0 = 2D 里不画这个道具（太小，画了只是一粒噪点）。 */
  r: number;
  mat: MatKey;
  /** `dot` 圆点；`bar` 沿 rot 的一根粗线，给绿篱、花坛这类有方向的用。 */
  shape?: 'dot' | 'bar';
  /** `bar` 的长度（世界单位） */
  len?: number;
  /** `bar` 的线宽（世界单位） */
  width?: number;
}

export const PROP_STYLE: Record<PropKind, PropStyle> = {
  // 树：0.42 ≈ 直径 5.9 m 的树冠，在整城视图里正好读成"一棵树"
  tree: { r: 0.42, mat: 'leaf' },
  treeRound: { r: 0.44, mat: 'leaf' },
  treeSmall: { r: 0.3, mat: 'leafDark' },
  conifer: { r: 0.4, mat: 'leafDark' },
  // 灌木：比树小一圈，是"面"的一部分而不是"点"
  shrub: { r: 0.16, mat: 'leafDark' },
  // 绿篱：4.2 m × 1.4 m 的一段，成排时才读得出来
  hedge: { r: 0, mat: 'leafDark', shape: 'bar', len: 0.6, width: 0.2 },
  planter: { r: 0.22, mat: 'soil' },
  fountain: { r: 0.5, mat: 'water' },
  lamp: { r: 0.14, mat: 'markWhite' },
  lampDouble: { r: 0.14, mat: 'markWhite' },
  // 下面这些在 2D 里小于一个像素，画了只会让画面变脏
  bench: { r: 0, mat: 'wood' },
  bin: { r: 0, mat: 'metal' },
  sign: { r: 0, mat: 'markWhite' },
  bollard: { r: 0, mat: 'metal' },
  shelter: { r: 0, mat: 'metal' },
  car: { r: 0, mat: 'wallPanel' },
  flagpole: { r: 0, mat: 'stonePave' },
  // 堆场三件套：0.05 世界单位 ≈ 0.35 m 半径，在 12k m² 的地块图上约 2 px ——
  // 画出来才能一眼看出"这块地上有几百件东西"，不给半径就成了看不见的实体。
  crate: { r: 0.05, mat: 'metal' },
  barrel: { r: 0.04, mat: 'metal' },
  rock: { r: 0.06, mat: 'gravel' },
  // 残墙：3.2 m 长的一段，在整城视图里是"一道线"而不是一个点
  ruinWall: { r: 0, mat: 'wallConcrete', shape: 'bar', len: 0.46, width: 0.06 },
  // 雕像：基座 2.4 m 见方，2D 里画成实心点（它本来就是广场的中心）
  statue: { r: 0.18, mat: 'stonePave' },
};

/** 树类道具（2D 里都画成圆点，统计里都算树）。 */
export const TREE_KINDS: PropKind[] = ['tree', 'treeRound', 'treeSmall', 'conifer'];

export function isTreeKind(k: PropKind): boolean {
  return TREE_KINDS.includes(k);
}

/**
 * `bar` 造型的实例长度（世界单位）。
 *
 * 实例自己的 `len` 优先（桥接拟合出来的绿篱长度是量出来的，比如 12.4 m 的花坛边），
 * 没给才退回样式表的默认值 × 缩放。2D 两个渲染器 + 3D 都用这一处，别各写一遍。
 */
export function barLength(p: Pick<PropFeature, 'len' | 'scale'>, look: PropStyle): number {
  return p.len ?? (look.len ?? 0.6) * p.scale;
}

/** 2D 里这个道具画成什么。`null` = 不画。 */
export type PropDraw =
  | { shape: 'bar'; len: number; width: number; mat: MatKey }
  | { shape: 'dot'; r: number; mat: MatKey };

/**
 * "这个道具在 2D 里怎么画"的**唯一**判断点。
 *
 * 抽出来是因为两个渲染器（`render/svg.ts`、`ui/view2d.ts`）加上联系表都各自写过一遍，
 * 而且三处都踩了同一个坑：先写 `if (look.r <= 0) continue` 再做 bar 分支——
 * `hedge` 的 `r` 就是 0（它没有半径，只有长度），于是绿篱在 2D 里从来没画出来过。
 * 现在判断只有一份，并且能被测试直接断言。
 */
export function propDraw(p: Pick<PropFeature, 'kind' | 'scale' | 'len'>): PropDraw | null {
  const look = PROP_STYLE[p.kind];
  if (!look) return null;
  if (look.shape === 'bar') {
    return { shape: 'bar', len: barLength(p, look), width: (look.width ?? 0.2) * p.scale, mat: look.mat };
  }
  if (look.r <= 0) return null;
  return { shape: 'dot', r: look.r * p.scale, mat: look.mat };
}
