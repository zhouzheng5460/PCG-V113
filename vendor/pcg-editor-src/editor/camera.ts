import { Bounds } from '../core/polygon';
import { Vec2 } from '../core/vec';

/**
 * 2D 相机：世界（meter，y 向北）↔ 屏幕（px，y 向下）
 * 缩放永远对准光标（PRD §62）。
 */
export class Camera2D {
  center: Vec2 = { x: 80, y: 55 };
  scale = 5; // px per meter
  width = 800;
  height = 600;

  static readonly MIN_SCALE = 0.15;
  static readonly MAX_SCALE = 120;

  setViewport(w: number, h: number) {
    this.width = Math.max(1, w);
    this.height = Math.max(1, h);
  }

  worldToScreen(p: Vec2): Vec2 {
    return {
      x: (p.x - this.center.x) * this.scale + this.width / 2,
      y: (this.center.y - p.y) * this.scale + this.height / 2,
    };
  }

  screenToWorld(p: Vec2): Vec2 {
    return {
      x: (p.x - this.width / 2) / this.scale + this.center.x,
      y: this.center.y - (p.y - this.height / 2) / this.scale,
    };
  }

  /** 屏幕像素长度 → 世界米 */
  pxToWorld(px: number): number {
    return px / this.scale;
  }

  worldToPx(m: number): number {
    return m * this.scale;
  }

  panByScreen(dx: number, dy: number) {
    this.center = {
      x: this.center.x - dx / this.scale,
      y: this.center.y + dy / this.scale,
    };
  }

  zoomAt(screen: Vec2, factor: number) {
    const before = this.screenToWorld(screen);
    const next = clamp(this.scale * factor, Camera2D.MIN_SCALE, Camera2D.MAX_SCALE);
    if (next === this.scale) return;
    this.scale = next;
    const after = this.screenToWorld(screen);
    this.center = {
      x: this.center.x + (before.x - after.x),
      y: this.center.y + (before.y - after.y),
    };
  }

  fitBounds(b: Bounds, paddingPx = 56) {
    const w = Math.max(b.maxX - b.minX, 0.5);
    const h = Math.max(b.maxY - b.minY, 0.5);
    const sx = (this.width - paddingPx * 2) / w;
    const sy = (this.height - paddingPx * 2) / h;
    this.scale = clamp(Math.min(sx, sy), Camera2D.MIN_SCALE, Camera2D.MAX_SCALE);
    this.center = { x: (b.minX + b.maxX) / 2, y: (b.minY + b.maxY) / 2 };
  }

  visibleBounds(): Bounds {
    const tl = this.screenToWorld({ x: 0, y: 0 });
    const br = this.screenToWorld({ x: this.width, y: this.height });
    return { minX: tl.x, minY: br.y, maxX: br.x, maxY: tl.y };
  }
}

const clamp = (v: number, a: number, b: number) => Math.min(b, Math.max(a, v));

export const camera = new Camera2D();

if (import.meta.env.DEV && typeof window !== 'undefined') {
  (window as unknown as Record<string, unknown>).__camera = camera;
}
