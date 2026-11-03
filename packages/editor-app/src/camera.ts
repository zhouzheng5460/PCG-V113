// 2D 相机。世界坐标 y 朝上，屏幕 y 朝下 —— 换号只在这个文件里发生一次。
//
// 一条经验（来自 PCG `camera.ts`）：**缩放要以光标为锚点**。
// 以屏幕中心为锚点时，用户每滚一次轮都要重新找刚才那个位置，编辑大图会疯。
// 所以这里的 `zoomAt` 必须接收锚点，并保证"锚点下的世界坐标"在缩放前后不变。

import type { Bounds } from './polygon.ts';
import type { Vec2 } from './vec.ts';

export class Camera2D {
  /** 屏幕中心对应的世界坐标 */
  cx = 0;
  cy = 0;
  /** 像素 / 米 */
  zoom = 5;
  width = 1;
  height = 1;

  setViewport(w: number, h: number): void {
    this.width = Math.max(1, w);
    this.height = Math.max(1, h);
  }

  toScreen(p: Vec2): Vec2 {
    return {
      x: this.width / 2 + (p.x - this.cx) * this.zoom,
      y: this.height / 2 - (p.y - this.cy) * this.zoom,
    };
  }

  toWorld(p: Vec2): Vec2 {
    return {
      x: this.cx + (p.x - this.width / 2) / this.zoom,
      y: this.cy - (p.y - this.height / 2) / this.zoom,
    };
  }

  /** 世界长度 → 像素 */
  px(meters: number): number {
    return meters * this.zoom;
  }

  /** 像素 → 世界长度（命中容差用） */
  meters(px: number): number {
    return px / this.zoom;
  }

  panBy(dxScreen: number, dyScreen: number): void {
    this.cx -= dxScreen / this.zoom;
    this.cy += dyScreen / this.zoom;
  }

  /** 以屏幕点 anchor 为锚点缩放到 nextZoom。 */
  zoomAt(anchor: Vec2, nextZoom: number): void {
    const z0 = this.zoom;
    const z1 = Math.max(0.4, Math.min(400, nextZoom));
    if (Math.abs(z1 - z0) < 1e-9) return;
    const before = this.toWorld(anchor);
    this.zoom = z1;
    const after = this.toWorld(anchor);
    this.cx += before.x - after.x;
    this.cy += before.y - after.y;
  }

  zoomBy(factor: number, anchor: Vec2): void {
    this.zoomAt(anchor, this.zoom * factor);
  }

  /** 把世界包围盒装进视口（留 8% 边距）。 */
  fit(b: Bounds, padRatio = 0.08): void {
    const w = Math.max(b.maxX - b.minX, 1e-3);
    const h = Math.max(b.maxY - b.minY, 1e-3);
    this.cx = (b.minX + b.maxX) / 2;
    this.cy = (b.minY + b.maxY) / 2;
    this.zoom = Math.max(0.04, Math.min(this.width / w, this.height / h) * (1 - padRatio * 2));
  }

  /** 视口对应的世界矩形（渲染网格/背景用）。 */
  viewBounds(): Bounds {
    const a = this.toWorld({ x: 0, y: 0 });
    const b = this.toWorld({ x: this.width, y: this.height });
    return {
      minX: Math.min(a.x, b.x),
      minY: Math.min(a.y, b.y),
      maxX: Math.max(a.x, b.x),
      maxY: Math.max(a.y, b.y),
    };
  }
}
