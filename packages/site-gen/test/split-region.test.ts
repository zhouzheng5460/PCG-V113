// `primitives/split-region.ts` 的验收。
//
// 三件必须成立的事（用户 2026-11-03 对 P2 的硬要求）：
//   · **全覆盖**：各分区面积之和 = 原区域面积
//   · **不重叠**：任意两分区交集为空
//   · **确定性**：同一输入两次调用，序列化逐字节相同；且分区**顺序稳定**
//
// 另外验两件业务上的判断：
//   · L 形地块应该被切成两块、各自朝向互相垂直（这是"多方向"的最小证据）
//   · 一块方正的地块**不该**被切开（切了反而降低 usableScore）
//
// 跑法：node packages/site-gen/test/split-region.test.ts

import {
  partitionCoverage,
  splitRegion,
  type PartitionEvaluator,
  type RegionPartition,
} from '../src/primitives/split-region.ts';
import { intersectRegion, regionArea, type PolygonRegion, type Ring } from '../src/primitives/boolean.ts';
import { area as ringArea, pointInRing, type Vec2 } from '../../plan-ir/src/geom.ts';

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

/**
 * L 形：横腿 80×18 + 竖腿 20×52（总 2480）。
 *
 * 为什么不用对称的 L：对称 L 在 0 和 π/2 两个方向上的"行带覆盖率"一样，
 * 分不分都看不出差别。这里**故意让两条腿的进深不同**（18 vs 52），
 * 于是"横腿该用 0、竖腿该用 π/2"才有可测的收益 —— 这是"多方向"的最小可验证证据。
 */
const L: PolygonRegion = {
  outer: [
    [0, 0],
    [80, 0],
    [80, 18],
    [20, 18],
    [20, 70],
    [0, 70],
  ] as Vec2[],
};

/**
 * 极简 evaluator：**沿该朝向能被标准行带（宽 18 m）覆盖的面积比例 × 面积**。
 *
 * 两条性质很关键：
 *   1. **对切分是加性的** —— 切成两块后的分数之和 ≈ 不切时的分数，所以"平白切一刀"不会占便宜。
 *      （用 `sqrt(area)` 之类的凹函数会让切分永远占优 —— 我第一版就是这么写的，直接测出来。）
 *   2. **朝向敏感** —— 进深余量不同的腿会给出不同比例，于是"该用哪个方向"能被测出来。
 *
 * 和 parking 语义同构（都是"行带铺得下多少"），但不引用 parking 任何代码 ——
 * 这正是"原语不认识生成器"的验证。
 */
const bandEvaluator: PartitionEvaluator = (region: PolygonRegion, orientation: number, area: number) => {
  const n: Vec2 = [Math.cos(orientation), Math.sin(orientation)];
  let lo = Infinity;
  let hi = -Infinity;
  for (const p of region.outer) {
    const t = p[0] * n[0] + p[1] * n[1];
    lo = Math.min(lo, t);
    hi = Math.max(hi, t);
  }
  const depth = hi - lo;
  if (depth <= 1e-6) return 0;
  const covered = (Math.floor(depth / 18) * 18) / depth; // 行带覆盖进深的比例
  return area * covered;
};

const DIRS = [0, Math.PI / 2, Math.PI / 4];

// ── 1) 全覆盖 + 不重叠
{
  const parts = splitRegion(L, { type: 'orientation', directions: DIRS }, bandEvaluator);
  const cov = partitionCoverage(parts, L);
  check('① 全覆盖（面积和 = 原面积）', Math.abs(cov - 1) < 0.01, `覆盖 ${(cov * 100).toFixed(2)}%`);

  let overlap = 0;
  for (let i = 0; i < parts.length; i++) {
    for (let j = i + 1; j < parts.length; j++) {
      for (const r of intersectRegion(parts[i].region, parts[j].region)) overlap += regionArea(r);
    }
  }
  check('① 分区两两不重叠', overlap < 0.5, `重叠 ${overlap.toFixed(2)} m²`);
  check('① 分区数 ≥ 1', parts.length >= 1, `${parts.length} 块`);
}

// ── 2) L 形应该被切开，且两块朝向不同（"多方向"的最小证据）
{
  const parts = splitRegion(L, { type: 'orientation', directions: DIRS }, bandEvaluator);
  check('② L 形被切成多块', parts.length >= 2, `${parts.length} 块`);
  if (parts.length >= 2) {
    const a = parts[0].orientation;
    const b = parts[1].orientation;
    // 两个朝向按 mod π 比较，应当不同（差值不是 0 也不是 π）
    let d = Math.abs(a - b) % Math.PI;
    if (d > Math.PI / 2) d = Math.PI - d;
    check('② 两块朝向不同', d > 0.1, `${a.toFixed(2)} vs ${b.toFixed(2)}`);
  }
}

// ── 3) 已经"正好吃满模组"的地块**不该**被切开
//
// 用 108×90（两个方向都是 18 的整数倍）而不是 120×100：
// 后者在 18 m 模组下本来就有 10% 进深浪费，切掉一个进深刚好 36 m 的角落**确实更优** ——
// 那是真实现象，不是 bug。要验的是"切了没有收益时不该切"，所以用例必须无浪费。
{
  const sq = rect(0, 0, 108, 90);
  const parts = splitRegion(sq, { type: 'orientation', directions: [0, Math.PI / 2] }, bandEvaluator);
  check('③ 吃满模组的地块不切开', parts.length === 1, `${parts.length} 块`);
  check('③ 覆盖 100%', Math.abs(partitionCoverage(parts, sq) - 1) < 0.01);

  // 反过来：有浪费的地块**允许**被切（切角是真收益）
  const wasteful = rect(0, 0, 120, 100);
  const pw = splitRegion(wasteful, { type: 'orientation', directions: [0, Math.PI / 2] }, bandEvaluator);
  const unsplit = bandEvaluator(wasteful, 0, Math.abs(ringArea(wasteful.outer)));
  const splitSum = pw.reduce((s2, p) => s2 + p.usableScore, 0);
  check('③ 有浪费时切分不劣于不切', splitSum >= unsplit - 1e-6, `${splitSum.toFixed(0)} vs ${unsplit.toFixed(0)}`);
}

// ── 4) 确定性 + 顺序稳定
{
  const a = splitRegion(L, { type: 'orientation', directions: DIRS }, bandEvaluator);
  const b = splitRegion(L, { type: 'orientation', directions: DIRS }, bandEvaluator);
  const sa = JSON.stringify(a.map((p) => ({ o: p.orientation, a: Math.round(p.area * 100) })));
  const sb = JSON.stringify(b.map((p) => ({ o: p.orientation, a: Math.round(p.area * 100) })));
  check('④ 两次调用逐字节相同', sa === sb, sa === sb ? '' : '有差异');

  // 候选顺序打乱：结果必须一致（我们是按方向去重后固定顺序，评分用严格 >）
  const c = splitRegion(L, { type: 'orientation', directions: [...DIRS].reverse() }, bandEvaluator);
  const sc = JSON.stringify(c.map((p) => ({ o: p.orientation, a: Math.round(p.area * 100) })));
  check('④ 候选顺序不影响结果', sa === sc, sa === sc ? '' : '受顺序影响');
}

// ── 5) 候选不超过 3 个（接口约束）
{
  const many = [0, 0.1, 0.2, 0.3, 0.4, 0.5];
  const parts = splitRegion(L, { type: 'orientation', directions: many }, bandEvaluator);
  check('⑤ 候选多于 3 个时只取前 3', parts.length <= 2, `${parts.length} 块`);
  check('⑤ 仍然全覆盖', Math.abs(partitionCoverage(parts, L) - 1) < 0.01);
}

// ── 6) axis 策略：等分且覆盖完整
{
  const sq = rect(0, 0, 90, 40);
  const parts: RegionPartition[] = splitRegion(sq, { type: 'axis', axis: [1, 0], count: 3 }, bandEvaluator);
  check('⑥ axis 切成 3 条', parts.length === 3, `${parts.length} 条`);
  const cov = partitionCoverage(parts, sq);
  check('⑥ axis 全覆盖', Math.abs(cov - 1) < 0.01, `${(cov * 100).toFixed(1)}%`);

  let overlap = 0;
  for (let i = 0; i < parts.length; i++) {
    for (let j = i + 1; j < parts.length; j++) {
      for (const r of intersectRegion(parts[i].region, parts[j].region)) overlap += regionArea(r);
    }
  }
  check('⑥ axis 不重叠', overlap < 0.5, `${overlap.toFixed(2)} m²`);
}

// ── 7) 退化输入不崩
{
  const tiny: PolygonRegion = { outer: [[0, 0], [0.1, 0], [0, 0.1]] as Vec2[] };
  const parts = splitRegion(tiny, { type: 'orientation', directions: DIRS }, bandEvaluator);
  check('⑦ 极小地块不崩', Array.isArray(parts), `${parts.length} 块`);

  const empty: PolygonRegion = { outer: [] as Ring as Vec2[] };
  const p2 = splitRegion(empty, { type: 'orientation', directions: DIRS }, bandEvaluator);
  check('⑦ 空环返回空', p2.length === 0);
}

// ── 8) evaluator 被真的调用过（不是写死的结果）
{
  let calls = 0;
  const spy: PartitionEvaluator = (r, o, a) => {
    calls++;
    return bandEvaluator(r, o, a);
  };
  splitRegion(L, { type: 'orientation', directions: DIRS }, spy);
  check('⑧ evaluator 被调用', calls > 0, `${calls} 次`);
}

console.log(`\n${failures === 0 ? 'PASS' : 'FAIL'} — ${total} 项断言，${failures} 项失败`);
if (failures) process.exitCode = 1;
