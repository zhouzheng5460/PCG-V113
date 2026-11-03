import { BackgroundImage } from './model';
import { Bounds, emptyBounds } from './polygon';
import { Vec2, dist } from './vec';

/**
 * 背景图坐标换算（PRD §18~§21）
 *
 * 图片像素坐标 (px, py)：px 向右，py 向下（图片自身坐标系）
 * 世界坐标 (wx, wy)：x 向东，y 向北，单位 m
 *
 * world = origin + R(rotation) · (px·mpp, −py·mpp)
 * 即先把 py 翻成向上，再乘米/像素，再绕原点旋转，最后平移到 origin。
 */

export type BgTransform = Pick<BackgroundImage, 'origin' | 'metersPerPixel' | 'rotation'>;

export function imagePxToWorld(bg: BgTransform, px: Vec2): Vec2 {
  const cos = Math.cos(bg.rotation);
  const sin = Math.sin(bg.rotation);
  const ux = px.x * bg.metersPerPixel;
  const uy = -px.y * bg.metersPerPixel;
  return {
    x: bg.origin.x + ux * cos - uy * sin,
    y: bg.origin.y + ux * sin + uy * cos,
  };
}

export function worldToImagePx(bg: BgTransform, w: Vec2): Vec2 {
  const cos = Math.cos(bg.rotation);
  const sin = Math.sin(bg.rotation);
  const dx = w.x - bg.origin.x;
  const dy = w.y - bg.origin.y;
  const ux = dx * cos + dy * sin;
  const uy = -dx * sin + dy * cos;
  const m = bg.metersPerPixel || 1e-9;
  return { x: ux / m, y: -uy / m };
}

/** 图片四角的世界坐标（顺序：左上、右上、右下、左下） */
export function imageCornersWorld(bg: BackgroundImage): Vec2[] {
  const w = bg.widthPx;
  const h = bg.heightPx;
  return [
    imagePxToWorld(bg, { x: 0, y: 0 }),
    imagePxToWorld(bg, { x: w, y: 0 }),
    imagePxToWorld(bg, { x: w, y: h }),
    imagePxToWorld(bg, { x: 0, y: h }),
  ];
}

export function backgroundBounds(bg: BackgroundImage): Bounds {
  const pts = imageCornersWorld(bg);
  if (!pts.length) return emptyBounds();
  let minX = Infinity;
  let minY = Infinity;
  let maxX = -Infinity;
  let maxY = -Infinity;
  for (const p of pts) {
    minX = Math.min(minX, p.x);
    minY = Math.min(minY, p.y);
    maxX = Math.max(maxX, p.x);
    maxY = Math.max(maxY, p.y);
  }
  return { minX, minY, maxX, maxY };
}

export interface CalibrationResult {
  origin: Vec2;
  metersPerPixel: number;
  /** 两点在图片上的像素距离 */
  pixelDistance: number;
  /** 标定前后的比例变化 */
  ratio: number;
}

/**
 * 两点标定（PRD §20）
 * 保持 A 点位置不动，把图片缩放成「A→B 的像素距离 = 实际米数」。
 */
export function calibrate(
  bg: BgTransform,
  aWorld: Vec2,
  bWorld: Vec2,
  realMeters: number,
): CalibrationResult | null {
  if (!(realMeters > 0)) return null;
  const aPx = worldToImagePx(bg, aWorld);
  const bPx = worldToImagePx(bg, bWorld);
  const pixelDistance = dist(aPx, bPx);
  if (pixelDistance < 1e-6) return null;

  const metersPerPixel = realMeters / pixelDistance;
  // 让 A 点保持原位
  const offset = imagePxToWorld({ ...bg, metersPerPixel, origin: { x: 0, y: 0 } }, aPx);
  const origin = { x: aWorld.x - offset.x, y: aWorld.y - offset.y };

  return {
    origin,
    metersPerPixel,
    pixelDistance,
    ratio: metersPerPixel / (bg.metersPerPixel || metersPerPixel),
  };
}

/** 标定质量提示：两张图上的参考长度是否合理 */
export function calibrationWarning(realMeters: number, pixelDistance: number): string | null {
  if (pixelDistance < 20) return '参考线太短（< 20px），标定误差会很大';
  if (realMeters / pixelDistance > 5) return '比例异常：1 像素超过 5m，检查是否填错单位';
  if (realMeters / pixelDistance < 0.001) return '比例异常：1 像素小于 1mm，检查是否填错单位';
  return null;
}

/** 让图片以大概「占视口宽度 70%」的尺寸出现，用于导入后的初始摆放 */
export function fitBackgroundToView(
  widthPx: number,
  heightPx: number,
  viewCenter: Vec2,
  viewWidthWorld: number,
): BackgroundImage {
  const targetWorldWidth = Math.max(10, viewWidthWorld * 0.7);
  const metersPerPixel = targetWorldWidth / Math.max(1, widthPx);
  const w = widthPx * metersPerPixel;
  const h = heightPx * metersPerPixel;
  return {
    src: '',
    opacity: 0.45,
    origin: { x: viewCenter.x - w / 2, y: viewCenter.y + h / 2 },
    metersPerPixel,
    rotation: 0,
    locked: false,
    visible: true,
    widthPx,
    heightPx,
  };
}
