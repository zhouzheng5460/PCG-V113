// 把成排的灌木拟合成绿篱（`hedge` 的一段）。
//
// 为什么需要：Placemaker 的 `bush` 在花园里是沿花床边线、按 3.3 m 节律排出来的控制点。
// 桥接后一颗颗画成 1.5 m 的小锥，近看是"一堆绿色碎块"，远看是噪点；CS2 里它们本来就是
// 一条绿篱，连起来才对。
//
// **判断"这是绿篱"还是"随手撒的灌木"，靠 yaw，不靠距离。**
// Placemaker 排绿篱时整条线共用一个 yaw（yaw 是这条线的**法向**，走向 = yaw + 90°），
// 随手撒的每颗 yaw 都不同。实测 20 个生成器里只有 formalGarden 是前者——
// 197 颗灌木只用了 5 个朝向（-85° × 66、95° × 64、5° × 26、-175° × 25、0° × 16），
// 而 park / natureReserve / parking 的几百颗各有 150~200 个不同朝向。
//
// 只用最近邻距离串链的话，散点也能凑出 3 点链：实测 natureReserve 的覆盖率能到 48%，
// 全是假阳性——半片林地会凭空长出十几段绿篱。所以定 yaw 分组是**前提**，
// 距离只负责在同一组里把相隔很远的两个花床切开。
//
// 两道闸（都在下面 `HedgeOptions` 里可调）：
//   整体闸 `yawSpreadMax`——一份 Plan 的朝向档数铺满了大半角度区间，就是随机旋转，整份不连；
//   组内闸 `minYawGroup`——留下的朝向里，某一档至少 3 颗才算数。
// 实测结果：20 个生成器里只有 formalGarden 过闸（197 颗只用 2 档朝向），
// 其余 19 个的朝向档数是 17~19（上限 18），全被整体闸挡掉。

/** 一条绿篱（Plan 坐标，米）。 */
export interface HedgeBar {
  /** 起点 */
  a: [number, number];
  /** 终点 */
  b: [number, number];
  /** 走向（弧度，Plan 的 xz 平面，0 = +X）。等于 `yaw - π/2`，和 `PropFeature.rot` 同一套。 */
  dir: number;
  /** 被这条绿篱吸收的灌木下标（0-based，对应入参数组） */
  members: number[];
}

export interface HedgeOptions {
  /**
   * 同一个 yaw 至少要有几颗才算"这组是刻意排的"，默认 3。
   * 这是第二道闸：过了整体闸之后，组内太小的仍然当散点（3 颗同向也可能只是巧合）。
   */
  minYawGroup?: number;
  /** yaw 分箱宽度（度），默认 10。Placemaker 的 yaw 是 5° 的整数倍，10° 够容错又不串组。 */
  yawBinDeg?: number;
  /**
   * **整体闸**：一份 Plan 用到的朝向档数，占满 180°/`yawBinDeg` 这个区间的比例上限，默认 0.35。
   *
   * 判据的道理：排绿篱时整条线共用一个朝向，所以一份 Plan 只会用到极少数几档；
   * 随手撒的时候每颗朝向都随机，几乎铺满整个角度区间。实测 20 个生成器分得很开——
   * formalGarden 只用 2 档（197 颗），parking / natureReserve / park / dogPark 都是 17~19 档。
   *
   * 这道闸挡的是"局部巧合"：`parking` 的 232 颗里有 3 颗碰巧同向，不设闸就会凭空
   * 长出 3 段绿篱；`natureReserve` 同理。宁可少连，不能乱连。
   */
  yawSpreadMax?: number;
  /** 同一条线上，垂直方向的容差（米），默认 0.9。约等于半个灌木宽。 */
  perpTol?: number;
  /** 同一条线上，相邻两颗的最大间隔（米），超过就断开，默认 8。 */
  gapTol?: number;
  /** 一段至少几颗才算绿篱，默认 2（矩形花床的短边只有 2 颗，不能丢）。 */
  minRun?: number;
  /** 灌木宽度（米），用来把绿篱往两头各延长半个身位，默认 1.5。 */
  widthM?: number;
}

const DEFAULTS: Required<HedgeOptions> = {
  minYawGroup: 3,
  yawBinDeg: 10,
  yawSpreadMax: 0.35,
  perpTol: 0.9,
  gapTol: 8,
  minRun: 2,
  widthM: 1.5,
};

export interface HedgeFit {
  bars: HedgeBar[];
  /** 每颗灌木是否被并进绿篱（false = 保持散点） */
  inRun: boolean[];
}

/** yaw → Plan 平面里这条绿篱的走向（和 `yawToRot` 同一个换算）。 */
export function yawToDir(yaw: number): number {
  return yaw - Math.PI / 2;
}

/**
 * 把灌木点拟合成绿篱段。**纯函数**，不碰场景。
 *
 * 输入是 Placemaker 的 `PlanObject`（只用到 x / z / yaw），输出若干段绿篱 +
 * 每颗灌木是否被吸收。没被吸收的，调用方照旧画成 `shrub`。
 */
export function fitHedgeBars(
  bushes: readonly { x: number; z: number; yaw?: number }[],
  opts: HedgeOptions = {},
): HedgeFit {
  const o = { ...DEFAULTS, ...opts };
  const inRun = new Array<boolean>(bushes.length).fill(false);
  const bars: HedgeBar[] = [];
  if (bushes.length < o.minYawGroup) return { bars, inRun };

  // 1) 按 yaw 分箱。180° 等价（法向朝里朝外是同一条线），所以取模 180 再分箱。
  const bin = (yaw: number) => {
    let d = (yaw * 180) / Math.PI;
    d = ((d % 180) + 180) % 180;
    return Math.round(d / o.yawBinDeg) * o.yawBinDeg;
  };
  const groups = new Map<number, number[]>();
  bushes.forEach((b, i) => {
    const k = bin(b.yaw ?? 0);
    const g = groups.get(k);
    if (g) g.push(i);
    else groups.set(k, [i]);
  });

  // 整体闸：朝向铺满了大半角度区间 = 随机旋转 = 这是散点种植，一颗都不连。
  // 不设这道闸，232 颗里的 3 颗同向就会凭空长出 3 段绿篱（parking 实测如此）。
  const binCount = Math.round(180 / o.yawBinDeg);
  if (groups.size > binCount * o.yawSpreadMax) return { bars, inRun };

  for (const [binDeg, members] of groups) {
    if (members.length < o.minYawGroup) continue;

    // 2) 在这个朝向下取一组正交基：u = 走向，n = 法向。
    //    binDeg 是分箱后的 yaw；同一个箱里的 yaw 最多差 ±半个箱宽，当成同一条线的法向。
    const dir = yawToDir((binDeg * Math.PI) / 180);
    const ux = Math.cos(dir), uz = Math.sin(dir);
    const nx = -uz, nz = ux;
    const along = (i: number) => bushes[i].x * ux + bushes[i].z * uz;
    const perp = (i: number) => bushes[i].x * nx + bushes[i].z * nz;

    // 3) 按法向距离分桶：同一条线上的灌木在法向上必然重合。
    //    不同花床只要不在同一条线上，就会落进不同桶，不会被连起来。
    const sorted = [...members].sort((a, b) => perp(a) - perp(b));
    const lines: number[][] = [];
    let bucket: number[] = [];
    for (const i of sorted) {
      if (bucket.length > 0 && Math.abs(perp(i) - perp(bucket[0])) > o.perpTol) {
        lines.push(bucket);
        bucket = [];
      }
      bucket.push(i);
    }
    if (bucket.length > 0) lines.push(bucket);

    // 4) 每条线沿线排序，按间隔切段，够长的出绿篱。
    for (const line of lines) {
      const seq = [...line].sort((a, b) => along(a) - along(b));
      let run: number[] = [seq[0]];
      const flush = () => {
        if (run.length >= o.minRun) {
          const a0 = along(run[0]) - o.widthM / 2;
          const a1 = along(run[run.length - 1]) + o.widthM / 2;
          // 法向取成员均值：分箱后同桶内还有一点抖动，逐颗用会让绿篱呈锯齿状。
          const p0 = run.reduce((s, i) => s + perp(i), 0) / run.length;
          const at = (a: number): [number, number] => [a * ux + p0 * nx, a * uz + p0 * nz];
          bars.push({ a: at(a0), b: at(a1), dir, members: [...run] });
          for (const i of run) inRun[i] = true;
        }
        run = [];
      };
      for (let k = 1; k < seq.length; k++) {
        if (along(seq[k]) - along(seq[k - 1]) > o.gapTol) flush();
        run.push(seq[k]);
      }
      flush();
    }
  }

  // 段数按"沿线起点"排序，让产物稳定（Map 的迭代顺序依赖插入顺序，输入顺序一变产物就变）
  bars.sort((p, q) => p.a[1] - q.a[1] || p.a[0] - q.a[0]);
  return { bars, inRun };
}
