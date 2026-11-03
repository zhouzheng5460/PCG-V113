// 禁建区避障（ZoneObstacles）。
//
// 要解决的问题
// ------------
// 之前禁区是按"方向场源"参与合成的：把禁区的边延长成几条长直线，让街道
// 与它平行。结果是路网被**掰成与禁区边平行**、一路铺到禁区红线上，
// 布尔切块又把穿过禁区的那几段切掉 —— 于是禁区四周只剩几段被砍断的沥青，
// 转角处等级还跳变（local→collector→arterial）。读起来就是"路硬怼在禁区上"。
//
// 用户要的是**避障**：路网像水绕过石头一样，自然地从旁边绕开，留出一段让距。
//
// 为什么不用"更强的切向场源"来做
// ------------------------------
// 纯切向场会把进入影响带的流线**捕获成绕禁区一圈的闭合环**：闭合环会被
// 追踪器的自交检测掐断，于是每一条被捕获的线都留下两个断头 —— 正是要消灭的东西。
// 而"越靠近越切向"的权重一旦调强就必然捕获，调弱则路照样怼上去，没有中间状态。
//
// 所以这里不用场，用**几何绕行**：
//   1. 把禁区外扩一个让距 pad，得到一条圆角轮廓（凹角保持尖、凸角成圆弧）；
//   2. 已经追好的流线里，凡是落进"禁区 + pad"带的那一段，整段换成沿这条
//      外扩轮廓走的弧线，两端的连接点做几步拉普拉斯平滑。
// 每条线保留自己的入射 / 出射位置，所以线之间不会塌缩到同一条轮廓上，
// 也不会出现闭合环 —— 这是"绕行"能成立的关键。

import { Pt } from '../../core/geom';

/** 有符号距离 + 最近点：sd < 0 表示点在环内 */
interface SdSample {
  sd: number;
  /** 最近点 */
  qx: number;
  qy: number;
}

function ringSd(ring: Pt[], x: number, y: number): SdSample {
  let best = Infinity;
  let qx = 0;
  let qy = 0;
  for (let i = 0; i < ring.length; i++) {
    const a = ring[i];
    const b = ring[(i + 1) % ring.length];
    const dx = b.x - a.x;
    const dy = b.y - a.y;
    const L = dx * dx + dy * dy;
    let t = L < 1e-12 ? 0 : ((x - a.x) * dx + (y - a.y) * dy) / L;
    t = t < 0 ? 0 : t > 1 ? 1 : t;
    const px = a.x + dx * t;
    const py = a.y + dy * t;
    const d = Math.hypot(x - px, y - py);
    if (d < best) {
      best = d;
      qx = px;
      qy = py;
    }
  }
  return { sd: inside(ring, x, y) ? -best : best, qx, qy };
}

function inside(ring: Pt[], x: number, y: number): boolean {
  let hit = false;
  for (let i = 0, j = ring.length - 1; i < ring.length; j = i++) {
    const xi = ring[i].x;
    const yi = ring[i].y;
    const xj = ring[j].x;
    const yj = ring[j].y;
    if (yi > y !== yj > y && x < ((xj - xi) * (y - yi)) / (yj - yi) + xi) hit = !hit;
  }
  return hit;
}

function signedArea(ring: Pt[]): number {
  let a = 0;
  for (let i = 0; i < ring.length; i++) {
    const p = ring[i];
    const q = ring[(i + 1) % ring.length];
    a += p.x * q.y - q.x * p.y;
  }
  return a * 0.5;
}

/**
 * 把环按 outward 方向偏移 pad，**凸角做成圆弧**。
 *
 * 为什么一定要圆弧：拐角处如果按"两线求交"做尖角（miter），绕行弧线走到角上会
 * 突然拐 90°，路口就在那儿拧成一个尖 —— 这正是"路口形状不自然"的来源。
 * 用圆弧过渡，路是连续弯过去的，和用户画的参考线一致。
 */
function offsetLoop(ring: Pt[], pad: number): Pt[] {
  const r = signedArea(ring) < 0 ? ring.slice().reverse() : ring;
  const n = r.length;
  const out: Pt[] = [];
  const ARC = 6;

  for (let i = 0; i < n; i++) {
    const prev = r[(i - 1 + n) % n];
    const cur = r[i];
    const next = r[(i + 1) % n];

    // 两条边各自的**外法线**（CCW 环，边 a→b 的外法线 = (dy, -dx)/L）
    const d0 = norm(cur.x - prev.x, cur.y - prev.y);
    const d1 = norm(next.x - cur.x, next.y - cur.y);
    if (!d0 || !d1) continue;
    const n0x = d0.y;
    const n0y = -d0.x;
    const n1x = d1.y;
    const n1y = -d1.x;

    // 叉积 > 0 = 左转 = 凸角（CCW 环）
    const cross = d0.x * d1.y - d0.y * d1.x;
    if (cross > 1e-6) {
      // 凸角：从 n0 转到 n1，走短弧
      const a0 = Math.atan2(n0y, n0x);
      let da = Math.atan2(n1y, n1x) - a0;
      while (da > Math.PI) da -= Math.PI * 2;
      while (da < -Math.PI) da += Math.PI * 2;
      for (let k = 0; k <= ARC; k++) {
        const a = a0 + (da * k) / ARC;
        out.push(new Pt(cur.x + Math.cos(a) * pad, cur.y + Math.sin(a) * pad));
      }
    } else {
      // 凹角（或近共线）：偏移线在这里相交，一个点就够
      out.push(new Pt(cur.x + n1x * pad, cur.y + n1y * pad));
    }
  }
  return out;
}

function norm(x: number, y: number): Pt | null {
  const L = Math.hypot(x, y);
  return L < 1e-9 ? null : new Pt(x / L, y / L);
}

/** 闭合折线上按弧长定位：返回 {x, y, s}，s 是累计弧长（0 ~ 周长） */
interface LoopPoint {
  x: number;
  y: number;
  s: number;
}

function projectOnLoop(loop: Pt[], x: number, y: number): LoopPoint {
  let best = Infinity;
  let bx = 0;
  let by = 0;
  let bs = 0;
  let acc = 0;
  for (let i = 0; i < loop.length; i++) {
    const a = loop[i];
    const b = loop[(i + 1) % loop.length];
    const dx = b.x - a.x;
    const dy = b.y - a.y;
    const L = dx * dx + dy * dy;
    let t = L < 1e-12 ? 0 : ((x - a.x) * dx + (y - a.y) * dy) / L;
    t = t < 0 ? 0 : t > 1 ? 1 : t;
    const px = a.x + dx * t;
    const py = a.y + dy * t;
    const d = Math.hypot(x - px, y - py);
    if (d < best) {
      best = d;
      bx = px;
      by = py;
      bs = acc + Math.sqrt(L) * t;
    }
    acc += Math.sqrt(L);
  }
  return { x: bx, y: by, s: bs };
}

/** 沿闭合折线从 s0 走到 s1（走短弧），按约 step 的间距取点 */
function walkLoop(loop: Pt[], s0: number, s1: number, step: number): Pt[] {
  let total = 0;
  for (let i = 0; i < loop.length; i++) total += Pt.distance(loop[i], loop[(i + 1) % loop.length]);
  if (total < 1e-6) return [];

  let d = s1 - s0;
  while (d > total / 2) d -= total;
  while (d < -total / 2) d += total;

  const n = Math.max(2, Math.ceil(Math.abs(d) / Math.max(step, 0.2)));
  const out: Pt[] = [];
  for (let k = 0; k <= n; k++) {
    let s = s0 + (d * k) / n;
    while (s < 0) s += total;
    while (s >= total) s -= total;
    out.push(atLoop(loop, s));
  }
  return out;
}

function atLoop(loop: Pt[], s: number): Pt {
  const total = (() => {
    let acc = 0;
    for (let i = 0; i < loop.length; i++) acc += Pt.distance(loop[i], loop[(i + 1) % loop.length]);
    return acc;
  })();
  let rem = ((s % total) + total) % total;
  for (let i = 0; i < loop.length; i++) {
    const a = loop[i];
    const b = loop[(i + 1) % loop.length];
    const L = Pt.distance(a, b);
    if (rem <= L || i === loop.length - 1) {
      const t = L < 1e-9 ? 0 : rem / L;
      return new Pt(a.x + (b.x - a.x) * t, a.y + (b.y - a.y) * t);
    }
    rem -= L;
  }
  return loop[0];
}

/** 轻量拉普拉斯平滑：只在拐弯处起作用，直线段不受影响 */
function smooth(pts: Pt[], passes: number, weight: number): Pt[] {
  let out = pts;
  for (let p = 0; p < passes; p++) {
    const next: Pt[] = out.map((q) => q);
    for (let i = 1; i + 1 < out.length; i++) {
      const ax = (out[i - 1].x + out[i + 1].x) / 2;
      const ay = (out[i - 1].y + out[i + 1].y) / 2;
      next[i] = new Pt(out[i].x + (ax - out[i].x) * weight, out[i].y + (ay - out[i].y) * weight);
    }
    out = next;
  }
  return out;
}

export class ZoneObstacles {
  private rings: Pt[][];
  private pad: number;
  private loops: Pt[][];

  constructor(rings: Pt[][], pad: number) {
    this.rings = rings.filter((r) => r.length >= 3);
    this.pad = Math.max(0, pad);
    this.loops = this.rings.map((r) => offsetLoop(r, this.pad));
  }

  get empty(): boolean {
    return this.rings.length === 0;
  }

  /** 到最近禁区的有符号距离（内负外正，多个禁区取最小） */
  sd(x: number, y: number): number {
    let best = Infinity;
    for (const r of this.rings) {
      const s = ringSd(r, x, y).sd;
      if (s < best) best = s;
    }
    return best === Infinity ? Infinity : best;
  }

  /** 这个点是否已经进了"禁区 + 让距"带 */
  blocks(x: number, y: number): boolean {
    return this.sd(x, y) < this.pad;
  }

  /**
   * 让距轮廓本身，作为**一条环路**交给路网。
   *
   * 这是"避障"的落点：绕行不能靠每条流线各绕各的 —— 两族流线绕到同一条轮廓上
   * 会互相重叠（平面图立刻出针尖碎面），而绕行弧只在"恰好平行的那些线"上出现，
   * 于是有的边有绕行路、有的边什么都没有，一圈读下来是断的。
   *
   * 直接生成一条闭合的让距环路：它一定连续、转角是圆弧、断面统一。
   * 两族街道都只到这条环路上收口（T 口），城市里本来就是这么组织的。
   */
  ringRoads(): Pt[][] {
    return this.loops.filter((l) => l.length >= 6);
  }

  /** 最近禁区的**外法线**（由禁区指向该点）；在带里时用来判"这条线是绕着走还是横着撞" */
  normalAt(x: number, y: number): { nx: number; ny: number; d: number } {
    let bd = Infinity;
    let bx = x;
    let by = y;
    for (const r of this.rings) {
      const s = ringSd(r, x, y);
      const d = Math.abs(s.sd);
      if (d < bd) {
        bd = d;
        bx = s.qx;
        by = s.qy;
      }
    }
    const L = Math.hypot(x - bx, y - by);
    if (!isFinite(bd) || L < 1e-9) return { nx: 1, ny: 0, d: bd };
    return { nx: (x - bx) / L, ny: (y - by) / L, d: bd };
  }

  /**
   * 把一条流线按禁区拆开。
   *
   * 两种处理，取决于"这条线是绕着禁区走还是横着撞上去"：
   *
   * · **大致平行**（线与边界切线夹角 < 55°）：整段换成沿让距轮廓的绕行弧，
   *   线继续往前走 —— 这就是"路自然绕开禁区"的那条弧。
   * · **大致垂直**：不绕。在让距轮廓处把线**切断**，两头各自收口。
   *   收口点正好落在绕行路上，于是形成正常的 T 口。
   *
   * 为什么垂直的那一支必须切断而不是也绕：两族流线互相垂直，如果都绕，
   * 它们会一起挤到同一条让距轮廓上互相重叠，平面图里立刻长出一排针尖碎面
   * （实测：禁区边上多出十几片细长三角形）。城市里本来就该是"一条路绕、
   * 另一条路 T 上来"，不是两条路叠着走。
   */
  route(pts: Pt[], step: number, mode: 'wrap' | 'cut' = 'wrap'): Pt[][] {
    if (this.rings.length === 0 || pts.length < 3) return [pts];

    let segs: Pt[][] = [pts];
    for (let pass = 0; pass < 3; pass++) {
      let changed = false;
      const next: Pt[][] = [];
      for (const seg of segs) {
        const split = this.routeOne(seg, step, mode);
        if (split.length !== 1) changed = true;
        next.push(...split);
      }
      segs = next;
      if (!changed) break;
    }
    return segs.map((s) => this.finish(s)).filter((s) => s.length >= 2);
  }

  /**
   * 收尾：平滑 + **让距兜底**。
   *
   * 平滑会把绕行弧在禁区拐角处抹圆一点，抹进去的那几米可能重新落进让距带里
   * （实测每个种子 1~3 条边，全在禁区凸角）。所以平滑之后再强制夹一次：
   * 任何 sd < pad 的点直接投回让距轮廓上。只动这几个点，不会引起塌缩。
   */
  private finish(pts: Pt[]): Pt[] {
    let out = smooth(pts, 2, 0.35);
    for (let pass = 0; pass < 2; pass++) {
      let moved = false;
      out = out.map((p) => {
        if (this.sd(p.x, p.y) >= this.pad - 1e-3) return p;
        moved = true;
        return this.snapToLoop(p);
      });
      if (!moved) break;
      out = smooth(out, 1, 0.2);
    }
    return out;
  }

  private routeOne(pts: Pt[], step: number, mode: 'wrap' | 'cut'): Pt[][] {
    const hit = pts.map((p) => this.blocks(p.x, p.y));
    if (!hit.some(Boolean)) return [pts];

    const out: Pt[][] = [];
    let cur: Pt[] = [];
    let i = 0;
    while (i < pts.length) {
      if (!hit[i]) {
        cur.push(pts[i]);
        i++;
        continue;
      }
      let j = i;
      while (j + 1 < pts.length && hit[j + 1]) j++;

      const before = i > 0 ? pts[i - 1] : null;
      const after = j + 1 < pts.length ? pts[j + 1] : null;

      // 整条线都在带里：没有"绕过去"可言，丢掉
      if (!before && !after) return [];
      // 从头就在带里：把前面这段扔掉，从带外的点接上
      if (!before) {
        cur = [];
        i = j + 1;
        continue;
      }
      if (!after) {
        cur.push(before);
        break;
      }

      // 判定走向：弦方向 vs 边界切线
      const hx = after.x - before.x;
      const hy = after.y - before.y;
      const hl = Math.hypot(hx, hy);
      const mid = pts[Math.min(pts.length - 1, Math.floor((i + j) / 2))];
      const n = this.normalAt(mid.x, mid.y);
      // 切线 = 法线转 90°
      const parallel = hl > 1e-9 ? Math.abs((hx / hl) * -n.ny + (hy / hl) * n.nx) : 0;

      if (mode === 'wrap' && parallel >= 0.8) {
        // 基本平行才绕。放宽到 0.57 会在弧与直线的接点留下一个几十度的折角 ——
        // 那条折角进平面图就是一个斜交路口，比"切断"更难看。
        const arc = this.arcBetween(before, after, step);
        cur.push(before);
        if (arc.length > 1) for (let k = 1; k < arc.length - 1; k++) cur.push(arc[k]);
        cur.push(after);
        i = j + 1;
        continue;
      }

      // 横着撞上来：在让距轮廓上收口，剩下半条另起一段。
      // 收口点必须**投影到轮廓上**，不能停在最后一个带外点 —— 那里离轮廓还有
      // 零点几到几米，路端悬在空中，读起来是断头；投到轮廓上，所有横路的端头
      // 都落在同一条曲线上，与绕行弧相接就自动成了 T 口。
      const pe = this.snapToLoop(before);
      const ps = this.snapToLoop(after);
      cur.push(pe);
      out.push(cur);
      cur = [ps];
      i = j + 1;
    }
    if (cur.length >= 2) out.push(cur);
    return out.length > 0 ? out : [];
  }

  /** 把点投到最近禁区的让距轮廓上（横路的收口点用） */
  private snapToLoop(p: Pt): Pt {
    let best = p;
    let bd = Infinity;
    for (const loop of this.loops) {
      if (loop.length < 3) continue;
      const q = projectOnLoop(loop, p.x, p.y);
      const d = Math.hypot(q.x - p.x, q.y - p.y);
      if (d < bd) {
        bd = d;
        best = new Pt(q.x, q.y);
      }
    }
    return best;
  }

  /**
   * 在让距轮廓上，从 a 绕到 b 的一段弧。
   *
   * 选哪一圈：按"a、b 到各自投影的距离之和"最小来挑 —— 也就是挑这台线
   * 真正在绕的那块禁区，而不是碰巧弧长更短的另一块。
   */
  private arcBetween(a: Pt, b: Pt, step: number): Pt[] {
    let best: Pt[] = [];
    let bestNear = Infinity;
    for (const loop of this.loops) {
      if (loop.length < 3) continue;
      const pa = projectOnLoop(loop, a.x, a.y);
      const pb = projectOnLoop(loop, b.x, b.y);
      const near = Math.hypot(pa.x - a.x, pa.y - a.y) + Math.hypot(pb.x - b.x, pb.y - b.y);
      if (near > bestNear) continue;
      const arc = walkLoop(loop, pa.s, pb.s, step);
      if (arc.length < 2) continue;
      bestNear = near;
      best = [new Pt(pa.x, pa.y), ...arc.slice(1, -1), new Pt(pb.x, pb.y)];
    }
    return best;
  }
}
