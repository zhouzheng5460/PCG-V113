// 面积计算：衡量"铺了多少地"这件事的唯一口径。
//
// 为什么单独拆出来：这几行踩过两次坑，都是**算错了还以为在看真实数据**——
//   1. 把各面面积直接相加：Placemaker 分层叠放（草坪底 + 铺装压在上面），
//      相加会得到 120% 这种数，误判成"铺装溢出了地块"；
//   2. 并集时把洞也加进去：绕开建筑的铺装会在并集里留下洞，洞是顺时针的负面积，
//      取绝对值相加会让 union > sum（146%），比第一种更离谱。
//
// 所以口径必须能被单元测试钉住，别只活在某个工具的局部函数里。

import polygonClipping from 'polygon-clipping';

/** 多边形面积（m²，无符号）。 */
export function ringArea(r: readonly [number, number][] | undefined): number {
  if (!r || r.length < 3) return 0;
  let s = 0;
  for (let i = 0, j = r.length - 1; i < r.length; j = i++) s += r[j][0] * r[i][1] - r[i][0] * r[j][1];
  return Math.abs(s) / 2;
}

/**
 * 带符号面积。polygon-clipping 输出的 Polygon 是 `[外环, 洞1, 洞2…]`：
 * 外环逆时针（正）、洞顺时针（负）。
 */
export function signedRingArea(r: readonly [number, number][] | undefined): number {
  if (!r || r.length < 3) return 0;
  let s = 0;
  for (let i = 0, j = r.length - 1; i < r.length; j = i++) s += r[j][0] * r[i][1] - r[i][0] * r[j][1];
  return s / 2;
}

/** 多个环的面积之和，用于"我不管重叠，就想知道一共铺了多少层"的场合。 */
export function sumArea(rings: readonly (readonly [number, number][])[]): number {
  return rings.reduce((s, r) => s + ringArea(r), 0);
}

/**
 * 并集面积（重叠只算一次，洞扣掉）。
 *
 * `ok=false` 表示多边形自交/零宽导致 polygon-clipping 抛异常、退化成了相加——
 * 这时结果只可能偏高，调用方该提示一句。
 */
export function unionArea(
  rings: readonly (readonly [number, number][])[],
): { area: number; ok: boolean } {
  const valid = rings.filter((r) => r && r.length >= 3).map((r) => r as [number, number][]);
  if (valid.length === 0) return { area: 0, ok: true };
  if (valid.length === 1) return { area: ringArea(valid[0]), ok: true };
  try {
    const polys = valid.map((r) => [r]);
    const out = polygonClipping.union(polys[0] as never, ...(polys.slice(1) as never[]));
    let s = 0;
    for (const poly of out as [number, number][][][]) for (const ring of poly) s += signedRingArea(ring);
    return { area: Math.abs(s), ok: true };
  } catch {
    return { area: sumArea(valid), ok: false };
  }
}
