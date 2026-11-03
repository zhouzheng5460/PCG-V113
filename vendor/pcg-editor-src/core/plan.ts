/**
 * 规划层 = PRD §84 里的 PCG Planner。
 *
 * 为什么单独一层：`pcg.ts` 原来只回答"楼摆哪儿"，于是内部道路只能当障碍物避让、
 * 地面只是地块的一块底色。而 PRD §85 的 Phase 5 要的是五类产出
 * （Building Group / Parking / Ground / Landscape + 入口道路），§89 更把它写成
 * Demo 的验收底线。要同时安放这几类东西，必须有一个共享的中间结果：
 * 谁是路、谁是铺装、谁必须留白。
 *
 * 这一层只算几何与用地，不碰 store / three / React，也不写字号（Id）——
 * 对象化交给 store，才能保证"生成一次 = 一步撤销"。
 *
 * 顺序很重要：**先规划路，再落建筑**（`planAccessRoad` 在落位前调用），
 * 这样建筑天然会避让道路，而不是先盖楼再抠路。支路要等建筑落位后才知道连到哪。
 */
import { intersect, offsetPath, subtract, unionAll } from './boolean';
import { GroundSurface, LandUse, RoadClass } from './model';
import {
  Paths,
  boundsOfPaths,
  closestPointOnSegment,
  pathsArea,
  pointInPaths,
  polylineLength,
  ringCentroid,
  strokeEnvelope,
} from './polygon';
import { Vec2, add, dist, dot, len, mul, normalize, perp, sub, v2 } from './vec';

/* ------------------------------- 参数表 ------------------------------- */

/** 道路规划参数：改这里就能整体调整路网形态 */
export const ROAD_PLAN = {
  /** 进场主路宽（单车道 + 两侧让行） */
  accessWidth: 7,
  /** 支路宽 */
  spurWidth: 5,
  /** 绕行步长 */
  step: 8,
  /** 主路最长 */
  maxLen: 90,
  /** 支路沿主路的站距 */
  branchSpacing: 24,
  /**
   * 支路单侧最长延伸。
   * 只做"楼前引道"而不是横贯场地：开一条 40 m 的长路会把 20 多米深的场带
   * 从中劈成两条 8 m 的废条（两条都放不下楼），建筑量反而掉下去。
   */
  branchMax: 20,
  /** 支路短于这个就别开（开了也是个死胡同） */
  branchMin: 7,
  /** 支路末端离建筑基底的净距 */
  branchGap: 2,
} as const;

/** 候选项的采样间距（m）：3 m 够密，能撞上大部分真实空隙 */
const BRANCH_PITCH = 3;
/** 一个地块最多开几条支路：太多就把场地切碎了 */
const MAX_BRANCHES = 6;

/**
 * 建筑周边硬质铺装的宽度 */export const PAVING_PAD: Record<LandUse, number> = {
  industrial: 6,
  urban: 4,
  rural: 3,
  construction: 4,
  park: 2.5,
  civic: 5,
};

/** 停车配比：车位 / 100㎡ 建筑面积 */const PARKING_RATIO: Record<LandUse, number> = {
  industrial: 0.35,
  urban: 0.7,
  rural: 0.2,
  construction: 0.15,
  park: 0.5,
  civic: 1.0,
};

/** 单个车位含通道的占用面积（㎡）：2.5 × 5 车位 + 均摊通道 */
const PARKING_PER_SPACE = 25;

/* ------------------------------- 出入口 ------------------------------- */

export interface EntryPoint {
  /** 地块内的起步点 */
  pos: Vec2;
  /** 进入地块的方向（单位向量） */
  inward: Vec2;
  /** 开口宽度 */
  width: number;
}

/**
 * 从地块上的门推出"从哪进、朝哪走"。
 *
 * 不用 gate.dir 当进入方向：门画在围墙上时，dir 常被标成"沿墙"或"朝外"，
 * 用它开路会直接开出地块。这里改用几何事实——门在哪儿，就往地块重心走。
 */
export function pickEntryPoint(
  boundary: Paths,
  gates: { pos: Vec2; width: number }[],
): EntryPoint | null {
  if (!boundary.length) return null;
  const center = ringCentroid(boundary[0]);

  let best: EntryPoint | null = null;
  let bestDist = Infinity;

  for (const g of gates) {
    // 门在外侧时先投影到地块边界上
    let pos = g.pos;
    if (!pointInPaths(pos, boundary)) {
      const near = closestEdgeOf(pos, boundary);
      if (near) pos = near;
    }
    let inward = normalize(sub(center, pos));
    if (len(sub(center, pos)) < 1e-6) inward = v2(0, 1);
    // 离重心越近越优先（主入口通常正对主体）
    const d = dist(pos, center);
    if (d < bestDist) {
      bestDist = d;
      best = { pos, inward, width: Math.max(4, g.width) };
    }
  }
  if (best) return best;

  // 没有门：用地块最长边的中点当入口，朝内法向进入（降级方案，不能崩）
  let edge: { a: Vec2; b: Vec2; len: number } | null = null;
  for (const ring of boundary) {
    for (let i = 0; i < ring.length; i++) {
      const a = ring[i];
      const b2 = ring[(i + 1) % ring.length];
      const l = dist(a, b2);
      if (!edge || l > edge.len) edge = { a, b: b2, len: l };
    }
  }
  if (!edge) return null;
  const mid = { x: (edge.a.x + edge.b.x) / 2, y: (edge.a.y + edge.b.y) / 2 };
  const along = normalize(sub(edge.b, edge.a));
  // 取指向重心的一侧
  let inward = perp(along);
  if (dot(inward, sub(center, mid)) < 0) inward = mul(inward, -1);
  return { pos: mid, inward, width: 8 };
}

function closestEdgeOf(p: Vec2, paths: Paths): Vec2 | null {
  let best: { point: Vec2; dist: number } | null = null;
  for (const ring of paths) {
    for (let i = 0; i < ring.length; i++) {
      const a = ring[i];
      const b = ring[(i + 1) % ring.length];
      const c = closestPointOnSegment(p, a, b);
      if (!best || c.dist < best.dist) best = { point: c.point, dist: c.dist };
    }
  }
  return best?.point ?? null;
}

/* ------------------------------- 主路 ------------------------------- */

/** 点到线段的距离 */
function distToSegment(p: Vec2, a: Vec2, b: Vec2): number {
  return closestPointOnSegment(p, a, b).dist;
}

/**
 * Douglas–Peucker：把网格 BFS 出来的阶梯折线压成几条长直段。
 * 容差按米给，2 m 在园区尺度上刚好——再松就会把绕楼的弯给抹平（压回楼里）。
 */
function dpSimplify(pts: Vec2[], tol = 2): Vec2[] {
  if (pts.length <= 2) return pts.slice();
  const a = pts[0];
  const b = pts[pts.length - 1];
  let maxD = 0;
  let idx = 0;
  for (let i = 1; i < pts.length - 1; i++) {
    const d = distToSegment(pts[i], a, b);
    if (d > maxD) {
      maxD = d;
      idx = i;
    }
  }
  if (maxD > tol) {
    const left = dpSimplify(pts.slice(0, idx + 1), tol);
    const right = dpSimplify(pts.slice(idx), tol);
    return [...left.slice(0, -1), ...right];
  }
  return [a, b];
}

/**
 * 从 `from` 沿 `dir` 一直走，返回还留在 `area` 里的最后一个点。
 * 用来给主路定一个"要去的对面"，不判障碍——障碍交给 BFS 绕。
 */
function farEdgeAlong(area: Paths, from: Vec2, dir: Vec2, maxDist: number): Vec2 {
  let last = from;
  let inside = false;
  for (let d = 1; d <= maxDist; d += 1) {
    const p = add(from, mul(dir, d));
    if (pointInPaths(p, area)) {
      inside = true;
      last = p;
    } else if (inside) {
      // 已经进到场里了，再出去就是边界
      break;
    }
    /*
     * 还没进去之前不要停：入口常常画在退线带里甚至地块外（门在围墙上），
     * 头几米本来就在 area 之外，一遇到外面就 break 会让目标点退化成起点，
     * 主路于是直接判为"太短"而不生成。
     */
  }
  return last;
}

/**
 * 从入口往地块里"走"出一条路。
 *
 * 做法是网格 BFS 而不是贪心：
 * 早先用"直行 + 撞墙转 45°"的贪心实现，在没有目标点时它就是在做随机游走——
 * 示例项目里那条路一路蹭到地块西南角再折回来，完全不像进场道路。
 * 改成"在可通行网格上从入口做 BFS，取最远的可达点，回溯得到路径"之后，
 * 路自带目标（走到地块里最深处），绕行也有意义，而且天然贴着障碍走。
 */
export function traceServiceRoad(
  walkable: Paths,
  blocked: Paths,
  start: Vec2,
  inward: Vec2,
  maxLen: number = ROAD_PLAN.maxLen,
  step: number = ROAD_PLAN.step,
): Vec2[] {
  if (!walkable.length) return [];
  const b = boundsOfPaths(walkable);

  /* 1. 采样可通行网格 */
  const passable = (p: Vec2) =>
    pointInPaths(p, walkable) && !(blocked.length && pointInPaths(p, blocked));

  const gx0 = Math.floor(b.minX / step) * step;
  const gy0 = Math.floor(b.minY / step) * step;
  const cells = new Map<string, Vec2>();
  const key = (i: number, j: number) => `${i},${j}`;

  for (let x = gx0; x <= b.maxX; x += step) {
    for (let y = gy0; y <= b.maxY; y += step) {
      const p = v2(x, y);
      if (passable(p)) cells.set(key(Math.round(x / step), Math.round(y / step)), p);
    }
  }
  if (!cells.size) return [];

  /* 2. 起点吸附到最近的可通行格点 */
  let startKey: string | null = null;
  let startDist = Infinity;
  for (const [k, p] of cells) {
    const d = dist(p, start);
    if (d < startDist) {
      startDist = d;
      startKey = k;
    }
  }
  if (!startKey || startDist > step * 2.5) return [];

  /* 3. BFS（8 邻域，斜边记 √2 倍长度） */
  const offset = normalize(inward);
  /*
   * 终点的取法：从入口沿进入方向一直往前，走到离开可建范围为止，那个点就是目标点。
   * 然后"取离目标点最近的可达格"。
   *
   * 为什么不用打分（最远 / 最靠里 / 离重心最近都试过）：
   *   - 欧氏最远 → 一定是对角，路绕场一周；
   *   - 最靠里 → 为了多往北挪 10 m 会贴到地界上跑（示例项目里绕出 109 m 斜穿长路）；
   *   - 离重心最近 → 门口就停（重心常常离门不远）。
   * "朝对边开"没有这些歧义：主路的语义就是"从入口穿到场地的另一头"，
   * 障碍挡路就绕，绕完继续朝目标点靠。
   */
  const target = farEdgeAlong(walkable, start, offset, maxLen * 2);

  const distMap = new Map<string, number>([[startKey, 0]]);
  const prevMap = new Map<string, string>();
  const queue: string[] = [startKey];
  const neighbors = [
    [1, 0],
    [-1, 0],
    [0, 1],
    [0, -1],
    [1, 1],
    [1, -1],
    [-1, 1],
    [-1, -1],
  ];

  let farKey = startKey;
  let farDist = 0;
  let farScore = -Infinity;

  for (let head = 0; head < queue.length; head++) {
    const cur = queue[head];
    const curP = cells.get(cur)!;
    const curD = distMap.get(cur)!;
    if (curD <= maxLen) {
      // 离目标点越近越好；同样近时取路径更短的（少绕弯）
      const score = -dist(curP, target) - curD * 1e-3;
      if (score > farScore) {
        farScore = score;
        farKey = cur;
        farDist = curD;
      }
    }
    if (curD > maxLen) continue;

    const [ci, cj] = cur.split(',').map(Number);
    for (const [di, dj] of neighbors) {
      const nk = key(ci + di, cj + dj);
      const np = cells.get(nk);
      if (!np || distMap.has(nk)) continue;
      const seg = dist(curP, np);
      if (seg > step * 1.5) continue;
      // 斜向要防"穿过两格之间的墙角"
      if (di && dj && (!cells.has(key(ci + di, cj)) || !cells.has(key(ci, cj + dj)))) continue;
      // 朝入口反方向走没有意义（会给出一条先退出去的怪路）
      if (curD === 0 && dot(normalize(sub(np, curP)), offset) <= 0 && dist(np, start) < step * 0.5) continue;
      distMap.set(nk, curD + seg);
      prevMap.set(nk, cur);
      queue.push(nk);
    }
  }

  if (farDist < ROAD_PLAN.accessWidth * 1.5) return [];

  /* 4. 回溯 → 简化 */
  const chain: Vec2[] = [];
  let cursor: string | undefined = farKey;
  let guard = 0;
  while (cursor && guard++ < 5000) {
    chain.push(cells.get(cursor)!);
    cursor = prevMap.get(cursor);
  }
  chain.reverse();
  // 真实起点（门的位置可能不在格点上）接在链首
  chain.unshift(start);
  const simplified = dpSimplify(chain, Math.max(1.5, step * 0.28));
  return orthogonalize(simplified, passable, offset);
}

/**
 * 把斜段掰成"横 + 竖"。
 *
 * 网格 BFS 走出来的斜线是 8 邻域的产物，DP 简化之后会留成一条 45° 斜路——
 * 总图上的内部道路基本是横平竖直的，斜穿看着就像"随便连了一条"。
 * 做法：对每段斜线试两种 L 形走法，挑第一个整段都可通行的；
 * 都不通行就保留斜线（宁可难看也不能穿墙）。
 * 两种走法谁先试由 `inward` 决定：先朝里走，别一进门就沿着门口那条外部道路铺 24 m。
 */
function orthogonalize(line: Vec2[], passable: (p: Vec2) => boolean, inward: Vec2): Vec2[] {
  if (line.length < 2) return line;
  const aheadFirst = Math.abs(inward.y) >= Math.abs(inward.x);
  const out: Vec2[] = [line[0]];
  for (let i = 1; i < line.length; i++) {
    const a = out[out.length - 1];
    const b = line[i];
    const dx = Math.abs(b.x - a.x);
    const dy = Math.abs(b.y - a.y);
    if (dx < 1.5 || dy < 1.5) {
      out.push(b);
      continue;
    }
    const horizontal = v2(b.x, a.y);
    const vertical = v2(a.x, b.y);
    const corners = aheadFirst ? [vertical, horizontal] : [horizontal, vertical];
    const ok = corners.find((c) => legClear(a, c, passable) && legClear(c, b, passable));
    if (ok) out.push(ok, b);
    else out.push(b);
  }
  return out;
}

/** 沿直线每 2 m 采样一次，全段可通行才算通 */
function legClear(a: Vec2, b: Vec2, passable: (p: Vec2) => boolean): boolean {
  const l = dist(a, b);
  const n = Math.max(1, Math.ceil(l / 2));
  for (let i = 1; i <= n; i++) {
    const t = i / n;
    if (!passable(v2(a.x + (b.x - a.x) * t, a.y + (b.y - a.y) * t))) return false;
  }
  return true;
}

export interface PlannedRoad {
  centerline: Vec2[];
  width: number;
  roadClass: RoadClass;
  name: string;
}

/** 入口主路：入口点 + 绕行出来的中心线 */
export function planAccessRoad(
  boundary: Paths,
  buildable: Paths,
  blocked: Paths,
  gates: { pos: Vec2; width: number }[],
): { entry: EntryPoint; road: PlannedRoad } | null {
  const entry = pickEntryPoint(boundary, gates);
  if (!entry) return null;
  // 障碍先膨胀半个路宽：只判中心线的话，路边缘会啃进建筑 3.5 m
  const clearance = ROAD_PLAN.accessWidth / 2 + 0.5;
  const wide = blocked.length ? offsetPath(blocked, clearance) : [];
  /*
   * 主路长度上限按地块尺寸给，不写死 90 m。
   * 写死之后在 144×94 的地块上会出怪事：BFS 只能走到第 75 m，
   * 于是"最靠里"的落点退化成东侧那条 12 m 夹缝的尽头，
   * 主路就成了穿楼缝的小巷，而不是绕场一圈的干道。
   */
  const span = Math.max(boundsOfPaths(buildable).maxX - boundsOfPaths(buildable).minX,
    boundsOfPaths(buildable).maxY - boundsOfPaths(buildable).minY);
  const maxLen = Math.max(ROAD_PLAN.maxLen, span * 1.6);
  const centerline = traceServiceRoad(buildable, wide, entry.pos, entry.inward, maxLen);
  if (centerline.length < 2) return null;
  if (polylineLength(centerline) < ROAD_PLAN.accessWidth * 1.5) return null;
  return {
    entry,
    road: { centerline, width: ROAD_PLAN.accessWidth, roadClass: 'service', name: '内部主路' },
  };
}

/** 路带（面积形式），供避让与铺装扣减 */
export function roadEnvelope(road: PlannedRoad): Paths {
  const env = strokeEnvelope(road.centerline, road.width / 2);
  return env.length >= 3 ? [env] : [];
}

/* ------------------------------- 支路 ------------------------------- */

/** 点到折线的最近距离 */
function distToPolyline(p: Vec2, line: Vec2[]): number {
  let best = Infinity;
  for (let i = 0; i < line.length - 1; i++) {
    const d = closestPointOnSegment(p, line[i], line[i + 1]).dist;
    if (d < best) best = d;
  }
  return best;
}

/**
 * 从主路拉出来的支路网。
 *
 * 为什么不再"每栋楼接一条"：早先按「楼最近顶点 → 主路最近点」直连，
 * 斜直线十有八九要跨过别的楼，一跨就整条否决，示例项目里 5 栋楼最后只剩 1 条支路，
 * 另外 4 栋全靠绿地"间接可达"——这就是 PRD 说的"路只是被避让，从没被连上"。
 * 改成沿主路往两侧开路、遇楼即停：走向永远垂直主路（工业园区的真实形态），
 * 一定不会穿楼，楼与楼的间隙自然就成了楼前场。
 *
 * 站点的取法也不是固定站距：场地的空隙是随机分布的，按 24 m 等分去撞，
 * 撞上的位置经常只差 5 m 就完全错开（示例项目里正好错过唯一那段 20 m 的空隙）。
 * 所以做法是**每 3 m 试一次**，把所有可行的候选按长度从长到短排序，
 * 贪心保留——顺带满足"同侧支路至少间隔 spacing"，避免开出一排平行小路。
 */
export function planBranches(
  spine: Vec2[],
  width: number,
  area: Paths,
  walls: Paths,
): PlannedRoad[] {
  if (spine.length < 2 || !area.length) return [];
  const total = polylineLength(spine);
  if (total < ROAD_PLAN.branchMin + 2) return [];

  // 主路自己也算路面：支路末端如果离主路还是几米，那说明这条"支路"
  // 其实是在主路旁边并排铺了一条（主路在那一小段正好也朝同方向走）
  const nearSpine = (p: Vec2) => distToPolyline(p, spine) < ROAD_PLAN.branchMin * 0.6;

  const blocked = walls.length ? offsetPath(walls, width / 2 + ROAD_PLAN.branchGap) : [];
  const clear = (p: Vec2) => pointInPaths(p, area) && !(blocked.length && pointInPaths(p, blocked));
  const half = width / 2;

  interface Cand {
    s: number;
    side: 1 | -1;
    at: Vec2;
    normal: Vec2;
    len: number;
  }
  const cands: Cand[] = [];

  for (let s = 2; s <= total - 2; s += BRANCH_PITCH) {
    const at = pointAtDistance(spine, s);
    const dir = directionAt(spine, s);
    if (!dir) continue;
    const n = perp(dir);
    for (const side of [1, -1] as const) {
      const normal = mul(n, side);
      let len = 0;
      for (let d = 1; d <= ROAD_PLAN.branchMax; d += 1) {
        const c = add(at, mul(normal, d));
        // 中线与两侧路缘都要落在可建范围内，否则路带会啃进退线带
        if (!clear(c) || !clear(add(c, mul(dir, half))) || !clear(add(c, mul(dir, -half)))) break;
        len = d;
      }
      if (len < ROAD_PLAN.branchMin) continue;
      if (nearSpine(add(at, mul(normal, len)))) continue;
      cands.push({ s, side, at, normal, len });
    }
  }
  if (!cands.length) return [];

  cands.sort((a, b) => b.len - a.len);
  const kept: Cand[] = [];
  for (const c of cands) {
    /*
     * 同侧要隔足 spacing，异侧只要隔半个 —— 否则会在主路的同一小段上
     * 左右各开一条，两条平行小路相距 6 m，纯粹是把场地切碎。
     */
    const tooClose = kept.some((k) =>
      Math.abs(k.s - c.s) < (k.side === c.side ? ROAD_PLAN.branchSpacing : ROAD_PLAN.branchSpacing * 0.5),
    );
    if (tooClose) continue;
    kept.push(c);
    if (kept.length >= MAX_BRANCHES) break;
  }

  kept.sort((a, b) => a.s - b.s);
  return kept.map((c) => ({
    centerline: [c.at, add(c.at, mul(c.normal, c.len))],
    width,
    roadClass: 'service' as RoadClass,
    name: '支路',
  }));
}

/* ------------------------------- 地面 ------------------------------- */

export interface PlannedGround {
  boundary: Paths;
  surface: GroundSurface;
  name: string;
}

export interface PlanInput {
  /** 地块边界（含退线带） */
  boundary: Paths;
  /** 退线后可建范围 */
  buildable: Paths;
  /** 净地 = 可建范围 − 障碍物 */
  free: Paths;
  /** 禁建区等不参与地面分区的区域 */
  excluded?: Paths;
  /** 全部建筑基底（既有固定 + 本次生成） */
  buildings: Paths[];
  /** 入口主路（可为 null：没有门也没找到入口） */
  accessRoad: PlannedRoad | null;
  /**
   * 已经在"落建筑之前"规划好的支路。
   * 为什么允许外部传进来：支路必须是**先于建筑**定的（否则只能塞进楼缝），
   * 而这一步发生在 `pcg.ts` 的 2.5 阶段，此时还没有最终的分区输入。
   * 不传就自己算（单元测试与"只做分区"的场景够用了）。
   */
  branches?: PlannedRoad[];
  landUse: LandUse;
}

export interface SurfacePlan {
  roads: PlannedRoad[];
  grounds: PlannedGround[];
  stats: {
    siteArea: number;
    buildingArea: number;
    roadArea: number;
    pavingArea: number;
    lawnArea: number;
    parkingArea: number;
    parkingSpaces: number;
    /** 禁建区等不参与计量的面积 */
    excludedArea: number;
  };
}

/**
 * 地面分区：铺装贴着建筑，停车贴着路，剩下的全归绿地。
 *
 * 一条硬规则：地面对象之间**不允许重叠**。
 * 三个面叠在一起不只是 z-fighting，用地平衡统计也会重复计数。
 * 所以顺序是「先排停车 → 再排铺装（扣掉停车）→ 绿地兜底（扣掉前面所有）」。
 */
export function planSurfaces(input: PlanInput): SurfacePlan {
  const { boundary, free, buildings, landUse } = input;
  const siteArea = Math.abs(pathsArea(boundary));
  const bUnion = buildings.length ? unionAll(buildings) : [];

  const roads: PlannedRoad[] = [];
  if (input.accessRoad) roads.push(input.accessRoad);

  /*
   * 支路：优先用外部传进来的（那是在落建筑之前就定好的，建筑已经避让过它）。
   * 没传就现算——但要注意此时的 `buildings` 已经是最终结果，
   * 现算的支路只能塞进楼缝，正是我们要避免的退化情况，所以只在单测里用。
   */
  if (input.branches) {
    roads.push(...input.branches);
  } else if (input.accessRoad) {
    const area = input.buildable.length ? input.buildable : free;
    roads.push(...planBranches(input.accessRoad.centerline, ROAD_PLAN.spurWidth, area, bUnion));
  }

  const roadUnion = roads.length ? unionAll(roads.map(roadEnvelope)) : [];
  const roadArea = Math.abs(pathsArea(roadUnion));
  const buildingArea = Math.abs(pathsArea(bUnion));

  /* 1. 停车：按建筑面积配车位，贴着主路放，落在净地上 */
  let parking: Paths = [];
  let parkingSpaces = 0;
  const demand = Math.round((buildingArea / 100) * PARKING_RATIO[landUse]);
  const parkingTarget = Math.min(demand * PARKING_PER_SPACE, siteArea * 0.1);
  if (input.accessRoad && demand > 0 && parkingTarget >= 150) {
    parking = placeParking(input.accessRoad, parkingTarget, free, bUnion, roadUnion);
    const a = Math.abs(pathsArea(parking));
    parkingSpaces = Math.floor(a / PARKING_PER_SPACE);
  }

  /* 2. 铺装：建筑外扩一圈，落在净地上，扣掉建筑自己 / 路 / 停车 */
  let paving: Paths = [];
  const pad = PAVING_PAD[landUse];
  if (bUnion.length) {
    const ring = offsetPath(bUnion, pad);
    if (ring.length) {
      paving = intersect(ring, free);
      paving = subtract(paving, bUnion);
      if (roadUnion.length) paving = subtract(paving, roadUnion);
      if (parking.length) paving = subtract(paving, parking);
      if (input.excluded?.length) paving = subtract(paving, input.excluded);
    }
  }
  /*
   * 3. 先定下"要保留哪些面"，再算绿地。
   * 顺序不能反过来：如果先算绿地再丢掉太小的铺装，那小块地就谁都不属于它，
   * 用地平衡会莫名少掉几十平米（第一版示例项目就差 162 ㎡）。
   * 绿地永远兜底 —— 它是 subtract 出来的，保留它就不可能出现"无主地块"。
   */
  const grounds: PlannedGround[] = [];
  let parkingOut: Paths = [];
  let pavingOut: Paths = [];

  if (parking.length && Math.abs(pathsArea(parking)) >= 120) {
    parkingOut = parking;
    grounds.push({ boundary: parking, surface: 'parking', name: '停车场' });
  } else {
    parkingSpaces = 0;
  }
  if (paving.length && Math.abs(pathsArea(paving)) >= 60) {
    pavingOut = paving;
    grounds.push({ boundary: paving, surface: 'paving', name: '硬质铺装' });
  }

  let taken = bUnion;
  for (const p of [roadUnion, parkingOut, pavingOut]) {
    if (!p.length) continue;
    taken = taken.length ? unionAll([taken, p]) : p;
  }
  if (input.excluded?.length) taken = taken.length ? unionAll([taken, input.excluded]) : input.excluded;

  const lawn: Paths = taken.length ? subtract(boundary, taken) : boundary;
  if (lawn.length) grounds.push({ boundary: lawn, surface: 'lawn', name: '绿地' });

  const excludedArea = input.excluded?.length ? Math.abs(pathsArea(input.excluded)) : 0;

  return {
    roads,
    grounds,
    stats: {
      siteArea,
      buildingArea,
      roadArea,
      pavingArea: Math.abs(pathsArea(pavingOut)),
      lawnArea: Math.abs(pathsArea(lawn)),
      parkingArea: Math.abs(pathsArea(parkingOut)),
      parkingSpaces,
      excludedArea,
    },
  };
}

/**
 * 停车场地：沿主路扫描，找第一块"贴着路、面积够、在净地内"的带。
 *
 * 不走"固定取路中段"那一套：主路经常贴着地块边缘走，固定点算出来的矩形
 * 一半在地块外，交出来是空的（示例项目第一次跑就是这样，一个车位都没有）。
 * 沿路扫一遍，哪一段两侧放得下就用哪一段，顺带满足"停车在入口附近"。
 */
function placeParking(
  road: PlannedRoad,
  targetArea: number,
  free: Paths,
  buildings: Paths,
  roadUnion: Paths,
): Paths {
  const line = road.centerline;
  const total = polylineLength(line);
  if (total < 8 || !free.length) return [];

  const depth = 14;
  const length = Math.min(34, Math.max(10, targetArea / depth));
  let best: Paths = [];
  let bestArea = 0;

  for (let s = total * 0.12; s + length * 0.4 < total; s += 6) {
    const from = pointAtDistance(line, s);
    const to = pointAtDistance(line, Math.min(total, s + length));
    if (dist(from, to) < 8) continue;

    const dir = normalize(sub(to, from));
    const n = perp(dir);

    for (const sign of [1, -1]) {
      const base = mul(n, sign * (road.width / 2 + 1));
      const quad: Vec2[] = [
        add(from, base),
        add(to, base),
        add(add(to, base), mul(n, sign * depth)),
        add(add(from, base), mul(n, sign * depth)),
      ];
      let area = intersect([quad], free);
      if (!area.length) continue;
      if (buildings.length) area = subtract(area, buildings);
      if (roadUnion.length) area = subtract(area, roadUnion);
      const a = area.length ? Math.abs(pathsArea(area)) : 0;
      if (a > bestArea) {
        bestArea = a;
        best = area;
      }
      // 已经够用就停：越靠入口越好
      if (a >= targetArea * 0.8) return area;
    }
  }
  return bestArea >= 150 ? best : [];
}

/** 折线上按实际距离取点 */
function pointAtDistance(line: Vec2[], target: number): Vec2 {
  let acc = 0;
  for (let i = 0; i < line.length - 1; i++) {
    const l = dist(line[i], line[i + 1]);
    if (acc + l >= target) {
      const k = l < 1e-9 ? 0 : (target - acc) / l;
      return { x: line[i].x + (line[i + 1].x - line[i].x) * k, y: line[i].y + (line[i + 1].y - line[i].y) * k };
    }
    acc += l;
  }
  return line[line.length - 1];
}

/** 折线上某里程处的走向（单位向量）：支路要垂直它 */
function directionAt(line: Vec2[], target: number): Vec2 | null {
  let acc = 0;
  for (let i = 0; i < line.length - 1; i++) {
    const l = dist(line[i], line[i + 1]);
    if (acc + l >= target || i === line.length - 2) {
      const d = sub(line[i + 1], line[i]);
      return len(d) < 1e-9 ? null : normalize(d);
    }
    acc += l;
  }
  return null;
}
