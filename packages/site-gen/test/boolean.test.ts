// `primitives/boolean.ts` 的验收：六个边角用例 + 确定性 + 绕序归一。
//
// 为什么单独一个测试文件而且盯得这么细：布尔运算是**所有区域类生成器的地基**，
// 它错一格，上层每个生成器都跟着错，而且错法往往是"看起来还行"（少一块草、多一道缝）。
// 用户在整套工作流里反复强调的一点是：门禁必须打数字，不能只打 PASS。
//
// 六个用例对应真正会遇到的六种形状（不是凑数）：
//   1. 矩形减矩形（部分重叠）        —— 最常见：地块减铺装
//   2. 凹多边形减矩形                —— L 形地块减一个边角
//   3. cutter 完全在内部 → 出洞      —— 广场中间的景 / 地下车库坡道
//   4. cutter 横穿 → 断成两块        —— 绿带把停车场切成两半
//   5. cutter 与边界共线/贴边        —— **最容易崩的一种**（浮点退化）
//   6. 多个 cutter 互相重叠          —— 相邻两块禁建区叠在一起
//
// 另加两组性质：
//   · **确定性**：同一输入跑两次，序列化结果必须逐字节相同（golden 快照的前提）
//   · **绕序**：外环一律 CCW、洞一律 CW（下游按绕序判内外）
//
// 跑法：node packages/site-gen/test/boolean.test.ts

import {
  differenceRegion,
  intersectRegion,
  unionRegions,
  regionArea,
  type PolygonRegion,
  type Ring,
} from '../src/primitives/boolean.ts';
import { area as ringArea, isCCW, type Vec2 } from '../../plan-ir/src/geom.ts';

let failures = 0;
let total = 0;
function check(name: string, cond: boolean, detail = ''): void {
  total++;
  if (!cond) {
    failures++;
    console.log(`  FAIL ${name} ${detail}`);
  }
}

const rect = (x0: number, y0: number, x1: number, y1: number): PolygonRegion => ({
  outer: [
    [x0, y0],
    [x1, y0],
    [x1, y1],
    [x0, y1],
  ] as Vec2[],
});

const areaOf = (rs: PolygonRegion[]): number => rs.reduce((s, r) => s + regionArea(r), 0);
const near = (a: number, b: number, tol = 1e-2): boolean => Math.abs(a - b) <= tol;
const show = (rs: PolygonRegion[]): string =>
  rs.map((r) => `外${Math.abs(ringArea(r.outer)).toFixed(1)}${r.holes?.length ? `+洞${r.holes.length}` : ''}`).join(' + ');

// ── 1) 矩形减矩形（部分重叠）
{
  const a = rect(0, 0, 10, 10); // 100
  const b = rect(5, 0, 15, 10); // 与 a 重叠 5×10 = 50
  const r = differenceRegion(a, [b]);
  check('① 结果一块', r.length === 1, show(r));
  check('① 面积 = 100 − 50 = 50', near(areaOf(r), 50), areaOf(r).toFixed(2));
}

// ── 2) 凹多边形减矩形（L 形减掉内角那块）
{
  // L 形：100 − 25 = 75
  const L: PolygonRegion = {
    outer: [
      [0, 0],
      [10, 0],
      [10, 5],
      [5, 5],
      [5, 10],
      [0, 10],
    ] as Vec2[],
  };
  check('② L 形原面积 = 75', near(Math.abs(ringArea(L.outer)), 75), Math.abs(ringArea(L.outer)).toFixed(2));
  const r = differenceRegion(L, [rect(-1, -1, 4, 4)]); // 挖掉左下 4×4 = 16
  check('② 结果一块', r.length === 1, show(r));
  check('② 面积 = 75 − 16 = 59', near(areaOf(r), 59), areaOf(r).toFixed(2));
}

// ── 3) cutter 完全在内部 → 产生洞
{
  const a = rect(0, 0, 10, 10); // 100
  const hole = rect(3, 3, 7, 7); // 16
  const r = differenceRegion(a, [hole]);
  check('③ 结果一块', r.length === 1, show(r));
  check('③ 有一个洞', (r[0]?.holes?.length ?? 0) === 1, show(r));
  check('③ 面积 = 100 − 16 = 84', near(areaOf(r), 84), areaOf(r).toFixed(2));
  check('③ 外环逆时针', isCCW(r[0].outer), '');
  check('③ 洞顺时针', isCCW(r[0].holes![0]) === false, '');
}

// ── 4) cutter 横穿 → 断成两块
{
  const a = rect(0, 0, 10, 10); // 100
  const cut = rect(-1, 4, 11, 6); // 上下各留 4 和 4
  const r = differenceRegion(a, [cut]);
  check('④ 结果两块', r.length === 2, show(r));
  check('④ 面积 = 100 − 20 = 80', near(areaOf(r), 80), areaOf(r).toFixed(2));
}

// ── 5) cutter 与边界共线 / 贴边（浮点退化最容易崩的一种）
{
  const a = rect(0, 0, 10, 10);
  const flushRight = rect(5, 0, 10, 10); // 右边界完全共线，面积取一半
  const r1 = differenceRegion(a, [flushRight]);
  check('⑤ 共线切一半：面积 = 50', near(areaOf(r1), 50), areaOf(r1).toFixed(2));
  check('⑤ 共线切一半：不崩且出结果', r1.length >= 1, show(r1));

  const corner = rect(0, 0, 2, 2); // 只贴一个角
  const r2 = differenceRegion(a, [corner]);
  check('⑤ 贴角：面积 = 96', near(areaOf(r2), 96), areaOf(r2).toFixed(2));

  const outside = rect(50, 50, 60, 60); // 完全在外，什么都不该减
  const r3 = differenceRegion(a, [outside]);
  check('⑤ 完全在外：面积 = 100', near(areaOf(r3), 100), areaOf(r3).toFixed(2));
  check('⑤ 完全在外：一块', r3.length === 1, show(r3));
}

// ── 6) 多个 cutter 互相重叠 → 重叠部分只减一次
{
  const a = rect(0, 0, 10, 10); // 100
  const c1 = rect(0, 0, 6, 6); // 36
  const c2 = rect(4, 4, 10, 10); // 36，与 c1 重叠 2×2 = 4
  const r = differenceRegion(a, [c1, c2]);
  // 并集 = 36 + 36 − 4 = 68 → 剩 32
  check('⑥ 重叠 cutter 只减一次：面积 = 32', near(areaOf(r), 32), areaOf(r).toFixed(2));
}

// ── 7) 确定性：同一输入两次结果逐字节相同
{
  const a: PolygonRegion = {
    outer: [
      [0, 0],
      [13.7, 0.3],
      [10, 8.1],
      [4.4, 12.2],
      [-1.2, 6.6],
    ] as Vec2[],
  };
  const cutters = [rect(2, 2, 5, 5), rect(6, 1, 9, 4), rect(3.3, 6.6, 7.7, 9.9)];
  const s1 = JSON.stringify(differenceRegion(a, cutters));
  const s2 = JSON.stringify(differenceRegion(a, cutters));
  check('⑦ 两次输出逐字节相同', s1 === s2, s1 === s2 ? '' : '有差异');

  // 起点归一：把输入的起点转一位，输出必须不变
  const rotated: PolygonRegion = { outer: [...a.outer.slice(2), ...a.outer.slice(0, 2)] };
  const s3 = JSON.stringify(differenceRegion(rotated, cutters));
  check('⑦ 输入换起点不影响输出', s1 === s3, '起点归一生效');

  // 绕序归一：把输入反转，输出必须不变
  const reversed: PolygonRegion = { outer: a.outer.slice().reverse() };
  const s4 = JSON.stringify(differenceRegion(reversed, cutters));
  check('⑦ 输入反向绕序不影响输出', s1 === s4, '绕序归一生效');
}

// ── 8) union / intersection 也要能用（上层会用到）
{
  const a = rect(0, 0, 10, 10);
  const b = rect(5, 0, 15, 10);
  const u = unionRegions([a, b]);
  check('⑧ 并集面积 = 150（重叠只算一次）', near(areaOf(u), 150), areaOf(u).toFixed(2));
  check('⑧ 并集一块', u.length === 1, show(u));

  const i = intersectRegion(a, b);
  check('⑧ 交集面积 = 50', near(areaOf(i), 50), areaOf(i).toFixed(2));
}

// ── 9) 退化输入不许崩
{
  const a = rect(0, 0, 10, 10);
  const zero: PolygonRegion = { outer: [[0, 0], [0, 0], [0, 0]] as Vec2[] };
  const r = differenceRegion(a, [zero]);
  check('⑨ 零面积 cutter 被忽略', near(areaOf(r), 100), areaOf(r).toFixed(2));

  const empty = differenceRegion(a, []);
  check('⑨ 无 cutter 返回原面积', near(areaOf(empty), 100), areaOf(empty).toFixed(2));
}

console.log(`\n${failures === 0 ? 'PASS' : 'FAIL'} — ${total} 项断言，${failures} 项失败`);
if (failures) process.exitCode = 1;
