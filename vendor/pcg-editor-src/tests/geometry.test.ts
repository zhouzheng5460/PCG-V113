import { describe, expect, it } from 'vitest';
import { booleanOp, offsetPath, quantizeRing, union } from '../core/boolean';
import {
  Paths,
  circleRing,
  cleanRing,
  closestPointOnSegment,
  pathsArea,
  pointInPaths,
  polylineLength,
  rectRing,
  ringArea,
  ringPerimeter,
  selfIntersections,
} from '../core/polygon';
import { dist, v2 } from '../core/vec';
import { photoToWorld } from './helper';
import { Camera2D } from '../editor/camera';

const sq = (x: number, y: number, w: number, h: number) => rectRing(v2(x, y), v2(x + w, y + h));

describe('Phase 0 · 基础几何', () => {
  it('矩形面积 / 周长正确（Case UI-05：10×20 → 200㎡）', () => {
    const r = sq(0, 0, 10, 20);
    expect(ringArea(r)).toBeCloseTo(200, 6);
    expect(ringPerimeter(r)).toBeCloseTo(60, 6);
  });

  it('圆环面积接近 πr²', () => {
    const r = circleRing(v2(0, 0), 10, 720);
    expect(ringArea(r)).toBeCloseTo(Math.PI * 100, 0);
  });

  it('围合方向不影响带符号面积绝对值', () => {
    const r = sq(0, 0, 5, 5);
    expect(ringArea(r.slice().reverse())).toBeCloseTo(25, 6);
  });

  it('cleanRing 去掉共线点与重复点', () => {
    const r = [
      v2(0, 0),
      v2(5, 0),
      v2(10, 0),
      v2(10, 0),
      v2(10, 10),
      v2(0, 10),
    ];
    expect(cleanRing(r).length).toBe(4);
  });

  it('自交检测（蝴蝶形多边形）', () => {
    const bowtie = [v2(0, 0), v2(10, 10), v2(10, 0), v2(0, 10)];
    expect(selfIntersections(bowtie).length).toBeGreaterThan(0);
  });

  it('点包含 / 洞的奇偶判定', () => {
    const outer = sq(0, 0, 20, 20);
    const hole = sq(5, 5, 10, 10);
    const paths: Paths = [outer, hole];
    expect(pointInPaths(v2(2, 2), paths)).toBe(true);
    expect(pointInPaths(v2(10, 10), paths)).toBe(false);
    expect(pointInPaths(v2(30, 30), paths)).toBe(false);
  });

  it('折线长度与最近点', () => {
    const line = [v2(0, 0), v2(10, 0), v2(10, 10)];
    expect(polylineLength(line)).toBeCloseTo(20, 6);
    const c = closestPointOnSegment(v2(3, 5), v2(0, 0), v2(10, 0));
    expect(c.dist).toBeCloseTo(5, 6);
    expect(c.point.x).toBeCloseTo(3, 6);
  });
});

describe('Phase 0 · 量化与精度', () => {
  it('1mm 量化后面积误差 < 0.1%（Case UI-01 要求 < 1%）', () => {
    const r = sq(0.1234, 7.7777, 24.37, 18.53);
    const before = ringArea(r);
    const after = ringArea(quantizeRing(r));
    expect(Math.abs(after - before) / before).toBeLessThan(0.001);
  });
});

describe('Phase 0 · 布尔运算（Case UI-06）', () => {
  it('地块 - 建筑 = 合法多边形（含洞），面积正确', () => {
    const parcel = sq(0, 0, 20, 20); // 400
    const building = sq(5, 5, 10, 10); // 100
    const out = booleanOp('subtract', [parcel], [building]);
    // Clipper 会返回 外环 + 内环（洞）两个环，渲染用 evenodd 填充
    expect(out.length).toBe(2);
    expect(pathsArea(out)).toBeCloseTo(300, 3);
    // 减掉之后中心应落在洞里
    expect(pointInPaths(v2(10, 10), out)).toBe(false);
    expect(pointInPaths(v2(2, 10), out)).toBe(true);
  });

  it('Union 合并两个相交矩形', () => {
    const a = sq(0, 0, 20, 20);
    const b = sq(10, 10, 20, 20);
    const out = union([a], [b]);
    expect(pathsArea(out)).toBeCloseTo(400 + 400 - 100, 3);
  });

  it('Intersection 只保留重叠部分', () => {
    const a = sq(0, 0, 20, 20);
    const b = sq(10, 10, 20, 20);
    const out = booleanOp('intersect', [a], [b]);
    expect(pathsArea(out)).toBeCloseTo(100, 3);
  });

  it('不相交的相减结果为空', () => {
    const out = booleanOp('intersect', [sq(0, 0, 10, 10)], [sq(100, 100, 10, 10)]);
    expect(out.length).toBe(0);
  });
});

describe('Phase 0 · 偏移（PRD §51）', () => {
  it('向内偏移 3m：20×20 → 14×14', () => {
    const out = offsetPath([sq(0, 0, 20, 20)], -3);
    expect(pathsArea(out)).toBeCloseTo(196, 2);
  });

  it('向外偏移 3m：20×20 → 26×26', () => {
    const out = offsetPath([sq(0, 0, 20, 20)], 3);
    expect(pathsArea(out)).toBeCloseTo(676, 2);
  });

  it('过度内缩返回空而不是非法几何', () => {
    const out = offsetPath([sq(0, 0, 10, 10)], -8);
    expect(out.length).toBe(0);
  });
});

describe('编辑器 · 相机', () => {
  it('缩放对准光标：光标下的世界坐标保持不变', () => {
    const cam = new Camera2D();
    cam.setViewport(800, 600);
    cam.center = { x: 40, y: 30 };
    cam.scale = 5;
    const screen = { x: 620, y: 180 };
    const before = cam.screenToWorld(screen);
    cam.zoomAt(screen, 1.8);
    const after = cam.screenToWorld(screen);
    expect(dist(before, after)).toBeLessThan(1e-6);
  });

  it('fitBounds 后视口包含整个包围盒', () => {
    const cam = new Camera2D();
    cam.setViewport(1000, 700);
    cam.fitBounds({ minX: 0, minY: 0, maxX: 160, maxY: 110 }, 60);
    const tl = cam.worldToScreen({ x: 0, y: 110 });
    const br = cam.worldToScreen({ x: 160, y: 0 });
    expect(tl.x).toBeGreaterThanOrEqual(0);
    expect(tl.y).toBeGreaterThanOrEqual(0);
    expect(br.x).toBeLessThanOrEqual(1000);
    expect(br.y).toBeLessThanOrEqual(700);
  });

  it('photoToWorld 占位：米制坐标与像素换算一致', () => {
    expect(photoToWorld(100, 0.05)).toBeCloseTo(5, 6);
  });
});
