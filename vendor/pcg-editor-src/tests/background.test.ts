import { describe, expect, it } from 'vitest';
import {
  backgroundBounds,
  calibrate,
  calibrationWarning,
  fitBackgroundToView,
  imagePxToWorld,
  worldToImagePx,
} from '../core/background';
import { dist } from '../core/vec';

const base = { origin: { x: 0, y: 0 }, metersPerPixel: 0.05, rotation: 0 };

describe('Phase 2 · 背景图坐标换算', () => {
  it('像素 → 世界：px 向右、py 向下（y 翻成向北）', () => {
    expect(imagePxToWorld(base, { x: 100, y: 0 })).toEqual({ x: 5, y: 0 });
    const down = imagePxToWorld(base, { x: 0, y: 100 });
    expect(down.x).toBeCloseTo(0, 9);
    expect(down.y).toBeCloseTo(-5, 9);
  });

  it('世界 → 像素 与 像素 → 世界 互为逆运算', () => {
    const bg = { origin: { x: 12.5, y: -7.25 }, metersPerPixel: 0.0375, rotation: 0.42 };
    for (const px of [{ x: 0, y: 0 }, { x: 1000, y: 600 }, { x: 37, y: 512 }]) {
      const back = worldToImagePx(bg, imagePxToWorld(bg, px));
      expect(back.x).toBeCloseTo(px.x, 6);
      expect(back.y).toBeCloseTo(px.y, 6);
    }
  });

  it('旋转 90° 后 px 轴指向世界北', () => {
    const bg = { ...base, rotation: Math.PI / 2, metersPerPixel: 1 };
    const p = imagePxToWorld(bg, { x: 10, y: 0 });
    expect(p.x).toBeCloseTo(0, 9);
    expect(p.y).toBeCloseTo(10, 9);
  });

  it('包围盒随旋转变化（90° 时宽高互换）', () => {
    const bg = { ...base, metersPerPixel: 0.1, widthPx: 1000, heightPx: 500, src: '', opacity: 1, locked: false, visible: true };
    const b0 = backgroundBounds(bg);
    expect(b0.maxX - b0.minX).toBeCloseTo(100, 6);
    expect(b0.maxY - b0.minY).toBeCloseTo(50, 6);
    const b90 = backgroundBounds({ ...bg, rotation: Math.PI / 2 });
    expect(b90.maxX - b90.minX).toBeCloseTo(50, 6);
    expect(b90.maxY - b90.minY).toBeCloseTo(100, 6);
  });
});

describe('Phase 2 · 两点标定（PRD §20）', () => {
  it('标定后：A 点保持原位，A→B 的图上距离等于实际米数', () => {
    const bg = { ...base, metersPerPixel: 0.05 }; // 假设原来比例是错的
    const a = imagePxToWorld(bg, { x: 100, y: 100 });
    const b = imagePxToWorld(bg, { x: 500, y: 100 }); // 图上相距 400px
    const res = calibrate(bg, a, b, 24)!; // 实际 24m
    expect(res).not.toBeNull();
    expect(res.pixelDistance).toBeCloseTo(400, 6);
    expect(res.metersPerPixel).toBeCloseTo(24 / 400, 9);

    const nb = { ...bg, origin: res.origin, metersPerPixel: res.metersPerPixel };
    // A 不动
    expect(dist(imagePxToWorld(nb, { x: 100, y: 100 }), a)).toBeLessThan(1e-9);
    // B 的图上位置如今落在离 A 恰好 24m 处
    expect(dist(imagePxToWorld(nb, { x: 500, y: 100 }), a)).toBeCloseTo(24, 6);
  });

  it('标定保持既有旋转角（不擅自改朝向）', () => {
    const bg = { ...base, rotation: 0.7853981633974483 };
    const a = imagePxToWorld(bg, { x: 0, y: 0 });
    const b = imagePxToWorld(bg, { x: 200, y: 0 });
    const res = calibrate(bg, a, b, 10)!;
    const after = { ...bg, origin: res.origin, metersPerPixel: res.metersPerPixel };
    expect(after.rotation).toBeCloseTo(bg.rotation, 12);
    // 旋转下的方向也要保持
    const dirBefore = Math.atan2(b.y - a.y, b.x - a.x);
    const nb = imagePxToWorld(after, { x: 200, y: 0 });
    const dirAfter = Math.atan2(nb.y - a.y, nb.x - a.x);
    expect(dirAfter).toBeCloseTo(dirBefore, 9);
  });

  it('退化输入返回 null（两点重合 / 距离非法）', () => {
    const a = { x: 3, y: 4 };
    expect(calibrate(base, a, a, 10)).toBeNull();
    expect(calibrate(base, a, { x: 10, y: 10 }, 0)).toBeNull();
    expect(calibrate(base, a, { x: 10, y: 10 }, -5)).toBeNull();
  });

  it('标定质量提示：参考线过短 / 单位填错', () => {
    expect(calibrationWarning(10, 8)).toMatch(/太短/);
    expect(calibrationWarning(5000, 100)).toMatch(/异常/);
    expect(calibrationWarning(20, 400)).toBeNull();
  });

  it('Case UI-05 复现：标定后画 10×20 矩形，测量得 10 / 20 / 200', () => {
    // 图片上 200px = 50m  → 0.25 m/px
    const bg = { ...base, metersPerPixel: 1 };
    const a = imagePxToWorld(bg, { x: 0, y: 0 });
    const b = imagePxToWorld(bg, { x: 200, y: 0 });
    const res = calibrate(bg, a, b, 50)!;
    const cal = { ...bg, origin: res.origin, metersPerPixel: res.metersPerPixel };

    // 在图上取 40px × 80px 的矩形 → 应为 10m × 20m
    const p0 = imagePxToWorld(cal, { x: 0, y: 0 });
    const p1 = imagePxToWorld(cal, { x: 40, y: 0 });
    const p2 = imagePxToWorld(cal, { x: 40, y: 80 });
    const p3 = imagePxToWorld(cal, { x: 0, y: 80 });
    expect(dist(p0, p1)).toBeCloseTo(10, 6);
    expect(dist(p1, p2)).toBeCloseTo(20, 6);
    expect(dist(p0, p1) * dist(p1, p2)).toBeCloseTo(200, 4);
    expect(p0.y).toBeGreaterThan(p3.y); // py 向下 → 世界 y 更小
  });
});

describe('Phase 2 · 导入初始摆放', () => {
  it('图片以视口 70% 宽度居中摆放', () => {
    const bg = fitBackgroundToView(2000, 1000, { x: 100, y: 50 }, 200);
    const w = bg.widthPx * bg.metersPerPixel;
    expect(w).toBeCloseTo(140, 6);
    const bounds = backgroundBounds({ ...bg, src: '', opacity: 1, locked: false, visible: true });
    expect((bounds.minX + bounds.maxX) / 2).toBeCloseTo(100, 6);
    expect((bounds.minY + bounds.maxY) / 2).toBeCloseTo(50, 6);
  });
});
