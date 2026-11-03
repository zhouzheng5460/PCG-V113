// 全项目**唯一的单位真源**。
//
// 为什么要单独一个文件：单位是"影响实现"的规则里最底层的一条。
// 之前它藏在 `road/spec.ts` 里（一个道路规范文件定义了整个世界的尺度），
// 结果除了道路之外的模块都要"记得去道路文件里拿换算"，这是一种迟早出错的结构。
//
// 两个刻意的决定：
//   1. **米是唯一的真源**（Placemaker / slots / 建筑规范 / 导出全都用米），
//      世界单位只是渲染与生成器的内部长度单位，`UNIT_M = 7` 是它和米的换算比。
//   2. **不在几何里散落 `* 7`**。要米就调 `meters(u)`，要回单位就调 `u(m)`，
//      两个函数名就是"方向"，读代码不用在脑子里换算。

/**
 * 1 世界单位 = 7 m。
 *
 * 这个数是**反推出来的**，不是拍的：车位 0.36 × 0.72 单位 = 2.5 × 5.0 m，
 * 正好卡在《城市道路工程设计规范》的平行式小客车位尺寸上；
 * 城市半径 86 单位 ≈ 600 m，街区中位边长 ≈ 85 m，也对得上"居住区 / 街坊"级别。
 */
export const UNIT_M = 7;

/** 米 → 世界单位。几何要用单位，就在这里转一次。 */
export function u(meter: number): number {
  return meter / UNIT_M;
}

/** 世界单位 → 米。面积请用 `sqMeters()`，别自己乘两次。 */
export function meters(unit: number): number {
  return unit * UNIT_M;
}

/** 单位² → 米² */
export function sqMeters(unitArea: number): number {
  return unitArea * UNIT_M * UNIT_M;
}

/** 米² → 单位² */
export function sqUnits(meterArea: number): number {
  return meterArea / (UNIT_M * UNIT_M);
}
