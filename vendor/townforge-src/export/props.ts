// 道具低模：树、路灯、长椅、垃圾桶、指示牌、花坛、公交亭、喷泉、护柱、汽车。
//
// 每个道具都按"能认出是什么"的最低面数来做：
//   树 24 面、路灯 33 面、长椅 24 面、垃圾桶 15 面。
// 城市里道具数量是几百到上千，单件多花 20 面就会多出几万个三角形。

import { Pt } from '../core/geom';
import type { PropFeature } from '../scene/types';
import { MAT } from '../scene/palette';
import type { Primitive } from './mesh';
import { box, cone, cylinder, flatPolygon } from './mesh';

const WHITE: [number, number, number] = [0.94, 0.94, 0.9];

export function buildProp(p: PropFeature, baseY = 0.01): Primitive[] {
  const g = p.group;
  const c = p.at;
  const s = p.scale;
  const rot = p.rot;
  const y0 = baseY;
  const out: Primitive[] = [];
  const dirX = Math.cos(rot);
  const dirY = Math.sin(rot);

  switch (p.kind) {
    case 'tree': {
      const th = 0.6 * s;
      out.push(cylinder(c, 0.046 * s, y0, th, `${p.name}_t`, g, MAT.trunk, 4));
      out.push(cone(c, 0.33 * s, y0 + th * 0.8, 0.86 * s, `${p.name}_c1`, g, MAT.leaf, 6));
      out.push(cone(c, 0.23 * s, y0 + th * 0.8 + 0.5 * s, 0.58 * s, `${p.name}_c2`, g, MAT.leafDark, 6));
      break;
    }
    case 'treeSmall': {
      const th = 0.42 * s;
      out.push(cylinder(c, 0.04 * s, y0, th, `${p.name}_t`, g, MAT.trunk, 4));
      out.push(cone(c, 0.26 * s, y0 + th * 0.8, 0.62 * s, `${p.name}_c1`, g, MAT.leafDark, 6));
      break;
    }
    case 'treeRound': {
      // 阔叶树：冠**宽于高**（0.76 × 0.98 世界单位 = 5.3 × 6.9 m 的一团），两层错开半径。
      // `tree` 是细高的锥（11 m 高的圣诞树），在公园里一排排站开像苗圃；这种冠形才是"树冠是一团"。
      // 总高刻意和 `tree` 对齐（1.40 vs 1.57），不然混种时会看出"两种身高的树"。
      // 8 边换圆润，一棵 28 面对得起。
      const th = 0.55 * s;
      out.push(cylinder(c, 0.048 * s, y0, th, `${p.name}_t`, g, MAT.trunk, 4));
      out.push(cone(c, 0.38 * s, y0 + th * 0.76, 0.62 * s, `${p.name}_c1`, g, MAT.leaf, 8));
      out.push(cone(c, 0.3 * s, y0 + th * 1.45, 0.6 * s, `${p.name}_c2`, g, MAT.leaf, 8));
      break;
    }
    case 'shrub': {
      // 灌木：0.12 半径（直径 1.7 m）× 0.18 高（1.3 m），两个错开的小锥拼成一丛。
      // 错位量从 seed 来，一排灌木才不会长得一模一样——这点很重要：
      // Placemaker 的绿篱就是几十个 bush 排成一条线，齐整反而假。
      const r = 0.12 * s;
      const h = 0.18 * s;
      const off = ((p.seed % 5) - 2) * 0.03 * s;
      out.push(cone(c, r, y0, h, `${p.name}_s1`, g, MAT.leafDark, 6));
      out.push(cone(new Pt(c.x + off, c.y - off), r * 0.74, y0, h * 1.1, `${p.name}_s2`, g, MAT.leaf, 5));
      break;
    }
    case 'conifer': {
      const th = 0.3 * s;
      out.push(cylinder(c, 0.04 * s, y0, th * 1.6, `${p.name}_t`, g, MAT.trunk, 4));
      out.push(cone(c, 0.26 * s, y0 + th, 0.95 * s, `${p.name}_c1`, g, MAT.leafDark, 6));
      out.push(cone(c, 0.19 * s, y0 + th + 0.5 * s, 0.8 * s, `${p.name}_c2`, g, MAT.leaf, 6));
      out.push(cone(c, 0.11 * s, y0 + th + 1.0 * s, 0.6 * s, `${p.name}_c3`, g, MAT.leafDark, 6));
      break;
    }
    case 'lamp':
    case 'lampDouble': {
      const h = 1.8 * s;
      out.push(cylinder(c, 0.026 * s, y0, h, `${p.name}_p`, g, MAT.metal, 3));
      const arms = p.kind === 'lampDouble' ? [1, -1] : [1];
      for (const side of arms) {
        const ax = c.x + dirX * 0.2 * side;
        const ay = c.y + dirY * 0.2 * side;
        out.push(...box(new Pt(ax, ay), 0.4 * s, 0.045 * s, y0 + h - 0.03, 0.045 * s, rot, `${p.name}_a`, g, MAT.metal));
        out.push(
          ...box(new Pt(c.x + dirX * 0.4 * side, c.y + dirY * 0.4 * side), 0.15 * s, 0.09 * s, y0 + h - 0.08, 0.06 * s, rot, `${p.name}_h`, g, WHITE, WHITE)
        );
      }
      break;
    }
    case 'bench': {
      out.push(...box(c, 0.36 * s, 0.1 * s, y0 + 0.09 * s, 0.03 * s, rot, `${p.name}_s`, g, MAT.wood));
      out.push(...box(new Pt(c.x - dirX * 0.02, c.y - dirY * 0.02), 0.36 * s, 0.03 * s, y0 + 0.16 * s, 0.16 * s, rot, `${p.name}_b`, g, MAT.wood));
      out.push(cylinder(new Pt(c.x - 0.13, c.y), 0.014, y0, 0.1, `${p.name}_l1`, g, MAT.metal, 3));
      out.push(cylinder(new Pt(c.x + 0.13, c.y), 0.014, y0, 0.1, `${p.name}_l2`, g, MAT.metal, 3));
      break;
    }
    case 'bin': {
      out.push(cylinder(c, 0.055 * s, y0, 0.24 * s, `${p.name}_b`, g, MAT.metal, 5));
      break;
    }
    case 'sign': {
      out.push(cylinder(c, 0.018 * s, y0, 0.9 * s, `${p.name}_p`, g, MAT.metal, 3));
      out.push(...box(new Pt(c.x, c.y), 0.26 * s, 0.03 * s, y0 + 0.78 * s, 0.2 * s, rot, `${p.name}_s`, g, WHITE, WHITE));
      break;
    }
    case 'planter': {
      out.push(...box(c, 0.5 * s, 0.34 * s, y0, 0.2 * s, rot, `${p.name}_p`, g, MAT.stonePave, MAT.grass));
      break;
    }
    case 'shelter': {
      const h = 0.52 * s;
      out.push(...box(c, 1.15 * s, 0.52 * s, y0 + h, 0.05 * s, rot, `${p.name}_roof`, g, MAT.metal));
      out.push(...box(new Pt(c.x - dirX * 0.5 * s, c.y - dirY * 0.5 * s), 0.05 * s, 0.05 * s, y0, h, rot, `${p.name}_c1`, g, MAT.metal));
      out.push(...box(new Pt(c.x + dirX * 0.5 * s, c.y + dirY * 0.5 * s), 0.05 * s, 0.05 * s, y0, h, rot, `${p.name}_c2`, g, MAT.metal));
      out.push(...box(new Pt(c.x - dirY * 0.2 * s, c.y + dirX * 0.2 * s), 1.1 * s, 0.03 * s, y0 + 0.1, h - 0.14, rot, `${p.name}_b`, g, MAT.wallGlass));
      break;
    }
    case 'fountain': {
      out.push(cylinder(c, 0.55 * s, y0, 0.12 * s, `${p.name}_basin`, g, MAT.stonePave, 10));
      out.push(cylinder(c, 0.36 * s, y0 + 0.12 * s, 0.02, `${p.name}_water`, g, MAT.water, 10));
      out.push(cylinder(c, 0.06 * s, y0 + 0.12 * s, 0.3 * s, `${p.name}_jet`, g, MAT.stonePave, 5));
      break;
    }
    case 'bollard': {
      out.push(cylinder(c, 0.035 * s, y0, 0.14 * s, `${p.name}_b`, g, MAT.metal, 4));
      break;
    }
    case 'hedge': {
      // 长度可被实例覆盖：桥接把成排的灌木拟合成绿篱时，一段可能是 12 m 的花坛边，
      // 也可能只是 3 m 的一条短边。宽度/高度仍是造型本身的（1.54 × 1.12 m）。
      const len = p.len ?? 0.6 * s;
      out.push(...box(c, len, 0.22 * s, y0, 0.16 * s, rot, `${p.name}_h`, g, MAT.leafDark, MAT.leaf));
      break;
    }
    case 'car': {
      out.push(...box(c, 0.44 * s, 0.19 * s, y0, 0.09 * s, rot, `${p.name}_b`, g, MAT.wallPanel));
      out.push(...box(new Pt(c.x - dirX * 0.03, c.y - dirY * 0.03), 0.24 * s, 0.17 * s, y0 + 0.09 * s, 0.07 * s, rot, `${p.name}_r`, g, MAT.wallGlass));
      break;
    }
    case 'flagpole': {
      out.push(cylinder(c, 0.02 * s, y0, 1.6 * s, `${p.name}_p`, g, MAT.stonePave, 3));
      const flag = [
        new Pt(c.x, c.y),
        new Pt(c.x + 0.32 * s, c.y),
        new Pt(c.x + 0.32 * s, c.y + 0.2 * s),
        new Pt(c.x, c.y + 0.2 * s),
      ];
      const f = flatPolygon(flag, y0 + 1.4 * s, `${p.name}_f`, g, MAT.markYellow);
      if (f) {
        f.doubleSided = true;
        out.push(f);
      }
      break;
    }
    case 'crate': {
      // 堆场杂物（集装箱、托盘、料箱、废料堆）：一个 1.1 × 1.1 × 0.9 m 的扁方盒，12 面。
      // 不按分类换材质是有意的 —— `PropFeature` 里没有材质字段，为它开一条通道不值得；
      // 灰盒子在堆场里本来就读成"一垛货"，比 809 张长椅接近真相。
      out.push(...box(c, 0.16 * s, 0.16 * s, y0, 0.13 * s, rot, `${p.name}_c`, g, MAT.wallPanel, MAT.metal));
      break;
    }
    case 'barrel': {
      // 油桶 / 轮胎堆：0.32 m 半径、1 m 高的矮圆柱，5 边够圆
      out.push(cylinder(c, 0.045 * s, y0, 0.14 * s, `${p.name}_d`, g, MAT.metal, 5));
      break;
    }
    case 'rock': {
      // 石堆 / 瓦砾：一个 6 边的矮锥（1.8 m 直径、0.7 m 高），比盒子更像自然料堆。
      // 半径按 seed 抖一点，一片石头才不会像网格。
      const r = 0.13 * s * (0.85 + ((p.seed % 7) / 7) * 0.3);
      out.push(cone(c, r, y0, 0.1 * s, `${p.name}_r`, g, MAT.gravel, 6));
      break;
    }
    case 'statue': {
      // 基座 + 人像：两块方盒，24 面。广场中心的焦点物，值得比一个水盘多给几面。
      out.push(...box(c, 0.34 * s, 0.34 * s, y0, 0.2 * s, rot, `${p.name}_base`, g, MAT.stonePave, MAT.concrete));
      out.push(...box(c, 0.16 * s, 0.14 * s, y0 + 0.2 * s, 0.4 * s, rot, `${p.name}_fig`, g, MAT.metal));
      break;
    }
    case 'ruinWall': {
      // 残墙：3.2 × 0.28 × 1.1 m 的一段实墙，12 面。
      // 不做"断口"这种细节 —— 7 段墙在一整块 12500 m² 的地上，
      // 影响可读性的是"有墙"这件事本身，不是墙的断面造型。
      out.push(...box(c, 0.46 * s, 0.04 * s, y0, 0.16 * s, rot, `${p.name}_w`, g, MAT.wallConcrete, MAT.concrete));
      break;
    }
    default:
      break;
  }

  return out;
}
