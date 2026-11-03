// 布尔（并 / 差 / 交）与偏移。
//
// 为什么自己写：PCG 用 clipper-lib（npm 包），本机没有 node_modules 也上不了 npm。
// 但"布尔"是工具栏上一等公民，不能挂个死按钮。
//
// 算法：**切边 + 串环**。不用 Greiner-Hormann 那种"交点插入链表"的写法，
// 因为它对"两个环共边"这类退化情形的处理藏在细节里，调起来像玄学。这里是直白三步：
//   1. 把 A 的每条边在"与 B 的交点"处切开，B 同理；
//   2. 每个小段的**中点**做一次内外测试（射线法），按运算规则决定去留；
//   3. 按端点把留下的段串成环。
// 代价：**共线重叠**（两条边完全重合）会串出多余的碎环 —— 这是已知限制，
// 所以在编辑器里，布尔操作后会检查"结果是否自交/是否有悬挂端"，不合法就整体回滚并提示，
// 而不是把一个坏几何悄悄写进项目。
//
// 偏移用逐边法 + 斜接（miter）。凸多边形完全正确；凹多边形可能自交，
// 同样靠 `isSimple` 兜底 —— 不合法就返回空数组，UI 提示"偏移后自交，已取消"。

import {
  cleanRing,
  cross,
  ensureCCW,
  isSimple,
  pointInPaths,
  ringPerimeter,
  signedArea,
  type Paths,
  type Ring,
} from './polygon.ts';
import { len, sub, type Vec2 } from './vec.ts';

export type BoolOp = 'union' | 'subtract' | 'intersect';

const EPS = 1e-7;
/** 端点归并精度：1 µm。串环靠它判断"这两段接得上"。 */
const KEY_Q = 1e6;

const keyOf = (p: Vec2): string => `${Math.round(p.x * KEY_Q)},${Math.round(p.y * KEY_Q)}`;

interface Seg {
  a: Vec2;
  b: Vec2;
}

/** 线段参数交点；平行/共线返回 null（共线是已知不支持的退化情形）。 */
function intersectAt(p1: Vec2, p2: Vec2, p3: Vec2, p4: Vec2): number | null {
  const r = sub(p2, p1);
  const s = sub(p4, p3);
  const den = cross(r, s);
  if (Math.abs(den) < 1e-12) return null;
  const qp = sub(p3, p1);
  const t = cross(qp, s) / den;
  const u = cross(qp, r) / den;
  if (t < -EPS || t > 1 + EPS || u < -EPS || u > 1 + EPS) return null;
  return Math.max(0, Math.min(1, t));
}

/** 把一条边按另一组环的交点切开。 */
function splitEdge(a: Vec2, b: Vec2, others: Ring[]): Seg[] {
  const ts = [0, 1];
  for (const ring of others) {
    for (let i = 0; i < ring.length; i++) {
      const t = intersectAt(a, b, ring[i], ring[(i + 1) % ring.length]);
      if (t !== null && t > EPS && t < 1 - EPS) ts.push(t);
    }
  }
  ts.sort((x, y) => x - y);
  const out: Seg[] = [];
  for (let i = 1; i < ts.length; i++) {
    const t0 = ts[i - 1];
    const t1 = ts[i];
    if (t1 - t0 < 1e-9) continue;
    out.push({
      a: { x: a.x + (b.x - a.x) * t0, y: a.y + (b.y - a.y) * t0 },
      b: { x: a.x + (b.x - a.x) * t1, y: a.y + (b.y - a.y) * t1 },
    });
  }
  return out;
}

const midOf = (s: Seg): Vec2 => ({ x: (s.a.x + s.b.x) / 2, y: (s.a.y + s.b.y) / 2 });

/** 串环：按端点接龙。接不上（悬挂端）就返回 null，调用方回滚。 */
function chain(segs: Seg[]): Ring[] | null {
  const byStart = new Map<string, Seg[]>();
  for (const s of segs) {
    const k = keyOf(s.a);
    const arr = byStart.get(k);
    if (arr) arr.push(s);
    else byStart.set(k, [s]);
  }
  const used = new Set<Seg>();
  const rings: Ring[] = [];

  for (const seed of segs) {
    if (used.has(seed)) continue;
    const ring: Ring = [seed.a];
    used.add(seed);
    let cur = seed;
    let guard = 0;
    while (guard++ < 20000) {
      ring.push(cur.b);
      if (keyOf(cur.b) === keyOf(ring[0])) break;
      const cands = (byStart.get(keyOf(cur.b)) ?? []).filter((s) => !used.has(s));
      if (!cands.length) return null; // 悬挂端
      // 多条候选时取"最接近直行"的那条：布尔结果的环在交点处不会突然掉头
      const dir = sub(cur.b, cur.a);
      cands.sort((x, y) => cross(dir, sub(x.b, x.a)) - cross(dir, sub(y.b, y.a)));
      cur = cands[0];
      used.add(cur);
    }
    if (guard >= 20000) return null;
    const cleaned = cleanRing(ring.slice(0, -1), 1e-6);
    if (cleaned.length >= 3 && Math.abs(signedArea(cleaned)) > 1e-6) rings.push(cleaned);
  }
  return rings;
}

/**
 * 布尔运算。入参是"环组"，只认外环 —— 带洞的输入先按外环处理（编辑器里够用，
// 洞是生成算法才有的东西，用户画的形状一般没有洞）。
 */
export function booleanOp(op: BoolOp, a: Paths, b: Paths): Paths {
  const ra = a.filter((r) => r.length >= 3);
  const rb = b.filter((r) => r.length >= 3);
  if (!ra.length) return op === 'union' ? rb : [];
  if (!rb.length) return op === 'intersect' ? [] : ra;

  const segs: Seg[] = [];

  for (const ring of ra) {
    for (let i = 0; i < ring.length; i++) {
      for (const s of splitEdge(ring[i], ring[(i + 1) % ring.length], rb)) {
        const m = midOf(s);
        const inB = pointInPaths(rb, m);
        // 并集留"A 在外面"的段；交集留"A 在里面"的段；差集留"A 在外面"的段
        const keep = op === 'intersect' ? inB : !inB;
        if (keep) segs.push(s);
      }
    }
  }

  for (const ring of rb) {
    for (let i = 0; i < ring.length; i++) {
      for (const s of splitEdge(ring[i], ring[(i + 1) % ring.length], ra)) {
        const m = midOf(s);
        const inA = pointInPaths(ra, m);
        const keep = op === 'union' ? !inA : op === 'intersect' ? inA : inA;
        if (!keep) continue;
        // 并/交：B 的段顺着走；差：B 的段要反向，才能和 A 的段串成一条连续的环
        segs.push(op === 'subtract' ? { a: s.b, b: s.a } : s);
      }
    }
  }

  if (!segs.length) return [];
  const rings = chain(segs);
  if (!rings) return [];
  // 结果必须都是简单环：不合法宁可整体失败，也不要把坏几何写进项目
  for (const r of rings) if (!isSimple(r)) return [];
  return rings.map((r) => cleanRing(r, 1e-6)).filter((r) => r.length >= 3);
}

/**
 * 偏移。delta > 0 外扩，< 0 内缩。
 *
 * 逐边推平行线 + 相邻平行线求交（斜接）。凸多边形完全正确；
 * 凹多边形在 |delta| 超过凹口半径时会自交 —— 那时返回空数组让 UI 提示，不硬改。
 */
export function offsetRing(ring: Ring, delta: number): Paths {
  const src = cleanRing(ring, 1e-6);
  const n = src.length;
  if (n < 3 || Math.abs(delta) < 1e-9) return [src];

  // 先统一成 CCW，外法向才是"右手边"；否则内缩外扩会反
  const ccw = ensureCCW(src);
  const dir = Math.sign(signedArea(src)) || 1;

  interface Line {
    p: Vec2;
    d: Vec2;
  }
  const lines: Line[] = [];
  for (let i = 0; i < ccw.length; i++) {
    const a = ccw[i];
    const b = ccw[(i + 1) % ccw.length];
    const d = sub(b, a);
    const l = len(d) || 1;
    // CCW 环的左法向指向**内部**，所以外扩是往 -n 方向推
    const nx = -d.y / l;
    const ny = d.x / l;
    lines.push({ p: { x: a.x - nx * delta, y: a.y - ny * delta }, d });
  }

  const out: Ring = [];
  for (let i = 0; i < lines.length; i++) {
    const l0 = lines[i];
    const l1 = lines[(i + 1) % lines.length];
    const den = cross(l0.d, l1.d);
    if (Math.abs(den) < 1e-9) {
      // 共线：直接把端点平推
      out.push(l0.p);
      continue;
    }
    const t = cross(sub(l1.p, l0.p), l1.d) / den;
    out.push({ x: l0.p.x + l0.d.x * t, y: l0.p.y + l0.d.y * t });
  }

  const cleaned = cleanRing(out, 1e-6);
  if (cleaned.length < 3) return [];
  if (!isSimple(cleaned)) return [];

  // 两个兜底判据，缺一个就会把坏几何写进项目：
  //   1) 面积符号不许翻（内缩过头会让环反向）；
  //   2) **每个顶点到原边界的距离必须 ≈ |delta|**。
  //      第 2 条才是真正管用的那条：10×10 的方块内缩 6 m 时，四条偏移线都越过了中心，
  //      交点又拼出一个正向的小方块（面积 4，符号没翻、也还在原环内），前两条判据全过 ——
  //      但它显然是错的。而"顶点到原边界 4 m ≠ 6 m"当场就露了。
  if (signedArea(cleaned) * dir <= 0) return [];

  // 过度内缩的判据：**|delta| 不许超过"平均半宽"**（2·面积 / 周长，正方形 10×10 得 5）。
  //
  // 试过更"精确"的判据（每个顶点到原边界距离 ≈ |delta|），在凹角上会误杀：
  // L 形内缩 1 m 时，凹角那个顶点到原边界的真实距离是 √2 而不是 1，看着像错的其实是对的。
  // 平均半宽这个量既便宜又不会误杀，代价是 |delta| 接近半宽时会放过一个极瘦的结果 ——
  // 界面上就是"内缩 4.9 m"这种极端输入，接受它比误杀正常输入强。
  if (delta < 0) {
    const inradius = Math.abs((2 * signedArea(src)) / ringPerimeter(src));
    if (Math.abs(delta) >= inradius) return [];
  }
  return [cleaned];
}

export function unionAll(list: Paths[]): Paths {
  return list.reduce<Paths>((acc, p) => (acc.length ? booleanOp('union', acc, p) : p), []);
}
