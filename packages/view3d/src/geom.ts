// 二维轮廓 → three 几何体。
//
// 为什么手写而不用 ExtrudeGeometry：
//   1. ExtrudeGeometry 要先做 Shape + 三角化，绕序错了会**静默**翻面（从下面看不见）；
//   2. 楼块要硬边法线，索引几何共享顶点会把侧面磨圆，看起来像果冻；
//   3. 我们只需要"多边形挤出成柱体"这一件事，手写反而短。
//
// 统一约定：入参 ring 一律先规整成**逆时针**（从 +Y 往下看），否则顶/底面法线会反。

import { ccw, sampleBezier, type Vec2 } from '../../plan-ir/src/geom.ts';

/**
 * 洞环预处理：外环逆时针、洞顺时针（three 的 `triangulateShape` 靠这个判内外）。
 * 顶点顺序必须和三角化时的输入顺序一致，否则索引会错位。
 */
function holeRings(holes: readonly (readonly Vec2[])[] | undefined): Vec2[][] {
  if (!holes?.length) return [];
  return holes.filter((h) => h.length >= 3).map((h) => ccw(h).slice().reverse());
}

/** 一条闭合多边形环 → 上下两个平面 + 侧墙，硬边法线。`holes` 可选（带洞的柱体）。 */
export function prism(ring: readonly Vec2[], y0: number, y1: number, holes?: readonly (readonly Vec2[])[]): any {
  const r = ccw(ring);
  const hs = holeRings(holes);
  // 三角化的顶点序列 = 外环 + 各洞（three 返回的索引就是按这个顺序）
  const seq: Vec2[] = [...r, ...hs.flat()];
  const N = seq.length;
  const pos: number[] = [];
  const idx: number[] = [];

  for (const p of seq) pos.push(p[0], y0, p[1]);
  for (const p of seq) pos.push(p[0], y1, p[1]);

  const contour = r.map((p) => new THREE.Vector2(p[0], p[1]));
  const holePath = hs.map((h) => h.map((p) => new THREE.Vector2(p[0], p[1])));
  const tris: number[][] = THREE.ShapeUtils.triangulateShape(contour, holePath);

  // 绕序：在 (x,z) 里逆时针的多边形，按右手法则法线指向 **-Y**（因为 y 轴朝上、
  // z 轴朝"屏幕外"时，x-z 平面的正方向与常见的 x-y 平面相反）。
  // 第一版直接把三角化结果当顶面用，结果顶面法线朝下 —— 楼顶一片死黑，而侧面正常。
  // 所以：顶面反向一次（法线 +Y），底面用原序（法线 -Y）。
  for (const t of tris) idx.push(N + t[0], N + t[2], N + t[1]);
  for (const t of tris) idx.push(t[0], t[1], t[2]);
  // 外侧墙：每条边一个四边形
  for (let i = 0; i < r.length; i++) {
    const j = (i + 1) % r.length;
    idx.push(i, j, N + j, i, N + j, N + i);
  }
  // 洞的内侧墙：反向绕序，法线朝洞内（否则从洞里看是空心的）
  let off = r.length;
  for (const h of hs) {
    for (let i = 0; i < h.length; i++) {
      const j = (i + 1) % h.length;
      const a = off + i;
      const b = off + j;
      idx.push(a, N + a, N + b, a, N + b, b);
    }
    off += h.length;
  }

  return finishIndexed(pos, idx);
}

/**
 * 单个平面多边形（铺装、草地、水面）。法线朝 +Y —— 地面要能接住光。
 *
 * **`holes` 必须传进来**：`PlanSurface.holes` 是契约的一部分（布尔差集减掉内部障碍物的结果）。
 * 这里第二参数原来硬编码成 `[]`，于是 3D 里"洞"会被填平 —— 数据层说障碍重叠 0%，
 * 画面里建筑底下却重新铺满。见 `test/holes-contract.test.ts`（P3.2 加的合同测试）。
 */
export function flatRing(ring: readonly Vec2[], y: number, holes?: readonly (readonly Vec2[])[]): any {
  const r = ccw(ring);
  const hs = holeRings(holes);
  const seq: Vec2[] = [...r, ...hs.flat()];
  const pos: number[] = [];
  for (const p of seq) pos.push(p[0], y, p[1]);
  const contour = r.map((p) => new THREE.Vector2(p[0], p[1]));
  const holePath = hs.map((h) => h.map((p) => new THREE.Vector2(p[0], p[1])));
  const tris: number[][] = THREE.ShapeUtils.triangulateShape(contour, holePath);
  const idx: number[] = [];
  // 同 prism 的顶面：反向才朝 +Y
  for (const t of tris) idx.push(t[0], t[2], t[1]);
  return finishIndexed(pos, idx);
}

/**
 * 中心线 → 带状面（街道、人行道、车道）。
 *
 * 每个采样点按"前后点连线"求切线，再取法向偏移 ±w/2。端头直接平切，
 * 不封口——街道端头要么接别的街道要么在轮廓外，看得见封口反而更假。
 */
export function ribbon(centerline: readonly Vec2[], width: number, y: number): any {
  const pts = centerline;
  if (pts.length < 2) return emptyGeometry();
  const half = width / 2;
  const pos: number[] = [];
  const idx: number[] = [];

  for (let i = 0; i < pts.length; i++) {
    const prev = pts[Math.max(0, i - 1)];
    const next = pts[Math.min(pts.length - 1, i + 1)];
    let dx = next[0] - prev[0];
    let dz = next[1] - prev[1];
    const len = Math.hypot(dx, dz) || 1;
    dx /= len;
    dz /= len;
    // 左法向
    const nx = -dz;
    const nz = dx;
    pos.push(pts[i][0] + nx * half, y, pts[i][1] + nz * half);
    pos.push(pts[i][0] - nx * half, y, pts[i][1] - nz * half);
  }

  for (let i = 0; i < pts.length - 1; i++) {
    const a = i * 2;
    // 绕序同上：三角带也要让法线朝 +Y，否则整条街背面朝上、暗成一条黑带
    idx.push(a, a + 2, a + 3, a, a + 3, a + 1);
  }

  return finishIndexed(pos, idx);
}

/**
 * 三次贝塞尔 → 采样中心线。
 *
 * 直接复用 plan-ir 的 `sampleBezier`：它按"弧长估 / 4 m、夹到 1..64 段"采样，
 * 和 C# 的 `AttachmentFinder.Sample` 同分布。渲染也用它有两个好处——
 * 画出来的折线就是判定用的那条折线，不会出现"看着贴上了、判没过"；
 * 且密度天然自适应（6 m 的小路 2 段，200 m 的大道 50 段）。
 */
export function bezierOutline(a: Vec2, b: Vec2, c: Vec2, d: Vec2): Vec2[] {
  const { pts } = sampleBezier(a, b, c, d);
  if (pts.length >= 2) return pts;
  return [a, d];
}

/** 把索引几何转成非索引（每三角形独立顶点）→ 硬边法线。 */
function finishIndexed(pos: number[], idx: number[]): any {
  const g = new THREE.BufferGeometry();
  g.setAttribute('position', new THREE.Float32BufferAttribute(pos, 3));
  g.setIndex(idx);
  const flat = g.toNonIndexed();
  g.dispose();
  flat.computeVertexNormals();
  return flat;
}

export function emptyGeometry(): any {
  const g = new THREE.BufferGeometry();
  g.setAttribute('position', new THREE.Float32BufferAttribute([], 3));
  return g;
}

/** 多边形的轴对齐包围盒（世界 XZ），用于统计与相机取景。 */
export function ringBounds(rings: readonly (readonly Vec2[])[]): { min: Vec2; max: Vec2 } | null {
  let minX = Infinity;
  let minZ = Infinity;
  let maxX = -Infinity;
  let maxZ = -Infinity;
  for (const r of rings) {
    for (const p of r) {
      if (p[0] < minX) minX = p[0];
      if (p[1] < minZ) minZ = p[1];
      if (p[0] > maxX) maxX = p[0];
      if (p[1] > maxZ) maxZ = p[1];
    }
  }
  if (!Number.isFinite(minX)) return null;
  return { min: [minX, minZ], max: [maxX, maxZ] };
}
