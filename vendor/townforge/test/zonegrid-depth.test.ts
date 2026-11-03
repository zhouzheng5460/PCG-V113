// `ReduceDepths` 的单元测试 —— C# `ZoneGuard.cs:1231`（移植自游戏 `Game.Zones.CellOverlapJobs`）。
//
// 为什么值得单独测：这条规则是"地块为什么能变大"的**规则依据**，不是我的启发式。
// C# 的注释点名了一个病态场景，必须逐字复现：
//
//   > A column deeper than both neighbours is cut to the deeper neighbour unless a neighbour of its
//   > own depth stands beside it, so a lone 1-column cutout (depth 2 between blocked columns of
//   > depth 0) is blocked entirely: the playtest's "row 0 with row 1 blocked / behind a blocked cell".
//
// 跑法：node node_modules/tsx/dist/cli.mjs test/zonegrid-depth.test.ts

import { reduceDepths, type CellGrid } from '../src/bridge/zonegrid';

let failures = 0;
let total = 0;
function check(name: string, cond: boolean, detail = ''): void {
  total++;
  if (!cond) {
    failures++;
    console.log(`  FAIL ${name} ${detail}`);
  }
}

/** 造一个 grid：`dir = [0,-1]` 指向 -y（路在上方），position 放原点。 */
function grid(sx: number): CellGrid {
  return { position: [0, 0], dir: [0, -1], sx, sy: 6 };
}

const show = (d: number[]): string => `[${d.join(',')}]`;

// ── 1) 病态场景：孤立的一列深坑必须被整列削掉
//
// 剖面 [0, 2, 0]：中间那一列深 2，两边都是 0（被挡）。C# 注释说这种"孤零零一列"
// 会被彻底削平 —— 因为它两头都没有同深的邻居撑腰，游戏里就是"一排格子后面挡一格"的穿帮。
{
  const grids = [grid(3)];
  const depth = [[0, 2, 0]];
  reduceDepths(grids, depth);
  check('孤立 1 列深坑被削平', depth[0][1] === 0, show(depth[0]));
}

// ── 2) 削平会**沿列传染**（这是 C# 的真实行为，不是我实现的偏差）
//
// 剖面 [2,6,4]：中间的 6 被削到 2（左邻居），随后最右的 4 也被削到 2（因为它的左邻居
// 已经变成 2）。原因就是 C# 的 `Left`/`Right` 读的是**已经被改过的** `depth` ——
// 所以 `GameDepthPass` 的实现注释才要写 "repeated until stable (a removed column changes
// its neighbours)"。
//
// 这个"传染"是好事：它把一条深浅不齐的带子**抹成等深**，等深段变长，
// 宗地才有可能变大（实测拼接率 2%~13%）。
{
  const grids = [grid(3)];
  const depth = [[2, 6, 4]];
  reduceDepths(grids, depth);
  check('深列让到左邻居', depth[0][1] === 2, show(depth[0]));
  check('削平沿列传染', depth[0][2] === 2, show(depth[0]));
  check('格局变成等深', depth[0].every((d) => d === 2), show(depth[0]));
}

// ── 3) 不许把已经够浅的列削得更深（只会削，不会长）
{
  const grids = [grid(3)];
  const depth = [[6, 2, 6]];
  reduceDepths(grids, depth);
  check('浅列不动', depth[0][1] === 2, show(depth[0]));
  check('削平只减不增', depth[0][0] <= 6 && depth[0][2] <= 6, show(depth[0]));
}

// ── 4) 等深的邻居撑腰：一整条等深的带子一格都不许少
//
// 这是"地块能不能大"的直接前提：如果削平会误伤等深的带子，长地块就永远长不出来。
{
  const grids = [grid(6)];
  const depth = [[6, 6, 6, 6, 6, 6]];
  reduceDepths(grids, depth);
  check('等深带子原样保留', depth[0].every((d) => d === 6), show(depth[0]));
}

// ── 5) 越界读邻居：同一条路被切成两段时，排号要接得上（C# 的 `Get`）
//
// 两个网格沿路首尾相接（见 `ZoneGrid.side()` 的 chunk 切法）。第一段的右端在越界查找时
// 应当读到第二段左端的深度，否则"两段各自算自己的深度"就会在接缝处各削一刀。
{
  // 第二段在第一段右侧 6 格 · 8 m = 48 m 处
  const g1: CellGrid = { position: [0, 0], dir: [0, -1], sx: 6, sy: 6 };
  // 位置沿 right(dir) = [dir.y, -dir.x] = [-1, 0]... 直接按 cellCentre 推：第一段 x=0 的
  // 格心在 +20 m 处（sx=6），第二段的 block 中心要落在第一段再往左 48 m
  const g2: CellGrid = { position: [-48, 0], dir: [0, -1], sx: 6, sy: 6 };
  const grids = [g1, g2];
  const depth = [
    [6, 6, 6, 6, 6, 6],
    [6, 6, 6, 6, 6, 3],
  ];
  reduceDepths(grids, depth);
  // 第二段右端是 3，第一段左端挨着它 → 第一段左端也不该超过 3（沿路延续同一排）
  check('跨段读到了邻居的深度', depth[0][0] <= 3, show(depth[0]));
}

console.log(`\n${failures === 0 ? 'PASS' : 'FAIL'} — ${total} 项断言，${failures} 项失败`);
if (failures) process.exitCode = 1;
