// `primitives/polygon.ts` 的符号与夹取约定。
//
// 为什么单独钉这一条：`offsetRing(ring, dist)` 的约定是「**正数向内**」。
// 我在 Plaza 里写成 `offsetRing(piece.outer, -greenM)`（以为负数是内缩），
// 结果往外扩、被内部 grown 判据夹取、绿带恒为 0 —— 而所有数值门禁照样全绿
// （覆盖 100%、重叠 0），只有 fidelity 观测里"绿化 0.0%"才露馅。
// 所以这个符号必须被测试钉住，而不是靠注释。
//
// 跑法：node packages/site-gen/test/polygon.test.ts

import { clipHalfPlane, clipVSlab, minAreaBox, offsetRing, rectInFrame } from '../src/primitives/polygon.ts';
import { area as ringArea, signedArea, type Vec2 } from '../../plan-ir/src/geom.ts';

let failures = 0;
let total = 0;
const check = (name: string, cond: boolean, detail = ''): void => {
  total++;
  if (!cond) {
    failures++;
    console.log(`  FAIL ${name} ${detail}`);
  }
};

const rect = (x0: number, y0: number, x1: number, y1: number): Vec2[] => [
  [x0, y0],
  [x1, y0],
  [x1, y1],
  [x0, y1],
];

// ── 1) offsetRing 的符号约定：**正数向内**
{
  const r = rect(0, 0, 120, 100);
  const inset = offsetRing(r, 2.5);
  check('① 正数 = 向内（不夹取）', inset.clamped === false, `clamped=${inset.clamped}`);
  check('① 内缩后面积 = 115×95', Math.abs(ringArea(inset.ring) - 115 * 95) < 1, ringArea(inset.ring).toFixed(1));
  check('① 内缩后仍是逆时针', signedArea(inset.ring) > 0);

  // 负数会往外扩 → 被 grown 判据夹取（回退原环）。这不是 bug，是保护。
  const out = offsetRing(r, -2.5);
  check('① 负数 = 向外，被夹取并回退原环', out.clamped === true && Math.abs(ringArea(out.ring) - 12000) < 1, `${out.clamped} ${ringArea(out.ring).toFixed(0)}`);
}

// ── 2) 内缩过头要夹取，不许产出反向/自交的环
{
  const r = rect(0, 0, 20, 20);
  const over = offsetRing(r, 12); // 内缩 12 于 20×20 → 面积会翻，必须夹取
  check('② 内缩过头被夹取', over.clamped === true, `clamped=${over.clamped}`);
  check('② 夹取时返回原环', Math.abs(ringArea(over.ring) - 400) < 1, ringArea(over.ring).toFixed(0));
}

// ── 3) clipHalfPlane / clipVSlab 的互补性
{
  const r = rect(0, 0, 100, 60);
  const pos = clipHalfPlane(r, [50, 0], [1, 0]);
  const neg = clipHalfPlane(r, [50, 0], [-1, 0]);
  check('③ 半平面切成两半', Math.abs(ringArea(pos) + Math.abs(ringArea(neg)) - 6000) < 1, `${ringArea(pos).toFixed(0)}+${ringArea(neg).toFixed(0)}`);

  const frame = { origin: [0, 0] as Vec2, u: [1, 0] as Vec2, v: [0, 1] as Vec2 };
  const a = clipVSlab(r, frame, null, 20);
  const b = clipVSlab(r, frame, 20, null);
  check('③ v 切片互补', Math.abs((a ? ringArea(a) : 0) + (b ? ringArea(b) : 0) - 6000) < 1);
}

// ── 4) minAreaBox：斜放的长条，长边要跟着斜
{
  const rot = rect(0, 0, 100, 10).map(([x, y]) => [x * 0.866 - y * 0.5, x * 0.5 + y * 0.866] as Vec2);
  const box = minAreaBox(rot);
  check('④ 最小外接矩形长边 ≈ 100', Math.abs(box.w - 100) < 2, box.w.toFixed(1));
  check('④ 最小外接矩形短边 ≈ 10', Math.abs(box.h - 10) < 2, box.h.toFixed(1));
}

// ── 5) rectInFrame 与 clipVSlab 一致
{
  const frame = { origin: [0, 0] as Vec2, u: [1, 0] as Vec2, v: [0, 1] as Vec2 };
  const q = rectInFrame(frame, 10, 30, 5, 25);
  check('⑤ rectInFrame 面积 = 20×20', !!q && Math.abs(ringArea(q) - 400) < 1e-6, q ? ringArea(q).toFixed(1) : 'null');
  check('⑤ rectInFrame 退化返回 null', rectInFrame(frame, 10, 10, 0, 5) === null);
}

console.log(`\n${failures === 0 ? 'PASS' : 'FAIL'} — ${total} 项断言，${failures} 项失败`);
if (failures) process.exitCode = 1;
