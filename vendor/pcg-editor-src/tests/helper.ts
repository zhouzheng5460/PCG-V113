/**
 * Phase 2（背景图比例标定）会用到的换算：像素距离 → 世界米。
 * 先在这里固定公式，避免比例标定阶段再重写一遍换算逻辑。
 */
export function photoToWorld(px: number, metersPerPixel: number): number {
  return px * metersPerPixel;
}

export function metersPerPixelFromCalibration(
  pxDistance: number,
  realMeters: number,
): number {
  if (pxDistance <= 0) return 0;
  return realMeters / pxDistance;
}
