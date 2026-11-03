// 按建筑裁剪方案：让 Placemaker 先"不知道有楼"地把地块排满，事后再把压到楼上的部分切掉。
//
// 为什么不用 SiteSafety 那套（传 obstacles 给 Core）：它的语义是**整条丢弃**。
// 实测一块 13123 m²、4 栋楼的地块，parking 的车道从 53 条掉到 **1 条** —— 车位还在
// （183 个），但没有车道把它们连起来，整片停车场是散的。楼层挡住一条车道的一小段，
// 却赔上了整条路。
//
// 这里的做法相反：路网先生成完整，再按建筑**裁剪**而不是删除。代价是保留下来的车道
// 可能在楼边留一点点缺口，但整片路网是连通的 —— 这比"只有 1 条路"好得多。
//
// 放在 bake 阶段做（离线），运行时零成本，也不需要碰 Core 的生成逻辑。

import polygonClipping from 'polygon-clipping';

type Ring = [number, number][];

export interface ClipOptions {
  /** 采样精度：每条渠按多少段折线近似。默认 16。 */
  samples?: number;
  /** 一条渠被建筑盖住超过这个比例就整条丢。默认 0.85（只留"几乎全在楼里"的）。 */
  dropNetRatio?: number;
}

export interface ClipStats {
  surfacesIn: number;
  surfacesOut: number;
  /** 被建筑切掉面积（m²）。 */
  surfaceCutArea: number;
  objDropped: number;
  netDropped: number;
  netKept: number;
  /** 因为跨楼被切成几段而额外产生的渠条数。 */
  netSplit: number;
  /** 裁剪**之后**仍有渠压在楼上的长度（m）。正常应为 0，非 0 说明切段没切干净。 */
  netResidualLen: number;
  /** 被建筑盖住的渠长度（m）。 */
  netCutLen: number;
  /** 差集后仍与建筑重叠、被直接丢弃的铺装块数（退化几何的信号）。 */
  surfaceRejected: number;
}

export interface ClipResult<T> {
  plan: T;
  stats: ClipStats;
}

interface PlanLike {
  surfaces: { role: string; ring: Ring; [k: string]: unknown }[];
  nets: { role: string; a: Ring[number]; b: Ring[number]; c: Ring[number]; d: Ring[number]; [k: string]: unknown }[];
  objects: { role: string; x: number; z: number; [k: string]: unknown }[];
  [k: string]: unknown;
}

function ringArea(r: Ring): number {
  if (!r || r.length < 3) return 0;
  let s = 0;
  for (let i = 0, j = r.length - 1; i < r.length; j = i++) s += r[j][0] * r[i][1] - r[i][0] * r[j][1];
  return Math.abs(s) / 2;
}

/** 两块多边形的相交面积（m²）。退化输入按不重叠处理。 */
function overlapArea(a: Ring, b: Ring): number {
  try {
    const out = polygonClipping.intersection([a] as never, [b] as never) as unknown as Ring[][];
    let s = 0;
    for (const poly of out) for (const r of poly) s += ringArea(r);
    return s;
  } catch {
    return 0;
  }
}

/** 线段严格相交（端点接触不算）。 */
function segCross(a1: Ring[number], a2: Ring[number], b1: Ring[number], b2: Ring[number]): boolean {
  const d = (o: Ring[number], a: Ring[number], b: Ring[number]) =>
    (a[0] - o[0]) * (b[1] - o[1]) - (a[1] - o[1]) * (b[0] - o[0]);
  const d1 = d(b1, b2, a1), d2 = d(b1, b2, a2), d3 = d(a1, a2, b1), d4 = d(a1, a2, b2);
  return ((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0));
}

/**
 * 与桥接侧 `simpleRing` 同源的判定：任意三点不共线、任意非相邻边不相交。
 *
 * 裁剪出的碎块偶尔会在接缝处自交（十字切的边界贴合得不干净）。这种环桥接会直接拒收，
 * 与其让它到下游变成"莫名少了几块铺装"，不如在这里就筛掉并计数。
 */
function isSimpleRing(r: Ring): boolean {
  const n = r.length;
  if (n < 3) return false;
  for (let i = 0; i < n; i++) {
    const a = r[i], b = r[(i + 1) % n], c = r[(i + 2) % n];
    if (Math.abs((b[0] - a[0]) * (c[1] - a[1]) - (b[1] - a[1]) * (c[0] - a[0])) < 1e-9) return false;
  }
  for (let i = 0; i < n; i++) {
    for (let j = i + 2; j < n; j++) {
      if (i === 0 && j === n - 1) continue;
      if (segCross(r[i], r[(i + 1) % n], r[j], r[(j + 1) % n])) return false;
    }
  }
  return true;
}

/**
 * 去掉共线的中间点。
 *
 * 必须做：桥接的 `simpleRing` 判定"任意三点共线即退化"，而网格切分/差集切出来的
 * 矩形边界上到处都是共线点（一条直边被切成好几段）。不清理的话，这些面会被整块丢掉，
 * 表现就是"裁剪之后铺装莫名其妙少了几块"。
 *
 * 顺带把顶点数压下来，earcut 也轻松些。
 */
function dropCollinear(r: Ring, eps = 1e-9): Ring {
  // polygon-clipping 返回的环是**首尾闭合**的（[p0…pn,p0]）。必须先去掉重复的末点，
  // 否则首尾这两个同位置点会让它们各自的两条相邻边都判成"共线"，一次删掉两个，
  // 矩形直接变成三角形、面积凭空少一半。
  let cur = r.slice();
  if (cur.length >= 2) {
    const f = cur[0], l = cur[cur.length - 1];
    if (Math.abs(f[0] - l[0]) < 1e-9 && Math.abs(f[1] - l[1]) < 1e-9) cur.pop();
  }
  if (cur.length <= 3) return cur;

  let changed = true;
  while (changed && cur.length > 3) {
    changed = false;
    const next: Ring = [];
    for (let i = 0; i < cur.length; i++) {
      const a = cur[(i - 1 + cur.length) % cur.length];
      const b = cur[i];
      const c = cur[(i + 1) % cur.length];
      const cross = (b[0] - a[0]) * (c[1] - a[1]) - (b[1] - a[1]) * (c[0] - a[0]);
      if (Math.abs(cross) <= eps) { changed = true; continue; }
      next.push(b);
    }
    if (next.length < 3) break;
    cur = next;
  }
  return cur;
}

/**
 * 规范化：用 union 把自交的环解开成若干简单多边形。
 *
 * 差集/十字切的边界上偶尔会出现自交（两个块的接缝处来回重叠一次），
 * 桥接侧 `simpleRing` 会直接拒收（dropped++），表现是"怎么少了几块铺装"。
 * `union([r])` 本身就是"解自交"操作，返回的每一块都保证简单。
 */
function normalizeRing(r: Ring): Ring[] {
  try {
    const out = polygonClipping.union([r] as never) as unknown as Ring[][];
    const res: Ring[] = [];
    for (const poly of out) {
      if (poly.length >= 1 && poly[0].length >= 3) res.push(poly[0]);
    }
    return res.length > 0 ? res : [r];
  } catch {
    return [r];
  }
}

/**
 * 从环里减掉若干障碍，返回**无洞**的若干块。
 *
 * 难点：`difference` 对"中间压一栋楼"给的是回字形（外环 + 洞），而 SurfaceFeature 只认单环。
 * 取外环等于没减，取内环就把整块丢了。所以碰到带洞的结果，用**洞的包围盒**把原环切成
 * 上下左右四块再各自减一次 —— 每块都是简单多边形。
 */
function subtractRings(ring: Ring, quads: Ring[], depth = 2): Ring[] {
  let polys: Ring[][];
  try {
    polys = polygonClipping.difference([ring] as never, ...(quads.map((q) => [q]) as never[])) as unknown as Ring[][];
  } catch {
    return [ring];
  }

  const out: Ring[] = [];
  for (const poly of polys) {
    if (poly.length <= 1) {
      if (poly.length === 1 && poly[0].length >= 3) out.push(poly[0]);
      continue;
    }
    if (depth <= 0) continue;   // 切到底还带洞：这块放弃（宁可少一块铺装，也不要压楼）

    // 洞的包围盒 → 把原环的包围盒切成十字形的四块
    let hx0 = Infinity, hx1 = -Infinity, hy0 = Infinity, hy1 = -Infinity;
    for (let i = 1; i < poly.length; i++) {
      for (const p of poly[i]) {
        hx0 = Math.min(hx0, p[0]); hx1 = Math.max(hx1, p[0]);
        hy0 = Math.min(hy0, p[1]); hy1 = Math.max(hy1, p[1]);
      }
    }
    const xs = ring.map((p) => p[0]), ys = ring.map((p) => p[1]);
    const x0 = Math.min(...xs), x1 = Math.max(...xs);
    const y0 = Math.min(...ys), y1 = Math.max(...ys);
    const cells: Ring[] = [
      [[x0, y0], [hx0, y0], [hx0, y1], [x0, y1]],          // 左
      [[hx1, y0], [x1, y0], [x1, y1], [hx1, y1]],          // 右
      [[hx0, y0], [hx1, y0], [hx1, hy0], [hx0, hy0]],      // 下
      [[hx0, hy1], [hx1, hy1], [hx1, y1], [hx0, y1]],      // 上
    ];
    for (const cell of cells) {
      if (ringArea(cell) < 0.5) continue;
      let clipped: Ring[];
      try {
        const inter = polygonClipping.intersection([cell] as never, [ring] as never) as unknown as Ring[][];
        clipped = [];
        for (const p of inter) if (p.length >= 1 && p[0].length >= 3) clipped.push(p[0]);
      } catch {
        continue;
      }
      for (const c of clipped) out.push(...subtractRings(c, quads, depth - 1));
    }
  }
  return out;
}

/** 射线法：点是否在环内。 */
function inside(p: [number, number], ring: Ring): boolean {
  let hit = false;
  for (let i = 0, j = ring.length - 1; i < ring.length; j = i++) {
    const a = ring[i], b = ring[j];
    if ((a[1] > p[1]) !== (b[1] > p[1]) && p[0] < ((b[0] - a[0]) * (p[1] - a[1])) / (b[1] - a[1]) + a[0]) hit = !hit;
  }
  return hit;
}

/** 三次贝塞尔采样成折线。 */
function sampleBezier(a: Ring[number], b: Ring[number], c: Ring[number], d: Ring[number], n: number): [number, number][] {
  const pts: [number, number][] = [];
  for (let i = 0; i <= n; i++) {
    const t = i / n, u = 1 - t;
    pts.push([
      u * u * u * a[0] + 3 * u * u * t * b[0] + 3 * u * t * t * c[0] + t * t * t * d[0],
      u * u * u * a[1] + 3 * u * u * t * b[1] + 3 * u * t * t * c[1] + t * t * t * d[1],
    ]);
  }
  return pts;
}

/**
 * 裁剪一份 Plan，使结果不含任何压到建筑上的东西。
 *
 * 原地块形状（lotRing）不在这个函数里 —— 它只处理"建筑"这一类障碍。
 */
export function clipPlanToBuildings<T extends PlanLike>(
  plan: T,
  quadRings: Ring[],
  opts: ClipOptions = {},
): ClipResult<T> {
  const samples = opts.samples ?? 16;
  const dropRatio = opts.dropNetRatio ?? 0.85;
  const quads = quadRings.filter((q) => q && q.length >= 3);

  const stats: ClipStats = {
    surfacesIn: plan.surfaces.length,
    surfacesOut: 0,
    surfaceCutArea: 0,
    objDropped: 0,
    netDropped: 0,
    netKept: 0,
    netSplit: 0,
    netResidualLen: 0,
    netCutLen: 0,
    surfaceRejected: 0,
  };

  if (quads.length === 0) {
    stats.surfacesOut = plan.surfaces.length;
    stats.netKept = plan.nets.length;
    return { plan, stats };
  }

  // ---- 铺装：多边形差集（带洞的结果会被十字切开，见 subtractRings）
  const surfaces: T['surfaces'] = [];
  for (const s of plan.surfaces) {
    const before = ringArea(s.ring);
    const pieces = subtractRings(s.ring, quads).flatMap(normalizeRing).map((r) => dropCollinear(r));
    let kept = 0;
    for (const r of pieces) {
      const a = ringArea(r);
      // 碎块门槛 2 m²：差集在楼角/窄缝处会留下零点几平米的小片，
      // 桥接侧按退化环丢掉（dropped++），留着只会制造噪声。
      if (a < 2) { stats.surfaceRejected++; continue; }
      // 与桥接同源的严格判定：不自交、无共线三点。过不了的一律不留 ——
      // 让它在源头被筛掉，好过到下游变成"莫名少了几块铺装"。
      if (!isSimpleRing(r)) { stats.surfaceRejected++; continue; }
      // 双保险：差集偶尔对退化环无效（自交、极窄条），再验一次。
      // 宁可少一块铺装，也不要让地面压进楼里 —— 那种错误在 3D 里是穿模。
      if (quads.some((q) => overlapArea(r, q) > 0.5)) { stats.surfaceRejected++; continue; }
      surfaces.push({ ...s, ring: r });
      kept += a;
    }
    stats.surfaceCutArea += Math.max(0, before - kept);
  }
  stats.surfacesOut = surfaces.length;

  // ---- 对象：落在建筑里的丢掉（楼里的灯、树、车位都没意义）
  const objects = plan.objects.filter((o) => {
    const p: [number, number] = [o.x, o.z];
    const hit = quads.some((q) => inside(p, q));
    if (hit) stats.objDropped++;
    return !hit;
  });

  // ---- 渠：按折线找出"在楼外"的连续段，**逐段保留**（整条丢或整条留都不对）。
  //      整条留会穿模，整条丢就退化成 SiteSafety 的老毛病（53 条车道只剩 1 条）。
  //      子段重新拟合成三次贝塞尔：车道本来就以直线/缓弯为主，用直线拟足够。
  const nets: T['nets'] = [];
  for (const n of plan.nets) {
    const pts = sampleBezier(n.a, n.b, n.c, n.d, samples);
    let total = 0, hidden = 0;
    const runs: [number, number][][] = [];
    let cur: [number, number][] = [];
    for (let i = 1; i < pts.length; i++) {
      const len = Math.hypot(pts[i][0] - pts[i - 1][0], pts[i][1] - pts[i - 1][1]);
      total += len;
      const p0 = pts[i - 1], p1 = pts[i];
      // 必须**两端都在楼外**才算可保留：只看中点的话，段尾已经伸进楼里了
      // （mid 恰好在边界外一点，但 p1 已在楼内），切出来的段会带一截穿墙。
      const dirty = quads.some((q) => inside(p0, q) || inside(p1, q));
      if (dirty) {
        hidden += len;
        if (cur.length >= 2) runs.push(cur);
        cur = [];
      } else {
        if (cur.length === 0) cur.push(p0);
        cur.push(p1);
      }
    }
    if (cur.length >= 2) runs.push(cur);
    stats.netCutLen += hidden;

    // 整条几乎全在楼里：不用留
    if (total > 0 && hidden / total > dropRatio) { stats.netDropped++; continue; }
    // 完全没被盖：原样保留（不改变几何，保证绝大多数渠是可比的）
    if (hidden < 0.01) {
      nets.push(n);
      stats.netKept++;
      continue;
    }

    let pieces = 0;
    for (const run of runs) {
      const a0 = run[0], d0 = run[run.length - 1];
      const len0 = Math.hypot(d0[0] - a0[0], d0[1] - a0[1]);
      if (len0 < 1.5) continue;   // 楼边上的碎屑
      // 两端各内缩 0.25 m：段端点恰好贴着楼边时，"点在多边形内"的射线判定会抖，
      // 采样出来的残留会被误报成穿模。留一点余量，让车道离墙也有个视觉间距。
      const ux = (d0[0] - a0[0]) / len0;
      const uy = (d0[1] - a0[1]) / len0;
      const pad = 0.25;
      const a: [number, number] = [a0[0] + ux * pad, a0[1] + uy * pad];
      const d: [number, number] = [d0[0] - ux * pad, d0[1] - uy * pad];
      nets.push({
        ...n,
        a,
        b: [a[0] + (d[0] - a[0]) / 3, a[1] + (d[1] - a[1]) / 3],
        c: [a[0] + ((d[0] - a[0]) * 2) / 3, a[1] + ((d[1] - a[1]) * 2) / 3],
        d,
      });
      pieces++;
    }
    if (pieces === 0) stats.netDropped++;
    else { stats.netKept++; stats.netSplit += pieces; }
  }

  // 收尾自查：裁完还有多少渠压着楼。非 0 就是切段逻辑有漏洞，不该悄悄放过。
  for (const n of nets) {
    const pts = sampleBezier(n.a, n.b, n.c, n.d, samples);
    for (let i = 1; i < pts.length; i++) {
      const mid: [number, number] = [(pts[i][0] + pts[i - 1][0]) / 2, (pts[i][1] + pts[i - 1][1]) / 2];
      if (quads.some((q) => inside(mid, q))) {
        stats.netResidualLen += Math.hypot(pts[i][0] - pts[i - 1][0], pts[i][1] - pts[i - 1][1]);
      }
    }
  }

  return { plan: { ...plan, surfaces, objects, nets }, stats };
}
