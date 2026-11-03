// 流线追踪：沿张量场积分出两组"场线"（主方向 / 次方向）。
//
// 用的是 RK4（和 RogueCities 一致），比 Euler 在曲率大的地方稳定得多。
// 种子点用 Jobard–Lefer 的思路：先用抖动网格撒一遍，之后每接受一条流线，
// 就沿它的法向 ±spacing 处再推入种子 —— 这样线距会自动保持均匀，
// 不会出现"中心密、边缘空"或者大片空白。
//
// 注意：这里刻意把"场线"和"道路"分开。场线只是几何参考，
// 一部分才成为道路，一部分用来切分地块，下一步还有筛选。

import { Pt } from '../../core/geom';
import { Rng } from '../../core/rng';
import type { EdgeClass } from '../edgeclass';
import type { ZoneObstacles } from './obstacle';
import { type Family, TensorField } from './tensor';

export interface FieldLine {
  pts: Pt[];
  family: Family;
  /** 这条线在下游当哪一级道路 */
  cls: EdgeClass;
  order: number;
  length: number;
}

export interface TraceOptions {
  /** 线距（≈街区尺度） */
  spacing: number;
  /** 积分步长，默认 spacing × 0.22 */
  step?: number;
  /** 单条线最大长度，默认 6 × 城市半径 */
  maxLength?: number;
  /** 每族最多几条线 */
  maxLines?: number;
  /** 城市轮廓判定 */
  inside: (p: Pt) => boolean;
  bounds: { minX: number; maxX: number; minY: number; maxY: number };
  /** 一致性低于该值视为方向不可信，停止积分 */
  minCoherence?: number;
  classFor: (family: Family, order: number) => EdgeClass;
  /**
   * 禁建区避障。给了它，流线穿过禁区的那一段会被换成贴着禁区绕过去的弧线
   * （见 field/obstacle.ts）。种子也不会撒进"禁区 + 让距"带里。
   */
  obstacles?: ZoneObstacles;
  /**
   * 避障角色：'wrap' = 这个方向的路绕着禁区走，'cut' = 只到禁区边收口。
   *
   * 必须分工。两族流线互相垂直，如果都绕，它们会一起挤到同一条让距轮廓上
   * 互相重叠 —— 平面图里立刻长出一排针尖碎面和"骑在路上的楼"。
   * 城市里本来就该是"一个方向的路绕过去、另一个方向的路 T 上来"。
   */
  avoidRole?: 'wrap' | 'cut';
  /** 打印每轮播种统计 */
  debug?: boolean;
}

/** 均匀网格哈希：判"这个点附近是否已经有线" */
class PointHash {
  private cell: number;
  private map = new Map<string, Pt[]>();

  constructor(cell: number) {
    this.cell = cell;
  }

  private key(ix: number, iy: number): string {
    return `${ix},${iy}`;
  }

  add(p: Pt): void {
    const ix = Math.floor(p.x / this.cell);
    const iy = Math.floor(p.y / this.cell);
    const k = this.key(ix, iy);
    const list = this.map.get(k);
    if (list) list.push(p);
    else this.map.set(k, [p]);
  }

  near(p: Pt, r: number): boolean {
    const ix = Math.floor(p.x / this.cell);
    const iy = Math.floor(p.y / this.cell);
    const r2 = r * r;
    for (let dx = -1; dx <= 1; dx++) {
      for (let dy = -1; dy <= 1; dy++) {
        const list = this.map.get(this.key(ix + dx, iy + dy));
        if (!list) continue;
        for (const q of list) {
          const ddx = q.x - p.x;
          const ddy = q.y - p.y;
          if (ddx * ddx + ddy * ddy < r2) return true;
        }
      }
    }
    return false;
  }
}

function add(a: Pt, b: Pt, s: number): Pt {
  return new Pt(a.x + b.x * s, a.y + b.y * s);
}

function len(a: Pt): number {
  return Math.hypot(a.x, a.y);
}

/** 把单位向量翻到与参考方向同向，避免积分在 θ 与 θ+π 之间来回跳 */
function orient(v: Pt, ref: Pt): Pt {
  return v.x * ref.x + v.y * ref.y < 0 ? new Pt(-v.x, -v.y) : v;
}

interface MarchResult {
  pts: Pt[];
  length: number;
  stopped: 'outside' | 'long' | 'weak' | 'steps' | 'loop';
}

/**
 * 自身回环检测：场里有旋涡/闭合环时，流线会绕圈画回来。
 * 绕圈会让一条线穿过自己，切出几千个碎面，必须先掐断。
 * 用一个小网格哈希记录本条线走过的点，撞到 6 步以前的点就停。
 */
class SelfHash {
  private cell: number;
  private map = new Map<string, number[]>();
  private pts: Pt[];

  constructor(cell: number, pts: Pt[]) {
    this.cell = cell;
    this.pts = pts;
  }

  add(p: Pt, idx: number): void {
    const k = `${Math.floor(p.x / this.cell)},${Math.floor(p.y / this.cell)}`;
    const list = this.map.get(k);
    if (list) list.push(idx);
    else this.map.set(k, [idx]);
  }

  hitsOld(p: Pt, r: number, minIdx: number): boolean {
    const ix = Math.floor(p.x / this.cell);
    const iy = Math.floor(p.y / this.cell);
    const r2 = r * r;
    for (let dx = -1; dx <= 1; dx++) {
      for (let dy = -1; dy <= 1; dy++) {
        const list = this.map.get(`${ix + dx},${iy + dy}`);
        if (!list) continue;
        for (const i of list) {
          if (i >= minIdx) continue;
          const q = this.pts[i];
          const ddx = q.x - p.x;
          const ddy = q.y - p.y;
          if (ddx * ddx + ddy * ddy < r2) return true;
        }
      }
    }
    return false;
  }
}

/** 从 start 沿 ref 方向单向积分 */
function march(
  field: TensorField,
  family: Family,
  start: Pt,
  ref: Pt,
  opts: TraceOptions,
  h: number
): MarchResult {
  const pts: Pt[] = [start];
  const maxSteps = Math.ceil((opts.maxLength ?? opts.spacing * 30) / h);
  const minCoh = opts.minCoherence ?? 0.03;
  const self = new SelfHash(h * 2, pts);
  self.add(start, 0);
  let cur = start;
  let dir = orient(field.unit(cur.x, cur.y, family), ref);
  let dist = 0;
  let stopped: MarchResult['stopped'] = 'steps';

  for (let i = 0; i < maxSteps; i++) {
    const k1 = dir;
    const k2 = orient(field.unit(cur.x + (k1.x * h) / 2, cur.y + (k1.y * h) / 2, family), dir);
    const k3 = orient(field.unit(cur.x + (k2.x * h) / 2, cur.y + (k2.y * h) / 2, family), dir);
    const k4 = orient(field.unit(cur.x + k3.x * h, cur.y + k3.y * h, family), dir);

    const step = new Pt(
      ((k1.x + 2 * k2.x + 2 * k3.x + k4.x) * h) / 6,
      ((k1.y + 2 * k2.y + 2 * k3.y + k4.y) * h) / 6
    );
    const sl = len(step);
    if (sl < h * 0.2) {
      stopped = 'weak';
      break;
    }

    const next = new Pt(cur.x + step.x, cur.y + step.y);
    if (!opts.inside(next)) {
      stopped = 'outside';
      break;
    }
    const coh = field.sample(next.x, next.y).coherence;
    if (coh < minCoh) {
      stopped = 'weak';
      break;
    }
    // 绕回自己走过的位置 → 这是闭合环，停在这里，别把城切碎
    if (i > 8 && self.hitsOld(next, h * 1.4, i - 6)) {
      stopped = 'loop';
      break;
    }

    pts.push(next);
    self.add(next, pts.length - 1);
    dist += sl;
    dir = k4;
    cur = next;
    if (dist > (opts.maxLength ?? opts.spacing * 30)) {
      stopped = 'long';
      break;
    }
  }
  return { pts, length: dist, stopped };
}

/** 双向积分成一条完整流线 */
function traceBoth(field: TensorField, family: Family, seed: Pt, opts: TraceOptions, h: number): Pt[] {
  const d = field.unit(seed.x, seed.y, family);
  const fwd = march(field, family, seed, d, opts, h);
  const back = march(field, family, seed, new Pt(-d.x, -d.y), opts, h);
  // 反向结果去掉种子点后倒序接在前面
  const head = back.pts.slice(1).reverse();
  return head.concat(fwd.pts);
}

function polylineLength(pts: Pt[]): number {
  let s = 0;
  for (let i = 0; i + 1 < pts.length; i++) s += Math.hypot(pts[i + 1].x - pts[i].x, pts[i + 1].y - pts[i].y);
  return s;
}

/** 补种：扫一遍还没被任何流线覆盖的位置，补进队列。没有这一步，低一致性区域会留大洞。 */
function fillGaps(queue: Pt[], hash: PointHash, opts: TraceOptions, spacing: number): void {
  const step = spacing * 0.7;
  for (let y = opts.bounds.minY; y <= opts.bounds.maxY; y += step) {
    for (let x = opts.bounds.minX; x <= opts.bounds.maxX; x += step) {
      const p = new Pt(x, y);
      if (!opts.inside(p)) continue;
      if (opts.obstacles?.blocks(p.x, p.y)) continue;
      if (hash.near(p, spacing * 0.62)) continue;
      queue.push(p);
    }
  }
}

/**
 * 从**指定种子点**双向积分出一条流线。
 *
 * 与 traceFamily 的区别：这里不撒网格、不做线距去重，种子点由调用方给。
 * 地块内部路需要这个能力 —— 种子取在界边中点、朝地块里走，
 * 追出来的线两端必然落在地块轮廓上，于是"内部路接外部主路"是几何必然，
 * 而不是事后拿延长线去硬怼（怼不准就留下一个 0.3 单元的断口，路网就断了）。
 */
export function traceThrough(
  field: TensorField,
  family: Family,
  seed: Pt,
  opts: TraceOptions,
  dir?: Pt
): Pt[] {
  const h = opts.step ?? opts.spacing * 0.22;
  if (!dir) return traceBoth(field, family, seed, opts, h);
  const fwd = march(field, family, seed, dir, opts, h);
  const back = march(field, family, seed, new Pt(-dir.x, -dir.y), opts, h);
  return back.pts.slice(1).reverse().concat(fwd.pts);
}

/** 追踪一整族流线 */
export function traceFamily(field: TensorField, family: Family, opts: TraceOptions): FieldLine[] {
  const spacing = opts.spacing;
  const h = opts.step ?? spacing * 0.22;
  const maxLines = opts.maxLines ?? 160;
  const hash = new PointHash(spacing * 0.5);

  // 抖动网格种子 + 打乱顺序，避免从角落开始一路铺过去
  const seeds: Pt[] = [];
  for (let y = opts.bounds.minY; y <= opts.bounds.maxY; y += spacing) {
    for (let x = opts.bounds.minX; x <= opts.bounds.maxX; x += spacing) {
      seeds.push(
        new Pt(x + (Rng.float() - 0.5) * spacing * 0.7, y + (Rng.float() - 0.5) * spacing * 0.7)
      );
    }
  }
  for (let i = seeds.length - 1; i > 0; i--) {
    const j = Math.floor(Rng.float() * (i + 1));
    const t = seeds[i];
    seeds[i] = seeds[j];
    seeds[j] = t;
  }

  const queue: Pt[] = seeds;
  const lines: FieldLine[] = [];
  let order = 0;
  let head = 0;
  let guard = 0;

  for (let pass = 0; pass <= 2; pass++) {
    if (pass > 0) fillGaps(queue, hash, opts, spacing);
    const before = lines.length;
    let tried = 0;
    let rejectedShort = 0;
    let rejectedDup = 0;

    while (head < queue.length && lines.length < maxLines && guard++ < 40000) {
      const seed = queue[head++];
      if (!opts.inside(seed)) continue;
      // 禁区（含让距带）里不播种：从带里长出来的线没有"绕过去"这回事，
      // 只能原地被推出带外，追出来是一小截乱线。
      if (opts.obstacles?.blocks(seed.x, seed.y)) continue;
      if (hash.near(seed, spacing * 0.62)) continue;
      tried++;

      const raw = traceBoth(field, family, seed, opts, h);
      // 避障：绕的绕、撞的切（见 field/obstacle.ts 的 route）。必须在
      // "长度 / 重复"判据之前做 —— 避障会改长度和形状，先判再避等于拿没避过的线去重。
      const pieces = opts.obstacles ? opts.obstacles.route(raw, h * 2, opts.avoidRole ?? 'wrap') : [raw];

      for (const pts of pieces) {
        if (lines.length >= maxLines) break;
        const length = polylineLength(pts);
        if (pts.length < 4 || length < spacing * 1.1) {
          rejectedShort++;
          continue;
        }

        // 与已有线太贴近的候选直接丢掉。
        // 这类"平行孪生线"会在平面图里切出针尖状碎面，碎面被面积阈值丢掉后，
        // 它两侧的边就只剩一个地块，路网随之出现断口 —— 断网的根源在这里。
        let near = 0;
        let samples = 0;
        for (let i = 0; i < pts.length; i += 4) {
          samples++;
          if (hash.near(pts[i], spacing * 0.5)) near++;
        }
        if (samples > 0 && near / samples > 0.45) {
          rejectedDup++;
          continue;
        }

        lines.push({ pts, family, cls: opts.classFor(family, order), order, length });
        order++;

        // 沿线登记已占用，并按线距往法向播新种子（Jobard–Lefer）
        let acc = 0;
        for (let i = 0; i + 1 < pts.length; i++) {
          hash.add(pts[i]);
          const dx = pts[i + 1].x - pts[i].x;
          const dy = pts[i + 1].y - pts[i].y;
          const seg = Math.hypot(dx, dy);
          if (seg < 1e-9) continue;
          acc += seg;
          if (acc < spacing) continue;
          acc = 0;
          const nx = -dy / seg;
          const ny = dx / seg;
          queue.push(new Pt(pts[i].x + nx * spacing, pts[i].y + ny * spacing));
          queue.push(new Pt(pts[i].x - nx * spacing, pts[i].y - ny * spacing));
        }
      }
    }

    if (opts.debug) {
      console.log(
        `  [${family}] pass${pass}: 试 ${tried} / 太短 ${rejectedShort} / 太近 ${rejectedDup} / 新增 ${lines.length - before} / 队列 ${queue.length}`
      );
    }
  }

  return lines;
}
