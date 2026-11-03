// `ZoneBlockPredictor` + `ZoneGuard` 的格子几何 —— **按 C# 原样移植**。
//
// 为什么必须用它替换我自己的"沿街推带"：
//   Placemaker 的宗地（zoning）不是我们想出来的形状，而是 CS2 游戏里**道路自带的 8 m 格网**
//   被划出来的矩形组合。格子的位置、朝向、深度都有确定公式（`Game.Zones.ZoneUtils`），
//   `ZoneGuard` 只是"把规划好的四边形吸附到这些格子上"。所以"按 C# 的方法"= 先算格子，再划格子。
//
// 三个关键常量与公式（`Core/Build/ZoneGuard.cs` / `ZoneBlockPredictor.cs`）：
//   · 格子边长 **8 m**；一块 zone block 深度固定 **6 格（48 m）**，沿路方向每块 **≤ 10 格**；
//   · 格子宽 `ceil(路宽 / 8 - 0.01)` 格 —— 路越宽，格网离路越远；
//   · 格心 `Position + d*(sy-2y-1)*4 + Right(d)*(sx-2x-1)*4`，row 0 贴着路。
//
// 这里**全部用米**（Placemaker 的契约），和 TownForge 的世界单位只在入口/出口各转一次。

type V2 = [number, number];

/** 一块 zone block：中心、朝向（从地块指向它所属的路）、格数（x 沿路，y 向内）。 */
export interface CellGrid {
  /** block 中心（米） */
  position: V2;
  /** 单位向量，从 block 指向**路**（C# 里是 `Direction`，注意方向） */
  dir: V2;
  /** 沿路的格数 */
  sx: number;
  /** 向内的格数（C# 固定 6） */
  sy: number;
}

/** 格子边长（米）。C# `ZoneGuard.Cell`。 */
export const CELL_M = 8;
/** 一块 block 的深度（格）。C# `Size.y = 6`。 */
export const DEPTH_CELLS = 6;
/** 每块最多几格（沿路）。C# `chunks` 按 10 切。 */
export const MAX_CELLS_PER_BLOCK = 10;

const sub = (a: V2, b: V2): V2 => [a[0] - b[0], a[1] - b[1]];
const add = (a: V2, b: V2): V2 => [a[0] + b[0], a[1] + b[1]];
const mul = (a: V2, k: number): V2 => [a[0] * k, a[1] * k];
const dot = (a: V2, b: V2): number => a[0] * b[0] + a[1] * b[1];
const len = (a: V2): number => Math.hypot(a[0], a[1]);
const norm = (a: V2): V2 => {
  const l = len(a) || 1;
  return [a[0] / l, a[1] / l];
};
/** C# `MathUtils.Right`：绕 -90°。 */
const right = (d: V2): V2 => [d[1], -d[0]];
const lerp2 = (a: V2, b: V2, t: number): V2 => [a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t];
const clamp = (v: number, lo: number, hi: number): number => (v < lo ? lo : v > hi ? hi : v);

// ─────────────────────────────────────────── 三次贝塞尔（C# 的 Pos/Tangent/Off）

function bezier(c: [V2, V2, V2, V2], t: number): V2 {
  const u = 1 - t;
  const a = u * u * u;
  const b = 3 * u * u * t;
  const d = 3 * u * t * t;
  const e = t * t * t;
  return [a * c[0][0] + b * c[1][0] + d * c[2][0] + e * c[3][0], a * c[0][1] + b * c[1][1] + d * c[2][1] + e * c[3][1]];
}

function tangent(c: [V2, V2, V2, V2], t: number): V2 {
  const u = 1 - t;
  const a = 3 * u * u;
  const b = 6 * u * t;
  const d = 3 * t * t;
  const v: V2 = [
    a * (c[1][0] - c[0][0]) + b * (c[2][0] - c[1][0]) + d * (c[3][0] - c[2][0]),
    a * (c[1][1] - c[0][1]) + b * (c[2][1] - c[1][1]) + d * (c[3][1] - c[2][1]),
  ];
  return len(v) < 1e-5 ? sub(c[3], c[0]) : v;
}

/** 曲线上 t 处向外偏移 d 的点（C# `Off`：每点各自按切线偏移）。 */
function off(c: [V2, V2, V2, V2], t: number, d: number): V2 {
  const tan = norm(tangent(c, t));
  return add(bezier(c, t), mul(right(tan), d));
}

function offLength(c: [V2, V2, V2, V2], d: number): number {
  let total = 0;
  let prev = off(c, 0, d);
  for (let i = 1; i <= 32; i++) {
    const p = off(c, i / 32, d);
    total += len(sub(p, prev));
    prev = p;
  }
  return total;
}

/** 一段曲线在一侧（右/左）的若干 zone block。C# `Side`。 */
function side(curve: [V2, V2, V2, V2], cellWidth: number, into: CellGrid[]): void {
  const v0 = norm(tangent(curve, 0));
  const v1 = norm(tangent(curve, 1));
  if (len(v0) ** 2 < 0.5 || len(v1) ** 2 < 0.5) return;

  const offDist = cellWidth * (CELL_M / 2); // num2：半个格子宽（C# cw*4）
  const curveLen = offLength(curve, offDist);
  if (curveLen < CELL_M) return;

  // 曲率决定分段长度（C# 的 num4/num5 两行）：弯得越急，段越短
  const turnDeg = (Math.acos(clamp(dot(v0, v1), -1, 1)) * 180) / Math.PI;
  const curvature = (turnDeg * CELL_M) / curveLen;
  const pieceLen = (2 / Math.sqrt(clamp(curvature / 15, 0.0001, 1))) * CELL_M;
  const pieces = Math.max(1, Math.floor(curveLen / pieceLen));

  for (let i = 0; i < pieces; i++) {
    const t0 = i / pieces;
    const t1 = (i + 1) / pieces;
    const tan0 = norm(tangent(curve, t0));
    const tan1 = norm(tangent(curve, t1));
    let a = off(curve, t0, offDist);
    let d = off(curve, t1, offDist);
    let chord = sub(d, a);
    if (len(chord) ** 2 < 1e-6) continue;
    chord = norm(chord);

    // 自由端按"半个路宽减一格"外扩（C# 注释：不建模与相邻边的连续）
    const ext = offDist - (cellWidth > 1 ? CELL_M : 0);
    if (i === 0) a = sub(a, mul(chord, ext));
    if (i === pieces - 1) d = add(d, mul(chord, ext));

    const r0 = right(tan0);
    const r1 = right(tan1);
    const rc = right(chord);
    const maxDot = maxDotOf(curve, offDist, t0, t1, rc);
    const upper = len(sub(d, a));
    const d6 = dot(r0, rc);
    const d7 = dot(r1, rc);
    if (Math.abs(d6) > 1e-4) a = add(a, mul(r0, clamp((maxDot - dot(a, rc)) / d6, 0, upper)));
    if (Math.abs(d7) > 1e-4) d = add(d, mul(r1, clamp((maxDot - dot(d, rc)) / d7, 0, upper)));

    const cells = Math.floor((len(sub(d, a)) + 0.1) / CELL_M);
    if (cells < 2) continue;

    // 以弦中点对齐，再整体往内推 3 格（24 m）：`Size.y = 6` 的 block 中心在路面内侧 3 格
    const mid = lerp2(a, d, 0.5);
    a = sub(mid, mul(chord, cells * (CELL_M / 2)));
    a = add(a, mul(rc, maxDotOf(curve, offDist, t0, t1, rc) - dot(a, rc) + 3 * CELL_M));

    const chunks = Math.ceil(cells / MAX_CELLS_PER_BLOCK);
    for (let j = 0; j < chunks; j++) {
      const x0 = Math.floor((j * cells) / chunks);
      const x1 = Math.floor(((j + 1) * cells) / chunks);
      into.push({
        // C# 把整块的中心放在 a + chord*((x0+x1)*4)
        position: add(a, mul(chord, (x0 + x1) * (CELL_M / 2))),
        // Direction 指向**路**：右法向取反
        dir: [-rc[0], -rc[1]],
        sx: x1 - x0,
        sy: DEPTH_CELLS,
      });
    }
  }
}

function maxDotOf(c: [V2, V2, V2, V2], pad: number, t0: number, t1: number, dir: V2): number {
  let best = -Infinity;
  for (let i = 0; i <= 16; i++) {
    const p = off(c, t0 + (t1 - t0) * (i / 16), pad);
    best = Math.max(best, dot(p, dir));
  }
  return best;
}

/** 道路中间的一段（A→D，米）→ 两侧的 zone block。C# `Blocks`。 */
export function blocksOfRoad(a: V2, b: V2, widthM: number): CellGrid[] {
  const out: CellGrid[] = [];
  if (!(widthM > 0)) return out;
  const cellWidth = Math.max(1, Math.ceil(widthM / CELL_M - 0.01)); // C# CellWidth
  // 直线：控制点取三等分
  const curve: [V2, V2, V2, V2] = [a, lerp2(a, b, 1 / 3), lerp2(a, b, 2 / 3), b];
  side(curve, cellWidth, out);
  // 另一侧：把曲线反向
  side([curve[3], curve[2], curve[1], curve[0]], cellWidth, out);
  return out;
}

// ─────────────────────────────────────────── 格子几何（ZoneUtils）

/** 格心（米）；row 0 贴着路。C# `CellCentre`。 */
export function cellCentre(g: CellGrid, x: number, y: number): V2 {
  const d = norm(g.dir);
  const h = CELL_M / 2;
  const oy = (g.sy - 2 * y - 1) * h;
  const ox = (g.sx - 2 * x - 1) * h;
  return [g.position[0] + d[0] * oy + right(d)[0] * ox, g.position[1] + d[1] * oy + right(d)[1] * ox];
}

/** 一个格子的方块（米，CCW）。C# `CellSquare`。 */
export function cellSquare(g: CellGrid, x: number, y: number): V2[] {
  return cellRect(g, x, x, y, y);
}

/** 格子区间 [x0..x1] × [y0..y1] 的矩形（米，CCW）。C# `CellRect`。 */
export function cellRect(g: CellGrid, x0: number, x1: number, y0: number, y1: number): [V2, V2, V2, V2] {
  const d = norm(g.dir);
  const r = right(d);
  const h = CELL_M / 2;
  const u0 = (g.sx - 2 * x1 - 1) * h - h;
  const u1 = (g.sx - 2 * x0 - 1) * h + h;
  const v0 = (g.sy - 2 * y1 - 1) * h - h;
  const v1 = (g.sy - 2 * y0 - 1) * h + h;
  const p = g.position;
  return [
    [p[0] + r[0] * u0 + d[0] * v0, p[1] + r[1] * u0 + d[1] * v0],
    [p[0] + r[0] * u1 + d[0] * v0, p[1] + r[1] * u1 + d[1] * v0],
    [p[0] + r[0] * u1 + d[0] * v1, p[1] + r[1] * u1 + d[1] * v1],
    [p[0] + r[0] * u0 + d[0] * v1, p[1] + r[1] * u0 + d[1] * v1],
  ];
}

/**
 * 一块宗地 = "同一 block 里、全被占满的一段格子矩形"。
 *
 * 这就是 C# `ZoneGuard.Result.Zoning` 的 `ZonedRect`：规划不是"任意四边形"，
 * 而是"同属一块 block、连续的一段格子"。**必须全占满** ——
 * 矩形里有空格就等于把楼排到别人的地上（上一版就是这么压到路上的，见 D-039）。
 *
 * 除了格子区间，还带一套**世界坐标的 (u,v) 包围区间**：
 * u 沿路（`right(dir)` 方向），v 朝内。有了它，才谈得上"两个矩形共线接得上就能并"——
 * 见 `mergeRects`。这套数是纯投影，不是新的几何真源。
 */
export interface ZoningRect {
  gridIndex: number;
  /** 格子区间（含端点） */
  x0: number;
  x1: number;
  y0: number;
  y1: number;
  /** 矩形（米，CCW） */
  quad: [V2, V2, V2, V2];
  /** 包含多少格 */
  cells: number;
  /** 这块朝向路的方向（= grid.dir） */
  toRoad: V2;
  /**
   * 局部坐标系的**原点**（米）= 它所在 grid 的 block 中心。
   *
   * u/v 一律相对这个原点量。为什么不用世界投影：两个相邻 grid 的朝向最多差 0.8°
   * （`dot > 0.9999`），在离原点 1 km 处就是 14 m 的偏移 —— 那样既判不准"贴不贴"，
   * 也会把合并后的边长算成 23.87 m 这种非整格值（实测踩过，6 条断言直接红）。
   */
  origin: V2;
  /** 沿路区间（米，相对 `origin`）：点 = origin + right(dir)·u + dir·v */
  u0: number;
  u1: number;
  /** 朝内区间（米，相对 `origin`） */
  v0: number;
  v1: number;
  /**
   * 这块地由几次"提取"拼成（1 = 一次分割就成了；>1 = 合并出来的）。
   *
   * 记这个数是为了能回答"地块到底是不是都从一次分割来的"：
   * 上一轮全是 1，所以整座城像复印机印出来的（D-053）。
   */
  sources: number;
}

export interface ZoningOptions {
  /** 最少几列（C#：一格宽的 plot 会被深度规则切没，所以 ≥2 列 = 16 m） */
  minCols?: number;
  /** 最少几格（面积下限：1 格 = 64 m²） */
  minCells?: number;
  /** 最少几排（C# `ZoneGuard.MinRows = 2`：一格深 = 8 m，盖不了楼） */
  minRows?: number;
}

// ─────────────────────────────────────────── 深度规则：C# `ZoneGuard.ReduceDepths` 原样移植

/**
 * 每列从 row 0 起连续被占用几格（"深度"）。C# 的 `depth[gi][x]`。
 *
 * row 0 没被占 → 整列深度 0（这一列不归这块地）。
 */
export function gridDepth(occ: Set<string>, g: CellGrid): number[] {
  const out: number[] = new Array(g.sx).fill(0);
  for (let x = 0; x < g.sx; x++) {
    let d = 0;
    while (d < g.sy && occ.has(`${x},${d}`)) d++;
    out[x] = d;
  }
  return out;
}

/**
 * C# `ZoneGuard.ReduceDepths`（s36，移植自游戏 `Game.Zones.CellOverlapJobs` 的反编译 1.6.2f1）。
 *
 * 原文（`[Core/Build/ZoneGuard.cs:1231]`）：
 * > after the cells are blocked, **every block evens out its column depths**. Walking from both ends
 * > at once, column x becomes CalculateLeftDepth(x) (and CalculateRightDepth from the other end) and
 * > ReduceDepth blocks every cell from that depth on. A column deeper than both neighbours is cut to
 * > the deeper neighbour unless a neighbour of its own depth stands beside it, so a lone 1-column
 * > cutout (depth 2 between blocked columns of depth 0) is blocked entirely ...
 * > Neighbour blocks of the same road continue the row (**GetDepth past the end of the block**).
 *
 * 为什么必须移植这一条：上一版我是"按深度相等分带"，于是**一个深 4 的孤立列就把深 6 的带拦腰截断**，
 * 464 个列深为 6 的列被切成 2~3 列的小段，p50 和 p90 都是 1152 m²（D-053）。
 * 游戏自己的做法不是"切"，而是**削平**：深的列让到跟邻居一样浅。削平之后剖面是平滑的阶梯，
 * 同样深度的连续列自然就长了，地块也就大了 —— 这不是我发明的"合并"，是 C# 里就有的规则。
 *
 * `Get` 越界查找是另一半关键：同一条路的两段（`chunks` 切的）本来接在一起，
 * 靠它把排号接起来，两段的深度才一致，后面的 `mergeRects` 才有东西可并。
 *
 * @param depth 就地修改（C# 也是这样，`Left`/`Right` 读到的是已经被改过的值）
 */
export function reduceDepths(grids: CellGrid[], depth: number[][]): void {
  const cellAt = (gi: number, x: number): V2 => cellCentre(grids[gi], x, 0);

  /** C# `Get`：越界时去同一条路的相邻块里找延续这一排的那一列 */
  const get = (gi: number, x: number): number => {
    const g = grids[gi];
    if (x >= 0 && x < g.sx) return depth[gi][x];
    const at = cellAt(gi, x);
    const d = norm(g.dir);
    for (let hi = 0; hi < grids.length; hi++) {
      if (hi === gi) continue;
      const h = grids[hi];
      if (dot(norm(h.dir), d) < 0.95) continue;
      if (len(sub(h.position, at)) > h.sx * CELL_M) continue;
      for (let hx = 0; hx < h.sx; hx++) {
        const p = cellAt(hi, hx);
        const dx = p[0] - at[0];
        const dy = p[1] - at[1];
        if (dx * dx + dy * dy < 4) return depth[hi][hx];
      }
    }
    return 0;
  };

  /** C# `Left`：从左边数起，这一列应该被削到多深 */
  const left = (gi: number, x: number): number => {
    const d0 = get(gi, x - 1);
    const d2 = get(gi, x);
    if (d2 <= d0) return d2;
    const d3 = get(gi, x - 2);
    if (d0 !== d3 && d0 !== 0) return d0;
    const d4 = get(gi, x + 1);
    if (d4 - d2 < d2 - d0) return Math.min(Math.max(d0, d4), d2);
    if (get(gi, x + 2) !== d4) return Math.min(Math.max(d0, d4), d2);
    return d0;
  };

  /** C# `Right`：从右边数起 */
  const right = (gi: number, x: number): number => {
    const d0 = get(gi, x + 1);
    const d2 = get(gi, x);
    if (d2 <= d0) return d2;
    const d3 = get(gi, x + 2);
    if (d0 !== d3 && d0 !== 0) return d0;
    const d4 = get(gi, x - 1);
    if (d4 - d2 < d2 - d0) return Math.min(Math.max(d4, d0), d2);
    if (get(gi, x - 2) !== d4) return Math.min(Math.max(d4, d0), d2);
    return d0;
  };

  for (let gi = 0; gi < grids.length; gi++) {
    const n = grids[gi].sx;
    let a = 0;
    let b = n - 1;
    while (b >= 0) {
      const l = left(gi, a);
      if (l < depth[gi][a]) depth[gi][a] = l;
      const r = right(gi, b);
      if (r < depth[gi][b]) depth[gi][b] = r;
      a++;
      b--;
    }
  }
}

// ─────────────────────────────────────────── 宗地提取：最大同深矩形 + 逐层剥

/** 把列号列表按"连续"切成若干段。 */
function contiguousRuns(xs: number[]): number[][] {
  const out: number[][] = [];
  let cur: number[] = [];
  for (const x of xs) {
    if (cur.length && x !== cur[cur.length - 1] + 1) {
      out.push(cur);
      cur = [];
    }
    cur.push(x);
  }
  if (cur.length) out.push(cur);
  return out;
}

/**
 * 一道深度剖面 → 若干**取满**的矩形（最大同深矩形 + 逐层剥）。
 *
 * 为什么不是"深度相同才算一块"（上一版）：
 *   剖面里**一个浅列就能把一条满深带拦腰截断**。实测 n=15/seed=682063530 的
 *   列深分布是 `{1:263, 2:100, 3:84, 4:87, 5:60, 6:1095}` —— 65% 的列都是满深 6 格，
 *   但它们被那 594 个浅列切成 2~3 列的小段，于是 p50 和 p90 都是 1152 m²
 *   （同一尺寸的 3 格 × 6 格），整座城像复印机印出来的。
 * 现在的规则是"**取能取满的最大面积矩形**"：
 *   · 先在最宽的范围内找 `宽 × min(深)` 最大的那段 → 一整块大地（通常是整条边的满深带）；
 *   · 把它的占地"剥掉"，剩下的列各自继续剥（前面的矮列 / 后面的高处）；
 *   · 每一步都不允许框进空格，所以**矩形里每一格都真的被占着**。
 * 这一步是 C# `ZoneGuard.GameDepthPass` 的"按深度分带"意图，但换成按面积择优 ——
 * 分带只看"深度相等"会把地切碎，看面积才会把地并大。
 */
function peelRects(depth: number[], xs: number[], y0: number, out: { x0: number; x1: number; y0: number; y1: number }[]): void {
  if (!xs.length) return;
  let best = { area: 0, a: 0, b: 0, h: 0 };
  for (let i = 0; i < xs.length; i++) {
    let minh = Infinity;
    for (let j = i; j < xs.length; j++) {
      minh = Math.min(minh, depth[xs[j]] - y0);
      if (minh <= 0) break;
      const w = xs[j] - xs[i] + 1; // xs 一定连续，宽度就是差 + 1
      const area = w * minh;
      if (area > best.area) best = { area, a: i, b: j, h: minh };
    }
  }
  if (best.area <= 0) return;
  const x0 = xs[best.a];
  const x1 = xs[best.b];
  out.push({ x0, x1, y0, y1: y0 + best.h - 1 });
  // 剩余三块：左边、右边（同 y0 继续剥），以及这片上方"更高"的列（y0 抬到 y0+h）
  peelRects(depth, xs.filter((x) => x < x0), y0, out);
  peelRects(depth, xs.filter((x) => x > x1), y0, out);
  const above = xs.filter((x) => x >= x0 && x <= x1 && depth[x] - y0 > best.h);
  for (const run of contiguousRuns(above)) peelRects(depth, run, y0 + best.h, out);
}

/**
 * 一批格子 → 若干**取满的矩形宗地**。
 *
 * 流程（每一个 grid 各算一遍）：
 *   1. 每一**列**算自己的深度 = 从 row 0 起连续被占几格（row 0 没占 → 整列不算）；
 *   2. 取 `own` 与 `reduced` 的**较小值** —— `reduced` 是 C# `ReduceDepths` 削平后的深度，
 *      `own` 是这块地自己实际占到的深度。取 min 有两个作用：
 *      · 削平规则生效（深的列让到邻居一样浅）；
 *      · **绝不会把楼排到别的街区的地上**（`own` 是硬上限）。
 *   3. 拿这道剖面跑 `peelRects`，得到若干"取满最大面积"的矩形；
 *   4. 每个矩形回算 (u,v) 投影区间，供后面的合并用。
 *
 * 这里**不做最小面积过滤**：最小面积必须在"合并之后"判，否则
 * "1 列 + 邻居 9 列"里的那 1 列会在合并前就被丢掉。过滤见 `filterRects`。
 */
export function zoningRects(
  cells: { gridIndex: number; x: number; y: number }[],
  grids: CellGrid[],
  reduced?: number[][],
): ZoningRect[] {
  const byGrid = new Map<number, { x: number; y: number }[]>();
  for (const c of cells) {
    const arr = byGrid.get(c.gridIndex) ?? [];
    arr.push({ x: c.x, y: c.y });
    byGrid.set(c.gridIndex, arr);
  }

  const out: ZoningRect[] = [];
  for (const [gi, list] of byGrid) {
    const g = grids[gi];
    if (!g) continue;
    const occ = new Set<string>();
    for (const c of list) {
      if (c.x < 0 || c.y < 0 || c.x >= g.sx || c.y >= g.sy) continue;
      occ.add(`${c.x},${c.y}`);
    }

    const depth = gridDepth(occ, g);
    const red = reduced?.[gi];
    if (red) for (let x = 0; x < g.sx; x++) depth[x] = Math.min(depth[x], red[x] ?? depth[x]);

    const xs: number[] = [];
    for (let x = 0; x < g.sx; x++) if (depth[x] > 0) xs.push(x);
    const rects: { x0: number; x1: number; y0: number; y1: number }[] = [];
    for (const run of contiguousRuns(xs)) peelRects(depth, run, 0, rects);

    const d = norm(g.dir);
    const r = right(d);
    for (const rc of rects) {
      out.push({
        gridIndex: gi,
        x0: rc.x0,
        x1: rc.x1,
        y0: rc.y0,
        y1: rc.y1,
        quad: cellRect(g, rc.x0, rc.x1, rc.y0, rc.y1),
        cells: (rc.x1 - rc.x0 + 1) * (rc.y1 - rc.y0 + 1),
        toRoad: g.dir,
        origin: g.position,
        // 和 `cellRect` 逐字一致：u0c/u1c 是相对 block 中心的偏移，宽 = (x1-x0+1)·8
        u0: (g.sx - 2 * rc.x1 - 1) * (CELL_M / 2) - CELL_M / 2,
        u1: (g.sx - 2 * rc.x0 - 1) * (CELL_M / 2) + CELL_M / 2,
        v0: (g.sy - 2 * rc.y1 - 1) * (CELL_M / 2) - CELL_M / 2,
        v1: (g.sy - 2 * rc.y0 - 1) * (CELL_M / 2) + CELL_M / 2,
        sources: 1,
      });
    }
  }
  return out;
}

/** (u,v) 区间 → 米制四边形（和 `cellRect` 同一套绕序）。u/v 相对 `origin`。 */
function quadOfUV(origin: V2, dir: V2, u0: number, u1: number, v0: number, v1: number): [V2, V2, V2, V2] {
  const d = norm(dir);
  const r = right(d);
  const at = (uu: number, vv: number): V2 => [
    origin[0] + r[0] * uu + d[0] * vv,
    origin[1] + r[1] * uu + d[1] * vv,
  ];
  return [at(u0, v0), at(u1, v0), at(u1, v1), at(u0, v1)];
}

/** 把 `p` 投到以 `origin`/`dir` 为基准的 (u,v) 局部坐标。 */
function toUV(origin: V2, dir: V2, p: V2): V2 {
  const d = norm(dir);
  const r = right(d);
  const q = sub(p, origin);
  return [dot(q, r), dot(q, d)];
}

/** 合并的贴合容差（米）：两次独立计算的法向/端点误差都远小于这个 */
const JOIN_EPS = 0.6;

/**
 * **把共线、相接、同深的宗地并成一块大的**。
 *
 * 为什么必须有这一步：`ZoneBlockPredictor` 会把一条路的同侧按曲率切成若干段
 * （`chunks`，每段 ≤ 10 格），于是**同一条街的一整条满深带被拆成 2~3 个 grid**，
 * 各自成块。实测每个街区里有 **9~20 个 grid**，p50 地块因此卡在 1152 m²。
 * 而现实里（CS2 里也一样）那是**一块地**，不是三块。
 *
 * 判据（四条全中才并）：
 *   1. 朝向同向（`dot > 0.9999`）—— 反向的就是街对面，中间隔着路，不许并；
 *   2. 进深区间相同（v0/v1 各差 ≤ 0.6 m）—— 深度不同就是"前排/后排"，两回事；
 *   3. 沿路区间相接（间隙 ≤ 0.6 m）；
 *   4. 并完仍然是矩形 —— 因为 `PlanZoning.quad` 就是四边形，L 形塞不进去。
 * 反复并到没有可并的一对为止（一条边被切成 4 段时，只并一轮是不够的）。
 */
export function mergeRects(rects: ZoningRect[]): ZoningRect[] {
  let cur = rects.slice();
  for (let pass = 0; pass < 8; pass++) {
    let merged = false;
    const dead = new Set<number>();
    for (let i = 0; i < cur.length; i++) {
      if (dead.has(i)) continue;
      for (let j = i + 1; j < cur.length; j++) {
        if (dead.has(j)) continue;
        const a = cur[i];
        const b = cur[j];
        if (dot(a.toRoad, b.toRoad) < 0.9999) continue;

        // b 的四角投到 **a 的局部坐标系** 里再比。
        // 用世界投影会比出十几米的假偏移（见 `ZoningRect.origin` 的注释）。
        let bu0 = Infinity;
        let bu1 = -Infinity;
        let bv0 = Infinity;
        let bv1 = -Infinity;
        for (const p of b.quad) {
          const [uu, vv] = toUV(a.origin, a.toRoad, p);
          bu0 = Math.min(bu0, uu);
          bu1 = Math.max(bu1, uu);
          bv0 = Math.min(bv0, vv);
          bv1 = Math.max(bv1, vv);
        }
        if (Math.abs(a.v0 - bv0) > JOIN_EPS || Math.abs(a.v1 - bv1) > JOIN_EPS) continue;
        const touch = a.u1 >= bu0 - JOIN_EPS && bu1 >= a.u0 - JOIN_EPS;
        if (!touch) continue;

        // 整格外扩：只往 b 那一侧长出 **b 自己的宽度**，不取平均、也不用投影量 ——
        // 投影量会带上朝向差带来的 cos/sin 误差（0.8° × 24 m 进深 ≈ 0.33 m），
        // 那样边长会变成 40.33 m，8 m 模数的断言当场就红（D-054）。
        const wB = b.u1 - b.u0; // b 自己坐标系里的宽度，本来就是整格
        const bOnPlus = (bu0 + bu1) / 2 > (a.u0 + a.u1) / 2;
        const u0 = bOnPlus ? a.u0 : a.u0 - wB;
        const u1 = bOnPlus ? a.u1 + wB : a.u1;
        cur[i] = {
          gridIndex: a.gridIndex,
          x0: Math.min(a.x0, b.x0),
          x1: Math.max(a.x1, b.x1),
          y0: Math.min(a.y0, b.y0),
          y1: Math.max(a.y1, b.y1),
          quad: quadOfUV(a.origin, a.toRoad, u0, u1, a.v0, a.v1),
          cells: a.cells + b.cells,
          toRoad: a.toRoad,
          origin: a.origin,
          u0,
          u1,
          v0: a.v0,
          v1: a.v1,
          sources: a.sources + b.sources,
        };
        dead.add(j);
        merged = true;
      }
    }
    cur = cur.filter((_, k) => !dead.has(k));
    if (!merged) break;
  }
  return cur;
}

/**
 * 最小面积 / 最小列宽 / 最小进深过滤 —— **只在合并之后判**。
 *
 * 顺序很关键：先过滤再合并，会把"1 列 + 邻居 9 列"里的那 1 列先丢掉，
 * 于是那条满深带永远短一截；先合并再过滤，那 1 列就跟着邻居一起留下来。
 *
 * 三个下限都有出处：
 *   · `minCols = 2`（16 m）：C# 的 `ZoneLots` 注释"Speckled pads are ≥ 2 cells wide"；
 *   · `minRows = 2`（16 m）：C# 的 `ZoneGuard.MinRows = 2`；
 *   · `minCells` 按用途给（见 `placemaker.ts` 的 `MIN_CELLS`），工业大、住宅小。
 */
export function filterRects(rects: ZoningRect[], opts: ZoningOptions = {}): ZoningRect[] {
  const minCols = Math.max(1, opts.minCols ?? 2);
  const minRows = Math.max(1, opts.minRows ?? 2);
  const minCells = Math.max(1, opts.minCells ?? 4);
  return rects.filter((r) => {
    // 尺寸一律从 (u,v) 区间量：合并过的宗地横跨两个 grid，
    // 它的 `x0/x1` 是两边格子号取 min/max，**不是**真实的列数（x 是谁家的 x？）。
    const cols = Math.round((r.u1 - r.u0) / CELL_M);
    const rows = Math.round((r.v1 - r.v0) / CELL_M);
    return cols >= minCols && rows >= minRows && r.cells >= minCells;
  });
}

export { right as cellRight, sub as cellSub, add as cellAdd, mul as cellMul, dot as cellDot, len as cellLen };
